using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

[Collection("RoadStatics")]
public class PoiArrivalClearanceTests : IDisposable
{
    private sealed class Flat : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        { weight = 0; width = 0; }
    }
    private readonly ValheimWorldScope _scope = new ValheimWorldScope();
    private readonly Func<IEnumerable<RoadLocationProtection.Footprint>?>? previousSource = RoadLocationProtection.Source;
    private readonly Flat world = new();

    public PoiArrivalClearanceTests()
    {
        _scope.WithWorld(world);
        RoadSpatialGrid.Clear();
        RoadLocationProtection.Source = null;
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0, 0), 12f) });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnAngledArrivalAtTheTrimmedEdgeRemainsBuildable(bool reverse)
    {
        // Radius 12 + half-width 2 + blend margin 2 = the trimmer's 16 m.
        // This approach grazes the extra margin but never enters the location.
        var path = new List<Vector2> { new(0, 40), new(-16, 0) };
        if (reverse) path.Reverse();
        // The location the arrival is planned to, by identity: its end at the trimmed edge.
        var toLocation = reverse ? new RoadLocationProtection.Targets(new Vector2(0, 0), null) : new RoadLocationProtection.Targets(null, new Vector2(0, 0));
        Assert.True(RoadLocationProtection.BlocksSegment(path[0], path[1], 4f, default));
        Assert.False(RoadLocationProtection.BlocksSegment(path[0], path[1], 0f, default));
        PlainRoads.WithModDefaults(() =>
            Assert.NotNull(RoadSpatialGrid.PlanRoadPath(path, 4f, world, locations: toLocation)));
    }

    [Fact]
    public void AnArrivalDoesNotExemptAThirdLocation()
    {
        RoadLocationProtection.Set(new[] {
            new RoadLocationProtection.Footprint(new Vector2(0, 0), 12f),
            new RoadLocationProtection.Footprint(new Vector2(-8, 20), 2f) });
        var path = new List<Vector2> { new(0, 40), new(-16, 0) };
        Assert.Null(RoadSpatialGrid.PlanRoadPath(path, 4f, world, locations: new RoadLocationProtection.Targets(null, new Vector2(0, 0))));
    }

    [Fact]
    public void EvenAnArrivalCannotCrossTheDestinationItself()
    {
        var path = new List<Vector2> { new(40, 0), new(-16, 0) };
        Assert.Null(RoadSpatialGrid.PlanRoadPath(path, 4f, world, locations: new RoadLocationProtection.Targets(null, new Vector2(0, 0))));
    }

    [Fact]
    public void TrimmedArrivalsWorkAtDifferentWorldCoordinatesAndHeadings()
    {
        // Radial trimming uses floats at large coordinates. Exercise the
        // actual planner, not just the endpoint classification predicate.
        foreach (float offset in new[] { -6000f, 0f, 6000f })
        foreach (int degrees in new[] { 13, 71, 127, 193, 251, 317 })
        {
            var centre = new Vector2(offset, -offset);
            float angle = degrees * Mathf.PI / 180f;
            Vector2 Rotate(Vector2 p) => centre + new Vector2(
                p.x * Mathf.Cos(angle) - p.y * Mathf.Sin(angle),
                p.x * Mathf.Sin(angle) + p.y * Mathf.Cos(angle));
            RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(centre, 12f) });
            var path = new List<Vector2> { Rotate(new Vector2(0, 40)), Rotate(new Vector2(-16, 0)) };
            Assert.NotNull(RoadSpatialGrid.PlanRoadPath(path, 4f, world, locations: new RoadLocationProtection.Targets(null, centre)));
        }
    }

    [Fact]
    public void MovingAPlanEndNearAThirdLocationCannotGrantAnArrivalAllowance()
    {
        // The original input skirted the location well away from its clearance.
        // A changed end now grazes the margin, but it is not our destination.
        Assert.True(RoadLocationProtection.BlocksFinalSegment(new Vector2(0, 40), new Vector2(-16, 0), 4f,
            new Vector2(0, 40), new Vector2(-30, 0), new RoadLocationProtection.Targets(null, new Vector2(-30, 0)), out var hit));
        Assert.True(hit.HasValue);
        Assert.Equal(12f, hit!.Value.Radius);
    }

    [Fact]
    public void RefusalNamesTheSegmentAndTheActualBlockingLocation()
    {
        var lines = _scope.CaptureLog();
        AnArrivalDoesNotExemptAThirdLocation();
        string detail = Assert.Single(lines.Where(s => s.StartsWith("POI clearance refused trial:")));
        Assert.Contains("location=(-8.00,20.00) radius=2.00", detail);
        Assert.Contains("segment=(", detail);
        Assert.Contains("input=(0.00,40.00)->(-16.00,0.00)", detail);
        Assert.Contains("Trial refusal, not necessarily a lost road", detail);
    }

    public void Dispose()
    {
        RoadSpatialGrid.Clear();
        RoadLocationProtection.Reset();
        RoadLocationProtection.Source = previousSource;
        _scope.Dispose();
    }
}
