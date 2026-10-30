using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// One owner of "is this ground water" (RoadWater): one threshold set, and each rule that asks a different question
/// of it is a named case there.
/// </summary>
public class RoadWaterTests
{
    /// <summary>Ground at <see cref="Height"/> everywhere; swamp north of z = 0, meadows south; a river's core east of
    /// x = 1000.</summary>
    private sealed class Bands : WorldGenerator
    {
        public float Height = 29f;
        public override float GetHeight(float x, float z) => Height;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            wy > 0f ? Heightmap.Biome.Swamp : Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = x > 1000f ? 0.6f : 0f; width = 10f; }
    }

    private static readonly Vector2 Swamp = new(0f, 50f), Meadow = new(0f, -50f), River = new(1100f, -50f);

    [Fact]
    public void GroundIsClassifiedOnOneSetOfLines()
    {
        var world = new Bands();
        using var scope = new ValheimWorldScope().WithWorld(world);
        world.Height = 31f;
        Assert.Equal(RoadWater.Kind.Dry, RoadWater.At(world, Meadow));
        Assert.Equal(RoadWater.Kind.River, RoadWater.At(world, River));
        world.Height = 29f;
        Assert.Equal(RoadWater.Kind.Waded, RoadWater.At(world, Swamp));
        Assert.Equal(RoadWater.Kind.Shallow, RoadWater.At(world, Swamp, wade: false));
        Assert.Equal(RoadWater.Kind.Shallow, RoadWater.At(world, Meadow));
        world.Height = 27f;
        Assert.Equal(RoadWater.Kind.Deep, RoadWater.At(world, Swamp));
    }

    /// <summary>Named case Walked: the search and the shore fill walk a swamp's shallows only where fords are on (the
    /// shore fill ignored the switch before).</summary>
    [Fact]
    public void ASwampsShallowsAreWalkedOnlyWhereFordsAreOn()
    {
        var world = new Bands { Height = 29f };
        using var scope = new ValheimWorldScope().WithWorld(world);
        Assert.True(RoadWater.Walked(world, Swamp));
        Assert.False(RoadWater.Walked(world, Swamp, wade: false));
        Assert.False(RoadWater.Walked(world, Meadow));
        Assert.Equal(RoadConstants.DeepWaterHeight, RoadWater.Floor(world, Swamp));
        Assert.Equal(RoadConstants.ShallowWaterHeight, RoadWater.Floor(world, Swamp, wade: false));
        // The shore fill asks the same test, the fords switch included.
        Assert.False(RoadShoreFill.Wet(world, Swamp));
        Assert.True(RoadShoreFill.Wet(world, Swamp, wade: false));
        // Waded ground is land to the fill; with fords off it is shallows it builds out (1.5 m under the floor).
        Assert.False(RoadShoreFill.Unbuildable(world, Swamp, 29f));
        Assert.False(RoadShoreFill.Unbuildable(world, Swamp, 29f, wade: false));
        world.Height = 27f;
        Assert.True(RoadShoreFill.Unbuildable(world, Swamp, 29f, wade: false), "3.5 m under the shallow line");
    }

    /// <summary>Named case BuiltDry: what is shaped or built (pull, sway, turn curves, staircases, a place's entry and
    /// platform) never wades: a swamp's shallows the search walks are not dry for it, nor a river's core.</summary>
    [Fact]
    public void ShapedAndBuiltGroundNeverWades()
    {
        var world = new Bands { Height = 29f };
        using var scope = new ValheimWorldScope().WithWorld(world);
        Assert.True(RoadWater.Walked(world, Swamp));
        Assert.False(RoadWater.BuiltDry(29f));
        Assert.True(RoadWater.BuiltDry(RoadConstants.ShallowWaterHeight));
        Assert.False(RoadWater.BuiltDry(31f, 0.6f));
        Assert.True(RoadWater.BuiltDry(31f, 0.4f));
    }

    /// <summary>Named case LandingFloorFor: a crossing lands, a road ends and an island's land is a bank clearance over
    /// the shallows; a swamp wades down to the deep line.</summary>
    [Fact]
    public void ALandingStandsABankClearanceOverTheShallows()
    {
        Assert.Equal(RoadConstants.ShallowWaterHeight + RoadConstants.BankClearance, RoadWater.LandingFloorFor(Heightmap.Biome.Meadows));
        Assert.Equal(RoadConstants.DeepWaterHeight, RoadWater.LandingFloorFor(Heightmap.Biome.Swamp));
        // Dry for a built road, but not ground a crossing lands on.
        float bank = RoadConstants.ShallowWaterHeight + RoadConstants.BankClearance * 0.5f;
        Assert.True(RoadWater.BuiltDry(bank));
        Assert.True(bank < RoadWater.LandingFloor);
    }
}
