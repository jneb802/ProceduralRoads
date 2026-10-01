using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
namespace ProceduralRoads.Tests;

/// <summary>Staircase-tight switchbacks built by shaping.</summary>
[Collection("Batter statics")]
public class TightSwitchbackTests
{
    // A U on the grid: east 40 m, north 8 m, west 40 m. Ground rises to the
    // north, so the upper leg is 2.4 m above the lower: a climbing switchback.
    private static List<Vector2> U() => new() { new(0, 0), new(40, 0), new(40, 8), new(0, 8) };
    private static float North(Vector2 v) => 60f + 0.3f * v.y;

    [Fact]
    public void TheUBecomesAFlatLandingWithNarrowLegsBesideIt()
    {
        Assert.True(RoadSwitchbacks.Shape(U(), 4, out var shaped, out var landing, North, stair: true, bend: 0f, tight: true));
        var widths = RoadSwitchbacks.LastWidths;
        Assert.NotNull(widths);
        Assert.Equal(shaped!.Count, widths!.Count);
        // The landing's ends: the legs' centres 2 m apart at the apex.
        Assert.Contains(shaped, p => Vector2.Distance(p, new Vector2(40, 3)) < 0.01f);
        Assert.Contains(shaped, p => Vector2.Distance(p, new Vector2(40, 5)) < 0.01f);
        Assert.Equal(2f, widths.Min(), 3);
        Assert.Equal(4f, widths[0], 3);
        Assert.Equal(4f, widths[widths.Count - 1], 3);
        // The landing itself is flagged flat.
        int at3 = shaped.FindIndex(p => Vector2.Distance(p, new Vector2(40, 3)) < 0.01f);
        Assert.True(landing![at3]);
    }

    [Fact]
    public void WithoutTheSwitchTheUKeepsTodaysTurn()
    {
        Assert.True(RoadSwitchbacks.Shape(U(), 4, out var shaped, out _, North, stair: true, bend: 0f, tight: false));
        Assert.Null(RoadSwitchbacks.LastWidths);
        // Today's turn runs straight along the lower leg until its 2 m arc; the
        // tight one angles in toward the landing from 12 m back.
        Assert.DoesNotContain(shaped!, p => p.x < 37f && p.y > 0.5f && p.y < 2.5f);
        Assert.True(RoadSwitchbacks.Shape(U(), 4, out var tightShape, out _, North, stair: true, bend: 0f, tight: true));
        Assert.Contains(tightShape!, p => p.x < 37f && p.y > 0.5f && p.y < 2.5f);
    }

    [Fact]
    public void NarrowLegsNeedLessSeparationThanFullOnes()
    {
        // Two parallel legs 5 m apart at different heights: too close at full
        // 4 m width (needs 6 m with batter off), far enough at 2 m (needs 4 m).
        var pts = new List<Vector2> { new(0, 0), new(30, 0), new(30, 5), new(0, 5) };
        var h = new List<float> { 60, 66, 68, 74 };
        float saved = RoadTerrainModifier.BatterPerMetre;
        try
        {
            RoadTerrainModifier.BatterPerMetre = 0f;
            Assert.False(RoadSwitchbacks.Separated(pts, h, 4, surfaceOnly: true));
            Assert.True(RoadSwitchbacks.Separated(pts, h, 4, surfaceOnly: true, widths: new List<float> { 2, 2, 2, 2 }));
        }
        finally { RoadTerrainModifier.BatterPerMetre = saved; }
    }
}
