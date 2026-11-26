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

    /// <summary>
    /// Called from the SpawnZone postfix in every mode. On the server, a zone
    /// with a plan and no pieces yet gets them now: as ZDOs only under ghost
    /// generation, the way the game generates its own zones, otherwise as
    /// live objects.
    /// The one owner of placing pieces into zones, and whether a zone's structure is started (docs/OWNERS.md).
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
        if (!RoadLifecycleManager.HoldsTheNetwork || ZoneSystem.instance == null)
            return 0;

        int zones = 0;
        foreach (Vector2s zone in StructurePlans.ZonesNeedingPieces(ZoneSystem.instance.IsZoneGenerated))
        {
            if (SpawnInZone(zone, ghost: true, condemned) > 0)
                zones++;
        }
        return zones;
    }

    /// <summary>The one owner of placing pieces into zones, and whether a zone's structure is started (docs/OWNERS.md).</summary>
    private static int SpawnInZone(Vector2s zoneID, bool ghost, ICollection<ZDOID>? condemned = null)
    {
        if (!RoadLifecycleManager.HoldsTheNetwork || ZNetScene.instance == null || ZDOMan.instance == null)
            return 0;
        // A zone explored before this network gets no pieces (NetworkCutoff).
        if (NetworkCutoff.Excludes(zoneID))
            return 0;
        // Nor does a bridge that reaches into such a zone anywhere else: it is built whole or not at all.
        // The zone is not marked spawned, so the bridge is built if those zones stop being excluded.
        // What an append owes the zone goes out first (its pieces are owed here too, and laid once, by the queue); once it is all out, the zone's own plan, if it is still owed, below, but for those pieces.
        int appended = 0;
        if (StructureAppendQueue.ForZone(zoneID) != null)
        {
            appended = SpawnAppend(zoneID);
            if (StructureAppendQueue.ForZone(zoneID) != null) return appended;
        }
        List<StructurePiece>? pieces = StructurePlans.BuildablePlanFor(zoneID);
        if (pieces == null)
            return appended;
        // A bridge with pieces built keeps the kit they were built in: chosen with its first pieces, below, or here for a
        // zone built already (a record that saved no kit) (RoadNetworkGenerator.ChooseKitsAsBuilt).
        if (StructurePlans.IsSpawned(zoneID))
        {
            RoadNetworkGenerator.ChooseKitsAsBuilt(pieces);
            return appended;
        }
        // A zone whose plan's pieces are already standing is recorded as spawned and left alone; pieces condemned moments
        // ago do not count as standing, nor do an append's (its own work, not the zone's plan).
        if (StructurePlans.ZoneHasLivePieces(zoneID, condemned, laidByPlan: true))
        {
            RoadNetworkGenerator.ChooseKitsAsBuilt(pieces);
            StructurePlans.MarkSpawned(zoneID);
            return appended;
        }
        // The plan's pieces an append put out here already stand (the plans read them from then on): the rest is laid.
        HashSet<string> standing = AppendedKeys(zoneID, condemned);
        if (standing.Count > 0) pieces = pieces.FindAll(p => !standing.Contains(StructureAppendQueue.Key(p)));
        if (pieces.Count == 0)
        {
            StructurePlans.MarkSpawned(zoneID);
            return appended;
        }

        // A plan goes out whole: a zone waiting on a prefab not registered yet is not recorded as having its pieces,
        // so it gets them on a later spawn once the prefab is there.
        string? missing = MissingPrefab(pieces);
        if (missing != null)
        {
            if (s_warnedPrefabs.Add(missing))
                Log.LogWarning($"[BRIDGES] zone {zoneID}: pieces wait for missing prefab {missing}");
            return appended;
        }
        // A bridge's pieces saved as its first zone is built, the kits chosen as they go out, among the kits installed now,
        // and saved.
        if (WorldGenerator.instance is WorldGenerator world) StructureSites.KeepFirstBuilt(pieces, world, condemned);
        RoadNetworkGenerator.ChooseKitsAsBuilt(pieces);
        int spawned = 0;
        var emitted = new List<StructurePiece>();
        for (int i = 0; i < pieces.Count; i++)
        {
            try { spawned += SpawnPieces(pieces.GetRange(i, 1), ghost, emitted: emitted); }
            catch
            {
                // The pieces already out stand, and would make the zone look done (ZoneHasLivePieces): the rest of the
                // plan is owed as a manual road's pieces are, one by one, and the zone is recorded once they are out.
                // A piece whose object is out though a step after it failed stands too, and is not owed again.
                int rest = emitted.Count > 0 && emitted[emitted.Count - 1] == pieces[i] ? i + 1 : i;
                StructureAppendQueue.Enqueue(new Dictionary<Vector2s, List<StructurePiece>> { [zoneID] = pieces.GetRange(rest, pieces.Count - rest) });
                throw;
            }
        }
        // Recorded only once the plan is out.
        StructurePlans.MarkSpawned(zoneID);
        if (spawned > 0)
            Log.LogInfo($"[BRIDGES] zone {zoneID}: spawned {spawned} bridge pieces");
        return appended + spawned;
    }

    /// <summary>The keys of the pieces an append put out in the zone (StructurePlans.AppendKeyHash), as their ZDOs carry them; a
    /// piece condemned moments ago (<paramref name="condemned"/>, a respawn's) does not stand.</summary>
    private static HashSet<string> AppendedKeys(Vector2s zone, ICollection<ZDOID>? condemned = null)
    {
        var zdos = new List<ZDO>();
        ZDOMan.instance.FindObjects(zone, zdos, new HashSet<ZoneSystem.SectorIndex>());
        var keys = new HashSet<string>();
        foreach (var zdo in zdos)
        {
            if (condemned != null && condemned.Contains(zdo.m_uid)) continue;
            string key = zdo.GetString(StructurePlans.AppendKeyHash, "");
            if (key.Length > 0) keys.Add(key);
        }
        return keys;
    }

    /// <summary>
    /// Whether the zone's own plan is all out once an append's pieces are: recorded spawned already, standing as its plan
    /// laid it (StructurePlans.ZoneHasLivePieces, an append's pieces not counted: a plan that failed part way owes its rest
    /// through the queue), or every piece of its buildable plan standing as appended (<paramref name="standing"/>, by
    /// StructureAppendQueue.Key: the append was the zone's whole plan). Else its plan is still owed, and the zone is not
    /// recorded: an append marks only its own work done.
    /// </summary>
    private static bool PlanOut(Vector2s zone, HashSet<string> standing) =>
        StructurePlans.IsSpawned(zone) || StructurePlans.ZoneHasLivePieces(zone, laidByPlan: true) ||
        StructurePlans.BuildablePlanFor(zone) is not { } plan || plan.TrueForAll(p => standing.Contains(StructureAppendQueue.Key(p)));

    /// <summary>The first of these pieces' prefabs the scene does not have, or null when it has them all.</summary>
    private static string? MissingPrefab(List<StructurePiece> pieces)
    {
        foreach (StructurePiece piece in pieces)
            if (ZNetScene.instance.GetPrefab(piece.Prefab) == null)
                return piece.Prefab;
        return null;
    }

    private static int s_appendCursor;
    public static void ApplyPendingAppends()
    {
        if (RoadLifecycleManager.WorkersOwnWorld) return;
        if (!RoadLifecycleManager.HoldsTheNetwork || ZNetScene.instance == null || ZDOMan.instance == null) return;
        var zones = new List<Vector2s>(StructureAppendQueue.Zones);
        if (zones.Count == 0) return;
        // One zone / at most 64 new objects per retry. A missing prefab in one
        // zone must not starve every other pending bridge.
        if (s_appendCursor >= zones.Count) s_appendCursor = 0;
        SpawnAppend(zones[s_appendCursor++]);
    }

    private static int SpawnAppend(Vector2s zone)
    {
        var pieces = StructureAppendQueue.ForZone(zone);
        if (pieces == null || NetworkCutoff.Excludes(zone)) return 0;
        HashSet<string> existing = AppendedKeys(zone);
        int count = 0;
        bool changed = false;
        try
        {
            while (pieces.Count > 0 && count < 64)
            {
                var piece = pieces[0];
                string key = StructureAppendQueue.Key(piece);
                if (!existing.Contains(key))
                {
                    if (ZNetScene.instance.GetPrefab(piece.Prefab) == null)
                    {
                        if (s_warnedPrefabs.Add(piece.Prefab))
                            Log.LogWarning($"[BRIDGES] append pending in {zone}: missing prefab {piece.Prefab}");
                        return count;
                    }
                    count += SpawnPieces(new List<StructurePiece> { piece }, ghost: true, appendKey: key);
                    existing.Add(key);
                }
                // Once emitted, a piece is no longer owed, even if physics or
                // a player destroys it before the rest of the bridge is built.
                StructureAppendQueue.Acknowledge(zone, key);
                changed = true;
            }
            if (pieces.Count == 0)
            {
                StructureAppendQueue.Complete(zone);
                if (PlanOut(zone, existing)) StructurePlans.MarkSpawned(zone);
                changed = true;
            }
            return count;
        }
        // Saved when the queue moved: a piece owed for a prefab that is missing (a removed kit mod) rewrote the
        // records on every retry.
        finally { if (changed) RoadNetworkGenerator.SaveBridgeZones(); }
    }

    /// <summary>A piece's turn as spawned: pitch, yaw and roll in Unity's own Euler order, as the vanilla locations'
    /// children read: a vine hangs by its 180 degree roll. The roll was dropped here before
    /// (the wooden kits' toppled debris laid one too).</summary>
    internal static Quaternion Rotation(StructurePiece piece) => Quaternion.Euler(piece.PitchDegrees, piece.YawDegrees, piece.RollDegrees);

    /// <summary>Places the pieces; <paramref name="emitted"/>, when given, gets each piece as its object (its ZDO) is out,
    /// before the steps that follow on it. With <paramref name="loose"/>, a debug catalogue's (RailingCatalogue): not the
    /// mod's marker (no clearing or respawn of the network's pieces touches them), that string under its key on each instead.</summary>
    private static int SpawnPieces(List<StructurePiece> pieces, bool ghost, string? appendKey = null, List<StructurePiece>? emitted = null,
        (int key, string value)? loose = null)
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

            GameObject? go = null;
            try
            {
                Quaternion rotation = Rotation(piece);
                go = Object.Instantiate(prefab, piece.Position, rotation);

                ZNetView nview = go.GetComponent<ZNetView>();
                ZDO? zdo = nview != null ? nview.GetZDO() : null;
                if (zdo != null)
                {
                    emitted?.Add(piece);
                    if (loose is var (looseKey, looseValue)) zdo.Set(looseKey, looseValue);
                    else zdo.Set(MarkerHash, 1);
                    if (appendKey != null) zdo.Set(StructurePlans.AppendKeyHash, appendKey);
                    WearNTear wearNTear = go.GetComponent<WearNTear>();
                    if (wearNTear != null)
                    {
                        zdo.Set("health", wearNTear.m_health * piece.HealthFraction);
                        ShowHealth(wearNTear, piece.HealthFraction);
                    }
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
    /// Destroy every piece this mod spawned, loaded or not, except in zones
    /// explored before the network, and forget which zones had them, so the
    /// current plans can be spawned afresh. Server only. With
    /// <paramref name="sites"/>, only those saved crossing sites are forgotten,
    /// and only the zones they stand in or are solved into now are cleared.
    /// </summary>
    public static int ClearSpawnedPieces(ICollection<ZDOID>? condemned = null, List<BuiltBridge>? sites = null)
    {
        if (RoadLifecycleManager.WorkersOwnWorld) return 0;
        if (!RoadLifecycleManager.HoldsTheNetwork || ZDOMan.instance == null || ZNetScene.instance == null)
            return 0;
        StructurePlans.InvalidatePlans();
        HashSet<Vector2s>? zones = null;
        if (sites == null)
        {
            StructurePlans.ForgetSpawned();
            // The pieces go, and the bridges and staircases are built again from the plans as their zones are, the held
            // ground with them.
            BuiltBridges.Reset();
            BuiltStairs.Reset();
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
            Destroy(zdo);
            condemned?.Add(zdo.m_uid);
            destroyed++;
        }
        if (destroyed > 0)
            Log.LogInfo($"[BRIDGES] destroyed {destroyed} bridge pieces");
        return destroyed;
    }

    /// <summary>Destroys a piece's object, loaded or not. The scene drops a persistent object only when it owns it; a piece a
    /// client owns would survive and its marker would then block the respawn.</summary>
    internal static void Destroy(ZDO zdo)
    {
        ZNetView? instance = ZNetScene.instance.FindInstance(zdo);
        if (instance != null)
        {
            instance.ClaimOwnership();
            ZNetScene.instance.Destroy(instance.gameObject);
        }
        else
        {
            zdo.SetOwner(ZDOMan.GetSessionID());
            ZDOMan.instance.DestroyZDO(zdo);
        }
    }

    /// <summary>Places a debug catalogue's pieces (RailingCatalogue) by the one placing path, each live, or as its object
    /// only where <paramref name="ghost"/> says (a zone not loaded), carrying <paramref name="value"/> under <paramref name="key"/> and not
    /// the mod's marker. Returns how many were placed.</summary>
    internal static int SpawnLoose(List<StructurePiece> pieces, int key, string value, System.Func<Vector3, bool> ghost)
    {
        if (ZNetScene.instance == null || ZDOMan.instance == null) return 0;
        int spawned = 0;
        foreach (StructurePiece piece in pieces)
            spawned += SpawnPieces(new List<StructurePiece> { piece }, ghost: ghost(piece.Position), loose: (key, value));
        return spawned;
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

    /// <summary>The structures' walking surfaces a zone's vegetation is judged against (StructurePlans.WalksNear); none while
    /// the generation's workers own the world.</summary>
    public static List<StairSupport.Walk> GetWalks(Vector2s zoneID) =>
        RoadLifecycleManager.WorkersOwnWorld ? new List<StairSupport.Walk>() : StructurePlans.WalksNear(zoneID);
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
