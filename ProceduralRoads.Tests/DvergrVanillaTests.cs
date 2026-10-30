using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Both Dvergr kits against the vanilla prefabs, as read from the game's assets: the pieces' measured sizes, the harbour's stringers, and a piece's roll as
/// spawned.
/// </summary>
public sealed class DvergrVanillaTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public DvergrVanillaTests() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); }
    public void Dispose() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); _scope.Dispose(); }

    [Fact]
    public void TheViaductPiecesHaveTheirMeasuredSizes()
    {
        // Half-extents (along the deck, across it, up) at the turns the layout gives each piece, from each prefab's
        // colliders.
        Assert.Equal((1f, 1f, 4f), DvergrViaduct.Extents(DvergrViaduct.Footing));        // blackmarble_column_3 2 x 8 x 2
        Assert.Equal((1f, 1f, 1f), DvergrViaduct.Extents(DvergrViaduct.Block));          // blackmarble_2x2x2
        Assert.Equal((1f, 1f, 1f), DvergrViaduct.Extents(DvergrViaduct.Band));           // blackmarble_2x2_enforced
        Assert.Equal((1f, 1f, 1f), DvergrViaduct.Extents(DvergrViaduct.CorbelCorner));   // blackmarble_outcorner 2 x 2 x 2
        Assert.Equal((1f, 0.5f, 1f), DvergrViaduct.Extents(DvergrViaduct.Corbel));       // blackmarble_out_1 2 x 2 x 1
        Assert.Equal((1f, 0.5f, 1f), DvergrViaduct.Extents(DvergrViaduct.Filler));       // blackmarble_2x2x1
        Assert.Equal((1f, 0.5f, 1f), DvergrViaduct.Extents(DvergrViaduct.Arch));         // blackmarble_arch
        Assert.Equal((1f, 1f, 0.05f), DvergrViaduct.Extents(DvergrViaduct.Floor));       // iron_floor_2x2 2 x 0.1 x 2
        Assert.Equal((0.05f, 1f, 1f), DvergrViaduct.Extents(DvergrViaduct.Screen));      // iron_wall_2x2 2 x 2 x 0.1, turned 0/180
        Assert.Equal((1f, 1f, 1f), DvergrViaduct.Extents(DvergrViaduct.HeadLeft));       // blackmarble_head_big01 2 x 2 x 2
        Assert.Equal((1f, 1f, 1f), DvergrViaduct.Extents(DvergrViaduct.HeadRight));      // blackmarble_head_big02
    }

    [Fact]
    public void TheDvergrWoodStringersReachIntoThePlanksAsTheHarboursDo()
    {
        var w = new DvergrViaductTests.MistRiver { Bank = 34f };
        _scope.WithWorld(w);
        RoadCrossing c = DvergrViaductTests.River();
        float deck = BridgeLayout.DeckHeight(c, w);
        var pieces = DvergrWoodBridge.Solve(c, w, 5);
        float plank = Assert.Single(pieces.Where(p => p.Kind == StructurePieceKind.Deck).Select(p => p.Position.y).Distinct());
        Assert.Equal(deck - 0.4f, plank, 3);
        // Mistlands_Harbour1 fort (1)/pier: planks centred y 1.5, stringers y 1.0, both 0.8 m deep.
        Assert.All(pieces.Where(p => p.Kind == StructurePieceKind.Beam), p => Assert.Equal(plank - 0.5f, p.Position.y, 3));
    }
}


