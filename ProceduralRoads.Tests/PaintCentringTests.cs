using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Exact paint centres the painted band on the road. Vanilla reads a paint cell
/// as centred on its vertex (Heightmap.WorldToVertexMask); a half-cell shift put
/// the band 0.5 m toward -x/-z: paint over one bank and a levelled strip
/// without paint on the other.
/// </summary>
public class PaintCentringTests
{
    private const int Width = 64;
    private static int Index(int x, int z) => (z + Width / 2) * (Width + 1) + (x + Width / 2);

    private static TerrainComp Build(bool exact, float roadZ = 0f)
    {
        bool saved = RoadTerrainModifier.PaintExact;
        try
        {
            RoadTerrainModifier.PaintExact = exact;
            WorldGenerator.instance = new SyntheticWorld { HasRiver = false, HasMountain = false };
            var zone = new Vector2s(0, 0);
            Heightmap hm = Heightmap.CreateForZone(zone, Width);
            Heightmap.Registered = hm;
            TerrainComp tc = hm.m_terrainComp!;
            float natural = BiomeBlendedHeight.GetBlendedHeight(0f, 0f, WorldGenerator.instance);
            var points = new List<RoadSpatialGrid.RoadPoint>();
            for (float x = -40f; x <= 40f; x += 1f)
                points.Add(new RoadSpatialGrid.RoadPoint(new Vector2(x, roadZ), 4f, natural + 1f));
            RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, points, hm, tc);
            return tc;
        }
        finally { RoadTerrainModifier.PaintExact = saved; Heightmap.Registered = null; WorldGenerator.instance = null; }
    }

    [Fact]
    public void ExactPaintIsSymmetricAboutTheRoad()
    {
        var tc = Build(exact: true);
        // 1.7 m paint edge on a road along z = 0: cells centred at z = -1, 0, 1.
        foreach (int z in new[] { -1, 0, 1 }) Assert.True(tc.m_modifiedPaint[Index(0, z)], $"z={z} unpainted");
        foreach (int z in new[] { -2, 2 }) Assert.False(tc.m_modifiedPaint[Index(0, z)], $"z={z} painted");
    }

    [Fact]
    public void ARoadOffTheGridLinePaintsTheCellsNearestIt()
    {
        // Road along z = 0.4, paint edge 1.7 m: cell centres z = -1 (1.4 m)
        // through z = 2 (1.6 m) are inside, z = -2 (2.4 m) is not. Taking cell z
        // as centred at z + 0.5, as before, painted -1..1 and missed z = 2.
        var tc = Build(exact: true, roadZ: 0.4f);
        foreach (int z in new[] { -1, 0, 1, 2 }) Assert.True(tc.m_modifiedPaint[Index(0, z)], $"z={z} unpainted");
        Assert.False(tc.m_modifiedPaint[Index(0, -2)], "z=-2 painted");
    }
}
