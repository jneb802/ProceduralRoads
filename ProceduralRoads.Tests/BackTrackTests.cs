using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A chain of two staircases never doubles back alongside itself (StairChain.BackTracks): over a knoll in the Mistlands
/// (-6960,3360), a flat run at 35.5 led onto a flight that climbed to a pad at 40.5 and a second that came straight back down
/// beside it to 30.5, the two passing 3.7 m apart at 38.5. Crests and pits whose legs go off away from each other stay as they
/// are; one that doubles back is joined where the two pass (RoadStairRuns.JoinLow, JoinHigh); a switchback's pad is never
/// laid with both legs flat.
/// </summary>
public sealed class BackTrackTests
{
    private static StairPiece S(float x, float y, float z, float yaw) =>
        new() { Kind = StairPieceKind.Stair, Prefab = StairKit.BlackMarble.Stair, Position = new Vector3(x, y, z), YawDegrees = yaw };
    private static StairPiece F(float x, float y, float z, float yaw) =>
        new() { Kind = StairPieceKind.Landing, Prefab = StairKit.BlackMarble.Floor, Position = new Vector3(x, y, z), YawDegrees = yaw };
    private static StairPiece L(float x, float y, float z, float yaw) =>
        new() { Kind = StairPieceKind.Filler, Prefab = StairKit.BlackMarble.Floor, Position = new Vector3(x, y, z), YawDegrees = yaw };
    private static StairPiece B(float x, float y, float z, float yaw) =>
        new() { Kind = StairPieceKind.Filler, Prefab = "blackmarble_2x1x1", Position = new Vector3(x, y, z), YawDegrees = yaw };
    private static StairPiece C(float x, float y, float z, float yaw) =>
        new() { Kind = StairPieceKind.Filler, Prefab = "blackmarble_1x1", Position = new Vector3(x, y, z), YawDegrees = yaw };

    /// <summary>The ground well under both flights (dry, every floor within the walkway cap of it).</summary>
    private static float Low(float x, float z) => 33f;

    /// <summary>Ground two metres under flights set off at 40.</summary>
    private static float Under(float x, float z) => 38f;

    /// <summary>A plan of <paramref name="pieces"/> as laid: each walked piece's clearances, its end on its top row.</summary>
    private static StairPlan Plan(IEnumerable<StairPiece> pieces, bool reversed)
    {
        StairKit kit = StairKit.BlackMarble;
        var plan = new StairPlan { Kit = kit.Name, Reached = true, Reversed = reversed };
        plan.Pieces.AddRange(pieces);
        foreach (StairPiece p in plan.Pieces)
            for (int s = p.Kind == StairPieceKind.Stair ? 2 : 1; s > 0; s--)
                plan.Clearances.Add(new StairClearance(p.Position, new Vector3(1f, 1f, 1f), p.YawDegrees, p.Kind == StairPieceKind.Stair ? p.Position.y + 0.5f : p.Position.y + kit.FloorTop));
        StairPiece top = plan.Pieces.Last(p => p.Kind != StairPieceKind.Filler);
        float heading = top.Kind == StairPieceKind.Stair ? StairLayout.Wrap(top.YawDegrees + 180f) : top.YawDegrees;
        Vector2 edge = new Vector2(top.Position.x, top.Position.z) + StairLayout.Forward(heading) * (StairLayout.PieceRun / 2f);
        plan.End = new Vector3(edge.x, top.Position.y + (top.Kind == StairPieceKind.Stair ? StairLayout.PieceRise : kit.FloorTop), edge.y);
        plan.EndHeading = heading;
        plan.LastRowKind = top.Kind;
        return plan;
    }

