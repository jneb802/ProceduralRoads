using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
using static ProceduralRoads.Tests.DvergrViaductEndStairTests;

namespace ProceduralRoads.Tests;

/// <summary>
/// The height rules of the Dvergr viaducts (the vanilla viaducts look taller). Height pays more, with a lower crossing
/// as the fallback if nothing is found: over 10 m a viaduct's price falls
/// steeply, to a tenth at the 26 m ceiling (DvergrViaduct.HeightFactor), and never above the plain inverse price (10 / h), so a low one is
/// never priced out. And a lower limit: no viaduct whose tallest
/// pier is under DvergrViaduct.MinViaductPier (12 m); a valley then keeps its road or stairs, a river its wooden kit.
/// </summary>
public sealed class DvergrViaductHeightTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public DvergrViaductHeightTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
    }

    public void Dispose()
    {
        DvergrViaduct.CeilingPriceShare = DvergrViaduct.DefaultCeilingPriceShare;
        DvergrViaduct.MinViaductPier = DvergrViaduct.DefaultMinViaductPier;
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    private static RoadPathfinder Finder(WorldGenerator world) =>
        new(world) { Stairs = true, Viaducts = true, TerrainVarianceThreshold = 1000f, Meander = 0f };

    private static (Vector2 from, Vector2 to)? Jump(StepValley world, float z)
    {
        RoadPathfinder finder = Finder(world);
        Assert.NotNull(finder.FindPath(new Vector2(-100f, z), new Vector2(100f, z)));
        return finder.LastViaductJumps.Count == 0 ? null : finder.LastViaductJumps[0];
    }

    private static float Tallest(StepValley world, (Vector2 from, Vector2 to) jump) =>
        DvergrViaduct.TallestPier(DvergrViaduct.ValleyCrossing(jump.from, jump.to, world, out _)!, world);

    // ------------------------------------------------- tall first

    [Fact]
    public void TheHeightFactorIsTheInversePriceUpToTenMetresAndFallsSteeplyToATenthAtTheCeiling()
    {
        Assert.Equal(10f / 6f, DvergrViaduct.HeightFactor(6f), 3);
        Assert.Equal(1f, DvergrViaduct.HeightFactor(10f), 3);
        Assert.Equal(0.1f, DvergrViaduct.HeightFactor(26f), 3);
        Assert.Equal(0.1f, DvergrViaduct.HeightFactor(30f), 3);
        Assert.Equal(Mathf.Pow(0.1f, 10f / 16f), DvergrViaduct.HeightFactor(20f), 3);
        for (float h = 6f; h < 26f; h += 0.5f)
        {
            // Never dearer than the inverse price (10 / h): a viaduct it builds is not priced out.
            Assert.True(DvergrViaduct.HeightFactor(h) <= 10f / h + 1e-4f, $"{h} m");
            Assert.True(DvergrViaduct.HeightFactor(h + 0.5f) < DvergrViaduct.HeightFactor(h), $"{h} m");
        }
        // Over 10 m it pays clearly more for height than the inverse price does.
        Assert.True(DvergrViaduct.HeightFactor(14f) < 0.6f && DvergrViaduct.HeightFactor(17f) < 0.4f);
        // At a half share at the ceiling it is the inverse price.
        DvergrViaduct.CeilingPriceShare = 0.5f;
        for (float h = 6f; h <= 26f; h += 0.5f) Assert.Equal(10f / h, DvergrViaduct.HeightFactor(h), 4);
    }

    /// <summary>The same 64 m span costs less the taller its piers stand, all the way to the ceiling: 26 m under 20 m under 12 m.</summary>
    [Fact]
    public void ASpanOnTwentySixMetrePiersCostsLessThanOnTwentyAndTwentyLessThanOnTwelve()
    {
        float PriceAt(float depth)
        {
            var w = new DvergrViaductTests.MistValley { Top = 80f, Depth = depth };
            _scope.WithWorld(w);
            RoadCrossing c = DvergrViaduct.ValleyCrossing(new Vector2(-32f, 0f), new Vector2(32f, 0f), w, out string why)!;
            Assert.True(c != null, why);
            return DvergrViaduct.Price(c, w, RoadConstants.DefaultViaductCostFixed, RoadConstants.DefaultViaductCostPerMeter, 0f);
        }
        float p12 = PriceAt(12.5f), p20 = PriceAt(20.5f), p26 = PriceAt(25.5f);
        Assert.True(p26 < p20 && p20 < p12, $"26 m {p26:F0}, 20 m {p20:F0}, 12 m {p12:F0}");
    }

    /// <summary>One valley, 25 m deep south of z = 0 and 13 m north of it, walls too steep to walk; the road runs
    /// along z = 100: it goes 100 m south to the 25 m piers rather than cross where it is.</summary>
    [Fact]
    public void OfATallAndALowCrossingNearTheCeilingTheTallOneIsChosen()
    {
        var world = new StepValley { Top = 80f, Face = 2f, Depth = z => z < 0f ? 25f : 13f };
        _scope.WithWorld(world);
        var jump = Jump(world, 100f);
        Assert.True(jump.HasValue, "no viaduct");
        Assert.True(Tallest(world, jump!.Value) >= 25f, $"crossed on {Tallest(world, jump.Value):F1} m piers");
    }

    /// <summary>One valley, 18 m deep south of z = 0 and 13 m north of it; the road runs along z = 100. The inverse price
    /// crossed where the road is (13 m piers); the height price goes 100 m south to the 18 m piers.</summary>
    [Fact]
    public void OfATallAndALowCrossingOfOneValleyTheTallOneIsChosen()
    {
        var world = new StepValley { Depth = z => z < 0f ? 18f : 13f };
        _scope.WithWorld(world);
        var tall = Jump(world, 100f);
        Assert.True(tall.HasValue, "no viaduct");
        Assert.True(Tallest(world, tall!.Value) >= 16f, $"crossed on {Tallest(world, tall.Value):F1} m piers");

        // Control: the inverse price (a half share at the ceiling) keeps the low crossing.
        DvergrViaduct.CeilingPriceShare = 0.5f;
        var low = Jump(world, 100f);
        Assert.True(low.HasValue, "no viaduct at the inverse price");
        Assert.True(Tallest(world, low!.Value) < 14f, $"the inverse price crossed on {Tallest(world, low.Value):F1} m piers");
    }

    [Fact]
    public void WhereOnlyALowViaductFitsItIsStillBuilt()
    {
        // 13 m deep everywhere: the only viaduct is on 13 m piers, over the 12 m floor.
        var world = new StepValley { Depth = _ => 13f };
        _scope.WithWorld(world);
        var jump = Jump(world, 0f);
        Assert.True(jump.HasValue, "the low viaduct was priced out");
        float tallest = Tallest(world, jump!.Value);
        Assert.InRange(tallest, DvergrViaduct.MinViaductPier, 14f);
    }

    // ------------------------------------------------- the floor

    [Theory]
    [InlineData(11.5f, false)]
    [InlineData(12.5f, true)]
    public void AValleyUnderTheFloorGetsNoViaductAtOrOverItDoes(float depth, bool viaduct)
    {
        Assert.Equal(12f, DvergrViaduct.DefaultMinViaductPier);
        var world = new StepValley { Depth = _ => depth };
        _scope.WithWorld(world);
        float half = 8f + depth / 1.2f + 2f;
        RoadCrossing? c = DvergrViaduct.ValleyCrossing(new Vector2(-half, 0f), new Vector2(half, 0f), world, out string why);
        Assert.True((c != null) == viaduct, $"{depth} m: {(c == null ? why : "built")}");
        if (!viaduct) Assert.Contains("floor", why);
        var jump = Jump(world, 0f);
        Assert.Equal(viaduct, jump.HasValue);
        if (jump.HasValue) Assert.True(Tallest(world, jump.Value) >= DvergrViaduct.MinViaductPier - 1e-3f);
    }

    [Theory]
    [InlineData(11.5f, false)]
    [InlineData(12.5f, true)]
    public void ARiverUnderTheFloorKeepsTheWoodenKitAtOrOverItIsAViaduct(float height, bool viaduct)
    {
        // Banks at the bed plus the height over a sailable river; the bed under the water (sea level 30).
        float bed = 26f;
        var world = new DvergrViaductTests.MistRiver { Bank = bed + height, Bed = bed };
        _scope.WithWorld(world);
        RoadCrossing c = DvergrViaductTests.River();
        float tallest = DvergrViaduct.TallestPier(c, world);
        Assert.InRange(tallest, height - 0.6f, height + 0.6f);
        Assert.Equal(viaduct, tallest >= DvergrViaduct.MinViaductPier);
        Assert.Equal(viaduct, DvergrViaduct.Applies(c, world));
        List<StructurePiece> pieces = BridgeStructures.Solve(c, world, world.GetSeed());
        Assert.NotEmpty(pieces);
        Assert.Equal(viaduct, pieces.All(DvergrViaductTests.IsDvergr));
        if (!viaduct) Assert.DoesNotContain(pieces, DvergrViaductTests.IsDvergr);
    }

    [Fact]
    public void TheFloorIsASetting()
    {
        // 13 m piers: a viaduct at the 12 m default, none at 15 m.
        var world = new StepValley { Depth = _ => 13f };
        _scope.WithWorld(world);
        float half = 8f + 13f / 1.2f + 2f;
        Assert.NotNull(DvergrViaduct.ValleyCrossing(new Vector2(-half, 0f), new Vector2(half, 0f), world, out _));
        DvergrViaduct.MinViaductPier = 15f;
        Assert.Null(DvergrViaduct.ValleyCrossing(new Vector2(-half, 0f), new Vector2(half, 0f), world, out string why));
        Assert.Contains("under the 15 m floor", why);
        Assert.False(Jump(world, 0f).HasValue);
    }
}
