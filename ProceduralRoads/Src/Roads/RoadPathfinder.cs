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

    public float SlopeMultiplier = RoadConstants.DefaultSlopeMultiplier;
    public float RiverPenalty = RoadConstants.DefaultRiverPenalty;
    public float WaterPenalty = RoadConstants.DefaultWaterPenalty;
    public float SteepSlopePenalty = RoadConstants.DefaultSteepSlopePenalty;
    public float SteepSlopeThreshold = RoadConstants.DefaultSteepSlopeThreshold;
    public float TerrainVariancePenalty = RoadConstants.DefaultTerrainVariancePenalty;
    public float TerrainVarianceThreshold = RoadConstants.DefaultTerrainVarianceThreshold;
    public float BaseCost = RoadConstants.DefaultBaseCost;
    public float SwampShallowWaterPenalty = RoadConstants.DefaultSwampShallowWaterPenalty;

    /// <summary>
    /// Fords (prototype; config "Fords/Enabled", off by default): whether a
    /// road may jump a knee-deep river, and wade swamp shallows. Applied at
    /// config read like MaxIterations; a pathfinder instance copies it when
    /// made. Off, the pathfinder behaves exactly as before.
    /// </summary>
    public static bool FordsEnabled = false;

    public bool Fords = FordsEnabled;

    /// <summary>
    /// Bridges (prototype; config "Bridges/Enabled", off by default): whether
    /// a road may jump a river too long or too deep to ford, on a bridge.
    /// The cost levers are "Bridges/CostFixed" and "Bridges/CostPerMeter".
    /// </summary>
    public static bool BridgesEnabled = false;
    public static float ConfiguredBridgeCostFixed = RoadConstants.DefaultBridgeCostFixed;
    public static float ConfiguredBridgeCostPerMeter = RoadConstants.DefaultBridgeCostPerMeter;

    public bool Bridges = BridgesEnabled;
    public float BridgeCostFixed = ConfiguredBridgeCostFixed;
    public float BridgeCostPerMeter = ConfiguredBridgeCostPerMeter;

    /// <summary>Ground a crossing may land on: the shallow-water line plus the bank clearance.</summary>
    public const float LandingFloor = RoadConstants.ShallowWaterHeight + RoadConstants.BankClearance;

    /// <summary>The floor for road ground in a biome: swamps wade down to DeepWaterHeight.</summary>
    public static float FloorFor(Heightmap.Biome biome) =>
        biome == Heightmap.Biome.Swamp ? RoadConstants.DeepWaterHeight : LandingFloor;

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

    public RoadPathfinder(WorldGenerator worldGen)
    {
        m_worldGen = worldGen;
    }

    public List<Vector2>? FindPath(Vector2 start, Vector2 end)
    {
        Vector2i startGrid = WorldToGrid(start);
        Vector2i endGrid = WorldToGrid(end);

        if (startGrid == endGrid)
            return new List<Vector2> { start, end };

        SortedSet<(float priority, Vector2i pos)> openSet = new SortedSet<(float, Vector2i)>(
            Comparer<(float priority, Vector2i pos)>.Create((a, b) =>
            {
                int cmp = a.priority.CompareTo(b.priority);
                if (cmp != 0) return cmp;
                cmp = a.pos.x.CompareTo(b.pos.x);
                if (cmp != 0) return cmp;
                return a.pos.y.CompareTo(b.pos.y);
            }));

        Dictionary<Vector2i, float> gCosts = new Dictionary<Vector2i, float>();
        Dictionary<Vector2i, Vector2i> cameFrom = new Dictionary<Vector2i, Vector2i>();
        HashSet<Vector2i> closedSet = new HashSet<Vector2i>();

        openSet.Add((Heuristic(startGrid, endGrid), startGrid));
        gCosts[startGrid] = 0;

        int iterations = 0;

        while (openSet.Count > 0 && iterations < MaxIterations)
        {
            iterations++;

            var current = openSet.Min;
            openSet.Remove(current);
            Vector2i currentPos = current.pos;

            if (currentPos == endGrid)
                return ReconstructPath(cameFrom, currentPos, start, end);

            closedSet.Add(currentPos);

            for (int i = 0; i < Directions.Length; i++)
            {
                Vector2i neighborPos = new Vector2i(currentPos.x + Directions[i].x, currentPos.y + Directions[i].y);

                if (closedSet.Contains(neighborPos))
                    continue;

                float moveCost = GetMoveCost(currentPos, neighborPos, i);
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
                }

                float tentativeG = gCosts[currentPos] + moveCost;

                if (!gCosts.TryGetValue(neighborPos, out float existingG) || tentativeG < existingG)
                {
                    cameFrom[neighborPos] = currentPos;
                    gCosts[neighborPos] = tentativeG;
                    float h = Heuristic(neighborPos, endGrid);
                    openSet.Remove((existingG + h, neighborPos));
                    openSet.Add((tentativeG + h, neighborPos));
                }
            }
        }

        string reason = openSet.Count == 0 ? "no reachable path" : "max iterations reached";
        Log.LogWarning($"Pathfinding failed: {reason} after {iterations} iterations");
        return null;
    }

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

    private float GetTerrainVariance(Vector2 pos)
    {
        float centerHeight = m_worldGen.GetHeight(pos.x, pos.y);
        float minHeight = centerHeight;
        float maxHeight = centerHeight;
        
        for (int i = 0; i < RoadConstants.TerrainVarianceSampleCount; i++)
        {
            float angle = i * Mathf.PI * 2f / RoadConstants.TerrainVarianceSampleCount;
            float h = m_worldGen.GetHeight(
                pos.x + Mathf.Cos(angle) * RoadConstants.TerrainVarianceSampleRadius,
                pos.y + Mathf.Sin(angle) * RoadConstants.TerrainVarianceSampleRadius);
            minHeight = Mathf.Min(minHeight, h);
            maxHeight = Mathf.Max(maxHeight, h);
        }
        
        return maxHeight - minHeight;
    }

    private float GetMoveCost(Vector2i from, Vector2i to, int directionIndex)
    {
        Vector2 fromWorld = GridToWorld(from);
        Vector2 toWorld = GridToWorld(to);

        float dist = DirectionCosts[directionIndex] * CellSize;
        float h1 = m_worldGen.GetHeight(fromWorld.x, fromWorld.y);
        float h2 = m_worldGen.GetHeight(toWorld.x, toWorld.y);
        float slope = Mathf.Abs(h2 - h1) / dist;

        m_worldGen.GetRiverWeight(toWorld.x, toWorld.y, out float riverWeight, out _);
        if (riverWeight > RoadConstants.RiverImpassableThreshold)
            return RiverPenalty;

        float biomeHeight = m_worldGen.GetHeight(toWorld.x, toWorld.y);
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

        if (slope > SteepSlopeThreshold)
            return SteepSlopePenalty;

        if (GetTerrainVariance(toWorld) > TerrainVarianceThreshold)
            return TerrainVariancePenalty;

        Heightmap.Biome biome = m_worldGen.GetBiome(toWorld.x, toWorld.y);
        if (biome == Heightmap.Biome.Mountain && slope > RoadConstants.MountainSlopeThreshold)
            return WaterPenalty;

        float riverCost = riverWeight > 0 ? WaterPenalty * riverWeight : 0f;
        return BaseCost * dist + (slope * slope * SlopeMultiplier) + riverCost + wadeCost;
    }

    /// <summary>
    /// Scans cell by cell from a dry cell across river water in one of the
    /// eight principal directions and lands on the first dry ground outside
    /// the river band. A jump within the ford cap over water no deeper than
    /// wading is a FORD at a small cost (with fords on); anything longer or
    /// deeper is a BRIDGE at the bridge cost (with bridges on), held to
    /// near-level banks and refused in or to the Mistlands. Water without a
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
        float fromHeight = m_worldGen.GetHeight(fromWorld.x, fromWorld.y);
        bool sawRiverWater = false;
        float deepest = float.MaxValue;
        bool mistlands = m_worldGen.GetBiome(fromWorld.x, fromWorld.y) == Heightmap.Biome.Mistlands;
        int maxCells = Bridges ? RoadConstants.MaxBridgeCrossingCells : RoadConstants.MaxRiverCrossingCells;

        for (int step = 1; step <= maxCells; step++)
        {
            Vector2i check = new Vector2i(from.x + direction.x * step, from.y + direction.y * step);
            Vector2 world = GridToWorld(check);
            float height = m_worldGen.GetHeight(world.x, world.y);
            m_worldGen.GetRiverWeight(world.x, world.y, out float riverWeight, out _);
            bool water = height < LandingFloor;
            bool riverCore = riverWeight > RoadConstants.RiverImpassableThreshold;

            // Keep scanning over water and over the river band (its dry
            // shores included: a road cannot stand there either).
            if (water || riverCore)
            {
                sawRiverWater |= water && riverCore;
                deepest = Mathf.Min(deepest, height);
                mistlands |= m_worldGen.GetBiome(world.x, world.y) == Heightmap.Biome.Mistlands;
                continue;
            }

            // Dry ground: the far bank, if a river lay between.
            if (!sawRiverWater)
                return false;

            float distance = Vector2.Distance(fromWorld, world);
            // The cells are 8 m apart and a channel can hide between them:
            // the whole jump is sampled every 2 m before its depth is trusted.
            deepest = Mathf.Min(deepest, DeepestAlong(fromWorld, world));
            bool bridge = distance > RoadConstants.MaxRiverCrossingCells * CellSize
                || deepest < RoadConstants.SeaLevel - RoadConstants.FordWadeDepth;
            if (bridge ? !Bridges : !Fords)
                return false;
            if (distance > maxCells * CellSize)
                return false;

            mistlands |= m_worldGen.GetBiome(world.x, world.y) == Heightmap.Biome.Mistlands;
            if (bridge && mistlands)
                return false;

            float bankDelta = Mathf.Abs(height - fromHeight);
            if (bankDelta > (bridge ? RoadConstants.MaxBridgeBankDelta : RoadConstants.MaxFordBankDelta))
                return false;

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
    private static bool OnExistingRoad(Vector2 world)
    {
        if (!RoadSpatialGrid.IsInitialized)
            return false;
        RoadSpatialGrid.GetRoadWeight(world.x, world.y, out float weight, out _);
        return weight > 0f;
    }

    private List<Vector2> ReconstructPath(Dictionary<Vector2i, Vector2i> cameFrom, Vector2i current, Vector2 start, Vector2 end)
    {
        List<Vector2> path = new List<Vector2> { end };

        while (cameFrom.ContainsKey(current))
        {
            path.Add(GridToWorld(current));
            current = cameFrom[current];
        }

        path.Add(start);
        path.Reverse();
        return path;
    }
}
