using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// No piece is placed wholly under the ground: the game finds no terrain in its grown collider box and breaks it
/// once it weighs it, dropping its resources (supports fully buried pop,
/// leaving rocks behind; <see cref="BuriedPieces"/> for the decompiled rule).
/// </summary>
[Collection("StairLayout")]
public class BuriedPiecesTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    public void Dispose()
    {
        RoadSpatialGrid.Clear();
        StairStructures.Reset();
        _scope.Dispose();
    }

    /// <summary>A grid of whole-metre vertex heights from x0, z0, row by row in z; NaN off it.</summary>
    private static Func<int, int, float> Grid(int x0, int z0, int w, float[] heights) => (x, z) =>
    {
        int i = x - x0, k = z - z0;
        return i < 0 || k < 0 || i >= w || k >= heights.Length / w ? float.NaN : heights[k * w + i];
    };

    /// <summary>The live terrain round two test pillars (cli_ground_height at the vertices).</summary>
    private static readonly Func<int, int, float> TestPillarsGround = Grid(-2301, 1574, 7, new[]
    {
        245.573f, 245.774f, 245.942f, 246.010f, 245.932f, 245.394f, 244.746f,
        244.142f, 244.351f, 244.690f, 244.851f, 244.642f, 243.832f, 243.163f,
        242.715f, 242.677f, 243.006f, 243.219f, 242.998f, 242.269f, 241.782f,
        241.560f, 241.315f, 241.279f, 241.345f, 241.165f, 240.609f, 240.255f,
        240.302f, 240.003f, 239.910f, 239.837f, 239.551f, 239.029f, 238.736f,
        239.026f, 238.823f, 239.011f, 238.885f, 238.564f, 238.270f, 237.949f,
        238.063f, 237.915f, 238.075f, 237.975f, 237.833f, 237.585f, 236.800f,
    });

    /// <summary>
    /// A stone_pillar placed at (-2299, 240.51, 1576), its top 1.5 m under the ground, broke about
    /// 30 s on and left its Stone on the pillar placed over it at 242.51, which stood. One at (-2298, 238.54, 1578),
    /// its top 0.3 m under the ground at its middle but out of it on the downhill side, stood.
    /// </summary>
    [Fact]
    public void ThePillarTheGameBrokeIsBuriedAndTheOnesThatStoodAreNot()
    {
        Assert.True(BuriedPieces.Buried("stone_pillar", new Vector3(-2299f, 240.51f, 1576f), 0f, TestPillarsGround));
        Assert.False(BuriedPieces.Buried("stone_pillar", new Vector3(-2299f, 242.51f, 1576f), 0f, TestPillarsGround));
        // Its middle is under the ground (239.84 over its top at 239.54) ...
        Assert.True(BuriedPieces.MeshHeight(-2298f, 1578f, TestPillarsGround) > 238.54f + 1f);
        // ... but its grown footprint comes out of it downhill.
        Assert.False(BuriedPieces.Buried("stone_pillar", new Vector3(-2298f, 238.54f, 1578f), 0f, TestPillarsGround));
    }

    /// <summary>The live terrain at the foot of the staircase to Hildir_cave -2304,1536, the road's fill on it.</summary>
    private static readonly Func<int, int, float> FrostCaveFootGround = Grid(-2309, 1565, 8, new[]
    {
        258.558f, 257.693f, 256.759f, 256.357f, 256.320f, 256.325f, 256.349f, 257.105f,
        257.575f, 257.436f, 256.259f, 255.912f, 255.613f, 255.866f, 255.980f, 256.096f,
        256.336f, 256.246f, 255.719f, 255.609f, 255.613f, 255.613f, 255.634f, 255.633f,
        254.788f, 254.968f, 255.133f, 255.243f, 255.260f, 255.281f, 255.296f, 255.253f,
        253.157f, 253.513f, 254.527f, 254.868f, 254.903f, 254.936f, 254.963f, 254.821f,
        251.999f, 252.777f, 254.184f, 254.482f, 254.544f, 254.596f, 254.642f, 254.242f,
        251.010f, 252.530f, 253.911f, 254.091f, 254.189f, 254.268f, 254.336f, 253.368f,
        250.427f, 252.493f, 253.568f, 253.722f, 253.850f, 253.956f, 254.047f, 252.175f,
    });

    /// <summary>
    /// The staircase's foot: two stone 2x2 floors with a pillar column under each, laid over the natural
    /// ground, which the road's fill then raised 1-2 m. The pillar under the floor at -2304.7,1567.6 is gone and the
    /// Stone of each column's buried pillar lies on its floor; the top pillar under the other floor, and the stair's
    /// columns beside it, out of the ground downhill, stand.
    /// </summary>
    [Fact]
    public void TheFootColumnsTheRoadsFillBuriedAreTheOnesThatBroke()
    {
        Assert.True(BuriedPieces.Buried("stone_pillar", new Vector3(-2304.732f, 253.763f, 1567.577f), 337.5f, FrostCaveFootGround));
        Assert.True(BuriedPieces.Buried("stone_pillar", new Vector3(-2305.497f, 251.763f, 1569.425f), 337.5f, FrostCaveFootGround));
        Assert.False(BuriedPieces.Buried("stone_pillar", new Vector3(-2305.497f, 253.763f, 1569.425f), 337.5f, FrostCaveFootGround));
        Assert.False(BuriedPieces.Buried("stone_pillar", new Vector3(-2307.345f, 252.763f, 1568.660f), 247.5f, FrostCaveFootGround));
        Assert.False(BuriedPieces.Buried("stone_pillar", new Vector3(-2307.345f, 254.763f, 1568.660f), 247.5f, FrostCaveFootGround));
        Assert.False(BuriedPieces.Buried("stone_floor_2x2", new Vector3(-2304.732f, 255.263f, 1567.577f), 337.5f, FrostCaveFootGround));
        Assert.False(BuriedPieces.Buried("stone_stair", new Vector3(-2307.345f, 255.763f, 1568.660f), 67.5f, FrostCaveFootGround));
    }

    /// <summary>The game's terrain is two flat triangles a cell, split from (x+1, z) to (x, z+1); not bilinear.</summary>
    [Fact]
    public void TheGroundIsTheGamesTriangles()
    {
        Func<int, int, float> corner = (x, z) => x == 1 && z == 1 ? 10f : 0f;
        Assert.Equal(2f, BuriedPieces.MeshHeight(0.6f, 0.6f, corner), 4);   // bilinear: 3.6
        Assert.Equal(0f, BuriedPieces.MeshHeight(0.4f, 0.4f, corner), 4);   // bilinear: 1.6
        // A valley along the split: the lowest over a box is where its edge crosses it, at none of its corners.
        Func<int, int, float> valley = (x, z) => x == z ? 10f : 0f;
        var box = new[] { new Vector2(0.3f, 0.55f), new Vector2(0.4f, 0.55f), new Vector2(0.4f, 0.75f), new Vector2(0.3f, 0.75f) };
        foreach (Vector2 c in box) Assert.True(BuriedPieces.MeshHeight(c.x, c.y, valley) >= 0.5f - 1e-4f);
        Assert.Equal(0f, BuriedPieces.RangeOver(box, valley).lowest, 4);
    }

    /// <summary>A piece is buried only where its box grown by the game's 0.15 m is wholly in the ground: the ground over its
    /// grown footprint higher than its top by more than the growth everywhere, on any heading; unknown ground or an unknown
    /// piece never.</summary>
    [Fact]
    public void BuriedMeansUnderTheGroundAllOverTheGrownFootprint()
    {
        // A slope rising 1 in 2 to the north (+z).
        Func<int, int, float> slope = (x, z) => 0.5f * z;
        // A 1 x 1 x 2 pillar at the origin, top 1 m up: the ground over its grown footprint runs -0.325 .. 0.325, so it is
        // buried with its top under -0.475.
        Assert.False(BuriedPieces.Buried("stone_pillar", new Vector3(0f, -0.4f, 0f), 0f, slope));
        Assert.False(BuriedPieces.Buried("stone_pillar", new Vector3(0f, -1.45f, 0f), 0f, slope));
        Assert.True(BuriedPieces.Buried("stone_pillar", new Vector3(0f, -1.5f, 0f), 0f, slope));
        // Turned 45 degrees its grown corners reach further downhill: 0.65 * sqrt 2 = 0.92 m, 0.46 m lower.
        Assert.False(BuriedPieces.Buried("stone_pillar", new Vector3(0f, -1.55f, 0f), 45f, slope));
        Assert.True(BuriedPieces.Buried("stone_pillar", new Vector3(0f, -1.65f, 0f), 45f, slope));
        Assert.False(BuriedPieces.Buried("no_such_piece", new Vector3(0f, -50f, 0f), 0f, slope));
        Assert.False(BuriedPieces.Buried("stone_pillar", new Vector3(0f, -50f, 0f), 0f, (x, z) => x > 0 ? float.NaN : 0f));
        // Every kit's pieces are known.
        foreach (StairKit kit in StairKit.All)
        {
            Assert.True(BuriedPieces.Known(kit.Stair));
            Assert.True(BuriedPieces.Known(kit.Floor));
            foreach (FillerKind f in kit.Fillers) Assert.True(BuriedPieces.Known(f.Prefab));
        }
        // And every piece StairSupport lays.
        foreach (string support in new[] { "stone_wall_2x1", "stone_wall_1x1", "stone_arch", "blackmarble_2x1x1", "blackmarble_out_1", "blackmarble_outcorner" })
            Assert.True(BuriedPieces.Known(support));
    }

    /// <summary>
    /// A piece flush with the ground (its top at the surface, as a viaduct's deck end module laid flush with its bank), or into
    /// it by no more than the game's support growth, is grounded by the game and stands: not buried, so it is laid (by the
    /// burial test, <see cref="BuriedPieces.Buried"/>, and by the one way a structure leaves buried pieces out,
    /// <see cref="BuriedPieces.Omit"/>). Past the growth it touches none of the mesh: buried, and alone it falls, so it is left out.
    /// </summary>
    [Fact]
    public void AFlushPieceIsLaidAndOneInTheGroundPastTheGrowthIsLeftOut()
    {
        Func<int, int, float> flat = (_, _) => 10f;
        foreach (var (into, buried) in new[] { (0f, false), (0.14f, false), (0.16f, true) })
        {
            // blackmarble_floor: its box half a metre over and under its origin.
            var floor = new StructurePiece { Kind = StructurePieceKind.ViaductDeck, Prefab = "blackmarble_floor", Position = new Vector3(0.3f, 9.5f - into, 0.2f) };
            Assert.Equal(buried, BuriedPieces.Buried(floor.Prefab, floor.Position, 0f, flat));
            var laid = new List<StructurePiece> { floor };
            List<StructurePiece> left = BuriedPieces.Omit(laid, flat, pieces => StairSupport.Holding(pieces, flat));
            Assert.Equal(buried ? 1 : 0, left.Count);
            Assert.Equal(buried ? 0 : 1, laid.Count);
        }
    }

    /// <summary>
    /// A buried piece the structure's own reading has falling with everything laid (<see cref="ViaductSupport.Held.Falls"/>) is
    /// left out whatever it carries (<see cref="BuriedPieces.Omit"/>): the game breaks it, and a piece it carries falls with it,
    /// so a piece that stands with it laid and falls without it does not keep it. Here a buried floor falls in every reading and
    /// the floor over it stands only while it is laid: it is left out, the one over it laid. With that rule bypassed every test
    /// passed (the buried floor was kept for the one it carries); this is its own.
    /// </summary>
    [Fact]
    public void ABuriedPieceFallingWithEverythingLaidIsLeftOutWhateverItCarries()
    {
        Func<int, int, float> flat = (_, _) => 10f;
        // blackmarble_floor: its box half a metre over and under its origin; the lower one wholly in the ground at 10.
        var under = new StructurePiece { Kind = StructurePieceKind.ViaductDeck, Prefab = "blackmarble_floor", Position = new Vector3(0.3f, 8f, 0.2f) };
        var over = new StructurePiece { Kind = StructurePieceKind.ViaductDeck, Prefab = "blackmarble_floor", Position = new Vector3(0.3f, 20f, 0.2f) };
        Assert.True(BuriedPieces.Buried(under.Prefab, under.Position, 0f, flat));
        Assert.False(BuriedPieces.Buried(over.Prefab, over.Position, 0f, flat));
        // The structure's reading: the buried floor always falls; the one over it stands only on it.
        List<ViaductSupport.Held> Reading(IReadOnlyList<StructurePiece> laid) => laid.Select(p => new ViaductSupport.Held(p,
            p == under || !laid.Contains(under) ? 0f : 1000f, 1f)).ToList();
        Assert.True(Reading(new[] { under, over })[0].Falls && !Reading(new[] { under, over })[1].Falls && Reading(new[] { over })[0].Falls);

        var laid = new List<StructurePiece> { under, over };
        List<StructurePiece> left = BuriedPieces.Omit(laid, flat, Reading);
        Assert.Equal(new[] { under }, left);
        Assert.Equal(new[] { over }, laid);
    }

    private sealed class SlopeWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => 30f + 0.25f * wy;
    }

    /// <summary>
    /// The finished ground the pass reads is what the terrain writer leaves: a road and a staircase's cut over a
    /// slope, written into a zone, and read back vertex by vertex.
    /// </summary>
    [Fact]
    public void TheFinishedGroundIsWhatTheTerrainWriterLeaves()
    {
        WorldGenerator world = new SlopeWorld();
        _scope.WithWorld(world);
        RoadSpatialGrid.Clear();
        RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(-30f, -10f), new(30f, 10f) }, 4f, world);
        RoadSpatialGrid.FinalizeRoadNetwork();
        RoadSpatialGrid.CommitAppendCuts(new[] { new Vector3(-8f, 25f, 12f), new Vector3(-7f, 25f, 13f) }, StairLayout.CutRadius * 2f, RoadSpatialGrid.RoadNetworkVersion);
        var zone = new Vector2s(0, 0);
        Heightmap hm = Heightmap.CreateForZone(zone);
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, RoadSpatialGrid.GetRoadPointsInZone(zone), hm, hm.m_terrainComp!);
        TerrainComp tc = hm.m_terrainComp!;
        int grid = tc.m_width + 1;
        Func<int, int, float> finished = RoadTerrainModifier.FinishedGround((x, z) => BiomeBlendedHeight.GetBlendedHeight(x, z, world));
        int changed = 0;
        for (int vz = 0; vz < grid; vz++)
            for (int vx = 0; vx < grid; vx++)
            {
                int x = vx - tc.m_width / 2, z = vz - tc.m_width / 2, i = vz * grid + vx;
                float written = BiomeBlendedHeight.GetBlendedHeight(x, z, world) + (tc.m_modifiedHeight[i] ? tc.m_levelDelta[i] : 0f);
                if (tc.m_modifiedHeight[i]) changed++;
                Assert.Equal(written, finished(x, z), 3);
            }
        Assert.True(changed > 100, $"only {changed} vertices written");
        Assert.True(finished(-8, 12) < BiomeBlendedHeight.GetBlendedHeight(-8f, 12f, world) - 1f, "the cut is in it");
    }

    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => 30f;
    }

    /// <summary>
    /// A column carried down past the ground (as for a road's cut beside it that was never made) loses the pillar
    /// wholly under the finished ground and keeps the one through the surface; where a staircase's cut does lower
    /// the ground there, both stay. A cut never lowers the ground under a walk (StairStructures.CutFloor), so the second
    /// column stands beside its run's floor, not under it.
    /// </summary>
    [Fact]
    public void TheNetworkDropsTheBuriedPillarsOnlyWhereNoCutUncoversThem()
    {
        WorldGenerator world = new FlatWorld();
        _scope.WithWorld(world);
        RoadSpatialGrid.Clear();
        RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(300f, 300f), new(340f, 300f) }, 4f, world);
        RoadSpatialGrid.FinalizeRoadNetwork();
        StairStructures.Reset();
        StairRun Column(float x, float floorOff = 0f)
        {
            var run = new StairRun { From = new Vector2(x, 0f), To = new Vector2(x, 4f), Width = 2f };
            run.Pieces.Add(new StructurePiece { Kind = StructurePieceKind.StairFloor, Prefab = "stone_floor_2x2", Position = new Vector3(x + floorOff, 30.7f, 0f) });
            run.Pieces.Add(new StructurePiece { Kind = StructurePieceKind.StairSupport, Prefab = "stone_pillar", Position = new Vector3(x, 29.2f, 0f) });
            run.Pieces.Add(new StructurePiece { Kind = StructurePieceKind.StairSupport, Prefab = "stone_pillar", Position = new Vector3(x, 27.2f, 0f) });
            StairStructures.Add(run);
            return run;
        }
        StairRun open = Column(0f), cut = Column(20f, floorOff: 4f);
        // Cut to 27.5 m round the second column: its lower pillar's top at 28.2 m is out of the ground there.
        var cuts = new List<Vector3>();
        for (float x = 17f; x <= 23f; x += 1f)
            for (float z = -3f; z <= 3f; z += 1f) cuts.Add(new Vector3(x, 27.5f, z));
        RoadSpatialGrid.CommitAppendCuts(cuts, StairLayout.CutRadius * 2f, RoadSpatialGrid.RoadNetworkVersion);

        var byPrefab = new Dictionary<string, int>();
        int dropped = StairStructures.SettleOnFinishedGround(RoadTerrainModifier.FinishedGround((x, z) => BiomeBlendedHeight.GetBlendedHeight(x, z, world)), byPrefab);

        Assert.Equal(1, dropped);
        Assert.Equal(1, byPrefab["stone_pillar"]);
        Assert.Equal(2, open.Pieces.Count);
        Assert.DoesNotContain(open.Pieces, p => p.Position.y < 28f);
        Assert.Equal(3, cut.Pieces.Count);
    }

    /// <summary>
    /// A staircase settled on the finished ground (StairStructures.Settle) leaves out a wholly buried piece only where the
    /// rest stands without it (BuriedPieces.Omit, the support rule's reading). Three black marble 1 m blocks: one at the
    /// surface, grounded; one under it wholly in the ground, which the game does not ground, carried by the one above; one
    /// beside the buried one, deeper still and under ground not known at one vertex (so never judged buried), carried only
    /// through the buried one. Alone, the buried block carries nothing and is left out; with the block beside it, left out
    /// it would drop that block (the game breaks it, dropping its resources), so it stays.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABuriedPieceIsLeftOutOnlyWhereTheRestStandsWithoutIt(bool beside)
    {
        // Flat ground at 10, not known at (2, 0): under the block beside only (the buried block's own footprint reads x 1 at most).
        Func<int, int, float> vertex = (x, z) => x == 2 && z == 0 ? float.NaN : 10f;
        StructurePiece Block(float x, float y) => new() { Kind = StructurePieceKind.StairSupport, Prefab = "blackmarble_1x1", Position = new Vector3(x, y, 0.5f) };
        StructurePiece top = Block(0.25f, 10f), buried = Block(0.25f, 9f), hung = Block(1.35f, 8.8f);
        Assert.True(BuriedPieces.Buried(buried.Prefab, buried.Position, 0f, vertex));
        Assert.False(BuriedPieces.Buried(hung.Prefab, hung.Position, 0f, vertex));
        var run = new StairRun();
        run.Pieces.Add(top);
        run.Pieces.Add(buried);
        if (beside) run.Pieces.Add(hung);
        StairStructures.Settle(run, vertex);
        Assert.Contains(top, run.Pieces);
        Assert.Equal(beside, run.Pieces.Contains(buried));
        if (beside) Assert.Contains(hung, run.Pieces);
    }

    private sealed class FlatAt10 : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => 10f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
    }

    /// <summary>
    /// A bridge's end stairs are laid whole and their steps wholly in the ground left out by the one owner, as a staircase's
    /// and the viaduct's are (BridgeLayout.LeaveOutBuried, BuriedPieces.Omit, the bridge read by the support rule), not at
    /// emit by burial alone: an end whose two steps stand under the bank at 10 m lays all four (two lanes), and the bridge
    /// leaves them out, carrying nothing. Before, BridgeLayout.EmitSteps dropped them as it laid them (and StoneGround.Place,
    /// StairRailing's rails and posts likewise), without asking whether anything stood on them.
    /// </summary>
    [Fact]
    public void ABridgesEndStepsInTheGroundAreLeftOutByTheOneOwnerNotAsTheyAreLaid()
    {
        var world = new FlatAt10();
        _scope.WithWorld(world);
        var crossing = RoadCrossing.Between(new Vector2(0f, 0f), new Vector2(20f, 0f), riverbedHeight: 5f, fairwayCenter: new Vector2(10f, 0f),
            fairwayWidth: 0f, CrossingKind.Bridge, FordStyle.None);
        var end = new StructureEnds.End(new Vector2(0f, 0f), new Vector2(1f, 0f), 8f, 2);
        var pieces = new List<StructurePiece>();
        BridgeLayout.LayEndStairs(pieces, _ => 10f, end, new Vector2(0f, 1f), new BridgeLayout.Ruin(77, crossing), 0, "stone_stair", carry: false);
        List<StructurePiece> steps = pieces.FindAll(p => p.Kind == StructurePieceKind.Stair);
        Assert.Equal(4, steps.Count);
        Assert.All(steps, p => Assert.True(BuriedPieces.Buried(p.Prefab, p.Position, p.YawDegrees, (_, _) => 10f), $"step at {p.Position}"));
        BridgeLayout.LeaveOutBuried(pieces, _ => 10f, crossing, world, p => p.Kind == StructurePieceKind.Stair);
        Assert.DoesNotContain(pieces, p => p.Kind == StructurePieceKind.Stair);
    }
}
