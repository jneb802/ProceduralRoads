using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Handles terrain height and paint modifications for roads.
/// </summary>
public static class RoadTerrainModifier
{
    /// <summary>
    /// ZDO key on a zone's terrain compiler: the RoadNetworkVersion whose
    /// roads that zone's terrain carries. Ordinary zone loads skip a zone
    /// stamped with the current version, so the road terrain is written once
    /// per network and the player's later terrain edits in the road survive
    /// reloads. Explicit paths (load-time generation, road_regen_island,
    /// road_apply) force the write and re-stamp.
    /// </summary>
    internal static readonly int AppliedVersionHash = "ProceduralRoads_AppliedVersion".GetStableHashCode();

    /// <summary>
    /// The version of this terrain writer: how it turns road points into heights and paint. Raised whenever a change to
    /// the writer would write a zone differently, so a later build can tell the zones an older writer wrote
    /// (<see cref="WriterVersionHash"/>).
    /// <para>1: the first release's writer.</para>
    /// </summary>
    public const int WriterVersion = 1;

    /// <summary>ZDO key on a zone's terrain compiler: the <see cref="WriterVersion"/> that wrote it, stamped with
    /// <see cref="AppliedVersionHash"/> (<see cref="Stamp"/>); 0 on a compiler no writer of this build has stamped, and
    /// on every zone of a network a released build saved, which keeps the keys that build wrote.</summary>
    internal static readonly int WriterVersionHash = "ProceduralRoads_WriterVersion".GetStableHashCode();

    /// <summary>A zone's compiler carries the current network's roads as this writer wrote them.</summary>
    private static void Stamp(TerrainComp terrainComp)
    {
        ZDO zdo = terrainComp.m_nview.GetZDO();
        zdo.Set(AppliedVersionHash, RoadSpatialGrid.RoadNetworkVersion);
        if (!RoadSpatialGrid.LoadedFromReleasedFormat) zdo.Set(WriterVersionHash, WriterVersion);
    }

    /// <summary>What a terrain write did.</summary>
    public enum WriteOutcome
    {
        None,
        /// <summary>Saved into the compiler and stamped.</summary>
        Written,
        /// <summary>The roads change nothing in this zone (its points only brush its edge).</summary>
        NothingToWrite,
        /// <summary>The game would not save: this peer does not own the compiler.</summary>
        SaveRefused,
    }

    /// <summary>
    /// The outcome of the last write. ServerTerrainBake reads it straight after
    /// completing a compiler's queued terrain rebuild.
    /// </summary>
    public static WriteOutcome LastWriteOutcome = WriteOutcome.None;

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

    /// <summary>Forget every pending write and sweep record: a world unloads, or a test starts afresh.</summary>
    public static void ResetForWorld()
    {
        s_pendingForcedZones.Clear();
        s_pendingWrites.Clear();
        s_swept.Clear();
        s_sweepWrites.Clear();
    }

    /// <summary>
    /// Safety net for zones the spawn path never wrote: a zone can have a
    /// location placed in the same spawn and then no road terrain at all while
    /// its neighbours are written, which leaves a cliff at the zone edge.
    /// Called every few seconds: a loaded zone with road points whose compiler
    /// does not carry the current network, and has no write pending, gets one
    /// queued. A compiler the game released is claimed, as
    /// OnTerrainCompilerReady does; another peer's is left for that peer. A zone is done for a network version once it
    /// carries it, or after <see cref="SweepWrites"/> writes that left it unstamped (a write that changes nothing is
    /// never stamped), so a write dropped on its way (its owner changed at the rebuild) is queued again, and none loops.
    /// </summary>
    private static readonly HashSet<(Vector2s zone, int version)> s_swept = new();
    private static readonly Dictionary<(Vector2s zone, int version), int> s_sweepWrites = new();
    private const int SweepWrites = 3;

