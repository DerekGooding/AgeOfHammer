using System.Numerics;
using AgeOfHammer.Core.Combat;
using AgeOfHammer.Core.Data;
using AgeOfHammer.Core.State;
using static AgeOfHammer.Core.Tests.TestData;

namespace AgeOfHammer.Core.Tests;

public class AttackResolverTests
{
    private static (GameState s, Unit attacker, Unit target) Setup(ScriptedDice dice, Warscroll target)
    {
        var s = NewState(dice);
        var a = Unit(Scroll("attacker"), 0, new Vector2(10, 10), 1);
        var t = Unit(target, 1, new Vector2(12, 10), 2);
        s.Units.Add(a);
        s.Units.Add(t);
        return (s, a, t);
    }

    [Fact]
    public void FullSequenceHitWoundSaveDamage()
    {
        // 3 attacks: hit rolls 3 (hit), 2 (miss), 6 (hit); wound rolls 3 (wound), 1 (fail); save roll 3 vs 4+ -> fails.
        var dice = new ScriptedDice(3, 2, 6, 3, 1, 3);
        var (s, a, t) = Setup(dice, Scroll("target", unitSize: 5, health: 1, save: 4));
        var w = Melee(attacks: "3", hit: 3, wound: 3);
        var r = AttackResolver.Resolve(s, a, t, [new WeaponUse(w, 1)], new AttackOptions());
        Assert.Equal(1, r.DamageInflicted);
        Assert.Equal(1, r.ModelsSlain);
        Assert.Equal(4, t.AliveCount);
        Assert.Equal(0, dice.Remaining);
    }

    [Fact]
    public void UnmodifiedOneAlwaysFailsAndSixAlwaysHits()
    {
        Assert.False(AttackResolver.HitSucceeds(1, 2, +1));
        Assert.True(AttackResolver.HitSucceeds(6, 6, -1));
        Assert.True(AttackResolver.HitSucceeds(3, 4, +1));
        Assert.False(AttackResolver.SaveSucceeds(1, 2, 1, 0));
    }

    [Fact]
    public void RendWorsensSaves()
    {
        Assert.True(AttackResolver.SaveSucceeds(4, 4, 0, 0));
        Assert.False(AttackResolver.SaveSucceeds(4, 4, 0, 1));
        Assert.True(AttackResolver.SaveSucceeds(5, 4, 0, 1));
        Assert.False(AttackResolver.SaveSucceeds(6, 7, 0, 0)); // no save
    }

    [Fact]
    public void HitModifiersAreCappedAtPlusOne()
    {
        var dice = new ScriptedDice();
        var (s, a, t) = Setup(dice, Scroll("target"));
        a.Buffs.Add(new Buff("x", Stat.Hit, 1, Expiry.EndOfTurn));
        a.Buffs.Add(new Buff("y", Stat.Hit, 1, Expiry.EndOfTurn));
        Assert.Equal(1, AttackResolver.HitModifier(s, a, t, Melee(), new AttackOptions { ExtraHit = 1 }));
    }

    [Fact]
    public void SaveModifierCappedAtPlusOneButRendUnlimited()
    {
        var dice = new ScriptedDice();
        var (s, a, t) = Setup(dice, Scroll("target"));
        t.Buffs.Add(new Buff("x", Stat.Save, 1, Expiry.EndOfTurn));
        Assert.Equal(1, AttackResolver.SaveModifier(s, t, a, AttackType.Melee, new AttackOptions { ExtraSave = 1 }));
    }

    [Fact]
    public void CritTwoHitsScoresTwoHits()
    {
        // 1 attack: hit 6 (crit -> 2 hits), wounds 3, 3, saves 1, 1 (fail)
        var dice = new ScriptedDice(6, 3, 3, 1, 1);
        var (s, a, t) = Setup(dice, Scroll("target", unitSize: 5));
        var w = Melee(attacks: "1", abilities: "Crit (2 Hits)");
        var r = AttackResolver.Resolve(s, a, t, [new WeaponUse(w, 1)], new AttackOptions());
        Assert.Equal(2, r.Lines[0].Hits);
        Assert.Equal(2, r.DamageInflicted);
    }

    [Fact]
    public void CritAutoWoundSkipsWoundRoll()
    {
        // hit 6 -> auto-wound, save 1 -> fails
        var dice = new ScriptedDice(6, 1);
        var (s, a, t) = Setup(dice, Scroll("target", unitSize: 2));
        var w = Melee(attacks: "1", wound: 6, abilities: "Crit (Auto-wound)");
        var r = AttackResolver.Resolve(s, a, t, [new WeaponUse(w, 1)], new AttackOptions());
        Assert.Equal(1, r.DamageInflicted);
    }

    [Fact]
    public void CritMortalSkipsWoundAndSave()
    {
        // hit 6 -> mortal damage = damage characteristic (2). No further rolls.
        var dice = new ScriptedDice(6);
        var (s, a, t) = Setup(dice, Scroll("target", health: 5, save: 2));
        var w = Melee(attacks: "1", damage: "2", abilities: "Crit (Mortal)");
        var r = AttackResolver.Resolve(s, a, t, [new WeaponUse(w, 1)], new AttackOptions());
        Assert.Equal(2, r.Lines[0].MortalDamage);
        Assert.Equal(2, r.DamageInflicted);
        Assert.Equal(0, dice.Remaining);
    }

