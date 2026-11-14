using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// A staircase's top in the layout (one owner): it arrives facing its road's departure (priced per step off it); finishing near an
/// area it must meet the ground it leads onto; and it is finished onto that ground (FinishTop): climbed on under rising ground,
/// then a floor out onto it, else a crest, or stairs down onto ground below it.
/// </summary>
public static partial class StairLayout
{
    /// <summary>The actual last walked row, facing onward travel. Finish operations may leave End at the old top.</summary>
    internal sealed class TerminalExit
    {
        internal readonly List<StairPiece> Pieces;
        internal readonly List<Vector3> Surface, ForwardEdge;
        internal readonly Vector3 Edge;
        internal readonly Vector2 Direction;
        internal readonly float Width, Depth, High;
        internal readonly bool Descending;
        internal TerminalExit(List<StairPiece> pieces, List<Vector3> surface, List<Vector3> forwardEdge, Vector3 edge, Vector2 direction, float width, float depth, float high, bool descending)
        { Pieces=pieces; Surface=surface; ForwardEdge=forwardEdge; Edge=edge; Direction=direction; Width=width; Depth=depth; High=high; Descending=descending; }

        /// <summary>Whether it stands on <paramref name="ground"/> at its exit edge across its width (<see cref="GroundUnderEdge"/>):
        /// a stair down off a top meets the ground it steps onto (<see cref="FinishTop"/>, RoadStairRuns.RaisedLandingWalk).</summary>
        internal bool Grounded(Func<float, float, float> ground) =>
            GroundUnderEdge(new Vector2(Edge.x, Edge.z), -Direction, Edge.y, Width, ground);

        /// <summary>
        /// Whether this exit, a stair down off a top, comes down onto <paramref name="ground"/> as a walker takes it: every
        /// walking sample at its high edge clear (<paramref name="clear"/>, the caller's own surface rule where it has one; else
        /// no sample under the ground there by more than a centimetre), the ground at its low end's corners no higher than its
        /// high edge, its low edge meeting the ground (StairEnds.MeetsLowEdge) and standing on it (<see cref="Grounded"/>). The
        /// one acceptance of a descending finish, checked before the finish is kept: every top's stair down
        /// (<see cref="FinishTop"/>: a crest, a landing then down, back to back) and a placed approach's onto its raised landing
        /// (RoadStairRuns.RaisedLandingWalk, on the landing's heights). Grounded alone let a stair down stand with its high
        /// edge in the bank on one side.
        /// </summary>
        internal bool StepsDownOnto(Func<float, float, float> ground, Func<Vector3, bool>? clear = null)
        {
            if (!Descending) return false;
            clear ??= s => ground(s.x, s.z) - s.y <= 0.01f;
            Vector2 high = new Vector2(Edge.x, Edge.z) - Direction * Depth;
            var (left, right) = StairEnds.Corners(high, Direction, Width);
            return Surface.Where(s => s.y >= High - 0.01f).All(clear) && Grounded(ground)
                && ground(left.x, left.y) <= High + 0.01f && ground(right.x, right.y) <= High + 0.01f
                && StairEnds.MeetsLowEdge(high, Direction, Edge.y, Width, ground);
        }
    }

    /// <summary>
    /// Whether a top's stair down (<paramref name="plan"/>'s terminal row, descending) comes down onto the ground
    /// (<see cref="TerminalExit.StepsDownOnto"/>, the one acceptance) straight on: in its flight's own direction, back to back
    /// with the flight's last stair or on down the way that stair goes (<see cref="StraightOn"/>), never off a side or a
    /// corner of a floor turned off the flight. Every stair down a top's finish lays is accepted by it (<see cref="FinishTop"/>:
    /// a crest, a landing then down, back to back, the floor then down, a step back's): a mountain cave's approach top at
    /// -771.7,7478.3 ended on a floor turned off its flight, and that floor made a stair down in its place came off the turn's
    /// corner sideways onto the slope.
    /// The stair down onto a placed Location's raised landing (RoadStairRuns.RaisedLandingWalk) is accepted here too, with the
    /// landing's own surface rule as <paramref name="clear"/>.
    /// </summary>
    internal static bool StepsDownStraightOnto(StairPlan plan, Func<float, float, float> ground, Func<Vector3, bool>? clear = null) =>
        Terminal(plan) is { } exit && exit.StepsDownOnto(ground, clear) && StraightOn(plan, exit);

    /// <summary>Whether a descending terminal row (<paramref name="exit"/>) runs in its flight's direction: its stairs' heading
    /// the same as, or the reverse of, the last stair walked before it (<see cref="Footprint"/>'s heading, the way a stair
    /// climbs). With no stair before it (a top that is a floor alone), there is no flight to turn off: true.</summary>
    internal static bool StraightOn(StairPlan plan, TerminalExit exit)
    {
        int first = exit.Pieces.Min(p => plan.Pieces.IndexOf(p));
        int before = first <= 0 ? -1 : plan.Pieces.FindLastIndex(first - 1, first, p => p.Kind == StairPieceKind.Stair && !p.Sunk);
        if (before < 0) return true;
        float off = Mathf.Abs(Wrap(Footprint(exit.Pieces[0]).heading - Footprint(plan.Pieces[before]).heading + 180f) - 180f);
        return off < 0.01f || off > 179.99f;
    }

    /// <summary>A walked piece's exit edge's middle (its far edge, a stair's high edge), the heading it is walked out on and
    /// its walking surface there: where a top ending on it would stop (<see cref="StepBack"/>, RoadStairRuns.StopAtTheLip).</summary>
    internal static (Vector2 edge, float heading, float surface) ExitOf(StairPiece piece)
    {
        var (back, heading, _, depth, height) = Footprint(piece);
        return (back + Forward(heading) * depth, heading, height + (piece.Kind == StairPieceKind.Stair ? PieceRise : 0f));
    }

    internal static TerminalExit? Terminal(StairPlan plan)
    {
        int last=plan.Pieces.FindLastIndex(p=>p.Kind!=StairPieceKind.Support);
        if(last<0)return null;
        StairPiece final=plan.Pieces[last];
        Vector2 dir=Forward(plan.EndHeading),right=Right(plan.EndHeading);
        float Along(Vector3 p)=>p.x*dir.x+p.z*dir.y;
        float Across(Vector3 p)=>p.x*right.x+p.z*right.y;
        var row=new List<StairPiece>();
        // AddRow emits lanes together, with support pieces interleaved. Stop before a different walked row or wedge.
        for(int i=last;i>=plan.SunkPieces;i--)
        {
            StairPiece piece=plan.Pieces[i];
            if(piece.Kind==StairPieceKind.Support)continue;
            if(piece.Kind!=final.Kind || piece.Prefab!=final.Prefab || Mathf.Abs(piece.Position.y-final.Position.y)>0.01f
                || Mathf.Abs(Wrap(piece.YawDegrees-final.YawDegrees+180f)-180f)>0.01f
                || Mathf.Abs(Along(piece.Position)-Along(final.Position))>0.01f)break;
            row.Add(piece);
        }
        if(row.Count==0)return null;
        var surface=row.SelectMany(WalkingSurface).ToList();
        float forward=surface.Max(Along),back=surface.Min(Along);
        var edge=surface.Where(p=>Along(p)>=forward-0.01f).ToList();
        var rear=surface.Where(p=>Along(p)<=back+0.01f).ToList();
        Vector3 centre=new Vector3(edge.Average(p=>p.x),edge.Average(p=>p.y),edge.Average(p=>p.z));
        return new TerminalExit(row,surface,edge,centre,dir,surface.Max(Across)-surface.Min(Across),forward-back,surface.Max(p=>p.y),
            final.Kind==StairPieceKind.Stair && centre.y<rear.Average(p=>p.y)-0.01f);
    }

