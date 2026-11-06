using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// A road staircase's foot (one owner): it starts where its road can climb to it following its ground, sets off from the ring
/// at its road's end (<see cref="RoadEndRing"/>, the one owner of where a staircase meets the road at a road's end), never
/// crosses its road (<see cref="SpanAcrossItsRoad"/>), is lowered toward a road that cannot climb to it (the road's end moving
/// with it, <see cref="RoadEndAt"/>), is not set off from a road built as a ridge or a trench to it, and has an apron cut where
/// it stands against a bank. The layout's own foot rules (set off back, stair first, the ledge's edge) are StairLayout's.
/// </summary>
public static partial class RoadStairRuns
{
    /// <summary>Staircase feet lowered toward their road this generation (StairLayout.LowerFoot).</summary>
    public static int FeetLowered;

    /// <summary>Staircases started further back down their road this generation (<see cref="StartLower"/>).</summary>
    public static int FeetStartedLower;

    /// <summary>
    /// How far the road's ground back along its approach may lie under the line climbing to its staircase's foot at the grade
    /// cap. Further, and the road held to the foot's height stands on an embankment there: the grade limiter keeps it on that
    /// line all the way down, over the ground at the bottom of the climb (a road climbing to a staircase's foot at
    /// -4764.9,1737.7 stood 6 m over a gully, the ground lower on both sides).
    /// </summary>
    public const float ApproachFill = 1f;

    /// <summary>How far back along its road a foot's approach is read (<see cref="ApproachUnder"/>): the stretch the road's end
    /// ramp bends to the foot's height (RoadEndpointRamp.RampLength).</summary>
    public const float ApproachReach = RoadEndpointRamp.RampLength;

    /// <summary>
    /// How far the ground of the road arriving at path[<paramref name="foot"/>], walked from it by <paramref name="step"/>
    /// (+1 or -1) no further than index <paramref name="stop"/>, lies under the line climbing at <paramref name="cap"/> to the
    /// foot's ground: the most fill the road held to the foot stands on. Read every metre up to <see cref="ApproachReach"/>.
    /// </summary>
    internal static float ApproachUnder(List<Vector2> path, int foot, int step, int stop, Func<Vector2, float> ground, float cap)
    {
        float top = ground(path[foot]), walked = 0f, under = 0f;
        for (int i = foot + step; step < 0 ? i >= stop : i <= stop; i += step)
        {
            Vector2 a = path[i - step], b = path[i];
            float run = Vector2.Distance(a, b);
            int samples = Mathf.Max(1, Mathf.CeilToInt(run));
            for (int k = 1; k <= samples; k++)
            {
                float d = walked + run * k / samples;
                if (d > ApproachReach) return under;
                under = Mathf.Max(under, top - cap * d - ground(Vector2.Lerp(a, b, (float)k / samples)));
            }
            walked += run;
        }
        return under;
    }

    /// <summary>
    /// A stair stretch of <paramref name="path"/> started lower, the staircase taking the height its road cannot: where the
    /// road arriving at its foot (its lower end) cannot climb to it following its ground within the grade cap (its ground more
    /// than <see cref="ApproachFill"/> under the line to the foot, <see cref="ApproachUnder"/>), the foot moves back along the
    /// road to the nearest path point whose own approach is within it. More stairs and a lower foot instead of a road on an
    /// embankment. Not onto the path's ends, past a crossing, nor within <see cref="StoneGap"/> of the stretch before
    /// (<paramref name="before"/>, its last index, -1 for none) or after (<paramref name="after"/>, its first index, the path's
    /// count for none); nor for a run an earlier road laid between the stretch's ends, which is walked as it stands. The stretch
    /// as it was where no such point is.
    /// </summary>
    internal static (int from, int to) StartLower(List<Vector2> path, (int from, int to) stretch, int before, int after,
        IReadOnlyList<RoadCrossing> crossings, Func<Vector2, float> ground)
    {
        if (!RoadGrade.Capped(RoadGrade.Configured)) return stretch;
        var (from, to) = stretch;
        if (StairStructures.Between(path[from], path[to], SameRunReach) != null || StairStructures.Carrying(path[from], path[to], OnStairReach).HasValue)
            return stretch;
        float cap = RoadGrade.Configured;
        bool up = ground(path[to]) >= ground(path[from]);
        int foot = up ? from : to, step = up ? -1 : 1;
        // The approach runs back to the structure before it: the stretch's neighbour, a crossing, or the path's end.
        int stop = up ? Mathf.Max(0, before) : Mathf.Min(path.Count - 1, after);
        bool neighbour = up ? before >= 0 : after < path.Count;
        foreach (RoadCrossing c in crossings)
        {
            if (up && c.ToIndex <= foot && c.ToIndex > stop) { stop = c.ToIndex; neighbour = true; }
            if (!up && c.FromIndex >= foot && c.FromIndex < stop) { stop = c.FromIndex; neighbour = true; }
        }
        if (ApproachUnder(path, foot, step, stop, ground, cap) <= ApproachFill) return stretch;
        for (int j = foot + step; up ? j > stop && j >= 1 : j < stop && j <= path.Count - 2; j += step)
        {
            if (neighbour && Along(path, j, stop) < StoneGap) break;
            if (ApproachUnder(path, j, step, stop, ground, cap) <= ApproachFill) return up ? (j, to) : (from, j);
        }
        return stretch;
    }

    /// <summary>Metres along <paramref name="path"/> between two of its indices.</summary>
    internal static float Along(List<Vector2> path, int i, int j)
    {
        float along = 0f;
        for (int k = Mathf.Min(i, j) + 1; k <= Mathf.Max(i, j); k++) along += Vector2.Distance(path[k - 1], path[k]);
        return along;
    }

    /// <summary>Starts refused for a staircase onto a place's landing this generation (a road's end, or a point cut back along
    /// it): the road to the foot was built as a ridge or a trench (<see cref="RidgeToFoot"/>).</summary>
    public static int RidgesToFeet;

    /// <summary>
    /// How far the road arriving at a staircase's foot may stand over both its own ground and the ground on both sides (a
    /// ridge), or under them (a trench): <see cref="ApproachFill"/>, the most fill the road held to a foot may stand on. A
    /// bench across a slope, over the ground below it and at or under the ground above it, is neither, nor a road at its
    /// ground along a crest or down a gully. Further, it is a ridge to the foot: the road to the staircase onto the landing of
    /// StoneTowerRuins05 at -4801,1727 stood 1.4-5.3 m over its ground and 2-3 m over both sides for 15 m (it looks awful,
    /// not worth the trade off).
    /// </summary>
    public const float FootRidge = ApproachFill;

