using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// What two staircases meeting over a ridge or through a valley are laid with (one owner): a staircase searched down from
/// another's top edge, floors carried on or across from one top to the other, a floor shared at a valley's bottom, and a span of
/// floors across a valley with the flights cut back to it.
/// </summary>
public static partial class StairLayout
{
    /// <summary>
    /// A staircase searched down from <paramref name="top"/>, the top edge of a staircase already laid, to
    /// <paramref name="foot"/>: its first stair carries straight on from that edge a rise lower, back to back with the top
    /// stair laid (the crest pair over a ridge), and its foot lands where the search reaches the ground near
    /// <paramref name="foot"/>. Searched downward (every stair a rise lower), then turned round into the climb from its foot
    /// it is laid as, so it is emitted as any staircase. Over a ridge two staircases met top to top this way share their
    /// crest exactly (at 3377,6425 and -8030,-660: just one stair up and another down). No cantilevers.
    /// </summary>
    public static StairPlan BuildDown(StairContinuation top, Vector2 foot, Func<float, float, float> ground, float width = 2f, StairKit? kit = null,
        int maxExpansions = MaxExpansions, int totalExpansions = int.MaxValue, int stallExpansions = int.MaxValue,
        float maxBuried = StairCover.RoadStaircase, bool sunkFoot = true, IReadOnlyList<Vector2>? corridor = null)
    {
        kit ??= StairKit.Stone;
        var plan = new StairPlan { Kit = kit.Name, Reversed = true, BuiltDown = true };
        // Down the road's own cells where it has them, as a road's staircase climbs them: left to find its own way down, it
        // came down beside the road's continuation and lay over it.
        var goals = new List<Vector2>();
        if (corridor != null)
        {
            Vector2 lastGoal = top.Edge;
            foreach (Vector2 p in corridor)
            {
                if (Vector2.Distance(p, lastGoal) < FollowSpacing || Vector2.Distance(p, foot) < FollowSpacing) continue;
                goals.Add(p);
                lastGoal = p;
            }
        }
        goals.Add(foot);
        var route = new Route(goals, ground(foot.x, foot.y)) { MaxBuried = maxBuried, Kit = kit, Down = true, Corridor = corridor };
        var root = new Node
        {
            Kind = top.Kind,
            Start = top.Edge - Forward(top.Heading) * PieceRun,
            Edge = top.Edge,
            Heading = top.Heading,
            Height = top.Height,
            Centre = top.Edge - Forward(top.Heading) * (PieceRun / 2f),
            Placed = true,
            Parent = new Node { Edge = top.Edge, Start = top.Edge, Heading = top.Heading, Height = top.Height, Placed = true },
        };
        Node current = root;
        Node? reached = null;
        for (int g = 0; g < route.Goals.Count; g++)
        {
            bool final = g == route.Goals.Count - 1;
            var leg = new Route(route.Goals.GetRange(g, route.Goals.Count - g), route.GoalHeight) { MaxBuried = maxBuried, Kit = kit, Down = true, Corridor = corridor };
            current.Goal = 0;
            Node? at = Search(current, leg, final, ground, width, Math.Min(maxExpansions, totalExpansions), stallExpansions, out Node closest, out string reason, out int used);
            plan.Expansions += used;
            totalExpansions -= used;
            if (at == null) { plan.Reason = route.Goals.Count > 1 ? $"{reason} (waypoint {g + 1} of {route.Goals.Count})" : reason; return plan; }
            current = at;
            if (final) reached = at;
        }
        if (reached == null) { plan.Reason = "not reached"; return plan; }
        plan.Reached = true;
        // The way down, top first, then turned round: each piece the same piece climbed from its low edge.
        var down = new List<Node>();
        for (Node? n = reached; n != null && !n.Placed; n = n.Parent) down.Add(n);
        down.Reverse(); // top first
        if (down.Count == 0) { plan.Reached = false; plan.Reason = "no piece down from the top"; return plan; }
        Node last = down[down.Count - 1];
        Node climb = new Node { Edge = last.Edge, Start = last.Edge, Heading = Wrap(last.Heading + 180f), Height = last.Height };
        for (int k = down.Count - 1; k >= 0; k--)
        {
            Node d = down[k];
            Node? below = k + 1 < down.Count ? down[k + 1] : null;
            float height = d.Parent!.Height;
            if (d.Switchback != 0)
            {
                // Its two floors, the flight arriving on the one it left from and leaving from the one it arrived on.
                climb = new Node
                {
                    Parent = climb, Kind = StairPieceKind.Landing, Start = d.Edge, Edge = d.Start, Heading = Wrap(d.Heading + 180f),
                    Height = height, Switchback = -d.Switchback, Centre = d.Centre, Depth = climb.Depth + 1,
                };
                continue;
            }
            climb = new Node
            {
                Parent = climb, Kind = d.Kind, Start = d.Edge, Edge = d.Start, Heading = Wrap(d.Heading + 180f), Height = height,
                TurnSteps = below == null || below.Switchback != 0 ? 0 : -below.TurnSteps, Pivot = below?.Pivot ?? d.Edge,
                Overlaps = below != null && below.Overlaps, Centre = d.Centre, Depth = climb.Depth + 1, Float = d.Float, Buried = d.Buried,
            };
        }
        Emit(plan, climb, ground, width, kit, sunkFoot);
        plan.Reversed = true;
        if (plan.Foot == null) plan.Foot = new Vector3(last.Edge.x, last.Height, last.Edge.y);
        return plan;
    }

