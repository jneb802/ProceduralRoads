using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// In every generation (at world load, road_generate, road_regen_island), between choosing the network's destinations
/// and building it: the server places the zones of the destinations whose landing only placement settles
/// (<see cref="PlacedSites.NeedsPlacing(ZoneSystem.LocationInstance, WorldGenerator)"/>), as Valheim does for zones near a player
/// (ZoneSystem.SpawnZone, Ghost), reads what each placement settled, and only then builds the
/// roads, all under the loading panel. The game turns such a
/// place as its zone is placed, from its unseeded Random: nothing before can say which way its
/// landing faces. A place's prefab and its zone's terrain load over frames, so the placing is
/// spread over frames: waited for on the main thread, 21 zones never came and the load grew by
/// 276 s. It runs as steps of the generation's preparation
/// (<see cref="RoadNetworkGenerator.SitesFirst"/>, <see cref="RoadPreparation"/>), each with its own
/// stage and count on the loading panel: <see cref="ListingStage"/>, <see cref="PlacingStage"/>
/// (one try a frame) and <see cref="ReadingStage"/>; then the generation rebuilds their ground
/// (<see cref="PlacedSites.PrepareSteps"/>). A place that is unique or shows a map icon once placed is never
/// a destination (<see cref="PlacedSites.PlayerDecides"/>), so never placed here; Hildir's quest sites are, where
/// the roads go to them, and their icons stay off the map until a player comes near (<see cref="HiddenSiteIcons"/>).
/// </summary>
public static class SitesFirstLoad
{
    /// <summary>The loading panel's stages, in order.</summary>
    public const string ListingStage = "Finding places to place before their roads";
    public const string PlacingStage = "Placing places before their roads";
    public const string ReadingStage = "Reading placed places";

    private static readonly List<(Vector2 centre, Vector2s zone)> s_pending = new();
    private static readonly List<(Vector2 centre, Vector2s zone)> s_placing = new();
    private static double s_mainMs;
    private static int s_frames;

    public static void Install()
    {
        RoadNetworkGenerator.SitesFirst = Steps;
        RoadNetworkGenerator.OnReset += Reset;
    }

    /// <summary>
    /// The generation's chosen destinations that are placed first listed, placed over frames (one try a
    /// frame for each, until every one is placed) and read, before the roads are built. No time limit (the
    /// loading panel has a cancel button): with the 120 s cap a place still waiting was given no road, and placing every Mountain
    /// destination takes longer; the loading panel's Cancel ends the generation.
    /// </summary>
    internal static IEnumerable<bool> Steps(RoadNetworkGenerator.GenerationWork work, RoadPreparation preparation)
    {
        Reset();
        if (!RoadLifecycleManager.HoldsTheNetwork || ZoneSystem.instance == null || WorldGenerator.instance == null) yield break;
        var selected = work.AllSelected.ToList();
        preparation.BeginStage(ListingStage, selected.Count, "destinations"); yield return true;
        int already = 0;
        foreach (var (_, position, _) in selected)
        {
            List(position, ref already);
            if (preparation.Checkpoint()) yield return true;
        }
        if (RoadNetworkGenerator.RoadsLog) ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(
            $"[SITES] before the roads: {s_pending.Count} place(s) to place, {already} already placed");

        if (s_pending.Count > 0)
        {
            preparation.BeginStage(PlacingStage, s_pending.Count, "places"); yield return true;
            float started = Time.realtimeSinceStartup;
            while (true)
            {
                PlaceOnce(preparation);
                if (s_pending.Count == 0) break;
                // A place's prefab and its zone's terrain load between frames: the next try is next frame.
                preparation.EndSlice();
                yield return true;
            }
            float wall = Time.realtimeSinceStartup - started;
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(FormattableString.Invariant(
                $"[SITES] before the roads: placed {s_placing.Count} place(s) in {wall:F1} s of placing ({s_mainMs:F0} ms of main thread over {s_frames} frame(s))"));
        }

        // Reading a place asks the site footprint index (its keep-out edge): built here over frames, not in the first
        // read's one frame.
        if (s_placing.Count > 0)
            foreach (var step in RoadPreparation.Own(LocationPrefabLevelling.PrimeFootprintSteps(preparation))) yield return step;

        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        int read = 0, count = s_placing.Count;
        if (count > 0) { preparation.BeginStage(ReadingStage, count, "places"); yield return true; }
        foreach (var (centre, zone) in s_placing.ToList())
        {
            if (Read(centre, zone)) read++;
            else PlacedSites.NoteWhyNotPlaced(centre, "placed, but what its placing settled could not be read");
            preparation.Checkpoint(); yield return true;
        }
        if (RoadNetworkGenerator.RoadsLog) ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(FormattableString.Invariant(
            $"[SITES] before the roads: read {read} of {count} placed place(s) (turn, root, parts, rocks) in {Ms(began):F0} ms"));
        s_placing.Clear();
    }

