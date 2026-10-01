using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Automatic rock clearing: the rocks clipping the road are looked for over
/// the loaded zones, one heightmap every half second, so a road is walkable
/// without anyone clearing it by hand. Natural boulders in every supported
/// biome, and Mistlands rocks and cliffs, are cleared as
/// <see cref="Mode"/> says; never location parts, player pieces or objects
/// another peer owns (their zone is looked at again on the next pass, once
/// ownership has settled). Networked rocks are destroyed through ZNetScene and
/// stay gone; local scenery is rebuilt on reload and cleared again.
/// </summary>
public static class RoadRockClearing
{
    /// <summary>Set from the "Roads/RockClearing" setting.</summary>
    internal static RockClearingMode Mode = RockClearingMode.Carve;
    private static float s_next;
    private static int s_cursor;
    /// <summary>
    /// Loaded zones the pass has finished with, by heightmap instance id and the
    /// road network they were checked against. Carved and removed rocks stay
    /// that way in the zone's saved objects, so a finished zone has nothing
    /// left until it is loaded again (a new heightmap, and local scenery
    /// rebuilt) or the network changes.
    /// </summary>
    private static readonly Dictionary<int, int> s_done = new Dictionary<int, int>();
    /// <summary>Zones left for later, by heightmap instance id: not looked at again before this time.</summary>
    private static readonly Dictionary<int, float> s_retryAt = new Dictionary<int, float>();
    private const float RetryDelay = 2f;
    private static readonly List<ZDO> s_zdos = new List<ZDO>();

    public static void Reset() { s_next = 0f; s_cursor = 0; s_done.Clear(); s_retryAt.Clear(); }

    public static void Tick()
    {
        RoadRockStats.MaybeLog();
        if (!RoadRockPolicy.Clears(Mode) || !RoadNetworkGenerator.RoadsAvailable || ZoneSystem.instance == null || ZNetScene.instance == null ||
            Time.unscaledTime < s_next) return;
        s_next = Time.unscaledTime + 0.5f;
        var maps = Heightmap.GetAllHeightmaps();
        if (maps == null || maps.Count == 0) return;
        int version = RoadSpatialGrid.RoadNetworkVersion;
        // One probe per tick, but finished zones cost nothing: move past them to
        // the next zone that needs a look, so a newly loaded zone is reached in
        // half a second rather than after a turn through every loaded one.
        Heightmap? map = null;
        Vector2s zone = default;
        for (int looked = 0; looked < maps.Count && map == null; looked++)
        {
            if (s_cursor >= maps.Count) s_cursor = 0;
            var candidate = maps[s_cursor++];
            if (candidate == null) continue;
            if (s_done.TryGetValue(candidate.GetInstanceID(), out int doneFor) && doneFor == version) { RoadRockStats.Skipped++; continue; }
            // A distant low-detail heightmap has no collider and no objects: nothing to probe.
            if (candidate.IsDistantLod) continue;
            zone = ZoneSystem.GetZone(candidate.transform.position);
            if (RoadSpatialGrid.GetRoadPointsInZone(zone).Count == 0) { MarkDone(candidate, version); continue; }
            // Not yet: every saved object round the zone must have its instance
            // (the game creates them over several frames) and the terrain its
            // collider. Looked at again when it is ready; probing it before only
            // repeated the work and could finish a zone whose rocks were missing.
            // And the zone's road terrain must be written, or the road is judged
            // on the ungraded ground (measured: a zone loaded with the world was
            // finished before its saved terrain compiler came alive, and a rock
            // in the road was left).
            if (s_retryAt.TryGetValue(candidate.GetInstanceID(), out float retry) && Time.unscaledTime < retry) { RoadRockStats.NotReady++; continue; }
            if (candidate.HaveQueuedRebuild() || !ZNetScene.instance.IsAreaReady(candidate.transform.position) ||
                !RoadTerrainModifier.TerrainSettled(candidate.transform.position)) { RoadRockStats.NotReady++; continue; }
            map = candidate;
        }
        if (map == null) return;
        // Objects created or moved since the last physics step are not where the
        // overlap queries look until the transforms are synced (the server's
        // pass for peers does the same).
        long sync = RoadRockStats.Now;
        Physics.SyncTransforms();
        RoadRockStats.SyncTicks += RoadRockStats.Now - sync;
        // A zone is 64 m across: 46 m from its centre reaches its corners.
        var lines = RoadRockProbe.Run(map.transform.position, 46f, apply: true, sites: false, mist: true, cliffs: true,
            partial: true, carve: RoadRockPolicy.Carves(Mode), out int cleared);
        // Done only when the ground answered at every road point and nothing was
        // left for another peer that owns it.
        bool notOwner = lines.Exists(l => l.Contains("not owner"));
        float settledBy = Time.time;
        int settling = RoadRockProbe.LastGroundMisses == 0 && !notOwner ? Settling(map.transform.position, null, out settledBy) : 0;
        if (RoadRockProbe.LastGroundMisses == 0 && !notOwner && settling == 0)
        {
            MarkDone(map, version);
            s_retryAt.Remove(map.GetInstanceID());
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug($"Road rocks: zone {zone} done: {lines[0]}");
        }
        else
        {
            if (s_retryAt.Count > 1024) s_retryAt.Clear();
            // Objects still to fall move on their own schedule (a first check 20 s
            // after they wake): look again once they have, not every retry delay.
            float wait = settling > 0 ? Mathf.Clamp(settledBy - Time.time, RetryDelay, 30f) : RetryDelay;
            s_retryAt[map.GetInstanceID()] = Time.unscaledTime + wait;
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug($"Road rocks: zone {zone} again later ({RoadRockProbe.LastGroundMisses} road points without ground" +
                $"{(notOwner ? ", a rock owned elsewhere" : "")}{(settling > 0 ? $", {settling} object(s) still to fall or push up, again in {Mathf.Clamp(settledBy - Time.time, RetryDelay, 30f):F1} s" : "")}): {lines[0]}");
        }
        if (cleared > 0)
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo($"Road rocks: cleared {cleared} in zone {zone}");
    }

