using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

public class SharedZoneWriterTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RealWriterMeetsAcrossZonesAndPreservesPreexistingVerge(bool reverse)
    {
        using var scope = new ValheimWorldScope();
        using var overrides = StaticOverride.Keep(() => RoadEarthworkNoise.Amplitude)
            .AndKeep(() => RoadEarthworkNoise.FillSpread)
            .AndKeep(() => RoadTerrainModifier.BatterPerMetre);
        var plane = new PlaneTerrain(64, 0, .25f);
        var paint = new Color(.2f, .4f, .6f, .3f);
        var heightmaps = new Heightmap[2];
        try
        {
            scope.WithTerrain(plane).WithZdos();
            RoadTerrainModifier.ResetForWorld(); RoadSpatialGrid.Clear(); RoadSpatialGrid.FinalizeRoadNetwork();
            RoadEarthworkNoise.Amplitude = 0; RoadEarthworkNoise.FillSpread = 0; RoadTerrainModifier.BatterPerMetre = 0;
            var points = new List<RoadSpatialGrid.RoadPoint>();
            for (int x = 16; x <= 48; x++) points.Add(new RoadSpatialGrid.RoadPoint(new Vector2(x, 0), 4, 67));
            foreach (int zone in reverse ? new[] { 1, 0 } : new[] { 0, 1 })
            {
                var hm = scope.RegisterHeightmap(new Vector2s(zone, 0));
                heightmaps[zone] = hm;
                hm.AuthoredHeight = plane.GetHeight;
                var tc = hm.m_terrainComp!;
                for (int i = 0; i < tc.m_paintMask.Length; i++) tc.m_paintMask[i] = paint;
                // An earlier writer raised this off-road vertex by 1m.
                int verge = 44 * 65 + (zone == 0 ? 64 : 0);
                tc.m_levelDelta[verge] = 1; tc.m_modifiedHeight[verge] = true;
                hm.RebuildTerrain();
                Assert.Equal(64f, hm.LastRenderedHeights![32 * 65 + (zone == 0 ? 64 : 0)], 3);
                RoadTerrainModifier.ApplyRoadTerrainModsWithContext(new Vector2s(zone, 0), points, hm, tc);

                var once = TerrainSnapshot.Of(tc);
                var renderedOnce = hm.LastRenderedHeights!.ToArray();
                int saves = tc.SaveCount;
                RoadTerrainModifier.ApplyRoadTerrainMods(new Vector2s(zone, 0), points);
                Assert.Equal(saves, tc.SaveCount);
                TerrainAssert.Unchanged(once, tc, $"zone {zone} after repeat write");
                Assert.Equal(renderedOnce, hm.LastRenderedHeights!.ToArray());
            }
            TerrainAssert.SeamAgrees(heightmaps[0], heightmaps[1]);
            for (int zone = 0; zone < 2; zone++)
            {
                int edge = zone == 0 ? 64 : 0;
                var hm = heightmaps[zone]; var tc = hm.m_terrainComp!;
                Assert.Equal(67f, hm.LastRenderedHeights![32 * 65 + edge], 3);
                Assert.Equal(new Color(0, 0, 1, .3f), tc.m_paintMask[32 * 65 + edge]);
                Assert.Equal(68f, hm.LastRenderedHeights[44 * 65 + edge], 3);
                Assert.Equal(paint, tc.m_paintMask[44 * 65 + edge]);
            }
        }
        finally { RoadTerrainModifier.ResetForWorld(); RoadSpatialGrid.Clear(); }
    }
}