    public static int SweepUnstamped()
    {
        if (RoadLifecycleManager.WorkersOwnWorld) return 0;
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
            // Explored before this network: no compiler made or claimed, nothing written.
            if (roadPoints.Count == 0 || NetworkCutoff.Excludes(zoneID)) { s_swept.Add((zoneID, version)); continue; }
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
            if (CarriesCurrentRoads(terrainComp)) { s_swept.Add((zoneID, version)); continue; }
            if (s_pendingWrites.ContainsKey(terrainComp)) continue;
            int writes = s_sweepWrites.TryGetValue((zoneID, version), out int w) ? w + 1 : 1;
            s_sweepWrites[(zoneID, version)] = writes;
            if (writes >= SweepWrites) s_swept.Add((zoneID, version));
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
        if (RoadLifecycleManager.WorkersOwnWorld) return;
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
        // Forced only by an operator's command (ApplyToLoadedZones); otherwise a
        // zone explored before this network is left exactly as it is.
        bool forced = s_pendingForcedZones.Remove(zoneID);
        if (roadPoints.Count == 0 || (!forced && (NetworkCutoff.Excludes(zoneID) || CarriesCurrentRoads(terrainComp))))
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
    public static bool HasSavedTerrainCompiler(Vector2s zoneID) => SavedTerrainCompilers(zoneID).Count > 0;

    /// <summary>The terrain compilers saved in a zone, as ZDOs (one, normally; two is a zone the game would fight over).</summary>
    internal static List<ZDO> SavedTerrainCompilers(Vector2s zoneID)
    {
        var zdos = new List<ZDO>();
        if (ZDOMan.instance == null) return zdos;
        // Valheim 1.0 asks callers to carry the set of sectors already visited; this is a single-zone lookup.
        ZDOMan.instance.FindObjects(zoneID, zdos, new HashSet<ZoneSystem.SectorIndex>());
        zdos.RemoveAll(zdo => zdo.GetPrefab() != TerrainCompilerPrefabHash);
        return zdos;
    }

    /// <summary>
    /// Apply terrain mods for road points in a zone. A zone whose terrain
    /// compiler already carries the current network version is left alone (see
    /// AppliedVersionHash); ApplyRoadTerrainModsWithContext forces the write.
    /// </summary>
    public static void ApplyRoadTerrainMods(Vector2s zoneID, List<RoadSpatialGrid.RoadPoint> roadPoints)
    {
        // Before the compiler is asked for: a zone explored before this
        // network gets no compiler and no write (NetworkCutoff).
        if (RoadLifecycleManager.WorkersOwnWorld || NetworkCutoff.Excludes(zoneID)) return;
        TerrainContext? context = GetTerrainContext(zoneID);
        if (context == null)
            return;

        if (CarriesCurrentRoads(context.Value.TerrainComp))
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(
                $"Zone {zoneID}: terrain already carries road network version {RoadSpatialGrid.RoadNetworkVersion}, skipping");
            return;
        }

        QueueWrite(zoneID, roadPoints, context.Value.Heightmap, context.Value.TerrainComp, force: false);
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
        return CarriesCurrentRoads(ZoneSystem.GetZone(terrainComp.m_hmap.transform.position),
            zdo.GetInt(AppliedVersionHash, 0));
    }

    // Saved compilers and live ones must agree: an append elsewhere does not
    // make this zone stale, and does not justify taking its owner or rebuilding it.
    internal static bool CarriesCurrentRoads(Vector2s zone, int applied)
    {
        int version = RoadSpatialGrid.RoadNetworkVersion;
        return version != 0 && applied != 0 && applied == version;
    }

    /// <summary>
    /// Queue the current network's terrain for every loaded zone that has road points, whether or not the zone already
    /// carries it: zones generated before the network existed (around the player's login position on a fresh world) get
    /// their roads here; zones generated later get them as they spawn. Height deltas are computed from the location-shaped
    /// height, so writing them again does not accumulate; paint is blended toward the paved colour each time.
    /// </summary>
    /// <param name="operatorRequest">An operator's command (road_generate, road_regen_island): the
    /// zones loaded around them that hold road are written even if they were explored before this
    /// network, and become the network's (NetworkCutoff.Admit), like the zones of a manual road.
    /// Otherwise (a network just generated) those zones are left alone (NetworkCutoff).</param>
    public static int ApplyToLoadedZones(bool operatorRequest = false) => ApplyToLoadedZones(operatorRequest, out _);

