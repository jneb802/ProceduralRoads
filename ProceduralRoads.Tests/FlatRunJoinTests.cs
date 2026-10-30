using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A flat run is a stair run that does not climb: where a run of floors and a stair meet, at 0, 22.5, 45, 67.5 or 90
/// degrees, they snap edge to edge as two stairs do (one corner shared, the wedge filled), laid by the one search.
/// </summary>
public sealed class FlatRunJoinTests
{
    /// <summary>One end of a walked piece: its edge's two corners, the way out across it and its walking height.</summary>
    private readonly record struct End(Vector2 A, Vector2 B, Vector2 Out, float Height, int Piece)
    {
        public Vector2 Centre => (A + B) * 0.5f;
    }

    private static IEnumerable<End> Ends(IReadOnlyList<StairPiece> pieces, StairKit kit)
    {
        for (int i = 0; i < pieces.Count; i++)
        {
            StairPiece p = pieces[i];
            if (p.Kind != StairPieceKind.Stair && p.Kind != StairPieceKind.Landing) continue;
            var c = new Vector2(p.Position.x, p.Position.z);
            bool stair = p.Kind == StairPieceKind.Stair;
            float heading = stair ? StairLayout.Wrap(p.YawDegrees + 180f) : p.YawDegrees;
            Vector2 f = StairLayout.Forward(heading), r = StairLayout.Right(heading);
            float low = stair ? p.Position.y : p.Position.y + kit.FloorTop, high = stair ? low + StairLayout.PieceRise : low;
            yield return new End(c - f - r, c - f + r, -f, low, i);
            yield return new End(c + f - r, c + f + r, f, high, i);
        }
    }

    /// <summary>Every pair of ends that meet: level, facing each other, within a corner's reach, neither inside the other piece
    /// (overlapping floors). Faulty where they share no corner, or not both where they run straight on: a gap or a sideways
    /// jog.</summary>
    private static List<(StairPiece a, StairPiece b, bool straight, bool faulty)> Joins(IReadOnlyList<StairPiece> pieces, StairKit kit)
    {
        const float corner = 0.05f;
        var ends = Ends(pieces, kit).ToList();
        var joins = new List<(StairPiece, StairPiece, bool, bool)>();
        for (int i = 0; i < ends.Count; i++)
            for (int j = i + 1; j < ends.Count; j++)
            {
                End e = ends[i], o = ends[j];
                if (e.Piece == o.Piece || Mathf.Abs(e.Height - o.Height) > 0.05f) continue;
                if (Vector2.Distance(e.Centre, o.Centre) > 1.5f || Vector2.Dot(e.Out, o.Out) > 0.05f) continue;
                // One end inside the other piece: floors set back over each other overlap, they do not join. Nor where either
                // end, or the ground between them, lies under a third floor level with them (a floor set back over both).
                if (Vector2.Dot(o.Centre - e.Centre, e.Out) < -0.05f) continue;
                if (Vector2.Distance(e.Centre, o.Centre) > 0.05f
                    && new[] { e.Centre, o.Centre, (e.Centre + o.Centre) * 0.5f }.Any(at => Covered(pieces, kit, at, e.Height, e.Piece, o.Piece))) continue;
                int shared = new[] { e.A, e.B }.Count(a => Vector2.Distance(a, o.A) < corner || Vector2.Distance(a, o.B) < corner);
                bool straight = Vector2.Dot(e.Out, o.Out) < -0.999f;
                joins.Add((pieces[e.Piece], pieces[o.Piece], straight, shared == 0 || (straight && shared < 2)));
            }
        return joins;
    }

    /// <summary>Whether a floor other than <paramref name="a"/> and <paramref name="b"/> covers <paramref name="at"/> (inside
    /// it, not on its edge), its walking surface there at <paramref name="height"/>.</summary>
    private static bool Covered(IReadOnlyList<StairPiece> pieces, StairKit kit, Vector2 at, float height, int a, int b)
    {
        for (int i = 0; i < pieces.Count; i++)
        {
            StairPiece p = pieces[i];
            if (i == a || i == b || p.Kind != StairPieceKind.Landing || Mathf.Abs(p.Position.y + kit.FloorTop - height) > 0.05f) continue;
            Vector2 d = at - new Vector2(p.Position.x, p.Position.z);
            if (Mathf.Abs(Vector2.Dot(d, StairLayout.Forward(p.YawDegrees))) < 0.95f && Mathf.Abs(Vector2.Dot(d, StairLayout.Right(p.YawDegrees))) < 0.95f) return true;
        }
        return false;
    }