    /// <summary>The shortest stretch of the road to a staircase's foot that is a ridge or a trench (<see cref="RidgeToFoot"/>):
    /// a road's width. Shorter, it is a hump across the road, not a ridge along it.</summary>
    public const float FootRidgeLength = 4f;

    /// <summary>What a start refused for the road to its foot (<see cref="RidgeToFoot"/>) is refused as.</summary>
    public const string RidgeToFootRefusal = "the road to its foot is built";

    /// <summary>
    /// The ridge or trench the road arriving at a staircase's foot is built on within <see cref="ApproachReach"/> of it (the
    /// stretch its end ramp holds to the foot's height): where <paramref name="points"/> at <paramref name="heights"/> stand
    /// over their ground and both sides, or under them, by more than <see cref="FootRidge"/> for <see cref="FootRidgeLength"/>
    /// or more (<see cref="RoadRidges.Built"/>); null where they do not. The foot is at the points' end, or at their start
    /// (<paramref name="footAtStart"/>). The one test of the road to a foot: a staircase is not started from a road built so,
    /// but from further back along it where the road meets its ground, or not at all.
    /// </summary>
    internal static RoadRidges.Stretch? RidgeToFoot(IReadOnlyList<Vector2> points, IReadOnlyList<float> heights, bool footAtStart,
        Func<Vector2, float> ground)
    {
        var near = new List<Vector2>();
        var nearHeights = new List<float>();
        int n = Mathf.Min(points.Count, heights.Count);
        float walked = 0f;
        for (int k = 0; k < n; k++)
        {
            int i = footAtStart ? k : n - 1 - k;
            if (k > 0) walked += Vector2.Distance(near[near.Count - 1], points[i]);
            if (walked > ApproachReach) break;
            near.Add(points[i]);
            nearHeights.Add(heights[i]);
        }
        List<RoadRidges.Stretch> found = RoadRidges.Find(near, nearHeights, ground, FootRidgeLength, FootRidge, built: true);
        return found.Count > 0 ? found[0] : null;
    }

    /// <summary>Why a start is refused for the road to its foot (<see cref="RidgeToFoot"/>).</summary>
    internal static string RidgeRefusal(RoadRidges.Stretch stretch) => FormattableString.Invariant(
        $"{RidgeToFootRefusal} {(stretch.Kind == RoadRidges.Kind.Ridge ? "on a ridge" : "in a trench")} ({stretch.Worst:F1} m for {stretch.Length:F0} m at {stretch.Points[0].x:F1}, {stretch.Points[0].y:F1})");

    /// <summary>
    /// The index of the nearest point of <paramref name="path"/> back from its end at a placed place (its start where
    /// <paramref name="atStart"/>) that the road climbs to on its ground, as <see cref="StartLower"/> moves a road staircase's
    /// foot: its ground back along the road within <see cref="ApproachFill"/> of the line climbing to it at the grade cap
    /// (<see cref="ApproachUnder"/>). Not past the road's nearest structure (<paramref name="structures"/>: a staircase's or
    /// crossing's index nearest that end, -1 for none) nor within <see cref="StoneGap"/> of it, onto the path's other end, nor
    /// further along the road than <paramref name="reach"/>. -1 where no such point is, or the end is one already.
    /// </summary>
    internal static int LowerEnd(List<Vector2> path, bool atStart, int structures, Func<Vector2, float> ground, float reach)
    {
        if (!RoadGrade.Capped(RoadGrade.Configured) || path.Count < 3) return -1;
        float cap = RoadGrade.Configured;
        int foot = atStart ? 0 : path.Count - 1, step = atStart ? 1 : -1;
        int stop = structures >= 0 ? structures : atStart ? path.Count - 1 : 0;
        if (ApproachUnder(path, foot, step, stop, ground, cap) <= ApproachFill) return -1;
        for (int j = foot + step; atStart ? j < stop && j <= path.Count - 2 : j > stop && j >= 1; j += step)
        {
            if (Along(path, j, foot) > reach || (structures >= 0 && Along(path, j, stop) < StoneGap)) break;
            if (ApproachUnder(path, j, step, stop, ground, cap) <= ApproachFill) return j;
        }
        return -1;
    }

    /// <summary>Staircase feet carried on down to a crossing's end a short stretch of road away this generation
    /// (<see cref="EndUnderFoot"/>).</summary>
    public static int FeetLoweredToEnds;

    /// <summary>How far back along the road from a foot its ground is read for LowerFoot.</summary>
    public const float FootBackReach = 5f;

    /// <summary>How far back along its road a staircase's foot (or top) judges the road's direction of travel from: one owner
    /// for road staircases and approaches. The last search step alone (one of 16 headings) can point well off the road as
    /// laid; a much longer reach reads round a bend the road has already made.</summary>
    public const float TravelReach = 8f;

    /// <summary>The flight's heading this many stair pieces up from its foot: past any swing its first
    /// pieces make onto the road's heading (Ruin1 -> Hildir_cave: 247.5, 270, then 315).</summary>
    public const int SettledPieces = 3;

    /// <summary>
    /// The structure's end of a crossing (StructureEnds) a staircase's <paramref name="foot"/>, at
    /// <paramref name="footHeight"/>, stands over by more than the road between can climb, with its index on the path, or
    /// null. The road from the end runs level at the end's height over its last StructureEnds.ApronLength (and on under
    /// its joining piece, RoadSpatialGrid.LevelledEnds), then within the grade cap; a stretch shorter than
    /// StructureEnds.StubLength is all one level landing (within StructureEnds.StepTolerance). Measured along
    /// <paramref name="path"/> from <paramref name="footIndex"/>, stepping by <paramref name="step"/>, to the end's foot,
    /// wherever the end's way of meeting the road (StructureEnds.MeetEnds) put it. The staircase is carried on down to it.
    /// </summary>
    internal static (int index, StructureEnds.End end)? EndUnderFoot(List<Vector2> path, Vector2 foot, int footIndex, int step,
        IReadOnlyList<RoadCrossing> crossings, WorldGenerator world, float footHeight)
    {
        float cap = RoadGrade.Capped(RoadGrade.Configured) ? RoadGrade.Configured : float.PositiveInfinity;
        float Climbs(float run) => run < StructureEnds.StubLength ? StructureEnds.StepTolerance
            : Mathf.Max(StructureEnds.StepTolerance, cap * (run - StructureEnds.ApronLength));
        float run = Vector2.Distance(foot, path[footIndex]);
        for (int i = footIndex; i >= 0 && i < path.Count; i += step)
        {
            if (i != footIndex) run += Vector2.Distance(path[i - step], path[i]);
            // No end stands under the sea: past where the road could climb from there, none is too low.
            if (Climbs(run) >= footHeight - RoadConstants.SeaLevel) return null;
            foreach (RoadCrossing c in crossings)
            {
                if ((step < 0 ? c.ToIndex : c.FromIndex) != i) continue;
                var (near, far) = BridgeStructures.Ends(c, world);
                StructureEnds.End end = step < 0 ? far : near;
                if (end.IsStructure && footHeight - end.MeetHeight > Climbs(run)) return (i, end);
            }
        }
        return null;
    }

