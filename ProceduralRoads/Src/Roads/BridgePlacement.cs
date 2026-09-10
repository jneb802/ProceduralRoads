using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Spawns the bridge plans (bridges prototype, see BridgePlans) into the
/// world as persistent ZDOs of vanilla prefabs, so unmodded clients see
/// them too. Only the server creates or destroys pieces: clients receive
/// them like any other object. A zone gets its pieces once, when it first
/// comes alive with the network in place; the zone is then recorded in the
/// persisted spawned set, and every piece carries a ZDO marker so debug
/// tooling can find it and a zone whose record was lost is still left alone.
/// </summary>
public static class BridgePlacement
{
    private static ManualLogSource Log => ProceduralRoadsPlugin.ProceduralRoadsLogger;

    /// <summary>ZDO marker on every piece this mod spawned. Vanilla clients
    /// ignore unknown ZDO variables, so the marker is crossplay-safe.</summary>
    private static int MarkerHash => BridgePlans.MarkerHash;

    private static readonly HashSet<string> s_warnedPrefabs = new();

    /// <summary>Whether this peer may create or destroy world objects.</summary>
    private static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer();

    /// <summary>
    /// Called from the SpawnZone postfix in every mode. On the server, a zone
    /// with a plan and no pieces yet gets them now: as ZDOs only under ghost
    /// generation, the way the game generates its own zones, otherwise as
    /// live objects.
    /// </summary>
    public static int OnZoneSpawned(Vector2s zoneID, ZoneSystem.SpawnMode mode) =>
        SpawnInZone(zoneID, mode == ZoneSystem.SpawnMode.Ghost);

    /// <summary>
    /// Spawn the plans into every zone that is already loaded: zones generated
    /// before the network existed (around the login position, or on a world
    /// the mod was added to) never spawn again while they stay loaded, so
    /// they get their pieces here. Returns how many zones got pieces.
    /// </summary>
    public static int SpawnInLoadedZones(ICollection<ZDOID>? condemned = null)
    {
        var heightmaps = Heightmap.GetAllHeightmaps();
        if (heightmaps == null)
            return 0;
        int zones = 0;
        foreach (Heightmap heightmap in heightmaps)
        {
            if (heightmap == null)
                continue;
            if (SpawnInZone(ZoneSystem.GetZone(heightmap.transform.position), ghost: false, condemned) > 0)
                zones++;
        }
        return zones;
    }

    /// <summary>
    /// The network was rebuilt in a running world: destroy the pieces of the
    /// old one everywhere and spawn the new plans into the loaded zones
    /// (other zones get theirs when they load). Returns (destroyed, zones).
    /// </summary>
    public static (int destroyed, int zones) RespawnFromPlans()
    {
        // The ids come back because destruction is QUEUED: the old pieces are
        // still in ZDOMan's sector lookup while the replacements go in, and
        // without this the replacement pass would mistake them for pieces
        // already standing and skip every zone it was asked to rebuild.
        var condemned = new HashSet<ZDOID>();
        int destroyed = ClearSpawnedPieces(condemned);
        return (destroyed, SpawnInLoadedZones(condemned));
    }

    private static int SpawnInZone(Vector2s zoneID, bool ghost, ICollection<ZDOID>? condemned = null)
    {
        if (!IsServer || ZNetScene.instance == null || ZDOMan.instance == null)
            return 0;
        List<BridgePiece>? pieces = BridgePlans.PlanFor(zoneID);
        if (pieces == null)
            return 0;
        if (BridgePlans.IsSpawned(zoneID))
            return 0;
        // A zone whose pieces are already standing is recorded as spawned and
        // left alone; pieces condemned moments ago do not count as standing.
        if (BridgePlans.ZoneHasLivePieces(zoneID, condemned))
        {
            BridgePlans.MarkSpawned(zoneID);
            return 0;
        }

        BridgePlans.MarkSpawned(zoneID);
        int spawned = SpawnPieces(pieces, ghost);
        if (spawned > 0)
            Log.LogInfo($"[BRIDGES] zone {zoneID}: spawned {spawned} bridge pieces");
        return spawned;
    }

