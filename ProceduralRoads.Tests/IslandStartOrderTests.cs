using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// Islands start in the order of their Mistlands (RoadNetworkGenerator.GenerationWork.StartOrder): most Mistlands first,
/// ties and the rest as chosen, each keeping its number in the chosen order; one worker builds them as chosen.
/// </summary>
public class IslandStartOrderTests : IDisposable
{
    private readonly int _workers = RoadParallel.Configured;

    public void Dispose() => RoadParallel.Configured = _workers;

    /// <summary>Mistlands east of x = 0, Meadows west of it.</summary>
    private sealed class HalfMistWorld : WorldGenerator
    {
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            wx > 0f ? Heightmap.Biome.Mistlands : Heightmap.Biome.Meadows;
    }

    private static Island IslandOf(params Vector2Int[] cells) =>
        new() { CellSize = 8f, WorldOffset = 0f, Cells = cells.ToList(), CellCount = cells.Length };

    private static RoadNetworkGenerator.GenerationWork Work(WorldGenerator world, int workers, params Island[] islands)
    {
        RoadParallel.Configured = workers;
        var work = new RoadNetworkGenerator.GenerationWork(DateTime.Now, world);
        foreach (Island island in islands)
            work.Islands.Add(new RoadNetworkGenerator.IslandWork(island, new List<(string, Vector3, float)>(), null, 0f));
        return work;
    }

    private sealed class ObservedWorld : WorldGenerator
    {
        internal int OpenReads, ClosedReads;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false)
        {
            if (RiverWeightReads.TryRead(this, wx, wy, out _, out _)) System.Threading.Interlocked.Increment(ref OpenReads);
            else System.Threading.Interlocked.Increment(ref ClosedReads);
            return Heightmap.Biome.Meadows;
        }
    }

    [Fact]
    public void ComputeOpensRiverReadsOnTheSameWorldItUsesToOrderIslands()
    {
        using var scope = new ValheimWorldScope().WithWorld(new HalfMistWorld());
        RoadNetworkGenerator.Reset();
        var world = new ObservedWorld();
        var work = Work(world, 2, IslandOf(new Vector2Int(-1, 0)), IslandOf(new Vector2Int(1, 0)));
        work.Compute();
        Assert.True(world.OpenReads > 0);
        Assert.Equal(0, world.ClosedReads);
        Assert.False(RiverWeightReads.TryRead(world, 0f, 0f, out _, out _));
    }

    [Fact]
    public void IslandsStartMostMistlandsFirstAndKeepTheirNumbers()
    {
        var world = new HalfMistWorld();
        // Cells west of x 0 are Meadows (x index <= 0), east of it Mistlands.
        Island none = IslandOf(new Vector2Int(-3, 0), new Vector2Int(-2, 0), new Vector2Int(-1, 0));
        Island two = IslandOf(new Vector2Int(-1, 1), new Vector2Int(1, 1), new Vector2Int(2, 1));
        Island three = IslandOf(new Vector2Int(1, 2), new Vector2Int(2, 2), new Vector2Int(3, 2));
        Island twoAgain = IslandOf(new Vector2Int(1, 3), new Vector2Int(2, 3));
        Assert.Equal(new[] { 0, 2, 3, 2 }, new[] { none, two, three, twoAgain }.Select(i => RoadNetworkGenerator.MistlandsCells(i, world)));
        // The chosen order (numbers 0..3) is kept: the start order lists those numbers, most Mistlands first, ties as chosen.
        var work = Work(world, 4, none, two, three, twoAgain);
        Assert.Equal(new[] { 2, 1, 3, 0 }, work.StartOrder(world));
        Assert.Same(three, work.Islands[2].Island);
        // One worker builds them as chosen.
        Assert.Equal(new[] { 0, 1, 2, 3 }, Work(world, 1, none, two, three, twoAgain).StartOrder(world));
    }
}
