using System.Numerics;
using AgeOfHammer.Core.Combat;
using AgeOfHammer.Core.Data;
using AgeOfHammer.Core.Dice;
using AgeOfHammer.Core.State;

namespace AgeOfHammer.Core.Lab;

/// <summary>
/// Exact probability distributions for attacks: P(total damage = d) for an attack activation, including hit/wound/save
/// modifiers, rend, crits (2 hits, auto-wound, mortal), damage dice, and ward saves. Uses the same modifiers as the
/// engine (<see cref="AttackResolver"/>), so passive abilities and conditions are taken into account.
/// </summary>
public static class CombatMath
{
    /// <summary>Largest damage value tracked; anything above is folded into the last bucket.</summary>
    public const int Cap = 200;

    /// <summary>Distribution of total damage points (after ward saves) from an activation.</summary>
    public static double[] Activation(GameState s, Unit attacker, Unit target, IEnumerable<WeaponUse> weapons, AttackOptions? options = null)
    {
        options ??= new AttackOptions();
        double[] total = [1.0];
        foreach (var (w, models) in weapons)
            if (models > 0) total = Convolve(total, Weapon(s, attacker, target, w, models, options));
        var ward = AttackResolver.EffectiveWard(s, target);
        if (ward is int wv)
        {
            int mod = Modifiers.Get(s, target, Stat.WardRoll, AttackType.Any);
            double pSave = P(r => r + mod >= wv);
            total = Thin(total, 1 - pSave);
        }
        return total;
    }

    /// <summary>Damage distribution (before ward) of one weapon used by <paramref name="models"/> models.</summary>
    public static double[] Weapon(GameState s, Unit attacker, Unit target, WeaponProfile w, int models, AttackOptions options)
    {
        int hitMod = AttackResolver.HitModifier(s, attacker, target, w, options);
        int woundMod = AttackResolver.WoundModifier(s, attacker, target, w);
        int rend = AttackResolver.EffectiveRend(s, attacker, target, w);
        int saveMod = AttackResolver.SaveModifier(s, target, attacker, w.Type, options);
        int failFloor = Modifiers.Get(s, target, Stat.UnmodifiedHitFailsAgainst, w.Type, attacker, w);
        int dmgBonus = AttackResolver.DamageBonus(s, attacker, target, w);
        int dmgMult = AttackResolver.DamageMultiplier(s, attacker, target, w);
        int atkBonus = AttackResolver.AttacksBonus(s, attacker, target, w);

        double pCrit = failFloor >= 6 ? 0 : 1 / 6.0;
        double pNormal = P(r => r != 6 && r > failFloor && AttackResolver.HitSucceeds(r, w.Hit, hitMod));
        double pWound = P(r => AttackResolver.WoundSucceeds(r, w.Wound, woundMod));
        double pUnsaved = 1 - P(r => AttackResolver.SaveSucceeds(r, target.Warscroll.Save, saveMod, rend));

        var damage = Shift(Dice(w.DamageExpr), dmgBonus, dmgMult);
        double[] afterSave = Mix(1 - pUnsaved, pUnsaved, damage);         // a wound -> damage
        double[] afterWound = Mix(1 - pWound, pWound, afterSave);           // a hit -> damage
        double[] crit = w.Crit switch
        {
            CritEffect.TwoHits => Convolve(afterWound, afterWound),
            CritEffect.AutoWound => afterSave,
            CritEffect.Mortal => damage,
            _ => afterWound,
        };
        // One attack: miss, normal hit, or critical hit.
        var perAttack = new double[Math.Max(afterWound.Length, crit.Length)];
        perAttack[0] += 1 - pNormal - pCrit;
        for (int i = 0; i < afterWound.Length; i++) perAttack[i] += pNormal * afterWound[i];
        for (int i = 0; i < crit.Length; i++) perAttack[i] += pCrit * crit[i];

        // Number of attacks: each model rolls the Attacks characteristic (+ bonus).
        var attacksPerModel = Shift(Dice(w.AttacksExpr), atkBonus, 1);
        double[] attackCount = [1.0];
        for (int m = 0; m < models; m++) attackCount = Convolve(attackCount, attacksPerModel);

        var result = new double[1];
        double[] power = [1.0]; // distribution of damage from n attacks
        for (int n = 0; n < attackCount.Length; n++)
        {
            if (attackCount[n] > 1e-12) result = AddScaled(result, power, attackCount[n]);
            if (n < attackCount.Length - 1) power = Convolve(power, perAttack);
        }
        return result;
    }

