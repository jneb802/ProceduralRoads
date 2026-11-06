using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>A staircase on a road: path points <see cref="FromIndex"/> to
/// <see cref="ToIndex"/> are climbed on it. The road before it stops at
/// <see cref="FromPoint"/>, standing at <see cref="FromHeight"/>; the road after
/// it starts at <see cref="ToPoint"/> and <see cref="ToHeight"/>.</summary>
public sealed class RoadStairSpan
{
    public int FromIndex;
    public int ToIndex;
    public Vector2 FromPoint;
    public Vector2 ToPoint;
    public float FromHeight;
    public float ToHeight;
    public StairKit Kit = StairKit.Stone;
    public StairPlan Plan = new();
    /// <summary>An earlier road laid this staircase: this road walks it and lays nothing.</summary>
    public bool Existing;
    /// <summary>Laid over this stretch and the one before (a merge): its foot is the earlier stretch's.</summary>
    public bool Merged;
    /// <summary>An approach laid out over the ground its place is expected to leave (<see cref="StairRun.ExpectedPlace"/>).</summary>
    public (Vector2 Centre, float Ground)? ExpectedPlace;
    /// <summary>Its top is met by the next staircase of its road (over a crest, or carried on from), not by a road: no road's
    /// end is put past it (RoadStairRuns.RoadEndPast).</summary>
    internal bool TopMet;
    /// <summary>Where its own foot stands, where its road's end was put a ring's radius off it (RoadStairRuns.RoadEndAt,
    /// RoadEndPast): the next staircase of its road that meets it there (down into a valley and up out of it) meets that foot,
    /// and no road's end is left between the two.</summary>
    internal (Vector2 At, float Height)? OwnFoot;
    /// <summary>Its foot is where its road begins or ends (no road past it, nor another staircase meeting it there): the
    /// walker steps off it onto the ground (RoadStairRuns.FootLeftOver judges it by the end reader).</summary>
    internal bool NoRoadAtFoot;
}

/// <summary>
/// The staircases of a generated road. The pathfinder buys a step too steep for road as a stair step
/// and keeps the steps it bought; once the path has been trimmed and fitted to
/// its ends, the stretches of it that lie along those steps are found here,
/// joined where only a little road would lie between them, and each is laid
/// out by <see cref="StairLayout"/> in its biome's kit. The road is cut at them
/// as at a bridge: nothing is levelled or painted on a staircase. Pure logic.
/// </summary>
public static partial class RoadStairRuns
{
    /// <summary>
    /// Two staircases with less road than this between them are one: the
    /// stretch between is laid by the stair layout too, as floors and
    /// stairs hugging the ground (stone blocks rather than earthworks
    /// between two stair runs that are close together; 20 m is the working value).
    /// </summary>
    public const float StoneGap = 20f;
    /// <summary>A path point this close to a stair step lies on it (the path's
    /// ends are moved a little by trimming; its middle points are cell centres).</summary>
    public const float OnStep = 1f;
    /// <summary>Stairs on generated roads are this wide (2 m works well and is a bit more flexible).</summary>
    public const float Width = 2f;
    /// <summary>
    /// Where the ground between two staircases going the same way is steeper
    /// than this share of the road's grade cap, the road between them could
    /// not be built, and they are one staircase however far apart, up to
    /// <see cref="SteepGapReach"/>: 217 roads on one world were refused for
    /// "ends too far apart for the cap" between two staircases.
    /// </summary>
    public const float SteepGapShare = 0.8f;
    /// <summary>
    /// The longest road between two staircases going the same way that is still laid as one staircase
    /// (<see cref="SteepGapShare"/>); and so also the longest level going one staircase's layout may carry: a layout whose
    /// corridor leaves more than this over its climb is refused before its search (<see cref="LevelGoing"/>).
    /// </summary>
    public const float SteepGapReach = 80f;
    /// <summary>Per-leg search budget of a road staircase's layout, in pieces.</summary>
    public const int GenerationExpansions = 50000;
    /// <summary>Total search budget of a road staircase's layout, every leg and backtrack together.</summary>
    public const int GenerationTotalExpansions = 160000;
    /// <summary>Stall budget of a road staircase's layout: a leg gives up after this many pieces without getting any
    /// nearer the goal.</summary>
    public const int GenerationStall = 4000;
    /// <summary>Floor rows a generated staircase ends on at its top, for the road to meet; and the landing rows a road carried
    /// onto a staircase keeps at each end, its foot and its top alike (CarryEnd): the road meets that row, never runs through
    /// it.</summary>
    public const int TopLandings = 1;

    /// <summary>Layouts already made this generation, by ends and kit: a road's
    /// staircases are laid again on every retry and replan, and by every later
    /// road over the same face.</summary>
    private static readonly PlanCache s_plans = new();
    /// <summary>The longest stretch a generated road's staircase is laid out over. Settable for tests.</summary>
    internal static float MaxStairSpan = 250f;
    /// <summary>Whether a layout over level going (<see cref="LevelGoing"/>) is refused. On; settable for tests.</summary>
    internal static bool RefuseLevelGoing = true;

    /// <summary>
    /// How much of a staircase from <paramref name="a"/> to <paramref name="b"/> along <paramref name="corridor"/> (the
    /// straight line where none is given) would be level going, not a climb: the corridor's length less a stair's run
    /// (<see cref="StairLayout.PieceRun"/>) for each stair's rise (<see cref="StairLayout.PieceRise"/>) of the ground's
    /// climb and drop walked along it (<see cref="CorridorGround"/>). Ends at a similar height with a valley or a ridge
    /// between are climbing: the ground's own climb and drop count, not only the ends'. Level going past
    /// <see cref="SteepGapReach"/> is longer than any staircase carries.
    /// </summary>
    internal static float LevelGoingLeft(Vector2 a, Vector2 b, IReadOnlyList<Vector2>? corridor, Func<float, float, float> ground)
    {
        var (length, climb, drop) = CorridorGround(a, b, corridor, ground);
        return length - (climb + drop) * StairLayout.PieceRun / StairLayout.PieceRise;
    }

    /// <summary>Whether a staircase's layout from <paramref name="a"/> to <paramref name="b"/> along
    /// <paramref name="corridor"/> carries more level going than <see cref="SteepGapReach"/> (<see cref="LevelGoingLeft"/>).</summary>
    internal static bool LevelGoing(Vector2 a, Vector2 b, IReadOnlyList<Vector2>? corridor, Func<float, float, float> ground) =>
        LevelGoingLeft(a, b, corridor, ground) > SteepGapReach;

    /// <summary>The ground along a layout's corridor, as its search follows it (a, the corridor's points, b; the straight
    /// line where it has none), read every <see cref="StairLayout.PieceRun"/> with the layout's own ground: its length, and
    /// its total climb and drop walked from a to b.</summary>
    internal static (float length, float climb, float drop) CorridorGround(Vector2 a, Vector2 b, IReadOnlyList<Vector2>? corridor,
        Func<float, float, float> ground)
    {
        float length = 0f, climb = 0f, drop = 0f, last = ground(a.x, a.y);
        Vector2 from = a;
        void Walk(Vector2 to)
        {
            float len = Vector2.Distance(from, to);
            if (len * len <= 0.01f) return;
            int n = Mathf.Max(1, Mathf.CeilToInt(len / StairLayout.PieceRun));
            for (int k = 1; k <= n; k++)
            {
                Vector2 p = Vector2.Lerp(from, to, k / (float)n);
                float h = ground(p.x, p.y);
                if (h > last) climb += h - last; else drop += last - h;
                last = h;
            }
            length += len;
            from = to;
        }
        if (corridor != null) foreach (Vector2 p in corridor) Walk(p);
        Walk(b);
        return (length, climb, drop);
    }
    /// <summary>The share of the allowance a road's staircase layout gets where another follows if it fails
    /// (held to the road's headings, then free in its cells; the last, free anywhere, gets it all): a half.
    /// Settable for tests.</summary>
    internal static int HeldShare = 2;
    /// <summary>The total allowance of the last layout made on this thread (tests).</summary>
    [ThreadStatic] internal static int LastAllowance;

    /// <summary>
    /// The ground as places will leave it (<see cref="PlacedSites.GroundAt(float, float, WorldGenerator)"/>, its one owner)
    /// on a 1 m lattice, read bilinearly between lattice points as the game draws its terrain mesh (1 m between vertices):
    /// the natural ground, and in the footprint of a place placed before its roads the ground it levels. Each
    /// worker thread keeps its own tiles of <see cref="TileSize"/> metres, plain
    /// arrays filled as they are first read: a layout asks the ground millions of
    /// times, and sampled afresh by each, the world generator's blended noise made
    /// one island take 401 s in game; shared through a concurrent
    /// dictionary, the lookups themselves were the cost (slower still).
    /// </summary>
    private const int TileSize = 32;
    [ThreadStatic] private static Dictionary<(int, int), float[]>? t_tiles;
    [ThreadStatic] private static int t_generation;
    [ThreadStatic] private static WorldGenerator? t_world;
    [ThreadStatic] private static (int, int) t_lastKey;
    [ThreadStatic] private static float[]? t_last;
    private static int s_generation;

    internal static float GroundAt(float x, float z, WorldGenerator world)
    {
        if (t_tiles == null || t_generation != s_generation || !ReferenceEquals(t_world, world))
        {
            t_tiles = new Dictionary<(int, int), float[]>();
            t_generation = s_generation;
            t_world = world;
            t_last = null;
        }
        int x0 = Mathf.FloorToInt(x), z0 = Mathf.FloorToInt(z);
        float fx = x - x0, fz = z - z0;
        int ti = TileOf(x0), tk = TileOf(z0), i = x0 - ti * TileSize, k = z0 - tk * TileSize;
        float h00, h10, h01, h11;
        // The cell's four corners in one tile (all but its last row and column): the tile found once. A staircase's search
        // reads the ground some hundred times a piece it tries (StairSupport.Choose), and finding the tile for each corner
        // was most of that.
        if (i + 1 < TileSize && k + 1 < TileSize)
        {
            float[] tile = Tile(ti, tk);
            int at = k * TileSize + i;
            h00 = Read(tile, at, x0, z0, world);
            h10 = Read(tile, at + 1, x0 + 1, z0, world);
            h01 = Read(tile, at + TileSize, x0, z0 + 1, world);
            h11 = Read(tile, at + TileSize + 1, x0 + 1, z0 + 1, world);
        }
        else
        {
            h00 = Lattice(x0, z0, world);
            h10 = Lattice(x0 + 1, z0, world);
            h01 = Lattice(x0, z0 + 1, world);
            h11 = Lattice(x0 + 1, z0 + 1, world);
        }
        return Mathf.Lerp(Mathf.Lerp(h00, h10, fx), Mathf.Lerp(h01, h11, fx), fz);
    }

    /// <summary>The tile a lattice index falls in (rounded down).</summary>
    private static int TileOf(int i) => i >= 0 ? i / TileSize : (i + 1) / TileSize - 1;

    private static float Lattice(int i, int k, WorldGenerator world)
    {
        int ti = TileOf(i), tk = TileOf(k);
        return Read(Tile(ti, tk), (k - tk * TileSize) * TileSize + (i - ti * TileSize), i, k, world);
    }

    /// <summary>This thread's tile (<paramref name="ti"/>, <paramref name="tk"/>), made where it has none.</summary>
    private static float[] Tile(int ti, int tk)
    {
        var key = (ti, tk);
        float[]? tile = t_last != null && t_lastKey == key ? t_last : null;
        if (tile == null)
        {
            if (!t_tiles!.TryGetValue(key, out tile))
            {
                tile = new float[TileSize * TileSize];
                for (int n = 0; n < tile.Length; n++) tile[n] = float.NaN;
                t_tiles[key] = tile;
            }
            t_last = tile;
            t_lastKey = key;
        }
        return tile;
    }

    /// <summary>The lattice point (<paramref name="i"/>, <paramref name="k"/>), at <paramref name="at"/> in its tile: read
    /// from the ground as places leave it the first time.</summary>
    private static float Read(float[] tile, int at, int i, int k, WorldGenerator world)
    {
        float h = tile[at];
        if (float.IsNaN(h)) tile[at] = h = PlacedSites.GroundAt(i, k, world);
        return h;
    }

    /// <summary>Forget the lattice on every thread: the places placed before their roads changed (<see cref="PlacedSites"/>),
    /// so the ground as they leave it did. Filled before they were made ready, a thread's tiles kept the natural ground in
    /// their footprints. The layouts already laid are kept: the places are made ready as a generation is prepared, before
    /// any is laid.</summary>
    internal static void ForgetGround() => System.Threading.Interlocked.Increment(ref s_generation);

    /// <summary>
    /// The ground a staircase is laid out on (one owner): its search reads it, and its kit chooses its supports on it when it
    /// lays them (<see cref="StairStructures.Dresser"/>), so the search and the first build choose the same. A road's staircase
    /// and a way onto a placed place's landing, this lattice (<see cref="GroundAt"/>), which carries the place's preload in
    /// its footprint (<see cref="PlacedSites.GroundAt(float, float, WorldGenerator)"/>); an approach onto a place the game
    /// places after its roads (<paramref name="expectedPlace"/>), the ground that place is expected to leave over it
    /// (<see cref="ApproachGround"/>). Chosen on the natural ground, an approach's flight over a fill hung arches through it
    /// and one over a cut stood off it; chosen on the continuous ground, which differs from the lattice between its metres, a
    /// piece near the wall's height was arched where the search had priced a wall (or the other way round), and one it had
    /// refused could be laid hanging: a way onto a landing was searched on the place's own grid, the lattice's metres, out to
    /// the grid's edge, and dressed on the continuous ground past its footprint's.
    /// </summary>
    internal static Func<float, float, float> PlanningGround((Vector2 Centre, float Ground)? expectedPlace, WorldGenerator world)
    {
        if (expectedPlace is var (centre, centreGround))
            return ApproachGround(centre, centreGround, LocationLevelling.OpsAt(centre), world);
        return (x, z) => GroundAt(x, z, world);
    }

