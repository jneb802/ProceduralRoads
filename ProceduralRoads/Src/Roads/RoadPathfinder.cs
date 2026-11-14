using System.Linq;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// A* pathfinding for road generation with 16-direction movement, slope-based cost, and river avoidance.
/// </summary>
public partial class RoadPathfinder
{
    private static ManualLogSource Log => ProceduralRoadsPlugin.ProceduralRoadsLogger;
    
    public const float CellSize = RoadConstants.PathfindingCellSize;
    public static int MaxIterations = RoadConstants.PathfindingMaxIterations;

    /// <summary>The iteration limit of a pricing search (half the building budget, at least 1000).</summary>
    internal static int PricingIterations => System.Math.Max(1000, MaxIterations / 2);

    /// <summary>
    /// Price of steepness per step, times the step's grade squared. At 10 an
    /// 8 m step at 0.33 cost about 1.1 over its 8 for length, so a straight
    /// climb up the fall line was nearly as cheap as flat ground and a
    /// traverse was never worth its extra length: roads went straight up on
    /// dirt walls instead of traversing the slope and switching back.
    /// </summary>
    public float SlopeMultiplier = DefaultSlopeMultiplier;
    /// <summary>What a new pathfinder prices slope at. Settable for tests.</summary>
    internal static float DefaultSlopeMultiplier = RoadConstants.DefaultSlopeMultiplier;
    public float RiverPenalty = RoadConstants.DefaultRiverPenalty;
    public float WaterPenalty = RoadConstants.DefaultWaterPenalty;
    public float SteepSlopePenalty = RoadConstants.DefaultSteepSlopePenalty;
    public float SteepSlopeThreshold = RoadConstants.DefaultSteepSlopeThreshold;
    public float TerrainVariancePenalty = RoadConstants.DefaultTerrainVariancePenalty;
    public float TerrainVarianceThreshold = RoadConstants.DefaultTerrainVarianceThreshold;
    public float BaseCost = RoadConstants.DefaultBaseCost;
    public float StandingWaterPenalty = RoadConstants.DefaultStandingWaterPenalty;

    /// <summary>
    /// Whether a road may jump a knee-deep river, and wade swamp shallows.
    /// On: a road that meets a river it can ford does so rather than going
    /// around. Held per instance so a test can pathfind without it.
    /// </summary>
    public bool Fords = true;

    /// <summary>
    /// Whether a road may jump a river too long or too deep to ford, on a
    /// bridge. How dear a bridge is (<see cref="BridgeCostFixed"/>,
    /// <see cref="BridgeCostPerMeter"/>) decides how often one appears.
    /// </summary>
    public bool Bridges = true;

    public float BridgeCostFixed = RoadConstants.DefaultBridgeCostFixed;
    public float BridgeCostPerMeter = RoadConstants.DefaultBridgeCostPerMeter;
    /// <summary>The share of its price the longest bridge pays with its deck as tall as the stone kit stands
    /// (<see cref="BridgeHeightFactor"/>, <see cref="RoadConstants.DefaultBridgeHeightShare"/>); 1 is off.</summary>
    public float BridgeHeightShare = RoadConstants.DefaultBridgeHeightShare;

    /// <summary>
    /// Whether a road may cross a dry Mistlands valley at the high level on a Dvergr viaduct
    /// (DvergrViaduct) instead of a staircase down into it and another out. From a
    /// Mistlands cell at a valley's rim the search looks straight on, in the eight principal
    /// directions, for ground back at the rim's height within DvergrViaduct.MaxValleySpan, and
    /// buys the jump where DvergrViaduct.ValleyCrossing builds it, at DvergrViaduct.Price: dearer
    /// the lower its piers and the more end stairs it has. The road is brought to each end's foot
    /// as the one owner of a crossing's ends decides (StructureEnds.MeetEnds). Needs
    /// <see cref="Bridges"/>. On by default.
    /// </summary>
    public bool Viaducts = ConfiguredViaducts;
    /// <summary>Whether a searched path stops at its exact goal, its goal cell's centre dropped where the goal lies back toward
    /// the point before it (<see cref="PastItsGoal"/>): a marked road's legs (ManualRoads), whose marks are where the road
    /// ends, with nothing carried on past them. Off (generation, road_connect): the path keeps its goal cell's centre, the
    /// last move the search priced; a generated road's end is carried on past its goal (onto a location's landing, through
    /// its door, along the road it joins), so the centre is not a fold the road ends on. Measured on two worlds, dropping it
    /// on generated roads cost three Mistlands viaducts and four staircases (their last edge no longer the move the search
    /// priced: a way onto a landing on a ridge, a join's staircase not laid), and searches found no route a fifth more often.</summary>
    public bool StopsAtItsGoal;
    public static bool ConfiguredViaducts = true;
    public float ViaductCostFixed = RoadConstants.DefaultViaductCostFixed;
    public float ViaductCostPerMeter = RoadConstants.DefaultViaductCostPerMeter;
    /// <summary>Per metre of a viaduct's end stairs, bank to deck, both ends together (DvergrViaduct.Price).</summary>
    public float ViaductEndStairCostPerMeter = RoadConstants.DefaultViaductEndStairCostPerMeter;
    /// <summary>A valley starts where the next cell is at least this much lower: the rim.</summary>
    public const float ViaductRimDrop = 2f;
    /// <summary>The road's own steps within this many metres of a viaduct's end, before its near bank and after its far
    /// bank along the search's path, cost <see cref="ViaductApproachFactor"/> of their price
    /// (<see cref="RoadConstants.DefaultViaductApproachReach"/>); 0 is off.</summary>
    public float ViaductApproachReach = RoadConstants.DefaultViaductApproachReach;
    /// <summary>What a step within <see cref="ViaductApproachReach"/> of a viaduct's end costs, of its price
    /// (<see cref="RoadConstants.DefaultViaductApproachFactor"/>).</summary>
    public float ViaductApproachFactor = RoadConstants.DefaultViaductApproachFactor;
    /// <summary>The viaduct jumps of the last path found, bank cell to bank cell, in path order.</summary>
    public List<(Vector2 from, Vector2 to)> LastViaductJumps { get; } = new();
    /// <summary>The ground each of <see cref="LastViaductJumps"/> was bought on, by index: the ends and the locations of the
    /// search that bought it (<see cref="SearchGround"/>). A road whose path joins several searches (a manual road's legs)
    /// keeps each jump's own, so its line is asked again on the ground it was bought on (<see cref="MovedViaductAllowed"/>).</summary>
    public List<SearchGround> LastViaductGrounds { get; } = new();

    /// <summary>The ground a search judges its steps and jumps on beside the network: the ends it leaves and arrives at
    /// (NetworkCutoff.AvoidsSegment's exemptions) and the locations whose footprints it may enter
    /// (RoadLocationProtection.BlocksRoute).</summary>
    public readonly struct SearchGround : System.IEquatable<SearchGround>
    {
        public readonly Vector2? Start, End;
        public readonly RoadLocationProtection.Targets Locations;
        public SearchGround(Vector2? start, Vector2? end, RoadLocationProtection.Targets locations) { Start = start; End = end; Locations = locations; }
        // Compared bit for bit, as the rest of the remembered keys are (TerrainFingerprint.Bits): Unity's == on a Vector2 is
        // approximate, and an approximate Equals would disagree with the exact GetHashCode below.
        public bool Equals(SearchGround o) => Exact(Start, o.Start) && Exact(End, o.End) && Exact(Locations.Start, o.Locations.Start) && Exact(Locations.End, o.Locations.End);
        private static bool Exact(Vector2? a, Vector2? b) => a.HasValue == b.HasValue
            && (a is not Vector2 u || TerrainFingerprint.Bits(u.x) == TerrainFingerprint.Bits(b!.Value.x) && TerrainFingerprint.Bits(u.y) == TerrainFingerprint.Bits(b.Value.y));
        public override bool Equals(object? obj) => obj is SearchGround o && Equals(o);
        public override int GetHashCode() => (Start, End, Locations.Start, Locations.End).GetHashCode();
    }

    /// <summary>The ground of the search running, or of the one last run.</summary>
    private SearchGround Ground => new(m_searchStart, m_searchEnd, m_locations);

    /// <summary>
    /// Whether a road may climb ground too steep for it on a staircase. A step
    /// steeper than <see cref="StairMinGrade"/> (and no steeper than
    /// <see cref="StairMaxGrade"/>) may be bought as a stair step instead of
    /// road: <see cref="StairCostPerMeterClimbed"/> for every metre it climbs,
    /// and <see cref="StairCostFixed"/> where a staircase starts, varied a
    /// little from location to location so a world gets long traverses, staircases and
    /// both. The steps bought are kept in
    /// <see cref="LastStairSteps"/>; the generator lays them with StairLayout.
    /// Both costs are fixed (no config for them). Stairs are always on for generated
    /// roads (no setting for them); a search may be made without them (manual roads).
    /// </summary>
    public bool Stairs = true;
    public float StairCostFixed = RoadConstants.DefaultStairCostFixed;
    public float StairCostPerMeterClimbed = RoadConstants.DefaultStairCostPerMeterClimbed;
    /// <summary>Largest step between a network road's surface and the ground a road joining it
    /// arrives on (<see cref="RoadSpatialGrid.TryGetRoadAtGradeWithin"/>). 0 joins anywhere:
    /// settable for tests (the unit suite's plain roads).</summary>
    internal static float JunctionStep = DefaultJunctionStep;
    public const float DefaultJunctionStep = 1.5f;
    /// <summary>Gentler than this a road is fine and a staircase is not offered. 0.25: over five RoadSeed worlds (grade sweep) it lays 261 staircases where 0.30 laid 146, and
    /// connects 8 more destinations; the road cap stays 0.35 (lower caps lose destinations). More stairs
    /// are wanted within reason.</summary>
    public const float StairMinGrade = 0.25f;

    /// <summary>What a staircase of the network makes of a pathfinding cell (<see cref="StaircaseAt"/>).</summary>
    internal enum StaircaseCell : byte
    {
        /// <summary>No staircase stands on it.</summary>
        None,
        /// <summary>One stands on it and a road climbed it there: walked up or down, from a laid stair cell, never run through.</summary>
        Climbed,
        /// <summary>One stands on it and no road climbed it there (a location's approach): closed to every road.</summary>
        Closed,
    }

    /// <summary>The one rule for a staircase standing on a cell: none, one a road climbed there
    /// (<see cref="StairStructures.Standing"/> and <see cref="StairStructures.OnLaidStair"/>), or one no road climbed, which closes
    /// it. The search's step cost asks it of every step (one lookup for a cell no staircase stands on, no allocation); the
    /// network snap asks it through <see cref="ClosedByAStaircase"/>.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static StaircaseCell StaircaseAt(int cellX, int cellZ) =>
        !StairStructures.Standing(cellX, cellZ) ? StaircaseCell.None
        : StairStructures.OnLaidStair(cellX, cellZ) ? StaircaseCell.Climbed : StaircaseCell.Closed;

    /// <summary>Whether no road may be laid at <paramref name="p"/> for a staircase of the network (<see cref="StaircaseAt"/>:
    /// one stands on its cell and no road climbed it there), so the search neither runs through it nor walks it. Asked of a
    /// point the search did not choose, as the network snap moves one (<see cref="ClosedToARoad"/>).</summary>
    internal static bool ClosedByAStaircase(Vector2 p) =>
        StaircaseAt(Mathf.RoundToInt(p.x / CellSize), Mathf.RoundToInt(p.y / CellSize)) == StaircaseCell.Closed;

    /// <summary>Whether the search keeps every road off <paramref name="p"/>'s cell for a structure of the network: a staircase
    /// no road climbed there (<see cref="ClosedByAStaircase"/>) or a viaduct's deck standing over it
    /// (<see cref="DvergrViaduct.Standing"/>, which a road uses only by its own jump, bank to bank), as the step cost refuses
    /// them. Asked of a point the search did not choose: the network snap never moves a waypoint onto one
    /// (<see cref="RoadNetworkGenerator.SnapToNetwork"/>).</summary>
    internal static bool ClosedToARoad(Vector2 p) =>
        ClosedByAStaircase(p) || DvergrViaduct.Standing(Mathf.RoundToInt(p.x / CellSize), Mathf.RoundToInt(p.y / CellSize));
    /// <summary>The grade up to which a stair step's climb price has no steepness surcharge
    /// (<see cref="SteepStairFactor"/>).</summary>
    public const float StairMaxGrade = 1f;
    /// <summary>
    /// A staircase whose stairs can be held out from the face (StairSupport)
    /// is offered up to this grade between two cells: effectively a sheer wall (between two 8 m cells, 64 m of climb).
    /// The layout holds a flight out from any face too high for a wall (<see cref="StairSupport"/>), so
    /// the search does not cap below what the layout builds. Over six worlds a cap of 2, 3, 4 or 8 builds the same
    /// network, and Hildir's cave (-2304,1536) keeps its staircase.
    /// </summary>
    public const float StairCantileverMaxGrade = 8f;
    public const float SteepStairPriceMax = 2.5f;

    /// <summary>From this grade on a staircase's flights are held out from the face (<see cref="SteepStairFactor"/>).</summary>
    public const float StairHeldFromGrade = 2f;

    /// <summary>Slack over <see cref="StairCantileverMaxGrade"/> before a step is too steep.</summary>
    internal const float StairGradeTolerance = 0.01f;

    /// <summary>Whether a stair step of <paramref name="grade"/> is too steep: the one test for
    /// the search's offer of a step and the layout's check of the steps the search bought.</summary>
    internal static bool StairStepTooSteep(float grade) => grade > StairCantileverMaxGrade + StairGradeTolerance;

    /// <summary>Times the price per metre climbed for a stair step this steep: 1 up to <see cref="StairMaxGrade"/>, rising to
    /// <see cref="SteepStairPriceMax"/> on a face steep enough that its flights are held out
    /// (<see cref="StairHeldFromGrade"/>), and that price on any steeper face (over a certain grade it is
    /// more expensive but still possible: cheaper lesser grades first, then up to a maximum).</summary>
    public static float SteepStairFactor(float slope) =>
        Mathf.Lerp(1f, SteepStairPriceMax, Mathf.Clamp01((slope - StairMaxGrade) / (StairHeldFromGrade - StairMaxGrade)));

    /// <summary>The site wobble on <see cref="StairCostFixed"/>: from (1 - this) to (1 + this) times it.</summary>
    public const float StairCostWobble = 0.3f;

    /// <summary>The stair steps of the last path found, from cell centre to cell centre.</summary>
    public List<(Vector2 from, Vector2 to)> LastStairSteps { get; } = new();

    /// <summary>Cells closed to stairs for the road being built: a staircase the search bought
    /// there could not be laid, so the road is searched again with stairs open everywhere else
    /// (RoadNetworkGenerator.GenerateRoad). Cleared, with <see cref="m_stairOpen"/>, when that road is done.</summary>
    private readonly HashSet<Vector2i> m_noStairs = new(RoadGridComparer.Instance);
    public int NoStairCells => m_noStairs.Count;
    /// <summary>Release every exclusion and price belonging to the road that just ended, including a failed trial.</summary>
    internal void EndRoad()
    {
        ClearNoStairs();
        ClearEarthworkPenalty();
    }

    public void ClearNoStairs() { m_noStairs.Clear(); m_stairOpen.Clear(); m_noJumps.Clear(); m_noViaducts.Clear(); m_gridOnly.Clear(); }

    /// <summary>Mistlands viaducts only: the valley viaducts the road being built may not take, each the one jump it is
    /// (DvergrViaduct.SameJump, within DvergrViaduct.JumpMatch either way round): one with an end no way of meeting reaches, or
    /// one on a road refused with it (RoadNetworkGenerator.BanForTheNextSearch). Its neighbours (a cell shorter or longer, a
    /// column over) stay open: a valley viaduct's banks are cell centres, and the one-cell match of a water crossing's ban
    /// (<see cref="Banned"/>, whose banks are detected points) closed the one jump beside a refused one that its ends met.
    /// Cleared with the stair closings when that road is done.</summary>
    private readonly List<(Vector2 from, Vector2 to)> m_noViaducts = new();
    public void BanViaduct(Vector2 from, Vector2 to) => m_noViaducts.Add((from, to));
    private bool BannedViaduct(Vector2 from, Vector2 to)
    {
        foreach (var (a, b) in m_noViaducts)
            if (DvergrViaduct.SameJump(from, to, a, b)) return true;
        return false;
    }


    /// <summary>Sites the road being built crosses on the grid's eight headings only: a crossing on a knight's heading there had
    /// an end no way of meeting reaches (StructureEnds.MeetEnds), and the road is searched again for the same site's crossing on
    /// a grid heading before that counts against its crossing bans (RoadNetworkGenerator). A water or valley jump on a knight's
    /// heading whose middle lies within a site's radius is not taken. Cleared with the stair closings when that road is done.</summary>
    private readonly List<(Vector2 at, float radius)> m_gridOnly = new();
    public void KeepToGridHeadingsAt(Vector2 at, float radius) => m_gridOnly.Add((at, radius));
    private bool GridOnlyAt(Vector2 from, Vector2 to)
    {
        Vector2 middle = (from + to) * 0.5f;
        foreach (var (at, radius) in m_gridOnly)
            if (Vector2.Distance(middle, at) <= radius) return true;
        return false;
    }

    /// <summary>Whether a jump of (<paramref name="dx"/>, <paramref name="dz"/>) cells is on a knight's heading.</summary>
    private static bool KnightJump(int dx, int dz)
    {
        Vector2Int d = JumpDirection(dx, dz);
        return Mathf.Abs(d.x) > 1 || Mathf.Abs(d.y) > 1;
    }

    /// <summary>Crossings the road being built may not take, either way: one with an end no way of meeting reaches
    /// (StructureEnds.MeetEnds), or a valley viaduct the built road did not allow (RoadNetworkGenerator), searched again
    /// without it. A jump, or a step over shallow water, from within a cell of one end to within a cell of the other is the
    /// same site. Cleared with the stair closings when that road is done.</summary>
    private readonly List<(Vector2 from, Vector2 to)> m_noJumps = new();
    public void BanJump(Vector2 from, Vector2 to) => m_noJumps.Add((from, to));
    private bool Banned(Vector2 from, Vector2 to)
    {
        foreach (var (a, b) in m_noJumps)
            if ((Vector2.Distance(from, a) <= CellSize && Vector2.Distance(to, b) <= CellSize)
                || (Vector2.Distance(from, b) <= CellSize && Vector2.Distance(to, a) <= CellSize))
                return true;
        return false;
    }

