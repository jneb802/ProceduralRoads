using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The steepest step the search offers a staircase follows the kit that would be built there: one owner,
/// <see cref="StairKit.ForSite"/>.
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

    /// <summary>Every kit laid now is held up by StairSupport, so may climb a sheer face; old saves' wood stood on columns.</summary>
    [Theory]
    [InlineData(Heightmap.Biome.Meadows)]
    [InlineData(Heightmap.Biome.BlackForest)]
    [InlineData(Heightmap.Biome.Mountain)]
    [InlineData(Heightmap.Biome.Mistlands)]
    public void EveryBiomesKitClimbsSheerFaces(Heightmap.Biome biome)
    {
        RoadPathfinder finder = Finder();
        Assert.Equal(RoadPathfinder.StairCantileverMaxGrade, finder.StairGradeLimit(biome, new Vector2(1200f, 300f)));
        Assert.Equal(RoadPathfinder.StairMaxGrade, RoadPathfinder.StairGradeLimit(StairKit.Wood));
    }
}
