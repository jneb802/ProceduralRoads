using System.Globalization;
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

        ManualRoadCommands.Register();
        new Terminal.ConsoleCommand(
            "road_bake",
            "Road terrain the server writes for players without the mod: what it has written so far; road_bake again to go over every road zone of the current network once more (zones already carrying it are left alone); road_bake zone [x z] for what the server knows and would do about one zone (default: where you stand); road_bake vegetation for the road zones still holding vegetation on the road, most first; road_bake find [x z [radius]] for road zones not generated yet near a point; road_bake server for the server's own zone machinery -- reference position, live zone count, and each peer's zone.",
            (args) =>
            {
                if (args.Length > 1 && args[1] == "zone")
                {
                    Vector3 point = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
                    if (args.Length > 3 &&
                        float.TryParse(args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x) &&
                        float.TryParse(args[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
                        point = new Vector3(x, 0f, z);
                    args.Context.AddString(ServerTerrainBake.DescribeZone(point));
                    return;
                }
                if (args.Length > 1 && args[1] == "vegetation")
                {
                    int max = 5;
                    if (args.Length > 2 && int.TryParse(args[2], out int n))
                        max = n;
                    foreach (string line in ServerTerrainBake.FindVegetationOnRoads(max))
                        args.Context.AddString(line);
                    return;
                }
                if (args.Length > 1 && args[1] == "find")
                {
                    Vector3 from = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
                    float radius = 1000f;
                    if (args.Length > 3 &&
                        float.TryParse(args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fx) &&
                        float.TryParse(args[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fz))
                        from = new Vector3(fx, 0f, fz);
                    if (args.Length > 4 &&
                        float.TryParse(args[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r))
                        radius = r;
                    foreach (string line in ServerTerrainBake.FindUngenerated(from, radius, 5))
                        args.Context.AddString(line);
                    return;
                }
                if (args.Length > 1 && args[1] == "server")
                {
                    foreach (string line in ServerTerrainBake.DescribeServer())
                        args.Context.AddString(line);
                    return;
                }
                if (args.Length > 1 && args[1] == "again")
                {
                    ServerTerrainBake.Requeue();
                    args.Context.AddString("Road zones queued again for the current network");
                }
                args.Context.AddString(ServerTerrainBake.StatusLine());
            },
            isCheat: true,
            isNetwork: false,
            onlyServer: false,
            isSecret: false,
            allowInDevBuild: true);

        new Terminal.ConsoleCommand(
            "road_zone_report",
            "What one zone holds because of the roads, read from the saved data: its terrain compiler's fingerprint, its bridge pieces and its vegetation. road_zone_report [x z] (default: where you stand). The same zone written by a modded client and by the server should report the same thing.",
            (args) =>
            {
                Vector3 point = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
                if (args.Length > 2 &&
                    float.TryParse(args[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(args[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
                    point = new Vector3(x, 0f, z);
                foreach (string line in ZoneReport.Describe(point))
                    args.Context.AddString(line);
            },
            isCheat: true,
            isNetwork: false,
            onlyServer: false,
            isSecret: false,
            allowInDevBuild: true);

        // road_debug - Show detailed road info at player position
        new Terminal.ConsoleCommand(
            "road_regen_island",
            "Clear all roads and regenerate ONLY the island at your position (or road_regen_island <x> <z>), then apply terrain to the loaded zones. Seconds instead of a whole-world generation when iterating on one site.",
            (args) => RegenerateIslandHere(args),
            isCheat: true,
            isNetwork: false,
            onlyServer: false,
            isSecret: false,
            allowInDevBuild: true);

        new Terminal.ConsoleCommand(
            "road_crossings",
            "List the river crossings (fords and bridges) of the road network nearest to you (road_crossings [count=10]).",
            (args) => CrossingsCommand(args),
            isCheat: true,
            isNetwork: false,
            onlyServer: false,
            isSecret: false,
            allowInDevBuild: true);

        new Terminal.ConsoleCommand(
            "road_bridges",
            "Bridges prototype: how many bridge pieces are planned and spawned, or road_bridges respawn to destroy every spawned bridge piece and spawn the current plans again -- into the zones this peer has loaded, and, on a server, into every planned zone the world has already generated. Zones not generated yet get theirs when they are.",
            (args) => BridgesCommand(args),
            isCheat: true,
            isNetwork: false,
            onlyServer: false,
            isSecret: false,
            allowInDevBuild: true);

        new Terminal.ConsoleCommand(
            "road_bridge_repairs",
            "DIAGNOSTIC: the pieces the COMPLETE bridge at the crossing nearest a point has and the shipped one does not -- exactly what a player would replace to close it up: road_bridge_repairs <x> <z>. Nothing is placed; this only says where the missing pieces belong.",
            (args) => BridgeRepairsCommand(args),
            isCheat: true,
            isNetwork: false,
            onlyServer: false,
            isSecret: false,
            allowInDevBuild: true);

        new Terminal.ConsoleCommand(
            "road_site",
            "Inspect the closest location at <x> <z>: saved root, platform estimate and protected radius. Read-only.",
            args => InspectRoadSite(args), isCheat: true);

        new Terminal.ConsoleCommand(
            "road_ends",
            "Compare each location's nearest road point with procedural terrain: road_ends [ring=8] [top=20]. Ring is a radius in metres. CSV also records loaded collision height where available; procedural deltas do not measure the visible rim.",
            (args) => ReportRoadEnds(args),
            isCheat: true,
            isNetwork: false,
            onlyServer: false,
            isSecret: false,
            allowInDevBuild: true);

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
        
        args.Context.AddString(args.Length <= 1 ? "Detecting islands with the road generation settings..."
            : $"Detecting legacy base-height islands (cellSize={cellSize}m, minCells={minCells})...");
        
        // Run detection
        var islands = args.Length <= 1 ? IslandDetector.DetectRoadIslands()
            : IslandDetector.DetectIslands(cellSize, minCells);
        
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

    private static void InspectRoadSite(Terminal.ConsoleEventArgs args)
    {
        if (args.Length != 3 || !float.TryParse(args[1], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float x) ||
            !float.TryParse(args[2], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float z) ||
            float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(z) || float.IsInfinity(z) ||
            ZoneSystem.instance == null || WorldGenerator.instance == null)
        { args.Context.AddString("Usage: road_site <x> <z> in a loaded world"); return; }
        ZoneSystem.LocationInstance? best = null; float distance = 64f;
        foreach (var site in ZoneSystem.instance.GetLocationList())
        {
            float d=Vector2.Distance(new Vector2(x,z),new Vector2(site.m_position.x,site.m_position.z));
            if (d < distance) { best=site; distance=d; }
        }
        if (best == null) { args.Context.AddString("No location within 64m"); return; }
        var centre=new Vector2(best.Value.m_position.x,best.Value.m_position.z);
        float? saved=LocationLevelling.PlacementHeightSource?.Invoke(centre);
        float baseHeight=LocationLevelling.CentreHeight(centre,WorldGenerator.instance);
        float? platform=LocationLevelling.PlatformHeight(baseHeight,LocationLevelling.OpsAt(centre));
        float? approach=LocationLevelling.ApproachHeight(baseHeight,LocationLevelling.OpsAt(centre));
        float radius=RoadSiteProtection.RadiusAt(centre,best.Value.m_location.m_exteriorRadius);
        args.Context.AddString($"Site {best.Value.m_location.m_prefab.Name} at {centre}; savedRoot={saved?.ToString("F3") ?? "unknown"}; base={baseHeight:F3}; platform={platform?.ToString("F3") ?? "unknown"}; approach={approach?.ToString("F3") ?? "unknown"}; protectedRadius={radius:F2}");
    }

    private static void ReportRoadEnds(Terminal.ConsoleEventArgs args)
    {
        float ring = 8f;
        int top = 20;
        if ((args.Length > 1 && (!float.TryParse(args[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out ring) ||
                float.IsNaN(ring) || float.IsInfinity(ring) || ring <= 0f)) ||
            (args.Length > 2 && (!int.TryParse(args[2], out top) || top < 0)))
        {
            args.Context.AddString("Usage: road_ends [positive ring radius in metres=8] [top>=0]");
            return;
        }

        if (ZoneSystem.instance == null || WorldGenerator.instance == null || !RoadSpatialGrid.IsInitialized)
        {
            args.Context.AddString("Error: world or road network not available");
            return;
        }

        var locations = new List<(string name, Vector3 position, float radius)>();
        foreach (var inst in ZoneSystem.instance.GetLocationList())
            locations.Add((inst.m_location.m_prefab.Name, inst.m_position, inst.m_location.m_exteriorRadius));

        var rows = RoadEndReport.Compute(locations, ring, WorldGenerator.instance);

        string path = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "ProceduralRoads.ends.csv");
        var sb = new System.Text.StringBuilder("name,x,z,roadHeight,terrainAtEnd,ringMean,ringMin,ringMax,deltaEnd,deltaRing,sampleKind,ringRadius,locationX,locationZ,loadedGround,roadMinusLoadedGround\n");
        foreach (var r in rows)
        {
            bool loaded = ZoneSystem.instance.GetGroundHeight(new Vector3(r.Point.x, 0f, r.Point.y), out float ground);
            string liveGround = loaded ? ground.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : "";
            string liveDelta = loaded ? (r.RoadHeight - ground).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : "";
            sb.Append(System.FormattableString.Invariant($"{r.Name},{r.Point.x:F1},{r.Point.y:F1},{r.RoadHeight:F2},{r.TerrainAtEnd:F2},{r.RingMean:F2},{r.RingMin:F2},{r.RingMax:F2},{r.DeltaEnd:F2},{r.DeltaRing:F2},nearest-road-point/procedural,{r.RingRadius:F2},{r.LocationCentre.x:F1},{r.LocationCentre.y:F1},{liveGround},{liveDelta}\n"));
        }
        System.IO.File.WriteAllText(path, sb.ToString());

        args.Context.AddString($"{rows.Count} nearest road points -> {path}; worst {Mathf.Min(top, rows.Count)} by |road - ring mean|:");
        args.Context.AddString($"Ring radius {ring:F1} m. Terrain/ring are procedural samples, not final ground. Loaded collision height is recorded separately in the CSV where available.");
        for (int i = 0; i < Mathf.Min(top, rows.Count); i++)
        {
            var r = rows[i];
            args.Context.AddString($"  {r.Name} ({r.Point.x:F0},{r.Point.y:F0}) road={r.RoadHeight:F1} terrain={r.TerrainAtEnd:F1} ring={r.RingMean:F1} [{r.RingMin:F1}..{r.RingMax:F1}] dEnd={r.DeltaEnd:+0.0;-0.0} dRing={r.DeltaRing:+0.0;-0.0}");
        }
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
        Vector2s zoneID = ZoneSystem.GetZone(playerPos);
        
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
        args.Context.AddString("Queuing terrain for loaded zones...");
        int zonesWithRoads = RoadTerrainModifier.ApplyToLoadedZones();
        args.Context.AddString($"Queued road terrain for {zonesWithRoads} visible zones.");
        ReportBridgeRespawn(args, BridgePlacement.RespawnFromPlans());
    }

    /// <summary>Bridge pieces of the old network sit at the old crossings:
    /// after a successful rebuild they go, and the new plans go in.</summary>
    private static void ReportBridgeRespawn(Terminal.ConsoleEventArgs args, (int destroyed, int zones) result)
    {
        if (result.destroyed > 0)
            args.Context.AddString($"Removed {result.destroyed} bridge pieces of the previous network.");
        if (result.zones > 0)
            args.Context.AddString($"Spawned bridges into {result.zones} zone(s).");
    }

    /// <summary>road_crossings [count] lists the river crossings nearest the player.</summary>
    private static void CrossingsCommand(Terminal.ConsoleEventArgs args)
    {
        if (!RoadNetworkGenerator.RoadsAvailable)
        {
            args.Context.AddString("Error: No roads available. Run 'road_generate' first.");
            return;
        }

        IReadOnlyList<RoadCrossing> crossings = RoadNetworkGenerator.GetRoadCrossings();
        args.Context.AddString($"{crossings.Count} river crossing(s) on the roads.");
        if (crossings.Count == 0)
            return;

        int count = 10;
        if (args.Length > 1 && int.TryParse(args[1], out int requested))
            count = requested;
        Vector3 here = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
        Vector2 here2 = new Vector2(here.x, here.z);
        foreach (RoadCrossing site in crossings.OrderBy(c => Vector2.Distance(c.Center, here2)).Take(count))
        {
            args.Context.AddString(
                $"  ({site.Center.x:F0},{site.Center.y:F0}) {Vector2.Distance(site.Center, here2):F0} m away: {site.Kind}{(site.Style != FordStyle.None ? " " + site.Style : "")}, {site.Width:F0} m wide " +
                $"from ({site.FromBank.x:F1},{site.FromBank.y:F1}) to ({site.ToBank.x:F1},{site.ToBank.y:F1}), " +
                $"bed {site.WaterLevel - site.RiverbedHeight:F1} m deep, fairway {site.FairwayWidth:F0} m, " +
                // Pier height, not water depth, is what decides whether vanilla
                // support can reach the deck. The two differ a lot: a 2.3 m deep
                // channel between high banks carries a 12 m structure.
                $"{(site.Kind == CrossingKind.Bridge && WorldGenerator.instance != null ? $"pier {BridgeLayout.PierHeight(site, WorldGenerator.instance):F1} m, " : "")}" +
                $"{BridgePlans.PiecesAt(site)} pieces");
        }
    }

    /// <summary>road_bridges reports the plans; road_bridges respawn destroys every
    /// spawned bridge piece and spawns the current plans again into the loaded
    /// zones (fixture iteration).</summary>
    private static void BridgesCommand(Terminal.ConsoleEventArgs args)
    {
        if (!RoadNetworkGenerator.RoadsAvailable)
        {
            args.Context.AddString("Error: No roads available. Run 'road_generate' first.");
            return;
        }

        if (args.Length > 1 && args[1] == "respawn")
        {
            (int destroyed, int zones) = BridgePlacement.RespawnFromPlans();
            args.Context.AddString($"Destroyed {destroyed} bridge pieces; spawned the current plans into {zones} zone(s). Zones the world has not generated yet get theirs when it does.");
            return;
        }

        List<RoadCrossing> sites = BridgeLayout.DistinctSites(RoadNetworkGenerator.GetRoadCrossings());
        args.Context.AddString(
            $"{sites.Count} crossing site(s), " +
            $"{BridgePlans.TotalPlannedPieces} pieces planned across {BridgePlans.PlannedZoneCount} zone(s), {BridgePlans.SpawnedZones.Count} zone(s) spawned. road_crossings lists them.");
    }

    /// <summary>
    /// What is intentionally missing from a bridge, as coordinates.
    ///
    /// The bridges ship ruined on purpose -- piers outlive decks, a navigation
    /// gap stays clear for boats -- and the claim that goes with that is that a
    /// player can put the missing pieces back with ordinary vanilla ones and
    /// get a continuous, aligned crossing. This command is how that claim is
    /// checked in a running game rather than argued about: it prints the
    /// difference between the completed layout and the shipped one.
    ///
    /// Deterministic, and deliberately computed from the PLAN rather than from
    /// what is standing: two runs on the same world and seed print the same
    /// list, whether or not anything has since decayed or been built.
    /// </summary>
    private static void BridgeRepairsCommand(Terminal.ConsoleEventArgs args)
    {
        if (!RoadNetworkGenerator.RoadsAvailable)
        {
            args.Context.AddString("Error: No roads available. Run 'road_generate' first.");
            return;
        }
        if (WorldGenerator.instance == null)
        {
            args.Context.AddString("Error: no world");
            return;
        }

        Vector2 at;
        if (args.Length >= 3 && float.TryParse(args[1], out float x) && float.TryParse(args[2], out float z))
            at = new Vector2(x, z);
        else if (Player.m_localPlayer != null)
            at = new Vector2(Player.m_localPlayer.transform.position.x, Player.m_localPlayer.transform.position.z);
        else
        {
            args.Context.AddString("Usage: road_bridge_repairs <x> <z>");
            return;
        }

        List<RoadCrossing> sites = BridgeLayout.DistinctSites(RoadNetworkGenerator.GetRoadCrossings());
        if (sites.Count == 0)
        {
            args.Context.AddString("No crossings on this network.");
            return;
        }

        RoadCrossing site = sites.OrderBy(c => Vector2.Distance(c.Center, at)).First();
        int seed = WorldGenerator.instance.GetSeed();
        List<BridgePiece> complete = BridgeLayout.SolveComplete(site, WorldGenerator.instance, seed);
        List<BridgePiece> shipped = BridgeLayout.Solve(site, WorldGenerator.instance, seed);

        (float dropFrom, float dropTo) = BridgeLayout.BankDrop(site, WorldGenerator.instance);
        (bool nearLands, bool farLands) = BridgeLayout.StairRunsLand(site, WorldGenerator.instance);
        args.Context.AddString(
            $"Crossing ({site.Center.x:F0},{site.Center.y:F0}) {site.Kind}{(site.Style != FordStyle.None ? " " + site.Style : "")}, " +
            $"{site.Width:F2} m wide, level deck, bank drop {dropFrom:F2}/{dropTo:F2} m, " +
            $"built {BridgeLayout.BuiltLength(site.Width):F2} m over {BridgeLayout.Bays(site.Width)} bay(s) " +
            $"of {BridgeLayout.StationSpacing():F3} m, deck {BridgeLayout.DeckHalfWidth * 2f:F0} m wide, " +
            $"from ({site.FromBank.x:F2},{site.FromBank.y:F2}) to ({site.ToBank.x:F2},{site.ToBank.y:F2}).");
        if (!nearLands || !farLands)
            args.Context.AddString($"STAIRS UNLANDED: near={(nearLands ? "ok" : "above ground")} far={(farLands ? "ok" : "above ground")} " +
                $"-- the bank falls faster than {BridgeLayout.MaxStairSteps} steps of 1 in 2 can follow.");
        // A site with no turnable line keeps the bearing the router priced --
        // better an off-grid bridge than a road over open water -- but then
        // NONE of its pieces can be replaced by an ordinary hammer, which is
        // exactly what this command is for. Say so before listing them.
        float siteHeading = BridgeLayout.YawDegrees(site.Direction);
        if (!BridgeLayout.HeadingIsPlaceable(siteHeading))
            args.Context.AddString($"HEADING NOT PLACEABLE: this crossing stands at {siteHeading:F2} deg, and the vanilla hammer " +
                $"turns only in {BridgeLayout.PlaceableHeadingStep} deg steps. No admissible line reached land on both banks here, " +
                "so the crossing kept the bearing the router priced. The pieces below are NOT hand-replaceable at this site.");
        int gap = 0, ruined = 0;
        List<string> lines = new();
        foreach (BridgePiece piece in complete)
        {
            if (shipped.Any(b => b.Prefab == piece.Prefab && Vector3.Distance(b.Position, piece.Position) < 0.05f))
                continue;
            // Two reasons a piece is missing, reported apart: the navigation
            // gap is left open on purpose for boats, the rest is ruin.
            bool inGap = BridgeLayout.InNavigationGap(site, site.Along(new Vector2(piece.Position.x, piece.Position.z)));
            if (inGap) gap++; else ruined++;
            lines.Add(string.Format(CultureInfo.InvariantCulture,
                "REPAIR {0} {1} {2:F3} {3:F3} {4:F3} yaw={5:F2} pitch={6:F2} why={7}",
                lines.Count + 1, piece.Prefab, piece.Position.x, piece.Position.y, piece.Position.z,
                piece.YawDegrees, piece.PitchDegrees, inGap ? "gap" : "ruin"));
        }
        args.Context.AddString($"Completed: {complete.Count} pieces. Shipped: {shipped.Count}. Missing: {lines.Count} ({gap} in the navigation gap, {ruined} ruin).");
        foreach (string line in lines)
            args.Context.AddString(line);
        int n = lines.Count;
        args.Context.AddString($"OK: BRIDGE_REPAIRS {n} piece(s) to replace");
    }

    private static void RegenerateIslandHere(Terminal.ConsoleEventArgs args)
    {
        Vector3 pos;
        if (args.Length >= 3 && float.TryParse(args[1], out float x) && float.TryParse(args[2], out float z))
        {
            pos = new Vector3(x, 0f, z);
        }
        else if (Player.m_localPlayer != null)
        {
            pos = Player.m_localPlayer.transform.position;
        }
        else
        {
            args.Context.AddString("No local player; use road_regen_island <x> <z>");
            return;
        }

        args.Context.AddString($"Regenerating island at ({pos.x:F0},{pos.z:F0})...");
        if (!RoadNetworkGenerator.RegenerateIslandAt(pos, out string summary))
        {
            args.Context.AddString($"Failed: {summary}");
            return;
        }

        int zones = RoadTerrainModifier.ApplyToLoadedZones();
        args.Context.AddString(summary);
        args.Context.AddString($"Queued terrain for {zones} loaded zone(s).");
        ReportBridgeRespawn(args, BridgePlacement.RespawnFromPlans());
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
        Vector2s zoneID = ZoneSystem.GetZone(playerPos);

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
                debugMarker.DebugInfo = new RoadPointDebugInfo
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
        Vector2s zoneID = ZoneSystem.GetZone(playerPos);

        // Get road points from current and adjacent zones
        List<RoadSpatialGrid.RoadPoint> nearbyPoints = new List<RoadSpatialGrid.RoadPoint>();
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                Vector2s checkZone = new Vector2s((int)zoneID.x + dx, (int)zoneID.y + dz);
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
        if (!debugInfo.IsBiomeBoundary && debugInfo.PointBiome != debugInfo.Biome00)
            sb.AppendLine($"  Point biome {debugInfo.PointBiome} is not the corners' {debugInfo.Biome00}: " +
                          $"the game renders {debugInfo.Biome00} height here, raw GetHeight uses {debugInfo.PointBiome}");
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
