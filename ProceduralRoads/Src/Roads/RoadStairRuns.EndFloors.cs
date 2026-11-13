using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// A road staircase keeps one landing row at each end (one owner, one rule at both ends): the road is carried on over the
/// level floors its layout laid at its foot and at its top up to the row next to the stair (<see cref="TopLandings"/>), never
/// through it, where the road there is built by the road's own rules and meets that row by the ring's (RoadEndRing); where it
/// is not, the floors stay as laid.
/// </summary>
public static partial class RoadStairRuns
{
    /// <summary>Road staircases joining the network with the floors at their foot, and at their top, carried over by their road
    /// (<see cref="CarryRoadOntoStairs"/>).</summary>
    public static int FeetCarriedOnto, TopsCarriedOnto;

    /// <summary>Ends carried onto, every lay of every road tried (attempts): every floor row but the landing row the road
    /// meets.</summary>
    public static int EndsCarried;

    /// <summary>Ends <see cref="CarryRoadOntoStairs"/> looked at, every lay tried: by the floor rows laid there (1, 2, 3, 4
    /// or more; <see cref="StairLayout.EndFloorRows"/>), and those tried
    /// (at least one carry asked of <see cref="RoadOnto"/>'s owners).</summary>
    public static readonly int[] EndsByFloorRows = new int[4];
    public static int EndsTried;

    /// <summary>Roads tried onto a staircase's end (<see cref="RoadOnto"/>), every lay of every road tried, and of them those
    /// refused before their road was planned (<see cref="RefusedUnplanned"/>).</summary>
    public static int RoadOntoCalls, RoadOntoUnplanned;

    /// <summary>Ends whose carry was decided again from this generation's cache (<see cref="PlanCache"/>), every lay tried: the
    /// same end of the same layout on the same road, the network unchanged, its road not judged again.</summary>
    public static int CarriesReused;

    /// <summary>Ends whose floors were kept, by why, every lay of every road tried (attempts, not staircases).</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> EndFloorsKept = new();

