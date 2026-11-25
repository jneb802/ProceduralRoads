using System;
using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

public sealed class SharedZonePaintTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    public void Dispose() { RoadNetworkGenerator.Reset(); _scope.Dispose(); }

    private sealed class Flat : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void BothZonesJudgeTheSharedPaintRowFromTheSameGround(bool reverse, bool horizontal)
    {
        using var plain = PlainEarthworks.Apply();
        RoadNetworkGenerator.Reset();
        _scope.WithWorld(new Flat()).WithZdos();
        Vector2 At(float across, float along) => horizontal ? new Vector2(along, across) : new Vector2(across, along);
        var points = new List<Vector2>();
        var heights = new List<float>();
        for (int i = -12; i <= 12; i++) { points.Add(At(31.5f, i)); heights.Add(40f); }
        RoadSpatialGrid.CommitLevelled(points, heights, 4f);
        Vector2 cut = At(35f, 0f);
        RoadSpatialGrid.CommitCuts(new[] { new Vector3(cut.x, 36f, cut.y) }, 2f);
        RoadSpatialGrid.FinalizeRoadNetwork();
        var maps = new Heightmap[2];
        foreach (int side in reverse ? new[] { 1, 0 } : new[] { 0, 1 })
        {
            var zone = horizontal ? new Vector2s(0, side) : new Vector2s(side, 0);
            maps[side] = _scope.RegisterHeightmap(zone);
            RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, RoadSpatialGrid.GetRoadPointsInZone(zone), maps[side], maps[side].m_terrainComp!);
        }
        for (int along = 0; along <= 64; along++)
        {
            int west = horizontal ? 64 * 65 + along : along * 65 + 64;
            int east = horizontal ? along : along * 65;
            Assert.Equal(maps[0].m_terrainComp!.m_modifiedPaint[west], maps[1].m_terrainComp!.m_modifiedPaint[east]);
            Assert.Equal(maps[0].m_terrainComp!.m_paintMask[west], maps[1].m_terrainComp!.m_paintMask[east]);
        }
        int middle = horizontal ? 64 * 65 + 32 : 32 * 65 + 64;
        Assert.False(maps[0].m_terrainComp!.m_modifiedPaint[middle], "the face across the border must suppress paint on both sides");
        int away = horizontal ? 64 * 65 + 40 : 40 * 65 + 64;
        Assert.True(maps[0].m_terrainComp!.m_modifiedPaint[away], "the level shared road must still be painted");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APointOutsideOneZoneUsesTheSameBlendedDeckInBoth(bool reverse)
    {
        using var plain = PlainEarthworks.Apply();
        RoadNetworkGenerator.Reset();
        _scope.WithWorld(new Flat()).WithZdos();
        var points = new List<Vector2>();
        var heights = new List<float>();
        for (int z = -12; z <= 12; z++) { points.Add(new Vector2(31.5f, z)); heights.Add(40f); }
        RoadSpatialGrid.CommitLevelled(points, heights, 4f, unpainted: true);
        RoadSpatialGrid.CommitLevelled(new[] { new Vector2(33f, 0f) }, new[] { 41f }, 4f);
        RoadSpatialGrid.FinalizeRoadNetwork();
        var maps = new Heightmap[2];
        foreach (int side in reverse ? new[] { 1, 0 } : new[] { 0, 1 })
        {
            var zone = new Vector2s(side, 0);
            maps[side] = _scope.RegisterHeightmap(zone);
            RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, RoadSpatialGrid.GetRoadPointsInZone(zone), maps[side], maps[side].m_terrainComp!);
        }
        int west = 32 * 65 + 64, east = 32 * 65;
        Assert.Equal(maps[0].m_terrainComp!.m_modifiedPaint[west], maps[1].m_terrainComp!.m_modifiedPaint[east]);
        Assert.Equal(maps[0].m_terrainComp!.m_paintMask[west], maps[1].m_terrainComp!.m_paintMask[east]);
        Assert.True(maps[0].m_terrainComp!.m_modifiedPaint[west], "the blended road surface should be painted from either zone");
    }

}