    /// <param name="admitted">The zones explored before the network that the command now builds in full.</param>
    public static int ApplyToLoadedZones(bool operatorRequest, out List<Vector2s> admitted)
    {
        admitted = new List<Vector2s>();
        if (RoadLifecycleManager.WorkersOwnWorld) return 0;
        var heightmaps = Heightmap.GetAllHeightmaps();
        int zonesWithRoads = 0;
        if (heightmaps == null)
            return 0;

        if (operatorRequest)
        {
            var written = new HashSet<Vector2s>();
            foreach (var heightmap in heightmaps)
                if (heightmap != null)
                {
                    Vector2s zone = ZoneSystem.GetZone(heightmap.transform.position);
                    if (RoadSpatialGrid.GetRoadPointsInZone(zone).Count > 0) written.Add(zone);
                }
            admitted = NetworkCutoff.Admit(written);
            if (admitted.Count > 0)
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning(
                    NetworkCutoff.AdmissionWarning("this command builds roads in", admitted));
        }

        foreach (var heightmap in heightmaps)
        {
            if (heightmap == null) continue;

            Vector2s zoneID = ZoneSystem.GetZone(heightmap.transform.position);
            var roadPoints = RoadSpatialGrid.GetRoadPointsInZone(zoneID);
            if (roadPoints.Count == 0) continue;
            if (NetworkCutoff.Excludes(zoneID)) continue;

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
        if (RoadLifecycleManager.WorkersOwnWorld) return;
        QueueWrite(zoneID, roadPoints, heightmap, terrainComp, force: true);
    }

    private static void QueueWrite(Vector2s zoneID, List<RoadSpatialGrid.RoadPoint> roadPoints,
        Heightmap heightmap, TerrainComp terrainComp, bool force)
    {
        if (roadPoints == null || roadPoints.Count == 0 || heightmap == null || terrainComp == null ||
            terrainComp.m_nview == null || !terrainComp.m_nview.IsValid() || !terrainComp.m_nview.IsOwner())
            return;
        // Only an operator's forced write reaches a zone explored before this network.
        if (!force && NetworkCutoff.Excludes(zoneID))
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
            (!request.Force && (NetworkCutoff.Excludes(request.Zone) || CarriesCurrentRoads(terrainComp))))
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
        ModificationStats stats = ModifyVertexHeights(request.Zone, request.Points, context);
        stats.PaintedTexels = ApplyRoadPaint(request.Points, terrainComp, stats.PaintedCells, context);
        FinalizeTerrainMods(request.Zone, request.Points, stats, context);
        // Vanilla now applies our deltas to this very array and rebuilds its
        // collider. Do not Poke again from here: that would queue another rebuild.
    }

    /// <summary>
    /// The bake owns a completed zone and must finish its queued write before
    /// inspecting the stamp or destroying temporary terrain. Rebuild through
    /// vanilla so ApplyPendingTerrain receives location-shaped, pre-compiler
    /// heights. Ordinary spawn hooks still defer until placement finishes.
    /// </summary>
    internal static void CompletePendingTerrain(TerrainComp compiler, Heightmap heightmap)
    {
        if (compiler == null || heightmap == null || compiler.m_hmap != heightmap ||
            !s_pendingWrites.ContainsKey(compiler))
            return;
        heightmap.Regenerate();
    }

    /// <summary>Finish a server-owned zone through the same writer as live terrain.</summary>
    internal static void BakeRoadTerrain(Vector2s zone, Heightmap heightmap, TerrainComp compiler)
    {
        if (CarriesCurrentRoads(compiler)) return;
        QueueWrite(zone, RoadSpatialGrid.GetRoadPointsInZone(zone), heightmap, compiler, force: false);
        CompletePendingTerrain(compiler, heightmap);
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
        /// <summary>Road points' paint dedupe cells: NOT a count of what was painted.</summary>
        public HashSet<Vector2i> PaintedCells;
        /// <summary>Paint texels of this zone actually written.</summary>
        public int PaintedTexels;
    }

    /// <summary>The ground a place the game placed (not first, so with no preload) leaves at a point in its footprint,
    /// rebuilt from its saved placement (PlacedSiteReader.PlacedGroundAt, main thread); null where unknown.</summary>
    internal static System.Func<Vector2, float?>? PlacedGround;

    /// <summary>The ground a place leaves at <paramref name="p"/> in its footprint, as the game draws it under the
    /// compiler's deltas: a place placed before its roads, its preload (<see cref="PlacedSites.GroundAt"/>, the ground
    /// <see cref="FinishedGround"/> predicts on); any other placed place, rebuilt from its saved placement
    /// (<see cref="PlacedGround"/>). Null where unknown. The writer and the predictor both read it, so a piece is judged
    /// on the ground that is written.</summary>
    internal static float? PlacedBaseAt(Vector2 p) => PlacedSites.GroundAt(p) ?? PlacedGround?.Invoke(p);

