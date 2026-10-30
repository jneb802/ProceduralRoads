using Xunit;
namespace ProceduralRoads.Tests;
public class MeanderTests
{
    [Fact]
    public void TheFieldIsSmoothBoundedAndRepeatable()
    {
        float lo = 1f, hi = 0f, jump = 0f;
        for (int i = 0; i < 2000; i++)
        {
            float x = i * 0.013f - 7f, y = i * 0.007f + 3f;
            float v = RoadPathfinder.MeanderField(x * 150f, y * 150f);
            Assert.InRange(v, 0f, 1f);
            Assert.Equal(v, RoadPathfinder.MeanderField(x * 150f, y * 150f));
            lo = System.Math.Min(lo, v); hi = System.Math.Max(hi, v);
            jump = System.Math.Max(jump, System.Math.Abs(v - RoadPathfinder.MeanderField((x + 0.013f) * 150f, (y + 0.007f) * 150f)));
        }
        // It varies (a flat field would bend nothing) and has no steps.
        Assert.True(hi - lo > 0.3f, $"range {lo}..{hi}");
        Assert.True(jump < 0.08f, $"largest change between neighbouring samples {jump}");
    }

    [Fact]
    public void ThePullToleranceKeepsBendsAndDropsTheStaircase()
    {
        var staircase = new System.Collections.Generic.List<UnityEngine.Vector2> { new(0, 0), new(8, 3), new(16, 0), new(24, 3), new(32, 0) };
        Assert.True(RoadPathPull.WithinTolerance(staircase, 0, 4, 4f));
        var bend = new System.Collections.Generic.List<UnityEngine.Vector2> { new(0, 0), new(8, 6), new(16, 9), new(24, 6), new(32, 0) };
        Assert.False(RoadPathPull.WithinTolerance(bend, 0, 4, 4f));
    }

    [Fact]
    public void TheSwayIsBoundedAndDoesNotRepeat()
    {
        float lo = 1f, hi = -1f;
        var a = new float[400];
        for (int i = 0; i < a.Length; i++) { a[i] = RoadWiggle.Sway(i * 4f, 11.7f); lo = System.Math.Min(lo, a[i]); hi = System.Math.Max(hi, a[i]); }
        Assert.InRange(lo, -1f, 1f); Assert.InRange(hi, -1f, 1f);
        Assert.True(hi - lo > 0.4f, $"range {lo}..{hi}");
        // No period: the sway 97 m (one long wave) on is not the sway here.
        float diff = 0f;
        for (int i = 0; i < 300; i++) diff += System.Math.Abs(a[i] - RoadWiggle.Sway(i * 4f + 97f, 11.7f));
        Assert.True(diff / 300f > 0.05f, $"mean difference one long wave apart {diff / 300f}");
    }

    [Fact]
    public void ResamplingKeepsBothEnds()
    {
        var path = new System.Collections.Generic.List<UnityEngine.Vector2> { new(0, 0), new(50, 0), new(50, 37) };
        var even = RoadWiggle.Resample(path, 12f);
        Assert.Equal(path[0], even[0]);
        Assert.Equal(path[2], even[even.Count - 1]);
        for (int i = 1; i < even.Count - 1; i++) Assert.InRange(UnityEngine.Vector2.Distance(even[i - 1], even[i]), 6f, 12.01f);
    }

    [Fact]
    public void ASwayThatMovesMoreEarthIsNotKept()
    {
        System.Func<UnityEngine.Vector2, float> ground = q => q.y;   // ground rises to the north
        var plain = new System.Collections.Generic.List<UnityEngine.Vector2> { new(0, 0), new(10, 0), new(20, 0) };
        var level = new System.Collections.Generic.List<float> { 0f, 0f, 0f };
        var north = new System.Collections.Generic.List<UnityEngine.Vector2> { new(0, 0), new(10, 3), new(20, 0) };
        // Swayed 3 m north onto ground 3 m higher: a 3 m cut the plain road does not have.
        Assert.False(RoadWiggle.NoMoreEarth(north, level, plain, level, ground));
        var slight = new System.Collections.Generic.List<UnityEngine.Vector2> { new(0, 0), new(10, 0.4f), new(20, 0) };
        Assert.True(RoadWiggle.NoMoreEarth(slight, level, plain, level, ground));
    }

