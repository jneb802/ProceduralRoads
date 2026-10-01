using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// A bounded local alternative to carving straight into the side of a site.
/// Tries arrival directions around the site over the last 64 metres.
/// Scores the actual smoothed, grade-limited profile across the road's width;
/// keeps the old route unless a candidate materially reduces earthworks.
/// This is not entrance detection: candidates remain outside the protected footprint and never introduce a
/// water crossing. Contour arcs can reach a lower side of a hillside site.
/// </summary>
public static class RoadSiteApproach
{
    public const float Reach = 64f;

    public static List<Vector2> Improve(List<Vector2> path, Vector2 centre, float radius,
        float width, WorldGenerator world, bool atStart,
        Func<Vector2, float?> target, Func<Vector2, float> ground, float? desiredHeight = null)
    {
        if (radius <= 0f || radius > Reach || path.Count < 3) return path;
        var oriented = new List<Vector2>(path);
        if (atStart) oriented.Reverse();
        int last = oriented.Count - 1, join = last;
        float oldLength = 0;
        while (join > 0 && oldLength < Reach)
        {
            oldLength += Vector2.Distance(oriented[join], oriented[join - 1]);
            join--;
        }
        // Leave enough of the old route to preserve its other approach.
        if (join < 2 || oldLength > Reach * 1.5f) return path;
        Vector2 oldEnd = oriented[last], anchor = oriented[join];
        float arrivalRadius = Vector2.Distance(oldEnd, centre);
        if (arrivalRadius < radius - 0.1f || arrivalRadius > radius + 16f) return path;
        var oldTail = oriented.GetRange(join, oriented.Count - join);
        // Keep authored ground separate from procedural profile heights.
        // Both caches are bounded and die with this decision, including errors.
        var groundSamples = new ApproachTerrainSamples(ground);
        var profileSamples = new ApproachTerrainSamples(
            p => BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world));
        float Ground(Vector2 p) => groundSamples.Get(p);
        float anchorHeight = Ground(anchor);
        string lastRejection = "";
        float Reject(string reason) { lastRejection = reason; return float.PositiveInfinity; }
        float Score(List<Vector2> tail, out float worst, bool requireClear = true)
        {
            worst = 0f; lastRejection = "";
            // Trial profiles do not contribute to the network's grade report.
            float previousGrade = RoadGrade.SteepestPlanned;
            RoadSpatialGrid.PlannedPath? plan;
            try
            {
                plan = requireClear
                    ? RoadSpatialGrid.PlanRoadPath(tail, width, world, anchorHeight, target(tail[tail.Count - 1]), terrainHeight: profileSamples.Get)
                    : RoadSpatialGrid.PlanComparisonProfile(tail, width, world, anchorHeight, target(tail[tail.Count - 1]), profileSamples.Get);
            }
            finally { RoadGrade.SteepestPlanned = previousGrade; }
            if (plan == null) return Reject("profile unavailable");
            double sum = 0;
            for (int i = 0; i < plan.Points.Count; i++)
            {
                Vector2 point = plan.Points[i];
                if (float.IsNaN(plan.Heights[i]) || float.IsInfinity(plan.Heights[i])) return Reject("non-finite profile");
                // Inspect the spline, not just the control points. Its turn
                // must not cut through the location or sneak across water.
                if (requireClear && Vector2.Distance(point, centre) < radius - 0.1f) return Reject("spline enters footprint");
                Vector2 previous = plan.Points[Math.Max(0, i - 1)];
                if (requireClear && RoadSiteProtection.BlocksSegment(previous, point, width * 0.5f + 2f, null, null))
                    return Reject("spline enters protected clearance");
                world.GetRiverWeight(point.x, point.y, out float river, out _);
                if (river > RoadConstants.RiverImpassableThreshold || Ground(point) < RoadConstants.ShallowWaterHeight)
                    return Reject("water or river");
                Vector2 direction = plan.Points[Math.Min(i + 1, plan.Points.Count - 1)]
                    - plan.Points[Math.Max(i - 1, 0)];
                Vector2 side = new Vector2(-direction.y, direction.x).normalized * (width * 0.5f);
                for (int j = -1; j <= 1; j++)
                {
                    float h = Ground(point + side * j);
                    if (float.IsNaN(h) || float.IsInfinity(h)) return Reject("non-finite ground");
                    float cut = Mathf.Abs(plan.Heights[i] - h);
                    worst = Mathf.Max(worst, cut);
                    sum += cut;
                }
            }
            float mismatch = desiredHeight.HasValue ? Mathf.Abs(Ground(tail[tail.Count - 1]) - desiredHeight.Value) : 0f;
            return (float)(sum / (plan.Points.Count * 3)) + worst * 0.5f + plan.TotalLength * 0.015f + mismatch * 2f;
        }
        // The old spline can already graze the protected edge. That must not
        // prevent searching for a valid replacement; clearance gates candidates.
        float oldScore = Score(oldTail, out float oldWorst, false);
        float oldMismatch = desiredHeight.HasValue ? Mathf.Abs(Ground(oldEnd) - desiredHeight.Value) : 0f;
        if (float.IsInfinity(oldScore) || (oldWorst < 2f && oldMismatch <= 1.5f)) return path;
        float bestScore = oldScore;
        List<Vector2>? best = null;
        Vector2 radial = oldEnd - centre;
        for (int turn = -8; turn <= 8; turn++)
        {
            float angle = turn * Mathf.PI / 8f;
            Vector2 end = centre + new Vector2(radial.x * Mathf.Cos(angle) - radial.y * Mathf.Sin(angle),
                radial.x * Mathf.Sin(angle) + radial.y * Mathf.Cos(angle));
            Vector2 line = end - anchor;
            Vector2 side = new Vector2(-line.y, line.x).normalized;
            for (int bend = -1; bend <= 2; bend++)
            {
                Vector2 middle = (anchor + end) * 0.5f + side * (bend * 16f);
                var candidate = new List<Vector2>();
                float length = 0;
                // Quadratic arc avoids an abrupt dogleg at a single corner.
                for (int step = 0; step <= 8; step++)
                {
                    float t = step / 8f;
                    Vector2 point;
                    if (bend == 2)
                    {
                        // Follow the site's outside instead of a chord through
                        // it when its accessible elevation is on the far side.
                        Vector2 from = anchor - centre, to = end - centre;
                        float a = (float)Math.Atan2(from.y, from.x);
                        float delta = (float)Math.Atan2(from.x * to.y - from.y * to.x,
                            from.x * to.x + from.y * to.y);
                        float r = to.magnitude + 2f;
                        float tangentAngle = from.magnitude > r
                            ? (float)Math.Acos(r / from.magnitude) : 0f;
                        float sign = delta < 0f ? -1f : 1f;
                        float straight = (float)Math.Sqrt(Math.Max(0f, from.sqrMagnitude - r*r));
                        float arc = Mathf.Max(0f, Mathf.Abs(delta) - tangentAngle) * r;
                        Vector2 finish = centre + to.normalized * r;
                        if (arc <= 0f) point = anchor * (1f-t) + finish*t;
                        else
                        {
                            float ta = a + sign*tangentAngle;
                            Vector2 tangent = centre + new Vector2(Mathf.Cos(ta),Mathf.Sin(ta))*r;
                            float distance = t*(straight+arc);
                            if (distance < straight) point = anchor + (tangent-anchor)*(distance/straight);
                            else
                            {
                                float theta = ta + sign*(distance-straight)/r;
                                point = centre + new Vector2(Mathf.Cos(theta),Mathf.Sin(theta))*r;
                            }
                        }
                    }
                    else point = anchor * ((1 - t) * (1 - t)) + middle * (2 * t * (1 - t)) + end * (t * t);
                    if (candidate.Count > 0) length += Vector2.Distance(candidate[candidate.Count - 1], point);
                    candidate.Add(point);
                }
                if (length > oldLength + Mathf.PI * radius) continue;
                // This was already an acceptance rule. Apply it before
                // smoothing, grading and sampling an unusable trial profile.
                if (desiredHeight.HasValue && Mathf.Abs(Ground(candidate[candidate.Count - 1]) - desiredHeight.Value) > 1.5f) continue;
                float score = Score(candidate, out float worst);
                float limit = oldMismatch > 1.5f ? Mathf.Max(2f, oldWorst + oldMismatch * 0.5f) : oldWorst - 0.5f;
                if (worst > limit || score >= bestScore || score > oldScore * 0.8f) continue;
                bestScore = score;
                best = candidate;
            }
        }
        // Geometric arcs can still cut across a ridge. When a site's height
        // is missed, search the local ground itself for a walkable contour.
        // One bounded search serves all possible arrival directions; it never
        // changes the main network search budget or exempts the destination.
        for (int margin = 1; best == null && desiredHeight.HasValue && oldMismatch > 1.5f && margin <= 9; margin += 4)
        {
            const float step = 4f;
            var directions = new[] { new Vector2i(1,0), new Vector2i(-1,0), new Vector2i(0,1), new Vector2i(0,-1),
                new Vector2i(1,1),new Vector2i(1,-1),new Vector2i(-1,1),new Vector2i(-1,-1),
                new Vector2i(2,1),new Vector2i(2,-1),new Vector2i(-2,1),new Vector2i(-2,-1),
                new Vector2i(1,2),new Vector2i(1,-2),new Vector2i(-1,2),new Vector2i(-1,-2) };
            var frontier = new SortedSet<(float cost,int x,int z)>();
            var costs = new Dictionary<Vector2i,float>();
            var parents = new Dictionary<Vector2i,Vector2i>();
            var origin = new Vector2i(0,0);
            costs[origin]=0f; frontier.Add((0f,0,0));
            float maxRadius = Mathf.Min(192f, Vector2.Distance(anchor,centre)+Reach);
            float maxGrade = RoadGrade.Configured > 0f ? RoadGrade.Configured : 0.35f;
            Vector2 Point(Vector2i node) => anchor + new Vector2(node.x*step,node.y*step);
            int visited=0, goals=0;
            while (frontier.Count > 0 && visited++ < 4096 && goals < 8)
            {
                var current=frontier.Min; frontier.Remove(current);
                var key=new Vector2i(current.x,current.z);
                if (current.cost > costs[key]) continue;
                Vector2 here=Point(key); float height=Ground(here);
                float distanceToSite=Vector2.Distance(here,centre);
                if (parents.ContainsKey(key) && distanceToSite >= radius+margin && distanceToSite <= radius+margin+9f &&
                    Mathf.Abs(height-desiredHeight.Value) <= 1.5f)
                {
                    goals++;
                    var candidate=new List<Vector2> { here };
                    var cursor=key;
                    while (parents.TryGetValue(cursor,out var parent)) { candidate.Add(Point(parent)); cursor=parent; }
                    candidate.Reverse();
                    float score=Score(candidate,out float worst);
                    ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(
                        $"Site arrival {centre}: {here} ground {height:F2}, score {score:F2}/{oldScore:F2}, earthwork {worst:F2}/{oldWorst:F2}, {lastRejection}");
                    if (worst <= Mathf.Max(2f,oldWorst+oldMismatch*0.5f) && score < bestScore && score <= oldScore*0.8f)
                    { best=candidate; bestScore=score; }
                }
                foreach (var direction in directions)
                {
                    var next=new Vector2i(key.x+direction.x,key.y+direction.y);
                    Vector2 there=Point(next), delta=there-here;
                    float nextRadius=Vector2.Distance(there,centre);
                    if (nextRadius < radius+margin || nextRadius > maxRadius) continue;
                    float t=Mathf.Clamp01(((centre.x-here.x)*delta.x+(centre.y-here.y)*delta.y)/delta.sqrMagnitude);
                    if (Vector2.Distance(here+delta*t,centre) < radius+margin ||
                        RoadSiteProtection.BlocksSegment(here,there,width*0.5f+2f+margin,null,null)) continue;
                    float nextHeight=Ground(there), middleHeight=Ground((here+there)*0.5f);
                    if (float.IsNaN(nextHeight) || float.IsInfinity(nextHeight) ||
                        float.IsNaN(middleHeight) || float.IsInfinity(middleHeight) || nextHeight < RoadConstants.ShallowWaterHeight) continue;
                    float slope=Mathf.Max(Mathf.Abs(nextHeight-middleHeight),Mathf.Abs(middleHeight-height))*2f/delta.magnitude;
                    if (slope > maxGrade) continue;
                    world.GetRiverWeight(there.x,there.y,out float river,out _);
                    if (river > RoadConstants.RiverImpassableThreshold) continue;
                    float cost=current.cost+delta.magnitude*(1f+slope*4f);
                    if (costs.TryGetValue(next,out float previous) && previous <= cost) continue;
                    costs[next]=cost; parents[next]=key; frontier.Add((cost,next.x,next.y));
                }
            }
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(
                $"Site contour {centre}: margin {margin}m, {visited} nodes, {goals} arrivals near platform, accepted={best != null}");
        }
        if (best == null) return path;
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(
            $"Site approach {centre}: end {oldEnd} -> {best[best.Count - 1]}, earthwork score {oldScore:F2} -> {bestScore:F2}");
        var result = oriented.GetRange(0, join);
        result.AddRange(best);
        if (atStart) result.Reverse();
        return result;
    }
}
