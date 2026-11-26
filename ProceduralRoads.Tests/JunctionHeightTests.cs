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
        public float Gz = 0.12f, Gx, Bump, Ridge;
        public override float GetHeight(float x, float z) => 60f + Gz * z + Gx * x
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
                Assert.True(b != null, RoadSpatialGrid.LastRefusal);
                int on = 0;
                for (int i = 0; i < b!.Points.Count; i++)
                {
                    // The trunk's surface at the branch point: along the trunk between its two points either side, as a road
                    // held to it reads it (RoadSpatialGrid.SurfaceReader), not its nearer point's height.
                    float best = float.MaxValue, th = 0f;
                    for (int j = 1; j < t!.Points.Count; j++)
                    {
                        Vector2 a0 = t.Points[j - 1], a1 = t.Points[j], q = b.Points[i];
                        float len2 = (a1 - a0).sqrMagnitude;
                        float u = len2 > 0f ? Mathf.Clamp01(Vector2.Dot(q - a0, a1 - a0) / len2) : 0f;
                        float d = Vector2.Distance(q, Vector2.Lerp(a0, a1, u));
                        if (d < best) { best = d; th = Mathf.Lerp(t.Heights[j - 1], t.Heights[j], u); }
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

    /// <summary>A road held to another reads that road's surface between its points (RoadSpatialGrid.SurfaceReader,
    /// the terrain writer's fit), not the nearer point's height: a road along a trunk at the cap, its points 1 m apart, held
    /// at 0.9 m spacing climbs with it within the cap on every step. Read off the nearest point, it climbed in cap-sized
    /// jumps at its own spacing, 38 % (seen on a generated network), and the step between two held points was not judged.</summary>
    [Fact]
    public void ARoadHeldToAnotherClimbsWithItWithinTheCapAtItsOwnSpacing()
    {
        var world = new Hill { Gz = 0f };
        using var worldScope = new ValheimWorldScope().WithWorld(world);
        RoadSpatialGrid.Clear();
        try
        {
            var trunk = new List<Vector2>(); var trunkHeights = new List<float>();
            for (int i = 0; i <= 40; i++) { trunk.Add(new Vector2(i, 0f)); trunkHeights.Add(60f + RoadGrade.Configured * i); }
            RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(trunk, trunkHeights, trunk.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 40f, false));
            var points = new List<Vector2>(); var heights = new List<float>();
            for (float x = 5f; x <= 35f + 1e-3f; x += 0.9f) { points.Add(new Vector2(x, 0f)); heights.Add(60f); }
            var held = new bool[points.Count];
            RoadSpatialGrid.HoldToNetwork(points, heights, 4f, RoadGrade.Configured, null, false, false, held);
            Assert.Contains(true, held);
            for (int i = 1; i < points.Count; i++)
            {
                float g = Mathf.Abs(heights[i] - heights[i - 1]) / Vector2.Distance(points[i - 1], points[i]);
                Assert.True(g <= RoadGrade.Configured + 1e-3f, $"step at x = {points[i].x:F1}: {g:P1}");
            }
            Assert.Equal(-1, RoadSpatialGrid.WorstOverLimit(points, heights, null, RoadGrade.Configured, 1f));
        }
        finally { RoadSpatialGrid.Clear(); }
    }

    private static void Commit(List<Vector2> points, List<float> heights) =>
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(points, heights, points.ConvertAll(_ => default(RoadPointDebugInfo)), 4f,
            points.Count - 1, false));

    /// <summary>One reader of a met road's height (RoadSpatialGrid.SurfaceReader): a road held to a trunk at the cap, the
    /// height a joined end is accepted on and the join search's arrival (RoadSpatialGrid.TryGetRoadHeightWithin) read the
    /// trunk's surface at the same place to the same height, along its line. Before, the hold read along the line and every
    /// other reader the nearest point's height: a join end judged at one height was then held to another, up to half the
    /// trunk's spacing times its grade apart.</summary>
    [Fact]
    public void EveryReaderOfAMetRoadsHeightReadsItsSurfaceAlongItsLine()
    {
        var world = new Hill { Gz = 0f };
        using var worldScope = new ValheimWorldScope().WithWorld(world);
        RoadSpatialGrid.Clear();
        try
        {
            var trunk = new List<Vector2>(); var trunkHeights = new List<float>();
            for (int i = 0; i <= 40; i++) { trunk.Add(new Vector2(i, 0f)); trunkHeights.Add(60f + RoadGrade.Configured * i); }
            Commit(trunk, trunkHeights);
            var points = new List<Vector2>(); var heights = new List<float>();
            for (float x = 10.4f; x <= 30f; x += 1f) { points.Add(new Vector2(x, 0.3f)); heights.Add(60f); }
            var held = new bool[points.Count];
            RoadSpatialGrid.HoldToNetwork(points, heights, 4f, RoadGrade.Configured, null, false, false, held);
            for (int i = 0; i < points.Count; i++)
            {
                Assert.True(held[i]);
                Assert.True(RoadSpatialGrid.TryGetRoadHeightWithin(points[i], 8f, out float read));
                float line = 60f + RoadGrade.Configured * points[i].x;
                Assert.True(Mathf.Abs(read - heights[i]) <= 1e-3f, $"at x = {points[i].x:F1}: read {read:F3}, held {heights[i]:F3}");
                Assert.True(Mathf.Abs(read - line) <= 1e-2f, $"at x = {points[i].x:F1}: read {read:F3}, the trunk's line {line:F3}");
                Assert.Equal(read, RoadSpatialGrid.RoadHeightAt(points[i].x, points[i].y, 8f));
            }
        }
        finally { RoadSpatialGrid.Clear(); }
    }

    /// <summary>A met road's height is that road's own: a road held to a level trunk with another road 2.5 m beside it and
    /// 3 m higher (within the band two roads mix over) is held at the trunk's height. Before, the hold took any road point
    /// round the nearest one as the next point along it, and lerped toward the higher road's surface.</summary>
    [Fact]
    public void ARoadHeldToATrunkNeverReadsARoadBesideItAtAnotherHeight()
    {
        var world = new Hill { Gz = 0f };
        using var worldScope = new ValheimWorldScope().WithWorld(world);
        RoadSpatialGrid.Clear();
        try
        {
            var trunk = new List<Vector2>(); var trunkHeights = new List<float>();
            var beside = new List<Vector2>(); var besideHeights = new List<float>();
            for (int i = 0; i <= 40; i++)
            {
                trunk.Add(new Vector2(i, 0f)); trunkHeights.Add(60f);
                beside.Add(new Vector2(i, 2.5f)); besideHeights.Add(63f);
            }
            Commit(trunk, trunkHeights);
            Commit(beside, besideHeights);
            var points = new List<Vector2>(); var heights = new List<float>();
            for (int x = 10; x <= 30; x++) { points.Add(new Vector2(x, 0.3f)); heights.Add(60f); }
            var held = new bool[points.Count];
            RoadSpatialGrid.HoldToNetwork(points, heights, 4f, RoadGrade.Configured, null, false, false, held);
            for (int i = 0; i < points.Count; i++)
            {
                Assert.True(held[i]);
                Assert.True(Mathf.Abs(heights[i] - 60f) <= 1e-3f, $"at x = {points[i].x:F1}: held at {heights[i]:F3}, the trunk at 60");
            }
        }
        finally { RoadSpatialGrid.Clear(); }
    }

    /// <summary>A road held to the network keeps the schedule its landing acceptance saved, held edges too
    /// (RoadSpatialGrid.WorstOverLimit against RoadGrade.EdgeLimit): a branch run in 2.5 m beside a trunk's 30 % ramp and
    /// joined to it at the ramp's top is held up the ramp, in the band the two mix over, over its junction landing, and is
    /// refused or let go there, never built with the landing climbing at 30 %. Judged against the cap alone, the held points
    /// passed any edge under the cap, and the branch was built so.</summary>
    [Fact]
    public void ARoadHeldToTheNetworkKeepsItsLandingsLimitsOnHeldEdges()
    {
        var world = new Hill { Gz = 0f };
        PlainRoads.WithModDefaults(() =>
        {
            RoadWiggle.Amplitude = 0f;
            using var worldScope = new ValheimWorldScope().WithWorld(world);
            RoadSpatialGrid.Clear();
            try
            {
                var trunk = new List<Vector2>(); var trunkHeights = new List<float>();
                for (int i = 0; i <= 60; i++) { trunk.Add(new Vector2(i, 0f)); trunkHeights.Add(60f + 0.3f * Mathf.Clamp(i - 14, 0, 16)); }
                Commit(trunk, trunkHeights);
                var b = RoadSpatialGrid.PlanRoadPath(Line(-30, -40, 6, -2.5f, 30, -2.5f), 4f, world);
                if (b == null) return;
                Assert.NotNull(b.Grades);
                for (int i = 1; i < b.Points.Count; i++)
                {
                    // Over at least a metre back, against the rise each edge between may make.
                    float run = 0f, allowed = 0f; int j = i;
                    while (j > 0 && (run < 1f || j == i))
                    {
                        float edge = Vector2.Distance(b.Points[j - 1], b.Points[j]);
                        run += edge; allowed += RoadGrade.EdgeLimit(b.Grades, j, RoadGrade.Configured) * edge; j--;
                    }
                    float over = (Mathf.Abs(b.Heights[i] - b.Heights[j]) - allowed) / run;
                    Assert.True(over <= 2e-3f, $"at {b.Points[i]}: {over:P1} over its edges' limits, held {b.Held?[i]}, limit {RoadGrade.EdgeLimit(b.Grades, i, RoadGrade.Configured):P0}");
                }
            }
            finally { RoadSpatialGrid.Clear(); }
        });
    }

    /// <summary>A branch that joins a 20 % trunk and runs on along it lands level where it leaves the trunk, its junction
    /// landing (RoadPitches.JunctionLanding at JunctionLandingGrade) the last metres before the stretch the two share, on the
    /// production planner (RoadSpatialGrid.PlanRoadPath, RoadPitches.AcceptLanding). The shared stretch is the trunk's own: it
    /// climbs with the trunk and is not this road's landing. Before, the landing was laid on the trunk, its last 6 m, and those
    /// edges were judged at the cap instead of the landing grade; the branch climbed its 15 % approach right onto the trunk.</summary>
    [Fact]
    public void ABranchMergingAlongASteepTrunkLandsLevelBeforeTheSharedStretch()
    {
        // The trunk climbs 20 % along z; the branch comes in square to it at z = -30, climbing a 15 % slope along x, then runs
        // on along the trunk up to its end at (0, 0).
        var world = new Hill { Gz = 0.2f, Gx = -0.15f };
        PlainRoads.WithModDefaults(() =>
        {
            RoadWiggle.Amplitude = 0f;
            using var worldScope = new ValheimWorldScope().WithWorld(world);
            RoadSpatialGrid.Clear();
            try
            {
                var t = RoadSpatialGrid.PlanRoadPath(Line(0, -100, 0, 100), 4f, world);
                Assert.True(t != null, $"trunk: {RoadSpatialGrid.LastRefusal}");
                RoadSpatialGrid.Commit(t!);
                var b = RoadSpatialGrid.PlanRoadPath(Line(40, -30, 0, -30, 0, 0), 4f, world);
                Assert.True(b != null, $"branch: {RoadSpatialGrid.LastRefusal}");
                var p = b!.Points; var h = b.Heights;
                int n = p.Count;
                var along = new float[n];
                for (int i = 1; i < n; i++) along[i] = along[i - 1] + Vector2.Distance(p[i - 1], p[i]);
                // Where it leaves the trunk: the first point, from its end back, off the trunk's surface (the two 4 m roads' half
                // widths, RoadSpatialGrid.OnItsSurface).
                int k = n - 1;
                while (k > 0 && Mathf.Abs(p[k - 1].x) < 4f) k--;
                Assert.True(along[n - 1] - along[k] > 15f, $"the shared stretch is {along[n - 1] - along[k]:F1} m");
                Assert.True((h[n - 1] - h[k]) / (along[n - 1] - along[k]) > 0.15f, "the shared stretch climbs with the trunk");
                // Its landing: the edges of the JunctionLanding metres before it leaves the trunk, on the landing's schedule, and
                // each step within it, judged as the planner judges it (RoadSpatialGrid.WorstOverLimit: over at least a road step
                // back, against the rise each edge between may make). Its whole rise there is a landing's.
                int landed = 0, from = k;
                var dump = new System.Text.StringBuilder();
                for (int i = 1; i <= k; i++)
                {
                    if (along[k] - along[i - 1] > RoadPitches.JunctionLanding - 0.5f) continue;
                    from = Mathf.Min(from, i - 1);
                    landed++;
                    dump.Append(System.FormattableString.Invariant($" ({p[i].x:F1},{p[i].y:F1}) {h[i]:F2}"));
                    Assert.True(RoadGrade.EdgeLimit(b.Grades, i, RoadGrade.Configured) <= RoadPitches.JunctionLandingGrade + 1e-4f,
                        $"at {p[i]}, {along[k] - along[i]:F1} m before the trunk: its schedule {RoadGrade.EdgeLimit(b.Grades, i, RoadGrade.Configured):P0}, not the landing");
                    float run = 0f, allowed = 0f; int j = i;
                    while (j > 0 && (run < RoadSpatialGrid.RoadStep(4f) || j == i))
                    {
                        float edge = Vector2.Distance(p[j - 1], p[j]);
                        run += edge; allowed += RoadGrade.EdgeLimit(b.Grades, j, RoadGrade.Configured) * edge; j--;
                    }
                    Assert.True((Mathf.Abs(h[i] - h[j]) - allowed) / run <= 2e-3f, $"at {p[i]}: {Mathf.Abs(h[i] - h[j]) / run:P1} over {run:F1} m");
                }
                Assert.True(Mathf.Abs(h[k] - h[from]) <= RoadPitches.JunctionLandingGrade * (along[k] - along[from]) + 0.02f,
                    $"the landing climbs {h[k] - h[from]:F2} m over {along[k] - along[from]:F1} m:{dump}");
                Assert.True(landed >= 4, $"{landed} landing edges before the trunk");
            }
            finally { RoadSpatialGrid.Clear(); }
        });
    }

    /// <summary>Why a branch planned along <paramref name="branch"/> after a trunk along <paramref name="trunk"/> is refused,
    /// or null where it is built.</summary>
    private static string? Refusal(Hill world, List<Vector2> trunk, List<Vector2> branch)
    {
        string? why = null;
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
                why = RoadSpatialGrid.PlanRoadPath(branch, 4f, world) == null ? RoadSpatialGrid.LastRefusal ?? "refused" : null;
            }
            finally { RoadSpatialGrid.Clear(); }
        });
        return why;
    }

    /// <summary>A branch joining a steep trunk lands level where it leaves it (RoadPitches.AcceptLanding), and the stretch the
    /// two share has the trunk's heights. One converging on a 25 % trunk at a shallow angle has its landing beside the trunk, in
    /// the band the two surfaces mix over, held up the trunk's climb: it has no room to land there and is refused (BRIEF-RULES
    /// 17), never built with its landing on the trunk judged at the cap. Coming in square to the same trunk it lands level and
    /// meets it.</summary>
    [Fact]
    public void ABranchMergingUpASteepTrunkMeetsItLevel()
    {
        // A 25 % trunk: steep, and within the cap with its rests (at 30 % its climb is too steep for them and not built).
        var world = new Hill { Gz = 0.25f, Bump = 2f };
        List<Vector2> trunk = Line(0, -100, 0, 100);
        Assert.Equal("held to a road over the grade cap", Refusal(world, trunk, Line(25, -70, 12, -35, 4, -12, 0, 0)));
        Assert.InRange(WorstOnTrunk(world, trunk, Line(40, -40, 0, -40, 0, 0)), 0f, 0.3f);
    }

    /// <summary>A stretch a road shares with a trunk has the trunk's heights: one coming in square to a 12 % trunk and running
    /// on along it to its end, and one running along a 10 % trunk between two ends off it. Converging on the 12 % trunk at a
    /// shallow angle, the first has no room for its landing where it leaves the trunk and is refused (BRIEF-RULES 17).</summary>
    [Fact]
    public void AStretchSharedWithAnotherRoadHasItsHeights()
    {
        List<Vector2> trunk = Line(0, -100, 0, 100);
        Assert.Equal("held to a road over the grade cap", Refusal(new Hill { Bump = 4f }, trunk, Line(60, -120, 30, -60, 6, -20, 0, 0, 0, 30, 0, 60)));
        Assert.InRange(WorstOnTrunk(new Hill { Bump = 4f }, trunk, Line(60, -20, 0, -20, 0, 0, 0, 30, 0, 60)), 0f, 0.3f);
        Assert.InRange(WorstOnTrunk(new Hill { Gz = 0.1f, Bump = 6f }, trunk,
            Line(300, -200, 150, -120, 40, -60, 4, -30, 0, -20, 0, 0, 0, 20, 0, 40, 6, 50, 40, 70)), 0f, 0.3f);
    }

    /// <summary>A branch run on along a trunk over a ridge takes the trunk's cut, coming in square to it on the level ground
    /// before the ridge. Converging on the trunk on the ridge's flank, its landing is held up the flank where it leaves the
    /// trunk: no room, refused (BRIEF-RULES 17).</summary>
    [Fact]
    public void ABranchOverARidgeTakesTheTrunksCut()
    {
        var world = new Hill { Gz = 0f, Ridge = 14f };
        List<Vector2> trunk = Line(0, -100, 0, 100);
        Assert.Equal("held to a road over the grade cap", Refusal(world, trunk, Line(60, -120, 30, -70, 6, -40, 0, -30, 0, 0, 0, 30, 0, 50)));
        Assert.InRange(WorstOnTrunk(world, trunk, Line(60, -40, 0, -40, 0, 0, 0, 30, 0, 50)), 0f, 0.3f);
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
                foreach (var j in junctions!) worst = Mathf.Max(worst, j.Fill);
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

    /// <summary>A junction's fill and its pull are two measurements, each at its own worst point. A road coming square to a
    /// road laid on a 6 m fill stands on that fill at the edge of the other road's reach, where its own profile is near the
    /// fill's height (little pull), and is pulled furthest on the other road's surface, where it adds no fill. Read at the
    /// point of greatest pull, its fill was nothing and the junction ridge was missed; the fill rule refuses it at the point
    /// of most fill, and the profile re-route still points at the point of greatest pull.</summary>
    [Fact]
    public void AJunctionsFillIsItsMostFillNotItsFillWhereItIsPulledFurthest()
    {
        // With the mod's own earthwork cap: the profile re-route runs only with one.
        PlainRoads.WithModDefaults(() =>
        {
            var world = new Hill { Gz = 0f };
            using var worldScope = new ValheimWorldScope().WithWorld(world);
            RoadSpatialGrid.Clear();
            try
            {
                var trunk = RoadSpatialGrid.PlanRoadPath(Line(-60f, 0f, 60f, 0f), 4f, world)!;
                for (int i = 0; i < trunk.Heights.Count; i++) trunk.Heights[i] = 66f;
                RoadSpatialGrid.Commit(trunk);
                // Square to it from the low ground: near the fill's top (pulled 0.5 m) out to 2 m from its centre, then
                // dipping 4 m under it on its surface (pulled 4 m, past the profile limit, on no fill of its own).
                var points = new List<Vector2>();
                var heights = new List<float>();
                for (int z = -24; z <= -1; z++) { points.Add(new Vector2(0f, z)); heights.Add(z >= -2 ? 62f : 65.5f); }
                var junctions = RoadSpatialGrid.HoldToNetwork(points, heights, 4f, 0.35f, p => world.GetHeight(p.x, p.y));
                var plan = new RoadSpatialGrid.PlannedPath(points, heights, points.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 23f, false)
                { Junctions = junctions };
                var plans = new List<RoadSpatialGrid.PlannedPath> { plan };

                string? refused = RoadNetworkGenerator.BuiltRoadRefusal(plans, new List<RoadCrossing>(), out Vector2 at);
                Assert.True(refused != null && refused.StartsWith("a junction ridge"), $"refused: {refused ?? "nothing"}");
                Assert.True(at.y < -3.9f, $"the junction ridge is placed at {at}, not where it stands on the fill");
                Assert.StartsWith("held to a road", RoadNetworkGenerator.SearchedRound(plans, out Vector2 pulledAt));
                Assert.True(pulledAt.y > -2.1f, $"the profile re-route is placed at {pulledAt}, not where the road was pulled furthest");
            }
            finally { RoadSpatialGrid.Clear(); }
        });
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
            // Stretches of 10 m that may climb at the cap and rests of 6 m that may climb 0.1; the road climbs 0.6 of each.
            var points = new List<Vector2>();
            var grades = new List<float> { 0f };
            var own = new List<float> { 60f };
            for (int i = 0; i <= 60; i++) points.Add(new Vector2(i * 2f, 0f));
            for (int i = 1; i < points.Count; i++)
            {
                float limit = (i - 1) % 8 < 5 ? RoadGrade.Configured : 0.1f;
                grades.Add(limit);
                own.Add(own[i - 1] + 0.6f * limit * 2f);
            }
            float cap = RoadGrade.Configured;
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

    /// <summary>A road with a floor (a raised ford's surface, dry land's) stays on it where it is held to a road a step or less
    /// below it: held at its floor, its junction record says so. Held to one more than a step below its floor where the two
    /// surfaces touch (a wade's bank beside dry land's road stood 0.9 m under it), the two do not meet: the road there is let
    /// go, and over the other road's surface it is refused (BRIEF-RULES 17).</summary>
    [Theory]
    [InlineData(59.4f, true)]
    [InlineData(57f, false)]
    public void ARoadsFloorHoldsWhereItIsHeldToARoadAStepBelowItElseItIsNotBuilt(float below, bool built)
    {
        var world = new Hill { Gz = 0f };
        using var worldScope = new ValheimWorldScope().WithWorld(world);
        RoadSpatialGrid.Clear();
        try
        {
            var across = Line(0f, -40f, 0f, 40f);
            RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(across, across.ConvertAll(_ => below), across.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 80f, false));
            var plan = RoadSpatialGrid.PlanRoadPath(Line(-40f, 0f, 40f, 0f), 4f, world, minHeight: 59.5f);
            if (!built) { Assert.Null(plan); return; }
            Assert.NotNull(plan);
            Assert.NotNull(plan!.Junctions);
            Assert.All(plan.Junctions!, j => Assert.InRange(j.Pull, -0.6f, 0.01f));
            float lowest = float.MaxValue;
            foreach (float h in plan.Heights) lowest = Mathf.Min(lowest, h);
            Assert.True(lowest >= 59.5f - 0.001f, $"the road dips to {lowest:F2} under its 59.5 m floor");
        }
        finally { RoadSpatialGrid.Clear(); }
    }

    /// <summary>Flat swamp ground at 29.6 m, under dry land's road floor (RoadConstants.DryRoadFloor).</summary>
    private sealed class Swamp : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 29.6f;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Swamp;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    /// <summary>A road on dry land's floor (30.5 m) from the north ending on a stored wade's bed, 29.7 to 29.8 m under it (seen on
    /// a generated network: the road stood 0.79 m over the bed it ended on). The two do not meet within a step: the road is
    /// refused, or where it is built its end meets the wade as two pieces of road meet (RoadNetworkGenerator.PiecesMeet).</summary>
    [Fact]
    public void ARoadOnDryLandsFloorEndingOnAWadesBedIsRefusedOrMeetsIt()
    {
        var world = new Swamp();
        using var worldScope = new ValheimWorldScope().WithWorld(world);
        RoadSpatialGrid.Clear();
        try
        {
            // The wade as stored: its bank at 30.55 m, down to its bed (29.71 m) and up to its far bank at 30.51 m.
            var wade = new List<Vector2>();
            var bed = new List<float>();
            float[] xs = { 40.27f, 41.44f, 42.49f, 43.37f, 44.00f, 44.58f, 45.33f, 46.20f, 47.19f, 48.27f, 49.43f, 50.64f, 51.88f, 53.12f, 54.36f,
                55.57f, 56.73f, 57.81f, 58.80f, 59.67f, 60.42f, 61.00f, 61.33f, 61.75f, 62.23f, 62.78f, 63.37f, 64.00f, 64.65f };
            float[] zs = { -4183.21f, -4183.45f, -4183.68f, -4183.87f, -4184f, -4184f, -4184f, -4184f, -4184f, -4184f, -4184f, -4184f, -4184f, -4184f, -4184f,
                -4184f, -4184f, -4184f, -4184f, -4184f, -4184f, -4184f, -4184.41f, -4184.92f, -4185.51f, -4186.18f, -4186.90f, -4187.67f, -4188.48f };
            float[] ys = { 30.89f, 30.80f, 30.71f, 30.62f, 30.55f, 30.45f, 30.33f, 30.19f, 30.03f, 29.89f, 29.86f, 29.83f, 29.80f, 29.77f, 29.74f,
                29.71f, 29.82f, 29.99f, 30.15f, 30.30f, 30.42f, 30.51f, 30.58f, 30.66f, 30.76f, 30.87f, 31.00f, 31.13f, 31.09f };
            for (int i = 0; i < xs.Length; i++) { wade.Add(new Vector2(xs[i], zs[i])); bed.Add(ys[i]); }
            // Stored as built: the road on land either side, and between its banks (44 m to 61 m) the wade painted on the bed.
            void Store(int from, int to, bool painted)
            {
                var pts = wade.GetRange(from, to - from + 1);
                RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(pts, bed.GetRange(from, to - from + 1),
                    pts.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, Vector2.Distance(pts[0], pts[pts.Count - 1]), painted));
            }
            Store(0, 4, false);
            Store(4, 21, true);
            Store(21, xs.Length - 1, false);
            // The road from a raised ford's end to the north, on dry land's floor, onto the wade.
            var road = Line(56f, -4167f, 56f, -4176f, 55.5f, -4181.1f, 56f, -4184f, 57.5f, -4184.95f);
            var plan = RoadSpatialGrid.PlanRoadPath(road, 4f, world, startGround: RoadConstants.DryRoadFloor, minHeight: RoadConstants.DryRoadFloor);
            if (plan == null) return;
            // Built, every point of it meets each walked point of the wade within the walk gate's reach as two pieces meet.
            for (int i = 0; i < plan.Points.Count; i++)
                for (int k = 0; k < wade.Count; k++)
                {
                    float d = Vector2.Distance(plan.Points[i], wade[k]);
                    if (d > 1.5f) continue;
                    float rise = Mathf.Abs(plan.Heights[i] - bed[k]);
                    Assert.True(RoadNetworkGenerator.PiecesMeet(rise, d),
                        System.FormattableString.Invariant($"the road at ({plan.Points[i].x:F1}, {plan.Points[i].y:F1}) stands {rise:F2} m off the wade {d:F2} m away"));
                }
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
