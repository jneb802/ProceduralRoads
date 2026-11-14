using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A location that levels no flat but smooths the ground round it is landed on the level ground its smoothing leaves beside it
/// (PlacedLocationShape.OnLevelGround), all round it and clear of its own pieces; with none level it is reached at its ring
/// (PlacedLocations.FlatlessPlaced). The case: a StoneTowerRuins04 placed on a Mountain slope, read in game (its turn, root and
/// kept smoothing; the natural ground 12 m apart round it), its road ended at its ring 33 m off while a player stood on the
/// level pad 4 to 7 m south-east of it, at 192.5 m.
/// </summary>
[Collection("RoadStatics")]
public class SmoothLandingTests : IDisposable
{
    private static readonly Vector2 Centre = new(5123.247f, -5624.781f);
    private const float Yaw = 337.5f, Root = 191.019012f, Exterior = 12.28f;

    /// <summary>Its five smoothings as it kept them (none levels): offset x, z, height, smoothing radius and power.</summary>
    private static readonly List<LevelOp> TowerOps = new()
    {
        new LevelOp(0f, 2.61f, 0f, 0f, false, 6.74f, 3f),
        new LevelOp(-3.97f, 2.07f, 0f, 0f, false, 6.14f, 1f),
        new LevelOp(0.51f, -5.75f, 0f, 0f, false, 7.8f, 1f),
        new LevelOp(4.03f, 2.83f, 0f, 0f, false, 5.29f, 1f),
        new LevelOp(3.7f, -5.04f, 1.61f, 0f, false, 7.96f, 2f),
    };

    /// <summary>The natural ground (WorldGenerator heights read in game) every 12 m from (5105, -5643), row by row in z.</summary>
    private static readonly float[] Natural12 =
    {
        193.5f, 211.2f, 220.0f, 222.8f,
        182.6f, 202.5f, 204.8f, 207.5f,
        168.0f, 177.0f, 184.2f, 187.0f,
        149.0f, 159.8f, 167.6f, 171.5f,
    };

    private static float Natural(float x, float z)
    {
        float fx = Mathf.Clamp((x - 5105f) / 12f, 0f, 2.9999f), fz = Mathf.Clamp((z + 5643f) / 12f, 0f, 2.9999f);
        int i = (int)fx, k = (int)fz;
        float tx = fx - i, tz = fz - k;
        float a = Mathf.Lerp(Natural12[k * 4 + i], Natural12[k * 4 + i + 1], tx);
        float b = Mathf.Lerp(Natural12[(k + 1) * 4 + i], Natural12[(k + 1) * 4 + i + 1], tx);
        return Mathf.Lerp(a, b, tz);
    }

    /// <summary>The tower's stones, by the metre they stand on east and north of its centre (its prefab's pieces, turned as
    /// placed): its walls and their fallen courses lie north and west of its centre.</summary>
    private static readonly (int dx, int dz)[] Stones =
    {
        (-7,5), (-7,6), (-7,8), (-6,2), (-6,3), (-6,4), (-6,5), (-6,6), (-6,7), (-5,0), (-5,1), (-5,3), (-5,4), (-5,5), (-5,6), (-5,7),
        (-4,-1), (-4,0), (-4,1), (-4,2), (-4,4), (-4,5), (-4,6), (-4,7), (-4,8), (-3,0), (-3,1), (-3,2), (-3,6), (-3,7), (-3,8),
        (-2,0), (-2,1), (-2,2), (-2,5), (-2,7), (-2,8), (-2,9), (-1,0), (-1,1), (-1,3), (-1,7), (-1,8), (-1,9), (0,0), (0,1), (0,2),
        (0,6), (0,7), (0,8), (0,9), (0,10), (0,11), (1,-1), (1,1), (1,2), (1,3), (1,4), (1,7), (1,8), (1,9), (1,10), (2,2), (2,3),
        (2,4), (2,5), (2,6), (2,7), (2,8), (2,9), (3,2), (3,3), (3,4), (3,5), (3,6), (4,3), (4,4),
    };

    // The tower's stones as the game reads them: built pieces (WearNTear parts, PlacedLocationReader.LocationRocks).
    private static List<LocationRock> Pieces() => Stones
        .Select(s => new LocationRock(new Vector2(Mathf.Round(Centre.x) + s.dx, Mathf.Round(Centre.y) + s.dz), 0.5f, 0.5f, 0f, float.NegativeInfinity, float.PositiveInfinity, "stone", built: true)).ToList();