    /// <summary>What <see cref="TrimBelow"/> changes, kept to put back.</summary>
    internal sealed class PlanSnapshot
    {
        public readonly List<StairPiece> Pieces;
        public readonly List<StairClearance> Clearances;
        public readonly List<Vector3> Cuts;
        public readonly int Stairs, Landings, SunkPieces, SunkClearances, LastRowPieces, LastRowClearances;
        public readonly float Rise, WalkedLength;
        public readonly Vector3? Foot;
        public PlanSnapshot(StairPlan p)
        {
            Pieces = new List<StairPiece>(p.Pieces); Clearances = new List<StairClearance>(p.Clearances); Cuts = new List<Vector3>(p.Cuts);
            Stairs = p.Stairs; Landings = p.Landings; SunkPieces = p.SunkPieces; SunkClearances = p.SunkClearances;
            LastRowPieces = p.LastRowPieces; LastRowClearances = p.LastRowClearances; Rise = p.Rise; WalkedLength = p.WalkedLength; Foot = p.Foot;
        }
        public void Restore(StairPlan p)
        {
            p.Pieces.Clear(); p.Pieces.AddRange(Pieces); p.Clearances.Clear(); p.Clearances.AddRange(Clearances); p.Cuts.Clear(); p.Cuts.AddRange(Cuts);
            p.Stairs = Stairs; p.Landings = Landings; p.SunkPieces = SunkPieces; p.SunkClearances = SunkClearances;
            p.LastRowPieces = LastRowPieces; p.LastRowClearances = LastRowClearances; p.Rise = Rise; p.WalkedLength = WalkedLength; p.Foot = Foot;
        }
    }

