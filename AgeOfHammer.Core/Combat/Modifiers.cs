using AgeOfHammer.Core.Data;
using AgeOfHammer.Core.Geometry;
using AgeOfHammer.Core.State;

namespace AgeOfHammer.Core.Combat;

/// <summary>
/// Collects every modifier that applies to a unit's stat: its buffs, its own passive abilities and faction battle
/// traits, and auras from other units (friendly or enemy) that include it.
/// </summary>
public static class Modifiers
{
    /// <summary>Sum of additive modifiers to <paramref name="stat"/> for <paramref name="unit"/>.</summary>
    /// <param name="opponent">The other unit in the current attack (for "target..." conditions).</param>
    /// <param name="weapon">The weapon being used (for weapon-specific modifiers).</param>
    public static int Get(GameState state, Unit unit, Stat stat, AttackType type, Unit? opponent = null, WeaponProfile? weapon = null)
    {
        int total = unit.BuffTotal(stat, type, weapon?.Name);
        foreach (var (m, _) in Applicable(state, unit, stat, type, opponent, weapon))
            if (m.Multiply is null) total += m.Value;
        return total;
    }

    /// <summary>Product of multipliers ("double the Damage characteristic"), 1 if none.</summary>
    public static int Multiplier(GameState state, Unit unit, Stat stat, AttackType type, Unit? opponent = null, WeaponProfile? weapon = null)
    {
        int product = 1;
        foreach (var (m, _) in Applicable(state, unit, stat, type, opponent, weapon))
            if (m.Multiply is int k) product *= k;
        return product;
    }

    /// <summary>For "best of" stats such as Ward (5 = 5+): the lowest value from buffs and modifiers, or null.</summary>
    public static int? Lowest(GameState state, Unit unit, Stat stat, Unit? opponent = null)
    {
        int? best = null;
        foreach (var b in unit.Buffs.Where(b => b.Stat == stat)) best = best is null ? b.Value : Math.Min(best.Value, b.Value);
        foreach (var (m, _) in Applicable(state, unit, stat, AttackType.Any, opponent, null))
            best = best is null ? m.Value : Math.Min(best.Value, m.Value);
        return best;
    }

    public static bool HasFlag(GameState state, Unit unit, Stat stat) =>
        unit.Buffs.Any(b => b.Stat == stat && b.Value > 0) || Applicable(state, unit, stat, AttackType.Any, null, null).Any(x => x.M.Value > 0);

    /// <summary>Abilities that belong to a unit: its warscroll's plus its faction's battle traits.</summary>
    public static IEnumerable<AbilityDef> AbilitiesOf(GameState state, Unit unit)
    {
        foreach (var a in unit.Warscroll.Abilities) yield return a;
        if (state.Players.Length > unit.Owner)
            foreach (var a in state.Players[unit.Owner].Faction.BattleTraits) yield return a;
    }

    private static IEnumerable<(PassiveModifierDef M, Unit Source)> Applicable(
        GameState state, Unit unit, Stat stat, AttackType type, Unit? opponent, WeaponProfile? weapon)
    {
        // The unit's own (non-aura) modifiers.
        foreach (var ability in AbilitiesOf(state, unit))
            foreach (var m in ability.Modifiers)
                if (m.Aura == null && Matches(m, stat, type, weapon) && ConditionHolds(state, m.Condition, unit, unit, opponent))
                    yield return (m, unit);

        // Auras from units on the battlefield (including this unit's own auras).
        foreach (var source in state.Units)
        {
            if (!source.OnBattlefield) continue;
            foreach (var ability in source.Warscroll.Abilities)
                foreach (var m in ability.Modifiers)
                    if (m.Aura != null && Matches(m, stat, type, weapon) && InAura(state, source, m.Aura, unit)
                        && ConditionHolds(state, m.Condition, source, unit, opponent))
                        yield return (m, source);
        }
    }

