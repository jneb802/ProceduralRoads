using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// How a generated road may meet the roads already built: at a junction, at its
/// ends, or along a trunk it shares; never across one, nor beside one at its height.
/// </summary>
public class RoadMeetingTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public RoadMeetingTests() => RoadNetworkGenerator.Reset();

    public void Dispose()
    {
        SetPathfinder(null);
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, pathfinder);

    /// <summary>A flat world: a road built north-south, then one east-west across it. The second would cross the first at
    /// grade and is not built (roads that cross are disallowed even if that rejects a POI); one that
    /// ends on the first (a T) is.</summary>
    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f ? 0f : 40f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override int GetSeed() => 77;
    }

    [Fact]
    public void ARoadDoesNotCrossAnotherButMayMeetIt()
    {
        bool Cross(out bool tee)
        {
            RoadNetworkGenerator.Reset();
            var world = new FlatWorld();
            _scope.WithWorld(world);
            SetPathfinder(new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f });
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, -80f), 0f, new Vector2(0f, 80f), 0f, 4f, "trunk"));
            tee = RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 10f), 0f, new Vector2(0f, 10f), 0f, 4f, "tee");
            return RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, -30f), 0f, new Vector2(80f, -30f), 0f, 4f, "across");
        }
        Assert.False(Cross(out bool tee), "the road across was built");
        Assert.True(tee, "the road ending on the trunk was refused");
    }

    /// <summary>A road at 40 m, the trunk's height, from z = -60 to 60, its x given by <paramref name="x"/>.</summary>
    private static RoadSpatialGrid.PlannedPath Road(Func<float, float> x)
    {
        var points = new List<Vector2>();
        var heights = new List<float>();
        for (float z = -60f; z <= 60f; z += 1f) { points.Add(new Vector2(x(z), z)); heights.Add(40f); }
        return new RoadSpatialGrid.PlannedPath(points, heights, points.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f, 120f, false);
    }

    /// <summary>Beside a built road at its height: a road off
    /// the trunk and back onto it (a lens) or beside it all the way braids and is refused; one coming onto the trunk and
    /// staying (a Y) is not. Within a road's width of the trunk the two are one road (RoadRide): the lens and the
    /// duplicate there ride the trunk, are not refused, and store nothing of their own along it.</summary>
    [Fact]
    public void ARoadDoesNotRunBesideAnotherAtItsHeight()
    {
        RoadNetworkGenerator.Reset();
        var world = new FlatWorld();
        _scope.WithWorld(world);
        SetPathfinder(new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f });
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, -80f), 0f, new Vector2(0f, 80f), 0f, 4f, "trunk"));
        // On the trunk, 4.5 m off it (past a road's width, within reach) from z -20 to 20, back on it.
        var lens = new List<RoadSpatialGrid.PlannedPath> { Road(z => Mathf.Abs(z) < 20f ? 4.5f : 0f) };
        // 4.5 m beside it all the way.
        var duplicate = new List<RoadSpatialGrid.PlannedPath> { Road(z => 4.5f) };
        // In from 20 m off, onto it from z = 0 on.
        var y = new List<RoadSpatialGrid.PlannedPath> { Road(z => z < 0f ? Mathf.Min(20f, -z * 0.6f + 0.5f) : 0f) };
        Assert.True(RoadNetworkGenerator.BraidsNetwork(lens, out _), "the lens");
        Assert.True(RoadNetworkGenerator.BraidsNetwork(duplicate, out _), "the duplicate");
        Assert.False(RoadNetworkGenerator.BraidsNetwork(y, out _), "the Y");
        // Within a road's width: one road. Not refused, and nothing of it stored along the trunk.
        var narrowLens = new List<RoadSpatialGrid.PlannedPath> { Road(z => Mathf.Abs(z) < 20f ? 3f : 0f) };
        var narrowDuplicate = new List<RoadSpatialGrid.PlannedPath> { Road(z => 3f) };
        Assert.False(RoadNetworkGenerator.BraidsNetwork(narrowLens, out _), "the lens within a road's width");
        Assert.False(RoadNetworkGenerator.BraidsNetwork(narrowDuplicate, out _), "the duplicate within a road's width");
        Assert.Empty(RoadRide.Own(narrowLens, out _));
        Assert.Empty(RoadRide.Own(narrowDuplicate, out _));
    }

    /// <summary>A lens 4.6 to 5.3 m off the trunk for 25 m, at its height, back on it at both ends: two 4 m roads that far apart still read as one smeared road, so it braids and is refused, however its
    /// distance wavers about 5 m.</summary>
    [Fact]
    public void ALensWaveringAboutFiveMetresOffTheTrunkIsRefused()
    {
        RoadNetworkGenerator.Reset();
        var world = new FlatWorld();
        _scope.WithWorld(world);
        SetPathfinder(new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f });
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, -80f), 0f, new Vector2(0f, 80f), 0f, 4f, "trunk"));
        var lens = new List<RoadSpatialGrid.PlannedPath> { Road(z => Mathf.Abs(z) < 12.5f ? 4.95f + 0.35f * Mathf.Sin(z * 1.3f) : 0f) };
        Assert.True(RoadNetworkGenerator.BraidsNetwork(lens, out _), "the wavering lens");
        // Clear of it (a verge of grass between the two roads): not beside it.
        var apart = new List<RoadSpatialGrid.PlannedPath> { Road(z => 8f) };
        Assert.False(RoadNetworkGenerator.BraidsNetwork(apart, out _), "8 m off");
    }

    /// <summary>A road that comes in beside the trunk at its height and ends on it (a Y) may run beside it only up to the
    /// cap every parallel run has (RoadBeside.MostRun, away from its own ends): within it, it is a Y; past it, it is refused
    /// like a lens or a duplicate. Judged with its end on the trunk cut off, a short shallow Y read as a duplicate; then any Y passed, however
    /// long it ran in beside the trunk.</summary>
    [Fact]
    public void AYRunsBesideTheTrunkOnlyUpToTheCapEveryParallelRunHas()
    {
        RoadNetworkGenerator.Reset();
        var world = new FlatWorld();
        _scope.WithWorld(world);
        SetPathfinder(new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f });
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, -80f), 0f, new Vector2(0f, 80f), 0f, 4f, "trunk"));
        // In at a shallow angle, 4.5 m beside the trunk (past a road's width: RoadRide) from z 47 to 54 (7 m away from its
        // end), onto it over its last 6 m.
        var shortY = new List<RoadSpatialGrid.PlannedPath> { Road(z => z < 50f ? Mathf.Min(20f, 4.5f + (50f - z) * 0.6f) : z < 54f ? 4.5f : 4.5f * (60f - z) / 6f) };
        Assert.False(RoadNetworkGenerator.BraidsNetwork(shortY, out _), "a Y within the cap");
        // The same Y with 74 m beside the trunk before it joins: past the cap.
        var longY = new List<RoadSpatialGrid.PlannedPath> { Road(z => z < -20f ? Mathf.Min(20f, 4.5f + (-20f - z) * 0.6f) : z < 54f ? 4.5f : 4.5f * (60f - z) / 6f) };
        Assert.True(RoadNetworkGenerator.BraidsNetwork(longY, out _), "a Y past the cap");
        // Beside it to its end without joining it: a duplicate, as before.
        var besideToTheEnd = new List<RoadSpatialGrid.PlannedPath> { Road(z => z < -20f ? Mathf.Min(20f, 4.5f + (-20f - z) * 0.6f) : 4.5f) };
        Assert.True(RoadNetworkGenerator.BraidsNetwork(besideToTheEnd, out _), "beside it to its end, not joining");
    }

    /// <summary>A strip of level ground at 40 m between the sea north of z = 11 and south of z = -40.</summary>
    private sealed class Strip : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => Mathf.Abs(wx) > 200f || wy > 11f || wy < -40f ? 0f : 40f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
        public override int GetSeed() => 77;
    }

    /// <summary>An earthwork re-route is taken only where the network takes it: one that crosses another road is not,
    /// and the first plan stands. Here the road's straight line passes
    /// 7 m from a short road on a 3.5 m fill and is held up to it (a stretch held over 3 m off its profile, which is
    /// re-routed but never refused); the only way round, south, crosses a road that runs from just south of the line to
    /// the sea. Taken, the re-route was refused for the crossing and the road with it.</summary>
    [Fact]
    public void AnEarthworkReRouteThatCrossesAnotherRoadIsNotTaken()
    {
        float cap = RoadNetworkGenerator.EarthworkCap;
        try
        {
            bool Build()
            {
                RoadNetworkGenerator.Reset();
                var world = new Strip();
                _scope.WithWorld(world);
                SetPathfinder(new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f, Fords = false, Bridges = false });
                RoadNetworkGenerator.EarthworkCap = 8f;
                RoadSpatialGrid.PlannedPath Laid(float x0, float z0, float x1, float z1, float h)
                {
                    var points = new List<Vector2>();
                    int n = Mathf.CeilToInt(Vector2.Distance(new Vector2(x0, z0), new Vector2(x1, z1)));
                    for (int i = 0; i <= n; i++) points.Add(Vector2.Lerp(new Vector2(x0, z0), new Vector2(x1, z1), i / (float)n));
                    return new RoadSpatialGrid.PlannedPath(points, points.Select(_ => h).ToList(), points.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f, n, false);
                }
                RoadSpatialGrid.Commit(Laid(-4f, 7f, 4f, 7f, 43.5f));
                RoadSpatialGrid.Commit(Laid(-6f, -8f, -6f, -40f, 40f));
                RoadSpatialGrid.FinalizeRoadNetwork();
                return RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0f), 0f, new Vector2(80f, 0f), 0f, 4f, "along");
            }
            Assert.True(Build(), "refused: " + RoadSpatialGrid.LastRefusal);
            // The road built is the first plan: it does not cross the road to the south.
            Assert.Empty(RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(-6f, 0f, -20f), 3f).Where(p => Mathf.Abs(p.p.x + 6f) > 1f));
        }
        finally { RoadNetworkGenerator.EarthworkCap = cap; }
    }

    /// <summary>The planner's join and its search ask one owner whether a place is already served, the same as the count
    /// of places reached: a place 19 m from a trunk on a 3 m fill is reached (it is within the place's reach), and the
    /// planner leaves it, where it used to search for a junction at grade from it again (none along the fill) and come
    /// back with no join, or build a connector beside the trunk. A boss's entrance is not served by a road passing it.</summary>
    [Fact]
    public void APlaceTheNetworkServesIsServedForThePlannerAsForTheCount()
    {
        RoadNetworkGenerator.Reset();
        RoadSiteProtection.Reset();
        var world = new FlatWorld();
        _scope.WithWorld(world);
        var finder = new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f };
        SetPathfinder(finder);
        float step = RoadPathfinder.JunctionStep;
        RoadPathfinder.JunctionStep = RoadPathfinder.DefaultJunctionStep;
        try
        {
            var points = new List<Vector2>();
            for (int x = -60; x <= 60; x += 2) points.Add(new Vector2(x, 19f));
            RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(points, points.Select(_ => 43f).ToList(), points.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f, 120f, false));
            RoadSpatialGrid.FinalizeRoadNetwork();
            float laid = RoadSpatialGrid.TotalRoadLength;
            var hints = new List<Vector2>(points);

            Assert.True(RoadNetworkGenerator.Reached(Vector3.zero, 10f));
            Assert.True(RoadNetworkGenerator.JoinPlace(finder, world, hints, new RoadConnectionPlanner.Place("Cave", Vector3.zero, 10f)));
            Assert.Equal(laid, RoadSpatialGrid.TotalRoadLength);
            Assert.False(RoadNetworkGenerator.PlaceServed("SomeBossEntrance", Vector3.zero, 10f));
        }
        finally { RoadPathfinder.JunctionStep = step; }
    }

    /// <summary>An earthwork re-route passed over for crossing another road prices that crossing up for the next attempt,
    /// which goes round it: here past the end of the short road to the south. Without it the next attempt searched the same
    /// way, was passed over again, and the road kept its first plan, held 3.5 m up to the road on a fill beside it.</summary>
    [Fact]
    public void AnEarthworkReRoutePassedOverForACrossingGoesRoundItNextTime()
    {
        float cap = RoadNetworkGenerator.EarthworkCap;
        try
        {
            RoadNetworkGenerator.Reset();
            var world = new Strip();
            _scope.WithWorld(world);
            SetPathfinder(new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f, Fords = false, Bridges = false });
            RoadNetworkGenerator.EarthworkCap = 8f;
            RoadSpatialGrid.PlannedPath Laid(float x0, float z0, float x1, float z1, float h)
            {
                var points = new List<Vector2>();
                int n = Mathf.CeilToInt(Vector2.Distance(new Vector2(x0, z0), new Vector2(x1, z1)));
                for (int i = 0; i <= n; i++) points.Add(Vector2.Lerp(new Vector2(x0, z0), new Vector2(x1, z1), i / (float)n));
                return new RoadSpatialGrid.PlannedPath(points, points.Select(_ => h).ToList(), points.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f, n, false);
            }
            RoadSpatialGrid.Commit(Laid(-4f, 7f, 4f, 7f, 43.5f));
            // A short road south of the line, ending 20 m short of the sea.
            RoadSpatialGrid.Commit(Laid(-6f, -8f, -6f, -20f, 40f));
            RoadSpatialGrid.FinalizeRoadNetwork();

            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0f), 0f, new Vector2(80f, 0f), 0f, 4f, "along"), "refused: " + RoadSpatialGrid.LastRefusal);
            // Built round the south road's end, clear of the road on the fill: nothing of it within 9 m of that road.
            Assert.Empty(RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(0f, 0f, 7f), 9f).Where(p => p.p.y < 5f));
        }
        finally { RoadNetworkGenerator.EarthworkCap = cap; }
    }

    /// <summary>A staircase onto a placed place's landing meets the network along its walk: one point a row, at the row's
    /// middle and walking surface. Taken piece by piece, two pieces a row side by side read as a zig-zag across the width
    /// and as a longer road, and a short pass beside another road could count as a braid.</summary>
    [Fact]
    public void AStaircaseOntoALandingMeetsTheNetworkAlongItsWalk()
    {
        var plan = new StairPlan();
        for (int row = 0; row < 3; row++)
            foreach (float x in new[] { -1f, 1f })
                plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Stair, Prefab = "stone_stair", Position = new Vector3(x, 40f + row, 2f * row) });
        var span = new RoadStairSpan { FromPoint = new Vector2(0f, -1f), ToPoint = new Vector2(0f, 5f), Plan = plan };
        var road = new List<RoadSpatialGrid.PlannedPath> { new(new List<Vector2> { new(0f, -20f), new(0f, -2f) }, new List<float> { 40f, 40f },
            new List<RoadPointDebugInfo> { default, default }, 4f, 18f, false) };

        var all = RoadNetworkGenerator.WithEntry(false, road, new Vector2(0f, -2f), null, span);

        var entry = all[all.Count - 1];
        Assert.Equal(3, entry.Points.Count);
        for (int row = 0; row < 3; row++)
        {
            Assert.Equal(new Vector2(0f, 2f * row), entry.Points[row]);
            Assert.Equal(40.5f + row, entry.Heights[row], 3);
        }
    }

    /// <summary>A place's join tries the next junction away from one whose road is refused: here the cheapest junction,
    /// straight north on the trunk, can be reached only across a short road on a 5 m fill (not a junction: off grade),
    /// and that road is refused for crossing it; a junction further east along the trunk is reached clear of it. The join
    /// took its first junction only, and the place was left cut off (offline: the components the join-arrival rule added).</summary>
    [Fact]
    public void AJoinTriesTheNextJunctionWhereTheRoadToTheFirstIsRefused()
    {
        int retries = RoadNetworkGenerator.JoinRetries;
        float step = RoadPathfinder.JunctionStep;
        RoadPathfinder.JunctionStep = RoadPathfinder.DefaultJunctionStep;
        try
        {
            bool Join(int tries)
            {
                RoadNetworkGenerator.Reset();
                RoadSiteProtection.Reset();
                var world = new FlatWorld();
                _scope.WithWorld(world);
                var finder = new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f };
                SetPathfinder(finder);
                RoadNetworkGenerator.JoinRetries = tries;
                RoadSpatialGrid.PlannedPath Laid(float x0, float z0, float x1, float z1, float h)
                {
                    var points = new List<Vector2>();
                    int n = Mathf.CeilToInt(Vector2.Distance(new Vector2(x0, z0), new Vector2(x1, z1)));
                    for (int i = 0; i <= n; i++) points.Add(Vector2.Lerp(new Vector2(x0, z0), new Vector2(x1, z1), i / (float)n));
                    return new RoadSpatialGrid.PlannedPath(points, points.Select(_ => h).ToList(), points.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f, n, false);
                }
                var trunk = Laid(-100f, 0f, 100f, 0f, 40f);
                RoadSpatialGrid.Commit(trunk);
                RoadSpatialGrid.Commit(Laid(-6f, -20f, 6f, -20f, 45f));
                RoadSpatialGrid.FinalizeRoadNetwork();
                return RoadNetworkGenerator.JoinPlace(finder, world, new List<Vector2>(trunk.Points), new RoadConnectionPlanner.Place("Cave", new Vector3(0f, 0f, -40f), 5f));
            }
            Assert.False(Join(0), "the road to the first junction was built");
            Assert.True(Join(2), "refused: " + RoadSpatialGrid.LastRefusal);
        }
        finally { RoadNetworkGenerator.JoinRetries = retries; RoadPathfinder.JunctionStep = step; }
    }
}
