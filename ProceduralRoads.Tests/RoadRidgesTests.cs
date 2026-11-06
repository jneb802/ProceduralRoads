using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// A road laid in a trench (the ground higher on both sides) or along a ridge (lower on both sides) over 10 m or more is
/// a defect (RoadRidges, the one test); a shelf on a hillside, up on one side and down on the other, is not. The generator
/// searches a road off its trenches and ridges (RoadNetworkGenerator.OffRidges) and leaves its shelves as they are.
/// </summary>
public sealed class RoadRidgesTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private readonly float _cap = RoadNetworkGenerator.EarthworkCap;
    private readonly int _retries = RoadNetworkGenerator.RidgeRetries;

    public RoadRidgesTests() => RoadNetworkGenerator.Reset();

    public void Dispose()
    {
        RoadNetworkGenerator.EarthworkCap = _cap;
        RoadNetworkGenerator.RidgeRetries = _retries;
        SetPathfinder(null);
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, pathfinder);

    /// <summary>A straight road along z from 0 to <paramref name="length"/>, a point a metre, 40 m high.</summary>
    private static (List<Vector2> points, List<float> heights) Straight(float length)
    {
        var points = new List<Vector2>();
        for (float z = 0f; z <= length + 1e-3f; z += 1f) points.Add(new Vector2(0f, z));
        return (points, points.Select(_ => 40f).ToList());
    }

    [Fact]
    public void GroundHigherOnBothSidesIsATrench()
    {
        var (points, heights) = Straight(30f);
        var found = RoadRidges.Find(points, heights, p => Mathf.Abs(p.y - 15f) <= 10f ? 46f : 40f);
        var trench = Assert.Single(found);
        Assert.Equal(RoadRidges.Kind.Trench, trench.Kind);
        Assert.InRange(trench.Length, 18f, 20f);
        Assert.Equal(6f, trench.Worst, 3);
    }

    [Fact]
    public void GroundLowerOnBothSidesIsARidge()
    {
        var (points, heights) = Straight(30f);
        var ridge = Assert.Single(RoadRidges.Find(points, heights, p => Mathf.Abs(p.x) < 1f ? 40f : 34f));
        Assert.Equal(RoadRidges.Kind.Ridge, ridge.Kind);
        Assert.InRange(ridge.Length, 29f, 30f);
    }

    /// <summary>A hillside falling 0.9 across the road: 6.3 m up on one side and down on the other, however long.</summary>
    [Fact]
    public void AShelfOnAHillsideIsNeither() =>
        Assert.Empty(RoadRidges.Find(Straight(60f).points, Straight(60f).heights, p => 40f + 0.9f * p.x));

    /// <summary>A road 6 m over ground rising 0.7 across it and 0.9 along it: straight across, the ground stands only 1.1 m
    /// under it on its higher side, but on the diagonal across it falling the other way it stands about 5 m under on both
    /// sides. A ridge.</summary>
    [Fact]
    public void AFillClimbingASlopeIsARidgeOnItsDiagonal()
    {
        float Ground(Vector2 p) => 34f + 0.7f * p.x + 0.9f * p.y;
        var (points, _) = Straight(30f);
        var heights = points.Select(p => Ground(p) + 6f).ToList();
        var straightAcross = points.Select(p => Mathf.Max(Ground(p + new Vector2(7f, 0f)), Ground(p - new Vector2(7f, 0f)))).ToList();
        Assert.All(points.Select((p, i) => heights[i] - straightAcross[i]), d => Assert.True(d < RoadRidges.Depth));
        var ridge = Assert.Single(RoadRidges.Find(points, heights, Ground));
        Assert.Equal(RoadRidges.Kind.Ridge, ridge.Kind);
    }

    /// <summary>A trench 6 m long is a blip, not a defect.</summary>
    [Fact]
    public void AShortTrenchIsNotOne()
    {
        var (points, heights) = Straight(30f);
        Assert.Empty(RoadRidges.Find(points, heights, p => Mathf.Abs(p.y - 15f) <= 3f ? 46f : 40f));
    }

    /// <summary>The road from (0,-80) to (0,80) built on <paramref name="world"/>, searched off its trenches and ridges
    /// <paramref name="retries"/> times; the trenches and ridges of what was stored.</summary>
    private List<RoadRidges.Stretch> Build(WorldGenerator world, int retries, bool stairs = false)
    {
        RoadNetworkGenerator.Reset();
        _scope.WithWorld(world);
        SetPathfinder(new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f, Fords = false, Bridges = false, Stairs = stairs });
        RoadNetworkGenerator.EarthworkCap = 8f;
        RoadNetworkGenerator.RidgeRetries = retries;
        var pieces = new List<RoadSpatialGrid.PlannedPath>();
        var hook = RoadNetworkGenerator.PieceCommitted;
        RoadNetworkGenerator.PieceCommitted = (plan, _) => pieces.Add(plan);
        try
        {
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, -80f), 0f, new Vector2(0f, 80f), 0f, 4f, "over"),
                "refused: " + RoadSpatialGrid.LastRefusal);
        }
        finally { RoadNetworkGenerator.PieceCommitted = hook; }
        Assert.NotEmpty(pieces);
        return RoadRidges.Find(pieces, q => BiomeBlendedHeight.GetBlendedHeight(q.x, q.y, world));
    }

    /// <summary>Flat ground at 60 m with a block <paramref name="high"/> m higher within <paramref name="reach"/> of x = 0 and
    /// 30 m of z = 0, its edges ramped over 20 m, cut by a gully 4 m wide along x = 0 down to the flat ground.</summary>
    private sealed class Gully : WorldGenerator
    {
        private readonly float m_reach, m_high;
        public Gully(float reach, float high = 6f) { m_reach = reach; m_high = high; }
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            float block = Mathf.Clamp01((30f - Mathf.Abs(wy)) / 20f + 1f) * Mathf.Clamp01((m_reach - Mathf.Abs(wx)) / 20f + 1f);
            float wall = Mathf.Clamp01((Mathf.Abs(wx) - 2f) / 2f);
            return 60f + m_high * block * wall;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
        public override int GetSeed() => 77;
    }

    /// <summary>A road along the floor of a gully 6 m deep, 70 m long, is in a trench; searched off it, it climbs onto the
    /// ground beside the gully and is in none.</summary>
    [Fact]
    public void ARoadInATrenchIsSearchedOffIt()
    {
        var gully = new Gully(reach: 20f);
        var plain = Build(gully, retries: 0);
        var trench = Assert.Single(plain);
        Assert.Equal(RoadRidges.Kind.Trench, trench.Kind);
        Assert.True(trench.Length >= 60f, FormattableString.Invariant($"a trench of {trench.Length:F0} m"));
        Assert.Equal(0, RoadNetworkGenerator.RidgeReroutes);

        Assert.Empty(Build(gully, retries: 2));
        Assert.Equal(1, RoadNetworkGenerator.RidgeReroutes);
    }

    /// <summary>A road climbing along a hillside that falls 0.9 across it, cut into the slope as a shelf: no trench or ridge,
    /// nothing re-routed.</summary>
    [Fact]
    public void AShelfOnAHillsideIsLeftAlone()
    {
        Assert.Empty(Build(new Hillside(), retries: 2));
        Assert.Equal(0, RoadNetworkGenerator.RidgeReroutes);
    }

    private sealed class Hillside : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f ? 0f : 80f + 0.9f * wx + 0.3f * wy;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
        public override int GetSeed() => 77;
    }
}
