using System.IO;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// What the move to Valheim 1.0 could break quietly.
///
/// The compiler catches a renamed method. It does not catch a zone id that
/// wraps because the type narrowed, a saved world that no longer loads, or a
/// zone that stops finding the road points inside it. Those are the three
/// things the 1.0 change could have broken without saying so, and they are
/// what these tests hold.
/// </summary>
public class ValheimVersionCompatibilityTests
{
    /// <summary>
    /// A road network exactly as the code before Valheim 1.0 wrote one:
    /// version 1, one grid cell at (0,0), two points. Written out by hand
    /// rather than round-tripped, so a change to the writer cannot quietly
    /// change what this test is comparing against.
    /// </summary>
    private static byte[] NetworkAsSavedBefore1Point0()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(1);                     // format version
        writer.Write(1);                     // cells
        writer.Write(0); writer.Write(0);    // cell (0,0) -- the mod's own road grid
        writer.Write(2);                     // points in the cell
        writer.Write(0f); writer.Write(0f); writer.Write(4f); writer.Write(41.5f);
        writer.Write(8f); writer.Write(0f); writer.Write(4f); writer.Write(42.5f);
        writer.Flush();
        return stream.ToArray();
    }

    [Fact]
    public void ARoadNetworkSavedBeforeValheim1PointOStillLoads()
    {
        // The retyping of a zone id from Vector2i to Vector2s must not have
        // reached the saved format. If it ever does, every player who updates
        // loses the roads in their world, and they will not get them back.
        WorldGenerator.instance = new SyntheticWorld();
        try
        {
            RoadSpatialGrid.Clear();
            Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(NetworkAsSavedBefore1Point0()),
                "a network saved by the pre-1.0 code no longer loads");
            Assert.Equal(2, RoadSpatialGrid.TotalRoadPoints);

            List<RoadSpatialGrid.RoadPoint> near =
                RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(4f, 0f, 0f), 16f);
            Assert.Equal(2, near.Count);
            Assert.Contains(near, p => Mathf.Abs(p.h - 41.5f) < 0.001f);
            Assert.Contains(near, p => Mathf.Abs(p.h - 42.5f) < 0.001f);
        }
        finally { RoadSpatialGrid.Clear(); WorldGenerator.instance = null; }
    }

    [Fact]
    public void TheFormatIsStillWrittenTheWayItIsRead()
    {
        // The other half: what the mod writes today must match the bytes
        // above, or a world saved now would not load on a build that reads
        // the documented format.
        WorldGenerator.instance = new SyntheticWorld();
        try
        {
            RoadSpatialGrid.Clear();
            Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(NetworkAsSavedBefore1Point0()));
            byte[] written = RoadSpatialGrid.SerializeAllRoadPoints()!;
            Assert.Equal(NetworkAsSavedBefore1Point0(), written);
        }
        finally { RoadSpatialGrid.Clear(); WorldGenerator.instance = null; }
    }

    [Theory]
    // The corners and edges of the world, where a zone index is largest.
    [InlineData(10500f, 10500f)]
    [InlineData(-10500f, -10500f)]
    [InlineData(10500f, -10500f)]
    [InlineData(-10500f, 10500f)]
    [InlineData(0f, 0f)]
    public void AZoneIdAtTheEdgeOfTheWorldSurvivesBeingAShort(float x, float z)
    {
        // Vector2s holds shorts. A zone is 64 m and the world runs to about
        // 10 500 m, so a zone index reaches only about +-164 and there is
        // room to spare -- but nothing in the type says so, and a silent wrap
        // would put a road's terrain in the wrong zone somewhere far out.
        Vector2s zone = ZoneSystem.GetZone(new Vector3(x, 0f, z));

        Assert.InRange((int)zone.x, short.MinValue, short.MaxValue);
        Assert.InRange((int)zone.y, short.MinValue, short.MaxValue);

        // And it must still name the place it came from.
        Vector3 back = ZoneSystem.GetZonePos(zone);
        Assert.True(Mathf.Abs(back.x - x) <= ZoneSystem.ZoneSize,
            $"zone {zone} maps back to x={back.x}, not near {x}");
        Assert.True(Mathf.Abs(back.z - z) <= ZoneSystem.ZoneSize,
            $"zone {zone} maps back to z={back.z}, not near {z}");
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(512f, -768f)]
    [InlineData(-9000f, 9000f)]
    public void AZoneStillFindsTheRoadPointsInsideIt(float x, float z)
    {
        // The point of the retyping: a Vector2s zone id has to select the same
        // road points a Vector2i one did. A road is laid across a known place
        // and the zone containing that place is asked for it.
        var world = new SyntheticWorld { HasRiver = false, HasMountain = false };
        WorldGenerator.instance = world;
        try
        {
            RoadSpatialGrid.Clear();
            List<Vector2> path = new();
            for (int i = -3; i <= 3; i++)
                path.Add(new Vector2(x + i * 8f, z));
            RoadSpatialGrid.AddRoadPath(path, 4f, world);
            RoadSpatialGrid.FinalizeRoadNetwork();
            Assert.True(RoadSpatialGrid.TotalRoadPoints > 0, "the road was not laid, so this proves nothing");

            Vector2s zone = ZoneSystem.GetZone(new Vector3(x, 0f, z));
            List<RoadSpatialGrid.RoadPoint> inZone = RoadSpatialGrid.GetRoadPointsInZone(zone);

            Assert.NotEmpty(inZone);
            Assert.Contains(inZone, p => Mathf.Abs(p.p.x - x) < 1f && Mathf.Abs(p.p.y - z) < 1f);
        }
        finally { RoadSpatialGrid.Clear(); WorldGenerator.instance = null; }
    }

    [Fact]
    public void AWorldReadFromDiskIsReadyEvenThoughNoEventArrives()
    {
        // The 1.0 break that a compiling build hides. ZoneSystem.Load writes
        // m_locationsGenerated straight from the save, so the property setter --
        // the only thing that raises GenerateLocationsCompleted -- never runs.
        // A mod that treats "the event fired" as the definition of "locations are
        // ready" waits forever, and an existing world silently gets no roads.
        RoadNetworkGenerator.Reset();
        var zones = new ZoneSystem();
        ZoneSystem.instance = zones;
        try
        {
            bool eventFired = false;
            zones.GenerateLocationsCompleted += () => eventFired = true;

            zones.LoadLocationsGeneratedFromSave(true);   // what loading a world does

            Assert.False(eventFired, "1.0 does not raise the event when a world is loaded");
            Assert.True(RoadNetworkGenerator.IsLocationsReady,
                "the world's locations are in place, so roads must be allowed to load or build");
        }
        finally { ZoneSystem.instance = null; RoadNetworkGenerator.Reset(); }
    }

    [Fact]
    public void AFreshlyGeneratedWorldStillAnnouncesItsLocations()
    {
        // The other half: on a new world the setter does run, the event is raised
        // for whoever subscribed, and the mod is told the ordinary way.
        RoadNetworkGenerator.Reset();
        var zones = new ZoneSystem();
        ZoneSystem.instance = zones;
        try
        {
            int fired = 0;
            zones.GenerateLocationsCompleted += () => fired++;
            Assert.Equal(0, fired);

            zones.LocationsGenerated = true;              // what generating a world does

            Assert.Equal(1, fired);
            Assert.True(RoadNetworkGenerator.IsLocationsReady);

            // 1.0 drops the handlers once it has fired, and fires immediately for
            // anyone who subscribes afterwards. Both halves matter to a mod that
            // subscribes from a patch whose order it does not control.
            int late = 0;
            zones.GenerateLocationsCompleted += () => late++;
            Assert.Equal(1, late);
            Assert.Equal(1, fired);
        }
        finally { ZoneSystem.instance = null; RoadNetworkGenerator.Reset(); }
    }
}
