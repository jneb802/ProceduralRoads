using System;
using System.Linq;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

[Collection("RoadStatics")]
public class SnappedLocationProtectionTests : IDisposable
{
    private sealed class FlatWorld : WorldGenerator
    {
        public float Height = 40f;
        public override float GetHeight(float x, float z) => Height;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        { weight = 0; width = 0; }
    }

    private readonly ValheimWorldScope _scope = new ValheimWorldScope();
    private readonly Func<System.Collections.Generic.IEnumerable<RoadLocationProtection.Footprint>?>? previousSource = RoadLocationProtection.Source;
    private readonly FlatWorld world = new();

    public SnappedLocationProtectionTests()
    {
        _scope.WithWorld(world);
        RoadSpatialGrid.Clear();
        RoadLocationProtection.Source = null;
        RoadLocationProtection.Set(Array.Empty<RoadLocationProtection.Footprint>());
    }

    [Fact]
    public void MergingParallelRoadsKeepsTheSafeUnsnappedRouteBesideAProtectedLocation()
    {
        var oldRoad = Enumerable.Range(0, 51).Select(i => new Vector2(-200 + i * 8, 0)).ToList();
        Assert.True(RoadSpatialGrid.AddRoadPath(oldRoad, 4, world));
        var path = Enumerable.Range(0, 26).Select(i => new Vector2(-100 + i * 8, 12)).ToList();
        var centre = new Vector2(-48, 5); // 7 m from the route, 5 m from the old road: the location and its clearance reach 6 m
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(centre, 2f) });
        for (int i = 1; i < path.Count; i++)
            Assert.False(RoadLocationProtection.BlocksSegment(path[i - 1], path[i], 4, default));

        PlainRoads.WithModDefaults(() =>
        {
            using (StaticOverride.Set(() => RoadNetworkGenerator.RoadSnap, 0f).And(() => RoadNetworkGenerator.CorridorSnap, 0f))
            {
                var control = RoadSpatialGrid.PlanRoadPath(path, 4, world);
                Assert.NotNull(control);
                AssertClear(control!, 4);
            }

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
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0, 0), 5f) });
        var path = new System.Collections.Generic.List<Vector2> { new(-20, 0), new(20, 0) };
        var plan = RoadSpatialGrid.PlanRoadPath(path, 4, world, followTerrain: followTerrain);
        Assert.Null(plan);
        Assert.Equal("final road crosses a protected location", RoadSpatialGrid.LastRefusal);
    }

    [Fact]
    public void FinalLocationClearanceUsesTheRequestedRoadWidth()
    {
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0, 6), 2f) });
        var path = new System.Collections.Generic.List<Vector2> { new(-20, 0), new(20, 0) };
        Assert.NotNull(RoadSpatialGrid.PlanRoadPath(path, 2, world));
        Assert.Null(RoadSpatialGrid.PlanRoadPath(path, 8, world));
    }

    [Fact]
    public void PreTrimSnapStillAllowsLeavingTheRoutesOwnEndpointFootprint()
    {
        var oldRoad = Enumerable.Range(0, 51).Select(i => new Vector2(-200 + i * 8, 0)).ToList();
        Assert.True(RoadSpatialGrid.AddRoadPath(oldRoad, 4, world));
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(-100, 12), 6f) });
        var path = Enumerable.Range(0, 26).Select(i => new Vector2(-100 + i * 8, 12)).ToList();
        PlainRoads.WithModDefaults(() =>
        {
            var snapped = RoadNetworkGenerator.SnapToNetwork(path, 4, locations: new RoadLocationProtection.Targets(path[0], null))!;
            Assert.Contains(snapped, p => Math.Abs(p.y) < 1f);
            Assert.Equal(path[0], snapped[0]);
            Assert.Equal(path[path.Count - 1], snapped[snapped.Count - 1]);
        });
    }

    [Fact]
    public void ProtectedLocationCheckDoesNotForbidAnIntentionalWade()
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
            Assert.False(RoadLocationProtection.BlocksSegment(plan.Points[i - 1], plan.Points[i], clearance, default),
                $"Final segment {plan.Points[i - 1]} -> {plan.Points[i]} crosses protected space");
    }

    public void Dispose()
    {
        RoadSpatialGrid.Clear();
        RoadLocationProtection.Reset();
        RoadLocationProtection.Source = previousSource;
        _scope.Dispose();
    }
}
