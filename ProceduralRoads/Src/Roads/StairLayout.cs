using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

public enum StairPieceKind
{
    Stair,    // one stone_stair: 2 m along, 1 m up
    Landing,  // one stone_floor_2x2, level with the top of the stair before it
    Filler,   // covers the wedge a turn leaves on its outside
    Support,  // stone_pillar stacked from under a piece down into the ground
}

/// <summary>One piece of a planned staircase. Position is the prefab's origin.</summary>
public sealed class StairPiece
{
    public StairPieceKind Kind;
    public string Prefab = "";
    public Vector3 Position;
    public float YawDegrees;
    /// <summary>A walked piece held out from a face, or part of what holds it (StairLayout.AddCantilever):
    /// a support so marked is clipped into the face beside its piece, not a column down to the ground.</summary>
    public bool Cantilevered;
}

/// <summary>
/// Space a walker on the stair needs clear of rock: one stretch of a piece,
/// from its underside to <see cref="StairLayout.ClearanceHeight"/> above its
/// walking surface. <see cref="Centre"/> and <see cref="Half"/> are in the
/// box's own frame, turned by <see cref="YawDegrees"/> (local +z along the walk).
/// </summary>
public readonly struct StairClearance
{
    public readonly Vector3 Centre;
    public readonly Vector3 Half;
    public readonly float YawDegrees;
    public readonly float Surface;

    public StairClearance(Vector3 centre, Vector3 half, float yawDegrees, float surface)
    {
        Centre = centre; Half = half; YawDegrees = yawDegrees; Surface = surface;
    }
}

/// <summary>
/// The top row of a staircase already laid, for another to carry on from: its
/// far edge (the snap edge the next piece's back edge meets), the heading it
/// walks and its walking surface there.
/// </summary>
public readonly struct StairContinuation
{
    public readonly Vector2 Edge;
    public readonly float Heading;
    public readonly float Height;
    public readonly StairPieceKind Kind;

    public StairContinuation(Vector2 edge, float heading, float height, StairPieceKind kind)
    {
        Edge = edge; Heading = heading; Height = height; Kind = kind;
    }

    /// <summary>The row's back edge: where it starts, a piece's run before its far edge.</summary>
    public Vector2 Start => Edge - StairLayout.Forward(Heading) * StairLayout.PieceRun;
}

public sealed class StairPlan
{
    /// <summary>A copy: every field as it is, its three lists new (their pieces are never changed once laid out, only
    /// added or taken away). Memberwise, so a field added to StairPlan is copied too.</summary>
    internal StairPlan Copy()
    {
        var c = (StairPlan)MemberwiseClone();
        c.Pieces = new List<StairPiece>(Pieces);
        c.Clearances = new List<StairClearance>(Clearances);
        c.Cuts = new List<Vector3>(Cuts);
        return c;
    }

    /// <summary>The <see cref="StairKit.Name"/> it is built of.</summary>
    public string Kit = "";
    /// <summary>The heading the climb sets off on from its foot, and the one it leaves its top on.</summary>
    public float StartHeading, EndHeading;
    public List<StairPiece> Pieces { get; private set; } = new();
    /// <summary>What must be clear of rock before the pieces go down.</summary>
    public List<StairClearance> Clearances { get; private set; } = new();
    /// <summary>Points where the ground covers a walking surface, each with the
    /// height (y) to lower the ground to: a small earthwork that exposes a
    /// buried stretch, applied as a level of radius <see cref="StairLayout.CutRadius"/>.</summary>
    public List<Vector3> Cuts { get; private set; } = new();
    /// <summary>The deepest cut, metres.</summary>
    public float MaxCut;
    public bool Reached;
    /// <summary>Search steps the layout spent, every leg and backtrack together.</summary>
    public int Expansions;
    public string Reason = "";
    /// <summary>True when the request ran downhill and was laid out from the other end.</summary>
    public bool Reversed;
    /// <summary>Waypoints of the coarse route the search followed (1 when it went straight for the goal).</summary>
    public int Waypoints = 1;
    /// <summary>Times a leg was undone because the next one found nothing that fits.</summary>
    public int Backtracks;
    public int Stairs;
    public int Landings;
    public int Turns;
    /// <summary>Sum of every turn's size in 22.5 degree steps, switchbacks' half-turns left out.</summary>
    public int TurnSteps;
    /// <summary>The part of <see cref="TurnSteps"/> taken floor to floor, overlapping, with no filler.</summary>
    public int OverlapTurnSteps;
    public int Switchbacks;
    public float Rise;
    public float WalkedLength;
    /// <summary>Largest gap between a piece's underside and the ground below its centre, before supports
    /// (pieces held out from a face on cantilevers aside).</summary>
    public float MaxFloat;
    /// <summary>Largest depth the ground stands above a walking surface anywhere it was sampled.</summary>
    public float MaxBuried;
    /// <summary>Walked rows held out from a steep face on a cantilever (pillars, arches, a block
    /// beside), not stood on columns.</summary>
    public int Cantilevered;
    /// <summary>Its first walked piece is a floor (a pad at its foot).</summary>
    public bool StartsOnFloor;
    /// <summary>Searched down from the top of another staircase (StairLayout.BuildDown): its top is that one's top edge exactly,
    /// its foot (<see cref="Foot"/>) where the search reached the ground near the road.</summary>
    public bool BuiltDown;
    /// <summary>Its top carried on as a floor at a ridge's crest for another staircase to come down from (RoadStairRuns).</summary>
    public bool CrestFloored;
    /// <summary>Cut points of the level apron cut behind its foot where it stood against a bank (StairLayout.CutFootApron).</summary>
    public int FootApron;
    /// <summary>Pieces it set off back from its foot, more than StairLayout.FacingSteps off the road's travel, before
    /// one faced it (StairLayout.MostSetOffBack); 0 where it set off facing the road or was held to none.</summary>
    public int SetOffBack;
    /// <summary>Set once its top has been carried out over a lip and crested for a road (RoadStairRuns.TopOntoSurface):
    /// where the road starts past it, on the surface.</summary>
    public Vector3? TopCrested;
    /// <summary>The sunk step under its foot: its pieces and clearances, first in the lists (0 when there is none).</summary>
    public int SunkPieces, SunkClearances;
    /// <summary>Where its first step's low edge is (its foot), when it has a sunk step under it.</summary>
    public Vector3? Foot;
    /// <summary>Stairs added down from its foot to its road (<see cref="StairLayout.LowerFoot"/>).</summary>
    public int FootLowered;
    /// <summary>The steepest face a cantilevered row crosses (rise over run across the flight).</summary>
    public float SteepestFace;
    public Vector3 End;
    /// <summary>Where the last walked row begins in <see cref="Pieces"/> and <see cref="Clearances"/>,
    /// and what it is: what <see cref="StairLayout.StepDownTop"/> may replace.</summary>
    public int LastRowPieces = -1, LastRowClearances = -1, LastRowPieceCount, LastRowClearanceCount;
    public StairPieceKind LastRowKind;
    public Vector2 LastRowStart;
    public float LastRowHeading, LastRowHeight;
}