    /// <summary>
    /// Takes away every walked piece of <paramref name="plan"/> under <paramref name="height"/> (a stair whose base, a floor or
    /// filler whose walking surface, is lower), with the columns under them, their clearances and their cuts: the bottom of
    /// a flight into a valley a floor span crosses instead (RoadStairRuns.SpanTheValley). Returns how many walked pieces went.
    /// </summary>
    internal static int TrimBelow(StairPlan plan, float height, StairKit kit)
    {
        int lanes = 1, removed = 0, stairs = 0, floors = 0, before = 0;
        var kept = new List<StairPiece>();
        bool dropping = false;
        for (int i = 0; i < plan.Pieces.Count; i++)
        {
            StairPiece p = plan.Pieces[i];
            if (p.Kind != StairPieceKind.Support)
            {
                float surface = p.Kind == StairPieceKind.Stair ? p.Position.y : p.Position.y + kit.FloorTop;
                // A turn's wedge stands level with the top of the stair before it: at the height itself, the stair it
                // followed went, and its turn with it.
                dropping = p.Kind == StairPieceKind.Filler ? surface < height + 0.01f : surface < height - 0.01f;
                if (dropping)
                {
                    removed++;
                    if (p.Kind == StairPieceKind.Stair) stairs++;
                    else if (p.Kind == StairPieceKind.Landing) floors++;
                    if (i < plan.LastRowPieces) before++;
                    continue;
                }
            }
            else if (dropping) { if (i < plan.LastRowPieces) before++; continue; }
            kept.Add(p);
        }
        if (removed == 0) return 0;
        plan.Pieces.Clear();
        plan.Pieces.AddRange(kept);
        if (plan.LastRowPieces >= 0) plan.LastRowPieces -= before;
        int clearancesBefore = 0;
        var clearances = new List<StairClearance>();
        for (int i = 0; i < plan.Clearances.Count; i++)
        {
            if (plan.Clearances[i].Surface < height - 0.01f) { if (i < plan.LastRowClearances) clearancesBefore++; continue; }
            clearances.Add(plan.Clearances[i]);
        }
        plan.Clearances.Clear();
        plan.Clearances.AddRange(clearances);
        if (plan.LastRowClearances >= 0) plan.LastRowClearances -= clearancesBefore;
        plan.Cuts.RemoveAll(c => c.y < height - CutBelowSurface - 0.01f);
        plan.SunkPieces = plan.SunkClearances = 0;
        plan.Stairs = Mathf.Max(0, plan.Stairs - stairs);
        plan.Landings = Mathf.Max(0, plan.Landings - floors);
        plan.Rise = Mathf.Max(0f, plan.Rise - stairs / (float)lanes * PieceRise);
        plan.WalkedLength = Mathf.Max(0f, plan.WalkedLength - (stairs + floors) / (float)lanes * PieceRun);
        return removed;
    }

    /// <summary>One row of floors per <paramref name="width"/> at <paramref name="start"/> (its back edge), facing
    /// <paramref name="heading"/>, walking surface at <paramref name="height"/>, without counting walked length.</summary>
    internal static void AddFloorAt(StairPlan plan, StairKit kit, Vector2 start, float heading, float height, Func<float, float, float> ground, float width)
    {
        int lanes = Mathf.Max(1, Mathf.RoundToInt(width / PieceRun));
        AddRow(plan, kit, StairPieceKind.Landing, kit.Floor, start, heading, height, lanes, ground, mark: true);
        plan.Landings += lanes;
    }

    /// <summary>Furthest a staircase's top is carried on, as floors, to the top of another it meets head-on.</summary>
    public const float TopJoinReach = ReachRadius + 0.5f;

    /// <summary>And furthest aside: its last floor steps aside by up to this, overlapping the floor
    /// before it, to end on the other's edge. At 0.25 two tops 0.6 m out of line over a ridge stayed
    /// apart once the climb to the first had found another way up its face (cantilevers).</summary>
    public const float TopJoinAside = PieceRun / 2f;

    /// <summary>
    /// Floors carried on level from a staircase's top to <paramref name="to"/>, the top edge of another
    /// staircase it meets head-on at <paramref name="height"/>, the last one overlapping the floor before
    /// it so it ends exactly on that edge. The search stops within <see cref="ReachRadius"/> of its goal,
    /// and the road laid across the gap left between the two tops blended over both (at 7842,1803 the second stairs at the
    /// top of the ridge were cut off by the short road work at the top; the two stairs should connect via their snap points
    /// without the short road section).
    /// Only ahead, within <see cref="TopJoinReach"/> and <see cref="TopJoinAside"/>, at the same height; returns whether it
    /// did (then <see cref="StairPlan.End"/> is <paramref name="to"/>).
    /// </summary>
    public static bool CarryTopTo(StairPlan plan, Vector2 to, float height, Func<float, float, float> ground, float width, StairKit? kit = null)
    {
        kit ??= StairKit.Named(plan.Kit) ?? StairKit.Stone;
        if (!plan.Reached || Mathf.Abs(height - plan.End.y) > 0.05f) return false;
        Vector2 end = new Vector2(plan.End.x, plan.End.z);
        Vector2 dir = Forward(plan.EndHeading);
        Vector2 gap = to - end;
        float along = Vector2.Dot(gap, dir);
        float aside = Vector2.Dot(gap, Right(plan.EndHeading));
        if (along < 0.05f || along > TopJoinReach || Mathf.Abs(aside) > TopJoinAside) return false;
        int lanes = Mathf.Max(1, Mathf.RoundToInt(width / PieceRun));
        int floors = Mathf.CeilToInt(along / PieceRun - 0.001f);
        for (int i = 0; i < floors; i++)
        {
            // Floors may overlap (two flat stones can overlap, which looks better): the last
            // one sets back over the one before (or the top row) to end on the edge, and steps
            // aside onto it where the two tops are not quite in line.
            float back = Mathf.Min(i * PieceRun, along - PieceRun);
            Vector2 at = end + dir * back + (i == floors - 1 ? Right(plan.EndHeading) * aside : new Vector2(0f, 0f));
            AddRow(plan, kit, StairPieceKind.Landing, kit.Floor, at, plan.EndHeading, plan.End.y, lanes, ground, mark: true);
        }
        plan.Landings += floors * lanes;
        plan.WalkedLength += along;
        plan.End = new Vector3(to.x, plan.End.y, to.y);
        return true;
    }

