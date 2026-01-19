using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Orchestrates road network generation after POI locations are known.
/// </summary>
public static class RoadNetworkGenerator
{
    private static ManualLogSource Log => Log;
    
    private static readonly HashSet<string> BossLocationNames = new HashSet<string>
    {
        "Eikthyrnir",
        "GDKing",
        "Bonemass",
        "Dragonqueen",
        "GoblinKing",
        "SeekerQueen",
    };

    private static readonly Dictionary<string, int> LocationPriorities = new()
    {
        // Bosses - always include (100)
        { "Eikthyrnir", 100 },
        { "GDKing", 100 },
        { "Bonemass", 100 },
        { "Dragonqueen", 100 },
        { "GoblinKing", 100 },
        { "SeekerQueen", 100 },
        
        // Major dungeons (80)
        { "Crypt4", 80 },
        { "SunkenCrypt4", 80 },
        { "MountainCave02", 80 },
        { "TrollCave02", 80 },
        { "Crypt3", 75 },
        
        // Mistlands major (75)
        { "Mistlands_DvergrTownEntrance1", 75 },
        { "Mistlands_DvergrTownEntrance2", 75 },
        { "Mistlands_Harbour1", 70 },
        
        // Villages/Farms (60)
        { "WoodVillage1", 60 },
        { "WoodFarm1", 55 },
        
        // Mistlands structures (50)
        { "Mistlands_GuardTower1_new", 50 },
        { "Mistlands_GuardTower2_new", 50 },
        { "Mistlands_GuardTower3_new", 50 },
        { "Mistlands_Lighthouse1_new", 50 },
        { "Mistlands_Excavation1", 45 },
        { "Mistlands_Excavation2", 45 },
        { "Mistlands_Excavation3", 45 },
        
        // Towers (40)
        { "StoneTower1", 40 },
        { "StoneTower3", 40 },
        
        // Ruins/minor (30)
        { "Mistlands_GuardTower1_ruined_new", 30 },
        { "Mistlands_GuardTower3_ruined_new", 30 },
        { "StoneTowerRuins03", 30 },
        { "StoneTowerRuins04", 30 },
        { "StoneTowerRuins05", 30 },
        { "StoneTowerRuins07", 30 },
        { "StoneTowerRuins08", 30 },
        { "StoneTowerRuins09", 30 },
        { "StoneTowerRuins10", 30 },
        { "StoneHenge1", 25 },
        { "StoneHenge2", 25 },
        { "StoneHenge3", 25 },
        { "SwampHut5", 25 },
        { "SwampRuin1", 25 },
        { "SwampRuin2", 25 },
    };
    
    private const int DefaultPriority = 20;
    private const int MinLocationsPerIsland = 2;
    private const int MaxLocationsPerIsland = 12;
    private const float AreaPerLocation = 2_000_000f; // 2 km² per additional location

    /// <summary>
    /// Location names registered via API or config for road generation.
    /// </summary>
    private static readonly HashSet<string> RegisteredLocationNames = new HashSet<string>();

    #region Location Registration API

    /// <summary>
    /// Register a location name for road generation.
    /// Call this from other mods to include custom locations in the road network.
    /// </summary>
    public static void RegisterLocation(string locationName)
    {
        if (string.IsNullOrWhiteSpace(locationName))
            return;
        
        string trimmed = locationName.Trim();
        if (RegisteredLocationNames.Add(trimmed))
        {
            Log.LogDebug($"Registered location for roads: {trimmed}");
        }
    }

    /// <summary>
    /// Unregister a location name from road generation.
    /// </summary>
    public static void UnregisterLocation(string locationName)
    {
        if (string.IsNullOrWhiteSpace(locationName))
            return;
        
        string trimmed = locationName.Trim();
        if (RegisteredLocationNames.Remove(trimmed))
        {
            Log.LogDebug($"Unregistered location from roads: {trimmed}");
        }
    }

    /// <summary>
    /// Get all currently registered location names.
    /// </summary>
    public static IReadOnlyCollection<string> GetRegisteredLocations()
    {
        return RegisteredLocationNames;
    }

    #endregion

    public static float RoadWidth = 4f;
    public static int MaxRoadsFromSpawn = 5;
    public static float MaxRoadLength = 3000f;

    private static bool m_roadsGenerated = false;
    private static RoadPathfinder? m_pathfinder;
    private static int m_roadsGeneratedCount = 0;

    public static bool RoadsGenerated => m_roadsGenerated;

    public static void Initialize() => Reset();

