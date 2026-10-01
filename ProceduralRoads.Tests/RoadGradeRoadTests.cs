using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The grade cap where it meets real roads: what the pathfinder will and will
/// not route over, and what AddRoadPath will and will not store.
/// </summary>
public class RoadGradeRoadTests
{
    /// <summary>A plane rising east, above water everywhere the tests look.</summary>
    private sealed class Ramp : WorldGenerator
    {
        public float Grade = 0.35f;
        public override float GetHeight(float wx, float wy) => 60f + Grade * wx;
        public override Heightmap.Biome GetBiome(float wx, float wy) => Heightmap.Biome.Meadows;
    }

    private static RoadPathfinder Finder(WorldGenerator world, float cap)
    {
        // The variance term prices a plane slope everywhere alike and would
        // only slow the search down; these tests are about the grade.
        return new RoadPathfinder(world) { MaxGrade = cap, TerrainVarianceThreshold = 1000f };
    }

    private static float SteepestAlong(List<Vector2> path, WorldGenerator world)
    {
        float steepest = 0f;
        for (int i = 1; i < path.Count; i++)
        {
            float run = Vector2.Distance(path[i - 1], path[i]);
            if (run < 0.001f) continue;
            float rise = Mathf.Abs(world.GetHeight(path[i].x, path[i].y) - world.GetHeight(path[i - 1].x, path[i - 1].y));
            steepest = Mathf.Max(steepest, rise / run);
        }
        return steepest;
    }

    private static float Length(List<Vector2> path)
    {
        float total = 0f;
        for (int i = 1; i < path.Count; i++) total += Vector2.Distance(path[i - 1], path[i]);
        return total;
    }

    [Fact]
    public void UncappedTheSearchClimbsStraightUpTheSlope()
    {
        // The baseline this whole change exists to move. Without a cap the
        // direct climb is expensive but cheapest, so that is what is built.
        var world = new Ramp();
        var path = Finder(world, 0f).FindPath(new Vector2(0f, 0f), new Vector2(160f, 0f));

        Assert.NotNull(path);
        Assert.True(Length(path!) < 175f, $"expected a straight climb, got {Length(path!):F0} m");
        Assert.True(SteepestAlong(path!, world) > 0.3f,
            $"expected the full {world.Grade:P0} slope, got {SteepestAlong(path!, world):P0}");
    }

    [Fact]
    public void CappedTheSearchTraversesInsteadAndTakesTheLongWay()
    {
        // Same two points, same slope. The search has to cross the slope to
        // get there, so the road is far longer than the straight line between
        // its ends. That is the switchback.
        //
        // The steps are held to the cap PLUS RoadConstants.SearchGradeMargin,
        // not to the cap itself: the ground a route crosses is not the profile
        // the road is built to, and the finished profile is still held to the
        // cap by RoadGrade.Limit. Before the margin this read <= 0.2; the
        // search now takes steps up to 0.3 here, which is the intended change.
        var world = new Ramp();
        var path = Finder(world, 0.2f).FindPath(new Vector2(0f, 0f), new Vector2(160f, 0f));

        Assert.NotNull(path);
        float ceiling = 0.2f + RoadConstants.SearchGradeMargin;
        Assert.True(SteepestAlong(path!, world) <= ceiling + 0.001f,
            $"a step climbs at {SteepestAlong(path!, world):P1}, over the {ceiling:P0} search ceiling");
        // Still a traverse, and a shorter one: 226 m here against 160 m straight,
        // where before the margin it was over 300 m. Needing less zig-zag to
        // stay inside the ceiling is the point of the margin, so this asserts
        // that a traverse happened, not that it stayed as long as it was.
        Assert.True(Length(path!) > 200f,
            $"expected a traverse over the 160 m straight line, got {Length(path!):F0} m");
    }

    [Fact]
    public void ASlopeTooSteepToTraverseHasNoRouteAtAll()
    {
        // At 1:1 not even the shallowest of the sixteen directions is inside a
        // 20% cap, so there is no road and the destination goes unconnected.
        // This is the case the cap is meant to reach: an honest refusal in
        // place of a road glued to a wall.
        var world = new Ramp { Grade = 1.0f };
        Assert.Null(Finder(world, 0.2f).FindPath(new Vector2(0f, 0f), new Vector2(160f, 0f)));
        Assert.NotNull(Finder(world, 0f).FindPath(new Vector2(0f, 0f), new Vector2(160f, 0f)));
    }

