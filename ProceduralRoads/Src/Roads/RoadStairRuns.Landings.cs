using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The one owner of a road that meets a flight of stairs: it meets the foot straight on, else it lands on the row it
/// reaches (<see cref="Land"/>), else it is refused. Two flights ask it: a standing staircase's (<see cref="LandingOnto"/>,
/// with <see cref="OnALaidFlight"/>), its flight cut back from below to the row (StairLayout.TrimBelow), and a structure's
/// end stairs (StructureEnds.MeetEnds), laid only down to the row. The road's ground over the stairs taken away is
/// filled level with the row, a small landing the road and the flight meet on, under the same earthwork cap; another road
/// already on that ground is raised to it as well (<see cref="RaiseOthers"/>), so both meet level on the landing.
/// </summary>
public static partial class RoadStairRuns
{
    /// <summary>Landings laid onto a staircase's lower stairs this generation (<see cref="LayLanding"/>).</summary>
    public static int FlightLandings;

    /// <summary>A landing onto a standing staircase's lower stairs (<see cref="LandingOnto"/>), laid with its road
    /// (<see cref="LayLanding"/>).</summary>
    internal sealed class FlightLanding
    {
        /// <summary>The staircase as it stands.</summary>
        public StairRun Run = null!;
        /// <summary>The same staircase cut back to the row the road reaches.</summary>
        public StairRun Trimmed = null!;
        /// <summary>Its plan, cut back with it.</summary>
        public StairPlan Plan = null!;
        public StairKit Kit = StairKit.Stone;
        /// <summary>The base of the row the landing is level with: the trimmed staircase's foot.</summary>
        public float Height;
        /// <summary>Where the trimmed staircase's foot stands.</summary>
        public Vector2 Foot;
        /// <summary>Where the walked pieces taken away stood.</summary>
        public List<Vector2> Removed = new();
        /// <summary>The road's heights with the landing, point for point.</summary>
        public float[] Heights = Array.Empty<float>();
        /// <summary>Other roads' points on the landing's ground or eased to it, and the height each is raised (or lowered)
        /// to (<see cref="RaiseOthers"/>).</summary>
        public List<(RoadSpatialGrid.RoadPoint at, float to)> Others = new();
    }

