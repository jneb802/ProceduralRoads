using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Handles terrain height and paint modifications for roads.
/// </summary>
public static class RoadTerrainModifier
{
    /// <summary>
    /// ZDO key on a zone's terrain compiler, the stamp (<see cref="Stamp"/>): this zone's road terrain is final for network
    /// version V. Its writer saved what the roads change in it, or found they change nothing (the roads only brush the
    /// zone, or a staircase's cuts find the ground already low enough): either is the writer's verdict, so either is
    /// stamped, and an empty stamped compiler is kept as it is (the server bake keeps one it made, too). Every reader asks
    /// <see cref="CarriesCurrentRoads(Vector2s, int)"/> (a zone spawn, a compiler coming alive, the sweep, the bake and its
    /// report): a zone stamped with the current version, or with an ancestor of it and no new road point in the zone, is
    /// not written again, so the road terrain is written once per network and the player's later terrain edits in the road
    /// survive reloads. A new network, or an append with points in the zone, makes the stamp stale and the zone is written
    /// again. Explicit paths (load-time generation, road_regen_island, road_apply) force the write and re-stamp. A write the
    /// game refused to save is never stamped.
    /// </summary>
    internal static readonly int AppliedVersionHash = "ProceduralRoads_AppliedVersion".GetStableHashCode();

    /// <summary>
    /// The version of this terrain writer: how it turns road points into heights and paint. Raised whenever a change to
    /// the writer would write a zone differently, so a later build can tell the zones an older writer wrote
    /// (<see cref="WriterVersionHash"/>).
    /// <para>1: the first release's writer.</para>
    /// <para>2: paint held to the road's own surface (its earthwork's delta, before a staircase's cut or a structure's held
    /// ground changes it), and none on a vertex standing on a face (<see cref="PaintFace"/>).</para>
    /// </summary>
    public const int WriterVersion = 2;

    /// <summary>ZDO key on a zone's terrain compiler: the <see cref="WriterVersion"/> that wrote it, stamped with
    /// <see cref="AppliedVersionHash"/> (<see cref="Stamp"/>); 0 on a compiler no writer of this build has stamped, and
    /// on every zone of a network a released build saved, which keeps the keys that build wrote.</summary>
    internal static readonly int WriterVersionHash = "ProceduralRoads_WriterVersion".GetStableHashCode();

    /// <summary>A zone's compiler carries the current network's roads as this writer wrote them (<see cref="AppliedVersionHash"/>):
    /// the one place a stamp is written, from <see cref="FinalizeTerrainMods"/>.</summary>
    private static void Stamp(TerrainComp terrainComp)
    {
        ZDO zdo = terrainComp.m_nview.GetZDO();
        zdo.Set(AppliedVersionHash, RoadSpatialGrid.RoadNetworkVersion);
        if (RoadNetworkPersistence.AllowsNetworkChanges) zdo.Set(WriterVersionHash, WriterVersion);
    }

    /// <summary>What a terrain write did.</summary>
    public enum WriteOutcome
    {
        None,
        /// <summary>Saved into the compiler and stamped.</summary>
        Written,
        /// <summary>The roads change nothing in this zone (its points only brush its edge, or a staircase's cuts find the
        /// ground low enough): stamped, nothing saved.</summary>
        NothingToWrite,
        /// <summary>The game would not save: this peer does not own the compiler.</summary>
        SaveRefused,
    }

    /// <summary>
    /// The outcome of the last write. ServerTerrainBake reads it straight after
    /// completing a compiler's queued terrain rebuild.
    /// </summary>
    public static WriteOutcome LastWriteOutcome = WriteOutcome.None;

    /// <summary>
    /// Where the terrain writer spends this peer's main-thread time: every zone write (a zone spawn, a compiler coming
    /// alive, the sweep, the bake), summed over a minute and written as one debug line when any ran (the plugin's Update asks
    /// <see cref="TakeLine"/>), as RoadRockStats does for the rock pass. Rewrites are writes of a zone already written this session for the same network: each is a full
    /// write and a heightmap rebuild. The time is the writer's own (heights, paint, save), not the game's mesh rebuild.
    /// </summary>
    internal static class WriteStats
    {
        internal static int Writes, NothingToWrite, Rewrites, MostWritesOfOneZone;
        internal static long TotalTicks, MaxTicks;
        internal static Vector2s SlowestZone;
        private static readonly Dictionary<(Vector2s zone, int version), int> s_perZone = new();
        /// <summary>How often the plugin writes the line (<see cref="TakeLine"/>), in seconds.</summary>
        internal const float Period = 60f;

        internal static void Record(Vector2s zone, int version, long ticks, WriteOutcome outcome)
        {
            Writes++;
            if (outcome == WriteOutcome.NothingToWrite) NothingToWrite++;
            int times = s_perZone.TryGetValue((zone, version), out int t) ? t + 1 : 1;
            s_perZone[(zone, version)] = times;
            if (times > 1) Rewrites++;
            if (times > MostWritesOfOneZone) MostWritesOfOneZone = times;
            TotalTicks += ticks;
            if (ticks > MaxTicks) { MaxTicks = ticks; SlowestZone = zone; }
        }

        private static string Ms(long ticks) =>
            (ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The minute's line, when the writer ran in it, as a string; the counts start again. Null when it did not run.</summary>
        internal static string? TakeLine()
        {
            if (Writes == 0) return null;
            string line = $"[TERRAIN] writes ({Period:F0} s): {Writes} zone write(s), {NothingToWrite} with nothing to write, " +
                          $"{Rewrites} rewrite(s) of a zone already written this session (the most writes of one zone {MostWritesOfOneZone}), " +
                          $"{Ms(TotalTicks)} ms in all, the slowest {Ms(MaxTicks)} ms (zone {SlowestZone})";
            Writes = NothingToWrite = Rewrites = MostWritesOfOneZone = 0;
            TotalTicks = MaxTicks = 0;
            return line;
        }

        internal static void Reset()
        {
            s_perZone.Clear();
            Writes = NothingToWrite = Rewrites = MostWritesOfOneZone = 0;
            TotalTicks = MaxTicks = 0;
        }
    }

    /// <summary>Prefab hash of the game's terrain compiler object (one per zone).</summary>
    internal static readonly int TerrainCompilerPrefabHash = "_TerrainCompiler".GetStableHashCode();

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
    /// Whether a loaded zone's road terrain is in place: the zone owes no road write (no road point reaches it, as the
    /// writer gathers them: a structure-only zone, a bridge's mid-span, needs no compiler), or its terrain compiler is
    /// alive (the roads are queued or written when it comes alive) and no write is still pending for it. Before that the
    /// ground under a road is the ungraded ground, so anything judged against it is judged wrongly. The one question of
    /// road-write debt every reader asks (RoadRockClearing.TerrainWritten). The one owner of terrain settled, and whether
    /// the world is held (docs/OWNERS.md).
    /// </summary>
    internal static bool TerrainSettled(Vector3 zonePos)
    {
        if (RoadSpatialGrid.GetRoadPointsInZone(ZoneSystem.GetZone(zonePos)).Count == 0) return true;
        TerrainComp? terrainComp = TerrainComp.FindTerrainCompiler(zonePos);
        return terrainComp != null && !s_pendingWrites.ContainsKey(terrainComp);
    }

    /// <summary>
    /// A zone's write met <see cref="ModificationStats.UnknownGround"/> vertices a road into a location would level whose location's
    /// ground is not known: a bug under the preload-list rule (a road enters only a location on the preload list, whose ground the
    /// preload published: RoadLocationProtection.Enterable), so said as an error naming the location, the zone and the road point.
    /// Those vertices are not written; the rest of the zone is, and it is stamped: nothing waits and nothing is written
    /// again for it (a saved network that roads into a location not on the list, from an older build, says it here).
    /// </summary>
    private static void SayUnknownGround(Vector2s zone, ModificationStats stats)
    {
        if (stats.UnknownGround == 0) return;
        Vector2 at = stats.UnknownAt;
        string location = RoadLocationProtection.NearestFootprint(at, 0f, out RoadLocationProtection.Footprint f)
            ? System.FormattableString.Invariant($"{LocationPrefabLevelling.PrefabAt(f.Centre) ?? "a location"} at ({f.Centre.x:F1}, {f.Centre.y:F1})")
            : "a location";
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogError(System.FormattableString.Invariant(
            $"Zone {zone}: road terrain into {location} whose ground is not known: {stats.UnknownGround} vertex(es) not written, first at ({at.x:F1}, {at.y:F1}), by the road point at ({stats.UnknownRoad.x:F1}, {stats.UnknownRoad.y:F1}); a road enters only a location on the preload list"));
    }

    /// <summary>Forget every pending write and sweep record: a world unloads, or a test starts afresh.</summary>
    public static void ResetForWorld()
    {
        s_pendingForcedZones.Clear();
        s_pendingWrites.Clear();
        s_swept.Clear();
        s_sweepWrites.Clear();
        WriteStats.Reset();
    }

    /// <summary>
    /// Safety net for zones the spawn path never wrote: a zone can have a
    /// location placed in the same spawn and then no road terrain at all while
    /// its neighbours are written, which leaves a cliff at the zone edge.
    /// Called every few seconds: a loaded zone with road points whose compiler
    /// does not carry the current network, and has no write pending, gets one
    /// queued. A compiler the game released is claimed, as
    /// OnTerrainCompilerReady does; another peer's is left for that peer. A zone is done for a network version once it
    /// carries it (a write that found nothing to write is stamped too, <see cref="AppliedVersionHash"/>), or after
    /// <see cref="SweepWrites"/> writes that left it unstamped, so a write dropped on its way (its owner changed at the
    /// rebuild) is queued again, and none loops.
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
            if (!TakeIfReleased(terrainComp.m_nview)) continue;   // another peer's; look again next sweep
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
        // Forced only by ApplyToLoadedZones, which asks it of zones the network
        // may write; otherwise a zone explored before this network is left exactly as it is.
        bool forced = s_pendingForcedZones.Remove(zoneID);
        if (roadPoints.Count == 0 || (!forced && (NetworkCutoff.Excludes(zoneID) || CarriesCurrentRoads(terrainComp))))
            return;

        if (!TakeIfReleased(terrainComp.m_nview))
            return;

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

    /// <summary>Whether the compiler is ours to write: already ours, or released by the game and now claimed. Another
    /// peer's is left alone (false): it writes its own zones.</summary>
    internal static bool TakeIfReleased(ZNetView nview)
    {
        if (nview.IsOwner()) return true;
        if (nview.HasOwner()) return false;
        nview.ClaimOwnership();
        return true;
    }

    // Saved compilers and live ones must agree: an append elsewhere does not
    // make this zone stale, and does not justify taking its owner or rebuilding it.
    internal static bool CarriesCurrentRoads(Vector2s zone, int applied)
    {
        int version = RoadSpatialGrid.RoadNetworkVersion;
        return version != 0 && applied != 0 && (applied == version ||
            RoadSpatialGrid.PendingPoints(RoadSpatialGrid.GetRoadPointsInZone(zone), applied).Count == 0);
    }

