using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Logging;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ProceduralRoads;

/// <summary>
/// Road terrain for players who do not have the mod. The server writes it
/// into the game's own terrain compilers, so every client, modded or not,
/// receives the roads as the same kind of data a player's own digging makes.
///
/// A zone's height and paint changes live in its terrain compiler, one
/// _TerrainCompiler ZDO per zone. A client applies them to the terrain it
/// generates itself and reloads them whenever the ZDO changes; that is
/// vanilla code, so a client without the mod needs nothing more. What it
/// cannot do is write them: without the mod nobody on that client knows where
/// the roads are, and a dedicated server keeps only the zones around its own
/// reference position loaded. Every other zone a road touches has to be
/// written here, by the server, as ZDO data.
///
/// Two paths, both writing through RoadTerrainModifier (the compiler's Awake
/// postfix, OnTerrainCompilerReady, is the one writer):
///
///   - A zone generated for a remote peer ("ghost" generation) gets its
///     compiler while the game still has the zone's heightmap in hand: the
///     PlaceVegetation postfix calls OnGhostZoneGenerated.
///   - Zones generated earlier without roads -- before the mod was installed,
///     before this network existed, or by ghost generation on a build that did
///     not write it -- are written from a queue, in bounded slices of each
///     frame, against a temporary copy of the zone's terrain: Tick.
///
/// A zone whose compiler is stamped with the current network version is left
/// alone, so a restart writes nothing twice and later player edits survive.
/// Development switches: PROCEDURALROADS_SERVER_BAKE (default on) and
/// PROCEDURALROADS_PEER_PROBE (default off; logs where each connected player
/// stands against the road and the ground).
/// </summary>
public static class ServerTerrainBake
{
    private static ManualLogSource Log => ProceduralRoadsPlugin.ProceduralRoadsLogger;

    private static readonly int TerrainCompilerPrefabHash = "_TerrainCompiler".GetStableHashCode();

    /// <summary>Main-thread time the queue may use per frame.</summary>
    private const double FrameBudgetMs = 8.0;
    /// <summary>Zones whose terrain the game is still building that one frame may pass over.</summary>
    private const int NotReadyPerFrame = 8;
    /// <summary>A zone whose terrain has not been built after this many tries is given up.</summary>
    private const int MaxNotReadyTries = 2000;
    private const float WaitForOwnerSeconds = 10f;
    private const float ProgressEverySeconds = 10f;
    private const float ProbeEverySeconds = 5f;
    private const float GhostLogEverySeconds = 30f;

    private static bool? s_enabled;
    private static bool? s_probe;
    private static bool Enabled => s_enabled ??= DebugSwitches.Flag("SERVER_BAKE", true);
    private static bool Probe => s_probe ??= DebugSwitches.Flag("PEER_PROBE", false);

    /// <summary>
    /// Validation switches: make the first N ghost writes, or the first N
    /// writes on temporary terrain, report failure without writing anything.
    /// They exist to prove in a running game what the tests prove in
    /// isolation -- a ghost failure that is repaired, and a repair that spends
    /// its attempts and is given up. Both default to 0, so an unset
    /// environment behaves exactly as before, and the counters are NOT reset
    /// with the queue: "the first N this session" stays the first N even if
    /// the network is regenerated.
    /// </summary>
    private static int? s_failGhostWrites;
    private static int? s_failZoneWrites;
    private static int FailGhostWrites => s_failGhostWrites ??= DebugSwitches.Count("FAIL_GHOST_WRITES", 0);
    private static int FailZoneWrites => s_failZoneWrites ??= DebugSwitches.Count("FAIL_ZONE_WRITES", 0);
    private static int s_ghostWritesFailed;
    private static int s_zoneWritesFailed;

    /// <summary>
    /// Validation switch: PROCEDURALROADS_FAULT_ZONE_ONCE=x,z throws an
    /// exception the FIRST time that zone's queued work runs, and never again,
    /// to prove in a running game that an exception stops one zone, that the
    /// rest of the queue and generation-time writes carry on, and that
    /// road_bake again brings the zone back. Unset by default.
    /// </summary>
    private static bool? s_faultZoneOnceSet;
    private static Vector2s s_faultZoneOnce;
    private static bool s_faultZoneOnceFired;
    private static bool FaultZoneOnce(Vector2s zone)
    {
        if (s_faultZoneOnceSet == null)
        {
            s_faultZoneOnceSet = DebugSwitches.Zone("FAULT_ZONE_ONCE", out int x, out int z);
            s_faultZoneOnce = new Vector2s((short)x, (short)z);
        }
        if (s_faultZoneOnceSet != true || s_faultZoneOnceFired || !zone.Equals(s_faultZoneOnce))
            return false;
        s_faultZoneOnceFired = true;
        return true;
    }

    private static int s_version;
    private static readonly List<Vector2s> s_queue = new();
    private static int s_next;
    private static readonly Dictionary<Vector2s, int> s_notReady = new();
    private static readonly Stopwatch s_wall = new();
    private static double s_busyMs;
    private static float s_nextProgress;
    private static float s_nextRepairCheck;
    /// <summary>
    /// The one owner of "when does this zone come round again, and how many
    /// more tries is it worth": deferrals and repair budgets both. A second
    /// scheduler is how a zone came back after it had been given up.
    /// </summary>
    private static readonly GhostRepairLedger s_ghostRepair = new();
    private static readonly ServerBakeFaults s_faults = new();
    private static float s_nextProbe;
    private static bool s_finished;
    private static int s_loggedGhost;
    private static float s_nextGhostLog;
    private static Counts s_counts;

    private struct Counts
    {
        public int Zones, Created, Rewritten, LiveWritten, Current, Ungenerated, Loaded, Duplicates, Failed, BridgeZones;
        public int NothingToWrite, VegetationZones, VegetationRemoved, GhostRocks, GhostSettled;
        public int GhostCreated, GhostRewritten, GhostFailed;
        public long Bytes;
        public double GhostMs;
    }

    private enum Outcome { Done, TerrainNotReady, Waiting }

    private enum WriteResult { Written, NothingToWrite, Failed }

    /// <summary>Prefab hashes of the game's own vegetation (ZoneSystem.m_vegetation), per world.</summary>
    private static HashSet<int>? s_vegetationPrefabs;

    private static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer();

    /// <summary>Forget the queue: the world is going away.</summary>
    public static void Reset()
    {
        s_version = 0;
        s_queue.Clear();
        s_next = 0;
        s_notReady.Clear();
        s_wall.Reset();
        s_busyMs = 0;
        s_finished = false;
        s_counts = default;
        s_vegetationPrefabs = null;
        s_ghostRepair.Clear();
        s_faults.Clear();
    }

