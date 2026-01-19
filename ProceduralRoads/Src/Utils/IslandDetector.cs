using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

public class Island
{
    public int Id { get; set; }
    public Vector2 Center { get; set; }
    public Vector2 Min { get; set; }
    public Vector2 Max { get; set; }
    public int CellCount { get; set; }
    public float ApproxArea { get; set; }
    public List<Vector2Int> Cells { get; set; } = new();
    public float CellSize { get; set; }
    public float WorldOffset { get; set; }
    
    private HashSet<Vector2Int>? _cellSet;
    
    public bool ContainsPoint(float worldX, float worldZ)
    {
        if (worldX < Min.x || worldX > Max.x || worldZ < Min.y || worldZ > Max.y)
            return false;
        
        _cellSet ??= new HashSet<Vector2Int>(Cells);
        
        int cellX = Mathf.RoundToInt((worldX + WorldOffset) / CellSize);
        int cellZ = Mathf.RoundToInt((worldZ + WorldOffset) / CellSize);
        return _cellSet.Contains(new Vector2Int(cellX, cellZ));
    }
    
    public bool ContainsPoint(Vector3 worldPos) => ContainsPoint(worldPos.x, worldPos.z);
    
    public Vector2 GetEdgePoint(float cellSize, float worldOffset)
    {
        if (Cells.Count == 0) return Center;
        
        float minDistToEdge = float.MaxValue;
        Vector2Int edgeCell = Cells[0];
        
        foreach (var cell in Cells)
        {
            Vector2 worldPos = CellToWorld(cell, cellSize, worldOffset);
            float distToMinX = Mathf.Abs(worldPos.x - Min.x);
            float distToMaxX = Mathf.Abs(worldPos.x - Max.x);
            float distToMinY = Mathf.Abs(worldPos.y - Min.y);
            float distToMaxY = Mathf.Abs(worldPos.y - Max.y);
            float minDist = Mathf.Min(distToMinX, distToMaxX, distToMinY, distToMaxY);
            
            if (minDist < minDistToEdge)
            {
                minDistToEdge = minDist;
                edgeCell = cell;
            }
        }
        
        return CellToWorld(edgeCell, cellSize, worldOffset);
    }
    
    private Vector2 CellToWorld(Vector2Int cell, float cellSize, float worldOffset)
    {
        return new Vector2(
            cell.x * cellSize - worldOffset,
            cell.y * cellSize - worldOffset
        );
    }
}

public static class IslandDetector
{
    private const float WaterThreshold = 0.05f;
    private const float DefaultCellSize = 128f;
    private const int MinIslandCells = 10;
    
    public static List<Island> DetectIslands(float cellSize = DefaultCellSize, int minCells = MinIslandCells)
    {
        var wg = WorldGenerator.instance;
        if (wg == null)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning("IslandDetector: WorldGenerator not available");
            return new List<Island>();
        }
        
        float worldRadius = 10000f;
        float worldSize = worldRadius * 2;
        int gridSize = Mathf.CeilToInt(worldSize / cellSize);
        float worldOffset = worldRadius;
        
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo($"IslandDetector: Scanning {gridSize}x{gridSize} grid (cellSize={cellSize}m)");
        
        bool[,] isLand = new bool[gridSize, gridSize];
        int landCells = 0;
        
        for (int x = 0; x < gridSize; x++)
        {
            for (int y = 0; y < gridSize; y++)
            {
                float worldX = x * cellSize - worldOffset;
                float worldY = y * cellSize - worldOffset;
                
                float distFromCenter = Mathf.Sqrt(worldX * worldX + worldY * worldY);
                if (distFromCenter > worldRadius)
                {
                    isLand[x, y] = false;
                    continue;
                }
                
                float baseHeight = wg.GetBaseHeight(worldX, worldY, false);
                isLand[x, y] = baseHeight >= WaterThreshold;
                if (isLand[x, y]) landCells++;
            }
        }
        
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo($"IslandDetector: Found {landCells} land cells out of {gridSize * gridSize} total");
        
        bool[,] visited = new bool[gridSize, gridSize];
        var islands = new List<Island>();
        int islandId = 0;
        
        for (int x = 0; x < gridSize; x++)
        {
            for (int y = 0; y < gridSize; y++)
            {
                if (isLand[x, y] && !visited[x, y])
                {
                    var island = FloodFill(isLand, visited, x, y, gridSize, cellSize, worldOffset);
                    if (island.CellCount >= minCells)
                    {
                        island.Id = islandId++;
                        islands.Add(island);
                    }
                }
            }
        }
        
        islands.Sort((a, b) => b.CellCount.CompareTo(a.CellCount));
        
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo($"IslandDetector: Detected {islands.Count} islands (min {minCells} cells)");
        
        return islands;
    }
    
    private static Island FloodFill(bool[,] isLand, bool[,] visited, int startX, int startY, 
        int gridSize, float cellSize, float worldOffset)
    {
        var island = new Island();
        var queue = new Queue<Vector2Int>();
        queue.Enqueue(new Vector2Int(startX, startY));
        visited[startX, startY] = true;
        
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        float sumX = 0, sumY = 0;
        
        int[] dx = { 0, 1, 0, -1 };
        int[] dy = { 1, 0, -1, 0 };
        
        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            island.Cells.Add(cell);
            island.CellCount++;
            
            float worldX = cell.x * cellSize - worldOffset;
            float worldY = cell.y * cellSize - worldOffset;
            sumX += worldX;
            sumY += worldY;
            
            if (worldX < minX) minX = worldX;
            if (worldX > maxX) maxX = worldX;
            if (worldY < minY) minY = worldY;
            if (worldY > maxY) maxY = worldY;
            
            for (int i = 0; i < 4; i++)
            {
                int nx = cell.x + dx[i];
                int ny = cell.y + dy[i];
                
                if (nx >= 0 && nx < gridSize && ny >= 0 && ny < gridSize &&
                    isLand[nx, ny] && !visited[nx, ny])
                {
                    visited[nx, ny] = true;
                    queue.Enqueue(new Vector2Int(nx, ny));
                }
            }
        }
        
        Vector2 centroid = new Vector2(sumX / island.CellCount, sumY / island.CellCount);
        
        float minDistToCentroid = float.MaxValue;
        Vector2 closestCell = centroid;
        foreach (var cell in island.Cells)
        {
            float wx = cell.x * cellSize - worldOffset;
            float wy = cell.y * cellSize - worldOffset;
            float dist = (wx - centroid.x) * (wx - centroid.x) + (wy - centroid.y) * (wy - centroid.y);
            if (dist < minDistToCentroid)
            {
                minDistToCentroid = dist;
                closestCell = new Vector2(wx, wy);
            }
        }
        
        island.Center = closestCell;
        island.Min = new Vector2(minX, minY);
        island.Max = new Vector2(maxX, maxY);
        island.ApproxArea = island.CellCount * cellSize * cellSize;
        island.CellSize = cellSize;
        island.WorldOffset = worldOffset;
        
        return island;
    }
    
    public static string GetIslandSummary(Island island)
    {
        float width = island.Max.x - island.Min.x;
        float height = island.Max.y - island.Min.y;
        return $"Island {island.Id}: center=({island.Center.x:F0},{island.Center.y:F0}) " +
               $"size={width:F0}x{height:F0}m area≈{island.ApproxArea/1000000:F2}km² cells={island.CellCount}";
    }
}
