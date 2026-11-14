using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A 4 m road cut across a mountainside that rises a metre per metre to the east, over a hump, its points about a metre
/// apart: the points of a Mountain road as stored, moved to the origin. Every texel the road reaches takes the road's
/// cross-section (RoadProfile.PaintStrength) out to its painted edge, the strongest any of its points gives it, whatever
/// the points' order. Before, the paint pass skipped each point falling in a 1.5 m cell an earlier point had painted and
/// blended the rest one after another, so a texel on the fade took only the paint of a point up to 1.5 m away: one 1.34 m
/// from the centre line on the cut side was paved 0.04 instead of 0.81 (the edge stopped short, a texel early, on one side
/// and then the other), and the result changed with the order the points came in.
/// </summary>
public sealed class CutPaintTests : IDisposable
{
    private const int Width = 64;
    private readonly ValheimWorldScope _scope = new();

    public void Dispose() => _scope.Dispose();

    /// <summary>The road's points (x, z, height), in the order the zone met them (south to north).</summary>
    private static readonly (float x, float z, float h)[] Points =
    {
        (32.856f, -31.809f, 10.345f),
        (32.267f, -31.022f, 10.366f),
        (31.677f, -30.236f, 10.399f),
        (31.087f, -29.449f, 10.444f),
        (30.497f, -28.663f, 10.475f),
        (30.263f, -28.355f, 10.521f),
        (29.673f, -27.594f, 10.553f),
        (29.083f, -26.834f, 10.548f),
        (28.493f, -26.073f, 10.534f),
        (27.903f, -25.312f, 10.525f),
        (27.312f, -24.552f, 10.544f),
        (26.723f, -23.791f, 10.590f),
        (26.133f, -23.030f, 10.616f),
        (25.543f, -22.270f, 10.605f),
        (24.953f, -21.509f, 10.579f),
        (24.363f, -20.748f, 10.600f),
        (23.821f, -20.074f, 10.686f),
        (23.255f, -19.420f, 10.803f),
        (22.665f, -18.787f, 10.901f),
        (22.054f, -18.176f, 10.942f),
        (21.420f, -17.587f, 10.944f),
        (20.727f, -16.966f, 10.951f),
        (20.033f, -16.344f, 10.977f),
        (19.340f, -15.723f, 11.020f),
        (18.647f, -15.101f, 11.052f),
        (17.954f, -14.480f, 11.042f),
        (17.261f, -13.858f, 11.022f),
        (16.567f, -13.237f, 11.041f),
        (15.874f, -12.638f, 11.102f),
        (15.157f, -12.066f, 11.151f),
        (14.420f, -11.522f, 11.147f),
        (13.662f, -11.007f, 11.113f),
        (12.885f, -10.521f, 11.077f),
        (12.090f, -10.065f, 11.046f),
        (11.278f, -9.639f, 11.040f),
        (10.471f, -9.235f, 11.073f),
        (9.662f, -8.832f, 11.129f),
        (8.854f, -8.427f, 11.207f),
        (8.047f, -8.023f, 11.337f),
        (7.229f, -7.594f, 11.550f),
        (6.431f, -7.133f, 11.845f),
        (5.651f, -6.640f, 12.162f),
        (4.892f, -6.115f, 12.485f),
        (4.153f, -5.561f, 12.808f),
        (3.438f, -4.978f, 13.115f),
        (2.748f, -4.366f, 13.311f),
        (2.082f, -3.728f, 13.438f),
        (1.441f, -3.063f, 13.524f),
        (0.829f, -2.373f, 13.571f),
        (0.245f, -1.658f, 13.621f),
        (-0.354f, -0.897f, 13.672f),
        (-0.951f, -0.135f, 13.690f),
        (-1.549f, 0.626f, 13.666f),
        (-2.147f, 1.387f, 13.652f),
        (-2.730f, 2.162f, 13.629f),
        (-3.283f, 2.960f, 13.556f),
        (-3.802f, 3.780f, 13.426f),
        (-4.287f, 4.619f, 13.284f),
        (-4.738f, 5.478f, 13.086f),
        (-5.182f, 6.364f, 12.801f),
        (-5.624f, 7.250f, 12.479f),
        (-6.067f, 8.136f, 12.135f),
        (-6.510f, 9.022f, 11.788f),
        (-6.953f, 9.908f, 11.466f),
        (-7.396f, 10.794f, 11.186f),
        (-7.839f, 11.680f, 10.950f),
        (-8.282f, 12.566f, 10.712f),
        (-8.726f, 13.452f, 10.474f),
        (-9.168f, 14.338f, 10.293f),
        (-9.611f, 15.224f, 10.137f),
        (-10.055f, 16.109f, 9.978f),
        (-10.497f, 16.995f, 9.806f),
        (-10.940f, 17.881f, 9.586f),
        (-11.383f, 18.767f, 9.365f),
        (-11.826f, 19.653f, 9.135f),
        (-12.270f, 20.539f, 8.920f),
        (-12.712f, 21.425f, 8.743f),
        (-13.155f, 22.311f, 8.590f),
        (-13.598f, 23.197f, 8.483f),
        (-14.041f, 24.083f, 8.413f),
        (-14.484f, 24.969f, 8.338f),
        (-14.927f, 25.855f, 8.285f),
        (-15.370f, 26.741f, 8.230f),
        (-15.813f, 27.627f, 8.168f)
    };