    /// <summary>Write appended roads into the loaded zones at once (a manual road's command): stamped and pre-network
    /// zones are left alone, and each zone gets only what it does not carry yet (OnTerrainCompilerReady).</summary>
    public static void ApplyAdditionsToLoadedZones()
    {
        if (RoadLifecycleManager.WorkersOwnWorld) return;
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

    /// <summary>
    /// Queue the current network's terrain for every loaded zone that has road points, whether or not the zone already
    /// carries it: zones generated before the network existed (around the player's login position on a fresh world) get
    /// their roads here; zones generated later get them as they spawn. Height deltas are computed from the location-shaped
    /// height, so writing them again does not accumulate; paint is blended toward the paved colour each time. A zone the
    /// network leaves as it is (NetworkCutoff.Excludes) is not written, whoever asks (the load, road_generate,
    /// road_regen_island).
    /// </summary>
    public static int ApplyToLoadedZones()
    {
        if (RoadLifecycleManager.WorkersOwnWorld) return 0;
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
        // A zone explored before this network is not written (ApplyToLoadedZones asks only of the others).
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
            terrainComp.m_nview != null && terrainComp.m_nview.IsValid())
            TakeIfReleased(terrainComp.m_nview);
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

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
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
        // The pending points say which vertices to write; every road point of the zone says to
        // what height, as a full write would: written from the new points alone, the north zone
        // of a frost cave's approach stood 0.43 m above its neighbour along their shared edge, a
        // tear.
        ModificationStats stats = ModifyVertexHeights(request.Zone, request.Points, context,
            ReferenceEquals(pending, request.Points) || pending.Count == request.Points.Count ? null : pending);
        stats.PaintedTexels = ApplyRoadPaint(pending, terrainComp, stats.RoadDeltas, context) + stats.KeptPaint;
        FinalizeTerrainMods(request.Zone, request.Points, stats, context, onlyCuts: pending.Count > 0 && pending.TrueForAll(p => p.cutOnly));
        SayUnknownGround(request.Zone, stats);
        WriteStats.Record(request.Zone, request.Version, System.Diagnostics.Stopwatch.GetTimestamp() - started, LastWriteOutcome);
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
        /// <summary>Paint texels of this zone actually written.</summary>
        public int PaintedTexels;
        /// <summary>Per vertex, the level delta the road's own earthwork asks for there, before a staircase's cut or a
        /// structure's held ground changes it; NaN where the road writes none. The road's surface, which paint is held to
        /// (<see cref="ApplyRoadPaint"/>).</summary>
        public float[] RoadDeltas;
        /// <summary>Border texels given the paint a zone left as it is holds there (<see cref="KeptGround"/>).</summary>
        public int KeptPaint;
        /// <summary>Vertices a road into a location would level whose location's ground is not known: not written, said as an error
        /// (<see cref="SayUnknownGround"/>); the first of them and the road point there.</summary>
        public int UnknownGround;
        public Vector2 UnknownAt, UnknownRoad;
    }

    /// <summary>
    /// The ground the zones left as they are (<see cref="NetworkCutoff"/>) hold, read once per zone write: each one's
    /// level and smooth deltas and paint, from its live compiler or else from its saved terrain. A zone shares its border
    /// vertices with its neighbour, each holding them in its own compiler, so the two must hold the same there. A kept
    /// zone can hold more than the generator's ground at its border (an earlier network's road, a player's digging), and a
    /// fade that started from the generator's ground there left a step along the seam, deep enough to open a hole.
    /// </summary>
    private sealed class KeptGround
    {
        private sealed class Zone
        {
            public int Width;
            public Vector3 Centre;
            public float[] Delta = null!;
            public bool[] Modified = null!;
            public Color[] Paint = null!;
            public bool[] Painted = null!;
        }

        private readonly Dictionary<Vector2s, Zone?> m_zones = new();

        private Zone? Of(Vector2s zone)
        {
            if (!m_zones.TryGetValue(zone, out Zone? kept)) m_zones[zone] = kept = Read(zone);
            return kept;
        }

        /// <summary>The kept zone's vertex nearest <paramref name="p"/> within its square: on the shared border, the very
        /// vertex the writing zone holds there.</summary>
        private static int Index(Zone kept, Vector2 p)
        {
            float spacing = RoadConstants.ZoneSize / kept.Width;
            int vx = Mathf.Clamp(Mathf.RoundToInt((p.x - kept.Centre.x) / spacing + kept.Width / 2f), 0, kept.Width);
            int vz = Mathf.Clamp(Mathf.RoundToInt((p.y - kept.Centre.z) / spacing + kept.Width / 2f), 0, kept.Width);
            return vz * (kept.Width + 1) + vx;
        }

        /// <summary>The change to the ground (level plus smooth) the kept zone holds nearest <paramref name="p"/>; null
        /// where it holds none, and the generator's ground is what it shows.</summary>
        public float? DeltaAt(Vector2s zone, Vector2 p)
        {
            Zone? kept = Of(zone);
            if (kept == null) return null;
            int i = Index(kept, p);
            return kept.Modified[i] ? kept.Delta[i] : null;
        }

        /// <summary>The paint the kept zone holds nearest <paramref name="p"/>, where it holds any.</summary>
        public bool PaintAt(Vector2s zone, Vector2 p, out Color paint)
        {
            paint = default;
            Zone? kept = Of(zone);
            if (kept == null) return false;
            int i = Index(kept, p);
            if (!kept.Painted[i]) return false;
            paint = kept.Paint[i];
            return true;
        }

        private static Zone? Read(Vector2s zone)
        {
            Vector3 centre = ZoneSystem.GetZonePos(zone);
            TerrainComp? live = TerrainComp.FindTerrainCompiler(centre);
            if (live != null && live.m_initialized && live.m_levelDelta != null && live.m_smoothDelta != null &&
                live.m_modifiedHeight != null && live.m_paintMask != null && live.m_modifiedPaint != null)
            {
                var delta = new float[live.m_levelDelta.Length];
                for (int i = 0; i < delta.Length; i++) delta[i] = live.m_levelDelta[i] + live.m_smoothDelta[i];
                return new Zone
                {
                    Width = live.m_width, Centre = centre, Delta = delta, Modified = (bool[])live.m_modifiedHeight.Clone(),
                    Paint = (Color[])live.m_paintMask.Clone(), Painted = (bool[])live.m_modifiedPaint.Clone(),
                };
            }
            List<ZDO> saved = SavedTerrainCompilers(zone);
            return saved.Count == 1 ? FromSave(saved[0].GetByteArray(ZDOVars.s_TCData), centre) : null;
        }

        /// <summary>The game's saved terrain (TerrainComp.Save, see <see cref="TerrainFingerprint"/>), read as its Load
        /// reads it; null where there is none or it does not read.</summary>
        private static Zone? FromSave(byte[]? data, Vector3 centre)
        {
            if (data == null || data.Length == 0) return null;
            try
            {
                var package = new ZPackage(Utils.Decompress(data));
                package.ReadInt();
                package.ReadInt();
                package.ReadVector3();
                package.ReadSingle();
                int vertices = package.ReadInt();
                int width = Mathf.RoundToInt(Mathf.Sqrt(vertices)) - 1;
                if (width <= 0 || (width + 1) * (width + 1) != vertices) return null;
                var kept = new Zone
                {
                    Width = width, Centre = centre, Delta = new float[vertices], Modified = new bool[vertices],
                    Paint = new Color[vertices], Painted = new bool[vertices],
                };
                for (int i = 0; i < vertices; i++)
                {
                    kept.Modified[i] = package.ReadBool();
                    if (kept.Modified[i]) kept.Delta[i] = package.ReadSingle() + package.ReadSingle();
                }
                // Paint saved on the (width + 1)^2 grid; an older width^2 grid is left unread (heights only).
                if (package.ReadInt() == vertices)
                    for (int i = 0; i < vertices; i++)
                    {
                        kept.Painted[i] = package.ReadBool();
                        if (kept.Painted[i])
                            kept.Paint[i] = new Color(package.ReadSingle(), package.ReadSingle(), package.ReadSingle(), package.ReadSingle());
                    }
                return kept;
            }
            catch (System.Exception)
            {
                return null;
            }
        }
    }