    private static int SpawnPieces(List<BridgePiece> pieces, bool ghost)
    {
        int spawned = 0;
        foreach (BridgePiece piece in pieces)
        {
            GameObject? prefab = ZNetScene.instance.GetPrefab(piece.Prefab);
            if (prefab == null)
            {
                if (s_warnedPrefabs.Add(piece.Prefab))
                    Log.LogWarning($"[BRIDGES] prefab not found: {piece.Prefab}");
                continue;
            }

            if (ghost)
                ZNetView.StartGhostInit();

            Quaternion rotation = Quaternion.Euler(piece.PitchDegrees, piece.YawDegrees, 0f);
            GameObject go = Object.Instantiate(prefab, piece.Position, rotation);

            ZNetView nview = go.GetComponent<ZNetView>();
            ZDO? zdo = nview != null ? nview.GetZDO() : null;
            if (zdo != null)
            {
                zdo.Set(MarkerHash, 1);
                WearNTear wearNTear = go.GetComponent<WearNTear>();
                if (wearNTear != null)
                    zdo.Set("health", wearNTear.m_health * piece.HealthFraction);
            }

            if (ghost)
            {
                // Plain Destroy: a ghost-init view keeps its ZDO (the point of
                // ghost generation); ZNetView.Destroy would drop it.
                Object.Destroy(go);
                ZNetView.FinishGhostInit();
            }
            spawned++;
        }
        return spawned;
    }

    /// <summary>
    /// Destroy every piece this mod spawned, loaded or not, and forget which
    /// zones had them, so the current plans can be spawned afresh. Server only.
    /// </summary>
    public static int ClearSpawnedPieces(ICollection<ZDOID>? condemned = null)
    {
        if (!IsServer || ZDOMan.instance == null || ZNetScene.instance == null)
            return 0;
        BridgePlans.InvalidatePlans();
        BridgePlans.ForgetSpawned();

        List<ZDO> tagged = new();
        foreach (var kv in ZDOMan.instance.m_objectsByID)
        {
            if (kv.Value.GetInt(MarkerHash) == 1)
                tagged.Add(kv.Value);
        }

        int destroyed = 0;
        foreach (ZDO zdo in tagged)
        {
            ZNetView instance = ZNetScene.instance.FindInstance(zdo);
            if (instance != null)
            {
                // The scene drops a persistent object only when it owns it;
                // a piece a client owns would survive and its marker would
                // then block the respawn.
                instance.ClaimOwnership();
                ZNetScene.instance.Destroy(instance.gameObject);
            }
            else
            {
                zdo.SetOwner(ZDOMan.GetSessionID());
                ZDOMan.instance.DestroyZDO(zdo);
            }
            condemned?.Add(zdo.m_uid);
            destroyed++;
        }
        if (destroyed > 0)
            Log.LogInfo($"[BRIDGES] destroyed {destroyed} bridge pieces");
        return destroyed;
    }

    /// <summary>
    /// Clear-areas around the plan so vegetation does not spawn through the
    /// deck (a bridge zone often carries no painted road points of its own).
    /// </summary>
    public static List<ZoneSystem.ClearArea> GetClearAreas(Vector2s zoneID)
    {
        List<ZoneSystem.ClearArea> areas = new();
        List<BridgePiece>? pieces = BridgePlans.PlanFor(zoneID);
        if (pieces == null)
            return areas;

        Vector3? last = null;
        foreach (BridgePiece piece in pieces)
        {
            if (last.HasValue && Vector3.Distance(last.Value, piece.Position) < 2f)
                continue;
            areas.Add(new ZoneSystem.ClearArea(piece.Position, 2.5f));
            last = piece.Position;
        }
        return areas;
    }
}
