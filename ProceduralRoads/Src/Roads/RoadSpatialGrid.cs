using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BepInEx.Logging;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>A detached observation of the stored records and successful planned-path length.
/// Length includes overlapping commits and excludes unplanned levelled bridge supports.</summary>
public readonly struct RoadStatistics
{
    public int StoredPointRecords { get; }
    public int Cells { get; }
    public float? CommittedPathLengthMeters { get; }
    public bool LengthKnown=>CommittedPathLengthMeters.HasValue;
    public double? CommittedKilometres=>CommittedPathLengthMeters/1000d;
    internal RoadStatistics(int records,int cells,float? length) { StoredPointRecords=records; Cells=cells; CommittedPathLengthMeters=length; }
    public string LengthText(string format="F0",float divisor=1f,string unit="m")=>
        CommittedPathLengthMeters.HasValue ? (CommittedPathLengthMeters.Value/divisor).ToString(format,System.Globalization.CultureInfo.InvariantCulture)+unit : "unknown (legacy save)";
}

/// <summary>
/// Spatial data structure for road points. Provides efficient lookup for road influence at any world position.
/// </summary>
public static partial class RoadSpatialGrid
{
    public struct RoadPoint
    {
        public Vector2 p;
        public float w;
        public float w2;
        public float h;
        /// <summary>Paint only: the terrain is not leveled toward this point
        /// (a waded ford keeps the riverbed as it is).</summary>
        public bool paintOnly;
        // 0 is the original network; positive values identify append operations.
        public int addition;
        /// <summary>Cut only: not road. A staircase's earthwork: the terrain
        /// within w/2 is lowered to h where it stands higher, never raised, never
        /// painted (StairLayout's cuts). Everything that asks where the ROAD is
        /// skips these points, as it skips paint-only ones.</summary>
        public bool cutOnly;
        /// <summary>Into a site: a road's last stretch onto the landing in front of a place
        /// (RoadSiteEntry). The terrain writer levels and paints toward these points inside
        /// a place's footprint too, which it leaves alone for every other point, and only
        /// within <see cref="RoadSiteEntry.InsideReach"/> of them there.</summary>
        public bool intoSite;
        /// <summary>Unpainted: road, levelled like any other point, but never painted. The road a structure stands on past
        /// its stairs' foot (under the stairs and under the deck's end): levelled so the stairs stand on level ground and
        /// no terrain pokes through the deck, while the paint stops at the bottom step. Everything that asks where the
        /// road is counts these points as road.</summary>
        public bool unpainted;
        /// <summary>The island worker that committed it (<see cref="CrossIslandReads.Tag"/>): another island's worker does not
        /// see it. 0 for none. Not saved.</summary>
        public byte island;

        public RoadPoint(Vector2 position, float width, float height, bool paintOnly = false, int addition = 0, bool cutOnly = false, bool intoSite = false,
            bool unpainted = false)
        {
            p = position;
            w = width;
            w2 = width * width;
            h = height;
            this.paintOnly = paintOnly;
            this.addition = addition;
            this.cutOnly = cutOnly;
            this.intoSite = intoSite;
            this.unpainted = unpainted;
            island = 0;
        }

        /// <summary>A point a road runs through: not a ford's paint-only point, not a stair's cut.</summary>
        public bool IsRoad => !paintOnly && !cutOnly;
        /// <summary>What the point is, as the network's identity counts it: road 0, paint-only 1, cut-only 2, into a site 3,
        /// unpainted 4.</summary>
        public int Kind => paintOnly ? 1 : cutOnly ? 2 : intoSite ? 3 : unpainted ? 4 : 0;
    }

    public const float GridSize = RoadConstants.SpatialGridSize;
    public const float DefaultRoadWidth = RoadConstants.DefaultRoadWidth;

    // Every step of every road search reads this grid (is there a road here?), on every worker.
    // Point lookups (TryGetValue) take no lock: in the game's Mono a ReaderWriterLockSlim read
    // takes a spin lock on entry and exit, one shared word for every thread. Writers, and readers
    // that walk the whole grid, still take m_roadCacheLock. A lookup may see a road half merged;
    // islands never share roads and one island's roads are built in turn. Never ask it for Count,
    // IsEmpty, Keys or Values on a hot path: in Mono those take every one of its locks. Keyed with RoadGridComparer: Vector2i hashes x ^ y, a few hundred
    // values for a world's 64 m cells.
    private static ConcurrentDictionary<Vector2i, RoadPoint[]> m_roadPoints = NewCells();
    private static ConcurrentDictionary<Vector2i, RoadPoint[]> NewCells(IEnumerable<KeyValuePair<Vector2i, RoadPoint[]>>? from = null) =>
        from == null ? new(RoadGridComparer.Instance) : new(from, RoadGridComparer.Instance);
    private static ReaderWriterLockSlim m_roadCacheLock = new ReaderWriterLockSlim();
    private static bool m_initialized = false;
    
    private static Dictionary<Vector2, RoadPointDebugInfo> m_debugInfo = new Dictionary<Vector2, RoadPointDebugInfo>();
    private static readonly object m_recordGate = new object();
    internal static object RecordGate => m_recordGate;

    /// <summary>
    /// Keep each road point's smoothing record for road_debug_markers and
    /// road_debug_log (PROCEDURALROADS_ROAD_POINT_DEBUG). Off by default: the
    /// records live until the world unloads and hold an array of window heights
    /// per point, roughly one per metre of road, which on a large network is
    /// hundreds of thousands of objects the garbage collector walks for the
    /// whole session. Without them the commands show the point's height alone,
    /// as they already do for a network loaded from the save.
    /// </summary>
    internal static bool RecordPointDebugInfo = DebugSwitches.Flag("ROAD_POINT_DEBUG", false);
    
#if ROADS_TESTS
    internal static Action? BeforeStatisticsPublication;
    internal static Action? BeforeVersionCalculation;
#endif
    private static int m_storedPointRecords;
    private static float? m_committedPathLength=0f;
    public static RoadStatistics Statistics
    {
        get
        {
            m_roadCacheLock.EnterReadLock();
            try { return new RoadStatistics(m_storedPointRecords,GridCellsWithRoads,m_committedPathLength); }
            finally { m_roadCacheLock.ExitReadLock(); }
        }
    }
    public static int StoredPointRecords=>Statistics.StoredPointRecords;
    public static float? CommittedPathLengthMeters=>Statistics.CommittedPathLengthMeters;
    /// <summary>Compatibility alias: counts stored cell records, including cell copies.</summary>
    public static int TotalRoadPoints=>StoredPointRecords;
    /// <summary>How many cells hold road points: counted as cells are added, never asked of the dictionary
    /// (in the game's Mono its Count takes every one of its locks).</summary>
    public static int GridCellsWithRoads { get; private set; } = 0;

    /// <summary>The narrowest width a point that is not a staircase's cut was filed with by
    /// <see cref="AddRoadPoint"/>, which files a point in every cell whose box lies within its width
    /// (<see cref="InsideRoadGrid"/>), not only in its own. 0 once points came from a save, which
    /// another build may have filed otherwise. Lowered before the points it covers are published.</summary>
    private static float s_narrowestFiled = float.PositiveInfinity;

    private static void NoteFiled(Dictionary<Vector2i, List<RoadPoint>> cells)
    {
        float narrowest = s_narrowestFiled;
        foreach (var points in cells.Values)
            foreach (var point in points)
                if (!point.cutOnly && point.w < narrowest) narrowest = point.w;
        s_narrowestFiled = narrowest;
    }

    /// <summary>How many cells round a query's own cell the ring lookups below must read to see every
    /// road point within <paramref name="reach"/> (none of them reads a staircase's cut). A point lies
    /// in its home cell, at most ceil(reach / GridSize) cells from the query's, and is also filed in
    /// every cell within its width: reach under the narrowest width filed needs the query's cell
    /// alone. A network read from a save reads two more rings, as before.</summary>
    private static int RingRadius(float reach)
    {
        float narrowest = s_narrowestFiled;
        if (narrowest <= 0f) return Mathf.CeilToInt(reach / GridSize) + 1;
        // The margin covers rounding at a cell's border.
        return reach < narrowest - 0.01f ? 0 : Mathf.CeilToInt(reach / GridSize);
    }
    /// <summary>Compatibility alias. NaN means legacy committed length is unknown.
    /// New consumers should use the nullable Statistics projection.</summary>
    public static float TotalRoadLength=>CommittedPathLengthMeters ?? float.NaN;
    internal static double? SnapshotTotalKilometres()=>Statistics.CommittedKilometres;
    
    /// <summary>
    /// Version hash for the current road network. Changes when roads are regenerated.
    /// Used to detect if a zone's terrain mods are stale and need reapplication.
    /// </summary>
    public static int RoadNetworkVersion { get; private set; } = 0;
    
    private static ManualLogSource Log => ProceduralRoadsPlugin.ProceduralRoadsLogger;

    public static bool IsInitialized => m_initialized;

    /// <summary>
    /// Whether the network in memory was read from format 1, the only format a
    /// released build (1.4.x) can read. Such a network is never written back
    /// in a newer format (RoadNetworkGenerator.SaveGlobalRoadData), so the
    /// world still opens in the build that made it.
    /// </summary>
    public static bool LoadedFromReleasedFormat { get; private set; }