    public static double Mean(double[] dist)
    {
        double m = 0;
        for (int i = 0; i < dist.Length; i++) m += i * dist[i];
        return m;
    }

    /// <summary>Probability that total damage is at least <paramref name="k"/>.</summary>
    public static double AtLeast(double[] dist, int k)
    {
        double p = 0;
        for (int i = Math.Max(0, k); i < dist.Length; i++) p += dist[i];
        return p;
    }

    // ---------------------------------------------------------------- distribution helpers

    private static double P(Func<int, bool> success)
    {
        int n = 0;
        for (int r = 1; r <= 6; r++) if (success(r)) n++;
        return n / 6.0;
    }

    /// <summary>Distribution of a dice expression such as "2D6+1" (clamped at 0).</summary>
    public static double[] Dice(DiceExpression e)
    {
        double[] d = [1.0];
        if (!e.IsFixed)
        {
            var one = new double[e.Sides + 1];
            for (int f = 1; f <= e.Sides; f++) one[f] = 1.0 / e.Sides;
            for (int i = 0; i < e.Count; i++) d = Convolve(d, one);
        }
        return Shift(d, e.Modifier, 1);
    }

    /// <summary>(X + add) * mult, clamped at 0.</summary>
    private static double[] Shift(double[] d, int add, int mult)
    {
        int max = Math.Min(Cap, Math.Max(0, (d.Length - 1 + add) * mult));
        var r = new double[max + 1];
        for (int i = 0; i < d.Length; i++)
            r[Math.Clamp((i + add) * mult, 0, max)] += d[i];
        return r;
    }

    /// <summary>With probability <paramref name="p0"/> the value is 0, otherwise it follows <paramref name="d"/>.</summary>
    private static double[] Mix(double p0, double pd, double[] d)
    {
        var r = new double[Math.Max(1, d.Length)];
        r[0] += p0;
        for (int i = 0; i < d.Length; i++) r[i] += pd * d[i];
        return r;
    }

    public static double[] Convolve(double[] a, double[] b)
    {
        int len = Math.Min(Cap + 1, a.Length + b.Length - 1);
        var r = new double[len];
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == 0) continue;
            for (int j = 0; j < b.Length; j++)
                r[Math.Min(len - 1, i + j)] += a[i] * b[j];
        }
        return r;
    }

    private static double[] AddScaled(double[] acc, double[] d, double weight)
    {
        var r = new double[Math.Max(acc.Length, d.Length)];
        acc.CopyTo(r, 0);
        for (int i = 0; i < d.Length; i++) r[i] += weight * d[i];
        return r;
    }

    /// <summary>Each damage point independently survives with probability <paramref name="keep"/> (ward saves).</summary>
    private static double[] Thin(double[] d, double keep)
    {
        var r = new double[d.Length];
        for (int n = 0; n < d.Length; n++)
        {
            if (d[n] == 0) continue;
            double c = 1; // C(n, k)
            for (int k = 0; k <= n; k++)
            {
                r[k] += d[n] * c * Math.Pow(keep, k) * Math.Pow(1 - keep, n - k);
                c = c * (n - k) / (k + 1);
            }
        }
        return r;
    }
}

/// <summary>Reference opponents used to compare units on equal terms.</summary>
public static class References
{
    public sealed record Target(string Name, Warscroll Warscroll);
    public sealed record Attacker(string Name, WeaponProfile Weapon);

    public static readonly IReadOnlyList<Target> Targets =
    [
        new("Horde (20 x 1W, 5+)", Make("ref-horde", 20, 1, 5, 25, null, "INFANTRY")),
        new("Elite infantry (5 x 2W, 3+)", Make("ref-elite", 5, 2, 3, 40, null, "INFANTRY")),
        new("Monster (14W, 4+)", Make("ref-monster", 1, 14, 4, 100, null, "MONSTER")),
        new("Warded hero (6W, 3+, ward 5+)", Make("ref-hero", 1, 6, 3, 40, 5, "HERO", "INFANTRY")),
    ];

    public static readonly IReadOnlyList<Attacker> Attackers =
    [
        new("Basic attack (4+/4+, -, 1)", new WeaponProfile { Name = "Basic", Attacks = "1", Hit = 4, Wound = 4, Rend = 0, Damage = "1" }),
        new("Elite attack (3+/3+, 1, 2)", new WeaponProfile { Name = "Elite", Attacks = "1", Hit = 3, Wound = 3, Rend = 1, Damage = "2" }),
    ];