/// <summary>Mist lights on Dvergr wood bridges (only some bridges have them, and on those only some posts), mounted as Mistlands_Harbour1's: 0.5 m over a pile's top.</summary>
public sealed class DvergrWoodLightTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private readonly DvergrViaductTests.MistRiver _world = new() { Bank = 34f };
    private readonly RoadCrossing _crossing = DvergrViaductTests.River();

    public DvergrWoodLightTests() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); _scope.WithWorld(_world); }
    public void Dispose() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); _scope.Dispose(); }

    private static List<StructurePiece> Lights(List<StructurePiece> ps) => ps.Where(p => p.Prefab == DvergrWoodBridge.Light).ToList();

    [Fact]
    public void OnlySomeBridgesAndOnThemOnlySomePilesHaveMistLights()
    {
        int lit = 0, lights = 0;
        float harbour = 0f;
        const int seeds = 600;
        for (int s = 1; s <= seeds; s++)
        {
            var pieces = DvergrWoodBridge.Solve(_crossing, _world, s);
            var l = Lights(pieces);
            if (l.Count == 0) continue;
            lit++;
            // Every top pile of the bridge, where a light may stand.
            var topPiles = pieces.Where(p => p.Prefab == DvergrWoodBridge.Pile && p.Kind == StructurePieceKind.Post)
                .GroupBy(p => (Mathf.Round(p.Position.x * 100f), Mathf.Round(p.Position.z * 100f))).Select(g => g.OrderByDescending(p => p.Position.y).First())
                .Where(p => !DvergrWoodBridge.InNavigationGap(_crossing, _world, DvergrViaductTests.Along(_crossing, p))
                    && DvergrViaductTests.Along(_crossing, p) > -0.01f && DvergrViaductTests.Along(_crossing, p) < BridgeLayout.BuiltLength(_crossing.Width) + 0.01f).ToList();
            Assert.True(l.Count < topPiles.Count, "every pile lit");
            harbour += 1f + DvergrWoodBridge.LightChance * (topPiles.Count - 1); // the harbour's: one always, a third of the rest
            lights += l.Count;
        }
        // About one bridge in three (DvergrWoodBridge.LitBridgeChance) ...
        Assert.InRange(lit / (float)seeds, 1f / 3f - 0.06f, 1f / 3f + 0.06f);
        // ... and on those half the piles the harbour's RandomSpawn (one always, 33 % of the rest) would light: every
        // other one of them (DvergrWoodBridge.LightSpacing), the first always, so a little over half.
        Assert.InRange(lights / harbour, 0.5f - 0.04f, 0.5f + 0.1f);
    }

    [Fact]
    public void AMistLightStandsHalfAMetreOverAPilesTopTurnedAsThePile()
    {
        int checkedLights = 0;
        for (int s = 1; s <= 60; s++)
        {
            var pieces = DvergrWoodBridge.Solve(_crossing, _world, s);
            foreach (StructurePiece light in Lights(pieces))
            {
                Assert.Equal(StructurePieceKind.Flair, light.Kind);
                StructurePiece top = pieces.Where(p => p.Prefab == DvergrWoodBridge.Pile && Mathf.Abs(p.Position.x - light.Position.x) < 1e-3f
                    && Mathf.Abs(p.Position.z - light.Position.z) < 1e-3f).OrderByDescending(p => p.Position.y).First();
                // Mistlands_Harbour1 fort (1)/pier: dverger_demister (3) at y 5, its pile centred y 2.5 (4 m long, top 4.5).
                Assert.Equal(top.Position.y + DvergrWoodBridge.PileLength * 0.5f + 0.5f, light.Position.y, 3);
                Assert.Equal(top.YawDegrees, light.YawDegrees);
                Assert.Equal(0f, light.PitchDegrees);
                Assert.Equal(0f, light.RollDegrees);
                checkedLights++;
            }
        }
        Assert.True(checkedLights > 10);
    }

    [Fact]
    public void TheLightsAreFixedPerCrossingAndSeed()
    {
        string Sig(List<StructurePiece> ps) => string.Join("|", Lights(ps).Select(p => FormattableString.Invariant($"{p.Position.x:F2},{p.Position.y:F2},{p.Position.z:F2}/{p.HealthFraction:F3}")));
        for (int s = 1; s <= 30; s++)
        {
            Assert.Equal(Sig(DvergrWoodBridge.Solve(_crossing, _world, s)), Sig(DvergrWoodBridge.Solve(_crossing, _world, s)));
            // The complete bridge has the same lights (ruin takes none away, but the navigation gap's piles carry none).
            Assert.Equal(Lights(DvergrWoodBridge.Solve(_crossing, _world, s)).Select(p => p.Position),
                Lights(DvergrWoodBridge.SolveComplete(_crossing, _world, s)).Select(p => p.Position));
        }
        // Worn as every harbour piece (vanilla's location damage, 10-60 %).
        Assert.All(Enumerable.Range(1, 30).SelectMany(s => Lights(DvergrWoodBridge.Solve(_crossing, _world, s))),
            p => Assert.InRange(p.HealthFraction, BridgeLayout.LocationDamageMin, BridgeLayout.LocationDamageMax));
    }

    [Fact]
    public void AMistlandsFordSpanMayHaveThemTooAndARailedKitNever()
    {
        RoadCrossing ford = RoadCrossing.Between(new Vector2(-31f, 0f), new Vector2(31f, 0f), 26f, new Vector2(-6f, 0f), 20f, CrossingKind.Ford, FordStyle.Span);
        Assert.Equal(BridgeStructures.Kit.DvergrWood, BridgeStructures.KitFor(ford, _world));
        Assert.Contains(Enumerable.Range(1, 40), s => Lights(DvergrWoodBridge.Solve(ford, _world, s)).Count > 0);
        var meadows = new DvergrViaductTests.MistRiver { Bank = 34f, Biome = Heightmap.Biome.Meadows };
        _scope.WithWorld(meadows);
        Assert.All(Enumerable.Range(1, 40), s => Assert.Empty(Lights(BridgeStructures.Solve(_crossing, meadows, s))));
    }
}

