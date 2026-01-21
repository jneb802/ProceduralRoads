using System.Collections.Generic;
using System.Linq;
using System.Text;
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
    private static List<GameObject> s_debugMarkers = new();

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

        // road_debug_markers - Spawn interactable debug markers in current zone
        new Terminal.ConsoleCommand(
            "road_debug_markers",
            "Spawn interactable debug cubes above road points in current zone. Interact to see smoothing details.",
            (args) => SpawnDebugMarkers(args),
            isCheat: true,
            isNetwork: false,
            onlyServer: false,
            isSecret: false,
            allowInDevBuild: true);

        // road_debug_markers_clear - Remove all debug markers
        new Terminal.ConsoleCommand(
            "road_debug_markers_clear",
            "Remove all spawned debug markers.",
            (args) => ClearDebugMarkers(args),
            isCheat: true,
            isNetwork: false,
            onlyServer: false,
            isSecret: false,
            allowInDevBuild: true);

        // road_debug_log - Log debug info for all road points in radius (for underground/underwater points)
        new Terminal.ConsoleCommand(
            "road_debug_log",
            "Log debug info for all road points within radius. Usage: road_debug_log [radius=15]",
            (args) => LogRoadPointsInRadius(args),
            isCheat: true,
            isNetwork: false,
            onlyServer: false,
            isSecret: false,
            allowInDevBuild: true);

        // road_terrain_compare - Compare WorldGenerator vs Heightmap heights
        new Terminal.ConsoleCommand(
            "road_terrain_compare",
            "Compare WorldGenerator height vs actual Heightmap height at road points. Diagnoses height sampling issues.",
            (args) => CompareTerrainHeights(args),
            isCheat: true,
            isNetwork: false,
            onlyServer: false,
            isSecret: false,
            allowInDevBuild: true);

        // road_biome_check - Show biome info and blending status at player position
        new Terminal.ConsoleCommand(
            "road_biome_check",
            "Show biome info and compare raw vs blended terrain heights. Verifies biome blending fix.",
            (args) => CheckBiomeBlending(args),
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
        // Debug: show current state
        Log.LogDebug($"[road_pins] RoadsGenerated={RoadNetworkGenerator.RoadsGenerated}, RoadsLoadedFromZDO={RoadNetworkGenerator.RoadsLoadedFromZDO}, RoadsAvailable={RoadNetworkGenerator.RoadsAvailable}");
        
        if (!RoadNetworkGenerator.RoadsAvailable)
        {
            args.Context.AddString("Error: No roads available. Run 'road_generate' first.");
            return;
        }

        if (Minimap.instance == null)
        {
            args.Context.AddString("Error: Minimap not available");
            return;
        }

        var roadStarts = RoadNetworkGenerator.GetRoadStartPoints();
        Log.LogDebug($"[road_pins] GetRoadStartPoints returned {roadStarts.Count} points");
        
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

                RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zoneID, roadPoints, heightmap, terrainComp);
                zonesWithRoads++;
            }
        }

        args.Context.AddString($"Applied roads to {zonesWithRoads} visible zones.");
    }

    /// <summary>
    /// Spawn debug markers above road points in the current zone.
    /// </summary>
    private static void SpawnDebugMarkers(Terminal.ConsoleEventArgs args)
    {
        Player player = Player.m_localPlayer;
        if (player == null)
        {
            args.Context.AddString("Error: No local player found");
            return;
        }

        if (!RoadSpatialGrid.IsInitialized)
        {
            args.Context.AddString("Error: Road network not initialized");
            return;
        }

        Vector3 playerPos = player.transform.position;
        Vector2i zoneID = ZoneSystem.GetZone(playerPos);

        var roadPoints = RoadSpatialGrid.GetRoadPointsInZone(zoneID);
        if (roadPoints.Count == 0)
        {
            args.Context.AddString($"No road points in current zone {zoneID}");
            return;
        }

        args.Context.AddString($"Spawning {roadPoints.Count} debug markers in zone {zoneID}...");

        // Clear any existing markers first
        ClearDebugMarkersInternal();

        int spawnedCount = 0;
        int debugInfoCount = 0;

        foreach (var rp in roadPoints)
        {
            // Create a primitive cube
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.name = $"RoadDebugMarker_{rp.p.x:F0}_{rp.p.y:F0}";
            
            // Position above the road surface
            marker.transform.position = new Vector3(rp.p.x, rp.h + 0.5f, rp.p.y);
            marker.transform.localScale = new Vector3(0.3f, 0.3f, 0.3f);
            
            // Set material to white - just modify the existing material's color
            var renderer = marker.GetComponent<Renderer>();
            if (renderer != null && renderer.material != null)
            {
                renderer.material.color = Color.white;
            }
            
            // Add the debug marker component
            var debugMarker = marker.AddComponent<RoadPointDebugMarker>();
            debugMarker.RoadPointPosition = rp.p;
            debugMarker.RoadPointHeight = rp.h;
            
            // Try to get debug info for this point
            if (RoadSpatialGrid.TryGetDebugInfo(rp.p, out var debugInfo))
            {
                debugMarker.DebugInfo = debugInfo;
                debugInfoCount++;
            }
            else
            {
                // Create minimal debug info if not available (e.g., loaded from ZDO)
                debugMarker.DebugInfo = new RoadSpatialGrid.RoadPointDebugInfo
                {
                    PointIndex = -1,
                    TotalPoints = -1,
                    OriginalHeight = rp.h,
                    SmoothedHeight = rp.h,
                    ActualWindowSize = 0
                };
            }
            
            s_debugMarkers.Add(marker);
            spawnedCount++;
        }

        args.Context.AddString($"Spawned {spawnedCount} markers ({debugInfoCount} with full debug info)");
        args.Context.AddString("Interact with markers (E) to see smoothing calculation details");
        args.Context.AddString("Use 'road_debug_markers_clear' to remove them");
    }

    /// <summary>
    /// Clear all debug markers.
    /// </summary>
    private static void ClearDebugMarkers(Terminal.ConsoleEventArgs args)
    {
        int count = ClearDebugMarkersInternal();
        args.Context.AddString($"Removed {count} debug markers");
    }

    /// <summary>
    /// Internal method to clear debug markers.
    /// </summary>
    private static int ClearDebugMarkersInternal()
    {
        int count = s_debugMarkers.Count;
        foreach (var marker in s_debugMarkers)
        {
            if (marker != null)
            {
                Object.Destroy(marker);
            }
        }
        s_debugMarkers.Clear();
        return count;
    }

    /// <summary>
    /// Log debug info for all road points within a radius around the player.
    /// Useful for diagnosing underground/underwater road points that can't be clicked.
    /// </summary>
    private static void LogRoadPointsInRadius(Terminal.ConsoleEventArgs args)
    {
        var player = Player.m_localPlayer;
        if (player == null)
        {
            args.Context.AddString("Error: No local player found");
            return;
        }

        if (!RoadSpatialGrid.IsInitialized)
        {
            args.Context.AddString("Error: Road network not initialized");
            return;
        }

        float radius = 15f;
        if (args.Length > 1 && float.TryParse(args[1], out float parsedRadius))
        {
            radius = parsedRadius;
        }

        Vector3 playerPos = player.transform.position;
        Vector2 playerPos2D = new Vector2(playerPos.x, playerPos.z);
        Vector2i zoneID = ZoneSystem.GetZone(playerPos);

        // Get road points from current and adjacent zones
        List<RoadSpatialGrid.RoadPoint> nearbyPoints = new List<RoadSpatialGrid.RoadPoint>();
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                Vector2i checkZone = new Vector2i(zoneID.x + dx, zoneID.y + dz);
                var zonePoints = RoadSpatialGrid.GetRoadPointsInZone(checkZone);
                foreach (var rp in zonePoints)
                {
                    float dist = Vector2.Distance(rp.p, playerPos2D);
                    if (dist <= radius)
                    {
                        nearbyPoints.Add(rp);
                    }
                }
            }
        }

        if (nearbyPoints.Count == 0)
        {
            args.Context.AddString($"No road points found within {radius}m");
            return;
        }

        // Sort by distance
        nearbyPoints.Sort((a, b) => Vector2.Distance(a.p, playerPos2D).CompareTo(Vector2.Distance(b.p, playerPos2D)));

        args.Context.AddString($"=== Road Points within {radius}m (found {nearbyPoints.Count}) ===");
        
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"\n=== Road Points Debug Log at ({playerPos.x:F0}, {playerPos.z:F0}) ===");
        sb.AppendLine($"Player altitude: {playerPos.y:F1}m, Zone: {zoneID}");
        sb.AppendLine();

        int logged = 0;
        int maxToLog = 20; // Limit output

        foreach (var rp in nearbyPoints)
        {
            float dist = Vector2.Distance(rp.p, playerPos2D);
            
            // Get current terrain height for comparison (both raw and blended)
            float rawTerrain = WorldGenerator.instance?.GetHeight(rp.p.x, rp.p.y) ?? 0f;
            float blendedTerrain = WorldGenerator.instance != null 
                ? BiomeBlendedHeight.GetBlendedHeight(rp.p.x, rp.p.y, WorldGenerator.instance) 
                : 0f;
            float deviation = rp.h - blendedTerrain;
            
            bool hasDebugInfo = RoadSpatialGrid.TryGetDebugInfo(rp.p, out var debugInfo);

            if (logged < maxToLog)
            {
                sb.AppendLine($"[{logged}] pos=({rp.p.x:F1},{rp.p.y:F1}) dist={dist:F1}m");
                sb.AppendLine($"    Road height: {rp.h:F2}m");
                sb.AppendLine($"    Raw terrain: {rawTerrain:F2}m, Blended terrain: {blendedTerrain:F2}m");
                sb.AppendLine($"    Deviation from blended: {deviation:F2}m");
                
                if (hasDebugInfo)
                {
                    sb.AppendLine($"    Original (at generation): {debugInfo.OriginalHeight:F2}m, Window size: {debugInfo.ActualWindowSize}");
                }
                else
                {
                    sb.AppendLine($"    (No generation debug info available)");
                }
                sb.AppendLine();
                logged++;
            }
        }

        sb.AppendLine($"Summary: {nearbyPoints.Count} points logged");

        // Log to BepInEx
        Log.LogInfo(sb.ToString());

        args.Context.AddString($"Logged {logged} points (see BepInEx console for details)");
    }

    /// <summary>
    /// Compare WorldGenerator height vs actual Heightmap height at road points.
    /// This helps diagnose if there's a discrepancy between procedural generation and rendered terrain.
    /// </summary>
    private static void CompareTerrainHeights(Terminal.ConsoleEventArgs args)
    {
        var player = Player.m_localPlayer;
        if (player == null)
        {
            args.Context.AddString("Error: No local player found");
            return;
        }

        Vector3 playerPos = player.transform.position;
        Vector2 playerPos2D = new Vector2(playerPos.x, playerPos.z);
        
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"\n=== Terrain Height Comparison at ({playerPos.x:F0}, {playerPos.z:F0}) ===");
        sb.AppendLine($"Player Y position: {playerPos.y:F2}m");
        sb.AppendLine();

        // Get WorldGenerator height at player position
        float wgHeight = WorldGenerator.instance?.GetHeight(playerPos.x, playerPos.z) ?? 0f;
        sb.AppendLine($"WorldGenerator.GetHeight at player: {wgHeight:F2}m");

        // Try to get Heightmap height at player position (static method with out parameter)
        float hmHeight = 0f;
        bool foundHeightmap = Heightmap.GetHeight(playerPos, out hmHeight);
        if (foundHeightmap)
        {
            sb.AppendLine($"Heightmap.GetHeight at player: {hmHeight:F2}m");
            sb.AppendLine($"Difference (Heightmap - WorldGen): {hmHeight - wgHeight:F2}m");
        }
        else
        {
            sb.AppendLine("No heightmap found at player position");
        }

        // Also try ZoneSystem.GetGroundHeight
        float groundHeight = ZoneSystem.instance?.GetGroundHeight(playerPos) ?? 0f;
        sb.AppendLine($"ZoneSystem.GetGroundHeight: {groundHeight:F2}m");
        sb.AppendLine();

        // Sample a grid of points around the player
        sb.AppendLine("Grid sample (5m spacing):");
        sb.AppendLine("Pos(X,Z) | WorldGen | Heightmap | Diff");
        sb.AppendLine("---------|----------|-----------|-----");
        
        int largeDiscrepancies = 0;
        for (int dx = -2; dx <= 2; dx++)
        {
            for (int dz = -2; dz <= 2; dz++)
            {
                float x = playerPos.x + dx * 5f;
                float z = playerPos.z + dz * 5f;
                Vector3 samplePos = new Vector3(x, 0, z);
                
                float wg = WorldGenerator.instance?.GetHeight(x, z) ?? 0f;
                string hmStr = "N/A";
                string diffStr = "";
                
                if (Heightmap.GetHeight(samplePos, out float hm))
                {
                    hmStr = $"{hm:F1}m";
                    float diff = hm - wg;
                    diffStr = $"{diff:+0.0;-0.0}m";
                    if (Mathf.Abs(diff) > 2f)
                        largeDiscrepancies++;
                }
                
                // Only log corners and center to reduce spam
                if ((dx == 0 && dz == 0) || (Mathf.Abs(dx) == 2 && Mathf.Abs(dz) == 2))
                {
                    sb.AppendLine($"({x:F0},{z:F0}) | {wg:F1}m | {hmStr} | {diffStr}");
                }
            }
        }

        sb.AppendLine();
        
        // Now check road points
        if (RoadSpatialGrid.IsInitialized)
        {
            var nearbyPoints = RoadSpatialGrid.GetRoadPointsNearPosition(playerPos, 15f);
            if (nearbyPoints.Count > 0)
            {
                sb.AppendLine($"Road points comparison ({nearbyPoints.Count} points):");
                sb.AppendLine("Pos | RoadH | WorldGen | Heightmap | WG-HM Diff");
                sb.AppendLine("----|-------|----------|-----------|----------");
                
                int shown = 0;
                foreach (var rp in nearbyPoints)
                {
                    if (shown >= 10) break;
                    
                    Vector3 rpPos = new Vector3(rp.p.x, 0, rp.p.y);
                    float wg = WorldGenerator.instance?.GetHeight(rp.p.x, rp.p.y) ?? 0f;
                    string hmStr = "N/A";
                    string diffStr = "";
                    
                    if (Heightmap.GetHeight(rpPos, out float hm))
                    {
                        hmStr = $"{hm:F1}m";
                        float diff = wg - hm;
                        diffStr = $"{diff:+0.0;-0.0}m";
                    }
                    
                    sb.AppendLine($"({rp.p.x:F0},{rp.p.y:F0}) | {rp.h:F1}m | {wg:F1}m | {hmStr} | {diffStr}");
                    shown++;
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine($"Large discrepancies (>2m): {largeDiscrepancies}");
        
        Log.LogInfo(sb.ToString());
        args.Context.AddString("Terrain comparison logged (see BepInEx console)");
        args.Context.AddString($"WorldGen: {wgHeight:F2}m, Heightmap: {(foundHeightmap ? hmHeight.ToString("F2") + "m" : "N/A")}, Ground: {groundHeight:F2}m");
    }

    /// <summary>
    /// Check biome blending at player position.
    /// Shows the raw WorldGenerator.GetHeight vs the biome-blended height we use for roads.
    /// This helps verify the biome boundary fix is working.
    /// </summary>
    private static void CheckBiomeBlending(Terminal.ConsoleEventArgs args)
    {
        var player = Player.m_localPlayer;
        if (player == null)
        {
            args.Context.AddString("Error: No local player found");
            return;
        }

        var worldGen = WorldGenerator.instance;
        if (worldGen == null)
        {
            args.Context.AddString("Error: WorldGenerator not available");
            return;
        }

        Vector3 playerPos = player.transform.position;
        float wx = playerPos.x;
        float wz = playerPos.z;
        
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"\n=== Biome Blending Check at ({wx:F0}, {wz:F0}) ===");
        sb.AppendLine();

        // Use the new debug info API for comprehensive data
        var debugInfo = BiomeBlendedHeight.GetBlendDebugInfo(wx, wz, worldGen);
        
        // Get biome at center
        Heightmap.Biome centerBiome = worldGen.GetBiome(wx, wz);
        sb.AppendLine($"Center biome: {centerBiome}");
        sb.AppendLine();

        // Show heightmap geometry (64m chunks centered on zones)
        sb.AppendLine($"Heightmap geometry (64m chunks centered on zones):");
        sb.AppendLine($"  Zone center: ({debugInfo.ZoneCenter.x:F0}, {debugInfo.ZoneCenter.y:F0})");
        sb.AppendLine($"  Heightmap corner: ({debugInfo.HeightmapCorner.x:F0}, {debugInfo.HeightmapCorner.y:F0})");
        sb.AppendLine($"  Local position in heightmap: ({debugInfo.LocalPosition.x:F1}, {debugInfo.LocalPosition.y:F1})");
        sb.AppendLine($"  Interpolation params: tx={debugInfo.Tx:F3}, tz={debugInfo.Tz:F3}");
        sb.AppendLine();

        sb.AppendLine($"Corner biomes (64m heightmap at {debugInfo.HeightmapCorner.x:F0},{debugInfo.HeightmapCorner.y:F0}):");
        sb.AppendLine($"  Bottom-left:  {debugInfo.Biome00}");
        sb.AppendLine($"  Bottom-right: {debugInfo.Biome10}");
        sb.AppendLine($"  Top-left:     {debugInfo.Biome01}");
        sb.AppendLine($"  Top-right:    {debugInfo.Biome11}");
        sb.AppendLine($"  At biome boundary: {debugInfo.IsBiomeBoundary}");
        sb.AppendLine();

        // Get actual rendered terrain height from Heightmap
        float heightmapHeight = 0f;
        bool hasHeightmap = Heightmap.GetHeight(playerPos, out heightmapHeight);
        
        sb.AppendLine("Height comparison:");
        sb.AppendLine($"  Player position Y:            {playerPos.y:F2}m");
        if (hasHeightmap)
            sb.AppendLine($"  Heightmap (rendered terrain): {heightmapHeight:F2}m");
        else
            sb.AppendLine($"  Heightmap: NOT LOADED");
        sb.AppendLine($"  Our BiomeBlendedHeight:       {debugInfo.BlendedHeight:F2}m");
        sb.AppendLine($"  Raw WorldGenerator.GetHeight: {debugInfo.RawHeight:F2}m");
        sb.AppendLine($"  Height difference (blend-raw): {debugInfo.HeightDifference:+0.00;-0.00}m");
        sb.AppendLine();
        
        if (hasHeightmap)
        {
            float ourError = debugInfo.BlendedHeight - heightmapHeight;
            sb.AppendLine($"  ERROR (our blend vs actual): {ourError:+0.00;-0.00}m");
            if (Mathf.Abs(ourError) > 1f)
                sb.AppendLine($"  ** WARNING: Blending doesn't match rendered terrain! **");
            else if (Mathf.Abs(ourError) < 0.5f)
                sb.AppendLine($"  ✓ Good match with rendered terrain");
        }
        sb.AppendLine();

        // If at boundary, show what each biome would return
        if (debugInfo.IsBiomeBoundary)
        {
            sb.AppendLine("Per-biome heights at this location:");
            sb.AppendLine($"  {debugInfo.Biome00}: {debugInfo.Height00:F2}m");
            if (debugInfo.Biome10 != debugInfo.Biome00)
                sb.AppendLine($"  {debugInfo.Biome10}: {debugInfo.Height10:F2}m");
            if (debugInfo.Biome01 != debugInfo.Biome00 && debugInfo.Biome01 != debugInfo.Biome10)
                sb.AppendLine($"  {debugInfo.Biome01}: {debugInfo.Height01:F2}m");
            if (debugInfo.Biome11 != debugInfo.Biome00 && debugInfo.Biome11 != debugInfo.Biome10 && debugInfo.Biome11 != debugInfo.Biome01)
                sb.AppendLine($"  {debugInfo.Biome11}: {debugInfo.Height11:F2}m");
            sb.AppendLine();
            
            sb.AppendLine("Blend calculation:");
            float hBottom = Mathf.Lerp(debugInfo.Height00, debugInfo.Height10, debugInfo.Tx);
            float hTop = Mathf.Lerp(debugInfo.Height01, debugInfo.Height11, debugInfo.Tx);
            sb.AppendLine($"  Bottom edge (h00->h10 @ tx={debugInfo.Tx:F2}): {hBottom:F2}m");
            sb.AppendLine($"  Top edge (h01->h11 @ tx={debugInfo.Tx:F2}):    {hTop:F2}m");
            sb.AppendLine($"  Final (bottom->top @ tz={debugInfo.Tz:F2}):   {debugInfo.BlendedHeight:F2}m");
            sb.AppendLine();
        }

        // Sample heights in cardinal directions to show gradient
        sb.AppendLine("Height gradient (10m spacing):");
        float[] offsets = { -20, -10, 0, 10, 20 };
        
        sb.AppendLine("  X direction:");
        foreach (float offset in offsets)
        {
            float raw = worldGen.GetHeight(wx + offset, wz);
            float blended = BiomeBlendedHeight.GetBlendedHeight(wx + offset, wz, worldGen);
            Heightmap.Biome biome = worldGen.GetBiome(wx + offset, wz);
            sb.AppendLine($"    X+{offset:+00;-00}m: raw={raw:F1}m, blended={blended:F1}m, diff={blended-raw:+0.0;-0.0}m [{biome}]");
        }
        
        sb.AppendLine("  Z direction:");
        foreach (float offset in offsets)
        {
            float raw = worldGen.GetHeight(wx, wz + offset);
            float blended = BiomeBlendedHeight.GetBlendedHeight(wx, wz + offset, worldGen);
            Heightmap.Biome biome = worldGen.GetBiome(wx, wz + offset);
            sb.AppendLine($"    Z+{offset:+00;-00}m: raw={raw:F1}m, blended={blended:F1}m, diff={blended-raw:+0.0;-0.0}m [{biome}]");
        }

        Log.LogInfo(sb.ToString());
        
        args.Context.AddString($"Biome: {centerBiome}, At boundary: {debugInfo.IsBiomeBoundary}");
        if (hasHeightmap)
            args.Context.AddString($"Heightmap: {heightmapHeight:F2}m, Blended: {debugInfo.BlendedHeight:F2}m, Error: {debugInfo.BlendedHeight - heightmapHeight:+0.0;-0.0}m");
        else
            args.Context.AddString($"Raw: {debugInfo.RawHeight:F2}m, Blended: {debugInfo.BlendedHeight:F2}m");
        args.Context.AddString("Full details logged (see BepInEx console)");
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
