using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>The road-end report behind the road_ends debug command, on the synthetic one-island world.</summary>
public class RoadEndReportTests
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
    public void RoadEndReportListsEveryConnectedLocationWithItsNearestPoint()
    {
        var world = SetUp();
        try
        {
            RoadNetworkGenerator.GenerateRoads(force: true);
            var locations = new List<(string name, Vector3 position, float radius)>();
            foreach (var inst in ZoneSystem.instance!.GetLocationList())
                locations.Add((inst.m_location.m_prefab.Name, inst.m_position, inst.m_location.m_exteriorRadius));

            var rows = RoadEndReport.Compute(locations, 8f, world);
            Assert.True(rows.Count >= 2, $"expected road ends at several locations, got {rows.Count}");
            for (int i = 1; i < rows.Count; i++)
                Assert.True(Mathf.Abs(rows[i - 1].DeltaRing) >= Mathf.Abs(rows[i].DeltaRing), "rows are not sorted by |delta ring|");
            foreach (var r in rows)
            {
                var near = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(r.Point.x, 0f, r.Point.y), 0.5f);
                Assert.True(near.Count > 0, $"{r.Name}: reported point is not a road point");
                Assert.True(Mathf.Abs(r.TerrainAtEnd - world.GetHeight(r.Point.x, r.Point.y)) < 0.5f, $"{r.Name}: terrain height is not the world's");
                Assert.True(r.RingMin <= r.RingMean && r.RingMean <= r.RingMax, $"{r.Name}: ring stats inconsistent");
            }
        }
        finally { TearDown(); }
    }
}