/// <summary>
/// Deterministic layout of a stone staircase between two points on the ground.
/// Pure logic: the game samples the ground and places the returned pieces.
///
/// Rules from a hand-built example:
///
/// * Every vanilla stair piece is 2 m long and rises 1 m, so a flight is
///   always a grade of 0.5. Ground gentler than that gets a level landing.
/// * Follow the contours: each step picks the heading, in the hammer's 22.5
///   degree steps, along which the ground best matches the piece, so on
///   ground steeper than 0.5 the stair crosses the slope instead of
///   climbing straight up it.
/// * Every turn pivots on the inside corner of the previous piece's top
///   edge, which opens a wedge on the outside that a filler covers. A pivot
///   on the outside corner always pushes the new piece's inside corner back
///   into the previous piece: the first two versions split turns into
///   convex (inside) and concave (outside) by the slope, and the outside-pivot
///   turns overlapped both ways round. The hand-built
///   examples pivot on the inside corner every time.
/// * The wedge of a 22.5 degree turn is filled with a 1 m stone block laid
///   flat at the old heading, level with the stair top (a hand-built switchback
///   example: less sticking out than a stair at the half angle, and it
///   stays on the hammer's grid). A 45 degree turn on a landing is filled
///   with a floor at the halfway heading.
/// * A turn may be any size on the hammer's grid (as long as the stair
///   corners snap as expected and the stairs don't overlap each other):
///   a stair up to two steps (45 degrees) off the stair
///   below it, four (90) off a landing, three off a switchback. A landing
///   never turns: it lines up with the stair below it. Its wedge is filled with
///   flat pieces on the hammer's grid that <see cref="StairFiller"/> chooses
///   turn by turn: the fewest that cover it and show least. A single floor
///   at the half angle (33.75 degrees for a 67.5 degree turn) left the
///   wedge open and the junction did not connect up; a fan of 2x1 blocks
///   hinged on the inside corner covered it but stuck out.
/// * A switchback is a landing two lanes wide: the flight arrives on one
///   floor, a second floor lies beside it, and the next flight leaves that
///   one heading back the way it came. It is how a stair climbs ground too
///   steep to cross in one traverse.
/// * Every piece is supported down to the ground: stone loses its support
///   within a metre sideways, so a piece not sunk into the ground stands on
///   a column of pillars driven into it.
/// * Or, on a face of 2:1 and steeper, held out from it (as in a hand-built cantilever reference):
///   pillars clipped into the face, arches out under the piece, a
///   block beside it (<see cref="AddCantilever"/>), priced by the face's grade up to
///   a maximum. Turns too: their wedge rests on an arch and a strip column
///   (<see cref="AddTurnSupport"/>, from hand-built turns). A switchback's pad stands on
///   columns, and is priced by the steepness round it, so the search turns on benches.
///
/// The search is best-first over whole pieces. A partial staircase is scored by
/// the length walked so far, how badly its pieces fit the ground, and how
/// often it turned, plus the least walking still needed: at least the
/// distance left, and at least two metres for every metre still to climb.
/// </summary>
public static partial class StairLayout
{
    [ThreadStatic] private static long s_ids;

    public const float PieceRun = 2f;
    public const float PieceRise = 1f;
    public const float HeadingStep = 22.5f;
    /// <summary>
    /// A generated staircase keeps this close to the stretch of road it
    /// replaces, so it does not wander over its road or into the road's other
    /// staircases (much wider reaches the next switchback leg; much narrower
    /// fills a steep face with mini switchbacks).
    /// </summary>
    public const float CorridorHalfWidth = 10f;
    /// <summary>Waypoints taken from a followed line stand at least this far apart.</summary>
    public const float FollowSpacing = 6f;
    /// <summary>A staircase's ends line up with the road within this many heading steps (45 degrees).</summary>
    public const int AlignSteps = 2;
    public const int MaxStairTurnSteps = 2;
    /// <summary>How far a stair may turn as it leaves a landing. The landing
    /// itself never turns: it lines up with the stair below it (a landing turned 45 degrees off the stair below left a gap
    /// to the stair above).</summary>
    public const int MaxTurnOffLandingSteps = 4;
    /// <summary>How far one floor may turn off the floor before it. Floors overlap
    /// at a turn instead of leaving a wedge (unlike stairs, two flat stones
    /// can overlap, which looks better): they pivot on the outside corner. Without it
    /// a run of floors could only go straight, and a long gentle valley floor
    /// could only bend by climbing a stair.</summary>
    public const int MaxFloorTurnSteps = 2;
    /// <summary>A flight leaving a switchback bends at most one step off straight
    /// back (157.5 degrees): more leaves a wedge no filler covers.</summary>
    public const int MaxSwitchbackBendSteps = 1;
    public const int MaxPieces = 800;
    /// <summary>A run longer than this follows a coarse route (<see cref="StairRoute"/>), leg by leg
    /// through waypoints. Shorter runs are one search, which lays a better staircase where it can
    /// finish.</summary>
    public const float RouteBeyond = 120f;
    /// <summary>A waypoint is passed when an edge comes this close to it, at any height: the route's flex.</summary>
    public const float WaypointReach = 3.5f;
    /// <summary>A leg to an intermediate waypoint ends with its walking surface this close to the ground.</summary>
    public const float LegEndHeight = 1f;
    /// <summary>How many kept legs the search may undo when the next one finds nothing that fits.</summary>
    public const int MaxLegBacktrack = 4;
    public const int MaxExpansions = 100000;
    /// <summary>Done when the last edge is this close to the goal and within half a step of its height.
    /// Just under two pieces: at 2.5 m a stair a step from the top could overshoot it on every heading.</summary>
    public const float ReachRadius = 3.5f;
    public const float ReachHeight = 0.5f;
    /// <summary>A staircase that may finish anywhere near its goal finishes no lower than this
    /// below the goal's ground: at 6 m from a cave's ledge, without it, one stopped on the
    /// slope in front, 2.5 m under the ledge.</summary>
    public const float ArriveBelow = 1f;
    /// <summary>Headroom kept clear of rock above every walking surface: player height, as a road's (one owner,
    /// <see cref="RoadRockPolicy.ClearanceHeight"/>).</summary>
    public const float ClearanceHeight = RoadRockPolicy.ClearanceHeight;
    /// <summary>A stair's clearance is cut into this many stretches along it, so it follows the steps up.</summary>
    public const int ClearanceStretches = 2;

    /// <summary>Cost per metre a piece floats above the ground (mean over it): mostly
    /// hugging the terrain is preferred, some variation is fine.</summary>
    private const float FloatWeight = 3f;
    /// <summary>Cost per metre the ground stands over a walking surface (worst sample):
    /// the earthwork that exposes it. Hugging is preferred to digging, so it costs
    /// more than floating.</summary>
    private const float BuriedWeight = 6f;
    private const float TurnWeight = 0.5f;

    private const float LandingWeight = 0.25f;
    /// <summary>Above 1 the search prefers pieces that get closer over a
    /// cheaper staircase it has not proved yet: weighted A*, which finds a
    /// good staircase in a few hundred steps instead of the best in thousands.</summary>
    private const float HeuristicWeight = 3f;
    /// <summary>
    /// A new piece may not overlap, seen from above, any piece laid before the
    /// previous one by more than this (pieces that merely touch are fine): an
    /// upper flight overlapping a lower one has nowhere for its supports to stand.
    /// </summary>
    private const float OverlapTolerance = 0.1f;

    private sealed class Node
    {
        public Node? Parent;
        public StairPieceKind Kind;
        /// <summary>Centre of the edge this piece starts from (its foot), and the heading it walks.</summary>
        public Vector2 Start;
        public Vector2 Edge; // centre of the edge it ends on: the next piece starts here
        public float Heading;
        public float Height; // walking surface at Edge
        public int TurnSteps; // signed, relative to the parent; positive turns right
        /// <summary>0, or the side (+1 right, -1 left) a switchback's return lane lies on.</summary>
        public int Switchback;
        /// <summary>Where this step stands, for the check that the stair never runs into itself.</summary>
        public Vector2 Centre;
        public Vector2 Pivot;
        public int Depth;
        public long Id;
        /// <summary>Index of the waypoint this staircase is heading for.</summary>
        public int Goal;
        /// <summary>Turned on the outside corner (floor after floor): overlaps on the inside, no wedge to fill.</summary>
        public bool Overlaps;
        /// <summary>A row already laid that this staircase carries on from: the search's root, never emitted.</summary>
        public bool Placed;
        public float Cost;
        public float Score;
        public float Float;
        public float Buried;
        /// <summary>0, or the side (+1 right, -1 left) of the face a cantilevered piece is held out from.</summary>
        public int Cantilever;
        /// <summary>The face's grade across a cantilevered piece.</summary>
        public float Face;
        /// <summary>Held to a facing (<see cref="Route.Facing"/>): pieces from the foot that set off back, more than
        /// <see cref="FacingSteps"/> off it, none facing it yet; -1 once one has (and with no facing).</summary>
        public int BackRun;
        /// <summary>Held to a facing: stairs climbed since the first piece that faced it (capped at
        /// <see cref="FirstFlightStairs"/>); -1 before one has.</summary>
        public int FacedStairs = -1;
    }

