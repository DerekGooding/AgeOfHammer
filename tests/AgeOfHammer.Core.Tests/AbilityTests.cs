using System.Numerics;
using AgeOfHammer.Core.AI;
using AgeOfHammer.Core.Combat;
using AgeOfHammer.Core.Data;
using AgeOfHammer.Core.Dice;
using AgeOfHammer.Core.Engine;
using AgeOfHammer.Core.State;
using static AgeOfHammer.Core.Tests.TestData;

namespace AgeOfHammer.Core.Tests;

public class PassiveAbilityTests
{
    private static AbilityDef Passive(string name, params PassiveModifierDef[] mods) => new() { Name = name, Modifiers = mods.ToList() };

    [Fact]
    public void AuraGrantsWardToFriendlyUnitsWhollyWithinRange()
    {
        var bell = Scroll("bell", health: 10);
        bell.Abilities.Add(Passive("Altar", new PassiveModifierDef
        {
            Stat = "Ward", Value = 6, Aura = new AuraDef { Range = 13, Wholly = true, Keywords = ["INFANTRY"] },
        }));
        var rats = Scroll("rats");
        var foe = Scroll("foe");
        var e = DeployedEngine(new ScriptedDice(), [bell, rats, rats], [new(10, 20), new(15, 20), new(40, 20)], [foe], [new(50, 5)]);
        var units = e.State.UnitsOf(0).ToList();
        Assert.Equal(6, AttackResolver.EffectiveWard(e.State, units[1]));    // near the bell
        Assert.Null(AttackResolver.EffectiveWard(e.State, units[2]));        // 30" away
        Assert.Null(AttackResolver.EffectiveWard(e.State, e.State.UnitsOf(1).Single())); // enemy
    }

    [Fact]
    public void ConditionalDamageAgainstLargeUnitsAndWeaponFilter()
    {
        var s = NewState(new ScriptedDice());
        var attacker = Unit(Scroll("vanq", weapons: [Melee()]), 0, new Vector2(10, 10), 1);
        attacker.Warscroll.Weapons[0].Name = "Celestial Greatsword";
        attacker.Warscroll.Abilities.Add(Passive("Lightning Strikes",
            new PassiveModifierDef { Stat = "Damage", Value = 1, Weapon = "Greatsword", Condition = "targetModels>=5" }));
        var small = Unit(Scroll("small", unitSize: 3), 1, new Vector2(12, 10), 2);
        var big = Unit(Scroll("big", unitSize: 5), 1, new Vector2(30, 10), 3);
        s.Units.AddRange([attacker, small, big]);
        var w = attacker.Warscroll.Weapons[0];
        Assert.Equal(0, AttackResolver.DamageBonus(s, attacker, small, w));
        Assert.Equal(1, AttackResolver.DamageBonus(s, attacker, big, w));
    }

    [Fact]
    public void HitAgainstAndMultiplyAndUnmodifiedFail()
    {
        var s = NewState(new ScriptedDice());
        var attacker = Unit(Scroll("a"), 0, new Vector2(10, 10), 1);
        attacker.Warscroll.Abilities.Add(Passive("Prime Huntress",
            new PassiveModifierDef { Stat = "Damage", Multiply = 2, Condition = "targetKeyword:MONSTER" }));
        var target = Unit(Scroll("t", health: 10), 1, new Vector2(12, 10), 2);
        target.Warscroll.Keywords.Add("MONSTER");
        target.Warscroll.Abilities.Add(Passive("Illusions", new PassiveModifierDef { Stat = "HitAgainst", Value = -1 }));
        s.Units.AddRange([attacker, target]);
        var w = Melee(damage: "2");
        Assert.Equal(-1, AttackResolver.HitModifier(s, attacker, target, w, new AttackOptions()));
        Assert.Equal(2, AttackResolver.DamageMultiplier(s, attacker, target, w));

        // Shadowy Killers: unmodified hit rolls of 1-4 fail. Roll 4 (would hit on 3+) then 5 (hits).
        target.Warscroll.Abilities.Add(Passive("Shadowy", new PassiveModifierDef { Stat = "UnmodifiedHitFailsAgainst", Value = 4 }));
        var dice = new ScriptedDice(4, 5, 3, 1); // hit 4 fails, hit 5 ok, wound 3, save 1
        var s2 = NewState(dice);
        s2.Units.AddRange([attacker, target]);
        var r = AttackResolver.Resolve(s2, attacker, target, [new WeaponUse(Melee(attacks: "2", hit: 3, damage: "2"), 1)], new AttackOptions());
        Assert.Equal(1, r.Lines[0].Hits);
        Assert.Equal(4, r.DamageInflicted); // 2 damage doubled vs MONSTER
    }

