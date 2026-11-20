using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A river crossing's terrain facts, kept per pathfinder, cell and direction (RoadPathfinder.TryGetRiverCrossing): asked
/// again, a crossing gives the landing and the price a fresh pathfinder gives, to the bit, and reads nothing of the world.
/// </summary>
public sealed class RiverCrossingFactsTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public RiverCrossingFactsTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
    }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    /// <summary>Another world, its reads counted.</summary>
    private sealed class Counting : WorldGenerator
    {
        private readonly WorldGenerator m_world;
        public int Reads;
        public Counting(WorldGenerator world) => m_world = world;
        public override float GetHeight(float wx, float wy) { Reads++; return m_world.GetHeight(wx, wy); }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false)
        { Reads++; return m_world.GetBiome(wx, wy, oceanLevel, waterAlwaysOcean); }
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        { Reads++; m_world.GetRiverWeight(wx, wy, out weight, out width); }
        public override float GetBiomeHeight(Heightmap.Biome biome, float wx, float wy, out Color mask, bool preGeneration = false, bool riverPreDN = true)
        { Reads++; return m_world.GetBiomeHeight(biome, wx, wy, out mask, preGeneration, riverPreDN); }
        public override float GetBaseHeight(float wx, float wy, bool menuTerrain) { Reads++; return m_world.GetBaseHeight(wx, wy, menuTerrain); }
        public override int GetSeed() => m_world.GetSeed();
    }

    private static int Bits(float f) => BitConverter.ToInt32(BitConverter.GetBytes(f), 0);

    private static readonly MethodInfo Crossing = typeof(RoadPathfinder).GetMethod("TryGetRiverCrossing", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static (bool found, Vector2i landing, int cost) Ask(RoadPathfinder finder, Vector2i cell, Vector2? leaving, Vector2Int direction)
    {
        var args = new object?[] { cell, leaving, direction, null, null };
        bool found = (bool)Crossing.Invoke(finder, args)!;
        return (found, (Vector2i)args[3]!, Bits((float)args[4]!));
    }

    private static IEnumerable<Vector2Int> Principal()
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
                if (dx != 0 || dz != 0) yield return new Vector2Int(dx, dz);
    }

    private static RoadPathfinder Finder(WorldGenerator world, bool fords, bool bridges) =>
        new(world) { Fords = fords, Bridges = bridges };

    public static IEnumerable<object[]> Worlds()
    {
        yield return new object[] { "wide" };
        yield return new object[] { "synthetic" };
    }

    private static WorldGenerator World(string name) =>
        name == "wide" ? new BridgeTests.WideRiverWorld { EastRise = 1f } : new SyntheticWorld { HasRiver = true, HasMountain = false };

    /// <summary>Every cell near the river, every principal direction, with bridges and fords, bridges only and fords only:
    /// a pathfinder asked a second time gives what a fresh one gives the first time, and the exact start (left from a point
    /// that is not the cell's centre) is judged afresh, as before.</summary>
    [Theory]
    [MemberData(nameof(Worlds))]
    public void ACrossingAskedAgainIsTheCrossingAFreshSearchFinds(string name)
    {
        WorldGenerator world = World(name);
        int found = 0;
        foreach (var (fords, bridges) in new[] { (true, true), (false, true), (true, false) })
        {
            var kept = Finder(world, fords, bridges);
            for (int x = -40; x <= 40; x++)
                for (int z = -3; z <= 3; z++)
                    foreach (Vector2Int d in Principal())
                    {
                        var cell = new Vector2i(x, z);
                        var fresh = Ask(Finder(world, fords, bridges), cell, null, d);
                        Assert.Equal(fresh, Ask(kept, cell, null, d));
                        Assert.Equal(fresh, Ask(kept, cell, null, d));
                        if (fresh.found) found++;
                        if (z == 0 && d.y == 0)
                        {
                            Vector2 leaving = new(x * RoadConstants.PathfindingCellSize + 1.5f, 0.75f);
                            Assert.Equal(Ask(Finder(world, fords, bridges), cell, leaving, d), Ask(kept, cell, leaving, d));
                        }
                    }
            // One pathfinder whose crossings were asked with bridges on, asked with them off, gives what one with them off gives.
            kept.Bridges = !bridges;
            kept.Fords = true;
            for (int x = -40; x <= 40; x++)
                foreach (Vector2Int d in Principal())
                    Assert.Equal(Ask(Finder(world, true, !bridges), new Vector2i(x, 0), null, d), Ask(kept, new Vector2i(x, 0), null, d));
        }
        Assert.True(found > 0, "the sweep should find crossings");
    }

    private static readonly MethodInfo Facts = typeof(RoadPathfinder).GetMethod("CrossingFactsFor", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static bool InteriorWater(RoadPathfinder finder, Vector2i cell, Vector2Int direction, bool whole)
    {
        object facts = Facts.Invoke(finder, new object?[] { cell, null, direction, whole })!;
        return (bool)facts.GetType().GetField("InteriorWater")!.GetValue(facts)!;
    }

    private static IEnumerable<Vector2Int> AllMoves() => RoadPathfinder.Directions;

    /// <summary>An ordinary move asks only whether water lies inside it (RoadPathfinder.RoadMoveCost); the facts memo then
    /// holds that answer alone. The same answer as the whole facts give, and a crossing asked after it is the crossing a
    /// fresh pathfinder finds, in every direction a search moves, in both memory modes.</summary>
    [Theory]
    [MemberData(nameof(Worlds))]
    public void AMovesWaterProbeAskedFirstLeavesTheCrossingAFreshSearchFinds(string name)
    {
        WorldGenerator world = World(name);
        bool low = GenerationMemory.Low;
        try
        {
            foreach (bool keepLess in new[] { false, true })
            {
                GenerationMemory.Low = keepLess;
                var probed = Finder(world, true, true);
                int wet = 0;
                for (int x = -40; x <= 40; x++)
                    for (int z = -2; z <= 2; z++)
                        foreach (Vector2Int d in AllMoves())
                        {
                            var cell = new Vector2i(x, z);
                            bool water = InteriorWater(probed, cell, d, whole: false);
                            Assert.Equal(InteriorWater(Finder(world, true, true), cell, d, whole: true), water);
                            Assert.Equal(water, InteriorWater(probed, cell, d, whole: false));
                            Assert.Equal(Ask(Finder(world, true, true), cell, null, d), Ask(probed, cell, null, d));
                            Assert.Equal(water, InteriorWater(probed, cell, d, whole: false));
                            if (water) wet++;
                        }
                Assert.True(wet > 0, "the sweep should find moves over water");
            }
        }
        finally { GenerationMemory.Low = low; }
    }

    /// <summary>The facts memo's packed key keeps every cell, direction and switch apart, negative cells included.</summary>
    [Fact]
    public void TheFactsKeyTellsEveryCellDirectionAndSwitchApart()
    {
        var seen = new HashSet<long>();
        int count = 0;
        foreach (int x in new[] { -1313, -2, -1, 0, 1, 2, 1313 })
            foreach (int z in new[] { -1313, -1, 0, 1, 1313 })
                foreach (Vector2Int d in AllMoves())
                    for (int switches = 0; switches < 8; switches++)
                    {
                        count++;
                        Assert.True(seen.Add(RoadPathfinder.CrossingKey(new Vector2i(x, z), d, (switches & 4) != 0, (switches & 2) != 0, (switches & 1) != 0)));
                    }
        Assert.Equal(count, seen.Count);
    }

    /// <summary>A crossing asked again reads nothing of the world: its scan, its ford-or-bridge class, its banks and its
    /// squeezes are this pathfinder's facts.</summary>
    [Fact]
    public void ACrossingAskedAgainReadsNothingOfTheWorld()
    {
        var world = new Counting(new BridgeTests.WideRiverWorld { EastRise = 1f });
        var finder = Finder(world, true, true);
        int crossings = 0;
        for (int x = -40; x <= 40; x++)
            foreach (Vector2Int d in Principal())
            {
                var cell = new Vector2i(x, 0);
                var first = Ask(finder, cell, null, d);
                world.Reads = 0;
                Assert.Equal(first, Ask(finder, cell, null, d));
                Assert.Equal(0, world.Reads);
                if (first.found) crossings++;
            }
        Assert.True(crossings > 0);
    }

    /// <summary>With the generation keeping less in memory (GenerationMemory.Low), the crossing facts are worked out again at
    /// each ask, not kept, and give the same crossings as kept ones.</summary>
    [Theory]
    [MemberData(nameof(Worlds))]
    public void KeepingLessInMemoryGivesTheSameCrossingsAndKeepsNone(string name)
    {
        WorldGenerator world = World(name);
        bool low = GenerationMemory.Low;
        try
        {
            var kept = Finder(world, true, true);
            GenerationMemory.Low = true;
            var unkept = Finder(world, true, true);
            var store = typeof(RoadPathfinder).GetField("m_crossings", BindingFlags.NonPublic | BindingFlags.Instance)!;
            for (int x = -40; x <= 40; x++)
                foreach (Vector2Int d in Principal())
                {
                    var cell = new Vector2i(x, 0);
                    GenerationMemory.Low = false;
                    var keptAnswer = Ask(kept, cell, null, d);
                    GenerationMemory.Low = true;
                    Assert.Equal(keptAnswer, Ask(unkept, cell, null, d));
                    Assert.Equal(keptAnswer, Ask(unkept, cell, null, d));
                }
            Assert.Empty((System.Collections.IEnumerable)store.GetValue(unkept)!);
            Assert.True(((System.Collections.ICollection)store.GetValue(kept)!).Count > 0);
        }
        finally { GenerationMemory.Low = low; }
    }
}
