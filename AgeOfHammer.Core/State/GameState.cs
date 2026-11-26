using System.Numerics;
using AgeOfHammer.Core.Data;
using AgeOfHammer.Core.Dice;
using AgeOfHammer.Core.Geometry;

namespace AgeOfHammer.Core.State;

public enum Phase { Deployment, Hero, Movement, Shooting, Charge, Combat, EndOfTurn, GameOver }

public enum Stat
{
    Hit, Wound, Rend, Attacks, Damage, Save, Ward, Move, Control, Charge, Cast, StrikeFirst, StrikeLast,
    /// <summary>Modifier to hit rolls of attacks that target this unit.</summary>
    HitAgainst,
    /// <summary>Modifier to wound rolls of attacks that target this unit.</summary>
    WoundAgainst,
    /// <summary>Caps the unit's control score.</summary>
    MaxControl,
    /// <summary>Extra dice for charge rolls (to a maximum of 3).</summary>
    ChargeDice,
    /// <summary>Flag: retreating does not inflict mortal damage on this unit.</summary>
    NoRetreatDamage,
    /// <summary>Coherency range in inches, if more than 1/2".</summary>
    CoherencyRange,
    /// <summary>Flag: the unit cannot use or receive commands.</summary>
    NoCommands,
    /// <summary>Modifier to this unit's ward rolls.</summary>
    WardRoll,
    /// <summary>Flag: can shoot even after running or retreating.</summary>
    ShootAfterRun,
    /// <summary>Flag: can charge even after running or retreating.</summary>
    ChargeAfterRun,
    /// <summary>Attacks that target this unit fail on an unmodified hit roll of this value or less.</summary>
    UnmodifiedHitFailsAgainst,
}

public enum Expiry { EndOfPhase, EndOfTurn, EndOfRound, UntilYourNextTurn, Permanent }

/// <summary>A temporary modifier on a unit (from a command, spell or ability).</summary>
public sealed record Buff(string Source, Stat Stat, int Value, Expiry Expiry, AttackType AppliesTo = AttackType.Any)
{
    /// <summary>Only applies to weapons whose name contains this text.</summary>
    public string? Weapon { get; init; }
    /// <summary>The player whose ability created it (for <see cref="Expiry.UntilYourNextTurn"/>).</summary>
    public int SourcePlayer { get; init; } = -1;
}

public sealed class Model
{
    public int Index { get; init; }
    public Vector2 Position { get; set; }
    /// <summary>Damage points allocated to this model.</summary>
    public int Damage { get; set; }
    public bool Slain { get; set; }
}

public sealed class Unit
{
    public int Id { get; init; }
    public int Owner { get; init; }
    public required Warscroll Warscroll { get; init; }
    public List<Model> Models { get; } = new();

    public bool Deployed { get; set; }

    // Per-turn status
    public bool Moved { get; set; }
    public bool Ran { get; set; }
    public bool Retreated { get; set; }
    public bool Charged { get; set; }
    public bool ChargeAttempted { get; set; }
    // Per-phase status
    public bool Shot { get; set; }
    public bool Fought { get; set; }
    public bool ReceivedCommandThisPhase { get; set; }
    public int CastsThisPhase { get; set; }

    public List<Buff> Buffs { get; } = new();

    public string Name => Warscroll.Name;
    public float BaseRadius => Warscroll.BaseRadius;
    public IEnumerable<Model> Alive => Models.Where(m => !m.Slain);
    public int AliveCount => Models.Count(m => !m.Slain);
    public bool IsDestroyed => AliveCount == 0;
    public bool OnBattlefield => Deployed && !IsDestroyed;
    public int WoundsRemaining => Alive.Sum(m => Warscroll.Health - m.Damage);
    public List<Vector2> AlivePositions => Alive.Select(m => m.Position).ToList();
    public Vector2 Centroid => Geo.Centroid(AlivePositions);

    public int BuffTotal(Stat stat, AttackType type = AttackType.Any, string? weapon = null) =>
        Buffs.Where(b => b.Stat == stat && (b.AppliesTo == AttackType.Any || type == AttackType.Any || b.AppliesTo == type)
                         && (b.Weapon == null || (weapon != null && weapon.Contains(b.Weapon, StringComparison.OrdinalIgnoreCase))))
             .Sum(b => b.Value);

    /// <summary>Damage points the unit has taken (including slain models).</summary>
    public int DamagePoints => Warscroll.Health * Models.Count - WoundsRemaining;

    public bool HasBuff(Stat stat) => Buffs.Any(b => b.Stat == stat);

    public override string ToString() => $"{Name} #{Id} (P{Owner + 1})";
}

public sealed class Player
{
    public int Index { get; init; }
    public required FactionData Faction { get; init; }
    public string Name { get; set; } = "";
    public int CommandPoints { get; set; }
    public int VictoryPoints { get; set; }
    public HashSet<string> CommandsUsedThisPhase { get; } = new();
    public HashSet<string> SpellsCastThisTurn { get; } = new();
}

