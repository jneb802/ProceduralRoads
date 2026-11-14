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
    /// <summary>
    /// Zones whose only movers are outside the game's active area round the player (RoadRockPolicy.ZoneVerdict
    /// .WaitForPlayerZone): held in <see cref="s_retryAt"/> with no due time, and released together when the player's
    /// zone or the road network differs from the one they waited from, so an idle player costs nothing.
    /// </summary>
    private static readonly List<int> s_waiting = new List<int>();
    private static Vector2s s_waitedFrom;
    private static int s_waitedVersion;
    private static readonly List<ZDO> s_zdos = new List<ZDO>();

    /// <summary>
    /// The carves the last pass decided, applied over the frames after it: a carve (the swap to the fractured copy,
    /// the chunks' saved healths, the mesh rebuilt) is Unity work, ~18 ms each, and a pass carving several in its one
    /// frame held that frame for 60 to 130 ms. Each frame carves while it is inside the bake's frame budget
    /// (ServerTerrainBake.FrameBudgetMs), at least one; no new pass starts until they are done, and the zone is
    /// judged done or due again only then, as it was when the carves ran in the pass.
    /// </summary>
    private static readonly List<RoadRockProbe.PendingCarve> s_carves = new List<RoadRockProbe.PendingCarve>();
    private static int s_carveNext;
    /// <summary>The pass the pending carves came from, and what it found (read before any later pass overwrites it).</summary>
    private static Pass? s_pass;

    private sealed class Pass
    {
        public Heightmap Map = null!;
        public Vector2s Zone;
        public string Line = "";
        public int Version, Cleared, GroundMisses, NotOwner, Skipped;
    }

    public static void Reset() { s_next = 0f; s_cursor = 0; s_done.Clear(); s_retryAt.Clear(); s_waiting.Clear(); s_carves.Clear(); s_carveNext = 0; s_pass = null; }

    /// <summary>A structure was laid with no change to the network (VegetationClearing.Reached): every loaded zone is looked at
    /// again, as after an append, its new clearances with the rest.</summary>
    internal static void LookAgain() => s_done.Clear();

    public static void Tick()
    {
        RoadRockStats.MaybeLog();
        if (!RoadLifecycleManager.WritesTheNetwork || ZoneSystem.instance == null || ZNetScene.instance == null) return;
        if (s_pass != null) { CarveSome(s_pass); return; }
        if (Time.unscaledTime < s_next) return;
        s_next = Time.unscaledTime + 0.5f;
        var maps = Heightmap.GetAllHeightmaps();
        if (maps == null || maps.Count == 0) return;
        int version = RoadSpatialGrid.RoadNetworkVersion;
        ReleaseWaiting(version);
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
            // A zone with road or any structure in reach is looked at; a zone explored before
            // the road network keeps its rocks (NetworkCutoff).
            if (!RoadRockPolicy.ZoneNeedsLook(zone, candidate.transform.position)) { MarkDone(candidate, version); continue; }
            // Not yet: every saved object round the zone must have its instance
            // (the game creates them over several frames) and the terrain its
            // collider, and its road terrain must be written, or the road is
            // judged on the ungraded ground. Looked at again when it is ready.
            if (s_retryAt.TryGetValue(candidate.GetInstanceID(), out float retry) && Time.unscaledTime < retry) { RoadRockStats.NotReady++; continue; }
            if (!TerrainWritten(candidate, candidate.transform.position) ||
                !ZNetScene.instance.IsAreaReady(candidate.transform.position)) { RoadRockStats.NotReady++; continue; }
            map = candidate;
        }
        if (map == null) return;
        // Objects created or moved since the last physics step are not where the
        // overlap queries look until the transforms are synced (the server's
        // pass for peers does the same).
        long sync = RoadRockStats.Now;
        Physics.SyncTransforms();
        RoadRockStats.SyncTicks += RoadRockStats.Now - sync;
        s_carves.Clear();
        s_carveNext = 0;
        var lines = RoadRockProbe.RunPass(map.transform.position, RoadRockPolicy.ZoneCornerReach, s_carves, out int cleared);
        var pass = new Pass
        {
            Map = map, Zone = zone, Line = lines[0], Version = version, Cleared = cleared,
            GroundMisses = RoadRockProbe.LastGroundMisses, NotOwner = RoadRockProbe.LastNotOwner,
        };
        // The carves go over the next frames; the zone is judged once they are done.
        if (s_carves.Count > 0) { s_pass = pass; return; }
        Finish(pass);
    }

    /// <summary>Whether a zone's road terrain is written, the one owner of the test: its heightmap <paramref name="map"/> has
    /// no rebuild queued, and the zone at <paramref name="zonePos"/> owes no road write
    /// (<see cref="RoadTerrainModifier.TerrainSettled"/>: none reaches it, or its compiler has it). The pass and the carve's
    /// fragment ground ask it alike; the pass waits for the zone's objects too. The one owner of terrain settled, and
    /// whether the world is held (docs/OWNERS.md).</summary>
    internal static bool TerrainWritten(Heightmap map, Vector3 zonePos) =>
        !map.HaveQueuedRebuild() && RoadTerrainModifier.TerrainSettled(zonePos);

    /// <summary>This frame's share of the pending carves, then the zone judged once the last is done. A zone unloaded
    /// meanwhile (its heightmap gone) drops what is left: its next heightmap is a new zone to the pass.</summary>
    private static void CarveSome(Pass pass)
    {
        if (pass.Map == null)
        {
            s_carves.Clear(); s_carveNext = 0; s_pass = null;
            return;
        }
        long started = RoadRockStats.Now;
        // As the pass: objects created or moved since the last physics step are synced first.
        Physics.SyncTransforms();
        RoadRockStats.SyncTicks += RoadRockStats.Now - started;
        Vector3 centre = pass.Map.transform.position;
        float dropRadius = RoadRockProbe.DropRadius(RoadRockPolicy.ZoneCornerReach);
        int dropsBefore = RoadRockCarve.DropsNear(centre, dropRadius);
        int done = 0;
        while (s_carveNext < s_carves.Count &&
               RoadRockPolicy.CarvesMoreThisFrame(done, RoadRockStats.Ms(RoadRockStats.Now - started), ServerTerrainBake.FrameBudgetMs))
        {
            switch (RoadRockProbe.CarveDeferred(s_carves[s_carveNext++]))
            {
                case RoadRockProbe.DeferredOutcome.Carved:
                case RoadRockProbe.DeferredOutcome.RemovedWhole:
                    pass.Cleared++;
                    break;
                case RoadRockProbe.DeferredOutcome.Skipped:
                    pass.Skipped++;
                    break;
            }
            done++;
        }
        RoadRockProbe.WarnIfDropsRose(centre, dropsBefore, RoadRockCarve.DropsNear(centre, dropRadius));
        RoadRockStats.EndCarveFrame(started);
        if (s_carveNext < s_carves.Count) return;
        s_carves.Clear(); s_carveNext = 0; s_pass = null;
        Finish(pass);
    }

    /// <summary>The zone after its pass and carves: done only when the ground answered at every road point, nothing was
    /// left for another peer that owns it (or skipped since the pass), and nothing round it will still move.</summary>
    private static void Finish(Pass pass)
    {
        int version = pass.Version;
        Heightmap map = pass.Map;
        Vector2s zone = pass.Zone;
        bool notOwner = pass.NotOwner > 0 || pass.Skipped > 0;
        float settledBy = Time.time;
        int unsimulated = 0;
        int settling = pass.GroundMisses == 0 && !notOwner ? Settling(map.transform.position, null, out settledBy, out unsimulated) : 0;
        string skipped = pass.Skipped > 0 ? $" ({pass.Skipped} carve(s) skipped: the rock went or passed to another peer)" : "";
        var verdict = RoadRockPolicy.JudgeZone(pass.GroundMisses, notOwner, settling, unsimulated);
        if (verdict == RoadRockPolicy.ZoneVerdict.Done)
        {
            MarkDone(map, version);
            s_retryAt.Remove(map.GetInstanceID());
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug($"Road rocks: zone {zone} done: {pass.Line}");
        }
        else if (verdict == RoadRockPolicy.ZoneVerdict.WaitForPlayerZone)
        {
            // Zones already waiting from another player zone are released first, so every waiter shares one origin.
            ReleaseWaiting(version);
            if (s_retryAt.Count > 1024) { s_retryAt.Clear(); s_waiting.Clear(); }
            s_waitedFrom = PlayerZone();
            s_waitedVersion = version;
            s_retryAt[map.GetInstanceID()] = float.PositiveInfinity;
            s_waiting.Add(map.GetInstanceID());
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug($"Road rocks: zone {zone} waits for the player's zone to change " +
                $"({unsimulated} object(s) to fall or push up outside the active area round the player, which the game does not move until it is in it){skipped}: {pass.Line}");
        }
        else
        {
            if (s_retryAt.Count > 1024) { s_retryAt.Clear(); s_waiting.Clear(); }
            // Objects still to fall move on their own schedule (a first check 20 s
            // after they wake): look again once they have, not every retry delay.
            float wait = settling > 0 ? Mathf.Clamp(settledBy - Time.time, RetryDelay, 30f) : RetryDelay;
            s_retryAt[map.GetInstanceID()] = Time.unscaledTime + wait;
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug($"Road rocks: zone {zone} again later ({pass.GroundMisses} road points without ground" +
                $"{(notOwner ? ", a rock owned elsewhere" : "")}{(settling > 0 ? $", {settling} object(s) still to fall or push up, again in {Mathf.Clamp(settledBy - Time.time, RetryDelay, 30f):F1} s" : "")}{(unsimulated > 0 ? $", {unsimulated} more outside the active area round the player" : "")}){skipped}: {pass.Line}");
        }
        if (pass.Cleared > 0)
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo($"Road rocks: cleared {pass.Cleared} in zone {zone}");
    }

    /// <summary>The zone of ZNet's reference position: the one StaticPhysics is simulated round (SlowUpdater).</summary>
    private static Vector2s PlayerZone() => ZoneSystem.GetZone(ZNet.instance != null ? ZNet.instance.GetReferencePosition() : Vector3.zero);

    /// <summary>Lets the zones waiting for the player's zone be looked at again once it or the network changed.</summary>
    private static void ReleaseWaiting(int version)
    {
        if (s_waiting.Count == 0 || !RoadRockPolicy.ReleasesWaiting(s_waitedFrom, s_waitedVersion, PlayerZone(), version)) return;
        foreach (int id in s_waiting) s_retryAt.Remove(id);
        s_waiting.Clear();
    }

    /// <summary>
    /// Objects round a zone that the game will still move: StaticPhysics drops
    /// an object that sits above its fall height (and lifts one below the
    /// ground) on its first check, 20 s after it wakes, and on every check
    /// after. Cutting a road lowers the ground, so a rock beside the cut drops
    /// into the road seconds after the zone loads (measured: 3 m, into 12 road
    /// points, 9 s after load, after the pass had found the road clear). A zone
    /// is finished only when nothing round it will move. Same fall height as
    /// StaticPhysics.GetFallHeight. Only objects the game simulates now count
    /// (RoadRockPolicy.HoldsZone); one outside the active area round the player
    /// is counted in <c>unsimulated</c> instead, since the game does not move it
    /// until the player comes closer.
    /// </summary>
    private static int Settling(Vector3 zoneCentre, out int unsimulated) => Settling(zoneCentre, null, out _, out unsimulated);

    /// <summary>StaticPhysics.m_updateTime: when an object's first fall check is due (Time.time).</summary>
    private static readonly System.Reflection.FieldInfo? s_updateTime =
        typeof(StaticPhysics).GetField("m_updateTime", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    /// <param name="settledBy">Time.time by which every one of them will have moved: their first
    /// check (m_updateTime) is past, plus the fall itself (0.2 m every 0.05 s).</param>
    private static int Settling(Vector3 zoneCentre, List<string>? named, out float settledBy, out int unsimulated)
    {
        settledBy = Time.time;
        unsimulated = 0;
        Vector2s playerZone = PlayerZone();
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
            bool moves = physics.IsFalling || FallTarget(physics, out to);
            if (!moves) continue;
            // StaticPhysics.SUpdate's own condition, round the zone SlowUpdater passes it: the area decides only whether
            // a fall starts; one already falling runs on outside it.
            bool simulated = physics.IsFalling || !ZNetScene.OutsideActiveArea(physics.transform.position, playerZone);
            if (RoadRockPolicy.HoldsZone(moves, simulated))
            {
                count++;
                float due = s_updateTime != null ? (float)s_updateTime.GetValue(physics) : Time.time + 20f;
                float fall = Mathf.Abs(physics.transform.position.y - (physics.IsFalling ? physics.transform.position.y : to)) / 0.2f * 0.05f;
                settledBy = Mathf.Max(settledBy, Mathf.Max(due, Time.time) + fall + 0.5f);
            }
            else unsimulated++;
            if (named != null && named.Count < 12)
            {
                Vector3 p = physics.transform.position;
                named.Add(System.FormattableString.Invariant(
                    $"  settling: {RoadRockCarve.PrefabName(view.gameObject, view)} at ({p.x:F1},{p.y:F2},{p.z:F1}) -> y {(physics.IsFalling ? p.y : FallTarget(physics, out float t2) ? t2 : p.y):F2}{(physics.IsFalling ? " (falling)" : "")}{(simulated ? "" : " (outside the active area)")} fall={physics.m_fall} pushUp={physics.m_pushUp} checkSolids={physics.m_checkSolids}"));
            }
        }
        return count;
    }

    /// <summary>
    /// Where StaticPhysics will move an object on its next check, if anywhere:
    /// down to its fall height (the game's own StaticPhysics.GetFallHeight: the solid
    /// height round it with m_checkSolids, else the ground) when it is above it, or up
    /// to the ground when it is below it (PushUp, copied: the game's moves the object).
    /// False when it stays put.
    /// </summary>
    internal static bool FallTarget(StaticPhysics physics, out float y)
    {
        Vector3 p = physics.transform.position;
        y = p.y;
        float floor = physics.GetFallHeight();
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
        lines.Add($"zone {zone}: roads available {RoadLifecycleManager.HasNetwork}, network {version}, road points in zone {RoadSpatialGrid.GetRoadPointsInZone(zone).Count}");
        if (map == null) { lines.Add("no heightmap here"); return lines; }
        bool listed = Heightmap.GetAllHeightmaps().Contains(map);
        bool done = s_done.TryGetValue(map.GetInstanceID(), out int doneFor);
        lines.Add($"heightmap {map.GetInstanceID()} in the pass's list {listed}, distant LOD {map.IsDistantLod}, rebuild queued {map.HaveQueuedRebuild()}, " +
                  $"area ready {(ZNetScene.instance != null && ZNetScene.instance.IsAreaReady(map.transform.position))}, terrain settled {RoadTerrainModifier.TerrainSettled(map.transform.position)}, " +
                  $"done {(done ? (doneFor == version ? "yes" : $"for network {doneFor}") : "no")}, still to settle {Settling(map.transform.position, out int outside)} (and {outside} outside the active area round the player, " +
                  $"waiting {s_waiting.Contains(map.GetInstanceID())}); {s_done.Count} zones marked; " +
                  $"{Heightmap.GetAllHeightmaps().Count} heightmaps listed, cursor {s_cursor}");
        Settling(map.transform.position, lines, out float by, out _);
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
