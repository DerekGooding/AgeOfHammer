using System.Numerics;
using AgeOfHammer.Core.Combat;
using AgeOfHammer.Core.Data;
using AgeOfHammer.Core.Engine;
using AgeOfHammer.Core.State;

namespace AgeOfHammer.Core.AI;

/// <summary>
/// A simple heuristic opponent. It only ever produces <see cref="GameAction"/>s, exactly like the UI,
/// and falls back to ending the phase / declining if an action is rejected.
/// </summary>
public sealed class AiPlayer
{
    public int Player { get; }
    private readonly HashSet<int> _handled = new();
    private (int Round, int Active, Phase Phase) _key;
    private Vector2? _moveGoal;
    private Unit? _chargeTarget;

    public AiPlayer(int player) => Player = player;

    /// <summary>Whether the AI puts units into reserve (deep strike, tunnels) during deployment.</summary>
    public bool UseReserves { get; set; } = true;

    /// <summary>Takes one action if it is this AI's decision. Returns true if it acted.</summary>
    public bool Act(GameEngine engine)
    {
        if (engine.Decider != Player) return false;
        var action = Decide(engine);
        var result = engine.Submit(action);
        if (result.Ok) return true;

        engine.State.Write($"(AI: {action.GetType().Name} rejected: {result.Error})");
        var fallback = Fallback(engine);
        var r2 = engine.Submit(fallback);
        if (!r2.Ok) throw new InvalidOperationException($"AI stuck: {result.Error} / {r2.Error}");
        return true;
    }

    private GameAction Fallback(GameEngine e) => e.Pending switch
    {
        PendingMove => new CancelMove(Player),
        PendingDefence => new RespondDefence(Player, false),
        PendingReaction => new DeclineReaction(Player),
        _ when e.State.Phase == Phase.Deployment => throw new InvalidOperationException("AI could not deploy a unit."),
        _ => new EndPhase(Player),
    };

    public GameAction Decide(GameEngine e)
    {
        var s = e.State;
        var key = (s.BattleRound, s.ActivePlayer, s.Phase);
        if (key != _key) { _key = key; _handled.Clear(); _abilitiesTried.Clear(); }

        switch (e.Pending)
        {
            case PendingDefence pd: return DecideDefence(e, pd);
            case PendingReaction pr: return DecideReaction(e, pr);
            case PendingMove pm: return DecideMove(e, pm);
            case PendingAbilityWindow: return TryAbility(e) ?? new DoneAbilities(Player);
            case PendingMarker pmk:
                return new PlaceMarker(Player, Placement.FindMarkerSpot(e, Player, pmk.Def, ForwardObjective(e)));
        }

        if (s.Phase != Phase.Deployment && TryAbility(e) is { } ability) return ability;

        return s.Phase switch
        {
            Phase.Deployment => Deploy(e),
            Phase.Hero => Hero(e),
            Phase.Movement => Movement(e),
            Phase.Shooting => Shooting(e),
            Phase.Charge => Charge(e),
            Phase.Combat => CombatPhase(e),
            Phase.EndOfTurn => EndOfTurn(e),
            _ => new EndPhase(Player),
        };
    }

    // ---------------------------------------------------------------- abilities

    private readonly HashSet<string> _abilitiesTried = new();

    /// <summary>Uses the first scripted ability that looks worthwhile right now, or returns null.</summary>
    private GameAction? TryAbility(GameEngine e)
    {
        foreach (var (unit, ability) in e.UsableAbilities(Player).ToList())
        {
            if (!_abilitiesTried.Add($"{unit.Id}|{ability.Name}")) continue;
            var target = ChooseAbilityTarget(e, unit, ability);
            if (target == null) continue;
            return new UseAbility(Player, unit.Id, ability.Name, target == unit && ability.Activate!.Target == "self" ? null : [target.Id]);
        }
        return null;
    }

