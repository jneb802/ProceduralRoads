using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Mistlands bridges in the style of the vanilla Dvergr viaduct: piers
/// 16 m apart from the midpoint (the navigation bay first on a known river), the wooden kit under 6 m or over the pier
/// ceiling, and the wooden kit's ruin rule. The search's side -- a viaduct in place of a staircase down into a valley
/// and another out of it -- is DvergrViaductRoadTests. What only a game run can say -- whether the
/// game's structural support holds a pier that tall, and how it looks -- is not asserted here: these tests check the
/// geometry by construction (every piece touches the ground or a piece that does), not WearNTear's arithmetic.
/// </summary>
public sealed class DvergrViaductTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    /// <summary>A valley along z through <see cref="Cx"/>: flats at <see cref="Top"/>, a floor
    /// <see cref="Depth"/> below them <see cref="FloorHalf"/> either side of the middle, faces of
    /// <see cref="Face"/> between. Sea past |x - Cx|, |z| = 200, so there is no way round it.</summary>
    internal sealed class MistValley : WorldGenerator
    {
        public float Cx, Top = 60f, Depth = 14f, FloorHalf = 8f, Face = 1.2f;
        public Heightmap.Biome Biome = Heightmap.Biome.Mistlands;
        public override float GetHeight(float wx, float wy)
        {
            float x = wx - Cx;
            if (Mathf.Abs(x) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            return Top - Mathf.Clamp(Depth - (Mathf.Abs(x) - FloorHalf) * Face, 0f, Depth);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Biome;
        public override int GetSeed() => 76;
    }

    /// <summary>A sailable river in the Mistlands: banks at <see cref="Bank"/> for |x| &gt; 30, a bed at
    /// <see cref="Bed"/> between, the water at sea level.</summary>
    internal sealed class MistRiver : WorldGenerator
    {
        public float Bank = 45f, Bed = 26f;
        /// <summary>Half the channel's width.</summary>
        public float Half = 30f;
        /// <summary>How fast the ground falls away west of the near bank (x = -31), to 6 m under it.</summary>
        public float NearFall;
        public Heightmap.Biome Biome = Heightmap.Biome.Mistlands;
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) <= Half ? Bed : wx < -Half - 1f ? Bank - Mathf.Min(NearFall * (-Half - 1f - wx), 6f) : Bank;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Biome;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = Mathf.Abs(wx) < Half ? 1f : 0f;
            width = 2f * Half;
        }
        public override int GetSeed() => 76;
    }

    /// <summary>A Mistlands river in a gorge: the near wall a step up to <see cref="Bank"/> at x = -30, as MistRiver's; the
    /// bed at <see cref="Bed"/> to the far wall's foot at x = 22, the far wall a slope of 2 up to its rim at x = 31; past the
    /// rim the ground falls <see cref="FarFall"/> a metre, to 3 m under it. The river's weight peaks on x =
    /// <see cref="Line"/>, so the fairway lies over it and the navigation bay is off the gorge's middle.</summary>
    internal sealed class MistGorge : WorldGenerator
    {
        public float Bank = 38f, Bed = 20f, Line = 14f, FarFall;
        public const float WallFoot = 22f, Rim = 31f;
        public override float GetHeight(float wx, float wy) =>
            wx < -30f ? Bank
            : wx <= WallFoot ? Bed
            : wx <= Rim ? Bed + (wx - WallFoot) * (Bank - Bed) / (Rim - WallFoot)
            : Bank - Mathf.Min(FarFall * (wx - Rim), 3f);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = Mathf.Max(0f, 1f - Mathf.Abs(wx - Line) / 60f);
            width = 120f;
        }
        public override int GetSeed() => 76;
    }

    /// <summary>A wide river (<see cref="WideRiverCrossing"/>): banks at 47 m, the bed at 25 m, 90 m across.</summary>
    internal static MistRiver WideRiver() => new() { Bank = 47f, Bed = 25f, Half = 45f };

    /// <summary>Bank to bank across <see cref="WideRiver"/>: 92 m.</summary>
    internal static RoadCrossing WideRiverCrossing() =>
        RoadCrossing.Between(new Vector2(-46f, 0f), new Vector2(46f, 0f), 25f, new Vector2(0f, 0f), 20f, CrossingKind.Bridge);

    /// <summary>Rim to rim across the gorge: 62 m, as <see cref="River"/>.</summary>
    internal static RoadCrossing Gorge() =>
        RoadCrossing.Between(new Vector2(-31f, 0f), new Vector2(MistGorge.Rim, 0f), 20f, new Vector2(0f, 0f), 20f, CrossingKind.Bridge);

    public DvergrViaductTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
    }

    private readonly float _raisedShare = DvergrViaduct.RaisedShare;

    public void Dispose()
    {
        DvergrViaduct.RaisedShare = _raisedShare;
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    internal static RoadCrossing River(float bank = 45f) =>
        // Banks on the 45 m ground either side of a 60 m channel; the fairway 20 m wide, centred 25 m out from the near bank.
        RoadCrossing.Between(new Vector2(-31f, 0f), new Vector2(31f, 0f), 26f, new Vector2(-6f, 0f), 20f, CrossingKind.Bridge);

    internal static float Along(RoadCrossing c, StructurePiece p) => c.Along(new Vector2(p.Position.x, p.Position.z));

    internal static bool IsDvergr(StructurePiece p) => p.Prefab.StartsWith("blackmarble_", StringComparison.Ordinal) || p.Prefab.StartsWith("iron_", StringComparison.Ordinal)
        || p.Prefab == DvergrViaduct.Vine;   // the vines it hangs, as the vanilla viaduct's

    // ---------------------------------------------------------------- piers

    [Fact]
    public void AShortCrossingOffARiverGetsOneMiddlePier()
    {
        Assert.Equal(new[] { 8f }, DvergrViaduct.PierAlongs(16f, null));
        Assert.Equal(new[] { 12f }, DvergrViaduct.PierAlongs(24f, null));
        // The narrowest valley a road jumps (two cells): one pier, in the middle.
        var notch = new MistValley { FloorHalf = 2f, Face = 3f };
        _scope.WithWorld(notch);
        RoadCrossing? c = DvergrViaduct.ValleyCrossing(new Vector2(-8f, 0f), new Vector2(8f, 0f), notch, out string why);
        Assert.True(c != null, why);
        Assert.Equal(new[] { 8f }, DvergrViaduct.PierAlongs(c!, notch));
    }

    [Fact]
    public void ALongCrossingHasPiersEvery16MetresFromTheMiddle()
    {
        List<float> piers = DvergrViaduct.PierAlongs(80f, null);
        Assert.Equal(new[] { 8f, 24f, 40f, 56f, 72f }, piers);
        // Not one outside the gap: each at least the bank margin in from either bank.
        foreach (float width in new[] { 33f, 47f, 64f, 95f })
        {
            List<float> p = DvergrViaduct.PierAlongs(width, null);
            Assert.Contains(width * 0.5f, p);
            Assert.All(p, a => Assert.InRange(a, DvergrViaduct.PierBankMargin - 1e-3f, width - DvergrViaduct.PierBankMargin + 1e-3f));
            for (int i = 1; i < p.Count; i++) Assert.Equal(DvergrViaduct.PierSpacing, p[i] - p[i - 1], 3);
        }
    }

    [Fact]
    public void OnARiverTheNavigationBayComesFirst()
    {
        // Its iron floors 6 m over the water, under a longship's yard: a navigation bay. (Its default 45 m banks stand the
        // floors over the ship, which needs none: ShipGapTests.ADeckOverTheShipLeavesNoGapInAnyKit.)
        var world = new MistRiver { Bank = 38f, Bed = 20f };
        _scope.WithWorld(world);
        RoadCrossing c = River();
        Assert.True(DvergrViaduct.HasNavigationBay(c, world));
        // The bay is centred on the river (x 0, 31 m out), not on the fairway the crossing measured (25 m out).
        Assert.Equal(25f, c.Along(c.FairwayCenter), 3);
        float f = DvergrViaduct.FairwayAlong(c, world)!.Value;
        Assert.Equal(31f, f, 3);
        // The bay's two piers at the river +- 16 m (two vanilla bays), then every 16 m outward while inside
        // the gap (-1 m and 63 m are not).
        Assert.Equal(new[] { 15f, 47f }, DvergrViaduct.PierAlongs(c, world));
        Assert.True(DvergrViaduct.Applies(c, world), DvergrViaduct.Refusal(c, world));

        // The deck is missing over the river: at least a longship's way at the structure's heights (Ships.NavigationGapWidth,
        // its parapet arches at the yard's: 13.2 m), in whole modules centred on it (14 m), and stands 9 m out from each bay
        // pier.
        float way = DvergrViaduct.ShipWayUnder(c, world)!.Value.DeckWidth;
        Assert.Equal(13.2f, way, 3);
        Assert.Equal(14f, DvergrViaduct.NavigationGap(way), 3);
        List<StructurePiece> complete = DvergrViaduct.SolveComplete(c, world, 1);
        var floors = complete.Where(p => p.Prefab == DvergrViaduct.Floor).Select(p => Along(c, p)).ToList();
        float nearEdge = floors.Where(a => a < f).Max() + 1f, farEdge = floors.Where(a => a > f).Min() - 1f;
        Assert.Equal(14f, farEdge - nearEdge, 3);
        Assert.Equal(f, (nearEdge + farEdge) * 0.5f, 3);
        Assert.True(farEdge - nearEdge >= way);
        Assert.Equal(9f, nearEdge - (f - 16f), 3);
        Assert.Equal(9f, (f + 16f) - farEdge, 3);
        // No pier piece in the bay between them: nothing stands in the boat's way.
        Assert.DoesNotContain(complete, p => p.Kind == StructurePieceKind.ViaductPier && Mathf.Abs(Along(c, p) - f) < 16f - 6.01f);
        // The bay's piers stand: pier pieces under both of them.
        foreach (float pier in new[] { f - 16f, f + 16f })
            Assert.Contains(complete, p => p.Kind == StructurePieceKind.ViaductPier && Mathf.Abs(Along(c, p) - pier) <= 2.01f && p.Prefab == DvergrViaduct.Footing);
        // The ruined one keeps the gap open too, and its piers.
        List<StructurePiece> built = DvergrViaduct.Solve(c, world, 1);
        Assert.DoesNotContain(built, p => p.Prefab == DvergrViaduct.Floor && Mathf.Abs(Along(c, p) - f) < 7f);
        Assert.Equal(2 * 4, built.Count(p => p.Prefab == DvergrViaduct.Footing));
    }

    [Fact]
    public void OnARiverBothBayPiersStandThoughOneIsInTheBankWall()
    {
        // The fairway 15 m short of the far rim (x 14, 45 m out): the far bay pier at 61 m stands in the far wall's slope,
        // a metre inside the rim, its shafts' feet from 32 m to the rim's 38 m. It is laid, cut to the ground under it.
        var world = new MistGorge();
        _scope.WithWorld(world);
        RoadCrossing c = Gorge();
        Assert.True(DvergrViaduct.HasNavigationBay(c, world));
        float f = DvergrViaduct.FairwayAlong(c, world)!.Value;
        Assert.Equal(45f, f, 3);
        Assert.Equal(new[] { 13f, 29f, 61f }, DvergrViaduct.PierAlongs(c, world).Select(a => Mathf.Round(a * 100f) / 100f));
        Assert.True(DvergrViaduct.Applies(c, world), DvergrViaduct.Refusal(c, world));
        AssertBothBayPiersHoldTheGap(c, world, f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABayWiderThanTheBanksStillHasItsPierInTheBank(bool reversed)
    {
        // The fairway against the gorge's east wall (its deep water ends 4.6 m short of the rim): the east bay pier falls
        // past the east bank, on the ground falling away beyond the rim. The pier stands there, cut to that ground; the deck
        // runs on over it, 7 m past it, and the end stairs go down from there, clear of its corbels. The same crossed from
        // the east, the pier then the near one.
        var world = new MistGorge { Line = 20f, FarFall = 1f };
        _scope.WithWorld(world);
        RoadCrossing c = reversed
            ? RoadCrossing.Between(new Vector2(MistGorge.Rim, 0f), new Vector2(-31f, 0f), 20f, new Vector2(0f, 0f), 20f, CrossingKind.Bridge)
            : Gorge();
        Assert.True(DvergrViaduct.HasNavigationBay(c, world));
        float f = DvergrViaduct.FairwayAlong(c, world)!.Value;
        float pier = reversed ? f - 16f : f + 16f;
        Assert.True(pier < 0f || pier > c.Width, $"the east bay pier at {pier:F1} m is inside the {c.Width:F0} m crossing");
        Assert.Contains(DvergrViaduct.PierAlongs(c, world), a => Mathf.Abs(a - pier) < 1e-3f);
        Assert.True(DvergrViaduct.Applies(c, world), DvergrViaduct.Refusal(c, world));
        AssertBothBayPiersHoldTheGap(c, world, f);
        // The deck's east end 7 m past the east bay pier, and its end stairs start there.
        var (near, far) = DvergrViaduct.Ends(c, world);
        StructureEnds.End east = reversed ? near : far;
        Assert.Equal(7f, Mathf.Abs(c.Along(east.Edge) - pier), 2);
        Assert.True(east.Steps > 0);
    }

    /// <summary>Both bay piers of a river viaduct stand, built and whole, a pier piece within 3 m of each (its shafts'
    /// reach); the deck runs over each; nothing is laid twice or into the end stairs.</summary>
    private static void AssertBothBayPiersHoldTheGap(RoadCrossing c, WorldGenerator world, float f)
    {
        List<StructurePiece> whole = DvergrViaduct.SolveComplete(c, world, 1);
        foreach (float pier in new[] { f - 16f, f + 16f })
            Assert.Contains(whole, p => p.Kind == StructurePieceKind.ViaductDeck && Mathf.Abs(Along(c, p) - pier) <= 0.01f);
        foreach (var pieces in new[] { whole, DvergrViaduct.Solve(c, world, 1), DvergrViaduct.Solve(c, world, 7) })
        {
            foreach (float pier in new[] { f - 16f, f + 16f })
                Assert.Contains(pieces, p => p.Kind == StructurePieceKind.ViaductPier && Mathf.Abs(Along(c, p) - pier) <= 3.01f);
            // Nothing doubled: no two pieces of one prefab at one place.
            var twice = pieces.GroupBy(p => (p.Prefab, Mathf.RoundToInt(p.Position.x * 10f), Mathf.RoundToInt(p.Position.y * 10f), Mathf.RoundToInt(p.Position.z * 10f)))
                .Where(g => g.Count() > 1).Select(g => g.Key.Prefab).ToList();
            Assert.True(twice.Count == 0, $"laid twice: {string.Join(", ", twice)}");
            // No pier piece in an end step's or its column's room.
            var stairs = pieces.Where(p => p.Kind == StructurePieceKind.Stair || (p.Prefab == DvergrViaduct.Block && p.Kind != StructurePieceKind.ViaductPier && p.Kind != StructurePieceKind.ViaductDeck)).Select(p => new Box(p, c)).ToList();
            foreach (Box pier in pieces.Where(p => p.Kind == StructurePieceKind.ViaductPier).Select(p => new Box(p, c)))
                Assert.DoesNotContain(stairs, s => s.Overlaps(pier));
        }
    }

    [Fact]
    public void OffARiverThereIsNoGapInTheDeck()
    {
        var world = new MistValley();
        _scope.WithWorld(world);
        RoadCrossing c = DvergrViaduct.ValleyCrossing(new Vector2(-24f, 0f), new Vector2(24f, 0f), world, out string why)!;
        Assert.True(c != null, why);
        Assert.False(DvergrViaduct.HasNavigationBay(c!, world));
        // The deck's modules, floors and course: the floors that would lie wholly in the bank are left out, the course
        // over them is flush with the bank.
        var deck = DvergrViaduct.SolveComplete(c!, world, 1).Where(p => p.Kind == StructurePieceKind.ViaductDeck).Select(p => Mathf.Round(Along(c!, p) * 2f) / 2f).OrderBy(a => a).Distinct().ToList();
        for (int i = 1; i < deck.Count; i++) Assert.Equal(DvergrViaduct.Module, deck[i] - deck[i - 1], 3);
        // Bank to bank: the remainder past the outer piers goes onto the banks.
        Assert.True(deck.First() - 1f <= 0f && deck.Last() + 1f >= c!.Width, $"deck {deck.First() - 1f}..{deck.Last() + 1f} of {c.Width}");
    }

    // ----------------------------------------------------------- fallbacks

    [Fact]
    public void UnderSixMetresAWoodenKitStays()
    {
        // A Mistlands river whose deck stands 4 m over the water: a wooden bridge, of the Dvergr pier's wood since layout 8
        // (DvergrWoodBridgeTests); only its bank stairs are the wooden kits'.
        var low = new MistRiver { Bank = 34f };
        _scope.WithWorld(low);
        RoadCrossing c = River();
        Assert.False(DvergrViaduct.Applies(c, low));
        Assert.Contains("under 6", DvergrViaduct.Refusal(c, low));
        Assert.Equal(BridgeStructures.Kit.DvergrWood, BridgeStructures.KitFor(c, low));
        List<StructurePiece> pieces = BridgeStructures.Solve(c, low, 1);
        Assert.NotEmpty(pieces);
        Assert.All(pieces, p => Assert.False(IsDvergr(p), p.Prefab));
        Assert.All(pieces.Where(p => p.Kind != StructurePieceKind.Stair && p.Prefab != BridgeLayout.PostPrefab), p => Assert.StartsWith("dvergrprops_", p.Prefab));

        // The same 15 m high crossing out of the Mistlands: wood as well.
        var meadows = new MistRiver { Biome = Heightmap.Biome.Meadows };
        _scope.WithWorld(meadows);
        Assert.False(DvergrViaduct.Applies(c, meadows));
        Assert.All(BridgeStructures.Solve(c, meadows, 1), p => Assert.StartsWith("wood_", p.Prefab));

        // And from 6 m up in the Mistlands, the viaduct.
        var high = new MistRiver();
        _scope.WithWorld(high);
        Assert.All(BridgeStructures.Solve(c, high, 1), p => Assert.True(IsDvergr(p), p.Prefab));

        // A dry valley under 6 m is not jumped at all: no wooden viaduct over dry ground.
        var shallow = new MistValley { Depth = 5f };
        _scope.WithWorld(shallow);
        Assert.Null(DvergrViaduct.ValleyCrossing(new Vector2(-16f, 0f), new Vector2(16f, 0f), shallow, out string why));
        Assert.Contains("under 6", why);
    }

    [Fact]
    public void PiersOverTheCeilingAreNoViaduct()
    {
        // A valley 34 m deep: at the rim its middle pier would stand 34 m, over the 26 m ceiling: no viaduct there.
        var deep = new MistValley { Top = 80f, Depth = 34f };
        _scope.WithWorld(deep);
        Assert.Null(DvergrViaduct.ValleyCrossing(new Vector2(-32f, 0f), new Vector2(32f, 0f), deep, out string why));
        Assert.Contains("ceiling", why);

        // On a river the crossing stays a bridge of the Dvergr pier's wood.
        var deepRiver = new MistRiver { Bank = 62f, Bed = 20f };
        _scope.WithWorld(deepRiver);
        RoadCrossing c = River();
        Assert.Contains("ceiling", DvergrViaduct.Refusal(c, deepRiver));
        Assert.Equal(BridgeStructures.Kit.DvergrWood, BridgeStructures.KitFor(c, deepRiver));
        Assert.True(DvergrViaduct.PierCeiling <= 27f, "the ceiling stays under what the game was read to hold");
    }


    // ------------------------------------------------------- by construction

    public static IEnumerable<object[]> Viaducts()
    {
        yield return new object[] { "valley", 1 };
        yield return new object[] { "valley", 77 };
        yield return new object[] { "notch", 5 };
        yield return new object[] { "wide", 3 };
        yield return new object[] { "river", 1 };
        yield return new object[] { "river", 9 };
        yield return new object[] { "sloped", 2 };
        yield return new object[] { "tall", 4 };
        yield return new object[] { "gorge-wall", 1 };
        yield return new object[] { "gorge-rim", 6 };
        yield return new object[] { "wide-river", 3 };
        yield return new object[] { "wide-river", 8 };
        yield return new object[] { "valley-18", 2 };
        yield return new object[] { "raised-17", 5 };
    }

    private (RoadCrossing crossing, WorldGenerator world) Case(string name)
    {
        switch (name)
        {
            case "river":
            {
                var w = new MistRiver(); _scope.WithWorld(w); return (River(), w);
            }
            case "wide-river":
            {
                // A river viaduct laid in game (4442, -4992) to (4356.5, -4992) as near as a plain river has it: a channel 90 m
                // wide, its piers 22 m from the deck to the bed, the ship passing under the deck.
                var w = WideRiver(); _scope.WithWorld(w); return (WideRiverCrossing(), w);
            }
            case "raised-17":
            {
                // 17 m piers at grade, raised two steps (DvergrViaduct.RaisedSteps): its bays judged at grade would be 16 m and
                // fall short under the raised deck.
                var w = new DvergrViaductEndStairTests.StepValley { Top = 80f, Depth = _ => 17f, FloorHalf = 36f, Face = 2f };
                _scope.WithWorld(w);
                RoadCrossing r = DvergrViaduct.ValleyCrossing(new Vector2(-48f, 0f), new Vector2(48f, 0f), w, out string why)!;
                Assert.True(r != null, why);
                // The plain roads the suite runs (PlainRoads) raise none: this case raises as the game does.
                DvergrViaduct.RaisedShare = DvergrViaduct.DefaultRaisedShare;
                for (w.Seed = 1; w.Seed < 1000 && DvergrViaduct.DrawnSteps(r, w.Seed) != DvergrViaduct.MostRaisedSteps; w.Seed++) { }
                Assert.Equal(DvergrViaduct.MostRaisedSteps, DvergrViaduct.RaisedSteps(r, w));
                return (r, w);
            }
            case "valley-18":
            {
                // Piers 18 m tall: the tallest 16 m bays carry.
                var w = new MistValley { Depth = 18f }; _scope.WithWorld(w);
                return (DvergrViaduct.ValleyCrossing(new Vector2(-32f, 0f), new Vector2(32f, 0f), w, out _)!, w);
            }
            case "gorge-wall":
            {
                // The far bay pier in the far wall's slope (OnARiverBothBayPiersStandThoughOneIsInTheBankWall).
                var w = new MistGorge(); _scope.WithWorld(w); return (Gorge(), w);
            }
            case "gorge-rim":
            {
                // The far bay pier past the far rim (ABayWiderThanTheBanksStillHasItsPierInTheBank).
                var w = new MistGorge { Line = 20f, FarFall = 1f }; _scope.WithWorld(w); return (Gorge(), w);
            }
            case "notch":
            {
                var w = new MistValley { FloorHalf = 2f, Face = 3f }; _scope.WithWorld(w);
                return (DvergrViaduct.ValleyCrossing(new Vector2(-8f, 0f), new Vector2(8f, 0f), w, out _)!, w);
            }
            case "wide":
            {
                // 96 m of flat-floored valley 12 m deep: six bays.
                var w = new MistValley { Depth = 12f, FloorHalf = 36f, Face = 1f }; _scope.WithWorld(w);
                return (DvergrViaduct.ValleyCrossing(new Vector2(-48f, 0f), new Vector2(48f, 0f), w, out _)!, w);
            }
            case "tall":
            {
                // 21 m deep: piers taller than 16 m bays carry (DvergrViaduct.Spacing), under the ceiling.
                var w = new MistValley { Depth = 21f }; _scope.WithWorld(w);
                RoadCrossing t = DvergrViaduct.ValleyCrossing(new Vector2(-32f, 0f), new Vector2(32f, 0f), w, out string why)!;
                Assert.True(t != null, why);
                Assert.InRange(DvergrViaduct.TallestPier(t, w), 19f, DvergrViaduct.PierCeiling);
                return (t, w);
            }
            case "sloped":
            {
                // Banks 2 m apart in height: the lower one is walked off on marble steps.
                var w = new SlopedValley(); _scope.WithWorld(w);
                return (DvergrViaduct.ValleyCrossing(new Vector2(-24f, 0f), new Vector2(24f, 0f), w, out _)!, w);
            }
            default:
            {
                var w = new MistValley(); _scope.WithWorld(w);
                return (DvergrViaduct.ValleyCrossing(new Vector2(-24f, 0f), new Vector2(24f, 0f), w, out _)!, w);
            }
        }
    }

    /// <summary>The valley with its east bank 2 m lower than the west.</summary>
    private sealed class SlopedValley : WorldGenerator
    {
        private readonly MistValley _v = new();
        public override float GetHeight(float wx, float wy) => _v.GetHeight(wx, wy) - (wx > 18f ? 2f : 0f);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override int GetSeed() => 76;
    }

    private readonly struct Box
    {
        public readonly float U0, U1, V0, V1, Y0, Y1;
        public readonly StructurePiece Piece;
        public Box(StructurePiece piece, RoadCrossing c)
        {
            Piece = piece;
            Vector2 dir = c.Direction, side = new(-dir.y, dir.x);
            Vector2 rel = new Vector2(piece.Position.x, piece.Position.z) - c.FromBank;
            float u = Vector2.Dot(rel, dir), v = Vector2.Dot(rel, side);
            var (hu, hv, hy) = DvergrViaduct.Extents(piece.Prefab);
            float y0 = piece.Position.y - hy, y1 = piece.Position.y + hy;
            if (piece.Prefab == DvergrViaduct.StepPrefab) { y0 = piece.Position.y; y1 = piece.Position.y + 1f; }
            U0 = u - hu; U1 = u + hu; V0 = v - hv; V1 = v + hv; Y0 = y0; Y1 = y1;
        }
        public bool Touches(Box o, float tol = 0.05f) =>
            U0 <= o.U1 + tol && o.U0 <= U1 + tol && V0 <= o.V1 + tol && o.V0 <= V1 + tol && Y0 <= o.Y1 + tol && o.Y0 <= Y1 + tol;
        /// <summary>Whether the two share room, more than a few centimetres every way.</summary>
        public bool Overlaps(Box o) => Touches(o, -0.1f);
    }

    /// <summary>The ground under a box's footprint, at its corners, edges' middles and centre.</summary>
    private static List<float> GroundUnder(Box b, RoadCrossing c, WorldGenerator world)
    {
        Vector2 dir = c.Direction, side = new(-dir.y, dir.x);
        var g = new List<float>();
        foreach (float su in new[] { 0f, 0.5f, 1f })
            foreach (float sv in new[] { 0f, 0.5f, 1f })
            {
                Vector2 p = c.FromBank + dir * Mathf.Lerp(b.U0, b.U1, su) + side * Mathf.Lerp(b.V0, b.V1, sv);
                g.Add(BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world));
            }
        return g;
    }

    [Theory]
    [MemberData(nameof(Viaducts))]
    public void EveryPieceIsGroundedOrRestsOnASupportedPiece(string name, int seed)
    {
        var (c, world) = Case(name);
        Assert.NotNull(c);
        Assert.True(DvergrViaduct.Applies(c, world), DvergrViaduct.Refusal(c, world));
        foreach (bool ruined in new[] { true, false })
        {
            List<StructurePiece> pieces = ruined ? DvergrViaduct.Solve(c, world, seed) : DvergrViaduct.SolveComplete(c, world, seed);
            Assert.NotEmpty(pieces);
            var boxes = pieces.Select(p => new Box(p, c)).ToList();
            var supported = new bool[boxes.Count];
            var queue = new Queue<int>();
            for (int i = 0; i < boxes.Count; i++)
            {
                List<float> g = GroundUnder(boxes[i], c, world);
                // Never wholly under the ground: the game would break it.
                Assert.False(g.All(h => h >= boxes[i].Y1 + BuriedPieces.Growth), $"{boxes[i].Piece.Prefab} at {boxes[i].Piece.Position} is wholly buried");
                // Grounded: the ground's surface passes through it somewhere.
                if (g.Max() >= boxes[i].Y0 - 0.05f && g.Min() <= boxes[i].Y1 + BuriedPieces.Growth) { supported[i] = true; queue.Enqueue(i); }
            }
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                for (int j = 0; j < boxes.Count; j++)
                    if (!supported[j] && boxes[i].Touches(boxes[j])) { supported[j] = true; queue.Enqueue(j); }
            }
            var loose = Enumerable.Range(0, boxes.Count).Where(i => !supported[i]).Select(i => $"{boxes[i].Piece.Prefab} at {boxes[i].Piece.Position}").ToList();
            Assert.True(loose.Count == 0, $"{name} seed {seed} {(ruined ? "ruined" : "complete")}: {loose.Count} piece(s) touch nothing that is held: {string.Join("; ", loose.Take(8))}");
        }
    }

    /// <summary>
    /// Every piece the game's support rule (<see cref="SupportGraph"/>) holds at <see cref="DvergrViaduct.HoldMargin"/>
    /// times its least or more, whole and ruined, and the rule cost the viaduct nothing anyone walks on: no piece carrying
    /// the deck or the walk under the margin (DvergrViaduct.Hold lays those all the same and reports them), no deck piece
    /// left out of the whole viaduct, and its course walked on iron only over a navigation bay (elsewhere its bays carry
    /// it, but on the shortest bays, TightSpacing, whose middle may be). Ornaments the rule leaves hanging (an arch beside a bay pier, a corbel's tip) may be left out: they are
    /// reported. A river viaduct laid in game (piers 22 m tall, 16 m bays, a 32 m navigation bay) had its deck between piers at
    /// 100-120 against black marble's 100, and it fell.
    /// </summary>
    [Theory]
    [MemberData(nameof(Viaducts))]
    public void EveryPieceHoldsByTheGamesSupportRule(string name, int seed)
    {
        var (c, world) = Case(name);
        Assert.True(DvergrViaduct.Applies(c, world), DvergrViaduct.Refusal(c, world));
        BridgeLayout.ShipWay? way = DvergrViaduct.ShipWayUnder(c, world);
        bool InBay(float along) => way is BridgeLayout.ShipWay w && Mathf.Abs(along - w.Centre) <= DvergrViaduct.NavigationBay * 0.5f + 1e-3f;
        foreach (bool ruined in new[] { false, true })
        {
            List<StructurePiece> pieces = DvergrViaduct.Solve(c, world, seed, ruined, out DvergrViaduct.HoldReport report);
            string Where(StructurePiece p) => FormattableString.Invariant($"{p.Prefab} {Along(c, p):F0} m along, {p.Position.y:F1} high");
            string label = $"{name} seed {seed} {(ruined ? "ruined" : "complete")}";
            var weak = ViaductSupport.Of(c, world, pieces).Where(h => h.Share < DvergrViaduct.HoldMargin).ToList();
            Assert.True(weak.Count == 0, $"{label}: {weak.Count} piece(s) under {DvergrViaduct.HoldMargin} times their least: "
                + string.Join("; ", weak.OrderBy(h => h.Share).Take(6).Select(h => $"{Where(h.Piece)}: {h.Support:F0}")));
            Assert.True(report.Unheld.Count == 0, $"{label}: carrying the deck under the margin: {string.Join("; ", report.Unheld.Take(6).Select(Where))}");
            if (ruined) continue;
            var lost = report.DroppedPieces.Where(p => !DvergrViaduct.IsOrnament(p)).ToList();
            Assert.True(lost.Count == 0, $"{label}: left out of the deck: {string.Join("; ", lost.Take(6).Select(Where))}");
            List<float> piers = DvergrViaduct.PierAlongs(c, world);
            bool tight = piers.Count > 1 && Enumerable.Range(1, piers.Count - 1).Any(i => piers[i] - piers[i - 1] <= DvergrViaduct.TightSpacing + 1e-3f && !InBay((piers[i] + piers[i - 1]) * 0.5f));
            var ironOff = report.IronAlongs.Where(a => !InBay(a) && !tight).ToList();
            Assert.True(ironOff.Count == 0, $"{label}: course walked on iron off a navigation bay at {string.Join(", ", ironOff.Select(a => a.ToString("F0")))} m along");
        }
    }

    /// <summary>
    /// A way across every viaduct, whole and ruined, which the support rule never takes (DvergrViaduct.Hold leaves out
    /// only ornaments, and course in a ruin). Whole: every module that has its floors has a walking surface at the deck's
    /// height, a course block or the walk on iron. Ruined: the floors' way across (a module open side to side, never two
    /// running; the deck's gap over a river apart), as the ruin chose it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Viaducts))]
    public void TheSupportRuleNeverTakesTheWayAcross(string name, int seed)
    {
        var (c, world) = Case(name);
        float deck = DvergrViaduct.DeckHeight(c, world);
        bool Lower(StructurePiece p) => p.Prefab == DvergrViaduct.Floor && Mathf.Abs(p.Position.y - (deck - DvergrViaduct.FloorDrop)) < 0.01f;
        bool Walk(StructurePiece p) =>
            (p.Prefab == DvergrViaduct.Floor && Mathf.Abs(p.Position.y - (deck - DvergrViaduct.IronDeckDrop)) < 0.01f)
            || (p.Prefab == DvergrViaduct.Block && p.Kind == StructurePieceKind.ViaductDeck && Mathf.Abs(p.Position.y - (deck - DvergrViaduct.CourseDrop)) < 0.01f);
        List<int> Modules(IEnumerable<StructurePiece> ps, Func<StructurePiece, bool> what) =>
            ps.Where(what).Select(p => Mathf.RoundToInt(Along(c, p))).Distinct().OrderBy(a => a).ToList();
        List<StructurePiece> whole = DvergrViaduct.SolveComplete(c, world, seed);
        List<int> floored = Modules(whole, Lower), walked = Modules(whole, Walk);
        Assert.NotEmpty(floored);
        // Two lanes a module, so a module a lane, but never a module of floor with nothing on it.
        foreach (int a in floored)
            Assert.True(walked.Any(w => Mathf.Abs(w - a) <= 1), $"{name} seed {seed}: floors and nothing to walk on at {a} m along");
        List<int> ruinFloors = Modules(DvergrViaduct.Solve(c, world, seed), Lower);
        for (int i = 1; i < ruinFloors.Count; i++)
        {
            int gap = ruinFloors[i] - ruinFloors[i - 1];
            // The deck's own gap over a river (no floors whole either) is the ship's, not the ruin's.
            bool riverGap = !floored.Any(a => a > ruinFloors[i - 1] && a < ruinFloors[i]);
            Assert.True(gap <= 2 * (int)DvergrViaduct.Module || riverGap, $"{name} seed {seed} ruined: no floor from {ruinFloors[i - 1]} to {ruinFloors[i]} m along");
        }
    }

    /// <summary>
    /// The bays carry the deck it is built at, not the deck at grade: 17 m piers at grade (16 m bays), raised two steps
    /// (19 m), have shorter bays, and the whole viaduct keeps every arch along both edges (2 a module joint), where on 16 m
    /// bays the support rule would leave the arches at mid-bay hanging.
    /// </summary>
    [Fact]
    public void TheBaysCarryARaisedDeck()
    {
        var (c, world) = Case("raised-17");
        List<float> piers = DvergrViaduct.PierAlongs(c, world);
        for (int i = 1; i < piers.Count; i++) Assert.InRange(piers[i] - piers[i - 1], DvergrViaduct.TightSpacing - 1e-3f, DvergrViaduct.PierSpacing - 1f);
        List<StructurePiece> whole = DvergrViaduct.SolveComplete(c, world, 5);
        float deck = DvergrViaduct.DeckHeight(c, world);
        int modules = whole.Where(p => p.Prefab == DvergrViaduct.Floor && Mathf.Abs(p.Position.y - (deck - DvergrViaduct.FloorDrop)) < 0.01f)
            .Select(p => Mathf.RoundToInt(Along(c, p))).Distinct().Count();
        Assert.Equal(2 * (modules - 1), whole.Count(p => p.Prefab == DvergrViaduct.Arch));
    }

    /// <summary>Bays flex where 16 m ones do not carry the deck (the other bays may flex a little), the widest that does (fewer piers do): 18 m piers stand 16 m apart, 22 m
    /// piers 12 m (two corbel bands tip to tip, never closer), those between no closer the taller they are, and 23 m piers
    /// are over the ceiling.</summary>
    [Fact]
    public void TallPiersStandCloser()
    {
        float last = float.MaxValue;
        foreach (var (depth, spacing) in new[] { (18f, DvergrViaduct.PierSpacing), (19f, 0f), (20f, 0f), (22f, DvergrViaduct.TightSpacing) })
        {
            var w = new MistValley { Top = 80f, Depth = depth, FloorHalf = 36f, Face = 2f };
            _scope.WithWorld(w);
            RoadCrossing c = DvergrViaduct.ValleyCrossing(new Vector2(-48f, 0f), new Vector2(48f, 0f), w, out string why)!;
            Assert.True(c != null, why);
            List<float> piers = DvergrViaduct.PierAlongs(c, w);
            float bay = piers[1] - piers[0];
            for (int i = 1; i < piers.Count; i++) Assert.Equal(bay, piers[i] - piers[i - 1], 3);
            // In between, one of the spacings, never wider than a lower viaduct's (the banks carry their end bays too).
            if (spacing > 0f) Assert.Equal(spacing, bay, 3);
            else Assert.Contains(DvergrViaduct.BaySpacings, s => Mathf.Abs(s - bay) < 1e-3f);
            Assert.True(bay <= last + 1e-3f, $"{depth} m: {bay} m bays, wider than {last} m");
            last = bay;
        }
        var deep = new MistValley { Top = 80f, Depth = 23f, FloorHalf = 36f, Face = 2f };
        _scope.WithWorld(deep);
        Assert.Null(DvergrViaduct.ValleyCrossing(new Vector2(-48f, 0f), new Vector2(48f, 0f), deep, out string over));
        Assert.Contains("ceiling", over);
    }

    /// <summary>
    /// The deck reach (<see cref="DvergrViaduct.FloorFromPier"/>) against the rule: a pier's screen keeps the game's 257.6
    /// at its top 20 m over its foot (a pier at x 4384, read in game); 16 m bays reach their middle on 18 m piers and
    /// not on 20 m (as SupportGraph has the arches there, 1.29 against 1.08), 14 m bays on 19 m and not on 20 m, 12 m bays
    /// on 20 m and not on 21 m. Over that the middle of a 12 m bay is walked on iron.
    /// </summary>
    [Fact]
    public void TheDeckReachIsTheRules()
    {
        Assert.Equal(257.6f, DvergrViaduct.ScreenTop(22f), 1);
        foreach (var (spacing, carried, short_) in new[] { (DvergrViaduct.PierSpacing, 18f, 20f), (14f, 19f, 20f), (DvergrViaduct.TightSpacing, 20f, 21f) })
        {
            Assert.True(DvergrViaduct.FloorFromPier(carried, spacing / 2f) >= DvergrViaduct.FloorNeed, $"{spacing} m bays on {carried} m piers");
            Assert.True(DvergrViaduct.FloorFromPier(short_, spacing / 2f) < DvergrViaduct.FloorNeed, $"{spacing} m bays on {short_} m piers");
        }
        Assert.Equal(DvergrViaduct.PierSpacing, DvergrViaduct.BaySpacings[0]);
        Assert.Equal(DvergrViaduct.TightSpacing, DvergrViaduct.BaySpacings[DvergrViaduct.BaySpacings.Length - 1]);
    }

    /// <summary>
    /// No two piers' tops overlap (in game six piers once stood grouped too close, their tops overlapping): over every
    /// viaduct case the piers stand at least two corbel bands apart (<see cref="DvergrViaduct.TightSpacing"/>), and every
    /// pier keeps its whole corbel band (vanilla's 20 out_1, 8 outcorners and 4 fillers) in the whole viaduct but what the
    /// support rule leaves hanging.
    /// </summary>
    [Theory]
    [MemberData(nameof(Viaducts))]
    public void NoTwoPiersTopsOverlap(string name, int seed)
    {
        var (c, world) = Case(name);
        List<float> piers = DvergrViaduct.PierAlongs(c, world);
        for (int i = 1; i < piers.Count; i++)
            Assert.True(piers[i] - piers[i - 1] >= DvergrViaduct.TightSpacing - 1e-3f, $"{name}: piers at {piers[i - 1]:F0} and {piers[i]:F0} m along");
        List<StructurePiece> whole = DvergrViaduct.Solve(c, world, seed, false, out DvergrViaduct.HoldReport report);
        foreach (string prefab in new[] { DvergrViaduct.Corbel, DvergrViaduct.CorbelCorner, DvergrViaduct.Filler })
        {
            var tops = whole.Where(p => p.Prefab == prefab).Select(p => FormattableString.Invariant($"{p.Position.x:F2} {p.Position.y:F2} {p.Position.z:F2}")).ToList();
            Assert.Equal(tops.Count, tops.Distinct().Count());
        }
        float deck = DvergrViaduct.DeckHeight(c, world);
        int laid = piers.Count(a => deck - DvergrViaduct.PierGround(c, a, world) >= DvergrViaduct.MinPier);
        int dropped = report.DroppedPieces.Count(p => p.Prefab == DvergrViaduct.Corbel);
        // Every pier laid within the deck keeps its 20 out_1 but those the support rule left out.
        Assert.True(whole.Count(p => p.Prefab == DvergrViaduct.Corbel) + dropped <= 20 * laid);
    }

    /// <summary>
    /// The navigation bay keeps a ship's width (a bay large enough for a longship): its piers stay
    /// at the way +- 16 m. Its middle is too far from piers 22 m tall for the black marble course (the deck reach there is
    /// about 60 against the 190 it needs), so it is walked on iron: every module between the bay piers has a walking
    /// surface at the deck's height, a course block or an iron floor on an iron plate.
    /// </summary>
    [Fact]
    public void ANavigationBayTooLongForItsCourseIsWalkedOnIron()
    {
        var w = WideRiver();
        _scope.WithWorld(w);
        RoadCrossing c = WideRiverCrossing();
        BridgeLayout.ShipWay way = DvergrViaduct.ShipWayUnder(c, w)!.Value;
        Assert.False(DvergrViaduct.HasNavigationBay(c, w));
        List<float> piers = DvergrViaduct.PierAlongs(c, w);
        float nearBay = way.Centre - DvergrViaduct.NavigationBay * 0.5f, farBay = way.Centre + DvergrViaduct.NavigationBay * 0.5f;
        Assert.Contains(piers, a => Mathf.Abs(a - nearBay) < 1e-3f);
        Assert.Contains(piers, a => Mathf.Abs(a - farBay) < 1e-3f);
        Assert.DoesNotContain(piers, a => a > nearBay + 1e-3f && a < farBay - 1e-3f);
        float deck = DvergrViaduct.DeckHeight(c, w);
        bool IronWalk(StructurePiece p) => p.Prefab == DvergrViaduct.Floor && Mathf.Abs(p.Position.y - (deck - DvergrViaduct.IronDeckDrop)) < 0.01f;
        bool Walk(StructurePiece p) => IronWalk(p)
            || (p.Prefab == DvergrViaduct.Block && p.Kind == StructurePieceKind.ViaductDeck && Mathf.Abs(p.Position.y - (deck - DvergrViaduct.CourseDrop)) < 0.01f);
        List<StructurePiece> whole = DvergrViaduct.SolveComplete(c, w, 3);
        for (float a = nearBay; a <= farBay + 1e-3f; a += DvergrViaduct.Module)
            Assert.True(whole.Any(p => Walk(p) && Mathf.Abs(Along(c, p) - a) < 0.01f), $"nothing to walk on {a:F0} m along");
        var ironModules = whole.Where(IronWalk).Select(p => Mathf.RoundToInt(Along(c, p))).Distinct().ToList();
        Assert.NotEmpty(ironModules);
        Assert.All(ironModules, a => Assert.InRange(a, nearBay, farBay));
        // The ruin takes course as ever (a pit a player jumps), never the iron: each iron module keeps its walk.
        foreach (int seed in new[] { 3, 8, 21 })
        {
            List<StructurePiece> ruin = DvergrViaduct.Solve(c, w, seed);
            foreach (int a in ironModules)
                Assert.True(ruin.Any(p => IronWalk(p) && Mathf.RoundToInt(Along(c, p)) == a), $"seed {seed}: no iron to walk on {a} m along");
            foreach (List<StructurePiece> pieces in new[] { whole, ruin })
                foreach (StructurePiece f in pieces.Where(IronWalk))
                    Assert.Contains(pieces, p => p.Prefab == DvergrViaduct.Screen
                        && Vector3.Distance(p.Position, f.Position - new Vector3(0f, DvergrViaduct.CourseDrop - DvergrViaduct.IronDeckDrop, 0f)) < 0.01f);
        }
    }

    [Fact]
    public void APierIsTheVanillaPattern()
    {
        var (c, world) = Case("valley");
        List<StructurePiece> pieces = DvergrViaduct.SolveComplete(c, world, 1);
        // The middle pier, 14 m tall: four shafts, each a footing, a band 9 m under the deck and blocks; the corbel band's
        // 8 m; two iron screens down through it.
        float mid = c.Width * 0.5f;
        var pier = pieces.Where(p => p.Kind == StructurePieceKind.ViaductPier && Mathf.Abs(Along(c, p) - mid) <= 5.01f).ToList();
        Assert.Equal(4, pier.Count(p => p.Prefab == DvergrViaduct.Footing));
        float deck = DvergrViaduct.DeckHeight(c, world);
        Assert.Equal(4, pier.Count(p => p.Prefab == DvergrViaduct.Band && Mathf.Abs(p.Position.y - (deck - 9f)) < 0.01f));
        // Vanilla's middle pier: 20 out_1, 4 fillers, 8 corner pieces in its corbel band.
        Assert.Equal(20, pier.Count(p => p.Prefab == DvergrViaduct.Corbel));
        Assert.Equal(4, pier.Count(p => p.Prefab == DvergrViaduct.Filler));
        Assert.Equal(8, pier.Count(p => p.Prefab == DvergrViaduct.CorbelCorner));
        Assert.True(pier.Count(p => p.Prefab == DvergrViaduct.Screen) >= 2 * 6, "an iron screen at u +-1 from the deck to the ground");
        // The deck: floors two abreast at D - 2, the course at D - 1, arches at D + 1 on both edges.
        Assert.All(pieces.Where(p => p.Prefab == DvergrViaduct.Floor), p => Assert.Equal(deck - 2f, p.Position.y, 3));
        Assert.All(pieces.Where(p => p.Prefab == DvergrViaduct.Arch), p => Assert.Equal(deck + 1f, p.Position.y, 3));
        Assert.Contains(pieces, p => p.Prefab == DvergrViaduct.HeadLeft);
        // Every piece on a heading the hammer can build.
        Assert.All(pieces, p => Assert.True(BridgeLayout.HeadingIsPlaceable(p.YawDegrees, 0.5f), $"{p.Prefab} at yaw {p.YawDegrees}"));
    }

    // ---------------------------------------------------------------- ruin

    [Fact]
    public void RuinIsFixedPerCrossingAndSeed()
    {
        var (c, world) = Case("wide");
        string Key(StructurePiece p) => FormattableString.Invariant($"{p.Prefab} {p.Position.x:F3} {p.Position.y:F3} {p.Position.z:F3} {p.YawDegrees:F1} {p.HealthFraction:F4}");
        var a = DvergrViaduct.Solve(c, world, 1234).Select(Key).ToList();
        var b = DvergrViaduct.Solve(c, world, 1234).Select(Key).ToList();
        Assert.Equal(a, b);
        // Another world seed, other ruins.
        var other = DvergrViaduct.Solve(c, world, 98765).Select(Key).ToList();
        Assert.NotEqual(a, other);
        // Ruin only takes away: every piece standing is one the complete viaduct has.
        string Place(StructurePiece p) => FormattableString.Invariant($"{p.Prefab} {p.Position.x:F3} {p.Position.y:F3} {p.Position.z:F3}");
        var complete = new HashSet<string>(DvergrViaduct.SolveComplete(c, world, 1234).Select(Place));
        Assert.All(DvergrViaduct.Solve(c, world, 1234), p => Assert.Contains(Place(p), complete));
        // Like vanilla, mostly standing: some floors and more arches gone, the piers' cores never.
        int floors = DvergrViaduct.Solve(c, world, 1234).Count(p => p.Prefab == DvergrViaduct.Floor);
        int allFloors = DvergrViaduct.SolveComplete(c, world, 1234).Count(p => p.Prefab == DvergrViaduct.Floor);
        Assert.InRange(floors, allFloors * 0.7f, allFloors);
        foreach (int seed in new[] { 1, 2, 3, 4, 5 })
            Assert.Equal(DvergrViaduct.SolveComplete(c, world, seed).Count(p => p.Prefab == DvergrViaduct.Footing || p.Prefab == DvergrViaduct.Band),
                DvergrViaduct.Solve(c, world, seed).Count(p => p.Prefab == DvergrViaduct.Footing || p.Prefab == DvergrViaduct.Band));
    }

    [Fact]
    public void RuinedDeckStillHasAWayAcross()
    {
        var (c, world) = Case("wide");
        foreach (int seed in Enumerable.Range(1, 40))
        {
            var floors = DvergrViaduct.Solve(c, world, seed).Where(p => p.Prefab == DvergrViaduct.Floor)
                .Select(p => Mathf.RoundToInt(Along(c, p))).Distinct().OrderBy(a => a).ToList();
            for (int i = 1; i < floors.Count; i++)
                Assert.True(floors[i] - floors[i - 1] <= 4, $"seed {seed}: no floor from {floors[i - 1]} to {floors[i]}");
        }
    }
}