    private static bool Matches(PassiveModifierDef m, Stat stat, AttackType type, WeaponProfile? weapon)
    {
        if (!Enum.TryParse<Stat>(m.Stat, true, out var s) || s != stat) return false;
        var at = Enum.TryParse<AttackType>(m.AttackType, true, out var parsed) ? parsed : AttackType.Any;
        if (at != AttackType.Any && type != AttackType.Any && at != type) return false;
        if (m.Weapon != null && (weapon == null || !weapon.Name.Contains(m.Weapon, StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

    /// <summary>Is <paramref name="unit"/> affected by an aura from <paramref name="source"/>?</summary>
    public static bool InAura(GameState state, Unit source, AuraDef aura, Unit unit)
    {
        if (!unit.OnBattlefield) return false;
        bool friendly = aura.Side.Equals("friendly", StringComparison.OrdinalIgnoreCase);
        if (friendly != (unit.Owner == source.Owner)) return false;
        if (unit == source && !aura.IncludeSelf) return false;
        if (aura.Keywords.Any(k => !unit.Warscroll.HasKeyword(k))) return false;
        if (aura.KeywordsAny.Count > 0 && !aura.KeywordsAny.Any(unit.Warscroll.HasKeyword)) return false;
        if (aura.Names.Count > 0 && !aura.Names.Any(n => unit.Name.Equals(n, StringComparison.OrdinalIgnoreCase))) return false;
        if (aura.Range is not float range || unit == source) return true;
        return aura.Wholly ? WhollyWithin(unit, source, range) : GameState.Distance(unit, source) <= range + Geo.Epsilon;
    }

    /// <summary>Every model of <paramref name="unit"/> is entirely within <paramref name="range"/> of <paramref name="of"/>.</summary>
    public static bool WhollyWithin(Unit unit, Unit of, float range)
    {
        foreach (var m in unit.Alive)
        {
            float best = float.MaxValue;
            foreach (var o in of.Alive)
                best = MathF.Min(best, System.Numerics.Vector2.Distance(m.Position, o.Position) - of.BaseRadius + unit.BaseRadius);
            if (best > range + Geo.Epsilon) return false;
        }
        return true;
    }

    /// <summary>
    /// Evaluates condition terms joined with '&amp;'. Terms (self = the unit with the ability, affected = the unit
    /// being modified, target = the other unit in the attack or ability):
    /// charged, notCharged, chargedThisPhase, damaged, damagePoints&gt;=N, damagePoints&lt;N, inCombat, notInCombat,
    /// notMoved, engagedWithCharger (in combat with an enemy that charged this turn), targetWithin:N,
    /// contesting, contestingFriendlyTerritory, keyword:X, keywordAny:A|B,
    /// targetModels&gt;=N, targetKeyword:X, targetHealth&gt;=N, targetCharged, targetDamaged, targetNotFly.
    /// Unknown terms are false, so a typo never silently applies a bonus.
    /// </summary>
    public static bool ConditionHolds(GameState state, string? condition, Unit self, Unit affected, Unit? target)
    {
        if (string.IsNullOrWhiteSpace(condition)) return true;
        foreach (var raw in condition.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!Term(state, raw, self, affected, target)) return false;
        return true;
    }

    private static bool Term(GameState state, string term, Unit self, Unit affected, Unit? target)
    {
        static int N(string t, string prefix) => int.Parse(t[prefix.Length..]);
        switch (term)
        {
            case "charged": return self.Charged;
            case "notCharged": return !self.Charged;
            case "chargedThisPhase": return self.Charged && state.Phase == Phase.Charge;
            case "damaged": return self.DamagePoints > 0;
            case "inCombat": return state.InCombat(self);
            case "notInCombat": return !state.InCombat(self);
            case "contesting": return Contesting(state, self, friendlyTerritoryOnly: false);
            case "contestingFriendlyTerritory": return Contesting(state, self, friendlyTerritoryOnly: true);
            case "targetCharged": return target?.Charged == true;
            case "targetDamaged": return target != null && target.DamagePoints > 0;
            case "targetNotFly": return target != null && !target.Warscroll.CanFly;
            case "notMoved": return !self.Moved;
            case "engagedWithCharger": return state.EnemiesInCombatWith(self).Any(e => e.Charged);
        }
        if (term.StartsWith("damagePoints>=")) return self.DamagePoints >= N(term, "damagePoints>=");
        if (term.StartsWith("damagePoints<")) return self.DamagePoints < N(term, "damagePoints<");
        if (term.StartsWith("keyword:")) return affected.Warscroll.HasKeyword(term["keyword:".Length..]);
        if (term.StartsWith("keywordAny:")) return term["keywordAny:".Length..].Split('|').Any(affected.Warscroll.HasKeyword);
        if (term.StartsWith("targetModels>=")) return target != null && target.AliveCount >= N(term, "targetModels>=");
        if (term.StartsWith("targetHealth>=")) return target != null && target.Warscroll.Health >= N(term, "targetHealth>=");
        if (term.StartsWith("targetKeyword:")) return target != null && target.Warscroll.HasKeyword(term["targetKeyword:".Length..]);
        if (term.StartsWith("targetWithin:")) return target != null && GameState.Distance(target, self) <= N(term, "targetWithin:") + Geo.Epsilon;
        return false;
    }

    private static bool Contesting(GameState state, Unit unit, bool friendlyTerritoryOnly)
    {
        var territory = state.Battleplan.Territories.Count > unit.Owner ? state.TerritoryOf(unit.Owner) : default;
        foreach (var o in state.Objectives)
        {
            if (friendlyTerritoryOnly && !territory.ContainsCircle(o.Position, Objective.MarkerRadius)) continue;
            if (unit.Alive.Any(m => Geo.Distance(m.Position, unit.BaseRadius, o.Position, Objective.MarkerRadius) <= Objective.ControlRange + Geo.Epsilon))
                return true;
        }
        return false;
    }
}