    /// <summary>Cells opened to stairs for the road being built: its switchback turns there were
    /// too close to fit their curves, so the road is searched again climbing that stretch on stairs
    /// and back to road beyond it. In them a staircase is offered on any step steep
    /// enough for one, not only where road cannot climb, with no entry price and at
    /// <see cref="OpenStairPrice"/> of the rest: at half its price, entry included, the search kept
    /// the switchbacks it had (the 1000 entry outweighs a 24 m climb of road).</summary>
    private readonly HashSet<Vector2i> m_stairOpen = new(RoadGridComparer.Instance);
    public int OpenStairCells => m_stairOpen.Count;
    /// <summary>A staircase's price in cells opened to stairs, entry aside, as a share of its price elsewhere.</summary>
    internal const float OpenStairPrice = 0.5f;
    private bool OpenToStairsAt(Vector2i from, Vector2i to) =>
        m_stairOpen.Count > 0 && (m_stairOpen.Contains(from) || m_stairOpen.Contains(to));
    /// <summary>Open the cells of these points, and <paramref name="reachCells"/> round each, to stairs; the cells not open
    /// before.</summary>
    public List<Vector2i> OpenToStairs(IEnumerable<Vector2> points, int reachCells) => MarkCells(m_stairOpen, points, reachCells);
    /// <summary>Shut cells opened to stairs again.</summary>
    public void ShutToStairs(IEnumerable<Vector2i> cells) { foreach (var c in cells) m_stairOpen.Remove(c); }
    /// <summary>Close the cells of these points, and <paramref name="reachCells"/> round each, to stairs.</summary>
    public void CloseToStairs(IEnumerable<Vector2> points, int reachCells) => MarkCells(m_noStairs, points, reachCells);

    private List<Vector2i> MarkCells(HashSet<Vector2i> cells, IEnumerable<Vector2> points, int reachCells)
    {
        var added = new List<Vector2i>();
        foreach (Vector2 p in points)
        {
            Vector2i c = WorldToGrid(p);
            for (int dx = -reachCells; dx <= reachCells; dx++)
                for (int dz = -reachCells; dz <= reachCells; dz++)
                    if (cells.Add(new Vector2i(c.x + dx, c.y + dz))) added.Add(new Vector2i(c.x + dx, c.y + dz));
        }
        return added;
    }

    /// <summary>
    /// The steepest ground a road may climb, as rise over run; 0 for no cap.
    /// A step over it is refused outright rather than priced, so the search
    /// has to cross the slope - which the 16 directions already allow - or
    /// fail and leave the destination unconnected. Every other steep case
    /// here is a large but finite price and so is never a refusal, which is
    /// why a destination on a cliff got the direct climb: it was the cheapest
    /// of the expensive lines. Held per instance so a test can pathfind
    /// without it. The cap is <see cref="RoadGrade.Configured"/>.
    /// </summary>
    public float MaxGrade = RoadGrade.Configured;

    /// <summary>
    /// A step from existing road onto existing road costs this share of its
    /// price, so a later road merges onto a road already built and branches
    /// off where it must, instead of running beside it: a location-to-location
    /// search had no reason to prefer the road over the ground beside it, and
    /// with the meander and the sway, road with another 3.5-10 m beside it on
    /// the same heading grew by half. The search heuristic still assumes full
    /// price, so it may settle for a slightly dearer route.
    /// </summary>
    public float ReuseFactor = DefaultReuseFactor;
    /// <summary>Settable for tests; 1 prices road already built like new ground.</summary>
    internal static float DefaultReuseFactor = 0.5f;
    /// <summary>Returned by GetMoveCost for a step no road may take, as
    /// against the water and river penalties, which say only that a step is
    /// dear. Nothing may buy its way past this one.</summary>
    public const float Impassable = float.PositiveInfinity;

