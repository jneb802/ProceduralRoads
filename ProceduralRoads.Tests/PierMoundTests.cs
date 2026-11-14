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
/// no further, at most the kit's limit (BridgeSupport.MostMoundOf), never an island in water (StructureGround.MayRaise); the mound is stored
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
    /// x = 0, faces of 1.5 between. Sea past |x|, |z| = 200, so there is no way round.</summary>
    private sealed class DeepValley : WorldGenerator
    {
        public const float Top = 60f;
        public float Depth = 23.5f;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            return Mathf.Max(Top - Depth, Top - Mathf.Max(0f, 16f + Depth / 1.5f - Mathf.Abs(wx)) * 1.5f);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override int GetSeed() => 64;
    }

    private static float Bank(DeepValley v) => 16f + v.Depth / 1.5f + 2f;

    /// <summary>
    /// A valley whose floor lies 23.5 m under its flats: a viaduct at grade stands its piers 1.5 m over the ceiling. Before,
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

    /// <summary>A valley 28.5 m deep: its piers 6.5 m over the ceiling, more than a viaduct's mound makes up
    /// (RoadRules.ViaductMostMound): no viaduct, and no mound. Every other kit's mound keeps the global limit
    /// (BridgeSupport.MostMound).</summary>
    [Fact]
    public void NoMoundIsMadeOverItsMost()
    {
        var world = new DeepValley { Depth = 28.5f };
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

    /// <summary>A valley 26.5 m deep: its piers 4.5 m over the ceiling, within a viaduct's mound (RoadRules.ViaductMostMound,
    /// the maintainer's 5.5 m): the viaduct is laid on its mounds, and the support rule holds every piece on them.</summary>
    [Fact]
    public void AViaductPierUpToItsMoundOverTheCeilingStandsOnItsMound()
    {
        var world = new DeepValley { Depth = 26.5f };
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

    /// <summary>In water a mound keeps its top under the surface (RoadConstants.SeaLevel): one that would break it, an
    /// island, is never made; on dry ground the surface does not enter into it.</summary>
    [Fact]
    public void AMoundInWaterStaysUnderTheSurface()
    {
        float sea = RoadConstants.SeaLevel;
        Assert.True(StructureGround.MayRaise(sea - 3f, sea - 0.5f, BridgeStructures.Kit.DvergrViaduct));
        Assert.False(StructureGround.MayRaise(sea - 1f, sea, BridgeStructures.Kit.DvergrViaduct));
        Assert.False(StructureGround.MayRaise(sea - 1f, sea + 1f, BridgeStructures.Kit.DvergrViaduct));
        Assert.True(StructureGround.MayRaise(sea + 0.5f, sea + 2.5f, BridgeStructures.Kit.DvergrViaduct));
    }
}
