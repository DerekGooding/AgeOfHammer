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
    /// <summary>Mortal damage the attacker suffered from the target's abilities (e.g. saves of 6).</summary>
    public int Reflected { get; set; }

    public string Summary()
    {
        var sb = new StringBuilder();
        sb.Append($"{Attacker.Name} attacks {Target.Name}: ");
        sb.Append(string.Join("; ", Lines));
        if (WardSaved > 0) sb.Append($". Ward saved {WardSaved}");
        sb.Append($". {DamageInflicted} damage, {ModelsSlain} slain");
        if (Target.IsDestroyed) sb.Append($" - {Target.Name} DESTROYED");
        if (Reflected > 0) sb.Append($". {Attacker.Name} suffers {Reflected} mortal damage in return" + (Attacker.IsDestroyed ? $" - {Attacker.Name} DESTROYED" : ""));
        return sb.ToString();
    }
}

/// <summary>Weapons in an activation: each weapon with the number of models attacking with it.</summary>
public readonly record struct WeaponUse(WeaponProfile Weapon, int Models);

public static class AttackResolver
{
    /// <summary>Hit modifier: the attacker's own modifiers (not for Companion weapons) plus modifiers on the target
    /// against attacks that target it, capped at +/-1 in total.</summary>
    public static int HitModifier(GameState s, Unit attacker, Unit target, WeaponProfile w, AttackOptions o)
    {
        int own = w.Companion ? 0 : Modifiers.Get(s, attacker, Stat.Hit, w.Type, target, w) + o.ExtraHit;
        return Math.Clamp(own + Modifiers.Get(s, target, Stat.HitAgainst, w.Type, attacker, w), -1, 1);
    }

    public static int WoundModifier(GameState s, Unit attacker, Unit target, WeaponProfile w)
    {
        int own = w.Companion ? 0 : Modifiers.Get(s, attacker, Stat.Wound, w.Type, target, w);
        return Math.Clamp(own + Modifiers.Get(s, target, Stat.WoundAgainst, w.Type, attacker, w), -1, 1);
    }

    public static int AttacksBonus(GameState s, Unit attacker, Unit target, WeaponProfile w) =>
        w.Companion ? 0 : Modifiers.Get(s, attacker, Stat.Attacks, w.Type, target, w);

    public static int DamageBonus(GameState s, Unit attacker, Unit target, WeaponProfile w) =>
        (w.Companion ? 0 : Modifiers.Get(s, attacker, Stat.Damage, w.Type, target, w)) + (w.ChargeBonusDamage && attacker.Charged ? 1 : 0);

    public static int DamageMultiplier(GameState s, Unit attacker, Unit target, WeaponProfile w) =>
        w.Companion ? 1 : Modifiers.Multiplier(s, attacker, Stat.Damage, w.Type, target, w);

    private static int RollDamage(IDiceRoller dice, WeaponProfile w, int bonus, int multiplier) =>
        Math.Max(0, (w.DamageExpr.Roll(dice) + bonus) * multiplier);

