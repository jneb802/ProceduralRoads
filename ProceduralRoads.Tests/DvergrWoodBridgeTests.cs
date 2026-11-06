using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
using static ProceduralRoads.Tests.DvergrViaductTests;

namespace ProceduralRoads.Tests;

/// <summary>
/// Low Mistlands bridges of the Dvergr harbour pier's wood (DvergrWoodBridge): the kit choice, that only
/// the look changes (deck, ends and stair positions are the plain wooden kit's), piles grounded, determinism, ruin, the navigation
/// gap and the free span. Whether the game's support holds the pier's pieces as laid, and what they drop, has to be
/// measured in game; these tests check the layout by construction.
/// </summary>
public sealed class DvergrWoodBridgeTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public DvergrWoodBridgeTests() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); }
    public void Dispose() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); _scope.Dispose(); }

    /// <summary>A Mistlands river whose deck stands 4 m over the water (banks at 34 m, the bed at 26 m).</summary>
    private (RoadCrossing crossing, MistRiver world) Low()
    {
        var w = new MistRiver { Bank = 34f };
        _scope.WithWorld(w);
        return (River(), w);
    }

    private static float Ground(StructurePiece p, WorldGenerator w) => BiomeBlendedHeight.GetBlendedHeight(p.Position.x, p.Position.z, w);

    // ------------------------------------------------------------- the kit

    [Fact]
    public void AMistlandsBridgeThatIsNoViaductIsDvergrWoodAndNothingElseIs()
    {
        var (c, low) = Low();
        Assert.Equal(BridgeStructures.Kit.DvergrWood, BridgeStructures.KitFor(c, low));
        Assert.Contains(BridgeStructures.Solve(c, low, 1), p => p.Prefab == DvergrWoodBridge.Plank);

        // From 6 m up, at 12 m piers and more (bed 26, banks 45): the viaduct.
        var high = new MistRiver();
        _scope.WithWorld(high);
        Assert.Equal(BridgeStructures.Kit.DvergrViaduct, BridgeStructures.KitFor(c, high));

        // Over the 20 m ceiling, no viaduct: still Dvergr wood, as every Mistlands bridge that is not a viaduct is.
        var deep = new MistRiver { Bed = 20f };
        _scope.WithWorld(deep);
        Assert.Equal(BridgeStructures.Kit.DvergrWood, BridgeStructures.KitFor(c, deep));

        // The same low river out of the Mistlands: the plain wooden kit.
        var meadows = new MistRiver { Bank = 34f, Biome = Heightmap.Biome.Meadows };
        _scope.WithWorld(meadows);
        Assert.Equal(BridgeStructures.Kit.Wood, BridgeStructures.KitFor(c, meadows));
        Assert.All(BridgeStructures.Solve(c, meadows, 1), p => Assert.StartsWith("wood_", p.Prefab));

        // A Mistlands ford's spanned footbridge is Dvergr wood too; a wading ford has no kit's pieces, and a
        // span out of the Mistlands keeps the plain footbridge.
        _scope.WithWorld(low);
        Assert.Equal(BridgeStructures.Kit.DvergrWood, BridgeStructures.KitFor(SpanFord(), low));
        Assert.Equal(BridgeStructures.Kit.Wood, BridgeStructures.KitFor(SpanFord(FordStyle.Wade), low));
        _scope.WithWorld(meadows);
        Assert.Equal(BridgeStructures.Kit.Wood, BridgeStructures.KitFor(SpanFord(), meadows));
        Assert.All(BridgeStructures.Solve(SpanFord(), meadows, 1), p => Assert.StartsWith("wood_", p.Prefab));
    }

    /// <summary>
    /// One rule for every structure with end stairs: a viaduct, a Dvergr wood bridge, a plain wooden bridge and a ford's
    /// spanned footbridge in either kit all have their ends from StructureEnds, and a road come at either end from the side
    /// is put through the stairs' foot, on their line (StructureEnds.MeetTheRoad), as the generator does for every one
    /// (MeetsTheRoad), never ending at the deck's end instead.
    /// </summary>
    [Theory]
    [InlineData("viaduct")]
    [InlineData("dvergr wood")]
    [InlineData("wood")]
    [InlineData("dvergr wood span")]
    [InlineData("wood span")]
    public void EveryStructuresEndIsMetAtItsStairsFootTheSameWay(string kind)
    {
        WorldGenerator world = kind switch
        {
            "viaduct" => new MistRiver(),
            "dvergr wood" or "dvergr wood span" => new MistRiver { Bank = 34f },
            _ => new MistRiver { Bank = 34f, Biome = Heightmap.Biome.Meadows },
        };
        _scope.WithWorld(world);
        RoadCrossing c = kind.EndsWith("span") ? SpanFord() : River();
        Assert.Equal(kind switch
        {
            "viaduct" => BridgeStructures.Kit.DvergrViaduct,
            "dvergr wood" or "dvergr wood span" => BridgeStructures.Kit.DvergrWood,
            _ => BridgeStructures.Kit.Wood,
        }, BridgeStructures.KitFor(c, world));
        Assert.True(RoadNetworkGenerator.MeetsTheRoad(c), "its ends are not met through StructureEnds");
        c.FromIndex = 1; c.ToIndex = 2;
        var (near, far) = BridgeStructures.Ends(c, world);
        Vector2 outward = (c.ToBank - c.FromBank).normalized;
        var path = new List<Vector2> { c.FromBank - outward * 60f, c.FromBank, c.ToBank, c.ToBank + new Vector2(-outward.y, outward.x) * 10f, c.ToBank + outward * 40f + new Vector2(0f, 30f) };
        StructureEnds.MeetTheRoad(path, new[] { c }, world, out int unmet, out _);
        Assert.Equal(0, unmet);
        Assert.Contains(near.Apron, path);
        Assert.Contains(far.Apron, path);
        Assert.DoesNotContain(c.ToBank + new Vector2(-outward.y, outward.x) * 10f, path);
    }

    [Theory]
    [InlineData("viaduct", true)]
    [InlineData("dvergr wood", false)]
    [InlineData("wood", false)]
    public void AViaductsEndsAreMetAndCheckedEvenWhileARoadIsPlannedAgainWithItsEndsAsTheyWere(string kind, bool met)
    {
        // A road refused with its ends met is planned again once with its structures' ends as they were. A viaduct is not
        // one of them: its stairs come down where the road arrives only if the road is brought to their foot, so its ends
        // are always met and always checked.
        WorldGenerator world = kind switch
        {
            "viaduct" => new MistRiver(),
            "dvergr wood" => new MistRiver { Bank = 34f },
            _ => new MistRiver { Bank = 34f, Biome = Heightmap.Biome.Meadows },
        };
        _scope.WithWorld(world);
        RoadCrossing c = River();
        var again = typeof(RoadNetworkGenerator).GetField("t_endsAsTheyWere", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        again.SetValue(null, true);
        try { Assert.Equal(met, RoadNetworkGenerator.MeetsTheRoad(c)); }
        finally { again.SetValue(null, false); }
    }

    [Fact]
    public void TheDeckARoadIsJudgedUnderIsTheOneBuilt()
    {
        // A ford span's deck is its footbridge's, over the bank (not the bridge kit's at the bank); a Dvergr wood deck is as
        // wide as its planks are long.
        var world = new MistRiver { Bank = 34f };
        _scope.WithWorld(world);
        RoadCrossing span = SpanFord();
        Assert.Equal(BridgeLayout.SpanDeckHeight(span, world), BridgeStructures.DeckHeight(span, world));
        Assert.True(BridgeStructures.DeckHeight(span, world) > BridgeLayout.DeckHeight(span, world));
        RoadCrossing bridge = River();
        Assert.Equal(BridgeStructures.Kit.DvergrWood, BridgeStructures.KitFor(bridge, world));
        Assert.Equal(DvergrWoodBridge.BeamLength * 0.5f, BridgeStructures.DeckHalfWidth(bridge, world));
    }

    private static RoadCrossing SpanFord(FordStyle style = FordStyle.Span) =>
        RoadCrossing.Between(new Vector2(-31f, 0f), new Vector2(31f, 0f), 26f, new Vector2(-6f, 0f), 20f, CrossingKind.Ford, style);

    [Theory]
    [InlineData(5f)]
    [InlineData(11.5f)]
    public void TheFloorIsTheViaductsOwn(float floor)
    {
        // Moving the viaduct's floor moves the line with it: one floor, read in one place.
        float was = DvergrViaduct.MinViaductPier;
        try
        {
            DvergrViaduct.MinViaductPier = Mathf.Max(DvergrViaduct.MinClearance, floor);
            var high = new MistRiver();
            _scope.WithWorld(high);
            RoadCrossing c = River();
            bool under = DvergrViaduct.TallestPier(c, high) < DvergrViaduct.MinViaductPier - 1e-3f;
            Assert.Equal(under ? BridgeStructures.Kit.DvergrWood : BridgeStructures.Kit.DvergrViaduct, BridgeStructures.KitFor(c, high));
        }
        finally { DvergrViaduct.MinViaductPier = was; }
    }

    // ---------------------------------------------- only the look changes

    [Fact]
    public void TheDeckTheEndsAndTheStairsAreThePlainWoodenKits()
    {
        var (c, low) = Low();
        Assert.Equal(BridgeLayout.DeckHeight(c, low), BridgeStructures.DeckHeight(c, low));
        var (n, f) = BridgeStructures.Ends(c, low);
        var (wn, wf) = BridgeLayout.Ends(c, low);
        foreach (var (a, b) in new[] { (n, wn), (f, wf) })
        {
            Assert.Equal(b.Edge, a.Edge);
            Assert.Equal(b.Height, a.Height);
            Assert.Equal(b.Steps, a.Steps);
        }
        // The stairs, piece for piece, where the plain wooden bridge lays them, in the Dvergr stair.
        string Key(StructurePiece p) => FormattableString.Invariant($"{p.Position.x:F3} {p.Position.y:F3} {p.Position.z:F3} {p.YawDegrees:F2}");
        var wood = BridgeLayout.Solve(c, low, 1).Where(p => p.Kind == StructurePieceKind.Stair).Select(Key).OrderBy(k => k).ToList();
        var dvergr = DvergrWoodBridge.Solve(c, low, 1).Where(p => p.Kind == StructurePieceKind.Stair).Select(Key).OrderBy(k => k).ToList();
        Assert.NotEmpty(wood);
        Assert.Equal(wood, dvergr);
        Assert.All(DvergrWoodBridge.Solve(c, low, 1).Where(p => p.Kind == StructurePieceKind.Stair),
            p => Assert.Equal(DvergrWoodBridge.StairPrefab, p.Prefab));
        // The planks' walking surface is the deck's.
        foreach (StructurePiece p in DvergrWoodBridge.Solve(c, low, 1).Where(p => p.Kind == StructurePieceKind.Deck))
            Assert.Equal(BridgeLayout.DeckHeight(c, low), p.Position.y + DvergrWoodBridge.BeamDepth * 0.5f, 3);
    }

    [Fact]
    public void AMistlandsFordSpanIsLaidOnThePlainFootbridgesDeckAndStairs()
    {
        var (_, low) = Low();
        RoadCrossing ford = SpanFord();
        float deck = BridgeLayout.SpanDeckHeight(ford, low);
        Assert.Equal(deck, DvergrWoodBridge.DeckHeight(ford, low));
        var dvergr = BridgeStructures.Solve(ford, low, 1);
        Assert.Contains(dvergr, p => p.Prefab == DvergrWoodBridge.Plank && p.Kind == StructurePieceKind.Deck);
        Assert.All(dvergr, p => Assert.StartsWith("dvergrprops_", p.Prefab));
        foreach (StructurePiece p in dvergr.Where(p => p.Kind == StructurePieceKind.Deck))
            Assert.Equal(deck, p.Position.y + DvergrWoodBridge.BeamDepth * 0.5f, 3);
        // Its stairs where the plain footbridge lays them, so the road meets it where it did.
        string Key(StructurePiece p) => FormattableString.Invariant($"{p.Position.x:F3} {p.Position.y:F3} {p.Position.z:F3} {p.YawDegrees:F2}");
        var wood = BridgeLayout.Solve(ford, low, 1).Where(p => p.Kind == StructurePieceKind.Stair).Select(Key).OrderBy(k => k).ToList();
        Assert.NotEmpty(wood);
        Assert.Equal(wood, dvergr.Where(p => p.Kind == StructurePieceKind.Stair).Select(Key).OrderBy(k => k).ToList());
        // Its deck runs where the footbridge's did: from the near bank over the built length.
        float length = BridgeLayout.BuiltLength(ford.Width);
        Assert.All(dvergr.Where(p => p.Kind == StructurePieceKind.Deck), p => Assert.InRange(Along(ford, p), 0f, length));
    }

    // ------------------------------------------- the measured pieces' heights

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    public void ThePlanksStringersAndPileTopsStandWhereTheMeasuredPiecesPutThem(int seed)
    {
        var (c, low) = Low();
        float deck = BridgeLayout.DeckHeight(c, low);
        var pieces = DvergrWoodBridge.Solve(c, low, seed);
        // dvergrprops_wood_beam is 0.8 m deep with its origin at its centre: the planks' top is the walking surface ...
        Assert.Equal(0.8f, DvergrWoodBridge.BeamDepth);
        Assert.All(pieces.Where(p => p.Kind == StructurePieceKind.Deck), p => Assert.Equal(deck, p.Position.y + 0.4f, 3));
        // ... the stringers' centres 0.5 m under the planks' (Mistlands_Harbour1 fort (1)/pier: planks y 1.5,
        // stringers y 1.0), so they reach 0.3 m up into the planks as vanilla's do ...
        Assert.Equal(0.5f, DvergrWoodBridge.StringerDrop);
        Assert.All(pieces.Where(p => p.Kind == StructurePieceKind.Beam), p => Assert.Equal(deck - 0.4f - 0.5f, p.Position.y, 3));
        Assert.All(pieces.Where(p => p.Kind == StructurePieceKind.Beam), p => Assert.Equal(0.3f, (p.Position.y + 0.4f) - (deck - 0.8f), 3));
        // ... dvergrprops_wood_pole is 4 m with its origin at its centre: each standing bent's top pile ends 0.75 m over the
        // walking surface, as the pier's ...
        var tops = pieces.Where(p => p.Prefab == DvergrWoodBridge.Pile && p.Kind == StructurePieceKind.Post && Along(c, p) >= -0.01f
                && Along(c, p) <= BridgeLayout.BuiltLength(c.Width) + 0.01f)
            .GroupBy(p => (Mathf.Round(p.Position.x * 100f), Mathf.Round(p.Position.z * 100f))).Select(g => g.Max(p => p.Position.y) + 2f).ToList();
        Assert.NotEmpty(tops);
        Assert.All(tops, t => Assert.Equal(deck + 0.75f, t, 3));
    }

    /// <summary>A Mistlands river whose near bank (x &lt; 0) is 4 m lower than the far one: the near stair walks down off
    /// the deck over falling ground, so its first steps stand on posts.</summary>
    private sealed class LowNearBank : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => Mathf.Abs(wx) <= 30f ? 26f : wx < 0f ? 30f : 34f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = Mathf.Abs(wx) < 30f ? 1f : 0f;
            width = 60f;
        }
        public override int GetSeed() => 76;
    }

    [Fact]
    public void AStairStepOverTheGroundStandsOnADvergrPileCarriedToTheGround()
    {
        var w = new LowNearBank();
        _scope.WithWorld(w);
        RoadCrossing c = River();
        var pieces = DvergrWoodBridge.Solve(c, w, 1);
        Assert.DoesNotContain(pieces, p => p.Prefab == BridgeLayout.PostPrefab);
        var posts = pieces.Where(p => p.Kind == StructurePieceKind.Post && Along(c, p) < -0.5f).ToList();
        Assert.NotEmpty(posts);
        Assert.All(posts, p => Assert.Equal(DvergrWoodBridge.Pile, p.Prefab));
        foreach (StructurePiece step in pieces.Where(p => p.Kind == StructurePieceKind.Stair && Along(c, p) < 0f))
        {
            float ground = Ground(step, w);
            var under = posts.Where(p => Mathf.Abs(p.Position.x - step.Position.x) < 1e-3f && Mathf.Abs(p.Position.z - step.Position.z) < 1e-3f)
                .OrderByDescending(p => p.Position.y).ToList();
            if (step.Position.y <= ground + BridgeLayout.StairFootTolerance) { Assert.Empty(under); continue; }
            Assert.NotEmpty(under);
            // The column's top is the step's foot, each pile 4 m under the one above, the lowest one's foot in the ground.
            Assert.Equal(step.Position.y, under[0].Position.y + DvergrWoodBridge.PileLength * 0.5f, 3);
            for (int i = 1; i < under.Count; i++) Assert.Equal(under[i - 1].Position.y - DvergrWoodBridge.PileLength, under[i].Position.y, 3);
            Assert.True(under[under.Count - 1].Position.y - DvergrWoodBridge.PileLength * 0.5f <= ground);
        }
    }

    // ----------------------------------------------- damaged like vanilla

    [Fact]
    public void EveryPieceIsWornAsTheVanillaHarboursAndTheSameEveryTime()
    {
        var (c, low) = Low();
        for (int s = 1; s <= 12; s++)
        {
            var pieces = DvergrWoodBridge.Solve(c, low, s);
            Assert.All(pieces, p => Assert.InRange(p.HealthFraction, BridgeLayout.LocationDamageMin, BridgeLayout.LocationDamageMax));
            Assert.Equal(pieces.Select(p => p.HealthFraction), DvergrWoodBridge.Solve(c, low, s).Select(p => p.HealthFraction));
        }
        // Spread over the range, not one value.
        var healths = DvergrWoodBridge.Solve(c, low, 1).Select(p => p.HealthFraction).ToList();
        Assert.True(healths.Min() < 0.2f && healths.Max() > 0.5f, $"health {healths.Min():F2}..{healths.Max():F2}");
    }

    [Fact]
    public void EveryPileStandsAndAPlankIsMissingAsRarelyAsTheHarboursAre()
    {
        var (c, low) = Low();
        float length = BridgeLayout.BuiltLength(c.Width);
        float[] bents = DvergrWoodBridge.BentAlongs(length);
        int n = bents.Length, planks = Mathf.RoundToInt(length / DvergrWoodBridge.PlankPitch);
        int laid = 0, could = 0;
        for (int s = 1; s <= 100; s++)
        {
            var pieces = DvergrWoodBridge.Solve(c, low, s);
            var complete = DvergrWoodBridge.SolveComplete(c, low, s);
            // Every bent but the navigation gap's stands: no pile falls, none leaves a stub, nothing lies on the bed.
            Assert.Equal(complete.Count(p => p.Prefab == DvergrWoodBridge.Pile && !BridgeLayout.InNavigationGap(c, Along(c, p))),
                pieces.Count(p => p.Prefab == DvergrWoodBridge.Pile));
            Assert.DoesNotContain(pieces, p => p.Kind == StructurePieceKind.Debris);
            // Planks over the standing middle bays (the end bays keep every plank, the way on from the stairs).
            for (int j = 0; j < planks; j++)
            {
                float a = (j + 0.5f) * DvergrWoodBridge.PlankPitch;
                int bay = Array.FindIndex(bents, b => b >= a) - 1;
                if (bay <= 0 || bay >= n - 2 || BridgeLayout.InNavigationGap(c, bents[bay]) || BridgeLayout.InNavigationGap(c, bents[bay + 1])) continue;
                could++;
                if (pieces.Any(p => p.Kind == StructurePieceKind.Deck && Mathf.Abs(Along(c, p) - a) < 0.01f)) laid++;
            }
        }
        float survival = (float)laid / could;
        Assert.True(could > 500, $"only {could} planks sampled");
        // 31 of 36 (1 - BridgeLayout.DvergrWoodPlankLoss), within the sampling's spread.
        Assert.InRange(survival, 1f - BridgeLayout.DvergrWoodPlankLoss - 0.04f, 1f - BridgeLayout.DvergrWoodPlankLoss + 0.04f);
    }

    // ------------------------------------------------------ by construction

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public void EveryPileColumnReachesTheGroundAndNoneIsBuried(int seed)
    {
        var (c, low) = Low();
        float deck = BridgeLayout.DeckHeight(c, low);
        var pieces = DvergrWoodBridge.Solve(c, low, seed);
        var piles = pieces.Where(p => p.Prefab == DvergrWoodBridge.Pile).ToList();
        Assert.NotEmpty(piles);
        foreach (var column in piles.GroupBy(p => (Mathf.Round(p.Position.x * 100f), Mathf.Round(p.Position.z * 100f))))
        {
            var stack = column.OrderByDescending(p => p.Position.y).ToList();
            float ground = Ground(stack[0], low);
            // Each pile stands on the one below (origin at its centre, PileLength long), none wholly under the ground,
            // and the lowest one's foot is in it.
            float half = DvergrWoodBridge.PileLength * 0.5f;
            for (int i = 1; i < stack.Count; i++) Assert.Equal(stack[i - 1].Position.y - DvergrWoodBridge.PileLength, stack[i].Position.y, 3);
            Assert.All(stack, p => Assert.True(p.Position.y + half > ground, $"pile at {p.Position} is wholly under the ground {ground}"));
            Assert.True(stack[stack.Count - 1].Position.y - half <= ground, $"pile column at {stack[0].Position} stops above the ground {ground}");
            // A bent's piles rise past the walking surface as the pier's do; a stair's post stops at its step's foot.
            float top = stack[0].Position.y + half;
            Assert.True(Mathf.Abs(top - (deck + DvergrWoodBridge.PileAboveDeck)) < 1e-3f
                || pieces.Any(s => s.Kind == StructurePieceKind.Stair && Mathf.Abs(s.Position.y - top) < 1e-3f), $"pile top {top}");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public void EveryPlankLiesOverABayWhoseBentsStand(int seed)
    {
        var (c, low) = Low();
        float length = BridgeLayout.BuiltLength(c.Width);
        float[] bents = DvergrWoodBridge.BentAlongs(length);
        var pieces = DvergrWoodBridge.Solve(c, low, seed);
        var standing = new HashSet<int>();
        for (int i = 0; i < bents.Length; i++)
            if (pieces.Any(p => p.Prefab == DvergrWoodBridge.Pile && p.Kind == StructurePieceKind.Post && Mathf.Abs(Along(c, p) - bents[i]) < 1e-2f
                && p.Position.y + DvergrWoodBridge.PileLength * 0.5f > BridgeLayout.DeckHeight(c, low)))
                standing.Add(i);
        Assert.Contains(0, standing);
        Assert.Contains(bents.Length - 1, standing);
        foreach (StructurePiece plank in pieces.Where(p => p.Kind == StructurePieceKind.Deck))
        {
            float a = Along(c, plank);
            int bay = Array.FindIndex(bents, b => b >= a) - 1;
            Assert.True(standing.Contains(bay) && standing.Contains(bay + 1), $"plank at {a:F1} m over bay {bay} with a bent down");
        }
        // Every stringer lies under planks of its own bay.
        foreach (StructurePiece s in pieces.Where(p => p.Kind == StructurePieceKind.Beam))
            Assert.Contains(pieces, p => p.Kind == StructurePieceKind.Deck && Mathf.Abs(Along(c, p) - Along(c, s)) <= DvergrWoodBridge.BeamLength * 0.5f);
        // The bents are the pier's: at most 5 m apart, piles 5.75 m apart across.
        for (int i = 1; i < bents.Length; i++) Assert.InRange(bents[i] - bents[i - 1], 0.1f, DvergrWoodBridge.BentSpacing + 1e-3f);
        Assert.Equal(5.75f, 2f * DvergrWoodBridge.PileSideOffset, 3);
    }

    [Fact]
    public void TheLayoutIsFixedPerCrossingAndSeedAndRuinOnlyTakesAway()
    {
        var (c, low) = Low();
        string Sig(List<StructurePiece> ps) => string.Join("|", ps.Select(p => FormattableString.Invariant($"{p.Prefab}@{p.Position.x:F2},{p.Position.y:F2},{p.Position.z:F2}/{p.YawDegrees:F1}/{p.HealthFraction:F3}")));
        Assert.Equal(Sig(DvergrWoodBridge.Solve(c, low, 1)), Sig(DvergrWoodBridge.Solve(c, low, 1)));
        // Another seed ruins it otherwise, over the same complete bridge.
        var seeds = Enumerable.Range(1, 12).Select(s => Sig(DvergrWoodBridge.Solve(c, low, s))).Distinct().Count();
        Assert.True(seeds > 1, "the ruin never varies with the seed");
        var complete = DvergrWoodBridge.SolveComplete(c, low, 1);
        string P(StructurePiece p) => FormattableString.Invariant($"{p.Prefab}@{p.Position.x:F2},{p.Position.y:F2},{p.Position.z:F2}");
        var completeSet = new HashSet<string>(complete.Select(P));
        for (int s = 1; s <= 12; s++)
        {
            var ruined = DvergrWoodBridge.Solve(c, low, s);
            Assert.True(ruined.Count(p => p.Kind == StructurePieceKind.Deck) <= complete.Count(p => p.Kind == StructurePieceKind.Deck));
            // Every standing piece is one of the complete bridge's (the kit leaves no stubs or debris: its piles all stand).
            // Mist lights are the seed's own, not ruin (DvergrWoodLightTests).
            Assert.All(ruined.Where(p => p.Kind != StructurePieceKind.Flair), p => Assert.Contains(P(p), completeSet));
        }
        // Somewhere over twelve seeds a plank is missing mid-span.
        Assert.Contains(Enumerable.Range(1, 12), s => DvergrWoodBridge.Solve(c, low, s).Count(p => p.Kind == StructurePieceKind.Deck) < complete.Count(p => p.Kind == StructurePieceKind.Deck));
    }

    [Fact]
    public void ABridgeCarriesNoLanterns()
    {
        // The harbour's lanterns hang from its buildings, not the dock; its mist lights are DvergrVanillaTests'.
        var (c, low) = Low();
        for (int s = 1; s <= 40; s++)
            Assert.DoesNotContain(DvergrWoodBridge.Solve(c, low, s), p => p.Prefab.Contains("lantern"));
    }

    [Fact]
    public void TheNavigationGapStaysOpen()
    {
        var (c, low) = Low();
        Assert.True(c.FairwayWidth > 0f && !BridgeLayout.IsFreeSpan(c, low));
        for (int s = 1; s <= 12; s++)
            foreach (StructurePiece p in DvergrWoodBridge.Solve(c, low, s).Where(p => p.Kind != StructurePieceKind.Debris))
                Assert.False(BridgeLayout.InNavigationGap(c, Along(c, p)), $"{p.Prefab} at {Along(c, p):F1} m stands in the navigation gap");
        // The complete bridge fills it, as the wooden kit's does: it is a run of bays a player could close.
        Assert.Contains(DvergrWoodBridge.SolveComplete(c, low, 1), p => p.Kind == StructurePieceKind.Deck && BridgeLayout.InNavigationGap(c, Along(c, p)));
    }

    [Fact]
    public void AFreeSpanHasBentsOnlyAtItsBanksAndStringersBankToBank()
    {
        var w = new MistRiver { Bank = 34f };
        _scope.WithWorld(w);
        // 14 m over a 14 m fairway: the boat's lane covers it all, so nothing stands in the water.
        RoadCrossing c = RoadCrossing.Between(new Vector2(-7f, 0f), new Vector2(7f, 0f), 26f, new Vector2(0f, 0f), 14f, CrossingKind.Bridge);
        Assert.True(BridgeLayout.IsFreeSpan(c, w));
        Assert.Equal(BridgeStructures.Kit.DvergrWood, BridgeStructures.KitFor(c, w));
        float length = BridgeLayout.BuiltLength(c.Width);
        var pieces = DvergrWoodBridge.Solve(c, w, 3);
        // (The stairs' posts, outside the deck's ends, stand on the banks.)
        Assert.All(pieces.Where(p => p.Prefab == DvergrWoodBridge.Pile && Along(c, p) > -0.5f && Along(c, p) < length + 0.5f),
            p => Assert.True(Along(c, p) < 0.01f || Mathf.Abs(Along(c, p) - length) < 0.01f, $"pile at {Along(c, p):F1}"));
        Assert.DoesNotContain(pieces, p => p.Kind == StructurePieceKind.Debris);
        var stringers = pieces.Where(p => p.Kind == StructurePieceKind.Beam).Select(p => Along(c, p)).Distinct().OrderBy(a => a).ToList();
        Assert.True(stringers.First() - DvergrWoodBridge.BeamLength * 0.5f <= 0.01f && stringers.Last() + DvergrWoodBridge.BeamLength * 0.5f >= length - 0.01f);
        for (int i = 1; i < stringers.Count; i++) Assert.True(stringers[i] - stringers[i - 1] <= DvergrWoodBridge.BeamLength + 1e-3f);
    }

    [Fact]
    public void EveryHeadingIsOneTheHammerHas()
    {
        var (c, low) = Low();
        Assert.All(DvergrWoodBridge.Solve(c, low, 1).Where(p => p.Kind != StructurePieceKind.Debris),
            p => Assert.True(BridgeLayout.HeadingIsPlaceable(p.YawDegrees), $"{p.Prefab} at yaw {p.YawDegrees}"));
    }
}

