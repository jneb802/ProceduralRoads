using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Can a player repair one of these bridges with ordinary vanilla pieces, and
/// pull a cart across the result?
///
/// The requirement is stronger than "no two pieces overlap": if every
/// intentionally missing pier and deck plate is replaced with a standard piece,
/// the result is a continuous, aligned, four-metre-wide walk from bank to
/// bank, stairs and approaches included. That is a claim about the EMITTED
/// pieces and their SNAP POINTS IN THREE DIMENSIONS, so it is asserted against
/// those -- a pitched plate's corners are not where a flat one's would be, and
/// the tests transform them the way the game does.
///
/// The snap offsets were measured off the prefabs in game (valheimCLI
/// `cli_piece_geometry`, logs/piece-geometry-*.txt):
///
///   wood_floor  2.000 x 0.130 x 2.000, snaps at its four corners (+-1, 0, +-1)
///   wood_beam   2.000 x 0.400 x 0.400, snaps at (+-1, 0, 0)
///   wood_pole2  0.400 x 2.000 x 0.400, snaps at (0, +-1, 0)
///   wood_stair  2.000 x 1.121 x 2.029, snaps HIGH at (+-1, 1, -1), LOW at (+-1, 0, +1)
/// </summary>
public class BridgeRepairabilityTests
{
    private const float Tol = 0.003f;

    /// <summary>A river of a chosen width between banks of chosen heights.
    /// The bank ground may be given its own profile so the ground under a
    /// stair's footprint can differ from the ground at the bank point.</summary>
    private sealed class CrossingWorld : WorldGenerator
    {
        public Vector2 From, To;
        public float Bed = 26f;
        public float FromBankH = 32f;
        public float ToBankH = 32f;
        /// <summary>Ground height as a function of distance PAST the far bank
        /// (metres outward); null means flat at ToBankH.</summary>
        public Func<float, float>? FarApproach;
        /// <summary>Metres of fall per metre to the RIGHT of the crossing line,
        /// past the far bank. The bank falls away ACROSS the deck as well as
        /// along it, which is the only way the two lanes -- and the two corners
        /// of one step's foot edge -- can reach the dirt at different steps.</summary>
        public float CrossSlope = 0f;

        public override float GetHeight(float wx, float wy)
        {
            Vector2 d = (To - From).normalized;
            float w = Vector2.Distance(From, To);
            Vector2 p = new(wx, wy);
            float t = Vector2.Dot(p - From, d);
            if (t <= 0f) return FromBankH;
            if (t >= w)
            {
                float h = FarApproach != null ? FarApproach(t - w) : ToBankH;
                if (CrossSlope != 0f)
                {
                    float lateral = Vector2.Dot(p - From, new Vector2(-d.y, d.x));
                    h -= lateral * CrossSlope;
                }
                return h;
            }
            return Bed;
        }

