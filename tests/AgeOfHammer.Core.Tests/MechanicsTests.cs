using System.Numerics;
using AgeOfHammer.Core.Combat;
using AgeOfHammer.Core.Data;
using AgeOfHammer.Core.Dice;
using AgeOfHammer.Core.Engine;
using AgeOfHammer.Core.State;
using static AgeOfHammer.Core.Tests.TestData;

namespace AgeOfHammer.Core.Tests;

public class AttackTriggerTests
{
    private static AbilityDef Passive(params PassiveModifierDef[] mods) => new() { Name = "p", Modifiers = mods.ToList() };

    private static (GameState S, Unit A, Unit T) Pair(IDiceRoller dice, Warscroll attacker, Warscroll target)
    {
        var s = NewState(dice);
        var a = Unit(attacker, 0, new Vector2(10, 10), 1);
        var t = Unit(target, 1, new Vector2(12, 10), 2);
        s.Units.AddRange([a, t]);
        return (s, a, t);
    }

    [Fact]
    public void ExtremisChamberReflectsMortalOnSaveSix()
    {
        var target = Scroll("dracoth", health: 5, save: 3);
        target.Abilities.Add(Passive(new PassiveModifierDef { Stat = "SaveSixMortal", Value = 1, AttackType = "Melee" }));
        // 2 attacks: hits 4,4; wounds 4,4; saves 6 (saved, triggers) and 1 (fails).
        var (s, a, t) = Pair(new ScriptedDice(4, 4, 4, 4, 6, 1), Scroll("attacker", health: 3, weapons: [Melee(attacks: "2")]), target);
        var r = AttackResolver.Resolve(s, a, t, [new WeaponUse(a.Warscroll.Weapons[0], 1)], new AttackOptions());
        Assert.Equal(1, r.DamageInflicted);
        Assert.Equal(1, r.Reflected);
        Assert.Equal(1, a.DamagePoints);
    }

    [Fact]
    public void SpreadersOfFilthRollsPerSlainModel()
    {
        var monks = Scroll("monks", unitSize: 10, save: 7);
        monks.Abilities.Add(Passive(new PassiveModifierDef { Stat = "SlainMortalOn", Value = 6 }));
        // 3 attacks hit (3,3,3), wound (3,3,3), save rolls 1,1,1 (no save anyway) => 3 slain; reflect rolls 6, 2, 6 => 2 mortal.
        var (s, a, t) = Pair(new ScriptedDice(3, 3, 3, 3, 3, 3, 1, 1, 1, 6, 2, 6), Scroll("attacker", health: 5, weapons: [Melee(attacks: "3")]), monks);
        var r = AttackResolver.Resolve(s, a, t, [new WeaponUse(a.Warscroll.Weapons[0], 1)], new AttackOptions());
        Assert.Equal(3, r.ModelsSlain);
        Assert.Equal(2, r.Reflected);
    }

    [Fact]
    public void ShockGauntletsCritScoresDiceHits()
    {
        var fiends = Scroll("fiends", weapons: [Melee(attacks: "1") ]);
        fiends.Weapons[0].Name = "Shock Gauntlets";
        fiends.Abilities.Add(Passive(new PassiveModifierDef { Stat = "CritHits", Amount = "D6", Weapon = "Shock Gauntlets" }));
        // Hit roll 6 (crit) => D6 = 3 hits; wounds 3,3,3; saves 1,1,1.
        var (s, a, t) = Pair(new ScriptedDice(6, 3, 3, 3, 3, 1, 1, 1), fiends, Scroll("t", unitSize: 5));
        var r = AttackResolver.Resolve(s, a, t, [new WeaponUse(a.Warscroll.Weapons[0], 1)], new AttackOptions());
        Assert.Equal(3, r.Lines[0].Hits);
        Assert.Equal(3, r.DamageInflicted);
    }

    [Fact]
    public void HitsAreMortalSkipsWoundAndSave()
    {
        var cannon = Scroll("cannon", weapons: [Ranged(attacks: "2", hit: 4)]);
        cannon.Abilities.Add(Passive(new PassiveModifierDef { Stat = "HitsAreMortal", Value = 1, AttackType = "Ranged" }));
        var (s, a, t) = Pair(new ScriptedDice(5, 2), cannon, Scroll("t", health: 5, save: 2));
        var r = AttackResolver.Resolve(s, a, t, [new WeaponUse(a.Warscroll.Weapons[0], 1)], new AttackOptions());
        Assert.Equal(1, r.DamageInflicted);
    }
}

