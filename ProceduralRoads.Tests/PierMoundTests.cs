using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A mound under a structure's limiting pier (StructureGround.Mound): a viaduct whose piers stand a little over the pier
/// ceiling (DvergrViaduct.PierCeiling) is built on mounds under them, the ground raised to the ceiling under the deck and
/// no further, at most the kit's limit (BridgeSupport.MostMoundOf), above or under water alike (StructureGround.MayRaise); the mound is stored
/// with the road as mound points (ground, not road: it clears no vegetation and no rock and reaches nothing), and the
/// terrain writer and the finished ground every structure reads agree on it.
/// </summary>
public sealed class PierMoundTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public PierMoundTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
    }

    public void Dispose()
    {
        RoadNetworkGenerator.PieceCommitted = null;
        SetPathfinder(null);
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, pathfinder);

    /// <summary>A Mistlands valley along z: flats at <see cref="Top"/>, a floor <see cref="Depth"/> under them within 16 m of
    /// x = 0, faces of <see cref="Face"/> between. Sea past |x|, |z| = 200, so there is no way round.</summary>
    private sealed class DeepValley : WorldGenerator
    {
        public const float Top = 70f;
        public float Depth = 27.5f;
        /// <summary>The faces' slope (1.5 unless a case needs its banks on the flats at the rim).</summary>
        public float Face = 1.5f;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            return Mathf.Max(Top - Depth, Top - Mathf.Max(0f, 16f + Depth / Face - Mathf.Abs(wx)) * Face);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override int GetSeed() => 64;
    }

    private static float Bank(DeepValley v) => 16f + v.Depth / v.Face + 2f;

    /// <summary>
    /// A valley whose floor lies 27.5 m under its flats: a viaduct at grade stands its piers 1.5 m over the ceiling. Before,
    /// no viaduct (the search's valley jump and the kind refused it); now the road is generated over a viaduct on mounds:
    /// under each pier over the ceiling the ground its shafts and screens stand on, raised to the ceiling under the deck,
    /// stored as mound points, in the finished ground the viaduct is laid on, and the viaduct laid on it
    /// (BridgeStructures.Solve, read by the support rule).
    /// </summary>
    [Fact]
    public void AViaductAMetreAndAHalfOverThePierCeilingIsBuiltOnMounds()
    {
        var world = new DeepValley();
        _scope.WithWorld(world);
        float bank = Bank(world);
        RoadCrossing? jump = DvergrViaduct.ValleyCrossing(new Vector2(-bank, 0f), new Vector2(bank, 0f), world, out string why);
        Assert.True(jump != null, $"the valley jump is refused: {why}");
        Assert.True(DvergrViaduct.TallestPier(jump!, world) > DvergrViaduct.PierCeiling, "the case does not need a mound");

        SetPathfinder(new RoadPathfinder(world) { Stairs = true, Viaducts = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(-110f, 0f, 0f), 0f, new Vector3(110f, 0f, 0f), 0f, 4f, "across"), "no road");
        RoadCrossing c = Assert.Single(RoadNetworkGenerator.GetRoadCrossings(), x => x.ValleyJump);
        Assert.True(BridgeStructures.IsViaduct(c, world));
        float deck = DvergrViaduct.DeckHeight(c, world), top = deck - DvergrViaduct.PierCeiling;
        List<StructureGround.Mound> mounds = DvergrViaduct.Mounds(c, world);
        Assert.NotEmpty(mounds);
        Func<int, int, float> finished = StructureGround.Vertices(world);
        foreach (StructureGround.Mound m in mounds)
        {
            Assert.Equal(top, m.Top);
            foreach (Vector2 p in m.Points)
            {
                // Stored as a mound at its top: levelled by the writer, not walked, not road.
                Assert.Contains(RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(p.x, 0f, p.y), 0.01f), rp => rp.mound && rp.Levelled && !rp.IsRoad && !rp.Walked && !rp.unpainted && rp.h == top);
                // Every vertex of the mesh cell round it at the top (the writer's flat), so the game's mesh reads it there.
                foreach (var (x, z) in new[] { (Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y)), (Mathf.CeilToInt(p.x), Mathf.CeilToInt(p.y)) })
                    Assert.Equal(top, finished(x, z), 3);
                Assert.True(top - BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world) <= RoadRules.ViaductMostMound + 1e-3f);
            }
        }
        List<StructurePiece> pieces = BridgeStructures.Solve(c, world, world.GetSeed());
        Assert.NotEmpty(pieces);
        // Its piers stand on the mounds: no pier's piece reaches below the mound's top by more than its sunk footing.
        float lowestShaftTop = pieces.Where(p => p.Kind == StructurePieceKind.ViaductPier && p.Prefab != DvergrViaduct.Footing).Min(p => p.Position.y);
        Assert.True(lowestShaftTop >= top - 1f, $"a pier's block at {lowestShaftTop:F2} m, under the mound at {top:F2} m");
    }

    /// <summary>
    /// The mound's ground in the terrain writer and in the finished ground every structure and the support rule read
    /// (RoadTerrainModifier.FinishedGround): a mound stored as a generated road stores it (RoadNetworkGenerator.CrossingGround.CommitGround)
    /// or as a manual road's append does (RoadNetworkGenerator.Footprint's WithFills, RoadSpatialGrid.CommitAppend), stored
    /// as ground, not road, written into its zone and read back vertex by vertex, the same; its top flat at the mound's
    /// height, and the same ground by either path.
    /// </summary>
    [Fact]
    public void TheWriterAndTheFinishedGroundAgreeOnAMound()
    {
        float[] generated = WriteAMound(manual: false), appended = WriteAMound(manual: true);
        Assert.Equal(generated, appended);
    }

    /// <summary>The mound of <see cref="TheWriterAndTheFinishedGroundAgreeOnAMound"/> stored by one path, written and checked;
    /// the heights written into its zone.</summary>
    private float[] WriteAMound(bool manual)
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        var world = new DeepValley();
        _scope.WithWorld(world);
        float floor = DeepValley.Top - world.Depth, top = floor + 2f;
        var mound = new StructureGround.Mound(new List<Vector2> { new(-2f, 0.3f), new(2f, 0.3f), new(0f, -0.7f), new(0f, 1.3f), new(-1f, 0.3f), new(1f, 0.3f) }, top);
        Assert.True(StructureGround.MayRaise(floor, top, BridgeStructures.Kit.DvergrViaduct));
        var ground = new RoadNetworkGenerator.CrossingGround();
        ground.Mounds.Add(mound);
        if (manual)
        {
            RoadSpatialGrid.FinalizeRoadNetwork();
            var append = RoadNetworkGenerator.Footprint(new List<RoadSpatialGrid.PlannedPath>(), Array.Empty<RoadStairSpan>(), null, ground,
                Enumerable.Empty<RoadSpatialGrid.PlannedPath>(), Enumerable.Empty<RoadSpatialGrid.PlannedPath>());
            RoadSpatialGrid.CommitAppend(append.WithFills, RoadSpatialGrid.RoadNetworkVersion);
        }
        else
        {
            ground.CommitGround();
            RoadSpatialGrid.FinalizeRoadNetwork();
        }
        var zone = new Vector2s(0, 0);
        // Ground, not road: every reader that asks where the road is passes it over.
        Assert.All(RoadSpatialGrid.GetRoadPointsInZone(zone), rp => Assert.False(rp.IsRoad, $"the mound at {rp.p} is stored as road"));
        Heightmap hm = Heightmap.CreateForZone(zone);
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, RoadSpatialGrid.GetRoadPointsInZone(zone), hm, hm.m_terrainComp!);
        TerrainComp tc = hm.m_terrainComp!;
        int grid = tc.m_width + 1, raised = 0;
        Func<int, int, float> finished = RoadTerrainModifier.FinishedGround((x, z) => BiomeBlendedHeight.GetBlendedHeight(x, z, world));
        for (int vz = 0; vz < grid; vz++)
            for (int vx = 0; vx < grid; vx++)
            {
                int x = vx - tc.m_width / 2, z = vz - tc.m_width / 2, i = vz * grid + vx;
                float written = BiomeBlendedHeight.GetBlendedHeight(x, z, world) + (tc.m_modifiedHeight[i] ? tc.m_levelDelta[i] : 0f);
                Assert.Equal(written, finished(x, z), 3);
                if (written > floor + 0.5f && Mathf.Abs(x) <= 4 && Mathf.Abs(z) <= 4) raised++;
            }
        Assert.True(raised > 0, "nothing raised");
        foreach (Vector2 p in mound.Points)
            Assert.Equal(top, BuriedPieces.MeshHeight(p.x, p.y, finished), 3);
        // Nothing painted: a mound is no road a player sees.
        Assert.DoesNotContain(true, tc.m_modifiedPaint);
        var heights = new float[grid * grid];
        for (int i = 0; i < heights.Length; i++) heights[i] = tc.m_modifiedHeight[i] ? tc.m_levelDelta[i] : 0f;
        return heights;
    }

    /// <summary>A mound is ground, not road: the rock clearing (RockClearances.Near, the one owner of which rocks a road
    /// clears) gives it no headroom box, stored by generation or by a manual road's append; a road point beside it still
    /// gets one. Before, every mound point was a road box under the deck.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMoundGetsNoRockBox(bool manual)
    {
        var world = new DeepValley();
        _scope.WithWorld(world);
        float top = DeepValley.Top - world.Depth + 2f;
        var ground = new RoadNetworkGenerator.CrossingGround();
        ground.Mounds.Add(new StructureGround.Mound(new List<Vector2> { new(-1f, 0f), new(0f, 0f), new(1f, 0f) }, top));
        // A road 20 m away, the control.
        RoadSpatialGrid.CommitLevelled(new[] { new Vector2(0f, 20f), new Vector2(0f, 21f) }, new[] { top, top }, 4f);
        if (manual)
        {
            RoadSpatialGrid.FinalizeRoadNetwork();
            var append = RoadNetworkGenerator.Footprint(new List<RoadSpatialGrid.PlannedPath>(), Array.Empty<RoadStairSpan>(), null, ground,
                Enumerable.Empty<RoadSpatialGrid.PlannedPath>(), Enumerable.Empty<RoadSpatialGrid.PlannedPath>());
            RoadSpatialGrid.CommitAppend(append.WithFills, RoadSpatialGrid.RoadNetworkVersion);
        }
        else
        {
            ground.CommitGround();
            RoadSpatialGrid.FinalizeRoadNetwork();
        }
        bool Ground(Vector3 at, out float height) { height = top; return true; }
        Assert.Empty(RockClearances.Near(new Vector3(0f, 0f, 0f), 3f, Ground, out int misses));
        Assert.Equal(0, misses);
        Assert.NotEmpty(RockClearances.Near(new Vector3(0f, 0f, 20.5f), 1f, Ground, out _));
    }

    /// <summary>A valley 32.5 m deep: its piers 6.5 m over the ceiling, more than a viaduct's mound makes up
    /// (RoadRules.ViaductMostMound): no viaduct, and no mound. Every other kit's mound keeps the global limit
    /// (BridgeSupport.MostMound).</summary>
    [Fact]
    public void NoMoundIsMadeOverItsMost()
    {
        var world = new DeepValley { Depth = 32.5f };
        _scope.WithWorld(world);
        float bank = Bank(world);
        Assert.Null(DvergrViaduct.ValleyCrossing(new Vector2(-bank, 0f), new Vector2(bank, 0f), world, out string why));
        Assert.Contains("ceiling", why);
        var probe = RoadCrossing.Between(new Vector2(-bank, 0f), new Vector2(bank, 0f), DeepValley.Top - world.Depth, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
        probe.ValleyJump = true;
        Assert.Null(DvergrViaduct.Mounds(probe, world, DeepValley.Top));
        Assert.Empty(DvergrViaduct.Mounds(probe, world));
        Assert.True(StructureGround.MayRaise(40f, 40f + RoadRules.ViaductMostMound, BridgeStructures.Kit.DvergrViaduct));
        Assert.False(StructureGround.MayRaise(40f, 40f + RoadRules.ViaductMostMound + 0.01f, BridgeStructures.Kit.DvergrViaduct));
        Assert.True(StructureGround.MayRaise(40f, 40f + BridgeSupport.MostMound, BridgeStructures.Kit.Stone));
        Assert.False(StructureGround.MayRaise(40f, 40f + BridgeSupport.MostMound + 0.01f, BridgeStructures.Kit.Stone));
        Assert.False(StructureGround.MayRaise(float.NaN, 40f, BridgeStructures.Kit.DvergrViaduct));
    }

    /// <summary>A valley 30.5 m deep: its piers 4.5 m over the ceiling, within a viaduct's mound (RoadRules.ViaductMostMound,
    /// the maintainer's 5.5 m): the viaduct is laid on its mounds, and the support rule holds every piece on them.</summary>
    [Fact]
    public void AViaductPierUpToItsMoundOverTheCeilingStandsOnItsMound()
    {
        var world = new DeepValley { Depth = 30.5f };
        _scope.WithWorld(world);
        float bank = Bank(world);
        RoadCrossing? jump = DvergrViaduct.ValleyCrossing(new Vector2(-bank, 0f), new Vector2(bank, 0f), world, out string why);
        Assert.True(jump != null, $"the valley jump is refused: {why}");
        Assert.True(DvergrViaduct.TallestPier(jump!, world) > DvergrViaduct.PierCeiling + BridgeSupport.MostMound, "the case is within the global mound");

        SetPathfinder(new RoadPathfinder(world) { Stairs = true, Viaducts = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(-110f, 0f, 0f), 0f, new Vector3(110f, 0f, 0f), 0f, 4f, "across"), "no road");
        RoadCrossing c = Assert.Single(RoadNetworkGenerator.GetRoadCrossings(), x => x.ValleyJump);
        Assert.True(BridgeStructures.IsViaduct(c, world));
        float top = DvergrViaduct.DeckHeight(c, world) - DvergrViaduct.PierCeiling;
        List<StructureGround.Mound> mounds = DvergrViaduct.Mounds(c, world);
        Assert.NotEmpty(mounds);
        Assert.Contains(mounds, m => m.Points.Any(p => top - BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world) > BridgeSupport.MostMound));
        foreach (StructureGround.Mound m in mounds)
            foreach (Vector2 p in m.Points)
                Assert.True(top - BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world) <= RoadRules.ViaductMostMound + 1e-3f);
        // The support rule holds every piece on the mounds.
        Assert.NotEmpty(BridgeStructures.Solve(c, world, world.GetSeed()));
        Assert.False(DvergrViaduct.Refuses(c, world, world.GetSeed()), "the support rule refuses it");
    }

    /// <summary>A ship's way (DvergrViaduct.ShipWayUnder) viaduct whose piers would stand 28 m, 2 m over the ceiling, on a bed
    /// under the water: each such pier stands on a mound of the bed's ground up to the ceiling under the deck
    /// (deck - PierCeiling; water is no limit, the top may rise above the surface). On the bare bed the support rule refuses it (the screens' enforced
    /// bands under the margin); once its road's ground is stored as generation stores it (RoadNetworkGenerator.GroundOf,
    /// CommitGround), the finished ground is the mound's top under each such pier and the support rule holds every piece,
    /// whole and ruined, as a valley viaduct's (AViaductPierUpToItsMoundOverTheCeilingStandsOnItsMound).</summary>
    [Theory]
    [InlineData(28f, false)]
    [InlineData(30.5f, true)]
    public void AShipWayViaductPierOverTheCeilingStandsOnAMoundInTheWater(float pier, bool breaksTheSurface)
    {
        var river = new DvergrViaductTests.MistRiver { Bank = 26f + pier, Bed = 26f };
        _scope.WithWorld(river);
        RoadCrossing c = DvergrViaductTests.River();
        Assert.NotNull(DvergrViaduct.ShipWayUnder(c, river));
        Assert.True(BridgeStructures.IsViaduct(c, river));
        float tallest = DvergrViaduct.TallestPier(c, river);
        Assert.InRange(tallest, DvergrViaduct.PierCeiling + 1f, DvergrViaduct.PierCeiling + RoadRules.ViaductMostMound);
        Assert.Null(DvergrViaduct.Refusal(c, river));
        float top = DvergrViaduct.DeckHeight(c, river) - DvergrViaduct.PierCeiling;
        Assert.Equal(breaksTheSurface, RoadWater.AtOrOverSea(top));
        List<StructureGround.Mound> mounds = DvergrViaduct.Mounds(c, river);
        Assert.NotEmpty(mounds);
        foreach (StructureGround.Mound m in mounds)
        {
            Assert.Equal(top, m.Top, 3);
            foreach (Vector2 p in m.Points)
                Assert.InRange(top - BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, river), 0f, RoadRules.ViaductMostMound + 1e-3f);
        }
        // The mound is what holds it: on the bare bed the support rule refuses it.
        Assert.True(DvergrViaduct.Refuses(c, river, river.GetSeed()), "held on the bare bed: the case does not need its mound");
        // Its road's ground stored as generation stores it; the finished ground every structure reads is the mound's top.
        RoadNetworkGenerator.CrossingGround ground = RoadNetworkGenerator.GroundOf(new List<RoadCrossing> { c }, say: false);
        Assert.Equal(mounds.Count, ground.Mounds.Count);
        ground.CommitGround();
        RoadSpatialGrid.FinalizeRoadNetwork();
        Func<int, int, float> finished = StructureGround.Vertices(river);
        foreach (Vector2 p in mounds.SelectMany(m => m.Points))
            Assert.Equal(top, finished(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y)), 3);
        foreach (bool ruin in new[] { false, true })
        {
            List<StructurePiece> laid = DvergrViaduct.Solve(c, river, river.GetSeed(), ruin, out DvergrViaduct.HoldReport report, say: false);
            Assert.True(laid.Count > 0, $"ruin {ruin}: refused, {report.Unheld.Count} carrying piece(s) under the margin");
            Assert.Empty(report.Unheld);
        }
        Assert.False(DvergrViaduct.Refuses(c, river, river.GetSeed()), "the support rule refuses it on its mounds");
    }

    /// <summary>The most a valley viaduct stands: piers at the ceiling (DvergrViaduct.PierCeiling, 26 m) on the full mound
    /// under them (RoadRules.ViaductMostMound, 5.5 m), the deck about 31.5 m over the natural ground (faces of 6, so the banks
    /// stand on the flats at the rim, where a valley of faces of 1.5 is jumped lower down its faces). The search's probe
    /// offers it, the road is generated over it, its mounds reach the whole limit, and the support rule holds it whole
    /// and ruined; a valley a little deeper than the two together is no viaduct.</summary>
    [Theory]
    [InlineData(31f)]
    [InlineData(31.45f)]
    public void AValleyViaductOnCeilingPiersOverTheFullMoundIsOfferedAndHeld(float depth)
    {
        var world = new DeepValley { Depth = depth, Face = 6f };
        _scope.WithWorld(world);
        float bank = Bank(world);
        RoadCrossing? jump = DvergrViaduct.ValleyCrossing(new Vector2(-bank, 0f), new Vector2(bank, 0f), world, out string why);
        Assert.True(jump != null, $"the valley jump is refused: {why}");
        Assert.True(DvergrViaduct.TallestPier(jump!, world) > DvergrViaduct.PierCeiling + RoadRules.ViaductMostMound - 0.6f,
            $"the case stands {DvergrViaduct.TallestPier(jump!, world):F2} m, short of the ceiling and the full mound");

        SetPathfinder(new RoadPathfinder(world) { Stairs = true, Viaducts = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(-110f, 0f, 0f), 0f, new Vector3(110f, 0f, 0f), 0f, 4f, "across"), "no road");
        RoadCrossing c = Assert.Single(RoadNetworkGenerator.GetRoadCrossings(), x => x.ValleyJump);
        Assert.True(BridgeStructures.IsViaduct(c, world));
        float deck = DvergrViaduct.DeckHeight(c, world), top = deck - DvergrViaduct.PierCeiling;
        Assert.True(deck - (DeepValley.Top - depth) > depth - 0.01f, $"the deck {deck:F2} m stands under the valley's depth");
        float deepest = DvergrViaduct.Mounds(c, world).SelectMany(m => m.Points).Max(p => top - BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world));
        Assert.InRange(deepest, RoadRules.ViaductMostMound - 0.6f, RoadRules.ViaductMostMound + 1e-3f);
        foreach (bool ruin in new[] { false, true })
        {
            List<StructurePiece> laid = DvergrViaduct.Solve(c, world, world.GetSeed(), ruin, out DvergrViaduct.HoldReport report, say: false);
            Assert.True(laid.Count > 0, $"ruin {ruin}: refused, {report.Unheld.Count} carrying piece(s) under the margin");
            Assert.Empty(report.Unheld);
        }
        Assert.False(DvergrViaduct.Refuses(c, world, world.GetSeed()), "the support rule refuses it");

        var deeper = new DeepValley { Depth = DvergrViaduct.PierCeiling + RoadRules.ViaductMostMound + 0.1f, Face = 6f };
        _scope.WithWorld(deeper);
        float far = Bank(deeper);
        Assert.Null(DvergrViaduct.ValleyCrossing(new Vector2(-far, 0f), new Vector2(far, 0f), deeper, out string over));
        Assert.Contains("ceiling", over);
    }

    /// <summary>Water is no limit to a mound: ground under the surface may be raised to above it, up to the kit's limit.</summary>
    [Fact]
    public void AMoundInWaterMayRiseAboveTheSurface()
    {
        float sea = RoadConstants.SeaLevel;
        Assert.True(StructureGround.MayRaise(sea - 3f, sea - 0.5f, BridgeStructures.Kit.DvergrViaduct));
        Assert.True(StructureGround.MayRaise(sea - 1f, sea, BridgeStructures.Kit.DvergrViaduct));
        Assert.True(StructureGround.MayRaise(sea - 1f, sea + 1f, BridgeStructures.Kit.DvergrViaduct));
        Assert.True(StructureGround.MayRaise(sea - 1f, sea - 1f + RoadRules.ViaductMostMound, BridgeStructures.Kit.DvergrViaduct));
        Assert.False(StructureGround.MayRaise(sea - 1f, sea - 1f + RoadRules.ViaductMostMound + 0.01f, BridgeStructures.Kit.DvergrViaduct));
        Assert.True(StructureGround.MayRaise(sea + 0.5f, sea + 2.5f, BridgeStructures.Kit.DvergrViaduct));
    }
}