    /// <summary>
    /// <paramref name="plan"/> with its road carried on over the level floors at its ends up to the landing row kept next to
    /// its stair (<see cref="TopLandings"/>), where the road can be built there (StairLayout.TrimEndFloors lays the staircase so): at its foot (<paramref name="foot"/>: it
    /// is set off from its road's end's ring and meets no other staircase there) and at its top (<paramref name="top"/>).
    /// Both ends by one rule (<see cref="CarryEnd"/>): each keeps its landing row next to the stair, the road meeting that
    /// row. A foot carried onto starts on its kept row, set off from a new ring centred a ring's radius behind it; a top
    /// carried onto ends on its kept row's far edge, and its road starts again a ring's radius past it
    /// (<see cref="RoadEndPast"/>). Each looks out along the road its own way. The road to judge is the path from the new
    /// end out to the first structure on it (<paramref name="crossings"/>, this road's staircases <paramref name="spans"/>, the
    /// next stretch <paramref name="next"/>), at most a ramp's length. The end the road goes on toward the next stretch from
    /// (its top, or a foot it comes down to) is not tried where the next stretch's staircase sets off from it, as laid or as
    /// carried (<see cref="SetsOffFrom"/>, the test LayOnce decides that by): that end is shared.
    /// </summary>
    internal static StairPlan CarryRoadOntoStairs(StairPlan plan, List<Vector2> path, int start, int to, bool foot, bool top,
        List<RoadStairSpan> spans, IReadOnlyList<RoadCrossing> crossings, (int from, int to)? next, WorldGenerator world, Func<float, float, float> ground)
    {
        var stops = new HashSet<int>();
        if (next is (int nextFrom, _)) stops.Add(nextFrom);
        foreach (RoadCrossing c in crossings) { stops.Add(c.FromIndex); stops.Add(c.ToIndex); }
        foreach (RoadStairSpan s in spans) { stops.Add(s.FromIndex); stops.Add(s.ToIndex); }
        bool reversed = plan.Reversed;
        // This road's staircases as flights, read by every road tried here (OnAFlight): built once, when first asked.
        var runs = new Lazy<List<StairRun>>(() => spans.ConvertAll(s => StairRun.FromPlan(s.FromPoint, s.ToPoint, Width, s.Plan, s.Kit)), false);
        // The next stretch's staircase sets off from this one's end on its way on (a reversed plan's foot, else its top).
        bool Onward(Vector2 at, float height) => next is (int f, int t) && SetsOffFrom(path, reversed, to, at, height, f, t, ground);
        int footAt = reversed ? to : start, beyond = reversed ? to + 1 : start - 1;
        if (foot && beyond >= 0 && beyond < path.Count && plan.Ring is RoadEndRing was && plan.RingStart is Vector2 wasStart && !was.Through
            && plan.FootApron == 0)
        {
            CarryPrint road = CarryRoad(path, footAt, beyond - footAt, stops, reversed, reversed ? next : null, to, spans);
            plan = CarryEnd(plan, atTop: false, road, () => reversed && Onward(RoadEndAt(plan, wasStart), ground(path[footAt].x, path[footAt].y)),
                trimmed =>
                {
                    // Met from straight behind its kept floor row, a ring's radius back (RoadEndAt, a foot with no ring yet).
                    Vector3 f = trimmed.Foot!.Value;
                    Vector2 back = new(f.x, f.z);
                    (trimmed.Ring, trimmed.RingStart) = (null, null);
                    Vector2 end = RoadEndAt(trimmed, back);
                    trimmed.RingStart = back;
                    return (end, f.y, end);
                },
                beyond, beyond - footAt, path, stops, reversed ? Onward : null, runs, world, ground);
        }
        int topAt = reversed ? start : to, past = reversed ? start - 1 : to + 1;
        if (top && past >= 0 && past < path.Count && !plan.CrestFloored && plan.TopCrested == null && !plan.BuiltDown)
        {
            Vector2 road = Back(path, topAt, past - topAt, TravelReach);
            CarryPrint read = CarryRoad(path, topAt, past - topAt, stops, reversed, reversed ? null : next, to, spans);
            plan = CarryEnd(plan, atTop: true, read, () => !reversed && Onward(new Vector2(plan.End.x, plan.End.z), plan.End.y),
                trimmed =>
                {
                    Vector2 last = new(trimmed.End.x, trimmed.End.z);
                    return (RoadEndPast(trimmed, last, trimmed.EndHeading, road, trimmed.End.y, top: true), trimmed.End.y, last);
                },
                past, past - topAt, path, stops, reversed ? null : Onward, runs, world, ground);
        }
        return plan;
    }

