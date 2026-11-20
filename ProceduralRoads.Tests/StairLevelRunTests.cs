using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// No staircase walks level further than a rest (StairLayout.RestFloors rows, StairLayout.PastARest), anywhere on it: at its
/// top, at its foot or between its flights. The decision (18 Nov 2026): "Use A, but as a 'want to see in game' to confirm.
/// And ensure queen still met. I don't think B is strict enough, stairs could just shift where the last stair is." A cap on
/// the end floors alone (past the last stair) let a staircase dodge it by moving its last stair to the far end of the flat,
/// so the rule reads every level run. The road's final check refuses such a staircase (RoadStairRuns.NoWayOff, the one list
/// every path's refusals go through), and the layout search lays none (StairLayout.Expand).
/// </summary>
[Collection("StairLayout")]
public class StairLevelRunTests : IDisposable
{
    public StairLevelRunTests() { RoadSpatialGrid.Clear(); StairStructures.Reset(); }

    public void Dispose()
    {
        RoadStairRuns.EndsJudged = null;
        RoadSpatialGrid.Clear();
        StairStructures.Reset();
    }

    /// <summary>A straight staircase walking +z from z = 0 at <paramref name="foot"/> high, <paramref name="lanes"/> pieces
    /// abreast, laid as <paramref name="walk"/> reads: S a stair up a rise, F a row of floors, foot first.</summary>
    private static StairRun Straight(string walk, int lanes, StairKit kit, float foot = 30f)
    {
        StairPlan plan = PlanOf(walk, lanes, kit, foot);
        return StairRun.FromPlan(new Vector2(0f, 0f), new Vector2(plan.End.x, plan.End.z), lanes * StairLayout.PieceRun, plan, kit);
    }

    /// <summary>The layout <see cref="Straight"/> lays, as a plan ending at its last row's far edge heading +z.</summary>
    private static StairPlan PlanOf(string walk, int lanes, StairKit kit, float foot = 30f)
    {
        var plan = new StairPlan { Kit = kit.Name, Reached = true, EndHeading = 0f };
        float height = foot, z = 0f;
        foreach (char c in walk)
        {
            for (int lane = 0; lane < lanes; lane++)
            {
                float x = (lane - (lanes - 1) / 2f) * StairLayout.PieceRun;
                plan.Pieces.Add(c == 'S'
                    ? new StairPiece { Kind = StairPieceKind.Stair, Prefab = kit.Stair, YawDegrees = 180f, Position = new Vector3(x, height, z + StairLayout.PieceRun / 2f) }
                    : new StairPiece { Kind = StairPieceKind.Landing, Prefab = kit.Floor, YawDegrees = 0f, Position = new Vector3(x, height - kit.FloorTop, z + StairLayout.PieceRun / 2f) });
            }
            if (c == 'S') height += StairLayout.PieceRise;
            z += StairLayout.PieceRun;
        }
        plan.End = new Vector3(0f, height, z);
        return plan;
    }

    private static string Of(int stairs, char piece = 'S') => new(piece, stairs);

    /// <summary>The road's final check on <paramref name="run"/> alone, its ends taken as they are judged elsewhere (the ends'
    /// own rules are StairEndsTests'): only its level runs judged, on <paramref name="ground"/> (<see cref="Slope"/> unless given).</summary>
    private static Vector2? Refused(StairRun run, out string why, Func<float, float, float>? ground = null)
    {
        RoadStairRuns.EndsJudged = (_, ends) => ends.Clear();
        return RoadStairRuns.NoWayOff(new[] { run }, _ => ground ?? Slope, out why);
    }

    /// <summary>Ground climbing a rise per run along +z from 30 at z = 0: under every floor of these staircases, never a dip.</summary>
    private static float Slope(float x, float z) => 30f + z * StairLayout.PieceRise / StairLayout.PieceRun;

    /// <summary>Ground climbing 2 m up a face off z = 0, then flat at the face's top (32), as under the crypt's way in: its
    /// floors at grade all along (drops of 0.01 and 0.12 in game).</summary>
    private static float FaceThenFlat(float x, float z) => 30f + Mathf.Clamp(z, 0f, 4f) * StairLayout.PieceRise / StairLayout.PieceRun;