    internal static readonly Vector2Int[] Directions = new Vector2Int[]
    {
        new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1),
        new Vector2Int(1, 1), new Vector2Int(-1, 1), new Vector2Int(1, -1), new Vector2Int(-1, -1),
        new Vector2Int(2, 1), new Vector2Int(2, -1), new Vector2Int(-2, 1), new Vector2Int(-2, -1),
        new Vector2Int(1, 2), new Vector2Int(-1, 2), new Vector2Int(1, -2), new Vector2Int(-1, -2),
    };

    private static readonly float[] DirectionCosts;

    static RoadPathfinder()
    {
        DirectionCosts = new float[Directions.Length];
        for (int i = 0; i < Directions.Length; i++)
            DirectionCosts[i] = Mathf.Sqrt(Directions[i].x * Directions[i].x + Directions[i].y * Directions[i].y);
    }

    private WorldGenerator m_worldGen;
    private readonly RoadTerrainSamples m_terrain;
    private Vector2? m_searchStart, m_searchEnd;
    private System.Func<Vector2i, Vector2?>? m_crossingDestination;
    /// <summary>The locations the search leaves and arrives at, by identity: the only footprints it may enter (RoadLocationProtection.BlocksRoute).</summary>
    private RoadLocationProtection.Targets m_locations;
    public float LocationClearance = RoadLocationProtection.Clearance(RoadConstants.DefaultRoadWidth);

    /// <summary>Roughness about a quadratic surface; see RoadTerrainSamples.QuadVariance.</summary>
    internal bool QuadVariance { get => m_terrain.QuadVariance; set => m_terrain.QuadVariance = value; }

    public RoadPathfinder(WorldGenerator worldGen)
    {
        m_worldGen = worldGen;
        m_terrain = new RoadTerrainSamples(worldGen);
    }

    /// <summary>Hand this pathfinder's terrain-memo counters to the
    /// generation totals and clear them. Called when the pathfinder is
    /// finished with, so a discarded one does not take its numbers with it.</summary>
    internal void FoldTerrainMemoCounters() => m_terrain.FoldIntoTotals();

    public float LastPathCost { get; private set; }

    internal RoadIslandActivity.Token? Activity;
    /// <summary>Set by FindPathToNetwork when its caller's served test says the start is served already, so a null result
    /// can be told apart from a failure.</summary>
    public bool AlreadyOnNetwork { get; private set; }

    /// <summary>The one owner of where a road may go (docs/OWNERS.md).</summary>
    /// <param name="locations">The locations the road leaves and arrives at (their identity, RoadLocationProtection.Targets): the only
    /// footprints it may cross, as its departure and arrival. None by default: a road between two marks enters none.</param>
    /// <param name="joinedThrough">Where <paramref name="end"/> is a junction a join search chose (FindPathToNetwork), the
    /// network cell that search arrived at it through: the road ends through that cell, arriving into it only as
    /// <see cref="ArrivesLevel"/> allows, as the join search judged its arrival there. None for every other end.</param>
    public List<Vector2>? FindPath(Vector2 start, Vector2 end, RoadLocationProtection.Targets locations = default, Vector2? joinedThrough = null) =>
        FindPath(start, end, null, locations, joinedThrough);

    public List<Vector2>? FindPath(Vector2 start, Vector2 end, int? iterationLimit, RoadLocationProtection.Targets locations = default,
        Vector2? joinedThrough = null)
    {
        long began = SearchStats.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        m_lastIterations = 0;
        List<Vector2>? path = FindPathCore(new[] { (start, 0f) }, new[] { (end, 0f) }, start, end, locations, iterationLimit, joinedThrough);
        FoldSqueezes();
        if (SearchStats.Enabled)
            SearchStats.Record(iterationLimit.HasValue ? SearchStats.Pricing : SearchStats.Building, path != null,
                m_lastIterations >= (iterationLimit ?? MaxIterations), m_lastIterations, began);
        return path;
    }

    private int m_lastIterations;

    /// <summary>
    /// A search from any of <paramref name="starts"/> to any of <paramref name="ends"/>, each a point and what beginning
    /// or ending there costs on top of the road's own price (a location's doors, RoadNetworkGenerator.DoorsOf): the cheapest
    /// road from a start to an end with those costs counted, so the end the search finds is the one it aimed at.
    /// <see cref="LastStartIndex"/> and <see cref="LastEndIndex"/> say which. <paramref name="exemptStart"/> and
    /// <paramref name="exemptEnd"/>: the points whose cut-off zones the search may cross, as a search's own ends' are
    /// (NetworkCutoff.AvoidsSegment); <paramref name="locations"/>: the locations whose footprints it may cross
    /// (RoadLocationProtection.BlocksRoute), by identity; <paramref name="joinedThrough"/>: as <see cref="FindPath(Vector2, Vector2, int?, RoadLocationProtection.Targets, Vector2?)"/>,
    /// for a single end that is a junction a join search chose.
    /// The one owner of where a road may go (docs/OWNERS.md).
    /// </summary>
    public List<Vector2>? FindPathBetween(IReadOnlyList<(Vector2 at, float cost)> starts, IReadOnlyList<(Vector2 at, float cost)> ends,
        Vector2? exemptStart, Vector2? exemptEnd, RoadLocationProtection.Targets locations = default, Vector2? joinedThrough = null)
    {
        long began = SearchStats.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        m_lastIterations = 0;
        List<Vector2>? path = FindPathCore(starts, ends, exemptStart, exemptEnd, locations, null, joinedThrough);
        FoldSqueezes();
        if (SearchStats.Enabled)
            SearchStats.Record(SearchStats.Building, path != null, m_lastIterations >= MaxIterations, m_lastIterations, began);
        return path;
    }

    /// <summary>Which of the starts and of the ends the last <see cref="FindPathBetween"/> found its road between.</summary>
    public int LastStartIndex { get; private set; }
    public int LastEndIndex { get; private set; }

    /// <summary>The open set's entry for "ended at end <c>e</c>", priced with what ending there costs: never a cell.</summary>
    private const int FinishMark = int.MinValue;

    /// <summary>The search (<see cref="SearchCore"/>), or, while this pathfinder remembers its searches
    /// (<see cref="RemembersSearches"/>), the one it made already on the same network with the same asking, answered as it was
    /// (<see cref="Remembered"/>).</summary>
    private List<Vector2>? FindPathCore(IReadOnlyList<(Vector2 at, float cost)> starts, IReadOnlyList<(Vector2 at, float cost)> ends,
        Vector2? exemptStart, Vector2? exemptEnd, RoadLocationProtection.Targets locations, int? iterationLimit, Vector2? joinedThrough)
    {
        if (!RemembersSearches) return SearchCore(starts, ends, exemptStart, exemptEnd, locations, iterationLimit, joinedThrough);
        SearchAsked asked = Asking(starts, ends, exemptStart, exemptEnd, locations, iterationLimit, joinedThrough);
        if (Recall(asked) is SearchAnswered known) return Replay(known, exemptStart, exemptEnd, locations);
        int squeezesBefore = m_squeezeHits.Count;
        var squeezedBefore = squeezesBefore == 0 ? null : new Dictionary<(int x, int z), (int steps, int landings)>(m_squeezeHits);
        List<Vector2>? path = SearchCore(starts, ends, exemptStart, exemptEnd, locations, iterationLimit, joinedThrough);
        Remember(asked, path, squeezedBefore);
        return path;
    }

    private List<Vector2>? SearchCore(IReadOnlyList<(Vector2 at, float cost)> starts, IReadOnlyList<(Vector2 at, float cost)> ends,
        Vector2? exemptStart, Vector2? exemptEnd, RoadLocationProtection.Targets locations, int? iterationLimit, Vector2? joinedThrough)
    {
        using var activity = new RoadSearchActivity(Activity);
        m_lastFailure = null;
        // The memo is NOT blanked here. It holds world-generator facts, and
        // the generator a pathfinder was built for does not change underneath
        // it: a pathfinder is built at the start of a generation and thrown
        // away at the end of it. Blanking between searches threw away answers
        // that were still true and bought nothing.
        m_searchStart = exemptStart;
        m_searchEnd = exemptEnd;
        m_locations = locations;

        LastPathCost = 0;
        LastStairSteps.Clear();
        LastViaductJumps.Clear();
        LastViaductGrounds.Clear();
        LastStartIndex = LastEndIndex = 0;
        Vector2 start = starts[0].at, end = ends[0].at;
        Vector2i startGrid = WorldToGrid(start);
        // A junction a join search chose (FindPathToNetwork): the road ends at it through the network cell that search
        // arrived at it through, and arrives into that cell as the search judged its arrival there (ArrivesLevel): only a
        // level arrival ends the search, kept until it is the cheapest thing left; an off-grade one is not an end, and
        // the cell stays open to every other direction. Searched to the junction's own cell with no arrival test, the
        // road came straight down a cut's side where its search had arrived along the cut. Not where the join search
        // judges no arrival (no junction step).
        bool joining = joinedThrough.HasValue && ends.Count == 1 && JunctionStep > 0f;
        Vector2i endGrid = joining ? WorldToGrid(joinedThrough!.Value) : WorldToGrid(end);

        if (starts.Count == 1 && ends.Count == 1 && startGrid == endGrid)
            return new List<Vector2> { start, end };

        // Each end's cell, the cheapest end where two share one. With one end the bound is the distance to it; with
        // several, the distance to the circle round them all, never more than to the nearest (a consistent bound).
        var goalCells = new Vector2i[ends.Count];
        var goalAt = new Dictionary<Vector2i, int>(RoadGridComparer.Instance);
        float leastCost = float.PositiveInfinity, far = 0f, mx = 0f, mz = 0f;
        for (int e = 0; e < ends.Count; e++)
        {
            goalCells[e] = joining ? endGrid : WorldToGrid(ends[e].at);
            if (!goalAt.TryGetValue(goalCells[e], out int had) || ends[e].cost < ends[had].cost) goalAt[goalCells[e]] = e;
            leastCost = Mathf.Min(leastCost, ends[e].cost);
            mx += CellCentre(goalCells[e]).x / ends.Count;
            mz += CellCentre(goalCells[e]).y / ends.Count;
        }
        var middle = new Vector2(mx, mz);
        for (int e = 0; e < ends.Count; e++) far = Mathf.Max(far, (CellCentre(goalCells[e]) - middle).magnitude);
        // On the discounted stretch after a viaduct's far bank, less the most that stretch can take off (a consistent bound).
        Dictionary<Vector2i, float>? stretchLeft = null;
        float Bound(Vector2i key)
        {
            Vector2i from = CellOf(key);
            float bound = ends.Count == 1 ? Heuristic(from, endGrid) : Mathf.Max(0f, (CellCentre(from) - middle).magnitude - far);
            if (OnApproach(key)) bound = Mathf.Max(0f, bound - (1f - ViaductApproachFactor) * ApproachLeft(stretchLeft!, key));
            return bound + leastCost;
        }
        var startAt = new Dictionary<Vector2i, int>(RoadGridComparer.Instance);
        // The cheapest state each end's cell was taken in (a plain cell or on a viaduct's discounted stretch).
        var reachedAt = new Vector2i[ends.Count];
        var reachedCost = new float[ends.Count];
        for (int e = 0; e < ends.Count; e++) { reachedAt[e] = goalCells[e]; reachedCost[e] = float.PositiveInfinity; }

        SearchState state = RentSearchState();
        try
        {
        OpenHeap openSet = state.Open;
        Dictionary<Vector2i, float> gCosts = state.GCosts;
        Dictionary<Vector2i, Vector2i> cameFrom = state.CameFrom;
        HashSet<Vector2i> closedSet = state.Closed;
        // The cells reached on a staircase, and which way it was going there (+1 up, -1 down).
        Dictionary<Vector2i, int> byStair = state.ByStair;
        // The cells reached by a viaduct's jump.
        HashSet<Vector2i> byViaduct = state.ByViaduct;
        stretchLeft = state.StretchLeft;

        for (int b = 0; b < starts.Count; b++)
        {
            Vector2i cell = WorldToGrid(starts[b].at);
            if (gCosts.TryGetValue(cell, out float had) && had <= starts[b].cost) continue;
            startAt[cell] = b;
            gCosts[cell] = starts[b].cost;
            openSet.Push((starts[b].cost + Bound(cell), cell));
        }

        int iterations = 0;
        // The cheapest level arrival into a joined junction's network cell found so far (joining), its end entry in the open set.
        bool haveGoal = false;
        float goalCost = 0f;
        Vector2i goalBefore = default;
        int goalStair = 0;

        m_crossingDestination = cell => goalAt.TryGetValue(cell, out int e) ? ends[e].at : null;
        while (openSet.TrimClosed(closedSet) && iterations < (iterationLimit ?? MaxIterations))
        {
            // Every 256 iterations: a cancelled generation drains within milliseconds, without a
            // static read and token check on each of the millions of nodes a world searches.
            if ((iterations & 255) == 0) RoadLifecycleManager.ThrowIfCancellationRequested();
            iterations++;
            activity.Step();
            m_lastIterations = iterations;

            var current = openSet.Pop();
            Vector2i currentPos = current.pos;

            if (currentPos.x == FinishMark)
                return Finished(currentPos.y, joining);
            // The state's cell; on a viaduct's discounted stretch, the metres of it left.
            Vector2i currentCell = CellOf(currentPos);
            bool onApproach = OnApproach(currentPos);
            float approachLeft = onApproach ? ApproachLeft(stretchLeft, currentPos) : 0f;
            // A joined junction's cell ends the search here only as a start (no arrival to judge), as the join search's first cell.
            if (goalAt.TryGetValue(currentCell, out int goal) && !(joining && cameFrom.ContainsKey(currentPos)))
            {
                // Ended here at no more than the bound promised: no cheaper end is left. Else the end goes in the open
                // set at its full price, and the search carries on past it.
                if (gCosts[currentPos] < reachedCost[goal]) { reachedCost[goal] = gCosts[currentPos]; reachedAt[goal] = currentPos; }
                if (ends[goal].cost <= Bound(currentPos)) return Finished(goal, false);
                openSet.Push((gCosts[currentPos] + ends[goal].cost, new Vector2i(FinishMark, goal)));
            }

            closedSet.Add(currentPos);
            // Neither changes while the cell's neighbours are relaxed: only other cells are written.
            float currentG = gCosts[currentPos];
            int arriving = byStair.TryGetValue(currentPos, out int stairHere) ? stairHere : 0;
            // A start's own cell, the path's first: the road leaves it from the exact start, not its centre (ReconstructPath).
            Vector2? leaving = !cameFrom.ContainsKey(currentPos) && startAt.TryGetValue(currentCell, out int leftFrom)
                ? starts[leftFrom].at : null;

            for (int i = 0; i < Directions.Length; i++)
            {
                Vector2i neighborPos = new Vector2i(currentCell.x + Directions[i].x, currentCell.y + Directions[i].y);
                // The state a plain step arrives in: on the discounted stretch while some of it is left past the step.
                Vector2i target = onApproach ? ApproachNext(approachLeft, neighborPos, i) : neighborPos;

                // A closed cell is still a joined junction's end for a level arrival from here.
                bool closed = closedSet.Contains(target);
                if (closed && !(joining && neighborPos == endGrid))
                    continue;

                float moveCost = GetMoveCost(currentCell, neighborPos, i, arriving, out int stair);
                if (!float.IsPositiveInfinity(moveCost) && moveCost < RiverPenalty)
                {
                    moveCost += TurnCost(cameFrom, currentPos, i);
                    if (onApproach) moveCost = ApproachAfterJump(approachLeft, i, moveCost);
                }
                // Refused outright, not merely dear: kept apart from the price
                // test below because the two mean different things to a reader
                // and, once there are crossings, to the search.
                if (float.IsPositiveInfinity(moveCost))
                    continue;
                if (moveCost >= RiverPenalty)
                {
                    // A blocked neighbour may be the near edge of a river:
                    // with fords on, look for dry ground on the far side
                    // and take the whole crossing as one move.
                    if (!(Fords || Bridges) || !TryGetRiverCrossing(currentCell, leaving, Directions[i], out Vector2i landing, out float crossingCost))
                        continue;
                    closed = closedSet.Contains(landing);
                    if (closed && !(joining && landing == endGrid))
                        continue;
                    // Not discounted, and the end of any discounted stretch: it lands on the plain cell.
                    neighborPos = target = landing;
                    // The turn limit and price bind a crossing as they bind every other step (TurnCost, the one owner): a
                    // crossing turned back sharper than the limit on the step before it is no move.
                    moveCost = crossingCost + TurnCost(cameFrom, currentPos, i);
                    if (float.IsPositiveInfinity(moveCost)) continue;
                    stair = 0;
                }

                float tentativeG = currentG + moveCost;
                // Arriving level at a joined junction's cell: the search may end there (but not back along its own path).
                if (joining && neighborPos == endGrid && (!haveGoal || tentativeG < goalCost)
                    && ArrivesLevel(end, currentPos, state, cameFrom) && !OnPathTo(cameFrom, currentPos, neighborPos))
                {
                    haveGoal = true;
                    goalCost = tentativeG;
                    goalBefore = currentPos;
                    goalStair = stair;
                    openSet.Push((tentativeG + ends[0].cost, new Vector2i(FinishMark, 0)));
                }
                if (closed) continue;

                if (!gCosts.TryGetValue(target, out float existingG) || tentativeG < existingG)
                {
                    cameFrom[target] = currentPos;
                    if (stair != 0) byStair[target] = stair; else byStair.Remove(target);
                    byViaduct.Remove(target);
                    if (OnApproach(target)) stretchLeft[target] = approachLeft - DirectionCosts[i] * CellSize;
                    gCosts[target] = tentativeG;
                    openSet.Push((tentativeG + Bound(target), target));
                }
            }

            // Across a Mistlands valley at the rim's height, on a viaduct. Not from the start cell: the path begins at
            // the exact start, not that cell's centre, so the jump could not be found on it again.
            bool atStart = startAt.ContainsKey(currentCell);
            for (int d = 0; Viaducts && Bridges && !atStart && d < RoadRules.ViaductHeadings; d++)
            {
                if (!TryGetViaduct(currentCell, d, out Vector2i landing, out float jumpCost))
                    continue;
                // The far bank starts the discounted stretch after it.
                Vector2i landed = ViaductApproachReach > 0f ? ApproachKey(landing) : landing;
                if (closedSet.Contains(landed) || !ClearOfJumpsBefore(currentPos, landing, cameFrom, byViaduct))
                    continue;
                // A viaduct's jump pays the turn's price, not its limit (ViaductTurnPrice; Mistlands viaducts only).
                float turn = ViaductTurnPrice(cameFrom, currentPos, d);
                float tentativeG = currentG + turn + ApproachToJump(currentPos, landing, jumpCost, cameFrom, gCosts, byViaduct);
                if (!gCosts.TryGetValue(landed, out float existingG) || tentativeG < existingG)
                {
                    cameFrom[landed] = currentPos;
                    byStair.Remove(landed);
                    byViaduct.Add(landed);
                    if (OnApproach(landed)) stretchLeft[landed] = ViaductApproachReach;
                    gCosts[landed] = tentativeG;
                    openSet.Push((tentativeG + Bound(landed), landed));
                }
            }
        }

        // Keep a level junction already found when the budget ends, as the join search does.
        if (haveGoal) return Finished(0, true);

        string reason = !openSet.TrimClosed(closedSet) ? "no reachable path" : "max iterations reached";
        // Not a fault (screening declines pairs; the totals are in the closing summary): debug level.
        m_lastFailure = $"Pathfinding failed: {reason} after {iterations} iterations";
        Log.LogDebug(m_lastFailure);
        return null;

        // The road to end e, from the start its cells lead back to; arrived: by the level arrival kept for it (joining).
        List<Vector2> Finished(int e, bool arrived)
        {
            Vector2i cell = arrived ? goalCells[e] : reachedAt[e], root = cell;
            if (arrived)
            {
                cameFrom[cell] = goalBefore;
                if (goalStair != 0) byStair[cell] = goalStair; else byStair.Remove(cell);
                byViaduct.Remove(cell); // arrived by a step or a crossing, never by a viaduct's jump
                gCosts[cell] = goalCost;
            }
            while (cameFrom.TryGetValue(root, out Vector2i before)) root = before;
            LastStartIndex = startAt.TryGetValue(root, out int b) ? b : 0;
            LastEndIndex = e;
            LastPathCost = gCosts[cell];
            return ReconstructPath(cameFrom, byStair, byViaduct, cell, starts[LastStartIndex].at, ends[e].at);
        }
        }
        finally { m_crossingDestination = null; ReturnSearchState(state); }
    }

    /// <summary>Search from a destination towards any existing road. Sampled hints
    /// guide the search; they do not promise a globally cheapest junction.
    /// <paramref name="served"/>: whether the location the search starts from is already served by the network
    /// (RoadNetworkGenerator.Reached), the caller's own test of it (RoadNetworkGenerator.JoinTo's), asked first
    /// (AlreadyOnNetwork). Not served, a junction at the first cell is one the search finds like any other: a path from the
    /// start to it, a candidate to build to, never a way already walked.
    /// <paramref name="location"/>: the location it starts from, by identity (RoadLocationProtection.Footprint.Is), the one footprint
    /// it may leave through; none for a point. The one owner of where a road may go (docs/OWNERS.md).</summary>
    public List<Vector2>? FindPathToNetwork(Vector2 start, float reach, System.Func<bool> served, IReadOnlyList<Vector2>? hints = null,
        System.Func<Vector2, bool>? targetAllowed = null, Vector2? location = null)
    {
        long began = SearchStats.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        m_lastIterations = 0;
        List<Vector2>? path = FindPathToNetworkCore(start, reach, hints, targetAllowed, served, location);
        FoldSqueezes();
        if (SearchStats.Enabled)
            SearchStats.Record(SearchStats.Joining, path != null || AlreadyOnNetwork, m_lastIterations >= MaxIterations, m_lastIterations, began);
        return path;
    }

    private List<Vector2>? FindPathToNetworkCore(Vector2 start, float reach, IReadOnlyList<Vector2>? hints, System.Func<Vector2, bool>? targetAllowed,
        System.Func<bool> served, Vector2? location)
    {
        using var activity = new RoadSearchActivity(Activity);
        LastPathCost = 0;
        // Kept between searches, for the reason given in FindPath.
        m_searchStart = start;
        m_searchEnd = null;
        m_locations = new RoadLocationProtection.Targets(location, null);
        AlreadyOnNetwork = false;
        LastStairSteps.Clear();
        LastViaductJumps.Clear();
        LastViaductGrounds.Clear();
        Vector2i startGrid = WorldToGrid(start);
        SearchState state = RentSearchState();
        try
        {

        // Each cell's distance to the hints is fixed for the search, and the hints are the island's whole list (a point
        // per 64 m of every road built): worked out once per cell, not once per relaxation.
        var heuristics = state.Heuristics;
        float NetworkHeuristic(Vector2i cell)
        {
            if (hints == null || hints.Count == 0) return 0f;
            if (heuristics.TryGetValue(cell, out float known)) return known;
            return heuristics[cell] = HintDistance(cell);
        }
        float HintDistance(Vector2i cell)
        {
            Vector2 world = CellCentre(cell);
            float best = float.MaxValue;
            for (int i = 0; i < hints.Count; i++)
            {
                float dx = hints[i].x - world.x, dy = hints[i].y - world.y;
                float d = dx * dx + dy * dy;
                if (d < best) best = d;
            }
            return Mathf.Max(0f, Mathf.Sqrt(best) - reach);
        }

        // A road joins the network where the road there stands within JunctionStep of the ground it
        // arrives on; where the network is on a fill or in a cut, the search carries on along it.
        // The cheap lookup first: most cells have no road in reach, and the ground is only read where
        // one is, once a cell per search. Read for every cell, the world generator's height made in-game
        // join searches twice as slow. The ground is the one every junction rule reads
        // (RoadSpatialGrid.JunctionGround), not the search's raw height: near a biome border the two
        // differ, and a trunk the branch rule (TrunkBranch) called at grade the search did not.
        bool Join(Vector2i cell, out Vector2 junction)
        {
            Vector2 at = CellCentre(cell);
            if (!RoadSpatialGrid.TryGetRoadWithin(at, reach, out junction, targetAllowed)) return false;
            return JunctionStep <= 0f
                || RoadSpatialGrid.TryGetRoadAtGradeWithin(at, reach, JunctionGroundAt(cell, state), JunctionStep, out junction, targetAllowed);
        }

        if (served())
        {
            // Already served, by the caller's own test of the exact point. This is not a failure: a null with
            // AlreadyOnNetwork tells the caller not to build a road to a location the network already serves.
            LastPathCost = 0f;
            AlreadyOnNetwork = true;
            return null;
        }

        OpenHeap openSet = state.Open;
        Dictionary<Vector2i, float> gCosts = state.GCosts;
        Dictionary<Vector2i, Vector2i> cameFrom = state.CameFrom;
        HashSet<Vector2i> closedSet = state.Closed;
        Dictionary<Vector2i, int> byStair = state.ByStair;
        HashSet<Vector2i> byViaduct = state.ByViaduct;
        Dictionary<Vector2i, float> stretchLeft = state.StretchLeft;
        // On the discounted stretch after a viaduct's far bank, less the most that stretch can take off (a consistent bound).
        float Bound(Vector2i key) => OnApproach(key)
            ? Mathf.Max(0f, NetworkHeuristic(CellOf(key)) - (1f - ViaductApproachFactor) * ApproachLeft(stretchLeft, key))
            : NetworkHeuristic(key);

        openSet.Push((NetworkHeuristic(startGrid), startGrid));
        gCosts[startGrid] = 0f;
        int iterations = 0;
        // A junction is judged per arrival, not per cell: the step into a network cell that arrives level with
        // the road there may end the search there (a goal, kept here until it is the cheapest thing left);
        // one arriving off grade may not (it is refused, not priced), and the cell stays open to every other
        // direction (off-grade arrival directions are disallowed, not priced; a different arrival direction that is
        // on grade is still allowed).
        // Judged when the cell was popped, its cheapest arrival decided for all of them, and the cell was
        // closed: a level arrival from another neighbour was never tried.
        (float priority, Vector2i pos) goalKey = default;
        bool haveGoal = false;
        float goalCost = 0f;
        Vector2i goalBefore = default;
        Vector2 goalJunction = default;
        int goalStair = 0;
        // Network cells already found to hold a junction (or not), so the road grid is read once per cell.
        var joins = state.Joins;
        Vector2? JoinAt(Vector2i cell)
        {
            if (!joins.TryGetValue(cell, out Vector2? at))
                joins[cell] = at = Join(cell, out Vector2 junction) ? junction : null;
            return at;
        }
        List<Vector2> TakeGoal()
        {
            LastPathCost = goalCost;
            cameFrom[goalKey.pos] = goalBefore;
            if (goalStair != 0) byStair[goalKey.pos] = goalStair; else byStair.Remove(goalKey.pos);
            byViaduct.Remove(goalKey.pos); // the goal is reached by a step or a crossing, never by a viaduct's jump
            return ReconstructPath(cameFrom, byStair, byViaduct, goalKey.pos, start, goalJunction);
        }

        m_crossingDestination = JoinAt;
        while (iterations < MaxIterations)
        {
            bool open = openSet.TrimClosed(closedSet);
            if (haveGoal && (!open || OpenHeap.Compare(goalKey, openSet.Min) <= 0))
                return TakeGoal();
            if (!open) break;
            // Every 256 iterations: a cancelled generation drains within milliseconds, without a
            // static read and token check on each of the millions of nodes a world searches.
            if ((iterations & 255) == 0) RoadLifecycleManager.ThrowIfCancellationRequested();
            iterations++;
            activity.Step();
            m_lastIterations = iterations;
            var current = openSet.Pop();
            Vector2i currentPos = current.pos;
            // The state's cell; on a viaduct's discounted stretch, the metres of it left.
            Vector2i currentCell = CellOf(currentPos);
            bool onApproach = OnApproach(currentPos);
            float approachLeft = onApproach ? ApproachLeft(stretchLeft, currentPos) : 0f;

            // The start itself at the network (no arrival to judge), or the plain rule without a junction step.
            if ((JunctionStep <= 0f || !cameFrom.ContainsKey(currentPos)) && JoinAt(currentCell) is Vector2 junction)
            {
                LastPathCost = gCosts.TryGetValue(currentPos, out float cost) ? cost : 0f;
                return ReconstructPath(cameFrom, byStair, byViaduct, currentPos, start, junction);
            }

            closedSet.Add(currentPos);
            // Neither changes while the cell's neighbours are relaxed: only other cells are written.
            float currentG = gCosts[currentPos];
            int arriving = byStair.TryGetValue(currentPos, out int stairHere) ? stairHere : 0;

            for (int i = 0; i < Directions.Length; i++)
            {
                Vector2i neighborPos = new Vector2i(currentCell.x + Directions[i].x, currentCell.y + Directions[i].y);
                // The state a plain step arrives in: on the discounted stretch while some of it is left past the step.
                Vector2i target = onApproach ? ApproachNext(approachLeft, neighborPos, i) : neighborPos;
                // A closed cell is still a junction for a level arrival from here.
                bool closed = closedSet.Contains(target);
                if (closed && (JunctionStep <= 0f || JoinAt(neighborPos) == null))
                    continue;

                float moveCost = GetMoveCost(currentCell, neighborPos, i, arriving, out int stair);
                if (!float.IsPositiveInfinity(moveCost) && moveCost < RiverPenalty)
                {
                    moveCost += TurnCost(cameFrom, currentPos, i);
                    if (onApproach) moveCost = ApproachAfterJump(approachLeft, i, moveCost);
                }
                if (moveCost >= RiverPenalty)
                {
                    if (!(Fords || Bridges))
                    {
                        continue;
                    }
                    // The start's cell, the path's first: left from the exact start (ReconstructPath).
                    if (!TryGetRiverCrossing(currentCell, cameFrom.ContainsKey(currentPos) ? null : start, Directions[i], out Vector2i landing, out float crossingCost))
                        continue;
                    closed = closedSet.Contains(landing);
                    if (closed && (JunctionStep <= 0f || JoinAt(landing) == null))
                    {
                        continue;
                    }
                    // Not discounted, and the end of any discounted stretch: it lands on the plain cell.
                    neighborPos = target = landing;
                    // The turn limit and price bind a crossing as they bind every other step (TurnCost, the one owner): a
                    // crossing turned back sharper than the limit on the step before it is no move.
                    moveCost = crossingCost + TurnCost(cameFrom, currentPos, i);
                    if (float.IsPositiveInfinity(moveCost)) continue;
                    stair = 0;
                }

                float tentativeG = currentG + moveCost;
                // Arriving level at a junction: the search may end there (but not back along its own path).
                if (JunctionStep > 0f && JoinAt(neighborPos) is Vector2 meets)
                {
                    float goalPriority = tentativeG + NetworkHeuristic(neighborPos);
                    if ((!haveGoal || goalPriority < goalKey.priority) && ArrivesLevel(meets, currentPos, state, cameFrom) && !OnPathTo(cameFrom, currentPos, neighborPos))
                    {
                        haveGoal = true;
                        goalKey = (goalPriority, neighborPos);
                        goalCost = tentativeG;
                        goalBefore = currentPos;
                        goalJunction = meets;
                        goalStair = stair;
                    }
                }
                if (closed) continue;
                if (!gCosts.TryGetValue(target, out float existingG) || tentativeG < existingG)
                {
                    cameFrom[target] = currentPos;
                    if (stair != 0) byStair[target] = stair; else byStair.Remove(target);
                    byViaduct.Remove(target);
                    if (OnApproach(target)) stretchLeft[target] = approachLeft - DirectionCosts[i] * CellSize;
                    gCosts[target] = tentativeG;
                    openSet.Push((tentativeG + Bound(target), target));
                }
            }

            for (int d = 0; Viaducts && Bridges && currentCell != startGrid && d < RoadRules.ViaductHeadings; d++)
            {
                if (!TryGetViaduct(currentCell, d, out Vector2i landing, out float jumpCost))
                    continue;
                // The far bank starts the discounted stretch after it.
                Vector2i landed = ViaductApproachReach > 0f ? ApproachKey(landing) : landing;
                if (closedSet.Contains(landed) || !ClearOfJumpsBefore(currentPos, landing, cameFrom, byViaduct))
                    continue;
                // A viaduct's jump pays the turn's price, not its limit (ViaductTurnPrice; Mistlands viaducts only).
                float turn = ViaductTurnPrice(cameFrom, currentPos, d);
                float tentativeG = currentG + turn + ApproachToJump(currentPos, landing, jumpCost, cameFrom, gCosts, byViaduct);
                if (!gCosts.TryGetValue(landed, out float existingG) || tentativeG < existingG)
                {
                    cameFrom[landed] = currentPos;
                    byStair.Remove(landed);
                    byViaduct.Add(landed);
                    if (OnApproach(landed)) stretchLeft[landed] = ViaductApproachReach;
                    gCosts[landed] = tentativeG;
                    openSet.Push((tentativeG + Bound(landed), landed));
                }
            }
        }

        // The budget spent with a level junction already found: the best one found, not none.
        if (haveGoal) return TakeGoal();
        LastPathCost = 0f;
        return null;
        }
        finally { m_crossingDestination = null; ReturnSearchState(state); }
    }

    /// <summary>The ground a road arriving at <paramref name="cell"/> stands on, the one every junction rule reads
    /// (RoadSpatialGrid.JunctionGround), not the search's raw height: near a biome border the two differ. Read once a
    /// cell per search, and only where a road is in reach: read for every cell, the world generator's height made in-game
    /// join searches twice as slow.</summary>
    private float JunctionGroundAt(Vector2i cell, SearchState state)
    {
        var grounds = state.Grounds;
        if (!grounds.TryGetValue(cell, out float h)) grounds[cell] = h = RoadSpatialGrid.JunctionGround(CellCentre(cell), m_worldGen);
        return h;
    }

    /// <summary>Whether a road stepping into a network cell from the cell <paramref name="before"/> arrives level with the
    /// road at <paramref name="junction"/>, the network point it joins there: the road's surface there within two junction
    /// steps of the ground it arrives from. The one owner of a join's arrival: the join search (FindPathToNetwork) ends
    /// only on such an arrival, and the road built to the junction it chose (FindPath, FindPathBetween given the network
    /// cell the search arrived through) arrives into that cell only on one, so the road built keeps the arrival its
    /// search judged. Judged per arrival, not per cell. An arrival with no room for the road's junction landing before the
    /// stretch it shares with the met road (<see cref="RoomToLand"/>) is not level either.</summary>
    private bool ArrivesLevel(Vector2 junction, Vector2i before, SearchState state, Dictionary<Vector2i, Vector2i> cameFrom) =>
        !RoadSpatialGrid.TryGetRoadHeightWithin(junction, RoadBeside.OnRoad, out float surface)
        || Mathf.Abs(surface - JunctionGroundAt(CellOf(before), state)) <= 2f * JunctionStep && MeetsWithinCap(junction)
            && RoomToLand(junction, before, cameFrom);

    [System.ThreadStatic] private static List<Vector2>? t_approach;

    /// <summary>Whether a road arriving at <paramref name="junction"/> from the cell <paramref name="before"/> (and the cells
    /// the search came by before it, <paramref name="cameFrom"/>) has room for its junction landing where it leaves the met
    /// road, as the planner lays it (RoadPitches.AcceptLanding): RoadSpatialGrid.RoomToLandBefore on its way in, the last
    /// cells back from the junction. A shallow merge onto a steep road has its landing beside that road, held up its climb,
    /// and is not offered; a square arrival that lands level is. Asked only once the arrival is otherwise level.</summary>
    private static bool RoomToLand(Vector2 junction, Vector2i before, Dictionary<Vector2i, Vector2i> cameFrom)
    {
        if (!(RoadPitches.JunctionLanding > 0f)) return true;
        var way = t_approach ??= new List<Vector2>();
        way.Clear();
        way.Add(junction);
        // The way in back from the junction, a few cells at a time until the verdict is known: a stretch along the met road
        // (the search may ride it), then the landing where it leaves it.
        float chunk = RoadPitches.JunctionLanding + RoadNetworkGenerator.RoadWidth + CellSize;
        Vector2i c = before;
        bool more = true;
        while (true)
        {
            float length = 0f;
            while (more && length < chunk)
            {
                Vector2 at = CellCentre(CellOf(c));
                length += Vector2.Distance(way[way.Count - 1], at);
                way.Add(at);
                if (cameFrom.TryGetValue(c, out Vector2i back)) c = back; else more = false;
            }
            bool room = RoadSpatialGrid.RoomToLandBefore(way, RoadNetworkGenerator.RoadWidth, out bool decided);
            if (decided || !more) return room;
        }
    }

    /// <summary>The junction-level reach: how far round a junction the network must be within the grade cap
    /// (<see cref="MeetsWithinCap"/>), measured along the met road. Half a junction landing (<see cref="RoadPitches.JunctionLanding"/>),
    /// 3 m today; widening it (to 10 m, say) is this one line.</summary>
    internal static float JunctionLevelReach => RoadPitches.JunctionLanding / 2f;

    /// <summary>Whether the network at <paramref name="junction"/> is within the grade cap (<see cref="RoadGrade.Configured"/>)
    /// across it: every two stored road points within half a junction landing (<see cref="RoadPitches.JunctionLanding"/>) of
    /// it and at least that far apart differ in height by no more than the cap over their distance, whichever way the met road
    /// bends. A junction on another road's pitch (a stretch steeper than the cap, between its landings) is not a level
    /// meeting: a road joining there meets a slope, with the met road falling away beside its landing. The one owner of a
    /// junction's grade: the join search's arrival (<see cref="ArrivesLevel"/>) and every road's meeting with the network
    /// (RoadNetworkGenerator.MeetingRefusal) ask it.</summary>
    internal static bool MeetsWithinCap(Vector2 junction)
    {
        float half = JunctionLevelReach;
        if (!(half > 0f)) return true;
        var near = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(junction.x, 0f, junction.y), half);
        for (int i = 0; i < near.Count; i++)
        {
            if (!near[i].IsRoad) continue;
            for (int j = i + 1; j < near.Count; j++)
            {
                if (!near[j].IsRoad) continue;
                float d = Vector2.Distance(near[i].p, near[j].p);
                if (d >= half && Mathf.Abs(near[i].h - near[j].h) > RoadGrade.Configured * d) return false;
            }
        }
        return true;
    }

    /// <summary>Whether the search's path to <paramref name="from"/> already passes through <paramref name="cell"/>:
    /// arriving at it again would be a loop.</summary>
    private static bool OnPathTo(Dictionary<Vector2i, Vector2i> cameFrom, Vector2i from, Vector2i cell)
    {
        for (Vector2i c = from; ; c = cameFrom[c])
        {
            if (RoadGridComparer.Instance.Equals(CellOf(c), cell)) return true;
            if (!cameFrom.ContainsKey(c)) return false;
        }
    }

    /// <summary>
    /// A search's open cells by (priority, x, y), smallest first: an array binary heap. A cell whose cost improves is
    /// pushed again and its older entry left where it is. That entry's priority is no lower than the new one's (the same
    /// key at most), so the cell is taken, and closed, before the old entry surfaces, and <see cref="TrimClosed"/> drops it
    /// then: the open cells come out in the order a sorted set holding one entry per cell gives, without a tree node
    /// allocated per push or a removal per improvement.
    /// </summary>
    internal sealed class OpenHeap
    {
        private (float priority, Vector2i pos)[] m_items = new (float, Vector2i)[1024];
        private int m_count;

        public void Clear() => m_count = 0;

        /// <summary>The order a cell is taken in: priority, then x, then y.</summary>
        public static int Compare((float priority, Vector2i pos) a, (float priority, Vector2i pos) b)
        {
            int cmp = a.priority.CompareTo(b.priority);
            if (cmp != 0) return cmp;
            cmp = a.pos.x.CompareTo(b.pos.x);
            if (cmp != 0) return cmp;
            return a.pos.y.CompareTo(b.pos.y);
        }

        public void Push((float priority, Vector2i pos) item)
        {
            if (m_count == m_items.Length) System.Array.Resize(ref m_items, m_count * 2);
            int i = m_count++;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (Compare(m_items[parent], item) <= 0) break;
                m_items[i] = m_items[parent];
                i = parent;
            }
            m_items[i] = item;
        }

        /// <summary>The smallest entry; call <see cref="TrimClosed"/> first.</summary>
        public (float priority, Vector2i pos) Min => m_items[0];

        public (float priority, Vector2i pos) Pop()
        {
            var top = m_items[0];
            var last = m_items[--m_count];
            int i = 0;
            while (true)
            {
                int child = 2 * i + 1;
                if (child >= m_count) break;
                if (child + 1 < m_count && Compare(m_items[child + 1], m_items[child]) < 0) child++;
                if (Compare(last, m_items[child]) <= 0) break;
                m_items[i] = m_items[child];
                i = child;
            }
            if (m_count > 0) m_items[i] = last;
            return top;
        }

        /// <summary>Drops the entries on top whose cell is closed (left behind by an improvement); false when no
        /// open cell is left.</summary>
        public bool TrimClosed(HashSet<Vector2i> closed)
        {
            while (m_count > 0 && closed.Contains(m_items[0].pos)) Pop();
            return m_count > 0;
        }
    }

    /// <summary>A search's working collections, kept on the pathfinder between searches and cleared for each, so each
    /// search does not grow them again from empty. Taken, not shared: a search started while another runs gets its own.</summary>
    private sealed class SearchState
    {
        public readonly OpenHeap Open = new();
        public readonly Dictionary<Vector2i, float> GCosts = new(RoadGridComparer.Instance);
        public readonly Dictionary<Vector2i, Vector2i> CameFrom = new(RoadGridComparer.Instance);
        public readonly HashSet<Vector2i> Closed = new(RoadGridComparer.Instance);
        public readonly Dictionary<Vector2i, int> ByStair = new(RoadGridComparer.Instance);
        public readonly HashSet<Vector2i> ByViaduct = new(RoadGridComparer.Instance);
        public readonly Dictionary<Vector2i, float> Grounds = new(RoadGridComparer.Instance);
        public readonly Dictionary<Vector2i, Vector2?> Joins = new(RoadGridComparer.Instance);
        public readonly Dictionary<Vector2i, float> Heuristics = new(RoadGridComparer.Instance);
        /// <summary>The metres of the discounted stretch after a viaduct's far bank left at each state on it, as its cheapest
        /// arrival brought it (<see cref="ApproachLeft"/>).</summary>
        public readonly Dictionary<Vector2i, float> StretchLeft = new(RoadGridComparer.Instance);

        public void Clear()
        {
            Open.Clear(); GCosts.Clear(); CameFrom.Clear(); Closed.Clear(); ByStair.Clear(); ByViaduct.Clear();
            Grounds.Clear(); Joins.Clear(); Heuristics.Clear(); StretchLeft.Clear();
        }
    }

    private SearchState? m_spareState;

    private SearchState RentSearchState()
    {
        SearchState state = System.Threading.Interlocked.Exchange(ref m_spareState, null) ?? new SearchState();
        state.Clear();
        m_fromRoadKnown = m_fromClearKnown = false;
        return state;
    }

    private void ReturnSearchState(SearchState state) => System.Threading.Volatile.Write(ref m_spareState, state);

    private Vector2i WorldToGrid(Vector2 world)
    {
        return new Vector2i(Mathf.RoundToInt(world.x / CellSize), Mathf.RoundToInt(world.y / CellSize));
    }

    /// <summary>The world position of a search cell: the one place cell indices become metres (the shared per-cell
    /// height cache is valid only while every reader agrees). The one owner of Zone, grid and cell arithmetic (docs/OWNERS.md).</summary>
    internal static Vector2 CellCentre(Vector2i grid) => new(grid.x * CellSize, grid.y * CellSize);

    private float Heuristic(Vector2i from, Vector2i to)
    {
        float dx = (to.x - from.x) * CellSize;
        float dy = (to.y - from.y) * CellSize;
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// The sharpest single-step turn the search may make, in degrees against
    /// the step it arrived by; 0 is no limit. Without it the per-step price
    /// lets a weave of gentle steps with near-reversing turns undercut one
    /// steep step; a real switchback turns its 180 degrees over several steps.
    /// </summary>
    public float TurnLimitDegrees = DefaultTurnLimit;
    /// <summary>Settable for tests; 0 is no limit.</summary>
    internal static float DefaultTurnLimit = 100f;

    /// <summary>
    /// Cells the road being built could not cross without cutting or filling
    /// past the game's terrain limit (see RoadNetworkGenerator.EarthworkCap):
    /// stepping into one costs <see cref="EarthworkPenalty"/>, so the next
    /// search goes round or takes another crossing. Cleared for every road.
    /// </summary>
    private readonly HashSet<Vector2i> m_earthworkPenalty = new(RoadGridComparer.Instance);
    public static float EarthworkPenalty = 1500f;
    internal void ClearEarthworkPenalty() => m_earthworkPenalty.Clear();
    internal void PenalizeAround(Vector2 world, float radius)
    {
        int r = Mathf.CeilToInt(radius / CellSize);
        var centre = WorldToGrid(world);
        for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
            {
                var cell = new Vector2i(centre.x + dx, centre.y + dz);
                if (Vector2.Distance(CellCentre(cell), world) <= radius + CellSize * 0.5f) m_earthworkPenalty.Add(cell);
            }
    }
    internal int EarthworkPenaltyCells => m_earthworkPenalty.Count;
    internal int PenalizedWaypoints(List<Vector2> path) { int n = 0; foreach (var p in path) if (m_earthworkPenalty.Contains(WorldToGrid(p))) n++; return n; }

    /// <summary>
    /// A smooth low-frequency cost field adds up to this share of the base
    /// step price, so long runs bend instead of running dead straight. It
    /// fades out with the step's slope, gone by <see cref="MeanderFlat"/>, so
    /// climbs keep a slow wander while the steepest ground (switchbacks,
    /// pitches past the sustained grade, staircases) is priced without it.
    /// It only adds, so the heuristic's BaseCost-per-metre bound still holds.
    /// 0 is off.
    /// </summary>
    public float Meander = DefaultMeander;
    /// <summary>Settable for tests; 0 is no meander.</summary>
    internal static float DefaultMeander = 3f;
    /// <summary>Slope at which the meander field has faded out: the top of the 20-28 % climbs,
    /// below <see cref="RoadPitches.Sustained"/>. </summary>
    public static readonly float MeanderFlat = 0.28f;
    internal float MeanderCost(Vector2 from, Vector2 to, Vector2i toCell, float slope, float dist)
    {
        if (slope >= MeanderFlat || Meander <= 0f) return 0f;
        float fade = 1f - slope / MeanderFlat;
        var mid = (from + to) * 0.5f;
        return BaseCost * dist * Meander * fade * RoadRules.For(m_terrain.Biome(toCell)).Meander * MeanderField(mid.x, mid.y);
    }


    // Octaves of the meander field: wavelength (m) and weight. The lengths are
    // not multiples of each other and each octave is turned, so no period or
    // grid direction shows along a long road: a repeating period is noticed.
    private static readonly (float wave, float weight, float cos, float sin, float ox, float oz)[] MeanderOctaves =
    {
        (171f, 0.55f, 0.906f, 0.423f, 1013.7f, -2027.3f),
        (67f, 0.30f, -0.342f, 0.940f, -331.1f, 877.9f),
        (29f, 0.15f, 0.643f, -0.766f, 4561.3f, 12.7f),
    };

    /// <summary>The meander field at a world position, in [0,1].</summary>
    internal static float MeanderField(float worldX, float worldZ)
    {
        float sum = 0f;
        for (int i = 0; i < MeanderOctaves.Length; i++)
        {
            var o = MeanderOctaves[i];
            float weight = o.weight;
            float u = (o.cos * worldX - o.sin * worldZ + o.ox) / o.wave;
            float v = (o.sin * worldX + o.cos * worldZ + o.oz) / o.wave;
            sum += weight * ValueNoise(u, v);
        }
        return sum;
    }

    /// <summary>Smooth value noise in [0,1]: hashed lattice values, smoothstep
    /// between them. Managed and stateless, so the parallel island workers can
    /// share it (Unity's Perlin is a native call).</summary>
    internal static float ValueNoise(float x, float y)
    {
        int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
        float fx = x - x0, fy = y - y0;
        fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy);
        float a = Lattice(x0, y0), b = Lattice(x0 + 1, y0), c = Lattice(x0, y0 + 1), d = Lattice(x0 + 1, y0 + 1);
        return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
    }

    private static float Lattice(int x, int y) => (HashCell(x, y) & 0xFFFFFF) / (float)0xFFFFFF;

    /// <summary>The one integer hash of a cell: the lattice values of <see cref="ValueNoise"/>, and a ford's style
    /// (RoadCrossing.SiteHash). The one owner of seeded hashing of cells (docs/OWNERS.md).</summary>
    internal static uint HashCell(int x, int y)
    {
        unchecked
        {
            uint h = (uint)x * 374761393u + (uint)y * 668265263u;
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }

    /// <summary>Price per radian turned between consecutive steps (Galin et
    /// al. 2010 price curvature for the same reason).</summary>
    public float TurnWeight = DefaultTurnWeight;
    /// <summary>Settable for tests; 0 is no turn price.</summary>
    internal static float DefaultTurnWeight = 4f;

    /// <summary>
    /// What turning into step <paramref name="direction"/> costs, given the
    /// step the search arrived at <paramref name="at"/> by. The search state is
    /// the cell alone, so this reads the arriving step from cameFrom: a cost
    /// that depends on history, which makes A* no longer exact (a cell reached
    /// by a worse-heading route first may keep it). Measured, not assumed.
    /// </summary>
    private float TurnCost(Dictionary<Vector2i, Vector2i> cameFrom, Vector2i at, int direction)
    {
        if (TurnLimitDegrees <= 0f && TurnWeight <= 0f) return 0f;
        float angle = TurnAngle(cameFrom, at, direction);
        if (TurnLimitDegrees > 0f && angle * Mathf.Rad2Deg > TurnLimitDegrees + 0.01f) return Impassable;
        return TurnWeight * angle;
    }

    /// <summary>
    /// Mistlands viaducts only: what turning onto a viaduct's jump in direction <paramref name="direction"/> costs. Held to the
    /// turn limit only where <see cref="RoadRules.ViaductTurnLimit"/> says so (it does not, and the why is there); otherwise
    /// the turn's price alone (<see cref="TurnWeight"/> a radian). Every other move keeps the limit (<see cref="TurnCost"/>).
    /// </summary>
    private float ViaductTurnPrice(Dictionary<Vector2i, Vector2i> cameFrom, Vector2i at, int direction) =>
        RoadRules.ViaductTurnLimit ? TurnCost(cameFrom, at, direction)
            : TurnWeight <= 0f ? 0f : TurnWeight * TurnAngle(cameFrom, at, direction);

    /// <summary>The angle, in radians, between the step the search arrived at <paramref name="at"/> by and step
    /// <paramref name="direction"/>; 0 where it arrived by none.</summary>
    private float TurnAngle(Dictionary<Vector2i, Vector2i> cameFrom, Vector2i at, int direction)
    {
        if (!cameFrom.TryGetValue(at, out Vector2i from)) return 0f;
        Vector2i atCell = CellOf(at), fromCell = CellOf(from);
        Vector2 incoming = new Vector2(atCell.x - fromCell.x, atCell.y - fromCell.y);
        if (incoming.sqrMagnitude < 1e-6f) return 0f;
        Vector2 outgoing = new Vector2(Directions[direction].x, Directions[direction].y);
        float cos = Mathf.Clamp((incoming.x * outgoing.x + incoming.y * outgoing.y) /
                                (Mathf.Sqrt(incoming.sqrMagnitude) * Mathf.Sqrt(outgoing.sqrMagnitude)), -1f, 1f);
        return (float)System.Math.Acos(cos);
    }

    /// <summary>
    /// The narrowest gap, in metres, left between a location's footprint and the
    /// water for a road to pass through. A road forced along a strip of shore
    /// beside a location comes out strange: squeezed against the footprint on one
    /// side and the water on the other, tilted down the bank or run out on a
    /// causeway. Such ground is not a road at all, for any location, the road's
    /// own destination included; the search goes round, or the location goes
    /// unserved. 12 m is the location clearance (4 m to the road's centre), half a
    /// 4 m road and 6 m of bank. Settable for tests; 0 is off.
    /// </summary>
    internal static float SqueezeCorridor = 12f;

    /// <summary>Whether <paramref name="p"/> lies between a location's footprint and water less than
    /// <see cref="SqueezeCorridor"/> apart, water the road can build out not counted
    /// (<see cref="RoadShoreFill"/>).</summary>
    internal bool LocationShoreSqueeze(Vector2 p) => RoadShoreFill.Squeezed(m_worldGen, p, buildable: true, out _, Fords);

    /// <summary>Whether a crossing's line runs over water within <see cref="SqueezeCorridor"/> of a
    /// location's footprint (sampled every 4 m, 8 m clear of each bank).</summary>
    internal bool DeckSqueezed(Vector2 from, Vector2 to, out Vector2 at)
    {
        at = default;
        if (SqueezeCorridor <= 0f) return false;
        float length = Vector2.Distance(from, to);
        int n = Mathf.Max(1, Mathf.CeilToInt(length / 4f));
        for (int i = 1; i < n; i++)
        {
            float along = length * i / n;
            // The banks are the landing test's; the deck test is for the water it runs over.
            if (along < 8f || length - along < 8f) continue;
            Vector2 p = Vector2.Lerp(from, to, (float)i / n);
            if (!Wet(p)) continue;
            float gap = RoadLocationProtection.EdgeDistance(p, SqueezeCorridor);
            if (gap >= 0f && gap < SqueezeCorridor) { at = p; return true; }
        }
        return false;
    }

    /// <summary>The squeeze, once per cell for this pathfinder's life: the
    /// locations and the ground do not change during a generation, and the test
    /// samples the world sixteen times.</summary>
    private readonly Dictionary<Vector2i, bool> m_squeezed = new(RoadGridComparer.Instance);
    private bool SqueezedCell(Vector2i cell)
    {
        if (SqueezeCorridor <= 0f) return false;
        // Asked of most steps the search takes: the terrain memo's slot for the cell, which the step read already, before
        // the dictionary.
        if (m_terrain.KnowsSqueeze(cell, out bool squeezed)) return squeezed;
        if (!m_squeezed.TryGetValue(cell, out squeezed))
        {
            Vector2 p = CellCentre(cell);
            m_squeezed[cell] = squeezed = LocationShoreSqueeze(p);
            // Let through only because the shallows beside it are built out: counted once per cell.
            if (!squeezed && RoadShoreFill.FillDepth > 0f && RoadShoreFill.Squeezed(m_worldGen, p, buildable: false, out _, Fords))
                RecordShoreFill(p);
        }
        m_terrain.NoteSqueeze(cell, squeezed);
        return squeezed;
    }

    /// <summary>Where the squeeze let the search through on shallows a road builds out, by the location beside
    /// them: (centre x, centre z) -> cells. Folded once per search, as <see cref="SqueezeHits"/> is.</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<(int x, int z), int> ShoreFillCells = new();
    private readonly Dictionary<(int x, int z), int> m_shoreFills = new();
    private void RecordShoreFill(Vector2 p)
    {
        if (!RoadLocationProtection.NearestFootprint(p, SqueezeCorridor, out var f)) return;
        var key = (Mathf.RoundToInt(f.Centre.x), Mathf.RoundToInt(f.Centre.y));
        m_shoreFills.TryGetValue(key, out int n);
        m_shoreFills[key] = n + 1;
    }

    /// <summary>Where the squeeze refused something this generation, by the
    /// location it squeezed against: (centre x, centre z) -> steps, landings.</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<(int x, int z), (int steps, int landings)> SqueezeHits = new();
    /// <summary>This search's squeezes, folded into <see cref="SqueezeHits"/> once when it ends:
    /// recorded there per step, every worker took a lock (and allocated) on each step into a squeezed cell.</summary>
    private readonly Dictionary<(int x, int z), (int steps, int landings)> m_squeezeHits = new();
    private void RecordSqueeze(Vector2 p, bool landing)
    {
        if (!RoadLocationProtection.NearestFootprint(p, SqueezeCorridor, out var f)) return;
        var key = (Mathf.RoundToInt(f.Centre.x), Mathf.RoundToInt(f.Centre.y));
        m_squeezeHits.TryGetValue(key, out var n);
        m_squeezeHits[key] = landing ? (n.steps, n.landings + 1) : (n.steps + 1, n.landings);
    }
    private void FoldSqueezes()
    {
        foreach (var fill in m_shoreFills) ShoreFillCells.AddOrUpdate(fill.Key, fill.Value, (_, n) => n + fill.Value);
        m_shoreFills.Clear();
        if (m_squeezeHits.Count == 0) return;
        foreach (var hit in m_squeezeHits)
            SqueezeHits.AddOrUpdate(hit.Key, hit.Value, (_, n) => (n.steps + hit.Value.steps, n.landings + hit.Value.landings));
        m_squeezeHits.Clear();
    }

    /// <summary>Water the search would not walk (<see cref="RoadWater.Walked"/>).</summary>
    private bool Wet(Vector2 p) => !RoadWater.Walked(m_worldGen, p, Fords);

    /// <summary>The road's price for a step, or the staircase's where that is
    /// cheaper (<paramref name="stair"/> is then +1 for a step up, -1 down);
    /// <paramref name="arriving"/>: the way the staircase the search arrived at
    /// <paramref name="from"/> on was going, or 0.</summary>
    private float GetMoveCost(Vector2i from, Vector2i to, int directionIndex, int arriving, out int stair)
    {
        stair = 0;
        // A banned crossing walked cell by cell (a ford over shallow water) is banned as its jump is.
        if (m_noJumps.Count > 0 && Banned(CellCentre(from), CellCentre(to))) return float.PositiveInfinity;
        float road = RoadMoveCost(from, to, directionIndex, arriving, out float stairCost, out int climb, out bool tooSteep);
        // A staircase only where the road cannot climb: where it can, it
        // traverses and switches back as it always did (where it is steep we
        // build stairs, where it is gentle we don't, so the switchbacks stay). Offered wherever it was
        // cheaper, stairs took 1197 stretches of one world's roads.
        // Once on a staircase it may carry on over any ground steeper than
        // StairMinGrade: held to ground too steep for road all the way, a climb
        // broke into short staircases a cell of road apart, which piled into
        // each other on switchback legs (RoadSeedA kept 36 of them),
        // and the long runs were lost.
        stair = (tooSteep || arriving != 0 || OpenToStairsAt(from, to)) && stairCost < road ? climb : 0;
        return stair != 0 ? stairCost : road;
    }

    private float RoadMoveCost(Vector2i from, Vector2i to, int directionIndex, int arriving, out float stairCost, out int climb, out bool tooSteep)
    {
        climb = 0;
        tooSteep = false;
        stairCost = float.PositiveInfinity;
        Vector2 fromWorld = CellCentre(from);
        Vector2 toWorld = CellCentre(to);
        // Another location's footprint, or a zone the network leaves alone (or a manual road's players' zones): round it, not through.
        if (NetworkGround.BlocksRoute(fromWorld, toWorld, LocationClearance, m_locations, m_searchStart, m_searchEnd, FromClearOfLocations(from, fromWorld)))
            return Impassable;

        float dist = DirectionCosts[directionIndex] * CellSize;
        float h1 = m_terrain.Height(from);
        float h2 = m_terrain.Height(to);
        float slope = Mathf.Abs(h2 - h1) / dist;

        float riverWeight = m_terrain.River(to);
        // Standing water reaches the same crossing step as river water, including swamp cells otherwise walked.
        // The detector decides whether the whole run is a crossing or a puddle left at the dry floor.
        if ((Fords || Bridges) && RoadCrossingDetector.Shallows && !RoadWater.River(riverWeight)
            && (RoadWater.Wet(h2) || dist >= RoadCrossingDetector.ShallowMinLength
                && StepInteriorWater(from, directionIndex))) return RiverPenalty;
        // The one water test (RoadWater), on the ground as it will be; with fords on, swamp shallows are waded at a price.
        float wadeCost = 0f;
        switch (RoadWater.Of(m_terrain.Ground(to), riverWeight, m_worldGen, toWorld, Fords))
        {
            case RoadWater.Kind.River: return RiverPenalty;
            case RoadWater.Kind.Deep: return WaterPenalty * 2f;
            case RoadWater.Kind.Shallow: return WaterPenalty;
            case RoadWater.Kind.Waded: wadeCost = StandingWaterPenalty; break;
        }

        if (SqueezedCell(to))
        {
            RecordSqueeze(CellCentre(to), landing: false);
            return Impassable;
        }

        // Steep dry ground may be climbed on a staircase. Water, rivers, locations
        // and squeezes were refused above, for stairs as for road. Stairs go
        // down as well as up where it makes sense for path finding, and over a crest the climb
        // runs on into the descent at no new start price; the generator lays
        // the two as two staircases. A staircase an earlier road laid here is
        // walked on at the reuse share, like road.
        climb = h2 >= h1 ? 1 : -1;
        if (Stairs && wadeCost == 0f && riverWeight <= 0f && slope >= StairMinGrade && !StairStepTooSteep(slope)
            && (m_noStairs.Count == 0 || !(m_noStairs.Contains(from) || m_noStairs.Contains(to))))
        {
            bool laid = StairStructures.OnLaidStair(from.x, from.y) && StairStructures.OnLaidStair(to.x, to.y);
            float biomePrice = RoadRules.For(m_terrain.Biome(to)).StairPrice;
            bool open = OpenToStairsAt(from, to);
            stairCost = BaseCost * dist + biomePrice * (StairCostPerMeterClimbed * SteepStairFactor(slope) * Mathf.Abs(h2 - h1)
                + (arriving != 0 || laid || open ? 0f : StairEntryCost(from)));
            if (laid) stairCost *= ReuseFactor;
            if (open) stairCost *= OpenStairPrice;
            // Priced up for earthwork, a cell opened to stairs prices only its road: there the staircase is the way.
            if (m_earthworkPenalty.Count > 0 && m_earthworkPenalty.Contains(to) && !open) stairCost += EarthworkPenalty;
        }

        // A viaduct laid this generation stands over this cell: no road under its deck or through
        // its piers; a road uses it by its own jump, bank to bank.
        if (DvergrViaduct.Standing(to.x, to.y))
            return Impassable;

        // A laid staircase stands here (StaircaseAt): no road through it, only up or down it where a road
        // climbed it, from a laid stair cell. Roads built after one ran under and across it, and their
        // earthworks undermined its columns (broken stairs, overlapping roads).
        StaircaseCell staircase = StaircaseAt(to.x, to.y);
        if (staircase != StaircaseCell.None)
        {
            bool walking = staircase == StaircaseCell.Climbed && StairStructures.OnLaidStair(from.x, from.y);
            if (!walking) stairCost = float.PositiveInfinity;
            tooSteep = walking;
            return Impassable;
        }

        // A grade cap is a refusal, not a price: above it there is no road, so
        // the search crosses the slope or gives the destination up. The slope
        // terms below still price everything under the cap.
        // The margin is the search's, not the road's: RoadGrade.Limit still
        // holds the finished profile to MaxGrade, so a route admitted here can
        // still be refused when its profile is built. A cap switched off
        // (MaxGrade <= 0) stays off - the margin never becomes a cap.
        if (MaxGrade > 0f && slope > MaxGrade + RoadConstants.SearchGradeMargin)
        {
            tooSteep = true;
            return Impassable;
        }

        if (slope > SteepSlopeThreshold)
        {
            tooSteep = true;
            return SteepSlopePenalty;
        }

        if (TerrainVariancePenalty > 0f && m_terrain.Variance(to) > TerrainVarianceThreshold)
            return TerrainVariancePenalty;

        // Steep ground refused at the water's price where a biome says so (RoadRules: the Mountains, over 0.4).
        if (slope > RoadRules.For(m_terrain.Biome(to)).SteepGround)
        {
            tooSteep = true;
            return WaterPenalty;
        }

        float riverCost = riverWeight > 0 ? WaterPenalty * riverWeight : 0f;
        bool reuse = ReuseFactor < 1f && OnExistingRoad(toWorld) && FromOnExistingRoad(from, fromWorld);
        float cost = BaseCost * dist + (slope * slope * SlopeMultiplier) + riverCost + wadeCost
             + RoadPitches.OverSustainedPrice(slope, dist)
             + (reuse ? 0f : MeanderCost(fromWorld, toWorld, to, slope, dist));
        if (reuse) cost *= ReuseFactor;
        return cost + (m_earthworkPenalty.Count > 0 && m_earthworkPenalty.Contains(to) ? EarthworkPenalty : 0f);
    }

    /// <summary>What starting a staircase at a cell costs: <see cref="StairCostFixed"/>,
    /// varied by the cell so some sites take stairs and their neighbours do not.</summary>
    private float StairEntryCost(Vector2i cell)
    {
        unchecked
        {
            uint h = (uint)cell.x * 2246822519u ^ (uint)cell.y * 3266489917u ^ (uint)m_worldGen.GetSeed() * 668265263u;
            h = (h ^ (h >> 15)) * 2654435761u;
            h ^= h >> 13;
            float u = (h & 0xFFFF) / 65535f;
            return StairCostFixed * (1f + StairCostWobble * (2f * u - 1f));
        }
    }

    /// <summary>What the terrain says of a viaduct from a cell in each direction: where it lands and
    /// its price, or null. Terrain only, so it holds for this pathfinder's life. One row a cell, each direction worked out
    /// as it is first asked: the search asks all of a cell's directions in a row as it expands it, so the row is looked up
    /// once for them (<see cref="m_viaductRow"/>), not once a direction.</summary>
    private readonly Dictionary<Vector2i, ViaductRow> m_viaducts = new(RoadGridComparer.Instance);

    private sealed class ViaductRow
    {
        /// <summary>The directions worked out, one bit each.</summary>
        public int Known;
        public readonly (Vector2i landing, float cost, Vector2 from, Vector2 to)?[] Found = new (Vector2i landing, float cost, Vector2 from, Vector2 to)?[RoadRules.ViaductHeadings];
    }

    /// <summary>The row of the cell last asked (<see cref="m_viaductCell"/>), or null.</summary>
    private ViaductRow? m_viaductRow;
    private Vector2i m_viaductCell;

    /// <summary>
    /// A viaduct from <paramref name="from"/> straight on in principal direction
    /// <paramref name="d"/> across a Mistlands valley to the first cell back at the rim's height
    /// (within <see cref="RoadConstants.MaxBridgeBankDelta"/>), where
    /// <see cref="DvergrViaduct.ValleyCrossing"/> builds one: no water or river under it, a deck
    /// 6 m or more over the valley's floor, piers under the ceiling. Not over a location, another
    /// road or a staircase: roads do not cross. A viaduct both of whose banks are road already
    /// is shared, at the bridges' discount.
    /// </summary>
    private bool TryGetViaduct(Vector2i from, int d, out Vector2i landing, out float cost)
    {
        landing = from;
        cost = 0f;
        if (m_viaductRow == null || m_viaductCell != from)
        {
            if (!m_viaducts.TryGetValue(from, out m_viaductRow)) m_viaducts[from] = m_viaductRow = new ViaductRow();
            m_viaductCell = from;
        }
        ViaductRow row = m_viaductRow;
        if ((row.Known & (1 << d)) == 0)
        {
            row.Found[d] = ViaductOverTerrain(from, Directions[d]);
            row.Known |= 1 << d;
        }
        (Vector2i landing, float cost, Vector2 from, Vector2 to)? found = row.Found[d];
        if (!found.HasValue) return false;
        (Vector2i to, float price, Vector2 a, Vector2 b) = found.Value;
        if (!ViaductLineAllowed(a, b, from, to, d, Ground)) return false;
        landing = to;
        cost = price;
        if (OnExistingRoad(a) && OnExistingRoad(b)) cost *= RoadConstants.SharedCrossingCostFraction;
        return true;
    }

    /// <summary>
    /// Whether a valley viaduct may stand on the deck line from <paramref name="a"/> to <paramref name="b"/>, a jump from cell
    /// <paramref name="from"/> to <paramref name="to"/> on search heading <paramref name="d"/> (an index into
    /// <see cref="Directions"/>), judged on <paramref name="ground"/>: the one test of a valley viaduct's line, asked of every
    /// valley jump the search buys (<see cref="TryGetViaduct"/>) and of a bought viaduct's banks moved along its line
    /// (<see cref="MovedViaductAllowed"/>: StructureEnds.MeetEnds over DvergrViaduct.MovedBanks), so a moved line obeys every
    /// rule a bought one does: on a heading a valley viaduct is offered on (<see cref="RoadRules.ViaductHeadings"/>); not a
    /// crossing the road being built may not take (<see cref="Banned"/>, <see cref="BannedViaduct"/>); not on a knight's
    /// heading at a site kept to the grid's (<see cref="GridOnlyAt"/>); and clear of the network and the locations
    /// (<see cref="ViaductLineClear"/>).
    /// </summary>
    internal bool ViaductLineAllowed(Vector2 a, Vector2 b, Vector2i from, Vector2i to, int d, SearchGround ground)
    {
        if (d < 0 || d >= RoadRules.ViaductHeadings) return false;
        if (m_noJumps.Count > 0 && Banned(a, b)) return false;
        if (m_noViaducts.Count > 0 && BannedViaduct(a, b)) return false;
        if (m_gridOnly.Count > 0 && KnightJump(Directions[d].x, Directions[d].y) && GridOnlyAt(a, b)) return false;
        return ViaductLineClear(a, b, from, to, ground);
    }

    /// <summary><see cref="ViaductLineAllowed(Vector2, Vector2, Vector2i, Vector2i, int, SearchGround)"/> of a line on the jump
    /// whose bought banks were <paramref name="boughtFrom"/> and <paramref name="boughtTo"/>: the jump's cells are the cells they
    /// stand in, its heading theirs.</summary>
    internal bool ViaductLineAllowed(Vector2 a, Vector2 b, Vector2 boughtFrom, Vector2 boughtTo, SearchGround ground)
    {
        Vector2i from = WorldToGrid(boughtFrom), to = WorldToGrid(boughtTo);
        int d = System.Array.IndexOf(Directions, JumpDirection(to.x - from.x, to.y - from.y));
        return ViaductLineAllowed(a, b, from, to, d, ground);
    }

    /// <summary>
    /// Whether a viaduct this road's search bought (<paramref name="bought"/>, one of <see cref="LastViaductJumps"/>, either way
    /// round within DvergrViaduct.JumpMatch) may stand on its banks moved along its line (<paramref name="moved"/>,
    /// DvergrViaduct.MovedBanks): <see cref="ViaductLineAllowed(Vector2, Vector2, Vector2, Vector2, SearchGround)"/> of the moved
    /// line on the bought jump, on the ground that jump was bought on (<see cref="LastViaductGrounds"/>), not the last search's.
    /// A viaduct no jump of this road's searches bought is not moved. The generator's test of every move
    /// (RoadNetworkGenerator.MeetEnds), for generation, its trials and manual roads alike.
    /// </summary>
    internal bool MovedViaductAllowed(RoadCrossing bought, RoadCrossing moved)
    {
        int k = LastViaductJumps.FindIndex(j => DvergrViaduct.SameJump(bought.FromBank, bought.ToBank, j.from, j.to));
        if (k < 0 || k >= LastViaductGrounds.Count) return false;
        return ViaductLineAllowed(moved.FromBank, moved.ToBank, bought.FromBank, bought.ToBank, LastViaductGrounds[k]);
    }

    /// <summary>What <see cref="MovedViaductAllowed"/> reads of this pathfinder beside the network, as it stands: the jumps and the
    /// ground each was bought on, the road's crossing bans and grid-heading sites, and the location clearance. The remembered
    /// structure ends key it (StructureEnds.MeetEndsRemembered): a move is answered again only where it would be judged the same.</summary>
    internal object MoveAsked() => new ViaductMoveAsked(this);

    private sealed class ViaductMoveAsked : System.IEquatable<ViaductMoveAsked>
    {
        private readonly float m_clearance;
        private readonly (Vector2 from, Vector2 to)[] m_jumps, m_noJumps, m_noViaducts;
        private readonly SearchGround[] m_grounds;
        private readonly (Vector2 at, float radius)[] m_gridOnly;

        public ViaductMoveAsked(RoadPathfinder f)
        {
            m_clearance = f.LocationClearance;
            m_jumps = f.LastViaductJumps.ToArray();
            m_grounds = f.LastViaductGrounds.ToArray();
            m_noJumps = f.m_noJumps.ToArray();
            m_noViaducts = f.m_noViaducts.ToArray();
            m_gridOnly = f.m_gridOnly.ToArray();
        }

        public bool Equals(ViaductMoveAsked? o) =>
            o != null && m_clearance.Equals(o.m_clearance)
            && m_jumps.SequenceEqual(o.m_jumps) && m_grounds.SequenceEqual(o.m_grounds) && m_noJumps.SequenceEqual(o.m_noJumps)
            && m_noViaducts.SequenceEqual(o.m_noViaducts) && m_gridOnly.SequenceEqual(o.m_gridOnly);

        public override bool Equals(object? obj) => obj is ViaductMoveAsked o && Equals(o);
        public override int GetHashCode() => (m_jumps.Length, m_noJumps.Length, m_noViaducts.Length, m_gridOnly.Length).GetHashCode();
    }

    /// <summary>
    /// Whether a valley viaduct's deck line from <paramref name="a"/> to <paramref name="b"/>, a jump from cell
    /// <paramref name="from"/> to <paramref name="to"/>, stands clear of the network and the locations: clear of the viaducts
    /// other roads laid (DvergrViaduct.TooClose, the one owner: never back to back, nor in one's deck or approach; a shared
    /// viaduct, both banks the same, is the same structure), off every location's clearance on <paramref name="ground"/>
    /// (NetworkGround.BlocksRoute), and nothing of the network under the deck: another road, a staircase or another viaduct.
    /// Part of <see cref="ViaductLineAllowed(Vector2, Vector2, Vector2i, Vector2i, int, SearchGround)"/>.
    /// </summary>
    private bool ViaductLineClear(Vector2 a, Vector2 b, Vector2i from, Vector2i to, SearchGround ground)
    {
        if (DvergrViaduct.IsTooClose(a, b)) return false;
        if (NetworkGround.BlocksRoute(a, b, LocationClearance, ground.Locations, ground.Start, ground.End)) return false;
        float length = Vector2.Distance(a, b);
        for (float s = CellSize * 0.5f; s <= length - CellSize * 0.5f; s += 2f)
        {
            Vector2 p = Vector2.Lerp(a, b, s / length);
            if (OnExistingRoad(p)) return false;
            Vector2i cell = WorldToGrid(p);
            if (cell == from || cell == to || cell == WorldToGrid(a) || cell == WorldToGrid(b)) continue;
            if (StairStructures.Standing(cell.x, cell.y) || DvergrViaduct.Standing(cell.x, cell.y)) return false;
        }
        return true;
    }

    // A search's states (the keys of its costs, its came-from links and its closed set): a cell, or the same cell on the
    // discounted stretch after a viaduct's far bank. The stretch is a state of its own because what the steps on from a cell
    // cost depends on it: an arrival costing a little more with 40 m of the discount left is not lost to a cheaper plain one
    // (A* keeps the cheapest of each). The metres of the stretch left are its label's (StretchLeft), measured along the
    // path's own steps from the bank, as its cheapest arrival brought them, as a cell's turn cost is its cheapest arrival's.
    // One stretch state a cell, not one for each mix of steps that reaches it: kept apart by the steps taken (axis, diagonal
    // and knight counted), every order of them was a state at each cell within the reach of each far bank, many times the
    // cells, and searches across Mistlands valleys ran out of their budget before their roads were found. Such a state is
    // its cell's key with 1 in x's high bits; a cell's own coordinates stay well inside the low bits (a world is a few
    // thousand cells across).
    private const int ApproachShift = 16, ApproachHalf = 1 << (ApproachShift - 1);

    private static int High(int v) => (v + ApproachHalf) >> ApproachShift;

    /// <summary>The cell a search state stands on.</summary>
    internal static Vector2i CellOf(Vector2i key) =>
        new(key.x - (High(key.x) << ApproachShift), key.y - (High(key.y) << ApproachShift));

    /// <summary>Whether a search state is on the discounted stretch after a viaduct's far bank.</summary>
    private static bool OnApproach(Vector2i key) => High(key.x) != 0;

    /// <summary>The state on the discounted stretch at <paramref name="cell"/>.</summary>
    private static Vector2i ApproachKey(Vector2i cell) => new(cell.x + (1 << ApproachShift), cell.y);

    /// <summary>The metres of the discounted stretch left at state <paramref name="key"/> (0 for a plain cell), its label's.</summary>
    private static float ApproachLeft(Dictionary<Vector2i, float> stretchLeft, Vector2i key) =>
        OnApproach(key) && stretchLeft.TryGetValue(key, out float left) ? left : 0f;

    /// <summary>The state a plain step in direction <paramref name="d"/> from a state on the discounted stretch with
    /// <paramref name="left"/> metres of it left arrives at <paramref name="to"/> in: still on the stretch while some of it
    /// is left past the step (its label then carries what is left), else the plain cell.</summary>
    private Vector2i ApproachNext(float left, Vector2i to, int d) =>
        left - DirectionCosts[d] * CellSize > 0f ? ApproachKey(to) : to;

    /// <summary>A plain step's price in direction <paramref name="d"/> from a state with <paramref name="left"/> metres of
    /// the discounted stretch after a viaduct's far bank (<see cref="ViaductApproachReach"/>, along the path's own steps):
    /// the part of the step within the stretch at <see cref="ViaductApproachFactor"/> of its price, the rest at its price.
    /// A crossing is not discounted, and ends the stretch (the search lands it on a plain cell).</summary>
    private float ApproachAfterJump(float left, int d, float moveCost)
    {
        float length = DirectionCosts[d] * CellSize;
        return moveCost * (1f - (1f - ViaductApproachFactor) * Mathf.Min(length, left) / length);
    }

    /// <summary>Whether two cells are one plain step apart (one of <see cref="Directions"/>), not a crossing's jump.</summary>
    private static bool OneStep(Vector2i from, Vector2i to)
    {
        int dx = System.Math.Abs(to.x - from.x), dy = System.Math.Abs(to.y - from.y);
        return dx <= 2 && dy <= 2 && dx + dy > 0 && !(dx == 2 && dy != 1) && !(dy == 2 && dx != 1);
    }

    /// <summary>A viaduct jump's price from state <paramref name="from"/>, less the discount (<see cref="ViaductApproachFactor"/>)
    /// on the steps of the search's own path within <see cref="ViaductApproachReach"/> before its near bank, measured along
    /// those steps (the step across the reach's start prorated), back no further than a crossing, an earlier jump or its
    /// discounted stretch. Never below the plain distance price of the jump and of its far stretch's discount: a viaduct and
    /// its discounted stretch together never cost less than plain flat road over the same metres, so the search's distance
    /// bound holds (each step back is at least a cell long, so the walk ends within the reach).</summary>
    internal float ApproachToJump(Vector2i from, Vector2i landing, float jumpCost, Dictionary<Vector2i, Vector2i> cameFrom,
        Dictionary<Vector2i, float> gCosts, HashSet<Vector2i> byViaduct)
    {
        if (ViaductApproachReach <= 0f) return jumpCost;
        float left = ViaductApproachReach, approach = 0f;
        Vector2i at = from;
        while (left > 0f && !OnApproach(at) && !byViaduct.Contains(at) && cameFrom.TryGetValue(at, out Vector2i prev)
               && !OnApproach(prev) && OneStep(prev, at))
        {
            float length = Vector2.Distance(CellCentre(prev), CellCentre(at));
            float price = gCosts[at] - gCosts[prev];
            if (length > left) { approach += price * left / length; break; }
            approach += price;
            left -= length;
            at = prev;
        }
        float floor = BaseCost * (Vector2.Distance(CellCentre(CellOf(from)), CellCentre(landing))
                                  + (1f - ViaductApproachFactor) * ViaductApproachReach);
        return Mathf.Max(floor, jumpCost - (1f - ViaductApproachFactor) * approach);
    }

    /// <summary>How far back along the search's own path a viaduct jump is checked against the ones before it.</summary>
    private const float JumpLookBack = 64f;

    /// <summary>
    /// A jump from <paramref name="from"/> to <paramref name="landing"/> clear of the viaducts earlier on the same path
    /// (DvergrViaduct.TooClose, the one owner of the clearance between structures): walked back along it up to
    /// <see cref="JumpLookBack"/>, every cell reached by a jump is an earlier viaduct's far end (two viaducts were
    /// seen on one road, Start to MountainCave02).
    /// </summary>
    private bool ClearOfJumpsBefore(Vector2i from, Vector2i landing, Dictionary<Vector2i, Vector2i> cameFrom, HashSet<Vector2i> byViaduct)
    {
        if (byViaduct.Count == 0) return true;
        List<(Vector2, Vector2)>? before = null;
        Vector2 start = CellCentre(CellOf(from));
        Vector2i at = from;
        for (int guard = 0; guard < 64 && Vector2.Distance(CellCentre(CellOf(at)), start) <= JumpLookBack; guard++)
        {
            if (!cameFrom.TryGetValue(at, out Vector2i prev)) break;
            if (byViaduct.Contains(at)) (before ??= new List<(Vector2, Vector2)>()).Add((CellCentre(CellOf(prev)), CellCentre(CellOf(at))));
            at = prev;
        }
        return before == null || !DvergrViaduct.IsTooClose(start, CellCentre(landing), before);
    }

    private (Vector2i landing, float cost, Vector2 from, Vector2 to)? ViaductOverTerrain(Vector2i from, Vector2Int step)
    {
        // No biome test here: whether the valley is Mistlands is the viaduct's own share rule (ValleyCrossing, through
        // DvergrViaduct's refusal), so a valley whose near rim is just outside the Mistlands is still offered.
        float rim = m_terrain.Height(from);
        // A valley starts here: the next cell on is well below the rim.
        if (m_terrain.Height(new Vector2i(from.x + step.x, from.y + step.y)) > rim - ViaductRimDrop) return null;
        int maxCells = Mathf.FloorToInt(DvergrViaduct.MaxValleySpan / (CellSize * Mathf.Sqrt(step.x * step.x + step.y * step.y)) + 1e-3f);
        for (int s = 2; s <= maxCells; s++)
        {
            var check = new Vector2i(from.x + step.x * s, from.y + step.y * s);
            if (m_terrain.River(check) > 0f) return null;   // a river is a bridge's crossing
            float h = m_terrain.Height(check);
            if (h < RoadWater.LandingFloor) return null;
            if (h < rim - RoadConstants.MaxBridgeBankDelta) continue;
            if (h > rim + RoadConstants.MaxBridgeBankDelta) return null;
            var (a, b) = BridgeLayout.TurnedOntoPlaceableHeading(CellCentre(from), CellCentre(check));
            // One probe reads its deck once (DvergrViaduct.GradeDeck): the refusal's reads are the price's.
            RoadCrossing? viaduct = DvergrViaduct.ValleyCrossing(a, b, m_worldGen, out _, out DvergrViaduct.GradeDeck? deck);
            if (viaduct == null || deck == null) return null;
            float length = Vector2.Distance(a, b);
            return (check, DvergrViaduct.Price(deck, ViaductCostFixed, ViaductCostPerMeter, ViaductEndStairCostPerMeter) + BaseCost * length, a, b);
        }
        return null;
    }

    /// <summary>
    /// Scans cell by cell from a dry cell across river water in one of the
    /// eight principal directions and lands on the first dry ground outside
    /// the river band. A jump the crossing builder fords
    /// (RoadCrossingDetector.IsFordJump, the one rule) is a FORD at a small
    /// cost (with fords on); anything else is a BRIDGE at the bridge cost
    /// (with bridges on), held to near-level banks. Standing water is measured by RoadCrossingDetector.Detect too; a puddle keeps ordinary road.
    /// A dry river valley is not crossed. The jump is judged as it is built, from the point the road
    /// leaves <paramref name="from"/> by: its centre, or <paramref name="leaving"/>
    /// where the cell is the path's first, which the road leaves from the exact start
    /// (<see cref="ReconstructPath"/> puts it in the centre's place), so the class,
    /// span and banks the search prices are those of the segment the crossing is built on.
    /// </summary>
    private bool TryGetRiverCrossing(Vector2i from, Vector2? leaving, Vector2Int direction, out Vector2i landing, out float crossingCost)
    {
        landing = from;
        crossingCost = 0f;

        // Every heading the search steps on may cross (CrossingTerrainOf reads the cells a knight's step passes between).
        Vector2 fromWorld = leaving ?? CellCentre(from);

        CrossingFacts facts = CrossingFactsFor(from, leaving, direction);
        // A wade can finish inside its pool. Targets belong to this search, not to the terrain memo.
        // Its eligibility is the crossing owner's verdict on the complete pool.
        if (facts.Standing && facts.Style == FordStyle.Wade && m_crossingDestination != null)
            for (var cell = new Vector2i(from.x + direction.x, from.y + direction.y); cell != facts.Landing;
                cell = new Vector2i(cell.x + direction.x, cell.y + direction.y))
            {
                if (m_crossingDestination(cell) is not Vector2 target) continue;
                facts.Landing = cell;
                facts.ToBank = target;
                facts.Distance = Vector2.Distance(facts.FromBank, facts.ToBank);
                facts.SqueezedAt = CrossingSqueeze(facts);
                break;
            }
        if (facts.Kind == CrossingTerrain.None)
            return false;
        // Eligibility is for the complete pool; acceptance is for the endpoint this search uses.
        // An unused far shore cannot refuse an interior target, and the new landing must pass too.
        if (facts.SqueezedAt is Vector2 at)
        {
            RecordSqueeze(at, landing: true);
            return false;
        }
        Vector2 world = CellCentre(facts.Landing);
        if (m_noJumps.Count > 0 && Banned(fromWorld, world))
            return false;
        if (m_gridOnly.Count > 0 && KnightJump(direction.x, direction.y) && GridOnlyAt(fromWorld, world))
            return false;
        // Nor over a location's footprint or its clearance, bank to bank as built: the location protection every other step
        // and jump of the search asks (RoadLocationProtection.BlocksRoute). No road piece is planned over the deck, so no
        // later check of the road reads it.
        // Nor over or onto a zone the network goes round, bank to bank as built (a swamp bridge's walked-out banks
        // included): the one ground rule every other step and jump of the search asks (NetworkGround).
        if (NetworkGround.BlocksRoute(facts.FromBank, facts.ToBank, LocationClearance, m_locations, m_searchStart, m_searchEnd))
            return false;
        landing = facts.Landing;
        // Both ends already on road (a finished road's banks): the
        // crossing exists, so using it costs a fraction of its price,
        // and a later road detours to share it rather than build
        // another beside it, up to what that saving buys.
        bool shared = OnExistingRoad(fromWorld) && OnExistingRoad(world);
        // Standing fords keep the shallow-water rate per cell of their measured wet run, rather than
        // a river's fixed charge. A puddle is still one ordinary step; bridges keep their bridge price.
        float price = facts.Puddle ? StandingWaterPenalty
            : facts.Bridge ? (BridgeCostFixed + BridgeCostPerMeter * facts.Distance) * BridgeHeightFactor(facts.StoneReach, facts.Distance)
            : facts.Standing ? StandingWaterPenalty * facts.WetLength / CellSize : RoadConstants.RiverCrossingPenalty;
        if (shared)
            price *= RoadConstants.SharedCrossingCostFraction;
        crossingCost = price + BaseCost * facts.Distance
            + RoadConstants.BankDeltaPenalty * facts.BankDelta * facts.BankDelta;
        return true;
    }

    /// <param name="whole">False: only <see cref="CrossingFacts.InteriorWater"/> is asked (an ordinary move's question), and only
    /// it is worked out until a crossing step asks for the rest; the same facts either way.</param>
    private CrossingFacts CrossingFactsFor(Vector2i from, Vector2? leaving, Vector2Int direction, bool whole = true)
    {
        Vector2 fromWorld = leaving ?? CellCentre(from);
        // The terrain's part of the crossing, once per cell and direction for this pathfinder's life (as the squeeze is):
        // the scan, the class, the swamp shelf, the span and bank caps and full crossing's squeeze read the world and the locations
        // alone, which do not change during a generation. Not for the path's first cell, which is left from the exact
        // start. What a search or the network changes (a banned jump, the search's own targets, their squeezes and the zones it goes
        // round, a road already on both banks) is asked below on every call, as before.
        CrossingFacts facts = default;
        long key = CrossingKey(from, direction, Fords, Bridges, RoadCrossingDetector.Shallows);
        // Kept only where the generation spends memory for speed (GenerationMemory.KeepsCrossingFacts); the facts are the
        // same worked out again.
        bool keep = !leaving.HasValue && GenerationMemory.KeepsCrossingFacts;
        bool known = keep && m_crossings.TryGetValue(key, out facts);
        if (known && (facts.Whole || !whole)) return facts;
        bool interiorWater = known ? facts.InteriorWater : InteriorWater(fromWorld, from, direction);
        if (!whole) facts = new CrossingFacts { Kind = CrossingTerrain.None, InteriorWater = interiorWater };
        else
        {
            facts = CrossingTerrainOf(from, fromWorld, direction, interiorWater);
            if (facts.Kind == CrossingTerrain.Crossing) facts.SqueezedAt = CrossingSqueeze(facts);
            facts.Whole = true;
        }
        if (keep) m_crossings[key] = facts;
        return facts;
    }

    /// <summary>Whether the step from <paramref name="from"/> in direction <paramref name="directionIndex"/> runs over standing
    /// water inside it (<see cref="CrossingFactsFor"/>'s <see cref="CrossingFacts.InteriorWater"/>, read from the cell's centre:
    /// terrain alone, the same whatever the switches), asked of nearly every step the search prices. Kept a row a cell where the
    /// generation keeps crossing facts (GenerationMemory.KeepsCrossingFacts), each direction worked out as first asked, so the
    /// row is looked up once for a cell's steps (<see cref="m_waterRow"/>) and not once a step.</summary>
    private bool StepInteriorWater(Vector2i from, int directionIndex)
    {
        if (!GenerationMemory.KeepsCrossingFacts) return CrossingFactsFor(from, null, Directions[directionIndex], whole: false).InteriorWater;
        if (m_waterRow == null || m_waterCell != from)
        {
            if (!m_waterRows.TryGetValue(from, out m_waterRow)) m_waterRows[from] = m_waterRow = new WaterRow();
            m_waterCell = from;
        }
        WaterRow row = m_waterRow;
        int bit = 1 << directionIndex;
        if ((row.Known & bit) == 0)
        {
            if (CrossingFactsFor(from, null, Directions[directionIndex], whole: false).InteriorWater) row.Wet |= bit;
            row.Known |= bit;
        }
        return (row.Wet & bit) != 0;
    }

    private sealed class WaterRow { public int Known, Wet; }
    private readonly Dictionary<Vector2i, WaterRow> m_waterRows = new(RoadGridComparer.Instance);
    /// <summary>The row of the cell last asked (<see cref="m_waterCell"/>), or null.</summary>
    private WaterRow? m_waterRow;
    private Vector2i m_waterCell;

    /// <summary>What the terrain says of a river crossing from a cell in a direction (<see cref="TryGetRiverCrossing"/>'s
    /// terrain part): none, or a crossing (its landing, ford or bridge, its banks as
    /// built, its span and the banks' height difference).</summary>
    private enum CrossingTerrain : byte { None, Crossing }

    // Retain the full crossing's identity even when squeezed; a search may use an earlier wet endpoint.
    // Ordinary crossings keep this terrain-only verdict cached. A clipped crossing asks again at its chosen end.
    private Vector2? CrossingSqueeze(CrossingFacts facts)
    {
        if (SqueezedCell(facts.Landing)) return CellCentre(facts.Landing);
        return DeckSqueezed(facts.FromBank, facts.ToBank, out Vector2 at) ? at : null;
    }

    private struct CrossingFacts
    {
        public CrossingTerrain Kind;
        public Vector2? SqueezedAt;
        public Vector2i Landing;
        public bool Bridge;
        public FordStyle Style;
        public bool Standing, Puddle, InteriorWater;
        /// <summary>Everything worked out, not only <see cref="InteriorWater"/> (<see cref="CrossingFactsFor"/>).</summary>
        public bool Whole;
        public Vector2 FromBank, ToBank;
        public float Distance, BankDelta, WetLength;
        /// <summary>A bridge's deck toward the tallest the stone kit stands, 0 to 1, where a stone deck belongs
        /// (<see cref="BridgeStoneReach"/>); 0 for anything else, for a span no longer than a ford's, and for a knight's jump,
        /// which pays the bridge's full price at any height.</summary>
        public float StoneReach;
    }

    /// <summary><see cref="CrossingFacts"/> per (cell, direction, fords, bridges, shallows), for this pathfinder's life: a
    /// generation's pathfinders are its own (RoadNetworkGenerator.ResetGenerationState), one per worker, so nothing here
    /// outlives the generation or is shared between threads.</summary>
    private readonly Dictionary<long, CrossingFacts> m_crossings = new(MixedKey.Instance);

    /// <summary>A packed key's hash, mixed: long's own (its halves xored) puts neighbouring cells and directions in one bucket.</summary>
    private sealed class MixedKey : IEqualityComparer<long>
    {
        public static readonly MixedKey Instance = new();
        public bool Equals(long a, long b) => a == b;
        public int GetHashCode(long key) => unchecked((int)((ulong)(key * -7046029254386353131L) >> 32));
    }

    /// <summary><see cref="m_crossings"/>' key: the cell (26 bits a side), the direction (-2..2 each) and the three switches in one
    /// long, asked on every move (a tuple of seven hashed and compared field by field cost the search more than the facts).</summary>
    internal static long CrossingKey(Vector2i cell, Vector2Int direction, bool fords, bool bridges, bool shallows) =>
        ((long)(cell.x & 0x3FFFFFF) << 38) | ((long)(cell.y & 0x3FFFFFF) << 12) | (long)((direction.x + 2) << 9)
        | (long)((direction.y + 2) << 6) | (fords ? 4L : 0L) | (bridges ? 2L : 0L) | (shallows ? 1L : 0L);

    // Probe every move long enough to span a qualifying pool, including adjacent diagonals.
    private bool InteriorWater(Vector2 fromWorld, Vector2i from, Vector2Int direction)
    {
        Vector2 nextWorld = CellCentre(new Vector2i(from.x + direction.x, from.y + direction.y));
        return Vector2.Distance(fromWorld, nextWorld) >= RoadCrossingDetector.ShallowMinLength
            && RoadCrossingDetector.SegmentCrossesRiver(fromWorld, nextWorld, m_worldGen, standing: true, natural: NaturalHeightAt);
    }

    /// <param name="interiorWater">The move's own probe (<see cref="InteriorWater"/>), worked out once for both questions.</param>
    private CrossingFacts CrossingTerrainOf(Vector2i from, Vector2 fromWorld, Vector2Int direction, bool interiorWater)
    {
        // A rejected crossing still contains water: ordinary moves must not reinterpret it as dry land.
        var none = new CrossingFacts { Kind = CrossingTerrain.None, InteriorWater = interiorWater };
        // Every heading the search steps on, the knight's moves included: a knight's step passes through the two cells
        // between its own (KnightBetweenPair), and the scan reads them as it reads its own, so no water on the line is skipped
        // and the crossing starts at the same bank. Neither of them can be a landing: the road lands on the cells it steps on.
        bool knight = Mathf.Abs(direction.x) > 1 || Mathf.Abs(direction.y) > 1;

        // The cells' height and river weight are the search memo's own facts (the same
        // generator calls at the same cell centres): read through it, a scan repeated from
        // every bank cell of a river reads each cell once, and stays out of the game's river
        // cache, whose every miss takes a write lock all workers share.
        bool sawRiverWater = false, sawWater = interiorWater || RoadWater.Wet(NaturalHeightAt(fromWorld));
        int maxCells = Bridges ? RoadConstants.MaxBridgeCrossingCells : RoadConstants.MaxRiverCrossingCells;
        // As far as the longest crossing reaches (maxCells cells), not maxCells steps: a knight's step is 2.24 cells long, and
        // a landing past the reach is refused below anyway (the viaduct scan's bound, ViaductOverTerrain).
        int maxSteps = knight ? Mathf.FloorToInt(maxCells / Mathf.Sqrt(direction.x * direction.x + direction.y * direction.y) + 1e-3f) + 1 : maxCells;

        for (int step = 1; step <= maxSteps; step++)
        {
            Vector2i check = new Vector2i(from.x + direction.x * step, from.y + direction.y * step);
            if (knight)
            {
                var (one, two) = KnightBetweenPair(new Vector2i(check.x - direction.x, check.y - direction.y), direction);
                ReadBetween(one, ref sawWater, ref sawRiverWater);
                ReadBetween(two, ref sawWater, ref sawRiverWater);
            }
            Vector2 world = CellCentre(check);
            float height = m_terrain.Height(check);
            float riverWeight = m_terrain.River(check);
            bool water = height < RoadWater.LandingFloor;
            bool riverCore = RoadWater.River(riverWeight);

            // River jumps keep their dry-shore scan. Standing water ends at its natural water line;
            // its complete measured run and treatment are the detector's, including the puddle exemption.
            bool wet = RoadWater.Wet(height);
            sawWater |= wet;
            sawRiverWater |= water && riverCore;
            if (riverCore || (sawRiverWater ? water : wet)) continue;
            if (!sawRiverWater && !(RoadCrossingDetector.Shallows && sawWater)) return none;

            bool river = sawRiverWater || RoadCrossingDetector.SegmentCrossesRiver(fromWorld, world, m_worldGen);
            bool bridge, puddle = false;
            float wetLength = 0f;
            FordStyle style = FordStyle.None;
            Vector2 fromBank = fromWorld, toBank = world;
            List<RoadCrossing>? found = null;
            if (river)
            {
                bridge = !RoadCrossingDetector.IsFordJump(fromWorld, world, m_worldGen);
                // A bridge on a knight's heading is built on the hammer's nearest heading, its line turned about its middle
                // (BridgeLayout.TurnedOntoPlaceableHeading, the viaduct's turn too): its banks, span, bank heights and swamp
                // shelf are judged and priced on the turned line, the one the detector builds within a cell of.
                if (knight && bridge) (fromBank, toBank) = BridgeLayout.TurnedOntoPlaceableHeading(fromWorld, world);
            }
            else
            {
                // The detector walks a wade's banks out along the path to the dry floor. Give it the same
                // bank reach as construction, including the near shore when leaving a cell inside the pool.
                Vector2 near = fromWorld;
                for (int back = 0; back < maxCells && RoadWater.Wet(BiomeBlendedHeight.GetBlendedHeight(near.x, near.y, m_worldGen)); back++)
                    near = CellCentre(new Vector2i(from.x - direction.x * (back + 1), from.y - direction.y * (back + 1)));
                Vector2 along = (world - near).normalized;
                var context = new List<Vector2> { near - along * RoadConstants.HighBankReach, near, world,
                    world + along * RoadConstants.HighBankReach };
                found = RoadCrossingDetector.Detect(context, m_worldGen, Bridges, Fords);
                // The two extra stretches supply banks; any other wet run on them belongs to its own step.
                found.RemoveAll(c => c.ToIndex <= 1 || c.FromIndex >= 2);
                if (found.Exists(c => c.Invalid)) return none;
                foreach (var c in found) wetLength += c.WetLength;
                puddle = found.Count == 0;
                bridge = found.Exists(c => c.Kind == CrossingKind.Bridge);
                if (found.Count > 0) { fromBank = found[0].FromBank; toBank = found[found.Count - 1].ToBank; style = found[0].Style; }
                if (puddle)
                {
                    var next = new Vector2i(from.x + direction.x, from.y + direction.y);
                    return new CrossingFacts { Kind = CrossingTerrain.Crossing, Standing = true, Puddle = true, InteriorWater = interiorWater, Landing = next,
                        FromBank = fromWorld, ToBank = CellCentre(next), Distance = Vector2.Distance(fromWorld, CellCentre(next)) };
                }
            }
            if (bridge ? !Bridges : !Fords) return none;
            // Land beyond the bank the detector bought, so the next road step cannot turn inside its wade approach.
            if (style == FordStyle.Wade)
            {
                Vector2 along = (world - fromWorld).normalized;
                int landingStep = step;
                while (Vector2.Dot(CellCentre(check) - toBank, along) < 0f)
                {
                    if (++landingStep > maxSteps) return none;
                    check = new Vector2i(from.x + direction.x * landingStep, from.y + direction.y * landingStep);
                }
            }
            // A pool's road meets each bank at the ground there, and its piece on to this step's cell is planned from it: a bank
            // no graded road leaves within the earthwork cap is the planner's refusal (RoadNetworkGenerator.PastTerrainLimitOffBank),
            // asked here so the search goes round rather than choosing a road it would then search again. Every treatment: the
            // planner may raise a span the search priced (RoadCrossingDetector.KeepStructuresApart) and meets the raise's bank.
            if (!river && !bridge
                && (RoadNetworkGenerator.PastTerrainLimitOffBank(fromBank, fromWorld, m_worldGen)
                    || RoadNetworkGenerator.PastTerrainLimitOffBank(toBank, CellCentre(check), m_worldGen)))
                return none;
            // In a swamp the detector walks a bridge's wet bank out over the
            // wade shelf until it finds dry ground, which can add a great deal
            // of deck. Do it HERE, before the span is measured against the cap
            // and before the crossing is priced, so the search judges the
            // bridge it will actually get. Deciding afterwards would leave a
            // route accepted at one length and built at another.
            // Its biome as the detector reads it: at the middle of its shores (RoadCrossingDetector.ShoreBiome).
            float shelf = river && bridge ? RoadRules.For(RoadCrossingDetector.ShoreBiome(fromBank, toBank, m_worldGen)).ShelfReach : 0f;
            if (shelf > 0f)
                (fromBank, toBank) = RoadCrossingDetector.ExtendOverShelf(fromBank, toBank, shelf, m_worldGen);

            float distance = Vector2.Distance(fromBank, toBank);
            // The longest crossing: a bridge's, or, in a search that may only ford, the one ford cap (RoadCrossingDetector.WithinFordCap).
            if (Bridges ? distance > RoadConstants.MaxBridgeSpan : !RoadCrossingDetector.WithinFordCap(distance))
                return none;

            float bankDelta = Mathf.Abs(
                BiomeBlendedHeight.GetBlendedHeight(toBank.x, toBank.y, m_worldGen)
                - BiomeBlendedHeight.GetBlendedHeight(fromBank.x, fromBank.y, m_worldGen));
            if (bankDelta > (bridge ? RoadConstants.MaxBridgeBankDelta : RoadConstants.MaxFordBankDelta))
                return none;
            // Last, being the dearest (a full detection): the bridge the detector makes of the knight's own line must turn onto a
            // heading the hammer builds; a jump whose water it leaves off them is none. Read on the knight's line: on the turned
            // line the road is laid on, already placeable, it refuses almost nothing (tried: it then took knight bridges whose
            // ways on came off their banks on fill, refused along a ridge with no way round).
            // The same detection, once, also gives a bridge longer than a ford its deck's height (BridgeStoneReach), knight's
            // heading or not.
            if (river && bridge && (knight || !RoadCrossingDetector.WithinFordCap(distance))) found = JumpCrossings(fromWorld, world);
            if (river && knight && bridge && !KnightBridgeIsPlaceable(found!)) return none;

            return new CrossingFacts
            {
                Kind = CrossingTerrain.Crossing, Landing = check, Bridge = bridge, Style = style, Standing = !river, InteriorWater = interiorWater,
                FromBank = fromBank, ToBank = toBank, Distance = distance, BankDelta = bankDelta, WetLength = wetLength,
                // A knight's jump pays the full price (no height share): its cheaper tall bridges, off the Mistlands, drew Mistlands
                // roads off the grid viaducts they were built on, onto routes then refused.
                StoneReach = bridge && !knight && !RoadCrossingDetector.WithinFordCap(distance) && found != null ? BridgeStoneReach(found) : 0f,
            };
        }

        return none;
    }

    /// <summary>The two cells a knight's step from <paramref name="at"/> in <paramref name="direction"/> passes through between
    /// its ends: one cell on along the long axis, and that cell and the one beside it toward the step's short side (the line
    /// runs through their shared edge).</summary>
    internal static (Vector2i, Vector2i) KnightBetweenPair(Vector2i at, Vector2Int direction)
    {
        int sx = System.Math.Sign(direction.x), sy = System.Math.Sign(direction.y);
        return Mathf.Abs(direction.x) > 1
            ? (new Vector2i(at.x + sx, at.y), new Vector2i(at.x + sx, at.y + sy))
            : (new Vector2i(at.x, at.y + sy), new Vector2i(at.x + sx, at.y + sy));
    }

    /// <summary>The crossings the detector builds on a jump from <paramref name="a"/> to <paramref name="b"/>
    /// (RoadCrossingDetector.Detect, with the same bank reach either side as a pool's): the crossings a road through the
    /// two points straight on builds, its banks, its climb to the bank tops and its deck as built.</summary>
    private List<RoadCrossing> JumpCrossings(Vector2 a, Vector2 b)
    {
        Vector2 along = (b - a).normalized;
        var context = new List<Vector2> { a - along * RoadConstants.HighBankReach, a, b, b + along * RoadConstants.HighBankReach };
        return RoadCrossingDetector.Detect(context, m_worldGen, Bridges, Fords);
    }

    /// <summary>Whether the bridge the detector builds on a knight's jump (<see cref="JumpCrossings"/>) stands on a heading
    /// the hammer builds.</summary>
    private static bool KnightBridgeIsPlaceable(List<RoadCrossing> jump)
    {
        foreach (RoadCrossing c in jump)
            if (c.ToIndex > 1 && c.FromIndex < 2 && c.Kind == CrossingKind.Bridge && !BridgeLayout.HeadingIsPlaceable(BridgeLayout.YawDegrees(c.Direction)))
                return false;
        return true;
    }

    /// <summary>
    /// How far the deck of a jump's bridge stands toward the tallest the stone kit stands
    /// (BridgeLayout.StoneHeightReach), on the crossings the detector builds of the jump (<see cref="JumpCrossings"/>, or a
    /// pool's own detection): the deck every kit lays, read on the crossing the road through the jump straight on is built
    /// with. Where the stone kit belongs (BridgeStructures.StoneBelongs) only, and not where the detection climbed to the bank
    /// tops (RoadCrossing.OnTops), else 0; the lowest of the jump's bridges.
    /// </summary>
    private float BridgeStoneReach(List<RoadCrossing> jump)
    {
        float reach = float.MaxValue;
        foreach (RoadCrossing c in jump)
            if (c.ToIndex > 1 && c.FromIndex < 2 && c.Kind == CrossingKind.Bridge)
                // The owner's decision of 13 Nov 2026: no share for a deck the detection climbed to the bank tops for, which
                // the road the builder lays may not take (a bend within the climb's reach lays it at the water's edge); a deck is
                // never discounted for a height it may not be laid at.
                reach = Mathf.Min(reach, !c.OnTops && BridgeStructures.StoneBelongs(c, m_worldGen) ? BridgeLayout.StoneHeightReach(c, m_worldGen) : 0f);
        return reach == float.MaxValue ? 0f : reach;
    }

    /// <summary>
    /// The share of its price a bridge pays for its deck's height: the share at the stone kit's tallest
    /// (<see cref="BridgeHeightShare"/>) to the power of how far the deck stands toward it (<paramref name="reach"/>,
    /// <see cref="CrossingFacts.StoneReach"/>) times the span's weight, which rises from nothing at the longest ford
    /// (<see cref="RoadConstants.MaxFordSpan"/>) to the whole at the longest bridge (<see cref="RoadConstants.MaxBridgeSpan"/>).
    /// The viaduct's shape (DvergrViaduct.HeightFactor: the price falls geometrically as the structure stands taller, up to its
    /// ceiling and no further), weighted by the span: a short bridge may sit low, a long one is bought tall. 1 for a deck at the
    /// water line or a span no longer than a ford's (RoadCrossingDetector.WithinFordCap, the detector's ford cap). A price only: every bridge is judged by the same rules.
    /// </summary>
    internal float BridgeHeightFactor(float reach, float span)
    {
        float weight = RoadCrossingDetector.WithinFordCap(span) ? 0f
            : Mathf.Clamp01((span - RoadConstants.MaxFordSpan) / (RoadConstants.MaxBridgeSpan - RoadConstants.MaxFordSpan));
        float power = Mathf.Clamp01(reach) * weight;
        return power <= 0f ? 1f : Mathf.Pow(BridgeHeightShare, power);
    }

    /// <summary>A cell a knight's step passes between, read into the scan's water as its own cells are (CrossingTerrainOf).</summary>
    private void ReadBetween(Vector2i cell, ref bool sawWater, ref bool sawRiverWater)
    {
        float height = m_terrain.Height(cell);
        sawWater |= RoadWater.Wet(height);
        sawRiverWater |= height < RoadWater.LandingFloor && RoadWater.River(m_terrain.River(cell));
    }

    // Exact cell-centre reads share the terrain memo; interior samples are the sampler's exact natural heights, their
    // heightmap corners read through the memo's own (RoadTerrainSamples.NaturalAt).
    private float NaturalHeightAt(Vector2 point)
    {
        var cell = WorldToGrid(point);
        Vector2 centre = CellCentre(cell);
        return centre.x == point.x && centre.y == point.y ? m_terrain.Height(cell) : m_terrain.NaturalAt(point);
    }

    /// <summary>Whether a finished road already runs through this point.</summary>
    private static bool OnExistingRoad(Vector2 world) =>
        RoadSpatialGrid.IsInitialized && RoadSpatialGrid.AnyRoadAt(world.x, world.y);

    // The cell being expanded is the step's start for all of its neighbours: whether a road runs through it is read once
    // per expansion. Forgotten at each search's start (RentSearchState), as roads are committed between searches.
    private Vector2i m_fromRoadCell;
    private bool m_fromRoadKnown, m_fromOnRoad;

    // Likewise whether any location's footprint comes within the clearance and the longest step of it (RoadLocationProtection.NoneWithin):
    // where none does, no step from it comes near one, and the footprints are not walked for each of its sixteen steps.
    // Forgotten at each search's start with the road's (RentSearchState).
    private Vector2i m_fromClearCell;
    private bool m_fromClearKnown, m_fromClear;

    /// <summary>The longest step the search takes (a knight's move), and a metre more for the float rounding of a step's
    /// nearest point.</summary>
    private static readonly float LongestStepReach = Mathf.Sqrt(5f) * CellSize + 1f;

    private bool FromClearOfLocations(Vector2i from, Vector2 fromWorld)
    {
        if (!m_fromClearKnown || m_fromClearCell != from)
        {
            m_fromClear = RoadLocationProtection.NoneWithin(fromWorld, LocationClearance + LongestStepReach);
            m_fromClearCell = from;
            m_fromClearKnown = true;
        }
        return m_fromClear;
    }

    private bool FromOnExistingRoad(Vector2i from, Vector2 fromWorld)
    {
        if (!m_fromRoadKnown || m_fromRoadCell != from)
        {
            m_fromOnRoad = OnExistingRoad(fromWorld);
            m_fromRoadCell = from;
            m_fromRoadKnown = true;
        }
        return m_fromOnRoad;
    }

    /// <summary>The deck line of the viaduct the search bought from <paramref name="from"/> to <paramref name="landing"/>
    /// (<see cref="ViaductOverTerrain"/>'s, read from its memo where it is kept).</summary>
    private (Vector2 from, Vector2 to) ViaductJumpLine(Vector2i from, Vector2i landing)
    {
        Vector2Int direction = JumpDirection(landing.x - from.x, landing.y - from.y);
        int d = System.Array.IndexOf(Directions, direction);
        if (d >= 0 && d < RoadRules.ViaductHeadings && m_viaducts.TryGetValue(from, out ViaductRow? row) && row.Found[d] is { } found && found.landing == landing)
            return (found.from, found.to);
        return BridgeLayout.TurnedOntoPlaceableHeading(CellCentre(from), CellCentre(landing));
    }

    /// <summary>The search heading a jump of (<paramref name="dx"/>, <paramref name="dz"/>) cells was made on: the jump divided by
    /// its whole number of steps (a grid heading's or a knight's).</summary>
    internal static Vector2Int JumpDirection(int dx, int dz)
    {
        int a = System.Math.Abs(dx), b = System.Math.Abs(dz);
        while (b != 0) (a, b) = (b, a % b);
        return a == 0 ? new Vector2Int(0, 0) : new Vector2Int(dx / a, dz / a);
    }

    /// <summary>
    /// The searched path from <paramref name="start"/> to <paramref name="end"/> through the cells the search came by, its
    /// crossings' banks and staircase and viaduct steps recorded. Its first cell is left from the exact start, never its
    /// centre; its last, the goal's cell, is arrived at the exact end, its centre dropped (a finder that
    /// <see cref="StopsAtItsGoal"/>: a marked road's legs) where the end lies back toward the
    /// point before it (the road would run past its goal to the centre and double back onto itself) and the cell was reached
    /// by an ordinary move: a staircase, a viaduct or a crossing lands at the centre, so it is kept there. The one owner of
    /// a searched path's points, for every search (FindPath, FindPathBetween, FindPathToNetwork).
    /// </summary>
    private List<Vector2> ReconstructPath(Dictionary<Vector2i, Vector2i> cameFrom, Dictionary<Vector2i, int> byStair, HashSet<Vector2i> byViaduct,
        Vector2i current, Vector2 start, Vector2 end)
    {
        List<Vector2> path = new List<Vector2> { end };

        bool bankReplacedCell = false;
        // The keys are search states (a cell, or one on a viaduct's discounted stretch); the path is their cells.
        bool goal = true;
        while (cameFrom.ContainsKey(current))
        {
            bool skipGoalCentre = goal && StopsAtItsGoal && PastItsGoal(cameFrom, byStair, byViaduct, current, start, end);
            goal = false;
            Vector2i before = cameFrom[current];
            Vector2i cell = CellOf(current), beforeCell = CellOf(before);
            if (!bankReplacedCell && !skipGoalCentre) path.Add(CellCentre(cell));
            bankReplacedCell = false;
            if (byStair.ContainsKey(current)) LastStairSteps.Add((CellCentre(beforeCell), CellCentre(cell)));
            if (byViaduct.Contains(current))
            {
                // The deck line the search priced (BridgeLayout.TurnedOntoPlaceableHeading): a knight's viaduct's ends stand a few metres off the cells'
                // centres, and the road runs to them.
                var (deckFrom, deckTo) = ViaductJumpLine(beforeCell, cell);
                LastViaductJumps.Add((deckFrom, deckTo));
                LastViaductGrounds.Add(Ground);
                if (deckTo != CellCentre(cell))
                {
                    if (path[path.Count - 1] == CellCentre(cell)) path[path.Count - 1] = deckTo; else path.Add(deckTo);
                    path.Add(deckFrom);
                    bankReplacedCell = true;
                }
            }
            if (!byViaduct.Contains(current) && !byStair.ContainsKey(current)
                && (Mathf.Abs(cell.x - beforeCell.x) > 1 || Mathf.Abs(cell.y - beforeCell.y) > 1))
            {
                var direction = JumpDirection(cell.x - beforeCell.x, cell.y - beforeCell.y);
                // The facts the search priced this jump on, from the facts memo: the path's first cell's from the exact start.
                var facts = CrossingFactsFor(beforeCell, cameFrom.ContainsKey(before) ? null : start, direction);
                if (facts.Kind == CrossingTerrain.Crossing && facts.Style == FordStyle.Wade && facts.Landing == cell)
                {
                    // Retain the detector's exact banks in the route it priced. The builder then sees that same
                    // straight natural bed, even where the road turns just before or after the crossing.
                    path.Add(facts.ToBank);
                    if (cameFrom.ContainsKey(before)) { path.Add(facts.FromBank); bankReplacedCell = true; }
                }
                else if (facts.Kind == CrossingTerrain.Crossing && facts.Bridge && facts.Landing == cell && cameFrom.ContainsKey(before)
                    && (Mathf.Abs(direction.x) > 1 || Mathf.Abs(direction.y) > 1))
                {
                    // A bridge on a knight's heading runs on the line the search priced it on, turned onto the hammer's
                    // heading (BridgeLayout.TurnedOntoPlaceableHeading): the road comes to the turned line's ends, so the
                    // detector finds its banks on a placeable line and turns nothing, and no approach bends back to a moved bank.
                    var (lineFrom, lineTo) = BridgeLayout.TurnedOntoPlaceableHeading(CellCentre(beforeCell), CellCentre(cell));
                    if (path[path.Count - 1] == CellCentre(cell)) path[path.Count - 1] = lineTo; else path.Add(lineTo);
                    path.Add(lineFrom);
                    bankReplacedCell = true;
                }
            }
            current = before;
        }
        LastStairSteps.Reverse();
        LastViaductJumps.Reverse();
        LastViaductGrounds.Reverse();

        path.Add(start);
        path.Reverse();
        return path;
    }

    /// <summary>Whether the goal's cell <paramref name="goal"/>, reached by an ordinary move (a step to a neighbour or a
    /// knight's move, not a staircase, a viaduct or a crossing's jump), has <paramref name="end"/> lying back toward the
    /// point the path comes from (the cell before's centre, or the exact start where that cell is the first): its centre
    /// is then past the goal (<see cref="ReconstructPath"/>).</summary>
    private bool PastItsGoal(Dictionary<Vector2i, Vector2i> cameFrom, Dictionary<Vector2i, int> byStair, HashSet<Vector2i> byViaduct,
        Vector2i goal, Vector2 start, Vector2 end)
    {
        if (byStair.ContainsKey(goal) || byViaduct.Contains(goal)) return false;
        Vector2i before = cameFrom[goal];
        Vector2i goalCell = CellOf(goal), beforeCell = CellOf(before);
        int dx = Mathf.RoundToInt(Mathf.Abs(goalCell.x - beforeCell.x)), dy = Mathf.RoundToInt(Mathf.Abs(goalCell.y - beforeCell.y));
        bool ordinary = (dx <= 1 && dy <= 1) || (dx == 2 && dy == 1) || (dx == 1 && dy == 2);
        if (!ordinary) return false;
        Vector2 centre = CellCentre(goalCell);
        Vector2 from = cameFrom.ContainsKey(before) ? CellCentre(beforeCell) : start;
        return Vector2.Dot(end - centre, from - centre) > 0f;
    }
}