    /// <summary>
    /// road_bake zone: what the server knows about one zone and what it would
    /// do about it now. Answers from the save, so it works for zones nobody
    /// has loaded.
    /// </summary>
    public static string DescribeZone(Vector3 point)
    {
        if (ZoneSystem.instance == null || ZDOMan.instance == null)
            return "No world loaded";
        Vector2s zone = ZoneSystem.GetZone(point);
        bool generated = ZoneSystem.instance.IsZoneGenerated(zone);
        bool loadedHere = ZoneSystem.instance.m_zones.ContainsKey(zone);
        List<RoadSpatialGrid.RoadPoint> roadPoints = RoadSpatialGrid.GetRoadPointsInZone(zone);
        int points = roadPoints.Count;
        // The road point nearest the zone centre, as somewhere to stand.
        string sample = "";
        Vector3 centre = ZoneSystem.GetZonePos(zone);
        float best = float.MaxValue;
        foreach (RoadSpatialGrid.RoadPoint rp in roadPoints)
        {
            float d2 = (rp.p - new Vector2(centre.x, centre.z)).sqrMagnitude;
            if (d2 < best)
            {
                best = d2;
                sample = $" (nearest the centre: {rp.p.x:F1} {rp.p.y:F1} at h {rp.h:F2}, {rp.w:F0} m wide{(rp.paintOnly ? ", paint only" : "")})";
            }
        }
        int pieces = BridgePlans.PlannedPieceCount(zone);
        List<ZDO> saved = FindSavedCompilers(zone);
        List<ServerBakePlanner.Compiler> facts = Describe(saved, zone);
        var described = new List<string>();
        long me = ZDOMan.GetSessionID();
        foreach (ServerBakePlanner.Compiler c in facts)
        {
            string owner = c.Owner == 0 ? "nobody" : c.Owner == me ? "this peer" : c.OwnerActiveHere ? "a peer in the zone" : "a peer elsewhere";
            string current = c.AppliedVersion != 0 && (c.AppliedVersion == RoadSpatialGrid.RoadNetworkVersion || c.UnchangedByAppends) ? " (current)" : "";
            described.Add($"stamp {c.AppliedVersion}{current}, owned by {owner}");
        }
        string next = points == 0
            ? "nothing to write (no road terrain)"
            : ServerBakePlanner.Decide(RoadSpatialGrid.RoadNetworkVersion, generated, loadedHere, facts, me).ToString();
        if (s_faults.IsQuarantined(zone))
            next = "stopped after a zone exception; fix the cause, then road_bake again";
        else if (s_faults.IsPaused)
            next = "queue paused after an error; fix the cause, then road_bake again";
        return $"Zone {zone}: generated {generated}, loaded here {loadedHere}, {points} road points{sample}, " +
               $"{pieces} bridge pieces planned{(BridgePlans.IsSpawned(zone) ? " (spawned)" : "")}; " +
               $"vegetation {(VegetationClearing.IsCleared(zone, RoadSpatialGrid.RoadNetworkVersion) ? "matches the roads" : "not cleared yet")}, " +
               $"{CountVegetationOnRoad(zone)} vegetation objects inside its road's clear areas; " +
               $"{saved.Count} terrain compiler(s){(described.Count > 0 ? ": " + string.Join("; ", described) : "")}; " +
               $"next: {next}";
    }

    /// <summary>
    /// road_bake server: what the SERVER's own zone machinery is doing, asked
    /// of the running game instead of inferred from the game's IL.
    ///
    /// A dedicated server reported `loaded here False` for all 49 zones within
    /// three of the origin, which contradicted a reading of ZoneSystem.Update: it calls
    /// CreateLocalZones(ZNet.GetReferencePosition()), GetReferencePosition
    /// returns m_referencePosition, and ZNet's .ctor pins that at Vector3.zero
    /// while only local-player code ever moves it. Every link in that chain is
    /// an inference, and one of them is wrong. This reports the facts.
    ///
    /// What turns on it: `loadedHere` is m_zones.ContainsKey(zone), and the
    /// planner picks WriteLiveCompiler ONLY when that is true. If a dedicated
    /// server never holds a live zone, WriteLiveCompiler and LeaveToLiveZone
    /// are unreachable there, which is a fact about the mod worth knowing
    /// rather than a test that keeps failing to be set up.
    /// </summary>
    public static List<string> DescribeServer()
    {
        var lines = new List<string>();
        if (ZNet.instance == null || ZoneSystem.instance == null)
        {
            lines.Add("No world loaded");
            return lines;
        }
        lines.Add($"server {ZNet.instance.IsServer()}, connection status {ZNet.GetConnectionStatus()} " +
                  "(ZoneSystem.Update returns at once unless this is Connected)");
        lines.Add($"locations generated {ZoneSystem.instance.LocationsGenerated} " +
                  "(on a server Update returns BEFORE CreateLocalZones while this is false)");
        Vector3 rp = ZNet.instance.GetReferencePosition();
        lines.Add($"reference position {rp.x:F1},{rp.y:F1},{rp.z:F1} -> zone {ZoneSystem.GetZone(rp)} " +
                  "(CreateLocalZones sweeps outwards from here)");
        lines.Add($"zones live on the server: {ZoneSystem.instance.m_zones.Count}");
        // Aggregate over the WHOLE collection, separately from the capped
        // listing below. Whether ANY live zone carries road points is the only
        // thing that decides whether WriteLiveCompiler can run at all --
        // ProcessTerrain returns at GetRoadPointsInZone(zone).Count == 0 before
        // the planner is consulted -- and a capped listing cannot carry that
        // claim, so the count is made over every live zone here.
        int roadBearing = 0;
        int minX = 0, maxX = 0, minY = 0, maxY = 0;
        bool first = true;
        foreach (var kv in ZoneSystem.instance.m_zones)
        {
            if (RoadSpatialGrid.GetRoadPointsInZone(kv.Key).Count > 0)
                roadBearing++;
            int zx = kv.Key.x;
            int zy = kv.Key.y;
            if (first)
            {
                minX = maxX = zx;
                minY = maxY = zy;
                first = false;
            }
            else
            {
                if (zx < minX) minX = zx;
                if (zx > maxX) maxX = zx;
                if (zy < minY) minY = zy;
                if (zy > maxY) maxY = zy;
            }
        }
        lines.Add($"live zones carrying road points: {roadBearing} of {ZoneSystem.instance.m_zones.Count} " +
                  "(counted over the WHOLE collection, not the sample below)");
        if (!first)
            lines.Add($"live zone index bounds: x {minX}..{maxX}, y {minY}..{maxY} (whole collection)");
        int shown = 0;
        foreach (var kv in ZoneSystem.instance.m_zones)
        {
            if (shown++ >= 10)
            {
                lines.Add("  ... (first 10 shown)");
                break;
            }
            lines.Add($"  live zone {kv.Key}: {RoadSpatialGrid.GetRoadPointsInZone(kv.Key).Count} road points, " +
                      $"generated {ZoneSystem.instance.IsZoneGenerated(kv.Key)}");
        }
        foreach (ZNetPeer peer in ZNet.instance.GetPeers())
        {
            Vector3 p = peer.GetRefPos();
            lines.Add($"peer {peer.m_uid} at {p.x:F1},{p.y:F1},{p.z:F1} -> zone {ZoneSystem.GetZone(p)} " +
                      "(peers drive CreateGhostZones, NOT CreateLocalZones)");
        }
        return lines;
    }

    /// <summary>
    /// road_bake find: the road zones nobody has generated yet nearest a
    /// point, each with a road point to stand on (DescribeZone).
    /// </summary>
    public static List<string> FindUngenerated(Vector3 point, float radius, int max)
    {
        var lines = new List<string>();
        if (ZoneSystem.instance == null || !RoadNetworkGenerator.RoadsAvailable)
        {
            lines.Add("No road network");
            return lines;
        }
        var found = new List<KeyValuePair<float, Vector2s>>();
        foreach (Vector2s zone in RoadSpatialGrid.GetZonesWithRoadPoints())
        {
            Vector3 centre = ZoneSystem.GetZonePos(zone);
            float distance = new Vector2(centre.x - point.x, centre.z - point.z).magnitude;
            if (distance <= radius && !ZoneSystem.instance.IsZoneGenerated(zone))
                found.Add(new KeyValuePair<float, Vector2s>(distance, zone));
        }
        found.Sort((a, b) => a.Key.CompareTo(b.Key));
        lines.Add($"{found.Count} ungenerated road zones within {radius:F0} m of ({point.x:F0}, {point.z:F0})");
        for (int i = 0; i < found.Count && i < max; i++)
            lines.Add($"  {found[i].Key:F0} m: {DescribeZone(ZoneSystem.GetZonePos(found[i].Value))}");
        return lines;
    }