    [Fact]
    public void ASwayWithMorePointsPastTheLimitButNoneDeeperNamesNoSpotToHold()
    {
        // The plain road 9 m deep at one point; the swayed one 8.5 m deep at two: it moves more earth (more points past the
        // terrain limit), yet none deeper than the plain road's deepest, so there is no spot to hold its sway round
        // (RoadSpatialGrid stops its hold rounds there rather than plan the same sway again).
        System.Func<UnityEngine.Vector2, float> ground = q => q.y;
        var plain = new System.Collections.Generic.List<UnityEngine.Vector2> { new(0, 0), new(10, 9), new(20, 0), new(30, 0) };
        var swayed = new System.Collections.Generic.List<UnityEngine.Vector2> { new(0, 0), new(10, 8.5f), new(20, 8.5f), new(30, 0) };
        var level = new System.Collections.Generic.List<float> { 0f, 0f, 0f, 0f };
        Assert.False(RoadWiggle.NoMoreEarth(swayed, level, plain, level, ground));
        Assert.Empty(RoadWiggle.MoreEarthAt(swayed, level, plain, level, ground));
    }

    [Fact]
    public void TheSwayEasesOnSlopesButNeverSwitchesOff()
    {
        float cap = 0.35f, flat = RoadWiggle.Flat, floor = RoadWiggle.SlopeFloor;
        Assert.Equal(1f, RoadWiggle.GradeFactor(0f, cap));
        Assert.Equal(1f, RoadWiggle.GradeFactor(flat, cap));
        float mid = RoadWiggle.GradeFactor((flat + cap) / 2f, cap);
        Assert.InRange(mid, floor + 0.01f, 0.99f);
        Assert.Equal((1f + floor) / 2f, mid, 3);
        Assert.Equal(floor, RoadWiggle.GradeFactor(cap, cap), 4);
        Assert.Equal(floor, RoadWiggle.GradeFactor(cap + 0.5f, cap), 4);
        // Falling ground counts like rising ground; no cap takes the default.
        Assert.Equal(mid, RoadWiggle.GradeFactor(-(flat + cap) / 2f, cap), 4);
        Assert.Equal(RoadWiggle.GradeFactor(0.27f, RoadConstants.DefaultMaxRoadGrade), RoadWiggle.GradeFactor(0.27f, 0f));
        // A steep slope, about 0.27 grade, keeps a real share of the sway.
        Assert.True(RoadWiggle.GradeFactor(0.27f, cap) > 0.5f);
        // Monotone non-increasing, never below the floor, deterministic.
        float prev = 2f;
        for (int i = 0; i <= 100; i++)
        {
            float g = i * 0.01f, f = RoadWiggle.GradeFactor(g, cap);
            Assert.True(f <= prev + 1e-6f && f >= floor - 1e-6f, $"grade {g}: {f}");
            Assert.Equal(f, RoadWiggle.GradeFactor(g, cap));
            prev = f;
        }
    }

    private static System.Collections.Generic.List<UnityEngine.Vector2> Straight(float x0, float x1, float step = 8f)
    {
        var path = new System.Collections.Generic.List<UnityEngine.Vector2>();
        for (float x = x0; x <= x1; x += step) path.Add(new UnityEngine.Vector2(x, 0f));
        return path;
    }

    private static System.Collections.Generic.List<UnityEngine.Vector2> Swayed(System.Func<UnityEngine.Vector2, float> ground)
    {
        System.Collections.Generic.List<UnityEngine.Vector2> swayed = null!;
        PlainRoads.WithModDefaults(() => swayed = RoadWiggle.Apply(Straight(-150f, 150f), ground, _ => false, _ => Heightmap.Biome.Meadows, 4f));
        return swayed;
    }

