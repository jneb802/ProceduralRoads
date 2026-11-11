using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public sealed class RoadTravelTests : IDisposable
{
    public RoadTravelTests() { RoadNetworkGenerator.Reset(); RoadNetworkGenerator.MarkRoadsLoadedFromZDO(); RoadTravelApi.WorldId = 123; }
    public void Dispose() => RoadNetworkGenerator.Reset();
    private static void Road(float ax, float az, float bx, float bz, float height = 10)
    {
        RoadTravelApi.Add(new Vector2(ax, az), new Vector2(bx, bz), 4,
            new List<Vector2> { new(ax, az), new(bx, bz) }, new List<float> { height, height });
    }
    [Fact]
    public void ParallelRoadsDoNotBecomeConnectedFromProximity()
    {
        Road(0, 0, 100, 0); Road(0, 5, 100, 5); RoadTravelApi.Rebuild();
        Assert.False(RoadTravelApi.Connected(RoadTravelApi.GetRoutes()[0].Id, RoadTravelApi.GetRoutes()[1].Id));
    }
    [Fact]
    public void ParallelRoadProjectionKeepsTheClosestRoadAndStableTies()
    {
        Road(0, 0, 100, 0); Road(0, 5, 100, 5); RoadTravelApi.Rebuild();
        string first = RoadTravelApi.GetRoutes()[0].Id, second = RoadTravelApi.GetRoutes()[1].Id;
        Assert.True(RoadTravelApi.TryFindRoad(new Vector3(50, 10, 1), 10, out string nearFirst, out _, out _, out _));
        Assert.Equal(first, nearFirst);
        Assert.True(RoadTravelApi.TryFindRoad(new Vector3(50, 10, 4), 32, out string nearSecond, out _, out _, out _));
        Assert.Equal(second, nearSecond);
        Assert.True(RoadTravelApi.TryFindRoad(new Vector3(50, 10, 2.5f), 10, out string tied, out _, out _, out _));
        RoadTravelApi.Clear(); Road(0, 5, 100, 5); Road(0, 0, 100, 0); RoadTravelApi.Rebuild();
        Assert.True(RoadTravelApi.TryFindRoad(new Vector3(50, 10, 2.5f), 10, out string reordered, out _, out _, out _));
        Assert.Equal(tied, reordered);
    }
    [Fact]
    public void ForcedRegenerationDiscardsLoadedConnectionsWhenNewDataIsUnavailable()
    {
        Road(0, 0, 100, 0); RoadTravelApi.Rebuild();
        Assert.True(RoadTravelApi.Ready);
        WorldGenerator.instance = null;
        RoadNetworkGenerator.GenerateRoads(true);
        Assert.Empty(RoadTravelApi.GetRoutes());
        Assert.False(RoadTravelApi.Ready);
    }
    [Fact]
    public void JunctionsConnectButOverpassesDoNot()
    {
        Road(0, 0, 100, 0); Road(50, -30, 50, 30); Road(70, -30, 70, 30, 20); RoadTravelApi.Rebuild();
        IReadOnlyList<RoadTravelApi.Route> roads = RoadTravelApi.GetRoutes();
        Assert.True(RoadTravelApi.Connected(roads[0].Id, roads[1].Id));
        Assert.False(RoadTravelApi.Connected(roads[0].Id, roads[2].Id));
    }
    [Fact]
    public void IdentityAndConnectionsSurviveSerializationAndGenerationOrder()
    {
        Road(0, 0, 100, 0); Road(100, 0, 150, 10); RoadTravelApi.Rebuild();
        string[] ids = RoadTravelApi.GetRoutes().Select(r => r.Id).OrderBy(id => id).ToArray();
        string geometry = RoadTravelApi.GeometryVersion;
        byte[] saved = RoadTravelApi.Export();
        RoadTravelApi.Clear(); Assert.True(RoadTravelApi.Import(saved));
        Assert.True(RoadTravelApi.Connected(ids[0], ids[1]));
        Assert.Equal(geometry, RoadTravelApi.GeometryVersion);
        RoadTravelApi.Clear(); Road(100, 0, 150, 10); Road(0, 0, 100, 0); RoadTravelApi.Rebuild();
        Assert.Equal(ids, RoadTravelApi.GetRoutes().Select(r => r.Id).OrderBy(id => id).ToArray());
        Assert.Equal(geometry, RoadTravelApi.GeometryVersion);
        RoadTravelApi.WorldId = 456; Assert.False(RoadTravelApi.Import(saved));
    }
    [Fact]
    public void ImportedGeometryReplacesTheTerrainGridWithAuthoritativeHeights()
    {
        Road(0, 0, 20, 0, 42); RoadTravelApi.Rebuild();
        byte[] saved = RoadTravelApi.Export();
        RoadSpatialGrid.Clear();
        Assert.True(RoadTravelApi.Import(saved));
        Assert.True(RoadSpatialGrid.IsInitialized);
        Assert.Equal(2, RoadSpatialGrid.TotalRoadPoints);
        Assert.Equal(20f, RoadSpatialGrid.TotalRoadLength);
        Assert.Contains(RoadSpatialGrid.GetRoadPointsInZone(new Vector2s(0, 0)), point => point.p.x == 0 && point.h == 42 && point.w == 4);
        Assert.True(RoadTravelApi.Import(saved));
        Assert.Equal(2, RoadSpatialGrid.TotalRoadPoints);
    }
    [Fact]
    public void ProjectionUsesSegmentAndReturnsDirectionAndWidth()
    {
        Road(0, 0, 100, 0); RoadTravelApi.Rebuild();
        Assert.True(RoadTravelApi.TryFindRoad(new Vector3(50, 10, 3), 4, out _, out Vector3 point, out Vector3 direction, out float width));
        Assert.Equal(50, point.x); Assert.Equal(0, point.z); Assert.Equal(1, direction.x); Assert.Equal(4, width);
        Assert.False(RoadTravelApi.TryFindRoad(new Vector3(50, 10, 5), 4, out _, out _, out _, out _));
        Assert.False(RoadTravelApi.TryGetLocationAccess(new Vector3(50, 0, 1), out _, out _, out _, out _));
        Assert.True(RoadTravelApi.TryGetLocationAccess(new Vector3(0, 0, 0), out _, out _, out _, out _));
    }
    [Fact]
    public void CoastalPortGetsADryApproachWithoutChangingItsLocationIdentity()
    {
        SyntheticWorld world = new() { HasRiver = false, HasMountain = false };
        WorldGenerator.instance = world;
        Island island = new() { Id = 1, Min = new Vector2(-640, -640), Max = new Vector2(640, 640), CellSize = 64, WorldOffset = 0 };
        for (int x = -10; x <= 10; x++) for (int z = -10; z <= 10; z++) island.Cells.Add(new Vector2Int(x, z));
        Vector3 port = new(500, 25, 0);
        List<(string name, Vector3 position, float radius)> locations = new() { ("MWL_PortStone1", port, 16) };
        typeof(RoadNetworkGenerator).GetMethod("PreparePortApproaches", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { new List<Island> { island }, locations });
        Dictionary<Vector2, Vector2> approaches = (Dictionary<Vector2, Vector2>)typeof(RoadNetworkGenerator)
            .GetField("PortApproaches", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Vector2 approach = approaches[new Vector2(port.x, port.z)];
        Assert.Equal(Mathf.RoundToInt(approach.x / RoadPathfinder.CellSize) * RoadPathfinder.CellSize, approach.x);
        Assert.Equal(Mathf.RoundToInt(approach.y / RoadPathfinder.CellSize) * RoadPathfinder.CellSize, approach.y);
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, new RoadPathfinder(world));
        Assert.True(RoadNetworkGenerator.GenerateRoad(port, 16, new Vector3(0, 48, 0), 0, 4));
        RoadTravelApi.Rebuild();
        Assert.True(RoadTravelApi.TryGetLocationAccess(port, out _, out Vector3 access, out _, out _));
        Assert.True(world.GetHeight(access.x, access.z) >= RoadConstants.ShallowWaterHeight);
        Assert.Equal(RoadTravelApi.NodeId(port), RoadTravelApi.GetRoutes()[0].StartNode);
        WorldGenerator.instance = null;
    }
    [Fact]
    public void ReversingRoadDirectionKeepsItsIdentity()
    {
        Road(0, 0, 100, 10); string first = RoadTravelApi.GetRoutes()[0].Id;
        RoadTravelApi.Clear(); Road(100, 10, 0, 0);
        Assert.Equal(first, RoadTravelApi.GetRoutes()[0].Id);
    }
    [Fact]
    public void PortRetriesAnotherLocationWhenItsNearestConnectionCannotBeBuilt()
    {
        SyntheticWorld world = new() { HasMountain = false };
        WorldGenerator.instance = world;
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, new RoadPathfinder(world));
        Vector3 port = new(0, 48, 0);
        List<(string name, Vector3 position, float radius)> locations = new()
        { ("MWL_PortStone1", port, 0), ("Blocked", new Vector3(100, 26, 0), 0), ("Dry", new Vector3(-200, 48, 0), 0) };
        typeof(RoadNetworkGenerator).GetMethod("EnsurePortRoads", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { locations });
        Assert.Single(RoadTravelApi.GetRoutes());
        Assert.Equal(RoadTravelApi.NodeId(port), RoadTravelApi.GetRoutes()[0].StartNode);
        Assert.Equal(RoadTravelApi.NodeId(new Vector3(-200, 48, 0)), RoadTravelApi.GetRoutes()[0].EndNode);
        WorldGenerator.instance = null;
    }
    [Fact]
    public void PortAccessMovesInlandFromEitherEndWithoutCrossingAShortRoad()
    {
        Road(0, 0, 200, 0); RoadTravelApi.Rebuild();
        Assert.True(RoadTravelApi.TryGetLocationAccess(new Vector3(0, 0, 0), out _, out Vector3 start, out Vector3 forward, out _));
        Assert.True(RoadTravelApi.TryGetLocationAccess(new Vector3(200, 0, 0), out _, out Vector3 end, out Vector3 backward, out _));
        Assert.Equal(40, start.x); Assert.Equal(160, end.x);
        Assert.Equal(1, forward.x); Assert.Equal(-1, backward.x);
        RoadTravelApi.Clear(); Road(0, 0, 20, 0); RoadTravelApi.Rebuild();
        Assert.True(RoadTravelApi.TryGetLocationAccess(new Vector3(0, 0, 0), out _, out Vector3 shortStart, out _, out _));
        Assert.True(RoadTravelApi.TryGetLocationAccess(new Vector3(20, 0, 0), out _, out Vector3 shortEnd, out _, out _));
        Assert.Equal(7, shortStart.x); Assert.Equal(13, shortEnd.x);
    }
    private sealed class CountingWorld : WorldGenerator
    {
        internal int HeightQueries;
        public override float GetHeight(float x, float z) { HeightQueries++; return 40; }
        public override Heightmap.Biome GetBiome(float x, float z) => Heightmap.Biome.Meadows;
    }
    [Fact]
    public void RepeatedPathQueriesReuseTerrainSamplesWithinAPathAndRefreshBetweenPaths()
    {
        CountingWorld world = new(); RoadPathfinder pathfinder = new(world);
        List<Vector2>? first = pathfinder.FindPath(new Vector2(0, 0), new Vector2(256, 0));
        Assert.NotNull(first);
        int queries = world.HeightQueries;
        Assert.True(queries < 2000, "Terrain height queries: " + queries);
        world.HeightQueries = 0;
        List<Vector2>? second = pathfinder.FindPath(new Vector2(0, 0), new Vector2(256, 0));
        Assert.Equal(first, second);
        Assert.Equal(queries, world.HeightQueries);
    }
}
