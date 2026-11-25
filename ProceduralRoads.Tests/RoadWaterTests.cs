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
    /// <summary>The sea line's two named cases and the cap's two keep the answers of the comparisons they replaced for an
    /// unknown (NaN) height: neither wet nor over the sea, neither past the cap nor within it.</summary>
    [Fact]
    public void UnknownHeightIsNeitherSide()
    {
        Assert.False(RoadWater.Wet(float.NaN));
        Assert.False(RoadWater.AtOrOverSea(float.NaN));
        Assert.True(RoadWater.AtOrOverSea(RoadConstants.SeaLevel));
        Assert.True(RoadWater.Wet(RoadConstants.SeaLevel - 0.001f));
        Assert.False(RoadNetworkGenerator.PastTerrainLimit(float.NaN, 30f, 8f));
        Assert.False(RoadNetworkGenerator.WithinTerrainLimit(float.NaN, 30f, 8f));
        Assert.True(RoadNetworkGenerator.WithinTerrainLimit(38f, 30f, 8f));
        Assert.True(RoadNetworkGenerator.PastTerrainLimit(38.01f, 30f, 8f));
    }

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

    /// <summary>Named case BuiltDry: what is shaped or built (pull, sway, turn curves, staircases, a location's entry and
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

    /// <summary>A patch of <see cref="Inner"/> (raw <see cref="InnerHeight"/>) inside a zone whose heightmap corners are all
    /// <see cref="Outer"/> (rendered <see cref="OuterHeight"/>): GetHeight reads the patch's own formula, as the game's does;
    /// the game renders the corners'. In game at (4293.0, -3515.2): a Swamp point at 29.0 m raw inside Black Forest corners,
    /// rendered at 36.3 m.</summary>
    private sealed class Patch : WorldGenerator
    {
        public Heightmap.Biome Outer = Heightmap.Biome.BlackForest, Inner = Heightmap.Biome.Swamp;
        public float OuterHeight = 36.3f, InnerHeight = 29f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            Mathf.Abs(wx) < 20f && Mathf.Abs(wy) < 20f ? Inner : Outer;
        public override float GetBiomeHeight(Heightmap.Biome biome, float wx, float wy, out Color mask, bool preGeneration = false, bool riverPreDN = true)
        {
            mask = default;
            return biome == Inner ? InnerHeight : OuterHeight;
        }
        public override float GetHeight(float wx, float wy) => GetBiomeHeight(GetBiome(wx, wy), wx, wy, out _);
    }

    /// <summary>Every water test judges the ground as it will be (RoadWater.Ground), not the generator's raw height: a
    /// patch the generator puts under the shallow line that the game renders dry is dry, for the built road, the search
    /// and a landing alike; one it puts dry that the game renders under water is wet.</summary>
    [Fact]
    public void WaterIsJudgedOnTheRenderedGroundNotTheGeneratorsRawHeight()
    {
        var world = new Patch();
        using var scope = new ValheimWorldScope().WithWorld(world);
        var p = new Vector2(5f, 4f);
        Assert.True(world.GetHeight(p.x, p.y) < RoadConstants.ShallowWaterHeight, "the generator reads it under water");
        Assert.Equal(36.3f, RoadWater.Ground(world, p), 3);
        Assert.True(RoadWater.BuiltDry(world, p));
        Assert.Equal(RoadWater.Kind.Dry, RoadWater.At(world, p));
        Assert.True(RoadWater.OnLand(world, p));
        Assert.False(RoadShoreFill.Wet(world, p));

        // The other way: raw 31 m inside corners rendered at 29 m is water.
        world.Inner = Heightmap.Biome.Meadows; world.Outer = Heightmap.Biome.Plains; world.InnerHeight = 31f; world.OuterHeight = 29f;
        Assert.True(world.GetHeight(p.x, p.y) >= RoadConstants.ShallowWaterHeight);
        Assert.False(RoadWater.BuiltDry(world, p));
        Assert.Equal(RoadWater.Kind.Shallow, RoadWater.At(world, p));
    }

    /// <summary>Real water stays water: ground the game renders under the shallow line is wet whatever reads it.</summary>
    [Fact]
    public void RealWaterIsStillWet()
    {
        var world = new Patch { Outer = Heightmap.Biome.Meadows, Inner = Heightmap.Biome.Meadows, OuterHeight = 29f, InnerHeight = 29f };
        using var scope = new ValheimWorldScope().WithWorld(world);
        var p = new Vector2(5f, 4f);
        Assert.False(RoadWater.BuiltDry(world, p));
        Assert.Equal(RoadWater.Kind.Shallow, RoadWater.At(world, p));
        Assert.False(RoadWater.OnLand(world, p));
        Assert.True(RoadShoreFill.Wet(world, p));
    }

    /// <summary>Where a location placed before its roads levels its ground, that levelled ground is what a water test judges
    /// (PlacedLocations.GroundAt, the owner): a location that raises its ground over the shallows the generator reads there is
    /// dry ground in its footprint; outside it the generator's water is water.</summary>
    [Fact]
    public void ALocationsLevelledGroundOverGeneratorWaterIsDry()
    {
        var world = new Bands { Height = 29f };
        using var scope = new ValheimWorldScope().WithWorld(world);
        try
        {
            var centre = new Vector2(0f, -50f);
            RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(centre, 20f) });
            PlacedLocations.Ready(new PlacedLocation { Record = new PlacedLocationRecord { Prefab = "Test", Centre = centre }, Ground = (_, _) => 33f });
            var inside = centre + new Vector2(6f, 3f);
            Assert.Equal(33f, RoadWater.Ground(world, inside));
            Assert.True(RoadWater.BuiltDry(world, inside));
            Assert.Equal(RoadWater.Kind.Dry, RoadWater.At(world, inside));
            Assert.False(RoadWater.BuiltDry(world, centre + new Vector2(60f, 0f)));
        }
        finally
        {
            PlacedLocations.Reset();
            RoadLocationProtection.Reset();
        }
    }
}

/// <summary>The dry floor is the shallow line, and raising to it is one rule.</summary>
public class DryFloorTests
{
    [Fact]
    public void DryFloorIsTheShallowLine() =>
        Assert.Equal(RoadConstants.ShallowWaterHeight, RoadConstants.DryRoadFloor);

    [Theory]
    [InlineData(-5f, 30.5f)]
    [InlineData(0f, 30.5f)]
    [InlineData(28f, 30.5f)]
    [InlineData(30f, 30.5f)]
    [InlineData(30.4999f, 30.5f)]
    [InlineData(30.5f, 30.5f)]
    [InlineData(30.5001f, 30.5001f)]
    [InlineData(31.25f, 31.25f)]
    [InlineData(120f, 120f)]
    public void RaisingToTheFloorIsTheHigherOfTheGroundAndTheFloor(float g, float raised) =>
        Assert.Equal(raised, RoadWater.AtLeastDryFloor(g));
}

/// <summary>"Wet" is the one sea-line test.</summary>
public class WetTests
{
    [Theory]
    [InlineData(-100f, true)]
    [InlineData(29.9999f, true)]
    [InlineData(30f, false)]
    [InlineData(30.0001f, false)]
    [InlineData(31f, false)]
    public void WetIsUnderTheSeaLevel(float h, bool wet) => Assert.Equal(wet, RoadWater.Wet(h));
}
