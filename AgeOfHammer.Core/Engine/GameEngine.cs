using System.Numerics;
using AgeOfHammer.Core.Combat;
using AgeOfHammer.Core.Data;
using AgeOfHammer.Core.Dice;
using AgeOfHammer.Core.Geometry;
using AgeOfHammer.Core.State;

namespace AgeOfHammer.Core.Engine;

/// <summary>
/// The AoS4 rules engine. All state changes go through <see cref="Submit"/>; <see cref="Decider"/> says
/// which player must act next, and <see cref="Pending"/> describes any decision the engine is waiting on.
/// </summary>
public sealed partial class GameEngine
{
    public const int CommandPointsPerRound = 4;

    public GameState State { get; }
    public PendingDecision? Pending { get; private set; }

    private int _deployingPlayer;
    private int _firstFinishedDeploying = -1;
    private int _combatPicker;
    private bool _reactionOffered;

    private GameEngine(GameState state) => State = state;

    public static GameEngine Create(Battleplan plan, FactionData faction0, IEnumerable<string> army0,
        FactionData faction1, IEnumerable<string> army1, IDiceRoller dice)
    {
        var state = new GameState
        {
            Battleplan = plan,
            Dice = dice,
            Players =
            [
                new Player { Index = 0, Faction = faction0, Name = faction0.Name },
                new Player { Index = 1, Faction = faction1, Name = faction1.Name },
            ],
        };
        int id = 1;
        foreach (var (faction, army, owner) in new[] { (faction0, army0, 0), (faction1, army1, 1) })
            foreach (var wid in army)
            {
                var ws = faction.Get(wid);
                var unit = new Unit { Id = id++, Owner = owner, Warscroll = ws };
                for (int i = 0; i < ws.UnitSize; i++) unit.Models.Add(new Model { Index = i });
                state.Units.Add(unit);
            }
        for (int i = 0; i < plan.Objectives.Count; i++)
            state.Objectives.Add(new Objective { Id = i + 1, Position = plan.Objectives[i].ToVector() });

        var engine = new GameEngine(state);
        state.Phase = Phase.Deployment;
        engine._deployingPlayer = 0;
        state.Write($"Battleplan: {plan.Name}. Deployment begins - players alternate placing units.");
        if (!state.UnitsOf(0).Any()) engine._deployingPlayer = 1;
        return engine;
    }

    /// <summary>The player who must act next, or -1 when the game is over.</summary>
    public int Decider
    {
        get
        {
            if (State.Phase == Phase.GameOver) return -1;
            if (Pending != null) return Pending.Decider;
            if (State.Phase == Phase.Deployment) return _deployingPlayer;
            if (State.Phase == Phase.Combat) return CombatDecider();
            return State.ActivePlayer;
        }
    }

    public ActionResult Submit(GameAction action)
    {
        if (State.Phase == Phase.GameOver) return ActionResult.Fail("The game is over.");
        if (action.Player != Decider) return ActionResult.Fail($"It is player {Decider + 1}'s decision.");

        if (Pending != null)
        {
            switch (Pending, action)
            {
                case (PendingMove pm, CompleteMove cm): return DoCompleteMove(pm, cm);
                case (PendingMove pm, CancelMove): return DoCancelMove(pm);
                case (PendingMove pm, RerollCharge): return DoRerollCharge(pm);
                case (PendingDefence pd, RespondDefence rd):
                    Pending = null;
                    pd.Resume(rd.UseAllOutDefence);
                    return ActionResult.Success;
                case (PendingReaction pr, UseReaction ur): return DoUseReaction(pr, ur);
                case (PendingReaction, DeclineReaction):
                    Pending = null;
                    AdvancePhase();
                    return ActionResult.Success;
                default:
                    return ActionResult.Fail($"Waiting for a different decision ({Pending.GetType().Name}).");
            }
        }

        return action switch
        {
            DeployUnit d => DoDeploy(d),
            DeclareMove m => DoDeclareMove(m),
            Shoot s => DoShoot(s),
            DeclareCharge c => DoDeclareCharge(c),
            Fight f => DoFight(f),
            Rally r => DoRally(r),
            CastSpell cs => DoCastSpell(cs),
            PowerThrough pt => DoPowerThrough(pt),
            EndPhase e => DoEndPhase(e),
            _ => ActionResult.Fail($"{action.GetType().Name} is not valid right now."),
        };
    }