    /// <summary>
    /// road_bake vegetation: the generated road zones that still hold the
    /// game's vegetation inside their road's clear areas, most first, each with
    /// a road point to stand on -- what the next clearing pass would take.
    /// </summary>
    public static List<string> FindVegetationOnRoads(int max)
    {
        var lines = new List<string>();
        if (ZoneSystem.instance == null || ZDOMan.instance == null || !RoadNetworkGenerator.RoadsAvailable)
        {
            lines.Add("No road network");
            return lines;
        }
        HashSet<Vector2s> zones = RoadSpatialGrid.GetZonesWithRoadPoints();
        foreach (Vector2s zone in BridgePlans.PlannedZones)
            zones.Add(zone);
        var found = new List<KeyValuePair<int, Vector2s>>();
        int total = 0;
        foreach (Vector2s zone in zones)
        {
            if (!ZoneSystem.instance.IsZoneGenerated(zone))
                continue;
            int count = CountVegetationOnRoad(zone);
            if (count == 0)
                continue;
            found.Add(new KeyValuePair<int, Vector2s>(count, zone));
            total += count;
        }
        found.Sort((a, b) => b.Key.CompareTo(a.Key));
        lines.Add($"{found.Count} generated road zones hold {total} vegetation objects inside their road's clear areas");
        for (int i = 0; i < found.Count && i < max; i++)
            lines.Add($"  {found[i].Key}: {DescribeZone(ZoneSystem.GetZonePos(found[i].Value))}");
        return lines;
    }

    /// <summary>Go over every road zone of the current network again, from the next frame (road_bake again).</summary>
    public static void Requeue()
    {
        s_faults.Clear();
        s_version = 0;
        // Do not change s_enabled: an operator's startup opt-out still holds.
    }

    /// <summary>Every frame (ZoneSystem.Update postfix): work the queue for a slice of the frame.</summary>
    public static void Tick()
    {
        try
        {
            if (!IsServer || ZoneSystem.instance == null || ZDOMan.instance == null || ZNetScene.instance == null)
                return;
            if (s_faults.IsPaused)
                return;
            if (Probe)
                ProbePeers();
            if (!Enabled || !RoadNetworkGenerator.RoadsAvailable)
                return;
            int version = RoadSpatialGrid.RoadNetworkVersion;
            if (version == 0)
                return;
            // A new network (loaded, generated, or regenerated by a command)
            // replaces whatever was queued for the old one.
            if (version != s_version)
                Enqueue(version);
            RunSlice();
        }
        catch (Exception ex)
        {
            s_faults.Pause(ex);
            Log.LogError($"[BAKE] queue paused after an error; ghost generation remains enabled. " +
                         $"Fix the cause, then road_bake again: {ex}");
        }
    }

    /// <summary>
    /// A zone is being generated for a remote peer and its vegetation has just
    /// been placed: the heightmap is still alive, the zone root about to go.
    /// Write the zone's compiler now.
    /// </summary>
    public static void OnGhostZoneGenerated(Vector2s zone, Heightmap hmap)
    {
        try
        {
            if (!Enabled || !IsServer || hmap == null || ZDOMan.instance == null ||
                !RoadNetworkGenerator.RoadsAvailable || RoadSpatialGrid.RoadNetworkVersion == 0)
                return;
            if (RoadSpatialGrid.GetRoadPointsInZone(zone).Count == 0)
            {
                // No road here, but a rock of this zone can reach over the road
                // next door, and this is the only time the server has it. No
                // terrain is owed, so a fault here is not a repair.
                try { s_counts.GhostRocks += ClearGhostRocks(zone, settle: false); }
                catch (Exception ex) { Log.LogError($"[BAKE] zone {zone} (generated for a peer, no road): rocks: {ex}"); }
                return;
            }

            if (s_faults.IsQuarantined(zone))
                return;

            var clock = Stopwatch.StartNew();
            WriteResult result;
            bool created = false;
            int bytes = 0;
            if (s_ghostWritesFailed < FailGhostWrites)
            {
                // PROCEDURALROADS_FAIL_GHOST_WRITES: fail without writing, so
                // the repair path can be proven in a real game rather than
                // only in tests. Nothing is written, so the zone really is
                // left without its road until the repair writes it.
                s_ghostWritesFailed++;
                result = WriteResult.Failed;
                Log.LogWarning($"[BAKE] zone {zone}: ghost write failed ON PURPOSE " +
                               $"({s_ghostWritesFailed} of {FailGhostWrites}, PROCEDURALROADS_FAIL_GHOST_WRITES)");
            }
            else
            {
                result = WriteOnGhostTerrain(zone, hmap, out created, out bytes);
            }
            s_counts.GhostMs += clock.Elapsed.TotalMilliseconds;
            if (result == WriteResult.Failed)
            {
                s_counts.GhostFailed++;
                // Vanilla will finish generating this zone regardless, after
                // which nothing would look at it again this session.
                s_ghostRepair.Record(zone);
            }
            else if (result == WriteResult.NothingToWrite)
                s_counts.NothingToWrite++;
            else if (created)
                s_counts.GhostCreated++;
            else
                s_counts.GhostRewritten++;
            s_counts.Bytes += bytes;
            int rocks = ClearGhostRocks(zone);
            s_counts.GhostRocks += rocks;
            Log.LogDebug($"[BAKE] zone {zone} (generated for a peer): {result}, " +
                         $"{bytes} bytes, {rocks} rock(s) cleared, {clock.Elapsed.TotalMilliseconds:F1} ms");
        }
        catch (Exception ex)
        {
            s_counts.GhostFailed++;
            s_ghostRepair.Record(zone);
            Log.LogError($"[BAKE] zone {zone} (generated for a peer): {ex}");
        }
    }

    /// <summary>
    /// Rocks clipping the road in a zone generated for a remote peer, cleared
    /// as Roads/RockClearing says while the zone's objects still exist: the
    /// server never loads that zone, so the automatic pass that clears loaded
    /// zones never sees it, and a player without the mod has no pass at all.
    /// </summary>
    private static int ClearGhostRocks(Vector2s zone, bool settle = true)
    {
        if (!RoadRockPolicy.Clears(RoadRockClearing.Mode)) return 0;
        RoadRockCarve.GhostSpawned = ZoneSystem.instance.m_tempSpawnedObjects;
        try
        {
            long sync = RoadRockStats.Now;
            Physics.SyncTransforms();
            // What a client would do next: StaticPhysics drops an object onto
            // the ground, and the road cut has just lowered it (the ghost
            // heightmap was rebuilt with the road terrain written above), so a
            // rock beside a cut falls into the road on the client, where
            // nothing would carve it. Settle it here and save where it lands.
            // A zone with no road of its own has no cut: nothing is moved there.
            float drop = 0f;
            int settled = settle ? SettleGhostObjects(zone, out drop) : 0;
            if (settled > 0) Physics.SyncTransforms();
            RoadRockStats.SyncTicks += RoadRockStats.Now - sync;
            s_counts.GhostSettled += settled;
            if (settled > 0)
                Log.LogDebug($"[BAKE] zone {zone} (generated for a peer): {settled} object(s) settled onto the graded ground, the largest move {drop:F2} m");
            // A zone is 64 m across: 46 m from its centre reaches its corners.
            // Its rocks reach further (a rock1_mountain is ~30 m across), and
            // only this zone's objects are judged here, so the road points
            // under any of them are probed, in whichever zone they lie.
            Vector3 centre = ZoneSystem.GetZonePos(zone);
            float radius = GhostRockReach(centre);
            if (!settle && RoadSpatialGrid.GetRoadPointsNearPosition(centre, radius).Count == 0) return 0;
            var lines = RoadRockProbe.Run(centre, radius, apply: true, sites: false, mist: true, cliffs: true,
                partial: true, carve: RoadRockPolicy.Carves(RoadRockClearing.Mode), out int cleared);
            Log.LogDebug($"[BAKE] zone {zone} (generated for a peer): {lines[0]}");
            return cleared;
        }
        finally { RoadRockCarve.GhostSpawned = null; }
    }

