using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The road itself carried on from its end at a placed location onto the landing in front of it, where a road can get there:
/// stairs only where it cannot, or where the road already arrives on a staircase. Its line (<see cref="Route"/>, to
/// <see cref="PlacedLocationShape.Ends"/>) is the road's own last stretch, planned with the road by the road's own planner
/// (RoadNetworkGenerator.PlanWayIn): its water crossed as the road's is (a wade, a raised ford or a bridge, as the terrain gives),
/// its earthwork and ridges judged as the road's, its joint with the road smoothed with it. Only the Location is its own: the
/// road stops outside a location's whole footprint, and this last stretch is let into its own footprint along itself only
/// (<see cref="RoadSpatialGrid.RoadPoint.intoLocation"/>, RoadLocationProtection.Targets.Into): its earthwork and paint there
/// reach <see cref="InsideReach"/> from it and no further, and it keeps that far out of the entrance pit and the location's rock
/// and boulders (<see cref="PlacedLocationShape.KeepsOff"/>).
/// </summary>
public static class RoadLocationEntry
{
    /// <summary>Inside a footprint a road point levels the ground out to its half width and this much more.</summary>
    public const float InsideMargin = RoadConstants.TerrainBlendMargin;
    /// <summary>At most this many degrees round the location's rock between waypoints of a road led
    /// round it: at 20 the road's bends had no room for their curves.</summary>
    public const float AroundStep = 45f;

    /// <summary>How much further than its reach the road's end is aimed off the pit, for the
    /// planner's own small moves of its points.</summary>
    public const float EndSlack = 0.5f;

    /// <summary>How much further than its reach a road led round the rock keeps off it: the
    /// planner rounds its bends inside the waypoints, and at <see cref="EndSlack"/> a road came
    /// 0.1 m too near (21.9 of 22.0 m, two frost caves).</summary>
    public const float RingSlack = 2f;

    /// <summary>How many landings a road onto a location tries.</summary>
    public const int RoadLandingTries = 8;

    /// <summary>How far another road within a road's width of an approach may stand above or below it.</summary>
    public const float OtherRoadRise = 1f;
    /// <summary>Beyond a road's width, another road may stand this much higher or lower for every
    /// metre of gap between their edges: a bank of 1 in 2. Checked only within a road's width,
    /// approaches ran 6-10 m from a road several metres above or below them, and the terrain
    /// between became walls (TrollCave02 at 6204,-1985: criss-crossing roads with
    /// weird changes of heights).</summary>
    public const float OtherRoadBank = 0.5f;

    /// <summary>How far past its half width a road keeps off a large boulder it hugs. Negative: the
    /// road's edge may lie that far inside a large boulder's box, which overstates a rough rock (its
    /// colliders' corners); at 0.5 m out, roads onto two frost caves were refused 1.5 and 2.2 m off a
    /// rock1_mountain's box.</summary>
    public const float HugGap = -1f;

    /// <summary>How far a point of the road reaches inside a footprint.</summary>
    public static float InsideReach(float width) => width * 0.5f + InsideMargin;

