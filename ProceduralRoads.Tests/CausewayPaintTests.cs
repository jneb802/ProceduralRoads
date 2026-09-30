using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
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
        using var scope = new ValheimWorldScope();
        using var plain = PlainEarthworks.Apply()
            .And(() => RoadTerrainModifier.PaintBelow, paintBelow)
            .And(() => RoadTerrainModifier.PaintExact, false)
            .And(() => RoadProfile.FlatCoreRatio, flatCore);
        scope.WithWorld(new SyntheticWorld { HasRiver = false, HasMountain = false });
        var zone = new Vector2s(0, 0);
        Heightmap hm = scope.RegisterHeightmap(zone, Width);
        TerrainComp tc = hm.m_terrainComp!;
        float natural = BiomeBlendedHeight.GetBlendedHeight(0f, 0f, WorldGenerator.instance);
        var points = new List<RoadSpatialGrid.RoadPoint>();
        for (float x = -40f; x <= 40f; x += 1f)
            points.Add(new RoadSpatialGrid.RoadPoint(new Vector2(x, 0f), 4f, natural + Lift));
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, points, hm, tc);
        return tc;
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
        using var overrides = StaticOverride.Keep(() => RoadProfile.FlatCoreRatio);
        RoadProfile.FlatCoreRatio = 1f;
        Assert.Equal(1f, RoadProfile.LevelBlend(1.9f, 4f));
        Assert.Equal(0f, RoadProfile.PaintStrength(1.7f, 4f));
        Assert.Equal(1f, RoadProfile.PaintStrength(1.2f, 4f));
        RoadProfile.FlatCoreRatio = RoadConstants.RoadFlatCoreRatio;
        Assert.True(RoadProfile.LevelBlend(1.9f, 4f) < 1f);
        Assert.Equal(1f, RoadProfile.PaintStrength(1.2f, 4f));
    }

    [Fact]
    public void AJoinLowerThanTheRoadKeepsTheRoadsPaint()
    {
        // A main road along z = 0, 1 m up, and a joining road from the north
        // ending on it 3 m lower. The levelling blends the two, so the main
        // road's surface at the join sits below its stored height; that is
        // still the road, and it keeps its paint.
        using var scope = new ValheimWorldScope();
        using var overrides = StaticOverride.Keep(() => RoadTerrainModifier.PaintBelow).AndKeep(() => RoadTerrainModifier.PaintExact);
        RoadTerrainModifier.PaintBelow = 0.5f; RoadTerrainModifier.PaintExact = true;
        scope.WithWorld(new SyntheticWorld { HasRiver = false, HasMountain = false });
        var zone = new Vector2s(0, 0);
        Heightmap hm = scope.RegisterHeightmap(zone, Width);
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
}
