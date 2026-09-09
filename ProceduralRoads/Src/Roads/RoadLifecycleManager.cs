using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Manages road generation lifecycle: initialization, loading, and cleanup.
/// </summary>
public static class RoadLifecycleManager
{
    /// <summary>
    /// Called when ZoneSystem starts. Initializes road generator and subscribes to events.
    /// </summary>
    public static void OnZoneSystemStart(ZoneSystem zoneSystem)
    {
        RoadNetworkGenerator.Initialize();
        zoneSystem.GenerateLocationsCompleted += OnLocationsGenerated;
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Subscribed to GenerateLocationsCompleted event");
    }

    /// <summary>
    /// Called when ZoneSystem is destroyed. Cleans up road data and unsubscribes from events.
    /// </summary>
    public static void OnZoneSystemDestroy(ZoneSystem zoneSystem)
    {
        zoneSystem.GenerateLocationsCompleted -= OnLocationsGenerated;
        m_worldDataLoaded = false;
        RoadNetworkGenerator.Reset();
        RoadClearAreaManager.ClearCache();
        RoadTerrainModifier.ResetDebugCounters();

        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Road data cleared on world unload");
    }

    /// <summary>
    /// Whether the world's own data has finished loading, so a road network
    /// saved in it would be in memory to find.
    /// </summary>
    private static bool m_worldDataLoaded;

    /// <summary>
    /// Called when location generation completes.
    ///
    /// This can fire BEFORE the world's ZDOs are in memory. Loading a saved
    /// world runs ZoneSystem.Load, which sets LocationsGenerated from the
    /// save, and that setter raises this event -- while ZDOMan.LoadChunks,
    /// on the next line of ZNet.LoadWorld, has not run yet. Deciding here
    /// would look for a saved network before anything was loaded, find
    /// nothing, and generate a new one over the top of it.
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
        m_worldDataLoaded = true;
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("World data loaded");
        DecideOnce("world data loaded");
    }

    /// <summary>
    /// Load the saved network, or build one - whichever this world needs, once,
    /// and only when both halves are ready.
    ///
    /// Both of the two things this waits on can arrive in either order: a saved
    /// world raises the locations event during its own load, a fresh one raises
    /// it long afterwards. So each caller says it is ready and the last one to
    /// arrive does the work.
    /// </summary>
    private static void DecideOnce(string trigger)
    {
        if (RoadNetworkGenerator.RoadsAvailable)
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

        if (RoadNetworkGenerator.TryLoadGlobalRoadData())
        {
            RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Loaded roads from global persistence");
            return;
        }

        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("No persisted roads found, generating...");
        // GenerateRoadsOnLoad, not GenerateRoads: it honours the
        // PROCEDURALROADS_GENERATE_ROADS_ON_LOAD switch and then applies road
        // terrain to the zones that spawned during the loading screen, before
        // the network existed. Calling the generator directly loses both.
        RoadNetworkGenerator.GenerateRoadsOnLoad();
    }

    /// <summary>
    /// Called when player spawns. Enables deferred road loading for existing worlds.
    /// </summary>
    public static void OnPlayerSpawn(Vector3 spawnPoint)
    {
        if (RoadNetworkGenerator.RoadsAvailable)
            return;

        // A last resort, for a host path that did not go through ZNet's world
        // load. It does NOT establish that a saved network has arrived: on a
        // client the player spawns without any guarantee that the road
        // metadata has replicated, and nothing here waits for it. Road
        // generation is host-side, so a client reaching this is already
        // outside what this hook can promise.
        m_worldDataLoaded = true;
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug($"Player spawning at {spawnPoint}");
        DecideOnce("player spawn");
    }

    /// <summary>
    /// Called before world save. Persists global road data.
    /// </summary>
    public static void OnPrepareSave()
    {
        if (RoadNetworkGenerator.RoadsGenerated)
        {
            RoadNetworkGenerator.SaveGlobalRoadData();
        }
    }
}