    /// <summary>
    /// Main entry point for road generation. Calls various generation methods.
    /// </summary>
    /// <param name="force">If true, regenerate roads even if already generated (for existing worlds)</param>
    public static void GenerateRoads(bool force = false)
    {
        if (m_roadsGenerated && !force)
        {
            Log.LogDebug("Roads already generated, skipping");
            return;
        }
        
        // If forcing regeneration, reset state first
        if (force && m_roadsGenerated)
        {
            Log.LogDebug("Force regenerating roads...");
            Reset();
        }

        if (!ProceduralRoadsPlugin.EnableRoads.Value)
        {
            Log.LogDebug("Road generation disabled in config");
            return;
        }

        if (WorldGenerator.instance == null)
        {
            Log.LogWarning("WorldGenerator not available, cannot generate roads");
            return;
        }

        if (ZoneSystem.instance == null)
        {
            Log.LogWarning("ZoneSystem not available, cannot generate roads");
            return;
        }

        Log.LogDebug("Starting road network generation...");

        DateTime startTime = DateTime.Now;
        m_pathfinder = new RoadPathfinder(WorldGenerator.instance);
        m_roadsGeneratedCount = 0;

        // Gather all location data
        var locations = GatherLocationData();
        if (locations == null)
            return;

        // Detect islands
        var islands = IslandDetector.DetectIslands();
        Log.LogDebug($"Generating roads for {islands.Count} islands");

        // Generate roads per island (DEBUG: limit to 5 islands)
        foreach (var island in islands.Take(5))
        {
            var islandLocations = GetLocationsOnIsland(island, locations.Value.AllLocations);
            if (islandLocations.Count == 0) continue;
            
            // Filter to priority locations based on island size
            int maxLocs = GetMaxLocationsForIsland(island);
            var selected = SelectLocations(islandLocations, maxLocs);
            
            Log.LogDebug(
                $"Island {island.Id}: {islandLocations.Count} candidates -> {selected.Count} selected (max {maxLocs}, area {island.ApproxArea/1_000_000:F1}km²)");
            
            // Check if this island contains StartTemple
            bool isStarterIsland = island.ContainsPoint(locations.Value.SpawnPoint);
            
            if (isStarterIsland)
            {
                GenerateIslandRoads(island, selected, 
                    locations.Value.SpawnPoint, locations.Value.SpawnRadius);
            }
            else
            {
                GenerateIslandRoads(island, selected);
            }
        }

        TimeSpan elapsed = DateTime.Now - startTime;
        LogGenerationStats(m_roadsGeneratedCount, elapsed);

        // Finalize the road network and compute version hash for zone caching
        RoadSpatialGrid.FinalizeRoadNetwork();
        
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
            Log.LogWarning("GenerateRoad called without active pathfinder");
            return false;
        }

        List<Vector2>? path = m_pathfinder.FindPath(startCenter, endCenter);

        // Attempt to keep UI responsive during generation
        UnityEngine.Canvas.ForceUpdateCanvases();

        if (path == null || path.Count < 2)
        {
            if (label != null)
                Log.LogWarning($"Could not find path: {label}");
            return false;
        }

        // Trim path to stop at location radii
        path = TrimPathToRadii(path, startCenter, startRadius, endCenter, endRadius);

        if (path == null || path.Count < 2)
        {
            if (label != null)
                Log.LogWarning($"Path too short after trimming: {label}");
            return false;
        }

        RoadSpatialGrid.AddRoadPath(path, width, WorldGenerator.instance);
        m_roadsGeneratedCount++;