    public static void Clear()
    {
        m_roadCacheLock.EnterWriteLock();
        try
        {
            LoadedFromReleasedFormat = false;
            m_roadPoints.Clear();
            s_appendParents.Clear();
            m_initialized = false;
            m_storedPointRecords = 0;
            lock (m_recordGate) Junctions.Clear();
            GridCellsWithRoads = 0;
            s_narrowestFiled = float.PositiveInfinity;
            m_committedPathLength = 0f;
            RoadNetworkVersion = 0;
            lock (m_recordGate) m_debugInfo.Clear();
            System.Threading.Interlocked.Exchange(ref siteRefusalSamples, 0);
        }
        finally
        {
            m_roadCacheLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Try to get debug info for a road point at the given position.
    /// </summary>
    public static bool TryGetDebugInfo(Vector2 position, out RoadPointDebugInfo debugInfo)
    {
        lock (m_recordGate) return m_debugInfo.TryGetValue(position, out debugInfo);
    }

    /// <summary>
    /// A road decided but not yet stored: every dense point and the height it
    /// will carry, already ramped, already inside the grade cap.
    ///
    /// Planning and storing are apart because a road can be refused, and a
    /// caller that lays one road in several pieces - the land either side of
    /// a river crossing, say - has to know that every piece is buildable
    /// before it stores the first. Half a road in the grid is worse than none:
    /// it is a paved stretch that stops in open ground.
    /// </summary>
    public sealed class PlannedPath
    {
        public readonly List<Vector2> Points;
        public readonly List<float> Heights;
        public readonly List<RoadPointDebugInfo> DebugInfos;
        public readonly float Width;
        /// <summary>Per-point widths where a tight switchback narrowed the
        /// road; null when every point is <see cref="Width"/>.</summary>
        public List<float>? Widths;
        public readonly float TotalLength;
        public readonly bool FollowTerrain;
        /// <summary>Where it took the height of a road already laid (<see cref="HoldToNetwork"/>).</summary>
        public List<Junction>? Junctions;
        /// <summary>Per point, whether it is stored unpainted (<see cref="RoadPoint.unpainted"/>); null when every point
        /// is painted.</summary>
        public List<bool>? Unpainted;
        /// <summary>Its start, or its end, is held at a place's ground and ends on a level landing
        /// (<see cref="PlaceEnds"/>, RoadPitches.ApplyJunctionLanding).</summary>
        public bool PlaceLandingStart, PlaceLandingEnd;

        internal PlannedPath(List<Vector2> points, List<float> heights,
            List<RoadPointDebugInfo> debugInfos, float width, float totalLength, bool followTerrain)
        {
            Points = points; Heights = heights; DebugInfos = debugInfos;
            Width = width; TotalLength = totalLength; FollowTerrain = followTerrain;
        }

        /// <summary>Its points <paramref name="from"/> to <paramref name="to"/> (inclusive) as a piece of their own: what a road
        /// keeps of a piece where it rides another road (RoadRide.Own). Its junctions stay with the piece they lie on.</summary>
        internal PlannedPath Slice(int from, int to)
        {
            int n = to - from + 1;
            float length = 0f;
            for (int i = from + 1; i <= to; i++) length += Vector2.Distance(Points[i - 1], Points[i]);
            var slice = new PlannedPath(Points.GetRange(from, n), Heights.GetRange(from, n),
                DebugInfos.Count == Points.Count ? DebugInfos.GetRange(from, n) : new List<RoadPointDebugInfo>(DebugInfos), Width, length, FollowTerrain)
            {
                Widths = Widths != null && Widths.Count == Points.Count ? Widths.GetRange(from, n) : Widths,
                Unpainted = Unpainted != null && Unpainted.Count == Points.Count ? Unpainted.GetRange(from, n) : null,
                PlaceLandingStart = PlaceLandingStart && from == 0, PlaceLandingEnd = PlaceLandingEnd && to == Points.Count - 1,
            };
            if (Junctions != null)
            {
                var kept = Junctions.FindAll(j => slice.Points.Exists(q => (q - j.At).sqrMagnitude < 1f));
                if (kept.Count > 0) slice.Junctions = kept;
            }
            return slice;
        }
    }

    /// <summary>Why the last PlanRoadPath on this thread returned null. The
    /// generation summary counts these, so "could not be built" says which
    /// rule refused.</summary>
    [System.ThreadStatic] public static string? LastRefusal;

    /// <summary>
    /// Metres the road's heights are smoothed over. The road follows the
    /// ground: averaged over a long window every hollow shorter than it was
    /// filled and every bump cut (11.7 % of a measured network stood over 1 m
    /// off the ground at 41 m). At 9 m the road keeps the hill's own shape,
    /// short dips included, and the grade limiter only moves ground where the
    /// rules require; the pitches put their eases on the flattest natural
    /// ground. Settable for tests.
    /// </summary>
    internal static int SmoothWindow = 9;
    /// <summary>Off, the heights are smoothed over the long window the road
    /// always had. Settable for tests.</summary>
    internal static bool FollowGround = true;

    /// <summary>The smoothing window in points.</summary>
    internal static int SmoothingWindow(float width) =>
        FollowGround ? Mathf.Max(1, Mathf.RoundToInt(SmoothWindow / Mathf.Max(0.25f, width / 4f)) | 1) : RoadConstants.HeightSmoothingWindow;

    /// <summary>A joining road aims its end at the height of the road it
    /// joins. Settable for tests.</summary>
    internal static bool JunctionMatch = true;

    /// <summary>A shaped road has only its curves and landings tested for
    /// water and sites (the legs are the route the search already judged).
    /// Off, every dense point of a road with switchbacks is tested. Settable
    /// for tests.</summary>
    internal static bool CurveChecks = true;

    /// <summary>
    /// Work out what a road would be, without storing any of it.
    ///
    /// startGround and endGround are the heights the road has to MEET at its
    /// two ends - the ground a location stands on - where those are known.
    /// Without them each end meets the natural terrain under it.
    ///
    /// followTerrain: paint only; every point keeps the raw terrain height and
    /// the terrain is not leveled toward it at all (a WADED ford). Such a road
    /// is the ground, so there is nothing for the grade cap to hold: it is the
    /// water's bed, and it is crossed, not climbed.
    /// minHeight: no stored point below it (a RAISED ford's surface).
    ///
    /// Returns null when the profile cannot be built inside the grade cap:
    /// the two ends are further apart in height than the cap allows over the
    /// length between them. That is a road too steep to walk, and the
    /// caller's business is to drop it, not to lay it anyway.
    /// </summary>
    public static PlannedPath? PlanRoadPath(List<Vector2> path, float width, WorldGenerator worldGen,
        float? startGround = null, float? endGround = null,
        bool followTerrain = false, float minHeight = float.NegativeInfinity,
        System.Func<Vector2, float>? terrainHeight = null) =>
        PlanRoadPath(path, width, worldGen, startGround, endGround, followTerrain, minHeight, terrainHeight, checkSites: true);

    // Approach selection must be able to score an old route that grazes a
    // site, or it stops before looking for a safe replacement. This profile
    // is comparison-only; the chosen candidate still uses PlanRoadPath.
    internal static PlannedPath? PlanComparisonProfile(List<Vector2> path, float width, WorldGenerator worldGen,
        float? startGround, float? endGround, System.Func<Vector2, float> terrainHeight) =>
        PlanRoadPath(path, width, worldGen, startGround, endGround, false, float.NegativeInfinity, terrainHeight, checkSites: false);

    /// <summary>A road's last stretch onto a place's landing (RoadSiteEntry): every rule of a
    /// road but the site clearance, which it checks itself against the place's shape. Its end on the landing is held at
    /// the place's ground and ends on a level landing (<see cref="PlaceEnds"/>).</summary>
    internal static PlannedPath? PlanSiteEntry(List<Vector2> path, float width, WorldGenerator worldGen,
        float startGround, float endGround, System.Func<Vector2, float> terrainHeight)
    {
        var was = PlaceEnds;
        PlaceEnds = (false, true);
        try { return PlanRoadPath(path, width, worldGen, startGround, endGround, false, float.NegativeInfinity, terrainHeight, checkSites: false); }
        finally { PlaceEnds = was; }
    }

    /// <summary>The ends of the road being planned on this thread that are held at a place's ground (its start, its end):
    /// the road's own ends at a place (RoadNetworkGenerator.PlanPieces, from ApproachGround) and the landing end of the
    /// way onto a placed place's landing (<see cref="PlanSiteEntry"/>). Such an end, where its ground is given, ends on the
    /// level landing a join ends on (RoadPitches.ApplyJunctionLanding), which the pitches treat as a rest; where the climb
    /// then has too little room the road is refused (RoadGrade.Limit).</summary>
    [System.ThreadStatic] internal static (bool start, bool end) PlaceEnds;
    /// <summary>The ends of the road being planned on this thread where a staircase starts (its start, its end): a
    /// staircase's foot the road is held at (RoadNetworkGenerator.PlanPieces), and the road's own end at its place where an
    /// approach staircase may be laid up from it once the road is planned (RoadNetworkGenerator.SiteApproaches, which levels
    /// the road's end there itself where that fits, RoadPitches.LandEnd). A foot held at its height ends on the level landing a join ends on
    /// (RoadPitches.ApplyJunctionLanding): a road's last 8 m at a pitch's grade ran into a staircase's foot. Where that landing
    /// does not fit, the road keeps its own grade into the foot if it stays within the cap there (RoadPitches.PitchedInto),
    /// else it is refused. And no tight switchback narrows the road into either (RoadSwitchbacks.Shape).</summary>
    [System.ThreadStatic] internal static (bool start, bool end) StairStarts;
    /// <summary>Why a road is refused where a staircase foot's landing has no room and a pitch past the cap runs into the foot
    /// (RoadPitches.PitchedInto).</summary>
    internal const string PitchedIntoFootRefusal = "grade: a pitch runs into a staircase's foot with no room for its landing";
    private static int s_placeLandingsStored;
    /// <summary>Road ends held at a place's ground stored with a level landing this generation (<see cref="PlaceEnds"/>).</summary>
    public static int PlaceLandingsStored => s_placeLandingsStored;
    internal static void ResetPlaceLandings() => s_placeLandingsStored = 0;

    private static PlannedPath? PlanRoadPath(List<Vector2> path, float width, WorldGenerator worldGen,
        float? startGround, float? endGround, bool followTerrain, float minHeight,
        System.Func<Vector2, float>? terrainHeight, bool checkSites)
    {
        var plan = PlanRoadPathShaped(path, width, worldGen, startGround, endGround, followTerrain, minHeight, terrainHeight);
        if (checkSites) plan = CheckSiteClearance(plan, path);
        // The sway is cosmetic: the road is planned without it too, and the
        // swayed plan is kept only if it was built and moves no more earth.
        if (RoadWiggle.Enabled && !followTerrain && !SuppressWiggle)
        {
            string? why = LastRefusal;
            Vector2? refusedAt = RefusedAt;
            PlannedPath? plain;
            SuppressWiggle = true;
            try
            {
                plain = PlanRoadPathShaped(path, width, worldGen, startGround, endGround, followTerrain, minHeight, terrainHeight);
                if (checkSites) plain = CheckSiteClearance(plain, path);
            }
            finally { SuppressWiggle = false; }
            float Ground(Vector2 q) => terrainHeight != null ? terrainHeight(q) : BiomeBlendedHeight.GetBlendedHeight(q.x, q.y, worldGen);
            string? plainWhy = LastRefusal;
            bool refused = plan == null, moreEarth = !refused && plain != null && !RoadWiggle.NoMoreEarth(plan!.Points, plan.Heights, plain.Points, plain.Heights, Ground);
            // A swayed plan refused or moving more earth somewhere loses its sway only there: it is planned
            // again with the sway held round the stretch at fault (where it moved more earth, where it was
            // refused, or else over one half of it, then the other), up to HoldRounds times.
            List<Vector2>? held = null, beforeHalves = null;
            int halves = 0;
            for (int round = 0; plain != null && (refused || moreEarth) && round < RoadWiggle.HoldRounds; round++)
            {
                var next = new List<Vector2>();
                if (moreEarth)
                {
                    if (held != null) next.AddRange(held);
                    next.AddRange(RoadWiggle.MoreEarthAt(plan!.Points, plan.Heights, plain.Points, plain.Heights, Ground));
                }
                else if (halves == 0 && refusedAt is Vector2 at && (held == null || !held.Contains(at)))
                {
                    if (held != null) next.AddRange(held);
                    next.Add(at);
                }
                else if (halves < 2)
                {
                    if (halves == 0) beforeHalves = held;
                    if (beforeHalves != null) next.AddRange(beforeHalves);
                    next.AddRange(RoadWiggle.HalfOf(path, second: halves == 1));
                    halves++;
                }
                else break;
                // Nothing new to hold (where the swayed plan moves more earth, none of it is deeper than the plain plan's
                // deepest): planned again it is the same sway.
                if (next.Count == 0 || (held != null && next.Count == held.Count)) break;
                held = next;
                PlannedPath? again;
                RoadWiggle.HeldAt = held;
                try
                {
                    again = PlanRoadPathShaped(path, width, worldGen, startGround, endGround, followTerrain, minHeight, terrainHeight);
                    refusedAt = RefusedAt;
                    if (checkSites) { again = CheckSiteClearance(again, path); if (again == null) refusedAt = RefusedAt; }
                }
                finally { RoadWiggle.HeldAt = null; }
                plan = again;
                refused = plan == null;
                moreEarth = !refused && !RoadWiggle.NoMoreEarth(plan!.Points, plan.Heights, plain.Points, plain.Heights, Ground);
            }
            if (refused) { System.Threading.Interlocked.Increment(ref SwayRefused); plan = plain; LastRefusal = plainWhy; }
            else if (moreEarth) { System.Threading.Interlocked.Increment(ref SwayMoreEarth); plan = plain; LastRefusal = plainWhy; }
            else
            {
                System.Threading.Interlocked.Increment(ref SwayKept);
                if (held != null) System.Threading.Interlocked.Increment(ref SwayKeptHeld);
                LastRefusal = why;
                if (plain != null) CountSlopeSway(plan!.Points, plain.Points, Ground);
            }
        }
        return plan;
    }

    private static int siteRefusalSamples;

    private static PlannedPath? CheckSiteClearance(PlannedPath? plan, List<Vector2> input)
    {
        if (plan == null) return null;
        // Preserve legitimate trimmed arrivals at their site's clearance
        // edge. Only the input endpoints can grant that margin allowance:
        // snapping must not turn a third site into an exempt destination.
        Vector2 start = input[0], end = input[input.Count - 1];
        for (int i = 1; i < plan.Points.Count; i++)
        {
            float width = plan.Widths != null ? Mathf.Max(plan.Widths[i - 1], plan.Widths[i]) : plan.Width;
            // The same owner and width every shaping step asked (RoadSiteProtection.BlocksRoad), with the site it hit.
            if (RoadSiteProtection.BlocksFinalSegment(plan.Points[i - 1], plan.Points[i], RoadSiteProtection.Clearance(width), start, end, out var hit))
            {
                LastRefusal = "final road crosses a protected site";
                RefusedAt = (plan.Points[i - 1] + plan.Points[i]) * 0.5f;
                string site = hit.HasValue
                    ? System.FormattableString.Invariant($"site=({hit.Value.Centre.x:F2},{hit.Value.Centre.y:F2}) radius={hit.Value.Radius:F2}")
                    : "site=index unavailable";
                string detail = System.FormattableString.Invariant(
                    $"POI clearance refused trial: segment=({plan.Points[i - 1].x:F2},{plan.Points[i - 1].y:F2})->({plan.Points[i].x:F2},{plan.Points[i].y:F2}); {site}; width={width:F2}; input=({start.x:F2},{start.y:F2})->({end.x:F2},{end.y:F2}). Trial refusal, not necessarily a lost road.");
                // Keep ordinary server logs useful without printing thousands
                // of rejected sway/approach trials. Debug retains every one.
                if (System.Threading.Interlocked.Increment(ref siteRefusalSamples) <= 20) Log.LogInfo(detail);
                else Log.LogDebug(detail);
                return null;
            }
        }
        return plan;
    }

    [System.ThreadStatic] internal static bool SuppressWiggle;

    /// <summary>Slope-sway metric per generation: kept swayed plans with some steep stretch
    /// where the road still leaves its plain line, and the metres of those stretches (ground
    /// grade over <see cref="RoadWiggle.Flat"/>, more than 1 m from the plain line), by grade
    /// band: 0.12-0.20, 0.20-0.28, over 0.28.</summary>
    internal static int SlopeSwayPlans;
    internal static readonly long[] SlopeSwayMillimetres = new long[3];

    private static void CountSlopeSway(IList<Vector2> swayed, IList<Vector2> plain, System.Func<Vector2, float> ground)
    {
        if (plain.Count < 2 || swayed.Count < 3) return;
        var metres = new float[3];
        for (int i = 1; i < swayed.Count - 1; i++)
        {
            float run = Vector2.Distance(swayed[i - 1], swayed[i + 1]);
            if (run < 1f) continue;
            float grade = Mathf.Abs(ground(swayed[i + 1]) - ground(swayed[i - 1])) / run;
            if (grade <= RoadWiggle.Flat) continue;
            float best = float.MaxValue;
            for (int j = 1; j < plain.Count; j++)
            {
                Vector2 a = plain[j - 1], ab = plain[j] - a;
                float l2 = ab.sqrMagnitude;
                float t = l2 > 0f ? Mathf.Clamp01(Vector2.Dot(swayed[i] - a, ab) / l2) : 0f;
                best = Mathf.Min(best, Vector2.Distance(swayed[i], a + ab * t));
            }
            if (best <= 1f) continue;
            metres[grade < 0.20f ? 0 : grade < 0.28f ? 1 : 2] += run * 0.5f;
        }
        if (metres[0] + metres[1] + metres[2] <= 0f) return;
        System.Threading.Interlocked.Increment(ref SlopeSwayPlans);
        for (int b = 0; b < 3; b++) System.Threading.Interlocked.Add(ref SlopeSwayMillimetres[b], (long)(metres[b] * 1000f));
    }
    /// <summary>Waypoints the road being planned must keep where they are, on this thread: the foot of a structure's end
    /// stairs and its apron (RoadNetworkGenerator.PlanPieces, StructureEnds.MeetEnds), which the pull would
    /// otherwise skip and the sway's resampling cut past, leaving the foot off the road. The sway keeps them where they
    /// are and is held round them (RoadWiggle.Apply).</summary>
    [System.ThreadStatic] internal static List<Vector2>? HeldPoints;
    /// <summary>A road end at a structure's end, on this thread (StructureEnds, the one owner of how a structure meets the
    /// road): a piece of road ending within half a metre of one is held at its height over its last
    /// <see cref="StructureEnds.ApronLength"/>, the earthwork levelling the road into the structure.</summary>
    [System.ThreadStatic] internal static List<(Vector2 at, float height)>? LevelledEnds;
    /// <summary>The refusal of a piece whose levelled stretch to a structure's end reaches its other end, held at another
    /// height (<see cref="LevelledEnds"/>).</summary>
    internal const string LevelledOverHeldEnd = "a structure's levelled end reaches a held end at another height";
    /// <summary>Sway outcomes per planned road piece this generation (candidates included):
    /// kept (of them, kept with the sway held round the stretches at fault), dropped because the swayed
    /// plan was refused, dropped for moving more earth.</summary>
    internal static int SwayKept, SwayRefused, SwayMoreEarth, SwayKeptHeld;
    /// <summary>Where the last plan on this thread was refused, where the check that refused it says: a protected
    /// site's clearance or a switchback's turns. Null otherwise.</summary>
    [System.ThreadStatic] internal static Vector2? RefusedAt;

    /// <summary>How a planned road's bends were rounded where its full arcs were refused: with the least arcs
    /// (<see cref="RoadSwitchbacks.LeastBends"/>) or not at all; per refusal of the full arcs. Trial plans included.</summary>
    internal enum BendStage { Least, Corners }
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<(BendStage, string), int> BendFallbacks = new();
    private static void Shaped(BendStage stage, string? refusedFor) =>
        BendFallbacks.AddOrUpdate((stage, refusedFor ?? "other"), 1, (_, n) => n + 1);

    /// <summary>The ends of a piece of <paramref name="points"/> (<paramref name="total"/> long) that are levelled
    /// (<see cref="LevelledEnds"/>), each whether at its start and its height: the one reading of them, for the levelling and
    /// for putting it back once the grade is limited, so both hold the same height. A piece that is one level landing between
    /// two held ends (StructureEnds.Share) is levelled at their common height all along, as the landing was laid, not at the
    /// end levelled last: levelled at both ends whose levelled stretches overlap, or at one end with the whole piece within
    /// its stretch and its other end held (<paramref name="startGround"/>, <paramref name="endGround"/>: a staircase's foot,
    /// a place's ground, a join), where the two share it (StructureEnds.SharesLanding, each within a walker's step of it) and
    /// the held end is more than a step off the structure's (within a step the stretch stays at the structure's end's height).
    /// Further apart each keeps its own height, and the piece is refused (<see cref="LevelledOverHeldEnd"/>).</summary>
    private static List<(bool atStart, float height)> LevelledOn(List<(Vector2 at, float height)> ends, List<Vector2> points, float total,
        float? startGround, float? endGround)
    {
        var on = new List<(bool atStart, float height)>();
        foreach (var (at, height) in ends)
        {
            bool atStart = (points[0] - at).sqrMagnitude < 0.25f, atEnd = (points[points.Count - 1] - at).sqrMagnitude < 0.25f;
            if (atStart || atEnd) on.Add((atStart, height));
        }
        int s = on.FindIndex(e => e.atStart), e = on.FindIndex(x => !x.atStart);
        if (s >= 0 && e >= 0 && total <= 2f * (StructureEnds.ApronLength + StructureEnds.JoinOverlap) + 1e-3f
            && StructureEnds.SharesLanding(on[s].height, on[e].height))
        {
            float common = StructureEnds.LandingHeight(on[s].height, on[e].height);
            on[s] = (true, common);
            on[e] = (false, common);
        }
        else if ((s >= 0) != (e >= 0) && total <= StructureEnds.ApronLength + StructureEnds.JoinOverlap + 1e-3f)
        {
            int k = s >= 0 ? s : e;
            if ((on[k].atStart ? endGround : startGround) is float held && !StairLayout.Walkable(on[k].height, held)
                && StructureEnds.SharesLanding(on[k].height, held))
                on[k] = (on[k].atStart, StructureEnds.LandingHeight(on[k].height, held));
        }
        return on;
    }

    /// <summary>Whether the levelled stretches of a piece's ends (<see cref="LevelledOn"/>) take an end of it held at another
    /// height: a stretch over the whole piece whose other end is held (<paramref name="startGround"/>,
    /// <paramref name="endGround"/>) more than a walker's step (StairLayout.Walkable) off it, or two stretches that overlap at
    /// heights they cannot share. The two cannot both be met level, and the piece is refused.</summary>
    private static bool LevelledOverHeld(List<(bool atStart, float height)> on, float total, float? startGround, float? endGround, float minHeight)
    {
        int s = on.FindIndex(x => x.atStart), e = on.FindIndex(x => !x.atStart);
        if (s >= 0 && e >= 0 && total <= 2f * (StructureEnds.ApronLength + StructureEnds.JoinOverlap) + 1e-3f)
            return !StairLayout.Walkable(on[s].height, on[e].height);
        foreach (var (atStart, height) in on)
            if (total <= StructureEnds.ApronLength + StructureEnds.JoinOverlap + 1e-3f && (atStart ? endGround : startGround) is float other
                && !StairLayout.Walkable(other, Mathf.Max(height, minHeight)))
                return true;
        return false;
    }

    /// <summary>How near a road already laid a planned road's end is joined to it, its end aimed at that road's height
    /// (<see cref="JunctionMatch"/>).</summary>
    private static float JoinReach(float width) => width * 0.75f;

    /// <summary>
    /// The height a road planned along <paramref name="path"/> from <paramref name="startGround"/> stores at its first point,
    /// where nothing but that value decides it: no road already laid within <see cref="RoadProfile.Footprint(float)"/> of it (so it is neither
    /// joined, <see cref="JoinReach"/>, nor held to one), and no structure's end levelled (<see cref="LevelledEnds"/>), no
    /// waypoint held (<see cref="HeldPoints"/>) and no place's landing (<see cref="PlaceEnds"/>) on this thread. Every
    /// shaping step keeps the first point where it is, and the grade limiter holds its height. Null where any of them could
    /// move it.
    /// </summary>
    internal static float? FirstHeight(List<Vector2> path, float width, float startGround)
    {
        if (path.Count < 2 || LevelledEnds != null || HeldPoints != null || PlaceEnds != default) return null;
        if (m_initialized && TryGetRoadHeightWithin(path[0], RoadProfile.Footprint(width), out _)) return null;
        return startGround;
    }

    private static PlannedPath? PlanRoadPathShaped(List<Vector2> path, float width, WorldGenerator worldGen,
        float? startGround, float? endGround, bool followTerrain, float minHeight,
        System.Func<Vector2, float>? terrainHeight)
    {
        // A tight staircase switchback that cannot keep its legs off each
        // other's surface falls back to the ordinary turn, so the road is
        // not lost to the tighter geometry.
        bool tight = RoadSwitchbacks.TightSwitchbacks && !followTerrain;
        RoadSwitchbacks.LastFilleted = 0;
        var plan = PlanRoadPathCore(path, width, worldGen, tight, startGround, endGround, followTerrain, minHeight, terrainHeight);
        if (plan == null && tight && LastRefusal == SwitchbackRefusal.LegsTooClose)
            plan = PlanRoadPathCore(path, width, worldGen, false, startGround, endGround, followTerrain, minHeight, terrainHeight);
        // A minimum-radius fillet is cosmetic too: a road it fails keeps its ordinary bend arcs before it loses them all.
        if (plan == null && RoadSwitchbacks.LastFilleted > 0 && !followTerrain)
        {
            RoadSwitchbacks.SuppressFillets = true;
            try
            {
                plan = PlanRoadPathCore(path, width, worldGen, tight, startGround, endGround, followTerrain, minHeight, terrainHeight);
                if (plan == null && tight && LastRefusal == SwitchbackRefusal.LegsTooClose)
                    plan = PlanRoadPathCore(path, width, worldGen, false, startGround, endGround, followTerrain, minHeight, terrainHeight);
            }
            finally { RoadSwitchbacks.SuppressFillets = false; }
        }
        // Rounded bends are cosmetic: a road whose arcs fail a water, site or
        // turn-room check keeps its corners rather than being lost -- after
        // first trying the least arcs, which leave its corners by centimetres.
        if (plan == null && RoadSwitchbacks.Fallback && !followTerrain &&
            (LastRefusal == SwitchbackRefusal.CurveInWater || LastRefusal == SwitchbackRefusal.CurveCrossesSite))
        {
            // No switchback either: an arc rounding the same U fails the same water check.
            string why = LastRefusal;
            RoadSwitchbacks.SuppressSwitchbacks = true;
            try
            {
                foreach (bool corners in new[] { false, true })
                {
                    if (!corners && !(RoadSwitchbacks.BendRadius > 0f)) continue;
                    RoadSwitchbacks.SuppressBends = corners;
                    RoadSwitchbacks.LeastBends = !corners;
                    plan = PlanRoadPathCore(path, width, worldGen, false, startGround, endGround, followTerrain, minHeight, terrainHeight);
                    if (plan != null && !RoadSwitchbacks.Separated(plan.Points, plan.Heights, width))
                    { plan = null; LastRefusal = why; }
                    else if (plan == null) LastRefusal = why;
                    if (plan != null) { Shaped(corners ? BendStage.Corners : BendStage.Least, why); break; }
                }
            }
            finally { RoadSwitchbacks.SuppressSwitchbacks = false; RoadSwitchbacks.SuppressBends = false; RoadSwitchbacks.LeastBends = false; }
        }
        // Refused with its arcs for any other reason (most often the profile: arcs shorten the road), the road is
        // planned with the least arcs, then with its corners.
        if (plan == null && RoadSwitchbacks.BendRadius > 0f && !followTerrain)
        {
            string? why = LastRefusal;
            try
            {
                foreach (bool corners in new[] { false, true })
                {
                    RoadSwitchbacks.SuppressBends = corners;
                    RoadSwitchbacks.LeastBends = !corners;
                    plan = PlanRoadPathCore(path, width, worldGen, tight, startGround, endGround, followTerrain, minHeight, terrainHeight);
                    if (plan == null && tight && LastRefusal == SwitchbackRefusal.LegsTooClose)
                        plan = PlanRoadPathCore(path, width, worldGen, false, startGround, endGround, followTerrain, minHeight, terrainHeight);
                    if (plan != null) { Shaped(corners ? BendStage.Corners : BendStage.Least, why); break; }
                }
            }
            finally { RoadSwitchbacks.SuppressBends = false; RoadSwitchbacks.LeastBends = false; }
        }
        return plan;
    }

    private static PlannedPath? PlanRoadPathCore(List<Vector2> path, float width, WorldGenerator worldGen, bool tightTurns,
        float? startGround, float? endGround, bool followTerrain, float minHeight,
        System.Func<Vector2, float>? terrainHeight)
    {
        LastRefusal = null; RefusedAt = null;
        if (path == null || path.Count < 2 || worldGen == null)
        { LastRefusal = "no path"; return null; }

        // A road joining another aims its end at the height of the road it
        // joins, not at the natural ground: blending with existing roads only
        // happens when most of the new road overlaps them, so a road joining
        // another on fill arrived metres low and the terrain blend built a
        // step between them.
        List<Junction>? junctions = null;
        bool joinedStart = false, joinedEnd = false;
        if (JunctionMatch && !followTerrain && path.Count >= 2)
        {
            float reach = JoinReach(width);
            if (TryGetRoadHeightWithin(path[0], reach, out float joinStart)) { startGround = joinStart; joinedStart = true; }
            if (TryGetRoadHeightWithin(path[path.Count - 1], reach, out float joinEnd)) { endGround = joinEnd; joinedEnd = true; }
        }

        // Trial profiles share procedural samples, never authored location ground.
        float SampleTerrain(Vector2 point) => terrainHeight != null ? terrainHeight(point)
            : BiomeBlendedHeight.GetBlendedHeight(point.x, point.y, worldGen);
        float segmentLength = width / 4f;

        float totalLength = 0f;
        for (int i = 0; i < path.Count - 1; i++)
            totalLength += Vector2.Distance(path[i], path[i + 1]);

        // A held waypoint (a viaduct's end-stair foot, HeldPoints) is never pulled past, as one snapped onto a road.
        List<Vector2>? held = HeldPoints;
        bool Held(Vector2 p) => held != null && held.Exists(h => (h - p).sqrMagnitude < 0.25f);
        bool holds = held != null && path.Exists(Held);
        // The ends the road is planned between: every shaping step below keeps the line clear of protected sites as the
        // final check (CheckSiteClearance) will judge it, against these ends and the road's width (RoadSiteProtection.BlocksRoad).
        Vector2 planStart = path[0], planEnd = path[path.Count - 1];
        if (!followTerrain && RoadPathPull.Enabled)
            path = RoadPathPull.Pull(path, width, SampleTerrain, p => !RoadWater.BuiltDry(worldGen, p), RoadGrade.Configured + RoadConstants.SearchGradeMargin,
            anchor: RoadNetworkGenerator.RoadSnap > 0f ? p => TryGetRoadWithin(p, 0.5f, out _) || Held(p)
                : holds ? Held : (System.Func<Vector2, bool>?)null);

        // A held waypoint stays where it is through the sway's resampling, the sway held round it (a piece meeting a
        // structure's end sways everywhere else: unswayed, a road from a crossing ran dead straight for its whole piece).
        if (!followTerrain && RoadWiggle.Enabled && !SuppressWiggle)
            path = RoadWiggle.Apply(path, SampleTerrain, p => !RoadWater.BuiltDry(worldGen, p), p => worldGen.GetBiome(p.x, p.y), width, p => TryGetRoadWithin(p, width + RoadConstants.TerrainBlendMargin, out _), holds ? path.FindAll(Held) : null);

        // Order: the search's path was snapped onto existing road in
        // GenerateRoad; the pull above keeps every snapped waypoint; the sway
        // leaves shared road alone. A last snap catches what the site-approach
        // smoothing and the sway's resampling moved back off (measured: 126 m
        // of road beside another without it, 52 m with it).
        // A snap that would bring the line into a site's clearance is not made.
        if (!followTerrain)
        {
            var snapped = RoadNetworkGenerator.SnapToNetwork(path, width, holds ? Held : null)!;
            if (RoadSiteProtection.FirstBlockedLeg(snapped, width, null, planStart, planEnd) < 0) path = snapped;
        }

        List<Vector2>? turns = null; List<bool>? landings = null; List<float>? shapeWidths = null;
        // The fillets never drop a held waypoint, nor one on a road already laid (the junction), though they round it.
        bool shapedOk = followTerrain || RoadSwitchbacks.Shape(path, width, out turns, out landings, SampleTerrain, tight: tightTurns,
            held: holds ? Held : null, onRoad: RoadNetworkGenerator.RoadSnap > 0f ? p => TryGetRoadWithin(p, 0.5f, out _) : null,
            stairStarts: StairStarts);
        if (!followTerrain) shapeWidths = RoadSwitchbacks.LastWidths;
        if (!shapedOk)
        { LastRefusal = RoadSwitchbacks.LastRefusal ?? SwitchbackRefusal.NoTurningRoom; RefusedAt = RoadSwitchbacks.LastRefusalAt; Log.LogDebug($"Road refused: {LastRefusal}"); return null; }
        List<Vector2> densePoints = turns ?? SplinePath(path, segmentLength);
        // The spline swings a little past its waypoints' legs; where that would take it into a site's clearance the
        // road keeps to the legs themselves, which the steps above kept clear.
        if (turns == null && !followTerrain && RoadSiteProtection.FirstBlockedLeg(densePoints, width, null, planStart, planEnd) >= 0)
            densePoints = Densify(path, segmentLength);
        if (turns != null)
        {
            totalLength = 0f;
            // Check new curves and landings early, including their water
            // clearance. The completed buildable plan gets a separate POI
            // check on every leg, since snapping can move ordinary legs too.
            // A road rounded only for its bends checks only arcs here.
            bool hasSwitchback = landings != null && landings.Contains(true);
            var curve = RoadSwitchbacks.LastCurve;
            bool curvesOnly = (CurveChecks || !hasSwitchback) && landings != null;
            for (int i=1;i<densePoints.Count;i++)
            {
                totalLength += Vector2.Distance(densePoints[i-1],densePoints[i]);
                if (curvesOnly && !landings![i] && !landings[i-1] && !(curve != null && i < curve.Count && (curve[i] || curve[i-1]))) continue;
                // The final check's own clearance (its width and ends): a fillet into a site's clearance is refused here,
                // where the road is planned again with the least arcs or its corners (PlanRoadPathShaped), not at the end.
                float legWidth = shapeWidths != null && shapeWidths.Count == densePoints.Count ? Mathf.Max(shapeWidths[i-1],shapeWidths[i]) : width;
                if (RoadSiteProtection.BlocksRoad(densePoints[i-1],densePoints[i],legWidth,planStart,planEnd))
                {
                    LastRefusal = SwitchbackRefusal.CurveCrossesSite;
                    return null;
                }
                if (!RoadWater.BuiltDry(worldGen, densePoints[i]))
                {
                    LastRefusal = SwitchbackRefusal.CurveInWater;
                    return null;
                }
            }
        }
        float[]? edgeGrades = null;
        bool landStart = false, landEnd = false, placeStart = false, placeEnd = false;
        if (landings != null)
        {
            edgeGrades = new float[densePoints.Count];
            for(int i=0;i<edgeGrades.Length;i++) edgeGrades[i] = landings[i] || (i>0 && landings[i-1])
                ? Mathf.Min(RoadGrade.Configured,RoadSwitchbacks.EffectiveLandingGrade()) : RoadGrade.Configured;
        }
        List<float> denseHeights = new List<float>(densePoints.Count);

        foreach (var point in densePoints)
            denseHeights.Add(SampleTerrain(point));

        List<float> smoothedHeights = SmoothHeights(denseHeights, followTerrain ? RoadConstants.HeightSmoothingWindow : SmoothingWindow(width), out var debugInfos);

        int overlapCount = DetectOverlap(densePoints, width);
        if (overlapCount > densePoints.Count * RoadConstants.OverlapThreshold)
        {
            Log.LogDebug($"Road path overlaps with existing roads ({overlapCount}/{densePoints.Count} points), blending heights");
            BlendWithExistingRoads(densePoints, smoothedHeights, width);
        }

        Log.LogDebug($"Road path: {path.Count} waypoints -> {densePoints.Count} dense points");
        Log.LogDebug($"  Path length: {totalLength:F0}m, smoothing window: {RoadConstants.HeightSmoothingWindow} points");
        Log.LogDebug($"  Overlap: {overlapCount}/{densePoints.Count} points overlap existing roads");

        // Endpoint ramps: near each end of the road the final height blends
        // from the ground the end has to meet toward the smoothed road
        // height, so roads meet locations and terrain without a smoothed ledge.
        float[] distanceFromStart = new float[densePoints.Count];
        for (int i = 1; i < densePoints.Count; i++)
            distanceFromStart[i] = distanceFromStart[i - 1] + Vector2.Distance(densePoints[i - 1], densePoints[i]);
        float pathTotal = densePoints.Count > 0 ? distanceFromStart[densePoints.Count - 1] : 0f;

        List<float> finalHeights = new List<float>(densePoints.Count);
        for (int i = 0; i < densePoints.Count; i++)
        {
            float fromStart = distanceFromStart[i];
            float fromEnd = pathTotal - fromStart;
            float distFromNearestEnd = Mathf.Min(fromStart, fromEnd);
            float? target = fromStart <= fromEnd ? startGround : endGround;
            float rampBase = RoadEndpointRamp.BaseHeight(denseHeights[i], target, distFromNearestEnd);
            finalHeights.Add(followTerrain
                ? denseHeights[i]
                : Mathf.Max(Mathf.Lerp(rampBase, smoothedHeights[i], RoadEndpointRamp.Blend(distFromNearestEnd)), minHeight));
        }

        // A structure's end (StructureEnds): the road's last ApronLength to it is level at its height.
        if (!followTerrain && LevelledEnds is { } levelledEnds && densePoints.Count > 1)
        {
            var on = LevelledOn(levelledEnds, densePoints, pathTotal, startGround, endGround);
            // The levelled stretch never takes the piece's other end where that end is held at another height (a
            // staircase's foot, another structure's end) that cannot share its landing (StructureEnds.SharesLanding): the two
            // cannot both be met, and the piece is refused.
            if (LevelledOverHeld(on, pathTotal, startGround, endGround, minHeight))
            {
                LastRefusal = LevelledOverHeldEnd;
                Log.LogDebug("Road refused: a structure's levelled end reaches a held end at another height");
                return null;
            }
            foreach (var (atStart, height) in on)
                for (int i = 0; i < densePoints.Count; i++)
                {
                    float from = atStart ? distanceFromStart[i] : pathTotal - distanceFromStart[i];
                    if (from <= StructureEnds.ApronLength + StructureEnds.JoinOverlap + 1e-3f) finalHeights[i] = Mathf.Max(height, minHeight);
                }
        }

        // Smoothing and the ramp both move heights after the search priced the
        // ground, so a route the search accepted can still be built too steep.
        // This is where that is caught, and the ends are held: they are the
        // heights the road has to meet. A waded ford is exempt: it is painted
        // on the ground rather than built, so its profile is the river bed's
        // and holding it to a road's grade would mean levelling the river.
        if (!followTerrain)
        {
            float steepestBefore = RoadGrade.SteepestStep(densePoints, finalHeights);
            // A join's end, an end held at a place's ground (PlaceEnds) and one held at a staircase's foot (StairStarts) end on a
            // level landing; a foot's, only where it fits (below).
            placeStart = PlaceEnds.start && startGround.HasValue;
            placeEnd = PlaceEnds.end && endGround.HasValue;
            landStart = joinedStart || placeStart || (StairStarts.start && startGround.HasValue);
            landEnd = joinedEnd || placeEnd || (StairStarts.end && endGround.HasValue);
            // Only a staircase's foot asks for the landing at an end (not a join's, nor a place's): where the landing does not
            // fit, the road there is planned as before it, its own grade within the cap all the way to the foot
            // (RoadPitches.PitchedInto, the end audit's rule), else refused; an approach's road end is left so too
            // (RoadPitches.LandEnd). A foot's landing that fits is kept where the other's does not.
            bool stairOnlyStart = landStart && !joinedStart && !placeStart, stairOnlyEnd = landEnd && !joinedEnd && !placeEnd;
            bool fallback = RoadPitches.JunctionLanding > 0f && (stairOnlyStart || stairOnlyEnd);
            float[]? switchbackGrades = fallback && edgeGrades != null ? (float[])edgeGrades.Clone() : null;

            float[]? Profile(bool atStart, bool atEnd, float[]? grades)
            {
                if (RoadPitches.JunctionLanding > 0f && (atStart || atEnd))
                    grades = RoadPitches.ApplyJunctionLanding(densePoints, grades, RoadGrade.Configured, atStart, atEnd, RoadPitches.JunctionLanding);
                if (RoadPitches.Enabled)
                {
                    // Place the pitches on the profile the road will actually have at the cap, not on
                    // the ground-following one: a climb over 50 % ground is spread to the cap by the
                    // limiter, so judging its room on the raw rise dropped eases that fit.
                    var capped = new List<float>(finalHeights);
                    var basis = RoadGrade.Limit(densePoints, capped, RoadGrade.Configured, grades) ? capped : finalHeights;
                    grades = RoadPitches.PlacePitches(densePoints, basis, RoadGrade.Configured, grades,
                        ground: FollowGround ? denseHeights : null) ?? grades;
                }
                return grades;
            }
            edgeGrades = Profile(landStart, landEnd, edgeGrades);
            bool limited = RoadGrade.Limit(densePoints, finalHeights, RoadPitches.LimitCap(RoadGrade.Configured, edgeGrades), edgeGrades);
            bool pitchedIntoFoot = false;
            if (!limited && fallback)
            {
                // A failed limit leaves the profile as it was (RoadGrade.Limit).
                var unlimited = new List<float>(finalHeights);
                var tries = new List<(bool start, bool end)>();
                if (stairOnlyStart && stairOnlyEnd) { tries.Add((true, false)); tries.Add((false, true)); }
                tries.Add((landStart && !stairOnlyStart, landEnd && !stairOnlyEnd));
                foreach (var (keepStart, keepEnd) in tries)
                {
                    for (int i = 0; i < finalHeights.Count; i++) finalHeights[i] = unlimited[i];
                    float[]? grades = Profile(keepStart, keepEnd, switchbackGrades == null ? null : (float[])switchbackGrades.Clone());
                    if (!RoadGrade.Limit(densePoints, finalHeights, RoadPitches.LimitCap(RoadGrade.Configured, grades), grades)) continue;
                    if (!RoadPitches.PitchedInto(densePoints, finalHeights, stairOnlyStart && !keepStart, stairOnlyEnd && !keepEnd, RoadGrade.Configured))
                    {
                        (edgeGrades, limited) = (grades, true);
                        break;
                    }
                    pitchedIntoFoot = true;
                }
                if (!limited) for (int i = 0; i < finalHeights.Count; i++) finalHeights[i] = unlimited[i];
            }
            if (!limited && pitchedIntoFoot)
            {
                Log.LogDebug($"Road profile refused: no room for a staircase foot's landing, and a pitch past the {RoadGrade.Configured:P0} cap runs into the foot");
                LastRefusal = PitchedIntoFootRefusal;
                return null;
            }
            if (!limited)
            {
                Log.LogDebug(
                    $"Road profile refused: ends {finalHeights[0]:F1}m and {finalHeights[finalHeights.Count - 1]:F1}m " +
                    $"are {Mathf.Abs(finalHeights[finalHeights.Count - 1] - finalHeights[0]):F1}m apart over {pathTotal:F0}m, " +
                    $"over the {RoadGrade.Configured:P0} cap including turn landings");
                LastRefusal = landings != null ? "grade: ends too far apart once turn landings are flattened" : "grade: ends too far apart for the cap";
                return null;
            }
            float steepestAfter = RoadGrade.SteepestStep(densePoints, finalHeights);
            RoadGrade.RecordSteepest(steepestAfter);
            if (steepestAfter < steepestBefore - 0.001f)
                Log.LogDebug($"  Grade limited: steepest step {steepestBefore:P0} -> {steepestAfter:P0}");

            // The limiter may cut a point below a raised ford's surface; the
            // floor is a constant, so putting it back leaves the profile
            // inside the cap.
            ApplyFloor(finalHeights, minHeight);

            // Held to the roads it meets after its grade was limited, a road can be pulled steeper than any cap
            // (at the top of a 59 m staircase at 1937.8,751.4 a 19 m
            // road from the stair's top was held up to a road 9 m above it within 10 m). Where a held stretch cannot be
            // reached within the cap its pitches allow, it is let go: the road keeps its own heights there and
            // passes that road at its own level rather than climbing to it. If it is still too steep, it is refused.
            var own = new List<float>(finalHeights);
            var heldAt = new bool[densePoints.Count];
            var letGo = new bool[densePoints.Count];
            float cap = RoadPitches.LimitCap(RoadGrade.Configured, edgeGrades);
            for (int attempt = 0; ; attempt++)
            {
                for (int k = 0; k < own.Count; k++) finalHeights[k] = own[k];
                System.Array.Clear(heldAt, 0, heldAt.Length);
                junctions = HoldToNetwork(densePoints, finalHeights, width, cap, SampleTerrain, joinedStart, joinedEnd, heldAt,
                    letGo, edgeGrades, minHeight);
                // The floor again (a raised ford's surface, dry land's floor): held to a road below it, or eased
                // down to one, a stretch beside a raised ford dipped under its deck. Before the grade is judged.
                ApplyFloor(finalHeights, minHeight);
                if (!RoadGrade.Capped(RoadGrade.Configured)) break;
                int worst = WorstOverCap(densePoints, finalHeights, cap, edgeGrades, heldAt);
                if (worst < 0) break;
                if (attempt >= HeldLetGoRounds || !LetGoAround(worst, heldAt, letGo) || LetGoOntoRoad(densePoints, letGo, width))
                {
                    LastRefusal = "held to a road over the grade cap";
                    Log.LogDebug($"Road refused: held to a road it meets, a step climbs over the {RoadGrade.Configured:P0} cap");
                    return null;
                }
            }
        }

        // A structure's end holds (StructureEnds): the grade limiter and the hold to the roads it meets can move the
        // levelled stretch, which left viaduct ends a step or more off the road. It
        // is put back; where the road cannot then reach it within the grade cap, the piece is refused (a valley viaduct is
        // then not built with this road).
        if (!followTerrain && LevelledEnds is { } heldEnds && densePoints.Count > 1)
        {
            var moved = new bool[densePoints.Count];
            bool any = false;
            foreach (var (atStart, height) in LevelledOn(heldEnds, densePoints, pathTotal, startGround, endGround))
            {
                float target = Mathf.Max(height, minHeight);
                for (int i = 0; i < densePoints.Count; i++)
                {
                    float from = atStart ? distanceFromStart[i] : pathTotal - distanceFromStart[i];
                    if (from > StructureEnds.ApronLength + StructureEnds.JoinOverlap + 1e-3f || Mathf.Abs(finalHeights[i] - target) <= 0.01f) continue;
                    finalHeights[i] = target;
                    moved[i] = any = true;
                }
            }
            // Judged only where it was put back: the steps into and along the levelled stretch.
            if (any && RoadGrade.Capped(RoadGrade.Configured))
            {
                float cap = RoadPitches.LimitCap(RoadGrade.Configured, edgeGrades);
                for (int i = 1; i < densePoints.Count; i++)
                {
                    if (!moved[i - 1] && !moved[i]) continue;
                    float run = Vector2.Distance(densePoints[i - 1], densePoints[i]);
                    if (run < 0.5f) continue;
                    float allowed = edgeGrades != null && i < edgeGrades.Length ? Mathf.Max(edgeGrades[i], RoadGrade.Configured) : cap;
                    if (Mathf.Abs(finalHeights[i] - finalHeights[i - 1]) / run > allowed + HeldCapMargin)
                    {
                        LastRefusal = "a structure's end cannot be met within the grade cap";
                        Log.LogDebug("Road refused: a structure's levelled end cannot be met within the grade cap");
                        return null;
                    }
                }
            }
        }

        if (!followTerrain && turns != null && landings != null && landings.Contains(true) && !RoadSwitchbacks.Separated(densePoints, finalHeights, width,
                RoadSwitchbacks.StairTurns || tightTurns ? landings : null, surfaceOnly: RoadSwitchbacks.StairTurns || tightTurns, widths: shapeWidths))
        { LastRefusal = SwitchbackRefusal.LegsTooClose; Log.LogDebug("Road refused: switchback legs blend at different heights"); return null; }
        return new PlannedPath(densePoints, finalHeights, debugInfos, width, totalLength, followTerrain)
        {
            Widths = shapeWidths, Junctions = junctions,
            PlaceLandingStart = placeStart && !joinedStart && RoadPitches.JunctionLanding > 0f,
            PlaceLandingEnd = placeEnd && !joinedEnd && RoadPitches.JunctionLanding > 0f,
        };
    }

    /// <summary>No point of a road below <paramref name="floor"/> (a raised ford's surface, dry land's floor): the one place
    /// the floor is put back, after the grade limiter and again after the road is held to the roads it meets.</summary>
    private static void ApplyFloor(List<float> heights, float floor)
    {
        if (floor > float.NegativeInfinity)
            for (int k = 0; k < heights.Count; k++)
                heights[k] = Mathf.Max(heights[k], floor);
    }

    /// <summary>Store a planned road. Nothing here can fail; every decision
    /// was made in PlanRoadPath.</summary>
    /// <param name="intoSite">A road carried on into a place's footprint onto its landing: its points level and paint there.
    /// A road's points inside a footprint are stored into it whatever this says: the only road there is the place's own,
    /// carried on over the ground it only paints (<see cref="RoadNetworkGenerator.KeepOutEdge"/>), and the terrain writer
    /// leaves every other point there alone.</param>
    public static void Commit(PlannedPath plan, int addition = 0, bool intoSite = false)
    {
        Dictionary<Vector2i, List<RoadPoint>> tempPoints = new Dictionary<Vector2i, List<RoadPoint>>();
        AddPlan(tempPoints, plan, addition, intoSite);
        if (RecordPointDebugInfo)
            for (int i = 0; i < plan.Points.Count; i++)
            {
                RoadPointDebugInfo debugInfo = plan.DebugInfos[i];
                debugInfo.SmoothedHeight = plan.Heights[i];
                lock (m_recordGate) m_debugInfo[plan.Points[i]] = debugInfo;
            }

        if (plan.PlaceLandingStart) System.Threading.Interlocked.Increment(ref s_placeLandingsStored);
        if (plan.PlaceLandingEnd) System.Threading.Interlocked.Increment(ref s_placeLandingsStored);
        MergePoints(tempPoints,plan.TotalLength,true);
        if (plan.Junctions != null && SearchStats.Enabled) lock (m_recordGate) Junctions.AddRange(plan.Junctions);
        RoadNetworkGenerator.IslandActivity?.Committed(plan.TotalLength);
    }

    /// <summary>A plan's points as road points, the one loop a generated road (<see cref="Commit"/>) and a manual road
    /// (<see cref="CommitAppend"/>) are stored by: each point at its own width (a switchback's turns are wider), and a
    /// point inside a place's footprint stored into it (<see cref="Commit"/>'s intoSite).</summary>
    private static void AddPlan(Dictionary<Vector2i, List<RoadPoint>> into, PlannedPath plan, int addition, bool intoSite)
    {
        for (int i = 0; i < plan.Points.Count; i++)
            AddRoadPoint(into, plan.Points[i], plan.Widths != null && i < plan.Widths.Count ? plan.Widths[i] : plan.Width, plan.Heights[i], plan.FollowTerrain, addition,
                intoSite: intoSite || (!plan.FollowTerrain && RoadSiteProtection.Contains(plan.Points[i])),
                unpainted: plan.Unpainted != null && i < plan.Unpainted.Count && plan.Unpainted[i]);
    }

    /// <summary>
    /// The commit path of a generated road: its pieces stored together, and never a stretch riding a road already stored
    /// (RoadRide, the one owner: the generator keeps only a road's own pieces, RoadRide.Own, before it gets here). A ridden
    /// stretch that reaches this point anyway is a defect upstream: the unit suite throws on it; a game logs it, counts it
    /// (RoadRide.CaughtAtCommit) and stores the road without it. <paramref name="ownChecked"/>: the pieces are what
    /// RoadRide.Own kept a moment ago, against the network as it stands; the ride is not replayed on them (a whole ride per
    /// commit), in the game and the unit suite alike.
    /// </summary>
    public static void CommitRoad(List<PlannedPath> pieces, bool intoSite = false, System.Action<PlannedPath>? committed = null,
        IReadOnlyList<RoadRide.Segment>? decks = null, bool ownChecked = false)
    {
        if (!ownChecked && RoadRide.Doubled(pieces, decks) is Vector2 at)
        {
            System.Threading.Interlocked.Increment(ref RoadRide.m_caught);
#if ROADS_TESTS
            if (RoadRide.ThrowWhenCaught) throw new System.InvalidOperationException(System.FormattableString.Invariant($"a road stored along a road already stored (a double road) at ({at.x:F1}, {at.y:F1})"));
#endif
            Log.LogWarning(System.FormattableString.Invariant($"Road pieces ride a road already stored at ({at.x:F1}, {at.y:F1}): stored without that stretch"));
            pieces = RoadRide.Own(pieces, out _, decks);
        }
        foreach (PlannedPath plan in pieces)
        {
            Commit(plan, intoSite: intoSite);
            committed?.Invoke(plan);
        }
    }

    /// <summary>Road points levelled to the given heights, stored without planning; painted unless
    /// <paramref name="unpainted"/> (the ground under a bridge deck's ends, which is never painted).</summary>
    public static void CommitLevelled(IList<Vector2> points, IList<float> heights, float width, bool unpainted = false)
    {
        var temp = new Dictionary<Vector2i, List<RoadPoint>>();
        for (int i = 0; i < points.Count; i++)
            AddRoadPoint(temp, points[i], width, heights[i], false, unpainted: unpainted);
        MergePoints(temp);
    }

    /// <summary>A staircase's earthwork, stored with the road it climbs: a
    /// cut-only point per cut (x, z, the height to lower the ground to).</summary>
    public static void CommitCuts(IReadOnlyList<Vector3> cuts, float width)
    {
        var temp = new Dictionary<Vector2i, List<RoadPoint>>();
        foreach (Vector3 c in cuts)
            AddRoadPoint(temp, new Vector2(c.x, c.z), width, c.y, paintOnly: false, cutOnly: true);
        MergePoints(temp);
    }

    /// <summary>
    /// A staircase's cuts taken back where its pieces were taken away (RoadStairRuns.LayLanding): every cut-only point within
    /// <paramref name="reach"/> of one of <paramref name="near"/> that <paramref name="taken"/> picks leaves the network. A
    /// cut wins over any road's fill (RoadTerrainModifier), so left in, it dug a pit in the ground filled over it. Returns
    /// how many stored records went.
    /// </summary>
    internal static int TakeBackCuts(IReadOnlyList<Vector2> near, float reach, Func<RoadPoint, bool> taken)
    {
        var cells = new HashSet<Vector2i>();
        int rings = Mathf.CeilToInt(reach / GridSize) + 1;
        foreach (Vector2 q in near)
        {
            Vector2i home = GetRoadGrid(q.x, q.y);
            for (int y = home.y - rings; y <= home.y + rings; y++)
                for (int x = home.x - rings; x <= home.x + rings; x++)
                    cells.Add(new Vector2i(x, y));
        }
        int me = CrossIslandReads.Me;
        bool Taken(RoadPoint rp)
        {
            if (!rp.cutOnly || !CrossIslandReads.Sees(me, rp.island) || !taken(rp)) return false;
            foreach (Vector2 q in near)
                if (Vector2.Distance(rp.p, q) <= reach) return true;
            return false;
        }
        int removed = 0;
        RoadNetworkGenerator.IslandNetworkChanged();
        m_roadCacheLock.EnterWriteLock();
        try
        {
            int cellsWithRoads = GridCellsWithRoads;
            foreach (Vector2i cell in cells)
            {
                if (!m_roadPoints.TryGetValue(cell, out var points)) continue;
                var kept = Array.FindAll(points, rp => !Taken(rp));
                if (kept.Length == points.Length) continue;
                removed += points.Length - kept.Length;
                if (kept.Length > 0) m_roadPoints[cell] = kept;
                else if (m_roadPoints.TryRemove(cell, out _)) cellsWithRoads--;
            }
            m_storedPointRecords -= removed;
            GridCellsWithRoads = cellsWithRoads;
        }
        finally
        {
            m_roadCacheLock.ExitWriteLock();
        }
        return removed;
    }

    /// <summary>
    /// Stored road points moved to new heights (RoadStairRuns.LayLanding, a structure's end landing: another road raised to
    /// it): each point in <paramref name="changes"/>, as it was read, goes to its height in every cell it is filed in; a point
    /// two changes name goes to the higher. A point no longer stored as it was read is left. Returns how many stored records
    /// moved.
    /// </summary>
    internal static int SetRoadHeights(IReadOnlyList<(RoadPoint at, float to)> changes)
    {
        if (changes.Count == 0) return 0;
        RoadNetworkGenerator.IslandNetworkChanged();
        m_roadCacheLock.EnterWriteLock();
        try { return Raise(m_roadPoints, changes, null); }
        finally { m_roadCacheLock.ExitWriteLock(); }
    }

    /// <summary><see cref="SetRoadHeights"/> on <paramref name="cells"/> (the caller holds the grid's write lock). A cell's
    /// array is always replaced, never changed in place: other threads read a committed array without the lock. With
    /// <paramref name="addition"/> (an append) each moved point becomes the append's, so a terrain compiler stamped before it
    /// writes the point again.</summary>
    private static int Raise(IDictionary<Vector2i, RoadPoint[]> cells, IReadOnlyList<(RoadPoint at, float to)> changes, int? addition)
    {
        var byPoint = new Dictionary<Vector2, List<(float from, float to)>>();
        var visit = new HashSet<Vector2i>();
        foreach (var (at, to) in changes)
        {
            if (!byPoint.TryGetValue(at.p, out var list)) byPoint[at.p] = list = new List<(float from, float to)>();
            int e = list.FindIndex(x => Mathf.Abs(x.from - at.h) < 1e-4f);
            if (e >= 0) list[e] = (list[e].from, Mathf.Max(list[e].to, to));
            else list.Add((at.h, to));
            Vector2i home = GetRoadGrid(at.p.x, at.p.y);
            int rings = Mathf.CeilToInt(at.w / GridSize) + 1;
            for (int y = home.y - rings; y <= home.y + rings; y++)
                for (int x = home.x - rings; x <= home.x + rings; x++)
                    visit.Add(new Vector2i(x, y));
        }
        int moved = 0;
        int me = CrossIslandReads.Me;
        foreach (Vector2i cell in visit)
        {
            if (!cells.TryGetValue(cell, out var points)) continue;
            RoadPoint[]? copy = null;
            for (int k = 0; k < points.Length; k++)
            {
                if (!points[k].IsRoad || !CrossIslandReads.Sees(me, points[k].island) || !byPoint.TryGetValue(points[k].p, out var list)) continue;
                float h = points[k].h;
                int e = list.FindIndex(x => Mathf.Abs(x.from - h) < 1e-4f);
                if (e < 0) continue;
                RoadPoint[] into = copy ??= (RoadPoint[])points.Clone();
                into[k].h = list[e].to;
                if (addition is int a) into[k].addition = a;
                moved++;
            }
            if (copy != null) cells[cell] = copy;
        }
        return moved;
    }

    /// <summary>Plan a road and store it, for the caller that lays one road in
    /// one piece. False means it was refused and nothing was stored.</summary>
    public static bool AddRoadPath(List<Vector2> path, float width, WorldGenerator worldGen,
        float? startGround = null, float? endGround = null,
        bool followTerrain = false, float minHeight = float.NegativeInfinity)
    {
        PlannedPath? plan = PlanRoadPath(path, width, worldGen, startGround, endGround, followTerrain, minHeight);
        if (plan == null) return false;
        Commit(plan);
        return true;
    }

    /// <summary>
    /// Called after all roads are generated, and after a network is loaded from
    /// the save, to compute the network version: a hash of the world seed and
    /// every stored road point (position, width, height, paint-only flag)
    /// in canonical order (cells by coordinate, points by position, width,
    /// height, flag), each record
    /// mixed into the running value, so it is the same after generation and
    /// after a save/load round trip (which carries exactly the stored points)
    /// and changes whenever any road moves or changes height (balanced height
    /// changes too: records are mixed in order, never summed).
    /// RoadTerrainModifier stamps it on each zone's terrain compiler to tell
    /// zones that already carry the current roads from zones that still need
    /// them.
    /// </summary>
    public static void FinalizeRoadNetwork()
    {
        int worldSeed = WorldGenerator.instance?.GetSeed() ?? 0;
        m_roadCacheLock.EnterWriteLock();
        try { RoadNetworkVersion=ComputeRoadNetworkVersion(m_roadPoints,worldSeed); }
        finally { m_roadCacheLock.ExitWriteLock(); }
        Log.LogDebug($"Road network finalized: version={RoadNetworkVersion}, stored-records={StoredPointRecords}, cells={GridCellsWithRoads}");
    }
    // Caller owns the grid write lock; no locking getters or callbacks inside the hash publication.
    private static int ComputeRoadNetworkVersion(IReadOnlyDictionary<Vector2i,RoadPoint[]> geometry,int worldSeed)
    {
#if ROADS_TESTS
        BeforeVersionCalculation?.Invoke();
#endif
        uint hash = 2166136261u; // FNV offset basis
        int storedPoints = 0;
        int cells = 0;

        var keys = new List<Vector2i>(geometry.Keys);
        keys.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
        var records = new List<RoadPoint>();
        foreach (var key in keys)
        {
            cells++;
            Mix(ref hash, key.x);
            Mix(ref hash, key.y);
            records.Clear();
            records.AddRange(geometry[key]);
            records.Sort(CompareRecords);
            foreach (var rp in records)
            {
                storedPoints++;
                Mix(ref hash, rp.p.x.GetHashCode());
                Mix(ref hash, rp.p.y.GetHashCode());
                Mix(ref hash, rp.w.GetHashCode());
                Mix(ref hash, rp.h.GetHashCode());
                // A road-only network mixes exactly what it always did.
                Mix(ref hash, rp.Kind);
            }
        }

        Mix(ref hash, worldSeed);
        Mix(ref hash, storedPoints);
        Mix(ref hash, cells);
        int version = unchecked((int)hash);
        if (version == 0)
            version = 1; // 0 means "no network"

        return version;
    }

    private static int CompareRecords(RoadPoint a, RoadPoint b)
    {
        int c = a.p.x.CompareTo(b.p.x);
        if (c != 0) return c;
        c = a.p.y.CompareTo(b.p.y);
        if (c != 0) return c;
        c = a.w.CompareTo(b.w);
        if (c != 0) return c;
        c = a.h.CompareTo(b.h);
        if (c != 0) return c;
        c = a.paintOnly.CompareTo(b.paintOnly);
        if (c != 0) return c;
        c = a.cutOnly.CompareTo(b.cutOnly);
        if (c != 0) return c;
        c = a.intoSite.CompareTo(b.intoSite);
        return c != 0 ? c : a.unpainted.CompareTo(b.unpainted);
    }

    /// <summary>FNV-1a step over the four bytes of value, then an avalanche so neighbouring records do not cancel.</summary>
    private static void Mix(ref uint hash, int value)
    {
        unchecked
        {
            uint v = (uint)value;
            for (int i = 0; i < 4; i++)
            {
                hash ^= (v >> (8 * i)) & 0xFFu;
                hash *= 16777619u;
            }
            hash ^= hash >> 15;
            hash *= 0x2C1B3C6Du;
            hash ^= hash >> 12;
        }
    }

    private static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        return 0.5f * (
            (2f * p1) +
            (-p0 + p2) * t +
            (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
            (-p0 + 3f * p1 - 3f * p2 + p3) * t3
        );
    }

    /// <summary>The waypoints' straight legs, a point every <paramref name="segmentLength"/> metres or less.</summary>
    private static List<Vector2> Densify(List<Vector2> waypoints, float segmentLength)
    {
        var result = new List<Vector2>();
        for (int i = 0; i < waypoints.Count - 1; i++)
        {
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(waypoints[i], waypoints[i + 1]) / segmentLength));
            for (int s = 0; s < steps; s++) result.Add(Vector2.Lerp(waypoints[i], waypoints[i + 1], s / (float)steps));
        }
        result.Add(waypoints[waypoints.Count - 1]);
        return result;
    }

