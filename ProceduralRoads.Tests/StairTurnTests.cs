using System.Collections.Generic;
using UnityEngine;
using Xunit;
namespace ProceduralRoads.Tests;

/// <summary>Staircase turns: tight, with a flat landing carried along both legs.</summary>
[Collection("Batter statics")]
public class StairTurnTests
{
    // A zigzag on knight moves: 17.9 m legs, turns of about 127 degrees.
    private static List<Vector2> KnightZigzag()
    {
        var p = new List<Vector2>();
        for (int i = 0; i <= 6; i++) p.Add(new Vector2(i % 2 == 0 ? 0f : 16f, i * 8f));
        return p;
    }
    private static float Rising(Vector2 v) => 60f + v.y * 0.3f;

    [Fact]
    public void AKnightZigzagHasNoRoomForCurvedTurns()
    {
        Assert.False(RoadSwitchbacks.Shape(KnightZigzag(), 4, out _, out _, Rising, stair: false));
    }

    [Fact]
    public void StaircaseTurnsFitTheSameZigzag()
    {
        Assert.True(RoadSwitchbacks.Shape(KnightZigzag(), 4, out var shaped, out var landing, Rising, stair: true));
        Assert.NotNull(shaped); Assert.NotNull(landing);
        Assert.Equal(shaped!.Count, landing!.Count);
    }

    [Fact]
    public void TheLandingRunsAlongBothLegs()
    {
        Assert.True(RoadSwitchbacks.Shape(KnightZigzag(), 4, out var shaped, out var landing, Rising, stair: true));
        // Every turn's landing is longer than its arc: at least the arc plus
        // most of the configured run on each side (legs permitting).
        float run = 0f, longest = 0f;
        for (int i = 1; i < shaped!.Count; i++)
        {
            if (landing![i] && landing[i - 1]) run += Vector2.Distance(shaped[i - 1], shaped[i]);
            else { longest = Mathf.Max(longest, run); run = 0f; }
        }
        longest = Mathf.Max(longest, run);
        Assert.True(longest >= 2f * RoadSwitchbacks.StairLanding, $"longest landing {longest:F1} m");
    }

    // Two parallel legs at different heights, run the other way along z.
    private static List<Vector2> Legs(float apart) => new() { new(0, 0), new(30, 0), new(30, apart), new(0, apart) };
    private static readonly List<float> Climbing = new() { 60, 66, 68, 74 };

    [Fact]
    public void TodaysRuleKeepsWholeFootprintsApart()
    {
        // 7 m apart: the 8 m footprint rule refuses it.
        Assert.False(RoadSwitchbacks.Separated(Legs(7f), Climbing, 4));
    }

    [Fact]
    public void BuffersMayOverlapWhenNeitherReachesTheOtherSurface()
    {
        // 4 m road, 2 m margin, batter off: surfaces are safe from 6 m apart.
        float saved = RoadTerrainModifier.BatterPerMetre;
        try
        {
            RoadTerrainModifier.BatterPerMetre = 0f;
            Assert.True(RoadSwitchbacks.Separated(Legs(7f), Climbing, 4, surfaceOnly: true));
            Assert.False(RoadSwitchbacks.Separated(Legs(5f), Climbing, 4, surfaceOnly: true));
        }
        finally { RoadTerrainModifier.BatterPerMetre = saved; }
    }

    [Fact]
    public void ABatteredLegReachesFurtherSoMustSitFurtherAway()
    {
        float saved = RoadTerrainModifier.BatterPerMetre;
        try
        {
            RoadTerrainModifier.BatterPerMetre = 1.5f;   // may add up to BATTER_MAX
            Assert.False(RoadSwitchbacks.Separated(Legs(7f), Climbing, 4, surfaceOnly: true));
        }
        finally { RoadTerrainModifier.BatterPerMetre = saved; }
    }

    [Fact]
    public void PointsOfOneLandingMaySitTogether()
    {
        // The same close legs, but every point is one flat landing.
        var landing = new List<bool> { true, true, true, true };
        Assert.True(RoadSwitchbacks.Separated(Legs(3f), new() { 60, 60.4f, 60.6f, 61 }, 4, landing, surfaceOnly: true));
        // Two DIFFERENT landings close together at different heights are not one landing.
        var two = new List<bool> { true, false, true, true };
        Assert.False(RoadSwitchbacks.Separated(Legs(3f), new() { 60, 66, 68, 74 }, 4, two, surfaceOnly: true));
    }
}
