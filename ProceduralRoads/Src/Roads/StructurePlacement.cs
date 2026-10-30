using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Spawns the structure plans (bridges today, see StructurePlans) into the
/// world as persistent ZDOs of vanilla prefabs, so unmodded clients see
/// them too. Only the server creates or destroys pieces: clients receive
/// them like any other object. A zone gets its pieces once, when it first
/// comes alive with the network in place; the zone is then recorded in the
/// persisted spawned set, and every piece carries a ZDO marker so debug
/// tooling can find it and a zone whose record was lost is still left alone.
/// </summary>
public static class StructurePlacement
{
    private static ManualLogSource Log => ProceduralRoadsPlugin.ProceduralRoadsLogger;

    /// <summary>ZDO marker on every piece this mod spawned. Vanilla clients
    /// ignore unknown ZDO variables, so the marker is crossplay-safe.</summary>
    private static int MarkerHash => StructurePlans.MarkerHash;

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
        if (RoadLifecycleManager.WorkersOwnWorld) return 0;
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
    /// With <paramref name="olderLayoutOnly"/>, only the crossing sites an older
    /// layout solved (<see cref="BuiltBridges.OlderLayout"/>) are solved again
    /// and their saved pieces replaced: the zones they stand in, or will, are
    /// cleared and laid again, every other structure there from what it saved.
    /// </summary>
    public static (int destroyed, int zones) RespawnFromPlans(bool olderLayoutOnly = false)
    {
        if (RoadLifecycleManager.WorkersOwnWorld) return (0, 0);
        List<BuiltBridge>? sites = olderLayoutOnly ? BuiltBridges.OlderLayout() : null;
        if (sites is { Count: 0 }) return (0, 0);
        // The ids come back because destruction is QUEUED: the old pieces are
        // still in ZDOMan's sector lookup while the replacements go in, and
        // without this the replacement pass would mistake them for pieces
        // already standing and skip every zone it was asked to rebuild.
        var condemned = new HashSet<ZDOID>();
        int destroyed = ClearSpawnedPieces(condemned, sites);
        int zones = SpawnInLoadedZones(condemned);
        zones += SpawnGhostsIntoGeneratedZones(condemned);
        return (destroyed, zones);
    }

    /// <summary>
    /// Put the plans back into every zone the world has already generated,
    /// as ZDOs -- the route the server bake uses for a zone it does not have
    /// loaded. Zones already handled above are recorded as spawned and skipped.
    /// A dedicated server's players stand in ghost zones with no Heightmap, and
    /// the game never spawns a generated zone again, so without this a respawn
    /// there would destroy every piece and rebuild none.
    /// </summary>
    private static int SpawnGhostsIntoGeneratedZones(ICollection<ZDOID>? condemned)
    {
        if (!IsServer || ZoneSystem.instance == null)
            return 0;

        int zones = 0;
        foreach (Vector2s zone in StructurePlans.ZonesNeedingPieces(ZoneSystem.instance.IsZoneGenerated))
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
        // A zone explored before this network gets no pieces (NetworkCutoff).
        if (NetworkCutoff.Excludes(zoneID))
            return 0;
        // Nor does a bridge that reaches into such a zone anywhere else: it is built whole or not at all.
        // The zone is not marked spawned, so the bridge is built if those zones stop being excluded.
        List<StructurePiece>? pieces = StructurePlans.BuildablePlanFor(zoneID);
        if (pieces == null)
            return 0;
        // A bridge with pieces built keeps the kit they were built in: chosen with its first pieces, below, or here for a
        // zone built already (a record that saved no kit) (RoadNetworkGenerator.ChooseKitsAsBuilt).
        if (StructurePlans.IsSpawned(zoneID))
        {
            RoadNetworkGenerator.ChooseKitsAsBuilt(pieces);
            return 0;
        }
        // A zone whose pieces are already standing is recorded as spawned and
        // left alone; pieces condemned moments ago do not count as standing.
        if (StructurePlans.ZoneHasLivePieces(zoneID, condemned))
        {
            RoadNetworkGenerator.ChooseKitsAsBuilt(pieces);
            StructurePlans.MarkSpawned(zoneID);
            return 0;
        }

        // A plan goes out whole: a zone waiting on a prefab not registered yet is not recorded as having its pieces,
        // so it gets them on a later spawn once the prefab is there.
        string? missing = MissingPrefab(pieces);
        if (missing != null)
        {
            if (s_warnedPrefabs.Add(missing))
                Log.LogWarning($"[BRIDGES] zone {zoneID}: pieces wait for missing prefab {missing}");
            return 0;
        }
        // A bridge's pieces saved as its first zone is built, the kits chosen as they go out, among the kits installed now,
        // and saved.
        if (WorldGenerator.instance is WorldGenerator world) BridgeStructures.KeepFirstBuilt(pieces, world, condemned);
        RoadNetworkGenerator.ChooseKitsAsBuilt(pieces);
        int spawned = SpawnPieces(pieces, ghost);
        // Recorded only once the plan is out.
        StructurePlans.MarkSpawned(zoneID);
        if (spawned > 0)
            Log.LogInfo($"[BRIDGES] zone {zoneID}: spawned {spawned} bridge pieces");
        return spawned;
    }

