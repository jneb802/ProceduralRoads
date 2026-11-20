using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Manages road generation lifecycle: initialization, loading, and cleanup.
/// </summary>
public static partial class RoadLifecycleManager
{
    /// <summary>
    /// Called when ZoneSystem starts. Initializes road generator and subscribes to events.
    /// </summary>
    private static ZoneSystem? m_subscribedZone;

    public static void OnZoneSystemStart(ZoneSystem zoneSystem)
    {
        if (ReferenceEquals(m_subscribedZone, zoneSystem)) return;
        if (m_subscribedZone != null) m_subscribedZone.GenerateLocationsCompleted -= OnLocationsGenerated;
        m_subscribedZone = zoneSystem;
        AllowInitialRetry();
        zoneSystem.GenerateLocationsCompleted -= OnLocationsGenerated;
        RoadNetworkGenerator.Initialize();
        zoneSystem.GenerateLocationsCompleted += OnLocationsGenerated;
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Subscribed to GenerateLocationsCompleted event");
        DecideOnce("zone system start");
    }

    /// <summary>
    /// Called when ZoneSystem is destroyed. Cleans up road data and unsubscribes from events.
    /// </summary>
    public static void OnZoneSystemDestroy(ZoneSystem zoneSystem)
    {
        zoneSystem.GenerateLocationsCompleted -= OnLocationsGenerated;
        m_subscribedZone = null;
        m_worldDataLoaded = false;
        ChoicePending = false;
        m_includedNote = null;
        RoadNetworkGenerator.Reset();
        // This world's placed locations and the map icons they hide: another world's are not its. Kept through
        // a regeneration: the locations stay placed, and nothing places them again to hide them.
        PlacedLocations.Reset();
        HiddenLocationIcons.Reset();
        NetworkCutoff.Forget(zoneSystem);
        RoadClearAreaManager.ClearCache();
        RoadTerrainModifier.ResetForWorld();
        RoadNetworkSync.Reset();

        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Road data cleared on world unload");
    }

    /// <summary>
    /// Whether the world's own data has finished loading, so a road network
    /// saved in it would be in memory to find.
    /// </summary>
    private static bool m_worldDataLoaded;

    /// <summary>
    /// Called when location generation completes. On an old-format save this
    /// fires from inside ZNet's load routine, so it only marks the locations
    /// ready; DecideOnce still waits for the world data.
    /// </summary>
    private static void OnLocationsGenerated()
    {
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Location generation complete...");
        RoadNetworkGenerator.MarkLocationsReady();
        RoadClearAreaManager.ClearCache();
        DecideOnce("locations generated");
    }

    /// <summary>
    /// Called once the world's own data, ZDOs included, has been read.
    /// </summary>
    public static void OnWorldDataLoaded()
    {
        // Before anything of this session can generate a zone: which zones
        // the players had already explored (see NetworkCutoff).
        // Once per world: a second signal later in the session must not count
        // this session's zones as explored before the network.
        NetworkCutoff.SnapshotIfMissing(ZoneSystem.instance?.m_generatedZones);
        m_worldDataLoaded = true;
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("World data loaded");
        DecideOnce("world data loaded");
    }

    /// <summary>Whether this peer has a screen of its own to ask on: not a dedicated server.</summary>
    internal static bool HostHasScreen => ZNet.instance != null && !ZNet.instance.IsDedicated();

    /// <summary>What a peer that does not hold the network is told when asked to make one.</summary>
    internal const string NotTheServer = "Roads are generated only on the server; this peer holds no road network";

    /// <summary>Whether this peer may hold a road network: only the server (or the hosting player). A client, whatever it
    /// can see of the world, loads, generates and writes no road. The one check every way a network is made asks: the load
    /// decision, the spawn fallback, road_generate and road_regen_island.</summary>
    internal static bool HoldsTheNetwork => ZNet.instance != null && ZNet.instance.IsServer();

    /// <summary>Whether this peer has a road network to read: the server's own (generated, loaded or added to), or on a modded
    /// client the one the server sent it (RoadNetworkSync). The one check every read of the network asks (the console's
    /// readers, its diagnostics, the load decision); never false on a peer whose road queries answer.</summary>
    internal static bool HasNetwork => RoadNetworkGenerator.RoadsAvailable;

    /// <summary>Whether this peer writes the road network into the world now: only the peer that holds it
    /// (<see cref="HoldsTheNetwork"/>), with its network available (no generation's workers own the world). The one check every
    /// write path asks: zone spawn, vegetation placing, terrain compilers, the terrain sweep, the rock pass, the server bake and
    /// the saves. A client holding a received network reads it and writes nothing.</summary>
    internal static bool WritesTheNetwork => HoldsTheNetwork && RoadNetworkGenerator.RoadsAvailable;

