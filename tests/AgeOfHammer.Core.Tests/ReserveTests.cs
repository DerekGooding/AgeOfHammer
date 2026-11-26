using System.Numerics;
using AgeOfHammer.Core.AI;
using AgeOfHammer.Core.Combat;
using AgeOfHammer.Core.Data;
using AgeOfHammer.Core.Dice;
using AgeOfHammer.Core.Engine;
using AgeOfHammer.Core.State;
using static AgeOfHammer.Core.Tests.TestData;

namespace AgeOfHammer.Core.Tests;

public class ReserveTests
{
    private static FactionData DeepStrikers(params Warscroll[] scrolls)
    {
        var f = Faction("storm", scrolls);
        f.BattleTraits.Add(new AbilityDef { Name = "Realm", Timing = "Deployment Phase", Reserve = new ReserveDef { Pool = "Realm", Declare = true } });
        f.BattleTraits.Add(new AbilityDef
        {
            Name = "Scions", Timing = "Your Movement Phase", Phase = "movement", Perspective = "your",
            Reserve = new ReserveDef { Pool = "Realm", Arrive = new ArrivalDef { MinEnemyDistance = 9 } },
        });
        return f;
    }

    private static Battleplan Open() => new()
    {
        Id = "t", Name = "t", BattleRounds = 5,
        Territories = [new RectDef { X = 0, Y = 32, W = 60, H = 12 }, new RectDef { X = 0, Y = 0, W = 60, H = 12 }],
    };

    /// <summary>Player 0 has [a, b] (deep strike), player 1 has [foe].</summary>
    private static (GameEngine E, Unit A, Unit B, Unit Foe) Setup(IDiceRoller dice)
    {
        var a = Scroll("a", health: 3);
        var b = Scroll("b", health: 3);
        var e = GameEngine.Create(Open(), DeepStrikers(a, b), ["a", "b"], Faction("x", Scroll("foe", health: 10)), ["foe"], dice);
        var units = e.State.UnitsOf(0).ToList();
        return (e, units[0], units[1], e.State.UnitsOf(1).Single());
    }

    [Fact]
    public void AtMostHalfTheArmyCanStartInReserve()
    {
        var (e, a, b, foe) = Setup(new SeededDiceRoller(1));
        Assert.True(e.Submit(new DeployInReserve(0, a.Id, "Realm")).Ok);
        Assert.True(a.InReserve);
        Assert.False(a.OnBattlefield);
        Assert.True(e.Submit(new DeployUnit(1, foe.Id, [new Vector2(30, 5)])).Ok);
        Assert.False(e.CanDeployInReserve(b, "Realm")); // 1 of 2 units already reserved
        Assert.False(e.Submit(new DeployInReserve(0, b.Id, "Realm")).Ok);
    }

    [Fact]
    public void ArrivalRulesRoundTwoAndMoreThanNineInches()
    {
        var (e, a, b, foe) = Setup(new SeededDiceRoller(3));
        e.Submit(new DeployInReserve(0, a.Id, "Realm"));
        e.Submit(new DeployUnit(1, foe.Id, [new Vector2(30, 5)]));
        e.Submit(new DeployUnit(0, b.Id, [new Vector2(30, 40)]));

        // Round 1: no arrivals.
        AdvanceUntil(e, () => e.State.Phase == Phase.Movement && e.State.ActivePlayer == 0);
        Assert.Equal(1, e.State.BattleRound);
        Assert.False(e.CanArrive(a));

        // Round 2, player 0's movement phase.
        AdvanceUntil(e, () => e.State.BattleRound == 2 && e.State.Phase == Phase.Movement && e.State.ActivePlayer == 0);
        Assert.True(e.CanArrive(a));
        Assert.False(e.Submit(new ArriveFromReserve(0, a.Id, [new Vector2(30, 12)])).Ok); // ~6" from the enemy
        Assert.True(e.Submit(new ArriveFromReserve(0, a.Id, [new Vector2(30, 20)])).Ok);
        Assert.True(a.OnBattlefield);
        Assert.True(a.SetUpThisTurn);
        Assert.True(Modifiers.ConditionHolds(e.State, "setUpThisTurn", a, a, null));
        Assert.False(e.CanNormalMove(a)); // arriving uses the unit's move
    }

