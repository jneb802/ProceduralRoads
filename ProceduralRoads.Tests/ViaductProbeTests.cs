using System;
using System.Reflection;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// One valley probe of the search (DvergrViaduct.ValleyCrossing, then its price): the Mistlands share rule asked before
/// any ground is read, with the verdict Refusal gives; its deck at grade read once (DvergrViaduct.GradeDeck) and handed to
/// the refusal and the price, which give what the deck's own functions give.
/// </summary>
public sealed class ViaductProbeTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    /// <summary>A valley along z: flats at <see cref="Top"/>, a floor <see cref="Depth"/> under them within
    /// <see cref="FloorHalf"/> of x = 0, faces of 1.2 between; sea past |x|, |z| = 200. Mistlands west of
    /// <see cref="MistlandsWestOf"/> (x), the Plains east of it. Counts its ground and river reads.</summary>
    internal sealed class Valley : WorldGenerator
    {
        public float Top = 60f, FloorHalf = 8f, Face = 1.2f;
        public Func<float, float> Depth = _ => 14f;
        public float MistlandsWestOf = float.MaxValue;
        /// <summary>How much lower the flat east of the valley is.</summary>
        public float FarDrop;
        public int Heights, Rivers;
        public override float GetHeight(float wx, float wy)
        {
            Heights++;
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            float top = wx > 0f ? Top - FarDrop : Top;
            float floor = Top - Depth(wy);
            return Mathf.Max(floor, top - Mathf.Clamp(top - floor - (Mathf.Abs(wx) - FloorHalf) * Face, 0f, top - floor));
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            wx < MistlandsWestOf ? Heightmap.Biome.Mistlands : Heightmap.Biome.Plains;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            Rivers++;
            weight = 0f;
            width = 0f;
        }
        public override int GetSeed() => 77;
    }

    public ViaductProbeTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
    }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    [Fact]
    public void AValleyOutsideTheMistlandsIsRefusedBeforeAnyGroundIsRead()
    {
        var plains = new Valley { MistlandsWestOf = -1000f };
        Assert.Null(DvergrViaduct.ValleyCrossing(new Vector2(-24f, 0f), new Vector2(24f, 0f), plains, out string why));
        Assert.Equal("not in the Mistlands", why);
        Assert.Equal(0, plains.Heights);
        Assert.Equal(0, plains.Rivers);
    }

    /// <summary>A valley whose banks straddle the Mistlands' edge, the edge swept across it: the probe's verdict is the one
    /// Refusal gives the same crossing (built where the whole valley is Mistlands), refused or not, and with the same reason.</summary>
    [Fact]
    public void AValleyAcrossTheMistlandsEdgeGetsRefusalsVerdict()
    {
        Vector2 from = new(-24f, 0f), to = new(24f, 0f);
        RoadCrossing built = DvergrViaduct.ValleyCrossing(from, to, new Valley(), out string allMistlands)!;
        Assert.True(built != null, allMistlands);
        int refused = 0, kept = 0;
        for (float edge = -30f; edge <= 30f; edge += 1f)
        {
            var world = new Valley { MistlandsWestOf = edge };
            string? expected = DvergrViaduct.Refusal(built!, world);
            RoadCrossing? probe = DvergrViaduct.ValleyCrossing(from, to, world, out string why);
            Assert.Equal(expected == null, probe != null);
            if (expected != null) { Assert.Equal(expected, why); refused++; }
            else kept++;
        }
        Assert.True(refused > 0 && kept > 0, $"{refused} refused, {kept} kept: the sweep should cross the share rule's line");
    }

    private static int Bits(float f) => BitConverter.ToInt32(BitConverter.GetBytes(f), 0);

    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Static;

    /// <summary>The price as the deck's own functions give it, each read afresh: at grade (AtGrade), the tallest pier
    /// (TallestPier) and the natural ends (NaturalEnds) there, in Price's formula.</summary>
    private static float PriceReadAfresh(RoadCrossing c, WorldGenerator world)
    {
        float deck = DvergrViaduct.AtGrade(c, world);
        float tallest = (float)typeof(DvergrViaduct).GetMethod("TallestPier", Private, null,
            new[] { typeof(RoadCrossing), typeof(WorldGenerator), typeof(float) }, null)!.Invoke(null, new object[] { c, world, deck })!;
        var ends = ((StructureEnds.End near, StructureEnds.End far))typeof(DvergrViaduct).GetMethod("NaturalEnds", Private, null,
                new[] { typeof(RoadCrossing), typeof(WorldGenerator), typeof(float), typeof((Vector2, Vector2)?) }, null)!
            .Invoke(null, new object?[] { c, world, deck, null })!;
        return (RoadConstants.DefaultViaductCostFixed + RoadConstants.DefaultViaductCostPerMeter * c.Width) * DvergrViaduct.HeightFactor(tallest)
            + RoadConstants.DefaultViaductEndStairCostPerMeter * (ends.near.Rise + ends.far.Rise);
    }

    /// <summary>Valleys of many depths, widths and far-bank drops: where the probe lays a viaduct, its price from the deck it
    /// read is the one the deck's functions give read afresh, to the bit, and the one the public price gives; where it does
    /// not, Refusal of the same crossing refuses it with the same reason.</summary>
    [Fact]
    public void AProbesDeckGivesThePriceAndVerdictItsFunctionsGive()
    {
        int laid = 0, refused = 0;
        foreach (float depth in new[] { 6f, 9f, 14f, 20f, 28f, 36f })
        foreach (float drop in new[] { 0f, 1f, 2f, 3f })
        foreach (float half in new[] { 12f, 20f, 31f, 44f })
        {
            var world = new Valley { Depth = z => depth + z * 0.05f, FarDrop = drop };
            Vector2 from = new(-half, 3f), to = new(half, 3f);
            RoadCrossing? c = DvergrViaduct.ValleyCrossing(from, to, world, out string why, out DvergrViaduct.GradeDeck? grade);
            Assert.Equal(DvergrViaduct.ValleyCrossing(from, to, world, out string plainWhy) != null, c != null);
            Assert.Equal(plainWhy, why);
            if (c == null) { Assert.Null(grade); refused++; continue; }
            Assert.Same(c, grade!.Crossing);
            Assert.Null(DvergrViaduct.Refusal(c, world));
            float price = DvergrViaduct.Price(grade, RoadConstants.DefaultViaductCostFixed, RoadConstants.DefaultViaductCostPerMeter, RoadConstants.DefaultViaductEndStairCostPerMeter);
            Assert.Equal(Bits(PriceReadAfresh(c, world)), Bits(price));
            Assert.Equal(Bits(DvergrViaduct.Price(c, world, RoadConstants.DefaultViaductCostFixed,
                RoadConstants.DefaultViaductCostPerMeter, RoadConstants.DefaultViaductEndStairCostPerMeter)), Bits(price));
            Assert.Equal(Bits(DvergrViaduct.AtGrade(c, world)), Bits(grade.Height));
            Assert.Equal(DvergrViaduct.PierAlongs(c, world), grade.Piers);
            laid++;
        }
        Assert.True(laid > 0 && refused > 0, $"{laid} laid, {refused} refused: the sweep should cover both");
    }

    /// <summary>One probe and its price read the ship's way under the deck (a sample of the river every metre along it)
    /// once, besides the probe's own river samples along the valley: its deck is read once, not again by every test of the
    /// refusal and the price.</summary>
    [Fact]
    public void OneProbeAndItsPriceReadTheShipsWayOnce()
    {
        var world = new Valley { FarDrop = 2f };
        Vector2 from = new(-24f, 0f), to = new(24f, 0f);
        RoadCrossing c = DvergrViaduct.ValleyCrossing(from, to, world, out string why)!;
        Assert.True(c != null, why);
        world.Rivers = 0;
        DvergrViaduct.ShipWayUnder(c!, world);
        int shipWay = world.Rivers;
        int alongTheLine = Mathf.CeilToInt(Vector2.Distance(from, to)) / 2 + 1;
        Assert.True(shipWay > 0);

        world.Rivers = 0;
        RoadCrossing probe = DvergrViaduct.ValleyCrossing(from, to, world, out _, out DvergrViaduct.GradeDeck? grade)!;
        DvergrViaduct.Price(grade!, RoadConstants.DefaultViaductCostFixed, RoadConstants.DefaultViaductCostPerMeter, RoadConstants.DefaultViaductEndStairCostPerMeter);
        Assert.NotNull(probe);
        Assert.Equal(alongTheLine + shipWay, world.Rivers);
    }
}
