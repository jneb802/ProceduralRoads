using System;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public class RoadTerrainSamplesTests
{
    private sealed class CountingWorld : WorldGenerator
    {
        public int Heights, Biomes, Rivers, OriginReads;
        public float Ground = 40;
        public float Flow = 0.1f;
        public bool ThrowOnce;
        public override float GetHeight(float x, float z)
        {
            Heights++;
            if (x == 0 && z == 0) OriginReads++;
            if (ThrowOnce) { ThrowOnce = false; throw new InvalidOperationException("terrain unavailable"); }
            return Ground + x * 0.001f + z * 0.002f;
        }
        public override Heightmap.Biome GetBiome(float x, float z) { Biomes++; return Heightmap.Biome.Meadows; }
        public override void GetRiverWeight(float x, float z, out float w, out float width) { Rivers++; w=Flow; width=5; }
    }
    [Fact]
    public void GridComparerPreservesEqualityWithoutConcentratingDiagonalCells()
    {
        var comparer = RoadGridComparer.Instance;
        var hashes = new System.Collections.Generic.HashSet<int>();
        for (int i=-512; i<512; i++) {
            var p = new Vector2i(i,i);
            Assert.True(comparer.Equals(p,new Vector2i(i,i)));
            Assert.False(comparer.Equals(p,new Vector2i(i,i+1)));
            hashes.Add(comparer.GetHashCode(p));
        }
        Assert.True(hashes.Count > 512, "diagonal keys concentrate in too few hash buckets");
    }
    [Fact]
    public void RealSearchReusesTheStartingCellAcrossOutgoingMoves()
    {
        var world = new CountingWorld { Flow=0 };
        var finder = new RoadPathfinder(world);
        Assert.NotNull(finder.FindPath(new Vector2(0,0), new Vector2(160,0)));
        // Ring samples from neighbouring cells may also hit the origin.
        // The sixteen outgoing moves must not each evaluate it again.
        Assert.InRange(world.OriginReads, 1, 4);
    }
    [Fact]
    public void RepeatedMovesReuseFactsAndVarianceButDoNotReadUnusedFacts()
    {
        var world = new CountingWorld(); var samples = new RoadTerrainSamples(world); var p = new Vector2i(3, -7);
        float height = samples.Height(p);
        Assert.Equal(1, world.Heights); Assert.Equal(0, world.Biomes); Assert.Equal(0, world.Rivers);
        float variance = samples.Variance(p);
        for (int i=0;i<16;i++)
        {
            Assert.Equal(height, samples.Height(p)); Assert.Equal(variance, samples.Variance(p));
            Assert.Equal(Heightmap.Biome.Meadows, samples.Biome(p)); Assert.Equal(0.1f, samples.River(p));
        }
        Assert.Equal(1 + RoadConstants.TerrainVarianceSampleCount, world.Heights);
        Assert.Equal(1, world.Biomes); Assert.Equal(1, world.Rivers);
    }
    [Fact]
    public void CollidingCoordinatesEvictRatherThanReuseAnotherHeight()
    {
        var world = new CountingWorld(); var samples = new RoadTerrainSamples(world);
        var a = new Vector2i(0,0); var b = new Vector2i(RoadTerrainSamples.Capacity,0);
        float first = samples.Height(a); float other = samples.Height(b);
        Assert.NotEqual(first,other); Assert.Equal(first,samples.Height(a)); Assert.Equal(3,world.Heights);
    }
    [Fact]
    public void ResetDiscardsAllFacts()
    {
        var world = new CountingWorld(); var samples = new RoadTerrainSamples(world); var p = new Vector2i(0,0);
        Assert.Equal(40,samples.Height(p)); samples.Biome(p); samples.River(p); samples.Variance(p);
        world.Ground=50; samples.Reset();
        Assert.Equal(50,samples.Height(p)); samples.Biome(p); samples.River(p); samples.Variance(p);
        Assert.Equal(2,world.Biomes); Assert.Equal(2,world.Rivers);
        Assert.Equal(2 * (1 + RoadConstants.TerrainVarianceSampleCount),world.Heights);
    }
    [Fact]
    public void FailedReadIsNotRememberedAsAValidSample()
    {
        var world = new CountingWorld { ThrowOnce=true }; var samples = new RoadTerrainSamples(world);
        Assert.Throws<InvalidOperationException>(()=>samples.Height(new Vector2i(0,0)));
        Assert.Equal(40,samples.Height(new Vector2i(0,0))); Assert.Equal(2,world.Heights);
    }
    [Fact]
    public void GroundDecidesTheSearchAndAFailedOneLeavesNothingBehind()
    {
        // Was PathfinderResetsItsCacheEvenAfterAFailedSearch, which moved the
        // ground under a live pathfinder to prove the memo had been blanked.
        // The memo is kept between searches now, and no generator behaves that
        // way: a pathfinder is built for one world and discarded with it. Each
        // world therefore gets its own pathfinder, as in the mod.
        var start = new Vector2(0,0); var end = new Vector2(160,0);
        Assert.Null(new RoadPathfinder(new CountingWorld { Ground=10, Flow=0 }).FindPath(start,end));
        Assert.NotNull(new RoadPathfinder(new CountingWorld { Ground=40, Flow=0 }).FindPath(start,end));
        Assert.Null(new RoadPathfinder(new CountingWorld { Ground=10, Flow=0 }).FindPath(start,end));
    }

    [Fact]
    public void AFailedSearchDoesNotSpoilTheNextOneOnTheSamePathfinder()
    {
        // The original point of the test above, kept: a search that finds
        // nothing must leave the memo holding facts, not wreckage.
        var world = new CountingWorld { Ground=40, Flow=0 };
        var finder = new RoadPathfinder(world);
        // Starved of iterations rather than of ground: the ground here is
        // walkable everywhere, so a budget is what makes a search give up.
        int previous = RoadPathfinder.MaxIterations;
        try
        {
            RoadPathfinder.MaxIterations = 50;
            Assert.Null(finder.FindPath(new Vector2(0,0), new Vector2(40000,0)));
            RoadPathfinder.MaxIterations = previous;
            Assert.NotNull(finder.FindPath(new Vector2(0,0), new Vector2(160,0)));
        }
        finally { RoadPathfinder.MaxIterations = previous; }
    }

    [Fact]
    public void TheMemoIsKeptBetweenSearchesOnTheOneWorld()
    {
        var world = new CountingWorld { Flow=0 };
        var finder = new RoadPathfinder(world);
        var start = new Vector2(0,0); var end = new Vector2(160,0);
        var first = finder.FindPath(start,end);
        Assert.NotNull(first);
        int asked = world.Heights + world.Biomes + world.Rivers;
        Assert.True(asked > 0, "the first search read nothing, so this proves nothing");

        var again = finder.FindPath(start,end);
        int repeat = world.Heights + world.Biomes + world.Rivers - asked;
        // Same ground, mostly already held. Not zero: cells whose hashes
        // collide turn each other out of the one slot they share and are read
        // again, which is what the eviction counter is for and what a bigger
        // memo would answer. Measured on this world: 1387 cold, 484 repeated.
        Assert.True(repeat * 2 < asked,
            $"a repeated search read {repeat} of the first search's {asked}; reuse is not working");
        // And the answer is the one the cold search gave.
        Assert.Equal(first!.Count, again!.Count);
        for (int i = 0; i < first.Count; i++) Assert.Equal(first[i], again[i]);
    }

    [Fact]
    public void KeepingTheMemoDoesNotChangeWhatIsFound()
    {
        // A pathfinder whose memo is warm from an overlapping search must
        // return exactly what a cold one returns. Cached or recomputed is the
        // same fact from the same generator; if it ever were not, the memo
        // would be holding something that does not belong in it.
        var start = new Vector2(-200,40); var end = new Vector2(240,-120);
        var warm = new RoadPathfinder(new CountingWorld { Flow=0 });
        warm.FindPath(new Vector2(-200,0), new Vector2(200,0));
        var fromWarm = warm.FindPath(start,end);
        var fromCold = new RoadPathfinder(new CountingWorld { Flow=0 }).FindPath(start,end);
        Assert.Equal(fromCold == null, fromWarm == null);
        if (fromCold == null) return;
        Assert.Equal(fromCold.Count, fromWarm!.Count);
        for (int i = 0; i < fromCold.Count; i++) Assert.Equal(fromCold[i], fromWarm[i]);
    }
    [Fact]
    public void CacheStorageDoesNotGrowWithExploredArea()
    {
        var samples = new RoadTerrainSamples(new CountingWorld());
        for(int i=0;i<RoadTerrainSamples.Capacity*3;i++) samples.Height(new Vector2i(i,-i));
        var array = (Array)typeof(RoadTerrainSamples).GetField("samples",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(samples)!;
        Assert.Equal(RoadTerrainSamples.Capacity,array.Length);
    }

    [Fact]
    public void CountersSeparateFactsHeldFromFactsReadFromTheWorld()
    {
        var world = new CountingWorld(); var samples = new RoadTerrainSamples(world);
        var p = new Vector2i(3, -7);
        samples.Height(p); samples.Biome(p); samples.River(p); samples.Variance(p);
        // Four different facts, none of them held yet. Variance asks Height
        // for its centre, and that one is already held: the single hit.
        Assert.Equal(4, samples.Misses);
        Assert.Equal(1, samples.Hits);
        // Height, biome and river are one world call each; variance is a ring
        // of TerrainVarianceSampleCount on top of the centre height it reuses.
        Assert.Equal(3 + RoadConstants.TerrainVarianceSampleCount, samples.TerrainCalls);
        Assert.Equal(world.Heights + world.Biomes + world.Rivers, samples.TerrainCalls);

        for (int i = 0; i < 10; i++)
        { samples.Height(p); samples.Biome(p); samples.River(p); samples.Variance(p); }
        // Variance asks Height for the centre first, so a round is five hits.
        Assert.Equal(1 + 50, samples.Hits);
        Assert.Equal(4, samples.Misses);
        Assert.Equal(3 + RoadConstants.TerrainVarianceSampleCount, samples.TerrainCalls);
    }

    [Fact]
    public void AnEvictionIsCountedOnlyWhenASlotHeldSomethingElse()
    {
        var world = new CountingWorld(); var samples = new RoadTerrainSamples(world);
        var a = new Vector2i(0, 0); var b = new Vector2i(RoadTerrainSamples.Capacity, 0);
        // First touch of an empty slot is not an eviction.
        samples.Height(a);
        Assert.Equal(0, samples.Replacements);
        // b hashes to a's slot and turns it out; a then turns b out.
        samples.Height(b);
        Assert.Equal(1, samples.Replacements);
        samples.Height(a);
        Assert.Equal(2, samples.Replacements);
        // A slot asked for what it already holds is neither evicted nor missed.
        samples.Height(a);
        Assert.Equal(2, samples.Replacements);
        Assert.Equal(1, samples.Hits);
    }

    [Fact]
    public void TotalsCollectFromAMemoThatIsAboutToBeDiscarded()
    {
        RoadTerrainSamples.ResetTotals();
        var world = new CountingWorld();
        var first = new RoadTerrainSamples(world); var second = new RoadTerrainSamples(world);
        first.Height(new Vector2i(1, 1)); first.Height(new Vector2i(1, 1));
        second.Height(new Vector2i(2, 2));
        first.FoldIntoTotals(); second.FoldIntoTotals();
        Assert.Equal(2, RoadTerrainSamples.TotalMisses);
        Assert.Equal(1, RoadTerrainSamples.TotalHits);
        Assert.Equal(2, RoadTerrainSamples.TotalTerrainCalls);
        // Folding hands the numbers over rather than copying them, so a memo
        // folded twice does not count twice.
        first.FoldIntoTotals();
        Assert.Equal(2, RoadTerrainSamples.TotalMisses);
        RoadTerrainSamples.ResetTotals();
        Assert.Equal(0, RoadTerrainSamples.TotalMisses);
    }

    [Fact]
    public void ARefusedReadIsCountedAsAWorldCallAndStillNotHeld()
    {
        var world = new CountingWorld { ThrowOnce = true };
        var samples = new RoadTerrainSamples(world);
        Assert.Throws<InvalidOperationException>(() => samples.Height(new Vector2i(0, 0)));
        // The world was asked, and answered with a throw: a call, not a fact.
        Assert.Equal(1, samples.TerrainCalls);
        Assert.Equal(1, samples.Misses);
        Assert.Equal(0, samples.Hits);
        samples.Height(new Vector2i(0, 0));
        Assert.Equal(2, samples.Misses);
        Assert.Equal(0, samples.Hits);
    }
}
