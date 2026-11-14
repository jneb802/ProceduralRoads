using System;
using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

public class LocationApproachTests
{
    private sealed class Hillside : WorldGenerator
    {
        // The direct east approach cuts across a ridge; the south side of
        // the same location has an open contour. Height stays finite and dry.
        public override float GetHeight(float x, float z) =>
            60f + 8f * (float)Math.Exp(-Math.Pow((x - 28f) / 16f, 2) - Math.Pow(z / 7f, 2));
    }
    private sealed class Flat : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 60;
    }
    private static List<Vector2> Route()
    {
        var path = new List<Vector2>();
        for (int x = 200; x >= 16; x -= 8) path.Add(new Vector2(x, 0));
        return path;
    }
    private static List<Vector2> Improve(List<Vector2> path, WorldGenerator world, bool start = false) =>
        RoadLocationApproach.Improve(path, new Vector2(0, 0), 16, 4, world, start,
            _ => 60f, p => world.GetHeight(p.x, p.y));

    [Fact]
    public void GoesAroundTheRidgeInsteadOfCuttingThroughIt()
    {
        RoadSpatialGrid.Clear();
        using var grade = GradeCap.At(0.35f);
        var world = new Hillside(); var original = Route();
        var better = Improve(original, world);
        Assert.NotSame(original, better);
        Assert.True(Mathf.Abs(better[better.Count - 1].y) > 5f);
        // The upstream road and the location's exterior radius survive.
        Assert.Equal(original[0], better[0]);
        Assert.Equal(original[10], better[10]);
        Assert.InRange(better[better.Count - 1].magnitude, 15.99f, 16.01f);
        var plan = RoadSpatialGrid.PlanRoadPath(better, 4, world, endGround: 60f)!;
        Assert.NotNull(plan);
        Assert.InRange(RoadGrade.SteepestStep(plan.Points, plan.Heights), 0, 0.3501f);
        Assert.All(plan.Points, p => Assert.True(p.magnitude >= 15.9f));
    }

    /// <summary>A location that paints past the ground it shapes (Arrival under Radius) is approached to its arrival edge, as the
    /// final check holds it: the re-aim is not refused for the paint it only stands in.</summary>
    [Fact]
    public void ReaimsAtALocationThatPaintsPastItsShapedGround()
    {
        RoadSpatialGrid.Clear();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(), 20f, 16f) });
        // Only a location on the preload list is arrived at inside its footprint (RoadLocationProtection.Enterable).
        PlacedLocations.Preload(new Vector2());
        using var grade = GradeCap.At(0.35f);
        try
        {
            var world = new Hillside(); var original = Route();
            var better = Improve(original, world);
            Assert.NotSame(original, better);
            Assert.True(Mathf.Abs(better[better.Count - 1].y) > 5f);
        }
        finally { RoadLocationProtection.Reset(); PlacedLocations.Reset(); }
    }

    /// <summary>The same location not on the preload list, the same trial asked at the ground it shapes: every proposal ending in
    /// the paint it does not shape is refused by the trial's final check (RoadLocationProtection.Enterable, through ArrivalKeep),
    /// so the road keeps its route; generation asks it only at the ring outside the whole footprint
    /// (RoadNetworkGenerator.KeepOutEdge).</summary>
    [Fact]
    public void AnApproachToALocationNotOnThePreloadListStaysOutOfItsFootprint()
    {
        RoadSpatialGrid.Clear();
        // Not on the preload list: none left by another test (PlacedLocations.Preloaded reads it).
        PlacedLocations.Reset();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(), 20f, 16f) });
        // The footprint is installed and the location is kept out of: a failure below is then the approach's, not a missing index.
        Assert.True(RoadLocationProtection.Contains(new Vector2()), "the location's footprint was not installed by Set");
        Assert.False(RoadLocationProtection.Enterable(new Vector2()), "the location at the origin is enterable: it is on the preload list or not a footprint");
        using var grade = GradeCap.At(0.35f);
        try
        {
            var world = new Hillside(); var original = Route();
            var better = Improve(original, world);
            Assert.Same(original, better);
        }
        finally { RoadLocationProtection.Reset(); }
    }

    private sealed class PlatformSlope : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 52f + Mathf.Max(0, -z) * 0.5f;
    }

    [Fact]
    public void SeeksThePlatformsElevationInsteadOfStoppingBelowIt()
    {
        RoadSpatialGrid.Clear();
        RoadLocationProtection.Reset();
        using var grade = GradeCap.At(0.35f);
        var world = new PlatformSlope(); var original = Route();
        float Ground(Vector2 p) => world.GetHeight(p.x,p.y);
        float? Target(Vector2 p) => Mathf.Abs(Ground(p)-60f) <= 1.5f ? 60f : null;
        var better = RoadLocationApproach.Improve(original,new Vector2(),16,4,world,false,Target,Ground,60f);
        Assert.NotSame(original,better);
        Assert.InRange(Ground(better[better.Count-1]),58.5f,61.5f);
        var plan = RoadSpatialGrid.PlanRoadPath(better,4,world,endGround:Target(better[better.Count-1]));
        Assert.NotNull(plan);
        Assert.Equal(60f,plan!.Heights[plan.Heights.Count-1]);
        Assert.True(RoadGrade.SteepestStep(plan.Points,plan.Heights) <= 0.3501f);
    }

    private sealed class BacksideSlope : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 68f + 0.5f * Mathf.Clamp(x, -16f, 16f);
    }

    [Fact]
    public void ReachesPlatformOnOtherSideWithoutCrossingProtectedLocation()
    {
        RoadSpatialGrid.Clear();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(),12f) });
        using var grade = GradeCap.At(0.35f);
        var world = new BacksideSlope(); var original = Route();
        float Ground(Vector2 p) => world.GetHeight(p.x,p.y);
        float? Target(Vector2 p) => Mathf.Abs(Ground(p)-60f) <= 1.5f ? 60f : null;
        try
        {
            var better = RoadLocationApproach.Improve(original,new Vector2(),16,4,world,false,Target,Ground,60f);
            Assert.NotSame(original,better);
            Assert.InRange(Ground(better[better.Count-1]),58.5f,61.5f);
            var plan = RoadSpatialGrid.PlanRoadPath(better,4,world,endGround:60f)!;
            Assert.NotNull(plan);
            Assert.All(plan.Points,p => Assert.True(p.magnitude >= 15.9f));
            Assert.True(RoadGrade.SteepestStep(plan.Points,plan.Heights) <= 0.3501f);
            Assert.Equal(original[0],better[0]);
        }
        finally { RoadLocationProtection.Reset(); }
    }

    [Fact]
    public void TerrainSearchCanGoAroundAnObstacleThatBlocksTheSimpleArcs()
    {
        RoadSpatialGrid.Clear();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(),12f),
            new RoadLocationProtection.Footprint(new Vector2(40,0),18f) });
        using var grade = GradeCap.At(0.35f);
        var world = new BacksideSlope(); var original = Route();
        float Ground(Vector2 p) => world.GetHeight(p.x,p.y);
        float? Target(Vector2 p) => Mathf.Abs(Ground(p)-60f) <= 1.5f ? 60f : null;
        try
        {
            var better = RoadLocationApproach.Improve(original,new Vector2(),16,4,world,false,Target,Ground,60f);
            Assert.NotSame(original,better);
            Assert.InRange(Ground(better[better.Count-1]),58.5f,61.5f);
            var plan = RoadSpatialGrid.PlanRoadPath(better,4,world,endGround:60f)!;
            Assert.NotNull(plan);
            Assert.All(plan.Points,p => {
                Assert.True(p.magnitude >= 15.9f);
                Assert.True(Vector2.Distance(p,new Vector2(40,0)) >= 21.9f);
            });
            Assert.True(RoadGrade.SteepestStep(plan.Points,plan.Heights) <= 0.3501f);
        }
        finally { RoadLocationProtection.Reset(); }
    }

    private sealed class PlatformBeyondACliff : WorldGenerator
    {
        // The location's own height is only found 25 m out and further: below that, a cliff.
        public override float GetHeight(float x, float z) => new Vector2(x, z).magnitude >= 25f ? 60f : 50f;
    }

    [Fact]
    public void TheTerrainSearchNeverEndsFurtherOutThanTheNetworkCountsTheLocationReached()
    {
        // A road the contour search moved further out than the reached test looks counted
        // its location unreached although it was built to it (two Crypt4s 42.7 and 43.6 m out
        // against 42 m reached). Here the only platform height is past that
        // reach (16 m keep-out edge + one 8 m cell), so the road keeps its own end.
        RoadSpatialGrid.Clear();
        RoadLocationProtection.Reset();
        using var grade = GradeCap.At(0.35f);
        var world = new PlatformBeyondACliff(); var original = Route();
        float Ground(Vector2 p) => world.GetHeight(p.x,p.y);
        float? Target(Vector2 p) => Mathf.Abs(Ground(p)-60f) <= 1.5f ? 60f : null;
        var better = RoadLocationApproach.Improve(original,new Vector2(),16,4,world,false,Target,Ground,60f);
        Assert.InRange(better[better.Count-1].magnitude,0f,16f+RoadPathfinder.CellSize+0.01f);
    }

    [Fact]
    public void NetworkResetAlsoInvalidatesThePlacementHeightSnapshot()
    {
        int resets=0;
        using var overrides = StaticOverride.Keep(() => LocationLevelling.ResetPlacements);
        LocationLevelling.ResetPlacements=()=>resets++;
        RoadNetworkGenerator.Reset(); Assert.Equal(1,resets);
    }

    [Fact]
    public void ExteriorArrivalDoesNotAimIntoAnExcavationButStillReachesARaisedPlatform()
    {
        Assert.Equal(60f,LocationLevelling.ApproachHeight(60f,new[] {new LevelOp(2,0,-4,8,false)}));
        Assert.Equal(68f,LocationLevelling.ApproachHeight(60f,new[] {new LevelOp(2,0,8,8,false)}));
        var world=new Flat(); using var scope=new ValheimWorldScope().WithWorld(world);
        using var overrides = StaticOverride.Keep(() => LocationLevelling.Source);
        // Ground level with the raised platform (StairLayout.StairFitsFrom) is arrived on as it is; a platform 0.8 m
        // over it is not level with it (1.5 m counted before the end-level rule): no target.
        LocationLevelling.Source=_=>new[] {new LevelOp(2,0,0.4f,8,false)};
        Assert.Equal(60f,RoadNetworkGenerator.ApproachGround(new Vector2(20,0),new Vector2(),16));
        LocationLevelling.Source=_=>new[] {new LevelOp(2,0,0.8f,8,false)};
        Assert.Null(RoadNetworkGenerator.ApproachGround(new Vector2(20,0),new Vector2(),16));
    }

    [Fact]
    public void PlacedPlatformUsesSavedRootRatherThanRecomputedGround()
    {
        var world=new Flat();
        using var overrides = StaticOverride.Keep(() => LocationLevelling.PlacementHeightSource);
        LocationLevelling.PlacementHeightSource=_=>66f;
        Assert.Equal(64.8f,LocationLevelling.PlatformHeight(LocationLevelling.CentreHeight(new Vector2(),world),
            new[] {new LevelOp(2,0,-1.2f,8,false)}));
        LocationLevelling.PlacementHeightSource=_=>float.NaN;
        Assert.Equal(60f,LocationLevelling.CentreHeight(new Vector2(),world));
        LocationLevelling.PlacementHeightSource=_=>null;
        Assert.Equal(60f,LocationLevelling.CentreHeight(new Vector2(),world));
    }

    [Fact]
    public void PlatformTargetDoesNotInventAnEmbankmentOrMergeDifferentStoreys()
    {
        Assert.Equal(58.8f, LocationLevelling.PlatformHeight(60f, new[] { new LevelOp(2,0,-1.2f,8,false) }));
        Assert.Null(LocationLevelling.PlatformHeight(60f,new[] { new LevelOp(0,0,0,8,false),new LevelOp(10,0,6,4,false) }));
        var world = new PlatformSlope(); using var scope = new ValheimWorldScope().WithWorld(world);
        using var overrides = StaticOverride.Keep(() => LocationLevelling.Source);
        LocationLevelling.Source = _ => new[] { new LevelOp(0,0,8,4,false) };
        Assert.Null(RoadNetworkGenerator.ApproachGround(new Vector2(16,0),new Vector2(),16));
        Assert.Equal(60f,RoadNetworkGenerator.ApproachGround(new Vector2(0,-16),new Vector2(),16));
    }

    [Fact]
    public void FlatApproachIsLeftExactlyAlone()
    {
        RoadSpatialGrid.Clear();
        var original = Route();
        Assert.Same(original, Improve(original, new Flat()));
    }

    [Fact]
    public void StartAndEndUseTheSameDecision()
    {
        RoadSpatialGrid.Clear();
        using var grade = GradeCap.At(0.35f);
        var original = Route(); var world = new Hillside();
        var end = Improve(original, world);
        original.Reverse();
        var start = Improve(original, world, true);
        start.Reverse();
        Assert.Equal(end, start);
    }

    [Fact]
    public void CannotImproveByCuttingThroughTheLocation()
    {
        RoadSpatialGrid.Clear();
        var path = Route();
        var better = Improve(path, new Hillside());
        Assert.All(better, p => Assert.True(p.magnitude >= 15.9f));
    }

    [Fact]
    public void TrialCandidatesDoNotChangeNetworkGradeStatistics()
    {
        RoadSpatialGrid.Clear();
        RoadGrade.SteepestPlanned = 0.1f;
        Improve(Route(), new Hillside());
        Assert.Equal(0.1f, RoadGrade.SteepestPlanned);
    }

    [Fact]
    public void InvalidTerrainRetainsTheOldPath()
    {
        var path = Route();
        Assert.Same(path, RoadLocationApproach.Improve(path, new Vector2(), 16, 4, new Flat(), false,
            _ => float.NaN, _ => float.NaN));
    }

    [Fact]
    public void ShortRoadAndUnknownFootprintStayAlone()
    {
        var world = new Flat(); var shortPath = new List<Vector2> { new(24, 0), new(16, 0) };
        Assert.Same(shortPath, Improve(shortPath, world));
        var path = Route();
        Assert.Same(path, RoadLocationApproach.Improve(path, new Vector2(), 0, 4, world, false,
            _ => 60f, _ => 60f));
    }
}
