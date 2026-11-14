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
    /// How far the road's ground back along its approach may lie under the line climbing to its staircase's foot as its road
    /// is built to climb there (RoadPitches.ClimbToFoot: its level landing, then the grade cap). Further, and the road held to the foot's height stands on an embankment there: the grade limiter keeps it on that
    /// line all the way down, over the ground at the bottom of the climb (a road climbing to a staircase's foot at
    /// -4764.9,1737.7 stood 6 m over a gully, the ground lower on both sides).
    /// </summary>
    public const float ApproachFill = 1f;

    /// <summary>How far back along its road a foot's approach is read (<see cref="ApproachUnder"/>): the stretch the road's end
    /// ramp bends to the foot's height (RoadEndpointRamp.RampLength).</summary>
    public const float ApproachReach = RoadEndpointRamp.RampLength;

    /// <summary>
    /// How far the ground of the road arriving at path[<paramref name="foot"/>], walked from it by <paramref name="step"/>
    /// (+1 or -1) no further than index <paramref name="stop"/>, lies under the line climbing to the foot's ground as the road
    /// is built to climb there (RoadPitches.ClimbToFoot: its level landing, then <paramref name="cap"/>): the most fill the
    /// road held to the foot stands on. Read every metre up to <see cref="ApproachReach"/>.
    /// </summary>
    internal static float ApproachUnder(List<Vector2> path, int foot, int step, int stop, Func<Vector2, float> ground, float cap) =>
        Approach(path, foot, step, stop, ground, cap, leaving: false);

    /// <summary>
    /// As <see cref="ApproachUnder"/>, for the road leaving a staircase's top at path[<paramref name="top"/>], walked from it
    /// by <paramref name="step"/>: how far its ground stands over the line climbing on from the top's ground as the road is
    /// built to climb there (its level landing, then <paramref name="cap"/>), the deepest cut the road held to the top lies in.
    /// </summary>
    internal static float ApproachOver(List<Vector2> path, int top, int step, int stop, Func<Vector2, float> ground, float cap) =>
        Approach(path, top, step, stop, ground, cap, leaving: true);

    private static float Approach(List<Vector2> path, int end, int step, int stop, Func<Vector2, float> ground, float cap, bool leaving)
    {
        float top = ground(path[end]), walked = 0f, under = 0f;
        int foot = end;
        for (int i = foot + step; step < 0 ? i >= stop : i <= stop; i += step)
        {
            Vector2 a = path[i - step], b = path[i];
            float run = Vector2.Distance(a, b);
            int samples = Mathf.Max(1, Mathf.CeilToInt(run));
            for (int k = 1; k <= samples; k++)
            {
                float d = walked + run * k / samples;
                if (d > ApproachReach) return under;
                float at = ground(Vector2.Lerp(a, b, (float)k / samples));
                under = Mathf.Max(under, leaving ? at - (top + RoadPitches.ClimbToFoot(d, cap)) : top - RoadPitches.ClimbToFoot(d, cap) - at);
            }
            walked += run;
        }
        return under;
    }

    /// <summary>
    /// A stair stretch of <paramref name="path"/> started lower, the staircase taking the height its road cannot: where the
    /// road arriving at its foot (its lower end) cannot climb to it following its ground as it is built to (its ground more
    /// than <see cref="ApproachFill"/> under the line to the foot, <see cref="ApproachUnder"/>), the foot moves back along the
    /// road to the nearest path point whose own approach is within it. More stairs and a lower foot instead of a road on an
    /// embankment. Only to ground lower than the road could climb to the foot from at the cap over the road between
    /// (<see cref="RoadPitches.ClimbToFoot"/>): to ground it could, the staircase would take no height its road cannot, and lays
    /// floors where a road could travel (a foot moved 43 m back over ground climbing 4.5 m was laid as 18 level floors over
    /// 28 m). Not onto the path's ends, past a crossing, nor within <see cref="StoneGap"/> of the stretch before
    /// (<paramref name="before"/>, its last index, -1 for none) or after (<paramref name="after"/>, its first index, the path's
    /// count for none); nor for a run an earlier road laid between the stretch's ends, which is walked as it stands. The stretch
    /// as it was where no such point is.
    /// </summary>
    internal static (int from, int to) StartLower(List<Vector2> path, (int from, int to) stretch, int before, int after,
        IReadOnlyList<RoadCrossing> crossings, Func<Vector2, float> ground)
    {
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
        float height = ground(path[foot]), back = 0f;
        for (int j = foot + step; up ? j > stop && j >= 1 : j < stop && j <= path.Count - 2; j += step)
        {
            if (neighbour && Along(path, j, stop) < StoneGap) break;
            back += Vector2.Distance(path[j - step], path[j]);
            if (ground(path[j]) >= height - RoadPitches.ClimbToFoot(back, cap)) continue;
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

    /// <summary>Starts refused for a staircase onto a location's landing this generation (a road's end, or a point cut back along
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
    /// The index of the nearest point of <paramref name="path"/> back from its end at a placed location (its start where
    /// <paramref name="atStart"/>) that the road climbs to on its ground, as <see cref="StartLower"/> moves a road staircase's
    /// foot: its ground back along the road within <see cref="ApproachFill"/> of the line climbing to it as the road is built
    /// to (<see cref="ApproachUnder"/>). Not past the road's nearest structure (<paramref name="structures"/>: a staircase's or
    /// crossing's index nearest that end, -1 for none) nor within <see cref="StoneGap"/> of it, onto the path's other end, nor
    /// further along the road than <paramref name="reach"/>. -1 where no such point is, or the end is one already.
    /// </summary>
    internal static int LowerEnd(List<Vector2> path, bool atStart, int structures, Func<Vector2, float> ground, float reach)
    {
        if (path.Count < 3) return -1;
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

    /// <summary>Of them, feet met on a stair sunk into the road under them (StairLayout.FootToRoad), and of those the sunk
    /// stairs laid for it (the rest kept their own).</summary>
    public static int FeetSunkAtEnds, SunkStairsLaid;

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
    /// StructureEnds.StubLength is all one level landing (a walker's step of it, StairLayout.Walkable: the lip of a foot over
    /// its road). Measured along
    /// <paramref name="path"/> from <paramref name="footIndex"/>, stepping by <paramref name="step"/>, to the end's foot,
    /// wherever the end's way of meeting the road (StructureEnds.MeetEnds) put it. The staircase is carried on down to it.
    /// </summary>
    internal static (int index, StructureEnds.End end)? EndUnderFoot(List<Vector2> path, Vector2 foot, int footIndex, int step,
        IReadOnlyList<RoadCrossing> crossings, WorldGenerator world, float footHeight)
    {
        float cap = RoadGrade.Configured;
        // What the road between climbs past a walker's step (StairLayout.Walkable): none on a stub, the grade cap past its apron.
        float Climbs(float run) => run < StructureEnds.StubLength ? 0f : Mathf.Max(0f, cap * (run - StructureEnds.ApronLength));
        bool Over(float end, float run) => footHeight - end > Climbs(run) && footHeight > end && !StairLayout.Walkable(footHeight, end);
        float run = Vector2.Distance(foot, path[footIndex]);
        for (int i = footIndex; i >= 0 && i < path.Count; i += step)
        {
            if (i != footIndex) run += Vector2.Distance(path[i - step], path[i]);
            // No end stands under the sea: past where the road could climb from there, none is too low.
            if (!Over(RoadConstants.SeaLevel, run)) return null;
            foreach (RoadCrossing c in crossings)
            {
                if ((step < 0 ? c.ToIndex : c.FromIndex) != i) continue;
                var (near, far) = BridgeStructures.Ends(c, world);
                StructureEnds.End end = step < 0 ? far : near;
                if (end.IsStructure && Over(end.MeetHeight, run)) return (i, end);
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
    private static StairPlan? FromTheEndsHeight(List<Vector2> path, int from, int to, StairKit kit,
        StructureEnds.End end, Vector2 foot, WorldGenerator world, List<RoadStairSpan> spans)
    {
        Vector2 a = path[from], b = path[to];
        LayoutGround levelled = LayoutGround.Levelled(world, end.Meet, foot, end.MeetHeight);
        Func<float, float, float> Levelled = levelled.Read;
        var corridor = new List<Vector2> { a };
        corridor.AddRange(path.GetRange(from, to - from + 1));
        corridor.Add(b);
        float arriving = StairLayout.Bearing(from > 0 ? path[from - 1] : a, from > 0 ? a : path[from + 1]);
        float leaving = StairLayout.Bearing(to + 1 < path.Count ? b : path[to - 1], to + 1 < path.Count ? path[to + 1] : b);
        RoadEndRing? ring = FootRingOf(path, from, to, a, b, Levelled, end.MeetHeight);
        StairPlan plan = Plan(a, b, kit, levelled, arriving, leaving, corridor, footHeight: end.MeetHeight,
            facing: FootFacing(path, from, to, Levelled), topFacing: TopFacing(path, from, to, Levelled), ring: ring);
        if (!Usable(plan, path, from, to, world, spans, null, Levelled))
            plan = Plan(a, b, kit, levelled, null, null, corridor, footHeight: end.MeetHeight, ring: ring);
        bool ok = Usable(plan, path, from, to, world, spans, null, Levelled);
        return ok ? plan : null;
    }

    /// <summary>Staircase feet kept on the shorter flight with their road's landing raised to them this generation
    /// (StairLayout.StairsDownTo, <see cref="LandingRaised"/>), counted as their roads are accepted.</summary>
    public static int FootLandingsRaised;

    /// <summary>
    /// Whether a staircase's road may have its landing raised to a foot at <paramref name="foot"/> (its low edge and height, as
    /// the shorter flight leaves it, StairLayout.StairsDownTo): the road's own earthwork brings the ground there up to the
    /// foot, so the foot steps off level. Asked of the owners that already decide the road's earthwork, at the road's end at the
    /// foot (<see cref="RoadEndAt"/>) and back along the road as far as its ground is read for the foot (<see cref="FootBackReach"/>), each point
    /// at the foot's height: the earthwork cap (RoadNetworkGenerator.PastTerrainLimit against EarthworkLimit, over the ground
    /// before roads), a location's footprint but the road's own location (RoadLocationProtection.BlocksSegment), another road there
    /// (RoadSpatialGrid.TryGetRoadWithin, within <see cref="OnRoad"/>: its own road is not in the grid yet), and ground that
    /// holds a fill: dry (RoadWater.BuiltDry). Where any refuses, the stair is added instead.
    /// </summary>
    internal static bool LandingRaised(StairPlan plan, Vector3 foot, List<Vector2> path, int footIndex, int step, WorldGenerator world,
        RoadLocationProtection.Targets locations)
    {
        var points = new List<Vector2> { RoadEndAt(plan, new Vector2(foot.x, foot.z)) };
        for (float d = 0f; d <= FootBackReach; d += 1f) points.Add(Back(path, footIndex, step, d));
        Vector2? last = null;
        foreach (Vector2 p in points)
        {
            if (RoadNetworkGenerator.PastTerrainLimit(p, foot.y, RoadNetworkGenerator.EarthworkLimit, world)) return false;
            if (RoadLocationProtection.BlocksSegment(last ?? p, p, 0f, locations)) return false;
            if (RoadSpatialGrid.IsInitialized && RoadSpatialGrid.TryGetRoadWithin(p, OnRoad, out _)) return false;
            if (!RoadWater.BuiltDry(world, p)) return false;
            last = p;
        }
        return true;
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
    /// (<see cref="SpanAcrossItsRoad"/>); starts onto a placed location's landing whose every layout crossed (or was refused
    /// otherwise with one that crossed), and such layouts; staircases at a location refused by its location check
    /// (<see cref="LocationConflict"/>).</summary>
    public static int RoadFeetAcross, MergedFeetAcross, RoadTopsAcross, ApproachStartsAcross, ApproachLayoutsAcross, LocationChecksAcross;

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
        $"road staircases {RoadFeetAcross} at the foot ({MergedFeetAcross} merged), {RoadTopsAcross} at the top; approach starts {ApproachStartsAcross} ({ApproachLayoutsAcross} layouts); location checks {LocationChecksAcross}; approach layouts {ApproachLayouts} for roads ({ApproachExpansions} expansions), {TrialApproachLayouts} for approach trials ({TrialApproachExpansions} expansions); ends laid off their road by 22.5 degree step 0..8: {string.Join("/", RingEnds)} ({RingEndsThrough} on a through road)");

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

    /// <summary>Staircase feet with no road past them finished onto the ground this generation (StairLayout.FinishFoot).</summary>
    public static int FeetFinished;

    /// <summary>
    /// A staircase's own foot with no road carried on past it (RoadStairRuns.Lay: finished onto nothing) finished by the end
    /// rule onto the ground past it (StairLayout.FinishFoot, the one owner, in the order a top is finished): each finish kept
    /// only where the end reader finds it finished as its rule chose with a way off (<see cref="FootFinished"/>), on the ground
    /// as its own cuts leave it (the finished ground the end audit reads it on, StairEnds.GroundOf: read on the ground before
    /// them, a foot's stair cut into a bank stood in it), and its added pieces pass the checks the pieces a top's finish adds
    /// pass (<see cref="AddedFits"/>) and keep off kept ground (<see cref="OnKeptGround"/>). Returns the new foot, or null
    /// where none was laid (a foot already finished or lowered, or none kept: as laid).
    /// </summary>
    internal static Vector3? FinishFootOntoNothing(StairPlan plan, List<Vector2> path, int from, int to, WorldGenerator world,
        List<RoadStairSpan> before, Vector2? meet)
    {
        if (plan.FootLowered > 0 || plan.FootFloorsTrimmed > 0 || plan.Foot == null) return null;
        Func<float, float, float> ground = (x, z) => GroundAt(x, z, world);
        int kept = plan.Pieces.Count - plan.SunkPieces;
        StairLayout.FootFinish finish = StairLayout.FinishFoot(plan, ground, Width,
            p => !OnKeptGround(p) && AddedFits(p.Pieces.GetRange(0, p.Pieces.Count - kept), path, from, to, world, before, meet),
            p => FootFinished(p, ground));
        return finish == StairLayout.FootFinish.None ? null : plan.Foot;
    }

    /// <summary>Whether <paramref name="plan"/>'s foot is finished by the end reader (StairEnds.Read on a run of its pieces),
    /// on <paramref name="ground"/> as the plan's own cuts leave it, as the end audit reads the finished ground: the terrain's
    /// whole-metre vertices cut (RoadTerrainModifier.GroundAfterCuts) and the game's mesh between them (BuriedPieces.MeshHeight,
    /// as StairEnds.GroundOf): a way off it (not a miss nor a gap) and finished as its rule chose (StairEnds.Unfinished), the
    /// verdict the end audit gives it. Read with the cuts at each point instead, a stair cut into a bank at a foot read
    /// 1.12 m short where the audit's mesh read it 0.9.</summary>
    internal static bool FootFinished(StairPlan plan, Func<float, float, float> ground)
    {
        StairEnds.End? foot = FootEnd(plan, AfterItsCuts(plan, ground));
        return foot != null && StairEnds.Finished(foot);
    }

    /// <summary><paramref name="ground"/> as <paramref name="plan"/>'s own cuts leave it, as the end audit's mesh reads it
    /// (<see cref="FootFinished"/>).</summary>
    private static Func<float, float, float> AfterItsCuts(StairPlan plan, Func<float, float, float> ground) =>
        (x, z) => BuriedPieces.MeshHeight(x, z, (i, k) => RoadTerrainModifier.GroundAfterCuts(ground(i, k), plan.Cuts, i, k));

    /// <summary>Whether <paramref name="plan"/>'s own foot with no road past it (RoadStairRuns.Lay: finished onto nothing) is
    /// stepped off onto a road all the same, by the end reader on the ground <see cref="FootFinished"/> reads (StairEnds.End.OntoAnotherRoad):
    /// the road it joins at the path's end, a road not its own, with no ring of its own (RoadEndRing) between them. The
    /// verdict the road's final check gives that foot (<see cref="NoWayOff"/>, <see cref="FootOntoAnotherRoad"/>), read as the
    /// staircase is laid, so its stretch is searched again instead of the road refused.</summary>
    internal static bool OntoAnotherRoad(StairPlan plan, Func<float, float, float> ground) =>
        FootEnd(plan, AfterItsCuts(plan, ground)) is { OntoAnotherRoad: true };

    /// <summary>A plan's foot read by the end reader (StairEnds.Read on a run of its pieces), or null where it has no walked
    /// piece; as <see cref="TopEnd"/> reads its top.</summary>
    internal static StairEnds.End? FootEnd(StairPlan plan, Func<float, float, float> ground)
    {
        StairPiece? foot = plan.Pieces.Find(p => p.Kind != StairPieceKind.Support);
        if (foot == null) return null;
        StairLayout.TerminalExit? exit = StairLayout.Terminal(plan);
        Vector2 top = exit != null ? new Vector2(exit.Edge.x, exit.Edge.z) : new Vector2(plan.End.x, plan.End.z);
        var run = StairRun.FromPlan(new Vector2(foot.Position.x, foot.Position.z), top, Width, plan);
        return StairEnds.Read(run, ground).Find(e => !e.Top);
    }

    /// <summary>Level pads laid where a staircase starts at its road's end this generation (RoadEndRing.PadUnder): at feet,
    /// at tops (a flight going down from the road's end).</summary>
    public static int PadsAtFeet, PadsAtTops;

    /// <summary>Of them, pads with points left out over falling ground (RoadEndRing.PadMostFill).</summary>
    public static int PadsPartial;

    /// <summary>
    /// The one finished-connection verdict of a road's staircases (<paramref name="stairs"/>) onto the road's own pieces
    /// (<paramref name="own"/>, as finished: a landing laid at an end, RoadPitches.LandEnd, included): why the road may not be
    /// committed for one of their ends, and where; null when none. Asked of every road before anything of it is stored, a
    /// generated one, a marked manual one and an approach trial alike (RoadNetworkGenerator.RoadRefusal, the one list): the road is
    /// refused and searched for as any refused road is, never kept with a staircase hanging (in game a tower's road
    /// staircase's foot stood 6.4 m over the road cut under it at -784,-4432, its start pad refused and only counted).
    /// <list type="bullet">
    /// <item>An end of a staircase it lays (<paramref name="finished"/>, the runs as the road would store them) with no way off
    /// it by the one end reader (StairEnds.Read: a gap or a miss), read on the ground <paramref name="groundOf"/> gives it: in
    /// the one list, the finished ground as the road will stand once stored (StairEnds.GroundOf inside
    /// RoadNetworkGenerator.AsWritten: its own earthwork, cuts, pads and the roads it raises, its own surface under a walker),
    /// the ground the staircase-end audit reads it on once it is (StairEnds.AuditAll), so the two agree. Its other staircases
    /// and the network's meeting it end to end are joined to it (StairStructures.MetAtEnds). Read on the ground its layout was
    /// laid out on, a top whose road on was held down 2.4 m to the road beside it (RoadSpatialGrid.HoldToNetwork) at
    /// 6518.7,-1452.7 (offline) was kept, its floor left standing over the road's cut.</item>
    /// <item>A foot laid against its road's end (its ring) whose walked pieces there (within the start pad's reach,
    /// RoadEndRing.StartReach, but the steps sunk on purpose) all stand more than a walker's step over the road's own height at
    /// that end and are not met on a stair sunk under the foot (StairLayout.FootMeets), or whose lowest stands more than a rise
    /// under it (a pit at its foot under the road's bank).</item>
    /// <item>A top arriving on its road's end's ring (a flight going down from the road's end, RoadEndRing.MetAtTop) whose
    /// exit is not within a walker's step of the road's height there (StairLayout.Walkable), as a foot is met.</item>
    /// <item>A foot or a top of a staircase it lays with a way off it but not finished as the end rule chose (StairEnds.Unfinished, the
    /// end audit's own verdict, on the same finished ground): no finish worked there (StairLayout.FinishFoot or FinishTop kept none), and
    /// the road is refused and searched for again, not left for the end audit to find.</item>
    /// </list>
    /// The road's height at the ring is its own end's there: the end of the piece planned to that staircase
    /// (PlannedPath.StairAtStart, StairAtEnd) nearest the ring's centre, else (a staircase planned with none marked) of the
    /// pieces' ends within the ring's StartReach the nearest its centre (read only within the ring's own reach, the tower's
    /// road above stored its end 4.0 m off the ring it was planned on, 6.4 m under the staircase's foot, and the foot was
    /// never refused).
    /// </summary>
    internal static string? ConnectionRefusal(IEnumerable<RoadStairSpan> stairs, IReadOnlyList<RoadSpatialGrid.PlannedPath> own, out Vector2 at,
        IReadOnlyList<StairRun>? finished = null, Func<StairRun, Func<float, float, float>>? groundOf = null)
    {
        at = default;
        foreach (RoadStairSpan st in stairs)
        {
            StairPlan plan = st.Plan;
            if (st.Existing || plan.Ring is not RoadEndRing ring || RoadEnd(st, ring, own) is not float height) continue;
            if (RoadEndRing.MetAtTop(plan))
            {
                if (StairLayout.Walkable(plan.End.y, height)) continue;
                at = ring.Centre;
                return TopOffItsRoad;
            }
            float reach = ring.StartReach, lowest = float.PositiveInfinity;
            foreach (StairPiece p in plan.Pieces)
            {
                if (p.Kind == StairPieceKind.Support || p.Sunk) continue;
                if (Vector2.Distance(new Vector2(p.Position.x, p.Position.z), ring.Centre) > reach) continue;
                lowest = Mathf.Min(lowest, StairLayout.Footprint(p).height);
            }
            if (!float.IsPositiveInfinity(lowest) && lowest > height && !StairLayout.FootMeets(plan, lowest, height))
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
        // Every end of its staircases with a way off it on the finished ground (read last: the ring checks above read no ground).
        if (finished != null && groundOf != null && NoWayOff(finished, groundOf, out string why) is Vector2 off)
        {
            at = off;
            return why;
        }
        return null;
    }

    /// <summary>The road's own height at the end on <paramref name="ring"/> where <paramref name="st"/> meets it
    /// (<see cref="ConnectionRefusal"/>): of the pieces of <paramref name="own"/> planned to it, else of every piece, the end
    /// within the ring's StartReach nearest its centre (the lower of two as near); null where none ends there (a staircase
    /// met by the next one on a shared floor has none).</summary>
    private static float? RoadEnd(RoadStairSpan st, RoadEndRing ring, IReadOnlyList<RoadSpatialGrid.PlannedPath> own)
    {
        bool Marked(RoadSpatialGrid.PlannedPath piece, bool atStart) =>
            (atStart ? piece.StairAtStart : piece.StairAtEnd) is { } m && ReferenceEquals(m.Stair, st);
        foreach (bool marked in new[] { true, false })
        {
            float? road = null;
            float nearest = float.PositiveInfinity;
            foreach (RoadSpatialGrid.PlannedPath piece in own)
                for (int k = 0; k < piece.Points.Count && piece.Points.Count >= 2; k += piece.Points.Count - 1)
                {
                    if (marked && !Marked(piece, k == 0)) continue;
                    float d = Vector2.Distance(piece.Points[k], ring.Centre);
                    if (d > ring.StartReach || d > nearest + 0.01f) continue;
                    road = d < nearest - 0.01f || road is not float r ? piece.Heights[k] : Mathf.Min(r, piece.Heights[k]);
                    nearest = Mathf.Min(nearest, d);
                }
            if (road != null) return road;
        }
        return null;
    }

    /// <summary>Why <see cref="ConnectionRefusal"/> refuses a road: an end of a staircase it lays with no way off it on the
    /// finished ground; a foot over its road, or under it; a top arriving on its road's end off its road's height.</summary>
    internal const string NoWayOffItsEnd = "a staircase's end with no way off it", FootOverItsRoad = "a staircase's foot over its road",
        FootUnderItsRoad = "a staircase's foot under its road", TopOffItsRoad = "a staircase's top off its road",
        FootUnfinished = "a staircase's foot no finish fits", TopUnfinished = "a staircase's top no finish fits", FootOntoAnotherRoad = "a staircase's foot onto a road not its own";

    /// <summary>Where the first end of <paramref name="runs"/> with no way off it is, by the one end reader (StairEnds.Read on
    /// <paramref name="groundOf"/>'s ground for its run: a gap or a miss, an end not verified is neither; <paramref name="why"/>
    /// <see cref="NoWayOffItsEnd"/>), or the first end, foot or top, with a way off not finished as its rule chose (StairEnds.Unfinished,
    /// the end audit's UNFINISHED: <see cref="TopUnfinished"/>,
    /// <see cref="FootUnfinished"/>), or the first foot stepped off onto a road with no road of its own recorded there
    /// (StairEnds.End.OntoAnotherRoad: none planned to it, or its road's end there not kept; <see cref="FootOntoAnotherRoad"/>:
    /// each end is met by its road, never left not verified), its last stair's exit edge (or the run's end where none was
    /// read); null where every end has one, every end is finished and every foot is met by its own road. Each run is joined to the others of <paramref name="runs"/> and to the network's
    /// staircases meeting it end to end (StairStructures.MetAtEnds), as the audit joins it once all are stored.</summary>
    internal static Vector2? NoWayOff(IReadOnlyList<StairRun> runs, Func<StairRun, Func<float, float, float>> groundOf, out string why)
    {
        foreach (StairRun run in runs)
        {
            List<StructurePiece> others = StairStructures.MetAtEnds(run);
            foreach (StairRun other in runs)
                if (!ReferenceEquals(other, run)) others.AddRange(other.Pieces);
            foreach (StairEnds.End end in StairEnds.Read(run, groundOf(run), others))
                if (end.Verdict is StairEnds.Verdict.Gap or StairEnds.Verdict.Miss || end.OntoAnotherRoad || StairEnds.Unfinished(end) != null)
                {
                    why = end.Verdict is StairEnds.Verdict.Gap or StairEnds.Verdict.Miss ? NoWayOffItsEnd
                        : end.OntoAnotherRoad ? FootOntoAnotherRoad : end.Top ? TopUnfinished : FootUnfinished;
                    if (RoadNetworkGenerator.RoadsLog)
                        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(StairEnds.Line(end) + " (as the road would be stored: refused)");
                    return end.Edge is Vector3 e ? new Vector2(e.x, e.z) : end.Top ? run.To : run.From;
                }
        }
        why = "";
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
    /// road at another height or on another staircase's flight; its points inside a location's footprint are left out (the
    /// terrain writer leaves a location's ground alone). Each refusal is counted by why (<see cref="PadsRefused"/>), and each pad
    /// laid; not where <paramref name="tally"/> is false (a check reading what the road would write: the pads it stores are
    /// counted as it is stored). The one owner of staircase starts (docs/OWNERS.md).
    /// </summary>
    internal static List<RoadSpatialGrid.PlannedPath> StartPads(IReadOnlyList<RoadSpatialGrid.PlannedPath> own, IReadOnlyList<RoadStairSpan> stairs,
        WorldGenerator? world, string? label = null, bool tally = true) => StartPads(own, stairs, world, label, tally, null, null);

    /// <summary><see cref="StartPads(IReadOnlyList{RoadSpatialGrid.PlannedPath}, IReadOnlyList{RoadStairSpan}, WorldGenerator?, string?, bool)"/>
    /// read against the network as it will stand once the road's <paramref name="proposed"/> changes are made (the other
    /// roads it raises at their new heights), with each count and line it would make kept in <paramref name="said"/> (as
    /// <paramref name="tally"/> true makes them, said with the road's label) for its commit to make as it stores the pads
    /// (RoadNetworkGenerator.Written).</summary>
    internal static List<RoadSpatialGrid.PlannedPath> StartPads(IReadOnlyList<RoadSpatialGrid.PlannedPath> own, IReadOnlyList<RoadStairSpan> stairs,
        WorldGenerator? world, string? label, bool tally, RoadNetworkGenerator.Proposed? proposed, List<Action<string?>>? said)
    {
        void Tally(Action<string?> say) { if (said != null) said.Add(say); else if (tally) say(label); }
        var pads = new List<RoadSpatialGrid.PlannedPath>();
        if (world == null || stairs.Count == 0) return pads;
        // The earthwork cap as every road is judged by it (RoadNetworkGenerator.EarthworkLimit: none where none is set).
        float cap = RoadNetworkGenerator.EarthworkLimit;
        // The ground before roads, as every road's earthwork is measured over (RoadNetworkGenerator.GroundBeforeRoads).
        Func<Vector2, float> before = RoadNetworkGenerator.GroundBeforeRoads(world);
        float Ground(float x, float z) => before(new Vector2(x, z));
        var runs = new List<StairRun>(stairs.Count);
        foreach (RoadStairSpan st in stairs) runs.Add(StairRun.FromPlan(st.FromPoint, st.ToPoint, Width, st.Plan, st.Kit));
        void Refused(RoadEndRing.Pad? pad, RoadEndRing r, string kind, string why) => Tally(l =>
        {
            PadsRefused.AddOrUpdate(kind, 1, (_, n) => n + 1);
            if (RoadNetworkGenerator.RoadsLog)
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(FormattableString.Invariant(
                    $"[STAIRS] start pad at ({(pad?.End ?? r.Centre).x:F1}, {(pad?.End ?? r.Centre).y:F1}) not laid on {l ?? "(unlabelled)"}: {why}"));
        });
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
                Refused(null, ring, why.StartsWith(RoadEndRing.PastTheCap, StringComparison.Ordinal) ? RoadEndRing.PastTheCap : why, why);
                continue;
            }
            bool meets = false;
            foreach (RoadSpatialGrid.PlannedPath piece in own)
            {
                if (piece.Points.Count < 2 || piece.FollowTerrain) continue;
                foreach (int i in new[] { 0, piece.Points.Count - 1 })
                    meets |= Vector2.Distance(piece.Points[i], pad.End) <= PadMeetsEnd
                        && StairLayout.Walkable(piece.Heights[i], pad.Height);
            }
            if (!meets) { Refused(pad, ring, "no road ends there", "no road of its own ends there at its height"); continue; }
            var points = new List<Vector2>();
            var heights = new List<float>();
            string? clash = null;
            for (int i = 0; i < pad.Points.Count && clash == null; i++)
            {
                Vector2 q = pad.Points[i];
                if (RoadLocationProtection.Contains(q)) continue;
                if (!RoadWater.BuiltDry(world, q)) clash = "in water";
                else if (RoadSpatialGrid.TryGetRoadHeightWithin(q, RoadEndRing.PadSpacing, out float road, proposed)
                         && !StairLayout.Walkable(road, pad.Heights[i])) clash = "on another road";
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
            RoadEndRing.Pad laid = pad;
            int count = points.Count;
            Tally(l =>
            {
                System.Threading.Interlocked.Increment(ref laid.Top ? ref PadsAtTops : ref PadsAtFeet);
                if (laid.LeftOut > 0) System.Threading.Interlocked.Increment(ref PadsPartial);
                if (RoadNetworkGenerator.RoadsLog)
                    ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(FormattableString.Invariant(
                        $"[STAIRS] start pad {(laid.Top ? "top" : "foot")} at ({laid.End.x:F1}, {laid.End.y:F1}) on {l ?? "(unlabelled)"}: {count} point(s) at {laid.Height:F2}, under {laid.Pieces} walked piece(s), cut {laid.MostCut:F2} m, fill {laid.MostFill:F2} m, {laid.LeftOut} left out over falling ground"));
            });
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
    /// <summary>Merges whose free layout was not laid: both held layouts before it ran out of budget (RoadStairRuns.Merge).</summary>
    public static int MergesFreeSkipped;
}
