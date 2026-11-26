using System.Text;
using AgeOfHammer.Core.Data;
using AgeOfHammer.Core.Dice;
using AgeOfHammer.Core.State;

namespace AgeOfHammer.Core.Combat;

/// <summary>Situational modifiers for one attack activation (commands, reactions).</summary>
public sealed record AttackOptions
{
    /// <summary>Extra hit modifier, e.g. +1 for All-out Attack, -1 for Covering Fire.</summary>
    public int ExtraHit { get; init; }
    /// <summary>Extra save modifier for the target, e.g. +1 for All-out Defence.</summary>
    public int ExtraSave { get; init; }
}

public sealed class WeaponAttackLine
{
    public required string Weapon { get; init; }
    public int Models { get; init; }
    public int Attacks { get; set; }
    public int Hits { get; set; }
    public int Crits { get; set; }
    public int Wounds { get; set; }
    public int FailedSaves { get; set; }
    public int Damage { get; set; }
    public int MortalDamage { get; set; }
    public override string ToString() =>
        $"{Weapon} x{Models}: {Attacks} atk, {Hits} hit ({Crits} crit), {Wounds} wnd, {FailedSaves} unsaved -> {Damage} dmg" +
        (MortalDamage > 0 ? $" + {MortalDamage} mortal" : "");
}

public sealed class AttackResult
{
    public required Unit Attacker { get; init; }
    public required Unit Target { get; init; }
    public List<WeaponAttackLine> Lines { get; } = new();
    public int TotalDamage { get; set; }
    public int WardSaved { get; set; }
    public int DamageInflicted { get; set; }
    public int ModelsSlain { get; set; }

    public string Summary()
    {
        var sb = new StringBuilder();
        sb.Append($"{Attacker.Name} attacks {Target.Name}: ");
        sb.Append(string.Join("; ", Lines));
        if (WardSaved > 0) sb.Append($". Ward saved {WardSaved}");
        sb.Append($". {DamageInflicted} damage, {ModelsSlain} slain");
        if (Target.IsDestroyed) sb.Append($" - {Target.Name} DESTROYED");
        return sb.ToString();
    }
}

/// <summary>Collects every modifier that applies to a stat for a unit (buffs + data-driven passive abilities).</summary>
public static class Modifiers
{
    public static int Get(GameState state, Unit unit, Stat stat, AttackType type, Unit? opponent = null)
    {
        int total = unit.BuffTotal(stat, type);
        foreach (var ability in AbilitiesOf(state, unit))
            foreach (var m in ability.Modifiers)
            {
                if (!Enum.TryParse<Stat>(m.Stat, true, out var s) || s != stat) continue;
                var at = Enum.TryParse<AttackType>(m.AttackType, true, out var parsed) ? parsed : AttackType.Any;
                if (at != AttackType.Any && type != AttackType.Any && at != type) continue;
                if (!ConditionHolds(m.Condition, unit, opponent)) continue;
                total += m.Value;
            }
        return total;
    }

    public static bool HasFlag(GameState state, Unit unit, Stat stat) =>
        unit.HasBuff(stat) || Get(state, unit, stat, AttackType.Any) > 0;

    public static IEnumerable<AbilityDef> AbilitiesOf(GameState state, Unit unit)
    {
        foreach (var a in unit.Warscroll.Abilities) yield return a;
        if (state.Players.Length > unit.Owner)
            foreach (var a in state.Players[unit.Owner].Faction.BattleTraits) yield return a;
    }

    private static bool ConditionHolds(string? condition, Unit unit, Unit? opponent)
    {
        if (string.IsNullOrWhiteSpace(condition)) return true;
        var c = condition.Trim();
        if (c.Equals("charged", StringComparison.OrdinalIgnoreCase)) return unit.Charged;
        if (c.Equals("targetHealth5Plus", StringComparison.OrdinalIgnoreCase)) return opponent != null && opponent.Warscroll.Health >= 5;
        if (c.StartsWith("targetKeyword:", StringComparison.OrdinalIgnoreCase))
            return opponent != null && opponent.Warscroll.HasKeyword(c["targetKeyword:".Length..]);
        return false; // unknown conditions never apply
    }
}

/// <summary>Weapons in an activation: each weapon with the number of models attacking with it.</summary>
public readonly record struct WeaponUse(WeaponProfile Weapon, int Models);

