using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// A road staircase's top (one owner): it arrives facing the way its road leaves, only its joining piece lies on that road
/// (else it is laid again beside the road's start), and under ground that stands over it it is carried out over the lip and
/// finished onto the surface (StairLayout.FinishTop).
/// </summary>
public static partial class RoadStairRuns
{
    /// <summary>
    /// A stair stretch of <paramref name="path"/> ended higher, the staircase taking the height its road cannot: where the
    /// road leaving its top (its upper end) cannot climb on from it following its ground as it is built to (its ground more
    /// than <see cref="ApproachFill"/> over the line climbing on from the top, <see cref="ApproachOver"/>: a face's rim the
    /// search's step from the top cell crossed at under the cap end to end), the top moves on along the road to the nearest
    /// path point whose own way on is within it, so the flight is laid to the rim and the road leaves it at the ground. The
    /// mirror of <see cref="StartLower"/> at the foot, with its limits: not onto the path's ends, past a crossing, nor within
    /// <see cref="StoneGap"/> of the stretch before (<paramref name="before"/>, its last index, -1 for none) or after
    /// (<paramref name="after"/>, its first index, the path's count for none); nor for a run an earlier road laid between the
    /// stretch's ends. The stretch as it was where no such point is; the layout then lays it within its rules or refuses it.
    /// </summary>
    internal static (int from, int to) EndHigher(List<Vector2> path, (int from, int to) stretch, int before, int after,
        IReadOnlyList<RoadCrossing> crossings, Func<Vector2, float> ground)
    {
        var (from, to) = stretch;
        if (StairStructures.Between(path[from], path[to], SameRunReach) != null || StairStructures.Carrying(path[from], path[to], OnStairReach).HasValue)
            return stretch;
        float cap = RoadGrade.Configured;
        bool up = ground(path[to]) >= ground(path[from]);
        int top = up ? to : from, step = up ? 1 : -1;
        // The way on runs to the structure after it: the stretch's neighbour, a crossing, or the path's end.
        int stop = up ? Mathf.Min(path.Count - 1, after) : Mathf.Max(0, before);
        bool neighbour = up ? after < path.Count : before >= 0;
        foreach (RoadCrossing c in crossings)
        {
            if (up && c.FromIndex >= top && c.FromIndex < stop) { stop = c.FromIndex; neighbour = true; }
            if (!up && c.ToIndex <= top && c.ToIndex > stop) { stop = c.ToIndex; neighbour = true; }
        }
        if (ApproachOver(path, top, step, stop, ground, cap) <= ApproachFill) return stretch;
        for (int j = top + step; up ? j < stop && j <= path.Count - 2 : j > stop && j >= 1; j += step)
        {
            if (neighbour && Along(path, j, stop) < StoneGap) break;
            if (ApproachOver(path, j, step, stop, ground, cap) <= ApproachFill) return up ? (from, j) : (j, to);
        }
        return stretch;
    }