    private static Warscroll Make(string id, int size, int health, int save, int baseMm, int? ward, params string[] keywords) =>
        new()
        {
            Id = id, Name = id, UnitSize = size, Health = health, Save = save, BaseSizeMm = baseMm, Ward = ward,
            Keywords = keywords.ToList(), Weapons = [new WeaponProfile { Name = "Ref", Attacks = "1" }],
        };
}

/// <summary>Offence and defence of one warscroll against the reference opponents.</summary>
public sealed class UnitEfficiency
{
    public required Warscroll Warscroll { get; init; }
    /// <summary>Expected damage of all melee weapons, per reference target.</summary>
    public required double[] Melee { get; init; }
    /// <summary>Expected damage of all ranged weapons, per reference target.</summary>
    public required double[] Ranged { get; init; }
    /// <summary>Expected number of reference attacks needed to destroy the unit, per reference attacker.</summary>
    public required double[] AttacksToKill { get; init; }

    public double Per100(double value) => Warscroll.Points == 0 ? 0 : value * 100 / Warscroll.Points;

    /// <summary>Evaluates a warscroll at full strength, with its passive abilities, in an empty sandbox.</summary>
    public static UnitEfficiency Evaluate(FactionData faction, Warscroll w)
    {
        var (s, unit) = Sandbox(faction, w);
        var melee = new double[References.Targets.Count];
        var ranged = new double[References.Targets.Count];
        for (int t = 0; t < References.Targets.Count; t++)
        {
            var target = Place(s, References.Targets[t].Warscroll, 1, new Vector2(20, 22));
            melee[t] = CombatMath.Mean(CombatMath.Activation(s, unit, target, Uses(unit, w.MeleeWeapons)));
            ranged[t] = CombatMath.Mean(CombatMath.Activation(s, unit, target, Uses(unit, w.RangedWeapons)));
            s.Units.Remove(target);
        }

        var toKill = new double[References.Attackers.Count];
        for (int a = 0; a < References.Attackers.Count; a++)
        {
            var refScroll = References.Targets[1].Warscroll;
            var attacker = Place(s, refScroll, 1, new Vector2(20, 22));
            double perAttack = CombatMath.Mean(CombatMath.Activation(s, attacker, unit, [new WeaponUse(References.Attackers[a].Weapon, 1)]));
            int wounds = w.Health * w.UnitSize;
            toKill[a] = perAttack <= 0 ? double.PositiveInfinity : wounds / perAttack;
            s.Units.Remove(attacker);
        }
        return new UnitEfficiency { Warscroll = w, Melee = melee, Ranged = ranged, AttacksToKill = toKill };
    }

    /// <summary>Damage distribution of a warscroll's weapons against a reference target.</summary>
    public static double[] Distribution(FactionData faction, Warscroll w, References.Target target, bool melee)
    {
        var (s, unit) = Sandbox(faction, w);
        var t = Place(s, target.Warscroll, 1, new Vector2(20, 22));
        return CombatMath.Activation(s, unit, t, Uses(unit, melee ? w.MeleeWeapons : w.RangedWeapons));
    }

    private static (GameState, Unit) Sandbox(FactionData faction, Warscroll w)
    {
        var refFaction = new FactionData { Id = "reference", Name = "Reference" };
        var s = new GameState
        {
            Battleplan = new Battleplan { Id = "lab", Name = "lab" },
            Dice = new SeededDiceRoller(1),
            Players = [new Player { Index = 0, Faction = faction }, new Player { Index = 1, Faction = refFaction }],
        };
        var unit = Place(s, w, 0, new Vector2(10, 22));
        return (s, unit);
    }

    private static int _nextId = 1;

    private static Unit Place(GameState s, Warscroll w, int owner, Vector2 at)
    {
        var u = new Unit { Id = Interlocked.Increment(ref _nextId), Owner = owner, Warscroll = w, Deployed = true };
        var pos = Geometry.Geo.Formation(w.UnitSize, u.BaseExtent, at, 10);
        for (int i = 0; i < w.UnitSize; i++) u.Models.Add(new Model { Index = i, Position = pos[i] });
        s.Units.Add(u);
        return u;
    }

    private static List<WeaponUse> Uses(Unit u, IEnumerable<WeaponProfile> weapons) =>
        weapons.Select(w => new WeaponUse(w, w.ModelsWithWeapon is int cap ? Math.Min(cap, u.AliveCount) : u.AliveCount)).ToList();
}