    /// <summary>One destination: placed first, already placed (read only), or neither.</summary>
    private static void List(Vector3 position, ref int already)
    {
        var centre = new Vector2(position.x, position.z);
        Vector2s zone = ZoneSystem.GetZone(position);
        if (!ZoneSystem.instance.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance instance)) return;
        if (Vector2.Distance(new Vector2(instance.m_position.x, instance.m_position.z), centre) > 1f) return;
        // A zone explored before the road network is left exactly as it is (NetworkCutoff): nothing is laid
        // onto a landing there, so its place gets no road (PlacedSites.NotPlaced).
        if (NetworkCutoff.Excludes(zone)) { PlacedSites.NoteWhyNotPlaced(centre, PlacedSites.ZoneLeftAlone); return; }
        if (!PlacedSites.NeedsPlacing(instance, WorldGenerator.instance)) return;
        if (PlacedSiteReader.Placed(zone, centre, out _)) { already++; s_placing.Add((centre, zone)); return; }
        if (instance.m_placed || ZoneSystem.instance.IsZoneGenerated(zone))
        {
            PlacedSites.NoteWhyNotPlaced(centre, "its zone is generated, but no placing of it was found there to read");
            return;
        }
        s_pending.Add((centre, zone));
    }

    /// <summary>One frame of placing: each place not yet placed tried once.</summary>
    private static void PlaceOnce(RoadPreparation preparation)
    {
        long frame = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = s_pending.Count - 1; i >= 0; i--)
        {
            var (centre, zone) = s_pending[i];
            // Two destinations in one zone: placed for the first, it is only read for the second.
            // Asked again, SpawnZone makes the generated zone's root and hands it back to be taken
            // down (7872,1920).
            if (!ZoneSystem.instance.IsZoneGenerated(zone))
            {
                // Whatever the zone holds that the game shows on the map once placed stays off it until a player
                // comes as near as the game would have placed it from (HiddenSiteIcons, at ZoneSystem.PlaceLocations).
                bool spawned;
                GameObject root;
                using (HiddenSiteIcons.PlacingEarly()) spawned = ZoneSystem.instance.SpawnZone(zone, ZoneSystem.SpawnMode.Ghost, out root);
                if (!spawned) continue;
                if (root != null) UnityEngine.Object.Destroy(root);
            }
            s_pending.RemoveAt(i);
            s_placing.Add((centre, zone));
            preparation.CompleteUnit();
        }
        s_mainMs += Ms(frame);
        s_frames++;
    }

    /// <summary>What a placement settled, remembered for the roads (<see cref="PlacedSites"/>). Main thread.</summary>
    private static bool Read(Vector2 centre, Vector2s zone)
    {
        if (!PlacedSiteReader.Placed(zone, centre, out ZDO? proxy)) return false;
        float yaw = proxy!.GetRotation().eulerAngles.y, root = proxy.GetPosition().y;
        // A place that levels nothing (no terrain modifier) is read too: it has no flat, and is reached at its ring at the
        // height the game stood it (PlacedSites.FlatlessPlaced).
        IReadOnlyList<LevelOp>? ops = PlacedSiteReader.KeptOps(centre, proxy);
        if (ops == null) return false;
        float rock = PlacedSiteReader.RockRadius(centre);
        // The ground the road onto its landing may be laid over: out to the road's trim ring and round
        // its rock (a staircase from behind is led round it), and a margin beyond.
        float ring = RoadNetworkGenerator.DoorRing(centre, rock, RoadNetworkGenerator.RoadWidth, placed: true);
        float half = Mathf.Max(ring, rock + RoadStairRuns.RockMargin + RoadStairRuns.RockClearance) + PlacedSiteReader.Margin;
        int x0 = Mathf.FloorToInt(centre.x - half), z0 = Mathf.FloorToInt(centre.y - half), w = Mathf.CeilToInt(2f * half) + 1;
        string prefab = ZoneSystem.instance.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance placed) ? placed.m_location.m_prefab.Name : "?";
        // One that levels no flat but smooths round it is landed on the level ground beside it (PlacedSiteShape.OnLevelGround),
        // clear of its own pieces: read with its boulders.
        bool pieces = PlacedSites.FlatFor(prefab, ops) == null && PlacedSites.SmoothsItsGround(ops);
        List<SiteRock> rocks = PlacedSiteReader.PlaceRocks(x0, z0, w, w, centre, root, yaw, out _, pieces);
        PlacedSites.Remember(new PlacedSiteRecord
        {
            Prefab = prefab,
            Centre = centre, Yaw = yaw, Root = root, RockRadius = rock, KeepSmallRocks = PlacedSiteReader.KeepsEveryRock(centre),
            Ops = new List<LevelOp>(ops), Rocks = rocks, X0 = x0, Z0 = z0, W = w + 1, H = w + 1,
        });
        return true;
    }

    public static void Reset()
    {
        s_pending.Clear();
        s_placing.Clear();
        s_mainMs = 0;
        s_frames = 0;
    }

    private static double Ms(long began) => (System.Diagnostics.Stopwatch.GetTimestamp() - began) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
}