    /// <summary>The ground at the road's height under its centre line, rising a metre per metre to the east of it.</summary>
    private sealed class SideSlope : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            // The road's centre and height at this z, between the two points either side of it (the road runs north
            // throughout), and the ground rising a metre per metre east of it.
            int i = 1;
            while (i < Points.Length - 1 && Points[i].z < wy) i++;
            var (a, b) = (Points[i - 1], Points[i]);
            float t = (wy - a.z) / (b.z - a.z);
            return a.h + (b.h - a.h) * t + (wx - (a.x + (b.x - a.x) * t));
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    private static int Index(int x, int z) => (z + Width / 2) * (Width + 1) + (x + Width / 2);

    private TerrainComp Write(IEnumerable<(float x, float z, float h)> order)
    {
        using var plain = PlainEarthworks.Apply();
        _scope.WithWorld(new SideSlope());
        var zone = new Vector2s(0, 0);
        Heightmap hm = _scope.RegisterHeightmap(zone, Width);
        TerrainComp tc = hm.m_terrainComp!;
        var points = order.Select(p => new RoadSpatialGrid.RoadPoint(new Vector2(p.x, p.z), 4f, p.h)).ToList();
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, points, hm, tc);
        return tc;
    }

    private static float Paved(TerrainComp tc, int x, int z) => tc.m_modifiedPaint[Index(x, z)] ? tc.m_paintMask[Index(x, z)].b : 0f;

    /// <summary>The road's cross-section at a texel: the strongest of its points' paint at the vertex and at its grass
    /// cell's middle, half a cell toward +x/+z.</summary>
    private static float Profile(int x, int z) => Points.Max(p => Mathf.Max(
        RoadProfile.PaintStrength(Vector2.Distance(new Vector2(x, z), new Vector2(p.x, p.z)), 4f),
        RoadProfile.PaintStrength(Vector2.Distance(new Vector2(x + 0.5f, z + 0.5f), new Vector2(p.x, p.z)), 4f)));

    [Fact]
    public void ARoadCutIntoASideSlopeIsPaintedToItsEdgeOnBothSides()
    {
        TerrainComp tc = Write(Points);
        // The texel the game showed paved 0.04, on the cut side 1.34 m from the centre line.
        Assert.True(Paved(tc, -4, 7) > 0.75f, $"the cut side's fade at (-4, 7) paved {Paved(tc, -4, 7):F3}");
        // Inside the zone, clear of its border, where the zone's neighbours write the rest of the road. A vertex beside
        // a face (RoadTerrainModifier.PaintFace: where the road's south end runs across the slope, its fill falls more
        // than that within a metre of the road) is left unpainted by that rule, and is not this test's.
        Func<int, int, float> ground = (x, z) =>
        {
            int i = Index(x, z);
            return WorldGenerator.instance.GetHeight(x, z) + (tc.m_modifiedHeight[i] ? tc.m_levelDelta[i] + tc.m_smoothDelta[i] : 0f);
        };
        int checkedTexels = 0;
        for (int z = -26; z <= 26; z++)
            for (int x = -26; x <= 26; x++)
            {
                float h = ground(x, z);
                bool face = false;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                        face |= Mathf.Abs(ground(x + dx, z + dz) - h) > RoadTerrainModifier.PaintFace;
                if (face) continue;
                float want = Profile(x, z);
                float got = Paved(tc, x, z);
                Assert.True(Mathf.Abs(got - want) < 0.01f, $"({x}, {z}) paved {got:F3}, the road's profile there {want:F3}");
                if (want > 0f) checkedTexels++;
            }
        Assert.True(checkedTexels > 200, $"only {checkedTexels} texels under the road");
    }

    [Fact]
    public void ThePaintDoesNotDependOnThePointsOrder()
    {
        TerrainComp forward = Write(Points);
        TerrainComp backward = Write(Points.Reverse());
        for (int z = -30; z <= 30; z++)
            for (int x = -30; x <= 30; x++)
                Assert.True(Mathf.Abs(Paved(forward, x, z) - Paved(backward, x, z)) < 1e-4f,
                    $"({x}, {z}) paved {Paved(forward, x, z):F3} south to north, {Paved(backward, x, z):F3} north to south");
    }
}
