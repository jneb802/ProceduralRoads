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
    /// Spawn a zone's plans as ZDOs only, for a zone the server has generated
    /// but does not have loaded: one generated before the network existed,
    /// which no zone spawn will offer again. See ServerTerrainBake.
    /// </summary>
    public static int SpawnGhostInZone(Vector2s zoneID) => SpawnInZone(zoneID, ghost: true);

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
    /// old one everywhere, and spawn the new plans into the zones this peer
    /// has loaded AND -- on a server -- into every planned zone the world has
    /// already generated, which is where a dedicated server's players actually
    /// are. A zone the world has not generated yet is left alone on purpose:
    /// it gets its pieces when it is generated. Returns (destroyed, zones).
    /// </summary>
    public static (int destroyed, int zones) RespawnFromPlans()
    {
        // The ids come back because destruction is QUEUED: the old pieces are
        // still in ZDOMan's sector lookup while the replacements go in, and
        // without this the replacement pass would mistake them for pieces
        // already standing and skip every zone it was asked to rebuild.
        var condemned = new HashSet<ZDOID>();
        int destroyed = ClearSpawnedPieces(condemned);
        int zones = SpawnInLoadedZones(condemned);
        zones += SpawnGhostsIntoGeneratedZones(condemned);
        return (destroyed, zones);
    }

    /// <summary>
    /// Put the plans back into every zone the world has already generated,
    /// as ZDOs -- the route the server bake uses for a zone it does not have
    /// loaded. Zones already handled above are recorded as spawned and skipped.
    ///
    /// Without this a respawn on a dedicated server destroyed everywhere and
    /// rebuilt nowhere. ClearSpawnedPieces walks every marked ZDO in the world,
    /// while SpawnInLoadedZones can only reach zones that still have a
    /// Heightmap -- and a dedicated server has none where its players are,
    /// because their surroundings are ghost zones whose root ZoneSystem
    /// destroys on the spot. Nor did the loss heal when the players came back:
    /// ZoneSystem.CreateGhostZones calls SpawnZone only for a zone that is NOT
    /// yet generated, so the hook that spawns pieces never fired again. Only a
    /// server restart put them back, through the bake. Measured with two
    /// clients standing on the bridge: 142 pieces to 0, and still 0 after both
    /// left and rejoined.
    /// </summary>
    private static int SpawnGhostsIntoGeneratedZones(ICollection<ZDOID>? condemned)
    {
        if (!IsServer || ZoneSystem.instance == null)
            return 0;

        int zones = 0;
        foreach (Vector2s zone in BridgePlans.ZonesNeedingPieces(ZoneSystem.instance.IsZoneGenerated))
        {
            if (SpawnInZone(zone, ghost: true, condemned) > 0)
                zones++;
        }
        return zones;
    }

    private static int SpawnInZone(Vector2s zoneID, bool ghost, ICollection<ZDOID>? condemned = null)
    {
        if (!IsServer || ZNetScene.instance == null || ZDOMan.instance == null)
            return 0;
        if (BridgeAppendQueue.ForZone(zoneID) != null) return SpawnAppend(zoneID);
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

    private static int s_appendCursor;
    public static void ApplyPendingAppends()
    {
        if (!IsServer || ZNetScene.instance == null || ZDOMan.instance == null) return;
        var zones = new List<Vector2s>(BridgeAppendQueue.Zones);
        if (zones.Count == 0) return;
        // One zone / at most 64 new objects per retry. A missing prefab in one
        // zone must not starve every other pending bridge.
        if (s_appendCursor >= zones.Count) s_appendCursor = 0;
        SpawnAppend(zones[s_appendCursor++]);
    }

    private static readonly int AppendPieceHash = "ProceduralRoads_AppendPiece".GetStableHashCode();
    private static int SpawnAppend(Vector2s zone)
    {
        var pieces = BridgeAppendQueue.ForZone(zone);
        if (pieces == null) return 0;
        var zdos = new List<ZDO>();
        ZDOMan.instance.FindObjects(zone, zdos, new HashSet<ZoneSystem.SectorIndex>());
        var existing = new HashSet<string>();
        foreach (var zdo in zdos)
        {
            string key = zdo.GetString(AppendPieceHash, "");
            if (key.Length > 0) existing.Add(key);
        }
        int count = 0;
        try
        {
            while (pieces.Count > 0 && count < 64)
            {
                var piece = pieces[0];
                string key = BridgeAppendQueue.Key(piece);
                if (!existing.Contains(key))
                {
                    if (ZNetScene.instance.GetPrefab(piece.Prefab) == null)
                    {
                        if (s_warnedPrefabs.Add(piece.Prefab))
                            Log.LogWarning($"[BRIDGES] append pending in {zone}: missing prefab {piece.Prefab}");
                        return count;
                    }
                    count += SpawnPieces(new List<BridgePiece> { piece }, ghost: true, appendKey: key);
                    existing.Add(key);
                }
                // Once emitted, a piece is no longer owed, even if physics or
                // a player destroys it before the rest of the bridge is built.
                BridgeAppendQueue.Acknowledge(zone, key);
            }
            if (pieces.Count == 0) BridgeAppendQueue.Complete(zone);
            return count;
        }
        finally { RoadNetworkGenerator.SaveBridgeZones(); }
    }

    private static int SpawnPieces(List<BridgePiece> pieces, bool ghost, string? appendKey = null)
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

            GameObject? go = null;
            try
            {
                Quaternion rotation = Quaternion.Euler(piece.PitchDegrees, piece.YawDegrees, 0f);
                go = Object.Instantiate(prefab, piece.Position, rotation);

                ZNetView nview = go.GetComponent<ZNetView>();
                ZDO? zdo = nview != null ? nview.GetZDO() : null;
                if (zdo != null)
                {
                    zdo.Set(MarkerHash, 1);
                    if (appendKey != null) zdo.Set(AppendPieceHash, appendKey);
                    WearNTear wearNTear = go.GetComponent<WearNTear>();
                    if (wearNTear != null)
                        zdo.Set("health", wearNTear.m_health * piece.HealthFraction);
                }

                if (appendKey != null && zdo == null)
                    throw new System.InvalidOperationException($"Bridge append produced no ZDO for {piece.Prefab}");
                spawned++;
            }
            finally
            {
                if (ghost)
                {
                    if (go != null) Object.Destroy(go);
                    ZNetView.FinishGhostInit();
                }
            }
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
            // Wide enough for a two-lane deck: pieces sit up to DeckHalfWidth
            // either side of the crossing line, and the clearing has to keep
            // trees out of BOTH lanes, not just the centreline.
            areas.Add(new ZoneSystem.ClearArea(piece.Position, BridgeLayout.DeckHalfWidth + 1.5f));
            last = piece.Position;
        }
        return areas;
    }
}
