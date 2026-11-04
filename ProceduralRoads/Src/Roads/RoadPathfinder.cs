using System.Linq;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// A* pathfinding for road generation with 16-direction movement, slope-based cost, and river avoidance.
/// </summary>
public class RoadPathfinder
{
    private static ManualLogSource Log => ProceduralRoadsPlugin.ProceduralRoadsLogger;
    
    public const float CellSize = RoadConstants.PathfindingCellSize;
    public static int MaxIterations = RoadConstants.PathfindingMaxIterations;

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
    public float SwampShallowWaterPenalty = RoadConstants.DefaultSwampShallowWaterPenalty;

    /// <summary>
    /// Whether a road may jump a knee-deep river, and wade swamp shallows.
    /// On: a road that meets a river it can ford does so rather than going
    /// around. Held per instance so a test can pathfind without it.
    /// </summary>
    public bool Fords = true;

    /// <summary>
    /// Whether a road may jump a river too long or too deep to ford, on a
    /// bridge. The cost levers are "Bridges/CostFixed" and
    /// "Bridges/CostPerMeter"; how dear a bridge is decides how often one
    /// appears, which is the lever a player actually wants.
    /// </summary>
    public bool Bridges = true;

    public static float ConfiguredBridgeCostFixed = RoadConstants.DefaultBridgeCostFixed;
    public static float ConfiguredBridgeCostPerMeter = RoadConstants.DefaultBridgeCostPerMeter;

    public float BridgeCostFixed = ConfiguredBridgeCostFixed;
    public float BridgeCostPerMeter = ConfiguredBridgeCostPerMeter;

    /// <summary>
    /// Whether a road may cross a dry Mistlands valley at the high level on a Dvergr viaduct
    /// (DvergrViaduct) instead of a staircase down into it and another out. From a
    /// Mistlands cell at a valley's rim the search looks straight on, in the eight principal
    /// directions, for ground back at the rim's height within DvergrViaduct.MaxValleySpan, and
    /// buys the jump where DvergrViaduct.ValleyCrossing builds it, at DvergrViaduct.Price: dearer
    /// the lower its piers and the more end stairs it has. The road is put
    /// through each end's stair foot after the search (StructureEnds.MeetTheRoad). Needs
    /// <see cref="Bridges"/>. On by default; PROCEDURALROADS_VIADUCTS=off for a control run.
    /// </summary>
    public bool Viaducts = ConfiguredViaducts;
    public static bool ConfiguredViaducts = DebugSwitches.Flag("VIADUCTS", true);
    public float ViaductCostFixed = RoadConstants.DefaultViaductCostFixed;
    public float ViaductCostPerMeter = RoadConstants.DefaultViaductCostPerMeter;
    /// <summary>Per metre of a viaduct's end stairs, bank to deck, both ends together (DvergrViaduct.Price).</summary>
    public float ViaductEndStairCostPerMeter = RoadConstants.DefaultViaductEndStairCostPerMeter;
    /// <summary>A valley starts where the next cell is at least this much lower: the rim.</summary>
    public const float ViaductRimDrop = 2f;
    /// <summary>The viaduct jumps of the last path found, bank cell to bank cell, in path order.</summary>
    public List<(Vector2 from, Vector2 to)> LastViaductJumps { get; } = new();

    /// <summary>
    /// Whether a road may climb ground too steep for it on a staircase. A step
    /// steeper than <see cref="StairMinGrade"/> (and no steeper than
    /// <see cref="StairMaxGrade"/>) may be bought as a stair step instead of
    /// road: <see cref="StairCostPerMeterClimbed"/> for every metre it climbs,
    /// and <see cref="StairCostFixed"/> where a staircase starts, varied a
    /// little from site to site so a world gets long traverses, staircases and
    /// both. The steps bought are kept in
    /// <see cref="LastStairSteps"/>; the generator lays them with StairLayout.
    /// The levers are "Stairs/Enabled", "Stairs/CostFixed" and "Stairs/CostPerMeterClimbed".
    /// </summary>
    public bool Stairs = ConfiguredStairs;
    public static bool ConfiguredStairs = true;
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
    /// <summary>Steeper than this between two cells no staircase is offered where its stairs
    /// stand on columns (wood: Meadows and the Black Forest's wood): the layout climbs a 1:1 face with
    /// switchbacks, not a cliff. Where they can be held out from the face (stone, black marble:
    /// StairKit.Cantilever), up to <see cref="StairCantileverMaxGrade"/>.</summary>
    public const float StairMaxGrade = 1f;
    /// <summary>
    /// Steeper than <see cref="StairMaxGrade"/>, a staircase whose stairs can be held out from the
    /// face on cantilevers (a wall almost a sheer cliff can still be built on) is offered up to this grade between two
    /// cells, its price per metre climbed rising from 1 times at <see cref="StairMaxGrade"/> to
    /// <see cref="SteepStairPriceMax"/> times here (over a certain grade it is more
    /// expensive but still possible: cheaper lesser grades first, then up to a maximum).
    /// Between two 8 m cells 2 is 16 m of climb: the flights cross the face, turning on the ground
    /// gentler round it (StairLayout's tree-well pricing).
    /// 3 (24 m) bought steps straight up faces the layout then could not lay (through a place), and
    /// the retry with those cells closed found no way: Hildir's cave (-2304,1536) lost its 106 m
    /// staircase. At 2 it keeps it, and reaches one more place on RoadSeedA-E than 1:1 does.
    /// </summary>
    public const float StairCantileverMaxGrade = 2f;
    public const float SteepStairPriceMax = 2.5f;

    /// <summary>The steepest step a staircase is offered on at <paramref name="at"/> in <paramref name="biome"/>: what the kit
    /// built there can hold. One owner, the kit (<see cref="StairKit.ForSite"/>): stone and black marble
    /// cantilever, so up to <see cref="StairCantileverMaxGrade"/>; wood (the Meadows, the Black Forest's wood share) stands on
    /// columns, so <see cref="StairMaxGrade"/>. The Black Forest's kit is drawn per 4 m cell, so a step there is offered the
    /// steeper grade only where stone would be built.</summary>
    internal float StairGradeLimit(Heightmap.Biome biome, Vector2 at) =>
        StairKit.ForSite(biome, at, m_worldGen.GetSeed()).Cantilever != null ? StairCantileverMaxGrade : StairMaxGrade;