    /// <summary>
    /// A way in onto a crypt's landing as the game laid it: a stone staircase two pieces abreast climbing 2 m off its start pad,
    /// then 10 rows of floors (20 floors) over flat ground to the landing. Refused by the road's final check, at the middle of
    /// its flat: a level run of 10 rows is no rest (StairLayout.RestFloors).
    /// </summary>
    [Fact]
    public void AStaircaseClimbing2MThenWalking10LevelRowsToItsLandingIsRefused()
    {
        StairRun run = Straight(Of(2) + Of(10, 'F'), lanes: 2, StairKit.Stone);
        StairEnds.LevelRun flat = Assert.Single(StairEnds.LevelRuns(run, FaceThenFlat));
        Assert.Equal(10, flat.Rows);

        Vector2? at = Refused(run, out string why, FaceThenFlat);

        Assert.True(at.HasValue, "kept: a 2 m climb then 10 level rows");
        Assert.Equal(RoadStairRuns.LevelRunPastARest, why);
        Assert.Equal(flat.At, at!.Value);
        Assert.InRange(at.Value.y, 2 * StairLayout.PieceRun, 12 * StairLayout.PieceRun);
    }

    /// <summary>Ground at 32 (the crypt shape's floors at grade) dipping <paramref name="depth"/> under them between z 8 and
    /// 20, back up to 32 beyond.</summary>
    private static Func<float, float, float> Dip(float depth) =>
        (x, z) => FaceThenFlat(x, z) - (z > 8f && z < 20f ? depth : 0f);

    /// <summary>
    /// Walkways are exempt (decision, 19 Nov 2026): a level walkway of floors carrying the road over a dip is no staircase, so
    /// the rest's length does not bound it. The same 2 m climb and 10 rows of floors, with the ground under the floors dipping
    /// 3 m between ends on ground at their height, is a walkway (StairChain.OverADip, the chain's one rule for a valley crossed
    /// level) and kept. A dip shallower than StairChain.CrestRise is flat ground still, and ground falling away under the
    /// floors and not coming back is no dip: both refused, as the crypt's walk over flat ground is.
    /// </summary>
    [Fact]
    public void AWalkwayCarryingTheRoadOverADipIsKeptButNotOverFlatOrFallingGround()
    {
        StairRun run = Straight(Of(2) + Of(10, 'F'), lanes: 2, StairKit.Stone);

        StairEnds.LevelRun walkway = Assert.Single(StairEnds.LevelRuns(run, Dip(3f)));
        Assert.Equal(10, walkway.Rows);
        Assert.False(walkway.PastARest, $"ground {walkway.EndA:F2}/{walkway.EndB:F2} at its ends, {walkway.Low:F2} lowest");
        Assert.Null(Refused(run, out string kept, Dip(3f)));
        Assert.Equal("", kept);

        Assert.NotNull(Refused(run, out string shallow, Dip(StairChain.CrestRise / 2f)));
        Assert.Equal(RoadStairRuns.LevelRunPastARest, shallow);

        float Falling(float x, float z) => FaceThenFlat(x, z) - Mathf.Clamp(z - 8f, 0f, 16f) * 0.25f;
        Assert.NotNull(Refused(run, out string falling, Falling));
        Assert.Equal(RoadStairRuns.LevelRunPastARest, falling);
    }

    /// <summary>Dips between the shallow one refused and the 3 m one kept: as deep as StairChain.CrestRise and more, across the
    /// same 12 m, each a walkway (its 4 rows on the flat either side at grade, a rest's length, StairLayout.RestFloors).</summary>
    [Theory]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void AWalkwayOverADipBetweenTheShallowAndTheDeepIsKept(float depth)
    {
        StairRun run = Straight(Of(2) + Of(10, 'F'), lanes: 2, StairKit.Stone);

        StairEnds.LevelRun walkway = Assert.Single(StairEnds.LevelRuns(run, Dip(depth)));
        Assert.Equal(2, walkway.Beside);
        Assert.False(walkway.PastARest, $"ground {walkway.EndA:F2}/{walkway.EndB:F2} at its ends, {walkway.Low:F2} lowest, {walkway.Beside} rows beside it at most");
        Assert.Null(Refused(run, out string kept, Dip(depth)));
        Assert.Equal("", kept);
    }

    /// <summary>Ground at the crypt shape's flat top (32) with one hollow <paramref name="depth"/> deep under the single floor
    /// row between z 12 and 14.</summary>
    private static Func<float, float, float> Hollow(float depth) =>
        (x, z) => FaceThenFlat(x, z) - (z > 12f && z < 14f ? depth : 0f);