    /// <summary>
    /// A landing for a road, its <paramref name="points"/> standing at <paramref name="heights"/>, that runs on or under a
    /// standing staircase's flight (<see cref="OnALaidFlight"/>), or null where none fits and the road is refused. Tried
    /// row by row up the flight from the one above its foot, short of its top row: the flight is cut back below the row
    /// (StairLayout.TrimBelow), the road's points within <see cref="OnRoad"/> of a piece taken away are levelled at the
    /// row's base and the road on either side eased to them at the road grade. The first row that fits is taken: the road
    /// then meets its foot level (a point within <see cref="JoiningStep"/>) and is on or under no flight; every height it
    /// changes is within <paramref name="cap"/> of <paramref name="ground"/>, dry, and outside every place; and another road
    /// on the ground the landing takes (a road that met the flight at its foot, which the landing would leave below it) is
    /// raised to it within the same cap (<see cref="RaiseOthers"/>). A road onto a staircase's upper stairs from above is
    /// not cut down to a row: it is refused.
    /// </summary>
    internal static FlightLanding? LandingOnto(IReadOnlyList<Vector2> points, IReadOnlyList<float> heights, Func<float, float, float> ground, float cap)
    {
        IReadOnlyList<StairRun> runs = StairStructures.Runs;
        if (points.Count == 0 || OnAFlight(runs, points, heights, out _) is not StairRun run) return null;
        StairKit kit = run.Kit ?? StairKit.Named(run.KitName) ?? StairKit.Stone;
        var steps = run.Pieces.Where(p => p.Kind == StructurePieceKind.StairStep).OrderBy(p => p.Position.y).ToList();
        if (steps.Count < 3) return null;
        float lowest = steps[0].Position.y, highest = steps[steps.Count - 1].Position.y;
        var low = new Vector2(steps[0].Position.x, steps[0].Position.z);
        bool fromFoot = Vector2.Distance(run.From, low) <= Vector2.Distance(run.To, low);
        float lastRow = float.NegativeInfinity;
        foreach (StructurePiece row in steps)
        {
            float height = row.Position.y;
            if (height < lowest + 0.01f || height > highest - 0.01f || height < lastRow + 0.01f) continue;
            lastRow = height;
            StairPlan plan = PlanOf(run, kit);
            if (StairLayout.TrimBelow(plan, height, kit) == 0) continue;
            var kept = new HashSet<Vector3>(plan.Pieces.Select(p => p.Position));
            var removed = run.Pieces.Where(p => p.Kind != StructurePieceKind.StairSupport && !kept.Contains(p.Position))
                .Select(p => new Vector2(p.Position.x, p.Position.z)).ToList();
            // The row's foot: half a piece back from its middle, off the next stair up.
            int at = run.Pieces.IndexOf(row);
            StructurePiece? next = run.Pieces.Skip(at + 1).FirstOrDefault(p => p.Kind == StructurePieceKind.StairStep);
            if (next == null) continue;
            var middle = new Vector2(row.Position.x, row.Position.z);
            Vector2 up = (new Vector2(next.Position.x, next.Position.z) - middle).normalized;
            Vector2 foot = middle - up * (StairLayout.PieceRun / 2f);
            if (Land(points, heights, removed, foot, height, ground, cap, out var others) is not float[] raised) continue;
            StairRun trimmed = StairRun.FromPlan(fromFoot ? foot : run.From, fromFoot ? run.To : foot, run.Width, plan, run.Kit);
            trimmed.KeepsSiteRocks = run.KeepsSiteRocks;
            var after = runs.Select(r => ReferenceEquals(r, run) ? trimmed : r).ToList();
            if (OnAFlight(after, points, raised, out _) != null) continue;
            return new FlightLanding { Run = run, Trimmed = trimmed, Plan = plan, Kit = kit, Height = height, Foot = foot, Removed = removed, Heights = raised, Others = others! };
        }
        return null;
    }

    /// <summary>
    /// The landing step, for every flight a road meets (<see cref="LandingOnto"/>, StructureEnds.MeetEnds): the road
    /// <paramref name="points"/>, standing at <paramref name="heights"/>, with its points within <see cref="OnRoad"/> of
    /// the ground <paramref name="removed"/> (where the stairs taken away stood) levelled at <paramref name="height"/>, the
    /// row's base, and the road either side eased to them at the road grade; null where it does not fit. It fits where the
    /// road comes to the row's <paramref name="foot"/> (a levelled point within <see cref="JoiningStep"/>), every height it
    /// changes is within <paramref name="cap"/> of <paramref name="ground"/>, dry and outside every place, and every other
    /// road on the landing's ground can be raised to it (<paramref name="others"/>, <see cref="RaiseOthers"/>).
    /// </summary>
    internal static float[]? Land(IReadOnlyList<Vector2> points, IReadOnlyList<float> heights, IReadOnlyList<Vector2> removed, Vector2 foot,
        float height, Func<float, float, float> ground, float cap, out List<(RoadSpatialGrid.RoadPoint at, float to)>? others, bool overWater = false)
    {
        others = null;
        if (Fill(points, heights, removed, foot, height, ground, cap, overWater) is not float[] raised) return null;
        others = RaiseOthers(removed, height, ground, cap, overWater);
        return others == null ? null : raised;
    }

