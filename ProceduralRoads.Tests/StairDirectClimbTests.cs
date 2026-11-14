using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// A road's staircase climbs straight where its road wandered (StairLayout.FollowedRoute) and rests no longer than a road's
/// ease between its stairs (StairLayout.LongFloorsPrice). Walked through every point of the road's path, a staircase walked
/// the road's own wander: across a gentle shelf and back, a switchback between, its spare length laid as long level walks of
/// floors; and with a floor costing about what a stair does, nothing priced those walks. On the ground of a generated
/// Mistlands world (the world generator's own heights on its 8 m grid, before any road): a 24 m climb whose road wound east
/// across a shelf and back laid 15 floors in a row and a switchback; a 30 m climb round a hill laid walks of up to 20 floors.
/// </summary>
[Collection("StairLayout")]
public class StairDirectClimbTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public StairDirectClimbTests() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); StairStructures.Reset(); }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        StairStructures.Reset();
        _scope.Dispose();
    }

    /// <summary>A fixture's ground (StairLayoutTests.FixtureGround on its 8 m grid), the Mistlands throughout.</summary>
    private sealed class FixtureWorld : WorldGenerator
    {
        public readonly Func<float, float, float> Ground;
        public FixtureWorld(string fixture) => Ground = StairLayoutTests.FixtureGround(fixture, 8);
        public override float GetHeight(float wx, float wy) => Ground(wx, wy);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override int GetSeed() => 77;
    }

    private static List<Vector2> Points(string line) => line.Split(' ').Select(p => p.Split(','))
        .Select(f => new Vector2(float.Parse(f[0], CultureInfo.InvariantCulture), float.Parse(f[1], CultureInfo.InvariantCulture))).ToList();

    /// <summary>The longest run of floors between two stairs (a rest or a walk; not those past its last stair or before its
    /// first), and the plan's walked pieces as S (a stair) and Fn (n floors), for the message.</summary>
    private static (int longest, string said) InteriorFloors(StairPlan plan)
    {
        int longest = 0, floors = 0;
        bool stair = false;
        var said = new System.Text.StringBuilder();
        foreach (StairPiece p in plan.Pieces.Where(p => !p.Sunk && p.Kind is StairPieceKind.Stair or StairPieceKind.Landing))
        {
            if (p.Kind == StairPieceKind.Landing) { floors++; continue; }
            if (floors > 0) said.Append($"F{floors} ");
            if (stair) longest = Math.Max(longest, floors);
            floors = 0;
            stair = true;
            said.Append("S ");
        }
        if (floors > 0) said.Append($"F{floors}");
        return (longest, said.ToString().Trim());
    }

    /// <summary>
    /// A road climbing about 24 m south-west up a hillside, its path wound east across a gentle shelf and back (a switchback
    /// the road search drew), every step of it that climbs a stair step: laid by RoadStairRuns.Lay, the road's staircase
    /// climbs straight up the hillside, with no switchback and no floor run longer than a rest. Walked through every point of
    /// the path, it laid floors east across the shelf, switched back and laid 15 floors back across the hillside.
    /// </summary>
    [Fact]
    public void AStaircaseWhoseRoadWoundAcrossAShelfAndBackClimbsStraightWithoutASwitchback()
    {
        var world = new FixtureWorld("stair-climb-past-a-shelf-8m.txt");
        _scope.WithWorld(world);
        List<Vector2> path = Points("3656,6800 3656,6792 3656,6784 3656,6776 3664,6768 3672,6768 3680,6768 3688,6768 3688,6760 3680,6752 "
            + "3672,6744 3664,6736 3656,6728 3648,6720 3640,6712 3640,6704 3632,6696 3624,6688");
        // From the foot of the hillside (3664, 6768) to its top (3640, 6704): every step that climbs is a stair step.
        var steps = new List<(Vector2 from, Vector2 to)>();
        for (int i = 5; i <= 15; i++)
            if (world.GetHeight(path[i].x, path[i].y) > world.GetHeight(path[i - 1].x, path[i - 1].y)) steps.Add((path[i - 1], path[i]));

        List<RoadStairSpan>? spans = RoadStairRuns.Lay(path, steps, new List<RoadCrossing>(), world);

        Assert.True(spans != null, $"not laid: {RoadStairRuns.LastRefusal} at {RoadStairRuns.LastFailedStretch}");
        RoadStairSpan span = Assert.Single(spans!);
        StairPlan plan = span.Plan;
        var (longest, said) = InteriorFloors(plan);
        // One search straight for its top: the road's points left out where a flight climbs straight past them.
        Assert.True(plan.Waypoints == 1, $"searched through {plan.Waypoints} waypoints: {said}");
        Assert.True(plan.Switchbacks == 0, $"{plan.Switchbacks} switchback(s): {said}");
        Assert.True(longest <= StairLayout.RestFloors, $"a run of {longest} floors between stairs, a rest is at most {StairLayout.RestFloors}: {said}");
        Assert.True(Mathf.Abs(span.ToHeight - span.FromHeight) > 20f, $"climbs {span.FromHeight:F1} -> {span.ToHeight:F1}");
    }

    /// <summary>
    /// A road climbing about 30 m east round the south of a hill, then over a saddle and gently rising ground to its top: its
    /// staircase, laid as RoadStairRuns lays a road's stretch (StairLayout.Build on the planning lattice, its road's path as its
    /// corridor, the generation's budgets, a sunk foot and one top landing), has no floor run longer than a rest between its
    /// stairs. Walked through every point of the path, it laid a walk of 20 floors over the gently rising ground.
    /// </summary>
    [Fact]
    public void AStaircaseRoundAHillOverGentlyRisingGroundRestsNoLongerThanARoadsEase()
    {
        var world = new FixtureWorld("stair-climb-round-a-hill-8m.txt");
        _scope.WithWorld(world);
        List<Vector2> path = Points("4208,-5728 4216,-5728 4224,-5736 4232,-5744 4232,-5752 4240,-5752 4248,-5752 4256,-5752 "
            + "4264,-5744 4272,-5744 4280,-5736 4288,-5728 4296,-5720 4304,-5720");
        Vector2 foot = path[0], top = path[path.Count - 1];

        StairPlan plan = StairLayout.Build(foot, top, RoadStairRuns.LayoutGround.Lattice(world).Read, RoadStairRuns.Width, StairKit.BlackMarble,
            RoadStairRuns.GenerationExpansions, corridor: path, totalExpansions: RoadStairRuns.GenerationTotalExpansions,
            stallExpansions: RoadStairRuns.GenerationStall, topLandings: RoadStairRuns.TopLandings, sunkFoot: true);

        Assert.True(plan.Reached, plan.Reason);
        var (longest, said) = InteriorFloors(plan);
        Assert.True(plan.Rise > 25f, $"climbs {plan.Rise:F1} m: {said}");
        Assert.True(longest <= StairLayout.RestFloors, $"a run of {longest} floors between stairs, a rest is at most {StairLayout.RestFloors}: {said}");
    }

    /// <summary>Ground the stairs climb ahead of rising at the grade of a road's gentle climb (1 in 10) for 60 m: the floors
    /// between its stairs are rests of a road's ease at most (StairLayout.RestFloors), not walks; before, a floor cost what a
    /// stair did and the stairs stood one by one between long level walks.</summary>
    [Fact]
    public void FloorsBetweenStairsOnGentleGroundRunNoLongerThanARoadsEase()
    {
        StairPlan plan = StairLayout.Build(new Vector2(0f, 0f), new Vector2(0f, 60f), (x, z) => 0.1f * z, RoadStairRuns.Width, StairKit.Stone);

        Assert.True(plan.Reached, plan.Reason);
        var (longest, said) = InteriorFloors(plan);
        Assert.True(longest <= StairLayout.RestFloors, $"a run of {longest} floors between stairs: {said}");
    }

    /// <summary>The route a staircase follows its road's line by (StairLayout.FollowedRoute): where the line winds over ground
    /// a flight climbs straight, one search straight for the end, the road's points left out (a corridor chord joining the
    /// two); where it switches back up a face steeper than a flight, a waypoint on each leg of its switchback, as the road found
    /// the climb.</summary>
    [Fact]
    public void AFollowedLineKeepsItsTurnsOnlyWhereTheClimbNeedsThem()
    {
        // A gentle slope (0.2) up +z, the line wandering east 24 m and back: one leg, straight from foot to top.
        Func<float, float, float> gentle = (x, z) => 0.2f * z;
        var wander = Points("0,8 8,16 16,24 24,32 16,40 8,48 0,56");
        var (goals, chords) = StairLayout.FollowedRoute(new Vector2(0f, 0f), new Vector2(0f, 64f), wander, gentle);
        Assert.Equal(new[] { new Vector2(0f, 64f) }, goals);
        Assert.Equal(new[] { (new Vector2(0f, 0f), new Vector2(0f, 64f)) }, chords);

        // A face of 1.0 up +z climbed by a road's switchback 40 m across it and back twice: too steep for a flight straight
        // up, so the route keeps a waypoint on each leg, as far along it as a flight climbs straight (each turn's corner cut).
        Func<float, float, float> steep = (x, z) => z;
        var zigzag = Points("8,2 16,4 24,6 32,8 40,10 32,12 24,14 16,16 8,18 0,20 8,22 16,24 24,26 32,28 40,30");
        var (turns, _) = StairLayout.FollowedRoute(new Vector2(0f, 0f), new Vector2(40f, 30f), zigzag, steep);
        Assert.Equal(new[] { new Vector2(32f, 12f), new Vector2(8f, 22f), new Vector2(40f, 30f) }, turns);
    }
}