    /// <summary>How far either side of a stair's low end's middle the ground under it is read (<see cref="GroundUnderEdge"/>,
    /// RoadStairRuns.SpanTheValley): its half width, a piece's at least, a little in from its corners.</summary>
    internal static float ExitHalf(float width) => Mathf.Max(PieceRun, width) / 2f - 0.1f;

    /// <summary>The walking surface at the centre of a walked piece: a stair by its middle (its origin is its base), a floor by its top.</summary>
    internal static float CentreSurface(StairPiece piece) =>
        piece.Position.y + (piece.Kind == StairPieceKind.Stair ? StepMiddle : StairKit.FloorTopOf(piece.Prefab));

    /// <summary>How far over a stair step's base its walking surface is at its middle: half a rise. Add it to a base inline
    /// (<c>base + StepMiddle</c>, never through a float helper that would round the sum before a further term).</summary>
    internal const float StepMiddle = PieceRise / 2f;

    /// <summary>The top walking surface of a walked piece: a stair rises a step from its base, a floor stands at its top.</summary>
    internal static float TopSurface(StairPiece piece) =>
        piece.Position.y + (piece.Kind == StairPieceKind.Stair ? PieceRise : StairKit.FloorTopOf(piece.Prefab));

    /// <summary>How many pieces side by side a row <paramref name="width"/> wide takes: one at least.</summary>
    internal static int Lanes(float width) => Mathf.Max(1, Mathf.RoundToInt(width / PieceRun));

    /// <summary>How far off a walking piece's footprint the point a staircase's end was recorded at may lie and still be on
    /// that piece (StairRailing's AtEnd and <see cref="AddLocationFloor"/>, one rule).</summary>
    public const float EndReach = 0.3f;

    /// <summary>Whether <paramref name="p"/> lies on the footprint from <paramref name="back"/> (its back edge's middle), its
    /// forward <paramref name="dir"/> and right <paramref name="right"/>, <paramref name="depth"/> along and
    /// <paramref name="across"/> wide, or within <paramref name="margin"/> of it.</summary>
    internal static bool OnFootprint(Vector2 back, Vector2 dir, Vector2 right, float depth, float across, Vector2 p, float margin)
    {
        Vector2 d = p - back;
        float along = Vector2.Dot(d, dir), aside = Vector2.Dot(d, right);
        return along >= -margin && along <= depth + margin && Mathf.Abs(aside) <= across / 2f + margin;
    }

    /// <summary>The side of a footprint (from <paramref name="back"/> on <paramref name="heading"/>, <paramref name="depth"/>
    /// along) facing <paramref name="toward"/> most: e, the way out across it Right(heading + 90 e) (0 right, 1 back, 2 left,
    /// 3 front), the first of those on a tie; -1 where <paramref name="toward"/> is its middle. The one choice of a location's
    /// landing's side facing the location (<see cref="AddLocationFloor"/>).</summary>
    internal static int SideFacing(Vector2 back, float heading, float depth, Vector2 toward)
    {
        Vector2 to = toward - (back + Forward(heading) * (depth / 2f));
        if (to.sqrMagnitude < 1e-6f) return -1;
        int best = 0;
        float most = float.NegativeInfinity;
        for (int e = 0; e < 4; e++)
        {
            float dot = Vector2.Dot(Right(Wrap(heading + 90f * e)), to);
            if (dot > most) { most = dot; best = e; }
        }
        return best;
    }

    /// <summary>
    /// Whether a piece on <paramref name="heading"/> keeps to how a staircase arrives at a location's landing
    /// (RoadStairRuns.IntoTheLocation, the one owner): within <see cref="ArrivalSteps"/> of <paramref name="into"/>, the heading that
    /// faces into the location (or, from the root, climbs straight away from it). Held through the arriving flight's last stairs
    /// (<see cref="ArrivalStairs"/>), not its last piece alone: the staircase down to the Mountain tower at -1917,6724 left its
    /// last flight running along the tower's wall and ending into it (it would be better if it turned towards the location), and
    /// on the saved tower's ground one coming down round its side turned onto the landing on two floors after a flight along it.
    /// </summary>
    internal static bool ArrivalHeld(float heading, float into) =>
        Mathf.Abs(Wrap(heading - into + 180f) - 180f) <= ArrivalSteps * HeadingStep + 0.01f;

    /// <summary>How far off facing into the location an arriving flight may head (<see cref="ArrivalHeld"/>): 90 degrees, so it
    /// comes in anywhere from along a tangent to the location's radius up to straight in, and only never faces away from it
    /// (45 degrees, <see cref="AlignSteps"/>, before). Off the root of a staircase laid up from the
    /// landing its first piece may turn as far (<see cref="Route.IntoLocationFromRoot"/>).</summary>
    public const int ArrivalSteps = 4;

    /// <summary>The stairs of the flight arriving at a location's landing held to face into it (<see cref="ArrivalHeld"/>), and
    /// every floor walked after them: held through one stair only, the flight down to the saved tower came along its side on
    /// a run of floors half a metre over its landing and stepped down onto it on the one stair.</summary>
    public const int ArrivalStairs = 2;

    /// <summary>Whether a top arriving at a location's landing (<see cref="Route.IntoLocationAtTop"/>) faces into it: it and every
    /// piece back through its last <see cref="ArrivalStairs"/> stairs (<see cref="ArrivalHeld"/>).</summary>
    private static bool ArrivesIntoTheLocation(Node node, Route route)
    {
        if (!route.IntoLocationAtTop.HasValue) return true;
        float into = Bearing(node.Edge, route.IntoLocationAtTop.Value);
        int stairs = 0;
        for (Node? n = node; n != null && n.Parent != null && !n.Placed; n = n.Parent)
        {
            if (!ArrivalHeld(n.Heading, into)) return false;
            if (n.Kind == StairPieceKind.Stair && ++stairs >= ArrivalStairs) return true;
        }
        return true;
    }

    /// <summary>A piece ending within <see cref="ReachRadius"/> of the top, by how far off the road's departure from the top
    /// it heads (at the top of the 41 m staircase at 5669.6,4239.7 the top should face the other
    /// direction): the flight comes in along the road, as its foot leaves it. A top arriving on a road's end's ring
    /// (Route.TopRing) is priced as a foot leaving it is: by the heading it goes down on, off the road's travel
    /// (RoadEndRing.Price).</summary>
    private static float TopOffRoadPrice(Vector2 edge, float heading, int goal, Route route)
    {
        if (goal != route.Last) return 0f;
        if (route.TopRing is RoadEndRing ring)
        {
            Vector2 exit = edge + Forward(heading) * (PieceRun * route.TopLandings);
            return Vector2.Distance(exit, ring.Centre) <= ring.Radius + PieceRun + RoadEndRing.Delta
                ? ring.Price(Wrap(heading + 180f), RoadEndRing.Turn(Bearing(ring.Centre, exit), ring.Travel)) : 0f;
        }
        return route.TopFacing.HasValue && Vector2.Distance(edge, route.Final) <= ReachRadius
            ? FootOffRoadWeight * Mathf.Round(Mathf.Abs(Wrap(heading - route.TopFacing.Value + 180f) - 180f) / HeadingStep) : 0f;
    }