    /// <summary>The road's heights with a landing at <paramref name="height"/> over <paramref name="removed"/>, or null
    /// where it does not fit (<see cref="Land"/>).</summary>
    private static float[]? Fill(IReadOnlyList<Vector2> points, IReadOnlyList<float> heights, IReadOnlyList<Vector2> removed, Vector2 foot,
        float height, Func<float, float, float> ground, float cap, bool overWater)
    {
        var along = new float[points.Count];
        for (int i = 1; i < points.Count; i++) along[i] = along[i - 1] + Vector2.Distance(points[i - 1], points[i]);
        var raised = heights.ToArray();
        var landing = new List<int>();
        for (int i = 0; i < points.Count; i++)
            if (removed.Any(r => Vector2.Distance(r, points[i]) < OnRoad)) landing.Add(i);
        if (landing.Count == 0 || !landing.Exists(i => Vector2.Distance(points[i], foot) <= JoiningStep)) return null;
        float grade = RoadGrade.Configured;
        for (int i = 0; i < points.Count; i++)
        {
            float d = float.PositiveInfinity;
            foreach (int k in landing) d = Mathf.Min(d, Mathf.Abs(along[i] - along[k]));
            raised[i] = Mathf.Clamp(heights[i], height - grade * d, height + grade * d);
        }
        Vector2? last = null;
        for (int i = 0; i < points.Count; i++)
        {
            if (Mathf.Abs(raised[i] - heights[i]) < 0.001f) { last = null; continue; }
            Vector2 p = points[i];
            float g = ground(p.x, p.y);
            if (!Earthwork(raised[i], g, cap, overWater) || RoadSiteProtection.BlocksSegment(last ?? p, p, 1f, null, null)) return null;
            last = p;
        }
        return raised;
    }

    /// <summary>
    /// Whether a landing may take ground at <paramref name="ground"/> to <paramref name="height"/>: within
    /// <paramref name="cap"/> of it, on dry ground. Over water (<paramref name="overWater"/>, the earthwork meet of a
    /// structure's end, StructureEnds.MeetEnds) the ground under the floor every road on land is built up to
    /// (RoadConstants.DryRoadFloor) is filled to it as an ordinary road's is, the cap judged from there.
    /// </summary>
    private static bool Earthwork(float height, float ground, float cap, bool overWater) =>
        RoadWater.BuiltDry(ground) ? Mathf.Abs(height - ground) <= cap
        : overWater && Mathf.Max(height, RoadConstants.DryRoadFloor) - ground <= cap;

    /// <summary>How far apart two points of one stored road may stand and be walked from one to the other
    /// (<see cref="RaiseOthers"/>): the network's roads are stored a few metres apart at most, and two roads this close
    /// side by side are refused (RoadNetworkGenerator.MeetingRefusal).</summary>
    private const float AlongOneRoad = 4f;

