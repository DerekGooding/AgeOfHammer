using System.Numerics;
using AgeOfHammer.Core.AI;
using AgeOfHammer.Core.Data;
using AgeOfHammer.Core.Dice;
using AgeOfHammer.Core.Engine;
using AgeOfHammer.Core.State;
using static AgeOfHammer.Core.Tests.TestData;

namespace AgeOfHammer.Core.Tests;

public class DeploymentTests
{
    [Fact]
    public void PlayersAlternateAndMustDeployInTerritory()
    {
        var w = Scroll("w");
        var e = GameEngine.Create(OpenField(), Faction("a", w), ["w", "w"], Faction("b", w), ["w"], new ScriptedDice());
        Assert.Equal(Phase.Deployment, e.State.Phase);
        Assert.Equal(0, e.Decider);
        var u0 = e.State.UnitsOf(0).ToList();

        var outside = e.Submit(new DeployUnit(0, u0[0].Id, [new Vector2(30, 5)]));
        Assert.False(outside.Ok);

        Assert.True(e.Submit(new DeployUnit(0, u0[0].Id, [new Vector2(30, 40)])).Ok);
        Assert.Equal(1, e.Decider);
        var u1 = e.State.UnitsOf(1).Single();
        Assert.True(e.Submit(new DeployUnit(1, u1.Id, [new Vector2(30, 5)])).Ok);
        // Player 1 finished first; player 0 deploys the rest.
        Assert.Equal(0, e.Decider);
        Assert.True(e.Submit(new DeployUnit(0, u0[1].Id, [new Vector2(20, 40)])).Ok);

        Assert.Equal(Phase.Hero, e.State.Phase);
        Assert.Equal(1, e.State.BattleRound);
        Assert.Equal(1, e.State.ActivePlayer); // finished deploying first -> goes first
        Assert.Equal(GameEngine.CommandPointsPerRound, e.State.Players[0].CommandPoints);
    }
}

public class MovementTests
{
    [Fact]
    public void NormalMoveCannotExceedMoveCharacteristic()
    {
        var w = Scroll("w", move: 5);
        var e = DeployedEngine(new ScriptedDice(), [w], [new(10, 30)], [w], [new(50, 5)]);
        AdvanceTo(e, Phase.Movement);
        var unit = e.State.UnitsOf(e.State.ActivePlayer).Single();
        Assert.True(e.Submit(new DeclareMove(e.State.ActivePlayer, unit.Id, MoveKind.Normal)).Ok);
        var start = unit.Models[0].Position;
        Assert.False(e.Submit(new CompleteMove(e.State.ActivePlayer, [start + new Vector2(6, 0)])).Ok);
        Assert.True(e.Submit(new CompleteMove(e.State.ActivePlayer, [start + new Vector2(5, 0)])).Ok);
        Assert.Equal(start + new Vector2(5, 0), unit.Models[0].Position);
    }

    [Fact]
    public void NormalMoveCannotEndInCombatRange()
    {
        var w = Scroll("w", move: 10);
        var e = DeployedEngine(new ScriptedDice(), [w], [new(10, 20)], [w], [new(20, 20)]);
        AdvanceTo(e, Phase.Movement);
        int me = e.State.ActivePlayer;
        var unit = e.State.UnitsOf(me).Single();
        var enemy = e.State.UnitsOf(1 - me).Single();
        e.Submit(new DeclareMove(me, unit.Id, MoveKind.Normal));
        var toward = Vector2.Normalize(enemy.Models[0].Position - unit.Models[0].Position);
        // Bases are 32mm (~0.63" radius each). Ending 2" away is within 3" combat range.
        float centreGap = Vector2.Distance(unit.Models[0].Position, enemy.Models[0].Position);
        var bad = unit.Models[0].Position + toward * (centreGap - 1.26f - 2f);
        var r = e.Submit(new CompleteMove(me, [bad]));
        Assert.False(r.Ok);
        Assert.Contains("combat range", r.Error);
    }

    [Fact]
    public void RunAddsD6AndPreventsShootingAndCharging()
    {
        var w = Scroll("w", move: 5, weapons: [Ranged(), Melee()]);
        var dice = new ScriptedDice();
        var e = DeployedEngine(dice, [w], [new(10, 30)], [w], [new(10, 10)]);
        AdvanceTo(e, Phase.Movement);
        int me = e.State.ActivePlayer;
        var unit = e.State.UnitsOf(me).Single();
        dice.Add(4);
        e.Submit(new DeclareMove(me, unit.Id, MoveKind.Run));
        var pm = Assert.IsType<PendingMove>(e.Pending);
        Assert.Equal(9, pm.Allowance);
        e.Submit(new CancelMove(me));
        Assert.True(unit.Ran);
        Assert.False(e.CanShootBasic(unit));
        Assert.False(e.CanChargeBasic(unit));
    }

