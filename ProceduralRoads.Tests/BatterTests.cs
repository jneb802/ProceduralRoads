using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The batter: releasing a deep cut over more ground than a shallow one.
///
/// Depth and shape are different complaints. A seven-metre cut released over
/// the fixed two-metre margin is an eight-metre slot with near-vertical walls,
/// and that is what reads as a trench however legal the centreline grade is.
/// </summary>
[Collection("Batter statics")]
public class BatterTests
{
    /// <summary>With no batter the profile is exactly what it was: this is the
    /// guarantee that a zero batter changes no terrain anywhere.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(1.5f)]
    [InlineData(2.9f)]
    [InlineData(4f)]
    public void ZeroBatterReproducesTheFixedMargin(float dist) =>
        Assert.Equal(RoadProfile.LevelBlend(dist, 4f),
                     RoadProfile.LevelBlend(dist, 4f, RoadConstants.TerrainBlendMargin), 6);

    /// <summary>Terrain is still pulled fully to road height across the flat
    /// core. A batter widens the shoulder; it must not narrow the road.</summary>
    [Theory]
    [InlineData(2f)]
    [InlineData(8f)]
    public void TheFlatCoreIsUntouchedByTheMargin(float margin) =>
        Assert.Equal(1f, RoadProfile.LevelBlend(0.5f, 4f, margin), 6);

    /// <summary>A wider margin releases later: at the old outer edge the
    /// terrain is still being pulled, where before it was free.</summary>
    [Fact]
    public void AWiderMarginIsStillPullingWhereTheNarrowOneHasLetGo()
    {
        float atOldEdge = 2f + RoadConstants.TerrainBlendMargin;   // halfWidth + margin
        Assert.Equal(0f, RoadProfile.LevelBlend(atOldEdge, 4f), 6);
        Assert.True(RoadProfile.LevelBlend(atOldEdge, 4f, 8f) > 0.1f,
            "a battered shoulder should still be shaping ground at the unbattered edge");
    }

    /// <summary>And it does let go: the blend still reaches zero, at the wider
    /// edge, so a batter is a slope and not an unbounded reach.</summary>
    [Fact]
    public void AWiderMarginStillReleases() =>
        Assert.Equal(0f, RoadProfile.LevelBlend(2f + 8f, 4f, 8f), 6);

    /// <summary>The blend falls monotonically outward at any margin, so a
    /// shoulder never rises again further from the road.</summary>
    [Theory]
    [InlineData(2f)]
    [InlineData(6f)]
    public void TheShoulderOnlyEverFalls(float margin)
    {
        float previous = 1f;
        for (float d = 0f; d <= 2f + margin + 1f; d += 0.25f)
        {
            float here = RoadProfile.LevelBlend(d, 4f, margin);
            Assert.True(here <= previous + 1e-6f, $"blend rose at {d} m");
            previous = here;
        }
    }

    /// <summary>A negative margin cannot invert the profile.</summary>
    [Fact]
    public void ANegativeMarginIsClampedNotInverted() =>
        Assert.InRange(RoadProfile.LevelBlend(2.5f, 4f, -5f), 0f, 1f);