    [Fact]
    public void StoredHeightsNeverExceedTheCap()
    {
        var world = new Ramp { Grade = 0.35f };
        WorldGenerator.instance = world;
        RoadSpatialGrid.Clear();
        try
        {
            // A path the search would have produced under the cap: knight
            // moves across the slope, with enough spacing for turn landings.
            // The former 8 m spaced V turns are now explicitly refused in SwitchbackTests.
            var path = new List<Vector2>();
            for (int i = 0; i <= 20; i++)
                path.Add(new Vector2(i * 16f, (i % 2 == 0 ? 1f : -1f) * 64f));

            using (GradeCap.At(0.2f))
                Assert.True(RoadSpatialGrid.AddRoadPath(path, 4f, world));

            var stored = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(80f, 0f, 0f), 400f);
            Assert.True(stored.Count > 50, $"only {stored.Count} road points stored");
            stored.Sort((a, b) => a.p.x.CompareTo(b.p.x));
            var points = new List<Vector2>();
            var heights = new List<float>();
            foreach (var rp in stored) { points.Add(rp.p); heights.Add(rp.h); }
            Assert.True(RoadGrade.SteepestStep(points, heights) <= 0.2f + 0.01f,
                $"stored profile climbs at {RoadGrade.SteepestStep(points, heights):P1}");
        }
        finally { RoadSpatialGrid.Clear(); WorldGenerator.instance = null; }
    }

    [Fact]
    public void ARoadWhoseEndsAreTooFarApartIsRefusedAndStoresNothing()
    {
        var world = new Ramp { Grade = 0.5f };
        WorldGenerator.instance = world;
        RoadSpatialGrid.Clear();
        try
        {
            var path = new List<Vector2>();
            for (float x = 0f; x <= 160f; x += 8f) path.Add(new Vector2(x, 0f));

            using (GradeCap.At(0.2f))
                Assert.False(RoadSpatialGrid.AddRoadPath(path, 4f, world));

            Assert.Equal(0, RoadSpatialGrid.TotalRoadPoints);
            Assert.Empty(RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(80f, 0f, 0f), 400f));
        }
        finally { RoadSpatialGrid.Clear(); WorldGenerator.instance = null; }
    }

    [Fact]
    public void ARefusedPlanStoresNothingEvenWhenItsNeighboursAreFine()
    {
        // A road laid in more than one piece must not end up half in the grid.
        // Planning is apart from storing so the caller can find out that one
        // piece is unbuildable before it has stored any of the others.
        var world = new Ramp { Grade = 0.5f };
        WorldGenerator.instance = world;
        RoadSpatialGrid.Clear();
        try
        {
            var gentle = new List<Vector2>();
            for (float y = 0f; y <= 160f; y += 8f) gentle.Add(new Vector2(0f, y)); // across the slope: level
            var steep = new List<Vector2>();
            for (float x = 0f; x <= 160f; x += 8f) steep.Add(new Vector2(x, 0f));  // up it: 50%

            using (GradeCap.At(0.2f))
            {
                var planA = RoadSpatialGrid.PlanRoadPath(gentle, 4f, world);
                var planB = RoadSpatialGrid.PlanRoadPath(steep, 4f, world);
                Assert.NotNull(planA);
                Assert.Null(planB);
                // Planning both first is what lets the caller store neither.
                Assert.Equal(0, RoadSpatialGrid.TotalRoadPoints);
            }
        }
        finally { RoadSpatialGrid.Clear(); WorldGenerator.instance = null; }
    }

    [Fact]
    public void TheSteepestRoadPlannedIsRecorded()
    {
        // The cap's claim about the roads it let through, in one number. It is
        // recorded at planning time because the spatial grid keeps points as a
        // set per cell, with no order and no road they belong to: the nearest
        // neighbour of a stored point can be a different road crossing it.
        var world = new Ramp { Grade = 0.35f };
        WorldGenerator.instance = world;
        RoadSpatialGrid.Clear();
        try
        {
            var path = new List<Vector2>();
            for (int i = 0; i <= 20; i++)
                path.Add(new Vector2(i * 16f, (i % 2 == 0 ? 1f : -1f) * 64f));

            RoadGrade.SteepestPlanned = 0f;
            using (GradeCap.At(0.2f))
                Assert.True(RoadSpatialGrid.AddRoadPath(path, 4f, world));
            Assert.InRange(RoadGrade.SteepestPlanned, 0.01f, 0.2f + 0.001f);

            // And it is a high-water mark, not the last road's figure.
            float afterFirst = RoadGrade.SteepestPlanned;
            RoadSpatialGrid.Clear();
            var flat = new List<Vector2>();
            for (int i = 0; i <= 20; i++) flat.Add(new Vector2(0f, i * 8f)); // across the slope
            using (GradeCap.At(0.2f))
                Assert.True(RoadSpatialGrid.AddRoadPath(flat, 4f, world));
            Assert.Equal(afterFirst, RoadGrade.SteepestPlanned);
        }
        finally { RoadSpatialGrid.Clear(); WorldGenerator.instance = null; RoadGrade.SteepestPlanned = 0f; }
    }

    [Fact]
    public void OnGroundInsideTheCapNothingChanges()
    {
        // The other half of the same claim: where the cap does not bite, every
        // stored height is what it was before there was a cap. Without this the
        // tests above would be satisfied by a limiter that quietly regraded
        // every road in the world.
        var world = new Ramp { Grade = 0.1f };
        WorldGenerator.instance = world;
        try
        {
            var path = new List<Vector2>();
            for (float x = 0f; x <= 320f; x += 8f) path.Add(new Vector2(x, 0f));

            List<float> Heights(float cap)
            {
                RoadSpatialGrid.Clear();
                using (GradeCap.At(cap))
                    Assert.True(RoadSpatialGrid.AddRoadPath(path, 4f, world));
                var stored = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(160f, 0f, 0f), 400f);
                stored.Sort((a, b) => a.p.x.CompareTo(b.p.x));
                var hs = new List<float>();
                foreach (var rp in stored) hs.Add(rp.h);
                return hs;
            }

            var capped = Heights(0.2f);
            var uncapped = Heights(0f);
            Assert.Equal(uncapped.Count, capped.Count);
            for (int i = 0; i < capped.Count; i++)
                Assert.True(Mathf.Abs(capped[i] - uncapped[i]) < 0.001f,
                    $"point {i} moved {capped[i] - uncapped[i]:F4} m on ground well inside the cap");
        }
        finally { RoadSpatialGrid.Clear(); WorldGenerator.instance = null; }
    }
}