    /// <summary>A staircase from <paramref name="from"/> to <paramref name="to"/>, built
    /// of <paramref name="kit"/> (stone when none is given), searching at most
    /// <paramref name="maxExpansions"/> pieces per leg before giving up.
    /// <paramref name="arriving"/> and <paramref name="leaving"/>: the heading a
    /// road arrives at <paramref name="from"/> on and leaves <paramref name="to"/>
    /// on, if any. The staircase then sets off and finishes within
    /// <see cref="AlignSteps"/> of them, turning or switching back on the way
    /// as it must (a mini switchback at the end so the direction of the road mostly lines up with the direction of
    /// the stairs; a staircase free to start any way went back over the road
    /// it started from).</summary>
    /// <paramref name="corridor"/>: a line from <paramref name="from"/> to
    /// <paramref name="to"/> the staircase follows, through its points as
    /// waypoints, every piece within <see cref="CorridorHalfWidth"/> of it, if
    /// given: the stretch of road a generated staircase replaces, where the
    /// road's own search found the climb.
    /// <paramref name="maxBuried"/>: deepest the ground may stand over a walking
    /// surface on any piece (<see cref="StairCover.RoadStaircase"/> unless asked otherwise).
    /// <paramref name="arriveWithin"/>: the staircase may finish anywhere this close to
    /// <paramref name="to"/>, meeting the ground where it does, not at <paramref name="to"/>'s height:
    /// for an area to reach, such as the ledge in front of a cave, which whole-metre steps
    /// from the road's height could not meet at one point.
    /// <paramref name="allowed"/>: where a piece's centre may stand at all (round a cave's rock, say).
    /// <paramref name="after"/>: the top row of a staircase already laid at <paramref name="from"/>. Climbing on
    /// from it, the staircase carries on as that one would have: its first piece's back edge on the row's far
    /// edge at its walking height, turning off it as any piece off the one below (inside-corner pivot, filler,
    /// no switchback straight off it), and no sunk foot (MountainCave02 -2560,1536: the final staircase builds off the snap
    /// points of what is already there). Laid
    /// down to it (its goal below the row), it finishes head-on at the row, facing back along it.
    public static StairPlan Build(Vector2 from, Vector2 to, Func<float, float, float> ground, float width = 2f, StairKit? kit = null,
        int maxExpansions = MaxExpansions, float? arriving = null, float? leaving = null, IReadOnlyList<Vector2>? corridor = null,
        int totalExpansions = int.MaxValue, int stallExpansions = int.MaxValue, int topLandings = 0, bool sunkFoot = false,
        float maxBuried = StairCover.RoadStaircase, float? arriveWithin = null, Func<Vector2, bool>? allowed = null, float? footHeight = null,
        StairContinuation? after = null, bool relayPads = true, float? facing = null, float? topFacing = null)
    {
        StairPlan plan = BuildOnce(from, to, ground, width, kit, maxExpansions, arriving, leaving, corridor, totalExpansions, stallExpansions,
            topLandings, sunkFoot, maxBuried, arriveWithin, allowed, footHeight, after, stairFirst: false, facing, topFacing);
        // Started on a pad of floors from the ground: laid again coming up out of the ground on a stair, and that kept
        // where it reaches. A pad stays only where nothing else does. Two floors laid off a road's end and turned off
        // square stood a step proud of the road (at the foot of Hildir's cave's approach at 1641,790 and at 6497,-1497: stairs out
        // of the road on the flight's heading, turning on the stairs, join better than two starter blocks or a turn at the bottom).
        // The stair first may set off a step further off the
        // road's heading than a floor (Route.StairFirst). Not where its foot meets another staircase head-on (relayPads
        // false): the two would run into each other.
        // Not laid down from its top (Reversed): its foot is where it comes down, often onto another staircase's (a gully's floor).
        if (relayPads && plan.Reached && !plan.Reversed && plan.StartsOnFloor && sunkFoot && !after.HasValue)
        {
            StairPlan stair = BuildOnce(from, to, ground, width, kit, maxExpansions, arriving, leaving, corridor, totalExpansions, stallExpansions,
                topLandings, sunkFoot, maxBuried, arriveWithin, allowed, footHeight, after, stairFirst: true, facing, topFacing);
            stair.Expansions += plan.Expansions;
            if (stair.Reached) return stair;
            plan.Expansions = stair.Expansions;
        }
        return plan;
    }

    private static StairPlan BuildOnce(Vector2 from, Vector2 to, Func<float, float, float> ground, float width, StairKit? kit,
        int maxExpansions, float? arriving, float? leaving, IReadOnlyList<Vector2>? corridor,
        int totalExpansions, int stallExpansions, int topLandings, bool sunkFoot,
        float maxBuried, float? arriveWithin, Func<Vector2, bool>? allowed, float? footHeight,
        StairContinuation? after, bool stairFirst, float? facing, float? topFacing)
    {
        if (after.HasValue)
        {
            from = after.Value.Edge;
            arriving = after.Value.Heading;
        }
        kit ??= StairKit.Stone;
        if (width < PieceRun - 0.01f) throw new ArgumentOutOfRangeException(nameof(width), "A stair is at least one piece (2 m) wide.");
        var plan = new StairPlan { Kit = kit.Name };
        List<Vector2>? line = corridor == null ? null : new List<Vector2>(corridor);
        if (ground(to.x, to.y) < ground(from.x, from.y))
        {
            (from, to) = (to, from);
            line?.Reverse();
            plan.Reversed = true;
            // Climbed the other way: it sets off back along the road that left the
            // top, and finishes facing back down the road that arrived at the foot.
            (arriving, leaving) = (leaving.HasValue ? Wrap(leaving.Value + 180f) : null, arriving.HasValue ? Wrap(arriving.Value + 180f) : null);
            // Its top meets the row head-on: floors carried on past it would lie on the row.
            if (after.HasValue) topLandings = 0;
        }

        var goals = new List<Vector2> { to };
        if (line != null)
        {
            // The road's own cells, a few metres apart at least, ending on the goal.
            // Left to search its own way between the ends, a generated staircase
            // missed the climb the road had found more often than it met it: on
            // RoadSeedA 699 layouts were not reached, and roads lost their stairs.
            goals.Clear();
            Vector2 last = from;
            foreach (Vector2 p in line)
            {
                if (Vector2.Distance(p, last) < FollowSpacing || Vector2.Distance(p, to) < FollowSpacing) continue;
                goals.Add(p);
                last = p;
            }
            goals.Add(to);
        }
        else if (Vector2.Distance(from, to) > RouteBeyond)
        {
            List<Vector2>? waypoints = StairRoute.Waypoints(from, to, ground);
            if (waypoints != null) goals = waypoints;
        }
        // Down to a laid row, its top meets the row at the row's own height, over the ground or not.
        var route = new Route(goals, after.HasValue && plan.Reversed ? after.Value.Height : ground(to.x, to.y)) { StartAligned = arriving.HasValue, EndHeading = leaving, Corridor = corridor, MaxBuried = maxBuried, ArriveWithin = arriveWithin, Allowed = allowed, Kit = kit,
            StairFirst = stairFirst, Facing = facing, TopFacing = topFacing };
        plan.Waypoints = goals.Count;
        var root = new Node
        {
            Edge = from,
            Start = from,
            Heading = SnapHeading(arriving ?? Bearing(from, goals[0])),
            // A foot set beside its road (RoadStairRuns.Lay) stands at the road's height, not the
            // ground's there: across a slope the two differ.
            Height = footHeight ?? ground(from.x, from.y),
        };
        bool carriesOn = after.HasValue && !plan.Reversed;
        if (carriesOn)
        {
            // The laid row itself, as though this search had laid it: its children turn off it by
            // the rules for any piece. Its own parent only marks it as not the first.
            StairContinuation row = after!.Value;
            root = new Node
            {
                Kind = row.Kind,
                Start = row.Start,
                Edge = row.Edge,
                Heading = row.Heading,
                Height = row.Height,
                Centre = row.Edge - Forward(row.Heading) * (PieceRun / 2f),
                Placed = true,
                Parent = new Node { Edge = row.Start, Start = row.Start, Heading = row.Heading,
                    Height = row.Height - (row.Kind == StairPieceKind.Stair ? PieceRise : 0f) },
            };
        }
        root.Score = Remaining(root, route);

        // One leg per waypoint, each searched from where the last one ended and
        // kept: a single search over a whole valley-to-summit route kept
        // reopening pieces laid far back and ran out of steps. An intermediate
        // waypoint is reached near the ground; only the last leg must meet the
        // goal's height. A leg can end where nothing fits next: then the search
        // steps back to where the previous leg started and aims one waypoint
        // further, up to MaxLegBacktrack legs back.
        Node current = root;
        Node? best = null;
        var legStarts = new List<Node>();
        int g = 0, back = 0;
        while (g < route.Goals.Count)
        {
            var leg = new Route(route.Goals.GetRange(g, route.Goals.Count - g), route.GoalHeight) { StartAligned = route.StartAligned, EndHeading = route.EndHeading, Corridor = route.Corridor, MaxBuried = route.MaxBuried, ArriveWithin = route.ArriveWithin, Allowed = route.Allowed, Kit = route.Kit, StairFirst = route.StairFirst, Facing = route.Facing, TopFacing = route.TopFacing };
            current.Goal = 0;
            bool last = g == route.Goals.Count - 1;
            if (totalExpansions <= 0) { plan.Reason = "search budget spent"; break; }
            Node? reached = Search(current, leg, last, ground, width, Math.Min(maxExpansions, totalExpansions), stallExpansions, out Node closest, out string reason, out int used);
            totalExpansions -= used;
            plan.Expansions += used;
            if (reached != null)
            {
                legStarts.Add(current);
                current = reached;
                g++;
                back = 0;
                if (last) { best = reached; plan.Reached = true; }
                continue;
            }
            if (legStarts.Count == 0 || back >= MaxLegBacktrack)
            {
                plan.Reason = route.Goals.Count > 1 ? $"{reason} (waypoint {g + 1} of {route.Goals.Count})" : reason;
                best = closest;
                break;
            }
            current = legStarts[legStarts.Count - 1];
            legStarts.RemoveAt(legStarts.Count - 1);
            back++;
            plan.Backtracks++;
        }
        best ??= current;
        // Floors carried on straight from the top step, level with it, for the road
        // to meet: run up to the last step, the road's earthwork stopped short and a
        // strip of natural ground and rock showed between (a flat 2x2 landing or two that clips into the road helps).
        if (plan.Reached)
            for (int i = 0; i < topLandings; i++)
            {
                Vector2 dir = Forward(best.Heading);
                best = new Node
                {
                    Parent = best,
                    Kind = StairPieceKind.Landing,
                    Start = best.Edge,
                    Edge = best.Edge + dir * PieceRun,
                    Heading = best.Heading,
                    Height = best.Height,
                    Centre = best.Edge + dir * (PieceRun / 2f),
                    Depth = best.Depth + 1,
                };
            }

        Emit(plan, best, ground, width, kit, sunkFoot && plan.Reached && !carriesOn);
        return plan;
    }

