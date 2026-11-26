using System;
using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>The fill spread (<see cref="RoadTerrainModifier.FillExtras"/>) judges a road point's fill on the ground the
/// terrain writer levels from: inside a placed location's footprint the ground the location leaves
/// (<see cref="PlacedLocations.GroundAt(Vector2)"/>), not the natural ground under it.</summary>
public sealed class FillSpreadGroundTests : IDisposable
{
    private const float Radius = 8f;
    private const float NaturalHeight = 34f;
    /// <summary>The location's landing, raised 6 m over the flat natural ground.</summary>
    private const float LandingHeight = 40f;
    private static readonly Vector2 Centre = new(0f, 0f);
    private readonly ValheimWorldScope scope = new();
    private readonly IDisposable spread = StaticOverride.Keep(() => RoadEarthworkNoise.FillSpread);

    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float x, float z) => NaturalHeight;
    }

    public FillSpreadGroundTests()
    {
        scope.WithWorld(new FlatWorld());
        RoadNetworkGenerator.Reset();
        PlacedLocations.Reset();
        RoadEarthworkNoise.FillSpread = 1f;
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(Centre, Radius) });
        PlacedLocations.Ready(new PlacedLocation
        {
            Record = new PlacedLocationRecord { Centre = Centre },
            Ground = (_, _) => LandingHeight,
        });
    }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        PlacedLocations.Reset();
        spread.Dispose();
        scope.Dispose();
    }

    /// <summary>A road onto the landing, at the landing's height: on the landing it stands on the location's ground (no
    /// fill, no spread), where the natural ground under the footprint read it as 6 m of fill over flat ground and spread
    /// its side slope the widest (4 m) out of the footprint. Away from the location the same road on the same natural
    /// ground keeps its spread, so only the footprint's reading changes.</summary>
    [Fact]
    public void TheFillSpreadReadsAPlacedLocationsGroundInItsFootprint()
    {
        var points = new List<RoadSpatialGrid.RoadPoint>
        {
            new(new Vector2(2f, 0f), 4f, LandingHeight),
            new(new Vector2(200f, 0f), 4f, LandingHeight),
        };
        float[]? extra = RoadTerrainModifier.FillExtras(points, WorldGenerator.instance);
        Assert.NotNull(extra);
        Assert.Equal(0f, extra![0], 3);
        Assert.Equal(RoadEarthworkNoise.FillSpreadMax, extra[1], 3);
    }

    /// <summary>A road point just outside the footprint, its 6 m slope samples reaching onto the raised landing: the ground
    /// it is judged on is not flat (a 6 m step over 12 m), so no spread fans out against the location's edge.</summary>
    [Fact]
    public void AFillBesideAPlacedFootprintIsJudgedOnTheLandingBesideIt()
    {
        var points = new List<RoadSpatialGrid.RoadPoint> { new(new Vector2(10f, 0f), 4f, LandingHeight) };
        Assert.Equal(0f, RoadTerrainModifier.FillExtras(points, WorldGenerator.instance)![0], 3);
    }

    /// <summary>A footprint whose ground is not known (NaN): no spread is judged on it, never a NaN spread.</summary>
    [Fact]
    public void AFootprintWithGroundNotKnownGivesNoSpread()
    {
        PlacedLocations.Reset();
        var points = new List<RoadSpatialGrid.RoadPoint> { new(new Vector2(2f, 0f), 4f, LandingHeight) };
        Assert.Equal(0f, RoadTerrainModifier.FillExtras(points, WorldGenerator.instance)![0]);
    }
}
