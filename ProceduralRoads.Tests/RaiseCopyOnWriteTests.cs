using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

[Collection("RoadStatics")]
public class RaiseCopyOnWriteTests
{
    /// <summary>A committed cell array is read by other threads without the grid's lock, so raising a stored point replaces
    /// the array and never edits the one already published (with or without an append).</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(7)]
    public void RaiseLeavesTheCommittedArrayUnchanged(int? addition)
    {
        var at = new RoadSpatialGrid.RoadPoint(new Vector2(10f, 10f), 4f, 20f);
        Vector2i cell = RoadSpatialGrid.GetRoadGrid(at.p.x, at.p.y);
        var committed = new[] { at, new RoadSpatialGrid.RoadPoint(new Vector2(11f, 10f), 4f, 20f) };
        var cells = new ConcurrentDictionary<Vector2i, RoadSpatialGrid.RoadPoint[]>();
        cells[cell] = committed;
        MethodInfo raise = typeof(RoadSpatialGrid).GetMethod("Raise", BindingFlags.NonPublic | BindingFlags.Static)!;

        object? moved = raise.Invoke(null, new object?[] { cells, new List<(RoadSpatialGrid.RoadPoint, float)> { (at, 25f) }, addition });

        Assert.Equal(1, moved);
        Assert.Equal(20f, committed[0].h);
        Assert.Equal(0, committed[0].addition);
        Assert.NotSame(committed, cells[cell]);
        Assert.Equal(25f, cells[cell][0].h);
        Assert.Equal(20f, cells[cell][1].h);
    }
    /// <summary>
    /// A raise is predicted as it is committed (RoadSpatialGrid.Raises, the one rule): what the finished-ground predictor
    /// reads before the road is stored (RoadTerrainModifier.AsStored, through RoadTerrainModifier.PointsNear) is what
    /// RoadSpatialGrid.SetRoadHeights stores. Two raises of one point (12 then 11) leave it at the higher; a raise naming a
    /// point at another width, or at a height within 1e-4 of the stored one, moves it. Before, the prediction matched exact
    /// height and width and took the last target.
    /// </summary>
    [Fact]
    public void ARaiseIsPredictedAsItIsCommitted()
    {
        using var scope = new ValheimWorldScope();
        RoadSpatialGrid.Clear();
        try
        {
            var points = Enumerable.Range(0, 5).Select(k => new Vector2(100f + k, 100f)).ToList();
            RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(_ => 10f),
                points.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 4f, false));
            RoadSpatialGrid.RoadPoint At(int k) => Assert.Single(RoadSpatialGrid.StoredAt(new[] { points[k] })!);
            RoadSpatialGrid.RoadPoint wider = At(1), close = At(2);
            wider.w = 6f; wider.w2 = 36f;
            close.h += 5e-5f;
            var raised = new List<(RoadSpatialGrid.RoadPoint at, float to)> { (At(0), 12f), (At(0), 11f), (wider, 13f), (close, 14f) };
            var centre = new Vector3(102f, 0f, 100f);
            Dictionary<Vector2, float> Heights(List<RoadSpatialGrid.RoadPoint> read) =>
                read.Where(rp => rp.IsRoad).GroupBy(rp => rp.p).ToDictionary(g => g.Key, g => Assert.Single(g.Select(rp => rp.h).Distinct()));

            Dictionary<Vector2, float> predicted;
            using (RoadTerrainModifier.AsStored(new List<RoadSpatialGrid.RoadPoint>(), raised))
                predicted = Heights(RoadTerrainModifier.PointsNear(centre, 20f));
            Assert.True(RoadSpatialGrid.SetRoadHeights(raised) > 0);
            Dictionary<Vector2, float> committed = Heights(RoadSpatialGrid.GetRoadPointsNearPosition(centre, 20f));

            Assert.Equal(new[] { 12f, 13f, 14f, 10f, 10f }, points.Select(p => committed[p]));
            Assert.Equal(points.Select(p => committed[p]), points.Select(p => predicted[p]));
        }
        finally { RoadSpatialGrid.Clear(); }
    }
    /// <summary>
    /// A road's crossings' raises and its landing's are one ordered transformation (RoadNetworkGenerator.CrossingGround.Raises:
    /// the crossings', then the landing's on the heights they leave), predicted (RoadTerrainModifier.AsStored) as it is
    /// committed (CrossingGround.RaiseOthers, once). Two records at one point, at 38 and 40: the crossing takes the first
    /// 38 to 40 and the landing takes 40 to 42, so both end at 42; another point at 40 the crossing takes to 41 and the
    /// landing names at 40 stays at 41, as the crossings' raise was written before the landing's. Before, the two lists
    /// were merged by the max rule for the prediction (40, 42, 42) and the landing raised again at the commit (42, 42, 42).
    /// </summary>
    [Fact]
    public void ACrossingsAndALandingsRaisesArePredictedAndCommittedInTheirOrder()
    {
        using var scope = new ValheimWorldScope();
        RoadSpatialGrid.Clear();
        try
        {
            Vector2 shared = new(100f, 100f), other = new(110f, 100f);
            RoadSpatialGrid.PlannedPath Road(Vector2 a, Vector2 b, float h) =>
                new(new List<Vector2> { a, b }, new List<float> { h, h }, new List<RoadPointDebugInfo> { default, default }, 4f, 4f, false);
            RoadSpatialGrid.Commit(Road(shared, shared + new Vector2(0f, 4f), 38f));
            RoadSpatialGrid.Commit(Road(shared, shared + new Vector2(4f, 0f), 40f));
            RoadSpatialGrid.Commit(Road(other, other + new Vector2(0f, -4f), 40f));
            RoadSpatialGrid.RoadPoint At(Vector2 p, float h) =>
                RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(p.x, 0f, p.y), 0.1f).First(rp => rp.p == p && Mathf.Abs(rp.h - h) < 1e-3f);
            var ground = new RoadNetworkGenerator.CrossingGround();
            ground.Raised.Add((At(shared, 38f), 40f));
            ground.Raised.Add((At(other, 40f), 41f));
            ground.AddLanding(new List<(RoadSpatialGrid.RoadPoint at, float to)> { (At(shared, 40f), 42f), (At(other, 40f), 42f) });
            List<float> Heights(List<RoadSpatialGrid.RoadPoint> read) => new[] { shared, other }
                .SelectMany(p => read.Where(rp => rp.IsRoad && rp.p == p).Select(rp => rp.h).OrderBy(h => h)).ToList();

            List<float> predicted;
            using (RoadTerrainModifier.AsStored(new List<RoadSpatialGrid.RoadPoint>(), ground.Raises()))
                predicted = Heights(RoadTerrainModifier.PointsNear(new Vector3(105f, 0f, 100f), 20f));
            ground.RaiseOthers();
            List<float> committed = Heights(RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(105f, 0f, 100f), 20f));

            Assert.Equal(new[] { 42f, 42f, 41f }, committed);
            Assert.Equal(committed, predicted);
        }
        finally { RoadSpatialGrid.Clear(); }
    }
}
