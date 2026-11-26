using System.Numerics;
using AgeOfHammer.Core.Combat;
using AgeOfHammer.Core.Data;
using AgeOfHammer.Core.Dice;
using AgeOfHammer.Core.Geometry;
using AgeOfHammer.Core.State;

namespace AgeOfHammer.Core.Engine;

/// <summary>
/// Activated abilities (<see cref="ActivationDef"/>): when they can be used, valid targets, and resolving effects.
/// <list type="bullet">
/// <item>The active player uses them with <see cref="UseAbility"/> in their own phases.</item>
/// <item>In the combat phase whoever is picking can use their units' combat abilities.</item>
/// <item>The other player gets an ability window at the end of each phase for their "Any"/"Enemy" phase abilities.</item>
/// <item>Abilities marked Auto are resolved by the engine when their phase starts.</item>
/// </list>
/// </summary>
public sealed partial class GameEngine
{
    private bool _abilityWindowOffered;

    /// <summary>The scripted abilities a unit has (warscroll abilities plus faction battle traits that can be activated).</summary>
    public IEnumerable<AbilityDef> ActivatedAbilities(Unit unit) =>
        Modifiers.AbilitiesOf(State, unit).Where(a => a.Activate != null);

    /// <summary>Does the current phase and turn match the ability's timing for this unit's owner?</summary>
    public bool TimingMatches(Unit unit, AbilityDef ability)
    {
        var phase = ability.Phase switch
        {
            "hero" or "start-of-turn" => Phase.Hero,
            "movement" => Phase.Movement,
            "shooting" => Phase.Shooting,
            "charge" => Phase.Charge,
            "combat" => Phase.Combat,
            "end-of-turn" => Phase.EndOfTurn,
            _ => (Phase?)null,
        };
        if (phase != State.Phase) return false;
        bool myTurn = unit.Owner == State.ActivePlayer;
        return ability.Perspective switch
        {
            "your" => myTurn,
            "enemy" => !myTurn,
            _ => true,
        };
    }

    private string UsageKey(Unit unit, AbilityDef ability) => ability.CostKind == "prayer"
        ? $"turn{State.BattleRound}.{(State.SecondTurnOfRound ? 2 : 1)}|P{unit.Owner}|prayer|{ability.Name}" // each prayer once per turn per army
        : ability.Frequency switch
    {
        "battle-army" => $"battle|P{unit.Owner}|{ability.Name}",
        "battle-unit" => $"battle|U{unit.Id}|{ability.Name}",
        "round-army" => $"round{State.BattleRound}|P{unit.Owner}|{ability.Name}",
        "turn-army" => $"turn{State.BattleRound}.{(State.SecondTurnOfRound ? 2 : 1)}|P{unit.Owner}|{ability.Name}",
        _ => $"phase{State.BattleRound}.{(State.SecondTurnOfRound ? 2 : 1)}.{State.Phase}|U{unit.Id}|{ability.Name}",
    };

    /// <summary>Could this unit use this ability right now, ignoring whose decision it is?</summary>
    public bool AbilityAvailable(Unit unit, AbilityDef ability)
    {
        var act = ability.Activate;
        if (act == null || !unit.OnBattlefield || !TimingMatches(unit, ability)) return false;
        if (State.UsedAbilities.Contains(UsageKey(unit, ability))) return false;
        if (ability.CostKind == "prayer" && unit.ChantsThisPhase >= Math.Max(1, unit.Warscroll.PriestLevel)) return false;
        if (!Modifiers.ConditionHolds(State, act.Requires, unit, unit, null)) return false;
        return act.Target == "self" ? Modifiers.ConditionHolds(State, act.TargetRequires, unit, unit, unit) : AbilityTargets(unit, ability).Count > 0;
    }

    /// <summary>Can <paramref name="player"/> use this unit's ability as their next action?</summary>
    public bool CanUseAbility(int player, Unit unit, AbilityDef ability)
    {
        if (unit.Owner != player || Decider != player || ability.Activate?.Auto == true) return false;
        if (Pending != null && Pending is not PendingAbilityWindow) return false;
        if (Pending == null && State.Phase is not Phase.Combat && player != State.ActivePlayer) return false;
        return AbilityAvailable(unit, ability);
    }