public sealed class Objective
{
    public const float MarkerRadius = 20f / 25.4f; // 40mm marker
    public const float ControlRange = 3f;
    public int Id { get; init; }
    public Vector2 Position { get; init; }
    /// <summary>Index of controlling player or -1.</summary>
    public int ControlledBy { get; set; } = -1;
}

public sealed class GameState
{
    public required Battleplan Battleplan { get; init; }
    public required IDiceRoller Dice { get; init; }
    public Player[] Players { get; init; } = Array.Empty<Player>();
    public List<Unit> Units { get; } = new();
    public List<Objective> Objectives { get; } = new();

    public int BattleRound { get; set; }
    public Phase Phase { get; set; } = Phase.Deployment;
    public int ActivePlayer { get; set; }
    /// <summary>Who took the first turn this battle round.</summary>
    public int FirstPlayer { get; set; }
    /// <summary>True during the second player's turn of the battle round.</summary>
    public bool SecondTurnOfRound { get; set; }
    public int? Winner { get; set; }
    public bool IsDraw { get; set; }

    /// <summary>Keys of abilities already used, for once-per-phase/turn/round/battle limits.</summary>
    public HashSet<string> UsedAbilities { get; } = new();

    public List<string> Log { get; } = new();
    public event Action<string>? Logged;

    public Player Active => Players[ActivePlayer];
    public int Opponent(int player) => 1 - player;
    public RectF Bounds => Battleplan.Bounds;

    public Unit GetUnit(int id) => Units.First(u => u.Id == id);
    public IEnumerable<Unit> UnitsOf(int player) => Units.Where(u => u.Owner == player);
    public IEnumerable<Unit> LiveUnitsOf(int player) => Units.Where(u => u.Owner == player && u.OnBattlefield);
    public IEnumerable<Unit> EnemiesOf(Unit unit) => Units.Where(u => u.Owner != unit.Owner && u.OnBattlefield);
    public RectF TerritoryOf(int player) => Battleplan.Territories[player].ToRect();

    public void Write(string message)
    {
        Log.Add(message);
        Logged?.Invoke(message);
    }

    // ---- Measurement helpers that need the whole state ----

    /// <summary>Closest base-to-base distance between two units.</summary>
    public static float Distance(Unit a, Unit b)
    {
        float best = float.MaxValue;
        foreach (var ma in a.Alive)
            foreach (var mb in b.Alive)
                best = MathF.Min(best, Geo.Distance(ma.Position, a.BaseRadius, mb.Position, b.BaseRadius));
        return best;
    }

    public static float Distance(Model m, Unit owner, Unit target)
    {
        float best = float.MaxValue;
        foreach (var mb in target.Alive)
            best = MathF.Min(best, Geo.Distance(m.Position, owner.BaseRadius, mb.Position, target.BaseRadius));
        return best;
    }

    public bool InCombat(Unit unit) => unit.OnBattlefield && EnemiesOf(unit).Any(e => Distance(unit, e) <= Geo.CombatRange + Geo.Epsilon);

    public IEnumerable<Unit> EnemiesInCombatWith(Unit unit) =>
        EnemiesOf(unit).Where(e => Distance(unit, e) <= Geo.CombatRange + Geo.Epsilon);

    /// <summary>Visibility: a straight line between model centres not crossing obscuring terrain.</summary>
    public bool CanSee(Vector2 from, Vector2 to)
    {
        foreach (var t in Battleplan.Terrain)
        {
            if (!t.Obscuring) continue;
            var r = t.Area.ToRect();
            if (r.Contains(from) || r.Contains(to)) continue; // models in/at terrain can see out
            if (Geo.SegmentIntersectsRect(from, to, r)) return false;
        }
        return true;
    }

    public bool CanSee(Model m, Unit target) => target.Alive.Any(t => CanSee(m.Position, t.Position));

    public bool CanSee(Unit a, Unit b) => a.Alive.Any(m => CanSee(m, b));

    /// <summary>Control score a player has on an objective.</summary>
    public int ControlScore(Objective o, int player)
    {
        int total = 0;
        foreach (var u in LiveUnitsOf(player))
        {
            int control = Math.Max(0, u.Warscroll.Control + Combat.Modifiers.Get(this, u, Stat.Control, AttackType.Any));
            if (Combat.Modifiers.Lowest(this, u, Stat.MaxControl) is int cap) control = Math.Min(control, cap);
            foreach (var m in u.Alive)
                if (Geo.Distance(m.Position, u.BaseRadius, o.Position, Objective.MarkerRadius) <= Objective.ControlRange + Geo.Epsilon)
                    total += control;
        }
        return total;
    }
}
