using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Harmony patches for ZoneSystem to handle road generation and terrain modification.
/// </summary>
public static class ZoneSystem_Patch
{
    private static readonly Dictionary<Vector2i, List<ZoneSystem.ClearArea>> s_roadClearAreasCache = 
        new Dictionary<Vector2i, List<ZoneSystem.ClearArea>>();
    
    private static int s_coordLogCount = 0;
    
    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.Start))]
    public static class ZoneSystem_Start_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ZoneSystem __instance)
        {
            RoadNetworkGenerator.Initialize();
            __instance.GenerateLocationsCompleted += OnLocationsGenerated;
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Subscribed to GenerateLocationsCompleted event");
        }
    }

    private static void OnLocationsGenerated()
    {
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Location generation complete...");
        RoadNetworkGenerator.MarkLocationsReady();
        s_roadClearAreasCache.Clear();

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

    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.PlaceVegetation))]
    public static class ZoneSystem_PlaceVegetation_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(Vector2i zoneID, List<ZoneSystem.ClearArea> clearAreas)
        {
            if (!RoadNetworkGenerator.RoadsGenerated)
                return;

            if (!s_roadClearAreasCache.TryGetValue(zoneID, out List<ZoneSystem.ClearArea> roadClearAreas))
            {
                roadClearAreas = CreateRoadClearAreas(zoneID);
                s_roadClearAreasCache[zoneID] = roadClearAreas;
            }

            clearAreas.AddRange(roadClearAreas);
        }
    }

    private static List<ZoneSystem.ClearArea> CreateRoadClearAreas(Vector2i zoneID)
    {
        List<ZoneSystem.ClearArea> clearAreas = new List<ZoneSystem.ClearArea>();
        List<RoadSpatialGrid.RoadPoint> roadPoints = RoadSpatialGrid.GetRoadPointsInZone(zoneID);

        if (roadPoints.Count == 0)
            return clearAreas;

        HashSet<Vector2i> processedCells = new HashSet<Vector2i>();

        foreach (RoadSpatialGrid.RoadPoint roadPoint in roadPoints)
        {
            Vector2i cell = new Vector2i(
                Mathf.RoundToInt(roadPoint.p.x / RoadConstants.VegetationClearSampleInterval),
                Mathf.RoundToInt(roadPoint.p.y / RoadConstants.VegetationClearSampleInterval));

            if (processedCells.Contains(cell))
                continue;
            processedCells.Add(cell);

            Vector3 center = new Vector3(
                cell.x * RoadConstants.VegetationClearSampleInterval,
                0f,
                cell.y * RoadConstants.VegetationClearSampleInterval);
            
            float radius = roadPoint.w * RoadConstants.VegetationClearMultiplier;
            clearAreas.Add(new ZoneSystem.ClearArea(center, radius));
        }

        return clearAreas;
    }

    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.SpawnZone))]
    public static class ZoneSystem_SpawnZone_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ZoneSystem __instance, Vector2i zoneID, ZoneSystem.SpawnMode mode, ref bool __result)
        {
            if (mode == ZoneSystem.SpawnMode.Client)
                return;

            if (__result && RoadNetworkGenerator.RoadsGenerated)
            {
                List<RoadSpatialGrid.RoadPoint> roadPoints = RoadSpatialGrid.GetRoadPointsInZone(zoneID);
                if (roadPoints.Count > 0)
                    ApplyRoadTerrainMods(zoneID, roadPoints);
            }
        }
    }

    /// <summary>
    /// Apply terrain mods for road points in a zone.
    /// </summary>
    private static void ApplyRoadTerrainMods(Vector2i zoneID, List<RoadSpatialGrid.RoadPoint> roadPoints)
    {
        TerrainContext? context = GetTerrainContext(zoneID);
        if (context == null)
            return;

        ModificationStats stats = ModifyVertexHeights(zoneID, roadPoints, context.Value);
        ApplyRoadPaint(roadPoints, context.Value.TerrainComp, stats.PaintedCells);
        FinalizeTerrainMods(zoneID, roadPoints.Count, stats, context.Value);
    }

    /// <summary>
    /// Public entry point for applying road terrain mods to a specific zone.
    /// Used by console commands to force-update loaded zones.
    /// </summary>
    public static void ApplyRoadTerrainModsPublic(Vector2i zoneID, List<RoadSpatialGrid.RoadPoint> roadPoints, 
        Heightmap heightmap, TerrainComp terrainComp)
    {
        if (roadPoints == null || roadPoints.Count == 0)
            return;

        int gridSize = terrainComp.m_width + 1;
        if (terrainComp.m_levelDelta == null || terrainComp.m_levelDelta.Length < gridSize * gridSize)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug($"Zone {zoneID}: TerrainComp arrays not initialized");
            return;
        }

        TerrainContext context = new TerrainContext
        {
            Heightmap = heightmap,
            TerrainComp = terrainComp,
            HeightmapPosition = heightmap.transform.position,
            GridSize = gridSize,
            VertexSpacing = RoadConstants.ZoneSize / terrainComp.m_width
        };

        ModificationStats stats = ModifyVertexHeights(zoneID, roadPoints, context);
        ApplyRoadPaint(roadPoints, context.TerrainComp, stats.PaintedCells);
        FinalizeTerrainMods(zoneID, roadPoints.Count, stats, context);
    }

    private struct TerrainContext
    {
        public Heightmap Heightmap;
        public TerrainComp TerrainComp;
        public Vector3 HeightmapPosition;
        public int GridSize;
        public float VertexSpacing;
    }

    private static TerrainContext? GetTerrainContext(Vector2i zoneID)
    {
        Heightmap heightmap = Heightmap.FindHeightmap(ZoneSystem.GetZonePos(zoneID));
        TerrainComp? terrainComp = heightmap?.GetAndCreateTerrainCompiler();
        int gridSize = (terrainComp?.m_width ?? 0) + 1;

        if (heightmap == null || terrainComp == null || !terrainComp.m_nview.IsOwner() ||
            terrainComp.m_levelDelta == null || terrainComp.m_levelDelta.Length < gridSize * gridSize)
            return null;

        return new TerrainContext
        {
            Heightmap = heightmap,
            TerrainComp = terrainComp,
            HeightmapPosition = heightmap.transform.position,
            GridSize = gridSize,
            VertexSpacing = RoadConstants.ZoneSize / terrainComp.m_width
        };
    }

    private struct ModificationStats
    {
        public int VerticesModified;
        public int VerticesChecked;
        public HashSet<Vector2i> PaintedCells;
    }

    private static ModificationStats ModifyVertexHeights(Vector2i zoneID, List<RoadSpatialGrid.RoadPoint> roadPoints, TerrainContext context)
    {
        ModificationStats stats = new ModificationStats { PaintedCells = new HashSet<Vector2i>() };

        LogCoordinateDebug(zoneID, roadPoints, context);

        for (int vz = 0; vz < context.GridSize; vz++)
        {
            for (int vx = 0; vx < context.GridSize; vx++)
            {
                stats.VerticesChecked++;
                
                Vector3 vertexWorldPos = new Vector3(
                    context.HeightmapPosition.x + (vx - context.TerrainComp.m_width / 2f) * context.VertexSpacing,
                    0f,
                    context.HeightmapPosition.z + (vz - context.TerrainComp.m_width / 2f) * context.VertexSpacing);
                Vector2 vertexPos2D = new Vector2(vertexWorldPos.x, vertexWorldPos.z);
                
                BlendResult blendResult = CalculateBlendedHeight(roadPoints, vertexPos2D);
                if (blendResult.InfluencingPoints == 0)
                    continue;

                float baseHeight = BiomeBlendedHeight.GetBlendedHeight(vertexWorldPos.x, vertexWorldPos.z, WorldGenerator.instance);
                float finalHeight = Mathf.Lerp(baseHeight, blendResult.TargetHeight, blendResult.MaxBlend);
                float delta = Mathf.Clamp(finalHeight - baseHeight, RoadConstants.TerrainDeltaMin, RoadConstants.TerrainDeltaMax);

                if (Mathf.Abs(delta) > RoadConstants.MinHeightDeltaThreshold || blendResult.MaxBlend > RoadConstants.MinBlendForModification)
                {
                    int index = vz * context.GridSize + vx;
                    context.TerrainComp.m_levelDelta[index] = delta;
                    context.TerrainComp.m_smoothDelta[index] = 0f;
                    context.TerrainComp.m_modifiedHeight[index] = true;
                    stats.VerticesModified++;
                    
                    if (stats.VerticesModified <= RoadConstants.MaxVertexModificationLogs)
                    {
                        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(
                            $"[VERTEX] Zone {zoneID} v[{vx},{vz}]: pos=({vertexWorldPos.x:F1},{vertexWorldPos.z:F1}), " +
                            $"base={baseHeight:F2}m, target={blendResult.TargetHeight:F2}m, blend={blendResult.MaxBlend:F2}, delta={delta:F2}m");
                    }
                }
            }
        }

        return stats;
    }

    private struct BlendResult
    {
        public float TargetHeight;
        public float MaxBlend;
        public int InfluencingPoints;
    }

    private static BlendResult CalculateBlendedHeight(List<RoadSpatialGrid.RoadPoint> roadPoints, Vector2 vertexPos)
    {
        float weightedHeightSum = 0f;
        float totalWeight = 0f;
        float maxBlend = 0f;
        int influencingPoints = 0;

        foreach (RoadSpatialGrid.RoadPoint rp in roadPoints)
        {
            float distSq = (rp.p - vertexPos).sqrMagnitude;
            float influenceRadius = (rp.w * 0.5f) + RoadConstants.TerrainBlendMargin;
            float influenceRadiusSq = influenceRadius * influenceRadius;
            
            if (distSq < influenceRadiusSq)
            {
                float dist = Mathf.Sqrt(distSq);
                float t = dist / influenceRadius;
                float pointBlend = 1f - Mathf.SmoothStep(0f, 1f, t);
                float weight = pointBlend * pointBlend;
                
                weightedHeightSum += rp.h * weight;
                totalWeight += weight;
                influencingPoints++;
                
                if (pointBlend > maxBlend)
                    maxBlend = pointBlend;
            }
        }

        return new BlendResult
        {
            TargetHeight = influencingPoints > 0 ? weightedHeightSum / totalWeight : 0f,
            MaxBlend = maxBlend,
            InfluencingPoints = influencingPoints
        };
    }

    private static void ApplyRoadPaint(List<RoadSpatialGrid.RoadPoint> roadPoints, TerrainComp terrainComp, HashSet<Vector2i> paintedCells)
    {
        Heightmap hmap = terrainComp.m_hmap;
        if (hmap == null)
            return;
            
        int gridSize = terrainComp.m_width + 1;
        Vector3 terrainPos = hmap.transform.position;
        float scale = hmap.m_scale;
        
        foreach (RoadSpatialGrid.RoadPoint roadPoint in roadPoints)
        {
            Vector2i cell = new Vector2i(
                Mathf.RoundToInt(roadPoint.p.x / RoadConstants.PaintDedupeInterval),
                Mathf.RoundToInt(roadPoint.p.y / RoadConstants.PaintDedupeInterval));
            
            if (paintedCells.Contains(cell))
                continue;
            paintedCells.Add(cell);
            
            Vector3 worldPos = new Vector3(roadPoint.p.x - 0.5f, 0f, roadPoint.p.y - 0.5f);
            Vector3 localPos = worldPos - terrainPos;
            int halfWidth = (terrainComp.m_width + 1) / 2;
            int centerX = Mathf.FloorToInt(localPos.x / scale + 0.5f) + halfWidth;
            int centerY = Mathf.FloorToInt(localPos.z / scale + 0.5f) + halfWidth;
            
            float radius = roadPoint.w * 0.5f;
            float radiusInVertices = radius / scale;
            int radiusCeil = Mathf.CeilToInt(radiusInVertices);
            
            for (int dy = -radiusCeil; dy <= radiusCeil; dy++)
            {
                for (int dx = -radiusCeil; dx <= radiusCeil; dx++)
                {
                    int vx = centerX + dx;
                    int vy = centerY + dy;
                    
                    if (vx < 0 || vy < 0 || vx >= gridSize || vy >= gridSize)
                        continue;
                    
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    if (dist > radiusInVertices)
                        continue;
                    
                    float blendFactor = 1f - Mathf.Clamp01(dist / radiusInVertices);
                    blendFactor = Mathf.Pow(blendFactor, 0.1f);
                    
                    int index = vy * gridSize + vx;
                    
                    Color currentColor = terrainComp.m_paintMask[index];
                    float alpha = currentColor.a;
                    Color newColor = Color.Lerp(currentColor, Heightmap.m_paintMaskPaved, blendFactor);
                    newColor.a = alpha;
                    
                    terrainComp.m_modifiedPaint[index] = true;
                    terrainComp.m_paintMask[index] = newColor;
                }
            }
        }
    }

    private static void FinalizeTerrainMods(Vector2i zoneID, int roadPointCount, ModificationStats stats, TerrainContext context)
    {
        int paintOps = stats.PaintedCells.Count;
        
        if (stats.VerticesModified > 0 || paintOps > 0)
        {
            context.TerrainComp.Save();
            context.Heightmap.Poke(true);
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(
                $"Zone {zoneID}: {stats.VerticesModified}/{stats.VerticesChecked} vertices modified, {paintOps} paint cells");
        }
        else if (roadPointCount > 0)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning(
                $"Zone {zoneID}: {roadPointCount} road points but 0 vertices matched! Coordinate mismatch?");
        }
    }

    private static void LogCoordinateDebug(Vector2i zoneID, List<RoadSpatialGrid.RoadPoint> roadPoints, TerrainContext context)
    {
        if (s_coordLogCount >= RoadConstants.MaxCoordDebugLogs)
            return;

        s_coordLogCount++;
        
        float halfSize = context.TerrainComp.m_width / 2f * context.VertexSpacing;
        RoadSpatialGrid.RoadPoint firstRoadPoint = roadPoints.Count > 0 ? roadPoints[0] : default;
        
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(
            $"[COORD DEBUG] Zone {zoneID}: hmPos=({context.HeightmapPosition.x:F1},{context.HeightmapPosition.z:F1}), " +
            $"vertices cover X[{context.HeightmapPosition.x - halfSize:F1},{context.HeightmapPosition.x + halfSize:F1}], " +
            $"first road point=({firstRoadPoint.p.x:F1},{firstRoadPoint.p.y:F1}), width={firstRoadPoint.w:F1}m");
    }

    /// <summary>
    /// Hook into ZDOMan.PrepareSave to persist global road data before world save.
    /// </summary>
    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.PrepareSave))]
    public static class ZDOMan_PrepareSave_Patch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (RoadNetworkGenerator.RoadsGenerated)
            {
                RoadNetworkGenerator.SaveGlobalRoadData();
            }
        }
    }

    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.OnDestroy))]
    public static class ZoneSystem_OnDestroy_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(ZoneSystem __instance)
        {
            __instance.GenerateLocationsCompleted -= OnLocationsGenerated;
            RoadNetworkGenerator.Reset();
            s_roadClearAreasCache.Clear();
            s_coordLogCount = 0;

            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Road data cleared on world unload");
        }
    }

    /// <summary>
    /// Hook into Game.SpawnPlayer to enable deferred road loading for existing worlds.
    /// </summary>
    [HarmonyPatch(typeof(Game), nameof(Game.SpawnPlayer))]
    public static class Game_SpawnPlayer_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Vector3 spawnPoint)
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
    }
}
