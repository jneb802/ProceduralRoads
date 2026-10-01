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

    // Borrow the height array only during ApplyToHeightmap; never keep a second
    // terrain cache. Requests live until the compiler rebuilds or is destroyed.
    private sealed class PendingWrite
    {
        public Vector2s Zone;
        public List<RoadSpatialGrid.RoadPoint> Points = null!;
        public int Version;
        public bool Force;
    }

    private static readonly Dictionary<TerrainComp, PendingWrite> s_pendingWrites = new();
    internal static int PendingWriteCount => s_pendingWrites.Count;

    public static void OnTerrainCompilerDestroyed(TerrainComp terrainComp) => s_pendingWrites.Remove(terrainComp);

    /// <summary>
    /// Whether a loaded zone's road terrain is in place: its terrain compiler
    /// is alive (the roads are queued or written when it comes alive) and no
    /// write is still pending for it. Before that the ground under a road is
    /// the ungraded ground, so anything judged against it is judged wrongly.
    /// </summary>
    internal static bool TerrainSettled(Vector3 zonePos)
    {
        TerrainComp? terrainComp = TerrainComp.FindTerrainCompiler(zonePos);
        return terrainComp != null && !s_pendingWrites.ContainsKey(terrainComp);
    }

    public static void ResetDebugCounters()
    {
        s_coordLogCount = 0;
        s_pendingForcedZones.Clear();
        s_pendingWrites.Clear();
        s_swept.Clear();
    }

    /// <summary>
    /// Safety net for zones the spawn path never wrote: a zone can have a
    /// location placed in the same spawn and then no road terrain at all while
    /// its neighbours are written, which leaves a cliff at the zone edge.
    /// Called every few seconds: a loaded zone with road points whose compiler
    /// does not carry the current network, and has no write pending, gets one
    /// queued. A compiler the game released is claimed, as
    /// OnTerrainCompilerReady does; another peer's is left for that peer. Each
    /// zone is swept at most once per network version, so a zone whose write
    /// changes nothing (and is never stamped) does not loop.
    /// </summary>
    private static readonly HashSet<(Vector2s zone, int version)> s_swept = new();

    public static int SweepUnstamped()
    {
        int version = RoadSpatialGrid.RoadNetworkVersion;
        if (version == 0) return 0;
        var heightmaps = Heightmap.GetAllHeightmaps();
        if (heightmaps == null) return 0;
        int queued = 0;
        foreach (var heightmap in heightmaps)
        {
            if (heightmap == null) continue;
            Vector2s zoneID = ZoneSystem.GetZone(heightmap.transform.position);
            if (s_swept.Contains((zoneID, version))) continue;
            var roadPoints = RoadSpatialGrid.GetRoadPointsInZone(zoneID);
            if (roadPoints.Count == 0) { s_swept.Add((zoneID, version)); continue; }
            TerrainComp? terrainComp = TerrainComp.FindTerrainCompiler(heightmap.transform.position);
            if (terrainComp == null)
            {
                if (HasSavedTerrainCompiler(zoneID)) continue;   // its Awake writes it; look again next sweep
                terrainComp = heightmap.GetAndCreateTerrainCompiler();
                if (terrainComp == null) continue;
            }
            if (terrainComp.m_nview == null || !terrainComp.m_nview.IsValid()) continue;
            if (!terrainComp.m_nview.IsOwner())
            {
                if (terrainComp.m_nview.HasOwner()) continue;   // another peer's; look again next sweep
                terrainComp.m_nview.ClaimOwnership();           // released by the game: ours to write
            }
            s_swept.Add((zoneID, version));
            if (CarriesCurrentRoads(terrainComp) || s_pendingWrites.ContainsKey(terrainComp)) continue;
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo($"Zone {zoneID}: road terrain missing after spawn, queued by the sweep");
            QueueWrite(zoneID, roadPoints, heightmap, terrainComp, force: false);
            queued++;
        }
        return queued;
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

        QueueWrite(zoneID, roadPoints, terrainComp.m_hmap, terrainComp, forced);
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

        QueueWrite(zoneID, roadPoints, context.Value.Heightmap, context.Value.TerrainComp, force);
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
        if (zdo == null) return false;
        int applied = zdo.GetInt(AppliedVersionHash, 0);
        if (applied == version) return true;
        var zone = ZoneSystem.GetZone(terrainComp.m_hmap.transform.position);
        return applied != 0 && RoadSpatialGrid.PendingPoints(RoadSpatialGrid.GetRoadPointsInZone(zone), applied).Count == 0;
    }

    /// <summary>
    /// Queue the current network's terrain mods for every loaded zone that has
    /// road points, whether or not the zone already carries them. Zones
    /// generated before the network existed (the zones around the player's
    /// login position on a fresh world, or around a teleport target that landed
    /// mid-generation) get their roads here; zones generated afterwards get
    /// them from the ZoneSystem.SpawnZone hook. Height deltas are computed from
    /// the pre-compiler location-shaped height, so writing them again does not accumulate;
    /// paint is blended toward the paved colour each time, so the road edges
    /// come out a shade more solid on every explicit reapplication.
    /// </summary>
    public static void ApplyAdditionsToLoadedZones()
    {
        foreach (var heightmap in Heightmap.GetAllHeightmaps())
        {
            if (heightmap == null) continue;
            var zone = ZoneSystem.GetZone(heightmap.transform.position);
            var points = RoadSpatialGrid.GetRoadPointsInZone(zone);
            if (points.Count > 0)
            {
                var compiler = TerrainComp.FindTerrainCompiler(heightmap.transform.position);
                if (compiler != null) OnTerrainCompilerReady(compiler);
                else OnZoneSpawned(zone, points);
            }
        }
    }

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

            // OnTerrainCompilerReady can already have handled a newly created
            // compiler (a queued request otherwise coalesces below).
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
    /// Explicit reapplication: queues a write on the next terrain rebuild,
    /// even for an already-stamped zone.
    /// </summary>
    public static void ApplyRoadTerrainModsWithContext(Vector2s zoneID, List<RoadSpatialGrid.RoadPoint> roadPoints,
        Heightmap heightmap, TerrainComp terrainComp)
    {
        QueueWrite(zoneID, roadPoints, heightmap, terrainComp, force: true);
    }

    private static void QueueWrite(Vector2s zoneID, List<RoadSpatialGrid.RoadPoint> roadPoints,
        Heightmap heightmap, TerrainComp terrainComp, bool force)
    {
        if (roadPoints == null || roadPoints.Count == 0 || heightmap == null || terrainComp == null ||
            terrainComp.m_nview == null || !terrainComp.m_nview.IsValid() || !terrainComp.m_nview.IsOwner())
            return;

        // Multiple requests before the late pass coalesce, preserving an explicit
        // reapplication even if a normal zone-load request follows it.
        if (s_pendingWrites.TryGetValue(terrainComp, out var previous) &&
            previous.Version == RoadSpatialGrid.RoadNetworkVersion)
            force |= previous.Force;
        s_pendingWrites[terrainComp] = new PendingWrite
        {
            Zone = zoneID, Points = roadPoints, Version = RoadSpatialGrid.RoadNetworkVersion, Force = force
        };
        // Rebuild after this spawn finishes placing its location modifiers. An
        // immediate rebuild from TerrainComp.Awake can be too early in a spawn.
        heightmap.Poke(1);
    }

    /// <summary>
    /// Prefix of vanilla TerrainComp.ApplyToHeightmap. These heights already
    /// include location modifiers but not this compiler's deltas. Computing
    /// against WorldGenerator here would apply the location's shaping twice.
    /// </summary>
    public static void ApplyPendingTerrain(TerrainComp terrainComp, Heightmap heightmap, List<float> heights)
    {
        if (!s_pendingWrites.TryGetValue(terrainComp, out var request))
            return;
        s_pendingWrites.Remove(terrainComp);
        // Ownership can change between queueing and rebuilding: the game
        // releases objects outside the player's active area, and a zone at the
        // ring's edge (or just after a teleport) can be released before its
        // heightmap rebuilds. OnTerrainCompilerReady fires once, so a write
        // dropped here as "not owner" was never asked for again and the road
        // ended in a cliff at the zone edge. A released (unowned) compiler is
        // claimed, as OnTerrainCompilerReady does. Another peer's is still
        // left alone and the request dropped: that peer writes its own zones,
        // and if the compiler comes back to us unwritten the sweep queues it.
        if (request.Version == RoadSpatialGrid.RoadNetworkVersion && terrainComp.m_hmap == heightmap &&
            terrainComp.m_nview != null && terrainComp.m_nview.IsValid() &&
            !terrainComp.m_nview.IsOwner() && !terrainComp.m_nview.HasOwner())
            terrainComp.m_nview.ClaimOwnership();
        // The network can change between requesting and rebuilding.
        if (request.Version != RoadSpatialGrid.RoadNetworkVersion ||
            terrainComp.m_hmap != heightmap || terrainComp.m_nview == null ||
            !terrainComp.m_nview.IsValid() || !terrainComp.m_nview.IsOwner() ||
            (!request.Force && CarriesCurrentRoads(terrainComp)))
            return;

        int gridSize = terrainComp.m_width + 1;
        int count = gridSize * gridSize;
        if (heights == null || heights.Count != count || terrainComp.m_levelDelta == null ||
            terrainComp.m_levelDelta.Length != count)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning(
                $"Zone {request.Zone}: road terrain not written: height arrays do not match the compiler grid");
            return;
        }
        for (int i = 0; i < count; i++)
            if (float.IsNaN(heights[i]) || float.IsInfinity(heights[i]))
            {
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning(
                    $"Zone {request.Zone}: road terrain not written: non-finite baseline height");
                return;
            }

        var context = new TerrainContext
        {
            Heightmap = heightmap, TerrainComp = terrainComp,
            HeightmapPosition = heightmap.transform.position,
            GridSize = gridSize, VertexSpacing = RoadConstants.ZoneSize / terrainComp.m_width,
            PreCompilerHeights = heights
        };
        // A compiler carrying an ancestor needs only the new road footprint.
        // Old roads in this same zone may contain player edits: never replay them.
        var pending = request.Force ? request.Points : RoadSpatialGrid.PendingPoints(
            request.Points, terrainComp.m_nview.GetZDO().GetInt(AppliedVersionHash, 0));
        ModificationStats stats = ModifyVertexHeights(request.Zone, pending, context);
        ApplyRoadPaint(pending, terrainComp, stats.PaintedCells, context);
        FinalizeTerrainMods(request.Zone, request.Points.Count, stats, context);
        // Vanilla now applies our deltas to this very array and rebuilds its
        // collider. Do not Poke again from here: that would queue another rebuild.
    }

    private struct TerrainContext
    {
        public Heightmap Heightmap;
        public TerrainComp TerrainComp;
        public Vector3 HeightmapPosition;
        public int GridSize;
        public float VertexSpacing;
        public IReadOnlyList<float> PreCompilerHeights;
    }

    private static TerrainContext? GetTerrainContext(Vector2s zoneID)
    {
        Heightmap? heightmap = Heightmap.FindHeightmap(ZoneSystem.GetZonePos(zoneID));
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
                if (!roadPoints[k].paintOnly)
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
                if (RoadSiteProtection.Contains(vertexPos2D)) continue;
                
                int index = vz * context.GridSize + vx;
                float baseHeight = context.PreCompilerHeights[index] + context.HeightmapPosition.y;
                BlendResult blendResult = CalculateBlendedHeight(roadPoints, vertexPos2D, baseHeight, fillExtra);
                if (blendResult.InfluencingPoints == 0)
                    continue;

                float finalHeight = Mathf.Lerp(baseHeight, blendResult.TargetHeight, blendResult.MaxBlend)
                    + RoadEarthworkNoise.FaceBump(vertexPos2D, blendResult.MaxBlend, blendResult.TargetHeight - baseHeight);
                float delta = Mathf.Clamp(finalHeight - baseHeight, RoadConstants.TerrainDeltaMin, RoadConstants.TerrainDeltaMax);

                if (Mathf.Abs(delta) > RoadConstants.MinHeightDeltaThreshold || blendResult.MaxBlend > RoadConstants.MinBlendForModification)
                {
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

    /// <summary>
    /// The road surface height at a vertex, from the road points within reach.
    /// The points sit on the centreline, so the surface is fitted as a LINE
    /// along the road (weighted least squares, the same blend weights as
    /// before) and read at the vertex. A weighted mean would do mid-road,
    /// where the points lie on both sides of the vertex, but at a road end
    /// every point lies on one side: on a slope the mean of their heights is
    /// the height some way back along the road, and the terrain at the end,
    /// and for the blend margin beyond it, came out on a shelf. The fit
    /// follows the road's own gradient through the end instead.
    ///
    /// The sums gathered below are the ones a plane fit would need, but they
    /// are used to find the road's direction, not to fit a plane: the height
    /// is regressed along that direction alone and the gradient across the
    /// road is zero by construction. See FitHeightAtVertex, which explains why
    /// a free plane fit was tried and abandoned.
    /// </summary>
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

    private static BlendResult CalculateBlendedHeight(List<RoadSpatialGrid.RoadPoint> roadPoints, Vector2 vertexPos,
        float groundHere, float[]? fillExtra = null)
    {
        // Weighted sums for the fit h = a + b*dx + c*dy, (dx, dy) relative to
        // the vertex, so a is the surface height at the vertex.
        double sw = 0, sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0, sh = 0, sxh = 0, syh = 0;
        float maxBlend = 0f;
        int influencingPoints = 0;

        for (int k = 0; k < roadPoints.Count; k++)
        {
            RoadSpatialGrid.RoadPoint rp = roadPoints[k];
            // A paint-only point (a waded ford) leaves the ground as it is.
            if (rp.paintOnly)
                continue;
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
                double weight = pointBlend * pointBlend;
                double dx = rp.p.x - vertexPos.x;
                double dy = rp.p.y - vertexPos.y;
                double h = rp.h;

                sw += weight;
                sx += weight * dx;
                sy += weight * dy;
                sxx += weight * dx * dx;
                sxy += weight * dx * dy;
                syy += weight * dy * dy;
                sh += weight * h;
                sxh += weight * dx * h;
                syh += weight * dy * h;
                influencingPoints++;

                if (pointBlend > maxBlend)
                    maxBlend = pointBlend;
            }
        }

        return new BlendResult
        {
            TargetHeight = influencingPoints > 0 && sw > 0 ? FitHeightAtVertex(sw, sx, sy, sxx, sxy, syy, sh, sxh, syh) : 0f,
            MaxBlend = maxBlend,
            InfluencingPoints = influencingPoints
        };
    }

    /// <summary>
    /// The fitted road surface height at the vertex.
    ///
    /// This is a LINE fit along the road, not a plane fit. The points are the
    /// road's centreline, so the surface is one-dimensional: the second-moment
    /// sums (sxx, sxy, syy) are here only to find the points' principal
    /// direction, which is the road direction; the height is then regressed on
    /// the offset along that direction alone, and the gradient ACROSS the road
    /// is zero by construction, which keeps the cross-section level.
    ///
    /// A free plane fit was tried and abandoned: on a bend the lateral offset
    /// of the centreline grows with distance in the same way the ramp's lift
    /// does, and the plane attributed the one to the other, tilting the road
    /// sideways by as much as the clamp allows. Nothing below fits a plane.
    ///
    /// The ridge (a prior spread of HeightFitRidgeMetres) keeps the gradient
    /// defined for a single point or two nearly coincident ones, and the clamp
    /// bounds it.
    /// </summary>
    private static float FitHeightAtVertex(double sw, double sx, double sy, double sxx, double sxy, double syy,
        double sh, double sxh, double syh)
    {
        double mx = sx / sw, my = sy / sw, mh = sh / sw;
        double cxx = sxx - sw * mx * mx;
        double cxy = sxy - sw * mx * my;
        double cyy = syy - sw * my * my;
        double cxh = sxh - sw * mx * mh;
        double cyh = syh - sw * my * mh;

        // Principal direction of the 2x2 weighted covariance (largest eigenvalue).
        double half = 0.5 * (cxx + cyy);
        double diff = 0.5 * (cxx - cyy);
        double root = System.Math.Sqrt(diff * diff + cxy * cxy);
        double ux, uy;
        if (root < 1e-12)
        {
            ux = 1; uy = 0; // isotropic or a single point: any direction
        }
        else
        {
            // Eigenvector of the largest eigenvalue half + root.
            ux = cxy;
            uy = (half + root) - cxx;
            if (System.Math.Abs(ux) + System.Math.Abs(uy) < 1e-12) { ux = 1; uy = 0; }
            double len = System.Math.Sqrt(ux * ux + uy * uy);
            ux /= len; uy /= len;
        }

        // Regress height on the offset s along that direction (centred sums project linearly).
        double css = ux * ux * cxx + 2 * ux * uy * cxy + uy * uy * cyy;
        double csh = ux * cxh + uy * cyh;
        double ridge = sw * RoadConstants.HeightFitRidgeMetres * RoadConstants.HeightFitRidgeMetres;
        double b = csh / (css + ridge);
        double limit = RoadConstants.HeightFitMaxGradient;
        if (b > limit) b = limit; else if (b < -limit) b = -limit;

        double ms = ux * mx + uy * my; // mean offset along the road, from the vertex
        return (float)(mh - b * ms);
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
        for (int z = vy; z <= vy + reach; z++)
            for (int x = vx; x <= vx + reach; x++)
            {
                if (x >= context.GridSize || z >= context.GridSize) continue;
                int i = z * context.GridSize + x;
                sum += context.PreCompilerHeights[i] + context.HeightmapPosition.y
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
                    if (RoadSiteProtection.Contains(paintPosition)) continue;

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
                    Color newColor = Color.Lerp(currentColor, RoadSurface.MaskAt(paintPosition.x, paintPosition.y), blendFactor);
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
