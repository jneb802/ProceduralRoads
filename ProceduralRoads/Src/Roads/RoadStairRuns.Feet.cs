using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// A road staircase's foot (one owner): it faces the road it leaves, comes up out of the road's middle where it carries on
/// the road's way and is set beside the road where it turns off it or another road goes on past it, is lowered toward a road
/// that cannot climb to it, and has an apron cut where it stands against a bank. The layout's own foot rules (facing, set
/// off back, stair first, the ledge's edge) are StairLayout's.
/// </summary>
public static partial class RoadStairRuns
{
    /// <summary>Staircase feet lowered toward their road this generation (StairLayout.LowerFoot).</summary>
    public static int FeetLowered;

    /// <summary>How far back along the road from a foot its ground is read for LowerFoot.</summary>
    public const float FootBackReach = 5f;

    /// <summary>How far a staircase's first heading may turn from its road's direction of travel
    /// and still set off from the middle of the road: short of halfway to square, it carries on
    /// the way the road goes (a first flight on the diagonal, 45 degrees, steps aside). At 22.5
    /// a staircase 39 degrees off its road was set beside it and its bottom steps ran into the
    /// cliff the road ended at (MountainCave02 -2560,1536: a stair going the same direction as the road stays centred,
    /// one going perpendicular or switching backwards starts off to the side).</summary>
    public const float FootAlignTolerance = 45f;

    /// <summary>
    /// The sides of the road a turned foot may be set on, the one it turns to first. Turned off the road (less than
    /// 157.5 degrees), only that one: set on the other, its flight ran back across the road's line and the road met its
    /// side, not its foot (at the foot at 8972.6,3335.8 the bottom of the stairs was off the road, crossing
    /// it, targeting the wrong side). Turned right back along the road, either.
    /// </summary>
    internal static int[] BesideSides(float turn, int first) =>
        Mathf.Abs(turn) < BackAlongRoad ? new[] { first } : new[] { first, -first };

    /// <summary>A foot turned this far off its road is turned back along it: either side of the road will do.</summary>
    public const float BackAlongRoad = 157.5f;

    /// <summary>How far off the road's travel an approach's flight must set off to be laid again beside the road's end: back
    /// along the road (a Hildir_cave foot, 118 degrees off). Nearer it, the search's own price for a start off
    /// the road (StairLayout.FootOffRoadWeight) brings it round, and a second search for every approach cost the time.</summary>
    public const float ApproachBesideFrom = 90f;

    /// <summary>
    /// Where a staircase setting off from a road's end at <paramref name="end"/> on <paramref name="settled"/>, the road
    /// arriving on <paramref name="travel"/>, starts beside the road instead: its edge on the road's edge, on the side it
    /// turns to first (the other second); none where it carries on within <paramref name="from"/> degrees of the road.
    /// </summary>
    internal static List<Vector2> BesideTheRoadsEnd(Vector2 end, float travel, float settled, float from = FootAlignTolerance)
    {
        var points = new List<Vector2>();
        float turn = StairLayout.Wrap(settled - travel + 180f) - 180f; // + to the right
        if (Mathf.Abs(turn) < from) return points;
        float offset = BesideOffset(turn);
        Vector2 right = StairLayout.Right(travel);
        int first = turn > 0f ? 1 : -1;
        foreach (int side in BesideSides(turn, first)) points.Add(end + right * (side * offset));
        return points;
    }

    /// <summary>How far across a road's travel a staircase set beside the road stands from its line, its edge on the
    /// road's edge: its back edge when it leaves square to the road (<paramref name="turn"/> degrees off it), its whole
    /// width when it carries on along it or turns back. The one offset for a foot beside its road's end, a road
    /// staircase's foot and its top.</summary>
    internal static float BesideOffset(float turn) =>
        RoadNetworkGenerator.RoadWidth / 2f + Width / 2f * Mathf.Abs(Mathf.Cos(turn * Mathf.Deg2Rad));

    /// <summary>Staircases set beside their road, and ones that could not be (left in the middle of it).</summary>
    public static int FeetBeside, FeetBesideRefused;