    /// <summary>
    /// Only the dip is exempt (decision, 19 Nov 2026: "Yes, only the dip is exempt"): the rows of a level run at grade (within a
    /// step of the ground, StairLayout.Walkable) still count toward a rest's length, on either side of the dip. The crypt's
    /// 20 m boardwalk on flat ground, with one 1.1 m hollow under a single floor row: a dip by the chain's rule
    /// (StairChain.OverADip), but its other 9 rows walk at grade, so it is no walkway and refused, by the road's final check
    /// and by the layout search alike (one rule, StairLayout.PastARest).
    /// </summary>
    [Fact]
    public void ABoardwalkOnFlatGroundOverOneHollowUnderASingleFloorIsRefused()
    {
        StairRun run = Straight(Of(2) + Of(10, 'F'), lanes: 2, StairKit.Stone);
        var ground = Hollow(1.1f);

        StairEnds.LevelRun flat = Assert.Single(StairEnds.LevelRuns(run, ground));
        Assert.True(StairChain.OverADip(flat.Low, flat.EndA, flat.EndB), $"ground {flat.EndA:F2}/{flat.EndB:F2} at its ends, {flat.Low:F2} lowest");
        Assert.Equal(5, flat.Beside);
        Assert.True(flat.PastARest);
        Assert.NotNull(Refused(run, out string why, ground));
        Assert.Equal(RoadStairRuns.LevelRunPastARest, why);

        var (_, longest, said) = Searched(ground, 28f, 2f * StairLayout.PieceRun);
        Assert.True(longest <= StairLayout.RestFloors, $"laid a level run of {longest} rows: {said}");
    }

    /// <summary>
    /// A layout with no stair in it is no staircase: the road runs on over the stretch (RoadStairRuns.Lay, "floors only, left to
    /// its road"). The layout search judges a level run past a rest only once a stair has been laid: a stretch of flat ground
    /// 20 m long (10 rows, more than a rest) is laid as floors alone and reached, for the road to run on over it, where judged
    /// from its first floor on, the search found no layout (not reached) and the road was refused there. Once a stair follows
    /// such a start, it is refused at that stair (<see cref="TheLayoutSearchLaysNoClimbOf2MThenWalking10LevelRowsToItsGoal"/>).
    /// </summary>
    [Fact]
    public void AStairFreeFlatStartLongerThanARestIsLeftToItsRoadNotRefused()
    {
        float Flat(float x, float z) => 40f;
        StairPlan plan = StairLayout.Build(new Vector2(0f, 0f), new Vector2(0f, 20f), Flat, StairLayout.PieceRun, StairKit.Stone);

        Assert.True(plan.Reached, plan.Reason);
        Assert.DoesNotContain(plan.Pieces, p => p.Kind == StairPieceKind.Stair);
        Assert.True(plan.Pieces.Count(p => p.Kind == StairPieceKind.Landing) > StairLayout.RestFloors);
    }

    /// <summary>
    /// Floors a finish lays outside the search are judged by the same rule (StairLayout.PastARest), so a finish that would walk
    /// past a rest is no finish and the end's next case is tried, where before the whole road was refused at the final check
    /// (RoadStairRuns.NoWayOff). A search ending on 4 top rows (a rest) on flat ground at grade is a finished top; a finish's
    /// floor laid on (5 rows) is not (RoadStairRuns.TopFinished). The same at a foot (RoadStairRuns.FootFinished): 4 rows of
    /// floors before the first stair finished, 5 not.
    /// </summary>
    [Fact]
    public void AFinishWalkingPastARestIsNoFinishAtATopOrAFoot()
    {
        int rest = StairLayout.RestFloors;
        float Top(float x, float z) => 30f + Mathf.Clamp(z, 0f, 6f) * StairLayout.PieceRise / StairLayout.PieceRun;
        Assert.True(RoadStairRuns.TopFinished(PlanOf(Of(3) + Of(rest, 'F'), 1, StairKit.Stone), Top));
        StairPlan finished = PlanOf(Of(3) + Of(rest + 1, 'F'), 1, StairKit.Stone);
        Assert.Equal(StairEnds.Verdict.Ok, RoadStairRuns.TopEnd(finished, Top)!.Verdict);
        Assert.False(RoadStairRuns.TopFinished(finished, Top));

        // Flat at the floors' height under them (clear of the sea), climbing a rise a run under the flight after them.
        Func<float, float, float> Foot(int floors) => (x, z) => 40f + Mathf.Max(0f, z - floors * StairLayout.PieceRun) * StairLayout.PieceRise / StairLayout.PieceRun;
        Assert.True(RoadStairRuns.FootFinished(PlanOf(Of(rest, 'F') + Of(3), 1, StairKit.Stone, 40f), Foot(rest)));
        StairPlan atFoot = PlanOf(Of(rest + 1, 'F') + Of(3), 1, StairKit.Stone, 40f);
        Assert.Equal(StairEnds.Verdict.Ok, RoadStairRuns.FootEnd(atFoot, Foot(rest + 1))!.Verdict);
        Assert.False(RoadStairRuns.FootFinished(atFoot, Foot(rest + 1)));
    }

