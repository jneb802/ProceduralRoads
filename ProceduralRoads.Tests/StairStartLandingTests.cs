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

    /// <summary>A road climbing at 25 % along +x to a staircase's foot at x = 32 (the ground there 70 m), the staircase up from
    /// it to x = 40 the road's end. Held at the foot's height, its last 6 m into the foot are a level landing; before, they
    /// climbed with the ground.</summary>
    [Fact]
    public void ARoadHeldAtAStaircasesFootEndsOnALevelLanding()
    {
        _world.Height = (x, _) => x <= 32f ? 70f - 0.25f * (32f - x) : 70f + 0.75f * (x - 32f);
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
        Assert.True(plans != null, RoadSpatialGrid.LastRefusal);
        RoadSpatialGrid.PlannedPath toFoot = plans![0];
        Assert.True(Vector2.Distance(toFoot.Points[toFoot.Points.Count - 1], stair.FromPoint) < 0.5f);
        Assert.True(Mathf.Abs(toFoot.Heights[toFoot.Heights.Count - 1] - 70f) < 0.01f,
            string.Join(" ", toFoot.Points.Select((p, i) => $"({p.x:F1},{p.y:F1}):{toFoot.Heights[i]:F2}")));
        Assert.True(SteepestNearEnd(toFoot, RoadPitches.JunctionLanding) <= RoadPitches.JunctionLandingGrade + 1e-3f,
            $"into the foot at {SteepestNearEnd(toFoot, RoadPitches.JunctionLanding):P1}");
        // Not a place's landing: the count of those is the places'.
        Assert.False(toFoot.PlaceLandingStart || toFoot.PlaceLandingEnd);
    }

    /// <summary>A road held at both ends climbing within its cap, too short for the foot's level landing: 14 m at 27.9 % into a
    /// staircase's foot (3.9 m up), whose last 6 m level would leave 8 m at 45 %. It is planned as before the landing, its own
    /// grade within the cap all the way to the foot (the end audit's rule, StairEnds.RoadGradeOff), not refused (refused, a
    /// Mistlands town's road lost its place). A road the cap cannot carry to the foot at all is still refused.</summary>
    [Fact]
    public void ARoadTooShortForTheFootsLandingKeepsItsGradeWithinTheCap()
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
        var plans = Plan(length);
        Assert.True(plans != null, RoadSpatialGrid.LastRefusal);
        RoadSpatialGrid.PlannedPath toFoot = plans![0];
        Assert.True(Mathf.Abs(toFoot.Heights[toFoot.Heights.Count - 1] - (31.6f + rise)) < 0.01f);
        Assert.True(SteepestNearEnd(toFoot, RoadPitches.JunctionLanding) <= RoadGrade.Configured + 1e-3f,
            $"into the foot at {SteepestNearEnd(toFoot, RoadPitches.JunctionLanding):P1}");

        // Past a pitch's grade all the way: refused, as before.
        float steep = RoadPitches.PitchGrade + (RoadPitches.PitchGrade - RoadGrade.Configured);
        _world.Height = (x, _) => x <= length ? 31.6f + steep * x : 31.6f + steep * length + 0.75f * (x - length);
        Assert.Null(Plan(length));
    }

    /// <summary>The one rule for a road whose landing at a staircase's foot does not fit (RoadPitches.PitchedInto): kept only
    /// where no edge within a landing's length of that end is steeper than the cap. A pitch there is refused; one further back,
    /// or at the other end, is not.</summary>
    [Fact]
    public void ARoadIsPitchedIntoAFootOnlyWithinALandingsLengthOfIt()
    {
        using var overrides = StaticOverride.Keep(() => RoadPitches.JunctionLanding);
        RoadPitches.JunctionLanding = (float)PlainRoads.ModDefaults["junction landing"];
        var points = Enumerable.Range(0, 21).Select(i => new Vector2(i, 0f)).ToList();
        float cap = RoadGrade.Configured, pitch = RoadPitches.PitchGrade;
        Assert.True(pitch > cap);
        List<float> Climb(int pitchedFrom, int pitchedTo)
        {
            var h = new List<float> { 30f };
            for (int i = 1; i < points.Count; i++) h.Add(h[i - 1] + (i > pitchedFrom && i <= pitchedTo ? pitch : cap));
            return h;
        }
        Assert.False(RoadPitches.PitchedInto(points, Climb(0, 0), atStart: false, atEnd: true, cap));
        int landing = Mathf.CeilToInt(RoadPitches.JunctionLanding);
        Assert.True(RoadPitches.PitchedInto(points, Climb(20 - landing + 1, 20), atStart: false, atEnd: true, cap));
        Assert.False(RoadPitches.PitchedInto(points, Climb(2, 20 - landing), atStart: false, atEnd: true, cap));
        Assert.True(RoadPitches.PitchedInto(points, Climb(2, 20 - landing + 1), atStart: false, atEnd: true, cap));
        Assert.False(RoadPitches.PitchedInto(points, Climb(20 - landing + 1, 20), atStart: true, atEnd: false, cap));
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

    /// <summary>An approach laid from a road's end once the road is planned (RoadNetworkGenerator.SiteApproaches): the road's
    /// last 8 m at a pitch's 50 % into the end are limited again with both ends held, the last 6 m level and the climb taken
    /// before them (pitch edges kept to their own grade, the rest to the cap). Where the climb has no room left the road is
    /// left as it was; the approach is laid only where the road keeps within the cap into the end.</summary>
    [Fact]
    public void AnApproachsRoadEndIsLevelledWhereItsClimbHasRoom()
    {
        using var overrides = StaticOverride.Keep(() => RoadPitches.JunctionLanding);
        RoadPitches.JunctionLanding = (float)PlainRoads.ModDefaults["junction landing"];
        var points = Enumerable.Range(0, 21).Select(i => new Vector2(i, 0f)).ToList();
        var heights = points.Select(p => p.x <= 12f ? 30f : 30f + 0.5f * (p.x - 12f)).ToList();
        Assert.True(RoadPitches.LandEnd(points, heights, atStart: false, RoadGrade.Configured));
        Assert.Equal(30f, heights[0], 3);
        Assert.Equal(34f, heights[20], 3);
        for (int i = 15; i <= 20; i++)
            Assert.True(Mathf.Abs(heights[i] - heights[i - 1]) <= RoadPitches.JunctionLandingGrade + 1e-3f, $"edge {i}: {heights[i] - heights[i - 1]:F3}");
        for (int i = 1; i <= 20; i++)
            Assert.True(Mathf.Abs(heights[i] - heights[i - 1]) <= RoadPitches.PitchGrade + 1e-3f, $"edge {i}: {heights[i] - heights[i - 1]:F3}");

        // 10 m up over 20 m at a pitch's grade all the way: with 6 m level, 14 m at 50 % make 7 m. Not levelled.
        var steep = points.Select(p => 30f + 0.5f * p.x).ToList();
        var was = new List<float>(steep);
        Assert.False(RoadPitches.LandEnd(points, steep, atStart: false, RoadGrade.Configured));
        Assert.Equal(was, steep);

        // At the cap all the way: no room for the landing either, but within the cap into the end (RoadPitches.PitchedInto),
        // so left as it was and the approach laid, as a road planned to a staircase's foot is.
        var atCap = points.Select(p => 30f + RoadGrade.Configured * p.x).ToList();
        var before = new List<float>(atCap);
        Assert.True(RoadPitches.LandEnd(points, atCap, atStart: false, RoadGrade.Configured));
        Assert.Equal(before, atCap);
    }
}