    /// <summary>The ground <paramref name="run"/> was laid out on, an approach's by its place (<see cref="StairRun.ExpectedPlace"/>).</summary>
    internal static Func<float, float, float> PlanningGround(StairRun run, WorldGenerator world) => PlanningGround(run.ExpectedPlace, world);

    /// <summary>
    /// The finished ground <paramref name="run"/>'s kit stands its walls down to (<see cref="StairStructures.Dresser"/>): the
    /// game's vertices after the network's earthwork (<see cref="StructureGround.Vertices"/>), unknown inside the footprint of
    /// a place the game places after its roads; inside an approach's own place's footprint, the ground that place is expected
    /// to leave (<see cref="PlanningGround(StairRun, WorldGenerator)"/>, which its layout was searched on), the one source
    /// for both. Unknown there, an approach's last flight across its place's smoothing was left unwalled where that ground
    /// falls away under its downhill edge.
    /// </summary>
    internal static Func<int, int, float> FinishedGround(StairRun run, WorldGenerator world)
    {
        Func<int, int, float> vertices = StructureGround.Vertices(world);
        if (run.ExpectedPlace is not var (centre, _)) return vertices;
        Func<float, float, float> expected = PlanningGround(run, world);
        return (x, z) =>
        {
            float h = vertices(x, z);
            if (!float.IsNaN(h)) return h;
            return RoadSiteProtection.NearestFootprint(new Vector2(x, z), 0f, out RoadSiteProtection.Footprint f)
                && (f.Centre - centre).sqrMagnitude < 0.25f ? expected(x, z) : float.NaN;
        };
    }

    /// <summary>The centre of the place a staircase onto a place's landing (<see cref="StairRun.KeepsSiteRocks"/>) comes into at
    /// its <see cref="StairRun.To"/> end (<see cref="IntoThePlace(Vector2)"/>): an approach's own place
    /// (<see cref="StairRun.ExpectedPlace"/>), else the footprint that end stands in. Null for any other staircase, or where no
    /// footprint holds that end.</summary>
    internal static Vector2? PlaceOf(StairRun run)
    {
        if (!run.KeepsSiteRocks) return null;
        if (run.ExpectedPlace is var (centre, _)) return IntoThePlace(centre);
        return RoadSiteProtection.NearestFootprint(run.To, 0f, out RoadSiteProtection.Footprint f) ? IntoThePlace(f.Centre) : null;
    }

    public static void ResetCache()
    {
        System.Threading.Interlocked.Increment(ref s_generation);
        s_plans.Clear();
        s_laid.Clear();
        StairLayout.ResetSupportRefusals();
        StairSupport.ResetCounts();
        Crests = ClimbedOn = RoadTopsCrested = TopFloors = FeetLowered = FeetLoweredToEnds = FeetFacedFreely = RidgesBridged = RidgesSnapped = TopsBeside = ValleysMet = FootAprons = RingEndsThrough = RidgesSnappedOffCrest = CrestStubs = ValleysSpanned = RidgesJoinedLow = BackTracksJoined = BackTracksMerged = BackTracksRefused = LedgeUnpriced = FlightLandings = FeetStartedLower = RidgesToFeet = KitsRelaid = ApproachesNotFacingIn = ApproachFacingReruns = AimsBuried = LaidBesideTheirAim = BareTopsKept = 0;
        RoadFeetAcross = MergedFeetAcross = RoadTopsAcross = ApproachStartsAcross = ApproachLayoutsAcross = SiteChecksAcross = 0;
        Array.Clear(RingEnds, 0, RingEnds.Length);
        PadsAtFeet = PadsAtTops = PadsPartial = 0;
        FeetCarriedOnto = TopsCarriedOnto = EndsCarried = EndsSkippedByRows = EndsTried = RoadOntoCalls = RoadOntoUnplanned = CarriesReused = 0;
        Array.Clear(EndsByFloorRows, 0, EndsByFloorRows.Length);
        EndFloorsKept.Clear();
        PadsRefused.Clear();
        StairChain.ResetLog();
        Rejections.Clear();
        LayFailures.Clear();
        s_described = 0;
    }

    /// <summary>
    /// The index ranges of <paramref name="path"/> to climb on stairs. A
    /// staircase only goes one way, so where the path turns from climbing to
    /// descending (over a ridge, through a gully) it is two staircases, and
    /// two are joined across a short gap only when they go the same way: a
    /// layout climbs from its lower end to its upper and cannot cross a crest.
    /// <paramref name="height"/> null: all one way.
    /// </summary>
    public static List<(int from, int to)> Find(IReadOnlyList<Vector2> path, IReadOnlyList<(Vector2 from, Vector2 to)> steps,
        Func<Vector2, float>? height = null)
    {
        var runs = new List<(int from, int to, int way)>();
        if (steps.Count == 0) return new List<(int, int)>();
        int Way(int a, int b) => height == null || height(path[b]) >= height(path[a]) ? 1 : -1;
        for (int i = 1; i < path.Count; i++)
        {
            if (!OnAStep(path[i - 1], path[i], steps)) continue;
            int way = Way(i - 1, i);
            if (runs.Count > 0 && runs[runs.Count - 1].to == i - 1 && runs[runs.Count - 1].way == way)
                runs[runs.Count - 1] = (runs[runs.Count - 1].from, i, way);
            else
                runs.Add((i - 1, i, way));
        }
        // Join runs with only a little road between them, going the same way, or
        // with road between them too steep to build.
        float steep = RoadGrade.Capped(RoadGrade.Configured) ? RoadGrade.Configured * SteepGapShare : float.PositiveInfinity;
        for (int r = runs.Count - 1; r > 0; r--)
        {
            if (runs[r - 1].way != runs[r].way) continue;
            float gap = 0f;
            for (int i = runs[r - 1].to + 1; i <= runs[r].from; i++) gap += Vector2.Distance(path[i - 1], path[i]);
            bool tooSteep = height != null && gap < SteepGapReach && gap > 0f
                && Mathf.Abs(height(path[runs[r].from]) - height(path[runs[r - 1].to])) / gap > steep;
            if (gap >= StoneGap && !tooSteep) continue;
            if (runs[r - 1].to < runs[r].from && Way(runs[r - 1].to, runs[r].from) != runs[r].way) continue;
            runs[r - 1] = (runs[r - 1].from, runs[r].to, runs[r].way);
            runs.RemoveAt(r);
        }
        return runs.ConvertAll(r => (r.from, r.to));
    }

    /// <summary>
    /// How far apart a crest pair's two ends may stand for the road to be tried straight across the hump between them
    /// (<see cref="Humps"/>): six pathfinding cells. The search reads ground only cell to cell, 8 m apart, and over a hump a
    /// few cells across a single steep cell step buys a stair (the knoll at -6955,3372: 32.5, 36.0, 35.5, 31.0 at 8 m from
    /// end to end, its two staircases wound west up to a landing at 40 and back down); across more cells than this, the
    /// search's own grade reading has room to find the road, and a straight line takes the road far off its route.
    /// </summary>
    public const float HumpReach = 6f * RoadPathfinder.CellSize;

    /// <summary>
    /// The crest pairs of <paramref name="path"/>'s stair stretches (<see cref="Find"/>, the ground of
    /// <paramref name="world"/>): a stretch climbing to a point and the next coming down from it, both ends lower than that
    /// point, the two ends within <see cref="HumpReach"/> of each other. Each as the indices of its two ends, in path order.
    /// </summary>
    internal static List<(int from, int to)> Humps(List<Vector2> path, IReadOnlyList<(Vector2 from, Vector2 to)> steps, WorldGenerator world)
    {
        var humps = new List<(int, int)>();
        float Ground(Vector2 p) => GroundAt(p.x, p.y, world);
        List<(int from, int to)> stretches = Find(path, steps, Ground);
        for (int r = 1; r < stretches.Count; r++)
        {
            var (up, down) = (stretches[r - 1], stretches[r]);
            if (up.to != down.from) continue;
            float top = Ground(path[up.to]);
            if (Ground(path[up.from]) >= top || Ground(path[down.to]) >= top) continue;
            if (Vector2.Distance(path[up.from], path[down.to]) <= HumpReach) humps.Add((up.from, down.to));
        }
        return humps;
    }

    /// <summary>The stair steps of <paramref name="steps"/> that none of path[from..to]'s segments lies on: the steps a
    /// road keeps where that stretch is laid as road instead.</summary>
    internal static List<(Vector2 from, Vector2 to)> StepsOff(List<Vector2> path, int from, int to, IReadOnlyList<(Vector2 from, Vector2 to)> steps)
    {
        var kept = new List<(Vector2, Vector2)>();
        foreach (var step in steps)
        {
            bool on = false;
            for (int i = from + 1; i <= to && !on; i++) on = OnAStep(path[i - 1], path[i], new[] { step });
            if (!on) kept.Add(step);
        }
        return kept;
    }

    private static bool OnAStep(Vector2 a, Vector2 b, IReadOnlyList<(Vector2 from, Vector2 to)> steps)
    {
        foreach (var (from, to) in steps)
            if (DistanceToSegment(a, from, to) <= OnStep && DistanceToSegment(b, from, to) <= OnStep
                && Vector2.Distance(a, b) > 0.5f)
                return true;
        return false;
    }

    /// <summary>The path indices round the stair steps on path[from..to] too steep for <paramref name="kit"/>
    /// (<see cref="RoadPathfinder.StairStepTooSteep"/>), first to last, or null where none is.</summary>
    internal static (int from, int to)? TooSteepFor(List<Vector2> path, int from, int to, IReadOnlyList<(Vector2 from, Vector2 to)> steps,
        Func<float, float, float> ground, StairKit kit)
    {
        int first = -1, last = -1;
        for (int i = from + 1; i <= to; i++)
        {
            float run = Vector2.Distance(path[i - 1], path[i]);
            if (run < 0.5f || !OnAStep(path[i - 1], path[i], steps)) continue;
            if (!RoadPathfinder.StairStepTooSteep(Mathf.Abs(ground(path[i].x, path[i].y) - ground(path[i - 1].x, path[i - 1].y)) / run, kit)) continue;
            if (first < 0) first = i - 1;
            last = i;
        }
        return first < 0 ? null : (first, last);
    }

    /// <summary>What a staircase refused for <see cref="InExcludedZone"/> is called in the logs (<see cref="Refusal.ExcludedZone"/>).</summary>
    internal const string ExcludedZone = "in a zone explored before the network";