    /// <summary>
    /// Objects round a zone that the game will still move: StaticPhysics drops
    /// an object that sits above its fall height (and lifts one below the
    /// ground) on its first check, 20 s after it wakes, and on every check
    /// after. Cutting a road lowers the ground, so a rock beside the cut drops
    /// into the road seconds after the zone loads (measured: 3 m, into 12 road
    /// points, 9 s after load, after the pass had found the road clear). A zone
    /// is finished only when nothing round it will move. Same fall height as
    /// StaticPhysics.GetFallHeight.
    /// </summary>
    private static int Settling(Vector3 zoneCentre) => Settling(zoneCentre, null, out _);

    /// <summary>StaticPhysics.m_updateTime: when an object's first fall check is due (Time.time).</summary>
    private static readonly System.Reflection.FieldInfo? s_updateTime =
        typeof(StaticPhysics).GetField("m_updateTime", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    /// <param name="settledBy">Time.time by which every one of them will have moved: their first
    /// check (m_updateTime) is past, plus the fall itself (0.2 m every 0.05 s).</param>
    private static int Settling(Vector3 zoneCentre, List<string>? named, out float settledBy)
    {
        settledBy = Time.time;
        s_zdos.Clear();
        ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(zoneCentre), new SimulationDistance(1, 0), s_zdos);
        int count = 0;
        foreach (var zdo in s_zdos)
        {
            var view = ZNetScene.instance.FindInstance(zdo);
            if (view == null) continue;
            var physics = view.GetComponent<StaticPhysics>();
            if (physics == null) continue;
            float to = physics.transform.position.y;
            if (physics.IsFalling || FallTarget(physics, out to))
            {
                count++;
                float due = s_updateTime != null ? (float)s_updateTime.GetValue(physics) : Time.time + 20f;
                float fall = Mathf.Abs(physics.transform.position.y - (physics.IsFalling ? physics.transform.position.y : to)) / 0.2f * 0.05f;
                settledBy = Mathf.Max(settledBy, Mathf.Max(due, Time.time) + fall + 0.5f);
                if (named != null && named.Count < 12)
                {
                    Vector3 p = physics.transform.position;
                    named.Add(System.FormattableString.Invariant(
                        $"  settling: {RoadRockCarve.PrefabName(view.gameObject, view)} at ({p.x:F1},{p.y:F2},{p.z:F1}) -> y {(physics.IsFalling ? p.y : FallTarget(physics, out float t2) ? t2 : p.y):F2}{(physics.IsFalling ? " (falling)" : "")} fall={physics.m_fall} pushUp={physics.m_pushUp} checkSolids={physics.m_checkSolids}"));
                }
            }
        }
        return count;
    }

