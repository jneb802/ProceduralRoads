using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using Splatform;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Console commands for road generation and debugging.
/// Commands:
///   road_generate - Generate roads for existing worlds
///   road_pins - Place pins at road start points
///   road_islands - Detect and display islands with map pins
///   road_clearpins - Remove all pins added by this mod
///   road_debug - Show detailed road info at player position
/// </summary>
public static class ConsoleCommands
{
    private static bool s_commandsRegistered = false;
    private static ManualLogSource Log => ProceduralRoadsPlugin.ProceduralRoadsLogger;
    private static List<Minimap.PinData> s_modPins = new();

    /// <summary>
    /// Register console commands. Called from Terminal.InitTerminal patch.
    /// </summary>
    public static void RegisterCommands()
    {
        if (s_commandsRegistered)
            return;

        // road_debug - Show detailed road info at player position
        new Terminal.ConsoleCommand(
            "road_debug",
            "Show detailed road point info near player position (for debugging terrain issues)",
            (args) => DebugRoadPoints(args),
            isCheat: true,
            isNetwork: false,
            onlyServer: false,
            isSecret: false,
            allowInDevBuild: true);

        // road_islands - Detect and visualize islands
        new Terminal.ConsoleCommand(
            "road_islands",
            "Detect islands and place map pins at their centers. Args: [cellSize] [minCells]",
            (args) => DetectAndShowIslands(args),
            isCheat: true,
            isNetwork: false,
            onlyServer: false,
            isSecret: false,
            allowInDevBuild: true);

        // road_generate - Generate roads for existing worlds
        new Terminal.ConsoleCommand(
            "road_generate",
            "Generate roads for an existing world. Use after adding mod to existing save.",
            (args) => GenerateRoadsCommand(args),
            isCheat: true,
            isNetwork: false,
            onlyServer: false,
            isSecret: false,
            allowInDevBuild: true);

        // road_pins - Show road start points on map
        new Terminal.ConsoleCommand(
            "road_pins",
            "Place map pins at the start point of each generated road.",
            (args) => ShowRoadStartPins(args),
            isCheat: true,
            isNetwork: false,
            onlyServer: false,
            isSecret: false,
            allowInDevBuild: true);

        // road_clearpins - Remove all pins added by this mod
        new Terminal.ConsoleCommand(
            "road_clearpins",
            "Remove all map pins added by ProceduralRoads commands.",
            (args) => ClearAllModPins(args),
            isCheat: true,
            isNetwork: false,
            onlyServer: false,
            isSecret: false,
            allowInDevBuild: true);

        s_commandsRegistered = true;
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Road console commands registered");
    }
    
    /// <summary>
    /// Detect islands and place map pins to visualize them.
    /// </summary>
    private static void DetectAndShowIslands(Terminal.ConsoleEventArgs args)
    {
        // Parse arguments
        float cellSize = 128f;
        int minCells = 10;
        
        if (args.Length > 1 && float.TryParse(args[1], out float cs))
        {
            cellSize = cs;
        }
        if (args.Length > 2 && int.TryParse(args[2], out int mc))
        {
            minCells = mc;
        }
        
        // Check prerequisites
        if (WorldGenerator.instance == null)
        {
            args.Context.AddString("Error: WorldGenerator not available. Are you in a world?");
            return;
        }
        
        if (Minimap.instance == null)
        {
            args.Context.AddString("Error: Minimap not available");
            return;
        }
        
        args.Context.AddString($"Detecting islands (cellSize={cellSize}m, minCells={minCells})...");
        
        // Run detection
        var islands = IslandDetector.DetectIslands(cellSize, minCells);
        
        if (islands.Count == 0)
        {
            args.Context.AddString("No islands detected!");
            return;
        }
        
        args.Context.AddString($"Found {islands.Count} islands:");
        
        // Create pins for each island
        int pinCount = 0;
        foreach (var island in islands)
        {
            string summary = IslandDetector.GetIslandSummary(island);
            args.Context.AddString($"  {summary}");
            Log.LogInfo(summary);
            
            // Add pin at island center
            Vector3 pinPos = new Vector3(island.Center.x, 0, island.Center.y);
            string pinName = $"Island {island.Id} ({island.ApproxArea/1000000:F1}km²)";
            
            var pin = Minimap.instance.AddPin(pinPos, Minimap.PinType.Icon3, pinName, false, false, 0L, PlatformUserID.None);
            if (pin != null)
            {
                s_modPins.Add(pin);
                pinCount++;
            }
        }
        
        args.Context.AddString($"Added {pinCount} map pins. Use 'road_clearpins' to remove them.");
        args.Context.AddString("Open map (M) to see island locations.");
    }
    
