using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

[Collection("RoadStatics")]
public class PoiArrivalClearanceTests : IDisposable
{
    private sealed class Flat : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f;
        public override Heightmap.Biome GetBiome(float x, float z) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        { weight = 0; width = 0; }
    }
    private readonly WorldGenerator? previousWorld = WorldGenerator.instance;
    private readonly Func<IEnumerable<RoadSiteProtection.Footprint>?>? previousSource = RoadSiteProtection.Source;
    private readonly Flat world = new();

    public PoiArrivalClearanceTests()
    {
        WorldGenerator.instance = world;
        RoadSpatialGrid.Clear();
        RoadSiteProtection.Source = null;
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(new Vector2(0, 0), 12f) });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnAngledArrivalAtTheTrimmedEdgeRemainsBuildable(bool reverse)
    {
        // Radius 12 + half-width 2 + blend margin 2 = the trimmer's 16 m.
        // This approach grazes the extra margin but never enters the site.
        var path = new List<Vector2> { new(0, 40), new(-16, 0) };
        if (reverse) path.Reverse();
        Assert.True(RoadSiteProtection.BlocksSegment(path[0], path[1], 4f, null, null));
        Assert.False(RoadSiteProtection.BlocksSegment(path[0], path[1], 0f, null, null));
        PlainRoads.WithModDefaults(() =>
            Assert.NotNull(RoadSpatialGrid.PlanRoadPath(path, 4f, world)));
    }

    [Fact]
    public void AnArrivalDoesNotExemptAThirdSite()
    {
        RoadSiteProtection.Set(new[] {
            new RoadSiteProtection.Footprint(new Vector2(0, 0), 12f),
            new RoadSiteProtection.Footprint(new Vector2(-8, 20), 2f) });
        var path = new List<Vector2> { new(0, 40), new(-16, 0) };
        Assert.Null(RoadSpatialGrid.PlanRoadPath(path, 4f, world));
    }

    [Fact]
    public void EvenAnArrivalCannotCrossTheDestinationItself()
    {
        var path = new List<Vector2> { new(40, 0), new(-16, 0) };
        Assert.Null(RoadSpatialGrid.PlanRoadPath(path, 4f, world));
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
            RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(centre, 12f) });
            var path = new List<Vector2> { Rotate(new Vector2(0, 40)), Rotate(new Vector2(-16, 0)) };
            Assert.NotNull(RoadSpatialGrid.PlanRoadPath(path, 4f, world));
        }
    }

    [Fact]
    public void MovingAPlanEndNearAThirdSiteCannotGrantAnArrivalAllowance()
    {
        // The original input skirted the site well away from its clearance.
        // A changed end now grazes the margin, but it is not our destination.
        Assert.True(RoadSiteProtection.BlocksFinalSegment(new Vector2(0, 40), new Vector2(-16, 0), 4f,
            new Vector2(0, 40), new Vector2(-30, 0), out var hit));
        Assert.True(hit.HasValue);
        Assert.Equal(12f, hit!.Value.Radius);
    }

    [Fact]
    public void RefusalNamesTheSegmentAndTheActualBlockingSite()
    {
        var previous = BepInEx.Logging.ManualLogSource.Captured;
        var lines = new List<string>();
        BepInEx.Logging.ManualLogSource.Captured = lines;
        try
        {
            AnArrivalDoesNotExemptAThirdSite();
            string detail = Assert.Single(lines.Where(s => s.StartsWith("POI clearance refused trial:")));
            Assert.Contains("site=(-8.00,20.00) radius=2.00", detail);
            Assert.Contains("segment=(", detail);
            Assert.Contains("input=(0.00,40.00)->(-16.00,0.00)", detail);
            Assert.Contains("Trial refusal, not necessarily a lost road", detail);
        }
        finally { BepInEx.Logging.ManualLogSource.Captured = previous; }
    }

    public void Dispose()
    {
        RoadSpatialGrid.Clear();
        RoadSiteProtection.Reset();
        RoadSiteProtection.Source = previousSource;
        WorldGenerator.instance = previousWorld;
    }
}
