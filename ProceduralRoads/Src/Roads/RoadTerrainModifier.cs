using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Handles terrain height and paint modifications for roads.
/// </summary>
public static class RoadTerrainModifier
{
    private static int s_coordLogCount = 0;

    /// <summary>
    /// ZDO key on a zone's terrain compiler: the RoadNetworkVersion whose
    /// roads that zone's terrain carries. Ordinary zone loads skip a zone
    /// stamped with the current version, so the road terrain is written once
    /// per network and the player's later terrain edits in the road survive
    /// reloads. Explicit paths (load-time generation, road_regen_island,
    /// road_apply) force the write and re-stamp.
    /// </summary>
    private static readonly int AppliedVersionHash = "ProceduralRoads_AppliedVersion".GetStableHashCode();

    /// <summary>Prefab hash of the game's terrain compiler object (one per zone).</summary>
    private static readonly int TerrainCompilerPrefabHash = "_TerrainCompiler".GetStableHashCode();

    /// <summary>
    /// Zones whose explicit (forced) application was requested while their
    /// saved terrain compiler was not alive yet; written, stamp or no stamp,
    /// when that compiler comes alive. Cleared with the world.
    /// </summary>
    private static readonly HashSet<Vector2i> s_pendingForcedZones = new HashSet<Vector2i>();

    public static void ResetDebugCounters()
    {
        s_coordLogCount = 0;
        s_pendingForcedZones.Clear();
    }

