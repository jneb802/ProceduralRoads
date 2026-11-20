using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The structure plans of the current network -- the pieces the network
/// builds out of vanilla prefabs, bridges today -- bucketed by zone, and the
/// record of which zones have received their pieces. The one owner of
/// structures: placement, appends, the server bake and the zone report all
/// read from here. Plans are recomputed deterministically from the network
/// and the world seed, but for the structures saved with it (a staircase, a
/// bridge whose first zone was built: BuiltBridges); the spawned-zone set is persisted with
/// the network (see RoadNetworkPersistence; its saved key and the pieces'
/// ZDO marker keep their bridge names, so existing worlds load unchanged), so
/// a zone whose pieces were all destroyed does not get them again on the next
/// load. Pure logic: the game-facing spawning lives in StructurePlacement.
/// </summary>
public static class StructurePlans
{
    private static ManualLogSource Log => ProceduralRoadsPlugin.ProceduralRoadsLogger;

    /// <summary>Every kind of structure the network builds. Each plans its own
    /// pieces; everything after that is shared.</summary>
    public static readonly IReadOnlyList<IStructureSource> Sources = new IStructureSource[] { new BridgeStructures(), new StairStructures() };

    private static Dictionary<Vector2s, List<StructurePiece>>? s_plansByZone;
    /// <summary>The same pieces, by source and then zone: clear areas are each source's own business.</summary>
    private static Dictionary<IStructureSource, Dictionary<Vector2s, List<StructurePiece>>>? s_plansBySource;
    private static readonly HashSet<Vector2s> s_spawnedZones = new();

    /// <summary>Zones that have received their bridge pieces.</summary>
    public static IReadOnlyCollection<Vector2s> SpawnedZones => s_spawnedZones;

    /// <summary>Forget the plans and the spawned zones: the network is gone.</summary>
    public static void Reset()
    {
        s_plansByZone = null;
        s_plansBySource = null;
        s_spawnedZones.Clear();
    }

    /// <summary>Forget the plans only; they are recomputed on the next use.</summary>
    public static void InvalidatePlans()
    {
        s_plansByZone = null;
        s_plansBySource = null;
    }

    /// <summary>Forget which zones have pieces (they are being destroyed).</summary>
    public static void ForgetSpawned() => s_spawnedZones.Clear();

    /// <summary>These zones' emitted pieces are being destroyed for a partial respawn: they are forgotten, so each gets its
    /// whole plan again.</summary>
    public static void ForgetSpawned(ICollection<Vector2s> zones) => s_spawnedZones.ExceptWith(zones);

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
    /// Whether the zone still holds bridge pieces that are staying: markers of
    /// pieces already condemned (<paramref name="condemned"/>) are not counted,
    /// since a destroyed ZDO is only queued for removal. No side effect: the
    /// caller records the zone as built.
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
    public static List<StructurePiece>? PlanFor(Vector2s zone)
    {
        EnsurePlans();
        return s_plansByZone != null && s_plansByZone.TryGetValue(zone, out List<StructurePiece>? list) ? list : null;
    }

    /// <summary>
    /// The pieces planned for a zone that may be built now, or null when there are none. A bridge that would put
    /// any of its pieces (ends, piers, deck or stairs; debris on the riverbed does not count) in a zone
    /// NetworkCutoff leaves as it is is not built at all, in any zone: no half bridge appears, and the road there is
    /// left unbridged. Asked at spawn time, so if those zones stop being excluded the whole bridge can be built.
    /// </summary>
    public static List<StructurePiece>? BuildablePlanFor(Vector2s zone) => Buildable(PlanFor(zone));

    /// <summary>The pieces of a list that may be built now (see BuildablePlanFor), or null when none may.</summary>
    private static List<StructurePiece>? Buildable(List<StructurePiece>? pieces)
    {
        if (pieces == null || NetworkCutoff.ExcludedCount == 0)
            return pieces;
        List<StructurePiece>? buildable = null;
        for (int i = 0; i < pieces.Count; i++)
        {
            HashSet<Vector2s>? zones = pieces[i].StructureZones;
            bool whole = zones == null || !ReachesExcludedZone(zones);
            if (buildable != null) { if (whole) buildable.Add(pieces[i]); }
            else if (!whole) buildable = pieces.GetRange(0, i);
        }
        if (buildable == null)
            return pieces;
        return buildable.Count > 0 ? buildable : null;
    }