    /// <summary>
    /// Best-first over whole pieces from <paramref name="root"/> toward the route's first
    /// goal: with <paramref name="final"/>, at the goal's height; otherwise anywhere within
    /// reach of it. A state seen before (within a piece, same height, heading and goal) is not
    /// expanded twice, so the search cannot circle; a budget bounds the work on ground
    /// nothing can climb.
    /// </summary>
    private static Node? Search(Node root, Route route, bool final, Func<float, float, float> ground, float width, int maxExpansions,
        int stallExpansions, out Node closest, out string reason, out int used)
    {
        // A leg ends near the ground, or the next one starts from a stair hanging in the air
        // or buried in the slope (measured: a leg kept at 18 m up left nothing to reach).
        // Nor does a staircase end on a switchback, or on floors laid off one: the pad turns back to
        // climb on, and finished there the top faced back down the flight. The steps down off the top
        // then ran back down beside it, two rows wide (MountainCave02 -2560,1536: double stairs that looked like a bug).
        bool Done(Node n) => final ? !EndsTurnedBack(n) && Arrived(n, route) && Mathf.Abs(ground(n.Edge.x, n.Edge.y) - n.Height) <= ReachHeight
            && (!route.ArriveWithin.HasValue || TopReachesGround(n.Edge, n.Heading, n.Height, ground))
            : n.Goal > 0 && Mathf.Abs(ground(n.Edge.x, n.Edge.y) - n.Height) <= LegEndHeight;
        root.Score = Remaining(root, route);
        var open = new SortedSet<Node>(NodeOrder.Instance) { root };
        var seen = new HashSet<(int, int, int, int, int)>();
        var children = new List<Node>();
        closest = root;
        reason = "";
        int expansions = 0, improved = 0;
        Node? best = null;
        while (open.Count > 0 && expansions < maxExpansions)
        {
            // No nearer the goal in this many pieces: it is wandering, and will not
            // get there. Failing searches took 70% of a whole world's generation,
            // about 2 s each, most of it after they had stopped closing in.
            if (expansions - improved > stallExpansions) { reason = $"no nearer the goal in {stallExpansions} steps of search"; break; }
            Node node = open.Min!;
            open.Remove(node);
            if (node != root && Done(node)) { best = node; break; }
            if (!seen.Add(Key(node)) || node.Depth >= MaxPieces) continue;
            if (Remaining(node, route) < Remaining(closest, route) - 0.01f) { closest = node; improved = expansions; }
            expansions++;
            children.Clear();
            Expand(node, route, ground, width, children);
            foreach (Node child in children)
            {
                child.Id = ++s_ids;
                // Taken as soon as it is made: waiting for it to come off the
                // queue spent the budget on cheaper, unfinished staircases while
                // one a step from the top sat behind them.
                if (Done(child) && (best == null || child.Cost < best.Cost)) best = child;
                open.Add(child);
            }
            if (best != null) break;
        }
        if (best == null && reason.Length == 0)
            reason = open.Count == 0
                ? "no piece fits: every heading is buried, crosses itself or turns too far"
                : $"not reached within {maxExpansions} steps of search";
        used = expansions;
        return best;
    }

    /// <summary>Near the goal, at its height, facing the way the road leaves. The
    /// search also asks that the staircase end level with the ground at its own
    /// end: within reach of the goal on a steep slope, one stopped 2.4 m under
    /// the ground there, and the road leaving it was dug 5 m into the hill.</summary>
    private static bool Arrived(Node node, Route route) =>
        node.Goal == route.Last && Vector2.Distance(node.Edge, route.Final) <= (route.ArriveWithin ?? ReachRadius) &&
        (route.ArriveWithin.HasValue ? node.Height >= route.GoalHeight - ArriveBelow : Mathf.Abs(route.GoalHeight - node.Height) <= ReachHeight) &&
        (!route.EndHeading.HasValue || Mathf.Abs(Wrap(node.Heading - route.EndHeading.Value + 180f) - 180f) <= AlignSteps * HeadingStep + 0.01f) &&
        (!route.TopFacing.HasValue || Mathf.Abs(Wrap(node.Heading - route.TopFacing.Value + 180f) - 180f) <= FacingSteps * HeadingStep + 0.01f);

    /// <summary>States closer than a piece apart, at the same height and heading,
    /// are one state: at half a metre a 82 m climb up a real 1:1 to 2:1 face
    /// drowned in near-copies and was not solved
    /// in 100000 steps; at 2 m it is, in 1.7 s offline.</summary>
    private const float StateCell = 2f;

    private static (int, int, int, int, int) Key(Node node) =>
        (Mathf.RoundToInt(node.Edge.x / StateCell), Mathf.RoundToInt(node.Edge.y / StateCell), Mathf.RoundToInt(node.Height),
         Mathf.RoundToInt(node.Heading / HeadingStep) % 16, node.Goal);

    /// <summary>Where the search is going: waypoints, the last being the goal, and the route length left after each.</summary>
    private sealed class Route
    {
        public readonly List<Vector2> Goals;
        public readonly float[] Tail;
        public readonly float GoalHeight;
        /// <summary>The first piece sets off within <see cref="AlignSteps"/> of the root's heading, not any way.</summary>
        public bool StartAligned;
        /// <summary>The last piece faces within <see cref="AlignSteps"/> of this, if set.</summary>
        public float? EndHeading;
        /// <summary>A line every piece keeps within <see cref="CorridorHalfWidth"/> of, if set.</summary>
        public IReadOnlyList<Vector2>? Corridor;
        /// <summary>Deepest the ground may stand over a walking surface on any piece.</summary>
        public float MaxBuried = StairCover.RoadStaircase;
        /// <summary>Set: the staircase may finish anywhere this close to the goal, at the
        /// ground's height where it finishes and no more than <see cref="ArriveBelow"/> below
        /// the goal's, rather than within <see cref="ReachRadius"/> at the goal's own height.</summary>
        public float? ArriveWithin;

        /// <summary>Set: where a piece may stand at all (its centre), beyond the corridor.</summary>
        public Func<Vector2, bool>? Allowed;
        /// <summary>What it is built of: whether a piece on a steep face can be held out from it.</summary>
        public StairKit Kit = StairKit.Stone;
        /// <summary>The first piece is a stair, not a floor: the staircase comes up out of the ground it starts on.</summary>
        public bool StairFirst;
        /// <summary>Set: the heading a road arrives on at the staircase's lower end, where the climb starts (the caller
        /// reckons which end that is: Build does not turn it round when it climbs the other way); the first piece sets
        /// off within <see cref="FacingSteps"/> of it, never back across the road. Held to the road's heading as well
        /// (AlignSteps, a step more stair first), it holds both.</summary>
        public float? Facing;
        /// <summary>Set: the heading a road leaves the staircase's top on (the caller reckons which end is the top); the last
        /// piece arrives within <see cref="FacingSteps"/> of it, and pieces near the top are priced by how far off it they
        /// head (<see cref="FootOffRoadWeight"/>).</summary>
        public float? TopFacing;
        /// <summary>Searched down from its top (<see cref="BuildDown"/>): every stair a rise lower, the first piece on the
        /// root's own heading.</summary>
        public bool Down;