    /// <summary>The knoll's two flights as laid (6960 m west and 3360 m north of where they stood; no columns): the first from
    /// the flat run at 35.5 east, then north and west up to the pad at 40.5; the second, the way the road comes down, laid
    /// climbing from its foot at 30.5 in the north-east up to the pad.</summary>
    internal static (StairPiece[] first, StairPiece[] second) KnollPieces() => (new[] {
            F(1.000f, 34.994f, 0.000f, 90f), F(3.000f, 34.994f, 0.000f, 90f), F(5.000f, 34.994f, 0.000f, 90f),
            L(7.000f, 34.994f, 0.000f, 90f),
            S(7.000f, 35.494f, 2.000f, 180f),
            B(7.000f, 35.994f, 3.500f, 180f), C(7.414f, 35.994f, 3.707f, 135f),
            S(6.000f, 36.494f, 4.414f, 135f),
            B(4.939f, 36.994f, 5.475f, 135f),
            S(4.045f, 37.494f, 5.721f, 112.5f),
            S(2.197f, 38.494f, 6.486f, 112.5f),
            B(0.812f, 38.994f, 7.060f, 112.5f),
            S(0.242f, 39.494f, 7.793f, 135f),
            B(-0.818f, 39.994f, 8.854f, 135f), C(-1.335f, 39.994f, 9.319f, 202.5f),
            F(-0.299f, 39.994f, 10.513f, 22.5f) },
        new[] {
            S(7.857f, 30.494f, 24.796f, 337.5f),
            S(8.622f, 31.494f, 22.948f, 337.5f),
            B(9.196f, 31.994f, 21.562f, 337.5f),
            S(9.928f, 32.494f, 20.993f, 315f),
            B(10.989f, 32.994f, 19.932f, 315f), C(11.428f, 32.994f, 20.079f, 0f),
            S(10.928f, 33.494f, 18.579f, 0f),
            B(10.928f, 33.994f, 17.079f, 0f), C(11.342f, 33.994f, 16.872f, 45f),
            S(9.928f, 34.494f, 16.165f, 45f),
            B(8.868f, 34.994f, 15.104f, 45f),
            S(8.622f, 35.494f, 14.209f, 22.5f),
            S(7.856f, 36.494f, 12.361f, 22.5f),
            B(7.283f, 36.994f, 10.976f, 22.5f), C(7.586f, 36.994f, 10.625f, 67.5f),
            S(6.009f, 37.494f, 10.514f, 67.5f),
            S(4.162f, 38.494f, 9.748f, 67.5f),
            B(2.776f, 38.994f, 9.174f, 67.5f), C(2.743f, 38.994f, 8.712f, 112.5f),
            S(1.548f, 39.494f, 9.748f, 112.5f) });

    internal static (RoadStairSpan up, StairPlan down) Knoll()
    {
        var (first, second) = KnollPieces();
        StairPlan up = Plan(first, false);
        return (new RoadStairSpan { Plan = up, Kit = StairKit.BlackMarble, ToPoint = new Vector2(up.End.x, up.End.z), ToHeight = up.End.y }, Plan(second, true));
    }

