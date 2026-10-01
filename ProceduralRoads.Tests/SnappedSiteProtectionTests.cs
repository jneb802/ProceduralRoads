using System;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

[Collection("RoadStatics")]
public class SnappedSiteProtectionTests : IDisposable
{
    private sealed class FlatWorld : WorldGenerator
    {
        public float Height = 40f;
        public override float GetHeight(float x, float z) => Height;
        public override Heightmap.Biome GetBiome(float x, float z) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        { weight = 0; width = 0; }
    }

    private readonly WorldGenerator? previousWorld = WorldGenerator.instance;
    private readonly Func<System.Collections.Generic.IEnumerable<RoadSiteProtection.Footprint>?>? previousSource = RoadSiteProtection.Source;
    private readonly FlatWorld world = new();

    public SnappedSiteProtectionTests()
    {
        WorldGenerator.instance = world;
        RoadSpatialGrid.Clear();
        RoadSiteProtection.Source = null;
        RoadSiteProtection.Set(Array.Empty<RoadSiteProtection.Footprint>());
    }

    [Fact]
    public void MergingParallelRoadsKeepsTheSafeUnsnappedRouteBesideAProtectedSite()
    {
        var oldRoad = Enumerable.Range(0, 51).Select(i => new Vector2(-200 + i * 8, 0)).ToList();
        Assert.True(RoadSpatialGrid.AddRoadPath(oldRoad, 4, world));
        var path = Enumerable.Range(0, 26).Select(i => new Vector2(-100 + i * 8, 12)).ToList();
        var centre = new Vector2(-48, 6);
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(centre, 2f) });
        for (int i = 1; i < path.Count; i++)
            Assert.False(RoadSiteProtection.BlocksSegment(path[i - 1], path[i], 4, null, null));

        PlainRoads.WithModDefaults(() =>
        {
            float snap = RoadNetworkGenerator.RoadSnap, corridor = RoadNetworkGenerator.CorridorSnap;
            RoadNetworkGenerator.RoadSnap = 0; RoadNetworkGenerator.CorridorSnap = 0;
            var control = RoadSpatialGrid.PlanRoadPath(path, 4, world);
            Assert.NotNull(control);
            AssertClear(control!, 4);
            RoadNetworkGenerator.RoadSnap = snap; RoadNetworkGenerator.CorridorSnap = corridor;

            var plan = RoadSpatialGrid.PlanRoadPath(path, 4, world);
            Assert.NotNull(plan); // Keep the safe alternative rather than losing the road.
            AssertClear(plan!, 4);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalStraightSegmentsAlsoRespectProtectedFootprints(bool followTerrain)
    {
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(new Vector2(0, 0), 5f) });
        var path = new System.Collections.Generic.List<Vector2> { new(-20, 0), new(20, 0) };
        var plan = RoadSpatialGrid.PlanRoadPath(path, 4, world, followTerrain: followTerrain);
        Assert.Null(plan);
        Assert.Equal("final road crosses a protected site", RoadSpatialGrid.LastRefusal);
    }

    [Fact]
    public void FinalSiteClearanceUsesTheRequestedRoadWidth()
    {
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(new Vector2(0, 6), 2f) });
        var path = new System.Collections.Generic.List<Vector2> { new(-20, 0), new(20, 0) };
        Assert.NotNull(RoadSpatialGrid.PlanRoadPath(path, 2, world));
        Assert.Null(RoadSpatialGrid.PlanRoadPath(path, 8, world));
    }

    [Fact]
    public void PreTrimSnapStillAllowsLeavingTheRoutesOwnEndpointFootprint()
    {
        var oldRoad = Enumerable.Range(0, 51).Select(i => new Vector2(-200 + i * 8, 0)).ToList();
        Assert.True(RoadSpatialGrid.AddRoadPath(oldRoad, 4, world));
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(new Vector2(-100, 12), 6f) });
        var path = Enumerable.Range(0, 26).Select(i => new Vector2(-100 + i * 8, 12)).ToList();
        PlainRoads.WithModDefaults(() =>
        {
            var snapped = RoadNetworkGenerator.SnapToNetwork(path, 4)!;
            Assert.Contains(snapped, p => Math.Abs(p.y) < 1f);
            Assert.Equal(path[0], snapped[0]);
            Assert.Equal(path[path.Count - 1], snapped[snapped.Count - 1]);
        });
    }

    [Fact]
    public void ProtectedSiteCheckDoesNotForbidAnIntentionalWade()
    {
        world.Height = 28f;
        var path = new System.Collections.Generic.List<Vector2> { new(-20, 0), new(20, 0) };
        var plan = RoadSpatialGrid.PlanRoadPath(path, 4, world, followTerrain: true);
        Assert.NotNull(plan);
        Assert.True(plan!.FollowTerrain);
        Assert.All(plan.Heights, height => Assert.Equal(28f, height));
    }

    private static void AssertClear(RoadSpatialGrid.PlannedPath plan, float clearance)
    {
        for (int i = 1; i < plan.Points.Count; i++)
            Assert.False(RoadSiteProtection.BlocksSegment(plan.Points[i - 1], plan.Points[i], clearance, null, null),
                $"Final segment {plan.Points[i - 1]} -> {plan.Points[i]} crosses protected space");
    }

    public void Dispose()
    {
        RoadSpatialGrid.Clear();
        RoadSiteProtection.Reset();
        RoadSiteProtection.Source = previousSource;
        WorldGenerator.instance = previousWorld;
    }
}
