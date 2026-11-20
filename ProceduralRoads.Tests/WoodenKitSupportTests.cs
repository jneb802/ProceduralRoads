using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The wooden kits read like the stone kit (docs/OWNERS.md, a structure's final support and support per kit): every piece they
/// lay has its measured data (BuriedPieces.DataOf), the level ones a material the game's support rule reads; the bridge the
/// drawn wooden kit would lay is read by the rule before its kit is chosen (BridgeStructures.CheckSupport) and recorded; one
/// it breaks is built in another kit that fits, else not at all, on every path that asks the kit. Their level pieces' burial is
/// the one owner's (BuriedPieces.Buried).
/// </summary>
public sealed class WoodenKitSupportTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    // The fixtures here are kits' structure cases whose water only raises a deck (standing it just under the deck, over ground
    // above the sea) or lies under a valley deepened past the sea: none is water a ship is meant to sail, so no lane off a
    // river here (BridgeLayout.DeepWaterLanes; ShipGapTests has the lanes, river or not).
    public WoodenKitSupportTests()
    {
        BridgeLayout.DeepWaterLanes = false;
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        PlacedLocations.Reset();
    }

    public void Dispose()
    {
        BridgeLayout.DeepWaterLanes = true;
        PlacedLocations.Reset();
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    /// <summary>The railed kits' reference ground (RailedBridgeTests): a bed 28.9 m between -2 and 4, banks to 33.9 m at the
    /// deck ends, and outward of them <see cref="Bank"/> (by the distance from the deck's centre) or, where it reads,
    /// <see cref="Hill"/>.</summary>
    private sealed class World : WorldGenerator
    {
        private static readonly Vector2[] Profile =
            { new(-7f, 33.9f), new(-6f, 32.9f), new(-4f, 30.9f), new(-2f, 28.9f), new(4f, 28.9f), new(6f, 30.9f), new(7f, 33.9f) };
        public Func<float, float>? Bank;
        public Func<float, float, float>? Hill;
        public Heightmap.Biome Biome = Heightmap.Biome.Meadows;
        public int Seed = 76;

        public override float GetHeight(float wx, float wy)
        {
            if (Hill != null && Mathf.Abs(wx) > 6.5f && !float.IsNaN(Hill(wx, wy))) return Hill(wx, wy);
            if (Mathf.Abs(wx) > 6f && Bank != null) return Bank(Mathf.Abs(wx));
            float h = Profile[Profile.Length - 1].y;
            if (wx <= Profile[0].x) h = Profile[0].y;
            else
                for (int i = 1; i < Profile.Length; i++)
                    if (wx <= Profile[i].x) { h = Mathf.Lerp(Profile[i - 1].y, Profile[i].y, (wx - Profile[i - 1].x) / (Profile[i].x - Profile[i - 1].x)); break; }
            return h;
        }

        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Biome;
        public override int GetSeed() => Seed;
    }

    /// <summary>Banks falling away a little faster than a step to 6.5 m under the deck: seven steps down each end.</summary>
    private static float LongBank(float a) => Mathf.Max(34.9f - 0.58f * (a - 6f), 34.9f - 6.5f);

    /// <summary>The reference crossing: 12 m along +x, its deck at 34.9 m.</summary>
    private (RoadCrossing c, World w) Reference(World w)
    {
        _scope.WithWorld(w);
        var c = RoadCrossing.Between(new Vector2(-6f, 0f), new Vector2(6f, 0f), 28.9f, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
        c.WaterLevel = 34.9f - BridgeLayout.DeckFreeboard;
        return (c, w);
    }

    /// <summary>The first seed whose draw among the kits that fit is <paramref name="kit"/>.</summary>
    private static void Draw(RoadCrossing c, World w, BridgeStructures.Kit kit)
    {
        for (w.Seed = 1; BridgeLayout.PickKit(c, w.Seed, BridgeStructures.KitsThatFit(c, w)) != kit; w.Seed++)
            Assert.True(w.Seed < 200, $"no seed draws {kit}");
    }

    /// <summary>
    /// Every piece the wooden kits lay, complete and ruined, end stairs and railing included, has its measured data, as the
    /// Unity rip of the installed game has it and the game itself read back (Valheim 1.0.21): the posts, beams, plates and the staircases' level rails wood, the Dvergr
    /// pier's plank and pile iron (the game's own material), each a material the support rule reads, the stair rails and the
    /// braces as their pitched boxes; the stairs and the mist light none. On the parent the stair rails and braces had none.
    /// </summary>
    [Fact]
    public void EveryPieceTheWoodenKitsLayHasItsMeasuredData()
    {
        var (c, w) = Reference(new World { Bank = LongBank });
        var laid = new List<StructurePiece>();
        foreach (BridgeLayout.RailedKit kit in new[] { BridgeLayout.Rail, BridgeLayout.Braced })
        {
            laid.AddRange(BridgeLayout.SolveRailedComplete(c, w, w.Seed, kit));
            laid.AddRange(BridgeLayout.SolveRailed(c, w, w.Seed, kit));
        }
        w.Biome = Heightmap.Biome.Mistlands;
        for (int seed = 1; seed <= 6; seed++)
        {
            laid.AddRange(DvergrWoodBridge.SolveComplete(c, w, seed));
            laid.AddRange(DvergrWoodBridge.Solve(c, w, seed));
        }
        var expected = new Dictionary<string, SupportGraph.Material?>
        {
            ["wood_pole2"] = SupportGraph.Wood, ["wood_pole"] = SupportGraph.Wood, ["wood_beam"] = SupportGraph.Wood,
            ["wood_floor"] = SupportGraph.Wood, ["wood_stair"] = null, ["wood_beam_26"] = SupportGraph.Wood, ["wood_beam_67"] = SupportGraph.Wood,
            ["dvergrprops_wood_beam"] = SupportGraph.Iron, ["dvergrprops_wood_pole"] = SupportGraph.Iron,
            ["dvergrprops_wood_stair"] = null, ["dverger_demister"] = null,
        };
        Assert.Equal(expected.Keys.OrderBy(k => k), laid.Select(p => p.Prefab).Distinct().OrderBy(k => k));
        foreach (KeyValuePair<string, SupportGraph.Material?> entry in expected)
        {
            string prefab = entry.Key;
            SupportGraph.Material? material = entry.Value;
            Assert.True(BuriedPieces.DataOf(prefab, out BuriedPieces.Measured data), $"{prefab} has no measured data");
            Assert.True(data.Material.HasValue == material.HasValue && (!material.HasValue || data.Material!.Value.Max == material.Value.Max
                && data.Material.Value.Min == material.Value.Min), $"{prefab}: material max {data.Material?.Max} min {data.Material?.Min}");
        }
        // The staircases' railing: its 1 m level rail is wood too, its crossed rail two pitched boxes of wood.
        Assert.True(BuriedPieces.DataOf(StairRailing.ShortRailPrefab, out BuriedPieces.Measured shortRail) && shortRail.Material?.Max == SupportGraph.Wood.Max);
        Assert.True(BuriedPieces.DataOf(StairRailing.CrossedRailPrefab, out BuriedPieces.Measured crossed) && crossed.Material?.Max == SupportGraph.Wood.Max
            && crossed.Pitched?.Length == 2);
    }

    /// <summary>
    /// A wooden kit's drawn candidate is read by the support rule like the stone kit's, and the calibration record says so:
    /// its measured ceiling's margin as its estimate beside the rule's reading of its pieces, complete and ruined. The railing
    /// posts down the end stairs rest on the stairs' stringers, which the rule reads as their pitched boxes: every one is
    /// judged. On the parent the record said "full not read (no measured support data)".
    /// </summary>
    [Theory]
    [InlineData(BridgeStructures.Kit.WoodRail)]
    [InlineData(BridgeStructures.Kit.WoodBraced)]
    public void AWoodenCandidateIsReadByTheRuleBeforeItsKitIsChosen(BridgeStructures.Kit kit)
    {
        var (c, w) = Reference(new World { Bank = LongBank });
        Draw(c, w, kit);
        var lines = new List<string>();
        BepInEx.Logging.ManualLogSource.Captured = lines;
        try
        {
            Assert.Equal(kit, BridgeStructures.KitFor(c, w));
            string record = Assert.Single(lines, l => l.Contains($"[SUPPORT] {kit}"));
            Assert.Contains("m under the measured ceiling", record);
            Assert.Contains("full holds 0 of", record);
            Assert.DoesNotContain("falling against pieces not read", record);
            Assert.Contains("response built", record);
            BridgeStructures.SupportCheck check = BridgeStructures.CheckSupport(c, w, kit);
            Assert.True(check.FullRead && check.Holds && check.Pieces > 100 && check.Unjudged == 0, BridgeStructures.SupportLine(c, check, "test"));
        }
        finally { BepInEx.Logging.ManualLogSource.Captured = null; }
    }

    [Theory]
    [InlineData(BridgeStructures.Kit.WoodRail, false)]
    [InlineData(BridgeStructures.Kit.WoodBraced, false)]
    [InlineData(BridgeStructures.Kit.DvergrWood, false)]
    [InlineData(BridgeStructures.Kit.WoodRail, true)]
    [InlineData(BridgeStructures.Kit.WoodBraced, true)]
    [InlineData(BridgeStructures.Kit.DvergrWood, true)]
    public void TheSupportRuleReadsTheVariantTheBuilderLays(BridgeStructures.Kit kit, bool weathered)
    {
        using var wear = Valheim.Testing.StaticOverride.Keep(() => StructureWear.Weathered);
        StructureWear.Weathered = weathered ? WeatheredStructures.Bridges : WeatheredStructures.None;
        var (c, w) = Reference(new World { Bank = LongBank,
            Biome = kit == BridgeStructures.Kit.DvergrWood ? Heightmap.Biome.Mistlands : Heightmap.Biome.Meadows });
        c.Kit = kit;
        var built = BridgeStructures.Solve(c, w, w.Seed);
        Assert.NotEmpty(built);
        Assert.All(built, p => Assert.Equal(kit, p.Kit));
        var repaired = BridgeStructures.SolveComplete(c, w, w.Seed);
        var held = new List<ViaductSupport.Held>(ViaductSupport.Of(c, w, repaired));
        held.AddRange(ViaductSupport.Of(c, w, built));
        var expected = BridgeStructures.Read(kit, true, 0f, "", held);
        var actual = BridgeStructures.CheckSupport(c, w, kit);
        Assert.Equal(expected.Pieces, actual.Pieces);
        Assert.Equal(expected.Falling, actual.Falling);
        Assert.Equal(expected.Unjudged, actual.Unjudged);
        Assert.Equal(expected.Weakest, actual.Weakest);
        Assert.Equal(expected.WeakestPrefab, actual.WeakestPrefab);
    }

    /// <summary>The support rule's verdict on a piece it reads as falling only against a piece it does not read (a wooden stair,
    /// no material: ViaductSupport.Held.ThroughUnread) is none (BridgeStructures.Read): counted apart, neither falling nor the
    /// weakest share, so a bridge whose only falling piece is such a one holds, and its record says it was not judged; a piece
    /// falling on its own breaks it. The kits' readings above judge every piece they lay (no such piece), so this is the
    /// unread path's own test: with it counted as falling, every other test passed.</summary>
    [Fact]
    public void APieceFallingOnlyAgainstAPieceTheRuleDoesNotReadIsNoVerdict()
    {
        StructurePiece Beam(float x) => new() { Kind = StructurePieceKind.Beam, Prefab = "wood_beam_26", Position = new Vector3(x, 30f, 0f) };
        float min = SupportGraph.Wood.Min, over = ViaductSupport.Overread("wood_beam_26");
        var standing = new ViaductSupport.Held(Beam(0f), 2f * min * over, min);
        var unread = new ViaductSupport.Held(Beam(2f), 0.5f * min * over, min, throughUnread: true);
        var falling = new ViaductSupport.Held(Beam(4f), 0.25f * min * over, min);
        Assert.True(unread.Falls && falling.Falls && !standing.Falls);

        BridgeStructures.SupportCheck held = BridgeStructures.Read(BridgeStructures.Kit.WoodRail, true, 1f, "m", new[] { standing, unread });
        Assert.True(held.Holds, "a piece falling only against an unread one broke the bridge");
        Assert.Equal((0, 1), (held.Falling, held.Unjudged));
        Assert.Equal(2f, held.Weakest, 3);
        var crossing = RoadCrossing.Between(new Vector2(-5f, 0f), new Vector2(5f, 0f), 28f, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
        Assert.Contains("1 falling against pieces not read, no verdict", BridgeStructures.SupportLine(crossing, held, "test"));

        BridgeStructures.SupportCheck broken = BridgeStructures.Read(BridgeStructures.Kit.WoodRail, true, 1f, "m", new[] { standing, unread, falling });
        Assert.True(broken.Breaks);
        Assert.Equal((1, 1), (broken.Falling, broken.Unjudged));
        Assert.Equal(0.25f, broken.Weakest, 3);
    }

    /// <summary>
    /// The pitched pieces are read as their measured boxes (BuriedPieces.DataOf, the rip's colliders): turned as the game lays
    /// them, the box round their pitched boxes is the bounds measured in game (cli_piece_geometry) to 2 mm, at any yaw. On the
    /// parent they had no boxes and no material.
    /// </summary>
    [Theory]
    [InlineData("wood_beam_26")]
    [InlineData("wood_beam_67")]
    [InlineData("wood_wall_roof_top")]
    public void APitchedPieceIsReadAsItsMeasuredBoxes(string prefab)
    {
        Assert.True(BuriedPieces.DataOf(prefab, out BuriedPieces.Measured data));
        Assert.NotNull(data.Pitched);
        Assert.Equal(SupportGraph.Wood.Max, data.Material!.Value.Max);
        var at = new Vector3(10f, 40f, -5f);
        var piece = new SupportGraph.Piece(at, data.ComOffset, 0f, data.Pitched!, SupportGraph.Wood, false);
        Assert.Equal(data.Min.x, piece.Min.x - at.x, 2);
        Assert.Equal(data.Min.y, piece.Min.y - at.y, 2);
        Assert.Equal(data.Min.z, piece.Min.z - at.z, 2);
        Assert.Equal(data.Max.x, piece.Max.x - at.x, 2);
        Assert.Equal(data.Max.y, piece.Max.y - at.y, 2);
        Assert.Equal(data.Max.z, piece.Max.z - at.z, 2);
        // Turned a quarter, its local x along the world's z: the same bounds across.
        var turned = new SupportGraph.Piece(at, data.ComOffset, 90f, data.Pitched!, SupportGraph.Wood, false);
        Assert.Equal(data.Max.x - data.Min.x, turned.Max.z - turned.Min.z, 2);
        Assert.Equal(data.Max.y - data.Min.y, turned.Max.y - turned.Min.y, 2);
    }

    /// <summary>
    /// The sloped contact test (SupportGraph.OnTerrain on a pitched box, through the support rule's own reading,
    /// StairSupport.Holding): a brace (wood_beam_67) on level ground is grounded, at its material's most, while the lowest
    /// corner of its box grown 0.15 m on its own axes is in the ground, and carried by nothing a hair over it, though the box
    /// round it (which a level box's test reads) is wholly over the ground there. On the parent the brace was not read.
    /// </summary>
    [Fact]
    public void APitchedBeamIsGroundedWhereItsOwnBoxMeetsTheGround()
    {
        Assert.True(BuriedPieces.DataOf(BridgeLayout.BracePrefab, out BuriedPieces.Measured data));
        SupportGraph.Piece probe = new(Vector3.zero, data.ComOffset, 30f, data.Pitched!, SupportGraph.Wood, false);
        // The grown box's lowest corner under the origin: under the box round it, grown.
        float lowest = float.PositiveInfinity;
        foreach (BuriedPieces.PitchedBox b in data.Pitched!)
        {
            float t = b.PitchDegrees * Mathf.Deg2Rad;
            lowest = Mathf.Min(lowest, b.Centre.y - Mathf.Abs(Mathf.Sin(t)) * (b.Half.x + SupportGraph.Reach) - Mathf.Abs(Mathf.Cos(t)) * (b.Half.y + SupportGraph.Reach));
        }
        Assert.True(lowest < probe.Min.y - SupportGraph.Reach - 0.02f, $"grown corner {lowest:F3}, bounds {probe.Min.y:F3}");
        Func<int, int, float> flat = (x, z) => 20f;
        foreach (float over in new[] { -0.02f, 0.02f })
        {
            var brace = new StructurePiece { Kind = StructurePieceKind.Beam, Prefab = BridgeLayout.BracePrefab, Position = new Vector3(0.3f, 20f - lowest + over, 0.6f), YawDegrees = 30f };
            ViaductSupport.Held held = Assert.Single(StairSupport.Holding(new[] { brace }, flat));
            Assert.Equal(over < 0f ? SupportGraph.Wood.Max : 0f, held.Support);
            Assert.False(BuriedPieces.Buried(brace.Prefab, brace.Position, brace.YawDegrees, flat));
        }
        // Wholly in the ground: buried, and not grounded.
        var deep = new StructurePiece { Kind = StructurePieceKind.Beam, Prefab = BridgeLayout.BracePrefab, Position = new Vector3(0.3f, 20f - 5f, 0.6f), YawDegrees = 30f };
        Assert.True(BuriedPieces.Buried(deep.Prefab, deep.Position, deep.YawDegrees, flat));
        Assert.Equal(0f, Assert.Single(StairSupport.Holding(new[] { deep }, flat)).Support);
    }

    /// <summary>
    /// A braced bridge's braces and its end stairs' stringers and handrails are read by the support rule (their pitched boxes),
    /// on the candidate the builder reads (BridgeStructures.CheckSupport through ViaductSupport.Of): every one has its support,
    /// and a piece read as falling against a piece the rule does not read touches an end stair, none a brace or a stair rail
    /// only. On the parent none of them was read.
    /// </summary>
    [Fact]
    public void ABracedBridgesBracesAndStairRailsAreReadByTheRule()
    {
        var (c, w) = Reference(new World { Bank = LongBank });
        List<StructurePiece> whole = BridgeLayout.SolveRailedComplete(c, w, w.Seed, BridgeLayout.Braced);
        List<ViaductSupport.Held> held = ViaductSupport.Of(c, w, whole);
        foreach (string prefab in new[] { BridgeLayout.BracePrefab, BridgeLayout.StairRailPrefab })
        {
            int laid = whole.Count(p => p.Prefab == prefab);
            Assert.True(laid > 0, prefab);
            Assert.Equal(laid, held.Count(h => h.Piece.Prefab == prefab));
        }
        Assert.Equal(whole.Count(p => p.Prefab != BridgeLayout.StairPrefab), held.Count);
        BridgeStructures.SupportCheck check = BridgeStructures.CheckSupport(c, w, BridgeStructures.Kit.WoodBraced);
        Assert.True(check.FullRead && check.Holds, BridgeStructures.SupportLine(c, check, "test"));
    }

    /// <summary>A Meadows canyon 34 m wide, its floor at <see cref="Floor"/>, between banks at 33.9 m.</summary>
    private sealed class Canyon : WorldGenerator
    {
        public float Floor;
        public int Seed = 76;
        public override float GetHeight(float wx, float wy) => Mathf.Abs(wx) < 17f ? Floor : 33.9f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override int GetSeed() => Seed;
    }

    /// <summary>A 40 m crossing over <see cref="Canyon"/>, its deck at 34.9 m.</summary>
    private (RoadCrossing c, Canyon w) OverTheCanyon(float floor)
    {
        var w = new Canyon { Floor = floor };
        _scope.WithWorld(w);
        var c = RoadCrossing.Between(new Vector2(-20f, 0f), new Vector2(20f, 0f), floor, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
        c.WaterLevel = 34.4f;
        return (c, w);
    }

    /// <summary>
    /// A wooden bridge the rule breaks is not built in its kit, as a stone one is not (BridgeStructures.KitAmong): another kit
    /// that fits, read the same way, else none. Over a canyon floor 15.4 m under the deck, under the measured ceiling (18 m),
    /// both railed kits are read broken (handrails and railing posts over mid-span) and in the Meadows no other kit fits: the
    /// drawn kit's record says it breaks beside the ceiling's margin ("estimate optimistic") and that it was refused, and
    /// every path that asks the kit finds none (the kit, the solve's pieces, the kit saved at the first build). On the parent
    /// the drawn kit was built ("response built (a wooden kit's reading is not answered yet)").
    /// </summary>
    [Theory]
    [InlineData(BridgeStructures.Kit.WoodRail)]
    [InlineData(BridgeStructures.Kit.WoodBraced)]
    public void AWoodenBridgeTheRuleBreaksIsRefusedOnEveryPath(BridgeStructures.Kit drawn)
    {
        var lines = new List<string>();
        BepInEx.Logging.ManualLogSource.Captured = lines;
        try
        {
            var (c, w) = OverTheCanyon(19.5f);
            for (w.Seed = 1; BridgeLayout.PickKit(c, w.Seed, BridgeStructures.KitsThatFit(c, w)) != drawn; w.Seed++)
                Assert.True(w.Seed < 200);
            Assert.Equal(new[] { BridgeStructures.Kit.WoodRail, BridgeStructures.Kit.WoodBraced }, BridgeStructures.KitsThatFit(c, w));
            foreach (BridgeStructures.Kit kit in BridgeStructures.KitsThatFit(c, w))
            {
                BridgeStructures.SupportCheck read = BridgeStructures.CheckSupport(c, w, kit);
                Assert.True(read.EstimateFits && read.Breaks, BridgeStructures.SupportLine(c, read, "test"));
            }
            Assert.Null(BridgeStructures.KitFor(c, w));
            string record = Assert.Single(lines, l => l.Contains("[SUPPORT]"));
            Assert.Contains($"[SUPPORT] {drawn}", record);
            Assert.Contains("full breaks", record);
            Assert.Contains("estimate optimistic", record);
            Assert.Contains("response refused, no kit", record);
            Assert.Empty(BridgeStructures.Solve(c, w, w.Seed));
            Assert.False(BridgeStructures.ChooseKit(c, w, new[] { c }));
            Assert.False(BridgeStructures.Chosen(c.Kit));
        }
        finally { BepInEx.Logging.ManualLogSource.Captured = null; }
    }

    /// <summary>
    /// A wooden bridge the rule breaks is built in another kit that fits, read the same way (BridgeStructures.KitAmong): over a
    /// canyon floor 11.9 m under the deck the rule breaks pieces of the braced kit and holds the railed one. Drawn braced,
    /// the record is the braced candidate's and says it was refused and built in WoodRail, and every path that asks the kit
    /// builds the railed one (the kit, the solve's pieces, the kit saved at the first build); drawn railed, it is built as
    /// drawn. On the parent the braced bridge was built ("response built (a wooden kit's reading is not answered yet)").
    /// </summary>
    [Fact]
    public void AWoodenBridgeTheRuleBreaksIsBuiltInAnotherKitThatFits()
    {
        var lines = new List<string>();
        BepInEx.Logging.ManualLogSource.Captured = lines;
        try
        {
            var (c, w) = OverTheCanyon(23f);
            // A world seed that draws the braced kit, the rule breaking it and holding the railed one (the ruin is seeded too).
            for (w.Seed = 1; BridgeLayout.PickKit(c, w.Seed, BridgeStructures.KitsThatFit(c, w)) != BridgeStructures.Kit.WoodBraced
                     || !BridgeStructures.CheckSupport(c, w, BridgeStructures.Kit.WoodBraced).Breaks
                     || !BridgeStructures.CheckSupport(c, w, BridgeStructures.Kit.WoodRail).Holds; w.Seed++)
                Assert.True(w.Seed < 200);
            Assert.Equal(BridgeStructures.Kit.WoodRail, BridgeStructures.KitFor(c, w));
            string record = Assert.Single(lines, l => l.Contains("[SUPPORT]"));
            Assert.Contains("[SUPPORT] WoodBraced", record);
            Assert.Contains("full breaks", record);
            Assert.Contains("response refused, built in WoodRail", record);
            List<StructurePiece> pieces = BridgeStructures.Solve(c, w, w.Seed);
            Assert.NotEmpty(pieces);
            Assert.All(pieces, p => Assert.Equal(BridgeStructures.Kit.WoodRail, p.Kit));
            Assert.True(BridgeStructures.ChooseKit(c, w, new[] { c }));
            Assert.Equal(BridgeStructures.Kit.WoodRail, c.Kit);

            lines.Clear();
            var (railed, rw) = OverTheCanyon(23f);
            for (rw.Seed = 1; BridgeLayout.PickKit(railed, rw.Seed, BridgeStructures.KitsThatFit(railed, rw)) != BridgeStructures.Kit.WoodRail
                     || !BridgeStructures.CheckSupport(railed, rw, BridgeStructures.Kit.WoodRail).Holds; rw.Seed++)
                Assert.True(rw.Seed < 200);
            Assert.Equal(BridgeStructures.Kit.WoodRail, BridgeStructures.KitFor(railed, rw));
            Assert.Contains("response built", Assert.Single(lines, l => l.Contains("[SUPPORT] WoodRail")));
        }
        finally { BepInEx.Logging.ManualLogSource.Captured = null; }
    }

    /// <summary>
    /// One burial owner for a railed bridge's level pieces (BuriedPieces.Buried, decision Q5: nothing wholly buried, the rest
    /// shown): a hillside rising steeply across the end stairs' left side stands 0.3 m over the railing posts' tops at their
    /// middles but falls away under their downhill faces, so the game grounds them; each is laid exactly where the one owner
    /// keeps it, and at least one the old point test (the ground at the post's middle over its top by the growth) left out is
    /// now laid. No level beam or post laid is wholly in the ground.
    /// </summary>
    [Fact]
    public void ARailedEndsPostsAndLevelBeamsAreBuriedByTheOneOwner()
    {
        // The posts' tops down the run: 35.9 m at the deck's end (6 m out), a metre lower each 2 m step.
        static float PostTop(float x) => 35.9f - (Mathf.Abs(x) - 6f) * 0.5f;
        var (c, w) = Reference(new World
        {
            Bank = LongBank,
            Hill = (x, z) => z > 1.5f ? PostTop(x) + 0.3f + 3f * (z - 2f) : float.NaN,
        });
        Func<Vector2, float> ground = StructureGround.At(w);
        Func<int, int, float> vertex = (x, z) => ground(new Vector2(x, z));
        var (near, far) = StructureEnds.Of(c, w);
        foreach (BridgeLayout.RailedKit kit in new[] { BridgeLayout.Rail, BridgeLayout.Braced })
        {
            List<StructurePiece> plan = BridgeLayout.SolveRailedComplete(c, w, 1, kit);
            Assert.DoesNotContain(plan, p => (p.Prefab == BridgeLayout.BeamPrefab || p.Prefab == BridgeLayout.RailPostPrefab)
                && BuriedPieces.Buried(p.Prefab, p.Position, p.YawDegrees, vertex));
            int keptByTheOwnerOnly = 0;
            foreach (StructureEnds.End end in new[] { near, far })
                for (int k = 1; k <= end.Steps; k++)
                {
                    Vector2 at = StructureEnds.StepJoint(end.Edge, end.Up, k) + new Vector2(0f, BridgeLayout.DeckHalfWidth);
                    var post = new Vector3(at.x, end.Height - k * StairLayout.PieceRise + BridgeLayout.RailHeight * 0.5f, at.y);
                    bool laid = plan.Any(p => p.Prefab == BridgeLayout.RailPostPrefab && Vector3.Distance(p.Position, post) < 0.01f);
                    bool buried = BuriedPieces.Buried(BridgeLayout.RailPostPrefab, post, BridgeLayout.YawDegrees(c.Direction) + 90f, vertex);
                    Assert.True(laid == !buried, $"{kit.Name}: post at {post} laid {laid}, buried {buried}");
                    if (laid && ground(at) >= post.y + BridgeLayout.RailHeight * 0.5f + SupportGraph.Reach) keptByTheOwnerOnly++;
                }
            Assert.True(keptByTheOwnerOnly > 0, $"{kit.Name}: no post on the hillside the old point test would have left out");
        }
    }
}
