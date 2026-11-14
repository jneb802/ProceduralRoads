using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A bridge's price in the search falls as its deck stands taller, toward the tallest the stone kit stands
/// (BridgeLayout.StoneHeightReach), weighted by its span (RoadPathfinder.BridgeHeightFactor): nothing on a span no longer
/// than a ford's, the whole on the longest bridge. Asked through the search's own jump (RoadPathfinder.TryGetRiverCrossing)
/// over a straight river whose banks stand at one height on both sides, and read on the crossing the detector builds on the
/// road through the same cells (one owner of the deck the search prices and the builder lays).
/// </summary>
public sealed class BridgeHeightPriceTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public BridgeHeightPriceTests() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); }
    public void Dispose() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); _scope.Dispose(); }

    /// <summary>A river <see cref="Half"/> each side of x = 0, its bed at <see cref="Bed"/>; the land beyond, both banks, flat
    /// at <see cref="Bank"/>.</summary>
    private sealed class RiverWorld : WorldGenerator
    {
        public float Half = 58f, Bed = 27f, Bank = 31.5f;
        public Heightmap.Biome Biome = Heightmap.Biome.BlackForest;
        public override float GetHeight(float wx, float wy) => Mathf.Abs(wx) < Half ? Bed : Bank;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Biome;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = Mathf.Abs(wx) < Half ? 1f : 0f;
            width = 2f * Half;
        }
    }

    private static readonly MethodInfo Crossing = typeof(RoadPathfinder).GetMethod("TryGetRiverCrossing", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo Facts = typeof(RoadPathfinder).GetMethod("CrossingFactsFor", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>The last dry cell west of the river: the jump's start.</summary>
    private static Vector2i WestBank(RiverWorld w) => new(-(Mathf.FloorToInt((w.Half - 1e-3f) / RoadConstants.PathfindingCellSize) + 1), 0);

    /// <summary>The search's price of the bridge east over <paramref name="w"/>'s river, and the jump's span.</summary>
    private (float price, float span, float reach) Jump(RiverWorld w)
    {
        _scope.WithWorld(w);
        var finder = new RoadPathfinder(w) { Bridges = true, Fords = true };
        var args = new object?[] { WestBank(w), null, new Vector2Int(1, 0), null, null };
        Assert.True((bool)Crossing.Invoke(finder, args)!, $"no bridge over a {2f * w.Half} m river");
        object facts = Facts.Invoke(finder, new object?[] { WestBank(w), null, new Vector2Int(1, 0), true })!;
        Assert.True((bool)facts.GetType().GetField("Bridge")!.GetValue(facts)!);
        float span = (float)facts.GetType().GetField("Distance")!.GetValue(facts)!;
        float reach = (float)facts.GetType().GetField("StoneReach")!.GetValue(facts)!;
        return ((float)args[4]!, span, reach);
    }

    /// <summary>The bridge's own price without its height, as the search priced it before: fixed, per metre, the road's metre.</summary>
    private static float Plain(float span) =>
        RoadConstants.DefaultBridgeCostFixed + RoadConstants.DefaultBridgeCostPerMeter * span + RoadConstants.DefaultBaseCost * span;

    /// <summary>The bank height at which the deck stands as tall as the stone kit stands over the bed.</summary>
    private static float AtTheLimit(RiverWorld w) => w.Bed + BridgeLayout.StoneTallestPier;

    [Fact]
    public void OfTwoEqualLongStoneCrossingsTheTallerPricesCheaperUpToTheKitLimitAndNotPastIt()
    {
        var w = new RiverWorld();
        Assert.Equal(9.75f, BridgeLayout.StoneTallestPier, 3);
        var (low, span, lowReach) = Jump(w);
        Assert.Equal(RoadConstants.MaxBridgeSpan, span, 3);
        Assert.Equal(0f, lowReach);
        // A deck at the water line pays what a bridge paid before.
        Assert.Equal(Plain(span), low, 1);

        float previous = low;
        foreach (float bank in new[] { 33f, 34.5f, 36f, AtTheLimit(w) })
        {
            w.Bank = bank;
            var (price, _, reach) = Jump(w);
            Assert.True(price < previous, $"banks at {bank} m: {price} not under {previous}");
            Assert.True(reach > 0f && reach <= 1f, $"banks at {bank} m: reach {reach}");
            previous = price;
        }
        // At the limit the longest bridge pays the share of its own price.
        Assert.Equal((RoadConstants.DefaultBridgeCostFixed + RoadConstants.DefaultBridgeCostPerMeter * span) * RoadConstants.DefaultBridgeHeightShare
            + RoadConstants.DefaultBaseCost * span, previous, 0);

        // Past the stone kit's limit a taller deck is no cheaper.
        foreach (float past in new[] { AtTheLimit(w) + 0.5f, AtTheLimit(w) + 2f })
        {
            w.Bank = past;
            var (price, _, reach) = Jump(w);
            Assert.Equal(1f, reach);
            Assert.Equal(previous, price, 0);
        }
    }

    /// <summary>A river square to the knight's heading (2, 1) through the origin, <see cref="Half"/> each side of it along
    /// that heading, its bed at <see cref="Bed"/>; both banks flat at <see cref="Bank"/>.</summary>
    private sealed class SlantRiver : WorldGenerator
    {
        public float Half = 40f, Bed = 27f, Bank = 31.5f;
        private static readonly Vector2 Knight = new Vector2(2f, 1f).normalized;
        private bool Wet(float x, float z) => Mathf.Abs(x * Knight.x + z * Knight.y) < Half;
        public override float GetHeight(float wx, float wy) => Wet(wx, wy) ? Bed : Bank;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.BlackForest;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = Wet(wx, wy) ? 1f : 0f;
            width = 2f * Half;
        }
    }

    /// <summary>The search's price of the first bridge a knight's jump (2, 1) buys over <paramref name="w"/>'s river, from the
    /// knight's line's cells west of it, with its span and reach.</summary>
    private (float price, float span, float reach) KnightJump(SlantRiver w)
    {
        _scope.WithWorld(w);
        var finder = new RoadPathfinder(w) { Bridges = true, Fords = true };
        var knight = new Vector2Int(2, 1);
        for (int k = 1; k <= 12; k++)
        {
            var from = new Vector2i(-2 * k, -k);
            var args = new object?[] { from, null, knight, null, null };
            if (!(bool)Crossing.Invoke(finder, args)!) continue;
            object facts = Facts.Invoke(finder, new object?[] { from, null, knight, true })!;
            if (!(bool)facts.GetType().GetField("Bridge")!.GetValue(facts)!) continue;
            return ((float)args[4]!, (float)facts.GetType().GetField("Distance")!.GetValue(facts)!,
                (float)facts.GetType().GetField("StoneReach")!.GetValue(facts)!);
        }
        Assert.Fail("no knight's bridge over the slant river");
        return default;
    }

    /// <summary>A knight's jump pays a bridge's full price at any deck height; a grid heading's keeps the height share. On the
    /// knight's sixteen headings the search bought cheaper tall bridges it then could not build onto, and the roads that took
    /// them were refused where the full price kept them on their Mistlands viaducts (13 Nov 2026). Through the search's own
    /// jump (RoadPathfinder.TryGetRiverCrossing): a long knight's bridge with its deck at the stone kit's limit pays what it
    /// pays at the water line, and the same height on a grid heading's bridge of the longest span still pays the share.</summary>
    [Fact]
    public void AKnightsBridgePaysTheFullPriceAtAnyHeightAndAGridBridgeKeepsTheShare()
    {
        var slant = new SlantRiver();
        var (low, span, lowReach) = KnightJump(slant);
        Assert.True(span > RoadConstants.MaxFordSpan, $"the knight's bridge spans {span} m, no longer than a ford's");
        Assert.Equal(0f, lowReach);
        Assert.Equal(Plain(span), low, 1);
        slant.Bank = slant.Bed + BridgeLayout.StoneTallestPier;
        var (tall, tallSpan, tallReach) = KnightJump(slant);
        Assert.Equal(span, tallSpan, 3);
        Assert.Equal(0f, tallReach);
        Assert.Equal(low, tall, 0);

        var grid = new RiverWorld();
        float gridLow = Jump(grid).price;
        grid.Bank = AtTheLimit(grid);
        var (gridTall, gridSpan, gridReach) = Jump(grid);
        Assert.Equal(1f, gridReach);
        Assert.Equal((RoadConstants.DefaultBridgeCostFixed + RoadConstants.DefaultBridgeCostPerMeter * gridSpan) * RoadConstants.DefaultBridgeHeightShare
            + RoadConstants.DefaultBaseCost * gridSpan, gridTall, 0);
        Assert.True(gridTall < gridLow, $"the grid's tall bridge pays {gridTall}, not under {gridLow}");
    }

    /// <summary>A gorge across x = 0: water under 30 m out, a dry shelf at the water line to 34 m, then a 4.5 m climb to a
    /// plateau at 36 from 38 m out on both sides, within the detector's bank-top reach of the water's edge.</summary>
    private sealed class GorgeWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            float ax = Mathf.Abs(wx);
            if (ax < 30f) return 26f;
            if (ax < 34f) return 31.5f;
            if (ax < 38f) return Mathf.Lerp(31.5f, 36f, (ax - 34f) / 4f);
            return 36f;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.BlackForest;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = Mathf.Clamp01(1f - Mathf.Abs(wx) / 60f);
            width = weight > 0f ? 120f : 0f;
        }
    }

    /// <summary>The owner's decision of 13 Nov 2026: a deck is never discounted for a height it may not be laid at. The search
    /// detects a jump on its straight line, and there the detector climbs from the water's edge to the bank tops
    /// (RoadCrossing.OnTops); the builder detects on the road, which may bend within that climb's reach and lay the deck at
    /// the water's edge (BridgeTests.ADeckSpringsFromTheBankTopsWhereTheRoadClimbsACliffOnBothSides, the whole case). Through
    /// the search's own jump (RoadPathfinder.TryGetRiverCrossing) over a gorge whose tops stand 4.5 m over its water-line
    /// shelves: the straight line's crossing climbs to the tops and stands tall enough for the share, yet the jump gets none
    /// and pays a bridge's full price. Banks tall at the water's edge itself keep the share
    /// (OfTwoEqualLongStoneCrossingsTheTallerPricesCheaperUpToTheKitLimitAndNotPastIt).</summary>
    [Fact]
    public void AJumpWhoseDetectionClimbsToTheTopsGetsNoHeightShare()
    {
        var w = new GorgeWorld();
        _scope.WithWorld(w);
        var finder = new RoadPathfinder(w) { Bridges = true, Fords = true };
        var east = new Vector2Int(1, 0);
        for (int x = -4; x >= -8; x--)
        {
            var from = new Vector2i(x, 0);
            var args = new object?[] { from, null, east, null, null };
            if (!(bool)Crossing.Invoke(finder, args)!) continue;
            object facts = Facts.Invoke(finder, new object?[] { from, null, east, true })!;
            if (!(bool)facts.GetType().GetField("Bridge")!.GetValue(facts)!) continue;
            float span = (float)facts.GetType().GetField("Distance")!.GetValue(facts)!;
            Assert.True(span > RoadConstants.MaxFordSpan, $"a {span} m jump is no longer than a ford's");

            // The jump's straight line, as the search detects it: the banks climbed to the tops, a deck the share would buy.
            Vector2 a = RoadPathfinder.CellCentre(from), b = RoadPathfinder.CellCentre((Vector2i)args[3]!);
            var line = new List<Vector2> { a - new Vector2(RoadConstants.HighBankReach, 0f), a, b, b + new Vector2(RoadConstants.HighBankReach, 0f) };
            RoadCrossing straight = Assert.Single(RoadCrossingDetector.Detect(line, w, bridges: true, fords: true), c => c.Kind == CrossingKind.Bridge);
            Assert.True(straight.OnTops, $"the straight line's crossing {straight.FromBank} -> {straight.ToBank} did not climb to the tops");
            Assert.True(BridgeLayout.StoneHeightReach(straight, w) > 0f, "the tops stand no taller than the water line");

            Assert.Equal(0f, (float)facts.GetType().GetField("StoneReach")!.GetValue(facts)!);
            Assert.Equal(Plain(span), (float)args[4]!, 1);
            return;
        }
        Assert.Fail("no bridge over the gorge");
    }

    [Fact]
    public void AShortSpanGainsLittleFromHeightAndALongSpanClearlyMore()
    {
        float Saving(float half)
        {
            var w = new RiverWorld { Half = half };
            float low = Jump(w).price;
            w.Bank = AtTheLimit(w);
            float tall = Jump(w).price;
            return 1f - tall / low;
        }
        // Cell to cell: 48 m (no longer than a ford's longest jump), 64 m, 96 m and the longest, 128 m.
        float ford = Saving(22f), shorter = Saving(30f), mid = Saving(42f), longest = Saving(58f);
        Assert.Equal(0f, ford, 4);
        // With the share at 0.3: 64 m weighs 0.2 (0.3^0.2 = 0.79, about 21 % saved), 96 m 0.6 (0.49, about 51 %), 128 m the
        // whole (0.3, about 70 %).
        Assert.True(shorter > 0f && shorter < 0.3f, $"a 64 m bridge saves {shorter:P0}");
        Assert.True(mid > shorter && mid < longest, $"a 96 m bridge saves {mid:P0}");
        Assert.True(longest > 0.65f, $"the longest bridge saves {longest:P0}");
    }

    [Fact]
    public void TheWeightRisesFromTheLongestFordToTheLongestBridge()
    {
        var finder = new RoadPathfinder(new RiverWorld());
        Assert.Equal(1f, finder.BridgeHeightFactor(1f, RoadConstants.MaxFordSpan));
        // The detector's ford cap (RoadCrossingDetector.WithinFordCap) is the longest ford, no slack: a span past it is longer
        // than a ford's, as the detector judges it.
        Assert.True(RoadCrossingDetector.WithinFordCap(RoadConstants.MaxFordSpan));
        Assert.False(RoadCrossingDetector.WithinFordCap(RoadConstants.MaxFordSpan + 0.25f));
        Assert.True(finder.BridgeHeightFactor(1f, RoadConstants.MaxFordSpan + 0.25f) < 1f);
        Assert.Equal(1f, finder.BridgeHeightFactor(0f, RoadConstants.MaxBridgeSpan));
        Assert.Equal(RoadConstants.DefaultBridgeHeightShare, finder.BridgeHeightFactor(1f, RoadConstants.MaxBridgeSpan), 5);
        Assert.Equal(Mathf.Pow(RoadConstants.DefaultBridgeHeightShare, 0.5f * 0.5f),
            finder.BridgeHeightFactor(0.5f, (RoadConstants.MaxFordSpan + RoadConstants.MaxBridgeSpan) * 0.5f), 5);
        // Off: the share at 1.
        finder.BridgeHeightShare = 1f;
        Assert.Equal(1f, finder.BridgeHeightFactor(1f, RoadConstants.MaxBridgeSpan));
    }

    [Theory]
    [InlineData(Heightmap.Biome.Meadows)]
    [InlineData(Heightmap.Biome.Mistlands)]
    public void WhereNoStoneDeckBelongsHeightBuysNothing(Heightmap.Biome biome)
    {
        var w = new RiverWorld { Biome = biome };
        float low = Jump(w).price;
        w.Bank = AtTheLimit(w);
        var (tall, _, reach) = Jump(w);
        Assert.Equal(0f, reach);
        Assert.Equal(low, tall, 0);
    }

    /// <summary>The height the search prices is the one the builder lays: the reach on the crossing the detector builds on the
    /// road through the jump's cells, from the same owner (BridgeLayout.StoneHeightReach of BridgeLayout.WoodDeckHeight).</summary>
    [Fact]
    public void TheSearchPricesTheDeckTheBuilderLays()
    {
        var w = new RiverWorld { Bank = 34.5f };
        var (_, _, reach) = Jump(w);
        float x = WestBank(w).x * RoadConstants.PathfindingCellSize;
        var road = new List<Vector2> { new(x - 16f, 0f), new(x - 8f, 0f), new(x, 0f), new(-x, 0f), new(-x + 8f, 0f), new(-x + 16f, 0f) };
        RoadCrossing built = Assert.Single(RoadCrossingDetector.Detect(road, w, bridges: true, fords: true));
        Assert.Equal(CrossingKind.Bridge, built.Kind);
        Assert.Equal(34.5f, BridgeLayout.WoodDeckHeight(built, w), 3);
        Assert.Equal(BridgeLayout.StoneHeightReach(built, w), reach, 5);
        Assert.InRange(reach, 0.5f, 0.6f);
    }

    /// <summary>The stone kit's tallest pier is its support estimate's: a deck that high over flat ground under its piers
    /// fits the kit, a little higher does not.</summary>
    [Fact]
    public void TheTallestStonePierIsWhereTheKitStopsFitting()
    {
        foreach (var (rise, fits) in new[] { (BridgeLayout.StoneTallestPier - 0.05f, true), (BridgeLayout.StoneTallestPier + 0.05f, false) })
        {
            var w = new RiverWorld { Half = 16f, Bed = 25f, Bank = 25f + rise };
            _scope.WithWorld(w);
            var c = RoadCrossing.Between(new Vector2(-16f, 0f), new Vector2(16f, 0f), w.Bed, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
            c.WaterLevel = 25f - BridgeLayout.DeckFreeboard;
            Assert.Equal(fits, BridgeLayout.StoneFits(c, w));
        }
    }
}
