using System;
using UnityEngine;

namespace ProceduralRoads.Tests;

/// <summary>
/// A test's ground as production lays an approach out on it: the planning lattice (RoadStairRuns.LayoutGround.Lattice, the
/// ground at the whole-metre vertices joined as the game's mesh joins them, a placed location's preload in its footprint) of a
/// world whose every biome's height is that ground. RoadNetworkGenerator's entry staircases pass the lattice; a test passing
/// the continuous ground itself (LayoutGround.Own) laid its approach on ground production never reads.
/// </summary>
internal static class TestLattice
{
    private sealed class World : WorldGenerator
    {
        private readonly Func<float, float, float> _ground;
        private readonly Heightmap.Biome _biome;
        public World(Func<float, float, float> ground, Heightmap.Biome biome) { _ground = ground; _biome = biome; }
        public override float GetHeight(float wx, float wy) => _ground(wx, wy);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => _biome;
        public override float GetBiomeHeight(Heightmap.Biome biome, float wx, float wy, out Color mask, bool preGeneration = false, bool riverPreDN = true)
        {
            mask = default;
            return _ground(wx, wy);
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(System.Reflection.MethodInfo, object?, Heightmap.Biome), World> s_worlds = new();

    /// <summary>The planning lattice of <paramref name="ground"/> (a mountain's, as the approach tests' locations are): one world
    /// per ground function, as production passes its one world to every layout (a layout's key names its world).</summary>
    internal static RoadStairRuns.LayoutGround Of(Func<float, float, float> ground, Heightmap.Biome biome = Heightmap.Biome.Mountain) =>
        RoadStairRuns.LayoutGround.Lattice(s_worlds.GetOrAdd((ground.Method, ground.Target, biome), _ => new World(ground, biome)));
}