    /// <summary>The one owner of what the terrain writer does: cuts, batter and the ground after a staircase's cuts (docs/OWNERS.md).</summary>
    /// <param name="only">Write only the vertices these points reach (an incremental write of
    /// appended points); null for every vertex any point reaches. Heights always come from all
    /// of <paramref name="roadPoints"/>.</param>
    private static ModificationStats ModifyVertexHeights(Vector2s zoneID, List<RoadSpatialGrid.RoadPoint> roadPoints, TerrainContext context,
        List<RoadSpatialGrid.RoadPoint>? only = null)
    {
        ModificationStats stats = new ModificationStats { RoadDeltas = new float[context.GridSize * context.GridSize] };
        for (int i = 0; i < stats.RoadDeltas.Length; i++) stats.RoadDeltas[i] = float.NaN;
        bool seamNear = NetworkCutoff.SeamNear(zoneID);
        KeptGround? keptGround = seamNear ? new KeptGround() : null;

        // The fill spread, per road point, once per zone: it samples the
        // natural ground around the point, which the per-vertex loop cannot afford.
        float[]? fillExtra = FillExtras(roadPoints, WorldGenerator.instance);
        // The zone's points laid out once for the fit at every vertex (FitPoints).
        FitPoints fit = FitPoints.Of(roadPoints, fillExtra);

        // A staircase's cuts (cut-only points): the ground within each one's
        // reach is lowered to its height where it stands higher, never raised.
        var cuts = roadPoints.FindAll(rp => rp.cutOnly);
        // A road onto a location's landing (RoadLocationEntry): the only points that level
        // the ground inside a location's footprint, their own location's alone, and there only close to them.
        var entering = Entering(roadPoints);
        float[]? onlyFill = only != null ? FillExtras(only, WorldGenerator.instance) : null;
        var onlyCuts = only?.FindAll(rp => rp.cutOnly);
        var onlyEntering = only != null ? Entering(only) : null;

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
                bool inside = RoadLocationProtection.Contains(vertexPos2D);
                List<RoadSpatialGrid.RoadPoint> own = inside ? EnteringAt(vertexPos2D, entering) : NoPoints;
                // Next to a zone left as it is, the road fades in from its border (NetworkCutoff.SeamWeight), from the
                // ground that zone holds there.
                float seam = 1f;
                Vector2s kept = default;
                if (seamNear) seam = NetworkCutoff.SeamWeight(vertexPos2D, out kept);
                int index = vz * context.GridSize + vx;
                // The change to the ground the kept zone holds nearest this vertex, where it holds one (KeptGround): the
                // fade runs from it, exactly on the border they share, to what this zone would have at the end of the fade.
                float? held = null;
                if (seam < 1f)
                {
                    if (seam <= 0f && keptGround!.PaintAt(kept, vertexPos2D, out Color keptPaint))
                    {
                        context.TerrainComp.m_paintMask[index] = keptPaint;
                        context.TerrainComp.m_modifiedPaint[index] = true;
                        stats.KeptPaint++;
                    }
                    held = keptGround!.DeltaAt(kept, vertexPos2D);
                }
                // A location's footprint keeps its ground but under a road onto its landing; only on the border with a kept
                // zone is it written, where the two zones must agree.
                if (inside && own.Count == 0 && !(held.HasValue && seam <= 0f)) continue;
                if (seam <= 0f && !held.HasValue)
                    continue;
                
                float baseHeight = context.PreCompilerHeights[index] + context.HeightmapPosition.y;
                // Inside a location: the ground it leaves, which the game draws under the compiler's
                // deltas in play, whatever terrain this is written on: the one published lookup the predictor reads too
                // (PlacedLocations.GroundAt, acquired on the main thread before this write, ApplyPendingTerrain). Where it is not
                // known, a vertex a road into the location would level is not written on the ground under the compiler in its
                // stead: a bug under the preload-list rule, said as an error (SayUnknownGround), never waited on; the
                // predictor reads it as not known too (BaseGround: NaN).
                if (inside && PlacedLocations.GroundAt(vertexPos2D) is float placed) baseHeight = placed;
                else if (inside && own.Count > 0)
                {
                    if (stats.UnknownGround++ == 0) { stats.UnknownAt = vertexPos2D; stats.UnknownRoad = own[0].p; }
                    continue;
                }
                if (only != null && !(inside
                        ? EnteringAt(vertexPos2D, onlyEntering!) is { Count: > 0 } onlyOwn && CalculateBlendedHeight(onlyOwn, vertexPos2D, baseHeight, bounded: true).InfluencingPoints > 0
                        : AnyReaches(only, vertexPos2D, onlyFill)
                          || (onlyCuts!.Count > 0 && !float.IsPositiveInfinity(LowestCut(onlyCuts, vertexPos2D)))))
                    continue;
                // With no road point here the vertex keeps what the compiler already
                // holds (an older road's cut, a player's digging): a staircase's cut
                // added later must lower it from there, never lift it back up.
                bool modified = context.TerrainComp.m_modifiedHeight[index];
                float current = baseHeight + (modified ? context.TerrainComp.m_levelDelta[index] + context.TerrainComp.m_smoothDelta[index] : 0f);
                float? written = WrittenDelta(vertexPos2D, baseHeight, current, inside, fit, cuts, own, out BlendResult blendResult);
                if (held is float from)
                {
                    // From the kept zone's ground to the road's (or the generator's ground, off the road): the kept zone's
                    // earthwork is walked out instead of ending in a wall. Off the road a vertex this zone already holds is
                    // left as it is (a player's digging, or this fade written before), and so is a location's footprint away
                    // from a road onto its landing, which keeps its ground: both but on the border itself.
                    if (written.HasValue || seam <= 0f || (!modified && !inside))
                    {
                        context.TerrainComp.m_levelDelta[index] = Faded(from, seam, written ?? 0f);
                        context.TerrainComp.m_smoothDelta[index] = 0f;
                        context.TerrainComp.m_modifiedHeight[index] = true;
                        stats.VerticesModified++;
                    }
                }
                else if (written is float delta)
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
                        context.TerrainComp.m_levelDelta[index] = Faded(0f, seam, delta);
                        context.TerrainComp.m_smoothDelta[index] = Mathf.Lerp(context.TerrainComp.m_smoothDelta[index], 0f, seam);
                    }
                    context.TerrainComp.m_modifiedHeight[index] = true;
                    stats.VerticesModified++;
                }
                if (blendResult.InfluencingPoints > 0)
                {
                    // The road's change as the fade writes it, so its paint is held to the ground just written.
                    if (RoadLevelDelta(blendResult, vertexPos2D, baseHeight) is float road)
                        stats.RoadDeltas[index] = Faded(held ?? 0f, seam, road);
                }
            }
        }

        return stats;
    }

    /// <summary>A zone's points of roads planned into a location (<see cref="RoadSpatialGrid.RoadPoint.intoLocation"/>), by the location
    /// each is into (<see cref="RoadSpatialGrid.RoadPoint.location"/>, exact): what <see cref="EnteringAt"/> picks from. A point
    /// whose location is not known is into none.</summary>
    private static List<(Vector2 location, List<RoadSpatialGrid.RoadPoint> points)> Entering(List<RoadSpatialGrid.RoadPoint> roadPoints)
    {
        var into = new List<(Vector2 location, List<RoadSpatialGrid.RoadPoint> points)>();
        foreach (RoadSpatialGrid.RoadPoint rp in roadPoints)
        {
            if (!rp.intoLocation || !rp.Levelled || float.IsNaN(rp.location.x) || float.IsNaN(rp.location.y)) continue;
            int k = into.FindIndex(g => g.location.x == rp.location.x && g.location.y == rp.location.y);
            if (k < 0) into.Add((rp.location, new List<RoadSpatialGrid.RoadPoint> { rp }));
            else into[k].points.Add(rp);
        }
        return into;
    }

    private static readonly List<RoadSpatialGrid.RoadPoint> NoPoints = new();

    /// <summary>The road points that may level <paramref name="vertex"/> inside a footprint: those planned into the one location
    /// whose footprint holds it alone (RoadLocationProtection.Admits); none where another location's footprint holds it too. The
    /// writer (<see cref="ModifyVertexHeights"/>) and the predictor (<see cref="FinishedGround"/>) both ask this.</summary>
    private static List<RoadSpatialGrid.RoadPoint> EnteringAt(Vector2 vertex, List<(Vector2 location, List<RoadSpatialGrid.RoadPoint> points)> into)
    {
        foreach (var (location, points) in into)
            if (RoadLocationProtection.Admits(vertex, location)) return points;
        return NoPoints;
    }

    /// <summary>The one faded change to the ground at a vertex in a seam fade (<see cref="NetworkCutoff.SeamWeight"/>
    /// <paramref name="seam"/>): from <paramref name="from"/>, the change the kept zone holds there (0 where it holds none), on
    /// the shared border, to <paramref name="to"/>, this zone's own, past the fade. Both the ground written and the road's
    /// change its paint is held to (<see cref="ModificationStats.RoadDeltas"/>) read it, so paint follows the ground.</summary>
    private static float Faded(float from, float seam, float to) => from + Mathf.Clamp01(seam) * (to - from);

    /// <summary>A level change as the game keeps it: clamped to its terrain edit limit.</summary>
    private static float Clamped(float delta) => Mathf.Clamp(delta, RoadConstants.TerrainDeltaMin, RoadConstants.TerrainDeltaMax);

    /// <summary>
    /// The delta <see cref="ModifyVertexHeights"/> writes at a vertex, null where it writes none: the road's earthwork
    /// (inside a location's footprint only that of a road onto that location's landing, <paramref name="entering"/>: <see cref="EnteringAt"/>), then a staircase's
    /// cut where it stands lower. <paramref name="current"/>: the height the vertex holds before this write, which a cut
    /// with no road point here lowers from. Where a saved structure stands, the ground it was laid on
    /// (<see cref="StructureGround.Held"/>) instead, whatever the road would make of it. Any thread: reads only its
    /// arguments and the held ground.
    /// </summary>
    private static float? WrittenDelta(Vector2 vertex, float baseHeight, float current, bool inside, FitPoints roadPoints,
        List<RoadSpatialGrid.RoadPoint> cuts, List<RoadSpatialGrid.RoadPoint> entering, out BlendResult blend,
        StairStructures.WalkFloor? walks = null)
    {
        float? road = RoadWrite(vertex, baseHeight, current, inside, roadPoints, cuts, entering, out blend, walks);
        if (StructureGround.Held(vertex) is not float held) return road;
        // The road's own delta where it leaves the held ground exactly: the ground a structure was laid on by this writer.
        if (road is float same && baseHeight + same == held) return same;
        float delta = Clamped(held - baseHeight);
        return road.HasValue || Writes(delta, 0f) ? delta : null;
    }

    /// <summary>The road surface's own delta before cuts or held ground, for the writer's paint and the same surface
    /// read beyond a zone's border by <see cref="FinishedGround"/>.</summary>
    private static float? RoadLevelDelta(BlendResult blend, Vector2 vertex, float baseHeight)
    {
        if (blend.InfluencingPoints == 0) return null;
        float road = Clamped(RoadTarget(blend, vertex, baseHeight) - baseHeight);
        return Writes(road, blend.MaxBlend) ? road : null;
    }

    /// <summary>What the road points write at a vertex before any saved structure's ground is held there
    /// (<see cref="WrittenDelta"/>): inside a location's footprint only the points into that location (<paramref name="entering"/>),
    /// else the road's earthwork and a staircase's cut (<see cref="RoadDelta"/>); null where they write none.</summary>
    private static float? RoadWrite(Vector2 vertex, float baseHeight, float current, bool inside, FitPoints roadPoints,
        List<RoadSpatialGrid.RoadPoint> cuts, List<RoadSpatialGrid.RoadPoint> entering, out BlendResult blend,
        StairStructures.WalkFloor? walks = null)
    {
        blend = default;
        if (inside && entering.Count == 0) return null;
        return RoadDelta(vertex, baseHeight, current, inside, roadPoints, cuts, entering, out blend, walks);
    }

    /// <summary>
    /// A road's write before it is stored (RoadNetworkGenerator.UnderAPlannedStructure): its points as they would be stored
    /// (<paramref name="added"/>) and the stored points it raises to new heights (<paramref name="raised"/>), each zone's
    /// as the finished-ground predictor reads a road not stored yet (<see cref="PointsInZone"/>). Whether it changes
    /// a vertex (<see cref="Changes"/>) is this writer's own write there (<see cref="RoadWrite"/>, before any saved structure's
    /// ground is held) from the zone's stored points as <see cref="FinishedGround"/> reads them, with the road and without it.
    /// One caller at a time; it reads the network as it stands.
    /// </summary>
    internal sealed class Unwritten
    {
        private readonly Storing m_road;
        private readonly WorldGenerator? m_world;
        private readonly Dictionary<Vector2s, (ZonePoints without, ZonePoints with)> m_zones = new();

        internal Unwritten(List<RoadSpatialGrid.RoadPoint> added, RoadSpatialGrid.Raises raised, WorldGenerator? world,
            IEnumerable<StairRun>? runs = null, RoadStairRuns.FlightLanding? landing = null)
        {
            m_road = new Storing(added, raised, runs, landing); m_world = world;
        }

        /// <summary>Whether the road changes the ground the writer leaves at (<paramref name="x"/>, <paramref name="z"/>): its
        /// write there (<see cref="RoadWrite"/>) over the ground before roads (<see cref="BaseGround"/>) differs with the road
        /// from without it by more than a change the writer writes at all (RoadConstants.MinHeightDeltaThreshold, as
        /// <see cref="Writes"/> reads it). Ground not known (NaN: inside a location whose ground is not read yet) is no verdict
        /// (<see cref="Verdict"/>): false here.</summary>
        internal bool Changes(int x, int z, System.Func<int, int, float> natural) => Verdict(x, z, natural) == true;

        /// <summary><see cref="Changes"/>, with ground not known as no verdict: null where the ground before roads is not
        /// known (inside a location whose ground is not read yet) and the road writes there, with it or without it (points into
        /// that location: <see cref="EnteringAt"/>); false where neither writes there, which the writer leaves alone whatever its
        /// ground (<see cref="RoadWrite"/>).</summary>
        internal bool? Verdict(int x, int z, System.Func<int, int, float> natural)
        {
            var v = new Vector2(x, z);
            float baseHeight = BaseGround(v, natural, out bool inside);
            Vector2s zone = ZoneSystem.GetZone(new Vector3(x, 0f, z));
            if (!m_zones.TryGetValue(zone, out var points))
                m_zones[zone] = points = (new ZonePoints(PointsInZone(zone, null), m_world), new ZonePoints(PointsInZone(zone, m_road), m_world));
            if (float.IsNaN(baseHeight))
                return inside && (EnteringAt(v, points.with.Entering).Count > 0 || EnteringAt(v, points.without.Entering).Count > 0) ? null : false;
            float Written(ZonePoints p, StairStructures.WalkFloor? walks) =>
                RoadWrite(v, baseHeight, baseHeight, inside, p.Fit, p.Cuts, inside ? EnteringAt(v, p.Entering) : NoPoints, out _, walks) ?? 0f;
            return Mathf.Abs(Written(points.with, m_road.Walks) - Written(points.without, null)) > RoadConstants.MinHeightDeltaThreshold;
        }
    }

    /// <summary>A zone's points as the writer reads them (<see cref="ModifyVertexHeights"/>): all, their fill spread, laid
    /// out for the fit (<see cref="FitPoints"/>), the cuts, those into each location. What the readers predicting the writer
    /// (<see cref="Unwritten"/>, <see cref="FinishedGround"/>) work out once per zone.</summary>
    private sealed class ZonePoints
    {
        public readonly List<RoadSpatialGrid.RoadPoint> All;
        public readonly float[]? Fill;
        public readonly FitPoints Fit;
        public readonly List<RoadSpatialGrid.RoadPoint> Cuts;
        public readonly List<(Vector2 location, List<RoadSpatialGrid.RoadPoint> points)> Entering;
        public ZonePoints(List<RoadSpatialGrid.RoadPoint> all, WorldGenerator? world)
        {
            All = all; Fill = FillExtras(all, world); Fit = FitPoints.Of(all, Fill); Cuts = all.FindAll(rp => rp.cutOnly); Entering = RoadTerrainModifier.Entering(all);
        }
    }

    /// <summary>The zones <see cref="FinishedGround"/> has read, each one's points once (<see cref="ZonePoints"/>), with the
    /// road not stored yet as it stood when they were made (<see cref="AsStored"/>): shared by the readers one pass makes
    /// of the same network (<see cref="SharedPaintHeight"/>'s ground and the road's surface).</summary>
    private sealed class FinishedZones
    {
        private readonly Storing? m_storing = t_storing;
        private readonly Dictionary<Vector2s, ZonePoints> m_zones = new();
        private Vector2s m_lastZone;
        private ZonePoints? m_last;

        internal ZonePoints For(Vector2s zone)
        {
            if (m_last != null && m_lastZone.x == zone.x && m_lastZone.y == zone.y) return m_last;
            if (!m_zones.TryGetValue(zone, out ZonePoints? points))
                m_zones[zone] = points = new ZonePoints(PointsInZone(zone, m_storing), WorldGenerator.instance);
            m_lastZone = zone; m_last = points;
            return points;
        }

        internal StairStructures.WalkFloor? Walks => m_storing?.Walks;
    }

    /// <summary>The ground a vertex holds before any road writes it, as the writer reads it: the natural height, or inside a
    /// location's footprint (<paramref name="inside"/>) the ground it leaves as it is published (<see cref="PlacedLocations.GroundAt(Vector2)"/>),
    /// NaN where that is not known. <see cref="FinishedGround"/> and <see cref="Unwritten"/> read it.</summary>
    private static float BaseGround(Vector2 v, System.Func<int, int, float> natural, out bool inside)
    {
        inside = RoadLocationProtection.Contains(v);
        return inside ? PlacedLocations.GroundAt(v) ?? float.NaN : natural(Mathf.RoundToInt(v.x), Mathf.RoundToInt(v.y));
    }

    /// <summary>The road's earthwork and a staircase's cut at a vertex (<see cref="WrittenDelta"/>), null where they write
    /// none.</summary>
    private static float? RoadDelta(Vector2 vertex, float baseHeight, float current, bool inside, FitPoints roadPoints,
        List<RoadSpatialGrid.RoadPoint> cuts, List<RoadSpatialGrid.RoadPoint> entering, out BlendResult blend,
        StairStructures.WalkFloor? walks = null)
    {
        blend = inside
            ? CalculateBlendedHeight(entering, vertex, baseHeight, bounded: true)
            : BlendedHeight(roadPoints, vertex, baseHeight);
        float cut = !inside && cuts.Count > 0 ? CutAt(cuts, vertex, walks) : float.PositiveInfinity;
        if (blend.InfluencingPoints == 0 && float.IsPositiveInfinity(cut)) return null;
        float finalHeight = blend.InfluencingPoints == 0 ? current : RoadTarget(blend, vertex, baseHeight);
        bool cutHere = cut < finalHeight;
        if (blend.InfluencingPoints == 0 && !cutHere) return null;
        if (cutHere) finalHeight = cut;
        float delta = Clamped(finalHeight - baseHeight);
        return Writes(delta, blend.MaxBlend) || cutHere ? delta : null;
    }

    /// <summary>A road not stored yet, as the finished-ground predictor reads it while a verdict is made before its road is
    /// stored (<see cref="AsStored"/>): its points as it would store them, and the stored points it raises to new heights.</summary>
    private sealed class Storing
    {
        public readonly List<RoadSpatialGrid.RoadPoint> Added;
        /// <summary>The stored points it raises by the commit's own rule (RoadSpatialGrid.Raises): which moves, to what.</summary>
        public readonly RoadSpatialGrid.Raises Raises;
        /// <summary>Its staircases' walks (StairStructures.WalkFloor), which floor the cuts under them as once they are stored.</summary>
        public readonly StairStructures.WalkFloor? Walks;
        /// <summary>The stored cuts its landing takes back (RoadStairRuns.CutsTakenBack), left out; null for none.</summary>
        public readonly RoadSpatialGrid.CutsTakenBack? Removed;
        public Storing(List<RoadSpatialGrid.RoadPoint> added, RoadSpatialGrid.Raises raised, IEnumerable<StairRun>? runs = null,
            RoadStairRuns.FlightLanding? landing = null)
        {
            Added = added; Raises = raised;
            Walks = runs != null || landing != null
                ? new StairStructures.WalkFloor(runs ?? System.Array.Empty<StairRun>(), landing?.Run, landing?.Trimmed) : null;
            Removed = landing != null ? RoadStairRuns.CutsTakenBack(landing) : null;
        }

        /// <summary>A stored point as it will stand once the road is: null where its landing takes it back, else at the height
        /// its raises take it to.</summary>
        public RoadSpatialGrid.RoadPoint? AsItWillStand(RoadSpatialGrid.RoadPoint rp, int me)
        {
            if (Removed != null && Removed.Takes(rp, me)) return null;
            if (Raises.To(rp, me, out float to)) rp.h = to;
            return rp;
        }
    }

    /// <summary>The road the predictor made on this thread reads as stored (<see cref="AsStored"/>); null: the network as it stands.</summary>
    [System.ThreadStatic] private static Storing? t_storing;
    /// <summary>The network as a road not stored yet would leave it, on this thread, by identity (null: as stored); a remembered
    /// piece (RoadSpatialGrid's) is keyed by it.</summary>
    internal static object? StoringNow => t_storing;

    /// <summary>
    /// Until disposed, every finished-ground predictor made on this thread (<see cref="FinishedGround"/>, so
    /// StructureGround.Vertices and StructureGround.At) reads the network as it will stand once a road not stored yet is:
    /// <paramref name="added"/>, its points as it would store them, gathered by each zone as stored points are
    /// (RoadSpatialGrid.Gathers), the stored points it raises (<paramref name="raised"/>) at their new heights, and its
    /// staircases (<paramref name="runs"/>) flooring the cuts under their walks as once they are stored (StairStructures.CutFloor,
    /// <see cref="CutAt"/>). What a structure's verdict before its road is stored reads (RoadNetworkGenerator.BuiltRefusal, RoadNetworkGenerator.InAnotherLocation,
    /// RoadNetworkGenerator.WithNoKit, RoadNetworkGenerator.StairsRefusal): the ground its first build reads, the road's own
    /// earthwork at its ends in it; the built road those checks read too (<see cref="PointsNear"/>).
    /// </summary>
    internal static System.IDisposable AsStored(List<RoadSpatialGrid.RoadPoint> added, List<(RoadSpatialGrid.RoadPoint at, float to)> raised,
        IEnumerable<StairRun>? runs = null) => AsStored(added, new RoadSpatialGrid.Raises(raised), runs);

    /// <summary><see cref="AsStored(List{RoadSpatialGrid.RoadPoint}, List{ValueTuple{RoadSpatialGrid.RoadPoint, float}}, IEnumerable{StairRun}?)"/>
    /// with the stored points raised by <paramref name="raised"/>, in its stages (a road's crossings', then its landing's).</summary>
    internal static System.IDisposable AsStored(List<RoadSpatialGrid.RoadPoint> added, RoadSpatialGrid.Raises raised, IEnumerable<StairRun>? runs = null,
        RoadStairRuns.FlightLanding? landing = null)
    {
        var restore = new Restore(t_storing);
        t_storing = new Storing(added, raised, runs, landing);
        return restore;
    }

    private sealed class Restore : System.IDisposable
    {
        private readonly Storing? m_before;
        public Restore(Storing? before) { m_before = before; }
        public void Dispose() => t_storing = m_before;
    }

    /// <summary>A zone's points as the writer gathers them (RoadSpatialGrid.GetRoadPointsInZone), with a road not stored yet
    /// (<paramref name="storing"/>) as it would be: the stored points it raises at their new heights, its own gathered.</summary>
    private static List<RoadSpatialGrid.RoadPoint> PointsInZone(Vector2s zone, Storing? storing)
    {
        List<RoadSpatialGrid.RoadPoint> stored = RoadSpatialGrid.GetRoadPointsInZone(zone);
        if (storing == null) return stored;
        var with = new List<RoadSpatialGrid.RoadPoint>(stored.Count + storing.Added.Count);
        int me = CrossIslandReads.Me;
        foreach (RoadSpatialGrid.RoadPoint rp in stored) if (storing.AsItWillStand(rp, me) is RoadSpatialGrid.RoadPoint now) with.Add(now);
        Vector3 centre = ZoneSystem.GetZonePos(zone);
        foreach (RoadSpatialGrid.RoadPoint rp in storing.Added)
            if (RoadSpatialGrid.Gathers(centre, rp)) with.Add(rp);
        return with;
    }

    /// <summary>
    /// Every road point within <paramref name="radius"/> of <paramref name="at"/> as the network stands
    /// (RoadSpatialGrid.GetRoadPointsNearPosition), and within <see cref="AsStored"/> as it will stand once the road not
    /// stored yet is: the stored points it raises at their new heights, its own points as it would store them. What the
    /// built-road checks read (StructureEnds.BuiltRoadNear: RoadNetworkGenerator.BuiltRefusal), so a verdict made before its
    /// road is stored reads the road as the predictor does, its own fill at its structures' ends in it.
    /// </summary>
    internal static List<RoadSpatialGrid.RoadPoint> PointsNear(Vector3 at, float radius)
    {
        List<RoadSpatialGrid.RoadPoint> stored = RoadSpatialGrid.GetRoadPointsNearPosition(at, radius);
        Storing? storing = t_storing;
        if (storing == null) return stored;
        var with = new List<RoadSpatialGrid.RoadPoint>(stored.Count);
        int me = CrossIslandReads.Me;
        foreach (RoadSpatialGrid.RoadPoint rp in stored) if (storing.AsItWillStand(rp, me) is RoadSpatialGrid.RoadPoint now) with.Add(now);
        Vector2 centre = new(at.x, at.z);
        float radiusSq = radius * radius;
        foreach (RoadSpatialGrid.RoadPoint rp in storing.Added)
            if ((rp.p - centre).sqrMagnitude <= radiusSq) with.Add(rp);
        return with;
    }

    /// <summary>
    /// The surface of the road nearest <paramref name="at"/> within <paramref name="reach"/>: its height, how far its point is
    /// and that point's road width, as the network stands (RoadSpatialGrid.TryGetRoadHeightWithin) and within
    /// <see cref="AsStored"/> as it will stand once the road not stored yet is (<see cref="PointsNear"/>: its own points, the
    /// stored ones it raises at their new heights). What the end reader stands a walker on (StairEnds.WithRoads), so a
    /// staircase's ends read before its road is stored read that road as the audit reads it once it is.
    /// </summary>
    internal static bool RoadHeightNear(Vector2 at, float reach, out float height, out float distance, out float width)
    {
        if (t_storing == null) return RoadSpatialGrid.TryGetRoadHeightWithin(at, reach, out height, out distance, out width);
        // The one reader of a road's height (RoadSpatialGrid.SurfaceReader), over the points as they will stand, as far
        // round as the nearest one's fit may reach.
        float widest = 0f;
        List<RoadSpatialGrid.RoadPoint> near = PointsNear(new Vector3(at.x, 0f, at.y), reach);
        foreach (RoadSpatialGrid.RoadPoint rp in near) if (rp.IsRoad && rp.w > widest) widest = rp.w;
        List<RoadSpatialGrid.RoadPoint> around = PointsNear(new Vector3(at.x, 0f, at.y), reach + FitReach(widest));
        return (t_storedSurface ??= new RoadSpatialGrid.SurfaceReader()).ResetOver(around).Read(at, reach, out height, out distance, out width);
    }

    /// <summary>
    /// A road's points at exactly <paramref name="at"/>, in order, as the network stores them (RoadSpatialGrid.StoredAt), and
    /// within <see cref="AsStored"/> as it will once the road not stored yet is: its own points as it would store them, the
    /// stored ones at the heights it raises them to. Null where any is not there. What a staircase's end reads its road's
    /// slope on (StairRoad.Stored).
    /// </summary>
    internal static RoadSpatialGrid.RoadPoint[]? StoredAt(IReadOnlyList<Vector2> at)
    {
        Storing? storing = t_storing;
        if (storing == null) return RoadSpatialGrid.StoredAt(at);
        var found = new RoadSpatialGrid.RoadPoint[at.Count];
        int me = CrossIslandReads.Me;
        for (int i = 0; i < at.Count; i++)
        {
            Vector2 p = at[i];
            int k = storing.Added.FindIndex(rp => rp.IsRoad && rp.p.x == p.x && rp.p.y == p.y);
            if (k >= 0) { found[i] = storing.Added[k]; continue; }
            if (RoadSpatialGrid.StoredAt(new[] { p }) is not { } stored || storing.AsItWillStand(stored[0], me) is not RoadSpatialGrid.RoadPoint now)
                return null;
            found[i] = now;
        }
        return found;
    }

    /// <summary>
    /// The ground the terrain writer will leave at whole-metre vertices (the game's heightmap vertices), for a
    /// zone no player has dug, built from the network as it now stands: the natural height, or inside a location the
    /// ground it leaves as it is published (<see cref="PlacedLocations.GroundAt(Vector2)"/>, the writer's own lookup); then the
    /// road's earthwork and the staircases' cuts, exactly as <see cref="ModifyVertexHeights"/> writes them from the zone's
    /// points. NaN inside a location whose ground is not known yet (not placed first, and its saved placement not read). Cached per vertex and per
    /// zone; one caller at a time (built for a pass after generation, or an append on the main thread).
    /// With <paramref name="roadSurface"/>, the road's surface before cuts and held ground, as its paint reads it
    /// (<see cref="RoadLevelDelta"/>). The one owner of ground readers (docs/OWNERS.md).
    /// </summary>
    internal static System.Func<int, int, float> FinishedGround(System.Func<int, int, float> natural, bool roadSurface = false) =>
        FinishedGround(natural, roadSurface, new FinishedZones());

    /// <summary><see cref="FinishedGround(System.Func{int, int, float}, bool)"/> reading its zones through
    /// <paramref name="zones"/>, which another reader of the same pass may share.</summary>
    private static System.Func<int, int, float> FinishedGround(System.Func<int, int, float> natural, bool roadSurface, FinishedZones zones)
    {
        var heights = new Dictionary<Vector2i, float>(RoadGridComparer.Instance);
        return (x, z) =>
        {
            var key = new Vector2i(x, z);
            if (heights.TryGetValue(key, out float known)) return known;
            var v = new Vector2(x, z);
            float baseHeight = BaseGround(v, natural, out bool inside);
            float h = baseHeight;
            if (!float.IsNaN(baseHeight))
            {
                ZonePoints points = zones.For(ZoneSystem.GetZone(new Vector3(x, 0f, z)));
                List<RoadSpatialGrid.RoadPoint> entering = inside ? EnteringAt(v, points.Entering) : NoPoints;
                float? written = roadSurface
                    ? RoadLevelDelta(inside ? CalculateBlendedHeight(entering, v, baseHeight, bounded: true)
                        : BlendedHeight(points.Fit, v, baseHeight), v, baseHeight)
                    : WrittenDelta(v, baseHeight, baseHeight, inside, points.Fit, points.Cuts, entering, out _, zones.Walks);
                if (written is float delta) h = baseHeight + delta;
            }
            heights[key] = h;
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

    /// <summary>The height the staircases' cuts take the ground to at this vertex (infinity for none): the lowest any cut within
    /// reach asks for (<see cref="LowestCut"/>), but never below a walk over it: under a walked piece no lower than just under
    /// its walking surface (StairStructures.CutFloor; with <paramref name="walks"/>, a road's staircases read before it is
    /// stored, as once they are: RoadTerrainModifier.AsStored, Unwritten). A cut uncovers a walk and the headroom over it; the ground under a
    /// flight, which its supports stand in, stays. The one owner of what the terrain writer does (docs/OWNERS.md).</summary>
    internal static float CutAt(List<RoadSpatialGrid.RoadPoint> cuts, Vector2 vertex, StairStructures.WalkFloor? walks = null)
    {
        float cut = LowestCut(cuts, vertex);
        return float.IsPositiveInfinity(cut) ? cut : Mathf.Max(cut, StairStructures.CutFloor(vertex, walks));
    }

    /// <summary>The lowest height any cut within reach asks for at this vertex
    /// (infinity for none): its own height within its reach, climbing on the
    /// batter beyond.</summary>
    internal static float LowestCut(List<RoadSpatialGrid.RoadPoint> cuts, Vector2 vertex)
    {
        float lowest = float.PositiveInfinity;
        foreach (var c in cuts)
        {
            float h = CutHeightAt(c.p, c.h, Mathf.Sqrt(c.w2) * 0.5f, vertex);
            if (h < lowest) lowest = h;
        }
        return lowest;
    }

    /// <summary>The height one cut at <paramref name="at"/>, <paramref name="height"/> high within <paramref name="reach"/>, asks of
    /// <paramref name="vertex"/>: its own height within its reach, climbing on the batter beyond it, infinity past that
    /// (<see cref="CutBatterReach"/>). The one formula of a cut's ground, for the writer and for what predicts it. The one owner of what the terrain writer does (docs/OWNERS.md).</summary>
    private static float CutHeightAt(Vector2 at, float height, float reach, Vector2 vertex)
    {
        float d = (at - vertex).magnitude;
        if (d > reach + CutBatterReach) return float.PositiveInfinity;
        return height + Mathf.Max(0f, d - reach) * CutBatterSlope;
    }

    /// <summary>How far from a staircase's cut its ground is changed: the cut's own reach and its batter (the writer's reach,
    /// <see cref="LowestCut"/>). The one owner of what the terrain writer does (docs/OWNERS.md).</summary>
    internal const float StairCutReach = StairLayout.CutRadius + CutBatterReach;

    /// <summary>How far from a cut-only point of stored width <paramref name="width"/> its ground is changed (half the width and the
    /// batter): <see cref="StairCutReach"/> for a staircase's cut (<see cref="StairLayout.CutWidth"/>), more for a wider crossing cut.</summary>
    internal static float CutReach(float width) => width * 0.5f + CutBatterReach;

    /// <summary>A vertex spacing past a cut's reach that a zone still gathers it, so the zone next door, holding the same border
    /// vertices, sees it too.</summary>
    internal const float CutGatherPad = 1f;

    /// <summary>
    /// The ground <paramref name="ground"/> at (<paramref name="x"/>, <paramref name="z"/>) as the writer leaves it under the
    /// not yet written staircase cuts <paramref name="cuts"/> (a plan's, each a point and its height, at the staircase cut's
    /// radius): lowered to the lowest cut within reach, climbing back on the batter, as <see cref="RoadDelta"/> does. Read-only,
    /// for what predicts the writer (the walking floor a built staircase holds the cut above is not read: nothing stands yet).
    /// The one owner of what the terrain writer does (docs/OWNERS.md).
    /// </summary>
    internal static float GroundAfterCuts(float ground, IReadOnlyList<Vector3> cuts, float x, float z)
    {
        var vertex = new Vector2(x, z);
        float g = ground;
        foreach (Vector3 c in cuts)
        {
            float h = CutHeightAt(new Vector2(c.x, c.z), c.y, StairLayout.CutRadius, vertex);
            if (h < g) g = h;
        }
        return g;
    }

    /// <summary>The fill spread per road point (null when it is off): it samples the
    /// natural ground round each point, so it is worked out once per set of points.</summary>
    /// <remarks>Also the shoulder a road lays over shallows it was let through on beside a location
    /// (<see cref="RoadShoreFill.Reach"/>), the wider of the two: a fill spread too, within its
    /// widest (<see cref="RoadEarthworkNoise.FillSpreadMax"/>, the zone gather's pad), and off with it.</remarks>
    internal static float[]? FillExtras(List<RoadSpatialGrid.RoadPoint> roadPoints, WorldGenerator? world)
    {
        if (!(RoadEarthworkNoise.FillSpread > 0f) || world == null) return null;
        bool shore = RoadShoreFill.FillDepth > 0f && RoadPathfinder.SqueezeCorridor > 0f;
        var fillExtra = new float[roadPoints.Count];
        for (int k = 0; k < roadPoints.Count; k++)
            if (roadPoints[k].Levelled)
            {
                fillExtra[k] = RoadEarthworkNoise.FillExtra(roadPoints[k].p, roadPoints[k].h,
                    (x, z) => BiomeBlendedHeight.GetBlendedHeight(x, z, world));
                if (shore)
                    fillExtra[k] = Mathf.Max(fillExtra[k], RoadShoreFill.Reach(world, roadPoints[k].p, roadPoints[k].w));
            }
        return fillExtra;
    }

    /// <summary>The height the road asks for at a vertex its points reach: the level blend, with the earthwork noise on
    /// its face, kept between the natural ground and the road's height, so the noise never digs a fill face below the
    /// ground nor raises a cut face above it (a dip at a fill's toe stood its whole height as one wall).</summary>
    private static float RoadTarget(BlendResult blend, Vector2 vertex, float baseHeight) =>
        Mathf.Clamp(Mathf.Lerp(baseHeight, blend.TargetHeight, blend.MaxBlend)
            + RoadEarthworkNoise.FaceBump(vertex, blend.MaxBlend, blend.TargetHeight - baseHeight, blend.FaceWidth),
            Mathf.Min(baseHeight, blend.TargetHeight), Mathf.Max(baseHeight, blend.TargetHeight));

    /// <summary>Whether a vertex is written at all, for a change this big at this blend.</summary>
    private static bool Writes(float delta, float maxBlend) =>
        Mathf.Abs(delta) > RoadConstants.MinHeightDeltaThreshold || maxBlend > RoadConstants.MinBlendForModification;

    /// <summary>
    /// The height a road's earthwork leaves at a vertex outside every footprint, from
    /// <paramref name="baseHeight"/>: what <see cref="ModifyVertexHeights"/> writes there
    /// on a zone no player has dug and no staircase cuts (the writer's own <see cref="RoadDelta"/>, with no cuts).
    /// Any thread: reads only its arguments.
    /// </summary>
    internal static float ModelledHeight(List<RoadSpatialGrid.RoadPoint> roadPoints, Vector2 vertex, float baseHeight, float[]? fillExtra)
    {
        var none = new List<RoadSpatialGrid.RoadPoint>();
        return RoadDelta(vertex, baseHeight, baseHeight, false, FitPoints.Of(roadPoints, fillExtra), none, none, out _) is float delta ? baseHeight + delta : baseHeight;
    }

    /// <summary>
    /// The height this writer writes at <paramref name="vertex"/> from <paramref name="roadPoints"/> over
    /// <paramref name="baseHeight"/>, before the game's clamp (<see cref="Clamped"/>): the road's surface as it fits it
    /// (<see cref="CalculateBlendedHeight"/>: the line along the road, <see cref="FitHeightAtVertex"/>, past a road's end its
    /// own last stretch), blended into the ground past each point's flat core with the face noise
    /// (<see cref="RoadTarget"/>), as <see cref="RoadDelta"/> writes it outside a footprint where no staircase cuts; the
    /// ground itself where no point reaches. With no fill spread (it is read round each stored point, from the ground).
    /// What the earthwork cap measures a road by (RoadNetworkGenerator.EarthworkAcross). Any thread: reads only its
    /// arguments. The one owner of what the terrain writer does (docs/OWNERS.md).
    /// </summary>
    internal static float WrittenAt(List<RoadSpatialGrid.RoadPoint> roadPoints, Vector2 vertex, float baseHeight) =>
        Written(CalculateBlendedHeight(roadPoints, vertex, baseHeight), vertex, baseHeight);

    /// <summary><see cref="WrittenAt(List{RoadSpatialGrid.RoadPoint}, Vector2, float)"/> from the points of
    /// <paramref name="roadPoints"/> (laid out once: <see cref="FitPoints"/>, no fill spread) at the first
    /// <paramref name="count"/> indices of <paramref name="only"/>, in that order: the same height to the bit as from a list of
    /// those points.</summary>
    internal static float WrittenAt(FitPoints roadPoints, int[] only, int count, Vector2 vertex, float baseHeight) =>
        Written(BlendedHeight(roadPoints, vertex, baseHeight, false, only, count), vertex, baseHeight);

    /// <summary>
    /// A road's surface at <paramref name="toward"/> as this writer fits it, read along the road from its point at
    /// <paramref name="at"/>: the fit there (<see cref="BlendedHeight"/>: the line along the road through the points of
    /// <paramref name="roadPoints"/> at the first <paramref name="count"/> indices of <paramref name="only"/>, read at
    /// <paramref name="at"/>), carried along that line's direction and gradient to <paramref name="toward"/>'s place along it,
    /// level across it (the writer's surface is level across a road). The batter is left out (no ground: the road's surface,
    /// not its earthwork's release). Read at the road's own point, where its neighbours reach with their whole weight, so a
    /// position at the edge of the band two roads mix over reads that road's surface, not the thin fringe of its blend.
    /// The one fit of a met road's height (RoadSpatialGrid.SurfaceReader).
    /// </summary>
    internal static float SurfaceAlong(FitPoints roadPoints, int[] only, int count, Vector2 at, Vector2 toward)
    {
        BlendResult blend = BlendedHeight(roadPoints, at, float.NegativeInfinity, false, only, count);
        double along = blend.LineX * (toward.x - at.x) + blend.LineY * (toward.y - at.y);
        return (float)(blend.TargetHeight + blend.Gradient * along);
    }

    private static float Written(BlendResult blend, Vector2 vertex, float baseHeight) =>
        blend.InfluencingPoints == 0 ? baseHeight : RoadTarget(blend, vertex, baseHeight);

    private struct BlendResult
    {
        public float TargetHeight;
        public float MaxBlend;
        /// <summary>The side slope's width (<see cref="RoadProfile.FaceWidth"/>) of the point that pulls hardest.</summary>
        public float FaceWidth;
        public int InfluencingPoints;
        /// <summary>The line along the road the height was read off (<see cref="LineOf"/>): its direction and gradient.</summary>
        public double LineX, LineY, Gradient;
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
    /// <see cref="BatterMaxExtra"/>. The one owner of what the terrain writer does (docs/OWNERS.md).</summary>
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
    /// zone gather: <see cref="FitReach(float, float)"/> with the widest fill spread.</summary>
    internal static float GatherRadius(float roadWidth) =>
        FitReach(roadWidth, RoadEarthworkNoise.FillSpread > 0f ? RoadEarthworkNoise.FillSpreadMax : 0f);

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
    /// Past a road's end (every point that reaches the vertex on one side of it along the road) the line's direction and
    /// gradient are the road's own last stretch: every point within the reach any point's blend can have
    /// (<see cref="Reaches"/>, the cull's reach), weighted alike. Only the end point's blend reaches far past an end: 3 m past
    /// an end on a 50 % plane, with points a metre apart, the fit had that one point and read no gradient, the terrain
    /// stood 0.75 m off the plane (half its 1.5 m at the blend there). The level along that line is still the
    /// blend-weighted one. An append rewrites every vertex its points reach so (<see cref="ModifyVertexHeights"/>'s
    /// incremental write), as a full write would. Mid-road, points on both sides, the fit is the blend-weighted line as
    /// before. Inside a location's footprint (bounded) the stretch is the points that reach it.
    ///
    /// The sums gathered below are the ones a plane fit would need, but they
    /// are used to find the road's direction, not to fit a plane: the height
    /// is regressed along that direction alone and the gradient across the
    /// road is zero by construction. See FitHeightAtVertex, which explains why
    /// a free plane fit was tried and abandoned.
    /// </summary>
    /// <param name="bounded">Inside a location's footprint: each point reaches its half width and
    /// <see cref="RoadLocationEntry.InsideMargin"/>, no further (no batter, noise or fill spread),
    /// so a road onto a location's landing never levels its entrance or its rocks.</param>
    private static BlendResult CalculateBlendedHeight(List<RoadSpatialGrid.RoadPoint> roadPoints, Vector2 vertexPos,
        float groundHere, float[]? fillExtra = null, bool bounded = false) =>
        BlendedHeight((t_fit ??= new FitPoints()).Set(roadPoints, fillExtra), vertexPos, groundHere, bounded);

    /// <summary><see cref="CalculateBlendedHeight"/> over road points laid out once (<see cref="FitPoints"/>): the one fit.</summary>
    /// <param name="only">Where given, the fit reads only these of the points (their indices, the first <paramref name="onlyCount"/>), in
    /// this order: a caller choosing the points near each vertex of one set laid out once.</param>
    private static BlendResult BlendedHeight(FitPoints roadPoints, Vector2 vertexPos, float groundHere, bool bounded = false,
        int[]? only = null, int onlyCount = 0)
    {
        // Weighted sums for the fit h = a + b*dx + c*dy, (dx, dy) relative to
        // the vertex, so a is the surface height at the vertex.
        var level = new FitSums();
        // The road's own last stretch, every point alike: the line past an end.
        var stretch = new FitSums();
        float maxBlend = 0f, faceWidth = 0f;
        int influencingPoints = 0;
        // The widest margin any point can have, past its half width and fill spread: the noise at its widest and the
        // widest batter. A point farther than that is passed over before its noise (a Perlin sample) and batter are worked
        // out; the rest are judged exactly as before.
        bool cull = !bounded && CullFarPoints;
        // One pass over the points: the reach is judged once per point (Reaches: each point's reach worked out once per set,
        // FitPoints.Far).
        float[] px = roadPoints.X, py = roadPoints.Y, pw = roadPoints.W, ph = roadPoints.H, far = roadPoints.Far;
        bool[] levelled = roadPoints.Levelled;
        float[]? fillExtra = roadPoints.Fill;
        double[] reachX = roadPoints.ReachX, reachY = roadPoints.ReachY;
        int reached = 0;
        int count = only != null ? onlyCount : roadPoints.Count;

        for (int j = 0; j < count; j++)
        {
            int k = only != null ? only[j] : j;
            // A paint-only point (a waded ford) leaves the ground as it is; a
            // staircase's cut is applied on its own, after the road surface. A mound is levelled as road.
            if (!levelled[k])
                continue;
            // (rp.p - vertexPos).sqrMagnitude, each part rounded where the vector and its square stored them.
            float ox = px[k] - vertexPos.x, oy = py[k] - vertexPos.y;
            float distSq = ox * ox + oy * oy;
            if (!bounded)
            {
                float reach = far[k];
                bool reaches = distSq < reach * reach;
                // The road's own last stretch: every point within the reach any point's blend can have, every one alike,
                // whether or not the far points are passed over (the cull changes no vertex). Inside a location: the points
                // that reach it (below).
                if (reaches)
                    stretch.Add(1.0, px[k] - vertexPos.x, py[k] - vertexPos.y, ph[k]);
                else if (cull)
                    continue;
            }
            // A road point standing well below the ground gets a wider
            // release, so the cut is walked out on a slope instead of ending
            // in a wall. The ground under the vertex stands in for the ground
            // under the point: they are metres apart, and this costs no query.
            float margin = bounded ? RoadLocationEntry.InsideMargin
                         : RoadConstants.TerrainBlendMargin * roadPoints.MarginFactor(k)
                         + BatterExtra(BatterDivergence(ph[k], groundHere))
                         + (fillExtra != null ? fillExtra[k] : 0f);
            float influenceRadius = (pw[k] * 0.5f) + margin;
            float influenceRadiusSq = influenceRadius * influenceRadius;

            if (distSq < influenceRadiusSq)
            {
                float dist = Mathf.Sqrt(distSq);
                float pointBlend = RoadProfile.LevelBlend(dist, pw[k], margin);
                if (pointBlend <= 0f)
                    continue;
                double weight = pointBlend * pointBlend;
                double dx = px[k] - vertexPos.x;
                double dy = py[k] - vertexPos.y;
                level.Add(weight, dx, dy, ph[k]);
                if (bounded) stretch.Add(1.0, dx, dy, ph[k]);
                reachX[reached] = dx; reachY[reached] = dy; reached++;
                influencingPoints++;

                if (pointBlend > maxBlend)
                {
                    maxBlend = pointBlend;
                    faceWidth = RoadProfile.FaceWidth(pw[k], margin);
                }
            }
        }

        // Past the road's end: every point reaching the vertex lies on one side of it along the road's own stretch.
        bool pastEnd = false;
        if (influencingPoints > 0)
        {
            LineOf(stretch, out double lx, out double ly, out _);
            double before = double.PositiveInfinity, after = double.NegativeInfinity;
            for (int n = 0; n < reached; n++)
            {
                double along = lx * reachX[n] + ly * reachY[n];
                if (along < before) before = along;
                if (along > after) after = along;
            }
            pastEnd = before > 0.0 || after < 0.0;
        }
        double ux = 1, uy = 0, gradient = 0;
        float target = influencingPoints > 0 && level.W > 0 ? FitHeightAtVertex(level, pastEnd ? stretch : level, out ux, out uy, out gradient) : 0f;
        return new BlendResult
        {
            TargetHeight = target,
            MaxBlend = maxBlend,
            FaceWidth = faceWidth,
            InfluencingPoints = influencingPoints,
            LineX = ux, LineY = uy, Gradient = gradient
        };
    }

    /// <summary>
    /// Road points as the fit reads them (<see cref="BlendedHeight"/>), laid out flat once per set: each point's position,
    /// width, height, whether it is levelled, its fill spread, its reach (<see cref="Reaches"/>) and, once first asked, its
    /// noise's margin factor (<see cref="RoadEarthworkNoise.MarginFactor"/>). Under the game's Mono a list of road points
    /// read point by point copies each whole point, and the margin factor is a noise sample per point and vertex; read
    /// flat, and once per point for a set the writer fits at every vertex of a zone, the fit is the same to the bit.
    /// One per set of points, or a thread's own (<see cref="t_fit"/>) for a set fitted once.
    /// </summary>
    internal sealed class FitPoints
    {
        internal int Count;
        internal float[] X = new float[0], Y = new float[0], W = new float[0], H = new float[0], Far = new float[0];
        internal bool[] Levelled = new bool[0];
        internal float[]? Fill;
        /// <summary>Room for the offsets of the points reaching a vertex (<see cref="BlendedHeight"/>).</summary>
        internal double[] ReachX = new double[0], ReachY = new double[0];
        private float[] m_margin = new float[0];
        private bool[] m_marginKnown = new bool[0];

        internal static FitPoints Of(List<RoadSpatialGrid.RoadPoint> points, float[]? fill) => new FitPoints().Set(points, fill);

        internal FitPoints Set(List<RoadSpatialGrid.RoadPoint> points, float[]? fill)
        {
            int n = points.Count;
            if (X.Length < n)
            {
                int size = System.Math.Max(n, 2 * X.Length);
                X = new float[size]; Y = new float[size]; W = new float[size]; H = new float[size]; Far = new float[size];
                Levelled = new bool[size]; ReachX = new double[size]; ReachY = new double[size];
                m_margin = new float[size]; m_marginKnown = new bool[size];
            }
            float widest = WidestMargin();
            for (int k = 0; k < n; k++)
            {
                RoadSpatialGrid.RoadPoint rp = points[k];
                X[k] = rp.p.x; Y[k] = rp.p.y; W[k] = rp.w; H[k] = rp.h;
                Levelled[k] = rp.Levelled;
                Far[k] = FitReach(rp.w, fill != null ? fill[k] : 0f, widest);
                m_marginKnown[k] = false;
            }
            Count = n;
            Fill = fill;
            return this;
        }

        /// <summary>No points, the room kept: a set laid out a point at a time (<see cref="Add"/>).</summary>
        internal FitPoints Clear() { Count = 0; Fill = null; return this; }

        /// <summary>Lays out one more point, with no fill spread, as <see cref="Set"/> lays out each (the same fit to the bit):
        /// a set gathered as it is read (RoadSpatialGrid.SurfaceReader). Its index.</summary>
        internal int Add(in RoadSpatialGrid.RoadPoint rp)
        {
            int k = Count;
            if (X.Length <= k)
            {
                int size = System.Math.Max(16, 2 * X.Length);
                System.Array.Resize(ref X, size); System.Array.Resize(ref Y, size); System.Array.Resize(ref W, size);
                System.Array.Resize(ref H, size); System.Array.Resize(ref Far, size); System.Array.Resize(ref Levelled, size);
                System.Array.Resize(ref ReachX, size); System.Array.Resize(ref ReachY, size);
                System.Array.Resize(ref m_margin, size); System.Array.Resize(ref m_marginKnown, size);
            }
            X[k] = rp.p.x; Y[k] = rp.p.y; W[k] = rp.w; H[k] = rp.h;
            Levelled[k] = rp.Levelled;
            Far[k] = FitReach(rp.w, 0f, WidestMargin());
            m_marginKnown[k] = false;
            Count = k + 1;
            return k;
        }

        /// <summary>The point's <see cref="RoadEarthworkNoise.MarginFactor"/>, worked out the first time it is asked.</summary>
        internal float MarginFactor(int k)
        {
            if (!m_marginKnown[k])
            {
                m_margin[k] = RoadEarthworkNoise.MarginFactor(new Vector2(X[k], Y[k]));
                m_marginKnown[k] = true;
            }
            return m_margin[k];
        }
    }

    /// <summary>A thread's own <see cref="FitPoints"/>, for a set of points fitted once (<see cref="CalculateBlendedHeight"/>),
    /// so the hot path allocates nothing.</summary>
    [System.ThreadStatic] private static FitPoints? t_fit;

    /// <summary>A thread's own reader of a road's height as it will stand (<see cref="RoadHeightNear"/>).</summary>
    [System.ThreadStatic] private static RoadSpatialGrid.SurfaceReader? t_storedSurface;

    /// <summary>Whether any road point of <paramref name="points"/> may reach the vertex at <paramref name="vertex"/>
    /// (<see cref="Reaches"/>): the vertices an incremental write rewrites, every one whose fit those points enter
    /// (<see cref="CalculateBlendedHeight"/>, the road's last stretch past an end).</summary>
    private static bool AnyReaches(List<RoadSpatialGrid.RoadPoint> points, Vector2 vertex, float[]? fill)
    {
        float widest = WidestMargin();
        for (int k = 0; k < points.Count; k++)
            if (points[k].Levelled && Reaches(points[k], (points[k].p - vertex).sqrMagnitude, fill != null ? fill[k] : 0f, widest)) return true;
        return false;
    }

    /// <summary>The widest margin any road point can have past its half width and fill spread: the noise at its widest and
    /// the widest batter (<see cref="Reaches"/>).</summary>
    private static float WidestMargin() =>
        RoadConstants.TerrainBlendMargin * (1f + Mathf.Abs(RoadEarthworkNoise.WidthVariation)) + Mathf.Max(0f, BatterMaxExtra);

    /// <summary>How far from a vertex a road point <paramref name="width"/> wide with <paramref name="fill"/> spread may enter its fit
    /// (<see cref="Reaches"/>, as <see cref="WrittenAt"/> fits it): a point farther off is left out of it.</summary>
    internal static float FitReach(float width, float fill = 0f) => FitReach(width, fill, WidestMargin());

    /// <summary><see cref="FitReach(float, float)"/> with the widest margin (<see cref="WidestMargin"/>) already worked out,
    /// for a caller judging many points at once.</summary>
    private static float FitReach(float width, float fill, float widest) => (width * 0.5f + widest + Mathf.Max(0f, fill)) * 1.001f + 0.01f;

    /// <summary>Whether road point <paramref name="rp"/>, <paramref name="distSq"/> from a vertex squared, may reach it at all
    /// (<see cref="CullFarPoints"/>): within its half width, the widest margin any point can have (<paramref name="widest"/>:
    /// <see cref="WidestMargin"/>, worked out once by the caller) and its fill spread (<paramref name="fill"/>), with a little
    /// room for rounding (<see cref="FitReach(float, float)"/>).</summary>
    private static bool Reaches(in RoadSpatialGrid.RoadPoint rp, float distSq, float fill, float widest)
    {
        float far = FitReach(rp.w, fill, widest);
        return distSq < far * far;
    }


    /// <summary>Weighted sums of road points relative to a vertex, for <see cref="FitHeightAtVertex"/>.</summary>
    private struct FitSums
    {
        public double W, X, Y, XX, XY, YY, H, XH, YH;
        public void Add(double weight, double dx, double dy, double h)
        {
            W += weight;
            X += weight * dx;
            Y += weight * dy;
            XX += weight * dx * dx;
            XY += weight * dx * dy;
            YY += weight * dy * dy;
            H += weight * h;
            XH += weight * dx * h;
            YH += weight * dy * h;
        }
    }

    /// <summary>
    /// The fitted road surface height at the vertex: the line along the road through <paramref name="line"/>'s points (its
    /// direction and gradient, <see cref="LineOf"/>), read at the vertex through <paramref name="level"/>'s weighted mean
    /// (mid-road both are the blend-weighted points; past a road's end the line is the road's own last stretch,
    /// <see cref="CalculateBlendedHeight"/>).
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
    private static float FitHeightAtVertex(in FitSums level, in FitSums line, out double ux, out double uy, out double b)
    {
        LineOf(line, out ux, out uy, out b);
        double mx = level.X / level.W, my = level.Y / level.W, mh = level.H / level.W;
        double ms = ux * mx + uy * my; // mean offset along the road, from the vertex
        return (float)(mh - b * ms);
    }

    /// <summary>The line along the road through <paramref name="sums"/>' points (<see cref="FitHeightAtVertex"/>): its
    /// direction (<paramref name="ux"/>, <paramref name="uy"/>, the points' principal direction) and its gradient
    /// <paramref name="b"/> along it; any direction and no gradient where there are none.</summary>
    private static void LineOf(in FitSums sums, out double ux, out double uy, out double b)
    {
        ux = 1; uy = 0; b = 0;
        double sw = sums.W;
        if (!(sw > 0)) return;
        double mx = sums.X / sw, my = sums.Y / sw, mh = sums.H / sw;
        double cxx = sums.XX - sw * mx * mx;
        double cxy = sums.XY - sw * mx * my;
        double cyy = sums.YY - sw * my * my;
        double cxh = sums.XH - sw * mx * mh;
        double cyh = sums.YH - sw * my * mh;

        // Principal direction of the 2x2 weighted covariance (largest eigenvalue).
        double half = 0.5 * (cxx + cyy);
        double diff = 0.5 * (cxx - cyy);
        double root = System.Math.Sqrt(diff * diff + cxy * cxy);
        if (root >= 1e-12)
        {
            // Eigenvector of the largest eigenvalue half + root.
            ux = cxy;
            uy = (half + root) - cxx;
            if (System.Math.Abs(ux) + System.Math.Abs(uy) < 1e-12) { ux = 1; uy = 0; }
            double len = System.Math.Sqrt(ux * ux + uy * uy);
            ux /= len; uy /= len;
        }
        // else isotropic or a single point: any direction

        // Regress height on the offset s along that direction (centred sums project linearly).
        double css = ux * ux * cxx + 2 * ux * uy * cxy + uy * uy * cyy;
        double csh = ux * cxh + uy * cyh;
        double ridge = sw * RoadConstants.HeightFitRidgeMetres * RoadConstants.HeightFitRidgeMetres;
        b = csh / (css + ridge);
        double limit = RoadConstants.HeightFitMaxGradient;
        if (b > limit) b = limit; else if (b < -limit) b = -limit;
    }

    /// <summary>
    /// No paint on ground more than this many metres below the road surface.
    /// Paint reaches a set distance across the road however the ground falls
    /// away, so without it paint runs down a raised causeway's bank. Settable
    /// for tests; 0 turns it off.
    /// </summary>
    internal static float PaintBelow = 0.5f;

    /// <summary>
    /// No paint on a vertex whose ground stands more than this many metres above or below a vertex it shares a cell of the
    /// terrain mesh with. The game spreads a vertex's paint over the cells round it, so a painted vertex at the top or the
    /// foot of a face stretches the road's texture over the whole face: down a staircase's cut into the side of its road,
    /// or a high fill's side. The road's own surface never changes this much within a metre. Settable for tests; 0 turns
    /// it off.
    /// </summary>
    internal static float PaintFace = 1.5f;

    /// <summary>The ground height under a paint cell once levelled. With exact
    /// paint a cell is centred on its own vertex, as vanilla reads it.</summary>
    private static float LevelledCellHeight(TerrainContext context, int vx, int vy)
    {
        if (vx >= context.GridSize || vy >= context.GridSize) return float.NaN;
        int i = vy * context.GridSize + vx;
        return context.PreCompilerHeights[i] + context.HeightmapPosition.y
             + context.TerrainComp.m_levelDelta[i] + context.TerrainComp.m_smoothDelta[i];
    }

    /// <summary>Whether the vertex, at levelled height <paramref name="ground"/>, is on a face: a vertex it shares a mesh
    /// cell with stands more than <see cref="PaintFace"/> off it.</summary>
    private static bool OnAFace(TerrainContext context, int vx, int vy, float ground, System.Func<int, int, float>? shared = null)
    {
        if (!(PaintFace > 0f)) return false;
        for (int y = shared != null ? vy - 1 : Mathf.Max(0, vy - 1); y <= (shared != null ? vy + 1 : Mathf.Min(context.GridSize - 1, vy + 1)); y++)
            for (int x = shared != null ? vx - 1 : Mathf.Max(0, vx - 1); x <= (shared != null ? vx + 1 : Mathf.Min(context.GridSize - 1, vx + 1)); x++)
                if (Mathf.Abs((shared != null ? shared(x, y) : LevelledCellHeight(context, x, y)) - ground) > PaintFace)
                    return true;
        return false;
    }

    /// <summary>Paint's ground facts on a shared border: the network's finished ground, not one zone's compiler or a
    /// neighbour list clipped at its edge. The road-only surface excludes cuts as the writer's RoadDeltas do. The seam
    /// uses the same kept ground and fade as the writer.</summary>
    private static System.Func<int, int, float> SharedPaintHeight(bool roadSurface, FinishedZones zones, KeptGround kept)
    {
        var world = WorldGenerator.instance;
        float Natural(int x, int z) => BiomeBlendedHeight.GetBlendedHeight(x, z, world);
        var finished = FinishedGround(Natural, roadSurface, zones);
        return (x, z) =>
        {
            var vertex = new Vector2(x, z);
            float seam = NetworkCutoff.SeamWeight(vertex, out Vector2s zone);
            float h = finished(x, z);
            if (seam >= 1f) return h;
            float baseHeight = BaseGround(vertex, Natural, out _);
            return baseHeight + Faded(kept.DeltaAt(zone, vertex) ?? 0f, seam, h - baseHeight);
        };
    }

    /// <summary>Paint the road into the compiler; returns how many of its texels were written. The one owner of the painted edge and footprint of a road (docs/OWNERS.md).</summary>
    /// <param name="roadDeltas">Per vertex, the level delta the road's own earthwork asks for
    /// (<see cref="ModificationStats.RoadDeltas"/>).</param>
    private static int ApplyRoadPaint(List<RoadSpatialGrid.RoadPoint> roadPoints, TerrainComp terrainComp,
        float[] roadDeltas, TerrainContext context)
    {
        Heightmap hmap = terrainComp.m_hmap;
        if (hmap == null)
            return 0;
        int gridSize = terrainComp.m_width + 1;
        // Every point is asked and each texel takes the strongest paint any of them gives it, then is painted once: the
        // road's cross-section (PaintStrength) out to its edge wherever the road is, whatever the points' spacing or
        // order. Painting a texel once per point blended the points' strengths together, and skipping points that fell
        // in an already painted 1.5 m cell left a texel on the fade only the paint of a point up to 1.5 m away, so the
        // edge faded early or not at all depending on which points the zone met first.
        var strongest = new float[gridSize * gridSize];
        Vector3 terrainPos = hmap.transform.position;
        float scale = hmap.m_scale;
        bool seamNear = NetworkCutoff.SeamNear(ZoneSystem.GetZone(terrainPos));
        System.Func<int, int, float>? sharedGround = null, sharedDeck = null, sharedAt = null;
        float Shared(System.Func<int, int, float> read, int x, int y) => read(
            Mathf.RoundToInt(terrainPos.x + (x - terrainComp.m_width / 2) * scale),
            Mathf.RoundToInt(terrainPos.z + (y - terrainComp.m_width / 2) * scale));

        foreach (RoadSpatialGrid.RoadPoint roadPoint in roadPoints)
        {
            if (roadPoint.cutOnly) continue; // stone pieces cover a staircase; its cut is not painted
            // The road a structure stands on past its stairs' foot, and a mound, are levelled, not painted.
            if (roadPoint.unpainted || roadPoint.mound) continue;
            Vector3 worldPos = new Vector3(roadPoint.p.x, 0f, roadPoint.p.y);
            Vector3 localPos = worldPos - terrainPos;
            int halfWidth = (terrainComp.m_width + 1) / 2;
            int centerX = Mathf.FloorToInt(localPos.x / scale + 0.5f) + halfWidth;
            int centerY = Mathf.FloorToInt(localPos.z / scale + 0.5f) + halfWidth;
            // Paint is held against the road's levelled surface under the road
            // point, not the point's stored height: where two roads meet the
            // levelling blends them, and the higher road's surface sits below
            // its own stored height, which would break its paint at the join.
            // The road's surface, not the ground left there: a staircase beside
            // its road cuts into the road's side, and held to the cut's floor
            // the paint ran up the face from it.
            float deck = roadPoint.h;
            if (PaintBelow > 0f && centerX >= 0 && centerY >= 0 && centerX < gridSize && centerY < gridSize)
            {
                int centre = centerY * gridSize + centerX;
                deck = LevelledCellHeight(context, centerX, centerY);
                if (!float.IsNaN(roadDeltas[centre]))
                    deck += roadDeltas[centre] - terrainComp.m_levelDelta[centre];
            }
            
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
                    // The cheap tests first, the footprint and ground reads only for a texel this point can still raise:
                    // every texel keeps the strongest paint any point gives it, so the order changes nothing painted.
                    // From the road point itself, not its snapped vertex; the cell is centred on its vertex
                    // the way vanilla reads a paint cell (Heightmap.WorldToVertexMask).
                    float distMetres = Vector2.Distance(paintPosition, roadPoint.p);
                    // The game's grass test reads the same texel for the cell half a cell toward +x/+z
                    // (Heightmap.IsCleared), so the texel takes the stronger of the profile at its vertex and at
                    // that cell's middle: grass is kept off the painted road on its -x/-z side too.
                    float grassMetres = Vector2.Distance(paintPosition + new Vector2(0.5f * scale, 0.5f * scale), roadPoint.p);
                    if (Mathf.Min(distMetres, grassMetres) > radius)
                        continue;

                    // Paint follows the shared cross-section profile: solid
                    // in the core, fading out strictly inside the leveled
                    // footprint (unpainted verge).
                    float blendFactor = Mathf.Max(RoadProfile.PaintStrength(distMetres, roadPoint.w), RoadProfile.PaintStrength(grassMetres, roadPoint.w))
                        * (seamNear ? NetworkCutoff.SeamWeight(paintPosition) : 1f);
                    if (blendFactor <= 0f)
                        continue;
                    int index = vy * gridSize + vx;
                    if (blendFactor <= strongest[index])
                        continue;
                    // Inside a footprint only a road into that location paints, where no other location's footprint lies.
                    if (RoadLocationProtection.Contains(paintPosition)
                        && !(roadPoint.intoLocation && RoadLocationProtection.Admits(paintPosition, roadPoint.location))) continue;
                    bool border = vx == 0 || vy == 0 || vx == gridSize - 1 || vy == gridSize - 1;
                    System.Func<int, int, float>? shared = null;
                    float surface = deck;
                    if (border)
                    {
                        if (sharedGround == null)
                        {
                            // One read of the network's zones and the kept ground for both: the same facts either way.
                            var zones = new FinishedZones();
                            var kept = new KeptGround();
                            sharedGround = SharedPaintHeight(roadSurface: false, zones, kept);
                            sharedDeck = SharedPaintHeight(roadSurface: true, zones, kept);
                            sharedAt = (x, y) => Shared(sharedGround, x, y);
                        }
                        shared = sharedAt;
                        surface = Shared(sharedDeck!, centerX, centerY);
                    }
                    float ground = shared != null ? shared(vx, vy) : LevelledCellHeight(context, vx, vy);
                    if (PaintBelow > 0f && surface - ground > PaintBelow)
                        continue;
                    if (OnAFace(context, vx, vy, ground, shared))
                        continue;

                    if (blendFactor > strongest[index]) strongest[index] = blendFactor;
                }
            }
        }
        int painted = 0;
        for (int index = 0; index < strongest.Length; index++)
        {
            float blendFactor = strongest[index];
            if (!(blendFactor > 0f)) continue;
            Color currentColor = terrainComp.m_paintMask[index];
            float alpha = currentColor.a;
            Color newColor = Color.Lerp(currentColor, Heightmap.m_paintMaskPaved, blendFactor);
            newColor.a = alpha;
            terrainComp.m_modifiedPaint[index] = true;
            terrainComp.m_paintMask[index] = newColor;
            painted++;
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
        else
        {
            // Nothing to write is the writer's verdict, not a write lost on its way: the zone's road terrain is final for
            // this network, so it is stamped with nothing saved (AppliedVersionHash) and no load, sweep or bake writes it
            // again until the network changes. The caller (ApplyPendingTerrain) writes only a compiler this peer owns.
            // Unstamped, a zone the roads only brush was written again on every load, and a staircase's cuts on ground
            // already low enough every 2 s.
            Stamp(context.TerrainComp);
            LastWriteOutcome = WriteOutcome.NothingToWrite;
            // A point just outside the zone reaches into it only faintly, so a
            // zone whose points all lie outside it can have nothing to change.
            // Only a point INSIDE the zone that changed nothing is suspicious
            // (a staircase's cut on ground already low enough is not).
            if (!onlyCuts && AnyPointInside(zoneID, roadPoints))
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning(
                    $"Zone {zoneID}: {roadPoints.Count} road points but 0 vertices matched! Coordinate mismatch?");
            else if (!onlyCuts)
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