    /// <summary>The best target for an ability, or null if the AI judges it not worth using now.</summary>
    private static Unit? ChooseAbilityTarget(GameEngine e, Unit unit, AbilityDef ability)
    {
        var s = e.State;
        var act = ability.Activate!;
        var effects = act.Effects;
        bool HurtsRecipient(EffectDef fx) => fx.Type is "mortal" or "mortalPerModel" or "mortalDice" or "slayRoll";

        // Self-harm (e.g. "Inflict D3 mortal damage on this unit, then +1 Attacks"): only if healthy and about to fight.
        if (effects.Any(fx => fx.To == "self" && HurtsRecipient(fx)) && (unit.WoundsRemaining < 6 || !s.InCombat(unit))) return null;

        var targets = e.AbilityTargets(unit, ability);
        if (targets.Count == 0) return null;

        if (act.Target == "enemy")
        {
            // Finish off what can be finished, otherwise hit the most valuable unit.
            return targets.OrderBy(t => t.WoundsRemaining <= 3 ? 0 : 1).ThenByDescending(t => t.Warscroll.Points).First();
        }

        // Friendly or self targets: pick the one that benefits.
        bool Contesting(Unit u) => Modifiers.ConditionHolds(s, "contesting", u, u, null);
        bool AboutToFight(Unit u) => s.InCombat(u) || u.Charged ||
                                     s.EnemiesOf(u).Any(en => GameState.Distance(u, en) <= e.MoveCharacteristic(u) + 7);
        IEnumerable<Unit> useful = targets.Where(t =>
        {
            foreach (var fx in effects.Where(fx => fx.To != "self"))
            {
                switch (fx.Type)
                {
                    case "heal": if (t.DamagePoints > 0) return true; break;
                    case "returnModels": if (t.Models.Any(m => m.Slain)) return true; break;
                    case "buff":
                        if (fx.Stat is "Control") { if (Contesting(t)) return true; }
                        else if (fx.Stat is "Charge" or "ChargeDice") { if (e.CanChargeBasic(t) || s.Phase != Phase.Charge) return true; }
                        else if (fx.Stat is "ShootAfterRun" or "ChargeAfterRun") return true;
                        else if (AboutToFight(t)) return true;
                        break;
                }
                if (HurtsRecipient(fx) && t.WoundsRemaining < 4) return false; // don't hurt fragile friends
            }
            return effects.All(fx => fx.To == "self");
        });
        return useful.OrderByDescending(t => t.Warscroll.Points).FirstOrDefault();
    }

    // ---------------------------------------------------------------- helpers

    private IEnumerable<Unit> Mine(GameEngine e) => e.State.LiveUnitsOf(Player).Where(u => !_handled.Contains(u.Id));

    public static double ExpectedDamage(GameEngine e, Unit attacker, Unit target, IEnumerable<WeaponUse> uses) =>
        uses.Sum(u => AttackResolver.ExpectedDamage(e.State, attacker, target, u.Weapon, u.Models));

    private static double MeleeThreat(GameEngine e, Unit attacker, Unit target) =>
        attacker.Warscroll.MeleeWeapons.Sum(w => AttackResolver.ExpectedDamage(e.State, attacker, target, w, attacker.AliveCount));

    private static double ChargeProbability(float distance)
    {
        int needed = (int)MathF.Ceiling(MathF.Max(2, distance - 0.5f));
        int ways = 0;
        for (int a = 1; a <= 6; a++) for (int b = 1; b <= 6; b++) if (a + b >= needed) ways++;
        return ways / 36.0;
    }

    private int Cp(GameEngine e) => e.State.Players[Player].CommandPoints;

    // ---------------------------------------------------------------- pending decisions

    private GameAction DecideDefence(GameEngine e, PendingDefence pd)
    {
        double threat = pd.Attacker.Warscroll.Weapons.Sum(w =>
            AttackResolver.ExpectedDamage(e.State, pd.Attacker, pd.Target, w, pd.Attacker.AliveCount));
        bool worth = threat >= 3 || (pd.Target.Warscroll.IsHero && threat >= 1.5);
        return new RespondDefence(Player, worth && Cp(e) >= 1);
    }

