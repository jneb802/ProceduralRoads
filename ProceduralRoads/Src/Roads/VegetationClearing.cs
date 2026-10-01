using System.Collections.Generic;
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
/// axis-aligned square). Anything that is not a vegetation prefab -- player
/// builds, containers, bridge pieces -- is never touched, and objects within a
/// location's exterior radius are left to the location.
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
    public static bool ShouldRemove(int prefab, Vector2 position, bool playerCreated,
        ICollection<int> vegetationPrefabs, IReadOnlyList<Area> clearAreas,
        IReadOnlyList<Footprint> locations, IReadOnlyList<Footprint> playerGround)
    {
        // Somebody's own object, whatever it is: never ours to take.
        if (playerCreated)
            return false;
        if (!vegetationPrefabs.Contains(prefab) || !InsideClearArea(clearAreas, position))
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

    public static bool IsCleared(Vector2s zone, int version) =>
        version != 0 && version == Version && s_cleared.Contains(zone);

    public static void MarkCleared(Vector2s zone, int version)
    {
        if (version == 0)
            return;
        if (version != Version)
        {
            s_cleared.Clear();
            Version = version;
        }
        s_cleared.Add(zone);
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

    public static void Reset()
    {
        s_cleared.Clear();
        Version = 0;
    }
}
