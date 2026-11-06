using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The road carried on onto a staircase's end runs along this road's own path (RoadStairRuns.Line), which the search walked
/// by RoadWater.Walked, wading a swamp's shallows. The carry reads water by the same rule (RoadStairRuns.CarryWater): a foot
/// whose road runs on across swamp shallows that its own road already crosses is carried, where reading the same points
/// as ground a road is moved onto (RoadWater.BuiltDry, never waded) kept its floors for water.
/// The world here is rebuilt from numbers read in game (WorldGenerator.GetBiomeHeight per biome, sampled live) round a
/// staircase foot at (4295.1, -3513.8) on a road from a coastal landing to the Bonemass altar: zone (4288, -3520), its
/// heightmap's south corners Black Forest and its north corners Swamp, the points along the road Swamp, no river.
/// There the rendered ground falls from 36.8 m at the foot to under the shallow line 20 m on (30.31 m at 4302.3, -3494.1),
/// where the live road stands 32.3 to 33.3 m on the shallows it fills.
/// </summary>
public class CarryWadeTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public CarryWadeTests() => RoadNetworkGenerator.Reset();

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    // Read in game: GetBiomeHeight(BlackForest) and GetBiomeHeight(Swamp) on a 4 m grid round the carry, and at 1 m steps
    // along the carry's line from the foot to the zone's north edge.
    // Grid: rows z = -3528 .. -3488 (south to north), each x = 4280 .. 4316; (BlackForest, Swamp) per point.
    internal static readonly float[,] GridBlackForest = {
        { 35.326f, 38.008f, 41.668f, 45.060f, 45.628f, 47.579f, 49.276f, 50.177f, 52.397f, 54.448f },
        { 35.431f, 37.879f, 41.675f, 45.422f, 46.250f, 47.296f, 49.187f, 50.781f, 53.158f, 55.738f },
        { 37.327f, 39.552f, 42.534f, 45.724f, 47.291f, 48.192f, 50.153f, 52.300f, 54.420f, 56.967f },
        { 40.052f, 42.809f, 44.744f, 46.314f, 48.085f, 49.069f, 51.286f, 52.685f, 54.000f, 56.560f },
        { 42.438f, 45.235f, 45.527f, 47.010f, 48.960f, 50.286f, 52.653f, 54.182f, 53.981f, 55.629f },
        { 44.615f, 45.811f, 45.803f, 47.714f, 49.626f, 50.650f, 52.843f, 55.718f, 55.173f, 55.330f },
        { 46.581f, 46.509f, 46.427f, 48.083f, 49.643f, 50.086f, 51.759f, 53.963f, 54.432f, 54.495f },
        { 46.829f, 47.001f, 46.772f, 47.939f, 49.627f, 50.909f, 50.948f, 51.350f, 52.948f, 53.873f },
        { 45.199f, 45.603f, 46.686f, 48.571f, 50.247f, 51.557f, 52.154f, 51.484f, 53.104f, 53.717f },
        { 43.611f, 44.361f, 46.652f, 48.566f, 49.466f, 50.885f, 51.900f, 51.701f, 52.051f, 53.018f },
        { 43.434f, 44.796f, 47.142f, 48.027f, 48.363f, 49.269f, 50.760f, 51.135f, 51.225f, 52.554f },
    };
    internal static readonly float[,] GridSwamp = {
        { 29.663f, 29.929f, 29.264f, 28.628f, 28.899f, 29.922f, 31.229f, 30.779f, 29.392f, 28.907f },
        { 29.533f, 30.067f, 29.526f, 29.052f, 28.915f, 29.898f, 31.329f, 31.334f, 30.257f, 29.426f },
        { 28.934f, 28.999f, 29.014f, 28.600f, 28.701f, 29.181f, 30.096f, 31.607f, 31.582f, 30.578f },
        { 28.652f, 28.333f, 28.502f, 29.056f, 29.689f, 29.385f, 29.337f, 30.692f, 31.778f, 31.812f },
        { 29.353f, 28.833f, 28.932f, 30.779f, 31.526f, 30.896f, 30.217f, 29.614f, 30.429f, 31.442f },
        { 30.355f, 29.453f, 29.740f, 30.867f, 31.288f, 31.528f, 31.134f, 29.941f, 30.020f, 30.818f },
        { 29.904f, 29.491f, 29.231f, 29.078f, 29.328f, 30.748f, 31.451f, 30.299f, 30.049f, 30.380f },
        { 29.704f, 29.749f, 29.088f, 28.761f, 28.581f, 29.909f, 31.107f, 30.579f, 30.460f, 31.246f },
        { 29.351f, 29.531f, 29.158f, 28.947f, 28.562f, 29.480f, 30.366f, 30.042f, 30.513f, 31.306f },
        { 28.933f, 28.805f, 29.115f, 28.610f, 29.649f, 29.589f, 29.215f, 29.589f, 29.966f, 30.647f },
        { 30.181f, 30.054f, 30.068f, 30.116f, 30.543f, 29.952f, 28.954f, 30.495f, 31.166f, 30.544f },
    };
    private static readonly (float x, float z, float bf, float swamp)[] LineSamples = {
        (4293.73f, -3517.56f, 47.088f, 29.048f),
        (4294.07f, -3516.62f, 47.305f, 29.421f),
        (4294.42f, -3515.68f, 47.605f, 29.766f),
        (4294.76f, -3514.74f, 47.877f, 30.135f),
        (4295.10f, -3513.80f, 48.157f, 30.494f),
        (4295.44f, -3512.86f, 48.611f, 31.105f),
        (4295.78f, -3511.92f, 48.895f, 31.596f),
        (4296.13f, -3510.98f, 49.048f, 31.833f),
        (4296.47f, -3510.04f, 49.469f, 31.722f),
        (4296.81f, -3509.10f, 49.888f, 31.504f),
        (4297.15f, -3508.16f, 49.941f, 31.378f),
        (4297.49f, -3507.22f, 50.054f, 30.992f),
        (4297.84f, -3506.28f, 50.273f, 30.738f),
        (4298.18f, -3505.34f, 50.181f, 30.557f),
        (4298.52f, -3504.40f, 50.188f, 30.266f),
        (4298.86f, -3503.46f, 50.087f, 30.128f),
        (4299.21f, -3502.52f, 49.994f, 29.980f),
        (4299.55f, -3501.58f, 50.290f, 29.896f),
        (4299.89f, -3500.64f, 50.746f, 29.981f),
        (4300.23f, -3499.71f, 50.998f, 29.927f),
        (4300.57f, -3498.77f, 51.232f, 30.009f),
        (4300.92f, -3497.83f, 51.614f, 30.018f),
        (4301.26f, -3496.89f, 51.909f, 29.906f),
        (4301.60f, -3495.95f, 52.069f, 29.985f),
        (4301.94f, -3495.01f, 52.120f, 29.933f),
        (4302.28f, -3494.07f, 51.965f, 29.753f),
        (4302.63f, -3493.13f, 51.756f, 29.523f),
        (4302.97f, -3492.19f, 51.714f, 29.201f),
        (4303.31f, -3491.25f, 51.622f, 29.132f),
        (4303.65f, -3490.31f, 51.519f, 29.123f),
        (4304.00f, -3489.37f, 51.282f, 29.129f),
        (4304.34f, -3488.43f, 51.053f, 29.035f),
    };

    private const float GridX0 = 4280f, GridZ0 = -3528f, GridStep = 4f;

    /// <summary>The dumped zone: Black Forest at its heightmap's south corners (z = -3552), Swamp at its north corners
    /// and at every other point (<see cref="PointBiome"/>); each biome's height from the samples along the line where a point
    /// is one of them, else bilinear over the grid. <see cref="Sink"/> lowers every height (real water for the controls).</summary>
    internal sealed class DumpedZone : WorldGenerator
    {
        public Heightmap.Biome PointBiome = Heightmap.Biome.Swamp;
        public float Sink;
        /// <summary>Where set, the point biome is Black Forest south of this z (the border as read in game between the
        /// rows z = -3528, Black Forest, and z = -3520, Swamp), and <see cref="GetHeight"/> is that biome's height.</summary>
        public float? ForestSouthOf;
        /// <summary>River weights read in game at search cell centres; 0 elsewhere.</summary>
        public Dictionary<(int x, int z), float> Rivers = new();

        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false)
        {
            if (Mathf.Abs(wy + 3552f) < 0.01f) return Heightmap.Biome.BlackForest;
            if (Mathf.Abs(wy + 3488f) < 0.01f && (Mathf.Abs(wx - 4256f) < 0.01f || Mathf.Abs(wx - 4320f) < 0.01f)) return Heightmap.Biome.Swamp;
            if (ForestSouthOf is float border && wy < border) return Heightmap.Biome.BlackForest;
            return PointBiome;
        }

        public override float GetBiomeHeight(Heightmap.Biome biome, float wx, float wy, out Color mask, bool preGeneration = false, bool riverPreDN = true)
        {
            mask = default;
            bool forest = biome == Heightmap.Biome.BlackForest;
            foreach (var s in LineSamples)
                if (Mathf.Abs(s.x - wx) < 0.1f && Mathf.Abs(s.z - wy) < 0.1f) return (forest ? s.bf : s.swamp) - Sink;
            return Bilinear(forest ? GridBlackForest : GridSwamp, wx, wy) - Sink;
        }

        public override float GetHeight(float wx, float wy) => GetBiomeHeight(GetBiome(wx, wy), wx, wy, out _);

        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            width = 0f;
            weight = Rivers.TryGetValue((Mathf.RoundToInt(wx), Mathf.RoundToInt(wy)), out float w) && Mathf.Abs(wx - Mathf.Round(wx)) < 0.01f
                && Mathf.Abs(wy - Mathf.Round(wy)) < 0.01f ? w : 0f;
            if (weight > 0f) width = 20f;
        }

        private static float Bilinear(float[,] grid, float wx, float wy)
        {
            float u = Mathf.Clamp((wx - GridX0) / GridStep, 0f, grid.GetLength(1) - 1.001f);
            float v = Mathf.Clamp((wy - GridZ0) / GridStep, 0f, grid.GetLength(0) - 1.001f);
            int i = (int)u, j = (int)v;
            float fu = u - i, fv = v - j;
            float south = Mathf.Lerp(grid[j, i], grid[j, i + 1], fu), north = Mathf.Lerp(grid[j + 1, i], grid[j + 1, i + 1], fu);
            return Mathf.Lerp(south, north, fv);
        }
    }

    private static readonly Vector2 Shallows = new(4302.3f, -3494.1f);

    private DumpedZone World()
    {
        var world = new DumpedZone();
        _scope.WithWorld(world);
        return world;
    }

    private static List<Vector2> Line() => LineSamples.Select(s => new Vector2(s.x, s.z)).ToList();

    /// <summary>The point the carry was refused at: rendered under the shallow line in a swamp, so not dry to a built road,
    /// but waded by the search that laid the road there.</summary>
    [Fact]
    public void TheRefusedPointIsSwampShallowsTheSearchWades()
    {
        var world = World();
        float ground = RoadWater.Ground(world, Shallows);
        Assert.InRange(ground, 30.2f, 30.4f);
        Assert.Equal(RoadWater.Kind.Waded, RoadWater.At(world, Shallows));
        Assert.False(RoadWater.BuiltDry(world, Shallows));
        Assert.True(RoadWater.Walked(world, Shallows));
    }

    /// <summary>Along the carry's line the ground falls from the dry foot into the shallows the road already crosses: read as
    /// the search read it, none is water; read as ground a road is moved onto (the old check), seven points are.</summary>
    [Fact]
    public void TheCarrysLineIsWalkedAsTheSearchWalkedIt()
    {
        var world = World();
        List<Vector2> line = Line();
        Assert.True(RoadWater.Ground(world, line[0]) > 36.5f, "the foot's end of the line is dry land");
        Assert.Null(RoadStairRuns.CarryWater(world, line));
        // The old reading of the same points: the first refusal at the shallows, seven in all (ground 29.0 to 30.3 m).
        List<Vector2> notBuiltDry = line.Where(p => !RoadWater.BuiltDry(world, p)).ToList();
        Assert.Equal(7, notBuiltDry.Count);
        Assert.InRange(notBuiltDry.Min(p => RoadWater.Ground(world, p)), 29.0f, 29.1f);
        Assert.True((notBuiltDry[0] - Shallows).magnitude < 0.1f);
    }

    /// <summary>Real water still refuses the carry: the same ground 2.5 m lower (under the deep line in the swamp), and the
    /// same ground where the points are not swamp (shallows nobody wades).</summary>
    [Fact]
    public void RealWaterStillRefusesTheCarry()
    {
        var world = World();
        List<Vector2> line = Line();
        world.Sink = 2.5f;
        Assert.True(RoadWater.Ground(world, Shallows) < RoadConstants.DeepWaterHeight);
        Assert.Equal(RoadWater.Kind.Deep, RoadWater.At(world, Shallows));
        Assert.StartsWith("water: at (", RoadStairRuns.CarryWater(world, line));
        Assert.Equal("water: at (4302.3, -3494.1)", RoadStairRuns.CarryWater(world, new[] { Shallows }));

        world.Sink = 0f;
        world.PointBiome = Heightmap.Biome.BlackForest;
        Assert.True(RoadWater.Ground(world, Shallows) < RoadConstants.ShallowWaterHeight);
        Assert.Equal(RoadWater.Kind.Shallow, RoadWater.At(world, Shallows));
        Assert.Equal("water: at (4302.3, -3494.1)", RoadStairRuns.CarryWater(world, line));
    }
}