    private static bool ReachesExcludedZone(HashSet<Vector2s> zones)
    {
        foreach (Vector2s zone in zones)
            if (NetworkCutoff.Excludes(zone))
                return true;
        return false;
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

    /// <summary>The zones the plans put pieces in (a copy).</summary>
    public static List<Vector2s> PlannedZones
    {
        get
        {
            EnsurePlans();
            return s_plansByZone != null ? new List<Vector2s>(s_plansByZone.Keys) : new List<Vector2s>();
        }
    }

    /// <summary>
    /// The planned zones a respawn has to put pieces back into: every zone the
    /// world has already generated and which is not recorded as spawned. Asked
    /// of the world, not of the zones with a Heightmap: a dedicated server keeps
    /// its players' surroundings as ghost zones with none. A zone not generated
    /// yet gets its pieces when it is generated, from the zone-spawn hook.
    /// </summary>
    public static List<Vector2s> ZonesNeedingPieces(Func<Vector2s, bool> isZoneGenerated)
    {
        List<Vector2s> zones = new();
        foreach (Vector2s zone in PlannedZones)
        {
            if (IsSpawned(zone) || !isZoneGenerated(zone))
                continue;
            zones.Add(zone);
        }
        return zones;
    }

    /// <summary>Circles to keep vegetation out of round a zone's planned pieces, from every source.</summary>
    public static List<(Vector3 centre, float radius)> ClearAreas(Vector2s zone)
    {
        var areas = new List<(Vector3 centre, float radius)>();
        EnsurePlans();
        if (s_plansBySource == null)
            return areas;
        foreach (IStructureSource source in Sources)
            if (s_plansBySource.TryGetValue(source, out var byZone) && byZone.TryGetValue(zone, out List<StructurePiece>? planned)
                && Buildable(planned) is { } pieces)
                source.AddClearAreas(pieces, areas);
        return areas;
    }

    /// <summary>Where a walker on any structure needs rock cleared, within reach of a point.</summary>
    public static List<StructureClearance> ClearancesNear(Vector3 centre, float radius)
    {
        var clearances = new List<StructureClearance>();
        foreach (IStructureSource source in Sources)
            source.AddClearances(centre, radius, clearances);
        return clearances;
    }

    /// <summary>Whether any structure of any source needs rock cleared within reach of a point.</summary>
    public static bool AnyStructureNear(Vector3 centre, float radius) => ClearancesNear(centre, radius).Count > 0;

    private static void EnsurePlans()
    {
        if (RoadLifecycleManager.WorkersOwnWorld) throw new InvalidOperationException("Road generation is in progress.");
        if (s_plansByZone != null || WorldGenerator.instance == null)
            return;

        // Built whole, then published: a source that throws leaves no half-built plan behind for the next caller.
        var plansByZone = new Dictionary<Vector2s, List<StructurePiece>>();
        var plansBySource = new Dictionary<IStructureSource, Dictionary<Vector2s, List<StructurePiece>>>();
        int seed = WorldGenerator.instance.GetSeed();
        foreach (IStructureSource source in Sources)
        {
            var byZone = new Dictionary<Vector2s, List<StructurePiece>>();
            plansBySource[source] = byZone;
            int total = 0;
            foreach (StructurePiece piece in source.Plan(WorldGenerator.instance, seed))
            {
                Vector2s zone = ZoneSystem.GetZone(piece.Position);
                if (!plansByZone.TryGetValue(zone, out List<StructurePiece>? list))
                {
                    list = new List<StructurePiece>();
                    plansByZone[zone] = list;
                }
                list.Add(piece);
                if (!byZone.TryGetValue(zone, out List<StructurePiece>? own))
                {
                    own = new List<StructurePiece>();
                    byZone[zone] = own;
                }
                own.Add(piece);
                total++;
            }
            if (total > 0)
                Log.LogInfo($"{source.LogTag} planned {total} pieces across {byZone.Count} zones");
        }
        if (SearchStats.Enabled) Log.LogInfo($"[BRIDGES] site solves: {BridgeStructures.SolveTimes.Summary()}");
        s_plansBySource = plansBySource;
        s_plansByZone = plansByZone;
    }
}
