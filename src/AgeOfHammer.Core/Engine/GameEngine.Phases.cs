using System.Numerics;
using AgeOfHammer.Core.Combat;
using AgeOfHammer.Core.Data;
using AgeOfHammer.Core.Dice;
using AgeOfHammer.Core.Geometry;
using AgeOfHammer.Core.State;

namespace AgeOfHammer.Core.Engine;

public sealed partial class GameEngine
{
    // ================================================================== HERO PHASE

    public bool CanRally(Unit unit) =>
        State.Phase == Phase.Hero && Pending == null && unit.Owner == State.ActivePlayer && unit.OnBattlefield
        && !State.InCombat(unit) && CanUseCommand(unit.Owner, Commands.Rally, unit)
        && (unit.Alive.Any(m => m.Damage > 0) || unit.Models.Any(m => m.Slain));

    private ActionResult DoRally(Rally r)
    {
        var unit = State.GetUnit(r.UnitId);
        if (unit.Owner != r.Player || !CanRally(unit)) return ActionResult.Fail("That unit cannot Rally now.");
        PayCommand(r.Player, Commands.Rally, unit);
        int points = 0;
        for (int i = 0; i < 6; i++) if (State.Dice.D6() >= 4) points++;
        int health = unit.Warscroll.Health;
        int returned = 0, healed = 0;
        if (unit.Models.Count > 1)
        {
            while (points >= health && unit.Models.FirstOrDefault(m => m.Slain) is { } dead)
            {
                if (!TryPlaceReturningModel(unit, dead)) break;
                points -= health;
                returned++;
            }
        }
        foreach (var m in unit.Alive.Where(m => m.Damage > 0).OrderByDescending(m => m.Damage))
        {
            int h = Math.Min(points, m.Damage);
            m.Damage -= h;
            points -= h;
            healed += h;
        }
        State.Write($"{unit.Name} rallies: {returned} model(s) returned, {healed} wound(s) healed.");
        return ActionResult.Success;
    }

    private bool TryPlaceReturningModel(Unit unit, Model dead)
    {
        float r = unit.BaseRadius;
        foreach (var anchor in unit.Alive.ToList())
            for (int a = 0; a < 12; a++)
            {
                float ang = a * MathF.PI / 6;
                var p = anchor.Position + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * (2 * r + 0.2f);
                if (!State.Bounds.ContainsCircle(p, r) || OverlapsTerrain(p, r)) continue;
                if (OverlapsAnything(unit, p, r)) continue;
                if (State.EnemiesOf(unit).Any(e => e.Alive.Any(em => Geo.Distance(p, r, em.Position, e.BaseRadius) <= Geo.CombatRange))) continue;
                dead.Slain = false;
                dead.Damage = 0;
                dead.Position = p;
                return true;
            }
        return false;
    }

    private bool OverlapsAnything(Unit self, Vector2 p, float r) =>
        State.Units.Where(u => u.OnBattlefield).Any(u => u.Alive.Any(m => Geo.Overlaps(p, r, m.Position, u.BaseRadius)));

    public bool CanCast(Unit unit) =>
        State.Phase == Phase.Hero && Pending == null && unit.Owner == State.ActivePlayer && unit.OnBattlefield
        && unit.Warscroll.IsWizard && unit.CastsThisPhase < unit.Warscroll.WizardLevel && unit.Warscroll.Spells.Count > 0;

    public IEnumerable<Unit> SpellTargets(Unit caster, SpellDef spell)
    {
        if (spell.Target == "self") return [caster];
        var pool = spell.Target == "friendly" ? State.LiveUnitsOf(caster.Owner) : State.EnemiesOf(caster);
        return pool.Where(t => GameState.Distance(caster, t) <= spell.Range && (t == caster || State.CanSee(caster, t)));
    }