    [Fact]
    public void MaxControlCapsControlScore()
    {
        var beast = Scroll("beast", unitSize: 3, control: 2);
        beast.Abilities.Add(Passive("Beast", new PassiveModifierDef { Stat = "MaxControl", Value = 1 }));
        var plan = OpenField();
        plan.Territories = [new RectDef { W = 60, H = 44 }, new RectDef { W = 60, H = 44 }];
        var e = DeployedEngine(new ScriptedDice(), [beast], [new(30, 20)], [Scroll("x")], [new(50, 5)], plan);
        Assert.Equal(3, e.State.ControlScore(e.State.Objectives[0], 0)); // 3 models x capped 1
    }

    [Fact]
    public void NoRetreatDamageBattleTraitWithKeywordCondition()
    {
        var rat = Scroll("rat", health: 5, move: 6);
        var f0 = Faction("skv", rat);
        f0.BattleTraits.Add(Passive("Too Quick", new PassiveModifierDef { Stat = "NoRetreatDamage", Value = 1, Condition = "keywordAny:INFANTRY|CAVALRY" }));
        var foe = Scroll("foe");
        var plan = new Battleplan { Id = "t", Name = "t", Territories = [new RectDef { W = 60, H = 44 }, new RectDef { W = 60, H = 44 }] };
        var e = GameEngine.Create(plan, f0, ["rat"], Faction("b", foe), ["foe"], new ScriptedDice());
        e.Submit(new DeployUnit(0, e.State.UnitsOf(0).Single().Id, [new Vector2(10, 20)]));
        e.Submit(new DeployUnit(1, e.State.UnitsOf(1).Single().Id, [new Vector2(12, 20)]));
        AdvanceTo(e, Phase.Movement);
        var unit = e.State.UnitsOf(0).Single();
        Assert.True(e.Submit(new DeclareMove(0, unit.Id, MoveKind.Retreat)).Ok);
        Assert.Equal(0, unit.DamagePoints);
    }

    [Fact]
    public void WardRollModifierReducesWardSaves()
    {
        var s = NewState(new ScriptedDice(5, 5)); // two ward rolls of 5 vs 5+ ward with -1 => both fail
        var target = Unit(Scroll("t", health: 5, ward: 5), 1, new Vector2(10, 10), 1);
        var censer = Unit(Scroll("censer"), 0, new Vector2(12, 10), 2);
        censer.Warscroll.Abilities.Add(Passive("Corrupting", new PassiveModifierDef { Stat = "WardRoll", Value = -1, Aura = new AuraDef { Side = "enemy", Range = 13 } }));
        s.Units.AddRange([target, censer]);
        var (inflicted, warded, _) = AttackResolver.ApplyDamage(s, target, 2);
        Assert.Equal(0, warded);
        Assert.Equal(2, inflicted);
    }
}

public class ActivatedAbilityTests
{
    private static AbilityDef Active(string name, string phase, string perspective, string timing, ActivationDef act) =>
        new() { Name = name, Kind = "active", Phase = phase, Perspective = perspective, Timing = timing, Activate = act };

