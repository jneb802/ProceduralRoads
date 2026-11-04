using System.Collections.Generic;
using System.Linq;

namespace ProceduralRoads;

/// <summary>What happens to natural rocks that clip a road's surface.</summary>
public enum RockClearingMode
{
    /// <summary>Rocks are left where the world put them.</summary>
    Off,
    /// <summary>A rock that blocks at least half the road's width is removed whole.</summary>
    Remove,
    /// <summary>Only the pieces of a rock that stand in the road are taken, as a
    /// pickaxe would; a rock with no pieces is removed whole.</summary>
    Carve,
}

/// <summary>Known natural boulders only, never ore, fragments or arbitrary destructibles.</summary>
public static class RoadRockPolicy
{
    /// <summary>The player's collision capsule, read from the Player prefab:
    /// 1.85 m tall, 0.49 m radius, standing on its feet.</summary>
    public const float PlayerHeight = 1.85f;
    public const float PlayerWidth = 0.98f;
    /// <summary>The narrowest way a carve leaves through a rock: the player's
    /// width and about a quarter metre either side, so walking it never catches
    /// (the smallest possible hole in the rock the player can
    /// reliably fit through).</summary>
    public const float MinOpening = 1.5f;

    /// <summary>Share of a clearance <paramref name="width"/> metres wide that a
    /// carved rock may keep, from either side or both, so that
    /// <see cref="MinOpening"/> stays open. A clearance no wider than that is
    /// opened in full.</summary>
    public static float EdgeAllowance(float width) => width <= MinOpening ? 0f : 1f - MinOpening / width;

    public const Heightmap.Biome SupportedBiomes = Heightmap.Biome.Mountain |
        Heightmap.Biome.Plains | Heightmap.Biome.BlackForest | Heightmap.Biome.Meadows | Heightmap.Biome.Swamp;

    /// <summary>Whether this setting clears rocks at all.</summary>
    public static bool Clears(RockClearingMode mode) => mode != RockClearingMode.Off;

    /// <summary>
    /// Whether a rock standing here may be carved or removed at all: never in
    /// a zone explored before the road network reached the world
    /// (NetworkCutoff), where the rocks are the players' landscape.
    /// </summary>
    public static bool MayTouch(UnityEngine.Vector3 position) => !NetworkCutoff.Excludes(ZoneSystem.GetZone(position));

    /// <summary>Whether this setting carves rocks rather than removing them whole.</summary>
    public static bool Carves(RockClearingMode mode) => mode == RockClearingMode.Carve;

    /// <summary>Keep a rock that blocks under half the road's width at its
    /// worst point, when rocks are removed whole.</summary>
    public static bool KeepPartial(int blockedSlots, int slots) => blockedSlots * 2 < slots;
    /// <summary>A rock smaller than this (bounds diagonal, m) is never carved: one in the road's
    /// clearance is removed whole: a small rock is kept or cleared, never cut (keeping it was
    /// measured to leave the centre of the lane blocked at four of 28 sites).</summary>
    public const float CarveMinSize = 12f;

    // Shared rocks also occur outside these biomes. The instance biome and
    // enabled vegetation registration are checked separately before removal.
    private static readonly HashSet<string> NaturalBoulders = new() { "rock1_mountain", "rock2_mountain", "rock3_mountain",
        "rock3_mountain_1", "rock2_heath", "rock4_heath", "rock4_forest", "rock4_coast", "HeathRockPillar", "Rock_3", "Rock_4",
        "Rock_4_plains" };
    private static readonly HashSet<string> MistlandsBoulders = new() { "rock_mistlands1", "rock_mistlands2" };
    private static readonly HashSet<string> MistlandsCliffs = new() { "cliff_mistlands1", "cliff_mistlands2" };

    public static bool IsNaturalBoulder(string name) => NaturalBoulders.Contains(name);

    /// <summary>Every rock prefab clearing may touch, for road_rock_kinds: the lists above, written once.</summary>
    public static readonly string[] KnownRocks = NaturalBoulders.Concat(MistlandsBoulders).Concat(MistlandsCliffs).ToArray();

    /// <summary>
    /// The names the clearing rules are asked about for an object. A rock
    /// that has been carved (or mined) is its fractured copy, "rock1_mountain_frac",
    /// and is judged as the rocks that fracture into it: one fractured prefab
    /// can serve several (rock_mistlands2 and rock4_forest share
    /// rock4_forest_frac). Anything else is judged by its own name.
    /// </summary>
    public static IReadOnlyList<string> PolicyNames(string name, IReadOnlyDictionary<string, List<string>> fracturedFrom) =>
        fracturedFrom.TryGetValue(name, out var sources) && sources.Count > 0 ? sources : new[] { name };

