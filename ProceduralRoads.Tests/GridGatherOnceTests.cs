using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A road point is filed in every 64 m grid cell its width reaches (RoadSpatialGrid.AddRoadPoint), so near a cell's border
/// it is in two. The gathers take each stored point once (RoadSpatialGrid.GetRoadPointsInZone,
/// RoadSpatialGrid.GetRoadPointsNearPosition): before, a zone's gather returned the copy too, and the terrain writer's fit
/// weighed the points near a border twice, so the ground it wrote there was not the surface the earthwork cap measures the
/// road by (RoadTerrainModifier.WrittenAt over the road's own points, RoadNetworkGenerator.EarthworkAcross).
/// </summary>
public sealed class GridGatherOnceTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private readonly IDisposable _fill;

    /// <summary>Meadows, flat at <see cref="Ground"/>.</summary>
    private sealed class Flat : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => Ground;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override int GetSeed() => 41;
    }

    private const float Ground = 55f, Crest = 60f, Grade = 0.3f, Width = RoadConstants.DefaultRoadWidth;

    /// <summary>The cells' border between cell (0, 0) and (1, 0), and between zones (0, 0) and (1, 0).</summary>
    private const float Border = RoadConstants.HalfZoneSize;

    public GridGatherOnceTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        // No fill spread: the earthwork cap's surface (WrittenAt) is read without it.
        _fill = StaticOverride.Keep(() => RoadEarthworkNoise.FillSpread);
        RoadEarthworkNoise.FillSpread = 0f;
        _scope.WithWorld(new Flat());
    }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        _fill.Dispose();
        _scope.Dispose();
    }

    /// <summary>A road along z = 0 from x = 10 to 54, a point every metre, over a crest on the cells' border, on fill.</summary>
    private static (List<Vector2> points, List<float> heights) Road()
    {
        var points = new List<Vector2>();
        var heights = new List<float>();
        for (int x = 10; x <= 54; x++) { points.Add(new Vector2(x, 0f)); heights.Add(Crest - Grade * Mathf.Abs(x - Border)); }
        return (points, heights);
    }

    private static void Store()
    {
        var (points, heights) = Road();
        RoadSpatialGrid.CommitLevelled(points, heights, Width);
        RoadSpatialGrid.FinalizeRoadNetwork();
    }

    /// <summary>A point 1 m inside cell (0, 0) is filed in it and in cell (1, 0); each zone's gather and a gather round it
    /// take it once.</summary>
    [Fact]
    public void APointNearACellBorderIsGatheredOnce()
    {
        Store();
        var at = new Vector2(Border - 1f, 0f);
        Assert.Contains(RoadSpatialGrid.CellPoints(new Vector2i(0, 0))!, rp => rp.p == at);
        Assert.Contains(RoadSpatialGrid.CellPoints(new Vector2i(1, 0))!, rp => rp.p == at);
        Assert.Single(RoadSpatialGrid.GetRoadPointsInZone(new Vector2s(0, 0)), rp => rp.p == at);
        Assert.Single(RoadSpatialGrid.GetRoadPointsInZone(new Vector2s(1, 0)), rp => rp.p == at);
        Assert.Single(RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(at.x, 0f, at.y), 3f), rp => rp.p == at);
        // Every stored point of the road, once in a zone's gather.
        var gathered = RoadSpatialGrid.GetRoadPointsInZone(new Vector2s(0, 0));
        Assert.Equal(gathered.Count, gathered.Select(rp => rp.p).Distinct().Count());
    }

    /// <summary>
    /// The terrain writer (RoadTerrainModifier.ApplyRoadTerrainModsWithContext, from the zone's gather) and the surface the
    /// earthwork cap measures (RoadTerrainModifier.WrittenAt over the road's own points) agree vertex by vertex up to the
    /// cells' border, where the fit takes points from both cells. Before, the copies near the border weighed twice in the
    /// writer's fit and it wrote the crest's ground off the measured surface.
    /// </summary>
    [Fact]
    public void TheWriterAndTheMeasuredSurfaceAgreeAtACellBorder()
    {
        Store();
        var zone = new Vector2s(0, 0);
        Heightmap hm = _scope.RegisterHeightmap(zone, 64);
        TerrainComp tc = hm.m_terrainComp!;
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, RoadSpatialGrid.GetRoadPointsInZone(zone), hm, tc);
        var (points, heights) = Road();
        var own = points.Select((p, i) => new RoadSpatialGrid.RoadPoint(p, Width, heights[i])).ToList();
        float worst = 0f;
        Vector2 worstAt = default;
        int read = 0;
        for (int x = 20; x <= 32; x++)
            for (int z = -1; z <= 1; z++)
            {
                int index = (z + 32) * 65 + (x + 32);
                Assert.True(tc.m_modifiedHeight[index], $"the writer left ({x}, {z})");
                var v = new Vector2(x, z);
                float under = BiomeBlendedHeight.GetBlendedHeight(x, z, WorldGenerator.instance);
                float written = under + tc.m_levelDelta[index] + tc.m_smoothDelta[index];
                float measured = RoadTerrainModifier.WrittenAt(own, v, under);
                read++;
                if (Mathf.Abs(written - measured) > worst) { worst = Mathf.Abs(written - measured); worstAt = v; }
            }
        Assert.Equal(39, read);
        Assert.True(worst < 1e-3f, $"the writer writes {worst:F4} m off the measured surface at {worstAt}");
    }
}
