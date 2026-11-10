using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Manages road generation lifecycle: initialization, loading, and cleanup.
/// </summary>
public static class RoadLifecycleManager
{
    private static readonly Queue<Heightmap> PendingTerrain = new();
    private static float nextTerrainScan;

    public static void UpdateLoadedTerrain()
    {
        if (!RoadTravelApi.Ready || ZoneSystem.instance == null) return;
        if (PendingTerrain.Count == 0 && Time.unscaledTime >= nextTerrainScan)
        {
            nextTerrainScan = Time.unscaledTime + 2f;
            foreach (Heightmap heightmap in Heightmap.GetAllHeightmaps())
                if (heightmap != null && !heightmap.IsDistantLod) PendingTerrain.Enqueue(heightmap);
        }
        if (PendingTerrain.Count == 0) return;
        Heightmap current = PendingTerrain.Dequeue();
        if (current == null || !ZoneSystem.instance.IsZoneLoaded(current.transform.position)) return;
        Vector2s zone = ZoneSystem.GetZone(current.transform.position);
        List<RoadSpatialGrid.RoadPoint> points = RoadSpatialGrid.GetRoadPointsInZone(zone);
        if (points.Count == 0) return;
        TerrainComp terrain = TerrainComp.FindTerrainCompiler(current.transform.position);
        // Network objects can arrive after the zone root. Wait for them before creating a compiler.
        if (terrain == null)
        {
            if (ZNetScene.instance == null || !ZNetScene.instance.IsAreaReady(current.transform.position)) return;
            terrain = current.GetAndCreateTerrainCompiler();
        }
        if (!terrain.m_nview.IsValid() || !terrain.m_nview.IsOwner()) return;
        RoadTerrainModifier.ApplyRoadTerrainMods(zone, points);
    }
    public static void UpdateServer()
    {
        if (ZNet.instance != null && ZNet.instance.IsServer() && RoadNetworkGenerator.IsLocationsReady &&
            !RoadNetworkGenerator.RoadsAvailable && WorldGenerator.instance != null)
            OnLocationsGenerated();
    }
    /// <summary>
    /// Called when ZoneSystem starts. Initializes road generator and subscribes to events.
    /// </summary>
    public static void OnZoneSystemStart(ZoneSystem zoneSystem)
    {
        PendingTerrain.Clear();
        nextTerrainScan = 0f;
        RoadNetworkGenerator.Initialize();
        RoadTravelApi.WorldId = ZNet.instance != null && ZNet.m_world != null ? ZNet.instance.GetWorldUID() : 0;
        zoneSystem.GenerateLocationsCompleted += OnLocationsGenerated;
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Subscribed to GenerateLocationsCompleted event");
    }

    /// <summary>
    /// Called when ZoneSystem is destroyed. Cleans up road data and unsubscribes from events.
    /// </summary>
    public static void OnZoneSystemDestroy(ZoneSystem zoneSystem)
    {
        PendingTerrain.Clear();
        zoneSystem.GenerateLocationsCompleted -= OnLocationsGenerated;
        RoadNetworkGenerator.Reset();
        RoadClearAreaManager.ClearCache();
        RoadTerrainModifier.ResetDebugCounters();

        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Road data cleared on world unload");
    }

    /// <summary>
    /// Called when location generation completes. Triggers road loading or generation.
    /// </summary>
    private static void OnLocationsGenerated()
    {
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Location generation complete...");
        RoadNetworkGenerator.MarkLocationsReady();
        RoadClearAreaManager.ClearCache();

        bool hasWorldGen = WorldGenerator.instance != null;
        bool hasLocations = ZoneSystem.instance?.GetLocationList()?.Count > 0;

        if (hasWorldGen && hasLocations)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(
                $"WorldGenerator and locations available ({ZoneSystem.instance!.GetLocationList()!.Count} locations)...");
            
            if (RoadNetworkGenerator.TryLoadGlobalRoadData())
            {
                RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Loaded roads from global persistence");
            }
            else
            {
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("No persisted roads found, generating...");
                RoadNetworkGenerator.GenerateRoads();
            }
        }
        else
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(
                $"Deferring road generation (WorldGen={hasWorldGen}, Locations={hasLocations})...");
        }
    }

    /// <summary>
    /// Called when player spawns. Enables deferred road loading for existing worlds.
    /// </summary>
    public static void OnPlayerSpawn(Vector3 spawnPoint)
    {
        if (!RoadNetworkGenerator.IsLocationsReady || RoadNetworkGenerator.RoadsAvailable)
            return;

        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(
            $"Player spawning at {spawnPoint}, attempting to load global road data...");

        if (RoadNetworkGenerator.TryLoadGlobalRoadData())
        {
            RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Roads loaded from global persistence");
        }
        else
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("No global road data, generating...");
            RoadNetworkGenerator.GenerateRoads();
        }
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
