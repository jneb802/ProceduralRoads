using System.Linq;
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
        Assert.False(RoadWiggle.NoMoreEarth(Plan(north, level), RoadWiggle.EarthOf(Plan(plain, level), ground), ground));
        var slight = new System.Collections.Generic.List<UnityEngine.Vector2> { new(0, 0), new(10, 0.4f), new(20, 0) };
        Assert.True(RoadWiggle.NoMoreEarth(Plan(slight, level), RoadWiggle.EarthOf(Plan(plain, level), ground), ground));
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
        RoadNetworkGenerator.Earthwork plainEarth = RoadWiggle.EarthOf(Plan(plain, level), ground);
        Assert.False(RoadWiggle.NoMoreEarth(Plan(swayed, level), plainEarth, ground));
        Assert.Empty(RoadWiggle.MoreEarthAt(Plan(swayed, level), plainEarth, ground));
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

    /// <summary>A plan of these points and heights, 4 m wide, for the sway's earth comparison.</summary>
    private static RoadSpatialGrid.PlannedPath Plan(System.Collections.Generic.List<UnityEngine.Vector2> points, System.Collections.Generic.List<float> heights) =>
        new(points, heights, points.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 0f, false);

    private static System.Collections.Generic.List<UnityEngine.Vector2> Swayed(System.Func<UnityEngine.Vector2, float> ground)
    {
        System.Collections.Generic.List<UnityEngine.Vector2> swayed = null!;
        PlainRoads.WithModDefaults(() => swayed = RoadWiggle.Apply(Straight(-150f, 150f), ground, _ => false, _ => Heightmap.Biome.Meadows, 4f));
        return swayed;
    }

    /// <summary>A road shaped before its crossings is kept only as the sway keeps a swayed plan (RoadWiggle.NoMoreEarth over the
    /// whole road, PlanCandidate's Plan): its pieces moving 2 m more earth than the plain road's are not kept; within the slack, or
    /// with less, they are.</summary>
    [Fact]
    public void AShapedRoadIsKeptOnlyWhereItMovesNoMoreEarthThanThePlainOne()
    {
        var line = Straight(0f, 80f, 1f);
        var flat = Plan(line, line.ConvertAll(_ => 40f));
        var filled = Plan(line, line.ConvertAll(p => p.x > 30f && p.x < 50f ? 42f : 40f));
        static float Ground(UnityEngine.Vector2 q) => 40f;
        Assert.False(RoadWiggle.NoMoreEarth(new[] { flat, filled }, new[] { flat, flat }, Ground));
        Assert.True(RoadWiggle.NoMoreEarth(new[] { flat, flat }, new[] { flat, filled }, Ground));
        Assert.True(RoadWiggle.NoMoreEarth(new[] { flat }, new[] { flat }, Ground));
    }

    /// <summary>A swamp road through shallows rising gently to one side: a waded point sways onto the drier side as on dry
    /// ground, and never toward the deeper side, where it keeps its line (RoadWiggle.Apply: never onto wetter ground). Before,
    /// a waded point never swayed at all, so a swamp road ran dead straight through its shallows.</summary>
    [Fact]
    public void AWadedPointSwaysOntoDrierGroundAndNeverWetter()
    {
        static float Shallows(UnityEngine.Vector2 q) => 29f + 0.05f * q.y;
        static bool Wet(UnityEngine.Vector2 q) => !RoadWater.BuiltDry(Shallows(q));
        System.Collections.Generic.List<UnityEngine.Vector2> swayed = null!;
        PlainRoads.WithModDefaults(() => swayed = RoadWiggle.Apply(Straight(-150f, 150f), Shallows, Wet, _ => Heightmap.Biome.Swamp, 4f));
        Assert.All(swayed, q => Assert.True(Shallows(q) >= Shallows(new UnityEngine.Vector2(q.x, 0f)) - 1e-4f, $"{q} swayed into deeper water"));
        Assert.Contains(swayed, q => q.y > 1f);
        Assert.DoesNotContain(swayed, q => q.y < -1e-3f);
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
    public void AShortSwingIsEasedToHalfALongOneKept()
    {
        // Out 8 m and back within 24 m: halved. Then out 9 m and back over 108 m: kept as it is.
        var offset = new[] { 0f, 8f, 0f, -3f, -9f, -9f, -9f, -9f, -9f, -9f, -3f, 0f };
        RoadWiggle.EaseShortWaves(offset, 12f);
        Assert.Equal(new[] { 0f, 4f, 0f, -3f, -9f, -9f, -9f, -9f, -9f, -9f, -3f, 0f }, offset);
        // The zero crossing between two swings is read between waypoints: 0.5 of the way from +6 to -6.
        var two = new[] { 0f, 6f, -6f, -6f, -6f, -6f, -6f, -6f, -6f, -6f, 0f };
        RoadWiggle.EaseShortWaves(two, 12f);
        Assert.Equal(6f * RoadWiggle.WaveShare(18f), two[1], 4);
        Assert.Equal(-6f, two[5], 4);
        Assert.Equal(0.5f, RoadWiggle.WaveShare(RoadWiggle.ShortWave), 4);
        Assert.Equal(1f, RoadWiggle.WaveShare(RoadWiggle.LongWave), 4);
        float prev = 0f;
        for (float l = 0f; l <= 120f; l += 2f) { float f = RoadWiggle.WaveShare(l); Assert.True(f >= prev); prev = f; }
    }

    [Fact]
    public void OnARoadTheShortSwingsAreHalvedAndTheLongOnesUnchanged()
    {
        // In game (1474,2574): a road swinging 5-9 m out and back every 25 m or so,
        // a bit too wavy: the sway kept, its amplitude about halved. The same 1.2 km road,
        // swayed as before (every swing full size) and now: each swing's peak, by the swing's length.
        System.Collections.Generic.List<UnityEngine.Vector2> Sway(float share, float x0)
        {
            float was = RoadWiggle.ShortWaveShare;
            RoadWiggle.ShortWaveShare = share;
            System.Collections.Generic.List<UnityEngine.Vector2> swayed = null!;
            try { PlainRoads.WithModDefaults(() => swayed = RoadWiggle.Apply(Straight(x0, x0 + 1200f), _ => 60f, _ => false, _ => Heightmap.Biome.Meadows, 4f)); }
            finally { RoadWiggle.ShortWaveShare = was; }
            return swayed;
        }
        // No protected location from another test may take a swayed point back onto the plain line (KeepClearOfLocations):
        // that splits a swing, and the swings measured here would not be the ones eased.
        var source = RoadLocationProtection.Source;
        RoadLocationProtection.Source = null; RoadLocationProtection.Reset();
        int shortSwings = 0, longSwings = 0;
        try
        {
            foreach (float x0 in new[] { -600f, 1474f, 3301f })
            {
                var today = Sway(1f, x0);
                var eased = Sway(0.5f, x0);
                Assert.Equal(today.Count, eased.Count);
                // Deterministic: the same road sways the same way.
                var again = Sway(0.5f, x0);
                for (int i = 0; i < eased.Count; i++) Assert.Equal(eased[i], again[i]);
                for (int i = 1; i < today.Count;)
                {
                    if (today[i].y == 0f) { i++; continue; }
                    int a = i; bool up = today[i].y > 0f;
                    float peakToday = 0f, peakEased = 0f;
                    while (i < today.Count && today[i].y != 0f && today[i].y > 0f == up)
                    {
                        peakToday = UnityEngine.Mathf.Max(peakToday, UnityEngine.Mathf.Abs(today[i].y));
                        peakEased = UnityEngine.Mathf.Max(peakEased, UnityEngine.Mathf.Abs(eased[i].y));
                        i++;
                    }
                    // The swing's length, from the waypoint before it to the one after it: no shorter than the swing.
                    float length = (i - a + 1) * RoadWiggle.Spacing;
                    if (length <= RoadWiggle.ShortWave && peakToday > 1f)
                    {
                        shortSwings++;
                        Assert.InRange(peakEased / peakToday, 0.49f, 0.51f);
                    }
                    else if (length - 2f * RoadWiggle.Spacing >= RoadWiggle.LongWave)
                    {
                        longSwings++;
                        Assert.Equal(peakToday, peakEased, 3);
                    }
                }
            }
        }
        finally { RoadLocationProtection.Source = source; RoadLocationProtection.Reset(); }
        Assert.True(shortSwings > 0, "no short swing to ease");
        Assert.True(longSwings > 0, "no long swing to keep");
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
                Assert.True(RoadWiggle.NoMoreEarth(plan, RoadWiggle.EarthOf(straight!, ground), ground));
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
    [Fact]
    public void ASwayWhoseEdgeAloneCutsAWallIsHeldThere()
    {
        // Level ground with a wall 9 m high just outside where the sway swings furthest out: swayed there, the road's
        // centre stays clear of it and its outer edge is cut 9 m into it, past the shaped limit; the plain line passes it
        // by. Earth is measured across the road's width, as the earthwork cap measures it, so the sway is held there.
        // Measured on the centre line alone, the swayed road moved no more earth and was kept with the cut at its edge.
        var spot = new UnityEngine.Vector2(0f, 0f);
        foreach (var p in Swayed(_ => 60f)) if (UnityEngine.Mathf.Abs(p.y) > UnityEngine.Mathf.Abs(spot.y)) spot = p;
        Assert.True(UnityEngine.Mathf.Abs(spot.y) > 5f, $"the sway reached {spot.y:F2} m");
        float side = spot.y < 0f ? -1f : 1f, edge = spot.y + side * 2f;
        System.Func<UnityEngine.Vector2, float> ground = q =>
            60f + (UnityEngine.Mathf.Abs(q.x - spot.x) < 6f && UnityEngine.Mathf.Abs(q.y - edge) <= 0.6f ? 9f : 0f);
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
                float near = 0f;
                foreach (var p in plan!.Points)
                    if (UnityEngine.Mathf.Abs(p.x - spot.x) < 6f) near = UnityEngine.Mathf.Max(near, UnityEngine.Mathf.Abs(p.y));
                Assert.True(near < UnityEngine.Mathf.Abs(spot.y) - 1f, $"beside the wall the road is {near:F2} m off its plain line");
            });
        }
        finally { RoadSpatialGrid.Clear(); }
    }

    private sealed class ForestWorld : SyntheticWorld
    {
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.BlackForest;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APieceMeetingAStructuresEndSwaysAwayFromItsHeldFoot(bool atEnd)
    {
        // A Black Forest piece meeting a structure's end, its foot and apron held (RoadSpatialGrid.HeldPoints), at the
        // piece's end or mid-way: the road keeps every held point exactly, and sways further than the hold and its fade
        // from them. Unswayed, the whole piece ran dead straight.
        var road = Straight(-300f, 300f);
        var feet = atEnd
            ? new System.Collections.Generic.List<UnityEngine.Vector2> { new UnityEngine.Vector2(300f, 0f), new UnityEngine.Vector2(284f, 0f) }
            : new System.Collections.Generic.List<UnityEngine.Vector2> { new UnityEngine.Vector2(-100f, 0f), new UnityEngine.Vector2(-92f, 0f) };
        foreach (var f in feet) Assert.Contains(f, road);
        var world = new ForestWorld { HasRiver = false, HasMountain = false };
        using var scope = new Valheim.Testing.Doubles.ValheimWorldScope().WithWorld(world);
        RoadSpatialGrid.Clear();
        RoadSpatialGrid.HeldPoints = feet;
        try
        {
            PlainRoads.WithModDefaults(() =>
            {
                var plan = RoadSpatialGrid.PlanRoadPath(road, 4f, world, terrainHeight: _ => 60f);
                Assert.NotNull(plan);
                foreach (var f in feet)
                {
                    float onFoot = float.MaxValue;
                    foreach (var p in plan!.Points) onFoot = UnityEngine.Mathf.Min(onFoot, (p - f).magnitude);
                    Assert.True(onFoot < 0.5f, $"the road passes {onFoot:F2} m from the held point {f}");
                }
                float near = 0f, away = 0f;
                foreach (var p in plan!.Points)
                {
                    float dx = float.MaxValue;
                    foreach (var f in feet) dx = UnityEngine.Mathf.Min(dx, UnityEngine.Mathf.Abs(p.x - f.x));
                    if (dx < RoadWiggle.HoldRadius - RoadWiggle.Spacing) near = UnityEngine.Mathf.Max(near, UnityEngine.Mathf.Abs(p.y));
                    else if (dx > RoadWiggle.HoldRadius + RoadWiggle.ShareFade + RoadWiggle.Spacing) away = UnityEngine.Mathf.Max(away, UnityEngine.Mathf.Abs(p.y));
                }
                Assert.True(near < 0.5f, $"round the held points the road is {near:F2} m off its plain line");
                Assert.True(away > 3f, $"away from the held points the road stayed within {away:F2} m of its plain line");
            });
        }
        finally { RoadSpatialGrid.HeldPoints = null; RoadSpatialGrid.Clear(); }
    }

    [Fact]
    public void ResamplingKeepsTheKeptWaypoints()
    {
        var path = Straight(0f, 200f, 10f);
        var keep = new System.Collections.Generic.List<UnityEngine.Vector2> { new UnityEngine.Vector2(50f, 0f), new UnityEngine.Vector2(130f, 0f) };
        var even = RoadWiggle.ResampleKeeping(path, 12f, keep);
        foreach (var k in keep) Assert.Contains(k, even);
        Assert.Equal(path[0], even[0]);
        Assert.Equal(path[path.Count - 1], even[even.Count - 1]);
        for (int i = 1; i < even.Count; i++) Assert.InRange(even[i].x - even[i - 1].x, 2.9f, 1.25f * 12f + 0.01f);
    }
    /// <summary>The sway never moves a dry point onto a river's core, whatever the ground's height there: the wet test the
    /// network's sway reads (RoadSpatialGrid's, RoadWater.BuiltDry) is wet on a river's core at any height, so a point onto
    /// ground as high or higher is still a dry point moved into water. A river's core beside the road on level ground: the
    /// road keeps its line where the sway would reach it, and sways as before where nothing beside it is wet.</summary>
    [Fact]
    public void TheSwayNeverMovesADryPointOntoARiversCore()
    {
        System.Collections.Generic.List<UnityEngine.Vector2> Sway(System.Func<UnityEngine.Vector2, float> river)
        {
            System.Collections.Generic.List<UnityEngine.Vector2> swayed = null!;
            System.Func<UnityEngine.Vector2, float> ground = _ => 60f;
            PlainRoads.WithModDefaults(() => swayed = RoadWiggle.Apply(Straight(-150f, 150f), ground,
                q => !RoadWater.BuiltDry(ground(q), river(q)), _ => Heightmap.Biome.Meadows, 4f));
            return swayed;
        }
        float Furthest(System.Collections.Generic.List<UnityEngine.Vector2> line) => line.ConvertAll(q => UnityEngine.Mathf.Abs(q.y)).Max();
        Assert.True(Furthest(Sway(_ => 0f)) > 5f, "the sway does not reach off the line where nothing is wet");
        // Everything more than a metre off the line a river's core, its ground as high as the road's.
        var held = Sway(q => UnityEngine.Mathf.Abs(q.y) > 1f ? 1f : 0f);
        Assert.True(Furthest(held) <= 1f, $"a dry point swayed {Furthest(held):F2} m onto a river's core");
    }
}