        public bool InCorridor(Vector2 p)
        {
            if (Allowed != null && !Allowed(p)) return false;
            if (Corridor == null || Corridor.Count == 0) return true;
            if (Corridor.Count == 1) return Vector2.Distance(p, Corridor[0]) <= CorridorHalfWidth;
            float[] boxes = SegmentBoxes(Corridor);
            for (int i = 1; i < Corridor.Count; i++)
            {
                // Beyond the segment's box grown by the half width: farther than that from the segment.
                int b = 4 * (i - 1);
                if (p.x < boxes[b] || p.x > boxes[b + 1] || p.y < boxes[b + 2] || p.y > boxes[b + 3]) continue;
                Vector2 a = Corridor[i - 1], ab = Corridor[i] - a;
                float t = ab.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                if (Vector2.Distance(p, a + ab * t) <= CorridorHalfWidth) return true;
            }
            return false;
        }
        // Each corridor segment's box grown by the half width (and a little for rounding), worked out once per corridor:
        // a candidate piece is tested against the few segments near it, not every segment of up to 250 m of road.
        private IReadOnlyList<Vector2>? m_boxedCorridor;
        private float[]? m_boxes;
        private float[] SegmentBoxes(IReadOnlyList<Vector2> corridor)
        {
            if (m_boxes != null && ReferenceEquals(m_boxedCorridor, corridor) && m_boxes.Length == 4 * (corridor.Count - 1)) return m_boxes;
            var boxes = new float[4 * (corridor.Count - 1)];
            float grow = CorridorHalfWidth + 0.01f;
            for (int i = 1; i < corridor.Count; i++)
            {
                Vector2 a = corridor[i - 1], c = corridor[i];
                int b = 4 * (i - 1);
                boxes[b] = Mathf.Min(a.x, c.x) - grow; boxes[b + 1] = Mathf.Max(a.x, c.x) + grow;
                boxes[b + 2] = Mathf.Min(a.y, c.y) - grow; boxes[b + 3] = Mathf.Max(a.y, c.y) + grow;
            }
            m_boxedCorridor = corridor;
            return m_boxes = boxes;
        }

        public Route(List<Vector2> goals, float goalHeight)
        {
            Goals = goals;
            GoalHeight = goalHeight;
            Tail = new float[goals.Count];
            for (int g = goals.Count - 2; g >= 0; g--) Tail[g] = Tail[g + 1] + Vector2.Distance(goals[g], goals[g + 1]);
        }
        public Vector2 Final => Goals[Goals.Count - 1];
        public int Last => Goals.Count - 1;

        /// <summary>The waypoint an edge here heads for next. A waypoint is passed
        /// once the edge comes close to it, or is already nearer the waypoint after
        /// it than the waypoint itself is: a stair cannot always step onto a point.</summary>
        public int Advance(int goal, Vector2 edge)
        {
            while (goal < Last && (Vector2.Distance(edge, Goals[goal]) <= WaypointReach ||
                   Vector2.Distance(edge, Goals[goal + 1]) < Vector2.Distance(Goals[goal], Goals[goal + 1]))) goal++;
            return goal;
        }
    }

    private sealed class NodeOrder : IComparer<Node>
    {
        public static readonly NodeOrder Instance = new();
        public int Compare(Node? a, Node? b)
        {
            int c = a!.Score.CompareTo(b!.Score);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        }
    }

    /// <summary>The least walking still needed: never less than the route left
    /// (to the next waypoint, then waypoint to waypoint), and never less than one
    /// stair run per stair rise still to climb. Climbing past the goal must be
    /// walked back down.</summary>
    private static float Remaining(Node node, Route route) => Remaining(node.Edge, node.Goal, node.Height, route);

    private static float Remaining(Vector2 edge, int goal, float height, Route route)
    {
        float distance = Vector2.Distance(edge, route.Goals[goal]) + route.Tail[goal];
        float rise = route.Down ? height - route.GoalHeight : route.GoalHeight - height;
        float climb = rise > 0f ? rise * PieceRun / PieceRise : -rise * PieceRun / PieceRise * 2f;
        return Mathf.Max(distance, climb);
    }

    private static readonly StairPieceKind[] Kinds = { StairPieceKind.Stair, StairPieceKind.Landing };