    /// <summary>
    /// The road from <paramref name="request"/>'s end onto <paramref name="shape"/>'s landing,
    /// round the location's side first where it ends behind it (as a staircase is led), over
    /// <paramref name="ground"/>; null, with <paramref name="why"/>, where a road cannot get there.
    /// </summary>
    /// <param name="roadsNear">The network's road points within a distance of a point (none when null).</param>
    /// <param name="planner">The road's own planner with the road this is the last stretch of (RoadNetworkGenerator.PlanEntryFrom);
    /// null: planned from the road's end alone (RoadNetworkGenerator.WayInRoad.Alone).</param>
    /// <param name="search">The road's own pathfinder, from the road's end to an end on the landing (<see cref="WayInSearch"/>);
    /// null: this thread's (RoadNetworkGenerator.SearchWayIn), none outside a search.</param>
    internal static RoadSpatialGrid.PlannedPath? Plan(LocationApproachRequest request, PlacedLocationShape shape, float width,
        Func<float, float, float> ground, out string why,
        Func<Vector2, float, IEnumerable<(Vector2 at, float height)>>? roadsNear = null, WayInPlanner? planner = null, WayInSearch? search = null)
    {
        why = "";
        float reach = InsideReach(width);
        planner ??= (List<Vector2> line, float landing, out string one) =>
            RoadNetworkGenerator.PlanWayIn(RoadNetworkGenerator.WayInRoad.Alone(request), line, landing, shape.Centre, width, out one, out RoadNetworkGenerator.WayInPlan? p) != null ? p : null;
        search ??= (a, b) => RoadNetworkGenerator.SearchWayIn(a, b, shape.Centre);
        // Up to the level ground beside the mouth, at its height (the road or stairs come up level), its earthwork reach clear of the entrance,
        // the nearest landing the road can get to; failing any, the point in front of the pit.
        // Nearest the road's end first: ranked by the front axis alone, a road from the west tried
        // only the landings east of a frost cave's mouth, past a rock and 10 m of earthwork, and
        // never the ledge on its own side.
        // Where it may end, best first (PlacedLocationShape.Ends, the one owner): on the Location's levelled ground, level with it,
        // nearest its entrance's landing, or anywhere on it nearest the road's end; with no levelled ground, its landing as before.
        // A Location's stakes are no obstacle (LocationRock.ClearedForAWay): the rock pass removes those the way goes through. An
        // entrance with no end clear of the Location (its landing under its rock: inside a cave past its mouth) gets no road;
        // only a Location with no entrance is aimed at its landing as before.
        Vector2 from = request.RoadEnd;
        var tried = new List<string>();
        var landings = shape.Ends(ground, from, reach + EndSlack, RoadLandingTries, PlacedLocationShape.Walker.Road(width).BodyMargin);
        if (landings.Count == 0 && shape.Door == null)
        {
            Vector2 aim = shape.LandingFor(reach + EndSlack);
            landings.Add((aim, ground(aim.x, aim.y)));
        }
        if (landings.Count == 0) tried.Add(RoadStairRuns.NoEndClear);
        foreach (var (target, height) in landings)
        {
            RoadSpatialGrid.PlannedPath? plan = PlanTo(request, shape, width, target, height, planner, ground, search, out string one, roadsNear);
            if (plan != null) return plan;
            tried.Add(FormattableString.Invariant($"to ({target.x:F0}, {height:F1}, {target.y:F0}) {one}"));
        }
        why = "road: " + string.Join("; ", tried);
        return null;
    }

    /// <summary>The road's own planner for a way in along a line onto a landing at a height: the way in (the road's last planned
    /// stretch), or null with why (RoadNetworkGenerator.PlanWayIn, over the road it is the last stretch of).</summary>
    internal delegate RoadNetworkGenerator.WayInPlan? WayInPlanner(List<Vector2> line, float landing, out string why);

    /// <summary>The road's own pathfinder for a way in from the road's end to an end on the landing: the searched path, its first
    /// point the road's end and its last that end, or null where none is found.</summary>
    internal delegate List<Vector2>? WayInSearch(Vector2 from, Vector2 to);

    /// <summary>Why a way in searched round a crest or a valley was not taken, after why its straight line was not.</summary>
    internal const string SearchedRefusal = "searched round the crest: ";

    private static RoadSpatialGrid.PlannedPath? PlanTo(LocationApproachRequest request, PlacedLocationShape shape, float width,
        Vector2 target, float landing, WayInPlanner planner, Func<float, float, float> ground, WayInSearch search, out string why,
        Func<Vector2, float, IEnumerable<(Vector2 at, float height)>>? roadsNear)
    {
        why = "";
        Vector2 roadEnd = request.RoadEnd;
        if (Vector2.Distance(roadEnd, target) > request.Reach) { why = "too far"; return null; }
        PlacedLocationShape.Walker walker = PlacedLocationShape.Walker.Road(width);
        List<Vector2> line = Route(roadEnd, shape, target, walker);
        // The straight line first (PlanAlong: the road's planner, its ridges, walk, grade and keep-offs, ShapeRefusal). Where it is
        // refused and runs over a crest or a valley a staircase would climb (the stairs' own crest test, RoadStairRuns.Turn), the
        // way in's road searched by the road's own pathfinder, over its cells, round the crest where the ground lets it, planned
        // and judged as any way in is; only then a staircase (the caller's next way).
        RoadSpatialGrid.PlannedPath? plan = PlanAlong(request, shape, width, line, landing, planner, walker, out why, roadsNear);
        if (plan != null || line.Count != 2 || RoadStairRuns.Turn(roadEnd, target, ground) == null) return plan;
        string straightWhy = why;
        List<Vector2>? searched = search(roadEnd, target);
        if (searched == null || searched.Count <= 2) { why = straightWhy + "; " + SearchedRefusal + "no path found"; return null; }
        var round = new List<Vector2>(searched);
        round[0] = roadEnd;
        round[round.Count - 1] = target;
        RoadSpatialGrid.PlannedPath? found = PlanAlong(request, shape, width, round, landing, planner, walker, out string one, roadsNear);
        if (found == null) { why = straightWhy + "; " + SearchedRefusal + one; return null; }
        if (RoadNetworkGenerator.RoadsLog)
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(FormattableString.Invariant(
                $"[WAYIN] searched round the crest from ({roadEnd.x:F1}, {roadEnd.y:F1}) onto ({target.x:F1}, {landing:F1}, {target.y:F1}), {round.Count} points; the straight line: {straightWhy}"));
        return found;
    }

