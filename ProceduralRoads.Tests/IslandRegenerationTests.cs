using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Single-island regeneration (road_regen_island) on the synthetic world: one island, a spawn temple and three road locations.
/// </summary>
public class IslandRegenerationTests
{
    private static readonly (string name, float x, float z, float radius)[] StandardLocations =
    {
        ("StartTemple", 0f, 0f, 25f),
        ("Eikthyrnir", 260f, 40f, 10f),
        ("Crypt4", -220f, 150f, 18f),
        ("Crypt4", 120f, -260f, 18f),
    };

    private static SyntheticWorld SetUp() => SetUp(StandardLocations);

    private static SyntheticWorld SetUp(params (string name, float x, float z, float radius)[] locations)
    {
        var world = new SyntheticWorld { HasRiver = false, HasMountain = false };
        WorldGenerator.instance = world;
        var zones = new ZoneSystem();
        foreach (var (name, x, z, radius) in locations)
        {
            zones.Locations.Add(new ZoneSystem.LocationInstance
            {
                m_location = new ZoneSystem.ZoneLocation { m_prefab = new ZoneSystem.ZoneLocation.PrefabEntry { Name = name }, m_exteriorRadius = radius },
                m_position = new Vector3(x, world.GetHeight(x, z), z),
            });
        }
        ZoneSystem.instance = zones;
        ZDOMan.instance = new ZDOMan();
        RoadNetworkGenerator.Reset();
        return world;
    }

    private static void TearDown()
    {
        RoadNetworkGenerator.Reset();
        RoadNetworkGenerator.GenerateOnLoad = true;
        ProceduralRoadsPlugin.ConfigLocationNames = new();
        ZDOMan.instance = null;
        ZoneSystem.instance = null;
        WorldGenerator.instance = null;
    }

    [Fact]
    public void IslandRegenerationMatchesGlobalOnAOneIslandWorld()
    {
        SetUp();
        try
        {
            RoadNetworkGenerator.GenerateRoads(force: true);
            int globalPoints = RoadSpatialGrid.TotalRoadPoints;
            Assert.True(globalPoints > 0, "global generation produced no road points");

            Assert.True(RoadNetworkGenerator.RegenerateIslandAt(new Vector3(0f, 0f, 0f), out string summary), summary);
            Assert.True(RoadNetworkGenerator.RoadsAvailable);
            Assert.Equal(globalPoints, RoadSpatialGrid.TotalRoadPoints);
            Assert.Contains("roads", summary);
        }
        finally { TearDown(); }
    }

    [Fact]
    public void LoadTimeGenerationCanBeSwitchedOffAndIslandRegenerationStillWorks()
    {
        SetUp();
        try
        {
            RoadNetworkGenerator.GenerateOnLoad = false;
            Assert.False(RoadNetworkGenerator.GenerateRoadsOnLoad());
            Assert.False(RoadNetworkGenerator.RoadsAvailable);
            Assert.Equal(0, RoadSpatialGrid.TotalRoadPoints);

            Assert.True(RoadNetworkGenerator.RegenerateIslandAt(Vector3.zero, out string summary), summary);
            Assert.True(RoadSpatialGrid.TotalRoadPoints > 0);

            RoadNetworkGenerator.GenerateOnLoad = true;
            RoadNetworkGenerator.Reset();
            Assert.True(RoadNetworkGenerator.GenerateRoadsOnLoad());
            Assert.True(RoadNetworkGenerator.RoadsAvailable);
        }
        finally { RoadNetworkGenerator.GenerateOnLoad = true; TearDown(); }
    }

    [Fact]
    public void LoadTimeGenerationAppliesTerrainToZonesLoadedBeforeIt()
    {
        SetUp();
        try
        {
            // A zone that exists before generation, on the road from the start
            // temple towards Eikthyrnir: its heightmap must carry road deltas and
            // paint right after the load-time generation, without any zone spawn.
            var zone = new Vector2s(2, 0);
            Heightmap hm = Heightmap.CreateForZone(zone, 64);
            Heightmap.Registered = hm;
            TerrainComp tc = hm.m_terrainComp!;

            Assert.True(RoadNetworkGenerator.GenerateRoadsOnLoad());
            Assert.True(RoadSpatialGrid.GetRoadPointsInZone(zone).Count > 0, "test zone has no road points; move it onto the road");
            Assert.Equal(1, tc.SaveCount);
            Assert.Contains(tc.m_modifiedHeight, m => m);
            Assert.Contains(tc.m_modifiedPaint, m => m);
        }
        finally
        {
            Heightmap.Registered = null;
            TearDown();
        }
    }

