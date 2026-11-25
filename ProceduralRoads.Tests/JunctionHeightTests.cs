using System.Collections.Generic;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// A road that joins, shares or runs beside a road already laid takes that road's height
/// where the two would mix (RoadSpatialGrid.HoldToNetwork). Before, the branch kept its own
/// grade into the junction and a shared stretch its own heights: two surfaces on the same
/// ground, 3.1 m apart at points 1.8 m apart, double roads and weird junctions.
/// </summary>
[Collection("RoadStatics")]
public class JunctionHeightTests
{
    private sealed class Hill : WorldGenerator
    {
        public float Gz = 0.12f, Bump, Ridge;
        public override float GetHeight(float x, float z) => 60f + Gz * z
            + Bump * Mathf.Sin(x * 0.11f) * Mathf.Cos(z * 0.07f) + Ridge * (float)System.Math.Exp(-(z / 12f) * (z / 12f));
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    private static List<Vector2> Line(params float[] xy)
    {
        var w = new List<Vector2>();
        for (int i = 0; i < xy.Length; i += 2) w.Add(new Vector2(xy[i], xy[i + 1]));
        var dense = new List<Vector2> { w[0] };
        for (int i = 1; i < w.Count; i++)
        {
            int n = Mathf.Max(1, Mathf.RoundToInt(Vector2.Distance(w[i - 1], w[i]) / 8f));
            for (int k = 1; k <= n; k++) dense.Add(Vector2.Lerp(w[i - 1], w[i], k / (float)n));
        }
        return dense;
    }

    /// <summary>The worst height difference between a branch point and the trunk point nearest it, among branch points on the trunk (within 2 m).</summary>
    private static float WorstOnTrunk(Hill world, List<Vector2> trunk, List<Vector2> branch)
    {
        float worst = 0f;
        PlainRoads.WithModDefaults(() =>
        {
            RoadWiggle.Amplitude = 0f;
            using var worldScope = new ValheimWorldScope().WithWorld(world);
            RoadSpatialGrid.Clear();
            try
            {
                var t = RoadSpatialGrid.PlanRoadPath(trunk, 4f, world);
                Assert.NotNull(t);
                RoadSpatialGrid.Commit(t!);
                var b = RoadSpatialGrid.PlanRoadPath(branch, 4f, world);
                Assert.NotNull(b);
                int on = 0;
                for (int i = 0; i < b!.Points.Count; i++)
                {
                    float best = float.MaxValue, th = 0f;
                    for (int j = 0; j < t!.Points.Count; j++)
                    {
                        float d = Vector2.Distance(b.Points[i], t.Points[j]);
                        if (d < best) { best = d; th = t.Heights[j]; }
                    }
                    if (best >= 2f) continue;
                    on++;
                    worst = Mathf.Max(worst, Mathf.Abs(b.Heights[i] - th));
                }
                Assert.True(on > 0, "the branch never reached the trunk");
            }
            finally { RoadSpatialGrid.Clear(); }
        });
        return worst;
    }

    [Fact]
    public void ABranchMergingUpASteepTrunkMeetsItLevel()
    {
        var world = new Hill { Gz = 0.3f, Bump = 2f };
        List<Vector2> trunk = Line(0, -100, 0, 100), branch = Line(25, -70, 12, -35, 4, -12, 0, 0);
        Assert.InRange(WorstOnTrunk(world, trunk, branch), 0f, 0.3f);
    }

    [Fact]
    public void AStretchSharedWithAnotherRoadHasItsHeights()
    {
        Assert.InRange(WorstOnTrunk(new Hill { Bump = 4f }, Line(0, -100, 0, 100), Line(60, -120, 30, -60, 6, -20, 0, 0, 0, 30, 0, 60)), 0f, 0.3f);
        Assert.InRange(WorstOnTrunk(new Hill { Gz = 0.1f, Bump = 6f }, Line(0, -100, 0, 100),
            Line(300, -200, 150, -120, 40, -60, 4, -30, 0, -20, 0, 0, 0, 20, 0, 40, 6, 50, 40, 70)), 0f, 0.3f);
    }