public static class AttackResolver
{
    public static int HitModifier(GameState s, Unit attacker, Unit target, WeaponProfile w, AttackOptions o) =>
        w.Companion ? 0 : Math.Clamp(Modifiers.Get(s, attacker, Stat.Hit, w.Type, target) + o.ExtraHit, -1, 1);

    public static int WoundModifier(GameState s, Unit attacker, Unit target, WeaponProfile w) =>
        w.Companion ? 0 : Math.Clamp(Modifiers.Get(s, attacker, Stat.Wound, w.Type, target), -1, 1);

    public static int EffectiveRend(GameState s, Unit attacker, Unit target, WeaponProfile w)
    {
        int rend = w.Rend + (w.Companion ? 0 : Modifiers.Get(s, attacker, Stat.Rend, w.Type, target));
        foreach (var (kw, bonus) in w.AntiAbilities)
        {
            bool applies = kw.Equals("Charge", StringComparison.OrdinalIgnoreCase) ? target.Charged : target.Warscroll.HasKeyword(kw);
            if (applies) rend += bonus;
        }
        return Math.Max(0, rend);
    }

    /// <summary>Total modifier to the target's save rolls (positive capped at +1), before rend.</summary>
    public static int SaveModifier(GameState s, Unit target, Unit attacker, AttackType type, AttackOptions o) =>
        Math.Min(1, Modifiers.Get(s, target, Stat.Save, type, attacker) + o.ExtraSave);

    public static int? EffectiveWard(GameState s, Unit target)
    {
        int? ward = target.Warscroll.Ward;
        foreach (var b in target.Buffs.Where(b => b.Stat == Stat.Ward))
            ward = ward is null ? b.Value : Math.Min(ward.Value, b.Value);
        return ward is > 0 and < 7 ? ward : null;
    }

    public static bool HitSucceeds(int roll, int hit, int mod) => roll != 1 && (roll == 6 || roll + mod >= hit);
    public static bool WoundSucceeds(int roll, int wound, int mod) => roll != 1 && (roll == 6 || roll + mod >= wound);
    public static bool SaveSucceeds(int roll, int save, int mod, int rend) => save < 7 && roll != 1 && roll + mod - rend >= save;

    /// <summary>Resolves a full attack activation and applies damage to the target.</summary>
    public static AttackResult Resolve(GameState s, Unit attacker, Unit target, IReadOnlyList<WeaponUse> weapons, AttackOptions options)
    {
        var dice = s.Dice;
        var result = new AttackResult { Attacker = attacker, Target = target };
        int pool = 0;

        foreach (var (w, models) in weapons)
        {
            if (models <= 0) continue;
            var line = new WeaponAttackLine { Weapon = w.Name, Models = models };
            int hitMod = HitModifier(s, attacker, target, w, options);
            int woundMod = WoundModifier(s, attacker, target, w);
            int rend = EffectiveRend(s, attacker, target, w);
            int saveMod = SaveModifier(s, target, attacker, w.Type, options);
            int extraAttacks = w.Companion ? 0 : Modifiers.Get(s, attacker, Stat.Attacks, w.Type, target);
            int extraDamage = (w.Companion ? 0 : Modifiers.Get(s, attacker, Stat.Damage, w.Type, target))
                              + (w.ChargeBonusDamage && attacker.Charged ? 1 : 0);

            for (int m = 0; m < models; m++)
                line.Attacks += Math.Max(0, w.AttacksExpr.Roll(dice) + extraAttacks);

            int normalHits = 0, autoWounds = 0;
            for (int a = 0; a < line.Attacks; a++)
            {
                int roll = dice.D6();
                if (!HitSucceeds(roll, w.Hit, hitMod)) continue;
                bool crit = roll == 6;
                line.Hits++;
                if (crit)
                {
                    line.Crits++;
                    switch (w.Crit)
                    {
                        case CritEffect.TwoHits: line.Hits++; normalHits += 2; continue;
                        case CritEffect.AutoWound: autoWounds++; continue;
                        case CritEffect.Mortal:
                            int md = Math.Max(0, w.DamageExpr.Roll(dice) + extraDamage);
                            line.MortalDamage += md;
                            continue;
                    }
                }
                normalHits++;
            }

            line.Wounds = autoWounds;
            for (int h = 0; h < normalHits; h++)
                if (WoundSucceeds(dice.D6(), w.Wound, woundMod)) line.Wounds++;

            for (int wd = 0; wd < line.Wounds; wd++)
            {
                if (SaveSucceeds(dice.D6(), target.Warscroll.Save, saveMod, rend)) continue;
                line.FailedSaves++;
                line.Damage += Math.Max(0, w.DamageExpr.Roll(dice) + extraDamage);
            }

            pool += line.Damage + line.MortalDamage;
            result.Lines.Add(line);
        }

        result.TotalDamage = pool;
        var (inflicted, warded, slain) = ApplyDamage(s, target, pool);
        result.WardSaved = warded;
        result.DamageInflicted = inflicted;
        result.ModelsSlain = slain;
        return result;
    }