    [Fact]
    public void AtTheDoubleRunsSixAndCostsCp()
    {
        var w = Scroll("w", move: 5);
        var e = DeployedEngine(new ScriptedDice(), [w], [new(10, 30)], [w], [new(50, 5)]);
        AdvanceTo(e, Phase.Movement);
        int me = e.State.ActivePlayer;
        var unit = e.State.UnitsOf(me).Single();
        int cp = e.State.Players[me].CommandPoints;
        Assert.True(e.Submit(new DeclareMove(me, unit.Id, MoveKind.Run, AtTheDouble: true)).Ok);
        Assert.Equal(11, ((PendingMove)e.Pending!).Allowance);
        Assert.Equal(cp - 1, e.State.Players[me].CommandPoints);
    }

    [Fact]
    public void RetreatInflictsD3MortalDamage()
    {
        var w = Scroll("w", health: 5, move: 5);
        var dice = new ScriptedDice();
        var e = DeployedEngine(dice, [w], [new(10, 20)], [w], [new(12, 20)]);
        AdvanceTo(e, Phase.Movement);
        int me = e.State.ActivePlayer;
        var unit = e.State.UnitsOf(me).Single();
        Assert.True(e.State.InCombat(unit));
        Assert.False(e.CanNormalMove(unit));
        dice.Add(2);
        Assert.True(e.Submit(new DeclareMove(me, unit.Id, MoveKind.Retreat)).Ok);
        Assert.Equal(2, unit.Models[0].Damage);
        Assert.True(unit.Retreated);
    }
}

public class ChargeAndCombatTests
{
    [Fact]
    public void ChargeMustEndWithinHalfInch()
    {
        var w = Scroll("w");
        var dice = new ScriptedDice();
        var e = DeployedEngine(dice, [w], [new(10, 20)], [w], [new(18, 20)]);
        AdvanceTo(e, Phase.Charge);
        int me = e.State.ActivePlayer;
        var unit = e.State.UnitsOf(me).Single();
        var enemy = e.State.UnitsOf(1 - me).Single();
        dice.Add(4, 4); // 8" charge
        Assert.True(e.Submit(new DeclareCharge(me, unit.Id)).Ok);
        var pm = Assert.IsType<PendingMove>(e.Pending);
        Assert.Equal(8, pm.Allowance);

        // Moving only 2" doesn't reach.
        var dir = Vector2.Normalize(enemy.Models[0].Position - unit.Models[0].Position);
        Assert.False(e.Submit(new CompleteMove(me, [unit.Models[0].Position + dir * 2])).Ok);

        var pos = Placement.AutoCharge(e, pm, enemy);
        Assert.NotNull(pos);
        Assert.True(e.Submit(new CompleteMove(me, pos!)).Ok);
        Assert.True(unit.Charged);
        Assert.True(e.State.InCombat(unit));
    }

    [Fact]
    public void FailedChargeWithNoRerollEndsImmediately()
    {
        var w = Scroll("w");
        var dice = new ScriptedDice();
        var e = DeployedEngine(dice, [w], [new(10, 20)], [w], [new(21, 20)]);
        AdvanceTo(e, Phase.Charge);
        int me = e.State.ActivePlayer;
        e.State.Players[me].CommandPoints = 0; // no Forward to Victory
        var unit = e.State.UnitsOf(me).Single();
        dice.Add(1, 1);
        Assert.True(e.Submit(new DeclareCharge(me, unit.Id)).Ok);
        Assert.Null(e.Pending);
        Assert.False(unit.Charged);
        Assert.False(e.CanCharge(unit));
    }

    [Fact]
    public void ForwardToVictoryRerollsTheCharge()
    {
        var w = Scroll("w");
        var dice = new ScriptedDice();
        var e = DeployedEngine(dice, [w], [new(10, 20)], [w], [new(18, 20)]);
        AdvanceTo(e, Phase.Charge);
        int me = e.State.ActivePlayer;
        var unit = e.State.UnitsOf(me).Single();
        dice.Add(1, 1, 6, 6);
        e.Submit(new DeclareCharge(me, unit.Id));
        Assert.True(((PendingMove)e.Pending!).CanReroll);
        Assert.True(e.Submit(new RerollCharge(me)).Ok);
        Assert.Equal(12, ((PendingMove)e.Pending!).Allowance);
    }