    /// <summary>
    /// Whether a top finishing anywhere near its aim (an area to reach: a landing, a ledge) leaves the walker on the
    /// ground it leads onto: the ground just past it (<see cref="StairEnds.OverAhead"/>, the end reader's corners) stands no more than a rise and a
    /// step-up over it, which a crest (<see cref="ClimbOn"/> and <see cref="AddDescent"/>) or a step meets. Finishing within reach of the aim and up
    /// to <see cref="StairFitsFrom"/> under it, two tops stopped under the shoulder they led onto (at the
    /// mountain cave entry -9293.7,706.8 and at 3522.0,-5773.6: the top did not fully make it).
    /// </summary>
    internal static bool TopReachesGround(Vector2 edge, float heading, float height, float width, Func<float, float, float> ground) =>
        StairEnds.OverAhead(edge, Forward(heading), height, width, ground) <= PieceRise + StepUp;

    /// <summary>
    /// The end rule's stair down (StairEnds.Case.Reverse): where a staircase's top (<see cref="Terminal"/>'s exit edge, facing
    /// <see cref="StairPlan.EndHeading"/>) stands more than <see cref="StepUp"/> but no more than a ledge over the ground a
    /// floor off it would stand on, one stair on down to that ground, facing back: a ramp from the clearing up onto the top,
    /// left buried where it meets the ground (the 2x2 stone was too high to walk from the landing on to the block, which
    /// could be a set of stairs down into the ground). One row: a top takes at most one reversed stair
    /// (<see cref="FinishTop"/>). Laid off a stair climbed on past the top, it is the crest: up one rise, the same stair turned
    /// round back to back, down onto the lip (at the top of a cantilevered staircase by the mountain cave at 1920,704, which
    /// ended 0.8 m under the plateau's lip: two stone steps in opposite directions at the top of the landing connect it
    /// better). Returns how many pieces and clearances were added.
    /// </summary>
    public static (int pieces, int clearances) AddDescent(StairPlan plan, Func<float, float, float> ground, float width)
    {
        int pieces = plan.Pieces.Count, clearances = plan.Clearances.Count;
        StairKit kit = StairKit.Of(plan.Kit);
        int lanes = Lanes(width);
        TerminalExit? exit=Terminal(plan);
        Vector2 dir=Forward(plan.EndHeading);
        Vector2 edge=exit==null ? new Vector2(plan.End.x,plan.End.z) : new Vector2(exit.Edge.x,exit.Edge.z);
        float top=exit?.Edge.y ?? plan.End.y;
        Vector2 low = edge + dir * PieceRun;
        // Only where a far corner reads the rule's stair down (StairEnds.Drops): never onto ground a player steps onto.
        var (l, r) = StairEnds.Drops(edge, dir, top, width, ground);
        if (StairEnds.CaseOf(l) != StairEnds.Case.Reverse && StairEnds.CaseOf(r) != StairEnds.Case.Reverse) return (0, 0);
        if (RowHeld(kit, kit.Stair, low, Wrap(plan.EndHeading + 180f), top - PieceRise, lanes, ground))
        {
            AddRow(plan, kit, StairPieceKind.Stair, kit.Stair, low, Wrap(plan.EndHeading + 180f), top - PieceRise, lanes, ground, mark: true, cut: false);
            plan.Stairs += lanes;
        }
        return (plan.Pieces.Count - pieces, plan.Clearances.Count - clearances);
    }

    /// <summary>
    /// Whether ground stands under a stair's low end (<paramref name="low"/> its low edge, <paramref name="dir"/> along it
    /// from there, so half a metre to a metre on), from side to side of its width, no more than <see cref="StepUp"/> under
    /// <paramref name="height"/>, the height it stands at there: a walker steps off it onto the ground and it stands on
    /// it, not run out past a lip (<see cref="AddDescent"/>'s stair, <see cref="TerminalExit.Grounded"/>).
    /// </summary>
    internal static bool GroundUnderEdge(Vector2 low, Vector2 dir, float height, float width, Func<float, float, float> ground)
    {
        Vector2 across = new Vector2(dir.y, -dir.x);
        float half = ExitHalf(width);
        foreach (float along in new[] { 0.5f, 1f })
            foreach (float side in new[] { -half, 0f, half })
            {
                Vector2 p = low + dir * along + across * side;
                if (ground(p.x, p.y) < height - StepUp) return false;
            }
        return true;
    }

    /// <summary>
    /// The end rule's floor (StairEnds.Case.Floor): where the ground a floor laid on from a staircase's top
    /// (<see cref="StairPlan.End"/>, facing <see cref="StairPlan.EndHeading"/>) would stand on lies within a step of its
    /// height at both corners of one of its free edges (StairEnds.Lands, the one rule: its exit), that floor, level with the top (at the
    /// mountain cave entry at 1932.2,697.1 there was a gap at the top of the stair run, the plateau 0-0.5 m over the top
    /// edge; one stone_floor_2x2 laid out from the top fixed it). Not where the top is a floor already. One rule for every
    /// staircase's end, a road's or an approach's (<see cref="FinishTop"/>); an edge over a drop is the railing's to rail
    /// (StairRailing.LedgeDrop). Returns the pieces and clearances added.
    /// </summary>
    /// <param name="onward">The floor's edge a stair down is laid off next (<see cref="FloorThenDown"/>): that stair is its exit
    /// (StairEnds.Exit), so the floor need not land by its own edges; its walking surface stands at most a step under the
    /// ground at any corner (RoadStairRuns.SurfaceUnderGroundAllowed), else none is laid and the finish goes on.</param>
    public static (int pieces, int clearances) AddTopFloor(StairPlan plan, Func<float, float, float> ground, float width,
        StairEnds.FloorEdge onward = StairEnds.FloorEdge.None)
    {
        if (!plan.Reached || plan.LastRowKind == StairPieceKind.Landing && plan.LastRowPieces >= 0) return (0, 0);
        Vector2 dir = Forward(plan.EndHeading), edge = new Vector2(plan.End.x, plan.End.z);
        float top = plan.End.y;
        if (!StairEnds.Lands(edge, dir, top, width, ground, onward)) return (0,0);
        // A floor that need not land by its own edges (its exit the stair down off it) carries the walk: its walking surface at
        // most a step under the ground at any corner (RoadStairRuns.SurfaceUnderGroundAllowed, the raised landing's allowance).
        if (onward != StairEnds.FloorEdge.None)
        {
            var d = StairEnds.FloorDrops(edge, dir, top, width, ground);
            foreach (float drop in new[] { d.backLeft, d.backRight, d.farLeft, d.farRight })
                if (!RoadStairRuns.SurfaceUnderGroundAllowed(top, top - drop)) return (0, 0);
        }
        int pieces = plan.Pieces.Count, clearances = plan.Clearances.Count;
        StairKit kit = StairKit.Of(plan.Kit);
        int lanes = Lanes(width);
        if (!RowHeld(kit, kit.Floor, edge, plan.EndHeading, top, lanes, ground)) return (0, 0);
        AddRow(plan, kit, StairPieceKind.Landing, kit.Floor, edge, plan.EndHeading, top, lanes, ground, mark: true, cut: false);
        plan.Landings += lanes;
        return (plan.Pieces.Count - pieces, plan.Clearances.Count - clearances);
    }