    /// <summary>
    /// Every other road on the ground a landing takes (its points within <see cref="OnRoad"/> of <paramref name="removed"/>)
    /// raised to the landing's <paramref name="height"/>, and on along that road eased to it at the road grade, so both roads
    /// meet level on the landing: a road that came to the flight's old foot, which the landing would otherwise leave below
    /// it or bury. Walked along the stored road from the landing (each point within <see cref="AlongOneRoad"/> of the
    /// last); only points whose height changes are returned, with the height each goes to. Null where it does not fit: a
    /// height it changes past <paramref name="cap"/> of <paramref name="ground"/>, in water or in a place, or the road still
    /// raised where the ease runs out of reach (twice the cap at the road grade). Empty where no other road is there.
    /// </summary>
    private static List<(RoadSpatialGrid.RoadPoint at, float to)>? RaiseOthers(IReadOnlyList<Vector2> removed, float height, Func<float, float, float> ground, float cap, bool overWater)
    {
        var changed = new List<(RoadSpatialGrid.RoadPoint at, float to)>();
        if (removed.Count == 0) return changed;
        float grade = Mathf.Max(RoadGrade.Configured, 0.01f);
        float ease = 2f * Mathf.Min(cap, RoadConstants.TerrainDeltaMax) / grade;
        Vector2 centre = default;
        foreach (Vector2 r in removed) centre += r;
        centre *= 1f / removed.Count;
        float spread = removed.Max(r => Vector2.Distance(r, centre));
        // Each stored point once (a point is filed in every cell its width reaches).
        var seen = new HashSet<(Vector2, float)>();
        var near = new List<RoadSpatialGrid.RoadPoint>();
        foreach (RoadSpatialGrid.RoadPoint rp in RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(centre.x, 0f, centre.y), spread + OnRoad + ease))
            if (rp.IsRoad && seen.Add((rp.p, rp.h))) near.Add(rp);
        if (near.Count == 0) return changed;
        // How far along its road each point stands from the landing's ground: 0 on it.
        var from = new float[near.Count];
        var done = new bool[near.Count];
        for (int i = 0; i < near.Count; i++) from[i] = removed.Any(r => Vector2.Distance(r, near[i].p) < OnRoad) ? 0f : float.PositiveInfinity;
        while (true)
        {
            int next = -1;
            for (int i = 0; i < near.Count; i++)
                if (!done[i] && !float.IsInfinity(from[i]) && (next < 0 || from[i] < from[next])) next = i;
            if (next < 0) break;
            done[next] = true;
            for (int i = 0; i < near.Count; i++)
            {
                float step = Vector2.Distance(near[next].p, near[i].p);
                if (!done[i] && step <= AlongOneRoad) from[i] = Mathf.Min(from[i], from[next] + step);
            }
        }
        for (int i = 0; i < near.Count; i++)
        {
            if (float.IsInfinity(from[i])) continue;
            RoadSpatialGrid.RoadPoint rp = near[i];
            float to = Mathf.Clamp(rp.h, height - grade * from[i], height + grade * from[i]);
            if (Mathf.Abs(to - rp.h) < 0.001f) continue;
            float g = ground(rp.p.x, rp.p.y);
            if (Vector2.Distance(rp.p, centre) > spread + OnRoad + ease - AlongOneRoad || !Earthwork(to, g, cap, overWater)
                || RoadSiteProtection.BlocksSegment(rp.p, rp.p, 1f, null, null)) return null;
            changed.Add((rp, to));
        }
        return changed;
    }

    /// <summary>A copy of the plan a standing run was laid from, where it was laid this generation as it stands (its turns
    /// counted on its pieces); else a plan of its pieces and clearances as they stand.</summary>
    private static StairPlan PlanOf(StairRun run, StairKit kit)
    {
        if (s_laid.TryGetValue(run, out var laid) && laid.plan.Pieces.Count == run.Pieces.Count) return laid.plan.Copy();
        var plan = new StairPlan { Kit = kit.Name, Reached = true };
        foreach (StructurePiece p in run.Pieces)
        {
            var kind = p.Kind switch
            {
                StructurePieceKind.StairStep => StairPieceKind.Stair,
                StructurePieceKind.StairFloor => StairPieceKind.Landing,
                StructurePieceKind.StairFiller => StairPieceKind.Filler,
                _ => StairPieceKind.Support,
            };
            plan.Pieces.Add(new StairPiece { Kind = kind, Prefab = p.Prefab, Position = p.Position, YawDegrees = p.YawDegrees, Cantilevered = p.Cantilevered });
            if (kind == StairPieceKind.Stair) plan.Stairs++;
            else if (kind == StairPieceKind.Landing) plan.Landings++;
        }
        plan.Clearances.AddRange(run.Clearances);
        return plan;
    }

    /// <summary>
    /// A landing laid with its road (<see cref="LandingOnto"/>; the road's heights are the caller's): the staircase is
    /// replaced in the network by itself cut back, its laid plan with it, the other roads on its ground raised to it
    /// (<see cref="FlightLanding.Others"/>), and its cuts under the pieces taken away and
    /// below the landing are taken back, which would dig through the landing's fill. False where the staircase no longer
    /// stands as the landing found it.
    /// </summary>
    internal static bool LayLanding(FlightLanding landing)
    {
        if (!StairStructures.Swap(landing.Run, landing.Trimmed)) return false;
        RoadSpatialGrid.SetRoadHeights(landing.Others);
        if (s_laid.TryRemove(landing.Run, out _)) s_laid[landing.Trimmed] = (landing.Plan, landing.Kit);
        float below = landing.Height - StairLayout.CutBelowSurface - 0.01f;
        RoadSpatialGrid.TakeBackCuts(landing.Removed, StairLayout.PieceRun, rp => rp.h < below);
        System.Threading.Interlocked.Increment(ref FlightLandings);
        return true;
    }
}
