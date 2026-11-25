using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;
using Xunit.Abstractions;

namespace ProceduralRoads.Tests;

/// <summary>The earthwork noise on a side slope: scaled, octave by octave, to the face's width against the detail's own
/// wavelengths, and kept between the natural ground and the road. A stair end's pad and the road it extends are written by
/// the same cross-section, so their faces cannot be told apart.</summary>
public class FaceNoiseWidthTests
{
    private readonly ITestOutputHelper _output;
    public FaceNoiseWidthTests(ITestOutputHelper output) { _output = output; }

    private const int Width = 64;
    private static int Index(int x, int z) => (z + Width / 2) * (Width + 1) + (x + Width / 2);

    /// <summary>Mistlands everywhere, where vanilla's detail is at its strongest (6 m and 2 m).</summary>
    private sealed class MistWorld : SyntheticWorld
    {
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            Heightmap.Biome.Mistlands;
    }

    /// <summary>The deltas a fill of <paramref name="fill"/> metres leaves, written from <paramref name="points"/> (made at
    /// the natural height at the origin), with the noise on or off and no fill spread; that height; and the natural ground
    /// at each vertex.</summary>
    private static (float[] delta, float natural, float[] ground) Fill(System.Func<float, List<RoadSpatialGrid.RoadPoint>> points, float amplitude)
    {
        using var scope = new ValheimWorldScope();
        using var overrides = StaticOverride.Keep(() => RoadEarthworkNoise.Amplitude).AndKeep(() => RoadEarthworkNoise.FillSpread);
        RoadEarthworkNoise.Amplitude = amplitude; RoadEarthworkNoise.FillSpread = 0f;
        scope.WithWorld(new MistWorld { HasRiver = false, HasMountain = false });
        var zone = new Vector2s(0, 0);
        Heightmap hm = scope.RegisterHeightmap(zone, Width);
        TerrainComp tc = hm.m_terrainComp!;
        float natural = BiomeBlendedHeight.GetBlendedHeight(0f, 0f, WorldGenerator.instance);
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, points(natural), hm, tc);
        var ground = new float[tc.m_levelDelta.Length];
        for (int z = -Width / 2; z <= Width / 2; z++)
            for (int x = -Width / 2; x <= Width / 2; x++)
                ground[Index(x, z)] = BiomeBlendedHeight.GetBlendedHeight(x, z, WorldGenerator.instance);
        return (tc.m_levelDelta.ToArray(), natural, ground);
    }

    private const float FillHeight = 2f;

    /// <summary>A 4 m road along x at z = 0.</summary>
    private static List<RoadSpatialGrid.RoadPoint> Road(float natural)
    {
        var points = new List<RoadSpatialGrid.RoadPoint>();
        for (float x = -30f; x <= 30f; x += 1f)
            points.Add(new RoadSpatialGrid.RoadPoint(new Vector2(x, 0f), 4f, natural + FillHeight));
        return points;
    }

    /// <summary>The same strip as a stair end's pad lays it (RoadEndRing.Pad): a half-metre grid of 1 m points
    /// (<see cref="RoadEndRing.PadPointWidth"/>), its edge where the road's is.</summary>
    private static List<RoadSpatialGrid.RoadPoint> Pad(float natural)
    {
        var points = new List<RoadSpatialGrid.RoadPoint>();
        for (float x = -30f; x <= 30f; x += 0.5f)
            for (float z = -1.5f; z <= 1.5f; z += 0.5f)
                points.Add(new RoadSpatialGrid.RoadPoint(new Vector2(x, z), RoadEndRing.PadPointWidth, natural + FillHeight));
        return points;
    }

    [Fact]
    public void APadAndItsRoadHaveTheSameFaceInTheSameNoise()
    {
        var road = Fill(Road, 1f).delta; var pad = Fill(Pad, 1f).delta;
        float worst = 0f;
        for (int x = -20; x <= 20; x++)
            for (int z = 2; z <= 5; z++)
                worst = Mathf.Max(worst, Mathf.Abs(road[Index(x, z)] - pad[Index(x, z)]));
        _output.WriteLine($"worst road/pad face difference {worst:F3} m");
        // The margin's wander is sampled at each point's own position (pad points sit 1.5 m off the centre line), so not
        // exactly equal; nothing like a different owner's edge.
        Assert.True(worst < 0.15f, $"road and pad faces differ by {worst:F3} m");
    }

    [Fact]
    public void NoFillFaceIsDugBelowTheNaturalGroundAndNoFaceStepsPastTheScaledDetail()
    {
        var (on, _, _) = Fill(Road, 1f); var (off, _, _) = Fill(Road, 0f);
        // The widest face the margin's wander makes, so the most detail any vertex here can carry.
        float face = RoadProfile.FaceWidth(4f, RoadConstants.TerrainBlendMargin * (1f + RoadEarthworkNoise.WidthVariation));
        float steepOn = 0f, steepOff = 0f, scaled = 0f, full = 0f;
        for (int x = -20; x <= 20; x++)
        {
            for (int z = -6; z <= 6; z++)
                Assert.True(on[Index(x, z)] >= -1e-4f, $"({x},{z}) dug {on[Index(x, z)]:F3} m below the natural ground");
            for (int z = 2; z < 6; z++)
            {
                steepOn = Mathf.Max(steepOn, Mathf.Abs(on[Index(x, z)] - on[Index(x, z + 1)]));
                steepOff = Mathf.Max(steepOff, Mathf.Abs(off[Index(x, z)] - off[Index(x, z + 1)]));
                scaled = Mathf.Max(scaled, Mathf.Abs(RoadEarthworkNoise.Detail(x, z, true, face)));
                full = Mathf.Max(full, Mathf.Abs(RoadEarthworkNoise.Detail(x, z, true)));
            }
        }
        _output.WriteLine($"steepest face step: noise {steepOn:F3} m, plain {steepOff:F3} m; most detail on the face {scaled:F3} m (full {full:F3} m)");
        // A neighbour step on the face is the plain profile's plus at most the bump, which is at most the detail scaled
        // to the face; at full height the same detail would have allowed several times that.
        Assert.True(steepOn <= steepOff + scaled + 1e-3f, $"face step {steepOn:F3} m past plain {steepOff:F3} + scaled detail {scaled:F3}");
        Assert.True(scaled < 0.6f * full, $"scaled {scaled:F3} m against full {full:F3} m");
    }

    [Theory]
    [InlineData(2f)]
    [InlineData(-2f)]
    public void LoudNoiseNeverTakesAFacePastTheNaturalGroundOrTheRoad(float earthwork)
    {
        // Four times vanilla's detail: bumps far past what the face can hold, so only the bound keeps the face between
        // the natural ground (delta 0) and the road.
        var (delta, natural, ground) = Fill(n =>
        {
            var points = new List<RoadSpatialGrid.RoadPoint>();
            for (float x = -30f; x <= 30f; x += 1f)
                points.Add(new RoadSpatialGrid.RoadPoint(new Vector2(x, 0f), 4f, n + earthwork));
            return points;
        }, 4f);
        for (int x = -20; x <= 20; x++)
            for (int z = -6; z <= 6; z++)
            {
                float road = natural + earthwork - ground[Index(x, z)];
                Assert.InRange(delta[Index(x, z)], Mathf.Min(0f, road) - 1e-3f, Mathf.Max(0f, road) + 1e-3f);
            }
    }

    [Fact]
    public void AFillFaceAtAStairFootIsNoLongerAWall()
    {
        // A stair foot on the road from Mistlands_GuardTower2_new to Crypt4: the vertex (8875, -86), 3 m off the road's centre
        // (8872, -86.24) on a 1.77 m sidehill fill (road 36.07, natural ground 34.3). Its blend 0.515; the bump at full
        // detail was -1.25 m, which put the vertex 0.34 m below the natural ground: the whole fill fell in one metre.
        using var scope = new ValheimWorldScope();
        scope.WithWorld(new MistWorld { HasRiver = false, HasMountain = false });
        var vertex = new Vector2(8875f, -86f);
        float margin = RoadConstants.TerrainBlendMargin * RoadEarthworkNoise.MarginFactor(new Vector2(8872f, -86.24347f));
        float blend = RoadProfile.LevelBlend(3.0f, 4f, margin);
        float earthwork = 36.07f - 34.3f;
        float full = RoadEarthworkNoise.Detail(vertex.x, vertex.y, true) * 4f * blend * (1f - blend) * Mathf.Min(1f, earthwork / 2f);
        float bump = RoadEarthworkNoise.FaceBump(vertex, blend, earthwork, RoadProfile.FaceWidth(4f, margin));
        _output.WriteLine($"blend {blend:F3}, face {RoadProfile.FaceWidth(4f, margin):F2} m: full-detail bump {full:F3} m, scaled {bump:F3} m");
        Assert.InRange(full, -1.35f, -1.15f);
        Assert.InRange(bump, -0.45f, -0.1f);
        // And above the natural ground with it.
        Assert.True(Mathf.Lerp(34.3f, 36.07f, blend) + bump > 34.3f);
    }

    [Fact]
    public void APadsFaceAtAStairFootNeverStandsAboveTheRoad()
    {
        // A stair foot at (-2496, 6848) (Mistlands, road 37.41): the start pad's face south of the foot rose to
        // 38.1-38.45 at z 6845, a metre above the road, over natural ground at 33.4-34.3: the pad's noise, positive on
        // a fill face, put the face over the deck. Kept between the ground and the road, it can rise no higher.
        using var scope = new ValheimWorldScope();
        scope.WithWorld(new MistWorld { HasRiver = false, HasMountain = false });
        const float road = 37.41f, natural = 33.8f;
        var pad = new List<RoadSpatialGrid.RoadPoint>();
        for (float x = -2498f; x <= -2494f; x += 0.5f)
            for (float z = 6846f; z <= 6850f; z += 0.5f)
                if ((new Vector2(x, z) - new Vector2(-2496f, 6848f)).magnitude <= 2f + 1e-3f)
                    pad.Add(new RoadSpatialGrid.RoadPoint(new Vector2(x, z), RoadEndRing.PadPointWidth, road, unpainted: true));
        var vertex = new Vector2(-2496f, 6845f);
        float h = RoadTerrainModifier.ModelledHeight(pad, vertex, natural, null);
        // The same vertex at full detail and unbounded, as the writer had it: over the road.
        float margin = RoadConstants.TerrainBlendMargin * RoadEarthworkNoise.MarginFactor(new Vector2(-2496f, 6846f));
        float blend = RoadProfile.LevelBlend(1f, RoadEndRing.PadPointWidth, margin);
        float before = Mathf.Lerp(natural, road, blend)
            + RoadEarthworkNoise.Detail(vertex.x, vertex.y, true) * 4f * blend * (1f - blend) * Mathf.Min(1f, (road - natural) / 2f);
        _output.WriteLine($"pad face at (-2496, 6845): {h:F2} (before {before:F2}), road {road:F2}, natural {natural:F2}");
        Assert.True(before > road + 0.3f, $"before {before:F2}: the bump is not reproduced");
        Assert.InRange(h, natural, road + 1e-3f);
    }
}
