using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A second road at a Location the network already serves whose stub drops onto the serving road (RoadNetworkGenerator.DropStubs,
/// asked by RoadNetworkGenerator.PlanCandidate; the way in it may not join, LocationWaysIn.OnAWayIn). The project owner's
/// decisions (18 Nov 2026):
/// - "Drop, don't replace. A duplicate is just dropped: it is not built and nothing replaces it. The Location is already
///   reached, so nothing is lost."
/// - "Far side: a far-side road keeps its own way in, unless it would join the same serving road straight away; then it is a
///   duplicate and is dropped." Straight away is the stub's reach (RoadNetworkGenerator.StubLength, TrunkReach).
/// - "Start: the island Start (spawn and edge point) follows the same rule" (RoadNetworkGenerator.WayEnd).
/// - 19 Nov 2026: "drop only the duplicate part": a planner road from a served Location X to a Location Y not reached, whose
///   X end would duplicate X's way in, is still built to Y, from where it meets X's serving road; only the second way in at
///   X is not built. The whole road is a duplicate only where its other end is reached too.
/// - "The way in means the serving road's last 40 m and the stretch 40 m from its staircase's foot. Both are measured ALONG the
///   road, as one definition (RoadStairRuns.LocationReach)."
/// - "Whether a road meets the way in is decided by the existing junction owner's test" (MergeWithin's RoadBeside.OnRoad).
/// </summary>
[Collection("RoadStatics")]
public sealed class LeavingReachedTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private readonly float _snap = RoadNetworkGenerator.RoadSnap, _corridor = RoadNetworkGenerator.CorridorSnap;

    /// <summary>High ground (50 m) south of z = 0, falling to 40 m by z = 24 and level north of that.</summary>
    private sealed class Shelf : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 50f - (z <= 0f ? 0f : z >= 24f ? 10f : z * (10f / 24f));
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override float GetBiomeHeight(Heightmap.Biome biome, float wx, float wy, out Color mask, bool preGeneration = false, bool riverPreDN = true)
        { mask = default; return GetHeight(wx, wy); }
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
        public override int GetSeed() => 11;
    }

    private readonly Shelf _world = new();

    public LeavingReachedTests()
    {
        _scope.WithWorld(_world);
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        LocationWaysIn.Reset();
        RoadNetworkGenerator.RoadSnap = 6f;
        RoadNetworkGenerator.CorridorSnap = 14f;
    }

    public void Dispose()
    {
        RoadNetworkGenerator.RoadSnap = _snap;
        RoadNetworkGenerator.CorridorSnap = _corridor;
        IslandStart(null);
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        LocationWaysIn.Reset();
        _scope.Dispose();
    }

    /// <summary>The island's start on this worker (RoadNetworkGenerator.t_islandStart, set by RouteIsland).</summary>
    private static void IslandStart(Vector2? start) => RoadNetworkGenerator.t_islandStart = start;

    /// <summary>The start: a Location of exterior <paramref name="radius"/> whose edge is at (0, -12), or (radius 0) the island's
    /// edge point there.</summary>
    private static Vector2 Start(float radius) => new(0f, -12f - radius);

    /// <summary>A road committed through <paramref name="points"/> on the ground, recorded at the start as its way in: as a road
    /// arriving at it (its location end last), or (<paramref name="foot"/>) as the road to a staircase's foot at its first point
    /// (LocationWaysIn.Way.Foot), as generation records a road it stores.</summary>
    private void Serving(float radius, List<Vector2> points, bool foot)
    {
        if (radius <= 0f) IslandStart(Start(radius));
        var heights = points.ConvertAll(p => _world.GetHeight(p.x, p.y));
        float length = 0f;
        for (int i = 1; i < points.Count; i++) length += Vector2.Distance(points[i - 1], points[i]);
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(points, heights, points.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, length, false));
        var toward = new List<Vector2>(points);
        var hs = new List<float>(heights);
        if (foot) { toward.Reverse(); hs.Reverse(); }
        LocationWaysIn.Record(Start(radius), LocationWaysIn.Of(toward, hs, Start(radius), locationEndIsLast: true, foot: foot)!);
    }

    /// <summary>The road already serving the start, arriving at it as road: west along z = 0 on the high ground from x = 150 to
    /// 20, its location end at x = 20; its last 40 m by the start, x 60 to 20, is its way in.</summary>
    private void ServingRoad(float radius)
    {
        var points = new List<Vector2>();
        for (float x = 150f; x >= 20f; x -= 2f) points.Add(new Vector2(x, 0f));
        Serving(radius, points, foot: false);
    }

    /// <summary>A second road from the start's edge at (0, -12), round onto the serving road at (<paramref name="joinX"/>, 0) and on
    /// north to (<paramref name="joinX"/>, <paramref name="north"/>).</summary>
    private static List<Vector2> RoundOntoIt(float north, float joinX = 30f)
    {
        var path = new List<Vector2>();
        for (float x = 0f; x < 15f; x += 3f) path.Add(new Vector2(x, -12f));
        for (int i = 0; i <= 5; i++) path.Add(Vector2.Lerp(new Vector2(15f, -12f), new Vector2(joinX, 0f), i / 5f));
        for (float z = 4f; z <= north; z += 4f) path.Add(new Vector2(joinX, z));
        return path;
    }

    /// <summary>The road planned from the start (served) to its last point, whose end is reached already
    /// (<paramref name="endReached"/>: a junction, or a Location the network serves) or not (a planner road's destination).</summary>
    private RoadNetworkGenerator.RoadPlan Plan(List<Vector2> path, float startRadius, bool endReached = false) =>
        RoadNetworkGenerator.PlanCandidate(new RoadPathfinder(_world), path, Start(startRadius), startRadius,
            path[path.Count - 1], 0f, false, false, startReached: true, endReached: endReached, 4f);

    /// <summary>The road's plans have a point on its stub round from the start (south of the serving road's line).</summary>
    private static bool WithItsStub(RoadNetworkGenerator.RoadPlan plan) =>
        plan.Plans != null && plan.Plans.SelectMany(p => p.Points).Any(p => p.y < -1f);

    /// <summary>The viaduct example: a road from the start, its other end reached too, that joins the serving road 34 m from it,
    /// within the stub's reach, on the serving road's way in (x 20 to 60): the whole road is a duplicate, dropped
    /// (PlanStop.Duplicate), from the Location or the edge point.</summary>
    [Theory]
    [InlineData(8f)]
    [InlineData(0f)]
    public void ARoadThatJoinsTheServingRoadOnItsWayInIsDropped(float startRadius)
    {
        ServingRoad(startRadius);
        var plan = Plan(RoundOntoIt(100f), startRadius, endReached: true);
        Assert.Equal(RoadNetworkGenerator.PlanStop.Duplicate, plan.Stop);
        Assert.Null(plan.Plans);
        Assert.Equal(RoadNetworkGenerator.DuplicateRefusal, RoadSpatialGrid.LastRefusal);
        Assert.Equal(Start(startRadius), plan.DuplicateOf!.Value.location);
    }

    /// <summary>The SunkenCrypt4 shape (Crypt2 -> SunkenCrypt4): a planner road from the served start to a Location not
    /// reached, its stub from the start dropped onto the serving road's way in. Only that stub, the second way in, is a
    /// duplicate: the road to the Location not reached is still built, from where it meets the serving road. (It was dropped
    /// whole, the Location left unconnected.)</summary>
    [Theory]
    [InlineData(8f)]
    [InlineData(0f)]
    public void ARoadToALocationNotReachedIsBuiltFromTheServingRoadNotDroppedAsADuplicate(float startRadius)
    {
        ServingRoad(startRadius);
        var plan = Plan(RoundOntoIt(100f), startRadius, endReached: false);
        Assert.True(plan.Stop == RoadNetworkGenerator.PlanStop.Planned && plan.Plans != null, $"not planned: {plan.Stop} {RoadSpatialGrid.LastRefusal}");
        Assert.False(WithItsStub(plan), "built with its stub round from the start: a second way in");
        Vector2 first = plan.Plans![0].Points[0];
        Assert.True(Mathf.Abs(first.y) <= RoadBeside.OnRoad && first.x >= 20f && first.x <= 60f, $"it starts at {first}, not where it meets the serving road");
        Assert.Contains(plan.Plans.SelectMany(p => p.Points), p => p.y >= 96f);
    }

    /// <summary>Joining the serving road within the stub's reach but past its way in (at x = 70, 71 m from the start), it starts at
    /// that junction: its stub round from the start is never planned.</summary>
    [Theory]
    [InlineData(8f)]
    [InlineData(0f)]
    public void ARoadJoiningTheServingRoadPastItsWayInStartsAtTheJunction(float startRadius)
    {
        ServingRoad(startRadius);
        var plan = Plan(RoundOntoIt(100f, joinX: 70f), startRadius);
        Assert.True(plan.Plans != null, $"not planned: {plan.Stop} {RoadSpatialGrid.LastRefusal}");
        Assert.DoesNotContain(plan.Plans!.SelectMany(p => p.Points), p => p.y < -1f);
    }

    /// <summary>A road to a Location not reached joining the serving road on its way in (at x = 30), its 8 m north of the
    /// junction too steep from the junction's height (its ends too far apart for the cap), though planned with its stub round
    /// from the start: refused, never built with that stub, a second way in beside the serving road. (Refused from the junction,
    /// a road was planned once more with its stub: here that built it.)</summary>
    [Theory]
    [InlineData(8f)]
    [InlineData(0f)]
    public void ARoadRefusedFromTheJunctionIsNeverBuiltWithItsStubFromTheLocation(float startRadius)
    {
        ServingRoad(startRadius);
        var plan = Plan(RoundOntoIt(8f, joinX: 30f), startRadius);
        Assert.False(WithItsStub(plan), $"{plan.Stop} {RoadSpatialGrid.LastRefusal}: built with its stub round from the start onto the road already serving it");
        Assert.Null(plan.Plans);
    }

    /// <summary>The same road, its stub dropped onto a road that is no way into the start (a road passing by, not recorded as
    /// one): refused from that junction, it is planned with its stub, the start's own way in from that side.</summary>
    [Theory]
    [InlineData(8f)]
    [InlineData(0f)]
    public void ARoadRefusedFromAJunctionOnAnotherRoadIsBuiltWithItsStub(float startRadius)
    {
        if (startRadius <= 0f) IslandStart(Start(startRadius));
        var points = new List<Vector2>();
        for (float x = 150f; x >= 20f; x -= 2f) points.Add(new Vector2(x, 0f));
        var heights = points.ConvertAll(p => _world.GetHeight(p.x, p.y));
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(points, heights, points.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 130f, false));
        var plan = Plan(RoundOntoIt(8f, joinX: 30f), startRadius);
        Assert.True(WithItsStub(plan), $"{plan.Stop} {RoadSpatialGrid.LastRefusal}: not planned with its stub");
    }

    /// <summary>Only the road's own plan flags it a duplicate (GenerateRoadCore, PlanStop.Duplicate). A plan made and thrown away
    /// that hits the way in (an approach trial, TrialBuilds; a re-route, Replan; a lay again: each through PlanCandidate)
    /// flags nothing: the road, refused another way later, is not counted nor dropped as a duplicate, and keeps its door
    /// retries (RoadDoors stops them only for a flagged road). The flag was set by every such plan.</summary>
    [Theory]
    [InlineData(8f)]
    [InlineData(0f)]
    public void APlanThrownAwayThatHitsTheWayInFlagsNoDuplicate(float startRadius)
    {
        var flag = typeof(RoadNetworkGenerator).GetField("t_reEnd", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        flag.SetValue(null, null);
        ServingRoad(startRadius);
        var path = RoundOntoIt(100f);
        bool trial = RoadNetworkGenerator.TrialBuilds(new RoadPathfinder(_world), path, Start(startRadius), startRadius, path[path.Count - 1], 0f,
            false, false, startReached: true, endReached: true, 4f, startRadius, 0f);
        Assert.False(trial);
        Assert.Null(flag.GetValue(null));
        var plan = Plan(path, startRadius, endReached: true);
        Assert.Equal(RoadNetworkGenerator.PlanStop.Duplicate, plan.Stop);
        Assert.Null(flag.GetValue(null));
    }

    /// <summary>The road to a staircase's foot at the start: from the foot at (20, 0) east to (60, 0), south to (60, -30) and back
    /// west to (30, -30). Its first 40 m along it from the foot (to (60, 0)) is the staircase's way in; (30, -30) is 100 m along it,
    /// 32 m from the foot in a straight line.</summary>
    private void ToAStaircasesFoot(float radius)
    {
        var points = new List<Vector2>();
        for (float x = 20f; x < 60f; x += 2f) points.Add(new Vector2(x, 0f));
        for (float z = 0f; z > -30f; z -= 2f) points.Add(new Vector2(60f, z));
        for (float x = 60f; x >= 30f; x -= 2f) points.Add(new Vector2(x, -30f));
        Serving(radius, points, foot: true);
    }

    /// <summary>Measured along the road: a road joining the foot's road 30 m along it from the foot is on the way in, a duplicate
    /// (its other end reached too), dropped; one joining it 100 m along it, though 32 m from the foot in a straight line, is not, and starts at that junction.</summary>
    [Theory]
    [InlineData(8f)]
    [InlineData(0f)]
    public void TheWayInOffAStaircasesFootIsMeasuredAlongItsRoad(float startRadius)
    {
        ToAStaircasesFoot(startRadius);
        var onIt = Plan(RoundOntoIt(100f, joinX: 50f), startRadius, endReached: true);
        Assert.Equal(RoadNetworkGenerator.PlanStop.Duplicate, onIt.Stop);

        var path = new List<Vector2>();
        for (float x = 0f; x < 15f; x += 3f) path.Add(new Vector2(x, -12f));
        for (int i = 0; i <= 5; i++) path.Add(Vector2.Lerp(new Vector2(15f, -12f), new Vector2(30f, -30f), i / 5f));
        for (float z = -34f; z >= -80f; z -= 4f) path.Add(new Vector2(30f, z));
        var past = Plan(path, startRadius);
        Assert.True(past.Plans != null, $"not planned: {past.Stop} {RoadSpatialGrid.LastRefusal}");
        Assert.DoesNotContain(past.Plans!.SelectMany(p => p.Points), p => p.y > -29f);
    }
}