    /// <summary>
    /// Where a staircase's foot is a floor on a location's landing (its first walked piece, on the point <paramref name="end"/> it
    /// ends at there), a floor level with it laid out onto the location's ground on its side facing the location
    /// (<paramref name="centre"/>, RoadStairRuns.IntoTheLocation) most, by the end rule's floor (<see cref="AddTopFloor"/>'s):
    /// where a floor off that side lands (StairEnds.Lands, the one reader: on level ground too, the rule's 2x2 at the lower
    /// landing, flush with the location's ground), something holds it (<see cref="RowHeld"/>) and it
    /// stands on no walked piece of the plan. Laid as the foot's own floor, its clearance with it (no cuts, as a top floor's),
    /// first in the walk; kept only where <paramref name="fits"/> says the plan may stand so. In game: a staircase down onto a
    /// stone tower's landing at -1903.4,6717.1 turned square at its foot onto a floor along the landing's edge, its side toward
    /// the tower 0.56 m over a dip and the tower's level ground on past it; a stone_floor_2x2 laid by hand on that side, level
    /// with it, joined them. Returns where the location's ground meets the floor laid (its far edge's middle), or null where none is.
    /// </summary>
    public static Vector2? AddLocationFloor(StairPlan plan, Func<float, float, float> ground, Vector2 end, Vector2 centre, Func<StairPlan, bool> fits)
    {
        if (!plan.Reached) return null;
        int first = plan.Pieces.FindIndex(plan.SunkPieces, p => p.Kind != StairPieceKind.Support && !p.Sunk);
        if (first < 0 || plan.Pieces[first].Kind != StairPieceKind.Landing) return null;
        StairKit kit = StairKit.Of(plan.Kit);
        var (back, heading, across, depth, height) = Footprint(plan.Pieces[first]);
        Vector2 middle = back + Forward(heading) * (depth / 2f);
        // On the end: as the railing finds a location's landing (StairRailing's AtEnd).
        if (!OnFootprint(back, Forward(heading), Right(heading), depth, across, end, EndReach)) return null;
        // Its side facing the location most, as the railing opens it.
        int side = SideFacing(back, heading, depth, centre);
        if (side < 0) return null;
        float facing = Wrap(heading + 90f * (side + 1));
        Vector2 outward = Forward(facing);
        float reach = side % 2 == 0 ? across / 2f : depth / 2f, run = side % 2 == 0 ? depth : across;
        Vector2 edge = middle + outward * reach;
        if (!StairEnds.Lands(edge, outward, height, run, ground)) return null;
        // On no walked piece of the plan (one past that side: the walk goes on across it).
        Vector2 floor = edge + outward * (PieceRun / 2f);
        foreach (StairPiece p in plan.Pieces)
        {
            if (p.Kind == StairPieceKind.Support) continue;
            var (b, h, x, z, _) = Footprint(p);
            if (Footprints(floor, facing, PieceRun, PieceRun, b + Forward(h) * (z / 2f), h, x, z)) return null;
        }
        if (!RowHeld(kit, kit.Floor, edge, facing, height, 1, ground)) return null;
        StairPlan before = plan.Copy(deepPieces: true);
        // Laid as a row would be, then moved first in the walk: the top's last row stays the plan's last (Terminal).
        var laid = new StairPlan { Kit = plan.Kit, MaxFloat = plan.MaxFloat };
        AddRow(laid, kit, StairPieceKind.Landing, kit.Floor, edge, facing, height, 1, ground, mark: true, cut: false);
        plan.Pieces.InsertRange(first, laid.Pieces);
        plan.Clearances.InsertRange(plan.SunkClearances, laid.Clearances);
        if (plan.LastRowPieces >= first) { plan.LastRowPieces += laid.Pieces.Count; plan.LastRowClearances += laid.Clearances.Count; }
        plan.MaxFloat = laid.MaxFloat;
        plan.Landings++;
        if (fits(plan)) return edge + outward * PieceRun;
        plan.Restore(before);
        return null;
    }

    /// <summary>A walked piece standing within this of its height over or under a point still covers it (StairEnds' walk,
    /// RoadStairRuns.TopOntoSurface: the ground past a road's top within this of it is met by the road's earthwork). Two
    /// hand-laid floors: at 1932,697 the ground there 0-0.5 m over the top, at 6465,-1522 up to 0.8 m nearby with the floor
    /// flush with or slightly under the snow where the walker steps off.</summary>
    public const float TopFloorBand = 0.6f;

    /// <summary>How far past a piece's edge the search reads whether its step off is onto a location's landing (Route.OnLanding).</summary>
    public const float ProudReach = 0.5f;

    /// <summary>Most stairs <see cref="ClimbOn"/> adds past a top.</summary>
    public const int MostClimbedOn = 3;

    /// <summary>
    /// The end rule's short top (StairEnds.Case.Short): where the ground a floor off a staircase's top
    /// (<see cref="StairPlan.End"/>, facing <see cref="StairPlan.EndHeading"/>) would stand on stands more than a step over it,
    /// more stairs straight on, up to <see cref="MostClimbedOn"/>, until it does not (StairEnds.At, the one reader): the top
    /// then finishes by the rule there, a floor or the crest's stair down (<see cref="FinishTop"/>). The search finishes
    /// within reach of its aim and up to <see cref="StairFitsFrom"/> under it, and two tops stopped under the shoulder they
    /// led onto (the mountain cave entry at -9293.7,706.8, where the top did not fully make it, 1.1 m under the
    /// shoulder 3 m on; and at 3522.0,-5773.6, 1-3 m under the knoll). Each stair added keeps its walk no deeper than
    /// <paramref name="maxCover"/> in the ground; a dip just past the top under it is its supports' to stand over (an approach's
    /// top at 5706.4,142.92,3591.7, the ground under its edge falling away on one side, took no crest). Moves <see cref="StairPlan.End"/>
    /// to the new top. Returns the pieces and clearances added; <paramref name="stairs"/> how many rows.
    /// </summary>
    public static (int pieces, int clearances) ClimbOn(StairPlan plan, Func<float, float, float> ground, float width, float maxCover, out int stairs)
    {
        stairs = 0;
        int pieces = plan.Pieces.Count, clearances = plan.Clearances.Count;
        if (!plan.Reached) return (0, 0);
        StairKit kit = StairKit.Of(plan.Kit);
        int lanes = Lanes(width);
        Vector2 dir = Forward(plan.EndHeading), edge = new Vector2(plan.End.x, plan.End.z);
        float top = plan.End.y;
        // Off the top as laid, short at either far corner (TopCases); off a stair climbed, by the rule there.
        while (stairs < MostClimbedOn && (stairs == 0 ? Array.IndexOf(TopCases(plan, ground, width), StairEnds.Case.Short) >= 0
                   : StairEnds.At(edge, dir, top, width, ground) == StairEnds.Case.Short))
        {
            bool clear = true;
            for (float along = 0.5f; along < PieceRun; along += 0.5f)
            {
                Vector2 p = edge + dir * along;
                if (ground(p.x, p.y) - (top + PieceRise * along / PieceRun) > maxCover) { clear = false; break; }
            }
            if (!clear) break;
            if (!RowHeld(kit, kit.Stair, edge, plan.EndHeading, top, lanes, ground)) break;
            AddRow(plan, kit, StairPieceKind.Stair, kit.Stair, edge, plan.EndHeading, top, lanes, ground, mark: true, cut: false);
            stairs++;
            edge += dir * PieceRun;
            top += PieceRise;
        }
        if (stairs == 0) return (0, 0);
        plan.Stairs += stairs * lanes;
        plan.Rise += stairs * PieceRise;
        plan.WalkedLength += stairs * PieceRun;
        plan.End = new Vector3(edge.x, top, edge.y);
        return (plan.Pieces.Count - pieces, plan.Clearances.Count - clearances);
    }

    /// <summary>How <see cref="FinishTop"/> finished a top onto the ground past it.</summary>
    public enum TopFinish { None, TopFloor, Crest, LandingThenDown, BackToBack, StepBack, FloorThenDown }

