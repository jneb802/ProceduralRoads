using System.Collections.Generic;
using System.IO;
using System.Threading;
using BepInEx.Logging;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Spatial data structure for road points. Provides efficient lookup for road influence at any world position.
/// </summary>
public static class RoadSpatialGrid
{
    public struct RoadPoint
    {
        public Vector2 p;
        public float w;
        public float w2;
        public float h;

        public RoadPoint(Vector2 position, float width, float height)
        {
            p = position;
            w = width;
            w2 = width * width;
            h = height;
        }
    }

    /// <summary>
    /// Debug information about how a road point's height was calculated.
    /// Stored during generation for later inspection.
    /// </summary>
    public struct RoadPointDebugInfo
    {
        public int PointIndex;           // Index in the path
        public int TotalPoints;          // Total points in this road
        public float OriginalHeight;     // Height before smoothing (terrain height)
        public float SmoothedHeight;     // Final height after smoothing
        public int WindowStart;          // First index used in smoothing window
        public int WindowEnd;            // Last index used in smoothing window
        public int ActualWindowSize;     // How many heights were averaged
        public float[] WindowHeights;    // The original heights in window
    }

    public const float GridSize = RoadConstants.SpatialGridSize;
    public const float DefaultRoadWidth = RoadConstants.DefaultRoadWidth;

    private static Dictionary<Vector2i, RoadPoint[]> m_roadPoints = new Dictionary<Vector2i, RoadPoint[]>();
    private static RoadPoint[]? m_cachedRoadPoints;
    private static Vector2i m_cachedRoadGrid = new Vector2i(-999999, -999999);
    private static ReaderWriterLockSlim m_roadCacheLock = new ReaderWriterLockSlim();
    private static bool m_initialized = false;
    
    // Debug info storage - keyed by road point position for lookup
    private static Dictionary<Vector2, RoadPointDebugInfo> m_debugInfo = new Dictionary<Vector2, RoadPointDebugInfo>();
    
    public static int TotalRoadPoints { get; private set; } = 0;
    public static int GridCellsWithRoads { get; private set; } = 0;
    public static float TotalRoadLength { get; private set; } = 0f;
    
    /// <summary>
    /// Version hash for the current road network. Changes when roads are regenerated.
    /// Used to detect if a zone's terrain mods are stale and need reapplication.
    /// </summary>
    public static int RoadNetworkVersion { get; private set; } = 0;
    
    private static ManualLogSource Log => ProceduralRoadsPlugin.ProceduralRoadsLogger;

    public static bool IsInitialized => m_initialized;

