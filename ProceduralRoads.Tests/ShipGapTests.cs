using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The navigation gap: only a river the world generator laid gets one, within the river's part of the crossing and
/// over its deepest water there (not the deepest water the whole crossing measured), as wide as a longship's way at the
/// deck's height and none under a deck it passes under (<see cref="Ships"/>), for every kit: the wooden kit, the Dvergr
/// wood bridge (in whole bays of its grid) and the Dvergr viaduct (in modules).
/// </summary>
public sealed class ShipGapTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public ShipGapTests() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); }
    public void Dispose() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); _scope.Dispose(); }

    /// <summary>Banks at <see cref="Bank"/> for |x| &gt; <see cref="BedHalf"/>, a bed between (26 m, or <see cref="Bed"/>),
    /// the water at sea level; a river whose centreline runs along x = <see cref="RiverLine"/> (its weight one at the line,
    /// none <see cref="RiverHalf"/> out, as the game's), or none.</summary>
    private sealed class Channel : WorldGenerator
    {
        public float Bank = 34f;
        public float BedHalf = 30f;
        public Func<float, float>? Bed;
        public float? RiverLine = 6f;
        public float RiverHalf = 40f;
        public Heightmap.Biome Biome = Heightmap.Biome.Meadows;
        public override float GetHeight(float wx, float wy) => Mathf.Abs(wx) <= BedHalf ? Bed?.Invoke(wx) ?? 26f : Bank;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Biome;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = RiverLine is float line ? Mathf.Max(0f, 1f - Mathf.Abs(wx - line) / RiverHalf) : 0f;
            width = RiverLine.HasValue ? 2f * RiverHalf : 0f;
        }
        public override int GetSeed() => 76;
    }

    /// <summary>Bank to bank across the channel, its measured fairway (the deepest water) 15 m left of the middle, 16 m
    /// out from the near bank: 21 m short of the river's centreline at x = 6 (37 m out).</summary>
    private static RoadCrossing Crossing() =>
        RoadCrossing.Between(new Vector2(-31f, 0f), new Vector2(31f, 0f), 26f, new Vector2(-15f, 0f), 10f, CrossingKind.Bridge);

    private const float RiverAlong = 37f;

    private static float Along(RoadCrossing c, StructurePiece p) => c.Along(new Vector2(p.Position.x, p.Position.z));

    private (RoadCrossing c, Channel w) Make(Channel w)
    {
        _scope.WithWorld(w);
        return (Crossing(), w);
    }

    // ------------------------------------------------------------- the owner

    [Fact]
    public void TheGapIsTheLongshipsSolidWidthAtTheDecksHeightAndRoomEachSide()
    {
        float Hull = Ships.Longship.Hull + 2f * Ships.PassingMargin, Yard = 9.2f + 2f * Ships.PassingMargin;
        Assert.Equal(9.8f, Hull, 4);
        Assert.Equal(Hull, Ships.NarrowestWay, 4);
        // Heights over the water, the deck's underside and walking surface; the deck's gap and the way between the piers:
        // a low deck meets the hull ...
        Assert.Equal((Hull, Hull), Ships.NavigationGapWidth(1f, 2f));
        // ... under the yard the mast meets it, and the yard sweeps over it: the hull between the piers is the widest ...
        Assert.Equal((Hull, Hull), Ships.NavigationGapWidth(5f, 7.9f));
        // ... a deck at the yard's height (8-9 m) meets the yard, and nothing stands in its way below ...
        Assert.Equal((Yard, Yard), Ships.NavigationGapWidth(7.5f, 8.5f));
        Assert.Equal((Yard, Yard), Ships.NavigationGapWidth(8.5f, 9.5f));
        // ... over it only the mast's top ...
        Assert.Equal((Hull, Hull), Ships.NavigationGapWidth(9.5f, 10.5f));
        // ... and a deck over the ship needs no gap, but its piers still stand clear of the hull's way.
        Assert.Equal((0f, Hull), Ships.NavigationGapWidth(Ships.Longship.Top, Ships.Longship.Top + 1f));
        // The viaduct's in whole modules, an odd number of them: 10 m under the yard, 14 m at it.
        Assert.Equal(10f, DvergrViaduct.NavigationGap(Hull), 4);
        Assert.Equal(14f, DvergrViaduct.NavigationGap(Yard), 4);
        // The wooden kit keeps its clearance each side of the ship's way.
        var (c, w) = Make(new Channel());
        BridgeLayout.ShipWay way = BridgeLayout.NavigationGap(c, w)!.Value;
        Assert.Equal(Hull, way.DeckWidth, 4);
        Assert.Equal(Hull, way.PierWidth, 4);
        float edge = way.DeckWidth * 0.5f + BridgeLayout.FairwayClearance;
        Assert.True(BridgeLayout.InNavigationGap(c, w, RiverAlong + edge - 0.01f));
        Assert.True(BridgeLayout.InNavigationGap(c, w, RiverAlong - edge + 0.01f));
        Assert.False(BridgeLayout.InNavigationGap(c, w, RiverAlong + edge + 0.01f));
        Assert.False(BridgeLayout.InNavigationGap(c, w, RiverAlong - edge - 0.01f));
    }

    [Fact]
    public void ADeckClearsAShipOnlyOverItsTop()
    {
        Assert.Equal(11f, Ships.Longship.Top, 4);
        Assert.True(Ships.Clears(Ships.Longship.Top, Ships.Longship));
        Assert.False(Ships.Clears(Ships.Longship.Top - 0.1f, Ships.Longship));
    }

    // ---------------------------------------------------------- over a river

    [Fact]
    public void OverAnEvenRiverBedTheGapIsOnItsCentrelineNotTheCrossingsDeepestWater()
    {
        var (c, w) = Make(new Channel());
        Assert.Equal(16f, c.Along(c.FairwayCenter), 3);
        BridgeLayout.ShipWay? river = BridgeLayout.NavigationGap(c, w);
        Assert.True(river.HasValue);
        Assert.Equal(RiverAlong, river!.Value.Centre, 1);
        Assert.False(BridgeLayout.InNavigationGap(c, w, c.Along(c.FairwayCenter)));
        Assert.False(BridgeLayout.IsFreeSpan(c, w));
        // The wooden kit: nothing in the gap, ruined or not; the complete bridge fills it.
        for (int s = 1; s <= 10; s++)
            Assert.DoesNotContain(BridgeStructures.Solve(c, w, s), p => BridgeLayout.InNavigationGap(river, Along(c, p)));
        Assert.Contains(BridgeStructures.SolveComplete(c, w, 1), p => p.Kind == StructurePieceKind.Deck && Mathf.Abs(Along(c, p) - RiverAlong) < 2f);
    }

    [Fact]
    public void OverARiverTheDvergrWoodBridgeOpensWholeBaysOverTheShipsWay()
    {
        var (c, w) = Make(new Channel { Biome = Heightmap.Biome.Mistlands });
        Assert.Equal(BridgeStructures.Kit.DvergrWood, BridgeStructures.KitFor(c, w));
        Assert.NotNull(DvergrWoodBridge.NavigationBays(c, w));
        for (int s = 1; s <= 10; s++)
            Assert.DoesNotContain(DvergrWoodBridge.Solve(c, w, s), p => p.Kind != StructurePieceKind.Debris && DvergrWoodBridge.InNavigationGap(c, w, Along(c, p)));
        Assert.True(DvergrWoodBridge.InNavigationGap(c, w, RiverAlong));
        Assert.Contains(DvergrWoodBridge.Solve(c, w, 1), p => p.Prefab == DvergrWoodBridge.Pile && Mathf.Abs(Along(c, p) - c.Along(c.FairwayCenter)) < DvergrWoodBridge.BentSpacing * 0.5f);
    }

    /// <summary>A viaduct over a river, its deck <paramref name="bank"/> high (the water at 30 m) over a bed at <paramref name="bed"/>.</summary>
    private (RoadCrossing c, Channel w) Viaduct(float bank, float bed = 20f) =>
        Make(new Channel { Bank = bank, Bed = _ => bed, Biome = Heightmap.Biome.Mistlands });

    [Theory]
    [InlineData(38f, 14f)]   // its iron floors under the yard, its parapet arches at it: the yard's, 13.2 m, in 7 modules
    [InlineData(40f, 14f)]   // its floors at the yard's height: the yard's, in 7
    public void OverARiverTheViaductsGapIsTheShipsWayAtItsDeckInModules(float bank, float open)
    {
        var (c, w) = Viaduct(bank);
        Assert.Equal(BridgeStructures.Kit.DvergrViaduct, BridgeStructures.KitFor(c, w));
        BridgeLayout.ShipWay way = DvergrViaduct.ShipWayUnder(c, w)!.Value;
        Assert.Equal(open, DvergrViaduct.NavigationGap(way.DeckWidth), 3);
        Assert.Equal(RiverAlong, way.Centre, 1);
        List<StructurePiece> complete = DvergrViaduct.SolveComplete(c, w, 1);
        var floors = complete.Where(p => p.Prefab == DvergrViaduct.Floor).Select(p => Along(c, p)).ToList();
        float nearEdge = floors.Where(a => a < RiverAlong).Max() + 1f, farEdge = floors.Where(a => a > RiverAlong).Min() - 1f;
        Assert.Equal(open, farEdge - nearEdge, 1);
        Assert.Equal(RiverAlong, (nearEdge + farEdge) * 0.5f, 1);
    }

    [Fact]
    public void ADeckOverTheShipIsWholeButNoPierStandsInTheHullsWay()
    {
        // The viaduct's iron floors 11 m and more over the water: no navigation bay in its deck, which is whole, but the
        // bay's piers (+- 16 m), so no pier piece under the ship's top reaches the hull's 9.8 m way.
        var (c, w) = Viaduct(43f, bed: 24f);
        Assert.Equal(BridgeStructures.Kit.DvergrViaduct, BridgeStructures.KitFor(c, w));
        Assert.True(DvergrViaduct.DeckHeight(c, w) - DvergrViaduct.FloorDrop - c.WaterLevel >= Ships.Longship.Top);
        Assert.False(DvergrViaduct.HasNavigationBay(c, w));
        BridgeLayout.ShipWay way = DvergrViaduct.ShipWayUnder(c, w)!.Value;
        Assert.Equal(0f, way.DeckWidth);
        Assert.Equal(Ships.NarrowestWay, way.PierWidth, 3);
        Assert.Contains(DvergrViaduct.PierAlongs(c, w), a => Mathf.Abs(a - (way.Centre - DvergrViaduct.NavigationBay * 0.5f)) < 1e-3f);
        Assert.DoesNotContain(DvergrViaduct.SolveComplete(c, w, 1), p => p.Kind == StructurePieceKind.ViaductPier
            && Mathf.Abs(Along(c, p) - way.Centre) < way.PierWidth * 0.5f && p.Position.y < c.WaterLevel + Ships.Longship.Top);
        var floors = DvergrViaduct.SolveComplete(c, w, 1).Where(p => p.Prefab == DvergrViaduct.Floor)
            .Select(p => Mathf.Round(Along(c, p) * 2f) / 2f).Distinct().OrderBy(a => a).ToList();
        for (int i = 1; i < floors.Count; i++)
            Assert.Equal(DvergrViaduct.Module, floors[i] - floors[i - 1], 3);

        // A railed wooden bridge as high: a whole deck, no column in the way, built or repaired.
        var (wc, ww) = Make(new Channel { Bank = 42f });
        BridgeLayout.ShipWay wood = BridgeLayout.NavigationGap(wc, ww)!.Value;
        Assert.Equal(0f, wood.DeckWidth);
        Assert.Equal(Ships.NarrowestWay, wood.PierWidth, 3);
        Assert.False(BridgeLayout.InNavigationGap(wc, ww, wood.Centre));
        var complete = BridgeStructures.SolveComplete(wc, ww, 1);
        Assert.DoesNotContain(complete, p => p.Prefab == BridgeLayout.PostPrefab && p.Kind == StructurePieceKind.Post && BridgeLayout.InPierOpening(wood, Along(wc, p)));
        Assert.Contains(complete, p => p.Kind == StructurePieceKind.Deck && Mathf.Abs(Along(wc, p) - wood.Centre) < 1.5f);
        for (int s = 1; s <= 10; s++)
            Assert.DoesNotContain(BridgeStructures.Solve(wc, ww, s), p => p.Prefab == BridgeLayout.PostPrefab && p.Kind == StructurePieceKind.Post && BridgeLayout.InPierOpening(wood, Along(wc, p)));

        // The Dvergr wood bridge as high: whole bays of its grid without bents, their deck carried over.
        var (dc, dw) = Make(new Channel { Bank = 43.5f, Biome = Heightmap.Biome.Mistlands }); // its stringers hang 1.3 m under the planks
        var (first, count, deckOpen) = DvergrWoodBridge.NavigationBays(dc, dw)!.Value;
        Assert.False(deckOpen);
        float[] bents = DvergrWoodBridge.BentAlongs(BridgeLayout.BuiltLength(dc.Width));
        float bay = bents[1] - bents[0];
        Assert.True(count * bay - DvergrWoodBridge.PileWidth >= Ships.NarrowestWay + 2f * BridgeLayout.FairwayClearance);
        Assert.True((count - 1) * bay - DvergrWoodBridge.PileWidth < Ships.NarrowestWay + 2f * BridgeLayout.FairwayClearance);
        foreach (var pieces in new[] { DvergrWoodBridge.SolveComplete(dc, dw, 1), DvergrWoodBridge.Solve(dc, dw, 1) })
        {
            Assert.DoesNotContain(pieces, p => p.Prefab == DvergrWoodBridge.Pile && Along(dc, p) > bents[first] + 0.01f && Along(dc, p) < bents[first + count] - 0.01f);
            Assert.Contains(pieces, p => p.Kind == StructurePieceKind.Beam && Along(dc, p) > bents[first] && Along(dc, p) < bents[first + count]);
        }
        int planks = DvergrWoodBridge.SolveComplete(dc, dw, 1)
            .Count(p => p.Kind == StructurePieceKind.Deck && Along(dc, p) > bents[first] && Along(dc, p) < bents[first + count]);
        Assert.InRange(planks, Mathf.FloorToInt(count * bay) - 1, Mathf.CeilToInt(count * bay));
        Assert.False(DvergrWoodBridge.InNavigationGap(dc, dw, bents[first] + count * bay * 0.5f));
    }

    [Fact]
    public void WithinTheRiverTheGapSlidesOverItsDeepestWater()
    {
        // The river's part of the crossing is x -14..26 (along 17..57). Over its centreline the bed shoals to 1.5 m of water;
        // from x = 4 on it is 8 m deep, inside the river part and past it (x 26..30), where no gap may go.
        var (c, w) = Make(new Channel { Bed = x => x >= 4f ? 22f : x >= -6f && x <= 2f ? 28.5f : 26f });
        BridgeLayout.ShipWay? river = BridgeLayout.NavigationGap(c, w);
        Assert.True(river.HasValue);
        float mid = river!.Value.Centre, half = river.Value.PierWidth * 0.5f;
        float Depth(float along) => c.WaterLevel - BiomeBlendedHeight.GetBlendedHeight(c.FromBank.x + along, 0f, w);
        // The ship's way lies wholly in the river's part, over the deep water: off the centreline and its shoal ...
        Assert.InRange(mid + half, 0f, 57f);
        for (float a = mid - half; a <= mid + half; a += 0.5f)
            Assert.True(Depth(a) > 7.5f, $"{Depth(a):F1} m of water at {a:F1} m along, gap at {mid:F1}");
        Assert.False(BridgeLayout.InNavigationGap(river, RiverAlong - 8f));
        // ... and, of the windows as deep, the one nearest the centreline.
        Assert.InRange(mid, RiverAlong - 2.5f + half, RiverAlong + 2f + half);
        // Every kit leaves it there.
        for (int s = 1; s <= 5; s++)
            Assert.DoesNotContain(BridgeStructures.Solve(c, w, s), p => BridgeLayout.InNavigationGap(river, Along(c, p)));
    }

    /// <summary>
    /// The ship's way measures a structure up to its top over the gap, every kit alike (BridgeStructures.TopOverDeck): a
    /// railed deck 7-8 m over the water, under the yard (8-9 m), has its handrail at the yard's height, so its gap holds the
    /// yard and the passing margin each side, not the hull's way the deck alone would ask; the open bays hold it.
    /// </summary>
    [Fact]
    public void AWoodDeckUnderTheYardOpensForTheYardItsRailingMeets()
    {
        float yard = 9.2f + 2f * Ships.PassingMargin;
        var w = new Channel();
        _scope.WithWorld(w);
        RoadCrossing c = Crossing();
        float over = float.NaN;
        for (w.Bank = 34f; w.Bank < 45f; w.Bank += 0.25f)
        {
            over = BridgeLayout.DeckHeight(c, w) - c.WaterLevel;
            if (over >= 7f && over < 8f) break;
        }
        Assert.True(over >= 7f && over < 8f, $"no bank puts the deck 7-8 m over the water ({over:F2})");
        // The deck alone, under the yard, would ask only the hull's way.
        Assert.Equal(Ships.NarrowestWay, Ships.NavigationGapWidth(over - BridgeLayout.DeckDepth, over).deck, 3);
        BridgeLayout.ShipWay way = BridgeLayout.NavigationGap(c, w)!.Value;
        Assert.Equal(yard, way.DeckWidth, 3);
        foreach (BridgeLayout.RailedKit kit in new[] { BridgeLayout.Rail, BridgeLayout.Braced })
        {
            var (_, count, deckOpen) = BridgeLayout.RailedNavigationBays(c, w, kit)!.Value;
            Assert.True(deckOpen);
            Assert.True(count * BridgeLayout.BayLength(kit) - BridgeLayout.PoleWidth >= yard, $"{kit.Name}: {count} bays");
        }
        // Every kit measures its own top: the stone kit's railing, the Dvergr wood kit's piles, the viaduct's parapet.
        Assert.Equal(BridgeLayout.RailHeight + BridgeLayout.RailHalfDepth, BridgeStructures.TopOverDeck(BridgeStructures.Kit.WoodBraced), 4);
        Assert.Equal(1f, BridgeStructures.TopOverDeck(BridgeStructures.Kit.Stone), 4);
        Assert.True(BridgeStructures.TopOverDeck(BridgeStructures.Kit.DvergrWood) > DvergrWoodBridge.PileAboveDeck);
        Assert.Equal(2f, BridgeStructures.TopOverDeck(BridgeStructures.Kit.DvergrViaduct), 4);
    }

    [Fact]
    public void ARiverNarrowerThanTheShipsWayCentresTheGapOnIt()
    {
        // A deck at the yard's height (a 13.2 m way); weight past 0.5 for 6 m either side of x = 6 (along 31..43: wider than
        // the hull's way, narrower than the yard's); the deepest water is 12 m off it, outside the river.
        var (c, w) = Make(new Channel { Bank = 38.5f, RiverHalf = 14f, Bed = x => x >= 18f ? 20f : 26f });
        BridgeLayout.ShipWay? river = BridgeLayout.NavigationGap(c, w);
        Assert.True(river.HasValue);
        Assert.Equal(13.2f, river!.Value.DeckWidth, 3);
        Assert.Equal(RiverAlong, river.Value.Centre, 1);
    }

    /// <summary>Shaped like a 95 m Mistlands river bridge whose ruin left 25 m between piles: a crossing 95.2 m bank to
    /// bank, the river's centreline 61.6 m out, a low deck (the hull's 9.8 m way).</summary>
    [Fact]
    public void TheDvergrWoodBridgeOpensTheFewestWholeBaysThatHoldTheShipsWay()
    {
        var w = new Channel { BedHalf = 46f, RiverLine = 14f, Biome = Heightmap.Biome.Mistlands };
        _scope.WithWorld(w);
        RoadCrossing c = RoadCrossing.Between(new Vector2(-47.6f, 0f), new Vector2(47.6f, 0f), 26f, new Vector2(-30f, 0f), 60f, CrossingKind.Bridge);
        Assert.Equal(BridgeStructures.Kit.DvergrWood, BridgeStructures.KitFor(c, w));
        Assert.False(BridgeLayout.IsFreeSpan(c, w));
        float deck = DvergrWoodBridge.DeckHeight(c, w);
        BridgeLayout.ShipWay way = BridgeLayout.NavigationChannel(c, w, deck - DvergrWoodBridge.DeckDepth, deck, BridgeStructures.Kit.DvergrWood)!.Value;
        Assert.Equal(61.6f, way.Centre, 1);
        Assert.Equal(9.8f, way.DeckWidth, 3);
        // The even grid, untouched: 19 bays of 4.95 m.
        float length = BridgeLayout.BuiltLength(c.Width);
        float[] bents = DvergrWoodBridge.BentAlongs(length);
        float bay = bents[1] - bents[0];
        for (int i = 1; i < bents.Length; i++) Assert.Equal(bay, bents[i] - bents[i - 1], 3);
        var (first, count, deckOpen) = DvergrWoodBridge.NavigationBays(c, w)!.Value;
        Assert.True(deckOpen);
        // What reaches past a bent toward the gap: half a pile, or a bay's 6 m stringer centred on its bay.
        float reach = Mathf.Max(DvergrWoodBridge.PileWidth * 0.5f, (DvergrWoodBridge.BeamLength - bay) * 0.5f);
        float need = way.PierWidth + 2f * BridgeLayout.FairwayClearance;
        Assert.True(count * bay - 2f * reach >= need, $"{count} bays open {count * bay - 2f * reach:F2} m, {need:F2} needed");
        Assert.True((count - 1) * bay - 2f * reach < need, $"{count - 1} bays would do");
        Assert.Equal(3, count);
        // As near the ship's way as whole bays go.
        Assert.True(Mathf.Abs(bents[first] + count * bay * 0.5f - way.Centre) <= bay * 0.5f + 1e-3f);
        float Half(StructurePiece p) => p.Kind switch
        {
            StructurePieceKind.Post => DvergrWoodBridge.PileWidth * 0.5f,
            StructurePieceKind.Deck => DvergrWoodBridge.BeamWidth * 0.5f,
            StructurePieceKind.Beam => DvergrWoodBridge.BeamLength * 0.5f,
            _ => 0f,
        };
        float mid = (bents[first] + bents[first + count]) * 0.5f;
        for (int s = 1; s <= 10; s++)
        {
            var pieces = DvergrWoodBridge.Solve(c, w, s).Where(p => p.Kind != StructurePieceKind.Debris).ToList();
            // Every pile on the grid ...
            Assert.All(pieces.Where(p => p.Prefab == DvergrWoodBridge.Pile && Along(c, p) > -0.01f && Along(c, p) < length + 0.01f),
                p => Assert.Contains(bents, b => Mathf.Abs(b - Along(c, p)) < 1e-2f));
            // ... and the open stretch is the opened bays' and no more.
            float near = pieces.Where(p => Along(c, p) < mid).Max(p => Along(c, p) + Half(p));
            float far = pieces.Where(p => Along(c, p) > mid).Min(p => Along(c, p) - Half(p));
            Assert.Equal(bents[first] + reach, near, 2);
            Assert.Equal(bents[first + count] - reach, far, 2);
        }
    }

    /// <summary>Shaped like a 66 m Mistlands bridge over a harbour's water whose first 21 m of deck was left open: the
    /// river weight passes the river line over the near bank (peaking 10 m out, over dry ground) and only the edge of the
    /// deep water, narrower than a ship's way; the water a ship sails is not river.</summary>
    [Fact]
    public void ARiversWeightOverABankOpensNoGap()
    {
        float[] t = { 0f, 4f, 8f, 12f, 16f, 20f, 24f, 30f, 40f, 50f, 60f, 66f };
        float[] h = { 39.5f, 36f, 32.9f, 30.5f, 28.2f, 25f, 23.5f, 22.3f, 22.3f, 25.3f, 34f, 39.5f };
        float Ground(float along)
        {
            int k = 1;
            while (k < t.Length - 1 && along > t[k]) k++;
            return h[k - 1] + (h[k] - h[k - 1]) * Mathf.Clamp01((along - t[k - 1]) / (t[k] - t[k - 1]));
        }
        const float half = 32.95f;
        var w = new Channel { BedHalf = 60f, Bed = x => Ground(x + half), RiverLine = 10f - half, RiverHalf = 24f, Biome = Heightmap.Biome.Mistlands };
        _scope.WithWorld(w);
        RoadCrossing c = RoadCrossing.Between(new Vector2(-half, 0f), new Vector2(half, 0f), 22.3f, new Vector2(35f - half, 0f), 40f, CrossingKind.Bridge);
        Assert.Null(BridgeLayout.NavigationGap(c, w));
        Assert.Null(DvergrWoodBridge.NavigationBays(c, w));
        float length = BridgeLayout.BuiltLength(c.Width);
        for (int s = 1; s <= 10; s++)
        {
            var pieces = DvergrWoodBridge.Solve(c, w, s);
            Assert.Equal(DvergrWoodBridge.SolveComplete(c, w, s).Count(p => p.Prefab == DvergrWoodBridge.Pile), pieces.Count(p => p.Prefab == DvergrWoodBridge.Pile));
            // The deck runs bank to bank: a plank missing now and then, never a bay's worth.
            var planks = pieces.Where(p => p.Kind == StructurePieceKind.Deck).Select(p => Along(c, p)).OrderBy(a => a).ToList();
            Assert.True(planks.First() < 1f && planks.Last() > length - 1f, $"seed {s}: deck {planks.First():F1}..{planks.Last():F1} of {length:F1} m");
            for (int i = 1; i < planks.Count; i++)
                Assert.True(planks[i] - planks[i - 1] < DvergrWoodBridge.BentSpacing, $"seed {s}: no deck {planks[i - 1]:F1}..{planks[i]:F1}");
        }
    }

    // ------------------------------------------------------------ over a lake

    [Fact]
    public void OverALakeTheWoodenKitHasNoGap()
    {
        var (c, w) = Make(new Channel { RiverLine = null });
        Assert.True(c.FairwayWidth > 0f);
        Assert.Null(BridgeLayout.NavigationGap(c, w));
        Assert.False(BridgeLayout.InNavigationGap(c, w, c.Along(c.FairwayCenter)));
        Assert.False(BridgeLayout.IsFreeSpan(c, w));
        // Every station over the deep water stands in the complete layout's place in some ruin: nothing is kept open.
        var bays = Enumerable.Range(1, 40).SelectMany(s => BridgeStructures.Solve(c, w, s))
            .Where(p => p.Kind == StructurePieceKind.Post).Select(p => Mathf.Round(Along(c, p))).Distinct().ToList();
        Assert.Contains(bays, a => Mathf.Abs(a - c.Along(c.FairwayCenter)) <= 1f);
        Assert.Contains(bays, a => Mathf.Abs(a - RiverAlong) <= 1f);
    }

    [Fact]
    public void OverALakeTheDvergrWoodBridgeStandsEveryBent()
    {
        var (c, w) = Make(new Channel { RiverLine = null, Biome = Heightmap.Biome.Mistlands });
        Assert.Equal(BridgeStructures.Kit.DvergrWood, BridgeStructures.KitFor(c, w));
        int Piles(List<StructurePiece> ps) => ps.Count(p => p.Prefab == DvergrWoodBridge.Pile);
        Assert.Equal(Piles(DvergrWoodBridge.SolveComplete(c, w, 1)), Piles(DvergrWoodBridge.Solve(c, w, 1)));
    }

    [Fact]
    public void OverALakeTheViaductsDeckRunsBankToBank()
    {
        var (c, w) = Make(new Channel { RiverLine = null, Bank = 45f, Biome = Heightmap.Biome.Mistlands });
        Assert.Equal(BridgeStructures.Kit.DvergrViaduct, BridgeStructures.KitFor(c, w));
        Assert.False(DvergrViaduct.HasNavigationBay(c, w));
        Assert.Contains(c.Width * 0.5f, DvergrViaduct.PierAlongs(c, w));
        var floors = DvergrViaduct.SolveComplete(c, w, 1).Where(p => p.Prefab == DvergrViaduct.Floor)
            .Select(p => Mathf.Round(Along(c, p) * 2f) / 2f).Distinct().OrderBy(a => a).ToList();
        for (int i = 1; i < floors.Count; i++)
            Assert.Equal(DvergrViaduct.Module, floors[i] - floors[i - 1], 3);
    }
}