    /// <summary>The way in along <paramref name="line"/> onto the landing at <paramref name="landing"/>: planned with the road by
    /// <paramref name="planner"/>, and judged against the Location's shape, the road behind it, the roads beside it and the
    /// Location's pieces; null with why.</summary>
    private static RoadSpatialGrid.PlannedPath? PlanAlong(LocationApproachRequest request, PlacedLocationShape shape, float width,
        List<Vector2> line, float landing, WayInPlanner planner, PlacedLocationShape.Walker walker, out string why,
        Func<Vector2, float, IEnumerable<(Vector2 at, float height)>>? roadsNear)
    {
        why = "";
        Vector2 roadEnd = request.RoadEnd;
        Vector2 target = line[line.Count - 1];
        // Planned with the road as its last stretch by the road's own planner: its water, earthwork and ridges are the road's.
        // Into this location alone (its identity, the shape's centre): another location's footprint, nested in it or over it, is not.
        RoadNetworkGenerator.WayInPlan? planned = planner(line, landing, out string refused);
        // The road as shaped with it (pulled, swayed, one spline through the joint): where it runs within the lead-round ring of the
        // Location (PlacedLocationShape.LeadRoundRing) it keeps off the Location's edge as the way in does, so a shaped line never cuts
        // back toward the rock the way in was led round (PlacedLocationShape.KeepsOff on the shaped points, not on the route).
        // Every piece of it, not only the one the way in was split from: a way in that is the road's own ford whole goes on from the
        // piece before it, which may run into the footprint up to its bank (RoadNetworkGenerator.PlanWayIn). The footprint it
        // enters is not kept clear by the pull or the sway (RoadLocationProtection.Targets.Into): where the shaped road is refused by the Location's shape, it is planned again
        // on its plain line (unswayed, RoadSpatialGrid.SuppressWiggle), as the sway's own fallback plans any refused swayed road.
        float ring = shape.LeadRoundRing(walker);
        string? OffItsShape(RoadNetworkGenerator.WayInPlan p)
        {
            foreach (RoadSpatialGrid.PlannedPath piece in p.Plans)
                foreach (Vector2 q in piece.Points)
                {
                    if (Vector2.Distance(q, shape.Centre) > ring) continue;
                    string off = shape.KeepsOff(q, walker);
                    if (off != "") return FormattableString.Invariant($"{off} at ({q.x:F1}, {q.y:F1})");
                }
            foreach (Vector2 q in p.Entry.Points)
            {
                string into = shape.KeepsOff(q, walker);
                if (into != "") return FormattableString.Invariant($"{into} at ({q.x:F1}, {q.y:F1})");
            }
            return null;
        }
        string? offShape = planned != null ? OffItsShape(planned) : null;
        if (offShape != null && !RoadSpatialGrid.SuppressWiggle)
        {
            RoadSpatialGrid.SuppressWiggle = true;
            try { planned = planner(line, landing, out refused); }
            finally { RoadSpatialGrid.SuppressWiggle = false; }
            if (planned != null) offShape = OffItsShape(planned);
            else refused = offShape;
        }
        RoadSpatialGrid.PlannedPath? plan = planned?.Entry;
        if (plan == null)
        {
            why = refused;
            // For the log: the two heights a refused grade had to join, and how far apart.
            if (why.StartsWith("grade:", StringComparison.Ordinal))
                why += FormattableString.Invariant($" (road end {request.RoadHeight:F1}, landing {landing:F1}, {Vector2.Distance(roadEnd, target):F1} m apart)");
            return null;
        }
        if (offShape != null) { why = offShape; return null; }
        // The way in starts at its joint with the road, its end away from the landing, which may stand back on the road's own
        // tail: it is the road's last planned stretch measured back from the landing (RoadNetworkGenerator.PlanWayIn), and the
        // road past the joint is what it replaces. Its points from the joint until it first comes within a width of the road's
        // end run over that stretch, not back over its road; from there on every point keeps off the road's tail.
        int count = plan.Points.Count;
        bool jointFirst = Vector2.Distance(plan.Points[0], target) >= Vector2.Distance(plan.Points[count - 1], target);
        int FromJoint(int i) => jointFirst ? i : count - 1 - i;
        int atItsEnd = -1;
        for (int k = 0; k < count && atItsEnd < 0; k++)
            if (Vector2.Distance(plan.Points[FromJoint(k)], roadEnd) <= width) atItsEnd = k;
        for (int i = 0; i < plan.Points.Count; i++)
        {
            Vector2 p = plan.Points[i];
            bool overWhatItReplaces = FromJoint(i) < atItsEnd;
            if (!overWhatItReplaces && Vector2.Distance(p, roadEnd) > width && NearTail(p, request.RoadTail, roadEnd, width))
            { why = FormattableString.Invariant($"back over its own road at ({p.x:F1}, {p.y:F1})"); return null; }
            // Nor along another road at another height: led round a TrollCave's rock, an approach ran
            // beside the road below it on 6 m of fill (double roads).
            // Against the nearest point only: every point within the width of a sloping road differs by
            // its grade across that width, and refused an approach 1.0 m off at a TrollCave.
            if (roadsNear != null && Vector2.Distance(p, roadEnd) > 2f * width)
            {
                (Vector2 at, float height)? nearest = null;
                foreach (var q in roadsNear(p, RoadProfile.Footprint(width)))
                    if (nearest == null || (q.at - p).sqrMagnitude < (nearest.Value.at - p).sqrMagnitude) nearest = q;
                if (nearest is { } n)
                {
                    float gap = Mathf.Max(0f, Vector2.Distance(n.at, p) - width);
                    if (FloatBits.Abs(n.height - plan.Heights[i]) > OtherRoadRise + OtherRoadBank * gap)
                    { why = FormattableString.Invariant($"beside another road at ({n.at.x:F1}, {n.at.y:F1}), {n.height - plan.Heights[i]:F1} m off its height {gap:F1} m away"); return null; }
                }
            }
        }
        // Nor where its earthwork and blend into the Location's ground would move by more than a step (either way) the ground under the
        // Location's own built pieces: the final check's verdict (RoadNetworkGenerator.UnderALocationsPieces), asked here so
        // the next way on (a staircase, the fallback landing) is tried.
        if (RoadNetworkGenerator.UnderALocationsPieces(plan, out _) is string pieces) { why = pieces; return null; }
        return plan;
    }

