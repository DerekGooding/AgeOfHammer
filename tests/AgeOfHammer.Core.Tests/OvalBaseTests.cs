using System.Numerics;
using AgeOfHammer.Core.Data;
using AgeOfHammer.Core.Dice;
using AgeOfHammer.Core.Engine;
using AgeOfHammer.Core.Geometry;
using AgeOfHammer.Core.State;
using static AgeOfHammer.Core.Tests.TestData;

namespace AgeOfHammer.Core.Tests;

public class OvalBaseTests
{
    private const float MmToInch = 1 / 25.4f;

    [Fact]
    public void RealOvalWarscrollsParseLengthAndWidth()
    {
        var skaven = DataLoader.LoadAllFactions(DataLoader.FindDataDirectory()).First(f => f.Id == "skaven");
        var jezzails = skaven.Get("warplock-jezzails");
        Assert.Equal((60f, 35f), jezzails.OvalMm);
        Assert.Equal(35 * MmToInch / 2, jezzails.BaseRadius, 4);
        Assert.Equal(25 * MmToInch / 2, jezzails.BaseHalfLength, 4);
        var doomwheel = skaven.Get("doomwheel");
        Assert.Equal((105f, 70f), doomwheel.OvalMm);
        Assert.Null(skaven.Get("clanrats").OvalMm); // round 25mm
        Assert.Equal(0, skaven.Get("clanrats").BaseHalfLength);
    }

    [Fact]
    public void StadiumGapsAreMeasuredFromTheRealEdge()
    {
        // Two 60 x 35mm ovals (length 2.36", width 1.38") lying along the X axis.
        var oval = new Base(Vector2.Zero, new Vector2(25 * MmToInch / 2, 0), 35 * MmToInch / 2);
        float length = 60 * MmToInch, width = 35 * MmToInch;
        // End to end, 1" apart.
        Assert.Equal(1f, Geo.Gap(oval, oval.MovedTo(new Vector2(length + 1, 0))), 3);
        // Side by side, 1" apart.
        Assert.Equal(1f, Geo.Gap(oval, oval.MovedTo(new Vector2(0, width + 1))), 3);
        // Overlapping along the length.
        Assert.True(Geo.Overlaps(oval, oval.MovedTo(new Vector2(length - 0.2f, 0))));
        // A round base is a stadium with no length.
        Assert.Equal(Geo.Gap(Vector2.Zero, 1, new Vector2(5, 0), 1), Geo.Gap(Base.Circle(Vector2.Zero, 1), Base.Circle(new Vector2(5, 0), 1)), 4);
    }

    [Fact]
    public void CombatRangeIsMeasuredFromTheTipOfAnOval()
    {
        var wheel = Scroll("wheel", health: 8);
        wheel.BaseSize = "105 × 70mm";
        var rat = Scroll("rat", baseMm: 25);
        // The wheel's tip is 105/2 mm = 2.07" from its centre along X. Put the rat 2.5" beyond the tip:
        // in combat (within 3"), although an equal-area circle (about 86mm across) would put it about 2.9" away.
        float tip = 105 * MmToInch / 2, ratR = 25 * MmToInch / 2;
        var e = DeployedEngine(new ScriptedDice(), [wheel], [new(20, 20)], [rat], [new(20 + tip + 2.5f + ratR, 20)]);
        var w = e.State.UnitsOf(0).Single();
        var r = e.State.UnitsOf(1).Single();
        Assert.Equal(2.5f, GameState.Distance(w, r), 2);
        Assert.True(e.State.InCombat(w));

        // Turned 90 degrees, the same position measures from the oval's side instead.
        w.Facing = MathF.PI / 2;
        Assert.True(GameState.Distance(w, r) > 3f);
    }

    [Fact]
    public void FacingRotatesTheFootprint()
    {
        var unit = new Unit { Id = 1, Owner = 0, Warscroll = new Warscroll { Id = "j", Name = "j", BaseSize = "60 × 35mm", BaseSizeMm = 46 } };
        var flat = unit.BaseExtent;
        unit.Facing = MathF.PI / 2;
        var turned = unit.BaseExtent;
        Assert.Equal(60 * MmToInch, flat.X, 3);
        Assert.Equal(35 * MmToInch, flat.Y, 3);
        Assert.Equal(flat.X, turned.Y, 3);
        Assert.Equal(flat.Y, turned.X, 3);
    }

    [Fact]
    public void OvalUnitsDeployWithoutOverlapAndInCoherency()
    {
        var jezz = Scroll("jezz", unitSize: 3);
        jezz.BaseSize = "60 × 35mm";
        var e = DeployedEngine(new ScriptedDice(), [jezz], [new(20, 20)], [Scroll("foe")], [new(50, 5)]);
        var u = e.State.UnitsOf(0).Single();
        var bases = u.Alive.Select(u.BaseOf).ToList();
        Assert.True(Geo.IsCoherent(bases));
        for (int i = 0; i < bases.Count; i++)
            for (int j = i + 1; j < bases.Count; j++)
                Assert.False(Geo.Overlaps(bases[i], bases[j]));
    }
}