    /// <summary>A floor laid into a plan at <paramref name="start"/>, walking on <paramref name="heading"/> at
    /// <paramref name="height"/>: the shared floor two staircases meeting at a valley's bottom start from (RoadStairRuns).</summary>
    public static void AddFloorRow(StairPlan plan, Vector2 start, float heading, float height, Func<float, float, float> ground, float width)
    {
        StairKit kit = StairKit.Named(plan.Kit) ?? StairKit.Stone;
        int lanes = Mathf.Max(1, Mathf.RoundToInt(width / PieceRun));
        AddRow(plan, kit, StairPieceKind.Landing, kit.Floor, start, heading, height, lanes, ground, mark: true);
        plan.Landings += lanes;
        plan.WalkedLength += PieceRun;
    }

    /// <summary>
    /// Floors from a staircase's top to <paramref name="to"/>, another staircase's top edge at <paramref name="height"/>,
    /// whichever way it lies (ahead, aside or behind), within <see cref="TopJoinReach"/>: the shared floor two staircases
    /// meeting over a ridge stand on, where the second's top did not land on the first's edge (at 3377,6425:
    /// the two should snap together, with no road spur at the top). Laid along the way across, the last floor set back
    /// to end on the edge. Returns whether it did (then <see cref="StairPlan.End"/> is <paramref name="to"/>).
    /// </summary>
    public static bool BridgeTopTo(StairPlan plan, Vector2 to, float height, Func<float, float, float> ground, float width, StairKit? kit = null,
        float reach = TopJoinReach, float within = 0.05f)
    {
        kit ??= StairKit.Named(plan.Kit) ?? StairKit.Stone;
        if (!plan.Reached || Mathf.Abs(height - plan.End.y) > within) return false;
        Vector2 end = new Vector2(plan.End.x, plan.End.z), gap = to - end;
        float across = gap.magnitude;
        if (across < 0.05f) return true;
        if (across > reach) return false;
        float heading = SnapHeading(Bearing(end, to));
        Vector2 dir = Forward(heading);
        float along = Vector2.Dot(gap, dir), aside = Vector2.Dot(gap, Right(heading));
        // The last floor steps aside onto the floor before it, overlapping it, never off it.
        if (Mathf.Abs(aside) > TopJoinAside + 0.01f) return false;
        int lanes = Mathf.Max(1, Mathf.RoundToInt(width / PieceRun));
        int floors = Mathf.Max(1, Mathf.CeilToInt(along / PieceRun - 0.001f));
        for (int i = 0; i < floors; i++)
        {
            float back = Mathf.Max(0f, Mathf.Min(i * PieceRun, along - PieceRun));
            Vector2 at = end + dir * back + (i == floors - 1 ? Right(heading) * aside : new Vector2(0f, 0f));
            AddRow(plan, kit, StairPieceKind.Landing, kit.Floor, at, heading, plan.End.y, lanes, ground, mark: true);
        }
        plan.Landings += floors * lanes;
        plan.WalkedLength += across;
        plan.End = new Vector3(to.x, plan.End.y, to.y);
        return true;
    }
}