    public static int EffectiveRend(GameState s, Unit attacker, Unit target, WeaponProfile w)
    {
        int rend = w.Rend + (w.Companion ? 0 : Modifiers.Get(s, attacker, Stat.Rend, w.Type, target, w));
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

    /// <summary>The best ward the target has: its own, or from a buff or aura (e.g. "WARD (6+) while wholly within 13"").</summary>
    public static int? EffectiveWard(GameState s, Unit target)
    {
        int? ward = target.Warscroll.Ward;
        if (Modifiers.Lowest(s, target, Stat.Ward) is int other) ward = ward is null ? other : Math.Min(ward.Value, other);
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
        int saveSixes = 0;
        bool anyMelee = false;

        foreach (var (w, models) in weapons)
        {
            if (models <= 0) continue;
            var line = new WeaponAttackLine { Weapon = w.Name, Models = models };
            int hitMod = HitModifier(s, attacker, target, w, options);
            int woundMod = WoundModifier(s, attacker, target, w);
            int rend = EffectiveRend(s, attacker, target, w);
            int saveMod = SaveModifier(s, target, attacker, w.Type, options);
            int extraAttacks = AttacksBonus(s, attacker, target, w);
            int extraDamage = DamageBonus(s, attacker, target, w);
            int damageMultiplier = DamageMultiplier(s, attacker, target, w);
            anyMelee |= w.IsMelee;
            // Weapon/attacker triggers (none apply to Companion weapons' bonuses except where scripted per weapon).
            string? hitMortal = Modifiers.Amount(s, attacker, Stat.HitMortal, w.Type, target, w);
            string? critHits = Modifiers.Amount(s, attacker, Stat.CritHits, w.Type, target, w);
            bool hitsAreMortal = Modifiers.Find(s, attacker, Stat.HitsAreMortal, w.Type, target, w).Count > 0;

            for (int m = 0; m < models; m++)
                line.Attacks += Math.Max(0, w.AttacksExpr.Roll(dice) + extraAttacks);

            int normalHits = 0, autoWounds = 0;
            int failFloor = Modifiers.Get(s, target, Stat.UnmodifiedHitFailsAgainst, w.Type, attacker, w);
            for (int a = 0; a < line.Attacks; a++)
            {
                int roll = dice.D6();
                if (roll <= failFloor || !HitSucceeds(roll, w.Hit, hitMod)) continue;
                bool crit = roll == 6;
                line.Hits++;
                if (hitMortal != null) line.MortalDamage += DiceExpression.Parse(hitMortal).Roll(dice);
                if (hitsAreMortal)
                {
                    line.MortalDamage += 1;
                    if (crit) line.Crits++;
                    continue;
                }
                if (crit && critHits != null)
                {
                    int extra = Math.Max(1, DiceExpression.Parse(critHits).Roll(dice));
                    line.Crits++;
                    line.Hits += extra - 1;
                    normalHits += extra;
                    continue;
                }
                if (crit)
                {
                    line.Crits++;
                    switch (w.Crit)
                    {
                        case CritEffect.TwoHits: line.Hits++; normalHits += 2; continue;
                        case CritEffect.AutoWound: autoWounds++; continue;
                        case CritEffect.Mortal:
                            line.MortalDamage += RollDamage(dice, w, extraDamage, damageMultiplier);
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
                int saveRoll = dice.D6();
                if (saveRoll == 6 && target.Warscroll.Save < 7) saveSixes++;
                if (SaveSucceeds(saveRoll, target.Warscroll.Save, saveMod, rend)) continue;
                line.FailedSaves++;
                line.Damage += RollDamage(dice, w, extraDamage, damageMultiplier);
            }

            pool += line.Damage + line.MortalDamage;
            result.Lines.Add(line);
        }

        result.TotalDamage = pool;
        var (inflicted, warded, slain) = ApplyDamage(s, target, pool);
        result.WardSaved = warded;
        result.DamageInflicted = inflicted;
        result.ModelsSlain = slain;

        // The target's retaliation triggers, resolved after the attacks.
        int reflect = 0;
        if (saveSixes > 0)
        {
            var type = anyMelee ? AttackType.Melee : AttackType.Ranged;
            int perSix = Modifiers.Find(s, target, Stat.SaveSixMortal, type, attacker).Sum(x => x.Modifier.Value);
            reflect += saveSixes * perSix;
        }
        if (anyMelee && slain > 0 && Modifiers.Find(s, target, Stat.SlainMortalOn, AttackType.Melee, attacker).Select(x => x.Modifier.Value).DefaultIfEmpty(0).Max() is int on and > 0)
            for (int i = 0; i < slain; i++) if (dice.D6() >= on) reflect++;
        if (reflect > 0 && attacker.OnBattlefield)
            result.Reflected = ApplyDamage(s, attacker, reflect).Inflicted;
        return result;
    }

    /// <summary>Rolls ward saves then allocates damage model-by-model (damaged models first, excess carries over).</summary>
    public static (int Inflicted, int Warded, int Slain) ApplyDamage(GameState s, Unit target, int damage)
    {
        int warded = 0;
        var ward = EffectiveWard(s, target);
        if (ward is int wv)
        {
            int mod = Modifiers.Get(s, target, Stat.WardRoll, AttackType.Any);
            for (int i = 0; i < damage; i++)
                if (s.Dice.D6() + mod >= wv) warded++;
        }
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
        double attacks = models * Math.Max(0, w.AttacksExpr.Average + AttacksBonus(s, attacker, target, w));
        double dmg = Math.Max(0, (w.DamageExpr.Average + DamageBonus(s, attacker, target, w)) * DamageMultiplier(s, attacker, target, w));
        int failFloor = Modifiers.Get(s, target, Stat.UnmodifiedHitFailsAgainst, w.Type, attacker, w);
        double pCrit = failFloor >= 6 ? 0 : 1 / 6.0;
        double pNormalHit = P(r => r != 6 && r > failFloor && HitSucceeds(r, w.Hit, hitMod));
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
        int wardMod = Modifiers.Get(s, target, Stat.WardRoll, AttackType.Any);
        double wardFactor = ward is int wv ? 1 - P(r => r + wardMod >= wv) : 1;
        return attacks * perAttack * wardFactor;
    }
}