    private ActionResult DoCastSpell(CastSpell cs)
    {
        var caster = State.GetUnit(cs.UnitId);
        if (caster.Owner != cs.Player || !CanCast(caster)) return ActionResult.Fail("That unit cannot cast now.");
        if (cs.SpellIndex < 0 || cs.SpellIndex >= caster.Warscroll.Spells.Count) return ActionResult.Fail("Unknown spell.");
        var spell = caster.Warscroll.Spells[cs.SpellIndex];
        var player = State.Players[cs.Player];
        if (player.SpellsCastThisTurn.Contains(spell.Name)) return ActionResult.Fail($"{spell.Name} has already been cast this turn.");
        var target = State.GetUnit(cs.TargetId);
        if (!SpellTargets(caster, spell).Contains(target)) return ActionResult.Fail("Invalid target for that spell.");

        caster.CastsThisPhase++;
        player.SpellsCastThisTurn.Add(spell.Name);
        int d1 = State.Dice.D6(), d2 = State.Dice.D6();
        int roll = d1 + d2 + Modifiers.Get(State, caster, Stat.Cast, AttackType.Any);
        if (d1 == 1 && d2 == 1)
        {
            State.Write($"{caster.Name} MISCASTS {spell.Name}!");
            caster.CastsThisPhase = caster.Warscroll.WizardLevel;
            AttackResolver.InflictMortal(State, caster, State.Dice.D3(), "Miscast");
            CheckWipeout();
            return ActionResult.Success;
        }
        if (roll < spell.CastingValue)
        {
            State.Write($"{caster.Name} fails to cast {spell.Name} ({roll} vs {spell.CastingValue}).");
            return ActionResult.Success;
        }
        var unbinder = State.EnemiesOf(caster)
            .Where(e => e.Warscroll.IsWizard && GameState.Distance(caster, e) <= 30)
            .OrderByDescending(e => e.Warscroll.WizardLevel).FirstOrDefault();
        if (unbinder != null)
        {
            int unbind = State.Dice.Roll2D6();
            if (unbind > roll)
            {
                State.Write($"{caster.Name} casts {spell.Name} ({roll}) but {unbinder.Name} unbinds it ({unbind}).");
                return ActionResult.Success;
            }
        }
        State.Write($"{caster.Name} casts {spell.Name} ({roll} vs {spell.CastingValue}).");
        ApplyEffect(spell.Effect, caster, target, spell.Name);
        CheckWipeout();
        return ActionResult.Success;
    }

    private void ApplyEffect(EffectDef? effect, Unit source, Unit target, string name)
    {
        if (effect == null) { State.Write($"({name} has no scripted effect yet.)"); return; }
        switch (effect.Type.ToLowerInvariant())
        {
            case "mortal":
                AttackResolver.InflictMortal(State, target, DiceExpression.Parse(effect.Amount).Roll(State.Dice), name);
                break;
            case "heal":
                int heal = DiceExpression.Parse(effect.Amount).Roll(State.Dice);
                int done = 0;
                foreach (var m in target.Alive.Where(m => m.Damage > 0))
                {
                    int h = Math.Min(heal - done, m.Damage);
                    m.Damage -= h;
                    done += h;
                }
                State.Write($"{name}: healed {done} on {target.Name}.");
                break;
            case "buff":
                if (Enum.TryParse<Stat>(effect.Stat, true, out var stat))
                {
                    var exp = Enum.TryParse<Expiry>(effect.Expiry, true, out var e) ? e : Expiry.EndOfTurn;
                    var at = Enum.TryParse<AttackType>(effect.AttackType, true, out var t) ? t : AttackType.Any;
                    target.Buffs.Add(new Buff(name, stat, effect.Value, exp, at));
                    State.Write($"{name}: {target.Name} gets {effect.Value:+#;-#;0} {stat} ({exp}).");
                }
                break;
        }
    }

    // ================================================================== MOVEMENT PHASE

    public int MoveCharacteristic(Unit unit) => Math.Max(0, unit.Warscroll.Move + Modifiers.Get(State, unit, Stat.Move, AttackType.Any));

    private bool CanStartMove(Unit unit) =>
        State.Phase == Phase.Movement && Pending == null && unit.Owner == State.ActivePlayer && unit.OnBattlefield && !unit.Moved;

    public bool CanNormalMove(Unit unit) => CanStartMove(unit) && !State.InCombat(unit);
    public bool CanRun(Unit unit) => CanNormalMove(unit);
    public bool CanRetreat(Unit unit) => CanStartMove(unit) && State.InCombat(unit);