    /// <summary>
    /// Which chunks to take at one road point so the road stays passable: the
    /// clearance is cut into strips across the road; a rock may keep chunks in
    /// the edge strips as long as one open run covers at least
    /// (1 - <paramref name="edgeAllowance"/>) of the strips: a rock may keep all
    /// of the road but a way through a player fits (<see cref="EdgeAllowance(float)"/>).
    /// Chunks go centre-first, one at a
    /// time, until the run is long enough or nothing removable is left (a strip
    /// held by something else -- a tree, a rock that is not ours -- never opens).
    /// </summary>
    /// <param name="blockers">per strip, the removable chunks in it</param>
    /// <param name="heldByOther">per strip, whether something not removable is in it</param>
    /// <param name="removed">chunks already taken; the ones chosen here are added</param>
    /// <returns>the chunks chosen here, in the order taken</returns>
    public static List<int> ChunksToOpen(IReadOnlyList<IReadOnlyCollection<int>> blockers, IReadOnlyList<bool> heldByOther,
        float edgeAllowance, ISet<int> removed)
    {
        var taken = new List<int>();
        int strips = blockers.Count;
        if (strips == 0) return taken;
        int need = System.Math.Max(1, (int)System.Math.Ceiling(strips * (1f - edgeAllowance) - 1e-4f));
        // Strips in order of distance from the centre line.
        var order = new List<int>();
        for (int s = 0; s < strips; s++) order.Add(s);
        float mid = (strips - 1) * 0.5f;
        order.Sort((a, b) => System.Math.Abs(a - mid).CompareTo(System.Math.Abs(b - mid)));
        while (true)
        {
            int run = 0, best = 0;
            for (int s = 0; s < strips; s++)
            {
                bool open = !heldByOther[s];
                if (open) foreach (int c in blockers[s]) if (!removed.Contains(c)) { open = false; break; }
                run = open ? run + 1 : 0;
                if (run > best) best = run;
            }
            if (best >= need) return taken;
            int pick = -1;
            foreach (int s in order)
            {
                foreach (int c in blockers[s]) if (!removed.Contains(c)) { pick = c; break; }
                if (pick >= 0) break;
            }
            if (pick < 0) return taken;
            removed.Add(pick);
            taken.Add(pick);
        }
    }

    /// <summary>The road's direction at a point: toward the nearest other
    /// stored point, ignoring duplicates (a point is stored once per grid cell
    /// it touches) and anything beyond 3 m.</summary>
    public static UnityEngine.Vector2 Heading(RoadSpatialGrid.RoadPoint point, List<RoadSpatialGrid.RoadPoint> all)
    {
        var dir = new UnityEngine.Vector2(1f, 0f); float best = float.MaxValue;
        foreach (var q in all)
        {
            float d = (q.p - point.p).sqrMagnitude;
            if (d > 0.04f && d < best && d < 9f) { best = d; dir = (q.p - point.p).normalized; }
        }
        return dir;
    }

    /// <summary>
    /// The surface a road point's clearance stands on, and whether the point is
    /// probed at all. A pass over loaded zones skips a point with no ground
    /// under it (the terrain collider is not built yet) and looks again later.
    /// A zone the server generates for a remote peer has only its own terrain,
    /// and nothing looks at its objects again: a rock of that zone reaching over
    /// the road in the zone next door is judged then or never, so the clearance
    /// stands on the road's own stored height, the surface it levels to (as the
    /// carve already does in RoadRockCarve.PointsNear).
    /// </summary>
    public static bool ClearanceSurface(bool haveGround, float ground, float roadHeight, bool generating, out float surface)
    {
        surface = haveGround ? ground : roadHeight;
        return haveGround || generating;
    }

    /// <summary>
    /// How far round a zone's centre a pass over that zone's own objects must
    /// look for road points: <paramref name="zoneReach"/> (its corners), or
    /// further when one of its rocks reaches out of it. A clearance can meet a
    /// rock only at a road point within <paramref name="roadReach"/> of the
    /// rock's horizontal bounds, and the farthest point of the bounds from the
    /// centre is a corner. Bounds are (min, max) in x and z.
    /// </summary>
    public static float OwnObjectsReach(UnityEngine.Vector2 centre,
        IEnumerable<(UnityEngine.Vector2 min, UnityEngine.Vector2 max)> bounds, float zoneReach, float roadReach)
    {
        float reach = zoneReach;
        foreach (var (min, max) in bounds)
        {
            float dx = System.Math.Max(System.Math.Abs(min.x - centre.x), System.Math.Abs(max.x - centre.x));
            float dz = System.Math.Max(System.Math.Abs(min.y - centre.y), System.Math.Abs(max.y - centre.y));
            reach = System.Math.Max(reach, (float)System.Math.Sqrt(dx * dx + dz * dz) + roadReach);
        }
        return reach;
    }

    /// <summary>Whether any clearing rule could take this prefab (every rock in <see cref="KnownRocks"/>).</summary>
    public static bool IsClearableRock(string name) => IsNaturalBoulder(name) || IsMistlandsRock(name, true, true);

    public static bool CanClear(string name, bool registeredVegetation, Heightmap.Biome biome,
        bool protectedSite, bool playerPiece, bool hasNetworkView, bool validView, bool owned) =>
        IsNaturalBoulder(name) && registeredVegetation && (biome & SupportedBiomes) != 0 &&
        !protectedSite && !playerPiece && (!hasNetworkView || (validView && owned));

    // Mistlands rocks and cliffs are recognised separately: the cliffs are
    // outcrops tens of metres across, not boulders.
    public static bool IsMistlandsBoulder(string name) => MistlandsBoulders.Contains(name);
    public static bool IsMistlandsCliff(string name) => MistlandsCliffs.Contains(name);
    public static bool IsMistlandsRock(string name, bool rocks, bool cliffs) =>
        (rocks && IsMistlandsBoulder(name)) || (cliffs && IsMistlandsCliff(name));

    public static bool CanClearMistlands(string name, bool rocks, bool cliffs, bool registeredVegetation, Heightmap.Biome biome,
        bool protectedSite, bool playerPiece, bool hasNetworkView, bool validView, bool owned) =>
        IsMistlandsRock(name, rocks, cliffs) && registeredVegetation && (biome & Heightmap.Biome.Mistlands) != 0 &&
        !protectedSite && !playerPiece && (!hasNetworkView || (validView && owned));
}