    private GameAction DecideReaction(GameEngine e, PendingReaction pr)
    {
        if (pr.Kind == ReactionKind.CoveringFire && Cp(e) >= 2)
        {
            var best = pr.Eligible
                .Select(u => (u, t: e.ClosestShootTarget(u)))
                .Where(x => x.t != null)
                .Select(x => (x.u, dmg: ExpectedDamage(e, x.u, x.t!, e.ShootingWeapons(x.u, x.t!))))
                .OrderByDescending(x => x.dmg).FirstOrDefault();
            if (best.u != null && best.dmg >= 1.5) return new UseReaction(Player, best.u.Id);
        }
        if (pr.Kind == ReactionKind.CounterCharge && Cp(e) >= 3)
        {
            var best = pr.Eligible
                .Select(u => (u, d: e.State.EnemiesOf(u).Min(en => GameState.Distance(u, en))))
                .Where(x => x.d <= 6).OrderBy(x => x.d).FirstOrDefault();
            if (best.u != null)
            {
                _chargeTarget = e.State.EnemiesOf(best.u).OrderBy(en => GameState.Distance(best.u, en)).First();
                return new UseReaction(Player, best.u.Id);
            }
        }
        return new DeclineReaction(Player);
    }

    private GameAction DecideMove(GameEngine e, PendingMove pm)
    {
        if (pm.Kind == MoveKind.Charge)
        {
            var targets = e.State.EnemiesOf(pm.Unit).OrderBy(t => t == _chargeTarget ? 0 : 1)
                .ThenBy(t => GameState.Distance(pm.Unit, t));
            foreach (var t in targets)
            {
                if (GameState.Distance(pm.Unit, t) > pm.Allowance + 0.5f) continue;
                var pos = Placement.AutoCharge(e, pm, t);
                if (pos != null) return new CompleteMove(Player, pos);
            }
            if (pm.CanReroll && Cp(e) >= 2) return new RerollCharge(Player);
            return new CancelMove(Player);
        }

        var goal = _moveGoal ?? DefaultGoal(e, pm.Unit);
        if (pm.Kind == MoveKind.Redeploy) goal = DefaultGoal(e, pm.Unit);
        var positions = Placement.MoveToward(e, pm, goal);
        return positions != null ? new CompleteMove(Player, positions) : new CancelMove(Player);
    }

    // ---------------------------------------------------------------- phases

    /// <summary>The objective the AI most wants to push towards: not ours, closest to the enemy's units.</summary>
    private Vector2 ForwardObjective(GameEngine e)
    {
        var s = e.State;
        var enemies = s.LiveUnitsOf(1 - Player).ToList();
        var objectives = s.Objectives.Where(o => o.ControlledBy != Player).DefaultIfEmpty(s.Objectives.FirstOrDefault()).Where(o => o != null).ToList();
        if (objectives.Count == 0) return s.Bounds.Center;
        if (enemies.Count == 0) return objectives[0]!.Position;
        return objectives.OrderBy(o => enemies.Min(en => Vector2.Distance(en.Centroid, o!.Position))).First()!.Position;
    }