    private static void Expand(Node node, Route route, Func<float, float, float> ground, float width, List<Node> next)
    {
        float half = width / 2f;
        Vector2 right = Right(node.Heading);

        foreach (StairPieceKind kind in Kinds)
        {
            // The first piece may face any of the hammer's sixteen headings: on
            // ground steeper than a flight it has to start across the slope.
            bool first = node.Parent == null;
            if (first && kind == StairPieceKind.Landing && route.StairFirst) continue;
            // Leaving a switchback the flight may bend one step off the
            // reverse heading, like any stair turn (a hand-built second switchback
            // turns 157.5 degrees). Three steps left a 67.5 degree wedge that
            // no filler covered, so the junction did not connect up well.
            bool floorOnFloor = kind == StairPieceKind.Landing && node.Kind == StairPieceKind.Landing && node.Switchback == 0 && !first;
            // Coming up out of the road on a stair (StairFirst), it may set off a step further off the road's heading than
            // a floor would: a foot at Hildir's cave left the road 55 degrees off its heading and turned on the stairs.
            int maxTurn = first ? (route.StartAligned ? AlignSteps + (route.StairFirst && kind == StairPieceKind.Stair ? 1 : 0) : 8)
                : node.Switchback != 0 ? MaxSwitchbackBendSteps
                : kind == StairPieceKind.Landing ? (floorOnFloor ? MaxFloorTurnSteps : 0)
                : node.Kind == StairPieceKind.Landing ? MaxTurnOffLandingSteps
                : MaxStairTurnSteps;
            if (route.Down && node.Placed) maxTurn = 0; // straight on from the top it carries down from: back to back
            for (int turn = first && !route.StartAligned && !route.Down ? -7 : -maxTurn; turn <= maxTurn; turn++)
            {
                float heading = Wrap(node.Heading + turn * HeadingStep);
                var setOff = BackRunAfter(node, heading, kind, route);
                if (setOff == null) continue;
                Vector2 dir = Forward(heading);
                (Vector2 start, Vector2 pivot) = first ? (node.Edge, node.Edge) : TurnOff(node.Edge, node.Heading, heading, turn, half, floorOnFloor);

                Vector2 edge = start + dir * PieceRun;
                Vector2 middle = start + dir * (PieceRun / 2f);
                if (!route.InCorridor(middle)) continue;

                float rise = kind == StairPieceKind.Stair ? (route.Down ? -PieceRise : PieceRise) : 0f;
                Fit(start, dir, Right(heading), half, node.Height, rise, ground, out float floatGap, out float buried);
                // The wedge a turn opens is filled level with the piece before (StairFiller): it is walked
                // too, and buried it needs earthwork as a piece does. Where a staircase is kept off earthwork
                // (an approach, laid with less than StairCover.RoadStaircase: no cuts in a place's footprint), it is read
                // as a piece is: unread, a turn on Hildir's cave's face left a filler 0.6 m
                // under the slope inside the cave's footprint, and the approach was refused. A road's staircase
                // cuts its fillers out, as before: read there, the 82 m climb up the real face found no way up.
                if (turn != 0 && !first && !floorOnFloor && route.MaxBuried < StairCover.RoadStaircase) buried = Mathf.Max(buried, WedgeBuried(node.Edge, node.Height, pivot, start, ground));
                if (buried > route.MaxBuried) continue; // the stair would disappear into the slope
                // Held out from a steep face straight on and through turns (hand-built turn examples;
                // AddTurnSupport holds the wedge); the flight leaving a switchback's pad, and floors turned
                // over floors, stand on ordinary support.
                float underside = node.Height - (kind == StairPieceKind.Stair ? 0f : route.Kit.FloorThickness);
                float face = 0f;
                int cantilever = !route.Down && node.Switchback == 0 && !floorOnFloor && width <= PieceRun + 0.01f && floatGap >= CantileverDrop / 9f
                    ? CantileverSide(start, dir, Right(heading), half, underside, route.Kit, ground, out face, node.Height, rise, route.MaxBuried) : 0;
                // Columns where they are the cheaper (lesser grades, little air under the piece); held out
                // where the air under it would cost more than the face's price.
                if (cantilever != 0 && CantileverPrice(face) >= FloatWeight * floatGap) cantilever = 0;
                if (CrossesItself(node, middle, heading, width, underside, cantilever, route.Kit)) continue;
                // At its foot (the first EdgeStairs stairs), no stair stands on a ledge with its side out past the ledge's
                // sharp edge: the flight turns to set off from the landing instead. A steady slope crossed, and everywhere
                // up the flight and at its top, stairs hug the ground as before (at the mountain cave at
                // -2556.7,1554.2 the stairs should not stick out the edge of the cliff, so turn it to start on
                // the landing; only at the bottom step of stairs, the top steps work fine already).
                float offEdge = kind == StairPieceKind.Stair && cantilever == 0 && node.Depth < EdgeStairs && !route.Down
                    && !(node.Depth == 0 ? node : node.Parent!).Placed ? EdgeWeight * HangsOverEdge(start, dir, Right(heading), half, node.Height, ground) : 0f;

                var child = new Node
                {
                    Parent = node,
                    Kind = kind,
                    Start = start,
                    Edge = edge,
                    Heading = heading,
                    Height = node.Height + rise,
                    TurnSteps = first ? 0 : turn,
                    Overlaps = floorOnFloor && turn != 0,
                    Pivot = pivot,
                    Centre = start + dir * (PieceRun / 2f),
                    Depth = node.Depth + 1,
                    Float = floatGap,
                    Buried = buried,
                    Cantilever = cantilever,
                    Face = face,
                    BackRun = setOff.Value.backRun,
                    FacedStairs = setOff.Value.faced,
                };
                // A cantilevered piece is priced by the face it crosses, not by the air under it, and by how
                // much of its strip stands out of the face.
                float hug = cantilever != 0
                    ? HugWeight * StripExposed(start, dir, Right(heading) * cantilever, half, underside + 1f, ground) : 0f;
                child.Cost = node.Cost + PieceRun + hug + offEdge
                    + (cantilever != 0 ? CantileverPrice(face) : FloatWeight * floatGap) + BuriedWeight * buried
                    + TurnWeight * Mathf.Abs(first ? 0 : turn) + (first ? FootOffRoadPrice(heading, route) : 0f) + TopOffRoadPrice(edge, heading, node.Goal, route)
                    + (kind == StairPieceKind.Landing ? LandingWeight : 0f);
                if (offEdge > 0f) t_ledgePrices++;
                child.Goal = route.Advance(node.Goal, child.Edge);
                child.Score = child.Cost + HeuristicWeight * Remaining(child, route);
                next.Add(child);
            }
        }

        if (node.Parent == null || SwitchbackSinceLastStair(node)) return;
        Vector2 forward = Forward(node.Heading);
        for (int side = -1; side <= 1; side += 2)
        {
            // Floor A continues the lane; floor B lies beside it; the next flight leaves B heading back.
            Vector2 returnStart = node.Edge + right * (side * width);
            Vector2 centre = node.Edge + forward * (PieceRun / 2f) + right * (side * width / 2f);
            float padUnderside = node.Height - route.Kit.FloorThickness;
            if (CrossesItself(node, node.Edge + forward * (PieceRun / 2f), node.Heading, width, padUnderside, 0, route.Kit)
                || CrossesItself(node, centre + right * (side * width / 2f), node.Heading, width, padUnderside, 0, route.Kit)
                || !route.InCorridor(centre + right * (side * width / 2f))) continue;
            Fit(node.Edge, forward, right, half, node.Height, 0f, ground, out float floatA, out float buriedA);
            Fit(returnStart, forward, right, half, node.Height, 0f, ground, out float floatB, out float buriedB);
            float buried = Mathf.Max(buriedA, buriedB);
            if (buried > route.MaxBuried) continue;
            var setOff = BackRunAfter(node, Wrap(node.Heading + 180f), StairPieceKind.Landing, route);
            if (setOff == null) continue;
            float floatGap = (floatA + floatB) / 2f;
            var child = new Node
            {
                Parent = node,
                Kind = StairPieceKind.Landing,
                Start = node.Edge,
                Edge = returnStart,
                Heading = Wrap(node.Heading + 180f),
                Height = node.Height,
                Switchback = side,
                BackRun = setOff.Value.backRun,
                FacedStairs = setOff.Value.faced,
                Centre = centre,
                Depth = node.Depth + 1,
                Float = floatGap,
                Buried = buried,
            };
            child.Cost = node.Cost + PieceRun + width
                + FloatWeight * floatGap + BuriedWeight * buried
                + SwitchbackWeight + SwitchbackSteepPrice(Steepness(centre, ground));
            child.Goal = route.Advance(node.Goal, child.Edge);
            child.Score = child.Cost + HeuristicWeight * Remaining(child, route);
            next.Add(child);
        }
    }

    /// <summary>Where a piece turned <paramref name="turn"/> heading steps off one ending on <paramref name="edge"/>
    /// starts (its back edge's centre), and the corner it pivots on: the inside corner (the right one on a right
    /// turn), or for a floor after a floor the outside one, so the two overlap.</summary>
    private static (Vector2 start, Vector2 pivot) TurnOff(Vector2 edge, float fromHeading, float heading, int turn, float half, bool floorOnFloor)
    {
        if (turn == 0) return (edge, edge);
        float side = (turn > 0) != floorOnFloor ? 1f : -1f;
        Vector2 pivot = edge + Right(fromHeading) * (side * half);
        return (pivot - Right(heading) * (side * half), pivot);
    }

    /// <summary>Tests: a flight of stairs laid out on the given headings from a foot, every stair held out from the
    /// face on its <paramref name="side"/>, emitted as a search would emit it.</summary>
    internal static StairPlan LayFlight(Vector2 foot, float footHeight, IReadOnlyList<float> headings, StairKit kit, Func<float, float, float> ground, int side)
    {
        var plan = new StairPlan { Kit = kit.Name, Reached = true };
        Node node = new() { Edge = foot, Start = foot, Heading = headings[0], Height = footHeight };
        foreach (float h in headings)
        {
            bool first = node.Parent == null;
            int turn = first ? 0 : Mathf.RoundToInt((Wrap(h - node.Heading + 180f) - 180f) / HeadingStep);
            (Vector2 start, Vector2 pivot) = first ? (node.Edge, node.Edge) : TurnOff(node.Edge, node.Heading, h, turn, PieceRun / 2f, false);
            node = new Node
            {
                Parent = node, Kind = StairPieceKind.Stair, Start = start, Edge = start + Forward(h) * PieceRun, Heading = h,
                Height = node.Height + PieceRise, TurnSteps = turn, Pivot = pivot, Centre = start + Forward(h) * (PieceRun / 2f),
                Depth = node.Depth + 1, Cantilever = side,
            };
        }
        Emit(plan, node, ground, PieceRun, kit);
        return plan;
    }

    /// <summary>How far the walking surface of a piece stands above the ground
    /// (mean over its samples) and how deep the ground buries it (worst sample).</summary>
    private static void Fit(Vector2 start, Vector2 dir, Vector2 right, float half, float baseHeight, float rise,
        Func<float, float, float> ground, out float floatGap, out float buried)
    {
        floatGap = 0f;
        buried = 0f;
        int n = 0;
        for (int i = 1; i <= 3; i++)
        {
            float along = PieceRun * i / 4f;
            float surface = baseHeight + rise * along / PieceRun;
            for (int s = -1; s <= 1; s++)
            {
                Vector2 p = start + dir * along + right * (s * half * 0.9f);
                float g = ground(p.x, p.y);
                floatGap += Mathf.Max(0f, surface - g);
                buried = Mathf.Max(buried, g - surface);
                n++;
            }
        }
        floatGap /= n;
    }

