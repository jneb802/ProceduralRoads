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
    private static readonly HashSet<Vector2s> s_pendingForcedZones = new HashSet<Vector2s>();

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
    public static void OnZoneSpawned(Vector2s zoneID, List<RoadSpatialGrid.RoadPoint> roadPoints)
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

        Vector2s zoneID = ZoneSystem.GetZone(terrainComp.m_hmap.transform.position);
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
    public static bool HasSavedTerrainCompiler(Vector2s zoneID)
    {
        if (ZDOMan.instance == null)
            return false;
        var zdos = new List<ZDO>();
        // Valheim 1.0 asks callers to carry the set of sectors already
        // visited; this is a single-zone lookup, so it starts empty.
        ZDOMan.instance.FindObjects(zoneID, zdos, new HashSet<ZoneSystem.SectorIndex>());
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
    public static void ApplyRoadTerrainMods(Vector2s zoneID, List<RoadSpatialGrid.RoadPoint> roadPoints, bool force = false)
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
        ApplyRoadPaint(roadPoints, context.Value.TerrainComp, stats.PaintedCells, context.Value);
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

            Vector2s zoneID = ZoneSystem.GetZone(heightmap.transform.position);
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
    public static void ApplyRoadTerrainModsWithContext(Vector2s zoneID, List<RoadSpatialGrid.RoadPoint> roadPoints,
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
        ApplyRoadPaint(roadPoints, context.TerrainComp, stats.PaintedCells, context);
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

    private static TerrainContext? GetTerrainContext(Vector2s zoneID)
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

    private static ModificationStats ModifyVertexHeights(Vector2s zoneID, List<RoadSpatialGrid.RoadPoint> roadPoints, TerrainContext context)
    {
        ModificationStats stats = new ModificationStats { PaintedCells = new HashSet<Vector2i>() };

        LogCoordinateDebug(zoneID, roadPoints, context);

        // The fill spread, per road point, once per zone: it samples the
        // natural ground around the point, which the per-vertex loop cannot afford.
        var world = WorldGenerator.instance;
        float[]? fillExtra = null;
        if (RoadEarthworkNoise.FillSpread > 0f && world != null)
        {
            fillExtra = new float[roadPoints.Count];
            for (int k = 0; k < roadPoints.Count; k++)
                fillExtra[k] = RoadEarthworkNoise.FillExtra(roadPoints[k].p, roadPoints[k].h,
                    (x, z) => BiomeBlendedHeight.GetBlendedHeight(x, z, world));
        }

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
                if (!WithinReach(roadPoints, vertexPos2D))
                    continue;

                float baseHeight = BiomeBlendedHeight.GetBlendedHeight(vertexWorldPos.x, vertexWorldPos.z, WorldGenerator.instance);
                BlendResult blendResult = CalculateBlendedHeight(roadPoints, vertexPos2D, baseHeight, fillExtra);
                if (blendResult.InfluencingPoints == 0)
                    continue;

                float finalHeight = Mathf.Lerp(baseHeight, blendResult.TargetHeight, blendResult.MaxBlend)
                    + RoadEarthworkNoise.FaceBump(vertexPos2D, blendResult.MaxBlend, blendResult.TargetHeight - baseHeight);
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

    /// <summary>How far the batter widens the release per metre of cut past
    /// <see cref="BatterThreshold"/>; fill gets nothing, see
    /// <see cref="BatterDivergence"/>. Settable for tests.</summary>
    internal static float BatterPerMetre = 1f;

    /// <summary>
    /// How deep a cut has to be before the batter starts widening at all;
    /// below this the road keeps the tight fixed margin. A batter applied from
    /// zero ruins ordinary road: a narrow road winding through the mountains
    /// becomes a broad open slope with no character. Its footprint also scales
    /// with the ground's own steepness: on a 2:1 sidehill the release travels
    /// much further before it meets falling ground than on the flat.
    /// </summary>
    internal static float BatterThreshold = 2f;

    /// <summary>
    /// The most the batter may add to the margin, however deep the cut. A
    /// trench wants a slope, not an apron, and an unbounded widening is also
    /// an unbounded influence radius, which the zone gather has to pad for.
    /// </summary>
    internal static float BatterMaxExtra = 4f;

    /// <summary>How much the batter widens the release for a given divergence:
    /// nothing at all until <see cref="BatterThreshold"/>, then
    /// <see cref="BatterPerMetre"/> per metre beyond it, never more than
    /// <see cref="BatterMaxExtra"/>.</summary>
    internal static float BatterExtra(float divergence) =>
        Mathf.Min(BatterMaxExtra, BatterPerMetre * Mathf.Max(0f, divergence - BatterThreshold));

    /// <summary>
    /// The divergence the batter widens for: how far the ground stands ABOVE
    /// the road, so a trench and nothing else, clamped to the most the writer
    /// can actually move the ground (<see cref="RoadConstants.TerrainDeltaMax"/>).
    /// Past that the delta is clamped anyway, so a wider release buys nothing,
    /// and leaving it unbounded makes the influence radius unbounded. Fill gets
    /// nothing: a road built up on fill is not walled in, and widening a fill
    /// on a sidehill fans an apron across the slope.
    /// </summary>
    internal static float BatterDivergence(float roadHeight, float ground) =>
        Mathf.Min(Mathf.Max(0f, ground - roadHeight), RoadConstants.TerrainDeltaMax);

    /// <summary>How far from a road point's centre its levelling can reach:
    /// the half-width, the blend margin, and the widest batter.</summary>
    internal static float MaxInfluenceRadius(float roadWidth) =>
        roadWidth * 0.5f + RoadConstants.TerrainBlendMargin + BatterExtra(RoadConstants.TerrainDeltaMax);

    /// <summary>How far a road point can reach into a zone's terrain, for the
    /// zone gather: <see cref="MaxInfluenceRadius"/> plus the widest the
    /// earthwork noise and fill spread can make the side slope.</summary>
    internal static float GatherRadius(float roadWidth) =>
        MaxInfluenceRadius(roadWidth) + RoadEarthworkNoise.MaxExtraReach;

    /// <summary>Whether any road point could reach the vertex, before the
    /// ground under it is sampled.</summary>
    private static bool WithinReach(List<RoadSpatialGrid.RoadPoint> roadPoints, Vector2 vertexPos)
    {
        foreach (RoadSpatialGrid.RoadPoint rp in roadPoints)
        {
            float reach = GatherRadius(rp.w);
            if ((rp.p - vertexPos).sqrMagnitude < reach * reach)
                return true;
        }
        return false;
    }

    private static BlendResult CalculateBlendedHeight(List<RoadSpatialGrid.RoadPoint> roadPoints, Vector2 vertexPos,
        float groundHere, float[]? fillExtra = null)
    {
        float weightedHeightSum = 0f;
        float totalWeight = 0f;
        float maxBlend = 0f;
        int influencingPoints = 0;

        for (int k = 0; k < roadPoints.Count; k++)
        {
            RoadSpatialGrid.RoadPoint rp = roadPoints[k];
            float distSq = (rp.p - vertexPos).sqrMagnitude;
            // A road point standing well below the ground gets a wider
            // release, so the cut is walked out on a slope instead of ending
            // in a wall. The ground under the vertex stands in for the ground
            // under the point: they are metres apart, and this costs no query.
            float margin = RoadConstants.TerrainBlendMargin * RoadEarthworkNoise.MarginFactor(rp.p)
                         + BatterExtra(BatterDivergence(rp.h, groundHere))
                         + (fillExtra != null ? fillExtra[k] : 0f);
            float influenceRadius = (rp.w * 0.5f) + margin;
            float influenceRadiusSq = influenceRadius * influenceRadius;

            if (distSq < influenceRadiusSq)
            {
                float dist = Mathf.Sqrt(distSq);
                float pointBlend = RoadProfile.LevelBlend(dist, rp.w, margin);
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

    /// <summary>
    /// No paint on ground more than this many metres below the road surface.
    /// Paint reaches a set distance across the road however the ground falls
    /// away, so on a raised causeway it ran down the bank (measured: paint
    /// 2.3 m from the centre of a 4 m road on 3.8 m of fill, where the ground
    /// was 1.5 m below the road). Settable for tests; 0 turns it off.
    /// </summary>
    internal static float PaintBelow = 0.5f;

    /// <summary>
    /// Measure paint distance from the road point itself, in metres, and
    /// centre the paint on the vertex the way vanilla reads a paint cell
    /// (Heightmap.WorldToVertexMask). Snapping the point to a vertex first
    /// was off by up to ~0.7 m, and a half-cell shift put the painted band
    /// 0.6-0.8 m toward -x/-z of the levelled one (measured at two headings).
    /// Settable for tests.
    /// </summary>
    internal static bool PaintExact = true;

    /// <summary>Distance used for a painted vertex: exact metres from the road
    /// point, or the snapped vertex offset in metres.</summary>
    internal static float PaintDistance(Vector2 vertex, Vector2 point, int dx, int dy, float scale, bool exact) =>
        exact ? Vector2.Distance(vertex, point) : Mathf.Sqrt(dx * dx + dy * dy) * scale;

    /// <summary>The ground height under a paint cell once levelled. With exact
    /// paint a cell is centred on its own vertex, as vanilla reads it;
    /// otherwise it is taken half a vertex off, the mean of four.</summary>
    private static float LevelledCellHeight(TerrainContext context, int vx, int vy)
    {
        float sum = 0f; int n = 0;
        int reach = PaintExact ? 0 : 1;
        var world = WorldGenerator.instance;
        for (int z = vy; z <= vy + reach; z++)
            for (int x = vx; x <= vx + reach; x++)
            {
                if (x >= context.GridSize || z >= context.GridSize) continue;
                int i = z * context.GridSize + x;
                float wx = context.HeightmapPosition.x + (x - context.TerrainComp.m_width / 2f) * context.VertexSpacing;
                float wz = context.HeightmapPosition.z + (z - context.TerrainComp.m_width / 2f) * context.VertexSpacing;
                sum += BiomeBlendedHeight.GetBlendedHeight(wx, wz, world)
                     + context.TerrainComp.m_levelDelta[i] + context.TerrainComp.m_smoothDelta[i];
                n++;
            }
        return sum / n;
    }

    private static void ApplyRoadPaint(List<RoadSpatialGrid.RoadPoint> roadPoints, TerrainComp terrainComp, HashSet<Vector2i> paintedCells,
        TerrainContext? context = null)
    {
        Heightmap hmap = terrainComp.m_hmap;
        if (hmap == null)
            return;
            
        int gridSize = terrainComp.m_width + 1;
        Vector3 terrainPos = hmap.transform.position;
        float scale = hmap.m_scale;
        
        foreach (RoadSpatialGrid.RoadPoint roadPoint in roadPoints)
        {
            // Points closer together than PaintDedupeInterval paint the same
            // texels and are skipped, but only while a point's paint reaches
            // the neighbouring texels; a 2 m road paints 0.85 m out on 1 m
            // texels, so each point paints its own texel only and skipping any
            // of them leaves the texels between bare (a gap every 1.5 m, on
            // master too). Then every point paints.
            float paintReach = roadPoint.w * 0.5f * RoadConstants.RoadPaintOuterRatio;
            if (paintReach >= scale)
            {
                float dedupeInterval = Mathf.Min(RoadConstants.PaintDedupeInterval, paintReach);
                Vector2i cell = new Vector2i(
                    Mathf.RoundToInt(roadPoint.p.x / dedupeInterval),
                    Mathf.RoundToInt(roadPoint.p.y / dedupeInterval));
                if (paintedCells.Contains(cell))
                    continue;
                paintedCells.Add(cell);
            }
            
            float cellShift = PaintExact ? 0f : 0.5f;
            Vector3 worldPos = new Vector3(roadPoint.p.x - cellShift, 0f, roadPoint.p.y - cellShift);
            Vector3 localPos = worldPos - terrainPos;
            int halfWidth = (terrainComp.m_width + 1) / 2;
            int centerX = Mathf.FloorToInt(localPos.x / scale + 0.5f) + halfWidth;
            int centerY = Mathf.FloorToInt(localPos.z / scale + 0.5f) + halfWidth;
            // Paint is held against the levelled surface under the road point,
            // not the point's stored height: where two roads meet the
            // levelling blends them, and the higher road's surface sits below
            // its own stored height, which would break its paint at the join.
            float deck = PaintBelow > 0f && context.HasValue
                && centerX >= 0 && centerY >= 0 && centerX < gridSize && centerY < gridSize
                ? LevelledCellHeight(context.Value, centerX, centerY) : roadPoint.h;
            
            float radius = roadPoint.w * 0.5f;
            float radiusInVertices = radius / scale;
            // With exact distances the disc can reach one vertex further on
            // the side the snap moved away from.
            int radiusCeil = Mathf.CeilToInt(radiusInVertices) + (PaintExact ? 1 : 0);
            
            for (int dy = -radiusCeil; dy <= radiusCeil; dy++)
            {
                for (int dx = -radiusCeil; dx <= radiusCeil; dx++)
                {
                    int vx = centerX + dx;
                    int vy = centerY + dy;
                    
                    if (vx < 0 || vy < 0 || vx >= gridSize || vy >= gridSize)
                        continue;
                    
                    Vector2 paintPosition = new Vector2(
                        terrainPos.x + (vx - halfWidth + cellShift) * scale,
                        terrainPos.z + (vy - halfWidth + cellShift) * scale);
                    float distMetres = PaintDistance(paintPosition, roadPoint.p, dx, dy, scale, PaintExact);
                    if (distMetres > radius)
                        continue;

                    // Paint follows the shared cross-section profile: solid
                    // in the core, fading out strictly inside the leveled
                    // footprint (unpainted verge), instead of the old
                    // pow(x,0.1) near-solid disc to the full road edge.
                    float blendFactor = RoadProfile.PaintStrength(distMetres, roadPoint.w);
                    if (blendFactor <= 0f)
                        continue;
                    if (PaintBelow > 0f && context.HasValue && deck - LevelledCellHeight(context.Value, vx, vy) > PaintBelow)
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

    private static void FinalizeTerrainMods(Vector2s zoneID, int roadPointCount, ModificationStats stats, TerrainContext context)
    {
        int paintOps = stats.PaintedCells.Count;
        
        if (stats.VerticesModified > 0 || paintOps > 0)
        {
            context.TerrainComp.m_nview?.GetZDO()?.Set(AppliedVersionHash, RoadSpatialGrid.RoadNetworkVersion);
            context.TerrainComp.Save();
            // Valheim 1.0 turned Poke's bool into a selector for WHICH late
            // pass rebuilds the mesh, not a count: LateUpdate acts on 1,
            // CustomLateUpdate on 2, and any other positive value is never
            // consumed, so the heightmap would stay dirty. 1 is what the
            // game's own TerrainComp modification path now passes after
            // editing terrain, which is exactly what this is. paintOnly stays
            // false because this changed heights as well as paint.
            context.Heightmap.Poke(1);
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(
                $"Zone {zoneID}: {stats.VerticesModified}/{stats.VerticesChecked} vertices modified, {paintOps} paint cells");
        }
        else if (roadPointCount > 0)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning(
                $"Zone {zoneID}: {roadPointCount} road points but 0 vertices matched! Coordinate mismatch?");
        }
    }

    private static void LogCoordinateDebug(Vector2s zoneID, List<RoadSpatialGrid.RoadPoint> roadPoints, TerrainContext context)
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
