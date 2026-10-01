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
        /// <summary>Paint only: the terrain is not leveled toward this point
        /// (a waded ford keeps the riverbed as it is).</summary>
        public bool paintOnly;

        public RoadPoint(Vector2 position, float width, float height, bool paintOnly = false)
        {
            p = position;
            w = width;
            w2 = width * width;
            h = height;
            this.paintOnly = paintOnly;
        }
    }

    public const float GridSize = RoadConstants.SpatialGridSize;
    public const float DefaultRoadWidth = RoadConstants.DefaultRoadWidth;

    private static Dictionary<Vector2i, RoadPoint[]> m_roadPoints = new Dictionary<Vector2i, RoadPoint[]>();
    private static ReaderWriterLockSlim m_roadCacheLock = new ReaderWriterLockSlim();
    private static bool m_initialized = false;
    
    private static Dictionary<Vector2, RoadPointDebugInfo> m_debugInfo = new Dictionary<Vector2, RoadPointDebugInfo>();
    private static readonly object m_recordGate = new object();
    
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

    /// <summary>
    /// A road decided but not yet stored: every dense point and the height it
    /// will carry, already ramped, already inside the grade cap.
    ///
    /// Planning and storing are apart because a road can be refused, and a
    /// caller that lays one road in several pieces - the land either side of
    /// a river crossing, say - has to know that every piece is buildable
    /// before it stores the first. Half a road in the grid is worse than none:
    /// it is a paved stretch that stops in open ground.
    /// </summary>
    public sealed class PlannedPath
    {
        public readonly List<Vector2> Points;
        public readonly List<float> Heights;
        public readonly List<RoadPointDebugInfo> DebugInfos;
        public readonly float Width;
        public readonly float TotalLength;
        public readonly bool FollowTerrain;

        internal PlannedPath(List<Vector2> points, List<float> heights,
            List<RoadPointDebugInfo> debugInfos, float width, float totalLength, bool followTerrain)
        {
            Points = points; Heights = heights; DebugInfos = debugInfos;
            Width = width; TotalLength = totalLength; FollowTerrain = followTerrain;
        }
    }

    /// <summary>
    /// Work out what a road would be, without storing any of it.
    ///
    /// startGround and endGround are the heights the road has to MEET at its
    /// two ends - the ground a location stands on - where those are known.
    /// Without them each end meets the natural terrain under it, as before.
    ///
    /// followTerrain: paint only; every point keeps the raw terrain height and
    /// the terrain is not leveled toward it at all (a WADED ford). Such a road
    /// is the ground, so there is nothing for the grade cap to hold: it is the
    /// water's bed, and it is crossed, not climbed.
    /// minHeight: no stored point below it (a RAISED ford's surface).
    ///
    /// Returns null when the profile cannot be built inside the grade cap:
    /// the two ends are further apart in height than the cap allows over the
    /// length between them. That is a road too steep to walk, and the
    /// caller's business is to drop it, not to lay it anyway.
    /// </summary>
    public static PlannedPath? PlanRoadPath(List<Vector2> path, float width, WorldGenerator worldGen,
        float? startGround = null, float? endGround = null,
        bool followTerrain = false, float minHeight = float.NegativeInfinity,
        System.Func<Vector2, float>? terrainHeight = null)
    {
        if (path == null || path.Count < 2 || worldGen == null)
            return null;

        // Trial profiles share procedural samples, never authored location ground.
        float SampleTerrain(Vector2 point) => terrainHeight != null ? terrainHeight(point)
            : BiomeBlendedHeight.GetBlendedHeight(point.x, point.y, worldGen);
        float segmentLength = width / 4f;

        float totalLength = 0f;
        for (int i = 0; i < path.Count - 1; i++)
            totalLength += Vector2.Distance(path[i], path[i + 1]);

        List<Vector2>? turns = null; List<bool>? landings = null;
        if (!followTerrain && !RoadSwitchbacks.Shape(path, width, out turns, out landings, SampleTerrain))
        { Log.LogDebug("Road refused: switchback has no room for a turning landing"); return null; }
        List<Vector2> densePoints = turns ?? SplinePath(path, segmentLength);
        if (turns != null)
        {
            totalLength = 0f;
            for (int i=1;i<densePoints.Count;i++)
            {
                totalLength += Vector2.Distance(densePoints[i-1],densePoints[i]);
                if (RoadSiteProtection.BlocksSegment(densePoints[i-1],densePoints[i],width*0.5f+2f,null,null))
                    return null;
                worldGen.GetRiverWeight(densePoints[i].x,densePoints[i].y,out float river,out _);
                if (river>RoadConstants.RiverImpassableThreshold ||
                    worldGen.GetHeight(densePoints[i].x,densePoints[i].y)<RoadConstants.ShallowWaterHeight) return null;
            }
        }
        float[]? edgeGrades = null;
        if (landings != null)
        {
            edgeGrades = new float[densePoints.Count];
            for(int i=0;i<edgeGrades.Length;i++) edgeGrades[i] = landings[i] || (i>0 && landings[i-1])
                ? Mathf.Min(RoadGrade.Configured,RoadSwitchbacks.LandingGrade) : RoadGrade.Configured;
        }
        List<float> denseHeights = new List<float>(densePoints.Count);

        foreach (var point in densePoints)
            denseHeights.Add(SampleTerrain(point));

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

        // Endpoint ramps: near each end of the road the final height blends
        // from the ground the end has to meet toward the smoothed road
        // height, so roads meet locations and terrain without a smoothed ledge.
        float[] distanceFromStart = new float[densePoints.Count];
        for (int i = 1; i < densePoints.Count; i++)
            distanceFromStart[i] = distanceFromStart[i - 1] + Vector2.Distance(densePoints[i - 1], densePoints[i]);
        float pathTotal = densePoints.Count > 0 ? distanceFromStart[densePoints.Count - 1] : 0f;

        List<float> finalHeights = new List<float>(densePoints.Count);
        for (int i = 0; i < densePoints.Count; i++)
        {
            float fromStart = distanceFromStart[i];
            float fromEnd = pathTotal - fromStart;
            float distFromNearestEnd = Mathf.Min(fromStart, fromEnd);
            float? target = fromStart <= fromEnd ? startGround : endGround;
            float rampBase = RoadEndpointRamp.BaseHeight(denseHeights[i], target, distFromNearestEnd);
            finalHeights.Add(followTerrain
                ? denseHeights[i]
                : Mathf.Max(Mathf.Lerp(rampBase, smoothedHeights[i], RoadEndpointRamp.Blend(distFromNearestEnd)), minHeight));
        }

        // Smoothing and the ramp both move heights after the search priced the
        // ground, so a route the search accepted can still be built too steep.
        // This is where that is caught, and the ends are held: they are the
        // heights the road has to meet. A waded ford is exempt: it is painted
        // on the ground rather than built, so its profile is the river bed's
        // and holding it to a road's grade would mean levelling the river.
        if (!followTerrain)
        {
            float steepestBefore = RoadGrade.SteepestStep(densePoints, finalHeights);
            if (!RoadGrade.Limit(densePoints, finalHeights, RoadGrade.Configured, edgeGrades))
            {
                Log.LogDebug(
                    $"Road profile refused: ends {finalHeights[0]:F1}m and {finalHeights[finalHeights.Count - 1]:F1}m " +
                    $"are {Mathf.Abs(finalHeights[finalHeights.Count - 1] - finalHeights[0]):F1}m apart over {pathTotal:F0}m, " +
                    $"over the {RoadGrade.Configured:P0} cap including turn landings");
                return null;
            }
            float steepestAfter = RoadGrade.SteepestStep(densePoints, finalHeights);
            RoadGrade.RecordSteepest(steepestAfter);
            if (steepestAfter < steepestBefore - 0.001f)
                Log.LogDebug($"  Grade limited: steepest step {steepestBefore:P0} -> {steepestAfter:P0}");

            // The limiter may cut a point below a raised ford's surface; the
            // floor is a constant, so putting it back leaves the profile
            // inside the cap.
            if (minHeight > float.NegativeInfinity)
                for (int k = 0; k < finalHeights.Count; k++)
                    finalHeights[k] = Mathf.Max(finalHeights[k], minHeight);
        }

        if (!followTerrain && turns != null && !RoadSwitchbacks.Separated(densePoints, finalHeights, width))
        { Log.LogDebug("Road refused: switchback legs blend at different heights"); return null; }
        return new PlannedPath(densePoints, finalHeights, debugInfos, width, totalLength, followTerrain);
    }

    /// <summary>Store a planned road. Nothing here can fail; every decision
    /// was made in PlanRoadPath.</summary>
    public static void Commit(PlannedPath plan)
    {
        Dictionary<Vector2i, List<RoadPoint>> tempPoints = new Dictionary<Vector2i, List<RoadPoint>>();
        for (int i = 0; i < plan.Points.Count; i++)
        {
            AddRoadPoint(tempPoints, plan.Points[i], plan.Width, plan.Heights[i], plan.FollowTerrain);

            RoadPointDebugInfo debugInfo = plan.DebugInfos[i];
            debugInfo.SmoothedHeight = plan.Heights[i];
            lock (m_recordGate) m_debugInfo[plan.Points[i]] = debugInfo;
        }

        MergePoints(tempPoints);

        // Read-modify-write from several islands at once: without the gate
        // these silently lose updates.
        lock (m_recordGate)
        {
            TotalRoadPoints += plan.Points.Count;
            TotalRoadLength += plan.TotalLength;
            m_initialized = true;
        }
    }

    /// <summary>Road points levelled to the given heights and painted, stored
    /// without planning (the ground under a bridge deck's ends).</summary>
    public static void CommitLevelled(IList<Vector2> points, IList<float> heights, float width)
    {
        var temp = new Dictionary<Vector2i, List<RoadPoint>>();
        for (int i = 0; i < points.Count; i++)
            AddRoadPoint(temp, points[i], width, heights[i], false);
        MergePoints(temp);
    }

    /// <summary>Plan a road and store it, for the caller that lays one road in
    /// one piece. False means it was refused and nothing was stored.</summary>
    public static bool AddRoadPath(List<Vector2> path, float width, WorldGenerator worldGen,
        float? startGround = null, float? endGround = null,
        bool followTerrain = false, float minHeight = float.NegativeInfinity)
    {
        PlannedPath? plan = PlanRoadPath(path, width, worldGen, startGround, endGround, followTerrain, minHeight);
        if (plan == null) return false;
        Commit(plan);
        return true;
    }

    /// <summary>
    /// Called after all roads are generated, and after a network is loaded from
    /// the save, to compute the network version: a hash of the world seed and
    /// every stored road point (position, width, height, paint-only flag)
    /// in canonical order (cells by coordinate, points by position, width,
    /// height, flag), each record
    /// mixed into the running value, so it is the same after generation and
    /// after a save/load round trip (which carries exactly the stored points)
    /// and changes whenever any road moves or changes height. A sum of
    /// per-point hashes was tried first and let balanced height changes
    /// cancel (a road regraded from flat to a slope kept its version).
    /// RoadTerrainModifier stamps it on each zone's terrain compiler to tell
    /// zones that already carry the current roads from zones that still need
    /// them.
    /// </summary>
    public static void FinalizeRoadNetwork()
    {
        int worldSeed = WorldGenerator.instance?.GetSeed() ?? 0;
        uint hash = 2166136261u; // FNV offset basis
        int storedPoints = 0;
        int cells = 0;

        m_roadCacheLock.EnterReadLock();
        try
        {
            var keys = new List<Vector2i>(m_roadPoints.Keys);
            keys.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            var records = new List<RoadPoint>();
            foreach (var key in keys)
            {
                cells++;
                Mix(ref hash, key.x);
                Mix(ref hash, key.y);
                records.Clear();
                records.AddRange(m_roadPoints[key]);
                records.Sort(CompareRecords);
                foreach (var rp in records)
                {
                    storedPoints++;
                    Mix(ref hash, rp.p.x.GetHashCode());
                    Mix(ref hash, rp.p.y.GetHashCode());
                    Mix(ref hash, rp.w.GetHashCode());
                    Mix(ref hash, rp.h.GetHashCode());
                    Mix(ref hash, rp.paintOnly ? 1 : 0);
                }
            }
        }
        finally
        {
            m_roadCacheLock.ExitReadLock();
        }

        Mix(ref hash, worldSeed);
        Mix(ref hash, storedPoints);
        Mix(ref hash, cells);
        int version = unchecked((int)hash);
        if (version == 0)
            version = 1; // 0 means "no network"

        RoadNetworkVersion = version;
        Log.LogDebug($"Road network finalized: version={RoadNetworkVersion}, points={TotalRoadPoints}, cells={GridCellsWithRoads}");
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
        return c != 0 ? c : a.paintOnly.CompareTo(b.paintOnly);
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
            if (windowStart > i - halfWindow || windowEnd < i + halfWindow)
            {
                // Within half a window of either end the window is one-sided,
                // and the mean of a one-sided window on a slope is the height
                // some way back along the road: the road arrived at its ends
                // on a ledge (uphill) or a hump (downhill) as high as the
                // slope times the missing half window. Fit a line through the
                // window instead and read it at this point, which smooths the
                // same bumps but is exact on a slope. Mid-road the window is
                // symmetric and the line's value there is the mean, so the
                // mean's arithmetic is kept unchanged.
                smoothedHeight = LineFitAt(heights, windowStart, windowEnd, i, smoothedHeight);
            }
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

    /// <summary>
    /// Least-squares line through heights[start..end] against the point index,
    /// evaluated at index at; the mean is returned when the window has fewer
    /// than two points.
    /// </summary>
    private static float LineFitAt(List<float> heights, int start, int end, int at, float mean)
    {
        int n = end - start + 1;
        if (n < 2)
            return mean;

        double sx = 0, sxx = 0, sh = 0, sxh = 0;
        for (int j = start; j <= end; j++)
        {
            double x = j - at;
            double h = heights[j];
            sx += x;
            sxx += x * x;
            sh += h;
            sxh += x * h;
        }

        double det = n * sxx - sx * sx;
        if (det <= 0)
            return mean;
        return (float)((sxx * sh - sx * sxh) / det);
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

    private static void AddRoadPoint(Dictionary<Vector2i, List<RoadPoint>> roadPoints, Vector2 p, float width, float height, bool paintOnly)
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
                    list.Add(new RoadPoint(p, width, height, paintOnly));
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

    /// <summary>The road weight at a point: how much of a road covers it and
    /// how wide that road is.
    ///
    /// There is no cache in front of this. There used to be a single shared
    /// one-entry cache of the last cell looked at, which worked when one
    /// thread walked the world in order and became a liability the moment
    /// several islands were built at once: each worker is somewhere else
    /// entirely, so every call missed, and installing the new entry took an
    /// EXCLUSIVE lock that every other worker had to wait behind. It was also
    /// a way to publish a stale answer -- a thread could read a cell's array,
    /// be overtaken by a commit that replaced it, and then install the array
    /// it had read as the cache for everyone.
    ///
    /// What is left is one short read lock around the dictionary, because
    /// other islands are committing into it. The array it hands back is never
    /// modified in place -- a commit replaces a cell's array with a new one --
    /// so the arithmetic runs outside the lock, on an array that cannot change
    /// underneath it.</summary>
    public static void GetRoadWeight(float wx, float wy, out float weight, out float width)
    {
        Vector2i grid = GetRoadGrid(wx, wy);

        RoadPoint[]? points;
        m_roadCacheLock.EnterReadLock();
        try { m_roadPoints.TryGetValue(grid, out points); }
        finally { m_roadCacheLock.ExitReadLock(); }

        if (points == null)
        {
            weight = 0f;
            width = 0f;
            return;
        }
        GetWeight(points, wx, wy, out weight, out width);
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
                            // Pad by how far this point's levelling can REACH,
                            // not by its width: once the side slope widens with
                            // the cut or the fill the two diverge, and a point
                            // left out still pulls on vertices inside this
                            // zone, which leaves a seam along the zone edge.
                            // Width stays as a floor for a wide road.
                            float reach = Mathf.Max(rp.w, RoadTerrainModifier.GatherRadius(rp.w));
                            if (rp.p.x >= zonePos.x - RoadConstants.HalfZoneSize - reach &&
                                rp.p.x <= zonePos.x + RoadConstants.HalfZoneSize + reach &&
                                rp.p.y >= zonePos.z - RoadConstants.HalfZoneSize - reach &&
                                rp.p.y <= zonePos.z + RoadConstants.HalfZoneSize + reach)
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
                list.Add(point);
            }
            
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
            }
            
            GridCellsWithRoads = m_roadPoints.Count;
            m_initialized = true;
            
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
            // Zero cells is a saved network too. Writing its header replaces
            // an older nonempty blob and distinguishes it from no saved data.
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            
            // Version 2 adds the paint-only flag per point (waded fords).
            writer.Write(2);
            
            writer.Write(m_roadPoints.Count);
            
            foreach (var kvp in m_roadPoints)
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
            if (version != 1 && version != 2)
            {
                Log.LogWarning($"Unknown road data version: {version}");
                return false;
            }
            
            int cellCount = reader.ReadInt32();
            if (cellCount < 0 || cellCount > 1000000)
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
                
                if (pointCount < 0 || pointCount > 100000)
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
                    bool paintOnly = version >= 2 && reader.ReadBoolean();
                    points[i] = new RoadPoint(new Vector2(px, py), w, h, paintOnly);
                }
                
                loadedPoints[new Vector2i(gridX, gridY)] = points;
                totalPoints += pointCount;
            }
            
            m_roadCacheLock.EnterWriteLock();
            try
            {
                m_roadPoints = loadedPoints;
                m_initialized = true;
                GridCellsWithRoads = loadedPoints.Count;
                TotalRoadPoints = totalPoints;
            }
            finally
            {
                m_roadCacheLock.ExitWriteLock();
            }
            
            Log.LogDebug($"Deserialized {cellCount} grid cells, {totalPoints} road points");
            FinalizeRoadNetwork();
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
