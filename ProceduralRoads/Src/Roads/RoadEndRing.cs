using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Where a staircase may meet its road at the road's end (one owner): a circle a little inside the road's edge, centred on
/// the road's end, with the arc facing back the way the road came cut out and a clear lane kept along the road (both ways
/// where the road goes on past the end). A staircase starts anywhere on the remaining arc: its foot, or for a flight going
/// down from the road its top's exit edge, stands on the circle, and none of its walked pieces stands inside the circle or
/// in the lane (draw a circle about 0.5 m in from the road's edge, slice out the arc facing back the way
/// the road came with at least ~2 m of clear corridor, start the stair anywhere on the remaining arc; straight on is best,
/// turning off costs more). It replaces the foot's straight-on/turned split and the relays that set a turned foot beside
/// the road. It also decides the small level pad laid where a staircase starts at the road's end (<see cref="PadUnder"/>).
/// Pure geometry.
/// </summary>
public readonly struct RoadEndRing
{
    /// <summary>How far in from the road's edge the circle runs.</summary>
    public const float Inset = 0.5f;

    /// <summary>The circle's slack: walked pieces may come this far inside it (<see cref="KeptOut"/>), and a start this far
    /// behind or ahead of the road's end is still abreast of it (<see cref="WalksOffAcross"/>) (tuned in game). Not how far off
    /// the circle a staircase meets it: a start and an arrival alike stand on it (<see cref="Allows"/>).</summary>
    public const float Delta = 0.5f;

    /// <summary>Half the clear lane kept along the road (a ~2 m corridor where nothing is built).</summary>
    public const float CorridorHalfWidth = 1f;

    /// <summary>How far along the road from its end the clear lane is kept: the end's joining zone, which OverRoad leaves to
    /// the ring (RoadStairRuns.JoiningStep). Past it the rest of the road is OverRoad's.</summary>
    public const float CorridorLength = RoadStairRuns.JoiningStep;

    /// <summary>A walked piece's sample this far over the lane's or the inner circle's edge is on it (a piece placed on the
    /// line, not over it).</summary>
    public const float Margin = 0.05f;

    /// <summary>How far a walked piece may stand over the lane's or the inner circle's edge when a road staircase is judged on
    /// its road as planned (RoadStairRuns.SpanAcrossItsRoad): its layout kept out by <see cref="Margin"/> of a ring read from
    /// the search's waypoints, and the road as planned arrives a few degrees off them.</summary>
    public const float PlannedMargin = 0.25f;

    /// <summary>Price per heading step (22.5 degrees) a staircase leaves its road's end off the road's travel: straight on
    /// is free, a quarter turn costs 8, back along the road 16 (a piece's length is 2). The foot's old price per step
    /// (StairLayout.FootOffRoadWeight), now linear over the whole turn rather than split at 45 degrees.</summary>
    public const float TurnWeight = StairLayout.FootOffRoadWeight;

    /// <summary>Price per heading step a staircase's first piece leaves the circle off square (off the arc point's
    /// radial): straight on starts from the front of the ring, a quarter turn from its side.</summary>
    public const float SkewWeight = 0.5f;

    /// <summary>The most a start leaves the circle off square (off its arc point's radial): three heading steps, short of
    /// square to it. Square or more, a flight set off across the front of the ring. (Held to two steps, six worlds laid four
    /// staircases fewer and reached two locations fewer.)</summary>
    public const float MostSkew = 3f * StairLayout.HeadingStep;

    /// <summary>How far round the circle candidate starts are taken: the hammer's heading step.</summary>
    public const float ArcStep = StairLayout.HeadingStep;

    /// <summary>The road's end: the circle's centre.</summary>
    public readonly Vector2 Centre;
    /// <summary>The road's travel into its end (the walker's heading arriving there), degrees.</summary>
    public readonly float Travel;
    /// <summary>The road's height at its end.</summary>
    public readonly float Height;
    /// <summary>The circle's radius: half the road's width less <see cref="Inset"/>.</summary>
    public readonly float Radius;
    /// <summary>The road goes on past its end (a spur foot set on a road that carries on): the lane is kept both ways and the
    /// arc straight on is cut too.</summary>
    public readonly bool Through;

    public RoadEndRing(Vector2 centre, float travel, float height, bool through = false, float? roadWidth = null)
    {
        float width = roadWidth ?? RoadNetworkGenerator.RoadWidth;
        Centre = centre;
        Travel = StairLayout.Wrap(travel);
        Height = height;
        Through = through;
        Radius = Mathf.Max(0.5f, width / 2f - Inset);
    }

    /// <summary>Degrees either side of straight back (and, through, of straight on) cut out of the arc: where the lane meets
    /// the circle.</summary>
    public float CutArc => (float)Math.Asin(Mathf.Clamp01(CorridorHalfWidth / Radius)) * Mathf.Rad2Deg;

    /// <summary>Signed degrees from <paramref name="travel"/> to <paramref name="heading"/>, -180..180, + to the right.</summary>
    internal static float Turn(float heading, float travel) => StairLayout.Wrap(heading - travel + 180f) - 180f;

    /// <summary>The point of the circle <paramref name="arc"/> degrees round from straight on (+ to the right).</summary>
    public Vector2 ArcPoint(float arc) => Centre + StairLayout.Forward(Travel + arc) * Radius;

    /// <summary>Whether the arc point <paramref name="arc"/> degrees round is cut out (back, and through also straight on).</summary>
    public bool ArcCut(float arc)
    {
        float a = Mathf.Abs(Turn(Travel + arc, Travel));
        return a > 180f - CutArc + 0.01f || Through && StraightOn(Travel + arc);
    }

    /// <summary>Whether <paramref name="heading"/> is within the arc straight on, where the lane meets the circle on the far
    /// side (<see cref="CutArc"/>): the arc a through road cuts, and the way a road going on past the end goes.</summary>
    public bool StraightOn(float heading) => Mathf.Abs(Turn(heading, Travel)) < CutArc - 0.01f;

    /// <summary>A first piece setting off from the circle: its back edge centred on <see cref="At"/>, heading
    /// <see cref="Heading"/>, at <see cref="Arc"/> degrees round, and what it costs.</summary>
    public readonly struct Start
    {
        public readonly Vector2 At;
        public readonly float Heading, Arc, Price;
        public Start(Vector2 at, float heading, float arc, float price) { At = at; Heading = heading; Arc = arc; Price = price; }
    }

    /// <summary>What a staircase leaving the ring on <paramref name="heading"/> from <paramref name="arc"/> costs: by its turn
    /// off the road's travel (<see cref="TurnWeight"/>), and by how far off square it leaves the circle
    /// (<see cref="SkewWeight"/>).</summary>
    public float Price(float heading, float arc) =>
        TurnWeight * Mathf.Abs(Turn(heading, Travel)) / StairLayout.HeadingStep
        + SkewWeight * Mathf.Abs(Turn(heading, Travel + arc)) / StairLayout.HeadingStep;

    /// <summary>Every start on the remaining arc (every <see cref="ArcStep"/> round it, the cut arcs left out) on each of the
    /// hammer's sixteen headings that leaves the circle outward (within <see cref="MostSkew"/> of the arc point's radial: a
    /// flight does not set off across the front of the ring), whose first piece of <paramref name="width"/> keeps out of
    /// the circle and the lane and whose foot is not walked off across the road (<see cref="WalksOffAcross"/>), cheapest
    /// first. The one owner of staircase starts (docs/OWNERS.md).</summary>
    public List<Start> Starts(float width)
    {
        var starts = new List<Start>();
        for (float arc = -180f + ArcStep; arc <= 180f + 0.01f; arc += ArcStep)
        {
            if (ArcCut(arc)) continue;
            Vector2 at = ArcPoint(arc);
            for (int k = 0; k < 16; k++)
            {
                float heading = k * StairLayout.HeadingStep;
                if (Mathf.Abs(Turn(heading, Travel + arc)) > MostSkew + 0.01f) continue;
                if (Intrudes(Footprint(at, heading, width, StairLayout.PieceRun))) continue;
                if (WalksOffAcross(at, heading + 180f)) continue;
                starts.Add(new Start(at, heading, arc, Price(heading, arc)));
            }
        }
        starts.Sort((x, y) => x.Price != y.Price ? x.Price.CompareTo(y.Price) : Mathf.Abs(x.Arc).CompareTo(Mathf.Abs(y.Arc)));
        return starts;
    }

    /// <summary>Whether a first piece of <paramref name="width"/> may set off from <paramref name="at"/> on the circle on
    /// <paramref name="heading"/>, as <see cref="Starts"/> takes a start: not from a cut arc, within <see cref="MostSkew"/> of
    /// its arc point's radial, its footprint out of the circle and the lane, not walked off across the road. For a start the
    /// ring did not lay out itself (RoadStairRuns.CarryRoadOntoStairs: a ring put a radius behind a staircase's first stair),
    /// and for a top arriving on the ring from below (StairLayout.Build's search, read as a start leaving it the way the flight
    /// goes down): one precise target for both ends, no band round the circle for an arrival. The one owner of staircase
    /// starts and arrivals on a road's end (docs/OWNERS.md).</summary>
    public bool Allows(Vector2 at, float heading, float width)
    {
        if (Mathf.Abs(Vector2.Distance(at, Centre) - Radius) > 0.05f) return false;
        float arc = Turn(StairLayout.Bearing(Centre, at), Travel);
        return !ArcCut(arc) && Mathf.Abs(Turn(heading, Travel + arc)) <= MostSkew + 0.01f
            && !Intrudes(Footprint(at, heading, width, StairLayout.PieceRun)) && !WalksOffAcross(at, heading + 180f);
    }

    /// <summary>
    /// Whether a walker stepping straight off a staircase's end at <paramref name="at"/>, on <paramref name="walkOff"/>,
    /// crosses the road (the ring's rule for where a staircase starts: from the road's end, never across its road). From
    /// beside the road's body (behind its end by more than the circle's slack, <see cref="Delta"/>, where the clear lane runs;
    /// on a <see cref="Through"/> road ahead of it too), the step off heads for the road's other side without heading toward
    /// its end: square across the road, or back along it. A start in front of the end, or abreast of it (a foot lowered
    /// toward its road reads its ring a few degrees round), steps off onto the road's end; one behind it stepping off toward
    /// the end (square to the circle, or leaning so) comes onto the road where it ends. Near the cut arc a flight set off
    /// square across the road (up to <see cref="MostSkew"/> off its arc point's radial) kept every walked piece out of the
    /// lane, and its foot was walked off straight across the road behind its end, onto the bank on the road's other side.
    /// </summary>
    public bool WalksOffAcross(Vector2 at, float walkOff, float margin = Margin)
    {
        Vector2 d = at - Centre, forward = StairLayout.Forward(Travel), right = StairLayout.Right(Travel), dir = StairLayout.Forward(walkOff);
        float along = Vector2.Dot(d, forward), across = Vector2.Dot(d, right), goesAlong = Vector2.Dot(dir, forward), goesAcross = Vector2.Dot(dir, right);
        bool besideTheBody = along < -Delta || Through && along > Delta;
        return besideTheBody && Mathf.Abs(across) > margin && across * goesAcross < 0f && along * goesAlong >= 0f;
    }

    /// <summary>Samples over a piece's footprint from its back edge's centre <paramref name="back"/> on
    /// <paramref name="heading"/>, every half metre, its edges included.</summary>
    internal static IEnumerable<Vector2> Footprint(Vector2 back, float heading, float width, float depth)
    {
        Vector2 dir = StairLayout.Forward(heading), right = StairLayout.Right(heading);
        for (float along = 0f; along <= depth + 0.001f; along += 0.5f)
            for (float side = -width / 2f; side <= width / 2f + 0.001f; side += 0.5f)
                yield return back + dir * along + right * side;
    }

    /// <summary>Whether <paramref name="p"/> lies inside the circle by more than <see cref="Delta"/>, or in the clear lane
    /// (back along the road, and on along it where it goes on past its end), by more than <paramref name="margin"/>.</summary>
    public bool KeptOut(Vector2 p, float margin = Margin)
    {
        Vector2 d = p - Centre;
        if (d.magnitude < Radius - Delta - margin) return true;
        float along = Vector2.Dot(d, StairLayout.Forward(Travel)), across = Vector2.Dot(d, StairLayout.Right(Travel));
        if (Mathf.Abs(across) >= CorridorHalfWidth - margin) return false;
        return along <= 0f && along >= -CorridorLength || Through && along >= 0f && along <= CorridorLength;
    }

    private bool Intrudes(IEnumerable<Vector2> samples)
    {
        foreach (Vector2 p in samples) if (KeptOut(p)) return true;
        return false;
    }

    /// <summary>
    /// Whether a staircase meeting this road's end crosses it (the one test, for every staircase at a road's end, its foot or
    /// its top alike): any walked piece (stairs, floors, fillers; not what holds them up, nor the step sunk under its foot
    /// on purpose; at its <paramref name="top"/> not the floors level with the road, which the road comes onto) within
    /// RoadStairRuns.JunctionHeight of the road's height whose walking surface comes inside the circle (by more than
    /// <see cref="Delta"/>) or into the clear lane; and a foot set off from its ring walked off across the road
    /// (<see cref="WalksOffAcross"/>). <paramref name="piece"/>: the first such piece, for the log.
    /// </summary>
    public bool Intrudes(StairPlan plan, out string piece, bool top = false, float margin = Margin)
    {
        piece = "";
        // Its foot walked off across the road (WalksOffAcross), from where it sets off on this ring: the start it set off from on
        // its own ring, moved with the road's end (a foot lowered since, the road as planned).
        if (!top && plan.RingStart is Vector2 setOff && plan.Ring is RoadEndRing own
            && Centre + (setOff - own.Centre) is var start && WalksOffAcross(start, plan.StartHeading + 180f, margin))
        {
            piece = FormattableString.Invariant(
                $"its foot at ({start.x:F1}, {start.y:F1}) walked off on {StairLayout.Wrap(plan.StartHeading + 180f):F1} across the road arriving on {Travel:F1} at ({Centre.x:F1}, {Centre.y:F1})");
            return true;
        }
        for (int i = plan.SunkPieces; i < plan.Pieces.Count; i++)
        {
            StairPiece p = plan.Pieces[i];
            if (p.Kind == StairPieceKind.Support) continue;
            float surface = StairLayout.CentreSurface(p);
            if (Mathf.Abs(surface - Height) > RoadStairRuns.JunctionHeight) continue;
            // At a top, a floor a walker's step off the road's height (StairLayout.Walkable) is where the road comes onto it,
            // level (a top landing the road meets from any side): not a crossing. Only its flight counts there.
            if (top && p.Kind != StairPieceKind.Stair && StairLayout.Walkable(surface, Height)) continue;
            foreach (Vector3 s in StairLayout.WalkingSurface(p))
            {
                var at = new Vector2(s.x, s.z);
                if (!KeptOut(at, margin)) continue;
                Vector2 d = at - Centre;
                piece = FormattableString.Invariant(
                    $"{p.Kind} at ({p.Position.x:F1}, {p.Position.y:F2}, {p.Position.z:F1}): its surface at ({at.x:F1}, {at.y:F1}) {d.magnitude:F2} m from the road's end ({Centre.x:F1}, {Centre.y:F1}), {Vector2.Dot(d, StairLayout.Forward(Travel)):F2} along and {Vector2.Dot(d, StairLayout.Right(Travel)):F2} across its travel {Travel:F1}{(Through ? ", a through road" : "")}");
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether <paramref name="plan"/> met its road's end at its top (arrived on its ring going down from the road),
    /// not set off from it at its foot: as the layout laid it, no guess from its pieces.</summary>
    public static bool MetAtTop(StairPlan plan) => plan.Ring.HasValue && !plan.RingStart.HasValue;

    /// <summary><see cref="Intrudes(StairPlan, out string, bool, float)"/> without the detail.</summary>
    public bool Intrudes(StairPlan plan, bool top = false) => Intrudes(plan, out _, top);

    /// <summary>How far from <see cref="Centre"/> the road's end reaches, the disc its own earthwork levels: the ring and its
    /// <see cref="Inset"/> (<see cref="RoadsGround"/>, <see cref="PadUnder(StairPlan, RoadEndRing, bool, Func{float, float, float}, float, out string)"/>,
    /// RoadStairRuns.ConnectionRefusal).</summary>
    public float RoadsEnd => Radius + Inset;

    /// <summary>How far from <see cref="Centre"/> a staircase still starts: the road's end and <see cref="PadReach"/> on.</summary>
    public float StartReach => RoadsEnd + PadReach;

    /// <summary><paramref name="ground"/> as the road leaves it at its end: level at the road's height across the road's end
    /// (within half its width of the centre), the ground the road's own earthwork cuts or fills there; elsewhere as it is.
    /// A flight set off from the circle stands on the road's end there, at its height.</summary>
    public Func<float, float, float> RoadsGround(Func<float, float, float> ground)
    {
        Vector2 centre = Centre;
        float height = Height, reach = RoadsEnd;
        // (new Vector2(x, z) - centre).sqrMagnitude, each part rounded where the vector and its square stored them: the
        // same to the bit, without the vector calls the game's Mono makes for every ground read of a staircase's search.
        return (x, z) =>
        {
            float dx = x - centre.x, dz = z - centre.y;
            float distSq = dx * dx + dz * dz;
            return distSq <= reach * reach ? height : ground(x, z);
        };
    }

    // ---- The level pad where a staircase starts ----

    /// <summary>How far apart the pad's points stand: a lattice on the world's half metres.</summary>
    public const float PadSpacing = 0.5f;

    /// <summary>Each pad point's width: its flat core covers its lattice cell; past the pad the terrain writer blends it into
    /// the road's grade and the ground over its usual margin, as it does a junction's fill.</summary>
    public const float PadPointWidth = 1f;

    /// <summary>How far under a floor's top the pad lies under it: level with its top all over, the floor stood wholly in the
    /// ground and was not placed (BuriedPieces: the ground over its whole footprint at or over its top).</summary>
    public const float PadUnderFloor = 0.1f;

    /// <summary>How far from the road's end a walked piece at the road's level is still where the staircase starts: the ring's
    /// edge and two pieces on (two floors at the foot, then its first stair).</summary>
    public const float PadReach = 2f * StairLayout.PieceRun + 3f;

    /// <summary>The most a pad fills over its ground off the road's own end: past it the ground is left as it is there, so no
    /// ridge is filled up to a stair (RoadStairRuns.FootRidge, the road to a foot's own limit: no road rather than a
    /// ridge road). Such a point once refused the whole pad: a corner of the first stair over falling ground left the
    /// staircase's start with none.</summary>
    public const float PadMostFill = RoadStairRuns.FootRidge;

    /// <summary>The level pad under where a staircase meets its road's end (<see cref="PadUnder"/>): points levelled at
    /// <see cref="Height"/> round the road's end <see cref="End"/> and under the walked pieces there.</summary>
    public sealed class Pad
    {
        public Vector2 End;
        /// <summary>The walked piece's height the pad is levelled to: a foot's low edge, a top's exit.</summary>
        public float Height;
        /// <summary>Met at its top (a flight going down from the road's end), not its foot.</summary>
        public bool Top;
        public readonly List<Vector2> Points = new();
        public readonly List<float> Heights = new();
        /// <summary>The deepest cut and the highest fill it asks of the ground (0 for none).</summary>
        public float MostCut, MostFill;
        /// <summary>Points off the road's end left out for the fill they would need (<see cref="PadMostFill"/>), or for one
        /// beside them that would.</summary>
        public int LeftOut;
        /// <summary>Walked pieces it lies under.</summary>
        public int Pieces;

        /// <summary>As stored: levelled road points, never painted (the road's own paint is left as it was).</summary>
        public RoadSpatialGrid.PlannedPath ToPath() =>
            new(new List<Vector2>(Points), new List<float>(Heights), Points.ConvertAll(_ => default(RoadPointDebugInfo)), PadPointWidth, 0f, false)
            { Unpainted = Points.ConvertAll(_ => true) };
    }

    /// <summary>
    /// The level pad where a staircase laid against its ring (<see cref="StairPlan.Ring"/>) meets its road's end (one owner:
    /// the ring decides it): the road mostly level where the stairs start, as a junction is
    /// levelled, but much smaller: just the bit under the stairs. A road arriving on a grade ended level at its end, but the
    /// ground round it and under a flight turned off it followed the slope, a step's edge standing proud on one side.
    /// <para>The road's end (the ring's centre, moved with a foot lowered since: <see cref="StairPlan.Foot"/> less its offset
    /// on the ring) and the disc the road's end levels (half the road's width round it), and the walked pieces where the
    /// staircase starts: at a foot its first stair and any floors at the road's level before it, at a top (a flight going
    /// down from the road's end) the floors at the road's level it ends on, never the flight going down. All levelled to the
    /// walked piece's height there (a foot's low edge, a top's exit), under a floor <see cref="PadUnderFloor"/> lower; past
    /// it the terrain writer blends the pad into the road's grade and the ground.</para>
    /// <para>Within the road's earthwork rules: off the road's own end, no point that fills more than
    /// <see cref="PadMostFill"/>, nor one beside it that its flat core would level too (no ridge up to a stair: the ground
    /// there is left as it is, <see cref="Pad.LeftOut"/>); and none where the pad so laid is past <paramref name="cap"/> as
    /// every road's earthwork is measured (RoadNetworkGenerator.EarthworkAcross, its points as stored, over
    /// <paramref name="ground"/>). Null, with <paramref name="why"/>, where none is laid.</para>
    /// </summary>
    public static Pad? PadUnder(StairPlan plan, Func<float, float, float> ground, float cap, out string why)
    {
        why = "";
        if (plan.Ring is not RoadEndRing ring) { why = "no ring"; return null; }
        return PadUnder(plan, ring, MetAtTop(plan), ground, cap, out why);
    }

    /// <summary>
    /// <see cref="PadUnder(StairPlan, Func{float, float, float}, float, out string)"/> at the road's end on
    /// <paramref name="ring"/>, at the staircase's <paramref name="top"/> or its foot. A road staircase is laid against the ring
    /// at its foot only (<see cref="StairPlan.Ring"/>); its top decides where the road starts again, a ring's radius out from
    /// its exit (RoadStairRuns.RoadEndPast), and the ring there is read from its road as planned (RoadStairRuns.PlannedRing).
    /// </summary>
    public static Pad? PadUnder(StairPlan plan, RoadEndRing ring, bool top, Func<float, float, float> ground, float cap, out string why)
    {
        why = "";
        Vector2 end;
        float height;
        if (top)
        {
            end = ring.Centre;
            height = plan.End.y;
        }
        else
        {
            if (!plan.Ring.HasValue || plan.RingStart is not Vector2 start) { why = "no start on its ring"; return null; }
            StairPiece? first = null;
            for (int i = plan.SunkPieces; i < plan.Pieces.Count && first == null; i++)
                if (plan.Pieces[i].Kind != StairPieceKind.Support) first = plan.Pieces[i];
            if (first == null) { why = "no walked piece"; return null; }
            Vector2 foot = plan.Foot is Vector3 f ? new Vector2(f.x, f.z) : start;
            end = foot - (start - ring.Centre);
            height = plan.Foot?.y ?? StairLayout.Footprint(first).height;
        }
        // The walked pieces where it starts: in walking order away from the road, floors at the road's level and, at a
        // foot, the first stair.
        float reach = ring.StartReach;
        var under = new List<(Vector2 back, float heading, float across, float depth, float height)>();
        for (int k = 0; k < plan.Pieces.Count; k++)
        {
            int i = top ? plan.Pieces.Count - 1 - k : plan.SunkPieces + k;
            if (i < plan.SunkPieces || i >= plan.Pieces.Count) break;
            StairPiece p = plan.Pieces[i];
            if (p.Kind == StairPieceKind.Support) continue;
            var fp = StairLayout.Footprint(p);
            bool stair = p.Kind == StairPieceKind.Stair;
            if (Vector2.Distance(new Vector2(p.Position.x, p.Position.z), end) > reach) break;
            if (stair)
            {
                if (!top && StairLayout.Walkable(fp.height, height)) under.Add((fp.back, fp.heading, fp.across, fp.depth, height));
                break;
            }
            if (!StairLayout.Walkable(fp.height, height)) break;
            under.Add((fp.back, fp.heading, fp.across, fp.depth, fp.height - PadUnderFloor));
        }
        // A top whose flight goes down straight off the circle: the road's end alone.
        if (under.Count == 0 && !top) { why = "no walked piece at the road's level"; return null; }
        // A lattice over the road's end and those pieces.
        float disc = ring.RoadsEnd;
        float minX = end.x - disc, maxX = end.x + disc, minZ = end.y - disc, maxZ = end.y + disc;
        foreach (var u in under)
        {
            Vector2 dir = StairLayout.Forward(u.heading), right = StairLayout.Right(u.heading);
            foreach (Vector2 c in new[] { u.back - right * (u.across / 2f), u.back + right * (u.across / 2f),
                         u.back + dir * u.depth - right * (u.across / 2f), u.back + dir * u.depth + right * (u.across / 2f) })
            {
                minX = Mathf.Min(minX, c.x); maxX = Mathf.Max(maxX, c.x); minZ = Mathf.Min(minZ, c.y); maxZ = Mathf.Max(maxZ, c.y);
            }
        }
        var pad = new Pad { End = end, Height = height, Top = top, Pieces = under.Count };
        float x0 = Mathf.Floor(minX / PadSpacing) * PadSpacing, z0 = Mathf.Floor(minZ / PadSpacing) * PadSpacing;
        int nx = Mathf.FloorToInt((maxX - x0) / PadSpacing + 1e-3f) + 1, nz = Mathf.FloorToInt((maxZ - z0) / PadSpacing + 1e-3f) + 1;
        // Each lattice point: its height (NaN off the pad), on the road's end, and off it too deep (a fill past PadMostFill).
        var hs = new float[nx, nz];
        var gs = new float[nx, nz];
        var onRoads = new bool[nx, nz];
        var deep = new bool[nx, nz];
        for (int ix = 0; ix < nx; ix++)
            for (int iz = 0; iz < nz; iz++)
            {
                float x = x0 + ix * PadSpacing, z = z0 + iz * PadSpacing;
                var q = new Vector2(x, z);
                bool onRoad = (q - end).sqrMagnitude <= disc * disc;
                float h = onRoad ? height : float.PositiveInfinity;
                foreach (var u in under)
                {
                    Vector2 d = q - u.back;
                    float along = Vector2.Dot(d, StairLayout.Forward(u.heading)), side = Vector2.Dot(d, StairLayout.Right(u.heading));
                    if (along >= -1e-3f && along <= u.depth + 1e-3f && Mathf.Abs(side) <= u.across / 2f + 1e-3f) h = Mathf.Min(h, u.height);
                }
                hs[ix, iz] = float.IsPositiveInfinity(h) ? float.NaN : h;
                if (float.IsPositiveInfinity(h)) continue;
                float g = gs[ix, iz] = ground(x, z);
                float fill = h - g;
                onRoads[ix, iz] = onRoad;
                deep[ix, iz] = !onRoad && fill > PadMostFill;
            }
        // Off the road's end, too deep points are left as they are, and so are the points beside them (a lattice step away):
        // a pad point's flat core (half its width, a lattice step) would level those to the pad's height too, a lip standing
        // more than PadMostFill over the falling ground.
        bool Deep(int ix, int iz) => ix >= 0 && iz >= 0 && ix < nx && iz < nz && deep[ix, iz];
        for (int ix = 0; ix < nx; ix++)
            for (int iz = 0; iz < nz; iz++)
            {
                float h = hs[ix, iz];
                if (float.IsNaN(h)) continue;
                if (deep[ix, iz] || (!onRoads[ix, iz] && (Deep(ix - 1, iz) || Deep(ix + 1, iz) || Deep(ix, iz - 1) || Deep(ix, iz + 1))))
                {
                    pad.LeftOut++;
                    continue;
                }
                float g = gs[ix, iz];
                pad.MostCut = Mathf.Max(pad.MostCut, g - h);
                pad.MostFill = Mathf.Max(pad.MostFill, h - g);
                pad.Points.Add(new Vector2(x0 + ix * PadSpacing, z0 + iz * PadSpacing));
                pad.Heights.Add(h);
            }
        // The pad as it is stored, measured as every road's earthwork is: a point past the cap refuses the whole pad.
        Vector2? pastAt = null;
        RoadNetworkGenerator.Earthwork earth = RoadNetworkGenerator.EarthworkAcross(pad.ToPath(), q => ground(q.x, q.y), cap, q => pastAt ??= q);
        if (pastAt is Vector2 at)
        {
            why = FormattableString.Invariant($"{PastTheCap} at ({at.x:F1}, {at.y:F1}), {earth.Worst:F1} m");
            return null;
        }
        return pad;
    }

    /// <summary>Why a pad past the earthwork cap is not laid (<see cref="PadUnder(StairPlan, RoadEndRing, bool, Func{float, float, float}, float, out string)"/>).</summary>
    public const string PastTheCap = "past the earthwork cap";
}