public class AbilityMechanicsTests
{
    private static AbilityDef Active(string name, string phase, string perspective, string timing, ActivationDef act, string? cost = null, int? value = null) =>
        new() { Name = name, Kind = "active", Phase = phase, Perspective = perspective, Timing = timing, Activate = act, CostKind = cost, CostValue = value };

    [Fact]
    public void RuinationChamberResistsOncePerTurn()
    {
        var caster = Scroll("caster");
        caster.Abilities.Add(Active("Zap", "hero", "your", "Your Hero Phase", new ActivationDef
        {
            Target = "enemy", Effects = [new EffectDef { Type = "mortal", Amount = "1" }],
        }));
        caster.Abilities.Add(Active("Zap2", "hero", "your", "Your Hero Phase", new ActivationDef
        {
            Target = "enemy", Effects = [new EffectDef { Type = "mortal", Amount = "1" }],
        }));
        var knight = Scroll("knight", health: 6);
        knight.Abilities.Add(new AbilityDef { Name = "Ruination Chamber", Modifiers = [new PassiveModifierDef { Stat = "ResistAbilities", Value = 4, OncePerTurn = true }] });
        var dice = new ScriptedDice();
        var e = DeployedEngine(dice, [caster], [new(10, 30)], [knight], [new(10, 15)]);
        var me = e.State.UnitsOf(0).Single();
        var foe = e.State.UnitsOf(1).Single();
        dice.Add(5); // resistance roll: resisted
        Assert.True(e.Submit(new UseAbility(0, me.Id, "Zap", [foe.Id])).Ok);
        Assert.Equal(0, foe.DamagePoints);
        Assert.True(e.Submit(new UseAbility(0, me.Id, "Zap2", [foe.Id])).Ok); // no second resistance this turn
        Assert.Equal(1, foe.DamagePoints);
    }

    [Fact]
    public void PrayersNeedAChantingRollAndArePriestLimited()
    {
        var priest = Scroll("priest", health: 5);
        priest.PriestLevel = 1;
        priest.Keywords.Add("PRIEST");
        priest.Abilities.Add(Active("Smite", "hero", "your", "Your Hero Phase", new ActivationDef
        {
            Target = "enemy", Effects = [new EffectDef { Type = "mortal", Amount = "2" }],
        }, cost: "prayer", value: 4));
        priest.Abilities.Add(Active("Bless", "hero", "your", "Your Hero Phase", new ActivationDef
        {
            Target = "self", Effects = [new EffectDef { Type = "buff", Stat = "Control", Value = 1 }],
        }, cost: "prayer", value: 2));
        var dice = new ScriptedDice();
        var e = DeployedEngine(dice, [priest], [new(10, 30)], [Scroll("foe", health: 5)], [new(10, 15)]);
        var me = e.State.UnitsOf(0).Single();
        var foe = e.State.UnitsOf(1).Single();
        dice.Add(3); // chanting roll 3 < 4: not answered
        Assert.True(e.Submit(new UseAbility(0, me.Id, "Smite", [foe.Id])).Ok);
        Assert.Equal(0, foe.DamagePoints);
        // PRIEST (1): only one prayer per phase.
        Assert.False(e.AbilityAvailable(me, me.Warscroll.Abilities.First(a => a.Name == "Bless")));
    }

    [Fact]
    public void ResultsTableWithAreaEffects()
    {
        var bell = Scroll("bell", health: 15);
        bell.Abilities.Add(Active("Peal", "hero", "your", "Your Hero Phase", new ActivationDef
        {
            Target = "self",
            Effects =
            [
                new EffectDef
                {
                    Type = "table", Dice = "D6",
                    Table =
                    [
                        new TableRowDef { Min = 1, Max = 1, Effects = [new EffectDef { Type = "mortal", Amount = "3", To = "self" }] },
                        new TableRowDef { Min = 2, Max = 6, Effects = [new EffectDef { Type = "mortal", Amount = "2", Area = 13 }] },
                    ],
                },
            ],
        }));
        var dice = new ScriptedDice();
        var e = DeployedEngine(dice, [bell], [new(30, 30)], [Scroll("near", health: 5), Scroll("far", health: 5)], [new(30, 20), new(5, 5)]);
        var me = e.State.UnitsOf(0).Single();
        var foes = e.State.UnitsOf(1).ToList();
        dice.Add(5);
        Assert.True(e.Submit(new UseAbility(0, me.Id, "Peal")).Ok);
        Assert.Equal(2, foes[0].DamagePoints); // within 13"
        Assert.Equal(0, foes[1].DamagePoints); // far away
        Assert.Equal(0, me.DamagePoints);
    }