    /// <summary>A staircase whose body carries a walkway (2 stairs, 10 rows of floors, 3 stairs) with its top to be finished: the
    /// floor a top's finish lays has its own level run, and only that run's rest rule judges it (RoadStairRuns.WalksPastARest).</summary>
    private static StairPlan WalkwayBody() => PlanOf(Of(2) + Of(10, 'F') + Of(3), 1, StairKit.Stone);

    /// <summary>Ground under <see cref="WalkwayBody"/>: the face, the walkway's floors at 32 over <paramref name="dip"/> m of dip
    /// between z 8 and 20, the flight after them, then flat at the top's height.</summary>
    private static Func<float, float, float> UnderWalkwayBody(float dip) => (x, z) =>
        z < 24f ? Dip(dip)(x, z) : 32f + Mathf.Min(z - 24f, 6f) * StairLayout.PieceRise / StairLayout.PieceRun;

    /// <summary>
    /// A walkway in a staircase's body no longer refuses every finish at its top (the fix of "a walkway in a staircase's body refused
    /// every top finish"), on the production path: a road's top (StairLayout.FinishTop with RoadStairRuns.TopFinished, as
    /// RoadStairRuns.TopOntoSurface asks it) and an approach's (RoadStairRuns.FinishApproachTop). The body's run is kept by the
    /// final check over a dip; over ground that shows none (as the finished ground can) the body's run is past a rest, the road
    /// is refused for it there, and the finish at the top is still laid: it is judged only by the level run its own floor is in.
    /// </summary>
    [Theory]
    [InlineData(3f)]
    [InlineData(0f)]
    public void ATopFinishIsLaidOnAStaircaseWhoseBodyHoldsAWalkway(float dip)
    {
        Func<float, float, float> ground = UnderWalkwayBody(dip);
        StairRun body = StairRun.FromPlan(new Vector2(0f, 0f), new Vector2(0f, 30f), RoadStairRuns.Width, WalkwayBody(), StairKit.Stone);
        Assert.Equal(dip == 0f, Assert.Single(StairEnds.LevelRuns(body, ground)).PastARest);

        StairPlan road = WalkwayBody();
        StairLayout.TopFinish finish = StairLayout.FinishTop(road, ground, RoadStairRuns.Width, StairCover.RoadStaircase, _ => true, out _,
            wayOff: p => RoadStairRuns.TopFinished(p, ground));
        Assert.Equal(StairLayout.TopFinish.TopFloor, finish);
        Assert.False(RoadStairRuns.WalksPastARest(road, ground, top: true));

        StairPlan approach = WalkwayBody();
        var (approached, _) = RoadStairRuns.FinishApproachTop(approach, ground, _ => true, refuseBare: false);
        Assert.NotEqual(StairLayout.TopFinish.None, approached);
        Assert.False(RoadStairRuns.WalksPastARest(approach, ground, top: true));
    }

    /// <summary>The same body with 5 rows of floors the finish lays on its own run (a rest is 4) is a finish that walks past a rest,
    /// refused by the production path as the end's own run, whatever the walkway in the body: the top's next case is tried.</summary>
    [Fact]
    public void ATopFinishWalkingPastARestIsNoFinishOnAStaircaseWhoseBodyHoldsAWalkway()
    {
        int rest = StairLayout.RestFloors;
        // The walkway's flight, then a run of rest + 1 floors at the top, over flat ground at their height.
        StairPlan Body() => PlanOf(Of(2) + Of(10, 'F') + Of(3) + Of(rest + 1, 'F'), 1, StairKit.Stone);
        Func<float, float, float> ground = (x, z) => z < 30f ? UnderWalkwayBody(3f)(x, z) : 35f;
        StairPlan plan = Body();
        Assert.False(RoadStairRuns.TopFinished(plan, ground));
        Assert.True(RoadStairRuns.WalksPastARest(plan, ground, top: true));
    }

