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
    private static readonly HashSet<string> BossLocationNames = new HashSet<string>
    {
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
    private static RoadPathfinder? m_pathfinder;
    private static int m_roadsGeneratedCount = 0;

    public static bool RoadsGenerated => m_roadsGenerated;

    public static void Initialize()
    {
        m_roadsGenerated = false;
        m_pathfinder = null;
        m_roadsGeneratedCount = 0;
        RoadSpatialGrid.Clear();
    }

    /// <summary>
    /// Main entry point for road generation. Calls various generation methods.
    /// </summary>
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
        m_pathfinder = new RoadPathfinder(WorldGenerator.instance);
        m_roadsGeneratedCount = 0;

        // Gather all location data
        var locations = GatherLocationData();
        if (locations == null)
            return;

        // === Call different generation methods here ===
        GenerateBossRoads(locations.Value);
        
        // Future: Add more generation methods
        // GenerateVillageRoads(locations.Value);
        // GenerateTraderRoutes(locations.Value);

        TimeSpan elapsed = DateTime.Now - startTime;
        LogGenerationStats(m_roadsGeneratedCount, elapsed);

        m_roadsGenerated = true;
        m_pathfinder = null;
    }

    #region Core Road Generation Primitive

    /// <summary>
    /// Core primitive: Generates a single road between two points.
    /// Handles pathfinding, radius trimming, and adding to the spatial grid.
    /// </summary>
    /// <param name="startCenter">Center of the start location</param>
    /// <param name="startRadius">Exterior radius of start location (road starts at edge)</param>
    /// <param name="endCenter">Center of the end location</param>
    /// <param name="endRadius">Exterior radius of end location (road ends at edge)</param>
    /// <param name="width">Width of the road</param>
    /// <param name="label">Optional label for logging</param>
    /// <returns>True if road was successfully generated</returns>
    public static bool GenerateRoad(
        Vector2 startCenter, float startRadius,
        Vector2 endCenter, float endRadius,
        float width, string? label = null)
    {
        if (m_pathfinder == null)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning("GenerateRoad called without active pathfinder");
            return false;
        }

        List<Vector2>? path = m_pathfinder.FindPath(startCenter, endCenter);

        // Attempt to keep UI responsive during generation
        UnityEngine.Canvas.ForceUpdateCanvases();

        if (path == null || path.Count < 2)
        {
            if (label != null)
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning($"Could not find path: {label}");
            return false;
        }

        // Trim path to stop at location radii
        path = TrimPathToRadii(path, startCenter, startRadius, endCenter, endRadius);

        if (path == null || path.Count < 2)
        {
            if (label != null)
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning($"Path too short after trimming: {label}");
            return false;
        }

        RoadSpatialGrid.AddRoadPath(path, width, WorldGenerator.instance);
        m_roadsGeneratedCount++;

        if (label != null)
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo($"Generated road: {label} ({path.Count} waypoints)");

        return true;
    }

    /// <summary>
    /// Convenience overload using Vector3 positions (extracts X/Z as Vector2).
    /// </summary>
    public static bool GenerateRoad(
        Vector3 startPos, float startRadius,
        Vector3 endPos, float endRadius,
        float width, string? label = null)
    {
        return GenerateRoad(
            new Vector2(startPos.x, startPos.z), startRadius,
            new Vector2(endPos.x, endPos.z), endRadius,
            width, label);
    }

    #endregion

    #region Location Data

    public struct LocationData
    {
        public Vector3 SpawnPoint;
        public float SpawnRadius;
        public List<(string name, Vector3 position, float radius)> BossLocations;
        public List<(string name, Vector3 position, float radius)> AllLocations;
    }

    private static LocationData? GatherLocationData()
    {
        var locationInstances = ZoneSystem.instance.GetLocationList();
        if (locationInstances == null || locationInstances.Count == 0)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning("No location instances found");
            return null;
        }

        Vector3? spawnPoint = null;
        float spawnRadius = 0f;
        var bossLocations = new List<(string name, Vector3 position, float radius)>();
        var allLocations = new List<(string name, Vector3 position, float radius)>();

        foreach (var loc in locationInstances)
        {
            string prefabName = loc.m_location.m_prefab.Name;
            float exteriorRadius = loc.m_location.m_exteriorRadius;

            allLocations.Add((prefabName, loc.m_position, exteriorRadius));

            if (prefabName == "StartTemple")
            {
                spawnPoint = loc.m_position;
                spawnRadius = exteriorRadius;
            }
            else if (BossLocationNames.Contains(prefabName))
            {
                bossLocations.Add((prefabName, loc.m_position, exteriorRadius));
            }
        }

        if (!spawnPoint.HasValue)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning("Could not find spawn point (StartTemple)");
            spawnPoint = Vector3.zero;
        }

        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(
            $"Found spawn at {spawnPoint.Value}, {bossLocations.Count} boss locations, {allLocations.Count} total locations");

        return new LocationData
        {
            SpawnPoint = spawnPoint.Value,
            SpawnRadius = spawnRadius,
            BossLocations = bossLocations,
            AllLocations = allLocations
        };
    }

    #endregion

    #region Generation Methods (Add new methods here)

    /// <summary>
    /// Generates roads from spawn to boss locations, plus inter-boss connections.
    /// </summary>
    private static void GenerateBossRoads(LocationData locations)
    {
        // Sort bosses by distance from spawn
        var sortedBosses = locations.BossLocations
            .OrderBy(b => Vector3.Distance(b.position, locations.SpawnPoint))
            .ToList();

        // Connect spawn to nearby bosses
        int roadsFromSpawn = 0;
        foreach (var boss in sortedBosses)
        {
            if (roadsFromSpawn >= MaxRoadsFromSpawn)
                break;

            float distance = Vector3.Distance(boss.position, locations.SpawnPoint);
            if (distance > MaxRoadLength)
            {
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug($"Skipping {boss.name} - too far ({distance:F0}m)");
                continue;
            }

            bool success = GenerateRoad(
                locations.SpawnPoint, locations.SpawnRadius,
                boss.position, boss.radius,
                RoadWidth,
                $"Spawn -> {boss.name} ({distance:F0}m)");

            if (success)
                roadsFromSpawn++;
        }

        // Connect early-game bosses to each other
        GenerateInterBossRoads(sortedBosses);
    }

    /// <summary>
    /// Generates roads connecting early-game bosses to each other.
    /// </summary>
    private static void GenerateInterBossRoads(List<(string name, Vector3 position, float radius)> bosses)
    {
        int maxAdditionalRoads = 3;
        int additionalRoads = 0;

        var earlyBosses = bosses
            .Where(b => b.name == "Eikthyrnir" || b.name == "GDKing" || b.name == "Bonemass")
            .ToList();

        for (int i = 0; i < earlyBosses.Count && additionalRoads < maxAdditionalRoads; i++)
        {
            for (int j = i + 1; j < earlyBosses.Count && additionalRoads < maxAdditionalRoads; j++)
            {
                float distance = Vector3.Distance(earlyBosses[i].position, earlyBosses[j].position);

                if (distance > MaxRoadLength * 0.7f)
                    continue;

                bool success = GenerateRoad(
                    earlyBosses[i].position, earlyBosses[i].radius,
                    earlyBosses[j].position, earlyBosses[j].radius,
                    RoadWidth * 0.8f,
                    $"{earlyBosses[i].name} -> {earlyBosses[j].name}");

                if (success)
                    additionalRoads++;
            }
        }
    }

    #endregion

    #region Utility Methods

    public static void Reset()
    {
        m_roadsGenerated = false;
        m_pathfinder = null;
        m_roadsGeneratedCount = 0;
        RoadSpatialGrid.Clear();
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

    /// <summary>
    /// Trims a path so it stops at the exterior radius of both endpoints.
    /// </summary>
    private static List<Vector2>? TrimPathToRadii(List<Vector2> path, Vector2 startCenter, float startRadius, Vector2 endCenter, float endRadius)
    {
        if (path == null || path.Count < 2)
            return null;

        // Trim from start: find first point outside start radius
        int startIndex = 0;
        float startRadiusSq = startRadius * startRadius;
        for (int i = 0; i < path.Count; i++)
        {
            if ((path[i] - startCenter).sqrMagnitude > startRadiusSq)
            {
                startIndex = i;
                break;
            }
        }

        // Trim from end: find last point outside end radius
        int endIndex = path.Count - 1;
        float endRadiusSq = endRadius * endRadius;
        for (int i = path.Count - 1; i >= 0; i--)
        {
            if ((path[i] - endCenter).sqrMagnitude > endRadiusSq)
            {
                endIndex = i;
                break;
            }
        }

        // Validate we have enough path remaining
        if (endIndex <= startIndex)
            return null;

        // Extract the trimmed portion
        var trimmedPath = new List<Vector2>();

        // Add edge point at start radius if we trimmed anything
        if (startIndex > 0 && startIndex < path.Count)
        {
            Vector2 edgePoint = CalculateRadiusIntersection(path[startIndex], startCenter, startRadius);
            trimmedPath.Add(edgePoint);
        }

        // Add all points between start and end indices
        for (int i = startIndex; i <= endIndex; i++)
        {
            trimmedPath.Add(path[i]);
        }

        // Add edge point at end radius if we trimmed anything
        if (endIndex < path.Count - 1 && endIndex >= 0)
        {
            Vector2 edgePoint = CalculateRadiusIntersection(path[endIndex], endCenter, endRadius);
            trimmedPath.Add(edgePoint);
        }

        return trimmedPath.Count >= 2 ? trimmedPath : null;
    }

    /// <summary>
    /// Calculates the point on the radius circle in the direction from center to the given point.
    /// </summary>
    private static Vector2 CalculateRadiusIntersection(Vector2 outsidePoint, Vector2 center, float radius)
    {
        Vector2 direction = (outsidePoint - center).normalized;
        return center + direction * radius;
    }

    #endregion
}
