using System;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public class RoadIslandDetectorTests
{
    private sealed class ChannelWorld : WorldGenerator
    {
        public bool River;
        public bool Swamp;
        public float Water = 20;
        public override float GetHeight(float x, float z) => Math.Abs(x) < 24 ? Water : 40;
        public override Heightmap.Biome GetBiome(float x, float z) => Swamp ? Heightmap.Biome.Swamp : Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        { weight=River && Math.Abs(x)<24 ? 1 : 0; width=48; }
    }

    private static Island At(int cells, float minX, float minY) => new()
    { CellCount = cells, Min = new Vector2(minX, minY), Max = new Vector2(minX + 48, minY + 48) };

    /// <summary>Id decides the selection tie-break and, with routed connections
    /// off, an island's plan by parity, so equal-sized islands may not take
    /// their ids from Dictionary enumeration order through an unstable sort.
    /// The comparator is asserted directly because the instability is not
    /// observable from outside on a single runtime - the dictionary happens to
    /// yield one order, and a test through Detect passes either way.</summary>
    [Fact]
    public void EqualSizedIslandsAreOrderedByPositionNotByArrival()
    {
        Island southWest = At(100, -64, -64), northEast = At(100, 64, 64);
        Assert.True(RoadIslandDetector.BySizeThenPosition(southWest, northEast) < 0);
        Assert.True(RoadIslandDetector.BySizeThenPosition(northEast, southWest) > 0);

        // and the order is the same whichever way the list arrived
        foreach (var arrival in new[] { new[] { southWest, northEast }, new[] { northEast, southWest } })
        {
            var list = new System.Collections.Generic.List<Island>(arrival);
            list.Sort(RoadIslandDetector.BySizeThenPosition);
            Assert.Same(southWest, list[0]);
        }
    }

    [Fact]
    public void ABiggerIslandStillComesFirst()
    {
        Island big = At(200, 64, 64), small = At(100, -64, -64);
        Assert.True(RoadIslandDetector.BySizeThenPosition(big, small) < 0);
    }

    [Fact]
    public void OpenDeepStraitKeepsLandSeparate()
    {
        var islands = RoadIslandDetector.Detect(new ChannelWorld(), worldRadius:128, minArea:1);
        Assert.Equal(2, islands.Count);
        Assert.NotEqual(islands[0].ContainsPoint(-64,0), islands[0].ContainsPoint(64,0));
    }

    [Fact]
    public void RiverCanGroupBothBanks()
    {
        var islands = RoadIslandDetector.Detect(new ChannelWorld { River=true }, worldRadius:128, minArea:1);
        var island = Assert.Single(islands);
        Assert.True(island.ContainsPoint(-64,0));
        Assert.True(island.ContainsPoint(64,0));
    }

    [Fact]
    public void SwampShallowsRemainWalkable()
    {
        Assert.Single(RoadIslandDetector.Detect(new ChannelWorld { Swamp=true,Water=29 }, worldRadius:128, minArea:1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(float.NaN)]
    [InlineData(10001)]
    public void RejectsUnboundedWorldRadius(float radius) => Assert.Throws<ArgumentOutOfRangeException>(
        () => RoadIslandDetector.Detect(new ChannelWorld(),worldRadius:radius));

    [Fact]
    public void MinimumAreaFiltersSmallGroups() =>
        Assert.Empty(RoadIslandDetector.Detect(new ChannelWorld(),worldRadius:128,minArea:1000000));
}