    internal static List<string> JoinFaults(IReadOnlyList<StairPiece> pieces, StairKit kit) =>
        Joins(pieces, kit).Where(j => j.faulty).Select(j => FormattableString.Invariant(
            $"{j.a.Prefab} {j.a.Position} yaw {j.a.YawDegrees} meets {j.b.Prefab} {j.b.Position} yaw {j.b.YawDegrees}")).ToList();

    private static StairPiece Stair(StairKit kit, float x, float y, float z, float yaw) =>
        new() { Kind = StairPieceKind.Stair, Prefab = kit.Stair, Position = new Vector3(x, y, z), YawDegrees = yaw };

    /// <summary>A valley as laid in the Mistlands (-7408,-1944): the first flight down it northward on 0, the second up out of
    /// it on 337.5, a rise a step apart from the bottom. Spanned a rise up, its floors on 337.5 met the first's stair 22.5
    /// degrees off with no filler (0.38 m open at a corner) and its last floor stepped 0.84 m aside onto the second's.</summary>
    internal static (StairPlan below, StairPlan up) MistlandsValley()
    {
        StairKit kit = StairKit.BlackMarble;
        var below = new StairPlan { Kit = kit.Name, Reached = true, Reversed = true };
        below.Pieces.AddRange(new[] {
            Stair(kit, -7407.159f, 34.924f, -1953.230f, 0f), Stair(kit, -7407.159f, 35.924f, -1955.230f, 0f), Stair(kit, -7407.159f, 36.924f, -1957.230f, 0f) });
        var up = new StairPlan { Kit = kit.Name, Reached = true };
        up.Pieces.AddRange(new[] {
            Stair(kit, -7410.996f, 34.924f, -1947.166f, 157.5f), Stair(kit, -7411.761f, 35.924f, -1945.318f, 157.5f),
            Stair(kit, -7413.067f, 36.924f, -1943.362f, 135f), Stair(kit, -7414.374f, 37.924f, -1941.407f, 157.5f) });
        return (below, up);
    }

    /// <summary>Flat at 33 between the two flights' feet, rising half a metre a metre away from it either way.</summary>
    internal static float MistlandsValleyGround(float x, float z) => 33f + 0.5f * Mathf.Max(0f, Mathf.Max(-1953f - z, z + 1947f));

    /// <summary>The second flight of a valley laid again from the first's row, up to <paramref name="top"/>, as
    /// RoadStairRuns lays it.</summary>
    internal static Func<StairContinuation, StairPlan> Relay(Func<float, float, float> ground, Vector2 top, StairKit kit) =>
        row => StairLayout.Build(row.Edge, top, ground, 2f, kit, after: row, relayPads: false);

    [Fact]
    public void AValleySpanMeetsBothFlightsEdgeToEdge()
    {
        StairKit kit = StairKit.BlackMarble;
        var (below, up) = MistlandsValley();
        Assert.True(RoadStairRuns.SpanTheValley(below, up, kit, MistlandsValleyGround, 33.924f, 2f, Relay(MistlandsValleyGround, new Vector2(-7418f, -1932f), kit),
            (_, _) => true, out StairPlan spanned, out _));
        var all = below.Pieces.Concat(spanned.Pieces).ToList();
        Assert.Contains(all, p => p.Kind == StairPieceKind.Landing);
        Assert.Empty(JoinFaults(all, kit));
    }

    public static IEnumerable<object[]> Turns() =>
        from steps in Enumerable.Range(-StairLayout.MaxJoinTurnSteps, 2 * StairLayout.MaxJoinTurnSteps + 1)
        from floorFirst in new[] { true, false }
        select new object[] { steps, floorFirst };

