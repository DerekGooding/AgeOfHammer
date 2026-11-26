using System.Numerics;
using AgeOfHammer.Core.Dice;
using AgeOfHammer.Core.Geometry;

namespace AgeOfHammer.Core.Tests;

public class DiceExpressionTests
{
    [Theory]
    [InlineData("3", 0, 0, 3)]
    [InlineData("D6", 1, 6, 0)]
    [InlineData("d3", 1, 3, 0)]
    [InlineData("2D6", 2, 6, 0)]
    [InlineData("D3+1", 1, 3, 1)]
    [InlineData("2D3 - 1", 2, 3, -1)]
    public void Parses(string text, int count, int sides, int mod)
    {
        var e = DiceExpression.Parse(text);
        Assert.Equal(new DiceExpression(count, sides, mod), e);
    }

    [Fact]
    public void RollsUsingDice()
    {
        var dice = new ScriptedDice(4, 2);
        Assert.Equal(7, DiceExpression.Parse("2D6+1").Roll(dice));
        Assert.Equal(5, DiceExpression.Parse("5").Roll(dice));
    }

    [Fact]
    public void AverageAndToString()
    {
        Assert.Equal(3.5, DiceExpression.Parse("D6").Average);
        Assert.Equal(3.0, DiceExpression.Parse("D3+1").Average);
        Assert.Equal("2D6+1", DiceExpression.Parse("2d6+1").ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("X")]
    [InlineData("0D6")]
    public void RejectsBadInput(string text) => Assert.ThrowsAny<FormatException>(() => DiceExpression.Parse(text));

    [Fact]
    public void SeededRollerIsDeterministic()
    {
        var a = new SeededDiceRoller(42);
        var b = new SeededDiceRoller(42);
        for (int i = 0; i < 100; i++) Assert.Equal(a.D6(), b.D6());
    }
}

public class GeometryTests
{
    [Fact]
    public void DistanceIsBaseToBase()
    {
        // two 1" radius bases with centres 5" apart are 3" apart
        Assert.Equal(3f, Geo.Distance(new Vector2(0, 0), 1, new Vector2(5, 0), 1), 3);
        Assert.Equal(0f, Geo.Distance(new Vector2(0, 0), 1, new Vector2(1, 0), 1));
        Assert.True(Geo.Overlaps(new Vector2(0, 0), 1, new Vector2(1, 0), 1));
    }

    [Fact]
    public void CoherencyRequiresSingleGroupWithinHalfInch()
    {
        float r = 0.5f;
        var ok = new List<Vector2> { new(0, 0), new(1.4f, 0), new(2.8f, 0) };
        Assert.True(Geo.IsCoherent(ok, r));
        var split = new List<Vector2> { new(0, 0), new(1.4f, 0), new(10, 0), new(11.4f, 0) };
        Assert.False(Geo.IsCoherent(split, r));
        var tooFar = new List<Vector2> { new(0, 0), new(1.6f, 0) };
        Assert.False(Geo.IsCoherent(tooFar, r));
    }

    [Fact]
    public void FormationIsCoherentAndNonOverlapping()
    {
        float r = 25 / 25.4f / 2;
        var pos = Geo.Formation(20, r, new Vector2(30, 22), 10);
        Assert.Equal(20, pos.Count);
        Assert.True(Geo.IsCoherent(pos, r));
        for (int i = 0; i < pos.Count; i++)
            for (int j = i + 1; j < pos.Count; j++)
                Assert.False(Geo.Overlaps(pos[i], r, pos[j], r));
    }

    [Fact]
    public void RectContainsCircleWholly()
    {
        var rect = new RectF(0, 0, 10, 10);
        Assert.True(rect.ContainsCircle(new Vector2(5, 5), 1));
        Assert.False(rect.ContainsCircle(new Vector2(0.5f, 5), 1));
    }

    [Fact]
    public void SegmentCrossesRect()
    {
        var rect = new RectF(4, -1, 2, 2);
        Assert.True(Geo.SegmentIntersectsRect(new Vector2(0, 0), new Vector2(10, 0), rect));
        Assert.False(Geo.SegmentIntersectsRect(new Vector2(0, 5), new Vector2(10, 5), rect));
    }
}
