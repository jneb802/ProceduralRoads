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
    internal StairPiece Copy() => (StairPiece)MemberwiseClone();
    public StairPieceKind Kind;
    public string Prefab = "";
    public Vector3 Position;
    public float YawDegrees;
    /// <summary>A walked piece held out from a face, or part of what holds it, as an older build's layout recorded it
    /// (saved staircases; the kits now lay their own supports, StairSupport): a support so marked was clipped into the
    /// face beside its piece, not a column down to the ground.</summary>
    public bool Cantilevered;
    /// <summary>The turn its plan's stats (Turns, TurnSteps, OverlapTurnSteps) count with this piece: a wedge's first filler,
    /// a floor turned over a floor, a switchback's first floor. Its heading steps, and whether floor over floor; none if null.
    /// A piece cut away takes its turn out of the stats.</summary>
    internal (int Steps, bool Overlap)? Turn;
    /// <summary>The step sunk a whole rise under a staircase's foot (Emit's sunk foot): buried in the ground it comes up out
    /// of on purpose, never uncovered. Marked on the piece so a plan joined of two staircases (RoadStairRuns.Join) keeps the
    /// second's too, which its SunkPieces count does not reach.</summary>
    internal bool Sunk;
}

/// <summary>
/// Space a walker on the stair needs clear of rock (its walking box): one stretch of a piece, its walking surface's width,
/// from just over its walking surface to <see cref="RoadRockPolicy.ClearanceHeight"/> above it (StairLayout.AddClearance). <see cref="Centre"/> and <see cref="Half"/> are in the
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
    /// <summary>Every field and new lists. Cache copies share immutable pieces; a tentative finish requests
    /// independent pieces too, so its callback cannot change the snapshot it may restore.</summary>
    internal StairPlan Copy(bool deepPieces = false)
    {
        var c = (StairPlan)MemberwiseClone();
        c.Pieces = deepPieces ? Pieces.ConvertAll(p => p.Copy()) : new List<StairPiece>(Pieces);
        c.Clearances = new List<StairClearance>(Clearances);
        c.Cuts = new List<Vector3>(Cuts);
        return c;
    }

    /// <summary>Restore a tentative layout together with all its accounting and last-row metadata: every field of a deep
    /// copy of <paramref name="saved"/>, so a field added later is restored too (copied by hand, the longest flight was
    /// left out).</summary>
    internal void Restore(StairPlan saved)
    {
        StairPlan copy = saved.Copy(deepPieces: true);
        foreach (System.Reflection.FieldInfo field in s_fields) field.SetValue(this, field.GetValue(copy));
    }

    /// <summary>Every field of a plan, its lists' too (<see cref="Restore"/>).</summary>
    private static readonly System.Reflection.FieldInfo[] s_fields = typeof(StairPlan).GetFields(
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

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
    /// <summary>Times the search turned away a piece, a switchback or a finish only for how it arrives at a place's landing
    /// (StairLayout.ArrivalHeld; a root held to climb away from the place counts once): 0 where the hold never bore on it,
    /// so the same layout searched without it is searched the same way (RoadStairRuns.PlanPlacedSiteApproach and
    /// LaySiteApproach lay it again not facing in only where it did).</summary>
    public int HeldToFace;
    /// <summary>Of the first pieces the search tried off its root, how many fitted (<see cref="FitOffRoot"/>) and how many
    /// were turned away for standing deeper in the ground than it allows (<see cref="BuriedOffRoot"/>): none fitted and some
    /// buried, a layout laid up from a place's landing was refused for burial at the point it was aimed at on the landing's
    /// lip (RoadStairRuns.BuriedAtItsAim).</summary>
    public int FitOffRoot, BuriedOffRoot;
    public string Reason = "";
    /// <summary>True when the request ran downhill and was laid out from the other end.</summary>
    public bool Reversed;
    /// <summary>Waypoints of the coarse route the search followed (1 when it went straight for the goal).</summary>
    public int Waypoints = 1;
    /// <summary>Times a leg was undone because the next one found nothing that fits.</summary>
    public int Backtracks;
    public int Stairs;
    /// <summary>The most stairs it climbs without a floor between (StairLayout.RestEveryMax is the aim).</summary>
    public int LongestFlight;
    public int Landings;
    public int Turns;
    /// <summary>Sum of every turn's size in 22.5 degree steps, switchbacks' half-turns left out.</summary>
    public int TurnSteps;
    /// <summary>The part of <see cref="TurnSteps"/> taken floor to floor, overlapping, with no filler.</summary>
    public int OverlapTurnSteps;
    public int Switchbacks;
    public float Rise;
    public float WalkedLength;
    /// <summary>Largest gap between a piece's underside and the ground below its centre, before supports.</summary>
    public float MaxFloat;
    /// <summary>Largest depth the ground stands above a walking surface anywhere it was sampled.</summary>
    public float MaxBuried;
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
    /// <summary>Where a place's ground meets a floor laid out onto it from the foot on its landing
    /// (<see cref="StairLayout.AddPlaceFloor"/>): that floor's far edge's middle, recorded as where the place meets the staircase
    /// (StairRun.ToMeet); null where none was laid.</summary>
    public Vector2? PlaceMeet;
    /// <summary>The sunk step under its foot: its pieces and clearances, first in the lists (0 when there is none).</summary>
    public int SunkPieces, SunkClearances;
    /// <summary>The height of the road its foot is met at on a stair sunk under it (StairLayout.FootToRoad, a foot carried to
    /// a crossing's end); null where its foot meets its road within a step.</summary>
    public float? SunkStairMeets;
    /// <summary>Where its first step's low edge is (its foot), when it has a sunk step under it.</summary>
    public Vector3? Foot;
    /// <summary>Stairs added down from its foot to its road (<see cref="StairLayout.LowerFoot"/>).</summary>
    public int FootLowered;
    /// <summary>Level floors taken away at its foot and its top for the road to carry on over to the landing row kept next
    /// to its stair (<see cref="StairLayout.TrimEndFloors"/>, RoadStairRuns.CarryRoadOntoStairs); 0 where it kept them.</summary>
    public int FootFloorsTrimmed, TopFloorsTrimmed;
    public Vector3 End;
    /// <summary>The ring at the road's end the staircase was laid against (<see cref="RoadEndRing"/>): set off from it at its
    /// foot, or arrived on it at its top; null where its ends meet no road's end.</summary>
    public RoadEndRing? Ring;
    /// <summary>Where on its ring a staircase laid from a road's end set off: its first piece's back edge on the circle. The road's
    /// end is that point less its offset from the ring's centre wherever the foot is moved later (StairLayout.LowerFoot).</summary>
    public Vector2? RingStart;
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
///   below it, four (90) off a flat run (a flat run is a stair run that does
///   not climb), three off a switchback. A landing
///   lines up with the stair below it, save as a staircase's last row, where it
///   meets a road or another flight: there it may turn off the stair as a stair
///   turns off it, up to four. Its wedge is filled with
///   flat pieces on the hammer's grid that <see cref="StairFiller"/> chooses
///   turn by turn: the fewest that cover it and show least. A single floor
///   at the half angle (33.75 degrees for a 67.5 degree turn) left the
///   wedge open and the junction did not connect up; a fan of 2x1 blocks
///   hinged on the inside corner covered it but stuck out.
/// * A switchback is a landing two lanes wide: the flight arrives on one
///   floor, a second floor lies beside it, and the next flight leaves that
///   one heading back the way it came. It is how a stair climbs ground too
///   steep to cross in one traverse: a through climb, one leg up from the pad
///   and the other down, never both flat. Nor does a chain of two staircases
///   double back alongside itself over a crest or through a pit
///   (<see cref="StairChain.BackTracks"/>).
/// * Every piece is held up as its kit lays it at first build (<see cref="StairSupport"/>): a wall under its downhill
///   half, an arch or corbel off the slope, or black marble's cornices; the search prices each piece by the same choice
///   and avoids a piece nothing within reach holds. A switchback's pad is priced by the steepness round it, so the search
///   turns on benches.
///
/// The search is best-first over whole pieces. A partial staircase is scored by
/// the length walked so far, how badly its pieces fit the ground, and how
/// often it turned, plus the least walking still needed: at least the
/// distance left, and at least two metres for every metre still to climb.
/// </summary>
public static partial class StairLayout
{
    [ThreadStatic] private static long s_ids;

    /// <summary>
    /// The version of this layout: how a staircase is searched and laid. Raised whenever a change would lay a run
    /// differently, so a later build can tell the runs an older layout laid (<see cref="StairRun.Layout"/>).
    /// <para>1: the first release's layout. 2: raised-landings checked over the complete walked surface. 3: a staircase meets
    /// its road's end on the ring round it (RoadEndRing), and an approach's arriving flight faces into its place
    /// (RoadStairRuns.IntoThePlace). 4: a black marble piece whose cornice column would hang more than
    /// StairSupport.MostHungCourses courses over the first that touches the ground is refused (the game's support rule
    /// breaks it); an approach's arriving flight held within ArrivalSteps (90 degrees, was 45) of facing into its place, and
    /// one buried where it aims on the place's lip tried at the lip points beside it (RoadStairRuns.BuriedAtItsAim). 5: a
    /// road staircase starts at its first real stair: the level floors at its foot, and at its top past TopLandings, are
    /// road where the road can be carried over them (RoadStairRuns.CarryRoadOntoStairs). 6: an approach's foot on a place's
    /// landing gets a floor laid out onto the place's ground on its side facing the place where the step off it is not level
    /// but such a floor lands level (AddPlaceFloor).</para>
    /// The one owner of the saved network (docs/OWNERS.md).
    /// </summary>
    public const int LayoutVersion = 6;

    /// <summary>The one owner of step and stair geometry (docs/OWNERS.md).</summary>
    public const float PieceRun = 2f;
    /// <summary>The one owner of step and stair geometry (docs/OWNERS.md).</summary>
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
    /// <summary>How far a stair turns off the stair before it: up to 67.5 degrees, its wedge filled (the small
    /// turns 22.5, 45 and 67.5 stair to stair; 90 and more turn on a floor).</summary>
    public const int MaxStairTurnSteps = 3;
    /// <summary>How far a stair may turn as it leaves a flat run (0 to 90 degrees): a flat run is a stair run that does
    /// not climb, so the stair pivots on its inside corner and the wedge is filled as between two stairs. The run itself
    /// turns off the stair below it as far only as a staircase's last row (where it meets a road or another flight); elsewhere
    /// it lines up with it (a landing turned 45 degrees off the stair below left a gap to the stair above), and turns floor
    /// over floor.</summary>
    public const int MaxJoinTurnSteps = 4;
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
    /// below the goal's ground: level with it (<see cref="RoadNetworkGenerator.ArrivalLevel"/>).</summary>
    public const float ArriveBelow = RoadNetworkGenerator.ArrivalLevel;
    /// <summary>A stair's clearance is cut into this many stretches along it, so it follows the steps up: one a cut point's
    /// spacing long (<see cref="CutSpacing"/>), 4 to a stair, each rising a quarter of its rise. With 2 a stretch's box reached
    /// 0.4 m under the walk at its high end, into the stair's body and the rock it lies on, and the carve took every chunk
    /// there whole.</summary>
    public const int ClearanceStretches = (int)(PieceRun / CutSpacing);

    /// <summary>Cost per metre a piece floats above the ground (mean over it): mostly
    /// hugging the terrain is preferred, some variation is fine.</summary>
    private const float FloatWeight = 3f;
    /// <summary>Cost per metre the ground stands over a walking surface (worst sample):
    /// the earthwork that exposes it. Hugging is preferred to digging, so it costs
    /// more than floating.</summary>
    private const float BuriedWeight = 6f;
    private const float TurnWeight = 0.5f;
    /// <summary>A stair turned 67.5 degrees off the stair before costs this more: the sharpest small turn, laid where one
    /// fits, not chained into a curl where a switchback doubles back (three in a row turned a flight right round).</summary>
    private const float SharpTurnPrice = 3f;
    /// <summary>Nor within this many pieces of the foot: how a staircase sets off from its road is the foot's to decide.</summary>
    private const int SharpTurnFromDepth = 3;

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
        /// <summary>Stairs climbed since the last floor (a rest, a turn's or a switchback's landing alike), and how many this
        /// run of stairs is due to climb before its next rest (<see cref="RestDueAt"/>).</summary>
        public int SinceRest, RestDue;
        /// <summary>Held to a facing (<see cref="Route.Facing"/>): pieces from the foot that set off back, more than
        /// <see cref="FacingSteps"/> off it, none facing it yet; -1 once one has (and with no facing).</summary>
        public int BackRun;
        /// <summary>Held to a facing: stairs climbed since the first piece that faced it (capped at
        /// <see cref="FirstFlightStairs"/>); -1 before one has.</summary>
        public int FacedStairs = -1;
        /// <summary>Stairs between the root and this piece, this one included, up to <see cref="ArrivalStairs"/>: a place's
        /// arrival held from the root (<see cref="Route.IntoPlaceFromRoot"/>) is held until that many.</summary>
        public int Climbed;
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
    /// <paramref name="exitAllowed"/>: where the top's exit edge, half a piece past its centre, may finish.
    /// <paramref name="after"/>: the top row of a staircase already laid at <paramref name="from"/>. Climbing on
    /// from it, the staircase carries on as that one would have: its first piece's back edge on the row's far
    /// edge at its walking height, turning off it as any piece off the one below (inside-corner pivot, filler,
    /// no switchback straight off it), and no sunk foot (MountainCave02 -2560,1536: the final staircase builds off the snap
    /// points of what is already there). Laid
    /// down to it (its goal below the row), it finishes head-on at the row, facing back along it.
    /// <paramref name="intoPlace"/>: <paramref name="to"/> is a place's landing, and this the point a walker coming onto it
    /// faces (RoadStairRuns.IntoThePlace): the flight that arrives there faces it, climbing or coming down
    /// (<see cref="ArrivalHeld"/>), in place of <paramref name="leaving"/>.
    /// <paramref name="onLanding"/>: the place's landing a top climbing to it arrives on (RoadStairRuns.PlanPlacedSiteApproach):
    /// it is aimed at <paramref name="to"/>, but finishes as soon as it steps onto the landing, however far from its aim
    /// (<see cref="Arrived"/>).
    public static StairPlan Build(Vector2 from, Vector2 to, Func<float, float, float> ground, float width = 2f, StairKit? kit = null,
        int maxExpansions = MaxExpansions, float? arriving = null, float? leaving = null, IReadOnlyList<Vector2>? corridor = null,
        int totalExpansions = int.MaxValue, int stallExpansions = int.MaxValue, int topLandings = 0, bool sunkFoot = false,
        float maxBuried = StairCover.RoadStaircase, float? arriveWithin = null, Func<Vector2, bool>? allowed = null, float? footHeight = null,
        StairContinuation? after = null, bool relayPads = true, float? facing = null, float? topFacing = null,
        Func<Vector3, bool>? surfaceAllowed = null, Func<Vector2, bool>? exitAllowed = null, RoadEndRing? ring = null, Vector2? intoPlace = null,
        Func<Vector2, bool>? onLanding = null)
    {
        StairPlan plan = BuildOnce(from, to, ground, width, kit, maxExpansions, arriving, leaving, corridor, totalExpansions, stallExpansions,
            topLandings, sunkFoot, maxBuried, arriveWithin, allowed, footHeight, after, stairFirst: false, facing, topFacing, surfaceAllowed, exitAllowed, ring, intoPlace, onLanding);
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
                topLandings, sunkFoot, maxBuried, arriveWithin, allowed, footHeight, after, stairFirst: true, facing, topFacing, surfaceAllowed, exitAllowed, ring, intoPlace, onLanding);
            stair.Expansions += plan.Expansions;
            stair.HeldToFace += plan.HeldToFace;
            if (stair.Reached) return stair;
            plan.Expansions = stair.Expansions;
            plan.HeldToFace = stair.HeldToFace;
        }
        return plan;
    }

    private static StairPlan BuildOnce(Vector2 from, Vector2 to, Func<float, float, float> ground, float width, StairKit? kit,
        int maxExpansions, float? arriving, float? leaving, IReadOnlyList<Vector2>? corridor,
        int totalExpansions, int stallExpansions, int topLandings, bool sunkFoot,
        float maxBuried, float? arriveWithin, Func<Vector2, bool>? allowed, float? footHeight,
        StairContinuation? after, bool stairFirst, float? facing, float? topFacing, Func<Vector3, bool>? surfaceAllowed,
        Func<Vector2, bool>? exitAllowed, RoadEndRing? ring, Vector2? intoPlace, Func<Vector2, bool>? onLanding)
    {
        // Laid up from the place (its landing below the road), its root faces away from it as a road's leaving is held
        // (ArrivalSteps), and further, through its arriving stairs (ArrivalHeld); climbing to it, its top faces it from where it ends.
        if (intoPlace.HasValue && ground(to.x, to.y) < ground(from.x, from.y)) leaving = Bearing(to, intoPlace.Value);
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
        // The ring at a road's end (RoadEndRing): at the foot the search sets off from it, at the top it arrives on it. Not where
        // the staircase carries on from a row already laid.
        RoadEndRing? footRing = null, topRing = null;
        if (ring is RoadEndRing r)
        {
            if (!(after.HasValue && !plan.Reversed) && Vector2.Distance(r.Centre, from) < 0.01f) footRing = r;
            else if (!(after.HasValue && plan.Reversed) && Vector2.Distance(r.Centre, to) < 0.01f) topRing = r;
            // Across the road's end the ground is the road's, at its height (RoadEndRing.RoadsGround): a flight set off from the
            // circle stands on the road there, not on the slope the road cuts.
            if (footRing.HasValue || topRing.HasValue) ground = r.RoadsGround(ground);
        }
        // Down to a laid row, its top meets the row at the row's own height, over the ground or not.
        var route = new Route(goals, after.HasValue && plan.Reversed ? after.Value.Height : ground(to.x, to.y)) { StartAligned = arriving.HasValue, EndHeading = leaving, Corridor = corridor, MaxBuried = maxBuried, ArriveWithin = arriveWithin, Allowed = allowed, ExitAllowed = exitAllowed, SurfaceAllowed = surfaceAllowed, Kit = kit,
            StairFirst = stairFirst, Facing = facing, TopFacing = topFacing, TopRing = topRing, TopLandings = topLandings,
            RingStarts = footRing?.Starts(width), OnLanding = plan.Reversed ? null : onLanding };
        // Climbed the other way, the place is at the root: the walker comes down onto it, so the run from it climbs away.
        if (intoPlace.HasValue)
        {
            // Laid up from the place, its root is held from the start (StartAligned), which a search not held to face does
            // not do: the hold bears on it whatever it turns away (StairPlan.HeldToFace).
            if (plan.Reversed) { route.IntoPlaceFromRoot = arriving; plan.HeldToFace++; }
            else route.IntoPlaceAtTop = intoPlace.Value;
        }
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
            var leg = new Route(route.Goals.GetRange(g, route.Goals.Count - g), route.GoalHeight) { StartAligned = route.StartAligned, EndHeading = route.EndHeading, Corridor = route.Corridor, MaxBuried = route.MaxBuried, ArriveWithin = route.ArriveWithin, Allowed = route.Allowed, ExitAllowed = route.ExitAllowed, OnLanding = route.OnLanding, SurfaceAllowed = route.SurfaceAllowed, Kit = route.Kit, StairFirst = route.StairFirst, Facing = route.Facing, TopFacing = route.TopFacing,
                TopRing = route.TopRing, TopLandings = route.TopLandings, RingStarts = route.RingStarts, IntoPlaceFromRoot = route.IntoPlaceFromRoot, IntoPlaceAtTop = route.IntoPlaceAtTop };
            current.Goal = 0;
            bool last = g == route.Goals.Count - 1;
            if (totalExpansions <= 0) { plan.Reason = "search budget spent"; break; }
            Node? reached = Search(current, leg, last, ground, width, Math.Min(maxExpansions, totalExpansions), stallExpansions, out Node closest, out string reason, out int used);
            totalExpansions -= used;
            plan.Expansions += used;
            plan.HeldToFace += leg.HeldToFace;
            plan.FitOffRoot += leg.FitOffRoot;
            plan.BuriedOffRoot += leg.BuriedOffRoot;
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
        // A walk that ends on a floor already met there is finished: the floor's edge onto the road or the ground lands by
        // the one reader at its own far corners (StairEnds.Lands, the road's end on its ground), and no row is laid on past
        // it (a road's top onto its ring at -1871.4,6730.1 turned square onto a floor whose side toward the road was level
        // with it; the second 2x2 laid on was redundant).
        if (plan.Reached)
            for (int i = 0; i < topLandings && !FloorMet(best.Kind, best.Edge, best.Heading, best.Height, width, ground); i++)
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
        // The ring it was laid against, and where on it it set off (its first piece's back edge): the road's end is that point
        // less its offset from the ring's centre, wherever the foot is moved later.
        plan.Ring = footRing ?? topRing;
        if (footRing.HasValue)
        {
            Node? step = best;
            while (step?.Parent != null && step.Parent.Parent != null) step = step.Parent;
            if (step != null && step.Parent != null) plan.RingStart = step.Start;
        }
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
        bool Done(Node n)
        {
            if (!final) return n.Goal > 0 && Mathf.Abs(ground(n.Edge.x, n.Edge.y) - n.Height) <= LegEndHeight;
            if (EndsTurnedBack(n) || !Arrived(n, route) || (route.ExitAllowed != null && !route.ExitAllowed(n.Edge))
                || Mathf.Abs(ground(n.Edge.x, n.Edge.y) - n.Height) > ReachHeight
                || route.ArriveWithin.HasValue && !TopReachesGround(n.Edge, n.Heading, n.Height, width, ground)) return false;
            // A finish turned away only for how it arrives at the place (StairPlan.HeldToFace).
            if (ArrivesIntoThePlace(n, route)) return true;
            route.HeldToFace++;
            return false;
        }
        root.Score = Remaining(root, route);
        if (root.RestDue == 0) root.RestDue = RestDueAt(root.Edge);
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
            Expand(node, route, ground, width, children, final ? Done : null);
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

    /// <summary>Whether a walk ending on a piece of <paramref name="kind"/> whose exit edge is <paramref name="edge"/> (walked
    /// out on <paramref name="heading"/>, at <paramref name="height"/>) ends on a floor already met past it: the ground off its
    /// edge, the road's end on it included, within a step at its own far corners (StairEnds.Lands, the one reader). Its finish
    /// is complete there: no further floor row is laid on past it (the top landings of <see cref="Build"/>).</summary>
    internal static bool FloorMet(StairPieceKind kind, Vector2 edge, float heading, float height, float width, Func<float, float, float> ground) =>
        kind == StairPieceKind.Landing && StairEnds.Lands(edge, Forward(heading), height, width, ground);

    /// <summary>Near the goal, at its height, facing the way the road leaves. The
    /// search also asks that the staircase end level with the ground at its own
    /// end: within reach of the goal on a steep slope, one stopped 2.4 m under
    /// the ground there, and the road leaving it was dug 5 m into the hill.
    /// A top that is a road's end (<see cref="Route.TopRing"/>) arrives on its ring only: the landing a top may step onto
    /// first (<see cref="Route.OnLanding"/>) is a place's, and is not read there. Else near the goal, or stepping onto that
    /// landing.</summary>
    private static bool Arrived(Node node, Route route) =>
        node.Goal == route.Last
        && (route.TopRing is RoadEndRing ring
            ? OnTheRing(node, ring, route.TopLandings)
            : (Vector2.Distance(node.Edge, route.Final) <= (route.ArriveWithin ?? ReachRadius)
               || route.OnLanding != null && route.OnLanding(node.Edge + Forward(node.Heading) * ProudReach))) &&
        (route.ArriveWithin.HasValue && !route.TopRing.HasValue ? node.Height >= route.GoalHeight - ArriveBelow : Mathf.Abs(route.GoalHeight - node.Height) <= ReachHeight) &&
        (!route.EndHeading.HasValue || Mathf.Abs(Wrap(node.Heading - route.EndHeading.Value + 180f) - 180f) <= AlignSteps * HeadingStep + 0.01f) &&
        (!route.TopFacing.HasValue || Mathf.Abs(Wrap(node.Heading - route.TopFacing.Value + 180f) - 180f) <= FacingSteps * HeadingStep + 0.01f);

    /// <summary>
    /// Whether a top ending at <paramref name="node"/>, with its <paramref name="landings"/> floors carried on straight past it,
    /// meets a road's end on its ring (<see cref="RoadEndRing"/>): its exit edge on the circle (within RoadEndRing.Delta),
    /// facing in toward the road's end, off the cut arcs, not walked off across the road (RoadEndRing.WalksOffAcross), and
    /// that last row keeping out of the circle and the clear lane (its floors, level with the road, are where the road comes
    /// onto it). A flight going down from a road's end meets it as a foot does.
    /// </summary>
    private static bool OnTheRing(Node node, RoadEndRing ring, int landings)
    {
        Vector2 dir = Forward(node.Heading), exit = node.Edge + dir * (PieceRun * landings);
        Vector2 d = exit - ring.Centre;
        if (Mathf.Abs(d.magnitude - ring.Radius) > RoadEndRing.Delta || Vector2.Dot(dir, -d) <= 0f) return false;
        if (ring.ArcCut(RoadEndRing.Turn(Bearing(ring.Centre, exit), ring.Travel))) return false;
        if (ring.WalksOffAcross(exit, node.Heading)) return false;
        foreach (Vector2 p in RoadEndRing.Footprint(node.Start, node.Heading, PieceRun, PieceRun))
            if (ring.KeptOut(p)) return false;
        return true;
    }

    /// <summary>States closer than a piece apart, at the same height and heading,
    /// are one state: at half a metre a 82 m climb up a real 1:1 to 2:1 face
    /// drowned in near-copies and was not solved
    /// in 100000 steps; at 2 m it is, in 1.7 s offline.</summary>
    private const float StateCell = 2f;

    private static (int, int, int, int, int) Key(Node node) =>
        (Mathf.RoundToInt(node.Edge.x / StateCell), Mathf.RoundToInt(node.Edge.y / StateCell), Mathf.RoundToInt(node.Height),
         Mathf.RoundToInt(node.Heading / HeadingStep) % 16, node.Goal);

    /// <summary>A rest every <see cref="RestEveryMin"/> to <see cref="RestEveryMax"/> stairs (6 to 12 m of climb,
    /// mirroring the road's rests): a floor, or a turn's or switchback's landing. Each stair past the run's due count
    /// costs <see cref="RestPrice"/>, up to <see cref="RestOverdueMost"/> of them, so a floor soon costs less than climbing on; never a
    /// hard rule, so a face with no room for one keeps its staircase, paying the bounded price once (unbounded, a long face
    /// where every floor is dear ran the search out of budget).</summary>
    public const int RestEveryMin = 6, RestEveryMax = 12;
    private const float RestPrice = 1.5f;
    private const int RestOverdueMost = 11;

    /// <summary>The stairs a run setting off from <paramref name="at"/> is due to climb before its next rest: varied by the
    /// road rests' world-position noise (RoadPitches.SpacingNoise).</summary>
    internal static int RestDueAt(Vector2 at) => Mathf.RoundToInt(Mathf.Lerp(RestEveryMin, RestEveryMax, RoadPitches.SpacingNoise(at)));

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
        /// <summary>Set: where the last piece's exit edge may be, half a piece past its centre: a staircase onto a place's
        /// flat finishes within its lip (RoadStairRuns.PastTheLip), its top's exit edge as well as every centre.</summary>
        public Func<Vector2, bool>? ExitAllowed;
        /// <summary>Set: the place's landing a top arrives on (<see cref="Build"/>'s onLanding): a top whose step off it
        /// (<see cref="ProudReach"/> past its edge) is onto the landing has arrived wherever it is, nearer than its aim or not.
        /// Aimed where the landing meets the slope, an approach that met the landing's lip sooner went on along it past its
        /// level ground, turned onto a floor out over the drop beyond and stepped back down off it (in game: a cave's approach
        /// at 1600.2,6920.2, its top stair level with the landing; one floor laid by hand on from it, open onto it, in place of
        /// the turn, the floor and the stair down).</summary>
        public Func<Vector2, bool>? OnLanding;
        /// <summary>The caller's walked-surface validity, shared with its finished-plan check.</summary>
        public Func<Vector3, bool>? SurfaceAllowed;

        public bool SurfaceFits(bool stair, Vector2 start, float heading, float height, float width, float depth = PieceRun)
        {
            if (SurfaceAllowed == null) return true;
            foreach (Vector3 sample in Surface(stair, start, heading, height, width, depth))
                if (!SurfaceAllowed(sample)) return false;
            return true;
        }

        /// <summary>A candidate piece's walking surface as it will be laid, set off from <paramref name="start"/> on
        /// <paramref name="heading"/> at the walking height <paramref name="height"/> (searched down, a stair is the stair up
        /// from its far end).</summary>
        private IEnumerable<Vector3> Surface(bool stair, Vector2 start, float heading, float height, float width, float depth = PieceRun)
        {
            if (Down && stair) { start += Forward(heading) * PieceRun; heading = Wrap(heading + 180f); height -= PieceRise; }
            return WalkingSurface(stair, start, heading, height, width, depth);
        }

        /// <summary>Kept off earthwork (an approach: no cuts in a place's footprint, laid with less than
        /// StairCover.RoadStaircase): a piece's burial is read over its whole walking surface, its edges included
        /// (<see cref="Cover"/>), as the approach's refusal and the tests read it. A road's staircase cuts its rows out and
        /// keeps its inside reading (<see cref="Fit"/>).</summary>
        public bool KeptOffEarthwork => MaxBuried < StairCover.RoadStaircase;

        /// <summary>How deep the ground stands over a candidate piece's whole walking surface (<see cref="Surface"/>, read
        /// with <see cref="DeepestOver"/>: nothing allocated per candidate).</summary>
        public float Buried(bool stair, Vector2 start, float heading, float height, float width, Func<float, float, float> ground)
        {
            if (Down && stair) { start += Forward(heading) * PieceRun; heading = Wrap(heading + 180f); height -= PieceRise; }
            return DeepestOver(stair, start, heading, height, width, PieceRun, ground, MaxBuried);
        }

        /// <summary>How deep the ground stands over the fillers of the wedge a turn opens, as the kit lays them (<see cref="StairLayout.WedgeBuried"/>).</summary>
        public float WedgeBuried(Vector2 pivot, float from, float height, int turn, float width, bool offFloor, Func<float, float, float> ground) =>
            StairLayout.WedgeBuried(pivot, from, height, turn, width, offFloor, Kit.Fillers, ground, MaxBuried);

        public bool WedgeFits(Vector2 pivot, float from, float height, int turn, float width, bool offFloor)
        {
            if (SurfaceAllowed == null) return true;
            foreach (FillerPiece around in StairFiller.Around(from, turn, width, offFloor, Kit.Fillers))
            {
                FillerPiece filler = StairFiller.At(around, pivot);
                if (!SurfaceFits(false, filler.Back, filler.Heading, height, filler.Across, filler.Depth)) return false;
            }
            return true;
        }

        /// <summary>Whether something within reach holds up every filler of the wedge, as the kit will lay them (the fillers
        /// <see cref="AddWedge"/> lays: StairFiller.Choose's, read round the origin and moved to the pivot, allocating nothing,
        /// as the search asks it of every turn it tries): unchecked, a turn's fillers stood out over a drop beside the slope
        /// its stairs were held from, and the kit laid them hanging.</summary>
        public bool WedgeHeld(Vector2 pivot, float from, float height, int turn, float width, bool offFloor, Func<float, float, float> ground)
        {
            foreach (FillerPiece around in StairFiller.Around(from, turn, width, offFloor, Kit.Fillers))
            {
                FillerPiece filler = StairFiller.At(around, pivot);
                if (!Held(Kit, filler.Prefab, filler.Back, filler.Heading, height, filler.Across, filler.Depth, ground)) return false;
            }
            return true;
        }
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
        /// <summary>Set: the staircase's root is a place's landing (laid up from it to a road above), and this the heading it
        /// climbs away on; every piece from the root through its first <see cref="ArrivalStairs"/> stairs heads within <see cref="ArrivalSteps"/> of it
        /// (<see cref="ArrivalHeld"/>).</summary>
        public float? IntoPlaceFromRoot;
        /// <summary>Set: the staircase's top is a place's landing, and this the point a walker coming onto it faces; the top
        /// and every piece back through its arriving stairs head within <see cref="AlignSteps"/> of it as seen from the top
        /// (<see cref="ArrivalHeld"/>).</summary>
        public Vector2? IntoPlaceAtTop;
        /// <summary>What the hold on how it arrives at a place's landing turned away in this leg's search
        /// (<see cref="StairPlan.HeldToFace"/>).</summary>
        public int HeldToFace;
        /// <summary>First pieces off the root this leg's search kept, and turned away buried (<see cref="StairPlan.FitOffRoot"/>).</summary>
        public int FitOffRoot, BuriedOffRoot;
        /// <summary>Searched down from its top (<see cref="BuildDown"/>): every stair a rise lower, the first piece on the
        /// root's own heading.</summary>
        public bool Down;
        /// <summary>Set: the staircase sets off from a road's end on its ring (<see cref="RoadEndRing.Starts"/>): its first piece
        /// is one of these, priced by them, not turned off the root.</summary>
        public List<RoadEndRing.Start>? RingStarts;
        /// <summary>Set: the staircase's top meets a road's end on its ring (<see cref="OnTheRing"/>), with
        /// <see cref="TopLandings"/> floors carried on past its last row.</summary>
        public RoadEndRing? TopRing;
        public int TopLandings;

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
    private static float Remaining(Node node, Route route) => Remaining(node.Edge, node.Goal, node.Height, route) + RestsLeft(node, route);

    /// <summary>The least the rests still owed cost: a floor walked for each <see cref="RestEveryMax"/> stairs left to climb,
    /// or each stair past the due count where none is laid, whichever is less. Left out, every path paid for its rests
    /// as it went and none in prospect, and the search widened until its budget ran out on a long face.</summary>
    private static float RestsLeft(Node node, Route route)
    {
        float rise = Mathf.Abs(route.GoalHeight - node.Height);
        int stairs = Mathf.RoundToInt(rise / PieceRise), climbed = node.SinceRest + stairs;
        float floors = climbed / RestEveryMax * (PieceRun + LandingWeight);
        float overdue = Math.Max(0, Math.Min(climbed, node.RestDue + RestOverdueMost) - Math.Max(node.RestDue, node.SinceRest)) * RestPrice;
        return Mathf.Min(floors, overdue);
    }

    private static float Remaining(Vector2 edge, int goal, float height, Route route)
    {
        float distance = Vector2.Distance(edge, route.Goals[goal]) + route.Tail[goal];
        float rise = route.Down ? height - route.GoalHeight : route.GoalHeight - height;
        float climb = rise > 0f ? rise * PieceRun / PieceRise : -rise * PieceRun / PieceRise * 2f;
        return Mathf.Max(distance, climb);
    }

    private static readonly StairPieceKind[] Kinds = { StairPieceKind.Stair, StairPieceKind.Landing };

    /// <param name="finishes">Whether a piece finishes the staircase (its last leg's goal reached), if this leg is its last:
    /// only there may a flat run turn off the stair below it.</param>
    private static void Expand(Node node, Route route, Func<float, float, float> ground, float width, List<Node> next, Func<Node, bool>? finishes = null)
    {
        float half = width / 2f;
        Vector2 right = Right(node.Heading);

        foreach (StairPieceKind kind in Kinds)
        {
            // The first piece may face any of the hammer's sixteen headings: on
            // ground steeper than a flight it has to start across the slope.
            bool first = node.Parent == null;
            if (first && kind == StairPieceKind.Landing && route.StairFirst) continue;
            // A switchback's two legs are never both flat (StairChain.BackTracks): a pad come onto off a floor is left on a
            // stair.
            if (kind == StairPieceKind.Landing && node.Switchback != 0 && node.Parent!.Kind == StairPieceKind.Landing) continue;
            // Leaving a switchback the flight may bend one step off the
            // reverse heading, like any stair turn (a hand-built second switchback
            // turns 157.5 degrees). Three steps left a 67.5 degree wedge that
            // no filler covered, so the junction did not connect up well.
            bool floorOnFloor = kind == StairPieceKind.Landing && node.Kind == StairPieceKind.Landing && node.Switchback == 0 && !first;
            // A flat run turned off the stair below it (a join, as a stair off a flat run): only as the staircase's last row,
            // where it meets a road or another flight, or off a flight already laid that this one carries on from. Elsewhere it
            // lines up with the stair below it and turns floor over floor: turned anywhere, the search's floors multiplied
            // and its budgets ran out.
            bool joinOff = kind == StairPieceKind.Landing && node.Kind == StairPieceKind.Stair && node.Switchback == 0 && !first;
            // Coming up out of the road on a stair (StairFirst), it may set off a step further off the road's heading than
            // a floor would: a foot at Hildir's cave left the road 55 degrees off its heading and turned on the stairs.
            // Searched down (BuildDown), the flight below a switchback leaves it straight back: turned round into the climb, a
            // bend there falls on the side the climb arrives from, where the pad is laid square to the flight arriving, and the
            // pad stood 22.5 degrees off, its floors 0.4 m off the stair below and 0.7 m off each other.
            // Off a place's landing (IntoPlaceFromRoot), as far as its arrival is held (ArrivalSteps).
            int maxTurn = first ? (route.IntoPlaceFromRoot.HasValue ? ArrivalSteps
                    : route.StartAligned ? AlignSteps + (route.StairFirst && kind == StairPieceKind.Stair ? 1 : 0) : 8)
                : node.Switchback != 0 ? (route.Down ? 0 : MaxSwitchbackBendSteps)
                : kind == StairPieceKind.Landing ? (floorOnFloor ? MaxFloorTurnSteps : joinOff && (finishes != null || node.Placed) ? MaxJoinTurnSteps : 0)
                : node.Kind == StairPieceKind.Landing ? MaxJoinTurnSteps
                : MaxStairTurnSteps;
            if (route.Down && node.Placed) maxTurn = 0; // straight on from the top it carries down from: back to back
            // From a road's end, the first piece sets off from its ring (RoadEndRing): any start on the remaining arc, held to the
            // road's heading as a first piece off the root is (StartAligned), priced by the ring instead of FootOffRoadPrice.
            List<RoadEndRing.Start>? ring = first ? route.RingStarts : null;
            int lowest = first && !route.StartAligned && !route.Down ? -7 : -maxTurn;
            int choices = ring?.Count ?? maxTurn - lowest + 1;
            for (int c = 0; c < choices; c++)
            {
                int turn;
                float heading, startPrice;
                Vector2 start, pivot;
                if (ring != null)
                {
                    RoadEndRing.Start from = ring[c];
                    heading = from.Heading;
                    turn = Mathf.RoundToInt((Wrap(heading - node.Heading + 180f) - 180f) / HeadingStep);
                    if (turn < lowest || turn > maxTurn) continue;
                    (start, pivot, startPrice) = (from.At, from.At, from.Price);
                }
                else
                {
                    turn = lowest + c;
                    heading = Wrap(node.Heading + turn * HeadingStep);
                    (start, pivot) = first ? (node.Edge, node.Edge) : TurnOff(node.Edge, node.Heading, heading, turn, half, floorOnFloor);
                    startPrice = first ? FootOffRoadPrice(heading, route) : 0f;
                }
                // A place's arrival held from the root (IntoPlaceFromRoot): its first stairs climb away from the place.
                if (route.IntoPlaceFromRoot.HasValue && node.Climbed < ArrivalStairs && !ArrivalHeld(heading, route.IntoPlaceFromRoot.Value)) { route.HeldToFace++; continue; }
                // The sharpest small turn, 67.5 degrees stair to stair, only off a stair that came straight on, never one after
                // another round a curl, and not at the foot, whose setting off from its road the foot's own rules decide.
                if (!first && kind == StairPieceKind.Stair && node.Kind == StairPieceKind.Stair && Math.Abs(turn) > 2
                    && (node.TurnSteps != 0 || node.Parent?.Kind != StairPieceKind.Stair || node.Depth < SharpTurnFromDepth)) continue;
                var setOff = BackRunAfter(node, heading, kind, route);
                if (setOff == null) continue;
                Vector2 dir = Forward(heading);

                Vector2 edge = start + dir * PieceRun;
                // Turned off the stair below, it must be the last row: within reach of the goal (Done says the rest below).
                bool lastRowOnly = joinOff && turn != 0 && !node.Placed;
                if (lastRowOnly && Vector2.Distance(edge, route.Final) > (route.ArriveWithin ?? ReachRadius)) continue;
                Vector2 middle = start + dir * (PieceRun / 2f);
                if (!route.InCorridor(middle)) continue;

                float rise = kind == StairPieceKind.Stair ? (route.Down ? -PieceRise : PieceRise) : 0f;
                Fit(start, dir, Right(heading), half, node.Height, rise, ground, out float floatGap, out float buried, out float highest);
                if (route.KeptOffEarthwork) buried = route.Buried(kind == StairPieceKind.Stair, start, heading, node.Height, width, ground);
                // A flat run on columns stands no higher over the ground than the walkway cap: above it the staircase climbs or goes round.
                if (kind == StairPieceKind.Landing && highest > StairKit.WalkwayCap) continue;
                // The wedge a turn opens is filled level with the piece before (StairFiller): it is walked
                // too, and buried it needs earthwork as a piece does. Where a staircase is kept off earthwork
                // (an approach, laid with less than StairCover.RoadStaircase: no cuts in a place's footprint), its fillers are
                // read as a piece is, over their whole surface (StairLayout.WedgeBuried): unread, a turn on Hildir's cave's face left a filler 0.6 m
                // under the slope inside the cave's footprint, and the approach was refused. A road's staircase
                // cuts its fillers out, as before: read there, the 82 m climb up the real face found no way up.
                if (turn != 0 && !first && !floorOnFloor && route.KeptOffEarthwork && buried <= route.MaxBuried)
                    buried = Mathf.Max(buried, route.WedgeBuried(pivot, node.Heading, node.Height, turn, width, node.Kind == StairPieceKind.Landing, ground));
                if (buried > route.MaxBuried) { if (first) route.BuriedOffRoot++; continue; } // the stair would disappear into the slope
                if (!route.SurfaceFits(kind == StairPieceKind.Stair, start, heading, node.Height, width)) continue;
                if (turn != 0 && !first && !floorOnFloor && !route.WedgeFits(pivot, node.Heading, node.Height, turn, width, node.Kind == StairPieceKind.Landing)) continue;
                // What holds it up, as the kit will lay it at first build (StairSupport, one owner): a wall, an arch or
                // corbel off the slope, or cornices; refused where nothing within reach holds it, and that way avoided.
                // So is a turn whose wedge's fillers nothing holds: the kit lays them as it lays the piece.
                StairSupport.Choice support = StairSupport.Choose(route.Kit, SupportWalk(start, heading, node.Height, rise, width, kind, route.Kit), ground);
                if (support.Refused) { System.Threading.Interlocked.Increment(ref s_supportRefusals); continue; }
                // The cheap test before the wedge's (a choice for each of its fillers).
                if (CrossesItself(node, middle, heading, width)) continue;
                if (turn != 0 && !first && !floorOnFloor && !route.WedgeHeld(pivot, node.Heading, node.Height, turn, width, node.Kind == StairPieceKind.Landing, ground))
                {
                    System.Threading.Interlocked.Increment(ref s_supportRefusals);
                    continue;
                }
                // At its foot (the first EdgeStairs stairs), no stair stands on a ledge with its side out past the ledge's
                // sharp edge: the flight turns to set off from the landing instead. A steady slope crossed, and everywhere
                // up the flight and at its top, stairs hug the ground as before (at the mountain cave at
                // -2556.7,1554.2 the stairs should not stick out the edge of the cliff, so turn it to start on
                // the landing; only at the bottom step of stairs, the top steps work fine already).
                float offEdge = kind == StairPieceKind.Stair && support.Style is not (StairSupport.Style.Arch or StairSupport.Style.Cornice)
                    && node.Depth < EdgeStairs && !route.Down
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
                    BackRun = setOff.Value.backRun,
                    FacedStairs = setOff.Value.faced,
                    Climbed = Math.Min(ArrivalStairs, node.Climbed + (kind == StairPieceKind.Stair ? 1 : 0)),
                };
                float sharp = !first && kind == StairPieceKind.Stair && node.Kind == StairPieceKind.Stair && Math.Abs(turn) > 2 ? SharpTurnPrice : 0f;
                child.SinceRest = kind == StairPieceKind.Stair ? node.SinceRest + 1 : 0;
                child.RestDue = kind == StairPieceKind.Stair ? node.RestDue : RestDueAt(edge);
                float overdue = child.SinceRest > child.RestDue && child.SinceRest <= child.RestDue + RestOverdueMost ? RestPrice : 0f;
                child.Cost = node.Cost + PieceRun + offEdge + support.Price + sharp + overdue + BuriedWeight * buried
                    + TurnWeight * Mathf.Abs(first ? 0 : turn) + startPrice + TopOffRoadPrice(edge, heading, node.Goal, route)
                    + (kind == StairPieceKind.Landing ? LandingWeight : 0f);
                if (offEdge > 0f) t_ledgePrices++;
                child.Goal = route.Advance(node.Goal, child.Edge);
                if (lastRowOnly && !finishes!(child)) continue;
                child.Score = child.Cost + HeuristicWeight * Remaining(child, route);
                if (first) route.FitOffRoot++;
                next.Add(child);
            }
        }

        if (node.Parent == null || SwitchbackSinceLastStair(node)) return;
        // Held from a place's landing, no switchback in its arriving flight: its floors turn back on the place.
        if (route.IntoPlaceFromRoot.HasValue && node.Climbed < ArrivalStairs) { route.HeldToFace++; return; }
        Vector2 forward = Forward(node.Heading);
        for (int side = -1; side <= 1; side += 2)
        {
            // Floor A continues the lane; floor B lies beside it; the next flight leaves B heading back.
            Vector2 returnStart = node.Edge + right * (side * width);
            Vector2 centre = node.Edge + forward * (PieceRun / 2f) + right * (side * width / 2f);
            if (CrossesItself(node, node.Edge + forward * (PieceRun / 2f), node.Heading, width)
                || CrossesItself(node, centre + right * (side * width / 2f), node.Heading, width)
                || !route.InCorridor(centre + right * (side * width / 2f))) continue;
            Fit(node.Edge, forward, right, half, node.Height, 0f, ground, out float floatA, out float buriedA, out _);
            Fit(returnStart, forward, right, half, node.Height, 0f, ground, out float floatB, out float buriedB, out _);
            float buried = route.KeptOffEarthwork
                ? Mathf.Max(route.Buried(false, node.Edge, node.Heading, node.Height, width, ground), route.Buried(false, returnStart, node.Heading, node.Height, width, ground))
                : Mathf.Max(buriedA, buriedB);
            if (buried > route.MaxBuried) continue;
            if (!route.SurfaceFits(false, node.Edge, node.Heading, node.Height, width)
                || !route.SurfaceFits(false, returnStart, node.Heading, node.Height, width)) continue;
            // Both floors held up as the kit will lay them, as every piece the search tries is (StairSupport).
            if (StairSupport.Choose(route.Kit, SupportWalk(node.Edge, node.Heading, node.Height, 0f, width, StairPieceKind.Landing, route.Kit), ground).Refused
                || StairSupport.Choose(route.Kit, SupportWalk(returnStart, node.Heading, node.Height, 0f, width, StairPieceKind.Landing, route.Kit), ground).Refused)
            {
                System.Threading.Interlocked.Increment(ref s_supportRefusals);
                continue;
            }
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
                Climbed = node.Climbed,
                BackRun = setOff.Value.backRun,
                FacedStairs = setOff.Value.faced,
                Centre = centre,
                Depth = node.Depth + 1,
                Float = floatGap,
                Buried = buried,
            };
            child.RestDue = RestDueAt(returnStart);
            child.Cost = node.Cost + PieceRun + width
                + FloatWeight * floatGap + BuriedWeight * buried
                + SwitchbackWeight + SwitchbackSteepPrice(Steepness(centre, ground));
            child.Goal = route.Advance(node.Goal, child.Edge);
            child.Score = child.Cost + HeuristicWeight * Remaining(child, route);
            next.Add(child);
        }
    }

    /// <summary>A candidate piece's footprint for <see cref="StairSupport.Choose"/>: set off from <paramref name="start"/> at the
    /// walking height <paramref name="height"/>, climbing <paramref name="rise"/> (a stair down is the stair up from its far end),
    /// as <see cref="AddPiece"/> lays it (<see cref="PieceWalk"/>, the one builder the search and the finishes share).</summary>
    private static StairSupport.Walk SupportWalk(Vector2 start, float heading, float height, float rise, float width, StairPieceKind kind, StairKit kit)
    {
        if (kind != StairPieceKind.Stair) return PieceWalk(kit, kit.Floor, start, heading, height, width, PieceRun);
        return rise >= 0f
            ? PieceWalk(kit, kit.Stair, start, heading, height, width, PieceRun)
            : PieceWalk(kit, kit.Stair, start + Forward(heading) * PieceRun, Wrap(heading + 180f), height + rise, width, PieceRun);
    }

    /// <summary>Pieces the search left because nothing within reach holds them up (StairSupport), theirs or a turn's fillers
    /// or a switchback's floors: the routes avoided for it.</summary>
    private static int s_supportRefusals;
    internal static int SupportRefusals => s_supportRefusals;
    internal static void ResetSupportRefusals() => s_supportRefusals = 0;

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

    /// <summary>Tests: a flight of stairs laid out on the given headings from a foot, emitted as a search would emit it;
    /// <paramref name="kinds"/>, if given, the kind of each piece (a floor is a run that does not climb).</summary>
    internal static StairPlan LayFlight(Vector2 foot, float footHeight, IReadOnlyList<float> headings, StairKit kit, Func<float, float, float> ground,
        IReadOnlyList<StairPieceKind>? kinds = null)
    {
        var plan = new StairPlan { Kit = kit.Name, Reached = true };
        Node node = new() { Edge = foot, Start = foot, Heading = headings[0], Height = footHeight };
        for (int i = 0; i < headings.Count; i++)
        {
            float h = headings[i];
            StairPieceKind kind = kinds?[i] ?? StairPieceKind.Stair;
            bool first = node.Parent == null;
            bool floorOnFloor = !first && kind == StairPieceKind.Landing && node.Kind == StairPieceKind.Landing;
            int turn = first ? 0 : Mathf.RoundToInt((Wrap(h - node.Heading + 180f) - 180f) / HeadingStep);
            (Vector2 start, Vector2 pivot) = first ? (node.Edge, node.Edge) : TurnOff(node.Edge, node.Heading, h, turn, PieceRun / 2f, floorOnFloor);
            bool stair = kind == StairPieceKind.Stair;
            node = new Node
            {
                Parent = node, Kind = kind, Start = start, Edge = start + Forward(h) * PieceRun, Heading = h,
                Height = node.Height + (stair ? PieceRise : 0f), TurnSteps = turn, Overlaps = floorOnFloor && turn != 0, Pivot = pivot,
                Centre = start + Forward(h) * (PieceRun / 2f), Depth = node.Depth + 1,
            };
        }
        Emit(plan, node, ground, PieceRun, kit);
        return plan;
    }

    /// <summary>How far the walking surface of a piece stands above the ground
    /// (mean over its samples, and <paramref name="highest"/> the most) and how deep the ground buries it (worst sample).</summary>
    private static void Fit(Vector2 start, Vector2 dir, Vector2 right, float half, float baseHeight, float rise,
        Func<float, float, float> ground, out float floatGap, out float buried, out float highest)
    {
        floatGap = 0f;
        buried = 0f;
        highest = 0f;
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
                highest = Mathf.Max(highest, surface - g);
                buried = Mathf.Max(buried, g - surface);
                n++;
            }
        }
        floatGap /= n;
    }

    /// <summary>Whether a piece of <paramref name="width"/> across and a piece's run along, centred at <paramref name="centre"/>
    /// facing <paramref name="heading"/>, overlaps any piece laid before <paramref name="node"/>, seen from above: it may pass
    /// over nothing, as what holds it up (StairSupport) stands under it.</summary>
    private static bool CrossesItself(Node node, Vector2 centre, float heading, float width)
    {
        for (Node? earlier = node.Parent; earlier != null && earlier.Parent != null; earlier = earlier.Parent)
        {
            if (Vector2.Distance(earlier.Centre, centre) > 2f * (width + PieceRun)) continue;
            if (earlier.Switchback != 0)
            {
                // Its two floors, at the heading of the flight that arrived on them.
                float h = earlier.Parent.Heading;
                Vector2 a = earlier.Start + Forward(h) * (PieceRun / 2f);
                Vector2 b = a + Right(h) * (earlier.Switchback * width);
                if (Overlap(centre, heading, width, a, h, width) || Overlap(centre, heading, width, b, h, width)) return true;
                continue;
            }
            if (Overlap(centre, heading, width, earlier.Centre, earlier.Heading, width)) return true;
        }
        return false;
    }

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

    /// <summary>Two rectangles overlap seen from above: each its centre, yaw (local x is <see cref="Right"/>, local z
    /// <see cref="Forward"/>) and size along its local x and z.</summary>
    internal static bool Footprints(Vector2 ca, float yawA, float xA, float zA, Vector2 cb, float yawB, float xB, float zB)
    {
        Vector2 ra = Right(yawA), fa = Forward(yawA), rb = Right(yawB), fb = Forward(yawB);
        foreach (Vector2 axis in new[] { ra, fa, rb, fb })
        {
            float apart = Mathf.Abs(Vector2.Dot(cb - ca, axis));
            float reach = Mathf.Abs(Vector2.Dot(ra, axis)) * xA / 2f + Mathf.Abs(Vector2.Dot(fa, axis)) * zA / 2f
                + Mathf.Abs(Vector2.Dot(rb, axis)) * xB / 2f + Mathf.Abs(Vector2.Dot(fb, axis)) * zB / 2f;
            if (apart >= reach - OverlapTolerance) return false;
        }
        return true;
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

        int lanes = Lanes(width);
        int sunkPieces = 0;
        // One more stair below the first, a whole step down into the ground, so the
        // flight comes up out of the road rather than starting on top of it with a
        // lip (one extra stair clipped down into the earth makes it connect better). Buried on purpose: no earthwork uncovers it.
        if (sunkFoot && steps.Count > 0 && steps[0].Kind == StairPieceKind.Stair)
        {
            Node lowest = steps[0];
            SinkStep(plan, kit, lowest.Start, lowest.Heading, lowest.Parent!.Height, lanes, ground);
            sunkPieces = plan.SunkPieces;
        }
        int flight = 0;
        foreach (Node step in steps)
        {
            Node parent = step.Parent!;
            plan.MaxBuried = Mathf.Max(plan.MaxBuried, step.Buried);
            flight = step.Kind == StairPieceKind.Stair && step.Switchback == 0 ? flight + 1 : 0;
            plan.LongestFlight = Math.Max(plan.LongestFlight, flight);
            if (step.Switchback != 0)
            {
                int pad = plan.Pieces.Count;
                AddRow(plan, kit, StairPieceKind.Landing, kit.Floor, step.Start, parent.Heading, parent.Height, lanes, ground, mark: true);
                plan.Pieces[pad].Turn = (0, false);
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
                // The wedge, filled with flat pieces level with the top of the piece before.
                AddWedge(plan, kit, step.Pivot, parent.Heading, parent.Height, step.TurnSteps, width, parent.Kind == StairPieceKind.Landing, ground);
            }
            plan.LastRowPieces = plan.Pieces.Count; plan.LastRowClearances = plan.Clearances.Count;
            plan.LastRowKind = step.Kind; plan.LastRowStart = step.Start; plan.LastRowHeading = step.Heading; plan.LastRowHeight = parent.Height;
            int row = plan.Pieces.Count;
            AddRow(plan, kit, step.Kind, step.Kind == StairPieceKind.Stair ? kit.Stair : kit.Floor, step.Start, step.Heading, parent.Height, lanes, ground, mark: true);
            if (step.TurnSteps != 0 && step.Overlaps) plan.Pieces[row].Turn = (System.Math.Abs(step.TurnSteps), true);
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
        GuardEnds(plan, kit, steps.Count > 0 ? steps[0].Start : first.Edge, last.Edge, ground, sunkPieces);
    }

    /// <summary>The wedge a piece turned <paramref name="turn"/> heading steps off one walking <paramref name="from"/> opens on
    /// its outside, pivoting on <paramref name="pivot"/>: filled with flat pieces level with the piece before
    /// (<see cref="StairFiller"/>).</summary>
    private static void AddWedge(StairPlan plan, StairKit kit, Vector2 pivot, float from, float height, int turn, float width, bool offFloor,
        Func<float, float, float> ground)
    {
        int wedge = plan.Pieces.Count;
        foreach (FillerPiece filler in StairFiller.Choose(pivot, from, turn, width, offFloor, kit.Fillers))
            AddPiece(plan, kit, StairPieceKind.Filler, filler.Prefab, filler.Back, filler.Heading, height, filler.Across, filler.Depth, ground, mark: false);
        if (plan.Pieces.Count > wedge) plan.Pieces[wedge].Turn = (System.Math.Abs(turn), false);
        plan.Turns++;
        plan.TurnSteps += System.Math.Abs(turn);
    }

    /// <summary>Highest a walking surface may stand over the ground a player walks onto it from:
    /// the player has no step-up, so a stair's top over a clearing is climbed only by a jump. 0.2 m (0.3 m before: a top
    /// floor 0.26 m over a stone tower's landing at 186.31 was within it, and wanted flatter). The one owner of step and stair geometry (docs/OWNERS.md).</summary>
    public const float StepUp = 0.2f;

    /// <summary>Whether a player walks between walking surfaces at <paramref name="a"/> and <paramref name="b"/>: no more than a
    /// step (<see cref="StepUp"/>) apart, either way (a thousandth over it for float rounding). The one rule for every height a
    /// player walks between a road (or a shared landing) and a staircase, floor, structure end or walkable block, and between a
    /// staircase's walked pieces and the ground they step off onto. NaN is never walkable. The one owner of a walker's step
    /// (docs/OWNERS.md).</summary>
    public static bool Walkable(float a, float b) => Mathf.Abs(a - b) <= StepUp + 1e-3f;

    /// <summary>
    /// Whether something within reach holds up every piece of a row <see cref="AddRow"/> would lay (StairSupport.Choose, as
    /// the kit lays it at first build). The search refuses a piece nothing holds; what is added after it (a top's floor,
    /// crest or descent, stairs on up or down from an end, floors carried on along a crest) is not added where this
    /// fails, so the kit never meets a piece it cannot hold.
    /// </summary>
    internal static bool RowHeld(StairKit kit, string prefab, Vector2 start, float heading, float baseHeight, int lanes, Func<float, float, float> ground)
    {
        Vector2 right = Right(heading);
        for (int lane = 0; lane < lanes; lane++)
            if (!Held(kit, prefab, start + right * ((lane - (lanes - 1) / 2f) * PieceRun), heading, baseHeight, PieceRun, PieceRun, ground)) return false;
        return true;
    }

    /// <summary>Whether something within reach holds up one piece <see cref="AddPiece"/> would lay.</summary>
    private static bool Held(StairKit kit, string prefab, Vector2 back, float heading, float baseHeight, float across, float depth,
        Func<float, float, float> ground) =>
        !StairSupport.Choose(kit, PieceWalk(kit, prefab, back, heading, baseHeight, across, depth), ground).Refused;

    /// <summary>A piece's footprint as <see cref="AddPiece"/> lays it, its back edge centred on <paramref name="back"/>: a
    /// stair's underside at its base, a floor's or filler's its thickness under its walking surface.</summary>
    private static StairSupport.Walk PieceWalk(StairKit kit, string prefab, Vector2 back, float heading, float baseHeight, float across, float depth)
    {
        bool stair = prefab == kit.Stair;
        return new StairSupport.Walk(back, heading, depth, across, stair ? baseHeight : baseHeight - kit.FloorThickness, stair, stair ? 0f : kit.FloorThickness);
    }

    /// <summary>One row of pieces across the stair.</summary>
    private static void AddRow(StairPlan plan, StairKit kit, StairPieceKind kind, string prefab, Vector2 start, float heading,
        float baseHeight, int lanes, Func<float, float, float> ground, bool mark, bool cut = true)
    {
        Vector2 right = Right(heading);
        for (int lane = 0; lane < lanes; lane++)
            AddPiece(plan, kit, kind, prefab, start + right * ((lane - (lanes - 1) / 2f) * PieceRun), heading, baseHeight, PieceRun, PieceRun, ground, mark, cut);
    }

    /// <summary>One piece whose back edge is centred on <paramref name="back"/>, its clearance and cuts.</summary>
    private static void AddPiece(StairPlan plan, StairKit kit, StairPieceKind kind, string prefab, Vector2 back, float heading,
        float baseHeight, float across, float depth, Func<float, float, float> ground, bool mark, bool cut = true)
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
        AddClearance(plan, stair, back, dir, heading, baseHeight, across, depth);
        if (cut) AddCuts(plan, stair, back, dir, right, baseHeight, across, depth, ground);

        // How far it stands over the ground, held out from a slope aside: what holds it up is the kit's, laid at first build
        // (StairSupport).
        if (mark && StairSupport.Choose(kit, PieceWalk(kit, prefab, back, heading, baseHeight, across, depth), ground).Style
                is not (StairSupport.Style.Arch or StairSupport.Style.Cornice))
            plan.MaxFloat = Mathf.Max(plan.MaxFloat, underside - ground(centre.x, centre.y));
    }

    /// <summary>The walking box over the piece, in stretches along the walk (the one owner of what a staircase clears of rock): its
    /// walking surface's width across, from <see cref="RockClearances.Bottom"/> of the stretch's lowest walk, as a road's
    /// clearance starts over its surface, up to <see cref="RoadRockPolicy.ClearanceHeight"/> over its highest walk, so the whole headroom over
    /// every tread is in it (over the stretch's middle it fell a quarter of a stretch's rise short at its high end). A stair
    /// rises <see cref="PieceRise"/> / <see cref="ClearanceStretches"/> per stretch, a floor or block is level. Nothing under the walk: the piece's own body, the supports under it
    /// and its handrails may stand in rock (they were cleared from the underside up, so a rock a support stood in was fractured
    /// for it).</summary>
    private static void AddClearance(StairPlan plan, bool stair, Vector2 laneStart, Vector2 dir, float heading, float baseHeight, float across, float depth)
    {
        int stretches = stair ? ClearanceStretches : 1;
        float length = depth / stretches;
        for (int i = 0; i < stretches; i++)
        {
            // A stair's walking surface at the middle of this stretch, at its low end and at its high end; a floor's or block's
            // is its top.
            float surface = stair ? baseHeight + PieceRise * (i + 0.5f) / stretches : baseHeight;
            float lowest = stair ? baseHeight + PieceRise * i / stretches : baseHeight;
            float highest = stair ? baseHeight + PieceRise * (i + 1) / stretches : baseHeight;
            float bottom = RockClearances.Bottom(lowest);
            float top = BoxTop(highest);
            Vector2 mid = laneStart + dir * (length * (i + 0.5f));
            plan.Clearances.Add(new StairClearance(
                new Vector3(mid.x, (bottom + top) / 2f, mid.y),
                new Vector3(across / 2f, (top - bottom) / 2f, length / 2f),
                heading, surface));
        }
    }

    /// <summary>The top of a stair's walking box over its stretch's highest walk <paramref name="highest"/>: a road's
    /// (<see cref="RockClearances.Top"/>), a player's headroom (<see cref="RoadRockPolicy.ClearanceHeight"/>) over the lift
    /// (<see cref="RoadRockPolicy.ClearanceLift"/>) over the walk. The one owner of headroom (docs/OWNERS.md).</summary>
    internal static float BoxTop(float highest) => RockClearances.Top(highest);

    /// <summary>A stored walking box's highest walk (centre <paramref name="centre"/>, half extents <paramref name="half"/>, middle
    /// walk <paramref name="surface"/>): its middle walk stands under its highest as far as its lowest (the box's bottom less
    /// the lift) stands under it. Read from the bottom and surface, stored the same way by every build, so a box saved before
    /// the headroom became the road's is read as it was laid. The one owner of headroom (docs/OWNERS.md).</summary>
    internal static float HighestWalk(Vector3 centre, Vector3 half, float surface) =>
        2f * surface - (centre.y - half.y - RoadRockPolicy.ClearanceLift);

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
