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
    
    private static readonly HashSet<string> BossLocationNames = new HashSet<string>
    {
        "Eikthyrnir",
        "GDKing",
        "Bonemass",
        "Dragonqueen",
        "GoblinKing",
        "Mistlands_DvergrBossEntrance1",
    };

    // Destination priorities for the vanilla locations reviewed on Valheim 1.0.12.
    private static readonly Dictionary<string, int> LocationPriorities = new()
    {
        { "Bonemass", 100 },
        { "Dragonqueen", 100 },
        { "Eikthyrnir", 100 },
        { "GDKing", 100 },
        { "GoblinKing", 100 },
        { "Mistlands_DvergrBossEntrance1", 100 },

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
    /// <summary>Selection weight of a custom location (named by prefix, config or API).</summary>
    private const int CustomLocationPriority = 80;
    public static RoadNetworkOptions NetworkOptions { get; set; } = new();

    public static int MaxLocationsPerIsland = 48;


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

    public static float RoadWidth = 4f;
    public static int IslandRoadPercentage = 33;

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
        m_threadPathfinders != null ? m_threadPathfinders.Value : m_pathfinder;
    private static readonly object m_recordGate = new object();
    [System.ThreadStatic] private static int m_islandRoads;
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
    /// Whether the network arrives level with the place at <paramref name="centre"/> (its prefab <paramref name="prefab"/>,
    /// null where not known), by an approach staircase where the road cannot (<see cref="SiteApproaches"/>, and onto a placed
    /// place's landing, <see cref="PlanEntryFrom"/>): at a boss's entrance anywhere (<see cref="OwnLandingOnly"/>), and at
    /// every other place only in the Mountains, by the biome at its centre. Elsewhere a road that comes close is enough: it is not refused for an end
    /// not level with its place (<see cref="EndNotLevel"/>), a placed place whose landing no road gets onto keeps the
    /// road where it ended (<see cref="PlanEntry"/>), and the place is reached at any height (<see cref="Reached"/>).
    /// The one owner of that choice.
    /// </summary>
    internal static bool ApproachStairsAt(Vector2 centre, string? prefab)
    {
        if (prefab != null && OwnLandingOnly(prefab)) return true;
        var world = GenerationWorld;
        return world == null || world.GetBiome(centre.x, centre.y) == Heightmap.Biome.Mountain;
    }

    /// <summary>The one test of whether the network reaches the place at <paramref name="flat"/> (exterior
    /// <paramref name="radius"/>, as asked, not trimmed), measured at the trim's keep-out edge (<see cref="KeepOutEdge"/>):
    /// for the count, the planner's join (<see cref="PlaceServed"/>) and whether a road's own stub to the place is dropped
    /// where it merges (<see cref="EndReached"/>).
    ///
    /// Reached only where the network arrives level with the place (<see cref="ArrivalLevel"/>), by road or by a
    /// staircase's walked piece, its top or foot; a place with no known arrival height (no single levelled flat,
    /// <see cref="PlaceHeight"/>) is reached at any height. Outside the Mountains (<see cref="ApproachStairsAt"/>) every
    /// place is reached at any height, and a placed one at its ring as well as on its landing.</summary>
    internal static bool Reached(Vector2 flat, float radius, string? name = null)
    {
        // A place placed before its roads is reached on its landing, not at its ring.
        // A boss's entrance only by a road onto its own landing (OwnLandingOnly): placed, once one was carried onto it;
        // not placed, never by a road passing within reach (the count and the planner's join alike).
        if (radius > 0f && PlacedSites.TryGetReady(flat, out PlacedSite placed))
            return OwnLandingOnly(placed.Record.Prefab) ? PlacedSites.Landed(placed.Centre)
                : LandingReached(placed) || !ApproachStairsAt(placed.Centre, placed.Record.Prefab)
                    && NetworkNear(flat, KeepOutEdge(flat, radius, RoadWidth) + ReachPastEdge, null);
        if (name != null && OwnLandingOnly(name)) return false;
        float reach = radius > 0f
            ? KeepOutEdge(flat, radius, RoadWidth)
            : radius;
        // A staircase is the network too: a road that reaches a place by one ends on its top or foot,
        // with no road there (MountainCave02 -2560,1536: its staircase's top 38.8 m out, the
        // nearest road 52 m, counted unreached).
        return NetworkNear(flat, reach + ReachPastEdge, radius > 0f && ApproachStairsAt(flat, name) ? PlaceHeight(flat) : null);
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
    /// <see cref="ArrivalLevel"/>; at any height where it is not known): the network a place is reached by. The one height
    /// guard for every place, placed first or not.</summary>
    private static bool NetworkNear(Vector2 at, float reach, float? level)
    {
        float low = level.HasValue ? level.Value - ArrivalLevel : float.NegativeInfinity;
        float high = level.HasValue ? level.Value + ArrivalLevel : float.PositiveInfinity;
        return RoadSpatialGrid.TryGetRoadWithin(at, reach, out _, minHeight: low, maxHeight: high)
            || StairStructures.PiecesNear(new Vector3(at.x, 0f, at.y), reach).Exists(p => p.Kind != StructurePieceKind.StairSupport
                && RoadStairRuns.Surface(p) >= low && RoadStairRuns.Surface(p) <= high);
    }

    /// <summary>For the reports (an unreached place's line): the height the place is arrived at
    /// (a placed place's landing ground, else <see cref="PlaceHeight"/>) and how far over (+) or under (-) it the network
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
            height = placed.Ground(at.x, at.y);
        }
        else
        {
            at = flat;
            reach = KeepOutEdge(flat, radius, RoadWidth) + ReachPastEdge;
            height = PlaceHeight(flat);
        }
        if (height is not float place) return null;
        float? best = null;
        void Offer(float h) { if (best == null || Mathf.Abs(h - place) < Mathf.Abs(best.Value)) best = h - place; }
        foreach (var (_, h) in RoadsNear(at, reach)) Offer(h);
        foreach (StructurePiece p in StairStructures.PiecesNear(new Vector3(at.x, 0f, at.y), reach))
            if (p.Kind != StructurePieceKind.StairSupport && Vector2.Distance(new Vector2(p.Position.x, p.Position.z), at) <= reach)
                Offer(RoadStairRuns.Surface(p));
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
        PlacedSites.Landed(site.Centre) || NetworkNear(site.Shape.Landing, LandingReach, site.Ground(site.Shape.Landing.x, site.Shape.Landing.y));

    /// <summary>The ground a place's approach arrives at (its platform, never below its root), or
    /// null where its levelling is not known.</summary>
    internal static float? PlaceHeight(Vector2 centre)
    {
        var world = GenerationWorld;
        if (world == null) return null;
        IReadOnlyList<LevelOp>? ops = LocationLevelling.OpsAt(centre);
        return ops == null ? null : LocationLevelling.ApproachHeight(LocationLevelling.CentreHeight(centre, world), ops);
    }

    /// <summary>Why a road's end at one of its places does not arrive level with the place (<see cref="ArrivalLevel"/>)
    /// and no approach staircase was laid from it onto the place (<see cref="SiteApproaches"/>), or null. The end is where
    /// walking ends: the last road point, or the staircase the road ends on. A place whose arrival height is not known
    /// (<see cref="PlaceHeight"/>) is not judged. Over as well as under: before, only an end more than 20 m under its place was refused
    /// (MaxApproachClimb), and that road was then built anyway as the place's last resort when nothing else reached it.
    /// Only in the Mountains or at a boss's entrance (<see cref="ApproachStairsAt"/>): elsewhere an end off level is kept as it is.</summary>
    private static string? EndNotLevel(List<Vector2> path, List<RoadSpatialGrid.PlannedPath> plans, List<RoadStairSpan> stairs,
        List<RoadStairSpan> approaches, Vector2 startCenter, float startRadius, Vector2 endCenter, float endRadius, bool startAtPlace,
        bool endAtPlace)
    {
        string? At(bool atStart, Vector2 centre, float radius, bool atPlace)
        {
            if (!atPlace || radius <= 0f || plans.Count == 0) return null;
            if (approaches.Exists(a => a.FromIndex == (atStart ? 0 : path.Count - 1))) return null;
            if (PlaceHeight(centre) is not float place) return null;
            float height;
            RoadStairSpan? last = stairs.Find(s => atStart ? s.FromIndex == 0 : s.ToIndex == path.Count - 1);
            if (last != null) height = atStart ? last.FromHeight : last.ToHeight;
            else
            {
                var piece = atStart ? plans[0] : plans[plans.Count - 1];
                if (piece.Heights.Count == 0) return null;
                height = atStart ? piece.Heights[0] : piece.Heights[piece.Heights.Count - 1];
            }
            float off = height - place;
            if (Mathf.Abs(off) <= ArrivalLevel) return null;
            // Outside the Mountains a road that came close is enough (ApproachStairsAt): counted, not refused.
            if (!ApproachStairsAt(centre, PrefabAt(centre)))
            {
                if (RoadsLog) System.Threading.Interlocked.Increment(ref m_closeEnds);
                return null;
            }
            return FormattableString.Invariant($"ends {Mathf.Abs(off):F1} m {(off > 0f ? "over" : "under")} its place, not level with it, and no approach staircase onto it");
        }
        return At(true, startCenter, startRadius, startAtPlace) ?? At(false, endCenter, endRadius, endAtPlace);
    }

    /// <summary>The refusal a road whose end is not level with its place is counted under (<see cref="EndNotLevel"/>).</summary>
    internal const string NotLevelRefusal = "end not level with its place";

    /// <summary>Read the crossing registry under the same gate used by commits.
    /// Another island may append while this road looks for shared banks.</summary>
    internal static void SnapToExistingCrossings(IEnumerable<RoadCrossing> crossings)
    {
        lock (m_recordGate)
        {
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
                        if (SameCorridorCrossing(existing, span, out float d) && d < best) { best = d; match = existing; }
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
                    if (RoadCrossing.SameBanks(existing, crossing)) { match = existing; break; }
                // Two roads crossing the same water side by side share one crossing (the corridor
                // snap's rule for crossings; measured: two roads left one place through a pool,
                // each with its own ford 6.5 m apart). Nearest qualifying site wins.
                if (match == null && CorridorSnap > RoadSnap)
                {
                    float best = float.MaxValue;
                    foreach (RoadCrossing existing in m_roadCrossings)
                        if (SameCorridorCrossing(existing, crossing, out float d) && d < best) { best = d; match = existing; }
                }
                if (match != null)
                {
                    crossing.SnapTo(match);
                    crossing.Shared = true;
                }
            }
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
    /// <summary>The crossings built so far, copied under the gate commits take (another island may append meanwhile).</summary>
    internal static List<RoadCrossing> CrossingsSnapshot()
    {
        lock (m_recordGate) return new List<RoadCrossing>(m_roadCrossings);
    }
    public static bool RoadsLoadedFromZDO => m_roadsLoadedFromZDO;
    /// <summary>Whether the network must be saved in full (<see cref="m_networkDirty"/>).</summary>
    public static bool NetworkDirty => m_networkDirty;
    public static bool RoadsAvailable => !RoadLifecycleManager.WorkersOwnWorld && (m_roadsGenerated || m_roadsLoadedFromZDO || m_networkDirty);

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
    /// At world load, between choosing the destinations and building the roads: the preparation steps
    /// that place the places their landing waits for (<see cref="SitesFirstLoad"/>), each step a stage
    /// with its own progress on the loading panel. Only the load-time generation runs them
    /// (<see cref="BeginPreparation"/> with placeSitesFirst): placing waits for the game's frames, which a
    /// synchronous caller does not give it. Settable for tests.
    /// </summary>
    internal static Func<GenerationWork, RoadPreparation, IEnumerable<bool>>? SitesFirst;

    /// <summary>Load-time entry: generate unless GenerateOnLoad is off. Returns whether it generated.</summary>
    public static bool GenerateRoadsOnLoad()
    {
        if (!GenerateOnLoad)
        {
            Log.LogInfo("PROCEDURALROADS_GENERATE_ROADS_ON_LOAD is off: " +
                        "no roads until road_generate or road_regen_island");
            return false;
        }
        GenerateRoads();
        if (RoadsAvailable) ApplyGeneratedNetworkToLoadedZones();
        return RoadsAvailable;
    }

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

    /// <summary>
    /// Main entry point for road generation. Calls various generation methods.
    /// </summary>
    /// <param name="force">If true, regenerate roads even if already generated (for existing worlds)</param>
    public static void GenerateRoads(bool force = false)
    {
        if (RoadsAvailable && !force) return;
        RoadLifecycleManager.RunSynchronous(() =>
        {
            var work = PrepareGeneration(force);
            if (work == null) return;
            RoadLifecycleManager.ReportPrepared(work);
            try { work.Compute(); RoadLifecycleManager.ReportFinished(work); CompleteGeneration(work); }
            finally { ReleaseGenerationResources(); }
        });
    }

    // The asynchronous load path and explicit synchronous callers use these same phases.
    internal static GenerationWork? PrepareGeneration(bool force)
    {
        using var preparation=BeginPreparation(force);
        preparation.Drain();
        preparation.LogTimings();
        return preparation.TakeResult();
    }
    /// <param name="placeSitesFirst">The load-time generation: the places their landings wait for are placed
    /// first (<see cref="SitesFirst"/>), over frames.</param>
    internal static RoadPreparation BeginPreparation(bool force, bool placeSitesFirst = false)
    {
        var world=WorldGenerator.instance;
        var zones=ZoneSystem.instance;
        var options=NetworkOptions.Snapshot();
        var start=DateTime.Now;
        int percentage=IslandRoadPercentage,maxLocations=MaxLocationsPerIsland;
        return new RoadPreparation(p=>PrepareSteps(force,placeSitesFirst,world,zones,options,start,percentage,maxLocations,p));
    }
    private static IEnumerable<bool> PrepareSteps(bool force,bool placeSitesFirst,WorldGenerator? capturedWorld,ZoneSystem? capturedZones,
        RoadNetworkOptions options,DateTime startTime,int percentage,int maxLocations,RoadPreparation preparation)
    {
        if (!RoadLifecycleManager.HoldsTheNetwork)
        {
            Log.LogWarning("Roads are generated only on the server; this peer holds no road network");
            yield break;
        }
        if (RoadsAvailable && !force) yield break;
        if (force && RoadsAvailable) ResetData();
        if (capturedWorld == null)
        {
            Log.LogWarning("WorldGenerator not available, cannot generate roads");
            yield break;
        }

        if (capturedZones == null)
        {
            Log.LogWarning("ZoneSystem not available, cannot generate roads");
            yield break;
        }

        preparation.BeginStage("Preparing road settings"); yield return true;
        Log.LogDebug("Starting road network generation...");

        RegisterConfiguredLocations();
        NetworkCutoff.BeginNewNetwork();

        var world = capturedWorld;
        ResetGenerationState(world, options);

        LocationData? locations=null;
        foreach(var step in GatherLocationSteps(capturedZones,preparation,value=>locations=value)) yield return step;
        if(locations==null) yield break;

        List<Island>? islands=null;
        foreach(var step in IslandDetector.DetectRoadIslandsSteps(world,options,preparation,value=>islands=value)) yield return step;
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
                if(island.ContainsPoint(loc.position) && IsRoadLocation(loc.name) && options.Allows(world.GetBiome(loc.position.x,loc.position.z))) found.Add(loc);
                if(preparation.Checkpoint()) yield return true;
            }
            eligible[island]=found;
        }
        var selectedIslands = RoadNetworkSelection.Islands(islands, i => eligible[i].Count, percentage);
        Log.LogDebug($"Islands: {islands.Count} total, {selectedIslands.Count} selected (target {IslandRoadPercentage}%; boss islands are not automatically included)");


        preparation.BeginStage("Selecting road destinations",selectedIslands.Count,"islands"); yield return true;
        var work = new GenerationWork(startTime);
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
        // At world load the places whose landings wait for their placing are placed now, before their
        // roads; then their ground is rebuilt and their landings found, before the islands share them.
        // Both before "Reading saved location placements": it indexes the placements, these among them.
        if (placeSitesFirst && SitesFirst != null)
            foreach (var step in RoadPreparation.Own(SitesFirst(work, preparation))) yield return step;
        foreach (var step in PlacedSites.PrepareSteps((x, z) => BiomeBlendedHeight.GetBlendedHeight(x, z, world), preparation)) yield return step;
        if (RoadsLog && PlacedSites.ReadyCount > 0) Log.LogInfo($"  {PlacedSites.ReadyCount} place(s) placed before the roads, their roads to go onto their landings");
        RoadParallel.IslandsRunning = Math.Min(work.Islands.Count, RoadParallel.IslandWorkers);
        foreach(var step in PrepareAndSealSteps(preparation)) yield return step;
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
        internal GenerationWork(DateTime startTime) { StartTime = startTime; }
        internal readonly List<IslandWork> Islands = new();
        internal readonly int Workers = RoadParallel.IslandWorkers;
        internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Finished = new();
        /// <summary>Every island's chosen destinations, in the order the islands were chosen.</summary>
        internal IEnumerable<(string name, Vector3 position, float radius)> AllSelected => Islands.SelectMany(i => i.Locations);
        internal System.Threading.CancellationToken Cancellation;
        internal RoadIslandActivity? Activity;
        internal void Compute()
        {
            System.Threading.Tasks.Parallel.ForEach(Islands,
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Workers, CancellationToken = Cancellation },
                (island, state, number) =>
                {
                    Cancellation.ThrowIfCancellationRequested();
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var token=Activity?.Claim((int)number+1,island.Locations.Count);
                    IslandActivity=token;
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
                    finally { if(Finder!=null) Finder.Activity=null; IslandActivity=null; token?.Finish(); }
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
        Log.LogInfo($"Roads done: {m_roadsGeneratedCount} road(s), " +
                    $"{RoadSpatialGrid.Statistics.LengthText("F1",1000f," km")}, connecting " +
                    $"{m_destinationsConnected} of {m_destinationsSelected} destination(s), " +
                    $"in {elapsed.TotalSeconds:F0}s.");
        int unconnected = m_destinationsSelected - m_destinationsConnected;
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
        if (EarthworkCap > 0f)
            Log.LogInfo($"  Earthwork cap {EarthworkCap:F0} m: {m_earthworkReroutes} road(s) re-routed round deep cuts or fills, {m_earthworkUnavoided} refused as still past it; {m_pullReroutes} re-routed round a stretch held over {HoldPullLimit:F0} m off its profile to a road already laid");
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
            Log.LogInfo($"  Corridor snap (a road alongside another merges onto it, within {CorridorSnap:F0} m): {m_corridorSnapped} waypoint(s) moved, {m_chainsShared} chain(s) of crossings shared, candidates included");
        if (!RoadSpatialGrid.BendFallbacks.IsEmpty)
            Log.LogInfo("  Bend rounding refused with full arcs (plans, trials included): " + string.Join(", ",
                System.Linq.Enumerable.Select(System.Linq.Enumerable.OrderBy(RoadSpatialGrid.BendFallbacks, kv => kv.Key.Item1.ToString() + kv.Key.Item2),
                    kv => $"{(kv.Key.Item1 == RoadSpatialGrid.BendStage.Least ? "least arcs" : "corners kept")} after '{kv.Key.Item2}' {kv.Value}")));
        if (RoadJunctionFillet.Radius > 0f)
            Log.LogInfo($"  Junction fillets (a road's end carried onto the road it joins, its inside corners filled to a {RoadJunctionFillet.Radius:F0} m curve): {m_junctionFills} junction(s)");
        Log.LogInfo(FormattableString.Invariant($"  One road (RoadRide): {RoadRide.RiddenMetres:F0} m of planned road riding the network left to the road laid there, {RoadRide.CrossingsRidden} crossing(s) left to the road that built them, {RoadRide.CaughtAtCommit} ridden stretch(es) caught at the commit"));
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
        if (SearchStats.Enabled)
            foreach (string line in SearchStats.Describe())
                Log.LogInfo("  " + line);
        if (RoadsLog) LogStairs();
        LogSqueeze();
        LogGenerationStats(m_roadsGeneratedCount, elapsed);

        StairStructures.SortCanonical();
        RoadSpatialGrid.FinalizeRoadNetwork();
        
        NetworkCutoff.UseNewNetwork();
        RoadNetworkPersistence.NetworkGenerated();
        m_roadsGenerated = true;
        m_networkDirty = true;
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
    private static void NoteFailure(Vector2 a, Vector2 b, string why)
    {
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
    /// <summary>With <see cref="RoadsLog"/>: road ends outside the Mountains kept close to their place, off its level
    /// (<see cref="EndNotLevel"/>) or short of a placed place's landing (<see cref="PlanEntry"/>), with no staircase onto it.</summary>
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
    private static void LogStairs()
    {
        static string ByCount(IEnumerable<KeyValuePair<string, int>> tally) =>
            string.Join("; ", tally.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key}"));
        Log.LogInfo($"  Staircases: {m_stairRuns} laid on roads; {m_stretchRetries} search(es) again with a staircase's stretch closed, " +
                    $"{m_stretchRetriesKept} of them built with stairs; {RoadStairRuns.FeetBeside} set beside their road ({RoadStairRuns.FeetBesideRefused} could not be); " +
                    $"{m_tightTurnStairs} search(es) again with stairs open at switchback turns too close to fit, {m_tightTurnStairsBuilt} of them built with stairs; " +
                    $"{m_notLevelEnds} road(s) refused for an end not level with its place ({ArrivalLevel:F1} m) and no approach staircase onto it; " +
                    $"{m_closeEnds} end(s) kept close to their place, not level with it nor onto its landing, with no approach staircase outside the mountains; " +
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
        // Never make an arbitrary high embankment merely to hit a platform.
        // The approach search must first find naturally nearby elevation.
        if (platform.HasValue && RoadWater.BuiltDry(platform.Value) &&
            Mathf.Abs(BiomeBlendedHeight.GetBlendedHeight(point.x, point.y, world) - platform.Value) <= ArrivalLevel)
            return BiomeBlendedHeight.GetBlendedHeight(point.x, point.y, world);
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
        try
        {
            // A road to a placed place refused or with no route, aimed at one of its ring points, is the whole road again
            // aimed at the next (AimsAt), the landing's bearing's last: aimed by level alone, a place the bearing's point
            // reached was lost.
            int aims = Math.Max(AimCount(startCenter, startRadius, width), AimCount(endCenter, endRadius, width));
            for (int aim = 0; ; aim++)
            {
                Finder.ClearNoStairs();
                bool built = GenerateRoadCore(startCenter, startRadius, endCenter, endRadius, width, label, networkHints, RoadRetries.AimedAt(aim));
                if (aim > 0 && RoadsLog)
                    m_siteApproaches.AddOrUpdate(built ? "placed first: built aimed again round the ring" : "placed first: refused aimed again round the ring", 1, (_, n) => n + 1);
                if (built || aim + 1 >= aims) return built;
                if (RoadsLog && label != null) Log.LogDebug($"Road {label}: refused aimed at a placed place's ring point {aim + 1} of {aims}; aimed at the next");
            }
        }
        finally { Finder?.ClearNoStairs(); t_inRoad = false; }
    }

    /// <summary>How far out from a placed place's ring point its road's search ends, the road then running
    /// straight in to the ring point: a search cell.</summary>
    public const float LeadInLength = RoadPathfinder.CellSize;
    private static Vector2 LeadIn(PlacedSite site, Vector2 aim) => aim + (aim - site.Centre).normalized * LeadInLength;

    /// <summary>Placed places (and whether stairs were searched) whose ring every turn round found no route to
    /// once this generation, keyed on the places it belongs to (<see cref="RingKey"/>): read and written by the island
    /// worker that owns the place (ContainsKey and the indexer only).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<((int, int) a, (int, int) b, bool stairs), byte> m_ringRoundFailed = new();

    /// <summary>The key a failed ring search is kept under: the placed place it was searched round, or, where both of a
    /// road's ends are placed and both rings were turned together, the pair (either way round): which of the two no road
    /// got to the search cannot say. Keyed on one of them (the start's), a road between two placed places that failed cost
    /// the start its ring search for the rest of the generation, though it was the other place no road got to.</summary>
    internal static ((int, int) a, (int, int) b, bool stairs) RingKey(Vector2? startPlaced, Vector2? endPlaced, bool stairs)
    {
        var none = (int.MinValue, int.MinValue);
        var s = startPlaced is Vector2 sp ? PlaceKey(sp) : none;
        var e = endPlaced is Vector2 ep ? PlaceKey(ep) : none;
        if (s == none) return (e, none, stairs);
        if (e == none) return (s, none, stairs);
        return s.CompareTo(e) <= 0 ? (s, e, stairs) : (e, s, stairs);
    }
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

    /// <summary>Searches made round placed places' rings (tests).</summary>
    internal static int RingTurnSearches;

    /// <summary>Where a road to a placed place ends its search, in the order tried: its trim ring (as
    /// <see cref="TrimPathToRadii"/> cuts it) where that is nearest the place's landing and its level
    /// (<see cref="PlacedSites.Aims"/>).</summary>
    private static IReadOnlyList<Vector2> AimsAt(PlacedSite site, float radius, float width, float turn = 0f) =>
        PlacedSites.Aims(site, KeepOutEdge(site.Centre, radius, width),
            p => RoadCrossingDetector.IsRoadGround(p, GenerationWorld)
                && RoadCrossingDetector.IsRoadGround(LeadIn(site, p), GenerationWorld), turn);

    /// <summary>The <paramref name="aim"/>th of <see cref="AimsAt"/>, its last where it has fewer.</summary>
    private static Vector2 AimAt(PlacedSite site, float radius, float width, int aim, float turn = 0f)
    {
        IReadOnlyList<Vector2> aims = AimsAt(site, radius, width, turn);
        return aims[Math.Min(aim, aims.Count - 1)];
    }

    /// <summary>How many ring points a road's end at <paramref name="centre"/> is aimed at in turn: one where it is not
    /// a placed place.</summary>
    private static int AimCount(Vector2 centre, float radius, float width) =>
        radius > 0f && PlacedSites.TryGetReady(centre, out PlacedSite site) ? AimsAt(site, radius, width).Count : 1;

    /// <summary>A road's searches again, each against its own budget: closing a stretch to stairs (StairStretchRetries), the
    /// one tight-turn search with stairs opened, and banning a crossing (CrossingRetries). One spent never spends another.
    /// <see cref="Aim"/> is which of a placed place's ring points (<see cref="AimsAt"/>) the road is aimed at.</summary>
    internal readonly struct RoadRetries
    {
        public readonly int StairStretches;
        public readonly bool TightTurn;
        public readonly int CrossingBans;
        public readonly int Aim;
        /// <summary>The route the search found before the last crossing was banned: a search that finds it again took no
        /// notice of the ban, and is not searched again.</summary>
        public readonly IReadOnlyList<Vector2>? BeforeBan;
        private RoadRetries(int stairStretches, bool tightTurn, int crossingBans, int aim, IReadOnlyList<Vector2>? beforeBan)
        { StairStretches = stairStretches; TightTurn = tightTurn; CrossingBans = crossingBans; Aim = aim; BeforeBan = beforeBan; }
        public static RoadRetries AimedAt(int aim) => new(0, false, 0, aim, null);
        public RoadRetries NextStairStretch() => new(StairStretches + 1, TightTurn, CrossingBans, Aim, BeforeBan);
        public RoadRetries WithTightTurn() => new(StairStretches, true, CrossingBans, Aim, BeforeBan);
        public RoadRetries NextCrossingBan(IReadOnlyList<Vector2> searched) => new(StairStretches, TightTurn, CrossingBans + 1, Aim, searched);
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

        // A place placed before its roads: searched to its trim ring where that is nearest its landing,
        // not to its centre (PlacedSites.Aims).
        PlacedSite? startSite = startRadius > 0f && PlacedSites.TryGetReady(startCenter, out PlacedSite s0) ? s0 : null;
        PlacedSite? endSite = endRadius > 0f && PlacedSites.TryGetReady(endCenter, out PlacedSite s1) ? s1 : null;
        Vector2? startAim = startSite != null ? AimAt(startSite, startRadius, width, retries.Aim) : null;
        Vector2? endAim = endSite != null ? AimAt(endSite, endRadius, width, retries.Aim) : null;
        Vector2 pathStart = startAim is Vector2 sa ? LeadIn(startSite!, sa) : RoadEndpoint.FindGround(GenerationWorld, startCenter, startRadius);
        Vector2 pathEnd = endAim is Vector2 ea ? LeadIn(endSite!, ea) : RoadEndpoint.FindGround(GenerationWorld, endCenter, endRadius);
        List<Vector2>? path = Finder.FindPath(pathStart, pathEnd);
        // The landing's side out of reach (its lead-in on a cliff, in water or shut in): further round the
        // ring, then searched to the place as any road is, the road onto its landing then leading round
        // from where it arrives, as a staircase or a road is led round a place's rock.
        // Each of those searches that finds nothing spends its whole budget: where every turn found
        // nothing for a place once, its later roads go straight to the search to the place.
        var roundKey = RingKey(startSite?.Centre, endSite?.Centre, Finder.Stairs);
        bool round = (startAim != null || endAim != null) && (path == null || path.Count < 2)
            && !m_ringRoundFailed.ContainsKey(roundKey);
        foreach (float turn in new[] { 45f, -45f, 90f, -90f })
        {
            if (!round || (path != null && path.Count >= 2)) break;
            if (startSite != null) { startAim = AimAt(startSite, startRadius, width, 0, turn); pathStart = LeadIn(startSite, startAim.Value); }
            if (endSite != null) { endAim = AimAt(endSite, endRadius, width, 0, turn); pathEnd = LeadIn(endSite, endAim.Value); }
            System.Threading.Interlocked.Increment(ref RingTurnSearches);
            path = Finder.FindPath(pathStart, pathEnd);
        }
        if (round && (path == null || path.Count < 2)) m_ringRoundFailed[roundKey] = 0;
        if ((path == null || path.Count < 2) && (startAim != null || endAim != null))
        {
            startAim = endAim = null;
            pathStart = RoadEndpoint.FindGround(GenerationWorld, startCenter, startRadius);
            pathEnd = RoadEndpoint.FindGround(GenerationWorld, endCenter, endRadius);
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
        if (startRadius > 0f) startRadius = KeepOutEdge(startCenter, startRadius, width);
        if (endRadius > 0f) endRadius = KeepOutEdge(endCenter, endRadius, width);
        // The one planning pipeline (PlanFromPath), as the earthwork re-route plans too.
        var finder = Finder!;
        var searched = new List<Vector2>(path);
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
        if (plans != null && EarthworkCap > 0f)
        {
            // A road the game cannot build as planned -- a cut or fill past the
            // terrain limit is clamped, so the surface tilts across the road
            // and jumps between the clamp and the plan -- is
            // searched again with the cells round the offending points priced
            // up, up to EarthworkRetries times; a plan with none past the
            // limit is kept, and if none is found the road is refused.
            var where = new List<Vector2>();
            int past = PastTerrainLimit(plans, EarthworkCap, where) + InvalidCrossings(crossings, where) + HeldRidges(plans, where);
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
                int pastRetry = PastTerrainLimit(retry.Value.plans, EarthworkCap, whereRetry) + InvalidCrossings(retry.Value.crossings, whereRetry) + HeldRidges(retry.Value.plans, whereRetry);
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
                bool ridge = !water && !throughDeck && HeldRidges(plans, null) > 0 && PastTerrainLimit(plans, EarthworkCap, new List<Vector2>()) == 0;
                if (!water && !throughDeck) System.Threading.Interlocked.Increment(ref m_earthworkUnavoided);
                plans = null;
                RoadSpatialGrid.LastRefusal = water
                    ? "shallow water too long or deep to cross"
                    : throughDeck ? "bridge deck through the ground between its banks"
                    : ridge ? $"a junction ridge: held to a road on more than {JunctionFillLimit:F0} m of fill"
                    : $"earthwork past the {EarthworkCap:F0} m terrain limit";
            }
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
            RoadSpatialGrid.LastRefusal = "a viaduct the built road does not allow (" + lastWhy + ")";
        }
        if (plans != null && DanglingBridge(plans, crossings, path[0], path[path.Count - 1], out var dangling))
        {
            // A bridge must have road at both ends; one was found stopping in
            // 3 m of water.
            Log.LogWarning($"Bridge with no road beyond it on {label}: {dangling.FromBank} -> {dangling.ToBank}; road refused");
            plans = null;
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
                if (PlanEntry(site, atStart, path, plans, stairs, crossings, width, out var entryRoad, out var entryStairs, out string noEntry))
                {
                    entries.Add((site, entryRoad, entryStairs));
                    if (RoadsLog) m_siteApproaches.AddOrUpdate(entryRoad != null ? "placed first: road onto its landing" : "placed first: staircase onto its landing", 1, (_, n) => n + 1);
                    continue;
                }
                if (!ApproachStairsAt(site.Centre, site.Record.Prefab))
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
                RoadSpatialGrid.LastRefusal = "no way onto a placed place's landing";
                break;
            }
        // No road under a viaduct's deck laid before it (the built-result check, StructureEnds.UnderTheDeck).
        if (plans != null && UnderALaidDeck(plans, out Vector2 underAt) is string underDeck)
        {
            plans = null;
            RoadSpatialGrid.LastRefusal = underDeck;
            if (RoadsLog) Log.LogDebug(FormattableString.Invariant($"Road {label ?? "(unlabelled)"}: {underDeck} at ({underAt.x:F1}, {underAt.y:F1}); refused"));
        }
        // No road crosses another, nor runs beside one at its height, nor on a staircase's flight but onto a landing level
        // with the row it reaches (MeetingRefusal).
        RoadStairRuns.FlightLanding? landing = null;
        if (plans != null && MeetingRefusal(plans, out Vector2 meetsAt, out landing, landOnFlight: true) is string meetingRefused)
        {
            plans = null;
            RoadSpatialGrid.LastRefusal = meetingRefused;
            if (RoadsLog) Log.LogDebug(FormattableString.Invariant($"Road {label ?? "(unlabelled)"}: {meetingRefused} at ({meetsAt.x:F1}, {meetsAt.y:F1}); refused"));
        }
        if (plans != null && landing != null) LandOnTheFlight(plans, landing);
        // A road whose end at its place does not arrive level with it, and from which no approach staircase onto it can be
        // laid, does not reach it (EndNotLevel): refused, even at the cost of a POI; the planner tries its other candidates.
        var approaches = plans == null ? new List<RoadStairSpan>()
            : SiteApproaches(path, plans, stairs, startCenter, startSite != null ? 0f : askedStartRadius,
                endCenter, endSite != null ? 0f : askedEndRadius, startAtPlace, endAtPlace);
        if (plans != null && EndNotLevel(path, plans, stairs, approaches, startCenter, askedStartRadius, endCenter, askedEndRadius,
                startAtPlace && startSite == null, endAtPlace && endSite == null) is string notLevel)
        {
            if (RoadsLog)
            {
                System.Threading.Interlocked.Increment(ref m_notLevelEnds);
                if (label != null) Log.LogDebug($"Road {label}: {notLevel}");
            }
            plans = null;
            RoadSpatialGrid.LastRefusal = NotLevelRefusal;
        }
        // Refused with stairs on it, it is refused: stairs are always on, and a road is never searched again without them
        // (there is no flag for them, nor code to work
        // around them). The planner tries its other candidates.
        if (plans == null)
        {
            string why = RoadSpatialGrid.LastRefusal ?? "other (crossing or footprint)";
            m_refusalReasons.AddOrUpdate(why, 1, (_, n) => n + 1);
            if (label != null)
                Log.LogDebug($"Road could not be built ({why}), destination left unconnected: {label}");
            NoteFailure(startCenter, endCenter, "refused: " + why);
            System.Threading.Interlocked.Increment(ref m_roadsRefusedForGrade);
            return false;
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
        RoadSpatialGrid.CommitRoad(own, committed: plan => PieceCommitted?.Invoke(plan, label), decks: decks, ownChecked: true);
        foreach (var fill in fills) RoadSpatialGrid.CommitLevelled(fill.Points, fill.Heights, fill.Width);
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
                PlacedSites.MarkLanded(entrySite.Centre);
                if (RoadsLog && label != null) Log.LogDebug($"Road {label}: carried on onto the landing, {entryStairs.Kit.Name} staircase {entryStairs.Plan.Rise:F0} m {(entryStairs.Plan.Reversed ? "down" : "up")}");
            }
        }
        foreach (RoadStairSpan approach in approaches)
        {
            // An approach crosses its place's footprint by design: it is a staircase at its place, as an entry is.
            JoinNetwork(approach.FromPoint, approach.ToPoint, approach.Plan, approach.Kit, atPlace: true);
            if (RoadsLog && label != null) Log.LogDebug($"Road {label}: site approach staircase, {approach.Kit.Name} {approach.Plan.Rise:F0} m {(approach.Plan.Reversed ? "down" : "up")}");
        }
        foreach (RoadStairSpan stair in stairs)
        {
            StairStructures.MarkLaid(path.GetRange(stair.FromIndex, stair.ToIndex - stair.FromIndex + 1));
            if (stair.Existing) continue;
            JoinNetwork(path[stair.FromIndex], path[stair.ToIndex], stair.Plan, stair.Kit, atPlace: false);
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
                if (!onCrossing) networkHints.Add(path[i]);
            }
        m_islandRoads++;
        int roadNumber;
        // Each crossing's kind decided once, as it is stored (BridgeStructures.DecideKind): a viaduct, or a deck whose kit
        // is chosen when it is first built. Decided before the lock every island records its roads under: a viaduct's
        // test samples its piers and ends.
        foreach (RoadCrossing c in crossings) BridgeStructures.DecideKind(c, GenerationWorld);
        lock (m_recordGate)
        {
            m_roadCrossings.AddRange(crossings);
            roadNumber = ++m_roadsGeneratedCount;
        }
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
        public static float CutWidth => BridgeLayout.DeckHalfWidth * 2f;
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
                        Vector2 at = end.Edge - end.Up * (BridgeLayout.StairHalfRun + k * BridgeLayout.StairRun);
                        ground.Cuts.Add(new Vector3(at.x, end.Height - BridgeLayout.StairRise * (k + 1), at.y));
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
        if (RoadSpatialGrid.TryGetRoadWithin(path[start], 1f, out _)) return -1;
        float along = 0f;
        for (int i = start + step; i >= 0 && i < path.Count; i += step)
        {
            along += Vector2.Distance(path[i - step], path[i]);
            if (along > StubLength) return -1;
            if (RoadSpatialGrid.TryGetRoadWithin(path[i], 1f, out _)) return i;
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
        float band = RoadWidth + 2f * RoadConstants.TerrainBlendMargin;
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
    internal static float CorridorSnap = DebugSwitches.Number("CORRIDOR_SNAP", 14f, 0f, 40f);
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
                EarthworkCap > 0f ? EarthworkCap : float.PositiveInfinity) is RoadStairRuns.FlightLanding fits)
        {
            landed = fits;
            return null;
        }
        return OnAFlightRefusal;
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
        var lines = RoadRide.Around(points, 4f);
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
        // a protected site. Before trimming, only the route's own endpoint
        // footprints are exempt, just as they are during the search.
        float clearance = RoadSiteProtection.Clearance(width ?? RoadWidth);
        for (int i = 1; i < result.Count; i++)
            if (RoadSiteProtection.BlocksSegment(result[i - 1], result[i], clearance, path[0], path[path.Count - 1]))
                return path;
        return result;
    }

    private static int m_corridorSnapped;
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
        if (EarthworkCap > 0f && PastTerrainLimit(plans, EarthworkCap, where) > 0) { at = where[0]; return $"earthwork past the {EarthworkCap:F0} m terrain limit"; }
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
    internal static float EarthworkCap = 8f;

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
                if (Mathf.Abs(plan.Heights[i] - BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world)) > cap)
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
    /// the ring points the road's own search was aimed at (<see cref="AimAt"/>, turned where it went round the ring; null
    /// where it searched to the place), which the re-route comes in to head on as the road did. Worked out again here, at
    /// no turn and with only the ring point on road ground, they could differ, and the re-route then lost the head-on
    /// ring point.</summary>
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
        if (startPlaced && startAim is Vector2 sa) path.Insert(0, sa);
        if (endPlaced && endAim is Vector2 ea) path.Add(ea);
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
            result.Plans = PlanRoadPathWithCrossings(on, result.Crossings, width, startGround, endGround, stairs);
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
        // plans, its earthwork within the cap and no crossing, and it meets the network as any road must (MeetingRefusal)
        // near the stretch; a hump the cap or the network refuses stays stairs, and with no cap none is tried. True where the
        // road is planned so.
        bool AcrossTheHumps(List<Vector2> on, bool atStart, bool atEnd)
        {
            // Only within an earthwork cap: with none (0), nothing bounds the cut a hump's road would take.
            if (EarthworkCap <= 0f || finder.LastStairSteps.Count == 0 || GenerationWorld == null) return false;
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
                PastTerrainLimit(result.Plans, EarthworkCap, past);
                if (past.Exists(Near)
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
        List<RoadStairSpan>? stairs = null)
    {
        using var fitting = new RoadFittingActivity(IslandActivity);
        var plans = PlanPieces(path, crossings, width, startGround, endGround, stairs);
        // A shallow ford is a style, never a reason a road cannot be built: its banks hold their
        // natural heights and split the road, and a short piece between two held ends can be too
        // steep for the cap (measured: a road refused at a 17 m piece 7 m high between a pool's
        // bank and a turn). Plan again with the valid pools walked;
        // the dry-land floor raises them. The crossings list is updated in place for the caller.
        if (plans == null && crossings.Exists(c => c.Shallow && !c.Invalid))
        {
            var walked = crossings.FindAll(c => !(c.Shallow && !c.Invalid));
            var retry = PlanPieces(path, walked, width, startGround, endGround, stairs);
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
        float cap = EarthworkCap > 0f ? EarthworkCap : float.PositiveInfinity;
        return StructureEnds.MeetEnds(path, crossings, GenerationWorld, cap, IsStep);
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
    /// staircase is laid.
    /// </summary>
    private static void JoinNetwork(Vector2 from, Vector2 to, StairPlan plan, StairKit kit, bool atPlace)
    {
        RoadSpatialGrid.CommitCuts(atPlace ? RoadStairRuns.CutsOutsideFootprints(plan) : plan.Cuts, StairLayout.CutRadius * 2f);
        var run = StairRun.FromPlan(from, to, RoadStairRuns.Width, plan, kit);
        run.KeepsSiteRocks = atPlace;
        AddToNetwork(run, plan, kit);
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
        }
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
        List<(StructureEnds.End near, StructureEnds.End far, float deck, Vector2 centre, float reach)> decks;
        lock (m_laidDecks) { if (m_laidDecks.Count == 0) return null; decks = new(m_laidDecks); }
        foreach (var d in decks)
            foreach (var (p, h, w) in PlanPointsNear(plans, d.centre, d.reach))
                if (StructureEnds.UnderTheDeck(p, h, w, d.near, d.far, d.deck, DvergrViaduct.HalfWidth))
                { at = p; return "passes under a viaduct's deck"; }
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
                lock (m_laidDecks) m_laidDecks.Add((near, far, DvergrViaduct.DeckHeight(c, GenerationWorld), c.Center, c.Width * 0.5f + DvergrViaduct.HalfWidth + 4f));
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
    private static readonly List<(StructureEnds.End near, StructureEnds.End far, float deck, Vector2 centre, float reach)> m_laidDecks = new();

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

    private static List<RoadSpatialGrid.PlannedPath>? PlanPieces(
        List<Vector2> path, List<RoadCrossing> crossings, float width, float? startGround, float? endGround,
        List<RoadStairSpan>? stairs = null)
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
        try
        {
            for (int i = 0; i < pieces.Count; i++)
            {
                RoadSpatialGrid.PlannedPath? plan = RoadSpatialGrid.PlanRoadPath(
                    pieces[i].points, width, GenerationWorld,
                    pieces[i].startHeight ?? (i == 0 ? startGround : null),
                    pieces[i].endHeight ?? (i == pieces.Count - 1 ? endGround : null),
                    pieces[i].followTerrain, pieces[i].minHeight, ground);
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
        var (carried, dropped) = StairStructures.SettleOnFinishedGround(StructureGround.Vertices(world), byPrefab);
        if (byPrefab != null)
            Log.LogInfo($"  Staircases on the finished ground: {carried} pillar(s) carried down under earthwork; pieces wholly under it, not placed: {dropped}" +
                (byPrefab.Count == 0 ? "" : " (" + string.Join(", ", byPrefab.Select(kv => $"{kv.Value} {kv.Key}")) + ")"));
    }

    /// <summary>
    /// The road from <paramref name="path"/>'s end at a place placed before its roads onto the place's
    /// landing, on its placed ground: a road where one gets there (<see cref="RoadSiteEntry"/>), else, in the Mountains
    /// or at a boss's entrance (<see cref="ApproachStairsAt"/>), a staircase (<see cref="RoadStairRuns.PlanPlacedSiteApproach"/>), carried on from the top row of the
    /// staircase the road ends on where it ends on one. False, with <paramref name="why"/>, where neither does.
    /// </summary>
    private static bool PlanEntry(PlacedSite site, bool atStart, List<Vector2> path, List<RoadSpatialGrid.PlannedPath> plans,
        List<RoadStairSpan> stairs, List<RoadCrossing> crossings, float width, out RoadSpatialGrid.PlannedPath? road, out RoadStairSpan? span, out string why)
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
        // it does. A road that fails on its own is left to the road's own check, which names it.
        bool checkMeeting = MeetingRefusal(plans, out _) == null;
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
            string? Meets(RoadSpatialGrid.PlannedPath? entryRoad, RoadStairSpan? entryStairs) =>
                checkMeeting && MeetingRefusal(WithEntry(atStart, cutPlans, end, entryRoad, entryStairs), out Vector2 at) is string meeting
                    ? FormattableString.Invariant($"{meeting} at ({at.x:F1}, {at.y:F1})") : null;
            if (!PlanEntryFrom(site, atStart, p, cutPlans, stairs, width, shift, Meets, out road, out span, out string whyHere))
            {
                if (back == 0f) why = whyHere;
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
        road = null;
        span = null;
        why += FormattableString.Invariant($" (also from up to {EntryCutBacks[EntryCutBacks.Length - 1]:F0} m back along the road)");
        return false;
        }
        finally { if (SearchStats.Enabled) SearchStats.Record(SearchStats.LandingEntry, why == "", false, tries, began); }
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
            RoadEnd = end, RoadHeight = height, Arriving = RoadStairRuns.ArrivingTravel(path, end, atStart), Centre = site.Centre, RoadTail = tail,
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
        if (!ApproachStairsAt(site.Centre, site.Record.Prefab)) { why = noRoad + "; stairs: none outside the mountains"; return false; }
        StairContinuation? top = onStairs == null ? null
            : RoadStairRuns.TopAt(end, height, StairRun.FromPlan(onStairs.FromPoint, onStairs.ToPoint, RoadStairRuns.Width, onStairs.Plan).Pieces);
        // The same start onto the same landing lays the same staircase: a failed one is not laid out again.
        // Roads to a place come in on its one aimed ring point, and each failing layout cost seconds: one
        // place took 36 of them, 34 s (the game's slowest islands 570 s).
        var failedKey = (PlaceKey(site.Centre), Mathf.RoundToInt(end.x / EntryStartCell), Mathf.RoundToInt(end.y / EntryStartCell),
            Mathf.RoundToInt(height), top.HasValue);
        string noStairs;
        // And a place whose landing no staircase reached from EntryStairsPerPlace starts gets no more: its
        // roads may still go on as road. Three places took 84 layouts and 65 s there.
        m_entryStairsTried.TryGetValue(failedKey.Item1, out int triedHere);
        if (m_entryStairsFailed.TryGetValue(failedKey, out string? failed)) { span = null; noStairs = failed; }
        else if (triedHere >= EntryStairsPerPlace) { span = null; noStairs = FormattableString.Invariant($"no staircase from {triedHere} other starts, none laid out again"); }
        else
        {
            System.Threading.Interlocked.Increment(ref EntryStairLayouts);
            span = RoadStairRuns.PlanPlacedSiteApproach(request, r.Yaw, r.Ops, site.Ground, (x, z) => world.GetBiome(x, z), world.GetSeed(),
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
            string conflict = RoadStairRuns.SiteConflict(span.Plan, site.Centre, path, index, site.Ground, world, stairs,
                onStairs != null || RoadStairRuns.EndsOnStairs(end, stairs) ? end : null);
            // Laid out and clear of the place, but meeting the network as the rules forbid: as a failed layout, not laid
            // out again from this start, and counted toward the place's EntryStairsPerPlace.
            if (conflict == "" && meets(null, span) is string stairsMeet)
            {
                conflict = stairsMeet;
                m_entryStairsFailed[failedKey] = stairsMeet;
                m_entryStairsTried[failedKey.Item1] = triedHere + 1;
            }
            if (conflict == "") { why = ""; return true; }
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
            if (!ApproachStairsAt(centre, PrefabAt(centre)))
            {
                if (RoadsLog) m_siteApproaches.AddOrUpdate("none outside the mountains", 1, (_, n) => n + 1);
                return;
            }
            int index = atStart ? 0 : path.Count - 1;
            if (RoadEnd(atStart, path, plans, stairs) is not var (end, height, _)) return;
            var both = new List<RoadStairSpan>(stairs);
            both.AddRange(laid);
            RoadStairSpan? span = RoadStairRuns.LaySiteApproach(end, height, RoadStairRuns.ArrivingTravel(path, end, atStart), centre,
                LocationLevelling.CentreHeight(centre, world), LocationLevelling.OpsAt(centre), LocationLevelling.FacesDownhill(centre),
                path, index, both, world, out string why);
            // A place placed before its road was carried on onto its landing (PlanEntry); one that could not be
            // placed in time keeps this end.
            if (span != null) laid.Add(span);
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
        public List<(string name, Vector3 position, float radius)> BossLocations;
        public List<(string name, Vector3 position, float radius)> AllLocations;
    }

    private static IEnumerable<bool> GatherLocationSteps(ZoneSystem zones,RoadPreparation preparation,Action<LocationData?> complete)
    {
        // Snapshot the live ValueCollection in one main-thread operation; no live enumerator crosses a frame.
        var source=zones.GetLocationList()?.ToArray();
        preparation.BeginStage("Copying world locations",source?.Length ?? 0,"locations"); yield return true;
        if(source==null || source.Length==0) { Log.LogWarning("No location instances found"); complete(null); yield break; }
        Vector3? spawn=null; float radius=0;
        var bosses=new List<(string name,Vector3 position,float radius)>();
        var all=new List<(string name,Vector3 position,float radius)>();
        foreach(var location in source)
        {
            var item=(location.m_location.m_prefab.Name!,location.m_position,location.m_location.m_exteriorRadius);
            if(item.Item1=="StartTemple") { spawn=item.Item2; radius=item.Item3; all.Add(item); }
            // HARD RULE: a place the game leaves for a player to find is never a destination (PlacedSites.PlayerDecides).
            else if(PlacedSites.PlayerDecides(item.Item1,location.m_location.m_unique,location.m_location.m_iconPlaced,location.m_location.m_iconAlways)) { }
            else { all.Add(item); if(BossLocationNames.Contains(item.Item1)) bosses.Add(item); }
            if(preparation.Checkpoint()) yield return true;
        }
        if(!spawn.HasValue) { Log.LogWarning("Could not find spawn point (StartTemple)"); spawn=Vector3.zero; }
        Log.LogDebug($"Found spawn at {spawn.Value}, {bosses.Count} boss locations, {all.Count} total locations");
        complete(new LocationData {SpawnPoint=spawn.Value,SpawnRadius=radius,BossLocations=bosses,AllLocations=all});
    }

    private static LocationData? GatherLocationData()
    {
        LocationData? result=null;
        var zones=ZoneSystem.instance;
        using var preparation=new RoadPreparation(p=>GatherLocationSteps(zones,p,value=>result=value));
        preparation.Drain();
        return result;
    }

    private static List<(string name, Vector3 position, float radius)> GetLocationsOnIsland(
        Island island, List<(string name, Vector3 position, float radius)> allLocations)
    {
        var result = new List<(string name, Vector3 position, float radius)>();
        foreach (var loc in allLocations)
        {
            if (island.ContainsPoint(loc.position) && IsRoadLocation(loc.name)
                && (GenerationWorld == null || GenerationOptions.Allows(
                    GenerationWorld.GetBiome(loc.position.x, loc.position.z))))
                result.Add(loc);
        }
        return result;
    }

    /// <summary>Hildir's quest sites: her cave, crypt and tower. Not her camp (unique, a vendor).</summary>
    internal static bool IsHildirQuestSite(string name) => name == "Hildir_cave" || name == "Hildir_crypt" || name == "Hildir_plainsfortress";

    /// <summary>The one rule for Hildir's quest sites: roads go to them only with their setting on (config
    /// Locations.AttemptRoadsToHildirQuestSites). Then they are required destinations, placed before their roads; off, they
    /// are left for a player, as the game leaves them.</summary>
    internal static bool HildirRoads(string name) => IsHildirQuestSite(name) && NetworkOptions.HildirQuestSites;

    private static bool IsRoadLocation(string locationName)
    {
        if (IsHildirQuestSite(locationName)) return HildirRoads(locationName);
        return BossLocationNames.Contains(locationName) ||
               LocationPriorities.ContainsKey(locationName) ||
               IsCustomLocation(locationName);
    }

    private static bool IsCustomLocation(string name) => RegisteredLocationNames.Contains(name)
        || ConfiguredLocationNames.Contains(name);

    private static bool IsRequiredLocation(string name) => BossLocationNames.Contains(name)
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

    private static int GetMaxLocationsForIsland(Island island) =>
        RoadNetworkSelection.Quota(island.ApproxArea, MaxLocationsPerIsland);

    private static List<(string name, Vector3 position, float radius)> SelectLocations(
        List<(string name, Vector3 position, float radius)> candidates, int maxCount, float area = 0) =>
        RoadNetworkSelection.Destinations(candidates, maxCount, area, GetLocationPriority,
            IsRequiredLocation, GenerationWorld?.GetSeed() ?? 0, GenerationOptions);

    private static int GetLocationPriority(string locationName)
    {
        if (locationName == RoadCoastalLandings.Name) return 60;
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

    private static bool PrepareAndSeal()
    {
        RoadSiteProtection.Prime();
        LocationLevelling.Prime?.Invoke();
        if (RoadSiteProtection.InstalledButUnprepared)
        {
            Log.LogError("the site footprint index could not be built, so no road could avoid a POI. " +
                         "No roads were generated. This is a gap in preparation, not in the world.");
            return false;
        }
        LocationLevelling.Seal();
        return true;
    }

    private static void ReportMissesAfterSealing()
    {
        if (LocationLevelling.MissesAfterSealing > 0)
            Log.LogError($"{LocationLevelling.MissesAfterSealing} lookup(s) arrived after preparation closed; " +
                         "roads near those places meet natural terrain. See the errors above for what was missing.");
    }

    /// <summary>
    /// Clear the network and regenerate roads for the single island containing
    /// worldPos: the same island selection, location selection and pathfinding
    /// as the global pass, restricted to one island. A validation loop that
    /// iterates on one site runs in seconds instead of a whole-world generation.
    /// </summary>
    public static bool RegenerateIslandAt(Vector3 worldPos, out string summary)
    {
        bool result = false;
        string detail = "";
        RoadLifecycleManager.RunSynchronous(() => result = RegenerateIslandCore(worldPos, out detail));
        summary = detail;
        return result;
    }

    private static bool RegenerateIslandCore(Vector3 worldPos, out string summary)
    {
        if (!RoadLifecycleManager.HoldsTheNetwork)
        {
            summary = "Roads are generated only on the server; this peer holds no road network";
            return false;
        }
        if (GenerationWorld == null || ZoneSystem.instance == null)
        {
            summary = "World not ready";
            return false;
        }

        RegisterConfiguredLocations();

        var locations = GatherLocationData();
        if (locations == null)
        {
            summary = "No location data available";
            return false;
        }

        var islands = IslandDetector.DetectRoadIslands();
        Island? island = islands.FirstOrDefault(i => i.ContainsPoint(worldPos));
        if (island == null)
        {
            summary = $"No island at ({worldPos.x:F0},{worldPos.z:F0})";
            return false;
        }

        var islandLocations = GetLocationsOnIsland(island, locations.Value.AllLocations);
        if (islandLocations.Count == 0)
        {
            summary = $"Island {island.Id} has no road-eligible locations";
            return false;
        }

        var selected = SelectIslandDestinations(island, islandLocations, GetMaxLocationsForIsland(island));

        DateTime startTime = DateTime.Now;
        bool locationsWereReady = m_locationsReady;
        ResetData();
        m_locationsReady = locationsWereReady;
        NetworkCutoff.BeginNewNetwork();
        ResetGenerationState(GenerationWorld, GenerationOptions);

        // One island: its connection pricing may use every worker, so the
        // same main-thread preparation and seal as a whole-world generation.
        RoadParallel.IslandsRunning = 1;
        if (!PrepareAndSeal())
        {
            ReleasePathfinders();
            summary = "Preparation failed: the site footprint index could not be built (see the log)";
            return false;
        }
        try
        {
            if (island.ContainsPoint(locations.Value.SpawnPoint))
                GenerateIslandRoads(island, selected, locations.Value.SpawnPoint, locations.Value.SpawnRadius);
            else
                GenerateIslandRoads(island, selected);
        }
        finally { LocationLevelling.Unseal(); }
        ReportMissesAfterSealing();
        DropBuriedPieces();
        // As the whole-world pass does per island; without it the summary
        // reported 0/0 destinations.
        CountDestinationsReached(island, selected);

        StairStructures.SortCanonical();
        RoadSpatialGrid.FinalizeRoadNetwork();
        LogTerrainMemoCounters();
        NetworkCutoff.UseNewNetwork();
        RoadNetworkPersistence.NetworkGenerated();
        m_roadsGenerated = true;
        m_networkDirty = true;
        ReleasePathfinders();
        // Same as after global generation: without the metadata object the
        // save path has nowhere to put the network and logs an error instead.
        RoadNetworkPersistence.EnsureMetadataInstance();

        TimeSpan elapsed = DateTime.Now - startTime;
        summary =
            $"Island {island.Id} ({island.ApproxArea / 1_000_000f:F1}km²): " +
            $"{selected.Count} locations, {m_roadsGeneratedCount} roads, " +
            $"{RoadSpatialGrid.Statistics.LengthText()} in {elapsed.TotalSeconds:F1}s; " +
            $"{m_destinationsConnected}/{m_destinationsSelected} destinations connected, " +
            $"{m_roadsWithNoRoute} attempt(s) found no route" +
            (m_refusalReasons.IsEmpty ? "" : "; refused when built: " + string.Join("; ",
                System.Linq.Enumerable.Select(System.Linq.Enumerable.OrderByDescending(m_refusalReasons, kv => kv.Value), kv => $"{kv.Value} {kv.Key}")));
        // Also to the log: a console answer is lost when the command outlives
        // the caller's timeout, and a check needs a line to wait for.
        Log.LogInfo($"Island regenerated: {summary}");
        return true;
    }

    /// <summary>Told when the network is reset (the world unloaded, or regenerated).</summary>
    public static Action? OnReset;

    public static void Reset()
    {
        bool cancelled = RoadLifecycleManager.InitialGenerationActive;
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
        RoadSiteProtection.Reset();
    }

    /// <summary>The counters and per-place notes of a generation (logs, reports, retry budgets per place).</summary>
    private static void ResetGenerationCounters()
    {
        RoadTerrainSamples.ResetTotals();
        SearchStats.Reset();
        m_destinationsSelected = 0;
        m_destinationsConnected = 0;
        m_roadsGeneratedCount = 0;
        m_roadsRefusedForGrade = 0;
        m_refusalReasons.Clear();
        m_lastFailure.Clear();
        ClearStairNotes();
        m_builtTo.Clear();
        m_entryStairsFailed.Clear();
        m_entryStairsTried.Clear();
        m_ringRoundFailed.Clear();
        EntryStairLayouts = 0;
        m_roadsWithNoRoute = 0;
        m_earthworkReroutes = 0;
        m_pullReroutes = 0;
        m_offLandEnds = 0;
        RoadCrossingDetector.DeckLineWalkedIn = RoadCrossingDetector.DeckLineRefused = RoadCrossingDetector.DeckLineSpanDropped = 0;
        RoadPitches.ResetCounters();
        m_corridorSnapped = 0;
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
    public static bool TryLoadGlobalRoadData()
    {
        if (IsGenerating) return false;
        var bridgeZones = new HashSet<Vector2s>();
        bool loaded = RoadNetworkPersistence.TryLoadGlobalRoadData(m_roadStartPoints, m_roadCrossings, bridgeZones, m_coastalLandings);
        if (loaded)
        {
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
        RoadNetworkPersistence.SaveClearedZones(VegetationClearing.Version, VegetationClearing.ClearedZones);
    }

    #endregion
}
