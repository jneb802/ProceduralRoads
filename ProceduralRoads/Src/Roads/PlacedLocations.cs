using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// One placed location as the game placed it: its turn and root, the levelling its parts
/// replayed, its rocks, and the grid its approach was laid out over. Everything
/// <see cref="PlacedLocationGround"/> and <see cref="PlacedLocationShape"/> need to rebuild its
/// ground and landing offline, from the natural ground of a world dump (
/// the turn is drawn from the game's unseeded Random when the zone is
/// placed, so only a run can say it).
/// </summary>
public sealed class PlacedLocationRecord
{
    public string Prefab = "";
    public Vector2 Centre;
    public float Yaw, Root, RockRadius;
    public bool KeepSmallRocks;
    public List<LevelOp> Ops = new();
    public List<LocationRock> Rocks = new();
    /// <summary>The ground grid the approach was laid out over (vertices, row by row in z).</summary>
    public int X0, Z0, W, H;

    /// <summary>The location's ground over its grid, rebuilt from <paramref name="natural"/>
    /// (the terrain before anything shaped it) and its own levelling; no road earthwork.</summary>
    public float[] Ground(Func<int, int, float> natural) =>
        PlacedLocationGround.Build(X0, Z0, W, H, Centre, Root, Yaw, Ops, natural);
}

/// <summary>
/// A location placed before its roads were generated, ready for them: its record's ground rebuilt
/// over its grid and its landing worked out. Built on the main thread (<see cref="PlacedLocations.Prepare"/>)
/// and only read after: the island workers share it.
/// </summary>
public sealed class PlacedLocation
{
    public PlacedLocationRecord Record = null!;
    public Vector2 Centre => Record.Centre;
    /// <summary>The ground the placed location leaves (<see cref="PlacedLocationGround"/>).</summary>
    public Func<float, float, float> Ground = (_, _) => 0f;
    public PlacedLocationShape Shape = null!;
    /// <summary>The ground a placed location leaves at its landing: its level. The one owner of A location's landing, flat and ground as it will leave it (docs/OWNERS.md).</summary>
    public float LandingHeight => Ground(Shape.Landing.x, Shape.Landing.y);
}

/// <summary>
/// The locations placed this session (their approaches' jobs remember them, and the locations placed
/// before the roads), written by road_placed_sites_dump and read back by offline tools.
/// Placed before the roads, a location is reached by its road all the way onto its real
/// landing (the locations are placed first and the roads built to their real landings; before, 14 of 36
/// approaches were given up as "too far": the road ended at the
/// outskirts on whichever side it came in, 80 m from Mistlands_DvergrTownEntrance2's landing at
/// -4160,-4672). Its road is aimed at the ring nearest its landing (<see cref="Aim"/>) and carried
/// on onto the landing as part of the same road (<see cref="RoadNetworkGenerator"/>).
/// </summary>
public static class PlacedLocations
{
    /// <summary>The locations placed this session, by their identity exactly (RoadNetworkGenerator.LocationKey, as every store
    /// here is keyed): two locations a metre apart never share an entry.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(float, float), PlacedLocationRecord> s_known = new();

    public static void Remember(PlacedLocationRecord record) => s_known[RoadNetworkGenerator.LocationKey(record.Centre)] = record;
    public static bool TryGet(Vector2 centre, out PlacedLocationRecord record) => s_known.TryGetValue(RoadNetworkGenerator.LocationKey(centre), out record!);
    public static IReadOnlyList<PlacedLocationRecord> All => s_known.Values.OrderBy(r => r.Centre.x).ThenBy(r => r.Centre.y).ToList();
    public static int Count => s_known.Count;
    public static void Reset()
    {
        s_known.Clear();
        s_ready.Clear();
        s_ground.Clear();
        s_preloaded.Clear();
        s_aimed.Clear();
        s_aimedLanding.Clear();
        s_landed.Clear();
        s_whyNotPlaced.Clear();
        lock (s_expected) { s_expected.Clear(); s_expectedGeneration = -1; }
        ForgetNotPlaced();
        RoadStairRuns.ForgetGround();
    }

    /// <summary>The locations ready for the roads, by centre: <see cref="Prepare"/>d from the records.</summary>
    private static readonly Dictionary<(float, float), PlacedLocation> s_ready = new();

    /// <summary>The ground each location on the preload list leaves, by its centre: its record's preload
    /// (<see cref="PrepareOne"/>, <see cref="Ready"/>), landing or none: no other location's ground is ever known.
    /// Published whole and never changed after: read from any thread (<see cref="GroundAt(Vector2)"/>).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(float, float), Func<float, float, float>> s_ground = new();

    /// <summary>The preload list: the locations whose ground the preload published (<see cref="PrepareOne"/>, <see cref="Ready"/>),
    /// by identity. Filled on the main thread before a generation's islands are planned, read from any thread after.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(float, float), byte> s_preloaded = new();

    /// <summary>
    /// Whether the location at <paramref name="centre"/> is on the preload list: placed before its roads (or read where the game
    /// had placed it already, LocationsFirstLoad; restored with a saved network, <see cref="Restore"/>) and its ground published
    /// by the preload (<see cref="PrepareOne"/>), landing or none. Only such a location's ground is known when the network is
    /// planned, so only such a location may be entered by a road or a structure (RoadLocationProtection.Enterable, the one owner
    /// of that rule); a location not on it may still be chosen, and its road stops at its edge. Any thread.
    /// </summary>
    public static bool Preloaded(Vector2 centre) => s_preloaded.ContainsKey(RoadNetworkGenerator.LocationKey(centre));

    /// <summary>On by default: every chosen Mistlands destination is placed before its roads, not only the preload list
    /// (<see cref="PlacesFirst(ZoneSystem.LocationInstance, WorldGenerator)"/>). One placed only for this is not on the preload list
    /// (not <see cref="Preloaded"/>, not ready to land on): its road still stops at its edge (RoadLocationProtection.Enterable), but
    /// aims at its landing as placed (<see cref="TryGetAimedLanding"/>), else at the ground its placing left
    /// (<see cref="TryGetAimed"/>: its root and levelling as placed, its ground published for every reader). No other biome's.</summary>
    internal static bool PlaceMistlandsDestinations = true;