    /// <summary>
    /// Whether any piece of <paramref name="plan"/>, or any of its cuts (as far as a cut's ground reaches), lies in a zone the
    /// network leaves as it is (NetworkCutoff.Excludes). Such a staircase is not built at all (all or
    /// nothing): the writers skip excluded zones, so it would stand in the others only, a partial staircase.
    /// </summary>
    internal static bool InExcludedZone(StairPlan plan)
    {
        if (NetworkCutoff.ExcludedCount == 0) return false;
        foreach (StairPiece piece in plan.Pieces)
            if (NetworkCutoff.Excludes(ZoneSystem.GetZone(piece.Position))) return true;
        float reach = StairLayout.CutRadius * 2f;
        foreach (Vector3 cut in plan.Cuts)
            foreach (float dx in new[] { -reach, reach })
                foreach (float dz in new[] { -reach, reach })
                    if (NetworkCutoff.Excludes(ZoneSystem.GetZone(new Vector3(cut.x + dx, 0f, cut.z + dz)))) return true;
        return false;
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = ab.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary>Ends of a run closer than this to an existing run's ends are that run.</summary>
    public const float SameRunReach = 2f;

    /// <summary>
    /// Every staircase of <paramref name="path"/> laid out (<see cref="LayOnce"/>), or null when one cannot be. The foot's
    /// ledge price (StairLayout's price for a foot stair standing out past a ledge's edge) only chooses among a staircase's
    /// starts: where every layout of a stretch priced so is unusable, the stretch is laid once more without it before the
    /// road gives it up and closes it to stairs (the price is a tie-break; uncapped, it cost one layout
    /// four Mistlands staircases outright).
    /// A stretch is laid in the kit its road's line guesses; where the staircase laid stands in the other kit's biome
    /// (<see cref="StairKit.ForPlan"/>), the road's staircases are laid again with that stretch in that kit. Where that
    /// road then fails, they are laid once more in their guesses, unchecked, as before: no road loses its stairs for a kit.
    /// </summary>
    public static List<RoadStairSpan>? Lay(List<Vector2> path, IReadOnlyList<(Vector2 from, Vector2 to)> steps,
        IReadOnlyList<RoadCrossing> crossings, WorldGenerator world)
    {
        bool unpricedAnyway = StairLayout.UnpricedLedge;
        var unpriced = new HashSet<int>();
        // Stretches started lower (StartLower) on this lay, and those laid again from where the search put them: a staircase
        // started lower that cannot be laid is laid as before, its road held to its foot.
        var lowered = new HashSet<int>();
        var kept = new HashSet<int>();
        // Each stretch's kit once decided: the kit its first staircase stood in, or failing that its guess.
        var kits = new Dictionary<int, StairKit>();
        var guesses = new Dictionary<int, StairKit>();
        bool check = true;
        try
        {
            while (true)
            {
                lowered.Clear();
                var tally = new LayTally();
                List<RoadStairSpan>? spans = LayOnce(path, steps, crossings, world, unpricedAnyway, unpriced, kept, lowered, kits, check, tally, out var relay);
                if (relay is (int stretch, StairKit guess, StairKit laid))
                {
                    guesses[stretch] = guess;
                    kits[stretch] = laid;
                    System.Threading.Interlocked.Increment(ref KitsRelaid);
                    continue;
                }
                if (spans == null && lowered.Contains(t_stretch) && kept.Add(t_stretch)) continue;
                if (spans == null && guesses.Count > 0 && check)
                {
                    foreach (var g in guesses) kits[g.Key] = g.Value;
                    check = false;
                    continue;
                }
                // Counted for the lay kept only: one laid again (a kit's relay, a foot laid as before, a ledge unpriced) is not.
                if (spans != null)
                {
                    System.Threading.Interlocked.Add(ref FeetStartedLower, lowered.Count);
                    System.Threading.Interlocked.Add(ref FeetLowered, tally.FeetLowered);
                    System.Threading.Interlocked.Add(ref FeetLoweredToEnds, tally.FeetLoweredToEnds);
                    System.Threading.Interlocked.Add(ref FootAprons, tally.FootAprons);
                }
                // Only a stretch whose layouts paid the ledge price at a foot is laid again without it: a second lay of any other failure cost a whole allowance for nothing.
                if (spans != null || unpricedAnyway || !t_stretchLedgePaid || !unpriced.Add(t_stretch)) return spans;
                System.Threading.Interlocked.Increment(ref LedgeUnpriced);
            }
        }
        finally { StairLayout.UnpricedLedge = unpricedAnyway; }
    }

    /// <summary>
    /// Whether a stretch's staircase (path[<paramref name="from"/>..<paramref name="to"/>]) sets off from the end of the
    /// staircase before it, at <paramref name="end"/> (<paramref name="endHeight"/>, its path index <paramref name="endIndex"/>,
    /// <paramref name="reversed"/> where that one was laid down from its top), rather than from a road between them: straight
    /// on from that end (<see cref="MeetsEnd"/>), or one staircase over both across a steep gap
    /// (<see cref="MergesOverSteepGap"/>). The one test LayOnce decides it by, asked again by the carry onto that end
    /// (CarryRoadOntoStairs). Read before the stretch's own staircase is looked up: where it walks one an earlier road laid it
    /// sets off from neither, and the carry, asking first, keeps that end's floors.
    /// </summary>
    internal static bool SetsOffFrom(List<Vector2> path, bool reversed, int endIndex, Vector2 end, float endHeight, int from, int to,
        Func<float, float, float> ground) =>
        MeetsEnd(end, path[from]) || MergesOverSteepGap(path, reversed, endIndex, end, endHeight, from, to, ground);

    /// <summary>A staircase from <paramref name="a"/> sets off from the end of the one before at <paramref name="end"/>: within
    /// <see cref="StoneGap"/> of it, straight (on a winding road the two can stand that close however far the road between).</summary>
    internal static bool MeetsEnd(Vector2 end, Vector2 a) => Vector2.Distance(end, a) < StoneGap;

    /// <summary>
    /// The road between the staircase before (its end at <paramref name="end"/>, <paramref name="endHeight"/>, path index
    /// <paramref name="endIndex"/>, <paramref name="reversed"/> where laid down from its top) and this stretch's
    /// path[<paramref name="from"/>..<paramref name="to"/>] too steep to build, judged from where the earlier one really ended
    /// (up to 3.5 m and half a metre off its path point), along the road: one staircase over both. Only two going the same way
    /// (one staircase cannot cross a crest or a pit), within <see cref="SteepGapReach"/>, steeper than
    /// <see cref="SteepGapShare"/> of the road's grade cap, which must be set.
    /// </summary>
    internal static bool MergesOverSteepGap(List<Vector2> path, bool reversed, int endIndex, Vector2 end, float endHeight, int from, int to,
        Func<float, float, float> ground)
    {
        Vector2 a = path[from], b = path[to];
        if (!RoadGrade.Capped(RoadGrade.Configured) || reversed == (ground(b.x, b.y) > ground(a.x, a.y))) return false;
        float gap = Vector2.Distance(end, path[Mathf.Min(endIndex, from)]);
        for (int i = endIndex + 1; i <= from; i++) gap += Vector2.Distance(path[i - 1], path[i]);
        return gap < SteepGapReach && Mathf.Abs(ground(a.x, a.y) - endHeight) > RoadGrade.Configured * SteepGapShare * Mathf.Max(gap, 1f);
    }

    /// <summary>The stretch <see cref="LayOnce"/> is laying on this thread (its index among the path's stretches).</summary>
    [ThreadStatic] private static int t_stretch;

    /// <summary>Whether a layout of that stretch paid the foot's ledge price (made now or cached).</summary>
    [ThreadStatic] private static bool t_stretchLedgePaid;

    /// <summary>Stretches laid again without the foot's ledge price this generation, every priced layout unusable.</summary>
    public static int LedgeUnpriced;

    /// <summary>Stretches laid again this generation in the kit their first staircase stood in (<see cref="Lay"/>).</summary>
    public static int KitsRelaid;

    /// <summary>What one <see cref="LayOnce"/> did to its staircases' feet, added to the generation's counts
    /// (<see cref="FeetLowered"/>, <see cref="FeetLoweredToEnds"/>, <see cref="FootAprons"/>) only for the lay kept.</summary>
    private sealed class LayTally
    {
        public int FeetLowered, FeetLoweredToEnds, FootAprons;
    }

    /// <summary>
    /// Every staircase of <paramref name="path"/> laid out, or null when one
    /// cannot be: the caller then builds the road without stairs, as before.
    /// Not laid either: a run that overlaps a river crossing, or whose layout
    /// (which finds its own way between the run's ends) passes through a
    /// place's footprint or water. A run an earlier road already laid between
    /// the same ends is walked, not laid again. The ground is the height the
    /// road itself is built to (the game's blended terrain), so the two meet.
    /// A stretch in <paramref name="kits"/> is laid in that kit; any other in the kit its road's line guesses, and, with
    /// <paramref name="check"/>, where its staircase stands in the other kit's biome none is laid: <paramref name="relay"/>
    /// names the stretch, its guess and the kit it stood in, for <see cref="Lay"/> to lay the road's staircases again.
    /// </summary>
    private static List<RoadStairSpan>? LayOnce(List<Vector2> path, IReadOnlyList<(Vector2 from, Vector2 to)> steps,
        IReadOnlyList<RoadCrossing> crossings, WorldGenerator world, bool unpricedAnyway, HashSet<int> unpriced,
        HashSet<int> kept, HashSet<int> startedLower,
        IReadOnlyDictionary<int, StairKit> kits, bool check, LayTally tally, out (int stretch, StairKit guess, StairKit laid)? relay)
    {
        relay = null;
        var spans = new List<RoadStairSpan>();
        float Ground(float x, float z) => GroundAt(x, z, world);
        Heightmap.Biome Biome(float x, float z) => world.GetBiome(x, z);
        List<(int from, int to)> stretches = Find(path, steps, p => Ground(p.x, p.y));
        // Each staircase starts where its road can climb to its foot following its ground (StartLower), but one kept where
        // the search put it; those started lower are noted.
        for (int k = 0; k < stretches.Count; k++)
        {
            if (kept.Contains(k)) continue;
            var lower = StartLower(path, stretches[k], k > 0 ? stretches[k - 1].to : -1, k + 1 < stretches.Count ? stretches[k + 1].from : path.Count,
                crossings, p => Ground(p.x, p.y));
            if (lower == stretches[k]) continue;
            if (RoadNetworkGenerator.RoadsLog)
            {
                Vector2 was = path[lower.from != stretches[k].from ? stretches[k].from : stretches[k].to];
                Vector2 now = path[lower.from != stretches[k].from ? lower.from : lower.to];
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(FormattableString.Invariant(
                    $"[STAIRS] foot at ({was.x:F1}, {was.y:F1}) over a road that cannot climb to it on its ground: started lower, at ({now.x:F1}, {now.y:F1})"));
            }
            stretches[k] = lower;
            startedLower.Add(k);
        }
        for (int k = 0; k < stretches.Count; k++)
        {
            var (from, to) = stretches[k];
            t_stretch = k;
            t_stretchLedgePaid = false;
            StairLayout.UnpricedLedge = unpricedAnyway || unpriced.Contains(k);
            // Up to a crest the next stretch's staircase comes down from: the two meet top to top over the ridge, the
            // first with no floor, crest or road on past its top (the two should snap together, with no road spur at the top).
            bool ridgeFirst = k + 1 < stretches.Count && RidgePair(path, stretches[k], stretches[k + 1], p => Ground(p.x, p.y));
            // Down to a valley's bottom the next stretch's staircase climbs out of: the two meet at their feet on a shared
            // floor, so the first's foot neither faces, lowers to nor stands beside a road (the same rule applies to valleys).
            bool valleyFirst = k + 1 < stretches.Count && RidgePair(path, stretches[k], stretches[k + 1], p => -Ground(p.x, p.y));
            bool valleyMeet = false;
            LastFailedStretch = (from, to);
            foreach (RoadCrossing c in crossings)
                if (c.FromIndex < to && c.ToIndex > from) return Fail("over a river crossing");
            Vector2 a = path[from], b = path[to];
            StairPlan plan = null!;
            int start = from;
            Vector2? meet = null;
            // Laid over both stretches from a ridge's crest and joined to the first's top there (Merge, JoinCrest).
            bool crestJoined = false;
            StairRun? existing = StairStructures.Between(a, b, SameRunReach);
            StairKit kit;
            // The ring at the road's end its foot sets off from (RoadEndRing, the one owner of where a staircase meets its road's
            // end), where the foot (the lower of x and y) is this stretch's road point: none where it meets another staircase
            // (the one before at its top, or the next on a shared floor in a valley), nor after a staircase it carries on from
            // (laid from a crest or with the one before: its road's end is put a ring's radius behind its foot, RoadEndAt).
            RoadEndRing? Ring(Vector2 x, Vector2 y) => valleyFirst || meet != null ? null : FootRingOf(path, start, to, x, y, Ground);
            var carrying = existing == null ? StairStructures.Carrying(a, b, OnStairReach) : null;
            if (carrying.HasValue && s_laid.TryGetValue(carrying.Value.run, out var carried))
            {
                StructurePiece footPiece = carrying.Value.nearA, topPiece = carrying.Value.nearB;
                // Both ends lie on a staircase an earlier road laid, a few metres from its
                // own ends: the road walks that one. Laid again, the copy ran into it and
                // took the road's stairs with it (433 layouts on RoadSeedA).
                spans.Add(new RoadStairSpan
                {
                    FromIndex = from, ToIndex = to, Existing = true, Kit = carried.kit, Plan = carried.plan,
                    FromPoint = new Vector2(footPiece.Position.x, footPiece.Position.z), FromHeight = Surface(footPiece),
                    ToPoint = new Vector2(topPiece.Position.x, topPiece.Position.z), ToHeight = Surface(topPiece),
                });
                continue;
            }
            if (existing != null)
            {
                // Walked as the earlier road laid it: the same plan, so the same heights to meet.
                if (!s_laid.TryGetValue(existing, out (StairPlan plan, StairKit kit) laid)) return Fail("walks a staircase not laid this generation");
                bool same = Vector2.Distance(existing.From, a) <= SameRunReach;
                a = same ? existing.From : existing.To;
                b = same ? existing.To : existing.From;
                (plan, kit) = laid;
            }
            else
            {
                kit = kits.TryGetValue(k, out StairKit? decided) ? decided : StairKit.ForLine(path.GetRange(from, to - from + 1), Biome);
                // The steps it climbs are within its kit's grade: a kit that cannot hold a flight out from the
                // face (wood) is not laid over a step the search bought steeper than it holds by another
                // biome's limit (a Mountain face clipped by a Meadows climb). Those steps' cells are closed to stairs
                // and the road searched again, rather than the layout spending its allowance and failing.
                if (!kit.HoldsOut && TooSteepFor(path, from, to, steps, Ground, kit) is (int, int) steep)
                {
                    LastFailedStretch = steep;
                    return null;
                }
                // The road's own headings at the two ends: the staircase lines up with them
                // (within reason), and failing that is laid as it falls; either way none of
                // it may lie on the road it joins.
                float arriving = StairLayout.Bearing(from > 0 ? path[from - 1] : a, from > 0 ? a : path[from + 1]);
                float leaving = StairLayout.Bearing(to + 1 < path.Count ? b : path[to - 1], to + 1 < path.Count ? path[to + 1] : b);
                // One staircase over both stretches, in place of the one before and this.
                bool Merge()
                {
                    if (spans.Count == 0 || spans[spans.Count - 1].Existing) return false;
                    RoadStairSpan merged = spans[spans.Count - 1];
                    spans.RemoveAt(spans.Count - 1);
                    start = merged.FromIndex;
                    LastFailedStretch = (start, to);
                    // The second over a ridge, its top the crest the one before climbed to (met there, no road between): the
                    // staircase laid over both stretches still meets that top, as the second it replaces did: searched down
                    // from the first's top edge, or snapped off the crest's floor (SnapOffCrest); else laid up to the crest
                    // and joined to the first's top there (JoinCrest), or not laid. Laid from the road's point at the crest
                    // with no meet, it set off from the ground there, its top floors a rise over the first's crest floor and
                    // nothing joining the two (at -9775.8,-528.5 the first's top was left 0.94 m under the merged one's floor).
                    RoadStairSpan? crestOf = spans.Count > 0 && merged.TopMet && merged.Plan.Reversed
                        && spans[spans.Count - 1] is { Plan.Reversed: false } upTo && upTo.ToIndex == merged.FromIndex ? spans[spans.Count - 1] : null;
                    a = crestOf != null ? crestOf.ToPoint : path[start];
                    meet = crestOf != null ? a : null;
                    var line = new List<Vector2>(path.GetRange(start, to - start + 1)) { b };
                    // Its kit guessed over both stretches: the one before's own was for it alone.
                    kit = kits.TryGetValue(k, out StairKit? mergedKit) ? mergedKit : StairKit.ForLine(line, Biome);
                    if (crestOf != null)
                    {
                        StairPlan first = crestOf.Plan;
                        bool floored = first.CrestFloored || first.LastRowKind == StairPieceKind.Landing;
                        var crestRow = new StairContinuation(new Vector2(first.End.x, first.End.z), first.EndHeading, first.End.y,
                            floored ? StairPieceKind.Landing : StairPieceKind.Stair);
                        StairPlan down = StairLayout.BuildDown(crestRow, b, Ground, Width, kit, GenerationExpansions, GenerationTotalExpansions, GenerationStall, corridor: line);
                        if (down.Reached && Usable(down, path, start, to, world, spans, crestRow.Edge))
                        {
                            plan = down;
                            crestOf.ToPoint = a = crestRow.Edge;
                            meet = a;
                            System.Threading.Interlocked.Increment(ref RidgesSnapped);
                            return true;
                        }
                        if (floored && SnapOffCrest(crestOf, crestRow, b, kit, Ground, line, path, start, to, world, spans, ref a, ref meet) is StairPlan off)
                        {
                            plan = off;
                            System.Threading.Interlocked.Increment(ref RidgesSnapped);
                            System.Threading.Interlocked.Increment(ref RidgesSnappedOffCrest);
                            return true;
                        }
                    }
                    float mergedArriving = StairLayout.Bearing(start > 0 ? path[start - 1] : a, start > 0 ? a : path[start + 1]);
                    float? mergedFacing = FootFacing(path, start, to, Ground), mergedTop = TopFacing(path, start, to, Ground);
                    RoadEndRing? mergedRing = Ring(a, b);
                    plan = Plan(a, b, kit, Ground, mergedArriving, leaving, line, retried: true, facing: mergedFacing, topFacing: mergedTop, ring: mergedRing);
                    if (!Usable(plan, path, start, to, world, spans, meet)) plan = Plan(a, b, kit, Ground, null, null, line, retried: mergedFacing.HasValue, facing: mergedFacing, topFacing: mergedTop, ring: mergedRing);
                    if (mergedFacing.HasValue && !Usable(plan, path, start, to, world, spans, meet))
                    {
                        System.Threading.Interlocked.Increment(ref FeetFacedFreely);
                        plan = Plan(a, b, kit, Ground, null, null, line, ring: mergedRing);
                    }
                    if (!Usable(plan, path, start, to, world, spans, meet)) return false;
                    if (crestOf == null) return true;
                    crestJoined = plan.Reversed && !plan.BuiltDown && JoinCrest(meet!.Value);
                    return crestJoined;
                }
                // Down from the crest, the gap from the first one's top row to its own top laid as a flat run: the two meet
                // edge to edge, with no road between them. Returns whether they were joined (the plan's top moved to the crest).
                bool JoinCrest(Vector2 crest)
                {
                    RoadStairSpan over = spans[spans.Count - 1];
                    // Each top row: its far edge, the way it climbs there, what it is.
                    var firstTop = new StairContinuation(crest, over.Plan.EndHeading, over.ToHeight, over.Plan.LastRowKind);
                    var ownTop = new StairContinuation(new Vector2(plan.End.x, plan.End.z), plan.EndHeading, plan.End.y, plan.LastRowKind);
                    // On from the first's top to its own, or the other way where only that fits; up to CrestStubJoinReach, and a
                    // step apart in height at most (at 6678,2440, a stub of road about 2-3 m long on the crest between the two
                    // tops: short sections of road between two stairs are not allowed).
                    // Carried floors only as a short walkway: within the walkway cap over dry ground (StairLayout.JoinTops), else
                    // the gap is left to the road's crossings or another layout.
                    // Where no snapped run fits either way, one snapped to either top with its end floor over the other's (a
                    // refused join lost the pair: 50 of 51 over six worlds, tops off the rise, the other lower, or out of line).
                    TopJoin Either(bool clip)
                    {
                        TopJoin on = StairLayout.JoinTops(plan, firstTop, ownTop, Ground, Width, kit, CrestStubJoinReach, StairLayout.StepUp, clip);
                        if (on.Laid) return on;
                        TopJoin back = StairLayout.JoinTops(plan, ownTop, firstTop, Ground, Width, kit, CrestStubJoinReach, StairLayout.StepUp, clip);
                        return back.Laid || (float.IsNaN(on.Highest) && on.Why == StairLayout.OtherTopLower) ? back : on;
                    }
                    TopJoin joined = Either(false);
                    if (!joined.Laid)
                    {
                        TopJoin clipped = Either(true);
                        joined = clipped.Laid ? clipped : new TopJoin(false, joined.Length, joined.Highest, joined.Why + "; clipped: " + clipped.Why);
                    }
                    StairChain.LogTopJoin(crest, kit, joined);
                    if (!joined.Laid) return false;
                    plan.End = new Vector3(crest.x, plan.End.y, crest.y);
                    System.Threading.Interlocked.Increment(ref RidgesBridged);
                    return true;
                }
                // The way a staircase merged over two stretches lies: from the first's start to this one's end.
                List<Vector2> MergedCorridor() => new List<Vector2>(path.GetRange(start, to - start + 1)) { b };
                // The road between the staircase before it and this one too steep to
                // build, judged from where the earlier one really ended (up to 3.5 m and
                // half a metre off its path point): one staircase over both. 149 roads
                // on RoadSeedA lost their stairs there. Only two going the same way: one staircase cannot cross a crest or a
                // pit, and a pair going opposite ways is met by the crest and valley logic below (every such pair over six
                // worlds, 26 places, was the first's own end standing a third to half a rise off the ground within 2 m of it).
                if (spans.Count > 0 && spans[spans.Count - 1] is { Existing: false } prev
                    && MergesOverSteepGap(path, prev.Plan.Reversed, prev.ToIndex, prev.ToPoint, prev.ToHeight, from, to, Ground))
                {
                    // Set off from its road's end's ring, as any staircase's foot (Merge lays it so).
                    if (!Merge()) return Fail("merged over a steep gap: ", LastRefusal);
                    goto Laid;
                }
                // Straight on from the staircase before it (over a crest: one up, one
                // down): aimed at that one's end and arriving facing it, head-on. Laid
                // to the path point, the two tops ended inside each other.
                if (spans.Count > 0 && MeetsEnd(spans[spans.Count - 1].ToPoint, a))
                {
                    RoadStairSpan before = spans[spans.Count - 1];
                    // Its end here is its top where it climbed this way, its foot where it came down this way: met by this one,
                    // no road there (a foot's road's end, put a ring's radius off it, is taken back to the foot).
                    if (!before.Plan.Reversed) before.TopMet = true;
                    else if (before.OwnFoot is { } itsFoot) (before.ToPoint, before.ToHeight) = (itsFoot.At, itsFoot.Height);
                    a = before.ToPoint;
                    // It takes over the road between the two: none is built there.
                    start = before.ToIndex;
                    meet = a;
                    arriving = before.Plan.Reversed ? StairLayout.Wrap(before.Plan.StartHeading + 180f) : before.Plan.EndHeading;
                }
                var corridor = new List<Vector2> { a };
                corridor.AddRange(path.GetRange(start, to - start + 1));
                corridor.Add(b);
                // Every layout sets off within 90 degrees of the way the road comes to its foot (StairLayout.FacingSteps), or
                // back beside the road for at most StairLayout.MostSetOffBack pieces first, and only as a last resort any way:
                // set off freely, the foot at Hildir_cave 1664,768 climbed back alongside the road that had arrived heading
                // the other way.
                float? facing = meet == null && !valleyFirst ? FootFacing(path, start, to, Ground) : null;
                // And its top arrives within 90 degrees of the way the road leaves it, straight on preferred.
                float? topFacing = meet == null && !ridgeFirst ? TopFacing(path, start, to, Ground) : null;
                int tops = ridgeFirst ? 0 : TopLandings;
                // Down from the crest: from the first one's top stair, head-on, its own top on that stair's edge.
                StairContinuation? crestRow = null;
                // Whether the first's top is a floor on the crest. A staircase an earlier road built (Existing) is met as it is,
                // never edited: no floor is carried onto it nor flag set on its plan, which is the network's.
                bool crestFloored = false;
                if (meet != null && spans[spans.Count - 1].Plan is StairPlan first && !first.Reversed
                    && (first.LastRowKind == StairPieceKind.Stair || first.CrestFloored
                        || first.LastRowKind == StairPieceKind.Landing && first.Reached))
                {
                    RoadStairSpan up = spans[spans.Count - 1];
                    // Its top a floor already (a top landing, or the far floor of a flight up out of a valley): that floor is the
                    // crest's 2x2, the second searched down off it as off one laid on (laid from its own foot, the second's top stood a rise
                    // under the first's).
                    crestFloored = first.CrestFloored || first.LastRowKind == StairPieceKind.Landing;
                    if (crestFloored && !up.Existing) first.CrestFloored = true;
                    // A 2x2 at the crest between the two top stairs (a ridge always has at least one 2x2 between the up and down
                    // stairs): a floor on from the first's top stair, the second from its far edge.
                    // Only on this road's own first staircase: an earlier road's is met at its top stair, back to back.
                    if (!crestFloored && !up.Existing
                        && StairLayout.CarryTopTo(first, up.ToPoint + StairLayout.Forward(first.EndHeading) * StairLayout.PieceRun, up.ToHeight, Ground, Width, kit))
                        crestFloored = first.CrestFloored = true;
                    if (crestFloored) { up.ToPoint = new Vector2(first.End.x, first.End.z); up.ToHeight = first.End.y; a = up.ToPoint; meet = a; }
                    crestRow = new StairContinuation(up.ToPoint, first.EndHeading, up.ToHeight, crestFloored ? StairPieceKind.Landing : StairPieceKind.Stair);
                }
                // Up out of a valley the staircase before came down into: from a floor shared at the bottom, carried on from it
                // as from a laid row, no road between their feet.
                StairContinuation? valleyRow = null;
                // A staircase an earlier road built (Existing) is met as it is: not where its foot's sunk step would stand in the
                // shared floor (dropping it edits the network's plan), and never spanned across (that cuts its flight back).
                if (meet != null && spans[spans.Count - 1].Plan is StairPlan below && below.Reversed && below.Reached
                    && (!spans[spans.Count - 1].Existing || below.SunkPieces == 0)
                    && Ground(b.x, b.y) > Ground(a.x, a.y))
                {
                    RoadStairSpan before = spans[spans.Count - 1];
                    Vector2 foot = below.Foot is Vector3 f ? new Vector2(f.x, f.z) : before.ToPoint;
                    float valleyHeight = below.Foot is Vector3 g ? g.y : before.ToHeight;
                    // The floor laid on away from the first's flight, or turned along the valley's bottom where that is buried.
                    StairPlan? up = null;
                    // Straight on from the first's foot: its low edge on the floor's near edge, the floor at the foot's height. The
                    // second sets off from the floor's far edge, or from either side of it, or past a second floor on: turned, the
                    // floor itself met the foot off-line and the two did not snap (valley at 4392,6438: the two runs must line up where they connect).
                    // Away from the first flight along its own foot stair (a stair faces down its flight). Where its foot is a floor,
                    // the shared floor turns off that floor as one floor off another, on its outside corner, overlapping it: laid on
                    // at the foot point along the stair above, it met the foot floor 22.5 degrees off, sharing no corner (the gully test).
                    StairPiece? footStair = below.Pieces.Skip(below.SunkPieces).Where(p => p.Kind == StairPieceKind.Stair)
                        .OrderBy(p => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), foot)).FirstOrDefault();
                    StairPiece? footFloor = below.Pieces.Skip(below.SunkPieces).Where(p => p.Kind == StairPieceKind.Landing)
                        .FirstOrDefault(p => Vector2.Distance(new Vector2(p.Position.x, p.Position.z) - StairLayout.Forward(p.YawDegrees) * (StairLayout.PieceRun / 2f), foot) < 0.05f);
                    float along = StairLayout.Wrap(footStair != null ? footStair.YawDegrees : below.StartHeading + 180f);
                    Vector2 shared = footFloor != null ? StairLayout.FloorOff(foot, StairLayout.Wrap(footFloor.YawDegrees + 180f), ref along, Width) : foot;
                    Vector2 fwd = StairLayout.Forward(along), floorCentre = shared + fwd * (StairLayout.PieceRun / 2f);
                    var rows = new List<(float floor, StairContinuation row, bool second)>();
                    rows.Add((along, new StairContinuation(shared + fwd * StairLayout.PieceRun, along, valleyHeight, StairPieceKind.Landing), false));
                    foreach (int side in new[] { -1, 1 })
                        rows.Add((along, new StairContinuation(floorCentre + StairLayout.Right(along) * (side * StairLayout.PieceRun / 2f),
                            StairLayout.Wrap(along + side * 90f), valleyHeight, StairPieceKind.Landing), false));
                    rows.Add((along, new StairContinuation(shared + fwd * (2f * StairLayout.PieceRun), along, valleyHeight, StairPieceKind.Landing), true));
                    foreach (var (floor, row, second) in rows)
                    {
                        valleyRow = row;
                        // Laid afresh (not from the cache): the shared floor is added to it.
                        up = StairLayout.Build(row.Edge, b, Ground, Width, kit, GenerationExpansions / HeldShare, null, leaving, corridor,
                            GenerationTotalExpansions / HeldShare, GenerationStall / HeldShare, TopLandings, sunkFoot: true, after: row, relayPads: false);
                        if (!up.Reached) continue;
                        // The shared floor is this one's (and the second on from it): the row it carried on from is not emitted.
                        // Not shared where nothing holds it up (over a drop at the bottom): the next row is tried.
                        if (!StairLayout.AddFloorRow(up, shared, floor, valleyHeight, Ground, Width)
                            || second && !StairLayout.AddFloorRow(up, shared + fwd * StairLayout.PieceRun, floor, valleyHeight, Ground, Width)) continue;
                        if (!Usable(up, path, start, to, world, spans, meet)) continue;
                        plan = up;
                        valleyMeet = true;
                        // The step sunk under the first's foot would stand in the shared floor.
                        StairLayout.DropSunkFoot(below);
                        if (RoadNetworkGenerator.RoadsLog)
                            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(FormattableString.Invariant(
                                $"[STAIRS] valley at ({foot.x:F1}, {foot.y:F1}): shared floor on {floor:F1} at {valleyHeight:F2}, the second off it on {row.Heading:F1}{(second ? " past a second floor" : "")}"));
                        a = foot;
                        // Higher up, a straight span of floors from one flight to the other in line with both, the bottom
                        // and its turn cut off (a valley should avoid a switchback: smoother, with no 180 degree turn).
                        bool shallow = StairChain.CrossesLevel(valleyHeight, before.FromHeight, up.End.y);
                        if (!before.Existing && SpanTheValley(below, up, kit, Ground, valleyHeight, Width,
                                across => StairLayout.Build(across.Edge, b, Ground, Width, kit, GenerationExpansions / HeldShare, null, leaving, corridor,
                                    GenerationTotalExpansions / HeldShare, GenerationStall / HeldShare, TopLandings, sunkFoot: true, after: across, relayPads: false),
                                (p, at) => SpanUsable(p, path, before, to, world, spans, at), out StairPlan spanned, out Vector2 spanFrom, level: shallow))
                        {
                            plan = spanned;
                            a = spanFrom;
                            meet = spanFrom;
                            // The first ends where the span starts: no road between.
                            before.ToPoint = spanFrom;
                            if (below.Foot is Vector3 spanFoot) before.ToHeight = spanFoot.y;
                        }
                        System.Threading.Interlocked.Increment(ref ValleysMet);
                        break;
                    }
                    if (!valleyMeet && RoadNetworkGenerator.RoadsLog)
                        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(FormattableString.Invariant(
                            $"[STAIRS] valley at ({foot.x:F1}, {foot.y:F1}): from a shared floor, {(up != null && up.Reached ? "refused: " + Said(LastRefusal) : "not reached: " + up?.Reason)}; laid from the first's foot instead"));
                }
                // Searched down from that top stair's edge: its own top stair back to back with it, exactly (just one stair up and
                // another down); where that finds no way down, laid up to it and a floor across the gap.
                if (!valleyMeet)
                plan = crestRow.HasValue
                    ? StairLayout.BuildDown(crestRow.Value, b, Ground, Width, kit, GenerationExpansions, GenerationTotalExpansions, GenerationStall, corridor: corridor)
                    : null!;
                StairPlan? offCrest = null;
                // A valley meet keeps the plan it laid; otherwise the ridge is snapped down, or laid up to.
                if (!valleyMeet)
                {
                    if (plan != null && plan.Reached && Usable(plan, path, start, to, world, spans, meet)) System.Threading.Interlocked.Increment(ref RidgesSnapped);
                    else if (crestRow.HasValue && crestFloored
                        && (offCrest = SnapOffCrest(spans[spans.Count - 1], crestRow.Value, b, kit, Ground, corridor, path, start, to, world, spans, ref a, ref meet)) != null)
                    {
                        plan = offCrest;
                        System.Threading.Interlocked.Increment(ref RidgesSnapped);
                        System.Threading.Interlocked.Increment(ref RidgesSnappedOffCrest);
                    }
                    else
                    {
                        if (plan != null && RoadNetworkGenerator.RoadsLog)
                            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(FormattableString.Invariant(
                                $"[STAIRS] ridge at ({crestRow!.Value.Edge.x:F1}, {crestRow.Value.Edge.y:F1}): searched down, {(plan.Reached ? "refused: " + Said(LastRefusal) : "not reached: " + plan.Reason)}; laid up to it instead"));
                        plan = crestRow.HasValue ? Plan(crestRow.Value.Edge, b, kit, Ground, null, null, corridor, after: crestRow, retried: true, relayPads: false) : null!;
                    }
                }
                RoadEndRing? ring = Ring(a, b);
                if (plan == null || !Usable(plan, path, start, to, world, spans, meet))
                    plan = Plan(a, b, kit, Ground, arriving, leaving, corridor, topLandings: tops, retried: true, relayPads: meet == null, facing: facing, topFacing: topFacing, ring: ring);
                if (!Usable(plan, path, start, to, world, spans, meet)) plan = Plan(a, b, kit, Ground, null, null, corridor, topLandings: tops, retried: true, relayPads: meet == null, facing: facing, topFacing: topFacing, ring: ring);
                // Its own way between the ends: kept to the road's cells, a layout
                // that stalls, or comes back onto its road or another staircase,
                // would cost the whole road its stairs.
                if (!Usable(plan, path, start, to, world, spans, meet)) plan = Plan(a, b, kit, Ground, null, null, null, topLandings: tops, retried: facing.HasValue || topFacing.HasValue, relayPads: meet == null, facing: facing, topFacing: topFacing, ring: ring);
                // Its top on the road it hands to (only the joining piece may stand there): laid again to
                // beside the road's start past it, on the side it comes in from, the road carrying on from there.
                if (!Usable(plan, path, start, to, world, spans, meet) && meet == null && LastRefusal == Refusal.OnItsRoad)
                {
                    StairPlan? besideTop = TopBesideItsRoad(path, start, to, a, b, kit, Ground, corridor, tops, facing, topFacing, world, spans);
                    if (besideTop != null) plan = besideTop;
                }
                if ((facing.HasValue || topFacing.HasValue) && !Usable(plan, path, start, to, world, spans, meet))
                {
                    System.Threading.Interlocked.Increment(ref FeetFacedFreely);
                    string? faced = RoadNetworkGenerator.RoadsLog ? Said(LastRefusal) + " (" + plan.Reason + ")" : null;
                    plan = Plan(a, b, kit, Ground, null, null, null, topLandings: tops, relayPads: meet == null, ring: ring);
                    if (faced != null)
                        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(FormattableString.Invariant(
                            $"[STAIRS] foot facing {(facing.HasValue ? facing.Value.ToString("F0") : "-")}, top facing {(topFacing.HasValue ? topFacing.Value.ToString("F0") : "-")} at ({a.x:F1}, {a.y:F1}) -> ({b.x:F1}, {b.y:F1}): no layout facing the road usable: {faced}; laid any way, setting off {plan.StartHeading:F1}"));
                }
                // Into the staircase before it, or onto the road between: one
                // staircase over both stretches instead. On a steep road with
                // short switchback legs, runs a leg apart piled into each other.
                if (!Usable(plan, path, start, to, world, spans, meet) && !Merge()) return Fail("", LastRefusal, spans.Count > 0 ? ", merge too" : "");
                else if (meet == null && !valleyFirst)
                {
                    // Held to the road's last heading, a flight whose own way lay well off it swung
                    // round on its bottom steps (a weird jog; it should start aligned with the road): it sets off on its own heading
                    // instead, from wherever on its ring that fits (merged over two stretches as well: its foot is the first
                    // stretch's, MergedCorridor).
                    if (!plan.Reversed && SwingsAtItsFoot(plan, plan.RingStart ?? a))
                    {
                        StairPlan own = Plan(a, b, kit, Ground, null, leaving, start == from ? corridor : MergedCorridor(), topLandings: tops, facing: facing, topFacing: topFacing, ring: Ring(a, b));
                        if (Usable(own, path, start, to, world, spans, meet)) plan = own;
                    }
                }
                // Down from the crest, the gap from the first one's top row to its own top laid as a flat run (JoinCrest); a
                // staircase laid over both stretches from the crest was joined so already.
                else if (meet is Vector2 crest && plan.Reversed && !plan.BuiltDown && !crestJoined && !JoinCrest(crest))
                {
                    // Neither: one staircase over both stretches instead, or none.
                    System.Threading.Interlocked.Increment(ref CrestStubs);
                    if (!Merge()) return Fail("a road stub between its top and the staircase before it over the ridge, merge too");
                }
                // Whether the two double back where they meet: alongside each other (StairChain.BackTracks), or, over a crest or
                // through a pit (one of the two laid from its top, the other not), the road turning back however wide their
                // loop (StairChain.TurnsBack: its travel into the first's end and out of the second's).
                int pairFrom = spans.Count > 0 ? spans[spans.Count - 1].FromIndex : -1;
                bool turnsBack = meet != null && spans.Count > 0 && spans[spans.Count - 1].Plan.Reversed != plan.Reversed
                    && pairFrom > 0 && to + 1 < path.Count
                    && StairChain.TurnsBack(RoadTravel(path, pairFrom, pairFrom - 1, into: true), RoadTravel(path, to, to + 1, into: false));
                Vector2? backTrack = meet != null && spans.Count > 0 && (turnsBack || StairChain.BackTracks(spans[spans.Count - 1].Plan.Pieces, plan.Pieces, kit).Count > 0)
                    ? meet : null;
                // Over a ridge, the two joined lower down where their flights pass close, both tops taken away.
                if (meet != null && !valleyMeet && plan.Reversed && spans.Count > 0 && spans[spans.Count - 1] is { Existing: false } pair
                    && pair.Plan is { Reversed: false } && Vector2.Distance(pair.ToPoint, meet.Value) < StairLayout.PieceRun
                    && JoinLow(pair, plan, kit, Ground, Width, (p, at) => SpanUsable(p, path, pair, to, world, spans, at), out Vector2 low, turnsBack))
                {
                    pair.ToPoint = low;
                    pair.ToHeight = pair.Plan.End.y;
                    a = low;
                    meet = low;
                    if (backTrack is Vector2 joinedAt)
                    {
                        System.Threading.Interlocked.Increment(ref BackTracksJoined);
                        StairChain.LogBackTrack(joinedAt, kit, pit: false, FormattableString.Invariant($"joined low at ({low.x:F1}, {low.y:F1})"));
                        backTrack = null;
                    }
                }
                // A chain doubling back alongside itself, over a crest or through a pit, is never laid: in a pit joined where the
                // two pass (over a crest JoinLow has tried), else one staircase over both stretches, else refused so the road
                // finds another way.
                if (backTrack is Vector2 doubled && (turnsBack || StairChain.BackTracks(spans[spans.Count - 1].Plan.Pieces, plan.Pieces, kit).Count > 0))
                {
                    RoadStairSpan prior = spans[spans.Count - 1];
                    bool pit = prior.Plan.Reversed && !plan.Reversed;
                    if (pit && !prior.Existing
                        && JoinHigh(prior.Plan, plan, kit, Ground, Width, (p, at) => SpanUsable(p, path, prior, to, world, spans, at), out Vector2 high))
                    {
                        prior.ToPoint = high;
                        if (prior.Plan.Foot is Vector3 highFoot) prior.ToHeight = highFoot.y;
                        a = high;
                        meet = high;
                        System.Threading.Interlocked.Increment(ref BackTracksJoined);
                        StairChain.LogBackTrack(doubled, kit, pit, FormattableString.Invariant($"joined high at ({high.x:F1}, {high.y:F1})"));
                    }
                    else if (Merge())
                    {
                        System.Threading.Interlocked.Increment(ref BackTracksMerged);
                        StairChain.LogBackTrack(doubled, kit, pit, "laid as one staircase over both stretches");
                    }
                    else
                    {
                        System.Threading.Interlocked.Increment(ref BackTracksRefused);

                        StairChain.LogBackTrack(doubled, kit, pit, "refused: " + Said(LastRefusal));
                        return Fail($"a {(pit ? "pit" : "crest")} doubling back alongside itself, merge too");
                    }
                }
            }
            Laid:
            // A road staircase keeps one landing row at each end: the road carried on over the level floors at its foot and its
            // top up to that row, by one rule at both, where its road can be built there. An end it shares with another staircase keeps the layout's
            // own: set off from the one before (meet) or merged with it, over a crest (ridgeFirst), in a valley (valleyFirst,
            // and the second's end on the shared floor: valleyMeet; CarriedEnds), or where the next stretch's staircase sets off
            // from it (CarryRoadOntoStairs asks SetsOffFrom, the test the next stretch decides by). An end carried onto is not
            // moved after (TopOntoSurface, EndUnderFoot and LowerFoot leave it): the road there was judged where it is.
            var (footFree, topFree) = CarriedEnds(plan.Reversed, valleyFirst, ridgeFirst, valleyMeet);
            if (existing == null && meet == null && !plan.BuiltDown && (footFree || topFree))
                plan = CarryRoadOntoStairs(plan, path, start, to, foot: footFree, top: topFree, spans, crossings,
                    k + 1 < stretches.Count ? stretches[k + 1] : null, world, Ground);
            // The plan climbs from the lower end, exactly, to its own end near the higher.
            Vector2 end = new Vector2(plan.End.x, plan.End.z);
            float endHeight = plan.End.y;
            // Under ground that stands over its top, out over the lip and a crest onto it; the road starts on the surface past it.
            if (existing == null && !ridgeFirst && (meet == null || valleyMeet) && TopOntoSurface(plan, path, plan.Reversed ? start - 1 : to + 1, Ground, out Vector2 past, out float pastHeight,
                    (p, added) => TopFits(p, added, path, start, to, world, spans, meet)))
                (end, endHeight) = (past, pastHeight);
            // Its foot over a road that cannot climb to it within the cap: stairs on down toward the road. Set off from its
            // road's end's ring, the foot is where it set off on the ring, at the road's end's height.
            Vector2 footAt = plan.Reversed ? b : a;
            float footHeight = Ground(footAt.x, footAt.y);
            if (existing == null && plan.RingStart is Vector2 onRing) footAt = onRing;
            // Carried on to the floor row kept at its foot, the road ends at that floor's height.
            if (existing == null && plan.FootFloorsTrimmed > 0 && plan.Foot is Vector3 carriedTo) footHeight = carriedTo.y;
            // Searched down from a crest, its foot is where the search came down near the road, not the path point.
            if (plan.BuiltDown && plan.Foot is Vector3 down) (footAt, footHeight) = (new Vector2(down.x, down.z), down.y);
            // A short stretch of road from a crossing's end, levelled at the end's height all the way: the stairs carried on
            // down toward the end to that height, along the end's line, and the stretch they then stand on is theirs. Where
            // they cannot be, the road is not planned with that stretch (RoadNetworkGenerator.PlanPieces: no stub of road).
            // A short stretch of road from a crossing's end, levelled at the end's height all the way (RoadSpatialGrid.LevelledEnds):
            // the staircase's foot is brought to that height. Straight on, the stairs are carried on down toward the end
            // along its line, and the stretch they then stand on is theirs; else the staircase is laid again from its foot at
            // the end's height, on the ground the road levels there. Where neither is laid, the road is not planned with that
            // stretch (RoadNetworkGenerator.PlanPieces: no stub of road between two structures).
            if (existing == null && !valleyFirst && meet == null && plan.FootFloorsTrimmed == 0
                && EndUnderFoot(path, footAt, plan.Reversed ? to : start, plan.Reversed ? 1 : -1, crossings, world, footHeight) is (int endIndex, StructureEnds.End under))
            {
                Vector2 outward = -under.Up, across = new(-outward.y, outward.x);
                float wasOut = Vector2.Dot(footAt - under.Meet, outward);
                StairPlan toEnd = plan.Copy();
                if (StairLayout.LowerFootTo(toEnd, Ground, Width, under.MeetHeight) is Vector3 lowest
                    && Vector2.Dot(new Vector2(lowest.x, lowest.z) - under.Meet, outward) is float nowOut && nowOut >= 0f && nowOut < wasOut
                    && Mathf.Abs(Vector2.Dot(new Vector2(lowest.x, lowest.z) - under.Meet, across)) <= RoadNetworkGenerator.RoadWidth / 2f
                    && !InExcludedZone(toEnd) && KeepsItsRingLowered(toEnd, lowest))
                {
                    plan = toEnd;
                    (footAt, footHeight) = (new Vector2(lowest.x, lowest.z), lowest.y);
                    if (plan.Reversed) { while (to < endIndex && Vector2.Dot(path[to] - under.Meet, outward) > nowOut) to++; }
                    else { while (start > endIndex && Vector2.Dot(path[start] - under.Meet, outward) > nowOut) start--; }
                    tally.FeetLoweredToEnds++;
                }
                else if (FromTheEndsHeight(path, start, to, kit, Ground, under, footAt, world, spans) is StairPlan again)
                {
                    plan = again;
                    footHeight = under.MeetHeight;
                    if (plan.Foot is Vector3 f) footAt = new Vector2(f.x, f.z);
                    tally.FeetLoweredToEnds++;
                }
            }
            int beyond = plan.Reversed ? to + 1 : start - 1;
            // A foot set off from its road's end's ring moved down toward its road takes the road's end with it (RoadEndAt), only
            // where the road's end moved so still meets it on a ring, the road's travel read into the moved end: the ring judges
            // it, as it judges any foot, straight on or turned (no straight/turned split; held to straight on, the first of a
            // valley's two staircases, 44 degrees off its road, kept its foot up the valley's side and the road went down into
            // the valley between the two, 23 m, where lowered they met on a floor shared at the bottom).
            bool KeepsItsRingLowered(StairPlan moved, Vector3 foot)
            {
                if (moved.Ring is not RoadEndRing ring || !moved.RingStart.HasValue) return true;
                int at = moved.Reversed ? to : start, step = moved.Reversed ? 1 : -1;
                if (at + step < 0 || at + step >= path.Count) return true;
                Vector2 end = RoadEndAt(moved, new Vector2(foot.x, foot.z));
                var there = new RoadEndRing(end, StairLayout.Bearing(Back(path, at, step, TravelReach), end), foot.y, ring.Through);
                return !there.Intrudes(moved);
            }
            // The plan is this road's own (PlanCache hands out copies), so it is edited without a lock.
            if (existing == null && beyond >= 0 && beyond < path.Count && !valleyFirst && meet == null)
            {
                Vector3? lowered = plan.FootLowered > 0 ? plan.Foot : null;
                if (lowered == null)
                {
                    Vector2 backAt = Back(path, plan.Reversed ? to : start, plan.Reversed ? 1 : -1, FootBackReach);
                    StairPlan trial = plan.Copy();
                    lowered = StairLayout.LowerFoot(trial, Ground, Width, Ground(backAt.x, backAt.y),
                        Vector2.Distance(backAt, plan.RingStart.HasValue ? RoadEndAt(plan, footAt) : footAt),
                        RoadGrade.Capped(RoadGrade.Configured) ? RoadGrade.Configured : 1f);
                    if (lowered is Vector3 low && !KeepsItsRingLowered(trial, low)) lowered = null;
                    if (lowered != null) { plan = trial; tally.FeetLowered++; }
                }
                if (lowered is Vector3 f) { footAt = new Vector2(f.x, f.z); footHeight = f.y; }
            }
            // Its foot against a bank: a small level apron cut there for the road to meet (outside any place's footprint:
            // the commit skips cuts inside one), once per plan.
            if (existing == null && meet == null && !valleyFirst && plan.FootApron == 0 && StairLayout.CutFootApron(plan, Ground, Width) > 0)
                tally.FootAprons++;
            // Where the road ends at its foot (RoadEndRing): the centre of the ring it set off from, moved with the foot where
            // that was moved since (RoadEndAt); at a foot the staircase decided (searched down from a crest), a ring's radius out
            // from it where the road keeps out of it (RoadEndPast; its top's likewise, once every staircase is laid). A staircase
            // an earlier road laid is met where that road met it.
            (Vector2, float)? ownFoot = null;
            if (existing != null)
            {
                Vector2 Met(Vector2 near) => new[] { existing.FromMeet, existing.ToMeet }.Where(m => m.HasValue).Select(m => m!.Value)
                    .Where(m => Vector2.Distance(m, near) <= JoiningStep + 1f).OrderBy(m => Vector2.Distance(m, near)).DefaultIfEmpty(near).First();
                (footAt, end) = (Met(footAt), Met(end));
            }
            else
            {
                if (beyond >= 0 && beyond < path.Count && !valleyFirst && !(meet != null && !plan.Reversed))
                {
                    ownFoot = (footAt, footHeight);
                    footAt = plan.RingStart.HasValue ? RoadEndAt(plan, footAt)
                        : RoadEndPast(plan, footAt, StairLayout.Wrap(plan.StartHeading + 180f), Back(path, plan.Reversed ? to : start, plan.Reversed ? 1 : -1, TravelReach), footHeight, top: false);
                }
            }
            var span = plan.Reversed
                ? new RoadStairSpan { FromIndex = start, ToIndex = to, FromPoint = end, FromHeight = endHeight, ToPoint = footAt, ToHeight = footHeight, Kit = kit, Plan = plan }
                : new RoadStairSpan { FromIndex = start, ToIndex = to, FromPoint = footAt, FromHeight = footHeight, ToPoint = end, ToHeight = endHeight, Kit = kit, Plan = plan };
            span.Existing = existing != null;
            span.OwnFoot = ownFoot;
            span.NoRoadAtFoot = existing == null && (beyond < 0 || beyond >= path.Count) && !valleyFirst && !(meet != null && !plan.Reversed);
            span.Merged = start != from;
            // Its top the crest the staircase before it met, or the one before's met by this one: no road at it.
            span.TopMet = ridgeFirst || meet != null && plan.Reversed;
            // What holds it up is laid by its kit at first build, its walls down to the ground the terrain writer leaves
            // under the road's cut (StairSupport), not on a guess at the road's cut here.
            // Whole or not at all: what was added after its layout was judged (a top carried on, a foot lowered, an apron
            // cut) may not reach a zone explored before the network either.
            if (existing == null && InExcludedZone(plan)) { LastRefusal = Refusal.ExcludedZone; return Fail("", Refusal.ExcludedZone); }
            // Built in the kit its own pieces stand in, not its road's: a layout that left its road for another way between
            // its ends is laid again in that kit (Lay), once.
            if (check && existing == null && !kits.ContainsKey(k) && StairKit.ForPlan(plan, Biome) is var stood && stood != kit)
            {
                relay = (k, kit, stood);
                return null;
            }
            spans.Add(span);
        }
        // At a top the staircase decided, where the road carries on from it (not the next staircase), the road starts a ring's
        // radius out from its exit (RoadEndPast): its top meets the road on the ring there. Done once every staircase is laid,
        // so the next staircase meets a top where it really ends.
        foreach (RoadStairSpan s in spans)
        {
            if (s.Existing || s.TopMet) continue;
            bool reversed = s.Plan.Reversed;
            int top = reversed ? s.FromIndex : s.ToIndex, past = reversed ? top - 1 : top + 1;
            if (past < 0 || past >= path.Count) continue;
            Vector2 exit = reversed ? s.FromPoint : s.ToPoint;
            Vector2 road = RoadEndPast(s.Plan, exit, s.Plan.EndHeading, Back(path, top, past - top, TravelReach), reversed ? s.FromHeight : s.ToHeight, top: true);
            if (reversed) s.FromPoint = road; else s.ToPoint = road;
        }
        return spans;
    }

    /// <summary>Plans laid on the network this generation, by run, so a later road walking
    /// a run meets it at the heights it was laid at.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<StairRun, (StairPlan plan, StairKit kit)> s_laid = new();

    /// <summary>Record the plan a run was laid from (called as the run joins the network): its road was accepted, so the
    /// plan, as that road edited it, becomes the cached layout for its key (<see cref="PlanCache"/>).</summary>
    public static void Laid(StairRun run, StairPlan plan, StairKit kit)
    {
        RoadNetworkGenerator.IslandNetworkChanged();
        s_laid[run] = (plan, kit);
        s_plans.Commit(plan);
        if (plan.FootFloorsTrimmed > 0) System.Threading.Interlocked.Increment(ref FeetCarriedOnto);
        if (plan.TopFloorsTrimmed > 0) System.Threading.Interlocked.Increment(ref TopsCarriedOnto);
        RoadNetworkGenerator.IslandActivity?.BuiltStaircases(1);
    }

    /// <summary>The plan a run was laid from, if laid this generation (tests).</summary>
    internal static StairPlan? LaidPlan(StairRun run) => s_laid.TryGetValue(run, out var laid) ? laid.plan : null;

    /// <summary>
    /// Layouts made this generation, by ends and kit, each kept as laid out: a road is handed a copy, from the cache
    /// (<see cref="TryGetValue"/>) or of the layout it has just made (the indexer stores a copy and leaves the road its
    /// own), and whatever the road edits on it (a span cutting a flight back, a foot dropped into a shared floor, a crest's
    /// floor, a top carried out, a foot lowered) stays on that copy until the road is accepted, when <see cref="Laid"/>
    /// commits it under its key. Edited in place, a refused road left its edits for its own retry and every later road
    /// over the same cells.
    /// </summary>
    private sealed class PlanCache
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int, int, int, string, int, int), StairPlan> m_plans = new();
        /// <summary>The key each plan handed out was cached under, and the generation it belongs to.</summary>
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<StairPlan, Handed> m_handed = new();
        private int m_generation;
        /// <summary>Whether the layout cached under a key paid the foot's ledge price (Lay's tie-break).</summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int, int, int, string, int, int), bool> m_ledgePaid = new();
        /// <summary>StairLayout.LedgePrices when this thread last missed the cache, before it laid out the plan it caches next.</summary>
        [ThreadStatic] private static int t_pricesBefore;

        private sealed class Handed
        {
            public readonly (int, int, int, int, string, int, int) Key;
            public readonly int Generation;
            public Handed((int, int, int, int, string, int, int) key, int generation) { Key = key; Generation = generation; }
        }

        /// <summary>Layouts made without the foot's ledge price (<see cref="Lay"/>) are kept apart from priced ones.</summary>
        private static (int, int, int, int, string, int, int) Salted((int, int, int, int, string, int, int) key) =>
            StairLayout.UnpricedLedge ? (key.Item1, key.Item2, key.Item3, key.Item4, key.Item5 + "|unpriced ledge", key.Item6, key.Item7) : key;

        public bool TryGetValue((int, int, int, int, string, int, int) key, out StairPlan plan)
        {
            key = Salted(key);
            if (!m_plans.TryGetValue(key, out StairPlan? cached)) { t_pricesBefore = StairLayout.LedgePrices; plan = null!; return false; }
            if (m_ledgePaid.TryGetValue(key, out bool paid) && paid) t_stretchLedgePaid = true;
            plan = cached.Copy();
            m_handed.Add(plan, new Handed(key, m_generation));
            return true;
        }

        /// <summary>Caches a copy of <paramref name="value"/>; the road keeps <paramref name="value"/> itself.</summary>
        public StairPlan this[(int, int, int, int, string, int, int) key]
        {
            set
            {
                key = Salted(key);
                bool paid = StairLayout.LedgePrices != t_pricesBefore;
                m_ledgePaid[key] = paid;
                if (paid) t_stretchLedgePaid = true;
                m_plans[key] = value.Copy();
                m_handed.Add(value, new Handed(key, m_generation));
            }
        }

        /// <summary>An accepted road's plan, as it edited it, becomes the layout cached under its key.</summary>
        public void Commit(StairPlan plan)
        {
            if (m_handed.TryGetValue(plan, out Handed? h) && h.Generation == m_generation) m_plans[h.Key] = plan;
        }

        /// <summary>
        /// Each staircase end's carry decided this generation (<see cref="CarryEnd"/>): a layout handed out again on a retry or
        /// replan comes back to the same end with the same road, and would ask <see cref="RoadOnto"/>'s owners the same
        /// question again. Kept beside the layouts, keyed by what the decision reads
        /// (<see cref="CarryKey"/>), and forgotten with them.
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<CarryKey, CarryDecision> m_carries = new();

        public bool TryGetCarry(CarryKey key, out CarryDecision decision) => m_carries.TryGetValue(key, out decision!);

        public void Carried(CarryKey key, CarryDecision decision) => m_carries[key] = decision;

        public void Clear()
        {
            m_plans.Clear();
            m_ledgePaid.Clear();
            m_carries.Clear();
            System.Threading.Interlocked.Increment(ref m_generation);
        }
    }

    private static StairPlan Plan(Vector2 a, Vector2 b, StairKit kit, Func<float, float, float> ground, float? arriving, float? leaving, List<Vector2>? corridor,
        float maxBuried = StairCover.RoadStaircase, int topLandings = TopLandings, float? footHeight = null, StairContinuation? after = null,
        bool retried = false, bool relayPads = true, float? facing = null, float? topFacing = null, (Vector2 centre, float radius)? apron = null,
        RoadEndRing? ring = null, Vector2? intoPlace = null)
    {
        // apron: a staircase onto a place's apron, whose search keeps every piece and its top's exit edge within its lip (WithinLip),
        // arriving on it facing into the place (intoPlace, IntoThePlace).
        int Key(float? h) => h.HasValue ? Mathf.RoundToInt(StairLayout.SnapHeading(h.Value) / StairLayout.HeadingStep) : -1;
        int Row(int h)
        {
            if (!after.HasValue) return h;
            unchecked
            {
                StairContinuation row = after.Value;
                return ((h * 31 + Mathf.RoundToInt(row.Edge.x * 10f)) * 31 + Mathf.RoundToInt(row.Edge.y * 10f)) * 31
                    + Mathf.RoundToInt(row.Height * 100f) * 17 + Key(row.Heading) * 3 + (int)row.Kind;
            }
        }
        int Apron(int h)
        {
            unchecked
            {
                // The apron, and whether the plan is held to face into the place (intoPlace): the facing pass and its
                // fallback are separate plans.
                if (apron is { } flat)
                    h = (((h * 31 + Mathf.RoundToInt(flat.centre.x * 10f)) * 31 + Mathf.RoundToInt(flat.centre.y * 10f)) * 31 + Mathf.RoundToInt(flat.radius * 10f)) * 31
                        + (intoPlace.HasValue ? 1 : 0);
                // The ring at the road's end it sets off from or arrives on (RoadEndRing): its centre, travel, height and lane.
                if (ring is RoadEndRing r)
                    h = (((h * 31 + Mathf.RoundToInt(r.Centre.x * 10f)) * 31 + Mathf.RoundToInt(r.Centre.y * 10f)) * 31 + Mathf.RoundToInt(r.Travel * 10f)) * 31
                        + Mathf.RoundToInt(r.Height * 100f) * 3 + (r.Through ? 1 : 2);
                return h;
            }
        }
        int Line()
        {
            if (corridor == null) return Apron(Row(-1 - Mathf.RoundToInt(maxBuried * 100f) - 1000 * topLandings));
            unchecked
            {
                int h = corridor.Count;
                foreach (Vector2 p in corridor) h = h * 31 + Mathf.RoundToInt(p.x) * 7919 + Mathf.RoundToInt(p.y);
                return Apron(Row(footHeight.HasValue ? h * 31 + Mathf.RoundToInt(footHeight.Value * 100f) : h));
            }
        }
        var key = (Mathf.RoundToInt(a.x * 10f), Mathf.RoundToInt(a.y * 10f), Mathf.RoundToInt(b.x * 10f), Mathf.RoundToInt(b.y * 10f), kit.Name,
            Key(arriving) * 64 + Key(leaving) + (relayPads ? 0 : 1 << 20) + ((Key(facing) + 1) << 22) + ((Key(topFacing) + 1) << 27), Line());
        if (s_plans.TryGetValue(key, out StairPlan? cached)) return cached;
        // A stretch longer than any staircase laid is not laid out: none over 133 m was on the
        // layouts offline (208 m on RoadSeedD), and the 19 over 150 m that were tried (to 878 m, the road's
        // steps bought one by one along a whole slope) all failed, spending their allowances: 50 s of the
        // 83 s of failed layouts on that world.
        // Nor is level going longer than the road between two staircases still laid as one (SteepGapReach): the layouts
        // over it, a long winding stretch whose ground climbs little for its length, all failed offline, spending their
        // allowances. Ends at a similar height with a valley or a ridge between count as climbing.
        float span = Vector2.Distance(a, b);
        string? refused = null;
        if (span > MaxStairSpan)
            refused = FormattableString.Invariant($"{span:F0} m, longer than a staircase is laid ({MaxStairSpan:F0} m)");
        else if (RefuseLevelGoing && LevelGoingLeft(a, b, corridor, ground) is var level && level > SteepGapReach)
            refused = FormattableString.Invariant($"level going: {level:F0} m left over the corridor's climb, more than a staircase carries ({SteepGapReach:F0} m)");
        if (refused != null) return s_plans[key] = new StairPlan { Reason = refused };
        // A layout another follows if it fails (retried: held to the road's headings, then free in the road's
        // cells, then free anywhere between the ends) is tried on a share of the allowance; the last gets all
        // of it. Failing held layouts spent the full allowance
        // first, the largest part of the slowest island's staircases (191 s in game).
        int share = retried ? HeldShare : 1;
        LastAllowance = GenerationTotalExpansions / share;
        long began = SearchStats.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        Func<Vector2, float>? onApron = apron is { } kept ? ApronDepth(kept.centre, kept.radius) : null;
        Func<Vector2, bool>? withinLip = onApron == null ? null : p => WithinLip(onApron(p));
        StairPlan plan = StairLayout.Build(a, b, ground, Width, kit, GenerationExpansions / share, arriving, leaving, corridor, GenerationTotalExpansions / share,
            GenerationStall / share, topLandings, sunkFoot: true, maxBuried: maxBuried, footHeight: footHeight, after: after, relayPads: relayPads, facing: facing, topFacing: topFacing,
            allowed: withinLip, exitAllowed: withinLip, ring: ring, intoPlace: intoPlace);
        if (SearchStats.Enabled) SearchStats.Record(SearchStats.StairLayout, plan.Reached, !plan.Reached, plan.Pieces.Count, began);
        return s_plans[key] = plan;
    }

    /// <summary>Metres from either end of a staircase where it may meet its road.</summary>
    public const float JunctionReach = 6f;
    /// <summary>How far from the end the piece that joins the road (and the sunk step under it) stands: only it may lie on
    /// a road's surface.</summary>
    public const float JoiningStep = 3.2f;

    /// <summary>Near a junction, a piece this close to the road's level there meets the road; higher, it is over it.</summary>
    public const float JunctionHeight = 2f;
    /// <summary>A walked piece's centre closer than this to the road's centreline lies on the road.</summary>
    public const float OnRoad = 3f;

    /// <param name="ground">The road's ground the layout was laid on, where not the generator's (a stretch levelled to a
    /// crossing's end).</param>
    private static bool Usable(StairPlan plan, List<Vector2> path, int from, int to, WorldGenerator world, List<RoadStairSpan> before, Vector2? meet,
        Func<float, float, float>? ground = null)
    {
        ground ??= (x, z) => GroundAt(x, z, world);
        Refusal why = !plan.Reached ? Refusal.NotReached : !Clear(plan, world) ? Refusal.WaterOrPlace
            : InExcludedZone(plan) ? Refusal.ExcludedZone
            : OverRoad(plan, path, from, to, ground) ? Refusal.OnItsRoad
            // Into the ring at the road's end it set off from (RoadEndRing): across its road.
            : plan.Ring is RoadEndRing ring && plan.RingStart.HasValue && ring.Intrudes(plan) ? Refusal.AcrossItsRoad
            : OverStairs(plan, before, meet) ? Refusal.OnAnotherStaircase
            : BesideARoad(plan, path, from, to, ground) ? Refusal.BesideARoad : Refusal.None;
        if (why != Refusal.None) LastRefusal = why;
        if (RoadNetworkGenerator.RoadsLog) Rejections.AddOrUpdate(why == Refusal.NotReached ? "not reached: " + ReasonKind(plan.Reason) : Said(why), 1, (_, n) => n + 1);
        return why == Refusal.None;
    }

    /// <summary>A refusal in words, for the log.</summary>
    internal static string Said(Refusal why) => why switch
    {
        Refusal.None => "laid",
        Refusal.NotReached => "not reached",
        Refusal.WaterOrPlace => "water or a place",
        Refusal.ExcludedZone => ExcludedZone,
        Refusal.OnItsRoad => "on its road",
        Refusal.OnAnotherStaircase => "on another staircase",
        Refusal.AcrossItsRoad => AcrossItsRoad,
        _ => "beside a road",
    };

    /// <summary>With RoadNetworkGenerator.RoadsLog: layouts tried this generation, by what became of them.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Rejections = new();

    /// <summary>With RoadNetworkGenerator.RoadsLog: roads that gave up all their stairs this generation, by the staircase
    /// that stopped them.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> LayFailures = new();

    /// <summary>With RoadNetworkGenerator.RoadsLog: the first refusals of a generation, described, for the log.</summary>
    private static int s_described;
    private static void Describe(string what)
    {
        if (System.Threading.Interlocked.Increment(ref s_described) <= 25)
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("[STAIRS] refused: " + what);
    }

    /// <summary>Lay gives up the road's stairs: <paramref name="what"/>, then the refusal and <paramref name="after"/>,
    /// tallied with RoadNetworkGenerator.RoadsLog (no string is built without it).</summary>
    private static List<RoadStairSpan>? Fail(string what, Refusal? why = null, string after = "")
    {
        if (RoadNetworkGenerator.RoadsLog) LayFailures.AddOrUpdate(what + (why is Refusal r ? Said(r) : "") + after, 1, (_, n) => n + 1);
        return null;
    }

    /// <summary>A layout's reason for not reaching, without its numbers, and
    /// whether it gave up on the last leg (the goal's height and heading) or on
    /// the way: the tally that says which rule loses a road its stairs.</summary>
    internal static string ReasonKind(string reason)
    {
        if (reason.Length == 0) return "no reason";
        var m = System.Text.RegularExpressions.Regex.Match(reason, @"\(waypoint (\d+) of (\d+)\)");
        string where = !m.Success ? "one leg" : m.Groups[1].Value == m.Groups[2].Value ? "last leg" : "on the way";
        string kind = System.Text.RegularExpressions.Regex.Replace(reason, @"\s*\(waypoint \d+ of \d+\)", "");
        kind = System.Text.RegularExpressions.Regex.Replace(kind, @"\d+", "#");
        return $"{kind}, {where}";
    }

    /// <summary>A walked piece this close across to a road, and within <see cref="ParallelHeight"/> of its height, runs
    /// beside it; <see cref="ParallelPieces"/> in a row make a staircase redundant there. The owner's (<see cref="RoadBeside"/>).</summary>
    public const float ParallelReach = RoadBeside.Reach;
    public const float ParallelHeight = RoadBeside.Height;
    public const int ParallelPieces = RoadBeside.MostStairPieces;

    /// <summary>
    /// Whether a staircase over path[from..to] runs beside a road at that road's height for <see cref="ParallelPieces"/> or
    /// more walked pieces in a row, away from its own ends (at -5853,5918 a staircase ran parallel to a road, mostly flat pieces:
    /// better to connect further up the road, or avoid running parallel even if it costs POIs). Its own
    /// road outside the stretch (at the ground's height) and the network's roads count.
    /// </summary>
    internal static bool BesideARoad(StairPlan plan, List<Vector2> path, int from, int to, Func<float, float, float> ground)
    {
        Vector2 a = path[from], b = path[to];
        int run = 0;
        foreach (StairPiece piece in plan.Pieces)
        {
            if (piece.Kind != StairPieceKind.Stair && piece.Kind != StairPieceKind.Landing) continue;
            var p = new Vector2(piece.Position.x, piece.Position.z);
            float top = piece.Position.y + (piece.Kind == StairPieceKind.Stair ? StairLayout.PieceRise : StairKit.FloorTopOf(piece.Prefab));
            if (Vector2.Distance(p, a) <= ParallelReach || Vector2.Distance(p, b) <= ParallelReach) { run = 0; continue; }
            bool beside = false;
            for (int i = 1; i < path.Count && !beside; i++)
            {
                if (i > from && i <= to) continue;
                if (DistanceToSegment(p, path[i - 1], path[i]) >= ParallelReach) continue;
                Vector2 q = Vector2.Lerp(path[i - 1], path[i], 0.5f);
                beside = RoadBeside.AtItsHeight(ground(q.x, q.y), top);
            }
            if (!beside) beside = RoadBeside.NearAtItsHeight(p, top);
            run = beside ? run + 1 : 0;
            if (run >= ParallelPieces) return true;
        }
        return false;
    }

    /// <summary>Why a staircase's layout could not be laid (<see cref="Usable"/>).</summary>
    internal enum Refusal { None, NotReached, WaterOrPlace, ExcludedZone, OnItsRoad, OnAnotherStaircase, BesideARoad, AcrossItsRoad }

    /// <summary>The last layout refusal on this thread.</summary>
    [ThreadStatic] private static Refusal t_lastRefusal;
    internal static Refusal LastRefusal { get => t_lastRefusal; private set => t_lastRefusal = value; }

    /// <summary>The path indices of the stretch whose staircase the last failed <see cref="Lay"/>
    /// on this thread could not lay (a merge's whole stretch where it was merging).</summary>
    [ThreadStatic] internal static (int from, int to) LastFailedStretch;

    /// <summary>Pieces this close to where two staircases meet head-on may touch.</summary>
    public const float MeetReach = 3f;
    /// <summary>A road end this close to a walked piece of a laid staircase is on it.</summary>
    public const float OnStairReach = 4f;

    /// <summary>The walking surface at a walked piece's centre.</summary>
    internal static float Surface(StructurePiece piece)
    {
        if (piece.Kind == StructurePieceKind.StairStep) return piece.Position.y + StairLayout.PieceRise / 2f;
        return piece.Position.y + StairKit.FloorTopOf(piece.Prefab);
    }

    /// <summary>Walked pieces closer than this across, and this close in height, are one inside the other.</summary>
    public const float StairOverlap = 1.8f;

    /// <summary>Whether a walked piece lies inside a walked piece of this road's
    /// earlier staircases or of one already in the network.</summary>
    private static bool OverStairs(StairPlan plan, List<RoadStairSpan> before, Vector2? meet)
    {
        var others = new List<Vector3>();
        foreach (RoadStairSpan span in before)
            foreach (StairPiece p in span.Plan.Pieces)
                if (p.Kind != StairPieceKind.Support) others.Add(p.Position);
        // The laid staircases near any of its pieces, read once: asked piece by piece, every
        // candidate layout on every island worker took the shared lock once a piece.
        var walked = new List<Vector2>();
        foreach (StairPiece piece in plan.Pieces)
            if (piece.Kind != StairPieceKind.Support) walked.Add(new Vector2(piece.Position.x, piece.Position.z));
        List<StructurePiece> laid = StairStructures.PiecesNearAny(walked, StairOverlap);
        foreach (StairPiece piece in plan.Pieces)
        {
            if (piece.Kind == StairPieceKind.Support) continue;
            var at = new Vector2(piece.Position.x, piece.Position.z);
            // Where two staircases meet head-on their last pieces touch.
            if (meet.HasValue && Vector2.Distance(at, meet.Value) <= MeetReach) continue;
            foreach (Vector3 o in others)
                if (Vector2.Distance(at, new Vector2(o.x, o.z)) < StairOverlap && Mathf.Abs(o.y - piece.Position.y) < 2f)
                {
                    if (RoadNetworkGenerator.RoadsLog) Describe($"{piece.Kind} at {piece.Position} inside this road's earlier staircase piece at {o} ({before.Count} before, meet {meet})");
                    return true;
                }
            foreach (StructurePiece o in laid)
                if (o.Kind != StructurePieceKind.StairSupport && Mathf.Abs(o.Position.y - piece.Position.y) < 2f
                    && Vector2.Distance(at, new Vector2(o.Position.x, o.Position.z)) < StairOverlap)
                {
                    if (RoadNetworkGenerator.RoadsLog) Describe($"{piece.Kind} at {piece.Position} inside laid staircase piece {o.Kind} at {o.Position}");
                    return true;
                }
        }
        return false;
    }

    /// <summary>
    /// Whether a walked piece lies on the road the staircase joins, away from
    /// the two junctions (a staircase must not go over the road it starts from). At a junction, within the joining step of a
    /// road's end at its level, where a staircase meets its road is the ring's (<see cref="RoadEndRing"/>).
    /// </summary>
    internal static bool OverRoad(StairPlan plan, List<Vector2> path, int from, int to, Func<float, float, float> ground)
    {
        Vector2 a = path[from], b = path[to];
        float ha = ground(a.x, a.y), hb = ground(b.x, b.y);
        // An approach's path[from..to] is the road's end alone: its one end is its foot (or, laid down from the road, its
        // top), tested once.
        bool oneEnd = from == to;
        foreach (StairPiece piece in plan.Pieces)
        {
            if (piece.Kind == StairPieceKind.Support) continue;
            var p = new Vector2(piece.Position.x, piece.Position.z);
            // Its top walking surface: a stair rises a step from its base.
            float top = piece.Position.y + (piece.Kind == StairPieceKind.Stair ? StairLayout.PieceRise : StairKit.FloorTopOf(piece.Prefab));
            // Only the joining step (and its sunk step) may stand on the road, at the foot and at a road staircase's top
            // (an approach's path[from..to] is the road's end alone); near a junction, only at the road's level there.
            float topJoins = to > from ? JoiningStep : JunctionReach;
            float joinsA = !plan.Reversed ? JoiningStep : topJoins,
                joinsB = plan.Reversed ? JoiningStep : topJoins;
            if ((Vector2.Distance(p, a) <= joinsA && Mathf.Abs(top - ha) <= JunctionHeight)
                || (!oneEnd && Vector2.Distance(p, b) <= joinsB && Mathf.Abs(top - hb) <= JunctionHeight)) continue;
            for (int i = 1; i < path.Count; i++)
            {
                if (i > from && i <= to) continue; // the stretch the staircase replaces
                if (DistanceToSegment(p, path[i - 1], path[i]) < OnRoad)
                {
                    if (RoadNetworkGenerator.RoadsLog)
                        Describe($"{piece.Kind} at {p} y {piece.Position.y:F2} on road {path[i - 1]}->{path[i]} (segment {i}, stair {from}..{to} of {path.Count}, ends {a} {ha:F2} {b} {hb:F2})");
                    return true;
                }
            }
            // Nor on a road already in the network: checked against its own road
            // only, a staircase was laid across another (broken stairs, overlapping roads).
            if (RoadSpatialGrid.IsInitialized && RoadSpatialGrid.TryGetRoadWithin(p, OnRoad, out Vector2 other))
            {
                if (RoadNetworkGenerator.RoadsLog) Describe($"{piece.Kind} at {p} on a network road at {other}");
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether a road, its <paramref name="points"/> standing at <paramref name="heights"/>, runs on or under a walked piece
    /// of a staircase standing in the network (<see cref="StairStructures"/>): within <see cref="OnRoad"/> across of the
    /// piece, its walking surface no lower than <see cref="JunctionHeight"/> under the road's. The other half of
    /// <see cref="OverRoad"/>, which holds a staircase off the roads laid before it: this holds a road off the staircases
    /// laid before it. As there, the road may run on under the joining pieces only where it meets the run's end: its points
    /// within <see cref="JoiningStep"/> of the foot or top row, level with it. The search keeps
    /// out of a staircase's cells but for its two end cells (StairStructures.MarkStanding), and a road joining the network at
    /// a staircase's foot from beside it ran on under the flight there (the foot at -6951.1,3360.4: a road laid later joined
    /// at the foot, the staircase standing in its middle and the road running on under its first stairs).
    /// <paramref name="at"/>: the first road point on the flight.
    /// </summary>
    internal static bool OnALaidFlight(IReadOnlyList<Vector2> points, IReadOnlyList<float> heights, out Vector2 at) =>
        OnAFlight(StairStructures.Runs, points, heights, out at) != null;

    /// <summary><see cref="OnALaidFlight"/> against <paramref name="runs"/> too: staircases laid with the road, not yet in
    /// the network.</summary>
    internal static bool OnAFlight(IReadOnlyList<StairRun> runs, IReadOnlyList<Vector2> points, IReadOnlyList<float> heights) =>
        OnALaidFlight(points, heights, out _) || OnAFlight(runs, points, heights, out _) != null;

    /// <summary><see cref="OnALaidFlight"/> against <paramref name="runs"/>: the first run the road is on or under the
    /// flight of, or null.</summary>
    private static StairRun? OnAFlight(IReadOnlyList<StairRun> runs, IReadOnlyList<Vector2> points, IReadOnlyList<float> heights, out Vector2 at)
    {
        at = default;
        if (points.Count == 0) return null;
        float minX = float.PositiveInfinity, minZ = float.PositiveInfinity, maxX = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        foreach (Vector2 q in points)
        {
            minX = Mathf.Min(minX, q.x); maxX = Mathf.Max(maxX, q.x);
            minZ = Mathf.Min(minZ, q.y); maxZ = Mathf.Max(maxZ, q.y);
        }
        foreach (StairRun run in runs)
        {
            var walked = new List<(Vector2 p, float surface)>();
            foreach (StructurePiece piece in run.Pieces)
            {
                if (piece.Kind == StructurePieceKind.StairSupport) continue;
                var p = new Vector2(piece.Position.x, piece.Position.z);
                if (p.x < minX - OnRoad || p.x > maxX + OnRoad || p.y < minZ - OnRoad || p.y > maxZ + OnRoad) continue;
                walked.Add((p, Surface(piece)));
            }
            if (walked.Count == 0) continue;
            // Its foot and top rows: every walked piece level with its lowest and its highest.
            float low = float.PositiveInfinity, high = float.NegativeInfinity;
            foreach (StructurePiece piece in run.Pieces)
                if (piece.Kind != StructurePieceKind.StairSupport) { low = Mathf.Min(low, Surface(piece)); high = Mathf.Max(high, Surface(piece)); }
            var ends = new List<(Vector2 p, float surface)>();
            foreach (StructurePiece piece in run.Pieces)
                if (piece.Kind != StructurePieceKind.StairSupport && (Surface(piece) <= low + 0.1f || Surface(piece) >= high - 0.1f))
                    ends.Add((new Vector2(piece.Position.x, piece.Position.z), Surface(piece)));
            for (int i = 0; i < points.Count; i++)
            {
                Vector2 q = points[i];
                float h = heights[i];
                // Where the road meets the run's end, level with it, it runs on under the joining pieces there.
                if (ends.Exists(e => Vector2.Distance(e.p, q) <= JoiningStep && Mathf.Abs(e.surface - h) <= JunctionHeight)) continue;
                foreach (var (p, surface) in walked)
                    if (Vector2.Distance(q, p) < OnRoad && surface >= h - JunctionHeight)
                    {
                        at = q;
                        return run;
                    }
            }
        }
        return null;
    }

    /// <summary>No walked piece in water or a place's footprint, and no step between two of them through one;
    /// <paramref name="site"/>: the centre of a place whose own footprint it may cross.</summary>
    private static bool Clear(StairPlan plan, WorldGenerator world, Vector2? site = null) =>
        Blocked(plan, world, site, (x, z) => GroundAt(x, z, world)) == "";

    /// <summary>What a walked piece of the plan stands in, if anything: "water at (x, z)" or
    /// "another place at (x, z)" (the refusal says what it hit, and where).</summary>
    /// <param name="ground">The ground the plan is laid on, when known (a placed approach's real
    /// ground): water is judged by it, not by the world generator's height, which at a shore can be
    /// metres lower (TrollCave02 at -1793,6330: refused for "water" 12 m from the cave where
    /// the real ground stood at 31.7, above the sea).</param>
    internal static string Blocked(StairPlan plan, WorldGenerator world, Vector2? site = null, Func<float, float, float>? ground = null)
    {
        Vector2? last = null;
        foreach (StairPiece piece in plan.Pieces)
        {
            if (piece.Kind != StairPieceKind.Stair && piece.Kind != StairPieceKind.Landing) continue;
            var p = new Vector2(piece.Position.x, piece.Position.z);
            if (!RoadWater.BuiltDry(ground != null ? ground(p.x, p.y) : RoadWater.Ground(world, p)))
                return FormattableString.Invariant($"water at ({p.x:F1}, {p.y:F1})");
            if (RoadSiteProtection.BlocksSegment(last ?? p, p, 1f, null, site))
                return FormattableString.Invariant($"another place at ({p.x:F1}, {p.y:F1})");
            last = p;
        }
        return "";
    }
}