    /// <summary>
    /// Show road start points on the map.
    /// </summary>
    private static void ShowRoadStartPins(Terminal.ConsoleEventArgs args)
    {
        if (!RoadNetworkGenerator.RoadsGenerated)
        {
            args.Context.AddString("Error: No roads generated. Run 'road_generate' first.");
            return;
        }

        if (Minimap.instance == null)
        {
            args.Context.AddString("Error: Minimap not available");
            return;
        }

        var roadStarts = RoadNetworkGenerator.GetRoadStartPoints();
        if (roadStarts.Count == 0)
        {
            args.Context.AddString("No road start points recorded.");
            return;
        }

        int pinCount = 0;
        foreach (var start in roadStarts)
        {
            Vector3 pinPos = new Vector3(start.position.x, 0, start.position.y);
            var pin = Minimap.instance.AddPin(pinPos, Minimap.PinType.Icon0, start.label, false, false, 0L, PlatformUserID.None);
            if (pin != null)
            {
                s_modPins.Add(pin);
                pinCount++;
            }
        }

        args.Context.AddString($"Added {pinCount} road start pins. Use 'road_clearpins' to remove them.");
    }

    /// <summary>
    /// Clear all pins added by this mod.
    /// </summary>
    private static void ClearAllModPins(Terminal.ConsoleEventArgs args)
    {
        if (Minimap.instance == null)
        {
            args.Context.AddString("Error: Minimap not available");
            return;
        }

        int count = s_modPins.Count;
        foreach (var pin in s_modPins)
        {
            if (pin != null)
            {
                Minimap.instance.RemovePin(pin);
            }
        }
        s_modPins.Clear();

        args.Context.AddString($"Removed {count} pins.");
    }

    /// <summary>
    /// Debug road points near player position.
    /// Shows detailed info about road points, heights, and terrain.
    /// </summary>
    private static void DebugRoadPoints(Terminal.ConsoleEventArgs args)
    {
        Player player = Player.m_localPlayer;
        if (player == null)
        {
            args.Context.AddString("Error: No local player found");
            return;
        }

        Vector3 playerPos = player.transform.position;
        float searchRadius = 15f; // Search within 15m

        // Get zone info
        Vector2i zoneID = ZoneSystem.GetZone(playerPos);
        
        args.Context.AddString($"=== Road Debug at ({playerPos.x:F1}, {playerPos.z:F1}) ===");
        args.Context.AddString($"Zone: {zoneID}, Player altitude: {playerPos.y:F1}m");
        Log.LogInfo($"=== Road Debug at ({playerPos.x:F1}, {playerPos.z:F1}) ===");
        Log.LogInfo($"Zone: {zoneID}, Player altitude: {playerPos.y:F1}m");

        // Get terrain height at player position
        float terrainHeight = 0f;
        if (WorldGenerator.instance != null)
        {
            terrainHeight = WorldGenerator.instance.GetHeight(playerPos.x, playerPos.z);
            args.Context.AddString($"WorldGenerator height at position: {terrainHeight:F2}m");
            Log.LogInfo($"WorldGenerator height at position: {terrainHeight:F2}m");
        }

        // Get road points near player
        var nearbyPoints = RoadSpatialGrid.GetRoadPointsNearPosition(playerPos, searchRadius);
        
        if (nearbyPoints.Count == 0)
        {
            args.Context.AddString($"No road points within {searchRadius}m");
            Log.LogInfo($"No road points within {searchRadius}m");
            return;
        }

        args.Context.AddString($"Found {nearbyPoints.Count} road points within {searchRadius}m:");
        Log.LogInfo($"Found {nearbyPoints.Count} road points within {searchRadius}m:");

        // Calculate statistics
        float minHeight = float.MaxValue;
        float maxHeight = float.MinValue;
        float sumHeight = 0f;
        
        foreach (var rp in nearbyPoints)
        {
            if (rp.h < minHeight) minHeight = rp.h;
            if (rp.h > maxHeight) maxHeight = rp.h;
            sumHeight += rp.h;
        }
        
        float avgHeight = sumHeight / nearbyPoints.Count;
        float heightSpread = maxHeight - minHeight;

        args.Context.AddString($"Height stats: min={minHeight:F2}m, max={maxHeight:F2}m, spread={heightSpread:F2}m, avg={avgHeight:F2}m");
        Log.LogDebug($"Height stats: min={minHeight:F2}m, max={maxHeight:F2}m, spread={heightSpread:F2}m, avg={avgHeight:F2}m");

        // Show closest points with details
        int showCount = System.Math.Min(10, nearbyPoints.Count);
        args.Context.AddString($"Closest {showCount} points:");
        Log.LogInfo($"Closest {showCount} points:");
        
        Vector2 playerPos2D = new Vector2(playerPos.x, playerPos.z);
        
        for (int i = 0; i < showCount; i++)
        {
            var rp = nearbyPoints[i];
            float dist = Vector2.Distance(rp.p, playerPos2D);
            float localTerrain = WorldGenerator.instance != null 
                ? WorldGenerator.instance.GetHeight(rp.p.x, rp.p.y) 
                : 0f;
            float delta = rp.h - localTerrain;
            
            string info = $"  [{i}] pos=({rp.p.x:F1},{rp.p.y:F1}) dist={dist:F1}m h={rp.h:F2}m terrain={localTerrain:F2}m delta={delta:F2}m";
            args.Context.AddString(info);
            Log.LogInfo(info);
        }

        // Check for height discontinuities (large height changes between adjacent points)
        // Sort by X then Z to find neighbors
        var sortedByPos = nearbyPoints.OrderBy(p => p.p.x).ThenBy(p => p.p.y).ToList();
        
        float maxGradient = 0f;
        int discontinuities = 0;
        
        for (int i = 0; i < sortedByPos.Count - 1; i++)
        {
            var p1 = sortedByPos[i];
            var p2 = sortedByPos[i + 1];
            float posDist = Vector2.Distance(p1.p, p2.p);
            
            if (posDist > 0 && posDist < 3f) // Only check nearby points
            {
                float gradient = Mathf.Abs(p2.h - p1.h) / posDist;
                if (gradient > maxGradient) maxGradient = gradient;
                if (gradient > 0.5f) discontinuities++; // More than 0.5m per 1m = steep
            }
        }

        args.Context.AddString($"Max gradient: {maxGradient:F2}m/m, steep transitions: {discontinuities}");
        Log.LogInfo($"Max gradient: {maxGradient:F2}m/m, steep transitions: {discontinuities}");

        // Diagnosis hints
        if (heightSpread > 3f)
        {
            args.Context.AddString("WARNING: Large height spread - possible intersection of different roads");
            Log.LogWarning("Large height spread - possible intersection of different roads");
        }
        if (maxGradient > 0.5f)
        {
            args.Context.AddString("WARNING: Steep gradient detected - may cause terrain cliffs");
            Log.LogWarning("Steep gradient detected - may cause terrain cliffs");
        }
        if (nearbyPoints.Count < 5)
        {
            args.Context.AddString("NOTE: Few road points - may be edge of road path");
            Log.LogInfo("Few road points - may be edge of road path");
        }
    }