    /// <summary>The end rule's case off a plan's top as laid now (StairEnds.At, the one reader): off its exit edge
    /// (<see cref="StairPlan.End"/>, facing <see cref="StairPlan.EndHeading"/>).</summary>
    internal static StairEnds.Case TopCase(StairPlan plan, Func<float, float, float> ground, float width) => TopCases(plan, ground, width)[0];

    /// <summary>The end rule's cases off a plan's top (<see cref="TopCase"/>): the rule's own (StairEnds.Choose: a floor where
    /// the floor off it lands, one of its free edges with both corners within a step, StairEnds.Exit), and where the two far
    /// corners read different cases, the other corner's after it (<see cref="Cases"/>): tried in that order
    /// (<see cref="FinishTop"/>).</summary>
    internal static StairEnds.Case[] TopCases(StairPlan plan, Func<float, float, float> ground, float width)
    {
        var drops = TopsFloor(plan)
            ? StairEnds.FloorDrops(plan.LastRowStart, Forward(plan.LastRowHeading), plan.LastRowHeight, width, ground)
            : StairEnds.FloorDrops(new Vector2(plan.End.x, plan.End.z), Forward(plan.EndHeading), plan.End.y, width, ground);
        return Cases(drops);
    }

    /// <summary>The cases tried off an end for its floor's four corners' drops (StairEnds.FloorDrops): the rule's own
    /// (StairEnds.Choose), and where the far corners read different cases, the other far corner's after it; a top's
    /// (<see cref="TopCases"/>) and a foot's (<see cref="FootCases"/>) alike.</summary>
    internal static StairEnds.Case[] Cases((float backLeft, float backRight, float farLeft, float farRight) drops)
    {
        StairEnds.Case rule = StairEnds.Choose(drops, out bool disagree);
        if (!disagree) return new[] { rule };
        StairEnds.Case a = StairEnds.CaseOf(drops.farLeft), b = StairEnds.CaseOf(drops.farRight);
        StairEnds.Case other = a == rule ? b : a;
        return other == rule ? new[] { rule } : new[] { rule, other };
    }

    /// <summary>Whether a plan's top is a floor row laid by its search (read at its own far corners: it is the floor the
    /// rule would lay, StairEnds.Lands).</summary>
    private static bool TopsFloor(StairPlan plan) => plan.LastRowKind == StairPieceKind.Landing && plan.LastRowPieces >= 0;

    /// <summary>
    /// The one owner of a staircase's top finished onto the ground past it, for every top (a road's, RoadStairRuns.TopOntoSurface;
    /// an approach's, RoadStairRuns.FinishOverLedge): the end rule read off the top (StairEnds, the one reader: the drop at
    /// the far corners of the floor its exit edge would take) lays exactly one finish; where the two corners read different
    /// cases and the rule's finish is not laid or does not fit, the other corner's (a floor that would sink into the bank on
    /// one side, a climb on; <see cref="TopCases"/>). Short (the ground over it by more than a
    /// step): more stairs straight on (<see cref="ClimbOn"/>, its walk no deeper than <paramref name="cover"/> in the ground)
    /// until it is not, then the rule there, both corners' cases in turn, a floor or a stair down off the stairs climbed (the
    /// crest); the climb is judged with the finish laid after it (a climb that alone stops short of where the walker steps
    /// off, finished by its crest), and kept alone only where it fits so. A floor (<see cref="AddTopFloor"/>). With
    /// <paramref name="down"/> (a top over ground it steps down onto: an approach's): over the ground by up to a ledge, a stair
    /// down onto it (<see cref="AddDescent"/>), or its last floor made one (<see cref="StepDownTop"/>), each standing on the
    /// ground at its low end; past a ledge the stair overshot, taken off and a floor laid off the one before it where that
    /// floor lands (<see cref="StepBack"/>). Each is kept only where <paramref name="fits"/> says the plan may stand so (an
    /// approach: out of its location's pit and rock). With <paramref name="wayOff"/> (every top's: the end reader's way off it,
    /// RoadStairRuns.WayOffItsTop), a floor laid that has none (within a step of the ground at one far corner only) is not kept.
    /// A floor case whose floor is not kept (not laid, not fitting or with no way off) tries the stair down instead, where a
    /// stair down may be laid (off stairs climbed on, or with <paramref name="down"/>), landing first (<see cref="FinishCase"/>):
    /// the floor and a stair down off its far edge onto the ground past it, then a stair down in place of the floor; then
    /// more floors, a step back, the next case, and with none the top as laid. Returns the finish laid, if any; with none the plan is as it was but for a
    /// climb kept.
    /// The one owner of staircase ends (docs/OWNERS.md).
    /// </summary>
    public static TopFinish FinishTop(StairPlan plan, Func<float, float, float> ground, float width, float cover, Func<StairPlan, bool> fits,
        out bool climbed, bool down = false, Func<StairPlan, bool>? wayOff = null)
    {
        StairEnds.Case[] cases = TopCases(plan, ground, width);
        StairPlan start = plan.Copy(deepPieces: true);
        climbed = false;
        if (Trace != null) TraceInput(plan, cases, ground, width, cover);
        // What a finish adds stands clear of the staircase's own pieces, seen from above (OverlapsItself, the search's rule),
        // before the caller's own checks: a finish laid over its own flight is no finish.
        int laid = plan.Pieces.Count;
        Func<StairPlan, bool> fitting = p => !OverlapsItself(p, laid) && fits(p);
        if (Trace != null) fitting = p => { bool clear = !OverlapsItself(p, laid), ok = clear && fits(p); Say(ok ? "fit ok" : clear ? "fit refused" : "fit refused: over its own pieces"); return ok; };
        for (int i = 0; i < cases.Length; i++)
        {
            if (i > 0) plan.Restore(start);
            TopFinish finish = FinishBy(cases[i], plan, ground, width, cover, fitting, out climbed, down, wayOff);
            if (finish != TopFinish.None) { Say("finished " + finish); return finish; }
        }
        Say("None: " + (cases.Length > 1 ? "neither case" : "the case") + " finished");
        return TopFinish.None;
    }

    /// <summary>The lines a finish traces (RoadStairRuns.FinishOverLedge sets it, with the roads log on, and logs them); null
    /// where nothing is traced. A trace reads the plan and never changes it.</summary>
    [ThreadStatic] internal static List<string>? Trace;

    private static void Say(string line) => Trace?.Add(line);

    private static string N(float v) => v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The input state of a finish, one trace line (<see cref="Trace"/>).</summary>
    private static void TraceInput(StairPlan plan, StairEnds.Case[] cases, Func<float, float, float> ground, float width, float cover)
    {
        var (l, r) = TopsFloor(plan)
            ? StairEnds.Drops(plan.LastRowStart, Forward(plan.LastRowHeading), plan.LastRowHeight, width, ground)
            : StairEnds.Drops(new Vector2(plan.End.x, plan.End.z), Forward(plan.EndHeading), plan.End.y, width, ground);
        TerminalExit? exit = Terminal(plan);
        string terminal = exit == null ? "none"
            : $"{exit.Pieces[0].Kind} x{exit.Pieces.Count} {(exit.Descending ? "descending" : "ascending")} edge ({N(exit.Edge.x)}, {N(exit.Edge.y)}, {N(exit.Edge.z)})";
        Say($"input: kit {plan.Kit}, terminal {terminal}, last row {plan.LastRowKind} x{plan.LastRowPieceCount} ({(TopsFloor(plan) ? "a floor" : "not a floor")}), "
            + $"heading {N(plan.EndHeading)}, top {N(plan.End.y)}, corner drops {N(l)}/{N(r)}, cases {string.Join(",", cases)}, cover {N(cover)}");
    }