    [Fact]
    public void ImpactMortalsAfterChargingAndOncePerPhase()
    {
        var flayer = Scroll("flayer", health: 5);
        flayer.Abilities.Add(Active("Whirling Doom", "charge", "any", "Any Charge Phase", new ActivationDef
        {
            Target = "enemy", Range = 1, Requires = "chargedThisPhase", Roll = "D3", Success = 2,
            Effects = [new EffectDef { Type = "mortal", Amount = "roll" }],
        }));
        var foe = Scroll("foe", health: 10);
        var dice = new ScriptedDice();
        var e = DeployedEngine(dice, [flayer], [new(10, 20)], [foe], [new(17, 20)]);
        AdvanceTo(e, Phase.Charge);
        var unit = e.State.UnitsOf(0).Single();
        var enemy = e.State.UnitsOf(1).Single();
        Assert.Empty(e.UsableAbilities(0)); // hasn't charged

        dice.Add(4, 4);
        e.Submit(new DeclareCharge(0, unit.Id));
        var pm = (PendingMove)e.Pending!;
        e.Submit(new CompleteMove(0, Placement.AutoCharge(e, pm, enemy)!));
        Assert.Contains(e.UsableAbilities(0), x => x.Ability.Name == "Whirling Doom");

        dice.Add(3); // D3 roll of 3 => 3 mortal damage
        Assert.True(e.Submit(new UseAbility(0, unit.Id, "Whirling Doom", [enemy.Id])).Ok);
        Assert.Equal(3, enemy.DamagePoints);
        Assert.False(e.Submit(new UseAbility(0, unit.Id, "Whirling Doom", [enemy.Id])).Ok); // once per phase
    }

    [Fact]
    public void OncePerTurnArmyLimitsAllUnits()
    {
        var hero = Scroll("hero");
        hero.Abilities.Add(Active("Cheer", "hero", "your", "Once Per Turn (Army), Your Hero Phase", new ActivationDef
        {
            Target = "self", Effects = [new EffectDef { Type = "buff", Stat = "Control", Value = 1 }],
        }));
        var e = DeployedEngine(new ScriptedDice(), [hero, hero], [new(10, 30), new(30, 30)], [Scroll("x"), Scroll("x")], [new(50, 5), new(40, 5)]);
        var units = e.State.UnitsOf(0).ToList();
        if (e.State.ActivePlayer != 0) AdvanceTo(e, Phase.Hero); // ensure it's player 0's hero phase
        Assert.Equal(0, e.State.ActivePlayer);
        Assert.True(e.Submit(new UseAbility(0, units[0].Id, "Cheer")).Ok);
        Assert.False(e.Submit(new UseAbility(0, units[1].Id, "Cheer")).Ok);
    }

    [Fact]
    public void AutoHealAtEndOfTurn()
    {
        var terror = Scroll("terror", health: 12);
        terror.Abilities.Add(Active("Regenerating", "end-of-turn", "any", "End of Any Turn", new ActivationDef
        {
            Target = "self", Auto = true, Effects = [new EffectDef { Type = "heal", Amount = "3" }],
        }));
        var e = DeployedEngine(new SeededDiceRoller(1), [terror], [new(10, 30)], [Scroll("x")], [new(50, 5)]);
        var unit = e.State.UnitsOf(0).Single();
        unit.Models[0].Damage = 5;
        AdvanceTo(e, Phase.EndOfTurn);
        Assert.Equal(2, unit.DamagePoints);
    }

    [Fact]
    public void OpponentGetsAbilityWindowAtEndOfPhase()
    {
        var hound = Scroll("hound");
        hound.Abilities.Add(Active("Warning Shot", "shooting", "any", "Any Shooting Phase", new ActivationDef
        {
            Target = "enemy", Range = 24, Effects = [new EffectDef { Type = "mortal", Amount = "1" }],
        }));
        var e = DeployedEngine(new SeededDiceRoller(2), [Scroll("a", health: 3)], [new(10, 30)], [hound], [new(10, 15)]);
        // Player 0 deployed first and is active. Ending their shooting phase offers player 1 a window.
        AdvanceTo(e, Phase.Shooting);
        Assert.Equal(0, e.State.ActivePlayer);
        e.State.Players[1].CommandPoints = 0; // no Covering Fire reaction to muddy the test
        Assert.True(e.Submit(new EndPhase(0)).Ok);
        Assert.IsType<PendingAbilityWindow>(e.Pending);
        Assert.Equal(1, e.Decider);
        var hunter = e.State.UnitsOf(1).Single();
        var target = e.State.UnitsOf(0).Single();
        var used = e.Submit(new UseAbility(1, hunter.Id, "Warning Shot", [target.Id]));
        Assert.True(used.Ok, used.Error);
        Assert.Equal(1, target.DamagePoints);
        Assert.True(e.Submit(new DoneAbilities(1)).Ok);
        Assert.Equal(Phase.Charge, e.State.Phase);
    }

