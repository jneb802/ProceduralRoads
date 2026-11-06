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
    /// <summary>The heading the road arrives at its end on, read from the road as planned where it is
    /// (<see cref="RoadStairRuns.ArrivingTravel"/>).</summary>
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
/// the landings, led round the place's rock to its front, kept out of its pit and off its boulders, meeting the road's end on its
/// ring (<see cref="RoadEndRing"/>) and never across it, and finished over the ledge it arrives at.
/// </summary>
public static partial class RoadStairRuns
{
    /// <summary>A staircase climbing to the ledge in front of a cave may finish this far from its aim, along the ledge's lip:
    /// how far onto the ledge is the lip's (<see cref="PastTheLip"/>). 4.5 m reached the frost cave at 1408,-5888 turned 180
    /// but not turned 157.5; 6 m reaches both, on their dumped ground.</summary>
    public const float LedgeReach = 6f;

    /// <summary>How much further from the landing an approach's top may end once finished over its ledge (climbed on, crested,
    /// a floor out): within LedgeReach, a crest onto the shelf at Hildir_cave -3520,-4608 was refused and the top stopped a
    /// metre under it (the stair came in a bit too low). The flat and the place's rock still bound it.</summary>
    public const float FinishReach = 2f * StairLayout.PieceRun;

    /// <summary>An approach refused for a walked piece on its own road past the joining step (<see cref="OverRoad"/>).</summary>
    internal const string OnItsRoad = "on its road";

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
    /// Laid in the kit the line from the road's end to the first aim guesses, it is built in the kit its own pieces stand
    /// in (<see cref="StairKit.ForPlan"/>): laid again in <paramref name="laidIn"/>, that kit, where it differs, and kept as
    /// first laid where none is laid so.
    /// </summary>
    public static RoadStairSpan? PlanPlacedSiteApproach(SiteApproachRequest request, float yaw, IReadOnlyList<LevelOp> ops,
        Func<float, float, float> ground, Func<float, float, Heightmap.Biome> biome, int seed, out string why, float rockRadius = 0f,
        Func<Vector2, bool>? inFootprint = null, IReadOnlyList<SiteRock>? rocks = null, bool keepSmallRocks = false, float root = float.NaN,
        StairContinuation? onStairs = null, bool joinedAims = true, PlacedSiteShape? placed = null, StairKit? laidIn = null,
        bool faceInto = true)
    {
        // Every top has a way off it (StairEnds): searched first with a layout whose top no finish fits refused, so the next
        // aim, start or facing is tried; only where none is laid so is it laid as before, its top a bare stair, and counted.
        // The first pass's counts are held (ApproachCount) and kept only where its result is: laid again, the second pass
        // searches the same aims and counts them itself.
        t_bareRefused = false;
        int[]? outer = t_held;
        var held = new int[ApproachCounts];
        t_held = held;
        RoadStairSpan? span;
        try
        {
            span = Approach(request, yaw, ops, ground, biome, seed, out why, rockRadius, inFootprint, rocks, keepSmallRocks, root,
                onStairs, joinedAims, placed, laidIn, faceInto, refuseBare: true);
        }
        finally { t_held = outer; }
        if (span != null || !t_bareRefused)
        {
            for (int c = 0; c < ApproachCounts; c++)
                if (held[c] == 0) continue;
                else if (outer != null) outer[c] += held[c];
                else System.Threading.Interlocked.Add(ref Counter((ApproachCount)c), held[c]);
            return span;
        }
        int spent = LastApproachSteps;
        string refused = why;
        span = Approach(request, yaw, ops, ground, biome, seed, out why, rockRadius, inFootprint, rocks, keepSmallRocks, root,
            onStairs, joinedAims, placed, laidIn, faceInto, refuseBare: false);
        LastApproachSteps += spent;
        if (span != null)
        {
            System.Threading.Interlocked.Increment(ref BareTopsKept);
            if (RoadNetworkGenerator.RoadsLog)
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(FormattableString.Invariant(
                $"[STAIRS] approach to {request.Centre}: laid with a bare stair at its top, no layout with a way off it ({refused})"));
        }
        return span;
    }

    /// <summary>Approaches laid with a bare stair at their top this generation, no layout with a finish fitting found
    /// (<see cref="PlanPlacedSiteApproach"/>).</summary>
    public static int BareTopsKept;

    /// <summary>Whether this thread's approach search refused a layout for its bare top (<see cref="NoWayOffItsTop"/>).</summary>
    [ThreadStatic] private static bool t_bareRefused;

    /// <summary>The generation's counters an approach's search adds to (<see cref="Count"/>).</summary>
    private enum ApproachCount { FeetFacedFreely, AimsBuried, ApproachLayoutsAcross, LaidBesideTheirAim, ApproachFacingReruns, ApproachesNotFacingIn, ApproachStartsAcross, ClimbedOn, TopFloors, Crests }

    private const int ApproachCounts = (int)ApproachCount.Crests + 1;

    /// <summary>This thread's counts held for the pass under way (<see cref="PlanPlacedSiteApproach"/>'s first), or null:
    /// counted at once.</summary>
    [ThreadStatic] private static int[]? t_held;

    /// <summary>One more of <paramref name="c"/>: held for the pass under way where one is (<see cref="t_held"/>), else
    /// added to the generation's counter.</summary>
    private static void Count(ApproachCount c)
    {
        if (t_held is int[] held) { held[(int)c]++; return; }
        System.Threading.Interlocked.Increment(ref Counter(c));
    }