    /// <summary>Two floors then two stairs turned off them, or a flight that comes onto a landing, straight on, and leaves
    /// it turned: at every heading change a join may take, edge to edge, the wedge filled level with the join.</summary>
    [Theory]
    [MemberData(nameof(Turns))]
    public void AFlatRunAndAStairJoinEdgeToEdgeAtEveryAllowedTurn(int steps, bool floorFirst)
    {
        StairKit kit = StairKit.BlackMarble;
        float turned = StairLayout.Wrap(steps * StairLayout.HeadingStep);
        StairPieceKind floor = StairPieceKind.Landing, stair = StairPieceKind.Stair;
        StairPlan plan = StairLayout.LayFlight(new Vector2(0f, 0f), 5f, new[] { 0f, 0f, turned, turned }, kit, (x, z) => 0f, 0,
            floorFirst ? new[] { floor, floor, stair, stair } : new[] { stair, floor, stair, stair });
        Assert.Empty(JoinFaults(plan.Pieces, kit));
        var fillers = plan.Pieces.Where(p => p.Kind == StairPieceKind.Filler).ToList();
        float join = 5f + (floorFirst ? 0f : StairLayout.PieceRise);
        if (steps == 0) { Assert.Empty(fillers); return; }
        Assert.NotEmpty(fillers);
        Assert.All(fillers, f => Assert.Equal(join, f.Position.y + kit.FloorTop, 3));
    }

    /// <summary>A staircase's last row, where it meets a road or another flight, is a floor turned off the stair below it:
    /// at every heading change a join may take, edge to edge, the wedge filled level with the top stair.</summary>
    [Theory]
    [MemberData(nameof(LastRowTurns))]
    public void ALastRowFloorJoinsTheStairBelowEdgeToEdgeAtEveryAllowedTurn(int steps)
    {
        StairKit kit = StairKit.BlackMarble;
        float turned = StairLayout.Wrap(steps * StairLayout.HeadingStep);
        StairPlan plan = StairLayout.LayFlight(new Vector2(0f, 0f), 5f, new[] { 0f, 0f, turned }, kit, (x, z) => 0f, 0,
            new[] { StairPieceKind.Stair, StairPieceKind.Stair, StairPieceKind.Landing });
        Assert.Empty(JoinFaults(plan.Pieces, kit));
        Assert.Contains(Joins(plan.Pieces, kit), j => j.a.Kind != j.b.Kind && (steps == 0) == j.straight);
        var fillers = plan.Pieces.Where(p => p.Kind == StairPieceKind.Filler).ToList();
        if (steps == 0) { Assert.Empty(fillers); return; }
        Assert.NotEmpty(fillers);
        Assert.All(fillers, f => Assert.Equal(7f, f.Position.y + kit.FloorTop, 3));
    }

    public static IEnumerable<object[]> LastRowTurns() =>
        Enumerable.Range(-StairLayout.MaxJoinTurnSteps, 2 * StairLayout.MaxJoinTurnSteps + 1).Select(s => new object[] { s });

