using System.Collections.Generic;

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
    public const Heightmap.Biome SupportedBiomes = Heightmap.Biome.Mountain |
        Heightmap.Biome.Plains | Heightmap.Biome.BlackForest | Heightmap.Biome.Meadows | Heightmap.Biome.Swamp;

    /// <summary>Whether this setting clears rocks at all.</summary>
    public static bool Clears(RockClearingMode mode) => mode != RockClearingMode.Off;

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
    public static bool IsNaturalBoulder(string name) => name == "rock1_mountain" ||
        name == "rock2_mountain" || name == "rock3_mountain" || name == "rock3_mountain_1" ||
        name == "rock2_heath" || name == "rock4_heath" || name == "rock4_forest" ||
        name == "rock4_coast" || name == "HeathRockPillar" ||
        name == "Rock_3" || name == "Rock_4" || name == "Rock_4_plains";

    /// <summary>Every rock prefab clearing may touch, for road_rock_kinds.</summary>
    public static readonly string[] KnownRocks = { "rock1_mountain", "rock2_mountain", "rock3_mountain", "rock3_mountain_1",
        "rock2_heath", "rock4_heath", "rock4_forest", "rock4_coast", "HeathRockPillar", "Rock_3", "Rock_4", "Rock_4_plains",
        "rock_mistlands1", "rock_mistlands2", "cliff_mistlands1", "cliff_mistlands2" };

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
    /// (1 - <paramref name="edgeAllowance"/>) of the strips: a rock may overlap
    /// the road by 30% as long as both sides are not blocked so the road cannot
    /// be passed. Chunks go centre-first, one at a
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

    public static bool CanClear(string name, bool registeredVegetation, Heightmap.Biome biome,
        bool protectedSite, bool playerPiece, bool hasNetworkView, bool validView, bool owned) =>
        IsNaturalBoulder(name) && registeredVegetation && (biome & SupportedBiomes) != 0 &&
        !protectedSite && !playerPiece && (!hasNetworkView || (validView && owned));

    // Mistlands rocks and cliffs are recognised separately: the cliffs are
    // outcrops tens of metres across, not boulders.
    public static bool IsMistlandsBoulder(string name) => name == "rock_mistlands1" || name == "rock_mistlands2";
    public static bool IsMistlandsCliff(string name) => name == "cliff_mistlands1" || name == "cliff_mistlands2";
    public static bool IsMistlandsRock(string name, bool rocks, bool cliffs) =>
        (rocks && IsMistlandsBoulder(name)) || (cliffs && IsMistlandsCliff(name));

    public static bool CanClearMistlands(string name, bool rocks, bool cliffs, bool registeredVegetation, Heightmap.Biome biome,
        bool protectedSite, bool playerPiece, bool hasNetworkView, bool validView, bool owned) =>
        IsMistlandsRock(name, rocks, cliffs) && registeredVegetation && (biome & Heightmap.Biome.Mistlands) != 0 &&
        !protectedSite && !playerPiece && (!hasNetworkView || (validView && owned));
}
