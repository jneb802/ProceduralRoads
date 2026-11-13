using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Approach staircases laid with the roads (one owner): from a road's end at a place up (or down) to the flat apron the place
/// levels round its centre, climbing and coming down again over a crest or a valley between, crossing a dip within the walkway cap level, and
/// stopping where the ground to the apron is walkable.
/// </summary>
public static partial class RoadStairRuns
{
    /// <summary>A site's flat ground this far or less from the road's end is in reach of an approach staircase.</summary>
    public const float SiteReach = 40f;

    /// <summary>It is aimed this far inside the edge of the site's flat ground.</summary>
    public const float ApronInset = 1f;

    /// <summary>How far onto a place's flat an approach staircase may come (<see cref="PastTheLip"/>): a piece.</summary>
    public const float LipReach = StairLayout.PieceRun;

    /// <summary>The road's end must stand this close to the ground the staircase starts from.</summary>
    public const float SiteFootTolerance = 0.5f;

    /// <summary>What <see cref="LaySiteApproach"/> answers for a flat the place's turn decides: only known once the place is
    /// placed. Such a place is placed before its road and its way onto its landing laid on its real ground
    /// (<see cref="PlanPlacedSiteApproach"/>); one not placed before its roads gets no road
    /// (<see cref="PlacedSites.NotPlaced"/>), so no road ends here at it.</summary>
    public const string NotPlacedBeforeItsRoad = "flat off its centre: not placed before its road";

    /// <summary>What <see cref="LaySiteApproach"/> answers for a place whose flat is round its centre but which levels off it
    /// too: the ground round its flat turns with it, so is only known once it is placed. Where approaches are laid (the
    /// Mountains, a boss's entrance) such a place, turned, is placed first or gets no road, as
    /// <see cref="NotPlacedBeforeItsRoad"/>'s does; one the game does not turn keeps its road as it ended
    /// (RoadNetworkGenerator.EndNotLevel judges it).</summary>
    public const string LevelsOffItsCentre = "levels off its centre: placed before its road, or its ground not known";

