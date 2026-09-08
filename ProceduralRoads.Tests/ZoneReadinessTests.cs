using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// road_zone_state: the capture readiness report. A zone with road points is
/// ready once its live terrain compiler carries the current network and its
/// queued heightmap rebuild has run; unloaded zones and missing or unstamped
/// compilers are named as pending. The delayed Poke queues a rebuild that the
/// Regenerate patch times and takes.
/// </summary>
[Collection("RoadTimings")]
public class ZoneReadinessTests
{
    private static readonly Vector2i Zone = new(0, 0);

    private static void SetUp()
    {
        var world = new SyntheticWorld { HasRiver = false, HasMountain = false };
        WorldGenerator.instance = world;
        ZDOMan.instance = new ZDOMan();
        ZoneSystem.instance = new ZoneSystem();
        RoadNetworkGenerator.Reset(); // clears the grid too: build the network after it
        var path = new List<Vector2>();
        for (float x = -40f; x <= 40f; x += 8f)
            path.Add(new Vector2(x, 0f));
        RoadSpatialGrid.AddRoadPath(path, 4f, world);
        RoadSpatialGrid.FinalizeRoadNetwork();
        RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        RoadTerrainModifier.ResetDebugCounters();
        RoadTimings.Reset();
    }

    private static void TearDown()
    {
        Heightmap.Registered = null;
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.Reset();
        ZDOMan.instance = null;
        ZoneSystem.instance = null;
        WorldGenerator.instance = null;
    }

    [Fact]
    public void WithoutANetworkNothingIsReady()
    {
        SetUp();
        try
        {
            RoadNetworkGenerator.Reset();
            Assert.Equal("ROAD_READY ready=false reason=no-network", RoadTerrainModifier.DescribeZoneReadiness(Vector3.zero, 10f));
        }
        finally { TearDown(); }
    }

    [Fact]
    public void UnloadedZoneAndMissingCompilerArePending()
    {
        SetUp();
        try
        {
            // radius 10 around the origin covers only zone (0,0)
            ZoneSystem.instance!.LoadedZones = new HashSet<Vector2i>();
            Assert.Equal("ROAD_READY ready=false zones=1 with_roads=0 stamped=0 version=" + RoadSpatialGrid.RoadNetworkVersion + " pending=(0,0):not-loaded",
                RoadTerrainModifier.DescribeZoneReadiness(Vector3.zero, 10f));

            ZoneSystem.instance.LoadedZones = null;
            Heightmap.Registered = Heightmap.CreateForZone(Zone, 64, withCompiler: false);
            string report = RoadTerrainModifier.DescribeZoneReadiness(Vector3.zero, 10f);
            Assert.Contains("ready=false", report);
            Assert.Contains("with_roads=1 stamped=0", report);
            Assert.EndsWith("pending=(0,0):no-compiler", report);
        }
        finally { TearDown(); }
    }

    [Fact]
    public void WrittenZoneIsPendingUntilItsRebuildRunsThenReady()
    {
        SetUp();
        try
        {
            Heightmap hm = Heightmap.CreateForZone(Zone, 64);
            Heightmap.Registered = hm;
            TerrainComp tc = hm.m_terrainComp!;

            string before = RoadTerrainModifier.DescribeZoneReadiness(Vector3.zero, 10f);
            Assert.EndsWith("pending=(0,0):not-stamped", before);

            RoadTerrainModifier.OnTerrainCompilerReady(tc);
            Assert.True(RoadTerrainModifier.CarriesCurrentRoads(tc));
            Assert.True(hm.HaveQueuedRebuild());
            Assert.Equal(1, RoadTerrainModifier.PendingRebuilds);
            Assert.Equal(1, RoadTimings.GetCount("terrain.rebuild_queued"));
            Assert.EndsWith("pending=(0,0):rebuild-queued", RoadTerrainModifier.DescribeZoneReadiness(Vector3.zero, 10f));

            // The game's late update: the patch takes our pending entry and times the rebuild.
            Assert.True(RoadTerrainModifier.TakePendingRebuild(hm));
            Assert.False(RoadTerrainModifier.TakePendingRebuild(hm));
            hm.Regenerate();
            Assert.Equal(0, RoadTerrainModifier.PendingRebuilds);
            Assert.Equal("ROAD_READY ready=true zones=1 with_roads=1 stamped=1 version=" + RoadSpatialGrid.RoadNetworkVersion,
                RoadTerrainModifier.DescribeZoneReadiness(Vector3.zero, 10f));
        }
        finally { TearDown(); }
    }

    [Fact]
    public void ResetForgetsPendingRebuilds()
    {
        SetUp();
        try
        {
            Heightmap hm = Heightmap.CreateForZone(Zone, 64);
            Heightmap.Registered = hm;
            RoadTerrainModifier.OnTerrainCompilerReady(hm.m_terrainComp!);
            Assert.Equal(1, RoadTerrainModifier.PendingRebuilds);
            RoadTerrainModifier.ResetDebugCounters();
            Assert.Equal(0, RoadTerrainModifier.PendingRebuilds);
            Assert.False(RoadTerrainModifier.TakePendingRebuild(hm));
        }
        finally { TearDown(); }
    }
}
