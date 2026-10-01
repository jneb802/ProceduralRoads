using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Tests for the road cross-section profile — the single source of truth for
/// leveling and paint, including the level-wider-than-paint guarantee.
/// </summary>
public class RoadProfileTests
{
    private const float Width = 4f;
    private const float HalfWidth = Width * 0.5f;

    [Fact]
    public void FlatCoreIsFullyLeveledAndSolidlyPainted()
    {
        float core = HalfWidth * RoadConstants.RoadFlatCoreRatio;
        for (float d = 0f; d <= core; d += core / 4f)
        {
            Assert.Equal(1f, RoadProfile.LevelBlend(d, Width));
            Assert.Equal(1f, RoadProfile.PaintStrength(d, Width));
        }
    }

    [Fact]
    public void PaintEndsStrictlyInsideLeveledFootprint()
    {
        // Between the paint edge and the road edge there must be a leveled
        // but unpainted verge — the level-wide/paint-narrow rule.
        float paintEdge = HalfWidth * RoadConstants.RoadPaintOuterRatio;
        float vergeSample = (paintEdge + HalfWidth) * 0.5f;

        Assert.Equal(0f, RoadProfile.PaintStrength(vergeSample, Width));
        Assert.True(RoadProfile.LevelBlend(vergeSample, Width) > 0.2f,
            "Verge should still be meaningfully leveled");
    }

    [Fact]
    public void BothCurvesFallMonotonicallyToZero()
    {
        float outer = HalfWidth + RoadConstants.TerrainBlendMargin;
        float prevLevel = float.MaxValue, prevPaint = float.MaxValue;

        for (float d = 0f; d <= outer + 0.5f; d += 0.05f)
        {
            float level = RoadProfile.LevelBlend(d, Width);
            float paint = RoadProfile.PaintStrength(d, Width);

            Assert.True(level <= prevLevel + 0.0001f, $"LevelBlend rose at {d:F2}");
            Assert.True(paint <= prevPaint + 0.0001f, $"PaintStrength rose at {d:F2}");
            Assert.True(paint <= level + 0.0001f,
                $"Paint ({paint:F3}) exceeded leveling ({level:F3}) at {d:F2}m");

            prevLevel = level;
            prevPaint = paint;
        }

        Assert.Equal(0f, RoadProfile.LevelBlend(outer, Width));
        Assert.Equal(0f, RoadProfile.PaintStrength(outer, Width));
    }

    [Fact]
    public void CurvesAreContinuousAtBandBoundaries()
    {
        // No visible seams: sample densely and reject any jump larger than
        // what a smooth curve can produce over the step.
        float outer = HalfWidth + RoadConstants.TerrainBlendMargin;
        const float step = 0.01f;
        float prevLevel = RoadProfile.LevelBlend(0f, Width);
        float prevPaint = RoadProfile.PaintStrength(0f, Width);

        for (float d = step; d <= outer + 0.1f; d += step)
        {
            float level = RoadProfile.LevelBlend(d, Width);
            float paint = RoadProfile.PaintStrength(d, Width);
            Assert.True(Mathf.Abs(level - prevLevel) < 0.05f, $"LevelBlend jump at {d:F2}");
            Assert.True(Mathf.Abs(paint - prevPaint) < 0.08f, $"PaintStrength jump at {d:F2}");
            prevLevel = level;
            prevPaint = paint;
        }
    }

    [Fact]
    public void ProfileScalesWithRoadWidth()
    {
        // A wider road has a wider core and wider paint, but the ordering
        // core < paint edge < half width < leveled edge holds at any width.
        foreach (float width in new[] { 2f, 4f, 8f, 10f })
        {
            float halfWidth = width * 0.5f;
            float core = halfWidth * RoadConstants.RoadFlatCoreRatio;
            float paintEdge = halfWidth * RoadConstants.RoadPaintOuterRatio;

            Assert.True(core < paintEdge && paintEdge < halfWidth);
            Assert.Equal(1f, RoadProfile.PaintStrength(core, width));
            Assert.Equal(0f, RoadProfile.PaintStrength(halfWidth, width));
            Assert.True(RoadProfile.LevelBlend(halfWidth, width) > 0f,
                "Road edge should still be partially leveled");
        }
    }
}
