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
    private static void CountDestinationsReached(
        List<(string name, Vector3 position, float radius)> selected)
    {
        bool[] served = ServedDestinations(selected);
        int reached = 0; foreach (bool s in served) if (s) reached++;
        if (RoadsLog) LogUnconnected(selected);
        IslandActivity?.Measured(served);
        System.Threading.Interlocked.Add(ref m_destinationsSelected, selected.Count);
        System.Threading.Interlocked.Add(ref m_destinationsConnected, reached);
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

    /// <summary>Whether the network reaches this place: is there a road point
    /// where a road TO it would have stopped, give or take a path-grid step.
    ///
    /// The reach is the trimmer's own radius, not the location's exterior
    /// radius. Roads are cut back to RadiusAt() -- which is the larger of the
    /// exterior radius and how far the site's own terrain modifiers reach --
    /// plus half the road's width and two metres. On a site whose levelling
    /// reaches well beyond its exterior radius, a road that served it
    /// perfectly well stops far outside that radius, and measuring from the
    /// exterior radius calls it unreached.
    ///
    /// That is not hypothetical: measuring from the exterior radius counted
    /// 154 of 292 in game against 224 by the planner's own edges, while
    /// offline it counted 247 and looked right. Offline has no terrain radius
    /// to be wrong about -- the harness supplies exterior radii only -- so the
    /// two agreed there for a reason that does not hold in the game.</summary>
    internal static bool Reached(Vector3 centre, float radius, string? name = null)
    {
        Vector2 flat = new Vector2(centre.x, centre.z);
        float reach = radius > 0f
            ? RoadSiteProtection.RadiusAt(flat, radius) + RoadWidth * 0.5f + 2f
            : radius;
        // Only network an approach could climb from counts (MaxApproachClimb): a staircase
        // 45 m under Hildir's cave was counted as reaching it.
        float? place = radius > 0f ? PlaceHeight(flat) : null;
        // A staircase is the network too: a road that reaches a place by one ends on its top or foot,
        // with no road there (MountainCave02 -2560,1536: its staircase's top 38.8 m out, the
        // nearest road 52 m, counted unreached).
        // A place whose own road was built as its last resort (BuildLastResort) is reached by it:
        // the planner built it on purpose as the best the network has, so the count says what the
        // planner did, and the summary tallies how many places that serves.
        float lowest = place.HasValue && !m_lastResortServed.ContainsKey(PlaceKey(flat)) ? place.Value - MaxApproachClimb : float.NegativeInfinity;
        return RoadSpatialGrid.TryGetRoadWithin(flat, reach + RoadPathfinder.CellSize, out _, minHeight: lowest)
            || StairStructures.PiecesNear(centre, reach + RoadPathfinder.CellSize).Exists(p => p.Kind != StructurePieceKind.StairSupport
                && p.Position.y >= lowest);
    }

    /// <summary>
    /// The most a road's end at a place may stand under the place's own ground and still reach it: what an approach staircase can climb from there. Its reach is
    /// <see cref="RoadStairRuns.SiteReach"/> (40 m) at a flight's 0.5 grade, 20 m straight up;
    /// the 56 approaches laid in runs 51-57 climbed at most 16-18 m (Hildir's cave 11 m in run
    /// 55, the frost cave at -2560,1536 needs about 15 m). Settable for tests.
    /// </summary>
    internal static float MaxApproachClimb = 20f;

    /// <summary>The ground a place's approach arrives at (its platform, never below its root), or
    /// null where its levelling is not known.</summary>
    internal static float? PlaceHeight(Vector2 centre)
    {
        var world = GenerationWorld;
        if (world == null) return null;
        IReadOnlyList<LevelOp>? ops = LocationLevelling.OpsAt(centre);
        return ops == null ? null : LocationLevelling.ApproachHeight(LocationLevelling.CentreHeight(centre, world), ops);
    }

    /// <summary>Why a road's end at one of its places stands further under the place than an
    /// approach climbs, or null. The end is where walking ends: the last road point, or the staircase the road ends on.</summary>
    private static string? EndOutOfClimb(List<Vector2> path, List<RoadSpatialGrid.PlannedPath> plans, List<RoadStairSpan> stairs,
        Vector2 startCenter, float startRadius, Vector2 endCenter, float endRadius, bool startAtPlace, bool endAtPlace,
        out Vector2 shortOf, out float shortBy)
    {
        Vector2 at = default;
        float by = 0f;
        string? At(bool atStart, Vector2 centre, float radius, bool atPlace)
        {
            if (!atPlace || radius <= 0f || plans.Count == 0) return null;
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
            // Under only: a road ending over its place (22-23 m over sunken Mistlands entrances on
            // RoadSeedB) arrives at another kind of ground, and refusing those lost four places.
            float under = place - height;
            if (under <= MaxApproachClimb) return null;
            (at, by) = (centre, under);
            return FormattableString.Invariant($"ends {under:F0} m under its place, past an approach's {MaxApproachClimb:F0} m climb");
        }
        string? why = At(true, startCenter, startRadius, startAtPlace) ?? At(false, endCenter, endRadius, endAtPlace);
        (shortOf, shortBy) = (at, by);
        return why;
    }

    /// <summary>
    /// A road refused only for ending further under one of its places than an approach climbs is the
    /// place's last resort: kept here, by that place, the one ending least far under (then the
    /// shortest), and built after all when the planner would otherwise leave the place unconnected
    /// (Hildir's cave at -2304,1536 lost its 100 m staircase to the 20 m rule).
    /// </summary>

    private sealed class UnderRoad
    {
        public Vector2 StartCenter, EndCenter;
        public float StartRadius, EndRadius, Width, Under, Length;
        public string? Label;
        public List<Vector2>? Hints;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), UnderRoad> m_underRoads = new();
    /// <summary>Places reached by their last-resort road, and how far under them it ends.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), float> m_lastResortServed = new();
    /// <summary>Set while a last-resort road is built: its end is not refused for standing under its place.</summary>
    [System.ThreadStatic] private static bool t_lastResort;

    private static void RememberUnder(Vector2 place, UnderRoad road) =>
        m_underRoads.AddOrUpdate(PlaceKey(place), road, (_, kept) =>
            road.Under < kept.Under - 0.5f || (road.Under <= kept.Under + 0.5f && road.Length < kept.Length) ? road : kept);

    /// <summary>The place's last-resort road ((see <see cref="RememberUnder"/>), built now; false where it
    /// has none or it no longer builds.</summary>
    internal static bool BuildLastResort(Vector3 position)
    {
        var key = PlaceKey(new Vector2(position.x, position.z));
        if (!m_underRoads.TryRemove(key, out var road)) return false;
        bool built;
        t_lastResort = true;
        try { built = GenerateRoad(road.StartCenter, road.StartRadius, road.EndCenter, road.EndRadius, road.Width, road.Label, road.Hints); }
        finally { t_lastResort = false; }
        if (built) m_lastResortServed[key] = road.Under;
        if (RoadsLog)
        {
            System.Threading.Interlocked.Increment(ref m_lastResortTried);
            if (built) System.Threading.Interlocked.Increment(ref m_lastResortBuilt);
            Log.LogInfo(FormattableString.Invariant(
                $"  Road {road.Label}: last resort for a place no other road reaches, ending {road.Under:F0} m under it: {(built ? "built" : "refused")}"));
        }
        return built;
    }
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
    internal static RoadPreparation BeginPreparation(bool force)
    {
        var world=WorldGenerator.instance;
        var zones=ZoneSystem.instance;
        var options=NetworkOptions.Snapshot();
        var start=DateTime.Now;
        int percentage=IslandRoadPercentage,maxLocations=MaxLocationsPerIsland;
        return new RoadPreparation(p=>PrepareSteps(force,world,zones,options,start,percentage,maxLocations,p));
    }
    private static IEnumerable<bool> PrepareSteps(bool force,WorldGenerator? capturedWorld,ZoneSystem? capturedZones,
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
        float clearance = RoadWidth * 0.5f + 2f;
        m_generationWorld = world;
        m_pathfinder = new RoadPathfinder(world) { SiteClearance = clearance };
        m_threadPathfinders?.Dispose();
        m_threadPathfinders = new System.Threading.ThreadLocal<RoadPathfinder>(
            () => new RoadPathfinder(world) { SiteClearance = clearance }, true);
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
        m_roadsWithNoRoute = 0;
        m_earthworkReroutes = 0;
        m_pullReroutes = 0;
        m_underRoads.Clear();
        m_lastResortServed.Clear();
        m_offLandEnds = 0;
        RoadCrossingDetector.DeckLineWalkedIn = RoadCrossingDetector.DeckLineRefused = RoadCrossingDetector.DeckLineSpanDropped = 0;
        RoadPitches.ResetCounters();
        m_corridorSnapped = 0;
        m_chainsShared = 0;
        m_shallowFordsWalked = 0;
        RoadSpatialGrid.SwayKept = RoadSpatialGrid.SwayRefused = RoadSpatialGrid.SwayMoreEarth = 0;
        RoadSpatialGrid.SlopeSwayPlans = 0; System.Array.Clear(RoadSpatialGrid.SlopeSwayMillimetres, 0, 3);
        RoadPathfinder.SqueezeHits.Clear();
        m_earthworkUnavoided = 0;
        RoadGrade.SteepestPlanned = 0f;
        RoadSiteProtection.Reset();

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
                    CountDestinationsReached(island.Locations);
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
        if (RoadsLog)
            Log.LogInfo($"  Viaducts: {m_viaductsLaid} Mistlands viaduct(s) laid over dry valleys (DvergrViaduct); road attempts turned onto a bridge end's apron after the search {m_viaductFeetPut}, ends the road could not be brought to {m_viaductFeetUnmet}; searched again without a viaduct whose end the road did not meet {m_viaductsRefusedAtEnds}; river bridge ends not met {m_bridgeEndsUnmet}; roads planned again with ends as they were {m_endsKeptAsTheyWere}");
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
    /// <summary>With <see cref="RoadsLog"/>: why roads were tried again without stairs, and how often.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> m_stairFallbacks = new();
    /// <summary>With <see cref="RoadsLog"/>: road ends at a place, by what became of an approach staircase there.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> m_siteApproaches = new();
    /// <summary>With <see cref="RoadsLog"/>: the generation's staircase tallies for the summary.</summary>
    private static int m_stairRuns, m_stairRoadsUnlaid, m_stretchRetries, m_stretchRetriesKept, m_tightTurnStairs, m_tightTurnStairsBuilt,
        m_lastResortTried, m_lastResortBuilt;
    /// <summary>Staircases laid by this thread, with <see cref="RoadsLog"/>: a retry reads it to know whether it kept the road's stairs.</summary>
    [System.ThreadStatic] private static int t_stairRunsLaidHere;

    private static void ClearStairNotes()
    {
        m_stairsFailed.Clear();
        m_stairFallbacks.Clear();
        m_siteApproaches.Clear();
        m_stairRuns = m_stairRoadsUnlaid = m_stretchRetries = m_stretchRetriesKept = m_tightTurnStairs = m_tightTurnStairsBuilt = 0;
        m_lastResortTried = m_lastResortBuilt = 0;
    }

    /// <summary>The summary's staircase lines, with <see cref="RoadsLog"/>.</summary>
    private static void LogStairs()
    {
        static string ByCount(IEnumerable<KeyValuePair<string, int>> tally) =>
            string.Join("; ", tally.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key}"));
        Log.LogInfo($"  Staircases: {m_stairRuns} laid on roads; {m_stretchRetries} search(es) again with a staircase's stretch closed, " +
                    $"{m_stretchRetriesKept} of them built with stairs; {RoadStairRuns.FeetBeside} set beside their road ({RoadStairRuns.FeetBesideRefused} could not be); " +
                    $"{m_tightTurnStairs} search(es) again with stairs open at switchback turns too close to fit, {m_tightTurnStairsBuilt} of them built with stairs; " +
                    $"{m_lastResortTried} place(s) given their last-resort road ending past an approach's {MaxApproachClimb:F0} m climb, {m_lastResortBuilt} built; " +
                    $"{RoadStairRuns.Crests} top(s) crested onto a lip; {m_stairRoadsUnlaid} road(s) tried again without stairs" +
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
        return levelled.Value < RoadConstants.ShallowWaterHeight ? null : levelled;
    }

    internal static List<Vector2> ImproveSiteApproach(List<Vector2> path, Vector2 centre,
        float radius, float width, bool atStart)
    {
        var world = GenerationWorld;
        if (world == null || radius <= 0f) return path;
        var ops = LocationLevelling.OpsAt(centre);
        // Copy the terrain facts once: scoring must not reload the template
        // for every vertex. Unknown location shaping retains the old approach.
        if (ops == null || ops.Count == 0) return path;
        float centreHeight = LocationLevelling.CentreHeight(centre, world);
        float? platform = LocationLevelling.ApproachHeight(centreHeight, ops);
        if (platform < RoadConstants.ShallowWaterHeight) platform = null;
        Log.LogDebug($"Site approach {centre}: keep-out edge {radius:F1}m, approach {(platform.HasValue ? platform.Value.ToString("F2") : "unknown")}m");
        float Ground(Vector2 p) => LocationLevelling.GroundAt(p, centre, centreHeight, ops)
            ?? BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world);
        float? Target(Vector2 p) => platform.HasValue && Mathf.Abs(Ground(p) - platform.Value) <= 1.5f
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
        if (platform.HasValue && platform.Value >= RoadConstants.ShallowWaterHeight &&
            Mathf.Abs(BiomeBlendedHeight.GetBlendedHeight(point.x, point.y, world) - platform.Value) <= 1.5f)
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
    /// searched again after each with stairs open everywhere else, before it is refused. One stretch that
    /// would not lay cost the whole road its stairs: 65 of 96 roads, and 12 of
    /// the 16 Mistlands staircases lost to the ledge price were the other staircases of four such roads
    /// (no collateral). Offline, on RoadSeedA-E: 80 staircases and 3883
    /// build searches with no retry, 96 and 4075 with one, 98 and 4247 with two; destinations the same.</summary>
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
        Finder.ClearNoStairs();
        try { return GenerateRoadCore(startCenter, startRadius, endCenter, endRadius, width, label, networkHints, default); }
        finally { Finder?.ClearNoStairs(); t_inRoad = false; }
    }

    /// <summary>A road's searches again, each against its own budget: closing a stretch to stairs (StairStretchRetries), the
    /// one tight-turn search with stairs opened, and banning a viaduct (ViaductRetries). One spent never spends another.</summary>
    internal readonly struct RoadRetries
    {
        public readonly int StairStretches;
        public readonly bool TightTurn;
        public readonly int ViaductBans;
        private RoadRetries(int stairStretches, bool tightTurn, int viaductBans)
        { StairStretches = stairStretches; TightTurn = tightTurn; ViaductBans = viaductBans; }
        public RoadRetries NextStairStretch() => new(StairStretches + 1, TightTurn, ViaductBans);
        public RoadRetries WithTightTurn() => new(StairStretches, true, ViaductBans);
        public RoadRetries NextViaductBan() => new(StairStretches, TightTurn, ViaductBans + 1);
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
        // The road again with no stairs, as roads were built before them: when a
        // staircase the search bought cannot be laid, or the road round it cannot
        // be built.
        bool WithoutStairs(string why)
        {
            if (RoadsLog)
            {
                System.Threading.Interlocked.Increment(ref m_stairRoadsUnlaid);
                m_stairFallbacks.AddOrUpdate(why, 1, (_, n) => n + 1);
                m_stairsFailed[PlaceKey(startCenter)] = why;
                m_stairsFailed[PlaceKey(endCenter)] = why;
                if (label != null) Log.LogDebug($"Road {label}: built without stairs ({why})");
            }
            var noStairs = Finder!;
            noStairs.Stairs = false;
            try { return GenerateRoadCore(startCenter, askedStartRadius, endCenter, askedEndRadius, width, label, networkHints, retries); }
            finally { noStairs.Stairs = true; }
        }
        // A staircase that could not be laid: its stretch is closed to stairs and the road searched again with them open
        // everywhere else, each failing stretch in turn, up to StairStretchRetries; then the road is refused. The road
        // keeps every staircase that lays: it is never searched again without stairs.
        bool WithoutThatStaircase(RoadPathfinder finder, List<Vector2> laidOn)
        {
            if (retries.StairStretches >= StairStretchRetries)
            {
                m_refusalReasons.AddOrUpdate("a staircase could not be laid", 1, (_, n) => n + 1);
                NoteFailure(startCenter, endCenter, "refused: a staircase could not be laid");
                System.Threading.Interlocked.Increment(ref m_roadsRefusedForGrade);
                return false;
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

        Vector2 pathStart = RoadEndpoint.FindGround(GenerationWorld, startCenter, startRadius);
        Vector2 pathEnd = RoadEndpoint.FindGround(GenerationWorld, endCenter, endRadius);
        List<Vector2>? path = SnapToNetwork(Finder.FindPath(pathStart, pathEnd), width);

        if (path == null || path.Count < 2)
        {
            if (label != null)
                Log.LogDebug($"Could not find path: {label}");
            NoteFailure(startCenter, endCenter, Finder.Stairs ? "no route" : "no route without stairs");
            System.Threading.Interlocked.Increment(ref m_roadsWithNoRoute);
            return false;
        }

        // Leave enough space for the entire terrain blend, not just the
        // centreline. Sites retain their own authored terrain and paint.
        if (startRadius > 0f) startRadius = RoadSiteProtection.RadiusAt(startCenter, startRadius) + width * 0.5f + 2f;
        if (endRadius > 0f) endRadius = RoadSiteProtection.RadiusAt(endCenter, endRadius) + width * 0.5f + 2f;
        path = TrimPathToRadii(path, startCenter, startRadius, endCenter, endRadius);
        path = TrimWetEnds(path);

        if (path == null || path.Count < 2)
        {
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
        }

        path = ImproveSiteApproach(path, startCenter, startRadius, width, true);
        path = ImproveSiteApproach(path, endCenter, endRadius, width, false);
        Vector2 beforeStart = path[0], beforeEnd = path[path.Count - 1];
        var withStubs = path;
        path = DropStubs(path, PlaceReached(startCenter, startRadius), PlaceReached(endCenter, endRadius)) ?? path;
        // An end moved to a junction takes its height from the road it joins,
        // not from the place it no longer reaches: aiming at the place's edge
        // from the junction refused short roads as "ends too far apart for
        // the cap".
        bool startAtPlace = path[0] == beforeStart, endAtPlace = path[path.Count - 1] == beforeEnd;

        // Fords (prototype): where the road jumped a river, the water is
        // crossed in the ford's style, not paved like the land. Record the
        // crossings and paint the land and each crossing on its own; without
        // fords (or on a road that crossed no river) the whole path is one
        // road as before.
        var finder = Finder!;
        List<RoadCrossing> crossings = DetectCrossings(finder, path);
        // A crossing a few metres from one an earlier road made is the same
        // site: it takes that site's banks, so this road is painted up to
        // the one bridge built there instead of pointing at water beside it.
        SnapToExistingCrossings(crossings);
        foreach (var c in crossings) c.Road = label;
        if (!TryLayStairs(finder, path, crossings, out List<RoadStairSpan> stairs))
            return WithoutThatStaircase(finder, path);
        // The heights to meet are asked for at the road's OWN ends, after
        // trimming - not at the locations' centres. A road stops at the
        // exterior radius, which on a hillside is metres below the middle of
        // the place it is going to, and aiming at the middle builds that
        // difference as a rim around the doorstep.
        float? startGround = startAtPlace ? ApproachGround(path[0], startCenter, startRadius) : null;
        float? endGround = endAtPlace ? ApproachGround(path[path.Count - 1], endCenter, endRadius) : null;
        // Something this road needed was not prepared. Building it anyway
        // would put it through a footprint nothing could see, or land it on a
        // height nothing could read. Leaving the destination unconnected is
        // the recoverable half of that choice.
        if (LocationLevelling.PreparationMissedHere)
        {
            if (label != null)
                Log.LogDebug($"Road refused: preparation was incomplete for it, destination left unconnected: {label}");
            NoteFailure(startCenter, endCenter, "refused: preparation incomplete");
            System.Threading.Interlocked.Increment(ref m_roadsRefusedForGrade);
            return false;
        }

        RoadSpatialGrid.LastRefusal = null;
        var plans = PlanRoadPathWithCrossings(path, crossings, width, startGround, endGround, stairs);
        if (plans == null && !ReferenceEquals(withStubs, path))
        {
            // Dropping the stubs is cosmetic: a road refused without them is
            // planned with them. With both ends at places other roads reach,
            // the 40-60 m connector left between two parts of the network can
            // be too steep where the full road is not.
            // The stubs put back: both ends are on their places' rings again.
            startAtPlace = endAtPlace = true;
            path = withStubs;
            crossings = DetectCrossings(finder, path);
            SnapToExistingCrossings(crossings);
            foreach (var c in crossings) c.Road = label;
            startGround = ApproachGround(path[0], startCenter, startRadius);
            endGround = ApproachGround(path[path.Count - 1], endCenter, endRadius);
            RoadSpatialGrid.LastRefusal = null;
            if (!TryLayStairs(finder, path, crossings, out stairs))
                return WithoutThatStaircase(finder, path);
            plans = PlanRoadPathWithCrossings(path, crossings, width, startGround, endGround, stairs);
        }
        // Switchback turns too close to fit their curves, even tightened: the search climbs that
        // stretch on stairs instead, once, and carries on as road beyond it. On the five offline
        // worlds it was tried 11 times and built none with stairs (one road was built without them):
        // their staircases there could not be laid. One test world refused 22 roads for these turns.
        if (plans == null && finder.Stairs && !retries.TightTurn
            && RoadSpatialGrid.LastRefusal == "switchback: turns too close to fit their curves" && RoadSwitchbacks.LastRefusalAt is Vector2 tight)
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
        if (plans == null && stairs.Count > 0)
            return WithoutStairs("road round the stairs: " + (RoadSpatialGrid.LastRefusal ?? "refused"));
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
                var retry = Replan(finder, pathStart, pathEnd, startCenter, startRadius, endCenter, endRadius, width);
                if (retry == null)
                { Log.LogDebug($"Road {label}: earthwork re-route {attempt + 1} did not route or build ({RoadSpatialGrid.LastRefusal ?? "no route"}), {finder.EarthworkPenaltyCells} cell(s) priced up"); continue; }
                var whereRetry = new List<Vector2>();
                int pastRetry = PastTerrainLimit(retry.Value.plans, EarthworkCap, whereRetry) + InvalidCrossings(retry.Value.crossings, whereRetry) + HeldRidges(retry.Value.plans, whereRetry);
                int pulledRetry = HeldOffProfile(retry.Value.plans, whereRetry);
                Log.LogDebug($"Road {label}: earthwork re-route {attempt + 1}: {pastRetry} point(s) past the cap (was {past}), {retry.Value.crossings.Count} crossing(s), {finder.EarthworkPenaltyCells} cell(s) priced up, {finder.PenalizedWaypoints(retry.Value.path)} of {retry.Value.path.Count} waypoints in them; first offending point {(whereRetry.Count > 0 ? whereRetry[0].ToString() : "-")}, crossing {(retry.Value.crossings.Count > 0 ? retry.Value.crossings[0].FromBank + "->" + retry.Value.crossings[0].ToBank : "-")}");
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
        // The one owner's final check (StructureEnds): a player walks from the road onto every bridge end it is built with.
        // A valley viaduct whose end the road as planned does not meet is not built: the road is searched again without it
        // (four viaduct ends could not be walked onto). A road refused outright with a
        // valley viaduct on it is searched again without its viaducts too: the road as it would be without them.
        if (retries.ViaductBans < ViaductRetries)
        {
            Vector2 at = default;
            string builtWhy = "";
            RoadCrossing? unmet = plans != null ? BuiltRefusal(plans, crossings, out at, out builtWhy) : null;
            var jumps = unmet != null ? (unmet.ValleyJump ? new List<RoadCrossing> { unmet } : new List<RoadCrossing>())
                : plans == null ? (LastStubBetween is RoadCrossing stubbed && crossings.Contains(stubbed) ? new List<RoadCrossing> { stubbed } : crossings.FindAll(c => c.ValleyJump && !c.Shared))
                : new List<RoadCrossing>();
            if (jumps.Count > 0)
            {
                foreach (RoadCrossing c in jumps) finder.BanViaduct(c.FromBank, c.ToBank);
                System.Threading.Interlocked.Increment(ref m_viaductsRefusedAtEnds);
                if (label != null && RoadsLog)
                    Log.LogInfo(FormattableString.Invariant($"[VIADUCT] road {label}: {(unmet != null ? builtWhy : "refused with")} viaduct {jumps[0].FromBank} -> {jumps[0].ToBank}; searched again without it"));
                return GenerateRoadCore(startCenter, askedStartRadius, endCenter, askedEndRadius, width, label, networkHints, retries.NextViaductBan());
            }
            if (unmet != null)
            {
                // A river bridge's end: counted (its kit is the crossing's), the road built as it is.
                System.Threading.Interlocked.Increment(ref m_bridgeEndsUnmet);
                if (RoadsLog) Log.LogInfo(FormattableString.Invariant($"[BRIDGES] road {label}: an end of the bridge {unmet.FromBank} -> {unmet.ToBank} not met at {at}"));
            }
        }
        // Past the searches again, a valley viaduct the built road does not allow is not built: the road is refused with it
        // (three were built where the retries had run out and the check was skipped).
        if (plans != null && retries.ViaductBans >= ViaductRetries
            && BuiltRefusal(plans, crossings, out _, out string lastWhy) is RoadCrossing lastUnmet && lastUnmet.ValleyJump && !lastUnmet.Shared)
        {
            plans = null;
            RoadSpatialGrid.LastRefusal = "a viaduct the built road does not allow (" + lastWhy + ")";
        }
        // A road whose end at its place stands further under (or over) the place's own ground than
        // an approach can climb does not reach it: counted connected, the planner stopped looking,
        // and a 100 m staircase ended 45 m under Hildir's cave with a stub of road on top
        // (a staircase that ends short of the goal and adds a little stub road looks terrible). Refused, the planner tries
        // its other candidates, and reached the cave from the west with an 11 m approach.
        if (plans != null && !t_lastResort && EndOutOfClimb(path, plans, stairs, startCenter, askedStartRadius, endCenter, askedEndRadius,
                startAtPlace, endAtPlace, out Vector2 shortOf, out float shortBy) is string climb)
        {
            if (RoadsLog && label != null) Log.LogDebug($"Road {label}: {climb}");
            float length = 0f;
            for (int i = 1; i < path.Count; i++) length += Vector2.Distance(path[i - 1], path[i]);
            RememberUnder(shortOf, new UnderRoad { StartCenter = startCenter, StartRadius = askedStartRadius, EndCenter = endCenter,
                EndRadius = askedEndRadius, Width = width, Under = shortBy, Length = length, Label = label, Hints = networkHints });
            plans = null;
            RoadSpatialGrid.LastRefusal = $"end past an approach's {MaxApproachClimb:F0} m climb of its place";
        }
        if (plans != null && DanglingBridge(plans, crossings, path[0], path[path.Count - 1], out var dangling))
        {
            // A bridge must have road at both ends; one was found stopping in
            // 3 m of water.
            Log.LogWarning($"Bridge with no road beyond it on {label}: {dangling.FromBank} -> {dangling.ToBank}; road refused");
            plans = null;
            RoadSpatialGrid.LastRefusal = "bridge with no road at one end";
        }
        // No road under a viaduct's deck laid before it (the built-result check, StructureEnds.UnderTheDeck).
        if (plans != null && UnderALaidDeck(plans, out Vector2 underAt) is string underDeck)
        {
            plans = null;
            RoadSpatialGrid.LastRefusal = underDeck;
            if (RoadsLog) Log.LogDebug(FormattableString.Invariant($"Road {label ?? "(unlabelled)"}: {underDeck} at ({underAt.x:F1}, {underAt.y:F1}); refused"));
        }
        // No road crosses another, nor runs beside one at its height (MeetingRefusal).
        if (plans != null && MeetingRefusal(plans, out Vector2 meetsAt) is string meetingRefused)
        {
            plans = null;
            RoadSpatialGrid.LastRefusal = meetingRefused;
            if (RoadsLog) Log.LogDebug(FormattableString.Invariant($"Road {label ?? "(unlabelled)"}: {meetingRefused} at ({meetsAt.x:F1}, {meetsAt.y:F1}); refused"));
        }
        // Refused with stairs on it -- its earthwork or a bridge end may come from
        // the stair's held heights: tried again without them, as before stairs.
        if (plans == null && stairs.Count > 0)
            return WithoutStairs("road round the stairs: " + (RoadSpatialGrid.LastRefusal ?? "refused"));
        // A structure's end never costs a road: refused with ends brought through the owner, the road is planned again once,
        // the whole road, with every structure's ends kept as the road had them (every kit but a viaduct, whose ends are
        // always met: MeetsTheRoad).
        if (plans == null && !t_endsAsTheyWere && crossings.Exists(c => MeetsTheRoad(c) && !IsViaduct(c)))
        {
            System.Threading.Interlocked.Increment(ref m_endsKeptAsTheyWere);
            t_endsAsTheyWere = true;
            try { return GenerateRoadCore(startCenter, askedStartRadius, endCenter, askedEndRadius, width, label, networkHints, retries); }
            finally { t_endsAsTheyWere = false; }
        }
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

        var approaches = finder.Stairs ? SiteApproaches(path, plans, stairs, startCenter, askedStartRadius, endCenter, askedEndRadius, startAtPlace, endAtPlace)
            : new List<RoadStairSpan>();
        foreach (RoadSpatialGrid.PlannedPath plan in plans)
        {
            RoadSpatialGrid.Commit(plan);
            PieceCommitted?.Invoke(plan, label);
        }
        if (IslandActivity != null)
        {
            // A crossing snapped to a site an earlier road built is that bridge, not a new one.
            int bridges = 0;
            foreach (var c in crossings) if (c.Kind == CrossingKind.Bridge && !c.Shared && !c.Invalid) bridges++;
            IslandActivity.BuiltBridges(bridges);
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
        if (RoadCrossingDetector.ToWater)
        {
            GroundUnderDeck(crossings, width);
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
                foreach (var crossing in crossings)
                    if (i >= crossing.FromIndex && i <= crossing.ToIndex) { onCrossing = true; break; }
                // Nor on a staircase: there is no road there to join.
                foreach (var stair in stairs)
                    if (i >= stair.FromIndex && i <= stair.ToIndex) { onCrossing = true; break; }
                if (!onCrossing) networkHints.Add(path[i]);
            }
        m_islandRoads++;
        int roadNumber;
        // Each crossing's kit decided once, as it is stored (BridgeStructures.KitFor): saved with it, never decided again.
        // Decided before the lock every island records its roads under: a viaduct's test samples its piers and ends.
        foreach (RoadCrossing c in crossings) c.Kit ??= BridgeStructures.KitFor(c, GenerationWorld);
        lock (m_recordGate)
        {
            m_roadCrossings.AddRange(crossings);
            roadNumber = ++m_roadsGeneratedCount;
        }
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
    /// <summary>Metres of road carried in under each end of a bridge deck,
    /// over land only, so road and bridge look built together.</summary>
    public const float DeckGroundReach = 3f;
    /// <summary>Ground under a deck end is held at least this far below the deck.</summary>
    public const float DeckUnderClearance = 0.4f;

    /// <summary>
    /// Road carried in under each end of every bridge, up to
    /// <see cref="DeckGroundReach"/> and only while the ground is above the
    /// waterline, levelled to the ground or <see cref="DeckUnderClearance"/>
    /// under the deck, whichever is lower. It paints the road in under the
    /// deck, and it cuts what the height estimate missed: measured at one
    /// bridge, the estimate put the ground 1 m in at 31.2 m while the game's
    /// terrain stood 31.9-32.4 m under a 31.8 m deck, poking through it.
    /// </summary>
    private static void GroundUnderDeck(List<RoadCrossing> crossings, float width)
    {
        var world = GenerationWorld;
        var temp = new List<Vector2>(); var hs = new List<float>();
        foreach (var c in crossings)
        {
            if (c.Kind != CrossingKind.Bridge) continue;
            float span = Vector2.Distance(c.FromBank, c.ToBank);
            if (span < 2f) continue;
            float ceiling = BridgeStructures.DeckHeight(c, world) - DeckUnderClearance;
            // Under a viaduct's end stairs, decided from the road's arrival (StructureEnds), the ground is cut to each step.
            if (IsViaduct(c))
            {
                var (near, far) = DvergrViaduct.Ends(c, world);
                var cuts = new List<Vector3>();
                foreach (StructureEnds.End end in new[] { near, far })
                    for (int k = 0; k < end.Steps; k++)
                    {
                        Vector2 at = end.Edge - end.Up * (BridgeLayout.StairHalfRun + k * BridgeLayout.StairRun);
                        cuts.Add(new Vector3(at.x, end.Height - BridgeLayout.StairRise * (k + 1), at.y));
                    }
                if (cuts.Count > 0) RoadSpatialGrid.CommitCuts(cuts, BridgeLayout.DeckHalfWidth * 2f);
            }
            // No road painted in under a viaduct's deck at the ground's height: its road runs on under the end module at the
            // meeting height (StructureEnds.JoinOverlap), and road at the ground under a deck end is wrong.
            if (IsViaduct(c)) continue;
            float reach = Mathf.Min(DeckGroundReach, span * 0.5f);
            foreach (var (bank, other) in new[] { (c.FromBank, c.ToBank), (c.ToBank, c.FromBank) })
                for (float d = 0.5f; d <= reach; d += 0.5f)
                {
                    var p = bank + (other - bank) * (d / span);
                    float g = BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world);
                    if (g < RoadCrossingDetector.Waterline) break;
                    temp.Add(p); hs.Add(Mathf.Min(g, ceiling));
                }
        }
        if (temp.Count > 0) RoadSpatialGrid.CommitLevelled(temp, hs, width);
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

    /// <summary>A road already built comes within this place's trim radius
    /// (where a road to it stops). A place with no radius (a junction) is.</summary>
    internal static bool PlaceReached(Vector2 centre, float trimRadius) =>
        trimRadius <= 0f || (RoadSpatialGrid.IsInitialized && RoadSpatialGrid.TryGetRoadWithin(centre, trimRadius + 4f, out _));
    /// <summary>Drop a road's own stub where it merges within <see cref="StubLength"/>. Settable for tests.</summary>
    internal static bool OneBranch = true;

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

    /// <summary>A bridge whose bank has no planned road piece starting or ending at it.</summary>
    private static bool DanglingBridge(List<RoadSpatialGrid.PlannedPath> plans, List<RoadCrossing> crossings,
        Vector2 roadStart, Vector2 roadEnd, out RoadCrossing dangling)
    {
        // A bank at the road's own start or end is the place itself (a coast
        // landing starts on its bridge): only a bank in between needs road.
        bool AtRoadEnd(Vector2 bank) => Vector2.Distance(bank, roadStart) < 8f || Vector2.Distance(bank, roadEnd) < 8f;
        dangling = null!;
        foreach (var c in crossings)
        {
            if (c.Kind != CrossingKind.Bridge) continue;
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
    internal const float CorridorAngle = 25f;
    internal const float CorridorHeight = 3f;
    internal const float CorridorRun = 12f;

    /// <summary>Why a planned road may not meet the network as it would: it crosses another road (<see cref="CrossesNetwork"/>;
    /// it meets one at a junction, at its ends or along a stretch it shares, or it is not built, even if that rejects a POI), or it runs beside one at its height (<see cref="BraidsNetwork"/>;
    /// where its search did not put it on the other road it braids beside it: roads at 3354,6308 and -5487,5808 were a
    /// mess). Null where it does neither; <paramref name="at"/>: where. The one test of
    /// a road against the roads it meets, for the road, its earthwork re-routes and the ways onto a placed place's landing.</summary>
    internal static string? MeetingRefusal(List<RoadSpatialGrid.PlannedPath> plans, out Vector2 at)
    {
        at = default;
        if (CrossesNetwork(plans, out at)) return "crosses another road";
        if (BraidsNetwork(plans, out at)) return "runs beside another road";
        return null;
    }

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
        int k = 0;
        while (k < points.Count)
        {
            if (!RoadSpatialGrid.TryGetRoadWithin(points[k], touch, out _)) { k++; continue; }
            int first = k;
            while (k + 1 < points.Count && RoadSpatialGrid.TryGetRoadWithin(points[k + 1], touch, out _)) k++;
            int last = k++;
            if (along[first] < JunctionEnd || total - along[last] < JunctionEnd) continue;
            if (along[last] - along[first] >= SharedRun) continue;
            // The other road's line here: the road points round the middle of the touch. Not a staircase's cut-only points
            // (its guard cuts): near a staircase's end they outnumber the road points and turned the line.
            Vector2 mid = points[(first + last) / 2];
            var near = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(mid.x, 0f, mid.y), 4f);
            near.RemoveAll(q => q.cutOnly);
            if (near.Count < 2) continue;
            Vector2 centre = new Vector2(0f, 0f);
            foreach (var q in near) centre += q.p;
            centre *= 1f / near.Count;
            float sxx = 0f, sxy = 0f, syy = 0f;
            foreach (var q in near) { Vector2 d = q.p - centre; sxx += d.x * d.x; sxy += d.x * d.y; syy += d.y * d.y; }
            float angle = 0.5f * Mathf.Atan2(2f * sxy, sxx - syy);
            Vector2 dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)), normal = new Vector2(-dir.y, dir.x);
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
        var place = new RoadBeside.Place[points.Count];
        for (int i = 0; i < points.Count; i++) place[i] = RoadBeside.At(points[i], heights[i]);
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

    internal static List<Vector2>? SnapToNetwork(List<Vector2>? path, float? width = null)
    {
        if (path == null || RoadSnap <= 0f || path.Count < 3 || !RoadSpatialGrid.IsInitialized) return path;
        var moved = new Vector2[path.Count];
        var fixedPoint = new bool[path.Count];
        for (int i = 0; i < path.Count; i++) moved[i] = path[i];
        for (int i = 1; i < path.Count - 1; i++)
            if (RoadSpatialGrid.TryGetRoadWithin(path[i], RoadSnap, out Vector2 onRoad)) { moved[i] = onRoad; fixedPoint[i] = true; }
        if (CorridorSnap > RoadSnap && GenerationWorld != null)
        {
            var corridor = new Vector2?[path.Count];
            for (int i = 1; i < path.Count - 1; i++)
                if (!fixedPoint[i]) corridor[i] = SameCorridor(path[i], path[i + 1] - path[i - 1]);
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
                    if (a - 1 > 0 && !fixedPoint[a - 1] && RoadSpatialGrid.TryGetRoadWithin(path[a - 1], CorridorSnap * 1.5f, out Vector2 qa))
                        moved[a - 1] = Vector2.Lerp(path[a - 1], qa, 0.5f);
                    if (b + 1 < path.Count - 1 && !fixedPoint[b + 1] && RoadSpatialGrid.TryGetRoadWithin(path[b + 1], CorridorSnap * 1.5f, out Vector2 qb))
                        moved[b + 1] = Vector2.Lerp(path[b + 1], qb, 0.5f);
                    System.Threading.Interlocked.Add(ref m_corridorSnapped, b - a + 1);
                }
                a = b + 1;
            }
        }
        var result = new List<Vector2>(path.Count) { path[0] };
        for (int i = 1; i < path.Count - 1; i++)
            if (Vector2.Distance(result[result.Count - 1], moved[i]) > 0.5f) result.Add(moved[i]);
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
    private static int m_chainsShared;

    /// <summary>The nearest road point within <see cref="CorridorSnap"/> of <paramref name="p"/> on a
    /// road heading along <paramref name="heading"/> at about this ground's height, or null.</summary>
    private static Vector2? SameCorridor(Vector2 p, Vector2 heading)
    {
        if (heading.sqrMagnitude < 1e-4f) return null;
        heading.Normalize();
        var near = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(p.x, 0f, p.y), CorridorSnap);
        if (near.Count < 2) return null;
        float ground = BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, GenerationWorld);
        float cosLimit = Mathf.Cos(CorridorAngle * Mathf.PI / 180f);
        Vector2? best = null; float bestD = float.MaxValue;
        foreach (var q in near)
        {
            if (!q.IsRoad) continue;
            float d = Vector2.Distance(q.p, p);
            if (d >= bestD || Mathf.Abs(q.h - ground) > CorridorHeight) continue;
            // The other road's heading at q: from its neighbours 1.5-4 m away.
            Vector2 dir = new Vector2(0f, 0f);
            foreach (var r in near)
            {
                if (r.cutOnly) continue;
                float dr = Vector2.Distance(r.p, q.p);
                if (dr < 1.5f || dr > 4f) continue;
                Vector2 v = r.p - q.p;
                if (Vector2.Dot(v, dir) < 0f) v = -v;
                dir += v;
            }
            if (dir.sqrMagnitude < 1e-4f) continue;
            dir.Normalize();
            if (Mathf.Abs(Vector2.Dot(dir, heading)) < cosLimit) continue;
            best = q.p; bestD = d;
        }
        return best;
    }

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
    /// current penalties; null when it does not route or build.</summary>
    private static (List<Vector2> path, List<RoadCrossing> crossings, List<RoadSpatialGrid.PlannedPath> plans, List<RoadStairSpan> stairs, bool startAtPlace, bool endAtPlace)? Replan(
        RoadPathfinder finder, Vector2 pathStart, Vector2 pathEnd, Vector2 startCenter, float startRadius,
        Vector2 endCenter, float endRadius, float width)
    {
        var path = SnapToNetwork(finder.FindPath(pathStart, pathEnd), width);
        if (path == null || path.Count < 2) return null;
        path = TrimPathToRadii(path, startCenter, startRadius, endCenter, endRadius);
        path = TrimWetEnds(path);
        if (path == null || path.Count < 2) return null;
        path = ImproveSiteApproach(path, startCenter, startRadius, width, true);
        path = ImproveSiteApproach(path, endCenter, endRadius, width, false);
        Vector2 beforeStart = path[0], beforeEnd = path[path.Count - 1];
        path = DropStubs(path, PlaceReached(startCenter, startRadius), PlaceReached(endCenter, endRadius)) ?? path;
        // An end moved to a junction takes its height from the road it joins,
        // not from the place it no longer reaches: aiming at the place's edge
        // from the junction refused short roads as "ends too far apart for
        // the cap".
        bool startAtPlace = path[0] == beforeStart, endAtPlace = path[path.Count - 1] == beforeEnd;
        var crossings = DetectCrossings(finder, path);
        SnapToExistingCrossings(crossings);
        if (!TryLayStairs(finder, path, crossings, out List<RoadStairSpan> stairs)) return null;
        float? startGround = startAtPlace ? ApproachGround(path[0], startCenter, startRadius) : null;
        float? endGround = endAtPlace ? ApproachGround(path[path.Count - 1], endCenter, endRadius) : null;
        var plans = PlanRoadPathWithCrossings(path, crossings, width, startGround, endGround, stairs);
        return plans == null ? null : (path, crossings, plans, stairs, startAtPlace, endAtPlace);
    }

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
    /// order. Where a structure's end stairs would not land on the road, the path is first put
    /// through their foot (StructureEnds.MeetTheRoad) and its crossings found again.</summary>
    private static List<RoadCrossing> DetectCrossings(RoadPathfinder finder, List<Vector2> path)
    {
        List<RoadCrossing> Find()
        {
            List<RoadCrossing> found = finder.Fords || finder.Bridges
                ? RoadCrossingDetector.Detect(path, GenerationWorld, finder.Bridges, finder.Fords)
                : new List<RoadCrossing>();
            if (finder.Viaducts && finder.Bridges)
                DvergrViaduct.AddJumps(path, finder.LastViaductJumps, GenerationWorld, found);
            return found;
        }
        List<RoadCrossing> crossings = Find();
        if (!finder.Bridges) return crossings;
        // Every structure's end the one owner meets (MeetsTheRoad) meets the road through its apron (StructureEnds).
        var viaducts = crossings.FindAll(MeetsTheRoad);
        if (viaducts.Count == 0) return crossings;
        var steps = finder.LastStairSteps;
        bool IsStep(Vector2 a, Vector2 b)
        {
            foreach (var (from, to) in steps)
                if ((Vector2.Distance(a, from) <= 1f && Vector2.Distance(b, to) <= 1f) || (Vector2.Distance(a, to) <= 1f && Vector2.Distance(b, from) <= 1f))
                    return true;
            return false;
        }
        var unmetFeet = RoadsLog ? new List<Vector2>() : null;
        int turned = StructureEnds.MeetTheRoad(path, viaducts, GenerationWorld, out int unmet, out int pinned, IsStep, unmetFeet);
        if (turned > 0) System.Threading.Interlocked.Add(ref m_viaductFeetPut, turned);
        if (unmet > 0) System.Threading.Interlocked.Add(ref m_viaductFeetUnmet, unmet);
        if (unmetFeet != null)
            foreach (Vector2 foot in unmetFeet)
                Log.LogInfo(FormattableString.Invariant($"[VIADUCT] a bridge end at ({foot.x:F1}, {foot.y:F1}) not met by this road: it ends under the flight or comes to it on a staircase"));
        return turned + pinned > 0 ? Find() : crossings;
    }

    /// <summary>
    /// A staircase joins the network: its earthwork goes in with the road's (at a place, only outside every footprint: the
    /// terrain writer leaves the place's own ground alone, and its clearance keeps the place's own rocks), its pieces are a
    /// structure source's, standing from now on for later roads. The one way a road's, an entry's and an approach's
    /// staircase is laid.
    /// </summary>
    private static void JoinNetwork(Vector2 from, Vector2 to, StairPlan plan, StairKit kit, bool atPlace)
    {
        RoadSpatialGrid.CommitCuts(atPlace ? plan.Cuts.FindAll(c => !RoadSiteProtection.Contains(new Vector2(c.x, c.z))) : plan.Cuts, StairLayout.CutRadius * 2f);
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

    /// <summary>Whether this thread's road is being planned again with its structures' ends kept as the road had them (a road
    /// refused with them brought through the owner; see GenerateRoadCore).</summary>
    [ThreadStatic] private static bool t_endsAsTheyWere;
    private static int m_endsKeptAsTheyWere;

    /// <summary>Whether a crossing is built as a Dvergr viaduct: its kit (BridgeStructures.KitFor, the one owner; the kit stored
    /// with it once the road is recorded).</summary>
    private static bool IsViaduct(RoadCrossing c) =>
        c.Kind == CrossingKind.Bridge && BridgeStructures.KitFor(c, GenerationWorld) == BridgeStructures.Kit.DvergrViaduct;

    /// <summary>A crossing whose two ends the one owner brings the road to (StructureEnds): every structure with ends, a
    /// viaduct, a wooden or Dvergr wood bridge and a ford's spanned footbridge alike. The road ends on the foot of each end's
    /// stairs, on their line, straight on. A viaduct's ends always are, and are always checked (a road that cannot meet them
    /// is refused, or searched again without the viaduct); a wooden kit's or a span's are wherever that approach is clear of
    /// protected sites, and not while the road is planned again with its ends as they were.</summary>
    internal static bool MeetsTheRoad(RoadCrossing c)
    {
        var (near, far) = BridgeStructures.Ends(c, GenerationWorld);
        if (!near.IsSet) return false;
        if (IsViaduct(c)) return true;
        if (t_endsAsTheyWere) return false;
        // Where the straight approach to an end (1.5 aprons out along its line) would cross a protected site, the end
        // keeps the road it had: brought through the owner there, the road would be refused for the site.
        float reach = StructureEnds.ApronLength * 1.5f, clearance = RoadSiteProtection.Clearance(RoadConstants.DefaultRoadWidth);
        foreach (StructureEnds.End end in new[] { near, far })
            if (RoadSiteProtection.BlocksSegment(end.Joint, end.Meet - end.Up * reach, clearance, null, null)) return false;
        return true;
    }

    /// <summary>
    /// The final check of the one owner (StructureEnds.Reachable): every end of every bridge on the road, as planned, met by
    /// a piece of the road at its meeting point (the stairs' foot, or the deck's edge) within a step of its height. The first
    /// end that is not, with its crossing (a valley viaduct's before any other's), or null. A player walks onto every structure the road is built with.
    /// </summary>
    internal static RoadCrossing? UnreachableEnd(List<RoadSpatialGrid.PlannedPath> plans, List<RoadCrossing> crossings, out Vector2 where)
    {
        where = default;
        RoadCrossing? first = null;
        foreach (RoadCrossing c in crossings)
        {
            if (!MeetsTheRoad(c) || (first != null && !c.ValleyJump)) continue;
            var (near, far) = BridgeStructures.Ends(c, GenerationWorld);
            foreach (StructureEnds.End end in new[] { near, far })
            {
                bool met = false;
                foreach (var (_, h, _) in StructureEnds.BuiltRoadNear(end.Meet, 0.75f, plans))
                    if (StructureEnds.Reachable(h, end)) { met = true; break; }
                if (met) continue;
                // A valley viaduct's first, wherever it is on the road: it is the one a re-route can drop.
                where = end.Meet;
                if (c.ValleyJump) return c;
                first = c;
                break;
            }
        }
        return first;
    }

    /// <summary>
    /// The one owner's final check, on the road as BUILT (StructureEnds): the road this road plans together
    /// with the network laid before it. Judging only the ends of this road's own planned pieces passed viaducts with road
    /// under their deck ends (another road, and the 3 m every bridge's road was painted in under its deck at the
    /// ground's height after the check), and a floor raised by a road's fill. The first
    /// crossing on the road that fails, with where and why, or null:
    /// <list type="bullet">
    /// <item>an end a player cannot walk onto from the road (UnreachableEnd);</item>
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
        if (UnreachableEnd(plans, crossings, out Vector2 end) is RoadCrossing unmet)
        {
            where = end; why = "an end the road does not meet";
            if (unmet.ValleyJump) return unmet;
            first = unmet;
        }
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

    private static int m_viaductFeetPut, m_viaductFeetUnmet, m_viaductsRefusedAtEnds, m_bridgeEndsUnmet;
    /// <summary>Road searches made again without a valley viaduct whose end the road did not meet (or refused with it).</summary>
    public static int ViaductsRefusedAtEnds => m_viaductsRefusedAtEnds;
    /// <summary>River bridge ends the road as built does not meet (their kit stands).</summary>
    public static int BridgeEndsUnmet => m_bridgeEndsUnmet;
    /// <summary>Searches again without a viaduct, past the staircase retries.</summary>
    internal const int ViaductRetries = 3;
    /// <summary>Structure end stairs whose foot the road was turned onto after its search (StructureEnds.MeetTheRoad: it
    /// came at them off their line), and ends whose foot it could not be made to meet. Counted per road attempt, built
    /// or not.</summary>
    public static int ViaductFeetPut => m_viaductFeetPut;
    public static int ViaductFeetUnmet => m_viaductFeetUnmet;
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
        // A stretch of road to or from a structure's end is planned as two pieces, split at the apron's far end: the road up
        // to it, held there at the meeting height, and the apron itself, level (the stairs' rule: the road runs to
        // the structure at its height). One piece, the grade limiter spread the climb to a rim into the apron and tilted it
        // (most viaduct ends a little off level, seven a step or more).
        void AddLand(List<Vector2> land, float? startH, float? endH, StructureEnds.End? startEnd, StructureEnds.End? endEnd)
        {
            float landFloor = LandFloor;
            int last = land.Count - 1;
            int i0 = startEnd is StructureEnds.End se && startH.HasValue ? land.FindIndex(q => (q - se.Apron).sqrMagnitude < 0.09f) : -1;
            int i1 = endEnd is StructureEnds.End ee && endH.HasValue ? land.FindLastIndex(q => (q - ee.Apron).sqrMagnitude < 0.09f) : -1;
            int from = 0, to = last;
            if (i0 > 0 && i0 < last) { pieces.Add((land.GetRange(0, i0 + 1), false, landFloor, startH, startH)); from = i0; }
            bool levelTail = i1 > from && i1 < last;
            if (levelTail) to = i1;
            if (to > from) pieces.Add((land.GetRange(from, to - from + 1), false, landFloor, startH, endH));
            if (levelTail) pieces.Add((land.GetRange(i1, last - i1 + 1), false, landFloor, endH, endH));
        }
        LastStubBetween = null;

        // What the road is cut at, in path order: river crossings and staircases.
        var gaps = new List<(int from, int to, Vector2 fromPoint, Vector2 toPoint, Vector2? fromLand, Vector2? toLand, float? fromHeight, float? toHeight, RoadCrossing? crossing)>();
        // A bridge end the one owner meets (StructureEnds): the road ends at its meeting point (the stairs' foot, or the
        // deck's edge) at its height, its last ApronLength levelled to it (RoadSpatialGrid.LevelledEnds).
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
            else gaps.Add((c.FromIndex, c.ToIndex, c.FromBank, c.ToBank, c.FromLand, c.ToLand, null, null, c));
        }
        // The road's last ApronLength to a meeting point is laid on the structure's line, whichever way the road came:
        // points every half apron, held (HeldPoints) like the two ends, so its curve cannot bow off the line. The road is
        // cut back to its last point outside the apron and turned onto the line from there (the stairs' rule: the road
        // runs onto the structure's line).
        var apronPoints = new List<Vector2>();
        void AlongTheApron(List<Vector2> land, StructureEnds.End end, bool atEnd)
        {
            if (land.Count == 0) return;
            if (!atEnd) land.Reverse();   // worked at the end, the meeting point last
            Vector2 outward = -end.Up, across = new(-outward.y, outward.x);
            float Out(Vector2 p) => Vector2.Dot(p - end.Meet, outward);
            float further = StructureEnds.ApronLength * 1.5f;
            bool OnLine(Vector2 p) => Mathf.Abs(Vector2.Dot(p - end.Meet, across)) <= 1f;
            // The road is kept from half an apron past the apron's far end out, and turned onto the line there, so the
            // bend's rounding stays outside the apron.
            bool Clear(Vector2 p) => Out(p) >= further + 1f;
            int k = land.Count;
            while (k > 1 && !Clear(land[k - 1])) k--;
            // What the road had from there on is replaced by the apron; its first point (where it starts: a resumed
            // structure's end, or the road's own end) is always kept.
            float from = Clear(land[k - 1]) ? further : Mathf.Max(0f, Out(land[k - 1]) - 0.5f);
            land.RemoveRange(k, land.Count - k);
            var on = new List<Vector2>();
            if (from >= further && !OnLine(land[k - 1])) on.Add(end.Meet + outward * further);
            for (float d = StructureEnds.ApronLength; d > 0.25f; d -= StructureEnds.ApronLength * 0.5f)
                if (d < from - 0.25f) on.Add(end.Meet + outward * d);
            land.AddRange(on);
            apronPoints.AddRange(on);
            if (!atEnd) land.Reverse();
        }
        // What lies past a meeting point, toward its structure (between the foot and the deck, under the flight), is not road.
        // Where that is all the road there is (it ends under the flight), it is left as it was, ending at the bank: that end
        // is not met (UnreachableEnd).
        static bool TrimPast(List<Vector2> land, Vector2 meet, Vector2 up, bool atEnd)
        {
            int keep = land.Count;
            if (atEnd) while (keep > 0 && Vector2.Dot(land[keep - 1] - meet, up) > -0.25f) keep--;
            else { int k = 0; while (k < land.Count && Vector2.Dot(land[k] - meet, up) > -0.25f) k++; keep = land.Count - k; }
            if (keep == 0 && land.Count > 0) return false;
            if (atEnd) land.RemoveRange(keep, land.Count - keep);
            else land.RemoveRange(0, land.Count - keep);
            return true;
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
            Vector2? resumeAt = null, resumeLand = null, resumeUp = null, resumeBank = null, resumeJoint = null;
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
                if (resumeLand.HasValue && (land.Count == 0 || Vector2.Distance(resumeLand.Value, land[0]) > 0.5f))
                    land.Insert(0, resumeLand.Value);
                if (resumeOnStair && land.Count > 1) land.RemoveAt(0);
                if (resumeUp is Vector2 rUp && resumeAt is Vector2 rAt)
                {
                    if (!TrimPast(land, rAt, rUp, atEnd: false)) { resumeAt = resumeBank; resumeHeight = null; resumeJoint = null; }
                    else if (resumeEnd is StructureEnds.End re) AlongTheApron(land, re, atEnd: false);
                }
                if (resumeAt.HasValue && (land.Count == 0 || Vector2.Distance(resumeAt.Value, land[0]) > 0.5f))
                    land.Insert(0, resumeAt.Value);
                if (resumeJoint is Vector2 rj) land.Insert(0, rj);
                // A deck end walked in off land: the road runs on over the strip.
                if (gap.fromLand is Vector2 fromLand && land.Count > 0 && Vector2.Distance(fromLand, land[land.Count - 1]) > 0.5f)
                    land.Add(fromLand);
                if (gap.crossing == null && land.Count > 1) land.RemoveAt(land.Count - 1);
                Vector2 endAt = gap.fromPoint;
                float? endHeight = gap.fromHeight;
                if (gap.crossing != null && ups.TryGetValue(gap.crossing, out var meetEnds))
                {
                    if (!TrimPast(land, gap.fromPoint, meetEnds.near.Up, atEnd: true)) { endAt = gap.crossing.FromBank; endHeight = null; }
                    else AlongTheApron(land, meetEnds.near, atEnd: true);
                }
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
                    AddLand(land, resumeHeight, endHeight, resumeJoint.HasValue ? resumeEnd : null,
                        toCrossing != null && endHeight.HasValue && ups.TryGetValue(toCrossing, out var splitEnds) ? splitEnds.near : null);
                }

                // A bridge or a spanned ford is left to its pieces: nothing is
                // leveled or painted over the water. Nor on a staircase.
                // A shared crossing is carried by the road that built it: only the land up to its banks is ours.
                RoadCrossing? crossing = gap.crossing;
                if (crossing != null && crossing.Kind == CrossingKind.Ford && crossing.Style != FordStyle.Span && !crossing.Shared)
                {
                    List<Vector2> ford = new() { crossing.FromBank };
                    for (int k = Mathf.Max(crossing.FromIndex + 1, cursor + 1); k < crossing.ToIndex; k++)
                        ford.Add(path[k]);
                    ford.Add(crossing.ToBank);
                    if (ford.Count >= 2)
                        pieces.Add(crossing.Style == FordStyle.Wade
                            ? (crossing.Shallow
                                ? (ford, false, RoadConstants.SeaLevel - RoadCrossingDetector.ShallowWadeCover, null, null) // built up to wading depth
                                : (ford, true, float.NegativeInfinity, null, null))
                            : (ford, false, crossing.Shallow ? RoadConstants.DryRoadFloor : RoadPathfinder.LandingFloor, null, null));
                }

                resumeAt = gap.toPoint;
                resumeLand = gap.toLand;
                resumeHeight = gap.toHeight;
                resumeCrossing = gap.crossing != null && ups.ContainsKey(gap.crossing) ? gap.crossing : null;
                resumeOnStair = gap.crossing == null;
                resumeUp = gap.crossing != null && ups.TryGetValue(gap.crossing, out var resumeEnds) ? resumeEnds.far.Up : null;
                resumeBank = gap.crossing?.ToBank;
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
            if (resumeUp is Vector2 tUp && resumeAt is Vector2 tAt)
            {
                if (!TrimPast(tail, tAt, tUp, atEnd: false)) { resumeAt = resumeBank; resumeHeight = null; resumeJoint = null; }
                else if (resumeEnd is StructureEnds.End te) AlongTheApron(tail, te, atEnd: false);
            }
            if (resumeLand.HasValue && Vector2.Distance(resumeLand.Value, tail[0]) > 0.5f)
                tail.Insert(0, resumeLand.Value);
            if (resumeAt.HasValue && Vector2.Distance(resumeAt.Value, tail[0]) > 0.5f)
                tail.Insert(0, resumeAt.Value);
            if (resumeJoint is Vector2 tj) tail.Insert(0, tj);
            if (tail.Count >= 2 && !endsOnStair)
                AddLand(tail, resumeHeight, null, resumeJoint.HasValue ? resumeEnd : null, null);
        }

        if (pieces.Count == 0)
            return null;

        // Between two structures (a viaduct's end and a staircase, or two staircases), no stub of road. A stretch shorter
        // than StructureEnds.StubLength is one level landing at the joining height: within a
        // step (StepTolerance) of both it is that; further apart, the viaduct is not built with this road, which is searched
        // again without it (GenerateRoadCore). Two staircases of one road keep their own meeting (StairChain).
        foreach (var (points, fromH, toH, from, to) in between)
        {
            RoadCrossing? viaduct = from != null && from.ValleyJump && !from.Shared ? from : to != null && to.ValleyJump && !to.Shared ? to : null;
            // A river viaduct's kit is its crossing's (it stands whatever the road does), and a shared one is its first road's.
            if (viaduct == null) continue;
            if (StructureEnds.IsStub(points, fromH, toH) is string stub)
            {
                RoadSpatialGrid.LastRefusal = "a stub of road between two structures";
                LastStubBetween = viaduct;
                if (RoadsLog) Log.LogDebug(FormattableString.Invariant($"[VIADUCT] {stub} at ({points[0].x:F1}, {points[0].y:F1}); the viaduct {viaduct.FromBank} -> {viaduct.ToBank} is not built with this road"));
                return null;
            }
        }

        // Only the road's own two ends meet a location. Every join in between
        // is a river bank, which meets the natural ground as it always did, or
        // the end of a staircase, which the road meets at the stair's height.
        var plans = new List<RoadSpatialGrid.PlannedPath>(pieces.Count);
        System.Func<Vector2, float>? ground = StripGround(crossings, width);
        // The meeting points and apron ends of the bridges' ends, which the road was put through (StructureEnds.MeetTheRoad),
        // stay on it; and its last ApronLength to each is levelled to it.
        List<Vector2>? feet = null;
        foreach (var (near, far) in ups.Values)
            foreach (StructureEnds.End end in new[] { near, far })
                (feet ??= new List<Vector2>()).AddRange(new[] { end.Joint, end.Meet, end.Apron });
        if (apronPoints.Count > 0) (feet ??= new List<Vector2>()).AddRange(apronPoints);
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
    /// The network's staircases settled on the ground the terrain writer will leave (natural, the roads' earthwork and
    /// the staircases' cuts): columns carried down where it lies lower than their layout read, then pieces wholly under
    /// it not placed, which the game breaks once it weighs them and drops their resources (<see cref="BuriedPieces"/>).
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
    /// <summary>
    /// Staircases from the road's ends that stop at a place up to the place's
    /// flat ground (<see cref="RoadStairRuns.LaySiteApproach"/>). An end is at
    /// its place unless it was moved to a junction (<paramref name="startAtPlace"/>,
    /// <paramref name="endAtPlace"/>); a place with no radius (an island's edge) has none.
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
            int index = atStart ? 0 : path.Count - 1;
            if (RoadEnd(atStart, path, plans, stairs) is not var (end, height, _)) return;
            var both = new List<RoadStairSpan>(stairs);
            both.AddRange(laid);
            RoadStairSpan? span = RoadStairRuns.LaySiteApproach(end, height, RoadStairRuns.ArrivingTravel(path, end, atStart), centre,
                LocationLevelling.CentreHeight(centre, world), LocationLevelling.OpsAt(centre), path, index, both, world, out string why);
            if (span != null) laid.Add(span);
            if (RoadsLog) m_siteApproaches.AddOrUpdate(span != null ? "laid" : why, 1, (_, n) => n + 1);
        }
        At(true, startCenter, askedStartRadius, startAtPlace);
        At(false, endCenter, askedEndRadius, endAtPlace);
        return laid;
    }

    /// <summary>The staircases on <paramref name="path"/> laid out from the stair
    /// steps the last search bought; false when one cannot be laid.</summary>
    private static bool TryLayStairs(RoadPathfinder finder, List<Vector2> path, List<RoadCrossing> crossings, out List<RoadStairSpan> stairs)
    {
        stairs = new List<RoadStairSpan>();
        if (!finder.Stairs || finder.LastStairSteps.Count == 0) return true;
        List<RoadStairSpan>? laid = RoadStairRuns.Lay(path, finder.LastStairSteps, crossings, GenerationWorld);
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
            all.Add(item);
            if(item.Item1=="StartTemple") { spawn=item.Item2; radius=item.Item3; }
            else if(BossLocationNames.Contains(item.Item1)) bosses.Add(item);
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

    private static bool IsRoadLocation(string locationName)
    {
        return BossLocationNames.Contains(locationName) ||
               LocationPriorities.ContainsKey(locationName) ||
               IsCustomLocation(locationName);
    }

    private static bool IsCustomLocation(string name) => RegisteredLocationNames.Contains(name)
        || ConfiguredLocationNames.Contains(name);

    private static bool IsRequiredLocation(string name) => BossLocationNames.Contains(name)
        || name == "Hildir_cave" || name == "Hildir_crypt" || name == "Hildir_plainsfortress"
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
        m_pathfinder = new RoadPathfinder(GenerationWorld) { SiteClearance = RoadWidth * 0.5f + 2f };
        m_threadPathfinders?.Dispose();
        m_threadPathfinders = new System.Threading.ThreadLocal<RoadPathfinder>(
            () => new RoadPathfinder(GenerationWorld) { SiteClearance = RoadWidth * 0.5f + 2f }, true);
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
        m_roadsWithNoRoute = 0;
        m_earthworkReroutes = 0;
        m_pullReroutes = 0;
        m_offLandEnds = 0;
        RoadCrossingDetector.DeckLineWalkedIn = RoadCrossingDetector.DeckLineRefused = RoadCrossingDetector.DeckLineSpanDropped = 0;
        RoadPathfinder.SqueezeHits.Clear();
        m_earthworkUnavoided = 0;
        RoadGrade.SteepestPlanned = 0f;
        RoadSiteProtection.Reset();

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
        CountDestinationsReached(selected);

        StairStructures.SortCanonical();
        RoadSpatialGrid.FinalizeRoadNetwork();
        LogTerrainMemoCounters();
        NetworkCutoff.UseNewNetwork();
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

    public static void Reset()
    {
        bool cancelled = RoadLifecycleManager.InitialGenerationActive;
        RoadLifecycleManager.CancelAndDrain();
        RoadLifecycleManager.AllowInitialRetry();
        if (!cancelled) ResetData();
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
        m_roadsWithNoRoute = 0;
        m_earthworkReroutes = 0;
        m_pullReroutes = 0;
        m_offLandEnds = 0;
        RoadCrossingDetector.DeckLineWalkedIn = RoadCrossingDetector.DeckLineRefused = RoadCrossingDetector.DeckLineSpanDropped = 0;
        RoadPitches.ResetCounters();
        m_corridorSnapped = 0;
        RoadSpatialGrid.BendFallbacks.Clear();
        m_chainsShared = 0;
        m_shallowFordsWalked = 0;
        RoadSpatialGrid.SwayKept = RoadSpatialGrid.SwayRefused = RoadSpatialGrid.SwayMoreEarth = RoadSpatialGrid.SwayKeptHeld = 0;
        RoadSpatialGrid.SlopeSwayPlans = 0; System.Array.Clear(RoadSpatialGrid.SlopeSwayMillimetres, 0, 3);
        RoadPathfinder.SqueezeHits.Clear();
        m_earthworkUnavoided = 0;
        RoadGrade.SteepestPlanned = 0f;
        m_viaductsLaid = m_viaductFeetPut = m_viaductFeetUnmet = m_viaductsRefusedAtEnds = m_bridgeEndsUnmet = m_endsKeptAsTheyWere = 0;
    }

    internal static void ResetData()
    {
        ResetGenerationCounters();
        LocationLevelling.ResetPlacements?.Invoke();
        m_roadsGenerated = false;
        m_locationsReady = false;
        m_roadsLoadedFromZDO = false;
        m_networkDirty = false;
        ReleasePathfinders();
        RoadSiteProtection.Reset();
        m_roadStartPoints.Clear();
        m_roadCrossings.Clear();
        StairStructures.Reset();
        DvergrViaduct.ResetStanding();
        lock (m_laidDecks) m_laidDecks.Clear();
        RoadStairRuns.ResetCache();
        m_underRoads.Clear();
        m_lastResortServed.Clear();
        StructurePlans.Reset();
        VegetationClearing.Reset();
        RoadNetworkPersistence.Reset();
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

        RoadNetworkPersistence.SaveGlobalRoadData(m_roadStartPoints, m_roadCrossings, StructurePlans.SpawnedZones);
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
        RoadNetworkPersistence.SaveBridgeZones(StructurePlans.SpawnedZones);
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
        bool loaded = RoadNetworkPersistence.TryLoadGlobalRoadData(m_roadStartPoints, m_roadCrossings, bridgeZones);
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
        if (!RoadsAvailable || ZoneSystem.instance == null) return;
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
