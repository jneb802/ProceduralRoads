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
        /// <summary>How fast the ground falls away west of the near bank (x = -31), to 6 m under it.</summary>
        public float NearFall;
        public Heightmap.Biome Biome = Heightmap.Biome.Mistlands;
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) <= 30f ? Bed : wx < -31f ? Bank - Mathf.Min(NearFall * (-31f - wx), 6f) : Bank;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Biome;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = Mathf.Abs(wx) < 30f ? 1f : 0f;
            width = 60f;
        }
        public override int GetSeed() => 76;
    }

    public DvergrViaductTests()
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
    }

    private (RoadCrossing crossing, WorldGenerator world) Case(string name)
    {
        switch (name)
        {
            case "river":
            {
                var w = new MistRiver(); _scope.WithWorld(w); return (River(), w);
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
                // 25 m deep: piers taller than 20 m, under the ceiling.
                var w = new MistValley { Depth = 25f }; _scope.WithWorld(w);
                RoadCrossing t = DvergrViaduct.ValleyCrossing(new Vector2(-32f, 0f), new Vector2(32f, 0f), w, out string why)!;
                Assert.True(t != null, why);
                Assert.InRange(DvergrViaduct.TallestPier(t, w), 21f, DvergrViaduct.PierCeiling);
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
