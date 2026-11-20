using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// One road where two ride along each other (RoadRide). In zone (81,43) a road came along another
/// over its ford's footbridge and was stored a second time, 0.3-1.3 m beside the deck, levelled at 30.5
/// through the shallows under it; and its points along the other road's approach were stored again on top of it. There is a single owner for the double road
/// bit, and bridges/fords/stairs along the way do not trip it up.
/// </summary>
[Collection("RoadStatics")]
public class OneRoadTests
{
    private sealed class Flat : WorldGenerator
    {
        public override float GetHeight(float x, float z) => Mathf.Abs(x) > 300f || Mathf.Abs(z) > 300f ? 0f : 31f;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    /// <summary>A knee-deep gully across the x axis (|x| &lt; HalfWidth), a river core under it.</summary>
    private sealed class Gully : WorldGenerator
    {
        public float Bed = 29.35f, HalfWidth = 5f;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 150f || Mathf.Abs(wy) > 150f) return 20f;
            return Mathf.Abs(wx) < HalfWidth ? Bed : 33f;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            GetHeight(wx, wy) < RoadConstants.SeaLevel - 2f ? Heightmap.Biome.Ocean : Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = Mathf.Clamp01(1f - Mathf.Abs(wx) / (HalfWidth * 2f));
            width = weight > 0f ? HalfWidth * 4f : 0f;
        }
    }

    private static RoadSpatialGrid.PlannedPath Piece(IEnumerable<(float x, float z, float h)> at)
    {
        var list = at.ToList();
        var points = list.Select(p => new Vector2(p.x, p.z)).ToList();
        float length = 0f;
        for (int i = 1; i < points.Count; i++) length += Vector2.Distance(points[i - 1], points[i]);
        return new RoadSpatialGrid.PlannedPath(points, list.Select(p => p.h).ToList(), points.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f, length, false);
    }

    private static IEnumerable<float> Metres(float from, float to) { for (float x = from; x <= to + 1e-3f; x += 1f) yield return x; }

    private static List<RoadCrossing> Registry => (List<RoadCrossing>)RoadNetworkGenerator.GetRoadCrossings();

    /// <summary>Road A as found: along the x axis at 31, its ford's footbridge from x -5 to 5 (no road there,
    /// the structure replaces it), the crossing registered.</summary>
    private static RoadCrossing LayRoadAWithItsSpan()
    {
        RoadSpatialGrid.Commit(Piece(Metres(-60f, -5f).Select(x => (x, 0f, 31f))));
        RoadSpatialGrid.Commit(Piece(Metres(5f, 60f).Select(x => (x, 0f, 31f))));
        var span = RoadCrossing.Between(new Vector2(-5f, 0f), new Vector2(5f, 0f), 29.35f, new Vector2(0f, 0f), 0f, CrossingKind.Ford, FordStyle.Span);
        span.Shallow = true;
        Registry.Add(span);
        return span;
    }

    /// <summary>Road B as found: stored again on top of A's approach (within 0.3 m), from A's near bank on its
    /// own line 1.2 m beside the deck at the dry-land floor (30.5) through the gap, and on top of A again past it.</summary>
    private static RoadSpatialGrid.PlannedPath RoadBOverTheSpan() =>
        Piece(Metres(-40f, 40f).Select(x => (x, Mathf.Abs(x) < 5f ? 1.2f : 0.2f, Mathf.Abs(x) < 5f ? RoadConstants.DryRoadFloor : 31f)));

    private static void With(System.Action body)
    {
        using var scope = new ValheimWorldScope().WithWorld(new Flat());
        RoadNetworkGenerator.Reset();
        RoadRide.Reset();
        try { body(); }
        finally { Registry.Clear(); RoadNetworkGenerator.Reset(); }
    }

    /// <summary>Why nothing fired: no rule refused road B. Not a crossing (it never passes from one side of A to the other),
    /// not a braid: on A it is on the trunk two roads may share, and beside the deck it ran for 10 m, under the 12 m cap
    /// every parallel run has, where A had no road point to be beside at all (the snap, the meeting rules and the commit
    /// asked the stored road points only; a structure's gap was no road). And the commit stored whatever it was given.
    /// Now the gap is A's road (RoadRide's lines): B rides A the whole way and stores nothing; the commit path refuses B
    /// stored whole.</summary>
    [Fact]
    public void ARoadAlongAnotherOverItsSpanIsThatRoad()
    {
        With(() =>
        {
            LayRoadAWithItsSpan();
            var b = new List<RoadSpatialGrid.PlannedPath> { RoadBOverTheSpan() };
            Assert.Null(RoadNetworkGenerator.MeetingRefusal(b, out _));
            // Under the deck, B was 4-5 m from the nearest stored point of A (its banks): no road; now the deck's line is.
            Assert.False(RoadSpatialGrid.TryGetRoadWithin(new Vector2(0f, 1.2f), 4.5f, out _));
            var under = new Vector2(0f, 1.2f);
            Assert.True(RoadRide.Around(new[] { under }, 2f).Nearest(under, 2f, out RoadRide.Hit onDeck));
            Assert.Equal(0f, onDeck.At.y, 3);

            Assert.Empty(RoadRide.Own(b, out float ridden));
            Assert.InRange(ridden, 75f, 81f);
            var thrown = Assert.Throws<System.InvalidOperationException>(() => RoadSpatialGrid.CommitRoad(b));
            Assert.Contains("double road", thrown.Message);
        });
    }

    /// <summary>The snap onto the network moved a waypoint near A's footbridge to A's nearest stored point, a bank: the road
    /// between its waypoints then ran on its own line beside the deck. It snaps onto the deck's line now.</summary>
    [Fact]
    public void AWaypointBesideASpanSnapsOntoTheDeckNotABank()
    {
        With(() =>
        {
            LayRoadAWithItsSpan();
            using var overrides = StaticOverride.Keep(() => RoadNetworkGenerator.CorridorSnap).AndKeep(() => RoadNetworkGenerator.RoadSnap);
            RoadNetworkGenerator.CorridorSnap = 0f;
            RoadNetworkGenerator.RoadSnap = 6f;
            var path = new List<Vector2> { new(-40f, 10f), new(-20f, 2f), new(1f, 2f), new(20f, 2f), new(40f, 10f) };
            var snapped = RoadNetworkGenerator.SnapToNetwork(path)!;
            Assert.Contains(snapped, p => Vector2.Distance(p, new Vector2(1f, 0f)) < 0.01f);
        });
    }

    /// <summary>The release behaviour of the commit path's guard: a ridden stretch that reaches it is left out, counted and
    /// logged, never stored.</summary>
    [Fact]
    public void TheCommitPathStoresNoRiddenStretch()
    {
        With(() =>
        {
            LayRoadAWithItsSpan();
            using var overrides = StaticOverride.Keep(() => RoadRide.ThrowWhenCaught);
            RoadRide.ThrowWhenCaught = false;
            // B as above, then off on its own 20 m north of A.
            var b = Piece(Metres(-40f, 40f).Select(x => (x, Mathf.Abs(x) < 5f ? 1.2f : 0.2f, 31f))
                .Concat(Metres(1f, 30f).Select(z => (40f + z * 0.3f, z, 31f))));
            int before = RoadSpatialGrid.TotalRoadPoints;
            RoadSpatialGrid.CommitRoad(new List<RoadSpatialGrid.PlannedPath> { b });
            Assert.Equal(1, RoadRide.CaughtAtCommit);
            int stored = RoadSpatialGrid.TotalRoadPoints - before;
            Assert.InRange(stored, 25, 34);
            Assert.Empty(RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(0f, 0f, 0f), 3f));
        });
    }

    /// <summary>A road's own pieces never run on under its own deck (a neighbouring ford's piece in a pool chain did, beside
    /// a span turned onto a buildable heading: RoadSeedB offline, (1946, 2150), 20 road points under the deck), nor along
    /// one of its own pieces; but a road carrying on from where another ends rides nothing, and its approach to its own
    /// deck's bank is its own.</summary>
    [Fact]
    public void ARoadsOwnDeckAndPiecesAreItsRoadToo()
    {
        With(() =>
        {
            // Some network far off: the rule reads the network as stored before the road.
            RoadSpatialGrid.Commit(Piece(Metres(100f, 120f).Select(x => (x, 100f, 31f))));
            var deck = RoadCrossing.Between(new Vector2(-5f, 0f), new Vector2(5f, 0f), 29.35f, new Vector2(0f, 0f), 0f, CrossingKind.Ford, FordStyle.Span);
            var toBank = Piece(Metres(-40f, -5f).Select(x => (x, 0f, 31f)));
            var underIt = Piece(Metres(-5f, 20f).Select(x => (x, 1.2f, 30.5f)));
            var fromBank = Piece(Metres(5f, 40f).Select(x => (x, 0f, 31f)));
            var pieces = new List<RoadSpatialGrid.PlannedPath> { toBank, underIt, fromBank };
            var own = RoadRide.Own(pieces, out _, RoadRide.DeckLines(pieces, new[] { deck }));
            Assert.Contains(own, p => p.Points.Count == toBank.Points.Count);
            // Nothing stored under the deck; past it one road, never two side by side (the later piece yields where they
            // overlap), and the road carries on to its end.
            Assert.DoesNotContain(own, p => p.Points.Exists(q => q.x > -4f && q.x < 4f));
            foreach (float x in Metres(5f, 40f))
            {
                int near = own.Count(p => p.Points.Exists(q => Vector2.Distance(q, new Vector2(x, 0f)) < 1.6f));
                Assert.True(near == 1 || (near == 2 && own.Any(p => p.Points.Exists(q => Mathf.Abs(q.x - x) < 1.1f && p.Points[0] == q || p.Points[p.Points.Count - 1] == q && Mathf.Abs(q.x - x) < 1.1f))),
                    $"x {x}: {near} pieces");
            }
            Assert.Contains(own, p => p.Points[p.Points.Count - 1] == new Vector2(40f, 0f));

            // Carrying straight on from the end of a stored road: nothing ridden.
            RoadSpatialGrid.Commit(Piece(Metres(-80f, -40f).Select(x => (x, 30f, 31f))));
            var onwards = Piece(Metres(-40f, 0f).Select(x => (x, 30f, 31f)));
            Assert.Same(onwards, Assert.Single(RoadRide.Own(new List<RoadSpatialGrid.PlannedPath> { onwards }, out float none)));
            Assert.Equal(0f, none);
        });
    }

    /// <summary>The whole road, generated: a second road beside the first over the first's footbridge crosses on it and
    /// stores nothing of its own along the first, nor a crossing of its own.</summary>
    [Fact]
    public void ASecondRoadBesideTheFirstStoresOnlyItsOwnRoad()
    {
        var world = new Gully();
        using var scope = new ValheimWorldScope().WithWorld(world);
        RoadNetworkGenerator.Reset();
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, new RoadPathfinder(world) { Fords = true, Bridges = true, MaxGrade = 0f });
        RoadCrossingDetector.SetFordStyleWeights(0f, 0f, 1f);
        var pieces = new List<(string? label, RoadSpatialGrid.PlannedPath plan)>();
        RoadNetworkGenerator.PieceCommitted = (p, l) => pieces.Add((l, p));
        try
        {
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0f), 0f, new Vector2(80f, 0f), 0f, 4f, "A"));
            float a = pieces.Sum(p => p.plan.TotalLength);
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, -1.2f), 0f, new Vector2(80f, -1.2f), 0f, 4f, "B"));
            var span = Assert.Single(RoadNetworkGenerator.GetRoadCrossings());
            Assert.Equal(FordStyle.Span, span.Style);
            float b = pieces.Where(p => p.label == "B").Sum(p => p.plan.TotalLength);
            Assert.True(b < 0.1f * a, $"B stored {b:F0} m beside A's {a:F0} m");
        }
        finally
        {
            RoadNetworkGenerator.PieceCommitted = null;
            typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);
            RoadNetworkGenerator.Reset();
            RoadCrossingDetector.SetFordStyleWeights(1f, 1f, 1f);
        }
    }

    /// <summary>A road coming onto another at a shallow angle (a Y) keeps its own road up to the other road's surface, not
    /// only up to a road's width off it (a gap and a step at the junction otherwise).</summary>
    [Fact]
    public void AYKeepsItsRoadOntoTheTrunk()
    {
        With(() =>
        {
            RoadSpatialGrid.Commit(Piece(Metres(-80f, 80f).Select(x => (x, 0f, 31f))));
            // In from 20 m off at about 11 degrees, onto the trunk at x 20 and along it.
            var y = Piece(Metres(-80f, 60f).Select(x => (x, x < 20f ? Mathf.Min(20f, (20f - x) * 0.2f) : 0f, 31f)));
            var own = RoadRide.Own(new List<RoadSpatialGrid.PlannedPath> { y }, out _);
            var kept = Assert.Single(own);
            Assert.True(Mathf.Abs(kept.Points[kept.Points.Count - 1].y) <= 1.05f, $"ends {kept.Points[kept.Points.Count - 1]}");
            Assert.True(kept.Points[kept.Points.Count - 1].x < 20f);
        });
    }

    /// <summary>A road coming onto another at a slant that closes in more and more slowly (a slope's sway: RoadSeedE offline,
    /// (-3261.7, -2496.3), heading the trunk's way only 1.8 m off it, then 1.0 m off it 6 m on) keeps its road on into the trunk as far as it
    /// still closes in, and the commit's guard takes that mouth for what it is, not for a second road along the first.
    /// Before, the two read the mouth by different rules: the road kept it as far as it came closer (to 1.04 m off), the
    /// guard only where it had come in from a metre further off than it ended, and threw on the road the generator built.</summary>
    [Fact]
    public void AMouthClosingInSlowlyIsTheRoadsOwnToTheGuardToo()
    {
        With(() =>
        {
            RoadSpatialGrid.Commit(Piece(Metres(-80f, 80f).Select(x => (x, 0f, 31f))));
            // Steeply in from 20 m off, heading the trunk's way (within 25 degrees) only 1.5 m off it, then closing in more
            // and more slowly to a metre off (the RoadSeedE profile), then along it there.
            float[] closing = { 5.6f, 4.5f, 3.4f, 2.4f, 1.78f, 1.52f, 1.34f, 1.21f, 1.11f, 1.04f, 1.0f, 0.98f, 0.97f };
            float Off(float x) => x < 0f ? closing[0] - x * 2f : x < closing.Length - 1 ? closing[(int)x] : closing[closing.Length - 1];
            var y = Piece(Metres(-8f, 40f).Select(x => (x, Off(x), 31f)));
            var own = RoadRide.Own(new List<RoadSpatialGrid.PlannedPath> { y }, out float ridden);
            Assert.InRange(ridden, 20f, 40f);
            Assert.Null(RoadRide.Doubled(own));
            RoadSpatialGrid.CommitRoad(own);
            Assert.Equal(0, RoadRide.CaughtAtCommit);
        });
    }
}