    // ------------------------------------------------------------------ Deployment

    public string? ValidateDeployment(Unit unit, IReadOnlyList<Vector2> positions)
    {
        if (positions.Count != unit.Models.Count) return $"Expected {unit.Models.Count} positions.";
        var zone = State.TerritoryOf(unit.Owner);
        float r = unit.BaseRadius;
        foreach (var p in positions)
        {
            if (!zone.ContainsCircle(p, r)) return "Models must be set up wholly within your territory.";
            if (OverlapsTerrain(p, r)) return "Models cannot be set up on impassable terrain.";
        }
        if (Overlaps(unit, positions)) return "Models cannot overlap other models.";
        if (!Geo.IsCoherent(positions, r)) return "Unit must be set up in coherency.";
        return null;
    }

    private ActionResult DoDeploy(DeployUnit d)
    {
        if (State.Phase != Phase.Deployment) return ActionResult.Fail("Not the deployment phase.");
        var unit = State.GetUnit(d.UnitId);
        if (unit.Owner != d.Player) return ActionResult.Fail("Not your unit.");
        if (unit.Deployed) return ActionResult.Fail("Unit already deployed.");
        var error = ValidateDeployment(unit, d.Positions);
        if (error != null) return ActionResult.Fail(error);

        for (int i = 0; i < unit.Models.Count; i++) unit.Models[i].Position = d.Positions[i];
        unit.Deployed = true;
        State.Write($"{State.Players[d.Player].Name} deploys {unit.Name}.");

        bool mineLeft = State.UnitsOf(d.Player).Any(u => !u.Deployed);
        bool theirsLeft = State.UnitsOf(1 - d.Player).Any(u => !u.Deployed);
        if (!mineLeft && _firstFinishedDeploying < 0) _firstFinishedDeploying = d.Player;
        if (theirsLeft) _deployingPlayer = 1 - d.Player;
        else if (!mineLeft)
        {
            if (_firstFinishedDeploying < 0) _firstFinishedDeploying = 1 - d.Player;
            StartBattleRound(1);
        }
        return ActionResult.Success;
    }

    // ------------------------------------------------------------------ Rounds, turns, phases

    private void StartBattleRound(int round)
    {
        if (round > State.Battleplan.BattleRounds)
        {
            EndGame("The final battle round is over.");
            return;
        }
        State.BattleRound = round;
        foreach (var u in State.Units) u.Buffs.RemoveAll(b => b.Expiry == Expiry.EndOfRound);

        int first;
        if (round == 1)
        {
            first = Math.Max(0, _firstFinishedDeploying);
            State.Write($"=== Battle round 1. {State.Players[first].Name} finished deploying first and takes the first turn. ===");
        }
        else
        {
            int r0 = State.Dice.D6(), r1 = State.Dice.D6();
            first = r0 == r1 ? State.FirstPlayer : (r0 > r1 ? 0 : 1);
            State.Write($"=== Battle round {round}. Priority roll {r0} vs {r1}: {State.Players[first].Name} goes first. ===");
        }
        State.FirstPlayer = first;
        State.SecondTurnOfRound = false;

        var p0 = State.Players[0];
        var p1 = State.Players[1];
        p0.CommandPoints = CommandPointsPerRound;
        p1.CommandPoints = CommandPointsPerRound;
        if (p0.VictoryPoints < p1.VictoryPoints) p0.CommandPoints++;
        else if (p1.VictoryPoints < p0.VictoryPoints) p1.CommandPoints++;

        StartTurn(first);
    }