        public override Heightmap.Biome GetBiome(float wx, float wy) =>
            GetHeight(wx, wy) < RoadConstants.SeaLevel - 2f ? Heightmap.Biome.Ocean : Heightmap.Biome.Meadows;

        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            Vector2 d = (To - From).normalized;
            float w = Vector2.Distance(From, To);
            float t = Vector2.Dot(new Vector2(wx, wy) - From, d);
            weight = t > 0f && t < w ? 1f : 0f;
            width = weight > 0f ? w : 0f;
        }
    }

    private static (RoadCrossing crossing, CrossingWorld world) Crossing(
        float width, float headingDegrees = 0f, float fromBankH = 32f, float toBankH = 32f,
        float fairwayWidth = 0f, CrossingKind kind = CrossingKind.Bridge, FordStyle style = FordStyle.None,
        Func<float, float>? farApproach = null, float crossSlope = 0f)
    {
        float rad = headingDegrees * Mathf.PI / 180f;
        Vector2 dir = new(Mathf.Sin(rad), Mathf.Cos(rad));
        Vector2 from = new(-width * 0.5f * dir.x, -width * 0.5f * dir.y);
        Vector2 to = from + dir * width;
        var world = new CrossingWorld { From = from, To = to, FromBankH = fromBankH, ToBankH = toBankH, FarApproach = farApproach, CrossSlope = crossSlope };
        var crossing = RoadCrossing.Between(from, to, world.Bed, (from + to) * 0.5f, fairwayWidth, kind, style);
        return (crossing, world);
    }

    // ---- projections and snap helpers ----

    private static float AlongOf(RoadCrossing c, Vector3 p) =>
        Vector2.Dot(new Vector2(p.x, p.z) - c.FromBank, c.Direction);

    private static float LateralOf(RoadCrossing c, Vector3 p)
    {
        Vector2 side = new(-c.Direction.y, c.Direction.x);
        return Vector2.Dot(new Vector2(p.x, p.z) - c.FromBank, side);
    }

    private static List<BridgePiece> Of(IEnumerable<BridgePiece> plan, BridgePieceKind kind) =>
        plan.Where(p => p.Kind == kind).ToList();

    private static bool Coincide(Vector3 a, Vector3 b, float tol = 0.01f) => Vector3.Distance(a, b) <= tol;

    /// <summary>How many snap points of a land on snap points of b.</summary>
    private static int SharedSnaps(BridgePiece a, BridgePiece b, float tol = 0.01f)
    {
        var sa = BridgeLayout.SnapPoints(a);
        var sb = BridgeLayout.SnapPoints(b);
        return sa.Count(p => sb.Any(q => Coincide(p, q, tol)));
    }

    // ---- widths ----

    public static IEnumerable<object[]> BridgeWidths() => new List<object[]>
    {
        new object[] { 80f },      // exact multiple
        new object[] { 80.01f },   // remainder just above zero
        new object[] { 80.05f },   // the width that stacked two stations 5 cm apart
        new object[] { 80.6f },
        new object[] { 80.9f },    // just under half a span
        new object[] { 81.1f },    // just over
        new object[] { 81.9f },    // just under a full span
        new object[] { 77.51f },   // measured in game, remainder 1.51 m
        new object[] { 40f },
        new object[] { 127.6f },   // near the 128 m cap
    };

    public static IEnumerable<object[]> SpanWidths() => new List<object[]>
    {
        new object[] { 8f },
        new object[] { 8.05f },
        new object[] { 9.1f },
        new object[] { 9.5f },     // measured in game, remainder 1.50 m
        new object[] { 9.9f },
        new object[] { 2.05f },
        new object[] { 6f },
    };

    // ---- the complete layout, in 3D ----

    [Theory]
    [MemberData(nameof(BridgeWidths))]
    public void ACompletedBridgeIsOneContinuousAlignedWalk(float width) =>
        AssertRepairable(Crossing(width));

    [Theory]
    [MemberData(nameof(SpanWidths))]
    public void ACompletedFordSpanIsOneContinuousAlignedWalk(float width) =>
        AssertRepairable(Crossing(width, kind: CrossingKind.Ford, style: FordStyle.Span));

    // The sixteen headings a crossing that carries pieces may stand on
    // (RoadCrossing.Build turns it onto one); an off-grid bearing is not a
    // case the layout has to serve, because no such crossing is built.
    [Theory]
    [InlineData(0f)]
    [InlineData(22.5f)]
    [InlineData(90f)]
    [InlineData(202.5f)]
    public void ARotatedCrossingIsLaidOutTheSameWay(float heading) =>
        AssertRepairable(Crossing(77.51f, headingDegrees: heading));

    [Theory]
    [InlineData(32f, 34f)]      // 2 m of bank delta over 77.5 m
    [InlineData(34f, 32f)]
    [InlineData(31f, 33.5f)]    // the pathfinder's MaxBridgeBankDelta
    public void UnequalBanksGiveALevelDeckWalkedOffByTheStairs(float fromBankH, float toBankH)
    {
        var site = Crossing(77.51f, fromBankH: fromBankH, toBankH: toBankH);
        var plan = AssertRepairable(site);   // includes the level-rotation check

        // The deck is one height, the higher bank's, whatever the banks do.
        var decks = Of(plan, BridgePieceKind.Deck);
        float deckH = BridgeLayout.DeckHeight(site.crossing, site.world);
        Assert.InRange(deckH, Mathf.Max(fromBankH, toBankH) - Tol, Mathf.Max(fromBankH, toBankH) + Tol);
        foreach (var d in decks)
            Assert.InRange(d.Position.y, deckH - Tol, deckH + Tol);
        Assert.Equal(BridgeLayout.DeckSpan, BridgeLayout.StationSpacing());

        // The difference is walked off: each end's run is at least as many
        // steps as its own drop needs, at StairRise per step, and both land.
        // (Not "the lower bank has MORE steps": a deck end that stops short of
        // the bank spends its first step crossing the remainder, so a run can
        // be long for a reason that is not the drop.)
        (float dropFrom, float dropTo) = BridgeLayout.BankDrop(site.crossing, site.world);
        float built = BridgeLayout.BuiltLength(site.crossing.Width);
        foreach ((bool near, float drop) in new[] { (true, dropFrom), (false, dropTo) })
        {
            int steps = StairSteps(plan, site.crossing, near, built);
            int needed = Mathf.CeilToInt(drop / BridgeLayout.StairRise - 0.001f);
            Assert.True(steps >= needed,
                $"the {(near ? "near" : "far")} end drops {drop:F2} m but its run is {steps} step(s)");
        }
        Assert.Equal((true, true), BridgeLayout.StairRunsLand(site.crossing, site.world));
    }

    [Fact]
    public void ASteepShortBridgeIsStillLevelAndStillLands()
    {
        // 2.5 m of bank delta over 8 m: the steepest the pathfinder allows, on
        // a short span. The deck stays level and the low end takes the steps.
        var site = Crossing(8.3f, fromBankH: 31f, toBankH: 33.5f);
        var plan = AssertRepairable(site);
        foreach (var d in Of(plan, BridgePieceKind.Deck))
            Assert.InRange(d.Position.y, 33.5f - Tol, 33.5f + Tol);
        Assert.Equal((true, true), BridgeLayout.StairRunsLand(site.crossing, site.world));
    }

    /// <summary>Steps in one end's run, per lane (both lanes carry the same count).</summary>
    private static int StairSteps(List<BridgePiece> plan, RoadCrossing crossing, bool near, float built) =>
        Of(plan, BridgePieceKind.Stair).Count(st =>
            Mathf.Abs(LateralOf(crossing, st.Position) - BridgeLayout.LaneOffset) < 0.1f
            && (near ? AlongOf(crossing, st.Position) < 0f : AlongOf(crossing, st.Position) > built));

    // ---- P2: a run ends where its FOOT lands, not where its centre sits ----

    [Fact]
    public void AStairRunLandsOnItsFootEdge_NotItsCentre()
    {
        // The shelf reaches exactly 1 m past the bank and then drops 2 m: a
        // step's centre is over the shelf while its foot edge is over the
        // drop. Terminating on the centre left the bottom step's foot 1.00 m
        // in the air (the reviewer's reproduction).
        AssertRepairable(Crossing(76f, farApproach: past => past <= 1f ? 32f : 30f));
    }

    [Fact]
    public void ACrossSlopedBankLandsEveryFootCornerSeparately()
    {
        // The bank falls away ACROSS the deck. At 1 m per metre the first
        // step's centre is exactly on the dirt in the right lane while its
        // OUTER foot corner, a metre further out, is a metre below it: the
        // centre rule lands here, the foot-edge rule marches one more step.
        // (At 0.5 the corner comes out exactly level with the foot and the two
        // rules agree, which is why this case is steep.)
        AssertRepairable(Crossing(64f, crossSlope: 1f));
    }

    /// <summary>The whole requirement, asserted on the emitted pieces' snaps.</summary>
    private List<BridgePiece> AssertRepairable((RoadCrossing crossing, CrossingWorld world) site)
    {
        (RoadCrossing crossing, CrossingWorld world) = site;
        var plan = BridgeLayout.SolveComplete(crossing, world, 4242);
        Assert.NotEmpty(plan);

        float spacing = BridgeLayout.StationSpacing();
        int bays = BridgeLayout.Bays(crossing.Width);
        float built = BridgeLayout.BuiltLength(crossing.Width);

        // 0. Every piece a player could be asked to replace is one the vanilla
        //    hammer can actually produce. Player.UpdatePlacementGhost builds
        //    the ghost rotation as Quaternion.Euler(0, m_placeRotationDegrees *
        //    m_placeRotation, 0) and its snap path moves the ghost without
        //    turning it, so pitch and roll are both always zero: a pitched
        //    plate is unrepairable no matter how well it fits its neighbours.
        foreach (var piece in plan.Where(x => x.Kind != BridgePieceKind.Debris))
        {
            Assert.True(Mathf.Abs(piece.PitchDegrees) < 1e-4f,
                $"{piece.Prefab} at along {AlongOf(crossing, piece.Position):F2} is pitched {piece.PitchDegrees:F3} deg; the hammer places level");
            Assert.True(Mathf.Abs(piece.RollDegrees) < 1e-4f,
                $"{piece.Prefab} at along {AlongOf(crossing, piece.Position):F2} is rolled {piece.RollDegrees:F3} deg; the hammer places level");
            // ... and on one of the sixteen headings it can turn to.
            Assert.True(BridgeLayout.HeadingIsPlaceable(piece.YawDegrees, 0.06f),
                $"{piece.Prefab} at along {AlongOf(crossing, piece.Position):F2} stands at yaw {piece.YawDegrees:F2}, which is not a multiple of {BridgeLayout.PlaceableHeadingStep}");
        }

        // 1. Never longer than the crossing the pathfinder priced, and never
        //    more than one stair run short of it.
        Assert.True(built <= crossing.Width + Tol, $"built {built:F3} over a {crossing.Width:F3} m crossing");
        Assert.True(crossing.Width - built < BridgeLayout.DeckSpan + Tol, $"{crossing.Width - built:F3} m left unbridged");

        // 2. Two lanes, one plate per bay each, centred at +-LaneOffset.
        var decks = Of(plan, BridgePieceKind.Deck);
        var left = decks.Where(d => LateralOf(crossing, d.Position) < 0f).OrderBy(d => AlongOf(crossing, d.Position)).ToList();
        var right = decks.Where(d => LateralOf(crossing, d.Position) > 0f).OrderBy(d => AlongOf(crossing, d.Position)).ToList();
        Assert.Equal(bays, left.Count);
        Assert.Equal(bays, right.Count);
        foreach (var lane in new[] { left, right })
        {
            for (int i = 0; i < lane.Count; i++)
            {
                Assert.InRange(AlongOf(crossing, lane[i].Position), (i + 0.5f) * spacing - Tol, (i + 0.5f) * spacing + Tol);
                Assert.InRange(Mathf.Abs(LateralOf(crossing, lane[i].Position)), BridgeLayout.LaneOffset - Tol, BridgeLayout.LaneOffset + Tol);
            }
            // 3. Consecutive plates share their two edge snaps: in 3D, so a
            //    pitched plate's corner is where the next plate's corner is.
            for (int i = 0; i + 1 < lane.Count; i++)
                Assert.True(SharedSnaps(lane[i], lane[i + 1]) == 2,
                    $"plates {i} and {i + 1} share {SharedSnaps(lane[i], lane[i + 1])} snaps, not 2 (pitch {lane[i].PitchDegrees:F2})");
        }
        // 4. The two lanes meet along the centre seam: each bay's left and
        //    right plate share the two seam snaps.
        for (int i = 0; i < bays; i++)
            Assert.True(SharedSnaps(left[i], right[i]) == 2, $"bay {i}: lanes share {SharedSnaps(left[i], right[i])} snaps at the seam");

        // 5. Both ends: the top step of each lane's stair shares its two HIGH
        //    snaps with the end plate's end edge. This is the check an
        //    overlap-for-end-gap swap fails.
        var stairs = Of(plan, BridgePieceKind.Stair);
        foreach ((float endAlong, BridgePiece lp, BridgePiece rp) in new[]
                 {
                     (0f, left[0], right[0]),
                     (built, left[bays - 1], right[bays - 1]),
                 })
        {
            foreach (var plate in new[] { lp, rp })
            {
                var top = stairs
                    .Where(st => Mathf.Abs(LateralOf(crossing, st.Position) - LateralOf(crossing, plate.Position)) < 0.1f
                                 && Mathf.Abs(AlongOf(crossing, st.Position) - endAlong) < 2.5f)
                    .OrderByDescending(st => st.Position.y).FirstOrDefault();
                Assert.True(top != null, $"no stair beside the {(endAlong == 0f ? "near" : "far")} end of the lane");
                Assert.True(SharedSnaps(top!, plate) == 2,
                    $"stair top at end {endAlong:F2} shares {SharedSnaps(top!, plate)} snaps with the deck, not 2");
            }
        }

        // 6. Stair to ground: the lowest step of each stair has its LOW snaps
        //    in the dirt -- at or below the ground sampled at THAT footprint.
        foreach (float endAlong in new[] { 0f, built })
        {
            foreach (float lat in new[] { -BridgeLayout.LaneOffset, BridgeLayout.LaneOffset })
            {
                var run = stairs.Where(st => Mathf.Abs(LateralOf(crossing, st.Position) - lat) < 0.1f
                                             && (endAlong == 0f ? AlongOf(crossing, st.Position) < 0f : AlongOf(crossing, st.Position) > built))
                                .OrderBy(st => st.Position.y).ToList();
                Assert.NotEmpty(run);
                var bottom = run[0];
                var lows = BridgeLayout.SnapPoints(bottom).Where(s => s.y < bottom.Position.y + 0.5f).ToList();
                Assert.Equal(2, lows.Count);
                foreach (var lo in lows)
                {
                    // the biome-blended height is the terrain the game builds
                    float g = BiomeBlendedHeight.GetBlendedHeight(lo.x, lo.z, world);
                    Assert.True(lo.y <= g + 0.3f, $"the bottom step's foot at ({lo.x:F1},{lo.z:F1}) is {lo.y - g:F2} m above the ground");
                }
                // and every step in the run chains: each one's HIGH snaps are
                // the previous one's LOW snaps
                for (int i = 0; i + 1 < run.Count; i++)
                    Assert.Equal(2, SharedSnaps(run[i], run[i + 1]));
            }
        }

        // 7. Support: every plate rests on a beam at each end (a beam under
        //    each of its end edges, in its lane), every beam has a post column
        //    reaching the ground beneath its outer half, and posts chain on
        //    their 2 m snap.
        //
        //    A FREE SPAN is exempt from this check, and not by weakening it:
        //    it has no stations in the channel by design, so its deck is
        //    carried from the two abutments and there are no mid-span beams
        //    for a plate to rest on. Everything else here still applies to one
        //    -- the deck is level, the stairs land, the grid is placeable, and
        //    check 8 below still holds because its only posts are at the
        //    abutments, under beams. BridgeFreeSpanTests says what IS asserted
        //    of a span. We do not check what a PLAYER could build across it:
        //    players get better materials that span further, so that is
        //    deliberately not our problem.
        var beams = Of(plan, BridgePieceKind.Beam);
        var posts = Of(plan, BridgePieceKind.Post);
        if (!BridgeLayout.IsFreeSpan(crossing, world))
        {
            foreach (var plate in decks)
            {
                var edges = BridgeLayout.SnapPoints(plate);
                int supported = 0;
                foreach (var b in beams)
                {
                    if (Mathf.Abs(LateralOf(crossing, b.Position) - LateralOf(crossing, plate.Position)) > 0.1f) continue;
                    if (edges.Any(e => Mathf.Abs(AlongOf(crossing, new Vector3(e.x, 0, e.z)) - AlongOf(crossing, b.Position)) < 0.05f
                                       && e.y - b.Position.y >= BridgeLayout.BeamBelowDeck - 0.02f
                                       && e.y - b.Position.y <= BridgeLayout.BeamBelowDeck + 0.15f))
                        supported++;
                }
                Assert.True(supported >= 2, $"plate at along {AlongOf(crossing, plate.Position):F2} rests on {supported} beam(s), not 2");
            }
        }
        foreach (var group in posts.GroupBy(p => (Mathf.Round(p.Position.x * 100f), Mathf.Round(p.Position.z * 100f))))
        {
            var column = group.OrderByDescending(p => p.Position.y).ToList();
            for (int i = 0; i + 1 < column.Count; i++)
                Assert.Equal(1, SharedSnaps(column[i], column[i + 1]));
            var lowest = column[column.Count - 1];
            Assert.True(lowest.Position.y <= BiomeBlendedHeight.GetBlendedHeight(lowest.Position.x, lowest.Position.z, world) + BridgeLayout.PostSegment * 0.5f + 0.01f,
                "a post column does not reach the ground");
        }

        // 8. Cart clearance: nothing but deck plates above the WALKING
        //    SURFACE within the lanes, over the whole built length. The
        //    surface is the plate's measured collider top, 0.097 above its
        //    origin; a beam's top (0.2 above its centre, itself 0.13 below the
        //    deck) ends inside the plate's thickness, under the surface.
        const float floorTop = 0.097f, beamHalfHeight = 0.2f;
        foreach (var piece in plan.Where(p => p.Kind != BridgePieceKind.Deck && p.Kind != BridgePieceKind.Stair))
        {
            float along = AlongOf(crossing, piece.Position);
            if (along < 0f || along > built) continue;
            if (Mathf.Abs(LateralOf(crossing, piece.Position)) > BridgeLayout.DeckHalfWidth) continue;
            if (piece.Kind == BridgePieceKind.Beam)
            {
                // A beam sits BeamBelowDeck under its own station's deck height;
                // on a pitched deck that is not the nearest plate's centre, so
                // the surface it must stay under is the one over its station.
                float surface = piece.Position.y + BridgeLayout.BeamBelowDeck + floorTop;
                Assert.True(piece.Position.y + beamHalfHeight <= surface + 0.005f,
                    $"beam at along {along:F2} rises {piece.Position.y + beamHalfHeight - surface:F3} m above the walking surface");
            }
            else if (piece.Kind == BridgePieceKind.Post)
            {
                // A post only ever stands under a beam; it must not poke through it.
                var beamAbove = beams.Where(b => Mathf.Abs(AlongOf(crossing, b.Position) - along) < 0.05f
                                                 && Mathf.Abs(LateralOf(crossing, b.Position)) <= BridgeLayout.DeckHalfWidth)
                                     .OrderBy(b => b.Position.y).FirstOrDefault();
                Assert.True(beamAbove != null, $"post at along {along:F2} stands under no beam");
                Assert.True(piece.Position.y + BridgeLayout.PostSegment * 0.5f <= beamAbove!.Position.y + 0.005f,
                    $"post at along {along:F2} rises through its beam");
            }
        }

        return plan;
    }

    // ---- the pitched plate really is where the game would put it ----

    [Fact]
    public void ThePitchTransformMatchesPlainTrigonometry()
    {
        // Independent of SnapPoints: a plate pitched by theta has its +z corner
        // cos(theta) forward and -sin(theta) up (Unity's negative pitch tips
        // the nose up), and yaw turns local +z onto the crossing direction.
        var p = new BridgePiece { Prefab = BridgeLayout.DeckPrefab, Position = Vector3.zero, YawDegrees = 90f, PitchDegrees = -10f };
        var snaps = BridgeLayout.SnapPoints(p);
        float c = Mathf.Cos(10f * Mathf.PI / 180f), s = Mathf.Sin(10f * Mathf.PI / 180f);
        // yaw 90: local +z -> world +x, local +x -> world -z
        Assert.Contains(snaps, q => Coincide(q, new Vector3(c, s, -1f), 1e-4f));    // local (1,0,1)
        Assert.Contains(snaps, q => Coincide(q, new Vector3(-c, -s, 1f), 1e-4f));   // local (-1,0,-1)
    }

    // ---- ruin ----

    [Fact]
    public void LeftAndRightLanesAreRuinedIndependently_AndCarryTheirOwnHealth()
    {
        int both = 0, leftOnly = 0, rightOnly = 0, neither = 0;
        var healthPairs = new List<(float, float)>();
        for (int seed = 1; seed <= 40 && (both == 0 || leftOnly == 0 || rightOnly == 0 || neither == 0); seed++)
        {
            var (crossing, world) = Crossing(60f);
            var plan = BridgeLayout.Solve(crossing, world, seed);
            var complete = BridgeLayout.SolveComplete(crossing, world, seed);
            int bays = BridgeLayout.Bays(crossing.Width);
            var decks = Of(plan, BridgePieceKind.Deck);
            for (int i = 1; i + 1 < bays; i++)
            {
                // a bay with both stations standing is the only place a lane
                // decision is made, so ask the complete plan where the bay is
                float along = (i + 0.5f) * BridgeLayout.StationSpacing();
                var l = decks.FirstOrDefault(d => Mathf.Abs(AlongOf(crossing, d.Position) - along) < 0.05f && LateralOf(crossing, d.Position) < 0f);
                var r = decks.FirstOrDefault(d => Mathf.Abs(AlongOf(crossing, d.Position) - along) < 0.05f && LateralOf(crossing, d.Position) > 0f);
                bool stationsStand = Of(plan, BridgePieceKind.Beam).Count(b => Mathf.Abs(AlongOf(crossing, b.Position) - i * 2f) < 0.05f || Mathf.Abs(AlongOf(crossing, b.Position) - (i + 1) * 2f) < 0.05f) == 4;
                if (!stationsStand) continue;
                if (l != null && r != null) { both++; healthPairs.Add((l.HealthFraction, r.HealthFraction)); }
                else if (l != null) leftOnly++;
                else if (r != null) rightOnly++;
                else neither++;
            }
        }
        Assert.True(both > 0 && leftOnly > 0 && rightOnly > 0 && neither > 0,
            $"all four lane states should occur: both={both} left={leftOnly} right={rightOnly} neither={neither}");
        Assert.Contains(healthPairs, hp => Mathf.Abs(hp.Item1 - hp.Item2) > 0.05f);
    }

    [Fact]
    public void EverySurvivingPlateStillRestsOnTwoBeams()
    {
        // Asymmetric ruin must not leave a plate hanging: a plate exists only
        // where both its stations stand, whatever happened to the other lane.
        for (int seed = 1; seed <= 12; seed++)
        {
            var (crossing, world) = Crossing(77.51f, fairwayWidth: 30f);
            var plan = BridgeLayout.Solve(crossing, world, seed);
            var beams = Of(plan, BridgePieceKind.Beam);
            foreach (var plate in Of(plan, BridgePieceKind.Deck))
            {
                var edges = BridgeLayout.SnapPoints(plate);
                int under = beams.Count(b =>
                    Mathf.Abs(LateralOf(crossing, b.Position) - LateralOf(crossing, plate.Position)) < 0.1f &&
                    edges.Any(e => Mathf.Abs(AlongOf(crossing, new Vector3(e.x, 0, e.z)) - AlongOf(crossing, b.Position)) < 0.05f));
                Assert.True(under >= 2, $"seed {seed}: a surviving plate rests on {under} beam(s)");
            }
        }
    }

    [Fact]
    public void RuinIsStableUnderReplayAndKeyedByPiece()
    {
        var (crossing, world) = Crossing(77.51f, fairwayWidth: 30f);
        var a = BridgeLayout.Solve(crossing, world, 99);
        var b = BridgeLayout.Solve(crossing, world, 99);
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Prefab, b[i].Prefab);
            Assert.True(Coincide(a[i].Position, b[i].Position, 1e-5f));
            Assert.Equal(a[i].HealthFraction, b[i].HealthFraction);
        }
        // Keyed by (bay, side, role): a decision does not move when an
        // unrelated one is asked first or not at all.
        var r = new BridgeLayout.Ruin(99, crossing);
        Assert.Equal(r.Roll(5, BridgeLayout.Ruin.Left, BridgeLayout.Ruin.RoleDeck), r.Roll(5, BridgeLayout.Ruin.Left, BridgeLayout.Ruin.RoleDeck));
        Assert.NotEqual(r.Roll(5, BridgeLayout.Ruin.Left, BridgeLayout.Ruin.RoleDeck), r.Roll(5, BridgeLayout.Ruin.Right, BridgeLayout.Ruin.RoleDeck));
        Assert.NotEqual(r.Roll(5, BridgeLayout.Ruin.Left, BridgeLayout.Ruin.RoleDeck), r.Roll(6, BridgeLayout.Ruin.Left, BridgeLayout.Ruin.RoleDeck));
        // and a different seed ruins differently
        var c = BridgeLayout.Solve(crossing, world, 100);
        Assert.True(c.Count != a.Count || c.Where((p, i) => !Coincide(p.Position, a[i].Position) || p.HealthFraction != a[i].HealthFraction).Any());
    }

    [Fact]
    public void TheNavigationGapIsReportedApartFromRuin()
    {
        var (crossing, world) = Crossing(77.51f, fairwayWidth: 30f);
        var plan = BridgeLayout.Solve(crossing, world, 7);
        float mid = crossing.Along(crossing.FairwayCenter);
        Assert.True(BridgeLayout.InNavigationGap(crossing, mid));
        Assert.False(BridgeLayout.InNavigationGap(crossing, 1f));
        Assert.DoesNotContain(plan, p => p.Kind == BridgePieceKind.Deck && BridgeLayout.InNavigationGap(crossing, AlongOf(crossing, p.Position)));
        Assert.Contains(BridgeLayout.SolveComplete(crossing, world, 7), p => p.Kind == BridgePieceKind.Deck && BridgeLayout.InNavigationGap(crossing, AlongOf(crossing, p.Position)));
    }

    [Theory]
    [MemberData(nameof(BridgeWidths))]
    public void EveryPieceTheGameBuildsStandsWhereTheCompletedBridgeHasOne(float width)
    {
        var (crossing, world) = Crossing(width, fairwayWidth: 30f);
        var complete = BridgeLayout.SolveComplete(crossing, world, 99);
        var built = BridgeLayout.Solve(crossing, world, 99);
        Assert.True(complete.Count > built.Count);
        foreach (BridgePiece piece in built.Where(p => p.Kind is BridgePieceKind.Deck or BridgePieceKind.Beam or BridgePieceKind.Stair))
            Assert.True(complete.Any(c => c.Prefab == piece.Prefab && Coincide(c.Position, piece.Position)),
                $"{piece.Kind} at {piece.Position} is not on the completed bridge's grid");
    }

    // ---- the ground under a moved stair ----

    [Fact]
    public void AFarStairWhoseGroundDiffersFromTheBankStillReachesTheDirt()
    {
        // The far stair is anchored one remainder inward of the bank, and it
        // marches OUTWARD; the ground under its steps is not the ground at the
        // bank point. Here the approach drops 1.5 m two metres past the bank,
        // so a step count taken from the bank height would end with a foot in
        // the air. The stair must keep going until its foot is in the dirt.
        var site = Crossing(77.51f, farApproach: past => past < 2f ? 32f : 30.5f);
        var plan = AssertRepairable(site);
        var far = Of(plan, BridgePieceKind.Stair).Where(s => AlongOf(site.crossing, s.Position) > 70f).ToList();
        // more than the one step a flush bank needs
        Assert.True(far.Count >= 4, $"{far.Count} far-stair pieces");
    }

    [Fact]
    public void TheOldFixedStepCountWouldHaveLeftThatFootInTheAir()
    {
        // The regression, kept as arithmetic: the old count was
        // ceil(deckH - bankH) from the bank point. With a level bank (deckH ==
        // bankH) that is ONE step, whose foot is 1 m below the deck; over an
        // approach 1.5 m lower than the bank, that foot hangs 0.5 m up.
        float deckH = 32f, bankH = 32f, groundUnderStep = 30.5f;
        int oldSteps = Mathf.Max(1, Mathf.CeilToInt((deckH - bankH) / 1f - 0.02f));
        float oldFoot = deckH - 1f - (oldSteps - 1);
        Assert.True(oldFoot > groundUnderStep + 0.15f, "the old policy's last foot should be in the air here");
    }

    // ---- migration ----

    [Fact]
    public void ASavedNarrowLayoutIsRecognisedAndItsPiecesReplaced()
    {
        // This pin exists to FORCE the question on every geometry change, not
        // to record a number. Layout 4: the navigation gap went 20 m -> 16 m
        // (moves every bridge over a sailable channel) and free spans changed
        // narrow ones again. If you are here because this failed, the question
        // is "did the emitted shape move?", and if it did the answer is a bump.
        Assert.Equal(4, BridgeLayout.LayoutVersion);
        // The persistence round-trip itself is exercised in SpawnedZonesSurvive...;
        // here the contract that matters: a record without the current layout
        // id must not mark any zone spawned.
        Assert.False(RoadNetworkPersistence.BridgeLayoutIsStale);
    }

    // ---- the two earlier policies, kept as arithmetic ----

    [Theory]
    [InlineData(80.05f)]
    [InlineData(80.6f)]
    [InlineData(2.05f)]
    public void NeitherEarlierPolicyGotBothEndsRight(float width)
    {
        const float span = BridgeLayout.DeckSpan;
        int aCount = Mathf.CeilToInt(width / span) + 1;
        float aLast = Mathf.Min((aCount - 1) * span, width);
        float aPrev = Mathf.Min((aCount - 2) * span, width);
        Assert.True(aLast - aPrev < span * 0.5f, $"policy A was supposed to stack stations at width {width}");
        float bStairGap = width - aPrev;
        Assert.True(bStairGap > 0.01f, $"policy B was supposed to leave an end gap at width {width}");
        float[] alongs = BridgeLayout.StationsAlong(width);
        for (int i = 0; i + 1 < alongs.Length; i++)
            Assert.InRange(alongs[i + 1] - alongs[i], span - Tol, span + Tol);
        Assert.True(width - alongs[alongs.Length - 1] < span);
        Assert.True(alongs[alongs.Length - 1] <= width + Tol);
    }
}