    private ActionResult DoDeclareMove(DeclareMove m)
    {
        var unit = State.GetUnit(m.UnitId);
        if (unit.Owner != m.Player) return ActionResult.Fail("Not your unit.");
        float allowance = MoveCharacteristic(unit);
        switch (m.Kind)
        {
            case MoveKind.Normal:
                if (!CanNormalMove(unit)) return ActionResult.Fail("That unit cannot make a normal move.");
                break;
            case MoveKind.Run:
                if (!CanRun(unit)) return ActionResult.Fail("That unit cannot run.");
                int runRoll;
                if (m.AtTheDouble)
                {
                    if (!CanUseCommand(m.Player, Commands.AtTheDouble, unit)) return ActionResult.Fail("Cannot use At the Double.");
                    PayCommand(m.Player, Commands.AtTheDouble, unit);
                    runRoll = 6;
                }
                else runRoll = State.Dice.D6();
                allowance += runRoll;
                unit.Ran = true;
                State.Write($"{unit.Name} runs: +{runRoll}\" (total {allowance}\").");
                break;
            case MoveKind.Retreat:
                if (!CanRetreat(unit)) return ActionResult.Fail("That unit cannot retreat.");
                unit.Retreated = true;
                unit.Moved = true;
                AttackResolver.InflictMortal(State, unit, State.Dice.D3(), $"{unit.Name} retreats");
                CheckWipeout();
                if (unit.IsDestroyed || State.Phase == Phase.GameOver) return ActionResult.Success;
                break;
            default:
                return ActionResult.Fail("Use DeclareCharge for charges.");
        }
        unit.Moved = true;
        Pending = new PendingMove { Decider = m.Player, Unit = unit, Kind = m.Kind, Allowance = allowance };
        return ActionResult.Success;
    }

    /// <summary>Checks a proposed set of end positions for the pending move. Returns null if legal.</summary>
    public string? ValidateMove(PendingMove pm, IReadOnlyList<Vector2> positions)
    {
        var unit = pm.Unit;
        var alive = unit.Alive.ToList();
        if (positions.Count != alive.Count) return $"Expected {alive.Count} positions.";
        float r = unit.BaseRadius;
        bool fly = unit.Warscroll.CanFly;
        var enemies = State.EnemiesOf(unit).ToList();

        for (int i = 0; i < alive.Count; i++)
        {
            var from = alive[i].Position;
            var to = positions[i];
            if (Vector2.Distance(from, to) > pm.Allowance + Geo.Epsilon)
                return $"A model would move {Vector2.Distance(from, to):0.0}\" (max {pm.Allowance:0.0}\").";
            if (!State.Bounds.ContainsCircle(to, r)) return "Models must stay on the battlefield.";
            if (!fly)
                foreach (var t in State.Battleplan.Terrain.Where(t => t.Impassable))
                    if (Geo.SweptCircleHitsRect(from, to, r, t.Area.ToRect())) return $"Models cannot move through {t.Name}.";

            if (pm.Kind is MoveKind.Normal or MoveKind.Run or MoveKind.Redeploy or MoveKind.Retreat)
                foreach (var e in enemies)
                    foreach (var em in e.Alive)
                    {
                        if (Geo.Distance(to, r, em.Position, e.BaseRadius) <= Geo.CombatRange)
                            return "Models cannot end this move in combat range (3\") of an enemy.";
                        if (pm.Kind != MoveKind.Retreat
                            && Geo.SegmentPointDistance(from, to, em.Position) - r - e.BaseRadius < Geo.CombatRange - Geo.Epsilon)
                            return "Models cannot move through combat range of an enemy.";
                    }
        }
        if (Overlaps(unit, positions)) return "Models cannot overlap other models.";
        if (!Geo.IsCoherent(positions, r)) return "Unit must end its move in coherency.";

        if (pm.Kind == MoveKind.Charge)
        {
            bool reached = enemies.Any(e => e.Alive.Any(em => positions.Any(p =>
                Geo.Distance(p, r, em.Position, e.BaseRadius) <= Geo.ChargeEndRange + Geo.Epsilon)));
            if (!reached) return "A charging unit must end within 1/2\" of an enemy unit.";
        }
        return null;
    }

    private ActionResult DoCompleteMove(PendingMove pm, CompleteMove cm)
    {
        var error = ValidateMove(pm, cm.Positions);
        if (error != null) return ActionResult.Fail(error);
        var alive = pm.Unit.Alive.ToList();
        for (int i = 0; i < alive.Count; i++) alive[i].Position = cm.Positions[i];
        if (pm.Kind == MoveKind.Charge)
        {
            pm.Unit.Charged = true;
            State.Write($"{pm.Unit.Name} completes its charge!");
        }
        Pending = null;
        pm.Then?.Invoke();
        return ActionResult.Success;
    }