    private static PlacedLocationRecord Tower(List<LevelOp> ops, List<LocationRock> rocks, string prefab = "StoneTowerRuins04") => new()
    {
        Prefab = prefab, Centre = Centre, Yaw = Yaw, Root = Root, RockRadius = Exterior, Ops = ops, Rocks = rocks,
        X0 = 5088, Z0 = -5660, W = 72, H = 72,
    };

    /// <summary>The Mountain slope it stands on, as the world the preparation reads its biome from.</summary>
    private sealed class MountainSlope : WorldGenerator
    {
        public override float GetHeight(float x, float z) => Natural(x, z);
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
    }

    private readonly Valheim.Testing.Doubles.ValheimWorldScope _scope = new();

    public SmoothLandingTests()
    {
        PlacedLocations.Reset();
        _scope.WithWorld(new MountainSlope());
    }

    public void Dispose()
    {
        PlacedLocations.Reset();
        _scope.Dispose();
    }

    /// <summary>The tower is made ready with its landing on the pad its raised smoothing leaves south-east of it, level with
    /// where the player stood, clear of its stones; not at its ring.</summary>
    [Fact]
    public void ATowerThatOnlySmoothsIsLandedOnTheLevelPadBesideIt()
    {
        PlacedLocations.Load(new[] { Tower(TowerOps, Pieces()) });
        PlacedLocations.Prepare(Natural);
        Assert.True(PlacedLocations.TryGetReady(Centre, out PlacedLocation location), "the tower has no landing: reached at its ring");
        Assert.False(PlacedLocations.FlatlessPlaced(Centre));
        Vector2 landing = location.Shape.Landing;
        Vector2 off = landing - Centre;
        Assert.True(location.Shape.AllRound);
        Assert.InRange(off.magnitude, 3f, 8f);
        Assert.True(off.x > 0f && off.y < 0f, $"landing at ({landing.x:F1}, {landing.y:F1}), not south-east of the tower");
        Assert.InRange(location.Ground(landing.x, landing.y), 192.3f, 193.1f);
        // Clear of the tower's stones by the landing's clearance.
        foreach (LocationRock stone in location.Shape.Body)
            Assert.True(stone.Distance(landing) >= PlacedLocationShape.LevelGroundClearance,
                $"landing ({landing.x:F1}, {landing.y:F1}) {stone.Distance(landing):F1} m off a stone at ({stone.At.x}, {stone.At.y})");
        // The arrival height is the landing's ground: where the player stood (192.5).
        Assert.InRange(RoadNetworkGenerator.ArrivalHeight(Centre, Exterior, Centre + new Vector2(0f, -40f))!.Value, 192.3f, 193.1f);
    }

    /// <summary>A location that only smooths, weakly, on the same steep slope leaves no level ground: still reached at its ring.</summary>
    [Fact]
    public void ASmoothOnlyLocationWithNoLevelGroundStaysReachedAtItsRing()
    {
        var weak = new List<LevelOp> { new(0f, 0f, 0f, 0f, false, 4f, 1f) };
        PlacedLocations.Load(new[] { Tower(weak, Pieces(), "AbandonedLogCabin04") });
        PlacedLocations.Prepare(Natural);
        Assert.False(PlacedLocations.TryGetReady(Centre, out _));
        Assert.True(PlacedLocations.FlatlessPlaced(Centre));
        Assert.Null(PlacedLocationShape.OnLevelGround(Centre, Yaw, Exterior, Pieces(), (x, z) => Natural(x, z), false, Root, "AbandonedLogCabin04"));
    }

    /// <summary>A location entered only from its front keeps its front: never landed all round, and its flat's shape as before.</summary>
    [Fact]
    public void AFrontOnlyLocationIsUnchanged()
    {
        Func<float, float, float> flat = (_, _) => Root;
        Assert.Null(PlacedLocationShape.OnLevelGround(Centre, Yaw, Exterior, null, flat, false, Root, "Mistlands_DvergrTownEntrance1"));
        PlacedLocationShape shape = PlacedLocationShape.Of(Centre, Yaw, new LevelOp(0f, 0f, 0f, 8f, false, 12f, 3f), Exterior, null, flat,
            root: Root, prefab: "Mistlands_DvergrTownEntrance1");
        Assert.False(shape.AllRound);
        Assert.True(shape.EnteredFromFront);
        Assert.False(shape.InFront(Centre - StairLayout.Forward(Yaw) * 10f));
        Assert.All(shape.Landings(flat, 1f, 8), l => Assert.True(shape.InFront(l.at)));
    }
}