    /// <summary>
    /// A staircase from a road's end at a place up to the place's flat ground. A road stops
    /// outside a place's whole terrain footprint (at a frost cave 30 m from its
    /// centre), and nothing builds the last stretch, which is often too steep
    /// for road anyway. Where the place levels a single flat round its centre
    /// (an apron), the road's end is not level with it (more than
    /// <see cref="StairLayout.StairFitsFrom"/> above or below it; 2 m before the end-level rule) and within <see cref="SiteReach"/> of it, this lays a
    /// staircase from the road's end to just inside the apron's edge on the
    /// side the road comes from, over the ground the place is expected to leave
    /// (<see cref="PlacedSites.ExpectedGround"/>). It may cross the
    /// place's own footprint, but come no further onto the apron than its lip (<see cref="PastTheLip"/>), and every other
    /// check of a road's staircase applies. The terrain writer leaves a place's
    /// footprint alone, so its cuts are never made: it is laid out with no
    /// flight buried deeper than <see cref="StairCover.ApproachSearch"/> and
    /// refused where anything is buried deeper than <see cref="StairCover.ApproachRefusal"/>.
    ///
    /// A flat off the centre lies wherever the place is turned, which is not
    /// known until the place is placed: for a place that turns to face downhill
    /// (a frost cave's floor dug behind its mouth) the answer is
    /// <see cref="NotPlacedBeforeItsRoad"/>; a place placed first gets its
    /// staircase from the ground the game made (<see cref="PlanPlacedSiteApproach"/>), and one not placed before its roads no road
    /// (<see cref="PlacedSites.NotPlaced"/>).
    /// So does a place with its flat round its centre and any other levelling off it
    /// (<see cref="LevelsOffItsCentre"/>): only levelling round the centre is the same
    /// whichever way it is turned, so only there is the ground it is expected to leave exact.
    /// Null, with <paramref name="why"/>, where none is laid now; the road is then as it was.
    /// </summary>
    public static RoadStairSpan? LaySiteApproach(Vector2 roadEnd, float roadHeight, float arriving, Vector2 centre,
        float centreGround, IReadOnlyList<LevelOp>? ops, bool facesDownhill, List<Vector2> path, int endIndex,
        IReadOnlyList<RoadStairSpan> stairs, WorldGenerator world, out string why)
    {
        why = "";
        if (ops == null || ops.Count == 0) { why = "no levelling known"; return null; }
        if (!LocationLevelling.PlatformHeight(centreGround, ops).HasValue) { why = "no single flat level"; return null; }
        LevelOp? flat = Flat(ops);
        if (flat == null) { why = "no flat ground"; return null; }
        if (!flat.Value.Concentric) { why = facesDownhill ? NotPlacedBeforeItsRoad : "flat off its centre, turned at random"; return null; }
        if (PlacedSites.TurnsItsGround(ops)) { why = LevelsOffItsCentre; return null; }
        Vector2 outward = roadEnd - centre;
        if (outward.sqrMagnitude < 1e-4f) { why = "road ends at its centre"; return null; }
        // The ground this place is expected to leave, which its kit chooses its supports on too (PlanningGround).
        Func<float, float, float> expected = ApproachGround(centre, centreGround, ops, world);
        float Ground(float x, float z) => expected(x, z);
        // The road's end at the top of a staircase: carried on from its top row, which stands at
        // its own height, over the ground or not.
        StairContinuation? onStairs = null;
        foreach (RoadStairSpan s in stairs)
            onStairs ??= TopAt(roadEnd, roadHeight, StairRun.FromPlan(s.FromPoint, s.ToPoint, Width, s.Plan).Pieces);
        onStairs ??= TopAt(roadEnd, roadHeight, StairStructures.PiecesNear(new Vector3(roadEnd.x, 0f, roadEnd.y), OnStairReach));
        if (onStairs == null && Mathf.Abs(Ground(roadEnd.x, roadEnd.y) - roadHeight) > SiteFootTolerance) { why = "road end off the ground"; return null; }
        Vector2 target = centre + outward.normalized * (flat.Value.Radius - ApronInset);
        Heightmap.Biome Biome(float x, float z) => world.GetBiome(x, z);
        StairKit kit = StairKit.ForLine(new List<Vector2> { roadEnd, target }, Biome);
        float apron = Ground(target.x, target.y);
        why = AimRefused(roadEnd, roadHeight, target, apron);
        if (why != "") return null;
        // The search keeps off the apron past its lip as the plan is refused there (PastTheLip), and meets the road's end on
        // its ring (RoadEndRing): sets off from it, or arrives on it going down from the road.
        float radius = flat.Value.Radius;
        // Facing into the place as it comes onto its apron (IntoThePlace).
        Vector2 into = IntoThePlace(centre);
        bool faceInto = true;
        RoadEndRing? ring = onStairs == null ? EndRing(path, endIndex, roadEnd, arriving, roadHeight) : null;
        StairPlan Laid(StairKit k) => Plan(roadEnd, target, k, Ground, arriving, null, null, StairCover.ApproachSearch,
            apron < roadHeight ? TopLandings : 0, after: onStairs, apron: (centre, radius), ring: ring, intoPlace: faceInto ? into : null);
        string Refused(StairPlan p)
        {
            string refused = PlanRefused(p, ApronDepth(centre, radius), Ground);
            if (refused == "" && faceInto && onStairs == null && SetsOffOnAPad(p)) refused = HeldOnAPad;
            return refused != "" ? refused : SiteConflict(p, centre, path, endIndex, Ground, world, stairs, EndsOnStairs(roadEnd, stairs) ? roadEnd : null, arriving, roadHeight);
        }
        (StairPlan plan, string why) Lay()
        {
            StairPlan plan = Laid(kit);
            string why = Refused(plan);
            return (plan, why);
        }
        (StairPlan plan, why) = Lay();
        // Refused for burial where it was aimed on the apron's lip (BuriedAtItsAim): the points beside it round the lip
        // (ApronBeside) tried first, facing in, within the facing allowance (FacingStalls), and the aim kept where none is laid.
        if (why != "" && BuriedAtItsAim(plan))
        {
            System.Threading.Interlocked.Increment(ref AimsBuried);
            (Vector2 aimed, float aimedApron) = (target, apron);
            int spent = plan.Expansions;
            foreach (Vector2 beside in ApronBeside(centre, aimed))
            {
                if (spent >= FacingStalls * GenerationStall) break;
                float there = Ground(beside.x, beside.y);
                if (AimRefused(roadEnd, roadHeight, beside, there) != "") continue;
                (target, apron) = (beside, there);
                var (besidePlan, refused) = Lay();
                spent += besidePlan.Expansions;
                if (refused == "") { (plan, why) = (besidePlan, refused); System.Threading.Interlocked.Increment(ref LaidBesideTheirAim); break; }
            }
            if (why != "") (target, apron) = (aimed, aimedApron);
        }
        // None facing into the place: laid as before, free at its end, rather than none laid (ApproachesNotFacingIn). Only
        // where the hold bore on it (StairPlan.HeldToFace) or it was refused for setting off on a pad: else the layout laid
        // again is the same layout, refused the same way.
        if (why != "" && (plan.HeldToFace > 0 || why == HeldOnAPad))
        {
            System.Threading.Interlocked.Increment(ref ApproachFacingReruns);
            faceInto = false;
            (plan, why) = Lay();
            if (why == "") System.Threading.Interlocked.Increment(ref ApproachesNotFacingIn);
        }
        if (why != "") return null;
        // Built in the kit its own pieces stand in (StairKit.ForPlan): laid again in that kit where it differs, and kept
        // as first laid where that one is refused.
        if (StairKit.ForPlan(plan, Biome) is var stood && stood != kit && Laid(stood) is var again && Refused(again) == "")
            (plan, kit) = (again, stood);
        RoadStairSpan span = Span(plan, roadEnd, roadHeight, target, apron, kit, endIndex);
        span.ExpectedPlace = (centre, centreGround);
        return span;
    }

