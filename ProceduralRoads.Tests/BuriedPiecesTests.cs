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
        Assert.Equal(0f, BuriedPieces.LowestOver(box, valley), 4);
    }

    /// <summary>A piece is buried only where the ground stands at or over its top all over its footprint grown by the
    /// game's 0.15 m, on any heading; unknown ground or an unknown piece never.</summary>
    [Fact]
    public void BuriedMeansUnderTheGroundAllOverTheGrownFootprint()
    {
        // A slope rising 1 in 2 to the north (+z).
        Func<int, int, float> slope = (x, z) => 0.5f * z;
        // A 1 x 1 x 2 pillar at the origin, top 1 m up: the ground over its grown footprint runs -0.325 .. 0.325.
        Assert.False(BuriedPieces.Buried("stone_pillar", new Vector3(0f, -0.4f, 0f), 0f, slope));
        Assert.True(BuriedPieces.Buried("stone_pillar", new Vector3(0f, -1.33f, 0f), 0f, slope));
        // Turned 45 degrees its grown corners reach further downhill: 0.65 * sqrt 2 = 0.92 m, 0.46 m lower.
        Assert.False(BuriedPieces.Buried("stone_pillar", new Vector3(0f, -1.4f, 0f), 45f, slope));
        Assert.True(BuriedPieces.Buried("stone_pillar", new Vector3(0f, -1.47f, 0f), 45f, slope));
        Assert.False(BuriedPieces.Buried("no_such_piece", new Vector3(0f, -50f, 0f), 0f, slope));
        Assert.False(BuriedPieces.Buried("stone_pillar", new Vector3(0f, -50f, 0f), 0f, (x, z) => x > 0 ? float.NaN : 0f));
        // Every kit's pieces are known.
        foreach (StairKit kit in new[] { StairKit.Stone, StairKit.BlackMarble, StairKit.Wood })
        {
            Assert.True(BuriedPieces.Known(kit.Stair));
            Assert.True(BuriedPieces.Known(kit.Floor));
            foreach (FillerKind f in kit.Fillers) Assert.True(BuriedPieces.Known(f.Prefab));
        }
        // And every piece StairSupport lays.
        foreach (string support in new[] { "stone_wall_2x1", "stone_wall_1x1", "stone_arch", "blackmarble_2x1x1", "blackmarble_out_1", "blackmarble_outcorner" })
            Assert.True(BuriedPieces.Known(support));
    }

    private sealed class SlopeWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => 30f + 0.25f * wy;
    }
}