    /// <summary>
    /// A staircase that leaves its road at an angle or back the way it came set off from the
    /// middle of the road, its bottom steps across the road's width: half or all of a road
    /// walked by others was stairs (align to the middle of the road when travelling in the same direction, but when
    /// switching to the side or back a bit, a player must be able to walk by). Laid again from beside the road's end, its
    /// edge on the road's edge, on the side it turns to (the higher side when it turns back),
    /// its bottom step at the road's height; the road keeps its own end. If neither side lays,
    /// the plan is kept as it was.
    /// </summary>
    private static StairPlan BesideItsRoad(StairPlan plan, List<Vector2> path, int from, int to, StairKit kit,
        Func<float, float, float> ground, List<Vector2> corridor, WorldGenerator world, List<RoadStairSpan> spans)
    {
        // The foot is the low end: path[from] climbed forward, path[to] when reversed.
        int foot = plan.Reversed ? to : from, beyond = plan.Reversed ? to + 1 : from - 1;
        if (beyond < 0 || beyond >= path.Count) return plan;
        Vector2 end = path[foot];
        float travel = RoadTravel(path, foot, beyond, into: true);
        float turn = StairLayout.Wrap(SettledHeading(plan, end) - travel + 180f) - 180f; // -180..180, + to the right
        bool turned = SetsOffTurned(plan, end, travel);
        // Straight on, it stays in the road's middle only where the road ends at its foot: where another road goes on past
        // it (a junction, the staircase a spur off a through road), set beside too (at the ridge at
        // -6961,3369 the stairs would get in the way of the straight-through road versus branching off to the side).
        float spurSide = 0f;
        bool spur = !turned && RoadGoesOnPast(end, path, foot, beyond - foot, travel, out spurSide);
        if (!turned && !spur) return plan;
        float offset = BesideOffset(turn);
        Vector2 right = StairLayout.Right(travel);
        float roadHeight = ground(end.x, end.y);
        int first = spur ? (Mathf.Abs(turn) >= StairLayout.HeadingStep / 2f ? (turn > 0f ? 1 : -1) : spurSide > 0f ? -1 : 1)
            : Mathf.Abs(turn) < BackAlongRoad ? (turn > 0f ? 1 : -1)
            : (ground(end.x + right.x * offset, end.y + right.y * offset) >= ground(end.x - right.x * offset, end.y - right.y * offset) ? 1 : -1);
        foreach (int side in spur ? new[] { first, -first } : BesideSides(turn, first))
        {
            Vector2 beside = end + right * (side * offset);
            var line = new List<Vector2>(corridor);
            line[plan.Reversed ? line.Count - 1 : 0] = beside;
            // The heading it set off at, from beside the road: given at the foot's end of the path.
            // Still within 90 degrees of the road's travel (StairLayout.FacingSteps): held only to its own heading, a flight
            // relaid beside the road could turn a further 67.5 degrees, back along the road. A last resort that set off back
            // anyway (more than 90 degrees, no run back facing it) is relaid beside the road as it is, its base off the road's
            // middle (foot at -3187.2,-27.5: as a last resort the base is off the centre
            // of the road).
            float? holdTo = BesideHold(plan, travel);
            StairPlan again = plan.Reversed
                ? Plan(path[from], beside, kit, ground, null, StairLayout.Wrap(plan.StartHeading + 180f), line, footHeight: roadHeight, facing: holdTo, topFacing: TopFacing(path, from, to, ground))
                : Plan(beside, path[to], kit, ground, plan.StartHeading, null, line, footHeight: roadHeight, facing: holdTo, topFacing: TopFacing(path, from, to, ground));
            if (Usable(again, path, from, to, world, spans, null))
            {
                System.Threading.Interlocked.Increment(ref FeetBeside);
                if (spur) System.Threading.Interlocked.Increment(ref SpurFeet);
                return again;
            }
        }
        System.Threading.Interlocked.Increment(ref FeetBesideRefused);
        return plan;
    }

    /// <summary>How far back along its road a staircase's foot (or top) judges the road's direction of travel from: one owner
    /// for road staircases and approaches. The last search step alone (one of 16 headings) can point well off the road as
    /// laid; a much longer reach reads round a bend the road has already made.</summary>
    public const float TravelReach = 8f;

    /// <summary>The flight's heading this many stair pieces up from its foot: past any swing its first
    /// pieces make onto the road's heading (Ruin1 -> Hildir_cave: 247.5, 270, then 315).</summary>
    public const int SettledPieces = 3;

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

    /// <summary>The facing a foot relaid beside its road is held to: the road's travel where the plan faced it (or ran back
    /// facing it, StairLayout.MostSetOffBack), none for a last resort that set off back anyway, relaid beside the road as it is.</summary>
    internal static float? BesideHold(StairPlan plan, float travel) =>
        plan.SetOffBack > 0 || Mathf.Abs(StairLayout.Wrap(plan.StartHeading - travel + 180f) - 180f) <= StairLayout.FacingSteps * StairLayout.HeadingStep + 0.01f
            ? travel : null;

    /// <summary>Feet straight on with their road set beside it, another road going on past them (<see cref="RoadGoesOnPast"/>).</summary>
    public static int SpurFeet;

    /// <summary>How far round a foot <see cref="RoadGoesOnPast"/> looks for another road.</summary>
    public const float SpurReach = 6f;

