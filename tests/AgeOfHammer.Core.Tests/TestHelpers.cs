using System.Numerics;
using AgeOfHammer.Core.Data;
using AgeOfHammer.Core.Dice;
using AgeOfHammer.Core.Engine;
using AgeOfHammer.Core.State;

namespace AgeOfHammer.Core.Tests;

/// <summary>Returns queued results in order; throws if the queue runs dry so tests catch unexpected rolls.</summary>
public sealed class ScriptedDice : IDiceRoller
{
    private readonly Queue<int> _rolls;
    public ScriptedDice(params int[] rolls) => _rolls = new Queue<int>(rolls);
    public void Add(params int[] rolls) { foreach (var r in rolls) _rolls.Enqueue(r); }
    public int Remaining => _rolls.Count;
    public int Roll(int sides)
    {
        if (_rolls.Count == 0) throw new InvalidOperationException("ScriptedDice ran out of rolls");
        int r = _rolls.Dequeue();
        if (r < 1 || r > sides) throw new InvalidOperationException($"Scripted roll {r} invalid for D{sides}");
        return r;
    }
}

public static class TestData
{
    public static WeaponProfile Melee(string attacks = "1", int hit = 3, int wound = 3, int rend = 0, string damage = "1", params string[] abilities) =>
        new() { Name = "Blade", Range = 0, Attacks = attacks, Hit = hit, Wound = wound, Rend = rend, Damage = damage, Abilities = abilities.ToList() };

    public static WeaponProfile Ranged(int range = 18, string attacks = "1", int hit = 3, int wound = 3, int rend = 0, string damage = "1") =>
        new() { Name = "Bow", Range = range, Attacks = attacks, Hit = hit, Wound = wound, Rend = rend, Damage = damage };

    public static Warscroll Scroll(string id, int unitSize = 1, int health = 1, int save = 4, int move = 5, int control = 1,
        int baseMm = 32, int? ward = null, params WeaponProfile[] weapons) =>
        new()
        {
            Id = id, Name = id, UnitSize = unitSize, Health = health, Save = save, Move = move, Control = control,
            BaseSizeMm = baseMm, Ward = ward, Weapons = weapons.Length > 0 ? weapons.ToList() : [Melee()],
            Keywords = ["Infantry"],
        };

    public static FactionData Faction(string id, params Warscroll[] scrolls)
    {
        var f = new FactionData { Id = id, Name = id, Warscrolls = scrolls.ToList() };
        foreach (var w in f.Warscrolls) w.FactionId = id;
        return f;
    }

    public static Battleplan OpenField() => new()
    {
        Id = "test", Name = "Test Field", Width = 60, Height = 44, BattleRounds = 5,
        Territories = [new RectDef { X = 0, Y = 32, W = 60, H = 12 }, new RectDef { X = 0, Y = 0, W = 60, H = 12 }],
        Objectives = [new PointDef { X = 30, Y = 22 }],
    };

    /// <summary>A bare GameState with two players for testing the attack resolver directly.</summary>
    public static GameState NewState(IDiceRoller dice)
    {
        var f = Faction("test");
        return new GameState
        {
            Battleplan = OpenField(),
            Dice = dice,
            Players = [new Player { Index = 0, Faction = f }, new Player { Index = 1, Faction = f }],
        };
    }

    public static Unit Unit(Warscroll w, int owner, Vector2 at, int id = 0)
    {
        var u = new Unit { Id = id, Owner = owner, Warscroll = w, Deployed = true };
        var positions = Geometry.Geo.Formation(w.UnitSize, w.BaseRadius, at);
        // (round bases in unit tests; ovals are covered in OvalBaseTests)
        for (int i = 0; i < w.UnitSize; i++) u.Models.Add(new Model { Index = i, Position = positions[i] });
        return u;
    }

    /// <summary>Creates an engine and deploys every unit at the given centres (index-matched to armies).</summary>
    public static GameEngine DeployedEngine(IDiceRoller dice, Warscroll[] army0, Vector2[] at0, Warscroll[] army1, Vector2[] at1, Battleplan? plan = null)
    {
        var f0 = Faction("a", army0.DistinctBy(w => w.Id).ToArray());
        var f1 = Faction("b", army1.DistinctBy(w => w.Id).ToArray());
        plan ??= new Battleplan
        {
            Id = "t", Name = "t", Width = 60, Height = 44,
            // Territories cover the whole board so tests can deploy anywhere.
            Territories = [new RectDef { W = 60, H = 44 }, new RectDef { W = 60, H = 44 }],
        };
        var e = GameEngine.Create(plan, f0, army0.Select(w => w.Id), f1, army1.Select(w => w.Id), dice);
        var u0 = e.State.UnitsOf(0).ToList();
        var u1 = e.State.UnitsOf(1).ToList();
        int i0 = 0, i1 = 0;
        while (e.State.Phase == Phase.Deployment)
        {
            int p = e.Decider;
            var unit = p == 0 ? u0[i0] : u1[i1];
            var at = p == 0 ? at0[i0++] : at1[i1++];
            var pos = Geometry.Geo.Formation(unit.Models.Count, unit.BaseExtent, at);
            var r = e.Submit(new DeployUnit(p, unit.Id, pos));
            if (!r.Ok) throw new InvalidOperationException(r.Error);
        }
        return e;
    }

    /// <summary>Plays "do nothing" decisions (end phase, decline, skip) until <paramref name="done"/> is true.</summary>
    public static void AdvanceUntil(GameEngine e, Func<bool> done, int guard = 400)
    {
        while (!done())
        {
            if (--guard < 0) throw new InvalidOperationException("AdvanceUntil: condition never reached");
            if (e.State.Phase == Phase.GameOver) throw new InvalidOperationException("Game ended first");
            var r = e.Pending switch
            {
                PendingReaction => e.Submit(new DeclineReaction(e.Decider)),
                PendingDefence => e.Submit(new RespondDefence(e.Decider, false)),
                PendingMove => e.Submit(new CancelMove(e.Decider)),
                PendingAbilityWindow => e.Submit(new DoneAbilities(e.Decider)),
                PendingMarker => e.Submit(new PlaceMarker(e.Decider, null)),
                _ => e.Submit(new EndPhase(e.Decider)),
            };
            if (!r.Ok) throw new InvalidOperationException(r.Error);
        }
    }

    /// <summary>Ends phases until the engine reaches the requested phase for the active player.</summary>
    public static void AdvanceTo(GameEngine e, Phase phase)
    {
        int guard = 0;
        while (e.State.Phase != phase)
        {
            if (++guard > 50) throw new InvalidOperationException($"Could not reach {phase}");
            var r = e.Pending switch
            {
                PendingReaction => e.Submit(new DeclineReaction(e.Decider)),
                PendingDefence => e.Submit(new RespondDefence(e.Decider, false)),
                PendingMove => e.Submit(new CancelMove(e.Decider)),
                _ => e.Submit(new EndPhase(e.Decider)),
            };
            if (!r.Ok) throw new InvalidOperationException(r.Error);
        }
    }
}
