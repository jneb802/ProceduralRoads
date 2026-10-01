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
    public void ResetDiscardsAllFactsBetweenSearches()
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
    public void PathfinderResetsItsCacheEvenAfterAFailedSearch()
    {
        var world = new CountingWorld { Ground=10, Flow=0 }; var pathfinder = new RoadPathfinder(world);
        var start=new Vector2(0,0);var end=new Vector2(160,0);
        Assert.Null(pathfinder.FindPath(start,end));
        world.Ground=40;
        Assert.NotNull(pathfinder.FindPath(start,end));
        world.Ground=10;
        Assert.Null(pathfinder.FindPath(start,end));
    }
    [Fact]
    public void CacheStorageDoesNotGrowWithExploredArea()
    {
        var samples = new RoadTerrainSamples(new CountingWorld());
        for(int i=0;i<RoadTerrainSamples.Capacity*3;i++) samples.Height(new Vector2i(i,-i));
        var array = (Array)typeof(RoadTerrainSamples).GetField("samples",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(samples)!;
        Assert.Equal(RoadTerrainSamples.Capacity,array.Length);
    }
}