    /// <summary>
    /// Whether a road other than the one arriving at <paramref name="end"/> (along <paramref name="path"/> from index
    /// <paramref name="foot"/>, stepping <paramref name="step"/> away from the staircase) lies within <see cref="SpurReach"/>
    /// of the foot, any way round: the road does not end at the foot but goes on past it, a junction or a through road the
    /// staircase is a spur off. <paramref name="side"/>: summed across <paramref name="travel"/>, + where it lies to the right.
    /// </summary>
    internal static bool RoadGoesOnPast(Vector2 end, List<Vector2> path, int foot, int step, float travel, out float side)
    {
        side = 0f;
        // Its own road, back along the path a little past the ring.
        var own = new List<Vector2>();
        float walked = 0f;
        for (int i = foot; i >= 0 && i < path.Count; i += step)
        {
            if (own.Count > 0) walked += Vector2.Distance(own[own.Count - 1], path[i]);
            own.Add(path[i]);
            if (walked > SpurReach + 8f) break;
        }
        bool Own(Vector2 q)
        {
            if (own.Count == 1) return (q - own[0]).sqrMagnitude < OwnRoadReach * OwnRoadReach;
            for (int i = 1; i < own.Count; i++)
                if (DistanceToSegment(q, own[i - 1], own[i]) < OwnRoadReach) return true;
            return false;
        }
        Vector2 right = StairLayout.Right(travel);
        int hits = 0;
        for (int k = 0; k < 16; k++)
        {
            Vector2 p = end + StairLayout.Forward(k * StairLayout.HeadingStep) * SpurReach;
            if (!RoadSpatialGrid.TryGetRoadWithin(p, RoadNetworkGenerator.RoadWidth / 2f, out Vector2 at, q => !Own(q))) continue;
            hits++;
            side += Vector2.Dot(at - end, right);
        }
        return hits > 0;
    }

    /// <summary>Road points this near the arriving road's line are its own (<see cref="RoadGoesOnPast"/>).</summary>
    private const float OwnRoadReach = 3f;

    /// <summary>
    /// Whether a staircase from <paramref name="foot"/> leaves a road travelling <paramref name="travel"/> turned (set
    /// beside the road) rather than straight on (left in its middle): both its first stair and its settled flight
    /// (<see cref="SettledHeading"/>) at least <see cref="FootAlignTolerance"/> off the road. Its first stairs straight on and
    /// a bend after them, it stays centred (at the 41 m staircase's foot at 5629.9,4266.0: start in the middle if aligned with
    /// the road, and off to the side if turned); settled along the road past a jog at its first step, too.
    /// </summary>
    internal static bool SetsOffTurned(StairPlan plan, Vector2 foot, float travel)
    {
        float Off(float heading) => Mathf.Abs(StairLayout.Wrap(heading - travel + 180f) - 180f);
        StairPiece? first = plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair)
            .OrderBy(p => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), foot)).FirstOrDefault();
        // Set off back along the road (more than 90 degrees off it): beside the road, whatever it settles on (foot at -3187.2,-27.5:
        // as a last resort the base is off the centre of the road).
        if (first != null && Off(StairLayout.Wrap(first.YawDegrees + 180f)) > StairLayout.FacingSteps * StairLayout.HeadingStep + 0.01f) return true;
        if (Off(SettledHeading(plan, foot)) < FootAlignTolerance) return false;
        return first == null || Off(StairLayout.Wrap(first.YawDegrees + 180f)) >= FootAlignTolerance;
    }

    /// <summary>Whether a staircase climbing from <paramref name="foot"/> sets off on one heading and
    /// settles on another at least <see cref="FootAlignTolerance"/> away within its first pieces.</summary>
    internal static bool SwingsAtItsFoot(StairPlan plan, Vector2 foot)
    {
        StairPiece? first = plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair)
            .OrderBy(p => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), foot)).FirstOrDefault();
        if (first == null) return false;
        float swing = StairLayout.Wrap(SettledHeading(plan, foot) - (first.YawDegrees + 180f) + 180f) - 180f;
        return Mathf.Abs(swing) >= FootAlignTolerance;
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
        int at = atStart ? 0 : path.Count - 1;
        var line = new List<Vector2>(path);
        line[at] = end;
        Vector2 back = Back(line, at, atStart ? 1 : -1, TravelReach);
        return StairLayout.Bearing(back, end);
    }

    /// <summary>Staircase feet given a level apron cut into the bank behind them this generation (StairLayout.CutFootApron).</summary>
    public static int FootAprons;

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

    /// <summary>How far over the road's level the top of a foot's sunk step may stand (a stair's top is a rise over its base).</summary>
    public const float SunkStepAbove = 0.25f;

    /// <summary>How far back along its road behind its foot a piece's middle may stand and still be the step that joins it.</summary>
    public const float BehindFoot = 0.5f;
}
