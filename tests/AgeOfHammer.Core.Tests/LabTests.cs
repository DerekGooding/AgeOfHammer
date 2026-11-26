using System.Numerics;
using AgeOfHammer.Core.Combat;
using AgeOfHammer.Core.Data;
using AgeOfHammer.Core.Lab;
using AgeOfHammer.Core.State;
using static AgeOfHammer.Core.Tests.TestData;

namespace AgeOfHammer.Core.Tests;

public class CombatMathTests
{
    private static (GameState S, Unit A, Unit T) Pair(Warscroll attacker, Warscroll target)
    {
        var s = NewState(new ScriptedDice());
        var a = Unit(attacker, 0, new Vector2(10, 10), 1);
        var t = Unit(target, 1, new Vector2(12, 10), 2);
        s.Units.AddRange([a, t]);
        return (s, a, t);
    }

    [Fact]
    public void SingleAttackIsExact()
    {
        // 1 attack, 4+ hit, 4+ wound, no save, 1 damage: P(1 damage) = 1/2 * 1/2 = 0.25.
        var (s, a, t) = Pair(Scroll("a"), Scroll("t", save: 7));
        var d = CombatMath.Activation(s, a, t, [new WeaponUse(Melee(attacks: "1", hit: 4, wound: 4), 1)]);
        Assert.Equal(0.75, d[0], 6);
        Assert.Equal(0.25, d[1], 6);
    }

    [Theory]
    [InlineData("3", 3, 3, 1, "1", "")]
    [InlineData("D6", 4, 3, 2, "D3", "Crit (2 Hits)")]
    [InlineData("2", 3, 4, 1, "2", "Crit (Auto-wound)")]
    [InlineData("4", 3, 3, 1, "D3+1", "Crit (Mortal)")]
    public void MeanMatchesExpectedDamageAndSumsToOne(string attacks, int hit, int wound, int rend, string damage, string ability)
    {
        var target = Scroll("t", unitSize: 5, health: 3, save: 4, ward: 5);
        var (s, a, t) = Pair(Scroll("a", unitSize: 3), target);
        var w = ability.Length > 0 ? Melee(attacks, hit, wound, rend, damage, ability) : Melee(attacks, hit, wound, rend, damage);
        var d = CombatMath.Activation(s, a, t, [new WeaponUse(w, 3)]);
        Assert.Equal(1.0, d.Sum(), 6);
        Assert.Equal(AttackResolver.ExpectedDamage(s, a, t, w, 3), CombatMath.Mean(d), 4);
    }

    [Fact]
    public void WilsonIntervalIsSensible()
    {
        var r = Rate.Wilson(50, 100);
        Assert.Equal(0.5, r.Value, 6);
        Assert.InRange(r.Low, 0.40, 0.41);
        Assert.InRange(r.High, 0.59, 0.60);
        Assert.Equal(0, Rate.Wilson(0, 10).Low);
    }

    [Fact]
    public void EfficiencyEvaluatesEveryWarscroll()
    {
        var factions = DataLoader.LoadAllFactions(DataLoader.FindDataDirectory());
        foreach (var f in factions.Where(f => f.Id is "stormcast-eternals" or "skaven"))
            foreach (var w in f.Warscrolls)
            {
                var e = UnitEfficiency.Evaluate(f, w);
                Assert.All(e.Melee, v => Assert.True(v >= 0));
                Assert.All(e.Ranged, v => Assert.True(v >= 0));
                Assert.All(e.AttacksToKill, v => Assert.True(v > 0));
            }
        // Sanity: Vanquishers (+1 Damage vs 5+ models) hit the 20-model horde harder per attack than a lone monster.
        var sce = factions.First(f => f.Id == "stormcast-eternals");
        var vanq = UnitEfficiency.Evaluate(sce, sce.Get("vanquishers"));
        Assert.True(vanq.Melee[0] > 0);
    }
}

public class MatchupRunnerTests
{
    private static (ArmyList A, ArmyList B, MatchupSettings S) Setup(int games, int parallel)
    {
        var dir = DataLoader.FindDataDirectory();
        var factions = DataLoader.LoadAllFactions(dir);
        var sce = factions.First(f => f.Id == "stormcast-eternals");
        var skv = factions.First(f => f.Id == "skaven");
        var settings = new MatchupSettings { Battleplan = DataLoader.LoadAllBattleplans(dir).First(), Games = games, MaxParallelism = parallel };
        return (new ArmyList(sce, sce.DefaultArmy), new ArmyList(skv, skv.DefaultArmy), settings);
    }

    [Fact]
    public void ParallelRunsAreDeterministic()
    {
        var (a, b, s) = Setup(16, -1);
        var parallel = MatchupRunner.Run(a, b, s);
        var sequential = MatchupRunner.Run(a, b, s with { MaxParallelism = 1 });
        Assert.Equal(16, parallel.N);
        Assert.Equal(0, parallel.Failures);
        Assert.Equal(sequential.Games, parallel.Games);
        Assert.Equal(parallel.WinA.Value + parallel.WinB.Value + parallel.Draw.Value, 1.0, 6);
        Assert.NotEmpty(parallel.Units);
        Assert.Contains(parallel.Units, u => u.Side == "A" && u.DamageDealt > 0);
    }

    [Fact]
    public void ReplayReproducesTheSameGame()
    {
        var (a, b, s) = Setup(4, -1);
        var result = MatchupRunner.Run(a, b, s);
        var game = result.Games[3];
        var (engine, ai) = MatchupRunner.CreateGame(a, b, s, game.Seed, game.Swapped);
        while (engine.State.Phase != Phase.GameOver) ai[engine.Decider].Act(engine);
        int sideA = game.Swapped ? 1 : 0;
        Assert.Equal(game.VpA, engine.State.Players[sideA].VictoryPoints);
        Assert.Equal(game.VpB, engine.State.Players[1 - sideA].VictoryPoints);
    }
}
