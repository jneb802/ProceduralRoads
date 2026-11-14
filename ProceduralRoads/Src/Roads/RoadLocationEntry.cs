using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The road itself carried on from its end at a placed location onto the landing in
/// front of it, where a road can get there: stairs only where it cannot, or where the
/// road already arrives on a staircase. The road stops outside a location's
/// whole footprint; this last stretch is let into the footprint along itself only
/// (<see cref="RoadSpatialGrid.RoadPoint.intoLocation"/>): its earthwork and paint there
/// reach <see cref="InsideReach"/> from it and no further, and it keeps that far out of
/// the entrance pit and the location's rock and boulders. Every other rule of a road
/// holds: the grade cap, the earthwork cap, no water. Pure: any thread.
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
    public static RoadSpatialGrid.PlannedPath? Plan(LocationApproachRequest request, PlacedLocationShape shape, float width,
        Func<float, float, float> ground, WorldGenerator world, out string why,
        Func<Vector2, float, IEnumerable<(Vector2 at, float height)>>? roadsNear = null)
    {
        why = "";
        float reach = InsideReach(width);
        // Up to the level ground beside the mouth, at its height (the road or stairs come up level), its earthwork reach clear of the entrance,
        // the nearest landing the road can get to; failing any, the point in front of the pit.
        // Nearest the road's end first: ranked by the front axis alone, a road from the west tried
        // only the landings east of a frost cave's mouth, past a rock and 10 m of earthwork, and
        // never the ledge on its own side.
        var landings = shape.Landings(ground, reach + EndSlack, RoadLandingTries, width * 0.5f + PlacedLocationShape.BodyGap);
        Vector2 from = request.RoadEnd;
        landings.Sort((a, b) => Vector2.Distance(a.at, from).CompareTo(Vector2.Distance(b.at, from)));
        if (landings.Count == 0)
        {
            Vector2 aim = shape.LandingFor(reach + EndSlack);
            landings.Add((aim, ground(aim.x, aim.y)));
        }
        var tried = new List<string>();
        foreach (var (target, height) in landings)
        {
            RoadSpatialGrid.PlannedPath? plan = PlanTo(request, shape, width, reach, target, height, ground, world, out string one, roadsNear);
            if (plan != null) return plan;
            tried.Add(FormattableString.Invariant($"to ({target.x:F0}, {height:F1}, {target.y:F0}) {one}"));
        }
        why = "road: " + string.Join("; ", tried);
        return null;
    }

    private static RoadSpatialGrid.PlannedPath? PlanTo(LocationApproachRequest request, PlacedLocationShape shape, float width, float reach,
        Vector2 target, float landing, Func<float, float, float> ground, WorldGenerator world, out string why,
        Func<Vector2, float, IEnumerable<(Vector2 at, float height)>>? roadsNear)
    {
        why = "";
        Vector2 roadEnd = request.RoadEnd;
        if (Vector2.Distance(roadEnd, target) > request.Reach) { why = "too far"; return null; }
        List<Vector2> line = Route(roadEnd, shape, target, reach);
        RoadSpatialGrid.PlannedPath? plan;
        // A trial: it does not count toward the network's grade report.
        // Into this location alone (its identity, the shape's centre): another location's footprint, nested in it or over it, is not.
        var ownLocation = new RoadLocationProtection.Targets(null, shape.Centre);
        plan = RoadGrade.Trial(() => RoadSpatialGrid.PlanLocationEntry(line, width, world, request.RoadHeight, landing, p => ground(p.x, p.y), ownLocation));
        if (plan == null) { why = RoadSpatialGrid.LastRefusal ?? "cannot be built"; return null; }
        for (int i = 0; i < plan.Points.Count; i++)
        {
            Vector2 p = plan.Points[i];
            string into = shape.Refusal(p, reach, width * 0.5f + HugGap, width * 0.5f + PlacedLocationShape.BodyGap);
            if (into != "") { why = FormattableString.Invariant($"{into} at ({p.x:F1}, {p.y:F1})"); return null; }
            if (Vector2.Distance(p, roadEnd) > width && NearTail(p, request.RoadTail, roadEnd, width))
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
                    if (Mathf.Abs(n.height - plan.Heights[i]) > OtherRoadRise + OtherRoadBank * gap)
                    { why = FormattableString.Invariant($"beside another road at ({n.at.x:F1}, {n.at.y:F1}), {n.height - plan.Heights[i]:F1} m off its height {gap:F1} m away"); return null; }
                }
            }
            if (RoadLocationProtection.BlocksSegment(p, p, 0f, ownLocation)) { why = "into another location"; return null; }
            world.GetRiverWeight(p.x, p.y, out float river, out _);
            if (!RoadWater.BuiltDry(ground(p.x, p.y), river)) { why = "water"; return null; }
        }
        // Its earthwork as every road's is measured, across its own width, over the ground the location leaves
        // (RoadNetworkGenerator.EarthworkAcross); its fill judged by this way in's own limit, then the cap.
        RoadNetworkGenerator.Earthwork earth = RoadNetworkGenerator.EarthworkAcross(plan, q => ground(q.x, q.y), RoadNetworkGenerator.EarthworkLimit);
        if (earth.Fill > MaxFill)
        { why = FormattableString.Invariant($"a ridge: fill {earth.Fill:F1} m over the ground, past {MaxFill:F0} m"); return null; }
        if (earth.Past > 0)
        { why = FormattableString.Invariant($"earthwork {earth.Worst:F1} m past the {RoadNetworkGenerator.EarthworkCap:F0} m cap"); return null; }
        Worst = earth.Worst;
        return plan;
    }

    /// <summary>
    /// The road's line onto the landing: straight in from in front of the location; from beside
    /// or behind it, round the location's rock its reach clear of it, the shorter way, on chords
    /// of <see cref="AroundStep"/> degrees or less kept outside that, until a straight line in
    /// stays in front of its mouth, and in from there. A road cannot search its own way round as a staircase does: a straight
    /// line from the side cut through the rock beside the mouth.
    /// </summary>
    internal static List<Vector2> Route(Vector2 roadEnd, PlacedLocationShape shape, Vector2 target, float reach)
    {
        var line = new List<Vector2> { roadEnd };
        if (shape.Behind(roadEnd))
        {
            float ring = shape.KeepOut + reach + RingSlack;
            Vector2 c = shape.Centre;
            float from = Mathf.Atan2(roadEnd.y - c.y, roadEnd.x - c.x) * Mathf.Rad2Deg;
            float facing = Mathf.Atan2(shape.Front.y, shape.Front.x) * Mathf.Rad2Deg;
            float delta = (facing - from) % 360f;
            if (delta > 180f) delta -= 360f;
            if (delta < -180f) delta += 360f;
            int steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(delta) / AroundStep));
            // Each chord's middle no nearer than the ring.
            ring /= Mathf.Cos(Mathf.Abs(delta) / steps * 0.5f * Mathf.Deg2Rad);
            for (int i = 1; i <= steps; i++)
            {
                float a = (from + delta * i / steps) * Mathf.Deg2Rad;
                Vector2 p = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * ring;
                line.Add(p);
                // Round only until a straight line in stays in front of the mouth: on to the
                // front axis and back in, the last turn was a hairpin.
                if (InFrontAllTheWay(shape, p, target)) break;
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

    /// <summary>The deepest earthwork of the last road <see cref="Plan"/> returned on this thread, for the log.</summary>
    [ThreadStatic] public static float Worst;

    /// <summary>Most a road onto a location's landing may stand on fill over the ground beside it. Up to
    /// the 8 m earthwork cap, one wrapped round a bear cave's mouth on an embankment with sheer sides
    /// (at -4924,-5577, a disallowed ridge); a staircase, or nothing,
    /// serves that side instead.</summary>
    public const float MaxFill = 3f;
}