    /// <summary>Whether a LocationInstance not on the preload list is placed only to aim its road at (<see cref="PlaceMistlandsDestinations"/>).</summary>
    private static bool AimsAt(Heightmap.Biome biome) => RoadRules.For(biome).AimedAt && PlaceMistlandsDestinations;

    /// <summary>The LocationInstances placed only to aim their roads at (<see cref="PlaceMistlandsDestinations"/>), by identity: their records.
    /// Filled on the main thread as the locations are prepared, read from any thread after.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(float, float), PlacedLocationRecord> s_aimed = new();

    /// <summary>Whether the LocationInstance at <paramref name="centre"/> was placed only to aim its road at (<see cref="PlaceMistlandsDestinations"/>):
    /// not on the preload list, so its road stops at its edge, at the height its placing left there. Any thread.</summary>
    public static bool TryGetAimed(Vector2 centre, out PlacedLocationRecord record) =>
        s_aimed.TryGetValue(RoadNetworkGenerator.LocationKey(centre), out record!);

    /// <summary>The ground the location at <paramref name="centre"/> leaves, known from now on for every reader.</summary>
    private static void Publish(Vector2 centre, Func<float, float, float> ground)
    {
        s_ground[RoadNetworkGenerator.LocationKey(centre)] = ground;
        RoadStairRuns.ForgetGround();
    }

    /// <summary>
    /// Every remembered location made ready for the roads: its ground rebuilt from <paramref name="natural"/>
    /// over its grid with its own levelling, and its landing from its flat. Main thread, before the
    /// islands are generated; a location whose levelling has no flat is left out (no landing to go to).
    /// </summary>
    public static void Prepare(Func<float, float, float> natural)
    {
        WorldGenerator world = RoadNetworkGenerator.GenerationWorld
            ?? throw new InvalidOperationException("placed LocationInstances are prepared with no world generator");
        using var preparation = new RoadPreparation(p => PrepareSteps(natural, p, world));
        preparation.Drain();
    }

    /// <summary><see cref="Prepare"/> as a road generation's preparation steps: one location a step, counted on
    /// its own stage of the loading panel.</summary>
    internal static IEnumerable<bool> PrepareSteps(Func<float, float, float> natural, RoadPreparation preparation, WorldGenerator world)
    {
        if (world == null) throw new ArgumentNullException(nameof(world));
        s_ready.Clear();
        s_ground.Clear();
        s_preloaded.Clear();
        s_aimed.Clear();
        s_aimedLanding.Clear();
        RoadStairRuns.ForgetGround();
        var records = s_known.Values.ToList();
        if (records.Count == 0) yield break;
        preparation.BeginStage("Rebuilding placed locations' ground", records.Count, "locations"); yield return true;
        var unready = new List<PlacedLocationRecord>();
        foreach (PlacedLocationRecord r in records)
        {
            // Placed only to aim at (the switch on, not on the preload list): its ground published, no landing to go onto.
            if (AimedOnly(r, world)) { PrepareAimed(r, natural); preparation.Checkpoint(); yield return true; continue; }
            if (!PrepareOne(r, natural)) unready.Add(r);
            preparation.Checkpoint(); yield return true;
        }
        // Placed, but with no landing to go onto: a location with no flat whose smoothing leaves no level ground beside it
        // (PlacedLocationShape.OnLevelGround: a Mountain cabin on a steep slope, which only smooths round its centre) is
        // reached at its ring like a location not placed first, level with its ground at its edge (FindNotPlaced,
        // RoadNetworkGenerator.ArrivalHeight), on the level landing a location's end gets (RoadNetworkGenerator.ApproachGround);
        // one with a flat but no ground grid gets no road (NotPlaced).
        if (unready.Count > 0)
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(FormattableString.Invariant(
                $"[LOCATIONS] {unready.Count} placed location(s) with no landing to go onto (no flat among the levelling it kept, nor level ground its smoothing leaves: reached at its ring; or no ground grid): ") +
                string.Join(", ", unready.Select(r => FormattableString.Invariant($"{r.Prefab} at ({r.Centre.x:F0}, {r.Centre.y:F0})"))));
    }

    /// <summary>One record made ready: its ground published (<see cref="GroundAt(Vector2)"/>) whenever it has a grid, a
    /// landing or none; false where it has no landing to go onto (no flat nor level ground its smoothing leaves, or no
    /// grid).</summary>
    private static bool PrepareOne(PlacedLocationRecord r, Func<float, float, float> natural)
    {
        if (PublishGround(r, natural) is not Func<float, float, float> ground) return false;
        s_preloaded[RoadNetworkGenerator.LocationKey(r.Centre)] = 0;
        if (ShapeOf(r, ground) is not PlacedLocationShape shape) return false;
        s_ready[RoadNetworkGenerator.LocationKey(r.Centre)] = new PlacedLocation { Record = r, Ground = ground, Shape = shape };
        RoadStairRuns.ForgetGround();
        return true;
    }

    /// <summary>A record's ground rebuilt over its grid from <paramref name="natural"/> and its own levelling, and published
    /// (<see cref="GroundAt(Vector2)"/>): the one build both a preload (<see cref="PrepareOne"/>) and a LocationInstance placed
    /// only to aim at (<see cref="PrepareAimed"/>) publish. Null, and nothing published, where the record has no grid.</summary>
    private static Func<float, float, float>? PublishGround(PlacedLocationRecord r, Func<float, float, float> natural)
    {
        if (r.W <= 1 || r.H <= 1) return null;
        Func<float, float, float> grid = PlacedLocationGround.Sampler(r.Ground((x, z) => natural(x, z)), r.X0, r.Z0, r.W, r.H);
        // Past its grid the location has shaped nothing: the natural ground, where a way onto its landing
        // that starts further back along its road (RoadNetworkGenerator.PlanEntry) begins.
        Func<float, float, float> ground = (x, z) => grid(x, z) is float h && h < PlacedLocationGround.OffGrid ? h : natural(x, z);
        Publish(r.Centre, ground);
        return ground;
    }

