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
        Assert.InRange(trench.Length, 18f, 21f);
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

    /// <summary>A ridge 10.8 m long (z 3.1 to 13.9) read every 2 m: its first and last readings that find it, at z 4 and 12,
    /// are 8 m apart, but each reading stands for the road half way to the next, so it is measured at 10 m and found.</summary>
    [Fact]
    public void ARidgeIsMeasuredHalfWayToTheReadingsBesideIt()
    {
        var (points, heights) = Straight(30f);
        var ridge = Assert.Single(RoadRidges.Find(points, heights, p => p.y >= 3.1f && p.y <= 13.9f ? 34f : 40f));
        Assert.Equal(RoadRidges.Kind.Ridge, ridge.Kind);
        Assert.Equal(10f, ridge.Length, 3);
    }

    /// <summary>A ridge 14 m long whose road is two pieces meeting half way (a road onto a pad, a piece carried on from
    /// another): neither piece rides 10 m of it, but the road does. One ridge of the length of both, read over the road's
    /// pieces as one; two pieces that do not meet are two roads.</summary>
    [Fact]
    public void ARidgeSplitBetweenTwoPiecesIsOneRidge()
    {
        static float Ground(Vector2 p) => p.y >= 3f && p.y <= 17f ? 34f : 40f;
        RoadSpatialGrid.PlannedPath Piece(float from, float to, float x = 0f)
        {
            var points = new List<Vector2>();
            for (float z = from; z <= to + 1e-3f; z += 1f) points.Add(new Vector2(x, z));
            return new RoadSpatialGrid.PlannedPath(points, points.Select(_ => 40f).ToList(), new List<RoadPointDebugInfo>(), 4f, 0f, false);
        }
        var ridge = Assert.Single(RoadRidges.Find(new[] { Piece(0f, 10f), Piece(11f, 20f) }, Ground));
        Assert.Equal(RoadRidges.Kind.Ridge, ridge.Kind);
        Assert.InRange(ridge.Length, RoadRidges.MinLength, 15f);
        Assert.Empty(RoadRidges.Find(new[] { Piece(0f, 10f), Piece(0f, 10f, x: 30f) }, Ground));
    }

    /// <summary>A road's trench is one only where it is RoadRidges.TrenchMinLength long or RoadRidges.TrenchMinDepth deep: a
    /// notch 14 m long and 3.7 m deep (the median trench measured on two worlds) is built; one 14 m long and 4.5 m deep, or
    /// 24 m long and 2.5 m deep, is a trench. A ridge 14 m long and 2.5 m high is still one.</summary>
    [Theory]
    [InlineData(14f, 3.7f, false)]
    [InlineData(14f, 4.5f, true)]
    [InlineData(24f, 2.5f, true)]
    public void OnlyALongOrDeepTrenchIsATrench(float length, float depth, bool trench)
    {
        var points = new List<Vector2>();
        for (float z = 0f; z <= 40f + 1e-3f; z += 1f) points.Add(new Vector2(0f, z));
        var piece = new RoadSpatialGrid.PlannedPath(points, points.Select(_ => 40f).ToList(), new List<RoadPointDebugInfo>(), 4f, 0f, false);
        bool On(Vector2 p) => p.y >= 5f && p.y <= 5f + length;
        var found = RoadRidges.Find(new[] { piece }, p => On(p) && Mathf.Abs(p.x) > 1f ? 40f + depth : 40f);
        Assert.Equal(trench, found.Exists(st => st.Kind == RoadRidges.Kind.Trench));
        var ridges = RoadRidges.Find(new[] { piece }, p => On(p) && Mathf.Abs(p.x) > 1f ? 40f - 2.5f : 40f);
        Assert.Single(ridges, st => st.Kind == RoadRidges.Kind.Ridge);
    }

    /// <summary>The three crests whose refusal lost Mistlands viaduct roads, as the roads logs measured them on two generated
    /// worlds after 12 Nov's half-reading measure: 2.2 m over the ground on both sides for 11 m on the road past a 68 m valley
    /// viaduct, 2.3 m for 12 m between a 40 m valley viaduct and an 85.5 m river viaduct on a town entrance's road, and 3.1 m
    /// for 12 m on an island's start road to a town entrance. In the Mistlands (the owner's decision of 13 Nov 2026) each is
    /// shorter than RoadRules.Mistlands.Ridge.Long and no deeper than its Depth, and is no ridge; outside
    /// them each is still one.</summary>
    [Theory]
    [InlineData(11f, 2.2f)]
    [InlineData(12f, 2.3f)]
    [InlineData(12f, 3.1f)]
    public void TheTracedShortCrestsOfLostMistlandsViaductRoadsAreRidgesOnlyOutsideTheMistlands(float length, float height)
    {
        var points = new List<Vector2>();
        for (float z = 0f; z <= 40f + 1e-3f; z += 1f) points.Add(new Vector2(0f, z));
        var piece = new RoadSpatialGrid.PlannedPath(points, points.Select(_ => 40f).ToList(), new List<RoadPointDebugInfo>(), 4f, 0f, false);
        // Readings a metre apart, each standing for the road half way to the next: length - 1 m of crest reads as length.
        bool On(Vector2 p) => p.y >= 5f && p.y <= 5f + length - 1f;
        float Ground(Vector2 p) => On(p) && Mathf.Abs(p.x) > 1f ? 40f - height : 40f;
        var outside = Assert.Single(RoadRidges.Find(new[] { piece }, Ground));
        Assert.Equal(RoadRidges.Kind.Ridge, outside.Kind);
        Assert.Equal(length, outside.Length, 2);
        Assert.Equal(height, outside.Worst, 2);
        Assert.Empty(RoadRidges.Find(new[] { piece }, Ground, biomeAt: _ => Heightmap.Biome.Mistlands));
    }

    /// <summary>Two runs of one road that do not meet (either side of a viaduct): the first, at x 0, rides a 12 m crest
    /// 2.5 m high; the second, at x 100, a 12 m crest 5 m high over the same metres of its own run. In the Mistlands the
    /// deep one is a ridge and the shallow one is not: a stretch is judged against the deep stretches of its own run, so the
    /// runs' positions along the road never overlap.</summary>
    [Fact]
    public void AStretchIsNeverJudgedByADeepStretchOnAnotherRun()
    {
        static float Ground(Vector2 p)
        {
            float road = p.x > 50f ? 100f : 0f;
            if (p.y < 5f || p.y > 16f || Mathf.Abs(p.x - road) <= 1f) return 40f;
            return road > 0f ? 40f - 5f : 40f - 2.5f;
        }
        var shallow = Line(new Vector2(0f, 0f), new Vector2(0f, 40f));
        var deep = Line(new Vector2(100f, 0f), new Vector2(100f, 40f));
        var found = RoadRidges.Find(new[] { shallow, deep }, Ground, biomeAt: _ => Heightmap.Biome.Mistlands);
        var ridge = Assert.Single(found);
        Assert.Equal(5f, ridge.Worst, 2);
        Assert.All(ridge.Points, p => Assert.Equal(100f, p.x, 3));
    }

    /// <summary>A Mistlands road along z from a viaduct's bank end at the origin, riding a 12 m crest 2.5 m high starting
    /// <paramref name="from"/> metres along it: a ridge by the global rule, none by the Mistlands override.</summary>
    private static List<RoadRidges.Stretch> CrestFromAViaduct(float from, RoadRidges.Viaducts? viaducts)
    {
        float reach = RoadRules.MistlandsRidgeViaductReach;
        float length = (float.IsPositiveInfinity(reach) ? 300f : reach) + 100f;
        bool On(Vector2 p) => p.y >= from && p.y <= from + 11f;
        float Ground(Vector2 p) => On(p) && Mathf.Abs(p.x) > 1f ? 40f - 2.5f : 40f;
        return RoadRidges.Find(new[] { Line(new Vector2(0f, 0f), new Vector2(0f, length)) }, Ground, _ => Heightmap.Biome.Mistlands, viaducts);
    }

    private static readonly Vector2[] AtTheStart = { new(0f, 0f) };

    /// <summary>The Mistlands override's scope (RoadRules.MistlandsRidgeViaductReach, RoadRidges.Judged): a crest within the
    /// reach of a viaduct's end, travelled along the road, is no ridge; one beyond it is, as the global rule says. An
    /// infinite reach keeps the override over the whole of a road with a viaduct of its own, and never over one without.</summary>
    [Fact]
    public void TheMistlandsOverrideHoldsOnlyWithinItsReachOfAViaduct()
    {
        float reach = RoadRules.MistlandsRidgeViaductReach;
        Assert.Equal(reach, RoadRules.Mistlands.Ridge!.ViaductReach);
        var own = new RoadRidges.Viaducts(AtTheStart, Array.Empty<Vector2>());
        var laid = new RoadRidges.Viaducts(Array.Empty<Vector2>(), AtTheStart);
        var none = new RoadRidges.Viaducts(Array.Empty<Vector2>(), Array.Empty<Vector2>());
        if (float.IsPositiveInfinity(reach))
        {
            Assert.Empty(CrestFromAViaduct(250f, own));
            Assert.Single(CrestFromAViaduct(250f, laid));
            Assert.Single(CrestFromAViaduct(250f, none));
        }
        else
        {
            Assert.Empty(CrestFromAViaduct(reach - 20f, own));
            Assert.Empty(CrestFromAViaduct(reach - 20f, laid));
            Assert.Single(CrestFromAViaduct(reach + 20f, own));
            Assert.Single(CrestFromAViaduct(reach + 20f, laid));
            Assert.Single(CrestFromAViaduct(reach - 20f, none));
        }
        // Read without viaducts (the offline tools), the override holds everywhere.
        Assert.Empty(CrestFromAViaduct(150f, null));
    }

    /// <summary>A viaduct's end off the road counts its distance to the road's nearest point too: an end 30 m beside the road's
    /// start, with a crest 20 m short of the reach along it, is past the reach.</summary>
    [Fact]
    public void AViaductEndOffTheRoadIsTravelledToo()
    {
        float reach = RoadRules.MistlandsRidgeViaductReach;
        if (float.IsPositiveInfinity(reach)) return;
        var beside = new RoadRidges.Viaducts(new[] { new Vector2(-30f, 0f) }, Array.Empty<Vector2>());
        Assert.Single(CrestFromAViaduct(reach - 20f, beside));
        Assert.Empty(CrestFromAViaduct(reach - 40f, beside));
    }

    /// <summary>A road 9 m long, two pieces meeting at 5 m, all of it on a ridge: the road between their stretches is
    /// counted once, so it is 9 m, under RoadRidges.MinLength, and no ridge (its two pieces' stretches and the whole gap
    /// between their readings once added up to 10.5 m).</summary>
    [Fact]
    public void ARidgeOverTwoPiecesIsTheRoadItCovers()
    {
        var found = RoadRidges.Find(new[] { Line(new Vector2(0f, 0f), new Vector2(0f, 5f)), Line(new Vector2(0f, 5f), new Vector2(0f, 9f)) }, _ => 34f);
        Assert.Empty(found);
    }

    /// <summary>A hairpin: a leg 8 m along a ridge, a long piece away off it and back, and a leg back down beside the first,
    /// 2.5 m from it. Along the road the two legs are 50 m apart, so neither ridge reaches RoadRidges.MinLength; read by
    /// straight-line distance they were joined into one.</summary>
    [Fact]
    public void TwoLegsBesideEachOtherAreNotOneRidge()
    {
        static float Ground(Vector2 p) => p.y <= 9f ? 34f : 40f;
        var legOut = Line(new Vector2(0f, 0f), new Vector2(0f, 8f));
        var away = Line(new Vector2(0f, 8f), new Vector2(0f, 30f), new Vector2(2.5f, 30f), new Vector2(2.5f, 8f));
        var legBack = Line(new Vector2(2.5f, 8f), new Vector2(2.5f, 0f));
        Assert.Empty(RoadRidges.Find(new[] { legOut, away, legBack }, Ground));
    }

    /// <summary>A planned piece along <paramref name="corners"/>, a point a metre, 40 m high.</summary>
    private static RoadSpatialGrid.PlannedPath Line(params Vector2[] corners)
    {
        var points = new List<Vector2> { corners[0] };
        for (int c = 1; c < corners.Length; c++)
        {
            int n = Mathf.Max(1, Mathf.RoundToInt(Vector2.Distance(corners[c - 1], corners[c])));
            for (int i = 1; i <= n; i++) points.Add(Vector2.Lerp(corners[c - 1], corners[c], i / (float)n));
        }
        return new RoadSpatialGrid.PlannedPath(points, points.Select(_ => 40f).ToList(), new List<RoadPointDebugInfo>(), 4f, 0f, false);
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
    /// <paramref name="retries"/> times; the trenches and ridges of what was stored, null where it was refused (its reason in
    /// RoadSpatialGrid.LastRefusal).</summary>
    private List<RoadRidges.Stretch>? Build(WorldGenerator world, int retries, bool stairs = false)
    {
        RoadNetworkGenerator.Reset();
        _scope.WithWorld(world);
        SetPathfinder(new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f, Fords = false, Bridges = false, Stairs = stairs });
        RoadNetworkGenerator.EarthworkCap = RoadNetworkGenerator.DefaultEarthworkCap;
        RoadNetworkGenerator.RidgeRetries = retries;
        var pieces = new List<RoadSpatialGrid.PlannedPath>();
        var hook = RoadNetworkGenerator.PieceCommitted;
        RoadNetworkGenerator.PieceCommitted = (plan, _) => pieces.Add(plan);
        try
        {
            if (!RoadNetworkGenerator.GenerateRoad(new Vector2(0f, -80f), 0f, new Vector2(0f, 80f), 0f, 4f, "over"))
                return null;
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

    /// <summary>A road along the floor of a gully 6 m deep, 70 m long, is in a trench: not searched off it, it is refused for
    /// it; searched off it, it climbs onto the ground beside the gully and is in none.</summary>
    [Fact]
    public void ARoadInATrenchIsSearchedOffIt()
    {
        var gully = new Gully(reach: 20f);
        Assert.Null(Build(gully, retries: 0));
        Assert.StartsWith(RoadNetworkGenerator.TrenchRoadRefusal, RoadSpatialGrid.LastRefusal ?? "");
        Assert.Equal(0, RoadNetworkGenerator.RidgeReroutes);

        var off = Build(gully, retries: 2);
        Assert.True(off != null, "refused: " + RoadSpatialGrid.LastRefusal);
        Assert.Empty(off!);
        Assert.Equal(1, RoadNetworkGenerator.RidgeReroutes);
    }

    /// <summary>A road climbing along a hillside that falls 0.9 across it, cut into the slope as a shelf: no trench or ridge,
    /// nothing re-routed.</summary>
    [Fact]
    public void AShelfOnAHillsideIsLeftAlone()
    {
        Assert.Empty(Build(new Hillside(), retries: 2) ?? throw new Xunit.Sdk.XunitException("refused: " + RoadSpatialGrid.LastRefusal));
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
