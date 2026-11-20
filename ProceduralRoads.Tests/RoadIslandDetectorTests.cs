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
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Swamp ? Heightmap.Biome.Swamp : Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        { weight=River && Math.Abs(x)<24 ? 1 : 0; width=48; }
    }

    /// <summary>One landmass the game renders dry, with a point-biome strip across it: every heightmap corner (zone centre
    /// plus or minus 32 m) is <see cref="Corner"/>, but the point biome is <see cref="Strip"/> where |x| &lt; 12, a strip that
    /// reaches no corner. The game builds each zone from its corners' biomes, so the strip is rendered at the corners'
    /// height; WorldGenerator.GetHeight reads the strip biome's own formula there, under the water line.</summary>
    private sealed class PointBiomeStripWorld : WorldGenerator
    {
        public Heightmap.Biome Corner = Heightmap.Biome.BlackForest;
        public Heightmap.Biome Strip = Heightmap.Biome.Swamp;
        public float CornerHeight = 40f, StripHeight = 26f, HalfWidth = 12f;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            Math.Abs(x) < HalfWidth ? Strip : Corner;
        public override float GetBiomeHeight(Heightmap.Biome biome, float x, float z, out Color mask, bool preGeneration = false, bool riverPreDN = true)
        { mask = default; return biome == Corner ? CornerHeight : StripHeight; }
        public override float GetHeight(float x, float z) => GetBiomeHeight(GetBiome(x, z), x, z, out _);
    }

    /// <summary>A strip of another point biome across one rendered landmass does not split it: the detector reads the
    /// ground the game renders (BiomeBlendedHeight), as the road search does. Read as WorldGenerator.GetHeight the strip was
    /// water (a swamp formula under the deep line, an ocean formula under the shallow line) 24 m wide, too deep to ford and
    /// with no river to bridge, and the one landmass came out as two islands.</summary>
    [Theory]
    [InlineData(Heightmap.Biome.Swamp, 26f)]
    [InlineData(Heightmap.Biome.Ocean, 10f)]
    public void APointBiomeStripDoesNotSplitTheRenderedLand(Heightmap.Biome strip, float stripHeight)
    {
        var world = new PointBiomeStripWorld { Strip = strip, StripHeight = stripHeight };
        // The two readings at a cell in the strip: water raw, dry land as rendered.
        Assert.False(RoadWater.OnLand(world, new Vector2(0, 0), world.GetHeight(0, 0)));
        Assert.True(RoadWater.OnLand(world, new Vector2(0, 0), BiomeBlendedHeight.GetBlendedHeight(0, 0, world)));
        var island = Assert.Single(RoadIslandDetector.Detect(world, worldRadius:128, minArea:1));
        Assert.True(island.ContainsPoint(-64, 0));
        Assert.True(island.ContainsPoint(64, 0));
    }

    /// <summary>Water the game renders still splits the land: the strip widened over the heightmap corners at x = -32 and
    /// 32 is rendered at the swamp's height, under the deep line, across the zone between them.</summary>
    [Fact]
    public void RenderedWaterStillSplitsTheLand()
    {
        var world = new PointBiomeStripWorld { HalfWidth = 40f };
        Assert.False(RoadWater.OnLand(world, new Vector2(0, 0), BiomeBlendedHeight.GetBlendedHeight(0, 0, world)));
        var islands = RoadIslandDetector.Detect(world, worldRadius:128, minArea:1);
        Assert.Equal(2, islands.Count);
        Assert.NotEqual(islands[0].ContainsPoint(-96, 0), islands[0].ContainsPoint(96, 0));
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

    /// <summary>A point at an island's tip is on it: the membership test takes a point into the cell nearest it, half a cell
    /// either side of the cell's centre, and its bounding box reaches as far. The box was the outermost cells' centres, so a
    /// point in the last half cell at each of the island's four extreme extents was refused though its cell is the island's
    /// (a location centred there was no destination).</summary>
    [Fact]
    public void APointInTheHalfCellPastTheOutermostCellCentreIsOnTheIsland()
    {
        var islands = RoadIslandDetector.Detect(new ChannelWorld(), worldRadius:128, minArea:1);
        var east = Assert.Single(islands, i => i.ContainsPoint(64, 0));
        // The easternmost cell centre, and the southernmost.
        Assert.Equal(120f, east.Max.x);
        Assert.Equal(-120f, east.Min.y);
        Assert.True(east.ContainsPoint(123, 0));
        Assert.True(east.ContainsPoint(32, -123));
        // Past the half cell is the next cell, which is not the island's.
        Assert.False(east.ContainsPoint(125, 0));
        Assert.False(east.ContainsPoint(32, -125));
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
