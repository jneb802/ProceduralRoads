using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Where a staircase starts, the road ends on a level landing at its full width (RoadSpatialGrid.StairStarts): a road held at
/// a staircase's foot ends on the level landing a join ends on (RoadPitches.ApplyJunctionLanding, its last JunctionLanding
/// metres at JunctionLandingGrade), an approach laid from a road's end once the road is planned levels the road's end the same
/// way (RoadPitches.LandEnd), and no tight switchback's taper runs into either (RoadSwitchbacks.Shape). Read in game: a road's
/// last 8 m at a pitch's grade, narrowed by a tight switchback 15 m before it, ran into an approach's foot.
/// </summary>
[Collection("RoadStatics")]
public class StairStartLandingTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    private sealed class Ground : WorldGenerator
    {
        public Func<float, float, float> Height = (_, _) => 40f;
        public override float GetHeight(float wx, float wy) => Height(wx, wy);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
        public override int GetSeed() => 91;
    }

    private readonly Ground _world = new();

    public StairStartLandingTests()
    {
        _scope.WithWorld(_world);
        RoadNetworkGenerator.Reset();
    }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    /// <summary>The steepest step of <paramref name="plan"/> within <paramref name="length"/> metres of its last point, along it.</summary>
    private static float SteepestNearEnd(RoadSpatialGrid.PlannedPath plan, float length)
    {
        float steepest = 0f, along = 0f;
        for (int i = plan.Points.Count - 1; i > 0 && along < length - 1e-3f; i--)
        {
            float run = Vector2.Distance(plan.Points[i - 1], plan.Points[i]);
            along += run;
            if (run > 1e-3f) steepest = Mathf.Max(steepest, Mathf.Abs(plan.Heights[i] - plan.Heights[i - 1]) / run);
        }
        return steepest;
    }

    /// <summary>A climb too steep for its rests within the grade cap is not built ("if a climb is too steep we shouldn't
    /// climb there"): a 200 m road up 32 % ground, planned at the mod's own settings through the generation planner, keeps
    /// every rest or is refused, never stored with its rests dropped or an edge past the cap. 25 % ground keeps its rests
    /// and is built.</summary>
    [Theory]
    [InlineData(0.25f, true)]
    [InlineData(0.32f, false)]
    public void AClimbTooSteepForItsRestsIsRefused(float grade, bool built)
    {
        _world.Height = (x, _) => 30f + grade * Mathf.Clamp(x, 0f, 200f);
        List<RoadSpatialGrid.PlannedPath>? plans = null;
        PlainRoads.WithModDefaults(() =>
        {
            var path = new List<Vector2>();
            for (float x = -20f; x <= 220f + 1e-3f; x += 2f) path.Add(new Vector2(x, 0f));
            plans = RoadNetworkGenerator.PlanRoadPathWithCrossings(path, new List<RoadCrossing>(), 4f, null, null, new List<RoadStairSpan>());
        });
        if (!built)
        {
            Assert.Null(plans);
            Assert.Equal(RoadSpatialGrid.TooSteepForRestsRefusal, RoadSpatialGrid.LastRefusal);
            return;
        }
        Assert.True(plans != null, RoadSpatialGrid.LastRefusal);
        foreach (var plan in plans!)
        {
            Assert.True(RoadGrade.SteepestStep(plan.Points, plan.Heights) <= RoadGrade.Configured + 1e-3f);
            Assert.True(plan.Grades != null && plan.Grades.Exists(g => g <= RoadPitches.EaseGrade + 1e-4f), "no rest on the climb");
        }
    }

    /// <summary>No stored step of a road past the grade cap, at the mod's own settings (rests, the landing at a staircase's
    /// foot, ground following), on the planner generation stores roads from (RoadNetworkGenerator.PlanRoadPathWithCrossings).
    /// Read on a built network: a road climbing about 30 % to a staircase's foot 31 m on dropped 0.48 m over 0.97 m (50 %)
    /// beside its rests, where pitches beside a rest were allowed to 50 %. A climb that cannot keep its rests under the cap
    /// loses rests, never the cap: every edge is within it, or the road is refused.</summary>
    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.30f)]
    [InlineData(0.34f)]
    public void ARoadClimbingToAStaircasesFootIsWithinTheCapOnEveryEdge(float grade)
    {
        const float foot = 64f;
        _world.Height = (x, _) => x <= foot ? 30f + grade * x : 30f + grade * foot + 0.75f * (x - foot);
        List<RoadSpatialGrid.PlannedPath>? plans = null;
        PlainRoads.WithModDefaults(() =>
        {
            var path = new List<Vector2>();
            for (float x = 0f; x <= foot + 1e-3f; x += 2f) path.Add(new Vector2(x, 0f));
            path.Add(new Vector2(foot + 8f, 0f));
            var stair = new RoadStairSpan
            {
                FromIndex = path.Count - 2, ToIndex = path.Count - 1, FromPoint = new Vector2(foot, 0f), ToPoint = new Vector2(foot + 8f, 0f),
                FromHeight = _world.Height(foot, 0f), ToHeight = _world.Height(foot, 0f) + 6f,
            };
            plans = RoadNetworkGenerator.PlanRoadPathWithCrossings(path, new List<RoadCrossing>(), 4f, null, null, new List<RoadStairSpan> { stair });
        });
        if (plans == null) return;
        foreach (var plan in plans)
        {
            if (plan.FollowTerrain) continue;
            float steepest = RoadGrade.SteepestStep(plan.Points, plan.Heights);
            Assert.True(steepest <= RoadGrade.Configured + 1e-3f, $"a {grade:P0} climb to a staircase's foot is stored at {steepest:P1}");
        }
    }

    /// <summary>A road climbing at 20 % along +x to a staircase's foot at x = 32 (the ground there 70 m), the staircase up from
    /// it to x = 40 the road's end. Held at the foot's height, its last 6 m into the foot are a level landing; before, they
    /// climbed with the ground. At 25 % the foot's landing and the climb's rest leave too little room under the cap: the road
    /// is refused (a rest is never dropped, and a road never runs its own grade into a foot: BRIEF-RULES 17).</summary>
    [Theory]
    [InlineData(0.20f, true)]
    [InlineData(0.25f, false)]
    public void ARoadHeldAtAStaircasesFootEndsOnALevelLandingOrIsRefused(float grade, bool built)
    {
        _world.Height = (x, _) => x <= 32f ? 70f - grade * (32f - x) : 70f + 0.75f * (x - 32f);
        using var overrides = StaticOverride.Keep(() => RoadPitches.Enabled).AndKeep(() => RoadSpatialGrid.FollowGround)
            .AndKeep(() => RoadPitches.JunctionLanding);
        RoadPitches.Enabled = true;
        RoadSpatialGrid.FollowGround = true;
        RoadPitches.JunctionLanding = (float)PlainRoads.ModDefaults["junction landing"];
        var path = new List<Vector2>();
        for (float x = 0f; x <= 32f + 1e-3f; x += 4f) path.Add(new Vector2(x, 0f));
        path.Add(new Vector2(40f, 0f));
        var stair = new RoadStairSpan
        {
            FromIndex = path.Count - 2, ToIndex = path.Count - 1, FromPoint = new Vector2(32f, 0f), ToPoint = new Vector2(40f, 0f),
            FromHeight = 70f, ToHeight = 76f,
        };
        var plans = RoadNetworkGenerator.PlanRoadPathWithCrossings(path, new List<RoadCrossing>(), 4f, null, null, new List<RoadStairSpan> { stair });
        if (!built)
        {
            Assert.Null(plans);
            Assert.Contains(RoadSpatialGrid.LastRefusal, new[] { RoadSpatialGrid.TooSteepForRestsRefusal, RoadSpatialGrid.NoFootLandingRefusal });
            return;
        }
        Assert.True(plans != null, RoadSpatialGrid.LastRefusal);
        RoadSpatialGrid.PlannedPath toFoot = plans![0];
        Assert.True(Vector2.Distance(toFoot.Points[toFoot.Points.Count - 1], stair.FromPoint) < 0.5f);
        Assert.True(Mathf.Abs(toFoot.Heights[toFoot.Heights.Count - 1] - 70f) < 0.01f,
            string.Join(" ", toFoot.Points.Select((p, i) => $"({p.x:F1},{p.y:F1}):{toFoot.Heights[i]:F2}")));
        Assert.True(SteepestNearEnd(toFoot, RoadPitches.JunctionLanding) <= RoadPitches.JunctionLandingGrade + 1e-3f,
            $"into the foot at {SteepestNearEnd(toFoot, RoadPitches.JunctionLanding):P1}");
        // Not a location's landing: the count of those is the locations'.
        Assert.False(toFoot.LocationLandingStart || toFoot.LocationLandingEnd);
    }

    /// <summary>A road starting again at a staircase's top, down ground falling away at 25 % from it (offline, the top at
    /// 5137.8,3673.0 on a stone house's road: its pad level at the top's 47.50 m, the road beside it 0.96 m lower 2.8 m out).
    /// The pad (RoadEndRing.PadUnder) is level at the top's height over the road's end disc (RoadEndRing.RoadsEnd); the road
    /// lands on it (RoadSpatialGrid.StairStarts, RoadPitches.AcceptLanding): its first JunctionLanding metres at the landing's
    /// grade, every road point within the pad's reach within a walker's step of it (StairLayout.Walkable, the walk rule).
    /// Before, the road kept its own grade from the top's ring and stood up to a metre off the pad's edge. Down ground falling at
    /// 33 % the landing leaves the rest of the road no room under the cap: refused, never kept at its own grade.</summary>
    [Fact]
    public void ARoadStartingAgainAtAStaircasesTopLandsOnItsPad()
    {
        const float top = 47.5f;
        float fall = 0.25f;
        _world.Height = (x, _) => x <= 0f ? top - 6f + 0.75f * (x + 8f) : top - fall * x;
        using var overrides = StaticOverride.Keep(() => RoadPitches.Enabled).AndKeep(() => RoadSpatialGrid.FollowGround)
            .AndKeep(() => RoadPitches.JunctionLanding);
        RoadPitches.Enabled = true;
        RoadSpatialGrid.FollowGround = true;
        RoadPitches.JunctionLanding = (float)PlainRoads.ModDefaults["junction landing"];
        var path = new List<Vector2> { new(-8f, 0f) };
        for (float x = 0f; x <= 40f + 1e-3f; x += 4f) path.Add(new Vector2(x, 0f));
        // The staircase climbs from its foot at x = -8 to its top at x = 0, where its road starts again.
        var stair = new RoadStairSpan
        {
            FromIndex = 0, ToIndex = 1, FromPoint = new Vector2(-8f, 0f), ToPoint = new Vector2(0f, 0f), FromHeight = top - 6f, ToHeight = top,
        };
        var plans = RoadNetworkGenerator.PlanRoadPathWithCrossings(path, new List<RoadCrossing>(), 4f, null, null, new List<RoadStairSpan> { stair });
        Assert.True(plans != null, RoadSpatialGrid.LastRefusal);
        RoadSpatialGrid.PlannedPath fromTop = plans!.Single(p => Vector2.Distance(p.Points[0], stair.ToPoint) < 0.5f);
        string said = string.Join(" ", fromTop.Points.Select((p, i) => $"({p.x:F1}):{fromTop.Heights[i]:F2}").Take(12));
        float pad = new RoadEndRing(stair.ToPoint, 90f, top).RoadsEnd + 2f * RoadEndRing.PadSpacing;
        for (int i = 0; i < fromTop.Points.Count; i++)
            if (Vector2.Distance(fromTop.Points[i], stair.ToPoint) <= pad)
                Assert.True(StairLayout.Walkable(fromTop.Heights[i], top), $"the road {fromTop.Heights[i] - top:+0.00;-0.00} m off the pad at x = {fromTop.Points[i].x:F1}: {said}");
        Assert.True(fromTop.Landed.start, said);

        // Refused by the grade owner (here its rests leave no room either), never kept at its own grade off the pad.
        fall = 0.33f;
        Assert.Null(RoadNetworkGenerator.PlanRoadPathWithCrossings(path, new List<RoadCrossing>(), 4f, null, null, new List<RoadStairSpan> { stair }));
        Assert.StartsWith("grade:", RoadSpatialGrid.LastRefusal);
    }

    /// <summary>A road held at both ends climbing within its cap, too short for the foot's level landing: 14 m at 27.9 % into a
    /// staircase's foot (3.9 m up), whose last 6 m level would leave 8 m at 45 %. It is refused: a road never runs its own
    /// grade into a foot (BRIEF-RULES 17; it was once kept so, at its own grade within the cap).</summary>
    [Fact]
    public void ARoadTooShortForTheFootsLandingIsRefused()
    {
        using var overrides = StaticOverride.Keep(() => RoadPitches.Enabled).AndKeep(() => RoadSpatialGrid.FollowGround)
            .AndKeep(() => RoadPitches.JunctionLanding);
        RoadPitches.Enabled = true;
        RoadSpatialGrid.FollowGround = true;
        RoadPitches.JunctionLanding = (float)PlainRoads.ModDefaults["junction landing"];
        float length = 14f, rise = 3.9f;
        Assert.True(rise / length <= RoadGrade.Configured);
        Assert.True((rise - RoadPitches.JunctionLanding * RoadPitches.JunctionLandingGrade) / (length - RoadPitches.JunctionLanding) > RoadGrade.Configured);
        _world.Height = (x, _) => x <= length ? 31.6f + rise / length * x : 31.6f + rise + 0.75f * (x - length);
        List<RoadSpatialGrid.PlannedPath>? Plan(float to)
        {
            var path = new List<Vector2>();
            for (float x = 0f; x <= to + 1e-3f; x += 2f) path.Add(new Vector2(x, 0f));
            path.Add(new Vector2(to + 8f, 0f));
            var stair = new RoadStairSpan
            {
                FromIndex = path.Count - 2, ToIndex = path.Count - 1, FromPoint = new Vector2(to, 0f), ToPoint = new Vector2(to + 8f, 0f),
                FromHeight = _world.Height(to, 0f), ToHeight = _world.Height(to, 0f) + 6f,
            };
            return RoadNetworkGenerator.PlanRoadPathWithCrossings(path, new List<RoadCrossing>(), 4f, null, null, new List<RoadStairSpan> { stair });
        }
        Assert.Null(Plan(length));
        Assert.Equal(RoadSpatialGrid.NoFootLandingRefusal, RoadSpatialGrid.LastRefusal);

        // Twice the cap all the way: refused, as before.
        float steep = 2f * RoadGrade.Configured;
        _world.Height = (x, _) => x <= length ? 31.6f + steep * x : 31.6f + steep * length + 0.75f * (x - length);
        Assert.Null(Plan(length));
    }

    /// <summary>One landing acceptance, early and late (RoadPitches.AcceptLanding, decision 6i): the road above, 14 m at 27.9 %
    /// into a staircase's foot (3.9 m up), has no room for the foot's landing within the cap. Planned with the staircase
    /// (RoadSpatialGrid.StairStarts) it is refused; the same road planned with no staircase (the cap on every edge) and
    /// levelled once a staircase is laid from its end (RoadPitches.LandEnd) is refused there the same, left exactly as it was
    /// accepted: the staircase is not laid from that end. Never kept at its own grade into the foot (BRIEF-RULES 17).</summary>
    [Fact]
    public void AFootsLandingLaidOnceTheRoadIsPlannedKeepsTheScheduleTheRoadWasAcceptedOn()
    {
        using var overrides = StaticOverride.Keep(() => RoadPitches.Enabled).AndKeep(() => RoadSpatialGrid.FollowGround)
            .AndKeep(() => RoadPitches.JunctionLanding);
        RoadPitches.Enabled = true;
        RoadSpatialGrid.FollowGround = true;
        RoadPitches.JunctionLanding = (float)PlainRoads.ModDefaults["junction landing"];
        float length = 14f, rise = 3.9f;
        _world.Height = (x, _) => x <= length ? 31.6f + rise / length * x : 31.6f + rise + 0.75f * (x - length);
        var path = new List<Vector2>();
        for (float x = 0f; x <= length + 1e-3f; x += 2f) path.Add(new Vector2(x, 0f));
        var withStair = new List<Vector2>(path) { new Vector2(length + 8f, 0f) };
        var stair = new RoadStairSpan
        {
            FromIndex = withStair.Count - 2, ToIndex = withStair.Count - 1, FromPoint = new Vector2(length, 0f), ToPoint = new Vector2(length + 8f, 0f),
            FromHeight = _world.Height(length, 0f), ToHeight = _world.Height(length, 0f) + 6f,
        };
        var early = RoadNetworkGenerator.PlanRoadPathWithCrossings(withStair, new List<RoadCrossing>(), 4f, null, null, new List<RoadStairSpan> { stair });
        Assert.Null(early);
        Assert.Equal(RoadSpatialGrid.NoFootLandingRefusal, RoadSpatialGrid.LastRefusal);
        var plain = RoadNetworkGenerator.PlanRoadPathWithCrossings(path, new List<RoadCrossing>(), 4f, null, null, new List<RoadStairSpan>());
        Assert.True(plain != null, RoadSpatialGrid.LastRefusal);
        RoadSpatialGrid.PlannedPath late = plain![plain.Count - 1];
        var landed = new List<float>(late.Heights);
        bool lateOk = RoadPitches.LandEnd(late.Points, landed, atStart: false, RoadGrade.Configured, out _, out _, late.Grades, late.Held, late.Landed);
        Assert.Null(late.Grades);
        Assert.False(lateOk);
        Assert.Equal(late.Heights, landed);
    }

    /// <summary>One landing acceptance, early and late, over held points (RoadPitches.LandEnd, decision 6i): a road whose first
    /// 7 m are held to a road climbing at 30 % (consecutive held points, 1 m apart) cannot land a staircase's foot there.
    /// Planned with the landing, the held stretch is judged against the landing's limit (RoadSpatialGrid.WorstOverLimit) and
    /// the road refused; landed late from that end, the edges between two held points are judged the same, and the end does
    /// not land, the road left as it was. Before, the late acceptance limited only stretches of more than one edge between
    /// held points, and reported the end landed on a 30 % slope.</summary>
    [Fact]
    public void ALandingOverConsecutiveHeldPointsIsJudgedEarlyAndLateAlike()
    {
        using var overrides = StaticOverride.Keep(() => RoadPitches.JunctionLanding);
        RoadPitches.JunctionLanding = (float)PlainRoads.ModDefaults["junction landing"];
        var points = new List<Vector2>(); var heights = new List<float>(); var held = new List<bool>();
        for (int i = 0; i <= 20; i++)
        {
            points.Add(new Vector2(i, 0f));
            heights.Add(60f + 0.3f * Mathf.Min(i, 7));
            held.Add(i <= 7);
        }
        // Early: landed as planned, held back to the road it meets, then judged.
        var early = new List<float>(heights);
        Assert.Equal(RoadPitches.Landing.Landed, RoadPitches.AcceptLanding(points, early, (true, false), (false, false), null,
            RoadGrade.Configured, null, null, out float[]? grades, out _));
        for (int i = 0; i <= 7; i++) early[i] = heights[i];
        Assert.True(RoadSpatialGrid.WorstOverLimit(points, early, grades, RoadGrade.Configured, 1f) >= 0);
        // Late: the same verdict, and nothing moves.
        var late = new List<float>(heights);
        Assert.False(RoadPitches.LandEnd(points, late, atStart: true, RoadGrade.Configured, out _, out _, null, held));
        Assert.Equal(heights, late);
    }

    /// <summary>A late landing keeps the schedule the road was accepted on (RoadPitches.LandEnd on PlannedPath.Grades): a road
    /// planned with no staircase up a 140 m climb at 25 % over ground rolling 3 m either way (its pitches and eases placed),
    /// then 20 m on the level to its end, levelled for a staircase laid from that end once it is planned (as
    /// RoadNetworkGenerator.LocationApproaches and PlanEntryFrom do), ends on the landing with
    /// its climb as it was accepted; levelled again with what the plan then carries, nothing moves. Before, the late
    /// landing placed the pitches again on the limited profile and limited the whole road anew: here the climb moved 0.95 m
    /// 73 m before the end, and a second landing moved it another 1.07 m (in a world, a road re-profiled by up to 2.8 m over
    /// 130 m before its foot, and the road held to it next was refused).</summary>
    [Fact]
    public void ALateLandingKeepsTheRestOfTheRoadAsItWasAccepted()
    {
        using var overrides = StaticOverride.Keep(() => RoadPitches.Enabled).AndKeep(() => RoadSpatialGrid.FollowGround)
            .AndKeep(() => RoadPitches.JunctionLanding);
        RoadPitches.Enabled = true;
        RoadSpatialGrid.FollowGround = true;
        RoadPitches.JunctionLanding = (float)PlainRoads.ModDefaults["junction landing"];
        _world.Height = (x, _) => (x <= 140f ? 30f + 0.25f * x : 65f) + 3f * Mathf.Sin(x * 0.13f);
        var path = new List<Vector2>();
        for (float x = 0f; x <= 160f + 1e-3f; x += 4f) path.Add(new Vector2(x, 0f));
        var plans = RoadNetworkGenerator.PlanRoadPathWithCrossings(path, new List<RoadCrossing>(), 4f, null, null, new List<RoadStairSpan>());
        Assert.True(plans != null, RoadSpatialGrid.LastRefusal);
        Assert.Single(plans!);
        RoadSpatialGrid.PlannedPath road = plans![0];
        Assert.True(road.Grades != null && road.Grades.Exists(g => g <= RoadPitches.EaseGrade + 1e-4f), "the climb was planned with no ease");
        Assert.True(road.Grades!.TrueForAll(g => g <= RoadGrade.Configured + 1e-6f), "an edge was allowed past the cap");
        Assert.True(SteepestNearEnd(road, RoadPitches.JunctionLanding) > RoadPitches.JunctionLandingGrade + 1e-3f, "planned already level into its end");
        var accepted = new List<float>(road.Heights);

        var landed = new List<float>(road.Heights);
        Assert.True(RoadPitches.LandEnd(road.Points, landed, atStart: false, RoadGrade.Configured, out var grades, out var ends, road.Grades,
            road.Held, road.Landed));
        for (int i = 0; i < road.Heights.Count; i++) road.Heights[i] = landed[i];
        (road.Grades, road.Landed) = (grades, ends);
        Assert.True(SteepestNearEnd(road, RoadPitches.JunctionLanding) <= RoadPitches.JunctionLandingGrade + 1e-3f,
            $"into the foot at {SteepestNearEnd(road, RoadPitches.JunctionLanding):P1}");
        Assert.True(road.Landed.end);
        for (int i = 0; i < road.Points.Count && road.Points[i].x <= 120f; i++)
            Assert.True(Mathf.Abs(accepted[i] - landed[i]) < 1e-4f, $"the climb moved at x = {road.Points[i].x:F1}: {accepted[i]:F3} -> {landed[i]:F3}");

        // Landed again on what the plan now carries: nothing moves (but the limiter's own rounding).
        var again = new List<float>(road.Heights);
        Assert.True(RoadPitches.LandEnd(road.Points, again, atStart: false, RoadGrade.Configured, out _, out _, road.Grades, road.Held, road.Landed));
        for (int i = 0; i < again.Count; i++) Assert.True(Mathf.Abs(again[i] - road.Heights[i]) < 1e-4f, $"moved again at x = {road.Points[i].x:F1}");
    }

    /// <summary>The one rule for a road whose landing at a staircase's foot does not fit (RoadPitches.LeavesItsLanding): kept only
    /// where no edge within a landing's length of that end is steeper than the cap. A pitch there is refused; one further back,
    /// or at the other end, is not.</summary>
    [Fact]
    public void ARoadLeavesAFootsLandingOnlyWithinALandingsLengthOfIt()
    {
        using var overrides = StaticOverride.Keep(() => RoadPitches.JunctionLanding);
        RoadPitches.JunctionLanding = (float)PlainRoads.ModDefaults["junction landing"];
        var points = Enumerable.Range(0, 21).Select(i => new Vector2(i, 0f)).ToList();
        float cap = RoadGrade.Configured, pitch = 1.5f * RoadGrade.Configured;
        Assert.True(pitch > cap);
        List<float> Climb(int pitchedFrom, int pitchedTo)
        {
            var h = new List<float> { 30f };
            for (int i = 1; i < points.Count; i++) h.Add(h[i - 1] + (i > pitchedFrom && i <= pitchedTo ? pitch : cap));
            return h;
        }
        Assert.False(RoadPitches.LeavesItsLanding(points, Climb(0, 0), atStart: false, atEnd: true, cap));
        int landing = Mathf.CeilToInt(RoadPitches.JunctionLanding);
        Assert.True(RoadPitches.LeavesItsLanding(points, Climb(20 - landing + 1, 20), atStart: false, atEnd: true, cap));
        Assert.False(RoadPitches.LeavesItsLanding(points, Climb(2, 20 - landing), atStart: false, atEnd: true, cap));
        Assert.True(RoadPitches.LeavesItsLanding(points, Climb(2, 20 - landing + 1), atStart: false, atEnd: true, cap));
        Assert.False(RoadPitches.LeavesItsLanding(points, Climb(20 - landing + 1, 20), atStart: true, atEnd: false, cap));
        // Round a foot mid-road (a landing laid round a foot on a standing flight, RoadStairRuns.Fill): the same rule either
        // side of it, and the ends' verdict is this one at the ends.
        Assert.True(RoadPitches.LeavesItsLandingAround(points, Climb(10, 11), 10f + landing - 0.5f, cap));
        Assert.False(RoadPitches.LeavesItsLandingAround(points, Climb(10, 11), 11f + landing, cap));
        Assert.True(RoadPitches.LeavesItsLandingAround(points, Climb(20 - landing + 1, 20), 20f, cap));
        Assert.False(RoadPitches.LeavesItsLandingAround(points, Climb(20 - landing + 1, 20), 0f, cap));
    }

    /// <summary>A road climbing a U (east 40 m, north 12 m, back west 14 m) to a staircase's foot at its end: its last leg is
    /// shorter than a tight switchback's taper and the foot's landing (TightTaper + JunctionLanding), so the turn is the
    /// ordinary one and the road meets the foot at its full width all along the landing (before, the tight turn's leg was
    /// still narrowing 2 m from the foot).</summary>
    [Fact]
    public void NoTightSwitchbackNarrowsTheRoadIntoAStaircasesFoot()
    {
        _world.Height = (x, z) => z < 6f ? 60f + 0.2f * x : 76f - 0.2f * x;
        using var overrides = StaticOverride.Keep(() => RoadSwitchbacks.TightSwitchbacks).AndKeep(() => RoadSwitchbacks.StairTurns)
            .AndKeep(() => RoadPitches.JunctionLanding);
        RoadSwitchbacks.TightSwitchbacks = true;
        RoadSwitchbacks.StairTurns = true;
        RoadPitches.JunctionLanding = (float)PlainRoads.ModDefaults["junction landing"];
        var path = new List<Vector2> { new(0f, 0f), new(40f, 0f), new(40f, 12f), new(26f, 12f), new(18f, 12f) };
        float foot = _world.Height(26f, 12f);
        var stair = new RoadStairSpan
        {
            FromIndex = 3, ToIndex = 4, FromPoint = new Vector2(26f, 12f), ToPoint = new Vector2(18f, 12f), FromHeight = foot, ToHeight = foot + 6f,
        };
        var plans = RoadNetworkGenerator.PlanRoadPathWithCrossings(path, new List<RoadCrossing>(), 4f, null, null, new List<RoadStairSpan> { stair });
        Assert.True(plans != null, RoadSpatialGrid.LastRefusal);
        RoadSpatialGrid.PlannedPath toFoot = plans![0];
        Assert.True(Vector2.Distance(toFoot.Points[toFoot.Points.Count - 1], stair.FromPoint) < 0.5f);
        if (toFoot.Widths is { } widths)
        {
            float along = 0f;
            for (int i = toFoot.Points.Count - 1; i > 0 && along <= RoadPitches.JunctionLanding; i--)
            {
                Assert.True(widths[i] >= 4f - 1e-3f, $"{widths[i]:F2} m wide {along:F1} m from the foot");
                along += Vector2.Distance(toFoot.Points[i - 1], toFoot.Points[i]);
            }
        }
        // Shaped (RoadSwitchbacks.Shape): the U's last leg ending where a staircase starts takes the ordinary turn; the same
        // U ending on open road, and one whose last leg leaves the taper and the landing room, keep the tight one.
        var u = new List<Vector2> { new(0f, 0f), new(40f, 0f), new(40f, 8f), new(26f, 8f) };
        static float North(Vector2 v) => 60f + 0.3f * v.y;
        Assert.True(RoadSwitchbacks.Shape(u, 4, out _, out _, North, stair: true, bend: 0f, tight: true, stairStarts: (false, true)));
        Assert.Null(RoadSwitchbacks.LastWidths);
        Assert.True(RoadSwitchbacks.Shape(u, 4, out _, out _, North, stair: true, bend: 0f, tight: true));
        Assert.Equal(RoadSwitchbacks.TightWidth, RoadSwitchbacks.LastWidths!.Min(), 2);
        float room = RoadSwitchbacks.TightTaper + RoadPitches.JunctionLanding;
        var longer = new List<Vector2> { new(0f, 0f), new(40f, 0f), new(40f, 8f), new(40f - room - 1f, 8f) };
        Assert.True(RoadSwitchbacks.Shape(longer, 4, out _, out _, North, stair: true, bend: 0f, tight: true, stairStarts: (false, true)));
        Assert.Equal(RoadSwitchbacks.TightWidth, RoadSwitchbacks.LastWidths!.Min(), 2);
    }

    /// <summary>An approach laid from a road's end once the road is planned (RoadNetworkGenerator.LocationApproaches): the road's
    /// last 8 m at 50 % into the end (over the cap) are limited again with both ends held, the last 6 m level and the climb
    /// taken before them, every edge within the cap. Where the climb has no room left the road is
    /// left as it was; the approach is laid only where the road keeps within the cap into the end.</summary>
    [Fact]
    public void AnApproachsRoadEndIsLevelledWhereItsClimbHasRoom()
    {
        using var overrides = StaticOverride.Keep(() => RoadPitches.JunctionLanding);
        RoadPitches.JunctionLanding = (float)PlainRoads.ModDefaults["junction landing"];
        var points = Enumerable.Range(0, 21).Select(i => new Vector2(i, 0f)).ToList();
        var heights = points.Select(p => p.x <= 12f ? 30f : 30f + 0.5f * (p.x - 12f)).ToList();
        Assert.True(RoadPitches.LandEnd(points, heights, atStart: false, RoadGrade.Configured, out _, out _));
        Assert.Equal(30f, heights[0], 3);
        Assert.Equal(34f, heights[20], 3);
        for (int i = 15; i <= 20; i++)
            Assert.True(Mathf.Abs(heights[i] - heights[i - 1]) <= RoadPitches.JunctionLandingGrade + 1e-3f, $"edge {i}: {heights[i] - heights[i - 1]:F3}");
        for (int i = 1; i <= 20; i++)
            Assert.True(Mathf.Abs(heights[i] - heights[i - 1]) <= RoadGrade.Configured + 1e-3f, $"edge {i}: {heights[i] - heights[i - 1]:F3}");

        // 10 m up over 20 m at 50 % all the way: with 6 m level, 14 m at the cap make 4.9 m. Not levelled.
        var steep = points.Select(p => 30f + 0.5f * p.x).ToList();
        var was = new List<float>(steep);
        Assert.False(RoadPitches.LandEnd(points, steep, atStart: false, RoadGrade.Configured, out _, out _));
        Assert.Equal(was, steep);

        // At the cap all the way: no room for the landing either. Not levelled, left as it was, and no approach is laid from
        // that end (a road never runs its own grade into a foot: BRIEF-RULES 17).
        var atCap = points.Select(p => 30f + RoadGrade.Configured * p.x).ToList();
        var before = new List<float>(atCap);
        Assert.False(RoadPitches.LandEnd(points, atCap, atStart: false, RoadGrade.Configured, out _, out _));
        Assert.Equal(before, atCap);
    }
}
