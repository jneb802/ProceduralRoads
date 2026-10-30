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
        // This world's placed places and the map icons they hide: another world's are not its. Kept through
        // a regeneration: the places stay placed, and nothing places them again to hide them.
        PlacedSites.Reset();
        HiddenSiteIcons.Reset();
        NetworkCutoff.Forget(zoneSystem);
        RoadClearAreaManager.ClearCache();
        RoadTerrainModifier.ResetForWorld();

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

    /// <summary>Whether this peer may hold a road network: only the server (or the hosting player). A client, whatever it
    /// can see of the world, loads, generates and writes no road. The one check every way a network is made asks: the load
    /// decision, the spawn fallback, road_generate and road_regen_island.</summary>
    internal static bool HoldsTheNetwork => ZNet.instance != null && ZNet.instance.IsServer();

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
        if (RoadNetworkGenerator.RoadsAvailable || IsGenerating || ChoicePending)
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
        Carry(decision, afterSpawn);
    }

    /// <summary>Whether the loading screen is asking what an explored world's first road network does (ExistingWorldFirstLoad).</summary>
    public static bool ChoicePending { get; private set; }

    /// <summary>Whether the host waits on its loading screen: for the first network, or for the answer about it.</summary>
    internal static bool HoldsLoadingScreen => InitialGenerationActive || ChoicePending;

    /// <summary>What the players are told once a network over the explored land is built (ExistingWorldFirstLoad.KeptNote).</summary>
    private static string? m_includedNote;

    /// <summary>The host answered on the loading screen. Returns whether the answer was taken.</summary>
    public static bool Answer(ExistingWorldRoads answer)
    {
        if (!ChoicePending || answer == ExistingWorldRoads.Ask) return false;
        ChoicePending = false;
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo($"Existing world, first load: {answer} (chosen on screen)");
        Carry(answer switch
        {
            ExistingWorldRoads.Skip => ExistingWorldFirstLoad.Decision.Skip,
            ExistingWorldRoads.IncludeExplored => ExistingWorldFirstLoad.Decision.IncludeExplored,
            _ => ExistingWorldFirstLoad.Decision.NewZonesOnly,
        }, afterSpawn: false);
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
    private static void Carry(ExistingWorldFirstLoad.Decision decision, bool afterSpawn)
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
                string? backup = ExistingWorldFirstLoad.Backup?.Invoke();
                if (backup == null)
                {
                    const string refused = "The world could not be backed up, so roads are built only in land explored from now on.";
                    log.LogError($"Existing world: {refused}");
                    ExistingWorldFirstLoad.Tell?.Invoke(refused);
                    break;
                }
                HashSet<Vector2s> keep = ExistingWorldFirstLoad.PlayerMarkedZones(NetworkCutoff.ExploredAtLoad);
                NetworkCutoff.IncludeExplored(keep);
                m_includedNote = ExistingWorldFirstLoad.KeptNote(keep.Count);
                log.LogInfo($"Existing world: roads are built through the explored land ({NetworkCutoff.ExploredAtLoadCount} zone(s)), " +
                            $"round the {keep.Count} with player buildings or terrain edits; backup {backup}");
                break;
            case ExistingWorldFirstLoad.Decision.NewZonesOnly:
                log.LogInfo($"Existing world: roads are built only in land explored from now on; the {NetworkCutoff.ExploredAtLoadCount} explored zone(s) stay as they are.");
                break;
        }
        // Behind the loading screen: BeginInitialGeneration builds the network over many frames
        // (it honours the PROCEDURALROADS_GENERATE_ROADS_ON_LOAD switch, and Tick applies road
        // terrain to the zones spawned before the network existed). With the player already in
        // the world (the spawn fallback) it would hold zone streaming off around a live player
        // for minutes, so that path builds synchronously as before, through GenerateRoadsOnLoad.
        if (afterSpawn) { GenerateAfterSpawn(); TellIncluded(); }
        else BeginInitialGeneration();
    }

    /// <summary>Once a network over the explored land is built: tell the players which zones it went round.</summary>
    private static void TellIncluded()
    {
        if (m_includedNote == null || !RoadNetworkGenerator.RoadsGenerated) return;
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(m_includedNote);
        ExistingWorldFirstLoad.Tell?.Invoke(m_includedNote);
        m_includedNote = null;
    }

    /// <summary>The spawn fallback's generation: synchronous, once per world as the loading-screen
    /// one is (a failed attempt is not retried), host only, and never thrown into the spawn hook.</summary>
    private static void GenerateAfterSpawn()
    {
        if (m_initialAttempted || !HoldsTheNetwork) return;
        m_initialAttempted = true;
        try { RoadNetworkGenerator.GenerateRoadsOnLoad(); }
        catch (System.Exception error)
        {
            // GenerateRoadsOnLoad completes the network (RoadsGenerated), then applies it to the loaded zones. A
            // failure after completion can follow terrain already written and bridge pieces already spawned: the
            // network stays, so those effects keep its identity and are saved with it. Either way nothing is retried.
            if (RoadNetworkGenerator.RoadsGenerated)
            {
                // The synchronous generation reported "Complete" before the step that failed.
                ReportApplicationFailed(error);
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogError(
                    "Road network generated after spawn was completed and kept, but a later step failed " +
                    $"(the loaded zones may lack some or all of its terrain and bridges); network retained: {error}");
            }
            else
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogError($"Road generation after spawn failed; no network was published: {error}");
        }
    }

    /// <summary>
    /// Called when player spawns. Enables deferred road loading for existing worlds.
    /// </summary>
    public static void OnPlayerSpawn(Vector3 spawnPoint)
    {
        if (RoadNetworkGenerator.RoadsAvailable || IsGenerating)
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
        if (IsGenerating) return;
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