    private void StartTurn(int player)
    {
        State.ActivePlayer = player;
        foreach (var u in State.Units)
        {
            u.Moved = u.Ran = u.Retreated = u.Charged = u.ChargeAttempted = false;
            u.Buffs.RemoveAll(b => b.Expiry == Expiry.EndOfTurn);
        }
        foreach (var p in State.Players) p.SpellsCastThisTurn.Clear();
        State.Write($"--- {State.Players[player].Name}'s turn (round {State.BattleRound}) ---");
        EnterPhase(Phase.Hero);
    }

    private void EnterPhase(Phase phase)
    {
        State.Phase = phase;
        _reactionOffered = false;
        foreach (var u in State.Units)
        {
            u.Shot = u.Fought = u.ReceivedCommandThisPhase = false;
            u.CastsThisPhase = 0;
            u.Buffs.RemoveAll(b => b.Expiry == Expiry.EndOfPhase);
        }
        foreach (var p in State.Players) p.CommandsUsedThisPhase.Clear();
        if (phase == Phase.Combat) _combatPicker = State.ActivePlayer;
        State.Write($"{phase} phase.");
    }

    private void AdvancePhase()
    {
        if (State.Phase == Phase.GameOver) return;
        switch (State.Phase)
        {
            case Phase.Hero: EnterPhase(Phase.Movement); break;
            case Phase.Movement: EnterPhase(Phase.Shooting); break;
            case Phase.Shooting: EnterPhase(Phase.Charge); break;
            case Phase.Charge: EnterPhase(Phase.Combat); break;
            case Phase.Combat: EnterPhase(Phase.EndOfTurn); break;
            case Phase.EndOfTurn:
                ScoreEndOfTurn();
                if (State.Phase == Phase.GameOver) return;
                if (!State.SecondTurnOfRound)
                {
                    State.SecondTurnOfRound = true;
                    StartTurn(State.Opponent(State.FirstPlayer));
                }
                else StartBattleRound(State.BattleRound + 1);
                break;
        }
    }

    private ActionResult DoEndPhase(EndPhase e)
    {
        switch (State.Phase)
        {
            case Phase.Deployment: return ActionResult.Fail("All units must be deployed.");
            case Phase.Combat:
                PassCombat(e.Player);
                return ActionResult.Success;
            case Phase.Movement when TryOfferReaction(ReactionKind.Redeploy):
            case Phase.Shooting when TryOfferReaction(ReactionKind.CoveringFire):
            case Phase.Charge when TryOfferReaction(ReactionKind.CounterCharge):
                return ActionResult.Success;
        }
        AdvancePhase();
        return ActionResult.Success;
    }

    // ------------------------------------------------------------------ Scoring and game end

    public void UpdateObjectiveControl()
    {
        foreach (var o in State.Objectives)
        {
            int s0 = State.ControlScore(o, 0), s1 = State.ControlScore(o, 1);
            if (s0 > s1) o.ControlledBy = 0;
            else if (s1 > s0) o.ControlledBy = 1;
        }
    }

    private void ScoreEndOfTurn()
    {
        UpdateObjectiveControl();
        int me = State.ActivePlayer;
        int mine = State.Objectives.Count(o => o.ControlledBy == me);
        int theirs = State.Objectives.Count(o => o.ControlledBy == 1 - me);
        var plan = State.Battleplan;
        int vp = 0;
        if (mine >= 1) vp += plan.VpForOne;
        if (mine >= 2) vp += plan.VpForTwoPlus;
        if (mine > theirs) vp += plan.VpForMoreThanOpponent;
        State.Players[me].VictoryPoints += vp;
        State.Write($"{State.Players[me].Name} controls {mine} objective(s) vs {theirs}: scores {vp} VP " +
                    $"(total {State.Players[me].VictoryPoints}).");
    }

    private void CheckWipeout()
    {
        if (State.Phase is Phase.Deployment or Phase.GameOver) return;
        bool dead0 = !State.LiveUnitsOf(0).Any(), dead1 = !State.LiveUnitsOf(1).Any();
        if (dead0 && dead1) { State.IsDraw = true; EndGame("Both armies have been destroyed."); }
        else if (dead0) { State.Winner = 1; EndGame($"{State.Players[0].Name} has been wiped out."); }
        else if (dead1) { State.Winner = 0; EndGame($"{State.Players[1].Name} has been wiped out."); }
    }

