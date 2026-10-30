using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>A road's end at a placed place: where the staircase onto its landing sets off
/// (<see cref="RoadStairRuns.PlanPlacedSiteApproach"/>).</summary>
public sealed class SiteApproachRequest
{
    /// <summary>How much of a road's end is kept in <see cref="RoadTail"/> to keep the staircase off it: about 40 m at the 8 m cell.</summary>
    public const int TailPoints = 6;

    public Vector2 RoadEnd;
    public float RoadHeight;
    /// <summary>The heading the road arrives at its end on.</summary>
    public float Arriving;
    /// <summary>The place's centre.</summary>
    public Vector2 Centre;
    /// <summary>The road's last stretch, ending at <see cref="RoadEnd"/>: the staircase must keep off it.</summary>
    public List<Vector2> RoadTail = new();
    /// <summary>How far from the road's end the landing may be (<see cref="RoadStairRuns.SiteReach"/> unless the caller
    /// reaches further).</summary>
    public float Reach = RoadStairRuns.SiteReach;
    /// <summary>The stair layout's search allowance per landing and start tried: a road staircase's, as a way onto a landing
    /// is built with the roads (<see cref="RoadNetworkGenerator.EntryTotalExpansions"/>).</summary>
    public int TotalExpansions = RoadNetworkGenerator.EntryTotalExpansions, Stall = RoadNetworkGenerator.EntryStall;
    /// <summary>Most steps one leg of the layout searches: a road staircase's leg.</summary>
    public int Leg = RoadStairRuns.GenerationExpansions;
}

/// <summary>
/// The staircase from a road's end onto a placed place's landing (one owner): aimed where the landing joins the slope, then at
/// the landings, led round the place's rock to its front, kept out of its pit and off its boulders, set off beside the road's end
/// where it turns back along it, and finished over the ledge it arrives at.
/// </summary>
public static partial class RoadStairRuns
{
    /// <summary>A staircase climbing to the ledge in front of a cave may finish this far from its middle.
    /// 4.5 m reached the frost cave at 1408,-5888 turned 180 but not turned 157.5;
    /// 6 m reaches both, on their dumped ground.</summary>
    public const float LedgeReach = 6f;

    /// <summary>How much further from the landing an approach's top may end once finished over its ledge (climbed on, crested,
    /// a floor out): within LedgeReach, a crest onto the shelf at Hildir_cave -3520,-4608 was refused and the top stopped a
    /// metre under it (the stair came in a bit too low). The flat and the place's rock still bound it.</summary>
    public const float FinishReach = 2f * StairLayout.PieceRun;

    /// <summary>How many aims an approach tries.</summary>
    public const int AimTries = 5;

    /// <summary>A staircase onto a placed place's landing: its cuts outside every footprint, which the terrain writer
    /// leaves to the place's own levelling.</summary>
    internal static List<Vector3> CutsOutsideFootprints(StairPlan plan) =>
        plan.Cuts.FindAll(c => !RoadSiteProtection.Contains(new Vector2(c.x, c.z)));

    /// <summary>Search steps the last approach laid out on this thread spent, every layout together (log, tests).</summary>
    [ThreadStatic] internal static int LastApproachSteps;

