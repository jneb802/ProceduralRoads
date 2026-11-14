using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The ring at a road's end (RoadEndRing), the one owner of where a staircase meets its road there: a circle
/// 0.5 m in from the road's edge round the road's end, the arc back along the road cut out with a clear lane, a staircase
/// starting anywhere on the rest, straight on preferred. Its geometry and keep-out directly; laid flights from a dead end, a
/// through road, and down from a road's end; and the crossing cases first written for the test it replaced
/// (RoadStairRuns.CrossesItsRoad), on a laid staircase's shape over six worlds (RoadSeedA -7999.9,-2358.6: its road arriving
/// heading 251 to its end, the flight climbing north from the road's middle).
/// </summary>
[Collection("StairLayout")]
public class RoadEndRingTests
{
    private static readonly Vector2 End = new(-7999.9f, -2357.6f);
    private const float Travel = 251f, Height = 36f;

    private static StairPiece Stair(Vector2 at, float y, float climbing) =>
        new() { Kind = StairPieceKind.Stair, Prefab = "stone_stair", Position = new Vector3(at.x, y, at.y), YawDegrees = StairLayout.Wrap(climbing + 180f) };

    /// <summary>A flight whose first step's low edge is at <paramref name="foot"/>, climbing <paramref name="heading"/>, with
    /// its sunk stair a stair back under the road's level.</summary>
    private static StairPlan Flight(Vector2 foot, float heading, float height = Height)
    {
        var plan = new StairPlan { StartHeading = heading, Reached = true, SunkPieces = 1 };
        Vector2 step = StairLayout.Forward(heading) * StairLayout.PieceRun;
        for (int i = -1; i < 5; i++) plan.Pieces.Add(Stair(foot + step * (i + 0.5f), height + i, heading));
        return plan;
    }