    [Fact]
    public void CombatAlternatesStartingWithActivePlayer()
    {
        var w = Scroll("w", health: 10, save: 7, weapons: [Melee(attacks: "1")]);
        var dice = new SeededDiceRoller(1);
        var e = DeployedEngine(dice, [w, w], [new(10, 20), new(30, 20)], [w, w], [new(12, 20), new(32, 20)]);
        AdvanceTo(e, Phase.Combat);
        int active = e.State.ActivePlayer;
        e.State.Players[0].CommandPoints = e.State.Players[1].CommandPoints = 0; // no All-out Defence prompts
        Assert.Equal(active, e.Decider);
        var mine = e.PickableFighters(active).First();
        Assert.True(e.Submit(new Fight(active, mine.Id, e.FightTargets(mine).First().Id)).Ok);
        Assert.Equal(1 - active, e.Decider);
        var theirs = e.PickableFighters(1 - active).First();
        Assert.True(e.Submit(new Fight(1 - active, theirs.Id, e.FightTargets(theirs).First().Id)).Ok);
        Assert.Equal(active, e.Decider);
    }

    [Fact]
    public void StrikeFirstUnitsFightBeforeOthers()
    {
        var normal = Scroll("normal", health: 10, save: 7);
        var fast = Scroll("fast", health: 10, save: 7);
        fast.Abilities.Add(new AbilityDef { Name = "Quick", Modifiers = [new PassiveModifierDef { Stat = "StrikeFirst", Value = 1 }] });
        var e = DeployedEngine(new SeededDiceRoller(3), [normal], [new(10, 20)], [fast], [new(12, 20)]);
        AdvanceTo(e, Phase.Combat);
        e.State.Players[0].CommandPoints = e.State.Players[1].CommandPoints = 0;
        // Whoever is active, the strike-first unit (player 1) must pick first.
        Assert.Equal(1, e.Decider);
        Assert.Empty(e.PickableFighters(0));
    }

    [Fact]
    public void AllOutDefencePromptsDefender()
    {
        var w = Scroll("w", health: 10, save: 4, weapons: [Melee(attacks: "2")]);
        var e = DeployedEngine(new SeededDiceRoller(5), [w], [new(10, 20)], [w], [new(12, 20)]);
        AdvanceTo(e, Phase.Combat);
        int active = e.State.ActivePlayer;
        var mine = e.PickableFighters(active).Single();
        var target = e.FightTargets(mine).Single();
        Assert.True(e.Submit(new Fight(active, mine.Id, target.Id)).Ok);
        var pd = Assert.IsType<PendingDefence>(e.Pending);
        Assert.Equal(1 - active, e.Decider);
        int cp = e.State.Players[1 - active].CommandPoints;
        Assert.True(e.Submit(new RespondDefence(1 - active, true)).Ok);
        Assert.Equal(cp - 1, e.State.Players[1 - active].CommandPoints);
        Assert.Null(e.Pending);
    }

    [Fact]
    public void CommandCanOnlyBeUsedOncePerPhase()
    {
        var w = Scroll("w", move: 5);
        var e = DeployedEngine(new ScriptedDice(), [w, w], [new(10, 30), new(30, 30)], [w, w], [new(50, 5), new(40, 5)]);
        AdvanceTo(e, Phase.Movement);
        int me = e.State.ActivePlayer;
        var units = e.State.UnitsOf(me).ToList();
        Assert.True(e.Submit(new DeclareMove(me, units[0].Id, MoveKind.Run, AtTheDouble: true)).Ok);
        e.Submit(new CancelMove(me));
        Assert.False(e.CanUseCommand(me, Commands.AtTheDouble, units[1]));
    }
}

public class ScoringTests
{
    [Fact]
    public void ObjectiveControlUsesControlScoresWithin3Inches()
    {
        var big = Scroll("big", unitSize: 3, control: 1);
        var hero = Scroll("hero", control: 2);
        var plan = OpenField();
        plan.Territories = [new RectDef { W = 60, H = 44 }, new RectDef { W = 60, H = 44 }];
        var e = DeployedEngine(new ScriptedDice(), [big], [new(30, 19)], [hero], [new(30, 25)], plan);
        e.UpdateObjectiveControl();
        Assert.Equal(3, e.State.ControlScore(e.State.Objectives[0], 0));
        Assert.Equal(2, e.State.ControlScore(e.State.Objectives[0], 1));
        Assert.Equal(0, e.State.Objectives[0].ControlledBy);
    }

    [Fact]
    public void TiedControlKeepsPreviousController()
    {
        var a = Scroll("a", control: 1);
        var plan = OpenField();
        plan.Territories = [new RectDef { W = 60, H = 44 }, new RectDef { W = 60, H = 44 }];
        var e = DeployedEngine(new ScriptedDice(), [a], [new(30, 20)], [a], [new(30, 24)], plan);
        e.State.Objectives[0].ControlledBy = 1;
        e.UpdateObjectiveControl();
        Assert.Equal(1, e.State.Objectives[0].ControlledBy);
    }