    private GameAction Deploy(GameEngine e)
    {
        var s = e.State;
        // Deep strike: keep slow, hard-hitting melee units in reserve (fast units gain little and miss round-1 objectives).
        // One unit for small armies, two for larger ones.
        int wanted = s.UnitsOf(Player).Count() >= 8 ? 2 : 1;
        var reserveCandidate = s.UnitsOf(Player)
            .Where(u => !u.Deployed && !u.Warscroll.IsHero && !u.Warscroll.RangedWeapons.Any()
                        && u.Warscroll.Move <= 6 && u.Warscroll.Points >= 100)
            .Select(u => (u, pool: e.ReservePools(u).FirstOrDefault()))
            .Where(x => x.pool != null && e.CanDeployInReserve(x.u, x.pool))
            .OrderByDescending(x => x.u.Warscroll.Points).FirstOrDefault();
        if (UseReserves && reserveCandidate.u != null && e.ReservedCount(Player) < Math.Min(wanted, e.ReserveLimit(Player)))
            return new DeployInReserve(Player, reserveCandidate.u.Id, reserveCandidate.pool!);

        var unit = s.UnitsOf(Player).First(u => !u.Deployed);
        int index = s.UnitsOf(Player).ToList().IndexOf(unit);
        int count = s.UnitsOf(Player).Count();
        var zone = s.TerritoryOf(Player);
        var mid = s.Bounds.Center;
        // Spread units across the zone, towards the front edge (closest to the centre line).
        float x = zone.X + zone.W * (index + 1) / (count + 1);
        float frontY = zone.Center.Y < mid.Y ? zone.Bottom - 3 : zone.Y + 3;
        float frontX = zone.Center.X < mid.X ? zone.Right - 3 : zone.X + 3;
        Vector2 center = zone.W > zone.H ? new Vector2(x, frontY) : new Vector2(frontX, zone.Y + zone.H * (index + 1) / (count + 1));
        var pos = Placement.DeployNear(e, unit, center)
                  ?? throw new InvalidOperationException($"AI could not find room to deploy {unit.Name}.");
        return new DeployUnit(Player, unit.Id, pos);
    }

    private GameAction Hero(GameEngine e)
    {
        foreach (var u in Mine(e).ToList())
        {
            if (e.CanCast(u))
            {
                for (int i = 0; i < u.Warscroll.Spells.Count; i++)
                {
                    var spell = u.Warscroll.Spells[i];
                    if (!spell.IsScripted) continue; // unscripted: casting would only risk a miscast
                    if (e.State.Players[Player].SpellsCastThisTurn.Contains(spell.Name)) continue;
                    var target = e.SpellTargets(u, spell).FirstOrDefault();
                    if (target != null) return new CastSpell(Player, u.Id, i, target.Id);
                }
            }
            _handled.Add(u.Id);
            int lost = u.Warscroll.Health * u.Models.Count - u.WoundsRemaining;
            if (e.CanRally(u) && Cp(e) >= 2 && lost >= Math.Max(2, u.Warscroll.Health))
                return new Rally(Player, u.Id);
        }
        return new EndPhase(Player);
    }

    private Vector2 DefaultGoal(GameEngine e, Unit unit)
    {
        var s = e.State;
        var enemies = s.EnemiesOf(unit).ToList();
        // Melee-heavy units go for the nearest enemy if it is close; otherwise everyone heads for objectives.
        var nearestEnemy = enemies.OrderBy(en => GameState.Distance(unit, en)).FirstOrDefault();
        bool hasRanged = unit.Warscroll.RangedWeapons.Any();
        if (nearestEnemy != null && !hasRanged && GameState.Distance(unit, nearestEnemy) <= e.MoveCharacteristic(unit) + 12)
            return nearestEnemy.Centroid;

        var objective = s.Objectives
            .OrderBy(o => o.ControlledBy == Player ? 1 : 0)
            .ThenBy(o => Vector2.Distance(o.Position, unit.Centroid) + ClaimPenalty(e, unit, o))
            .FirstOrDefault();
        if (objective != null) return objective.Position;
        return nearestEnemy?.Centroid ?? unit.Centroid;
    }

    /// <summary>Discourage every unit from piling onto the same objective.</summary>
    private float ClaimPenalty(GameEngine e, Unit unit, Objective o) =>
        e.State.LiveUnitsOf(Player).Count(u => u != unit && Vector2.Distance(u.Centroid, o.Position) < 4) * 8f;

