using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// One owner of how a road's end meets the road it joins. Two ribbons meeting at an angle leave a point of grass between
/// them on the inside of the turn, and a road ending short of the other road's line leaves a notch. A road's end on a
/// road already laid is carried onto that road's centreline, and each inside corner the two roads make is filled with
/// road up to a curve of <see cref="Radius"/>: levelled and painted points on arcs tangent to both centrelines, so the
/// paint's inner edge runs round a curve instead of to a point. Nothing is laid past the other road's centreline (no
/// spur on the outside). A corner is left as it is where its fill would reach water, a place, a structure, a staircase's
/// flight or ground more than <see cref="MostEarth"/> off the road, and a road's end at a structure (a bridge's or a
/// staircase's) is never a junction here: those ends are held where they are.
/// </summary>
public static class RoadJunctionFillet
{
    /// <summary>
    /// The inside corner's radius at the paint's inner edge: the road's width, as an ordinary bend's least radius
    /// (RoadSwitchbacks.MinBendRadius), so a junction turns as tight as a road may. Settable for tests; 0 is off.
    /// </summary>
    internal static float Radius = 4f;

    /// <summary>How far along each road (in road widths) a corner's fill may run from the junction: an acute Y's fill is
    /// held to this, with a smaller radius, rather than paving a long wedge between the two roads.</summary>
    internal const float MostReach = 3f;

    /// <summary>Corners opener than this (degrees) are already round enough; narrower than <see cref="LeastAngle"/>, the
    /// two roads run together (a merge, not a junction).</summary>
    internal const float MostAngle = 150f;
    internal const float LeastAngle = 10f;

    /// <summary>The most ground (metres) a corner's fill may raise or lower: the fill is the junction's paint, not
    /// earthwork of its own.</summary>
    internal const float MostEarth = 2f;

    /// <summary>How far a road's end may stop short of the other road's centreline and still be carried onto it.</summary>
    private static float Reach(float width) => width;

    /// <summary>
    /// The fill for each end of <paramref name="own"/> that meets a road already laid: the stretch carrying the end onto
    /// that road's centreline and each inside corner's arcs, as pieces of their own (no length of road: they belong to the
    /// junction). Read against the network as it stands before <paramref name="own"/> is stored, with the road's own
    /// <paramref name="crossings"/> and new <paramref name="stairs"/> as structures: an end at one of them is held there, and
    /// no fill runs on or under a staircase's flight, the network's or the road's own.
    /// </summary>
    public static List<RoadSpatialGrid.PlannedPath> For(IReadOnlyList<RoadSpatialGrid.PlannedPath> own, WorldGenerator? world,
        IReadOnlyList<RoadCrossing>? crossings = null, IReadOnlyList<RoadStairSpan>? stairs = null)
    {
        var fills = new List<RoadSpatialGrid.PlannedPath>();
        if (!(Radius > 0f) || !RoadSpatialGrid.IsInitialized || own.Count == 0) return fills;
        var ends = new List<Vector2>();
        foreach (var piece in own)
            if (piece.Points.Count >= 2 && !piece.FollowTerrain) { ends.Add(piece.Points[0]); ends.Add(piece.Points[piece.Points.Count - 1]); }
        if (ends.Count == 0) return fills;
        float widest = 0f;
        foreach (var piece in own) widest = Mathf.Max(widest, piece.Width);
        var lines = RoadRide.Around(ends, (MostReach + 1f) * widest + Radius);
        // Structures' ends near the road's ends: the network's crossings and staircases and the road's own crossings.
        float near = (MostReach + 2f) * widest;
        var held = new List<Vector2>();
        void Hold(Vector2 q) { foreach (Vector2 e in ends) if ((q - e).sqrMagnitude < near * near) { held.Add(q); return; } }
        foreach (RoadCrossing c in RoadNetworkGenerator.CrossingsSnapshot()) { Hold(c.FromBank); Hold(c.ToBank); }
        if (crossings != null) foreach (RoadCrossing c in crossings) { Hold(c.FromBank); Hold(c.ToBank); }
        foreach (StairRun run in StairStructures.Runs) { Hold(run.From); Hold(run.To); }
        var ownRuns = new List<StairRun>();
        if (stairs != null)
            foreach (RoadStairSpan st in stairs)
            {
                Hold(st.FromPoint); Hold(st.ToPoint);
                ownRuns.Add(StairRun.FromPlan(st.FromPoint, st.ToPoint, RoadStairRuns.Width, st.Plan, st.Kit));
            }
        bool Clear(List<Vector2> points, List<float> heights) =>
            RoadJunctionFillet.Clear(points, heights, world) && (points.Count == 0 || !RoadStairRuns.OnAFlight(ownRuns, points, heights));
        foreach (var piece in own)
        {
            if (piece.Points.Count < 2 || piece.FollowTerrain) continue;
            foreach (bool last in new[] { false, true })
                if (Fill(piece, last, lines, held, Clear) is RoadSpatialGrid.PlannedPath fill) fills.Add(fill);
        }
        return fills;
    }