    /// <summary>
    /// The road's line onto the landing: straight in from in front of the location; from beside
    /// or behind it, round the location's rock on its lead-round ring (PlacedLocationShape.LeadRoundRing), the shorter way, on chords
    /// of <see cref="AroundStep"/> degrees or less kept outside that, until a straight line in
    /// stays in front of its mouth and, where its body is known (<see cref="PlacedLocationShape.BodyKnown"/>), keeps off it
    /// (<see cref="PlacedLocationShape.KeepsOffAllTheWay"/>, as <see cref="PlacedLocationShape.Behind"/> asks), and in from there. A road cannot search its own way round as a staircase does: a straight
    /// line from the side cut through the rock beside the mouth. A location whose way in comes from its entrance side only
    /// (<see cref="PlacedLocationShape.FrontLaneOnly"/>: within LandingReach of the landing, outside its door) is led round the same way from an end whose straight line
    /// in leaves that side (<see cref="PlacedLocationShape.OnTheWayInAllTheWay"/>, the road's <paramref name="walker"/> each
    /// side), until a straight line in keeps to it: from the side such a line ran in under the mouth's overhang.
    /// </summary>
    internal static List<Vector2> Route(Vector2 roadEnd, PlacedLocationShape shape, Vector2 target, PlacedLocationShape.Walker walker)
    {
        var line = new List<Vector2> { roadEnd };
        if (shape.Behind(roadEnd, target, walker) || !shape.OnTheWayInAllTheWay(roadEnd, target))
        {
            float ring = shape.LeadRoundRing(walker);
            Vector2 c = shape.Centre;
            float from = Mathf.Atan2(roadEnd.y - c.y, roadEnd.x - c.x) * Mathf.Rad2Deg;
            float facing = Mathf.Atan2(shape.Front.y, shape.Front.x) * Mathf.Rad2Deg;
            float delta = (facing - from) % 360f;
            if (delta > 180f) delta -= 360f;
            if (delta < -180f) delta += 360f;
            int steps = Mathf.Max(1, Mathf.CeilToInt(FloatBits.Abs(delta) / AroundStep));
            // Each chord's middle no nearer than the ring.
            ring /= Mathf.Cos(FloatBits.Abs(delta) / steps * 0.5f * Mathf.Deg2Rad);
            for (int i = 1; i <= steps; i++)
            {
                float a = (from + delta * i / steps) * Mathf.Deg2Rad;
                Vector2 p = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * ring;
                line.Add(p);
                // Round only until a straight line in stays in front of the mouth, on its way in and, where the body is known, off
                // it (KeepsOffAllTheWay, the test Behind asks of the line from the end): on to the front axis and back in, the last
                // turn was a hairpin; stopped where the line first came in front, it ran in past a crypt's stair wings beside its door.
                if (InFrontAllTheWay(shape, p, target) && shape.OnTheWayInAllTheWay(p, target)
                    && (!shape.BodyKnown || shape.KeepsOffAllTheWay(p, target, walker))) break;
            }
        }
        line.Add(target);
        return line;
    }