    private GameAction Movement(GameEngine e)
    {
        var s = e.State;
        // Bring reserves in as soon as allowed, near the objective we want.
        foreach (var r in e.ArrivableUnits(Player).ToList())
        {
            if (_handled.Contains(r.Id)) continue;
            _handled.Add(r.Id);
            var pos = Placement.FindArrival(e, r, ForwardObjective(e));
            if (pos != null) return new ArriveFromReserve(Player, r.Id, pos);
        }
        foreach (var u in Mine(e).ToList())
        {
            _handled.Add(u.Id);
            if (!e.CanNormalMove(u)) continue; // in combat: hold and fight

            // Shooters with a target already in range stay put.
            if (u.Warscroll.RangedWeapons.Any() && e.ShootTargets(u).Any()) continue;

            var goal = DefaultGoal(e, u);
            // Already standing on the objective we want.
            if (s.Objectives.Any(o => o.Position == goal && Vector2.Distance(u.Centroid, goal) < 2)) continue;

            _moveGoal = goal;
            float distance = Vector2.Distance(u.Centroid, goal);
            bool wantsCharge = !u.Warscroll.RangedWeapons.Any() && s.EnemiesOf(u).Any(en => GameState.Distance(u, en) <= 12 + e.MoveCharacteristic(u));
            bool run = distance > e.MoveCharacteristic(u) + 2 && !wantsCharge;
            return new DeclareMove(Player, u.Id, run ? MoveKind.Run : MoveKind.Normal);
        }
        _moveGoal = null;
        return new EndPhase(Player);
    }

    private GameAction Shooting(GameEngine e)
    {
        foreach (var u in Mine(e).ToList())
        {
            _handled.Add(u.Id);
            if (!e.CanShoot(u)) continue;
            var best = e.ShootTargets(u)
                .Select(t => (t, dmg: ExpectedDamage(e, u, t, e.ShootingWeapons(u, t))))
                .OrderByDescending(x => x.dmg).First();
            bool aoa = best.dmg >= 3 && Cp(e) >= 3 && e.CanUseCommand(Player, Commands.AllOutAttack, u);
            return new Shoot(Player, u.Id, best.t.Id, aoa);
        }
        return new EndPhase(Player);
    }

    private GameAction Charge(GameEngine e)
    {
        foreach (var u in Mine(e).ToList())
        {
            _handled.Add(u.Id);
            if (!e.CanCharge(u)) continue;
            var target = e.State.EnemiesOf(u)
                .Select(t => (t, d: GameState.Distance(u, t)))
                .Where(x => x.d <= GameEngine.MaxChargeDistance)
                .OrderByDescending(x => MeleeThreat(e, u, x.t) * ChargeProbability(x.d)).FirstOrDefault();
            if (target.t == null) continue;
            if (ChargeProbability(target.d) < 0.4) continue;
            _chargeTarget = target.t;
            return new DeclareCharge(Player, u.Id);
        }
        return new EndPhase(Player);
    }

    private GameAction CombatPhase(GameEngine e)
    {
        var options = e.PickableFighters(Player)
            .SelectMany(u => e.FightTargets(u).Select(t => (u, t, dmg: MeleeThreat(e, u, t), finish: t.WoundsRemaining,
                inRange: GameState.Distance(u, t) <= Geometry.Geo.CombatRange)))
            .ToList();
        if (options.Count == 0) return new EndPhase(Player);
        // Prefer targets already in combat range, then finishing off units, then maximum damage.
        var pick = options.OrderByDescending(o => o.inRange)
            .ThenByDescending(o => o.dmg >= o.finish ? 1 : 0).ThenByDescending(o => o.dmg).First();
        bool aoa = pick.dmg >= 3 && Cp(e) >= 2 && e.CanUseCommand(Player, Commands.AllOutAttack, pick.u);
        return new Fight(Player, pick.u.Id, pick.t.Id, aoa);
    }

    private GameAction EndOfTurn(GameEngine e)
    {
        foreach (var u in Mine(e).ToList())
        {
            _handled.Add(u.Id);
            if (e.CanPowerThrough(u) && Cp(e) >= 2)
                return new PowerThrough(Player, u.Id, e.PowerThroughTargets(u).OrderBy(t => t.WoundsRemaining).First().Id);
        }
        return new EndPhase(Player);
    }
}
