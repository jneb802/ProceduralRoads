using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

public static partial class RoadSpatialGrid
{
    // A durable chain of content hashes BEFORE each append. Point addition N
    // was introduced after parent[N-1]. Original v1/v2 networks have no chain.
    private static readonly List<int> s_appendParents = new();
    public static int AppendCount => s_appendParents.Count;

    public static List<RoadPoint> PendingPoints(List<RoadPoint> points, int appliedVersion)
    {
        if (appliedVersion != 0 && appliedVersion == RoadNetworkVersion) return new List<RoadPoint>();
        int parent = appliedVersion == 0 ? -1 : s_appendParents.IndexOf(appliedVersion);
        if (parent < 0) return points; // A fresh compiler, or an explicit full regeneration.
        return points.FindAll(p => p.addition > parent);
    }

    public static List<Vector2> RoadTargets(Func<Vector2, bool> allowed)
    {
        var seen = new HashSet<Vector2>();
        var result = new List<Vector2>();
        m_roadCacheLock.EnterReadLock();
        try
        {
            foreach (var cell in m_roadPoints.Values)
                foreach (var point in cell)
                    if (seen.Add(point.p) && allowed(point.p)) result.Add(point.p);
        }
        finally { m_roadCacheLock.ExitReadLock(); }
        return result;
    }

    /// <summary>Plan first; publish the union once. Old arrays are retained until
    /// the new dictionary is complete, so an allocation failure cannot publish half a route.</summary>
    public static void CommitAppend(IReadOnlyList<PlannedPath> plans, int expectedVersion)
    {
        if (RoadNetworkVersion != expectedVersion) throw new InvalidOperationException("The road network changed; plan the road again.");
        if (s_appendParents.Count >= ManualRoadDraft.MaxAppends) throw new InvalidOperationException("Manual road limit reached for this network.");
        if (plans.Count == 0) throw new ArgumentException("No road plans to commit.");
        int addition = s_appendParents.Count + 1;
        var newPoints = new Dictionary<Vector2i, List<RoadPoint>>();
        float length = 0; int count = 0;
        foreach (var plan in plans)
        {
            length += plan.TotalLength; count += plan.Points.Count;
            for (int i = 0; i < plan.Points.Count; i++)
                AddRoadPoint(newPoints, plan.Points[i], plan.Width, plan.Heights[i], plan.FollowTerrain, addition);
        }
        var combined = new Dictionary<Vector2i, RoadPoint[]>(m_roadPoints);
        foreach (var cell in newPoints)
        {
            var points = combined.TryGetValue(cell.Key, out var old) ? new List<RoadPoint>(old) : new List<RoadPoint>();
            points.AddRange(cell.Value); combined[cell.Key] = points.ToArray();
        }
        m_roadCacheLock.EnterWriteLock();
        try
        {
            s_appendParents.Add(expectedVersion);
            m_roadPoints = combined;
            TotalRoadPoints += count; TotalRoadLength += length;
            GridCellsWithRoads = combined.Count; m_initialized = true;
        }
        finally { m_roadCacheLock.ExitWriteLock(); }
        FinalizeRoadNetwork();
    }
}
