using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Trees and rocks on the roads of zones that were generated before the road.
///
/// The game keeps vegetation off a road only while it generates a zone: the
/// road's clear areas are handed to PlaceVegetation, and nothing is placed
/// inside them. A zone generated earlier -- before the mod was installed, or
/// before this network existed -- already has everything it grew, as
/// persistent objects, and nothing ever takes them away. So the server
/// removes, once per zone and network, exactly what generation would have left
/// out: objects of the game's own vegetation prefabs, inside the same clear
/// areas, tested the way the game tests them (ZoneSystem.InsideClearArea, an
/// axis-aligned square), and the grown trees whose trunks reach the road
/// (<see cref="TrunkReachesRoad"/>). Anything that is not a vegetation prefab -- player
/// builds, containers, bridge pieces -- is never touched, and objects within a
/// location's exterior radius are left to the location. The game's vegetation
/// list also holds the large rocks; on a road those the rock clearing takes
/// there belong to it (<see cref="LeftToRockClearing"/>), so the
/// Roads/RockClearing setting decides what happens to them, and a structure's
/// clear areas keep every rock out. Small stones (the pickable ones) are
/// vegetation and go with the trees.
///
/// Pure logic; ServerTerrainBake finds and removes the objects.
/// </summary>
public static class VegetationClearing
{
    /// <summary>A clear area as the game tests it: an axis-aligned square of half-size Radius.</summary>
    public readonly struct Area
    {
        public readonly Vector2 Centre;
        public readonly float Radius;

        public Area(Vector2 centre, float radius)
        {
            Centre = centre;
            Radius = radius;
        }
    }

    /// <summary>A location's footprint: a circle of its exterior radius.</summary>
    public readonly struct Footprint
    {
        public readonly Vector2 Centre;
        public readonly float Radius;

        public Footprint(Vector2 centre, float radius)
        {
            Centre = centre;
            Radius = radius;
        }
    }

    /// <summary>
    /// How far around a player's own object its vegetation is left alone. A
    /// planted grove stands beside the thing it was planted for -- a hut, a
    /// fence, a workbench -- so ground a player has built on is treated as
    /// theirs. Leaving a natural tree on the road is a blemish; taking a
    /// planted one is somebody's work gone.
    /// </summary>
    public const float PlayerGroundRadius = 8f;

    /// <summary>A road point as the grown-tree rule reads it: its centre and half its width.</summary>
    public readonly struct RoadCentre
    {
        public readonly Vector2 Centre;
        public readonly float HalfWidth;

        public RoadCentre(Vector2 centre, float halfWidth)
        {
            Centre = centre;
            HalfWidth = halfWidth;
        }
    }