    private ActionResult DoCancelMove(PendingMove pm)
    {
        if (pm.Kind == MoveKind.Normal) pm.Unit.Moved = false;
        if (pm.Kind == MoveKind.Charge) State.Write($"{pm.Unit.Name}'s charge fails.");
        Pending = null;
        pm.Then?.Invoke();
        return ActionResult.Success;
    }

    // ================================================================== SHOOTING PHASE

    /// <summary>Ranged weapons the unit may use right now (only Shoot in Combat weapons while in combat).</summary>
    public List<WeaponProfile> UsableRangedWeapons(Unit unit)
    {
        bool inCombat = State.InCombat(unit);
        return unit.Warscroll.RangedWeapons.Where(w => !inCombat || w.ShootInCombat).ToList();
    }

    public List<WeaponUse> ShootingWeapons(Unit unit, Unit target)
    {
        var list = new List<(WeaponProfile, int)>();
        foreach (var w in UsableRangedWeapons(unit))
        {
            int models = unit.Alive.Count(m => GameState.Distance(m, unit, target) <= w.Range + Geo.Epsilon && State.CanSee(m, target));
            if (w.ModelsWithWeapon is int cap) models = Math.Min(models, cap);
            list.Add((w, models));
        }
        return Uses(list);
    }

    public bool CanShootBasic(Unit unit) =>
        unit.OnBattlefield && !unit.Shot && !unit.Ran && !unit.Retreated && UsableRangedWeapons(unit).Count > 0;

    public bool CanShoot(Unit unit) =>
        State.Phase == Phase.Shooting && Pending == null && unit.Owner == State.ActivePlayer && CanShootBasic(unit)
        && ShootTargets(unit).Any();

    public IEnumerable<Unit> ShootTargets(Unit unit) => State.EnemiesOf(unit).Where(t => ShootingWeapons(unit, t).Count > 0);

    private ActionResult DoShoot(Shoot s)
    {
        var unit = State.GetUnit(s.UnitId);
        var target = State.GetUnit(s.TargetId);
        if (unit.Owner != s.Player || !CanShoot(unit)) return ActionResult.Fail("That unit cannot shoot now.");
        var weapons = ShootingWeapons(unit, target);
        if (weapons.Count == 0) return ActionResult.Fail("No weapons in range of that target.");
        var options = new AttackOptions();
        if (s.AllOutAttack)
        {
            if (!CanUseCommand(s.Player, Commands.AllOutAttack, unit)) return ActionResult.Fail("Cannot use All-out Attack.");
            PayCommand(s.Player, Commands.AllOutAttack, unit);
            options = options with { ExtraHit = 1 };
        }
        unit.Shot = true;
        BeginAttack(unit, target, weapons, options, null);
        return ActionResult.Success;
    }

    // ================================================================== CHARGE PHASE

    public const float MaxChargeDistance = 12f;

    public bool CanChargeBasic(Unit unit) =>
        unit.OnBattlefield && !unit.ChargeAttempted && !unit.Ran && !unit.Retreated && !State.InCombat(unit)
        && State.EnemiesOf(unit).Any(e => GameState.Distance(unit, e) <= MaxChargeDistance);

    public bool CanCharge(Unit unit) =>
        State.Phase == Phase.Charge && Pending == null && unit.Owner == State.ActivePlayer && CanChargeBasic(unit);

    private ActionResult DoDeclareCharge(DeclareCharge c)
    {
        var unit = State.GetUnit(c.UnitId);
        if (unit.Owner != c.Player || !CanCharge(unit)) return ActionResult.Fail("That unit cannot charge.");
        StartCharge(unit, c.Player, null);
        return ActionResult.Success;
    }

    private void StartCharge(Unit unit, int player, Action? then)
    {
        unit.ChargeAttempted = true;
        int roll = State.Dice.Roll2D6() + Modifiers.Get(State, unit, Stat.Charge, AttackType.Any);
        State.Write($"{unit.Name} declares a charge: rolled {roll}\".");
        bool canReroll = CanUseCommand(player, Commands.ForwardToVictory, unit);
        if (!ChargeReachable(unit, roll) && !canReroll)
        {
            State.Write($"{unit.Name}'s charge fails - no enemy within {roll}\".");
            then?.Invoke();
            return;
        }
        Pending = new PendingMove { Decider = player, Unit = unit, Kind = MoveKind.Charge, Allowance = roll, CanReroll = canReroll, Then = then };
    }