    /// <summary>How far round the zone's centre its clearable rocks reach, road width included (never under 46 m).</summary>
    private static float GhostRockReach(Vector3 centre)
    {
        var bounds = new List<(Vector2 min, Vector2 max)>();
        foreach (var go in ZoneSystem.instance.m_tempSpawnedObjects)
        {
            if (go == null || !RoadRockPolicy.IsClearableRock(RoadRockCarve.PrefabName(go, go.GetComponent<ZNetView>()))) continue;
            foreach (var c in go.GetComponentsInChildren<Collider>())
                bounds.Add((new Vector2(c.bounds.min.x, c.bounds.min.z), new Vector2(c.bounds.max.x, c.bounds.max.z)));
        }
        // 4 m: the widest road's half-width, as the carve reaches (RoadRockCarve.PointsNear).
        return RoadRockPolicy.OwnObjectsReach(new Vector2(centre.x, centre.z), bounds, 46f, 4f);
    }

    /// <summary>
    /// Move this zone's freshly spawned StaticPhysics objects where the game
    /// would (RoadRockClearing.FallTarget), transform and saved position both.
    /// A debug line per zone also says how far the ground under its road
    /// points is from the road's own height, which is ~0 only if the ghost
    /// heightmap carries the road terrain: the settling means nothing if not.
    /// </summary>
    private static int SettleGhostObjects(Vector2s zone, out float largest)
    {
        largest = 0f;
        int moved = 0;
        foreach (var go in ZoneSystem.instance.m_tempSpawnedObjects)
        {
            if (go == null) continue;
            var physics = go.GetComponent<StaticPhysics>();
            if (physics == null || !RoadRockClearing.FallTarget(physics, out float y)) continue;
            Vector3 p = go.transform.position;
            largest = Mathf.Max(largest, Mathf.Abs(p.y - y));
            p.y = y;
            go.transform.position = p;
            var view = go.GetComponent<ZNetView>();
            if (view != null && view.GetZDO() != null) view.GetZDO().SetPosition(p);
            moved++;
        }
        if (Log != null && s_graderChecks < 20)
        {
            s_graderChecks++;
            int n = 0; double sum = 0, worst = 0;
            foreach (var point in RoadSpatialGrid.GetRoadPointsInZone(zone))
            {
                if (point.paintOnly || !ZoneSystem.instance.GetGroundHeight(new Vector3(point.p.x, 0f, point.p.y), out float g)) continue;
                double d = Mathf.Abs(g - point.h); n++; sum += d; worst = System.Math.Max(worst, d);
            }
            if (n > 0) Log.LogDebug($"[BAKE] zone {zone}: ghost ground vs road height over {n} points: mean {sum / n:F2} m, worst {worst:F2} m");
        }
        return moved;
    }

    private static int s_graderChecks;

    public static string StatusLine()
    {
        Counts c = s_counts;
        int pending = Math.Max(0, s_queue.Count - s_next);
        return $"network {s_version}, {c.Zones} zones: {c.Created} compilers created, {c.Rewritten} saved ones written, " +
               $"{c.LiveWritten} written through a live compiler, " +
               $"{c.Current} already current, {c.Ungenerated} left to generation, {c.Loaded} loaded here without one, " +
               $"{c.Duplicates} with duplicate compilers, {c.NothingToWrite} with nothing to write, {c.Failed} failed, " +
               $"{s_ghostRepair.Count} deferred, {pending} pending; bridge pieces into {c.BridgeZones} zones; " +
               $"vegetation removed from {c.VegetationZones} zones ({c.VegetationRemoved} objects); {c.Bytes / 1024} KiB written; " +
               $"{s_busyMs / 1000.0:F1} s of frame time over {s_wall.Elapsed.TotalSeconds:F1} s. " +
               $"Generated for peers: {c.GhostCreated} compilers created, {c.GhostRewritten} saved ones written, " +
               $"{c.GhostFailed} failed ({s_ghostRepair.RepairCount} awaiting repair), {c.GhostRocks} rock(s) cleared, {c.GhostSettled} object(s) settled, {c.GhostMs:F0} ms; " +
               $"{s_faults.Count} zone exception(s); queue " +
               (!Enabled ? "disabled by startup setting" : s_faults.IsPaused
                   ? $"paused ({s_faults.PauseReason}); fix the cause, then road_bake again"
                   : s_faults.Count > 0 ? "enabled; stopped zones need road_bake again after fixing the cause" : "enabled");
    }

    private static void Enqueue(int version)
    {
        Reset();
        s_version = version;
        // The per-zone clear areas are cached for the world; a new network
        // (regenerated in a running session) must not be cleared, or
        // generated, with the old one's.
        RoadClearAreaManager.ClearCache();
        HashSet<Vector2s> zones = RoadSpatialGrid.GetZonesWithRoadPoints();
        int roadZones = zones.Count;
        foreach (Vector2s zone in BridgePlans.PlannedZones)
            zones.Add(zone);
        s_queue.AddRange(zones);
        // Nearest the world centre first, where players start.
        s_queue.Sort((a, b) => (a.x * a.x + a.y * a.y).CompareTo(b.x * b.x + b.y * b.y));
        s_counts.Zones = s_queue.Count;
        s_wall.Start();
        s_nextProgress = Time.time + ProgressEverySeconds;
        Log.LogInfo($"[BAKE] road network {version}: {s_queue.Count} zones carry roads or bridges " +
                    $"({roadZones} with road terrain); writing the generated ones that lack them");
    }

    private static void RunSlice()
    {
        float now = Time.time;

        // One drain, because there is one scheduler. A zone comes back when it
        // is due and the game has generated it -- whether it is here because a
        // ghost write failed, because a write failed, or because it was waiting
        // for an owner to leave. There is no second list that could hand the
        // same zone over again, or revive one that has been given up.
        if (s_ghostRepair.Count > 0 && now >= s_nextRepairCheck)
        {
            s_nextRepairCheck = now + 1f;
            List<Vector2s> ready = s_ghostRepair.TakeReady(
                zone => ZoneSystem.instance.IsZoneGenerated(zone), now);
            foreach (Vector2s zone in ready)
            {
                s_queue.Add(zone);
                s_finished = false;
                // Say so. Collapsing the two drains lost this line, and a
                // repair that leaves no trace cannot be told apart from one
                // that never happened -- an in-game run of the repair path
                // had to be read from a compilers-created count instead.
                Log.LogInfo($"[BAKE] zone {zone}: handed back to the queue after a failed write " +
                            $"({s_ghostRepair.FailedWrites(zone)} so far)");
            }
        }

        if (s_next < s_queue.Count)
        {
            var slice = Stopwatch.StartNew();
            int notReady = 0;
            while (s_next < s_queue.Count && slice.Elapsed.TotalMilliseconds < FrameBudgetMs)
            {
                Vector2s zone = s_queue[s_next++];
                if (!s_faults.TryProcessZone(zone, () => ProcessZone(zone), s_ghostRepair,
                        out Outcome outcome, out Exception? error))
                {
                    if (error != null)
                        Log.LogError($"[BAKE] zone {zone}: stopped after an exception; other zones continue. " +
                                     $"Fix the cause, then road_bake again: {error}");
                    continue;
                }
                if (outcome == Outcome.TerrainNotReady)
                {
                    // Asking started the build; come back to it after the rest.
                    s_queue.Add(zone);
                    if (++notReady >= NotReadyPerFrame)
                        break;
                }
            }
            s_busyMs += slice.Elapsed.TotalMilliseconds;
            if (s_next > 4096 && s_next * 2 > s_queue.Count)
            {
                s_queue.RemoveRange(0, s_next);
                s_next = 0;
            }
        }

        // Repairs still owed an answer are unfinished work: saying "done" over
        // the top of them is how an unresolved zone went unnoticed.
        bool idle = s_next >= s_queue.Count;
        if (idle && s_ghostRepair.Count == 0)
        {
            if (!s_finished)
            {
                s_finished = true;
                s_wall.Stop();
                Log.LogInfo((s_faults.Count == 0 ? "[BAKE] done: " : "[BAKE] incomplete: ") + StatusLine());
            }
        }
        else if (now >= s_nextProgress)
        {
            s_nextProgress = now + ProgressEverySeconds;
            Log.LogInfo((idle ? "[BAKE] waiting: " : "[BAKE] progress: ") + StatusLine());
        }

        // Zones generated for peers keep coming after the queue is done; say
        // so now and then, only when something changed.
        int ghost = s_counts.GhostCreated + s_counts.GhostRewritten + s_counts.GhostFailed;
        if (s_finished && ghost != s_loggedGhost && now >= s_nextGhostLog)
        {
            s_loggedGhost = ghost;
            s_nextGhostLog = now + GhostLogEverySeconds;
            Log.LogInfo($"[BAKE] generated for peers so far: {s_counts.GhostCreated} compilers created, " +
                        $"{s_counts.GhostRewritten} saved ones written, {s_counts.GhostFailed} failed, " +
                        $"{s_counts.GhostMs:F0} ms");
        }
    }