    public static void Clear()
    {
        m_roadCacheLock.EnterWriteLock();
        try
        {
            m_roadPoints.Clear();
            m_cachedRoadPoints = null;
            m_cachedRoadGrid = new Vector2i(-999999, -999999);
            m_initialized = false;
            TotalRoadPoints = 0;
            GridCellsWithRoads = 0;
            TotalRoadLength = 0f;
            RoadNetworkVersion = 0;
            m_debugInfo.Clear();
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
        return m_debugInfo.TryGetValue(position, out debugInfo);
    }

    public static void AddRoadPath(List<Vector2> path, float width, WorldGenerator worldGen)
    {
        if (path == null || path.Count < 2 || worldGen == null)
            return;

        float segmentLength = width / 4f;
        
        float totalLength = 0f;
        for (int i = 0; i < path.Count - 1; i++)
            totalLength += Vector2.Distance(path[i], path[i + 1]);
        
        List<Vector2> densePoints = SplinePath(path, segmentLength);
        List<float> denseHeights = new List<float>(densePoints.Count);
        
        // Use biome-blended heights to match what Heightmap actually renders.
        // WorldGenerator.GetHeight doesn't blend at biome boundaries, causing
        // "phantom cliffs" that don't exist in the visible terrain.
        foreach (var point in densePoints)
            denseHeights.Add(BiomeBlendedHeight.GetBlendedHeight(point.x, point.y, worldGen));
        
        List<float> smoothedHeights = SmoothHeights(denseHeights, RoadConstants.HeightSmoothingWindow, out var debugInfos);
        
        int overlapCount = DetectOverlap(densePoints, width);
        if (overlapCount > densePoints.Count * RoadConstants.OverlapThreshold)
        {
            Log.LogDebug($"Road path overlaps with existing roads ({overlapCount}/{densePoints.Count} points), blending heights");
            BlendWithExistingRoads(densePoints, smoothedHeights, width);
        }
        
        Log.LogDebug($"Road path: {path.Count} waypoints -> {densePoints.Count} dense points");
        Log.LogDebug($"  Path length: {totalLength:F0}m, smoothing window: {RoadConstants.HeightSmoothingWindow} points");
        Log.LogDebug($"  Overlap: {overlapCount}/{densePoints.Count} points overlap existing roads");

        Dictionary<Vector2i, List<RoadPoint>> tempPoints = new Dictionary<Vector2i, List<RoadPoint>>();
        for (int i = 0; i < densePoints.Count; i++)
        {
            AddRoadPoint(tempPoints, densePoints[i], width, smoothedHeights[i]);
            
            // Store debug info keyed by position
            m_debugInfo[densePoints[i]] = debugInfos[i];
        }

        MergePoints(tempPoints);
        
        TotalRoadPoints += densePoints.Count;
        TotalRoadLength += totalLength;
        m_initialized = true;
    }

    /// <summary>
    /// Called after all roads are generated to compute the network version hash.
    /// This version is stored in TerrainComp ZDOs to detect already-processed zones.
    /// </summary>
    public static void FinalizeRoadNetwork()
    {
        // Compute a version hash from road network properties + world seed
        // This changes when: world seed changes, road count changes, road layout changes
        int worldSeed = WorldGenerator.instance?.GetSeed() ?? 0;
        int hash = worldSeed;
        hash = hash * 31 + TotalRoadPoints;
        hash = hash * 31 + GridCellsWithRoads;
        hash = hash * 31 + (int)(TotalRoadLength * 10); // Include length with some precision
        
        RoadNetworkVersion = hash;
        Log.LogDebug($"Road network finalized: version={RoadNetworkVersion}, points={TotalRoadPoints}, cells={GridCellsWithRoads}");
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
        
        for (int i = 0; i < heights.Count; i++)
        {
            float sum = 0f;
            int count = 0;
            int windowStart = Mathf.Max(0, i - halfWindow);
            int windowEnd = Mathf.Min(heights.Count - 1, i + halfWindow);
            
            List<float> windowHeights = new List<float>();
            
            for (int j = windowStart; j <= windowEnd; j++)
            {
                sum += heights[j];
                count++;
                windowHeights.Add(heights[j]);
            }
            
            float smoothedHeight = sum / count;
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
                WindowHeights = windowHeights.ToArray()
            });
        }
        