    // ---- grown trees ----
    //
    // The clear areas test an object's origin and keep the middle 90 % of the road clear
    // (RoadConstants.VegetationClearMultiplier): bushes, saplings, small trees and small stones may stand on the outer
    // edge. A grown tree is wider than that edge: one whose origin stands just past the clear area keeps a metre or more
    // of trunk over the road. So a grown tree goes when its trunk reaches the road: its origin is nearer a road point
    // than half the road's width, plus the trunk's reach at the tree's scale, plus TrunkMargin.
    //
    // A grown tree is a vegetation entry that grows to full height: every TreeBase prefab in the game's vegetation lists
    // (chopped, it falls as a log), and the swamp's indestructible SwampTree2. The small ones -- Beech_small*,
    // FirTree_small*, YggaShoot_small1, the bushes and stumps -- are Destructibles and stay creep.
    //
    // Trunk reach: the furthest a solid collider of the prefab (viewblock meshes left out) stands from its origin across
    // the ground, between 0.25 m and 2 m up -- the height a player walks into -- at scale 1, measured on the game's
    // prefabs. A placed tree's scale replaces the prefab's own (ZoneSystem.PlaceVegetation), so the reach is multiplied
    // by the tree's scale. It is the reach in the trunk's widest direction, roots and flare included, whichever way the
    // road lies. The lean the game gives a tree (up to its entry's m_randTilt) and the canopy are not counted: a leaning
    // trunk may still overhang the road above head height.
    private static readonly Dictionary<string, float> s_trunkReach = new()
    {
        ["Beech1"] = 0.72f,
        ["Birch1"] = 0.76f,
        ["Birch1_aut"] = 0.76f,
        ["Birch2"] = 0.84f,
        ["Birch2_aut"] = 0.84f,
        ["Oak1"] = 2.06f,
        ["FirTree"] = 0.4f,
        ["Pinetree_01"] = 0.33f,
        ["Pinetree_Snow"] = 0.33f,
        ["Pinetree_Snow_dead"] = 0.33f,
        ["SnowFirTree"] = 1f,
        ["SnowFirTree 2"] = 1f,
        ["SwampTree1"] = 2.41f,
        ["SwampTree2"] = 3.04f,
        ["SwampTree2_darkland"] = 3.04f,
        ["YggaShoot1"] = 1.41f,
        ["YggaShoot2"] = 1.41f,
        ["YggaShoot3"] = 1.41f,
        ["AshlandsTree1_0"] = 0.75f,
        ["AshlandsTree3"] = 0.5f,
        ["AshlandsTree6"] = 4.32f,
    };

    private static readonly Dictionary<int, float> s_trunkReachByHash =
        s_trunkReach.ToDictionary(tree => tree.Key.GetStableHashCode(), tree => tree.Value);

    /// <summary>How far past the road's edge a grown tree's trunk is kept.</summary>
    public const float TrunkMargin = 0.25f;

    /// <summary>The largest scale the game's vegetation lists give a grown tree (SwampTree2_darkland, 3 to 5).</summary>
    public const float LargestTreeScale = 5f;

    /// <summary>How far past a road point's half-width a grown tree can stand and still reach it: the widest trunk at
    /// the largest scale. A zone's trees are tested against the road points this far beyond it too.</summary>
    public static readonly float TreeReach = s_trunkReach.Values.Max() * LargestTreeScale + TrunkMargin;

    /// <summary>A grown tree, and its trunk reach at scale 1; false for everything else, which keeps to the clear areas.</summary>
    public static bool IsGrownTree(string prefab, out float trunkReach) => s_trunkReach.TryGetValue(prefab, out trunkReach);

    /// <inheritdoc cref="IsGrownTree(string, out float)"/>
    public static bool IsGrownTree(int prefab, out float trunkReach) => s_trunkReachByHash.TryGetValue(prefab, out trunkReach);

    /// <summary>
    /// Whether a grown tree's trunk, <paramref name="trunkReach"/> at scale 1, reaches the road: the tree stands nearer
    /// a road point than half its width plus the trunk's reach at <paramref name="scale"/> plus TrunkMargin.
    /// </summary>
    public static bool TrunkReachesRoad(IReadOnlyList<RoadCentre> road, Vector2 position, float trunkReach, float scale)
    {
        float reach = trunkReach * scale + TrunkMargin;
        foreach (RoadCentre point in road)
        {
            float within = point.HalfWidth + reach;
            if ((position - point.Centre).sqrMagnitude < within * within)
                return true;
        }
        return false;
    }

    /// <summary>
    /// What both clearings take off the road: an object inside a clear area, and a grown tree whose trunk reaches the
    /// road. Generation asks with the scale it drew for the tree, the server with the scale the tree was saved at.
    /// </summary>
    public static bool OnRoad(bool insideClearArea, int prefab, float scale, IReadOnlyList<RoadCentre> road, Vector2 position) =>
        insideClearArea || IsGrownTree(prefab, out float trunkReach) && TrunkReachesRoad(road, position, trunkReach, scale);