    private static List<Vector2> SplinePath(List<Vector2> waypoints, float segmentLength)
    {
        if (waypoints.Count < 2) return new List<Vector2>(waypoints);
        
        var result = new List<Vector2>();
        
        for (int i = 0; i < waypoints.Count - 1; i++)
        {
            Vector2 p0 = waypoints[Mathf.Max(0, i - 1)];
            Vector2 p1 = waypoints[i];
            Vector2 p2 = waypoints[i + 1];
            Vector2 p3 = waypoints[Mathf.Min(waypoints.Count - 1, i + 2)];
            
            float segDist = Vector2.Distance(p1, p2);
            int steps = Mathf.Max(1, Mathf.CeilToInt(segDist / segmentLength));
            
            for (int s = 0; s < steps; s++)
            {
                float t = s / (float)steps;
                result.Add(CatmullRom(p0, p1, p2, p3, t));
            }
        }
        
        result.Add(waypoints[waypoints.Count - 1]);
        return result;
    }

    /// <summary>
    /// Simple moving average height smoothing.
    /// Creates smooth road surfaces by averaging heights in a sliding window.
    /// </summary>
    private static List<float> SmoothHeights(List<float> heights, int windowSize, out List<RoadPointDebugInfo> debugInfos)
    {
        debugInfos = new List<RoadPointDebugInfo>(heights.Count);
        
        if (heights.Count < 2)
        {
            if (heights.Count == 1)
            {
                debugInfos.Add(new RoadPointDebugInfo
                {
                    PointIndex = 0,
                    TotalPoints = 1,
                    OriginalHeight = heights[0],
                    SmoothedHeight = heights[0],
                    ActualWindowSize = 1
                });
            }
            return new List<float>(heights);
        }
        
        List<float> smoothed = new List<float>(heights.Count);
        int halfWindow = windowSize / 2;
        bool keepWindows = RecordPointDebugInfo;

        for (int i = 0; i < heights.Count; i++)
        {
            float sum = 0f;
            int count = 0;
            int windowStart = Mathf.Max(0, i - halfWindow);
            int windowEnd = Mathf.Min(heights.Count - 1, i + halfWindow);

            for (int j = windowStart; j <= windowEnd; j++)
            {
                sum += heights[j];
                count++;
            }
            
            float smoothedHeight = sum / count;
            if (windowStart > i - halfWindow || windowEnd < i + halfWindow)
            {
                // Within half a window of either end the window is one-sided,
                // and the mean of a one-sided window on a slope is the height
                // some way back along the road: the road arrived at its ends
                // on a ledge (uphill) or a hump (downhill) as high as the
                // slope times the missing half window. Fit a line through the
                // window instead and read it at this point, which smooths the
                // same bumps but is exact on a slope. Mid-road the window is
                // symmetric and the line's value there is the mean, so the
                // mean's arithmetic is kept unchanged.
                smoothedHeight = LineFitAt(heights, windowStart, windowEnd, i, smoothedHeight);
            }
            smoothed.Add(smoothedHeight);
            
            debugInfos.Add(new RoadPointDebugInfo
            {
                PointIndex = i,
                TotalPoints = heights.Count,
                OriginalHeight = heights[i],
                SmoothedHeight = smoothedHeight,
                WindowStart = windowStart,
                WindowEnd = windowEnd,
                ActualWindowSize = count,
                WindowHeights = keepWindows ? heights.GetRange(windowStart, count).ToArray() : null!
            });
        }
        
        return smoothed;
    }

