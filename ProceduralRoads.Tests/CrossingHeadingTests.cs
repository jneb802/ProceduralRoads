using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The heading constraint at the PLANNING level: what happens to a crossing
/// whose site has no line the vanilla hammer could build on.
///
/// The rule these tests hold to is that an unturnable heading never deletes a
/// crossing. The turn is decided in the detector, after the pathfinder has
/// accepted and priced the jump, and nothing re-searches afterwards -- so a
/// crossing dropped here does not move the road, it leaves the accepted path
/// painted as ordinary road over open water. A ford drops Span instead; a
/// bridge keeps the bearing the router priced and is reported as unturnable.
/// </summary>
public class CrossingHeadingTests
{
    /// <summary>A gully running north-south, land either side, crossed at
    /// whatever bearing the caller's road takes.</summary>
    private sealed class GullyWorld : WorldGenerator
    {
        public float Bed = 26f;
        public float HalfWidth = 12f;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 400f || Mathf.Abs(wy) > 400f) return 20f;
            return Mathf.Abs(wx) < HalfWidth ? Bed : 33f;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy) =>
            GetHeight(wx, wy) < RoadConstants.SeaLevel - 2f ? Heightmap.Biome.Ocean : Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = Mathf.Clamp01(1f - Mathf.Abs(wx) / (HalfWidth * 2f));
            width = weight > 0f ? HalfWidth * 4f : 0f;
        }
    }

    /// <summary>Two land patches too narrow for a bank to be re-found on any
    /// turned line: the reviewer's shape, which is what makes it unturnable.
    /// Offsetting the far patch to make the road's own bearing off-grid does
    /// not work -- the crossing scan only walks the eight unit grid directions
    /// (see ACrossingScanOnlyWalksTheEightGridDirections), so the router cannot
    /// reach it at all.</summary>
    private sealed class NarrowBanksWorld : WorldGenerator
    {
        private static bool Land(float x, float z) =>
            Mathf.Abs(z) < 0.6f && (Mathf.Abs(x) < 0.6f || Mathf.Abs(x - 32f) < 0.6f);
        public override float GetHeight(float x, float z) => Land(x, z) ? 33f : 26f;
        public override Heightmap.Biome GetBiome(float x, float z) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        {
            weight = Land(x, z) ? 0f : 1f;
            width = 32f;
        }
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, pathfinder);

    private static void TearDownGeneration()
    {
        SetPathfinder(null);
        RoadNetworkGenerator.Reset();
        RoadCrossingDetector.SetFordStyleWeights(1f, 1f);
        WorldGenerator.instance = null;
    }

    // ---- the defect: an accepted route losing its bridge ----

    [Fact]
    public void AnUnturnableSiteKeepsTheBridgeTheRouterPriced()
    {
        // The pathfinder accepts this jump and prices it; the detector cannot
        // find a turned line, because neither bank is wide enough to re-find.
        // Dropping the crossing here would leave the accepted path painted as
        // ordinary road across 32 m of river.
        var world = new NarrowBanksWorld();
        var router = new RoadPathfinder(world) { Bridges = true, Fords = false };
        var path = router.FindPath(new Vector2(0f, 0f), new Vector2(32f, 0f));
        Assert.NotNull(path);

        var crossing = Assert.Single(RoadCrossingDetector.Detect(path!, world, bridges: true, fords: false));
        Assert.Equal(CrossingKind.Bridge, crossing.Kind);

        // It kept the line the router priced: the banks are the ones on the
        // accepted jump, not banks re-found somewhere else.
        Assert.True(Vector2.Distance(crossing.FromBank, new Vector2(0f, 0f)) < 2f, $"near bank at {crossing.FromBank}");
        Assert.True(Vector2.Distance(crossing.ToBank, new Vector2(32f, 0f)) < 2f, $"far bank at {crossing.ToBank}");
        // This fixture's retained bearing happens to be 90 deg, which IS on the
        // grid, so it cannot show the unturnable REPORT -- only that the
        // crossing survived, which is the defect being held down here.
    }

    [Fact]
    public void AnUnturnableSpanFordWadesOrRaisesRatherThanVanishing()
    {
        // A span is pieces, so it needs a placeable heading; wading and raising
        // are terrain and need nothing. Given a site that cannot be turned, the
        // ford must change STYLE, not disappear.
        var world = new NarrowBanksWorld();
        RoadCrossingDetector.SetFordStyleWeights(0f, 0f, 1f);   // Span, if it can
        try
        {
            var router = new RoadPathfinder(world) { Bridges = true, Fords = true };
            var path = router.FindPath(new Vector2(0f, 0f), new Vector2(32f, 0f));
            Assert.NotNull(path);

            var crossing = Assert.Single(RoadCrossingDetector.Detect(path!, world, bridges: true, fords: true));
            Assert.NotEqual(FordStyle.Span, crossing.Style);
        }
        finally { RoadCrossingDetector.SetFordStyleWeights(1f, 1f, 1f); }
    }

    // ---- the turn itself, and what the road does about it ----

    [Fact]
    public void AGenuinelyTurnedCrossingStillHasRoadAtBothOfItsMovedBanks()
    {
        var world = new GullyWorld();
        WorldGenerator.instance = world;
        RoadNetworkGenerator.Reset();
        SetPathfinder(new RoadPathfinder(world) { Bridges = true, Fords = false });
        try
        {
            // A road at a bearing deliberately between two of the sixteen.
            Assert.True(RoadNetworkGenerator.GenerateRoad(
                new Vector2(-60f, -30f), 0f, new Vector2(60f, 30f), 0f, 4f, "turned"));
            var crossing = Assert.Single(RoadNetworkGenerator.GetRoadCrossings());

            float heading = BridgeLayout.YawDegrees(crossing.Direction);
            Assert.True(BridgeLayout.HeadingIsPlaceable(heading),
                $"the crossing stands at {heading:F2}, which no hammer can turn to");

            // The deck is NOT on the road's own end-to-end bearing: the road
            // runs at 63.43 deg and the crossing does not. That is a bridgehead
            // the painter has to bend out to -- but the grid scan put it there,
            // not the snap: this crossing is validly left at 90 degrees and
            // must be. The turn WITH its approaches is covered by
            // ATurnedCrossingsApproachesArePaintedToTheBanksItMovedTo.
            float roadBearing = BridgeLayout.YawDegrees(new Vector2(120f, 60f).normalized);
            Assert.False(BridgeLayout.HeadingIsPlaceable(roadBearing), "the fixture road is supposed to be off-grid");
            Assert.True(Mathf.Abs(heading - roadBearing) > 0.5f,
                $"road {roadBearing:F2}, deck {heading:F2}");

            // ... and the road actually RUNS to both bridgeheads. A road point
            // somewhere within 6 m of a bank says nothing about whether a cart
            // can get there, so walk the painted road itself: flood along it
            // from each end of the ROUTE and require the walk to arrive at the
            // bridgehead without ever crossing the water.
            Assert.True(PaintedRoadReaches(new Vector2(-60f, -30f), crossing.FromBank, crossing),
                $"the painted road does not run from the start to the near bridgehead at ({crossing.FromBank.x:F2},{crossing.FromBank.y:F2})");
            Assert.True(PaintedRoadReaches(new Vector2(60f, 30f), crossing.ToBank, crossing),
                $"the painted road does not run from the end to the far bridgehead at ({crossing.ToBank.x:F2},{crossing.ToBank.y:F2})");
            // A walk that can only succeed proves nothing: it must also be able
            // to fail. There is no road out to (300, 300).
            Assert.False(PaintedRoadReaches(new Vector2(-60f, -30f), new Vector2(300f, 300f), crossing),
                "the connectivity walk reached open country, so it cannot tell connected from not");
        }
        finally { TearDownGeneration(); }
    }

    /// <summary>
    /// Why the constraint costs so little: a crossing is found by walking the
    /// EIGHT unit grid directions out of a cell (TryGetRiverCrossing refuses a
    /// knight move outright), so the jump it accepts runs cell-centre to
    /// cell-centre along one of them and its bearing is a multiple of 45
    /// degrees -- already one of the sixteen the hammer can turn to. Nothing
    /// has to be turned unless a later step moves a bank OFF that line (the
    /// bank-top climb walks the road, which can bend).
    ///
    /// This is a fact about the router, not a guarantee about every crossing,
    /// which is why the snap still exists and still has to be correct.
    /// </summary>
    [Theory]
    [InlineData(-30f, 30f)]
    [InlineData(-8f, 40f)]
    [InlineData(0f, 0f)]
    [InlineData(24f, -24f)]
    public void ACrossingScanOnlyWalksTheEightGridDirections(float startZ, float endZ)
    {
        var world = new GullyWorld();
        var router = new RoadPathfinder(world) { Bridges = true, Fords = false };
        var path = router.FindPath(new Vector2(-60f, startZ), new Vector2(60f, endZ));
        Assert.NotNull(path);
        var crossings = RoadCrossingDetector.Detect(path!, world, bridges: true, fords: false);
        Assert.NotEmpty(crossings);

        foreach (var c in crossings)
        {
            Vector2 jump = path![c.ToIndex] - path[c.FromIndex];
            float bearing = BridgeLayout.YawDegrees(jump.normalized);
            float off = Mathf.Abs(bearing % 45f);
            Assert.True(Mathf.Min(off, 45f - off) < 0.01f,
                $"the accepted jump runs at {bearing:F3} deg, which is not a multiple of 45");
        }
    }

    /// <summary>
    /// Whether the PAINTED road connects one point to another on land: a
    /// breadth-first walk over 2 m steps, each of which must have road under
    /// it, and none of which may be over the crossing's own water. Proximity
    /// to a road point is not connection -- a road that stops 5 m short of a
    /// bridgehead has a point within 6 m of it and no way to reach it.
    ///
    /// The claim stops at PAINTED CONNECTIVITY. It queries road points only:
    /// not ground height, not slope, not water anywhere but the rectangle it
    /// excludes around this crossing. Whether a cart can be pulled along what
    /// it finds is a question for terrain-aware assertions and, in the end,
    /// for gameplay.
    /// </summary>
    private static bool PaintedRoadReaches(Vector2 start, Vector2 goal, RoadCrossing crossing)
    {
        const float Step = 2f;
        bool OnRoad(Vector2 p) =>
            RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(p.x, 0f, p.y), Step).Count > 0;
        bool OverTheWater(Vector2 p)
        {
            float along = crossing.Along(p);
            return along > 1f && along < crossing.Width - 1f
                   && Mathf.Abs(Vector2.Dot(p - crossing.FromBank, new Vector2(-crossing.Direction.y, crossing.Direction.x))) < 6f;
        }
        (int, int) Key(Vector2 p) => (Mathf.RoundToInt(p.x / Step), Mathf.RoundToInt(p.y / Step));

        var seen = new HashSet<(int, int)>();
        var queue = new Queue<Vector2>();
        queue.Enqueue(start);
        seen.Add(Key(start));
        while (queue.Count > 0)
        {
            Vector2 at = queue.Dequeue();
            if (Vector2.Distance(at, goal) <= Step * 1.5f)
                return true;
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                Vector2 next = at + new Vector2(dx, dy) * Step;
                if (seen.Count > 20000) return false;
                if (!seen.Add(Key(next))) continue;
                if (OverTheWater(next) || !OnRoad(next)) continue;
                queue.Enqueue(next);
            }
        }
        return false;
    }

    /// <summary>
    /// A crossing whose accepted line is ALREADY on the grid is left exactly
    /// as the router priced it. Re-finding its banks to satisfy a constraint
    /// that is already satisfied is how a 32 m jump between two 33 m banks
    /// became a 52 m one onto a 50 m cliff (ReviewerFinalCrossingTests).
    /// </summary>
    [Fact]
    public void AnAlreadyPlaceableCrossingKeepsTheExactBanksTheRouterPriced()
    {
        var world = new GullyWorld();
        var router = new RoadPathfinder(world) { Bridges = true, Fords = false };
        var path = router.FindPath(new Vector2(-60f, 0f), new Vector2(60f, 0f));   // due east: 90 deg
        Assert.NotNull(path);

        var crossing = Assert.Single(RoadCrossingDetector.Detect(path!, world, bridges: true, fords: false));
        Assert.True(BridgeLayout.HeadingIsPlaceable(BridgeLayout.YawDegrees(crossing.Direction)));

        // The banks are the water's edge on the accepted line: x = +-HalfWidth,
        // both on the SAME row (whichever row the router chose -- A* may route
        // a cell off the straight line, and that is its business). Nothing was
        // re-found on another row, and the span is the channel's own width.
        Assert.InRange(Mathf.Abs(crossing.FromBank.x), world.HalfWidth - 1.0f, world.HalfWidth + 1.0f);
        Assert.InRange(Mathf.Abs(crossing.ToBank.x), world.HalfWidth - 1.0f, world.HalfWidth + 1.0f);
        Assert.InRange(crossing.ToBank.y - crossing.FromBank.y, -0.01f, 0.01f);
        Assert.InRange(crossing.Width, world.HalfWidth * 2f - 2f, world.HalfWidth * 2f + 2f);
    }

    /// <summary>
    /// The bank-top climb walks the ROAD, so a bent approach can hand back an
    /// unplaceable heading for a crossing whose own water line was fine. When
    /// that happens the accepted water-edge line is taken, UNCHANGED -- these
    /// are the banks routing priced, and nothing is re-found. (The reviewer's
    /// ReviewOptionalBankTopTests asserts the heading; this asserts that the
    /// banks are the accepted ones, which is why the heading is right.)
    /// </summary>
    [Fact]
    public void ABentApproachFallsBackToTheAcceptedWaterEdgeBanks()
    {
        var world = new BentHighBanks();
        var path = new List<Vector2>
        {
            new(-16f, -16f), new(-16f, -8f), new(-16f, 0f),
            new(16f, 0f), new(16f, 8f), new(16f, 16f),
        };
        var crossing = Assert.Single(RoadCrossingDetector.Detect(path, world, bridges: true, fords: false));

        // The water's edge on the accepted jump: |x| = 12, on the road's row.
        Assert.InRange(crossing.FromBank.x, -12.01f, -11.99f);
        Assert.InRange(crossing.ToBank.x, 11.99f, 12.01f);
        Assert.InRange(crossing.FromBank.y, -0.01f, 0.01f);
        Assert.InRange(crossing.ToBank.y, -0.01f, 0.01f);
        Assert.True(BridgeLayout.HeadingIsPlaceable(BridgeLayout.YawDegrees(crossing.Direction)));
        // Not the bank tops the climb reached for, which are the 63.435 degree pair.
        Assert.True(world.GetHeight(crossing.FromBank.x, crossing.FromBank.y) < 39f,
            "the unplaceable bank-top layout was kept after all");
    }

    /// <summary>
    /// A candidate is banks AND the path interval it consumes. The fallback
    /// restored the banks but kept the rejected tops' interval, so the painter
    /// believed the land in front of the crossing was already spoken for and
    /// painted no approach. With two crossings on one road the same slip also
    /// decides where the SECOND one starts painting, so both are checked here.
    /// </summary>
    [Fact]
    public void FallingBackReturnsThePathIntervalTooWithAFollowingCrossingBehindIt()
    {
        var world = new TwoBentChannels();
        WorldGenerator.instance = world;
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        try
        {
            // The first channel is the reviewer's shape -- both its banks bend
            // up to high ground, so it climbs, comes back unplaceable and must
            // fall back. The second is approached along the high ground with no
            // rise to climb, so it never enters that path at all: it is the
            // crossing BEHIND the fallback, and it must still get its land.
            var path = new List<Vector2>
            {
                new(-16f, -8f), new(-16f, 0f), new(16f, 0f),
                new(16f, 8f), new(36f, 8f), new(68f, 8f), new(72f, 8f),
            };
            var crossings = RoadCrossingDetector.Detect(path, world, bridges: true, fords: false);
            Assert.Equal(2, crossings.Count);
            foreach (var c in crossings)
                Assert.True(BridgeLayout.HeadingIsPlaceable(BridgeLayout.YawDegrees(c.Direction)),
                    $"crossing at ({c.Center.x:F1},{c.Center.y:F1}) stands at {BridgeLayout.YawDegrees(c.Direction):F3}");

            // Cap held out: this route is drawn to place two bridges, not to
            // be a road a capped search would have chosen.
            using (GradeCap.Off())
                typeof(RoadNetworkGenerator).GetMethod("AddRoadPathWithCrossings",
                    BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
                    new object?[] { path, crossings, 4f, null, null });

            // The land in front of the first crossing, between the two, and
            // after the second: all painted.
            foreach ((float x, float z, string where) in new[]
                     {
                         (-16f, -4f, "in front of the first crossing"),
                         (26f, 8f, "between the two crossings"),
                         (70f, 8f, "after the second crossing"),
                     })
                Assert.True(RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(x, 0f, z), 2.5f).Count > 0,
                    $"no painted road {where}, at ({x},{z}); intervals " +
                    string.Join(" ", crossings.Select(c => $"{c.FromIndex}..{c.ToIndex}")));
        }
        finally { TearDownGeneration(); }
    }

    /// <summary>The reviewer's BentHighBanks channel at the origin, plus a
    /// second channel out along the high ground. The climb needs BOTH banks to
    /// rise, which only the first channel's do.</summary>
    private sealed class TwoBentChannels : WorldGenerator
    {
        private static bool Water(float x) => Mathf.Abs(x) < 12f || (x > 40f && x < 64f);
        public override float GetHeight(float x, float z) =>
            Water(x) ? 26f : Mathf.Abs(z) >= 8f ? 40f : 33f;
        public override Heightmap.Biome GetBiome(float x, float z) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        {
            weight = Water(x) ? 1f : 0f;
            width = 24f;
        }
    }

    /// <summary>The reviewer's fixture: equal-height dry banks at the water's
    /// edge, higher ground along approaches that bend away from the crossing
    /// line.</summary>
    private sealed class BentHighBanks : WorldGenerator
    {
        public override float GetHeight(float x, float z) =>
            Mathf.Abs(x) < 12f ? 26f : Mathf.Abs(z) >= 8f ? 40f : 33f;
        public override Heightmap.Biome GetBiome(float x, float z) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        {
            weight = Mathf.Abs(x) < 12f ? 1f : 0f;
            width = 24f;
        }
    }

    /// <summary>
    /// The combined case the reviewer asked for: a crossing whose heading IS
    /// changed by the snap, and then the painted approaches to the banks it
    /// moved to.
    ///
    /// (The reviewer's ReviewHeadingCoverageTests is a precondition check that
    /// the OLD fixture below exercised a changed heading. It did not, and it
    /// should not -- its crossing is validly left at 90 degrees. That test is
    /// not carried into the suite as a failing case; the gap it names is
    /// filled here instead.)
    ///
    /// It cannot be driven through the router, and that is a fact about the
    /// router rather than a gap in the fixture: an accepted jump runs along one
    /// of eight unit grid directions, so its bearing is a multiple of 45
    /// degrees and already placeable (see
    /// ACrossingScanOnlyWalksTheEightGridDirections), and the one way a router
    /// crossing used to come back off-grid -- the bank-top climb -- is now
    /// resolved by preferring the water's edge, not by turning. So the path is
    /// supplied at an off-grid bearing and painted the way the generator paints
    /// one, which exercises the turn and the painter together.
    /// </summary>
    [Fact]
    public void ATurnedCrossingsApproachesArePaintedToTheBanksItMovedTo()
    {
        var world = new GullyWorld();
        WorldGenerator.instance = world;
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        try
        {
            // 63.43 degrees: between two of the sixteen. The route carries a
            // waypoint of land either side of the crossing, as a routed one
            // does -- with the crossing at index 0 the painter has no land
            // segment in front of it and paints no near approach at all.
            var path = new List<Vector2>
            {
                new(-40f, -20f), new(-20f, -10f), new(20f, 10f), new(40f, 20f),
            };
            float bearing = BridgeLayout.YawDegrees((path[2] - path[1]).normalized);
            Assert.False(BridgeLayout.HeadingIsPlaceable(bearing), $"fixture bearing {bearing:F2}");

            var crossing = Assert.Single(RoadCrossingDetector.Detect(path, world, bridges: true, fords: false));
            float heading = BridgeLayout.YawDegrees(crossing.Direction);

            // PRECONDITION: this case must actually exercise a changed heading.
            Assert.True(BridgeLayout.HeadingIsPlaceable(heading), $"final heading {heading:F3}");
            Assert.True(Mathf.Abs(heading - bearing) > 0.5f,
                $"fixture does not exercise a changed heading: supplied {bearing:F3} deg, final {heading:F3} deg");

            // Paint it the way the generator does, then walk the painted road
            // from each end of the supplied route to the bank it moved to.
            using (GradeCap.Off())
                typeof(RoadNetworkGenerator)
                    .GetMethod("AddRoadPathWithCrossings", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, new object?[] { path, new List<RoadCrossing> { crossing }, 4f, null, null });

            Assert.True(PaintedRoadReaches(path[0], crossing.FromBank, crossing),
                $"no painted road from {path[0]} to the moved near bank ({crossing.FromBank.x:F2},{crossing.FromBank.y:F2})");
            Assert.True(PaintedRoadReaches(path[3], crossing.ToBank, crossing),
                $"no painted road from {path[3]} to the moved far bank ({crossing.ToBank.x:F2},{crossing.ToBank.y:F2})");
            Assert.False(PaintedRoadReaches(path[0], new Vector2(300f, 300f), crossing),
                "the walk reached open country, so it cannot tell connected from not");
        }
        finally { TearDownGeneration(); }
    }

    [Fact]
    public void HowFarATurnMovesABridgehead_Measured()
    {
        // The bound quoted in the handoff was half the span times sin(half a
        // step), which assumes the endpoints are rotated rigidly. They are not:
        // the banks are re-found on the terrain along the turned line, and
        // NearestPlaceableHeadings may fall through to a neighbouring heading.
        // So measure it on a fixture instead of asserting the arithmetic.
        var world = new GullyWorld();
        var router = new RoadPathfinder(world) { Bridges = true, Fords = false };
        var path = router.FindPath(new Vector2(-60f, -30f), new Vector2(60f, 30f));
        Assert.NotNull(path);
        var crossing = Assert.Single(RoadCrossingDetector.Detect(path!, world, bridges: true, fords: false));

        // Distance from each bridgehead to the nearest point of the road the
        // router accepted: what the painter has to bend across.
        float worst = 0f;
        foreach (Vector2 bank in new[] { crossing.FromBank, crossing.ToBank })
        {
            float nearest = float.MaxValue;
            for (int i = 1; i < path!.Count; i++)
            {
                Vector2 a = path[i - 1], b = path[i];
                float len = Vector2.Distance(a, b);
                if (len < 0.01f) continue;
                float t = Mathf.Clamp01(Vector2.Dot(bank - a, b - a) / (len * len));
                nearest = Mathf.Min(nearest, Vector2.Distance(bank, Vector2.Lerp(a, b, t)));
            }
            worst = Mathf.Max(worst, nearest);
        }
        // Not a claimed universal bound -- a measured fact about this fixture,
        // kept so a change that moves bridgeheads much further is noticed.
        Assert.True(worst < BridgeLayout.DeckHalfWidth * 4f,
            $"a bridgehead sits {worst:F2} m off the accepted road on this fixture");
    }
}