        return smoothed;
    }

    private static int DetectOverlap(List<Vector2> points, float width)
    {
        if (!m_initialized || m_roadPoints.Count == 0)
            return 0;

        int overlapCount = 0;
        float searchRadius = width * RoadConstants.OverlapSearchRadiusMultiplier;

        foreach (var point in points)
        {
            Vector2i grid = GetRoadGrid(point.x, point.y);
            
            m_roadCacheLock.EnterReadLock();
            try
            {
                if (m_roadPoints.TryGetValue(grid, out var existingPoints))
                {
                    foreach (var rp in existingPoints)
                    {
                        if (Vector2.Distance(rp.p, point) < searchRadius)
                        {
                            overlapCount++;
                            break;
                        }
                    }
                }
            }
            finally
            {
                m_roadCacheLock.ExitReadLock();
            }
        }

        return overlapCount;
    }

    private static void BlendWithExistingRoads(List<Vector2> points, List<float> heights, float width)
    {
        if (!m_initialized || m_roadPoints.Count == 0)
            return;

        float blendRadius = width * RoadConstants.OverlapBlendRadiusMultiplier;

        for (int i = 0; i < points.Count; i++)
        {
            Vector2i grid = GetRoadGrid(points[i].x, points[i].y);
            
            m_roadCacheLock.EnterReadLock();
            try
            {
                if (m_roadPoints.TryGetValue(grid, out var existingPoints))
                {
                    float totalWeight = 1.0f;
                    float weightedSum = heights[i];
                    
                    foreach (var rp in existingPoints)
                    {
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
            finally
            {
                m_roadCacheLock.ExitReadLock();
            }
        }
    }

    private static void AddRoadPoint(Dictionary<Vector2i, List<RoadPoint>> roadPoints, Vector2 p, float width, float height)
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
                    list.Add(new RoadPoint(p, width, height));
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

    private static void MergePoints(Dictionary<Vector2i, List<RoadPoint>> tempPoints)
    {
        m_roadCacheLock.EnterWriteLock();
        try
        {
            foreach (var kvp in tempPoints)
            {
                if (m_roadPoints.TryGetValue(kvp.Key, out var existing))
                {
                    var combined = new List<RoadPoint>(existing);
                    combined.AddRange(kvp.Value);
                    m_roadPoints[kvp.Key] = combined.ToArray();
                }
                else
                {
                    m_roadPoints.Add(kvp.Key, kvp.Value.ToArray());
                }
            }
            
            GridCellsWithRoads = m_roadPoints.Count;
            m_cachedRoadGrid = new Vector2i(-999999, -999999);
            m_cachedRoadPoints = null;
        }
        finally
        {
            m_roadCacheLock.ExitWriteLock();
        }
    }

    public static Vector2i GetRoadGrid(float wx, float wy)
    {
        int x = Mathf.FloorToInt((wx + GridSize / 2f) / GridSize);
        int y = Mathf.FloorToInt((wy + GridSize / 2f) / GridSize);
        return new Vector2i(x, y);
    }

    public static void GetRoadWeight(float wx, float wy, out float weight, out float width)
    {
        Vector2i grid = GetRoadGrid(wx, wy);

        m_roadCacheLock.EnterReadLock();
        try
        {
            if (grid == m_cachedRoadGrid)
            {
                if (m_cachedRoadPoints != null)
                {
                    GetWeight(m_cachedRoadPoints, wx, wy, out weight, out width);
                    return;
                }
                weight = 0f;
                width = 0f;
                return;
            }
        }
        finally
        {
            m_roadCacheLock.ExitReadLock();
        }

        if (m_roadPoints.TryGetValue(grid, out var points))
        {
            GetWeight(points, wx, wy, out weight, out width);
            
            m_roadCacheLock.EnterWriteLock();
            try
            {
                m_cachedRoadGrid = grid;
                m_cachedRoadPoints = points;
            }
            finally
            {
                m_roadCacheLock.ExitWriteLock();
            }
        }
        else
        {
            m_roadCacheLock.EnterWriteLock();
            try
            {
                m_cachedRoadGrid = grid;
                m_cachedRoadPoints = null;
            }
            finally
            {
                m_roadCacheLock.ExitWriteLock();
            }
            
            weight = 0f;
            width = 0f;
        }
    }

    private static void GetWeight(RoadPoint[] points, float wx, float wy, out float weight, out float width)
    {
        Vector2 pos = new Vector2(wx, wy);
        float bestWeight = 0f;
        float bestWidth = 0f;

        for (int i = 0; i < points.Length; i++)
        {
            RoadPoint rp = points[i];
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

    public static List<RoadPoint> GetRoadPointsInZone(Vector2i zoneID)
    {
        List<RoadPoint> result = new List<RoadPoint>();
        
        if (!m_initialized)
            return result;

        Vector3 zonePos = ZoneSystem.GetZonePos(zoneID);
        Vector2i grid = GetRoadGrid(zonePos.x, zonePos.z);

        m_roadCacheLock.EnterReadLock();
        try
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
                            if (rp.p.x >= zonePos.x - RoadConstants.HalfZoneSize - rp.w &&
                                rp.p.x <= zonePos.x + RoadConstants.HalfZoneSize + rp.w &&
                                rp.p.y >= zonePos.z - RoadConstants.HalfZoneSize - rp.w &&
                                rp.p.y <= zonePos.z + RoadConstants.HalfZoneSize + rp.w)
                            {
                                result.Add(rp);
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            m_roadCacheLock.ExitReadLock();
        }

        return result;
    }

    public static int GetTotalPointCount()
    {
        int count = 0;
        m_roadCacheLock.EnterReadLock();
        try
        {
            foreach (var kvp in m_roadPoints)
                count += kvp.Value.Length;
        }
        finally
        {
            m_roadCacheLock.ExitReadLock();
        }
        return count;
    }

    public static List<RoadPoint> GetRoadPointsNearPosition(Vector3 worldPos, float radius)
    {
        List<RoadPoint> result = new List<RoadPoint>();
        
        if (!m_initialized)
            return result;

        Vector2 pos2D = new Vector2(worldPos.x, worldPos.z);
        float radiusSq = radius * radius;

        int cellRadius = Mathf.CeilToInt(radius / GridSize) + 1;
        Vector2i centerGrid = GetRoadGrid(worldPos.x, worldPos.z);

        m_roadCacheLock.EnterReadLock();
        try
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
                                result.Add(rp);
                        }
                    }
                }
            }
        }
        finally
        {
            m_roadCacheLock.ExitReadLock();
        }

        result.Sort((a, b) => (a.p - pos2D).sqrMagnitude.CompareTo((b.p - pos2D).sqrMagnitude));
        return result;
    }

    #region ZDO Persistence

    public static readonly int RoadDataHash = "ProceduralRoads_RoadData".GetStableHashCode();

    /// <summary>
    /// Serialize road points for a specific zone to a byte array for ZDO storage.
    /// Uses the same logic as GetRoadPointsInZone to capture all affecting points.
    /// </summary>
    public static byte[]? SerializeZoneRoadPoints(Vector2i zoneID)
    {
        // Get all road points that affect this zone (same as GetRoadPointsInZone)
        var points = GetRoadPointsInZone(zoneID);
        
        if (points.Count == 0)
            return null;

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        
        writer.Write(points.Count);
        foreach (var rp in points)
        {
            writer.Write(rp.p.x);
            writer.Write(rp.p.y);
            writer.Write(rp.w);
            writer.Write(rp.h);
        }
        
        return ms.ToArray();
    }

    /// <summary>
    /// Deserialize road points from a byte array and add them to the grid.
    /// Points are added to grid cells based on their actual position.
    /// </summary>
    public static void DeserializeZoneRoadPoints(Vector2i zoneID, byte[] data)
    {
        if (data == null || data.Length == 0)
            return;

        try
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);
            
            int count = reader.ReadInt32();
            if (count <= 0 || count > 100000) // Sanity check
                return;
            
            // Group points by their grid cell
            var pointsByGrid = new Dictionary<Vector2i, List<RoadPoint>>();
            
            for (int i = 0; i < count; i++)
            {
                float px = reader.ReadSingle();
                float py = reader.ReadSingle();
                float w = reader.ReadSingle();
                float h = reader.ReadSingle();
                
                var point = new RoadPoint(new Vector2(px, py), w, h);
                Vector2i grid = GetRoadGrid(px, py);
                
                if (!pointsByGrid.TryGetValue(grid, out var list))
                {
                    list = new List<RoadPoint>();
                    pointsByGrid[grid] = list;
                }
                list.Add(point);
            }
            
            // Add to grid
            AddDeserializedPoints(pointsByGrid);
        }
        catch (System.Exception ex)
        {
            Log.LogWarning($"Failed to deserialize road points for zone {zoneID}: {ex.Message}");
        }
    }

    /// <summary>
    /// Add deserialized road points to the grid without duplicating.
    /// </summary>
    private static void AddDeserializedPoints(Dictionary<Vector2i, List<RoadPoint>> pointsByGrid)
    {
        m_roadCacheLock.EnterWriteLock();
        try
        {
            foreach (var kvp in pointsByGrid)
            {
                Vector2i grid = kvp.Key;
                var newPoints = kvp.Value;
                
                if (!m_roadPoints.ContainsKey(grid))
                {
                    m_roadPoints[grid] = newPoints.ToArray();
                }
                // If grid already has points, they were loaded from another zone or generated - skip
            }
            
            GridCellsWithRoads = m_roadPoints.Count;
            m_initialized = true;
            
            // Invalidate cache
            m_cachedRoadGrid = new Vector2i(-999999, -999999);
            m_cachedRoadPoints = null;
        }
        finally
        {
            m_roadCacheLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Serialize the entire road network to a byte array for global persistence.
    /// Format: [version:int][cellCount:int][grid.x:int][grid.y:int][pointCount:int][points...]...
    /// </summary>
    public static byte[]? SerializeAllRoadPoints()
    {
        m_roadCacheLock.EnterReadLock();
        try
        {
            if (m_roadPoints.Count == 0)
                return null;

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            
            // Version for future compatibility
            writer.Write(1);
            
            // Number of grid cells
            writer.Write(m_roadPoints.Count);
            
            foreach (var kvp in m_roadPoints)
            {
                // Grid coordinates
                writer.Write(kvp.Key.x);
                writer.Write(kvp.Key.y);
                
                // Points in this cell
                writer.Write(kvp.Value.Length);
                foreach (var rp in kvp.Value)
                {
                    writer.Write(rp.p.x);
                    writer.Write(rp.p.y);
                    writer.Write(rp.w);
                    writer.Write(rp.h);
                }
            }
            
            return ms.ToArray();
        }
        finally
        {
            m_roadCacheLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Deserialize the entire road network from a byte array.
    /// Clears existing data and replaces with loaded data.
    /// </summary>
    public static bool DeserializeAllRoadPoints(byte[] data)
    {
        if (data == null || data.Length == 0)
            return false;

        try
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);
            
            int version = reader.ReadInt32();
            if (version != 1)
            {
                Log.LogWarning($"Unknown road data version: {version}");
                return false;
            }
            
            int cellCount = reader.ReadInt32();
            if (cellCount < 0 || cellCount > 1000000) // Sanity check
            {
                Log.LogWarning($"Invalid cell count: {cellCount}");
                return false;
            }
            
            var loadedPoints = new Dictionary<Vector2i, RoadPoint[]>(cellCount);
            int totalPoints = 0;
            
            for (int c = 0; c < cellCount; c++)
            {
                int gridX = reader.ReadInt32();
                int gridY = reader.ReadInt32();
                int pointCount = reader.ReadInt32();
                
                if (pointCount < 0 || pointCount > 100000) // Sanity check
                {
                    Log.LogWarning($"Invalid point count at cell ({gridX},{gridY}): {pointCount}");
                    return false;
                }
                
                var points = new RoadPoint[pointCount];
                for (int i = 0; i < pointCount; i++)
                {
                    float px = reader.ReadSingle();
                    float py = reader.ReadSingle();
                    float w = reader.ReadSingle();
                    float h = reader.ReadSingle();
                    points[i] = new RoadPoint(new Vector2(px, py), w, h);
                }
                
                loadedPoints[new Vector2i(gridX, gridY)] = points;
                totalPoints += pointCount;
            }
            
            // Replace current data
            m_roadCacheLock.EnterWriteLock();
            try
            {
                m_roadPoints = loadedPoints;
                m_cachedRoadGrid = new Vector2i(-999999, -999999);
                m_cachedRoadPoints = null;
                m_initialized = true;
                GridCellsWithRoads = loadedPoints.Count;
                TotalRoadPoints = totalPoints;
            }
            finally
            {
                m_roadCacheLock.ExitWriteLock();
            }
            
            Log.LogDebug($"Deserialized {cellCount} grid cells, {totalPoints} road points");
            return true;
        }
        catch (System.Exception ex)
        {
            Log.LogWarning($"Failed to deserialize global road data: {ex.Message}");
            return false;
        }
    }

    #endregion
}