    /// <summary>
    /// Least-squares line through heights[start..end] against the point index,
    /// evaluated at index at; the mean is returned when the window has fewer
    /// than two points.
    /// </summary>
    private static float LineFitAt(List<float> heights, int start, int end, int at, float mean)
    {
        int n = end - start + 1;
        if (n < 2)
            return mean;

        double sx = 0, sxx = 0, sh = 0, sxh = 0;
        for (int j = start; j <= end; j++)
        {
            double x = j - at;
            double h = heights[j];
            sx += x;
            sxx += x * x;
            sh += h;
            sxh += x * h;
        }

        double det = n * sxx - sx * sx;
        if (det <= 0)
            return mean;
        return (float)((sxx * sh - sx * sxh) / det);
    }

    /// <summary>Where a road met one already laid: the point it was pulled furthest from its own
    /// profile (metres, + up), and how far the held road stands over the natural ground there
    /// (+ fill, - cut): a large one is an earthwork ridge (seen at 896,4416).</summary>
    public readonly struct Junction
    {
        public readonly Vector2 At;
        public readonly float Pull, Earth;
        /// <summary>Where along the new road: "start", "end" (a held stretch touching that end) or "middle".</summary>
        public readonly string Where;
        public Junction(Vector2 at, float pull, float earth, string where = "middle") { At = at; Pull = pull; Earth = earth; Where = where; }
    }