    /// <summary>
    /// The layout search's twin: the 2 m face and its flat top, with a 3 m dip across the flat. Over the dip the search may
    /// walk level further than a rest, as a walkway (it is laid and the road's final check keeps it); on the same ground
    /// without the dip it lays no run longer than a rest (<see cref="TheLayoutSearchLaysNoClimbOf2MThenWalking10LevelRowsToItsGoal"/>).
    /// </summary>
    [Fact]
    public void TheLayoutSearchLaysAWalkwayOverADipAndTheFinalCheckKeepsIt()
    {
        var ground = Dip(3f);
        var (plan, longest, said) = Searched(ground, 28f, 2f * StairLayout.PieceRun);

        Assert.True(plan.Reached, said);
        Assert.True(longest > StairLayout.RestFloors, $"laid no walkway over the dip: {said}");
        RoadStairRuns.EndsJudged = (_, ends) => ends.Clear();
        Assert.Null(RoadStairRuns.NoWayOff(new[] { StairRun.FromPlan(new Vector2(0f, 0f), new Vector2(0f, 28f), 2f * StairLayout.PieceRun, plan, StairKit.Stone) },
            _ => ground, out string why));
        Assert.Equal("", why);
    }

    /// <summary>
    /// A level run longer than a rest between two flights is refused too, so a staircase cannot keep its long flat by moving
    /// its last stair to the far end of it: the same 10 rows with one stair after them, and a rest of one row past
    /// StairLayout.RestFloors mid-climb, are refused; a rest of RestFloors rows mid-climb is kept.
    /// </summary>
    [Fact]
    public void AMidClimbRestLongerThanARestIsRefusedSoTheLastStairCannotShiftToKeepAFlat()
    {
        int rest = StairLayout.RestFloors;
        Assert.NotNull(Refused(Straight(Of(2) + Of(10, 'F') + Of(1), lanes: 2, StairKit.Stone), out string shifted));
        Assert.Equal(RoadStairRuns.LevelRunPastARest, shifted);

        StairRun longRest = Straight(Of(3) + Of(rest + 1, 'F') + Of(3), lanes: 1, StairKit.Stone);
        Assert.Equal(rest + 1, Assert.Single(StairEnds.LevelRuns(longRest, Slope)).Rows);
        Assert.NotNull(Refused(longRest, out string mid));
        Assert.Equal(RoadStairRuns.LevelRunPastARest, mid);

        StairRun aRest = Straight(Of(3) + Of(rest, 'F') + Of(3), lanes: 1, StairKit.Stone);
        Assert.Equal(rest, Assert.Single(StairEnds.LevelRuns(aRest, Slope)).Rows);
        Assert.Null(Refused(aRest, out string kept));
        Assert.Equal("", kept);
    }

    /// <summary>
    /// The Dvergr boss entrance's way in, as the offline generator laid it (13 Nov): one blackmarble piece wide, 16 m of climb
    /// in five flights with rests of 4, 2, 4 and 1 rows between them, none longer than a rest (StairLayout.RestFloors): every
    /// level run read as the layout laid it, and the road's final check keeps it.
    /// </summary>
    [Fact]
    public void TheBossEntrancesStaircaseClimbing16MWithRestsOfFourRowsOrFewerIsKept()
    {
        StairRun run = Straight(Of(2) + Of(4, 'F') + Of(5) + Of(2, 'F') + Of(4) + Of(4, 'F') + Of(4) + Of(1, 'F') + Of(1), lanes: 1, StairKit.BlackMarble);
        Assert.Equal(16, run.Pieces.Count(p => p.Kind == StructurePieceKind.StairStep));
        Assert.Equal(new[] { 4, 2, 4, 1 }, StairEnds.LevelRuns(run, Slope).OrderBy(l => l.At.y).Select(l => l.Rows).ToArray());

        Assert.Null(Refused(run, out string why));
        Assert.Equal("", why);
    }