    /// <summary>
    /// Load the saved network, or build one, once, when both halves are ready:
    /// the locations in place (IsLocationsReady: told by the event, or the game
    /// already in that state, since a 1.0-format load raises no event) and the
    /// world's own data read (a postfix on ZNet.ServerLoadWorld, so the saved
    /// network's ZDO is in memory). Whichever caller arrives last does the work.
    /// </summary>
    private static void DecideOnce(string trigger, bool afterSpawn = false)
    {
        // World load can announce readiness before ZoneSystem.Start. Initialize first, then replay both readiness facts.
        if (!ReferenceEquals(m_subscribedZone, ZoneSystem.instance)) return;
        if (HasNetwork || IsGenerating || ChoicePending)
            return;
        if (!HoldsTheNetwork)
            return;

        bool hasWorldGen = WorldGenerator.instance != null;
        bool hasLocations = ZoneSystem.instance?.GetLocationList()?.Count > 0;

        if (!hasWorldGen || !hasLocations || !RoadNetworkGenerator.IsLocationsReady || !m_worldDataLoaded)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(
                $"Waiting after {trigger} (WorldGen={hasWorldGen}, Locations={hasLocations}, " +
                $"LocationsReady={RoadNetworkGenerator.IsLocationsReady}, WorldData={m_worldDataLoaded})");
            return;
        }

        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(
            $"Deciding after {trigger}: {ZoneSystem.instance!.GetLocationList()!.Count} locations");
        // A host path that skipped ZNet's world load: the zones generated by now.
        NetworkCutoff.SnapshotIfMissing(ZoneSystem.instance.m_generatedZones);

        if (RoadNetworkGenerator.TryLoadGlobalRoadData())
        {
            RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Loaded roads from global persistence");
            RoadNetworkSync.Publish();
            return;
        }