    /// <summary>
    /// A road staircase whose top stops under the ground it leads onto (more than a step-up under it at the end reader's corners, the
    /// way the road carries on) is carried out over the lip, up to <see cref="StairLayout.MostClimbedOn"/> stairs, and
    /// finished with the back-to-back crest pair landing on the surface; the road then starts on the surface past the
    /// crest (at 6468.3,-1522.5 the top is better a bit out, doing the double stair ridge to land on the
    /// surface). Where the ground past the top is level with it or under it, the road's earthwork meets it as before.
    /// Only where the road carries on within 60 degrees of the top's heading, and once per plan (a plan is shared
    /// between the roads that walk it). Not at a top the road was carried on to (<see cref="StairPlan.TopFloorsTrimmed"/>): the
    /// road there was judged starting where it is. <paramref name="next"/>: the path index the road carries on from.
    /// </summary>
    internal static bool TopOntoSurface(StairPlan plan, List<Vector2> path, int next, Func<float, float, float> ground, out Vector2 past, out float pastHeight,
        Func<StairPlan, int, bool>? fits = null)
    {
        past = default; pastHeight = 0f;
        if (!plan.Reached || plan.TopFloorsTrimmed > 0 || next < 0 || next >= path.Count) return false;
        lock (plan)
        {
            if (plan.TopCrested is Vector3 done) { past = new Vector2(done.x, done.z); pastHeight = done.y; return true; }
            Vector2 top = new Vector2(plan.End.x, plan.End.z), dir = StairLayout.Forward(plan.EndHeading);
            Vector2 toRoad = path[next] - top;
            if (toRoad.sqrMagnitude < 1e-4f || Vector2.Dot(dir, toRoad.normalized) < 0.5f) return false; // more than 60 degrees off
            if (StairEnds.OverAhead(top, dir, plan.End.y, Width, ground) <= StairLayout.TopFloorBand) return false;
            int pieces = plan.Pieces.Count, clearances = plan.Clearances.Count;
            Vector3 end = plan.End;
            int stairs = plan.Stairs;
            float rise = plan.Rise, walked = plan.WalkedLength;
            // Finished by the end rule (StairLayout.FinishTop): climbed on, then a floor level with the top onto the ground, the
            // road on from its far edge at its height; else the crest's stair down. Its top row is not a floor to keep the floor off.
            plan.LastRowPieces = -1;
            // What it adds past the top passes the staircase's own checks (<paramref name="fits"/>, given the plan and the index
            // of the first piece added: Lay's TopFits), or that finish is not laid and the next is tried; none fitting, the top
            // stays as it is (unchecked, a climb, crest or floor past the top could land on another road or
            // staircase, in water or a location). A floor is kept only where the end reader finds it finished with a way off (TopFinished,
            // as an approach's top asks it), else the top's other case is tried, and with none the top stays as laid; a top left
            // unfinished then refuses its road at the end gate (NoWayOff, TopUnfinished), as an unfinished foot does.
            StairLayout.TopFinish finish = StairLayout.FinishTop(plan, ground, Width, StairCover.RoadStaircase, p => fits == null || fits(p, pieces), out _,
                wayOff: p => TopFinished(p, ground));
            if (finish == StairLayout.TopFinish.TopFloor)
            {
                Vector2 floorEnd = new Vector2(plan.End.x, plan.End.z) + StairLayout.Forward(plan.EndHeading) * StairLayout.PieceRun;
                past = floorEnd;
                pastHeight = plan.End.y;
                plan.TopCrested = new Vector3(past.x, pastHeight, past.y);
                System.Threading.Interlocked.Increment(ref TopFloors);
                return true;
            }
            if (finish == StairLayout.TopFinish.None)
            {
                plan.Pieces.RemoveRange(pieces, plan.Pieces.Count - pieces);
                plan.Clearances.RemoveRange(clearances, plan.Clearances.Count - clearances);
                plan.End = end; plan.Stairs = stairs; plan.Rise = rise; plan.WalkedLength = walked;
                return false;
            }
            // The crest's stair down off the stairs climbed on: the road on from its low end, at its walking height there, as
            // off a top floor (its slope meets the ground a rise over its low edge at most, StairEnds.MeetsLowEdge; a road
            // starting on that ground stood up to a rise over the stair's end, and its start pad, level with the end, was not
            // laid: 0.6 m in planning, 1.3 m on the finished ground at 904.1,5220.2).
            StairLayout.TerminalExit? down = StairLayout.Terminal(plan);
            past = down != null ? new Vector2(down.Edge.x, down.Edge.z)
                : new Vector2(plan.End.x, plan.End.z) + StairLayout.Forward(plan.EndHeading) * StairLayout.PieceRun;
            pastHeight = down != null ? down.Edge.y : ground(past.x, past.y);
            plan.TopCrested = new Vector3(past.x, pastHeight, past.y);
            return true;
        }
    }

    /// <summary>
    /// Whether what a staircase over path[from..to] adds past its top (<see cref="TopOntoSurface"/>, its pieces from
    /// <paramref name="added"/> on) may be laid, by the checks a staircase's own pieces pass in <see cref="Usable"/>: no walked
    /// piece in water or a location, on another road, on its own road away from the stretch and the road past the top that
    /// the additions replace (the road starts again past them), nor inside another staircase). Its own
    /// road is not in the grid yet, so every road the grid holds there is another.
    /// </summary>
    internal static bool TopFits(StairPlan plan, int added, List<Vector2> path, int from, int to, WorldGenerator world, List<RoadStairSpan> before, Vector2? meet)
    {
        var extra = new List<StairPiece>();
        for (int i = added; i < plan.Pieces.Count; i++) extra.Add(plan.Pieces[i]);
        // The stretch and the road past its top as far as the additions reach (the road starts again past them), and as far
        // again as a piece may stand off the road and still be on it: the road starting again at the last piece's far edge is
        // the one it meets, not one it lands on (a top climbed on two stairs and floored, its floor against the next stretch).
        int top = plan.Reversed ? from : to, step = plan.Reversed ? -1 : 1;
        float reach = 0f;
        foreach (StairPiece piece in extra)
            reach = Mathf.Max(reach, Vector2.Distance(new Vector2(piece.Position.x, piece.Position.z), path[top]) + StairLayout.PieceRun / 2f + OnRoad);
        int end = top + step;
        while (end + step >= 0 && end + step < path.Count && Vector2.Distance(path[end], path[top]) < reach) end += step;
        end = Mathf.Clamp(end, 0, path.Count - 1);
        int low = plan.Reversed ? end : from, high = plan.Reversed ? to : end;
        return AddedFits(extra, path, low, high, world, before, meet, plan.Kit);
    }