    [Fact]
    public void IslandRegenerationOnARoadFreeWorldSavesAndReloads()
    {
        // The validation loop: load-time generation off, one island regenerated,
        // the world saved, the next session loads the roads back. Island
        // regeneration must create the metadata object the save path writes to,
        // exactly as global generation does.
        SetUp();
        try
        {
            RoadNetworkGenerator.GenerateOnLoad = false;
            Assert.False(RoadNetworkGenerator.GenerateRoadsOnLoad());
            Assert.True(RoadNetworkGenerator.RegenerateIslandAt(Vector3.zero, out string summary), summary);
            Assert.True(RoadSpatialGrid.TotalRoadPoints > 0);
            byte[] network = RoadSpatialGrid.SerializeAllRoadPoints()!;
            int version = RoadSpatialGrid.RoadNetworkVersion;
            Assert.NotEqual(0, version);

            var log = new List<string>();
            BepInEx.Logging.ManualLogSource.Captured = log;
            RoadNetworkGenerator.SaveGlobalRoadData();
            BepInEx.Logging.ManualLogSource.Captured = null;
            Assert.DoesNotContain(log, l => l.Contains("No metadata ZDO"));
            Assert.Equal(1, ZDOMan.instance!.CountWithPrefab(RoadNetworkPersistence.MetadataPrefabName));

            RoadNetworkGenerator.Reset();
            Assert.Equal(0, RoadSpatialGrid.TotalRoadPoints);
            Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData(), "saved network did not load back");
            Assert.Equal(network, RoadSpatialGrid.SerializeAllRoadPoints());
            Assert.Equal(version, RoadSpatialGrid.RoadNetworkVersion);
        }
        finally { TearDown(); }
    }

    [Fact]
    public void IslandRegenerationAfterGlobalGenerationReusesTheMetadataObject()
    {
        SetUp();
        try
        {
            RoadNetworkGenerator.GenerateRoads(force: true);
            Assert.True(RoadNetworkGenerator.RegenerateIslandAt(Vector3.zero, out string summary), summary);
            RoadNetworkGenerator.SaveGlobalRoadData();
            Assert.Equal(1, ZDOMan.instance!.CountWithPrefab(RoadNetworkPersistence.MetadataPrefabName));

            byte[] network = RoadSpatialGrid.SerializeAllRoadPoints()!;
            RoadNetworkGenerator.Reset();
            Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData());
            Assert.Equal(network, RoadSpatialGrid.SerializeAllRoadPoints());
        }
        finally { TearDown(); }
    }

    [Fact]
    public void FreshSessionIslandRegenerationSeesConfiguredCustomLocations()
    {
        // Only the start temple and a location the config names: without the
        // config registration the island has no road-eligible location at all.
        SetUp(("StartTemple", 0f, 0f, 25f), ("ModCamp", 240f, -60f, 12f));
        try
        {
            RoadNetworkGenerator.GenerateOnLoad = false;
            Assert.False(RoadNetworkGenerator.RegenerateIslandAt(Vector3.zero, out string without));
            Assert.Contains("no road-eligible locations", without);

            ProceduralRoadsPlugin.ConfigLocationNames = new() { "ModCamp" };
            Assert.True(RoadNetworkGenerator.RegenerateIslandAt(Vector3.zero, out string summary), summary);
            Assert.Contains("ModCamp", RoadNetworkGenerator.GetRegisteredLocations());
            var nearCamp = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(240f, 0f, -60f), 12f + 8f);
            Assert.True(nearCamp.Count > 0, "no road reaches the configured location");
        }
        finally
        {
            RoadNetworkGenerator.UnregisterLocation("ModCamp");
            TearDown();
        }
    }

    [Fact]
    public void RegenerationRefusesAPointInTheOcean()
    {
        SetUp();
        try
        {
            Assert.False(RoadNetworkGenerator.RegenerateIslandAt(new Vector3(3000f, 0f, 3000f), out string summary));
            Assert.Contains("No island", summary);
            Assert.Equal(0, RoadSpatialGrid.TotalRoadPoints);
        }
        finally { TearDown(); }
    }

}