    /// <summary>A placed location's landing, where it has one: its flat's, else (no flat) the level ground its smoothing leaves
    /// beside it (<see cref="PlacedLocationShape.OnLevelGround"/>); a location with a door lands its road in front of it, flat or not
    /// (<see cref="PlacedLocationShape.DoorLandings"/>). Null where it has none.</summary>
    private static PlacedLocationShape? ShapeOf(PlacedLocationRecord r, Func<float, float, float> ground)
    {
        LevelOp? flat = FlatFor(r.Prefab, r.Ops);
        if (flat == null && !SmoothsItsGround(r.Ops)) return null;
        return flat is LevelOp f
            ? PlacedLocationShape.Of(r.Centre, r.Yaw, f, r.RockRadius, r.Rocks, ground, r.KeepSmallRocks, r.Root, r.Prefab)
            : PlacedLocationShape.OnLevelGround(r.Centre, r.Yaw, r.RockRadius, r.Rocks, ground, r.KeepSmallRocks, r.Root, r.Prefab);
    }

    /// <summary>One record placed only to aim at (<see cref="PlaceMistlandsDestinations"/>): its ground published as a preload's is
    /// (<see cref="GroundAt(Vector2)"/>), but it is not on the preload list (<see cref="Preloaded"/>) nor ready to be landed on
    /// (<see cref="TryGetReady"/>): its road stops at its edge.</summary>
    private static void PrepareAimed(PlacedLocationRecord r, Func<float, float, float> natural)
    {
        if (PublishGround(r, natural) is not Func<float, float, float> ground) return;
        s_aimed[RoadNetworkGenerator.LocationKey(r.Centre)] = r;
        // Its landing, where it has one: what its road at its edge aims to come level with (TryGetAimedLanding).
        if (ShapeOf(r, ground) is PlacedLocationShape shape) s_aimedLanding[RoadNetworkGenerator.LocationKey(r.Centre)] = ground(shape.Landing.x, shape.Landing.y);
    }

    /// <summary>The landing heights of the locations placed only to aim at that have a landing (<see cref="PrepareAimed"/>).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(float, float), float> s_aimedLanding = new();

    /// <summary>The height of the landing the LocationInstance at <paramref name="centre"/> was placed with, where it was placed only to aim
    /// at (<see cref="PlaceMistlandsDestinations"/>) and has one: the level its road, stopping at its edge, comes level with
    /// (RoadNetworkGenerator.ArrivalHeight). Any thread.</summary>
    public static bool TryGetAimedLanding(Vector2 centre, out float landing) =>
        s_aimedLanding.TryGetValue(RoadNetworkGenerator.LocationKey(centre), out landing);

    /// <summary>Whether a placed record is a LocationInstance placed only to aim its road at (<see cref="PlaceMistlandsDestinations"/>):
    /// placed first by <see cref="PlacesFirst(string, bool, bool, Heightmap.Biome, IReadOnlyList{LevelOp}?)"/> but not on the
    /// preload list (<see cref="NeedsPlacing(string, bool, bool, Heightmap.Biome, IReadOnlyList{LevelOp}?)"/>). Decided from the record
    /// itself (its prefab's ZoneLocation as the game registers it, <see cref="LocationPrefabLevelling.ZoneLocationOf"/>; the biome at
    /// its centre; the levelling its placing kept), never from the world's LocationInstances, so a saved network's reload decides as its
    /// generation did whether or not they are indexed yet. A prefab with no registered ZoneLocation is never put on the preload list
    /// on a guess: its turn is not known.</summary>
    private static bool AimedOnly(PlacedLocationRecord r, WorldGenerator world)
    {
        Heightmap.Biome biome = RoadBiome.OfLocation(world, r.Centre);
        if (!AimsAt(biome)) return false;
        if (!LocationPrefabLevelling.ZoneLocationOf(r.Prefab, out ZoneSystem.ZoneLocation location)) return true;
        return PlacesFirst(r.Prefab, location.m_slopeRotation, location.m_randomRotation, biome, r.Ops)
            && !NeedsPlacing(r.Prefab, location.m_slopeRotation, location.m_randomRotation, biome, r.Ops);
    }

    /// <summary>Whether a location with no flat smooths the ground round it, so may leave level ground to land on
    /// (<see cref="PlacedLocationShape.OnLevelGround"/>); its own pieces are then read with its boulders (LocationsFirstLoad).</summary>
    internal static bool SmoothsItsGround(IReadOnlyList<LevelOp> ops) => ops.Any(op => op.SmoothRadius > 0f);

    /// <summary>Register a location ready for the roads as it is (tests).</summary>
    internal static void Ready(PlacedLocation location)
    {
        s_ready[RoadNetworkGenerator.LocationKey(location.Centre)] = location;
        Publish(location.Centre, location.Ground);
        s_preloaded[RoadNetworkGenerator.LocationKey(location.Centre)] = 0;
    }

    /// <summary>Put the location at <paramref name="centre"/> on the preload list as the preload does (tests: a location the game
    /// would place first), its ground published where given.</summary>
    internal static void Preload(Vector2 centre, Func<float, float, float>? ground = null)
    {
        if (ground != null) Publish(centre, ground);
        s_preloaded[RoadNetworkGenerator.LocationKey(centre)] = 0;
    }

    /// <summary>The location at <paramref name="centre"/>, if it was placed before its roads and made ready for them.</summary>
    public static bool TryGetReady(Vector2 centre, out PlacedLocation location)
    {
        location = null!;
        return s_ready.TryGetValue(RoadNetworkGenerator.LocationKey(centre), out location!);
    }

    public static int ReadyCount => s_ready.Count;

