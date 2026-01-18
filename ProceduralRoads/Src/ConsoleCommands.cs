using System.Linq;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Console commands for debugging road generation.
/// Commands:
///   road_debug - Show detailed road info at player position
/// </summary>
public static class ConsoleCommands
{
    private static bool s_commandsRegistered = false;
    private static ManualLogSource Log => ProceduralRoadsPlugin.ProceduralRoadsLogger;

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

        s_commandsRegistered = true;
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Road console commands registered");
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
        Log.LogInfo($"Height stats: min={minHeight:F2}m, max={maxHeight:F2}m, spread={heightSpread:F2}m, avg={avgHeight:F2}m");

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
