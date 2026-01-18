using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// A* pathfinding for road generation with 16-direction movement, slope-based cost, and river avoidance.
/// </summary>
public class RoadPathfinder
{
    public const float CellSize = RoadConstants.PathfindingCellSize;
    public const int MaxIterations = RoadConstants.PathfindingMaxIterations;

    public float SlopeMultiplier = RoadConstants.DefaultSlopeMultiplier;
    public float RiverPenalty = RoadConstants.DefaultRiverPenalty;
    public float WaterPenalty = RoadConstants.DefaultWaterPenalty;
    public float SteepSlopePenalty = RoadConstants.DefaultSteepSlopePenalty;
    public float SteepSlopeThreshold = RoadConstants.DefaultSteepSlopeThreshold;
    public float TerrainVariancePenalty = RoadConstants.DefaultTerrainVariancePenalty;
    public float TerrainVarianceThreshold = RoadConstants.DefaultTerrainVarianceThreshold;
    public float BaseCost = RoadConstants.DefaultBaseCost;

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

    public List<Vector2> FindPath(Vector2 start, Vector2 end)
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
                    continue;

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
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning($"Pathfinding failed: {reason} after {iterations} iterations");
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
        if (biomeHeight < RoadConstants.ShallowWaterHeight)
            return WaterPenalty;

        if (slope > SteepSlopeThreshold)
            return SteepSlopePenalty;

        if (GetTerrainVariance(toWorld) > TerrainVarianceThreshold)
            return TerrainVariancePenalty;

        Heightmap.Biome biome = m_worldGen.GetBiome(toWorld.x, toWorld.y);
        if (biome == Heightmap.Biome.Mountain && slope > RoadConstants.MountainSlopeThreshold)
            return WaterPenalty;

        float riverCost = riverWeight > 0 ? WaterPenalty * riverWeight : 0f;
        return BaseCost * dist + (slope * slope * SlopeMultiplier) + riverCost;
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