    // Ground that wrinkles along the road: a sideways step only meets it on a sidehill.
    private static float Wrinkles(UnityEngine.Vector2 q) => 2f * UnityEngine.Mathf.Sin(q.x / 23f) * UnityEngine.Mathf.Cos(q.y / 17f + q.x / 41f);

    [Fact]
    public void OnGentleGroundTheSwayIsTheNoiseAlone()
    {
        // Level, and a slight fall across the road over ground rolling along it: the same sway, point for point.
        var level = Swayed(_ => 60f);
        var slight = Swayed(q => 60f + 0.03f * q.y + 1.5f * UnityEngine.Mathf.Sin(q.x / 23f));
        Assert.Equal(level.Count, slight.Count);
        float furthest = 0f;
        for (int i = 0; i < level.Count; i++)
        {
            Assert.Equal(level[i].x, slight[i].x, 4);
            Assert.Equal(level[i].y, slight[i].y, 4);
            furthest = UnityEngine.Mathf.Max(furthest, UnityEngine.Mathf.Abs(level[i].y));
        }
        Assert.True(furthest > 5f, $"the sway reached {furthest:F2} m");
    }

    [Fact]
    public void OnASidehillTheSwayGoesRoundASpur()
    {
        // A 12 % climb along a hillside falling 0.3 across it, with a 3 m spur standing out of it at x = 0.
        System.Func<UnityEngine.Vector2, float> hill = q => 60f + 0.12f * q.x + 0.3f * q.y;
        System.Func<UnityEngine.Vector2, float> spur = q => hill(q) + 3f * (float)System.Math.Exp(-(q.x * q.x + q.y * q.y) / (20f * 20f));
        var plain = Swayed(hill);
        var round = Swayed(spur);
        int at = 0;
        for (int i = 0; i < round.Count; i++) if (UnityEngine.Mathf.Abs(round[i].x) < UnityEngine.Mathf.Abs(round[at].x)) at = i;
        // Out round it: down the hill, onto ground about as high as the hill's line there.
        Assert.True(round[at].y < plain[at].y - 3f, $"at the spur {round[at].y:F2} m, on the plain hill {plain[at].y:F2} m");
        Assert.InRange(spur(round[at]) - hill(plain[at]), -1f, 1f);
        float amplitude = System.Convert.ToSingle(PlainRoads.ModDefaults["sway"]);
        foreach (var p in round) Assert.InRange(p.y, -amplitude - 0.01f, amplitude + 0.01f);
    }