    public bool ChargeReachable(Unit unit, float roll) =>
        State.EnemiesOf(unit).Any(e => GameState.Distance(unit, e) <= roll + Geo.ChargeEndRange);

    private ActionResult DoRerollCharge(PendingMove pm)
    {
        if (pm.Kind != MoveKind.Charge || !pm.CanReroll || !CanUseCommand(pm.Decider, Commands.ForwardToVictory, pm.Unit))
            return ActionResult.Fail("Cannot re-roll this charge.");
        PayCommand(pm.Decider, Commands.ForwardToVictory, pm.Unit);
        pm.CanReroll = false;
        pm.Allowance = State.Dice.Roll2D6() + Modifiers.Get(State, pm.Unit, Stat.Charge, AttackType.Any);
        State.Write($"{pm.Unit.Name} re-rolls the charge: {pm.Allowance}\".");
        if (!ChargeReachable(pm.Unit, pm.Allowance)) return DoCancelMove(pm);
        return ActionResult.Success;
    }

    // ================================================================== COMBAT PHASE

    public bool IsEligibleToFight(Unit unit) =>
        unit.OnBattlefield && !unit.Fought && (State.InCombat(unit) || unit.Charged);

    /// <summary>0 = strike-first, 1 = normal, 2 = strike-last.</summary>
    public int FightTier(Unit unit)
    {
        bool first = Modifiers.HasFlag(State, unit, Stat.StrikeFirst);
        bool last = Modifiers.HasFlag(State, unit, Stat.StrikeLast);
        if (first && !last) return 0;
        if (last && !first) return 2;
        return 1;
    }

    private int CurrentFightTier()
    {
        var all = State.Units.Where(IsEligibleToFight).ToList();
        return all.Count == 0 ? int.MaxValue : all.Min(FightTier);
    }

    public IEnumerable<Unit> PickableFighters(int player)
    {
        if (State.Phase != Phase.Combat) return [];
        int tier = CurrentFightTier();
        return State.LiveUnitsOf(player).Where(u => IsEligibleToFight(u) && FightTier(u) == tier);
    }

    private int CombatDecider()
    {
        if (PickableFighters(_combatPicker).Any()) return _combatPicker;
        int other = 1 - _combatPicker;
        if (PickableFighters(other).Any()) return other;
        return State.ActivePlayer;
    }

    public bool CanFight(Unit unit) =>
        State.Phase == Phase.Combat && Pending == null && unit.Owner == Decider && PickableFighters(unit.Owner).Contains(unit);

    /// <summary>Enemy units this unit could fight: within combat range, or reachable with a 3" pile-in.</summary>
    public IEnumerable<Unit> FightTargets(Unit unit) =>
        State.EnemiesOf(unit).Where(e => GameState.Distance(unit, e) <= Geo.CombatRange + Placement.PileInDistance + Geo.Epsilon);

    private ActionResult DoFight(Fight f)
    {
        var unit = State.GetUnit(f.UnitId);
        var target = State.GetUnit(f.TargetId);
        if (unit.Owner != f.Player || !CanFight(unit)) return ActionResult.Fail("That unit cannot fight now.");
        if (!FightTargets(unit).Contains(target)) return ActionResult.Fail("Target is out of reach.");
        var options = new AttackOptions();
        if (f.AllOutAttack)
        {
            if (!CanUseCommand(f.Player, Commands.AllOutAttack, unit)) return ActionResult.Fail("Cannot use All-out Attack.");
            PayCommand(f.Player, Commands.AllOutAttack, unit);
            options = options with { ExtraHit = 1 };
        }

        var pile = Placement.PileIn(State, unit, target);
        var alive = unit.Alive.ToList();
        for (int i = 0; i < alive.Count; i++) alive[i].Position = pile[i];
        unit.Fought = true;

        var weapons = Uses(unit.Warscroll.MeleeWeapons.Select(w =>
        {
            int models = unit.Alive.Count(m => GameState.Distance(m, unit, target) <= Geo.CombatRange + Geo.Epsilon);
            if (w.ModelsWithWeapon is int cap) models = Math.Min(models, cap);
            return (w, models);
        }));
        BeginAttack(unit, target, weapons, options, () => AfterFight(f.Player));
        return ActionResult.Success;
    }

    private void AfterFight(int player)
    {
        _combatPicker = 1 - player;
        if (!State.Units.Any(IsEligibleToFight)) AdvancePhase();
    }

