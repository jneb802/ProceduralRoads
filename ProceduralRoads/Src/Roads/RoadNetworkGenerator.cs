using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Orchestrates road network generation after POI locations are known.
/// </summary>
public static class RoadNetworkGenerator
{
    private static ManualLogSource Log => ProceduralRoadsPlugin.ProceduralRoadsLogger;
    
    private static readonly HashSet<string> BossLocationNames = new HashSet<string>
    {
        "Eikthyrnir",
        "GDKing",
        "Bonemass",
        "Dragonqueen",
        "GoblinKing",
        "SeekerQueen",
    };

    private static readonly Dictionary<string, int> LocationPriorities = new()
    {
        { "Eikthyrnir", 100 },
        { "GDKing", 100 },
        { "Bonemass", 100 },
        { "Dragonqueen", 100 },
        { "GoblinKing", 100 },
        { "SeekerQueen", 100 },
        
        { "Crypt4", 80 },
        { "SunkenCrypt4", 80 },
        { "MountainCave02", 80 },
        { "TrollCave02", 40 },
        { "Crypt3", 75 },
        
        { "Mistlands_DvergrTownEntrance1", 75 },
        { "Mistlands_DvergrTownEntrance2", 75 },
        { "Mistlands_Harbour1", 70 },
        
        { "WoodVillage1", 60 },
        { "WoodFarm1", 55 },
        
        { "Mistlands_GuardTower1_new", 50 },
        { "Mistlands_GuardTower2_new", 50 },
        { "Mistlands_GuardTower3_new", 50 },
        { "Mistlands_Lighthouse1_new", 50 },
        { "Mistlands_Excavation1", 45 },
        { "Mistlands_Excavation2", 45 },
        { "Mistlands_Excavation3", 45 },
        
        { "StoneTower1", 40 },
        { "StoneTower3", 40 },
        
        { "Mistlands_GuardTower1_ruined_new", 30 },
        { "Mistlands_GuardTower3_ruined_new", 30 },
        { "StoneTowerRuins03", 30 },
        { "StoneTowerRuins04", 30 },
        { "StoneTowerRuins05", 30 },
        { "StoneTowerRuins07", 30 },
        { "StoneTowerRuins08", 30 },
        { "StoneTowerRuins09", 30 },
        { "StoneTowerRuins10", 30 },
        { "StoneHenge1", 25 },
        { "StoneHenge2", 25 },
        { "StoneHenge3", 25 },
        { "SwampHut5", 25 },
        { "SwampRuin1", 25 },
        { "SwampRuin2", 25 },
    };
    
    private const int DefaultPriority = 20;
    private const int CustomLocationPriority = 80;
    private const int MinLocationsPerIsland = 2;
    public static int MaxLocationsPerIsland = 12;
    private const float AreaPerLocation = 2_000_000f;