    /// <summary>Every junction of the roads committed this generation (under m_recordGate), for the
    /// generation summary: recorded only with PROCEDURALROADS_TIMINGS (<see cref="SearchStats.Enabled"/>).</summary>
    public static readonly List<Junction> Junctions = new();

    /// <summary>How many held stretches a road may let go before it is refused.</summary>
    internal const int HeldLetGoRounds = 4;

    /// <summary>The index of the steepest step of a held profile over its own cap (the edge's own grade where
    /// pitches set one, else <paramref name="cap"/>) by more than <see cref="HeldCapMargin"/>, or -1. A step
    /// between two points both held to other roads is theirs, built within their own limits, and not judged.</summary>
    internal static int WorstOverCap(IReadOnlyList<Vector2> points, IReadOnlyList<float> heights, float cap, IReadOnlyList<float>? edgeGrades, bool[] held)
    {
        int worst = -1;
        float steepest = 0f;
        for (int i = 1; i < points.Count && i < heights.Count; i++)
        {
            if (held[i - 1] && held[i]) continue;
            float run = Vector2.Distance(points[i - 1], points[i]);
            if (run < 0.5f) continue;
            float grade = Mathf.Abs(heights[i] - heights[i - 1]) / run;
            float allowed = edgeGrades != null && i < edgeGrades.Count ? Mathf.Max(edgeGrades[i], RoadGrade.Configured) : cap;
            if (grade > allowed + HeldCapMargin && grade > steepest) { steepest = grade; worst = i; }
        }
        return worst;
    }