    private void PassCombat(int player)
    {
        var mine = State.LiveUnitsOf(player).Where(IsEligibleToFight).ToList();
        foreach (var u in mine) u.Fought = true;
        if (mine.Count > 0) State.Write($"{State.Players[player].Name} passes; {mine.Count} unit(s) do not fight.");
        _combatPicker = 1 - player;
        if (!State.Units.Any(IsEligibleToFight)) AdvancePhase();
    }

    // ================================================================== END OF TURN

    public IEnumerable<Unit> PowerThroughTargets(Unit unit) =>
        State.EnemiesInCombatWith(unit).Where(e => e.Warscroll.Health < unit.Warscroll.Health);

    public bool CanPowerThrough(Unit unit) =>
        State.Phase == Phase.EndOfTurn && Pending == null && unit.Owner == State.ActivePlayer && unit.OnBattlefield
        && unit.Charged && CanUseCommand(unit.Owner, Commands.PowerThrough, unit) && PowerThroughTargets(unit).Any();

    private ActionResult DoPowerThrough(PowerThrough pt)
    {
        var unit = State.GetUnit(pt.UnitId);
        var target = State.GetUnit(pt.TargetId);
        if (unit.Owner != pt.Player || !CanPowerThrough(unit) || !PowerThroughTargets(unit).Contains(target))
            return ActionResult.Fail("Cannot Power Through.");
        PayCommand(pt.Player, Commands.PowerThrough, unit);
        AttackResolver.InflictMortal(State, target, State.Dice.D3(), $"{unit.Name} powers through");
        CheckWipeout();
        return ActionResult.Success;
    }

    // ================================================================== REACTIONS (opponent's turn)

    public List<Unit> ReactionCandidates(ReactionKind kind)
    {
        int opp = 1 - State.ActivePlayer;
        var units = State.LiveUnitsOf(opp).Where(u => !u.ReceivedCommandThisPhase && !State.InCombat(u));
        return kind switch
        {
            ReactionKind.Redeploy => units.ToList(),
            ReactionKind.CoveringFire => units.Where(u => !u.Shot && UsableRangedWeapons(u).Count > 0 && ClosestShootTarget(u) != null).ToList(),
            ReactionKind.CounterCharge => units.Where(CanChargeBasic).ToList(),
            _ => [],
        };
    }

    /// <summary>Covering Fire must target the closest visible enemy unit in range.</summary>
    public Unit? ClosestShootTarget(Unit unit) =>
        ShootTargets(unit).OrderBy(t => GameState.Distance(unit, t)).FirstOrDefault();

    private bool TryOfferReaction(ReactionKind kind)
    {
        if (_reactionOffered) return false;
        _reactionOffered = true;
        int opp = 1 - State.ActivePlayer;
        if (!CanUseCommand(opp, Commands.ForReaction(kind), null)) return false;
        var eligible = ReactionCandidates(kind);
        if (eligible.Count == 0) return false;
        Pending = new PendingReaction { Decider = opp, Kind = kind, Eligible = eligible };
        return true;
    }

    private ActionResult DoUseReaction(PendingReaction pr, UseReaction ur)
    {
        var unit = State.GetUnit(ur.UnitId);
        if (!pr.Eligible.Contains(unit)) return ActionResult.Fail("That unit cannot react.");
        var command = Commands.ForReaction(pr.Kind);
        if (!CanUseCommand(ur.Player, command, unit)) return ActionResult.Fail($"Cannot use {command}.");
        Pending = null;
        PayCommand(ur.Player, command, unit);
        switch (pr.Kind)
        {
            case ReactionKind.Redeploy:
                int roll = State.Dice.D6();
                State.Write($"{unit.Name} redeploys up to {roll}\".");
                Pending = new PendingMove { Decider = ur.Player, Unit = unit, Kind = MoveKind.Redeploy, Allowance = roll, Then = AdvancePhase };
                break;
            case ReactionKind.CoveringFire:
                var target = ClosestShootTarget(unit)!;
                unit.Shot = true;
                BeginAttack(unit, target, ShootingWeapons(unit, target), new AttackOptions { ExtraHit = -1 }, AdvancePhase);
                break;
            case ReactionKind.CounterCharge:
                StartCharge(unit, ur.Player, AdvancePhase);
                break;
        }
        return ActionResult.Success;
    }
}
