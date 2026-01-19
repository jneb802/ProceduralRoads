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
            __instance.GenerateLocationsCompleted += OnLocationsGenerated;
            RoadNetworkGenerator.Initialize();
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Subscribed to GenerateLocationsCompleted event");
        }
    }

    private static void OnLocationsGenerated()
    {
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo("Location generation complete, generating roads...");
        RoadNetworkGenerator.GenerateRoads();
        s_roadClearAreasCache.Clear();
    }

    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.PlaceVegetation))]
    public static class ZoneSystem_PlaceVegetation_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(Vector2i zoneID, List<ZoneSystem.ClearArea> clearAreas)
        {
            if (!ProceduralRoadsPlugin.EnableRoads.Value || !RoadNetworkGenerator.RoadsGenerated)
                return;

            if (!s_roadClearAreasCache.TryGetValue(zoneID, out var roadClearAreas))
            {
                roadClearAreas = CreateRoadClearAreas(zoneID);
                s_roadClearAreasCache[zoneID] = roadClearAreas;
            }

            clearAreas.AddRange(roadClearAreas);
        }
    }

    private static List<ZoneSystem.ClearArea> CreateRoadClearAreas(Vector2i zoneID)
    {
        var clearAreas = new List<ZoneSystem.ClearArea>();
        var roadPoints = RoadSpatialGrid.GetRoadPointsInZone(zoneID);

        if (roadPoints.Count == 0)
            return clearAreas;

        HashSet<Vector2i> processedCells = new HashSet<Vector2i>();

        foreach (var roadPoint in roadPoints)
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
            if (!__result || mode == ZoneSystem.SpawnMode.Client)
                return;

            if (!ProceduralRoadsPlugin.EnableRoads.Value || !RoadNetworkGenerator.RoadsGenerated)
                return;

            var roadPoints = RoadSpatialGrid.GetRoadPointsInZone(zoneID);
            if (roadPoints.Count == 0)
                return;

            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug($"Applying {roadPoints.Count} road points in zone {zoneID}");
            ApplyRoadTerrainMods(zoneID, roadPoints);
        }
    }

    private static void ApplyRoadTerrainMods(Vector2i zoneID, List<RoadSpatialGrid.RoadPoint> roadPoints)
    {
        var context = GetTerrainContext(zoneID);
        if (context == null)
            return;

        var stats = ModifyVertexHeights(zoneID, roadPoints, context.Value);
        ApplyRoadPaint(roadPoints, context.Value.TerrainComp, stats.PaintedCells);
        FinalizeTerrainMods(zoneID, roadPoints.Count, stats, context.Value);
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
        Vector3 zonePos = ZoneSystem.GetZonePos(zoneID);
        Heightmap heightmap = Heightmap.FindHeightmap(zonePos);

        if (heightmap == null)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug($"No heightmap found for zone {zoneID}");
            return null;
        }

        TerrainComp terrainComp = heightmap.GetAndCreateTerrainCompiler();
        if (terrainComp == null)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug($"Could not get TerrainComp for zone {zoneID}");
            return null;
        }

        if (!terrainComp.m_nview.IsOwner())
            return null;

        int gridSize = terrainComp.m_width + 1;
        if (terrainComp.m_levelDelta == null || terrainComp.m_levelDelta.Length < gridSize * gridSize)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug($"Zone {zoneID}: TerrainComp arrays not initialized");
            return null;
        }

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
        var stats = new ModificationStats { PaintedCells = new HashSet<Vector2i>() };

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
                
                var blendResult = CalculateBlendedHeight(roadPoints, vertexPos2D);
                if (blendResult.InfluencingPoints == 0)
                    continue;

                float baseHeight = WorldGenerator.instance.GetHeight(vertexWorldPos.x, vertexWorldPos.z);
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
                        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(
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

        foreach (var rp in roadPoints)
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
        foreach (var roadPoint in roadPoints)
        {
            Vector2i cell = new Vector2i(
                Mathf.RoundToInt(roadPoint.p.x / RoadConstants.PaintDedupeInterval),
                Mathf.RoundToInt(roadPoint.p.y / RoadConstants.PaintDedupeInterval));
            
            if (paintedCells.Contains(cell))
                continue;
            paintedCells.Add(cell);
            
            Vector3 paintPos = new Vector3(roadPoint.p.x, 0f, roadPoint.p.y);
            
            TerrainOp.Settings paintSettings = new TerrainOp.Settings
            {
                m_level = false,
                m_smooth = false,
                m_paintCleared = true,
                m_paintType = TerrainModifier.PaintType.Paved,
                m_paintRadius = roadPoint.w * 0.5f,
                m_paintHeightCheck = false,
            };
            
            terrainComp.DoOperation(paintPos, paintSettings);
        }
    }

    private static void FinalizeTerrainMods(Vector2i zoneID, int roadPointCount, ModificationStats stats, TerrainContext context)
    {
        int paintOps = stats.PaintedCells.Count;
        
        if (stats.VerticesModified > 0 || paintOps > 0)
        {
            context.TerrainComp.Save();
            context.Heightmap.Poke(false);
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(
                $"Zone {zoneID}: {stats.VerticesModified}/{stats.VerticesChecked} vertices modified, {paintOps} paint ops");
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
        var firstRoadPoint = roadPoints.Count > 0 ? roadPoints[0] : default;
        
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(
            $"[COORD DEBUG] Zone {zoneID}: hmPos=({context.HeightmapPosition.x:F1},{context.HeightmapPosition.z:F1}), " +
            $"vertices cover X[{context.HeightmapPosition.x - halfSize:F1},{context.HeightmapPosition.x + halfSize:F1}], " +
            $"first road point=({firstRoadPoint.p.x:F1},{firstRoadPoint.p.y:F1}), width={firstRoadPoint.w:F1}m");
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
}