    /// <summary>The fill where one end of a piece meets the network, or null where it meets none.</summary>
    private static RoadSpatialGrid.PlannedPath? Fill(RoadSpatialGrid.PlannedPath piece, bool last, RoadRide.Lines lines, List<Vector2> held,
        Func<List<Vector2>, List<float>, bool> clear)
    {
        var pts = piece.Points;
        int n = pts.Count, endIndex = last ? n - 1 : 0, step = last ? -1 : 1;
        float width = piece.Widths != null && endIndex < piece.Widths.Count ? piece.Widths[endIndex] : piece.Width;
        // An end levelled but not painted is a structure's (the ground under a deck's or a staircase's end).
        if (piece.Unpainted != null && endIndex < piece.Unpainted.Count && piece.Unpainted[endIndex]) return null;
        Vector2 end = pts[endIndex];
        float endHeight = piece.Heights[endIndex];
        // The road's own line back from its end: where it heads into the junction, over its last few metres.
        var back = new List<(Vector2 p, float h, float along)> { (end, endHeight, 0f) };
        for (int i = endIndex + step; i >= 0 && i < n; i += step)
        {
            float run = back[back.Count - 1].along + Vector2.Distance(pts[i], pts[i - step]);
            back.Add((pts[i], piece.Heights[i], run));
            if (run > MostReach * width + Radius) break;
        }
        Vector2 behind = end;
        foreach (var b in back) { behind = b.p; if (b.along >= 3f) break; }
        if (Vector2.Distance(behind, end) < 1f) return null;
        Vector2 into = (end - behind).normalized;
        // The road it meets: a road's line at its height, never a structure's, nor at a structure's end (it holds the road
        // where it is).
        foreach (Vector2 h in held) if (Vector2.Distance(h, end) < width * 2f) return null;
        if (lines.Nearest(end, width * 2f, out _, roads: false)) return null;
        if (!lines.Nearest(end, Reach(width), out RoadRide.Hit other, height: endHeight, paintOnly: false, structures: false) || !other.HasHeading)
            return null;
        if (RoadSiteProtection.Contains(end)) return null;
        Vector2 along = other.Dir, across = new(-along.y, along.x);
        float meet = Vector2.Dot(into, across);
        if (Mathf.Abs(meet) < Mathf.Sin(LeastAngle * Mathf.Deg2Rad)) return null;
        // Onto the other road's centreline along its own heading; an end already past it stays where it is.
        float offset = Vector2.Dot(end - other.At, across), toLine = -offset / meet;
        if (toLine > Reach(width)) return null;
        Vector2 junction = end + into * Mathf.Max(0f, toLine);
        if (toLine < 0f) junction = end;

        var points = new List<Vector2>();
        var heights = new List<float>();
        float spacing = Mathf.Max(0.25f, width / 4f);
        float JunctionHeight(Vector2 q) => lines.StoredHeight(q, other.Height);
        if (toLine > spacing)
        {
            int count = Mathf.CeilToInt(toLine / spacing);
            for (int k = 1; k <= count; k++)
            {
                Vector2 q = end + into * (toLine * k / count);
                points.Add(q); heights.Add(Mathf.Lerp(endHeight, JunctionHeight(q), (float)k / count));
            }
        }
        if (!clear(points, heights)) { points.Clear(); heights.Clear(); }

        // Each inside corner: the road's line back (out) against the other road's either way (side).
        Vector2 outward = -into;
        float reachBack = back[back.Count - 1].along + Mathf.Max(0f, toLine);
        foreach (float sign in new[] { 1f, -1f })
        {
            Vector2 side = along * sign;
            float corner = Mathf.Abs(Turn(outward, side)) * Mathf.Rad2Deg;
            if (corner < LeastAngle || corner > MostAngle) continue;
            float half = corner * 0.5f * Mathf.Deg2Rad;
            float centreRadius = Radius + width * 0.5f;
            float tangent = centreRadius / (float)Math.Tan(half);
            // As far as both roads run from the junction, and no further than the fill may reach.
            float otherRuns = 0f;
            for (float d = 1f; d <= MostReach * width + 0.01f; d += 1f)
            {
                if (!lines.Nearest(junction + side * d, width * 0.5f, out _, side, endHeight, paintOnly: false, structures: false)) break;
                otherRuns = d;
            }
            tangent = Mathf.Min(tangent, Mathf.Min(MostReach * width, Mathf.Min(reachBack, otherRuns)));
            centreRadius = tangent * (float)Math.Tan(half);
            // A corner whose arc would not reach past the roads' own paint has nothing to round.
            if (centreRadius <= width * 0.5f + 0.25f) continue;
            Vector2 bisector = (outward + side).normalized;
            Vector2 centre = junction + bisector * (centreRadius / Mathf.Sin(half));
            var corners = new List<Vector2>();
            var cornerHeights = new List<float>();
            float apex = Vector2.Distance(centre, junction);
            Vector2 first = junction + outward * tangent - centre, second = junction + side * tangent - centre;
            float from = Mathf.Atan2(first.y, first.x), sweep = Turn(first, second);
            // Lanes a half road apart, from the arc out to the junction: the paint's solid core reaches between them.
            for (float r = centreRadius; r < apex - 0.25f; r += width * 0.5f)
            {
                int count = Mathf.Max(1, Mathf.CeilToInt(r * Mathf.Abs(sweep) / spacing));
                for (int k = 0; k <= count; k++)
                {
                    float a = from + sweep * k / count;
                    Vector2 q = centre + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
                    // Between the two centrelines only: nothing past the other road's line, nor past this road's.
                    if (Cross(side, q - junction) * Cross(side, outward) < -1e-3f) continue;
                    if (Cross(outward, q - junction) * Cross(outward, side) < -1e-3f) continue;
                    float onOther = Vector2.Dot(q - junction, side), onOwn = Vector2.Dot(q - junction, outward);
                    float hOther = JunctionHeight(junction + side * onOther), hOwn = OwnHeight(back, toLine, onOwn, endHeight);
                    float dOther = Mathf.Abs(Cross(side, q - junction)), dOwn = Mathf.Abs(Cross(outward, q - junction));
                    corners.Add(q);
                    cornerHeights.Add(dOther + dOwn < 1e-3f ? hOther : (hOther * dOwn + hOwn * dOther) / (dOther + dOwn));
                }
            }
            if (corners.Count == 0 || !clear(corners, cornerHeights)) continue;
            points.AddRange(corners);
            heights.AddRange(cornerHeights);
        }
        if (points.Count == 0) return null;
        var debug = new List<RoadPointDebugInfo>(points.Count);
        for (int i = 0; i < points.Count; i++) debug.Add(default);
        return new RoadSpatialGrid.PlannedPath(points, heights, debug, width, 0f, false);
    }