    /// <summary>The first of these pieces' prefabs the scene does not have, or null when it has them all.</summary>
    private static string? MissingPrefab(List<StructurePiece> pieces)
    {
        foreach (StructurePiece piece in pieces)
            if (ZNetScene.instance.GetPrefab(piece.Prefab) == null)
                return piece.Prefab;
        return null;
    }

    /// <summary>A piece's turn as spawned: pitch, yaw and roll in Unity's own Euler order, as the vanilla locations'
    /// children read: a vine hangs by its 180 degree roll. The roll was dropped here before
    /// (the wooden kits' toppled debris laid one too).</summary>
    internal static Quaternion Rotation(StructurePiece piece) => Quaternion.Euler(piece.PitchDegrees, piece.YawDegrees, piece.RollDegrees);

    private static int SpawnPieces(List<StructurePiece> pieces, bool ghost, string? appendKey = null)
    {
        int spawned = 0;
        foreach (StructurePiece piece in pieces)
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
    /// Destroy every piece this mod spawned, loaded or not, except in zones
    /// explored before the network, and forget which zones had them, so the
    /// current plans can be spawned afresh. Server only. With
    /// <paramref name="sites"/>, only those saved crossing sites are forgotten,
    /// and only the zones they stand in or are solved into now are cleared.
    /// </summary>
    public static int ClearSpawnedPieces(ICollection<ZDOID>? condemned = null, List<BuiltBridge>? sites = null)
    {
        if (RoadLifecycleManager.WorkersOwnWorld) return 0;
        if (!IsServer || ZDOMan.instance == null || ZNetScene.instance == null)
            return 0;
        StructurePlans.InvalidatePlans();
        HashSet<Vector2s>? zones = null;
        if (sites == null)
        {
            StructurePlans.ForgetSpawned();
            // The pieces go, and the bridges are built again from the plans as their zones are, the held ground with them.
            BuiltBridges.Reset();
        }
        else
        {
            zones = new HashSet<Vector2s>();
            foreach (BuiltBridge site in sites)
                foreach (StructurePiece piece in site.Pieces)
                    zones.Add(ZoneSystem.GetZone(piece.Position));
            // The sites go, and are solved again as their first zone is built; the zones they are solved into now are
            // cleared too, so no zone keeps a part of them.
            BuiltBridges.Forget(sites);
            foreach (Vector2s zone in StructurePlans.PlannedZones)
                if (StructurePlans.PlanFor(zone)!.Exists(p => p.Crossing != null && BuiltBridges.IsOneOf(p.Crossing, sites)))
                    zones.Add(zone);
            StructurePlans.ForgetSpawned(zones);
        }

        List<ZDO> tagged = new();
        foreach (var kv in ZDOMan.instance.m_objectsByID)
        {
            // A piece standing in a zone explored before this network stays, as everything there does
            // (NetworkCutoff); nothing could be built back there.
            Vector2s zone = ZoneSystem.GetZone(kv.Value.GetPosition());
            if (kv.Value.GetInt(MarkerHash) == 1 && !NetworkCutoff.Excludes(zone) && (zones == null || zones.Contains(zone)))
                tagged.Add(kv.Value);
        }

        int destroyed = 0;
        foreach (ZDO zdo in tagged)
        {
            ZNetView? instance = ZNetScene.instance.FindInstance(zdo);
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

    /// <summary>Clear-areas round a zone's planned structure pieces, so vegetation
    /// does not spawn through them (each source decides its own; see StructurePlans).</summary>
    public static List<ZoneSystem.ClearArea> GetClearAreas(Vector2s zoneID)
    {
        if (RoadLifecycleManager.WorkersOwnWorld) return new List<ZoneSystem.ClearArea>();
        List<ZoneSystem.ClearArea> areas = new();
        foreach (var (centre, radius) in StructurePlans.ClearAreas(zoneID))
            areas.Add(new ZoneSystem.ClearArea(centre, radius));
        return areas;
    }
    private static readonly System.Reflection.FieldInfo? s_healthPercentage =
        typeof(WearNTear).GetField("m_healthPercentage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    private static readonly System.Reflection.MethodInfo? s_updateVisual =
        typeof(WearNTear).GetMethod("UpdateVisual", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(bool) }, null);

    /// <summary>
    /// The health a piece was just given, shown at once. WearNTear.Awake reads the ZDO's health as the piece is
    /// instantiated, before it is set here, so the hammer showed 100 % and the piece looked new until its zone loaded again
    /// (the blocks of a ruined bridge all showed 100 % health). Vanilla sets its damage before
    /// Instantiate (WearNTear.m_randomInitialDamage), but draws it from Unity's random state; the fraction here is the
    /// structure's own, fixed per crossing and world seed, so the piece's percentage and look are brought up to it after.
    /// Without effects, as Awake does.
    /// </summary>
    private static void ShowHealth(WearNTear wearNTear, float fraction)
    {
        if (fraction >= 1f || s_healthPercentage == null) return;
        s_healthPercentage.SetValue(wearNTear, Mathf.Clamp01(fraction));
        s_updateVisual?.Invoke(wearNTear, new object[] { false });
    }
}