    [Fact]
    public void UnitsStillInReserveAreDestroyedAtRoundFour()
    {
        var (e, a, b, foe) = Setup(new SeededDiceRoller(5));
        e.Submit(new DeployInReserve(0, a.Id, "Realm"));
        e.Submit(new DeployUnit(1, foe.Id, [new Vector2(30, 5)]));
        e.Submit(new DeployUnit(0, b.Id, [new Vector2(30, 40)]));
        AdvanceUntil(e, () => e.State.BattleRound == 4);
        Assert.True(a.IsDestroyed);
        Assert.False(a.InReserve);
    }

    [Fact]
    public void GnawholesArePlacedAtRoundStartAndRestrictArrival()
    {
        var rats = Scroll("rats", health: 2);
        var other = Scroll("other", health: 2);
        var f = Faction("skv", rats, other);
        f.BattleTraits.Add(new AbilityDef { Name = "Tunnels", Timing = "Deployment Phase", Reserve = new ReserveDef { Pool = "tunnels", Declare = true } });
        f.BattleTraits.Add(new AbilityDef
        {
            Name = "Ambush", Timing = "Your Movement Phase", Phase = "movement", Perspective = "your",
            Reserve = new ReserveDef { Pool = "tunnels", Arrive = new ArrivalDef { MinEnemyDistance = 9, Marker = "Gnawhole", MarkerRange = 6 } },
        });
        f.BattleTraits.Add(new AbilityDef
        {
            Name = "Splinters", Timing = "Once Per Battle Round (Army), Start of Battle Round", Phase = "battle-round-start",
            Marker = new MarkerDef { Name = "Gnawhole", MinEnemyDistance = 9, MinObjectiveDistance = 3, MinTerrainDistance = 1 },
        });
        var e = GameEngine.Create(Open(), f, ["rats", "other"], Faction("x", Scroll("foe", health: 10)), ["foe"], new SeededDiceRoller(9));
        var mine = e.State.UnitsOf(0).ToList();
        e.Submit(new DeployInReserve(0, mine[0].Id, "tunnels"));
        e.Submit(new DeployUnit(1, e.State.UnitsOf(1).Single().Id, [new Vector2(30, 5)]));
        e.Submit(new DeployUnit(0, mine[1].Id, [new Vector2(30, 40)]));

        // Battle round 1 starts with a marker decision for player 0.
        var pm = Assert.IsType<PendingMarker>(e.Pending);
        Assert.Equal(0, e.Decider);
        Assert.False(e.Submit(new PlaceMarker(0, new Vector2(30, 10))).Ok); // too close to the enemy
        Assert.True(e.Submit(new PlaceMarker(0, new Vector2(10, 25))).Ok);
        Assert.Single(e.State.Markers);

        AdvanceUntil(e, () => e.State.BattleRound == 2 && e.State.Phase == Phase.Movement && e.State.ActivePlayer == 0);
        var unit = mine[0];
        Assert.NotNull(e.ValidateArrival(unit, [new Vector2(50, 30)])); // far from the Gnawhole
        Assert.Null(e.ValidateArrival(unit, [new Vector2(12, 27)]));
        Assert.True(e.Submit(new ArriveFromReserve(0, unit.Id, [new Vector2(12, 27)])).Ok);
    }

    [Fact]
    public void AiUsesReservesInRealArmies()
    {
        var dir = DataLoader.FindDataDirectory();
        var factions = DataLoader.LoadAllFactions(dir);
        var plan = DataLoader.LoadAllBattleplans(dir).First();
        var sce = factions.First(f => f.Id == "stormcast-eternals");
        var skv = factions.First(f => f.Id == "skaven");
        string[] a = ["lord-imperatant", "annihilators", "vindictors", "liberators", "prosecutors"];
        string[] b = ["clawlord", "clanrats", "stormvermin", "rat-ogors", "doom-flayers"];
        int arrivals = 0, gnawholes = 0, reserved = 0;
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
            reserved += e.State.Log.Count(l => l.Contains("in reserve ("));
            arrivals += e.State.Log.Count(l => l.Contains(" arrives from "));
            gnawholes += e.State.Log.Count(l => l.Contains("places a Gnawhole"));
        }
        Assert.True(reserved > 0, "no units were put in reserve");
        Assert.True(arrivals > 0, "no units arrived from reserve");
        Assert.True(gnawholes > 0, "no Gnawholes were placed");
    }
}