    /// <inheritdoc cref="OnRoad(bool, int, float, IReadOnlyList{RoadCentre}, Vector2)"/>
    public static bool OnRoad(bool insideClearArea, string prefab, float scale, IReadOnlyList<RoadCentre> road, Vector2 position) =>
        insideClearArea || IsGrownTree(prefab, out float trunkReach) && TrunkReachesRoad(road, position, trunkReach, scale);

    private static readonly Dictionary<int, string> s_rockClearingRocks =
        RoadRockPolicy.KnownRocks.ToDictionary(name => name.GetStableHashCode(), name => name);

    /// <summary>
    /// A large rock the rock clearing judges (<see cref="RoadRockPolicy.KnownRocks"/>: boulders, Mistlands rocks and
    /// cliffs, Destructibles a pickaxe breaks into a MineRock5). Small stones are Pickables picked up by hand
    /// (Pickable_Stone, Pickable_Flint) and stay vegetation.
    /// </summary>
    public static bool IsRock(int prefab) => s_rockClearingRocks.ContainsKey(prefab);

    /// <summary>
    /// A large rock the rock clearing, set to <paramref name="mode"/>, takes off a road in <paramref name="biome"/>
    /// (<see cref="RoadRockPolicy.ClearingTakes"/>): never removed with the vegetation from a road's clear areas. A rock
    /// it would leave goes with the vegetation, as generation keeps it off the road.
    /// </summary>
    public static bool LeftToRockClearing(int prefab, Heightmap.Biome biome, RockClearingMode mode) =>
        s_rockClearingRocks.TryGetValue(prefab, out string? name) && RoadRockPolicy.ClearingTakes(name, biome, mode);