        // A saved network this build cannot read is still somebody's roads:
        // never build a second network over it. The world runs without roads
        // until a build that reads it, or road_generate, says otherwise.
        if (RoadNetworkPersistence.LastLoadUnreadable)
            return;

        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("No persisted roads found");
        // An explored world's first network is the server's choice (ExistingWorldFirstLoad); a fresh world generates.
        var decision = !RoadNetworkGenerator.GenerateOnLoad || m_initialAttempted
            ? ExistingWorldFirstLoad.Decision.AsAlways
            : ExistingWorldFirstLoad.Decide(NetworkCutoff.ExploredAtLoadCount, RoadNetworkPersistence.FirstLoadSkipped(),
                ExistingWorldFirstLoad.Setting, ExistingWorldFirstLoad.CanAsk(), afterSpawn);
        if (decision == ExistingWorldFirstLoad.Decision.Ask)
        {
            // The loading screen holds (respawn and zone streaming) until the host answers (Answer, CancelToMenu).
            ChoicePending = true;
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(
                $"This world was explored without roads ({NetworkCutoff.ExploredAtLoadCount} zone(s)): asking what to do " +
                "(Roads.ExistingWorldFirstLoadRoads answers it without asking)");
            return;
        }
        Carry(decision);
    }

    /// <summary>Whether the loading screen is asking what an explored world's first road network does (ExistingWorldFirstLoad).</summary>
    public static bool ChoicePending { get; private set; }

    /// <summary>Whether the world is held (respawn, the server's zone streaming; in the world, the host's player): while a
    /// generation runs (at load, or in the world: the spawn fallback, road_generate, road_regen_island), or while the host
    /// is asked about an explored world's first network. The one owner of terrain settled, and whether the world is held (docs/OWNERS.md).</summary>
    internal static bool HoldsTheWorld => IsGenerating || ChoicePending;

    /// <summary>What the players are told once a network over the explored land is built (ExistingWorldFirstLoad.KeptNote).</summary>
    private static string? m_includedNote;

    /// <summary>The host answered on the loading screen. Returns whether the answer was taken.</summary>
    public static bool Answer(ExistingWorldRoads answer)
    {
        if (!ChoicePending || answer == ExistingWorldRoads.Ask) return false;
        ChoicePending = false;
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo($"Existing world, first load: {answer} (chosen on screen)");
        Carry(ExistingWorldFirstLoad.DecisionFor(answer));
        return true;
    }

    /// <summary>
    /// The host chose to go back to the menu: nothing is decided or written, and the next load asks again. The loading
    /// screen stays held until the game has left the world.
    /// </summary>
    public static bool CancelToMenu()
    {
        if (!ChoicePending || MenuTransitionBlocked()) return false;
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo("Existing world, first load: back to the menu; nothing decided");
#if ROADS_TESTS
        ReturnToMenuForTests?.Invoke();
#else
        Game.instance.Logout();
#endif
        return true;
    }

    /// <summary>Do what the first load decided.</summary>
    private static void Carry(ExistingWorldFirstLoad.Decision decision)
    {
        var log = ProceduralRoadsPlugin.ProceduralRoadsLogger;
        switch (decision)
        {
            case ExistingWorldFirstLoad.Decision.Skip:
                // Remembered, so no later load asks again or builds behind the players' backs; road_generate still can.
                if (!RoadNetworkPersistence.FirstLoadSkipped()) RoadNetworkPersistence.SaveFirstLoadSkipped();
                m_initialAttempted = true;
                log.LogInfo("Existing world: no road network is built, and the world stays as it is. road_generate builds one.");
                return;
            case ExistingWorldFirstLoad.Decision.IncludeExplored:
                // Before anything is written: the game's own backup of the world as it is on disk.
                if (!IncludeExploredLand(null, saveFirst: false, out string? backup, out int kept))
                {
                    const string refused = "The world could not be backed up, so roads are built only in land explored from now on.";
                    log.LogError($"Existing world: {refused}");
                    ExistingWorldFirstLoad.Tell?.Invoke(refused);
                    break;
                }
                m_includedNote = ExistingWorldFirstLoad.KeptNote(kept);
                log.LogInfo($"Existing world: roads are built through the explored land ({NetworkCutoff.ExploredAtLoadCount} zone(s)), " +
                            $"round the {kept} with player buildings or terrain edits; backup {backup}");
                break;
            case ExistingWorldFirstLoad.Decision.NewZonesOnly:
                log.LogInfo($"Existing world: roads are built only in land explored from now on; the {NetworkCutoff.ExploredAtLoadCount} explored zone(s) stay as they are.");
                break;
        }
        // BeginInitialGeneration builds the network over many frames, its locations placed first (it honours the
        // PROCEDURALROADS_GENERATE_ROADS_ON_LOAD switch, and Tick applies road terrain to the zones spawned before the
        // network existed): behind the loading screen, or with the player already in the world (the spawn fallback)
        // under the same panel, the world held still as road_generate holds it. Placing waits for frames, so no
        // generation in the game is synchronous.
        BeginInitialGeneration();
    }

    /// <summary>
    /// The next network is built through the explored land (ExistingWorldRoads.IncludeExplored), for the first load's
    /// answer and for road_generate and road_regen_island alike: the world backed up first (saved first when
    /// <paramref name="saveFirst"/>: a command's world has a session since its last save), then the explored zones players
    /// built in or dug kept (ExistingWorldFirstLoad.PlayerMarkedZones; <paramref name="kept"/> counts them), and for one
    /// <paramref name="island"/> every explored zone off it too, which the command builds nothing in
    /// (NetworkCutoff.IncludeExplored). With nothing explored there is nothing to back up or keep. False when the world
    /// could not be saved or backed up: then nothing is changed.
    /// </summary>
    internal static bool IncludeExploredLand(Island? island, bool saveFirst, out string? backup, out int kept)
    {
        backup = null;
        kept = 0;
        HashSet<Vector2s> explored = NetworkCutoff.Explored;
        if (explored.Count == 0) { NetworkCutoff.IncludeExplored(null); return true; }
        if (saveFirst && ExistingWorldFirstLoad.SaveNow?.Invoke() != true) return false;
        backup = ExistingWorldFirstLoad.Backup?.Invoke();
        if (backup == null) return false;
        HashSet<Vector2s> keep = ExistingWorldFirstLoad.PlayerMarkedZones(explored);
        kept = keep.Count;
        if (island != null)
        {
            HashSet<Vector2s> on = island.Zones();
            foreach (Vector2s zone in explored)
                if (!on.Contains(zone)) keep.Add(zone);
        }
        NetworkCutoff.IncludeExplored(keep);
        return true;
    }

    /// <summary>Once a network over the explored land is built: tell the players which zones it went round.</summary>
    private static void TellIncluded()
    {
        if (m_includedNote == null || !RoadNetworkGenerator.RoadsGenerated) return;
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(m_includedNote);
        ExistingWorldFirstLoad.Tell?.Invoke(m_includedNote);
        m_includedNote = null;
    }

    /// <summary>
    /// Called when player spawns. Enables deferred road loading for existing worlds.
    /// </summary>
    public static void OnPlayerSpawn(Vector3 spawnPoint)
    {
        if (HasNetwork || IsGenerating)
            return;

        // A last resort for a host whose world load did not go through ZNet.ServerLoadWorld.
        m_worldDataLoaded = true;
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug($"Player spawning at {spawnPoint}");
        DecideOnce("player spawn", afterSpawn: true);
    }

    /// <summary>
    /// Called before world save. Persists global road data.
    /// </summary>
    public static void OnPrepareSave()
    {
        // A client never saves a network: one it received is the server's.
        if (IsGenerating || !HoldsTheNetwork) return;
        if (RoadNetworkGenerator.NetworkDirty)
        {
            RoadNetworkGenerator.SaveGlobalRoadData();
        }
        else
        {
            // A loaded network is unchanged, but the zones that got their
            // bridge pieces this session must be remembered.
            RoadNetworkGenerator.SaveBridgeZones();
        }
        // Either way, the zones whose vegetation now matches the roads, and
        // the zones generated with this network in place.
        RoadNetworkGenerator.SaveClearedZones();
        RoadNetworkGenerator.SaveNetworkCutoff();
    }
}
