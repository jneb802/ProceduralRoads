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
    /// <summary>Road staircase tops finished out over a lip with a crest this generation (<see cref="TopOntoSurface"/>).</summary>
    public static int RoadTopsCrested;

    /// <summary>
    /// A road staircase whose top stops under the ground it leads onto (more than a step-up under it 2.5 m on, the
    /// way the road carries on) is carried out over the lip, up to <see cref="StairLayout.MostClimbedOn"/> stairs, and
    /// finished with the back-to-back crest pair landing on the surface; the road then starts on the surface past the
    /// crest (at 6468.3,-1522.5 the top is better a bit out, doing the double stair ridge to land on the
    /// surface). Where the ground past the top is level with it or under it, the road's earthwork meets it as before.
    /// Only where the road carries on within 60 degrees of the top's heading, and once per plan (a plan is shared
    /// between the roads that walk it). <paramref name="next"/>: the path index the road carries on from.
    /// </summary>
    internal static bool TopOntoSurface(StairPlan plan, List<Vector2> path, int next, Func<float, float, float> ground, out Vector2 past, out float pastHeight,
        Func<StairPlan, int, bool>? fits = null)
    {
        past = default; pastHeight = 0f;
        if (!plan.Reached || next < 0 || next >= path.Count) return false;
        lock (plan)
        {
            if (plan.TopCrested is Vector3 done) { past = new Vector2(done.x, done.z); pastHeight = done.y; return true; }
            Vector2 top = new Vector2(plan.End.x, plan.End.z), dir = StairLayout.Forward(plan.EndHeading);
            Vector2 toRoad = path[next] - top;
            if (toRoad.sqrMagnitude < 1e-4f || Vector2.Dot(dir, toRoad.normalized) < 0.5f) return false; // more than 60 degrees off
            Vector2 ahead = top + dir * StairLayout.TopAhead;
            if (ground(ahead.x, ahead.y) - plan.End.y <= StairLayout.TopFloorBand) return false;
            int pieces = plan.Pieces.Count, clearances = plan.Clearances.Count;
            Vector3 end = plan.End;
            int stairs = plan.Stairs;
            float rise = plan.Rise, walked = plan.WalkedLength;
            // Climbed on, then out to where the surface is within the floor's band: a floor level with the top onto it, the
            // road on from its far edge at its height; higher, the crest. Its top row is not a floor to keep the floor off.
            plan.LastRowPieces = -1;
            // What it adds past the top passes the staircase's own checks (<paramref name="fits"/>, given the plan and the index
            // of the first piece added: Lay's TopFits), or that finish is not laid and the next is tried; none fitting, the top
            // stays as it is (unchecked, a climb, crest or floor past the top could land on another road or
            // staircase, in water or a place).
            StairLayout.TopFinish finish = StairLayout.FinishTop(plan, ground, Width, StairCover.RoadStaircase, p => fits == null || fits(p, pieces), out _);
            if (finish == StairLayout.TopFinish.TopFloor)
            {
                Vector2 floorEnd = new Vector2(plan.End.x, plan.End.z) + StairLayout.Forward(plan.EndHeading) * StairLayout.PieceRun;
                past = floorEnd;
                pastHeight = plan.End.y;
                plan.TopCrested = new Vector3(past.x, pastHeight, past.y);
                System.Threading.Interlocked.Increment(ref RoadTopsCrested);
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
            Vector2 crestTop = new Vector2(plan.End.x, plan.End.z);
            past = crestTop + StairLayout.Forward(plan.EndHeading) * (2f * StairLayout.PieceRun);
            pastHeight = ground(past.x, past.y);
            plan.TopCrested = new Vector3(past.x, pastHeight, past.y);
            System.Threading.Interlocked.Increment(ref RoadTopsCrested);
            return true;
        }
    }

    /// <summary>
    /// Whether what a staircase over path[from..to] adds past its top (<see cref="TopOntoSurface"/>, its pieces from
    /// <paramref name="added"/> on) may be laid, by the checks a staircase's own pieces pass in <see cref="Usable"/>: no walked
    /// piece in water or a place, on another road, on its own road away from the stretch and the road past the top that
    /// the additions replace (the road starts again past them), nor inside another staircase). Its own
    /// road is not in the grid yet, so every road the grid holds there is another.
    /// </summary>
    internal static bool TopFits(StairPlan plan, int added, List<Vector2> path, int from, int to, WorldGenerator world, List<RoadStairSpan> before, Vector2? meet)
    {
        var extra = new StairPlan { Kit = plan.Kit, Reached = true };
        for (int i = added; i < plan.Pieces.Count; i++) extra.Pieces.Add(plan.Pieces[i]);
        // The stretch and the road past its top as far as the additions reach (the road starts again past them).
        int top = plan.Reversed ? from : to, step = plan.Reversed ? -1 : 1;
        float reach = 0f;
        foreach (StairPiece piece in extra.Pieces)
            reach = Mathf.Max(reach, Vector2.Distance(new Vector2(piece.Position.x, piece.Position.z), path[top]) + StairLayout.PieceRun / 2f);
        int end = top + step;
        while (end + step >= 0 && end + step < path.Count && Vector2.Distance(path[end], path[top]) < reach) end += step;
        end = Mathf.Clamp(end, 0, path.Count - 1);
        int low = plan.Reversed ? end : from, high = plan.Reversed ? to : end;
        // In water or a place: the one test every staircase gets (Blocked), on the ground it is laid on.
        if (Blocked(extra, world, null, (x, z) => GroundAt(x, z, world)) != "") { LastRefusal = Refusal.WaterOrPlace; return false; }
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

    /// <summary>The way the road comes to the foot of the staircase over path[from..to]: its heading over its last
    /// stretch into the lower end (as BesideItsRoad reads it), or null where no road comes to that end.</summary>
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
        Func<float, float, float> ground, List<Vector2> corridor, int tops, float? facing, float? topFacing, WorldGenerator world,
        List<RoadStairSpan> spans)
    {
        if (topFacing is not float leave) return null;
        bool reversed = ground(b.x, b.y) < ground(a.x, a.y);
        Vector2 top = reversed ? a : b, foot = reversed ? b : a;
        // The side it comes in from: the foot's side of the road leaving the top.
        Vector2 right = StairLayout.Right(leave);
        int first = Vector2.Dot(foot - top, right) >= 0f ? 1 : -1;
        float offset = BesideOffset(0f);
        foreach (int side in new[] { first, -first })
        {
            Vector2 beside = top + right * (side * offset);
            var line = new List<Vector2>(corridor);
            line[reversed ? 0 : line.Count - 1] = beside;
            StairPlan again = reversed
                ? Plan(beside, b, kit, ground, null, null, line, topLandings: tops, retried: true, facing: facing, topFacing: topFacing)
                : Plan(a, beside, kit, ground, null, null, line, topLandings: tops, retried: true, facing: facing, topFacing: topFacing);
            if (Usable(again, path, from, to, world, spans, null)) { System.Threading.Interlocked.Increment(ref TopsBeside); return again; }
        }
        return null;
    }

    /// <summary>Staircase tops laid again beside the road they hand to this generation (<see cref="TopBesideItsRoad"/>).</summary>
    public static int TopsBeside;
}