    /// <summary>Rolls ward saves then allocates damage model-by-model (damaged models first, excess carries over).</summary>
    public static (int Inflicted, int Warded, int Slain) ApplyDamage(GameState s, Unit target, int damage)
    {
        int warded = 0;
        var ward = EffectiveWard(s, target);
        if (ward is int wv)
            for (int i = 0; i < damage; i++)
                if (s.Dice.D6() >= wv) warded++;
        int remaining = damage - warded;
        int inflicted = 0, slain = 0;
        int health = target.Warscroll.Health;
        while (remaining > 0)
        {
            var model = target.Alive.OrderByDescending(m => m.Damage).FirstOrDefault();
            if (model is null) break;
            int take = Math.Min(remaining, health - model.Damage);
            model.Damage += take;
            remaining -= take;
            inflicted += take;
            if (model.Damage >= health)
            {
                model.Slain = true;
                slain++;
            }
        }
        return (inflicted, warded, slain);
    }

    /// <summary>Inflicts mortal damage (no hit/wound/save, ward still applies) and logs it.</summary>
    public static int InflictMortal(GameState s, Unit target, int amount, string source)
    {
        var (inflicted, warded, slain) = ApplyDamage(s, target, amount);
        s.Write($"{source}: {amount} mortal damage to {target.Name}" + (warded > 0 ? $" ({warded} warded)" : "") +
                $", {slain} slain" + (target.IsDestroyed ? $" - {target.Name} DESTROYED" : ""));
        return inflicted;
    }

    // ---- Expected value maths (used by the AI and UI previews) ----

    private static double P(Func<int, bool> success)
    {
        int n = 0;
        for (int r = 1; r <= 6; r++) if (success(r)) n++;
        return n / 6.0;
    }

    public static double ExpectedDamage(GameState s, Unit attacker, Unit target, WeaponProfile w, int models, AttackOptions? options = null)
    {
        options ??= new AttackOptions();
        int hitMod = HitModifier(s, attacker, target, w, options);
        int woundMod = WoundModifier(s, attacker, target, w);
        int rend = EffectiveRend(s, attacker, target, w);
        int saveMod = SaveModifier(s, target, attacker, w.Type, options);
        double attacks = models * Math.Max(0, w.AttacksExpr.Average + (w.Companion ? 0 : Modifiers.Get(s, attacker, Stat.Attacks, w.Type, target)));
        double dmg = Math.Max(0, w.DamageExpr.Average + (w.Companion ? 0 : Modifiers.Get(s, attacker, Stat.Damage, w.Type, target))
                                 + (w.ChargeBonusDamage && attacker.Charged ? 1 : 0));
        double pCrit = 1 / 6.0;
        double pNormalHit = P(r => r != 6 && HitSucceeds(r, w.Hit, hitMod));
        double pWound = P(r => WoundSucceeds(r, w.Wound, woundMod));
        double pUnsaved = 1 - P(r => SaveSucceeds(r, target.Warscroll.Save, saveMod, rend));
        double perAttack = w.Crit switch
        {
            CritEffect.TwoHits => (pNormalHit + 2 * pCrit) * pWound * pUnsaved * dmg,
            CritEffect.AutoWound => (pNormalHit * pWound + pCrit) * pUnsaved * dmg,
            CritEffect.Mortal => pNormalHit * pWound * pUnsaved * dmg + pCrit * dmg,
            _ => (pNormalHit + pCrit) * pWound * pUnsaved * dmg,
        };
        var ward = EffectiveWard(s, target);
        double wardFactor = ward is int wv ? 1 - (7 - wv) / 6.0 : 1;
        return attacks * perAttack * wardFactor;
    }
}
