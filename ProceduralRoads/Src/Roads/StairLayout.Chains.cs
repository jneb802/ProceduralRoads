using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// What two staircases meeting over a ridge or through a valley are laid with (one owner): a staircase searched down from
/// another's top edge, floors carried on from a top and a flat run from one top to the other (JoinTops), a floor shared at a
/// valley's bottom, the first of two
/// flights through a valley cut back to where the second is laid across it (TrimBelow), and a ridge's two flights cut back
/// (TrimAbove) and joined lower down by a straight flight off one's floor onto the other's (JoinDown).
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

    /// <summary>What <see cref="TrimBelow"/>, <see cref="TrimAbove"/> and a join laid into a plan change, kept to put back.</summary>
    internal sealed class PlanSnapshot
    {
        public readonly List<StairPiece> Pieces;
        public readonly List<StairClearance> Clearances;
        public readonly List<Vector3> Cuts;
        public readonly int Stairs, Landings, Turns, TurnSteps, OverlapTurnSteps, SunkPieces, SunkClearances, LastRowPieces, LastRowClearances;
        public readonly float Rise, WalkedLength;
        public readonly Vector3? Foot;
        public PlanSnapshot(StairPlan p)
        {
            Pieces = new List<StairPiece>(p.Pieces); Clearances = new List<StairClearance>(p.Clearances); Cuts = new List<Vector3>(p.Cuts);
            Stairs = p.Stairs; Landings = p.Landings; Turns = p.Turns; TurnSteps = p.TurnSteps; OverlapTurnSteps = p.OverlapTurnSteps; SunkPieces = p.SunkPieces; SunkClearances = p.SunkClearances;
            LastRowPieces = p.LastRowPieces; LastRowClearances = p.LastRowClearances; Rise = p.Rise; WalkedLength = p.WalkedLength; Foot = p.Foot;
        }
        public void Restore(StairPlan p)
        {
            p.Pieces.Clear(); p.Pieces.AddRange(Pieces); p.Clearances.Clear(); p.Clearances.AddRange(Clearances); p.Cuts.Clear(); p.Cuts.AddRange(Cuts);
            p.Stairs = Stairs; p.Landings = Landings; p.Turns = Turns; p.TurnSteps = TurnSteps; p.OverlapTurnSteps = OverlapTurnSteps; p.SunkPieces = SunkPieces; p.SunkClearances = SunkClearances;
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
                    Untally(plan, p);
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

    /// <summary>A piece cut away from <paramref name="plan"/>: the turn counted with it (<see cref="StairPiece.Turn"/>) leaves its stats.</summary>
    private static void Untally(StairPlan plan, StairPiece p)
    {
        if (p.Turn is not (int steps, bool overlap)) return;
        plan.Turns = Mathf.Max(0, plan.Turns - 1);
        plan.TurnSteps = Mathf.Max(0, plan.TurnSteps - steps);
        if (overlap) plan.OverlapTurnSteps = Mathf.Max(0, plan.OverlapTurnSteps - steps);
    }

    /// <summary>
    /// Takes away every walked piece of <paramref name="plan"/> laid after its piece <paramref name="row"/> that stands at or
    /// over that row's walking surface (a stair whose base, a floor or filler whose walking surface, is as high: the turn's
    /// wedge off the row goes with the stair it turned to), with the columns under them, their clearances and their cuts:
    /// the top of a flight over a ridge the two staircases leave lower down (RoadStairRuns.JoinLow). With
    /// <paramref name="keepLevel"/>, the floors laid straight after the row level with it stay (the rest of a switchback's
    /// pad). Pieces laid after it lower down (a valley's shared floor) stay. Its top row is then the caller's to set.
    /// Returns how many walked pieces went.
    /// </summary>
    internal static int TrimAbove(StairPlan plan, int row, bool keepLevel, StairKit kit)
    {
        float Surface(StairPiece p) => p.Kind == StairPieceKind.Stair ? p.Position.y : p.Position.y + kit.FloorTop;
        float height = Surface(plan.Pieces[row]);
        int removed = 0, stairs = 0, floors = 0, clearance = 0;
        var kept = new List<StairPiece>();
        var clearances = new List<StairClearance>();
        bool dropping = false, level = keepLevel;
        for (int i = 0; i < plan.Pieces.Count; i++)
        {
            StairPiece p = plan.Pieces[i];
            if (p.Kind == StairPieceKind.Support) { if (!dropping) kept.Add(p); continue; }
            if (i > row)
            {
                float surface = Surface(p);
                level &= p.Kind == StairPieceKind.Landing && Mathf.Abs(surface - height) < 0.01f;
                dropping = !level && surface > height - 0.01f;
            }
            // Each walked piece's clearance stretches follow it in order (AddClearance).
            for (int s = p.Kind == StairPieceKind.Stair ? ClearanceStretches : 1; s > 0 && clearance < plan.Clearances.Count; s--, clearance++)
                if (!dropping) clearances.Add(plan.Clearances[clearance]);
            if (!dropping) { kept.Add(p); continue; }
            Untally(plan, p);
            removed++;
            if (p.Kind == StairPieceKind.Stair) stairs++;
            else if (p.Kind == StairPieceKind.Landing) floors++;
        }
        if (removed == 0) return 0;
        plan.Pieces.Clear();
        plan.Pieces.AddRange(kept);
        plan.Clearances.Clear();
        plan.Clearances.AddRange(clearances);
        plan.Cuts.RemoveAll(c => c.y > height - CutBelowSurface + 0.01f);
        plan.LastRowPieces = plan.LastRowClearances = -1;
        plan.Stairs = Mathf.Max(0, plan.Stairs - stairs);
        plan.Landings = Mathf.Max(0, plan.Landings - floors);
        plan.Rise = Mathf.Max(0f, plan.Rise - stairs * PieceRise);
        plan.WalkedLength = Mathf.Max(0f, plan.WalkedLength - (stairs + floors) * PieceRun);
        return removed;
    }


    /// <summary>Furthest a staircase's top is carried on, as floors in line with it, to a point ahead (<see cref="CarryTopTo"/>).</summary>
    public const float TopJoinReach = ReachRadius + 0.5f;

    /// <summary>
    /// Floors carried on level and in line from a staircase's top to <paramref name="to"/>, a point straight ahead of its top
    /// edge at <paramref name="height"/>, the last one overlapping the floor before it so it ends exactly there: the 2x2 on
    /// a ridge's crest the second staircase comes down from. Only ahead, in line, within <see cref="TopJoinReach"/>, at the
    /// same height; returns whether it did (then <see cref="StairPlan.End"/> is <paramref name="to"/>). Two tops out of
    /// line are joined by <see cref="JoinTops"/>.
    /// </summary>
    public static bool CarryTopTo(StairPlan plan, Vector2 to, float height, Func<float, float, float> ground, float width, StairKit? kit = null)
    {
        kit ??= StairKit.Named(plan.Kit) ?? StairKit.Stone;
        if (!plan.Reached || Mathf.Abs(height - plan.End.y) > 0.05f) return false;
        Vector2 end = new Vector2(plan.End.x, plan.End.z);
        Vector2 dir = Forward(plan.EndHeading);
        Vector2 gap = to - end;
        float along = Vector2.Dot(gap, dir);
        if (along < 0.05f || along > TopJoinReach || Mathf.Abs(Vector2.Dot(gap, Right(plan.EndHeading))) > 0.05f) return false;
        int lanes = Mathf.Max(1, Mathf.RoundToInt(width / PieceRun));
        int floors = Mathf.CeilToInt(along / PieceRun - 0.001f);
        for (int i = 0; i < floors; i++)
        {
            // Floors may overlap (two flat stones can overlap, which looks better): the last
            // one sets back over the one before (or the top row) to end on the point.
            AddRow(plan, kit, StairPieceKind.Landing, kit.Floor, end + dir * Mathf.Min(i * PieceRun, along - PieceRun), plan.EndHeading, plan.End.y, lanes, ground, mark: true);
        }
        plan.Landings += floors * lanes;
        plan.WalkedLength += along;
        plan.End = new Vector3(to.x, plan.End.y, to.y);
        return true;
    }

    /// <summary>Where a floor on <paramref name="heading"/> starts, laid on from a floor ending on <paramref name="edge"/> on
    /// <paramref name="from"/>: turned floor over floor (on the outside corner, overlapping it) where it is within
    /// <see cref="MaxFloorTurnSteps"/> of it; else straight on, <paramref name="heading"/> then set to <paramref name="from"/>.</summary>
    public static Vector2 FloorOff(Vector2 edge, float from, ref float heading, float width)
    {
        int turn = Mathf.RoundToInt((Wrap(heading - from + 180f) - 180f) / HeadingStep);
        if (Math.Abs(turn) > MaxFloorTurnSteps) { heading = from; return edge; }
        return TurnOff(edge, from, heading, turn, width / 2f, true).start;
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

    /// <summary>Most rises a join between two tops climbs (<see cref="JoinTops"/>): a short flight, not a staircase.</summary>
    public const int TopJoinStairs = 3;

    /// <summary>
    /// The gap between two staircases that meet top to top laid as a flat run of the staircase search: on from
    /// <paramref name="from"/>, one's top row, as the search carries on from a row laid (a floor turned off a top stair up to
    /// <see cref="MaxJoinTurnSteps"/> on its inside corner, its wedge filled; off a top floor up to <see cref="MaxFloorTurnSteps"/>,
    /// overlapping it), finishing head-on at <paramref name="to"/>, the other's top edge, its last floor facing back along that
    /// row and ending on its edge. Two tops out of line take a second turn, floor over floor; each stretch's last floor sets
    /// back over the one before it (two flat stones can overlap) to end where the next turns off or on the edge, so the run
    /// meets both rows edge to edge, with no gap and no jog (at 3377,6425: the two should snap together, with no road spur at
    /// the top). Of the runs that fit, the one turning least, then the shortest. Carried floors are a short walkway, not a
    /// bridge: refused where the run is longer than <paramref name="reach"/>, where any floor of it would stand higher over
    /// the ground than <see cref="StairKit.WalkwayCap"/>, or over water, so the road's crossings or another layout take the
    /// gap. Laid into <paramref name="plan"/> (pieces only) where it fits, with the two tops within <paramref name="within"/>
    /// of each other in height, or a whole number of rises apart within it, up to <see cref="TopJoinStairs"/>: then the run
    /// climbs to <paramref name="to"/> on that many straight stairs at its end, on the other's line (refused, a top-to-top join
    /// lost the pair of staircases whose tops stood a step apart: 30 of 48 over six worlds). Its stairs stand on a whole floor
    /// on from a turn, or carry on straight from <paramref name="from"/>'s row, turned off it as a stair turns off a stair.
    /// Only up: the other way round, from the higher top, it is refused for the caller to lay from the lower. Off a top
    /// floor straight on to an edge less than a floor ahead, the one floor sets back over it. With <paramref name="clip"/>, where
    /// no such run fits, the run snaps to one top and its end floor overlaps the other's top floor instead (<see cref="ClipTops"/>).
    /// </summary>
    public static TopJoin JoinTops(StairPlan plan, StairContinuation from, StairContinuation to, Func<float, float, float> ground, float width,
        StairKit kit, float reach, float within = 0.05f, bool clip = false)
    {
        if (clip) return ClipTops(plan, from, to, ground, width, kit, reach);
        float apart = Vector2.Distance(from.Edge, to.Edge);
        // Level, or a whole number of rises up to the other.
        int k = Mathf.RoundToInt((to.Height - from.Height) / PieceRise);
        if (!plan.Reached || k < 0 || Mathf.Abs(to.Height - from.Height - k * PieceRise) > within)
            return new TopJoin(false, apart, float.NaN, k < 0 ? OtherTopLower : "tops apart in height off the rise");
        if (k > TopJoinStairs) return new TopJoin(false, apart, float.NaN, FormattableString.Invariant($"tops {k} rises apart in height"));
        if (apart < 0.05f && k == 0) return new TopJoin(true, 0f, 0f, "");
        float climb = k * PieceRun;
        float half = width / 2f, onto = Wrap(to.Heading + 180f);
        bool offFloor = from.Kind == StairPieceKind.Landing;
        int most = offFloor ? MaxFloorTurnSteps : MaxJoinTurnSteps;
        // The best within reach, else the shortest beyond it (refused, but said how long).
        (int t1, int t2, float d, float l)? best = null, beyond = null;
        float bestLength = float.MaxValue;
        for (int t1 = -most; t1 <= most; t1++)
        {
            float m = Wrap(from.Heading + t1 * HeadingStep);
            (Vector2 off, _) = TurnOff(from.Edge, from.Heading, m, t1, half, offFloor);
            int t2 = Mathf.RoundToInt((Wrap(onto - m + 180f) - 180f) / HeadingStep);
            if (Math.Abs(t2) > MaxFloorTurnSteps) continue;
            Vector2 mf = Forward(m), r = to.Edge - off;
            float d, l;
            if (t2 == 0)
            {
                // In line: one stretch, straight on to the edge.
                if (Mathf.Abs(Vector2.Dot(r, Right(m))) > 0.02f) continue;
                d = 0f;
                l = Vector2.Dot(r, mf);
            }
            else
            {
                // Out along the first heading d, turned floor over floor (on the outside corner) onto the other's line, then l
                // on to its edge: the turn's offset is the same wherever along it comes.
                (Vector2 s2, _) = TurnOff(new Vector2(0f, 0f), m, onto, t2, half, true);
                r -= s2;
                Vector2 of = Forward(onto);
                float det = mf.x * of.y - mf.y * of.x;
                d = (r.x * of.y - r.y * of.x) / det;
                l = (mf.x * r.y - mf.y * r.x) / det;
                if (d < 0f) continue;
                // Off a stair, the turned floor's back stays off it: set back over a short first stretch, it reached in over
                // the stair's top (the knoll's flights joined at 37.5 laid a floor half a metre in over the top stair).
                if (!offFloor)
                {
                    Vector2 back = off + mf * d + s2, side = Right(onto) * half;
                    if (Vector2.Dot(back + side - from.Edge, Forward(from.Heading)) < -0.01f
                        || Vector2.Dot(back - side - from.Edge, Forward(from.Heading)) < -0.01f) continue;
                }
            }
            // A whole floor at least on to the edge, or to the stairs up to it: a shorter one would reach into the other's top
            // (or under the stairs); else, in line, the stairs straight on from the row, turned off it as a stair off a stair.
            float floors = l - climb;
            bool stairsOff = k > 0 && t2 == 0 && Mathf.Abs(floors) < 0.02f && (t1 == 0 || (!offFloor && Math.Abs(t1) <= MaxStairTurnSteps));
            // Off a top floor straight on, less than a floor short of the other's edge: one floor set back over it.
            bool setBack = k == 0 && offFloor && t1 == 0 && t2 == 0 && floors > 0.04f;
            if (floors < PieceRun - 0.01f && !stairsOff && !setBack) continue;
            if (d + l > reach)
            {
                if (beyond == null || d + l < beyond.Value.d + beyond.Value.l) beyond = (t1, t2, d, l);
                continue;
            }
            int turns = Math.Abs(t1) + Math.Abs(t2);
            float length = d + l + turns * 1000f;
            if (length < bestLength) { bestLength = length; best = (t1, t2, d, l); }
        }
        if (best == null && beyond is { } far) return new TopJoin(false, far.d + far.l, float.NaN, FormattableString.Invariant($"longer than {reach:F0} m"));
        if (best is not { } run) return new TopJoin(false, apart, float.NaN, k > 0 ? FormattableString.Invariant($"no run up {k} rise(s) fits") : "no flat run fits");

        // The run as the search would lay it, on from the laid row (never emitted), then emitted as any staircase is.
        Node node = Root(from.Kind, from.Edge, from.Heading, from.Height);
        float first = Wrap(from.Heading + run.t1 * HeadingStep);
        float level = run.l - climb;
        (Vector2 s1, Vector2 p1) = TurnOff(from.Edge, from.Heading, first, run.t1, half, offFloor);
        if (run.t2 == 0 && level < 0.02f) node = JoinStairs(node, k, onto, run.t1, s1, p1);
        else if (run.t2 == 0) node = JoinStretch(node, first, run.t1, s1, p1, level, offFloor, setBack: offFloor && run.t1 == 0 && level < PieceRun);
        else
        {
            node = JoinStretch(node, first, run.t1, s1, p1, run.d, offFloor);
            // Where the first stretch is shorter than a floor, its one floor reaches on under the turned one.
            node.Edge = s1 + Forward(first) * run.d;
            (Vector2 s2, Vector2 p2) = TurnOff(node.Edge, first, onto, run.t2, half, true);
            node = JoinStretch(node, onto, run.t2, s2, p2, level, true);
        }
        if (k > 0 && node.Kind == StairPieceKind.Landing) node = JoinStairs(node, k, onto, 0, node.Edge, node.Edge);
        return LayJoin(plan, node, run.d + run.l, ground, width, kit);
    }

    /// <summary>Why <see cref="JoinTops"/> refuses from the higher top (the caller lays it from the lower).</summary>
    public const string OtherTopLower = "the other top lower";

    /// <summary>Most a clipped join's end floor stands over the top it meets (<see cref="ClipTops"/>): half a rise, a step no
    /// taller than a stair's.</summary>
    public const float ClipStep = PieceRise / 2f;

    /// <summary>
    /// Two tops no snapped run joins (<see cref="JoinTops"/>: tops off the rise from each other, out of line by less than a
    /// turn makes up, or closer than a floor) joined by snapping to one top and clipping the other: "snap one side and clip
    /// the other good enough". The run's end floor at the other end overlaps that top's floor, reaching a quarter to a whole
    /// floor into it (half where it fits), never past its far end; floors may overlap, but a stair is never overlapped (its
    /// treads would be covered), so at a top stair the end floor, in line with it, stops on its edge. Either way up to half the
    /// run's width aside, and level with that top or over it by up to <see cref="ClipStep"/>, never under it, so the walk across
    /// steps no more than that. Two ways, each up to <see cref="TopJoinStairs"/> straight stairs: snapped on from
    /// <paramref name="from"/> as a snapped run sets off (its turns), stairs then a floor clipping <paramref name="to"/>; or in
    /// line with <paramref name="to"/>, finishing head-on on its edge, from a floor clipping <paramref name="from"/>. Only up,
    /// as <see cref="JoinTops"/> (the caller asks from either top, so a top lower than the other is reached from it); of those
    /// that fit, the one turning least, then the shortest, within <paramref name="reach"/>, the walkway cap and dry ground.
    /// </summary>
    private static TopJoin ClipTops(StairPlan plan, StairContinuation from, StairContinuation to, Func<float, float, float> ground, float width, StairKit kit, float reach)
    {
        float apart = Vector2.Distance(from.Edge, to.Edge), half = width / 2f, onto = Wrap(to.Heading + 180f), rise = to.Height - from.Height;
        if (!plan.Reached) return new TopJoin(false, apart, float.NaN, "no clipped run fits");
        // How far into a top's row (from its far edge, `inward`) a floor clipping it reaches: a floor's a quarter to a whole
        // floor, half where it fits; a stair's not at all.
        static (float lo, float aim, float hi) Clip(StairContinuation top) =>
            top.Kind == StairPieceKind.Landing ? (PieceRun / 4f, PieceRun / 2f, PieceRun) : (0f, 0f, 0f);
        // Whether a floor laid along `dir`, its edge's middle at `at`, clips a top: that middle and both its corners within the
        // reach into its row, up to half the width aside.
        bool Clips(StairContinuation top, Vector2 inward, Vector2 at, float dir)
        {
            var (lo, _, hi) = Clip(top);
            Vector2 side = Right(dir) * half;
            float a = Vector2.Dot(at - top.Edge, inward);
            return a >= lo - 0.01f && Mathf.Abs(Vector2.Dot(at - top.Edge, Right(top.Heading))) <= half + 0.01f
                && Vector2.Dot(at + side - top.Edge, inward) <= hi + 0.01f && Vector2.Dot(at - side - top.Edge, inward) <= hi + 0.01f;
        }
        // The length of a straight run between lo and hi whose end lies a0 + length * cos into the top's row, as near its aim as fits.
        float? Length(StairContinuation top, float lo, float hi, float a0, float cos)
        {
            var (reachLo, aim, reachHi) = Clip(top);
            float min = Mathf.Max(lo, (reachLo - a0) / cos), max = Mathf.Min(hi, (reachHi - a0) / cos);
            return min > max + 0.001f ? null : Mathf.Clamp((aim - a0) / cos, min, max);
        }
        int Turn(float a, float b) => Mathf.RoundToInt((Wrap(b - a + 180f) - 180f) / HeadingStep);
        Node? best = null;
        float bestScore = float.MaxValue, laidLength = 0f;
        var why = new List<string>();

        // Snapped on from `from`: a whole number of rises up, the end floor over `to` (or on its stair's edge) by up to ClipStep.
        int up = Mathf.CeilToInt(rise / PieceRise - 0.01f);
        if (up < 0 || up * PieceRise - rise > ClipStep + 0.01f) why.Add("on from it, the other top lower");
        else if (up > TopJoinStairs) why.Add(FormattableString.Invariant($"tops {up} rises apart"));
        else
        {
            bool offFloor = from.Kind == StairPieceKind.Landing;
            int most = offFloor ? MaxFloorTurnSteps : MaxJoinTurnSteps;
            // Onto a floor turned up to a floor over a floor; onto a stair in line.
            int onFloor = to.Kind == StairPieceKind.Landing ? MaxFloorTurnSteps : 0;
            Vector2 inward = Forward(onto);
            for (int t1 = -most; t1 <= most; t1++)
            {
                float m = Wrap(from.Heading + t1 * HeadingStep);
                int t2 = Turn(m, onto);
                if (Math.Abs(t2) > onFloor) continue;
                (Vector2 s1, Vector2 p1) = TurnOff(from.Edge, from.Heading, m, t1, half, offFloor);
                Vector2 mf = Forward(m);
                float a0 = Vector2.Dot(s1 - to.Edge, inward), cos = Vector2.Dot(mf, inward);
                // Level: a whole floor on at least, or one set back over a top floor straight on. Up: the stairs straight on
                // from the row (turned off it as a stair off a stair), or a whole floor on first; then the end floor.
                float? length;
                if (up == 0) length = Length(to, offFloor && t1 == 0 ? 0.05f : PieceRun - 0.01f, reach, a0, cos);
                else
                {
                    float stairs = (up + 1) * PieceRun;
                    length = Length(to, stairs + PieceRun, reach, a0, cos);
                    if ((t1 == 0 || (!offFloor && Math.Abs(t1) <= MaxStairTurnSteps)) && Length(to, stairs, stairs, a0, cos) is { } off)
                        length = off;
                }
                if (length is not { } L || !Clips(to, inward, s1 + mf * L, m)) continue;
                float score = L + (Math.Abs(t1) + Math.Abs(t2)) * 1000f;
                if (score >= bestScore) continue;
                Node node = Root(from.Kind, from.Edge, from.Heading, from.Height);
                if (up == 0) node = JoinStretch(node, m, t1, s1, p1, L, offFloor, setBack: offFloor && t1 == 0 && L < PieceRun);
                else
                {
                    float flat = L - (up + 1) * PieceRun;
                    node = flat < 0.02f ? JoinStairs(node, up, m, t1, s1, p1)
                        : JoinStairs(JoinStretch(node, m, t1, s1, p1, flat, offFloor), up, m, 0, s1 + mf * flat, s1 + mf * flat);
                    node = JoinStretch(node, m, 0, node.Edge, node.Edge, PieceRun, false);
                }
                bestScore = score;
                best = node;
                laidLength = L;
            }
            if (best == null) why.Add(FormattableString.Invariant($"no run on from it clips the other ({up} rise(s) up)"));
        }

        // In line with `to`, finishing head-on on its edge, from a floor clipping `from` by up to ClipStep, whole rises below `to`.
        int below = Mathf.FloorToInt(rise / PieceRise + 0.01f);
        float lift = rise - below * PieceRise;
        int turned = Turn(from.Heading, onto);
        if (below < 0 || lift > ClipStep + 0.01f) why.Add("in line with the other, its own top lower");
        else if (below > TopJoinStairs) why.Add(FormattableString.Invariant($"tops {below} rises apart"));
        else if (Math.Abs(turned) > (from.Kind == StairPieceKind.Landing ? MaxFloorTurnSteps : 0)) why.Add("in line with the other, turned off its own top");
        else
        {
            Vector2 of = Forward(onto), inward = -Forward(from.Heading);
            // The run's start, its length back from `to`'s edge, lies a0 + length * cos into `from`'s row.
            float a0 = Vector2.Dot(to.Edge - from.Edge, inward), cos = Vector2.Dot(-of, inward);
            if (Length(from, (below + 1) * PieceRun - 0.01f, reach, a0, cos) is { } L && Clips(from, inward, to.Edge - of * L, onto)
                && L + Math.Abs(turned) * 1000f < bestScore)
            {
                Vector2 start = to.Edge - of * L;
                Node node = JoinStretch(Root(StairPieceKind.Landing, start, onto, from.Height + lift), onto, 0, start, start, L - below * PieceRun, false);
                if (below > 0) node = JoinStairs(node, below, onto, 0, node.Edge, node.Edge);
                best = node;
                laidLength = L;
            }
            else if (best == null) why.Add("no run in line with the other clips its own");
        }
        if (best == null) return new TopJoin(false, apart, float.NaN, string.Join(", ", why));
        return LayJoin(plan, best, laidLength, ground, width, kit);
    }

    /// <summary>A join's root: the row it carries on from (never emitted), its far edge at <paramref name="edge"/>.</summary>
    private static Node Root(StairPieceKind kind, Vector2 edge, float heading, float height)
    {
        Vector2 start = edge - Forward(heading) * PieceRun;
        return new Node
        {
            Kind = kind, Start = start, Edge = edge, Heading = heading, Height = height, Placed = true,
            Parent = new Node { Edge = start, Start = start, Heading = heading, Height = height },
        };
    }

    /// <summary>A join's floor, level with the piece before it.</summary>
    private static Node JoinFloor(Node parent, float heading, int turn, Vector2 start, Vector2 pivot, Vector2 edge, bool overlaps) => new()
    {
        Parent = parent, Kind = StairPieceKind.Landing, Start = start, Edge = edge, Heading = heading, Height = parent.Height,
        TurnSteps = turn, Pivot = pivot, Overlaps = overlaps && turn != 0, Centre = start + Forward(heading) * (PieceRun / 2f), Depth = parent.Depth + 1,
    };

    /// <summary>A join's stretch of floors from start, its last set back to end exactly at its length: the first turned off the
    /// piece before. Straight on off a top floor, a stretch shorter than a floor sets back over it (<paramref name="setBack"/>).</summary>
    private static Node JoinStretch(Node parent, float heading, int turn, Vector2 start, Vector2 pivot, float length, bool overlaps, bool setBack = false)
    {
        Vector2 dir = Forward(heading);
        int floors = Mathf.Max(1, Mathf.CeilToInt(length / PieceRun - 0.001f));
        for (int i = 0; i < floors; i++)
        {
            Vector2 at = start + dir * Mathf.Max(setBack ? length - PieceRun : 0f, Mathf.Min(i * PieceRun, length - PieceRun));
            parent = i == 0 ? JoinFloor(parent, heading, turn, at, pivot, start + dir * Mathf.Min(PieceRun, length), overlaps)
                : JoinFloor(parent, heading, 0, at, at, start + dir * Mathf.Min((i + 1) * PieceRun, length), false);
        }
        return parent;
    }

    /// <summary>A join's <paramref name="stairs"/> straight stairs up on <paramref name="heading"/>, the first turned off the piece before.</summary>
    private static Node JoinStairs(Node parent, int stairs, float heading, int turn, Vector2 start, Vector2 pivot)
    {
        for (int i = 0; i < stairs; i++, turn = 0, pivot = start = parent.Edge)
            parent = new Node
            {
                Parent = parent, Kind = StairPieceKind.Stair, Start = start, Edge = start + Forward(heading) * PieceRun, Heading = heading,
                Height = parent.Height + PieceRise, TurnSteps = turn, Pivot = pivot, Centre = start + Forward(heading) * (PieceRun / 2f), Depth = parent.Depth + 1,
            };
        return parent;
    }

    /// <summary>A join's run emitted as any staircase is and laid into <paramref name="plan"/> (pieces only): refused where any
    /// floor of it would stand higher over the ground than <see cref="StairKit.WalkwayCap"/>, or over water.</summary>
    private static TopJoin LayJoin(StairPlan plan, Node node, float length, Func<float, float, float> ground, float width, StairKit kit)
    {
        var laid = new StairPlan { Kit = kit.Name, Reached = true };
        Emit(laid, node, ground, width, kit);
        // How high over the ground its floors stand, at their corners and centres, and whether any stands over water.
        float highest = 0f;
        bool wet = false;
        foreach (StairPiece p in laid.Pieces)
        {
            if (p.Kind != StairPieceKind.Landing) continue;
            Vector2 c = new(p.Position.x, p.Position.z), f = Forward(p.YawDegrees) * (PieceRun / 2f), r = Right(p.YawDegrees) * (PieceRun / 2f);
            foreach (Vector2 at in new[] { c, c + f + r, c + f - r, c - f + r, c - f - r })
            {
                float g = ground(at.x, at.y);
                highest = Mathf.Max(highest, p.Position.y + kit.FloorTop - g);
                wet |= !RoadWater.BuiltDry(g);
            }
        }
        if (wet) return new TopJoin(false, length, highest, "over water");
        if (highest > StairKit.WalkwayCap) return new TopJoin(false, length, highest, FormattableString.Invariant($"over the {StairKit.WalkwayCap:F0} m walkway cap"));
        plan.Pieces.AddRange(laid.Pieces);
        plan.Clearances.AddRange(laid.Clearances);
        plan.Cuts.AddRange(laid.Cuts);
        plan.Landings += laid.Landings;
        plan.Stairs += laid.Stairs;
        plan.Rise += laid.Rise;
        plan.Turns += laid.Turns;
        plan.TurnSteps += laid.TurnSteps;
        plan.OverlapTurnSteps += laid.OverlapTurnSteps;
        plan.WalkedLength += length;
        return new TopJoin(true, length, highest, "");
    }
    /// <summary>Most of a ridge join's foot stair that stands over the floor its foot rests on, along it (<see cref="JoinDown"/>:
    /// a Mistlands ridge joined by hand stood 1.16 m of it on the pad below).</summary>
    public const float FootOverFloor = 1.25f;

    /// <summary>Furthest a corner of a ridge join's foot stands out past the floor it rests on (<see cref="JoinDown"/>: 0.35 m
    /// where the ridge was joined by hand).</summary>
    public const float FootPastFloor = 0.4f;

    /// <summary>
    /// Whether a ridge join's foot stair, its low edge's middle at <paramref name="foot"/> climbing on <paramref name="climb"/>,
    /// rests on <paramref name="floors"/> (one flight's floors level with its foot): its low edge's middle on one of them, its
    /// corners within <see cref="FootPastFloor"/> of them, and no more than <see cref="FootOverFloor"/> of it over them.
    /// </summary>
    internal static bool FootRests(Vector2 foot, float climb, float width, IReadOnlyList<StairPiece> floors)
    {
        bool On(Vector2 at, float past)
        {
            foreach (StairPiece f in floors)
            {
                Vector2 d = at - new Vector2(f.Position.x, f.Position.z);
                if (Mathf.Abs(Vector2.Dot(d, Forward(f.YawDegrees))) <= PieceRun / 2f + past && Mathf.Abs(Vector2.Dot(d, Right(f.YawDegrees))) <= PieceRun / 2f + past)
                    return true;
            }
            return false;
        }
        Vector2 side = Right(climb) * (width / 2f);
        return On(foot, 0.01f) && On(foot + side, FootPastFloor) && On(foot - side, FootPastFloor) && !On(foot + Forward(climb) * FootOverFloor, 0f);
    }

    /// <summary>Where a ridge join (<see cref="JoinDown"/>) carried <paramref name="stairs"/> straight down off
    /// <paramref name="high"/>, turned <paramref name="turn"/> heading steps, sets its foot: the low edge's middle, and the
    /// heading it climbs on from there.</summary>
    internal static (Vector2 foot, float climb) JoinFoot(StairContinuation high, int turn, int stairs, float width)
    {
        float down = Wrap(high.Heading + turn * HeadingStep);
        (Vector2 top, _) = TurnOff(high.Edge, high.Heading, down, turn, width / 2f, false);
        return (top + Forward(down) * (stairs * PieceRun), Wrap(down + 180f));
    }

    /// <summary>
    /// A ridge's two flights joined where they pass close, as by hand at a Mistlands ridge (from a floor at 90.9 three straight
    /// stairs down onto the other's switchback pad at 87.9, both tops taken away): <paramref name="stairs"/> straight stairs
    /// carried down off <paramref name="high"/>, a floor of one flight, as the search carries a stair on off a floor (turned
    /// <paramref name="turn"/> heading steps, up to <see cref="MaxJoinTurnSteps"/>, on its inside corner, the wedge filled level
    /// with the floor), their foot resting on the other's floor below (<see cref="FootRests"/>, the caller's to check). No stair
    /// is buried deeper than a road staircase's may be. Laid into <paramref name="plan"/> (pieces only) as a flight climbing
    /// from its foot; returns whether it was.
    /// </summary>
    public static bool JoinDown(StairPlan plan, StairContinuation high, int turn, int stairs, Func<float, float, float> ground, float width, StairKit kit)
    {
        float half = width / 2f;
        (Vector2 foot, float climb) = JoinFoot(high, turn, stairs, width);
        Vector2 dir = Forward(climb);
        Node node = new()
        {
            Kind = StairPieceKind.Landing, Edge = foot, Start = foot - dir * PieceRun, Heading = climb, Height = high.Height - stairs * PieceRise, Placed = true,
            Parent = new Node { Edge = foot, Start = foot, Heading = climb, Height = high.Height - stairs * PieceRise },
        };
        for (int i = 0; i < stairs; i++)
        {
            Vector2 start = node.Edge;
            Fit(start, dir, Right(climb), half, node.Height, PieceRise, ground, out float floatGap, out float deep, out _);
            if (deep > StairCover.RoadStaircase) return false;
            node = new Node
            {
                Parent = node, Kind = StairPieceKind.Stair, Start = start, Edge = start + dir * PieceRun, Heading = climb, Height = node.Height + PieceRise,
                Pivot = start, Centre = start + dir * (PieceRun / 2f), Depth = node.Depth + 1, Float = floatGap, Buried = deep,
            };
        }
        var flight = new StairPlan { Kit = kit.Name, Reached = true };
        Emit(flight, node, ground, width, kit);
        // The wedge between the floor and the top stair turned off it, as Emit fills a stair's turn off a floor.
        if (turn != 0)
        {
            Vector2 pivot = high.Edge + Right(high.Heading) * ((turn > 0 ? 1f : -1f) * half);
            AddWedge(flight, kit, pivot, high.Heading, high.Height, turn, width, true, ground, false);
        }
        plan.Pieces.AddRange(flight.Pieces);
        plan.Clearances.AddRange(flight.Clearances);
        plan.Cuts.AddRange(flight.Cuts);
        plan.Stairs += flight.Stairs;
        plan.Turns += flight.Turns;
        plan.TurnSteps += flight.TurnSteps;
        plan.WalkedLength += flight.WalkedLength;
        return true;
    }
}

/// <summary>What <see cref="StairLayout.JoinTops"/> did: whether it laid the run, how long the run is (or the gap, where no run
/// fits), how high over the ground its floors stand at most (NaN where none was laid out), and why it was refused.</summary>
public readonly struct TopJoin
{
    public readonly bool Laid;
    public readonly float Length, Highest;
    public readonly string Why;

    public TopJoin(bool laid, float length, float highest, string why)
    {
        Laid = laid; Length = length; Highest = highest; Why = why;
    }
}
