using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The bridge plans of the current network (bridges prototype), bucketed by
/// zone, and the record of which zones have received their pieces. Plans
/// are recomputed deterministically from the recorded crossings and the
/// world seed, never stored; the spawned-zone set is persisted with the
/// network (see RoadNetworkPersistence), so a zone whose pieces were all
/// destroyed does not get them again on the next load. Pure logic: the
/// game-facing spawning lives in BridgePlacement.
/// </summary>
public static class BridgePlans
{
    private static ManualLogSource Log => ProceduralRoadsPlugin.ProceduralRoadsLogger;

    private static Dictionary<Vector2s, List<BridgePiece>>? s_plansByZone;
    private static readonly HashSet<Vector2s> s_spawnedZones = new();

    /// <summary>Zones that have received their bridge pieces.</summary>
    public static IReadOnlyCollection<Vector2s> SpawnedZones => s_spawnedZones;

    /// <summary>Forget the plans and the spawned zones: the network is gone.</summary>
    public static void Reset()
    {
        s_plansByZone = null;
        s_spawnedZones.Clear();
    }

    /// <summary>Forget the plans only; they are recomputed on the next use.</summary>
    public static void InvalidatePlans() => s_plansByZone = null;

    /// <summary>Forget which zones have pieces (they are being destroyed).</summary>
    public static void ForgetSpawned() => s_spawnedZones.Clear();

    public static bool IsSpawned(Vector2s zone) => s_spawnedZones.Contains(zone);

    public static void MarkSpawned(Vector2s zone) => s_spawnedZones.Add(zone);

    public static void MarkSpawned(IEnumerable<Vector2s> zones)
    {
        foreach (Vector2s zone in zones)
            s_spawnedZones.Add(zone);
    }

    /// <summary>The marker a spawned bridge piece carries in its ZDO.</summary>
    public static readonly int MarkerHash = "ProceduralRoads_Bridge".GetStableHashCode();

    /// <summary>
    /// Whether the zone still holds bridge pieces that are STAYING -- markers
    /// belonging to pieces already condemned are not counted.
    ///
    /// The distinction is the whole point. ZDOMan.DestroyZDO does not remove a
    /// ZDO; it appends the id to m_destroySendList and the removal happens when
    /// that queue is processed. ZNetScene.Destroy drops the instance and then
    /// calls the same method, so it is queued too. A respawn that destroys the
    /// old pieces and immediately looks for duplicates therefore finds the
    /// pieces it has just condemned, concludes the zone is already built, and
    /// skips it -- after which the old pieces do disappear and nothing replaces
    /// them. Passing the condemned ids in is what keeps that from happening.
    ///
    /// This deliberately has no side effect: deciding a zone is already built
    /// and RECORDING that it is are separate acts, and the caller does the
    /// second one.
    /// </summary>
    public static bool ZoneHasLivePieces(Vector2s zoneID, ICollection<ZDOID>? condemned = null)
    {
        if (ZDOMan.instance == null)
            return false;
        var zdos = new List<ZDO>();
        ZDOMan.instance.FindObjects(zoneID, zdos, new HashSet<ZoneSystem.SectorIndex>());
        foreach (ZDO zdo in zdos)
        {
            if (zdo.GetInt(MarkerHash) != 1)
                continue;
            if (condemned != null && condemned.Contains(zdo.m_uid))
                continue;
            return true;
        }
        return false;
    }

    /// <summary>The pieces planned for a zone, or null when it has none.</summary>
    public static List<BridgePiece>? PlanFor(Vector2s zone)
    {
        EnsurePlans();
        return s_plansByZone != null && s_plansByZone.TryGetValue(zone, out List<BridgePiece>? list) ? list : null;
    }

    public static int PlannedPieceCount(Vector2s zone) => PlanFor(zone)?.Count ?? 0;

    public static int TotalPlannedPieces
    {
        get
        {
            EnsurePlans();
            int total = 0;
            if (s_plansByZone != null)
                foreach (var kv in s_plansByZone)
                    total += kv.Value.Count;
            return total;
        }
    }

    public static int PlannedZoneCount
    {
        get
        {
            EnsurePlans();
            return s_plansByZone?.Count ?? 0;
        }
    }

    /// <summary>Pieces the plan puts at a crossing (0 for a ford that is not spanned).</summary>
    public static int PiecesAt(RoadCrossing crossing) =>
        WorldGenerator.instance == null ? 0 : BridgeLayout.Solve(crossing, WorldGenerator.instance, WorldGenerator.instance.GetSeed()).Count;

    private static void EnsurePlans()
    {
        if (s_plansByZone != null || WorldGenerator.instance == null)
            return;

        s_plansByZone = new Dictionary<Vector2s, List<BridgePiece>>();
        int seed = WorldGenerator.instance.GetSeed();
        foreach (RoadCrossing crossing in BridgeLayout.DistinctSites(RoadNetworkGenerator.GetRoadCrossings()))
        {
            foreach (BridgePiece piece in BridgeLayout.Solve(crossing, WorldGenerator.instance, seed))
            {
                Vector2s zone = ZoneSystem.GetZone(piece.Position);
                if (!s_plansByZone.TryGetValue(zone, out List<BridgePiece>? list))
                {
                    list = new List<BridgePiece>();
                    s_plansByZone[zone] = list;
                }
                list.Add(piece);
            }
        }

        int total = 0;
        foreach (var kv in s_plansByZone)
            total += kv.Value.Count;
        if (total > 0)
            Log.LogInfo($"[BRIDGES] planned {total} pieces across {s_plansByZone.Count} zones");
    }
}
