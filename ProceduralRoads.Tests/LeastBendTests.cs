using System.Collections.Generic;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>A road whose full bend arcs are refused is planned with the least arcs (the minimum radius) before it keeps
/// its corners.</summary>
[Collection("RoadStatics")]
public class LeastBendTests
{
    private sealed class Flat : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float w, out float width) { w = 0f; width = 0f; }
    }

    /// <summary>Flat ground with a pond inside the corner, where the full arc runs and the least arc does not.</summary>
    private sealed class Pond : WorldGenerator
    {
        public override float GetHeight(float x, float z) => Vector2.Distance(new Vector2(x, z), new Vector2(10.3f, 3.7f)) < 1.2f ? 20f : 40f;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float w, out float width) { w = 0f; width = 0f; }
    }

    [Fact]
    public void ACornerWhoseWideArcRunsIntoWaterIsRoundedOnTheLeastArc()
    {
        // A 90 degree corner between 14 m legs: its full arc (12.6 m) runs through a pond inside the corner, the 4 m
        // arc stays dry. Kept as a corner, the spline through the waypoint turns on about 2 m.
        var path = new List<Vector2> { new(0f, 0f), new(14f, 0f), new(14f, 14f) };
        var world = new Pond();
        float bend = RoadSwitchbacks.BendRadius;
        bool fallback = RoadSwitchbacks.Fallback;
        using var worldScope = new ValheimWorldScope().WithWorld(world);
        RoadSpatialGrid.Clear();
        try
        {
            RoadSwitchbacks.BendRadius = 24f;
            RoadSwitchbacks.Fallback = true;
            RoadSwitchbacks.SuppressBends = true;
            var corner = RoadSpatialGrid.PlanRoadPath(path, 4f, world);
            RoadSwitchbacks.SuppressBends = false;
            Assert.NotNull(corner);
            Assert.True(CornerFilletTests.TightestRadius(corner!.Points) < 3.5f, $"corner kept: tightest {CornerFilletTests.TightestRadius(corner.Points):F2} m");

            var plan = RoadSpatialGrid.PlanRoadPath(path, 4f, world);
            Assert.NotNull(plan);
            // The least arc, not the full one: about 4 m.
            Assert.InRange(CornerFilletTests.TightestRadius(plan!.Points), 4f * 0.97f, 4f * 1.25f);
            Assert.True(CornerFilletTests.SharpestVertex(plan.Points) <= 15f, $"sharpest {CornerFilletTests.SharpestVertex(plan.Points):F1} deg");
            foreach (Vector2 q in plan.Points) Assert.True(world.GetHeight(q.x, q.y) > 30f, $"{q} in the pond");
        }
        finally
        {
            RoadSwitchbacks.SuppressBends = false;
            RoadSwitchbacks.BendRadius = bend;
            RoadSwitchbacks.Fallback = fallback;
            RoadSpatialGrid.Clear();
        }
    }

    [Fact]
    public void TheLeastArcsAreTheMinimumRadius()
    {
        var path = new List<Vector2> { new(0f, 0f), new(40f, 0f), new(40f, 40f) };
        float bend = RoadSwitchbacks.BendRadius;
        RoadSwitchbacks.BendRadius = 24f;
        RoadSwitchbacks.LeastBends = true;
        try
        {
            Assert.True(RoadSwitchbacks.Shape(path, 4f, out var shaped, out _, _ => 40f, stair: false));
            float tightest = CornerFilletTests.TightestRadius(shaped!);
            Assert.InRange(tightest, 4f * 0.97f, 4f * 1.1f);
        }
        finally { RoadSwitchbacks.LeastBends = false; RoadSwitchbacks.BendRadius = bend; }
    }
}
