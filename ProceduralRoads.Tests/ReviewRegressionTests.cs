using System.IO;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Regressions from review: the network version must change when a road
/// is regraded (balanced height changes cancelled in a summed hash), and the
/// explicit application path must not create a second terrain compiler for
/// a zone whose saved compiler is not alive yet.
/// </summary>
public class ReviewRegressionTests
{
    private class GradeWorld : WorldGenerator
    {
        public float Grade;
        public override float GetHeight(float x, float z) => 40f + Grade * x;
    }

    [Fact]
    public void GeneratedRegradedRoadMustChangeVersion()
    {
        var path = new List<Vector2> { new(-16, 0), new(-8, 0), new(0, 0), new(8, 0), new(16, 0) };
        var world = new GradeWorld();
        WorldGenerator.instance = world;
        try
        {
            RoadSpatialGrid.Clear();
            RoadSpatialGrid.AddRoadPath(path, 4, world);
            RoadSpatialGrid.FinalizeRoadNetwork();
            int old = RoadSpatialGrid.RoadNetworkVersion;
            RoadSpatialGrid.Clear();
            world.Grade = 0.125f;
            RoadSpatialGrid.AddRoadPath(path, 4, world);
            RoadSpatialGrid.FinalizeRoadNetwork();
            Assert.NotEqual(old, RoadSpatialGrid.RoadNetworkVersion);
        }
        finally { WorldGenerator.instance = null; RoadSpatialGrid.Clear(); }
    }

    private static byte[] Network(float h1, float h2)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(1); writer.Write(1); writer.Write(0); writer.Write(0); writer.Write(2);
        writer.Write(0f); writer.Write(0f); writer.Write(4f); writer.Write(h1);
        writer.Write(8f); writer.Write(0f); writer.Write(4f); writer.Write(h2);
        writer.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void RegradingMustChangeVersion()
    {
        WorldGenerator.instance = new SyntheticWorld();
        try
        {
            Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(Network(40, 40)));
            int old = RoadSpatialGrid.RoadNetworkVersion;
            Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(Network(41, 39)));
            Assert.NotEqual(old, RoadSpatialGrid.RoadNetworkVersion);
        }
        finally { WorldGenerator.instance = null; RoadSpatialGrid.Clear(); }
    }

    [Fact]
    public void LoadedZoneApplyMustNotDuplicateSavedCompiler()
    {
        WorldGenerator.instance = new SyntheticWorld { HasRiver = false, HasMountain = false };
        ZDOMan.instance = new ZDOMan();
        RoadSpatialGrid.Clear();
        try
        {
            var zone = new Vector2i(0, 0);
            RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(-8, 0), new(8, 0) }, 4, WorldGenerator.instance);
            RoadSpatialGrid.FinalizeRoadNetwork();
            ZDOMan.instance.CreateNewZDO(ZoneSystem.GetZonePos(zone), TerrainComp.PrefabName.GetStableHashCode());
            Heightmap hm = Heightmap.CreateForZone(zone, 64, withCompiler: false);
            Heightmap.Registered = hm;
            Assert.True(RoadTerrainModifier.HasSavedTerrainCompiler(zone));

            RoadTerrainModifier.ApplyToLoadedZones();
            Assert.Equal(1, ZDOMan.instance.CountWithPrefab(TerrainComp.PrefabName));
            Assert.Null(hm.m_terrainComp);

            // The saved compiler comes alive already stamped with the current
            // version (as after a reload with an unchanged network): the
            // explicit request made above is still honoured, once.
            TerrainComp saved = hm.GetAndCreateTerrainCompiler();
            saved.m_nview.GetZDO().Set("ProceduralRoads_AppliedVersion".GetStableHashCode(), RoadSpatialGrid.RoadNetworkVersion);
            RoadTerrainModifier.OnTerrainCompilerReady(saved);
            Assert.Equal(1, saved.SaveCount);
            RoadTerrainModifier.OnTerrainCompilerReady(saved);
            Assert.Equal(1, saved.SaveCount);
        }
        finally { Heightmap.Registered = null; ZDOMan.instance = null; WorldGenerator.instance = null; RoadSpatialGrid.Clear(); RoadTerrainModifier.ResetDebugCounters(); }
    }
}