    /// <summary>The layout search's longest level run on <paramref name="ground"/> from z = 0 to <paramref name="to"/>, and the
    /// layout's walk as S (a stair) and Fn (n rows of floors), for the message; -1 where it lays none.</summary>
    private static (StairPlan plan, int longest, string said) Searched(Func<float, float, float> ground, float to, float width)
    {
        StairPlan plan = StairLayout.Build(new Vector2(0f, 0f), new Vector2(0f, to), ground, width, StairKit.Stone);
        if (!plan.Reached) return (plan, -1, plan.Reason);
        List<StairEnds.LevelRun> runs = StairEnds.LevelRuns(StairRun.FromPlan(new Vector2(0f, 0f), new Vector2(0f, to), width, plan, StairKit.Stone), ground);
        string said = string.Join(" ", runs.OrderBy(l => l.At.y).Select(l => $"F{l.Rows}@{l.At.y:F0}"));
        return (plan, runs.Count == 0 ? 0 : runs.Max(l => l.Rows), said);
    }

    /// <summary>
    /// The same shape through the layout search, the road's final check's twin (one rule, StairLayout.PastARest, read by both):
    /// a climb of 2 m up a face, then 20 m of flat ground to the goal at the face's top. The search lays no layout walking 10
    /// rows level over the flat; priced past a rest instead (up to a bound), it laid one.
    /// </summary>
    [Fact]
    public void TheLayoutSearchLaysNoClimbOf2MThenWalking10LevelRowsToItsGoal()
    {
        float Ground(float x, float z) => 30f + Mathf.Clamp(z - 4f, 0f, 4f) * StairLayout.PieceRise / StairLayout.PieceRun;

        var (plan, longest, said) = Searched(Ground, 28f, 2f * StairLayout.PieceRun);

        Assert.True(longest <= StairLayout.RestFloors, $"laid a level run of {longest} rows ({(plan.Reached ? "reached" : "not reached")}): {said}");
    }

    /// <summary>
    /// A mid-climb shelf six rows long between two 4 m faces: the layout search lays no rest of more than
    /// StairLayout.RestFloors rows over it (it lays the shelf in shorter rests, or none), so the shelf's walk cannot be kept by
    /// moving a stair to its far end.
    /// </summary>
    [Fact]
    public void TheLayoutSearchLaysNoMidClimbRestLongerThanARest()
    {
        float Face(float z, float from) => Mathf.Clamp(z - from, 0f, 8f) * StairLayout.PieceRise / StairLayout.PieceRun;
        float Ground(float x, float z) => 30f + Face(z, 0f) + Face(z, 20f);

        var (_, longest, said) = Searched(Ground, 28f, StairLayout.PieceRun);

        Assert.True(longest <= StairLayout.RestFloors, $"laid a level run of {longest} rows: {said}");
    }

    /// <summary>
    /// A climb of 16 m up a steady face, as the Dvergr boss entrance's way in climbs: the layout search lays it, with rests
    /// of StairLayout.RestFloors rows or fewer, and the road's final check keeps it.
    /// </summary>
    [Fact]
    public void TheLayoutSearchLaysA16MClimbWithRestsOfFourRowsOrFewerAndTheFinalCheckKeepsIt()
    {
        float Ground(float x, float z) => 30f + Mathf.Clamp(z, 0f, 40f) * 0.4f;

        var (plan, longest, said) = Searched(Ground, 40f, StairLayout.PieceRun);

        Assert.True(plan.Reached, said);
        Assert.True(plan.Rise > 15f, $"climbs {plan.Rise:F1} m: {said}");
        Assert.InRange(longest, 0, StairLayout.RestFloors);
        RoadStairRuns.EndsJudged = (_, ends) => ends.Clear();
        Assert.Null(RoadStairRuns.NoWayOff(new[] { StairRun.FromPlan(new Vector2(0f, 0f), new Vector2(0f, 40f), StairLayout.PieceRun, plan, StairKit.Stone) },
            _ => Ground, out string why));
        Assert.Equal("", why);
    }

    /// <summary>The flight <see cref="PlanOf"/> lays, turned about the middle of z = 0 to <paramref name="about"/>: its foot at
    /// z = <paramref name="about"/>, its top where the original stood at the other side of it, its pieces facing the other way.</summary>
    private static StairPlan Mirrored(StairPlan plan, float about)
    {
        var turned = new StairPlan { Kit = plan.Kit, Reached = true, EndHeading = 180f };
        foreach (StairPiece p in plan.Pieces)
            turned.Pieces.Add(new StairPiece { Kind = p.Kind, Prefab = p.Prefab, YawDegrees = p.YawDegrees + 180f, Position = new Vector3(p.Position.x, p.Position.y, about - p.Position.z) });
        turned.End = new Vector3(plan.End.x, plan.End.y, about - plan.End.z);
        return turned;
    }

