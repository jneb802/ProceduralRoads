using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// A placed location's landing is level with its entrance by the one level a road reaches a location at
/// (<see cref="RoadNetworkGenerator.ReachRise"/>), and a road ending off that level does not count it reached: measured on a
/// test world with a custom location set, where a tower placed first in the Mountains was counted reached by a road
/// carried onto a shelf 1.8 m under its flat, and where a road ending on a ledge 15 m over a flat location was taken for
/// its arrival.
/// </summary>
public class PlacedLocationLandingLevelTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    /// <summary>The ground round the tower: a shelf at 102.67 m, the level the road ended on.</summary>
    private sealed class ShelfWorld : WorldGenerator
    {
        public float Height = Shelf;
        public Heightmap.Biome Biome = Heightmap.Biome.Mountain;
        public override float GetHeight(float wx, float wy) => Height;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Biome;
        public override int GetSeed() => 77;
    }

    private const float Root = 104.486f, Shelf = 102.674f;
    /// <summary>The tower's levelling: a flat 12 m round its centre at its root, smoothed out to 16 m.</summary>
    private static readonly LevelOp Flat = new(0f, 0f, 0f, 12f, false, 16f, 8f);

    public PlacedLocationLandingLevelTests()
    {
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), 20f) });
    }

    public void Dispose()
    {
        LocationLevelling.Source = null;
        RoadLocationProtection.Reset();
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    /// <summary>The ground the tower leaves on the shelf: its flat, smoothed to the shelf from 12 to 16 m out.</summary>
    private static float TowerGround(float x, float z)
    {
        float d = new Vector2(x, z).magnitude;
        float h = d <= Flat.Radius ? Root : Shelf;
        return d <= Flat.SmoothRadius ? Mathf.Lerp(h, Root, 1f - Mathf.Pow(d / Flat.SmoothRadius, Flat.SmoothPower)) : h;
    }

    /// <summary>The boulders the game left on the tower's flat, from its centre (turned 0), as read off its placing.</summary>
    private static readonly LocationRock[] TowerRocks =
    {
        new(new Vector2(0.515f, 12.471f), 1.032f, 1.091f, 275.8f, 103.54f, 105.75f, "MineRock_Obsidian"),
        new(new Vector2(-5.663f, 10.620f), 1.254f, 1.591f, 32.2f, 103.04f, 106.27f, "MineRock_Obsidian"),
        new(new Vector2(-11.304f, 3.335f), 1.234f, 1.505f, 8.3f, 103.02f, 106.12f, "MineRock_Obsidian"),
        new(new Vector2(8.215f, 7.484f), 1.358f, 1.670f, 312.9f, 103.07f, 106.10f, "MineRock_Obsidian"),
        new(new Vector2(-9.679f, -5.292f), 1.358f, 1.670f, 166.6f, 103.06f, 106.10f, "MineRock_Obsidian"),
        new(new Vector2(0.936f, -11.130f), 1.358f, 1.671f, 76.6f, 103.06f, 106.11f, "MineRock_Obsidian"),
        new(new Vector2(-9.245f, 7.940f), 1.116f, 1.145f, 265.2f, 103.85f, 106.40f, "Rock_3"),
        new(new Vector2(11.799f, 3.137f), 1.476f, 1.155f, 161.1f, 103.43f, 106.63f, "Rock_3"),
        new(new Vector2(-3.182f, -10.100f), 1.314f, 1.405f, 273.2f, 102.64f, 105.82f, "Rock_3"),
    };

    private static PlacedLocationShape Tower() =>
        PlacedLocationShape.Of(new Vector2(0f, 0f), 0f, Flat, 20f, TowerRocks, TowerGround, root: Root);

    /// <summary>Every landing a road or a staircase may be aimed at is level with the tower's entrance: the shelf 1.8 m under
    /// its flat is not one. Before, a landing could stand 2 m off the entrance (a number of its own), looser than the level
    /// a road reaches a location at, and a road carried onto the shelf counted the tower reached on its landing.</summary>
    [Fact]
    public void NoLandingStandsFurtherOffTheEntranceThanAStepAndAHalf()
    {
        PlacedLocationShape tower = Tower();
        var landings = tower.Landings(TowerGround, RoadLocationEntry.InsideReach(4f) + RoadLocationEntry.EndSlack, 400, 2f + PlacedLocationShape.BodyGap);

        Assert.NotEmpty(landings);
        Assert.All(landings, l => Assert.InRange(l.height, Root - RoadNetworkGenerator.ReachRise, Root + RoadNetworkGenerator.ReachRise));
    }

    /// <summary>The road carried on from its end at (-9.3, 20.2), on the shelf at 102.72 m (the live end, from the tower's
    /// centre), onto the tower's landing ends level with its entrance, not on the shelf where it began.</summary>
    [Fact]
    public void TheRoadOntoTheTowerEndsLevelWithItsEntrance()
    {
        var world = new ShelfWorld();
        _scope.WithWorld(world);
        Vector2 end = new(-9.3f, 20.2f);
        Vector2 before = end + end.normalized * 30f;
        var request = new LocationApproachRequest
        {
            RoadEnd = end, RoadHeight = 102.72f, Arriving = StairLayout.Bearing(before, end),
            Centre = new Vector2(0f, 0f), RoadTail = new List<Vector2> { before, end },
        };

        RoadSpatialGrid.PlannedPath? road = RoadLocationEntry.Plan(request, Tower(), 4f, TowerGround, world, out string why);

        // No road gets past the boulders onto the flat from here: none is laid, and the planner tries a staircase onto it
        // (the Mountains) or its other routes. Before, the road was laid onto the shelf, ending at 102.67 m.
        if (road != null)
            Assert.InRange(road.Heights[road.Heights.Count - 1], Root - RoadNetworkGenerator.ReachRise, Root + RoadNetworkGenerator.ReachRise);
        else
            Assert.DoesNotContain(FormattableString.Invariant($"{Shelf:F1}"), why);
    }

    /// <summary>A flat location 18 m across at its root (63.2 m), with a ledge 15 m over it on one side (78 m) and the ground on
    /// another a little over its flat (64.4 m), outside the Mountains: its height is its flat's, whatever the ground at its
    /// edge. A road ending on the ledge does not reach it and is refused; one ending on the low side does. On the live
    /// world a road passing along the ledge was the network nearest the location, and its own road's end on the low side
    /// (+1.2 m) was what reached it.</summary>
    [Fact]
    public void AFlatLocationUnderALedgeIsReachedOnlyLevelWithItsFlat()
    {
        _scope.WithWorld(new LedgeWorld());
        RoadNetworkGenerator.Reset();
        IReadOnlyList<LevelOp> flat = new[] { new LevelOp(0f, 0f, 0f, 18f, false, 22f, 8f) };
        LocationLevelling.Source = c => c.magnitude < 0.5f ? flat : null;
        var endNotLevel = typeof(RoadNetworkGenerator).GetMethod("EndNotLevel", BindingFlags.NonPublic | BindingFlags.Static)!;
        string? Why(Vector2 from, Vector2 to)
        {
            var path = new List<Vector2>();
            for (int i = 0; i <= 12; i++) path.Add(Vector2.Lerp(from, to, i / 12f));
            RoadSpatialGrid.PlannedPath plan = RoadSpatialGrid.PlanRoadPath(path, 4f, WorldGenerator.instance!)!;
            Assert.NotNull(plan);
            return ((string refusal, string why)?)endNotLevel.Invoke(null, new object[] {
                path, new List<RoadSpatialGrid.PlannedPath> { plan }, new List<RoadStairSpan>(), new List<RoadStairSpan>(),
                path[0], 0f, new Vector2(0f, 0f), 26f, false, true, true }) is (_, string why) ? why : null;
        }

        Assert.Equal(63.2f, RoadNetworkGenerator.ArrivalHeight(new Vector2(0f, 0f), 26f, new Vector2(-10f, 24.5f))!.Value, 2);
        Assert.Contains("over its location", Why(new Vector2(-10f, 120f), new Vector2(-10f, 24.5f)));
        Assert.Null(Why(new Vector2(120f, 0f), new Vector2(26.5f, 0f)));
    }

    /// <summary>A flat location's world: the ground at its root (63.2 m), a ledge 78 m high north of it from 20 m out, and the
    /// ground east of it from 20 m out 64.4 m, in the Mistlands.</summary>
    private sealed class LedgeWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => wy >= 20f ? 78f : wx >= 20f ? 64.4f : 63.2f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override int GetSeed() => 77;
    }
}