    [Fact]
    public void ChargeDiceAddsAThirdDice()
    {
        var pros = Scroll("pros");
        pros.Abilities.Add(new AbilityDef { Name = "Heralds", Modifiers = [new PassiveModifierDef { Stat = "ChargeDice", Value = 1 }] });
        var dice = new ScriptedDice();
        var e = DeployedEngine(dice, [pros], [new(10, 20)], [Scroll("foe")], [new(20, 20)]);
        AdvanceTo(e, Phase.Charge);
        dice.Add(1, 2, 3);
        e.Submit(new DeclareCharge(0, e.State.UnitsOf(0).Single().Id));
        Assert.Equal(6, ((PendingMove)e.Pending!).Allowance);
    }

    [Fact]
    public void NoCommandsBlocksAllOutDefence()
    {
        var foe = Scroll("foe");
        var e = DeployedEngine(new ScriptedDice(), [Scroll("a")], [new(10, 20)], [foe], [new(12, 20)]);
        var enemy = e.State.UnitsOf(1).Single();
        Assert.True(e.CanUseCommand(1, Commands.AllOutDefence, enemy));
        enemy.Buffs.Add(new Buff("Thunderstruck", Stat.NoCommands, 1, Expiry.EndOfTurn));
        Assert.False(e.CanUseCommand(1, Commands.AllOutDefence, enemy));
    }
}

public class ScriptDataTests
{
    [Fact]
    public void ScriptsMatchRealAbilitiesAndParse()
    {
        var factions = DataLoader.LoadAllFactions(DataLoader.FindDataDirectory());
        foreach (var f in factions) Assert.Empty(f.LoadWarnings);

        foreach (var id in new[] { "stormcast-eternals", "skaven" })
        {
            var f = factions.First(x => x.Id == id);
            var all = f.Warscrolls.SelectMany(w => w.Abilities).Concat(f.BattleTraits).ToList();
            Assert.True(all.Count(a => a.IsScripted) >= 30, $"{id}: only {all.Count(a => a.IsScripted)} scripted abilities");
            // Every stat/effect/condition name used in scripts must be known to the engine.
            foreach (var a in all)
            {
                foreach (var m in a.Modifiers) Assert.True(Enum.TryParse<Stat>(m.Stat, true, out _), $"{a.Name}: unknown stat {m.Stat}");
                foreach (var fx in a.Activate?.Effects ?? [])
                {
                    Assert.Contains(fx.Type, new[] { "mortal", "mortalPerModel", "mortalDice", "slayRoll", "heal", "returnModels", "buff", "table", "move", "setUp" });
                    if (fx.Type == "buff") Assert.True(Enum.TryParse<Stat>(fx.Stat, true, out _), $"{a.Name}: unknown stat {fx.Stat}");
                }
            }
        }
    }

    [Fact]
    public void ScriptedArmiesPlayFullGames()
    {
        var dir = DataLoader.FindDataDirectory();
        var factions = DataLoader.LoadAllFactions(dir);
        var plan = DataLoader.LoadAllBattleplans(dir).First();
        var sce = factions.First(f => f.Id == "stormcast-eternals");
        var skv = factions.First(f => f.Id == "skaven");
        // Armies that include many scripted units.
        string[] a = ["knight-azyros", "vindictors", "dracothian-guard-fulminators", "lord-vigilant-on-morrgryph", "aetherwings", "vanquishers"];
        string[] b = ["clawlord-on-gnaw-beast", "doom-flayers", "clanrats", "brood-terror", "rat-ogors", "stormvermin", "krittok-foulblade"];
        int abilityUses = 0;
        for (int seed = 1; seed <= 6; seed++)
        {
            var e = GameEngine.Create(plan, sce, a, skv, b, new SeededDiceRoller(seed));
            var ai = new[] { new AiPlayer(0), new AiPlayer(1) };
            int guard = 0;
            while (e.State.Phase != Phase.GameOver)
            {
                Assert.True(++guard < 20000);
                ai[e.Decider].Act(e);
            }
            Assert.DoesNotContain(e.State.Log, l => l.StartsWith("(AI:"));
            abilityUses += e.State.UsedAbilities.Count;
        }
        Assert.True(abilityUses > 20, $"abilities were used only {abilityUses} times in 6 games");
    }
}