    private static Outcome ProcessZone(Vector2s zone)
    {
        if (FaultZoneOnce(zone))
            throw new InvalidOperationException($"thrown ON PURPOSE in zone {zone} (PROCEDURALROADS_FAULT_ZONE_ONCE)");
        ZoneSystem zs = ZoneSystem.instance;
        bool generated = zs.IsZoneGenerated(zone);
        bool loadedHere = zs.m_zones.ContainsKey(zone);
        Outcome terrain = ProcessTerrain(zone, generated, loadedHere);
        return terrain == Outcome.Done ? ProcessVegetation(zone, generated) : terrain;
    }

    private static Outcome ProcessTerrain(Vector2s zone, bool generated, bool loadedHere)
    {

        // Bridge pieces need no terrain: a generated zone the server does not
        // have loaded gets them as ZDOs now. (Ungenerated zones get them when
        // generated, loaded ones from the zone-spawn hook.)
        if (generated && !loadedHere && BridgePlans.PlanFor(zone) != null &&
            BridgePlacement.SpawnGhostInZone(zone) > 0)
            s_counts.BridgeZones++;

        // Terrain has spent its attempts on this zone for this network. Say so
        // and stop, WITHOUT touching the ledger: the zone may still be here for
        // vegetation, and that deferral used to recreate its entry with the
        // count back at zero, which handed terrain a fresh three every time a
        // player stood near an uncleared zone. Only a new network, or the zone
        // being resolved outright, lifts this.
        if (s_ghostRepair.HasGivenUp(zone))
            return Outcome.Done;

        if (RoadSpatialGrid.GetRoadPointsInZone(zone).Count == 0)
        {
            // Nothing to write here at all, so nothing is owed to it either.
            s_ghostRepair.Succeeded(zone);
            return Outcome.Done;
        }

        List<ZDO> saved = FindSavedCompilers(zone);
        ServerBakePlanner.Action action = ServerBakePlanner.Decide(
            s_version, generated, loadedHere, Describe(saved, zone), ZDOMan.GetSessionID());
        switch (action)
        {
            case ServerBakePlanner.Action.LeaveToGeneration:
                s_counts.Ungenerated++;
                Resolve(zone, action, ServerBakePlanner.WriteReport.NotAttempted);
                return Outcome.Done;
            case ServerBakePlanner.Action.LeaveToLiveZone:
                s_counts.Loaded++;
                Resolve(zone, action, ServerBakePlanner.WriteReport.NotAttempted);
                return Outcome.Done;
            case ServerBakePlanner.Action.WriteLiveCompiler:
            {
                // The zone is live here and already has a compiler: write
                // through that one. Raising the saved ZDO on a temporary
                // terrain would put a second compiler in a zone that has one,
                // and the game destroys "another terrain compiler in this
                // area" on sight. Anything that stops the write leaves the
                // zone pending rather than counted as done.
                TerrainComp? live = TerrainComp.FindTerrainCompiler(ZoneSystem.GetZonePos(zone));
                if (live == null || live.m_hmap == null || live.m_nview == null || !live.m_nview.IsValid())
                {
                    // Its compiler is not up yet: a prerequisite, not a failure.
                    Resolve(zone, action, ServerBakePlanner.WriteReport.NotAttempted);
                    return Outcome.Waiting;
                }
                if (!live.m_nview.IsOwner())
                {
                    // Somebody else's to write; ours only once they let go.
                    if (live.m_nview.HasOwner())
                    {
                        Resolve(zone, action, ServerBakePlanner.WriteReport.NotAttempted);
                        return Outcome.Waiting;
                    }
                    live.m_nview.ClaimOwnership();
                }
                RoadTerrainModifier.LastWriteOutcome = RoadTerrainModifier.WriteOutcome.None;
                RoadTerrainModifier.BakeRoadTerrain(zone, live.m_hmap, live);
                if (RoadTerrainModifier.CarriesCurrentRoads(live))
                {
                    s_counts.LiveWritten++;
                    Resolve(zone, action, ServerBakePlanner.WriteReport.Written);
                    return Outcome.Done;
                }
                if (RoadTerrainModifier.LastWriteOutcome == RoadTerrainModifier.WriteOutcome.NothingToWrite)
                {
                    s_counts.NothingToWrite++;
                    Resolve(zone, action, ServerBakePlanner.WriteReport.NothingToWrite);
                    return Outcome.Done;
                }
                // A write WAS tried here and left no stamp. That spends an
                // attempt like any other failed write; without that, a live
                // compiler that never takes the roads is retried forever.
                Log.LogWarning($"[BAKE] zone {zone}: its live terrain compiler did not take the roads");
                s_counts.Failed++;
                // Resolve spends the attempt AND schedules the retry. Adding a
                // wait of our own here is what gave the zone two schedulers,
                // and left one of them alive after the budget was gone.
                Resolve(zone, action, ServerBakePlanner.WriteReport.Failed);
                return Outcome.Done;
            }
            case ServerBakePlanner.Action.AlreadyCurrent:
                s_counts.Current++;
                Resolve(zone, action, ServerBakePlanner.WriteReport.NotAttempted);
                return Outcome.Done;
            case ServerBakePlanner.Action.DuplicateCompilers:
                s_counts.Duplicates++;
                Log.LogWarning($"[BAKE] zone {zone}: {saved.Count} terrain compilers saved; " +
                               "left alone rather than write one and leave the other to fight it");
                // Terminal: the queue will refuse this zone the same way every
                // time, so a repair waiting on it would wait for ever.
                Resolve(zone, action, ServerBakePlanner.WriteReport.NotAttempted);
                return Outcome.Done;
            case ServerBakePlanner.Action.WaitForOwner:
                Resolve(zone, action, ServerBakePlanner.WriteReport.NotAttempted);
                return Outcome.Waiting;
            case ServerBakePlanner.Action.CreateCompiler:
            case ServerBakePlanner.Action.WriteSavedCompiler:
                if (!TerrainReady(zone))
                {
                    int tries = s_notReady.TryGetValue(zone, out int t) ? t + 1 : 1;
                    s_notReady[zone] = tries;
                    if (tries < MaxNotReadyTries)
                        return Outcome.TerrainNotReady;
                    s_counts.Failed++;
                    Log.LogWarning($"[BAKE] zone {zone}: the game never built its terrain; not written");
                    Resolve(zone, action, ServerBakePlanner.WriteReport.Failed);
                    return Outcome.Done;
                }
                ZDO? existing = action == ServerBakePlanner.Action.WriteSavedCompiler ? saved[0] : null;
                WriteResult wrote;
                int bytes = 0;
                if (s_zoneWritesFailed < FailZoneWrites)
                {
                    // PROCEDURALROADS_FAIL_ZONE_WRITES: the repair's own write
                    // fails, so the attempt budget can be driven to exhaustion
                    // and the terminal case seen in a real game.
                    s_zoneWritesFailed++;
                    wrote = WriteResult.Failed;
                    Log.LogWarning($"[BAKE] zone {zone}: zone write failed ON PURPOSE " +
                                   $"({s_zoneWritesFailed} of {FailZoneWrites}, PROCEDURALROADS_FAIL_ZONE_WRITES)");
                }
                else
                {
                    wrote = WriteOnTemporaryTerrain(zone, existing, out bytes);
                }
                switch (wrote)
                {
                    case WriteResult.Written:
                        if (existing == null)
                            s_counts.Created++;
                        else
                            s_counts.Rewritten++;
                        s_counts.Bytes += bytes;
                        s_ghostRepair.Succeeded(zone);
                        break;
                    case WriteResult.NothingToWrite:
                        s_counts.NothingToWrite++;
                        s_ghostRepair.Succeeded(zone);
                        break;
                    default:
                        // A write was attempted and failed: that, and only
                        // that, spends one of the zone's repair attempts.
                        s_counts.Failed++;
                        Resolve(zone, action, ServerBakePlanner.WriteReport.Failed);
                        break;
                }
                return Outcome.Done;
            default:
                return Outcome.Done;
        }
    }

