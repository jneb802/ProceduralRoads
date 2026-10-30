using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

public static partial class RoadSpatialGrid
{
    // A durable chain of content hashes BEFORE each append: parents[k] is the
    // version before append k+1, so a compiler stamped parents[k] carries
    // additions 1..k and is owed every point with addition > k. Original v1/v2
    // networks have no chain.
    private static List<int> s_appendParents = new();
#if ROADS_TESTS
    internal static Action? BeforeAppendTransaction;
    internal static Action? AfterAppendPublication;
#endif
    public static int AppendCount => s_appendParents.Count;

    public static List<RoadPoint> PendingPoints(List<RoadPoint> points, int appliedVersion)
    {
        if (appliedVersion != 0 && appliedVersion == RoadNetworkVersion) return new List<RoadPoint>();
        int parent = appliedVersion == 0 ? -1 : s_appendParents.IndexOf(appliedVersion);
        if (parent < 0) return points; // A fresh compiler, or an explicit full regeneration.
        return points.FindAll(p => p.addition > parent);
    }

    /// <summary>
    /// The road zones whose points changed since <paramref name="older"/>, when the network in memory is
    /// <paramref name="older"/> with appends; null when it is another network.
    /// </summary>
    public static HashSet<Vector2s>? ZonesChangedSince(int older)
    {
        if (older == 0) return null;
        if (older == RoadNetworkVersion) return new HashSet<Vector2s>();
        if (!s_appendParents.Contains(older)) return null;
        var zones = new HashSet<Vector2s>();
        foreach (Vector2s zone in GetZonesWithRoadPoints())
            if (PendingPoints(GetRoadPointsInZone(zone), older).Count > 0) zones.Add(zone);
        return zones;
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
                    if (!point.cutOnly && seen.Add(point.p) && allowed(point.p)) result.Add(point.p);
        }
        finally { m_roadCacheLock.ExitReadLock(); }
        return result;
    }

    /// <summary>Plan first; publish the union once. Old arrays are retained until
    /// the new dictionary is complete, so an allocation failure cannot publish half a route. A road's earthwork goes in the
    /// same append, published with it or not at all: its staircases' and structures' cuts (<paramref name="cuts"/>, each
    /// group cut-only points reaching its width/2) and the other roads it raises (<paramref name="raised"/>,
    /// <see cref="SetRoadHeights"/>).</summary>
    public static void CommitAppend(IReadOnlyList<PlannedPath> plans, int expectedVersion, bool intoSite = false,
        IReadOnlyList<(IReadOnlyList<Vector3> at, float width)>? cuts = null,
        IReadOnlyList<(RoadPoint at, float to)>? raised = null) =>
        PublishAppend(expectedVersion, plans.Count == 0 ? "No road plans to commit." : null, (addition, newPoints) =>
        {
            float? length = 0f;
            foreach (var plan in plans)
            {
                length=AddCommittedLength(length,plan.TotalLength);
                for (int i = 0; i < plan.Points.Count; i++)
                    AddRoadPoint(newPoints, plan.Points[i], plan.Width, plan.Heights[i], plan.FollowTerrain, addition, intoSite: intoSite,
                        unpainted: plan.Unpainted != null && i < plan.Unpainted.Count && plan.Unpainted[i]);
            }
            if (cuts != null)
                foreach (var (at, width) in cuts)
                    foreach (Vector3 c in at)
                        AddRoadPoint(newPoints, new Vector2(c.x, c.z), width, c.y, paintOnly: false, addition, cutOnly: true);
            return length;
        }, raised);

    /// <summary>
    /// Add a staircase's earthwork as one append: a cut-only point per cut
    /// (x, z, the height to lower the ground to), reaching <paramref name="width"/>/2.
    /// The road terrain writer lowers the ground at them as their zones load,
    /// like any appended road, and on a dedicated server through the bake.
    /// An empty list commits nothing and leaves the version as it is.
    /// </summary>
    public static void CommitAppendCuts(IReadOnlyList<Vector3> cuts, float width, int expectedVersion)
    {
        if (cuts.Count == 0)
        {
            // Checked as any append is, but nothing is published.
            m_roadCacheLock.EnterReadLock();
            try { CheckAppend(expectedVersion); }
            finally { m_roadCacheLock.ExitReadLock(); }
            return;
        }
        PublishAppend(expectedVersion, null, (addition, newPoints) =>
        {
            foreach (Vector3 c in cuts)
                AddRoadPoint(newPoints, new Vector2(c.x, c.z), width, c.y, paintOnly: false, addition, cutOnly: true);
            return 0f;
        });
    }

    // Caller holds the grid lock.
    private static void CheckAppend(int expectedVersion)
    {
        if (RoadNetworkVersion != expectedVersion) throw new InvalidOperationException("The road network changed; plan the road again.");
        if (s_appendParents.Count >= ManualRoadDraft.MaxAppends) throw new InvalidOperationException("Manual road limit reached for this network.");
    }

    /// <summary>One append transaction: its points built and the union, statistics and version published
    /// together under the grid's write lock.</summary>
    private static void PublishAppend(int expectedVersion, string? empty,
        Func<int, Dictionary<Vector2i, List<RoadPoint>>, float?> build, IReadOnlyList<(RoadPoint at, float to)>? raised = null)
    {
        int worldSeed=WorldGenerator.instance?.GetSeed() ?? 0;
#if ROADS_TESTS
        BeforeAppendTransaction?.Invoke();
#endif
        m_roadCacheLock.EnterWriteLock();
        try
        {
            CheckAppend(expectedVersion);
            if (empty != null) throw new ArgumentException(empty);
            int addition = s_appendParents.Count + 1;
            var newPoints = new Dictionary<Vector2i, List<RoadPoint>>();
            float? length = build(addition, newPoints);
            NoteFiled(newPoints);
            var combined = NewCells(m_roadPoints);
            foreach (var cell in newPoints)
            {
                var points = combined.TryGetValue(cell.Key, out var old) ? new List<RoadPoint>(old) : new List<RoadPoint>();
                points.AddRange(cell.Value); combined[cell.Key] = points.ToArray();
            }
            if (raised != null && raised.Count > 0) Raise(combined, raised, addition);
            int added=0;
            foreach(var points in newPoints.Values) added=checked(added+points.Count);
            int total=checked(m_storedPointRecords+added);
            float? nextLength=AddCommittedLength(m_committedPathLength,length);
            var parents=new List<int>(s_appendParents) {expectedVersion};
            int nextVersion=ComputeRoadNetworkVersion(combined,worldSeed);
            s_appendParents=parents;
            m_roadPoints = combined;
            m_storedPointRecords=total; m_committedPathLength=nextLength;
            GridCellsWithRoads = combined.Count; m_initialized = true;
            RoadNetworkVersion=nextVersion;
        }
        finally { m_roadCacheLock.ExitWriteLock(); }
#if ROADS_TESTS
        AfterAppendPublication?.Invoke();
#endif
    }
}