    /// <summary>ZoneSystem.InsideClearArea, the test PlaceVegetation uses: strict, and square.</summary>
    public static bool InsideClearArea(IReadOnlyList<Area> areas, Vector2 point)
    {
        foreach (Area area in areas)
        {
            if (point.x > area.Centre.x - area.Radius && point.x < area.Centre.x + area.Radius &&
                point.y > area.Centre.y - area.Radius && point.y < area.Centre.y + area.Radius)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether an existing object is one generation would not have placed on
    /// the road.
    ///
    /// What this CANNOT tell, and why the two guards below exist: a tree a
    /// player planted and grew is the same prefab as one the game grew, at an
    /// ordinary position, and vanilla writes nothing to tell them apart --
    /// ZoneSystem.PlaceVegetation spawns its vegetation under a ghost init and
    /// sets only the scale, and Plant.Grow instantiates the grown prefab
    /// without carrying the sapling's creator or plantTime onto it. So on a
    /// world that already exists there is no saved fact that says "a player
    /// grew this". What can be honoured is what IS marked: an object carrying
    /// a creator is somebody's, and vegetation standing on ground a player has
    /// built on is treated as theirs (PlayerGroundRadius). A grown tree alone
    /// on the road in a zone that predates the network is still taken.
    /// </summary>
    /// <param name="road">the road points near the object, for a grown tree (<see cref="TrunkReachesRoad"/>)</param>
    /// <param name="scale">the object's scale</param>
    /// <param name="structureAreas">the structures' clear areas (bridges, staircases), which keep every rock out as well</param>
    /// <param name="rockClearing">the Roads/RockClearing setting; Off leaves no rock to the rock clearing</param>
    /// <param name="biomeAt">the biome at a position, asked for a large rock only (<see cref="LeftToRockClearing"/>)</param>
    public static bool ShouldRemove(int prefab, Vector2 position, bool playerCreated,
        ICollection<int> vegetationPrefabs, IReadOnlyList<Area> clearAreas,
        IReadOnlyList<Footprint> locations, IReadOnlyList<Footprint> playerGround,
        IReadOnlyList<RoadCentre>? road = null, float scale = 1f, IReadOnlyList<Area>? structureAreas = null,
        RockClearingMode rockClearing = RockClearingMode.Off, Func<Vector2, Heightmap.Biome>? biomeAt = null)
    {
        // Somebody's own object, whatever it is: never ours to take.
        if (playerCreated || !vegetationPrefabs.Contains(prefab))
            return false;
        bool inside = structureAreas != null && InsideClearArea(structureAreas, position) ||
            InsideClearArea(clearAreas, position) &&
            !(IsRock(prefab) && LeftToRockClearing(prefab, biomeAt?.Invoke(position) ?? Heightmap.Biome.None, rockClearing));
        if (!OnRoad(inside, prefab, scale, road ?? Array.Empty<RoadCentre>(), position))
            return false;
        // Left to the location, and left to the player who built there.
        return !Inside(locations, position) && !Inside(playerGround, position);
    }

    private static bool Inside(IReadOnlyList<Footprint> footprints, Vector2 position)
    {
        foreach (Footprint footprint in footprints)
        {
            if ((position - footprint.Centre).sqrMagnitude < footprint.Radius * footprint.Radius)
                return true;
        }
        return false;
    }

    // ---- which zones' vegetation already matches the current network ----
    //
    // A zone generated with the road's clear areas matches from the start; a
    // zone generated before it matches once it has been cleared. Clearing
    // happens once per zone and network, so a tree a player plants on the road
    // later is theirs to keep. The set is saved with the network
    // (RoadNetworkPersistence) and belongs to one network version.

    private static readonly HashSet<Vector2s> s_cleared = new();

    /// <summary>The network version the cleared zones belong to (0 = none).</summary>
    public static int Version { get; private set; }

    public static IReadOnlyCollection<Vector2s> ClearedZones => s_cleared;

    /// <summary>
    /// Zones generated by the time the network was loaded, when the saved record of cleared zones could not be read:
    /// whether each was cleared already, and planted on since, is not known. None is cleared again on that guess until
    /// a build that reads the record says, or a new network starts the record afresh (<see cref="Reset"/>).
    /// </summary>
    private static readonly HashSet<Vector2s> s_unknown = new();

    /// <summary>Whether the zone's vegetation is taken to match the network: cleared for it, or with its clearing unknown.</summary>
    public static bool IsCleared(Vector2s zone, int version) =>
        s_unknown.Contains(zone) ||
        version != 0 && Version != 0 && s_cleared.Contains(zone) && (version == Version || StillCurrent(zone, version));

    private static bool StillCurrent(Vector2s zone, int version) =>
        version == RoadSpatialGrid.RoadNetworkVersion && RoadTerrainModifier.CarriesCurrentRoads(zone, Version);

    public static void MarkCleared(Vector2s zone, int version)
    {
        if (version == 0)
            return;
        CarryOver(version);
        s_cleared.Add(zone);
    }

    /// <summary>
    /// Move the record to <paramref name="version"/>. A zone a manual road left unchanged
    /// (RoadTerrainModifier.CarriesCurrentRoads, the terrain's own test) stays cleared; any other network starts afresh.
    /// </summary>
    public static void CarryOver(int version)
    {
        if (version == Version) return;
        if (Version != 0) s_cleared.RemoveWhere(zone => !StillCurrent(zone, version));
        Version = version;
    }

    /// <summary>Take the saved record, unless it belongs to another network.</summary>
    public static void Load(int savedVersion, IEnumerable<Vector2s> zones, int currentVersion)
    {
        Reset();
        if (savedVersion == 0 || savedVersion != currentVersion)
            return;
        Version = savedVersion;
        foreach (Vector2s zone in zones)
            s_cleared.Add(zone);
    }

    /// <summary>The saved record could not be read: every zone generated by now has an unknown clearing (<see cref="s_unknown"/>).</summary>
    public static void HistoryUnread()
    {
        Reset();
        if (ZoneSystem.instance != null) s_unknown.UnionWith(ZoneSystem.instance.m_generatedZones);
    }

    public static void Reset()
    {
        s_unknown.Clear();
        s_cleared.Clear();
        Version = 0;
    }
}