/// <summary>Vines on Dvergr viaducts, hung as the vanilla viaduct hangs them (a vanilla viaduct has vines): each pier's eight curtains at 40 %, every vine on a stone face, turned as
/// vanilla's.</summary>
public sealed class DvergrViaductVineTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public DvergrViaductVineTests() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); }
    public void Dispose() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); _scope.Dispose(); }

    private (RoadCrossing c, WorldGenerator w) Valley()
    {
        var w = new DvergrViaductTests.MistValley();
        _scope.WithWorld(w);
        return (DvergrViaduct.ValleyCrossing(new Vector2(-24f, 0f), new Vector2(24f, 0f), w, out _)!, w);
    }

    private (RoadCrossing c, WorldGenerator w) River()
    {
        var w = new DvergrViaductTests.MistRiver();
        _scope.WithWorld(w);
        return (DvergrViaductTests.River(), w);
    }

    private static List<StructurePiece> Vines(List<StructurePiece> ps) => ps.Where(p => p.Prefab == DvergrViaduct.Vine).ToList();

    /// <summary>A vine on the bridge's axes relative to its nearest pier: (along, across, under the deck).</summary>
    private static (float du, float v, float dy) Local(RoadCrossing c, float deck, StructurePiece p, List<float> piers)
    {
        float along = DvergrViaductTests.Along(c, p);
        float pier = piers.OrderBy(a => Mathf.Abs(a - along)).First();
        Vector2 dir = c.Direction, side = new(-dir.y, dir.x);
        return (along - pier, Vector2.Dot(new Vector2(p.Position.x, p.Position.z) - c.FromBank, side), p.Position.y - deck);
    }

    [Theory]
    [InlineData("valley")]
    [InlineData("river")]
    public void EveryVineHangsOnAStoneFaceAndNoneIsInTheGround(string where)
    {
        var (c, w) = where == "river" ? River() : Valley();
        Assert.True(DvergrViaduct.Applies(c, w));
        Vector2 dir = c.Direction, side = new(-dir.y, dir.x);
        int vines = 0;
        for (int s = 1; s <= 20; s++)
        {
            var pieces = DvergrViaduct.Solve(c, w, s);
            var stone = pieces.Where(p => p.Kind != StructurePieceKind.Flair && p.Kind != StructurePieceKind.Stair).ToList();
            foreach (StructurePiece v in Vines(pieces))
            {
                vines++;
                Assert.Equal(StructurePieceKind.Flair, v.Kind);
                Assert.True(v.Position.y >= BiomeBlendedHeight.GetBlendedHeight(v.Position.x, v.Position.z, w) + 1.2f, $"vine at {v.Position} in the ground");
                // Its origin on (or within 0.2 m of) a stone piece's bounds: the box reaches into the stone, as vanilla's.
                Assert.Contains(stone, p =>
                {
                    var (hu, hv, hy) = DvergrViaduct.Extents(p.Prefab);
                    Vector2 d = new Vector2(v.Position.x - p.Position.x, v.Position.z - p.Position.z);
                    return Mathf.Abs(Vector2.Dot(d, dir)) <= hu + 0.2f && Mathf.Abs(Vector2.Dot(d, side)) <= hv + 0.2f && Mathf.Abs(v.Position.y - p.Position.y) <= hy + 0.2f;
                });
            }
        }
        Assert.True(vines > 0, "no vines in twenty seeds");
    }

    [Fact]
    public void TheVinesAreVanillasPierVinesTurnedAsVanillasAre()
    {
        var (c, w) = River();     // piers about 19 m: the curtains' lower vines too
        float deck = DvergrViaduct.DeckHeight(c, w), yaw = BridgeLayout.YawDegrees(c.Direction);
        List<float> piers = DvergrViaduct.PierAlongs(c, w);
        // A sample of Mistlands_Viaduct1's middle pier, read from the game's assets (GameObject/GameObject/pillar/vines*/vines*: x, y,
        // z, pitch, yaw, roll), on the bridge's axes (du = z - 0.25, v = -x, dy = y - 17).
        var sample = new (float du, float v, float dy, float pitch, float yaw, float roll)[]
        {
            (0.03f, -2f, -17f, 0f, 270f, 180f),      // vines (curtain 0): (2, 0, 0.28), yaw 270, roll 180
            (0.03f, -2f, -9f, 0f, 270f, 90f),        // vines: (2, 8, 0.28), roll 90, sideways
            (-3.55f, 0.1f, -6.96f, -26f, 0f, 180f),  // vines (2): (-0.1, 10.04, -3.3), pitch 334 on a corbel's slope
            (1.78f, -1f, -12.75f, 0f, 270f, 180f),   // vines (4): (1, 4.25, 2.03)
        };
        var all = Enumerable.Range(1, 40).SelectMany(s => Vines(DvergrViaduct.Solve(c, w, s))).ToList();
        foreach (var e in sample)
            Assert.Contains(all, v =>
            {
                var (du, vv, dy) = Local(c, deck, v, piers);
                float turn = ((v.YawDegrees - yaw) % 360f + 360f) % 360f;
                return Mathf.Abs(du - e.du) < 0.01f && Mathf.Abs(vv - e.v) < 0.01f && Mathf.Abs(dy - e.dy) < 0.01f
                    && Mathf.Abs(v.PitchDegrees - e.pitch) < 0.01f && Mathf.Abs(((turn - e.yaw) % 360f + 540f) % 360f - 180f) < 0.01f && Mathf.Abs(v.RollDegrees - e.roll) < 0.01f;
            });
        // Every vine: hanging (roll 180), sideways (90) or upright (0); level or pitched 26 degrees on a corbel; turned a
        // quarter at a time from the deck's heading (vanilla's faces).
        Assert.All(all, v =>
        {
            Assert.Contains(v.RollDegrees, new[] { 0f, 90f, 180f });
            Assert.Contains(v.PitchDegrees, new[] { 0f, -26f });
            float turn = ((v.YawDegrees - yaw) % 360f + 360f) % 360f;
            Assert.True(Mathf.Abs(turn - 90f * Mathf.Round(turn / 90f)) < 0.01f, $"turn {turn}");
        });
    }

    [Fact]
    public void ACurtainIsThereFourTimesInTenAndTheVinesAreFixedPerCrossingAndSeed()
    {
        var (c, w) = River();
        float deck = DvergrViaduct.DeckHeight(c, w);
        List<float> piers = DvergrViaduct.PierAlongs(c, w);
        string Sig(List<StructurePiece> ps) => string.Join("|", Vines(ps).Select(p => FormattableString.Invariant($"{p.Position.x:F2},{p.Position.y:F2},{p.Position.z:F2}/{p.YawDegrees:F1}/{p.RollDegrees:F0}/{p.HealthFraction:F3}")));
        int curtains = 0, could = 0;
        for (int s = 1; s <= 200; s++)
        {
            var pieces = DvergrViaduct.Solve(c, w, s);
            if (s <= 10) Assert.Equal(Sig(pieces), Sig(DvergrViaduct.Solve(c, w, s)));
            // A curtain is seen by its vine 12.75 m under the deck on a shaft face: one per curtain (vanilla's y 4.25).
            var marks = Vines(pieces).Where(v => Mathf.Abs(Local(c, deck, v, piers).dy + 12.75f) < 0.01f)
                .Select(v => (Mathf.Round(v.Position.x * 100f), Mathf.Round(v.Position.z * 100f))).Distinct().Count();
            curtains += marks;
            could += 8 * piers.Count(a => deck - DvergrViaduct.PierGround(c, a, w) >= 12.75f + 1.2f + 1f);
        }
        Assert.True(could > 400);
        Assert.InRange(curtains / (float)could, 0.4f - 0.05f, 0.4f + 0.05f);
        // Worn as every viaduct piece (vanilla's location damage).
        Assert.All(Vines(DvergrViaduct.Solve(c, w, 3)), v => Assert.InRange(v.HealthFraction, BridgeLayout.LocationDamageMin, BridgeLayout.LocationDamageMax));
    }

    [Fact]
    public void ARuinedCorbelTakesItsVinesWithIt()
    {
        // Every vine on the complete viaduct's corbels that the ruined one lacks the corbel of is gone too.
        var (c, w) = River();
        for (int s = 1; s <= 20; s++)
        {
            var pieces = DvergrViaduct.Solve(c, w, s);
            var stone = pieces.Where(p => p.Kind != StructurePieceKind.Flair).ToList();
            foreach (StructurePiece v in Vines(pieces).Where(v => v.PitchDegrees != 0f))
                Assert.Contains(stone, p => (p.Prefab == DvergrViaduct.Corbel || p.Prefab == DvergrViaduct.CorbelCorner || p.Prefab == DvergrViaduct.Block)
                    && Vector3.Distance(p.Position, v.Position) < 2.5f);
        }
    }
}
