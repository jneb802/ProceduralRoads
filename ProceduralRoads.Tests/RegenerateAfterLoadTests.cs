using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// From review: a forced regeneration in a world whose roads came from the
/// save appended to that network instead of replacing it.
///
/// The reset before a forced regeneration asked whether roads were GENERATED
/// this session. A world loaded from a save has roads without having generated
/// them, so the reset was skipped and the new network was laid on top of the
/// old one: the spatial grid kept both sets of points, and the serialized
/// network the world then stored held both.
/// </summary>
public class RegenerateAfterLoadTests
{
    /// <summary>A saved network, as a load hands one back: one cell, two
    /// points, at a height nothing generated here would produce.</summary>
    private const float SavedHeight = 4242f;

    private static byte[] SavedNetwork()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(1);            // version
        writer.Write(1);            // cell count
        writer.Write(0); writer.Write(0);   // cell coordinates
        writer.Write(2);            // points in the cell
        writer.Write(0f); writer.Write(0f); writer.Write(4f); writer.Write(SavedHeight);
        writer.Write(8f); writer.Write(0f); writer.Write(4f); writer.Write(SavedHeight);
        writer.Flush();
        return stream.ToArray();
    }

    private static void SetPrivateFlag(string name, bool value) =>
        typeof(RoadNetworkGenerator).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, value);

    /// <summary>Places the generator can actually build a network between, so
    /// that a regeneration really regenerates. Without them the test would
    /// pass on a run that did nothing.</summary>
    private static void GiveTheWorldSomePlaces()
    {
        ZoneSystem zones = new();
        (string name, float x, float z)[] places =
        {
            ("StartTemple", 0f, 0f),
            ("Eikthyrnir", -220f, -120f),
            ("GDKing", 260f, 140f),
            ("Crypt4", -120f, 240f),
            ("SunkenCrypt4", 180f, -260f),
        };
        foreach ((string name, float x, float z) place in places)
        {
            zones.Locations.Add(new ZoneSystem.LocationInstance
            {
                m_position = new Vector3(place.x, 0f, place.z),
                m_location = new ZoneSystem.ZoneLocation
                {
                    m_prefab = new ZoneSystem.ZoneLocation.PrefabEntry { Name = place.name },
                    m_exteriorRadius = 16f,
                },
            });
        }
        ZoneSystem.instance = zones;
    }

    private static SyntheticWorld SetUp()
    {
        var world = new SyntheticWorld { HasRiver = false, HasMountain = false };
        WorldGenerator.instance = world;
        ZDOMan.instance = new ZDOMan();
        RoadNetworkGenerator.Reset();
        GiveTheWorldSomePlaces();
        return world;
    }

    private static void TearDown()
    {
        RoadNetworkGenerator.Reset();
        WorldGenerator.instance = null;
        ZoneSystem.instance = null;
        ZDOMan.instance = null;
    }

    private static bool HoldsASavedPoint()
    {
        List<RoadSpatialGrid.RoadPoint> near =
            RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(4f, 0f, 0f), 32f);
        foreach (RoadSpatialGrid.RoadPoint point in near)
            if (Mathf.Abs(point.h - SavedHeight) < 0.001f)
                return true;
        return false;
    }

    [Fact]
    public void ForcedGenerationReplacesANetworkLoadedFromTheSave()
    {
        SetUp();
        try
        {
            // What a generation on this world produces with nothing loaded.
            RoadNetworkGenerator.GenerateRoads(force: true);
            Assert.True(RoadNetworkGenerator.RoadsGenerated, "the control generation did not run");
            int fromClean = RoadSpatialGrid.TotalRoadPoints;
            Assert.True(fromClean > 0, "the control generation built no road, so this test proves nothing");

            // Now the same generation, in a world that loaded a saved network.
            RoadNetworkGenerator.Reset();
            Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(SavedNetwork()));
            RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
            SetPrivateFlag("m_roadsGenerated", false);

            Assert.False(RoadNetworkGenerator.RoadsGenerated, "a loaded world has not generated its roads");
            Assert.True(RoadNetworkGenerator.RoadsAvailable, "a loaded world does have roads");
            int loaded = RoadSpatialGrid.TotalRoadPoints;
            Assert.True(loaded > 0);
            Assert.True(HoldsASavedPoint(), "the saved network did not load, so this test proves nothing");

            RoadNetworkGenerator.GenerateRoads(force: true);

            Assert.True(RoadNetworkGenerator.RoadsGenerated, "the regeneration did not run");
            Assert.False(HoldsASavedPoint(),
                "a point from the network loaded off the save is still in the grid: the new network " +
                "was laid on top of the old one instead of replacing it");
            Assert.Equal(fromClean, RoadSpatialGrid.TotalRoadPoints);
            Assert.NotEqual(fromClean + loaded, RoadSpatialGrid.TotalRoadPoints);
        }
        finally { TearDown(); }
    }

    [Fact]
    public void TheNetworkTheWorldWouldStoreIsReplacedToo()
    {
        SetUp();
        try
        {
            RoadNetworkGenerator.GenerateRoads(force: true);
            byte[]? fromClean = RoadSpatialGrid.SerializeAllRoadPoints();
            Assert.NotNull(fromClean);

            RoadNetworkGenerator.Reset();
            Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(SavedNetwork()));
            RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
            SetPrivateFlag("m_roadsGenerated", false);

            RoadNetworkGenerator.GenerateRoads(force: true);

            byte[]? after = RoadSpatialGrid.SerializeAllRoadPoints();
            Assert.NotNull(after);
            // Byte for byte what a generation with nothing loaded produces:
            // the saved network is gone from what the world would write back.
            Assert.Equal(fromClean!.Length, after!.Length);
            Assert.Equal(fromClean, after);
        }
        finally { TearDown(); }
    }

    [Fact]
    public void WithoutForceAWorldThatLoadedItsRoadsIsLeftAlone()
    {
        // The other half of the same guard: load-time generation must not run
        // in a world that already brought a network with it.
        SetUp();
        try
        {
            Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(SavedNetwork()));
            RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
            SetPrivateFlag("m_roadsGenerated", false);
            int loaded = RoadSpatialGrid.TotalRoadPoints;

            RoadNetworkGenerator.GenerateRoads();

            Assert.False(RoadNetworkGenerator.RoadsGenerated,
                "generation ran in a world that had already loaded its roads");
            Assert.Equal(loaded, RoadSpatialGrid.TotalRoadPoints);
            Assert.True(HoldsASavedPoint());
        }
        finally { TearDown(); }
    }
}
