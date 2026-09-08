using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Road terrain is written to a zone once per network version. An ordinary
/// zone load (the ZoneSystem.SpawnZone hook) leaves a zone alone when its
/// terrain compiler is stamped with the current version, so a player's
/// terrain edits in the road survive a reload and paint is not blended
/// again; a changed network or an explicit reapplication writes it anew.
/// </summary>
public class TerrainReapplicationTests
{
    private const int Width = 64;
    private static readonly Vector2i Zone = new(0, 0);

    private static List<Vector2> StraightPath(float y)
    {
        var path = new List<Vector2>();
        for (float x = -40f; x <= 40f; x += 8f)
            path.Add(new Vector2(x, y));
        return path;
    }

    /// <summary>One road through the zone, network finalized, a fresh zone heightmap registered.</summary>
    private static (SyntheticWorld world, Heightmap hm, TerrainComp tc, List<RoadSpatialGrid.RoadPoint> points) SetUp()
    {
        var world = new SyntheticWorld { HasRiver = false, HasMountain = false };
        WorldGenerator.instance = world;
        RoadSpatialGrid.Clear();
        RoadSpatialGrid.AddRoadPath(StraightPath(0f), 4f, world);
        RoadSpatialGrid.FinalizeRoadNetwork();

        Heightmap hm = Heightmap.CreateForZone(Zone, Width);
        Heightmap.Registered = hm;
        var points = RoadSpatialGrid.GetRoadPointsInZone(Zone);
        Assert.True(points.Count > 0, "road missed the zone");
        return (world, hm, hm.m_terrainComp!, points);
    }

    private static void TearDown()
    {
        Heightmap.Registered = null;
        RoadSpatialGrid.Clear();
        WorldGenerator.instance = null;
    }

    private static int Index(int x, int z) => (z + Width / 2) * (Width + 1) + (x + Width / 2);

    [Fact]
    public void OrdinaryZoneLoadKeepsPlayerTerrainEditsInTheRoad()
    {
        var (_, _, tc, points) = SetUp();
        try
        {
            RoadTerrainModifier.ApplyRoadTerrainMods(Zone, points);
            Assert.Equal(1, tc.SaveCount);
            Assert.True(RoadTerrainModifier.CarriesCurrentRoads(tc));
            int i = Index(0, 0);
            Assert.True(tc.m_modifiedHeight[i], "road centre vertex not written");

            // The player raises and smooths the ground in the road, then the
            // zone is unloaded and loaded again.
            tc.m_levelDelta[i] = 5f;
            tc.m_smoothDelta[i] = 1f;
            var paintBefore = (Color[])tc.m_paintMask.Clone();

            RoadTerrainModifier.ApplyRoadTerrainMods(Zone, points);

            Assert.Equal(1, tc.SaveCount);
            Assert.Equal(5f, tc.m_levelDelta[i]);
            Assert.Equal(1f, tc.m_smoothDelta[i]);
            for (int k = 0; k < paintBefore.Length; k++)
                Assert.True(paintBefore[k].b == tc.m_paintMask[k].b && paintBefore[k].a == tc.m_paintMask[k].a,
                    $"paint changed at vertex {k} on a reload");
        }
        finally { TearDown(); }
    }

    [Fact]
    public void ExplicitReapplicationWritesTheRoadAgain()
    {
        var (_, hm, tc, points) = SetUp();
        try
        {
            RoadTerrainModifier.ApplyRoadTerrainMods(Zone, points);
            int i = Index(0, 0);
            float roadDelta = tc.m_levelDelta[i];
            tc.m_levelDelta[i] = 5f;
            tc.m_smoothDelta[i] = 1f;

            RoadTerrainModifier.ApplyRoadTerrainModsWithContext(Zone, points, hm, tc);

            Assert.Equal(2, tc.SaveCount);
            Assert.Equal(roadDelta, tc.m_levelDelta[i]);
            Assert.Equal(0f, tc.m_smoothDelta[i]);

            tc.m_levelDelta[i] = 5f;
            RoadTerrainModifier.ApplyRoadTerrainMods(Zone, points, force: true);
            Assert.Equal(3, tc.SaveCount);
            Assert.Equal(roadDelta, tc.m_levelDelta[i]);
        }
        finally { TearDown(); }
    }

    [Fact]
    public void AChangedNetworkIsWrittenOnTheNextZoneLoad()
    {
        var (world, _, tc, points) = SetUp();
        try
        {
            RoadTerrainModifier.ApplyRoadTerrainMods(Zone, points);
            int oldVersion = RoadSpatialGrid.RoadNetworkVersion;
            Assert.False(tc.m_modifiedHeight[Index(0, 20)]);

            RoadSpatialGrid.AddRoadPath(StraightPath(20f), 4f, world);
            RoadSpatialGrid.FinalizeRoadNetwork();
            Assert.NotEqual(oldVersion, RoadSpatialGrid.RoadNetworkVersion);
            Assert.False(RoadTerrainModifier.CarriesCurrentRoads(tc));

            RoadTerrainModifier.ApplyRoadTerrainMods(Zone, RoadSpatialGrid.GetRoadPointsInZone(Zone));
            Assert.Equal(2, tc.SaveCount);
            Assert.True(tc.m_modifiedHeight[Index(0, 20)], "second road not written");
            Assert.True(RoadTerrainModifier.CarriesCurrentRoads(tc));
        }
        finally { TearDown(); }
    }

    [Fact]
    public void NetworkVersionSurvivesASaveLoadRoundTripAndTracksHeights()
    {
        var (world, _, _, _) = SetUp();
        try
        {
            RoadSpatialGrid.AddRoadPath(StraightPath(20f), 4f, world);
            RoadSpatialGrid.FinalizeRoadNetwork();
            int version = RoadSpatialGrid.RoadNetworkVersion;
            Assert.NotEqual(0, version);

            byte[] data = RoadSpatialGrid.SerializeAllRoadPoints()!;
            RoadSpatialGrid.Clear();
            Assert.Equal(0, RoadSpatialGrid.RoadNetworkVersion);
            Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(data));
            Assert.Equal(version, RoadSpatialGrid.RoadNetworkVersion);

            // Same paths on higher ground: only the heights differ, and the
            // version must differ with them, so re-leveled roads reach zones.
            RoadSpatialGrid.Clear();
            var taller = new SyntheticWorld { HasRiver = false, HasMountain = false, IslandPeakHeight = 25f };
            WorldGenerator.instance = taller;
            RoadSpatialGrid.AddRoadPath(StraightPath(0f), 4f, taller);
            RoadSpatialGrid.AddRoadPath(StraightPath(20f), 4f, taller);
            RoadSpatialGrid.FinalizeRoadNetwork();
            Assert.NotEqual(version, RoadSpatialGrid.RoadNetworkVersion);
        }
        finally { TearDown(); }
    }
}