    /// <summary>
    /// One end of <paramref name="plan"/> carried onto (<see cref="CarryRoadOntoStairs"/>), its foot or its top
    /// (<paramref name="atTop"/>): every floor row but the landing row next to the stair (<see cref="TopLandings"/>, at a foot
    /// and at a top alike: StairLayout.TrimEndFloors's keep), where every owner passes the road there (<see cref="RoadOnto"/>);
    /// else the staircase stays as laid, kept for the reason. One try: the carry never takes the landing row, and never stops
    /// short of it. <paramref name="newEnd"/> puts the road's new end, and its height, for a trimmed plan, and the point the
    /// next stretch would set off from there (as LayOnce reads that end: a foot's road end, a top's last row); the road from
    /// there runs out along <paramref name="path"/> from <paramref name="from"/> stepping <paramref name="step"/>. The end is
    /// not tried where the next stretch's staircase sets off from it as laid (<paramref name="sharedAsLaid"/>, asked once) or
    /// as carried (<paramref name="onward"/>, null where the road does not go on from this end).
    /// <para>Decided once a generation (<see cref="PlanCache"/>, with the layouts): a road's staircases are laid again on every
    /// retry and replan, and the same end of the same plan on the same road (<paramref name="road"/>, what the road there reads
    /// of the path), the island's network unchanged, is decided as it was (<see cref="CarryKey"/>); its carry, and the ring a
    /// foot's road met, is laid again from the plan, its tallies and log line too.</para>
    /// </summary>
    private static StairPlan CarryEnd(StairPlan plan, bool atTop, CarryPrint road, Func<bool> sharedAsLaid, Func<StairPlan, (Vector2 end, float height, Vector2 onwardAt)> newEnd,
        int from, int step, List<Vector2> path, HashSet<int> stops, Func<Vector2, float, bool>? onward,
        Lazy<List<StairRun>> runs, WorldGenerator world, Func<float, float, float> ground)
    {
        string which = atTop ? "top" : "foot";
        int rows = StairLayout.EndFloorRows(plan, atTop, Width);
        if (rows == 0) return plan;
        System.Threading.Interlocked.Increment(ref EndsByFloorRows[Mathf.Min(rows, EndsByFloorRows.Length) - 1]);
        // Every floor row but the landing row the road meets: a single landing row has nothing to carry, and floors that
        // cannot be trimmed so (one off the end's level in the rows taken) stay as laid.
        if (StairLayout.TrimEndFloors(plan, atTop, TopLandings, Width) is not StairPlan trimmed) return plan;
        System.Threading.Interlocked.Increment(ref EndsTried);
        road.Add(plan);
        var key = new CarryKey(plan.Kit, atTop, road.A, road.B, world, RoadNetworkGenerator.IslandNetworkVersion);
        if (s_plans.TryGetCarry(key, out CarryDecision known))
        {
            System.Threading.Interlocked.Increment(ref CarriesReused);
            Decided(known.Why, which, known.At, known.Floors);
            if (known.Why != null) return plan;
            newEnd(trimmed);
            if (!atTop) trimmed.Ring = known.Ring;
            System.Threading.Interlocked.Increment(ref EndsCarried);
            return trimmed;
        }
        var (end, height, onwardAt) = newEnd(trimmed);
        int floors = atTop ? trimmed.TopFloorsTrimmed : trimmed.FootFloorsTrimmed;
        string? why = sharedAsLaid() || onward?.Invoke(onwardAt, height) == true ? SharedOnward
            : RoadOnto(trimmed, end, height, Line(end, path, from, step, stops), atTop, runs, world, ground);
        s_plans.Carried(key, new CarryDecision(trimmed.Ring, why, end, floors));
        Decided(why, which, end, floors);
        if (why != null) return plan;
        System.Threading.Interlocked.Increment(ref EndsCarried);
        return trimmed;
    }