    /// <summary>
    /// Generate roads for an existing world that was created before the mod was installed.
    /// </summary>
    private static void GenerateRoadsCommand(Terminal.ConsoleEventArgs args)
    {
        // Check prerequisites
        if (WorldGenerator.instance == null)
        {
            args.Context.AddString("Error: WorldGenerator not available. Are you in a world?");
            return;
        }

        if (ZoneSystem.instance == null)
        {
            args.Context.AddString("Error: ZoneSystem not available. Are you in a world?");
            return;
        }

        if (!ProceduralRoadsPlugin.EnableRoads.Value)
        {
            args.Context.AddString("Error: Roads are disabled in config. Enable them first.");
            return;
        }

        // Check if roads already exist
        bool alreadyGenerated = RoadNetworkGenerator.RoadsGenerated;
        if (alreadyGenerated)
        {
            args.Context.AddString("Roads already generated. Forcing regeneration...");
        }
        else
        {
            args.Context.AddString("Generating roads for existing world...");
        }

        Log.LogInfo("Manual road generation triggered via console command");

        // Generate roads (force=true to regenerate if needed)
        RoadNetworkGenerator.GenerateRoads(force: true);

        if (!RoadNetworkGenerator.RoadsGenerated)
        {
            args.Context.AddString("Road generation failed. Check the log for details.");
            return;
        }

        args.Context.AddString($"Road generation complete!");
        args.Context.AddString($"  Total road points: {RoadSpatialGrid.TotalRoadPoints}");
        args.Context.AddString($"  Total road length: {RoadSpatialGrid.TotalRoadLength:F0}m");
        args.Context.AddString($"  Grid cells with roads: {RoadSpatialGrid.GridCellsWithRoads}");

        // Apply roads to currently loaded zones
        args.Context.AddString("Applying to loaded zones...");

        var heightmaps = Heightmap.GetAllHeightmaps();
        int zonesWithRoads = 0;

        if (heightmaps != null)
        {
            foreach (var heightmap in heightmaps)
            {
                if (heightmap == null) continue;

                Vector3 hmPos = heightmap.transform.position;
                Vector2i zoneID = ZoneSystem.GetZone(hmPos);

                var roadPoints = RoadSpatialGrid.GetRoadPointsInZone(zoneID);
                if (roadPoints.Count == 0) continue;

                TerrainComp terrainComp = heightmap.GetAndCreateTerrainCompiler();
                if (terrainComp == null || !terrainComp.m_nview.IsOwner()) continue;

                ZoneSystem_Patch.ApplyRoadTerrainModsPublic(zoneID, roadPoints, heightmap, terrainComp);
                zonesWithRoads++;
            }
        }

        args.Context.AddString($"Applied roads to {zonesWithRoads} visible zones.");
    }
}

/// <summary>
/// Harmony patch to register console commands when Terminal initializes.
/// </summary>
[HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
public static class Terminal_InitTerminal_Patch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        ConsoleCommands.RegisterCommands();
    }
}