    /// <summary>Ground under two flights met on a crest (<see cref="TwoStaircasesMeetingOnAFlatCrestEachEndingOnARestWalkPastOneTogetherAndAreKeptApart"/>):
    /// a rise per run up from 30 at z = 0 to 33 at z = 6, flat at 33 to z = 22, then down a rise per run; with
    /// <paramref name="dip"/> m of dip between z 8 and 20.</summary>
    private static Func<float, float, float> CrestOf(float dip = 0f) => (x, z) =>
        (z < 6f ? 30f + z * StairLayout.PieceRise / StairLayout.PieceRun
            : z <= 22f ? 33f : 33f - (z - 22f) * StairLayout.PieceRise / StairLayout.PieceRun)
        - (z > 8f && z < 20f ? dip : 0f);

    /// <summary>
    /// Two staircases meeting end to end on a flat crest, each ending on a rest of 4 floors (StairLayout.RestFloors), walk
    /// 8 rows level from one flight to the other: one level run past a rest, though each alone is a rest. The road's final check
    /// (RoadStairRuns.NoWayOff) judges a level run carried on across onto a staircase met end to end as one run
    /// (StairEnds.LevelRuns with the met pieces), so it refuses them: as two of the road's own staircases, and as one of its own
    /// met by one the network already has (StairStructures.MetEndToEnd). Each alone is still kept.
    /// </summary>
    [Fact]
    public void TwoStaircasesMeetingOnAFlatCrestEachEndingOnARestWalkPastOneTogetherAndAreKeptApart()
    {
        int rest = StairLayout.RestFloors;
        // The first climbs 3 m from z = 0 and rests 4 rows (z 6-14); the second, its mirror, rests 4 rows (z 14-22) and comes down to z = 28.
        StairPlan first = PlanOf(Of(3) + Of(rest, 'F'), 1, StairKit.Stone);
        StairPlan second = Mirrored(first, 28f);
        Func<float, float, float> Crest = CrestOf();
        StairRun a = StairRun.FromPlan(new Vector2(0f, 0f), new Vector2(0f, 14f), RoadStairRuns.Width, first, StairKit.Stone);
        StairRun b = StairRun.FromPlan(new Vector2(0f, 28f), new Vector2(0f, 14f), RoadStairRuns.Width, second, StairKit.Stone);

        Assert.Equal(rest, Assert.Single(StairEnds.LevelRuns(a, Crest)).Rows);
        Assert.Equal(rest, Assert.Single(StairEnds.LevelRuns(b, Crest)).Rows);
        // Their pieces as one staircase: one level run of 8 rows, past a rest.
        StairPlan together = PlanOf(Of(3) + Of(2 * rest, 'F'), 1, StairKit.Stone);
        StairEnds.LevelRun joined = Assert.Single(StairEnds.LevelRuns(StairRun.FromPlan(new Vector2(0f, 0f), new Vector2(0f, 22f), RoadStairRuns.Width, together, StairKit.Stone), Crest));
        Assert.Equal(2 * rest, joined.Rows);
        Assert.True(joined.PastARest);

        RoadStairRuns.EndsJudged = (_, ends) => ends.Clear();
        Assert.NotNull(RoadStairRuns.NoWayOff(new[] { a, b }, _ => Crest, out string why));
        Assert.Equal(RoadStairRuns.LevelRunPastARest, why);
        Assert.Null(RoadStairRuns.NoWayOff(new[] { a }, _ => Crest, out why));
        // The second already in the network, met end to end at the crest: the first is refused for their level run together.
        StairStructures.Add(b);
        Assert.NotNull(RoadStairRuns.NoWayOff(new[] { a }, _ => Crest, out why));
        Assert.Equal(RoadStairRuns.LevelRunPastARest, why);
    }