    /// <summary>Abilities <paramref name="player"/> could use right now.</summary>
    public IEnumerable<(Unit Unit, AbilityDef Ability)> UsableAbilities(int player) =>
        State.LiveUnitsOf(player).SelectMany(u => ActivatedAbilities(u).Where(a => CanUseAbility(player, u, a)).Select(a => (u, a)));

    public List<Unit> AbilityTargets(Unit unit, AbilityDef ability)
    {
        var act = ability.Activate!;
        if (act.Target == "self") return [unit];
        bool enemy = act.Target == "enemy";
        var pool = enemy ? State.EnemiesOf(unit) : State.LiveUnitsOf(unit.Owner).Where(u => !act.Other || u != unit);
        return pool.Where(t =>
        {
            if (act.TargetKeywords.Any(k => !t.Warscroll.HasKeyword(k))) return false;
            if (act.TargetKeywordsAny.Count > 0 && !act.TargetKeywordsAny.Any(t.Warscroll.HasKeyword)) return false;
            if (act.TargetExcludeKeywords.Any(t.Warscroll.HasKeyword)) return false;
            if (act.TargetNames.Count > 0 && !act.TargetNames.Any(n => t.Name.Equals(n, StringComparison.OrdinalIgnoreCase))) return false;
            if (act.InCombat && GameState.Distance(unit, t) > Geo.CombatRange + Geo.Epsilon) return false;
            if (act.Range is float r && t != unit)
            {
                if (act.Wholly ? !Modifiers.WhollyWithin(t, unit, r) : GameState.Distance(unit, t) > r + Geo.Epsilon) return false;
            }
            return Modifiers.ConditionHolds(State, act.TargetRequires, unit, unit, t);
        }).ToList();
    }

    private ActionResult DoUseAbility(UseAbility ua)
    {
        var unit = State.GetUnit(ua.UnitId);
        var ability = ActivatedAbilities(unit).FirstOrDefault(a => a.Name == ua.Ability);
        if (ability == null) return ActionResult.Fail($"{unit.Name} has no usable ability '{ua.Ability}'.");
        if (!CanUseAbility(ua.Player, unit, ability)) return ActionResult.Fail($"{unit.Name} cannot use {ability.Name} now.");

        var valid = AbilityTargets(unit, ability);
        List<Unit> targets;
        if (ability.Activate!.Target == "self") targets = [unit];
        else
        {
            targets = (ua.Targets ?? []).Select(State.GetUnit).ToList();
            if (targets.Any(t => !valid.Contains(t))) return ActionResult.Fail("Invalid target.");
            targets = FillTargets(unit, ability.Activate, targets, valid);
            if (targets.Count == 0) return ActionResult.Fail("Pick a target.");
        }
        Resolve(unit, ability, targets);
        return ActionResult.Success;
    }

    /// <summary>Adds targets up to MaxTargets (0 = all eligible), nearest first, after the ones the player picked.</summary>
    private static List<Unit> FillTargets(Unit unit, ActivationDef act, List<Unit> picked, List<Unit> valid)
    {
        int max = act.MaxTargets <= 0 ? valid.Count : act.MaxTargets;
        var result = picked.Distinct().Take(max).ToList();
        foreach (var t in valid.OrderBy(t => GameState.Distance(unit, t)))
        {
            if (result.Count >= max) break;
            if (!result.Contains(t) && (act.MaxTargets != 1 || result.Count == 0)) result.Add(t);
        }
        return result;
    }