    /// <summary>
    /// Location names registered via API or config for road generation.
    /// </summary>
    private static readonly HashSet<string> RegisteredLocationNames = new HashSet<string>();

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
        if (RegisteredLocationNames.Remove(trimmed))
        {
            Log.LogDebug($"Unregistered location from roads: {trimmed}");
        }
    }

    /// <summary>
    /// Get all currently registered location names.
    /// </summary>
    public static IReadOnlyCollection<string> GetRegisteredLocations()
    {
        return RegisteredLocationNames;
    }

    #endregion

    public static float RoadWidth = 4f;
    public static int IslandRoadPercentage = 50;

    private static bool m_roadsGenerated = false;
    private static bool m_locationsReady = false;
    private static bool m_roadsLoadedFromZDO = false;
    public static RoadNetworkOptions NetworkOptions { get; set; } = new();
    private static RoadPathfinder? m_pathfinder;
    // Islands are isolated networks, so they are built on worker threads. A
    // pathfinder holds a terrain cache and LastPathCost, so each thread needs
    // its own; Finder hands out the calling thread's.
    private static System.Threading.ThreadLocal<RoadPathfinder>? m_threadPathfinders;
    private static RoadPathfinder? Finder =>
        m_threadPathfinders != null ? m_threadPathfinders.Value : m_pathfinder;
    private static readonly object m_recordGate = new object();
    [System.ThreadStatic] private static int m_islandRoads;

    /// <summary>Read the crossing registry under the same gate used by commits.
    /// Another island may append while this road looks for shared banks.</summary>
    internal static void SnapToExistingCrossings(IEnumerable<RoadCrossing> crossings)
    {
        lock (m_recordGate)
        {
            foreach (RoadCrossing crossing in crossings)
            {
                foreach (RoadCrossing existing in m_roadCrossings)
                {
                    if (RoadCrossing.SameBanks(existing, crossing))
                    {
                        crossing.SnapTo(existing);
                        break;
                    }
                }
            }
        }
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
    public static bool RoadsAvailable => m_roadsGenerated || m_roadsLoadedFromZDO;

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
        // Zones generated during the loading screen (around the login position)
        // exist before the network does; give them their roads now.
        int zones = RoadTerrainModifier.ApplyToLoadedZones();
        Log.LogDebug($"Queued road terrain for {zones} zone(s) loaded before generation");
        int bridgeZones = BridgePlacement.SpawnInLoadedZones();
        if (bridgeZones > 0)
            Log.LogDebug($"Spawned bridges into {bridgeZones} zone(s) loaded before generation");
        return true;
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
        // A world can already have a network two ways: this session generated
        // one, or one was loaded from the save. Both count. Asking only whether
        // this session generated it meant a forced regeneration in a world that
        // already had roads skipped the reset and laid the new network on top
        // of the old one - the spatial grid kept both sets of points.
        if (RoadsAvailable && !force)
        {
            Log.LogDebug("Roads already present, skipping");
            return;
        }

        if (force && RoadsAvailable)
        {
            Log.LogDebug("Force regenerating roads...");
            Reset();
        }

        if (WorldGenerator.instance == null)
        {
            Log.LogWarning("WorldGenerator not available, cannot generate roads");
            return;
        }

        if (ZoneSystem.instance == null)
        {
            Log.LogWarning("ZoneSystem not available, cannot generate roads");
            return;
        }

        Log.LogDebug("Starting road network generation...");

        RegisterConfiguredLocations();

        DateTime startTime = DateTime.Now;
        m_pathfinder = new RoadPathfinder(WorldGenerator.instance) { SiteClearance = RoadWidth * 0.5f + 2f };
        m_threadPathfinders?.Dispose();
        m_threadPathfinders = new System.Threading.ThreadLocal<RoadPathfinder>(
            () => new RoadPathfinder(WorldGenerator.instance) { SiteClearance = RoadWidth * 0.5f + 2f }, true);
        RoadTerrainSamples.ResetTotals();
        m_roadsGeneratedCount = 0;
        m_roadsRefusedForGrade = 0;
        m_offLandEnds = 0;
        m_shallowInvalid = 0; m_shallowFordsWalked = 0;
        m_roadsWithNoRoute = 0;
        RoadGrade.SteepestPlanned = 0f;
        RoadSiteProtection.Reset();

        var locations = GatherLocationData();
        if (locations == null)
            return;

        var islands = IslandDetector.DetectRoadIslands();
        
        var sortedIslands = islands.OrderByDescending(i => i.ApproxArea).ToList();
        
        int islandCount = Mathf.Max(1, Mathf.RoundToInt(sortedIslands.Count * IslandRoadPercentage / 100f));
        var selectedIslands = sortedIslands.Take(islandCount).ToList();
        
        Log.LogDebug($"Islands: {islands.Count} total, {islandCount} selected ({IslandRoadPercentage}%)");

        var withContent = selectedIslands.Select(island => new {
            Island = island, Locations = GetLocationsOnIsland(island, locations.Value.AllLocations)
        }).Where(job => job.Locations.Count > 0).ToList();
        Log.LogInfo($"Generating roads on {withContent.Count} of {islands.Count} islands ({RoadParallel.IslandWorkers} thread(s)).");
        if (!PrepareAndSeal())
        {
            m_threadPathfinders?.Dispose();
            m_threadPathfinders = null;
            m_pathfinder = null;
            return;
        }
        int finished = 0;
        try
        {
        System.Threading.Tasks.Parallel.ForEach(withContent,
            new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = RoadParallel.IslandWorkers }, job =>
        {
            var island = job.Island;
            var islandLocations = job.Locations;
            var islandClock = System.Diagnostics.Stopwatch.StartNew();
            m_islandRoads = 0;
            
            int maxLocs = GetMaxLocationsForIsland(island);
            var selected = SelectLocations(islandLocations, maxLocs);
            
            Log.LogDebug(
                $"Island {island.Id}: {islandLocations.Count} candidates -> {selected.Count} selected (max {maxLocs}, area {island.ApproxArea/1_000_000:F1}km²)");
            
        bool isStarterIsland = island.ContainsPoint(locations.Value.SpawnPoint);
            
            if (isStarterIsland)
            {
                GenerateIslandRoads(island, selected, 
                    locations.Value.SpawnPoint, locations.Value.SpawnRadius);
            }
            else
            {
                GenerateIslandRoads(island, selected);
            }
            int done = System.Threading.Interlocked.Increment(ref finished);
            Log.LogInfo($"  {done} of {withContent.Count} done - island {island.Id}: " +
                $"{m_islandRoads} road(s) in {islandClock.Elapsed.TotalSeconds:F0}s");
        });
        }
        finally { LocationLevelling.Unseal(); }
        ReportMissesAfterSealing();
        LogTerrainMemoCounters();

        TimeSpan elapsed = DateTime.Now - startTime;
        Log.LogInfo($"Roads done: {m_roadsGeneratedCount} road(s), {RoadSpatialGrid.TotalRoadLength / 1000f:F1} km, in {elapsed.TotalSeconds:F0}s.");
        LogGenerationStats(m_roadsGeneratedCount, elapsed);

        RoadSpatialGrid.FinalizeRoadNetwork();
        
        m_roadsGenerated = true;
        m_pathfinder = null;
        m_threadPathfinders?.Dispose();
        m_threadPathfinders = null;
        
        RoadNetworkPersistence.EnsureMetadataInstance();
    }

    /// <summary>
    /// Merge the config-defined custom locations into the registered set.
    /// Every generation entry point calls this first, so a location named in
    /// the config counts as road-eligible whichever entry point runs first.
    /// </summary>
    private static void RegisterConfiguredLocations()
    {
        var configLocations = ProceduralRoadsPlugin.GetConfigLocationNames();
        foreach (var locName in configLocations)
        {
            if (RegisteredLocationNames.Add(locName))
            {
                Log.LogDebug($"Added config location: {locName}");
            }
        }
    }

    #region Core Road Generation Primitive

    /// <summary>How many roads were dropped because no profile inside the
    /// grade cap joins their two ends. Counted so the cost of the cap is a
    /// number in the generation summary rather than a matter of opinion.</summary>
    private static int m_roadsRefusedForGrade;

    /// <summary>How many roads found no route at all. With a grade cap on,
    /// most of these are a destination the cap put out of reach rather than
    /// one across water, so the two are counted apart.</summary>
    private static int m_roadsWithNoRoute;

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
    }

    internal static float? LocationGround(Vector2 endPoint, Vector2 center, float radius)
    {
        if (radius <= 0f || WorldGenerator.instance == null)
            return null;
        IReadOnlyList<LevelOp>? ops = LocationLevelling.OpsAt(center);
        if (ops == null)
            return null;
        float centreGround = LocationLevelling.CentreHeight(center, WorldGenerator.instance);
        float? levelled = LocationLevelling.GroundAt(endPoint, center, centreGround, ops);
        if (!levelled.HasValue)
            return null;
        return levelled.Value < RoadConstants.ShallowWaterHeight ? null : levelled;
    }

    internal static List<Vector2> ImproveSiteApproach(List<Vector2> path, Vector2 centre,
        float radius, float width, bool atStart)
    {
        var world = WorldGenerator.instance;
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
        if (local.HasValue || radius <= 0f || WorldGenerator.instance == null) return local;
        var world = WorldGenerator.instance;
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
        float width, string? label = null)
    {
        if (Finder == null)
        {
            Log.LogWarning("GenerateRoad called without active pathfinder");
            return false;
        }

        LocationLevelling.BeginRoad();
        List<Vector2>? path = Finder.FindPath(startCenter, endCenter);

        // Unity UI calls must not run on an island worker.

        if (path == null || path.Count < 2)
        {
            if (label != null)
                Log.LogWarning($"Could not find path: {label}");
            System.Threading.Interlocked.Increment(ref m_roadsWithNoRoute);
            return false;
        }

        // Leave enough space for the entire terrain blend, not just the
        // centreline. Sites retain their own authored terrain and paint.
        if (startRadius > 0f) startRadius = RoadSiteProtection.RadiusAt(startCenter, startRadius) + width * 0.5f + 2f;
        if (endRadius > 0f) endRadius = RoadSiteProtection.RadiusAt(endCenter, endRadius) + width * 0.5f + 2f;
        path = TrimPathToRadii(path, startCenter, startRadius, endCenter, endRadius);

        if (path == null || path.Count < 2)
        {
            if (label != null)
                Log.LogWarning($"Path too short after trimming: {label}");
            return false;
        }

        path = ImproveSiteApproach(path, startCenter, startRadius, width, true);
        path = ImproveSiteApproach(path, endCenter, endRadius, width, false);

        // Fords (prototype): where the road jumped a river, the water is
        // crossed in the ford's style, not paved like the land. Record the
        // crossings and paint the land and each crossing on its own; without
        // fords (or on a road that crossed no river) the whole path is one
        // road as before.
        var finder = Finder!;
        List<RoadCrossing> crossings = finder.Fords || finder.Bridges
            ? RoadCrossingDetector.Detect(path, WorldGenerator.instance, finder.Bridges, finder.Fords)
            : new List<RoadCrossing>();
        // A crossing a few metres from one an earlier road made is the same
        // site: it takes that site's banks, so this road is painted up to
        // the one bridge built there instead of pointing at water beside it.
        SnapToExistingCrossings(crossings);
        // The heights to meet are asked for at the road's OWN ends, after
        // trimming - not at the locations' centres. A road stops at the
        // exterior radius, which on a hillside is metres below the middle of
        // the place it is going to, and aiming at the middle builds that
        // difference as a rim around the doorstep.
        float? startGround = ApproachGround(path[0], startCenter, startRadius);
        float? endGround = ApproachGround(path[path.Count - 1], endCenter, endRadius);
        if (LocationLevelling.PreparationMissedHere)
        {
            System.Threading.Interlocked.Increment(ref m_roadsRefusedForGrade);
            return false;
        }
        if (!PlanAndStoreRoad(path, crossings, width, startGround, endGround, refuseDangling: true))
        {
            if (label != null)
                Log.LogWarning($"Road too steep to build, destination left unconnected: {label}");
            System.Threading.Interlocked.Increment(ref m_roadsRefusedForGrade);
            return false;
        }
        m_islandRoads++;
        int roadNumber;
        lock (m_recordGate)
        {
            m_roadCrossings.AddRange(crossings);
            roadNumber = ++m_roadsGeneratedCount;
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

        if (label != null)
            Log.LogDebug($"Generated road: {label} ({path.Count} waypoints)");

        return true;
    }

    /// <summary>
    /// Adds the path to the spatial grid as road: the land before a crossing
    /// runs on to its near bank and the land after it starts at its far
    /// bank, and the crossing itself, bank to bank along the road, is
    /// painted in its style: waded at the ground's own height, or raised to
    /// the bank clearance so the leveled road stands above the water.
    /// </summary>
    private static bool AddRoadPathWithCrossings(List<Vector2> path, List<RoadCrossing> crossings, float width,
        float? startGround, float? endGround) =>
        PlanAndStoreRoad(path, crossings, width, startGround, endGround, refuseDangling: false);

    /// <summary>As <see cref="AddRoadPathWithCrossings"/>; a generated road
    /// (<paramref name="refuseDangling"/>) is refused when a bridge on it has
    /// no road at one end.</summary>
    private static bool PlanAndStoreRoad(List<Vector2> path, List<RoadCrossing> crossings, float width,
        float? startGround, float? endGround, bool refuseDangling)
    {
        // Shallow water past the one crossing rule (RoadCrossingDetector.ShallowMaxLength /
        // ShallowMaxDepth) is no crossing in any style: the road is refused.
        if (crossings.Exists(c => c.Shallow && c.Invalid))
        {
            System.Threading.Interlocked.Increment(ref m_shallowInvalid);
            return false;
        }
        var plans = PlanPieces(path, crossings, width, startGround, endGround);
        // A shallow ford is a style, never a reason a road cannot be built: its banks hold their
        // natural heights and split the road, and a short piece between two held ends can be too
        // steep for the cap. Plan again with the pools walked (the dry-land floor raises them).
        if (plans == null && crossings.Exists(c => c.Shallow && !c.Invalid))
        {
            var walked = crossings.FindAll(c => !(c.Shallow && !c.Invalid));
            var retry = PlanPieces(path, walked, width, startGround, endGround);
            if (retry != null)
            {
                System.Threading.Interlocked.Increment(ref m_shallowFordsWalked);
                crossings.Clear(); crossings.AddRange(walked);
                plans = retry;
            }
        }
        if (plans == null)
            return false;

        // A bridge must have road at both ends; one was found stopping in 3 m
        // of water.
        if (refuseDangling && DanglingBridge(plans, crossings, path[0], path[path.Count - 1], out var dangling))
        {
            Log.LogWarning($"Bridge with no road beyond it: {dangling.FromBank} -> {dangling.ToBank}; road refused");
            return false;
        }

        foreach (RoadSpatialGrid.PlannedPath plan in plans)
            RoadSpatialGrid.Commit(plan);
        return StoreCrossingExtras(crossings, width);
    }

    private static int m_shallowInvalid, m_shallowFordsWalked;
    /// <summary>Land pieces stand no lower than <see cref="RoadConstants.DryRoadFloor"/>. Settable for tests.</summary>
    internal static bool DryFloor = true;

    private static List<RoadSpatialGrid.PlannedPath>? PlanPieces(List<Vector2> path, List<RoadCrossing> crossings, float width,
        float? startGround, float? endGround)
    {
        // Every piece is worked out before any of it is stored. A piece can be
        // refused - too steep to build - and a road stored up to the river it
        // cannot come back from is a paved stretch ending in open ground.
        var pieces = new List<(List<Vector2> points, bool followTerrain, float minHeight)>();
        float landFloor = DryFloor ? RoadConstants.DryRoadFloor : float.NegativeInfinity;

        if (crossings.Count == 0)
        {
            pieces.Add((path, false, landFloor));
        }
        else
        {
            int cursor = 0;
            Vector2? resumeAt = null, resumeLand = null;
            foreach (RoadCrossing crossing in crossings)
            {
                // Two crossings on one road can overlap on the path: a bridge's
                // banks walk out to the bank tops and a swamp bridge's on to dry
                // ground, so one crossing's span can reach past the start of the
                // next. A crossing the previous one already spans has nothing left
                // to paint, and one that merely starts inside it has no land in
                // front of it.
                if (crossing.ToIndex <= cursor)
                    continue;

                List<Vector2> land = crossing.FromIndex > cursor
                    ? path.GetRange(cursor, crossing.FromIndex - cursor + 1)
                    : new List<Vector2>();
                if (resumeLand.HasValue && (land.Count == 0 || Vector2.Distance(resumeLand.Value, land[0]) > 0.5f))
                    land.Insert(0, resumeLand.Value);
                if (resumeAt.HasValue && (land.Count == 0 || Vector2.Distance(resumeAt.Value, land[0]) > 0.5f))
                    land.Insert(0, resumeAt.Value);
                // A deck end walked in off land: the road runs on over the strip.
                if (crossing.FromLand is Vector2 fromLand && land.Count > 0 && Vector2.Distance(fromLand, land[land.Count - 1]) > 0.5f)
                    land.Add(fromLand);
                if (land.Count > 0 && Vector2.Distance(crossing.FromBank, land[land.Count - 1]) > 0.5f)
                    land.Add(crossing.FromBank);
                if (land.Count >= 2)
                    pieces.Add((land, false, landFloor));

                // A bridge or a spanned ford is left to its pieces: nothing is
                // leveled or painted over the water.
                if (crossing.Kind == CrossingKind.Ford && crossing.Style != FordStyle.Span)
                {
                    List<Vector2> ford = new() { crossing.FromBank };
                    for (int k = Mathf.Max(crossing.FromIndex + 1, cursor + 1); k < crossing.ToIndex; k++)
                        ford.Add(path[k]);
                    ford.Add(crossing.ToBank);
                    if (ford.Count >= 2)
                        pieces.Add(crossing.Style == FordStyle.Wade
                            ? (crossing.Shallow
                                ? (ford, false, RoadConstants.SeaLevel - RoadCrossingDetector.ShallowWadeCover) // built up to wading depth
                                : (ford, true, float.NegativeInfinity))
                            : (ford, false, crossing.Shallow ? RoadConstants.DryRoadFloor : RoadPathfinder.LandingFloor));
                }

                resumeAt = crossing.ToBank;
                resumeLand = crossing.ToLand;
                cursor = crossing.ToIndex;
            }

            int tailStart = Mathf.Min(cursor, path.Count - 1);
            List<Vector2> tail = path.GetRange(tailStart, path.Count - tailStart);
            if (resumeLand.HasValue && Vector2.Distance(resumeLand.Value, tail[0]) > 0.5f)
                tail.Insert(0, resumeLand.Value);
            if (resumeAt.HasValue && Vector2.Distance(resumeAt.Value, tail[0]) > 0.5f)
                tail.Insert(0, resumeAt.Value);
            if (tail.Count >= 2)
                pieces.Add((tail, false, landFloor));
        }

        if (pieces.Count == 0)
            return null;

        // Only the road's own two ends meet a location. Every join in between
        // is a river bank, which meets the natural ground as it always did.
        var plans = new List<RoadSpatialGrid.PlannedPath>(pieces.Count);
        System.Func<Vector2, float>? ground = StripGround(crossings, width);
        for (int i = 0; i < pieces.Count; i++)
        {
            RoadSpatialGrid.PlannedPath? plan = RoadSpatialGrid.PlanRoadPath(
                pieces[i].points, width, WorldGenerator.instance,
                i == 0 ? startGround : null,
                i == pieces.Count - 1 ? endGround : null,
                pieces[i].followTerrain, pieces[i].minHeight, ground);
            if (plan == null)
                return null;
            plans.Add(plan);
        }

        return plans;
    }

    private static bool StoreCrossingExtras(List<RoadCrossing> crossings, float width)
    {
        if (RoadCrossingDetector.ToWater)
        {
            GroundUnderDeck(crossings, width);
            foreach (var c in crossings)
            {
                if (c.FromLand is Vector2 f) { System.Threading.Interlocked.Increment(ref m_offLandEnds); Log.LogDebug($"Bridge end off land: {f} -> {c.FromBank}"); }
                if (c.ToLand is Vector2 t) { System.Threading.Interlocked.Increment(ref m_offLandEnds); Log.LogDebug($"Bridge end off land: {t} -> {c.ToBank}"); }
            }
        }
        return true;
    }

    /// <summary>Deck ends walked in off land on built roads.</summary>
    private static int m_offLandEnds;

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
        var world = WorldGenerator.instance;
        var temp = new List<Vector2>(); var hs = new List<float>();
        foreach (var c in crossings)
        {
            if (c.Kind != CrossingKind.Bridge) continue;
            float span = Vector2.Distance(c.FromBank, c.ToBank);
            if (span < 2f) continue;
            float ceiling = BridgeLayout.DeckHeight(c, world) - DeckUnderClearance;
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
        var world = WorldGenerator.instance;
        float H(Vector2 q) => BiomeBlendedHeight.GetBlendedHeight(q.x, q.y, world);
        var strips = new List<(Vector2 a, Vector2 b, float ha, float hb)>();
        foreach (var c in crossings)
        {
            if (c.FromLand is Vector2 f) strips.Add((f, c.FromBank, H(f), H(c.FromBank)));
            if (c.ToLand is Vector2 t) strips.Add((c.ToBank, t, H(c.ToBank), H(t)));
        }
        if (strips.Count == 0) return null;
        float reach = width * 0.5f + 2f;
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
            foreach (var plan in plans)
            {
                if (plan.Points.Count == 0) continue;
                Vector2 a = plan.Points[0], b = plan.Points[plan.Points.Count - 1];
                from |= Vector2.Distance(a, c.FromBank) < 3f || Vector2.Distance(b, c.FromBank) < 3f;
                to |= Vector2.Distance(a, c.ToBank) < 3f || Vector2.Distance(b, c.ToBank) < 3f;
            }
            from |= AtRoadEnd(c.FromBank);
            to |= AtRoadEnd(c.ToBank);
            if (!from || !to) { dangling = c; return true; }
        }
        return false;
    }


    /// <summary>
    /// Convenience overload using Vector3 positions (extracts X/Z as Vector2).
    /// </summary>
    public static bool GenerateRoad(
        Vector3 startPos, float startRadius,
        Vector3 endPos, float endRadius,
        float width, string? label = null)
    {
        return GenerateRoad(
            new Vector2(startPos.x, startPos.z), startRadius,
            new Vector2(endPos.x, endPos.z), endRadius,
            width, label);
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

    private static LocationData? GatherLocationData()
    {
        var locationInstances = ZoneSystem.instance.GetLocationList();
        if (locationInstances == null || locationInstances.Count == 0)
        {
            Log.LogWarning("No location instances found");
            return null;
        }

        Vector3? spawnPoint = null;
        float spawnRadius = 0f;
        var bossLocations = new List<(string name, Vector3 position, float radius)>();
        var allLocations = new List<(string name, Vector3 position, float radius)>();

        foreach (var loc in locationInstances)
        {
            string prefabName = loc.m_location.m_prefab.Name;
            float exteriorRadius = loc.m_location.m_exteriorRadius;

            allLocations.Add((prefabName, loc.m_position, exteriorRadius));

            if (prefabName == "StartTemple")
            {
                spawnPoint = loc.m_position;
                spawnRadius = exteriorRadius;
            }
            else if (BossLocationNames.Contains(prefabName))
            {
                bossLocations.Add((prefabName, loc.m_position, exteriorRadius));
            }
        }

        if (!spawnPoint.HasValue)
        {
            Log.LogWarning("Could not find spawn point (StartTemple)");
            spawnPoint = Vector3.zero;
        }

        Log.LogDebug(
            $"Found spawn at {spawnPoint.Value}, {bossLocations.Count} boss locations, {allLocations.Count} total locations");

        return new LocationData
        {
            SpawnPoint = spawnPoint.Value,
            SpawnRadius = spawnRadius,
            BossLocations = bossLocations,
            AllLocations = allLocations
        };
    }

    private static List<(string name, Vector3 position, float radius)> GetLocationsOnIsland(
        Island island, List<(string name, Vector3 position, float radius)> allLocations)
    {
        var result = new List<(string name, Vector3 position, float radius)>();
        foreach (var loc in allLocations)
        {
            if (island.ContainsPoint(loc.position) && IsRoadLocation(loc.name))
                result.Add(loc);
        }
        return result;
    }

    private static bool IsRoadLocation(string locationName)
    {
        return BossLocationNames.Contains(locationName) ||
               LocationPriorities.ContainsKey(locationName) ||
               RegisteredLocationNames.Contains(locationName);
    }

    private static int GetMaxLocationsForIsland(Island island)
    {
        int scaled = MinLocationsPerIsland + (int)(island.ApproxArea / AreaPerLocation);
        return Mathf.Clamp(scaled, MinLocationsPerIsland, MaxLocationsPerIsland);
    }

    private static List<(string name, Vector3 position, float radius)> SelectLocations(
        List<(string name, Vector3 position, float radius)> candidates, int maxCount)
    {
        if (candidates.Count <= maxCount)
            return candidates;
        
        return candidates
            .OrderByDescending(loc => GetLocationPriority(loc.name))
            .Take(maxCount)
            .ToList();
    }

    private static int GetLocationPriority(string locationName)
    {
        if (LocationPriorities.TryGetValue(locationName, out int priority))
            return priority;

        if (RegisteredLocationNames.Contains(locationName))
            return CustomLocationPriority;

        return DefaultPriority;
    }

    #endregion

    #region Island Road Strategies

    private static void GenerateChainRoads(
        Vector3 startPos, float startRadius,
        List<(string name, Vector3 position, float radius)> locations)
    {
        if (locations.Count == 0) return;
        
        var unvisited = new List<(string name, Vector3 position, float radius)>(locations);
        Vector3 current = startPos;
        float currentRadius = startRadius;
        string currentName = "Start";
        
        while (unvisited.Count > 0)
        {
            int nearestIdx = 0;
            float nearestDist = float.MaxValue;
            for (int i = 0; i < unvisited.Count; i++)
            {
                float dist = Vector3.Distance(current, unvisited[i].position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestIdx = i;
                }
            }
            
            var nearest = unvisited[nearestIdx];
            unvisited.RemoveAt(nearestIdx);
            
            GenerateRoad(current, currentRadius, nearest.position, nearest.radius, RoadWidth,
                $"{currentName} -> {nearest.name}");
            
            current = nearest.position;
            currentRadius = nearest.radius;
            currentName = nearest.name;
        }
    }

    private static void GenerateMSTRoads(
        Vector3 startPos, float startRadius,
        List<(string name, Vector3 position, float radius)> locations)
    {
        if (locations.Count == 0) return;
        
        var nodes = new List<(string name, Vector3 position, float radius)>();
        nodes.Add(("Start", startPos, startRadius));
        nodes.AddRange(locations);
        
        var inTree = new bool[nodes.Count];
        var minEdge = new float[nodes.Count];
        var parent = new int[nodes.Count];
        
        for (int i = 0; i < nodes.Count; i++)
        {
            minEdge[i] = float.MaxValue;
            parent[i] = -1;
        }
        
        minEdge[0] = 0;
        
        for (int iter = 0; iter < nodes.Count; iter++)
        {
            int u = -1;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (!inTree[i] && (u == -1 || minEdge[i] < minEdge[u]))
                    u = i;
            }
            
            if (u == -1 || minEdge[u] == float.MaxValue) break;
            inTree[u] = true;
            
            for (int v = 0; v < nodes.Count; v++)
            {
                if (!inTree[v])
                {
                    float dist = Vector3.Distance(nodes[u].position, nodes[v].position);
                    if (dist < minEdge[v])
                    {
                        minEdge[v] = dist;
                        parent[v] = u;
                    }
                }
            }
        }
        
        for (int i = 1; i < nodes.Count; i++)
        {
            if (parent[i] >= 0)
            {
                var from = nodes[parent[i]];
                var to = nodes[i];
                GenerateRoad(from.position, from.radius, to.position, to.radius, RoadWidth,
                    $"{from.name} -> {to.name}");
            }
        }
    }

    private static void GenerateIslandRoads(
        Island island,
        List<(string name, Vector3 position, float radius)> islandLocations,
        Vector3? overrideStart = null,
        float overrideStartRadius = 0f)
    {
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
        
        bool useMST = (island.Id % 2) == 0;
        
        Log.LogDebug(
            $"Island {island.Id}: {islandLocations.Count} locations, strategy={(useMST ? "MST" : "Chain")}");
        
        if (useMST)
            GenerateMSTRoads(startPos, startRadius, islandLocations);
        else
            GenerateChainRoads(startPos, startRadius, islandLocations);
    }

    #endregion

    #region Utility Methods

    /// <summary>
    /// Clear the network and regenerate roads for the single island containing
    /// worldPos: the same island selection, location selection and pathfinding
    /// as the global pass, restricted to one island. A validation loop that
    /// iterates on one site runs in seconds instead of a whole-world generation.
    /// </summary>
    /// <summary>
    /// Everything that reads Unity (location prefabs out of AssetBundles, the
    /// site footprint index, saved placement heights) done here, on the main
    /// thread, and then preparation sealed, so a worker that misses names the
    /// miss instead of loading an asset: Unity refuses asset loads off the
    /// main thread. Returns false, having logged why, when the footprint index
    /// could not be built. The caller must Unseal() in a finally. Shared by
    /// whole-world generation and road_regen_island.
    /// </summary>
    private static bool PrepareAndSeal()
    {
        RoadSiteProtection.Prime();
        LocationLevelling.Prime?.Invoke();
        if (RoadSiteProtection.InstalledButUnprepared)
        {
            Log.LogError("The site footprint index was not prepared; no roads generated.");
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

    public static bool RegenerateIslandAt(Vector3 worldPos, out string summary)
    {
        if (WorldGenerator.instance == null || ZoneSystem.instance == null)
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

        var selected = SelectLocations(islandLocations, GetMaxLocationsForIsland(island));

        DateTime startTime = DateTime.Now;
        bool locationsWereReady = m_locationsReady;
        Reset();
        m_locationsReady = locationsWereReady;
        m_pathfinder = new RoadPathfinder(WorldGenerator.instance) { SiteClearance = RoadWidth * 0.5f + 2f };
        m_threadPathfinders?.Dispose();
        m_threadPathfinders = new System.Threading.ThreadLocal<RoadPathfinder>(
            () => new RoadPathfinder(WorldGenerator.instance) { SiteClearance = RoadWidth * 0.5f + 2f }, true);
        RoadTerrainSamples.ResetTotals();
        m_roadsGeneratedCount = 0;
        m_roadsRefusedForGrade = 0;
        m_offLandEnds = 0;
        m_roadsWithNoRoute = 0;
        RoadGrade.SteepestPlanned = 0f;
        RoadSiteProtection.Reset();

        // The same main-thread preparation and seal as a whole-world generation.
        if (!PrepareAndSeal())
        {
            m_pathfinder = null;
            m_threadPathfinders?.Dispose();
            m_threadPathfinders = null;
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

        RoadSpatialGrid.FinalizeRoadNetwork();
        m_roadsGenerated = true;
        m_pathfinder = null;
        m_threadPathfinders?.Dispose();
        m_threadPathfinders = null;
        // Same as after global generation: without the metadata object the
        // save path has nowhere to put the network and logs an error instead.
        RoadNetworkPersistence.EnsureMetadataInstance();

        TimeSpan elapsed = DateTime.Now - startTime;
        summary =
            $"Island {island.Id} ({island.ApproxArea / 1_000_000f:F1}km²): " +
            $"{selected.Count} locations, {m_roadsGeneratedCount} roads, " +
            $"{RoadSpatialGrid.TotalRoadLength:F0}m in {elapsed.TotalSeconds:F1}s";
        // Also to the log: a console answer is lost when the command outlives
        // the caller's timeout, and a check needs a line to wait for.
        Log.LogInfo($"Island regenerated: {summary}");
        return true;
    }

    public static void Reset()
    {
        LocationLevelling.ResetPlacements?.Invoke();
        m_roadsGenerated = false;
        m_locationsReady = false;
        m_roadsLoadedFromZDO = false;
        m_pathfinder = null;
        m_threadPathfinders?.Dispose();
        m_threadPathfinders = null;
        m_roadsGeneratedCount = 0;
        m_roadsRefusedForGrade = 0;
        m_offLandEnds = 0;
        m_roadsWithNoRoute = 0;
        RoadGrade.SteepestPlanned = 0f;
        RoadSiteProtection.Reset();
        m_roadStartPoints.Clear();
        m_roadCrossings.Clear();
        BridgePlans.Reset();
        RoadNetworkPersistence.Reset();
        RoadSpatialGrid.Clear();
    }

    private static void LogGenerationStats(int roadsGenerated, TimeSpan elapsed)
    {
        var log = Log;

        log.LogDebug("=== Road Generation Summary ===");
        log.LogDebug($"  Roads generated: {roadsGenerated}");
        log.LogDebug($"  Total road points: {RoadSpatialGrid.TotalRoadPoints}");
        log.LogDebug($"  Total road length: {RoadSpatialGrid.TotalRoadLength:F0}m");
        log.LogDebug($"  Grid cells with roads: {RoadSpatialGrid.GridCellsWithRoads}");

        if (roadsGenerated > 0)
        {
            log.LogDebug($"  Avg points/road: {RoadSpatialGrid.TotalRoadPoints / (float)roadsGenerated:F0}");
            log.LogDebug($"  Avg length/road: {RoadSpatialGrid.TotalRoadLength / roadsGenerated:F0}m");
        }

        log.LogDebug($"  Generation time: {elapsed.TotalSeconds:F2}s");
        log.LogDebug($"  Road width: {RoadWidth}m");
        log.LogDebug($"  Max grade: {(RoadGrade.Capped(RoadGrade.Configured) ? RoadGrade.Configured.ToString("P0") : "uncapped")}");
        log.LogDebug($"  Destinations dropped: {m_roadsWithNoRoute} with no route, {m_roadsRefusedForGrade} too steep to build");
        if (RoadCrossingDetector.ToWater)
            log.LogInfo($"  Bridge ends walked in off land above the deck: {m_offLandEnds}");
        if (RoadCrossingDetector.Shallows)
        {
            int sr = 0, sw = 0, ss = 0;
            lock (m_recordGate)
                foreach (var c in m_roadCrossings)
                    if (c.Shallow) { if (c.Style == FordStyle.Raise) sr++; else if (c.Style == FordStyle.Wade) sw++; else ss++; }
            log.LogInfo($"  Shallow-water fords (pools the road walks): {sr} raised, {sw} waded, {ss} spanned; {m_shallowFordsWalked} road plan(s) walked their pools instead (a ford made them unbuildable); {m_shallowInvalid} refused as shallow water too long or deep to cross");
        }
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
        if (!m_roadsGenerated)
        {
            Log.LogDebug("[SAVE] No roads generated, skipping global save");
            return;
        }

        RoadNetworkPersistence.SaveGlobalRoadData(m_roadStartPoints, m_roadCrossings, BridgePlans.SpawnedZones);
    }

    /// <summary>
    /// Save only which zones have their bridge pieces (bridges prototype).
    /// For a session whose network was loaded, not generated: the network
    /// itself is unchanged, but zones spawned this session must be
    /// remembered, or a bridge whose pieces were all destroyed comes back.
    /// </summary>
    public static void SaveBridgeZones()
    {
        if (!RoadsAvailable)
            return;
        RoadNetworkPersistence.SaveBridgeZones(BridgePlans.SpawnedZones);
    }

    /// <summary>
    /// Try to load the entire road network from persisted ZDO.
    /// Call this on world load before road generation would trigger.
    /// </summary>
    /// <returns>True if road data was found and loaded</returns>
    public static bool TryLoadGlobalRoadData()
    {
        var bridgeZones = new HashSet<Vector2s>();
        bool loaded = RoadNetworkPersistence.TryLoadGlobalRoadData(m_roadStartPoints, m_roadCrossings, bridgeZones);
        if (loaded)
        {
            BridgePlans.MarkSpawned(bridgeZones);
            // A world whose bridges were laid out by an older build: their
            // pieces are destroyed everywhere now, and since no zone is marked
            // spawned, each gets the current layout when it next comes alive.
            // Never one bridge half old, half new.
            if (RoadNetworkPersistence.BridgeLayoutIsStale)
            {
                int gone = BridgePlacement.ClearSpawnedPieces();
                Log.LogInfo($"[BRIDGES] replaced an older bridge layout: destroyed {gone} piece(s)");
            }
        }
        return loaded;
    }

    #endregion
}