    /// <summary>The points beside <paramref name="aim"/> round the lip of the apron about <paramref name="centre"/>, nearest
    /// first: <see cref="LipSteps"/> steps of <see cref="LipStep"/> either way along its circle, as beside an aim on a placed
    /// place's landing (<see cref="BesideOnTheLip"/>).</summary>
    internal static IEnumerable<Vector2> ApronBeside(Vector2 centre, Vector2 aim)
    {
        Vector2 out0 = aim - centre;
        float radius = out0.magnitude;
        if (radius < LipStep) yield break;
        for (int k = 1; k <= LipSteps; k++)
            foreach (float side in new[] { 1f, -1f })
            {
                float turn = side * k * LipStep / radius, c = Mathf.Cos(turn), s = Mathf.Sin(turn);
                yield return centre + new Vector2(out0.x * c - out0.y * s, out0.x * s + out0.y * c);
            }
    }

    /// <summary>The ground an approach is laid out on (<see cref="LaySiteApproach"/>) and its kit chooses its supports on
    /// (<see cref="PlanningGround"/>): the ground the place is expected to leave, over the ground as the places placed first
    /// leave it (<see cref="GroundAt"/>): the owner's second source, for a place with no preload
    /// (<see cref="PlacedSites.ExpectedGround"/>, the vertex-exact build).</summary>
    internal static Func<float, float, float> ApproachGround(Vector2 centre, float centreGround, IReadOnlyList<LevelOp>? ops, WorldGenerator world) =>
        PlacedSites.ExpectedGround(centre, centreGround, ops, world);

    /// <summary>The flat a place levels: the widest one round its centre, or failing that the widest anywhere.</summary>
    internal static LevelOp? FlatOf(IReadOnlyList<LevelOp> ops) => Flat(ops);

