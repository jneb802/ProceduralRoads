using System;
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
        /// <summary>Into a site: a road's last stretch onto the landing in front of a place
        /// (RoadSiteEntry). The terrain writer levels and paints toward these points inside
        /// a place's footprint too, which it leaves alone for every other point, and only
        /// within <see cref="RoadSiteEntry.InsideReach"/> of them there.</summary>
        public bool intoSite;

        public RoadPoint(Vector2 position, float width, float height, bool paintOnly = false, int addition = 0, bool cutOnly = false, bool intoSite = false)
        {
            p = position;
            w = width;
            w2 = width * width;
            h = height;
            this.paintOnly = paintOnly;
            this.addition = addition;
            this.cutOnly = cutOnly;
            this.intoSite = intoSite;
        }

        /// <summary>A point a road runs through: not a ford's paint-only point, not a stair's cut.</summary>
        public bool IsRoad => !paintOnly && !cutOnly;
        /// <summary>What the point is, as the network's identity counts it: road 0, paint-only 1, cut-only 2, into a site 3.</summary>
        public int Kind => paintOnly ? 1 : cutOnly ? 2 : intoSite ? 3 : 0;
    }

    public const float GridSize = RoadConstants.SpatialGridSize;
    public const float DefaultRoadWidth = RoadConstants.DefaultRoadWidth;

    private static Dictionary<Vector2i, RoadPoint[]> m_roadPoints = new Dictionary<Vector2i, RoadPoint[]>();
    private static RoadPoint[]? m_cachedRoadPoints;
    private static Vector2i m_cachedRoadGrid = new Vector2i(-999999, -999999);
    private static ReaderWriterLockSlim m_roadCacheLock = new ReaderWriterLockSlim();
    private static bool m_initialized = false;
    
    private static Dictionary<Vector2, RoadPointDebugInfo> m_debugInfo = new Dictionary<Vector2, RoadPointDebugInfo>();
    
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


    /// <summary>Compatibility alias. NaN means legacy committed length is unknown.
    /// New consumers should use the nullable Statistics projection.</summary>
    public static float TotalRoadLength=>CommittedPathLengthMeters ?? float.NaN;
    
    /// <summary>
    /// Version hash for the current road network. Changes when roads are regenerated.
    /// Used to detect if a zone's terrain mods are stale and need reapplication.
    /// </summary>
    public static int RoadNetworkVersion { get; private set; } = 0;
    
    private static ManualLogSource Log => ProceduralRoadsPlugin.ProceduralRoadsLogger;

    public static bool IsInitialized => m_initialized;

    /// <summary>
    /// Whether the network in memory was read from format 1, the only format a
    /// released build (1.4.x) can read. Such a network is never written back
    /// in a newer format (RoadNetworkGenerator.SaveGlobalRoadData), so the
    /// world still opens in the build that made it.
    /// </summary>
    public static bool LoadedFromReleasedFormat { get; private set; }

    public static void Clear()
    {
        m_roadCacheLock.EnterWriteLock();
        try
        {
            LoadedFromReleasedFormat = false;
            m_roadPoints.Clear();
            m_cachedRoadPoints = null;
            m_cachedRoadGrid = new Vector2i(-999999, -999999);
            m_initialized = false;
            m_storedPointRecords = 0;
            GridCellsWithRoads = 0;
            m_committedPathLength = 0f;
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
            m_debugInfo[densePoints[i]] = debugInfos[i];
        }

        MergePoints(tempPoints,totalLength,true);
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
        return c != 0 ? c : a.intoSite.CompareTo(b.intoSite);
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

    private static void MergePoints(Dictionary<Vector2i, List<RoadPoint>> tempPoints,float? committedLength=null,bool initialize=false)
    {
        m_roadCacheLock.EnterWriteLock();
        try
        {
            int added=0;
            foreach(var points in tempPoints.Values) added=checked(added+points.Count);
            int total=checked(m_storedPointRecords+added);
            float? length=AddCommittedLength(m_committedPathLength,committedLength);
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
                    m_roadPoints.Add(kvp.Key, kvp.Value.ToArray());
                }
            }
            
            m_storedPointRecords=total;
            m_committedPathLength=length;
            if(initialize) m_initialized=true;
            GridCellsWithRoads = m_roadPoints.Count;
            m_cachedRoadGrid = new Vector2i(-999999, -999999);
            m_cachedRoadPoints = null;
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

    /// <summary>The points stored in one 64 m grid cell (<see cref="GetRoadGrid"/>), or null. Lock-free like every point
    /// lookup.</summary>
    internal static RoadPoint[]? CellPoints(Vector2i cell) => m_roadPoints.TryGetValue(cell, out var points) ? points : null;

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

    public static List<RoadPoint> GetRoadPointsInZone(Vector2s zoneID)
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
    public static byte[]? SerializeZoneRoadPoints(Vector2s zoneID)
    {
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
    public static void DeserializeZoneRoadPoints(Vector2s zoneID, byte[] data)
    {
        if (data == null || data.Length == 0)
            return;

        try
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);
            
            int count = reader.ReadInt32();
            if (count <= 0 || count > 100000)
                return;
            
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
                if(!ManualRoadDraft.IsFinite(px) || !ManualRoadDraft.IsFinite(py) || !ManualRoadDraft.IsFinite(w) || w<=0 || !ManualRoadDraft.IsFinite(h)) return;
                list.Add(point);
            }
            if(ms.Position!=ms.Length) return;
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
            int added=0;
            foreach(var cell in pointsByGrid)
                if(!m_roadPoints.ContainsKey(cell.Key)) added=checked(added+cell.Value.Count);
            int total=checked(m_storedPointRecords+added);
            foreach (var kvp in pointsByGrid)
            {
                Vector2i grid = kvp.Key;
                var newPoints = kvp.Value;
                
                if (!m_roadPoints.ContainsKey(grid))
                {
                    m_roadPoints[grid] = newPoints.ToArray();
                    GridCellsWithRoads++;
                }
            }
            
            m_storedPointRecords=total;
            if(added>0) m_committedPathLength=null;
            m_initialized = true;
            
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
    /// Version 4: [4][geometry encoding 2-5][the old geometry payload after its version]
    /// [known-length byte 0 or 1][committed planned-path length:float]. Geometry/hash inputs are unchanged.
    /// Older Roads readers reject v4; this reader also accepts legacy v1-v3 with unknown nonempty length.
    /// Geometry encodings 4 (the cut-only flag) and 5 (the into-site flag) occur only inside the v4 envelope. Encodings 3
    /// and up carry the append block ([count][parent ids]); under encodings 4 and 5 it is written even when empty (count 0).
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
            
            // Version 2 adds the paint-only flag per point (waded fords), 3 the
            // append id, 4 the cut-only flag (staircase earthwork), 5 the
            // into-site flag (a road onto a place's landing). Each is
            // written only when the network needs it, so a road-only network
            // saves exactly as before.
            bool appended = s_appendParents.Count > 0;
            bool cuts = false, intoSite = false;
            foreach (var cell in m_roadPoints.Values)
                foreach (var rp in cell)
                {
                    cuts |= rp.cutOnly;
                    intoSite |= rp.intoSite;
                }
            int format = intoSite ? 5 : cuts ? 4 : appended ? 3 : 2;
            writer.Write(4);
            writer.Write(format);
            
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
                    if (format >= 4) writer.Write(rp.cutOnly);
                    if (format >= 5) writer.Write(rp.intoSite);
                    if (format >= 3) writer.Write(rp.addition);
                }
            }
            
            if (format >= 3)
            {
                writer.Write(s_appendParents.Count);
                foreach (int parent in s_appendParents) writer.Write(parent);
            }
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
    /// Why the last <see cref="DeserializeAllRoadPoints"/> refused its data: the envelope and
    /// geometry encoding it read, and the check that failed or the exception that stopped it.
    /// Null after a successful read. The unreadable-save error and a refused client
    /// snapshot name it, so a refusal says what this build could not read.
    /// </summary>
    public static string? LastReadRefusal { get; private set; }

    /// <summary>
    /// Deserialize the entire road network from a byte array.
    /// Clears existing data and replaces with loaded data.
    /// False (and <see cref="LastReadRefusal"/> says why) when the data is refused.
    /// </summary>
    public static bool DeserializeAllRoadPoints(byte[] data)
    {
        LastReadRefusal = null;
        int envelope = 0, version = 0;
        bool Refuse(string why)
        {
            LastReadRefusal = $"envelope {envelope}, encoding {version}: {why}";
            return false;
        }
        if (data == null || data.Length == 0)
            return Refuse("no data");

        // Left open by the reader, so a refusal can say how far the read got.
        var ms = new MemoryStream(data);
        try
        {
            using var reader = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true);
            
            envelope=reader.ReadInt32();
            version=envelope==4 ? reader.ReadInt32() : envelope;
            if ((envelope==4 && (version < 2 || version > 5)) || (envelope!=4 && (version < 1 || version > 3)))
                return Refuse("a format this build does not know (it reads envelope 4 with encodings 2-5, and formats 1-3 bare)");
            
            int cellCount = reader.ReadInt32();
            if (cellCount < 0 || cellCount > 1000000)
                return Refuse($"cell count {cellCount} out of range");
            
            var loadedPoints = new Dictionary<Vector2i, RoadPoint[]>(cellCount);
            int totalPoints = 0;
            // A released build (1.4.x, format 1) clamped widths and saved
            // averaged heights, but never checked them. One bad point there
            // costs that point, not the world its whole network.
            bool released = envelope == 1;
            int dropped = 0;
            
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
                    bool paintOnly = version >= 2 && reader.ReadBoolean();
                    bool cutOnly = version >= 4 && reader.ReadBoolean();
                    bool intoSite = version >= 5 && reader.ReadBoolean();
                    int addition = version >= 3 ? reader.ReadInt32() : 0;
                    if (!ManualRoadDraft.IsFinite(px) || !ManualRoadDraft.IsFinite(py) ||
                        !ManualRoadDraft.IsFinite(w) || w <= 0 || !ManualRoadDraft.IsFinite(h) || addition < 0)
                    {
                        if (!released)
                            return Refuse($"point {i} of cell ({gridX},{gridY}) has position ({px},{py}), width {w}, height {h}, " +
                                          $"addition {addition}: a non-finite value, no width or a negative addition");
                        dropped++;
                        continue;
                    }
                    points.Add(new RoadPoint(new Vector2(px, py), w, h, paintOnly, addition, cutOnly, intoSite));
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
            if (version >= 3)
            {
                // Encoding 3 is written only for a network with appends, so its block holds at
                // least one. Encoding 4 is written for any network with a staircase's cuts and
                // carries the block whether or not anything was appended: empty is valid there.
                int count = reader.ReadInt32();
                int least = version >= 4 ? 0 : 1;
                if (count < least || count > ManualRoadDraft.MaxAppends)
                    return Refuse($"append block holds {count} parent(s) ({least} to {ManualRoadDraft.MaxAppends} expected)");
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
            if(envelope==4)
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
            m_roadCacheLock.EnterWriteLock();
            try
            {
                int networkVersion=ComputeRoadNetworkVersion(loadedPoints,worldSeed);
                s_appendParents=parents;
                m_roadPoints = loadedPoints;
                m_cachedRoadGrid = new Vector2i(-999999, -999999);
                m_cachedRoadPoints = null;
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