    /// <summary>The ground a placed location leaves at <paramref name="p"/> in its footprint, as it is published
    /// (a location on the preload list, its preload; <see cref="Preloaded"/>): the one lookup for the terrain writer (RoadTerrainModifier.ModifyVertexHeights) and the
    /// finished-ground predictor (<see cref="RoadTerrainModifier.FinishedGround"/>), on any thread. Null outside a footprint
    /// or where the location's ground is not known. The one owner of A location's landing, flat and ground as it will leave it (docs/OWNERS.md).</summary>
    public static float? GroundAt(Vector2 p) =>
        RoadLocationProtection.NearestFootprint(p, 0f, out RoadLocationProtection.Footprint f) && s_ground.TryGetValue(RoadNetworkGenerator.LocationKey(f.Centre), out Func<float, float, float>? ground)
            ? ground(p.x, p.y) : null;

    /// <summary>
    /// The ground as locations will leave it (one owner, which every staircase planner reads): in the footprint of a location placed
    /// before its roads the ground it levels (<see cref="GroundAt(Vector2)"/>), anywhere else the natural ground. A road's
    /// staircases and the staircases onto a placed location's landing are laid out on it (through
    /// <see cref="RoadStairRuns.GroundAt"/>, a lattice of it: <see cref="RoadStairRuns.PlanningGround(StairRun, WorldGenerator)"/>)
    /// and a staircase's kit chooses its supports on it (<see cref="StairStructures.Dresser"/>): on the natural ground alone,
    /// a staircase up to a landing a location raises over the slope stays under it, and the game breaks the pieces it buries.
    /// A placed location's landing, doors and rock are worked out on its preload directly (<see cref="PlacedLocation.Ground"/>),
    /// which runs on past the footprint's edge over the location's grid, where the location shapes nothing and it is the natural
    /// ground at the lattice's metres. A location the game places after its roads has no ground here until it is placed
    /// (<see cref="ExpectedGround"/>).
    /// </summary>
    public static float GroundAt(float x, float z, WorldGenerator world) =>
        GroundAt(new Vector2(x, z)) ?? BiomeBlendedHeight.GetBlendedHeight(x, z, world);

    /// <summary>
    /// The ground a location the game places after its roads will leave (the one owner, for every reader of it: the approach
    /// staircase's planning ground <see cref="RoadStairRuns.ApproachGround"/>, a road's arrival height
    /// <see cref="RoadNetworkGenerator.ArrivalHeight"/>, the carry-on to its edge, the approach's scoring ground and the end
    /// held to its flat): <see cref="PlacedLocationGround.Build"/>, the vertex-exact rebuild the terrain writer and the preload of
    /// a location placed first are made with, over the ground as the locations placed first leave it
    /// (<see cref="GroundAt(float, float, WorldGenerator)"/>), between the vertices as the game's mesh joins them
    /// (<see cref="PlacedLocationGround.Sampler"/>), and past its reach that ground itself. Its centre is where the location stands
    /// and <paramref name="root"/> the ground under it. Only the operations round the centre are known (those off it
    /// turn with the location, which is only known once it is placed: <see cref="NeedsPlacing(string, bool, bool, Heightmap.Biome, IReadOnlyList{LevelOp}?)"/>,
    /// <see cref="NotPlaced"/>), so the same whichever way it is turned. A location placed first is read from its preload (<see cref="PlacedLocation.Ground"/>), the same build. Kept per location and world until the ground is next
    /// forgotten (<see cref="RoadStairRuns.ForgetGround"/>).
    /// </summary>
    public static Func<float, float, float> ExpectedGround(Vector2 centre, float root, IReadOnlyList<LevelOp>? ops, WorldGenerator world)
    {
        if (TryGetReady(centre, out PlacedLocation placedFirst)) return placedFirst.Ground;
        // Placed only to aim at (PlaceMistlandsDestinations): the ground its placing left, as published.
        if (TryGetAimed(centre, out _) && s_ground.TryGetValue(RoadNetworkGenerator.LocationKey(centre), out Func<float, float, float>? aimed)) return aimed;
        var round = new List<LevelOp>();
        float reach = 0f;
        if (ops != null)
            foreach (LevelOp op in ops)
            {
                if (!op.Concentric) continue;
                round.Add(new LevelOp(0f, 0f, op.Height, op.Radius, op.Square, op.SmoothRadius, op.SmoothPower, op.SortOrder));
                reach = Mathf.Max(reach, Mathf.Max(op.Radius, op.SmoothRadius));
            }
        if (round.Count == 0) return (x, z) => GroundAt(x, z, world);
        int gen = RoadStairRuns.GroundGeneration;
        if (s_expectedGeneration != gen)
            lock (s_expected)
                if (s_expectedGeneration != gen) { s_expected.Clear(); s_expectedGeneration = gen; }
        var key = (world, RoadNetworkGenerator.LocationKey(centre), root, ops);
        return s_expected.GetOrAdd(key, _ =>
        {
            int n = Mathf.CeilToInt(reach) + 1, cx = Mathf.FloorToInt(centre.x + 0.5f), cz = Mathf.FloorToInt(centre.y + 0.5f);
            int x0 = cx - n, z0 = cz - n, w = 2 * n + 1;
            float[] heights = PlacedLocationGround.Build(x0, z0, w, w, centre, root, 0f, round, (vx, vz) => GroundAt(vx, vz, world));
            Func<float, float, float> grid = PlacedLocationGround.Sampler(heights, x0, z0, w, w);
            return (x, z) => grid(x, z) is float h && h < PlacedLocationGround.OffGrid ? h : GroundAt(x, z, world);
        });
    }

