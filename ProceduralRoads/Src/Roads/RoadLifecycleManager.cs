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
    /// and only when both halves are ready: the locations in place, and the
    /// world's own data read. Each caller says it is ready and whichever
    /// arrives last does the work, because the order is not ours to choose.
    ///
    /// The two halves are separate on purpose, and the locations half has
    /// three doors into it. On Valheim 1.0, read from the assembly:
    ///
    ///   - A NEW world generates its locations in a coroutine that sets
    ///     ZoneSystem.LocationsGenerated through the property setter, and the
    ///     setter raises GenerateLocationsCompleted. The event arrives, late.
    ///   - An OLD-FORMAT save goes through ZoneSystem.LoadOld, which also uses
    ///     the setter, so the event arrives -- but DURING the load, before
    ///     ZDOMan.LoadChunks on the next line of ZNet.LoadWorld has run. Early,
    ///     with nothing yet in memory to find. This is not a pre-1.0 path: it
    ///     is what 1.0 runs the first time a player opens a world they saved
    ///     before updating.
    ///   - A 1.0-FORMAT save goes through ZoneSystem.Load, which writes the
    ///     backing field straight from the save and skips the setter. No event
    ///     is raised at all, however early we subscribed.
    ///
    /// So the event cannot be dropped in favour of asking the game, and asking
    /// the game cannot be dropped in favour of the event: two of the three
    /// doors only announce themselves, and the third only ever shows its state.
    ///
    /// RoadNetworkGenerator.IsLocationsReady covers both: it accepts either
    /// having been told or the game simply being in that state. What it must
    /// never do is stand in for the other half. Locations being in place says
    /// nothing about whether the world's ZDOs are in memory, and the saved
    /// network lives in a ZDO -- so the world-data gate below stays, or an
    /// existing world would build a second network over the one it saved and
    /// no one would be told.
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