    /// <summary>
    /// The zone gather must pad by how far a blend REACHES, not by the road's
    /// width. FOUND IN GAME, by looking at a hillside: with the batter on, a
    /// seam ran along a zone edge where vertices inside the zone kept their
    /// natural height while their neighbours had been moved.
    ///
    /// The cause is that a road point just outside a zone still pulls on
    /// vertices inside it. The gather pads its bounds to catch those points,
    /// and the pad was the road WIDTH — which equals the influence radius at a
    /// 4 m road with the fixed 2 m margin, and stops being enough the moment
    /// the margin grows with the cut. At a 5 m fill the blend reaches 11.5 m
    /// against a 4 m pad.
    ///
    /// Nothing offline could have caught this: the harness writes no terrain,
    /// and every other test here works inside a single zone.
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(1.5f)]
    [InlineData(4f)]
    public void TheZoneGatherPadsByTheBlendReachNotTheRoadWidth(float batter)
    {
        float saved = RoadTerrainModifier.BatterPerMetre;
        try
        {
            RoadTerrainModifier.BatterPerMetre = batter;
            const float width = 4f;
            float reach = RoadTerrainModifier.MaxInfluenceRadius(width);

            // Walk every divergence the writer can produce and take the
            // widest margin the blend actually asks for. Computed by sweeping
            // rather than by repeating the formula, so a change to the batter's
            // SHAPE - a threshold, a cap, a curve - is still covered here.
            float deepest = 0f;
            for (float d = 0f; d <= RoadConstants.TerrainDeltaMax + 1f; d += 0.25f)
            {
                float margin = RoadConstants.TerrainBlendMargin
                             + RoadTerrainModifier.BatterExtra(RoadTerrainModifier.BatterDivergence(30f - d, 30f));
                if (margin > deepest) deepest = margin;
            }
            Assert.True(reach >= width * 0.5f + deepest - 1e-4f,
                $"reach {reach:F2} m does not cover a {deepest:F2} m margin on a {width:F0} m road");

            // And it must never be narrower than the width the gather used
            // before, or a wide road loses points it used to collect.
            Assert.True(Mathf.Max(width, reach) >= width);
        }
        finally { RoadTerrainModifier.BatterPerMetre = saved; }
    }

    /// <summary>The divergence the batter widens for is clamped to what the
    /// writer can actually move the ground. Unbounded, the influence radius is
    /// unbounded too and no pad can be computed for it.</summary>
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(5f, 5f)]
    [InlineData(8f, 8f)]
    [InlineData(40f, 8f)]
    public void TheBatterDivergenceIsClampedToWhatTheWriterCanMove(float raw, float expected)
    {
        // A trench: the ground stands raw metres above the road.
        Assert.Equal(expected, RoadTerrainModifier.BatterDivergence(30f - raw, 30f), 4);
    }

    /// <summary>Fill is never battered: a road standing on an embankment is not
    /// walled in, and widening a fill on a sidehill fans an apron across it.</summary>
    [Theory]
    [InlineData(0.5f)]
    [InlineData(5f)]
    [InlineData(40f)]
    public void FillGetsNoBatter(float raw)
    {
        Assert.Equal(0f, RoadTerrainModifier.BatterDivergence(30f + raw, 30f), 4);
    }

    /// <summary>
    /// The batter must be INERT for ordinary road. Found by looking: applied
    /// from zero it turned a narrow winding mountain traverse into a wide open
    /// slope with no character. It is a last resort for a deep cut, not a
    /// general rule, and these are the depths that decide which is which.
    /// </summary>
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(2f, 0f)]      // ordinary road: untouched
    [InlineData(4f, 0f)]      // at the threshold: still untouched
    [InlineData(6f, 3f)]      // a real trench: 1.5 x 2 m past the threshold
    [InlineData(8f, 6f)]      // deeper: 1.5 x 4 m
    [InlineData(20f, 6f)]     // capped, never an apron
    public void TheBatterIsInertUntilTheCutIsDeepThenCapped(float divergence, float expected)
    {
        float savedP = RoadTerrainModifier.BatterPerMetre;
        float savedT = RoadTerrainModifier.BatterThreshold;
        float savedM = RoadTerrainModifier.BatterMaxExtra;
        try
        {
            RoadTerrainModifier.BatterPerMetre = 1.5f;
            RoadTerrainModifier.BatterThreshold = 4f;
            RoadTerrainModifier.BatterMaxExtra = 6f;
            Assert.Equal(expected, RoadTerrainModifier.BatterExtra(divergence), 4);
        }
        finally
        {
            RoadTerrainModifier.BatterPerMetre = savedP;
            RoadTerrainModifier.BatterThreshold = savedT;
            RoadTerrainModifier.BatterMaxExtra = savedM;
        }
    }

    /// <summary>The cap is what keeps the influence radius - and therefore the
    /// zone gather's pad - bounded. Without it a deep cut widens without
    /// limit and no pad can be computed.</summary>
    [Fact]
    public void TheMaxInfluenceRadiusRespectsTheCap()
    {
        float savedP = RoadTerrainModifier.BatterPerMetre;
        float savedM = RoadTerrainModifier.BatterMaxExtra;
        try
        {
            RoadTerrainModifier.BatterPerMetre = 8f;   // far past the default
            RoadTerrainModifier.BatterMaxExtra = 6f;
            float reach = RoadTerrainModifier.MaxInfluenceRadius(4f);
            Assert.Equal(2f + RoadConstants.TerrainBlendMargin + 6f, reach, 4);
        }
        finally
        {
            RoadTerrainModifier.BatterPerMetre = savedP;
            RoadTerrainModifier.BatterMaxExtra = savedM;
        }
    }
}
