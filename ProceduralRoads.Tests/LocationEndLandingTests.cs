using System;
using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A road end held at a location's ground ends on the same level landing as a road that joins another
/// (RoadPitches.ApplyJunctionLanding: its last JunctionLanding metres at JunctionLandingGrade), and the pitches treat
/// the landing as a rest. Before, only a join had one: an end held at the location's ground below ground steeper than the
/// cap was clamped onto one straight climb at the cap from the end, meeting the location at an angle. Where the landing
/// leaves the climb short of room, the road is refused rather than kept over the cap.
/// </summary>
[Collection("RoadStatics")]
public class LocationEndLandingTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    /// <summary>Ground along +x from a location's end at x = 0 (as read in game: the end held at 38.83, the ground about a
    /// metre over it, then steeper than the cap to a crest 11.5 m up).</summary>
    private sealed class Slope : WorldGenerator
    {
        public Func<float, float> Ground = x => 40f;
        public override float GetHeight(float wx, float wy) => Ground(wx);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
        public override int GetSeed() => 83;
    }

    private readonly Slope _world = new();

    /// <summary>The ground read in game along the road from its end (metres along, height), linear between, to the
    /// crest; level past it (in game it fell away into a hollow, which the smoothing here would lower the crest into).</summary>
    private static readonly (float along, float height)[] Live =
    {
        (0f, 39.81f), (2f, 39.78f), (4f, 39.94f), (10f, 40.94f), (18f, 43.88f), (24f, 47.25f), (30f, 50.41f), (36f, 50.4f),
    };

    private static float LiveGround(float x)
    {
        if (x <= Live[0].along) return Live[0].height;
        for (int i = 1; i < Live.Length; i++)
            if (x <= Live[i].along) return Mathf.Lerp(Live[i - 1].height, Live[i].height, (x - Live[i - 1].along) / (Live[i].along - Live[i - 1].along));
        return Live[Live.Length - 1].height;
    }

    public LocationEndLandingTests()
    {
        _scope.WithWorld(_world);
        RoadNetworkGenerator.Reset();
    }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    private static List<Vector2> Line(float length)
    {
        var path = new List<Vector2>();
        for (float x = 0f; x <= length + 1e-3f; x += 4f) path.Add(new Vector2(x, 0f));
        return path;
    }

    private static float GradeOver(RoadSpatialGrid.PlannedPath plan, float from, float to)
    {
        float hFrom = 0f, hTo = 0f;
        float bestFrom = float.MaxValue, bestTo = float.MaxValue;
        for (int i = 0; i < plan.Points.Count; i++)
        {
            float x = plan.Points[i].x;
            if (Mathf.Abs(x - from) < bestFrom) { bestFrom = Mathf.Abs(x - from); hFrom = plan.Heights[i]; }
            if (Mathf.Abs(x - to) < bestTo) { bestTo = Mathf.Abs(x - to); hTo = plan.Heights[i]; }
        }
        return (hTo - hFrom) / (to - from);
    }

    private static float Steepest(RoadSpatialGrid.PlannedPath plan, float from, float to)
    {
        float steepest = 0f;
        for (int i = 1; i < plan.Points.Count; i++)
        {
            if (plan.Points[i - 1].x < from - 1e-3f || plan.Points[i].x > to + 1e-3f) continue;
            float run = Vector2.Distance(plan.Points[i - 1], plan.Points[i]);
            if (run < 1e-3f) continue;
            steepest = Mathf.Max(steepest, Mathf.Abs(plan.Heights[i] - plan.Heights[i - 1]) / run);
        }
        return steepest;
    }

    /// <summary>The live numbers: the end held at 38.83 at x = 0, the ground 39.8 there, then rising at 44 % (over the
    /// 35 % cap) to 50.4 at 30 m and level past it; 11.5 m up over the first 36 m. Without the landing the profile is one
    /// climb at the cap from the end; with it the last 6 m into the location are at or under 5 %, and the road is still built
    /// within the cap on every edge.</summary>
    [Fact]
    public void ALocationEndHeldAtItsGroundEndsOnALevelLandingWithinTheCap()
    {
        _world.Ground = LiveGround;
        using var overrides = StaticOverride.Keep(() => RoadPitches.Enabled).AndKeep(() => RoadPitches.JunctionLanding)
            .AndKeep(() => RoadSpatialGrid.FollowGround);
        RoadPitches.Enabled = true;
        RoadSpatialGrid.FollowGround = true;
        const float held = 38.83f;
        float landing = (float)PlainRoads.ModDefaults["junction landing"];

        // As it was: no landing anywhere, the end clamped onto the cap.
        RoadPitches.JunctionLanding = 0f;
        var before = RoadNetworkGenerator.PlanRoadPathWithCrossings(Line(64f), new List<RoadCrossing>(), 4f, held, null);
        Assert.NotNull(before);
        var b = before![0];
        Assert.Equal(held, b.Heights[0], 2);
        Assert.True(GradeOver(b, 0f, landing) > 0.15f, $"the end already level: {GradeOver(b, 0f, landing):P1}");
        Assert.InRange(GradeOver(b, 0f, 36f), 0.25f, RoadGrade.Configured + 0.005f);

        // With the landing (the mod's own length).
        RoadPitches.JunctionLanding = landing;
        var after = RoadNetworkGenerator.PlanRoadPathWithCrossings(Line(64f), new List<RoadCrossing>(), 4f, held, null);
        Assert.NotNull(after);
        var a = after![0];
        Assert.Equal(held, a.Heights[0], 2);
        Assert.True(a.LocationLandingStart, "the location end was not marked as landed");
        Assert.False(a.LocationLandingEnd, "the free end got a landing");
        Assert.True(Steepest(a, 0f, landing) <= RoadPitches.JunctionLandingGrade + 1e-3f, $"landing at {Steepest(a, 0f, landing):P1}");
        // The climb beside it and the rest of the road within the cap; the crest still met.
        Assert.True(Steepest(a, 0f, 64f) <= RoadGrade.Configured + 1e-3f, $"steepest {Steepest(a, 0f, 64f):P1}");
        Assert.True(Steepest(a, 14f, 64f) <= RoadGrade.Configured + 1e-3f, $"past the pitch {Steepest(a, 14f, 64f):P1}");
        Assert.InRange(a.Heights[a.Heights.Count - 1], 50.0f, 50.8f);
    }

    /// <summary>A location end whose landing leaves the climb too little room: 6.8 m up over 20 m between two held ends fits
    /// at the 35 % cap (7.0 m), but not after a 6 m landing at 5 % (5.2 m). The road is refused
    /// (RoadGrade.Limit, "ends too far apart"), never kept with its end over spec.</summary>
    [Fact]
    public void ALocationEndWhoseLandingLeavesTooLittleRoomIsRefused()
    {
        _world.Ground = x => 40f + x * 0.34f;
        using var overrides = StaticOverride.Keep(() => RoadPitches.Enabled).AndKeep(() => RoadPitches.JunctionLanding);
        RoadPitches.Enabled = true;

        RoadPitches.JunctionLanding = 0f;
        Assert.NotNull(RoadNetworkGenerator.PlanRoadPathWithCrossings(Line(20f), new List<RoadCrossing>(), 4f, 40f, 46.8f));

        RoadPitches.JunctionLanding = (float)PlainRoads.ModDefaults["junction landing"];
        Assert.Null(RoadNetworkGenerator.PlanRoadPathWithCrossings(Line(20f), new List<RoadCrossing>(), 4f, 40f, 46.8f));
        Assert.StartsWith("grade:", RoadSpatialGrid.LastRefusal);
    }
}