    private static List<StairPiece> Walked(StairPlan plan) => plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair || p.Kind == StairPieceKind.Landing).ToList();

    [Fact]
    public void TheKnollDoublesBackAsLaid()
    {
        var (up, down) = Knoll();
        Assert.Equal(40.494f, up.Plan.End.y, 3);
        Assert.Equal(40.494f, down.End.y, 3);
        var faults = StairChain.BackTracks(up.Plan.Pieces, down.Pieces, StairKit.BlackMarble);
        // The two flights' top stairs pass 2.4 m apart at 39.5, facing 22.5 degrees apart: the walk turns back 157.5 over the
        // pad (lower down they face 45 apart: a 135 degree turn, no back-track on its own).
        Assert.Single(faults);
        Assert.StartsWith("stairs from 39.49", faults[0]);
    }

    /// <summary>The same knoll laid again, its two flights wound wider (6952 m west and 3360 m north of where they stood; the
    /// first from its foot in the road's middle west and round north to the floors at 40, the second, laid climbing, from its
    /// foot in the north up to them): their stairs at 38.5 stand 8.7 m apart, past the reach of two flights passing, but the
    /// road still turns back over the top: in from the north-east on 243 (its road's points 8 m back from the foot), out to the
    /// north-north-east on 26.6. Over a crest or through a pit, it is a back-track at any width (StairChain.TurnsBack); a
    /// ridge crossed on, or turned off 90 degrees, is not.</summary>
    [Fact]
    public void AKnollWoundWiderStillTurnsBack()
    {
        StairKit kit = StairKit.BlackMarble;
        StairPlan up = Plan(new[] {
            S(0.924f, 31.477f, 0.383f, 67.5f), S(-0.924f, 32.477f, -0.383f, 67.5f), B(-2.310f, 32.977f, -0.957f, 67.5f),
            S(-3.230f, 33.477f, -0.842f, 90f), B(-4.730f, 33.977f, -0.842f, 90f), S(-5.537f, 34.477f, -0.383f, 112.5f),
            S(-7.385f, 35.477f, 0.383f, 112.5f), B(-8.771f, 35.977f, 0.957f, 112.5f), S(-9.340f, 36.477f, 1.689f, 135f),
            S(-10.754f, 37.477f, 3.104f, 135f), B(-11.814f, 37.977f, 4.164f, 135f), C(-12.254f, 37.977f, 4.018f, 180f),
            S(-11.754f, 38.477f, 5.518f, 180f), B(-11.754f, 38.977f, 7.018f, 180f), S(-11.295f, 39.477f, 7.824f, 202.5f),
            F(-10.530f, 39.977f, 9.672f, 22.5f), F(-8.683f, 39.977f, 8.906f, 292.5f) }, false);
        StairPlan down = Plan(new[] {
            S(-0.700f, 30.477f, 24.278f, 315f), S(0.714f, 31.477f, 22.864f, 315f), B(1.774f, 31.977f, 21.803f, 315f),
            S(2.021f, 32.477f, 20.908f, 337.5f), B(2.594f, 32.977f, 19.522f, 337.5f), S(2.479f, 33.477f, 18.602f, 0f),
            B(2.479f, 33.977f, 17.102f, 0f), C(2.893f, 33.977f, 16.895f, 45f), S(1.479f, 34.477f, 16.188f, 45f),
            S(0.065f, 35.477f, 14.773f, 45f), B(-0.996f, 35.977f, 13.712f, 45f), S(-1.242f, 36.477f, 12.818f, 22.5f),
            B(-1.815f, 36.977f, 11.432f, 22.5f), S(-2.548f, 37.477f, 10.862f, 45f), S(-3.962f, 38.477f, 9.448f, 45f),
            B(-5.022f, 38.977f, 8.387f, 45f), C(-4.876f, 38.977f, 7.948f, 90f), S(-6.376f, 39.477f, 8.448f, 90f),
            B(-7.876f, 39.977f, 8.448f, 90f) }, true);
        // Two flights passing: none (at 38.5 the two face 135 degrees apart, 8.7 m apart).
        Assert.Empty(StairChain.BackTracks(up.Pieces, down.Pieces, kit));
        // The road: in along its points (7.63, 3.82) -> (0, 0), out along (-0.0, 23.6) -> (2.2, 28.0).
        float into = StairLayout.Bearing(new Vector2(7.63f, 3.82f), new Vector2(0f, 0f)), outOf = StairLayout.Bearing(new Vector2(0f, 23.6f), new Vector2(2.2f, 28f));
        Assert.True(StairChain.TurnsBack(into, outOf), $"in {into}, out {outOf}");
        Assert.False(StairChain.TurnsBack(into, into), "straight over the ridge");
        Assert.False(StairChain.TurnsBack(into, StairLayout.Wrap(into + 90f)), "turned 90 degrees");
        Assert.False(StairChain.TurnsBack(into, StairLayout.Wrap(into + 135f)), "turned 135 degrees");
    }

    /// <summary>Joined low where the two pass, by a flat run between stair rows at one height: the lowest within reach, the
    /// rows a rise up from where they set off (at 36.5 they stand 10.4 m apart, past the reach); the hump over it taken away.</summary>
    [Fact]
    public void TheKnollIsJoinedLowWhereItsFlightsPass()
    {
        StairKit kit = StairKit.BlackMarble;
        var (up, down) = Knoll();
        Assert.True(RoadStairRuns.JoinLow(up, down, kit, Low, 2f, (_, _) => true, out Vector2 at));
        Assert.Empty(StairChain.BackTracks(up.Plan.Pieces, down.Pieces, kit));
        // Nothing left over the run at 37.5: the pad and the stairs up to it gone from both.
        Assert.All(Walked(up.Plan).Concat(Walked(down)), p => Assert.True(
            (p.Kind == StairPieceKind.Stair ? p.Position.y + StairLayout.PieceRise : p.Position.y + kit.FloorTop) < 37.5f, $"{p.Kind} at {p.Position}"));
        var run = down.Pieces.Where(p => p.Kind == StairPieceKind.Landing && Mathf.Abs(p.Position.y + kit.FloorTop - 37.494f) < 0.01f).ToList();
        Assert.NotEmpty(run);
        // Each flight kept up to its stair at 36.5, the run between them, met at the first's.
        Assert.Contains(up.Plan.Pieces, p => p.Kind == StairPieceKind.Stair && Mathf.Abs(p.Position.y - 36.494f) < 0.01f);
        Assert.Contains(down.Pieces, p => p.Kind == StairPieceKind.Stair && Mathf.Abs(p.Position.y - 36.494f) < 0.01f);
        Assert.Equal(37.494f, up.Plan.End.y, 3);
        Assert.True(Vector2.Distance(at, new Vector2(5.293f, 5.121f)) < 0.01f, $"met at {at}");
        var faults = FlatRunJoinTests.JoinFaults(up.Plan.Pieces.Concat(down.Pieces).ToList(), kit);
        Assert.True(faults.Count == 0, string.Join("\n", faults) + "\n" + string.Join("\n", down.Pieces.Select(p => $"{p.Kind} {p.Position} {p.YawDegrees}")));
    }

    /// <summary>A crest whose legs go off away from each other, up one side and on down the other, is no back-track: straight
    /// over the top, and turned 90 degrees on a floor at the top. Neither is touched.</summary>
    [Fact]
    public void ACrestWhoseLegsDivergeStaysAsItIs()
    {
        StairKit kit = StairKit.BlackMarble;
        // Up north to a floor at 4 on the top; the second climbing west to the floor's east side.
        StairPlan first = StairLayout.LayFlight(new Vector2(0f, 0f), 40f, new[] { 0f, 0f, 0f, 0f, 0f }, kit, Under, 0,
            new[] { StairPieceKind.Stair, StairPieceKind.Stair, StairPieceKind.Stair, StairPieceKind.Stair, StairPieceKind.Landing });
        StairPlan turned = StairLayout.LayFlight(new Vector2(9f, 9f), 40f, new[] { 270f, 270f, 270f, 270f }, kit, Under, 0);
        // And straight on: the second climbing south from the north, back to back over the top floor.
        StairPlan straight = StairLayout.LayFlight(new Vector2(0f, 18f), 40f, new[] { 180f, 180f, 180f, 180f }, kit, Under, 0);
        foreach (StairPlan second in new[] { turned, straight })
        {
            StairPlan up = Plan(first.Pieces.ToList(), false), down = Plan(second.Pieces.ToList(), true);
            Assert.Empty(StairChain.BackTracks(up.Pieces, down.Pieces, kit));
            int pieces = up.Pieces.Count + down.Pieces.Count;
            var span = new RoadStairSpan { Plan = up, Kit = kit, ToPoint = new Vector2(up.End.x, up.End.z), ToHeight = up.End.y };
            Assert.False(RoadStairRuns.JoinLow(span, down, kit, Under, 2f, (_, _) => true, out _));
            Assert.Equal(pieces, up.Pieces.Count + down.Pieces.Count);
        }
    }

    /// <summary>The knoll upside down: down from a flat into a pit and straight back up beside the way down. Joined high,
    /// where the two pass, by the same flat run (the pit's highest within reach), the dip under it taken away.</summary>
    [Fact]
    public void APitThatDoublesBackIsJoinedHighWhereItsFlightsPass()
    {
        StairKit kit = StairKit.BlackMarble;
        const float m = 37.494f;
        // Turned over about 37.5: a piece's origin y (a stair's base, a floor's a half metre under its surface) to 2m - y - 1;
        // every piece faces the other way (a stair faces down its flight, a floor up it).
        StairPiece Over(StairPiece p) => new() { Kind = p.Kind, Prefab = p.Prefab, Position = new Vector3(p.Position.x, 2f * m - p.Position.y - 1f, p.Position.z),
            YawDegrees = StairLayout.Wrap(p.YawDegrees + 180f) };
        var (first, second) = KnollPieces();
        // The first walked down into the pit (laid climbing out of it), the second up out of it.
        StairPlan below = Plan(first.Select(Over).Reverse(), true), up = Plan(second.Select(Over).Reverse(), false);
        Assert.Single(StairChain.BackTracks(below.Pieces, up.Pieces, kit));

        Assert.True(RoadStairRuns.JoinHigh(below, up, kit, Low, 2f, (_, _) => true, out Vector2 at));
        Assert.Empty(StairChain.BackTracks(below.Pieces, up.Pieces, kit));
        Assert.All(Walked(below).Concat(Walked(up)), p => Assert.True(
            (p.Kind == StairPieceKind.Stair ? p.Position.y : p.Position.y + kit.FloorTop) > m - 0.01f, $"{p.Kind} at {p.Position}"));
        Assert.Contains(up.Pieces, p => p.Kind == StairPieceKind.Landing && Mathf.Abs(p.Position.y + kit.FloorTop - m) < 0.01f);
        Assert.True(Vector2.Distance(at, new Vector2(5.293f, 5.121f)) < 0.01f, $"met at {at}");
        Assert.Equal(m, below.Foot!.Value.y, 3);
        var faults = FlatRunJoinTests.JoinFaults(below.Pieces.Concat(up.Pieces).ToList(), kit);
        Assert.True(faults.Count == 0, string.Join("\n", faults));
    }

    /// <summary>Two flights side by side all the way, a pad between their feet: no flat run can turn the walk round (a run
    /// turns at most 135 degrees), so it is not joined: the road lays one staircase over both stretches, or none.</summary>
    [Fact]
    public void AHairpinPitIsNotJoined()
    {
        StairKit kit = StairKit.BlackMarble;
        StairPlan below = Plan(StairLayout.LayFlight(new Vector2(0f, 0f), 40f, new[] { 180f, 180f, 180f, 180f }, kit, Under, 0).Pieces.ToList(), true);
        var pad = new List<StairPiece> { F(0f, 39.5f, 1f, 0f), F(2f, 39.5f, 1f, 0f) };
        StairPlan up = Plan(pad.Concat(StairLayout.LayFlight(new Vector2(2f, 0f), 40f, new[] { 180f, 180f, 180f, 180f }, kit, Under, 0).Pieces), false);
        var faults = StairChain.BackTracks(below.Pieces, up.Pieces, kit);
        Assert.Contains(faults, f => f.Contains("both legs up"));
        Assert.Contains(faults, f => f.StartsWith("stairs"));
        int pieces = below.Pieces.Count + up.Pieces.Count;
        Assert.False(RoadStairRuns.JoinHigh(below, up, kit, Under, 2f, (_, _) => true, out _));
        Assert.Equal(pieces, below.Pieces.Count + up.Pieces.Count);
    }

    /// <summary>A switchback's pad: a through climb (in on a stair from below, out on one up) is fine; both legs flat, both
    /// down or both up is not.</summary>
    [Theory]
    [InlineData("down", "up", false)]
    [InlineData("up", "down", false)]
    [InlineData("flat", "flat", true)]
    [InlineData("down", "down", true)]
    [InlineData("up", "up", true)]
    [InlineData("flat", "up", false)]
    public void ASwitchbacksPadHasOneLegUpAndOneDown(string inLeg, string outLeg, bool fault)
    {
        StairKit kit = StairKit.BlackMarble;
        // The pad at 5, two floors abreast facing north; each leg the piece behind its floor.
        StairPiece Leg(string leg, float x) => leg switch
        {
            "down" => S(x, 4f, -1f, 180f),
            "up" => S(x, 5f, -1f, 0f),
            _ => F(x, 4.5f, -1f, 0f),
        };
        var pieces = new List<StairPiece> { Leg(inLeg, 0f), F(0f, 4.5f, 1f, 0f), F(2f, 4.5f, 1f, 0f), Leg(outLeg, 2f) };
        Assert.Equal(fault, StairChain.BackTracks(pieces, null, kit).Count > 0);
    }

    /// <summary>The search's switchbacks up a narrow gully are through climbs: no pad with both legs flat or the same way.</summary>
    [Fact]
    public void TheSearchsSwitchbacksAreThroughClimbs()
    {
        static float Gully(float x, float z) => z + (Mathf.Abs(x) > 6f ? 50f : 0f);
        StairPlan plan = StairLayout.Build(new Vector2(0, 0), new Vector2(0, 16), Gully);
        Assert.True(plan.Reached, plan.Reason);
        Assert.True(plan.Switchbacks > 0);
        Assert.Empty(StairChain.BackTracks(plan.Pieces, null, StairKit.Stone));
    }
}