    private void EndGame(string reason)
    {
        Pending = null;
        State.Phase = Phase.GameOver;
        if (State.Winner is null && !State.IsDraw)
        {
            int v0 = State.Players[0].VictoryPoints, v1 = State.Players[1].VictoryPoints;
            if (v0 == v1) State.IsDraw = true;
            else State.Winner = v0 > v1 ? 0 : 1;
        }
        string result = State.IsDraw ? "The battle is a DRAW." : $"{State.Players[State.Winner!.Value].Name} WINS!";
        State.Write($"=== GAME OVER: {reason} {result} " +
                    $"(VP {State.Players[0].VictoryPoints} - {State.Players[1].VictoryPoints}) ===");
    }

    // ------------------------------------------------------------------ Commands

    public bool CanUseCommand(int player, string command, Unit? unit)
    {
        var p = State.Players[player];
        return p.CommandPoints >= Commands.Cost(command)
               && !p.CommandsUsedThisPhase.Contains(command)
               && (unit == null || !unit.ReceivedCommandThisPhase);
    }

    private void PayCommand(int player, string command, Unit? unit)
    {
        var p = State.Players[player];
        p.CommandPoints -= Commands.Cost(command);
        p.CommandsUsedThisPhase.Add(command);
        if (unit != null) unit.ReceivedCommandThisPhase = true;
        State.Write($"{p.Name} uses {command}" + (unit != null ? $" on {unit.Name}" : "") + $" ({p.CommandPoints} CP left).");
    }

    // ------------------------------------------------------------------ Shared helpers

    private bool OverlapsTerrain(Vector2 p, float r) =>
        State.Battleplan.Terrain.Any(t => t.Impassable && Geo.CircleIntersectsRect(p, r, t.Area.ToRect()));

    /// <summary>True if any of the positions overlap each other or any other deployed model.</summary>
    private bool Overlaps(Unit unit, IReadOnlyList<Vector2> positions)
    {
        float r = unit.BaseRadius;
        for (int i = 0; i < positions.Count; i++)
        {
            for (int j = i + 1; j < positions.Count; j++)
                if (Geo.Overlaps(positions[i], r, positions[j], r)) return true;
            foreach (var other in State.Units)
            {
                if (other == unit || !other.OnBattlefield) continue;
                foreach (var m in other.Alive)
                    if (Geo.Overlaps(positions[i], r, m.Position, other.BaseRadius)) return true;
            }
        }
        return false;
    }

    private static List<WeaponUse> Uses(IEnumerable<(WeaponProfile W, int Models)> list) =>
        list.Where(x => x.Models > 0).Select(x => new WeaponUse(x.W, x.Models)).ToList();

    /// <summary>
    /// Starts an attack activation. If the defender can use All-out Defence they are asked first;
    /// <paramref name="then"/> runs once the attack has been resolved.
    /// </summary>
    private void BeginAttack(Unit attacker, Unit target, List<WeaponUse> weapons, AttackOptions options, Action? then)
    {
        void Resolve(bool allOutDefence)
        {
            var opts = options;
            if (allOutDefence)
            {
                PayCommand(target.Owner, Commands.AllOutDefence, target);
                opts = opts with { ExtraSave = opts.ExtraSave + 1 };
            }
            var result = AttackResolver.Resolve(State, attacker, target, weapons, opts);
            State.Write(result.Summary());
            CheckWipeout();
            if (State.Phase != Phase.GameOver) then?.Invoke();
        }

        if (weapons.Count == 0)
        {
            State.Write($"{attacker.Name} has no models able to attack {target.Name}.");
            then?.Invoke();
            return;
        }
        if (CanUseCommand(target.Owner, Commands.AllOutDefence, target))
            Pending = new PendingDefence { Decider = target.Owner, Attacker = attacker, Target = target, Resume = Resolve };
        else
            Resolve(false);
    }
}