    /// <summary>The road's own height <paramref name="onOwn"/> metres out from the junction along its line.</summary>
    private static float OwnHeight(List<(Vector2 p, float h, float along)> back, float toLine, float onOwn, float endHeight)
    {
        float fromEnd = onOwn - Mathf.Max(0f, toLine);
        if (fromEnd <= 0f) return endHeight;
        for (int i = 1; i < back.Count; i++)
            if (back[i].along >= fromEnd)
            {
                float t = (fromEnd - back[i - 1].along) / Mathf.Max(1e-4f, back[i].along - back[i - 1].along);
                return Mathf.Lerp(back[i - 1].h, back[i].h, t);
            }
        return back[back.Count - 1].h;
    }

    /// <summary>Whether a fill may be laid: none of it in a place, in water or over ground more than
    /// <see cref="MostEarth"/> off its height.</summary>
    private static bool Clear(List<Vector2> points, List<float> heights, WorldGenerator? world)
    {
        for (int i = 0; i < points.Count; i++)
        {
            Vector2 q = points[i];
            if (RoadSiteProtection.Contains(q)) return false;
            if (world == null) continue;
            world.GetRiverWeight(q.x, q.y, out float river, out _);
            if (!RoadWater.BuiltDry(world.GetHeight(q.x, q.y), river)) return false;
            if (Mathf.Abs(BiomeBlendedHeight.GetBlendedHeight(q.x, q.y, world) - heights[i]) > MostEarth) return false;
        }
        return true;
    }

    /// <summary>The signed angle (radians) from one direction to another.</summary>
    private static float Turn(Vector2 a, Vector2 b) => Mathf.Atan2(Cross(a, b), Vector2.Dot(a, b));
    private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
}
