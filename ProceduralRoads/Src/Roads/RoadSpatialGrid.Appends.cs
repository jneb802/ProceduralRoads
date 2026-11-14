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
    /// <summary>The zones each append of this session reaches beyond its road points (its committed scope: the zones its
    /// structures put pieces in, ManualRoads.ZonesReached), by its addition number. Not saved: a reload bakes every zone
    /// with roads or structures again (ServerTerrainBake), and the clearing each zone owes is saved with it
    /// (VegetationClearing.ClearedThrough).</summary>
    private static Dictionary<int, HashSet<Vector2s>> s_appendReach = new();
#if ROADS_TESTS
    internal static Action? BeforeAppendTransaction;
    internal static Action? AfterAppendPublication;
#endif
    public static int AppendCount => s_appendParents.Count;

    /// <summary>How many appends the network had at <paramref name="version"/>: <see cref="AppendCount"/> for the network as it
    /// is, k for the version before append k+1, -1 for a version that is not this network's (another network, or none).</summary>
    public static int AppendsCarriedBy(int version)
    {
        if (version == 0) return -1;
        if (version == RoadNetworkVersion) return s_appendParents.Count;
        return s_appendParents.IndexOf(version);
    }

    public static List<RoadPoint> PendingPoints(List<RoadPoint> points, int appliedVersion)
    {
        if (appliedVersion != 0 && appliedVersion == RoadNetworkVersion) return new List<RoadPoint>();
        int parent = appliedVersion == 0 ? -1 : s_appendParents.IndexOf(appliedVersion);
        if (parent < 0) return points; // A fresh compiler, or an explicit full regeneration.
        return points.FindAll(p => p.addition > parent);
    }

    /// <summary>
    /// The zones the appends since <paramref name="older"/> owe work, when the network in memory is <paramref name="older"/>
    /// with appends (null when it is another network): the zones whose road points changed, the zones whose vegetation their
    /// roads reach (RoadClearAreaManager.ZonesReached: a tree or log of a zone next to the road, over it), and the zones the
    /// appends' committed scope reaches (their structures' pieces, <see cref="CommitAppend"/>'s reached). The one owner of an
    /// append's affected zones: the server bake queues them, the clearing record owes them (VegetationClearing.AppendPublished).
    /// </summary>
    public static HashSet<Vector2s>? ZonesChangedSince(int older)
    {
        if (older == 0) return null;
        if (older == RoadNetworkVersion) return new HashSet<Vector2s>();
        int after = s_appendParents.IndexOf(older);
        if (after < 0) return null;
        var zones = new HashSet<Vector2s>();
        foreach (Vector2s zone in GetZonesWithRoadPoints())
            if (PendingPoints(GetRoadPointsInZone(zone), older).Count > 0) zones.Add(zone);
        zones.UnionWith(RoadClearAreaManager.ZonesReached(after));
        foreach (var reach in s_appendReach)
            if (reach.Key > after) zones.UnionWith(reach.Value);
        return zones;
    }

    /// <summary>Whether the appends since <paramref name="older"/> owe the zone work (as <see cref="ZonesChangedSince"/> reads
    /// it): for a version that is not this network's, whether it holds any road point.</summary>
    public static bool ZoneChangedSince(Vector2s zone, int older)
    {
        if (older == RoadNetworkVersion) return false;
        int after = older == 0 ? -1 : s_appendParents.IndexOf(older);
        if (after >= 0)
            foreach (var reach in s_appendReach)
                if (reach.Key > after && reach.Value.Contains(zone)) return true;
        return PendingPoints(GetRoadPointsInZone(zone), older).Count > 0 || RoadClearAreaManager.Reaches(zone, after);
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
                    if (point.Walked && seen.Add(point.p) && allowed(point.p)) result.Add(point.p);
        }
        finally { m_roadCacheLock.ExitReadLock(); }
        return result;
    }

    /// <summary>Plan first; publish the union once. Old arrays are retained until
    /// the new dictionary is complete, so an allocation failure cannot publish half a route. A road's earthwork goes in the
    /// same append, published with it or not at all: its staircases' and structures' cuts (<paramref name="cuts"/>, each
    /// group cut-only points reaching its width/2) and the other roads it raises (<paramref name="raised"/>,
    /// <see cref="SetRoadHeights"/>). <paramref name="reached"/>: the append's committed scope, every zone it owes work
    /// (ManualRoads.ZonesReached), published with it (<see cref="ZonesChangedSince"/>).</summary>
    public static void CommitAppend(IReadOnlyList<PlannedPath> plans, int expectedVersion, bool intoLocation = false,
        IReadOnlyList<(IReadOnlyList<Vector3> at, float width)>? cuts = null,
        IReadOnlyList<(RoadPoint at, float to)>? raised = null, IReadOnlyList<PlannedPath>? intoLocationPlans = null,
        IEnumerable<Vector2s>? reached = null) =>
        PublishAppend(expectedVersion, plans.Count == 0 ? "No road plans to commit." : null, (addition, newPoints) =>
        {
            float? length = 0f;
            foreach (var plan in plans)
            {
                length=AddCommittedLength(length,plan.TotalLength);
                AddPlan(newPoints, plan, addition, intoLocation);
            }
            // Into a location's location, as generation stores a way onto its landing (RoadSpatialGrid.CommitRoad, intoLocation).
            if (intoLocationPlans != null)
                foreach (var plan in intoLocationPlans)
                {
                    length=AddCommittedLength(length,plan.TotalLength);
                    AddPlan(newPoints, plan, addition, true);
                }
            if (cuts != null)
                foreach (var (at, width) in cuts)
                    foreach (Vector3 c in at)
                        AddRoadPoint(newPoints, new Vector2(c.x, c.z), width, c.y, paintOnly: false, addition, cutOnly: true);
            return length;
        }, raised, reached);

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
        Func<int, Dictionary<Vector2i, List<RoadPoint>>, float?> build, IReadOnlyList<(RoadPoint at, float to)>? raised = null,
        IEnumerable<Vector2s>? reached = null)
    {
        int worldSeed=WorldGenerator.instance?.GetSeed() ?? 0;
        RoadNetworkGenerator.IslandNetworkChanged();
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
            if (reached != null) s_appendReach[addition] = new HashSet<Vector2s>(reached);
            m_roadPoints = combined;
            m_storedPointRecords=total; m_committedPathLength=nextLength;
            GridCellsWithRoads = combined.Count; m_initialized = true;
            RoadNetworkVersion=nextVersion;
        }
        finally { m_roadCacheLock.ExitWriteLock(); }
        // The clearing record moves with the network at once, before any bake or save reads it.
        VegetationClearing.AppendPublished(expectedVersion);
#if ROADS_TESTS
        AfterAppendPublication?.Invoke();
#endif
    }
}
