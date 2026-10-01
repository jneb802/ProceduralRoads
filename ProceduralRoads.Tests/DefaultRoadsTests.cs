using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The mod's default route and shape settings, held together: the values
/// that were chosen by walking generated worlds, and roads built with all of
/// them on at once.
/// </summary>
public class DefaultRoadsTests
{
    [Fact]
    public void TheDefaultsAreTheChosenOnes()
    {
        var d = PlainRoads.ModDefaults;
        Assert.Equal(150f, d["slope weight"]);
        Assert.Equal(0.5f, d["road reuse"]);
        Assert.Equal(100f, d["turn limit"]);
        Assert.Equal(4f, d["turn weight"]);
        Assert.Equal(3f, d["meander"]);
        Assert.Equal(24f, d["bend radius"]);
        Assert.Equal(6f, d["junction landing"]);
        Assert.Equal(5f, d["pull tolerance"]);
        Assert.Equal(10f, d["sway"]);
        Assert.Equal(6f, d["road snap"]);
        Assert.Equal(14f, d["corridor snap"]);
        Assert.Equal(8f, d["earthwork cap"]);
        foreach (var on in new[] { "quadratic variance", "stair turns", "switchback fallback", "tight switchbacks",
                                   "follow ground", "junction match", "curve checks", "pitches", "pull", "one branch",
                                   "bridge ends to water" })
            Assert.True((bool)d[on], on);
    }

    private static List<Vector2> Straight(float x0, float x1, float step = 8f)
    {
        var path = new List<Vector2>();
        for (float x = x0; x <= x1; x += step) path.Add(new Vector2(x, 0f));
        return path;
    }

    [Fact]
    public void ARoadIsBuiltWithEveryDefaultOnAndSwaysOffTheStraight()
    {
        var world = new SyntheticWorld { HasRiver = false, HasMountain = false };
        WorldGenerator.instance = world;
        RoadSpatialGrid.Clear();
        try
        {
            PlainRoads.WithModDefaults(() =>
            {
                var plan = RoadSpatialGrid.PlanRoadPath(Straight(-200f, 200f), 4f, world);
                Assert.NotNull(plan);
                float furthest = 0f;
                foreach (var p in plan!.Points) furthest = Mathf.Max(furthest, Mathf.Abs(p.y));
                Assert.True(furthest > 1f, $"the road stayed within {furthest:F2} m of the straight line");
                Assert.True(furthest <= RoadWiggle.Amplitude + 0.01f, $"the road swayed {furthest:F2} m");
                Assert.True(RoadSpatialGrid.AddRoadPath(Straight(-200f, 200f), 4f, world));
                Assert.True(RoadSpatialGrid.TotalRoadPoints > 0);
            });
        }
        finally { RoadSpatialGrid.Clear(); WorldGenerator.instance = null; }
    }
}