    private void Resolve(Unit unit, AbilityDef ability, List<Unit> targets)
    {
        var act = ability.Activate!;
        State.UsedAbilities.Add(UsageKey(unit, ability));
        string label = $"{unit.Name}: {ability.Name}";
        State.Write($"{label}" + (act.Target == "self" ? "" : $" -> {string.Join(", ", targets.Select(t => t.Name))}"));

        int sharedRoll = 0;
        if (ability.CostKind == "prayer")
        {
            // APPROX: chanting roll of D6 + modifiers against the chanting value; ritual points are not modelled.
            unit.ChantsThisPhase++;
            int chant = State.Dice.D6() + Modifiers.Get(State, unit, Stat.Chant, AttackType.Any);
            if (chant < (ability.CostValue ?? 0))
            {
                State.Write($"  chanting roll {chant} (needs {ability.CostValue}+): the prayer is not answered.");
                return;
            }
            State.Write($"  chanting roll {chant}: the prayer is answered.");
            sharedRoll = chant;
        }
        if (act.Roll != null && !act.PerTarget)
        {
            sharedRoll = GateRoll(unit, act);
            if (sharedRoll < act.Success)
            {
                State.Write($"  rolled {sharedRoll} (needs {act.Success}+): no effect.");
                return;
            }
            State.Write($"  rolled {sharedRoll}.");
        }

        foreach (var target in targets)
        {
            int roll = sharedRoll;
            if (act.Roll != null && act.PerTarget)
            {
                roll = GateRoll(unit, act);
                if (roll < act.Success)
                {
                    State.Write($"  {target.Name}: rolled {roll} (needs {act.Success}+): no effect.");
                    continue;
                }
            }
            if (target.Owner != unit.Owner && Resists(target, spell: false, ability.Name)) continue;
            ApplyEffects(act.Effects, unit, target, ability.Name, roll, first: target == targets[0]);
        }
        CheckWipeout();
        FlushQueuedPending();
    }

    /// <summary>Applies a list of effects for one target. Self and area effects happen once (for the first target).</summary>
    private void ApplyEffects(IEnumerable<EffectDef> effects, Unit user, Unit target, string name, int roll, bool first)
    {
        foreach (var effect in effects)
        {
            if (effect.Area is float area)
            {
                if (!first) continue;
                bool enemy = effect.Side != "friendly";
                foreach (var u in State.Units.Where(u => u.OnBattlefield && (u.Owner != user.Owner) == enemy && u != user
                                                          && GameState.Distance(u, user) <= area + Geo.Epsilon).ToList())
                    if (!enemy || !Resists(u, spell: false, name)) ApplyEffect(effect, user, u, name, roll);
                continue;
            }
            if (effect.To == "self")
            {
                if (first) ApplyEffect(effect, user, user, name, roll);
                continue;
            }
            ApplyEffect(effect, user, target, name, roll);
        }
    }

    /// <summary>
    /// Resistance rolls (e.g. Ruination Chamber: "on a 4+, that ability has no effect on this unit").
    /// Returns true if the target ignores the ability or spell.
    /// </summary>
    public bool Resists(Unit target, bool spell, string source)
    {
        var mods = Modifiers.Find(State, target, Stat.ResistAbilities);
        if (spell) mods.AddRange(Modifiers.Find(State, target, Stat.ResistSpells));
        foreach (var (m, from) in mods.OrderBy(x => x.Modifier.Value))
        {
            string key = $"turn{State.BattleRound}.{(State.SecondTurnOfRound ? 2 : 1)}|P{target.Owner}|resist|{m.Stat}";
            if (m.OncePerTurn && State.UsedAbilities.Contains(key)) continue;
            if (m.OncePerTurn) State.UsedAbilities.Add(key);
            int roll = State.Dice.D6();
            if (roll >= m.Value)
            {
                State.Write($"  {target.Name} resists {source} (rolled {roll}).");
                return true;
            }
            State.Write($"  {target.Name} fails to resist {source} (rolled {roll}).");
            return false;
        }
        return false;
    }

    private PendingDecision? _queuedPending;

    /// <summary>Move/set-up effects queue a decision; it becomes active once the ability has fully resolved.</summary>
    private void FlushQueuedPending()
    {
        if (_queuedPending == null || State.Phase == Phase.GameOver) { _queuedPending = null; return; }
        if (Pending == null || Pending is PendingAbilityWindow) Pending = _queuedPending;
        _queuedPending = null;
    }

    /// <summary>The gate roll; with RollCount "models", one dice per model and the best result counts.</summary>
    private int GateRoll(Unit unit, ActivationDef act)
    {
        var dice = DiceExpression.Parse(act.Roll!);
        int count = act.RollCount == "models" ? Math.Max(1, unit.AliveCount) : 1;
        int best = 0;
        for (int i = 0; i < count; i++) best = Math.Max(best, dice.Roll(State.Dice));
        return best;
    }