    /// <summary>
    /// Tell the repair ledger what became of this zone, and say whether there
    /// is any point coming back to it.
    ///
    /// EVERY path out of ProcessTerrain goes through here. A zone the ledger
    /// has handed over is marked as being in the queue and is not handed over
    /// again until the queue answers, so one that left without an answer would
    /// sit there for the rest of the session: never retried, never given up,
    /// and never counted. Waiting for a prerequisite is not an answer that
    /// costs anything; a write that was tried and failed is.
    /// </summary>
    private static bool Resolve(Vector2s zone, ServerBakePlanner.Action action,
        ServerBakePlanner.WriteReport report)
    {
        switch (ServerBakePlanner.ResolveRepair(action, report))
        {
            case ServerBakePlanner.RepairOutcome.Resolved:
                // Forgotten entirely, deadline included: nothing scheduled
                // earlier can bring finished work back.
                s_ghostRepair.Succeeded(zone);
                return true;
            case ServerBakePlanner.RepairOutcome.FailedWrite:
                // A zone that was not being watched starts being watched here,
                // so a first failure from the ordinary queue gets a budget
                // instead of retrying without one.
                if (s_ghostRepair.FailedWrite(zone, Time.time, WaitForOwnerSeconds))
                {
                    Log.LogWarning($"[BAKE] zone {zone}: {GhostRepairLedger.MaxFailedWrites} writes failed; given up. " +
                                   "road_bake again after fixing the cause, or a new road network, will take it up afresh");
                    return false;
                }
                return true;
            case ServerBakePlanner.RepairOutcome.Terminal:
                if (s_ghostRepair.Holds(zone))
                    Log.LogWarning($"[BAKE] zone {zone}: its repair cannot be finished here and needs a person; no longer watched");
                s_ghostRepair.Terminal(zone);
                return false;
            default:
                // Waiting on a prerequisite: costs nothing, and the scheduler
                // brings it back. Nothing else may schedule it.
                s_ghostRepair.Defer(zone, Time.time, WaitForOwnerSeconds);
                return true;
        }
    }

    /// <summary>
    /// A zone generated before its road still has everything the game grew
    /// there. Remove, once per zone and network, what generation would have
    /// left out (VegetationClearing). Waits while another player is near the
    /// zone: a tree vanishing under someone's axe is not worth it.
    /// </summary>
    private static Outcome ProcessVegetation(Vector2s zone, bool generated)
    {
        if (!generated || VegetationClearing.IsCleared(zone, s_version))
        {
            // Nothing owed here any more. Terrain's give-up, if it has one,
            // stands -- this says the PENDING work is done, not that terrain
            // is well again.
            s_ghostRepair.PendingWorkDone(zone);
            return Outcome.Done;
        }
        List<VegetationClearing.Area> areas = ClearAreasFor(zone);
        if (areas.Count > 0)
        {
            if (AnyPeerNear(zone))
            {
                // Through the one scheduler, like every other deferral.
                s_ghostRepair.Defer(zone, Time.time, WaitForOwnerSeconds);
                return Outcome.Waiting;
            }
            int removed = RemoveVegetation(zone, areas);
            if (removed > 0)
            {
                s_counts.VegetationZones++;
                s_counts.VegetationRemoved += removed;
                Log.LogDebug($"[BAKE] zone {zone}: removed {removed} trees, rocks and bushes from the road");
            }
        }
        VegetationClearing.MarkCleared(zone, s_version);
        // Vegetation is finished for this zone and this network. If terrain
        // gave up earlier it stays given up; what goes is the pending entry,
        // which otherwise kept the bake from ever reporting itself done.
        s_ghostRepair.PendingWorkDone(zone);
        return Outcome.Done;
    }

    /// <summary>The clear areas generation hands PlaceVegetation for this zone: the road's and the bridges'.</summary>
    private static List<VegetationClearing.Area> ClearAreasFor(Vector2s zone)
    {
        var areas = new List<VegetationClearing.Area>();
        foreach (ZoneSystem.ClearArea area in RoadClearAreaManager.GetOrCreateClearAreas(zone))
            areas.Add(new VegetationClearing.Area(new Vector2(area.m_center.x, area.m_center.z), area.m_radius));
        foreach (ZoneSystem.ClearArea area in BridgePlacement.GetClearAreas(zone))
            areas.Add(new VegetationClearing.Area(new Vector2(area.m_center.x, area.m_center.z), area.m_radius));
        return areas;
    }

    private static bool AnyPeerNear(Vector2s zone)
    {
        Vector3 centre = ZoneSystem.GetZonePos(zone);
        foreach (ZNetPeer peer in ZNet.instance.GetPeers())
        {
            if (ZNetScene.InActiveArea(centre, peer.GetRefPos()))
                return true;
        }
        return false;
    }

    /// <summary>road_bake zone: what RemoveVegetation would take from the zone now, without taking it.</summary>
    internal static int CountVegetationOnRoad(Vector2s zone)
    {
        List<VegetationClearing.Area> areas = ClearAreasFor(zone);
        if (areas.Count == 0)
            return 0;
        HashSet<int> vegetation = VegetationPrefabs();
        List<VegetationClearing.Footprint> locations = LocationsNear(zone);
        var zdos = new List<ZDO>();
        ZDOMan.instance.FindObjects(zone, zdos, new HashSet<ZoneSystem.SectorIndex>());
        // The same gathering the removal uses, so the preview and the deletion
        // cannot disagree about what is protected.
        List<VegetationClearing.Footprint> playerGround = PlayerGroundNear(zone);
        int count = 0;
        foreach (ZDO zdo in zdos)
        {
            Vector3 position = zdo.GetPosition();
            if (VegetationClearing.ShouldRemove(zdo.GetPrefab(), new Vector2(position.x, position.z),
                    IsPlayerCreated(zdo), vegetation, areas, locations, playerGround))
                count++;
        }
        return count;
    }