    private static LevelOp? Flat(IReadOnlyList<LevelOp> ops)
    {
        LevelOp? flat = null;
        foreach (LevelOp op in ops)
        {
            if (op.Radius <= ApronInset) continue;
            if (flat == null || (op.Concentric && !flat.Value.Concentric)
                || (op.Concentric == flat.Value.Concentric && op.Radius > flat.Value.Radius)) flat = op;
        }
        return flat;
    }

    /// <param name="roadRefused">The road was tried first and refused: then stairs are
    /// laid even where the ends are level, of floors or up and over a ridge (at three caves a road could not reach,
    /// fall back to stairs, even if it uses level blocks).</param>
    private static string AimRefused(Vector2 roadEnd, float roadHeight, Vector2 target, float apron, bool roadRefused = false,
        float reach = SiteReach) =>
        Vector2.Distance(roadEnd, target) > reach ? "too far"
        : !roadRefused && Mathf.Abs(apron - roadHeight) <= StairLayout.StairFitsFrom ? "level enough"
        : !RoadWater.BuiltDry(apron) ? "flat under water"
        : "";

    /// <summary>The chain's (<see cref="StairChain.CrestRise"/>).</summary>
    public const float CrestRise = StairChain.CrestRise;

    /// <summary>Nor nearer either end than this: a hump or dip at an end is the end's own step.</summary>
    public const float CrestFromEnd = 4f;

    /// <summary>The highest ground on the straight line from <paramref name="a"/> to
    /// <paramref name="b"/>, sampled every metre, when it stands <see cref="CrestRise"/> or
    /// more over both ends (a ridge: <c>crest</c> true), else the lowest when it lies that
    /// much under both (a valley), and <see cref="CrestFromEnd"/> or more from each end: a
    /// line a staircase, which only climbs, cannot take in one.</summary>
    internal static (Vector2 at, bool crest)? Turn(Vector2 a, Vector2 b, Func<float, float, float> ground) =>
        Turn(a, b, ground, out _);

    /// <param name="level">The lowest point of a valley the line crosses level, within the walkway cap: where the
    /// walkway is, once laid.</param>
    internal static (Vector2 at, bool crest)? Turn(Vector2 a, Vector2 b, Func<float, float, float> ground, out Vector2? level)
    {
        level = null;
        float length = Vector2.Distance(a, b);
        if (length < 2f * CrestFromEnd) return null;
        float ha = ground(a.x, a.y), hb = ground(b.x, b.y);
        var along = new List<(Vector2 p, float h)>();
        for (float d = CrestFromEnd; d <= length - CrestFromEnd; d += 1f)
        {
            Vector2 p = Vector2.Lerp(a, b, d / length);
            along.Add((p, ground(p.x, p.y)));
        }
        float high = along.Max(s => s.h), low = along.Min(s => s.h);
        bool crest = high >= Mathf.Max(ha, hb) + CrestRise;
        // The middle of a flat top or floor, not its edge.
        float extreme = crest ? high : low;
        var at = along.FindAll(s => Mathf.Abs(s.h - extreme) <= 0.1f);
        Vector2 middle = at[at.Count / 2].p;
        // A valley within the walkway cap is crossed level, not down stairs to its floor and up again: the chain's one
        // rule (StairChain).
        if (!crest && StairChain.CrossesLevel(low, ha, hb))
        {
            if (low < Mathf.Min(ha, hb) - CrestRise) level = middle;
            return null;
        }
        return (middle, crest);
    }

    /// <summary>Where the walk to <paramref name="target"/> from <paramref name="roadEnd"/> stops
    /// needing stairs: back along the straight line from the target, every metre that rises or falls
    /// no more than <see cref="RoadPathfinder.StairMinGrade"/> is ground a player walks; the first that
    /// does not ends it. The target itself when the ground just before it is already too steep.</summary>
    internal static Vector2 FlatsFrom(Vector2 roadEnd, Vector2 target, Func<float, float, float> ground)
    {
        float length = Vector2.Distance(roadEnd, target);
        Vector2 flats = target;
        float last = ground(target.x, target.y);
        for (float d = 1f; d <= length; d += 1f)
        {
            Vector2 p = Vector2.Lerp(target, roadEnd, d / length);
            float h = ground(p.x, p.y);
            if (Mathf.Abs(h - last) > RoadPathfinder.StairMinGrade) break;
            flats = p; last = h;
        }
        return Vector2.Distance(flats, target) < CrestFromEnd ? target : flats;
    }