/// <summary>
/// Where generation time goes, by kind of search: how many, how many found
/// a road, how many spent their whole iteration budget, and the wall time of
/// each group summed over threads. Added after a full-world generation with
/// stairs ran 10-100x longer than without. Thread-safe; reset per generation. Off unless
/// PROCEDURALROADS_TIMINGS is on: off, no search reads the clock or counts.
/// </summary>
public static class SearchStats
{
    /// <summary>PROCEDURALROADS_TIMINGS: time the searches and print the generation's timing and
    /// tally lines. Off by default.</summary>
    internal static readonly bool Enabled = DebugSwitches.Flag("TIMINGS", false);

    /// <summary>Kinds; a landing entry (the way onto a placed location's landing, every start tried) includes the
    /// stair layouts it ran, which are also counted as stair layouts.</summary>
    public const int Pricing = 0, Building = 1, Joining = 2, StairLayout = 3, LandingEntry = 4;
    private static readonly string[] Names = { "pricing searches", "build searches", "join searches", "stair layouts", "landing entries" };
    private static readonly long[] s_count = new long[Names.Length], s_found = new long[Names.Length], s_maxed = new long[Names.Length], s_iterations = new long[Names.Length];
    private static readonly long[] s_ticks = new long[Names.Length], s_maxedTicks = new long[Names.Length];