    [Fact]
    public void ABranchOverARidgeTakesTheTrunksCut()
    {
        Assert.InRange(WorstOnTrunk(new Hill { Gz = 0f, Ridge = 14f }, Line(0, -100, 0, 100),
            Line(60, -120, 30, -70, 6, -40, 0, -30, 0, 0, 0, 30, 0, 50)), 0f, 0.3f);
    }
    /// <summary>A road running along one laid on a 6 m fill rides on that road's own surface:
    /// its junction's fill is measured over that surface, not the natural ground under it, so
    /// it is not a new ridge. A road coming in square to the embankment from the low ground is
    /// still held up on its own fill there.</summary>
    [Fact]
    public void AJunctionsFillIsMeasuredOverTheGroundTheRoadItMeetsLeft()
    {
        var world = new Hill { Gz = 0f };
        using var worldScope = new ValheimWorldScope().WithWorld(world);
        RoadSpatialGrid.Clear();
        try
        {
            var trunk = RoadSpatialGrid.PlanRoadPath(Line(-60f, 0f, 60f, 0f), 4f, world)!;
            for (int i = 0; i < trunk.Heights.Count; i++) trunk.Heights[i] = 66f;
            RoadSpatialGrid.Commit(trunk);
            float Earth(List<Vector2> branch)
            {
                var heights = new List<float>();
                foreach (Vector2 p in branch) heights.Add(60f);
                var junctions = RoadSpatialGrid.HoldToNetwork(branch, heights, 4f, 0.35f, p => world.GetHeight(p.x, p.y));
                Assert.NotNull(junctions);
                float worst = float.NegativeInfinity;
                foreach (var j in junctions!) worst = Mathf.Max(worst, j.Earth);
                return worst;
            }
            var along = new List<Vector2>();
            for (int x = -20; x <= 20; x++) along.Add(new Vector2(x, 0f));
            var square = new List<Vector2>();
            for (int z = -24; z <= -1; z++) square.Add(new Vector2(0f, z));

            Assert.InRange(Earth(along), -0.01f, RoadNetworkGenerator.JunctionFillLimit);
            Assert.True(Earth(square) > RoadNetworkGenerator.JunctionFillLimit);
        }
        finally { RoadSpatialGrid.Clear(); }
    }

    /// <summary>A pitched climb held to a road it crosses is eased to it within each edge's own limit, as the grade
    /// limiter gave them: its rests stay rests. Eased at the cap everywhere, the rests near the held stretch climbed at
    /// the cap, and the let-go rule could then refuse the road.</summary>
    [Fact]
    public void APitchedClimbIsEasedToARoadItMeetsWithinEachEdgesOwnLimit()
    {
        var world = new Hill { Gz = 0f };
        using var worldScope = new ValheimWorldScope().WithWorld(world);
        RoadSpatialGrid.Clear();
        try
        {
            // Pitches of 10 m that may climb 0.5 and rests of 6 m that may climb 0.1; the road climbs 0.6 of each.
            var points = new List<Vector2>();
            var grades = new List<float> { 0f };
            var own = new List<float> { 60f };
            for (int i = 0; i <= 60; i++) points.Add(new Vector2(i * 2f, 0f));
            for (int i = 1; i < points.Count; i++)
            {
                float limit = (i - 1) % 8 < 5 ? 0.5f : 0.1f;
                grades.Add(limit);
                own.Add(own[i - 1] + 0.6f * limit * 2f);
            }
            float cap = RoadPitches.LimitCap(RoadGrade.Configured, grades.ToArray());
            // A road across it at x = 90, 1.5 m over this road's own height there.
            var across = new List<Vector2>();
            var acrossHeights = new List<float>();
            for (int z = -40; z <= 40; z += 2) { across.Add(new Vector2(90f, z)); acrossHeights.Add(own[45] + 1.5f); }
            RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(across, acrossHeights, across.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 80f, false));

            (List<float> heights, bool[] held) Hold(IReadOnlyList<float>? edgeGrades, float withCap)
            {
                var heights = new List<float>(own);
                var held = new bool[points.Count];
                RoadSpatialGrid.HoldToNetwork(points, heights, 4f, withCap, null, false, false, held, null, edgeGrades);
                return (heights, held);
            }
            float WorstOver((List<float> heights, bool[] held) r)
            {
                float worst = 0f;
                for (int i = 1; i < points.Count; i++)
                {
                    if (r.held[i - 1] && r.held[i]) continue;
                    float grade = Mathf.Abs(r.heights[i] - r.heights[i - 1]) / 2f;
                    worst = Mathf.Max(worst, grade - RoadGrade.EdgeLimit(grades, i, cap));
                }
                return worst;
            }
            var eased = Hold(grades, cap);
            Assert.Contains(true, eased.held);
            Assert.True(WorstOver(eased) <= 0.001f, $"an edge climbs {WorstOver(eased):F3} over its own limit");
            // Eased at the cap, as before: a rest climbs at the cap.
            Assert.True(WorstOver(Hold(null, RoadGrade.Configured)) > 0.1f);
        }
        finally { RoadSpatialGrid.Clear(); }
    }

