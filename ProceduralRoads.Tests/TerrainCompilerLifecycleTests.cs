using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// One terrain compiler per zone. A zone spawn writes the roads through the
/// zone's live compiler, creating one only when the zone has none, saved or
/// alive; a saved compiler that is not alive yet gets its roads when it comes
/// alive (OnTerrainCompilerReady), which also claims an unowned one and leaves
/// another peer's alone.
/// </summary>
public class TerrainCompilerLifecycleTests
{
    private static readonly Vector2s Zone = new(0, 0);

    private static (SyntheticWorld world, List<RoadSpatialGrid.RoadPoint> points) SetUp()
    {
        var world = new SyntheticWorld { HasRiver = false, HasMountain = false };
        WorldGenerator.instance = world;
        ZDOMan.instance = new ZDOMan();
        RoadSpatialGrid.Clear();
        var path = new List<Vector2>();
        for (float x = -40f; x <= 40f; x += 8f)
            path.Add(new Vector2(x, 0f));
        RoadSpatialGrid.AddRoadPath(path, 4f, world);
        RoadSpatialGrid.FinalizeRoadNetwork();
        var points = RoadSpatialGrid.GetRoadPointsInZone(Zone);
        Assert.True(points.Count > 0);
        return (world, points);
    }

    private static void TearDown()
    {
        Heightmap.Registered = null;
        RoadSpatialGrid.Clear();
        ZDOMan.instance = null;
        WorldGenerator.instance = null;
    }

    private static int Compilers() => ZDOMan.instance!.CountWithPrefab(TerrainComp.PrefabName);

    [Fact]
    public void FreshZoneSpawnCreatesTheOneCompilerAndWritesTheRoads()
    {
        var (_, points) = SetUp();
        try
        {
            Heightmap hm = Heightmap.CreateForZone(Zone, 64, withCompiler: false);
            Heightmap.Registered = hm;
            Assert.Equal(0, Compilers());

            RoadTerrainModifier.OnZoneSpawned(Zone, points);

            Assert.NotNull(hm.m_terrainComp);
            Assert.Equal(1, Compilers());
            Assert.Equal(1, hm.m_terrainComp!.SaveCount);
            Assert.True(RoadTerrainModifier.CarriesCurrentRoads(hm.m_terrainComp));
        }
        finally { TearDown(); }
    }

    [Fact]
    public void ZoneWithASavedCompilerNotAliveYetGetsNoSecondOne()
    {
        var (_, points) = SetUp();
        try
        {
            // The save holds this zone's compiler; the game brings it to life
            // after the zone is loaded, i.e. after the spawn hook has run.
            ZDO saved = ZDOMan.instance!.CreateNewZDO(ZoneSystem.GetZonePos(Zone), TerrainComp.PrefabName.GetStableHashCode());
            Heightmap hm = Heightmap.CreateForZone(Zone, 64, withCompiler: false);
            Heightmap.Registered = hm;
            Assert.True(RoadTerrainModifier.HasSavedTerrainCompiler(Zone));

            RoadTerrainModifier.OnZoneSpawned(Zone, points);

            Assert.Null(hm.m_terrainComp);
            Assert.Equal(1, Compilers());
            Assert.Equal(0, saved.GetInt("ProceduralRoads_AppliedVersion".GetStableHashCode(), 0));
        }
        finally { TearDown(); }
    }

    [Fact]
    public void LiveCompilerIsWrittenOnceWhenItComesAliveAndOnceOnly()
    {
        var (_, _) = SetUp();
        try
        {
            Heightmap hm = Heightmap.CreateForZone(Zone, 64);
            Heightmap.Registered = hm;
            TerrainComp tc = hm.m_terrainComp!;

            RoadTerrainModifier.OnTerrainCompilerReady(tc);
            Assert.Equal(1, tc.SaveCount);
            Assert.True(RoadTerrainModifier.CarriesCurrentRoads(tc));

            RoadTerrainModifier.OnTerrainCompilerReady(tc);
            Assert.Equal(1, tc.SaveCount);

            // A zone spawn after the compiler is alive and stamped writes nothing either.
            RoadTerrainModifier.OnZoneSpawned(Zone, RoadSpatialGrid.GetRoadPointsInZone(Zone));
            Assert.Equal(1, tc.SaveCount);
            Assert.Equal(1, Compilers());
        }
        finally { TearDown(); }
    }

    [Fact]
    public void UnownedCompilerIsClaimedAndWrittenAnotherPeersIsLeftAlone()
    {
        SetUp();
        try
        {
            Heightmap hm = Heightmap.CreateForZone(Zone, 64);
            Heightmap.Registered = hm;
            TerrainComp tc = hm.m_terrainComp!;

            tc.m_nview.GetZDO().SetOwner(0);
            RoadTerrainModifier.OnTerrainCompilerReady(tc);
            Assert.True(tc.m_nview.IsOwner(), "unowned compiler in our area was not claimed");
            Assert.Equal(1, tc.SaveCount);

            Heightmap other = Heightmap.CreateForZone(Zone, 64);
            Heightmap.Registered = other;
            TerrainComp theirs = other.m_terrainComp!;
            theirs.m_nview.GetZDO().SetOwner(2);
            RoadTerrainModifier.OnTerrainCompilerReady(theirs);
            Assert.Equal(0, theirs.SaveCount);
            Assert.Equal(2, theirs.m_nview.GetZDO().GetOwner());
        }
        finally { TearDown(); }
    }

    [Fact]
    public void CompilerWithoutRoadsInItsZoneIsUntouched()
    {
        SetUp();
        try
        {
            Heightmap hm = Heightmap.CreateForZone(new Vector2s(5, 5), 64);
            Heightmap.Registered = hm;
            RoadTerrainModifier.OnTerrainCompilerReady(hm.m_terrainComp!);
            Assert.Equal(0, hm.m_terrainComp!.SaveCount);
        }
        finally { TearDown(); }
    }
}