    public static void Reset()
    {
        foreach (long[] a in new[] { s_count, s_found, s_maxed, s_iterations, s_ticks, s_maxedTicks }) System.Array.Clear(a, 0, a.Length);
    }

    // The same, for the thread's own island: an island is built on one worker, so these say
    // where ONE island's time went (searches lent to other threads are not in them).
    [System.ThreadStatic] private static long[]? t_count, t_maxed, t_ticks;

    /// <summary>Start this thread's island tally.</summary>
    public static void ResetThread()
    {
        t_count = new long[Names.Length]; t_maxed = new long[Names.Length]; t_ticks = new long[Names.Length];
    }

    /// <summary>This thread's island so far: seconds/searches (spent their budget) per kind.</summary>
    public static string BriefThread()
    {
        if (t_count == null) return "nothing timed";
        var parts = new List<string>();
        for (int k = 0; k < Names.Length; k++)
            if (t_count[k] > 0) parts.Add($"{Names[k]} {Seconds(t_ticks![k]):F0}s/{t_count[k]} ({t_maxed![k]} spent the budget)");
        return parts.Count == 0 ? "nothing timed" : string.Join(", ", parts);
    }

    public static void Record(int kind, bool found, bool maxed, long iterations, long beganTimestamp)
    {
        long ticks = System.Diagnostics.Stopwatch.GetTimestamp() - beganTimestamp;
        if (t_count != null)
        {
            t_count[kind]++; t_ticks![kind] += ticks;
            if (maxed) t_maxed![kind]++;
        }
        System.Threading.Interlocked.Increment(ref s_count[kind]);
        if (found) System.Threading.Interlocked.Increment(ref s_found[kind]);
        System.Threading.Interlocked.Add(ref s_iterations[kind], iterations);
        System.Threading.Interlocked.Add(ref s_ticks[kind], ticks);
        if (!maxed) return;
        System.Threading.Interlocked.Increment(ref s_maxed[kind]);
        System.Threading.Interlocked.Add(ref s_maxedTicks[kind], ticks);
    }

    private static double Seconds(long ticks) => ticks / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Thread time so far by kind, on one line: for the per-island
    /// progress lines, so a run still loading says where its time goes.</summary>
    public static string Brief()
    {
        var parts = new List<string>();
        for (int k = 0; k < Names.Length; k++)
            if (s_count[k] > 0) parts.Add($"{Names[k]} {Seconds(System.Threading.Interlocked.Read(ref s_ticks[k])):F0}s/{System.Threading.Interlocked.Read(ref s_count[k])}");
        return parts.Count == 0 ? "nothing timed" : string.Join(", ", parts);
    }

    /// <summary>One line per kind that ran.</summary>
    public static IEnumerable<string> Describe()
    {
        for (int k = 0; k < Names.Length; k++)
        {
            if (s_count[k] == 0) continue;
            yield return $"{Names[k]}: {s_count[k]}, {s_found[k]} succeeded, {s_maxed[k]} spent the whole budget " +
                         $"({Seconds(s_maxedTicks[k]):F0} s of {Seconds(s_ticks[k]):F0} s summed over threads), " +
                         $"{s_iterations[k] / (double)s_count[k]:F0} iterations each";
        }
    }
}