    private static bool InFrontAllTheWay(PlacedLocationShape shape, Vector2 from, Vector2 to)
    {
        for (int k = 0; k <= 8; k++)
            if (!shape.InFront(Vector2.Lerp(from, to, k / 8f))) return false;
        return true;
    }

    /// <summary>
    /// Where a road's way onto <paramref name="shape"/> from <paramref name="from"/> ends: the first of the target's own ends
    /// (<see cref="PlacedLocationShape.Ends"/>, the one owner, for the road's <paramref name="walker"/>: on the Location's levelled
    /// ground level with it, nearest its entrance's landing, or anywhere on it nearest the road; its landing as before where it levels
    /// none, its landing itself), else its landing. No fixed point, setback nor heading: the road reaches the Location level with its levelled ground
    /// wherever on it the way in arrives. Read by the door it is searched to (RoadNetworkGenerator.LeadInOnTheWayIn) and the tests;
    /// the way in itself tries every end (<see cref="Plan"/>).
    /// </summary>
    internal static Vector2 WayInEnd(PlacedLocationShape shape, Vector2 from, PlacedLocationShape.Walker walker)
    {
        if (shape.Door == null && !shape.LevelledArea) return shape.Landing;
        List<(Vector2 at, float height)> ends = shape.Ends(null, from, walker.Margin + EndSlack, 1, walker.BodyMargin);
        return ends.Count > 0 ? ends[0].at : shape.Landing;
    }

    /// <summary>Whether <paramref name="p"/> is within <paramref name="width"/> of the road's
    /// last stretch, but for its last <paramref name="width"/> before its end.</summary>
    private static bool NearTail(Vector2 p, List<Vector2> tail, Vector2 roadEnd, float width)
    {
        for (int i = 1; i < tail.Count; i++)
        {
            Vector2 a = tail[i - 1], b = tail[i];
            if (Vector2.Distance(a, roadEnd) <= width && Vector2.Distance(b, roadEnd) <= width) continue;
            Vector2 ab = b - a;
            float t = ab.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            Vector2 q = a + ab * t;
            if (Vector2.Distance(q, roadEnd) <= width) continue;
            if (Vector2.Distance(p, q) < width) return true;
        }
        return false;
    }

}