    private static ModificationStats ModifyVertexHeights(Vector2s zoneID, List<RoadSpatialGrid.RoadPoint> roadPoints, TerrainContext context)
    {
        ModificationStats stats = new ModificationStats { PaintedCells = new HashSet<Vector2i>() };
        bool seamNear = NetworkCutoff.SeamNear(zoneID);

        // The fill spread, per road point, once per zone: it samples the
        // natural ground around the point, which the per-vertex loop cannot afford.
        float[]? fillExtra = FillExtras(roadPoints, WorldGenerator.instance);

        // A staircase's cuts (cut-only points): the ground within each one's
        // reach is lowered to its height where it stands higher, never raised.
        var cuts = roadPoints.FindAll(rp => rp.cutOnly);
        // A road onto a place's landing (RoadSiteEntry): the only points that level
        // the ground inside a place's footprint, and there only close to them.
        var entering = roadPoints.FindAll(rp => rp.intoSite && rp.IsRoad);

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
                bool inside = RoadSiteProtection.Contains(vertexPos2D);
                if (inside && entering.Count == 0) continue;
                // Next to a zone left as it is, the road fades in from its border (NetworkCutoff.SeamWeight).
                float seam = seamNear ? NetworkCutoff.SeamWeight(vertexPos2D) : 1f;
                if (seam <= 0f)
                    continue;
                
                int index = vz * context.GridSize + vx;
                float baseHeight = context.PreCompilerHeights[index] + context.HeightmapPosition.y;
                // With no road point here the vertex keeps what the compiler already
                // holds (an older road's cut, a player's digging): a staircase's cut
                // added later must lower it from there, never lift it back up.
                float current = baseHeight + (context.TerrainComp.m_modifiedHeight[index]
                    ? context.TerrainComp.m_levelDelta[index] + context.TerrainComp.m_smoothDelta[index] : 0f);
                if (WrittenDelta(vertexPos2D, baseHeight, current, inside, roadPoints, fillExtra, cuts, entering, out BlendResult blendResult) is float delta)
                {
                    if (seam >= 1f)
                    {
                        context.TerrainComp.m_levelDelta[index] = delta;
                        context.TerrainComp.m_smoothDelta[index] = 0f;
                    }
                    else
                    {
                        // Part of the road's change, as the full write does an absolute one: writing it
                        // again gives the same ground, however often an operator reapplies the road.
                        context.TerrainComp.m_levelDelta[index] = delta * seam;
                        context.TerrainComp.m_smoothDelta[index] = Mathf.Lerp(context.TerrainComp.m_smoothDelta[index], 0f, seam);
                    }
                    context.TerrainComp.m_modifiedHeight[index] = true;
                    stats.VerticesModified++;
                }
            }
        }

        return stats;
    }

    /// <summary>
    /// The delta <see cref="ModifyVertexHeights"/> writes at a vertex, null where it writes none: the road's earthwork
    /// (inside a place's footprint only that of a road onto its landing, <paramref name="entering"/>), then a staircase's
    /// cut where it stands lower. <paramref name="current"/>: the height the vertex holds before this write, which a cut
    /// with no road point here lowers from. Where a saved structure stands, the ground it was laid on
    /// (<see cref="StructureGround.Held"/>) instead, whatever the road would make of it. Any thread: reads only its
    /// arguments and the held ground.
    /// </summary>
    private static float? WrittenDelta(Vector2 vertex, float baseHeight, float current, bool inside, List<RoadSpatialGrid.RoadPoint> roadPoints,
        float[]? fillExtra, List<RoadSpatialGrid.RoadPoint> cuts, List<RoadSpatialGrid.RoadPoint> entering, out BlendResult blend)
    {
        blend = default;
        if (inside && entering.Count == 0) return null;
        float? road = RoadDelta(vertex, baseHeight, current, inside, roadPoints, fillExtra, cuts, entering, out blend);
        if (StructureGround.Held(vertex) is not float held) return road;
        // The road's own delta where it leaves the held ground exactly: the ground a structure was laid on by this writer.
        if (road is float same && baseHeight + same == held) return same;
        float delta = Mathf.Clamp(held - baseHeight, RoadConstants.TerrainDeltaMin, RoadConstants.TerrainDeltaMax);
        return road.HasValue || Writes(delta, 0f) ? delta : null;
    }

    /// <summary>The road's earthwork and a staircase's cut at a vertex (<see cref="WrittenDelta"/>), null where they write
    /// none.</summary>
    private static float? RoadDelta(Vector2 vertex, float baseHeight, float current, bool inside, List<RoadSpatialGrid.RoadPoint> roadPoints,
        float[]? fillExtra, List<RoadSpatialGrid.RoadPoint> cuts, List<RoadSpatialGrid.RoadPoint> entering, out BlendResult blend)
    {
        blend = inside
            ? CalculateBlendedHeight(entering, vertex, baseHeight, bounded: true)
            : CalculateBlendedHeight(roadPoints, vertex, baseHeight, fillExtra);
        float cut = !inside && cuts.Count > 0 ? LowestCut(cuts, vertex) : float.PositiveInfinity;
        if (blend.InfluencingPoints == 0 && float.IsPositiveInfinity(cut)) return null;
        float finalHeight = blend.InfluencingPoints == 0 ? current : RoadTarget(blend, vertex, baseHeight);
        bool cutHere = cut < finalHeight;
        if (blend.InfluencingPoints == 0 && !cutHere) return null;
        if (cutHere) finalHeight = cut;
        float delta = Mathf.Clamp(finalHeight - baseHeight, RoadConstants.TerrainDeltaMin, RoadConstants.TerrainDeltaMax);
        return Writes(delta, blend.MaxBlend) || cutHere ? delta : null;
    }

    /// <summary>
    /// The ground the terrain writer will leave at whole-metre vertices (the game's heightmap vertices), for a
    /// zone no player has dug, built from the network as it now stands: the natural height, or inside a place
    /// placed before its roads the ground it leaves (<see cref="PlacedSites"/>); then the road's earthwork and the
    /// staircases' cuts, exactly as <see cref="ModifyVertexHeights"/> writes them from the zone's points. NaN inside
    /// any other place's footprint, whose own levelling is not known until it spawns. Cached per vertex and per
    /// zone; one caller at a time (built for a pass after generation, or an append on the main thread).
    /// </summary>
    internal static System.Func<int, int, float> FinishedGround(System.Func<int, int, float> natural)
    {
        var zones = new Dictionary<Vector2s, (List<RoadSpatialGrid.RoadPoint> all, float[]? fill, List<RoadSpatialGrid.RoadPoint> cuts, List<RoadSpatialGrid.RoadPoint> entering)>();
        var heights = new Dictionary<(int, int), float>();
        return (x, z) =>
        {
            if (heights.TryGetValue((x, z), out float known)) return known;
            var v = new Vector2(x, z);
            bool inside = RoadSiteProtection.Contains(v);
            float baseHeight;
            if (!inside) baseHeight = natural(x, z);
            else baseHeight = PlacedSites.GroundAt(v) ?? float.NaN;
            float h = baseHeight;
            if (!float.IsNaN(baseHeight))
            {
                Vector2s zone = ZoneSystem.GetZone(new Vector3(x, 0f, z));
                if (!zones.TryGetValue(zone, out var points))
                {
                    var all = RoadSpatialGrid.GetRoadPointsInZone(zone);
                    zones[zone] = points = (all, FillExtras(all, WorldGenerator.instance), all.FindAll(rp => rp.cutOnly), all.FindAll(rp => rp.intoSite && rp.IsRoad));
                }
                if (WrittenDelta(v, baseHeight, baseHeight, inside, points.all, points.fill, points.cuts, points.entering, out _) is float delta)
                    h = baseHeight + delta;
            }
            heights[(x, z)] = h;
            return h;
        };
    }

    /// <summary>
    /// How far past a cut's own reach its batter runs: the cut climbs back to
    /// the natural ground at <see cref="CutBatterSlope"/> instead of standing
    /// as a wall. Cut flat to its reach and no further, a staircase sat in a
    /// trench with sheer dirt sides.
    /// </summary>
    public const float CutBatterReach = 3f;
    /// <summary>Rise over run of a cut's batter.</summary>
    public const float CutBatterSlope = 1f;

    /// <summary>The lowest height any cut within reach asks for at this vertex
    /// (infinity for none): its own height within its reach, climbing on the
    /// batter beyond.</summary>
    internal static float LowestCut(List<RoadSpatialGrid.RoadPoint> cuts, Vector2 vertex)
    {
        float lowest = float.PositiveInfinity;
        foreach (var c in cuts)
        {
            float reach = Mathf.Sqrt(c.w2) * 0.5f;
            float d = (c.p - vertex).magnitude;
            if (d > reach + CutBatterReach) continue;
            float h = c.h + Mathf.Max(0f, d - reach) * CutBatterSlope;
            if (h < lowest) lowest = h;
        }
        return lowest;
    }

    /// <summary>The fill spread per road point (null when it is off): it samples the
    /// natural ground round each point, so it is worked out once per set of points.</summary>
    /// <remarks>Also the shoulder a road lays over shallows it was let through on beside a place
    /// (<see cref="RoadShoreFill.Reach"/>), the wider of the two: a fill spread too, within its
    /// widest (<see cref="RoadEarthworkNoise.FillSpreadMax"/>, the zone gather's pad), and off with it.</remarks>
    internal static float[]? FillExtras(List<RoadSpatialGrid.RoadPoint> roadPoints, WorldGenerator? world)
    {
        if (!(RoadEarthworkNoise.FillSpread > 0f) || world == null) return null;
        bool shore = RoadShoreFill.FillDepth > 0f && RoadPathfinder.SqueezeCorridor > 0f;
        var fillExtra = new float[roadPoints.Count];
        for (int k = 0; k < roadPoints.Count; k++)
            if (roadPoints[k].IsRoad)
            {
                fillExtra[k] = RoadEarthworkNoise.FillExtra(roadPoints[k].p, roadPoints[k].h,
                    (x, z) => BiomeBlendedHeight.GetBlendedHeight(x, z, world));
                if (shore)
                    fillExtra[k] = Mathf.Max(fillExtra[k], RoadShoreFill.Reach(world, roadPoints[k].p, roadPoints[k].w));
            }
        return fillExtra;
    }
    /// <summary>The height the road asks for at a vertex its points reach.</summary>
    private static float RoadTarget(BlendResult blend, Vector2 vertex, float baseHeight) =>
        Mathf.Lerp(baseHeight, blend.TargetHeight, blend.MaxBlend)
        + RoadEarthworkNoise.FaceBump(vertex, blend.MaxBlend, blend.TargetHeight - baseHeight);

    /// <summary>Whether a vertex is written at all, for a change this big at this blend.</summary>
    private static bool Writes(float delta, float maxBlend) =>
        Mathf.Abs(delta) > RoadConstants.MinHeightDeltaThreshold || maxBlend > RoadConstants.MinBlendForModification;

    /// <summary>
    /// The height a road's earthwork leaves at a vertex outside every footprint, from
    /// <paramref name="baseHeight"/>: what <see cref="ModifyVertexHeights"/> writes there
    /// on a zone no player has dug and no staircase cuts. For a ground rebuilt before its
    /// zone loads (<see cref="PlacedSiteGround"/>). Any thread: reads only its arguments.
    /// </summary>
    internal static float ModelledHeight(List<RoadSpatialGrid.RoadPoint> roadPoints, Vector2 vertex, float baseHeight, float[]? fillExtra)
    {
        BlendResult blend = CalculateBlendedHeight(roadPoints, vertex, baseHeight, fillExtra);
        if (blend.InfluencingPoints == 0) return baseHeight;
        float delta = Mathf.Clamp(RoadTarget(blend, vertex, baseHeight) - baseHeight, RoadConstants.TerrainDeltaMin, RoadConstants.TerrainDeltaMax);
        return Writes(delta, blend.MaxBlend) ? baseHeight + delta : baseHeight;
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

    /// <summary>Whether far road points are passed over before their noise and batter are worked out: always, in the game
    /// and the unit suite alike; a test turns it off for the cull's reference (every point judged in full).</summary>
    internal static bool CullFarPoints = true;

    /// <summary>
    /// The road surface height at a vertex, from the road points within reach.
    /// The points sit on the centreline, so the surface is fitted as a LINE
    /// along the road (weighted least squares, the blend weights) and read
    /// at the vertex. A weighted mean would do mid-road,
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
    /// <param name="bounded">Inside a place's footprint: each point reaches its half width and
    /// <see cref="RoadSiteEntry.InsideMargin"/>, no further (no batter, noise or fill spread),
    /// so a road onto a place's landing never levels its entrance or its rocks.</param>
    private static BlendResult CalculateBlendedHeight(List<RoadSpatialGrid.RoadPoint> roadPoints, Vector2 vertexPos,
        float groundHere, float[]? fillExtra = null, bool bounded = false)
    {
        // Weighted sums for the fit h = a + b*dx + c*dy, (dx, dy) relative to
        // the vertex, so a is the surface height at the vertex.
        double sw = 0, sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0, sh = 0, sxh = 0, syh = 0;
        float maxBlend = 0f;
        int influencingPoints = 0;
        // The widest margin any point can have, past its half width and fill spread: the noise at its widest and the
        // widest batter. A point farther than that is passed over before its noise (a Perlin sample) and batter are worked
        // out; the rest are judged exactly as before.
        float widest = RoadConstants.TerrainBlendMargin * (1f + Mathf.Abs(RoadEarthworkNoise.WidthVariation)) + Mathf.Max(0f, BatterMaxExtra);

        for (int k = 0; k < roadPoints.Count; k++)
        {
            RoadSpatialGrid.RoadPoint rp = roadPoints[k];
            // A paint-only point (a waded ford) leaves the ground as it is; a
            // staircase's cut is applied on its own, after the road surface.
            if (!rp.IsRoad)
                continue;
            float distSq = (rp.p - vertexPos).sqrMagnitude;
            if (!bounded && CullFarPoints)
            {
                // With a little room for rounding.
                float far = (rp.w * 0.5f + widest + (fillExtra != null ? Mathf.Max(0f, fillExtra[k]) : 0f)) * 1.001f + 0.01f;
                if (distSq >= far * far)
                    continue;
            }
            // A road point standing well below the ground gets a wider
            // release, so the cut is walked out on a slope instead of ending
            // in a wall. The ground under the vertex stands in for the ground
            // under the point: they are metres apart, and this costs no query.
            float margin = bounded ? RoadSiteEntry.InsideMargin
                         : RoadConstants.TerrainBlendMargin * RoadEarthworkNoise.MarginFactor(rp.p)
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
    /// away, so without it paint runs down a raised causeway's bank. Settable
    /// for tests; 0 turns it off.
    /// </summary>
    internal static float PaintBelow = 0.5f;

    /// <summary>The ground height under a paint cell once levelled. With exact
    /// paint a cell is centred on its own vertex, as vanilla reads it.</summary>
    private static float LevelledCellHeight(TerrainContext context, int vx, int vy)
    {
        if (vx >= context.GridSize || vy >= context.GridSize) return float.NaN;
        int i = vy * context.GridSize + vx;
        return context.PreCompilerHeights[i] + context.HeightmapPosition.y
             + context.TerrainComp.m_levelDelta[i] + context.TerrainComp.m_smoothDelta[i];
    }

    /// <summary>Paint the road into the compiler; returns how many of its texels were written.</summary>
    private static int ApplyRoadPaint(List<RoadSpatialGrid.RoadPoint> roadPoints, TerrainComp terrainComp, HashSet<Vector2i> paintedCells,
        TerrainContext context)
    {
        Heightmap hmap = terrainComp.m_hmap;
        if (hmap == null)
            return 0;
        int painted = 0;
            
        int gridSize = terrainComp.m_width + 1;
        Vector3 terrainPos = hmap.transform.position;
        float scale = hmap.m_scale;
        bool seamNear = NetworkCutoff.SeamNear(ZoneSystem.GetZone(terrainPos));
        
        foreach (RoadSpatialGrid.RoadPoint roadPoint in roadPoints)
        {
            if (roadPoint.cutOnly) continue; // stone pieces cover a staircase; its cut is not painted
            if (roadPoint.unpainted) continue; // the road a structure stands on past its stairs' foot is levelled, not painted
            // Points closer together than PaintDedupeInterval paint the same
            // texels and are skipped, but only while a point's paint reaches
            // the neighbouring texels; a 2 m road paints 0.85 m out on 1 m
            // texels, so each point paints its own texel only and skipping any
            // of them would leave the texels between bare. Then every point paints.
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
            
            Vector3 worldPos = new Vector3(roadPoint.p.x, 0f, roadPoint.p.y);
            Vector3 localPos = worldPos - terrainPos;
            int halfWidth = (terrainComp.m_width + 1) / 2;
            int centerX = Mathf.FloorToInt(localPos.x / scale + 0.5f) + halfWidth;
            int centerY = Mathf.FloorToInt(localPos.z / scale + 0.5f) + halfWidth;
            // Paint is held against the levelled surface under the road point,
            // not the point's stored height: where two roads meet the
            // levelling blends them, and the higher road's surface sits below
            // its own stored height, which would break its paint at the join.
            float deck = PaintBelow > 0f
                && centerX >= 0 && centerY >= 0 && centerX < gridSize && centerY < gridSize
                ? LevelledCellHeight(context, centerX, centerY) : roadPoint.h;
            
            float radius = roadPoint.w * 0.5f;
            float radiusInVertices = radius / scale;
            // With exact distances the disc can reach one vertex further on
            // the side the snap moved away from.
            int radiusCeil = Mathf.CeilToInt(radiusInVertices) + 1;
            
            for (int dy = -radiusCeil; dy <= radiusCeil; dy++)
            {
                for (int dx = -radiusCeil; dx <= radiusCeil; dx++)
                {
                    int vx = centerX + dx;
                    int vy = centerY + dy;
                    
                    if (vx < 0 || vy < 0 || vx >= gridSize || vy >= gridSize)
                        continue;
                    
                    Vector2 paintPosition = new Vector2(
                        terrainPos.x + (vx - halfWidth) * scale,
                        terrainPos.z + (vy - halfWidth) * scale);
                    if (!roadPoint.intoSite && RoadSiteProtection.Contains(paintPosition)) continue;

                    // From the road point itself, not its snapped vertex; the cell is centred on its vertex
                    // the way vanilla reads a paint cell (Heightmap.WorldToVertexMask).
                    float distMetres = Vector2.Distance(paintPosition, roadPoint.p);
                    if (distMetres > radius)
                        continue;

                    // Paint follows the shared cross-section profile: solid
                    // in the core, fading out strictly inside the leveled
                    // footprint (unpainted verge).
                    float blendFactor = RoadProfile.PaintStrength(distMetres, roadPoint.w)
                        * (seamNear ? NetworkCutoff.SeamWeight(paintPosition) : 1f);
                    if (blendFactor <= 0f)
                        continue;
                    if (PaintBelow > 0f && deck - LevelledCellHeight(context, vx, vy) > PaintBelow)
                        continue;

                    int index = vy * gridSize + vx;
                    
                    Color currentColor = terrainComp.m_paintMask[index];
                    float alpha = currentColor.a;
                    Color newColor = Color.Lerp(currentColor, Heightmap.m_paintMaskPaved, blendFactor);
                    newColor.a = alpha;
                    
                    terrainComp.m_modifiedPaint[index] = true;
                    terrainComp.m_paintMask[index] = newColor;
                    painted++;
                }
            }
        }
        return painted;
    }

    private static void FinalizeTerrainMods(Vector2s zoneID, List<RoadSpatialGrid.RoadPoint> roadPoints, ModificationStats stats, TerrainContext context,
        bool onlyCuts = false)
    {
        // Texels actually written, not the dedupe cells: a zone whose road
        // points all lie just outside it fills the dedupe set without painting
        // a texel of its own, and counting that as a write saved and stamped
        // an empty compiler.
        int paintOps = stats.PaintedTexels;
        
        if (stats.VerticesModified > 0 || paintOps > 0)
        {
            // Stamp only what was saved. TerrainComp.Save does nothing unless
            // this peer owns the compiler, and a stamp without the data behind
            // it would tell every later load that the zone already carries
            // its roads, so they would never be written again.
            if (SaveTerrain(context.TerrainComp))
            {
                Stamp(context.TerrainComp);
                LastWriteOutcome = WriteOutcome.Written;
            }
            else
            {
                LastWriteOutcome = WriteOutcome.SaveRefused;
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning(
                    $"Zone {zoneID}: the terrain compiler did not save (not ours to write?); " +
                    "left unstamped so the roads are written again");
            }
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(
                $"Zone {zoneID}: {stats.VerticesModified}/{stats.VerticesChecked} vertices modified, {paintOps} paint cells");
        }
        else if (onlyCuts && context.TerrainComp.m_nview != null && context.TerrainComp.m_nview.IsOwner())
        {
            // Every pending point is a staircase's cut and the ground is already
            // low enough (or the spot is protected): nothing to save but the
            // stamp. Without it the zone was queued again every 2 s, its mesh
            // rebuilt each time, and the rock sweep never finished it.
            Stamp(context.TerrainComp);
            LastWriteOutcome = WriteOutcome.NothingToWrite;
        }
        else
        {
            LastWriteOutcome = WriteOutcome.NothingToWrite;
            // A point just outside the zone reaches into it only faintly, so a
            // zone whose points all lie outside it can have nothing to change.
            // Only a point INSIDE the zone that changed nothing is suspicious.
            if (AnyPointInside(zoneID, roadPoints))
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning(
                    $"Zone {zoneID}: {roadPoints.Count} road points but 0 vertices matched! Coordinate mismatch?");
            else
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(
                    $"Zone {zoneID}: its {roadPoints.Count} road points lie outside it and change nothing in it");
        }
    }

    private static bool AnyPointInside(Vector2s zoneID, List<RoadSpatialGrid.RoadPoint> roadPoints)
    {
        Vector3 zonePos = ZoneSystem.GetZonePos(zoneID);
        foreach (RoadSpatialGrid.RoadPoint rp in roadPoints)
        {
            if (Mathf.Abs(rp.p.x - zonePos.x) <= RoadConstants.HalfZoneSize &&
                Mathf.Abs(rp.p.y - zonePos.z) <= RoadConstants.HalfZoneSize)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Save the compiler's arrays into its ZDO, and say whether they are
    /// there. The game's Save returns without a word when this peer does not
    /// own the compiler; its data is then whatever it was before.
    /// </summary>
    internal static bool SaveTerrain(TerrainComp terrainComp)
    {
        ZNetView? view = terrainComp.m_nview;
        if (view == null || !view.IsValid() || !view.IsOwner())
            return false;
        terrainComp.Save();
        return view.GetZDO().GetByteArray(ZDOVars.s_TCData) != null;
    }
}