    /// <summary>Applies one effect (shared by spells and abilities). <paramref name="gateRoll"/> feeds Amount "roll".</summary>
    private void ApplyEffect(EffectDef effect, Unit source, Unit recipient, string name, int gateRoll)
    {
        if (!recipient.OnBattlefield && effect.Type != "returnModels") return;
        int Amount() => effect.Amount.Equals("roll", StringComparison.OrdinalIgnoreCase) ? gateRoll : DiceExpression.Parse(effect.Amount).Roll(State.Dice);

        switch (effect.Type)
        {
            case "mortal":
                AttackResolver.InflictMortal(State, recipient, Amount(), name);
                break;
            case "mortalPerModel":
            {
                int threshold = Math.Max(2, effect.Threshold);
                int hits = 0;
                for (int i = 0; i < recipient.AliveCount; i++) if (State.Dice.D6() >= threshold) hits++;
                if (hits > 0) AttackResolver.InflictMortal(State, recipient, hits, name);
                else State.Write($"  {name}: no damage to {recipient.Name}.");
                break;
            }
            case "mortalDice":
            {
                int count = Amount(), threshold = Math.Max(2, effect.Threshold), hits = 0;
                for (int i = 0; i < count; i++) if (State.Dice.D6() >= threshold) hits++;
                if (hits > 0) AttackResolver.InflictMortal(State, recipient, hits, name);
                else State.Write($"  {name}: no damage to {recipient.Name}.");
                break;
            }
            case "slayRoll":
            {
                int dice = DiceExpression.Parse(effect.Amount).Roll(State.Dice);
                int need = Math.Max(effect.Threshold, recipient.Warscroll.Health + effect.HealthOffset);
                int slain = 0;
                for (int i = 0; i < dice; i++)
                    if (State.Dice.D6() >= need && recipient.Alive.FirstOrDefault() is { } model)
                    {
                        model.Slain = true;
                        slain++;
                    }
                State.Write($"  {name}: {slain} model(s) slain in {recipient.Name}" + (recipient.IsDestroyed ? $" - {recipient.Name} DESTROYED" : ""));
                break;
            }
            case "heal":
            {
                int heal = Amount(), done = 0;
                foreach (var m in recipient.Alive.Where(m => m.Damage > 0).OrderByDescending(m => m.Damage))
                {
                    int h = Math.Min(heal - done, m.Damage);
                    m.Damage -= h;
                    done += h;
                }
                State.Write($"  {name}: healed {done} on {recipient.Name}.");
                break;
            }
            case "returnModels":
            {
                int n = Amount(), returned = 0;
                foreach (var dead in recipient.Models.Where(m => m.Slain).Take(n).ToList())
                    if (recipient.AliveCount > 0 && TryPlaceReturningModel(recipient, dead)) returned++;
                State.Write($"  {name}: {returned} model(s) returned to {recipient.Name}.");
                break;
            }
            case "table":
            {
                int r = DiceExpression.Parse(effect.Dice).Roll(State.Dice);
                var row = effect.Table.FirstOrDefault(t => r >= t.Min && r <= t.Max);
                State.Write($"  {name}: rolled {r}" + (row != null && row.Label.Length > 0 ? $" - {row.Label}" : ""));
                if (row != null) ApplyEffects(row.Effects, source, recipient, name, r, first: true);
                break;
            }
            case "move":
            {
                if (_queuedPending != null || !recipient.OnBattlefield) break;
                int distance = effect.Amount.Equals("Move", StringComparison.OrdinalIgnoreCase) ? MoveCharacteristic(recipient) : Amount();
                var kind = effect.MoveKind == "retreat" ? MoveKind.Retreat : MoveKind.Normal;
                State.Write($"  {name}: {recipient.Name} can move up to {distance}\".");
                _queuedPending = new PendingMove { Decider = recipient.Owner, Unit = recipient, Kind = kind, Allowance = distance };
                break;
            }
            case "setUp":
                if (_queuedPending != null || !recipient.OnBattlefield) break;
                State.Write($"  {name}: {recipient.Name} is removed and set up again.");
                _queuedPending = new PendingSetUp
                {
                    Decider = recipient.Owner, Unit = recipient, MinEnemyDistance = effect.MinEnemyDistance,
                    Anchor = effect.WithinOfUser != null ? source : null, WithinOfAnchor = effect.WithinOfUser, Source = name,
                };
                break;
            case "buff":
                if (Enum.TryParse<Stat>(effect.Stat, true, out var stat))
                {
                    var expiry = Enum.TryParse<Expiry>(effect.Expiry, true, out var e) ? e : Expiry.EndOfTurn;
                    var at = Enum.TryParse<AttackType>(effect.AttackType, true, out var t) ? t : AttackType.Any;
                    recipient.Buffs.Add(new Buff(name, stat, effect.Value, expiry, at) { Weapon = effect.Weapon, SourcePlayer = source.Owner });
                    State.Write($"  {name}: {recipient.Name} gets {stat} {effect.Value:+#;-#;0}" +
                                (effect.Weapon != null ? $" ({effect.Weapon})" : "") + $" until {Describe(expiry)}.");
                }
                break;
        }
    }