    /// <summary>
    /// A road staircase over path[<paramref name="from"/>..<paramref name="to"/>] laid again from its foot at
    /// <paramref name="end"/>'s height (<see cref="EndUnderFoot"/>), on the ground the road levels to that height between the
    /// end's foot and the staircase's (<paramref name="foot"/>, a road's width across): the flight starts at the road's
    /// height where it starts. Null where no such layout is usable.
    /// </summary>
    private static StairPlan? FromTheEndsHeight(List<Vector2> path, int from, int to, StairKit kit, Func<float, float, float> ground,
        StructureEnds.End end, Vector2 foot, WorldGenerator world, List<RoadStairSpan> spans)
    {
        Vector2 a = path[from], b = path[to];
        float Levelled(float x, float z)
        {
            float g = ground(x, z);
            return DistanceToSegment(new Vector2(x, z), end.Meet, foot) <= RoadNetworkGenerator.RoadWidth / 2f ? Mathf.Min(g, end.MeetHeight) : g;
        }
        var corridor = new List<Vector2> { a };
        corridor.AddRange(path.GetRange(from, to - from + 1));
        corridor.Add(b);
        float arriving = StairLayout.Bearing(from > 0 ? path[from - 1] : a, from > 0 ? a : path[from + 1]);
        float leaving = StairLayout.Bearing(to + 1 < path.Count ? b : path[to - 1], to + 1 < path.Count ? path[to + 1] : b);
        RoadEndRing? ring = FootRingOf(path, from, to, a, b, Levelled, end.MeetHeight);
        StairPlan plan = Plan(a, b, kit, Levelled, arriving, leaving, corridor, footHeight: end.MeetHeight,
            facing: FootFacing(path, from, to, Levelled), topFacing: TopFacing(path, from, to, Levelled), ring: ring);
        if (!Usable(plan, path, from, to, world, spans, null, Levelled))
            plan = Plan(a, b, kit, Levelled, null, null, corridor, footHeight: end.MeetHeight, ring: ring);
        bool ok = Usable(plan, path, from, to, world, spans, null, Levelled);
        return ok && !InExcludedZone(plan) ? plan : null;
    }

    /// <summary>The point <paramref name="reach"/> metres along <paramref name="path"/> from index
    /// <paramref name="from"/>, stepping by <paramref name="step"/> (+1 or -1); its far end if shorter.</summary>
    internal static Vector2 Back(List<Vector2> path, int from, int step, float reach)
    {
        Vector2 at = path[from];
        for (int i = from + step; i >= 0 && i < path.Count; i += step)
        {
            float d = Vector2.Distance(at, path[i]);
            if (d >= reach) return Vector2.Lerp(at, path[i], reach / d);
            reach -= d;
            at = path[i];
        }
        return at;
    }