    /// <summary>
    /// Where StaticPhysics will move an object on its next check, if anywhere:
    /// down to its fall height (StaticPhysics.GetFallHeight: the solid height
    /// round it with m_checkSolids, else the ground) when it is above it, or up
    /// to the ground when it is below it (PushUp). False when it stays put.
    /// </summary>
    internal static bool FallTarget(StaticPhysics physics, out float y)
    {
        Vector3 p = physics.transform.position;
        y = p.y;
        float floor;
        if (physics.m_checkSolids)
            floor = ZoneSystem.instance.GetSolidHeight(p, physics.m_fallCheckRadius, out float solid, physics.transform) ? solid : p.y;
        else
            floor = ZoneSystem.instance.GetGroundHeight(p, out float ground) ? ground : p.y;
        if (physics.m_fall && p.y > floor + 0.05f) { y = floor; return true; }
        if (physics.m_pushUp && ZoneSystem.instance.GetGroundHeight(p, out float under) && p.y < under - 0.05f) { y = under; return true; }
        return false;
    }

    /// <summary>
    /// Why the automatic pass does or does not look at the zone round a point
    /// (a diagnostic: a static method a console tool can call with a point).
    /// </summary>
    public static List<string> Describe(Vector3 point)
    {
        var lines = new List<string>();
        var zone = ZoneSystem.GetZone(point);
        var map = Heightmap.FindHeightmap(point);
        int version = RoadSpatialGrid.RoadNetworkVersion;
        lines.Add($"zone {zone}: mode {Mode}, roads available {RoadNetworkGenerator.RoadsAvailable}, network {version}, road points in zone {RoadSpatialGrid.GetRoadPointsInZone(zone).Count}");
        if (map == null) { lines.Add("no heightmap here"); return lines; }
        bool listed = Heightmap.GetAllHeightmaps().Contains(map);
        bool done = s_done.TryGetValue(map.GetInstanceID(), out int doneFor);
        lines.Add($"heightmap {map.GetInstanceID()} in the pass's list {listed}, distant LOD {map.IsDistantLod}, rebuild queued {map.HaveQueuedRebuild()}, " +
                  $"area ready {(ZNetScene.instance != null && ZNetScene.instance.IsAreaReady(map.transform.position))}, terrain settled {RoadTerrainModifier.TerrainSettled(map.transform.position)}, " +
                  $"done {(done ? (doneFor == version ? "yes" : $"for network {doneFor}") : "no")}, still to settle {Settling(map.transform.position)}; {s_done.Count} zones marked; " +
                  $"{Heightmap.GetAllHeightmaps().Count} heightmaps listed, cursor {s_cursor}");
        Settling(map.transform.position, lines, out float by);
        lines.Add($"  settled by t={by:F1} (now {Time.time:F1})");
        return lines;
    }

    private static void MarkDone(Heightmap map, int version)
    {
        // Ids of unloaded zones pile up; forgetting them all costs one more look per loaded zone.
        if (s_done.Count > 1024) s_done.Clear();
        s_done[map.GetInstanceID()] = version;
    }
}
