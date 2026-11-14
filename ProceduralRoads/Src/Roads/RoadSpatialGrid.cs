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
        /// <summary>Into a location: a road planned into a location (<see cref="location"/>): its last stretch onto the landing in front
        /// of a location (RoadLocationEntry), its edge carried on, or its points inside its own location's footprint. The terrain writer
        /// levels and paints toward these points inside their own location's footprint, where no other location's footprint lies
        /// (RoadLocationProtection.Admits), which it leaves alone for every other point, and only within
        /// <see cref="RoadLocationEntry.InsideReach"/> of them there.</summary>
        public bool intoLocation;
        /// <summary>The location an into-location point was planned into, by its identity (RoadLocationProtection.Footprint.Is): the
        /// plan's target (RoadLocationProtection.Targets), set when it is stored (<see cref="AddPlan"/>) and bound again to the
        /// world's location by that identity when a save is read (LocationPrefabLevelling.LocationOf). NaN (<see cref="NoLocation"/>) where an into-location point has none or a save's location
        /// is not found: such a point is let into no footprint. Meaningful only with <see cref="intoLocation"/> (zero
        /// without).</summary>
        public Vector2 location;
        /// <summary>The location identity carried by the saved point, independent of whether that location still exists.
        /// Hashing, ordering and saving use this identity. Footprint admission uses <see cref="location"/>.</summary>
        internal Vector2 savedLocation;
        /// <summary>Unpainted: road, levelled like any other point, but never painted. The road a structure's end stairs
        /// stand on past their foot (StructureEnds.Unpainted): levelled so the stairs stand on level ground, while the paint
        /// stops at the bottom step; and a staircase's start pad. Everything that asks where the road is counts these points
        /// as road.</summary>
        public bool unpainted;
        /// <summary>A mound: ground, not road. The ground under a structure's limiting pier or stair footing raised to its
        /// top (StructureGround.Mound), stored with its road: the terrain writer levels toward it as toward a road point and
        /// paints none of it, and every finished-ground reader reads it (<see cref="Levelled"/>), but it is not walked
        /// (<see cref="Walked"/>: it clears no vegetation), not road (<see cref="IsRoad"/>: no rock headroom, no junction,
        /// no road a location or a mark is reached by), as a cut is not. Never with another kind.</summary>
        public bool mound;
        /// <summary>The island worker that committed it (<see cref="CrossIslandReads.Tag"/>): another island's worker does not
        /// see it. 0 for none. Not saved.</summary>
        public int island;

        public RoadPoint(Vector2 position, float width, float height, bool paintOnly = false, int addition = 0, bool cutOnly = false, bool intoLocation = false,
            bool unpainted = false, Vector2? location = null, bool mound = false)
        {
            p = position;
            w = width;
            w2 = width * width;
            h = height;
            this.paintOnly = paintOnly;
            this.addition = addition;
            this.cutOnly = cutOnly;
            this.intoLocation = intoLocation;
            this.unpainted = unpainted;
            this.mound = mound;
            this.location = location ?? (intoLocation ? NoLocation : default);
            savedLocation = this.location;
            island = 0;
        }

        /// <summary>No location (<see cref="location"/>).</summary>
        public static readonly Vector2 NoLocation = new Vector2(float.NaN, float.NaN);

        /// <summary>A point a road runs through: not a ford's paint-only point, not a stair's cut, not a mound.</summary>
        public bool IsRoad => Levelled && !mound;
        /// <summary>A point the terrain writer levels the ground toward (RoadTerrainModifier.ModifyVertexHeights, and the
        /// finished ground it leaves): road, unpainted road and a mound; not a ford's paint-only point (the riverbed kept),
        /// not a stair's cut (applied on its own).</summary>
        public bool Levelled => !paintOnly && !cutOnly;
        /// <summary>A point a player walks on: road, unpainted road and a waded ford's paint-only point; not a cut (a
        /// staircase's or crossing's earthwork) nor a mound, which change the ground and are not walked. The one test of
        /// walking space for vegetation (RoadClearAreaManager) and rock (RockClearances.Near), of the network a location or a mark is reached by
        /// (RoadNetworkGenerator.Reached) and of where a road is (<see cref="TryGetRoadWithin"/>, <see cref="AnyRoadAt"/>).</summary>
        public bool Walked => !cutOnly && !mound;
        /// <summary>What the point is, as the network's identity counts it: road 0, paint-only 1, cut-only 2, into a location 3,
        /// unpainted 4, a mound 5.</summary>
        public int Kind => paintOnly ? 1 : cutOnly ? 2 : intoLocation ? 3 : unpainted ? 4 : mound ? 5 : 0;
    }

    public const float GridSize = RoadConstants.SpatialGridSize;

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

    /// <summary>The widest road point stored (<see cref="NoteFiled"/>, a save read): how far round a road point another
    /// may reach it in the terrain writer's fit (<see cref="SurfaceReader"/>). Raised before the points it covers are
    /// published.</summary>
    private static float s_widestFit = 0f;

    private static void NoteFiled(Dictionary<Vector2i, List<RoadPoint>> cells)
    {
        float narrowest = s_narrowestFiled, widest = s_widestFit;
        foreach (var points in cells.Values)
            foreach (var point in points)
            {
                if (!point.cutOnly && point.w < narrowest) narrowest = point.w;
                if (point.IsRoad && point.w > widest) widest = point.w;
            }
        s_narrowestFiled = narrowest;
        s_widestFit = widest;
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
    /// released build (1.4.x) can read. What may be done to such a network is
    /// decided by RoadNetworkPersistence.AllowsNetworkChanges: it is never written back
    /// in a newer format, so the world still opens in the build that made it.
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
            s_appendReach.Clear();
            m_initialized = false;
            m_storedPointRecords = 0;
            lock (m_recordGate) Junctions.Clear();
            GridCellsWithRoads = 0;
            s_narrowestFiled = float.PositiveInfinity;
            s_widestFit = 0f;
            m_committedPathLength = 0f;
            RoadNetworkVersion = 0;
            lock (m_recordGate) m_debugInfo.Clear();
            System.Threading.Interlocked.Exchange(ref locationRefusalSamples, 0);
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
        /// <summary>Every point a mound's (<see cref="RoadPoint.mound"/>, StructureGround.Mound.Plan): stored as ground, not
        /// road.</summary>
        public bool Mound;
        /// <summary>Its start, or its end, is held at a location's ground and ends on a level landing
        /// (<see cref="LocationEnds"/>, RoadPitches.ApplyJunctionLanding).</summary>
        public bool LocationLandingStart, LocationLandingEnd;
        /// <summary>The staircase its start, or its end, was planned to meet, and whether that is the staircase's To end
        /// (RoadNetworkGenerator.PlanPieces: a road's own staircase; RoadNetworkGenerator.LocationApproaches and PlanEntryFrom:
        /// an approach or an entry laid from the road's end); kept by <see cref="Slice"/> only where that end is kept. The
        /// one record of which road meets a staircase's end (StairRun.FromRoad, StairRun.ToRoad, which keep and save that road's
        /// points: StairRoad). Generation only.</summary>
        internal (RoadStairSpan Stair, bool AtTo)? StairAtStart, StairAtEnd;
        /// <summary>What its profile was limited under (RoadPitches.AcceptLanding), per point: the schedule it was accepted
        /// on, each edge's limit (its turn landings, its pitches and eases, the landings at the ends it lands; null: the cap on
        /// every edge), and the points held at another road's or a structure's height (null: none); and the ends it lands on.
        /// Kept by <see cref="Slice"/> (an end's landing only where that end is kept); a landing laid at its end once it is
        /// planned (RoadPitches.LandEnd) is judged on these, never on its slopes, keeps the schedule with only its own landing
        /// added, and is kept on them with the heights it leaves. Generation only.</summary>
        internal List<float>? Grades;
        internal List<bool>? Held;
        internal (bool start, bool end) Landed;
        /// <summary>The locations it was planned between (PlanRoadPath's locations): what its points are stored into (<see cref="AddPlan"/>, <see cref="RoadPoint.location"/>). Kept by
        /// <see cref="Slice"/>. Default: none.</summary>
        internal RoadLocationProtection.Targets Locations;

        internal PlannedPath(List<Vector2> points, List<float> heights,
            List<RoadPointDebugInfo> debugInfos, float width, float totalLength, bool followTerrain)
        {
            Points = points; Heights = heights; DebugInfos = debugInfos;
            Width = width; TotalLength = totalLength; FollowTerrain = followTerrain;
        }

        /// <summary>The road's width at point <paramref name="i"/>: its own where a switchback's turn set one
        /// (<see cref="Widths"/>), else <see cref="Width"/>. What it is stored at (<see cref="AddPlan"/>) and its earthwork is
        /// measured across (RoadNetworkGenerator.EarthworkAcross).</summary>
        internal float WidthAt(int i) => Widths != null && i < Widths.Count ? Widths[i] : Width;

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
                Mound = Mound,
                LocationLandingStart = LocationLandingStart && from == 0, LocationLandingEnd = LocationLandingEnd && to == Points.Count - 1,
                StairAtStart = from == 0 ? StairAtStart : null, StairAtEnd = to == Points.Count - 1 ? StairAtEnd : null,
                Grades = Range(Grades, from, n), Held = Range(Held, from, n),
                Landed = (Landed.start && from == 0, Landed.end && to == Points.Count - 1),
                Locations = Locations,
            };
            slice.Junctions = Junction.On(Junctions, at => slice.Points.Exists(q => (q - at).sqrMagnitude < 1f));
            return slice;
        }

        /// <summary>A copy of it, every field its own (its lists copied, what they hold shared: values, and the staircases its
        /// ends meet): what a remembered piece hands out (RoadSpatialGrid.RemembersPlans), so no caller edits another's.</summary>
        internal PlannedPath Copy() =>
            new(new List<Vector2>(Points), new List<float>(Heights), new List<RoadPointDebugInfo>(DebugInfos), Width, TotalLength, FollowTerrain)
            {
                Widths = Widths == null ? null : new List<float>(Widths),
                Junctions = Junctions == null ? null : new List<Junction>(Junctions),
                Unpainted = Unpainted == null ? null : new List<bool>(Unpainted),
                Mound = Mound,
                LocationLandingStart = LocationLandingStart, LocationLandingEnd = LocationLandingEnd,
                StairAtStart = StairAtStart, StairAtEnd = StairAtEnd,
                Grades = Grades == null ? null : new List<float>(Grades),
                Held = Held == null ? null : new List<bool>(Held),
                Landed = Landed,
                Locations = Locations,
            };

        /// <summary><paramref name="count"/> of a per-point list from <paramref name="from"/>, or null where it is not one
        /// entry a point.</summary>
        internal List<T>? Range<T>(List<T>? list, int from, int count) =>
            list != null && list.Count == Points.Count ? list.GetRange(from, count) : null;
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
    /// water and locations (the legs are the route the search already judged).
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
        System.Func<Vector2, float>? terrainHeight = null, RoadLocationProtection.Targets locations = default) =>
        PlanRoadPath(path, width, worldGen, startGround, endGround, followTerrain, minHeight, terrainHeight, checkLocations: true, locations);

    // Approach selection must be able to score an old route that grazes a
    // location, or it stops before looking for a safe replacement. This profile
    // is comparison-only; the chosen candidate still uses PlanRoadPath.
    internal static PlannedPath? PlanComparisonProfile(List<Vector2> path, float width, WorldGenerator worldGen,
        float? startGround, float? endGround, System.Func<Vector2, float> terrainHeight, RoadLocationProtection.Targets locations) =>
        PlanRoadPath(path, width, worldGen, startGround, endGround, false, float.NegativeInfinity, terrainHeight, checkLocations: false, locations);

    /// <summary>A road's last stretch onto a location's landing (RoadLocationEntry): every rule of a
    /// road but the location clearance, which it checks itself against the location's shape. Its end on the landing is held at
    /// the location's ground and ends on a level landing (<see cref="LocationEnds"/>).</summary>
    internal static PlannedPath? PlanLocationEntry(List<Vector2> path, float width, WorldGenerator worldGen,
        float startGround, float endGround, System.Func<Vector2, float> terrainHeight, RoadLocationProtection.Targets locations)
    {
        var was = LocationEnds;
        LocationEnds = (false, true);
        try { return PlanRoadPath(path, width, worldGen, startGround, endGround, false, float.NegativeInfinity, terrainHeight, checkLocations: false, locations); }
        finally { LocationEnds = was; }
    }

    /// <summary>The ends of the road being planned on this thread that are held at a location's ground (its start, its end):
    /// the road's own ends at a location (RoadNetworkGenerator.PlanPieces, from ApproachGround) and the landing end of the
    /// way onto a placed location's landing (<see cref="PlanLocationEntry"/>). Such an end, where its ground is given, ends on the
    /// level landing a join ends on (RoadPitches.ApplyJunctionLanding), which the pitches treat as a rest; where the climb
    /// then has too little room the road is refused (RoadGrade.Limit).</summary>
    [System.ThreadStatic] internal static (bool start, bool end) LocationEnds;
    /// <summary>The ends of the road being planned on this thread where a staircase starts (its start, its end): a
    /// staircase's foot the road is held at (RoadNetworkGenerator.PlanPieces), and the road's own end at its location where an
    /// approach staircase may be laid up from it once the road is planned (RoadNetworkGenerator.LocationApproaches, which levels
    /// the road's end there itself where that fits, RoadPitches.LandEnd). A foot held at its height ends on the level landing a join ends on
    /// (RoadPitches.ApplyJunctionLanding): a road's last 8 m at a pitch's grade ran into a staircase's foot. Where that landing
    /// does not fit, the road is refused (RoadPitches.Landing.NoFootLanding). So does the road's end where it starts again at a
    /// staircase's top it lays (its pad is level at the top's height, RoadEndRing.PadUnder). And no tight switchback narrows the road into either (RoadSwitchbacks.Shape).</summary>
    [System.ThreadStatic] internal static (bool start, bool end) StairStarts;
    /// <summary>Why a road is refused where the level landing at a staircase's foot, or at a top it starts again from, has no
    /// room within the grade cap (RoadPitches.Landing.NoFootLanding): a road never runs its own grade into a staircase's end
    /// (BRIEF-RULES 17).</summary>
    internal const string NoFootLandingRefusal = "grade: no room for a staircase foot's level landing within the cap";
    /// <summary>Why a road is refused where a climb cannot keep its rests within the grade cap (RoadPitches.PlacePitches): the
    /// road is not built.</summary>
    internal const string TooSteepForRestsRefusal = "grade: a climb too steep for its rests within the cap";
    private static int s_locationLandingsStored;
    /// <summary>Road ends held at a location's ground stored with a level landing this generation (<see cref="LocationEnds"/>).</summary>
    public static int LocationLandingsStored => s_locationLandingsStored;
    internal static void ResetLocationLandings() => s_locationLandingsStored = 0;

    private static PlannedPath? PlanRoadPathFresh(List<Vector2> path, float width, WorldGenerator worldGen,
        float? startGround, float? endGround, bool followTerrain, float minHeight,
        System.Func<Vector2, float>? terrainHeight, bool checkLocations, RoadLocationProtection.Targets locations)
    {
        var plan = PlanRoadPathShaped(path, width, worldGen, startGround, endGround, followTerrain, minHeight, terrainHeight, locations);
        if (checkLocations) plan = CheckLocationClearance(plan, path, locations);
        // The sway is cosmetic: the road is planned without it too, and the
        // swayed plan is kept only if it was built and moves no more earth.
        if (RoadWiggle.Enabled && !followTerrain && !SuppressWiggle && !ShapedBeforeCrossings)
        {
            string? why = LastRefusal;
            Vector2? refusedAt = RefusedAt;
            PlannedPath? plain;
            SuppressWiggle = true;
            try
            {
                plain = PlanRoadPathShaped(path, width, worldGen, startGround, endGround, followTerrain, minHeight, terrainHeight, locations);
                if (checkLocations) plain = CheckLocationClearance(plain, path, locations);
            }
            finally { SuppressWiggle = false; }
            float Ground(Vector2 q) => terrainHeight != null ? terrainHeight(q) : BiomeBlendedHeight.GetBlendedHeight(q.x, q.y, worldGen);
            // Earth is measured as every road's earthwork is: across the width, over the ground before roads (the plan's own
            // ground where the caller gave one), the plain plan once for every swayed plan compared with it.
            System.Func<Vector2, float> earthGround = terrainHeight ?? RoadNetworkGenerator.GroundBeforeRoads(worldGen);
            RoadNetworkGenerator.Earthwork plainEarth = plain != null ? RoadWiggle.EarthOf(plain, earthGround) : default;
            string? plainWhy = LastRefusal;
            bool refused = plan == null, moreEarth = !refused && plain != null && !RoadWiggle.NoMoreEarth(plan!, plainEarth, earthGround);
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
                    next.AddRange(RoadWiggle.MoreEarthAt(plan!, plainEarth, earthGround));
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
                    again = PlanRoadPathShaped(path, width, worldGen, startGround, endGround, followTerrain, minHeight, terrainHeight, locations);
                    refusedAt = RefusedAt;
                    if (checkLocations) { again = CheckLocationClearance(again, path, locations); if (again == null) refusedAt = RefusedAt; }
                }
                finally { RoadWiggle.HeldAt = null; }
                plan = again;
                refused = plan == null;
                moreEarth = !refused && !RoadWiggle.NoMoreEarth(plan!, plainEarth, earthGround);
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
        if (plan != null) plan.Locations = locations;
        return plan;
    }

    private static int locationRefusalSamples;

    private static PlannedPath? CheckLocationClearance(PlannedPath? plan, List<Vector2> input, RoadLocationProtection.Targets locations)
    {
        if (plan == null) return null;
        // Preserve legitimate trimmed arrivals at their location's clearance
        // edge. Only the input endpoints, at the locations they were planned
        // to (locations), can grant that margin allowance: snapping must not
        // turn a third location into an exempt destination.
        Vector2 start = input[0], end = input[input.Count - 1];
        for (int i = 1; i < plan.Points.Count; i++)
        {
            float width = plan.Widths != null ? Mathf.Max(plan.Widths[i - 1], plan.Widths[i]) : plan.Width;
            // The same owner and width every shaping step asked (RoadLocationProtection.BlocksRoad), with the location it hit.
            if (RoadLocationProtection.BlocksFinalSegment(plan.Points[i - 1], plan.Points[i], RoadLocationProtection.Clearance(width), start, end, locations, out var hit))
            {
                LastRefusal = "final road crosses a protected location";
                RefusedAt = (plan.Points[i - 1] + plan.Points[i]) * 0.5f;
                string location = hit.HasValue
                    ? System.FormattableString.Invariant($"location=({hit.Value.Centre.x:F2},{hit.Value.Centre.y:F2}) radius={hit.Value.Radius:F2}")
                    : "location=index unavailable";
                string detail = System.FormattableString.Invariant(
                    $"POI clearance refused trial: segment=({plan.Points[i - 1].x:F2},{plan.Points[i - 1].y:F2})->({plan.Points[i].x:F2},{plan.Points[i].y:F2}); {location}; width={width:F2}; input=({start.x:F2},{start.y:F2})->({end.x:F2},{end.y:F2}). Trial refusal, not necessarily a lost road.");
                // Keep ordinary server logs useful without printing thousands
                // of rejected sway/approach trials. Debug retains every one.
                if (System.Threading.Interlocked.Increment(ref locationRefusalSamples) <= 20) Log.LogInfo(detail);
                else Log.LogDebug(detail);
                return null;
            }
        }
        return plan;
    }

    [System.ThreadStatic] internal static bool SuppressWiggle;
    /// <summary>The road being planned, on this thread, is a searched road whose line was pulled and swayed before its crossings
    /// were found (RoadNetworkGenerator.PlanCandidate, a road through standing water): its pieces are planned on that line, not
    /// shaped again.</summary>
    [System.ThreadStatic] internal static bool ShapedBeforeCrossings;

    /// <summary>
    /// A road's line shaped before it is planned: pulled straight where the search's grid staircased it
    /// (<see cref="RoadPathPull.Pull"/>), then swayed (<see cref="RoadWiggle.Apply"/>, with <paramref name="sway"/>), each
    /// waypoint in <paramref name="held"/> kept where it is and never pulled past, nor one snapped onto a road. The one owner of
    /// shaping a road's line: a piece as it is planned, and a searched road through standing water before its crossings are
    /// found.
    /// </summary>
    internal static List<Vector2> PullAndSway(List<Vector2> path, float width, WorldGenerator worldGen, System.Func<Vector2, float> ground,
        List<Vector2>? held, RoadLocationProtection.Targets locations, bool sway)
    {
        bool Held(Vector2 p) => held != null && held.Exists(h => (h - p).sqrMagnitude < 0.25f);
        bool holds = held != null && path.Exists(Held);
        if (RoadPathPull.Enabled)
            path = RoadPathPull.Pull(path, width, ground, p => !RoadWater.BuiltDry(worldGen, p), RoadGrade.Configured + RoadConstants.SearchGradeMargin,
            anchor: RoadNetworkGenerator.RoadSnap > 0f ? p => TryGetRoadWithin(p, 0.5f, out _) || Held(p)
                : holds ? Held : (System.Func<Vector2, bool>?)null, locations: locations);
        // A held waypoint stays where it is through the sway's resampling, the sway held round it (a piece meeting a
        // structure's end sways everywhere else: unswayed, a road from a crossing ran dead straight for its whole piece).
        if (sway && RoadWiggle.Enabled)
            path = RoadWiggle.Apply(path, ground, p => !RoadWater.BuiltDry(worldGen, p), p => RoadBiome.At(worldGen, p), width,
                p => TryGetRoadWithin(p, RoadBeside.CorridorDistance(width), out _), holds ? path.FindAll(Held) : null, locations);
        return path;
    }

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
    /// <summary>Why a road is refused whose structure's levelled end lies on another road it is held to, more than a step off
    /// that road's surface (PlanRoadPathCore).</summary>
    internal const string LevelledOnARoadRefusal = "a structure's levelled end lies on another road at another height";
    /// <summary>Sway outcomes per planned road piece this generation (candidates included):
    /// kept (of them, kept with the sway held round the stretches at fault), dropped because the swayed
    /// plan was refused, dropped for moving more earth.</summary>
    internal static int SwayKept, SwayRefused, SwayMoreEarth, SwayKeptHeld;
    /// <summary>Where the last plan on this thread was refused, where the check that refused it says: a protected
    /// location's clearance or a switchback's turns. Null otherwise.</summary>
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
    /// a location's ground, a join), where the two share it (StructureEnds.SharesLanding, each within a walker's step of it) and
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
    internal static float JoinReach(float width) => width * 0.75f;

    /// <summary>
    /// The height a road planned along <paramref name="path"/> from <paramref name="startGround"/> stores at its first point,
    /// where nothing but that value decides it: no road already laid within <see cref="RoadProfile.Footprint(float)"/> of it (so it is neither
    /// joined, <see cref="JoinReach"/>, nor held to one), and no structure's end levelled (<see cref="LevelledEnds"/>), no
    /// waypoint held (<see cref="HeldPoints"/>) and no location's landing (<see cref="LocationEnds"/>) on this thread. Every
    /// shaping step keeps the first point where it is, and the grade limiter holds its height. Null where any of them could
    /// move it.
    /// </summary>
    internal static float? FirstHeight(List<Vector2> path, float width, float startGround)
    {
        if (path.Count < 2 || LevelledEnds != null || HeldPoints != null || LocationEnds != default) return null;
        if (m_initialized && TryGetRoadHeightWithin(path[0], RoadProfile.Footprint(width), out _)) return null;
        return startGround;
    }

    private static PlannedPath? PlanRoadPathShaped(List<Vector2> path, float width, WorldGenerator worldGen,
        float? startGround, float? endGround, bool followTerrain, float minHeight,
        System.Func<Vector2, float>? terrainHeight, RoadLocationProtection.Targets locations)
    {
        // A tight staircase switchback that cannot keep its legs off each
        // other's surface falls back to the ordinary turn, so the road is
        // not lost to the tighter geometry.
        bool tight = RoadSwitchbacks.TightSwitchbacks && !followTerrain;
        RoadSwitchbacks.LastFilleted = 0;
        var plan = PlanRoadPathCore(path, width, worldGen, tight, startGround, endGround, followTerrain, minHeight, terrainHeight, locations);
        if (plan == null && tight && LastRefusal == SwitchbackRefusal.LegsTooClose)
            plan = PlanRoadPathCore(path, width, worldGen, false, startGround, endGround, followTerrain, minHeight, terrainHeight, locations);
        // A minimum-radius fillet is cosmetic too: a road it fails keeps its ordinary bend arcs before it loses them all.
        if (plan == null && RoadSwitchbacks.LastFilleted > 0 && !followTerrain)
        {
            RoadSwitchbacks.SuppressFillets = true;
            try
            {
                plan = PlanRoadPathCore(path, width, worldGen, tight, startGround, endGround, followTerrain, minHeight, terrainHeight, locations);
                if (plan == null && tight && LastRefusal == SwitchbackRefusal.LegsTooClose)
                    plan = PlanRoadPathCore(path, width, worldGen, false, startGround, endGround, followTerrain, minHeight, terrainHeight, locations);
            }
            finally { RoadSwitchbacks.SuppressFillets = false; }
        }
        // Rounded bends are cosmetic: a road whose arcs fail a water, location or
        // turn-room check keeps its corners rather than being lost -- after
        // first trying the least arcs, which leave its corners by centimetres.
        if (plan == null && RoadSwitchbacks.Fallback && !followTerrain &&
            (LastRefusal == SwitchbackRefusal.CurveInWater || LastRefusal == SwitchbackRefusal.CurveCrossesLocation))
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
                    plan = PlanRoadPathCore(path, width, worldGen, false, startGround, endGround, followTerrain, minHeight, terrainHeight, locations);
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
                    plan = PlanRoadPathCore(path, width, worldGen, tight, startGround, endGround, followTerrain, minHeight, terrainHeight, locations);
                    if (plan == null && tight && LastRefusal == SwitchbackRefusal.LegsTooClose)
                        plan = PlanRoadPathCore(path, width, worldGen, false, startGround, endGround, followTerrain, minHeight, terrainHeight, locations);
                    if (plan != null) { Shaped(corners ? BendStage.Corners : BendStage.Least, why); break; }
                }
            }
            finally { RoadSwitchbacks.SuppressBends = false; RoadSwitchbacks.LeastBends = false; }
        }
        return plan;
    }

    private static PlannedPath? PlanRoadPathCore(List<Vector2> path, float width, WorldGenerator worldGen, bool tightTurns,
        float? startGround, float? endGround, bool followTerrain, float minHeight,
        System.Func<Vector2, float>? terrainHeight, RoadLocationProtection.Targets locations)
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
        float segmentLength = RoadStep(width);

        float totalLength = 0f;
        for (int i = 0; i < path.Count - 1; i++)
            totalLength += Vector2.Distance(path[i], path[i + 1]);

        // A held waypoint (a viaduct's end-stair foot, HeldPoints) is never pulled past, as one snapped onto a road.
        List<Vector2>? held = HeldPoints;
        bool Held(Vector2 p) => held != null && held.Exists(h => (h - p).sqrMagnitude < 0.25f);
        bool holds = held != null && path.Exists(Held);
        // The ends the road is planned between: every shaping step below keeps the line clear of protected locations as the
        // final check (CheckLocationClearance) will judge it, against these ends, the locations they arrive at (locations) and the
        // road's width (RoadLocationProtection.BlocksRoad).
        Vector2 planStart = path[0], planEnd = path[path.Count - 1];
        // A searched road shaped before its crossings were found (ShapedBeforeCrossings) is not shaped again.
        if (!followTerrain && !ShapedBeforeCrossings)
            path = PullAndSway(path, width, worldGen, SampleTerrain, held, locations, sway: !SuppressWiggle);

        // Order: every searched road's path was snapped onto existing road
        // before anything was planned on it (RoadNetworkGenerator.PlanSearchedPath);
        // the pull above keeps every snapped waypoint; the sway leaves shared
        // road alone. A last snap, by the same owner with the structure ends
        // held, catches what the location-approach smoothing and the sway's
        // resampling moved back off (measured: 126 m of road beside another
        // without it, 52 m with it), and snaps the lines no search found (an
        // approach's candidate, a staircase's end floor).
        // A snap that would bring the line into a location's clearance is not made.
        if (!followTerrain)
        {
            var snapped = RoadNetworkGenerator.SnapToNetwork(path, width, holds ? Held : null, locations)!;
            if (RoadLocationProtection.FirstBlockedLeg(snapped, width, null, planStart, planEnd, locations) < 0) path = snapped;
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
        // The spline swings a little past its waypoints' legs; where that would take it into a location's clearance the
        // road keeps to the legs themselves, which the steps above kept clear.
        if (turns == null && !followTerrain && RoadLocationProtection.FirstBlockedLeg(densePoints, width, null, planStart, planEnd, locations) >= 0)
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
                // The final check's own clearance (its width and ends): a fillet into a location's clearance is refused here,
                // where the road is planned again with the least arcs or its corners (PlanRoadPathShaped), not at the end.
                float legWidth = shapeWidths != null && shapeWidths.Count == densePoints.Count ? Mathf.Max(shapeWidths[i-1],shapeWidths[i]) : width;
                if (RoadLocationProtection.BlocksRoad(densePoints[i-1],densePoints[i],legWidth,planStart,planEnd,locations))
                {
                    LastRefusal = SwitchbackRefusal.CurveCrossesLocation;
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
        bool[]? heldPoints = null;
        (bool start, bool end) landedEnds = default;
        bool landStart = false, landEnd = false, locationStart = false, locationEnd = false;
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
            // A join's end, an end held at a location's ground (LocationEnds) and one held at a staircase's foot or top (StairStarts) end
            // on a level landing, or the road is refused.
            locationStart = LocationEnds.start && startGround.HasValue;
            locationEnd = LocationEnds.end && endGround.HasValue;
            landStart = joinedStart || locationStart || (StairStarts.start && startGround.HasValue);
            landEnd = joinedEnd || locationEnd || (StairStarts.end && endGround.HasValue);
            // A staircase's foot's landing is asked apart from a join's or a location's only to name the refusal where it has no room.
            bool stairOnlyStart = landStart && !joinedStart && !locationStart, stairOnlyEnd = landEnd && !joinedEnd && !locationEnd;
            // The one landing acceptance (RoadPitches.AcceptLanding), on the turn landings' limits and the ground the eases follow;
            // the schedule it accepts is kept on the plan, and a landing laid at its end once it is planned keeps it
            // (RoadPitches.LandEnd).
            float[]? turnGrades = edgeGrades;
            List<float>? pitchGround = FollowGround ? denseHeights : null;
            // The points on the surface of a road it meets: that road's own edges, and where a joined end's landing begins.
            bool[]? shared = OnMetRoads(densePoints, width);
            RoadPitches.Landing landing = RoadPitches.AcceptLanding(densePoints, finalHeights, (joinedStart || locationStart, joinedEnd || locationEnd),
                (stairOnlyStart, stairOnlyEnd), turnGrades, RoadGrade.Configured, pitchGround, null, out edgeGrades, out landedEnds,
                shared: shared);
            if (landing == RoadPitches.Landing.NoFootLanding)
            {
                Log.LogDebug($"Road profile refused: no room for a staircase foot's level landing within the {RoadGrade.Configured:P0} cap");
                LastRefusal = NoFootLandingRefusal;
                return null;
            }
            if (landing == RoadPitches.Landing.TooSteepForRests)
            {
                var climb = RoadPitches.LastTooSteepForRests;
                RefusedAt = climb != null && climb.Count > 0 ? climb[climb.Count / 2] : null;
                Log.LogDebug($"Road profile refused: a climb too steep for its rests within the {RoadGrade.Configured:P0} cap at {RefusedAt}");
                LastRefusal = TooSteepForRestsRefusal;
                return null;
            }
            if (landing == RoadPitches.Landing.TooSteep)
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
            // The cap, or a last-resort pitch's grade on a schedule with one (RoadPitches.LimitCap): each edge at its own limit.
            float cap = RoadPitches.LimitCap(RoadGrade.Configured, edgeGrades);
            for (int attempt = 0; ; attempt++)
            {
                for (int k = 0; k < own.Count; k++) finalHeights[k] = own[k];
                System.Array.Clear(heldAt, 0, heldAt.Length);
                var underFloor = new List<int>();
                junctions = HoldToNetwork(densePoints, finalHeights, width, cap, SampleTerrain, joinedStart, joinedEnd, heldAt,
                    letGo, edgeGrades, minHeight, underFloor);
                // The floor again (a raised ford's surface, dry land's floor): held to a road below it, or eased
                // down to one, a stretch beside a raised ford dipped under its deck. Before the grade is judged.
                ApplyFloor(finalHeights, minHeight);
                int worst = WorstOverLimit(densePoints, finalHeights, edgeGrades, cap, segmentLength);
                // A road it meets standing more than a step under this road's floor (a wade's bank beside dry land's road) is not
                // met: let go of as a step past the cap is, else the road is refused (BRIEF-RULES 17).
                if (worst < 0 && underFloor.Count > 0) worst = Mathf.Max(1, underFloor[0]);
                if (worst < 0) break;
                if (attempt >= HeldLetGoRounds || !LetGoAround(worst, heldAt, letGo) || LetGoOntoRoad(densePoints, letGo, width))
                {
                    LastRefusal = "held to a road over the grade cap";
                    Log.LogDebug(FormattableString.Invariant($"Road refused: held to a road it meets, a step climbs over its limit (cap {RoadGrade.Configured:P0}) at ({densePoints[worst].x:F1}, {densePoints[worst].y:F1}): {Mathf.Abs(finalHeights[worst] - finalHeights[worst - 1]) / Mathf.Max(1e-3f, Vector2.Distance(densePoints[worst - 1], densePoints[worst])):P1}, held {heldAt[worst - 1]}/{heldAt[worst]}"));
                    return null;
                }
            }
            heldPoints = heldAt;
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
                    // A point held to another road (HoldToNetwork: on its surface or within the ground the terrain writer blends
                    // between the two), put back more than a step off it: the levelled end meets that road at another height, a step
                    // between the two roads (seen 0.6 m beside a turned bridge's end, the apron in the other road's blend), and the
                    // piece is refused (BRIEF-RULES 17). A ride is not exempt: whether the road stores that stretch is decided later,
                    // on the moved heights; a landing the two share is StructureEnds' to reconcile first.
                    if (heldPoints != null && heldPoints[i] && !StairLayout.Walkable(finalHeights[i], target))
                    {
                        LastRefusal = LevelledOnARoadRefusal;
                        RefusedAt = densePoints[i];
                        Log.LogDebug(FormattableString.Invariant($"Road refused: a structure's levelled end at {target:F2} lies on a road at {finalHeights[i]:F2} at ({densePoints[i].x:F1}, {densePoints[i].y:F1})"));
                        return null;
                    }
                    finalHeights[i] = target;
                    moved[i] = any = true;
                }
            }
            // Judged only where it was put back: the steps into and along the levelled stretch, each against its own limit.
            if (any && WorstOverLimit(densePoints, finalHeights, edgeGrades, RoadPitches.LimitCap(RoadGrade.Configured, edgeGrades), segmentLength, moved) >= 0)
            {
                LastRefusal = "a structure's end cannot be met within the grade cap";
                Log.LogDebug("Road refused: a structure's levelled end cannot be met within its edges' grade limits");
                return null;
            }
        }

        if (!followTerrain && turns != null && landings != null && landings.Contains(true) && !RoadSwitchbacks.Separated(densePoints, finalHeights, width,
                RoadSwitchbacks.StairTurns || tightTurns ? landings : null, surfaceOnly: RoadSwitchbacks.StairTurns || tightTurns, widths: shapeWidths))
        { LastRefusal = SwitchbackRefusal.LegsTooClose; Log.LogDebug("Road refused: switchback legs blend at different heights"); return null; }
        // Held: at a road it meets (HoldToNetwork) and along a structure's levelled end (LevelledEnds).
        List<bool>? heldOnPlan = heldPoints != null && System.Array.IndexOf(heldPoints, true) >= 0 ? new List<bool>(heldPoints) : null;
        if (!followTerrain && LevelledEnds is { } levelled && densePoints.Count > 1)
            foreach (var (atStart, _) in LevelledOn(levelled, densePoints, pathTotal, startGround, endGround))
                for (int i = 0; i < densePoints.Count; i++)
                {
                    float fromEnd = atStart ? distanceFromStart[i] : pathTotal - distanceFromStart[i];
                    if (fromEnd > StructureEnds.ApronLength + StructureEnds.JoinOverlap + 1e-3f) continue;
                    heldOnPlan ??= new List<bool>(new bool[densePoints.Count]);
                    heldOnPlan[i] = true;
                }
        return new PlannedPath(densePoints, finalHeights, debugInfos, width, totalLength, followTerrain)
        {
            Widths = shapeWidths, Junctions = junctions,
            Grades = edgeGrades == null ? null : new List<float>(edgeGrades), Held = heldOnPlan, Landed = landedEnds,
            LocationLandingStart = locationStart && !joinedStart && RoadPitches.JunctionLanding > 0f,
            LocationLandingEnd = locationEnd && !joinedEnd && RoadPitches.JunctionLanding > 0f,
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
    /// <param name="intoLocation">A road carried on into a location's footprint onto its landing: its points level and paint there,
    /// in that location alone (its target, <see cref="PlannedPath.Locations"/>). A road's points inside its own end's or start's
    /// footprint are stored into it whatever this says: the only road there is the location's own, carried on over the ground
    /// it only paints (<see cref="RoadNetworkGenerator.KeepOutEdge"/>), and the terrain writer leaves every other point
    /// there alone (<see cref="AddPlan"/>).</param>
    public static void Commit(PlannedPath plan, int addition = 0, bool intoLocation = false)
    {
        Dictionary<Vector2i, List<RoadPoint>> tempPoints = new Dictionary<Vector2i, List<RoadPoint>>();
        AddPlan(tempPoints, plan, addition, intoLocation);
        if (RecordPointDebugInfo)
            for (int i = 0; i < plan.Points.Count; i++)
            {
                RoadPointDebugInfo debugInfo = plan.DebugInfos[i];
                debugInfo.SmoothedHeight = plan.Heights[i];
                lock (m_recordGate) m_debugInfo[plan.Points[i]] = debugInfo;
            }

        if (plan.LocationLandingStart) System.Threading.Interlocked.Increment(ref s_locationLandingsStored);
        if (plan.LocationLandingEnd) System.Threading.Interlocked.Increment(ref s_locationLandingsStored);
        MergePoints(tempPoints,plan.TotalLength,true);
        if (plan.Junctions != null && SearchStats.Enabled) lock (m_recordGate) Junctions.AddRange(plan.Junctions);
        RoadNetworkGenerator.IslandActivity?.Committed(plan.TotalLength);
    }

    /// <summary>A plan's points as road points, the one loop a generated road (<see cref="Commit"/>) and a manual road
    /// (<see cref="CommitAppend"/>) are stored by: each point at its own width (a switchback's turns are wider), and each
    /// stored into the location it was planned into (<see cref="RoadPoint.location"/>), by the plan's targets
    /// (<see cref="PlannedPath.Locations"/>): every point of a road carried on into a location (<paramref name="intoLocation"/>) into
    /// that location, and another road's point inside its own end's or start's footprint into that one. A point inside any
    /// other location's footprint is stored into none: the terrain writer leaves that location's ground alone.</summary>
    private static void AddPlan(Dictionary<Vector2i, List<RoadPoint>> into, PlannedPath plan, int addition, bool intoLocation)
    {
        foreach (RoadPoint rp in PointsOf(plan, intoLocation))
            AddRoadPoint(into, rp.p, rp.w, rp.h, rp.paintOnly, addition, intoLocation: rp.intoLocation, location: rp.intoLocation ? rp.location : null,
                unpainted: rp.unpainted, mound: rp.mound);
    }

    /// <summary>A plan's points as they are stored (<see cref="AddPlan"/>), <paramref name="intoLocation"/> as a road into its
    /// location: each its width and height, a waded stretch paint only, and into the location it is planned into. Not stored: what
    /// reads a road's write before it is (RoadNetworkGenerator.UnderAPlannedStructure) reads these.</summary>
    internal static List<RoadPoint> PointsOf(PlannedPath plan, bool intoLocation)
    {
        var points = new List<RoadPoint>(plan.Points.Count);
        for (int i = 0; i < plan.Points.Count; i++)
        {
            Vector2? location = intoLocation ? plan.Locations.Location : plan.FollowTerrain ? null : plan.Locations.Holding(plan.Points[i]);
            points.Add(new RoadPoint(plan.Points[i], plan.WidthAt(i), plan.Heights[i], plan.FollowTerrain,
                intoLocation: intoLocation || location.HasValue, location: location,
                unpainted: plan.Unpainted != null && i < plan.Unpainted.Count && plan.Unpainted[i], mound: plan.Mound));
        }
        return points;
    }

    /// <summary>
    /// The commit path of a generated road: its pieces stored together, and never a stretch riding a road already stored
    /// (RoadRide, the one owner: the generator keeps only a road's own pieces, RoadRide.Own, before it gets here). A ridden
    /// stretch that reaches this point anyway is a defect upstream: the unit suite throws on it; a game logs it, counts it
    /// (RoadRide.CaughtAtCommit) and stores the road without it. <paramref name="ownChecked"/>: the pieces are what
    /// RoadRide.Own kept a moment ago, against the network as it stands; the ride is not replayed on them (a whole ride per
    /// commit), in the game and the unit suite alike.
    /// </summary>
    public static void CommitRoad(List<PlannedPath> pieces, bool intoLocation = false, System.Action<PlannedPath>? committed = null,
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
            Commit(plan, intoLocation: intoLocation);
            committed?.Invoke(plan);
        }
    }

    /// <summary>Road points levelled to the given heights, stored without planning; painted unless
    /// <paramref name="unpainted"/> (a staircase's start pad, which is never painted); a mound's ground, not road, where
    /// <paramref name="mound"/> (<see cref="RoadPoint.mound"/>).</summary>
    public static void CommitLevelled(IList<Vector2> points, IList<float> heights, float width, bool unpainted = false, bool mound = false)
    {
        var temp = new Dictionary<Vector2i, List<RoadPoint>>();
        for (int i = 0; i < points.Count; i++)
            AddRoadPoint(temp, points[i], width, heights[i], false, unpainted: unpainted, mound: mound);
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

    /// <summary>A cut as <see cref="CommitCuts"/> stores it: a cut-only point (x, z, the height to lower the ground to),
    /// <paramref name="width"/> wide. Not stored.</summary>
    internal static RoadPoint CutPoint(Vector3 cut, float width) => new(new Vector2(cut.x, cut.z), width, cut.y, cutOnly: true);

    /// <summary>
    /// A staircase's cuts taken back where its pieces were taken away (RoadStairRuns.LayLanding): every cut-only point within
    /// <paramref name="reach"/> of one of <paramref name="near"/> that <paramref name="taken"/> picks leaves the network. A
    /// cut wins over any road's fill (RoadTerrainModifier), so left in, it dug a pit in the ground filled over it. Returns
    /// how many stored records went.
    /// </summary>
    internal static int TakeBackCuts(IReadOnlyList<Vector2> near, float reach, Func<RoadPoint, bool> taken) =>
        TakeBack(new CutsTakenBack(near, reach, taken));

    /// <summary>
    /// The one rule for which stored cuts a staircase's pieces taken away take back (<see cref="TakeBackCuts"/>): every cut-only
    /// point this island sees within <c>reach</c> of one of <c>near</c> that <c>taken</c> picks. What the commit takes
    /// (<see cref="TakeBack"/>) and what the finished-ground predictor leaves out before it is (RoadTerrainModifier.AsStored).
    /// </summary>
    internal sealed class CutsTakenBack
    {
        private readonly IReadOnlyList<Vector2> m_near;
        private readonly float m_reach;
        private readonly Func<RoadPoint, bool> m_taken;
        /// <summary>The grid cells the commit visits: round each of <c>near</c>, as far as <c>reach</c> and a cell.</summary>
        internal readonly HashSet<Vector2i> Cells = new();

        internal CutsTakenBack(IReadOnlyList<Vector2> near, float reach, Func<RoadPoint, bool> taken)
        {
            (m_near, m_reach, m_taken) = (near, reach, taken);
            int rings = Mathf.CeilToInt(reach / GridSize) + 1;
            foreach (Vector2 q in near)
            {
                Vector2i home = GetRoadGrid(q.x, q.y);
                for (int y = home.y - rings; y <= home.y + rings; y++)
                    for (int x = home.x - rings; x <= home.x + rings; x++)
                        Cells.Add(new Vector2i(x, y));
            }
        }

        /// <summary>Whether <paramref name="rp"/>, read by island worker <paramref name="me"/>, is taken back.</summary>
        internal bool Takes(in RoadPoint rp, int me)
        {
            if (!rp.cutOnly || !CrossIslandReads.Sees(me, rp.island) || !m_taken(rp)) return false;
            foreach (Vector2 q in m_near)
                if (Vector2.Distance(rp.p, q) <= m_reach) return true;
            return false;
        }
    }

    /// <summary>The cuts <paramref name="back"/> takes leave the network; how many stored records went.</summary>
    internal static int TakeBack(CutsTakenBack back)
    {
        var cells = back.Cells;
        int me = CrossIslandReads.Me;
        bool Taken(RoadPoint rp) => back.Takes(rp, me);
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
    internal static int SetRoadHeights(IReadOnlyList<(RoadPoint at, float to)> changes) => SetRoadHeights(new Raises(changes));

    /// <summary><see cref="SetRoadHeights(IReadOnlyList{ValueTuple{RoadPoint, float}})"/> by <paramref name="raises"/>, each of
    /// its stages applied in turn to what the one before it left (<see cref="Raises"/>), once.</summary>
#if ROADS_TESTS
    /// <summary>Every set of raises written to the network on this thread (<see cref="SetRoadHeights(Raises)"/>): a test's record.</summary>
    [ThreadStatic] internal static List<Raises>? t_raisesWritten;
#endif

    internal static int SetRoadHeights(Raises raises)
    {
#if ROADS_TESTS
        t_raisesWritten?.Add(raises);
#endif
        if (raises.Count == 0) return 0;
        RoadNetworkGenerator.IslandNetworkChanged();
        m_roadCacheLock.EnterWriteLock();
        try { return RaiseBy(m_roadPoints, raises, null); }
        finally { m_roadCacheLock.ExitWriteLock(); }
    }

    /// <summary>
    /// The road points stored at exactly <paramref name="at"/> (a road's own points, as a staircase's end records its road:
    /// StairRoad), in order, as they stand now: at the heights <see cref="SetRoadHeights"/> and an append's raise moved them
    /// to. Null where any of them is not stored. Lock-free like every point lookup; read in the point's own cell.
    /// </summary>
    internal static RoadPoint[]? StoredAt(IReadOnlyList<Vector2> at)
    {
        var found = new RoadPoint[at.Count];
        int me = CrossIslandReads.Me;
        for (int i = 0; i < at.Count; i++)
        {
            Vector2 p = at[i];
            if (!m_roadPoints.TryGetValue(GetRoadGrid(p.x, p.y), out var cell)) return null;
            bool stored = false;
            foreach (RoadPoint rp in cell)
                if (rp.IsRoad && rp.p.x == p.x && rp.p.y == p.y && CrossIslandReads.Sees(me, rp.island)) { found[i] = rp; stored = true; break; }
            if (!stored) return null;
        }
        return found;
    }

    /// <summary><see cref="SetRoadHeights"/> on <paramref name="cells"/> (the caller holds the grid's write lock). A cell's
    /// array is always replaced, never changed in place: other threads read a committed array without the lock. With
    /// <paramref name="addition"/> (an append) each moved point becomes the append's, so a terrain compiler stamped before it
    /// writes the point again.</summary>
    private static int Raise(IDictionary<Vector2i, RoadPoint[]> cells, IReadOnlyList<(RoadPoint at, float to)> changes, int? addition) =>
        RaiseBy(cells, new Raises(changes), addition);

    private static int RaiseBy(IDictionary<Vector2i, RoadPoint[]> cells, Raises raises, int? addition)
    {
        int moved = 0;
        int me = CrossIslandReads.Me;
        foreach (Vector2i cell in raises.Cells)
        {
            if (!cells.TryGetValue(cell, out var points)) continue;
            RoadPoint[]? copy = null;
            for (int k = 0; k < points.Length; k++)
            {
                if (!raises.To(points[k], me, out float to)) continue;
                RoadPoint[] into = copy ??= (RoadPoint[])points.Clone();
                into[k].h = to;
                if (addition is int a) into[k].addition = a;
                moved++;
            }
            if (copy != null) cells[cell] = copy;
        }
        return moved;
    }

    /// <summary>
    /// The one rule for which stored point a list of raises moves and to what height: what the commit does
    /// (<see cref="SetRoadHeights"/>, an append's raise) and what the finished-ground predictor reads before the road is
    /// stored (RoadTerrainModifier.AsStored). A change names a point as it was read: a stored road point
    /// (<see cref="RoadPoint.IsRoad"/>, on an island this worker sees) at its position and at its height within 1e-4 of the
    /// change's, whatever its width, goes to the highest height the changes naming it ask. Changes are grouped by source
    /// height in their order (the first within 1e-4 takes the next), so two readers of one list agree. In stages (a road's
    /// crossings' raises, then its landing's: RoadNetworkGenerator.CrossingGround.Raises), each stage is applied to the
    /// heights the one before it left, as they were written one after the other.
    /// </summary>
    internal sealed class Raises
    {
        private readonly List<Dictionary<Vector2, List<(float from, float to)>>> m_stages = new();
        /// <summary>The grid cells the commit visits for these changes: each change's own cell and the rings its width reaches.</summary>
        internal readonly HashSet<Vector2i> Cells = new();
        /// <summary>Every point the changes name, as it was read.</summary>
        internal readonly List<RoadPoint> Sources = new();
        internal int Count => Sources.Count;

        internal Raises(IReadOnlyList<(RoadPoint at, float to)> changes) : this(new[] { changes }) { }

        internal Raises(IReadOnlyList<IReadOnlyList<(RoadPoint at, float to)>> stages)
        {
            foreach (var changes in stages) Stage(changes);
        }

        private void Stage(IReadOnlyList<(RoadPoint at, float to)> changes)
        {
            var byPoint = new Dictionary<Vector2, List<(float from, float to)>>();
            m_stages.Add(byPoint);
            foreach (var (at, to) in changes)
            {
                Sources.Add(at);
                if (!byPoint.TryGetValue(at.p, out var list)) byPoint[at.p] = list = new List<(float from, float to)>();
                int e = list.FindIndex(x => Mathf.Abs(x.from - at.h) < 1e-4f);
                if (e >= 0) list[e] = (list[e].from, Mathf.Max(list[e].to, to));
                else list.Add((at.h, to));
                Vector2i home = GetRoadGrid(at.p.x, at.p.y);
                int rings = Mathf.CeilToInt(at.w / GridSize) + 1;
                for (int y = home.y - rings; y <= home.y + rings; y++)
                    for (int x = home.x - rings; x <= home.x + rings; x++)
                        Cells.Add(new Vector2i(x, y));
            }
        }

        /// <summary>Whether these changes move <paramref name="stored"/>, read by island worker <paramref name="me"/>, and to
        /// what height (<paramref name="to"/>).</summary>
        internal bool To(in RoadPoint stored, int me, out float to)
        {
            to = stored.h;
            if (!stored.IsRoad || !CrossIslandReads.Sees(me, stored.island)) return false;
            bool moved = false;
            foreach (var byPoint in m_stages)
            {
                if (!byPoint.TryGetValue(stored.p, out var list)) continue;
                float h = to;
                int e = list.FindIndex(x => Mathf.Abs(x.from - h) < 1e-4f);
                if (e < 0) continue;
                to = list[e].to;
                moved = true;
            }
            return moved;
        }
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
                // The location an into-location point may change the ground of: another location is another network.
                if (rp.intoLocation)
                {
                    Mix(ref hash, rp.savedLocation.x.GetHashCode());
                    Mix(ref hash, rp.savedLocation.y.GetHashCode());
                }
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
        c = a.intoLocation.CompareTo(b.intoLocation);
        if (c != 0) return c;
        c = a.unpainted.CompareTo(b.unpainted);
        if (c != 0) return c;
        c = a.mound.CompareTo(b.mound);
        if (c != 0 || !a.intoLocation) return c;
        c = a.savedLocation.x.CompareTo(b.savedLocation.x);
        return c != 0 ? c : a.savedLocation.y.CompareTo(b.savedLocation.y);
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

    internal static List<Vector2> SplinePath(List<Vector2> waypoints, float segmentLength)
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
    internal static List<float> SmoothHeights(List<float> heights, int windowSize, out List<RoadPointDebugInfo> debugInfos)
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

    /// <summary>Where a road met one already laid: two measurements of one held stretch, each at its own worst point.
    /// <see cref="Pull"/>: how far the stretch was pulled from the road's own profile (metres, + up), greatest
    /// in size, at <see cref="PullAt"/> (RoadNetworkGenerator.HeldOffProfile). <see cref="Fill"/>: how far the held road
    /// stands over the ground the road it meets left there (+ fill, - cut), greatest, at <see cref="FillAt"/>: a large one
    /// is a junction ridge (RoadNetworkGenerator.HeldRidges). The two need not be at the same point: a stretch can be
    /// pulled furthest on the other road's surface, where it adds no fill, and stand on its most fill at the edge of
    /// that road's reach. NaN where the measurement is not known (no ground given, or not on this piece).</summary>
    public readonly struct Junction
    {
        public readonly Vector2 PullAt, FillAt;
        public readonly float Pull, Fill;
        /// <summary>Where along the new road: "start", "end" (a held stretch touching that end) or "middle".</summary>
        public readonly string Where;
        public Junction(Vector2 pullAt, float pull, Vector2 fillAt, float fill, string where = "middle")
        { PullAt = pullAt; Pull = pull; FillAt = fillAt; Fill = fill; Where = where; }

        /// <summary>This junction as kept on a piece cut from its road: each measurement whose point lies on the piece
        /// (<paramref name="on"/>), the other unknown (NaN); null when neither does.</summary>
        internal Junction? On(System.Predicate<Vector2> on)
        {
            bool pull = on(PullAt), fill = on(FillAt);
            if (!pull && !fill) return null;
            return new Junction(PullAt, pull ? Pull : float.NaN, FillAt, fill ? Fill : float.NaN, Where);
        }

        /// <summary><paramref name="junctions"/> as kept on a piece cut from their road (<see cref="On"/>); null when none is.</summary>
        internal static List<Junction>? On(List<Junction>? junctions, System.Predicate<Vector2> on)
        {
            if (junctions == null) return null;
            List<Junction>? kept = null;
            foreach (var j in junctions)
                if (j.On(on) is Junction k) (kept ??= new List<Junction>()).Add(k);
            return kept;
        }
    }

    /// <summary>Every junction of the roads committed this generation (under m_recordGate), for the
    /// generation summary: recorded only with PROCEDURALROADS_TIMINGS (<see cref="SearchStats.Enabled"/>).</summary>
    public static readonly List<Junction> Junctions = new();

    /// <summary>How many held stretches a road may let go before it is refused.</summary>
    internal const int HeldLetGoRounds = 4;

    /// <summary>A road's step: how far apart its planned points lie at most (the dense spacing, a quarter of its width), and
    /// the shortest run its grade is judged over (<see cref="WorstOverLimit"/>).</summary>
    internal static float RoadStep(float width) => width / 4f;

    /// <summary>
    /// The index of the step of a held profile furthest over its own limit, or -1: every edge judged against
    /// <see cref="RoadGrade.EdgeLimit"/> (the schedule the landing acceptance kept: a rest, a landing, a pitch; the cap where
    /// none is given), an edge between two held points too (held to the surface of the roads it meets,
    /// <see cref="SurfaceReader"/>, it is this road's step). Judged over at least one road step (<paramref name="step"/>, the
    /// dense spacing): the rise from the point that far back, against the rise each edge between may make, so a centimetre
    /// of stored height over a two-centimetre edge (a held point, a levelled end's split) is not read as a 50 % climb. Float
    /// slack only (<see cref="HeldCapMargin"/>). Where <paramref name="judged"/> is given, only the runs touching one of its
    /// points are judged (a structure's levelled end put back). No edge is judged at another limit than its own: a stretch a
    /// road shares with one it meets (<see cref="OnMetRoads"/>) is that road's, and its schedule says so (RoadPitches.AcceptLanding
    /// keeps no landing or rest of this road's on it, and lays a joined end's landing before it). The one judge of a finished
    /// profile's grade.
    /// </summary>
    internal static int WorstOverLimit(IReadOnlyList<Vector2> points, IReadOnlyList<float> heights, IReadOnlyList<float>? edgeGrades,
        float cap, float step, bool[]? judged = null)
    {
        int worst = -1;
        float furthest = 0f;
        int n = System.Math.Min(points.Count, heights.Count);
        for (int i = 1; i < n; i++)
        {
            float run = 0f, allowed = 0f;
            bool touches = judged == null || judged[i];
            int j = i;
            while (j > 0 && (run < step || j == i))
            {
                float edge = Vector2.Distance(points[j - 1], points[j]);
                run += edge;
                allowed += RoadGrade.EdgeLimit(edgeGrades, j, cap) * edge;
                j--;
                if (judged != null && judged[j]) touches = true;
            }
            if (!touches || run < 0.001f) continue;
            float over = (Mathf.Abs(heights[i] - heights[j]) - allowed) / run;
            if (over > HeldCapMargin && over > furthest) { furthest = over; worst = i; }
        }
        return worst;
    }

    /// <summary>Whether a let-go point would lie on another road's surface (within the two half widths): a road kept at its
    /// own height there would cross that one at a different level, two surfaces mixing, and is refused instead (seen
    /// beside the mountain cave entry at 1950,705).</summary>
    internal static bool LetGoOntoRoad(List<Vector2> points, bool[] letGo, float width)
    {
        for (int i = 0; i < points.Count; i++)
            if (letGo[i] && TryGetRoadHeightWithin(points[i], width, out _, out float near, out float other) && OnItsSurface(near, width, other))
                return true;
        return false;
    }

    /// <summary>Whether a point of a road <paramref name="width"/> wide, <paramref name="near"/> from the nearest point of a
    /// road <paramref name="other"/> wide, lies on that road's surface: within the two half widths, the two surfaces sharing
    /// ground (<see cref="LetGoOntoRoad"/>, <see cref="OnMetRoads"/>).</summary>
    internal static bool OnItsSurface(float near, float width, float other) => near < (width + other) / 2f;

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
    /// <summary>Float slack only: a held road's steps are held to the
    /// grade cap itself, with no allowance over it.</summary>
    internal const float HeldCapMargin = 1e-3f;

    /// <summary>The points of a road planned along <paramref name="points"/> (<paramref name="width"/> wide) that lie on the
    /// surface of a road already laid (<see cref="OnItsSurface"/>), read as the road is held to it (<see cref="HoldToNetwork"/>:
    /// the same reader, <see cref="SurfaceReader"/>, over the same band): a stretch the two share is that road's own, where this
    /// road cannot keep a height of its own (<see cref="LetGoOntoRoad"/>). Null where none does. The landing acceptance keeps
    /// no limit of this road's on it, and a joined end's landing begins where the road leaves it (RoadPitches.AcceptLanding).</summary>
    internal static bool[]? OnMetRoads(List<Vector2> points, float width)
    {
        if (!m_initialized || points.Count == 0) return null;
        float band = RoadProfile.Footprint(width);
        SurfaceReader surface = (t_profileSurface ??= new SurfaceReader()).Reset();
        bool[]? on = null;
        for (int i = 0; i < points.Count; i++)
            if (surface.Read(points[i], band, out _, out float near, out float nearWidth) && OnItsSurface(near, width, nearWidth))
                (on ??= new bool[points.Count])[i] = true;
        return on;
    }

    [System.ThreadStatic] private static List<Vector2>? t_landingWay;
    [System.ThreadStatic] private static List<bool>? t_landingShared;

    /// <summary>Whether a road coming in along <paramref name="way"/> (from the point it meets the network, outward) has
    /// room for its junction landing where it leaves the road it meets, as the planner lays and judges it: the points on that
    /// road's surface from the junction on are the shared stretch (<see cref="OnItsSurface"/>, <see cref="RoadPitches.SharedRun"/>,
    /// the reading <see cref="OnMetRoads"/> gives the planner), and over the <see cref="RoadPitches.JunctionLanding"/> metres
    /// before it every point the road would be held at (within the band <see cref="HoldToNetwork"/> holds over) stands within
    /// <see cref="RoadPitches.JunctionLandingGrade"/> of the height where the road leaves that road. A point past the band is
    /// the road's own, and the landing eases to it. Read every road step (<see cref="RoadStep"/>), stopping as soon as the
    /// verdict is known: the join search asks it on its arrivals (RoadPathfinder.ArrivesLevel). <paramref name="decided"/> is
    /// false where the way ends before the verdict is known (still on the met road, or within the landing): the caller may
    /// give it more of the way in; with none left, the result stands.</summary>
    internal static bool RoomToLandBefore(IReadOnlyList<Vector2> way, float width) => RoomToLandBefore(way, width, out _);

    internal static bool RoomToLandBefore(IReadOnlyList<Vector2> way, float width, out bool decided)
    {
        decided = true;
        if (!m_initialized || way.Count < 2) return true;
        float band = RoadProfile.Footprint(width), step = RoadStep(width);
        SurfaceReader surface = (t_profileSurface ??= new SurfaceReader()).Reset();
        var points = t_landingWay ??= new List<Vector2>();
        var shared = t_landingShared ??= new List<bool>();
        points.Clear(); shared.Clear();
        float leaveHeight = 0f, leaveAt = 0f, along = 0f;
        bool landing = false;
        for (int seg = 1; seg < way.Count; seg++)
        {
            Vector2 a = way[seg - 1], b = way[seg];
            float len = Vector2.Distance(a, b);
            int n = Mathf.Max(1, Mathf.CeilToInt(len / step));
            for (int k = seg == 1 ? 0 : 1; k <= n; k++)
            {
                Vector2 p = Vector2.Lerp(a, b, k / (float)n);
                if (points.Count > 0) along += Vector2.Distance(points[points.Count - 1], p);
                bool read = surface.Read(p, band, out float h, out float near, out float nearWidth);
                if (!landing)
                {
                    bool on = read && OnItsSurface(near, width, nearWidth);
                    points.Add(p); shared.Add(on);
                    if (on || points.Count == 1) leaveHeight = h;
                    if (on) continue;
                    // Left the met road: the landing begins at the last shared point (none: the junction itself).
                    landing = true;
                    leaveAt = RoadPitches.SharedRun(points, shared, atStart: true);
                }
                else points.Add(p);
                if (!read) return true;
                float from = along - leaveAt;
                if (from > RoadPitches.JunctionLanding) return true;
                if (Mathf.Abs(h - leaveHeight) > RoadPitches.JunctionLandingGrade * from + HeldCapMargin) return false;
            }
        }
        decided = false;
        return true;
    }

    /// <summary>
    /// Where a road comes within reach of a road already laid -- running along it, crossing it,
    /// or the mouth where it joins -- it takes that road's height there: the nearest point's
    /// within its width and the terrain blend on both sides, as far as the terrain writer mixes
    /// the two. Elsewhere it keeps its own profile, eased to and from those held stretches within
    /// each edge's own grade limit, as the grade limiter gave them (<paramref name="edgeGrades"/>:
    /// a rest or a turn landing gentler than the cap, never steeper; <see cref="RoadGrade.Limit"/>),
    /// else at <paramref name="cap"/>; held no lower than <paramref name="floor"/>. The road already laid is never
    /// changed, so the same ground never carries two road surfaces.
    /// </summary>
    internal static List<Junction>? HoldToNetwork(List<Vector2> points, List<float> heights, float width, float cap,
        System.Func<Vector2, float>? ground = null, bool joinedStart = false, bool joinedEnd = false, bool[]? heldOut = null,
        bool[]? letGo = null, IReadOnlyList<float>? edgeGrades = null, float floor = float.NegativeInfinity, List<int>? underFloor = null)
    {
        if (!m_initialized || points.Count == 0) return null;
        float band = RoadProfile.Footprint(width);
        int n = points.Count;
        var held = new bool[n];
        bool any = false;
        List<Junction>? junctions = null;
        // The held stretch being measured: its greatest pull (in size) and greatest fill, each at its own point.
        bool inStretch = false;
        Vector2 pullAt = default, fillAt = default;
        float worstPull = 0f, worstFill = float.NaN;
        int stretchFrom = 0;
        string Where(int to) => stretchFrom == 0 ? (joinedStart ? "start on a road" : "start at a location")
            : to == n - 1 ? (joinedEnd ? "end on a road" : "end at a location") : "middle";
        // The met road's surface, read for this whole profile off one copy of the network (the one reader).
        SurfaceReader surface = (t_profileSurface ??= new SurfaceReader()).Reset();
        for (int i = 0; i < n; i++)
        {
            if ((letGo == null || !letGo[i]) && surface.Read(points[i], band, out float h, out float near, out float nearWidth))
            {
                // Never below this road's own floor (a raised ford's surface, dry land's floor): held to a road below it,
                // it stands on the floor, and its junction record says so. Where the two surfaces touch and that road stands
                // more than a step under the floor, the two do not meet: noted (underFloor) for the caller to let go or refuse.
                if (h < floor && !StairLayout.Walkable(h, floor) && near < (width + nearWidth) / 2f) underFloor?.Add(i);
                h = Mathf.Max(h, floor);
                // Per held stretch, the point the road was pulled furthest from its own profile.
                float pull = h - heights[i];
                // Its fill over the ground the road it meets already left there: that road's own surface
                // within its width, blended out to the natural ground over the terrain margin. Against
                // the natural ground, a road running along one laid on a fill counted that road's own
                // embankment as a new junction ridge, and was refused for a ridge it did not make.
                float under = ground == null ? float.NaN
                    : Mathf.Lerp(ground(points[i]), h, RoadProfile.LevelBlend(near, nearWidth));
                // And, measured on its own, the point it stands on the most fill: not where it was pulled furthest.
                float fill = h - under;
                if (!inStretch)
                {
                    inStretch = true; stretchFrom = i;
                    pullAt = fillAt = points[i]; worstPull = pull; worstFill = fill;
                }
                else
                {
                    if (Mathf.Abs(pull) > Mathf.Abs(worstPull)) { pullAt = points[i]; worstPull = pull; }
                    if (fill > worstFill) { fillAt = points[i]; worstFill = fill; }
                }
                heights[i] = h; held[i] = true; any = true;
            }
            else if (inStretch)
            {
                (junctions ??= new List<Junction>()).Add(new Junction(pullAt, worstPull, fillAt, worstFill, Where(i - 1)));
                inStretch = false;
            }
        }
        if (inStretch) (junctions ??= new List<Junction>()).Add(new Junction(pullAt, worstPull, fillAt, worstFill, Where(n - 1)));
        if (heldOut != null) System.Array.Copy(held, heldOut, System.Math.Min(n, heldOut.Length));
        if (!any) return junctions;
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
                        if (!rp.Walked) continue; // a staircase's cut or a mound is not a road to meet
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
                        if (!rp.Walked) continue; // nor a height to blend toward
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

    private static void AddRoadPoint(Dictionary<Vector2i, List<RoadPoint>> roadPoints, Vector2 p, float width, float height, bool paintOnly, int addition = 0, bool cutOnly = false, bool intoLocation = false,
        bool unpainted = false, Vector2? location = null, bool mound = false)
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
                    list.Add(new RoadPoint(p, width, height, paintOnly, addition, cutOnly, intoLocation, unpainted, location, mound) { island = CrossIslandReads.Tag });
                }
            }
        }
    }

    /// <summary>Whether <paramref name="rp"/>, read in <paramref name="cell"/> by a gather over the cells within
    /// <paramref name="rings"/> of <paramref name="centre"/>, is a copy the gather reads in another of them: a point is filed
    /// in every cell its width reaches (<see cref="AddRoadPoint"/>), always in its own (<see cref="GetRoadGrid"/>), so a
    /// gather takes it from its own cell alone. One whose own cell lies outside the cells read is taken from each that holds
    /// it, as before. The one rule that each stored point enters a gather once (<see cref="GetRoadPointsInZone"/>, the
    /// terrain writer's fit; <see cref="GetRoadPointsNearPosition"/>): a copy weighed a point twice in a zone's fit.</summary>
    private static bool CopyGathered(in RoadPoint rp, Vector2i cell, Vector2i centre, int rings)
    {
        Vector2i home = GetRoadGrid(rp.p.x, rp.p.y);
        return (home.x != cell.x || home.y != cell.y)
            && System.Math.Abs(home.x - centre.x) <= rings && System.Math.Abs(home.y - centre.y) <= rings;
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
        int skipped = 0;
        for (int i = 0; i < points.Length; i++)
        {
            RoadPoint rp = points[i];
            if (!rp.Walked) continue;
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
            if (!rp.Walked) continue; // a staircase's cut or a mound is not painted road
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

    /// <summary>Whether the zone at <paramref name="zonePos"/> gathers a road point for its terrain write: the point within
    /// its reach of the zone's square (<see cref="ZoneReach"/>; a staircase's cut only its cut and batter and a vertex
    /// spacing, <see cref="RoadTerrainModifier.CutReach"/>: gathered wider, a zone it never touches waits for it forever,
    /// unstamped). <see cref="GetRoadPointsInZone"/>'s test, and the finished-ground predictor's for a road not stored yet
    /// (<see cref="RoadTerrainModifier.AsStored"/>).</summary>
    internal static bool Gathers(Vector3 zonePos, in RoadPoint rp)
    {
        float reach = RoadConstants.HalfZoneSize + (rp.cutOnly ? RoadTerrainModifier.CutReach(rp.w) + RoadTerrainModifier.CutGatherPad : ZoneReach(rp.w));
        return rp.p.x >= zonePos.x - reach && rp.p.x <= zonePos.x + reach && rp.p.y >= zonePos.z - reach && rp.p.y <= zonePos.z + reach;
    }

    /// <summary>Every road point that can change the zone's terrain (within its reach of the zone: <see cref="ZoneReach"/>, a
    /// staircase's cut its own), each stored point once (<see cref="CopyGathered"/>): the points the terrain writer fits the
    /// zone's vertices from.</summary>
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
                            if (Gathers(zonePos, rp) && !CopyGathered(rp, checkGrid, grid, 1)) result.Add(rp);
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

    /// <summary><see cref="TryGetRoadHeightWithin(Vector2, float, out float)"/> on the network as it will stand once a road's
    /// <paramref name="proposed"/> changes are made: the nearest road point at the height they raise it to.</summary>
    internal static bool TryGetRoadHeightWithin(Vector2 position, float reach, out float height, RoadNetworkGenerator.Proposed? proposed) =>
        TryGetRoadHeightWithin(position, reach, out height, out _, out _, proposed);

    /// <param name="distance">How far the nearest road point is.</param>
    /// <param name="width">That point's road width.</param>
    public static bool TryGetRoadHeightWithin(Vector2 position, float reach, out float height, out float distance, out float width) =>
        TryGetRoadHeightWithin(position, reach, out height, out distance, out width, null);

    private static bool TryGetRoadHeightWithin(Vector2 position, float reach, out float height, out float distance, out float width,
        RoadNetworkGenerator.Proposed? proposed) =>
        (t_surface ??= new SurfaceReader()).Reset(proposed).Read(position, reach, out height, out distance, out width);

    /// <summary>A thread's own <see cref="SurfaceReader"/> for one read (<see cref="TryGetRoadHeightWithin(Vector2, float, out float, out float, out float)"/>),
    /// and one for a profile read point by point (<see cref="HoldToNetwork"/>), so neither allocates once warm.</summary>
    [System.ThreadStatic] private static SurfaceReader? t_surface, t_profileSurface;

    /// <summary>
    /// The one reader of a met road's height: the road nearest a position within a reach, its walked surface (<see cref="RoadPoint.Walked"/>:
    /// a waded ford's painted bed too, which a road ending on it was never held to and stood 0.8 m over), and its surface there as the
    /// terrain writer fits it (<see cref="RoadTerrainModifier.SurfaceAlong"/>: the line along the road through its points that
    /// reach its nearest one, read at that point and carried along the road to the position's place along it). Not the
    /// nearest point's own height: a road held to another at its own spacing read that road in steps of that road's spacing,
    /// past the cap. Only that road's points enter the fit: a neighbour within a step of the cap of the nearest point's
    /// height over their distance (<see cref="RoadGrade.Configured"/>, float slack only), never a road crossing or passing at
    /// another height, whose surface the fit would otherwise mix in. Every reader of the fact reads it here: a road held to the
    /// network (<see cref="HoldToNetwork"/>), the height a road's joined end meets (<see cref="PlanRoadPathCore"/>), the join
    /// search's level arrival (RoadPathfinder.ArrivesLevel), a road beside another (RoadBeside), the end reader before a road
    /// is stored (RoadTerrainModifier.RoadHeightNear), <see cref="RoadHeightAt"/>, a junction fill's height on the road it meets
    /// (RoadJunctionFillet) and a crossing's bank height on the network's lines (RoadRide.Lines).
    /// The network is read from one local copy of the grid for the reader's whole profile, and each road point it reads is laid
    /// out for the fit once per profile (<see cref="RoadTerrainModifier.FitPoints"/>), with nothing allocated per read once warm
    /// (it runs in the search, under the game's Mono).
    /// </summary>
    internal sealed class SurfaceReader
    {
        private ConcurrentDictionary<Vector2i, RoadPoint[]>? m_cells;
        private List<RoadPoint>? m_over;
        private bool m_ready;
        private RoadNetworkGenerator.Proposed? m_proposed;
        private int m_me;
        private readonly RoadTerrainModifier.FitPoints m_fit = new();
        private readonly Dictionary<Vector3, int> m_laid = new(LaidKey.Instance);
        private int[] m_only = new int[16];
        private int[] m_stamp = new int[16];
        private int m_read;

        /// <summary>Starts a profile on the network as it stands (once a road's <paramref name="proposed"/> changes are made,
        /// where given): one local copy of the grid for every read until the next reset.</summary>
        internal SurfaceReader Reset(RoadNetworkGenerator.Proposed? proposed = null)
        {
            m_cells = m_roadPoints; m_over = null; m_ready = m_initialized;
            return Begin(proposed);
        }

        /// <summary>Starts a profile over <paramref name="points"/> alone (the network as it will stand once a road not stored
        /// yet is: RoadTerrainModifier.RoadHeightNear).</summary>
        internal SurfaceReader ResetOver(List<RoadPoint> points)
        {
            m_cells = null; m_over = points; m_ready = true;
            return Begin(null);
        }

        private SurfaceReader Begin(RoadNetworkGenerator.Proposed? proposed)
        {
            m_proposed = proposed; m_me = CrossIslandReads.Me;
            m_fit.Clear(); m_laid.Clear();
            return this;
        }

        private RoadPoint Raised(RoadPoint point) => m_proposed != null ? m_proposed.AsRaised(point, m_me) : point;

        /// <summary>The road nearest <paramref name="position"/> within <paramref name="reach"/>: its surface there
        /// (<paramref name="height"/>), how far its nearest point is (<paramref name="distance"/>) and that point's road
        /// width (<paramref name="width"/>); false when there is none.</summary>
        internal bool Read(Vector2 position, float reach, out float height, out float distance, out float width)
        {
            height = 0f; distance = float.PositiveInfinity; width = 0f;
            if (!m_ready || reach < 0) return false;
            float best = reach * reach;
            bool found = false;
            RoadPoint nearest = default;
            int skipped = 0;
            if (m_over != null)
            {
                foreach (var point in m_over)
                {
                    float d = (point.p - position).sqrMagnitude;
                    if (d <= best && point.Walked) { best = d; found = true; nearest = point; }
                }
            }
            else
            {
                int radius = RingRadius(reach);
                Vector2i center = GetRoadGrid(position.x, position.y);
                for (int x = -radius; x <= radius; x++)
                    for (int y = -radius; y <= radius; y++)
                        if (m_cells!.TryGetValue(new Vector2i(center.x + x, center.y + y), out var points))
                            foreach (var point in points)
                            {
                                float d = (point.p - position).sqrMagnitude;
                                if (d <= best && point.Walked)
                                {
                                    if (!CrossIslandReads.Sees(m_me, point.island)) { skipped = point.island; continue; }
                                    best = d; found = true; nearest = point;
                                }
                            }
                if (skipped != 0) CrossIslandReads.Skipped(CrossIslandReads.Kind.Points, skipped, position);
            }
            if (!found) return false;
            distance = Mathf.Sqrt(best);
            width = nearest.w;
            RoadPoint at = Raised(nearest);
            // Its road's points that may reach it in the fit, each once (a point is filed in every cell within its width).
            if (++m_read == int.MaxValue) { System.Array.Clear(m_stamp, 0, m_stamp.Length); m_read = 1; }
            int count = 0;
            float cap = RoadGrade.Configured;
            if (m_over != null)
            {
                foreach (var point in m_over) Neighbour(point, at, cap, ref count);
            }
            else
            {
                // The widest a point's fit reaches, in cells round the nearest point's own; a cell farther off is passed over.
                float far = RoadTerrainModifier.FitReach(Mathf.Max(width, s_widestFit));
                int radius = Mathf.CeilToInt(far / GridSize);
                Vector2i center = GetRoadGrid(at.p.x, at.p.y);
                for (int x = -radius; x <= radius; x++)
                    for (int y = -radius; y <= radius; y++)
                    {
                        var cell = new Vector2i(center.x + x, center.y + y);
                        float bx = Mathf.Max(0f, Mathf.Abs(at.p.x - cell.x * GridSize) - GridSize / 2f);
                        float by = Mathf.Max(0f, Mathf.Abs(at.p.y - cell.y * GridSize) - GridSize / 2f);
                        if (bx * bx + by * by > far * far || !m_cells!.TryGetValue(cell, out var points)) continue;
                        foreach (var point in points)
                            if (CrossIslandReads.Sees(m_me, point.island)) Neighbour(point, at, cap, ref count);
                    }
            }
            height = RoadTerrainModifier.SurfaceAlong(m_fit, m_only, count, at.p, position);
            return true;
        }

        /// <summary>Takes <paramref name="point"/> into the fit at <paramref name="at"/> where it is walked road of the same kind
        /// (levelled road with levelled road, a wade's painted bed with its bed), may reach it
        /// (<see cref="RoadTerrainModifier.FitReach(float, float)"/>) and is on its road: within the cap of its height over
        /// their distance.</summary>
        private void Neighbour(RoadPoint point, in RoadPoint at, float cap, ref int count)
        {
            if (!point.Walked || point.Levelled != at.Levelled) return;
            float d2 = (point.p - at.p).sqrMagnitude;
            float far = RoadTerrainModifier.FitReach(point.w);
            if (d2 >= far * far) return;
            point = Raised(point);
            if (Mathf.Abs(point.h - at.h) > cap * Mathf.Sqrt(d2) + HeldCapMargin) return;
            var key = new Vector3(point.p.x, point.p.y, point.h);
            if (!m_laid.TryGetValue(key, out int k))
            {
                k = m_fit.Add(point);
                m_laid[key] = k;
                if (m_stamp.Length <= k) System.Array.Resize(ref m_stamp, System.Math.Max(2 * m_stamp.Length, k + 1));
            }
            if (m_stamp[k] == m_read) return;
            m_stamp[k] = m_read;
            if (m_only.Length <= count) System.Array.Resize(ref m_only, 2 * m_only.Length);
            m_only[count++] = k;
        }

        /// <summary><see cref="m_laid"/>'s comparer: a point's place and height, each compared as float.Equals compares it (as the
        /// game's Vector3.Equals and the offline double's field by field do), hashed on all three. The offline double has no hash of
        /// its own, so a lookup boxed the key and hashed it by reflection on its first field alone.</summary>
        private sealed class LaidKey : IEqualityComparer<Vector3>
        {
            public static readonly LaidKey Instance = new();
            public bool Equals(Vector3 a, Vector3 b) => a.x.Equals(b.x) && a.y.Equals(b.y) && a.z.Equals(b.z);
            public int GetHashCode(Vector3 p) => unchecked((p.x.GetHashCode() * 73856093) ^ (p.y.GetHashCode() * 19349663) ^ (p.z.GetHashCode() * 83492791));
        }
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
        int skipped = 0;
        int radius = RingRadius(reach);
        Vector2i center = GetRoadGrid(position.x, position.y);
        {
            for (int x = -radius; x <= radius; x++)
                for (int y = -radius; y <= radius; y++)
                    if (m_roadPoints.TryGetValue(new Vector2i(center.x+x,center.y+y), out var points))
                        foreach (var point in points)
                        {
                            float distance = (point.p-position).sqrMagnitude;
                            if (distance <= best && point.Walked && !(painted && point.unpainted) && point.h >= minHeight && point.h <= maxHeight)
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
        int skipped = 0;
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

    /// <summary>Every road point within <paramref name="radius"/> of <paramref name="worldPos"/>, nearest first, each stored
    /// point once (<see cref="CopyGathered"/>).</summary>
    public static List<RoadPoint> GetRoadPointsNearPosition(Vector3 worldPos, float radius)
    {
        List<RoadPoint> result = new List<RoadPoint>();
        int me = CrossIslandReads.Me;
        int skipped = 0;
        
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
                            if ((rp.p - pos2D).sqrMagnitude <= radiusSq && !CopyGathered(rp, checkGrid, centerGrid, cellRadius))
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
    /// Serialize the entire road network to a byte array for global persistence, always in the one current format,
    /// [<see cref="SavedFormat"/>][<see cref="SavedEncoding"/>][cellCount], then per cell (in grid order) [x][y][count] and
    /// per point [px][py][w][h] floats, [paintOnly][cutOnly][intoLocation][unpainted][mound] bools, an into-location point's location (its
    /// centre, two floats), [addition] int; then the append block [count][parent ids] (count 0 when nothing was appended);
    /// then [known-length byte 0 or 1][committed planned-path length:float]. A released build (1.4.x) reads format 1 only
    /// and refuses this one whole.
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
            
            writer.Write(SavedFormat);
            writer.Write(SavedEncoding);
            
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
                    writer.Write(rp.cutOnly);
                    writer.Write(rp.intoLocation);
                    writer.Write(rp.unpainted);
                    writer.Write(rp.mound);
                    if (rp.intoLocation) { writer.Write(rp.savedLocation.x); writer.Write(rp.savedLocation.y); }
                    writer.Write(rp.addition);
                }
            }
            
            writer.Write(s_appendParents.Count);
            foreach (int parent in s_appendParents) writer.Write(parent);
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
    /// The format this build writes the network in (<see cref="SerializeAllRoadPoints"/>): a number distinct from the
    /// released build's 1, so that build refuses it whole instead of misreading it.
    /// </summary>
    internal const int SavedFormat = 4;

    /// <summary>The geometry encoding under <see cref="SavedFormat"/>, the second number of the record; no other is read.</summary>
    internal const int SavedEncoding = 8;

    /// <summary>The format a released build (1.4.x) wrote: read as it was written, never written by this build.</summary>
    internal const int ReleasedFormat = 1;

    /// <summary>
    /// Why the last <see cref="DeserializeAllRoadPoints"/> refused its data: the format and
    /// geometry encoding it read, and the check that failed or the exception that stopped it.
    /// Null after a successful read. The unreadable-save error names it, so a refusal says what this build could not read.
    /// </summary>
    public static string? LastReadRefusal { get; private set; }

    /// <summary>
    /// Deserialize the entire road network from a byte array: the released format 1 as a released build wrote it, or this
    /// build's own format (<see cref="SavedFormat"/>, encoding <see cref="SavedEncoding"/>); any other is refused.
    /// Clears existing data and replaces with loaded data.
    /// False (and <see cref="LastReadRefusal"/> says why) when the data is refused. <paramref name="received"/>: a network
    /// the server sent this client (RoadNetworkSync), read by the same checks; a client holds no location instances (the
    /// server's), so an into-location point keeps its saved location and is bound to none, unwarned: a client writes no
    /// ground, and the network's version (the saved location) is the server's.
    /// </summary>
    public static bool DeserializeAllRoadPoints(byte[] data, bool received = false)
    {
        LastReadRefusal = null;
        int format = 0, encoding = 0;
        bool Refuse(string why)
        {
            LastReadRefusal = $"format {format}, encoding {encoding}: {why}";
            return false;
        }
        if (data == null || data.Length == 0)
            return Refuse("no data");

        // Left open by the reader, so a refusal can say how far the read got.
        var ms = new MemoryStream(data);
        try
        {
            using var reader = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            format=reader.ReadInt32();
            encoding=format==SavedFormat ? reader.ReadInt32() : format;
            // A released build (1.4.x, format 1) clamped widths and saved
            // averaged heights, but never checked them. One bad point there
            // costs that point, not the world its whole network.
            bool released = format == ReleasedFormat;
            if (!released && (format != SavedFormat || encoding != SavedEncoding))
                return Refuse($"a format this build does not know (it reads the released format {ReleasedFormat}, and format {SavedFormat} with encoding {SavedEncoding})");
            
            int cellCount = reader.ReadInt32();
            if (cellCount < 0 || cellCount > 1000000)
                return Refuse($"cell count {cellCount} out of range");
            
            var loadedPoints = new Dictionary<Vector2i, RoadPoint[]>(cellCount);
            int totalPoints = 0;
            int dropped = 0;
            // An into-location point's location, bound once per saved centre to this world's location by its identity: the saved
            // centre is the identity the plan carried (RoadLocationProtection.Targets), found again exactly
            // (LocationPrefabLevelling.LocationOf), never resolved again with a tolerance. One gone from the world is no
            // location, and its points are let into no footprint.
            var locations = new Dictionary<(float, float), Vector2>();
            
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
                    bool paintOnly = !released && reader.ReadBoolean();
                    bool cutOnly = !released && reader.ReadBoolean();
                    bool intoLocation = !released && reader.ReadBoolean();
                    bool unpainted = !released && reader.ReadBoolean();
                    bool mound = !released && reader.ReadBoolean();
                    Vector2? location = null;
                    Vector2 savedLocation = default;
                    if (intoLocation)
                    {
                        float sx = reader.ReadSingle(), sy = reader.ReadSingle();
                        savedLocation = new Vector2(sx, sy);
                        Vector2 bound = RoadPoint.NoLocation;
                        if (!received && !(float.IsNaN(sx) && float.IsNaN(sy)) && !locations.TryGetValue((sx, sy), out bound))
                        {
                            bound = LocationPrefabLevelling.LocationOf(savedLocation, out _) ? savedLocation : RoadPoint.NoLocation;
                            locations[(sx, sy)] = bound;
                            if (float.IsNaN(bound.x))
                                Log.LogWarning($"[LOAD] saved road location ({sx}, {sy}) not found; its road points enter no footprint");
                        }
                        location = bound;
                    }
                    int addition = released ? 0 : reader.ReadInt32();
                    if (!ManualRoadDraft.IsFinite(px) || !ManualRoadDraft.IsFinite(py) ||
                        !ManualRoadDraft.IsFinite(w) || w <= 0 || !ManualRoadDraft.IsFinite(h) || addition < 0)
                    {
                        if (!released)
                            return Refuse($"point {i} of cell ({gridX},{gridY}) has position ({px},{py}), width {w}, height {h}, " +
                                          $"addition {addition}: a non-finite value, no width or a negative addition");
                        dropped++;
                        continue;
                    }
                    // This build's formats hold a point to the world (RoadConstants.InWorld); a released one keeps its
                    // reading above.
                    if (!released && !RoadConstants.InWorld(new Vector2(px, py)))
                        return Refuse($"point {i} of cell ({gridX},{gridY}) at ({px},{py}) lies outside the world");
                    points.Add(new RoadPoint(new Vector2(px, py), w, h, paintOnly, addition, cutOnly, intoLocation, unpainted, location, mound) { savedLocation = savedLocation });
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
            if (!released)
            {
                int count = reader.ReadInt32();
                if (count < 0 || count > ManualRoadDraft.MaxAppends)
                    return Refuse($"append block holds {count} parent(s) (0 to {ManualRoadDraft.MaxAppends} expected)");
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
            if(!released)
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
                s_appendReach = new Dictionary<int, HashSet<Vector2s>>();
                s_narrowestFiled = 0f;
                float widest = 0f;
                foreach (var cell in loadedPoints) foreach (var point in cell.Value) if (point.IsRoad && point.w > widest) widest = point.w;
                s_widestFit = widest;
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