    /// <summary>The climbing heading of the <see cref="SettledPieces"/>th stair nearest <paramref name="foot"/>
    /// (a stair piece faces down its flight), or the plan's start heading where it has fewer.</summary>
    internal static float SettledHeading(StairPlan plan, Vector2 foot)
    {
        var stairs = plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair)
            .OrderBy(p => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), foot)).Take(SettledPieces).ToList();
        if (stairs.Count < SettledPieces) return plan.StartHeading;
        return StairLayout.Wrap(stairs[stairs.Count - 1].YawDegrees + 180f);
    }

    /// <summary>How far round a foot <see cref="RoadGoesOnPast"/> looks for another road.</summary>
    public const float SpurReach = 6f;

    /// <summary>
    /// Whether a road other than the one arriving at <paramref name="end"/> (along <paramref name="path"/> from index
    /// <paramref name="foot"/>, stepping <paramref name="step"/> away from the staircase) lies within <see cref="SpurReach"/>
    /// of the foot, any way round: the road does not end at the foot but goes on past it, a junction or a through road the
    /// staircase is a spur off. Its ring keeps the clear lane both ways (<see cref="RoadEndRing.Through"/>). Only painted road
    /// goes on: a start pad levelled under a staircase's first pieces (StartPads, unpainted) is no road, its own nor another's
    /// (read once its road was stored, its own pad 4.6 m out toward its stairs made a road's plain end through).
    /// </summary>
    internal static bool RoadGoesOnPast(Vector2 end, List<Vector2> path, int foot, int step)
    {
        var own = new List<Vector2>();
        for (int i = foot; i >= 0 && i < path.Count; i += step) own.Add(path[i]);
        return RoadGoesOnPast(end, own);
    }

    /// <summary><see cref="RoadGoesOnPast(Vector2, List{Vector2}, int, int)"/> for a road whose points walking back from its
    /// end are <paramref name="back"/> (its own road: those a little past the ring are read). Given
    /// <paramref name="straightOn"/>, the ring at that end, only the headings in its straight-on arc are read
    /// (<see cref="RoadEndRing.StraightOn"/>): the carry's own check (RefusedUnplanned), deliberately straight on only, as
    /// decided for the carry: a road carried onto a staircase is looked along mostly straight to its first step, so it goes
    /// on past its new end only straight ahead. A foot laid fresh (its ring's Through) still reads every heading.</summary>
    internal static bool RoadGoesOnPast(Vector2 end, IReadOnlyList<Vector2> back, RoadEndRing? straightOn = null)
    {
        var own = new List<Vector2>();
        float walked = 0f;
        foreach (Vector2 p in back)
        {
            if (own.Count > 0) walked += Vector2.Distance(own[own.Count - 1], p);
            own.Add(p);
            if (walked > SpurReach + 8f) break;
        }
        if (own.Count == 0) own.Add(end);
        bool Own(Vector2 q)
        {
            if (own.Count == 1) return (q - own[0]).sqrMagnitude < OwnRoadReach * OwnRoadReach;
            for (int i = 1; i < own.Count; i++)
                if (DistanceToSegment(q, own[i - 1], own[i]) < OwnRoadReach) return true;
            return false;
        }
        if (!RoadSpatialGrid.IsInitialized) return false;
        for (int k = 0; k < 16; k++)
        {
            if (straightOn != null && !straightOn.Value.StraightOn(k * StairLayout.HeadingStep)) continue;
            Vector2 p = end + StairLayout.Forward(k * StairLayout.HeadingStep) * SpurReach;
            if (RoadSpatialGrid.TryGetRoadWithin(p, RoadNetworkGenerator.RoadWidth / 2f, out _, q => !Own(q), painted: true)) return true;
        }
        return false;
    }

    /// <summary>Road points this near the arriving road's line are its own (<see cref="RoadGoesOnPast"/>).</summary>
    private const float OwnRoadReach = 3f;

    /// <summary>
    /// The ring at a road staircase's foot (<see cref="RoadEndRing"/>) where its road arrives there: at path[<paramref name="foot"/>],
    /// the road's travel into it read from <paramref name="beyond"/> (<see cref="RoadTravel"/>), at <paramref name="height"/>,
    /// through where another road goes on past it (<see cref="RoadGoesOnPast(Vector2, List{Vector2}, int, int)"/>).
    /// </summary>
    internal static RoadEndRing FootRing(List<Vector2> path, int foot, int beyond, float height) =>
        new(path[foot], RoadTravel(path, foot, beyond, into: true), height, RoadGoesOnPast(path[foot], path, foot, beyond - foot));

    /// <summary>
    /// The ring at the foot of a staircase laid over path[<paramref name="from"/>..<paramref name="to"/>] from
    /// <paramref name="a"/> to <paramref name="b"/> (the foot the lower of the two on <paramref name="ground"/>, as
    /// StairLayout.Build climbs), where that foot is the path's point and the road arrives there; null where it is not (a
    /// foot meeting another staircase, or at the path's end).
    /// </summary>
    internal static RoadEndRing? FootRingOf(List<Vector2> path, int from, int to, Vector2 a, Vector2 b, Func<float, float, float> ground,
        float? height = null)
    {
        bool reversed = ground(b.x, b.y) < ground(a.x, a.y);
        int foot = reversed ? to : from, beyond = reversed ? to + 1 : from - 1;
        Vector2 at = reversed ? b : a;
        if (beyond < 0 || beyond >= path.Count || Vector2.Distance(at, path[foot]) > 0.01f) return null;
        return FootRing(path, foot, beyond, height ?? ground(at.x, at.y));
    }

    /// <summary>
    /// Where the road ends for a staircase whose foot stands at <paramref name="foot"/>: the centre of the ring it set off from
    /// (<see cref="RoadEndRing"/>), moved with the foot where the foot was moved since (lowered, carried on down to a crossing's
    /// end): the foot less its offset from the ring's centre. A foot laid without a ring (searched down from a crest) is met
    /// from straight behind it, the ring's radius back along its first heading. The road's end and the foot are a ring's radius
    /// apart: its first step clips the road's end.
    /// </summary>
    internal static Vector2 RoadEndAt(StairPlan plan, Vector2 foot) =>
        plan.Ring is RoadEndRing ring && plan.RingStart is Vector2 start ? foot - (start - ring.Centre)
        : foot - StairLayout.Forward(plan.StartHeading) * new RoadEndRing(foot, plan.StartHeading, 0f).Radius;

    /// <summary>
    /// Where the road ends at a staircase's end the staircase itself decided (its top, or a foot searched down to the road):
    /// at <paramref name="at"/> (the top's exit edge, or the foot), leaving the staircase on <paramref name="outward"/>, the road
    /// going on toward <paramref name="road"/> (its point <see cref="TravelReach"/> along) at <paramref name="height"/>. The
    /// centre of a ring that end stands on (RoadEndRing): a ring's radius out from it, squarely (straight on) where the road
    /// going on from there keeps out of the staircase, else turned up to 67.5 degrees toward the road, the least turn that
    /// does; straight on where none does (the staircase is then judged across its road once the road is planned).
    /// </summary>
    internal static Vector2 RoadEndPast(StairPlan plan, Vector2 at, float outward, Vector2 road, float height, bool top)
    {
        float radius = new RoadEndRing(at, outward, height).Radius;
        float toRoad = RoadEndRing.Turn(StairLayout.Bearing(at, road), outward);
        foreach (int k in new[] { 0, 1, -1, 2, -2, 3, -3 }.OrderBy(k => Mathf.Abs(k * StairLayout.HeadingStep - toRoad)).ThenBy(k => Mathf.Abs(k)))
        {
            Vector2 centre = at + StairLayout.Forward(outward + k * StairLayout.HeadingStep) * radius;
            if ((road - centre).sqrMagnitude < 0.01f) continue;
            if (!new RoadEndRing(centre, StairLayout.Bearing(road, centre), height).Intrudes(plan, top)) return centre;
        }
        return at + StairLayout.Forward(outward) * radius;
    }

    /// <summary>A planned road's end this near a staircase's end meets it (<see cref="SpanAcrossItsRoad"/>).</summary>
    public const float RoadMeetsEnd = 1f;

    /// <summary>
    /// The ring at a road's end where a planned piece of <paramref name="plans"/> ends at <paramref name="at"/> (within
    /// <see cref="RoadMeetsEnd"/>): centred on the piece's end, the road's travel into it and its height there as planned
    /// (<see cref="ArrivingTravel(List{Vector2}, Vector2, bool)"/>), through where another road goes on past; null where none
    /// ends there.
    /// </summary>
    internal static RoadEndRing? PlannedRing(IReadOnlyList<RoadSpatialGrid.PlannedPath> plans, Vector2 at)
    {
        foreach (RoadSpatialGrid.PlannedPath piece in plans)
        {
            int n = piece.Points.Count;
            if (n < 2) continue;
            bool atStart = Vector2.Distance(piece.Points[0], at) <= RoadMeetsEnd;
            if (!atStart && Vector2.Distance(piece.Points[n - 1], at) > RoadMeetsEnd) continue;
            int i = atStart ? 0 : n - 1;
            var back = new List<Vector2>();
            for (int k = i; k >= 0 && k < n; k += atStart ? 1 : -1) back.Add(piece.Points[k]);
            return new RoadEndRing(piece.Points[i], ArrivingTravel(piece.Points, piece.Points[i], atStart), piece.Heights[i],
                RoadGoesOnPast(piece.Points[i], back));
        }
        return null;
    }

    /// <summary>
    /// Which end of a road staircase crosses the road the planner built to it, "foot" or "top", or null: each end a planned
    /// piece of <paramref name="plans"/> ends at (<see cref="PlannedRing"/>; an end meeting another staircase has none), judged
    /// by its ring (<see cref="RoadEndRing.Intrudes(StairPlan, out string, bool, float)"/>) with the road's travel and height there as
    /// planned, the one reading of the road at a staircase's end for road staircases and approaches alike. Read from the
    /// search's waypoints, a cell apart, the road's travel was 26.6 or 45 degrees off the road as built, both ways (RoadSeedA
    /// -7999.9,-2358.6).
    /// </summary>
    internal static string? SpanAcrossItsRoad(RoadStairSpan span, IReadOnlyList<RoadSpatialGrid.PlannedPath> plans) =>
        SpanAcrossItsRoad(span, plans, out _);

    /// <summary><see cref="SpanAcrossItsRoad(RoadStairSpan, IReadOnlyList{RoadSpatialGrid.PlannedPath})"/>, with the piece that
    /// comes into the ring (log).</summary>
    internal static string? SpanAcrossItsRoad(RoadStairSpan span, IReadOnlyList<RoadSpatialGrid.PlannedPath> plans, out string detail)
    {
        detail = "";
        bool reversed = span.Plan.Reversed;
        foreach (var (at, end) in new[] { (reversed ? span.ToPoint : span.FromPoint, "foot"), (reversed ? span.FromPoint : span.ToPoint, "top") })
            if (PlannedRing(plans, at) is RoadEndRing ring && ring.Intrudes(span.Plan, out detail, top: end == "top", RoadEndRing.PlannedMargin)) return end;
        return null;
    }

    /// <summary>Refused for crossing their road this generation (<see cref="RoadEndRing.Intrudes(StairPlan, out string, bool, float)"/>):
    /// road staircases at their foot (of them merged over two stretches) and at their top, judged on the planned road
    /// (<see cref="SpanAcrossItsRoad"/>); starts onto a placed place's landing whose every layout crossed (or was refused
    /// otherwise with one that crossed), and such layouts; staircases at a place refused by its site check
    /// (<see cref="SiteConflict"/>).</summary>
    public static int RoadFeetAcross, MergedFeetAcross, RoadTopsAcross, ApproachStartsAcross, ApproachLayoutsAcross, SiteChecksAcross;

    /// <summary>A road staircase refused for crossing its road at <paramref name="end"/> ("foot" or "top") once the road is
    /// planned: its stretch is the one the search closes to stairs (<see cref="LastFailedStretch"/>), counted and logged.</summary>
    internal static void Refuse(RoadStairSpan span, string end, string detail = "")
    {
        LastFailedStretch = (span.FromIndex, span.ToIndex);
        LastRefusal = Refusal.AcrossItsRoad;
        if (end == "top") System.Threading.Interlocked.Increment(ref RoadTopsAcross);
        else
        {
            System.Threading.Interlocked.Increment(ref RoadFeetAcross);
            if (span.Merged) System.Threading.Interlocked.Increment(ref MergedFeetAcross);
        }
        Vector2 at = end == "top" != span.Plan.Reversed ? span.ToPoint : span.FromPoint;
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(FormattableString.Invariant(
            $"[STAIRS] across its road: road staircase {end}{(span.Merged ? " (merged)" : "")} at ({at.x:F1}, {at.y:F1}), path {span.FromIndex}..{span.ToIndex}, {Laid(span.Plan)}: {detail}"));
    }

    /// <summary>How a staircase met its road's end, for the log: from the ring it was laid against (its centre and the travel
    /// it read), or at an end it decided itself.</summary>
    internal static string Laid(StairPlan plan) => plan.Ring is RoadEndRing ring
        ? FormattableString.Invariant($"laid {(plan.RingStart.HasValue ? "from" : "onto")} its ring at ({ring.Centre.x:F1}, {ring.Centre.y:F1}) on a travel of {ring.Travel:F1}{(ring.Through ? ", through" : "")}")
        : "its ends its own";

    /// <summary>Ends of staircases laid this generation where a road ends at them (road staircases' feet and tops,
    /// approaches' road ends), by the heading they leave the road's end on, off the road's travel, in heading steps (22.5
    /// degrees; the last counts 180): straight on is the first.</summary>
    public static readonly int[] RingEnds = new int[9];

    /// <summary>Ends laid at a road's end on a through road (<see cref="RoadEndRing.Through"/>).</summary>
    public static int RingEndsThrough;

    /// <summary>A staircase end laid at a road's end on <paramref name="ring"/>, leaving it on <paramref name="heading"/> (its
    /// foot's first heading, or the heading its top goes down on) from <paramref name="at"/>: counted in
    /// <see cref="RingEnds"/> and logged, the one tally of how staircases leave their roads.</summary>
    internal static void TallyRing(RoadEndRing ring, float heading, Vector2 at, string end)
    {
        float off = RoadEndRing.Turn(heading, ring.Travel);
        System.Threading.Interlocked.Increment(ref RingEnds[Mathf.Clamp(Mathf.RoundToInt(Mathf.Abs(off) / StairLayout.HeadingStep), 0, 8)]);
        if (ring.Through) System.Threading.Interlocked.Increment(ref RingEndsThrough);
        float arc = RoadEndRing.Turn(StairLayout.Bearing(ring.Centre, at), ring.Travel);
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(FormattableString.Invariant(
            $"[STAIRS] ring {end} at ({ring.Centre.x:F1}, {ring.Centre.y:F1}): {off:F1} off its road (travel {ring.Travel:F1}), arc {arc:F1}, {Vector2.Distance(ring.Centre, at):F2} m out{(ring.Through ? ", through" : "")}"));
    }

    /// <summary>A road staircase joining the network on <paramref name="plans"/>: each end a planned piece ends at, tallied
    /// (<see cref="TallyRing"/>): its foot by its first heading, its top by the heading it goes down on.</summary>
    internal static void TallyRing(RoadStairSpan span, IReadOnlyList<RoadSpatialGrid.PlannedPath> plans)
    {
        StairPlan plan = span.Plan;
        bool reversed = plan.Reversed;
        // Its foot where it stands now (lowered or carried on down, the road's end moved with it), else where it set off.
        Vector2 foot = plan.FootLowered > 0 && plan.Foot is Vector3 f ? new Vector2(f.x, f.z)
            : plan.RingStart ?? (plan.Foot is Vector3 g ? new Vector2(g.x, g.z) : reversed ? span.ToPoint : span.FromPoint);
        if (PlannedRing(plans, reversed ? span.ToPoint : span.FromPoint) is RoadEndRing atFoot) TallyRing(atFoot, plan.StartHeading, foot, "foot");
        if (PlannedRing(plans, reversed ? span.FromPoint : span.ToPoint) is RoadEndRing atTop)
            TallyRing(atTop, StairLayout.Wrap(plan.EndHeading + 180f), new Vector2(plan.End.x, plan.End.z), "top");
    }

    /// <summary>An approach joining the network from a road's end: tallied by the ring it met there (<see cref="TallyRing"/>), its
    /// foot by its first heading, a flight going down from the road by the heading its top goes down on.</summary>
    internal static void TallyRing(StairPlan plan)
    {
        if (plan.Ring is not RoadEndRing ring) return;
        if (plan.RingStart is Vector2 start) TallyRing(ring, plan.StartHeading, start, "approach foot");
        else if (plan.Reversed) TallyRing(ring, StairLayout.Wrap(plan.EndHeading + 180f), new Vector2(plan.End.x, plan.End.z), "approach top");
    }

    /// <summary>The counts of <see cref="RoadFeetAcross"/> and the rest, and of <see cref="RingEnds"/>, for the generation's
    /// summary.</summary>
    internal static string AcrossCounts() => FormattableString.Invariant(
        $"road staircases {RoadFeetAcross} at the foot ({MergedFeetAcross} merged), {RoadTopsAcross} at the top; approach starts {ApproachStartsAcross} ({ApproachLayoutsAcross} layouts); site checks {SiteChecksAcross}; ends laid off their road by 22.5 degree step 0..8: {string.Join("/", RingEnds)} ({RingEndsThrough} on a through road)");

    /// <summary>What a staircase refused for crossing its road is refused as (<see cref="RoadEndRing.Intrudes(StairPlan, out string, bool, float)"/>).</summary>
    internal const string AcrossItsRoad = "across its road";

    /// <summary>How far a flight may swing off its first heading within its first pieces before it is laid again on its own
    /// heading (<see cref="SwingsAtItsFoot"/>).</summary>
    public const float SwingTolerance = 45f;

    /// <summary>Whether a staircase climbing from <paramref name="foot"/> sets off on one heading and
    /// settles on another at least <see cref="SwingTolerance"/> away within its first pieces.</summary>
    internal static bool SwingsAtItsFoot(StairPlan plan, Vector2 foot)
    {
        StairPiece? first = plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair)
            .OrderBy(p => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), foot)).FirstOrDefault();
        if (first == null) return false;
        float swing = StairLayout.Wrap(SettledHeading(plan, foot) - (first.YawDegrees + 180f) + 180f) - 180f;
        return Mathf.Abs(swing) >= SwingTolerance;
    }

    /// <summary>The road's own heading at a staircase's end <paramref name="end"/>, from the road's point
    /// <see cref="TravelReach"/> back along it on the side of <paramref name="beyond"/>: the way it comes
    /// <paramref name="into"/> the end, else the way it leaves it. The one reading of the road's direction for a foot
    /// facing it, a foot set beside it and a top facing it.</summary>
    internal static float RoadTravel(List<Vector2> path, int end, int beyond, bool into)
    {
        Vector2 back = Back(path, end, beyond - end, TravelReach);
        return into ? StairLayout.Bearing(back, path[end]) : StairLayout.Bearing(path[end], back);
    }

    /// <summary>
    /// The road's travel into <paramref name="end"/>, the point an approach staircase sets off from at the road's start
    /// (<paramref name="atStart"/>) or its end: measured <see cref="TravelReach"/> back along the road, as a road
    /// staircase's foot (<see cref="RoadTravel"/>). It was the road's last segment alone.
    /// </summary>
    internal static float ArrivingTravel(List<Vector2> path, Vector2 end, bool atStart)
    {
        if (path.Count < 2) return 0f;
        int at = atStart ? 0 : path.Count - 1;
        var line = new List<Vector2>(path);
        line[at] = end;
        Vector2 back = Back(line, at, atStart ? 1 : -1, TravelReach);
        return StairLayout.Bearing(back, end);
    }

    /// <summary>
    /// <see cref="ArrivingTravel(List{Vector2}, Vector2, bool)"/> read from the road as planned at that end,
    /// <paramref name="piece"/>, where it reaches <paramref name="end"/>; from the search's path where it does not. The search's
    /// waypoints, a cell apart, said the road came into the end at MountainCave02 3489.3,-5760.3 heading 89 where the road as
    /// planned came in at about 138: the staircase there, 44 degrees off by the waypoints, was left in the road's middle and
    /// crossed it.
    /// </summary>
    internal static float ArrivingTravel(List<Vector2> path, RoadSpatialGrid.PlannedPath? piece, Vector2 end, bool atStart)
    {
        if (piece != null && piece.Points.Count >= 2
            && Vector2.Distance(piece.Points[atStart ? 0 : piece.Points.Count - 1], end) <= 0.5f)
            return ArrivingTravel(piece.Points, end, atStart);
        return ArrivingTravel(path, end, atStart);
    }

    /// <summary>Staircase feet given a level apron cut into the bank behind them this generation (StairLayout.CutFootApron).</summary>
    public static int FootAprons;

    /// <summary>Level pads laid where a staircase starts at its road's end this generation (RoadEndRing.PadUnder): at feet,
    /// at tops (a flight going down from the road's end).</summary>
    public static int PadsAtFeet, PadsAtTops;

    /// <summary>Of them, pads with points left out over falling ground (RoadEndRing.PadMostFill).</summary>
    public static int PadsPartial;

    /// <summary>
    /// Why a road with <paramref name="stairs"/> may not be committed for one of their feet, and where; null when none: a
    /// foot laid against its road's end (its ring) where one of the road's own pieces (<paramref name="own"/>) ends, whose
    /// walked pieces there (within the start pad's reach, RoadEndRing.PadReach, but the steps sunk on purpose) all stand more
    /// than RoadEndRing.LevelWithRoad over the road's own height at that end (the staircase stands over its road with no way
    /// onto it), or whose lowest stands more than a rise under it (a pit at its foot under the road's bank). Asked before the
    /// commit with the road's other refusals: the road is refused and searched for as any refused road is, never kept with
    /// the staircase hanging (in game a tower's road staircase's foot stood 6.4 m over the road cut under it at -784,-4432,
    /// its start pad refused and only counted).
    /// </summary>
    internal static string? FootLeftOver(IEnumerable<RoadStairSpan> stairs, IReadOnlyList<RoadSpatialGrid.PlannedPath> own, out Vector2 at,
        Func<float, float, float>? ground = null)
    {
        at = default;
        var all = stairs as IReadOnlyList<RoadStairSpan> ?? stairs.ToList();
        foreach (RoadStairSpan st in all)
        {
            StairPlan plan = st.Plan;
            // No road at its foot (it is where its road begins or ends): stepped off onto the ground, judged by the end reader
            // as laid (StairEnds.Read, on the ground it was laid out on with the roads on it, the road's other staircases
            // meeting it). A foot the reader finds no edge to step off is refused, as a foot over its road is (on RoadSeedD
            // the road from the island's start at 5704,-3576 set off with a staircase on a shore steeper than its flight, its
            // foot left 2.06 m over the ground with no road there).
            if (!st.Existing && st.NoRoadAtFoot && ground != null && FootOffTheGround(st, all, ground) is Vector2 off)
            {
                at = off;
                return FootOverTheGround;
            }
            if (st.Existing || plan.Ring is not RoadEndRing ring || RoadEndRing.MetAtTop(plan)) continue;
            // The road's own end at the ring, and its height there (a staircase met by the next one on a shared floor has none):
            // of its pieces' ends where the staircase still starts (StartReach), the nearest the ring's centre. Read only within
            // the ring's own disc (RoadsEnd), the tower's road above stored its end 4.0 m off the ring it was planned on, 6.4 m
            // under the staircase's foot, and the foot was never refused.
            float? road = null;
            float nearest = float.PositiveInfinity;
            foreach (RoadSpatialGrid.PlannedPath piece in own)
                for (int k = 0; k < piece.Points.Count && piece.Points.Count >= 2; k += piece.Points.Count - 1)
                {
                    float d = Vector2.Distance(piece.Points[k], ring.Centre);
                    if (d > ring.StartReach || d > nearest + 0.01f) continue;
                    road = d < nearest - 0.01f || road is not float r ? piece.Heights[k] : Mathf.Min(r, piece.Heights[k]);
                    nearest = Mathf.Min(nearest, d);
                }
            if (road is not float height) continue;
            float reach = ring.StartReach, lowest = float.PositiveInfinity;
            foreach (StairPiece p in plan.Pieces)
            {
                if (p.Kind == StairPieceKind.Support || p.Sunk) continue;
                if (Vector2.Distance(new Vector2(p.Position.x, p.Position.z), ring.Centre) > reach) continue;
                lowest = Mathf.Min(lowest, StairLayout.Footprint(p).height);
            }
            if (!float.IsPositiveInfinity(lowest) && lowest - height > RoadEndRing.LevelWithRoad)
            {
                at = ring.Centre;
                return FootOverItsRoad;
            }
            // Its lowest walked stair wholly under the road's height there (more than a rise, StairLayout.PieceRise: a foot is
            // sunk a rise on purpose, its slope meeting the road): the end rule's short foot, a pit at the foot with the road's
            // bank standing over it (in game a foot lowered toward the ground back along its road at 4071,5570.9 stood 1.47 m
            // under the road its road was built at there, a 1.0-1.1 m bank a metre in front of it).
            if (!float.IsPositiveInfinity(lowest) && height - lowest > StairLayout.PieceRise)
            {
                at = ring.Centre;
                return FootUnderItsRoad;
            }
        }
        return null;
    }

    /// <summary>Why <see cref="FootLeftOver"/> refuses a road: a foot over its road, or under it, or, with no road there, a
    /// foot no edge of which steps off onto the ground.</summary>
    internal const string FootOverItsRoad = "a staircase's foot over its road", FootUnderItsRoad = "a staircase's foot under its road",
        FootOverTheGround = "a staircase's foot with no road, over the ground";

    /// <summary>Where <paramref name="st"/>'s foot, with no road there, gaps by the end reader (StairEnds.Read: no edge of its
    /// last walked pieces steps off onto <paramref name="ground"/>, the ground a road's staircase is laid out on
    /// (PlanningGround), as its own earthwork leaves it (its cuts, a foot's apron among them: each lowers the ground to its
    /// height within StairLayout.CutRadius, as the network's cut-only points are written) and with the network's roads on it,
    /// nor onto another of <paramref name="all"/>'s staircases): the foot's last stair's exit edge. Null where a walker steps
    /// off it. Read without its cuts, a foot onto the level apron cut for it at the island's start on RoadSeedA was refused.</summary>
    internal static Vector2? FootOffTheGround(RoadStairSpan st, IReadOnlyList<RoadStairSpan> all, Func<float, float, float> ground)
    {
        StairRun run = StairRun.FromPlan(st.FromPoint, st.ToPoint, Width, st.Plan, st.Kit);
        var others = new List<StructurePiece>();
        foreach (RoadStairSpan other in all)
            if (!ReferenceEquals(other, st)) others.AddRange(StairRun.FromPlan(other.FromPoint, other.ToPoint, Width, other.Plan, other.Kit).Pieces);
        List<Vector3> cuts = st.Plan.Cuts;
        float Cut(float x, float z)
        {
            float g = ground(x, z);
            foreach (Vector3 c in cuts)
                if (c.y < g && (c.x - x) * (c.x - x) + (c.z - z) * (c.z - z) <= StairLayout.CutRadius * StairLayout.CutRadius) g = c.y;
            return g;
        }
        foreach (StairEnds.End end in StairEnds.Read(run, StairEnds.WithRoads(Cut), others))
            if (!end.Top && end.Verdict == StairEnds.Verdict.Gap)
                return end.Edge is Vector3 e ? new Vector2(e.x, e.z) : st.Plan.Reversed ? st.ToPoint : st.FromPoint;
        return null;
    }

    /// <summary>Pads not laid this generation, by why (<see cref="StartPads"/>).</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> PadsRefused = new();

    /// <summary>How far from a pad's road end one of its road's own ends must stand for the road to meet the staircase there.</summary>
    public const float PadMeetsEnd = 1f;

    /// <summary>
    /// The level pads where <paramref name="stairs"/> start at the ends of <paramref name="own"/> (RoadEndRing.PadUnder, the
    /// ring's), as levelled road points never painted, read against the network as it stands before <paramref name="own"/>
    /// is stored: at the ring a staircase was laid against, and at a road staircase's top where its road starts again. A pad
    /// is laid only where one of the road's own pieces ends at its road's end, within a step of its height
    /// (a staircase met by the next one on a shared floor has no road there), and none of it lies in water, on another
    /// road at another height or on another staircase's flight; its points inside a place's footprint are left out (the
    /// terrain writer leaves a place's ground alone). Each refusal is counted by why (<see cref="PadsRefused"/>).
    /// </summary>
    internal static List<RoadSpatialGrid.PlannedPath> StartPads(IReadOnlyList<RoadSpatialGrid.PlannedPath> own, IReadOnlyList<RoadStairSpan> stairs,
        WorldGenerator? world, string? label = null)
    {
        var pads = new List<RoadSpatialGrid.PlannedPath>();
        if (world == null || stairs.Count == 0) return pads;
        float cap = RoadNetworkGenerator.ShapedLimit;
        float Ground(float x, float z) => BiomeBlendedHeight.GetBlendedHeight(x, z, world);
        var runs = new List<StairRun>(stairs.Count);
        foreach (RoadStairSpan st in stairs) runs.Add(StairRun.FromPlan(st.FromPoint, st.ToPoint, Width, st.Plan, st.Kit));
        void Refused(RoadEndRing.Pad? pad, RoadEndRing r, string kind, string why)
        {
            PadsRefused.AddOrUpdate(kind, 1, (_, n) => n + 1);
            if (RoadNetworkGenerator.RoadsLog)
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(FormattableString.Invariant(
                    $"[STAIRS] start pad at ({(pad?.End ?? r.Centre).x:F1}, {(pad?.End ?? r.Centre).y:F1}) not laid on {label ?? "(unlabelled)"}: {why}"));
        }
        // Each end of each staircase where its road ends: the ring it was laid against (a road staircase's foot, an
        // approach's road end), and a road staircase's top, on the ring its road starts on again there (RoadEndPast, read
        // as planned: PlannedRing). Unpadded, a top floor on that ring stood over the road's grade carried on past its end
        // (at -4564.2,2167.6 the top 2x2 was 0.37 m over the road, too high to step onto).
        var ends = new List<(int s, RoadEndRing ring, bool top)>();
        for (int s = 0; s < stairs.Count; s++)
        {
            StairPlan plan = stairs[s].Plan;
            if (stairs[s].Existing) continue;
            if (plan.Ring is RoadEndRing laidOn) ends.Add((s, laidOn, RoadEndRing.MetAtTop(plan)));
            if (!RoadEndRing.MetAtTop(plan) && plan.Reached
                && PlannedRing(own, plan.Reversed ? stairs[s].FromPoint : stairs[s].ToPoint) is RoadEndRing atTop)
                ends.Add((s, atTop, true));
        }
        foreach (var (s, ring, top) in ends)
        {
            StairPlan plan = stairs[s].Plan;
            if (RoadEndRing.PadUnder(plan, ring, top, Ground, cap, out string why) is not RoadEndRing.Pad pad)
            {
                Refused(null, ring, why.StartsWith("a cut", StringComparison.Ordinal) ? "past the earthwork cap" : why, why);
                continue;
            }
            bool meets = false;
            foreach (RoadSpatialGrid.PlannedPath piece in own)
            {
                if (piece.Points.Count < 2 || piece.FollowTerrain) continue;
                foreach (int i in new[] { 0, piece.Points.Count - 1 })
                    meets |= Vector2.Distance(piece.Points[i], pad.End) <= PadMeetsEnd
                        && Mathf.Abs(piece.Heights[i] - pad.Height) <= StructureEnds.StepTolerance;
            }
            if (!meets) { Refused(pad, ring, "no road ends there", "no road of its own ends there at its height"); continue; }
            var points = new List<Vector2>();
            var heights = new List<float>();
            string? clash = null;
            for (int i = 0; i < pad.Points.Count && clash == null; i++)
            {
                Vector2 q = pad.Points[i];
                if (RoadSiteProtection.Contains(q)) continue;
                if (!RoadWater.BuiltDry(world, q)) clash = "in water";
                else if (RoadSpatialGrid.TryGetRoadHeightWithin(q, RoadEndRing.PadSpacing, out float road)
                         && Mathf.Abs(road - pad.Heights[i]) > StructureEnds.StepTolerance) clash = "on another road";
                points.Add(q);
                heights.Add(pad.Heights[i]);
            }
            if (clash == null && points.Count > 0)
            {
                var others = new List<StairRun>(runs);
                others.RemoveAt(s);
                if (OnAFlight(others, points, heights)) clash = "on another staircase";
            }
            if (clash != null) { Refused(pad, ring, clash, clash); continue; }
            if (points.Count == 0) continue;
            System.Threading.Interlocked.Increment(ref pad.Top ? ref PadsAtTops : ref PadsAtFeet);
            if (pad.LeftOut > 0) System.Threading.Interlocked.Increment(ref PadsPartial);
            if (RoadNetworkGenerator.RoadsLog)
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(FormattableString.Invariant(
                    $"[STAIRS] start pad {(pad.Top ? "top" : "foot")} at ({pad.End.x:F1}, {pad.End.y:F1}) on {label ?? "(unlabelled)"}: {points.Count} point(s) at {pad.Height:F2}, under {pad.Pieces} walked piece(s), cut {pad.MostCut:F2} m, fill {pad.MostFill:F2} m, {pad.LeftOut} left out over falling ground"));
            pad.Points.Clear(); pad.Points.AddRange(points);
            pad.Heights.Clear(); pad.Heights.AddRange(heights);
            pads.Add(pad.ToPath());
        }
        return pads;
    }

    /// <summary>The way the road comes to the foot of the staircase over path[from..to]: its heading over its last
    /// stretch into the lower end, or null where no road comes to that end.</summary>
    internal static float? FootFacing(List<Vector2> path, int from, int to, Func<float, float, float> ground)
    {
        // StairLayout.Build climbs from the lower end: path[to] when the road comes down the stretch.
        bool reversed = ground(path[to].x, path[to].y) < ground(path[from].x, path[from].y);
        int foot = reversed ? to : from, beyond = reversed ? to + 1 : from - 1;
        if (beyond < 0 || beyond >= path.Count) return null;
        return RoadTravel(path, foot, beyond, into: true);
    }

    /// <summary>Staircase layouts (road staircases and approaches) laid any way at their foot, none facing the road (nor
    /// set off back beside it within StairLayout.MostSetOffBack) reaching: layouts tried, not staircases laid.</summary>
    public static int FeetFacedFreely;
}