    private static List<VegetationClearing.Footprint> LocationsNear(Vector2s zone)
    {
        var locations = new List<VegetationClearing.Footprint>();
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (ZoneSystem.instance.m_locationInstances.TryGetValue(new Vector2s(zone.x + dx, zone.y + dy),
                        out ZoneSystem.LocationInstance location) && location.m_location != null)
                    locations.Add(new VegetationClearing.Footprint(
                        new Vector2(location.m_position.x, location.m_position.z), location.m_location.m_exteriorRadius));
            }
        }
        return locations;
    }

    /// <summary>Whether a player made this object: vanilla stamps what a player builds with its creator.</summary>
    private static bool IsPlayerCreated(ZDO zdo) => zdo.GetLong(ZDOVars.s_creator, 0L) != 0L;

    /// <summary>
    /// Ground a player has built on, as a circle around each of their objects,
    /// gathered from this zone AND its eight neighbours. Vegetation standing
    /// there is left alone, planted or not: see VegetationClearing.ShouldRemove
    /// for what the save cannot tell us.
    ///
    /// The neighbours are the whole point. A hut at (31, 0) and a tree at
    /// (33, 0) are two metres apart with a zone border between them; the border
    /// is an artefact of how the world is stored, not a fact about the ground,
    /// and a protection radius that stopped at it would be 8 m in the middle of
    /// a zone and nothing at its edge. The radius is smaller than a zone
    /// (asserted in the tests), so the eight neighbours reach everything that
    /// can reach in.
    /// </summary>
    private static List<VegetationClearing.Footprint> PlayerGroundNear(Vector2s zone)
    {
        var ground = new List<VegetationClearing.Footprint>();
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                var zdos = new List<ZDO>();
                // A fresh visited set per call, as every other caller does:
                // sharing one would make the later zones return nothing.
                ZDOMan.instance.FindObjects(new Vector2s(zone.x + dx, zone.y + dy), zdos,
                    new HashSet<ZoneSystem.SectorIndex>());
                foreach (ZDO zdo in zdos)
                {
                    if (!IsPlayerCreated(zdo))
                        continue;
                    Vector3 position = zdo.GetPosition();
                    ground.Add(new VegetationClearing.Footprint(
                        new Vector2(position.x, position.z), VegetationClearing.PlayerGroundRadius));
                }
            }
        }
        return ground;
    }

    private static int RemoveVegetation(Vector2s zone, List<VegetationClearing.Area> areas)
    {
        HashSet<int> vegetation = VegetationPrefabs();
        List<VegetationClearing.Footprint> locations = LocationsNear(zone);
        var zdos = new List<ZDO>();
        ZDOMan.instance.FindObjects(zone, zdos, new HashSet<ZoneSystem.SectorIndex>());
        List<VegetationClearing.Footprint> playerGround = PlayerGroundNear(zone);
        long me = ZDOMan.GetSessionID();
        int removed = 0;
        foreach (ZDO zdo in zdos)
        {
            Vector3 position = zdo.GetPosition();
            if (!VegetationClearing.ShouldRemove(zdo.GetPrefab(), new Vector2(position.x, position.z),
                    IsPlayerCreated(zdo), vegetation, areas, locations, playerGround))
                continue;
            // As BridgePlacement.ClearSpawnedPieces: through the scene when the
            // object is alive here, straight from the ZDOs otherwise.
            ZNetView instance = ZNetScene.instance.FindInstance(zdo);
            if (instance != null)
            {
                instance.ClaimOwnership();
                // Destroy is deferred: nothing later in this zone's pass (the rock carve's support
                // test) may lean on an object that is about to vanish.
                foreach (var c in instance.GetComponentsInChildren<Collider>()) c.enabled = false;
                ZNetScene.instance.Destroy(instance.gameObject);
            }
            else
            {
                zdo.SetOwner(me);
                ZDOMan.instance.DestroyZDO(zdo);
            }
            removed++;
        }
        return removed;
    }

    internal static HashSet<int> VegetationPrefabs()
    {
        if (s_vegetationPrefabs != null)
            return s_vegetationPrefabs;
        s_vegetationPrefabs = new HashSet<int>();
        foreach (ZoneSystem.ZoneVegetation vegetation in ZoneSystem.instance.m_vegetation)
        {
            if (vegetation.m_prefab != null)
                s_vegetationPrefabs.Add(vegetation.m_prefab.name.GetStableHashCode());
        }
        Log.LogDebug($"[BAKE] {s_vegetationPrefabs.Count} vegetation prefabs");
        return s_vegetationPrefabs;
    }

    internal static List<ZDO> FindSavedCompilers(Vector2s zone)
    {
        var zdos = new List<ZDO>();
        ZDOMan.instance.FindObjects(zone, zdos, new HashSet<ZoneSystem.SectorIndex>());
        zdos.RemoveAll(zdo => zdo.GetPrefab() != TerrainCompilerPrefabHash);
        return zdos;
    }

    private static List<ServerBakePlanner.Compiler> Describe(List<ZDO> compilers, Vector2s zone)
    {
        var facts = new List<ServerBakePlanner.Compiler>(compilers.Count);
        long me = ZDOMan.GetSessionID();
        foreach (ZDO zdo in compilers)
        {
            long owner = zdo.GetOwner();
            bool ownerActiveHere = owner != 0 && owner != me &&
                                   ZDOMan.instance.IsInPeerActiveArea(zdo.GetPosition(), owner);
            int applied = zdo.GetInt(RoadTerrainModifier.AppliedVersionHash);
            facts.Add(new ServerBakePlanner.Compiler(applied, owner, ownerActiveHere,
                RoadTerrainModifier.CarriesCurrentRoads(zone, applied)));
        }
        return facts;
    }

    /// <summary>Whether the game has the zone's terrain built (asking starts the build), as SpawnZone checks.</summary>
    private static bool TerrainReady(Vector2s zone)
    {
        Heightmap? template = ZoneSystem.instance.m_zonePrefab.GetComponentInChildren<Heightmap>();
        if (template == null || HeightmapBuilder.instance == null || WorldGenerator.instance == null)
            return false;
        return HeightmapBuilder.instance.IsTerrainReady(ZoneSystem.GetZonePos(zone), template.m_width,
            template.m_scale, template.IsDistantLod, WorldGenerator.instance);
    }

    /// <summary>
    /// Write a zone the server does not have loaded: make the zone's terrain
    /// the way the game makes a zone, bring its compiler to life against it
    /// (the saved one, or a new one if it has none), and take both down again,
    /// leaving only the compiler's ZDO.
    /// </summary>
    private static WriteResult WriteOnTemporaryTerrain(Vector2s zone, ZDO? saved, out int bytes)
    {
        bytes = 0;
        var clock = Stopwatch.StartNew();
        GameObject? root = null;
        GameObject? compiler = null;
        ZDO? made = null;
        WriteResult result = WriteResult.Failed;
        try
        {
            root = Object.Instantiate(ZoneSystem.instance.m_zonePrefab, ZoneSystem.GetZonePos(zone), Quaternion.identity);
            Heightmap? hmap = root.GetComponentInChildren<Heightmap>();
            if (hmap == null || !EnsureGenerated(hmap))
            {
                Log.LogWarning($"[BAKE] zone {zone}: no terrain to write against; not written");
                return result;
            }
            RoadTerrainModifier.LastWriteOutcome = RoadTerrainModifier.WriteOutcome.None;
            compiler = saved != null ? BringSavedCompilerAlive(saved, hmap) : CreateCompiler(hmap);
            if (saved == null && compiler != null)
                made = compiler.GetComponent<ZNetView>()?.GetZDO();
            result = Written(zone, compiler, hmap, out bytes);
            Log.LogDebug($"[BAKE] zone {zone}: {(saved != null ? "saved" : "new")} compiler {result}, " +
                         $"{bytes} bytes, {clock.Elapsed.TotalMilliseconds:F1} ms");
            return result;
        }
        finally
        {
            if (compiler != null)
                Release(compiler, bound: saved != null);
            if (root != null)
                Object.DestroyImmediate(root);
            // A compiler made for a zone the roads only brush would sit in the
            // save with nothing in it.
            if (result == WriteResult.NothingToWrite && made != null)
                ZDOMan.instance.DestroyZDO(made);
        }
    }

    /// <summary>The ghost-generation path: the zone's own heightmap, still alive.</summary>
    private static WriteResult WriteOnGhostTerrain(Vector2s zone, Heightmap hmap, out bool created, out int bytes)
    {
        created = false;
        bytes = 0;

        TerrainComp? live = TerrainComp.FindTerrainCompiler(hmap.transform.position);
        if (live != null)
        {
            // Made while the zone was being populated (a location's own
            // terrain operation): its Awake postfix already offered it the
            // roads. It is the game's object, not ours to take down.
            RoadTerrainModifier.CompletePendingTerrain(live, hmap);
            return live.m_hmap == hmap && RoadTerrainModifier.CarriesCurrentRoads(live)
                ? WriteResult.Written
                : WriteResult.Failed;
        }

        List<ZDO> saved = FindSavedCompilers(zone);
        ServerBakePlanner.Action action = ServerBakePlanner.Decide(RoadSpatialGrid.RoadNetworkVersion,
            generated: true, loadedHere: false, Describe(saved, zone), ZDOMan.GetSessionID());
        GameObject? go = null;
        bool bound = false;
        WriteResult result = WriteResult.Failed;
        RoadTerrainModifier.LastWriteOutcome = RoadTerrainModifier.WriteOutcome.None;
        try
        {
            switch (action)
            {
                case ServerBakePlanner.Action.AlreadyCurrent:
                    return result = WriteResult.Written;
                case ServerBakePlanner.Action.CreateCompiler:
                    go = CreateCompiler(hmap);
                    created = true;
                    break;
                case ServerBakePlanner.Action.WriteSavedCompiler:
                    go = BringSavedCompilerAlive(saved[0], hmap);
                    bound = true;
                    break;
                default:
                    Log.LogWarning($"[BAKE] zone {zone} (generated for a peer): {action}; not written now");
                    return result;
            }
            result = Written(zone, go, hmap, out bytes);
            return result;
        }
        finally
        {
            ZDO? made = created && go != null ? go.GetComponent<ZNetView>()?.GetZDO() : null;
            if (go != null)
                Release(go, bound);
            if (result == WriteResult.NothingToWrite && made != null)
                ZDOMan.instance.DestroyZDO(made);
        }
    }

    private static bool EnsureGenerated(Heightmap hmap)
    {
        if (hmap.GetPaintMask() == null)
            hmap.Regenerate();
        return hmap.GetPaintMask() != null;
    }

    /// <summary>
    /// Whether the compiler came alive against this heightmap and now carries
    /// the current roads. Its Awake postfix (RoadTerrainModifier.
    /// OnTerrainCompilerReady) queued the write; complete the rebuild before
    /// checking its stamp and releasing the temporary terrain.
    /// </summary>
    private static WriteResult Written(Vector2s zone, GameObject? go, Heightmap hmap, out int bytes)
    {
        bytes = 0;
        TerrainComp? tc = go != null ? go.GetComponent<TerrainComp>() : null;
        if (tc == null || tc.m_nview == null || !tc.m_nview.IsValid())
        {
            Log.LogWarning($"[BAKE] zone {zone}: the terrain compiler did not come alive; not written");
            return WriteResult.Failed;
        }
        if (tc.m_hmap != hmap)
        {
            Log.LogWarning($"[BAKE] zone {zone}: the terrain compiler found another heightmap; not written");
            return WriteResult.Failed;
        }
        RoadTerrainModifier.CompletePendingTerrain(tc, hmap);
        if (!RoadTerrainModifier.CarriesCurrentRoads(tc))
        {
            // Points just outside the zone that reach into it too faintly to
            // change anything: nothing is wrong, there is simply nothing to do.
            if (RoadTerrainModifier.LastWriteOutcome == RoadTerrainModifier.WriteOutcome.NothingToWrite)
                return WriteResult.NothingToWrite;
            Log.LogWarning($"[BAKE] zone {zone}: the roads did not go into its terrain compiler");
            return WriteResult.Failed;
        }
        bytes = tc.m_nview.GetZDO().GetByteArray(ZDOVars.s_TCData)?.Length ?? 0;
        return WriteResult.Written;
    }

    /// <summary>
    /// Ghost init, as the game creates the objects of a zone it generates for
    /// a remote peer: the ZDO is made, the object is not registered with the
    /// scene, and destroying the object leaves the ZDO in the world.
    /// </summary>
    private static GameObject CreateCompiler(Heightmap hmap)
    {
        ZNetView.StartGhostInit();
        try
        {
            return Object.Instantiate(hmap.m_terrainCompilerPrefab, hmap.transform.position, Quaternion.identity);
        }
        finally
        {
            ZNetView.FinishGhostInit();
        }
    }

    /// <summary>
    /// Bring a saved compiler to life exactly as ZNetScene does, so its Awake
    /// loads the terrain edits already in it and the roads are written on top.
    /// Its owner, if any, is gone or elsewhere (the planner waits otherwise);
    /// the game hands it back to the next peer that comes near, as it does
    /// with every object the server generates.
    /// </summary>
    private static GameObject? BringSavedCompilerAlive(ZDO zdo, Heightmap hmap)
    {
        if (!zdo.IsOwner())
            zdo.SetOwner(ZDOMan.GetSessionID());
        ZNetView.m_useInitZDO = true;
        ZNetView.m_initZDO = zdo;
        try
        {
            return Object.Instantiate(hmap.m_terrainCompilerPrefab, zdo.GetPosition(), zdo.GetRotation());
        }
        finally
        {
            if (ZNetView.m_initZDO != null)
            {
                Log.LogWarning($"[BAKE] saved terrain compiler {zdo.m_uid} was not taken up by the object made for it");
                ZNetView.m_initZDO = null;
            }
            ZNetView.m_useInitZDO = false;
        }
    }

    /// <summary>
    /// Take a compiler down and keep its ZDO. A saved one is first undone from
    /// the scene the way ZNetScene removes an object that leaves its area.
    /// </summary>
    private static void Release(GameObject go, bool bound)
    {
        ZNetView? view = go.GetComponent<ZNetView>();
        if (bound && view != null && view.GetZDO() != null)
        {
            ZNetScene.instance.m_instances.Remove(view.GetZDO());
            view.ResetZDO();
        }
        Object.DestroyImmediate(go);
    }

    /// <summary>
    /// PROCEDURALROADS_PEER_PROBE: where each connected player stands, against
    /// the generated ground and the nearest road point. A player's height
    /// comes from their own client's physics, so a player standing on a
    /// raised road that their client did not build would show it here.
    /// Peers are numbered, never named.
    /// </summary>
    private static void ProbePeers()
    {
        float now = Time.time;
        if (now < s_nextProbe)
            return;
        s_nextProbe = now + ProbeEverySeconds;

        List<ZNetPeer> peers = ZNet.instance.GetPeers();
        for (int i = 0; i < peers.Count; i++)
        {
            ZNetPeer peer = peers[i];
            ZDO? character = peer.m_characterID.IsNone() ? null : ZDOMan.instance.GetZDO(peer.m_characterID);
            Vector3 pos = character != null ? character.GetPosition() : peer.m_refPos;
            Vector2s zone = ZoneSystem.GetZone(pos);
            float ground = WorldGenerator.instance != null
                ? BiomeBlendedHeight.GetBlendedHeight(pos.x, pos.z, WorldGenerator.instance)
                : float.NaN;

            string road = "no road point within 3 m";
            if (RoadNetworkGenerator.RoadsAvailable)
            {
                float best = 9f;
                RoadSpatialGrid.RoadPoint? nearest = null;
                var here = new Vector2(pos.x, pos.z);
                foreach (RoadSpatialGrid.RoadPoint rp in RoadSpatialGrid.GetRoadPointsInZone(zone))
                {
                    float d2 = (rp.p - here).sqrMagnitude;
                    if (d2 < best)
                    {
                        best = d2;
                        nearest = rp;
                    }
                }
                if (nearest.HasValue)
                    road = $"road h {nearest.Value.h:F2} at {Mathf.Sqrt(best):F1} m, " +
                           $"player - road {pos.y - nearest.Value.h:+0.00;-0.00}";
            }

            Log.LogInfo($"[PROBE] peer {i + 1} {(character != null ? "player" : "ref")} " +
                        $"({pos.x:F1}, {pos.y:F2}, {pos.z:F1}) zone {zone}: generated ground {ground:F2}, " +
                        $"player - ground {pos.y - ground:+0.00;-0.00}; {road}; {DescribeCompilers(zone)}");
        }
    }

    private static string DescribeCompilers(Vector2s zone)
    {
        List<ZDO> saved = FindSavedCompilers(zone);
        if (saved.Count == 0)
            return "no terrain compiler";
        if (saved.Count > 1)
            return $"{saved.Count} terrain compilers";
        int stamp = saved[0].GetInt(RoadTerrainModifier.AppliedVersionHash);
        return stamp != 0 && stamp == RoadSpatialGrid.RoadNetworkVersion
            ? "its compiler carries the roads"
            : $"compiler stamped {stamp}";
    }
}