    [Fact]
    public void ScoringAtEndOfTurn()
    {
        var a = Scroll("a", control: 1);
        var plan = OpenField();
        plan.Territories = [new RectDef { W = 60, H = 44 }, new RectDef { W = 60, H = 44 }];
        var e = DeployedEngine(new SeededDiceRoller(7), [a], [new(30, 20)], [a], [new(50, 5)], plan);
        // Player 0 finished deploying first, so takes the first turn.
        AdvanceTo(e, Phase.EndOfTurn);
        Assert.Equal(0, e.State.ActivePlayer);
        e.Submit(new EndPhase(0));
        // Player 0 holds the only objective: 1 (at least one) + 1 (more than opponent) = 2 VP.
        Assert.Equal(2, e.State.Players[0].VictoryPoints);
        // Player 1 scores nothing at the end of their own turn.
        AdvanceTo(e, Phase.EndOfTurn);
        Assert.Equal(1, e.State.ActivePlayer);
        e.Submit(new EndPhase(1));
        Assert.Equal(0, e.State.Players[1].VictoryPoints);
        Assert.Equal(2, e.State.Players[0].VictoryPoints);
    }
}

public class DataTests
{
    [Fact]
    public void ShippedFactionsAndBattleplansLoad()
    {
        var dir = DataLoader.FindDataDirectory();
        var factions = DataLoader.LoadAllFactions(dir);
        Assert.Contains(factions, f => f.Id == "stormcast-eternals");
        Assert.Contains(factions, f => f.Id == "skaven");
        foreach (var f in factions)
        {
            Assert.NotEmpty(f.DefaultArmy);
            foreach (var w in f.Warscrolls) Assert.NotEmpty(w.Weapons);
        }
        var plans = DataLoader.LoadAllBattleplans(dir);
        Assert.NotEmpty(plans);
        Assert.All(plans, p => Assert.Equal(2, p.Territories.Count));
    }

    [Fact]
    public void InvalidFactionIsRejected()
    {
        var json = """{ "id": "x", "defaultArmy": ["missing"], "warscrolls": [] }""";
        Assert.Throws<InvalidDataException>(() => DataLoader.ParseFaction(json));
    }
}

public class SimulationTests
{
    private static GameEngine RunAiVsAi(int seed, int maxActions = 20000)
    {
        var dir = DataLoader.FindDataDirectory();
        var factions = DataLoader.LoadAllFactions(dir);
        var plan = DataLoader.LoadAllBattleplans(dir).First();
        var sce = factions.First(f => f.Id == "stormcast-eternals");
        var skv = factions.First(f => f.Id == "skaven");
        var e = GameEngine.Create(plan, sce, sce.DefaultArmy, skv, skv.DefaultArmy, new SeededDiceRoller(seed));
        var ai = new[] { new AiPlayer(0), new AiPlayer(1) };
        int actions = 0;
        while (e.State.Phase != Phase.GameOver)
        {
            if (++actions > maxActions) throw new InvalidOperationException("Game did not finish:\n" + string.Join("\n", e.State.Log.TakeLast(40)));
            ai[e.Decider].Act(e);
        }
        return e;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void AiVsAiGameCompletes(int seed)
    {
        var e = RunAiVsAi(seed);
        Assert.Equal(Phase.GameOver, e.State.Phase);
        Assert.True(e.State.Winner.HasValue || e.State.IsDraw);
        Assert.DoesNotContain(e.State.Log, l => l.StartsWith("(AI:"));
    }

    public static IEnumerable<object[]> AllFactions() =>
        DataLoader.LoadAllFactions(DataLoader.FindDataDirectory()).Select(f => new object[] { f.Id });

    /// <summary>Every imported army must survive a full AI game (vs Stormcast Eternals) without engine errors.</summary>
    [Theory]
    [MemberData(nameof(AllFactions))]
    public void EveryFactionCanPlayAFullGame(string factionId)
    {
        var dir = DataLoader.FindDataDirectory();
        var factions = DataLoader.LoadAllFactions(dir);
        var plan = DataLoader.LoadAllBattleplans(dir).First();
        var f = factions.First(x => x.Id == factionId);
        var opponent = factions.First(x => x.Id == "stormcast-eternals");
        Assert.NotEmpty(f.DefaultArmy);
        var e = GameEngine.Create(plan, f, f.DefaultArmy, opponent, opponent.DefaultArmy, new SeededDiceRoller(7));
        var ai = new[] { new AiPlayer(0), new AiPlayer(1) };
        int actions = 0;
        while (e.State.Phase != Phase.GameOver)
        {
            Assert.True(++actions < 20000, "game did not finish");
            ai[e.Decider].Act(e);
        }
        Assert.DoesNotContain(e.State.Log, l => l.StartsWith("(AI:"));
    }

    [Fact]
    public void SameSeedGivesSameGame()
    {
        var a = RunAiVsAi(42);
        var b = RunAiVsAi(42);
        Assert.Equal(a.State.Log, b.State.Log);
    }
}