    private static ref int Counter(ApproachCount c)
    {
        switch (c)
        {
            case ApproachCount.FeetFacedFreely: return ref FeetFacedFreely;
            case ApproachCount.AimsBuried: return ref AimsBuried;
            case ApproachCount.ApproachLayoutsAcross: return ref ApproachLayoutsAcross;
            case ApproachCount.LaidBesideTheirAim: return ref LaidBesideTheirAim;
            case ApproachCount.ApproachFacingReruns: return ref ApproachFacingReruns;
            case ApproachCount.ApproachesNotFacingIn: return ref ApproachesNotFacingIn;
            case ApproachCount.ApproachStartsAcross: return ref ApproachStartsAcross;
            case ApproachCount.ClimbedOn: return ref ClimbedOn;
            case ApproachCount.TopFloors: return ref TopFloors;
            default: return ref Crests;
        }
    }

    private static RoadStairSpan? Approach(SiteApproachRequest request, float yaw, IReadOnlyList<LevelOp> ops,
        Func<float, float, float> ground, Func<float, float, Heightmap.Biome> biome, int seed, out string why, float rockRadius,
        Func<Vector2, bool>? inFootprint, IReadOnlyList<SiteRock>? rocks, bool keepSmallRocks, float root,
        StairContinuation? onStairs, bool joinedAims, PlacedSiteShape? placed, StairKit? laidIn, bool faceInto, bool refuseBare)
    {
        why = "";
        // A placed place's shape stands for its flat: one landed on the level ground its smoothing leaves has none
        // (PlacedSiteShape.OnLevelGround).
        LevelOp? found = Flat(ops) ?? (placed != null ? PlacedSiteShape.DoorFlat : null);
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
        Vector2 front = shape.Front;
        float pit = shape.Pit;
        // Laid again not facing into the place (faceInto false, ApproachesNotFacingIn): its end held as the place is turned.
        float inward = StairLayout.Wrap(yaw + 180f);
        // margin: the search keeps each piece's centre this much further off, so a turn's fillers
        // (laid after it, up to about a metre and a half from a piece's centre) stay clear too.
        bool Allowed(Vector2 p, float margin) => shape.Allowed(p, margin, Width * 0.5f + margin);
        List<Vector2>? around = shape.Behind(roadEnd) ? Around(roadEnd, shape.Centre, front, shape.KeepOut) : null;
        // The level ground beside the mouth, as the road aims at (ideally the road or stairs come up to be level); the
        // point in front of the pit where none is found.
        var landing = shape.ConnectedLanding(ground, PlacedSiteShape.LevelGroundClearance);
        // The search keeps off the landing past its lip as the plan is refused there (PastTheLip): every piece's centre, and
        // its top's exit edge, half a piece further on.
        Func<Vector2, bool> withinLip = p => WithinLip(landing.Depth(p));
        Func<Vector2, bool> allowed = p => Allowed(p, FillerReach) && withinLip(p);
        // Aimed where the landing meets the slope, a top climbing to it stops wherever it steps onto the landing first
        // (StairLayout.Build's onLanding): the lip met sooner is as good.
        Func<Vector2, bool> onLanding = landing.Contains;
        var aims = new List<(string name, Vector2 target)>();
        // Aimed first where the landing's level ground joins the slope, then at the landings themselves, overhanging
        // ones last (PlacedSiteShape.Aims); joinedAims false (tests) aims at the level landings in front of the mouth only.
        if (joinedAims) aims.AddRange(shape.Aims(ground, roadEnd, PlacedSiteShape.LevelGroundClearance, AimTries, landing));
        else foreach (var (at, _) in landing.Landings) aims.Add(("level landing", landing.AtLip(at)));
        if (aims.Count == 0) aims.Add(("landing", shape.Landing));
        StairKit kit = laidIn ?? StairKit.ForLine(new List<Vector2> { roadEnd, aims[0].target }, biome);
        // The ring at the road's end (RoadEndRing): every layout from the road's end sets off from it, or arrives on it going
        // down from the road; none where the road ends on a staircase's top row and the approach carries on from it.
        var back = new List<Vector2>(request.RoadTail);
        back.Reverse();
        RoadEndRing? ring = onStairs.HasValue ? null : new RoadEndRing(roadEnd, request.Arriving, request.RoadHeight, RoadGoesOnPast(roadEnd, back));
        var tried = new List<string>();
        // Some aim searched for a layout held to face into the place: only then is the approach laid again not facing in. An
        // aim refused before any search (AimRefused, no walkable ground to it) is refused the same way not facing in.
        bool searched = false;
        // Whether held to face into the place made any difference: some layout's search turned something away for it
        // (StairPlan.HeldToFace), or some layout was reached and refused (laid again not facing in, its end is held as the
        // place is turned, and it may end elsewhere). Where neither, every layout searched again not facing in would be
        // searched as it was, and not reached again: it is not searched again (an approach to no landing searched every aim
        // twice for nothing).
        bool facingBore = false;
        // The steps every layout of the approach spent, every landing and start together (LastApproachSteps).
        int spent = 0;
        // Held to face into the place, the approach searches within one allowance it shares with its layout laid again not
        // facing in (FacingStalls): every layout of the facing pass together, whatever each could spend alone. A shelf's
        // approach that could face in from no landing searched each landing a second time over for nothing (Hildir_cave
        // -2304,1536: 99772 steps held to face in, then 5785 laid not facing in).
        bool facingPass = faceInto && laidIn == null;
        int facingAllowance = facingPass ? FacingStalls * request.Stall : int.MaxValue;
        int Leg() => request.Leg;
        int Total() => facingPass ? Math.Min(request.TotalExpansions, Math.Max(0, facingAllowance - spent)) : request.TotalExpansions;
        StairPlan Spent(StairPlan laid) { spent += laid.Expansions; facingBore |= laid.HeldToFace > 0; return laid; }
        // From a staircase's top row: carried on off its snap edge first, then, where no piece fits
        // that way (a steep face straight on from the row buried every heading: MountainCave02
        // -2560,1536), set off from the row's end
        // any way, at its height, with no sunk foot under the row.
        var starts = onStairs.HasValue ? new StairContinuation?[] { onStairs, null } : new StairContinuation?[] { null };
        // Each aim, and after one whose layout held to face in was refused for burial at it (BuriedAtItsAim), the points
        // beside it along the lip (BesideOnTheLip): never beside one of those, nor laid not facing in.
        foreach (var (from, queue) in starts.Select(f => (f, aims.ConvertAll(x => (x.name, x.target, beside: false)))))
        for (int a = 0; a < queue.Count; a++)
        {
            var (name, target, besideAnAim) = queue[a];
            if (spent >= facingAllowance) { tried.Add(FormattableString.Invariant($"{name}: the facing allowance spent ({facingAllowance} steps)")); break; }
            bool sunk = !onStairs.HasValue;
            float apron = ground(target.x, target.y);
            // What its last flight faces as it comes onto the landing (IntoThePlace).
            Vector2 into = IntoThePlace(shape);
            string aim = $"{name} ({target.x:F1}, {apron:F1}, {target.y:F1}){(onStairs.HasValue && !from.HasValue ? " from the row's end" : "")}";
            why = AimRefused(roadEnd, request.RoadHeight, target, apron, roadRefused: true, request.Reach);
            if (why != "") { tried.Add($"{aim}: {why}"); continue; }
            // A landing at the top is for a road to meet: only where the top is the road's end.
            // At the place's end the walker steps off onto its ground, and a floor carried on
            // past the aim ran into the rim.
            // Climbing to the ledge, it may finish anywhere along its lip (PastTheLip): whole-metre steps from the
            // road's height met no one point of a real ledge (frost cave at 1408,-5888:
            // within 2.3 m of the rim at 0.58 m off its height, refused). Only a climb: laid
            // down from a road above, the layout starts at the ledge.
            bool area = apron > request.RoadHeight;
            bool raisedArrival = area && pit <= 0f;
            string SurfaceRefusal(Vector3 sample)
            {
                if (raisedArrival && !RaisedLandingSurfaceAllowed(sample, landing))
                    return "clips the raised landing";
                return "";
            }
            bool SurfaceAllowed(Vector3 sample) => SurfaceRefusal(sample) == "";
            Func<Vector3, bool>? surfaceAllowed = raisedArrival ? SurfaceAllowed : null;
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
                    Total(), request.Stall, 0, sunkFoot: sunk, maxBuried: StairCover.ApproachSearch, allowed: allowed, exitAllowed: withinLip, surfaceAllowed: surfaceAllowed, after: from, ring: ring));
                plan = up;
                if (up.Reached)
                {
                    Vector2 top = new Vector2(up.End.x, up.End.z);
                    StairPlan down = Spent(StairLayout.Build(top, target, ground, Width, kit, Leg(), up.EndHeading, faceInto ? null : inward, null,
                        Total(), request.Stall, 0, sunkFoot: true, maxBuried: StairCover.ApproachSearch, allowed: allowed, exitAllowed: withinLip, onLanding: onLanding, surfaceAllowed: surfaceAllowed,
                        intoPlace: faceInto ? into : null));
                    // Held to face the place at the landing and to meet the first head-on, the
                    // second fitted nowhere beside the place's rock (5632,5312 and
                    // -9088,-512: no piece fits down from the crest): then as it falls.
                    if (!down.Reached)
                        down = Spent(StairLayout.Build(top, target, ground, Width, kit, Leg(), null, null, null,
                            Total(), request.Stall, 0, sunkFoot: true, maxBuried: StairCover.ApproachSearch, allowed: allowed, exitAllowed: withinLip, onLanding: onLanding, surfaceAllowed: surfaceAllowed));
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
                    Total(), request.Stall, TopLandings, sunkFoot: false, maxBuried: StairCover.ApproachSearch, allowed: allowed, exitAllowed: withinLip, surfaceAllowed: surfaceAllowed, after: from, ring: ring));
                plan = down;
                if (down.Reached)
                {
                    float walking = down.Reversed ? StairLayout.Wrap(down.StartHeading + 180f) : down.EndHeading;
                    StairPlan up = Spent(StairLayout.Build(floor, target, ground, Width, kit, Leg(), walking, faceInto ? null : inward, null,
                        Total(), request.Stall, 0, sunkFoot: false, maxBuried: StairCover.ApproachSearch,
                        arriveWithin: LedgeReach, allowed: allowed, exitAllowed: withinLip, onLanding: onLanding, surfaceAllowed: surfaceAllowed, intoPlace: faceInto ? into : null));
                    if (!up.Reached)
                        up = Spent(StairLayout.Build(floor, target, ground, Width, kit, Leg(), null, null, null,
                            Total(), request.Stall, 0, sunkFoot: false, maxBuried: StairCover.ApproachSearch,
                            arriveWithin: LedgeReach, allowed: allowed, exitAllowed: withinLip, onLanding: onLanding, surfaceAllowed: surfaceAllowed));
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
                float? endHeld = faceInto || flats ? null : inward;
                Vector2? intoHeld = faceInto ? into : null;
                plan = Spent(StairLayout.Build(roadEnd, goal, ground, Width, kit, Leg(), around == null ? null : request.Arriving,
                    endHeld, corridor, Total(), request.Stall, apron < request.RoadHeight && !flats ? TopLandings : 0, sunkFoot: sunk,
                    maxBuried: StairCover.ApproachSearch, arriveWithin: area || flats ? LedgeReach : null, allowed: allowed, exitAllowed: withinLip, onLanding: onLanding, surfaceAllowed: surfaceAllowed, after: from,
                    facing: from.HasValue || ground(goal.x, goal.y) < ground(roadEnd.x, roadEnd.y) ? null : request.Arriving,
                    ring: ring, intoPlace: intoHeld));
                if (!plan.Reached && around == null && !from.HasValue)
                {
                    Count(ApproachCount.FeetFacedFreely);
                    string faced = plan.Reason ?? "";
                    plan = Spent(StairLayout.Build(roadEnd, goal, ground, Width, kit, Leg(), null,
                        endHeld, corridor, Total(), request.Stall, apron < request.RoadHeight && !flats ? TopLandings : 0, sunkFoot: sunk,
                        maxBuried: StairCover.ApproachSearch, arriveWithin: area || flats ? LedgeReach : null, allowed: allowed, exitAllowed: withinLip, onLanding: onLanding, surfaceAllowed: surfaceAllowed, after: from,
                        ring: ring, intoPlace: intoHeld));
                    if (RoadNetworkGenerator.RoadsLog)
                        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(FormattableString.Invariant(
                            $"[STAIRS] approach to {request.Centre} from ({roadEnd.x:F1}, {roadEnd.y:F1}), road arriving {request.Arriving:F0}: no layout facing it ({faced}); laid any way: {(plan.Reached ? "reached" : "not reached")}, setting off {plan.StartHeading:F1}"));
                }
            }
            string Refused(StairPlan laid, bool finished = false)
            {
                // Earthwork is made outside every footprint, as for a road's staircase; inside one the
                // terrain writer leaves the ground alone, so only there must the plan need none. Its
                // foot, at the road's end outside the footprint, was laid as floors flush with or
                // under the snow, and the staircase seemed not to reach the road. Read as the search reads
                // each piece (Cover: every walked piece's whole surface, its edges included), not at its cut rows.
                float coverInside = Cover(laid, ground, inFootprint);
                var walk = raisedArrival ? RaisedLandingWalk(laid, landing) : (clear: true, arrival: false);
                string refused = !laid.Reached ? "not reached"
                    : coverInside > StairCover.ApproachRefusal ? "needs earthwork in the footprint"
                    : !walk.clear ? "clips the raised landing"
                    : PastTheLip(laid, landing.Depth) ? "too far onto the flat"
                    // Set off back along its road's end, on it (OverRoad): the next landing or start is tried instead.
                    : onStairs == null && request.RoadTail.Count >= 2
                      && OverRoad(laid, request.RoadTail, request.RoadTail.Count - 1, request.RoadTail.Count - 1, ground) ? OnItsRoad
                    // Into the ring at its road's end (RoadEndRing): never laid, the next landing or start is tried.
                    : ring is RoadEndRing r && r.Intrudes(laid, RoadEndRing.MetAtTop(laid)) ? AcrossItsRoad
                    : faceInto && sunk && !from.HasValue && SetsOffOnAPad(laid) ? HeldOnAPad
                    // Arriving as another way into the place does (PlaceWaysIn, read in SiteConflict too): the next aim or
                    // start is tried, rather than the same way in laid twice from every start.
                    : laid.Reached && DuplicatesAWayIn(laid, request.Centre) ? BesideAnApproach
                    : "";
                // The search keeps each piece's centre allowed; a turn's fillers come after it.
                if (refused == "" && laid.Pieces.Exists(pc => pc.Kind != StairPieceKind.Support && !Allowed(new Vector2(pc.Position.x, pc.Position.z), 0f)))
                    refused = "in the place's rock";
                if (refused == "" && finished && Vector2.Distance(StairLayout.Terminal(laid) is { } exit ? new Vector2(exit.Edge.x,exit.Edge.z) : new Vector2(laid.End.x,laid.End.z), target) > LedgeReach + FinishReach + 0.01f)
                    refused = "past the landing";
                if (refused == "" && finished && raisedArrival && !walk.arrival)
                    refused = "does not meet the raised landing's lip";
                // Held to face into the place, a top may climb on past the landing onto the ground beside it (a shelf at
                // Hildir_cave -2304,1536: the top stood 1.3 m over it): its top, before it is finished over the ledge, ends
                // level with a landing, or the next aim is tried (and the approach laid as before where none faces in).
                if (refused == "" && finished && faceInto && landing.Landings.Count > 0
                    && !landing.Landings.Exists(l => Mathf.Abs(l.height - laid.End.y) <= RoadNetworkGenerator.ArrivalLevel))
                    refused = NotLevelWithTheLanding;
                return refused;
            }
            searched = true;
            why = Refused(plan);
            // Laid straight from the road's end (no crest or valley between, whose second staircase is laid again as it
            // falls where it does not face in).
            if (why != "" && faceInto && !besideAnAim && turn == null && BuriedAtItsAim(plan))
            {
                if (laidIn == null) Count(ApproachCount.AimsBuried);
                int at = a + 1;
                foreach (var (along, point) in BesideOnTheLip(landing, target))
                    if (!queue.Exists(q => Vector2.Distance(q.target, point) < LipStep / 2f))
                        queue.Insert(at++, (FormattableString.Invariant($"{name} {along:+0.0;-0.0} m along the lip"), point, true));
            }
            if (why != "")
            {
                facingBore |= plan.Reached;
                if (why == AcrossItsRoad) Count(ApproachCount.ApproachLayoutsAcross);
                tried.Add($"{aim}: {why}{(plan.Reached ? "" : $" ({plan.Reason})")}");
                continue;
            }
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
                // Aimed where the landing meets the slope, it ends where it first steps onto the landing level: nothing past.
                StairPlan whole = plan.Copy(deepPieces: true);
                if (StopAtTheLip(plan, landing, Width, kit) && Refused(plan) != "") plan.Restore(whole);
                var misfits = new List<string>();
                bool Fits(StairPlan p)
                {
                    string m = Refused(p, finished: true);
                    if (m != "") { misfits.Add(m); StairLayout.Trace?.Add("refusal: " + m); }
                    return m == "";
                }
                // Logged at the top they were read at: the finish may move the top (a stair climbed on and kept), and printed
                // at the moved top, a short top read 0.37 m under the ground past it read as if the ground were a metre higher (Hildir's
                // shelf at -2880,-5440).
                Vector3 readAt = plan.End;
                var (lipLeft, lipRight) = RoadNetworkGenerator.RoadsLog
                    ? StairEnds.Drops(new Vector2(readAt.x, readAt.z), StairLayout.Forward(plan.EndHeading), readAt.y, Width, ground) : (0f, 0f);
                StairLayout.LastStepBack = null;
                StairLayout.TopFinish finish = FinishOverLedge(plan, ground, Width, Fits);
                // Every top has a way off it (StairEnds): no finish fitting, the layout is refused and the next aim or start
                // is tried, never laid a bare stair (three approach tops in game: 5706.4,3591.7, -1870.9,7095.9 and
                // -9138.9,1981.2, each a bare stair over ground level with it, every floor refused by the approach's own rules).
                bool bare = refuseBare && finish == StairLayout.TopFinish.None && TopEnd(plan, ground) is { Verdict: not StairEnds.Verdict.Ok };
                if (RoadNetworkGenerator.RoadsLog)
                    ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(FormattableString.Invariant(
                        $"[STAIRS] approach to {request.Centre}: top at ({readAt.x:F1}, {readAt.y:F2}, {readAt.z:F1}), drop {lipLeft:+0.00;-0.00}/{lipRight:+0.00;-0.00}, finished {finish}{(plan.End.x != readAt.x || plan.End.y != readAt.y || plan.End.z != readAt.z ? $" (top now at ({plan.End.x:F1}, {plan.End.y:F2}, {plan.End.z:F1}))" : "")}{(misfits.Count > 0 ? "; refused: " + string.Join(", ", misfits) : "")}{(finish == StairLayout.TopFinish.None && StairLayout.LastStepBack is string stepBack ? "; step back: " + stepBack : "")}{(bare ? "; " + NoWayOffItsTop + ", the next tried" : "")}"));
                if (bare) { t_bareRefused = true; facingBore = true; tried.Add($"{aim}: {NoWayOffItsTop}{(misfits.Count > 0 ? " (" + string.Join(", ", misfits.Distinct()) + ")" : "")}"); continue; }
            }
            else
            {
                // Coming down onto the landing, its foot is where a player walks off onto the place's ground: a floor laid out
                // onto it on the foot's side facing the place where the step off there is not level (StairLayout.AddPlaceFloor,
                // a top floor's rule), kept only where the plan still stands so.
                plan.PlaceMeet = StairLayout.AddPlaceFloor(plan, ground, target, into, p => Refused(p) == "");
                if (plan.PlaceMeet is Vector2 meet && RoadNetworkGenerator.RoadsLog)
                    ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(FormattableString.Invariant(
                        $"[STAIRS] approach to {request.Centre}: a floor laid out onto the place's ground from its foot at ({target.x:F1}, {target.y:F1}), met at ({meet.x:F1}, {meet.y:F1})"));
            }
            why = Refused(plan, finished: topAtPlace);
            if (why != "") { facingBore = true; tried.Add($"{aim}: {why}"); continue; }
            if (laidIn == null && StairKit.ForPlan(plan, biome) is var stood && stood != kit
                && Approach(request, yaw, ops, ground, biome, seed, out _, rockRadius, inFootprint, rocks, keepSmallRocks, root,
                    onStairs, joinedAims, placed, stood, faceInto, refuseBare) is RoadStairSpan inItsKit)
            {
                LastApproachSteps += spent;
                return inItsKit;
            }
            LastApproachSteps = spent;
            if (besideAnAim && laidIn == null) Count(ApproachCount.LaidBesideTheirAim);
            return Span(plan, roadEnd, request.RoadHeight, target, apron, kit, request.RoadTail.Count - 1);
        }
        why = string.Join("; ", tried);
        LastApproachSteps = spent;
        // Cut short by its allowance: whether the hold bore on what it did not search is not known.
        if (spent >= facingAllowance) facingBore = true;
        // None faces into the place from any aim: laid as before, its end held as the place is turned, rather than none laid.
        // Only where some aim was searched (searched) and the hold bore on it (facingBore): a refusal after a search may come
        // of the facing hold (a tower's flight held to face in missed the raised landing's lip, and laid not facing in met
        // it), but none before one does, nor one the hold never touched.
        if (faceInto && laidIn == null && searched && facingBore)
        {
            Count(ApproachCount.ApproachFacingReruns);
            RoadStairSpan? turned = Approach(request, yaw, ops, ground, biome, seed, out string notFacing, rockRadius, inFootprint, rocks,
                keepSmallRocks, root, onStairs, joinedAims, placed, null, false, refuseBare);
            LastApproachSteps += spent;
            if (turned != null) Count(ApproachCount.ApproachesNotFacingIn);
            else why = notFacing;
            return turned;
        }
        if (laidIn == null && why.Contains(AcrossItsRoad))
        {
            Count(ApproachCount.ApproachStartsAcross);
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(FormattableString.Invariant(
                $"[STAIRS] across its road: approach start at ({roadEnd.x:F1}, {roadEnd.y:F1}) to {request.Centre}, arriving {request.Arriving:F0}"));
        }
        return null;
    }

    /// <summary>
    /// How an approach staircase arrives at a place's landing (one owner): its last flight faces into the place, toward its
    /// centre, climbing onto the landing or coming down onto it (StairLayout.ArrivalHeld holds the flight's last stairs, and
    /// every floor walked after them, within 90 degrees of it, as seen from where it arrives: from along a tangent to the place's
    /// radius up to straight in, never away, StairLayout.ArrivalSteps; 45 before 3 Nov). Held before to the place's turn
    /// (its yaw turned round), and at its last piece only: that faces into a cave's mouth but not into a tower from its side.
    /// The staircase down to the Mountain tower at -1917,6724 (yaw 247.5) came round the tower and onto the landing's lip to
    /// its north west, and its last flight ran along the tower's wall and ended into it (it would be better if it turned
    /// towards the place vs into the wall).
    /// </summary>
    internal static Vector2 IntoThePlace(PlacedSiteShape shape) => IntoThePlace(shape.Centre);

    /// <summary>The same for a place not yet placed, coming onto the apron round its <paramref name="centre"/>
    /// (<see cref="LaySiteApproach"/>).</summary>
    internal static Vector2 IntoThePlace(Vector2 centre) => centre;

    /// <summary>An approach held to face into its place (<see cref="IntoThePlace"/>) is not the worse at its road's end for it:
    /// one set off from the road on a pad of floors (no stair out of the road reached the landing facing in, StairLayout.Build)
    /// is refused, and laid as before where none faces in (<see cref="ApproachesNotFacingIn"/>). Its floors stood flush with
    /// the road and were dropped as buried once the ground settled (PlacedLandingStairTests'
    /// TheValidWalkSurvivesSettlementFirstBuildReloadAndZoneReplay).</summary>
    internal const string HeldOnAPad = "held: set off on a pad";

    /// <summary>An approach whose top no finish fits (<see cref="FinishOverLedge"/>) and has no way off as laid (StairEnds: a bare
    /// stair, or floors with no edge stepped off).</summary>
    internal const string NoWayOffItsTop = "no way off its top";

    /// <summary>The top end of <paramref name="plan"/> as the end reader reads it on <paramref name="ground"/>
    /// (StairEnds.Read), or null where it has no walked piece.</summary>
    internal static StairEnds.End? TopEnd(StairPlan plan, Func<float, float, float> ground)
    {
        StairPiece? foot = plan.Pieces.Find(p => p.Kind != StairPieceKind.Support);
        if (foot == null) return null;
        StairLayout.TerminalExit? exit = StairLayout.Terminal(plan);
        Vector2 top = exit != null ? new Vector2(exit.Edge.x, exit.Edge.z) : new Vector2(plan.End.x, plan.End.z);
        var run = StairRun.FromPlan(new Vector2(foot.Position.x, foot.Position.z), top, Width, plan);
        return StairEnds.Read(run, ground).Find(e => e.Top);
    }

    /// <summary>An approach held to face into its place whose top, before it is finished over the ledge, ends level with no landing.</summary>
    internal const string NotLevelWithTheLanding = "not level with the landing";

    /// <summary>How many stalls' worth of search steps (SiteApproachRequest.Stall each) an approach held to face into its place
    /// may spend, every landing and start together, before it is laid again not facing in (PlanPlacedSiteApproach): a
    /// landing the hold turns away is searched until it stalls, so about this many landings are searched facing in. On the
    /// tests' grounds the approaches laid facing in spent at most 8223 steps but one (24108, its fourth landing).</summary>
    internal const int FacingStalls = 4;

    /// <summary>
    /// Whether a layout held to face into its place (<see cref="IntoThePlace"/>) was refused for burial at the point it was
    /// aimed at on the landing: laid up from there to a road above (Reversed), no first piece off it fitted and some, facing
    /// in, stood deeper in the ground than the search allows (StairPlan.FitOffRoot, BuriedOffRoot). The saved tower's road's
    /// end 26 m out at 337.5 degrees (TowerPlacedFirstTests): from its first aims, the lip at the foot of the bank north west
    /// of the landing (-4803,1743) and the level landing (-4796,1733), every first stair facing in stood in the bank, and the
    /// approach was laid not facing in, 65 degrees off; 2 m along the lip from the level landing one faces in.
    /// </summary>
    internal static bool BuriedAtItsAim(StairPlan plan) => !plan.Reached && plan.Reversed && plan.FitOffRoot == 0 && plan.BuriedOffRoot > 0;

    /// <summary>
    /// How far apart the points beside a buried aim are tried (<see cref="BesideOnTheLip"/>): 0.6 of a piece's width
    /// (StairLayout.PieceRun), so each try sets the first stair's 2 m on ground more than half new and no strip of the bank a
    /// piece wide is stepped over between two tries.
    /// </summary>
    public const float LipStep = 0.6f * StairLayout.PieceRun;

    /// <summary>
    /// How many steps (<see cref="LipStep"/>) from a buried aim the points beside it are tried, either way: 1.2, 2.4 and
    /// 3.6 m, out past the landings aimed at apart (PlacedSiteShape.LandingApart, 3 m), so beyond that the next aim's
    /// neighbourhood is the next aim's own, and well within the reach a ledge's arrival may finish from its aim
    /// (<see cref="LedgeReach"/>). Tried within the approach's facing allowance (<see cref="FacingStalls"/>), so the approach
    /// costs no more for them.
    /// </summary>
    public const int LipSteps = 3;

    /// <summary>
    /// The points beside <paramref name="aim"/> on the landing's lip, nearest first, at most two per step (<see cref="LipSteps"/>):
    /// each <see cref="LipStep"/> or more from the aim and from each other, and at most LipSteps steps from the aim (and
    /// half a step more, the lip's cells standing on a whole-metre lattice). They are the aim moved along the lip's run
    /// (square to the outward of the lip cells within a metre and a half) where that stays on the landing within its lip
    /// (<see cref="WithinLip"/>), and the lip's own cells: along the run alone, the tower's lip cell at -4803,1743, at the end
    /// of a spur of the landing with the lip's row 3 m south of it, found nothing beside it. <c>along</c>: the distance,
    /// signed by the side of the run.
    /// </summary>
    internal static List<(float along, Vector2 at)> BesideOnTheLip(PlacedSiteShape.LandingRegion landing, Vector2 aim)
    {
        var beside = new List<(float along, Vector2 at)>();
        if (landing.Lip.Count == 0) return beside;
        Vector2 outward = new Vector2(0f, 0f);
        foreach (var (cell, o) in landing.Lip)
            if (Vector2.Distance(cell, aim) <= 1.5f) outward += o;
        if (outward.sqrMagnitude < 1e-4f) outward = landing.Lip.OrderBy(l => Vector2.Distance(l.at, aim)).First().outward;
        Vector2 run = outward.sqrMagnitude < 1e-4f ? new Vector2(1f, 0f) : new Vector2(-outward.y, outward.x).normalized;
        float most = LipSteps * LipStep + LipStep / 2f;
        var candidates = new List<(float along, Vector2 at)>();
        for (int k = 1; k <= LipSteps; k++)
            foreach (float side in new[] { 1f, -1f })
            {
                Vector2 p = aim + run * (side * k * LipStep);
                if (landing.Contains(p) && WithinLip(landing.Depth(p))) candidates.Add((side * k * LipStep, p));
            }
        foreach (var (cell, _) in landing.Lip)
        {
            float d = Vector2.Distance(cell, aim);
            if (d >= LipStep - 0.01f && d <= most) candidates.Add((Vector2.Dot(cell - aim, run) < 0f ? -d : d, cell));
        }
        foreach (var c in candidates.OrderBy(c => Mathf.Abs(c.along)).ThenBy(c => c.along < 0f).ThenBy(c => c.at.x).ThenBy(c => c.at.y))
        {
            if (beside.Exists(b => Vector2.Distance(b.at, c.at) < LipStep - 0.01f)) continue;
            beside.Add(c);
            if (beside.Count == 2 * LipSteps) break;
        }
        return beside;
    }

    /// <summary>Aims this generation whose layout held to face into the place was refused for burial at them
    /// (<see cref="BuriedAtItsAim"/>), and the points beside them tried (<see cref="BesideOnTheLip"/>, <see cref="ApronBeside"/>);
    /// <see cref="LaidBesideTheirAim"/> counts the approaches laid from one of those points.</summary>
    public static int AimsBuried, LaidBesideTheirAim;

    /// <summary>Approaches searched again not facing into their place after no layout facing in was laid, laid or not;
    /// <see cref="ApproachesNotFacingIn"/> counts those laid.</summary>
    public static int ApproachFacingReruns;

    /// <summary>Whether a staircase laid up from its road's end sets off on a pad of floors (<see cref="HeldOnAPad"/>).</summary>
    internal static bool SetsOffOnAPad(StairPlan plan) => !plan.Reversed && plan.StartsOnFloor;

    /// <summary>Approaches laid this generation whose last flight could not face into the place from any aim
    /// (<see cref="IntoThePlace"/>), laid as before: the end held as the place is turned, or free.</summary>
    public static int ApproachesNotFacingIn;

    /// <summary>
    /// How deep the ground stands over an approach's walked pieces where no earthwork uncovers them (one owner, with the
    /// search's reading of each piece, StairLayout.Cover: the whole walking surface, its edges included), point by point over
    /// <paramref name="ground"/>: every piece but its supports, the steps sunk under its feet (StairPiece.Sunk; a crest's second
    /// staircase has its own, at the landing) and a top stair stepping down onto
    /// what it meets (StairLayout.Terminal, descending): both buried on purpose, a foot set into the ground it comes off or
    /// onto (its high edge is judged by RaisedLandingWalk).
    /// </summary>
    internal static IEnumerable<(StairPiece piece, Vector3 at, float depth)> CoverSamples(StairPlan plan, Func<float, float, float> ground)
    {
        StairLayout.TerminalExit? exit = StairLayout.Terminal(plan);
        for (int i = plan.SunkPieces; i < plan.Pieces.Count; i++)
        {
            StairPiece p = plan.Pieces[i];
            if (p.Kind == StairPieceKind.Support || p.Sunk || exit != null && exit.Descending && exit.Pieces.Contains(p)) continue;
            foreach (var (at, depth) in StairLayout.Cover(StairLayout.WalkingSurface(p), ground)) yield return (p, at, depth);
        }
    }

    /// <summary>The deepest <see cref="CoverSamples"/> reads where <paramref name="where"/> holds (a placed place's footprint,
    /// which the terrain writer leaves alone; everywhere where null), over the ground the search read: across the road's end
    /// that is the road's (RoadEndRing.RoadsGround), as the road's earthwork levels it.</summary>
    internal static float Cover(StairPlan plan, Func<float, float, float> ground, Func<Vector2, bool>? where = null)
    {
        float deepest = 0f;
        if (plan.Ring is RoadEndRing ring) ground = ring.RoadsGround(ground);
        foreach (var (_, at, depth) in CoverSamples(plan, ground))
            if (depth > deepest && (where == null || where(new Vector2(at.x, at.z)))) deepest = depth;
        return deepest;
    }

    // The placed-approach validity owner, shared by final finish acceptance and precise regression controls.
    internal static (bool clear, bool arrival) RaisedLandingWalk(StairPlan plan, PlacedSiteShape.LandingRegion landing)
    {
        bool clear=true;
        var exit=StairLayout.Terminal(plan);
        Vector2 edge=exit==null ? default : new Vector2(exit.Edge.x,exit.Edge.z);
        bool near=exit!=null && exit.ForwardEdge.Any(s=>landing.Near(new Vector2(s.x,s.z),StairLayout.SurfaceSpacing));
        bool contact=exit!=null && exit.Descending && near
            && exit.Surface.Where(s=>s.y>=exit.High-0.01f).All(s=>RaisedLandingSurfaceAllowed(s,landing))
            && exit.Grounded((x,z)=>landing.Height(new Vector2(x,z)))
            && Corners(edge-exit.Direction*exit.Depth,exit.Direction,exit.Width).All(p=>landing.Height(p)<=exit.High+0.01f)
            && StairEnds.MeetsLowEdge(edge-exit.Direction*exit.Depth,exit.Direction,exit.Edge.y,exit.Width,(x,z)=>landing.Height(new Vector2(x,z)));
        for(int i=plan.SunkPieces;i<plan.Pieces.Count;i++)
        {
            StairPiece piece=plan.Pieces[i];
            if(contact && exit!.Pieces.Contains(piece))continue;
            if(StairLayout.WalkingSurface(piece).Any(s=>!RaisedLandingSurfaceAllowed(s,landing)))clear=false;
        }
        // A floor top, and a stair down (the contact above), at its own far corners (the finish the rule lays where a floor off
        // the stair before it would stand), a stair's off its edge.
        Vector2 from=exit!=null && exit.Pieces[0].Kind==StairPieceKind.Landing ? edge-exit.Direction*exit.Depth : edge;
        bool arrival=exit!=null && near && (contact || !exit.Descending
            && StairEnds.Lands(from,exit.Direction,exit.Edge.y,exit.Width,(x,z)=>landing.Height(new Vector2(x,z))));
        return(clear,arrival);
    }

    /// <summary>The two far corners of the floor off an exit edge (StairEnds.Corners, the one place the ground off it is read).</summary>
    private static Vector2[] Corners(Vector2 edge, Vector2 dir, float width)
    {
        var (l, r) = StairEnds.Corners(edge, dir, width);
        return new[] { l, r };
    }

    internal static bool RaisedLandingSurfaceAllowed(Vector3 sample, PlacedSiteShape.LandingRegion landing)
    {
        Vector2 at = new Vector2(sample.x, sample.z);
        return !landing.Contains(at) || landing.Height(at) - sample.y <= 0.01f;
    }

    /// <summary>
    /// An approach climbing to a place's landing ends on the first walked piece, from its foot, whose step off its far edge
    /// is onto the landing level with it, within the lip (<see cref="WithinLip"/>): the far corners of the floor off it (a
    /// floor's own; StairEnds.Corners, the one reader) on the landing region (<see cref="PlacedSiteShape.LandingRegion"/>), and a floor
    /// there landing on it by the end rule (StairEnds.Lands, as a top floor is laid). Every walked piece laid on past it at or over its height
    /// goes (StairLayout.TrimAbove), its top set there; the top is finished from there (<see cref="FinishOverLedge"/>). In
    /// game: a cave's approach whose top stair at 1600.2,157.2,6920.2 stood level with the landing went on, turned onto a
    /// floor and stepped back down off it toward the cave's mouth; the lip is where its top stair ends, one floor laid on
    /// from it level with the ground and nothing past it. Returns whether it cut anything.
    /// </summary>
    internal static bool StopAtTheLip(StairPlan plan, PlacedSiteShape.LandingRegion landing, float width, StairKit kit)
    {
        if (!plan.Reached || plan.Reversed) return false;
        int last = plan.Pieces.FindLastIndex(p => p.Kind is StairPieceKind.Stair or StairPieceKind.Landing);
        for (int i = plan.SunkPieces; i < last; i++)
        {
            StairPiece piece = plan.Pieces[i];
            if (piece.Kind is not (StairPieceKind.Stair or StairPieceKind.Landing)) continue;
            var (edge, heading, surface) = StairLayout.ExitOf(piece);
            Vector2 dir = StairLayout.Forward(heading);
            if (!WithinLip(landing.Depth(edge))) continue;
            // A stair by the floor off it, a floor by its own far corners.
            Vector2 from = piece.Kind == StairPieceKind.Landing ? edge - dir * StairLayout.Footprint(piece).depth : edge;
            if (!Corners(from, dir, width).All(landing.Contains)
                || !StairEnds.Lands(from, dir, surface, width, (x, z) => landing.Height(new Vector2(x, z)))) continue;
            if (StairLayout.TrimAbove(plan, i, false, kit) == 0) return false;
            plan.End = new Vector3(edge.x, surface, edge.y);
            plan.EndHeading = heading;
            return true;
        }
        return false;
    }

    /// <summary>Staircase tops finished with a crest this generation (StairLayout.FinishTop: climbed on, then a stair down).</summary>
    public static int Crests;

    /// <summary>Staircase tops carried on up more stairs this generation (<see cref="StairLayout.ClimbOn"/>).</summary>
    public static int ClimbedOn;

    /// <summary>
    /// A staircase's top over the lip in front of a place's mouth steps down onto it, without the top at the
    /// lip's exact height (one stair laid back down from the top floors lets a player climb onto the lip without
    /// needing exact heights): up, its top 2x2 landing, then
    /// down; back to back, the landing made a step down, only where there is no room for the landing (a back-to-back
    /// stair also works, but it looks better with a 2x2 between them, though that is not
    /// required). At most one reversed stair: where that cannot reach the ground, another arrival must be tried.
    /// Every tentative finish passes <paramref name="fits"/> or restores the complete plan.
    /// </summary>
    internal static StairLayout.TopFinish FinishOverLedge(StairPlan plan, Func<float, float, float> ground, float width, Func<StairPlan, bool> fits)
    {
        // A terminal down row already used the single reverse finish; it cannot acquire another one.
        bool traced = RoadNetworkGenerator.RoadsLog;
        Vector3 readAt = plan.End;
        if (traced) StairLayout.Trace = new List<string>();
        try
        {
        if (StairLayout.Terminal(plan)?.Descending == true) { StairLayout.Trace?.Add("None: the terminal row already descends"); return StairLayout.TopFinish.None; }
        // The one owner of a top's finish (StairLayout.FinishTop), within an approach's cover and kept out of the place's pit
        // and rock, onto ground it may step down onto.
        StairLayout.TopFinish finish = StairLayout.FinishTop(plan, ground, width, StairCover.ApproachRefusal, fits, out bool climbed, down: true);
        if (climbed) Count(ApproachCount.ClimbedOn);
        if (finish is StairLayout.TopFinish.TopFloor or StairLayout.TopFinish.StepBack) Count(ApproachCount.TopFloors);
        else if (finish == StairLayout.TopFinish.Crest) Count(ApproachCount.Crests);
        return finish;
        }
        finally
        {
            if (traced && StairLayout.Trace is { } lines)
            {
                StairLayout.Trace = null;
                foreach (string line in lines)
                    ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(FormattableString.Invariant(
                        $"[STAIRS] finish trace at ({readAt.x:F2}, {readAt.z:F2}): {line}"));
            }
        }
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
