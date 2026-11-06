using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Orchestrates road network generation after POI locations are known.
/// </summary>
public static partial class RoadNetworkGenerator
{
    private static ManualLogSource Log => ProceduralRoadsPlugin.ProceduralRoadsLogger;
    public static bool IsGenerating => RoadLifecycleManager.IsGenerating;
    
    /// <summary>The priority of a boss: the top tier of <see cref="LocationPriorities"/>, and a required destination.</summary>
    private const int BossPriority = 100;

    /// <summary>Whether a location is a boss (the only names at <see cref="BossPriority"/>).</summary>
    internal static bool IsBossLocation(string name) => LocationPriorities.TryGetValue(name, out int priority) && priority == BossPriority;

    // Destination priorities for the vanilla locations reviewed on Valheim 1.0.12.
    private static readonly Dictionary<string, int> LocationPriorities = new()
    {
        { "Bonemass", BossPriority },
        { "Dragonqueen", BossPriority },
        { "Eikthyrnir", BossPriority },
        { "GDKing", BossPriority },
        { "GoblinKing", BossPriority },
        { "Mistlands_DvergrBossEntrance1", BossPriority },

        { "Crypt2", 80 },
        { "Crypt3", 80 },
        { "Crypt4", 80 },
        { "Hildir_cave", 80 },
        { "Hildir_crypt", 80 },
        { "Hildir_plainsfortress", 80 },
        { "Mistlands_DvergrTownEntrance1", 80 },
        { "Mistlands_DvergrTownEntrance2", 80 },
        { "MountainCave02", 80 },
        { "SunkenCrypt4", 80 },

        { "Mistlands_Harbour1", 70 },

        { "GoblinCamp2", 60 },
        { "GoblinCamp2_1", 60 },
        { "WoodVillage1", 60 },
        { "WoodVillage2", 60 },

        { "WoodFarm1", 55 },

        { "Mistlands_GuardTower1_new", 50 },
        { "Mistlands_GuardTower2_new", 50 },
        { "Mistlands_GuardTower3_new", 50 },
        { "Mistlands_Lighthouse1_new", 50 },

        { "Mistlands_Excavation1", 45 },
        { "Mistlands_Excavation2", 45 },

        { "BearCave", 40 },
        { "StoneTower1", 40 },
        { "StoneTower3", 40 },
        { "TrollCave02", 40 },

        { "GoblinHut01", 35 },
        { "GoblinHut02", 35 },
        { "GoblinHut03", 35 },

        { "AbandonedLogCabin02", 30 },
        { "AbandonedLogCabin03", 30 },
        { "AbandonedLogCabin04", 30 },
        { "Mistlands_Excavation3", 30 },
        { "Mistlands_GuardTower1_ruined_new", 30 },
        { "Mistlands_GuardTower1_ruined_new2", 30 },
        { "Mistlands_GuardTower3_ruined_new", 30 },
        { "MountainWell1", 30 },
        { "Ruin1", 30 },
        { "Ruin2", 30 },
        { "Ruin3", 30 },
        { "StoneHouse3", 30 },
        { "StoneHouse4", 30 },
        { "StoneTowerRuins03", 30 },
        { "StoneTowerRuins04", 30 },
        { "StoneTowerRuins05", 30 },
        { "StoneTowerRuins05_leet", 30 },
        { "StoneTowerRuins07", 30 },
        { "StoneTowerRuins08", 30 },
        { "StoneTowerRuins09", 30 },
        { "StoneTowerRuins10", 30 },

        { "CombatRuin01", 25 },
        { "StoneHenge1", 25 },
        { "StoneHenge2", 25 },
        { "StoneHenge3", 25 },
        { "StoneHenge4", 25 },
        { "StoneHenge5", 25 },
        { "SwampRuin1", 25 },
        { "SwampRuin2", 25 },
    };
    
    private const int DefaultPriority = 20;
    /// <summary>Selection weight of a custom location (named by config or API).</summary>
    private const int CustomLocationPriority = 80;
    public static RoadNetworkOptions NetworkOptions { get; set; } = new();

    /// <summary>The shipped Roads.MaxLocationsPerIsland.</summary>
    public const int DefaultMaxLocationsPerIsland = 48;
    public static int MaxLocationsPerIsland = DefaultMaxLocationsPerIsland;


    /// <summary>
    /// Location names explicitly registered via the mod API.
    /// </summary>
    private static readonly HashSet<string> RegisteredLocationNames = new HashSet<string>();

    private static readonly HashSet<string> ConfiguredLocationNames = new();

    #region Location Registration API

    /// <summary>
    /// Register a location name for road generation.
    /// Call this from other mods to include custom locations in the road network.
    /// </summary>
    public static void RegisterLocation(string locationName)
    {
        if (string.IsNullOrWhiteSpace(locationName))
            return;
        
        string trimmed = locationName.Trim();
        if (DeferWhileGenerating(trimmed, true)) return;
        if (RegisteredLocationNames.Add(trimmed))
        {
            Log.LogDebug($"Registered location for roads: {trimmed}");
        }
    }

    /// <summary>
    /// Unregister a location name from road generation.
    /// </summary>
    public static void UnregisterLocation(string locationName)
    {
        if (string.IsNullOrWhiteSpace(locationName))
            return;
        
        string trimmed = locationName.Trim();
        if (DeferWhileGenerating(trimmed, false)) return;
        if (RegisteredLocationNames.Remove(trimmed))
        {
            Log.LogDebug($"Unregistered location from roads: {trimmed}");
        }
    }

    /// <summary>Registrations asked for while a generation runs: its island workers read the
    /// registered names, so the set cannot change under them. Another mod registering from a
    /// world-load hook now overlaps the asynchronous initial generation; it is applied when the
    /// generation ends (it takes effect from the next generation), never refused.</summary>
    private static readonly List<(string name, bool add)> m_pendingRegistrations = new();

    private static bool DeferWhileGenerating(string name, bool add)
    {
        if (!IsGenerating) return false;
        lock (m_pendingRegistrations) m_pendingRegistrations.Add((name, add));
        Log.LogDebug($"Road location {(add ? "registration" : "removal")} of {name} queued until road generation ends");
        return true;
    }

    /// <summary>Apply registrations queued while a generation ran. Called as it ends.</summary>
    internal static void ApplyPendingRegistrations()
    {
        (string name, bool add)[] pending;
        lock (m_pendingRegistrations) { pending = m_pendingRegistrations.ToArray(); m_pendingRegistrations.Clear(); }
        foreach (var (name, add) in pending)
            if (add) RegisterLocation(name); else UnregisterLocation(name);
    }

    /// <summary>
    /// Get all currently registered location names.
    /// </summary>
    public static IReadOnlyCollection<string> GetRegisteredLocations()
    {
        return RegisteredLocationNames.Union(ConfiguredLocationNames).ToArray();
    }

    #endregion

    public static float RoadWidth = RoadConstants.DefaultRoadWidth;
    /// <summary>The shipped Roads.IslandRoadPercentage.</summary>
    public const int DefaultIslandRoadPercentage = 33;
    public static int IslandRoadPercentage = DefaultIslandRoadPercentage;

    private static bool m_roadsGenerated = false;
    private static bool m_locationsReady = false;
    private static bool m_roadsLoadedFromZDO = false;
    /// <summary>The network in memory is not the saved one: generated this session, or appended to. Saved in full
    /// (<see cref="SaveGlobalRoadData"/>); a loaded, unchanged network saves only its bridge zones.</summary>
    private static bool m_networkDirty = false;
    private static RoadPathfinder? m_pathfinder;
    // Islands are isolated networks, so they are built on worker threads. A
    // pathfinder holds a terrain cache and LastPathCost, so each thread needs
    // its own; Finder hands out the calling thread's.
    private static System.Threading.ThreadLocal<RoadPathfinder>? m_threadPathfinders;
    private static RoadPathfinder? Finder =>
        t_finder ?? (m_threadPathfinders != null ? m_threadPathfinders.Value : m_pathfinder);
    /// <summary>The search a road planned outside generation is planned with on this thread (<see cref="PlanningWith"/>).</summary>
    [System.ThreadStatic] private static RoadPathfinder? t_finder;
    /// <summary>Where a road planned outside generation is handed over instead of stored (<see cref="PlanningWith"/>): the
    /// road the generator plans (GenerateRoad, with every search again it makes) is captured as it would be stored.</summary>
    [System.ThreadStatic] private static RoadCapture? t_capture;

    /// <summary>
    /// Plan roads on this thread as the generator plans them, outside generation and storing nothing: with
    /// <paramref name="finder"/>, each road GenerateRoad would store captured into <paramref name="capture"/> instead (a
    /// manual road stores it in its one append), and every refusal noted there. Until disposed. The staircases onto placed
    /// places' landings a generation found it could not lay (<see cref="m_entryStairsFailed"/>, <see cref="m_entryStairsTried"/>)
    /// are its own memo, for the network it was planning: forgotten here, so a road planned on the network as it is now lays
    /// them out afresh. Never while a generation runs (its workers read that memo): a manual road is refused then.
    /// </summary>
    internal static IDisposable PlanningWith(RoadPathfinder finder, RoadCapture capture)
    {
        if (RoadLifecycleManager.IsGenerating) throw new InvalidOperationException("A road is planned outside generation only while none runs.");
        m_entryStairsFailed.Clear();
        m_entryStairsTried.Clear();
        var (finderBefore, captureBefore) = (t_finder, t_capture);
        (t_finder, t_capture) = (finder, capture);
        return new Restore(() => (t_finder, t_capture) = (finderBefore, captureBefore));
    }

    private sealed class Restore : IDisposable
    {
        private Action? m_restore;
        public Restore(Action restore) => m_restore = restore;
        public void Dispose() { m_restore?.Invoke(); m_restore = null; }
    }

    /// <summary>What a road's planning outside generation tried (<see cref="PlanningWith"/>): each refusal, why and where
    /// (where it says), and the road planned, as it would be stored.</summary>
    internal sealed class RoadCapture
    {
        public readonly List<(string why, Vector2? at)> Refusals = new();
        public PlannedRoad? Road;
        public void Refused(string why, Vector2? at) => Refusals.Add((why, at));
    }

    /// <summary>A road as the generator planned it (GenerateRoadCore), before anything of it is stored.</summary>
    internal sealed class PlannedRoad
    {
        public Vector2 StartCenter, EndCenter;
        public List<Vector2> Path = new();
        public List<RoadSpatialGrid.PlannedPath> Plans = new();
        public List<RoadCrossing> Crossings = new();
        /// <summary>The staircases its search bought (some already laid, <see cref="RoadStairSpan.Existing"/>), and those
        /// laid at its places' ends.</summary>
        public List<RoadStairSpan> Stairs = new(), Approaches = new();
        /// <summary>Its ways onto placed places' landings (<see cref="PlanEntry"/>).</summary>
        public List<(PlacedSite site, RoadSpatialGrid.PlannedPath? road, RoadStairSpan? stairs)> Entries = new();
        /// <summary>Its ends carried on into their places (<see cref="CarryOnToEdges"/>).</summary>
        public List<RoadSpatialGrid.PlannedPath> Carried = new();
        /// <summary>Its ends arriving at their places as road: ways into them once it is committed (PlaceWaysIn.Record).</summary>
        public List<(Vector2 place, PlaceWaysIn.Way way)> WaysIn = new();
    }
    private static readonly object m_recordGate = new object();
    [System.ThreadStatic] private static int m_islandRoads;
    /// <summary>This island's network version: one more at every write to what a road's search, its build and the served
    /// test read (the grid's points, the staircases and their laid layouts, the viaducts laid, the places landed on, the
    /// crossings recorded, the places off their level, the join's hints). Each owner bumps it as it writes, on the thread
    /// that writes, which is the island's own worker: islands are disjoint, so another island's writes leave it alone.
    /// Unchanged between two calls, the network an island's roads read is the same (<see cref="GenerateRoutedRoads"/>'s
    /// join memo). Each value is taken once in the process (<see cref="s_networkVersions"/>), a thread's first read included,
    /// so it names one thread's network state only: the carry cache (<see cref="RoadStairRuns"/>' CarryKey) outlives the
    /// generation and is read on the main thread too (manual roads), where a per-thread count met a worker's. Its readers
    /// only compare it for equality.</summary>
    [System.ThreadStatic] private static int t_networkVersion;
    /// <summary>The last network version handed out in the process (<see cref="IslandNetworkVersion"/>); 0 is never one.</summary>
    private static int s_networkVersions;
    internal static int IslandNetworkVersion =>
        t_networkVersion != 0 ? t_networkVersion : t_networkVersion = System.Threading.Interlocked.Increment(ref s_networkVersions);
    internal static void IslandNetworkChanged() => t_networkVersion = System.Threading.Interlocked.Increment(ref s_networkVersions);
    [System.ThreadStatic] internal static RoadIslandActivity.Token? IslandActivity;
    /// <summary>How many of an island's destinations the finished network
    /// reaches. Measured rather than tallied from what the planner attempted:
    /// a destination can be reached by a road built to its neighbour, and a
    /// road refused between two touching sites can leave one reached anyway
    /// because the network already runs through its footprint. Counting
    /// attempts answers neither.
    ///
    /// Islands are disjoint, so this is stable while other islands commit.</summary>
    private static void CountDestinationsReached(Island island,
        List<(string name, Vector3 position, float radius)> selected)
    {
        RecordCoastalLandings(island, selected);
        bool[] served = ServedDestinations(selected);
        int reached = 0; foreach (bool s in served) if (s) reached++;
        if (RoadsLog) LogUnconnected(selected);
        IslandActivity?.Measured(served);
        System.Threading.Interlocked.Add(ref m_destinationsSelected, selected.Count);
        System.Threading.Interlocked.Add(ref m_destinationsConnected, reached);
    }

    /// <summary>Notes each of the island's shore landings with whether its destinations took it and whether the network
    /// reaches it: the landings that were not chosen too, a road passing one reaches it.</summary>
    private static void RecordCoastalLandings(Island island, List<(string name, Vector3 position, float radius)> selected)
    {
        var places = new List<CoastalLandingPlace>(island.CoastalLandings.Count);
        foreach (Vector3 p in island.CoastalLandings)
        {
            bool chosen = selected.Exists(d => d.name == RoadCoastalLandings.Name && d.position.Equals(p));
            places.Add(new CoastalLandingPlace(p, island.Id, chosen, Reached(p, RoadCoastalLandings.Radius, RoadCoastalLandings.Name)));
        }
        lock (m_recordGate)
        {
            m_coastalLandings.RemoveAll(l => l.IslandId == island.Id);
            m_coastalLandings.AddRange(places);
        }
    }

    /// <summary>Marks a place done, noting whether the network serves it. Called by the island's serial planner.</summary>
    internal static void SettleDestination(List<(string name, Vector3 position, float radius)> locations, int index)
    {
        var activity=IslandActivity;
        if(activity==null || index<0 || index>=locations.Count) return;
        var d=locations[index];
        activity.Settled(index, Reached(d.position,d.radius,d.name));
    }

    /// <summary>With <see cref="RoadsLog"/>: every destination the network does not reach, and how its last road attempt failed.</summary>
    private static void LogUnconnected(List<(string name, Vector3 position, float radius)> selected)
    {
        foreach (var d in selected)
        {
            if (Reached(d.position, d.radius, d.name)) continue;
            var key = PlaceKey(new Vector2(d.position.x, d.position.z));
            string last = m_builtTo.ContainsKey(key)
                ? "a road was built to it but stops short" + (m_lastFailure.TryGetValue(key, out string? f0) ? $" (a refused attempt: {f0})" : "")
                : m_lastFailure.TryGetValue(key, out string? f) ? f : "never attempted";
            string stairs = m_stairsFailed.TryGetValue(key, out string? st) ? $"; stairs tried: {st}" : "; stairs not tried";
            if (PlacedSites.TryGetReady(new Vector2(d.position.x, d.position.z), out PlacedSite placed))
                stairs += NearestToLanding(placed);
            Log.LogInfo(FormattableString.Invariant($"  unconnected: {d.name} at ({d.position.x:F0}, {d.position.z:F0}): {last}{stairs}"));
        }
    }

    private static void RefreshDestinationsReached(List<(string name, Vector3 position, float radius)> selected)
    {
        var activity=IslandActivity;
        if(activity!=null && activity.ReachSampleDue()) activity.Measured(ServedDestinations(selected));
    }

    private static bool[] ServedDestinations(List<(string name, Vector3 position, float radius)> selected)
    {
        var served=new bool[selected.Count];
        for(int i=0;i<selected.Count;i++) served[i]=Reached(selected[i].position,selected[i].radius,selected[i].name);
        return served;
    }

    /// <summary><see cref="Reached(Vector2, float, string?)"/> for a place's world position.</summary>
    internal static bool Reached(Vector3 centre, float radius, string? name = null) => Reached(new Vector2(centre.x, centre.z), radius, name);

    /// <summary>How far past a place's keep-out edge (<see cref="KeepOutEdge"/>) the network may run and still reach
    /// it: one path cell, the search's own resolution.</summary>
    internal const float ReachPastEdge = RoadPathfinder.CellSize;

    /// <summary>
    /// How far over or under a place's landing the network may arrive and still be level with it (a road always ends
    /// level, even at the cost of a POI, and may end in stairs if needed): half a stair's rise
    /// (<see cref="StairLayout.PieceRise"/>). Off by more, one stair is the nearer fit and the stairs builder lays it; off by less, no stair fits and it is the ground's own unevenness, which a landing
    /// is allowed too (<see cref="PlacedSiteShape.LevelTolerance"/>, and a stair top's own <see cref="StairLayout.ReachHeight"/>).
    /// The one level for the reached test (<see cref="Reached"/>), a road's end at its place, an approach staircase's
    /// threshold and the landing's level ground (<see cref="PlacedSiteShape.Aims"/>).
    /// </summary>
    public const float ArrivalLevel = StairLayout.PieceRise / 2f;

    /// <summary>
    /// How far over or under the place's own level (<see cref="ArrivalHeight"/>) the network may end and still reach it: the one
    /// height for "reached" (<see cref="Reached"/>, <see cref="LandingReached"/>) and for refusing a road whose end does not
    /// (<see cref="EndNotLevel"/>). About a step (a road ending more than about a step under or over the place's
    /// ground is not reached; it gets an approach or the place goes unserved). 1.5 m: a stair and a half
    /// (<see cref="StairLayout.PieceRise"/>), which the player walks up over the ground between a road's end and the place's
    /// edge (at least half a road's width, its blend and a path cell, 4 m or more: under 21 degrees). More is a climb: a
    /// staircase is laid onto it where one can be (from <see cref="ArrivalLevel"/> off, where a stair fits), and without one
    /// the end does not reach it. Before, a place with no single levelled flat (AbandonedLogCabin04, which only smooths) had no
    /// known height and was reached by a road 10 m under it, and outside the Mountains every place was reached at any height.
    /// </summary>
    public const float ReachRise = 1.5f;

    /// <summary>
    /// Whether an approach staircase is laid onto the place at <paramref name="centre"/> (its prefab <paramref name="prefab"/>,
    /// null where not known) where a road's end does not come level with it (<see cref="SiteApproaches"/>, and onto a placed
    /// place's landing, <see cref="PlanEntryFrom"/>): at a boss's entrance anywhere (<see cref="OwnLandingOnly"/>), and at
    /// every other place only in the Mountains, by the biome at its centre. Everywhere the network must first end level with
    /// the place to reach it (<see cref="ReachRise"/>, <see cref="Reached"/>): elsewhere an end not level and with no road onto
    /// its landing is refused (<see cref="EndNotLevel"/>); a place outside the Mountains left unserved that way then falls back
    /// to a road ending off its level (<see cref="ReachOffLevel"/>). The one owner of that choice. From a road's end at
    /// <paramref name="fromHeight"/> (null where not known), none down to a custom location standing below it (its own level,
    /// <see cref="CustomLevel"/>, more than <see cref="ArrivalLevel"/> under the end, where a stair would fit): its first
    /// attempt leaves it unserved, and its fallback ends the road at its ring off its level, with no stand-off
    /// (<see cref="StandOffLevel"/> stands an end off only below the place). A staircase down onto such a place's flat ended
    /// in the rocks the place keeps round it.
    /// </summary>
    internal static bool ApproachStairsAt(Vector2 centre, string? prefab, float? fromHeight = null)
    {
        if (prefab != null && OwnLandingOnly(prefab)) return true;
        // Every place on the fallback (HeightNeeded false, OffLevelAllowed): the road ends at its ring, no staircase.
        if (!HeightNeeded(centre)) return false;
        if (fromHeight is float from && CustomLevel(centre, prefab, out _) is float own && from - own > ArrivalLevel) return false;
        var world = GenerationWorld;
        return world == null || world.GetBiome(centre.x, centre.y) == Heightmap.Biome.Mountain;
    }

    /// <summary>Whether a road may reach a placed place at its ring: not where it takes a staircase (<see cref="ApproachStairsAt"/>),
    /// nor a place with a door (<see cref="PlacedSiteShape.HasDoor"/>: only a road to its door reaches it). The reached test
    /// (<see cref="Reached"/>) and the refusal of a road kept at a ring both read it.</summary>
    private static bool RingEnterable(PlacedSite site) =>
        !ApproachStairsAt(site.Centre, site.Record.Prefab) && !PlacedSiteShape.HasDoor(site.Record.Prefab);

    /// <summary>The one test of whether the network reaches the place at <paramref name="flat"/> (exterior
    /// <paramref name="radius"/>, as asked, not trimmed), measured at the trim's keep-out edge (<see cref="KeepOutEdge"/>):
    /// for the count, the planner's join (<see cref="JoinPlace"/>) and whether a road's own stub to the place is dropped
    /// where it merges (<see cref="EndReached"/>).
    ///
    /// Reached only where the network ends level with the place (<see cref="ReachRise"/> of <see cref="ArrivalHeight"/>, the
    /// place's own level, or its ground at its edge on the network's side), by road or by a staircase's walked piece, its top or foot, in every biome. A placed place is reached
    /// on its landing (<see cref="LandingReached"/>), and outside the Mountains (<see cref="ApproachStairsAt"/>) at its ring as
    /// well, level with its landing, but for a place with a door (a crypt, <see cref="PlacedSiteShape.DoorLandings"/>): only
    /// a road that reaches its door reaches it. A place that waits for its placing and was not placed before its roads is never reached
    /// (<see cref="PlacedSites.NotPlaced"/>). A point with no radius (a junction) is reached at any height, and so is a
    /// place on the fallback, on its landing or at its ring (<see cref="HeightNeeded"/>, <see cref="ReachOffLevel"/>), but not by
    /// network a ridge stands between it and the place (<see cref="BehindRidge"/>).</summary>
    internal static bool Reached(Vector2 flat, float radius, string? name = null)
    {
        // A place placed before its roads is reached on its landing, not at its ring.
        // A boss's entrance only by a road onto its own landing (OwnLandingOnly): placed, once one was carried onto it;
        // not placed, never by a road passing within reach (the count and the planner's join alike).
        // A place with a door only on its door landing (PlacedSiteShape.DoorLandings: a crypt's road reaches its door).
        // At any height only on the fallback, its first attempt at height having left it unserved (HeightNeeded).
        bool height = HeightNeeded(flat);
        // A custom location reached without its own height stands its road's end off (StandOffLevel): on the fallback, or
        // with no flat nor landing.
        float? standOff = radius > 0f ? StandOffLevel(flat, radius, name) : null;
        // On the fallback, at any height, but not from behind a ridge (BehindRidge).
        Func<Vector2, float, bool>? clear = radius > 0f && !height ? (q, h) => !BehindRidge(flat, radius, q, h) : null;
        // At its ring, and only there (not on its landing), a custom location standing its end off is reached at its stand-off
        // (AtStandOff, the cheap test first).
        Func<Vector2, float, bool>? atRing = clear != null ? (q, h) => OffLevelAt(flat, radius, standOff, q, h) : null;
        float fallbackReach = radius > 0f ? FallbackReach(flat, radius, standOff) : 0f;
        if (radius > 0f && PlacedSites.TryGetReady(flat, out PlacedSite placed))
        {
            if (OwnLandingOnly(placed.Record.Prefab)) return PlacedSites.Landed(placed.Centre);
            if (height ? LandingReached(placed) : PlacedSites.Landed(placed.Centre) || NetworkNear(placed.Shape.Landing, LandingReach, clear!, true))
                return true;
            if (!RingEnterable(placed)) return false;
            // At its ring only on the side it is entered from (PlacedSiteShape.OnEntranceSide): a Dvergr town entrance's back
            // is blocked rock.
            PlacedSiteShape shape = placed.Shape;
            Func<Vector2, float>? level = height ? ArrivalLevelFrom(flat, radius) : null;
            Func<Vector2, float, bool> ring = height
                ? (q, h) => shape.OnEntranceSide(q) && (level == null || Mathf.Abs(h - level(q)) <= ReachRise)
                : (q, h) => shape.OnEntranceSide(q) && atRing!(q, h);
            return NetworkNear(flat, height ? KeepOutEdge(flat, radius, RoadWidth) + ReachPastEdge : fallbackReach, ring, !height);
        }
        // One that waits for its placing and was not placed before its roads gets no road
        // (PlacedSites.NotPlaced), nor is it reached by one passing within reach of its ring.
        if (radius > 0f && PlacedSites.NotPlaced(flat)) return false;
        if (name != null && OwnLandingOnly(name)) return false;
        float reach = radius > 0f
            ? KeepOutEdge(flat, radius, RoadWidth)
            : radius;
        // A staircase is the network too: a road that reaches a place by one ends on its top or foot,
        // with no road there (MountainCave02 -2560,1536: its staircase's top 38.8 m out, the
        // nearest road 52 m, counted unreached).
        if (atRing != null) return NetworkNear(flat, fallbackReach, atRing, true);
        // With no flat nor landing, at height a custom location stands its road's end off where it stands above it; one
        // under the network is reached level with its edge, as every other place.
        if (standOff is float own && ArrivalLevelFrom(flat, radius) is Func<Vector2, float> edge)
            return NetworkNear(flat, fallbackReach, (q, h) => h >= own
                ? (q - flat).sqrMagnitude <= (reach + ReachPastEdge) * (reach + ReachPastEdge) && Mathf.Abs(h - edge(q)) <= ReachRise
                : OffLevelAt(flat, radius, standOff, q, h), false);
        return NetworkNear(flat, reach + ReachPastEdge, radius > 0f ? ArrivalLevelFrom(flat, radius) : null);
    }

    /// <summary>Whether a road must end level with the place at <paramref name="centre"/> to reach it (<see cref="ReachRise"/>,
    /// <see cref="Reached"/>), the one owner behind the reached test, the refusal of a road whose end is not
    /// (<see cref="EndNotLevel"/>) and the search to level doors only (<see cref="OnlyLevelDoors"/>): everywhere, but at a
    /// place whose first attempt, at height, left it unserved and which is tried again without it
    /// (<see cref="ReachOffLevel"/>, in the Mountains only a custom location). Mountains strict; everywhere else first arrive at
    /// height, and only where no road can, fall back to the road ending at the place's ring off its level, the place reached.
    /// Before, the Mistlands needed no height at all, and then, tried, nowhere but the Mountains.</summary>
    internal static bool HeightNeeded(Vector2 centre) => !m_offLevel.ContainsKey(PlaceKey(centre));

    /// <summary>The places reached without height (<see cref="ReachOffLevel"/>), by rounded centre (<see cref="PlaceKey"/>):
    /// while a place's second attempt runs, and after it for good where it reached the place.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), bool> m_offLevel = new();
    /// <summary>The custom locations among the destinations (<see cref="IsCustomPlace"/>), by rounded centre, for the
    /// tests that know a place by its centre only (a road's trim, <see cref="OffLevelStandOff"/>): noted as an island's
    /// destinations are planned (<see cref="GenerateRoutedRoads"/>) and as a place falls back (<see cref="ReachOffLevel"/>).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), bool> m_customPlaces = new();
    /// <summary>Notes the place <paramref name="name"/> at <paramref name="position"/> among <see cref="m_customPlaces"/> where it is a custom location.</summary>
    internal static void NoteCustomPlace(string name, Vector3 position)
    {
        if (IsCustomPlace(name)) m_customPlaces[PlaceKey(new Vector2(position.x, position.z))] = true;
    }

    /// <summary>
    /// The level a road's end at the place at <paramref name="centre"/> (<paramref name="name"/>, null where only its centre
    /// is known) stands off from, or null where it does not: a custom location's in any biome (<see cref="IsCustomPlace"/>) reached
    /// or ended at without its own height, on the fallback (<see cref="HeightNeeded"/>) or with no flat nor landing (not placed
    /// first, no single levelled platform, <see cref="LocationLevelling.ApproachHeight"/>: its ground at its edge is all the
    /// reached test reads, the foot of the rock a fort stands on). Its own level: its landing's ground placed first, its flat,
    /// else its root (<see cref="LocationLevelling.CentreHeight"/>). Null for every vanilla place.
    /// </summary>
    private static float? StandOffLevel(Vector2 centre, float radius, string? name = null)
    {
        if (radius <= 0f || CustomLevel(centre, name, out bool levelled) is not float own) return null;
        return levelled && HeightNeeded(centre) ? null : own;
    }

    /// <summary>A custom location's own level (<see cref="IsCustomPlace"/>, by <paramref name="name"/>, else by its
    /// centre among <see cref="m_customPlaces"/>): its landing's ground placed first, its flat, else its root
    /// (<see cref="LocationLevelling.CentreHeight"/>), <paramref name="levelled"/> false for the root. Null for every vanilla
    /// place. The level <see cref="StandOffLevel"/> and <see cref="ApproachStairsAt"/> read.</summary>
    private static float? CustomLevel(Vector2 centre, string? name, out bool levelled)
    {
        levelled = false;
        var world = GenerationWorld;
        if (world == null) return null;
        if (!(name != null ? IsCustomPlace(name) : m_customPlaces.ContainsKey(PlaceKey(centre)))) return null;
        levelled = true;
        if (PlacedSites.TryGetReady(centre, out PlacedSite site)) return site.LandingHeight;
        float root = LocationLevelling.CentreHeight(centre, world);
        float? flat = LocationLevelling.ApproachHeight(root, LocationLevelling.OpsAt(centre));
        levelled = flat.HasValue;
        return flat ?? root;
    }

    /// <summary>The one stand-off rule: how far from its centre a road's end at height <paramref name="h"/> stands at a custom
    /// location of level <paramref name="place"/> standing its end off (<see cref="StandOffLevel"/>), its ring
    /// <paramref name="ring"/> (<see cref="KeepOutEdge"/>) and past it the height the place stands over the end, over the road's
    /// grade cap (<see cref="RoadGrade.Configured"/>): a place standing above the road's end (never one below it) gets a road
    /// ending out in the open, the last climb the player's, not an end under a cliff.</summary>
    private static float StandOffRadius(float ring, float place, float h) =>
        ring + Mathf.Max(0f, place - h) / RoadGrade.Floored; // inline, not EaseLength: one rounding, as on the game's Mono

    /// <summary>MWL stand-off, 30 m past the ring: a road's end at a custom location standing above it is
    /// first tried this far past its ring (<see cref="StandOffInBand"/>); only where no end there works does it stand off at the
    /// grade (<see cref="StandOffRadius"/>). Where the grade's stand-off is nearer (a small rise), the grade's is kept.</summary>
    internal const float StandOffPastRing = 30f;

    /// <summary>Whether the grade's stand-off <paramref name="graded"/> (<see cref="StandOffRadius"/>) past the ring
    /// <paramref name="ring"/> lies beyond <see cref="StandOffPastRing"/>, so that is tried first.</summary>
    private static bool BandBefore(float ring, float graded) => graded - ring > StandOffPastRing;

    /// <summary>The end along <paramref name="dir"/> at <see cref="StandOffPastRing"/> past the ring <paramref name="ring"/>, or the
    /// nearest acceptable cell short of it within one march step (<see cref="ReachPastEdge"/>), that the owners an end is judged by
    /// accept, on the ground: dry (<see cref="RoadWater.BuiltDry(WorldGenerator, Vector2)"/>), the ground along the bearing within
    /// the grade cap (<see cref="RoadGrade.Configured"/>), not behind a ridge from the place (<see cref="BehindRidge"/>). Null where
    /// none does. The earthwork cap is the planner's: a road ending there that it refuses is searched again at the grade's
    /// stand-off (<see cref="GenerateRoad"/>). The one owner of the MWL stand-off's ends: the trim asks it
    /// (<see cref="OffLevelStandOff"/>) and so does the reached test (<see cref="AtBandEnd"/>).</summary>
    private static float? StandOffInBand(Vector2 centre, float radius, float ring, Vector2 dir, WorldGenerator world)
    {
        float H(float r) { Vector2 q = centre + dir * r; return BiomeBlendedHeight.GetBlendedHeight(q.x, q.y, world); }
        for (float r = BandOuter(ring); r >= BandInner(ring) - 0.01f; r -= ReachPastEdge)
        {
            Vector2 at = centre + dir * r;
            if (!RoadWater.BuiltDry(world, at)) continue;
            if (RoadGrade.Configured > 0f && Mathf.Abs(H(r + ReachPastEdge) - H(r - ReachPastEdge)) > 2f * ReachPastEdge * RoadGrade.Configured) continue;
            if (BehindRidge(centre, radius, at, H(r))) continue;
            return r;
        }
        return null;
    }

    /// <summary>The MWL stand-off's outermost end past the ring <paramref name="ring"/> (<see cref="StandOffPastRing"/>).</summary>
    private static float BandOuter(float ring) => ring + StandOffPastRing;
    /// <summary>The MWL stand-off's innermost end: one march step (<see cref="ReachPastEdge"/>) short of <see cref="BandOuter"/>.</summary>
    private static float BandInner(float ring) => BandOuter(ring) - ReachPastEdge;

    /// <summary>Whether network <paramref name="d"/> out from the place at <paramref name="centre"/> (at <paramref name="q"/>)
    /// stands at the MWL stand-off's end on its own bearing, within a path cell of it: the end the trim would choose there
    /// (<see cref="StandOffInBand"/>), so a bearing whose band end is wet, too steep or behind a ridge has none. The band's
    /// span first, the cheap test.</summary>
    private static bool AtBandEnd(Vector2 centre, float radius, float ring, Vector2 q, float d)
    {
        if (d < BandInner(ring) - ReachPastEdge - 0.01f || d > BandOuter(ring) + ReachPastEdge + 0.01f) return false;
        var world = GenerationWorld;
        if (world == null || d < 1e-3f) return false;
        return StandOffInBand(centre, radius, ring, (q - centre) * (1f / d), world) is float end && Mathf.Abs(d - end) <= ReachPastEdge;
    }

    /// <summary>Set on the road's thread while it is searched again with no MWL stand-off (<see cref="GenerateRoad"/>).</summary>
    [System.ThreadStatic] private static bool t_noStandOffBand;
    /// <summary>The road's ends on this thread stood off at the MWL stand-off (<see cref="OffLevelStandOff"/>), nearer than the
    /// grade's: each place's centre and the grade's stand-off there, the farthest the road searched again would end.</summary>
    [System.ThreadStatic] private static List<(Vector2 centre, float graded)>? t_bandedEnds;
    /// <summary>Where the road last refused on this thread was refused (<see cref="GenerateRoadCore"/>), where it says: null
    /// where it does not.</summary>
    [System.ThreadStatic] private static List<Vector2>? t_refusedAt;
    /// <summary>A road on this thread whose way onto a placed place ran beside the approach another road laid there
    /// (<see cref="ReEndOnAWayIn"/>): that place's centre, and where the approach's own road meets it, which the road is
    /// searched again to end on (<see cref="GenerateRoad"/>). Null where none.</summary>
    [System.ThreadStatic] private static (Vector2 place, Vector2 onItsRoad)? t_reEnd;

    /// <summary>The refusal of a road whose way onto a place runs beside the approach already laid there (<see cref="ReEndOnAWayIn"/>).</summary>
    internal const string BesideItsApproachRefusal = "its way onto the landing beside the place's approach";

    /// <summary>Where a road arriving at <paramref name="place"/> as <paramref name="arrival"/> ends instead, on the network, or
    /// null: where another way into that place arrives the same way (<see cref="PlaceWaysIn.Joining"/>, the one owner), on that
    /// way's road (an approach's foot, where its road meets it; a road's arrival where it passes nearest), as a junction
    /// (radius 0, as a place's join to the network ends, <see cref="JoinPlace"/>).</summary>
    internal static Vector2? ReEndOnAWayIn(Vector2 place, PlaceWaysIn.Way? arrival) =>
        PlaceWaysIn.Joining(place, arrival, out Vector2 at) != null ? at : null;

    /// <summary>Where a road whose staircase onto <paramref name="place"/> was refused at every aim and start as a way in the
    /// place has (<see cref="RoadStairRuns.BesideAnApproach"/>) ends instead: as its road arrives (<see cref="ReEndOnAWayIn"/>),
    /// else at the foot of the approach of that place nearest its end, the way in its staircase would have built twice.</summary>
    private static Vector2? WayInRefused(Vector2 place, bool atStart, List<RoadSpatialGrid.PlannedPath> plans)
    {
        if (ReEndOnAWayIn(place, ArrivalAt(place, atStart, plans, new(), new(), null)) is Vector2 on) return on;
        var (points, _, _) = Flatten(plans);
        if (points.Count == 0) return null;
        Vector2 end = points[atStart ? 0 : points.Count - 1];
        Vector2? best = null;
        foreach (PlaceWaysIn.Way way in PlaceWaysIn.There(place))
            if (way.Approach is StairRun run && RoadStairRuns.RoadEndOf(run, place) is var foot
                && (best == null || Vector2.Distance(foot, end) < Vector2.Distance(best.Value, end)))
                best = foot;
        return best;
    }

    /// <summary>The staircase a road lays onto its place at its start or end: its way onto a placed place's landing, or its
    /// approach (<see cref="SiteApproaches"/>, laid at its path's first or last point). Null where none.</summary>
    private static RoadStairSpan? StairsAt(bool atStart, List<(PlacedSite site, RoadSpatialGrid.PlannedPath? road, RoadStairSpan? stairs)> entries,
        List<RoadStairSpan> approaches, PlacedSite? site)
    {
        if (site != null && entries.Find(e => e.site == site).stairs is RoadStairSpan entry) return entry;
        return approaches.Find(a => (a.FromIndex == 0) == atStart);
    }

    /// <summary>How a road arrives at <paramref name="place"/> at its start or end (<see cref="PlaceWaysIn.Way"/>): its staircase
    /// there where it lays one (<see cref="StairsAt"/>), else its road with its road onto a placed place's landing, over its last
    /// stretch.</summary>
    private static PlaceWaysIn.Way? ArrivalAt(Vector2 place, bool atStart, List<RoadSpatialGrid.PlannedPath> plans,
        List<(PlacedSite site, RoadSpatialGrid.PlannedPath? road, RoadStairSpan? stairs)> entries, List<RoadStairSpan> approaches, PlacedSite? site)
    {
        if (StairsAt(atStart, entries, approaches, site) is RoadStairSpan stairs) return PlaceWaysIn.Of(stairs.Plan, place);
        var (points, heights, _) = Flatten(plans);
        if (points.Count == 0) return null;
        var line = new List<Vector2>(points);
        var hs = new List<float>(heights);
        if (atStart) { line.Reverse(); hs.Reverse(); }
        if (site != null && entries.Find(e => e.site == site).road is RoadSpatialGrid.PlannedPath entry && entry.Points.Count > 0)
        {
            bool fromEnd = Vector2.Distance(entry.Points[0], line[line.Count - 1]) <= Vector2.Distance(entry.Points[entry.Points.Count - 1], line[line.Count - 1]);
            for (int k = 0; k < entry.Points.Count; k++)
            {
                int i = fromEnd ? k : entry.Points.Count - 1 - k;
                line.Add(entry.Points[i]);
                hs.Add(entry.Heights[i]);
            }
        }
        return PlaceWaysIn.Of(line, hs, place);
    }

    /// <summary>Whether a road refused at <paramref name="at"/> (null or empty where its refusal does not say) may yet be built
    /// ending at the grade's stand-off instead of the MWL stand-off at <paramref name="bandedEnds"/>: refused where it does not
    /// say, or somewhere on the stretch the grade's stand-off gives up (within it of a banded end's place, and a path cell). A
    /// refusal elsewhere on the road is the same refusal searched again.</summary>
    internal static bool RefusedAtABandedEnd(IReadOnlyList<Vector2>? at, IReadOnlyList<(Vector2 centre, float graded)>? bandedEnds)
    {
        if (at == null || at.Count == 0 || bandedEnds == null) return true;
        foreach (var (centre, graded) in bandedEnds)
            foreach (Vector2 p in at)
                if ((p - centre).sqrMagnitude <= (graded + ReachPastEdge) * (graded + ReachPastEdge)) return true;
        return false;
    }

    /// <summary>The stand-off radius (<see cref="StandOffRadius"/>) at the ground along <paramref name="dir"/>: the first path cell
    /// out from the ring (<see cref="ReachPastEdge"/>) where the ground is low enough under the place for its own stand-off to
    /// fall inside it. A march, not a fixed point, so ground steeper than the grade cap still gives the nearest such cell; never
    /// past the stand-off from height 0.</summary>
    private static float StandOffOnGround(Vector2 centre, float ring, float place, Vector2 dir, WorldGenerator world)
    {
        float limit = StandOffRadius(ring, place, 0f);
        for (float r = ring; r < limit; r += ReachPastEdge)
        {
            Vector2 at = centre + dir * r;
            if (StandOffRadius(ring, place, BiomeBlendedHeight.GetBlendedHeight(at.x, at.y, world)) <= r) return r;
        }
        return limit;
    }

    /// <summary>The stand-off radius kept per place (<see cref="PlaceKey"/>) for the reached test's reach (<see cref="FallbackReach"/>):
    /// the largest of the grade's round the place (eight bearings, read once) and of every road end trimmed to it
    /// (<see cref="OffLevelStandOff"/>). The grade's, the outermost a road's end may stand at, so an end at the MWL stand-off
    /// (<see cref="StandOffPastRing"/>) and one at the grade's alike are in reach.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), float> m_standOffRadius = new();

    private static float CompassStandOff(Vector2 centre, float ring, float place, WorldGenerator world)
    {
        float most = ring;
        for (int i = 0; i < 8; i++)
        {
            float a = i * 45f * Mathf.Deg2Rad;
            most = Mathf.Max(most, StandOffOnGround(centre, ring, place, new Vector2(Mathf.Sin(a), Mathf.Cos(a)), world));
        }
        return most;
    }

    /// <summary>
    /// How much farther out than its ring (<see cref="KeepOutEdge"/>) a road arriving from <paramref name="toward"/> (where it
    /// crosses the ring, <see cref="RingCrossing"/>) ends at a custom location that stands its road's end off
    /// (<see cref="StandOffLevel"/>): the end on that bearing at the MWL stand-off (<see cref="StandOffInBand"/>), else
    /// (none there, or the road searched again without it) its stand-off at the ground (<see cref="StandOffOnGround"/>), that
    /// one kept where nearer than the MWL stand-off (a small rise); the grade's kept for the reached test (<see cref="m_standOffRadius"/>).
    /// 0 for every other place.
    /// </summary>
    internal static float OffLevelStandOff(Vector2 centre, float radius, Vector2 toward)
    {
        var world = GenerationWorld;
        if (world == null || StandOffLevel(centre, radius) is not float place) return 0f;
        float ring = KeepOutEdge(centre, radius, RoadWidth);
        Vector2 dir = toward - centre;
        dir = dir.sqrMagnitude < 1e-4f ? new Vector2(0f, 1f) : dir.normalized;
        float graded = StandOffOnGround(centre, ring, place, dir, world), at = graded;
        if (!t_noStandOffBand && BandBefore(ring, graded) && StandOffInBand(centre, radius, ring, dir, world) is float near)
        {
            at = near;
            (t_bandedEnds ??= new()).Add((centre, graded));
        }
        m_standOffRadius.AddOrUpdate(PlaceKey(centre), _ => Mathf.Max(CompassStandOff(centre, ring, place, world), graded), (_, old) => Mathf.Max(old, graded));
        return at - ring;
    }

    /// <summary>Where a road's path first stands outside the ring of radius <paramref name="ring"/> round
    /// <paramref name="centre"/>, from its start (<paramref name="fromStart"/>) or its end: the bearing the trim cuts it on. The
    /// search starts at the place's centre (or its ground), so the path's own end gives no bearing. The path's far end where
    /// none does.</summary>
    internal static Vector2 RingCrossing(List<Vector2> path, Vector2 centre, float ring, bool fromStart)
    {
        for (int k = 0; k < path.Count; k++)
        {
            Vector2 q = path[fromStart ? k : path.Count - 1 - k];
            if ((q - centre).sqrMagnitude > ring * ring) return q;
        }
        return path[fromStart ? path.Count - 1 : 0];
    }

    /// <summary>Whether network at <paramref name="q"/> (height <paramref name="h"/>) stands where a road to a custom location
    /// standing its end off (<paramref name="place"/>, its <see cref="StandOffLevel"/>) ends (<see cref="StandOffRadius"/>): within
    /// a path cell of it, or, where that lies past the MWL stand-off, at the MWL stand-off's end on its bearing
    /// (<see cref="AtBandEnd"/>, the trim's own owner). True for every other place (<paramref name="place"/> null).</summary>
    private static bool AtStandOff(Vector2 flat, float radius, float? place, Vector2 q, float h)
    {
        if (place is not float own) return true;
        float ring = KeepOutEdge(flat, radius, RoadWidth), graded = StandOffRadius(ring, own, h), d = Vector2.Distance(q, flat);
        return Mathf.Abs(d - graded) <= ReachPastEdge
            || BandBefore(ring, graded) && AtBandEnd(flat, radius, ring, q, d);
    }

    /// <summary>The one ring test of network at <paramref name="q"/> (height <paramref name="h"/>) at a place reached off its
    /// level (on the fallback, or a custom location standing its end off, <paramref name="place"/> its
    /// <see cref="StandOffLevel"/>): at its stand-off (<see cref="AtStandOff"/>, the cheap test first) and not behind a ridge
    /// from it (<see cref="BehindRidge"/>), both read from the network's own height. Asked by the reached test
    /// (<see cref="Reached"/>) and by the refusal of a road's end (<see cref="EndNotLevel"/>) alike, so a road the fallback
    /// commits is never left unserved.</summary>
    private static bool OffLevelAt(Vector2 flat, float radius, float? place, Vector2 q, float h) =>
        AtStandOff(flat, radius, place, q, h) && !BehindRidge(flat, radius, q, h);

    /// <summary>How far past the ring the network is looked for at a place on the fallback (<see cref="ReachPastEdge"/>); for a
    /// custom location standing its end off (<paramref name="place"/>, its <see cref="StandOffLevel"/>) out to its kept stand-off
    /// (<see cref="m_standOffRadius"/>) and a path cell.</summary>
    private static float FallbackReach(Vector2 flat, float radius, float? place)
    {
        float ring = KeepOutEdge(flat, radius, RoadWidth);
        var world = GenerationWorld;
        if (place is not float own || world == null) return ring + ReachPastEdge;
        return m_standOffRadius.GetOrAdd(PlaceKey(flat), _ => CompassStandOff(flat, ring, own, world)) + ReachPastEdge;
    }

    /// <summary>How many places the network reaches only off their level, on the fallback (<see cref="ReachOffLevel"/>).</summary>
    private static int m_reachedOffLevel;
    /// <summary>For tests and the summary: places reached off level this generation.</summary>
    internal static int ReachedOffLevelCount => m_reachedOffLevel;

    /// <summary>Whether the place at <paramref name="centre"/> (<paramref name="prefab"/>, null where not known) may fall
    /// back to a road ending off its level (<see cref="ReachOffLevel"/>): not a vanilla place in the Mountains, where it is reached at
    /// height or not at all (its approach staircase or its landing), a custom location excepted (<see cref="IsCustomPlace"/>); not a boss's entrance (<see cref="OwnLandingOnly"/>), reached
    /// only on its own landing; not a place with a door (a crypt, <see cref="PlacedSiteShape.DoorLandings"/>), reached only
    /// at its door.</summary>
    internal static bool OffLevelAllowed(Vector2 centre, string? prefab)
    {
        if (prefab != null && (OwnLandingOnly(prefab) || PlacedSiteShape.HasDoor(prefab))) return false;
        if (PlacedSites.TryGetReady(centre, out PlacedSite placed)
            && (OwnLandingOnly(placed.Record.Prefab) || PlacedSiteShape.HasDoor(placed.Record.Prefab))) return false;
        var world = GenerationWorld;
        if (world == null) return false;
        return world.GetBiome(centre.x, centre.y) != Heightmap.Biome.Mountain || IsCustomPlace(prefab);
    }

    /// <summary>A location mod's place (<see cref="IsCustomLocation"/>: registered through the API or named in the CustomLocations
    /// config, never one of the vanilla names in <see cref="LocationPriorities"/>), in any biome. It is the one Mountains place
    /// that may fall back to a road ending off its level, and wherever it is reached without its height (every biome) its
    /// road's end stands off from its own level (<see cref="StandOffLevel"/>). The fallback keeps every protection of
    /// <see cref="ReachOffLevel"/> (<see cref="BehindRidge"/>, the earthwork cap, the grade cap).</summary>
    private static bool IsCustomPlace(string? name) =>
        name != null && !LocationPriorities.ContainsKey(name) && IsCustomLocation(name);

    /// <summary>
    /// The fallback for a place its first attempt, at height, left unserved: tried again without height
    /// (<see cref="HeightNeeded"/> false for it alone), by <paramref name="attempt"/>, the old behaviour, a road ending at its
    /// ring off its level reaching it. Asked only of a place the network does not reach at height and that may fall back
    /// (<see cref="OffLevelAllowed"/>); first whether the network already there reaches it off level, no road needed. True
    /// where the place is reached that way: it stays without height for the rest of the generation (the count, a later
    /// road's stub), counted and logged ("reached off level"). Otherwise it needs height again, and is unserved; where only
    /// network behind a ridge from it was in reach (<see cref="BehindRidge"/>), counted and logged as that ("behind a ridge").
    /// </summary>
    internal static bool ReachOffLevel(string name, Vector3 position, float radius, Func<bool> attempt)
    {
        var centre = new Vector2(position.x, position.z);
        if (radius <= 0f || !OffLevelAllowed(centre, name) || Reached(centre, radius, name)) return false;
        (int, int) key = PlaceKey(centre);
        m_offLevel[key] = true;
        NoteCustomPlace(name, position);
        IslandNetworkChanged();
        if (Reached(centre, radius, name) || attempt() && Reached(centre, radius, name))
        {
            System.Threading.Interlocked.Increment(ref m_reachedOffLevel);
            string prefab = PlacedSites.TryGetReady(centre, out PlacedSite placed) ? placed.Record.Prefab : name;
            string off = ArrivalOffset(centre, radius) is (float place, float offset) ? FormattableString.Invariant($", the network {offset:+0.0;-0.0} m off its level ({place:F1} m)") : "";
            Log.LogInfo(FormattableString.Invariant($"  reached off level: {prefab} at ({position.x:F0}, {position.z:F0}){off}"));
            return true;
        }
        // Network behind a place entered only from its front is not in its reach (PlacedSiteShape.OnEntranceSide).
        Func<Vector2, float, bool> inReach = PlacedSites.TryGetReady(centre, out PlacedSite site) ? (q, _) => site.Shape.OnEntranceSide(q) : (_, _) => true;
        if (NetworkNear(centre, KeepOutEdge(centre, radius, RoadWidth) + ReachPastEdge, inReach, true))
        {
            System.Threading.Interlocked.Increment(ref m_behindRidge);
            Log.LogInfo(FormattableString.Invariant($"  behind a ridge: {name} at ({position.x:F0}, {position.z:F0}), the network in reach ends behind ground over {RidgeRise:F0} m above it and the place: unserved"));
        }
        m_offLevel.TryRemove(key, out _);
        IslandNetworkChanged();
        return false;
    }

    /// <summary>How many places the fallback left unserved because the network in their reach ends behind a ridge from them
    /// (<see cref="ReachOffLevel"/>, <see cref="BehindRidge"/>).</summary>
    private static int m_behindRidge;
    /// <summary>For tests and the summary: places left unserved behind a ridge this generation.</summary>
    internal static int BehindRidgeCount => m_behindRidge;

    /// <summary>
    /// How far the ground between a road's end and the place it reaches off level may rise over both of them
    /// (<see cref="RidgeOver"/>) before the end is behind a ridge from the place and does not reach it (<see cref="BehindRidge"/>).
    /// Mistlands_DvergrTownEntrance2 at (1344, 7168) was reached by a road ending 42 m off, at 46.4 m, behind a ridge 61.6 m
    /// high, the entrance at 36.1 m beyond it: stranded on the wrong side of the ridge (15.2 m over the end, on the ground in
    /// the game). A climb up to a place higher than the end, which the player sees from the road, rises no higher than the
    /// place: 0 m over both, however high the place, and is kept. The most ground a road is cut through
    /// (<see cref="ShapedLimit"/>: the earthwork cap, 8 m, never past what the terrain moves); a rise past it between the end
    /// and the place is ground no road could have been carried through, the place on its other side. Measured offline on eight worlds' fallbacks (143 arrivals): that ridge 15.6 m,
    /// then none over 7.2 m.
    /// </summary>
    internal static float RidgeRise => ShapedLimit;
    /// <summary>The ground over the first metres from a road's end is not read for a ridge: the road's own half-width
    /// (<see cref="RoadWidth"/>) and the blend past its edge (<see cref="RoadConstants.TerrainBlendMargin"/>), where its cut or
    /// fill, not the ground, decides.</summary>
    internal static float RidgePastEnd => RoadWidth / 2f + RoadConstants.TerrainBlendMargin;

    /// <summary>
    /// How far the highest ground on the straight line from <paramref name="end"/> (a road's end, at
    /// <paramref name="endHeight"/>) to <paramref name="place"/> (the place's landing or edge, at <paramref name="placeHeight"/>)
    /// rises over the higher of the two, read every metre on <paramref name="ground"/> from <see cref="RidgePastEnd"/> on; 0 or
    /// less where nothing stands over them. The one measure of a ridge between them (<see cref="BehindRidge"/>).
    /// </summary>
    internal static float RidgeOver(Vector2 end, float endHeight, Vector2 place, float placeHeight, Func<Vector2, float> ground)
    {
        float length = Vector2.Distance(end, place), peak = float.NegativeInfinity;
        int steps = Mathf.Max(1, Mathf.CeilToInt(length));
        for (int i = 0; i <= steps; i++)
            if (length * i / steps >= RidgePastEnd) peak = Mathf.Max(peak, ground(Vector2.Lerp(end, place, (float)i / steps)));
        return float.IsNegativeInfinity(peak) ? 0f : peak - Mathf.Max(endHeight, placeHeight);
    }

    /// <summary>
    /// Whether network at <paramref name="at"/> (its height <paramref name="height"/>) is behind a ridge from the place at
    /// <paramref name="centre"/> (exterior <paramref name="radius"/>, as asked): the ground on the line to the place's landing
    /// (placed first) or to its edge on that side, at its arrival height (<see cref="ArrivalHeight"/>), rises more than
    /// <see cref="RidgeRise"/> over both (<see cref="RidgeOver"/>), on the ground as places leave it
    /// (<see cref="PlacedSites.GroundAt(float, float, WorldGenerator)"/>). The one test, asked only on the fallback, where an end
    /// off level is otherwise accepted: by the reached test (<see cref="Reached"/>) and the refusal of a road's end
    /// (<see cref="EndNotLevel"/>). At height a road's end is level with its place, and is not asked.
    /// </summary>
    internal static bool BehindRidge(Vector2 centre, float radius, Vector2 at, float height)
    {
        var world = GenerationWorld;
        if (world == null || radius <= 0f || ArrivalHeight(centre, radius, at) is not float level) return false;
        Vector2 to;
        if (PlacedSites.TryGetReady(centre, out PlacedSite placed)) to = placed.Shape.Landing;
        else
        {
            to = EdgeToward(centre, radius, at);
        }
        return RidgeOver(at, height, to, level, q => PlacedSites.GroundAt(q.x, q.y, world)) > RidgeRise;
    }

    /// <summary>Whether a road's end at the place at <paramref name="centre"/> (exterior <paramref name="radius"/>, as
    /// asked) is already reached by other network, so the road's own stub there may be dropped (<see cref="DropStubs"/>):
    /// the one reached test (<see cref="Reached"/>), with the place's name, so a boss's entrance not placed first keeps its
    /// own stub. An end with no place (radius 0) is a junction on the network.</summary>
    internal static bool EndReached(Vector2 centre, float radius) =>
        radius <= 0f || RoadSpatialGrid.IsInitialized && Reached(centre, radius, PrefabAt(centre));

    /// <summary>The prefab of the place standing at <paramref name="centre"/> (the location in its zone, within a metre of
    /// it), or null where there is none.</summary>
    private static string? PrefabAt(Vector2 centre)
    {
        Vector2s zone = ZoneSystem.GetZone(new Vector3(centre.x, 0f, centre.y));
        return ZoneSystem.instance != null && ZoneSystem.instance.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance instance)
            && new Vector2(instance.m_position.x - centre.x, instance.m_position.z - centre.y).sqrMagnitude <= 1f
            ? instance.m_location.m_prefab.Name : null;
    }

    /// <summary>Whether road or a staircase's walked piece (its walking surface, <see cref="RoadStairRuns.Surface"/>) lies
    /// within <paramref name="reach"/> of <paramref name="at"/>, level with <paramref name="level"/> (within
    /// <see cref="ReachRise"/>; at any height where it is not known): the network a place is reached by. The one height
    /// guard for every place, placed first or not.</summary>
    private static bool NetworkNear(Vector2 at, float reach, float? level) =>
        NetworkNear(at, reach, level.HasValue ? _ => level.Value : null);

    /// <summary><see cref="NetworkNear(Vector2, float, float?)"/> with the level the network must meet where it stands
    /// (a place's ground at its edge on that side, <see cref="ArrivalHeight"/>).</summary>
    private static bool NetworkNear(Vector2 at, float reach, Func<Vector2, float>? level)
    {
        if (level == null)
            return RoadSpatialGrid.TryGetRoadWithin(at, reach, out _)
                || StairStructures.PiecesNear(new Vector3(at.x, 0f, at.y), reach).Exists(p => p.Kind != StructurePieceKind.StairSupport);
        return NetworkNear(at, reach, (q, h) => Mathf.Abs(h - level(q)) <= ReachRise, false);
    }

    /// <summary><see cref="NetworkNear(Vector2, float, float?)"/> for network a test <paramref name="accepts"/> where it stands
    /// (its point and its height): level with the place, road only; or on the fallback not behind a ridge from it
    /// (<see cref="BehindRidge"/>), with <paramref name="paintToo"/> the same network as at any height, a ford's painted points
    /// as well (<see cref="RoadSpatialGrid.TryGetRoadWithin"/>: Bonemass and a sunken crypt, reached over a ford).</summary>
    private static bool NetworkNear(Vector2 at, float reach, Func<Vector2, float, bool> accepts, bool paintToo)
    {
        foreach (RoadSpatialGrid.RoadPoint q in RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(at.x, 0f, at.y), reach))
            if ((paintToo ? !q.cutOnly : q.IsRoad) && (q.p - at).sqrMagnitude <= reach * reach && accepts(q.p, q.h)) return true;
        return StairStructures.PiecesNear(new Vector3(at.x, 0f, at.y), reach).Exists(p => p.Kind != StructurePieceKind.StairSupport
            && accepts(new Vector2(p.Position.x, p.Position.z), RoadStairRuns.Surface(p)));
    }

    /// <summary>For the reports (an unreached place's line): the height the place is arrived at
    /// (a placed place's landing ground, else <see cref="ArrivalHeight"/>) and how far over (+) or under (-) it the network
    /// within the place's reach comes nearest to level, road or a staircase's walked piece; offset null where no network is
    /// in reach. Null where the place's height is not known.</summary>
    internal static (float place, float? offset)? ArrivalOffset(Vector2 flat, float radius)
    {
        if (radius <= 0f || !RoadSpatialGrid.IsInitialized) return null;
        Vector2 at;
        float reach;
        float? height;
        if (PlacedSites.TryGetReady(flat, out PlacedSite placed))
        {
            at = placed.Shape.Landing;
            reach = LandingReach;
            height = placed.LandingHeight;
        }
        else
        {
            at = flat;
            reach = KeepOutEdge(flat, radius, RoadWidth) + ReachPastEdge;
            height = ArrivalHeight(flat, radius, flat);
        }
        if (height is not float place) return null;
        Func<Vector2, float> level = ArrivalLevelFrom(flat, radius) ?? (_ => place);
        float? best = null;
        void Offer(Vector2 q, float h) { float off = h - level(q); if (best == null || Mathf.Abs(off) < Mathf.Abs(best.Value)) best = off; }
        foreach (var (q, h) in RoadsNear(at, reach)) Offer(q, h);
        foreach (StructurePiece p in StairStructures.PiecesNear(new Vector3(at.x, 0f, at.y), reach))
            if (p.Kind != StructurePieceKind.StairSupport && Vector2.Distance(new Vector2(p.Position.x, p.Position.z), at) <= reach)
                Offer(new Vector2(p.Position.x, p.Position.z), RoadStairRuns.Surface(p));
        return (place, best);
    }

    /// <summary>How near a placed place's landing the network must come to reach it: the road onto it keeps
    /// its own reach (half its width and its blend) off the entrance's pit, so it stops a few metres short
    /// of the shape's landing point (4.8 m on the test cave).</summary>
    public const float LandingReach = 8f;

    /// <summary>For an unreached placed place's line: how near the network comes to its landing.</summary>
    private static string NearestToLanding(PlacedSite site)
    {
        Vector2 landing = site.Shape.Landing;
        float road = RoadSpatialGrid.IsInitialized && RoadSpatialGrid.TryGetRoadWithin(landing, 200f, out Vector2 r) ? Vector2.Distance(r, landing) : float.PositiveInfinity;
        float stair = float.PositiveInfinity;
        foreach (StructurePiece p in StairStructures.PiecesNear(new Vector3(landing.x, 0f, landing.y), 200f))
            if (p.Kind != StructurePieceKind.StairSupport) stair = Mathf.Min(stair, Vector2.Distance(new Vector2(p.Position.x, p.Position.z), landing));
        return FormattableString.Invariant($"; placed first, its landing ({landing.x:F1}, {landing.y:F1}): nearest road {road:F1} m, nearest stair piece {stair:F1} m");
    }

    /// <summary>Whether <paramref name="prefab"/> is reached only by a road onto its own landing (a boss's entrance), not by a
    /// road passing within reach of it.</summary>
    internal static bool OwnLandingOnly(string prefab) =>
        prefab.IndexOf("BossEntrance", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Whether the network comes onto a placed place's landing: a road or staircase was carried on
    /// onto it (<see cref="PlacedSites.MarkLanded"/>: laid to its landing's level ground, <see cref="PlacedSiteShape.Aims"/>),
    /// or road or a staircase's walked piece is near the shape's landing point, level with the ground there
    /// (<see cref="NetworkNear"/>, as every other place: without a height guard network 20-45 m under a landing counted
    /// it reached).</summary>
    internal static bool LandingReached(PlacedSite site) =>
        PlacedSites.Landed(site.Centre) || NetworkNear(site.Shape.Landing, LandingReach, site.LandingHeight);

    /// <summary>The edge of the ground a place shapes (<see cref="RoadSiteProtection.ArrivalRadiusAt"/>) on the side of
    /// <paramref name="q"/> (due +z where <paramref name="q"/> is within 1 cm of the centre).</summary>
    private static Vector2 EdgeToward(Vector2 centre, float radius, Vector2 q)
    {
        Vector2 out1 = q - centre;
        out1 = out1.sqrMagnitude < 1e-4f ? new Vector2(0f, 1f) : out1.normalized;
        return centre + out1 * RoadSiteProtection.ArrivalRadiusAt(centre, radius);
    }

    /// <summary>
    /// The height the network arriving at the place at <paramref name="centre"/> (exterior <paramref name="radius"/>, as asked)
    /// from <paramref name="from"/> must end level with to reach it (<see cref="ReachRise"/>): the one owner of a place's arrival
    /// height (the place's ground at its edge). Placed before its roads, its landing's ground
    /// (<see cref="PlacedSiteShape.Landing"/> on its placed ground). Else its single levelled platform, never below its root
    /// (<see cref="LocationLevelling.ApproachHeight"/>), the flat it is entered on. Else (no flat, several levels, or no
    /// levelling known) its ground at its edge on that side: the edge of the ground it shapes
    /// (<see cref="RoadSiteProtection.ArrivalRadiusAt"/>) toward <paramref name="from"/>, as its levelling round its centre leaves
    /// it (<see cref="LocationLevelling.PlacedGroundAt"/>). A cabin only smooths the ground round it: with no level known, a
    /// road 10 m under AbandonedLogCabin04 reached it; its ground at its edge on the road's side (10 m out, 185.3 m) is still 4.7
    /// m over the road's end. Its root, the ground under its centre, is not the level of a place entered from its side on a
    /// hillside (a troll cave's mouth). Null outside a generation, or for a point with no radius.
    /// </summary>
    internal static float? ArrivalHeight(Vector2 centre, float radius, Vector2 from)
    {
        if (PlacedSites.TryGetReady(centre, out PlacedSite site)) return site.LandingHeight;
        var world = GenerationWorld;
        if (world == null || radius <= 0f) return null;
        float root = LocationLevelling.CentreHeight(centre, world);
        IReadOnlyList<LevelOp>? ops = LocationLevelling.OpsAt(centre);
        if (LocationLevelling.ApproachHeight(root, ops) is float platform) return platform;
        Vector2 edge = EdgeToward(centre, radius, from);
        return LocationLevelling.PlacedGroundAt(edge, centre, root, ops, BiomeBlendedHeight.GetBlendedHeight(edge.x, edge.y, world));
    }

    /// <summary><see cref="ArrivalHeight"/> for network anywhere round the place, its edge read once a 5 degree bearing (a
    /// reached test asks of every road point in reach); null where the height is not known. Pure while preparation is
    /// closed (<see cref="LocationLevelling.Sealed"/>: the places placed, their levelling and roots fixed), so kept per place
    /// for the rest of that generation's islands (<see cref="m_levelFrom"/>), its bearings shared by every worker.</summary>
    private static Func<Vector2, float>? ArrivalLevelFrom(Vector2 centre, float radius)
    {
        if (!LocationLevelling.Sealed) return BuildArrivalLevelFrom(centre, radius);
        int seal = LocationLevelling.SealCount;
        if (m_levelFromSeal != seal)
            lock (m_levelFrom)
                if (m_levelFromSeal != seal) { m_levelFrom.Clear(); m_levelFromSeal = seal; }
        return m_levelFrom.GetOrAdd((PlaceKey(centre), radius), _ => BuildArrivalLevelFrom(centre, radius));
    }

    /// <summary>The places' arrival levels (<see cref="ArrivalLevelFrom"/>) while preparation stays closed, by rounded centre
    /// and radius as asked; emptied when it is next closed (<see cref="m_levelFromSeal"/>).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<((int, int), float), Func<Vector2, float>?> m_levelFrom = new();
    private static volatile int m_levelFromSeal = -1;

    private static Func<Vector2, float>? BuildArrivalLevelFrom(Vector2 centre, float radius)
    {
        if (ArrivalHeight(centre, radius, centre) is not float level) return null;
        if (PlacedSites.TryGetReady(centre, out _)) return _ => level;
        var world = GenerationWorld!;
        if (LocationLevelling.ApproachHeight(LocationLevelling.CentreHeight(centre, world), LocationLevelling.OpsAt(centre)).HasValue)
            return _ => level;
        var byBearing = new System.Collections.Concurrent.ConcurrentDictionary<int, float>();
        return q =>
        {
            int bearing = Mathf.RoundToInt(Mathf.Atan2(q.y - centre.y, q.x - centre.x) * Mathf.Rad2Deg / 5f);
            return byBearing.GetOrAdd(bearing, b =>
            {
                float a = b * 5f * Mathf.Deg2Rad;
                return ArrivalHeight(centre, radius, centre + new Vector2(Mathf.Cos(a), Mathf.Sin(a)))!.Value;
            });
        };
    }

    /// <summary>Why a road's end at one of its places does not end level with the place (<see cref="ReachRise"/> of
    /// <see cref="ArrivalHeight"/> on its side, the one test <see cref="Reached"/> makes) and no approach staircase was laid from it onto the
    /// place (<see cref="SiteApproaches"/>) nor a way onto its landing (a placed place's, <see cref="PlanEntry"/>), or null. The
    /// end is where walking ends: the last road point, or the staircase the road ends on. Over as well as under, in every
    /// biome: such a road would not reach its place, so it is refused and the planner tries its other candidates (or the
    /// place goes unserved), but at a place on its second attempt, without height (<see cref="HeightNeeded"/>,
    /// <see cref="ReachOffLevel"/>). Before, only in the Mountains or at a boss's entrance; elsewhere an end off level was
    /// kept and counted reached, which is now that fallback.</summary>
    internal static (string refusal, string why)? EndNotLevel(List<Vector2> path, List<RoadSpatialGrid.PlannedPath> plans, List<RoadStairSpan> stairs,
        List<RoadStairSpan> approaches, Vector2 startCenter, float startRadius, Vector2 endCenter, float endRadius, bool startAtPlace,
        bool endAtPlace)
    {
        (string, string)? At(bool atStart, Vector2 centre, float radius, bool atPlace)
        {
            if (!atPlace || radius <= 0f || plans.Count == 0) return null;
            if (approaches.Exists(a => a.FromIndex == (atStart ? 0 : path.Count - 1))) return null;
            float height;
            RoadStairSpan? last = stairs.Find(s => atStart ? s.FromIndex == 0 : s.ToIndex == path.Count - 1);
            if (last != null) height = atStart ? last.FromHeight : last.ToHeight;
            else
            {
                var piece = atStart ? plans[0] : plans[plans.Count - 1];
                if (piece.Heights.Count == 0) return null;
                height = atStart ? piece.Heights[0] : piece.Heights[piece.Heights.Count - 1];
            }
            Vector2 end = path[atStart ? 0 : path.Count - 1];
            // On the fallback an end at any height reaches its place, at its stand-off (a custom location's) and not from
            // behind a ridge: the reached test's own ring test (OffLevelAt), read from the end's height. On a placed place's
            // landing, as the reached test, not behind a ridge alone.
            if (!HeightNeeded(centre))
            {
                float? standOff = StandOffLevel(centre, radius);
                bool onLanding = PlacedSites.TryGetReady(centre, out PlacedSite placed)
                    && (end - placed.Shape.Landing).sqrMagnitude <= LandingReach * LandingReach;
                if (onLanding ? !BehindRidge(centre, radius, end, height) : OffLevelAt(centre, radius, standOff, end, height)) return null;
                return onLanding || AtStandOff(centre, radius, standOff, end, height)
                    ? (RidgeRefusal, FormattableString.Invariant($"ends behind ground over {RidgeRise:F0} m above it and its place"))
                    : (NotLevelRefusal, FormattableString.Invariant($"ends {(standOff ?? height) - height:+0.0;-0.0} m off its place, not at its stand-off, {Vector2.Distance(end, centre):F1} m out"));
            }
            // A custom location with no flat nor landing standing above the end: at its stand-off, as the reached test.
            if (StandOffLevel(centre, radius) is float own && height < own)
                return OffLevelAt(centre, radius, own, end, height) ? null
                    : (NotLevelRefusal, FormattableString.Invariant($"ends {own - height:F1} m under its place, not at its stand-off, {Vector2.Distance(end, centre):F1} m out"));
            if (ArrivalHeight(centre, radius, end) is not float place) return null;
            float off = height - place;
            if (Mathf.Abs(off) <= ReachRise)
            {
                // Level enough to reach it, but a stair or more off (no staircase outside the Mountains): counted, kept.
                if (RoadsLog && Mathf.Abs(off) > ArrivalLevel) System.Threading.Interlocked.Increment(ref m_closeEnds);
                return null;
            }
            return (NotLevelRefusal, FormattableString.Invariant($"ends {Mathf.Abs(off):F1} m {(off > 0f ? "over" : "under")} its place, not level with it, and no approach staircase onto it"));
        }
        return At(true, startCenter, startRadius, startAtPlace) ?? At(false, endCenter, endRadius, endAtPlace);
    }

    /// <summary>How many road ends were carried on from a place's ring to its edge (<see cref="CarryOnToEdges"/>).</summary>
    private static int m_carriedToEdge;
    /// <summary>For tests and the summary: road ends carried on to their place's edge this generation.</summary>
    internal static int CarriedToEdgeCount => m_carriedToEdge;
    /// <summary>A road end nearer its place's edge than this is not carried on.</summary>
    private const float CarryOnMin = 0.5f;

    /// <summary>
    /// The last stretch of a road whose end arrives level with a place that has no flat nor landing (not placed first, no single
    /// levelled platform, <see cref="LocationLevelling.ApproachHeight"/>): from its end at the ring (<see cref="KeepOutEdge"/>)
    /// straight on to the place's edge on its side, where its arrival height was measured (<see cref="ArrivalHeight"/>,
    /// <see cref="RoadSiteProtection.ArrivalRadiusAt"/>), from the road's height to that height, as a crypt's road is carried on
    /// to its door. In game: AbandonedLogCabin04 (-1867, 7101), the road at the right height but its end stopping
    /// short of the cabin, the road's whole blend kept outside the ground the cabin shapes. Not on the fallback (an end off
    /// level, <see cref="HeightNeeded"/>), nor from an end on stairs or with an approach staircase; none where the stretch
    /// cannot be planned (<see cref="RoadSpatialGrid.PlanSiteEntry"/>) or its end is in water: the road keeps its end.
    /// </summary>
    private static List<RoadSpatialGrid.PlannedPath> CarryOnToEdges(List<Vector2> path, List<RoadSpatialGrid.PlannedPath> plans,
        List<RoadStairSpan> stairs, List<RoadStairSpan> approaches, float width, Vector2 startCenter, float startRadius,
        Vector2 endCenter, float endRadius, bool startAtPlace, bool endAtPlace)
    {
        var carried = new List<RoadSpatialGrid.PlannedPath>();
        var world = GenerationWorld;
        if (world == null || path.Count < 2 || plans.Count == 0) return carried;
        void At(bool atStart, Vector2 centre, float radius, bool atPlace)
        {
            if (!atPlace || radius <= 0f || PlacedSites.TryGetReady(centre, out _) || !HeightNeeded(centre)) return;
            if (approaches.Exists(a => a.FromIndex == (atStart ? 0 : path.Count - 1))) return;
            if (RoadEnd(atStart, path, plans, stairs) is not var (end, height, onStairs) || onStairs != null) return;
            float root = LocationLevelling.CentreHeight(centre, world);
            IReadOnlyList<LevelOp>? ops = LocationLevelling.OpsAt(centre);
            if (LocationLevelling.ApproachHeight(root, ops).HasValue) return;
            // Its end stood off a custom location standing above it (StandOffLevel) stays there.
            if (StandOffLevel(centre, radius) is float own && height < own) return;
            Vector2 out1 = end - centre;
            float arrival = RoadSiteProtection.ArrivalRadiusAt(centre, radius), from = out1.magnitude;
            if (from - arrival < CarryOnMin) return;
            if (ArrivalHeight(centre, radius, end) is not float place || Mathf.Abs(height - place) > ReachRise) return;
            Vector2 edge = centre + out1 * (arrival / from);
            float Ground(Vector2 q) => LocationLevelling.PlacedGroundAt(q, centre, root, ops, BiomeBlendedHeight.GetBlendedHeight(q.x, q.y, world));
            world.GetRiverWeight(edge.x, edge.y, out float river, out _);
            if (!RoadWater.BuiltDry(Ground(edge), river)) return;
            var line = new List<Vector2> { end, edge };
            RoadSpatialGrid.PlannedPath? plan = RoadGrade.Trial(() => RoadSpatialGrid.PlanSiteEntry(line, width, world, height, place, Ground));
            if (plan != null && plan.Points.Count >= 2) carried.Add(plan);
        }
        At(true, startCenter, startRadius, startAtPlace);
        At(false, endCenter, endRadius, endAtPlace);
        return carried;
    }

    /// <summary>The refusal a road whose end is not level with its place is counted under (<see cref="EndNotLevel"/>).</summary>
    internal const string NotLevelRefusal = "end not level with its place";
    /// <summary>The refusal a road on the fallback whose end is behind a ridge from its place is counted under
    /// (<see cref="EndNotLevel"/>, <see cref="BehindRidge"/>).</summary>
    internal const string RidgeRefusal = "end behind a ridge from its place";

    /// <summary>Read the crossing registry under the same gate used by commits.
    /// Another island may append while this road looks for shared banks.</summary>
    internal static void SnapToExistingCrossings(IEnumerable<RoadCrossing> crossings)
    {
        lock (m_recordGate)
        {
            // Only this island's crossings and untagged ones: another island's is not this island's network (CrossIslandReads).
            int me = CrossIslandReads.Me;
            RoadCrossing? passed = null;
            bool Seen(RoadCrossing existing) { if (CrossIslandReads.Sees(me, existing.Island)) return true; passed = existing; return false; }
            // A run of back-to-back crossings (a raised pool, a wade, a bridge: one river crossed in
            // pieces) that spans the same water as a crossing another road built is replaced by that
            // one crossing (measured: a 3-piece chain ran 6 m beside a 53 m bridge; piece by piece
            // no bank matched, end to end both did).
            if (CorridorSnap > RoadSnap && crossings is List<RoadCrossing> list && list.Count > 1)
            {
                var crossingsList = list;
                crossingsList.Sort((x, y) => x.FromIndex.CompareTo(y.FromIndex));
                for (int a = 0; a < crossingsList.Count; a++)
                {
                    int b = a;
                    while (b + 1 < crossingsList.Count && Vector2.Distance(crossingsList[b].ToBank, crossingsList[b + 1].FromBank) < 1f) b++;
                    if (b == a) continue;
                    var span = RoadCrossing.Between(crossingsList[a].FromBank, crossingsList[b].ToBank, crossingsList[a].RiverbedHeight,
                        (crossingsList[a].FromBank + crossingsList[b].ToBank) * 0.5f, 0f, crossingsList[a].Kind, crossingsList[a].Style);
                    RoadCrossing? match = null; float best = float.MaxValue;
                    foreach (RoadCrossing existing in m_roadCrossings)
                        if (SameCorridorCrossing(existing, span, out float d) && d < best && Seen(existing)) { best = d; match = existing; }
                    if (match == null) { a = b; continue; }
                    var first = crossingsList[a];
                    int toIndex = crossingsList[b].ToIndex;
                    var toLand = crossingsList[b].ToLand;
                    first.SnapTo(match);
                    first.Shared = true;
                    first.ToIndex = toIndex;
                    first.ToLand = toLand;
                    crossingsList.RemoveRange(a + 1, b - a);
                    System.Threading.Interlocked.Increment(ref m_chainsShared);
                }
            }
            foreach (RoadCrossing crossing in crossings)
            {
                RoadCrossing? match = null;
                foreach (RoadCrossing existing in m_roadCrossings)
                    if (RoadCrossing.SameBanks(existing, crossing) && Seen(existing)) { match = existing; break; }
                // Two roads crossing the same water side by side share one crossing (the corridor
                // snap's rule for crossings; measured: two roads left one place through a pool,
                // each with its own ford 6.5 m apart). Nearest qualifying site wins.
                if (match == null && CorridorSnap > RoadSnap)
                {
                    float best = float.MaxValue;
                    foreach (RoadCrossing existing in m_roadCrossings)
                        if (SameCorridorCrossing(existing, crossing, out float d) && d < best && Seen(existing)) { best = d; match = existing; }
                }
                if (match != null)
                {
                    crossing.SnapTo(match);
                    crossing.Shared = true;
                }
            }
            if (passed != null) CrossIslandReads.Skipped(CrossIslandReads.Kind.Crossings, passed.Island, passed.Center);
        }
    }

    /// <summary>Both banks within <see cref="CorridorSnap"/> of the other crossing's (either order),
    /// heading within <see cref="CorridorAngle"/>, bank ground within <see cref="CorridorHeight"/>.</summary>
    internal static bool SameCorridorCrossing(RoadCrossing existing, RoadCrossing c, out float distance)
    {
        distance = float.MaxValue;
        if (Mathf.Abs(Vector2.Dot(existing.Direction, c.Direction)) < Mathf.Cos(CorridorAngle * Mathf.PI / 180f)) return false;
        bool same = (existing.FromBank - c.FromBank).sqrMagnitude <= (existing.FromBank - c.ToBank).sqrMagnitude;
        Vector2 ef = same ? existing.FromBank : existing.ToBank, et = same ? existing.ToBank : existing.FromBank;
        float df = Vector2.Distance(ef, c.FromBank), dt = Vector2.Distance(et, c.ToBank);
        if (df > CorridorSnap || dt > CorridorSnap) return false;
        var world = WorldGenerator.instance;
        if (world != null)
        {
            float H(Vector2 q) => BiomeBlendedHeight.GetBlendedHeight(q.x, q.y, world);
            if (Mathf.Abs(H(ef) - H(c.FromBank)) > CorridorHeight || Mathf.Abs(H(et) - H(c.ToBank)) > CorridorHeight) return false;
        }
        distance = df + dt;
        return true;
    }

    private static int m_roadsGeneratedCount = 0;
    private static List<(Vector2 position, string label)> m_roadStartPoints = new();
    private static readonly List<RoadCrossing> m_roadCrossings = new();
    private static readonly List<CoastalLandingPlace> m_coastalLandings = new();

    public static bool RoadsGenerated => m_roadsGenerated;
    /// <summary>
    /// Whether the world's locations are in place, so roads may be loaded or built.
    ///
    /// Not just "our event fired". Valheim 1.0 writes ZoneSystem's
    /// m_locationsGenerated straight from the save when a world is read from
    /// disk, bypassing the property setter that raises
    /// GenerateLocationsCompleted -- so on an existing world the event never
    /// arrives, no matter how early we subscribed. Before 1.0 the setter was the
    /// only writer and the event always came. Ask the game what is true rather
    /// than relying on having been told.
    /// </summary>
    public static bool IsLocationsReady =>
        m_locationsReady || (ZoneSystem.instance != null && ZoneSystem.instance.LocationsGenerated);

    /// <summary>River crossings of the generated roads (fords and bridges
    /// prototypes): empty unless the network was generated with either on.</summary>
    public static IReadOnlyList<RoadCrossing> GetRoadCrossings() => m_roadCrossings;
    /// <summary>The shore landings of the islands the network was built for (name <see cref="RoadCoastalLandings.Name"/>), by
    /// island then position, each with whether its island's destinations took it and whether the network reaches it.
    /// Saved with the network.</summary>
    public static IReadOnlyList<CoastalLandingPlace> GetCoastalLandings()
    {
        lock (m_recordGate)
            return m_coastalLandings.OrderBy(l => l.IslandId).ThenBy(l => l.Position.x).ThenBy(l => l.Position.z).ToList();
    }
    /// <summary>The crossings and the road start points in a fixed order, whichever island committed its roads first
    /// (the same rule as <see cref="StairStructures.SortCanonical"/>): generation lays islands in parallel, and the saved
    /// bytes must not depend on which finished first. Crossings by their banks, start points by position then label. No
    /// reader takes a crossing by its index or reads a road's crossings as a run (they are matched by banks, walked as
    /// sites, or counted), so the order within a road is not kept.</summary>
    internal static void SortRecordsCanonical()
    {
        lock (m_recordGate)
        {
            m_roadCrossings.Sort((a, b) =>
            {
                int c = a.FromBank.x.CompareTo(b.FromBank.x);
                if (c == 0) c = a.FromBank.y.CompareTo(b.FromBank.y);
                if (c == 0) c = a.ToBank.x.CompareTo(b.ToBank.x);
                if (c == 0) c = a.ToBank.y.CompareTo(b.ToBank.y);
                if (c == 0) c = a.Kind.CompareTo(b.Kind);
                if (c == 0) c = a.Style.CompareTo(b.Style);
                if (c == 0) c = a.ValleyJump.CompareTo(b.ValleyJump);
                return c != 0 ? c : a.RiverbedHeight.CompareTo(b.RiverbedHeight);
            });
            m_roadStartPoints.Sort((a, b) =>
            {
                int c = a.position.x.CompareTo(b.position.x);
                if (c == 0) c = a.position.y.CompareTo(b.position.y);
                return c != 0 ? c : string.CompareOrdinal(a.label, b.label);
            });
        }
    }
    /// <summary>The crossings built so far, copied under the gate commits take (another island may append meanwhile): on an
    /// island worker, its own island's and untagged ones (CrossIslandReads).</summary>
    internal static List<RoadCrossing> CrossingsSnapshot()
    {
        lock (m_recordGate) return Visible(m_roadCrossings);
    }

    /// <summary>A copy of <paramref name="all"/> holding what this thread sees of it (<see cref="CrossIslandReads.Sees(int, byte)"/>).
    /// The caller holds the gate. Not counted: the whole list is filtered, near or far; the reads near a place count.</summary>
    private static List<RoadCrossing> Visible(List<RoadCrossing> all)
    {
        int me = CrossIslandReads.Me;
        if (me == 0) return new List<RoadCrossing>(all);
        var visible = new List<RoadCrossing>(all.Count);
        foreach (RoadCrossing c in all)
            if (CrossIslandReads.Sees(me, c.Island)) visible.Add(c);
        return visible;
    }
    public static bool RoadsLoadedFromZDO => m_roadsLoadedFromZDO;
    /// <summary>Whether the network must be saved in full (<see cref="m_networkDirty"/>).</summary>
    public static bool NetworkDirty => m_networkDirty;
    public static bool RoadsAvailable => !RoadLifecycleManager.WorkersOwnWorld && NetworkInMemory;
    /// <summary>Whether a network is in memory (generated, loaded, or added to), whoever may use it now: a generation that
    /// replaces it asks this, as <see cref="RoadsAvailable"/> is false while the generation's workers own the world.</summary>
    internal static bool NetworkInMemory => m_roadsGenerated || m_roadsLoadedFromZDO || m_networkDirty;

    /// <summary>
    /// Whether a world without persisted roads generates its network when the
    /// player spawns. Off (PROCEDURALROADS_GENERATE_ROADS_ON_LOAD=0), the world
    /// stays road-free until road_generate or road_regen_island asks; a
    /// validation loop on one site then pays seconds, not a whole-world
    /// generation. There is no config key for this: it is a switch for
    /// developing the mod, and a config key cannot be taken back once written.
    /// </summary>
    public static bool GenerateOnLoad = true;

    /// <summary>
    /// Between choosing the destinations and building the roads: the preparation steps that place the places their
    /// landing waits for (<see cref="SitesFirstLoad"/>), each step a stage with its own progress on the loading panel.
    /// Every generation runs them (<see cref="PrepareSteps"/>): the places a generation will visit are the places it
    /// places first. Placing waits for the game's frames, so every generation is the owned one, run over frames
    /// (<see cref="RoadLifecycleManager.Tick"/>); only the offline tools drain a preparation in one call, with a hook of
    /// their own that needs no frames. Settable for tests.
    /// </summary>
    internal static Func<GenerationWork, RoadPreparation, IEnumerable<bool>>? SitesFirst;

    internal static void ApplyGeneratedNetworkToLoadedZones()
    {
        // Zones generated during the loading screen (around the login position)
        // exist before the network does; give them their roads now.
        int zones = RoadTerrainModifier.ApplyToLoadedZones();
        Log.LogDebug($"Queued road terrain for {zones} zone(s) loaded before generation");
        int bridgeZones = StructurePlacement.SpawnInLoadedZones();
        if (bridgeZones > 0)
            Log.LogDebug($"Spawned bridges into {bridgeZones} zone(s) loaded before generation");
    }

    /// <summary>
    /// Get the start points of all generated roads for visualization.
    /// </summary>
    public static IReadOnlyList<(Vector2 position, string label)> GetRoadStartPoints() => m_roadStartPoints;

    public static void Initialize() => Reset();

    /// <summary>
    /// Called when location generation is complete. Does not trigger road generation.
    /// </summary>
    public static void MarkLocationsReady()
    {
        m_locationsReady = true;
        Log.LogDebug("Locations marked ready for road generation");
    }

    /// <summary>
    /// Called when roads have been loaded from ZDO persistence (existing world).
    /// </summary>
    public static void MarkRoadsLoadedFromZDO()
    {
        m_roadsLoadedFromZDO = true;
        Log.LogDebug("Roads marked as loaded from ZDO persistence");
    }

#if ROADS_TESTS
    /// <summary>
    /// Tests: a whole-world generation run to its end as the game runs it (<see cref="RoadLifecycleManager.Begin"/>, its
    /// frames pumped): with <paramref name="force"/> as road_generate runs it (<see cref="RoadLifecycleManager.BeginForOperator"/>),
    /// without as the load does. Throws as the game's console would say it: busy, or the generation failed.
    /// </summary>
    internal static void GenerateRoads(bool force = false)
    {
        if (RoadsAvailable && !force) return;
        string? refused = force
            ? RoadLifecycleManager.BeginForOperator(null, line => Log.LogInfo(line))
            : RoadLifecycleManager.Begin(new RoadLifecycleManager.GenerationRequest()) ? null : RoadLifecycleManager.Busy;
        if (refused == RoadLifecycleManager.Busy) throw new InvalidOperationException(refused);
        RoadLifecycleManager.RunToEndForTests();
    }

    /// <summary>Tests: road_regen_island at <paramref name="worldPos"/>, run to its end as the game runs it. Returns whether it
    /// was regenerated, and its summary or why not.</summary>
    internal static bool RegenerateIslandAt(Vector3 worldPos, out string summary)
    {
        GenerationWork? done = null;
        var told = new List<string>();
        string? refused = RoadLifecycleManager.BeginForOperator(worldPos, told.Add, work => done = work);
        if (refused == RoadLifecycleManager.Busy) throw new InvalidOperationException(refused);
        if (refused == null) RoadLifecycleManager.RunToEndForTests();
        summary = done?.Summary ?? refused ?? (told.Count > 0 ? told[told.Count - 1] : "Nothing was regenerated");
        return done != null;
    }
#endif

    /// <summary>
    /// A generation's main-thread preparation, as steps the owned generation runs over frames: the destinations chosen,
    /// the places among them that wait for their placing placed first (<see cref="SitesFirst"/>) and made ready, then
    /// everything the island workers read. The one way a network is prepared, for the whole world or (with
    /// <paramref name="island"/>) for the one island there, which replaces the network.
    /// </summary>
    internal static RoadPreparation BeginPreparation(bool force, Vector3? island = null)
    {
        var world=WorldGenerator.instance;
        var zones=ZoneSystem.instance;
        var options=NetworkOptions.Snapshot();
        var start=DateTime.Now;
        int percentage=IslandRoadPercentage,maxLocations=MaxLocationsPerIsland;
        return new RoadPreparation(p=>PrepareSteps(force,island,world,zones,options,start,percentage,maxLocations,p));
    }
    private static IEnumerable<bool> PrepareSteps(bool force,Vector3? only,WorldGenerator? capturedWorld,ZoneSystem? capturedZones,
        RoadNetworkOptions options,DateTime startTime,int percentage,int maxLocations,RoadPreparation preparation)
    {
        if (!RoadLifecycleManager.HoldsTheNetwork)
        {
            preparation.Refuse(RoadLifecycleManager.NotTheServer);
            yield break;
        }
        if (NetworkInMemory && !force) yield break;
        // The whole world starts afresh here; one island only once its island is found and has places to go to
        // (a point in the ocean leaves the network as it is).
        if (only == null && force && NetworkInMemory) ResetData();
        if (capturedWorld == null || capturedZones == null)
        {
            preparation.Refuse(capturedWorld == null ? "WorldGenerator not available, cannot generate roads" : "ZoneSystem not available, cannot generate roads");
            yield break;
        }

        preparation.BeginStage("Preparing road settings"); yield return true;
        Log.LogDebug("Starting road network generation...");

        RegisterConfiguredLocations();
        var world = capturedWorld;
        if (only == null)
        {
            NetworkCutoff.BeginNewNetwork();
            ResetGenerationState(world, options);
        }

        LocationData? locations=null;
        foreach(var step in GatherLocationSteps(capturedZones,preparation,value=>locations=value)) yield return step;
        if(locations==null) { preparation.Refuse("No location data available"); yield break; }

        List<Island>? islands=null;
        foreach(var step in IslandDetector.DetectRoadIslandsSteps(world,options,preparation,value=>islands=value)) yield return step;
        if (only is Vector3 at)
        {
            Island? there = islands!.FirstOrDefault(i => i.ContainsPoint(at));
            if (there == null) { preparation.Refuse($"No island at ({at.x:F0},{at.z:F0})"); yield break; }
            islands = new List<Island> { there };
        }
        preparation.BeginStage("Indexing island membership",islands!.Sum(i=>(long)i.Cells.Count),"cells"); yield return true;
        foreach(var island in islands!)
            foreach(var step in island.PrepareMembership(preparation)) yield return step;

        preparation.BeginStage("Finding island destinations",(long)islands.Count*locations.Value.AllLocations.Count,"location checks"); yield return true;
        var eligible=new Dictionary<Island,List<(string name,Vector3 position,float radius)>>();
        foreach(var island in islands)
        {
            var found=new List<(string name,Vector3 position,float radius)>();
            foreach(var loc in locations.Value.AllLocations)
            {
                if(IsIslandDestination(island,loc,world,options)) found.Add(loc);
                if(preparation.Checkpoint()) yield return true;
            }
            eligible[island]=found;
        }
        List<Island> selectedIslands;
        if (only != null)
        {
            if (eligible[islands[0]].Count == 0) { preparation.Refuse($"Island {islands[0].Id} has no road-eligible locations"); yield break; }
            selectedIslands = islands;
        }
        else
        {
            selectedIslands = RoadNetworkSelection.Islands(islands, i => eligible[i].Count, percentage);
            Log.LogDebug($"Islands: {islands.Count} total, {selectedIslands.Count} selected (target {IslandRoadPercentage}%; boss islands are not automatically included)");
        }

        preparation.BeginStage("Selecting road destinations",selectedIslands.Count,"islands"); yield return true;
        var work = new GenerationWork(startTime, oneIsland: only != null);
        foreach (var island in selectedIslands)
        {
            var candidates = eligible[island];
            if(candidates.Count==0) { preparation.Checkpoint(); yield return true; continue; }
            int maxLocs = RoadNetworkSelection.Quota(island.ApproxArea,maxLocations);
            var selected = SelectIslandDestinations(island, candidates, maxLocs);
            Log.LogDebug($"Island {island.Id}: {candidates.Count} candidates -> {selected.Count} selected (max {maxLocs}, area {island.ApproxArea/1_000_000:F1}km²)");
            work.Islands.Add(new IslandWork(island, selected,
                island.ContainsPoint(locations.Value.SpawnPoint) ? locations.Value.SpawnPoint : (Vector3?)null,
                locations.Value.SpawnRadius));
            preparation.Checkpoint(); yield return true;
        }
        if (only != null)
        {
            // One island replaces the whole network, from here as a whole-world generation does from its start.
            bool locationsWereReady = m_locationsReady;
            ResetData();
            m_locationsReady = locationsWereReady;
            NetworkCutoff.BeginNewNetwork();
            ResetGenerationState(world, options);
        }
        // The places whose landings wait for their placing are placed now, before their roads; then their ground is
        // rebuilt and their landings found, before the islands share them. Both before "Reading saved location
        // placements": it indexes the placements, these among them.
        if (SitesFirst != null)
            foreach (var step in RoadPreparation.Own(SitesFirst(work, preparation))) yield return step;
        foreach (var step in PlacedSites.PrepareSteps(NaturalGround(world), preparation)) yield return step;
        if (RoadsLog && PlacedSites.ReadyCount > 0) Log.LogInfo($"  {PlacedSites.ReadyCount} place(s) placed before the roads, their roads to go onto their landings");
        // Which of the destinations get no road, their landing not known (PlacedSites.NotPlaced): once, here on the
        // main thread, for the island workers to read.
        PlacedSites.FindNotPlaced(work.AllSelected.Select(d => new Vector2(d.position.x, d.position.z)), world);
        RoadParallel.IslandsRunning = Math.Min(work.Islands.Count, work.Workers);
        foreach(var step in PrepareAndSealSteps(preparation)) yield return step;
        CatalogueDoors(work);
        preparation.Result=work;
    }

    internal sealed class IslandWork
    {
        internal readonly Island Island;
        internal readonly List<(string name, Vector3 position, float radius)> Locations;
        internal readonly Vector3? Spawn;
        internal readonly float Radius;
        internal IslandWork(Island island, List<(string name, Vector3 position, float radius)> locations, Vector3? spawn, float radius)
        { Island = island; Locations = locations; Spawn = spawn; Radius = radius; }
    }

    internal sealed class GenerationWork
    {
        internal readonly DateTime StartTime;
        /// <summary>One island's regeneration (road_regen_island): it says how it went in <see cref="Summary"/>.</summary>
        internal readonly bool OneIsland;
        /// <summary>One island's regeneration, as it went: set once the network is complete.</summary>
        internal string? Summary;
        internal GenerationWork(DateTime startTime, bool oneIsland = false) { StartTime = startTime; OneIsland = oneIsland; }
        /// <summary>This generation's network replaced the one in memory (set as <see cref="CompleteGeneration"/> publishes it):
        /// a failure after this keeps it; one before discards it.</summary>
        internal bool Published;
        internal readonly List<IslandWork> Islands = new();
        internal readonly int Workers = RoadParallel.IslandWorkers;
        internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Finished = new();
        /// <summary>Every island's chosen destinations, in the order the islands were chosen.</summary>
        internal IEnumerable<(string name, Vector3 position, float radius)> AllSelected => Islands.SelectMany(i => i.Locations);
        internal System.Threading.CancellationToken Cancellation;
        internal RoadIslandActivity? Activity;
        internal void Compute()
        {
            // One island at a time to whichever worker is free, in the islands' chosen order (RoadNetworkSelection.Islands:
            // most eligible destinations first). The default partitioning hands each worker a run of islands up front, so
            // a long island could wait behind another on its worker while others stood idle.
            System.Threading.Tasks.Parallel.ForEach(
                System.Collections.Concurrent.Partitioner.Create(Islands, System.Collections.Concurrent.EnumerablePartitionerOptions.NoBuffering),
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = Cancellation },
                (island, state, number) =>
                {
                    Cancellation.ThrowIfCancellationRequested();
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var token=Activity?.Claim((int)number+1,island.Locations.Count);
                    IslandActivity=token;
                    CrossIslandReads.Current = (int)number + 1;
                    if(Finder!=null) Finder.Activity=token;
                    try
                    {
                    m_islandRoads = 0;
                    if (SearchStats.Enabled) SearchStats.ResetThread();
                    GenerateIslandRoads(island.Island, island.Locations, island.Spawn, island.Radius);
                    Cancellation.ThrowIfCancellationRequested();
                    CountDestinationsReached(island.Island, island.Locations);
                    Finished.Enqueue(SearchStats.Enabled
                        ? $"island {number + 1} (id {island.Island.Id}): {m_islandRoads} road(s) from {island.Locations.Count} destination(s) " +
                          $"in {clock.Elapsed.TotalSeconds:F0}s ({SearchStats.BriefThread()}); so far {SearchStats.Brief()}"
                        : $"island {number + 1}: {m_islandRoads} road(s) from {island.Locations.Count} destination(s) in {clock.Elapsed.TotalSeconds:F0}s");
                    }
                    finally { if(Finder!=null) Finder.Activity=null; IslandActivity=null; CrossIslandReads.Current = 0; token?.Finish(); }
                });
        }
    }

    internal static void CompleteGeneration(GenerationWork work)
    {
        LocationLevelling.Unseal();
        ReportMissesAfterSealing();
        DropBuriedPieces();
        LogTerrainMemoCounters();

        TimeSpan elapsed = DateTime.Now - work.StartTime;
        if (work.OneIsland)
        {
            Island island = work.Islands[0].Island;
            work.Summary =
                $"Island {island.Id} ({island.ApproxArea / 1_000_000f:F1}km²): " +
                $"{work.Islands[0].Locations.Count} locations, {m_roadsGeneratedCount} roads, " +
                $"{RoadSpatialGrid.Statistics.LengthText()} in {elapsed.TotalSeconds:F1}s; " +
                $"{m_destinationsConnected}/{m_destinationsSelected} destinations connected ({m_reachedOffLevel} off level), " +
                $"{m_roadsWithNoRoute} attempt(s) found no route" +
                (m_refusalReasons.IsEmpty ? "" : "; refused when built: " + string.Join("; ",
                    System.Linq.Enumerable.Select(System.Linq.Enumerable.OrderByDescending(m_refusalReasons, kv => kv.Value), kv => $"{kv.Value} {kv.Key}")));
            // Also to the log: a console answer is lost when the command outlives
            // the caller's timeout, and a check needs a line to wait for.
            Log.LogInfo($"Island regenerated: {work.Summary}");
        }
        Log.LogInfo($"Roads done: {m_roadsGeneratedCount} road(s), " +
                    $"{RoadSpatialGrid.Statistics.LengthText("F1",1000f," km")}, connecting " +
                    $"{m_destinationsConnected} of {m_destinationsSelected} destination(s), " +
                    $"in {elapsed.TotalSeconds:F0}s.");
        int unconnected = m_destinationsSelected - m_destinationsConnected;
        Log.LogInfo($"  {m_carriedToEdge} road end(s) carried on from a place's ring to its edge, level with it (a place with no flat nor landing).");
        Log.LogInfo($"  {m_reachedOffLevel} destination(s) reached off level: no road could arrive level with them, so on the fallback a road ending off their level reaches them (outside the Mountains).");
        Log.LogInfo($"  {m_behindRidge} destination(s) left unserved behind a ridge: on the fallback the network in their reach ends behind ground over {RidgeRise:F0} m above it and the place.");
        if (m_destinationsNotPlaced > 0)
            Log.LogInfo($"  {m_destinationsNotPlaced} destination(s) given no road: their landing turns with them and they were not placed before their roads.");
        if (unconnected > 0)
            // Attempts, not destinations: a place is offered several candidate
            // connections and every one of them can fail before it is given up
            // on, so these are larger than the number left unconnected.
            Log.LogInfo($"  {unconnected} destination(s) left unconnected after every candidate was tried: " +
                        $"{m_roadsWithNoRoute} attempt(s) found no route, " +
                        $"{m_roadsRefusedForGrade} could not be built (grade, turn room, footprint or water).");
        if (SearchStats.Enabled)
        {
            List<RoadSpatialGrid.Junction> junctions;
            lock (RoadSpatialGrid.RecordGate) junctions = new List<RoadSpatialGrid.Junction>(RoadSpatialGrid.Junctions);
            if (junctions.Count > 0)
                // Where a road took another's height: the largest earthworks are where to look for
                // ridges (at 896,4416 a junction created a large earthwork ridge that looked bad).
                Log.LogInfo($"  Junctions (step {RoadPathfinder.JunctionStep:0.##} m): {junctions.Count} stretch(es) held to a road already laid; " +
                    FormattableString.Invariant($"pulled over 2 m {junctions.Count(j => Mathf.Abs(j.Pull) > 2f)}, over 4 m {junctions.Count(j => Mathf.Abs(j.Pull) > 4f)}; ") +
                    FormattableString.Invariant($"earthwork over 2 m {junctions.Count(j => Mathf.Abs(j.Earth) > 2f)}, over 4 m {junctions.Count(j => Mathf.Abs(j.Earth) > 4f)}; ") +
                    "pulled over 2 m by place: " + string.Join(", ", junctions.Where(j => Mathf.Abs(j.Pull) > 2f).GroupBy(j => j.Where).Select(g => $"{g.Key} {g.Count()}")) + "; " +
                    "largest earthwork (+ fill, - cut; pulled from the road's own profile): " +
                    string.Join(", ", junctions.Where(j => !float.IsNaN(j.Earth)).OrderByDescending(j => Mathf.Abs(j.Earth)).Take(12)
                        .Select(j => FormattableString.Invariant($"({j.At.x:F0}, {j.At.y:F0}) {j.Earth:+0.0;-0.0} m, pulled {j.Pull:+0.0;-0.0}"))));
        }
        if (!m_refusalReasons.IsEmpty)
            Log.LogInfo("  Roads refused when built, by rule: " + string.Join("; ",
                System.Linq.Enumerable.Select(System.Linq.Enumerable.OrderByDescending(m_refusalReasons, kv => kv.Value), kv => $"{kv.Value} {kv.Key}")));
        if (EarthworkCapped)
            Log.LogInfo($"  Earthwork cap {EarthworkCap:F0} m: {m_earthworkReroutes} road(s) re-routed round deep cuts or fills, {m_earthworkUnavoided} refused as still past it; {m_pullReroutes} re-routed round a stretch held over {HoldPullLimit:F0} m off its profile to a road already laid; {m_ridgeReroutes} re-routed off a trench or ridge, {m_ridgeStairs} onto stairs");
        if (RoadCrossingDetector.ToWater)
            Log.LogInfo($"  Bridge ends walked in off land above the deck: {m_offLandEnds}; deck line through the ground ({RoadCrossingDetector.DeckLineStep:0} m samples, candidates included): {RoadCrossingDetector.DeckLineWalkedIn} bridge(s) ended at the first dry ground, {RoadCrossingDetector.DeckLineRefused} refused, {RoadCrossingDetector.DeckLineSpanDropped} ford span(s) laid as another style");
        if (RoadCrossingDetector.Shallows)
        {
            int sr = 0, sw = 0, ss = 0;
            lock (m_recordGate)
                foreach (var c in m_roadCrossings)
                    if (c.Shallow) { if (c.Style == FordStyle.Raise) sr++; else if (c.Style == FordStyle.Wade) sw++; else ss++; }
            Log.LogInfo($"  Shallow-water fords (pools the road walks): {sr} raised, {sw} waded, {ss} spanned; {m_shallowFordsWalked} road plan(s) walked their pools instead (a ford made them unbuildable)");
        }
        if (CorridorSnap > RoadSnap)
            Log.LogInfo($"  Corridor snap (a road alongside another merges onto it, within {CorridorSnap:F0} m): {m_corridorSnapped} waypoint(s) moved, {m_chainsShared} chain(s) of crossings shared, candidates included; {m_reEndedOnApproach} road(s) ended on a place's approach's road instead of beside it, {m_reEndRefused} refused so");
        if (!RoadSpatialGrid.BendFallbacks.IsEmpty)
            Log.LogInfo("  Bend rounding refused with full arcs (plans, trials included): " + string.Join(", ",
                System.Linq.Enumerable.Select(System.Linq.Enumerable.OrderBy(RoadSpatialGrid.BendFallbacks, kv => kv.Key.Item1.ToString() + kv.Key.Item2),
                    kv => $"{(kv.Key.Item1 == RoadSpatialGrid.BendStage.Least ? "least arcs" : "corners kept")} after '{kv.Key.Item2}' {kv.Value}")));
        if (RoadJunctionFillet.Radius > 0f)
            Log.LogInfo($"  Junction fillets (a road's end carried onto the road it joins, its inside corners filled to a {RoadJunctionFillet.Radius:F0} m curve): {m_junctionFills} junction(s)");
        LogStairEnds();
        Log.LogInfo($"  Staircase start pads (the road levelled where a staircase starts at its end, RoadEndRing.PadUnder): {RoadStairRuns.PadsAtFeet} at feet, {RoadStairRuns.PadsAtTops} at tops ({RoadStairRuns.PadsPartial} with points over falling ground left out); not laid: "
            + (RoadStairRuns.PadsRefused.IsEmpty ? "none" : string.Join(", ", System.Linq.Enumerable.Select(System.Linq.Enumerable.OrderBy(RoadStairRuns.PadsRefused, kv => kv.Key, StringComparer.Ordinal), kv => $"{kv.Key} {kv.Value}"))));
        Log.LogInfo($"  Staircase end floors carried over by the road to the landing row next to the stair (RoadStairRuns.CarryRoadOntoStairs): {RoadStairRuns.FeetCarriedOnto} feet, {RoadStairRuns.TopsCarriedOnto} tops; ends (every lay tried) with 1/2/3/4+ floor rows {string.Join("/", RoadStairRuns.EndsByFloorRows)}, left as laid by the min-rows switch (PROCEDURALROADS_CARRY_MIN_ROWS {RoadStairRuns.CarryMinRows}) {RoadStairRuns.EndsSkippedByRows}, tried {RoadStairRuns.EndsTried}, carried {RoadStairRuns.EndsCarried} (one landing row kept); {RoadStairRuns.RoadOntoCalls} road(s) tried onto an end, {RoadStairRuns.RoadOntoUnplanned} refused before planning, {RoadStairRuns.CarriesReused} end(s) decided again from this generation's cache; kept (every lay tried): "
            + (RoadStairRuns.EndFloorsKept.IsEmpty ? "none" : string.Join(", ", System.Linq.Enumerable.Select(System.Linq.Enumerable.OrderBy(RoadStairRuns.EndFloorsKept, kv => kv.Key, StringComparer.Ordinal), kv => $"{kv.Key} {kv.Value}"))));
        Log.LogInfo(FormattableString.Invariant($"  One road (RoadRide): {RoadRide.RiddenMetres:F0} m of planned road riding the network left to the road laid there, {RoadRide.CrossingsRidden} crossing(s) left to the road that built them, {RoadRide.CaughtAtCommit} ridden stretch(es) caught at the commit"));
        LogDoors();
        Log.LogInfo($"  Cross-island reads (an island worker's reads that passed over a road point, ride line, staircase, crossing or viaduct another island committed; islands build as separate networks): {CrossIslandReads.Describe()}");
        Log.LogInfo($"  Structure ends met by earthwork (their landing filled over ground under the road's floor, StructureEnds.MeetEnds): {m_endEarthworks}; left from their landing pad off their line: {m_endPads}; on a landing shared with the next structure: {m_endShared}; where the road's last point stands, off their line: {m_endStands}; steps laid at them: {m_endSteps}; ends with none, one, more: {m_endFlat}/{m_endOneStep}/{m_endMoreSteps}");
        if (RoadsLog)
            Log.LogInfo($"  Crossing ends: searched again round a crossing with an end no way of meeting reaches {m_crossingsUnmetSearchedRound}, refused with one past those searches {m_bridgeEndsUnmet}; viaducts: {m_viaductsLaid} Mistlands viaduct(s) laid over dry valleys (DvergrViaduct), searched again without one the built road did not allow {m_viaductsRefusedAtEnds}");
        if (RoadsLog)
        {
            // The kits' shares outside the Mistlands, counted over the sites: each the kit it has, or the one its first build
            // would choose among the kits installed now (BridgeStructures.KitFor).
            int rail = 0, braced = 0, stone = 0;
            List<RoadCrossing> sites;
            lock (m_recordGate) sites = BridgeLayout.DistinctSites(m_roadCrossings);
            foreach (RoadCrossing c in sites)
            {
                BridgeStructures.Kit? kit = BridgeStructures.KitFor(c, GenerationWorld);
                if (kit == BridgeStructures.Kit.WoodRail) rail++;
                else if (kit == BridgeStructures.Kit.WoodBraced) braced++;
                else if (kit == BridgeStructures.Kit.Stone) stone++;
            }
            Log.LogInfo($"  Bridge kits outside the Mistlands (as first built with the kits installed now): {rail} WoodRail, {braced} WoodBraced, {stone} Stone");
        }
        if (RoadWiggle.Enabled)
            Log.LogInfo($"  Sway (planned pieces, candidates included): kept {RoadSpatialGrid.SwayKept} ({RoadSpatialGrid.SwayKeptHeld} with it held round the stretches at fault), dropped as refused {RoadSpatialGrid.SwayRefused}, dropped for more earth {RoadSpatialGrid.SwayMoreEarth}; full sway to {RoadWiggle.Flat:P0} ground grade, {RoadWiggle.SlopeFloor:P0} of it at the {RoadGrade.Configured:P0} cap, meander fades out at {RoadPathfinder.MeanderFlat:P0}");
            Log.LogInfo($"  Sway on slopes (kept plans, over {RoadWiggle.Flat:P0} grade and more than 1 m off the plain line): {RoadSpatialGrid.SlopeSwayPlans} plan(s), metres by grade band 12-20%: {RoadSpatialGrid.SlopeSwayMillimetres[0] / 1000}, 20-28%: {RoadSpatialGrid.SlopeSwayMillimetres[1] / 1000}, over 28%: {RoadSpatialGrid.SlopeSwayMillimetres[2] / 1000}");
        if (RoadPitches.Enabled)
            Log.LogInfo($"  Climbs that lost rests (too steep even with pitches the whole way): {RoadPitches.ClimbsThatLostEases}");
        if (RoadPitches.JunctionLanding > 0f)
            Log.LogInfo($"  Place ends on a level landing (held at the place's ground, RoadPitches.ApplyJunctionLanding): {RoadSpatialGrid.PlaceLandingsStored}");
        if (SearchStats.Enabled)
            foreach (string line in SearchStats.Describe())
                Log.LogInfo("  " + line);
        if (RoadsLog) LogStairs();
        Log.LogInfo("  Staircases refused across their road: " + RoadStairRuns.AcrossCounts());
        LogSqueeze();
        LogGenerationStats(m_roadsGeneratedCount, elapsed);

        StairStructures.SortCanonical();
        SortRecordsCanonical();
        RoadSpatialGrid.FinalizeRoadNetwork();
        
        NetworkCutoff.UseNewNetwork();
        RoadNetworkPersistence.NetworkGenerated();
        m_roadsGenerated = true;
        m_networkDirty = true;
        work.Published = true;
        ReleasePathfinders();
        
        RoadNetworkPersistence.EnsureMetadataInstance();
    }

    /// <summary>Drop the generation's pathfinders and the terrain grid they shared
    /// (<see cref="SharedTerrain"/>): kept, a world's grid held tens of MB for the
    /// rest of the session, read by nothing until the next generation.</summary>
    private static void ReleasePathfinders()
    {
        m_generationOptions = null;
        m_generationWorld = null;
        m_pathfinder = null;
        m_threadPathfinders?.Dispose();
        m_threadPathfinders = null;
        SharedTerrain.Clear();
    }

    internal static void ReleaseGenerationResources()
    {
        LocationLevelling.Unseal();
        PlacedSites.ForgetNotPlaced();
        ReleasePathfinders();
        m_generationWorld = null;
        m_generationOptions = null;
    }

    private static RoadNetworkOptions? m_generationOptions;
    private static RoadNetworkOptions GenerationOptions=>m_generationOptions ?? NetworkOptions;
    private static WorldGenerator? m_generationWorld;
    // As WorldGenerator.instance was read before: callers that can run without a world check it.
    private static WorldGenerator GenerationWorld => m_generationWorld ?? WorldGenerator.instance!;

    /// <summary>
    /// Merge the config-defined custom locations into the registered set.
    /// Every generation entry point calls this first, so a location named in
    /// the config counts as road-eligible whichever entry point runs first.
    /// </summary>
    private static void RegisterConfiguredLocations()
    {
        ConfiguredLocationNames.Clear();
        foreach (string name in ProceduralRoadsPlugin.GetConfigLocationNames())
            if (!string.IsNullOrWhiteSpace(name)) ConfiguredLocationNames.Add(name.Trim());
    }

    #region Core Road Generation Primitive

    /// <summary>How many roads were dropped because no profile inside the
    /// grade cap joins their two ends. Counted so the cost of the cap is a
    /// number in the generation summary rather than a matter of opinion.</summary>
    private static int m_roadsRefusedForGrade;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> m_refusalReasons = new();
    /// <summary>Destinations chosen, and destinations that ended up with a
    /// road. These are per DESTINATION; the refusal counters below are per
    /// ATTEMPT, and a destination is offered several candidates before it is
    /// given up on, so the two do not add up to each other.</summary>
    private static int m_destinationsSelected;
    private static int m_destinationsConnected;
    /// <summary>Destinations given no road as they were not placed before their roads (<see cref="PlacedSites.NotPlaced"/>):
    /// counted among those chosen and not reached.</summary>
    private static int m_destinationsNotPlaced;

    /// <summary>How many roads found no route at all. With a grade cap on,
    /// most of these are a destination the cap put out of reach rather than
    /// one across water, so the two are counted apart.</summary>
    private static int m_roadsWithNoRoute;

    /// <summary>PROCEDURALROADS_ROADS_LOG: say where each road was refused for crossing or running beside
    /// another, and list every destination left unconnected with how its last road attempt failed.
    /// Off by default: off, nothing is noted or logged.</summary>
    internal static readonly bool RoadsLog = DebugSwitches.Flag("ROADS_LOG", false);
    /// <summary>Per place (rounded centre), with <see cref="RoadsLog"/>: how its last road attempt failed,
    /// and whether a road was built to it.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), string> m_lastFailure = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), bool> m_builtTo = new();
    private static (int, int) PlaceKey(Vector2 c) => (Mathf.RoundToInt(c.x), Mathf.RoundToInt(c.y));
    private static void NoteFailure(Vector2 a, Vector2 b, string why, Vector2? at = null)
    {
        t_capture?.Refused(why, at);
        if (!RoadsLog) return;
        m_lastFailure[PlaceKey(a)] = why;
        m_lastFailure[PlaceKey(b)] = why;
    }
    private static void NoteBuilt(Vector2 a, Vector2 b)
    {
        if (!RoadsLog) return;
        m_builtTo[PlaceKey(a)] = true;
        m_builtTo[PlaceKey(b)] = true;
    }

    /// <summary>Per place (rounded centre), with <see cref="RoadsLog"/>: the staircase that failed where a road to it gave
    /// up its stairs.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), string> m_stairsFailed = new();
    /// <summary>With <see cref="RoadsLog"/>: why roads were searched as a plain road, and how often.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> m_stairFallbacks = new();
    /// <summary>With <see cref="RoadsLog"/>: road ends at a place, by what became of an approach staircase there.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> m_siteApproaches = new();
    /// <summary>With <see cref="RoadsLog"/>: the generation's staircase tallies for the summary.</summary>
    private static int m_stairRuns, m_stairRoadsUnlaid, m_stretchRetries, m_stretchRetriesKept, m_tightTurnStairs, m_tightTurnStairsBuilt, m_notLevelEnds;
    /// <summary>With <see cref="RoadsLog"/>: road ends kept close to their place, within <see cref="ReachRise"/> of its level
    /// but more than <see cref="ArrivalLevel"/> off (<see cref="EndNotLevel"/>), or short of a placed place's landing
    /// (<see cref="PlanEntry"/>), with no staircase onto it.</summary>
    private static int m_closeEnds;
    /// <summary>Staircases laid by this thread, with <see cref="RoadsLog"/>: a retry reads it to know whether it kept the road's stairs.</summary>
    [System.ThreadStatic] private static int t_stairRunsLaidHere;

    private static void ClearStairNotes()
    {
        m_stairsFailed.Clear();
        m_stairFallbacks.Clear();
        m_siteApproaches.Clear();
        m_stairRuns = m_stairRoadsUnlaid = m_stretchRetries = m_stretchRetriesKept = m_tightTurnStairs = m_tightTurnStairsBuilt = 0;
        m_notLevelEnds = m_closeEnds = 0;
    }

    /// <summary>The summary's staircase lines, with <see cref="RoadsLog"/>.</summary>
    internal static void LogStairs()
    {
        static string ByCount(IEnumerable<KeyValuePair<string, int>> tally) =>
            string.Join("; ", tally.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key}"));
        Log.LogInfo($"  Staircases: {m_stairRuns} laid on roads; {m_stretchRetries} search(es) again with a staircase's stretch closed, " +
                    $"{m_stretchRetriesKept} of them built with stairs; " +
                    $"{m_tightTurnStairs} search(es) again with stairs open at switchback turns too close to fit, {m_tightTurnStairsBuilt} of them built with stairs; " +
                    $"{m_notLevelEnds} road(s) refused for an end not level with its place ({ReachRise:F1} m) and no approach staircase onto it; " +
                    $"{m_closeEnds} end(s) kept close to their place, within {ReachRise:F1} m of its level or short of its landing, with no approach staircase; " +
                    $"{RoadStairRuns.FeetStartedLower} started further back down their road; {RoadStairRuns.KitsRelaid} stretch(es) laid again in the kit their staircase stood in; " +
                    $"{RoadStairRuns.RidgesToFeet} start(s) onto a place's landing refused over a ridge or trench to the foot; " +
                    $"{RoadStairRuns.ApproachesNotFacingIn} approach(es) laid not facing into their place ({RoadStairRuns.ApproachFacingReruns} searched again not facing in); " +
                    $"{RoadStairRuns.AimsBuried} aim(s) whose first stair facing in was buried, {RoadStairRuns.LaidBesideTheirAim} approach(es) laid beside such an aim along the lip; " +
                    $"{RoadStairRuns.Crests} top(s) crested onto a lip; {m_humpsLevelled} crest pair(s) planned as road across their hump (built or not); {m_stairRoadsUnlaid} road(s) searched as a plain road after closing stretches to stairs" +
                    (m_stairFallbacks.IsEmpty ? "" : ": " + ByCount(m_stairFallbacks)));
        if (!m_siteApproaches.IsEmpty)
            Log.LogInfo("  Site approach staircases, by road end at a place: " + ByCount(m_siteApproaches));
        if (!RoadStairRuns.Rejections.IsEmpty)
            Log.LogInfo("  Staircase layouts, by what became of them: " + ByCount(RoadStairRuns.Rejections));
        if (!RoadStairRuns.LayFailures.IsEmpty)
            Log.LogInfo("  Roads that gave up their stairs, by the staircase that stopped them: " + ByCount(RoadStairRuns.LayFailures));
    }

    /// <summary>
    /// The height a road has to arrive at for a location: the ground under the
    /// location's centre, which is what the game reads when it places the
    /// location and levels its footprint.
    ///
    /// Null for an endpoint that is not a location - an island's edge point
    /// has no footprint and radius zero - and null when that ground is under
    /// water, where the centre is not somewhere a road can end and
    /// RoadEndpoint has already moved the path elsewhere. Both fall back to
    /// the natural terrain under the road's own last point, as before.
    /// </summary>
    /// <summary>What the terrain memo did over a whole generation: how often a
    /// fact was already held, how often the world had to be asked, and how
    /// often a slot was evicted by a different position. Evictions are the
    /// figure that says the memo is too small for the way it is being asked,
    /// which a hit rate on its own does not.</summary>
    private static void LogTerrainMemoCounters()
    {
        if (m_threadPathfinders != null)
            foreach (var finder in m_threadPathfinders.Values) finder.FoldTerrainMemoCounters();
        long hits = RoadTerrainSamples.TotalHits, misses = RoadTerrainSamples.TotalMisses;
        long asked = hits + misses;
        if (asked == 0) return;
        Log.LogDebug($"Terrain memo: {asked} fact(s) asked, {hits} held ({100.0 * hits / asked:F1}%), " +
                     $"{misses} read from the world ({RoadTerrainSamples.TotalTerrainCalls} generator call(s)), " +
                     $"{RoadTerrainSamples.TotalReplacements} slot(s) evicted.");
        if (!SearchStats.Enabled) return;
        // Info, beside the search times it is part of: in game the generator's share
        // of a search (its river cache takes a write lock on every miss) can only be
        // read here; offline it is a dump lookup and says nothing.
        double seconds = RoadTerrainSamples.TotalTerrainTicks / (double)System.Diagnostics.Stopwatch.Frequency;
        long calls = RoadTerrainSamples.TotalTerrainCalls;
        Log.LogInfo(FormattableString.Invariant($"  World generator behind the search memo: {seconds:F0} s summed over threads, ") +
                    FormattableString.Invariant($"{calls} call(s), {(calls > 0 ? seconds * 1e6 / calls : 0):F1} us each"));
    }

    internal static float? LocationGround(Vector2 endPoint, Vector2 center, float radius)
    {
        if (radius <= 0f || GenerationWorld == null)
            return null;
        IReadOnlyList<LevelOp>? ops = LocationLevelling.OpsAt(center);
        if (ops == null)
            return null;
        float centreGround = LocationLevelling.CentreHeight(center, GenerationWorld);
        float? levelled = LocationLevelling.GroundAt(endPoint, center, centreGround, ops);
        if (!levelled.HasValue)
            return null;
        return RoadWater.BuiltDry(levelled.Value) ? levelled : null;
    }

    internal static List<Vector2> ImproveSiteApproach(List<Vector2> path, Vector2 centre,
        float radius, float width, bool atStart)
    {
        var world = GenerationWorld;
        if (world == null || radius <= 0f) return path;
        var ops = LocationLevelling.OpsAt(centre);
        // Copy the terrain facts once: scoring must not reload the template
        // for every vertex. Unknown location shaping retains the old approach.
        if (!LocationLevelling.Levels(ops)) return path;
        float centreHeight = LocationLevelling.CentreHeight(centre, world);
        float? platform = LocationLevelling.ApproachHeight(centreHeight, ops);
        if (platform is float level && !RoadWater.BuiltDry(level)) platform = null;
        Log.LogDebug($"Site approach {centre}: keep-out edge {radius:F1}m, approach {(platform.HasValue ? platform.Value.ToString("F2") : "unknown")}m");
        float Ground(Vector2 p) => LocationLevelling.GroundAt(p, centre, centreHeight, ops)
            ?? BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world);
        float? Target(Vector2 p) => platform.HasValue && Mathf.Abs(Ground(p) - platform.Value) <= ArrivalLevel
            ? Ground(p) : LocationLevelling.GroundAt(p, centre, centreHeight, ops);
        return RoadSiteApproach.Improve(path, centre, radius, width, world, atStart, Target, Ground, platform);
    }

    internal static float? ApproachGround(Vector2 point, Vector2 centre, float radius)
    {
        float? local = LocationGround(point, centre, radius);
        if (local.HasValue || radius <= 0f || GenerationWorld == null) return local;
        var world = GenerationWorld;
        float centreHeight = LocationLevelling.CentreHeight(centre, world);
        float? platform = LocationLevelling.ApproachHeight(centreHeight, LocationLevelling.OpsAt(centre));
        float natural = BiomeBlendedHeight.GetBlendedHeight(point.x, point.y, world);
        // Never make an arbitrary high embankment merely to hit a platform.
        // The approach search must first find naturally nearby elevation.
        if (platform.HasValue)
            return RoadWater.BuiltDry(platform.Value) && Mathf.Abs(natural - platform.Value) <= ArrivalLevel ? natural : null;
        // A place with no flat (a cabin that only smooths round its centre, not placed first or placed with no landing to go
        // onto: PlacedSites.Prepare) is arrived at its ground at its edge on the road's side (ArrivalHeight). An end whose
        // ground is level with that, as the reached test has it (ReachRise), is held there and so ends on the level landing
        // every place's end gets (RoadSpatialGrid.PlaceEnds), at a road's start as at its end; from there it is carried on
        // level to the edge (CarryOnToEdges). No higher bank or deeper cut than the reached test allows. The edge is the
        // place's own (its footprint's arrival radius): the radius asked here is the road's ring, outside it.
        float arrival = RoadSiteProtection.ArrivalRadiusAt(centre, 0f);
        if (arrival > 0f && !PlacedSites.TryGetReady(centre, out _) && ArrivalHeight(centre, arrival, point) is float edge
            && RoadWater.BuiltDry(edge) && Mathf.Abs(natural - edge) <= ReachRise)
            return edge;
        return null;
    }

    /// <summary>
    /// Core primitive: Generates a single road between two points.
    /// Handles pathfinding, radius trimming, and adding to the spatial grid.
    /// </summary>
    /// <param name="startCenter">Center of the start location</param>
    /// <param name="startRadius">Exterior radius of start location (road starts at edge)</param>
    /// <param name="endCenter">Center of the end location</param>
    /// <param name="endRadius">Exterior radius of end location (road ends at edge)</param>
    /// <param name="width">Width of the road</param>
    /// <param name="label">Optional label for logging</param>
    /// <returns>True if road was successfully generated</returns>
    public static bool GenerateRoad(
        Vector2 startCenter, float startRadius,
        Vector2 endCenter, float endRadius,
        float width, string? label = null) =>
        GenerateRoad(startCenter, startRadius, endCenter, endRadius, width, label, null);

    /// <summary>How many stretches whose staircase could not be laid a road closes to stairs, one at a time,
    /// searched again after each with stairs open everywhere else, before it is searched once as a plain road, so one
    /// stretch that will not lay does not cost the whole road its other staircases.</summary>
    public const int StairStretchRetries = 3;
    /// <summary>Cells round a failed stretch also closed, so the search does not lay the same
    /// staircase one column over.</summary>
    internal const int StairStretchReachCells = 1;
    /// <summary>Cells round switchback turns too close to fit their curves opened to stairs when the
    /// road is searched again (<see cref="RoadPathfinder.OpenToStairs"/>).</summary>
    internal const int TightTurnStairReachCells = 3;
    [System.ThreadStatic] private static bool t_inRoad;

    internal static bool GenerateRoad(
        Vector2 startCenter, float startRadius,
        Vector2 endCenter, float endRadius,
        float width, string? label, List<Vector2>? networkHints)
    {
        // The cells a retry closes to stairs belong to this road alone.
        if (t_inRoad || Finder == null)
            return GenerateRoadCore(startCenter, startRadius, endCenter, endRadius, width, label, networkHints, default);
        t_inRoad = true;
        t_bandedEnds?.Clear();
        t_refusedAt = null;
        t_reEnd = null;
        try
        {
            bool built = GenerateRoadByDoors(startCenter, startRadius, endCenter, endRadius, width, label, networkHints);
            // It arrived at a place the way another way into it does (ReEndOnAWayIn): searched once more,
            // ending on that approach's road instead of at the place, as a junction (radius 0, as a place's join ends).
            if (!built && t_reEnd is var (place, onItsRoad))
            {
                t_reEnd = null;
                bool atStart = (startCenter - place).sqrMagnitude <= (endCenter - place).sqrMagnitude;
                Finder?.ClearNoStairs();
                t_doors = null;
                built = atStart
                    ? GenerateRoadByDoors(onItsRoad, 0f, endCenter, endRadius, width, label, networkHints)
                    : GenerateRoadByDoors(startCenter, startRadius, onItsRoad, 0f, width, label, networkHints);
                if (built) System.Threading.Interlocked.Increment(ref m_reEndedOnApproach);
                else System.Threading.Interlocked.Increment(ref m_reEndRefused);
                if (label != null) Log.LogDebug(FormattableString.Invariant(
                    $"Road {label}: ended on the place's approach's road at ({onItsRoad.x:F1}, {onItsRoad.y:F1}) instead: {(built ? "built" : "refused (" + RoadSpatialGrid.LastRefusal + ")")}"));
                return built;
            }
            if (built || t_bandedEnds == null || t_bandedEnds.Count == 0) return built;
            // A road ending at a custom location's MWL stand-off (OffLevelStandOff) refused in its planning (the earthwork
            // cap, the grade cap) on the stretch the grade's stand-off gives up, or where the refusal does not say:
            // searched once more ending at the grade's stand-off. Refused anywhere else, the search again is the same refusal.
            if (!RefusedAtABandedEnd(t_refusedAt, t_bandedEnds))
            {
                if (label != null) Log.LogDebug($"Road {label}: refused ending at the MWL stand-off ({RoadSpatialGrid.LastRefusal}) away from that end; not searched again");
                return false;
            }
            if (label != null) Log.LogDebug($"Road {label}: refused ending at the MWL stand-off ({RoadSpatialGrid.LastRefusal}); searched again at the grade's stand-off");
            t_noStandOffBand = true;
            Finder?.ClearNoStairs();
            t_doors = null;
            return GenerateRoadByDoors(startCenter, startRadius, endCenter, endRadius, width, label, networkHints);
        }
        finally { Finder?.ClearNoStairs(); t_inRoad = false; t_doors = null; t_noStandOffBand = false; t_bandedEnds?.Clear(); t_refusedAt = null; t_reEnd = null; }
    }

    /// <summary>How far out from a door its road's search ends, the road then running straight in to the door
    /// (<see cref="RoadDoor.LeadIn"/>): a search cell.</summary>
    public const float LeadInLength = RoadPathfinder.CellSize;

    /// <summary>
    /// Where a road to the place at <paramref name="centre"/> (exterior <paramref name="radius"/>) stops, from its
    /// centre: the one owner for the trim (<see cref="TrimPathToRadii"/>) and for whether the network reaches the
    /// place (<see cref="Reached"/>). Enough room for the road's whole terrain blend outside the
    /// ground the place shapes (<see cref="RoadSiteProtection.ArrivalRadiusAt"/>): over ground the place only
    /// paints the road is carried on, stored into the site so it levels and paints there only along
    /// itself (<see cref="RoadSpatialGrid.Commit"/>). A place placed first keeps its whole footprint: its road is
    /// carried on onto its landing by its own owner (<see cref="PlanEntry"/>).
    /// </summary>
    internal static float KeepOutEdge(Vector2 centre, float radius, float width) =>
        KeepOutEdge(centre, radius, width, PlacedSites.TryGetReady(centre, out _));

    /// <summary><see cref="KeepOutEdge(Vector2, float, float)"/> for a place known to be placed first or not (the
    /// preparation that is making it ready asks before it is).</summary>
    internal static float KeepOutEdge(Vector2 centre, float radius, float width, bool placed) =>
        (placed ? RoadSiteProtection.RadiusAt(centre, radius) : RoadSiteProtection.ArrivalRadiusAt(centre, radius))
        + RoadSiteProtection.Clearance(width);

    /// <summary>A road's searches again, each against its own budget: closing a stretch to stairs (StairStretchRetries), the
    /// one tight-turn search with stairs opened, and banning a crossing (CrossingRetries). One spent never spends another.</summary>
    internal readonly struct RoadRetries
    {
        public readonly int StairStretches;
        public readonly bool TightTurn;
        public readonly int CrossingBans;
        /// <summary>The route the search found before the last crossing was banned: a search that finds it again took no
        /// notice of the ban, and is not searched again.</summary>
        public readonly IReadOnlyList<Vector2>? BeforeBan;
        private RoadRetries(int stairStretches, bool tightTurn, int crossingBans, IReadOnlyList<Vector2>? beforeBan)
        { StairStretches = stairStretches; TightTurn = tightTurn; CrossingBans = crossingBans; BeforeBan = beforeBan; }
        public RoadRetries NextStairStretch() => new(StairStretches + 1, TightTurn, CrossingBans, BeforeBan);
        public RoadRetries WithTightTurn() => new(StairStretches, true, CrossingBans, BeforeBan);
        public RoadRetries NextCrossingBan(IReadOnlyList<Vector2> searched) => new(StairStretches, TightTurn, CrossingBans + 1, searched);
        /// <summary>Whether <paramref name="searched"/> is the route found before the last ban.</summary>
        public bool SameAsBeforeBan(IReadOnlyList<Vector2> searched)
        {
            if (BeforeBan == null || BeforeBan.Count != searched.Count) return false;
            for (int i = 0; i < searched.Count; i++)
                if ((BeforeBan[i] - searched[i]).sqrMagnitude > 0.01f) return false;
            return true;
        }
    }

    private static bool GenerateRoadCore(
        Vector2 startCenter, float startRadius,
        Vector2 endCenter, float endRadius,
        float width, string? label, List<Vector2>? networkHints, RoadRetries retries)
    {
        if (Finder == null)
        {
            Log.LogWarning("GenerateRoad called without active pathfinder");
            return false;
        }
        // From here, anything this road asks for that preparation did not
        // provide marks the road rather than only the log.
        LocationLevelling.BeginRoad();
        float askedStartRadius = startRadius, askedEndRadius = endRadius;
        // Where this road is refused, where its refusal says (RefusedAtABandedEnd): left null by a refusal that does not.
        t_refusedAt = null;
        var refusedAt = new List<Vector2>();
        // A staircase that could not be laid: its stretch is closed to stairs and the road searched again with them open
        // everywhere else, each failing stretch in turn, up to StairStretchRetries; then once as a plain road, else refused.
        // The road keeps every staircase that lays (no collateral).
        bool WithoutThatStaircase(RoadPathfinder finder, List<Vector2> laidOn)
        {
            if (retries.StairStretches >= StairStretchRetries)
            {
                // Last resort: one plain road search before the road is refused.
                // A routing fallback, not a stairs switch: there is no setting for stairs, and the plain road keeps every grade
                // cap. Refused outright after the closings, the roads lost 35 destinations over RoadSeedA-E
                // against 12 with this search.
                if (RoadsLog)
                {
                    System.Threading.Interlocked.Increment(ref m_stairRoadsUnlaid);
                    m_stairFallbacks.AddOrUpdate("a staircase could not be laid", 1, (_, n) => n + 1);
                    m_stairsFailed[PlaceKey(startCenter)] = m_stairsFailed[PlaceKey(endCenter)] = "a staircase could not be laid";
                    if (label != null) Log.LogDebug($"Road {label}: a staircase could not be laid on {StairStretchRetries} stretch(es) closed in turn; searched once as a plain road");
                }
                bool stairs = finder.Stairs;
                finder.Stairs = false;
                try { return GenerateRoadCore(startCenter, askedStartRadius, endCenter, askedEndRadius, width, label, networkHints, retries); }
                finally { finder.Stairs = stairs; }
            }
            var (from, to) = RoadStairRuns.LastFailedStretch;
            from = Mathf.Clamp(from, 0, laidOn.Count - 1);
            to = Mathf.Clamp(to, from, laidOn.Count - 1);
            var stretch = laidOn.GetRange(from, to - from + 1);
            // The search's own steps on that stretch too: the path may have been moved onto a road there.
            foreach (var (a, b) in finder.LastStairSteps)
                if (stretch.Exists(p => (p - a).sqrMagnitude < 144f || (p - b).sqrMagnitude < 144f)) { stretch.Add(a); stretch.Add(b); }
            finder.CloseToStairs(stretch, StairStretchReachCells);
            if (!RoadsLog) return GenerateRoadCore(startCenter, askedStartRadius, endCenter, askedEndRadius, width, label, networkHints, retries.NextStairStretch());
            System.Threading.Interlocked.Increment(ref m_stretchRetries);
            if (label != null)
                Log.LogDebug($"Road {label}: staircase on path {from}..{to} could not be laid ({RoadStairRuns.Said(RoadStairRuns.LastRefusal)}); searched again with {finder.NoStairCells} cell(s) closed to stairs");
            int before = t_stairRunsLaidHere;
            bool built = GenerateRoadCore(startCenter, askedStartRadius, endCenter, askedEndRadius, width, label, networkHints, retries.NextStairStretch());
            if (built && t_stairRunsLaidHere > before) System.Threading.Interlocked.Increment(ref m_stretchRetriesKept);
            return built;
        }

        // A road to a place is searched to its doors (DoorsOf); one with none, to the ground at its centre.
        PlacedSite? startSite = startRadius > 0f && PlacedSites.TryGetReady(startCenter, out PlacedSite s0) ? s0 : null;
        PlacedSite? endSite = endRadius > 0f && PlacedSites.TryGetReady(endCenter, out PlacedSite s1) ? s1 : null;
        Vector2? startAim = null, endAim = null;
        Vector2 pathStart = RoadEndpoint.FindGround(GenerationWorld, startCenter, startRadius);
        Vector2 pathEnd = RoadEndpoint.FindGround(GenerationWorld, endCenter, endRadius);
        IReadOnlyList<RoadDoor> startDoors = DoorsLeft(startCenter, startRadius, width);
        IReadOnlyList<RoadDoor> endDoors = DoorsLeft(endCenter, endRadius, width);
        bool startDoor = startDoors.Count > 0, endDoor = endDoors.Count > 0;
        List<Vector2>? path;
        if (startDoor || endDoor)
        {
            // To every door at once (DoorsOf): the road ends at the cheapest it gets to, its ring point then joined head on
            // from its lead-in. A place placed first is exempt from nothing, as its lead-in is not; a place not placed
            // first lets the search through its own footprint, as its centre's ground does.
            if (startDoor) pathStart = startDoors[0].LeadIn;
            if (endDoor) pathEnd = endDoors[0].LeadIn;
            path = Finder.FindPathBetween(SearchEnds(startDoors, pathStart), SearchEnds(endDoors, pathEnd),
                startDoor && startSite == null ? startCenter : pathStart, endDoor && endSite == null ? endCenter : pathEnd);
            if (RoadsLog)
            {
                System.Threading.Interlocked.Increment(ref m_doorRoads);
                if (path != null && path.Count >= 2) System.Threading.Interlocked.Increment(ref m_doorRoadsFound);
            }
            if (startDoor) { pathStart = startDoors[Finder.LastStartIndex].LeadIn; startAim = startDoors[Finder.LastStartIndex].At; }
            if (endDoor) { pathEnd = endDoors[Finder.LastEndIndex].LeadIn; endAim = endDoors[Finder.LastEndIndex].At; }
            NoteDoorsTaken(path != null && path.Count >= 2, startDoor ? startAim : null, endDoor ? endAim : null);
        }
        else
        {
            if (RoadsLog && (startRadius > 0f || endRadius > 0f)) System.Threading.Interlocked.Increment(ref m_doorFallbacks);
            path = Finder.FindPath(pathStart, pathEnd);
        }
        // In to a placed place's ring point head on from its lead-in, so the road onto its landing
        // carries straight on: arriving along the ring, its staircase ran beside the road's own end
        // and was refused "on its road" (14 of the 36 placed places on one test world).
        if (path != null && path.Count >= 2)
        {
            if (startAim is Vector2 s2) path.Insert(0, s2);
            if (endAim is Vector2 e2) path.Add(e2);
        }
        path = SnapToNetwork(path, width);

        if (path == null || path.Count < 2)
        {
            if (label != null)
                Log.LogDebug($"Could not find path: {label}");
            NoteFailure(startCenter, endCenter, "no route");
            System.Threading.Interlocked.Increment(ref m_roadsWithNoRoute);
            return false;
        }

        // Whether other network already reaches each end's place, by the one reached test on the radius as asked; the
        // network does not change while this road is planned and re-routed.
        bool startReached = EndReached(startCenter, startRadius), endReached = EndReached(endCenter, endRadius);
        // On the bearing the trim cuts the path on, where it crosses the ring (not the search's own end, the centre).
        float startOut = startRadius > 0f ? OffLevelStandOff(startCenter, startRadius, RingCrossing(path, startCenter, KeepOutEdge(startCenter, startRadius, width), true)) : 0f;
        float endOut = endRadius > 0f ? OffLevelStandOff(endCenter, endRadius, RingCrossing(path, endCenter, KeepOutEdge(endCenter, endRadius, width), false)) : 0f;
        if (startRadius > 0f) startRadius = KeepOutEdge(startCenter, startRadius, width) + startOut;
        if (endRadius > 0f) endRadius = KeepOutEdge(endCenter, endRadius, width) + endOut;
        // The one planning pipeline (PlanFromPath), as the earthwork re-route plans too.
        var finder = Finder!;
        var searched = new List<Vector2>(path);
        // Read after the plan (refusedAt): never one an earlier plan on this thread left.
        RoadSpatialGrid.RefusedAt = null;
        RoadPlan planned = PlanFromPath(finder, path, startCenter, startRadius, endCenter, endRadius,
            startSite != null, endSite != null, startReached, endReached, width);
        switch (planned.Stop)
        {
            case PlanStop.NothingBetween:
                // The two sites are close enough that what is left between their
                // protected footprints is shorter than a road. A failed attempt
                // like any other -- the planner goes on to its next candidate, as
                // it always did. Whether the destination ends up reached is not
                // decided here: it is measured once the island is built.
                if (label != null)
                    Log.LogDebug($"Nothing to lay between the two sites once their footprints were cleared: {label}");
                NoteFailure(startCenter, endCenter, "nothing between the two footprints");
                System.Threading.Interlocked.Increment(ref m_roadsRefusedForGrade);
                return false;
            case PlanStop.Staircase:
                return WithoutThatStaircase(finder, planned.Path);
            case PlanStop.Preparation:
                if (label != null)
                    Log.LogDebug($"Road refused: preparation was incomplete for it, destination left unconnected: {label}");
                NoteFailure(startCenter, endCenter, "refused: preparation incomplete");
                System.Threading.Interlocked.Increment(ref m_roadsRefusedForGrade);
                return false;
        }
        path = planned.Path;
        if (planned.Plans == null && planned.Unmet == null && RoadSpatialGrid.RefusedAt is Vector2 planRefused) refusedAt.Add(planRefused);
        List<RoadCrossing> crossings = planned.Crossings;
        List<RoadStairSpan> stairs = planned.Stairs;
        var plans = planned.Plans;
        bool startAtPlace = planned.StartAtPlace, endAtPlace = planned.EndAtPlace;
        // Switchback turns too close to fit their curves, even tightened: the search climbs that
        // stretch on stairs instead, once, and carries on as road beyond it. On the five offline
        // worlds it was tried 11 times and built none with stairs (one road was built without them):
        // their staircases there could not be laid. One test world refused 22 roads for these turns.
        if (plans == null && !retries.TightTurn
            && RoadSpatialGrid.LastRefusal == SwitchbackRefusal.TurnsTooClose && RoadSwitchbacks.LastRefusalAt is Vector2 tight)
        {
            finder.OpenToStairs(new[] { tight }, TightTurnStairReachCells);
            if (!RoadsLog) return GenerateRoadCore(startCenter, askedStartRadius, endCenter, askedEndRadius, width, label, networkHints, retries.WithTightTurn());
            System.Threading.Interlocked.Increment(ref m_tightTurnStairs);
            int before = t_stairRunsLaidHere;
            bool built = GenerateRoadCore(startCenter, askedStartRadius, endCenter, askedEndRadius, width, label, networkHints, retries.WithTightTurn());
            if (built && t_stairRunsLaidHere > before) System.Threading.Interlocked.Increment(ref m_tightTurnStairsBuilt);
            if (label != null) Log.LogDebug($"Road {label}: switchback turns at {tight} too close to fit; searched again with {finder.OpenStairCells} cell(s) open to stairs: {(built ? "built" : "not built")}");
            return built;
        }
        if (plans != null && EarthworkCapped)
        {
            // A road the game cannot build as planned -- a cut or fill past the
            // terrain limit is clamped, so the surface tilts across the road
            // and jumps between the clamp and the plan -- is
            // searched again with the cells round the offending points priced
            // up, up to EarthworkRetries times; a plan with none past the
            // limit is kept, and if none is found the road is refused.
            var where = new List<Vector2>();
            int past = PastTerrainLimit(plans, EarthworkLimit, where) + InvalidCrossings(crossings, where) + HeldRidges(plans, where);
            // Stretches held to a road already laid far off this road's own profile are re-routed
            // round too, but never refuse a road: the fill or cut they build is a ridge beside the
            // network (at -2486,1716 a stretch held +8.8 m was a large earthwork ridge).
            int pulled = HeldOffProfile(plans, where);
            Log.LogDebug($"Road {label}: {past} planned point(s) past the {EarthworkCap:F0} m earthwork cap in {plans.Count} piece(s), {pulled} stretch(es) held over {HoldPullLimit:F0} m off its profile");
            finder.ClearEarthworkPenalty();
            for (int attempt = 0; (past > 0 || pulled > 0) && attempt < EarthworkRetries; attempt++)
            {
                foreach (var w in where) finder.PenalizeAround(w, EarthworkPenaltyRadius);
                var retry = Replan(finder, pathStart, pathEnd, startCenter, startRadius, endCenter, endRadius, width, startAim, endAim, startReached, endReached);
                if (retry == null)
                { Log.LogDebug($"Road {label}: earthwork re-route {attempt + 1} did not route or build ({RoadSpatialGrid.LastRefusal ?? "no route"}), {finder.EarthworkPenaltyCells} cell(s) priced up"); continue; }
                var whereRetry = new List<Vector2>();
                int pastRetry = PastTerrainLimit(retry.Value.plans, EarthworkLimit, whereRetry) + InvalidCrossings(retry.Value.crossings, whereRetry) + HeldRidges(retry.Value.plans, whereRetry);
                int pulledRetry = HeldOffProfile(retry.Value.plans, whereRetry);
                Log.LogDebug($"Road {label}: earthwork re-route {attempt + 1}: {pastRetry} point(s) past the cap (was {past}), {retry.Value.crossings.Count} crossing(s), {finder.EarthworkPenaltyCells} cell(s) priced up, {finder.PenalizedWaypoints(retry.Value.path)} of {retry.Value.path.Count} waypoints in them; first offending point {(whereRetry.Count > 0 ? whereRetry[0].ToString() : "-")}, crossing {(retry.Value.crossings.Count > 0 ? retry.Value.crossings[0].FromBank + "->" + retry.Value.crossings[0].ToBank : "-")}");
                // Only a re-route the network takes as it meets it is offered: one that then crosses another road or runs
                // beside one was refused for it after it had replaced a first plan that would have been built.
                if ((pastRetry < past || (pastRetry == past && pulledRetry < pulled)) && MeetingRefusal(retry.Value.plans, out Vector2 meets) is string meeting)
                {
                    Log.LogDebug(FormattableString.Invariant($"Road {label}: earthwork re-route {attempt + 1} not taken: it {meeting} at ({meets.x:F1}, {meets.y:F1})"));
                    // Priced up there too: the next attempt goes round where this one met the network, not the same way again.
                    where.Add(meets);
                    continue;
                }
                if (pastRetry < past || (pastRetry == past && pulledRetry < pulled))
                {
                    Log.LogDebug($"Road {label}: re-routed round deep earthwork, {past} -> {pastRetry} points past {EarthworkCap:F0} m, {pulled} -> {pulledRetry} held off its profile");
                    System.Threading.Interlocked.Increment(ref pastRetry < past ? ref m_earthworkReroutes : ref m_pullReroutes);
                    pulled = pulledRetry;
                    (path, crossings, plans, past, stairs) = (retry.Value.path, retry.Value.crossings, retry.Value.plans, pastRetry, retry.Value.stairs);
                    (startAtPlace, endAtPlace) = (retry.Value.startAtPlace, retry.Value.endAtPlace);
                    where = whereRetry;
                }
                else where = whereRetry.Count > 0 ? whereRetry : where;
            }
            finder.ClearEarthworkPenalty();
            if (past > 0)
            {
                // A road the game cannot build as planned is not a road: it is
                // refused, even if that leaves a destination unserved.
                bool water = crossings.Exists(c => c.Invalid && c.Shallow);
                bool throughDeck = !water && crossings.Exists(c => c.Invalid && !c.Shallow);
                bool ridge = !water && !throughDeck && HeldRidges(plans, null) > 0 && PastTerrainLimit(plans, EarthworkLimit, new List<Vector2>()) == 0;
                if (!water && !throughDeck) System.Threading.Interlocked.Increment(ref m_earthworkUnavoided);
                plans = null;
                refusedAt.AddRange(where);
                RoadSpatialGrid.LastRefusal = water
                    ? "shallow water too long or deep to cross"
                    : throughDeck ? "bridge deck through the ground between its banks"
                    : ridge ? $"a junction ridge: held to a road on more than {JunctionFillLimit:F0} m of fill"
                    : EarthworkRefusal;
            }
            else if (!crossings.Exists(c => c.Invalid))
                (path, crossings, plans, stairs, startAtPlace, endAtPlace) = OffRidges(finder, (path, crossings, plans, stairs, startAtPlace, endAtPlace), pulled,
                    pathStart, pathEnd, startCenter, startRadius, endCenter, endRadius, width, startAim, endAim, startReached, endReached);
        }
        // A crossing with an end no way of meeting reaches (StructureEnds.MeetEnds) is not built: the road is searched again
        // round it. A valley viaduct the built road does not allow (BuiltRefusal) is not built either, nor one a road
        // refused outright had on it: the road is searched again without it.
        // Each is banned as the search used it (RoadCrossing.Searched): its banks, and every move it lies on. A search that
        // finds the same route again is not searched again.
        bool bansSpent = retries.CrossingBans >= CrossingRetries || retries.SameAsBeforeBan(searched);
        if (!bansSpent)
        {
            string builtWhy = "";
            RoadCrossing? unmet = plans != null ? BuiltRefusal(plans, crossings, out _, out builtWhy) : null;
            var banned = planned.Unmet != null ? new List<RoadCrossing> { planned.Unmet }
                : unmet != null ? (unmet.ValleyJump ? new List<RoadCrossing> { unmet } : new List<RoadCrossing>())
                : plans == null ? (LastStubBetween is RoadCrossing stubbed && crossings.Contains(stubbed) ? new List<RoadCrossing> { stubbed }
                    : crossings.FindAll(c => c.ValleyJump && !c.Shared))
                : new List<RoadCrossing>();
            if (banned.Count > 0)
            {
                foreach (RoadCrossing c in banned)
                {
                    finder.BanJump(c.FromBank, c.ToBank);
                    if (c.Searched != null) foreach (var (from, to) in c.Searched) finder.BanJump(from, to);
                }
                System.Threading.Interlocked.Increment(ref planned.Unmet != null ? ref m_crossingsUnmetSearchedRound : ref m_viaductsRefusedAtEnds);
                if (label != null && RoadsLog)
                    Log.LogInfo(FormattableString.Invariant($"[BRIDGES] road {label}: {(planned.Unmet != null ? "an end not met of " + planned.Unmet : unmet != null ? builtWhy + " at " + unmet : "refused with a viaduct")} ({banned[0].FromBank} -> {banned[0].ToBank}, {banned[0].Searched?.Count ?? 0} move(s)); searched again without it"));
                return GenerateRoadCore(startCenter, askedStartRadius, endCenter, askedEndRadius, width, label, networkHints, retries.NextCrossingBan(searched));
            }
        }
        if (planned.Unmet != null) System.Threading.Interlocked.Increment(ref m_bridgeEndsUnmet);
        // Past the searches again, a valley viaduct the built road does not allow is not built: the road is refused with it
        // (three were built where the retries had run out and the check was skipped).
        if (plans != null && bansSpent
            && BuiltRefusal(plans, crossings, out _, out string lastWhy) is RoadCrossing lastUnmet && lastUnmet.ValleyJump && !lastUnmet.Shared)
        {
            plans = null;
            refusedAt.Add(lastUnmet.FromBank); refusedAt.Add(lastUnmet.ToBank);
            RoadSpatialGrid.LastRefusal = "a viaduct the built road does not allow (" + lastWhy + ")";
        }
        if (plans != null && DanglingBridge(plans, crossings, path[0], path[path.Count - 1], out var dangling))
        {
            // A bridge must have road at both ends; one was found stopping in
            // 3 m of water.
            Log.LogWarning($"Bridge with no road beyond it on {label}: {dangling.FromBank} -> {dangling.ToBank}; road refused");
            plans = null;
            refusedAt.Add(dangling.FromBank); refusedAt.Add(dangling.ToBank);
            RoadSpatialGrid.LastRefusal = "bridge with no road at one end";
        }
        // A place placed before its roads: the road carried on onto its landing, on its placed ground, as
        // a road or else a staircase, as part of this road. Where neither gets there the road is refused
        // and the planner tries its other candidates: there is no approach left to lay later. Outside the
        // Mountains (ApproachStairsAt) there is no staircase but at a boss's entrance, and a road that gets no further
        // keeps its end at the ring.
        var entries = new List<(PlacedSite site, RoadSpatialGrid.PlannedPath? road, RoadStairSpan? stairs)>();
        if (plans != null)
            foreach (var (site, atStart, atPlace) in new[] { (startSite, true, startAtPlace), (endSite, false, endAtPlace) })
            {
                if (site == null || !atPlace) continue;
                // The ground the road's other end was planned to meet (PlanFromPath), held there where the way onto the landing
                // starts lower and the road is planned again (EntryFromLower); where a way onto its own place's landing was laid
                // already, the height it was laid from.
                float? farGround = entries.Count > 0 ? plans[0].Heights[0]
                    : atStart ? (endAtPlace ? ApproachGround(path[path.Count - 1], endCenter, endRadius) : null)
                    : startAtPlace ? ApproachGround(path[0], startCenter, startRadius) : null;
                bool entered = PlanEntry(site, atStart, path, plans, stairs, crossings, width, out var entryRoad, out var entryStairs, out string noEntry, farGround);
                // No staircase laid, and at least one aim refused as another way into the place (PlaceWaysIn): the place has
                // its way in already, so the road is searched again ending on that way's road (GenerateRoad, ReEndOnAWayIn),
                // as an arrival that way is (below).
                if (!entered && noEntry.Contains(RoadStairRuns.BesideAnApproach) && WayInRefused(site.Centre, atStart, plans) is Vector2 onItsRoad)
                {
                    if (RoadsLog) Log.LogDebug(FormattableString.Invariant(
                        $"Road {label ?? "(unlabelled)"}: its staircase onto the landing of the place at {site.Centre} is a way in the place has; searched again to that way's road at ({onItsRoad.x:F1}, {onItsRoad.y:F1})"));
                    t_reEnd ??= (site.Centre, onItsRoad);
                    plans = null;
                    refusedAt.Add(path[atStart ? 0 : path.Count - 1]);
                    RoadSpatialGrid.LastRefusal = BesideItsApproachRefusal;
                    break;
                }
                if (entered)
                {
                    entries.Add((site, entryRoad, entryStairs));
                    if (RoadsLog) m_siteApproaches.AddOrUpdate(entryRoad != null ? "placed first: road onto its landing" : "placed first: staircase onto its landing", 1, (_, n) => n + 1);
                    continue;
                }
                // A place with a door is reached only in front of it (a crypt): a road kept at its ring would not reach it.
                // Nor one entered only from its front, kept at its ring behind it (PlacedSiteShape.OnEntranceSide).
                if (RingEnterable(site)
                    && site.Shape.OnEntranceSide(path[atStart ? 0 : path.Count - 1]))
                {
                    if (RoadsLog)
                    {
                        System.Threading.Interlocked.Increment(ref m_closeEnds);
                        m_siteApproaches.AddOrUpdate("placed first: kept at its ring outside the mountains", 1, (_, n) => n + 1);
                        Log.LogDebug($"Road {label ?? "(again without stairs)"}: no road onto the landing of the place at {site.Centre}, kept at its ring: {noEntry}");
                    }
                    continue;
                }
                if (RoadsLog)
                {
                    m_siteApproaches.AddOrUpdate("placed first: refused", 1, (_, n) => n + 1);
                    Log.LogDebug($"Road {label ?? "(again without stairs)"}: no way onto the landing of the place at {site.Centre}: {noEntry}");
                }
                plans = null;
                refusedAt.Add(path[atStart ? 0 : path.Count - 1]);
                RoadSpatialGrid.LastRefusal = "no way onto a placed place's landing";
                break;
            }
        // No road under a viaduct's deck laid before it (the built-result check, StructureEnds.UnderTheDeck).
        if (plans != null && UnderALaidDeck(plans, out Vector2 underAt) is string underDeck)
        {
            plans = null;
            refusedAt.Add(underAt);
            RoadSpatialGrid.LastRefusal = underDeck;
            if (RoadsLog) Log.LogDebug(FormattableString.Invariant($"Road {label ?? "(unlabelled)"}: {underDeck} at ({underAt.x:F1}, {underAt.y:F1}); refused"));
        }
        // No road crosses another, nor runs beside one at its height, nor on a staircase's flight but onto a landing level
        // with the row it reaches (MeetingRefusal).
        RoadStairRuns.FlightLanding? landing = null;
        if (plans != null && MeetingRefusal(plans, out Vector2 meetsAt, out landing, landOnFlight: t_capture == null) is string meetingRefused)
        {
            plans = null;
            refusedAt.Add(meetsAt);
            RoadSpatialGrid.LastRefusal = meetingRefused;
            if (RoadsLog) Log.LogDebug(FormattableString.Invariant($"Road {label ?? "(unlabelled)"}: {meetingRefused} at ({meetsAt.x:F1}, {meetsAt.y:F1}); refused"));
        }
        if (plans != null && landing != null) LandOnTheFlight(plans, landing);
        // A road whose end at its place does not arrive level with it, and from which no approach staircase onto it can be
        // laid, does not reach it (EndNotLevel): refused, even at the cost of a POI; the planner tries its other candidates.
        var approaches = plans == null ? new List<RoadStairSpan>()
            : SiteApproaches(path, plans, stairs, startCenter, startSite != null ? 0f : askedStartRadius,
                endCenter, endSite != null ? 0f : askedEndRadius, startAtPlace, endAtPlace);
        // A placed place's end is judged where no way onto its landing was laid (kept at its ring outside the Mountains).
        bool Entered(PlacedSite? site) => site != null && entries.Exists(e => e.site == site);
        // A place has one way in up a face (PlaceWaysIn, the one owner): an end arriving at its place the way another way into
        // it does, by staircase or as road, is not built so; the road is searched again ending on that way's road
        // (GenerateRoad, ReEndOnAWayIn).
        if (plans != null)
            foreach (var (atStart, centre, asked, atPlace, site) in new[]
                { (true, startCenter, askedStartRadius, startAtPlace, startSite), (false, endCenter, askedEndRadius, endAtPlace, endSite) })
            {
                if (!atPlace || asked <= 0f) continue;
                Vector2 place = site?.Centre ?? centre;
                if (ReEndOnAWayIn(place, ArrivalAt(place, atStart, plans, entries, approaches, site)) is not Vector2 onItsRoad) continue;
                if (RoadsLog) Log.LogDebug(FormattableString.Invariant(
                    $"Road {label ?? "(unlabelled)"}: arrives at the place at {place} the way another way into it does; searched again to that way's road at ({onItsRoad.x:F1}, {onItsRoad.y:F1})"));
                t_reEnd ??= (place, onItsRoad);
                plans = null;
                refusedAt.Add(path[atStart ? 0 : path.Count - 1]);
                RoadSpatialGrid.LastRefusal = BesideItsApproachRefusal;
                break;
            }
        if (plans != null && EndNotLevel(path, plans, stairs, approaches, startCenter, askedStartRadius, endCenter, askedEndRadius,
                startAtPlace && !Entered(startSite), endAtPlace && !Entered(endSite)) is (string refusal, string notLevel))
        {
            if (RoadsLog)
            {
                if (refusal == NotLevelRefusal) System.Threading.Interlocked.Increment(ref m_notLevelEnds);
                if (label != null) Log.LogDebug($"Road {label}: {notLevel}");
            }
            plans = null;
            // At one of its ends (which, the refusal does not say).
            refusedAt.Add(path[0]); refusedAt.Add(path[path.Count - 1]);
            RoadSpatialGrid.LastRefusal = refusal;
        }
        // Arriving level with a place that has no flat nor landing, carried on from its ring to the place's edge (CarryOnToEdge).
        var carried = plans == null ? new List<RoadSpatialGrid.PlannedPath>()
            : CarryOnToEdges(path, plans, stairs, approaches, width, startCenter, askedStartRadius, endCenter, askedEndRadius,
                startAtPlace && startSite == null, endAtPlace && endSite == null);
        // A staircase whose foot is not at its road's level where it ends (RoadStairRuns.FootLeftOver): refused, never laid
        // with the staircase hanging over its road.
        if (plans != null)
        {
            var laying = stairs.FindAll(st => !st.Existing);
            laying.AddRange(approaches);
            foreach (var entry in entries) if (entry.stairs != null) laying.Add(entry.stairs);
            if (RoadStairRuns.FootLeftOver(laying, plans, out Vector2 footAt, (x, z) => RoadStairRuns.GroundAt(x, z, GenerationWorld)) is string footRefused)
            {
                plans = null;
                refusedAt.Add(footAt);
                RoadSpatialGrid.LastRefusal = footRefused;
                if (RoadsLog) Log.LogInfo(FormattableString.Invariant($"[STAIRS] road {label ?? "(unlabelled)"}: {footRefused} at ({footAt.x:F1}, {footAt.y:F1}); refused"));
            }
        }
        // Refused with stairs on it, it is refused: stairs are always on, and a road is never searched again without them
        // (there is no flag for them, nor code to work
        // around them). The planner tries its other candidates.
        if (plans == null)
        {
            t_refusedAt = refusedAt.Count > 0 ? refusedAt : null;
            string why = RoadSpatialGrid.LastRefusal ?? "other (crossing or footprint)";
            m_refusalReasons.AddOrUpdate(why, 1, (_, n) => n + 1);
            if (label != null)
                Log.LogDebug($"Road could not be built ({why}), destination left unconnected: {label}");
            NoteFailure(startCenter, endCenter, "refused: " + why, refusedAt.Count > 0 ? refusedAt[0] : null);
            System.Threading.Interlocked.Increment(ref m_roadsRefusedForGrade);
            return false;
        }
        // Its ends arriving at their places as road (no staircase there): the ways into those places it adds (PlaceWaysIn),
        // recorded once it is stored, here or (planned outside generation) where its plan is committed (ManualRoads.Commit).
        var waysIn = new List<(Vector2 place, PlaceWaysIn.Way way)>();
        foreach (var (atStart, centre, asked, atPlace, site) in new[]
            { (true, startCenter, askedStartRadius, startAtPlace, startSite), (false, endCenter, askedEndRadius, endAtPlace, endSite) })
        {
            if (!atPlace || asked <= 0f || !PlaceWaysIn.On) continue;
            Vector2 place = site?.Centre ?? centre;
            if (StairsAt(atStart, entries, approaches, site) == null && ArrivalAt(place, atStart, plans, entries, approaches, site) is PlaceWaysIn.Way way)
                waysIn.Add((place, way));
        }
        // Planned outside generation (PlanningWith): handed over as it would be stored, nothing stored. A road onto a
        // staircase's flight is not planned so (MeetingRefusal above): it changes a staircase already laid.
        if (t_capture != null)
        {
            t_capture.Road = new PlannedRoad
            {
                StartCenter = startCenter, EndCenter = endCenter, Path = path, Plans = plans, Crossings = crossings, Stairs = stairs,
                Approaches = approaches, Entries = entries, Carried = carried, WaysIn = waysIn,
            };
            return true;
        }

        // One road where two would ride along each other (RoadRide, the one owner): where this road rides the network --
        // within a road's width of a road laid before it, or of a structure that road crosses or climbs on, heading its way
        // -- it stores nothing: not its points, nor a crossing of its own there (the road that built it carries it). Asked
        // of the network as it stands before this road is stored.
        var plannedCrossings = crossings;
        var crossingLines = RoadRide.Around(crossings.SelectMany(c => new[] { c.FromBank, c.Center, c.ToBank }).ToList(), RoadRide.Reach);
        int crossingsBefore = crossings.Count;
        crossings = crossings.FindAll(c => !RoadRide.RidesCrossing(crossingLines, c));
        if (crossings.Count < crossingsBefore) System.Threading.Interlocked.Add(ref RoadRide.m_crossingsRidden, crossingsBefore - crossings.Count);
        // Its own decks too: a structure replaces the road over its gap, so no piece of it runs on under its own
        // span or bridge (a neighbouring ford's piece in a pool chain did, under a deck turned onto a buildable heading).
        // A bridge whose ends the owner brings the road to (MeetsTheRoad) is left to StructureEnds' own trimming.
        // The other roads on a structure end's landing ground are raised to it with the road (StructureEnds.MeetEnds).
        CrossingGround crossingGround = GroundOf(crossings, width);
        crossingGround.RaiseOthers();
        foreach (RoadCrossing c in crossings)
            if (!c.Shared)
            {
                System.Threading.Interlocked.Add(ref m_endPads, (c.FromPad ? 1 : 0) + (c.ToPad ? 1 : 0));
                System.Threading.Interlocked.Add(ref m_endShared, (c.FromShared ? 1 : 0) + (c.ToShared ? 1 : 0));
                System.Threading.Interlocked.Add(ref m_endStands, (c.FromStands ? 1 : 0) + (c.ToStands ? 1 : 0));
                foreach (var end in new[] { c.FromEnd, c.ToEnd })
                    if (end is StructureEnds.End { IsStructure: true } e)
                    {
                        System.Threading.Interlocked.Add(ref m_endSteps, e.Steps);
                        System.Threading.Interlocked.Increment(ref e.Steps == 0 ? ref m_endFlat : ref e.Steps == 1 ? ref m_endOneStep : ref m_endMoreSteps);
                    }
                // An end met by earthwork (StructureEnds.MeetEnds): its landing filled over ground under the road's floor.
                int earthworks = (c.FromEarthwork ? 1 : 0) + (c.ToEarthwork ? 1 : 0);
                if (earthworks == 0) continue;
                System.Threading.Interlocked.Add(ref m_endEarthworks, earthworks);
                if (RoadsLog) Log.LogInfo(FormattableString.Invariant($"[BRIDGES] road {label}: {earthworks} end(s) of the bridge {c.FromBank} -> {c.ToBank} met by earthwork, {c.FromEnd?.Steps.ToString() ?? "-"}/{c.ToEnd?.Steps.ToString() ?? "-"} steps laid"));
            }
        if (landing != null && RoadStairRuns.LayLanding(landing) && RoadsLog)
            Log.LogDebug(FormattableString.Invariant($"[STAIRS] road {label ?? "(unlabelled)"}: landing at {landing.Height:F2} onto the staircase {landing.Run.From} -> {landing.Run.To}, {landing.Removed.Count} walked piece(s) under it taken away, its foot now ({landing.Foot.x:F1}, {landing.Foot.y:F1})"));
        var own = OwnPieces(plans, crossings, out float ridden, out var decks);
        if (ridden > 0f) System.Threading.Interlocked.Add(ref RoadRide.m_riddenCentimetres, (long)(ridden * 100f));
        // Where its ends meet the network as it stands, the junctions' fill (RoadJunctionFillet), stored with it.
        // Its own staircases are laid after the fills are read: given to them as the road's own structures.
        var ownStairs = stairs.FindAll(st => !st.Existing);
        ownStairs.AddRange(approaches);
        foreach (var entry in entries) if (entry.stairs != null) ownStairs.Add(entry.stairs);
        var fills = RoadJunctionFillet.For(own, GenerationWorld, plannedCrossings, ownStairs);
        // Where its staircases start at its ends, a small level pad under their start (RoadEndRing.PadUnder, the ring's).
        var pads = RoadStairRuns.StartPads(own, ownStairs, GenerationWorld, label);
        RoadSpatialGrid.CommitRoad(own, committed: plan => PieceCommitted?.Invoke(plan, label), decks: decks, ownChecked: true);
        foreach (var fill in fills) RoadSpatialGrid.CommitLevelled(fill.Points, fill.Heights, fill.Width);
        foreach (var pad in pads) RoadSpatialGrid.CommitLevelled(pad.Points, pad.Heights, pad.Width, unpainted: true);
        if (fills.Count > 0) System.Threading.Interlocked.Add(ref m_junctionFills, fills.Count);
        if (IslandActivity != null)
        {
            // A crossing snapped to a site an earlier road built is that bridge, not a new one.
            int bridges = 0;
            foreach (var c in crossings) if (c.Kind == CrossingKind.Bridge && !c.Shared && !c.Invalid) bridges++;
            IslandActivity.BuiltBridges(bridges);
        }
        foreach (var (entrySite, entryRoad, entryStairs) in entries)
        {
            if (entryRoad != null)
            {
                RoadSpatialGrid.CommitRoad(RoadRide.Own(new List<RoadSpatialGrid.PlannedPath> { entryRoad }, out _), intoSite: true,
                    committed: plan => PieceCommitted?.Invoke(plan, label), ownChecked: true);
                PlacedSites.MarkLanded(entrySite.Centre);
                if (RoadsLog && label != null) Log.LogDebug($"Road {label}: carried on onto the landing, {entryRoad.TotalLength:F0} m of road into the place");
            }
            if (entryStairs != null)
            {
                JoinNetwork(entryStairs.FromPoint, entryStairs.ToPoint, entryStairs.Plan, entryStairs.Kit, atPlace: true);
                RoadStairRuns.TallyRing(entryStairs.Plan);
                PlacedSites.MarkLanded(entrySite.Centre);
                if (RoadsLog && label != null) Log.LogDebug($"Road {label}: carried on onto the landing, {entryStairs.Kit.Name} staircase {entryStairs.Plan.Rise:F0} m {(entryStairs.Plan.Reversed ? "down" : "up")}");
            }
        }
        // Its ends arriving at their places as road (no staircase there): ways into those places from now on (PlaceWaysIn).
        foreach (var (place, way) in waysIn) PlaceWaysIn.Record(place, way);
        foreach (RoadSpatialGrid.PlannedPath edge in carried)
        {
            RoadSpatialGrid.CommitRoad(RoadRide.Own(new List<RoadSpatialGrid.PlannedPath> { edge }, out _), intoSite: true,
                committed: plan => PieceCommitted?.Invoke(plan, label), ownChecked: true);
            System.Threading.Interlocked.Increment(ref m_carriedToEdge);
            if (RoadsLog && label != null) Log.LogDebug($"Road {label}: carried on to its place's edge, {edge.TotalLength:F1} m into the place");
        }
        foreach (RoadStairSpan approach in approaches)
        {
            // An approach crosses its place's footprint by design: it is a staircase at its place, as an entry is.
            JoinNetwork(approach.FromPoint, approach.ToPoint, approach.Plan, approach.Kit, atPlace: true, expectedPlace: approach.ExpectedPlace);
            RoadStairRuns.TallyRing(approach.Plan);
            if (RoadsLog && label != null) Log.LogDebug($"Road {label}: site approach staircase, {approach.Kit.Name} {approach.Plan.Rise:F0} m {(approach.Plan.Reversed ? "down" : "up")}");
        }
        foreach (RoadStairSpan stair in stairs)
        {
            StairStructures.MarkLaid(path.GetRange(stair.FromIndex, stair.ToIndex - stair.FromIndex + 1));
            if (stair.Existing) continue;
            RoadStairRuns.TallyRing(stair, plans);
            JoinNetwork(path[stair.FromIndex], path[stair.ToIndex], stair.Plan, stair.Kit, atPlace: false, stair.FromPoint, stair.ToPoint);
        }
        crossingGround.CommitGround();
        if (RoadCrossingDetector.ToWater)
        {
            foreach (var c in crossings)
            {
                if (c.FromLand is Vector2 f) { System.Threading.Interlocked.Increment(ref m_offLandEnds); Log.LogDebug($"Bridge end off land on {label}: {f} -> {c.FromBank}"); }
                if (c.ToLand is Vector2 t) { System.Threading.Interlocked.Increment(ref m_offLandEnds); Log.LogDebug($"Bridge end off land on {label}: {t} -> {c.ToBank}"); }
            }
        }

        if (networkHints != null)
            for (int i = 0; i < path.Count; i += 8)
            {
                // Joins aim at these hints, so none may lie on a crossing: a
                // later road joined an earlier bridge's waypoint in mid-river
                // and built a second bridge out to it, ending mid-river.
                bool onCrossing = false;
                foreach (var crossing in plannedCrossings)
                    if (i >= crossing.FromIndex && i <= crossing.ToIndex) { onCrossing = true; break; }
                // Nor on a staircase: there is no road there to join.
                foreach (var stair in stairs)
                    if (i >= stair.FromIndex && i <= stair.ToIndex) { onCrossing = true; break; }
                if (!onCrossing) { networkHints.Add(path[i]); IslandNetworkChanged(); }
            }
        m_islandRoads++;
        int roadNumber;
        // Each crossing's kind decided once, as it is stored (BridgeStructures.DecideKind): a viaduct, or a deck whose kit
        // is chosen when it is first built. Decided before the lock every island records its roads under: a viaduct's
        // test samples its piers and ends.
        foreach (RoadCrossing c in crossings) BridgeStructures.DecideKind(c, GenerationWorld);
        foreach (RoadCrossing c in crossings) c.Island = CrossIslandReads.Tag;
        lock (m_recordGate)
        {
            m_roadCrossings.AddRange(crossings);
            roadNumber = ++m_roadsGeneratedCount;
        }
        IslandNetworkChanged();
        KeepClearOfViaducts(crossings, label);
        foreach (var c in crossings)
            if (c.Shallow && c.ShallowNote != null) Log.LogDebug("  " + c.ShallowNote);

        if (path.Count > 0)
        {
            string pinLabel = label ?? $"Road {roadNumber}";
            lock (m_recordGate) m_roadStartPoints.Add((path[0], pinLabel));
        }
        if (crossings.Count > 0 && label != null)
            Log.LogDebug($"Road {label}: {crossings.Count} river crossing(s)");
        if (RoadsLog && stairs.Count > 0 && label != null)
            Log.LogDebug($"Road {label}: {stairs.Count} staircase(s), {string.Join(", ", stairs.ConvertAll(st => $"{st.Kit.Name} {st.Plan.Rise:F0} m up"))}");

        if (label != null)
            Log.LogDebug($"Generated road: {label} ({path.Count} waypoints)");
        NoteBuilt(startCenter, endCenter);

        return true;
    }

    /// <summary>
    /// Adds the path to the spatial grid as road: the land before a crossing
    /// runs on to its near bank and the land after it starts at its far
    /// bank, and the crossing itself, bank to bank along the road, is
    /// painted in its style: waded at the ground's own height, or raised to
    /// the bank clearance so the leveled road stands above the water.
    /// </summary>
    /// <summary>Metres of levelled ground carried in under each end of a bridge deck,
    /// over land only.</summary>
    public const float DeckGroundReach = 3f;
    /// <summary>Ground under a deck end is held at least this far below the deck.</summary>
    public const float DeckUnderClearance = 0.4f;

    /// <summary>
    /// What a road's crossings change besides the road itself, the one list a generated road and a manual road both store
    /// with it: the other roads on an end's landing raised to it (StructureEnds.MeetEnds), and the ground under each
    /// bridge's deck ends (<see cref="CrossingGround.UnderDecks"/>).
    /// </summary>
    internal sealed class CrossingGround
    {
        /// <summary>The other roads' points on the landings of the road's own crossings' ends, each to its new height.</summary>
        public readonly List<(RoadSpatialGrid.RoadPoint at, float to)> Raised = new();
        /// <summary>The ground cut to each step under a viaduct's end stairs, decided from the road's arrival (StructureEnds),
        /// <see cref="CutWidth"/> wide.</summary>
        public readonly List<Vector3> Cuts = new();
        public static float CutWidth => BridgeLayout.EndStairsWidth;
        /// <summary>
        /// Road carried in under each end of every bridge, up to
        /// <see cref="DeckGroundReach"/> and only while the ground is above the
        /// waterline, levelled to the ground or <see cref="DeckUnderClearance"/>
        /// under the deck, whichever is lower, and never painted: the paint stops
        /// at the end's stairs' foot (StructureEnds.Unpainted). It cuts what the
        /// height estimate missed: measured at one bridge, the estimate put the
        /// ground 1 m in at 31.2 m while the game's terrain stood 31.9-32.4 m
        /// under a 31.8 m deck, poking through it. Null where there is none.
        /// </summary>
        public RoadSpatialGrid.PlannedPath? UnderDecks;

        /// <summary>The other roads raised (generation: before the road is stored, so its junctions meet them as raised).</summary>
        public void RaiseOthers() => RoadSpatialGrid.SetRoadHeights(Raised);

        /// <summary>The cuts and the ground under the decks stored (generation: once the road and its staircases are).</summary>
        public void CommitGround()
        {
            if (Cuts.Count > 0) RoadSpatialGrid.CommitCuts(Cuts, CutWidth);
            if (UnderDecks != null) RoadSpatialGrid.CommitLevelled(UnderDecks.Points, UnderDecks.Heights, UnderDecks.Width, unpainted: true);
        }
    }

    /// <summary><see cref="CrossingGround"/> of <paramref name="crossings"/>, a road's of <paramref name="width"/>: the raises of
    /// those it builds (not of one shared with a road laid before, which that road raised), the ground under every bridge's
    /// deck ends where crossings are built over water (<see cref="RoadCrossingDetector.ToWater"/>).</summary>
    internal static CrossingGround GroundOf(List<RoadCrossing> crossings, float width)
    {
        var ground = new CrossingGround();
        foreach (RoadCrossing c in crossings)
            if (!c.Shared)
                foreach (var raised in new[] { c.FromRaised, c.ToRaised })
                    if (raised != null) ground.Raised.AddRange(raised);
        if (!RoadCrossingDetector.ToWater) return ground;
        var world = GenerationWorld;
        var temp = new List<Vector2>(); var hs = new List<float>();
        foreach (var c in crossings)
        {
            // A bridge's alone: a ford's span keeps its deck's line clear of road (OwnPieces, RoadRide.DeckLines), its
            // footbridge low over shallow water where no ground stands under the deck to cut.
            if (c.Kind != CrossingKind.Bridge) continue;
            float span = Vector2.Distance(c.FromBank, c.ToBank);
            if (span < 2f) continue;
            float ceiling = BridgeStructures.DeckHeight(c, world) - DeckUnderClearance;
            // Under a viaduct's end stairs the ground is cut to each step.
            if (IsViaduct(c))
            {
                var (near, far) = DvergrViaduct.Ends(c, world);
                foreach (StructureEnds.End end in new[] { near, far })
                    for (int k = 0; k < end.Steps; k++)
                    {
                        Vector2 at = StructureEnds.StepCentre(end.Edge, end.Up, k);
                        ground.Cuts.Add(new Vector3(at.x, StructureEnds.StepBase(end.Height, k), at.y));
                    }
            }
            // No road painted in under a viaduct's deck at the ground's height: its road runs on under the end module at the
            // meeting height (StructureEnds.JoinOverlap), and road at the ground under a deck end is wrong.
            if (IsViaduct(c)) continue;
            float reach = Mathf.Min(DeckGroundReach, span * 0.5f);
            // Nor at an end with no stairs laid (met level, or landed on its last row, StructureEnds.MeetEnds): its road runs on
            // under the deck's end at the deck's height, as a viaduct's does.
            var (nearEnd, farEnd) = BridgeStructures.Ends(c, world);
            foreach (var (bank, other, end) in new[] { (c.FromBank, c.ToBank, nearEnd), (c.ToBank, c.FromBank, farEnd) })
                for (float d = 0.5f; d <= reach && end.Steps != 0; d += 0.5f)
                {
                    var p = bank + (other - bank) * (d / span);
                    float g = BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world);
                    if (g < RoadCrossingDetector.Waterline) break;
                    temp.Add(p); hs.Add(Mathf.Min(g, ceiling));
                }
        }
        if (temp.Count > 0)
            ground.UnderDecks = new RoadSpatialGrid.PlannedPath(temp, hs, temp.ConvertAll(_ => default(RoadPointDebugInfo)), width, 0f, false)
                { Unpainted = temp.ConvertAll(_ => true) };
        return ground;
    }

    /// <summary>
    /// The ground the planner sees, with each strip a deck
    /// end was walked in from (<see cref="RoadCrossing.FromLand"/>) lowered to
    /// a straight ramp between the old and new banks. Cut only: ground below
    /// the ramp is left as it is. The earthwork cap still measures against the
    /// real ground, so a cut past the terrain limit is refused as any other.
    /// Null when no crossing moved an end.
    /// </summary>
    private static System.Func<Vector2, float>? StripGround(List<RoadCrossing> crossings, float width)
    {
        var world = GenerationWorld;
        float H(Vector2 q) => BiomeBlendedHeight.GetBlendedHeight(q.x, q.y, world);
        var strips = new List<(Vector2 a, Vector2 b, float ha, float hb)>();
        foreach (var c in crossings)
        {
            if (c.FromLand is Vector2 f) strips.Add((f, c.FromBank, H(f), H(c.FromBank)));
            if (c.ToLand is Vector2 t) strips.Add((c.ToBank, t, H(c.ToBank), H(t)));
        }
        if (strips.Count == 0) return null;
        float reach = RoadSiteProtection.Clearance(width);
        return q =>
        {
            float g = H(q);
            foreach (var (a, b, ha, hb) in strips)
            {
                Vector2 ab = b - a;
                float len2 = ab.sqrMagnitude;
                if (len2 < 1e-4f) continue;
                float t = Vector2.Dot(q - a, ab) / len2;
                if (t < 0f || t > 1f || Vector2.Distance(q, a + ab * t) > reach) continue;
                g = Mathf.Min(g, Mathf.Lerp(ha, hb, t));
            }
            return g;
        };
    }

    /// <summary>
    /// After the search, a waypoint within this many metres of a road already
    /// built is moved onto it, so a road that runs alongside another merges
    /// with it and branches off where it leaves, instead of following the
    /// same path beside it. The end waypoints stay. Settable for tests; 0 is off.
    /// </summary>
    internal static float RoadSnap = 6f;

    /// <summary>
    /// A road's own stub is dropped where it merges into a road already built
    /// within <see cref="StubLength"/> of its end: two roads leaving one place
    /// the same way made two branches a few metres apart that joined just
    /// after, and the road now starts at the junction. A road heading another
    /// way never merges that soon and keeps its own branch, so a place can
    /// still have several where the network heads different directions. Works
    /// on snapped waypoints, so only with <see cref="RoadSnap"/>.
    /// </summary>
    internal static List<Vector2>? DropStubs(List<Vector2>? path, bool startReached = true, bool endReached = true)
    {
        if (path == null || RoadSnap <= 0f || path.Count < 3 || !RoadSpatialGrid.IsInitialized) return path;
        // Only a place another road already reaches loses this road's stub:
        // cutting the stub of a place nothing else reaches would cut its only
        // access.
        int first = startReached ? MergeWithin(path, +1) : -1, last = endReached ? MergeWithin(path, -1) : -1;
        // Running alongside the road already serving the place instead, never touching it: the two
        // share one trunk, this one branching off where the trunk stands at its ground's height.
        Vector2? firstOn = null, lastOn = null;
        if (first < 0 && startReached && TrunkBranch(path, +1, out Vector2 a) is int fa && fa >= 0) { first = fa; firstOn = a; }
        if (last < 0 && endReached && TrunkBranch(path, -1, out Vector2 b) is int lb && lb >= 0) { last = lb; lastOn = b; }
        if (first < 0 && last < 0) return path;
        int from = first < 0 ? 0 : first, to = last < 0 ? path.Count - 1 : last;
        if (to - from < 1) return path;
        var kept = path.GetRange(from, to - from + 1);
        if (firstOn is Vector2 fo && Vector2.Distance(fo, kept[0]) > 0.5f) kept.Insert(0, fo);
        if (lastOn is Vector2 lo && Vector2.Distance(lo, kept[kept.Count - 1]) > 0.5f) kept.Add(lo);
        return kept;
    }

    public const float StubLength = 80f;

    /// <summary>The first waypoint from one end that lies on a road already
    /// built, within <see cref="StubLength"/> along the path; -1 if none, or
    /// if the end itself is on a road.</summary>
    private static int MergeWithin(List<Vector2> path, int step)
    {
        int start = step > 0 ? 0 : path.Count - 1;
        if (RoadSpatialGrid.TryGetRoadWithin(path[start], RoadBeside.OnRoad, out _)) return -1;
        float along = 0f;
        for (int i = start + step; i >= 0 && i < path.Count; i += step)
        {
            along += Vector2.Distance(path[i - step], path[i]);
            if (along > StubLength) return -1;
            if (RoadSpatialGrid.TryGetRoadWithin(path[i], RoadBeside.OnRoad, out _)) return i;
        }
        return -1;
    }

    /// <summary>How far in from a place a road may branch off a trunk serving it.</summary>
    public const float TrunkReach = 120f;
    /// <summary>The trunk must come alongside within this far of the place.</summary>
    public const float TrunkNear = 30f;

    /// <summary>
    /// Where a road, near its <paramref name="step"/> end at a place another road already serves,
    /// runs within that road's terrain reach (the band RoadSpatialGrid.HoldToNetwork holds to its
    /// height) without touching it: the waypoint furthest from the place, along that stretch, where
    /// the other road stands within <see cref="RoadPathfinder.JunctionStep"/> of the ground, and the
    /// road point there (<paramref name="onTrunk"/>); else -1. The road then ends on the trunk there,
    /// at grade, instead of running beside it to the place at another height: two roads leaving one
    /// place side by side were the largest junction earthworks (so they share a trunk).
    /// </summary>
    private static int TrunkBranch(List<Vector2> path, int step, out Vector2 onTrunk)
    {
        onTrunk = default;
        var world = GenerationWorld;
        if (RoadPathfinder.JunctionStep <= 0f || world == null) return -1;
        float band = RoadProfile.Footprint(RoadWidth);
        int start = step > 0 ? 0 : path.Count - 1, best = -1;
        bool alongside = false;
        float along = 0f;
        for (int i = start; i >= 0 && i < path.Count; i += step)
        {
            if (i != start) along += Vector2.Distance(path[i - step], path[i]);
            if (along > TrunkReach) break;
            if (RoadSpatialGrid.TryGetRoadHeightWithin(path[i], band, out float h))
            {
                if (!alongside && along > TrunkNear) break;
                alongside = true;
                if (i != start && Mathf.Abs(h - RoadSpatialGrid.JunctionGround(path[i], world)) <= RoadPathfinder.JunctionStep
                    && RoadSpatialGrid.TryGetRoadWithin(path[i], band, out Vector2 q)) { best = i; onTrunk = q; }
            }
            else if (alongside) break;
        }
        return best;
    }

    /// <summary>A road's ends pulled back to road ground: the trim at a site's
    /// edge can fall in a river, and the road then ended in the water.</summary>
    internal static List<Vector2>? TrimWetEnds(List<Vector2>? path)
    {
        if (path == null || path.Count < 2) return path;
        var world = GenerationWorld;
        int from = 0, to = path.Count - 1;
        while (from < to && !RoadCrossingDetector.IsRoadGround(path[from], world)) from++;
        while (to > from && !RoadCrossingDetector.IsRoadGround(path[to], world)) to--;
        if (from == 0 && to == path.Count - 1) return path;
        return to - from >= 1 ? path.GetRange(from, to - from + 1) : null;
    }

    /// <summary>A crossing with a deck (BridgeStructures.HasDeck) whose bank has no planned road piece starting or ending at it.</summary>
    private static bool DanglingBridge(List<RoadSpatialGrid.PlannedPath> plans, List<RoadCrossing> crossings,
        Vector2 roadStart, Vector2 roadEnd, out RoadCrossing dangling)
    {
        // A bank at the road's own start or end is the place itself (a coast
        // landing starts on its bridge): only a bank in between needs road.
        bool AtRoadEnd(Vector2 bank) => Vector2.Distance(bank, roadStart) < 8f || Vector2.Distance(bank, roadEnd) < 8f;
        dangling = null!;
        foreach (var c in crossings)
        {
            if (!BridgeStructures.HasDeck(c)) continue;
            bool from = false, to = false;
            // The road ends at a bank, or where the one owner meets the bridge's end (StructureEnds: its stairs' foot).
            var (near, far) = MeetsTheRoad(c) ? BridgeStructures.Ends(c, GenerationWorld) : default;
            foreach (var plan in plans)
            {
                if (plan.Points.Count == 0) continue;
                Vector2 a = plan.Points[0], b = plan.Points[plan.Points.Count - 1];
                from |= Vector2.Distance(a, c.FromBank) < 3f || Vector2.Distance(b, c.FromBank) < 3f
                    || (near.IsSet && (Vector2.Distance(a, near.Joint) < 1f || Vector2.Distance(b, near.Joint) < 1f));
                to |= Vector2.Distance(a, c.ToBank) < 3f || Vector2.Distance(b, c.ToBank) < 3f
                    || (far.IsSet && (Vector2.Distance(a, far.Joint) < 1f || Vector2.Distance(b, far.Joint) < 1f));
            }
            from |= AtRoadEnd(c.FromBank);
            to |= AtRoadEnd(c.ToBank);
            if (!from || !to) { dangling = c; return true; }
        }
        return false;
    }

    /// <summary>
    /// A road running alongside another in the same corridor merges onto it even when it is
    /// further off than <see cref="RoadSnap"/>: a generated world was measured with ~400 m of
    /// road with a second road 8-14 m beside it on the same heading at nearly the same height,
    /// beyond the snap and beyond the reuse discount (which only prices cells the other road
    /// covers). A waypoint is moved onto the other road when that road is within this reach,
    /// heads the same way (within <see cref="CorridorAngle"/>) and stands within
    /// <see cref="CorridorHeight"/> of the ground here, over a run of at least
    /// <see cref="CorridorRun"/> metres; the waypoints either side of a run move halfway, so the
    /// road leans into the join instead of jogging. Settable for tests; 0 is off.
    /// </summary>
    internal static float CorridorSnap = 14f;
    internal const float CorridorAngle = RoadRide.Angle;
    internal const float CorridorHeight = 3f;
    internal const float CorridorRun = 12f;

    /// <summary>Why a planned road may not meet the network as it would: it crosses another road (<see cref="CrossesNetwork"/>;
    /// it meets one at a junction, at its ends or along a stretch it shares, or it is not built, even if that rejects a POI), or it runs beside one at its height (<see cref="BraidsNetwork"/>;
    /// where its search did not put it on the other road it braids beside it: roads at 3354,6308 and -5487,5808 were a
    /// mess), or it runs on or under a staircase's flight laid before it (<see cref="RoadStairRuns.OnALaidFlight"/>: it meets
    /// a staircase only at its foot or top, straight on). Null where it does none of these; <paramref name="at"/>: where. The
    /// one test of a road against the network it meets, for the road, its earthwork re-routes and the ways onto a placed
    /// place's landing.</summary>
    internal static string? MeetingRefusal(List<RoadSpatialGrid.PlannedPath> plans, out Vector2 at) => MeetingRefusal(plans, out at, out _, landOnFlight: false);

    /// <summary>
    /// <see cref="MeetingRefusal(List{RoadSpatialGrid.PlannedPath}, out Vector2)"/>; with <paramref name="landOnFlight"/>, a road
    /// on or under a staircase's flight is not refused where a landing level with the row it reaches fits
    /// (RoadStairRuns.LandingOnto, its fill within the earthwork cap): <paramref name="landed"/>, which the road is then laid
    /// with (<see cref="LandOnTheFlight"/>). It meets the foot straight on, else it lands on the row it reaches, else it is
    /// refused. Only the road itself is laid with one: its re-routes and its ways onto a place's landing are refused as before.
    /// </summary>
    internal static string? MeetingRefusal(List<RoadSpatialGrid.PlannedPath> plans, out Vector2 at, out RoadStairRuns.FlightLanding? landed, bool landOnFlight)
    {
        at = default;
        landed = null;
        if (CrossesNetwork(plans, out at)) return "crosses another road";
        if (BraidsNetwork(plans, out at)) return "runs beside another road";
        var (points, heights, _) = Flatten(plans);
        if (!RoadStairRuns.OnALaidFlight(points, heights, out at)) return null;
        if (landOnFlight && RoadStairRuns.LandingOnto(points, heights, (x, z) => BiomeBlendedHeight.GetBlendedHeight(x, z, GenerationWorld),
                EarthworkLimit) is RoadStairRuns.FlightLanding fits)
        {
            landed = fits;
            return null;
        }
        return OnAFlightRefusal;
    }

    /// <summary>
    /// Why a way onto a placed place's landing from the road's end at <paramref name="end"/> (a road or a staircase) may not
    /// meet the road and the network as it would (<see cref="PlanEntry"/>), or null: a staircase that doubles back beside its
    /// own road (<see cref="DoublesBackBesideItsRoad"/>), and, with <paramref name="network"/>, the road with it against the
    /// network (<see cref="MeetingRefusal(List{RoadSpatialGrid.PlannedPath}, out Vector2)"/>).
    /// </summary>
    /// <param name="own">The road's own earlier stretch (<see cref="OwnRoadOf"/>), built once for every candidate way from the
    /// same road; built here where not given.</param>
    internal static string? EntryMeetingRefusal(bool atStart, List<RoadSpatialGrid.PlannedPath> plans, Vector2 end,
        RoadSpatialGrid.PlannedPath? road, RoadStairSpan? span, bool network, Lazy<OwnRoad?>? own = null)
    {
        var all = WithEntry(atStart, plans, end, road, span);
        if (span != null && all.Count > plans.Count
            && DoublesBackBesideItsRoad((own ?? new Lazy<OwnRoad?>(() => OwnRoadOf(plans, atStart), false)).Value, all[atStart ? 0 : all.Count - 1], out Vector2 back))
            return FormattableString.Invariant($"{DoublesBackRefusal} at ({back.x:F1}, {back.y:F1})");
        return network && MeetingRefusal(all, out Vector2 at) is string meeting ? FormattableString.Invariant($"{meeting} at ({at.x:F1}, {at.y:F1})") : null;
    }

    /// <summary>The refusal of a way onto a landing that doubles back beside its own road (<see cref="DoublesBackBesideItsRoad"/>).</summary>
    internal const string DoublesBackRefusal = "doubles back beside its own road";

    /// <summary>
    /// Whether a way onto a placed place's landing (<paramref name="entry"/>, on the road's end at the place: its start where
    /// <paramref name="atStart"/>) ends beside the road's own earlier stretch at that road's height, as a road beside a road
    /// is judged (<see cref="RoadBeside"/>: within its reach across, within its height), away from the junction where the
    /// two meet (<see cref="JunctionEnd"/> back along the road from its end). The road was already at that height there: the
    /// staircase climbed or came down to it beside itself (a road coming in level with a landing, down a dip to its end, and
    /// a staircase from there doubling back up beside it onto the landing, its top a few metres from the road at the road's
    /// height), and is laid from further back along the road instead. A staircase that doubles back to end well above or
    /// below its road is not beside it. The network's roads are <see cref="BraidsNetwork"/>'s.
    /// </summary>
    internal static bool DoublesBackBesideItsRoad(OwnRoad? own, RoadSpatialGrid.PlannedPath entry, out Vector2 at)
    {
        at = default;
        if (own == null || entry.Points.Count == 0) return false;
        // Where it ends: its end away from the road's.
        int first = 0, last = entry.Points.Count - 1;
        int far = Vector2.Distance(entry.Points[first], own.End) >= Vector2.Distance(entry.Points[last], own.End) ? first : last;
        if (!own.Lines.Nearest(entry.Points[far], RoadBeside.Reach, out _, height: entry.Heights[far], heightTolerance: RoadBeside.Height)) return false;
        at = entry.Points[far];
        return true;
    }

    /// <summary>A road's own line short of its end at a place (<see cref="DoublesBackBesideItsRoad"/>), and that end.</summary>
    internal sealed class OwnRoad
    {
        internal readonly RoadRide.Lines Lines;
        internal readonly Vector2 End;
        internal OwnRoad(RoadRide.Lines lines, Vector2 end) { Lines = lines; End = end; }
    }

    /// <summary>The road <paramref name="plans"/>' own line, its junction with the way onto the landing at its end (its
    /// start where <paramref name="atStart"/>) left out (<see cref="JunctionEnd"/>); null where nothing is left.</summary>
    internal static OwnRoad? OwnRoadOf(List<RoadSpatialGrid.PlannedPath> plans, bool atStart)
    {
        var (points, heights, along) = Flatten(plans);
        if (points.Count < 2) return null;
        float total = along[points.Count - 1];
        var ownPoints = new List<Vector2>();
        var ownHeights = new List<float>();
        for (int i = 0; i < points.Count; i++)
            if ((atStart ? along[i] : total - along[i]) >= JunctionEnd) { ownPoints.Add(points[i]); ownHeights.Add(heights[i]); }
        if (ownPoints.Count == 0) return null;
        RoadRide.Lines lines = RoadRide.Around(Array.Empty<Vector2>(), 0f);
        lines.AddRoad(ownPoints, ownHeights);
        return new OwnRoad(lines, points[atStart ? 0 : points.Count - 1]);
    }

    /// <summary>A road laid with a landing onto a staircase's lower stairs (<see cref="MeetingRefusal(List{RoadSpatialGrid.PlannedPath}, out Vector2, out RoadStairRuns.FlightLanding?, bool)"/>):
    /// its heights levelled over the landing, as the landing found them.</summary>
    internal static void LandOnTheFlight(List<RoadSpatialGrid.PlannedPath> plans, RoadStairRuns.FlightLanding landing)
    {
        int k = 0;
        foreach (RoadSpatialGrid.PlannedPath plan in plans)
            for (int i = 0; i < plan.Heights.Count; i++) plan.Heights[i] = landing.Heights[k++];
    }

    /// <summary>The refusal of a road that runs on or under a staircase's flight (<see cref="MeetingRefusal"/>).</summary>
    internal const string OnAFlightRefusal = "runs under a staircase's flight";

    /// <summary>A stretch of a road this long or longer running on another road shares it (a trunk both walk); shorter, it
    /// passes through.</summary>
    public const float SharedRun = 12f;
    /// <summary>A road's own ends meet the network within this of them: a junction, not a crossing.</summary>
    public const float JunctionEnd = 6f;

    /// <summary>A road's planned pieces end to end: their points, heights, and how far along the road each point lies.</summary>
    private static (List<Vector2> points, List<float> heights, float[] along) Flatten(List<RoadSpatialGrid.PlannedPath> plans)
    {
        var points = new List<Vector2>();
        var heights = new List<float>();
        foreach (RoadSpatialGrid.PlannedPath plan in plans) { points.AddRange(plan.Points); heights.AddRange(plan.Heights); }
        var along = new float[points.Count];
        for (int i = 1; i < points.Count; i++) along[i] = along[i - 1] + Vector2.Distance(points[i - 1], points[i]);
        return (points, heights, along);
    }

    /// <summary>
    /// Whether a planned road passes through another already built: away from its own ends, a stretch on that road
    /// shorter than <see cref="SharedRun"/> that comes in on one side of it and leaves on the other (an X at grade, or one
    /// road over or under another). A road may still meet the network at its ends (a T or Y) and share a trunk along a
    /// longer stretch. <paramref name="at"/>: where.
    /// </summary>
    internal static bool CrossesNetwork(List<RoadSpatialGrid.PlannedPath> plans, out Vector2 at)
    {
        at = default;
        if (!RoadSpatialGrid.IsInitialized) return false;
        var (points, _, along) = Flatten(plans);
        if (points.Count < 3) return false;
        float total = along[points.Count - 1], touch = RoadWidth * 0.5f;
        // The network's line (RoadRide): a crossing's or a staircase's line is road too, so a road through a structure's gap
        // in another road crosses it as it crosses the road.
        var lines = RoadRide.Around(points, RoadRide.Reach);
        int k = 0;
        while (k < points.Count)
        {
            if (!lines.Nearest(points[k], touch, out _)) { k++; continue; }
            int first = k;
            while (k + 1 < points.Count && lines.Nearest(points[k + 1], touch, out _)) k++;
            int last = k++;
            if (along[first] < JunctionEnd || total - along[last] < JunctionEnd) continue;
            if (along[last] - along[first] >= SharedRun) continue;
            // The other road's line here: its heading at the middle of the touch (RoadRide: from its road points 1.5-4 m
            // apart, never a staircase's cut-only points, which near a staircase's end turned the line).
            Vector2 mid = points[(first + last) / 2];
            if (!lines.Nearest(mid, 4f, out RoadRide.Hit line) || !line.HasHeading) continue;
            Vector2 centre = line.At, dir = line.Dir, normal = new Vector2(-dir.y, dir.x);
            // Its own points a few metres before and after the touch, either side of the other road's line.
            int before = first, after = last;
            while (before > 0 && along[first] - along[before] < 4f) before--;
            while (after < points.Count - 1 && along[after] - along[last] < 4f) after++;
            float sideIn = Vector2.Dot(points[before] - centre, normal), sideOut = Vector2.Dot(points[after] - centre, normal);
            if (Mathf.Abs(sideIn) > touch && Mathf.Abs(sideOut) > touch && (sideIn > 0f) != (sideOut > 0f))
            {
                at = mid;
                return true;
            }
        }
        return false;
    }


    /// <summary>
    /// Whether a planned road runs beside another already built, at its height, for longer than the cap
    /// (<see cref="RoadBeside"/>: within its reach across and its height, off the other road), away from its own ends
    /// (<see cref="JunctionEnd"/>: its junctions). A lens (off the other road and back onto it), a duplicate (beside it all
    /// the way) and a road that comes in beside the trunk and ends on it, or starts on it and runs out beside it (a Y at its
    /// own end), are judged alike, by how long they run beside it (the length is capped like every
    /// other parallel check; such a Y once passed however long it ran beside the trunk). A road that shares a trunk
    /// along its middle and comes onto it or leaves it there (onto a shared bridge and off again) is not judged here: its run beside the trunk is the fork where two roads part.
    /// </summary>
    internal static bool BraidsNetwork(List<RoadSpatialGrid.PlannedPath> plans, out Vector2 at)
    {
        at = default;
        if (!RoadSpatialGrid.IsInitialized) return false;
        var (points, heights, along) = Flatten(plans);
        if (points.Count < 3) return false;
        float total = along[points.Count - 1];
        // Where it rides the network (RoadRide: within a road's width, its way, at its height) it is on the other road, the
        // trunk the two share, whose road it then stores none of; beside it is past that, within RoadBeside's reach.
        var lines = RoadRide.Around(points, RoadBeside.Reach);
        bool[] rides = RoadRide.Rides(lines, points, heights);
        var place = new RoadBeside.Place[points.Count];
        for (int i = 0; i < points.Count; i++) place[i] = rides[i] ? RoadBeside.Place.On : RoadBeside.At(lines, points[i], heights[i]);
        // Whether the road is on the other road from point i to its end (step +1) or from its start to i (step -1), its
        // junction's mouth aside.
        bool OnToItsEnd(int i, int step)
        {
            for (int j = i; j >= 0 && j < points.Count; j += step)
                if (place[j] != RoadBeside.Place.On && (step > 0 ? total - along[j] : along[j]) >= JunctionEnd) return false;
            return true;
        }
        int k = 0;
        while (k < points.Count)
        {
            if (place[k] != RoadBeside.Place.Beside) { k++; continue; }
            int first = k;
            while (k + 1 < points.Count && place[k + 1] == RoadBeside.Place.Beside) k++;
            int last = k++;
            bool fromTrunk = first > 0 && place[first - 1] == RoadBeside.Place.On, toTrunk = last + 1 < points.Count && place[last + 1] == RoadBeside.Place.On;
            // A fork off a trunk shared along the road's middle: not judged (see the summary).
            if (fromTrunk != toTrunk && !(toTrunk ? OnToItsEnd(last + 1, +1) : OnToItsEnd(first - 1, -1))) continue;
            // Its own ends left out: the stretch between them.
            while (first <= last && along[first] < JunctionEnd) first++;
            while (last >= first && total - along[last] < JunctionEnd) last--;
            if (first <= last && RoadBeside.TooLong(along[last] - along[first])) { at = points[first]; return true; }
        }
        return false;
    }

    /// <summary>Waypoints moved onto the network's line nearby. A held waypoint (<paramref name="held"/>: a structure's end
    /// the road was brought to) stays where it is and in the path, neither snapped nor eased toward a corridor.</summary>
    internal static List<Vector2>? SnapToNetwork(List<Vector2>? path, float? width = null, System.Func<Vector2, bool>? held = null)
    {
        if (path == null || RoadSnap <= 0f || path.Count < 3 || !RoadSpatialGrid.IsInitialized) return path;
        var moved = new Vector2[path.Count];
        var fixedPoint = new bool[path.Count];
        var keep = new bool[path.Count];
        for (int i = 0; i < path.Count; i++) { moved[i] = path[i]; keep[i] = held != null && held(path[i]); }
        // Onto the network's line (RoadRide): a crossing's or a staircase's line too, so a road coming along another over
        // its bridge or ford snaps onto the deck's line, not to the banks either side of the gap.
        var lines = RoadRide.Around(path, Mathf.Max(RoadSnap, CorridorSnap * 1.5f));
        // A structure's line only about the ground's height here (never a flight or deck far over or under the waypoint).
        for (int i = 1; i < path.Count - 1; i++)
        {
            if (keep[i]) { fixedPoint[i] = true; continue; }
            bool road = lines.Nearest(path[i], RoadSnap, out RoadRide.Hit onRoad, structures: false);
            if (GenerationWorld != null && lines.Nearest(path[i], road ? onRoad.Distance : RoadSnap, out RoadRide.Hit onDeck, height: BiomeBlendedHeight.GetBlendedHeight(path[i].x, path[i].y, GenerationWorld),
                    heightTolerance: RoadRide.StructureHeight, roads: false))
            { onRoad = onDeck; road = true; }
            if (road) { moved[i] = onRoad.At; fixedPoint[i] = true; }
        }
        if (CorridorSnap > RoadSnap && GenerationWorld != null)
        {
            // The nearest of the network's line within the corridor's reach heading this way, at about this ground's height.
            var corridor = new Vector2?[path.Count];
            for (int i = 1; i < path.Count - 1; i++)
                if (!fixedPoint[i] && lines.Nearest(path[i], CorridorSnap, out RoadRide.Hit same, path[i + 1] - path[i - 1],
                        BiomeBlendedHeight.GetBlendedHeight(path[i].x, path[i].y, GenerationWorld), CorridorHeight, paintOnly: false))
                    corridor[i] = same.At;
            int a = 1;
            while (a < path.Count - 1)
            {
                if (corridor[a] == null) { a++; continue; }
                int b = a;
                while (b + 1 < path.Count - 1 && (corridor[b + 1] != null || fixedPoint[b + 1])) b++;
                float run = 0f;
                for (int k = a; k < b; k++) run += Vector2.Distance(path[k], path[k + 1]);
                if (run >= CorridorRun)
                {
                    for (int k = a; k <= b; k++) if (corridor[k] is Vector2 q) moved[k] = q;
                    if (a - 1 > 0 && !fixedPoint[a - 1] && lines.Nearest(path[a - 1], CorridorSnap * 1.5f, out RoadRide.Hit qa))
                        moved[a - 1] = Vector2.Lerp(path[a - 1], qa.At, 0.5f);
                    if (b + 1 < path.Count - 1 && !fixedPoint[b + 1] && lines.Nearest(path[b + 1], CorridorSnap * 1.5f, out RoadRide.Hit qb))
                        moved[b + 1] = Vector2.Lerp(path[b + 1], qb.At, 0.5f);
                    System.Threading.Interlocked.Add(ref m_corridorSnapped, b - a + 1);
                }
                a = b + 1;
            }
        }
        var result = new List<Vector2>(path.Count) { path[0] };
        bool lastKept = true;
        for (int i = 1; i < path.Count - 1; i++)
        {
            bool near = Vector2.Distance(result[result.Count - 1], moved[i]) <= 0.5f;
            if (!near) { result.Add(moved[i]); lastKept = keep[i]; }
            // A held waypoint takes the place of a snapped one it landed on.
            else if (keep[i] && !lastKept) { result[result.Count - 1] = moved[i]; lastKept = true; }
        }
        result.Add(path[path.Count - 1]);
        // Snapping is cosmetic. Its new connecting legs have not been checked
        // by the route search, so keep the original path when the merge cuts
        // a protected site. Before trimming, the route's own endpoint
        // footprints are crossed only as its departure and arrival, just as
        // during the search (RoadSiteProtection.BlocksRoute).
        float clearance = RoadSiteProtection.Clearance(width ?? RoadWidth);
        for (int i = 1; i < result.Count; i++)
            if (RoadSiteProtection.BlocksRoute(result[i - 1], result[i], clearance, path[0], path[path.Count - 1]))
                return path;
        return result;
    }

    private static int m_corridorSnapped;
    /// <summary>Roads ended on a place's approach's road instead of beside it (<see cref="ReEndOnAWayIn"/>), and those still refused so.</summary>
    private static int m_reEndedOnApproach, m_reEndRefused;
    private static int m_junctionFills;
    private static int m_chainsShared;

    /// <summary>
    /// Why a planned road may not be built as it stands, and where; null when it may. The one list of generation's refusals
    /// on a road as planned, each asked of its own owner: earthwork past the terrain limit, a crossing no style may take or a
    /// deck through the ground (InvalidCrossings), a junction ridge (HeldRidges), crossing or running beside another road
    /// (MeetingRefusal), a valley viaduct's end the road does not meet or road under its deck (BuiltRefusal) and a bridge with no
    /// road beyond it (DanglingBridge). Generation searches again round most of these before it refuses; a manual road,
    /// which has no other route, is refused with the reason.
    /// </summary>
    internal static string? BuiltRoadRefusal(List<RoadSpatialGrid.PlannedPath> plans, List<RoadCrossing> crossings, out Vector2 at)
    {
        at = default;
        var where = new List<Vector2>();
        if (PastTerrainLimit(plans, EarthworkLimit, where) > 0) { at = where[0]; return EarthworkRefusal; }
        if (InvalidCrossings(crossings, where) > 0)
        {
            at = where.Count > 0 ? where[where.Count - 1] : at;
            return crossings.Exists(c => c.Invalid && c.Shallow) ? "shallow water too long or deep to cross" : "a bridge deck through the ground between its banks";
        }
        if (HeldRidges(plans, where) > 0) { at = where[where.Count - 1]; return $"a junction ridge: held to a road on more than {JunctionFillLimit:F0} m of fill"; }
        if (MeetingRefusal(plans, out at) is string meeting) return meeting;
        // Generation counts a river bridge's or a span's end it cannot meet and builds the road; only a valley viaduct's
        // failure, not shared, refuses it (GenerateRoadCore).
        if (BuiltRefusal(plans, crossings, out at, out string built) is RoadCrossing unmet && unmet.ValleyJump && !unmet.Shared) return built;
        var points = plans.SelectMany(p => p.Points).ToList();
        if (points.Count > 1 && DanglingBridge(plans, crossings, points[0], points[points.Count - 1], out RoadCrossing dangling))
        { at = dangling.Center; return "a bridge with no road at one end"; }
        return null;
    }

    /// <summary>
    /// What generation searches a road again round, where it can, and where: a stretch held to a road already laid more than
    /// <see cref="HoldPullLimit"/> off its own profile (HeldOffProfile, the earthwork re-route), then a trench or ridge
    /// (RoadRidges, read on the ground as <see cref="OffRidges"/> reads it); asked only as GenerateRoadCore asks them, with an
    /// earthwork cap and its retries. Null where none. Generation builds a road anyway where no way round clears these; a road
    /// with no other way to search (a staircase's end carried on to its first stair, tried once) is refused for them instead:
    /// the consequence of its one try, not another rule.
    /// </summary>
    internal static string? SearchedRound(List<RoadSpatialGrid.PlannedPath> plans, out Vector2 at)
    {
        at = default;
        if (!EarthworkCapped) return null;
        var where = new List<Vector2>();
        if (EarthworkRetries > 0 && HeldOffProfile(plans, where) > 0)
        { at = where[0]; return $"held to a road more than {HoldPullLimit:F0} m off its profile"; }
        if (RidgeRetries > 0 && RoadRidges.Find(plans, NaturalGround) is { Count: > 0 } ridges)
        {
            at = ridges[0].Points[0];
            return FormattableString.Invariant($"a {(ridges[0].Kind == RoadRidges.Kind.Ridge ? "ridge" : "trench")}: {ridges[0].Worst:F1} m for {ridges[0].Length:F0} m");
        }
        return null;
    }

    /// <summary>What a road stores of its planned pieces: none of what rides the network (RoadRide.Own), with its own decks
    /// (a spanned ford's, a bridge's whose ends are not met) kept clear.</summary>
    internal static List<RoadSpatialGrid.PlannedPath> OwnPieces(List<RoadSpatialGrid.PlannedPath> plans, List<RoadCrossing> crossings,
        out float ridden, out List<RoadRide.Segment> decks)
    {
        decks = RoadRide.DeckLines(plans, crossings.FindAll(c => (c.Kind == CrossingKind.Ford && c.Style == FordStyle.Span) || (c.Kind == CrossingKind.Bridge && !MeetsTheRoad(c))));
        return RoadRide.Own(plans, out ridden, decks);
    }

    /// <summary>The search a road is planned with: generation's finders and a manual road's alike.</summary>
    internal static RoadPathfinder NewFinder(WorldGenerator world, float siteClearance)
    {
        var finder = new RoadPathfinder(world) { SiteClearance = siteClearance };
        ConfigureFinder?.Invoke(finder);
        return finder;
    }

    /// <summary>Tests' settings for every search this thread makes (NewFinder), generated and manual alike.</summary>
    [ThreadStatic] private static Action<RoadPathfinder>? ConfigureFinder;
    internal static void ConfigureFinders(Action<RoadPathfinder>? configure) => ConfigureFinder = configure;

    /// <summary>The terrain limit a planned road is held to, metres of cut or
    /// fill (vanilla TerrainComp clamps level deltas to +/-8 m). A road still
    /// past it after the re-route retries is refused. Settable for tests; 0 is
    /// off.</summary>
    internal static float EarthworkCap = DefaultEarthworkCap;

    /// <summary>The cap a game runs with: the level-delta clamp the game itself applies (<see cref="RoadConstants.TerrainDeltaMax"/>).</summary>
    internal const float DefaultEarthworkCap = RoadConstants.TerrainDeltaMax;

    // ---- The earthwork cap as each kind of rule reads it: the one place its unset case (EarthworkCap 0) is decided ----
    // Three readings, each meant, none a copy of another:
    //  - EarthworkCapped: the searches that exist only to keep a road within the cap (the re-routes round deep earthwork,
    //    held stretches and ridges; the humps laid as road) run only with one.
    //  - EarthworkLimit: what a road, a landing or a structure's end is judged by; without a cap there is no limit. A test
    //    skipped and a limit no height passes are the same reading.
    //  - Shaped: how far ground is cut or filled to a height (a start pad, an ease's reach, ground no road is cut through):
    //    within the limit, and never past what the terrain itself moves (RoadConstants.TerrainDeltaMax, where the game
    //    clamps a level delta). Without a cap, the terrain's own.

    /// <summary>Whether a cap is set (<see cref="EarthworkCap"/>): the searches that keep a road within it run only then.</summary>
    internal static bool EarthworkCapped => EarthworkCap > 0f;

    /// <summary>The limit a road, a landing or an end is judged by: <see cref="EarthworkCap"/>, or none (infinity) without one.</summary>
    internal static float EarthworkLimit => EarthworkCap > 0f ? EarthworkCap : float.PositiveInfinity;

    /// <summary>How far ground is shaped within <paramref name="limit"/>: never past what the terrain moves
    /// (<see cref="RoadConstants.TerrainDeltaMax"/>).</summary>
    internal static float Shaped(float limit) => Mathf.Min(limit, RoadConstants.TerrainDeltaMax);

    /// <summary><see cref="Shaped(float)"/> of <see cref="EarthworkLimit"/>: the cap, or without one the terrain's own limit.</summary>
    internal static float ShapedLimit => Shaped(EarthworkLimit);

    /// <summary>Crossings that may not be built, counted into the earthwork re-route so the search goes round them:
    /// shallow water no ford style may cross (too long or deep), and a bridge whose deck would meet ground between its
    /// banks (RoadCrossingDetector.ClearOfGround). Each adds its banks and the line between to
    /// <paramref name="where"/> (the cells priced up).</summary>
    internal static int InvalidCrossings(List<RoadCrossing> crossings, List<Vector2>? where)
    {
        int n = 0;
        foreach (var c in crossings)
        {
            if (!c.Invalid) continue;
            n++;
            if (where == null) continue;
            float len = Vector2.Distance(c.FromBank, c.ToBank);
            int k = Mathf.Max(1, Mathf.CeilToInt(len / 16f));
            for (int i = 0; i <= k; i++) where.Add(Vector2.Lerp(c.FromBank, c.ToBank, (float)i / k));
        }
        return n;
    }
    internal static int EarthworkRetries = 2;
    /// <summary>Searches round a road's trenches and ridges (<see cref="RoadRidges"/>) per road; 0 is none. Settable for tests.</summary>
    internal static int RidgeRetries = 2;
    private static int m_ridgeReroutes;
    /// <summary>Roads re-routed off a trench or ridge this generation.</summary>
    internal static int RidgeReroutes => m_ridgeReroutes;

    private static float NaturalGround(Vector2 p) => BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, GenerationWorld);

    /// <summary>
    /// A road within the earthwork cap laid in a trench or along a ridge (<see cref="RoadRidges"/>) is searched again round
    /// it, up to <see cref="RidgeRetries"/> times, the cells round each such stretch priced up as the earthwork re-route
    /// prices them. A re-route is kept only where it lays a third fewer metres of trench and ridge and passes what the
    /// plan it replaces passed: nothing past the cap, no held ridge, no more stretches held off its profile, no invalid
    /// crossing, and the network takes it as it meets it. Otherwise the road keeps its plan.
    /// </summary>
    private static (List<Vector2> path, List<RoadCrossing> crossings, List<RoadSpatialGrid.PlannedPath> plans, List<RoadStairSpan> stairs, bool startAtPlace, bool endAtPlace) OffRidges(
        RoadPathfinder finder, (List<Vector2> path, List<RoadCrossing> crossings, List<RoadSpatialGrid.PlannedPath> plans, List<RoadStairSpan> stairs, bool startAtPlace, bool endAtPlace) road,
        int pulled, Vector2 pathStart, Vector2 pathEnd, Vector2 startCenter, float startRadius, Vector2 endCenter, float endRadius, float width,
        Vector2? startAim, Vector2? endAim, bool startReached, bool endReached)
    {
        var stretches = RoadRidges.Find(road.plans, NaturalGround);
        float metres = RoadRidges.Metres(stretches);
        for (int attempt = 0; attempt < RidgeRetries && stretches.Count > 0; attempt++)
        {
            foreach (var stretch in stretches) PenalizeStretch(finder, stretch);
            var retry = Replan(finder, pathStart, pathEnd, startCenter, startRadius, endCenter, endRadius, width, startAim, endAim, startReached, endReached);
            if (retry == null) continue;
            var after = RoadRidges.Find(retry.Value.plans, NaturalGround);
            if (!KeepsOffRidges(retry.Value.plans, retry.Value.crossings, metres, after, pulled)) { stretches = after; continue; }
            System.Threading.Interlocked.Increment(ref m_ridgeReroutes);
            road = retry.Value;
            stretches = after;
            metres = RoadRidges.Metres(after);
        }
        // What no way round clears goes up or down on stairs: the stretches left are opened to stairs, their road priced
        // up as before and their staircases not, and searched once more.
        if (stretches.Count > 0 && finder.Stairs && RidgeRetries > 0)
        {
            foreach (var stretch in stretches) PenalizeStretch(finder, stretch);
            var opened = finder.OpenToStairs(stretches.SelectMany(st => st.Points), RidgeStairReachCells);
            var retry = Replan(finder, pathStart, pathEnd, startCenter, startRadius, endCenter, endRadius, width, startAim, endAim, startReached, endReached);
            finder.ShutToStairs(opened);
            if (retry != null && KeepsOffRidges(retry.Value.plans, retry.Value.crossings, metres, RoadRidges.Find(retry.Value.plans, NaturalGround), pulled))
            {
                System.Threading.Interlocked.Increment(ref m_ridgeStairs);
                road = retry.Value;
            }
        }
        finder.ClearEarthworkPenalty();
        return road;
    }

    /// <summary>Cells round a trench or ridge opened to stairs where no re-route clears it.</summary>
    internal const int RidgeStairReachCells = 1;
    private static int m_ridgeStairs;
    /// <summary>Roads taken off a trench or ridge onto stairs this generation.</summary>
    internal static int RidgeStairs => m_ridgeStairs;

    /// <summary>The cells round a trench or ridge priced up, a point every <see cref="EarthworkPenaltyRadius"/> along it.</summary>
    private static void PenalizeStretch(RoadPathfinder finder, RoadRidges.Stretch stretch)
    {
        Vector2? last = null;
        foreach (var p in stretch.Points)
        {
            if (last is Vector2 l && Vector2.Distance(l, p) < EarthworkPenaltyRadius * 0.5f && p != stretch.Points[stretch.Points.Count - 1]) continue;
            finder.PenalizeAround(p, EarthworkPenaltyRadius);
            last = p;
        }
    }

    /// <summary>Whether a re-planned road replaces one with <paramref name="before"/> metres of trench and ridge and
    /// <paramref name="pulled"/> stretches held off its profile: a third fewer metres (<paramref name="after"/>), no more held
    /// stretches, and nothing a road is refused for.</summary>
    private static bool KeepsOffRidges(List<RoadSpatialGrid.PlannedPath> plans, List<RoadCrossing> crossings, float before, List<RoadRidges.Stretch> after, int pulled)
    {
        if (RoadRidges.Metres(after) * 3f > before * 2f) return false;
        var none = new List<Vector2>();
        return PastTerrainLimit(plans, EarthworkLimit, none) + InvalidCrossings(crossings, none) + HeldRidges(plans, none) == 0
            && HeldOffProfile(plans, none) <= pulled && MeetingRefusal(plans, out _) == null;
    }
    /// <summary>Cells within this of an offending point are priced up.</summary>
    internal const float EarthworkPenaltyRadius = 12f;
    private static int m_earthworkReroutes, m_earthworkUnavoided;
    /// <summary>Where the squeeze between a place and the water refused the
    /// search: one Info line, then one Debug line per place, named from the
    /// location list, busiest first.</summary>
    private static void LogSqueeze()
    {
        var hits = RoadPathfinder.SqueezeHits.ToArray();
        int steps = 0, landings = 0;
        foreach (var kv in hits) { steps += kv.Value.steps; landings += kv.Value.landings; }
        Log.LogInfo($"  Squeeze between a place and the water: refused {steps} search step(s) and {landings} crossing landing(s) beside {hits.Length} place(s)");
        int fills = 0;
        foreach (var kv in RoadPathfinder.ShoreFillCells) fills += kv.Value;
        Log.LogInfo($"  Shallows built out beside a place: the search went through {fills} cell(s) beside {RoadPathfinder.ShoreFillCells.Count} place(s)");
        var locations = ZoneSystem.instance != null ? ZoneSystem.instance.GetLocationList() : null;
        foreach (var kv in System.Linq.Enumerable.OrderByDescending(hits, h => h.Value.steps + h.Value.landings))
        {
            var c = new Vector2(kv.Key.x, kv.Key.z);
            string? name = "?";
            if (locations != null)
                foreach (var loc in locations)
                    if (Vector2.Distance(new Vector2(loc.m_position.x, loc.m_position.z), c) < 1.5f) { name = loc.m_location.m_prefab.Name ?? ""; break; }
            Log.LogDebug($"Squeeze: {name} at ({kv.Key.x},{kv.Key.z}): {kv.Value.steps} step(s), {kv.Value.landings} landing(s) refused");
        }
    }

    /// <summary>Deck ends walked in off land on built roads.</summary>
    private static int m_offLandEnds;
    internal static (int reroutes, int unavoided) EarthworkCounts => (m_earthworkReroutes, m_earthworkUnavoided);

    /// <summary>Why a road past the earthwork cap is refused (<see cref="BuiltRoadRefusal"/>).</summary>
    internal static string EarthworkRefusal => $"earthwork past the {EarthworkCap:F0} m terrain limit";

    /// <summary>Whether a road point at <paramref name="p"/> stored at <paramref name="height"/> is past <paramref name="cap"/>
    /// of earthwork: the one reading of a point against the cap (<see cref="PastTerrainLimit(List{RoadSpatialGrid.PlannedPath}, float, List{Vector2})"/>,
    /// and a point known before its road is planned, RoadStairRuns.RoadOnto).</summary>
    internal static bool PastTerrainLimit(Vector2 p, float height, float cap, WorldGenerator? world = null) =>
        PastTerrainLimit(height, BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world ?? GenerationWorld), cap);

    /// <summary>Whether ground cut or filled to <paramref name="height"/> from <paramref name="ground"/> is past <paramref name="cap"/>.</summary>
    internal static bool PastTerrainLimit(float height, float ground, float cap) => Mathf.Abs(height - ground) > cap;

    /// <summary>Whether ground cut or filled to <paramref name="height"/> from <paramref name="ground"/> stays within <paramref name="cap"/>.
    /// Not <c>!PastTerrainLimit</c>: an unknown (NaN) height or ground is neither.</summary>
    internal static bool WithinTerrainLimit(float height, float ground, float cap) => Mathf.Abs(height - ground) <= cap;

    /// <summary>Points of the plans (not wades) cut or filled past <paramref name="cap"/>, listed in <paramref name="where"/>.</summary>
    internal static int PastTerrainLimit(List<RoadSpatialGrid.PlannedPath> plans, float cap, List<Vector2> where)
    {
        int past = 0;
        var world = GenerationWorld;
        foreach (var plan in plans)
        {
            if (plan.FollowTerrain) continue;
            for (int i = 0; i < plan.Points.Count; i++)
            {
                var p = plan.Points[i];
                if (PastTerrainLimit(p, plan.Heights[i], cap, world))
                {
                    past++;
                    if (where.Count == 0 || Vector2.Distance(where[where.Count - 1], p) > 6f) where.Add(p);
                }
            }
        }
        return past;
    }

    /// <summary>Furthest a stretch may be held to a road already laid from its own profile before the
    /// road is re-routed round it (<see cref="HeldOffProfile"/>): two junction steps.</summary>
    public const float HoldPullLimit = 3f;
    private static int m_pullReroutes;

    /// <summary>Most fill a stretch held to a road already laid may stand on: past it the junction is a
    /// ridge across the slope, refused like earthwork past the cap (re-routed first). At 832,4359 a stretch
    /// held 4.5-4.7 m over the ground was an awkward junction earthwork ridge.</summary>
    public const float JunctionFillLimit = 4f;

    /// <summary>Stretches of <paramref name="plans"/> held to a road already laid on more than
    /// <see cref="JunctionFillLimit"/> of fill; each one's worst point goes in <paramref name="where"/>.</summary>
    private static int HeldRidges(List<RoadSpatialGrid.PlannedPath> plans, List<Vector2>? where) =>
        CountJunctions(plans, j => j.Earth > JunctionFillLimit, where);

    /// <summary>Stretches of <paramref name="plans"/> held to a road already laid more than
    /// <see cref="HoldPullLimit"/> off their own profile; each one's worst point goes in <paramref name="where"/>.</summary>
    private static int HeldOffProfile(List<RoadSpatialGrid.PlannedPath> plans, List<Vector2> where) =>
        CountJunctions(plans, j => Mathf.Abs(j.Pull) > HoldPullLimit, where);

    /// <summary>The junctions of <paramref name="plans"/> that are <paramref name="past"/> a limit; each one's point goes in <paramref name="where"/>.</summary>
    private static int CountJunctions(List<RoadSpatialGrid.PlannedPath> plans, Func<RoadSpatialGrid.Junction, bool> past, List<Vector2>? where)
    {
        int count = 0;
        foreach (var plan in plans)
            if (plan.Junctions != null)
                foreach (var j in plan.Junctions)
                    if (past(j)) { count++; where?.Add(j.At); }
        return count;
    }

    /// <summary>The road searched and planned again, with the pathfinder's
    /// current penalties; null when it does not route or build. <paramref name="startAim"/> and <paramref name="endAim"/>:
    /// the doors the road's own search ended at (<see cref="DoorsOf"/>; null where it searched to the place), which the
    /// re-route comes in to head on as the road did.</summary>
    internal static (List<Vector2> path, List<RoadCrossing> crossings, List<RoadSpatialGrid.PlannedPath> plans, List<RoadStairSpan> stairs, bool startAtPlace, bool endAtPlace)? Replan(
        RoadPathfinder finder, Vector2 pathStart, Vector2 pathEnd, Vector2 startCenter, float startRadius,
        Vector2 endCenter, float endRadius, float width, Vector2? startAim, Vector2? endAim, bool startReached, bool endReached)
    {
        PlacedSite? startSite = startRadius > 0f && PlacedSites.TryGetReady(startCenter, out PlacedSite s0) ? s0 : null;
        PlacedSite? endSite = endRadius > 0f && PlacedSites.TryGetReady(endCenter, out PlacedSite s1) ? s1 : null;
        bool startPlaced = startSite != null, endPlaced = endSite != null;
        var path = finder.FindPath(pathStart, pathEnd);
        if (path == null || path.Count < 2) return null;
        // In head on from the lead-in to the ring point the road's search was aimed at, as GenerateRoadCore does.
        if (startAim is Vector2 sa) path.Insert(0, sa);
        if (endAim is Vector2 ea) path.Add(ea);
        path = SnapToNetwork(path, width);
        if (path == null || path.Count < 2) return null;
        RoadPlan planned = PlanFromPath(finder, path, startCenter, startRadius, endCenter, endRadius,
            startPlaced, endPlaced, startReached, endReached, width);
        return planned.Stop != PlanStop.Planned || planned.Plans == null ? null
            : (planned.Path, planned.Crossings, planned.Plans, planned.Stairs, planned.StartAtPlace, planned.EndAtPlace);
    }

    /// <summary>Where <see cref="PlanFromPath"/> stopped.</summary>
    internal enum PlanStop
    {
        /// <summary>Planned, or refused by the planner (<see cref="RoadPlan.Plans"/> null; its refusal in RoadSpatialGrid.LastRefusal).</summary>
        Planned,
        /// <summary>Nothing left between the two footprints once trimmed.</summary>
        NothingBetween,
        /// <summary>A staircase could not be laid on <see cref="RoadPlan.Path"/>.</summary>
        Staircase,
        /// <summary>Something the road needed was not prepared.</summary>
        Preparation,
    }

    /// <summary>A searched path made a road: what <see cref="PlanFromPath"/> returns.</summary>
    internal sealed class RoadPlan
    {
        public PlanStop Stop;
        public List<Vector2> Path = new();
        public List<RoadCrossing> Crossings = new();
        public List<RoadStairSpan> Stairs = new();
        public List<RoadSpatialGrid.PlannedPath>? Plans;
        public bool StartAtPlace, EndAtPlace;
        /// <summary>A crossing with an end no way of meeting reaches (StructureEnds.MeetEnds), with the search's moves it was
        /// detected on (<see cref="RoadCrossing.Searched"/>); <see cref="Plans"/> then null.</summary>
        public RoadCrossing? Unmet;
    }

    /// <summary>Why a road is refused where a crossing on it has an end no way of meeting reaches.</summary>
    internal const string EndNotMet = "a bridge end the road does not meet";

    /// <summary>
    /// The one pipeline from a searched (and snapped) path to a planned road: trim to the keep-out edges
    /// (<paramref name="startRadius"/>, <paramref name="endRadius"/> are those edges) and off wet ends, improve the
    /// approach to a place not placed first, drop the stubs where other network reaches the place
    /// (<paramref name="startReached"/>, <paramref name="endReached"/>: <see cref="EndReached"/>), detect and snap the
    /// crossings, lay the staircases, read the ground to meet at each end, refuse what preparation missed, plan; a road
    /// refused without its stubs is planned once with them. Used by the road's own build and by its earthwork re-route
    /// (<see cref="Replan"/>).
    /// </summary>
    internal static RoadPlan PlanFromPath(RoadPathfinder finder, List<Vector2> path, Vector2 startCenter, float startRadius,
        Vector2 endCenter, float endRadius, bool startPlaced, bool endPlaced, bool startReached, bool endReached, float width)
    {
        var result = new RoadPlan { Path = path };
        List<Vector2>? trimmed = TrimWetEnds(TrimPathToRadii(path, startCenter, startRadius, endCenter, endRadius));
        if (trimmed == null || trimmed.Count < 2) { result.Stop = PlanStop.NothingBetween; return result; }
        path = trimmed;
        // A placed place's end is already on its landing's side, and is carried on onto the landing.
        if (!startPlaced) path = ImproveSiteApproach(path, startCenter, startRadius, width, true);
        if (!endPlaced) path = ImproveSiteApproach(path, endCenter, endRadius, width, false);
        Vector2 beforeStart = path[0], beforeEnd = path[path.Count - 1];
        var withStubs = path;
        path = DropStubs(path, startReached, endReached) ?? path;
        // An end moved to a junction takes its height from the road it joins,
        // not from the place it no longer reaches: aiming at the place's edge
        // from the junction refused short roads as "ends too far apart for
        // the cap".
        bool startAtPlace = path[0] == beforeStart, endAtPlace = path[path.Count - 1] == beforeEnd;
        // Detected, snapped to a crossing an earlier road made a few metres off (it takes that site's banks and ends, so this
        // road is painted up to the one bridge built there), each crossing's ends met (StructureEnds.MeetEnds, the road
        // brought to each end's foot), stairs laid, the ground to meet at each end read, and planned.
        bool Plan(List<Vector2> on, bool atStart, bool atEnd, IReadOnlyList<(Vector2 from, Vector2 to)>? steps = null)
        {
            result.Stop = PlanStop.Planned;
            result.Path = on;
            result.StartAtPlace = atStart; result.EndAtPlace = atEnd;
            result.Crossings = DetectCrossings(finder, on);
            SnapToExistingCrossings(result.Crossings);
            foreach (RoadCrossing c in result.Crossings) c.Searched = SearchedMoves(on, c);
            result.Unmet = null;
            if (MeetEnds(finder, on, result.Crossings) is RoadCrossing unmet)
            {
                // No way of meeting one of its ends reaches the road: the road is searched round that crossing
                // (GenerateRoadCore), or refused.
                (result.Unmet, result.Plans) = (unmet, null);
                RoadSpatialGrid.LastRefusal = EndNotMet;
                return true;
            }
            if (!TryLayStairs(steps ?? finder.LastStairSteps, on, result.Crossings, out List<RoadStairSpan> stairs)) { result.Stop = PlanStop.Staircase; return false; }
            result.Stairs = stairs;
            // The heights to meet are asked for at the road's OWN ends, after
            // trimming - not at the locations' centres. A road stops at the
            // exterior radius, which on a hillside is metres below the middle of
            // the place it is going to, and aiming at the middle builds that
            // difference as a rim around the doorstep.
            float? startGround = atStart ? ApproachGround(on[0], startCenter, startRadius) : null;
            float? endGround = atEnd ? ApproachGround(on[on.Count - 1], endCenter, endRadius) : null;
            // Something this road needed was not prepared. Building it anyway
            // would put it through a footprint nothing could see, or land it on a
            // height nothing could read. Leaving the destination unconnected is
            // the recoverable half of that choice.
            if (LocationLevelling.PreparationMissedHere) { result.Stop = PlanStop.Preparation; return false; }
            RoadSpatialGrid.LastRefusal = null;
            // An end at its place that does not come level with it is where an approach staircase may be laid up from
            // (SiteApproaches, once the road is planned): no tight switchback narrows the road into it (RoadSpatialGrid.StairStarts).
            bool ApproachFrom(bool at, bool placed, float? ground, Vector2 centre, float radius) =>
                at && !placed && ground == null && radius > 0f && ApproachStairsAt(centre, PrefabAt(centre));
            result.Plans = PlanRoadPathWithCrossings(on, result.Crossings, width, startGround, endGround, stairs,
                (ApproachFrom(atStart, startPlaced, startGround, startCenter, startRadius), ApproachFrom(atEnd, endPlaced, endGround, endCenter, endRadius)));
            // A staircase across the road as planned at either end (RoadStairRuns.SpanAcrossItsRoad): its stretch closed to stairs
            // and the road searched again, as for one that could not be laid.
            if (result.Plans != null)
                foreach (RoadStairSpan stair in stairs)
                {
                    if (stair.Existing || RoadStairRuns.SpanAcrossItsRoad(stair, result.Plans, out string piece) is not string end) continue;
                    RoadStairRuns.Refuse(stair, end, piece);
                    result.Plans = null;
                    result.Stop = PlanStop.Staircase;
                    return false;
                }
            return true;
        }
        // Over a hump the search climbed on stairs and came down again close by, the road straight across it on earthwork
        // first: both staircases dropped where that plans within the caps and meets the network as any road must.
        if (AcrossTheHumps(path, startAtPlace, endAtPlace)) return result;
        if (!Plan(path, startAtPlace, endAtPlace)) return result;
        // Dropping the stubs is cosmetic: a road refused without them is
        // planned with them. With both ends at places other roads reach,
        // the 40-60 m connector left between two parts of the network can
        // be too steep where the full road is not.
        if (result.Plans == null && !ReferenceEquals(withStubs, path)) Plan(withStubs, true, true);
        return result;

        // Each crest pair whose ends stand within RoadStairRuns.HumpReach (RoadStairRuns.Humps), last first: its stretch
        // laid as a straight road between its two ends, its stair steps dropped, and the road planned so. Kept where it
        // plans, its earthwork within the cap, no trench or ridge (RoadRidges) and no crossing, and it meets the network as any
        // road must (MeetingRefusal) near the stretch; a hump the caps, a trench or the network refuses stays stairs, and with no cap none is tried. True where the
        // road is planned so.
        bool AcrossTheHumps(List<Vector2> on, bool atStart, bool atEnd)
        {
            // Only within an earthwork cap: with none (0), nothing bounds the cut a hump's road would take.
            if (!EarthworkCapped || finder.LastStairSteps.Count == 0 || GenerationWorld == null) return false;
            List<(int from, int to)> humps = RoadStairRuns.Humps(on, finder.LastStairSteps, GenerationWorld);
            if (humps.Count == 0) return false;
            IReadOnlyList<(Vector2 from, Vector2 to)> steps = finder.LastStairSteps;
            bool across = false;
            for (int k = humps.Count - 1; k >= 0; k--)
            {
                var (from, to) = humps[k];
                Vector2 a = on[from], b = on[to];
                var straight = new List<Vector2>(on.GetRange(0, from + 1));
                int cells = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / RoadPathfinder.CellSize));
                for (int c = 1; c < cells; c++) straight.Add(Vector2.Lerp(a, b, c / (float)cells));
                straight.AddRange(on.GetRange(to, on.Count - to));
                var kept = RoadStairRuns.StepsOff(on, from, to, steps);
                if (!Plan(straight, atStart, atEnd, kept) || result.Plans == null) continue;
                bool Near(Vector2 p)
                {
                    Vector2 ab = b - a;
                    float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
                    return Vector2.Distance(p, a + ab * t) <= HumpCheckReach;
                }
                var past = new List<Vector2>();
                PastTerrainLimit(result.Plans, EarthworkLimit, past);
                if (past.Exists(Near)
                    || RoadRidges.Find(result.Plans, NaturalGround).Exists(st => st.Points.Exists(Near))
                    || result.Crossings.Exists(c => c.FromIndex <= from + cells && c.ToIndex >= from)
                    || MeetingRefusal(result.Plans, out Vector2 meets) != null && Near(meets)) continue;
                (on, steps, across) = (straight, kept, true);
                System.Threading.Interlocked.Increment(ref m_humpsLevelled);
                if (RoadsLog) Log.LogDebug(FormattableString.Invariant($"[STAIRS] hump between ({a.x:F1}, {a.y:F1}) and ({b.x:F1}, {b.y:F1}): laid as road on earthwork, both staircases dropped"));
            }
            return across && Plan(on, atStart, atEnd, steps) && result.Plans != null;
        }
    }

    /// <summary>How far from a hump's straight road its plan is read for earthwork past the cap and the network it meets
    /// (PlanFromPath): the stretch and the blend either side.</summary>
    private const float HumpCheckReach = 16f;

    private static int m_humpsLevelled;
    /// <summary>Crest pairs planned as road straight across their hump this generation, built or not (PlanFromPath).</summary>
    public static int HumpsLevelled => m_humpsLevelled;

    /// <summary>Plans every piece of the road and stores them; nothing is
    /// stored if any piece is refused.</summary>
    private static bool AddRoadPathWithCrossings(List<Vector2> path, List<RoadCrossing> crossings, float width,
        float? startGround, float? endGround)
    {
        var plans = PlanRoadPathWithCrossings(path, crossings, width, startGround, endGround);
        if (plans == null) return false;
        foreach (var plan in plans) RoadSpatialGrid.Commit(plan);
        return true;
    }

    internal static List<RoadSpatialGrid.PlannedPath>? PlanRoadPathWithCrossings(
        List<Vector2> path, List<RoadCrossing> crossings, float width, float? startGround, float? endGround,
        List<RoadStairSpan>? stairs = null, (bool start, bool end) approaches = default)
    {
        using var fitting = new RoadFittingActivity(IslandActivity);
        var plans = PlanPieces(path, crossings, width, startGround, endGround, stairs, approaches);
        // A shallow ford is a style, never a reason a road cannot be built: its banks hold their
        // natural heights and split the road, and a short piece between two held ends can be too
        // steep for the cap (measured: a road refused at a 17 m piece 7 m high between a pool's
        // bank and a turn). Plan again with the valid pools walked;
        // the dry-land floor raises them. The crossings list is updated in place for the caller.
        if (plans == null && crossings.Exists(c => c.Shallow && !c.Invalid))
        {
            var walked = crossings.FindAll(c => !(c.Shallow && !c.Invalid));
            var retry = PlanPieces(path, walked, width, startGround, endGround, stairs, approaches);
            if (retry != null)
            {
                System.Threading.Interlocked.Increment(ref m_shallowFordsWalked);
                crossings.Clear(); crossings.AddRange(walked);
                return retry;
            }
        }
        return plans;
    }

    private static int m_shallowFordsWalked;
    /// <summary>The crossings of a finished path: its river crossings (RoadCrossingDetector) and
    /// the Mistlands viaducts its search bought over dry valleys (DvergrViaduct.AddJumps), in path
    /// order.</summary>
    private static List<RoadCrossing> DetectCrossings(RoadPathfinder finder, List<Vector2> path)
    {
        List<RoadCrossing> found = finder.Fords || finder.Bridges
            ? RoadCrossingDetector.Detect(path, GenerationWorld, finder.Bridges, finder.Fords)
            : new List<RoadCrossing>();
        if (finder.Viaducts && finder.Bridges)
            DvergrViaduct.AddJumps(path, finder.LastViaductJumps, GenerationWorld, found);
        return found;
    }

    /// <summary>
    /// Every crossing's ends met on <paramref name="path"/> (StructureEnds.MeetEnds, the one owner, every kind alike), the
    /// road brought to each end's foot; the first crossing with an end no way of meeting reaches, with the search's jump it
    /// lies on, or null. A stair step the search bought is never cut; each end's landing is within the earthwork cap.
    /// </summary>
    private static RoadCrossing? MeetEnds(RoadPathfinder finder, List<Vector2> path, List<RoadCrossing> crossings)
    {
        var steps = finder.LastStairSteps;
        bool IsStep(Vector2 a, Vector2 b)
        {
            foreach (var (from, to) in steps)
                if ((Vector2.Distance(a, from) <= 1f && Vector2.Distance(b, to) <= 1f) || (Vector2.Distance(a, to) <= 1f && Vector2.Distance(b, from) <= 1f))
                    return true;
            return false;
        }
        return StructureEnds.MeetEnds(path, crossings, GenerationWorld, EarthworkLimit, IsStep);
    }

    /// <summary>The moves of the search's <paramref name="path"/> that <paramref name="c"/> lies on, from its
    /// <see cref="RoadCrossing.FromIndex"/> to its <see cref="RoadCrossing.ToIndex"/>: each step of a ford walked cell by
    /// cell, of a bank walked out to dry ground or moved back to a higher one, and the jump of a bridge or viaduct.</summary>
    internal static List<(Vector2 from, Vector2 to)> SearchedMoves(List<Vector2> path, RoadCrossing c)
    {
        var moves = new List<(Vector2 from, Vector2 to)>();
        int from = Mathf.Clamp(c.FromIndex, 0, path.Count - 1), to = Mathf.Clamp(c.ToIndex, 0, path.Count - 1);
        for (int i = from; i < to; i++) moves.Add((path[i], path[i + 1]));
        if (moves.Count == 0) moves.Add((c.FromBank, c.ToBank));
        return moves;
    }

    /// <summary>
    /// A staircase joins the network: its earthwork goes in with the road's (at a place, only outside every footprint: the
    /// terrain writer leaves the place's own ground alone, and its clearance keeps the place's own rocks), its pieces are a
    /// structure source's, standing from now on for later roads. The one way a road's, an entry's and an approach's
    /// staircase is laid. <paramref name="fromMeet"/> and <paramref name="toMeet"/>: where the road meets each end, where that is
    /// not the end recorded (a road's own staircase, recorded at the path points it climbs: its span's ends);
    /// <paramref name="expectedPlace"/>: an approach's place, laid out over the ground it is expected to leave.
    /// </summary>
    private static void JoinNetwork(Vector2 from, Vector2 to, StairPlan plan, StairKit kit, bool atPlace, Vector2? fromMeet = null, Vector2? toMeet = null,
        (Vector2 Centre, float Ground)? expectedPlace = null)
    {
        RoadSpatialGrid.CommitCuts(CutsOf(plan, atPlace), StairLayout.CutWidth);
        AddToNetwork(RunOf(from, to, plan, kit, atPlace, fromMeet, toMeet, expectedPlace), plan, kit);
    }

    /// <summary>A staircase's earthwork as it is laid (<see cref="JoinNetwork"/>, and a manual road's append): at a place, only
    /// outside every footprint.</summary>
    internal static IReadOnlyList<Vector3> CutsOf(StairPlan plan, bool atPlace) =>
        atPlace ? RoadStairRuns.CutsOutsideFootprints(plan) : plan.Cuts;

    /// <summary>A staircase's run as it is laid (<see cref="JoinNetwork"/>, and a manual road's append).</summary>
    internal static StairRun RunOf(Vector2 from, Vector2 to, StairPlan plan, StairKit kit, bool atPlace, Vector2? fromMeet = null,
        Vector2? toMeet = null, (Vector2 Centre, float Ground)? expectedPlace = null)
    {
        var run = StairRun.FromPlan(from, to, RoadStairRuns.Width, plan, kit);
        run.KeepsSiteRocks = atPlace;
        run.ExpectedPlace = expectedPlace;
        run.FromMeet = fromMeet;
        // At a place, where its ground meets a floor laid out onto it from the foot (StairPlan.PlaceMeet): the place's end.
        run.ToMeet = toMeet ?? (atPlace ? plan.PlaceMeet : null);
        return run;
    }

    /// <summary>A laid staircase's run becomes the network's: a structure source's pieces, standing from now on for later
    /// roads, its layout committed. <see cref="JoinNetwork"/>'s second half, and a manual road's, whose cuts go in its own
    /// append.</summary>
    private static void AddToNetwork(StairRun run, StairPlan plan, StairKit kit)
    {
        StairStructures.Add(run);
        StairStructures.MarkStanding(run);
        RoadStairRuns.Laid(run, plan, kit);
        if (RoadsLog)
        {
            System.Threading.Interlocked.Increment(ref m_stairRuns);
            t_stairRunsLaidHere++;
            // One line per end as laid, read on the ground its layout was decided on (StairEnds, the one end reader).
            foreach (StairEnds.End end in StairEnds.Read(run, RoadStairRuns.PlanningGround(run, GenerationWorld), StairStructures.MetAtEnds(run)))
                Log.LogInfo(StairEnds.Line(end) + " (as laid)");
        }
    }

    /// <summary>Every staircase end of the network as generated, read on its finished ground (StairEnds.AuditAll): the counts,
    /// and a line for each end with no way off it. The one invariant every gate reads ("ends: N ok, M miss, K gap"); and
    /// beside it the network's twin ways into places ("twins: N").</summary>
    private static void LogStairEnds()
    {
        var world = GenerationWorld;
        var (lines, ok, miss, gap) = StairEnds.AuditAll(run => StairEnds.GroundOf(run, world));
        Log.LogInfo($"  Staircase ends (StairEnds, on the finished ground): ends: {ok} ok, {miss} miss, {gap} gap; approaches laid with a bare top, no layout with a way off it: {RoadStairRuns.BareTopsKept}");
        foreach (var (verdict, line) in lines)
            if (verdict != StairEnds.Verdict.Ok) Log.LogInfo("  " + line);
        // Beside it, the ways into places (PlaceWaysIn.Twins, the rule's own test over the whole network): no place has two
        // ways in arriving the same way ("twins: N", each named with its place and both ways' ends).
        var twins = PlaceWaysIn.Twins();
        Log.LogInfo($"  Ways into places (PlaceWaysIn, the whole network): twins: {twins.Count}");
        foreach (var twin in twins) Log.LogInfo("  " + twin.line);
    }

    /// <summary>Whether a crossing is built as a Dvergr viaduct (BridgeStructures.IsViaduct, the one owner; the kind stored
    /// with it once the road is recorded).</summary>
    private static bool IsViaduct(RoadCrossing c) => BridgeStructures.IsViaduct(c, GenerationWorld);

    /// <summary>A crossing with a structure at each end the road is held to (StructureEnds.Of): a viaduct, a bridge or a
    /// ford's spanned footbridge in any kit. The road ends on each end's foot at its height, come along its line or from its
    /// landing pad, as the one owner decided (StructureEnds.MeetEnds). A waded or raised ford is road.</summary>
    internal static bool MeetsTheRoad(RoadCrossing c) => BridgeStructures.Ends(c, GenerationWorld).near.IsSet;

    /// <summary>
    /// The final check of a road's structures, on the road as BUILT: the road this road plans together
    /// with the network laid before it. Judging only the ends of this road's own planned pieces passed viaducts with road
    /// under their deck ends (another road, and the 3 m every bridge's road was painted in under its deck at the
    /// ground's height after the check), and a floor raised by a road's fill. The first
    /// crossing on the road that fails, with where and why, or null:
    /// <list type="bullet">
    /// <item>road under its deck (StructureEnds.UnderTheDeck), this road's or the network's;</item>
    /// <item>a valley viaduct under the floor over the finished ground (UnderTheFloor).</item>
    /// </list>
    /// </summary>
    internal static RoadCrossing? BuiltRefusal(List<RoadSpatialGrid.PlannedPath> plans, List<RoadCrossing> crossings, out Vector2 where, out string why)
    {
        // A valley viaduct's failure first, wherever it is on the road: it is the one a re-route can drop.
        RoadCrossing? first = null;
        why = "";
        where = default;
        foreach (RoadCrossing c in crossings)
        {
            if (!MeetsTheRoad(c) || c.Shared || (first != null && !c.ValleyJump)) continue;
            var (near, far) = BridgeStructures.Ends(c, GenerationWorld);
            float deck = BridgeStructures.DeckHeight(c, GenerationWorld), half = BridgeStructures.DeckHalfWidth(c, GenerationWorld);
            foreach (var (p, h, w) in StructureEnds.BuiltRoadNear(c.Center, c.Width * 0.5f + half + 4f, plans))
                if (StructureEnds.UnderTheDeck(p, h, w, near, far, deck, half))
                {
                    where = p; why = FormattableString.Invariant($"road under the deck at ({p.x:F1}, {p.y:F1}), {deck - h:F1} m below it");
                    if (c.ValleyJump) return c;
                    first = c;
                    break;
                }
        }
        if (UnderTheFloor(plans, crossings) is RoadCrossing low) { where = low.Center; why = "a pier under the floor over the finished ground"; return low; }
        return first;
    }

    /// <summary>
    /// Whether a road, as planned, runs under the deck of a viaduct laid before it (StructureEnds.UnderTheDeck): the other
    /// half of the built-result check. The search keeps out from under a deck (DvergrViaduct.Standing) but for its two bank
    /// cells, where a deck's end overhangs; a road through there is refused.
    /// </summary>
    internal static string? UnderALaidDeck(List<RoadSpatialGrid.PlannedPath> plans, out Vector2 at)
    {
        at = default;
        List<(StructureEnds.End near, StructureEnds.End far, float deck, Vector2 centre, float reach, byte island)> decks;
        lock (m_laidDecks) { if (m_laidDecks.Count == 0) return null; decks = new(m_laidDecks); }
        int me = CrossIslandReads.Me;
        foreach (var d in decks)
            foreach (var (p, h, w) in PlanPointsNear(plans, d.centre, d.reach))
                if (StructureEnds.UnderTheDeck(p, h, w, d.near, d.far, d.deck, DvergrViaduct.HalfWidth))
                {
                    // Another island's viaduct is not this island's network (CrossIslandReads).
                    if (!CrossIslandReads.Sees(me, d.island)) { CrossIslandReads.Skipped(CrossIslandReads.Kind.Viaducts, d.island, p); break; }
                    at = p; return "passes under a viaduct's deck";
                }
        return null;
    }

    private static IEnumerable<(Vector2 p, float h, float w)> PlanPointsNear(List<RoadSpatialGrid.PlannedPath> plans, Vector2 at, float reach)
    {
        float r2 = reach * reach;
        foreach (var plan in plans)
            for (int i = 0; i < plan.Points.Count; i++)
                if ((plan.Points[i] - at).sqrMagnitude <= r2) yield return (plan.Points[i], plan.Heights[i], plan.Width);
    }

    /// <summary>
    /// What later roads keep clear of, for each viaduct a road laid (a shared one is the other road's): its ends and
    /// clearance (DvergrViaduct.MarkLaid), its deck (<see cref="UnderALaidDeck"/>) and, for a valley's, the cells under it
    /// (DvergrViaduct.MarkStanding). A generated road and a manual one alike.
    /// </summary>
    private static void KeepClearOfViaducts(IEnumerable<RoadCrossing> crossings, string? label)
    {
        foreach (var c in crossings)
            if (!c.Shared && IsViaduct(c))
            {
                DvergrViaduct.MarkLaid(c);
                var (near, far) = DvergrViaduct.Ends(c, GenerationWorld);
                lock (m_laidDecks) m_laidDecks.Add((near, far, DvergrViaduct.DeckHeight(c, GenerationWorld), c.Center, c.Width * 0.5f + DvergrViaduct.HalfWidth + 4f, CrossIslandReads.Tag));
                IslandNetworkChanged();
            }
        foreach (var c in crossings)
        {
            if (!c.ValleyJump || c.Shared) continue;
            DvergrViaduct.MarkStanding(c);
            System.Threading.Interlocked.Increment(ref m_viaductsLaid);
            if (RoadsLog) Log.LogInfo($"[VIADUCT] road {label ?? "(unlabelled)"}: {DvergrViaduct.Describe(c, GenerationWorld)}");
        }
    }

    /// <summary>The viaducts laid this generation, their ends and deck, for <see cref="UnderALaidDeck"/>.</summary>
    private static readonly List<(StructureEnds.End near, StructureEnds.End far, float deck, Vector2 centre, float reach, byte island)> m_laidDecks = new();

    /// <summary>A valley viaduct on the road whose tallest pier over the finished ground (the road built under it, this
    /// road's plans and the network so far) is under the floor (DvergrViaduct.MinViaductPier), or null.</summary>
    internal static RoadCrossing? UnderTheFloor(List<RoadSpatialGrid.PlannedPath> plans, List<RoadCrossing> crossings)
    {
        foreach (RoadCrossing c in crossings)
        {
            if (!c.ValleyJump || c.Shared || !IsViaduct(c)) continue;
            if (DvergrViaduct.TallestPierOnFinishedGround(c, GenerationWorld, plans) < DvergrViaduct.MinViaductPier - 1e-3f) return c;
        }
        return null;
    }

    private static int m_viaductsRefusedAtEnds, m_crossingsUnmetSearchedRound, m_bridgeEndsUnmet, m_endEarthworks, m_endPads, m_endShared, m_endStands, m_endSteps, m_endFlat, m_endOneStep, m_endMoreSteps;
    /// <summary>Structure ends laid the road leaves from their landing pad, off their line (StructureEnds.PadLength).</summary>
    public static int EndPads => m_endPads;
    /// <summary>Structure ends laid on a landing shared with the facing end of the next structure (StructureEnds.Share).</summary>
    public static int EndShared => m_endShared;
    /// <summary>Structure ends laid where the road's last point stands, no point of it clear of the end
    /// (StructureEnds.Approach).</summary>
    public static int EndStands => m_endStands;
    /// <summary>Stair steps laid at structure ends.</summary>
    public static int EndSteps => m_endSteps;
    /// <summary>Structure ends laid with no stairs, with one step, and with more.</summary>
    public static (int none, int one, int more) EndsBySteps => (m_endFlat, m_endOneStep, m_endMoreSteps);
    /// <summary>Structure ends laid met by earthwork (StructureEnds.MeetEnds): their landing filled over ground under the
    /// road's floor.</summary>
    public static int EndEarthworks => m_endEarthworks;
    /// <summary>Road searches made again without a valley viaduct the built road did not allow (or refused with it).</summary>
    public static int ViaductsRefusedAtEnds => m_viaductsRefusedAtEnds;
    /// <summary>Road searches made again round a crossing with an end no way of meeting reaches (StructureEnds.MeetEnds).</summary>
    public static int CrossingsUnmetSearchedRound => m_crossingsUnmetSearchedRound;
    /// <summary>Road attempts refused with a crossing with an end no way of meeting reaches, its searches round it spent.</summary>
    public static int BridgeEndsUnmet => m_bridgeEndsUnmet;
    /// <summary>Searches again round a crossing, past the staircase retries.</summary>
    internal const int CrossingRetries = 3;
    /// <summary>Told of every road piece a generated road stores, with the road's label: for offline maps and the
    /// viaduct tests. Called from island workers.</summary>
    public static Action<RoadSpatialGrid.PlannedPath, string?>? PieceCommitted;
    private static int m_viaductsLaid;
    /// <summary>Mistlands viaducts the roads of this generation laid over dry valleys.</summary>
    public static int ViaductsLaid => m_viaductsLaid;


    /// <summary>The viaduct the last plan on this thread was refused with for a stub of road between it and another
    /// structure (StructureEnds.IsStub), or null.</summary>
    [System.ThreadStatic] internal static RoadCrossing? LastStubBetween;

    /// <summary>Land pieces stand no lower than <see cref="RoadConstants.DryRoadFloor"/>.</summary>
    private static float LandFloor => RoadConstants.DryRoadFloor;

    /// <summary>A staircase's end this far or further from the junction its road ends at is met by road laid to it.</summary>
    internal const float StairEndReach = 0.75f;

    /// <param name="approaches">Whether an approach staircase may be laid up from the road's own start, and its end
    /// (SiteApproaches): those ends, as every staircase's foot the road is held at, are where a staircase starts
    /// (RoadSpatialGrid.StairStarts).</param>
    private static List<RoadSpatialGrid.PlannedPath>? PlanPieces(
        List<Vector2> path, List<RoadCrossing> crossings, float width, float? startGround, float? endGround,
        List<RoadStairSpan>? stairs = null, (bool start, bool end) approaches = default)
    {
        // Every piece is worked out before any of it is stored. A piece can be
        // refused - too steep to build - and a road stored up to the river it
        // cannot come back from is a paved stretch ending in open ground.
        // A piece's ends may be held at a height: the road's own ends where they
        // meet a place, and the ends of a staircase, which the road meets level.
        var pieces = new List<(List<Vector2> points, bool followTerrain, float minHeight, float? startHeight, float? endHeight)>();
        var between = new List<(List<Vector2> points, float from, float to, RoadCrossing? fromCrossing, RoadCrossing? toCrossing)>();
        // A stretch of road to or from a structure's end is planned as two pieces, split where its levelled stretch starts
        // (StructureEnds.ApronLength along the road from the foot, the joining piece beyond it): the road up to it, held there
        // at the meeting height, and the levelled stretch itself (the stairs' rule: the road runs to the structure at its
        // height). One piece, the grade limiter spread the climb to a rim into the apron and tilted it (most viaduct ends a
        // little off level, seven a step or more).
        void AddLand(List<Vector2> land, float? startH, float? endH, bool startEnd, bool endEnd)
        {
            float landFloor = LandFloor;
            int i0 = startEnd && startH.HasValue ? SplitAt(land, fromEnd: false) : -1;
            int i1 = endEnd && endH.HasValue ? SplitAt(land, fromEnd: true) : -1;
            if (i0 >= 0 && i1 >= 0 && i1 <= i0) i0 = i1 = -1;
            int last = land.Count - 1, from = 0, to = last;
            if (i0 > 0 && i0 < last) { pieces.Add((land.GetRange(0, i0 + 1), false, landFloor, startH, startH)); from = i0; }
            bool levelTail = i1 > from && i1 < last;
            if (levelTail) to = i1;
            if (to > from) pieces.Add((land.GetRange(from, to - from + 1), false, landFloor, startH, endH));
            if (levelTail) pieces.Add((land.GetRange(i1, last - i1 + 1), false, landFloor, endH, endH));
        }
        // The index in land of the point StructureEnds.ApronLength + JoinOverlap along it from its start or end, put in where
        // no point stands within 0.3 m of it; -1 where the land is no longer.
        static int SplitAt(List<Vector2> land, bool fromEnd)
        {
            float want = StructureEnds.ApronLength + StructureEnds.JoinOverlap, along = 0f;
            for (int n = 1; n < land.Count; n++)
            {
                int a = fromEnd ? land.Count - n : n - 1, b = fromEnd ? a - 1 : a + 1;
                float run = Vector2.Distance(land[a], land[b]);
                if (along + run < want - 0.3f) { along += run; continue; }
                if (along + run <= want + 0.3f) return b;
                Vector2 at = Vector2.Lerp(land[a], land[b], (want - along) / run);
                int insert = fromEnd ? a : b;
                land.Insert(insert, at);
                return insert;
            }
            return -1;
        }
        LastStubBetween = null;

        // What the road is cut at, in path order: river crossings and staircases. Every crossing's ends are the one owner's
        // (StructureEnds.MeetEnds), which brought the path to them: a structure's at its crossing's FromIndex and ToIndex,
        // where the road ends at the end's foot at its height, its last ApronLength levelled to it
        // (RoadSpatialGrid.LevelledEnds), and runs on under the joining piece (StructureEnds.JoinOverlap), unpainted; a
        // crossing that is road at its banks.
        var gaps = new List<(int from, int to, Vector2 fromPoint, Vector2 toPoint, Vector2? fromLand, Vector2? toLand, float? fromHeight, float? toHeight, RoadCrossing? crossing)>();
        var levelled = new List<(Vector2 at, float height)>();
        var ups = new Dictionary<RoadCrossing, (StructureEnds.End near, StructureEnds.End far)>();
        foreach (RoadCrossing c in crossings)
        {
            if (MeetsTheRoad(c))
            {
                var (near, far) = BridgeStructures.Ends(c, GenerationWorld);
                ups[c] = (near, far);
                levelled.Add((near.Joint, near.MeetHeight));
                levelled.Add((far.Joint, far.MeetHeight));
                gaps.Add((c.FromIndex, c.ToIndex, near.Meet, far.Meet, null, null, near.MeetHeight, far.MeetHeight, c));
            }
            else gaps.Add((c.FromIndex, c.ToIndex, c.FromEnd?.Meet ?? c.FromBank, c.ToEnd?.Meet ?? c.ToBank, c.FromLand, c.ToLand, null, null, c));
        }
        if (stairs != null)
            foreach (RoadStairSpan st in stairs)
                gaps.Add((st.FromIndex, st.ToIndex, st.FromPoint, st.ToPoint, null, null, st.FromHeight, st.ToHeight, null));
        // Stable by start: crossings keep the order Detect gave them.
        gaps = gaps.Select((g, i) => (g, i)).OrderBy(x => x.g.from).ThenBy(x => x.i).Select(x => x.g).ToList();

        float landFloor = LandFloor;
        if (gaps.Count == 0)
        {
            pieces.Add((path, false, landFloor, null, null));
        }
        else
        {
            int cursor = 0;
            Vector2? resumeAt = null, resumeLand = null, resumeJoint = null;
            // What a piece runs from and to, where both are structures (a staircase, or a bridge end the owner meets).
            RoadCrossing? resumeCrossing = null;
            StructureEnds.End? resumeEnd = null;
            float? resumeHeight = null;
            // A staircase ends where its layout ended, up to a few metres from the
            // path point it aimed at: the road ends and starts there instead of
            // running on to that point and doubling back (a hairpin was refused).
            bool resumeOnStair = false;
            foreach (var gap in gaps)
            {
                // Two crossings on one road can overlap on the path: a bridge's
                // banks walk out to the bank tops and a swamp bridge's on to dry
                // ground, so one crossing's span can reach past the start of the
                // next. A crossing the previous one already spans has nothing left
                // to paint, and one that merely starts inside it has no land in
                // front of it.
                if (gap.to <= cursor)
                    continue;

                List<Vector2> land = gap.from > cursor
                    ? path.GetRange(cursor, gap.from - cursor + 1)
                    : new List<Vector2>();
                // A staircase starts at its own first point, not at the path point it was laid from: that point is dropped
                // where it is still the stretch's last (never a meeting point or apron the stretch was brought to).
                Vector2? stepFrom = gap.crossing == null && gap.from > cursor ? path[gap.from] : null;
                if (resumeLand.HasValue && (land.Count == 0 || Vector2.Distance(resumeLand.Value, land[0]) > 0.5f))
                    land.Insert(0, resumeLand.Value);
                if (resumeOnStair && land.Count > 1) land.RemoveAt(0);
                if (resumeAt.HasValue && (land.Count == 0 || Vector2.Distance(resumeAt.Value, land[0]) > 0.5f))
                    land.Insert(0, resumeAt.Value);
                if (resumeJoint is Vector2 rj) land.Insert(0, rj);
                // A deck end walked in off land: the road runs on over the strip.
                if (gap.fromLand is Vector2 fromLand && land.Count > 0 && Vector2.Distance(fromLand, land[land.Count - 1]) > 0.5f)
                    land.Add(fromLand);
                if (stepFrom is Vector2 sf && land.Count > 1 && land[land.Count - 1] == sf) land.RemoveAt(land.Count - 1);
                Vector2 endAt = gap.fromPoint;
                float? endHeight = gap.fromHeight;
                if (land.Count > 0 && Vector2.Distance(endAt, land[land.Count - 1]) > 0.5f)
                    land.Add(endAt);
                // On under the joining piece (StructureEnds.JoinOverlap): the road ends under the deck's end module or the
                // bottom step, levelled at the meeting height, so the structure stands on it.
                if (endHeight.HasValue && gap.crossing != null && ups.TryGetValue(gap.crossing, out var joinEnds) && land.Count > 0)
                    land.Add(joinEnds.near.Joint);
                if (land.Count >= 2)
                {
                    RoadCrossing? toCrossing = gap.crossing != null && ups.ContainsKey(gap.crossing) ? gap.crossing : null;
                    if (resumeHeight.HasValue && endHeight.HasValue)
                        between.Add((land, resumeHeight.Value, endHeight.Value, resumeCrossing, toCrossing));
                    AddLand(land, resumeHeight, endHeight, resumeJoint.HasValue, toCrossing != null);
                }

                // A bridge or a spanned ford is left to its pieces: nothing is
                // leveled or painted over the water. Nor on a staircase.
                // A shared crossing is carried by the road that built it: only the land up to its banks is ours.
                RoadCrossing? crossing = gap.crossing;
                if (crossing != null && crossing.Kind == CrossingKind.Ford && crossing.Style != FordStyle.Span && !crossing.Shared)
                {
                    List<Vector2> ford = new() { gap.fromPoint };
                    for (int k = Mathf.Max(crossing.FromIndex + 1, cursor + 1); k < crossing.ToIndex; k++)
                        ford.Add(path[k]);
                    ford.Add(gap.toPoint);
                    if (ford.Count >= 2)
                        pieces.Add(crossing.Style == FordStyle.Wade
                            ? (crossing.Shallow
                                ? (ford, false, RoadConstants.SeaLevel - RoadCrossingDetector.ShallowWadeCover, null, null) // built up to wading depth
                                : (ford, true, float.NegativeInfinity, null, null))
                            : (ford, false, crossing.Shallow ? RoadConstants.DryRoadFloor : RoadWater.LandingFloor, null, null));
                }

                resumeAt = gap.toPoint;
                resumeLand = gap.toLand;
                resumeHeight = gap.toHeight;
                resumeCrossing = gap.crossing != null && ups.ContainsKey(gap.crossing) ? gap.crossing : null;
                resumeOnStair = gap.crossing == null;
                resumeEnd = gap.crossing != null && ups.TryGetValue(gap.crossing, out var farEnds) ? farEnds.far : null;
                resumeJoint = resumeEnd?.Joint;
                cursor = gap.to;
            }

            int tailStart = Mathf.Min(cursor, path.Count - 1);
            List<Vector2> tail = path.GetRange(tailStart, path.Count - tailStart);
            // A staircase at the path's last index ends the road there: no stub of road from its end back to the path point
            // (short, possibly steep or doubled back, it could cost the road its stairs). Kept only where it
            // is all the road there is.
            bool endsOnStair = resumeOnStair && tail.Count == 1 && pieces.Count > 0;
            // But where the road ends on another road (a junction) and the staircase's layout ended short of it, the road is
            // laid from the staircase's end to the junction: the staircase meets the road it joins. Laid like any road, it
            // is held to the junction's height (HoldToNetwork).
            if (endsOnStair && resumeAt is Vector2 stairEnd && Vector2.Distance(stairEnd, tail[0]) > StairEndReach
                && RoadSpatialGrid.TryGetRoadHeightWithin(tail[0], RoadSnap, out _))
                endsOnStair = false;
            if (resumeOnStair && tail.Count > 1) tail.RemoveAt(0);
            if (resumeLand.HasValue && Vector2.Distance(resumeLand.Value, tail[0]) > 0.5f)
                tail.Insert(0, resumeLand.Value);
            if (resumeAt.HasValue && Vector2.Distance(resumeAt.Value, tail[0]) > 0.5f)
                tail.Insert(0, resumeAt.Value);
            if (resumeJoint is Vector2 tj) tail.Insert(0, tj);
            if (tail.Count >= 2 && !endsOnStair)
                AddLand(tail, resumeHeight, null, resumeJoint.HasValue, false);
        }

        if (pieces.Count == 0)
            return null;

        // Between two structures (a crossing's end and a staircase, or two crossings' ends), no stub of road. A stretch
        // shorter than StructureEnds.StubLength is one level landing at the joining height: within a step (StepTolerance) of
        // both it is that; further apart the road is not planned so (the stretch is levelled to the crossing's end all the
        // way, and a staircase's foot is always at the road's height where it starts). A crossing this road builds is not
        // built with it, and the road is searched again without it (GenerateRoadCore); another road's is met as it stands.
        // Two staircases of one road keep their own meeting (StairChain).
        foreach (var (points, fromH, toH, from, to) in between)
        {
            RoadCrossing? structure = from ?? to;
            if (structure == null) continue;
            if (StructureEnds.IsStub(points, fromH, toH) is string stub)
            {
                RoadSpatialGrid.LastRefusal = "a stub of road between two structures";
                RoadCrossing? own = from != null && !from.Shared ? from : to != null && !to.Shared ? to : null;
                LastStubBetween = own;
                if (RoadsLog) Log.LogDebug(FormattableString.Invariant($"[BRIDGES] {stub} at ({points[0].x:F1}, {points[0].y:F1}); the crossing {structure.FromBank} -> {structure.ToBank} {(own != null ? "is not built with this road" : "stands: the road is refused")}"));
                return null;
            }
        }

        // Only the road's own two ends meet a location. Every join in between
        // is a river bank, which meets the natural ground as it always did, or
        // the end of a staircase, which the road meets at the stair's height.
        var plans = new List<RoadSpatialGrid.PlannedPath>(pieces.Count);
        System.Func<Vector2, float>? ground = StripGround(crossings, width);
        // Each structure's end the road was brought to (StructureEnds.MeetEnds) stays on it: the point under its joining
        // piece, its foot and its apron, on its line; and its last ApronLength to each is levelled to it.
        List<Vector2>? feet = null;
        var ends = new List<StructureEnds.End>();
        foreach (var (near, far) in ups.Values)
            foreach (StructureEnds.End end in new[] { near, far })
            {
                (feet ??= new List<Vector2>()).AddRange(new[] { end.Joint, end.Meet, end.Meet - end.Up * StructureEnds.PadTurn, end.Meet - end.Up * (StructureEnds.ApronLength * 0.5f), end.Apron });
                ends.Add(end);
            }
        RoadSpatialGrid.HeldPoints = feet;
        RoadSpatialGrid.LevelledEnds = levelled.Count > 0 ? levelled : null;
        // Where a staircase starts: each staircase's foot (its lower end), which a piece is held at.
        var stairFeet = new List<Vector2>();
        if (stairs != null)
            foreach (RoadStairSpan st in stairs) stairFeet.Add(st.FromHeight <= st.ToHeight ? st.FromPoint : st.ToPoint);
        bool AtAFoot(Vector2 p) => stairFeet.Exists(f => (f - p).sqrMagnitude < 0.25f);
        try
        {
            for (int i = 0; i < pieces.Count; i++)
            {
                // The road's own ends held at a place's ground end on a level landing (RoadSpatialGrid.PlaceEnds).
                RoadSpatialGrid.PlaceEnds = (i == 0 && pieces[i].startHeight == null && startGround.HasValue,
                    i == pieces.Count - 1 && pieces[i].endHeight == null && endGround.HasValue);
                // And so does an end held at a staircase's foot, where the landing fits (else within the cap into it); there, and
                // at an end an approach may start from, no tight switchback narrows the road (RoadSpatialGrid.StairStarts).
                var points = pieces[i].points;
                RoadSpatialGrid.StairStarts = (
                    pieces[i].startHeight != null && AtAFoot(points[0]) || i == 0 && pieces[i].startHeight == null && approaches.start,
                    pieces[i].endHeight != null && AtAFoot(points[points.Count - 1]) || i == pieces.Count - 1 && pieces[i].endHeight == null && approaches.end);
                RoadSpatialGrid.PlannedPath? plan = RoadSpatialGrid.PlanRoadPath(
                    pieces[i].points, width, GenerationWorld,
                    pieces[i].startHeight ?? (i == 0 ? startGround : null),
                    pieces[i].endHeight ?? (i == pieces.Count - 1 ? endGround : null),
                    pieces[i].followTerrain, pieces[i].minHeight, ground);
                RoadSpatialGrid.PlaceEnds = default;
                RoadSpatialGrid.StairStarts = default;
                if (plan == null)
                    return null;
                // Past a structure's end (its stairs' foot, or its deck's edge) the road is levelled under it but not painted.
                plan.Unpainted = StructureEnds.Unpainted(plan.Points, ends, width * 0.5f);
                plans.Add(plan);
            }
        }
        finally
        {
            RoadSpatialGrid.HeldPoints = null;
            RoadSpatialGrid.LevelledEnds = null;
            RoadSpatialGrid.PlaceEnds = default;
            RoadSpatialGrid.StairStarts = default;
        }

        return plans;
    }

    /// <summary>
    /// The network's staircases settled on the ground the terrain writer will leave (natural, a placed place's own
    /// levelling, the roads' earthwork and the staircases' cuts): columns carried down where it lies lower than their layout
    /// read, then pieces wholly under it not placed, which the game breaks once it weighs them and drops their resources
    /// (<see cref="BuriedPieces"/>).
    /// Once, after every island is built.
    /// </summary>
    private static void DropBuriedPieces()
    {
        WorldGenerator? world = WorldGenerator.instance;
        if (world == null || StairStructures.Runs.Count == 0) return;
        var byPrefab = RoadsLog ? new SortedDictionary<string, int>(StringComparer.Ordinal) : null;
        int dropped = StairStructures.SettleOnFinishedGround(StructureGround.Vertices(world), byPrefab);
        if (byPrefab != null)
            Log.LogInfo($"  Staircases on the finished ground: pieces wholly under it, not placed: {dropped}; pieces the search left with nothing within reach to hold them: {StairLayout.SupportRefusals}" +
                (byPrefab.Count == 0 ? "" : " (" + string.Join(", ", byPrefab.Select(kv => $"{kv.Value} {kv.Key}")) + ")"));
    }

    /// <summary>
    /// The road from <paramref name="path"/>'s end at a place placed before its roads onto the place's
    /// landing, on its placed ground: a road where one gets there (<see cref="RoadSiteEntry"/>), else, in the Mountains
    /// or at a boss's entrance (<see cref="ApproachStairsAt"/>), a staircase (<see cref="RoadStairRuns.PlanPlacedSiteApproach"/>), carried on from the top row of the
    /// staircase the road ends on where it ends on one. False, with <paramref name="why"/>, where neither does.
    /// <paramref name="farGround"/>: the ground the road's other end was planned to meet (PlanFromPath).
    /// </summary>
    internal static bool PlanEntry(PlacedSite site, bool atStart, List<Vector2> path, List<RoadSpatialGrid.PlannedPath> plans,
        List<RoadStairSpan> stairs, List<RoadCrossing> crossings, float width, out RoadSpatialGrid.PlannedPath? road, out RoadStairSpan? span, out string why,
        float? farGround = null)
    {
        why = "";
        long began = SearchStats.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        int tries = 0;
        try
        {
        // From the road's end, else from further back along its last stretch with the road cut there: the
        // way onto the landing then has more length to climb, and a staircase from there takes the road's
        // place instead of running beside it. From its end alone, 111 roads were refused at their
        // landings, "grade: ends too far apart for the cap" and "stairs: on its road" (5568,3904: 29 times).
        // The way onto the landing is part of the road: it may not cross another road nor run beside one at its height
        // either (MeetingRefusal), and is planned again (as a staircase where a road onto it fails, from further back) where
        // it does. A road that fails on its own is left to the road's own check, which names it. A staircase is held off the
        // road's own earlier stretch too (EntryMeetingRefusal): one that doubles back to end beside it at its height is
        // refused, and is laid from further back along the road, where the road is already at that height.
        bool checkMeeting = MeetingRefusal(plans, out _) == null;
        // The road's own line is built once for every candidate from the same road (OwnRoadOf), when one is first asked.
        Func<RoadSpatialGrid.PlannedPath?, RoadStairSpan?, string?> MeetsFrom(List<RoadSpatialGrid.PlannedPath> withPlans, Vector2 end)
        {
            var own = new Lazy<OwnRoad?>(() => OwnRoadOf(withPlans, atStart), false);
            return (entryRoad, entryStairs) => EntryMeetingRefusal(atStart, withPlans, end, entryRoad, entryStairs, checkMeeting, own);
        }
        // A start refused because the road to its foot is built as a ridge or a trench (RoadStairRuns.RidgeToFoot).
        bool ridged = false;
        foreach (float back in EntryCutBacks)
        {
            List<Vector2> p = path;
            RoadSpatialGrid.PlannedPath piece = atStart ? plans[0] : plans[plans.Count - 1];
            int shift = 0;
            if (back > 0f && !CutBack(atStart, back, path, piece, stairs, crossings, out p, out piece, out shift)) break;
            var cutPlans = new List<RoadSpatialGrid.PlannedPath>(plans);
            cutPlans[atStart ? 0 : plans.Count - 1] = piece;
            tries++;
            Vector2 end = p[atStart ? 0 : p.Count - 1];
            if (!PlanEntryFrom(site, atStart, p, cutPlans, stairs, width, shift, MeetsFrom(cutPlans, end), out road, out span, out string whyHere))
            {
                if (back == 0f) why = whyHere;
                ridged |= whyHere.Contains(RoadStairRuns.RidgeToFootRefusal);
                continue;
            }
            if (back > 0f)
            {
                ApplyCutBack(atStart, path, p, plans, piece, stairs, crossings, shift);
                if (RoadsLog) m_siteApproaches.AddOrUpdate(FormattableString.Invariant($"placed first: onto its landing from {back:F0} m back along its road"), 1, (_, c) => c + 1);
            }
            why = "";
            return true;
        }
        // The road to the foot built as a ridge or a trench: the staircase starts lower, from where the road climbs to it on
        // its ground, the road's heights planned again to meet the ground there (EntryFromLower), as a road staircase's foot
        // is started lower (RoadStairRuns.StartLower).
        if (ridged)
        {
            tries++;
            string lower = EntryFromLower(site, atStart, path, plans, stairs, crossings, width, farGround, MeetsFrom, out road, out span);
            if (lower == "") { why = ""; return true; }
            why += "; from lower: " + lower;
        }
        road = null;
        span = null;
        why += FormattableString.Invariant($" (also from up to {EntryCutBacks[EntryCutBacks.Length - 1]:F0} m back along the road)");
        return false;
        }
        finally { if (SearchStats.Enabled) SearchStats.Record(SearchStats.LandingEntry, why == "", false, tries, began); }
    }

    /// <summary>The ground a placed place's road is judged on (RoadStairRuns.RidgeToFoot, LowerEnd): the place's placed ground,
    /// the natural ground past its grid (<see cref="PlacedSite.Ground"/> already falls back to it).</summary>
    private static Func<Vector2, float> PlacedGround(PlacedSite site) => q => site.Ground(q.x, q.y);

    /// <summary>
    /// The way onto a placed place's landing from lower down the road, where the road to its end is built as a ridge or a
    /// trench (RoadStairRuns.RidgeToFoot): the road's end moved back along it to the nearest point it climbs to on its ground
    /// (RoadStairRuns.LowerEnd, within <see cref="PlacedSiteReach"/>), the road planned again to meet its ground there (its
    /// other end to meet <paramref name="farGround"/>, as it was planned), and the way onto the landing laid from there; taken
    /// (the path, its plans, and the staircases' and crossings' indices moved with it) where it is, its road within the
    /// earthwork cap and held to no ridge. Why not, else "". The new end meets its own ground, not a target: a target holds the
    /// end ramp toward it for its whole length (RoadEndpointRamp.BaseHeight), a level shelf on fill down the slope.
    /// </summary>
    private static string EntryFromLower(PlacedSite site, bool atStart, List<Vector2> path, List<RoadSpatialGrid.PlannedPath> plans,
        List<RoadStairSpan> stairs, List<RoadCrossing> crossings, float width, float? farGround,
        Func<List<RoadSpatialGrid.PlannedPath>, Vector2, Func<RoadSpatialGrid.PlannedPath?, RoadStairSpan?, string?>> meetsFrom,
        out RoadSpatialGrid.PlannedPath? road, out RoadStairSpan? span)
    {
        road = null;
        span = null;
        // The structure nearest the end: the road is not cut back past it.
        int structures = -1;
        foreach (RoadStairSpan s in stairs) structures = atStart ? (structures < 0 ? s.FromIndex : Mathf.Min(structures, s.FromIndex)) : Mathf.Max(structures, s.ToIndex);
        foreach (RoadCrossing c in crossings) structures = atStart ? (structures < 0 ? c.FromIndex : Mathf.Min(structures, c.FromIndex)) : Mathf.Max(structures, c.ToIndex);
        Func<Vector2, float> ground = PlacedGround(site);
        int j = RoadStairRuns.LowerEnd(path, atStart, structures, ground, PlacedSiteReach);
        if (j < 0) return "no point back along the road it climbs to on its ground";
        var lowPath = atStart ? path.GetRange(j, path.Count - j) : path.GetRange(0, j + 1);
        int shift = atStart ? j : 0;
        var lowCrossings = new List<RoadCrossing>(crossings);
        void Shift(int by) { foreach (RoadStairSpan s in stairs) { s.FromIndex -= by; s.ToIndex -= by; } foreach (RoadCrossing c in crossings) { c.FromIndex -= by; c.ToIndex -= by; } }
        Shift(shift);
        List<RoadSpatialGrid.PlannedPath>? lowPlans = PlanRoadPathWithCrossings(lowPath, lowCrossings, width,
            atStart ? null : farGround, atStart ? farGround : null, stairs);
        Shift(-shift);
        if (lowPlans == null) return "the road to it not planned: " + (RoadSpatialGrid.LastRefusal ?? "refused");
        var none = new List<Vector2>();
        if (PastTerrainLimit(lowPlans, EarthworkLimit, none) > 0 || HeldRidges(lowPlans, none) > 0)
            return "the road to it past the earthwork cap or held on a ridge";
        Vector2 end = lowPath[atStart ? 0 : lowPath.Count - 1];
        if (!PlanEntryFrom(site, atStart, lowPath, lowPlans, stairs, width, shift, meetsFrom(lowPlans, end), out road, out span, out string why))
            return why;
        path.Clear(); path.AddRange(lowPath);
        plans.Clear(); plans.AddRange(lowPlans);
        Shift(shift);
        crossings.Clear(); crossings.AddRange(lowCrossings);
        System.Threading.Interlocked.Increment(ref RoadStairRuns.FeetStartedLower);
        if (RoadsLog) m_siteApproaches.AddOrUpdate("placed first: onto its landing from lower down its road, off a ridge to its foot", 1, (_, c) => c + 1);
        return "";
    }

    /// <summary>The road's planned pieces with the way onto a placed place's landing (a road, or a staircase's walked pieces)
    /// on its end at the place, running from <paramref name="end"/>, the road's end, onto the landing: the road as it meets the
    /// network (<see cref="MeetingRefusal"/>).</summary>
    internal static List<RoadSpatialGrid.PlannedPath> WithEntry(bool atStart, List<RoadSpatialGrid.PlannedPath> plans, Vector2 end,
        RoadSpatialGrid.PlannedPath? road, RoadStairSpan? span)
    {
        var points = new List<Vector2>();
        var heights = new List<float>();
        if (road != null) { points.AddRange(road.Points); heights.AddRange(road.Heights); }
        else if (span != null)
        {
            // Its walk: the pieces are laid a row at a time, side by side across the width, so each row (consecutive
            // walked pieces at one height) is one point at its middle, on its walking surface (RoadStairRuns.Surface).
            var row = new List<StructurePiece>();
            void EndRow()
            {
                if (row.Count == 0) return;
                points.Add(new Vector2(row.Average(q => q.Position.x), row.Average(q => q.Position.z)));
                heights.Add(RoadStairRuns.Surface(row[0]));
                row.Clear();
            }
            foreach (StructurePiece piece in StairRun.FromPlan(span.FromPoint, span.ToPoint, RoadStairRuns.Width, span.Plan).Pieces)
            {
                if (piece.Kind != StructurePieceKind.StairStep && piece.Kind != StructurePieceKind.StairFloor) continue;
                if (row.Count > 0 && (Mathf.Abs(piece.Position.y - row[0].Position.y) > 0.05f || piece.Kind != row[0].Kind
                    || Vector2.Distance(new Vector2(piece.Position.x, piece.Position.z), new Vector2(row[0].Position.x, row[0].Position.z)) > RoadStairRuns.Width + 0.5f))
                    EndRow();
                row.Add(piece);
            }
            EndRow();
        }
        var all = new List<RoadSpatialGrid.PlannedPath>(plans);
        if (points.Count == 0) return all;
        // From the road's end onto the landing; before the road where the road starts at the place.
        if ((points[0] - end).sqrMagnitude > (points[points.Count - 1] - end).sqrMagnitude) { points.Reverse(); heights.Reverse(); }
        if (atStart) { points.Reverse(); heights.Reverse(); }
        var entry = new RoadSpatialGrid.PlannedPath(points, heights, points.ConvertAll(_ => default(RoadPointDebugInfo)), RoadWidth, 0f, false);
        if (atStart) all.Insert(0, entry); else all.Add(entry);
        return all;
    }

    /// <summary>How far back along a road's last stretch the way onto a placed place's landing may start,
    /// the road cut there, tried in turn (<see cref="PlanEntry"/>); the first is the road's own end. Settable for tests.</summary>
    internal static float[] EntryCutBacks = { 0f, 12f, 24f };

    /// <summary>The stair layout's allowance for a way onto a placed place's landing, per landing and start
    /// tried: a road's staircase's (<see cref="RoadStairRuns.GenerationTotalExpansions"/>).</summary>
    internal static int EntryTotalExpansions = RoadStairRuns.GenerationTotalExpansions, EntryStall = RoadStairRuns.GenerationStall;

    /// <summary>Starts within a cell of this size (and a metre of height) lay the same staircase onto a landing.</summary>
    private const float EntryStartCell = 2f;
    /// <summary>How many starts' staircases onto one placed place's landing are laid out before no more are. Settable for tests.</summary>
    internal static int EntryStairsPerPlace = 4;
    /// <summary>Staircases onto placed places' landings laid out this generation (tests).</summary>
    internal static int EntryStairLayouts;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), int> m_entryStairsTried = new();
    /// <summary>Staircases onto placed places' landings that could not be laid, by place, start and height:
    /// read and written by the island worker that owns the place (TryGetValue and the indexer only).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<((int, int) place, int x, int z, int h, bool onTop), string> m_entryStairsFailed = new();

    /// <summary>How far from where a road ends a placed place's landing may be for the road to be carried on
    /// onto it: from the ring round a Dvergr entrance (41 m out, its landing 24 m) anywhere but its back. A
    /// landing facing the sea is reached along the shore from the side or behind (at -6016,4736: water
    /// 6-14 m out to the N, NE, E and SE of its landing, dry ground only to the S, SW and W; refused "too far"
    /// at the old 40 m, <see cref="RoadStairRuns.SiteReach"/>).</summary>
    public const float PlacedSiteReach = 64f;

    /// <summary>
    /// The road with its end at a place pulled <paramref name="back"/> metres back along its last planned
    /// stretch: that stretch cut at the planned point there, and the waypoints past it replaced by it.
    /// False where the stretch is too short, or a staircase or a river crossing lies in the part cut away.
    /// <paramref name="shift"/> is how many waypoints the start lost: everything indexed along the path (its
    /// staircases and its crossings) moves down by it when the cut is taken (<see cref="ApplyCutBack"/>). The
    /// one owner of cutting a road back: the crossings' indices were not moved with the staircases', and the
    /// network hints then read stale crossing ranges (a hint on a bridge or ford waypoint).
    /// </summary>
    internal static bool CutBack(bool atStart, float back, List<Vector2> path, RoadSpatialGrid.PlannedPath piece,
        List<RoadStairSpan> stairs, List<RoadCrossing> crossings, out List<Vector2> cutPath, out RoadSpatialGrid.PlannedPath cutPiece, out int shift)
    {
        cutPath = path;
        cutPiece = piece;
        shift = 0;
        int n = piece.Points.Count;
        if (n < 3 || path.Count < 2) return false;
        // The planned point `back` metres in from the place's end, leaving at least a few metres of stretch.
        int k = -1;
        float walked = 0f;
        for (int i = 1; i < n - 1; i++)
        {
            int a = atStart ? i - 1 : n - i, b2 = atStart ? i : n - 1 - i;
            walked += Vector2.Distance(piece.Points[a], piece.Points[b2]);
            if (walked >= back) { k = b2; break; }
        }
        if (k < 0) return false;
        int from = atStart ? k : 0, count = atStart ? n - k : k + 1;
        float rest = 0f;
        for (int i = from + 1; i < from + count; i++) rest += Vector2.Distance(piece.Points[i - 1], piece.Points[i]);
        if (rest < 4f) return false;
        Vector2 cut = piece.Points[k];
        // The waypoints within `back` of the place's end go; the cut point takes their place.
        int drop = 0;
        float along = 0f;
        for (int i = 1; i < path.Count - 1; i++)
        {
            int a = atStart ? i - 1 : path.Count - i, b2 = atStart ? i : path.Count - 1 - i;
            along += Vector2.Distance(path[a], path[b2]);
            if (along >= back) break;
            drop = i;
        }
        int dropped = drop + 1;
        foreach (RoadStairSpan s in stairs)
            if (atStart ? s.FromIndex < dropped : s.ToIndex > path.Count - 1 - dropped) return false;
        foreach (RoadCrossing c in crossings)
            if (atStart ? c.FromIndex < dropped : c.ToIndex > path.Count - 1 - dropped) return false;
        var trimmed = atStart ? path.GetRange(dropped, path.Count - dropped) : path.GetRange(0, path.Count - dropped);
        if (atStart) trimmed.Insert(0, cut); else trimmed.Add(cut);
        if (trimmed.Count < 2) return false;
        shift = atStart ? dropped - 1 : 0;
        var points = piece.Points.GetRange(from, count);
        cutPiece = new RoadSpatialGrid.PlannedPath(points, piece.Heights.GetRange(from, count), piece.DebugInfos.GetRange(from, count),
            piece.Width, rest, piece.FollowTerrain)
        {
            Widths = piece.Widths?.GetRange(from, count),
            Junctions = piece.Junctions?.FindAll(j => points.Exists(q => (q - j.At).sqrMagnitude <= 1f)),
            Unpainted = piece.Unpainted?.GetRange(from, count),
        };
        cutPath = trimmed;
        return true;
    }

    /// <summary>
    /// Where a road ends at one of its ends, and at what height: its last road point, or, where its last stretch is a
    /// staircase, that staircase's end (an approach or an entry carries on from there). Null where neither is at the end.
    /// <paramref name="shift"/>: how many path points were cut back from the start since the staircases were laid.
    /// </summary>
    private static (Vector2 end, float height, RoadStairSpan? onStairs)? RoadEnd(bool atStart, List<Vector2> path,
        List<RoadSpatialGrid.PlannedPath> plans, List<RoadStairSpan> stairs, int shift = 0)
    {
        Vector2 end = path[atStart ? 0 : path.Count - 1];
        RoadSpatialGrid.PlannedPath piece = atStart ? plans[0] : plans[plans.Count - 1];
        int at = atStart ? 0 : piece.Points.Count - 1;
        if (piece.Points.Count > 0 && Vector2.Distance(piece.Points[at], end) <= 0.5f) return (end, piece.Heights[at], null);
        RoadStairSpan? onStairs = stairs.Find(s => atStart ? s.FromIndex - shift == 0 : s.ToIndex == path.Count - 1);
        if (onStairs == null) return null;
        return atStart ? (onStairs.FromPoint, onStairs.FromHeight, onStairs) : (onStairs.ToPoint, onStairs.ToHeight, onStairs);
    }

    /// <summary>Take a cut made by <see cref="CutBack"/>: the path and its end piece replaced, and the staircases' and the
    /// crossings' indices moved down by <paramref name="shift"/>.</summary>
    internal static void ApplyCutBack(bool atStart, List<Vector2> path, List<Vector2> cutPath, List<RoadSpatialGrid.PlannedPath> plans,
        RoadSpatialGrid.PlannedPath cutPiece, List<RoadStairSpan> stairs, List<RoadCrossing> crossings, int shift)
    {
        path.Clear(); path.AddRange(cutPath);
        plans[atStart ? 0 : plans.Count - 1] = cutPiece;
        foreach (RoadStairSpan s in stairs) { s.FromIndex -= shift; s.ToIndex -= shift; }
        foreach (RoadCrossing c in crossings) { c.FromIndex -= shift; c.ToIndex -= shift; }
    }

    /// <param name="meets">Why the road with this way onto the landing would not meet the network as the rules allow, or null
    /// (<see cref="PlanEntry"/>): a road onto it that fails gives way to a staircase, a staircase that fails is not laid out
    /// again from the same start.</param>
    private static bool PlanEntryFrom(PlacedSite site, bool atStart, List<Vector2> path, List<RoadSpatialGrid.PlannedPath> plans,
        List<RoadStairSpan> stairs, float width, int shift, Func<RoadSpatialGrid.PlannedPath?, RoadStairSpan?, string?> meets,
        out RoadSpatialGrid.PlannedPath? road, out RoadStairSpan? span, out string why)
    {
        road = null;
        span = null;
        var world = GenerationWorld;
        int index = atStart ? 0 : path.Count - 1;
        if (RoadEnd(atStart, path, plans, stairs, shift) is not var (end, height, onStairs)) { why = "no road at the end"; return false; }
        var tail = new List<Vector2>();
        if (atStart) for (int i = Mathf.Min(SiteApproachRequest.TailPoints, path.Count - 1); i >= 0; i--) tail.Add(path[i]);
        else for (int i = Mathf.Max(0, path.Count - 1 - SiteApproachRequest.TailPoints); i < path.Count; i++) tail.Add(path[i]);
        tail[tail.Count - 1] = end;
        var request = new SiteApproachRequest
        {
            RoadEnd = end, RoadHeight = height, Arriving = RoadStairRuns.ArrivingTravel(path, atStart ? plans[0] : plans[plans.Count - 1], end, atStart), Centre = site.Centre, RoadTail = tail,
            Reach = PlacedSiteReach, TotalExpansions = EntryTotalExpansions, Stall = EntryStall,
            Leg = RoadStairRuns.GenerationExpansions,
        };
        PlacedSiteRecord r = site.Record;
        string noRoad = "road: arrives on a staircase";
        if (onStairs == null)
        {
            road = RoadSiteEntry.Plan(request, site.Shape, width, site.Ground, world, out noRoad, RoadsNear);
            if (road != null && meets(road, null) is string roadMeets) { noRoad = "road: " + roadMeets; road = null; }
            if (road != null) { why = ""; return true; }
        }
        // Outside the Mountains a road onto the landing or nothing, but at a boss's entrance (ApproachStairsAt).
        // Nor down onto a custom location standing below the end: its fallback ends the road at its ring.
        if (!ApproachStairsAt(site.Centre, site.Record.Prefab, height)) { why = noRoad + "; stairs: none outside the mountains nor down to a custom location"; return false; }
        // Nor from a road built as a ridge or a trench to its foot (RoadStairRuns.RidgeToFoot): PlanEntry starts it lower.
        RoadSpatialGrid.PlannedPath endPiece = atStart ? plans[0] : plans[plans.Count - 1];
        if (onStairs == null && RoadStairRuns.RidgeToFoot(endPiece.Points, endPiece.Heights, atStart, PlacedGround(site)) is { } ridge)
        {
            System.Threading.Interlocked.Increment(ref RoadStairRuns.RidgesToFeet);
            why = noRoad + "; stairs: " + RoadStairRuns.RidgeRefusal(ridge);
            return false;
        }
        // Where it starts, the road's end is a level landing (RoadPitches.LandEnd), as from an end at a place not placed first
        // (SiteApproaches): limited again with that end held; where that does not fit, left as it was if within the cap into
        // it, else no staircase from it.
        List<float>? landed = onStairs == null ? new List<float>(endPiece.Heights) : null;
        if (landed != null && !RoadPitches.LandEnd(endPiece.Points, landed, atStart, RoadGrade.Configured))
        {
            why = noRoad + "; stairs: " + ApproachLandingRefusal;
            return false;
        }
        StairContinuation? top = onStairs == null ? null
            : RoadStairRuns.TopAt(end, height, StairRun.FromPlan(onStairs.FromPoint, onStairs.ToPoint, RoadStairRuns.Width, onStairs.Plan).Pieces);
        // The same start onto the same landing lays the same staircase: a failed one is not laid out again.
        // Roads to a place come in on its one aimed ring point, and each failing layout cost seconds: one
        // place took 36 of them, 34 s (the game's slowest islands 570 s).
        var failedKey = (PlaceKey(site.Centre), Mathf.RoundToInt(end.x / EntryStartCell), Mathf.RoundToInt(end.y / EntryStartCell),
            Mathf.RoundToInt(height), top.HasValue);
        string noStairs;
        // Laid out on the ground its kit will hold it up on (RoadStairRuns.PlanningGround, one owner): the lattice, the place's
        // preload in its footprint. On the place's own ground out to its grid's edge, a flight up the slope just past the
        // footprint was priced on the grid's metres and held up on the continuous ground between them.
        Func<float, float, float> stairGround = RoadStairRuns.PlanningGround(expectedPlace: null, world);
        // And a place whose landing no staircase reached from EntryStairsPerPlace starts gets no more: its
        // roads may still go on as road. Three places took 84 layouts and 65 s there.
        m_entryStairsTried.TryGetValue(failedKey.Item1, out int triedHere);
        if (m_entryStairsFailed.TryGetValue(failedKey, out string? failed)) { span = null; noStairs = failed; }
        else if (triedHere >= EntryStairsPerPlace) { span = null; noStairs = FormattableString.Invariant($"no staircase from {triedHere} other starts, none laid out again"); }
        else
        {
            System.Threading.Interlocked.Increment(ref EntryStairLayouts);
            span = RoadStairRuns.PlanPlacedSiteApproach(request, r.Yaw, r.Ops, stairGround, (x, z) => world.GetBiome(x, z), world.GetSeed(),
                out noStairs, r.RockRadius, RoadSiteProtection.Contains, r.Rocks, r.KeepSmallRocks, r.Root, top, placed: site.Shape);
            if (span == null)
            {
                m_entryStairsFailed[failedKey] = noStairs;
                m_entryStairsTried[failedKey.Item1] = triedHere + 1;
            }
        }
        if (span != null)
        {
            // It may touch the road's own staircase where the road ends on it, or just past its top: a road
            // that climbed to the ring by a staircase and ran its last metres on was refused "on another
            // staircase" at a frost cave.
            string conflict = RoadStairRuns.SiteConflict(span.Plan, site.Centre, path, index, stairGround, world, stairs,
                onStairs != null || RoadStairRuns.EndsOnStairs(end, stairs) ? end : null, request.Arriving, request.RoadHeight);
            // Laid out and clear of the place, but meeting the network as the rules forbid: as a failed layout, not laid
            // out again from this start, and counted toward the place's EntryStairsPerPlace.
            if (conflict == "" && meets(null, span) is string stairsMeet)
            {
                conflict = stairsMeet;
                m_entryStairsFailed[failedKey] = stairsMeet;
                m_entryStairsTried[failedKey.Item1] = triedHere + 1;
            }
            if (conflict == "")
            {
                if (landed != null) for (int i = 0; i < landed.Count; i++) endPiece.Heights[i] = landed[i];
                why = "";
                return true;
            }
            noStairs = conflict;
            span = null;
        }
        why = noRoad + "; stairs: " + noStairs;
        return false;
    }

    /// <summary>The network's road points within <paramref name="within"/> of a point (any thread: the grid is read under its lock).</summary>
    internal static IEnumerable<(Vector2 at, float height)> RoadsNear(Vector2 p, float within)
    {
        foreach (RoadSpatialGrid.RoadPoint q in RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(p.x, 0f, p.y), within))
            if (q.IsRoad && (q.p - p).sqrMagnitude <= within * within) yield return (q.p, q.h);
    }

    /// <summary>Why no approach is laid from a road's end (<see cref="SiteApproaches"/>, <see cref="PlanEntryFrom"/>): its last
    /// metres cannot be levelled for its foot within the grade cap (RoadPitches.LandEnd).</summary>
    internal const string ApproachLandingRefusal = "the road's end cannot be levelled for the approach's foot within the grade cap";

    /// <summary>
    /// Staircases from the road's ends that stop at a place up to the place's
    /// flat ground (<see cref="RoadStairRuns.LaySiteApproach"/>). An end is at
    /// its place unless it was moved to a junction (<paramref name="startAtPlace"/>,
    /// <paramref name="endAtPlace"/>); a place with no radius (an island's edge) has none, nor one outside the Mountains
    /// but a boss's entrance (<see cref="ApproachStairsAt"/>).
    /// Judged by distance from the trimmed ring, an end the approach bend or the contour
    /// search had moved a few metres out (by design) counted as moved off its place, and
    /// its approach was never tried (at the frost cave -2560,1536 the road did
    /// not meet the front cave landing; 51 such ends on one world).
    /// </summary>
    private static List<RoadStairSpan> SiteApproaches(List<Vector2> path, List<RoadSpatialGrid.PlannedPath> plans,
        List<RoadStairSpan> stairs, Vector2 startCenter, float askedStartRadius,
        Vector2 endCenter, float askedEndRadius, bool startAtPlace, bool endAtPlace)
    {
        var laid = new List<RoadStairSpan>();
        var world = GenerationWorld;
        if (world == null || path.Count < 2) return laid;
        void At(bool atStart, Vector2 centre, float asked, bool atPlace)
        {
            if (asked <= 0f || !atPlace) return;
            if (RoadEnd(atStart, path, plans, stairs) is not var (end, height, onStairs)) return;
            // Nor down to a custom location standing below the end: its fallback ends the road at its ring.
            if (!ApproachStairsAt(centre, PrefabAt(centre), height))
            {
                if (RoadsLog) m_siteApproaches.AddOrUpdate("none outside the mountains nor down to a custom location", 1, (_, n) => n + 1);
                return;
            }
            int index = atStart ? 0 : path.Count - 1;
            // Not from a road built as a ridge or a trench to its foot (RoadStairRuns.RidgeToFoot): none, and the road's end
            // is then not level with its place (EndNotLevel).
            RoadSpatialGrid.PlannedPath piece = atStart ? plans[0] : plans[plans.Count - 1];
            if (onStairs == null && RoadStairRuns.RidgeToFoot(piece.Points, piece.Heights, atStart, NaturalGround) != null)
            {
                System.Threading.Interlocked.Increment(ref RoadStairRuns.RidgesToFeet);
                if (RoadsLog) m_siteApproaches.AddOrUpdate(RoadStairRuns.RidgeToFootRefusal, 1, (_, n) => n + 1);
                return;
            }
            // Where it starts, the road's end is a level landing, as at every staircase's foot the road is held at
            // (RoadSpatialGrid.StairStarts): planned before the approach, the road's last metres are limited again with that
            // end held (RoadPitches.LandEnd); where they cannot be, the road is left as it was if it keeps within the cap into
            // that end, else no approach is laid from it, and the road is not built so (EndNotLevel; the planner tries its other
            // candidates). Its last 8 m at a pitch's grade ran into an approach's foot.
            List<float>? landed = null;
            if (onStairs == null)
            {
                landed = new List<float>(piece.Heights);
                if (!RoadPitches.LandEnd(piece.Points, landed, atStart, RoadGrade.Configured))
                {
                    if (RoadsLog) m_siteApproaches.AddOrUpdate(ApproachLandingRefusal, 1, (_, n) => n + 1);
                    return;
                }
            }
            var both = new List<RoadStairSpan>(stairs);
            both.AddRange(laid);
            RoadStairSpan? span = RoadStairRuns.LaySiteApproach(end, height, RoadStairRuns.ArrivingTravel(path, piece, end, atStart), centre,
                LocationLevelling.CentreHeight(centre, world), LocationLevelling.OpsAt(centre), LocationLevelling.FacesDownhill(centre),
                path, index, both, world, out string why);
            // A place placed before its road was carried on onto its landing (PlanEntry); one that could not be
            // placed before its roads has no road (PlacedSites.NotPlaced), so none ends here.
            if (span != null)
            {
                laid.Add(span);
                if (landed != null) for (int i = 0; i < landed.Count; i++) piece.Heights[i] = landed[i];
            }
            if (RoadsLog) m_siteApproaches.AddOrUpdate(span != null ? "laid" : why, 1, (_, n) => n + 1);
        }
        At(true, startCenter, askedStartRadius, startAtPlace);
        At(false, endCenter, askedEndRadius, endAtPlace);
        return laid;
    }

    /// <summary>The staircases on <paramref name="path"/> laid out from the stair
    /// steps the last search bought; false when one cannot be laid.</summary>
    private static bool TryLayStairs(IReadOnlyList<(Vector2 from, Vector2 to)> steps, List<Vector2> path, List<RoadCrossing> crossings, out List<RoadStairSpan> stairs)
    {
        stairs = new List<RoadStairSpan>();
        if (steps.Count == 0) return true;
        List<RoadStairSpan>? laid = RoadStairRuns.Lay(path, steps, crossings, GenerationWorld);
        if (laid == null) return false;
        stairs = laid;
        return true;
    }

    /// <summary>
    /// Convenience overload using Vector3 positions (extracts X/Z as Vector2).
    /// </summary>
    public static bool GenerateRoad(
        Vector3 startPos, float startRadius,
        Vector3 endPos, float endRadius,
        float width, string? label = null) =>
        GenerateRoad(startPos, startRadius, endPos, endRadius, width, label, null);

    internal static bool GenerateRoad(
        Vector3 startPos, float startRadius,
        Vector3 endPos, float endRadius,
        float width, string? label, List<Vector2>? networkHints)
    {
        return GenerateRoad(
            new Vector2(startPos.x, startPos.z), startRadius,
            new Vector2(endPos.x, endPos.z), endRadius,
            width, label, networkHints);
    }

    #endregion

    #region Location Data

    public struct LocationData
    {
        public Vector3 SpawnPoint;
        public float SpawnRadius;
        public List<(string name, Vector3 position, float radius)> AllLocations;
    }

    private static IEnumerable<bool> GatherLocationSteps(ZoneSystem zones,RoadPreparation preparation,Action<LocationData?> complete)
    {
        // Snapshot the live ValueCollection in one main-thread operation; no live enumerator crosses a frame.
        var source=zones.GetLocationList()?.ToArray();
        preparation.BeginStage("Copying world locations",source?.Length ?? 0,"locations"); yield return true;
        if(source==null || source.Length==0) { Log.LogWarning("No location instances found"); complete(null); yield break; }
        Vector3? spawn=null; float radius=0;
        var all=new List<(string name,Vector3 position,float radius)>();
        foreach(var location in source)
        {
            var item=(location.m_location.m_prefab.Name!,location.m_position,location.m_location.m_exteriorRadius);
            if(item.Item1=="StartTemple") { spawn=item.Item2; radius=item.Item3; all.Add(item); }
            // HARD RULE: a place the game leaves for a player to find is never a destination (PlacedSites.PlayerDecides).
            else if(PlacedSites.PlayerDecides(item.Item1,location.m_location.m_unique,location.m_location.m_iconPlaced,location.m_location.m_iconAlways)) { }
            else all.Add(item);
            if(preparation.Checkpoint()) yield return true;
        }
        if(!spawn.HasValue) { Log.LogWarning("Could not find spawn point (StartTemple)"); spawn=Vector3.zero; }
        Log.LogDebug($"Found spawn at {spawn.Value}, {all.Count} total locations");
        complete(new LocationData {SpawnPoint=spawn.Value,SpawnRadius=radius,AllLocations=all});
    }

    /// <summary>Whether a location is a destination on <paramref name="island"/>: on it, a road location, and in a biome the
    /// network allows.</summary>
    private static bool IsIslandDestination(Island island, (string name, Vector3 position, float radius) location,
        WorldGenerator world, RoadNetworkOptions options) =>
        island.ContainsPoint(location.position) && IsRoadLocation(location.name)
        && options.Allows(world.GetBiome(location.position.x, location.position.z));

    /// <summary>Hildir's quest sites: her cave, crypt and tower. Not her camp (unique, a vendor).</summary>
    internal static bool IsHildirQuestSite(string name) => name == "Hildir_cave" || name == "Hildir_crypt" || name == "Hildir_plainsfortress";

    /// <summary>The one rule for Hildir's quest sites: roads go to them only with their setting on (config
    /// Locations.AttemptRoadsToHildirQuestSites). Then they are required destinations, placed before their roads; off, they
    /// are left for a player, as the game leaves them.</summary>
    internal static bool HildirRoads(string name) => IsHildirQuestSite(name) && GenerationOptions.HildirQuestSites;

    private static bool IsRoadLocation(string locationName)
    {
        if (IsHildirQuestSite(locationName)) return HildirRoads(locationName);
        return LocationPriorities.ContainsKey(locationName) ||
               IsCustomLocation(locationName);
    }

    private static bool IsCustomLocation(string name) => RegisteredLocationNames.Contains(name)
        || ConfiguredLocationNames.Contains(name);

    private static bool IsRequiredLocation(string name) => IsBossLocation(name)
        || HildirRoads(name)
        || name == RoadCoastalLandings.Name;

    private static List<(string name, Vector3 position, float radius)> SelectIslandDestinations(
        Island island, List<(string name, Vector3 position, float radius)> locations, int quota)
    {
        var candidates = new List<(string name, Vector3 position, float radius)>(locations);
        // Island ranking has already happened using real POIs only. Landings
        // consume quota slots like required sites; they never select an island.
        candidates.AddRange(island.CoastalLandings.Select(p => (RoadCoastalLandings.Name, p, RoadCoastalLandings.Radius)));
        var selected = SelectLocations(candidates, quota, island.ApproxArea);
        Log.LogDebug($"Coastal landings on island {island.Id}: {selected.Count(p => p.name == RoadCoastalLandings.Name)} selected; connection and boat access are not guaranteed");
        return selected;
    }

    private static List<(string name, Vector3 position, float radius)> SelectLocations(
        List<(string name, Vector3 position, float radius)> candidates, int maxCount, float area = 0) =>
        RoadNetworkSelection.Destinations(candidates, maxCount, area, GetLocationPriority,
            IsRequiredLocation, GenerationWorld?.GetSeed() ?? 0, GenerationOptions);

    private static int GetLocationPriority(string locationName)
    {
        if (locationName == RoadCoastalLandings.Name) return RoadCoastalLandings.Priority;
        if (LocationPriorities.TryGetValue(locationName, out int priority))
            return priority;

        if (IsCustomLocation(locationName))
            return CustomLocationPriority;

        return DefaultPriority;
    }

    #endregion

    #region Island Road Strategies

    private static void GenerateIslandRoads(
        Island island,
        List<(string name, Vector3 position, float radius)> islandLocations,
        Vector3? overrideStart = null,
        float overrideStartRadius = 0f)
    {
        RoadLifecycleManager.ThrowIfCancellationRequested();
        if (islandLocations.Count == 0) return;
        
        Vector3 startPos;
        float startRadius;
        if (overrideStart.HasValue)
        {
            startPos = overrideStart.Value;
            startRadius = overrideStartRadius;
        }
        else
        {
            Vector2 edge = island.GetEdgePoint();
            startPos = new Vector3(edge.x, 0, edge.y);
            startRadius = 0f;
        }
        
        Log.LogDebug($"Island {island.Id}: {islandLocations.Count} locations");
        
        IslandActivity?.Stage("Connecting");
        GenerateRoutedRoads(startPos, startRadius, islandLocations);
    }

    #endregion

    #region Utility Methods

    /// <summary>
    /// Everything that reads Unity (location prefabs out of AssetBundles, the
    /// site footprint index, saved placement heights) done here, on the main
    /// thread, and then preparation sealed, so a worker that misses names the
    /// miss instead of loading an asset: Unity refuses asset loads off the
    /// main thread. Throws, having logged why, when the footprint index could
    /// not be built (PrepareAndSeal returns false instead). The caller must
    /// Unseal() in a finally.
    /// </summary>
    private static IEnumerable<bool> PrepareAndSealSteps(RoadPreparation preparation)
    {
        if(LocationLevelling.PrimeIncremental!=null && (LocationLevelling.CanPrimeIncrementally?.Invoke() ?? true))
            foreach(var step in RoadPreparation.Own(LocationLevelling.PrimeIncremental(preparation))) yield return step;
        else
        {
            preparation.BeginStage("Preparing location footprints"); yield return true;
            foreach(var step in RoadSiteProtection.PrimeSteps(preparation)) yield return step;
            preparation.BeginStage("Preparing saved placements"); yield return true;
            LocationLevelling.Prime?.Invoke();
        }
        if(RoadSiteProtection.InstalledButUnprepared)
        {
            Log.LogError("the site footprint index could not be built, so no road could avoid a POI. " +
                         "No roads were generated. This is a gap in preparation, not in the world.");
            throw new InvalidOperationException("Road site preparation failed; no network was published.");
        }
        preparation.BeginStage("Finishing road preparation"); yield return true;
        LocationLevelling.Seal();
    }

    private static void ReportMissesAfterSealing()
    {
        if (LocationLevelling.MissesAfterSealing > 0)
            Log.LogError($"{LocationLevelling.MissesAfterSealing} lookup(s) arrived after preparation closed; " +
                         "roads near those places meet natural terrain. See the errors above for what was missing.");
    }

    /// <summary>Told when the network is reset (the world unloaded, or regenerated).</summary>
    public static Action? OnReset;

    public static void Reset()
    {
        bool cancelled = RoadLifecycleManager.IsGenerating;
        RoadLifecycleManager.CancelAndDrain();
        RoadLifecycleManager.AllowInitialRetry();
        if (!cancelled) ResetData();
        RoadNetworkPersistence.Reset();
    }

    /// <summary>
    /// Everything one generation starts afresh: its world and options, its searches (one per worker), and every counter and
    /// per-place note it keeps. The one reset a whole-world generation and an island's regeneration share.
    /// </summary>
    private static void ResetGenerationState(WorldGenerator world, RoadNetworkOptions options)
    {
        m_generationOptions = options;
        m_generationWorld = world;
        float clearance = RoadSiteProtection.Clearance(RoadWidth);
        m_pathfinder = NewFinder(world, clearance);
        m_threadPathfinders?.Dispose();
        m_threadPathfinders = new System.Threading.ThreadLocal<RoadPathfinder>(() => NewFinder(world, clearance), true);
        ResetGenerationCounters();
        ResetDoors();
        RoadSiteProtection.Reset();
        PlaceWaysIn.Reset();
    }

    /// <summary>The counters and per-place notes of a generation (logs, reports, retry budgets per place).</summary>
    private static void ResetGenerationCounters()
    {
        CrossIslandReads.Reset();
        RoadTerrainSamples.ResetTotals();
        SearchStats.Reset();
        m_destinationsSelected = 0;
        m_destinationsConnected = 0;
        m_destinationsNotPlaced = 0;
        m_offLevel.Clear();
        m_customPlaces.Clear();
        m_standOffRadius.Clear();
        m_reachedOffLevel = 0;
        m_behindRidge = 0;
        m_carriedToEdge = 0;
        m_roadsGeneratedCount = 0;
        m_roadsRefusedForGrade = 0;
        m_refusalReasons.Clear();
        m_lastFailure.Clear();
        ClearStairNotes();
        m_builtTo.Clear();
        m_entryStairsFailed.Clear();
        m_entryStairsTried.Clear();
        EntryStairLayouts = 0;
        m_roadsWithNoRoute = 0;
        m_earthworkReroutes = 0;
        m_ridgeReroutes = 0;
        m_ridgeStairs = 0;
        m_pullReroutes = 0;
        m_offLandEnds = 0;
        RoadCrossingDetector.DeckLineWalkedIn = RoadCrossingDetector.DeckLineRefused = RoadCrossingDetector.DeckLineSpanDropped = 0;
        RoadPitches.ResetCounters();
        RoadSpatialGrid.ResetPlaceLandings();
        m_corridorSnapped = 0;
        m_reEndedOnApproach = 0;
        m_reEndRefused = 0;
        m_junctionFills = 0;
        RoadSpatialGrid.BendFallbacks.Clear();
        m_chainsShared = 0;
        RoadRide.Reset();
        m_shallowFordsWalked = 0;
        m_humpsLevelled = 0;
        RoadSpatialGrid.SwayKept = RoadSpatialGrid.SwayRefused = RoadSpatialGrid.SwayMoreEarth = RoadSpatialGrid.SwayKeptHeld = 0;
        RoadSpatialGrid.SlopeSwayPlans = 0; System.Array.Clear(RoadSpatialGrid.SlopeSwayMillimetres, 0, 3);
        RoadPathfinder.SqueezeHits.Clear(); RoadPathfinder.ShoreFillCells.Clear();
        m_earthworkUnavoided = 0;
        RoadGrade.SteepestPlanned = 0f;
        m_viaductsLaid = m_viaductsRefusedAtEnds = m_crossingsUnmetSearchedRound = m_bridgeEndsUnmet = m_endEarthworks = m_endPads = m_endShared = m_endStands = m_endSteps = m_endFlat = m_endOneStep = m_endMoreSteps = 0;
    }

    internal static void ResetData()
    {
        ResetGenerationCounters();
        ResetDoors();
        PlacedSites.ForgetLanded();
        OnReset?.Invoke();
        ManualRoads.Reset();
        LocationLevelling.ResetPlacements?.Invoke();
        m_roadsGenerated = false;
        m_locationsReady = false;
        m_roadsLoadedFromZDO = false;
        m_networkDirty = false;
        m_kitsChosen = false;
        ReleasePathfinders();
        RoadSiteProtection.Reset();
        m_roadStartPoints.Clear();
        m_roadCrossings.Clear();
        lock (m_recordGate) m_coastalLandings.Clear();
        StairStructures.Reset();
        PlaceWaysIn.Reset();
        BuiltBridges.Reset();
        DvergrViaduct.ResetStanding();
        lock (m_laidDecks) m_laidDecks.Clear();
        RoadStairRuns.ResetCache();
        StructurePlans.Reset();
        VegetationClearing.Reset();
        RoadSpatialGrid.Clear();
    }

    private static void LogGenerationStats(int roadsGenerated, TimeSpan elapsed)
    {
        var log = Log;

        log.LogDebug("=== Road Generation Summary ===");
        log.LogDebug($"  Roads generated: {roadsGenerated}");
        log.LogDebug($"  Stored road-point records: {RoadSpatialGrid.StoredPointRecords}");
        log.LogDebug($"  Committed planned-path length: {RoadSpatialGrid.Statistics.LengthText()}");
        log.LogDebug($"  Grid cells with roads: {RoadSpatialGrid.GridCellsWithRoads}");

        if (roadsGenerated > 0)
        {
            log.LogDebug($"  Avg stored records/road: {RoadSpatialGrid.StoredPointRecords / (float)roadsGenerated:F0}");
            log.LogDebug($"  Avg committed length/road: {RoadSpatialGrid.Statistics.LengthText(divisor:roadsGenerated)}");
        }

        log.LogDebug($"  Generation time: {elapsed.TotalSeconds:F2}s");
        log.LogDebug($"  Road width: {RoadWidth}m");
        log.LogDebug($"  Max grade: {(RoadGrade.Capped(RoadGrade.Configured) ? RoadGrade.Configured.ToString("P0") : "uncapped")}");
        log.LogDebug($"  Destinations dropped: {m_roadsWithNoRoute} with no route, {m_roadsRefusedForGrade} refused when the profile was planned (grade, turn room, footprint or water)");
        log.LogDebug($"  Steepest road built: {RoadGrade.SteepestPlanned:P1}");
        log.LogDebug("===============================");
    }

    /// <summary>
    /// Trims a path so it stops at the exterior radius of both endpoints.
    /// </summary>
    private static List<Vector2>? TrimPathToRadii(List<Vector2> path, Vector2 startCenter, float startRadius, Vector2 endCenter, float endRadius)
    {
        if (path == null || path.Count < 2)
            return null;

        int startIndex = 0;
        float startRadiusSq = startRadius * startRadius;
        for (int i = 0; i < path.Count; i++)
        {
            if ((path[i] - startCenter).sqrMagnitude > startRadiusSq)
            {
                startIndex = i;
                break;
            }
        }

        int endIndex = path.Count - 1;
        float endRadiusSq = endRadius * endRadius;
        for (int i = path.Count - 1; i >= 0; i--)
        {
            if ((path[i] - endCenter).sqrMagnitude > endRadiusSq)
            {
                endIndex = i;
                break;
            }
        }

        if (endIndex <= startIndex)
            return null;

        var trimmedPath = new List<Vector2>();

        if (startIndex > 0 && startIndex < path.Count)
        {
            Vector2 edgePoint = CalculateRadiusIntersection(path[startIndex], startCenter, startRadius);
            trimmedPath.Add(edgePoint);
        }

        for (int i = startIndex; i <= endIndex; i++)
        {
            trimmedPath.Add(path[i]);
        }

        if (endIndex < path.Count - 1 && endIndex >= 0)
        {
            Vector2 edgePoint = CalculateRadiusIntersection(path[endIndex], endCenter, endRadius);
            trimmedPath.Add(edgePoint);
        }

        return trimmedPath.Count >= 2 ? trimmedPath : null;
    }

    /// <summary>
    /// Calculates the point on the radius circle in the direction from center to the given point.
    /// </summary>
    private static Vector2 CalculateRadiusIntersection(Vector2 outsidePoint, Vector2 center, float radius)
    {
        Vector2 direction = (outsidePoint - center).normalized;
        return center + direction * radius;
    }

    #endregion

    #region Persistence

    /// <summary>
    /// Unique prefab name for our metadata ZDO. Must not conflict with any game prefabs.
    /// This is public so Plugin.cs can register the prefab with Jotunn.
    /// </summary>
    public const string MetadataPrefabName = RoadNetworkPersistence.MetadataPrefabName;

    /// <summary>
    /// Save the entire road network to a dedicated ZDO for persistence across world reloads.
    /// Call this on world save.
    /// </summary>
    public static void SaveGlobalRoadData()
    {
        if (IsGenerating) return;
        if (!m_networkDirty)
        {
            Log.LogDebug("[SAVE] No roads generated or appended, skipping global save");
            return;
        }
        // A released build's network stays in the format that build reads.
        if (RoadSpatialGrid.LoadedFromReleasedFormat)
        {
            Log.LogWarning("[SAVE] The road network was saved by a released build (format 1); it is not rewritten");
            return;
        }

        RoadNetworkPersistence.SaveGlobalRoadData(m_roadStartPoints, m_roadCrossings, StructurePlans.SpawnedZones, GetCoastalLandings());
    }

    /// <summary>
    /// Save only which zones have their bridge pieces (bridges prototype).
    /// For a session whose network was loaded, not generated: the network
    /// itself is unchanged, but zones spawned this session must be
    /// remembered, or a bridge whose pieces were all destroyed comes back.
    /// </summary>
    public static void SaveBridgeZones()
    {
        if (IsGenerating) return;
        if (!RoadsAvailable)
            return;
        // The crossings too once a kit was chosen: but not over a network a released build saved, which keeps its format.
        bool crossings = m_kitsChosen && !RoadSpatialGrid.LoadedFromReleasedFormat;
        RoadNetworkPersistence.SaveBridgeZones(StructurePlans.SpawnedZones, crossings ? m_roadCrossings : null);
    }

    /// <summary>Whether a crossing's kit was chosen this session (<see cref="ChooseKitsAsBuilt"/>): the crossings are saved
    /// with the zones that have their pieces.</summary>
    private static bool m_kitsChosen;

    /// <summary>
    /// The kits of the bridges these pieces belong to chosen as the server builds them (BridgeStructures.ChooseKits), on
    /// the network's records, and saved at the next save: a bridge built keeps its kit, one not built yet takes its kit
    /// from the kits installed when it is.
    /// </summary>
    internal static void ChooseKitsAsBuilt(IEnumerable<StructurePiece> pieces)
    {
        if (WorldGenerator.instance is not WorldGenerator world) return;
        lock (m_recordGate)
            if (BridgeStructures.ChooseKits(pieces, world, m_roadCrossings)) m_kitsChosen = true;
    }

    /// <summary>
    /// Try to load the entire road network from persisted ZDO.
    /// Call this on world load before road generation would trigger.
    /// </summary>
    /// <returns>True if road data was found and loaded</returns>
    /// <summary>The natural ground places placed before their roads are made ready on (<see cref="PlacedSites.Prepare"/>), at
    /// a generation and at a load alike.</summary>
    private static Func<float, float, float> NaturalGround(WorldGenerator world) => (x, z) => BiomeBlendedHeight.GetBlendedHeight(x, z, world);

    public static bool TryLoadGlobalRoadData()
    {
        if (IsGenerating) return false;
        var bridgeZones = new HashSet<Vector2s>();
        bool loaded = RoadNetworkPersistence.TryLoadGlobalRoadData(m_roadStartPoints, m_roadCrossings, bridgeZones, m_coastalLandings);
        if (loaded)
        {
            // The places placed before the roads ready again, on the ground the generation made them ready on: a staircase
            // whose first zone is built this session is dressed as it would have been in the session that generated it.
            if (WorldGenerator.instance != null) PlacedSites.Prepare(NaturalGround(WorldGenerator.instance));
            else if (PlacedSites.Count > 0)
                Log.LogWarning($"[LOAD] {PlacedSites.Count} place(s) placed before their roads not made ready: no world generator yet; their staircases are dressed on the natural ground");
            // Before anything may write: which zones this network must leave alone.
            NetworkCutoff.UseLoadedNetwork(RoadSpatialGrid.RoadNetworkVersion, RoadNetworkPersistence.LoadZonesWithNetwork());
            // Zones whose pieces stand keep them, whatever layout made them.
            StructurePlans.MarkSpawned(bridgeZones);
            // The valley viaducts' keep-out, as the generation left it: no later road (a manual one) under a deck.
            DvergrViaduct.ResetStanding();
            foreach (RoadCrossing c in m_roadCrossings)
                if (c.ValleyJump) DvergrViaduct.MarkStanding(c);
        }
        return loaded;
    }

    /// <summary>
    /// Save which zones were generated with this network in place
    /// (NetworkCutoff), so the next session knows which zones it may write.
    /// </summary>
    public static void SaveNetworkCutoff()
    {
        if (IsGenerating || !RoadsAvailable || ZoneSystem.instance == null) return;
        byte[]? record = NetworkCutoff.Record(RoadSpatialGrid.RoadNetworkVersion, ZoneSystem.instance.m_generatedZones);
        if (record != null) RoadNetworkPersistence.SaveZonesWithNetwork(record);
    }

    /// <summary>
    /// Save which zones' vegetation matches the network (VegetationClearing),
    /// so a zone is cleared once per network and not again on every start.
    /// </summary>
    public static void SaveClearedZones()
    {
        if (IsGenerating) return;
        if (RoadsAvailable && VegetationClearing.Version != 0) VegetationClearing.CarryOver(RoadSpatialGrid.RoadNetworkVersion);
        if (!RoadsAvailable || VegetationClearing.Version == 0 ||
            VegetationClearing.Version != RoadSpatialGrid.RoadNetworkVersion)
            return;
        RoadNetworkPersistence.SaveClearedZones(VegetationClearing.Version, VegetationClearing.ClearedZones, VegetationClearing.ClearedThrough);
    }

    #endregion
}