    /// <summary>Whether a let-go point would lie on another road's surface (within the two half widths): a road kept at its
    /// own height there would cross that one at a different level, two surfaces mixing, and is refused instead (seen
    /// beside the mountain cave entry at 1950,705).</summary>
    internal static bool LetGoOntoRoad(List<Vector2> points, bool[] letGo, float width)
    {
        for (int i = 0; i < points.Count; i++)
            if (letGo[i] && TryGetRoadHeightWithin(points[i], width, out _, out float near, out float other) && near < (width + other) / 2f)
                return true;
        return false;
    }

    /// <summary>Let go of the held stretch nearest step <paramref name="i"/> (its whole run of held points): the one
    /// the road's own profile was eased toward and could not reach. False when nothing is held.</summary>
    private static bool LetGoAround(int i, bool[] held, bool[] letGo)
    {
        int at = -1;
        for (int d = 0; d < held.Length && at < 0; d++)
        {
            if (i - 1 - d >= 0 && held[i - 1 - d]) at = i - 1 - d;
            else if (i + d < held.Length && held[i + d]) at = i + d;
        }
        if (at < 0) return false;
        for (int k = at; k >= 0 && held[k]; k--) letGo[k] = true;
        for (int k = at; k < held.Length && held[k]; k++) letGo[k] = true;
        return true;
    }
    /// <summary>Allowance over the cap for a step of a held road: the heights it is held to are other roads'
    /// own, read from their nearest points, and jitter by a few centimetres a metre.</summary>
    internal const float HeldCapMargin = 0.1f;

    /// <summary>
    /// Where a road comes within reach of a road already laid -- running along it, crossing it,
    /// or the mouth where it joins -- it takes that road's height there: the nearest point's
    /// within its width and the terrain blend on both sides, as far as the terrain writer mixes
    /// the two. Elsewhere it keeps its own profile, eased to and from those held stretches within
    /// each edge's own grade limit, as the grade limiter gave them (<paramref name="edgeGrades"/>:
    /// a pitch steeper than the cap, a rest or a turn landing gentler; <see cref="RoadGrade.Limit"/>),
    /// else at <paramref name="cap"/>; held no lower than <paramref name="floor"/>. The road already laid is never
    /// changed, so the same ground never carries two road surfaces.
    /// </summary>
    internal static List<Junction>? HoldToNetwork(List<Vector2> points, List<float> heights, float width, float cap,
        System.Func<Vector2, float>? ground = null, bool joinedStart = false, bool joinedEnd = false, bool[]? heldOut = null,
        bool[]? letGo = null, IReadOnlyList<float>? edgeGrades = null, float floor = float.NegativeInfinity)
    {
        if (!m_initialized || points.Count == 0) return null;
        float band = RoadProfile.Footprint(width);
        int n = points.Count;
        var held = new bool[n];
        bool any = false;
        List<Junction>? junctions = null;
        Junction? stretch = null;
        int stretchFrom = 0;
        string Where(int to) => stretchFrom == 0 ? (joinedStart ? "start on a road" : "start at a place")
            : to == n - 1 ? (joinedEnd ? "end on a road" : "end at a place") : "middle";
        for (int i = 0; i < n; i++)
        {
            if ((letGo == null || !letGo[i]) && TryGetRoadHeightWithin(points[i], band, out float h, out float near, out float nearWidth))
            {
                // Never below this road's own floor (a raised ford's surface, dry land's floor): held to a road below it,
                // it stands on the floor, and its junction record says so.
                h = Mathf.Max(h, floor);
                if (stretch == null) stretchFrom = i;
                // Per held stretch, the point the road was pulled furthest from its own profile.
                float pull = h - heights[i];
                // Its fill over the ground the road it meets already left there: that road's own surface
                // within its width, blended out to the natural ground over the terrain margin. Against
                // the natural ground, a road running along one laid on a fill counted that road's own
                // embankment as a new junction ridge, and was refused for a ridge it did not make.
                float under = ground == null ? float.NaN
                    : Mathf.Lerp(ground(points[i]), h, RoadProfile.LevelBlend(near, nearWidth));
                float earth = h - under;
                if (stretch == null || Mathf.Abs(pull) > Mathf.Abs(stretch.Value.Pull))
                    stretch = new Junction(points[i], pull, earth, Where(i));
                heights[i] = h; held[i] = true; any = true;
            }
            else if (stretch != null)
            {
                var j = stretch.Value;
                (junctions ??= new List<Junction>()).Add(new Junction(j.At, j.Pull, j.Earth, Where(i - 1)));
                stretch = null;
            }
        }
        if (stretch != null) { var j = stretch.Value; (junctions ??= new List<Junction>()).Add(new Junction(j.At, j.Pull, j.Earth, Where(n - 1))); }
        if (heldOut != null) System.Array.Copy(held, heldOut, System.Math.Min(n, heldOut.Length));
        if (!any || !RoadGrade.Capped(cap)) return junctions;
        // Ease the road's own stretches to the held ones, both ways, each edge within its own limit;
        // its two ends keep the heights they have to meet.
        held[0] = held[n - 1] = true;
        for (int i = 1; i < n; i++)
        {
            if (held[i]) continue;
            float rise = RoadGrade.EdgeLimit(edgeGrades, i, cap) * Vector2.Distance(points[i - 1], points[i]);
            heights[i] = Mathf.Clamp(heights[i], heights[i - 1] - rise, heights[i - 1] + rise);
        }
        for (int i = n - 2; i >= 0; i--)
        {
            if (held[i]) continue;
            float rise = RoadGrade.EdgeLimit(edgeGrades, i + 1, cap) * Vector2.Distance(points[i + 1], points[i]);
            heights[i] = Mathf.Clamp(heights[i], heights[i + 1] - rise, heights[i + 1] + rise);
        }
        return junctions;
    }

    private static int DetectOverlap(List<Vector2> points, float width)
    {
        if (!m_initialized || GridCellsWithRoads == 0)
            return 0;

        int overlapCount = 0;
        float searchRadius = width * RoadConstants.OverlapSearchRadiusMultiplier;
        int me = CrossIslandReads.Me;

        foreach (var point in points)
        {
            Vector2i grid = GetRoadGrid(point.x, point.y);
            
            {
                if (m_roadPoints.TryGetValue(grid, out var existingPoints))
                {
                    foreach (var rp in existingPoints)
                    {
                        if (rp.cutOnly) continue; // a staircase's cut is not a road to meet
                        if (!CrossIslandReads.Sees(me, rp.island)) continue;
                        if (Vector2.Distance(rp.p, point) < searchRadius)
                        {
                            overlapCount++;
                            break;
                        }
                    }
                }
            }
        }

        return overlapCount;
    }

    private static void BlendWithExistingRoads(List<Vector2> points, List<float> heights, float width)
    {
        if (!m_initialized || GridCellsWithRoads == 0)
            return;

        float blendRadius = width * RoadConstants.OverlapBlendRadiusMultiplier;
        int me = CrossIslandReads.Me;

        for (int i = 0; i < points.Count; i++)
        {
            Vector2i grid = GetRoadGrid(points[i].x, points[i].y);
            
            {
                if (m_roadPoints.TryGetValue(grid, out var existingPoints))
                {
                    float totalWeight = 1.0f;
                    float weightedSum = heights[i];
                    
                    foreach (var rp in existingPoints)
                    {
                        if (rp.cutOnly) continue; // nor a height to blend toward
                        if (!CrossIslandReads.Sees(me, rp.island)) continue;
                        float dist = Vector2.Distance(rp.p, points[i]);
                        if (dist < blendRadius)
                        {
                            float weight = 1.0f - (dist / blendRadius);
                            weightedSum += rp.h * weight;
                            totalWeight += weight;
                        }
                    }
                    
                    if (totalWeight > 1.0f)
                        heights[i] = weightedSum / totalWeight;
                }
            }
        }
    }