    /// <summary>
    /// What a staircase end's carry (<see cref="CarryEnd"/>) is decided by, its cache key in <see cref="PlanCache"/>: the plan
    /// as it reaches the carry (its kit, ends, ring and every piece, in <see cref="A"/> and <see cref="B"/>: a layout from the
    /// cache has been edited by its road since, a span cut or a foot dropped), which end, what the road there reads of the path
    /// (<see cref="CarryRoad"/>), the world, and the island's network version (RoadNetworkGenerator.IslandNetworkVersion: the
    /// network RoadOnto's owners judge the road against, the same until a road or staircase is laid). Two retries of one road
    /// reach the same end on the same line; a replan whose line there differs is another key, not a stale decision.
    /// </summary>
    internal readonly struct CarryKey : IEquatable<CarryKey>
    {
        public readonly string Kit;
        public readonly bool AtTop;
        public readonly ulong A, B;
        public readonly WorldGenerator World;
        public readonly int Network;

        public CarryKey(string kit, bool atTop, ulong a, ulong b, WorldGenerator world, int network) =>
            (Kit, AtTop, A, B, World, Network) = (kit, atTop, a, b, world, network);

        public bool Equals(CarryKey o) => A == o.A && B == o.B && AtTop == o.AtTop && Network == o.Network && ReferenceEquals(World, o.World)
            && string.Equals(Kit, o.Kit, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is CarryKey k && Equals(k);
        public override int GetHashCode() => unchecked((int)A ^ (int)(A >> 32) ^ Network * 397 ^ (AtTop ? 1 : 0));
    }

    /// <summary>An end's carry as <see cref="CarryEnd"/> decided it: why it was not carried (null: carried, the staircase
    /// trimmed again from its plan), the ring a carried foot's road met, and what its log line said (the new end, the
    /// floors).</summary>
    internal sealed class CarryDecision
    {
        public readonly RoadEndRing? Ring;
        public readonly string? Why;
        public readonly Vector2 At;
        public readonly int Floors;

        public CarryDecision(RoadEndRing? ring, string? why, Vector2 at, int floors) => (Ring, Why, At, Floors) = (ring, why, at, floors);
    }

    /// <summary>
    /// What the road from one end reads of its road's path (<see cref="CarryKey"/>): from the end's path point
    /// <paramref name="at"/> out stepping <paramref name="step"/>, every point the road's line (<see cref="Line"/>) and its
    /// travel (<see cref="Back"/>) can reach, a ramp's length past the first, each with whether a structure ends there
    /// (<paramref name="stops"/>); where the next stretch may set off from this end (<paramref name="next"/>), the path from
    /// path[<paramref name="to"/>] to its first point and its last (<see cref="SetsOffFrom"/>); this road's staircases
    /// (<paramref name="spans"/>, the flights it may not run onto); and the caps the road is judged by.
    /// </summary>
    private static CarryPrint CarryRoad(List<Vector2> path, int at, int step, HashSet<int> stops, bool reversed, (int from, int to)? next, int to,
        List<RoadStairSpan> spans)
    {
        var print = new CarryPrint();
        print.Add(reversed);
        print.Add(path[at]);
        float along = 0f;
        for (int i = at + step; i >= 0 && i < path.Count; i += step)
        {
            if (i != at + step) along += Vector2.Distance(path[i - step], path[i]);
            print.Add(path[i]);
            print.Add(stops.Contains(i));
            if (along >= Mathf.Max(RoadEndpointRamp.RampLength, TravelReach)) break;
        }
        print.Add(next.HasValue);
        if (next is (int f, int t))
        {
            for (int i = Mathf.Min(to, f); i <= f; i++) print.Add(path[i]);
            print.Add(path[t]);
        }
        print.Add(spans.Count);
        foreach (RoadStairSpan s in spans)
        {
            print.Add(s.FromPoint);
            print.Add(s.ToPoint);
            print.Add(s.Kit.Name);
            print.Add(s.Plan);
        }
        print.Add(RoadNetworkGenerator.EarthworkCap);
        print.Add(RoadNetworkGenerator.ShapedLimit);
        print.Add(RoadGrade.Configured);
        return print;
    }

    /// <summary>Two 64-bit hashes of what a carry reads (<see cref="CarryKey"/>), lengths to the millimetre.</summary>
    internal struct CarryPrint
    {
        public ulong A, B;

        public void Add(int v)
        {
            unchecked
            {
                if (A == 0 && B == 0) (A, B) = (14695981039346656037UL, 0x9E3779B97F4A7C15UL);
                A = (A ^ (uint)v) * 1099511628211UL;
                B = (B + (uint)v + 0x9E3779B97F4A7C15UL) * 0xBF58476D1CE4E5B9UL;
                B ^= B >> 31;
            }
        }
        public void Add(bool v) => Add(v ? 1 : 0);
        public void Add(float v) => Add(float.IsNaN(v) ? int.MinValue + 1 : Mathf.RoundToInt(Mathf.Clamp(v, -1e6f, 1e6f) * 1000f));
        public void Add(Vector2 v) { Add(v.x); Add(v.y); }
        public void Add(Vector3 v) { Add(v.x); Add(v.y); Add(v.z); }
        public void Add(string v) { Add(v.Length); foreach (char c in v) Add(c); }
        public void Add(Vector2? v) { Add(v.HasValue); if (v is Vector2 p) Add(p); }
        public void Add(Vector3? v) { Add(v.HasValue); if (v is Vector3 p) Add(p); }

        /// <summary>A plan as a carry reads it: its kit, ends, headings, ring, what TrimEndFloors goes by, and every piece,
        /// clearance and cut.</summary>
        public void Add(StairPlan p)
        {
            Add(p.Kit);
            Add(p.Reached); Add(p.Reversed); Add(p.BuiltDown); Add(p.CrestFloored); Add(p.StartsOnFloor);
            Add(p.StartHeading); Add(p.EndHeading); Add(p.End); Add(p.Foot); Add(p.RingStart); Add(p.TopCrested);
            Add(p.Ring.HasValue);
            if (p.Ring is RoadEndRing r) { Add(r.Centre); Add(r.Travel); Add(r.Height); Add(r.Radius); Add(r.Through); }
            Add(p.SunkPieces); Add(p.SunkClearances); Add(p.FootLowered); Add(p.FootApron); Add(p.FootFloorsTrimmed); Add(p.TopFloorsTrimmed);
            Add(p.LastRowPieces); Add(p.LastRowClearances); Add(p.LastRowPieceCount); Add(p.LastRowClearanceCount); Add((int)p.LastRowKind);
            Add(p.LastRowStart); Add(p.LastRowHeading); Add(p.LastRowHeight);
            Add(p.Pieces.Count);
            foreach (StairPiece piece in p.Pieces)
            {
                Add((int)piece.Kind); Add(piece.Prefab); Add(piece.Position); Add(piece.YawDegrees); Add(piece.Sunk); Add(piece.Cantilevered);
                Add(piece.Turn.HasValue);
                if (piece.Turn is (int steps, bool overlap)) { Add(steps); Add(overlap); }
            }
            Add(p.Clearances.Count);
            foreach (StairClearance c in p.Clearances) { Add(c.Centre); Add(c.Half); Add(c.YawDegrees); Add(c.Surface); }
            Add(p.Cuts.Count);
            foreach (Vector3 c in p.Cuts) Add(c);
        }
    }

    /// <summary>
    /// Which ends of a stretch's staircase (path[from..to], laid down from its top where <paramref name="reversed"/>) are its
    /// road's own, the ends <see cref="CarryRoadOntoStairs"/> tries: not the end at path[to] where the next stretch's staircase
    /// meets it over a valley (<paramref name="valleyFirst"/>: its foot there) or a crest (<paramref name="ridgeFirst"/>: its
    /// top there), nor the end at path[from] the second out of a valley sets off from on the shared floor
    /// (<paramref name="valleyMeet"/>). That second's end at path[to] is its road's as any staircase's: its foot where it was
    /// laid down to it, else its top (kept as laid, the black marble foot at 1808,-6616 stood on six floors from the road's end
    /// to its first stair, a valley read in a dip 8 m from it).
    /// </summary>
    internal static (bool foot, bool top) CarriedEnds(bool reversed, bool valleyFirst, bool ridgeFirst, bool valleyMeet)
    {
        if (!valleyMeet) return (!valleyFirst, !ridgeFirst);
        bool atTo = !valleyFirst && !ridgeFirst;
        return (atTo && reversed, atTo && !reversed);
    }

    /// <summary>Why an end is kept where the next stretch's staircase sets off from it.</summary>
    private const string SharedOnward = "the next staircase sets off from it";

    /// <summary>The road from a carried end <paramref name="end"/> out along <paramref name="path"/> from its point
    /// <paramref name="from"/>, stepping <paramref name="step"/>: up to the first of <paramref name="stops"/> (a structure's end),
    /// no longer than the ramp a road's end holds (RoadEndpointRamp.RampLength).</summary>
    private static List<Vector2> Line(Vector2 end, List<Vector2> path, int from, int step, HashSet<int> stops)
    {
        var line = new List<Vector2> { end };
        float along = 0f;
        for (int i = from; i >= 0 && i < path.Count; i += step)
        {
            along += Vector2.Distance(line[line.Count - 1], path[i]);
            line.Add(path[i]);
            if (stops.Contains(i) || along >= RoadEndpointRamp.RampLength) break;
        }
        return line;
    }

    /// <summary>Whether the floors at one end go (<paramref name="why"/> null), tallied and logged either way.</summary>
    private static bool Decided(string? why, string end, Vector2 at, int floors)
    {
        if (why != null) EndFloorsKept.AddOrUpdate(end + ": " + why.Split(':', '(')[0].TrimEnd(), 1, (_, n) => n + 1);
        if (RoadNetworkGenerator.RoadsLog)
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(FormattableString.Invariant(
                $"[STAIRS] end floors {end} at ({at.x:F1}, {at.y:F1}): {floors} floor(s) {(why == null ? "carried over by the road to its landing row" : "kept: " + why)}"));
        return why == null;
    }

    /// <summary>
    /// Why the road cannot be carried on to a staircase's end at <paramref name="end"/> (at <paramref name="height"/>) over the
    /// floors <paramref name="plan"/> no longer lays, or null where it can. The road there is the road's own: planned along
    /// <paramref name="line"/> (its first point the end) from the end's height as any road piece is
    /// (RoadSpatialGrid.PlanRoadPath: its grade cap, its curves and turns, its clearance of places), and judged as any road is
    /// at its commit, by generation's own list of what refuses a road as planned (RoadNetworkGenerator.BuiltRoadRefusal: its
    /// earthwork cap, a junction held on a ridge of fill, the network it meets: another road crossed or run beside, a laid
    /// staircase's flight) and what generation searches a road round (RoadNetworkGenerator.SearchedRound: a stretch held far
    /// off its profile, a trench or ridge), which with its one try it cannot, so is refused for; water as the search read it
    /// for this road (<see cref="CarryWater"/>: RoadWater.Walked, a swamp's shallows waded); and this road's own staircases
    /// (<see cref="OnAFlight(IReadOnlyList{StairRun}, IReadOnlyList{Vector2}, IReadOnlyList{float})"/>). The staircase meets
    /// it by the ring's rules: at a foot its kept floor row sets off from the new end's ring as the ring allows a start
    /// (<see cref="RoadEndRing.Allows"/>), at a top its road starts where <see cref="RoadEndPast"/> put it; none of its pieces
    /// in that ring (<see cref="RoadEndRing.Intrudes(StairPlan, out string, bool, float)"/>), and the start pad laid there
    /// (<see cref="RoadEndRing.PadUnder(StairPlan, RoadEndRing, bool, Func{float, float, float}, float, out string)"/>). The
    /// trim's own: no road going on past the new end (a spur foot keeps its layout). How far an end moves is bound by the
    /// owners above, not here.
    /// </summary>
    internal static string? RoadOnto(StairPlan plan, Vector2 end, float height, List<Vector2> line, bool atTop,
        Lazy<List<StairRun>> runs, WorldGenerator world, Func<float, float, float> ground)
    {
        Vector2 away = Back(line, 0, 1, TravelReach);
        if (line.Count < 2 || (away - end).sqrMagnitude < 0.01f) return "no road beyond it";
        System.Threading.Interlocked.Increment(ref RoadOntoCalls);
        if (RefusedUnplanned(end, height, line, world) is string early)
        {
            System.Threading.Interlocked.Increment(ref RoadOntoUnplanned);
            return early;
        }
        // The road: a piece from the end, held to its height there, its far end on its ground.
        RoadSpatialGrid.PlannedPath? road = RoadSpatialGrid.PlanRoadPath(line, RoadNetworkGenerator.RoadWidth, world, startGround: height);
        if (road == null) return "the road: " + (RoadSpatialGrid.LastRefusal ?? "not planned");
        var plans = new List<RoadSpatialGrid.PlannedPath> { road };
        if ((RoadNetworkGenerator.BuiltRoadRefusal(plans, NoCrossings, out Vector2 at) ?? RoadNetworkGenerator.SearchedRound(plans, out at)) is string refused)
            return FormattableString.Invariant($"{refused}: at ({at.x:F1}, {at.y:F1})");
        if (CarryWater(world, road.Points) is string wet) return wet;
        if (OnAFlight(runs.Value, road.Points, road.Heights)) return "on this road's staircase";
        // The staircase meets it at the ring round the new end, the road's travel into it as planned.
        var ring = new RoadEndRing(end, ArrivingTravel(road.Points, end, atStart: true), height);
        if (!atTop)
        {
            if (!ring.Allows(plan.RingStart!.Value, plan.StartHeading, Width))
                return FormattableString.Invariant($"not a start its ring allows: on {plan.StartHeading:F1} off a road arriving on {ring.Travel:F1}");
            plan.Ring = ring;
        }
        if (ring.Intrudes(plan, out string piece, atTop)) return "the staircase in its ring: " + piece;
        if (RoadEndRing.PadUnder(plan, ring, atTop, ground, RoadNetworkGenerator.ShapedLimit, out string padWhy) == null) return "no start pad: " + padWhy;
        return null;
    }

    /// <summary>
    /// Why <see cref="RoadOnto"/> refuses the road from <paramref name="end"/> (at <paramref name="height"/>) along
    /// <paramref name="line"/> whatever road is planned there, or null where only planning it can say: each asked of the
    /// owner that refuses it after planning, at the points every plan keeps (its first point, the end, at its height, and
    /// its last, the line's own; RoadSpatialGrid.PlanRoadPath moves neither). Water at either (<see cref="CarryWater"/>, as
    /// RoadOnto reads every point), earthwork at the first (RoadNetworkGenerator.PastTerrainLimit at the cap, as
    /// BuiltRoadRefusal reads every point, where RoadSpatialGrid.FirstHeight says the height stored there is the end's), and
    /// a road going on past the end straight ahead (<see cref="RoadGoesOnPast(Vector2, IReadOnlyList{Vector2}, RoadEndRing?)"/>
    /// in the straight-on arc of the end's ring, its travel read along the line), which needs no road. That last is the
    /// carry's check and deliberately straight on only, as decided for the carry (the road looked along mostly straight to
    /// the first step), not the 16 headings a freshly laid foot's ring reads.
    /// </summary>
    private static string? RefusedUnplanned(Vector2 end, float height, List<Vector2> line, WorldGenerator world)
    {
        if (CarryWater(world, new[] { end, line[line.Count - 1] }) is string wet) return wet;
        if (RoadSpatialGrid.FirstHeight(line, RoadNetworkGenerator.RoadWidth, height) is float first
            && RoadNetworkGenerator.PastTerrainLimit(end, first, RoadNetworkGenerator.EarthworkLimit))
            return FormattableString.Invariant($"{RoadNetworkGenerator.EarthworkRefusal}: at ({end.x:F1}, {end.y:F1})");
        if (RoadGoesOnPast(end, line, new RoadEndRing(end, ArrivingTravel(line, end, atStart: true), height))) return "a road goes on past it";
        return null;
    }

    /// <summary>
    /// Water on the carry's road at <paramref name="points"/>: the first the search would not walk, or null. The carry's road
    /// is this road's own path for a ramp's length (<see cref="Line"/>), which the search walked by
    /// <see cref="RoadWater.Walked"/> (a swamp's shallows waded, fords on as in a game) and the terrain writer built up, so
    /// it is judged by that rule, not as ground a road is moved onto (RoadWater.BuiltDry): read so, a foot whose road runs on
    /// across swamp shallows its own road already crosses kept its floors for water.
    /// </summary>
    internal static string? CarryWater(WorldGenerator world, IEnumerable<Vector2> points)
    {
        foreach (Vector2 p in points)
            if (!RoadWater.Walked(world, p)) return Water(p);
        return null;
    }

    private static string Water(Vector2 p) => FormattableString.Invariant($"water: at ({p.x:F1}, {p.y:F1})");

    /// <summary>A short road carried onto a staircase crosses nothing: the structure it runs out to ends it (<see cref="Line"/>).</summary>
    private static readonly List<RoadCrossing> NoCrossings = new();
}