    /// <summary>
    /// The staircase for a road end at a place once the place is placed: turned to <paramref name="yaw"/>, its root at
    /// <paramref name="root"/>, over <paramref name="ground"/>, the ground the
    /// game made (any thread: pure). A place that turns to face downhill faces
    /// its mouth that way, with the floor dug behind it. The road must arrive from the front; the staircase ends
    /// facing into the place, first at the floor's front edge (down to it from a
    /// road above), then, failing that, on the rim in front of the floor, the
    /// crest of the ground out to the edge of the floor's smoothing (up to it
    /// from a road below, which cannot climb over the rim and down again). Its
    /// shape is checked here; what it runs into is checked by the caller
    /// (<see cref="SiteConflict"/>).
    /// </summary>
    public static RoadStairSpan? PlanPlacedSiteApproach(SiteApproachRequest request, float yaw, IReadOnlyList<LevelOp> ops,
        Func<float, float, float> ground, Func<float, float, Heightmap.Biome> biome, int seed, out string why, float rockRadius = 0f,
        Func<Vector2, bool>? inFootprint = null, IReadOnlyList<SiteRock>? rocks = null, bool keepSmallRocks = false, float root = float.NaN,
        StairContinuation? onStairs = null, bool joinedAims = true, PlacedSiteShape? placed = null)
    {
        why = "";
        LevelOp? found = Flat(ops);
        if (found == null) { why = "no flat ground"; return null; }
        Vector2 roadEnd = request.RoadEnd;
        // onStairs: the road ends on the top row of a staircase (TopAt), which stands at its own
        // height; the approach carries on from that row, off its snap edge, as the same staircase.
        if (onStairs == null && Mathf.Abs(ground(roadEnd.x, roadEnd.y) - request.RoadHeight) > SiteFootTolerance) { why = "road end off the ground"; return null; }
        // The place's rock: no piece within its exterior radius but in the cone in front of
        // its floor. A road arriving behind or beside it is led round, outside that, to the
        // front (a road wraps around to the front); 11 of 14 approaches were
        // refused for arriving behind before. Nor on its own boulders, which reach past
        // that radius (rock around a frost cave was being destroyed, likely part of
        // its prefab): a piece keeps its half width clear of each.
        // The entrance: the floor and the ground its smoothing drops into it. The staircase lands
        // on the wide flat landing in front, never down in it (the stairs should land on the wide
        // flat landing in front of the cave, not touch right up to its entrance).
        // A placed place's shape is built once, with its door (PlacedSites.PrepareOne); the road onto the landing aims at the same spot.
        PlacedSiteShape shape = placed ?? PlacedSiteShape.Of(request.Centre, yaw, found.Value, rockRadius, rocks, ground, keepSmallRocks, root);
        Vector2 flatCentre = shape.FlatCentre, front = shape.Front;
        float pit = shape.Pit;
        float inward = StairLayout.Wrap(yaw + 180f);
        // margin: the search keeps each piece's centre this much further off, so a turn's fillers
        // (laid after it, up to about a metre and a half from a piece's centre) stay clear too.
        bool Allowed(Vector2 p, float margin) => shape.Allowed(p, margin, Width * 0.5f + margin);
        Func<Vector2, bool> allowed = p => Allowed(p, FillerReach);
        List<Vector2>? around = shape.Behind(roadEnd) ? Around(roadEnd, shape.Centre, front, shape.KeepOut) : null;
        // The level ground beside the mouth, as the road aims at (ideally the road or stairs come up to be level); the
        // point in front of the pit where none is found.
        var aims = new List<(string name, Vector2 target)>();
        // Aimed first where the landing's level ground joins the slope, then at the landings themselves, overhanging
        // ones last (PlacedSiteShape.Aims); joinedAims false (tests) aims at the level landings in front of the mouth only.
        if (joinedAims) aims.AddRange(shape.Aims(ground, roadEnd, 1f, AimTries));
        else foreach (var (at, _) in shape.Landings(ground, 1f)) aims.Add(("level landing", at));
        if (aims.Count == 0) aims.Add(("landing", shape.Landing));
        StairKit kit = StairKit.ForLine(new List<Vector2> { roadEnd, aims[0].target }, biome, seed);
        var tried = new List<string>();
        // The steps every layout of the approach spent, every landing and start together (LastApproachSteps).
        int spent = 0;
        int Leg() => request.Leg;
        int Total() => request.TotalExpansions;
        StairPlan Spent(StairPlan laid) { spent += laid.Expansions; return laid; }
        // From a staircase's top row: carried on off its snap edge first, then, where no piece fits
        // that way (a steep face straight on from the row buried every heading: MountainCave02
        // -2560,1536), set off from the row's end
        // any way, at its height, with no sunk foot under the row.
        var starts = onStairs.HasValue ? new StairContinuation?[] { onStairs, null } : new StairContinuation?[] { null };
        foreach (StairContinuation? from in starts)
        foreach (var (name, target) in aims)
        {
            bool sunk = !onStairs.HasValue;
            float apron = ground(target.x, target.y);
            string aim = $"{name} ({target.x:F1}, {apron:F1}, {target.y:F1}){(onStairs.HasValue && !from.HasValue ? " from the row's end" : "")}";
            why = AimRefused(roadEnd, request.RoadHeight, target, apron, roadRefused: true, request.Reach);
            if (why != "") { tried.Add($"{aim}: {why}"); continue; }
            // A landing at the top is for a road to meet: only where the top is the road's end.
            // At the place's end the walker steps off onto its ground, and a floor carried on
            // past the aim ran into the rim.
            // Climbing to the ledge, it may finish anywhere on it: whole-metre steps from the
            // road's height met no one point of a real ledge (frost cave at 1408,-5888:
            // within 2.3 m of the rim at 0.58 m off its height, refused). Only a climb: laid
            // down from a road above, the layout starts at the ledge.
            bool area = apron > request.RoadHeight;
            List<Vector2>? corridor = around == null ? null : new List<Vector2>(around) { target };
            // A ridge between: up from the road's end to its crest, then a second staircase
            // up from the landing to meet the first head-on on top (the ridge before the frost cave at 3968,3584). A valley
            // the other way: down from the road's end to its floor, then on up from there.
            Vector2? walkway = null;
            var turn = around == null ? Turn(roadEnd, target, ground, out walkway) : null;
            StairPlan plan;
            Vector2 goal = target;
            if (turn.HasValue && turn.Value.crest)
            {
                StairPlan up = Spent(StairLayout.Build(roadEnd, turn.Value.at, ground, Width, kit, Leg(), request.Arriving, null, null,
                    Total(), request.Stall, 0, sunkFoot: sunk, maxBuried: StairCover.ApproachSearch, allowed: allowed, after: from));
                plan = up;
                if (up.Reached)
                {
                    Vector2 top = new Vector2(up.End.x, up.End.z);
                    StairPlan down = Spent(StairLayout.Build(top, target, ground, Width, kit, Leg(), up.EndHeading, inward, null,
                        Total(), request.Stall, 0, sunkFoot: true, maxBuried: StairCover.ApproachSearch, allowed: allowed));
                    // Held to face the place at the landing and to meet the first head-on, the
                    // second fitted nowhere beside the place's rock (5632,5312 and
                    // -9088,-512: no piece fits down from the crest): then as it falls.
                    if (!down.Reached)
                        down = Spent(StairLayout.Build(top, target, ground, Width, kit, Leg(), null, null, null,
                            Total(), request.Stall, 0, sunkFoot: true, maxBuried: StairCover.ApproachSearch, allowed: allowed));
                    plan = Join(up, down, new Vector3(target.x, apron, target.y));
                }
            }
            else if (turn.HasValue)
            {
                // Laid down from the road (climbing from the valley floor, the plan reversed),
                // its foot exactly on the floor; the second sets off from there the way the
                // first came down.
                Vector2 floor = turn.Value.at;
                StairPlan down = Spent(StairLayout.Build(roadEnd, floor, ground, Width, kit, Leg(), request.Arriving, null, null,
                    Total(), request.Stall, TopLandings, sunkFoot: false, maxBuried: StairCover.ApproachSearch, allowed: allowed, after: from));
                plan = down;
                if (down.Reached)
                {
                    float walking = down.Reversed ? StairLayout.Wrap(down.StartHeading + 180f) : down.EndHeading;
                    StairPlan up = Spent(StairLayout.Build(floor, target, ground, Width, kit, Leg(), walking, inward, null,
                        Total(), request.Stall, 0, sunkFoot: false, maxBuried: StairCover.ApproachSearch,
                        arriveWithin: LedgeReach, allowed: allowed));
                    if (!up.Reached)
                        up = Spent(StairLayout.Build(floor, target, ground, Width, kit, Leg(), null, null, null,
                            Total(), request.Stall, 0, sunkFoot: false, maxBuried: StairCover.ApproachSearch,
                            arriveWithin: LedgeReach, allowed: allowed));
                    plan = Join(down, up, new Vector3(target.x, apron, target.y));
                }
            }
            else
            {
                // Ends where it reaches ground a player walks to the landing on anyway, not at the
                // landing (a staircase that turned on the flat before a frost cave can just stop where it hits the flats, not at the
                // entrance), and sets
                // off any way from the road's end: held to the road's heading, staircases whose
                // landing lay behind it turned back on a platform where they met the road (a 180ish turn onto a weird 4x4 platform). Behind the place, led round it,
                // it keeps both.
                if (around == null)
                {
                    goal = FlatsFrom(roadEnd, target, ground);
                    if (Vector2.Distance(roadEnd, goal) < CrestFromEnd) { tried.Add($"{aim}: walkable ground to it from the road's end"); continue; }
                }
                bool flats = goal != target;
                // Set off facing the way the road arrives (StairLayout.FacingSteps); freely only where that does not reach.
                plan = Spent(StairLayout.Build(roadEnd, goal, ground, Width, kit, Leg(), around == null ? null : request.Arriving,
                    flats ? null : inward, corridor, Total(), request.Stall, apron < request.RoadHeight && !flats ? TopLandings : 0, sunkFoot: sunk,
                    maxBuried: StairCover.ApproachSearch, arriveWithin: area || flats ? LedgeReach : null, allowed: allowed, after: from,
                    facing: from.HasValue || ground(goal.x, goal.y) < ground(roadEnd.x, roadEnd.y) ? null : request.Arriving));
                if (!plan.Reached && around == null && !from.HasValue)
                {
                    System.Threading.Interlocked.Increment(ref FeetFacedFreely);
                    string faced = plan.Reason ?? "";
                    plan = Spent(StairLayout.Build(roadEnd, goal, ground, Width, kit, Leg(), null,
                        flats ? null : inward, corridor, Total(), request.Stall, apron < request.RoadHeight && !flats ? TopLandings : 0, sunkFoot: sunk,
                        maxBuried: StairCover.ApproachSearch, arriveWithin: area || flats ? LedgeReach : null, allowed: allowed, after: from));
                    if (RoadNetworkGenerator.RoadsLog)
                        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(FormattableString.Invariant(
                            $"[STAIRS] approach to {request.Centre} from ({roadEnd.x:F1}, {roadEnd.y:F1}), road arriving {request.Arriving:F0}: no layout facing it ({faced}); laid any way: {(plan.Reached ? "reached" : "not reached")}, setting off {plan.StartHeading:F1}"));
                }
            }
            string Refused(StairPlan laid)
            {
                // Earthwork is made outside every footprint, as for a road's staircase; inside one the
                // terrain writer leaves the ground alone, so only there must the plan need none. Its
                // foot, at the road's end outside the footprint, was laid as floors flush with or
                // under the snow, and the staircase seemed not to reach the road.
                float coverInside = 0f;
                foreach (Vector3 c in laid.Cuts)
                    if (inFootprint == null || inFootprint(new Vector2(c.x, c.z)))
                        coverInside = Mathf.Max(coverInside, ground(c.x, c.z) - c.y - StairLayout.CutBelowSurface);
                string refused = !laid.Reached ? "not reached"
                    : coverInside > StairCover.ApproachRefusal ? "needs earthwork in the footprint"
                    : OnFlat(laid, flatCentre, pit, front) ? "too far onto the flat"
                    // Set off back along its road's end, on it (OverRoad): the next landing or start is tried instead.
                    : onStairs == null && request.RoadTail.Count >= 2
                      && OverRoad(laid, request.RoadTail, request.RoadTail.Count - 1, request.RoadTail.Count - 1, ground) ? "on its road"
                    : "";
                // The search keeps each piece's centre allowed; a turn's fillers come after it.
                if (refused == "" && laid.Pieces.Exists(pc => pc.Kind != StairPieceKind.Support && !Allowed(new Vector2(pc.Position.x, pc.Position.z), 0f)))
                    refused = "in the place's rock";
                return refused;
            }
            why = Refused(plan);
            // Set off at a sharp angle to the road, or back along it: laid again from beside the road's end, on the side it
            // turns to, its first stair at the road's height and its edge on the road's edge (as BesideItsRoad for a road's
            // staircase), not across the road's end with a floor turned on it (Hildir_cave -2304,1536: where it starts at a sharper
            // angle it should align more to the side of the road).
            if (why == "" && !turn.HasValue && around == null && !from.HasValue && !plan.Reversed)
            {
                // By the first stair where it sets off back, else the settled flight: a run back beside the road (up to
                // StairLayout.MostSetOffBack pieces) settles facing it, but its base stood on the road's end.
                StairPiece? firstStair = plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair)
                    .OrderBy(p => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), roadEnd)).FirstOrDefault();
                float firstHeading = firstStair == null ? plan.StartHeading : StairLayout.Wrap(firstStair.YawDegrees + 180f);
                float settled = Mathf.Abs(StairLayout.Wrap(firstHeading - request.Arriving + 180f) - 180f) > ApproachBesideFrom
                    ? firstHeading : SettledHeading(plan, roadEnd);
                foreach (Vector2 at in BesideTheRoadsEnd(roadEnd, request.Arriving, settled, ApproachBesideFrom))
                {
                    if (!Allowed(at, FillerReach)) continue;
                    // On a share of the allowance: the staircase is laid already, this only moves its foot.
                    StairPlan again = Spent(StairLayout.Build(at, goal, ground, Width, kit, Leg() / 2, plan.StartHeading,
                        goal != target ? null : inward, corridor, Total() / 2, request.Stall / 2, apron < request.RoadHeight && goal == target ? TopLandings : 0, sunkFoot: sunk,
                        maxBuried: StairCover.ApproachSearch, arriveWithin: area || goal != target ? LedgeReach : null, allowed: allowed,
                        footHeight: request.RoadHeight, facing: request.Arriving));
                    if (Refused(again) != "") continue;
                    plan = again;
                    System.Threading.Interlocked.Increment(ref FeetBeside);
                    break;
                }
            }
            if (why != "") { tried.Add($"{aim}: {why}{(plan.Reached ? "" : $" ({plan.Reason})")}"); continue; }
            if (walkway is Vector2 dip)
            {
                // Over the dip at its lower end's height: its lowest floors.
                float run = plan.Pieces.Where(p => p.Kind == StairPieceKind.Landing).Select(p => p.Position.y + kit.FloorTop).DefaultIfEmpty(apron).Min();
                var (most, length) = StairChain.Walkway(plan, run, kit, ground);
                StairChain.LogWalkway(dip, kit, most, length, laid: true);
            }
            // Climbing onto the ledge, its top is where a player walks on from the place's
            // ground: stairs on down to that ground where it stands over it (not into the
            // entrance pit, nor into the place's rock: then it stays as it was).
            bool topAtPlace = turn.HasValue ? !turn.Value.crest : !plan.Reversed;
            if (topAtPlace)
            {
                var misfits = RoadNetworkGenerator.RoadsLog ? new List<string>() : null;
                bool Fits(StairPlan p)
                {
                    string m = OnFlat(p, flatCentre, pit, front) ? "onto the flat"
                        : p.Pieces.Exists(pc => pc.Kind != StairPieceKind.Support && !Allowed(new Vector2(pc.Position.x, pc.Position.z), 0f)) ? "in the place's rock"
                        // Climbed on, its top stays within reach of the landing it was aimed at, not past it onto the place's rim.
                        : Vector2.Distance(new Vector2(p.End.x, p.End.z), target) > LedgeReach + FinishReach + 0.01f ? "past the landing"
                        : "";
                    if (m != "") misfits?.Add(m);
                    return m == "";
                }
                float lip = misfits != null ? StairLayout.TopLip(plan, ground) - plan.End.y : 0f;
                LedgeFinish finish = FinishOverLedge(plan, ground, Width, Fits);
                if (misfits != null)
                    ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(FormattableString.Invariant(
                        $"[STAIRS] approach to {request.Centre}: top at ({plan.End.x:F1}, {plan.End.y:F2}, {plan.End.z:F1}), lip {lip:F2}, finished {finish}{(misfits.Count > 0 ? "; refused: " + string.Join(", ", misfits) : "")}"));
            }
            LastApproachSteps = spent;
            return Span(plan, roadEnd, request.RoadHeight, target, apron, kit, request.RoadTail.Count - 1);
        }
        why = string.Join("; ", tried);
        LastApproachSteps = spent;
        return null;
    }

    /// <summary>How a staircase's top finished over the ledge it arrives at (<see cref="FinishOverLedge"/>).</summary>
    internal enum LedgeFinish { None, LandingThenDown, BackToBack, Crest, TopFloor }

    /// <summary>Staircase tops finished with a crest this generation (<see cref="StairLayout.AddCrest"/>).</summary>
    public static int Crests;

    /// <summary>Staircase tops carried on up more stairs this generation (<see cref="StairLayout.ClimbOn"/>).</summary>
    public static int ClimbedOn;

    /// <summary>
    /// A staircase's top over the lip in front of a place's mouth steps down onto it, without the top at the
    /// lip's exact height (two stairs laid back down from the top floors let a player climb onto the lip without
    /// needing exact heights): up, its top 2x2 landing, then
    /// down; back to back, the landing made a step down, only where there is no room for the landing (a back-to-back
    /// stair also works, but it looks better with a 2x2 between them, though that is not
    /// required). As many rows down as <paramref name="fits"/> (out of the pit and the rock): all or none,
    /// a descent of two rows into the pit took the one that would have fitted with it.
    /// </summary>
    internal static LedgeFinish FinishOverLedge(StairPlan plan, Func<float, float, float> ground, float width, Func<StairPlan, bool> fits)
    {
        bool Descent(int most)
        {
            for (int rows = most; rows >= 1; rows--)
            {
                var added = StairLayout.AddDescent(plan, ground, width, rows);
                if (added.pieces == 0) return false;
                if (fits(plan)) return true;
                StairLayout.RemoveDescent(plan, added);
            }
            return false;
        }
        // Under ground that keeps rising: more stairs straight on first, then whatever finishes the top (as a road's, but
        // within an approach's cover and kept out of the place's pit and rock).
        StairLayout.TopFinish finish = StairLayout.FinishTop(plan, ground, width, StairCover.ApproachRefusal, fits, out bool climbed);
        if (climbed) System.Threading.Interlocked.Increment(ref ClimbedOn);
        if (finish == StairLayout.TopFinish.TopFloor) { System.Threading.Interlocked.Increment(ref TopFloors); return LedgeFinish.TopFloor; }
        if (finish == StairLayout.TopFinish.Crest) { System.Threading.Interlocked.Increment(ref Crests); return LedgeFinish.Crest; }
        if (Descent(2)) return LedgeFinish.LandingThenDown;
        if (!StairLayout.StepDownTop(plan, ground, width)) return LedgeFinish.None;
        Descent(1);
        return LedgeFinish.BackToBack;
    }

    /// <summary>Half the cone in front of a place's floor where its approach may come in past its
    /// rock: the whole front side of the line through its floor across its facing. At 45 the rock's
    /// ring shut out the natural line from a road's end beside a frost cave to the level ledge
    /// beside its mouth; in front, the boulders round its
    /// mouth keep approaches off it (<see cref="PlacedSiteShape.Body"/>).</summary>
    public const float FrontCone = 90f;

    /// <summary>A staircase keeps this far outside a place's exterior radius but in front of it,
    /// and is led round it this much further out again (the frost cave's own
    /// boulders carved by the loop: the road and stairs go a bit further out when looping to the front).</summary>
    public const float RockMargin = 3f, RockClearance = 3f;

    /// <summary>How far a turn's fillers can stand from the piece the search placed.</summary>
    public const float FillerReach = 1.5f;

    /// <summary>A cave's landing is aimed at this far beyond its entrance pit (its floor's smoothing).</summary>
    public const float LandingOut = 3f;

    /// <summary>Waypoints from a road's end behind a place round to its side, the shorter way,
    /// every 30 degrees on a circle <see cref="RockClearance"/> outside its rock. From the side
    /// the layout finds its own way in (it keeps out of the rock regardless): led on round to
    /// the cone, a staircase down a hillside dipped below the ledge and had to climb again,
    /// and a staircase only goes one way.</summary>
    internal static List<Vector2> Around(Vector2 roadEnd, Vector2 centre, Vector2 front, float rockRadius)
    {
        float ring = rockRadius + RockClearance;
        float from = Mathf.Atan2(roadEnd.y - centre.y, roadEnd.x - centre.x) * Mathf.Rad2Deg;
        float facing = Mathf.Atan2(front.y, front.x) * Mathf.Rad2Deg;
        static float Delta(float a, float b) { float d = (b - a) % 360f; if (d > 180f) d -= 360f; if (d < -180f) d += 360f; return d; }
        float delta = Delta(from, facing);
        float side = delta >= 0f ? 1f : -1f;
        // Stop at the place's side, 90 degrees off its facing, on the side we come round from.
        float sweep = delta - side * 90f;
        var points = new List<Vector2> { roadEnd };
        int steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Abs(sweep) / 30f));
        for (int i = 1; i <= steps; i++)
        {
            float a = (from + sweep * i / steps) * Mathf.Deg2Rad;
            points.Add(centre + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * ring);
        }
        return points;
    }
}