    /// <summary>Whether a piece of <paramref name="width"/> across and a piece's run
    /// along, centred at <paramref name="centre"/> facing <paramref name="heading"/>,
    /// overlaps any piece laid before <paramref name="node"/>, seen from above, where it
    /// may not. A piece held out from the face (<paramref name="cantilever"/> its wall side)
    /// drops nothing into what lies below it, so it may pass over a lower piece of the same
    /// staircase where its lowest part, arch or pillar, keeps <see cref="ClearanceHeight"/>
    /// of headroom over that piece's top (with a cantilever design, a switchback can run under itself more easily, without
    /// supports dropping into the stairs below it). A piece on columns may pass over nothing: its columns stand in
    /// whatever is below. Earlier cantilevers' strips count as their pieces do.</summary>
    private static bool CrossesItself(Node node, Vector2 centre, float heading, float width, float underside = 0f, int cantilever = 0, StairKit? kit = null)
    {
        CantileverKit? ck = kit?.Cantilever;
        bool held = cantilever != 0 && ck != null;
        float stripReach = width / 2f + CantileverKit.Strip / 2f;
        Vector2 strip = centre + Right(heading) * (cantilever * stripReach);
        // Lowest part under the piece, and in the strip beside it.
        float pieceLow = held ? underside - ck!.ArchHeight : float.NegativeInfinity;
        float stripLow = held ? underside - CantileverKit.StripDepth : float.NegativeInfinity;
        bool Clash(Vector2 c, float h, float across, float top)
        {
            if (Overlap(centre, heading, width, c, h, across) && !Clears(pieceLow, top)) return true;
            return held && Overlap(strip, heading, CantileverKit.Strip, c, h, across) && !Clears(stripLow, top);
        }
        for (Node? earlier = node.Parent; earlier != null && earlier.Parent != null; earlier = earlier.Parent)
        {
            if (Vector2.Distance(earlier.Centre, centre) > 2f * (width + PieceRun) + CantileverKit.Strip) continue;
            if (earlier.Switchback != 0)
            {
                // Its two floors, at the heading of the flight that arrived on them.
                float h = earlier.Parent.Heading;
                Vector2 a = earlier.Start + Forward(h) * (PieceRun / 2f);
                Vector2 b = a + Right(h) * (earlier.Switchback * width);
                if (Clash(a, h, width, earlier.Height) || Clash(b, h, width, earlier.Height)) return true;
                continue;
            }
            if (Clash(earlier.Centre, earlier.Heading, width, earlier.Height)) return true;
            // Its strip: pillars under, a block on top level with the piece's top edge.
            if (earlier.Cantilever != 0
                && Clash(earlier.Centre + Right(earlier.Heading) * (earlier.Cantilever * stripReach), earlier.Heading, CantileverKit.Strip, earlier.Height))
                return true;
        }
        return false;
    }

    /// <summary>Tests: <see cref="CrossesItself"/> for a new piece after <paramref name="laid"/> (oldest
    /// first; each a piece's centre, heading, top surface and cantilever side), the last of which it
    /// carries on from and is never checked against.</summary>
    internal static bool CrossesLaid(IReadOnlyList<(Vector2 centre, float heading, float top, int cantilever)> laid, Vector2 centre, float heading,
        float underside, int cantilever, StairKit kit, float width = 2f)
    {
        var node = new Node();
        foreach (var p in laid) node = new Node { Parent = node, Centre = p.centre, Heading = p.heading, Height = p.top, Cantilever = p.cantilever };
        return CrossesItself(node, centre, heading, width, underside, cantilever, kit);
    }

    /// <summary>Whether a part whose lowest point is <paramref name="low"/> leaves walking headroom
    /// over a surface at <paramref name="top"/>: only a cantilever, and only with the overhang allowed.</summary>
    private static bool Clears(float low, float top) => low - top >= ClearanceHeight;

    /// <summary>Foot stairs this thread's searches priced for standing out past a ledge's edge (the ledge price paid):
    /// RoadStairRuns.Lay lays a failed stretch again without the price only where its layouts paid it.</summary>
    [ThreadStatic] private static int t_ledgePrices;
    internal static int LedgePrices => t_ledgePrices;

    /// <summary>Per thread (the search runs on the thread that asks): the foot stairs are not priced for standing out past a ledge's
    /// edge. Set only for a stretch laid again after its priced layouts failed (RoadStairRuns.Lay), where the price had closed the way.</summary>
    [ThreadStatic] private static bool t_unpricedLedge;
    internal static bool UnpricedLedge { get => t_unpricedLedge; set => t_unpricedLedge = value; }

    /// <summary>Two pieces, <paramref name="acrossA"/> by a run, overlap by more than
    /// <see cref="OverlapTolerance"/> seen from above (separating axes).</summary>
    private static bool Overlap(Vector2 ca, float ha, float acrossA, Vector2 cb, float hb, float acrossB)
    {
        Vector2 fa = Forward(ha), ra = Right(ha), fb = Forward(hb), rb = Right(hb);
        return !Separated(fa, ca, fa, ra, acrossA, cb, fb, rb, acrossB) && !Separated(ra, ca, fa, ra, acrossA, cb, fb, rb, acrossB)
            && !Separated(fb, ca, fa, ra, acrossA, cb, fb, rb, acrossB) && !Separated(rb, ca, fa, ra, acrossA, cb, fb, rb, acrossB);
    }

    /// <summary>The two pieces are apart along <paramref name="axis"/>.</summary>
    private static bool Separated(Vector2 axis, Vector2 ca, Vector2 fa, Vector2 ra, float acrossA, Vector2 cb, Vector2 fb, Vector2 rb, float acrossB)
    {
        float apart = Mathf.Abs(Vector2.Dot(cb - ca, axis));
        return apart >= Reach(fa, ra, acrossA, axis) + Reach(fb, rb, acrossB, axis) - OverlapTolerance;
    }

    private static float Reach(Vector2 f, Vector2 r, float across, Vector2 axis) =>
        Mathf.Abs(Vector2.Dot(f, axis)) * PieceRun / 2f + Mathf.Abs(Vector2.Dot(r, axis)) * across / 2f;

    private static void Emit(StairPlan plan, Node last, Func<float, float, float> ground, float width, StairKit kit, bool sunkFoot = false)
    {
        var steps = new List<Node>();
        for (Node? n = last; n != null && n.Parent != null && !n.Placed; n = n.Parent) steps.Add(n);
        steps.Reverse();

        int lanes = Mathf.Max(1, Mathf.RoundToInt(width / PieceRun));
        int sunkPieces = 0;
        // One more stair below the first, a whole step down into the ground, so the
        // flight comes up out of the road rather than starting on top of it with a
        // lip (one extra stair clipped down into the earth makes it connect better). Buried on purpose: no earthwork uncovers it.
        if (sunkFoot && steps.Count > 0 && steps[0].Kind == StairPieceKind.Stair)
        {
            Node lowest = steps[0];
            AddRow(plan, kit, StairPieceKind.Stair, kit.Stair, lowest.Start - Forward(lowest.Heading) * PieceRun, lowest.Heading,
                lowest.Parent!.Height - PieceRise, lanes, ground, mark: false, cut: false);
            sunkPieces = plan.Pieces.Count;
            plan.SunkPieces = sunkPieces; plan.SunkClearances = plan.Clearances.Count;
            plan.Foot = new Vector3(lowest.Start.x, lowest.Parent!.Height, lowest.Start.y);
        }
        foreach (Node step in steps)
        {
            Node parent = step.Parent!;
            plan.MaxBuried = Mathf.Max(plan.MaxBuried, step.Buried);
            if (step.Switchback != 0)
            {
                AddRow(plan, kit, StairPieceKind.Landing, kit.Floor, step.Start, parent.Heading, parent.Height, lanes, ground, mark: true);
                AddRow(plan, kit, StairPieceKind.Landing, kit.Floor, step.Edge, parent.Heading, parent.Height, lanes, ground, mark: true);
                plan.Landings += 2 * lanes;
                plan.Switchbacks++;
                plan.Turns++;
                plan.WalkedLength += PieceRun + width;
                continue;
            }
            if (step.TurnSteps != 0 && step.Overlaps)
            {
                plan.Turns++;
                plan.TurnSteps += System.Math.Abs(step.TurnSteps);
                plan.OverlapTurnSteps += System.Math.Abs(step.TurnSteps);
            }
            else if (step.TurnSteps != 0)
            {
                // The wedge, filled with flat pieces level with the top of the piece before; off a cantilevered
                // piece, held by an arch and a strip column, not stood on columns down the drop.
                bool heldWedge = parent.Cantilever != 0 && kit.Cantilever != null && lanes == 1;
                if (heldWedge) AddTurnSupport(plan, kit, parent.Start, parent.Heading, parent.Height, parent.Cantilever, step.TurnSteps, width, ground);
                foreach (FillerPiece filler in StairFiller.Choose(step.Pivot, parent.Heading, step.TurnSteps, width, parent.Kind == StairPieceKind.Landing, kit.Fillers))
                    AddPiece(plan, kit, StairPieceKind.Filler, filler.Prefab, filler.Back, filler.Heading, parent.Height, filler.Across, filler.Depth, ground, mark: false,
                        held: heldWedge);
                plan.Turns++;
                plan.TurnSteps += System.Math.Abs(step.TurnSteps);
            }
            plan.LastRowPieces = plan.Pieces.Count; plan.LastRowClearances = plan.Clearances.Count;
            plan.LastRowKind = step.Kind; plan.LastRowStart = step.Start; plan.LastRowHeading = step.Heading; plan.LastRowHeight = parent.Height;
            AddRow(plan, kit, step.Kind, step.Kind == StairPieceKind.Stair ? kit.Stair : kit.Floor, step.Start, step.Heading, parent.Height, lanes, ground, mark: true,
                cantilever: lanes == 1 ? step.Cantilever : 0);
            if (step.Cantilever != 0 && lanes == 1 && kit.Cantilever != null)
            {
                plan.Cantilevered++;
                plan.SteepestFace = Mathf.Max(plan.SteepestFace, step.Face);
            }
            plan.LastRowPieceCount = plan.Pieces.Count - plan.LastRowPieces; plan.LastRowClearanceCount = plan.Clearances.Count - plan.LastRowClearances;
            if (step.Kind == StairPieceKind.Stair) plan.Stairs += lanes; else plan.Landings += lanes;
            plan.WalkedLength += PieceRun;
        }

        Node first = steps.Count > 0 ? steps[0].Parent! : last;
        plan.StartsOnFloor = steps.Count > 0 && steps[0].Kind == StairPieceKind.Landing && steps[0].Switchback == 0;
        plan.SetOffBack = 0;
        foreach (Node n in steps) plan.SetOffBack = Math.Max(plan.SetOffBack, n.BackRun);
        plan.StartHeading = steps.Count > 0 ? steps[0].Heading : last.Heading;
        plan.EndHeading = last.Heading;
        plan.Rise = last.Height - first.Height;
        plan.End = new Vector3(last.Edge.x, last.Height, last.Edge.y);
        GuardEnds(plan, kit, first.Edge, last.Edge, ground, sunkPieces);
    }