    /// <summary>The finish the end rule lays for <paramref name="rule"/> (<see cref="FinishTop"/>): off a top short of the
    /// ground, the stairs climbed on and then each case read off them (<see cref="TopCases"/>) in turn.</summary>
    private static TopFinish FinishBy(StairEnds.Case rule, StairPlan plan, Func<float, float, float> ground, float width, float cover,
        Func<StairPlan, bool> fits, out bool climbed, bool down, Func<StairPlan, bool>? wayOff)
    {
        climbed = false;
        StairPlan before = plan.Copy(deepPieces: true);
        Say("case " + rule);
        if (rule != StairEnds.Case.Short) return FinishCase(rule, plan, ground, width, fits, climbed: false, down, wayOff);
        ClimbOn(plan, ground, width, cover, out int stairs);
        Say($"climb-on laid {stairs} stair(s)");
        if (stairs == 0) { Say("no climb-on kept"); return TopFinish.None; }
        climbed = true;
        // The climb is judged with what is laid after it: a climb whose top alone stops short of where the walker steps off
        // (an approach's raised landing's lip, 0.8 m over it) is finished by its crest's stair down onto it. Alone it is kept
        // only where it fits so.
        bool fitsAlone = fits(plan);
        StairEnds.Case[] after = TopCases(plan, ground, width);
        Say($"after the climb: cases {string.Join(",", after)}, top {N(plan.End.y)}{(fitsAlone ? "" : ", the climb alone refused")}");
        StairPlan climb = plan.Copy(deepPieces: true);
        for (int i = 0; i < after.Length; i++)
        {
            if (i > 0) plan.Restore(climb);
            TopFinish finish = FinishCase(after[i], plan, ground, width, fits, climbed: true, down, wayOff);
            if (finish != TopFinish.None) return finish;
        }
        plan.Restore(fitsAlone ? climb : before);
        climbed = fitsAlone;
        Say(fitsAlone ? "the climb kept, no finish after it" : "climb-on taken off");
        return TopFinish.None;
    }

    /// <summary>The finishes a case tries (<see cref="FinishCase"/>).</summary>
    private enum Try { Floor, DownInPlace, FloorThenDown, FloorsOn, StepBack }

    /// <summary>
    /// The finish for one case off a top as it now stands (<see cref="FinishBy"/>): the case's own finish first, and where it
    /// is not laid, does not fit or (a floor) has no way off it (<paramref name="wayOff"/>), the next in its order, as the
    /// finisher fell through before one finish per case (5 Nov 2026). Landing first (the owner's "up flight, a 2x2 landing,
    /// then the stair down; back to back only where there is no room"): a floor; the floor and a stair down off its far edge
    /// (<see cref="FloorThenDown"/>); a stair down in place of the floor, back to back (<see cref="StairDown"/>); more level
    /// floors on (<see cref="FloorsOn"/>); a step back (<see cref="StepBack"/>). A stair down's case the same from the floor
    /// and a stair down on, more floors before a step back (the owner, 12 Nov 2026: "more floors first"); a step back's: the
    /// step back (and more floors on from its floor),
    /// then the floor and a stair down, then a stair down in place. A stair down only where one may be laid (off stairs climbed
    /// on, or with <paramref name="down"/>), a step back only with <paramref name="down"/> and nothing climbed. The plan is
    /// as it was where none is laid. A top that is a floor already (its search's) with a way off is kept as it is.
    /// </summary>
    private static TopFinish FinishCase(StairEnds.Case rule, StairPlan plan, Func<float, float, float> ground, float width,
        Func<StairPlan, bool> fits, bool climbed, bool down, Func<StairPlan, bool>? wayOff)
    {
        if (rule == StairEnds.Case.Floor && TopsFloor(plan) && (wayOff == null || wayOff(plan))) { Say("the top is a floor with a way off"); return TopFinish.None; }
        Try[] order = rule switch
        {
            StairEnds.Case.Floor => new[] { Try.Floor, Try.FloorThenDown, Try.DownInPlace, Try.FloorsOn, Try.StepBack },
            StairEnds.Case.Reverse => new[] { Try.FloorThenDown, Try.DownInPlace, Try.FloorsOn, Try.StepBack },
            StairEnds.Case.StepBack => new[] { Try.StepBack, Try.FloorThenDown, Try.DownInPlace },
            _ => Array.Empty<Try>(),
        };
        bool mayDown = climbed || down, mayStepBack = down && !climbed;
        StairPlan before = plan.Copy(deepPieces: true);
        foreach (Try t in order)
        {
            if (t is Try.DownInPlace or Try.FloorThenDown && !mayDown || t == Try.StepBack && !mayStepBack) continue;
            plan.Restore(before);
            TopFinish laid = t switch
            {
                Try.Floor => FloorFinish(plan, ground, width, fits, wayOff),
                Try.DownInPlace => StairDown(plan, ground, width, fits, climbed, floorNotKept: rule == StairEnds.Case.Floor),
                Try.FloorThenDown => FloorThenDown(plan, ground, width, fits),
                Try.FloorsOn => FloorsOn(plan, ground, width, fits, wayOff, laidOff: false),
                _ => StepBackFinish(plan, ground, width, fits, wayOff),
            };
            if (laid != TopFinish.None) return laid;
        }
        if (order.Length == 0) Say($"no finish for case {rule} (down {down}, climbed {climbed})");
        plan.Restore(before);
        return TopFinish.None;
    }

    /// <summary>The rule's floor (<see cref="AddTopFloor"/>), kept where it fits and has a way off it.</summary>
    private static TopFinish FloorFinish(StairPlan plan, Func<float, float, float> ground, float width, Func<StairPlan, bool> fits,
        Func<StairPlan, bool>? wayOff)
    {
        if (AddTopFloor(plan, ground, width).pieces == 0 || !fits(plan)) { Say("floor not laid or refused"); return TopFinish.None; }
        if (wayOff == null || wayOff(plan)) return TopFinish.TopFloor;
        Say("TopFloor: no way off it");
        return TopFinish.None;
    }

    /// <summary>The step back (<see cref="StepBack"/>), its floor kept where it fits and has a way off it, else more level floors
    /// on from it (<see cref="FloorsOn"/>): "1-2 fewer stairs up and 1-2 more level 2x2 floors".</summary>
    private static TopFinish StepBackFinish(StairPlan plan, Func<float, float, float> ground, float width, Func<StairPlan, bool> fits,
        Func<StairPlan, bool>? wayOff)
    {
        if (!StepBack(plan, ground, width, out bool descended)) { Say("step back: " + (LastStepBack ?? "not laid")); return TopFinish.None; }
        if (fits(plan) && (wayOff == null || wayOff(plan))) return TopFinish.StepBack;
        if (descended) { Say("step back: its stair down not kept"); return TopFinish.None; }
        Say("step back: its floor not kept");
        return FloorsOn(plan, ground, width, fits, wayOff, laidOff: true) != TopFinish.None ? TopFinish.StepBack : TopFinish.None;
    }