    /// <summary>
    /// A zone finished spawning (Full or Client mode) and has road points.
    /// The game keeps one terrain compiler per zone and creates the saved
    /// one from its ZDO only after the zone is loaded, so a compiler asked
    /// for at this moment would be a second one: the two then destroy each
    /// other on every load. If the zone has a saved compiler not yet alive,
    /// leave the roads to OnTerrainCompilerReady; otherwise write them now,
    /// creating the zone's compiler if it has none.
    /// </summary>
    public static void OnZoneSpawned(Vector2i zoneID, List<RoadSpatialGrid.RoadPoint> roadPoints)
    {
        Vector3 zonePos = ZoneSystem.GetZonePos(zoneID);
        if (TerrainComp.FindTerrainCompiler(zonePos) == null && HasSavedTerrainCompiler(zoneID))
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(
                $"Zone {zoneID}: saved terrain compiler not alive yet, road terrain applied when it is");
            return;
        }
        ApplyRoadTerrainMods(zoneID, roadPoints);
    }

    /// <summary>
    /// A terrain compiler came alive (created fresh, from the save, or under
    /// ghost init): if its zone has road points and it is not stamped with
    /// the current network version, write the roads into it. An unowned
    /// compiler in our area is claimed, as the game itself would shortly;
    /// one owned by another peer is theirs to write.
    /// </summary>
    public static void OnTerrainCompilerReady(TerrainComp terrainComp)
    {
        if (terrainComp == null || terrainComp.m_hmap == null || terrainComp.m_nview == null || !terrainComp.m_nview.IsValid())
            return;

        Vector2i zoneID = ZoneSystem.GetZone(terrainComp.m_hmap.transform.position);
        List<RoadSpatialGrid.RoadPoint> roadPoints = RoadSpatialGrid.GetRoadPointsInZone(zoneID);
        bool forced = s_pendingForcedZones.Remove(zoneID);
        if (roadPoints.Count == 0 || (!forced && CarriesCurrentRoads(terrainComp)))
            return;

        if (!terrainComp.m_nview.IsOwner())
        {
            if (terrainComp.m_nview.HasOwner())
                return;
            terrainComp.m_nview.ClaimOwnership();
        }

        ApplyRoadTerrainModsWithContext(zoneID, roadPoints, terrainComp.m_hmap, terrainComp);
    }

    /// <summary>Whether the zone's saved objects include a terrain compiler.</summary>
    public static bool HasSavedTerrainCompiler(Vector2i zoneID)
    {
        if (ZDOMan.instance == null)
            return false;
        var zdos = new List<ZDO>();
        ZDOMan.instance.FindObjects(zoneID, zdos);
        foreach (ZDO zdo in zdos)
            if (zdo.GetPrefab() == TerrainCompilerPrefabHash)
                return true;
        return false;
    }

    /// <summary>
    /// Apply terrain mods for road points in a zone. Without force, a zone
    /// whose terrain compiler already carries the current network version is
    /// left alone (see AppliedVersionHash).
    /// </summary>
    public static void ApplyRoadTerrainMods(Vector2i zoneID, List<RoadSpatialGrid.RoadPoint> roadPoints, bool force = false)
    {
        TerrainContext? context = GetTerrainContext(zoneID);
        if (context == null)
            return;

        if (!force && CarriesCurrentRoads(context.Value.TerrainComp))
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(
                $"Zone {zoneID}: terrain already carries road network version {RoadSpatialGrid.RoadNetworkVersion}, skipping");
            return;
        }

        ModificationStats stats = ModifyVertexHeights(zoneID, roadPoints, context.Value);
        ApplyRoadPaint(roadPoints, context.Value.TerrainComp, stats.PaintedCells);
        FinalizeTerrainMods(zoneID, roadPoints.Count, stats, context.Value);
    }

    /// <summary>
    /// Whether the zone's terrain compiler is stamped with the current road
    /// network version, i.e. its terrain already carries these roads.
    /// </summary>
    public static bool CarriesCurrentRoads(TerrainComp terrainComp)
    {
        int version = RoadSpatialGrid.RoadNetworkVersion;
        if (version == 0)
            return false;
        ZDO? zdo = terrainComp.m_nview?.GetZDO();
        return zdo != null && zdo.GetInt(AppliedVersionHash, 0) == version;
    }

    /// <summary>
    /// Apply the current network's terrain mods to every loaded zone that has
    /// road points, whether or not the zone already carries them. Zones
    /// generated before the network existed (the zones around the player's
    /// login position on a fresh world, or around a teleport target that landed
    /// mid-generation) get their roads here; zones generated afterwards get
    /// them from the ZoneSystem.SpawnZone hook. Height deltas are computed from
    /// the world generator height, so writing them again does not accumulate;
    /// paint is blended toward the paved colour each time, so the road edges
    /// come out a shade more solid on every explicit reapplication.
    /// </summary>
    public static int ApplyToLoadedZones()
    {
        var heightmaps = Heightmap.GetAllHeightmaps();
        int zonesWithRoads = 0;
        if (heightmaps == null)
            return 0;

        foreach (var heightmap in heightmaps)
        {
            if (heightmap == null) continue;

            Vector2i zoneID = ZoneSystem.GetZone(heightmap.transform.position);
            var roadPoints = RoadSpatialGrid.GetRoadPointsInZone(zoneID);
            if (roadPoints.Count == 0) continue;

            bool existed = TerrainComp.FindTerrainCompiler(heightmap.transform.position) != null;
            if (!existed && HasSavedTerrainCompiler(zoneID))
            {
                // Same as OnZoneSpawned: asking for a compiler now would make a
                // second one; the saved one gets this write when it comes alive.
                s_pendingForcedZones.Add(zoneID);
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(
                    $"Zone {zoneID}: saved terrain compiler not alive yet, road terrain applied when it is");
                zonesWithRoads++;
                continue;
            }
            TerrainComp terrainComp = heightmap.GetAndCreateTerrainCompiler();
            if (terrainComp == null || !terrainComp.m_nview.IsOwner()) continue;

            // A compiler created just now was written by OnTerrainCompilerReady.
            if (!existed && CarriesCurrentRoads(terrainComp))
            {
                zonesWithRoads++;
                continue;
            }

            ApplyRoadTerrainModsWithContext(zoneID, roadPoints, heightmap, terrainComp);
            zonesWithRoads++;
        }
        return zonesWithRoads;
    }

    /// <summary>
    /// Public entry point for applying road terrain mods to a specific zone.
    /// Always writes (the explicit path): used by ApplyToLoadedZones and the
    /// console commands to force-update loaded zones.
    /// </summary>
    public static void ApplyRoadTerrainModsWithContext(Vector2i zoneID, List<RoadSpatialGrid.RoadPoint> roadPoints, 
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
                float pointBlend = RoadProfile.LevelBlend(dist, rp.w);
                if (pointBlend <= 0f)
                    continue;
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
            TargetHeight = influencingPoints > 0 && totalWeight > 0f ? weightedHeightSum / totalWeight : 0f,
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

                    // Paint follows the shared cross-section profile: solid
                    // in the core, fading out strictly inside the leveled
                    // footprint (unpainted verge), instead of the old
                    // pow(x,0.1) near-solid disc to the full road edge.
                    float blendFactor = RoadProfile.PaintStrength(dist * scale, roadPoint.w);
                    if (blendFactor <= 0f)
                        continue;

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
            context.TerrainComp.m_nview?.GetZDO()?.Set(AppliedVersionHash, RoadSpatialGrid.RoadNetworkVersion);
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
}
