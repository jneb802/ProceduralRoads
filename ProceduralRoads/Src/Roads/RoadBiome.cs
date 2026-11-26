using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Which biome a point, a search cell, a location, a crossing, a line or a structure is in: the one owner every rule asks
/// (docs/OWNERS.md; the owner's decisions of 13 Nov 2026). A rule names its scope here and reads its values for that biome
/// from <see cref="RoadRules.For"/>; no other code asks the world for a biome. The scopes are different questions and stay
/// named apart:
/// <list type="bullet">
/// <item><see cref="At(WorldGenerator, Vector2)"/>: the game's biome at a point (WorldGenerator.GetBiome), the one source.</item>
/// <item><see cref="OfLocation"/>: a location's, at its centre.</item>
/// <item><see cref="OfCrossing"/>: a crossing's, at the middle of its two shores.</item>
/// <item><see cref="AnyIn"/>: a line or stretch is in a biome where any of its points is.</item>
/// <item><see cref="Leans"/>: a structure is where at least <see cref="Share"/> of its places are.</item>
/// <item><see cref="GroundAt"/>: the biomes a point's ground is built from, and how much of each, as the game blends its
/// heights (a value that must change with the ground across a border, such as the earthwork faces' detail).</item>
/// </list>
/// The search reads its cells' biomes through its own cache (RoadTerrainSamples.Biome), each one <see cref="At(WorldGenerator, Vector2)"/>
/// at the cell's centre.
/// </summary>
public static class RoadBiome
{
    // The world's biome readers are banned at compile time outside this owner and the blend (BannedSymbols.txt): this is the one read.
#pragma warning disable RS0030
    /// <summary>The game's biome at a point: the one source every scope reads.</summary>
    public static Heightmap.Biome At(WorldGenerator world, float x, float z) => world.GetBiome(x, z);
#pragma warning restore RS0030

    /// <summary><see cref="At(WorldGenerator, float, float)"/> at <paramref name="p"/>.</summary>
    public static Heightmap.Biome At(WorldGenerator world, Vector2 p) => At(world, p.x, p.y);

    /// <summary><see cref="At(WorldGenerator, float, float)"/> at a position in the world (its x and z), on the game's world (as the
    /// game's own WorldGenerator.GetBiome(Vector3) reads it).</summary>
    public static Heightmap.Biome At(Vector3 position) => At(WorldGenerator.instance, position.x, position.z);

    /// <summary>The biomes the ground at <paramref name="p"/> is built from and their weights: the four corners of its heightmap,
    /// blended as the game's HeightmapBuilder blends their heights (<see cref="BiomeBlendedHeight.Ground"/>, the same weights
    /// the blended height uses). <paramref name="corners"/> memoises the corner biomes (null: the generation's open corner
    /// grid, else the world).</summary>
    internal static BiomeBlendedHeight.Ground GroundAt(WorldGenerator world, Vector2 p, BiomeBlendedHeight.Corners? corners) =>
        BiomeBlendedHeight.GroundAt(p.x, p.y, world, corners);

    /// <summary>A location's biome: the biome at its centre.</summary>
    public static Heightmap.Biome OfLocation(WorldGenerator world, Vector2 centre) => At(world, centre.x, centre.y);

    /// <summary>A crossing's biome: the biome at the middle of its two shores (<paramref name="from"/> and <paramref name="to"/>).</summary>
    public static Heightmap.Biome OfCrossing(Vector2 from, Vector2 to, WorldGenerator world)
    {
        Vector2 center = (from + to) * 0.5f;
        return At(world, center.x, center.y);
    }

    /// <summary>Whether any of <paramref name="points"/> is in a biome <paramref name="pick"/> chooses: a line or stretch is in a
    /// biome where any point of it is.</summary>
    public static bool AnyIn(IEnumerable<Vector2> points, Func<Vector2, Heightmap.Biome> biomeAt, Func<Heightmap.Biome, bool> pick)
    {
        foreach (Vector2 p in points)
            if (pick(biomeAt(p))) return true;
        return false;
    }

    /// <summary>Share of a structure's places in a biome from which the structure is that biome's (<see cref="Leans"/>).</summary>
    public const float Share = 0.4f;

    /// <summary><paramref name="line"/> sampled every 2 m: the places a structure's line is judged at (<see cref="Leans"/>).</summary>
    public static List<Vector2> Samples(IReadOnlyList<Vector2> line)
    {
        var samples = new List<Vector2>();
        for (int i = 0; i < line.Count; i++)
        {
            Vector2 a = line[i], b = i + 1 < line.Count ? line[i + 1] : line[i];
            int n = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / 2f));
            for (int k = 0; k < n; k++) samples.Add(Vector2.Lerp(a, b, k / (float)n));
            if (i + 1 >= line.Count) break;
        }
        return samples;
    }

    /// <summary>The one rule for a structure across a biome edge: it is where at least <see cref="Share"/> of
    /// <paramref name="places"/> stand where <paramref name="advanced"/> holds, so a structure on a boundary leans to the more
    /// advanced kit (black marble over stone, stone over wood).</summary>
    public static bool Leans(IReadOnlyList<Vector2> places, Func<float, float, Heightmap.Biome> biomeAt, Func<Heightmap.Biome, bool> advanced)
    {
        int count = 0;
        foreach (Vector2 p in places)
            if (advanced(biomeAt(p.x, p.y))) count++;
        return places.Count > 0 && count >= Share * places.Count;
    }
}