    /// <summary>The end rule's stair down off a top as it stands (StairEnds.Case.Reverse): off stairs climbed on, the crest;
    /// off the top as laid, a stair down in place of its floor (<see cref="StepDownTop"/>) or off its stair
    /// (<see cref="AddDescent"/>). Each kept only where it comes down onto the ground (TerminalExit.StepsDownOnto) and fits. The
    /// plan is left changed where none is kept: the caller restores it.</summary>
    private static TopFinish StairDown(StairPlan plan, Func<float, float, float> ground, float width, Func<StairPlan, bool> fits, bool climbed,
        bool floorNotKept = false)
    {
        bool Grounded() => StepsDownStraightOnto(plan, ground);
        if (climbed)
        {
            if (AddDescent(plan, ground, width).pieces > 0 && Grounded() && fits(plan))
            {
                // The crest is the last stair climbed and its stair down: the top stays where it was before that stair,
                // as a crest's has (the down stair's low end on the lip; TopOntoSurface reads it off Terminal).
                Vector2 back = new Vector2(plan.End.x, plan.End.z) - Forward(plan.EndHeading) * PieceRun;
                plan.End = new Vector3(back.x, plan.End.y - PieceRise, back.y);
                return TopFinish.Crest;
            }
            Say("crest: stair down not laid, not grounded or refused");
            return TopFinish.None;
        }
        bool onFloor = TopsFloor(plan);
        if (!(onFloor ? StepDownTop(plan, ground, width, floorNotKept) : AddDescent(plan, ground, width).pieces > 0)) { Say("stair down not laid"); return TopFinish.None; }
        if (!Grounded()) { Say(Trace == null ? "" : "stair down not onto the ground: " + DownLine(plan, ground)); return TopFinish.None; }
        if (fits(plan)) return onFloor ? TopFinish.BackToBack : TopFinish.LandingThenDown;
        Say("stair down refused");
        return TopFinish.None;
    }

    /// <summary>Most level floors <see cref="FloorsOn"/> lays on past a top's floor that is not kept: "1-2 more level 2x2
    /// floors, which should get within our limits" (the owner's, 12 Nov 2026, of an approach's top whose one floor stood over the ground
    /// past it at a raised landing's edge).</summary>
    public const int MoreTopFloors = 2;

    /// <summary>
    /// Where a top's floor is not kept (it does not land, does not fit or has no way off it) and no stair down is
    /// (<see cref="FinishCase"/>), level floors laid on past it, straight on, up to <see cref="MoreTopFloors"/> more, each
    /// only where it lands by the one reader (StairEnds.Lands, as <see cref="AddTopFloor"/> lays one) and something holds it:
    /// kept at the first count that fits and has a way off (<paramref name="wayOff"/>). From a top that is a floor already (its
    /// search's: <see cref="StairPlan.End"/> its far edge), on from it; from a stair, the rule's floor then more; with
    /// <paramref name="laidOff"/>, on from the floor just laid off <see cref="StairPlan.End"/> (<see cref="StepBack"/>'s).
    /// <see cref="StairPlan.End"/> is left at the last floor's back edge, as <see cref="AddTopFloor"/> leaves it. Returns
    /// <see cref="TopFinish.TopFloor"/>, or None with the plan as it was.
    /// </summary>
    private static TopFinish FloorsOn(StairPlan plan, Func<float, float, float> ground, float width, Func<StairPlan, bool> fits,
        Func<StairPlan, bool>? wayOff, bool laidOff)
    {
        if (!plan.Reached) return TopFinish.None;
        StairPlan before = plan.Copy(deepPieces: true);
        StairKit kit = StairKit.Of(plan.Kit);
        int lanes = Lanes(width);
        Vector2 dir = Forward(plan.EndHeading), edge = new Vector2(plan.End.x, plan.End.z);
        float top = plan.End.y;
        int most = MoreTopFloors;
        if (laidOff) edge += dir * PieceRun;
        else if (!TopsFloor(plan))
        {
            if (AddTopFloor(plan, ground, width).pieces == 0) { Say("floors on: the first not laid"); return TopFinish.None; }
            edge += dir * PieceRun;
        }
        for (int k = 1; k <= most; k++)
        {
            if (!StairEnds.Lands(edge, dir, top, width, ground) || !RowHeld(kit, kit.Floor, edge, plan.EndHeading, top, lanes, ground))
            { Say($"floors on: floor {k} more does not land"); break; }
            AddRow(plan, kit, StairPieceKind.Landing, kit.Floor, edge, plan.EndHeading, top, lanes, ground, mark: true, cut: false);
            plan.Landings += lanes;
            plan.End = new Vector3(edge.x, top, edge.y);
            if (fits(plan) && (wayOff == null || wayOff(plan))) { Say($"floors on: {k} more"); return TopFinish.TopFloor; }
            Say($"floors on: {k} more not kept");
            edge += dir * PieceRun;
        }
        plan.Restore(before);
        return TopFinish.None;
    }

    /// <summary>A stair down's reading for the trace (<see cref="Trace"/>): its high and low edges' heights and the ground at
    /// its low end's corners and middle.</summary>
    private static string DownLine(StairPlan plan, Func<float, float, float> ground)
    {
        TerminalExit? exit = Terminal(plan);
        if (exit == null) return "no terminal";
        Vector2 low = new Vector2(exit.Edge.x, exit.Edge.z), high = low - exit.Direction * exit.Depth;
        var (l, r) = StairEnds.Corners(high, exit.Direction, exit.Width);
        return $"high {N(exit.High)} (ground {N(ground(high.x, high.y))}), low {N(exit.Edge.y)} (ground {N(ground(low.x, low.y))}), "
            + $"far corners {N(ground(l.x, l.y))}/{N(ground(r.x, r.y))}, grounded {exit.Grounded(ground)}, low edge met {StairEnds.MeetsLowEdge(high, exit.Direction, exit.Edge.y, exit.Width, ground)}, straight on {StraightOn(plan, exit)}";
    }

    /// <summary>
    /// The floor and a stair down off its far edge onto the ground past it (<see cref="AddTopFloor"/> then
    /// <see cref="AddDescent"/>, read by the one reader off that edge: over the ground there by more than a step and up to a
    /// ledge): "flight, 2x2 landing, then the stair down" (the owner, 25 Oct 2026). The stair down is the floor's exit
    /// (StairEnds.Exit's onward edge): the floor need not land by its own edges, it carries the walk to the stair, which lands by
    /// its low edge. A floor the top already ends on (its search's) is kept and the stair down laid off it. Kept only where its
    /// stair comes down onto the ground (TerminalExit.StepsDownOnto) and the plan fits; the caller restores the plan where not.
    /// </summary>
    private static TopFinish FloorThenDown(StairPlan plan, Func<float, float, float> ground, float width, Func<StairPlan, bool> fits)
    {
        Say("the floor and a stair down off it");
        bool floored = TopsFloor(plan) || AddTopFloor(plan, ground, width, StairEnds.FloorEdge.Front).pieces > 0;
        if (!floored || AddDescent(plan, ground, width).pieces == 0) Say("floor then down: not laid");
        else if (!StepsDownStraightOnto(plan, ground)) Say(Trace == null ? "" : "floor then down: not onto the ground: " + DownLine(plan, ground));
        else if (fits(plan)) return TopFinish.FloorThenDown;
        else Say("floor then down: refused");
        return TopFinish.None;
    }

    /// <summary>
    /// The end rule's step back (StairEnds.Case.StepBack): where the last stair of a top stands more than
    /// <see cref="StairRailing.LedgeDrop"/> over the ground its floor would take (it overshot), stairs taken off it one by one
    /// (<see cref="TrimAbove"/>) until a floor laid off the walked piece then last lands (StairEnds.Lands, the one rule: a free
    /// edge of it with both corners within a step), and that floor laid (<see cref="AddTopFloor"/>): "keep stepping back until
    /// the floor lands"; or, where that floor does not land but the stair stands within a ledge over the ground past it (the
    /// rule's stair down there, "land within StepUp/LedgeDrop"), a stair down laid off it (<see cref="AddDescent"/>) where it
    /// comes down onto the ground (<see cref="TerminalExit.StepsDownOnto"/>), <paramref name="descended"/>. Bounded by the
    /// staircase's own walk: never back past its first stair, nor past a turn (a piece turned off the flight, or a turn's
    /// filler) or a rest (a floor, tried as the last one); none landing before that, the top stays as laid
    /// (<see cref="LastStepBack"/> says why). Returns whether it did; the caller restores the plan where it does not fit.
    /// </summary>
    internal static bool StepBack(StairPlan plan, Func<float, float, float> ground, float width, out bool descended)
    {
        // Landing first: a floor that lands anywhere back along the flight, before a stair down.
        descended = false;
        StairPlan before = plan.Copy(deepPieces: true);
        if (StepBackTo(plan, ground, width, down: false)) return true;
        plan.Restore(before);
        string? why = LastStepBack;
        if (StepBackTo(plan, ground, width, down: true)) { descended = true; return true; }
        plan.Restore(before);
        LastStepBack = why;
        return false;
    }

