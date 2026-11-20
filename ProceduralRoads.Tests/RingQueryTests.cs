using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
namespace ProceduralRoads.Tests;

/// <summary>The road grid's ring lookups read only the cells that can hold a point within reach (the query's own cell
/// below the narrowest width filed), and answer as a scan of every stored point does.</summary>
[Collection("RoadStatics")]
public class RingQueryTests : System.IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    public RingQueryTests()
    {
        _scope.WithWorld(new SyntheticWorld { HasRiver = false, HasMountain = false });
        RoadSpatialGrid.Clear();
    }
    public void Dispose() { RoadSpatialGrid.Clear(); _scope.Dispose(); }

    [Fact]
    public void EveryRingLookupAnswersAsAScanOfEveryPointDoes()
    {
        var random = new System.Random(20261028);
        float Next(float lo, float hi) => lo + (float)random.NextDouble() * (hi - lo);
        var path = new List<Vector2>();
        for (int i = -6; i <= 6; i++) path.Add(new Vector2(i * 8f, 0f));
        Assert.True(RoadSpatialGrid.AddRoadPath(path, 4f, WorldGenerator.instance));
        // Roads of several widths, the narrowest 2 m, a few points at a time, and a staircase's cuts.
        float[] widths = { 2f, 2.5f, 4f, 6f, 9f };
        for (int r = 0; r < 60; r++)
        {
            var points = new List<Vector2>(); var heights = new List<float>();
            var at = new Vector2(Next(-400f, 400f), Next(-400f, 400f));
            for (int i = 0; i < 12; i++) { points.Add(at); heights.Add(Next(20f, 60f)); at += new Vector2(Next(-3f, 3f), Next(-3f, 3f)); }
            RoadSpatialGrid.CommitLevelled(points, heights, widths[r % widths.Length]);
        }
        RoadSpatialGrid.CommitCuts(Enumerable.Range(0, 40).Select(_ => new Vector3(Next(-400f, 400f), Next(20f, 60f), Next(-400f, 400f))).ToList(), 1f);
        RoadSpatialGrid.FinalizeRoadNetwork();

        var all = new List<RoadSpatialGrid.RoadPoint>();
        var seen = new HashSet<(float, float, float, float, bool, bool)>();
        for (int x = -12; x <= 12; x++)
            for (int y = -12; y <= 12; y++)
                foreach (var p in RoadSpatialGrid.CellPoints(new Vector2i(x, y)) ?? new RoadSpatialGrid.RoadPoint[0])
                    if (seen.Add((p.p.x, p.p.y, p.w, p.h, p.cutOnly, p.paintOnly))) all.Add(p);

        float[] reaches = { 0.3f, 0.5f, 1f, 1.5f, 1.98f, 2f, 2.5f, 6f, 12f, 63f, 64f, 70f, 130f };
        int found = 0;
        for (int q = 0; q < 3000; q++)
        {
            // Half the queries next to a stored point, so short reaches find something.
            var near = all[random.Next(all.Count)].p;
            Vector2 at = q % 2 == 0 ? near + new Vector2(Next(-2f, 2f), Next(-2f, 2f)) : new Vector2(Next(-450f, 450f), Next(-450f, 450f));
            float reach = reaches[q % reaches.Length];
            float ground = Next(20f, 60f);

            RoadSpatialGrid.RoadPoint? Best(System.Func<RoadSpatialGrid.RoadPoint, bool> keep)
            {
                RoadSpatialGrid.RoadPoint? best = null; float bestD = reach * reach;
                foreach (var p in all)
                {
                    float d = (p.p - at).sqrMagnitude;
                    if (d <= bestD && keep(p)) { bestD = d; best = p; }
                }
                return best;
            }

            var within = Best(p => !p.cutOnly);
            Assert.Equal(within.HasValue, RoadSpatialGrid.TryGetRoadWithin(at, reach, out Vector2 nearest));
            if (within.HasValue) { Assert.Equal(within.Value.p, nearest); found++; }

            var road = Best(p => p.IsRoad);
            Assert.Equal(road.HasValue, RoadSpatialGrid.TryGetRoadHeightWithin(at, reach, out float height, out _, out float width));
            if (road.HasValue) { Assert.Equal(road.Value.h, height); Assert.Equal(road.Value.w, width); }

            var grade = Best(p => p.IsRoad && Mathf.Abs(p.h - ground) <= 3f);
            Assert.Equal(grade.HasValue, RoadSpatialGrid.TryGetRoadAtGradeWithin(at, reach, ground, 3f, out Vector2 junction));
            if (grade.HasValue) Assert.Equal(grade.Value.p, junction);
        }
        Assert.True(found > 1000, $"only {found} lookups found a road");

        // The search's reuse test: AnyRoadAt answers as a weight above zero does, at the road's very edge too.
        int on = 0;
        for (int q = 0; q < 20000; q++)
        {
            var p = all[random.Next(all.Count)];
            float angle = Next(0f, 6.2831853f), r = p.w * 0.5f * (q % 4 == 0 ? Next(0.999f, 1.0001f) : Next(0f, 1.2f));
            Vector2 at = p.p + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * r;
            RoadSpatialGrid.GetRoadWeight(at.x, at.y, out float weight, out _);
            Assert.Equal(weight > 0f, RoadSpatialGrid.AnyRoadAt(at.x, at.y));
            if (weight > 0f) on++;
        }
        Assert.True(on > 5000, $"only {on} samples on a road");
    }
}