    /// <summary>Highest a walking surface may stand over the ground a player walks onto it from:
    /// the player has no step-up, so a stair's top over a clearing is climbed only by a jump.</summary>
    public const float StepUp = 0.3f;

    /// <summary>One row of pieces across the stair.</summary>
    private static void AddRow(StairPlan plan, StairKit kit, StairPieceKind kind, string prefab, Vector2 start, float heading,
        float baseHeight, int lanes, Func<float, float, float> ground, bool mark, bool cut = true, int cantilever = 0)
    {
        Vector2 right = Right(heading);
        for (int lane = 0; lane < lanes; lane++)
            AddPiece(plan, kit, kind, prefab, start + right * ((lane - (lanes - 1) / 2f) * PieceRun), heading, baseHeight, PieceRun, PieceRun, ground, mark, cut, cantilever);
    }

    /// <summary>One piece whose back edge is centred on <paramref name="back"/>, its
    /// clearance and cuts, then a pillar column under it if it does not rest on the ground,
    /// or, held out from a face on its <paramref name="cantilever"/> side, its cantilever.</summary>
    private static void AddPiece(StairPlan plan, StairKit kit, StairPieceKind kind, string prefab, Vector2 back, float heading,
        float baseHeight, float across, float depth, Func<float, float, float> ground, bool mark, bool cut = true, int cantilever = 0, bool held = false)
    {
        Vector2 dir = Forward(heading);
        Vector2 right = Right(heading);
        Vector2 centre = back + dir * (depth / 2f);
        var piece = new StairPiece { Kind = kind, Prefab = prefab };
        float underside;
        bool stair = prefab == kit.Stair;
        if (stair)
        {
            piece.Position = new Vector3(centre.x, baseHeight, centre.y);
            piece.YawDegrees = Wrap(heading + 180f); // the piece climbs toward its local -z
            underside = baseHeight;
        }
        else
        {
            // A floor or a filler: its top is the walking surface.
            piece.Position = new Vector3(centre.x, baseHeight - kit.FloorTop, centre.y);
            piece.YawDegrees = prefab == kit.Floor ? heading : Wrap(heading + 180f);
            underside = baseHeight - kit.FloorThickness;
        }
        plan.Pieces.Add(piece);
        AddClearance(plan, stair, back, dir, heading, baseHeight, baseHeight - kit.FloorThickness, across, depth);
        if (cut) AddCuts(plan, stair, back, dir, right, baseHeight, across, depth, ground);

        float g = ground(centre.x, centre.y);
        float gap = underside - g;
        CantileverKit? holder = cantilever != 0 ? kit.Cantilever : null;
        // A piece held out from a face stands over the drop by design: not counted as floating.
        if (mark && holder == null && !held) plan.MaxFloat = Mathf.Max(plan.MaxFloat, gap);
        if (holder != null)
        {
            piece.Cantilevered = true;
            AddCantilever(plan, holder, back, heading, underside, cantilever, across, ground);
            return;
        }
        // A turn's filler off a cantilevered piece rests on the turn's arch (AddTurnSupport).
        if (held) { piece.Cantilevered = true; return; }
        // A piece stands on the ground only sunk into it: one planned to sit on
        // the surface hovers a few centimetres above it in game (the terrain is
        // not the plan's to the centimetre, and the road levels the ground at a
        // junction), is not grounded, and Valheim drops it -- a black marble
        // staircase lost a quarter of its pieces that way. Anything less than
        // SupportBury into the ground gets a column driven into it.
        if (gap <= -SupportBury) return;
        // Stack from the underside down until the lowest pillar's whole base is
        // SupportBury under the lowest ground beneath it: sunk only at its centre,
        // a pillar on a slope showed its base on the downhill side, and one broke
        // (the whole base must clip into the ground).
        float lowest = g;
        for (int cx = -1; cx <= 1; cx += 2)
            for (int cz = -1; cz <= 1; cz += 2)
                lowest = Mathf.Min(lowest, ground(centre.x + cx * SupportHalfWidth, centre.y + cz * SupportHalfWidth));
        Func<int, int, float> vertex = (i, k) => ground(i, k);
        for (float top = underside; BuriedPieces.ColumnGoesOn(top, lowest, kit.Support, kit.SupportHeight, centre, heading, vertex); top -= kit.SupportHeight)
        {
            plan.Pieces.Add(new StairPiece
            {
                Kind = StairPieceKind.Support,
                Prefab = kit.Support,
                Position = new Vector3(centre.x, top - kit.SupportHeight / 2f, centre.y),
                YawDegrees = heading,
            });
        }
    }

    /// <summary>The piece's body and the headroom above it, in stretches along
    /// the walk: a stair rises a step per stretch, a floor or block is level.</summary>
    private static void AddClearance(StairPlan plan, bool stair, Vector2 laneStart, Vector2 dir, float heading, float baseHeight, float floorUnderside, float across, float depth)
    {
        int stretches = stair ? ClearanceStretches : 1;
        float length = depth / stretches;
        for (int i = 0; i < stretches; i++)
        {
            // A stair's walking surface at the middle of this stretch; a floor's or block's is its top.
            float surface = stair ? baseHeight + PieceRise * (i + 0.5f) / stretches : baseHeight;
            float bottom = stair ? baseHeight : floorUnderside;
            float top = surface + ClearanceHeight;
            Vector2 mid = laneStart + dir * (length * (i + 0.5f));
            plan.Clearances.Add(new StairClearance(
                new Vector3(mid.x, (bottom + top) / 2f, mid.y),
                new Vector3(across / 2f, (top - bottom) / 2f, length / 2f),
                heading, surface));
        }
    }

    /// <summary>Unity's yaw: 0 faces +z, 90 faces +x.</summary>
    public static Vector2 Forward(float headingDegrees)
    {
        float r = headingDegrees * Mathf.Deg2Rad;
        return new Vector2(Mathf.Sin(r), Mathf.Cos(r));
    }

    public static Vector2 Right(float headingDegrees)
    {
        float r = headingDegrees * Mathf.Deg2Rad;
        return new Vector2(Mathf.Cos(r), -Mathf.Sin(r));
    }

    public static float Bearing(Vector2 from, Vector2 to) => Wrap(Mathf.Atan2(to.x - from.x, to.y - from.y) * Mathf.Rad2Deg);

    public static float SnapHeading(float degrees) => Wrap(Mathf.Round(degrees / HeadingStep) * HeadingStep);

    public static float Wrap(float degrees)
    {
        float d = degrees % 360f;
        return d < 0f ? d + 360f : d;
    }
}
