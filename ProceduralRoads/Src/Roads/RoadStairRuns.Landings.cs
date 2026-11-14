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
/// filled level with the row, a small landing the road and the flight meet on, under the same earthwork cap; the
/// staircase's own road at the foot taken away is raised to it as well (<see cref="RaiseOwn"/>), so both meet level on the
/// landing; no other road is raised: one at another height on the landing's ground refuses it. A structure's end stairs,
/// which record no road of their own, raise the roads on their landing's ground (<see cref="RaiseOnTheGround"/>).
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
        /// <summary>The foot that moved is the run's From end (else its To end): the road that meets it there is the landing's
        /// (<see cref="MeetLanding"/>).</summary>
        public bool FootAtFrom;
        /// <summary>Where the walked pieces taken away stood.</summary>
        public List<Vector2> Removed = new();
        /// <summary>The road's heights with the landing, point for point.</summary>
        public float[] Heights = Array.Empty<float>();
        /// <summary>The staircase's own road's points on the landing's ground or eased to it, and the levelled ground under
        /// it there, with the height each is raised (or lowered) to (<see cref="RaiseOwn"/>).</summary>
        public List<(RoadSpatialGrid.RoadPoint at, float to)> Others = new();
    }

    /// <summary>
    /// A landing for a road, its <paramref name="points"/> standing at <paramref name="heights"/>, that runs on or under a
    /// standing staircase's flight (<see cref="OnALaidFlight"/>), or null where none fits and the road is refused. Tried
    /// row by row up the flight from the one above its foot, short of its top row: the flight is cut back below the row
    /// (StairLayout.TrimBelow), the road's points within <see cref="OnRoad"/> of a piece taken away are levelled at the
    /// row's base and the road on either side eased to them at the road grade. The first row that fits is taken: the road
    /// then meets its foot level (a point within <see cref="JoiningStep"/>) and is on or under no flight; every height it
    /// changes is dry and outside every location, and the road so landed is within <paramref name="cap"/> as every road's earthwork
    /// is measured (<see cref="WithinCap"/>, over <paramref name="earth"/>, each point <paramref name="widths"/> wide); and the staircase's
    /// own road at the foot taken away (StairRun.FromRoad or ToRoad, which the landing would leave below it) is raised to it
    /// within the same cap (<see cref="RaiseOwn"/>). A road onto a staircase's upper stairs from above is not cut down to a
    /// row: it is refused.
    /// </summary>
    internal static FlightLanding? LandingOnto(IReadOnlyList<Vector2> points, IReadOnlyList<float> heights, IReadOnlyList<float> widths,
        Func<float, float, float> ground, Func<Vector2, float> earth, float cap)
    {
        IReadOnlyList<StairRun> runs = StairStructures.Runs;
        if (points.Count == 0 || OnAFlight(runs, points, heights, out _) is not StairRun run) return null;
        StairKit kit = StairStructures.KitOf(run);
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
            if (Land(points, heights, removed, foot, height, ground, earth, cap, out var others, ofAFlight: true, own: fromFoot ? run.FromRoad : run.ToRoad,
                    widths: widths) is not float[] raised) continue;
            StairRun trimmed = StairRun.FromPlan(fromFoot ? foot : run.From, fromFoot ? run.To : foot, run.Width, plan, run.Kit);
            trimmed.KeepsLocationRocks = run.KeepsLocationRocks;
            trimmed.ExpectedLocation = run.ExpectedLocation;
            // The end kept keeps where the road meets it, and its road (StairRun.FromRoad, ToRoad); the landing's road meets
            // the new foot on it (MeetLanding, once that road is stored as its pieces).
            if (fromFoot) { trimmed.ToMeet = run.ToMeet; trimmed.ToRoad = run.ToRoad; }
            else { trimmed.FromMeet = run.FromMeet; trimmed.FromRoad = run.FromRoad; }
            var after = runs.Select(r => ReferenceEquals(r, run) ? trimmed : r).ToList();
            if (OnAFlight(after, points, raised, out _) != null) continue;
            return new FlightLanding { Run = run, Trimmed = trimmed, Plan = plan, Kit = kit, Height = height, Foot = foot, FootAtFrom = fromFoot, Removed = removed, Heights = raised, Others = others! };
        }
        return null;
    }

    /// <summary>
    /// The landing step, for every flight a road meets (<see cref="LandingOnto"/>, StructureEnds.MeetEnds): the road
    /// <paramref name="points"/>, standing at <paramref name="heights"/>, with its points within <see cref="OnRoad"/> of
    /// the ground <paramref name="removed"/> (where the stairs taken away stood) levelled at <paramref name="height"/>, the
    /// row's base, and the road either side eased to them at the road grade; null where it does not fit. It fits where the
    /// road comes to the row's <paramref name="foot"/> (a levelled point within <see cref="JoiningStep"/>), every height it
    /// changes is dry (<paramref name="ground"/>) and outside every location, the road so landed within <paramref name="cap"/> as every
    /// road's earthwork is measured (<see cref="WithinCap"/> over <paramref name="earth"/>; where <paramref name="landedAcross"/>,
    /// a structure's end, its caller measures it with the stretch run on under its joining piece, StructureEnds.WithinCapAcross), and for a standing
    /// flight (<paramref name="ofAFlight"/>; <paramref name="own"/>: its own road at the end taken away, StairRun.FromRoad or
    /// ToRoad, null where none is recorded) that road can be raised to it, no other road standing on the landing's ground at another height
    /// (<see cref="RaiseOwn"/>); for a structure's end stairs (no flight), every road on the landing's ground can be raised
    /// to it (<see cref="RaiseOnTheGround"/>). <paramref name="others"/>: what is raised.
    /// </summary>
    internal static float[]? Land(IReadOnlyList<Vector2> points, IReadOnlyList<float> heights, IReadOnlyList<Vector2> removed, Vector2 foot,
        float height, Func<float, float, float> ground, Func<Vector2, float> earth, float cap, out List<(RoadSpatialGrid.RoadPoint at, float to)>? others,
        bool overWater = false, bool ofAFlight = false, StairRoad? own = null, IReadOnlyList<float>? widths = null, bool landedAcross = false)
    {
        others = null;
        if (Fill(points, heights, removed, foot, height, ground, cap, overWater, widths, landedAcross ? null : earth) is not float[] raised) return null;
        others = ofAFlight ? RaiseOwn(own, removed, height, ground, earth, cap, overWater) : RaiseOnTheGround(removed, height, ground, earth, cap, overWater);
        return others == null ? null : raised;
    }

    /// <summary>
    /// Whether the road <paramref name="points"/> (each <paramref name="widths"/> wide, else the network's road width),
    /// standing at <paramref name="heights"/>, is within <paramref name="cap"/> as every road's earthwork is measured
    /// (RoadNetworkGenerator.EarthworkAcross, the one measurement, over <paramref name="earth"/>, the ground before roads) at
    /// the points <paramref name="changed"/> holds: a landing's fill and the roads it raises are judged as the finished road
    /// is (RoadNetworkGenerator.EarthworkRefusal), not point by point. The points it leaves as they were stand in the fit as
    /// the writer sees them.
    /// </summary>
    private static bool WithinCap(IReadOnlyList<Vector2> points, IReadOnlyList<float> heights, Func<int, float> widths, HashSet<Vector2> changed,
        Func<Vector2, float> earth, float cap)
    {
        if (changed.Count == 0) return true;
        var at = new List<Vector2>(points);
        var plan = new RoadSpatialGrid.PlannedPath(at, new List<float>(heights), new List<RoadPointDebugInfo>(), widths(0), 0f, false)
        { Widths = Enumerable.Range(0, at.Count).Select(widths).ToList() };
        bool within = true;
        RoadNetworkGenerator.EarthworkAcross(plan, earth, cap, p => { if (changed.Contains(p)) within = false; });
        return within;
    }

    /// <summary>The road's heights with a landing at <paramref name="height"/> over <paramref name="removed"/>, or null
    /// where it does not fit (<see cref="Land"/>).</summary>
    private static float[]? Fill(IReadOnlyList<Vector2> points, IReadOnlyList<float> heights, IReadOnlyList<Vector2> removed, Vector2 foot,
        float height, Func<float, float, float> ground, float cap, bool overWater, IReadOnlyList<float>? widths, Func<Vector2, float>? earth)
    {
        var along = new float[points.Count];
        for (int i = 1; i < points.Count; i++) along[i] = along[i - 1] + Vector2.Distance(points[i - 1], points[i]);
        var raised = heights.ToArray();
        var landing = new List<int>();
        for (int i = 0; i < points.Count; i++)
            if (OnTheLanding(removed, points[i])) landing.Add(i);
        if (landing.Count == 0 || !landing.Exists(i => Vector2.Distance(points[i], foot) <= JoiningStep)) return null;
        // Eased to the landing as a road is to a staircase's foot: its level landing, then the cap (RoadPitches.ClimbToFoot).
        float grade = RoadGrade.Configured;
        for (int i = 0; i < points.Count; i++)
        {
            float d = float.PositiveInfinity;
            foreach (int k in landing) d = Mathf.Min(d, Mathf.Abs(along[i] - along[k]));
            float reach = RoadPitches.ClimbToFoot(d, grade);
            raised[i] = Mathf.Clamp(heights[i], height - reach, height + reach);
        }
        // The foot's level landing (RoadPitches.JunctionLanding at JunctionLandingGrade) either side of the foot, edge by edge
        // as the end audit reads it (RoadPitches.LeavesItsLandingAround, StairEnds.OntoItsRoad): a road that cannot meet it
        // there is refused (BRIEF-RULES 17).
        int footAt = landing[0];
        foreach (int k in landing) if (Vector2.Distance(points[k], foot) < Vector2.Distance(points[footAt], foot)) footAt = k;
        if (RoadPitches.LeavesItsLandingAround(points, raised, along[footAt], RoadPitches.JunctionLandingGrade)) return null;
        Vector2? last = null;
        var changed = new HashSet<Vector2>();
        for (int i = 0; i < points.Count; i++)
        {
            if (Mathf.Abs(raised[i] - heights[i]) < 0.001f) { last = null; continue; }
            Vector2 p = points[i];
            if (!Dry(raised[i], ground(p.x, p.y), cap, overWater) || RoadLocationProtection.BlocksSegment(last ?? p, p, LocationMargin, default)) return null;
            changed.Add(p);
            last = p;
        }
        // Its earthwork as the finished road's is measured; a structure's end measures the road it lands with the stretch
        // run on under its joining piece (StructureEnds.WithinCapAcross, the same owner) and gives no ground here.
        if (earth != null && !WithinCap(points, raised, i => widths != null && i < widths.Count ? widths[i] : RoadNetworkGenerator.RoadWidth, changed, earth, cap))
            return null;
        return raised;
    }

    /// <summary>Whether <paramref name="p"/> lies on a structure's end landing: within <see cref="OnRoad"/> of a point it took
    /// (<see cref="Fill"/>), asked without a closure a point.</summary>
    private static bool OnTheLanding(IReadOnlyList<Vector2> removed, Vector2 p)
    {
        for (int r = 0; r < removed.Count; r++) if (Vector2.Distance(removed[r], p) < OnRoad) return true;
        return false;
    }

    /// <summary>
    /// Whether a landing may take ground at <paramref name="ground"/> to <paramref name="height"/>: dry ground, or over water
    /// where it may be filled (<paramref name="overWater"/>, the earthwork meet of a structure's end, StructureEnds.MeetEnds):
    /// the ground under the floor every road on land is built up to (RoadConstants.DryRoadFloor) filled to it as an ordinary
    /// road's is, within <paramref name="cap"/> from there. How far it may cut or fill dry ground is the earthwork cap's,
    /// measured as the finished road's (<see cref="WithinCap"/>).
    /// </summary>
    private static bool Dry(float height, float ground, float cap, bool overWater) =>
        RoadWater.BuiltDry(ground) || overWater && RoadWater.AtLeastDryFloor(height) - ground <= cap;

    /// <summary>How far apart two points of one stored road may stand and be walked from one to the other
    /// (<see cref="RaiseOnTheGround"/>): the network's roads are stored a few metres apart at most, and two roads this close
    /// side by side are refused (RoadNetworkGenerator.MeetingRefusal).</summary>
    private const float AlongOneRoad = 4f;

    /// <summary>
    /// Every other road on the ground a structure's end landing takes (its points within <see cref="OnRoad"/> of
    /// <paramref name="removed"/>) raised to the landing's <paramref name="height"/>, and on along that road eased to it as a road
    /// is to a foot (RoadPitches.ClimbToFoot: its level landing, then the road grade), so both roads meet level on the landing (StructureEnds.MeetEnds: a structure's end records no road of
    /// its own, so the road on its ground is found there, not by its identity; a flight's landing raises only its own road,
    /// <see cref="RaiseOwn"/>). Walked along the stored road from the landing (each point within <see cref="AlongOneRoad"/> of the
    /// last); only points whose height changes are returned, with the height each goes to. Null where it does not fit: a
    /// height it changes in water or in a location, the road raised past <paramref name="cap"/> as every road's earthwork is
    /// measured (<see cref="WithinCap"/>, over <paramref name="earth"/>), or the road still
    /// raised where the ease runs out of reach (twice the cap at the road grade). Empty where no other road is there.
    /// </summary>
    private static List<(RoadSpatialGrid.RoadPoint at, float to)>? RaiseOnTheGround(IReadOnlyList<Vector2> removed, float height, Func<float, float, float> ground,
        Func<Vector2, float> earth, float cap, bool overWater)
    {
        var changed = new List<(RoadSpatialGrid.RoadPoint at, float to)>();
        if (removed.Count == 0) return changed;
        float grade = RoadGrade.Floored;
        float ease = 2f * RoadGrade.EaseLength(RoadNetworkGenerator.Shaped(cap));
        Vector2 centre = default;
        foreach (Vector2 r in removed) centre += r;
        centre *= 1f / removed.Count;
        float spread = removed.Max(r => Vector2.Distance(r, centre));
        // Each stored point once (RoadSpatialGrid.GetRoadPointsNearPosition).
        var near = new List<RoadSpatialGrid.RoadPoint>();
        foreach (RoadSpatialGrid.RoadPoint rp in RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(centre.x, 0f, centre.y), spread + OnRoad + ease))
            if (rp.IsRoad) near.Add(rp);
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
        var heights = near.ConvertAll(rp => rp.h);
        var moved = new HashSet<Vector2>();
        for (int i = 0; i < near.Count; i++)
        {
            if (float.IsInfinity(from[i])) continue;
            RoadSpatialGrid.RoadPoint rp = near[i];
            // Eased to the landing as every road is to a foot's: its level landing, then the cap (RoadPitches.ClimbToFoot).
            float reach = RoadPitches.ClimbToFoot(from[i], grade);
            float to = Mathf.Clamp(rp.h, height - reach, height + reach);
            if (Mathf.Abs(to - rp.h) < 0.001f) continue;
            if (Vector2.Distance(rp.p, centre) > spread + OnRoad + ease - AlongOneRoad || !Dry(to, ground(rp.p.x, rp.p.y), cap, overWater)
                || RoadLocationProtection.BlocksSegment(rp.p, rp.p, LocationMargin, default)) return null;
            changed.Add((rp, to));
            heights[i] = to;
            moved.Add(rp.p);
        }
        // The roads raised, as the writer will see them with the points round them left as they stand.
        return WithinCap(near.ConvertAll(rp => rp.p), heights, i => near[i].w, moved, earth, cap) ? changed : null;
    }


    /// <summary>
    /// The flight's own road (<paramref name="own"/>, the road that met the end a landing takes away) raised to the landing's
    /// <paramref name="height"/> where it stands on the landing's ground (its points within <see cref="OnRoad"/> of
    /// <paramref name="removed"/>), and on along it, in its own order, eased to it as a road is to a foot
    /// (RoadPitches.ClimbToFoot: its level landing, then the road grade) and judged so either side of the landing's ground
    /// (RoadPitches.LeavesItsLandingAround), so both roads meet level on the landing; with it the levelled ground under a structure on that ground (unpainted points: a staircase's
    /// start pad, an end's apron), levelled to it. No other road is raised: a road's point on the landing's ground at another
    /// height refuses the landing, and so does the own road where it is not stored as recorded (StairRoad.Stored) and stands
    /// there. Only points whose height changes are returned, with the height each goes to. Null where it does not fit: a
    /// height it changes in water or in a location, the road raised past <paramref name="cap"/> as every road's earthwork is
    /// measured (<see cref="WithinCap"/>, over <paramref name="earth"/>), or the road still
    /// raised where the ease runs out of reach (twice the cap at the road grade, along the road). Empty where nothing changes.
    /// </summary>
    private static List<(RoadSpatialGrid.RoadPoint at, float to)>? RaiseOwn(StairRoad? own, IReadOnlyList<Vector2> removed, float height,
        Func<float, float, float> ground, Func<Vector2, float> earth, float cap, bool overWater)
    {
        var changed = new List<(RoadSpatialGrid.RoadPoint at, float to)>();
        if (removed.Count == 0) return changed;
        float grade = RoadGrade.Floored;
        float ease = 2f * RoadGrade.EaseLength(RoadNetworkGenerator.Shaped(cap));
        bool OnGround(Vector2 p) => removed.Any(r => Vector2.Distance(r, p) < OnRoad);
        bool Fits(Vector2 p, float to) => Dry(to, ground(p.x, p.y), cap, overWater) && !RoadLocationProtection.BlocksSegment(p, p, LocationMargin, default);
        var raised = new HashSet<Vector2>();
        var moved = new HashSet<Vector2>();
        if (own?.Stored() is RoadSpatialGrid.RoadPoint[] stored)
        {
            var heights = Array.ConvertAll(stored, rp => rp.h);
            // How far along its road each point stands from the landing's ground: 0 on it.
            var along = new float[stored.Length];
            for (int i = 1; i < stored.Length; i++) along[i] = along[i - 1] + Vector2.Distance(stored[i - 1].p, stored[i].p);
            var landing = new List<int>();
            for (int i = 0; i < stored.Length; i++) if (OnGround(stored[i].p)) landing.Add(i);
            for (int i = 0; i < stored.Length && landing.Count > 0; i++)
            {
                float from = float.PositiveInfinity;
                foreach (int k in landing) from = Mathf.Min(from, Mathf.Abs(along[i] - along[k]));
                RoadSpatialGrid.RoadPoint rp = stored[i];
                // Eased to the landing as every road is to a foot's: its level landing, then the cap (RoadPitches.ClimbToFoot).
                float reach = RoadPitches.ClimbToFoot(from, grade);
                float to = Mathf.Clamp(rp.h, height - reach, height + reach);
                raised.Add(rp.p);
                if (Mathf.Abs(to - rp.h) < 0.001f) continue;
                if (from > ease || !Fits(rp.p, to)) return null;
                changed.Add((rp, to));
                heights[i] = to;
                moved.Add(rp.p);
            }
            // Its own road meets the landing level either side of the landing's ground, as the road the flight lands does
            // (RoadPitches.LeavesItsLandingAround, Fill), or the landing is refused (BRIEF-RULES 17).
            if (landing.Count > 0)
            {
                var points = Array.ConvertAll(stored, rp => rp.p);
                int first = landing.Min(), last = landing.Max();
                if (RoadPitches.LeavesItsLandingAround(points, heights, along[first], RoadPitches.JunctionLandingGrade)
                    || RoadPitches.LeavesItsLandingAround(points, heights, along[last], RoadPitches.JunctionLandingGrade)) return null;
            }
            // Its own road raised, along it as stored.
            if (!WithinCap(Array.ConvertAll(stored, rp => rp.p), heights, i => stored[i].w, moved, earth, cap)) return null;
            moved.Clear();
        }
        // Every other stored point on the landing's ground, each once (RoadSpatialGrid.GetRoadPointsNearPosition).
        Vector2 centre = default;
        foreach (Vector2 r in removed) centre += r;
        centre *= 1f / removed.Count;
        float spread = removed.Max(r => Vector2.Distance(r, centre));
        var levelled = new List<RoadSpatialGrid.RoadPoint>();
        foreach (RoadSpatialGrid.RoadPoint rp in RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(centre.x, 0f, centre.y), spread + OnRoad))
        {
            if (!rp.IsRoad || raised.Contains(rp.p) || !OnGround(rp.p) || Mathf.Abs(rp.h - height) < 0.001f) continue;
            if (!rp.unpainted || !Fits(rp.p, height)) return null;
            changed.Add((rp, height));
            levelled.Add(rp);
            moved.Add(rp.p);
        }
        // The levelled ground under a structure there, at the landing's height.
        return WithinCap(levelled.ConvertAll(rp => rp.p), levelled.ConvertAll(_ => height), i => levelled[i].w, moved, earth, cap) ? changed : null;
    }

    /// <summary>
    /// The road that meets a landing's new foot (<see cref="FlightLanding.Foot"/>, the trimmed staircase's moved end), recorded
    /// on it (StairRun.FromRoad or ToRoad): the landing's own road, of its stored pieces (<paramref name="own"/>) the one levelled
    /// at the foot (its point nearest the foot, within <see cref="JoiningStep"/> of it, as <see cref="Land"/> requires), from that
    /// point on along the longer way, the road's body (the shorter way, if any, is the landing it ends on past the foot). None
    /// where no piece of it comes there: left unknown, never another road found near it.
    /// </summary>
    internal static void MeetLanding(FlightLanding landing, IReadOnlyList<RoadSpatialGrid.PlannedPath> own)
    {
        StairRoad? meets = null;
        float nearest = float.PositiveInfinity;
        foreach (RoadSpatialGrid.PlannedPath piece in own)
        {
            List<Vector2> points = piece.Points;
            for (int i = 0; i < points.Count && points.Count >= 2; i++)
            {
                float d = Vector2.Distance(points[i], landing.Foot);
                if (d > JoiningStep || d >= nearest) continue;
                nearest = d;
                float before = 0f, after = 0f;
                for (int k = 1; k <= i; k++) before += Vector2.Distance(points[k - 1], points[k]);
                for (int k = i + 1; k < points.Count; k++) after += Vector2.Distance(points[k - 1], points[k]);
                var road = after >= before ? points.GetRange(i, points.Count - i) : points.GetRange(0, i + 1);
                if (after < before) road.Reverse();
                meets = new StairRoad(road);
            }
        }
        if (landing.FootAtFrom) landing.Trimmed.FromRoad = meets; else landing.Trimmed.ToRoad = meets;
    }

    /// <summary>A copy of the plan a standing run was laid from, where it was laid this generation as it stands (its turns
    /// counted on its pieces); else a plan of its pieces and clearances as they stand.</summary>
    private static StairPlan PlanOf(StairRun run, StairKit kit)
    {
        if (s_laid.TryGetValue(run, out var laid) && laid.plan.Pieces.Count == run.Pieces.Count) return laid.plan.Copy();
        var plan = new StairPlan { Kit = kit.Name, Reached = true };
        foreach (StructurePiece p in run.Pieces)
        {
            StairPiece piece = StairRun.PieceOf(p);
            StairPieceKind kind = piece.Kind;
            plan.Pieces.Add(piece);
            if (kind == StairPieceKind.Stair) plan.Stairs++;
            else if (kind == StairPieceKind.Landing) plan.Landings++;
        }
        plan.Clearances.AddRange(run.Clearances);
        return plan;
    }

    /// <summary>
    /// A landing laid with its road (<see cref="LandingOnto"/>; the road's heights are the caller's): the staircase is
    /// replaced in the network by itself cut back, its laid plan with it, its own road on its ground raised to it
    /// (<see cref="FlightLanding.Others"/>), and its cuts under the pieces taken away and
    /// below the landing are taken back, which would dig through the landing's fill. False where the staircase no longer
    /// stands as the landing found it.
    /// </summary>
    /// <summary>The cuts a landing takes back (<see cref="LayLanding"/>): under the pieces it takes away, below the landing,
    /// which would dig through its fill. What the commit takes and its road's checks leave out before (RoadTerrainModifier.AsStored).</summary>
    internal static RoadSpatialGrid.CutsTakenBack CutsTakenBack(FlightLanding landing)
    {
        float below = landing.Height - StairLayout.CutBelowSurface - 0.01f;
        return new RoadSpatialGrid.CutsTakenBack(landing.Removed, StairLayout.PieceRun, rp => rp.h < below);
    }

    internal static bool LayLanding(FlightLanding landing, bool raiseOwn = true)
    {
        if (!StairStructures.Swap(landing.Run, landing.Trimmed)) return false;
        // Generation raises them with its road's other raises, in their order (RoadNetworkGenerator.CrossingGround.Raises).
        if (raiseOwn) RoadSpatialGrid.SetRoadHeights(landing.Others);
        if (s_laid.TryRemove(landing.Run, out _)) s_laid[landing.Trimmed] = (landing.Plan, landing.Kit);
        RoadSpatialGrid.TakeBack(CutsTakenBack(landing));
        System.Threading.Interlocked.Increment(ref FlightLandings);
        return true;
    }
}
