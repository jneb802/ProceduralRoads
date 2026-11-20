using System;
using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

public sealed class TerrainGatherReachTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    public TerrainGatherReachTests() { RoadNetworkGenerator.Reset(); _scope.WithWorld(new SyntheticWorld()); }
    public void Dispose() { RoadNetworkGenerator.Reset(); _scope.Dispose(); }

    [Theory]
    [InlineData(1f, 0f, 0f)]
    [InlineData(4f, 0f, 0f)]
    [InlineData(12f, 0f, 0f)]
    [InlineData(2f, 0.7f, 0f)]
    [InlineData(4f, 0.7f, 1f)]
    public void ZoneGatherIncludesTheLastStretchReadByTheWriter(float width, float variation, float fill)
    {
        using var settings = StaticOverride.Keep(() => RoadEarthworkNoise.WidthVariation)
            .AndKeep(() => RoadEarthworkNoise.FillSpread).AndKeep(() => RoadEarthworkNoise.Amplitude);
        RoadEarthworkNoise.WidthVariation = variation;
        RoadEarthworkNoise.FillSpread = fill;
        RoadEarthworkNoise.Amplitude = 0f;
        float spread = fill > 0f ? RoadEarthworkNoise.FillSpreadMax : 0f;
        float reach = RoadTerrainModifier.FitReach(width + 2f * spread);
        // Just inside the fit's rounding allowance, beyond the old gather.
        float x = RoadConstants.HalfZoneSize + reach - 0.001f;
        var far = new RoadSpatialGrid.RoadPoint(new Vector2(x, 0f), width, 44f);
        Assert.True(RoadSpatialGrid.Gathers(Vector3.zero, far), $"fit reaches {x}, zone gather drops it");
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(
            new List<Vector2> { far.p, new(31f, 0f) }, new List<float> { far.h, 40f },
            new List<RoadPointDebugInfo> { default, default }, width, x - 31f, false));
        var all = new List<RoadSpatialGrid.RoadPoint> { far, new(new Vector2(31f, 0f), width, 40f) };
        var gathered = RoadSpatialGrid.GetRoadPointsInZone(new Vector2s(0, 0));
        Assert.Contains(gathered, p => p.p == far.p);
        var vertex = new Vector2(32f, 0f);
        float[] extras = { spread, spread };
        float complete = RoadTerrainModifier.ModelledHeight(all, vertex, 40f, extras);
        Assert.NotEqual(RoadTerrainModifier.ModelledHeight(new List<RoadSpatialGrid.RoadPoint> { all[1] }, vertex, 40f, new[] { spread }), complete);
        Assert.Equal(complete, RoadTerrainModifier.ModelledHeight(gathered, vertex, 40f, extras));
    }
}
