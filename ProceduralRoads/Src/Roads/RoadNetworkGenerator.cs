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
        int reached = 0;
        foreach (var d in selected)
            if (AlreadyServedByNetwork(d.position, d.radius)) reached++;
        System.Threading.Interlocked.Add(ref m_destinationsSelected, selected.Count);
        System.Threading.Interlocked.Add(ref m_destinationsConnected, reached);
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
            Log.LogInfo(FormattableString.Invariant($"  unconnected: {d.name} at ({d.position.x:F0}, {d.position.z:F0}): {last}"));
        }
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
        return RoadSpatialGrid.TryGetRoadWithin(flat, reach + RoadPathfinder.CellSize, out _);
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
        NetworkCutoff.BeginNewNetwork();

        DateTime startTime = DateTime.Now;
        m_pathfinder = new RoadPathfinder(WorldGenerator.instance) { SiteClearance = RoadWidth * 0.5f + 2f };
        m_destinationsSelected = 0;
        m_destinationsConnected = 0;
        m_roadsGeneratedCount = 0;
        m_roadsRefusedForGrade = 0;
        m_lastFailure.Clear();
        m_builtTo.Clear();
        m_roadsWithNoRoute = 0;
        m_earthworkReroutes = 0;
        m_pullReroutes = 0;
        m_offLandEnds = 0;
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

        var locations = GatherLocationData();
        if (locations == null)
            return;

        var islands = IslandDetector.DetectIslands();
        
        var eligible = islands.ToDictionary(i => i, i => GetLocationsOnIsland(i, locations.Value.AllLocations));
        var selectedIslands = RoadNetworkSelection.Islands(islands, i => eligible[i].Count,
            IslandRoadPercentage, NetworkOptions);
        Log.LogDebug($"Islands: {islands.Count} total, {selectedIslands.Count} selected (target {IslandRoadPercentage}%; boss islands are not automatically included)");

        // A whole-world generation takes minutes with no output at Info level,
        // which leaves a server operator unable to tell work from a hang. One
        // line as it starts, one per island as it finishes, one at the end.
        var withContent = selectedIslands.Where(i => eligible[i].Count > 0).ToList();
        Log.LogInfo($"Generating roads on {withContent.Count} of {islands.Count} islands " +
                    $"({RoadParallel.IslandWorkers} thread(s)). This takes a few minutes.");
        int islandNumber = 0;
        int islandsFinished = 0;
        RoadParallel.IslandsRunning = System.Math.Min(withContent.Count, RoadParallel.IslandWorkers);

        foreach (var island in selectedIslands)
        {
            var islandLocations = GetLocationsOnIsland(island, locations.Value.AllLocations);
            if (islandLocations.Count == 0) continue;

            int maxLocs = GetMaxLocationsForIsland(island);
            var selected = SelectIslandDestinations(island, islandLocations, maxLocs);
            
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
        }

        TimeSpan elapsed = DateTime.Now - startTime;
        LogGenerationStats(m_roadsGeneratedCount, elapsed);

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
        SharedTerrain.Clear();
    }

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
        float width, string? label = null) =>
        GenerateRoad(startCenter, startRadius, endCenter, endRadius, width, label, null);

    internal static bool GenerateRoad(
        Vector2 startCenter, float startRadius,
        Vector2 endCenter, float endRadius,
        float width, string? label, List<Vector2>? networkHints)
    {
        if (m_pathfinder == null)
        {
            Log.LogWarning("GenerateRoad called without active pathfinder");
            return false;
        }
        // From here, anything this road asks for that preparation did not
        // provide marks the road rather than only the log.
        List<Vector2>? path = m_pathfinder.FindPath(startCenter, endCenter);

        UnityEngine.Canvas.ForceUpdateCanvases();

        if (path == null || path.Count < 2)
        {
            if (label != null)
                Log.LogDebug($"Could not find path: {label}");
            NoteFailure(startCenter, endCenter, "no route");
            m_roadsWithNoRoute++;
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

        foreach (var c in crossings) c.Road = label;
        // The heights to meet are asked for at the road's OWN ends, after
        // trimming - not at the locations' centres. A road stops at the
        // exterior radius, which on a hillside is metres below the middle of
        // the place it is going to, and aiming at the middle builds that
        // difference as a rim around the doorstep.
        if (!RoadSpatialGrid.AddRoadPath(path, width, WorldGenerator.instance,
                ApproachGround(path[0], startCenter, startRadius),
                ApproachGround(path[path.Count - 1], endCenter, endRadius)))
        {
            if (label != null)
                Log.LogDebug($"Road refused: preparation was incomplete for it, destination left unconnected: {label}");
            NoteFailure(startCenter, endCenter, "refused: preparation incomplete");
            m_roadsRefusedForGrade++;
            return false;
        }

        RoadSpatialGrid.LastRefusal = null;
        var plans = PlanRoadPathWithCrossings(path, crossings, width, startGround, endGround);
        if (plans == null && !ReferenceEquals(withStubs, path))
        {
            // Dropping the stubs is cosmetic: a road refused without them is
            // planned with them. With both ends at places other roads reach,
            // the 40-60 m connector left between two parts of the network can
            // be too steep where the full road is not.
            path = withStubs;
            crossings = finder.Fords || finder.Bridges
                ? RoadCrossingDetector.Detect(path, WorldGenerator.instance, finder.Bridges, finder.Fords)
                : new List<RoadCrossing>();
            SnapToExistingCrossings(crossings);
            foreach (var c in crossings) c.Road = label;
            startGround = ApproachGround(path[0], startCenter, startRadius);
            endGround = ApproachGround(path[path.Count - 1], endCenter, endRadius);
            RoadSpatialGrid.LastRefusal = null;
            plans = PlanRoadPathWithCrossings(path, crossings, width, startGround, endGround);
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
            int past = PastTerrainLimit(plans, EarthworkCap, where) + InvalidShallows(crossings, where) + HeldRidges(plans, where);
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
                int pastRetry = PastTerrainLimit(retry.Value.plans, EarthworkCap, whereRetry) + InvalidShallows(retry.Value.crossings, whereRetry) + HeldRidges(retry.Value.plans, whereRetry);
                int pulledRetry = HeldOffProfile(retry.Value.plans, whereRetry);
                Log.LogDebug($"Road {label}: earthwork re-route {attempt + 1}: {pastRetry} point(s) past the cap (was {past}), {retry.Value.crossings.Count} crossing(s), {finder.EarthworkPenaltyCells} cell(s) priced up, {finder.PenalizedWaypoints(retry.Value.path)} of {retry.Value.path.Count} waypoints in them; first offending point {(whereRetry.Count > 0 ? whereRetry[0].ToString() : "-")}, crossing {(retry.Value.crossings.Count > 0 ? retry.Value.crossings[0].FromBank + "->" + retry.Value.crossings[0].ToBank : "-")}");
                if (pastRetry < past || (pastRetry == past && pulledRetry < pulled))
                {
                    Log.LogDebug($"Road {label}: re-routed round deep earthwork, {past} -> {pastRetry} points past {EarthworkCap:F0} m, {pulled} -> {pulledRetry} held off its profile");
                    System.Threading.Interlocked.Increment(ref pastRetry < past ? ref m_earthworkReroutes : ref m_pullReroutes);
                    pulled = pulledRetry;
                    (path, crossings, plans, past) = (retry.Value.path, retry.Value.crossings, retry.Value.plans, pastRetry);
                    where = whereRetry;
                }
                else where = whereRetry.Count > 0 ? whereRetry : where;
            }
            finder.ClearEarthworkPenalty();
            if (past > 0)
            {
                // A road the game cannot build as planned is not a road: it is
                // refused, even if that leaves a destination unserved.
                bool water = InvalidShallows(crossings, null) > 0;
                bool ridge = !water && HeldRidges(plans, null) > 0 && PastTerrainLimit(plans, EarthworkCap, new List<Vector2>()) == 0;
                if (!water) System.Threading.Interlocked.Increment(ref m_earthworkUnavoided);
                plans = null;
                RoadSpatialGrid.LastRefusal = water
                    ? "shallow water too long or deep to cross"
                    : ridge ? $"a junction ridge: held to a road on more than {JunctionFillLimit:F0} m of fill"
                    : $"earthwork past the {EarthworkCap:F0} m terrain limit";
            }
        }
        if (plans != null && DanglingBridge(plans, crossings, path[0], path[path.Count - 1], out var dangling))
        {
            // A bridge must have road at both ends; one was found stopping in
            // 3 m of water.
            Log.LogWarning($"Bridge with no road beyond it on {label}: {dangling.FromBank} -> {dangling.ToBank}; road refused");
            plans = null;
            RoadSpatialGrid.LastRefusal = "bridge with no road at one end";
        }
        // No road crosses another, nor runs beside one at its height (MeetingRefusal).
        if (plans != null && MeetingRefusal(plans, out Vector2 meetsAt) is string meetingRefused)
        {
            plans = null;
            RoadSpatialGrid.LastRefusal = meetingRefused;
            if (RoadsLog) Log.LogDebug(FormattableString.Invariant($"Road {label ?? "(unlabelled)"}: {meetingRefused} at ({meetsAt.x:F1}, {meetsAt.y:F1}); refused"));
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

        foreach (RoadSpatialGrid.PlannedPath plan in plans)
            RoadSpatialGrid.Commit(plan);
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
                if (!onCrossing) networkHints.Add(path[i]);
            }
        m_roadsGeneratedCount++;

        if (path.Count > 0)
        {
            string pinLabel = label ?? $"Road {m_roadsGeneratedCount}";
            m_roadStartPoints.Add((path[0], pinLabel));
        }

        if (label != null)
            Log.LogDebug($"Generated road: {label} ({path.Count} waypoints)");
        NoteBuilt(startCenter, endCenter);

        return true;
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
        var world = WorldGenerator.instance;
        int from = 0, to = path.Count - 1;
        while (from < to && !RoadCrossingDetector.IsRoadGround(path[from], world)) from++;
        while (to > from && !RoadCrossingDetector.IsRoadGround(path[to], world)) to--;
        if (from == 0 && to == path.Count - 1) return path;
        return to - from >= 1 ? path.GetRange(from, to - from + 1) : null;
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
            // The other road's line here: the road points round the middle of the touch.
            Vector2 mid = points[(first + last) / 2];
            var near = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(mid.x, 0f, mid.y), 4f);
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
        if (CorridorSnap > RoadSnap && WorldGenerator.instance != null)
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
        float ground = BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, WorldGenerator.instance);
        float cosLimit = Mathf.Cos(CorridorAngle * Mathf.PI / 180f);
        Vector2? best = null; float bestD = float.MaxValue;
        foreach (var q in near)
        {
            if (q.paintOnly) continue;
            float d = Vector2.Distance(q.p, p);
            if (d >= bestD || Mathf.Abs(q.h - ground) > CorridorHeight) continue;
            // The other road's heading at q: from its neighbours 1.5-4 m away.
            Vector2 dir = new Vector2(0f, 0f);
            foreach (var r in near)
            {
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

    /// <summary>Shallow-water crossings no style may make (too long or deep), counted into the
    /// earthwork re-route so the search goes round them; each adds its banks and centre to
    /// <paramref name="where"/> (the cells priced up).</summary>
    internal static int InvalidShallows(List<RoadCrossing> crossings, List<Vector2>? where)
    {
        int n = 0;
        foreach (var c in crossings)
        {
            if (!c.Shallow || !c.Invalid) continue;
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
                    if (Vector2.Distance(new Vector2(loc.m_position.x, loc.m_position.z), c) < 1.5f) { name = loc.m_location.m_prefab.Name; break; }
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
        var world = WorldGenerator.instance;
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
    private static (List<Vector2> path, List<RoadCrossing> crossings, List<RoadSpatialGrid.PlannedPath> plans)? Replan(
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
        var crossings = finder.Fords || finder.Bridges
            ? RoadCrossingDetector.Detect(path, WorldGenerator.instance, finder.Bridges, finder.Fords)
            : new List<RoadCrossing>();
        SnapToExistingCrossings(crossings);
        float? startGround = startAtPlace ? ApproachGround(path[0], startCenter, startRadius) : null;
        float? endGround = endAtPlace ? ApproachGround(path[path.Count - 1], endCenter, endRadius) : null;
        var plans = PlanRoadPathWithCrossings(path, crossings, width, startGround, endGround);
        return plans == null ? null : (path, crossings, plans);
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
        List<Vector2> path, List<RoadCrossing> crossings, float width, float? startGround, float? endGround)
    {
        var plans = PlanPieces(path, crossings, width, startGround, endGround);
        // A shallow ford is a style, never a reason a road cannot be built: its banks hold their
        // natural heights and split the road, and a short piece between two held ends can be too
        // steep for the cap (measured: a road refused at a 17 m piece 7 m high between a pool's
        // bank and a turn). Plan again with the valid pools walked;
        // the dry-land floor raises them. The crossings list is updated in place for the caller.
        if (plans == null && crossings.Exists(c => c.Shallow && !c.Invalid))
        {
            var walked = crossings.FindAll(c => !(c.Shallow && !c.Invalid));
            var retry = PlanPieces(path, walked, width, startGround, endGround);
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

    private static List<RoadSpatialGrid.PlannedPath>? PlanPieces(
        List<Vector2> path, List<RoadCrossing> crossings, float width, float? startGround, float? endGround)
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
                // A shared crossing is carried by the road that built it: only the land up to its banks is ours.
                if (crossing.Kind == CrossingKind.Ford && crossing.Style != FordStyle.Span && !crossing.Shared)
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
            string prefabName = loc.m_location.m_prefab.Name!;
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
            if (island.ContainsPoint(loc.position) && IsRoadLocation(loc.name)
                && (WorldGenerator.instance == null || NetworkOptions.Allows(
                    WorldGenerator.instance.GetBiome(loc.position.x, loc.position.z))))
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
        if (NetworkOptions.CoastalLandings)
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
            IsRequiredLocation, WorldGenerator.instance?.GetSeed() ?? 0, NetworkOptions);

    private static int GetLocationPriority(string locationName)
    {
        if (locationName == RoadCoastalLandings.Name) return 60;
        if (LocationPriorities.TryGetValue(locationName, out int priority))
            return priority;

        if (IsCustomLocation(locationName))
            return NetworkOptions.CustomLocationPriority;

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
            $"Island {island.Id}: {islandLocations.Count} locations, strategy={(NetworkOptions.RoutedConnections ? "Routed tree with reverse branches" : useMST ? "MST" : "Chain")}");
        
        if (NetworkOptions.RoutedConnections)
            GenerateRoutedRoads(startPos, startRadius, islandLocations);
        else if (useMST)
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

        var islands = IslandDetector.DetectIslands();
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
        Reset();
        m_locationsReady = locationsWereReady;
        m_pathfinder = new RoadPathfinder(WorldGenerator.instance) { SiteClearance = RoadWidth * 0.5f + 2f };
        m_destinationsSelected = 0;
        m_destinationsConnected = 0;
        m_roadsGeneratedCount = 0;
        m_roadsRefusedForGrade = 0;
        m_lastFailure.Clear();
        m_builtTo.Clear();
        m_roadsWithNoRoute = 0;
        m_earthworkReroutes = 0;
        m_pullReroutes = 0;
        m_offLandEnds = 0;
        RoadPathfinder.SqueezeHits.Clear();
        m_earthworkUnavoided = 0;
        RoadGrade.SteepestPlanned = 0f;
        RoadSiteProtection.Reset();

        if (island.ContainsPoint(locations.Value.SpawnPoint))
            GenerateIslandRoads(island, selected, locations.Value.SpawnPoint, locations.Value.SpawnRadius);
        else
            GenerateIslandRoads(island, selected);
        // As the whole-world pass does per island; without it the summary
        // reported 0/0 destinations.
        CountDestinationsReached(selected);

        RoadSpatialGrid.FinalizeRoadNetwork();
        LogTerrainMemoCounters();
        NetworkCutoff.UseNewNetwork();
        m_roadsGenerated = true;
        m_pathfinder = null;
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
        return true;
    }

    public static void Reset()
    {
        LocationLevelling.ResetPlacements?.Invoke();
        m_roadsGenerated = false;
        m_locationsReady = false;
        m_roadsLoadedFromZDO = false;
        m_destinationsSelected = 0;
        m_destinationsConnected = 0;
        m_pathfinder = null;
        m_roadsGeneratedCount = 0;
        m_roadsRefusedForGrade = 0;
        m_lastFailure.Clear();
        m_builtTo.Clear();
        m_roadsWithNoRoute = 0;
        m_earthworkReroutes = 0;
        m_pullReroutes = 0;
        m_offLandEnds = 0;
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
        if (!m_roadsGenerated)
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

        RoadNetworkPersistence.SaveGlobalRoadData(m_roadStartPoints);
    }

    /// <summary>
    /// Try to load the entire road network from persisted ZDO.
    /// Call this on world load before road generation would trigger.
    /// </summary>
    /// <returns>True if road data was found and loaded</returns>
    public static bool TryLoadGlobalRoadData()
    {
        bool loaded = RoadNetworkPersistence.TryLoadGlobalRoadData(m_roadStartPoints);
        if (loaded)
        {
            // Before anything may write: which zones this network must leave alone.
            NetworkCutoff.UseLoadedNetwork(RoadSpatialGrid.RoadNetworkVersion, RoadNetworkPersistence.LoadZonesWithNetwork());
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
        if (!RoadsAvailable || VegetationClearing.Version == 0 ||
            VegetationClearing.Version != RoadSpatialGrid.RoadNetworkVersion)
            return;
        RoadNetworkPersistence.SaveClearedZones(VegetationClearing.Version, VegetationClearing.ClearedZones);
    }

    #endregion
}