    [Fact]
    public void ASidehillClimbKeepsItsSwayWithinTheCapAndTheSlack()
    {
        // A 12 % climb across ground falling 0.5 to the side, wrinkled: swayed across the slope, the
        // swayed line's ground bumped at every bend and the sway was dropped for more earth.
        System.Func<UnityEngine.Vector2, float> ground = q => 60f + 0.117f * q.x + 0.5f * q.y + Wrinkles(q);
        var world = new SyntheticWorld { HasRiver = false, HasMountain = false };
        using var scope = new Valheim.Testing.Doubles.ValheimWorldScope().WithWorld(world);
        RoadSpatialGrid.Clear();
        try
        {
            PlainRoads.WithModDefaults(() =>
            {
                RoadSpatialGrid.SwayKept = RoadSpatialGrid.SwayRefused = RoadSpatialGrid.SwayMoreEarth = 0;
                var plan = RoadSpatialGrid.PlanRoadPath(Straight(-150f, 150f), 4f, world, terrainHeight: ground);
                Assert.NotNull(plan);
                Assert.Equal(1, RoadSpatialGrid.SwayKept);
                Assert.Equal(0, RoadSpatialGrid.SwayMoreEarth);
                float furthest = 0f;
                foreach (var p in plan!.Points) furthest = UnityEngine.Mathf.Max(furthest, UnityEngine.Mathf.Abs(p.y));
                Assert.True(furthest > 3f, $"the road stayed within {furthest:F2} m of the straight line");
                RoadSpatialGrid.SuppressWiggle = true;
                RoadSpatialGrid.PlannedPath? straight;
                try { straight = RoadSpatialGrid.PlanRoadPath(Straight(-150f, 150f), 4f, world, terrainHeight: ground); }
                finally { RoadSpatialGrid.SuppressWiggle = false; }
                Assert.True(RoadWiggle.NoMoreEarth(plan.Points, plan.Heights, straight!.Points, straight.Heights, ground));
                // Over every 20 m the swayed road climbs within the cap.
                for (int i = 0, j = 0; i < plan.Points.Count; i++)
                {
                    float run = 0f;
                    for (j = i; j + 1 < plan.Points.Count && run < 20f; j++) run += UnityEngine.Vector2.Distance(plan.Points[j], plan.Points[j + 1]);
                    if (run < 20f) break;
                    float grade = UnityEngine.Mathf.Abs(plan.Heights[j] - plan.Heights[i]) / run;
                    Assert.True(grade <= RoadGrade.Configured + 0.01f, $"{grade:F3} over 20 m from point {i}");
                }
            });
        }
        finally { RoadSpatialGrid.Clear(); }
    }

    [Fact]
    public void ASwayThatMovesMoreEarthInOnePlaceIsHeldOnlyThere()
    {
        // Level ground with a 4 m knoll standing where the sway swings furthest out: swayed over it, the road
        // is cut through it; the plain line passes it by.
        var spot = new UnityEngine.Vector2(0f, 0f);
        foreach (var p in Swayed(_ => 60f)) if (UnityEngine.Mathf.Abs(p.y) > UnityEngine.Mathf.Abs(spot.y)) spot = p;
        Assert.True(UnityEngine.Mathf.Abs(spot.y) > 5f, $"the sway reached {spot.y:F2} m");
        System.Func<UnityEngine.Vector2, float> ground = q =>
        {
            float d2 = (q.x - spot.x) * (q.x - spot.x) + (q.y - spot.y) * (q.y - spot.y);
            return 60f + 4f * (float)System.Math.Exp(-d2 / (3f * 3f));
        };
        var world = new SyntheticWorld { HasRiver = false, HasMountain = false };
        using var scope = new Valheim.Testing.Doubles.ValheimWorldScope().WithWorld(world);
        RoadSpatialGrid.Clear();
        try
        {
            PlainRoads.WithModDefaults(() =>
            {
                RoadSpatialGrid.SwayKept = RoadSpatialGrid.SwayKeptHeld = RoadSpatialGrid.SwayRefused = RoadSpatialGrid.SwayMoreEarth = 0;
                var plan = RoadSpatialGrid.PlanRoadPath(Straight(-150f, 150f), 4f, world, terrainHeight: ground);
                Assert.NotNull(plan);
                Assert.Equal(1, RoadSpatialGrid.SwayKept);
                Assert.Equal(1, RoadSpatialGrid.SwayKeptHeld);
                float near = 0f, away = 0f;
                foreach (var p in plan!.Points)
                {
                    float dx = UnityEngine.Mathf.Abs(p.x - spot.x);
                    if (dx < 6f) near = UnityEngine.Mathf.Max(near, UnityEngine.Mathf.Abs(p.y));
                    else if (dx > RoadWiggle.HoldRadius + RoadWiggle.ShareFade + 12f) away = UnityEngine.Mathf.Max(away, UnityEngine.Mathf.Abs(p.y));
                }
                Assert.True(near < 1f, $"beside the knoll the road is {near:F2} m off its plain line");
                Assert.True(away > 3f, $"away from the knoll the road stayed within {away:F2} m of its plain line");
            });
        }
        finally { RoadSpatialGrid.Clear(); }
    }
}
