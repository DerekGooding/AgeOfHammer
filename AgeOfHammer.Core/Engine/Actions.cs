using System.Numerics;
using AgeOfHammer.Core.State;

namespace AgeOfHammer.Core.Engine;

public enum MoveKind { Normal, Run, Retreat, Charge, Redeploy }

public enum ReactionKind { Redeploy, CoveringFire, CounterCharge }

/// <summary>Names and costs of the AoS4 core commands that are implemented.</summary>
public static class Commands
{
    public const string AllOutAttack = "All-out Attack";
    public const string AllOutDefence = "All-out Defence";
    public const string AtTheDouble = "At the Double";
    public const string ForwardToVictory = "Forward to Victory";
    public const string Rally = "Rally";
    public const string Redeploy = "Redeploy";
    public const string CoveringFire = "Covering Fire";
    public const string CounterCharge = "Counter-charge";
    public const string PowerThrough = "Power Through";

    public static int Cost(string name) => name is CounterCharge or PowerThrough ? 2 : 1;

    public static string ForReaction(ReactionKind k) => k switch
    {
        ReactionKind.Redeploy => Redeploy,
        ReactionKind.CoveringFire => CoveringFire,
        _ => CounterCharge,
    };
}

/// <summary>Everything a player (human UI or AI) can do is expressed as one of these.</summary>
public abstract record GameAction(int Player);

public sealed record DeployUnit(int Player, int UnitId, IReadOnlyList<Vector2> Positions) : GameAction(Player);
public sealed record DeclareMove(int Player, int UnitId, MoveKind Kind, bool AtTheDouble = false) : GameAction(Player);
public sealed record CompleteMove(int Player, IReadOnlyList<Vector2> Positions) : GameAction(Player);
public sealed record CancelMove(int Player) : GameAction(Player);
public sealed record Shoot(int Player, int UnitId, int TargetId, bool AllOutAttack = false) : GameAction(Player);
public sealed record DeclareCharge(int Player, int UnitId) : GameAction(Player);
public sealed record RerollCharge(int Player) : GameAction(Player);
public sealed record Fight(int Player, int UnitId, int TargetId, bool AllOutAttack = false) : GameAction(Player);
public sealed record Rally(int Player, int UnitId) : GameAction(Player);
public sealed record CastSpell(int Player, int UnitId, int SpellIndex, int TargetId) : GameAction(Player);
public sealed record PowerThrough(int Player, int UnitId, int TargetId) : GameAction(Player);
public sealed record RespondDefence(int Player, bool UseAllOutDefence) : GameAction(Player);
public sealed record UseReaction(int Player, int UnitId) : GameAction(Player);
public sealed record DeclineReaction(int Player) : GameAction(Player);
/// <summary>Uses a scripted ability. Targets may be empty for self abilities; extra targets are filled in by the engine.</summary>
public sealed record UseAbility(int Player, int UnitId, string Ability, IReadOnlyList<int>? Targets = null) : GameAction(Player);
/// <summary>Closes an ability window (the non-active player has finished using abilities this phase).</summary>
public sealed record DoneAbilities(int Player) : GameAction(Player);
/// <summary>Ends the current phase. In the combat phase this passes (your remaining units do not fight).</summary>
public sealed record EndPhase(int Player) : GameAction(Player);

public sealed record ActionResult(bool Ok, string? Error = null)
{
    public static readonly ActionResult Success = new(true);
    public static ActionResult Fail(string error) => new(false, error);
}

/// <summary>A decision the engine is waiting on before play continues.</summary>
public abstract class PendingDecision
{
    public int Decider { get; init; }
}

/// <summary>A unit has declared a move (or charge) and the player must now place its models.</summary>
public sealed class PendingMove : PendingDecision
{
    public required Unit Unit { get; init; }
    public MoveKind Kind { get; init; }
    public float Allowance { get; set; }
    /// <summary>Forward to Victory can still be used to re-roll this charge.</summary>
    public bool CanReroll { get; set; }
    public Action? Then { get; init; }
}

/// <summary>The defender may spend a CP on All-out Defence before an attack is resolved.</summary>
public sealed class PendingDefence : PendingDecision
{
    public required Unit Attacker { get; init; }
    public required Unit Target { get; init; }
    public required Action<bool> Resume { get; init; }
}

/// <summary>At the end of a phase the non-active player may use their abilities for that phase.</summary>
public sealed class PendingAbilityWindow : PendingDecision
{
    public Phase Phase { get; init; }
}

/// <summary>The non-active player may use a reaction command at the end of a phase.</summary>
public sealed class PendingReaction : PendingDecision
{
    public ReactionKind Kind { get; init; }
    public required List<Unit> Eligible { get; init; }
}