    [Fact]
    public void WardNegatesDamagePoints()
    {
        // hit, wound, save fail, damage 3 fixed; ward 5+ rolls: 5 (saved), 2, 6 (saved) -> 1 damage
        var dice = new ScriptedDice(3, 3, 1, 5, 2, 6);
        var (s, a, t) = Setup(dice, Scroll("target", health: 5, ward: 5));
        var w = Melee(attacks: "1", damage: "3");
        var r = AttackResolver.Resolve(s, a, t, [new WeaponUse(w, 1)], new AttackOptions());
        Assert.Equal(2, r.WardSaved);
        Assert.Equal(1, r.DamageInflicted);
    }

    [Fact]
    public void ExcessDamageCarriesOverToNextModel()
    {
        var dice = new ScriptedDice();
        var (s, _, t) = Setup(dice, Scroll("target", unitSize: 3, health: 2));
        var (inflicted, _, slain) = AttackResolver.ApplyDamage(s, t, 5);
        Assert.Equal(5, inflicted);
        Assert.Equal(2, slain);
        Assert.Equal(1, t.AliveCount);
        Assert.Equal(1, t.WoundsRemaining);
    }

    [Fact]
    public void DamagedModelsTakeDamageFirst()
    {
        var dice = new ScriptedDice();
        var (s, _, t) = Setup(dice, Scroll("target", unitSize: 2, health: 3));
        t.Models[1].Damage = 2;
        AttackResolver.ApplyDamage(s, t, 1);
        Assert.True(t.Models[1].Slain);
        Assert.Equal(0, t.Models[0].Damage);
    }

    [Fact]
    public void AntiChargeAddsRendAgainstChargingUnits()
    {
        var dice = new ScriptedDice();
        var (s, a, t) = Setup(dice, Scroll("target"));
        var w = Melee(rend: 1, abilities: "Anti-Charge (+1 Rend)");
        Assert.Equal(1, AttackResolver.EffectiveRend(s, a, t, w));
        t.Charged = true;
        Assert.Equal(2, AttackResolver.EffectiveRend(s, a, t, w));
    }

    [Fact]
    public void AntiKeywordAddsRend()
    {
        var dice = new ScriptedDice();
        var (s, a, t) = Setup(dice, Scroll("target"));
        var w = Melee(abilities: "Anti-Monster (+1 Rend)");
        Assert.Equal(0, AttackResolver.EffectiveRend(s, a, t, w));
        t.Warscroll.Keywords.Add("Monster");
        Assert.Equal(1, AttackResolver.EffectiveRend(s, a, t, w));
    }

    [Fact]
    public void ChargeBonusDamageOnlyWhenCharged()
    {
        // hit, wound, failed save, damage 1 + 1 = 2
        var dice = new ScriptedDice(4, 4, 1);
        var (s, a, t) = Setup(dice, Scroll("target", health: 5));
        a.Charged = true;
        var w = Melee(abilities: "Charge (+1 Damage)");
        var r = AttackResolver.Resolve(s, a, t, [new WeaponUse(w, 1)], new AttackOptions());
        Assert.Equal(2, r.DamageInflicted);
    }

    [Fact]
    public void CompanionWeaponsIgnoreHitModifiers()
    {
        var dice = new ScriptedDice();
        var (s, a, t) = Setup(dice, Scroll("target"));
        a.Buffs.Add(new Buff("x", Stat.Hit, 1, Expiry.EndOfTurn));
        Assert.Equal(0, AttackResolver.HitModifier(s, a, t, Melee(abilities: "Companion"), new AttackOptions { ExtraHit = 1 }));
    }

    [Fact]
    public void DataDrivenPassiveModifierWithCondition()
    {
        var dice = new ScriptedDice();
        var (s, a, t) = Setup(dice, Scroll("target", health: 2));
        a.Warscroll.Abilities.Add(new AbilityDef
        {
            Name = "Lay Low",
            Modifiers = [new PassiveModifierDef { Stat = "Wound", Value = 1, AttackType = "Melee", Condition = "targetHealth5Plus" }],
        });
        Assert.Equal(0, AttackResolver.WoundModifier(s, a, t, Melee()));
        t.Warscroll.Health = 6;
        Assert.Equal(1, AttackResolver.WoundModifier(s, a, t, Melee()));
    }

    [Fact]
    public void ExpectedDamageMatchesHandCalculation()
    {
        var dice = new ScriptedDice();
        var (s, a, t) = Setup(dice, Scroll("target", save: 4));
        // 6 attacks, 3+ hit (4/6), 3+ wound (4/6), 4+ save rend 0 -> unsaved 3/6, dmg 1 => 6 * 2/3 * 2/3 * 1/2 = 1.333
        double e = AttackResolver.ExpectedDamage(s, a, t, Melee(attacks: "6"), 1);
        Assert.Equal(4.0 / 3.0, e, 3);
    }
}