    [Fact]
    public void MoveAndSetUpEffectsAskForPlacement()
    {
        var hero = Scroll("hero", health: 5);
        hero.Abilities.Add(Active("Nudge", "hero", "your", "Your Hero Phase", new ActivationDef
        {
            Target = "self", Effects = [new EffectDef { Type = "move", Amount = "3" }],
        }));
        hero.Abilities.Add(Active("Teleport", "hero", "your", "Your Hero Phase", new ActivationDef
        {
            Target = "self", Effects = [new EffectDef { Type = "setUp", MinEnemyDistance = 9 }],
        }));
        var e = DeployedEngine(new ScriptedDice(), [hero], [new(30, 35)], [Scroll("foe")], [new(30, 5)]);
        var me = e.State.UnitsOf(0).Single();
        Assert.True(e.Submit(new UseAbility(0, me.Id, "Nudge")).Ok);
        var pm = Assert.IsType<PendingMove>(e.Pending);
        Assert.Equal(3, pm.Allowance);
        Assert.True(e.Submit(new CompleteMove(0, [new Vector2(30, 32)])).Ok);

        Assert.True(e.Submit(new UseAbility(0, me.Id, "Teleport")).Ok);
        var ps = Assert.IsType<PendingSetUp>(e.Pending);
        Assert.NotNull(e.ValidateSetUp(ps, [new Vector2(30, 10)])); // too close to the enemy
        Assert.True(e.Submit(new CompleteMove(0, [new Vector2(50, 25)])).Ok);
        Assert.Equal(new Vector2(50, 25), me.Models[0].Position);
        Assert.True(me.SetUpThisTurn);
    }

    [Fact]
    public void FollowUpFightAndFightTwice()
    {
        var lord = Scroll("lord", health: 6);
        lord.Keywords.Add("HERO");
        lord.Abilities.Add(new AbilityDef
        {
            Name = "Furious Retribution",
            FollowUp = new FollowUpDef { Keywords = ["INFANTRY"], ExcludeKeywords = ["HERO"], Effects = [new EffectDef { Type = "buff", Stat = "Wound", Value = 1 }] },
        });
        var troops = Scroll("troops", health: 3);
        var foe = Scroll("foe", health: 30, save: 7);
        var e = DeployedEngine(new SeededDiceRoller(4), [lord, troops], [new(10, 20), new(12, 22)], [foe, Scroll("x")], [new(11.5f, 18.5f), new(50, 5)]);
        AdvanceTo(e, Phase.Combat);
        e.State.Players[0].CommandPoints = e.State.Players[1].CommandPoints = 0;
        var mine = e.State.UnitsOf(0).ToList();
        var enemy = e.State.UnitsOf(1).First();
        int active = e.State.ActivePlayer;
        Assert.Equal(0, active);
        Assert.True(e.Submit(new Fight(0, mine[0].Id, enemy.Id)).Ok);
        var pf = Assert.IsType<PendingFollowUp>(e.Pending);
        Assert.Contains(mine[1], pf.Candidates);
        Assert.True(e.Submit(new Fight(0, mine[1].Id, enemy.Id)).Ok);
        Assert.True(mine[1].Fought);
        Assert.Contains(mine[1].Buffs, b => b.Stat == Stat.Wound);
        Assert.Equal(1, e.Decider); // back to alternating: the opponent picks next

        // Fight twice: an ExtraFight buff lets a unit fight again (with STRIKE-LAST).
        var unit = mine[0];
        unit.Buffs.Add(new Buff("Frenzy", Stat.ExtraFight, 1, Expiry.EndOfTurn));
        unit.Fought = false;
        Assert.True(e.IsEligibleToFight(unit) || !e.State.InCombat(unit));
    }

    [Fact]
    public void ChargeRerollFlagRerollsFailedCharges()
    {
        var riders = Scroll("riders");
        riders.Abilities.Add(new AbilityDef { Name = "Vengeance", Modifiers = [new PassiveModifierDef { Stat = "ChargeReroll", Value = 1 }] });
        var dice = new ScriptedDice();
        var e = DeployedEngine(dice, [riders], [new(10, 20)], [Scroll("foe")], [new(20, 20)]);
        AdvanceTo(e, Phase.Charge);
        e.State.Players[0].CommandPoints = 0;
        dice.Add(1, 1, 6, 6); // 2" fails, free re-roll 12"
        e.Submit(new DeclareCharge(0, e.State.UnitsOf(0).Single().Id));
        Assert.Equal(12, ((PendingMove)e.Pending!).Allowance);
    }
}
