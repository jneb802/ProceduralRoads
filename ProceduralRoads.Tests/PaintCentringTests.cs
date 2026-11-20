using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Exact paint centres the painted band on the road. Vanilla draws a paint cell
/// as centred on its vertex (Heightmap.WorldToVertexMask); a half-cell shift put
/// the band 0.5 m toward -x/-z: paint over one bank and a levelled strip
/// without paint on the other. Vanilla's grass test reads the same texel for the
/// cell half a cell toward +x/+z (Heightmap.IsCleared subtracts 0.5 before
/// WorldToVertexMask), so a texel also takes the profile at that cell's middle.
/// </summary>
public class PaintCentringTests
{
    private const int Width = 64;
    private static int Index(int x, int z) => (z + Width / 2) * (Width + 1) + (x + Width / 2);

    private static TerrainComp Build(float roadZ = 0f) => Build(roadZ, alongZ: false);

    private static TerrainComp Build(float offset, bool alongZ, string caller = "context")
    {
        using var scope = new ValheimWorldScope();
        scope.WithWorld(new SyntheticWorld { HasRiver = false, HasMountain = false }).WithZdos();
        RoadSpatialGrid.Clear();
        RoadTerrainModifier.ResetForWorld();
        try
        {
            var zone = new Vector2s(0, 0);
            Heightmap hm = scope.RegisterHeightmap(zone, Width);
            TerrainComp tc = hm.m_terrainComp!;
            float natural = BiomeBlendedHeight.GetBlendedHeight(0f, 0f, WorldGenerator.instance);
            var points = new List<RoadSpatialGrid.RoadPoint>();
            for (float s = -40f; s <= 40f; s += 1f)
                points.Add(new RoadSpatialGrid.RoadPoint(alongZ ? new Vector2(offset, s) : new Vector2(s, offset), 4f, natural + 1f));
            if (caller == "context")
                RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, points, hm, tc);
            else
            {
                var line = points.ConvertAll(p => p.p);
                var heights = points.ConvertAll(p => p.h);
                if (caller == "append")
                {
                    RoadSpatialGrid.FinalizeRoadNetwork();
                    RoadSpatialGrid.CommitAppend(new[] { new RoadSpatialGrid.PlannedPath(line, heights,
                        new List<RoadPointDebugInfo>(), 4f, 80f, false) }, RoadSpatialGrid.RoadNetworkVersion);
                    RoadTerrainModifier.ApplyAdditionsToLoadedZones();
                }
                else
                {
                    RoadSpatialGrid.CommitLevelled(line, heights, 4f);
                    RoadSpatialGrid.FinalizeRoadNetwork();
                    if (caller == "spawn") RoadTerrainModifier.OnZoneSpawned(zone, RoadSpatialGrid.GetRoadPointsInZone(zone));
                    else if (caller == "compiler") RoadTerrainModifier.OnTerrainCompilerReady(tc);
                    else if (caller == "bake") RoadTerrainModifier.BakeRoadTerrain(zone, hm, tc);
                    else if (caller == "loaded") RoadTerrainModifier.ApplyToLoadedZones();
                    else throw new System.ArgumentException("Unknown terrain caller", nameof(caller));
                }
            }
            return tc;
        }
        finally
        {
            RoadSpatialGrid.Clear();
            RoadTerrainModifier.ResetForWorld();
        }
    }

    [Fact]
    public void ExactPaintIsSymmetricAboutTheRoad()
    {
        var tc = Build();
        // 1.7 m paint edge on a road along z = 0: cells centred at z = -1, 0, 1 alike.
        foreach (int z in new[] { -1, 1 })
            Assert.Equal(tc.m_paintMask[Index(0, 0)].b, tc.m_paintMask[Index(0, z)].b, 3);
        Assert.False(tc.m_modifiedPaint[Index(0, 2)], "z=2 painted");
        // z = -2 is drawn 2 m out, past the edge; it carries only the fade its grass cell's middle (z = -1.5) asks for,
        // under the grass test's half, so it stays a verge.
        Assert.True(tc.m_modifiedPaint[Index(0, -2)], "z=-2 must carry its grass cell's fade");
        Assert.True(tc.m_paintMask[Index(0, -2)].b > 0f && tc.m_paintMask[Index(0, -2)].b < 0.5f);
    }

    /// <summary>A 4 m road whose centre line lies 0.55 m past a vertex, as one read in game (paved 0, 0.098, 1, 1,
    /// 0.318, 0 across it on whole metres): its grass test, at each spot of its solid core (60 % of the half width),
    /// reads a texel paved over half, so no grass grows on the road's core on either side.</summary>
    [Theory]
    [InlineData(0.55f, true, "context")]
    [InlineData(0.55f, false, "context")]
    [InlineData(0.5f, true, "context")]
    [InlineData(0.25f, false, "context")]
    [InlineData(0.55f, true, "spawn")]
    [InlineData(0.55f, false, "spawn")]
    [InlineData(0.55f, true, "compiler")]
    [InlineData(0.55f, false, "compiler")]
    [InlineData(0.55f, true, "bake")]
    [InlineData(0.55f, false, "bake")]
    [InlineData(0.55f, true, "append")]
    [InlineData(0.55f, false, "append")]
    [InlineData(0.55f, true, "loaded")]
    [InlineData(0.55f, false, "loaded")]
    public void NoGrassGrowsOnTheSolidCore(float offset, bool alongZ, string caller)
    {
        var tc = Build(offset, alongZ, caller);
        float core = 4f * 0.5f * RoadConstants.RoadFlatCoreRatio;
        for (float l = -core + 0.01f; l < core; l += 0.05f)
        {
            // Heightmap.IsCleared: the texel at floor(world - 0.5 + 0.5) on each axis, cleared when paved over half.
            float across = offset + l;
            int t = Mathf.FloorToInt(across);
            Color c = alongZ ? tc.m_paintMask[Index(t, 0)] : tc.m_paintMask[Index(0, t)];
            Assert.True(c.b > 0.5f, $"grass {l:0.00} m from the centre line (texel {t}, paved {c.b:0.000})");
        }
    }

    [Fact]
    public void ARoadOffTheGridLinePaintsTheCellsNearestIt()
    {
        // Road along z = 0.4, paint edge 1.7 m: cell centres z = -1 (1.4 m)
        // through z = 2 (1.6 m) are inside, z = -2 (2.4 m) is not: a cell is centred
        // on its own vertex, so z = 2 is painted too.
        var tc = Build(roadZ: 0.4f);
        foreach (int z in new[] { -1, 0, 1, 2 }) Assert.True(tc.m_modifiedPaint[Index(0, z)], $"z={z} unpainted");
        Assert.False(tc.m_modifiedPaint[Index(0, -2)], "z=-2 painted");
    }
}