    /// <summary>One plan of two staircases that meet on a crest or in a valley, the first
    /// from the road's end, the second on to the landing: walked as one, road end to landing.</summary>
    internal static StairPlan Join(StairPlan up, StairPlan down, Vector3 landing)
    {
        var plan = new StairPlan
        {
            Kit = up.Kit,
            StartHeading = up.StartHeading,
            // Where the walk ends: the second's foot when it was laid from the landing up (over a
            // crest), else its top (out of a valley).
            EndHeading = down.Reversed ? StairLayout.Wrap(down.StartHeading + 180f) : down.EndHeading,
            Reached = up.Reached && down.Reached,
            Reason = !up.Reached ? "up to the crest: " + up.Reason : !down.Reached ? "down from the crest: " + down.Reason : "",
            MaxCut = Mathf.Max(up.MaxCut, down.MaxCut),
            Waypoints = up.Waypoints + down.Waypoints,
            Backtracks = up.Backtracks + down.Backtracks,
            Stairs = up.Stairs + down.Stairs,
            Landings = up.Landings + down.Landings,
            Turns = up.Turns + down.Turns,
            TurnSteps = up.TurnSteps + down.TurnSteps,
            OverlapTurnSteps = up.OverlapTurnSteps + down.OverlapTurnSteps,
            Switchbacks = up.Switchbacks + down.Switchbacks,
            Rise = landing.y - (up.End.y - up.Rise),
            WalkedLength = up.WalkedLength + down.WalkedLength,
            MaxFloat = Mathf.Max(up.MaxFloat, down.MaxFloat),
            MaxBuried = Mathf.Max(up.MaxBuried, down.MaxBuried),
            End = down.Reversed ? landing : down.End,
            // Its foot is the first's: the step sunk under it, and the ring at the road's end it met (RoadEndRing).
            SunkPieces = up.SunkPieces, SunkClearances = up.SunkClearances, Foot = up.Foot,
            Ring = up.Ring, RingStart = up.RingStart, SetOffBack = up.SetOffBack,
        };
        if (down.LastRowPieces >= 0)
        {
            plan.LastRowPieces = up.Pieces.Count + down.LastRowPieces; plan.LastRowPieceCount = down.LastRowPieceCount;
            plan.LastRowClearances = up.Clearances.Count + down.LastRowClearances; plan.LastRowClearanceCount = down.LastRowClearanceCount;
            plan.LastRowKind = down.LastRowKind; plan.LastRowStart = down.LastRowStart;
            plan.LastRowHeading = down.LastRowHeading; plan.LastRowHeight = down.LastRowHeight;
        }
        plan.Pieces.AddRange(up.Pieces); plan.Pieces.AddRange(down.Pieces);
        plan.Clearances.AddRange(up.Clearances); plan.Clearances.AddRange(down.Clearances);
        plan.Cuts.AddRange(up.Cuts); plan.Cuts.AddRange(down.Cuts);
        return plan;
    }

    /// <summary>What is wrong with an approach staircase's own shape, if anything.</summary>
    private static string PlanRefused(StairPlan plan, Func<Vector2, float> depth, Func<float, float, float> ground) =>
        !plan.Reached ? "not reached"
        : Cover(plan, ground) > StairCover.ApproachRefusal ? "needs earthwork in the footprint"
        : PastTheLip(plan, depth) ? "too far onto the flat"
        : "";

    /// <summary>The ring at an approach's road end at path[<paramref name="endIndex"/>], <paramref name="end"/> (RoadEndRing):
    /// the road arriving on <paramref name="arriving"/> (read from the road as planned, ArrivingTravel), at
    /// <paramref name="height"/>, through where another road goes on past it.</summary>
    internal static RoadEndRing EndRing(List<Vector2> path, int endIndex, Vector2 end, float arriving, float height)
    {
        var back = new List<Vector2> { end };
        for (int i = endIndex + (endIndex == 0 ? 1 : -1); i >= 0 && i < path.Count; i += endIndex == 0 ? 1 : -1) back.Add(path[i]);
        return new RoadEndRing(end, arriving, height, RoadGoesOnPast(end, back));
    }

