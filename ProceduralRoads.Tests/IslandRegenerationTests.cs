using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Single-island regeneration (road_regen_island) on the synthetic world: one island, a spawn temple and three road locations.
/// </summary>
public class IslandRegenerationTests
{
    private static SyntheticWorld SetUp()
    {
        var world = new SyntheticWorld { HasRiver = false, HasMountain = false };
        WorldGenerator.instance = world;
        var zones = new ZoneSystem();
        foreach (var (name, x, z, radius) in new[]
        {
            ("StartTemple", 0f, 0f, 25f),
            ("Eikthyrnir", 260f, 40f, 10f),
            ("Crypt4", -220f, 150f, 18f),
            ("Crypt4", 120f, -260f, 18f),
        })
        {
            zones.Locations.Add(new ZoneSystem.LocationInstance
            {
                m_location = new ZoneSystem.ZoneLocation { m_prefab = new ZoneSystem.ZoneLocation.PrefabEntry { Name = name }, m_exteriorRadius = radius },
                m_position = new Vector3(x, world.GetHeight(x, z), z),
            });
        }
        ZoneSystem.instance = zones;
        RoadNetworkGenerator.Reset();
        return world;
    }

    private static void TearDown()
    {
        RoadNetworkGenerator.Reset();
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