    private static string Describe(Expiry e) => e switch
    {
        Expiry.EndOfPhase => "end of phase",
        Expiry.EndOfTurn => "end of turn",
        Expiry.EndOfRound => "end of battle round",
        Expiry.UntilYourNextTurn => "your next turn",
        _ => "end of battle",
    };

    /// <summary>Resolves Auto abilities for both players at the start of a phase (e.g. end-of-turn heals).</summary>
    private void ResolveAutoAbilities()
    {
        foreach (var unit in State.Units.Where(u => u.OnBattlefield).ToList())
            foreach (var ability in ActivatedAbilities(unit).Where(a => a.Activate!.Auto).ToList())
            {
                if (State.Phase == Phase.GameOver || !unit.OnBattlefield || !AbilityAvailable(unit, ability)) continue;
                bool healOnly = ability.Activate!.Effects.All(e => e.Type is "heal" or "returnModels");
                if (ability.Activate.Target == "self" && !AutoWorthwhile(unit, ability)) continue;
                var candidates = AbilityTargets(unit, ability)
                    .Where(t => !healOnly || (t.DamagePoints > 0 && t.AliveCount > 0))
                    .OrderBy(t => healOnly ? -t.DamagePoints : t.WoundsRemaining).ToList();
                List<Unit> targets = ability.Activate.Target == "self" ? [unit] : FillTargets(unit, ability.Activate, [], candidates);
                if (targets.Count > 0) Resolve(unit, ability, targets);
            }
    }

    /// <summary>Skip auto effects that would do nothing (healing an undamaged unit, returning models to a full one).</summary>
    private static bool AutoWorthwhile(Unit unit, AbilityDef ability)
    {
        var types = ability.Activate!.Effects.Select(e => e.Type).ToList();
        if (types.All(t => t == "heal")) return unit.DamagePoints > 0 && unit.Alive.Any(m => m.Damage > 0);
        if (types.All(t => t == "returnModels")) return unit.Models.Any(m => m.Slain) && unit.AliveCount > 0;
        return true;
    }

    /// <summary>At the end of a phase, let the non-active player use their abilities for this phase. True if offered.</summary>
    private bool TryOfferAbilityWindow()
    {
        if (_abilityWindowOffered) return false;
        _abilityWindowOffered = true;
        int opp = 1 - State.ActivePlayer;
        bool any = State.LiveUnitsOf(opp).Any(u => ActivatedAbilities(u).Any(a => !a.Activate!.Auto && AbilityAvailable(u, a)));
        if (!any) return false;
        Pending = new PendingAbilityWindow { Decider = opp, Phase = State.Phase };
        return true;
    }

    private void RemoveExpiredUntilNextTurn(int player)
    {
        foreach (var u in State.Units)
            u.Buffs.RemoveAll(b => b.Expiry == Expiry.UntilYourNextTurn && b.SourcePlayer == player);
    }
}