    private static void AddRoadPoint(Dictionary<Vector2i, List<RoadPoint>> roadPoints, Vector2 p, float width, float height, bool paintOnly, int addition = 0, bool cutOnly = false, bool intoSite = false,
        bool unpainted = false)
    {
        Vector2i grid = GetRoadGrid(p.x, p.y);
        int radius = Mathf.CeilToInt(width / GridSize);

        for (int y = grid.y - radius; y <= grid.y + radius; y++)
        {
            for (int x = grid.x - radius; x <= grid.x + radius; x++)
            {
                Vector2i cellGrid = new Vector2i(x, y);
                if (InsideRoadGrid(cellGrid, p, width))
                {
                    if (!roadPoints.TryGetValue(cellGrid, out var list))
                    {
                        list = new List<RoadPoint>();
                        roadPoints.Add(cellGrid, list);
                    }
                    list.Add(new RoadPoint(p, width, height, paintOnly, addition, cutOnly, intoSite, unpainted) { island = CrossIslandReads.Tag });
                }
            }
        }
    }

    private static bool InsideRoadGrid(Vector2i grid, Vector2 p, float r)
    {
        Vector2 gridCenter = new Vector2(grid.x * GridSize, grid.y * GridSize);
        Vector2 delta = p - gridCenter;
        float halfGrid = GridSize / 2f;
        return Mathf.Abs(delta.x) < r + halfGrid && Mathf.Abs(delta.y) < r + halfGrid;
    }

    private static void MergePoints(Dictionary<Vector2i, List<RoadPoint>> tempPoints,float? committedLength=null,bool initialize=false)
    {
        RoadNetworkGenerator.IslandNetworkChanged();
        m_roadCacheLock.EnterWriteLock();
        try
        {
            int added=0;
            foreach(var points in tempPoints.Values) added=checked(added+points.Count);
            int total=checked(m_storedPointRecords+added);
            float? length=AddCommittedLength(m_committedPathLength,committedLength);
            NoteFiled(tempPoints);
            int cells=GridCellsWithRoads;
            foreach (var kvp in tempPoints)
            {
                if (m_roadPoints.TryGetValue(kvp.Key, out var existing))
                {
                    var combined = new RoadPoint[existing.Length + kvp.Value.Count];
                    Array.Copy(existing, combined, existing.Length);
                    kvp.Value.CopyTo(combined, existing.Length);
                    m_roadPoints[kvp.Key] = combined;
                }
                else
                {
                    m_roadPoints[kvp.Key] = kvp.Value.ToArray();
                    cells++;
                }
            }
            
#if ROADS_TESTS
            BeforeStatisticsPublication?.Invoke();
#endif
            m_storedPointRecords=total;
            m_committedPathLength=length;
            // A first cut or levelled support is a real, queryable grid too, before settlement or a save/load.
            if(initialize || added>0) m_initialized=true;
            GridCellsWithRoads = cells;
        }
        finally
        {
            m_roadCacheLock.ExitWriteLock();
        }
    }

    private static float? AddCommittedLength(float? current,float? addition)
    {
        if(!addition.HasValue) return current;
        if(!ManualRoadDraft.IsFinite(addition.Value) || addition.Value<0) throw new ArgumentException("Invalid committed path length.");
        if(!current.HasValue) return null;
        float result=current.Value+addition.Value;
        if(!ManualRoadDraft.IsFinite(result)) throw new OverflowException("Committed path length overflow.");
        return result;
    }

    /// <summary>The points stored in one 64 m grid cell (<see cref="GetRoadGrid"/>), or null: RoadRide reads its lines from
    /// these. Lock-free like every point lookup.</summary>
    internal static RoadPoint[]? CellPoints(Vector2i cell) => m_roadPoints.TryGetValue(cell, out var points) ? points : null;

    public static Vector2i GetRoadGrid(float wx, float wy)
    {
        int x = Mathf.FloorToInt((wx + GridSize / 2f) / GridSize);
        int y = Mathf.FloorToInt((wy + GridSize / 2f) / GridSize);
        return new Vector2i(x, y);
    }

    /// <summary>The road weight at a point: how much of a road covers it and how wide that road is.
    /// Lock-free: a commit replaces a cell's array, never edits it.</summary>
    public static void GetRoadWeight(float wx, float wy, out float weight, out float width)
    {
        Vector2i grid = GetRoadGrid(wx, wy);

        RoadPoint[]? points;
        m_roadPoints.TryGetValue(grid, out points);

        if (points == null)
        {
            weight = 0f;
            width = 0f;
            return;
        }
        GetWeight(points, wx, wy, out weight, out width);
    }

    /// <summary>Whether <see cref="GetRoadWeight"/> gives this point a weight above zero: the first point of the cell that
    /// weighs anything answers, without the widest weight being sought.</summary>
    public static bool AnyRoadAt(float wx, float wy)
    {
        if (!m_roadPoints.TryGetValue(GetRoadGrid(wx, wy), out RoadPoint[]? points)) return false;
        Vector2 pos = new Vector2(wx, wy);
        int me = CrossIslandReads.Me;
        byte skipped = 0;
        for (int i = 0; i < points.Length; i++)
        {
            RoadPoint rp = points[i];
            if (rp.cutOnly) continue;
            float sqrDist = Vector2.SqrMagnitude(rp.p - pos);
            float halfWidth = rp.w * 0.5f;
            if (!(sqrDist < halfWidth * halfWidth)) continue;
            if (!CrossIslandReads.Sees(me, rp.island)) { skipped = rp.island; continue; }
            float normalizedDist = Mathf.Sqrt(sqrDist) / halfWidth;
            if (normalizedDist < RoadConstants.EdgeFalloffStart) return true;
            // The same arithmetic as GetWeight: at the very edge the falloff can round to nothing.
            float edgeT = (normalizedDist - RoadConstants.EdgeFalloffStart) / (1f - RoadConstants.EdgeFalloffStart);
            if (1f - Smoothstep(edgeT) > 0f) return true;
        }
        if (skipped != 0) CrossIslandReads.Skipped(CrossIslandReads.Kind.Points, skipped, pos);
        return false;
    }

    private static void GetWeight(RoadPoint[] points, float wx, float wy, out float weight, out float width)
    {
        Vector2 pos = new Vector2(wx, wy);
        float bestWeight = 0f;
        float bestWidth = 0f;
        int me = CrossIslandReads.Me;

        for (int i = 0; i < points.Length; i++)
        {
            RoadPoint rp = points[i];
            if (rp.cutOnly) continue; // a staircase's cut is not painted road
            if (!CrossIslandReads.Sees(me, rp.island)) continue;
            float sqrDist = Vector2.SqrMagnitude(rp.p - pos);
            float halfWidth = rp.w * 0.5f;
            float halfWidthSqr = halfWidth * halfWidth;

            if (sqrDist < halfWidthSqr)
            {
                float dist = Mathf.Sqrt(sqrDist);
                float normalizedDist = dist / halfWidth;
                
                float pointWeight;
                if (normalizedDist < RoadConstants.EdgeFalloffStart)
                {
                    pointWeight = 1f;
                }
                else
                {
                    float edgeT = (normalizedDist - RoadConstants.EdgeFalloffStart) / (1f - RoadConstants.EdgeFalloffStart);
                    pointWeight = 1f - Smoothstep(edgeT);
                }

                if (pointWeight > bestWeight)
                {
                    bestWeight = pointWeight;
                    bestWidth = rp.w;
                }
            }
        }

        weight = bestWeight;
        width = bestWidth;
    }

