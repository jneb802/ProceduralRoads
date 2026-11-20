using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A road's own junction fill and a staircase's start pad are part of its line for the ridge and trench judge (the owner's rule
/// of 18 Nov 2026: the player sees one embankment). RoadNetworkGenerator.Ridges reads, with the road's pieces, each fill's
/// stretch carrying its end onto the met road's centreline (RoadJunctionFillet.For) and each pad's stretch from its end onto
/// the staircase (RoadEndRing.Pad.Line through RoadStairRuns.StartPads), from the one computation its commit stores
/// (RoadNetworkGenerator.AtEnds). Offline, roads were stored with a ridge just short of RoadRidges.MinLength that ran on over
/// their own fill or pad: 8.4 m and 1.7 m of fill, 9.8 m and about 1.2 m of fill, 8.3 m and a staircase's start pad. The live
/// lengths and heights are laid here along a crest (the road at its ground, the ground falling away on both sides), with the
/// fill and the pad laid by their owners.
/// </summary>
[Collection("RoadStatics")]
public sealed class RoadRidgeFillTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private const float Top = 50f;
    /// <summary>The points' spacing, metres: fine enough that a stretch measures its live length to a tenth.</summary>
    private const float Step = 0.1f;
    private readonly float _radius, _cap;

    /// <summary>
    /// A crest: flat at <see cref="Top"/> within <see cref="Flat"/> m of its line, falling to <see cref="Top"/> less its depth
    /// at RoadRidges.Side from it. Along x (z = 0) for x up to <see cref="EndX"/>, a plateau at <see cref="Top"/> past it;
    /// or along z (x = 0) for z from <see cref="EndX"/> on (<see cref="AlongZ"/>), a plateau before it.
    /// </summary>
    private sealed class Crest : WorldGenerator
    {
        public const float Flat = 2.5f;
        public float Depth, EndX;
        public bool AlongZ;
        public override float GetHeight(float x, float z)
        {
            float off = AlongZ ? x : z, along = AlongZ ? -z : x;
            if (along > EndX) return Top;
            float d = Mathf.Abs(off);
            return d <= Flat ? Top : Top - Depth * Mathf.Min(1f, (d - Flat) / (RoadRidges.Side - Flat));
        }
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override int GetSeed() => 79;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    public RoadRidgeFillTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        PlacedLocations.Reset();
        RoadLocationProtection.Reset();
        StairStructures.Reset();
        // The suite runs on plain roads (PlainRoads: no junction fill); these read the mod's fill.
        _radius = RoadJunctionFillet.Radius;
        _cap = RoadNetworkGenerator.EarthworkCap;
        RoadJunctionFillet.Radius = RoadSwitchbacks.DefaultMinBendRadius;
    }

    public void Dispose()
    {
        RoadJunctionFillet.Radius = _radius;
        RoadNetworkGenerator.EarthworkCap = _cap;
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.Reset();
        PlacedLocations.Reset();
        StairStructures.Reset();
        _scope.Dispose();
    }

    private static RoadSpatialGrid.PlannedPath Piece(Vector2 from, Vector2 to)
    {
        var points = new List<Vector2>();
        int n = Mathf.RoundToInt(Vector2.Distance(from, to) / Step);
        for (int k = 0; k <= n; k++) points.Add(Vector2.Lerp(from, to, k / (float)n));
        return new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(_ => Top), points.ConvertAll(_ => default(RoadPointDebugInfo)),
            RoadNetworkGenerator.RoadWidth, Vector2.Distance(from, to), false);
    }

    private static readonly List<RoadCrossing> None = new();

    /// <summary>
    /// A road laid first across the crest (north-south through the origin), and a road along the crest from the east, its end
    /// <paramref name="shortOf"/> m short of the laid road's centreline, its last <paramref name="own"/> m on the crest
    /// <paramref name="depth"/> m over the ground on both sides: its plans, with the fill its owner lays.
    /// </summary>
    private List<RoadSpatialGrid.PlannedPath> AtAJunction(float own, float shortOf, float depth)
    {
        _scope.WithWorld(new Crest { Depth = depth, EndX = shortOf + own });
        var laid = Piece(new Vector2(0f, -40f), new Vector2(0f, 40f));
        RoadSpatialGrid.CommitRoad(new List<RoadSpatialGrid.PlannedPath> { laid }, ownChecked: true);
        RoadSpatialGrid.RecordLaid(new[] { laid });
        return new List<RoadSpatialGrid.PlannedPath> { Piece(new Vector2(60f, 0f), new Vector2(shortOf, 0f)) };
    }

    /// <summary>The road's pieces on their own, as the judge read them before: no stretch long enough to count.</summary>
    private static void OnItsOwnNothingCounts(List<RoadSpatialGrid.PlannedPath> plans) =>
        Assert.Empty(RoadRidges.Find(plans, RoadNetworkGenerator.GroundBeforeRoads(WorldGenerator.instance), null, null, null));

    /// <summary>The fill its owner lays at the road's end, carried onto the laid road's centreline: <paramref name="shortOf"/>
    /// m of it along the road's line.</summary>
    private static RoadRidges.AtEnd TheFill(List<RoadSpatialGrid.PlannedPath> plans, float shortOf)
    {
        RoadRidges.AtEnd fill = Assert.Single(RoadNetworkGenerator.LinesAtEnds(plans, None, Array.Empty<RoadStairSpan>()));
        Assert.True(Vector2.Distance(new Vector2(shortOf, 0f), fill.From) < 1e-3f, $"the fill goes on from {fill.From}");
        Assert.Equal(0f, fill.Line.Points[fill.Line.Points.Count - 1].x, 2);
        return fill;
    }

    /// <summary>RoadSeedA (-9501.4, -2422.6): a road's own ridge of 8.4 m, 2.49 m over the ground, ends 1.7 m short of the road it
    /// joins, its fill carrying it on along the crest: 10.1 m together, refused along a ridge.</summary>
    [Fact]
    public void ARidgeOf8Point4MetresRunningOnOverItsOwnJunctionFillIsRefused()
    {
        var plans = AtAJunction(8.4f, 1.7f, 2.49f);
        OnItsOwnNothingCounts(plans);
        TheFill(plans, 1.7f);
        Assert.NotNull(RoadNetworkGenerator.AlongARidge(plans, None, out _, out string reason));
        Assert.Equal(RoadNetworkGenerator.RidgeRoadRefusal, reason);
        Assert.Equal(10.1f, Assert.Single(RoadNetworkGenerator.Ridges(plans, None)).Length, 0.2f);
    }

    /// <summary>Run61p (-9125.9, 1063.5): a road's own ridge of 9.8 m, 3.2 m over the ground, and about 1.2 m of its junction
    /// fill: 11 m, refused.</summary>
    [Fact]
    public void ARidgeOf9Point8MetresRunningOnOverItsOwnJunctionFillIsRefused()
    {
        var plans = AtAJunction(9.8f, 1.2f, 3.2f);
        OnItsOwnNothingCounts(plans);
        TheFill(plans, 1.2f);
        Assert.NotNull(RoadNetworkGenerator.AlongARidge(plans, None, out _, out string reason));
        Assert.Equal(RoadNetworkGenerator.RidgeRoadRefusal, reason);
        Assert.Equal(11f, Assert.Single(RoadNetworkGenerator.Ridges(plans, None)).Length, 0.2f);
    }

    /// <summary>Just under RoadRidges.MinLength with its fill (8 m and 1.7 m, 9.7 m): a blip, built.</summary>
    [Fact]
    public void ARidgeJustUnderTenMetresWithItsFillIsBuilt()
    {
        var plans = AtAJunction(8f, 1.7f, 2.49f);
        TheFill(plans, 1.7f);
        Assert.Null(RoadNetworkGenerator.AlongARidge(plans, None, out _, out _));
        Assert.Empty(RoadNetworkGenerator.Ridges(plans, None));
        Assert.Null(RoadNetworkGenerator.RoadRefusal(new RoadNetworkGenerator.PlannedRoad { Plans = plans }, out _));
    }

    /// <summary>The fill only counts where it is laid: with its owner laying none (the fill off), the same 8.4 m is built.</summary>
    [Fact]
    public void NoFillLaidNothingCounted()
    {
        var plans = AtAJunction(8.4f, 1.7f, 2.49f);
        RoadJunctionFillet.Radius = 0f;
        Assert.Empty(RoadNetworkGenerator.LinesAtEnds(plans, None, Array.Empty<RoadStairSpan>()));
        Assert.Null(RoadNetworkGenerator.AlongARidge(plans, None, out _, out _));
    }

    /// <summary>The road along the crest climbing north to its end at the origin, its last <paramref name="own"/> m on the crest
    /// <paramref name="depth"/> m over the ground, and a staircase set off straight on from the ring round its end, climbing on
    /// over the crest: its plans and the staircase.</summary>
    private (List<RoadSpatialGrid.PlannedPath> plans, RoadStairSpan stairs) AtAStaircase(float own, float depth)
    {
        _scope.WithWorld(new Crest { Depth = depth, EndX = own, AlongZ = true });
        var ring = new RoadEndRing(new Vector2(0f, 0f), 0f, Top);
        Vector2 start = ring.ArcPoint(0f);
        var plan = new StairPlan { StartHeading = 0f, Reached = true, SunkPieces = 1, Ring = ring, RingStart = start, Foot = new Vector3(start.x, Top, start.y) };
        Vector2 step = StairLayout.Forward(0f) * StairLayout.PieceRun;
        for (int i = -1; i < 5; i++)
        {
            Vector2 at = start + step * (i + 0.5f);
            plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Stair, Prefab = "stone_stair", Position = new Vector3(at.x, Top + i, at.y), YawDegrees = 180f });
        }
        var span = new RoadStairSpan { FromPoint = new Vector2(0f, 0f), FromHeight = Top, ToPoint = start + step * 5f, ToHeight = Top + 5f, Kit = StairKit.Stone, Plan = plan };
        return (new List<RoadSpatialGrid.PlannedPath> { Piece(new Vector2(0f, -60f), new Vector2(0f, 0f)) }, span);
    }

    /// <summary>RoadSeedC (-127.1, 973.0): a road's own ridge of 8.3 m, 2.58 m over the ground, ends where its staircase starts,
    /// the staircase's start pad carrying the crest on to the first stair: refused along a ridge. Its pad's stretch is the
    /// pad's own points, from the road's end through the ring onto the stair.</summary>
    [Fact]
    public void ARidgeOf8Point3MetresRunningOnOverItsStaircasesStartPadIsRefused()
    {
        var (plans, span) = AtAStaircase(8.3f, 2.58f);
        var stairs = new List<RoadStairSpan> { span };
        OnItsOwnNothingCounts(plans);
        RoadRidges.AtEnd pad = Assert.Single(RoadNetworkGenerator.LinesAtEnds(plans, None, stairs));
        Assert.Equal(new Vector2(0f, 0f), pad.From);
        RoadSpatialGrid.PlannedPath laid = Assert.Single(RoadStairRuns.StartPads(plans, stairs, WorldGenerator.instance, tally: false));
        Assert.All(pad.Line.Points.Skip(1), p => Assert.Contains(p, laid.Points));
        Assert.True(pad.Line.Points[pad.Line.Points.Count - 1].y > ring(span).Radius, "the pad's stretch stops short of the stair");
        Assert.NotNull(RoadNetworkGenerator.BuiltRoadRefusal(plans, None, out _, stairs));
        RoadRidges.Stretch st = Assert.Single(RoadNetworkGenerator.Ridges(plans, None, stairs));
        Assert.True(st.Length >= RoadRidges.MinLength, $"{st.Length:F1} m");
        // Without the staircase there is no pad, and the 8.3 m is a blip.
        Assert.Null(RoadNetworkGenerator.BuiltRoadRefusal(plans, None, out _));

        static RoadEndRing ring(RoadStairSpan s) => s.Plan.Ring!.Value;
    }

    /// <summary>One assembly on every path (rule 13): the final check (RoadRefusal, its fills and pads read once and stored as
    /// read), a way in as it is planned and a road judged by its plans alone (ShapeRefusal, BuiltRoadRefusal), the re-route and
    /// hump verdicts (Ridges with the road's staircases), the searched-round verdict (SearchedRound) and the offline gate (the
    /// road recorded as laid with its fill's stretch, RoadRidges.WithEnds, judged on its points as stored with RoadRidges.Find
    /// and RoadSpatialGrid.JoinedAt against the roads laid before it) all read the RoadSeedA fill as part of the ridge.</summary>
    [Fact]
    public void EveryPathReadsTheFillAsPartOfTheRoad()
    {
        // As generation judges a road: with the earthwork cap, which the searched-round verdict asks.
        RoadNetworkGenerator.EarthworkCap = RoadNetworkGenerator.DefaultEarthworkCap;
        var plans = AtAJunction(8.4f, 1.7f, 2.49f);
        var atEnds = RoadNetworkGenerator.LinesAtEnds(plans, None, Array.Empty<RoadStairSpan>());

        RoadNetworkGenerator.Refusal? final = RoadNetworkGenerator.RoadRefusal(new RoadNetworkGenerator.PlannedRoad { Plans = plans }, out _);
        RoadNetworkGenerator.Refusal? shaped = RoadNetworkGenerator.ShapeRefusal(plans, Array.Empty<RoadSpatialGrid.PlannedPath>(), None, null, atEnds);
        string? built = RoadNetworkGenerator.BuiltRoadRefusal(plans, None, out Vector2 builtAt);
        RoadRidges.Stretch judged = Assert.Single(RoadNetworkGenerator.Ridges(plans, None, Array.Empty<RoadStairSpan>()));
        Assert.True(RoadNetworkGenerator.EarthworkCapped, "no earthwork cap");
        Assert.True(RoadNetworkGenerator.RidgeRetries > 0, "no ridge retries");
        Assert.NotNull(RoadNetworkGenerator.SearchedRound(plans, out Vector2 searchedAt, None, atEnds));

        Assert.NotNull(final);
        Assert.NotNull(shaped);
        Assert.Equal(RoadNetworkGenerator.RidgeRoadRefusal, final!.Reason);
        Assert.Equal(final.Said, shaped!.Said);
        Assert.Equal(final.Said, built);
        Assert.True(Vector2.Distance(final.At, builtAt) < 1e-3f, $"{final.At} {builtAt}");
        Assert.True(Vector2.Distance(final.At, searchedAt) < 1e-3f, $"{final.At} {searchedAt}");

        // The offline gate: the road stored with its fill as generation stores it, recorded with the fill's stretch along its
        // line, then judged on the record at its stored heights.
        var fills = RoadJunctionFillet.For(plans, WorldGenerator.instance);
        RoadSpatialGrid.CommitRoad(plans, ownChecked: true);
        foreach (var fill in fills) RoadSpatialGrid.Commit(fill);
        int me = RoadSpatialGrid.RecordLaid(RoadRidges.WithEnds(plans, atEnds));
        var (_, pieces) = RoadSpatialGrid.LaidRoadAt(me);
        var stored = new List<RoadSpatialGrid.PlannedPath>();
        foreach (Vector2[] piece in pieces)
        {
            RoadSpatialGrid.RoadPoint[] at = RoadSpatialGrid.StoredAt(piece)!;
            Assert.NotNull(at);
            stored.Add(new RoadSpatialGrid.PlannedPath(piece.ToList(), at.Select(p => p.h).ToList(), new List<RoadPointDebugInfo>(), RoadNetworkGenerator.RoadWidth, 0f, false));
        }
        Assert.Equal(2, stored.Count);
        RoadRidges.Stretch gate = Assert.Single(RoadRidges.Find(stored, RoadNetworkGenerator.GroundBeforeRoads(WorldGenerator.instance), null, null,
            RoadSpatialGrid.JoinedAt(stored, before: me)));
        Assert.Equal(judged.Length, gate.Length, 3);
        Assert.Equal(judged.Points[0], gate.Points[0]);
    }

    /// <summary>RoadRidges.WithEnds: a line goes on from the end of a run only (after the piece that ends it, reversed ahead of
    /// the piece that starts it); at a joint where the road goes on in its next piece, or at no end of the road, it is not read.</summary>
    [Fact]
    public void ALineAtAnEndIsReadOnlyAtTheEndOfARun()
    {
        var a = Piece(new Vector2(0f, 0f), new Vector2(10f, 0f));
        var b = Piece(new Vector2(10f, 0f), new Vector2(20f, 0f));
        RoadRidges.AtEnd Line(Vector2 from, Vector2 to) => new(from, Piece(from, to));
        var ends = new[] { Line(new Vector2(20f, 0f), new Vector2(22f, 0f)), Line(new Vector2(0f, 0f), new Vector2(-2f, 0f)),
            Line(new Vector2(10f, 0f), new Vector2(10f, 2f)), Line(new Vector2(5f, 0f), new Vector2(5f, 2f)) };
        var line = RoadRidges.WithEnds(new[] { a, b }, ends);
        Assert.Equal(4, line.Count);
        Assert.Equal(new Vector2(-2f, 0f), line[0].Points[0]);
        Assert.Equal(new Vector2(0f, 0f), line[0].Points[line[0].Points.Count - 1]);
        Assert.Same(a, line[1]);
        Assert.Same(b, line[2]);
        Assert.Equal(new Vector2(22f, 0f), line[3].Points[line[3].Points.Count - 1]);
    }
}
