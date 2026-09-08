using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The cross-section on roads that do not line up with the vertex grid: at
/// an angle, offset by a fraction of a vertex, and at the minimum width.
/// Every vertex is judged by its exact lateral distance from the centreline,
/// so the checks hold whatever the grid alignment: the flat core is leveled
/// to the road height, paint is solid in the core and absent outside the
/// paint edge, terrain beyond the blend margin is untouched, and the paint
/// along the centreline has no gaps.
/// </summary>
public class RoadGeometryTests
{
    private const int Width = 64;
    private const float Lift = 1.5f;

    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => 40f;
    }

    private static int Index(int vx, int vz) => vz * (Width + 1) + vx;

    /// <summary>A straight road through the zone: direction at angleDeg, passing offset metres beside the zone centre.</summary>
    private static (TerrainComp tc, Vector2 dir, Vector2 origin, List<RoadSpatialGrid.RoadPoint> points) Build(float angleDeg, float offset, float width)
    {
        var world = new FlatWorld();
        WorldGenerator.instance = world;
        float a = angleDeg * Mathf.PI / 180f;
        var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
        var normal = new Vector2(-dir.y, dir.x);
        var origin = new Vector2(normal.x * offset, normal.y * offset);

        var points = new List<RoadSpatialGrid.RoadPoint>();
        float step = width / 4f; // AddRoadPath's dense spacing
        for (float s = -60f; s <= 60f; s += step)
            points.Add(new RoadSpatialGrid.RoadPoint(new Vector2(origin.x + dir.x * s, origin.y + dir.y * s), width, 40f + Lift));

        Heightmap hm = Heightmap.CreateForZone(new Vector2i(0, 0), Width);
        Heightmap.Registered = hm;
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(new Vector2i(0, 0), points, hm, hm.m_terrainComp!);
        return (hm.m_terrainComp!, dir, origin, points);
    }

    private static void Cleanup()
    {
        Heightmap.Registered = null;
        WorldGenerator.instance = null;
    }

    [Theory]
    [InlineData(0f, 0f, 4f)]      // grid-aligned (the case the profile tests cover)
    [InlineData(0f, 0.5f, 4f)]    // half a vertex off the grid
    [InlineData(45f, 0f, 4f)]     // diagonal
    [InlineData(30f, 0.3f, 4f)]   // an odd angle and a fractional offset
    public void CrossSectionHoldsAtAnyAngleOffsetAndWidth(float angle, float offset, float width)
    {
        try
        {
            var (tc, dir, origin, points) = Build(angle, offset, width);
            float halfW = width * 0.5f;
            float flatCore = halfW * RoadConstants.RoadFlatCoreRatio;
            float paintEdge = halfW * RoadConstants.RoadPaintOuterRatio;
            float outer = halfW + RoadConstants.TerrainBlendMargin;
            int leveled = 0, painted = 0, unpainted = 0, untouched = 0;

            for (int vz = 0; vz <= Width; vz++)
            for (int vx = 0; vx <= Width; vx++)
            {
                float wx = vx - Width / 2f, wz = vz - Width / 2f;   // zone centred on the origin, 1 m vertices
                float along = (wx - origin.x) * dir.x + (wz - origin.y) * dir.y;
                if (Mathf.Abs(along) > 24f) continue;                // well inside the road's length
                float lateral = Mathf.Abs(-(wx - origin.x) * dir.y + (wz - origin.y) * dir.x);
                int i = Index(vx, vz);

                if (lateral <= flatCore - 0.05f)
                {
                    Assert.True(tc.m_modifiedHeight[i], $"({wx},{wz}) lateral {lateral:F2}: core vertex not leveled");
                    Assert.True(Mathf.Abs(tc.m_levelDelta[i] - Lift) < 0.05f,
                        $"({wx},{wz}) lateral {lateral:F2}: delta {tc.m_levelDelta[i]:F2}, expected {Lift:F2}");
                    leveled++;
                }
                if (lateral >= outer + 0.05f)
                {
                    Assert.False(tc.m_modifiedHeight[i], $"({wx},{wz}) lateral {lateral:F2}: leveled beyond the blend margin");
                    Assert.False(tc.m_modifiedPaint[i], $"({wx},{wz}) lateral {lateral:F2}: painted beyond the blend margin");
                    untouched++;
                }
                // Paint is placed from road points rounded to vertices, so allow
                // half a vertex of slack beyond the paint edge.
                if (lateral >= paintEdge + 0.6f)
                {
                    Assert.False(tc.m_modifiedPaint[i], $"({wx},{wz}) lateral {lateral:F2}: painted outside the paint edge");
                    unpainted++;
                }
            }

            // No gaps: the vertex nearest to every road point carries paint.
            foreach (var rp in points)
            {
                if (Mathf.Abs((rp.p.x - origin.x) * dir.x + (rp.p.y - origin.y) * dir.y) > 24f) continue;
                int vx = Mathf.RoundToInt(rp.p.x) + Width / 2, vz = Mathf.RoundToInt(rp.p.y) + Width / 2;
                Assert.True(tc.m_modifiedPaint[Index(vx, vz)], $"road point ({rp.p.x:F1},{rp.p.y:F1}): nearest vertex has no paint (gap)");
                painted++;
            }
            Assert.True(leveled > 20 && painted > 20 && unpainted > 20 && untouched > 100,
                $"too few vertices judged: leveled {leveled}, painted {painted}, unpainted {unpainted}, untouched {untouched}");
        }
        finally { Cleanup(); }
    }
}