    private static float Smoothstep(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// How far outside a zone a road point can still change the zone's
    /// terrain: its leveled footprint (half the width plus the blend margin),
    /// or its full width, whichever is more. A zone's own compiler holds its
    /// border vertices, and the zone next door holds the same vertices in its
    /// own; both must see every point that reaches them, or they compute
    /// different heights for the same place and the road steps at the seam.
    /// The full width alone, used before, fell short for roads narrower than
    /// 4 m (a 2 m road reaches 3 m). From 4 m up the two agree and nothing changes.
    /// </summary>
    public static float ZoneReach(float width) =>
        Mathf.Max(width, RoadTerrainModifier.GatherRadius(width));

    public static List<RoadPoint> GetRoadPointsInZone(Vector2s zoneID)
    {
        List<RoadPoint> result = new List<RoadPoint>();
        
        if (!m_initialized)
            return result;

        Vector3 zonePos = ZoneSystem.GetZonePos(zoneID);
        Vector2i grid = GetRoadGrid(zonePos.x, zonePos.z);
        int me = CrossIslandReads.Me;

        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    Vector2i checkGrid = new Vector2i(grid.x + dx, grid.y + dy);
                    if (m_roadPoints.TryGetValue(checkGrid, out var points))
                    {
                        foreach (var rp in points)
                        {
                            if (!CrossIslandReads.Sees(me, rp.island)) continue;
                            // A staircase's cut reaches only w/2 (plus a vertex spacing): gathered
                            // wider, a zone it never touches waits for it forever, unstamped.
                            float reach = RoadConstants.HalfZoneSize + (rp.cutOnly ? RoadTerrainModifier.CutReach(rp.w) + RoadTerrainModifier.CutGatherPad : ZoneReach(rp.w));
                            if (rp.p.x >= zonePos.x - reach &&
                                rp.p.x <= zonePos.x + reach &&
                                rp.p.y >= zonePos.z - reach &&
                                rp.p.y <= zonePos.z + reach)
                            {
                                result.Add(rp);
                            }
                        }
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Every zone for which GetRoadPointsInZone is not empty: the zones a road
    /// writes terrain into, found from the points instead of by asking each
    /// zone in the world. Candidates come from each point's reach (the zone
    /// square widened by the point's width, GetRoadPointsInZone's own bounds,
    /// plus a zone of slack either way against rounding at the edges); each
    /// candidate is then kept only if GetRoadPointsInZone finds points in it,
    /// so the two agree zone for zone by construction.
    /// </summary>
    public static HashSet<Vector2s> GetZonesWithRoadPoints()
    {
        var candidates = new HashSet<Vector2s>();
        if (!m_initialized)
            return candidates;

        m_roadCacheLock.EnterReadLock();
        try
        {
            foreach (RoadPoint[] cell in m_roadPoints.Values)
            {
                foreach (RoadPoint rp in cell)
                {
                    float reach = RoadConstants.HalfZoneSize + ZoneReach(rp.w);
                    int x0 = Mathf.FloorToInt((rp.p.x - reach) / RoadConstants.ZoneSize);
                    int x1 = Mathf.CeilToInt((rp.p.x + reach) / RoadConstants.ZoneSize);
                    int y0 = Mathf.FloorToInt((rp.p.y - reach) / RoadConstants.ZoneSize);
                    int y1 = Mathf.CeilToInt((rp.p.y + reach) / RoadConstants.ZoneSize);
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                            candidates.Add(new Vector2s(x, y));
                }
            }
        }
        finally
        {
            m_roadCacheLock.ExitReadLock();
        }

        var zones = new HashSet<Vector2s>();
        foreach (Vector2s zone in candidates)
            if (GetRoadPointsInZone(zone).Count > 0)
                zones.Add(zone);
        return zones;
    }

    /// <summary>
    /// The surface of the road nearest (<paramref name="x"/>, <paramref name="z"/>) within <paramref name="reach"/>, or NaN
    /// where there is none: the finished ground a player stands on there, known before its zone loads (for aiming a
    /// teleport through cli_call; the generator's ground lies far over a road's cut).
    /// </summary>
    public static float RoadHeightAt(float x, float z, float reach) =>
        TryGetRoadHeightWithin(new Vector2(x, z), reach, out float height) ? height : float.NaN;

    /// <summary>The stored height of the nearest road point within <paramref name="reach"/>, without allocating a
    /// census; false when there is none.</summary>
    public static bool TryGetRoadHeightWithin(Vector2 position, float reach, out float height) =>
        TryGetRoadHeightWithin(position, reach, out height, out _, out _);

    /// <param name="distance">How far the nearest road point is.</param>
    /// <param name="width">That point's road width.</param>
    public static bool TryGetRoadHeightWithin(Vector2 position, float reach, out float height, out float distance, out float width)
    {
        height = 0f;
        distance = float.PositiveInfinity;
        width = 0f;
        if (!m_initialized || reach < 0) return false;
        float best = reach * reach;
        bool found = false;
        int me = CrossIslandReads.Me;
        byte skipped = 0;
        int radius = RingRadius(reach);
        Vector2i center = GetRoadGrid(position.x, position.y);
        {
            for (int x = -radius; x <= radius; x++)
                for (int y = -radius; y <= radius; y++)
                    if (m_roadPoints.TryGetValue(new Vector2i(center.x+x,center.y+y), out var points))
                        foreach (var point in points)
                        {
                            float d = (point.p-position).sqrMagnitude;
                            if (d <= best && point.IsRoad)
                            {
                                if (!CrossIslandReads.Sees(me, point.island)) { skipped = point.island; continue; }
                                best=d; height=point.h; width=point.w; found=true;
                            }
                        }
        }
        if (skipped != 0) CrossIslandReads.Skipped(CrossIslandReads.Kind.Points, skipped, position);
        if (found) distance = Mathf.Sqrt(best);
        return found;
    }

    /// <param name="minHeight">Only road whose surface stands this high or higher ...</param>
    /// <param name="maxHeight">... and this high or lower.</param>
    /// <param name="painted">Only painted road: not the unpainted ground levelled under a structure's start
    /// (<see cref="RoadPoint.unpainted"/>: a staircase's start pad, the ground under a deck's end).</param>
    public static bool TryGetRoadWithin(Vector2 position, float reach, out Vector2 nearest, System.Func<Vector2, bool>? allowed = null,
        float minHeight = float.NegativeInfinity, float maxHeight = float.PositiveInfinity, bool painted = false)
    {
        nearest = position;
        if (!m_initialized || reach < 0) return false;
        float best = reach * reach;
        bool found = false;
        int me = CrossIslandReads.Me;
        byte skipped = 0;
        int radius = RingRadius(reach);
        Vector2i center = GetRoadGrid(position.x, position.y);
        {
            for (int x = -radius; x <= radius; x++)
                for (int y = -radius; y <= radius; y++)
                    if (m_roadPoints.TryGetValue(new Vector2i(center.x+x,center.y+y), out var points))
                        foreach (var point in points)
                        {
                            float distance = (point.p-position).sqrMagnitude;
                            if (distance <= best && !point.cutOnly && !(painted && point.unpainted) && point.h >= minHeight && point.h <= maxHeight)
                            {
                                if (!CrossIslandReads.Sees(me, point.island)) { skipped = point.island; continue; }
                                if (allowed == null || allowed(point.p)) { best=distance; nearest=point.p; found=true; }
                            }
                        }
        }
        if (skipped != 0) CrossIslandReads.Skipped(CrossIslandReads.Kind.Points, skipped, position);
        return found;
    }

    /// <summary>The ground a road meeting the network is judged on (at grade, arriving level, branching off a trunk): the
    /// ground the game renders (<see cref="BiomeBlendedHeight"/>), which the roads are planned and their junctions measured
    /// over. One owner for the join search (RoadPathfinder) and the trunk branch (RoadNetworkGenerator.TrunkBranch): the
    /// search read the world generator's raw height, which differs near biome borders.</summary>
    internal static float JunctionGround(Vector2 p, WorldGenerator world) => BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world);

    /// <summary>
    /// The nearest road point within <paramref name="reach"/> whose surface is within
    /// <paramref name="maxStep"/> of <paramref name="groundHere"/>, the ground a road arriving here
    /// stands on: a junction a road can join at grade. One on a fill or in a cut beside it is not
    /// (a junction at 896,4416 created a large earthwork ridge that
    /// looked bad: junctions are planned where the terrain can support them).
    /// </summary>
    public static bool TryGetRoadAtGradeWithin(Vector2 position, float reach, float groundHere, float maxStep, out Vector2 nearest,
        System.Func<Vector2, bool>? allowed = null)
    {
        nearest = position;
        if (!m_initialized || reach < 0) return false;
        float best = reach * reach;
        bool found = false;
        int me = CrossIslandReads.Me;
        byte skipped = 0;
        int radius = RingRadius(reach);
        Vector2i center = GetRoadGrid(position.x, position.y);
        {
            for (int x = -radius; x <= radius; x++)
                for (int y = -radius; y <= radius; y++)
                    if (m_roadPoints.TryGetValue(new Vector2i(center.x+x,center.y+y), out var points))
                        foreach (var point in points)
                        {
                            float distance = (point.p-position).sqrMagnitude;
                            if (distance <= best && point.IsRoad && Mathf.Abs(point.h - groundHere) <= maxStep)
                            {
                                if (!CrossIslandReads.Sees(me, point.island)) { skipped = point.island; continue; }
                                if (allowed == null || allowed(point.p)) { best=distance; nearest=point.p; found=true; }
                            }
                        }
        }
        if (skipped != 0) CrossIslandReads.Skipped(CrossIslandReads.Kind.Points, skipped, position);
        return found;
    }

    public static List<RoadPoint> GetRoadPointsNearPosition(Vector3 worldPos, float radius)
    {
        List<RoadPoint> result = new List<RoadPoint>();
        int me = CrossIslandReads.Me;
        byte skipped = 0;
        
        if (!m_initialized)
            return result;

        Vector2 pos2D = new Vector2(worldPos.x, worldPos.z);
        float radiusSq = radius * radius;

        int cellRadius = Mathf.CeilToInt(radius / GridSize) + 1;
        Vector2i centerGrid = GetRoadGrid(worldPos.x, worldPos.z);

        {
            for (int dy = -cellRadius; dy <= cellRadius; dy++)
            {
                for (int dx = -cellRadius; dx <= cellRadius; dx++)
                {
                    Vector2i checkGrid = new Vector2i(centerGrid.x + dx, centerGrid.y + dy);
                    if (m_roadPoints.TryGetValue(checkGrid, out var points))
                    {
                        foreach (var rp in points)
                        {
                            if ((rp.p - pos2D).sqrMagnitude <= radiusSq)
                            {
                                if (CrossIslandReads.Sees(me, rp.island)) result.Add(rp);
                                else skipped = rp.island;
                            }
                        }
                    }
                }
            }
        }

        if (skipped != 0) CrossIslandReads.Skipped(CrossIslandReads.Kind.Points, skipped, pos2D);
        result.Sort((a, b) => (a.p - pos2D).sqrMagnitude.CompareTo((b.p - pos2D).sqrMagnitude));
        return result;
    }

    #region ZDO Persistence

    /// <summary>
    /// Every stored road point, copied out under the read lock. Unordered and
    /// without the road each belongs to: the grid keeps a set per cell.
    /// </summary>
    public static List<RoadPoint> SnapshotAllRoadPoints()
    {
        m_roadCacheLock.EnterReadLock();
        try
        {
            var all = new List<RoadPoint>();
            foreach (var cell in m_roadPoints.Values) all.AddRange(cell);
            return all;
        }
        finally
        {
            m_roadCacheLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Serialize the entire road network to a byte array for global persistence.
    /// Version 4: [4][geometry encoding 2-6][the old geometry payload after its version]
    /// [known-length byte 0 or 1][committed planned-path length:float]. Geometry/hash inputs are unchanged.
    /// Older Roads readers reject v4; this reader also accepts legacy v1-v3 with unknown nonempty length.
    /// Geometry encodings 4 (the cut-only flag), 5 (the into-site flag) and 6 (the unpainted flag) occur only inside the v4
    /// envelope. Encodings 3 and up carry the append block ([count][parent ids]); under encodings 4 to 6 it is written even
    /// when empty (count 0).
    /// </summary>
    public static byte[]? SerializeAllRoadPoints()
    {
        m_roadCacheLock.EnterReadLock();
        try
        {
            // Zero cells is a saved network too. Writing its header replaces
            // an older nonempty blob and distinguishes it from no saved data.
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            
            // Version 2 adds the paint-only flag per point (waded fords), 3 the
            // append id, 4 the cut-only flag (staircase earthwork), 5 the
            // into-site flag (a road onto a place's landing), 6 the unpainted
            // flag (the road a structure stands on past its stairs). Each is
            // written only when the network needs it, so a road-only network
            // saves exactly as before.
            bool appended = s_appendParents.Count > 0;
            bool cuts = false, intoSite = false, unpainted = false;
            foreach (var cell in m_roadPoints.Values)
                foreach (var rp in cell)
                {
                    cuts |= rp.cutOnly;
                    intoSite |= rp.intoSite;
                    unpainted |= rp.unpainted;
                }
            int format = unpainted ? 6 : intoSite ? 5 : cuts ? 4 : appended ? 3 : 2;
            writer.Write(4);
            writer.Write(format);
            
            // Cells in a fixed order, so the saved bytes depend on the network only:
            // the grid's own order is its hash table's, and islands fill it in parallel.
            var ordered = new List<KeyValuePair<Vector2i, RoadPoint[]>>(m_roadPoints);
            ordered.Sort((a, b) => a.Key.x != b.Key.x ? a.Key.x.CompareTo(b.Key.x) : a.Key.y.CompareTo(b.Key.y));
            writer.Write(ordered.Count);
            
            foreach (var kvp in ordered)
            {
                writer.Write(kvp.Key.x);
                writer.Write(kvp.Key.y);
                
                writer.Write(kvp.Value.Length);
                foreach (var rp in kvp.Value)
                {
                    writer.Write(rp.p.x);
                    writer.Write(rp.p.y);
                    writer.Write(rp.w);
                    writer.Write(rp.h);
                    writer.Write(rp.paintOnly);
                    if (format >= 4) writer.Write(rp.cutOnly);
                    if (format >= 5) writer.Write(rp.intoSite);
                    if (format >= 6) writer.Write(rp.unpainted);
                    if (format >= 3) writer.Write(rp.addition);
                }
            }
            
            if (format >= 3)
            {
                writer.Write(s_appendParents.Count);
                foreach (int parent in s_appendParents) writer.Write(parent);
            }
            writer.Write((byte)(m_committedPathLength.HasValue?1:0));
            writer.Write(m_committedPathLength ?? 0f);
            return ms.ToArray();
        }
        finally
        {
            m_roadCacheLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Why the last <see cref="DeserializeAllRoadPoints"/> refused its data: the envelope and
    /// geometry encoding it read, and the check that failed or the exception that stopped it.
    /// Null after a successful read. The unreadable-save error and a refused client
    /// snapshot name it, so a refusal says what this build could not read.
    /// </summary>
    public static string? LastReadRefusal { get; private set; }

    /// <summary>
    /// Deserialize the entire road network from a byte array.
    /// Clears existing data and replaces with loaded data.
    /// False (and <see cref="LastReadRefusal"/> says why) when the data is refused.
    /// </summary>
    public static bool DeserializeAllRoadPoints(byte[] data)
    {
        LastReadRefusal = null;
        int envelope = 0, version = 0;
        bool Refuse(string why)
        {
            LastReadRefusal = $"envelope {envelope}, encoding {version}: {why}";
            return false;
        }
        if (data == null || data.Length == 0)
            return Refuse("no data");

        // Left open by the reader, so a refusal can say how far the read got.
        var ms = new MemoryStream(data);
        try
        {
            using var reader = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            envelope=reader.ReadInt32();
            version=envelope==4 ? reader.ReadInt32() : envelope;
            if ((envelope==4 && (version < 2 || version > 6)) || (envelope!=4 && (version < 1 || version > 3)))
                return Refuse("a format this build does not know (it reads envelope 4 with encodings 2-6, and formats 1-3 bare)");
            
            int cellCount = reader.ReadInt32();
            if (cellCount < 0 || cellCount > 1000000)
                return Refuse($"cell count {cellCount} out of range");
            
            var loadedPoints = new Dictionary<Vector2i, RoadPoint[]>(cellCount);
            int totalPoints = 0;
            // A released build (1.4.x, format 1) clamped widths and saved
            // averaged heights, but never checked them. One bad point there
            // costs that point, not the world its whole network.
            bool released = envelope == 1;
            int dropped = 0;
            
            for (int c = 0; c < cellCount; c++)
            {
                int gridX = reader.ReadInt32();
                int gridY = reader.ReadInt32();
                int pointCount = reader.ReadInt32();
                
                if (pointCount < 0 || pointCount > 100000)
                    return Refuse($"point count {pointCount} at cell ({gridX},{gridY}) out of range");
                
                var points = new List<RoadPoint>(pointCount);
                for (int i = 0; i < pointCount; i++)
                {
                    float px = reader.ReadSingle();
                    float py = reader.ReadSingle();
                    float w = reader.ReadSingle();
                    float h = reader.ReadSingle();
                    bool paintOnly = version >= 2 && reader.ReadBoolean();
                    bool cutOnly = version >= 4 && reader.ReadBoolean();
                    bool intoSite = version >= 5 && reader.ReadBoolean();
                    bool unpainted = version >= 6 && reader.ReadBoolean();
                    int addition = version >= 3 ? reader.ReadInt32() : 0;
                    if (!ManualRoadDraft.IsFinite(px) || !ManualRoadDraft.IsFinite(py) ||
                        !ManualRoadDraft.IsFinite(w) || w <= 0 || !ManualRoadDraft.IsFinite(h) || addition < 0)
                    {
                        if (!released)
                            return Refuse($"point {i} of cell ({gridX},{gridY}) has position ({px},{py}), width {w}, height {h}, " +
                                          $"addition {addition}: a non-finite value, no width or a negative addition");
                        dropped++;
                        continue;
                    }
                    points.Add(new RoadPoint(new Vector2(px, py), w, h, paintOnly, addition, cutOnly, intoSite, unpainted));
                }
                
                var key=new Vector2i(gridX,gridY);
                if (points.Count == 0 && pointCount > 0) continue;   // every point of the cell was dropped
                if (loadedPoints.TryGetValue(key, out var earlier))
                {
                    // A released build read a cell listed twice as one cell (it filed each point under its grid
                    // cell), so a released blob keeps both lists; in this build's own formats a repeat is damage.
                    if (!released) return Refuse($"cell ({gridX},{gridY}) appears twice");
                    points.InsertRange(0, earlier);
                }
                loadedPoints[key]=points.ToArray();
                totalPoints=checked(totalPoints+points.Count - (earlier?.Length ?? 0));
            }
            
            var parents = new List<int>();
            if (version >= 3)
            {
                // Encoding 3 is written only for a network with appends, so its block holds at
                // least one. Encoding 4 is written for any network with a staircase's cuts and
                // carries the block whether or not anything was appended: empty is valid there.
                int count = reader.ReadInt32();
                int least = version >= 4 ? 0 : 1;
                if (count < least || count > ManualRoadDraft.MaxAppends)
                    return Refuse($"append block holds {count} parent(s) ({least} to {ManualRoadDraft.MaxAppends} expected)");
                for (int i = 0; i < count; i++)
                {
                    int parent = reader.ReadInt32();
                    if (parents.Contains(parent)) return Refuse($"append parent {parent} appears twice");
                    parents.Add(parent);
                }
                foreach (var cell in loadedPoints.Values)
                    foreach (var point in cell)
                        if (point.addition > count) return Refuse($"a point names append {point.addition} of {count}");
            }
            float? length=totalPoints==0 ? 0f : (float?)null;
            if(envelope==4)
            {
                byte known=reader.ReadByte();
                float metres=reader.ReadSingle();
                bool canonicalZero=BitConverter.ToInt32(BitConverter.GetBytes(metres),0)==0;
                if(known>1 || !ManualRoadDraft.IsFinite(metres) || metres<0 || (known==0 && !canonicalZero)
                    || (totalPoints==0 && (known!=1 || !canonicalZero)))
                    return Refuse($"planned-path length record (known {known}, {metres} m) does not fit {totalPoints} point(s)");
                length=known==1 ? metres : (float?)null;
            }
            if (ms.Position != ms.Length) return Refuse($"{ms.Length - ms.Position} byte(s) left over after the network");
            int worldSeed=WorldGenerator.instance?.GetSeed() ?? 0;
            var cells = NewCells(loadedPoints);
            m_roadCacheLock.EnterWriteLock();
            try
            {
                int networkVersion=ComputeRoadNetworkVersion(cells,worldSeed);
                s_appendParents=parents;
                s_narrowestFiled = 0f;
                m_roadPoints = cells;
                m_initialized = true;
                GridCellsWithRoads = loadedPoints.Count;
                m_storedPointRecords=totalPoints;
                m_committedPathLength=length;
                RoadNetworkVersion=networkVersion;
                LoadedFromReleasedFormat=released;
            }
            finally
            {
                m_roadCacheLock.ExitWriteLock();
            }
            
            if (dropped > 0)
                Log.LogWarning($"The saved road network (format 1, from a released build) held {dropped} point(s) with a " +
                               "non-finite position or height, or no width; they were left out and the rest of the network loaded.");
            Log.LogDebug($"Deserialized {cellCount} grid cells, {totalPoints} stored records");
            return true;
        }
        catch (System.Exception ex)
        {
            return Refuse($"{ex.GetType().Name} at byte {ms.Position} of {data.Length}: {ex.Message}");
        }
    }

    #endregion
}