    /// <summary>A road with a floor (a raised ford's surface) stays on it where it is held to a road below it: held after
    /// the floor was put back, a stretch beside a raised ford dipped under its deck. Its junction record is of the height
    /// it is held at, the floor, not the other road's.</summary>
    [Fact]
    public void ARoadsFloorHoldsWhereItIsHeldToARoadBelowIt()
    {
        var world = new Hill { Gz = 0f };
        using var worldScope = new ValheimWorldScope().WithWorld(world);
        RoadSpatialGrid.Clear();
        try
        {
            var across = Line(0f, -40f, 0f, 40f);
            RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(across, across.ConvertAll(_ => 57f), across.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 80f, false));
            var plan = RoadSpatialGrid.PlanRoadPath(Line(-40f, 0f, 40f, 0f), 4f, world, minHeight: 59.5f);
            Assert.NotNull(plan);
            // Held to the road below at its floor, and its junction record says so (pulled 0.5 m down, not 3 m).
            Assert.NotNull(plan!.Junctions);
            Assert.All(plan.Junctions!, j => Assert.InRange(j.Pull, -0.6f, 0.01f));
            float lowest = float.MaxValue;
            foreach (float h in plan.Heights) lowest = Mathf.Min(lowest, h);
            Assert.True(lowest >= 59.5f - 0.001f, $"the road dips to {lowest:F2} under its 59.5 m floor");
        }
        finally { RoadSpatialGrid.Clear(); }
    }

    /// <summary>A step in the ground: 70 m north of z = 0, 60 m south of z = -2.</summary>
    private sealed class Step : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 60f + 10f * Mathf.Clamp01((z + 2f) / 2f);
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    /// <summary>A road whose last metres run beside a road 10 m above it is held up to that road after its grade
    /// was limited: it climbed 9 m in 10 m out of a 59 m staircase's top (at 1937.8,751.4). A held stretch it cannot reach within its cap is let go: the road keeps its own heights
    /// and passes the other at its own level.</summary>
    [Fact]
    public void ARoadIsNeverHeldUpToAnotherSteeperThanTheCap()
    {
        var world = new Step();
        RoadSpatialGrid.PlannedPath? Branch()
        {
            RoadSpatialGrid.PlannedPath? b = null;
            PlainRoads.WithModDefaults(() =>
            {
                RoadWiggle.Amplitude = 0f;
                using var worldScope = new ValheimWorldScope().WithWorld(world);
                RoadSpatialGrid.Clear();
                try
                {
                    var trunk = RoadSpatialGrid.PlanRoadPath(Line(-40f, 4f, 40f, 4f), 4f, world);
                    Assert.NotNull(trunk);
                    RoadSpatialGrid.Commit(trunk!);
                    b = RoadSpatialGrid.PlanRoadPath(Line(0f, -30f, 0f, -3f), 4f, world);
                }
                finally { RoadSpatialGrid.Clear(); }
            });
            return b;
        }
        RoadSpatialGrid.PlannedPath? with = Branch();
        Assert.NotNull(with);
        Assert.True(RoadGrade.SteepestStep(with!.Points, with.Heights) <= RoadGrade.Configured + RoadSpatialGrid.HeldCapMargin,
            $"it climbs {RoadGrade.SteepestStep(with.Points, with.Heights):P0}");
    }

    /// <summary>A stretch let go that would lie on another road's surface (within the two half widths) is not let go:
    /// kept at its own height it would cross that road at another level, two surfaces mixing; the road is refused instead. Beside the road, clear of its surface, it may be.</summary>
    [Fact]
    public void ARoadIsNotLetGoOntoAnotherRoadsSurface()
    {
        var world = new Step();
        PlainRoads.WithModDefaults(() =>
        {
            RoadWiggle.Amplitude = 0f;
            using var worldScope = new ValheimWorldScope().WithWorld(world);
            RoadSpatialGrid.Clear();
            try
            {
                var trunk = RoadSpatialGrid.PlanRoadPath(Line(-40f, 4f, 40f, 4f), 4f, world);
                Assert.NotNull(trunk);
                RoadSpatialGrid.Commit(trunk!);
                var on = new List<Vector2> { new(0f, -10f), new(0f, 2.5f) };
                var beside = new List<Vector2> { new(0f, -10f), new(0f, -1f) };
                Assert.True(RoadSpatialGrid.LetGoOntoRoad(on, new[] { false, true }, 4f));
                Assert.False(RoadSpatialGrid.LetGoOntoRoad(beside, new[] { false, true }, 4f));
                Assert.False(RoadSpatialGrid.LetGoOntoRoad(on, new[] { false, false }, 4f));
            }
            finally
            {
                RoadSpatialGrid.Clear();
            }
        });
    }
}
