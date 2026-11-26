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
    /// <summary>Each unmodified save roll of 6 against this unit inflicts this much mortal damage on the attacker.</summary>
    SaveSixMortal,
    /// <summary>Each model slain by a combat attack: on a dice roll of this value or more, 1 mortal damage to the attacker.</summary>
    SlainMortalOn,
    /// <summary>Each hit with the weapon also inflicts Amount mortal damage.</summary>
    HitMortal,
    /// <summary>Each critical hit with the weapon scores Amount hits instead of 1.</summary>
    CritHits,
    /// <summary>Flag: each hit with the weapon inflicts 1 mortal damage and the attack sequence ends.</summary>
    HitsAreMortal,
    /// <summary>Enemy abilities and spells that target this unit are ignored on a dice roll of this value or more.</summary>
    ResistAbilities,
    /// <summary>Enemy spells that target this unit are ignored on a dice roll of this value or more.</summary>
    ResistSpells,
    /// <summary>Modifier to chanting rolls.</summary>
    Chant,
    /// <summary>Flag: charge rolls can be re-rolled for free.</summary>
    ChargeReroll,
    /// <summary>The unit may fight once more this phase (consumed when it fights; it then has STRIKE-LAST).</summary>
    ExtraFight,
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
    /// <summary>The reserve pool the unit is waiting in (deployed but not on the battlefield), or null.</summary>
    public string? ReservePool { get; set; }
    /// <summary>The unit was set up on the battlefield this turn (e.g. arrived from reserve).</summary>
    public bool SetUpThisTurn { get; set; }

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
    public int ChantsThisPhase { get; set; }

    public List<Buff> Buffs { get; } = new();

    public string Name => Warscroll.Name;
    public float BaseRadius => Warscroll.BaseRadius;
    /// <summary>Direction (radians) of the long axis of oval bases; 0 = along the battlefield's width.</summary>
    public float Facing { get; set; }
    /// <summary>The model base this unit would have at <paramref name="center"/>.</summary>
    public Base BaseAt(Vector2 center) =>
        new(center, new Vector2(MathF.Cos(Facing), MathF.Sin(Facing)) * Warscroll.BaseHalfLength, BaseRadius);
    public Base BaseOf(Model m) => BaseAt(m.Position);
    /// <summary>Bounding-box size of one base at the unit's facing (for laying out formations).</summary>
    public Vector2 BaseExtent => BaseAt(Vector2.Zero).Extent;
    /// <summary>Distance from a model's centre to the farthest point of its base.</summary>
    public float BaseReach => Warscroll.BaseHalfLength + BaseRadius;
    public IEnumerable<Model> Alive => Models.Where(m => !m.Slain);
    public int AliveCount => Models.Count(m => !m.Slain);
    public bool IsDestroyed => AliveCount == 0;
    public bool OnBattlefield => Deployed && !IsDestroyed && ReservePool == null;
    public bool InReserve => Deployed && !IsDestroyed && ReservePool != null;
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

/// <summary>A placed battlefield marker (e.g. a Gnawhole).</summary>
public sealed class Marker
{
    public int Owner { get; init; }
    public required string Name { get; init; }
    public Vector2 Position { get; init; }
    public float Radius { get; init; } = 1.5f;
}

public sealed class GameState
{
    public List<Marker> Markers { get; } = new();

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
                best = MathF.Min(best, Geo.Distance(a.BaseOf(ma), b.BaseOf(mb)));
        return best;
    }

    public static float Distance(Model m, Unit owner, Unit target)
    {
        float best = float.MaxValue;
        foreach (var mb in target.Alive)
            best = MathF.Min(best, Geo.Distance(owner.BaseOf(m), target.BaseOf(mb)));
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
                if (Geo.Distance(u.BaseOf(m), Base.Circle(o.Position, Objective.MarkerRadius)) <= Objective.ControlRange + Geo.Epsilon)
                    total += control;
        }
        return total;
    }
}
