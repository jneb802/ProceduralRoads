using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A raised causeway: a 4 m road 4 m above flat ground. A 60 % flat core
/// leaves the outer paint cell on the bank; the paint-below rule keeps paint
/// off it, and a flat core as wide as the road puts the paint on flat ground.
/// Built with snapped paint and plain earthworks, so the cells are the ones
/// the fixed cross-section gives.
/// </summary>
public class CausewayPaintTests
{
    private const int Width = 64;
    private const float Lift = 4f;

    private static TerrainComp Build(float paintBelow, float flatCore)
    {
        float savedBelow = RoadTerrainModifier.PaintBelow, savedCore = RoadProfile.FlatCoreRatio;
        bool savedExact = RoadTerrainModifier.PaintExact;
        try
        {
            PlainEarthworks.Begin();
            RoadTerrainModifier.PaintBelow = paintBelow;
            RoadTerrainModifier.PaintExact = false;
            RoadProfile.FlatCoreRatio = flatCore;
            WorldGenerator.instance = new SyntheticWorld { HasRiver = false, HasMountain = false };
            var zone = new Vector2s(0, 0);
            Heightmap hm = Heightmap.CreateForZone(zone, Width);
            Heightmap.Registered = hm;
            TerrainComp tc = hm.m_terrainComp!;
            float natural = BiomeBlendedHeight.GetBlendedHeight(0f, 0f, WorldGenerator.instance);
            var points = new List<RoadSpatialGrid.RoadPoint>();
            for (float x = -40f; x <= 40f; x += 1f)
                points.Add(new RoadSpatialGrid.RoadPoint(new Vector2(x, 0f), 4f, natural + Lift));
            RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, points, hm, tc);
            return tc;
        }
        finally
        {
            RoadTerrainModifier.PaintBelow = savedBelow; RoadProfile.FlatCoreRatio = savedCore;
            RoadTerrainModifier.PaintExact = savedExact;
            PlainEarthworks.End();
            Heightmap.Registered = null; WorldGenerator.instance = null;
        }
    }

    private static int Index(int x, int z) => (z + Width / 2) * (Width + 1) + (x + Width / 2);

    [Fact]
    public void WithoutPaintBelowTheOuterPaintCellLiesOnTheBank()
    {
        var tc = Build(0f, RoadConstants.RoadFlatCoreRatio);
        // Its far corner (2 m out) is only ~80 % levelled: ~0.8 m below the deck.
        Assert.True(tc.m_modifiedPaint[Index(0, 1)]);
        Assert.True(tc.m_levelDelta[Index(0, 2)] < tc.m_levelDelta[Index(0, 1)] - 0.5f);
    }

    [Fact]
    public void PaintBelowKeepsPaintOffTheBank()
    {
        var tc = Build(0.3f, RoadConstants.RoadFlatCoreRatio);
        Assert.False(tc.m_modifiedPaint[Index(0, 1)], "the cell reaching down the bank was painted");
        Assert.True(tc.m_modifiedPaint[Index(0, 0)], "the deck itself lost its paint");
    }

    [Fact]
    public void AFullWidthFlatTopCarriesThePaint()
    {
        var tc = Build(0.3f, 1f);
        // The top is level to 2 m out, so the same cell is on flat deck and keeps its paint.
        Assert.True(Mathf.Abs(tc.m_levelDelta[Index(0, 2)] - tc.m_levelDelta[Index(0, 0)]) < 0.05f);
        Assert.True(tc.m_modifiedPaint[Index(0, 1)]);
    }

    [Fact]
    public void FlatCoreChangesLevellingNotThePaintBands()
    {
        float saved = RoadProfile.FlatCoreRatio;
        try
        {
            RoadProfile.FlatCoreRatio = 1f;
            Assert.Equal(1f, RoadProfile.LevelBlend(1.9f, 4f));
            Assert.Equal(0f, RoadProfile.PaintStrength(1.7f, 4f));
            Assert.Equal(1f, RoadProfile.PaintStrength(1.2f, 4f));
            RoadProfile.FlatCoreRatio = RoadConstants.RoadFlatCoreRatio;
            Assert.True(RoadProfile.LevelBlend(1.9f, 4f) < 1f);
            Assert.Equal(1f, RoadProfile.PaintStrength(1.2f, 4f));
        }
        finally { RoadProfile.FlatCoreRatio = saved; }
    }

    [Fact]
    public void AJoinLowerThanTheRoadKeepsTheRoadsPaint()
    {
        // A main road along z = 0, 1 m up, and a joining road from the north
        // ending on it 3 m lower. The levelling blends the two, so the main
        // road's surface at the join sits below its stored height; that is
        // still the road, and it keeps its paint.
        float savedBelow = RoadTerrainModifier.PaintBelow; bool savedExact = RoadTerrainModifier.PaintExact;
        try
        {
            RoadTerrainModifier.PaintBelow = 0.5f; RoadTerrainModifier.PaintExact = true;
            WorldGenerator.instance = new SyntheticWorld { HasRiver = false, HasMountain = false };
            var zone = new Vector2s(0, 0);
            Heightmap hm = Heightmap.CreateForZone(zone, Width);
            Heightmap.Registered = hm;
            TerrainComp tc = hm.m_terrainComp!;
            float natural = BiomeBlendedHeight.GetBlendedHeight(0f, 0f, WorldGenerator.instance);
            var points = new List<RoadSpatialGrid.RoadPoint>();
            for (float x = -40f; x <= 40f; x += 1f)
                points.Add(new RoadSpatialGrid.RoadPoint(new Vector2(x, 0f), 4f, natural + 1f));
            for (float z = 30f; z >= 1f; z -= 1f)
                points.Add(new RoadSpatialGrid.RoadPoint(new Vector2(0f, z), 4f, natural - 2f));
            RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, points, hm, tc);
            // The main road's centreline through the join stays painted.
            for (int x = -4; x <= 4; x++)
                Assert.True(tc.m_modifiedPaint[Index(x, 0)], $"main road unpainted at x={x}");
        }
        finally
        {
            RoadTerrainModifier.PaintBelow = savedBelow; RoadTerrainModifier.PaintExact = savedExact;
            Heightmap.Registered = null; WorldGenerator.instance = null;
        }
    }
}