    /// <summary>Times the price per metre climbed for a stair step this steep.</summary>
    public static float SteepStairFactor(float slope) =>
        Mathf.Lerp(1f, SteepStairPriceMax, Mathf.Clamp01((slope - StairMaxGrade) / (StairCantileverMaxGrade - StairMaxGrade)));

    /// <summary>The site wobble on <see cref="StairCostFixed"/>: from (1 - this) to (1 + this) times it.</summary>
    public const float StairCostWobble = 0.3f;

    /// <summary>The stair steps of the last path found, from cell centre to cell centre.</summary>
    public List<(Vector2 from, Vector2 to)> LastStairSteps { get; } = new();

    /// <summary>Cells closed to stairs for the road being built: a staircase the search bought
    /// there could not be laid, so the road is searched again with stairs open everywhere else
    /// (RoadNetworkGenerator.GenerateRoad). Cleared, with <see cref="m_stairOpen"/>, when that road is done.</summary>
    private readonly HashSet<Vector2i> m_noStairs = new(RoadGridComparer.Instance);
    public int NoStairCells => m_noStairs.Count;
    public void ClearNoStairs() { m_noStairs.Clear(); m_stairOpen.Clear(); m_noViaducts.Clear(); }

    /// <summary>Viaduct jumps (from cell, to cell, either way) the road being built may not buy: one whose end the road as
    /// planned did not meet (StructureEnds; RoadNetworkGenerator), searched again without it. Cleared with the stair closings
    /// when that road is done.</summary>
    private readonly HashSet<(Vector2i, Vector2i)> m_noViaducts = new();
    public int NoViaducts => m_noViaducts.Count / 2;
    public void BanViaduct(Vector2 from, Vector2 to)
    {
        Vector2i a = WorldToGrid(from), b = WorldToGrid(to);
        m_noViaducts.Add((a, b));
        m_noViaducts.Add((b, a));
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
    /// <summary>Open the cells of these points, and <paramref name="reachCells"/> round each, to stairs.</summary>
    public void OpenToStairs(IEnumerable<Vector2> points, int reachCells) => MarkCells(m_stairOpen, points, reachCells);
    /// <summary>Close the cells of these points, and <paramref name="reachCells"/> round each, to stairs.</summary>
    public void CloseToStairs(IEnumerable<Vector2> points, int reachCells) => MarkCells(m_noStairs, points, reachCells);

    private void MarkCells(HashSet<Vector2i> cells, IEnumerable<Vector2> points, int reachCells)
    {
        foreach (Vector2 p in points)
        {
            Vector2i c = WorldToGrid(p);
            for (int dx = -reachCells; dx <= reachCells; dx++)
                for (int dz = -reachCells; dz <= reachCells; dz++)
                    cells.Add(new Vector2i(c.x + dx, c.y + dz));
        }
    }

    /// <summary>Ground a crossing may land on: the shallow-water line plus the bank clearance.</summary>
    public const float LandingFloor = RoadConstants.ShallowWaterHeight + RoadConstants.BankClearance;

    /// <summary>The floor for road ground in a biome: swamps wade down to DeepWaterHeight.</summary>
    public static float FloorFor(Heightmap.Biome biome) =>
        biome == Heightmap.Biome.Swamp ? RoadConstants.DeepWaterHeight : LandingFloor;

    /// <summary>
    /// The steepest ground a road may climb, as rise over run; 0 for no cap.
    /// A step over it is refused outright rather than priced, so the search
    /// has to cross the slope - which the 16 directions already allow - or
    /// fail and leave the destination unconnected. Every other steep case
    /// here is a large but finite price and so is never a refusal, which is
    /// why a destination on a cliff got the direct climb: it was the cheapest
    /// of the expensive lines. Held per instance so a test can pathfind
    /// without it. The lever is "Roads/MaxGrade".
    /// </summary>
    public float MaxGrade = RoadGrade.Configured;

    /// <summary>
    /// A step from existing road onto existing road costs this share of its
    /// price, so a later road merges onto a road already built and branches
    /// off where it must, instead of running beside it: a place-to-place
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

    private static readonly Vector2Int[] Directions = new Vector2Int[]
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
    public float SiteClearance = 4f;

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

    /// <summary>Set by FindPathToNetwork when the start was already within
    /// reach of a road, so a null result can be told apart from a failure.</summary>
    public bool AlreadyOnNetwork { get; private set; }

    public List<Vector2>? FindPath(Vector2 start, Vector2 end) => FindPath(start, end, null);

    public List<Vector2>? FindPath(Vector2 start, Vector2 end, int? iterationLimit)
    {
        List<Vector2>? path = FindPathCore(start, end, iterationLimit);
        FoldSqueezes();
        return path;
    }

    private List<Vector2>? FindPathCore(Vector2 start, Vector2 end, int? iterationLimit)
    {
        // The memo is NOT blanked here. It holds world-generator facts, and
        // the generator a pathfinder was built for does not change underneath
        // it: a pathfinder is built at the start of a generation and thrown
        // away at the end of it. Blanking between searches threw away answers
        // that were still true and bought nothing.
        m_searchStart = start;
        m_searchEnd = end;

        LastPathCost = 0;
        LastStairSteps.Clear();
        LastViaductJumps.Clear();
        Vector2i startGrid = WorldToGrid(start);
        Vector2i endGrid = WorldToGrid(end);

        if (startGrid == endGrid)
            return new List<Vector2> { start, end };

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

        openSet.Push((Heuristic(startGrid, endGrid), startGrid));
        gCosts[startGrid] = 0;

        int iterations = 0;

        while (openSet.TrimClosed(closedSet) && iterations < (iterationLimit ?? MaxIterations))
        {
            iterations++;

            var current = openSet.Pop();
            Vector2i currentPos = current.pos;

            if (currentPos == endGrid)
            {
                LastPathCost = gCosts[currentPos];
                return ReconstructPath(cameFrom, byStair, byViaduct, currentPos, start, end);
            }

            closedSet.Add(currentPos);
            // Neither changes while the cell's neighbours are relaxed: only other cells are written.
            float currentG = gCosts[currentPos];
            int arriving = byStair.TryGetValue(currentPos, out int stairHere) ? stairHere : 0;

            for (int i = 0; i < Directions.Length; i++)
            {
                Vector2i neighborPos = new Vector2i(currentPos.x + Directions[i].x, currentPos.y + Directions[i].y);

                if (closedSet.Contains(neighborPos))
                    continue;

                float moveCost = GetMoveCost(currentPos, neighborPos, i, arriving, out int stair);
                if (!float.IsPositiveInfinity(moveCost) && moveCost < RiverPenalty)
                    moveCost += TurnCost(cameFrom, currentPos, i);
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
                    if (!(Fords || Bridges) || !TryGetRiverCrossing(currentPos, Directions[i], out Vector2i landing, out float crossingCost))
                        continue;
                    if (closedSet.Contains(landing))
                        continue;
                    neighborPos = landing;
                    moveCost = crossingCost;
                    stair = 0;
                }

                float tentativeG = currentG + moveCost;

                if (!gCosts.TryGetValue(neighborPos, out float existingG) || tentativeG < existingG)
                {
                    cameFrom[neighborPos] = currentPos;
                    if (stair != 0) byStair[neighborPos] = stair; else byStair.Remove(neighborPos);
                    byViaduct.Remove(neighborPos);
                    gCosts[neighborPos] = tentativeG;
                    openSet.Push((tentativeG + Heuristic(neighborPos, endGrid), neighborPos));
                }
            }

            // Across a Mistlands valley at the rim's height, on a viaduct. Not from the start cell: the path begins at
            // the exact start, not that cell's centre, so the jump could not be found on it again.
            for (int d = 0; Viaducts && Bridges && currentPos != startGrid && d < ViaductDirections; d++)
            {
                if (!TryGetViaduct(currentPos, d, out Vector2i landing, out float jumpCost) || closedSet.Contains(landing)
                    || !ClearOfJumpsBefore(currentPos, landing, cameFrom, byViaduct))
                    continue;
                float tentativeG = currentG + jumpCost;
                if (!gCosts.TryGetValue(landing, out float existingG) || tentativeG < existingG)
                {
                    cameFrom[landing] = currentPos;
                    byStair.Remove(landing);
                    byViaduct.Add(landing);
                    gCosts[landing] = tentativeG;
                    openSet.Push((tentativeG + Heuristic(landing, endGrid), landing));
                }
            }
        }

        string reason = !openSet.TrimClosed(closedSet) ? "no reachable path" : "max iterations reached";
        // Not a fault (screening declines pairs; the totals are in the closing summary): debug level.
        Log.LogDebug($"Pathfinding failed: {reason} after {iterations} iterations");
        return null;
        }
        finally { ReturnSearchState(state); }
    }

    /// <summary>Search from a destination towards any existing road. Sampled hints
    /// guide the search; they do not promise a globally cheapest junction.
    /// <paramref name="served"/>: whether the place the search starts from is already served by the network
    /// (RoadNetworkGenerator.PlaceServed), the caller's own test of it, asked first in place of a junction at the first cell
    /// (AlreadyOnNetwork). Not served, a junction at the first cell is one the search finds like any other: a path from the
    /// start to it.</summary>
    public List<Vector2>? FindPathToNetwork(Vector2 start, float reach, IReadOnlyList<Vector2>? hints = null, System.Func<Vector2, bool>? targetAllowed = null,
        System.Func<bool>? served = null)
    {
        List<Vector2>? path = FindPathToNetworkCore(start, reach, hints, targetAllowed);
        FoldSqueezes();
        return path;
    }

    private List<Vector2>? FindPathToNetworkCore(Vector2 start, float reach, IReadOnlyList<Vector2>? hints, System.Func<Vector2, bool>? targetAllowed)
    {
        LastPathCost = 0;
        // Kept between searches, for the reason given in FindPath.
        m_searchStart = start;
        m_searchEnd = null;
        AlreadyOnNetwork = false;
        LastStairSteps.Clear();
        LastViaductJumps.Clear();
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
            Vector2 world = GridToWorld(cell);
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
        var grounds = state.Grounds;
        float Ground(Vector2i cell)
        {
            if (!grounds.TryGetValue(cell, out float h)) grounds[cell] = h = RoadSpatialGrid.JunctionGround(GridToWorld(cell), m_worldGen);
            return h;
        }
        bool Join(Vector2i cell, out Vector2 junction)
        {
            Vector2 at = GridToWorld(cell);
            if (!RoadSpatialGrid.TryGetRoadWithin(at, reach, out junction, targetAllowed)) return false;
            return JunctionStep <= 0f
                || RoadSpatialGrid.TryGetRoadAtGradeWithin(at, reach, Ground(cell), JunctionStep, out junction, targetAllowed);
        }
        bool ArrivesLevel(Vector2 junction, Vector2i before) =>
            !RoadSpatialGrid.TryGetRoadHeightWithin(junction, 1f, out float surface)
            || Mathf.Abs(surface - Ground(before)) <= 2f * JunctionStep;

        if (Join(startGrid, out _))
        {
            // Already within reach. This is not a failure, and returning null
            // for it made the caller build a road to a place the network
            // already passes within a cell of: the caller's own check uses the
            // exact point, this one uses the cell centre, up to 5.66 m away.
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
        // Whether the path to `from` already passes through `cell`: arriving at it again would be a loop.
        bool OnPathTo(Vector2i from, Vector2i cell)
        {
            for (Vector2i c = from; ; c = cameFrom[c])
            {
                if (RoadGridComparer.Instance.Equals(c, cell)) return true;
                if (!cameFrom.ContainsKey(c)) return false;
            }
        }

        List<Vector2> TakeGoal()
        {
            LastPathCost = goalCost;
            cameFrom[goalKey.pos] = goalBefore;
            if (goalStair != 0) byStair[goalKey.pos] = goalStair; else byStair.Remove(goalKey.pos);
            byViaduct.Remove(goalKey.pos); // the goal is reached by a step or a crossing, never by a viaduct's jump
            return ReconstructPath(cameFrom, byStair, byViaduct, goalKey.pos, start, goalJunction);
        }

        while (iterations < MaxIterations)
        {
            bool open = openSet.TrimClosed(closedSet);
            if (haveGoal && (!open || OpenHeap.Compare(goalKey, openSet.Min) <= 0))
                return TakeGoal();
            if (openSet.Count == 0) break;
            iterations++;
            var current = openSet.Min;
            openSet.Remove(current);
            Vector2i currentPos = current.pos;

            // The start itself at the network (no arrival to judge), or the plain rule without a junction step.
            if ((JunctionStep <= 0f || !cameFrom.ContainsKey(currentPos)) && JoinAt(currentPos) is Vector2 junction)
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
                Vector2i neighborPos = new Vector2i(currentPos.x + Directions[i].x, currentPos.y + Directions[i].y);
                // A closed cell is still a junction for a level arrival from here.
                bool closed = closedSet.Contains(neighborPos);
                if (closed && (JunctionStep <= 0f || JoinAt(neighborPos) == null))
                    continue;

                float moveCost = GetMoveCost(currentPos, neighborPos, i, arriving, out int stair);
                if (!float.IsPositiveInfinity(moveCost) && moveCost < RiverPenalty)
                    moveCost += TurnCost(cameFrom, currentPos, i);
                if (moveCost >= RiverPenalty)
                {
                    if (!(Fords || Bridges))
                    {
                        continue;
                    }
                    if (!TryGetRiverCrossing(currentPos, Directions[i], out Vector2i landing, out float crossingCost))
                        continue;
                    closed = closedSet.Contains(landing);
                    if (closed && (JunctionStep <= 0f || JoinAt(landing) == null))
                    {
                        continue;
                    }
                    neighborPos = landing;
                    moveCost = crossingCost;
                    stair = 0;
                }

                float tentativeG = currentG + moveCost;
                // Arriving level at a junction: the search may end there (but not back along its own path).
                if (JunctionStep > 0f && JoinAt(neighborPos) is Vector2 meets)
                {
                    float goalPriority = tentativeG + NetworkHeuristic(neighborPos);
                    if ((!haveGoal || goalPriority < goalKey.priority) && ArrivesLevel(meets, currentPos) && !OnPathTo(currentPos, neighborPos))
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
                if (!gCosts.TryGetValue(neighborPos, out float existingG) || tentativeG < existingG)
                {
                    cameFrom[neighborPos] = currentPos;
                    if (stair != 0) byStair[neighborPos] = stair; else byStair.Remove(neighborPos);
                    byViaduct.Remove(neighborPos);
                    gCosts[neighborPos] = tentativeG;
                    openSet.Push((tentativeG + NetworkHeuristic(neighborPos), neighborPos));
                }
            }

            for (int d = 0; Viaducts && Bridges && currentPos != startGrid && d < ViaductDirections; d++)
            {
                if (!TryGetViaduct(currentPos, d, out Vector2i landing, out float jumpCost) || closedSet.Contains(landing)
                    || !ClearOfJumpsBefore(currentPos, landing, cameFrom, byViaduct))
                    continue;
                float tentativeG = currentG + jumpCost;
                if (!gCosts.TryGetValue(landing, out float existingG) || tentativeG < existingG)
                {
                    cameFrom[landing] = currentPos;
                    byStair.Remove(landing);
                    byViaduct.Add(landing);
                    gCosts[landing] = tentativeG;
                    openSet.Push((tentativeG + NetworkHeuristic(landing), landing));
                }
            }
        }

        // The budget spent with a level junction already found: the best one found, not none.
        if (haveGoal) return TakeGoal();
        LastPathCost = 0f;
        return null;
        }
        finally { ReturnSearchState(state); }
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

        public void Clear()
        {
            Open.Clear(); GCosts.Clear(); CameFrom.Clear(); Closed.Clear(); ByStair.Clear(); ByViaduct.Clear();
            Grounds.Clear(); Joins.Clear(); Heuristics.Clear();
        }
    }

    private SearchState? m_spareState;

    private SearchState RentSearchState()
    {
        SearchState state = System.Threading.Interlocked.Exchange(ref m_spareState, null) ?? new SearchState();
        state.Clear();
        m_fromRoadKnown = false;
        return state;
    }

    private void ReturnSearchState(SearchState state) => System.Threading.Volatile.Write(ref m_spareState, state);

    private Vector2i WorldToGrid(Vector2 world)
    {
        return new Vector2i(Mathf.RoundToInt(world.x / CellSize), Mathf.RoundToInt(world.y / CellSize));
    }

    private Vector2 GridToWorld(Vector2i grid)
    {
        return new Vector2(grid.x * CellSize, grid.y * CellSize);
    }

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
                if (Vector2.Distance(GridToWorld(cell), world) <= radius + CellSize * 0.5f) m_earthworkPenalty.Add(cell);
            }
    }
    internal int EarthworkPenaltyCells => m_earthworkPenalty.Count;
    internal int PenalizedWaypoints(List<Vector2> path) { int n = 0; foreach (var p in path) if (m_earthworkPenalty.Contains(WorldToGrid(p))) n++; return n; }

    /// <summary>
    /// On gentle ground, a smooth low-frequency cost field adds up to this
    /// share of the base step price, so long flat runs bend instead of running
    /// dead straight. It fades out by <see cref="MeanderFlat"/> so climbs,
    /// contouring and switchbacks are priced as before. It only adds, so the
    /// heuristic's BaseCost-per-metre bound still holds. 0 is off.
    /// </summary>
    public float Meander = DefaultMeander;
    /// <summary>Settable for tests; 0 is no meander.</summary>
    internal static float DefaultMeander = 3f;
    /// <summary>Slope at which the meander field has faded out. Validation switch MEANDER_FLAT.</summary>
    public static readonly float MeanderFlat = DebugSwitches.Number("MEANDER_FLAT", 0.12f, 0.02f, 1f);
    internal float MeanderCost(Vector2 from, Vector2 to, Vector2i toCell, float slope, float dist)
    {
        if (slope >= MeanderFlat || Meander <= 0f) return 0f;
        float fade = 1f - slope / MeanderFlat;
        var mid = (from + to) * 0.5f;
        return BaseCost * dist * Meander * fade * MeanderBiome(m_terrain.Biome(toCell)) * MeanderField(mid.x, mid.y);
    }

    /// <summary>How much a biome's roads wander: open country most, none where
    /// the ground already bends every road.</summary>
    internal static float MeanderBiome(Heightmap.Biome biome) => biome switch
    {
        Heightmap.Biome.Meadows => 1f,
        Heightmap.Biome.Plains => 1f,
        Heightmap.Biome.BlackForest => 0.7f,
        Heightmap.Biome.Swamp => 0.5f,
        Heightmap.Biome.AshLands => 0.5f,
        Heightmap.Biome.DeepNorth => 0.5f,
        _ => 0f,   // Mountain, Mistlands, Ocean
    };

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

    private static float Lattice(int x, int y)
    {
        unchecked
        {
            uint h = (uint)x * 374761393u + (uint)y * 668265263u;
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (float)0xFFFFFF;
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
        if (!cameFrom.TryGetValue(at, out Vector2i from)) return 0f;
        Vector2 incoming = new Vector2(at.x - from.x, at.y - from.y);
        if (incoming.sqrMagnitude < 1e-6f) return 0f;
        Vector2 outgoing = new Vector2(Directions[direction].x, Directions[direction].y);
        float cos = Mathf.Clamp((incoming.x * outgoing.x + incoming.y * outgoing.y) /
                                (Mathf.Sqrt(incoming.sqrMagnitude) * Mathf.Sqrt(outgoing.sqrMagnitude)), -1f, 1f);
        float angle = (float)System.Math.Acos(cos);
        if (TurnLimitDegrees > 0f && angle * 57.29578f > TurnLimitDegrees + 0.01f) return Impassable;
        return TurnWeight * angle;
    }

    /// <summary>
    /// The narrowest gap, in metres, left between a place's footprint and the
    /// water for a road to pass through. A road forced along a strip of shore
    /// beside a place comes out strange: squeezed against the footprint on one
    /// side and the water on the other, tilted down the bank or run out on a
    /// causeway. Such ground is not a road at all, for any place, the road's
    /// own destination included; the search goes round, or the place goes
    /// unserved. 12 m is the site clearance (4 m to the road's centre), half a
    /// 4 m road and 6 m of bank. Settable for tests; 0 is off.
    /// </summary>
    internal static float SqueezeCorridor = 12f;

    /// <summary>Whether <paramref name="p"/> lies between a place's footprint
    /// and water less than <see cref="SqueezeCorridor"/> apart. Inside a
    /// footprint it is not: the search's own ends reach their centres there,
    /// and every other footprint is refused anyway.</summary>
    internal bool SiteShoreSqueeze(Vector2 p)
    {
        if (SqueezeCorridor <= 0f) return false;
        float gap = RoadSiteProtection.EdgeDistance(p, SqueezeCorridor);
        if (gap < 0f || gap >= SqueezeCorridor) return false;
        float reach = SqueezeCorridor - gap;
        for (int i = 0; i < 8; i++)
        {
            float a = i * Mathf.PI / 4f;
            var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            for (float r = reach * 0.5f; r <= reach + 0.01f; r += reach * 0.5f)
                if (Wet(p + dir * r)) return true;
        }
        return false;
    }

    /// <summary>Whether a crossing's line runs over water within <see cref="SqueezeCorridor"/> of a
    /// place's footprint (sampled every 4 m, 8 m clear of each bank).</summary>
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
            float gap = RoadSiteProtection.EdgeDistance(p, SqueezeCorridor);
            if (gap >= 0f && gap < SqueezeCorridor) { at = p; return true; }
        }
        return false;
    }

    /// <summary>The squeeze, once per cell for this pathfinder's life: the
    /// places and the ground do not change during a generation, and the test
    /// samples the world sixteen times.</summary>
    private readonly Dictionary<Vector2i, bool> m_squeezed = new(RoadGridComparer.Instance);
    private bool SqueezedCell(Vector2i cell)
    {
        if (SqueezeCorridor <= 0f) return false;
        if (!m_squeezed.TryGetValue(cell, out bool squeezed))
            m_squeezed[cell] = squeezed = SiteShoreSqueeze(GridToWorld(cell));
        return squeezed;
    }

    /// <summary>Where the squeeze refused something this generation, by the
    /// place it squeezed against: (centre x, centre z) -> steps, landings.</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<(int x, int z), (int steps, int landings)> SqueezeHits = new();
    /// <summary>This search's squeezes, folded into <see cref="SqueezeHits"/> once when it ends:
    /// recorded there per step, every worker took a lock (and allocated) on each step into a squeezed cell.</summary>
    private readonly Dictionary<(int x, int z), (int steps, int landings)> m_squeezeHits = new();
    private void RecordSqueeze(Vector2 p, bool landing)
    {
        if (!RoadSiteProtection.NearestFootprint(p, SqueezeCorridor, out var f)) return;
        var key = (Mathf.RoundToInt(f.Centre.x), Mathf.RoundToInt(f.Centre.y));
        m_squeezeHits.TryGetValue(key, out var n);
        m_squeezeHits[key] = landing ? (n.steps, n.landings + 1) : (n.steps + 1, n.landings);
    }
    private void FoldSqueezes()
    {
        if (m_squeezeHits.Count == 0) return;
        foreach (var hit in m_squeezeHits)
            SqueezeHits.AddOrUpdate(hit.Key, hit.Value, (_, n) => (n.steps + hit.Value.steps, n.landings + hit.Value.landings));
        m_squeezeHits.Clear();
    }

    /// <summary>Water the search would not walk: a river, or ground under the
    /// shallows (swamp shallows are waded, so there the deep line).</summary>
    private bool Wet(Vector2 p)
    {
        m_worldGen.GetRiverWeight(p.x, p.y, out float river, out _);
        if (river > RoadConstants.RiverImpassableThreshold) return true;
        float h = m_worldGen.GetHeight(p.x, p.y);
        return h < (m_worldGen.GetBiome(p.x, p.y) == Heightmap.Biome.Swamp ? RoadConstants.DeepWaterHeight : RoadConstants.ShallowWaterHeight);
    }

    /// <summary>The road's price for a step, or the staircase's where that is
    /// cheaper (<paramref name="stair"/> is then +1 for a step up, -1 down);
    /// <paramref name="arriving"/>: the way the staircase the search arrived at
    /// <paramref name="from"/> on was going, or 0.</summary>
    private float GetMoveCost(Vector2i from, Vector2i to, int directionIndex, int arriving, out int stair)
    {
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
        Vector2 fromWorld = GridToWorld(from);
        Vector2 toWorld = GridToWorld(to);
        if (RoadSiteProtection.BlocksSegment(fromWorld, toWorld, SiteClearance, m_searchStart, m_searchEnd))
            return Impassable;

        float dist = DirectionCosts[directionIndex] * CellSize;
        float h1 = m_terrain.Height(from);
        float h2 = m_terrain.Height(to);
        float slope = Mathf.Abs(h2 - h1) / dist;

        float riverWeight = m_terrain.River(to);
        if (riverWeight > RoadConstants.RiverImpassableThreshold)
            return RiverPenalty;

        float biomeHeight = h2;
        if (biomeHeight < RoadConstants.DeepWaterHeight)
            return WaterPenalty * 2f;
        // With fords on, swamp shallows are waded at a price.
        float wadeCost = 0f;
        if (biomeHeight < RoadConstants.ShallowWaterHeight)
        {
            if (!Fords || m_worldGen.GetBiome(toWorld.x, toWorld.y) != Heightmap.Biome.Swamp)
                return WaterPenalty;
            wadeCost = SwampShallowWaterPenalty;
        }

        if (SqueezedCell(to))
        {
            RecordSqueeze(GridToWorld(to), landing: false);
            return Impassable;
        }

        // Steep dry ground may be climbed on a staircase. Water, rivers, sites
        // and squeezes were refused above, for stairs as for road. Stairs go
        // down as well as up where it makes sense for path finding, and over a crest the climb
        // runs on into the descent at no new start price; the generator lays
        // the two as two staircases. A staircase an earlier road laid here is
        // walked on at the reuse share, like road.
        climb = h2 >= h1 ? 1 : -1;
        if (Stairs && wadeCost == 0f && riverWeight <= 0f && slope >= StairMinGrade && slope <= StairGradeLimit(m_terrain.Biome(to), GridToWorld(to))
            && (m_noStairs.Count == 0 || !(m_noStairs.Contains(from) || m_noStairs.Contains(to))))
        {
            bool laid = StairStructures.OnLaidStair(from.x, from.y) && StairStructures.OnLaidStair(to.x, to.y);
            float biomePrice = StairBiomeFactor(m_terrain.Biome(to));
            bool open = OpenToStairsAt(from, to);
            stairCost = BaseCost * dist + biomePrice * (StairCostPerMeterClimbed * SteepStairFactor(slope) * Mathf.Abs(h2 - h1)
                + (arriving != 0 || laid || open ? 0f : StairEntryCost(from)));
            if (laid) stairCost *= ReuseFactor;
            if (open) stairCost *= OpenStairPrice;
            if (m_earthworkPenalty.Count > 0 && m_earthworkPenalty.Contains(to)) stairCost += EarthworkPenalty;
        }

        // A viaduct laid this generation stands over this cell: no road under its deck or through
        // its piers; a road uses it by its own jump, bank to bank.
        if (DvergrViaduct.Standing(to.x, to.y))
            return Impassable;

        // A laid staircase stands here: no road through it, only up or down it.
        // Roads built after one ran under and across it, and their earthworks
        // undermined its columns (broken stairs, overlapping roads).
        if (StairStructures.Standing(to.x, to.y))
        {
            bool walking = StairStructures.OnLaidStair(from.x, from.y) && StairStructures.OnLaidStair(to.x, to.y);
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

        Heightmap.Biome biome = m_terrain.Biome(to);
        if (biome == Heightmap.Biome.Mountain && slope > RoadConstants.MountainSlopeThreshold)
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

    /// <summary>
    /// How dear a staircase is in a biome, times the stair prices. Stairs are a
    /// style of road, wanted more in the mountains than elsewhere, hence a cost per
    /// biome; the Mistlands' cliffs get short runs.
    /// </summary>
    internal static float StairBiomeFactor(Heightmap.Biome biome) => biome switch
    {
        // Mountains are cheaper: stairs are wanted there most.
        Heightmap.Biome.Mountain => 0.5f,
        Heightmap.Biome.DeepNorth => 0.5f,
        Heightmap.Biome.Mistlands => 1f,
        Heightmap.Biome.BlackForest => 2f,
        Heightmap.Biome.AshLands => 2f,
        Heightmap.Biome.Meadows => 2f,
        Heightmap.Biome.Plains => 2f,
        Heightmap.Biome.Swamp => 2f,
        _ => 2f,
    };

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

    /// <summary>The principal directions (the first eight of <see cref="Directions"/>): a viaduct
    /// runs on a heading the hammer can build, and the grid's diagonals are such headings.</summary>
    private const int ViaductDirections = 8;

    /// <summary>What the terrain says of a viaduct from a cell in a direction: where it lands and
    /// its price, or null. Terrain only, so it holds for this pathfinder's life.</summary>
    private readonly Dictionary<(int x, int z, int d), (Vector2i landing, float cost)?> m_viaducts = new();

    /// <summary>
    /// A viaduct from <paramref name="from"/> straight on in principal direction
    /// <paramref name="d"/> across a Mistlands valley to the first cell back at the rim's height
    /// (within <see cref="RoadConstants.MaxBridgeBankDelta"/>), where
    /// <see cref="DvergrViaduct.ValleyCrossing"/> builds one: no water or river under it, a deck
    /// 6 m or more over the valley's floor, piers under the ceiling. Not over a place, another
    /// road or a staircase: roads do not cross. A viaduct both of whose banks are road already
    /// is shared, at the bridges' discount.
    /// </summary>
    private bool TryGetViaduct(Vector2i from, int d, out Vector2i landing, out float cost)
    {
        landing = from;
        cost = 0f;
        var key = (from.x, from.y, d);
        if (!m_viaducts.TryGetValue(key, out var found))
        {
            found = ViaductOverTerrain(from, Directions[d]);
            m_viaducts[key] = found;
        }
        if (!found.HasValue) return false;
        (Vector2i to, float price) = found.Value;
        if (m_noViaducts.Count > 0 && m_noViaducts.Contains((from, to))) return false;
        Vector2 a = GridToWorld(from), b = GridToWorld(to);
        // Clear of the viaducts other roads laid (DvergrViaduct.TooClose, the one owner): never back to back, nor in one's
        // deck or approach. A shared viaduct (both banks the same) is the same structure.
        if (DvergrViaduct.IsTooClose(a, b)) return false;
        if (RoadSiteProtection.BlocksSegment(a, b, SiteClearance, m_searchStart, m_searchEnd)) return false;
        // Nothing of the network under the deck: another road, a staircase or another viaduct.
        float length = Vector2.Distance(a, b);
        for (float s = CellSize * 0.5f; s <= length - CellSize * 0.5f; s += 2f)
        {
            Vector2 p = Vector2.Lerp(a, b, s / length);
            if (OnExistingRoad(p)) return false;
            Vector2i cell = WorldToGrid(p);
            if (cell == from || cell == to) continue;
            if (StairStructures.Standing(cell.x, cell.y) || DvergrViaduct.Standing(cell.x, cell.y)) return false;
        }
        landing = to;
        cost = price;
        if (OnExistingRoad(a) && OnExistingRoad(b)) cost *= RoadConstants.SharedCrossingCostFraction;
        return true;
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
        Vector2 start = GridToWorld(from);
        Vector2i at = from;
        for (int guard = 0; guard < 64 && Vector2.Distance(GridToWorld(at), start) <= JumpLookBack; guard++)
        {
            if (!cameFrom.TryGetValue(at, out Vector2i prev)) break;
            if (byViaduct.Contains(at)) (before ??= new List<(Vector2, Vector2)>()).Add((GridToWorld(prev), GridToWorld(at)));
            at = prev;
        }
        return before == null || !DvergrViaduct.IsTooClose(start, GridToWorld(landing), before);
    }

    private (Vector2i landing, float cost)? ViaductOverTerrain(Vector2i from, Vector2Int step)
    {
        if (m_terrain.Biome(from) != Heightmap.Biome.Mistlands) return null;
        float rim = m_terrain.Height(from);
        // A valley starts here: the next cell on is well below the rim.
        if (m_terrain.Height(new Vector2i(from.x + step.x, from.y + step.y)) > rim - ViaductRimDrop) return null;
        int maxCells = Mathf.FloorToInt(DvergrViaduct.MaxValleySpan / (CellSize * Mathf.Sqrt(step.x * step.x + step.y * step.y)) + 1e-3f);
        for (int s = 2; s <= maxCells; s++)
        {
            var check = new Vector2i(from.x + step.x * s, from.y + step.y * s);
            if (m_terrain.River(check) > 0f) return null;   // a river is a bridge's crossing
            float h = m_terrain.Height(check);
            if (h < LandingFloor) return null;
            if (h < rim - RoadConstants.MaxBridgeBankDelta) continue;
            if (h > rim + RoadConstants.MaxBridgeBankDelta) return null;
            Vector2 a = GridToWorld(from), b = GridToWorld(check);
            RoadCrossing? viaduct = DvergrViaduct.ValleyCrossing(a, b, m_worldGen, out _);
            if (viaduct == null) return null;
            float length = Vector2.Distance(a, b);
            return (check, DvergrViaduct.Price(viaduct, m_worldGen, ViaductCostFixed, ViaductCostPerMeter, ViaductEndStairCostPerMeter) + BaseCost * length);
        }
        return null;
    }

    /// <summary>
    /// Scans cell by cell from a dry cell across river water in one of the
    /// eight principal directions and lands on the first dry ground outside
    /// the river band. A jump within the ford cap over water no deeper than
    /// wading is a FORD at a small cost (with fords on); anything longer or
    /// deeper is a BRIDGE at the bridge cost (with bridges on), held to
    /// near-level banks. Water without a
    /// river core under it (a lake, the sea) is not crossed; neither is a dry
    /// river valley.
    /// </summary>
    private bool TryGetRiverCrossing(Vector2i from, Vector2Int direction, out Vector2i landing, out float crossingCost)
    {
        landing = from;
        crossingCost = 0f;

        // The scan walks whole cells, so a knight move would skip cells it
        // never checked and could start the crossing one cell short of the bank.
        if (Mathf.Abs(direction.x) > 1 || Mathf.Abs(direction.y) > 1)
            return false;

        Vector2 fromWorld = GridToWorld(from);

        // The cells' height and river weight are the search memo's own facts (the same
        // generator calls at the same cell centres): read through it, a scan repeated from
        // every bank cell of a river reads each cell once, and stays out of the game's river
        // cache, whose every miss takes a write lock all workers share.
        float fromHeight = m_terrain.Height(from);
        bool sawRiverWater = false;
        float deepest = float.MaxValue;
        int maxCells = Bridges ? RoadConstants.MaxBridgeCrossingCells : RoadConstants.MaxRiverCrossingCells;

        for (int step = 1; step <= maxCells; step++)
        {
            Vector2i check = new Vector2i(from.x + direction.x * step, from.y + direction.y * step);
            Vector2 world = GridToWorld(check);
            float height = m_terrain.Height(check);
            float riverWeight = m_terrain.River(check);
            bool water = height < LandingFloor;
            bool riverCore = riverWeight > RoadConstants.RiverImpassableThreshold;

            // Keep scanning over water and over the river band (its dry
            // shores included: a road cannot stand there either).
            if (water || riverCore)
            {
                sawRiverWater |= water && riverCore;
                deepest = Mathf.Min(deepest, height);
                continue;
            }

            // Dry ground: the far bank, if a river lay between.
            if (!sawRiverWater)
                return false;

            bool bridgesAllowed = Bridges;
            Vector2 fromBank = fromWorld;
            Vector2 toBank = world;
            // In a swamp the detector walks a wet bank out over the wade shelf
            // until it finds dry ground, which can add a great deal of deck.
            // Do it HERE, before the span is measured against the cap and
            // before the crossing is priced, so the search judges the bridge it
            // will actually get. Deciding afterwards would leave a route
            // accepted at one length and built at another.
            if (bridgesAllowed && m_worldGen.GetBiome(
                    (fromWorld.x + world.x) * 0.5f, (fromWorld.y + world.y) * 0.5f) == Heightmap.Biome.Swamp)
                (fromBank, toBank) = RoadCrossingDetector.ExtendOverSwampShelf(fromWorld, world, m_worldGen);

            float distance = Vector2.Distance(fromBank, toBank);
            // The cells are 8 m apart and a channel can hide between them:
            // the whole jump is sampled every 2 m before its depth is trusted.
            deepest = Mathf.Min(deepest, DeepestAlong(fromWorld, world));
            bool bridge = distance > RoadConstants.MaxRiverCrossingCells * CellSize
                || deepest < RoadConstants.SeaLevel - RoadConstants.FordWadeDepth;
            if (bridge ? !Bridges : !Fords)
                return false;
            if (distance > maxCells * CellSize)
                return false;

            float bankDelta = Mathf.Abs(
                BiomeBlendedHeight.GetBlendedHeight(toBank.x, toBank.y, m_worldGen)
                - BiomeBlendedHeight.GetBlendedHeight(fromBank.x, fromBank.y, m_worldGen));
            if (bankDelta > (bridge ? RoadConstants.MaxBridgeBankDelta : RoadConstants.MaxFordBankDelta))
                return false;

            // A landing squeezed between a place and the water is not a road
            // either: the road off the bridge would run along the shore there.
            if (SqueezedCell(check))
            {
                RecordSqueeze(GridToWorld(check), landing: true);
                return false;
            }
            // Nor is a deck that runs along the shore past a place: a 104 m bridge was measured
            // flying past a troll cave 7 m from its edge, over the very strip the squeeze
            // keeps a road out of, because only steps and landings were tested. Counted with the
            // landings.
            if (DeckSqueezed(fromBank, toBank, out Vector2 at))
            {
                RecordSqueeze(at, landing: true);
                return false;
            }

            landing = check;
            // Both ends already on road (a finished road's banks): the
            // crossing exists, so using it costs a fraction of its price,
            // and a later road detours to share it rather than build
            // another beside it, up to what that saving buys.
            bool shared = OnExistingRoad(fromWorld) && OnExistingRoad(world);
            float price = bridge ? BridgeCostFixed + BridgeCostPerMeter * distance : RoadConstants.RiverCrossingPenalty;
            if (shared)
                price *= RoadConstants.SharedCrossingCostFraction;
            crossingCost = price + BaseCost * distance
                + RoadConstants.BankDeltaPenalty * bankDelta * bankDelta;
            return true;
        }

        return false;
    }

    /// <summary>The lowest ground on the line between two points, sampled
    /// every 2 m at the biome-blended height the game renders (a biome edge
    /// can move the ground metres from the raw generator height), so the
    /// pathfinder and the crossing detector judge the same water.</summary>
    private float DeepestAlong(Vector2 a, Vector2 b)
    {
        float length = Vector2.Distance(a, b);
        int samples = Mathf.Max(1, Mathf.CeilToInt(length / 2f));
        float deepest = float.MaxValue;
        for (int i = 0; i <= samples; i++)
        {
            float t = (float)i / samples;
            deepest = Mathf.Min(deepest, BiomeBlendedHeight.GetBlendedHeight(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, m_worldGen));
        }
        return deepest;
    }

    /// <summary>Whether a finished road already runs through this point.</summary>
    private static bool OnExistingRoad(Vector2 world) =>
        RoadSpatialGrid.IsInitialized && RoadSpatialGrid.AnyRoadAt(world.x, world.y);

    // The cell being expanded is the step's start for all of its neighbours: whether a road runs through it is read once
    // per expansion. Forgotten at each search's start (RentSearchState), as roads are committed between searches.
    private Vector2i m_fromRoadCell;
    private bool m_fromRoadKnown, m_fromOnRoad;

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

    private List<Vector2> ReconstructPath(Dictionary<Vector2i, Vector2i> cameFrom, Dictionary<Vector2i, int> byStair, HashSet<Vector2i> byViaduct,
        Vector2i current, Vector2 start, Vector2 end)
    {
        List<Vector2> path = new List<Vector2> { end };

        while (cameFrom.ContainsKey(current))
        {
            path.Add(GridToWorld(current));
            if (byStair.ContainsKey(current)) LastStairSteps.Add((GridToWorld(cameFrom[current]), GridToWorld(current)));
            if (byViaduct.Contains(current)) LastViaductJumps.Add((GridToWorld(cameFrom[current]), GridToWorld(current)));
            current = cameFrom[current];
        }
        LastStairSteps.Reverse();
        LastViaductJumps.Reverse();

        path.Add(start);
        path.Reverse();
        return path;
    }
}
