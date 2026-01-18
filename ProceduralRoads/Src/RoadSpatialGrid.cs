using System.Collections.Generic;
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

    public const float GridSize = RoadConstants.SpatialGridSize;
    public const float DefaultRoadWidth = RoadConstants.DefaultRoadWidth;

    private static Dictionary<Vector2i, RoadPoint[]> m_roadPoints = new Dictionary<Vector2i, RoadPoint[]>();
    private static RoadPoint[]? m_cachedRoadPoints;
    private static Vector2i m_cachedRoadGrid = new Vector2i(-999999, -999999);
    private static ReaderWriterLockSlim m_roadCacheLock = new ReaderWriterLockSlim();
    private static bool m_initialized = false;
    
    public static int TotalRoadPoints { get; private set; } = 0;
    public static int GridCellsWithRoads { get; private set; } = 0;
    public static float TotalRoadLength { get; private set; } = 0f;
    
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
        }
        finally
        {
            m_roadCacheLock.ExitWriteLock();
        }
    }

    public static void AddRoadPath(List<Vector2> path, float width, WorldGenerator worldGen)
    {
        if (path == null || path.Count < 2 || worldGen == null)
            return;

        float segmentLength = width / 4f;
        
        float totalLength = 0f;
        for (int i = 0; i < path.Count - 1; i++)
            totalLength += Vector2.Distance(path[i], path[i + 1]);
        
        List<Vector2> densePoints = new List<Vector2>();
        List<float> denseHeights = new List<float>();
        
        for (int i = 0; i < path.Count - 1; i++)
        {
            Vector2 start = path[i];
            Vector2 end = path[i + 1];
            Vector2 direction = (end - start).normalized;
            float segDist = Vector2.Distance(start, end);

            for (float t = 0; t <= segDist; t += segmentLength)
            {
                Vector2 point = start + direction * t;
                densePoints.Add(point);
                denseHeights.Add(worldGen.GetHeight(point.x, point.y));
            }
        }
        
        List<float> smoothedHeights = SmoothHeights(denseHeights, RoadConstants.HeightSmoothingWindow);
        
        int overlapCount = DetectOverlap(densePoints, width);
        if (overlapCount > densePoints.Count * RoadConstants.OverlapThreshold)
        {
            Log.LogInfo($"Road path overlaps with existing roads ({overlapCount}/{densePoints.Count} points), blending heights");
            BlendWithExistingRoads(densePoints, smoothedHeights, width);
        }
        
        Log.LogInfo($"Road path: {path.Count} waypoints -> {densePoints.Count} dense points");
        Log.LogInfo($"  Path length: {totalLength:F0}m, smoothing window: {RoadConstants.HeightSmoothingWindow} points");
        Log.LogInfo($"  Overlap: {overlapCount}/{densePoints.Count} points overlap existing roads");

        Dictionary<Vector2i, List<RoadPoint>> tempPoints = new Dictionary<Vector2i, List<RoadPoint>>();
        for (int i = 0; i < densePoints.Count; i++)
            AddRoadPoint(tempPoints, densePoints[i], width, smoothedHeights[i]);

        MergePoints(tempPoints);
        
        TotalRoadPoints += densePoints.Count;
        TotalRoadLength += totalLength;
        m_initialized = true;
    }
    
    private static List<float> SmoothHeights(List<float> heights, int windowSize)
    {
        List<float> smoothed = new List<float>(heights.Count);
        int halfWindow = windowSize / 2;
        
        for (int i = 0; i < heights.Count; i++)
        {
            float sum = 0f;
            int count = 0;
            
            for (int j = i - halfWindow; j <= i + halfWindow; j++)
            {
                if (j >= 0 && j < heights.Count)
                {
                    sum += heights[j];
                    count++;
                }
            }
            
            smoothed.Add(sum / count);
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
}
