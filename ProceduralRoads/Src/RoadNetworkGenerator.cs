using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Orchestrates road network generation after POI locations are known.
/// </summary>
public static class RoadNetworkGenerator
{
    private static readonly HashSet<string> RoadDestinations = new HashSet<string>
    {
        "StartTemple",
        "Eikthyrnir",
        "GDKing",
        "Bonemass",
        "Dragonqueen",
        "GoblinKing",
        "SeekerQueen",
    };

    public static float RoadWidth = 4f;
    public static int MaxRoadsFromSpawn = 5;
    public static float MaxRoadLength = 3000f;

    private static bool m_roadsGenerated = false;

    public static bool RoadsGenerated => m_roadsGenerated;

    public static void Initialize()
    {
        m_roadsGenerated = false;
        RoadSpatialGrid.Clear();
    }

    public static void GenerateRoads()
    {
        if (m_roadsGenerated)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Roads already generated, skipping");
            return;
        }

        if (!ProceduralRoadsPlugin.EnableRoads.Value)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo("Road generation disabled in config");
            return;
        }

        if (WorldGenerator.instance == null)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning("WorldGenerator not available, cannot generate roads");
            return;
        }

        if (ZoneSystem.instance == null)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning("ZoneSystem not available, cannot generate roads");
            return;
        }

        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo("Starting road network generation...");

        DateTime startTime = DateTime.Now;

        var locationInstances = ZoneSystem.instance.GetLocationList();
        if (locationInstances == null || locationInstances.Count == 0)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning("No location instances found");
            return;
        }

        Vector3? spawnPoint = null;
        float spawnRadius = 0f;
        List<(string name, Vector3 position, float radius)> destinations = new List<(string, Vector3, float)>();

        foreach (var loc in locationInstances)
        {
            string prefabName = loc.m_location.m_prefab.Name;
            float exteriorRadius = loc.m_location.m_exteriorRadius;
            
            if (prefabName == "StartTemple")
            {
                spawnPoint = loc.m_position;
                spawnRadius = exteriorRadius;
            }
            else if (RoadDestinations.Contains(prefabName))
            {
                destinations.Add((prefabName, loc.m_position, exteriorRadius));
            }
        }

        if (!spawnPoint.HasValue)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning("Could not find spawn point (StartTemple)");
            spawnPoint = Vector3.zero;
        }

        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo($"Found spawn at {spawnPoint.Value}, {destinations.Count} potential destinations");

        destinations.Sort((a, b) => 
            Vector3.Distance(a.position, spawnPoint.Value).CompareTo(Vector3.Distance(b.position, spawnPoint.Value)));

        RoadPathfinder pathfinder = new RoadPathfinder(WorldGenerator.instance);
        int roadsGenerated = 0;

        foreach (var dest in destinations)
        {
            if (roadsGenerated >= MaxRoadsFromSpawn)
                break;

            float distance = Vector3.Distance(dest.position, spawnPoint.Value);
            if (distance > MaxRoadLength)
            {
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug($"Skipping {dest.name} - too far ({distance:F0}m)");
                continue;
            }

            var (startEdge, endEdge) = CalculateRoadEndpoints(spawnPoint.Value, spawnRadius, dest.position, dest.radius);

            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(
                $"Finding path to {dest.name} at distance {distance:F0}m (spawn radius: {spawnRadius:F1}m, dest radius: {dest.radius:F1}m)...");

            List<Vector2> path = pathfinder.FindPath(startEdge, endEdge);

            if (path != null && path.Count >= 2)
            {
                RoadSpatialGrid.AddRoadPath(path, RoadWidth, WorldGenerator.instance);
                roadsGenerated++;
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo($"Generated road to {dest.name} ({path.Count} waypoints, {distance:F0}m)");
            }
            else
            {
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning($"Could not find path to {dest.name}");
            }
        }

        int interRoads = GenerateInterDestinationRoads(pathfinder, destinations);
        roadsGenerated += interRoads;

        TimeSpan elapsed = DateTime.Now - startTime;
        LogGenerationStats(roadsGenerated, elapsed);

        m_roadsGenerated = true;
    }

    private static void LogGenerationStats(int roadsGenerated, TimeSpan elapsed)
    {
        var log = ProceduralRoadsPlugin.ProceduralRoadsLogger;
        
        log.LogInfo("=== Road Generation Summary ===");
        log.LogInfo($"  Roads generated: {roadsGenerated}");
        log.LogInfo($"  Total road points: {RoadSpatialGrid.TotalRoadPoints}");
        log.LogInfo($"  Total road length: {RoadSpatialGrid.TotalRoadLength:F0}m");
        log.LogInfo($"  Grid cells with roads: {RoadSpatialGrid.GridCellsWithRoads}");
        
        if (roadsGenerated > 0)
        {
            log.LogInfo($"  Avg points/road: {RoadSpatialGrid.TotalRoadPoints / (float)roadsGenerated:F0}");
            log.LogInfo($"  Avg length/road: {RoadSpatialGrid.TotalRoadLength / roadsGenerated:F0}m");
        }
        
        log.LogInfo($"  Generation time: {elapsed.TotalSeconds:F2}s");
        log.LogInfo($"  Road width: {RoadWidth}m");
        log.LogInfo("===============================");
    }

    private static int GenerateInterDestinationRoads(RoadPathfinder pathfinder, List<(string name, Vector3 position, float radius)> destinations)
    {
        int maxAdditionalRoads = 3;
        int additionalRoads = 0;

        var bossAltars = destinations
            .Where(d => d.name == "Eikthyrnir" || d.name == "GDKing" || d.name == "Bonemass")
            .ToList();

        for (int i = 0; i < bossAltars.Count && additionalRoads < maxAdditionalRoads; i++)
        {
            for (int j = i + 1; j < bossAltars.Count && additionalRoads < maxAdditionalRoads; j++)
            {
                float distance = Vector3.Distance(bossAltars[i].position, bossAltars[j].position);
                
                if (distance > MaxRoadLength * 0.7f)
                    continue;

                var (edge1, edge2) = CalculateRoadEndpoints(
                    bossAltars[i].position, bossAltars[i].radius,
                    bossAltars[j].position, bossAltars[j].radius);

                List<Vector2> path = pathfinder.FindPath(edge1, edge2);

                if (path != null && path.Count >= 2)
                {
                    RoadSpatialGrid.AddRoadPath(path, RoadWidth * 0.8f, WorldGenerator.instance);
                    additionalRoads++;
                    ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo($"Generated inter-boss road: {bossAltars[i].name} -> {bossAltars[j].name}");
                }
            }
        }
        
        return additionalRoads;
    }

    public static void Reset()
    {
        m_roadsGenerated = false;
        RoadSpatialGrid.Clear();
    }

    private static (Vector2 start, Vector2 end) CalculateRoadEndpoints(Vector3 pos1, float radius1, Vector3 pos2, float radius2)
    {
        Vector2 center1 = new Vector2(pos1.x, pos1.z);
        Vector2 center2 = new Vector2(pos2.x, pos2.z);
        Vector2 direction = (center2 - center1).normalized;
        return (center1 + direction * radius1, center2 - direction * radius2);
    }
}