    /// <summary>The search lays a floor turned off the stair below it only as the staircase's last row: up a 1:2 slope
    /// north to a level top, the road leaving it south-east (135), the top floor turned off the top stair; nowhere else down the
    /// flight.</summary>
    [Fact]
    public void TheSearchTurnsAFloorOffAStairOnlyAsItsLastRow()
    {
        StairKit kit = StairKit.BlackMarble;
        static float Ground(float x, float z) => 0.5f * Mathf.Clamp(z, 0f, 8f);
        StairPlan plan = StairLayout.Build(new Vector2(0f, 0f), new Vector2(3f, 9f), Ground, kit: kit, arriving: 0f, leaving: 135f);
        Assert.True(plan.Reached, plan.Reason);
        Assert.Empty(JoinFaults(plan.Pieces, kit));
        var walked = plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair || p.Kind == StairPieceKind.Landing).ToList();
        var turnedOff = Joins(plan.Pieces, kit).Where(j => !j.straight && j.a.Kind != j.b.Kind
            && (j.a.Kind == StairPieceKind.Landing ? j.a : j.b) is var floor && floor.Position.y + kit.FloorTop > (j.a.Kind == StairPieceKind.Stair ? j.a : j.b).Position.y + 0.5f).ToList();
        Assert.True(turnedOff.Count > 0, string.Join(" ", walked.Select(p => $"{p.Kind}({p.Position.x:F1},{p.Position.y:F1},{p.Position.z:F1})y{p.YawDegrees}")));
        Assert.All(turnedOff, j => Assert.Same(walked[walked.Count - 1], j.a.Kind == StairPieceKind.Landing ? j.a : j.b));
    }

    /// <summary>Two flights meeting top to top over a ridge, level: the first up north from 0,0 to its top edge at 0,6, the
    /// second up to its top edge <paramref name="gap"/> on and <paramref name="aside"/> to the east, climbing on
    /// <paramref name="heading"/>; the gap laid as a flat run (StairLayout.JoinTops) meets both tops edge to edge, with no gap
    /// and no jog, its wedges filled.</summary>
    [Theory]
    [InlineData(0f, 4f, 180f)]
    [InlineData(0.6f, 4f, 180f)]
    [InlineData(1.0f, 4f, 180f)]
    [InlineData(-0.6f, 5f, 180f)]
    [InlineData(0f, 4f, 202.5f)]
    [InlineData(1.0f, 4f, 202.5f)]
    public void TwoTopsAreJoinedByAFlatRunEdgeToEdge(float aside, float gap, float heading)
    {
        StairKit kit = StairKit.BlackMarble;
        static float Ground(float x, float z) => 41f;
        StairPlan first = StairLayout.LayFlight(new Vector2(0f, 0f), 42f, new[] { 0f, 0f, 0f }, kit, Ground, 0);
        Vector2 top = new(aside, 6f + gap);
        StairPlan second = StairLayout.LayFlight(top - StairLayout.Forward(heading) * 6f, 42f, new[] { heading, heading, heading }, kit, Ground, 0);
        Assert.Equal(top.x, second.End.x, 3);
        Assert.Equal(top.y, second.End.z, 3);
        var from = new StairContinuation(new Vector2(0f, 6f), 0f, 45f, StairPieceKind.Stair);
        var to = new StairContinuation(top, heading, 45f, StairPieceKind.Stair);
        int before = second.Pieces.Count;
        TopJoin laid = StairLayout.JoinTops(second, from, to, Ground, 2f, kit, RoadStairRuns.CrestStubJoinReach);
        Assert.True(laid.Laid, laid.Why);
        var run = second.Pieces.Skip(before).ToList();
        Assert.All(run.Where(p => p.Kind == StairPieceKind.Landing || p.Kind == StairPieceKind.Filler), p => Assert.Equal(45f, p.Position.y + kit.FloorTop, 3));
        var all = first.Pieces.Concat(second.Pieces).ToList();
        Assert.Empty(JoinFaults(all, kit));
        // Both tops met by the run's floors: the first's top stair and the second's each join one.
        StairPiece topOf(StairPlan plan) => plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair).OrderBy(p => p.Position.y).Last();
        var joins = Joins(all, kit);
        foreach (StairPiece t in new[] { topOf(first), topOf(second) })
            Assert.Contains(joins, j => (j.a == t || j.b == t) && (j.a.Kind == StairPieceKind.Landing || j.b.Kind == StairPieceKind.Landing));
        // Into the second's top head-on, straight.
        Assert.Contains(joins, j => (j.a == topOf(second) || j.b == topOf(second)) && j.straight);
    }

    /// <summary>Carried floors between two tops are a short walkway, not a bridge: refused where a floor would stand higher
    /// over the ground than the walkway cap, over water, or past the reach; laid within all three.</summary>
    [Theory]
    [InlineData(4f, 4f, true, "")]
    [InlineData(6f, 4f, false, "walkway cap")]
    [InlineData(25f, 4f, false, "water")]
    [InlineData(4f, 12f, false, "longer than")]
    public void TwoTopsAreJoinedOnlyByAShortWalkway(float under, float gap, bool laid, string why)
    {
        StairKit kit = StairKit.BlackMarble;
        // Tops at 45, the ground between them (6 to 6 + gap north) under them by `under`.
        float Ground(float x, float z) => z > 6.5f && z < 5.5f + gap ? 45f - under : 41f;
        var plan = new StairPlan { Kit = kit.Name, Reached = true };
        var from = new StairContinuation(new Vector2(0f, 6f), 0f, 45f, StairPieceKind.Stair);
        var to = new StairContinuation(new Vector2(0f, 6f + gap), 180f, 45f, StairPieceKind.Stair);
        TopJoin join = StairLayout.JoinTops(plan, from, to, Ground, 2f, kit, RoadStairRuns.CrestStubJoinReach);
        Assert.Equal(laid, join.Laid);
        Assert.Equal(laid, plan.Pieces.Count > 0);
        Assert.Contains(why, join.Why);
        Assert.Equal(gap, join.Length, 2);
    }

    /// <summary>Two tops a step apart in height (refused as a flat run, 30 of 48 top-to-top joins over six worlds lost their
    /// pair of staircases) are joined by a run that climbs to the higher on straight stairs at its end, on the other's line:
    /// floors then a stair, or in line two metres apart the stair straight on from the lower top. Edge to edge, both ways
    /// asked (the caller asks from either top); from the higher it is refused.</summary>
    [Theory]
    [InlineData(0f, 4f, 180f, 1)]
    [InlineData(1f, 6f, 180f, 1)]
    [InlineData(0f, 7f, 202.5f, 1)]
    [InlineData(0f, 2f, 180f, 1)]
    [InlineData(0f, 6f, 180f, 2)]
    public void TopsAStepApartAreJoinedByARunUpToTheHigher(float aside, float gap, float heading, int rises)
    {
        StairKit kit = StairKit.BlackMarble;
        static float Ground(float x, float z) => 41f;
        StairPlan first = StairLayout.LayFlight(new Vector2(0f, 0f), 42f, new[] { 0f, 0f, 0f }, kit, Ground, 0);
        Vector2 top = new(aside, 6f + gap);
        float high = 45f + rises;
        StairPlan second = StairLayout.LayFlight(top - StairLayout.Forward(heading) * 6f, high - 3f, new[] { heading, heading, heading }, kit, Ground, 0);
        var from = new StairContinuation(new Vector2(0f, 6f), 0f, 45f, StairPieceKind.Stair);
        var to = new StairContinuation(top, heading, high, StairPieceKind.Stair);
        Assert.False(StairLayout.JoinTops(second, to, from, Ground, 2f, kit, RoadStairRuns.CrestStubJoinReach, StairLayout.StepUp).Laid);
        int before = second.Pieces.Count;
        TopJoin laid = StairLayout.JoinTops(second, from, to, Ground, 2f, kit, RoadStairRuns.CrestStubJoinReach, StairLayout.StepUp);
        Assert.True(laid.Laid, laid.Why);
        var run = second.Pieces.Skip(before).ToList();
        Assert.Equal(rises, run.Count(p => p.Kind == StairPieceKind.Stair));
        Assert.All(run.Where(p => p.Kind == StairPieceKind.Landing), p => Assert.Equal(45f, p.Position.y + kit.FloorTop, 3));
        var all = first.Pieces.Concat(second.Pieces).ToList();
        var faults = JoinFaults(all, kit);
        Assert.True(faults.Count == 0, string.Join("\n", faults));
        // The run's top stair meets the second's top stair at its edge, back to back.
        StairPiece runTop = run.Where(p => p.Kind == StairPieceKind.Stair).OrderBy(p => p.Position.y).Last();
        Vector2 edge = new Vector2(runTop.Position.x, runTop.Position.z) + StairLayout.Forward(StairLayout.Wrap(runTop.YawDegrees + 180f)) * (StairLayout.PieceRun / 2f);
        Assert.True(Vector2.Distance(edge, top) < 0.02f, $"run tops out at {edge}, the other's edge {top}");
        Assert.Equal(high, runTop.Position.y + StairLayout.PieceRise, 3);
    }

    /// <summary>More rises apart than a short flight: refused, as before.</summary>
    [Fact]
    public void TopsFourRisesApartAreNotJoined()
    {
        var plan = new StairPlan { Kit = StairKit.BlackMarble.Name, Reached = true };
        TopJoin join = StairLayout.JoinTops(plan, new StairContinuation(new Vector2(0f, 6f), 0f, 45f, StairPieceKind.Stair),
            new StairContinuation(new Vector2(0f, 16f), 180f, 49f, StairPieceKind.Stair), (x, z) => 44f, 2f, StairKit.BlackMarble, RoadStairRuns.CrestStubJoinReach, StairLayout.StepUp);
        Assert.False(join.Laid);
        Assert.Contains("4 rises apart", join.Why);
        Assert.Empty(plan.Pieces);
    }

    /// <summary>A top floor less than a floor short of the other's top edge, straight ahead and level (no flat run fitted:
    /// a floor on from it reached into the other's top): one floor set back over the top floor, ending on the edge.</summary>
    [Fact]
    public void ATopFloorJustShortOfTheOtherIsJoinedByAFloorSetBackOverIt()
    {
        StairKit kit = StairKit.BlackMarble;
        static float Ground(float x, float z) => 41f;
        StairPlan first = StairLayout.LayFlight(new Vector2(0f, 0f), 42f, new[] { 0f, 0f, 0f, 0f }, kit, Ground, 0,
            new[] { StairPieceKind.Stair, StairPieceKind.Stair, StairPieceKind.Stair, StairPieceKind.Landing });
        Vector2 top = new(0f, 9f);
        StairPlan second = StairLayout.LayFlight(top - StairLayout.Forward(180f) * 6f, 42f, new[] { 180f, 180f, 180f }, kit, Ground, 0);
        var from = new StairContinuation(new Vector2(0f, 8f), 0f, 45f, StairPieceKind.Landing);
        var to = new StairContinuation(top, 180f, 45f, StairPieceKind.Stair);
        int before = second.Pieces.Count;
        TopJoin laid = StairLayout.JoinTops(second, from, to, Ground, 2f, kit, RoadStairRuns.CrestStubJoinReach, StairLayout.StepUp);
        Assert.True(laid.Laid, laid.Why);
        StairPiece floor = Assert.Single(second.Pieces.Skip(before), p => p.Kind == StairPieceKind.Landing);
        Assert.True(Vector2.Distance(new Vector2(floor.Position.x, floor.Position.z), new Vector2(0f, 8f)) < 0.01f, $"floor at {floor.Position}");
        Assert.Empty(JoinFaults(first.Pieces.Concat(second.Pieces).ToList(), kit));
    }

    /// <summary>A staircase climbing on <paramref name="heading"/> to its top edge <paramref name="top"/> at
    /// <paramref name="height"/>: three stairs, then a floor where <paramref name="floor"/>.</summary>
    private static StairPlan TopAt(Vector2 top, float heading, float height, bool floor, StairKit kit, Func<float, float, float> ground)
    {
        int n = floor ? 4 : 3;
        var headings = Enumerable.Repeat(heading, n).ToArray();
        var kinds = Enumerable.Repeat(StairPieceKind.Stair, 3).Concat(floor ? new[] { StairPieceKind.Landing } : Array.Empty<StairPieceKind>()).ToArray();
        return StairLayout.LayFlight(top - StairLayout.Forward(heading) * (n * StairLayout.PieceRun), height - 3f, headings, kit, ground, 0, kinds);
    }

    /// <summary>Two tops met as the ridge meet meets them: a snapped run from either, else a clipped one from either.</summary>
    private static TopJoin Meet(StairPlan plan, StairContinuation a, StairContinuation b, Func<float, float, float> ground, StairKit kit)
    {
        TopJoin join = default;
        foreach (bool clip in new[] { false, true })
            foreach (var (from, to) in new[] { (a, b), (b, a) })
                if ((join = StairLayout.JoinTops(plan, from, to, ground, 2f, kit, RoadStairRuns.CrestStubJoinReach, StairLayout.StepUp, clip)).Laid)
                    return join;
        return join;
    }

    /// <summary>Whether a piece's footprint holds <paramref name="at"/> (within <paramref name="pad"/> of its sides), and its
    /// walking surface there: a floor level, a stair rising from its foot to its top edge.</summary>
    private static bool Surface(StairPiece p, StairKit kit, Vector2 at, float pad, out float height)
    {
        Vector2 d = at - new Vector2(p.Position.x, p.Position.z);
        float along = Vector2.Dot(d, StairLayout.Forward(p.YawDegrees)), aside = Vector2.Dot(d, StairLayout.Right(p.YawDegrees));
        float h = StairLayout.PieceRun / 2f + pad;
        // A stair faces down its flight: its top edge behind it.
        height = p.Kind == StairPieceKind.Stair ? p.Position.y + StairLayout.PieceRise * (1f - along / (StairLayout.PieceRun / 2f)) / 2f : p.Position.y + kit.FloorTop;
        return Mathf.Abs(along) <= h && Mathf.Abs(aside) <= h;
    }

    /// <summary>
    /// Whether two tops' join can be walked: no floor stands on or over a stair's tread (a floor inside its footprint at or
    /// above its surface), and the walk from <paramref name="start"/> through the run's pieces (nearest first) to
    /// <paramref name="end"/> stays on a stair or floor (or by a turn's filler) with no step taller than a rise.
    /// </summary>
    internal static List<string> WalkFaults(IReadOnlyList<StairPiece> pieces, IReadOnlyList<StairPiece> run, StairKit kit, Vector2 start, Vector2 end, float height)
    {
        var faults = new List<string>();
        var walked = pieces.Where(p => p.Kind == StairPieceKind.Stair || p.Kind == StairPieceKind.Landing).ToList();
        foreach (StairPiece s in walked.Where(p => p.Kind == StairPieceKind.Stair))
            for (float u = -0.9f; u <= 0.91f; u += 0.3f)
                for (float v = -0.9f; v <= 0.91f; v += 0.3f)
                {
                    Vector2 at = new Vector2(s.Position.x, s.Position.z) + StairLayout.Forward(s.YawDegrees) * u + StairLayout.Right(s.YawDegrees) * v;
                    Surface(s, kit, at, 0f, out float tread);
                    foreach (StairPiece f in walked.Where(p => p.Kind == StairPieceKind.Landing))
                        if (Surface(f, kit, at, -0.05f, out float top) && top > tread - 0.05f)
                            faults.Add(FormattableString.Invariant($"floor {f.Position} over the stair {s.Position} at {at}"));
                }
        var path = new List<Vector2> { start };
        var left = run.Where(p => p.Kind == StairPieceKind.Stair || p.Kind == StairPieceKind.Landing).Select(p => new Vector2(p.Position.x, p.Position.z)).ToList();
        while (left.Count > 0)
        {
            Vector2 next = left.OrderBy(c => Vector2.Distance(c, path[path.Count - 1])).First();
            left.Remove(next);
            path.Add(next);
        }
        path.Add(end);
        float at0 = height;
        for (int i = 1; i < path.Count; i++)
        {
            float len = Vector2.Distance(path[i - 1], path[i]);
            for (float t = 0f; t <= len; t += 0.1f)
            {
                Vector2 at = Vector2.Lerp(path[i - 1], path[i], len < 0.001f ? 0f : t / len);
                var under = walked.Select(p => Surface(p, kit, at, 0.02f, out float h) ? h : float.NaN).Where(h => !float.IsNaN(h)).ToList();
                if (under.Count == 0)
                {
                    if (!pieces.Any(p => p.Kind == StairPieceKind.Filler && Vector2.Distance(new Vector2(p.Position.x, p.Position.z), at) < 1.5f))
                        faults.Add(FormattableString.Invariant($"nothing to walk on at {at}"));
                    continue;
                }
                float step = under.OrderBy(h => Mathf.Abs(h - at0)).First();
                if (Mathf.Abs(step - at0) > StairLayout.PieceRise + 0.01f)
                    faults.Add(FormattableString.Invariant($"a step of {step - at0:F2} m at {at}"));
                at0 = step;
            }
        }
        return faults;
    }

    /// <summary>Two tops no snapped run joins, the pair otherwise lost (50 of 51 top-to-top joins over six worlds were refused):
    /// off the rise (half a rise, a rise and a half, 0.6 of one between two top stairs), closer than a floor and out of line,
    /// or the other top lower. Joined by snapping to one top and clipping the other: the end floor over the other's top floor
    /// (or on its top stair's edge), at its level or up to half a rise over it. Walkable: no tread covered, no step taller than
    /// a rise.</summary>
    [Theory]
    [InlineData(0f, 4f, 0.5f, true, true, 0)]
    [InlineData(0f, 6f, 1.5f, true, true, 1)]
    [InlineData(0f, 4f, 0.6f, false, false, 1)]
    [InlineData(0.6f, 1f, 0f, true, true, 0)]
    [InlineData(0.5f, 6f, -1.4f, true, true, 1)]
    [InlineData(0f, 4f, -0.4f, false, true, 0)]
    public void TopsNoSnappedRunJoinsAreJoinedByClippingOne(float aside, float gap, float rise, bool firstFloor, bool secondFloor, int stairs)
    {
        StairKit kit = StairKit.BlackMarble;
        static float Ground(float x, float z) => 41f;
        float firstEnd = firstFloor ? 8f : 6f;
        StairPlan first = TopAt(new Vector2(0f, firstEnd), 0f, 45f, firstFloor, kit, Ground);
        Vector2 top = new(aside, firstEnd + gap);
        StairPlan second = TopAt(top, 180f, 45f + rise, secondFloor, kit, Ground);
        var a = new StairContinuation(new Vector2(0f, firstEnd), 0f, 45f, firstFloor ? StairPieceKind.Landing : StairPieceKind.Stair);
        var b = new StairContinuation(top, 180f, 45f + rise, secondFloor ? StairPieceKind.Landing : StairPieceKind.Stair);
        Assert.False(StairLayout.JoinTops(second, a, b, Ground, 2f, kit, RoadStairRuns.CrestStubJoinReach, StairLayout.StepUp).Laid);
        Assert.False(StairLayout.JoinTops(second, b, a, Ground, 2f, kit, RoadStairRuns.CrestStubJoinReach, StairLayout.StepUp).Laid);
        int before = second.Pieces.Count;
        TopJoin laid = Meet(second, a, b, Ground, kit);
        Assert.True(laid.Laid, laid.Why);
        var run = second.Pieces.Skip(before).ToList();
        Assert.Equal(stairs, run.Count(p => p.Kind == StairPieceKind.Stair));
        var all = first.Pieces.Concat(second.Pieces).ToList();
        // From the middle of the first's top row to the middle of the second's.
        Vector2 Row(StairContinuation t) => t.Edge - StairLayout.Forward(t.Heading) * (StairLayout.PieceRun / 2f);
        var faults = WalkFaults(all, run, kit, Row(a), Row(b), 45f - (firstFloor ? 0f : 0.5f));
        Assert.True(faults.Count == 0, string.Join("\n", faults.Take(10)));
    }

    /// <summary>The walk check fails a floor laid over a stair's treads, and a walk that steps more than a rise.</summary>
    [Fact]
    public void TheWalkCheckFailsACoveredTreadAndATallStep()
    {
        StairKit kit = StairKit.BlackMarble;
        static float Ground(float x, float z) => 41f;
        StairPlan flight = TopAt(new Vector2(0f, 6f), 0f, 45f, false, kit, Ground);
        StairPlan over = TopAt(new Vector2(0f, 10f), 0f, 45f, true, kit, Ground);
        StairPiece floor = over.Pieces.Last(p => p.Kind == StairPieceKind.Landing);
        // The floor set back two metres, over the flight's top stair.
        floor.Position = new Vector3(floor.Position.x, 44.6f - kit.FloorTop, 5f);
        var pieces = flight.Pieces.Append(floor).ToList();
        Assert.Contains(WalkFaults(pieces, new[] { floor }, kit, new Vector2(0f, 1f), new Vector2(0f, 5f), 42.5f), f => f.Contains("over the stair"));
        floor.Position = new Vector3(0f, 47f - kit.FloorTop, 7f);
        Assert.Contains(WalkFaults(pieces, new[] { floor }, kit, new Vector2(0f, 5f), new Vector2(0f, 7f), 44.5f), f => f.Contains("a step of"));
    }

    /// <summary>What a clip never joins: tops four rises apart, and two tops side by side climbing the same way (a join would
    /// double back alongside both).</summary>
    [Theory]
    [InlineData(0f, 10f, 180f, 4f)]
    [InlineData(3f, 0f, 0f, 0f)]
    public void TopsAClipCannotJoinAreRefused(float aside, float gap, float heading, float rise)
    {
        StairKit kit = StairKit.BlackMarble;
        static float Ground(float x, float z) => 41f;
        var plan = new StairPlan { Kit = kit.Name, Reached = true };
        TopJoin join = Meet(plan, new StairContinuation(new Vector2(0f, 8f), 0f, 45f, StairPieceKind.Landing),
            new StairContinuation(new Vector2(aside, 8f + gap), heading, 45f + rise, StairPieceKind.Landing), Ground, kit);
        Assert.False(join.Laid);
        Assert.Empty(plan.Pieces);
    }

    /// <summary>The search turns a flight off a flat run with the one owner's rule: edge to edge.</summary>
    [Fact]
    public void TheSearchTurnsAFlightOffAFlatRunEdgeToEdge()
    {
        // Level ground north from the foot, a 1:2 slope rising east beside it: floors north, then a flight turned off them.
        static float Ground(float x, float z) => 0.5f * Mathf.Max(0f, x - 2f);
        StairPlan plan = StairLayout.Build(new Vector2(0f, 0f), new Vector2(12f, 6f), Ground, kit: StairKit.BlackMarble, arriving: 0f);
        Assert.True(plan.Reached, plan.Reason);
        Assert.Empty(JoinFaults(plan.Pieces, StairKit.BlackMarble));
        Assert.Contains(Joins(plan.Pieces, StairKit.BlackMarble), j => !j.straight && j.a.Kind != j.b.Kind);
    }
}
