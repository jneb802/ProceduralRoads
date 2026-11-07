using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Whether a bridge is a Mistlands one is the staircases' rule (StairKit.InMistlands: at least 40 % of it in the Mistlands),
/// over its length from bank to bank, not the biome at its centre (assistant-written, 6 Nov 2026): a bridge whose first
/// 40 % is in the Mistlands takes the Mistlands kit though its middle is not, and one with less takes the wooden kits.
/// </summary>
public sealed class BridgeBiomeShareTests : System.IDisposable
{
    /// <summary>A structure across a biome edge leans to the more advanced kit: it takes it where at least the share
    /// (StairKit.BiomeShare, 40 %) stands where that kit belongs. Half in the Meadows, a bridge may be stone; mostly Meadows,
    /// it may not.</summary>
    [Theory]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    public void ABoundaryLeansToTheMoreAdvancedKit(int meadowsOfTen, bool stone)
    {
        var places = new System.Collections.Generic.List<Vector2>();
        for (int i = 0; i < 10; i++) places.Add(new Vector2(i, 0f));
        Heightmap.Biome At(float x, float z) => x < meadowsOfTen ? Heightmap.Biome.Meadows : Heightmap.Biome.BlackForest;
        Assert.Equal(stone, StairKit.LeansTo(places, At, b => b != Heightmap.Biome.Meadows));
    }

    private readonly ValheimWorldScope _scope = new();
    public void Dispose() => _scope.Dispose();

    /// <summary>The Mistlands west of x = <see cref="Edge"/>, the Meadows east of it.</summary>
    private sealed class EdgeWorld : WorldGenerator
    {
        public float Edge;
        public override float GetHeight(float x, float z) => 30f;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            x < Edge ? Heightmap.Biome.Mistlands : Heightmap.Biome.BlackForest;
    }

    private static RoadCrossing Bridge() => new()
    {
        Kind = CrossingKind.Bridge,
        FromBank = new Vector2(0f, 0f),
        ToBank = new Vector2(40f, 0f),
        Center = new Vector2(20f, 0f),
    };

    [Theory]
    [InlineData(16.5f, true)]   // 40 % of the length (the centre is not in the Mistlands)
    [InlineData(10f, false)]    // a quarter
    [InlineData(30f, true)]     // three quarters, the centre too
    [InlineData(-5f, false)]    // none
    public void ABridgeIsMistlandsByTheShareOfItsLengthAsAStaircaseIs(float edge, bool mistlands)
    {
        var world = new EdgeWorld { Edge = edge };
        RoadCrossing c = Bridge();
        Assert.Equal(mistlands, BridgeStructures.InMistlands(c, world));
        Assert.Equal(mistlands, BridgeStructures.Fits(BridgeStructures.Kit.DvergrWood, c, world));
        Assert.Equal(!mistlands, BridgeStructures.Fits(BridgeStructures.Kit.WoodRail, c, world));
        Assert.Equal(!mistlands, BridgeStructures.Fits(BridgeStructures.Kit.WoodBraced, c, world));
        Assert.Equal(mistlands, StairKit.InMistlands(StairKit.Samples(new[] { c.FromBank, c.ToBank }), (x, z) => world.GetBiome(x, z)));
    }
}