    /// <summary>
    /// The same two flights met on the crest with the ground under their floors dipping 3 m between ends at their height: their
    /// level run together is a walkway over the dip (StairChain.OverADip), its rows beside the dip a rest at most, and kept by
    /// the final check. Only the dip is exempt: with the dip between z 8 and 12 under the first's floors, the rows beside it
    /// after it (one of the first's and the second's four) walk past a rest across the two staircases and are refused.
    /// </summary>
    [Fact]
    public void TwoStaircasesMeetingOverADipOnACrestAreAWalkwayOnlyOverTheDip()
    {
        int rest = StairLayout.RestFloors;
        StairPlan first = PlanOf(Of(3) + Of(rest, 'F'), 1, StairKit.Stone);
        StairRun a = StairRun.FromPlan(new Vector2(0f, 0f), new Vector2(0f, 14f), RoadStairRuns.Width, first, StairKit.Stone);
        StairRun b = StairRun.FromPlan(new Vector2(0f, 28f), new Vector2(0f, 14f), RoadStairRuns.Width, Mirrored(first, 28f), StairKit.Stone);
        RoadStairRuns.EndsJudged = (_, ends) => ends.Clear();
        Assert.Null(RoadStairRuns.NoWayOff(new[] { a, b }, _ => CrestOf(3f), out string why));
        Assert.Equal("", why);

        Func<float, float, float> shortDip = (x, z) => CrestOf()(x, z) - (z > 8f && z < 12f ? 3f : 0f);
        Assert.Null(RoadStairRuns.NoWayOff(new[] { a }, _ => shortDip, out why));
        Assert.Null(RoadStairRuns.NoWayOff(new[] { b }, _ => shortDip, out why));
        Assert.NotNull(RoadStairRuns.NoWayOff(new[] { a, b }, _ => shortDip, out why));
        Assert.Equal(RoadStairRuns.LevelRunPastARest, why);
    }

    /// <summary>
    /// The layout search does as the final check: the way down from a crest floor ending the first flight's rest of 4 rows
    /// (RoadStairRuns.DownFromCrest, StairLayout.BuildDown, the crest pair over a ridge) walks that level run on, so it lays no
    /// floor before its first stair down where the two together would walk past a rest. Reached or not, the pair never walks
    /// past a rest: their pieces read as one staircase by the final check's reader (StairEnds.LevelRuns).
    /// </summary>
    [Fact]
    public void TheWayDownFromACrestFloorEndingARestWalksNoFloorsPastItWithTheFirstFlight()
    {
        int rest = StairLayout.RestFloors;
        Func<float, float, float> crest = CrestOf();
        StairPlan first = PlanOf(Of(3) + Of(rest, 'F'), 1, StairKit.Stone);
        first.LastRowKind = StairPieceKind.Landing;
        var up = new RoadStairSpan { Plan = first, Kit = StairKit.Stone, ToPoint = new Vector2(first.End.x, first.End.z), ToHeight = first.End.y };
        StairContinuation? crestRow = RoadStairRuns.DownFromCrest(up, crest, out bool floored);
        Assert.True(crestRow.HasValue && floored);
        StairPlan down = StairLayout.BuildDown(crestRow!.Value, new Vector2(0f, 30f), crest, RoadStairRuns.Width, StairKit.Stone);
        if (!down.Reached) return;
        var both = new StairPlan { Kit = first.Kit, Reached = true };
        both.Pieces.AddRange(first.Pieces);
        both.Pieces.AddRange(down.Pieces);
        StairRun pair = StairRun.FromPlan(new Vector2(0f, 0f), new Vector2(0f, 30f), RoadStairRuns.Width, both, StairKit.Stone);
        Assert.DoesNotContain(StairEnds.LevelRuns(pair, crest), l => l.PastARest);
    }

    /// <summary>The crest join's verdict (RoadStairRuns.JoinedPastARest, asked of a join laid between two tops, JoinCrest) is the
    /// final check's (RoadStairRuns.NoWayOff) on the same two staircases: past a rest on the flat crest, a walkway over the dip.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(3f)]
    public void ACrestJoinIsJudgedAsTheFinalCheckJudgesItsTwoStaircases(float dip)
    {
        int rest = StairLayout.RestFloors;
        StairPlan first = PlanOf(Of(3) + Of(rest, 'F'), 1, StairKit.Stone), second = Mirrored(first, 28f);
        StairRun a = StairRun.FromPlan(new Vector2(0f, 0f), new Vector2(0f, 14f), RoadStairRuns.Width, first, StairKit.Stone);
        StairRun b = StairRun.FromPlan(new Vector2(0f, 28f), new Vector2(0f, 14f), RoadStairRuns.Width, second, StairKit.Stone);
        RoadStairRuns.EndsJudged = (_, ends) => ends.Clear();
        bool refused = RoadStairRuns.NoWayOff(new[] { a, b }, _ => CrestOf(dip), out _) != null;
        Assert.Equal(dip == 0f, refused);
        Assert.Equal(refused, RoadStairRuns.JoinedPastARest(second, first, CrestOf(dip)));
    }
}
