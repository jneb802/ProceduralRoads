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
/// the look changes (deck, ends and stair positions are the wooden kits'), piles grounded, determinism, ruin, the navigation
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

        // Over the 26 m ceiling, no viaduct: still Dvergr wood, as every Mistlands bridge that is not a viaduct is.
        var deep = new MistRiver { Bank = 62f, Bed = 20f };
        _scope.WithWorld(deep);
        Assert.Equal(BridgeStructures.Kit.DvergrWood, BridgeStructures.KitFor(c, deep));

        // The same low river out of the Mistlands, in the Meadows: a railed wooden kit; in the Mountains, a railed kit or
        // the stone kit where it fits. Never the Dvergr pieces.
        foreach (Heightmap.Biome biome in new[] { Heightmap.Biome.Meadows, Heightmap.Biome.Mountain })
        {
            var other = new MistRiver { Bank = 34f, Biome = biome };
            _scope.WithWorld(other);
            string prefix = BridgeStructures.KitFor(c, other) == BridgeStructures.Kit.Stone ? "stone_" : "wood_";
            Assert.True(biome != Heightmap.Biome.Meadows || prefix == "wood_");
            Assert.All(BridgeStructures.Solve(c, other, 1), p => Assert.StartsWith(prefix, p.Prefab));
            // A ford's spanned footbridge out of the Mistlands takes its kit by the same rule.
            BridgeStructures.Kit? span = BridgeStructures.KitFor(SpanFord(), other);
            Assert.True(BridgeStructures.RailedKitOf(span) != null || (span == BridgeStructures.Kit.Stone && biome != Heightmap.Biome.Meadows));
            Assert.Contains(BridgeStructures.Solve(SpanFord(), other, 1),
                p => p.Prefab == (span == BridgeStructures.Kit.Stone ? BridgeLayout.StoneFloorPrefab : BridgeLayout.RailPostPrefab));
        }

        // A Mistlands ford's spanned footbridge is Dvergr wood too; a wading ford has no kit's pieces.
        _scope.WithWorld(low);
        Assert.Equal(BridgeStructures.Kit.DvergrWood, BridgeStructures.KitFor(SpanFord(), low));
        Assert.Empty(BridgeStructures.Solve(SpanFord(FordStyle.Wade), low, 1));
    }

    /// <summary>
    /// One rule for every structure with end stairs: a viaduct, a Dvergr wood bridge, a railed wooden bridge in any biome
    /// but the Mistlands and a ford's spanned footbridge in either wood (in the Mountains the stone kit, where it is drawn) all have their ends from StructureEnds, and a road come at either end from the side
    /// is brought to the stairs' foot, on their line (StructureEnds.MeetEnds), as the generator does for every one
    /// (MeetsTheRoad), never ending at the deck's end instead. So too where an end is landed on a row or met level, kept
    /// as the road that laid the crossing met it: the stairs laid are the ones its end reports, and the road is brought to
    /// their foot.
    /// </summary>
    [Theory]
    [InlineData("viaduct")]
    [InlineData("dvergr wood")]
    [InlineData("wood mountain")]
    [InlineData("dvergr wood span")]
    [InlineData("wood span")]
    [InlineData("wood span mountain")]
    [InlineData("wood railed")]
    [InlineData("wood railed landed")]
    [InlineData("wood railed level")]
    [InlineData("wood span level")]
    [InlineData("dvergr wood landed")]
    public void EveryStructuresEndIsMetAtItsStairsFootTheSameWay(string meeting)
    {
        string kind = meeting.Replace(" landed", "").Replace(" level", "");
        WorldGenerator world = kind switch
        {
            "viaduct" => new MistRiver(),
            "dvergr wood" or "dvergr wood span" => new MistRiver { Bank = 34f },
            "wood railed" or "wood span" => new MistRiver { Bank = 34f, Biome = Heightmap.Biome.Meadows },
            _ => new MistRiver { Bank = 34f, Biome = Heightmap.Biome.Mountain },
        };
        // A bank falling away west of the near end, for a run long enough to land halfway down.
        if (meeting.EndsWith(" landed")) ((MistRiver)world).NearFall = 0.6f;
        _scope.WithWorld(world);
        RoadCrossing c = kind.EndsWith("span") ? SpanFord() : River();
        BridgeStructures.Kit? kit = BridgeStructures.KitFor(c, world);
        if (kind.StartsWith("wood"))
            Assert.True(BridgeStructures.RailedKitOf(kit) != null || (kit == BridgeStructures.Kit.Stone && kind.EndsWith("mountain")), $"{kit}");
        else
            Assert.Equal(kind == "viaduct" ? BridgeStructures.Kit.DvergrViaduct : BridgeStructures.Kit.DvergrWood, kit);
        Assert.True(RoadNetworkGenerator.MeetsTheRoad(c), "its ends are not met through StructureEnds");
        c.FromIndex = 1; c.ToIndex = 2;
        // Landed on the row halfway down the near end's run; the far end met level along its line.
        var natural = StructureEnds.Natural(c, world);
        MetEnds.Set(c, world, fromRow: meeting.EndsWith(" landed") ? natural.near.Steps / 2 : null, toLevel: meeting.EndsWith(" level"));
        var (near, far) = BridgeStructures.Ends(c, world);
        if (meeting.EndsWith(" landed")) Assert.True(near.Steps >= 2 && near.Steps < BridgeStructures.Ends(River(), world).near.Steps, $"landed on row {near.Steps}");
        if (meeting.EndsWith(" level")) Assert.Equal(0, far.Steps);
        var laid = BridgeStructures.Solve(c, world, 1).FindAll(p => p.Kind == StructurePieceKind.Stair);
        Assert.Equal(2 * near.Steps, laid.Count(p => p.Position.x < 0f));
        Assert.Equal(2 * far.Steps, laid.Count(p => p.Position.x > 0f));
        Vector2 outward = (c.ToBank - c.FromBank).normalized;
        var path = new List<Vector2> { c.FromBank - outward * 60f, c.FromBank, c.ToBank, c.ToBank + new Vector2(-outward.y, outward.x) * 10f, c.ToBank + outward * 40f + new Vector2(0f, 30f) };
        c.Shared = true;
        Assert.Null(StructureEnds.MeetEnds(path, new List<RoadCrossing> { c }, world, float.PositiveInfinity, null));
        Assert.Equal((near.Meet, far.Meet), (path[c.FromIndex], path[c.ToIndex]));
        Assert.Contains(near.Apron, path);
        Assert.Contains(far.Apron, path);
        Assert.DoesNotContain(c.ToBank + new Vector2(-outward.y, outward.x) * 10f, path);
    }

    /// <summary>A river across x, its water |x| under <see cref="Half"/>: the bed at 26 m, the banks at <see cref="Bank"/>, in
    /// <see cref="Biome"/>.</summary>
    private sealed class Channel : WorldGenerator
    {
        public float Bank = 34f, Bed = 26f, Half = 30f;
        public Heightmap.Biome Biome = Heightmap.Biome.Meadows;
        public override float GetHeight(float wx, float wy) => Mathf.Abs(wx) <= Half ? Bed : Bank;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Biome;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = Mathf.Abs(wx) < Half ? 1f : 0f;
            width = 2f * Half;
        }
        public override int GetSeed() => 76;
    }

    /// <summary>One crossing of every kind and kit that occurs, in a world it occurs in.</summary>
    private (RoadCrossing c, WorldGenerator world) Crossing(string kind)
    {
        Heightmap.Biome biome = kind.Contains("DvergrWood") || kind.Contains("viaduct") ? Heightmap.Biome.Mistlands
            : kind.Contains("Stone") ? Heightmap.Biome.Mountain : Heightmap.Biome.Meadows;
        if (kind == "valley viaduct")
        {
            var valley = new StructureEndTests.RimValley();
            _scope.WithWorld(valley);
            float bank = valley.Rim + 1f;
            return (DvergrViaduct.ValleyCrossing(new Vector2(-bank, 0f), new Vector2(bank, 0f), valley, out _)!, valley);
        }
        var world = new Channel { Biome = biome, Bank = kind is "river viaduct" or "tall bridge WoodRail" ? 45f : 34f };
        if (kind is "wade" or "raise") { world.Bank = 30.6f; world.Bed = 29.6f; world.Half = 4f; }
        _scope.WithWorld(world);
        // A free span: 11 m over an 11 m fairway, the boat's lane covering it all.
        float edge = kind.StartsWith("free span") ? 5.5f : world.Half + 1f;
        bool ford = kind is "wade" or "raise" || kind.StartsWith("span");
        var c = RoadCrossing.Between(new Vector2(-edge, 0f), new Vector2(edge, 0f), world.Bed, new Vector2(0f, 0f), kind.StartsWith("free span") ? 11f : 0f,
            ford ? CrossingKind.Ford : CrossingKind.Bridge, kind == "wade" ? FordStyle.Wade : kind == "raise" ? FordStyle.Raise : ford ? FordStyle.Span : FordStyle.None);
        string kit = kind.Split(' ').Last();
        if (Enum.TryParse(kit, out BridgeStructures.Kit k) && k != BridgeStructures.Kit.DvergrViaduct) c.Kit = k;
        return (c, world);
    }

    /// <summary>
    /// One owner of every crossing's ends, every kind and kit alike (StructureEnds.MeetEnds): a road come along the line
    /// at one bank and from the side at the other is brought to each end's foot, which the crossing saves, on the end's
    /// line, the road leading away from it outward and never back under it; every kit lays exactly the stairs its ends say,
    /// their lowest step at the foot (BridgeLayout.LayEndStairs); and the record reads the ends back, the bridge laid from
    /// it the same. A waded or raised ford is road: its ends are its banks, met as they are.
    /// </summary>
    [Theory]
    [InlineData("wade")]
    [InlineData("raise")]
    [InlineData("span DvergrWood")]
    [InlineData("span WoodRail")]
    [InlineData("span WoodBraced")]
    [InlineData("span Stone")]
    [InlineData("bridge DvergrWood")]
    [InlineData("bridge WoodRail")]
    [InlineData("bridge WoodBraced")]
    [InlineData("bridge Stone")]
    [InlineData("free span DvergrWood")]
    [InlineData("tall bridge WoodRail")]
    [InlineData("river viaduct")]
    [InlineData("valley viaduct")]
    public void EveryCrossingKindAndKitHasItsEndsFromTheOneOwner(string kind)
    {
        var (c, world) = Crossing(kind);
        Assert.True(kind.StartsWith("free span") == BridgeLayout.IsFreeSpan(c, world), $"{kind}: free span {BridgeLayout.IsFreeSpan(c, world)}");
        Assert.Equal(kind.Contains("viaduct"), BridgeStructures.IsViaduct(c, world));
        Vector2 dir = c.Direction, side = new(-dir.y, dir.x);
        Vector2 aside = c.ToBank + side * 10f;
        var path = new List<Vector2> { c.FromBank - dir * 60f, c.FromBank - dir * 30f, c.FromBank, c.ToBank, aside, c.ToBank + dir * 40f + side * 30f };
        var asSearched = new List<Vector2>(path);
        c.FromIndex = 2; c.ToIndex = 3;
        Assert.Null(StructureEnds.MeetEnds(path, new List<RoadCrossing> { c }, world, RoadConstants.TerrainDeltaMax, null));
        StructureEnds.End near = Assert.NotNull(c.FromEnd), far = Assert.NotNull(c.ToEnd);
        Assert.Equal(near.Meet, path[c.FromIndex]);
        Assert.Equal(far.Meet, path[c.ToIndex]);
        if (kind is "wade" or "raise")
        {
            Assert.True(near.Road && far.Road);
            Assert.Equal((c.FromBank, c.ToBank), (near.Meet, far.Meet));
            Assert.Equal(asSearched, path);
            Assert.False(BridgeStructures.Ends(c, world).near.IsSet);
            Assert.Empty(BridgeStructures.SolveComplete(c, world, 1));
            return;
        }
        Assert.False(near.Road || far.Road);
        Assert.Equal((near, far), BridgeStructures.Ends(c, world));
        foreach (var (end, outward) in new[] { (near, path[c.FromIndex - 1]), (far, path[c.ToIndex + 1]) })
        {
            // On its line, the road leading away from it outward.
            Assert.True(Vector2.Dot(outward - end.Meet, -end.Up) > 0.5f, $"{kind}: the road leaves the end at {end.Meet} toward {outward}");
            Assert.True(Mathf.Abs(Vector2.Dot(outward - end.Meet, new Vector2(-end.Up.y, end.Up.x))) < 0.01f, $"{kind}: the road off the end's line at {outward}");
            Assert.Contains(end.Apron, path);
        }
        Assert.DoesNotContain(aside, path);

        // Every kit lays exactly the stairs its ends say, their lowest step at the foot.
        void AssertLays(RoadCrossing crossing, StructureEnds.End n, StructureEnds.End f, string what)
        {
            var stairs = BridgeStructures.SolveComplete(crossing, world, 1).FindAll(p => p.Kind == StructurePieceKind.Stair);
            foreach (var (end, other) in new[] { (n, f), (f, n) })
            {
                var mine = stairs.FindAll(p => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), end.Edge) < Vector2.Distance(new Vector2(p.Position.x, p.Position.z), other.Edge));
                Assert.True(2 * end.Steps == mine.Count, $"{kind}, {what}: {mine.Count} stairs laid at the end at {end.Edge}, its {end.Steps} steps two abreast");
                if (end.Steps == 0) continue;
                StructurePiece lowest = mine.OrderBy(p => p.Position.y).First();
                Assert.True(Mathf.Abs(lowest.Position.y - end.MeetHeight) < 0.01f, $"{kind}, {what}: the lowest step at {lowest.Position.y:F2}, the foot at {end.MeetHeight:F2}");
                Vector2 at = new(lowest.Position.x, lowest.Position.z);
                Assert.True(Mathf.Abs(Vector2.Dot(at - end.Meet, end.Up) - BridgeLayout.StairHalfRun) < 0.01f, $"{kind}, {what}: the lowest step at {at}, the foot at {end.Meet}");
            }
        }
        AssertLays(c, near, far, "as met");

        // Saved and read back: the same ends, the same stairs.
        var loaded = new List<RoadCrossing>();
        Assert.True(RoadNetworkPersistence.DeserializeRoadCrossings(RoadNetworkPersistence.SerializeRoadCrossings(new[] { c }), loaded));
        RoadCrossing back = Assert.Single(loaded);
        Assert.Equal((near, far), BridgeStructures.Ends(back, world));
        AssertLays(back, near, far, "read back");
    }

    /// <summary>
    /// One stub rule for every crossing with a deck, every kind and kit alike (BridgeStructures.HasDeck; it was a valley
    /// viaduct's alone, and a ford span's end left a staircase 4.4 m over its levelled road): a staircase starting a few
    /// metres past an end, 3 m over its foot, is not planned with that road. A waded or raised ford is road, with no end
    /// of its own to hold the road to.
    /// </summary>
    [Theory]
    [InlineData("wade")]
    [InlineData("raise")]
    [InlineData("span DvergrWood")]
    [InlineData("span WoodRail")]
    [InlineData("span WoodBraced")]
    [InlineData("span Stone")]
    [InlineData("bridge DvergrWood")]
    [InlineData("bridge WoodRail")]
    [InlineData("bridge WoodBraced")]
    [InlineData("bridge Stone")]
    [InlineData("free span DvergrWood")]
    [InlineData("tall bridge WoodRail")]
    [InlineData("river viaduct")]
    [InlineData("valley viaduct")]
    public void EveryCrossingKindAndKitKeepsTheStubRule(string kind)
    {
        var (c, world) = Crossing(kind);
        Vector2 dir = c.Direction;
        var path = new List<Vector2> { c.FromBank - dir * 60f, c.FromBank - dir * 30f, c.FromBank, c.ToBank, c.ToBank + dir * 40f, c.ToBank + dir * 80f };
        c.FromIndex = 2; c.ToIndex = 3;
        Assert.Null(StructureEnds.MeetEnds(path, new List<RoadCrossing> { c }, world, RoadConstants.TerrainDeltaMax, null));
        StructureEnds.End far = Assert.NotNull(c.ToEnd);
        Assert.Equal(BridgeStructures.HasDeck(c), far.IsStructure);
        if (!far.IsStructure) return;
        Vector2 foot = far.Meet - far.Up * 5f, top = foot - far.Up * 10f;
        path.RemoveRange(c.ToIndex + 1, path.Count - c.ToIndex - 1);
        path.AddRange(new[] { foot, top, top - far.Up * 30f });
        var stairs = new List<RoadStairSpan> { new() { FromIndex = c.ToIndex + 1, ToIndex = c.ToIndex + 2, FromPoint = foot, ToPoint = top, FromHeight = far.MeetHeight + 3f, ToHeight = far.MeetHeight + 3f } };
        RoadSpatialGrid.LastRefusal = null;
        Assert.Null(RoadNetworkGenerator.PlanRoadPathWithCrossings(path, new List<RoadCrossing> { c }, 4f, null, null, stairs));
        Assert.Equal("a stub of road between two structures", RoadSpatialGrid.LastRefusal);
    }

    /// <summary>
    /// A road come along the bank square to a bridge's line, a few metres out from its near end's foot, where the bank
    /// falls away west of the deck: it cannot come in along the line over an apron, and under a 1 m earthwork cap no flat
    /// approach fits, so the end keeps its stairs (down to the ground, or a metre over it) and opens onto its landing pad,
    /// and the road leaves the pad
    /// square to the line (StructureEnds.PadLength), turning a road's half-width out from the foot (StructureEnds.PadTurn):
    /// its centreline across the pad 2 m beyond the foot, the bottom step on the road's edge, not its middle. Planned, the
    /// road is level with the foot over the pad, as at any end, within the grade and earthwork caps.
    /// </summary>
    [Fact]
    public void AnEndOpensOntoItsLandingPadWhereTheRoadComesSquareToIt()
    {
        var world = new MistRiver { Bank = 40f, NearFall = 0.8f, Biome = Heightmap.Biome.Meadows };
        _scope.WithWorld(world);
        var c = River();
        c.Kit = BridgeStructures.Kit.WoodRail;
        StructureEnds.End natural = StructureEnds.Natural(c, world).near;
        Assert.True(natural.Steps >= 3, $"{natural.Steps} stairs down the falling bank");
        float x = natural.Meet.x - 4f;
        var path = new List<Vector2> { new(x, 60f), new(x, 20f), new(x, 3f), c.FromBank, c.ToBank, new(60f, 0f), new(100f, 0f) };
        c.FromIndex = 3; c.ToIndex = 4;
        // Under a 1 m earthwork cap no flat approach fits: the stairs are laid down to the ground, or a metre over it.
        Assert.Null(StructureEnds.MeetEnds(path, new List<RoadCrossing> { c }, world, 1f, null));
        Assert.True(c.FromPad && !c.ToPad, "the near end opens onto its pad, the far end is met along its line");
        StructureEnds.End near = c.FromEnd!.Value;
        Assert.True(near.Steps >= natural.Steps - 1, $"{near.Steps} of the whole run's {natural.Steps} stairs laid");
        Assert.Equal(natural.Up, near.Up);
        Vector2 turn = near.Meet - near.Up * StructureEnds.PadTurn;
        Assert.Equal(new[] { new Vector2(x, 60f), new Vector2(x, 20f), turn, near.Meet }, path.Take(c.FromIndex + 1));
        var plans = RoadNetworkGenerator.PlanRoadPathWithCrossings(path, new List<RoadCrossing> { c }, 4f, null, null);
        Assert.True(plans != null, RoadSpatialGrid.LastRefusal);
        // The road's centreline, past the foot's own stub, never nearer the foot along the line than the turn: the
        // bottom step meets the road's edge.
        int across = 0;
        foreach (var r in plans!)
            for (int i = 0; i < r.Points.Count; i++)
            {
                Vector2 p = r.Points[i];
                float outward = Vector2.Dot(p - near.Meet, -near.Up), aside = Mathf.Abs(Vector2.Dot(p - near.Meet, new Vector2(-near.Up.y, near.Up.x)));
                if (aside > 0.5f && outward > -0.25f && Vector2.Distance(p, near.Meet) <= 12f)
                {
                    across++;
                    Assert.True(outward >= StructureEnds.PadTurn - 0.05f, $"the road across the pad at {p} is {outward:F2} m out from the foot {near.Meet}");
                }
                if (Vector2.Distance(p, near.Meet) <= StructureEnds.PadLength)
                {
                    Assert.True(Mathf.Abs(r.Heights[i] - near.MeetHeight) <= 0.05f, $"the pad at {p} stands {r.Heights[i]:F2}, the foot {near.MeetHeight:F2}");
                    Assert.True(Mathf.Abs(r.Heights[i] - world.GetHeight(p.x, p.y)) <= RoadConstants.TerrainDeltaMax, $"the pad at {p} past the earthwork cap");
                }
            }
        Assert.True(across > 0, "no road leaves the pad across the line");
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
    public void TheDeckTheEndsAndTheStairsAreTheRailedWoodenKits()
    {
        var (c, low) = Low();
        Assert.Equal(BridgeLayout.DeckHeight(c, low), BridgeStructures.DeckHeight(c, low));
        var (n, f) = BridgeStructures.Ends(c, low);
        var (wn, wf) = BridgeLayout.NaturalEnds(c, low);
        foreach (var (a, b) in new[] { (n, wn), (f, wf) })
        {
            Assert.Equal(b.Edge, a.Edge);
            Assert.Equal(b.Height, a.Height);
            Assert.Equal(b.Steps, a.Steps);
        }
        // The stairs, piece for piece, where a railed wooden bridge lays them, in the Dvergr stair.
        string Key(StructurePiece p) => FormattableString.Invariant($"{p.Position.x:F3} {p.Position.y:F3} {p.Position.z:F3} {p.YawDegrees:F2}");
        var wood = BridgeLayout.SolveRailed(c, low, 1, BridgeLayout.Rail).Where(p => p.Kind == StructurePieceKind.Stair).Select(Key).OrderBy(k => k).ToList();
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
    public void AMistlandsFordSpanIsLaidOnTheWoodenFootbridgesDeckAndStairs()
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
        // Its stairs where a railed footbridge lays them, so the road meets it as it meets any ford span.
        string Key(StructurePiece p) => FormattableString.Invariant($"{p.Position.x:F3} {p.Position.y:F3} {p.Position.z:F3} {p.YawDegrees:F2}");
        var wood = BridgeLayout.SolveRailed(ford, low, 1, BridgeLayout.Rail).Where(p => p.Kind == StructurePieceKind.Stair).Select(Key).OrderBy(k => k).ToList();
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

    /// <summary>
    /// The wood support ceiling is every wooden kit's (BridgeLayout.RailedMaxColumn): over a deep Mistlands river no bent's
    /// piles stand taller than it, deck to ground; the deck is carried over a run without bents only from bents within the
    /// free span of each other, and a longer run is left open, no plank over it.
    /// </summary>
    [Fact]
    public void NoPileColumnStandsOverTheWoodSupportCeiling()
    {
        var w = new MistRiver { Bank = 50f };
        _scope.WithWorld(w);
        RoadCrossing c = River();
        float deck = DvergrWoodBridge.DeckHeight(c, w);
        float length = BridgeLayout.BuiltLength(c.Width);
        Assert.True(deck - w.Bed > BridgeLayout.RailedMaxColumn, $"deck {deck:F1} m over a {w.Bed:F0} m bed");
        foreach (bool complete in new[] { true, false })
        {
            var pieces = complete ? DvergrWoodBridge.SolveComplete(c, w, 1) : DvergrWoodBridge.Solve(c, w, 1);
            var bentPiles = pieces.Where(p => p.Prefab == DvergrWoodBridge.Pile && Along(c, p) > -0.01f && Along(c, p) < length + 0.01f).ToList();
            Assert.NotEmpty(bentPiles);
            foreach (StructurePiece pile in bentPiles)
                Assert.True(pile.Position.y - DvergrWoodBridge.PileLength * 0.5f >= deck - BridgeLayout.RailedMaxColumn - DvergrWoodBridge.PileLength - 1e-3f,
                    $"a pile's foot at {pile.Position.y - DvergrWoodBridge.PileLength * 0.5f:F1} m under a deck at {deck:F1} m");
            var bentAlongs = bentPiles.Select(p => Along(c, p)).Distinct().OrderBy(a => a).ToList();
            foreach (StructurePiece plank in pieces.Where(p => p.Prefab == DvergrWoodBridge.Plank && p.Kind == StructurePieceKind.Deck))
            {
                float at = Along(c, plank);
                float before = bentAlongs.Where(a => a <= at + 1e-3f).DefaultIfEmpty(float.NegativeInfinity).Max();
                float after = bentAlongs.Where(a => a >= at - 1e-3f).DefaultIfEmpty(float.PositiveInfinity).Min();
                Assert.True(after - before <= BridgeLayout.FreeSpanMaxWidth + 1e-3f, $"a plank {at:F1} m along, between bents {before:F1} and {after:F1}");
            }
        }
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

    /// <summary>
    /// Every pile stands (the kit departs from the profile's falling columns, as the vanilla harbour keeps its piles), and a
    /// plank is lost at the profile's deck share (<see cref="BridgeLayout.BridgeRuin"/>), more toward mid-span, never two
    /// running: over 300 seeds about as many as the share says, a little under it for the planks put back.
    /// </summary>
    [Fact]
    public void EveryPileStandsAndAPlankIsLostAtTheDeckShareNeverTwoRunning()
    {
        var (c, low) = Low();
        float length = BridgeLayout.BuiltLength(c.Width);
        float[] bents = DvergrWoodBridge.BentAlongs(length);
        int n = bents.Length, planks = Mathf.RoundToInt(length / DvergrWoodBridge.PlankPitch);
        int lost = 0, could = 0;
        float expected = 0f;
        for (int s = 1; s <= 300; s++)
        {
            var pieces = DvergrWoodBridge.Solve(c, low, s);
            var complete = DvergrWoodBridge.SolveComplete(c, low, s);
            // Every bent but the navigation gap's stands: no pile falls, none leaves a stub, nothing lies on the bed.
            Assert.Equal(complete.Count(p => p.Prefab == DvergrWoodBridge.Pile && !DvergrWoodBridge.InNavigationGap(c, low, Along(c, p))),
                pieces.Count(p => p.Prefab == DvergrWoodBridge.Pile));
            Assert.DoesNotContain(pieces, p => p.Kind == StructurePieceKind.Debris);
            // Planks over the standing middle bays (the end bays keep every plank, the way on from the stairs).
            bool previousLost = false;
            for (int j = 0; j < planks; j++)
            {
                float a = (j + 0.5f) * DvergrWoodBridge.PlankPitch;
                int bay = Array.FindIndex(bents, b => b >= a) - 1;
                if (bay <= 0 || bay >= n - 2 || DvergrWoodBridge.InNavigationGap(c, low, bents[bay]) || DvergrWoodBridge.InNavigationGap(c, low, bents[bay + 1]))
                {
                    previousLost = false;
                    continue;
                }
                could++;
                expected += 1f - BridgeLayout.RuinProfile.Survives(
                    BridgeLayout.SpanSurvival(1f - Mathf.Abs(a - length * 0.5f) / (length * 0.5f)), BridgeLayout.BridgeRuin.DeckShare);
                bool gone = !pieces.Any(p => p.Kind == StructurePieceKind.Deck && Mathf.Abs(Along(c, p) - a) < 0.01f);
                Assert.False(gone && previousLost, $"seed {s}: two planks running lost at {a:F1} m");
                if (gone) lost++;
                previousLost = gone;
            }
        }
        Assert.True(could > 1500, $"only {could} planks sampled");
        Assert.True(lost >= expected * 0.8f && lost <= expected * 1.1f, FormattableString.Invariant($"{lost} planks lost, the share says {expected:F0}"));
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
                Assert.False(DvergrWoodBridge.InNavigationGap(c, low, Along(c, p)), $"{p.Prefab} at {Along(c, p):F1} m stands in the navigation gap");
        // The complete bridge fills it, as the wooden kit's does: it is a run of bays a player could close.
        Assert.Contains(DvergrWoodBridge.SolveComplete(c, low, 1), p => p.Kind == StructurePieceKind.Deck && DvergrWoodBridge.InNavigationGap(c, low, Along(c, p)));
    }

    [Fact]
    public void AFreeSpanHasBentsOnlyAtItsBanksAndStringersBankToBank()
    {
        var w = new MistRiver { Bank = 34f };
        _scope.WithWorld(w);
        // 11 m over an 11 m fairway: the boat's lane (9.8 m and its clearance) covers it all, so nothing stands in the water.
        RoadCrossing c = RoadCrossing.Between(new Vector2(-5.5f, 0f), new Vector2(5.5f, 0f), 26f, new Vector2(0f, 0f), 11f, CrossingKind.Bridge);
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