    /// <summary>What an approach staircase runs into, if anything: water, another
    /// place, its road away from its end, a road or staircase of the network, or its road's end's ring (RoadEndRing).</summary>
    /// <param name="meet">The road's end where the road ends on a staircase (its top or foot):
    /// the approach carries on from it, and the two touch there, as staircases meeting head-on
    /// may. Without it the frost cave above a 38 m staircase was refused "on another staircase"
    /// and nothing climbed its last 13 m (3712,3264).</param>
    internal static string SiteConflict(StairPlan plan, Vector2 centre, List<Vector2> path, int endIndex,
        Func<float, float, float> ground, WorldGenerator world, IReadOnlyList<RoadStairSpan> stairs, Vector2? meet = null, float? arriving = null,
        float? roadHeight = null)
    {
        string conflict = InExcludedZone(plan) ? ExcludedZone
            : Blocked(plan, world, centre, ground) is string hit && hit != "" ? hit
            : OverRoad(plan, path, endIndex, endIndex, ground) ? OnItsRoad
            : OverStairs(plan, new List<RoadStairSpan>(stairs), meet) ? "on another staircase"
            : DuplicatesAWayIn(plan, centre) ? BesideAnApproach
            : "";
        // Into its road's end's ring (RoadEndRing), where it meets the road, not a staircase: the road's travel and height there
        // as the caller read them from the road as planned (ArrivingTravel), else from the path and the ground.
        Vector2 end = path[endIndex];
        if (conflict == "" && meet == null && path.Count >= 2
            && EndRing(path, endIndex, end, arriving ?? ArrivingTravel(path, end, endIndex == 0), roadHeight ?? ground(end.x, end.y)).Intrudes(plan, out string piece, RoadEndRing.MetAtTop(plan)))
        {
            conflict = AcrossItsRoad;
            System.Threading.Interlocked.Increment(ref SiteChecksAcross);
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(FormattableString.Invariant($"[STAIRS] across its road: site check at ({end.x:F1}, {end.y:F1}) to {centre}: {piece}"));
        }
        return conflict;
    }

    /// <summary>An approach refused for arriving as another way into its place does (<see cref="DuplicatesAWayIn"/>).</summary>
    internal const string BesideAnApproach = "another way into the place arrives this way";

    /// <summary>Whether an approach staircase onto <paramref name="place"/> is a way in it has already: another way into that
    /// place arriving the same way (PlaceWaysIn, the one owner). Refused, the next aim or start is tried, and where none is
    /// laid its road ends on that way's road (RoadNetworkGenerator.ReEndOnAWayIn).</summary>
    internal static bool DuplicatesAWayIn(StairPlan plan, Vector2 place) => PlaceWaysIn.Joining(place, PlaceWaysIn.Of(plan, place), out _) != null;

    /// <summary>Where an approach's own road meets it (<see cref="StairRun.Ends"/>): of its two ends, the one away from the place
    /// at <paramref name="centre"/>. A later road to that place joins the network there (RoadNetworkGenerator.ReEndOnAWayIn).</summary>
    internal static Vector2 RoadEndOf(StairRun approach, Vector2 centre)
    {
        Vector2 from = approach.FromMeet ?? approach.From, to = approach.ToMeet ?? approach.To;
        return Vector2.Distance(from, centre) >= Vector2.Distance(to, centre) ? from : to;
    }

