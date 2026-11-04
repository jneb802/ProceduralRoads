using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Approach staircases laid with the roads (one owner): from a road's end at a place up (or down) to the flat apron the place
/// levels round its centre, climbing and coming down again over a crest or a valley between, crossing a shallow dip level, and
/// stopping where the ground to the apron is walkable.
/// </summary>
public static partial class RoadStairRuns
{
    /// <summary>A site's flat ground this far or less from the road's end is in reach of an approach staircase.</summary>
    public const float SiteReach = 40f;

    /// <summary>An approach staircase is laid only where the road's end and the site's flat ground differ by this much.</summary>
    public const float SiteMinClimb = 2f;

    /// <summary>It is aimed this far inside the edge of the site's flat ground.</summary>
    public const float ApronInset = 1f;

    /// <summary>And no walked piece of it stands deeper than this inside that edge
    /// (it may arrive within <see cref="StairLayout.ReachRadius"/> of its aim and
    /// carry on a landing): it reaches the apron, not the place on it.</summary>
    public const float ApronReach = 6f;

    /// <summary>The road's end must stand this close to the ground the staircase starts from.</summary>
    public const float SiteFootTolerance = 0.5f;

    /// <summary>
    /// A staircase from a road's end at a place up to the place's flat ground. A road stops
    /// outside a place's whole terrain footprint (at a frost cave 30 m from its
    /// centre), and nothing builds the last stretch, which is often too steep
    /// for road anyway. Where the place levels a single flat round its centre
    /// (an apron), the road's end is <see cref="SiteMinClimb"/> or more above
    /// or below it and within <see cref="SiteReach"/> of it, this lays a
    /// staircase from the road's end to just inside the apron's edge on the
    /// side the road comes from, over the ground the place leaves
    /// (<see cref="LocationLevelling.PlacedGroundAt"/>). It may cross the
    /// place's own footprint, but not reach far onto the apron, and every other
    /// check of a road's staircase applies. The terrain writer leaves a place's
    /// footprint alone, so its cuts are never made: it is laid out with no
    /// flight buried deeper than <see cref="StairCover.ApproachSearch"/> and
    /// refused where anything is buried deeper than <see cref="StairCover.ApproachRefusal"/>.
    ///
    /// A flat off the centre lies wherever the place is turned, which is not
    /// known until the place is placed, as its zone first loads: none is laid to it.
    /// A model of that ground, from an estimated turn, buried six stairs of the
    /// first one in the cliff below a frost cave's ledge; the
    /// staircase onto a placed place's landing is <see cref="PlanPlacedSiteApproach"/>.
    /// Null, with <paramref name="why"/>, where none is laid; the road is then as it was.
    /// </summary>
    public static RoadStairSpan? LaySiteApproach(Vector2 roadEnd, float roadHeight, float arriving, Vector2 centre,
        float centreGround, IReadOnlyList<LevelOp>? ops, List<Vector2> path, int endIndex,
        IReadOnlyList<RoadStairSpan> stairs, WorldGenerator world, out string why)
    {
        why = "";
        if (ops == null || ops.Count == 0) { why = "no levelling known"; return null; }
        if (!LocationLevelling.PlatformHeight(centreGround, ops).HasValue) { why = "no single flat level"; return null; }
        LevelOp? flat = Flat(ops);
        if (flat == null) { why = "no flat ground"; return null; }
        if (!flat.Value.Concentric) { why = "flat off its centre"; return null; }
        Vector2 outward = roadEnd - centre;
        if (outward.sqrMagnitude < 1e-4f) { why = "road ends at its centre"; return null; }
        float Ground(float x, float z) =>
            LocationLevelling.PlacedGroundAt(new Vector2(x, z), centre, centreGround, ops, GroundAt(x, z, world));
        // The road's end at the top of a staircase: carried on from its top row, which stands at
        // its own height, over the ground or not.
        StairContinuation? onStairs = null;
        foreach (RoadStairSpan s in stairs)
            onStairs ??= TopAt(roadEnd, roadHeight, StairRun.FromPlan(s.FromPoint, s.ToPoint, Width, s.Plan).Pieces);
        onStairs ??= TopAt(roadEnd, roadHeight, StairStructures.PiecesNear(new Vector3(roadEnd.x, 0f, roadEnd.y), OnStairReach));
        if (onStairs == null && Mathf.Abs(Ground(roadEnd.x, roadEnd.y) - roadHeight) > SiteFootTolerance) { why = "road end off the ground"; return null; }
        Vector2 target = centre + outward.normalized * (flat.Value.Radius - ApronInset);
        StairKit kit = StairKit.ForLine(new List<Vector2> { roadEnd, target }, (x, z) => world.GetBiome(x, z), world.GetSeed());
        float apron = Ground(target.x, target.y);
        why = AimRefused(roadEnd, roadHeight, target, apron);
        if (why != "") return null;
        StairPlan plan = Plan(roadEnd, target, kit, Ground, arriving, null, null, StairCover.ApproachSearch,
            apron < roadHeight ? TopLandings : 0, after: onStairs);
        why = PlanRefused(plan, centre, flat.Value.Radius, null);
        if (why == "") why = SiteConflict(plan, centre, path, endIndex, Ground, world, stairs, EndsOnStairs(roadEnd, stairs) ? roadEnd : null);
        if (why != "") return null;
        return Span(plan, roadEnd, roadHeight, target, apron, kit, endIndex);
    }

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
        : !roadRefused && Mathf.Abs(apron - roadHeight) < SiteMinClimb ? "level enough"
        : apron < RoadConstants.ShallowWaterHeight ? "flat under water"
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
    internal static (Vector2 at, bool crest)? Turn(Vector2 a, Vector2 b, Func<float, float, float> ground)
    {
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
        // A shallow valley is crossed level, not down stairs to its floor and up again: the chain's one rule (StairChain).
        if (!crest && StairChain.CrossesLevel(low, ha, hb)) return null;
        // The middle of a flat top or floor, not its edge.
        float extreme = crest ? high : low;
        var at = along.FindAll(s => Mathf.Abs(s.h - extreme) <= 0.1f);
        return (at[at.Count / 2].p, crest);
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
    private static string PlanRefused(StairPlan plan, Vector2 flatCentre, float radius, Vector2? front) =>
        !plan.Reached ? "not reached"
        : plan.MaxCut - StairLayout.CutBelowSurface > StairCover.ApproachRefusal ? "needs earthwork in the footprint"
        : OnFlat(plan, flatCentre, radius, front) ? "too far onto the flat"
        : "";

    /// <summary>What an approach staircase runs into, if anything: water, another
    /// place, its road away from its end, a road or staircase of the network.</summary>
    /// <param name="meet">The road's end where the road ends on a staircase (its top or foot):
    /// the approach carries on from it, and the two touch there, as staircases meeting head-on
    /// may. Without it the frost cave above a 38 m staircase was refused "on another staircase"
    /// and nothing climbed its last 13 m (3712,3264).</param>
    internal static string SiteConflict(StairPlan plan, Vector2 centre, List<Vector2> path, int endIndex,
        Func<float, float, float> ground, WorldGenerator world, IReadOnlyList<RoadStairSpan> stairs, Vector2? meet = null) =>
        InExcludedZone(plan) ? ExcludedZone
        : Blocked(plan, world, centre, ground) is string hit && hit != "" ? hit
        : OverRoad(plan, path, endIndex, endIndex, ground) ? "on its road"
        : OverStairs(plan, new List<RoadStairSpan>(stairs), meet) ? "on another staircase"
        : "";

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

    /// <summary>Whether a walked piece reaches too far onto the flat: deeper than
    /// <see cref="ApronReach"/> inside an apron's edge, or, for a flat in front of
    /// which the place stands (<paramref name="front"/>), anywhere inside
    /// <paramref name="radius"/> of it (its entrance pit).</summary>
    private static bool OnFlat(StairPlan plan, Vector2 flatCentre, float radius, Vector2? front)
    {
        float inner = Mathf.Max(0f, radius - ApronReach);
        foreach (StairPiece piece in plan.Pieces)
        {
            if (piece.Kind == StairPieceKind.Support) continue;
            var p = new Vector2(piece.Position.x, piece.Position.z);
            // In front of a place, any piece (fillers too) in its entrance pit; on an apron,
            // deeper than ApronReach inside its edge.
            if (front.HasValue ? Vector2.Distance(p, flatCentre) < radius : Vector2.Distance(p, flatCentre) < inner)
                return true;
        }
        return false;
    }

    /// <summary>The chain's (<see cref="StairChain.ShallowDip"/>).</summary>
    public const float ShallowDip = StairChain.ShallowDip;
}
