using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The steepest step the search offers a staircase follows the kit that would be built there (the
/// Black Forest's stone kit can cantilever, so steeper steps are allowed there): one owner, <see cref="StairKit.ForSite"/>.
/// </summary>
public sealed class StairGradeKitTests : System.IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    public void Dispose() => _scope.Dispose();

    private sealed class World : WorldGenerator
    {
        public override int GetSeed() => 5;
    }

    private RoadPathfinder Finder() { _scope.WithWorld(new World()); return new RoadPathfinder(WorldGenerator.instance); }

    [Theory]
    [InlineData(Heightmap.Biome.Meadows, false)]
    [InlineData(Heightmap.Biome.Mountain, true)]
    [InlineData(Heightmap.Biome.Mistlands, true)]
    public void TheKitOfTheBiomeDecidesTheGrade(Heightmap.Biome biome, bool cantilevers)
    {
        RoadPathfinder finder = Finder();
        Assert.Equal(cantilevers ? RoadPathfinder.StairCantileverMaxGrade : RoadPathfinder.StairMaxGrade, finder.StairGradeLimit(biome, new Vector2(1200f, 300f)));
    }

    [Fact]
    public void ABlackForestStepIsSteepOnlyWhereStoneIsBuilt()
    {
        RoadPathfinder finder = Finder();
        int stone = 0, wood = 0;
        for (int i = 0; i < 400; i++)
        {
            var at = new Vector2(3000f + i * 8f, 3000f);
            bool isStone = StairKit.ForSite(Heightmap.Biome.BlackForest, at, 5) == StairKit.Stone;
            Assert.Equal(isStone ? RoadPathfinder.StairCantileverMaxGrade : RoadPathfinder.StairMaxGrade, finder.StairGradeLimit(Heightmap.Biome.BlackForest, at));
            if (isStone) stone++; else wood++;
        }
        Assert.True(stone > 0 && wood > 0, $"{stone} stone, {wood} wood: the test needs both");
        // Near the centre it is all wood, as its stone share is 0 there.
        Assert.Equal(RoadPathfinder.StairMaxGrade, finder.StairGradeLimit(Heightmap.Biome.BlackForest, new Vector2(100f, 100f)));
    }
}