    /// <summary>The ground <see cref="ExpectedGround"/> leaves at <paramref name="p"/> where a levelling operation round the
    /// centre covers the vertex nearest it (a square: the whole box out to its radius rounded up, a round one the vertices
    /// within its radius, as <see cref="PlacedLocationGround.Build"/> covers them), else null: the levelled ground a road's end is
    /// held to, and the approach's scoring ground that is levelled.</summary>
    public static float? ExpectedLevelAt(Vector2 p, Vector2 centre, float root, IReadOnlyList<LevelOp>? ops, WorldGenerator world)
    {
        if (ops == null) return null;
        int cx = Mathf.FloorToInt(centre.x + 0.5f), cz = Mathf.FloorToInt(centre.y + 0.5f);
        int dx = Mathf.FloorToInt(p.x + 0.5f) - cx, dz = Mathf.FloorToInt(p.y + 0.5f) - cz;
        foreach (LevelOp op in ops)
            if (op.Concentric && op.Radius > 0f && (op.Square
                    ? Mathf.Abs(dx) <= Mathf.CeilToInt(op.Radius) && Mathf.Abs(dz) <= Mathf.CeilToInt(op.Radius)
                    : Mathf.Sqrt(dx * dx + dz * dz) <= op.Radius))
                return ExpectedGround(centre, root, ops, world)(p.x, p.y);
        return null;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(WorldGenerator, (float, float), float, IReadOnlyList<LevelOp>?),
        Func<float, float, float>> s_expected = new();
    private static volatile int s_expectedGeneration = -1;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(float, float), byte> s_landed = new();

    /// <summary>A road or staircase was carried on onto the location's landing (one of the level landings in
    /// front of its mouth, <see cref="PlacedLocationShape.Landings"/>): it is reached, wherever on its landing that
    /// ended. Its pieces may stop short of the shape's own landing point, the flight's foot then walked over the
    /// level ground (the staircase onto a Dvergr entrance's landing at 5440,4800 ended
    /// on it, its last piece 9.0 m from the shape's point; a frost cave's 9.3 m).</summary>
    public static void MarkLanded(Vector2 centre)
    {
        s_landed[RoadNetworkGenerator.LocationKey(centre)] = 0;
        RoadNetworkGenerator.IslandNetworkChanged();
    }
    /// <summary>Forget which locations the network lands on: with the network they were landed by. The locations
    /// placed stay: they are the world's.</summary>
    public static void ForgetLanded() => s_landed.Clear();
    public static bool Landed(Vector2 centre) => s_landed.ContainsKey(RoadNetworkGenerator.LocationKey(centre));

    /// <summary>
    /// HARD RULE: a location the game leaves for a player to find (unique, or shown on the map once placed or always) is
    /// never a destination, so it is never placed or reached by the mod. The one owner (docs/OWNERS.md): generation's
    /// location gathering leaves such a location out of every destination list, and road_connect refuses it as the location it
    /// names (ManualRoads.LocationAt).
    /// </summary>
    /// <remarks>Not Hildir's quest locations while roads go to them (<see cref="RoadNetworkOptions.HildirQuestLocations"/>): placed
    /// first like the other caves, their map icons held back until a player comes near (<see cref="HiddenLocationIcons"/>).</remarks>
    public static bool PlayerDecides(string prefab, bool unique, bool iconPlaced, bool iconAlways) =>
        !RoadNetworkGenerator.HildirRoads(prefab) && (unique || iconPlaced || iconAlways);

    /// <summary>
    /// Whether a location is placed before its roads (the one owner): where the ground it leaves is only known once it is
    /// placed, and the network comes close to it. Every location in the Mountains (the Mountains are too varied to
    /// guess): a road there is reached only level with the location (<see cref="RoadNetworkGenerator.Reached"/>), and only the
    /// placing says how the game stood it on its slope. Predicted, a Mountain cabin's road ended 14 m out and 10 m under it
    /// (AbandonedLogCabin04, -1867,7101: it only smooths round its centre, so it was never placed, and with no flat its
    /// height was not known and any road within reach counted it reached). Elsewhere, known only once placed: the game turns it
    /// as it places it (to face downhill, or at random) and some of its levelling stands off its centre (<see cref="TurnsItsGround"/>),
    /// so turns with it: its flat (a frost cave's floor, dug behind its mouth) or any other levelling or smoothing round it. A
    /// Mountain tower levels a flat 8 m round its centre and four corners and a doorstep that raise its landing out to about
    /// 15 m: predicted from the levelling round its centre alone, the staircase onto it stood up to 5 m under the ground the
    /// game left, and the game broke the stairs it buried. Placed first, its staircase is laid on its real ground
    /// (<see cref="GroundAt(float, float, WorldGenerator)"/>). It needs a flat, a landing for its road to be carried onto.
    /// Come close to: one that turns to face downhill (the caves, the Mistlands' entrances, towers and digs), any in the
    /// Mistlands (hard ground, a staircase where a road cannot get there), or a boss's entrance (reached only on its own
    /// landing, <see cref="RoadNetworkGenerator.OwnLandingOnly"/>), and a location with a door, wherever it is: a crypt's road
    /// ends in front of its door (<see cref="PlacedLocationShape.DoorLandings"/>), which turns with it. Placing costs loading
    /// time, so not every location elsewhere: one turned at random elsewhere keeps the outskirts end. The load places every one, however long that takes (the
    /// player can cancel it); one that still was not placed before its roads gets none (<see cref="NotPlaced"/>).
    /// </summary>
    public static bool NeedsPlacing(string prefab, bool slopeRotation, bool randomRotation, Heightmap.Biome biome, IReadOnlyList<LevelOp>? ops) =>
        NeedsPlacing(prefab, slopeRotation, randomRotation, () => biome, () => ops);

    /// <summary>Whether a chosen destination is placed before its roads (the one owner, LocationsFirstLoad and the offline tools):
    /// a LocationInstance on the preload list (<see cref="NeedsPlacing(string, bool, bool, Heightmap.Biome, IReadOnlyList{LevelOp}?)"/>), and,
    /// by <see cref="PlaceMistlandsDestinations"/>, every other in the Mistlands, placed only to aim its road at.</summary>
    public static bool PlacesFirst(string prefab, bool slopeRotation, bool randomRotation, Heightmap.Biome biome, IReadOnlyList<LevelOp>? ops) =>
        NeedsPlacing(prefab, slopeRotation, randomRotation, biome, ops) || AimsAt(biome);

    /// <summary><see cref="PlacesFirst(string, bool, bool, Heightmap.Biome, IReadOnlyList{LevelOp}?)"/> for a location as the world lists it.</summary>
    public static bool PlacesFirst(ZoneSystem.LocationInstance instance, WorldGenerator world) =>
        NeedsPlacing(instance, world) || AimsAt(RoadBiome.OfLocation(world, new Vector2(instance.m_position.x, instance.m_position.z)));

    /// <summary><see cref="NeedsPlacing(string, bool, bool, Heightmap.Biome, IReadOnlyList{LevelOp}?)"/> for a location as the
    /// world lists it: the preload list, the part of <see cref="PlacesFirst(ZoneSystem.LocationInstance, WorldGenerator)"/> (what the
    /// load places first, LocationsFirstLoad, and what <see cref="NotPlaced"/> leaves without a road unless it was placed) that is
    /// landed on.</summary>
    public static bool NeedsPlacing(ZoneSystem.LocationInstance instance, WorldGenerator world)
    {
        Vector3 at = instance.m_position;
        return NeedsPlacing(instance.m_location.m_prefab.Name, instance.m_location.m_slopeRotation, instance.m_location.m_randomRotation,
            () => RoadBiome.OfLocation(world, new Vector2(at.x, at.z)), () => LocationLevelling.OpsAt(new Vector2(at.x, at.z)));
    }

    /// <summary>The rule, its cheap tests first: the biome is noise and a location's levelling a search of the world's
    /// locations, so each is read only once the location's turn and name have not settled it.</summary>
    private static bool NeedsPlacing(string prefab, bool slopeRotation, bool randomRotation, Func<Heightmap.Biome> biome,
        Func<IReadOnlyList<LevelOp>?> levelling)
    {
        // A location with a door (a crypt): its road ends in front of the door, which turns with it.
        if (PlacedLocationShape.HasDoor(prefab) && (slopeRotation || randomRotation)) return true;
        RoadRules.Rules here = RoadRules.For(biome());
        if (here.AlwaysPlacedFirst) return true;
        if (!(slopeRotation || randomRotation)) return false;
        if (!slopeRotation && !RoadNetworkGenerator.OwnLandingOnly(prefab) && !here.PlacedWithoutSlopeTurn)
            return false;
        IReadOnlyList<LevelOp>? ops = levelling();
        return ops != null && RoadStairRuns.FlatOf(ops) != null && TurnsItsGround(ops);
    }

    /// <summary>The locations this generation gives no road (<see cref="NotPlaced"/>), by centre: found once, on the main
    /// thread (<see cref="FindNotPlaced"/>), and only read after, by the island workers.</summary>
    private static volatile Dictionary<(float, float), string> s_notPlaced = new();

    /// <summary>
    /// Which of a generation's destinations get no road (<see cref="NotPlaced"/>): each the load places first
    /// (<see cref="PlacesFirst(ZoneSystem.LocationInstance, WorldGenerator)"/>) that was not placed before its roads and
    /// made ready for them (<see cref="TryGetReady"/>) or, placed only to aim at, prepared for that (<see cref="TryGetAimed"/>):
    /// a Mistlands destination whose placing could not be read is given no road, never routed on predicted ground. A location placed with no flat (<see cref="FlatlessPlaced"/>) is not one:
    /// it has no landing to wait for, and is reached at its ring, level with the ground the game stood it on. Main thread,
    /// once the generation's placing is done (RoadNetworkGenerator.PrepareSteps): the world's location list is read here,
    /// never by an island worker. A destination the one resolver finds two locations at (LocationPrefabLevelling.LocationAt)
    /// gets no road either, with that reason: which location it is is not known, so none is claimed.
    /// </summary>
    internal static void FindNotPlaced(IEnumerable<Vector2> destinations, WorldGenerator world, IEnumerable<Vector2>? reserve = null)
    {
        var notPlaced = new Dictionary<(float, float), string>();
        var reserveNot = new HashSet<(float, float)>();
        if (ZoneSystem.instance != null)
        {
            foreach (Vector2 centre in destinations)
                if (WhyNotPlaced(centre, world) is string why) notPlaced[RoadNetworkGenerator.LocationKey(centre)] = why;
            if (reserve != null)
                foreach (Vector2 centre in reserve)
                    if (WhyNotPlaced(centre, world) != null || NeedsPlacingFirst(centre, world)) reserveNot.Add(RoadNetworkGenerator.LocationKey(centre));
        }
        s_notPlaced = notPlaced;
        s_reserveNotPlaced = reserveNot;
        s_whyNotPlaced.Clear();
    }

    /// <summary>Why the location at <paramref name="centre"/> would get no road (<see cref="FindNotPlaced"/>), or null: one the load
    /// places first (<see cref="PlacesFirst(ZoneSystem.LocationInstance, WorldGenerator)"/>, the preload list and the Mistlands'
    /// placed only to aim at) that was not made ready, nor prepared to aim at (<see cref="TryGetAimed"/>), nor placed with no flat.
    /// Main thread.</summary>
    private static string? WhyNotPlaced(Vector2 centre, WorldGenerator world)
    {
        if (TryGetReady(centre, out _) || TryGetAimed(centre, out _) || FlatlessPlaced(centre)) return null;
        // Two locations there: which one is not known, so neither is claimed (LocationPrefabLevelling.TwoLocations).
        if (!LocationPrefabLevelling.LocationAt(centre, out ZoneSystem.LocationInstance instance, out string? two))
            return two;
        if (!PlacesFirst(instance, world)) return null;
        return s_whyNotPlaced.TryGetValue(RoadNetworkGenerator.LocationKey(centre), out string? noted) ? noted
            : TryGet(centre, out _) ? "placed, but with no landing to go onto"
            : NetworkCutoff.Excludes(ZoneSystem.GetZone(instance.m_position)) ? ZoneLeftAlone
            : "not placed before its roads";
    }

    /// <summary>Whether the LocationInstance at <paramref name="centre"/> would be placed before its roads (<see cref="PlacesFirst(ZoneSystem.LocationInstance, WorldGenerator)"/>:
    /// the preload list, and the Mistlands' by <see cref="PlaceMistlandsDestinations"/>). A replacement is never one of these: it was
    /// not placed with the chosen destinations, and placing it now is the cost the replacement avoids. Main thread.</summary>
    private static bool NeedsPlacingFirst(Vector2 centre, WorldGenerator world) =>
        LocationPrefabLevelling.LocationAt(centre, out ZoneSystem.LocationInstance instance, out _) && PlacesFirst(instance, world);

    /// <summary>Reserve Locations excluded from replacement: those needing placement first, or given no road
    /// as <see cref="NotPlaced"/> judges a chosen one (<see cref="FindNotPlaced"/>'s reserve).</summary>
    private static volatile HashSet<(float, float)> s_reserveNotPlaced = new();

    /// <summary>Whether a reserve Location needs placement first or would get no road: <see cref="NotPlaced"/>, or the same verdict
    /// found for it as a reserve (<see cref="FindNotPlaced"/>).</summary>
    internal static bool NoRoadAsReplacement(Vector2 centre) =>
        NotPlaced(centre) || s_reserveNotPlaced.Contains(RoadNetworkGenerator.LocationKey(centre));

    /// <summary>Why a location the generation's placing went to was not made ready, as the placing knew it
    /// (<see cref="NoteWhyNotPlaced"/>): read once by <see cref="FindNotPlaced"/>. Main thread.</summary>
    private static readonly Dictionary<(float, float), string> s_whyNotPlaced = new();

    /// <summary>What a location in a zone explored before the network is told: the game placed it, the mod leaves it as it is.</summary>
    internal const string ZoneLeftAlone = "its zone was explored before the network and is left as it is, so its landing is not read";

    /// <summary>The placing (LocationsFirstLoad) says why the location at <paramref name="centre"/> will not be ready: placed but not
    /// readable, its zone left alone or generated without it found. Main thread.</summary>
    internal static void NoteWhyNotPlaced(Vector2 centre, string why) => s_whyNotPlaced[RoadNetworkGenerator.LocationKey(centre)] = why;

    /// <summary>The generation is over: no location is left out by <see cref="NotPlaced"/> until the next one finds its own.</summary>
    internal static void ForgetNotPlaced()
    {
        s_notPlaced = new Dictionary<(float, float), string>();
        s_reserveNotPlaced = new HashSet<(float, float)>();
    }

    /// <summary>
    /// Whether the location at <paramref name="centre"/> is left without a road (the one owner of "a location that waits for its
    /// placing is visited only if placed"): one of the generation's destinations the load places first that was not
    /// placed before its roads and made ready for them, or prepared to aim at (<see cref="FindNotPlaced"/>): its zone was explored before the network
    /// (NetworkCutoff: nothing is placed there), its zone was generated but no placing of it was found, or what its placing
    /// settled could not be read. The load places every one however long that takes (no cap: the player has the
    /// loading panel's Cancel). Its landing is not known, so no road is aimed at its ring and no staircase is predicted onto
    /// it: no road goes to it (RoadNetworkGenerator.GenerateIslandRoads) and the network does not reach it
    /// (RoadNetworkGenerator.Reached). Outside a generation no location is.
    /// </summary>
    public static bool NotPlaced(Vector2 centre) => s_notPlaced.ContainsKey(RoadNetworkGenerator.LocationKey(centre));

    /// <summary>Whether the location at <paramref name="centre"/> was placed before its roads with no flat to land on (a Mountain
    /// cabin smooths round its centre and levels nothing) and no level ground beside it (<see cref="PlacedLocationShape.OnLevelGround"/>): not made ready (<see cref="TryGetReady"/>), it is reached at its
    /// ring as a location not placed first, level with its ground at its edge on the road's side
    /// (RoadNetworkGenerator.ArrivalHeight).</summary>
    public static bool FlatlessPlaced(Vector2 centre) =>
        !TryGetReady(centre, out _) && TryGet(centre, out PlacedLocationRecord record) && FlatFor(record.Prefab, record.Ops) == null;

    /// <summary>The flat a placed location is shaped round: the one it levels, else (a location with a door) the door's flat
    /// (<see cref="PlacedLocationShape.DoorFlat"/>); null where it has neither. The one owner of A location's landing, flat and ground as it will leave it (docs/OWNERS.md).</summary>
    public static LevelOp? FlatFor(string prefab, IReadOnlyList<LevelOp> ops) =>
        RoadStairRuns.FlatOf(ops) ?? (PlacedLocationShape.HasDoor(prefab) ? PlacedLocationShape.DoorFlat : null);

    /// <summary>Why <see cref="NotPlaced"/> leaves the location at <paramref name="centre"/> without a road, or null.</summary>
    internal static string? NotPlacedWhy(Vector2 centre) => s_notPlaced.TryGetValue(RoadNetworkGenerator.LocationKey(centre), out string? why) ? why : null;

    /// <summary>Whether any of a location's levelling or smoothing stands off its centre (<see cref="LevelOp.Concentric"/>), so
    /// lies wherever the game turns it: the ground it leaves is then known only once it is placed.</summary>
    public static bool TurnsItsGround(IReadOnlyList<LevelOp>? ops) => ops != null && ops.Any(op => !op.Concentric);

    /// <summary>How much further out than the road's trim ring the search's end is put, so the trim keeps it.</summary>
    public const float AimOut = 0.5f;
    /// <summary>The angle between a location's doors round its ring (RoadNetworkGenerator.DoorsOf), from its landing's
    /// bearing round to its back: a landing facing the sea is come to along the shore (at -6016,4736).</summary>
    public const float AimStep = 22.5f;
    /// <summary>What a metre of height between a door's ground and the landing's costs, in metres round the
    /// ring: the way on from the ring point climbs it as a metre of staircase or up to three of road (the grade
    /// cap), where round the ring the road keeps its level.</summary>
    public const float AimRise = 2f;

    public static void Load(IEnumerable<PlacedLocationRecord> records) { foreach (var r in records) Remember(r); }

    private const int Format = 1;

    /// <summary>
    /// The locations placed before their roads, saved with the network: format 1, then their records as <see cref="Write"/>
    /// writes them. A load remembers them again (<see cref="Restore"/>) and the generator makes them ready
    /// (<see cref="Prepare"/>), so a staircase whose first zone is built after a restart is dressed on the ground it would
    /// have been dressed on in the session that generated it, and the ground in their footprints is known again.
    /// </summary>
    public static byte[] Serialize()
    {
        using var stream = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(stream);
        writer.Write(Format);
        writer.Write(Write(All));
        return stream.ToArray();
    }

    /// <summary>Replace the remembered locations with saved ones (<see cref="Serialize"/>); none saved (a network saved before
    /// they were) leaves none. Not yet ready for the roads: <see cref="Prepare"/> makes them so. A damaged record throws and
    /// changes nothing.</summary>
    public static void Restore(byte[]? data)
    {
        var records = new List<PlacedLocationRecord>();
        if (data != null && data.Length > 0)
        {
            using var reader = new System.IO.BinaryReader(new System.IO.MemoryStream(data));
            int format = reader.ReadInt32();
            if (format != Format) throw new System.IO.InvalidDataException($"placed locations format {format}");
            records = Read(reader.ReadString());
        }
        Reset();
        Load(records);
    }

    private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static float P(string s) => s.Length == 0 ? float.NaN : float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>
    /// One block per location: a "site" row, then its "op" rows, then a "rock" row per rock, each
    /// followed by a "top" row of its real top over its grid (empty cells: nothing of it stands
    /// there). Floats round-trip exactly.
    /// </summary>
    public static string Write(IEnumerable<PlacedLocationRecord> records)
    {
        var sb = new StringBuilder("# placed sites: site,prefab,cx,cz,yaw,root,rockRadius,keepSmallRocks,x0,z0,w,h | op,offsetX,offsetZ,height,radius,square,smoothRadius,smoothPower,sortOrder | rock,name,x,z,halfX,halfZ,yaw,minY,maxY,topX0,topZ0,topW[,built] | top,v;v;...\n");
        foreach (PlacedLocationRecord r in records)
        {
            sb.Append("site,").Append(r.Prefab.Replace(',', '_')).Append(',').Append(F(r.Centre.x)).Append(',').Append(F(r.Centre.y)).Append(',')
              .Append(F(r.Yaw)).Append(',').Append(F(r.Root)).Append(',').Append(F(r.RockRadius)).Append(',').Append(r.KeepSmallRocks ? 1 : 0).Append(',')
              .Append(r.X0).Append(',').Append(r.Z0).Append(',').Append(r.W).Append(',').Append(r.H).Append('\n');
            foreach (LevelOp op in r.Ops)
                sb.Append("op,").Append(F(op.OffsetX)).Append(',').Append(F(op.OffsetZ)).Append(',').Append(F(op.Height)).Append(',').Append(F(op.Radius)).Append(',')
                  .Append(op.Square ? 1 : 0).Append(',').Append(F(op.SmoothRadius)).Append(',').Append(F(op.SmoothPower)).Append(',').Append(op.SortOrder).Append('\n');
            foreach (LocationRock k in r.Rocks)
            {
                sb.Append("rock,").Append(k.Name.Replace(',', '_')).Append(',').Append(F(k.At.x)).Append(',').Append(F(k.At.y)).Append(',').Append(F(k.HalfX)).Append(',')
                  .Append(F(k.HalfZ)).Append(',').Append(F(k.Yaw)).Append(',').Append(F(k.MinY)).Append(',').Append(F(k.MaxY)).Append(',')
                  .Append(k.TopX0).Append(',').Append(k.TopZ0).Append(',').Append(k.TopW).Append(k.Built ? ",built" : "").Append('\n');
                sb.Append("top,");
                if (k.Top != null)
                    for (int i = 0; i < k.Top.Length; i++) sb.Append(i == 0 ? "" : ";").Append(float.IsNaN(k.Top[i]) ? "" : F(k.Top[i]));
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    public static List<PlacedLocationRecord> Read(string text)
    {
        var records = new List<PlacedLocationRecord>();
        PlacedLocationRecord? current = null;
        string[]? rock = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;
            string[] c = line.Split(',');
            switch (c[0])
            {
                case "site":
                    current = new PlacedLocationRecord
                    {
                        Prefab = c[1], Centre = new Vector2(P(c[2]), P(c[3])), Yaw = P(c[4]), Root = P(c[5]), RockRadius = P(c[6]),
                        KeepSmallRocks = c[7] == "1", X0 = int.Parse(c[8], CultureInfo.InvariantCulture), Z0 = int.Parse(c[9], CultureInfo.InvariantCulture),
                        W = int.Parse(c[10], CultureInfo.InvariantCulture), H = int.Parse(c[11], CultureInfo.InvariantCulture),
                    };
                    records.Add(current);
                    break;
                case "op":
                    current!.Ops.Add(new LevelOp(P(c[1]), P(c[2]), P(c[3]), P(c[4]), c[5] == "1", P(c[6]), P(c[7]), int.Parse(c[8], CultureInfo.InvariantCulture)));
                    break;
                case "rock":
                    rock = c;
                    break;
                case "top":
                    string[] k = rock ?? throw new FormatException("a top row with no rock row before it");
                    string values = line.Substring(4);
                    float[]? top = values.Length == 0 ? null : values.Split(';').Select(P).ToArray();
                    current!.Rocks.Add(new LocationRock(new Vector2(P(k[2]), P(k[3])), P(k[4]), P(k[5]), P(k[6]), P(k[7]), P(k[8]), k[1], top,
                        int.Parse(k[9], CultureInfo.InvariantCulture), int.Parse(k[10], CultureInfo.InvariantCulture), int.Parse(k[11], CultureInfo.InvariantCulture),
                        built: k.Length > 12 && k[12] == "built"));
                    rock = null;
                    break;
                default:
                    throw new FormatException($"unknown row kind '{c[0]}'");
            }
        }
        return records;
    }
}