    /// <summary>One pass of <see cref="StepBack"/>: back to the first stair whose floor lands, or with <paramref name="down"/>
    /// whose stair down comes down onto the ground.</summary>
    private static bool StepBackTo(StairPlan plan, Func<float, float, float> ground, float width, bool down)
    {
        LastStepBack = null;
        TerminalExit? exit = Terminal(plan);
        if (exit == null || exit.Descending || exit.Pieces[0].Kind != StairPieceKind.Stair) return false;
        Vector2 edge = new Vector2(exit.Edge.x, exit.Edge.z);
        if (StairEnds.At(edge, exit.Direction, exit.Edge.y, width, ground) != StairEnds.Case.StepBack) return false;
        int first = exit.Pieces.Min(p => plan.Pieces.IndexOf(p));
        if (first <= plan.SunkPieces) return false;
        StairPiece flight = exit.Pieces[0];
        int firstStair = plan.Pieces.FindIndex(plan.SunkPieces, p => p.Kind == StairPieceKind.Stair && !p.Sunk);
        int from = first, stepped = 0;
        while (true)
        {
            int previous = plan.Pieces.FindLastIndex(from - 1, from - plan.SunkPieces, p => p.Kind != StairPieceKind.Support);
            if (previous < plan.SunkPieces) { LastStepBack = "no walked piece before it"; return false; }
            StairPiece piece = plan.Pieces[previous];
            // The same row's other lane: on to the row before it.
            if (piece.Kind == StairPieceKind.Stair && Mathf.Abs(piece.Position.y - plan.Pieces[from].Position.y) < 0.01f
                && Mathf.Abs(Wrap(piece.YawDegrees - flight.YawDegrees + 180f) - 180f) < 1f) { from = previous; continue; }
            // A turn's filler, or a stair turned off the flight: the bound.
            if (piece.Kind is not (StairPieceKind.Stair or StairPieceKind.Landing)
                || piece.Kind == StairPieceKind.Stair && Mathf.Abs(Wrap(piece.YawDegrees - flight.YawDegrees + 180f) - 180f) >= 1f)
            { LastStepBack = FormattableString.Invariant($"stepped back {stepped} stair(s) to a turn, no floor landing"); return false; }
            var (at, heading, surface) = ExitOf(piece);
            if (!down && StairEnds.Lands(at, Forward(heading), surface, width, ground))
            {
                StairKit kit = StairKit.Of(plan.Kit);
                if (TrimAbove(plan, previous, false, kit) == 0) return false;
                plan.End = new Vector3(at.x, surface, at.y);
                plan.EndHeading = heading;
                return AddTopFloor(plan, ground, width).pieces > 0;
            }
            if (down && piece.Kind == StairPieceKind.Stair && StairEnds.At(at, Forward(heading), surface, width, ground) == StairEnds.Case.Reverse)
            {
                StairPlan before = plan.Copy(deepPieces: true);
                StairKit kit = StairKit.Of(plan.Kit);
                if (TrimAbove(plan, previous, false, kit) > 0)
                {
                    plan.End = new Vector3(at.x, surface, at.y);
                    plan.EndHeading = heading;
                    if (AddDescent(plan, ground, width).pieces > 0 && StepsDownStraightOnto(plan, ground)) return true;
                }
                plan.Restore(before);
            }
            stepped++;
            // A rest, or the staircase's first stair: the bound.
            if (piece.Kind == StairPieceKind.Landing || previous <= firstStair)
            { LastStepBack = FormattableString.Invariant($"stepped back {stepped} stair(s) to its {(piece.Kind == StairPieceKind.Landing ? "rest" : "first stair")}, no floor landing"); return false; }
            from = previous;
        }
    }

    /// <summary>Why the last <see cref="StepBack"/> on this thread laid nothing where it tried (null where it did, or never
    /// stepped): for the finish's log line.</summary>
    [ThreadStatic] internal static string? LastStepBack;

    /// <summary>
    /// Where a staircase ends on a floor whose surface stands more than <see cref="StepUp"/> over
    /// the ground just past it, that floor becomes a stair down, in its own location: its back edge
    /// at the floor's height, its front a rise lower, buried where it meets the ground. Nothing
    /// is laid further on, so it cannot reach a location's pit or rock (at Hildir's cave the last 2x2 is probably not
    /// needed, or is replaced with stairs going down into the landing). Returns whether it did. Only for the last row emitted (nothing after it).
    /// </summary>
    public static bool StepDownTop(StairPlan plan, Func<float, float, float> ground, float width, bool notKept = false)
    {
        if (plan.LastRowPieces < 0 || plan.LastRowKind != StairPieceKind.Landing) return false;
        Vector2 dir = Forward(plan.LastRowHeading);
        Vector2 front = plan.LastRowStart + dir * PieceRun;
        // By the one rule at its own corners, as a top floor is laid (StairEnds.Lands): read at one point a metre past its
        // middle, a cave's approach's last floor at 5046.8,-5892.6 stood 0.3-0.6 m over the ground across its way off and was
        // kept; walkable off one side only, replaced by hand with a stair down in its place.
        // A floor of the floor's case not kept (notKept, FinishTop's FinishCase: not fitting, or no way off it) is made a
        // stair down all the same.
        if (!notKept && StairEnds.Lands(plan.LastRowStart, dir, plan.LastRowHeight, width, ground)) return false;
        StairKit kit = StairKit.Of(plan.Kit);
        int lanes = Lanes(width);
        if (!RowHeld(kit, kit.Stair, front, Wrap(plan.LastRowHeading + 180f), plan.LastRowHeight - PieceRise, lanes, ground)) return false;
        // Only the row's own pieces (and its columns): supports sunk later for other pieces stay.
        plan.Pieces.RemoveRange(plan.LastRowPieces, plan.LastRowPieceCount);
        plan.Clearances.RemoveRange(plan.LastRowClearances, plan.LastRowClearanceCount);
        plan.LastRowPieces = plan.Pieces.Count; plan.LastRowClearances = plan.Clearances.Count;
        AddRow(plan, kit, StairPieceKind.Stair, kit.Stair, front, Wrap(plan.LastRowHeading + 180f), plan.LastRowHeight - PieceRise, lanes, ground, mark: true, cut: false);
        plan.Landings -= lanes; plan.Stairs += lanes;
        plan.LastRowKind = StairPieceKind.Stair;
        plan.LastRowStart = front; plan.LastRowHeading = Wrap(plan.LastRowHeading + 180f); plan.LastRowHeight -= PieceRise;
        plan.LastRowPieceCount = plan.Pieces.Count - plan.LastRowPieces;
        plan.LastRowClearanceCount = plan.Clearances.Count - plan.LastRowClearances;
        plan.End = new Vector3(front.x, plan.LastRowHeight, front.y);
        return true;
    }

}
