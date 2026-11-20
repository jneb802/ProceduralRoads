namespace ProceduralRoads;

/// <summary>
/// How a generation spends memory: the one owner of the memory-for-speed trade (docs/OWNERS.md), set from the
/// Roads.LowMemoryGeneration setting. Changes apply to the next generation; a change during a generation waits for it to end. Off, the
/// default, a generation keeps what it may need again to go faster; on, it keeps less and works more. Every cache this
/// governs answers exactly as the work it saves would, so the network is the same either way: only time and memory differ.
/// What it governs, each asking here:
/// <list type="bullet">
/// <item><see cref="KeepsNodes"/>: a staircase search's pieces taken from this thread's pool and given back as the layout
/// ends (StairLayout.NodePool, up to <see cref="StairLayout.PooledNodes"/> a thread), not made afresh.</item>
/// <item><see cref="KeepsSearchSets"/>: a staircase search's open queue and seen states kept a thread between searches
/// (StairLayout.Search), not made for each search.</item>
/// <item><see cref="SharesTerrain"/>: the pathfinders' shared grid of search-cell terrain (SharedTerrain), not only each
/// pathfinder's own fixed memo.</item>
/// <item><see cref="KeepsLattice"/>: the whole-metre planning ground (RoadStairRuns.GroundAt) kept a thread for the whole
/// generation, not only for the road being made.</item>
/// <item><see cref="LaysAhead"/>: the last island's stair layouts laid ahead on spare workers (RoadStairRuns.LayoutsAhead),
/// whose threads each keep their own of the above.</item>
/// <item><see cref="KeepsCrossingFacts"/>: a pathfinder's river-crossing terrain facts per cell and direction
/// (RoadPathfinder.CrossingTerrainOf), not worked out again at each ask.</item>
/// <item><see cref="KeepsSupportAnswers"/>: what holds up each piece a road's stretch's staircase layouts try
/// (StairSupport.Answers, StairSupport.Choose's answers on the stretch's ground), kept for the stretch, not chosen again
/// by each layout of it; a way onto a placed location's landing keeps its own for every leg it lays.</item>
/// <item><see cref="KeepsSearches"/>: an island's route searches remembered on its network as it stands
/// (RoadPathfinder.RemembersSearches), a search asked again answered as it was made, not searched again.</item>
/// <item><see cref="KeepsPlans"/>: an island's pieces of road planned on its network as it stands (RoadSpatialGrid.RemembersPlans),
/// a piece asked again answered as it was planned, not planned again; and with them its structures' ends met
/// (StructureEnds.MeetEndsRemembered).</item>
/// <item><see cref="KeepsCornerBiomes"/>: the world's heightmap corner biomes held while the workers run
/// (BiomeBlendedHeight.CornerGrid), not read from the world for each height.</item>
/// <item><see cref="KeepsHeights"/>: each thread's memo of the natural ground heights it read, by the exact point
/// (BiomeBlendedHeight.GetBlendedHeight), not read from the world again.</item>
/// <item><see cref="KeepsRiverIndex"/>: each river cell's points filed by sub-cell for the river reads during generation
/// (RiverWeightReads.CellIndex), not every point of the cell read at each ask.</item>
/// <item><see cref="KeepsMoveTerrain"/>: a pathfinder's meander field at each step's middle, a row a cell
/// (RoadPathfinder.MeanderCost), not worked out again at each ask.</item>
/// </list>
/// Not governed: the game's river weight read during generation (RiverWeightReads) keeps one river cell a thread, which
/// saves no memory to drop, and without it the reads fall back to the game's shared lock.
/// A new cache kept for speed asks here too. Not governed: the caches whose hits are decisions (the stair plan cache,
/// RoadStairRuns.PlanCache, whose layouts the accepted roads edit; the island's pair prices; the entry-stair budget).
/// </summary>
internal static class GenerationMemory
{
    /// <summary>The setting's default: a generation spends memory for speed.</summary>
    internal const bool DefaultLow = false;

    /// <summary>The trade as set: on keeps less (each governed cache off or smaller), off keeps it all.</summary>
    internal static bool Low
    {
        get => s_low;
        set
        {
            s_low = value;
            KeepsNodes = KeepsSearchSets = SharesTerrain = KeepsLattice = LaysAhead = KeepsCrossingFacts = KeepsSupportAnswers = KeepsSearches = KeepsPlans = KeepsCornerBiomes = KeepsHeights = KeepsRiverIndex = KeepsMoveTerrain = !value;
        }
    }
    private static bool s_low = DefaultLow;

    /// <summary>What a generation's log says it keeps (RoadNetworkGenerator.GenerationWork.Compute): the setting and each switch.</summary>
    internal static string Describe() =>
        $"[ROADS] memory: LowMemoryGeneration {(Low ? "on" : "off")} (nodes {On(KeepsNodes)}, search sets {On(KeepsSearchSets)}, " +
        $"shared terrain {On(SharesTerrain)}, lattice {On(KeepsLattice)}, laid ahead {On(LaysAhead)}, crossing facts {On(KeepsCrossingFacts)}, " +
        $"support answers {On(KeepsSupportAnswers)}, searches {On(KeepsSearches)}, plans {On(KeepsPlans)}, corner biomes {On(KeepsCornerBiomes)}, heights {On(KeepsHeights)}, river index {On(KeepsRiverIndex)}, move terrain {On(KeepsMoveTerrain)})";

    private static string On(bool kept) => kept ? "kept" : "not kept";

    // One switch for each governed cache, all set by Low (never set apart but to measure one cache's memory alone).
    internal static bool KeepsNodes = !DefaultLow;
    internal static bool KeepsSearchSets = !DefaultLow;
    internal static bool SharesTerrain = !DefaultLow;
    internal static bool KeepsLattice = !DefaultLow;
    internal static bool LaysAhead = !DefaultLow;
    internal static bool KeepsCrossingFacts = !DefaultLow;
    internal static bool KeepsSupportAnswers = !DefaultLow;
    internal static bool KeepsSearches = !DefaultLow;
    internal static bool KeepsPlans = !DefaultLow;
    internal static bool KeepsCornerBiomes = !DefaultLow;
    internal static bool KeepsHeights = !DefaultLow;
    internal static bool KeepsRiverIndex = !DefaultLow;
    internal static bool KeepsMoveTerrain = !DefaultLow;
}