    private static StairPlan Pieces(bool reversed, params (float x, float y, float z, float yaw)[] stairs)
    {
        var plan = new StairPlan { Reversed = reversed, Reached = true };
        foreach (var (x, y, z, yaw) in stairs)
            plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Stair, Prefab = "stone_stair", Position = new Vector3(x, y, z), YawDegrees = yaw });
        return plan;
    }

    /// <summary>A planned road's piece arriving on <paramref name="travel"/> to its end at <paramref name="end"/>, at
    /// <paramref name="height"/>, a point a metre for 12 m: ending there (or starting there, <paramref name="atStart"/>).</summary>
    private static RoadSpatialGrid.PlannedPath RoadTo(Vector2 end, float travel, float height, bool atStart = false)
    {
        var points = Enumerable.Range(0, 13).Select(k => end - StairLayout.Forward(travel) * (12 - k)).ToList();
        if (atStart) points.Reverse();
        return new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(_ => height), points.ConvertAll(_ => default(RoadPointDebugInfo)),
            RoadNetworkGenerator.RoadWidth, 12f, true);
    }

    // ---- Geometry ----

    /// <summary>For a 4 m road the circle runs 1.5 m from the road's end; the arc back along the road is cut where the 2 m
    /// lane meets it (41.8 degrees either side of straight back), and on a through road straight on too.</summary>
    [Fact]
    public void TheRingRunsHalfAMetreInsideTheRoadsEdgeWithItsBackArcCut()
    {
        var ring = new RoadEndRing(new Vector2(0f, 0f), 0f, 10f);
        Assert.Equal(1.5f, ring.Radius, 3);
        Assert.Equal(1f, new RoadEndRing(new Vector2(0f, 0f), 0f, 10f, roadWidth: 3f).Radius, 3);
        Assert.Equal(41.8f, ring.CutArc, 1);
        Assert.False(ring.ArcCut(0f));
        Assert.False(ring.ArcCut(135f));
        Assert.True(ring.ArcCut(157.5f));
        Assert.True(ring.ArcCut(180f));
        var through = new RoadEndRing(new Vector2(0f, 0f), 0f, 10f, through: true);
        Assert.True(through.ArcCut(0f));
        Assert.True(through.ArcCut(22.5f));
        Assert.False(through.ArcCut(45f));
        // Straight on is the front of the circle, north for a road heading north.
        Assert.True(Vector2.Distance(ring.ArcPoint(0f), new Vector2(0f, 1.5f)) < 0.001f);
        Assert.True(Vector2.Distance(ring.ArcPoint(90f), new Vector2(1.5f, 0f)) < 0.001f);
    }

    /// <summary>Kept out: the circle more than Delta in, and the lane back along the road (and on along it, through); not the
    /// road's sides past the lane, nor ahead of a dead end.</summary>
    [Fact]
    public void TheRingKeepsOutItsInnerCircleAndTheLane()
    {
        var ring = new RoadEndRing(new Vector2(0f, 0f), 0f, 10f);
        Assert.True(ring.KeptOut(new Vector2(0f, 0.5f)));
        Assert.False(ring.KeptOut(new Vector2(0f, 1.2f)), "within Delta of the circle");
        Assert.True(ring.KeptOut(new Vector2(0.5f, -2.5f)), "in the lane back along the road");
        Assert.False(ring.KeptOut(new Vector2(1.5f, -2.5f)), "beside the lane");
        Assert.False(ring.KeptOut(new Vector2(0f, 2.5f)), "ahead of a dead end");
        Assert.False(ring.KeptOut(new Vector2(0.5f, -4f)), "past the lane's length, the joining step's reach: the rest of the road is OverRoad's");
        var through = new RoadEndRing(new Vector2(0f, 0f), 0f, 10f, through: true);
        Assert.True(through.KeptOut(new Vector2(0f, 2.5f)), "ahead on a through road");
    }

    /// <summary>The starts are priced by their turn off the road's travel, linear in angle (no straight/turned split), and by
    /// how far off square they leave the circle: the cheapest is straight on from the front; a quarter turn from the side
    /// costs 8; every start keeps its own first piece out.</summary>
    [Fact]
    public void StartsArePricedByTheirTurnOffTheRoadStraightOnFirst()
    {
        var ring = new RoadEndRing(new Vector2(0f, 0f), 0f, 10f);
        List<RoadEndRing.Start> starts = ring.Starts(RoadStairRuns.Width);
        Assert.Equal(0f, starts[0].Heading);
        Assert.Equal(0f, starts[0].Arc);
        Assert.Equal(0f, starts[0].Price);
        Assert.Equal(8f, ring.Price(90f, 90f), 3);
        Assert.Equal(4f, ring.Price(45f, 45f), 3);
        Assert.Equal(2f * 4f + 0.5f * 4f, ring.Price(90f, 0f), 3);
        Assert.True(ring.Price(22.5f, 22.5f) < ring.Price(45f, 45f) && ring.Price(45f, 45f) < ring.Price(67.5f, 67.5f));
        foreach (RoadEndRing.Start s in starts)
        {
            Assert.False(ring.ArcCut(s.Arc));
            Assert.True(Mathf.Abs(Vector2.Distance(s.At, ring.Centre) - ring.Radius) < 0.001f);
            foreach (Vector2 p in RoadEndRing.Footprint(s.At, s.Heading, RoadStairRuns.Width, StairLayout.PieceRun))
                Assert.False(ring.KeptOut(p), $"start {s.Heading} at {s.Arc}: {p} kept out");
        }
        // None sets off straight back along the road into the lane.
        Assert.DoesNotContain(starts, s => Mathf.Abs(RoadEndRing.Turn(s.Heading, 0f)) > 157f && Mathf.Abs(s.Arc) > 90f);
        // Through: none straight on.
        Assert.DoesNotContain(new RoadEndRing(new Vector2(0f, 0f), 0f, 10f, through: true).Starts(RoadStairRuns.Width), s => s.Heading == 0f && s.Arc == 0f);
    }

    /// <summary>
    /// Read in game: an approach's foot at its road's end, set off from the ring at arc -135 (3 degrees short of the cut arc)
    /// on 315, the road arriving on 43: 88 degrees off the road, 47 off square to the circle, within MostSkew. Every walked
    /// piece kept out of the lane, but its foot stood behind the road's end and was walked off straight across the road, onto
    /// the bank on its other side. The ring takes no such start, in its starts, as a start it is asked about, nor laid; it
    /// still takes the same heading beside the road's end and in front of it, and a start stepped off toward the end.
    /// </summary>
    [Fact]
    public void AFootBehindTheRoadsEndIsNeverWalkedOffAcrossTheRoad()
    {
        var ring = new RoadEndRing(new Vector2(-795.0f, 7460.4f), 43f, 136.34f);
        Vector2 at = ring.ArcPoint(-135f);
        Assert.False(ring.ArcCut(-135f));
        Assert.True(ring.WalksOffAcross(at, 135f));
        Assert.False(ring.Allows(at, 315f, RoadStairRuns.Width));
        Assert.DoesNotContain(ring.Starts(RoadStairRuns.Width), s => Mathf.Abs(s.Arc + 135f) < 0.1f && s.Heading == 315f);
        StairPlan plan = Flight(at, 315f, 136.34f);
        plan.Ring = ring;
        plan.RingStart = at;
        Assert.True(ring.Intrudes(plan, out string piece));
        Assert.Contains("walked off", piece);
        // Kept: on the same heading beside the road's end and in front of it; behind it, stepped off toward the end.
        Assert.True(ring.Allows(ring.ArcPoint(-90f), 315f, RoadStairRuns.Width));
        Assert.True(ring.Allows(ring.ArcPoint(-45f), 315f, RoadStairRuns.Width));
        Assert.False(ring.WalksOffAcross(at, 43f + 67.5f));
        // Every start the ring takes steps off onto the road where it ends.
        foreach (RoadEndRing.Start s in ring.Starts(RoadStairRuns.Width))
            Assert.False(ring.WalksOffAcross(s.At, s.Heading + 180f), $"start {s.Heading} at {s.Arc}");
    }

    // ---- Laid flights ----

    /// <summary>Flat to 1.5 m past the road's end, then a face rising north at a flight's grade.</summary>
    private static float North(float x, float z) => 0.5f * Mathf.Max(0f, z - 1.5f);

    /// <summary>A dead end with its face straight ahead: the flight sets off straight on from the front of the ring, and keeps
    /// out of it.</summary>
    [Fact]
    public void AFlightFromADeadEndSetsOffStraightOnFromTheFrontOfTheRing()
    {
        var ring = new RoadEndRing(new Vector2(0f, 0f), 0f, 0f);
        StairPlan plan = StairLayout.Build(new Vector2(0f, 0f), new Vector2(0f, 24f), North, sunkFoot: true, facing: 0f, ring: ring);
        Assert.True(plan.Reached, plan.Reason);
        Assert.True(plan.RingStart is Vector2 start && Vector2.Distance(start, new Vector2(0f, 1.5f)) < 0.01f, $"set off from {plan.RingStart}");
        Assert.Equal(0f, plan.StartHeading);
        Assert.False(ring.Intrudes(plan, out string piece), piece);
        // Its road's end is the ring's centre; lowered two stairs, the road's end moves back with its foot.
        Assert.Equal(new Vector2(0f, 0f), RoadStairRuns.RoadEndAt(plan, plan.RingStart!.Value));
        Assert.Equal(new Vector2(0f, -4f), RoadStairRuns.RoadEndAt(plan, new Vector2(0f, -2.5f)));
    }

    /// <summary>The same face on a through road (a spur foot on a road that goes on past it): the lane is kept both ways, so
    /// the flight turns off and sets off from the side of the ring, keeping out of the lane on along the road.</summary>
    [Fact]
    public void AFlightOffAThroughRoadTurnsOffItAndKeepsTheLaneClearBothWays()
    {
        var ring = new RoadEndRing(new Vector2(0f, 0f), 0f, 0f, through: true);
        StairPlan plan = StairLayout.Build(new Vector2(0f, 0f), new Vector2(0f, 24f), North, sunkFoot: true, facing: 0f, ring: ring);
        Assert.True(plan.Reached, plan.Reason);
        Assert.True(plan.RingStart is Vector2 start && Mathf.Abs(start.x) > 1f, $"set off from {plan.RingStart} on {plan.StartHeading}");
        Assert.True(Mathf.Abs(RoadEndRing.Turn(plan.StartHeading, 0f)) >= 45f, $"set off on {plan.StartHeading}");
        Assert.False(ring.Intrudes(plan, out string piece), piece);
    }

    /// <summary>A flight going down from a road's end (an approach to a location below): its top arrives on the ring, its exit
    /// edge on the circle, and nothing of it stands inside the ring or the lane. The road arrives heading north at 12 m; the
    /// ground falls away north of a floor's length past the ring at a flight's grade.</summary>
    private static float Down(float x, float z) => 12f - 0.5f * Mathf.Max(0f, z - 3.5f);

    private static StairPlan DownFrom(float footZ, RoadEndRing ring) =>
        StairLayout.Build(new Vector2(0f, 0f), new Vector2(0f, footZ), Down, topLandings: 1, sunkFoot: true, ring: ring);

    [Fact]
    public void AFlightDescendingFromARoadsEndArrivesOnItsRing()
    {
        var ring = new RoadEndRing(new Vector2(0f, 0f), 0f, 12f);
        // From a foot whose straight flight's top floor ends on the circle (23.5 m out: whole pieces back to 1.5 m).
        StairPlan plan = DownFrom(23.5f, ring);
        Assert.True(plan.Reached, plan.Reason);
        Assert.True(plan.Reversed);
        var exit = new Vector2(plan.End.x, plan.End.z);
        Assert.True(ring.Allows(exit, StairLayout.Wrap(plan.EndHeading + 180f), StairLayout.PieceRun), $"top's exit at {exit} on {plan.EndHeading}");
        Assert.Null(plan.RingStart);
        Assert.False(ring.Intrudes(plan, out string piece), piece);
        // Straight on: it goes down the way the road arrives.
        Assert.True(Mathf.Abs(RoadEndRing.Turn(StairLayout.Wrap(plan.EndHeading + 180f), 0f)) <= 22.51f, $"goes down on {plan.EndHeading + 180f}");
    }

    /// <summary>Changed (decision 6j, its precise fallback): an arrival on the ring is the start's own target
    /// (RoadEndRing.Allows), not a band round the circle. From a foot 24 m out the straight flight's top floor ends half a
    /// metre past the circle, which the band took (its exit 2 m from the road's end); now the top arrives only where a start
    /// would set off, on the circle, or the flight is not laid.</summary>
    [Fact]
    public void AnArrivalOffTheCircleIsNotTakenWhereAStartWouldNotBe()
    {
        var ring = new RoadEndRing(new Vector2(0f, 0f), 0f, 12f);
        StairPlan plan = DownFrom(24f, ring);
        var exit = new Vector2(plan.End.x, plan.End.z);
        Assert.True(!plan.Reached || ring.Allows(exit, StairLayout.Wrap(plan.EndHeading + 180f), StairLayout.PieceRun),
            $"arrived {exit.magnitude:F2} m from the road's end (the circle {ring.Radius:F2}) on {plan.EndHeading}");
        // The start's own test, at the exit the band took: refused there.
        Assert.False(ring.Allows(new Vector2(0f, 2f), 0f, StairLayout.PieceRun));
    }

    /// <summary>Where nothing fits from a road's end (every start hemmed in), none is laid there; from a ring further back
    /// along the road (the road cut back, RoadNetworkGenerator.EntryCutBacks) one is.</summary>
    [Fact]
    public void WhereNothingFitsFromARoadsEndNoneIsLaidAndFromFurtherBackOneIs()
    {
        // Rock round the road's end but straight back along it: no piece's middle within 3.5 m of it but on the road behind it.
        static bool Allowed(Vector2 p) => p.magnitude > 3.5f || p.y < -1.5f && Mathf.Abs(p.x) < 3f;
        float Ground(float x, float z) => 0.5f * Mathf.Max(0f, z + 13.5f);
        var goal = new Vector2(6f, 24f);
        var atEnd = new RoadEndRing(new Vector2(0f, 0f), 0f, Ground(0f, 0f));
        StairPlan none = StairLayout.Build(new Vector2(0f, 0f), goal, Ground, sunkFoot: true, allowed: Allowed, ring: atEnd);
        Assert.False(none.Reached);
        Vector2 back = new(0f, -12f);
        var cutBack = new RoadEndRing(back, 0f, Ground(back.x, back.y));
        StairPlan from = StairLayout.Build(back, goal, Ground, sunkFoot: true, allowed: Allowed, ring: cutBack);
        Assert.True(from.Reached, from.Reason);
        Assert.False(cutBack.Intrudes(from, out string piece), piece);
    }

    // ---- The crossing cases of the test it replaced ----

    /// <summary>A flight turned off the road's middle crosses it.</summary>
    [Fact]
    public void AFlightTurnedOffTheRoadsMiddleCrossesIt() =>
        Assert.True(new RoadEndRing(End, Travel, Height).Intrudes(Flight(End, 0f)));

    /// <summary>The same flight from the ring on the side it turns to does not (was: set beside the road, its edge on the
    /// road's edge).</summary>
    [Fact]
    public void TheSameFlightFromTheRingOnTheSideItTurnsToDoesNot()
    {
        var ring = new RoadEndRing(End, Travel, Height);
        float turn = RoadEndRing.Turn(0f, Travel);
        Assert.False(ring.Intrudes(Flight(ring.ArcPoint(turn), 0f), out string piece), piece);
    }

    /// <summary>Changed: a flight carrying on along the road from the road's middle stands inside the ring (the old rule let a
    /// straight-on flight start in the road's middle); from the ring's front it does not.</summary>
    [Fact]
    public void AFlightCarryingOnAlongTheRoadSetsOffFromTheRingsFront()
    {
        var ring = new RoadEndRing(End, Travel, Height);
        Assert.True(ring.Intrudes(Flight(End, Travel + 22.5f)));
        Assert.False(ring.Intrudes(Flight(ring.ArcPoint(0f), Travel), out string piece), piece);
        Assert.False(ring.Intrudes(Flight(ring.ArcPoint(22.5f), Travel + 22.5f), out piece), piece);
    }

    /// <summary>Changed: beside the road's end, setting off 22.5 degrees back toward the road's line, a flight meets that line
    /// metres past a dead end's end, where there is no road: it does not cross. On a through road, whose lane goes
    /// on past the end, it does.</summary>
    [Fact]
    public void AFlightBesideTheRoadHeadingInCrossesOnlyAThroughRoad()
    {
        Vector2 beside = End + StairLayout.Right(Travel) * (RoadNetworkGenerator.RoadWidth / 2f + RoadStairRuns.Width / 2f);
        StairPlan plan = Flight(beside, Travel - 22.5f);
        Assert.False(new RoadEndRing(End, Travel, Height).Intrudes(plan, out string piece), piece);
        Assert.True(new RoadEndRing(End, Travel, Height, through: true).Intrudes(plan));
    }

    /// <summary>Beside its road heading a few degrees in toward the road's line (a straight-on flight set beside a through
    /// road on a waypoint travel of 26.6, its stairs on 8.2): at a dead end it does not cross.</summary>
    [Fact]
    public void AFlightBesideItsRoadHeadingSlightlyInDoesNotCrossADeadEnd()
    {
        const float travel = 26.6f;
        Vector2 end = new(0f, 0f);
        Vector2 beside = end + StairLayout.Right(travel) * (RoadNetworkGenerator.RoadWidth / 2f + RoadStairRuns.Width / 2f);
        Assert.False(new RoadEndRing(end, travel, 36f).Intrudes(Flight(beside, 8.2f, 36f), out string piece), piece);
    }

    /// <summary>
    /// A road staircase is judged on the road as planned, where it really ends (six worlds, RoadSeedA -7999.9,-2358.6): laid
    /// down from a crest, its foot came down 2.4 m off its path point to -7999.9,-2357.6, where the road ends, arriving
    /// heading about 260. Its stairs stand a metre from the road's end, inside the ring. A road ending elsewhere meets neither
    /// end.
    /// </summary>
    [Fact]
    public void ARoadStaircaseIsJudgedOnTheRoadAsPlanned()
    {
        StairPlan plan = Pieces(true, (-7999.9f, 35f, -2358.6f, 180f), (-7999.9f, 36f, -2356.6f, 180f), (-7999.9f, 37f, -2354.6f, 180f));
        Vector2 footAt = new(-7999.9f, -2357.6f), top = new(-8026f, -2367.2f);
        var span = new RoadStairSpan { FromIndex = 67, ToIndex = 69, FromPoint = top, FromHeight = 58.5f, ToPoint = footAt, ToHeight = 36f, Plan = plan };
        Assert.Equal("foot", RoadStairRuns.SpanAcrossItsRoad(span, new[] { RoadTo(footAt, 260f, 36f, atStart: true) }));
        Assert.Null(RoadStairRuns.SpanAcrossItsRoad(span, new[] { RoadTo(new Vector2(-7990f, -2340f), 260f, 36f) }));
    }

    /// <summary>Changed: a piece whose middle stands on the road's line at its end is inside the ring, wherever it turns (the
    /// old rule judged which side of the line it stood); the step sunk under a foot, under the road's level on purpose, is not
    /// judged.</summary>
    [Fact]
    public void APieceAtTheRoadsEndIsInsideTheRingButTheSunkStepIsNot()
    {
        var ring = new RoadEndRing(new Vector2(0f, 0f), 90f, 36f);
        StairPlan plan = Pieces(false, (0f, 35f, 0.02f, 0f), (0f, 36f, -1.98f, 0f), (0f, 37f, -3.98f, 0f));
        Assert.True(ring.Intrudes(plan));
        StairPlan fromRing = Flight(ring.ArcPoint(90f), 180f);
        Assert.Equal(1, fromRing.SunkPieces);
        Assert.False(ring.Intrudes(fromRing, out string piece), piece);
    }

    /// <summary>A top beside its road's end on the left (the road arriving heading 270 at 50 m) whose flight descends back
    /// toward the road's line, 45 degrees to the right: its second stair's corner comes within 0.8 m of the road's end, inside
    /// the ring; judged as a road staircase's top on the road as planned, the same. Held a little further out, ahead of a dead
    /// end it meets nothing; on a through road it still crosses the lane on along the road.</summary>
    [Fact]
    public void ATopBesideItsRoadDescendingBackAcrossItCrossesIt()
    {
        Vector2 end = new(0f, 0f);
        Vector2 down = StairLayout.Forward(315f) * StairLayout.PieceRun;
        Vector2 first = new(0f, -2.2f), second = first + down;
        StairPlan plan = Pieces(false, (first.x, 49f, first.y, 315f), (second.x, 48f, second.y, 315f));
        Assert.True(new RoadEndRing(end, 270f, 50f).Intrudes(plan));
        var span = new RoadStairSpan { FromPoint = new Vector2(-10f, -12f), FromHeight = 40f, ToPoint = end, ToHeight = 50f, Plan = plan };
        Assert.Equal("top", RoadStairRuns.SpanAcrossItsRoad(span, new[] { RoadTo(end, 270f, 50f) }));
        Vector2 out1 = new(0f, -3.5f), out2 = out1 + down;
        StairPlan further = Pieces(false, (out1.x, 49f, out1.y, 315f), (out2.x, 48f, out2.y, 315f));
        Assert.False(new RoadEndRing(end, 270f, 50f).Intrudes(further, out string piece), piece);
        Assert.True(new RoadEndRing(end, 270f, 50f, through: true).Intrudes(further));
    }

    /// <summary>
    /// An approach's road is read as planned (six worlds, a MountainCave02 3489.3,-5760.3): the search's waypoints, a cell
    /// apart, had the road come into its end heading 89, where the road as planned came in at about 138. Its flight climbing
    /// 45 from the road's middle crosses its road by either reading now: the ring does not hang on a 45 degree split.
    /// </summary>
    [Fact]
    public void AnApproachsRoadIsReadAsPlanned()
    {
        Vector2 end = new(3489.3f, -5760.3f);
        var path = new List<Vector2> { end - StairLayout.Forward(89f) * 8f, end };
        var points = Enumerable.Range(0, 13).Select(k => end - StairLayout.Forward(138f) * (12 - k)).ToList();
        var piece = new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(_ => 119.1f), points.ConvertAll(_ => default(RoadPointDebugInfo)),
            RoadNetworkGenerator.RoadWidth, 12f, true);
        float planned = RoadStairRuns.ArrivingTravel(path, piece, end, false);
        Assert.InRange(planned, 137f, 139f);
        StairPlan plan = Pieces(false, (3488.6f, 118.1f, -5761.1f, 225f), (3490.1f, 119.1f, -5759.6f, 225f), (3491.5f, 120.1f, -5758.2f, 225f));
        Assert.True(new RoadEndRing(end, RoadStairRuns.ArrivingTravel(path, end, false), 119.1f).Intrudes(plan), "by the waypoints");
        Assert.True(new RoadEndRing(end, planned, 119.1f).Intrudes(plan), "by the road as planned");
        // Where the planned road does not reach the end (it ends on a staircase), the search's path.
        var short_ = new RoadSpatialGrid.PlannedPath(points.Take(10).ToList(), Enumerable.Repeat(119.1f, 10).ToList(),
            Enumerable.Repeat(default(RoadPointDebugInfo), 10).ToList(), RoadNetworkGenerator.RoadWidth, 9f, true);
        Assert.Equal(RoadStairRuns.ArrivingTravel(path, end, false), RoadStairRuns.ArrivingTravel(path, short_, end, false));
    }

    /// <summary>Changed: a top in its road's middle whose flight descends right back along the road (six worlds, RoadSeedB
    /// 332.1,3088.0: 174 degrees off) stands in the lane back along the road: it crosses (it was left to OverRoad).</summary>
    [Fact]
    public void AFlightRightBackAlongItsRoadStandsInTheLane()
    {
        Vector2 down = StairLayout.Forward(264f) * StairLayout.PieceRun;
        StairPlan plan = Pieces(false, (down.x * 0.5f, 49f, down.y * 0.5f + 0.3f, 264f), (down.x * 1.5f, 48f, down.y * 1.5f + 0.3f, 264f));
        Assert.True(new RoadEndRing(new Vector2(0f, 0f), 90f, 50f).Intrudes(plan));
    }
}