    /// <summary>
    /// Whether pieces added to a staircase over path[low..high] past where its layout was judged (a top's finish,
    /// <see cref="TopFits"/>; a foot's with no road past it, <see cref="FinishFootOntoNothing"/>) may be laid, by the checks a
    /// staircase's own pieces pass in <see cref="Usable"/>: no walked piece in water or a location, on another road, on its own
    /// road away from that stretch, nor inside another staircase. Its own road is not in the grid yet, so every road the grid
    /// holds there is another.
    /// </summary>
    internal static bool AddedFits(List<StairPiece> added, List<Vector2> path, int low, int high, WorldGenerator world, List<RoadStairSpan> before,
        Vector2? meet, string kit = "")
    {
        var extra = new StairPlan { Kit = kit, Reached = true };
        extra.Pieces.AddRange(added);
        // In water or a location: the one test every staircase gets (Blocked), on the ground it is laid on.
        if (Blocked(extra, world, null, (x, z) => GroundAt(x, z, world)) != "") { LastRefusal = Refusal.WaterOrLocation; return false; }
        foreach (StairPiece piece in extra.Pieces)
        {
            if (piece.Kind != StairPieceKind.Stair && piece.Kind != StairPieceKind.Landing) continue;
            var p = new Vector2(piece.Position.x, piece.Position.z);
            Refusal why = RoadSpatialGrid.IsInitialized && RoadSpatialGrid.TryGetRoadWithin(p, OnRoad, out _) ? Refusal.OnItsRoad : Refusal.None;
            for (int i = 1; i < path.Count && why == Refusal.None; i++)
                if ((i <= low || i > high) && DistanceToSegment(p, path[i - 1], path[i]) < OnRoad) why = Refusal.OnItsRoad;
            if (why != Refusal.None) { LastRefusal = why; return false; }
        }
        if (OverStairs(extra, before, meet)) { LastRefusal = Refusal.OnAnotherStaircase; return false; }
        return true;
    }

    /// <summary>Staircase tops finished with a floor out onto the ground this generation (<see cref="StairLayout.AddTopFloor"/>).</summary>
    public static int TopFloors;

    /// <summary>The way the road leaves the top of the staircase over path[from..to]: its heading over its first stretch
    /// on from the higher end, or null where no road leaves that end.</summary>
    internal static float? TopFacing(List<Vector2> path, int from, int to, Func<float, float, float> ground)
    {
        bool reversed = ground(path[to].x, path[to].y) < ground(path[from].x, path[from].y);
        int top = reversed ? from : to, beyond = reversed ? from - 1 : to + 1;
        if (beyond < 0 || beyond >= path.Count) return null;
        return RoadTravel(path, top, beyond, into: false);
    }

    /// <summary>
    /// A staircase over path[from..to] whose top lay on the road it hands to, laid again with its top beside that road's
    /// start, its edge on the road's edge, on each side in turn (the side it comes in from first): the road carries on from
    /// the top's own end (at the top at 6495.5,-1464.9 the top entered the road on the wrong side,
    /// taking over the road itself). Null where neither side lays.
    /// </summary>
    private static StairPlan? TopBesideItsRoad(List<Vector2> path, int from, int to, Vector2 a, Vector2 b, StairKit kit,
        LayoutGround laidOn, List<Vector2> corridor, int tops, float? facing, float? topFacing, WorldGenerator world,
        List<RoadStairSpan> spans, StretchAnswers? answers)
    {
        if (topFacing is not float leave) return null;
        Func<float, float, float> ground = laidOn.Read;
        bool reversed = ground(b.x, b.y) < ground(a.x, a.y);
        Vector2 top = reversed ? a : b, foot = reversed ? b : a;
        // The side it comes in from: the foot's side of the road leaving the top.
        Vector2 right = StairLayout.Right(leave);
        int first = Vector2.Dot(foot - top, right) >= 0f ? 1 : -1;
        // Its edge on the road's edge, carried on along the road.
        float offset = RoadNetworkGenerator.RoadWidth / 2f + Width / 2f;
        foreach (int side in new[] { first, -first })
        {
            Vector2 beside = top + right * (side * offset);
            var line = new List<Vector2>(corridor);
            line[reversed ? 0 : line.Count - 1] = beside;
            StairPlan again = reversed
                ? Plan(beside, b, kit, laidOn, null, null, line, topLandings: tops, retried: true, facing: facing, topFacing: topFacing, ring: FootRingOf(path, from, to, beside, b, ground), answers: answers)
                : Plan(a, beside, kit, laidOn, null, null, line, topLandings: tops, retried: true, facing: facing, topFacing: topFacing, ring: FootRingOf(path, from, to, a, beside, ground), answers: answers);
            if (Usable(again, path, from, to, world, spans, null)) return again;
        }
        return null;
    }
}