        if (label != null)
            Log.LogDebug($"Generated road: {label} ({path.Count} waypoints)");

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
            Log.LogWarning("No location instances found");
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
            Log.LogWarning("Could not find spawn point (StartTemple)");
            spawnPoint = Vector3.zero;
        }

        Log.LogDebug(
            $"Found spawn at {spawnPoint.Value}, {bossLocations.Count} boss locations, {allLocations.Count} total locations");

        return new LocationData
        {
            SpawnPoint = spawnPoint.Value,
            SpawnRadius = spawnRadius,
            BossLocations = bossLocations,
            AllLocations = allLocations
        };
    }

    private static List<(string name, Vector3 position, float radius)> GetLocationsOnIsland(
        Island island, List<(string name, Vector3 position, float radius)> allLocations)
    {
        var result = new List<(string name, Vector3 position, float radius)>();
        foreach (var loc in allLocations)
        {
            if (island.ContainsPoint(loc.position) && IsRoadLocation(loc.name))
                result.Add(loc);
        }
        return result;
    }

    private static bool IsRoadLocation(string locationName)
    {
        return BossLocationNames.Contains(locationName) ||
               LocationPriorities.ContainsKey(locationName) ||
               RegisteredLocationNames.Contains(locationName);
    }

    private static int GetMaxLocationsForIsland(Island island)
    {
        int scaled = MinLocationsPerIsland + (int)(island.ApproxArea / AreaPerLocation);
        return Mathf.Clamp(scaled, MinLocationsPerIsland, MaxLocationsPerIsland);
    }

    private static List<(string name, Vector3 position, float radius)> SelectLocations(
        List<(string name, Vector3 position, float radius)> candidates, int maxCount)
    {
        if (candidates.Count <= maxCount)
            return candidates;
        
        return candidates
            .OrderByDescending(loc => GetLocationPriority(loc.name))
            .Take(maxCount)
            .ToList();
    }

    private static int GetLocationPriority(string locationName)
    {
        return LocationPriorities.TryGetValue(locationName, out int priority) ? priority : DefaultPriority;
    }

    #endregion

    #region Island Road Strategies

    private static void GenerateChainRoads(
        Vector3 startPos, float startRadius,
        List<(string name, Vector3 position, float radius)> locations)
    {
        if (locations.Count == 0) return;
        
        var unvisited = new List<(string name, Vector3 position, float radius)>(locations);
        Vector3 current = startPos;
        float currentRadius = startRadius;
        string currentName = "Start";
        
        while (unvisited.Count > 0)
        {
            int nearestIdx = 0;
            float nearestDist = float.MaxValue;
            for (int i = 0; i < unvisited.Count; i++)
            {
                float dist = Vector3.Distance(current, unvisited[i].position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestIdx = i;
                }
            }
            
            var nearest = unvisited[nearestIdx];
            unvisited.RemoveAt(nearestIdx);
            
            GenerateRoad(current, currentRadius, nearest.position, nearest.radius, RoadWidth,
                $"{currentName} -> {nearest.name}");
            
            current = nearest.position;
            currentRadius = nearest.radius;
            currentName = nearest.name;
        }
    }

    private static void GenerateMSTRoads(
        Vector3 startPos, float startRadius,
        List<(string name, Vector3 position, float radius)> locations)
    {
        if (locations.Count == 0) return;
        
        // Build node list: start point + all locations
        var nodes = new List<(string name, Vector3 position, float radius)>();
        nodes.Add(("Start", startPos, startRadius));
        nodes.AddRange(locations);
        
        // Prim's algorithm for MST
        var inTree = new bool[nodes.Count];
        var minEdge = new float[nodes.Count];
        var parent = new int[nodes.Count];
        
        for (int i = 0; i < nodes.Count; i++)
        {
            minEdge[i] = float.MaxValue;
            parent[i] = -1;
        }
        
        minEdge[0] = 0;
        
        for (int iter = 0; iter < nodes.Count; iter++)
        {
            int u = -1;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (!inTree[i] && (u == -1 || minEdge[i] < minEdge[u]))
                    u = i;
            }
            
            if (u == -1 || minEdge[u] == float.MaxValue) break;
            inTree[u] = true;
            
            for (int v = 0; v < nodes.Count; v++)
            {
                if (!inTree[v])
                {
                    float dist = Vector3.Distance(nodes[u].position, nodes[v].position);
                    if (dist < minEdge[v])
                    {
                        minEdge[v] = dist;
                        parent[v] = u;
                    }
                }
            }
        }
        
        // Generate roads for MST edges
        for (int i = 1; i < nodes.Count; i++)
        {
            if (parent[i] >= 0)
            {
                var from = nodes[parent[i]];
                var to = nodes[i];
                GenerateRoad(from.position, from.radius, to.position, to.radius, RoadWidth,
                    $"{from.name} -> {to.name}");
            }
        }
    }

    private static void GenerateIslandRoads(
        Island island,
        List<(string name, Vector3 position, float radius)> islandLocations,
        Vector3? overrideStart = null,
        float overrideStartRadius = 0f)
    {
        if (islandLocations.Count == 0) return;
        
        // Determine start point
        Vector3 startPos;
        float startRadius;
        if (overrideStart.HasValue)
        {
            startPos = overrideStart.Value;
            startRadius = overrideStartRadius;
        }
        else
        {
            Vector2 edge = island.GetEdgePoint();
            startPos = new Vector3(edge.x, 0, edge.y);
            startRadius = 0f;
        }
        
        // Pick strategy based on island ID (deterministic)
        bool useMST = (island.Id % 2) == 0;
        
        Log.LogDebug(
            $"Island {island.Id}: {islandLocations.Count} locations, strategy={(useMST ? "MST" : "Chain")}");
        
        if (useMST)
            GenerateMSTRoads(startPos, startRadius, islandLocations);
        else
            GenerateChainRoads(startPos, startRadius, islandLocations);
    }

    #endregion

    #region Generation Methods

    // Boss progression order (game progression chain)
    private static readonly string[] BossProgressionOrder = new[]
    {
        "Eikthyrnir",   // Meadows
        "GDKing",       // Black Forest
        "Bonemass",     // Swamp
        "Dragonqueen",  // Mountain
        "GoblinKing",   // Plains
        "SeekerQueen",  // Mistlands
    };

    /// <summary>
    /// Generates roads following boss progression:
    /// StartTemple -> Eikthyrnir -> GDKing -> Bonemass -> Dragonqueen -> GoblinKing -> SeekerQueen
    /// </summary>
    private static void GenerateBossRoads(LocationData locations)
    {
        // Build a lookup for boss locations by name
        var bossLookup = new Dictionary<string, (string name, Vector3 position, float radius)>();
        foreach (var boss in locations.BossLocations)
        {
            bossLookup[boss.name] = boss;
        }

        // Start from spawn point
        Vector3 currentPos = locations.SpawnPoint;
        float currentRadius = locations.SpawnRadius;
        string currentName = "StartTemple";

        // Connect through the progression chain
        foreach (string bossName in BossProgressionOrder)
        {
            if (!bossLookup.TryGetValue(bossName, out var boss))
            {
                Log.LogDebug($"Boss {bossName} not found in world, skipping");
                continue;
            }

            float distance = Vector3.Distance(currentPos, boss.position);

            GenerateRoad(
                currentPos, currentRadius,
                boss.position, boss.radius,
                RoadWidth,
                $"{currentName} -> {bossName} ({distance:F0}m)");

            // Move to this boss for the next connection
            currentPos = boss.position;
            currentRadius = boss.radius;
            currentName = bossName;
        }
    }

    /// <summary>
    /// Generates roads to custom locations registered via API or config.
    /// Connection strategy is pending future implementation.
    /// </summary>
    private static void GenerateLocationRoads(LocationData locations)
    {
        // Merge API-registered locations with config locations
        var allRegistered = new HashSet<string>(RegisteredLocationNames);
        foreach (var configLocation in ProceduralRoadsPlugin.GetConfigLocationNames())
        {
            allRegistered.Add(configLocation);
        }

        if (allRegistered.Count == 0)
        {
            Log.LogDebug("No custom locations registered for road generation");
            return;
        }

        Log.LogDebug($"Registered location names: [{string.Join(", ", allRegistered)}]");

        // Find matching locations in the world
        var matchedLocations = new List<(string name, UnityEngine.Vector3 position, float radius)>();
        foreach (var loc in locations.AllLocations)
        {
            if (allRegistered.Contains(loc.name))
            {
                matchedLocations.Add(loc);
            }
        }

        if (matchedLocations.Count == 0)
        {
            Log.LogWarning(
                $"Registered locations not found in world: {string.Join(", ", allRegistered)}");
            return;
        }

        // Log summary: count per location type
        var countsByType = matchedLocations.GroupBy(l => l.name)
            .Select(g => $"{g.Key}({g.Count()})")
            .ToArray();
        Log.LogDebug(
            $"Custom locations: {string.Join(", ", countsByType)}");

        // TODO: Connection strategy - implement road connections between locations
        // This is intentionally left as a stub. The connection logic (how to string
        // locations together) will be implemented separately as its own module.
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
        var log = Log;

        log.LogDebug("=== Road Generation Summary ===");
        log.LogDebug($"  Roads generated: {roadsGenerated}");
        log.LogDebug($"  Total road points: {RoadSpatialGrid.TotalRoadPoints}");
        log.LogDebug($"  Total road length: {RoadSpatialGrid.TotalRoadLength:F0}m");
        log.LogDebug($"  Grid cells with roads: {RoadSpatialGrid.GridCellsWithRoads}");

        if (roadsGenerated > 0)
        {
            log.LogDebug($"  Avg points/road: {RoadSpatialGrid.TotalRoadPoints / (float)roadsGenerated:F0}");
            log.LogDebug($"  Avg length/road: {RoadSpatialGrid.TotalRoadLength / roadsGenerated:F0}m");
        }

        log.LogDebug($"  Generation time: {elapsed.TotalSeconds:F2}s");
        log.LogDebug($"  Road width: {RoadWidth}m");
        log.LogDebug("===============================");
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
