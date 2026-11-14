using System.Collections.Generic;
using BepInEx.Logging;
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
    public List<Vector3> CoastalLandings { get; set; } = new();
    
    private HashSet<Vector2Int>? _cellSet;
    
    /// <summary>Whether the point is in one of the island's cells: the cell nearest it, half a cell either side of the
    /// cell's centre. <see cref="Min"/> and <see cref="Max"/> are the outermost cells' centres, so the quick box test
    /// reaches half a cell past them, as far as those cells do.</summary>
    public bool ContainsPoint(float worldX, float worldZ)
    {
        float half = CellSize * 0.5f;
        if (worldX < Min.x - half || worldX > Max.x + half || worldZ < Min.y - half || worldZ > Max.y + half)
            return false;
        
        _cellSet ??= new HashSet<Vector2Int>(Cells);
        
        int cellX = Mathf.RoundToInt((worldX + WorldOffset) / CellSize);
        int cellZ = Mathf.RoundToInt((worldZ + WorldOffset) / CellSize);
        return _cellSet.Contains(new Vector2Int(cellX, cellZ));
    }
    
    public bool ContainsPoint(Vector3 worldPos) => ContainsPoint(worldPos.x, worldPos.z);

    /// <summary>The zones holding any of the island's cells (each cell is far smaller than a zone, so it lies in one): the
    /// land road_regen_island builds in, every other zone being off the island.</summary>
    public HashSet<Vector2s> Zones()
    {
        var zones = new HashSet<Vector2s>();
        foreach (Vector2Int cell in Cells)
        {
            Vector2 p = CellToWorld(cell);
            zones.Add(ZoneSystem.GetZone(new Vector3(p.x, 0f, p.y)));
        }
        return zones;
    }
    
    public Vector2 GetEdgePoint()
    {
        if (Cells.Count == 0) return Center;
        
        float minDistToEdge = float.MaxValue;
        Vector2Int edgeCell = Cells[0];
        
        foreach (var cell in Cells)
        {
            Vector2 worldPos = CellToWorld(cell);
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
        
        return CellToWorld(edgeCell);
    }
    
    internal IEnumerable<bool> PrepareMembership(RoadPreparation preparation)
    {
        if(_cellSet!=null) yield break;
        var copied=new HashSet<Vector2Int>();
        foreach(var cell in Cells) { copied.Add(cell); if(preparation.Checkpoint()) yield return true; }
        _cellSet=copied;
    }

    public Vector2 CellToWorld(Vector2Int cell) => IslandDetector.CellToWorld(cell, CellSize, WorldOffset);
}

public static class IslandDetector
{
    private static ManualLogSource Log => ProceduralRoadsPlugin.ProceduralRoadsLogger;
    
    public static Vector2 CellToWorld(Vector2Int cell, float cellSize, float worldOffset)
    {
        return new Vector2(
            cell.x * cellSize - worldOffset,
            cell.y * cellSize - worldOffset
        );
    }
    
    /// <summary>The grouping used by road generation, also exposed by road_islands.</summary>
    public static List<Island> DetectRoadIslands()
    {
        List<Island>? result=null;
        using var preparation=new RoadPreparation(p=>DetectRoadIslandsSteps(WorldGenerator.instance,RoadNetworkGenerator.NetworkOptions,p,value=>result=value));
        preparation.Drain(); return result!;
    }
    /// <summary>Whether generation's island scan is bounded to the world's land (<see cref="RoadIslandDetector.DetectSteps"/>'s
    /// toLand): the same islands, scanned faster on a world whose land lies near its centre. Off in the game, which scans
    /// the whole world; on in the unit suite (PlainRoads), whose small worlds would otherwise each scan 6.25 M cells.</summary>
    internal static bool BoundedScan = false;

    internal static IEnumerable<bool> DetectRoadIslandsSteps(WorldGenerator world,RoadNetworkOptions options,
        RoadPreparation preparation,System.Action<List<Island>> complete)
    {
        var clock=System.Diagnostics.Stopwatch.StartNew();
        List<Island>? islands=null;
        foreach(var step in RoadIslandDetector.DetectSteps(world,preparation,value=>islands=value,options:options,toLand:BoundedScan)) yield return step;
        Log.LogInfo($"Road island detection: {islands!.Count} groups at {RoadIslandDetector.SampleSpacing} m in {clock.Elapsed.TotalSeconds:F1}s");
        complete(islands);
    }

    public static string GetIslandSummary(Island island)
    {
        float width = island.Max.x - island.Min.x;
        float height = island.Max.y - island.Min.y;
        return $"Island {island.Id}: center=({island.Center.x:F0},{island.Center.y:F0}) " +
               $"size={width:F0}x{height:F0}m area≈{island.ApproxArea/1000000:F2}km² cells={island.CellCount}";
    }
}