    /// <summary>Whether a road's end stands on a staircase: one of <paramref name="stairs"/>
    /// ends there, or a laid one has a walked piece within <see cref="OnStairReach"/>.</summary>
    internal static bool EndsOnStairs(Vector2 roadEnd, IReadOnlyList<RoadStairSpan> stairs)
    {
        foreach (RoadStairSpan s in stairs)
            if (Vector2.Distance(s.FromPoint, roadEnd) <= OnStairReach || Vector2.Distance(s.ToPoint, roadEnd) <= OnStairReach) return true;
        return StairStructures.PiecesNear(new Vector3(roadEnd.x, 0f, roadEnd.y), OnStairReach).Exists(p => p.Kind != StructurePieceKind.StairSupport);
    }

    private static RoadStairSpan Span(StairPlan plan, Vector2 roadEnd, float roadHeight, Vector2 target, float apron, StairKit kit, int endIndex)
    {
        Vector2 end = new Vector2(plan.End.x, plan.End.z);
        return plan.Reversed
            ? new RoadStairSpan { FromIndex = endIndex, ToIndex = endIndex, FromPoint = end, FromHeight = plan.End.y, ToPoint = target, ToHeight = apron, Kit = kit, Plan = plan }
            : new RoadStairSpan { FromIndex = endIndex, ToIndex = endIndex, FromPoint = roadEnd, FromHeight = roadHeight, ToPoint = end, ToHeight = plan.End.y, Kit = kit, Plan = plan };
    }

    /// <summary>
    /// Whether an approach staircase comes further onto a place's flat than its lip, where the level ground meets the slope
    /// (the one owner, for an apron, a raised landing and a cave's ledge alike): its top's exit edge, or any walked piece,
    /// deeper than <see cref="LipReach"/> inside it, by <paramref name="depth"/> (0 off the flat). It reaches the flat, not
    /// the place on it. Allowed 6 m onto an apron and, by its arrival reach (<see cref="LedgeReach"/>), anywhere on a
    /// raised landing, the staircase to the Mountain tower at -4801,1727 climbed on over its landing and ended 5.5 m from
    /// its centre, its top stair between the tower's own walls; the landing was level out to 10 m. The entrance pit in
    /// front of a cave is kept out of by the place's shape (<see cref="PlacedSiteShape.Allowed"/>).
    /// </summary>
    internal static bool PastTheLip(StairPlan plan, Func<Vector2, float> depth)
    {
        // The floor it finishes on, its last row, may stand on past the lip by its own depth: the way off the stairs onto the
        // flat (in game: an approach to a cave at 5708.8,3579.1 ended on a stair against the lip, with no floor to walk off
        // it on; the last floor of an approach may stand past the lip, onto the place's landing). So may the end rule's stair
        // down off a top (StairLayout.AddDescent, StepDownTop), which stands where that floor would: given nothing, a short top
        // onto Hildir's shelf at -2880,-5440 climbed one stair and its crest's stair down was refused "too far onto the flat",
        // the top laid a bare stair over the shelf.
        StairLayout.TerminalExit? exit = StairLayout.Terminal(plan);
        bool floorOut = exit != null && (exit.Pieces[0].Kind == StairPieceKind.Landing || exit.Descending);
        bool Within(Vector2 p, bool last) => depth(p) <= LipReach + (last && floorOut ? StairLayout.PieceRun : 0f) + 0.01f;
        if (exit != null && !Within(new Vector2(exit.Edge.x, exit.Edge.z), true)) return true;
        foreach (StairPiece piece in plan.Pieces)
            if (piece.Kind != StairPieceKind.Support && !Within(new Vector2(piece.Position.x, piece.Position.z), exit?.Pieces.Contains(piece) == true))
                return true;
        return false;
    }

    /// <summary>Whether a point this deep onto a flat is within its lip: the layout's search keeps every piece's centre and its
    /// top's exit edge there, as the plan is refused past it (<see cref="PastTheLip"/>); kept to centres alone, a top centred
    /// 1-2 m onto the flat was found, then refused.</summary>
    internal static bool WithinLip(float depth) => depth <= LipReach + 0.01f;

    /// <summary>How deep inside an apron's edge a point stands (none off it).</summary>
    internal static Func<Vector2, float> ApronDepth(Vector2 centre, float radius) => p => radius - Vector2.Distance(p, centre);
}
