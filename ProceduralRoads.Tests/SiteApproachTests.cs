using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public class SiteApproachTests
{
    private sealed class Hillside : WorldGenerator
    {
        // The direct east approach cuts across a ridge; the south side of
        // the same site has an open contour. Height stays finite and dry.
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
        RoadSiteApproach.Improve(path, new Vector2(0, 0), 16, 4, world, start,
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
        // The upstream road and the site's exterior radius survive.
        Assert.Equal(original[0], better[0]);
        Assert.Equal(original[10], better[10]);
        Assert.InRange(better[better.Count - 1].magnitude, 15.99f, 16.01f);
        var plan = RoadSpatialGrid.PlanRoadPath(better, 4, world, endGround: 60f)!;
        Assert.NotNull(plan);
        Assert.InRange(RoadGrade.SteepestStep(plan.Points, plan.Heights), 0, 0.3501f);
        Assert.All(plan.Points, p => Assert.True(p.magnitude >= 15.9f));
    }

    private sealed class PlatformSlope : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 52f + Mathf.Max(0, -z) * 0.5f;
    }

    [Fact]
    public void SeeksThePlatformsElevationInsteadOfStoppingBelowIt()
    {
        RoadSpatialGrid.Clear();
        RoadSiteProtection.Reset();
        using var grade = GradeCap.At(0.35f);
        var world = new PlatformSlope(); var original = Route();
        float Ground(Vector2 p) => world.GetHeight(p.x,p.y);
        float? Target(Vector2 p) => Mathf.Abs(Ground(p)-60f) <= 1.5f ? 60f : null;
        var better = RoadSiteApproach.Improve(original,new Vector2(),16,4,world,false,Target,Ground,60f);
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
    public void ReachesPlatformOnOtherSideWithoutCrossingProtectedSite()
    {
        RoadSpatialGrid.Clear();
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(new Vector2(),12f) });
        using var grade = GradeCap.At(0.35f);
        var world = new BacksideSlope(); var original = Route();
        float Ground(Vector2 p) => world.GetHeight(p.x,p.y);
        float? Target(Vector2 p) => Mathf.Abs(Ground(p)-60f) <= 1.5f ? 60f : null;
        try
        {
            var better = RoadSiteApproach.Improve(original,new Vector2(),16,4,world,false,Target,Ground,60f);
            Assert.NotSame(original,better);
            Assert.InRange(Ground(better[better.Count-1]),58.5f,61.5f);
            var plan = RoadSpatialGrid.PlanRoadPath(better,4,world,endGround:60f)!;
            Assert.NotNull(plan);
            Assert.All(plan.Points,p => Assert.True(p.magnitude >= 15.9f));
            Assert.True(RoadGrade.SteepestStep(plan.Points,plan.Heights) <= 0.3501f);
            Assert.Equal(original[0],better[0]);
        }
        finally { RoadSiteProtection.Reset(); }
    }

    [Fact]
    public void TerrainSearchCanGoAroundAnObstacleThatBlocksTheSimpleArcs()
    {
        RoadSpatialGrid.Clear();
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(new Vector2(),12f),
            new RoadSiteProtection.Footprint(new Vector2(40,0),18f) });
        using var grade = GradeCap.At(0.35f);
        var world = new BacksideSlope(); var original = Route();
        float Ground(Vector2 p) => world.GetHeight(p.x,p.y);
        float? Target(Vector2 p) => Mathf.Abs(Ground(p)-60f) <= 1.5f ? 60f : null;
        try
        {
            var better = RoadSiteApproach.Improve(original,new Vector2(),16,4,world,false,Target,Ground,60f);
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
        finally { RoadSiteProtection.Reset(); }
    }

    [Fact]
    public void NetworkResetAlsoInvalidatesThePlacementHeightSnapshot()
    {
        int resets=0;
        LocationLevelling.ResetPlacements=()=>resets++;
        try { RoadNetworkGenerator.Reset(); Assert.Equal(1,resets); }
        finally { LocationLevelling.ResetPlacements=null; }
    }

    [Fact]
    public void ExteriorArrivalDoesNotAimIntoAnExcavationButStillReachesARaisedPlatform()
    {
        Assert.Equal(60f,LocationLevelling.ApproachHeight(60f,new[] {new LevelOp(2,0,-4,8,false)}));
        Assert.Equal(68f,LocationLevelling.ApproachHeight(60f,new[] {new LevelOp(2,0,8,8,false)}));
        var world=new Flat(); WorldGenerator.instance=world;
        LocationLevelling.Source=_=>new[] {new LevelOp(2,0,0.8f,8,false)};
        try { Assert.Equal(60f,RoadNetworkGenerator.ApproachGround(new Vector2(20,0),new Vector2(),16)); }
        finally { WorldGenerator.instance=null; LocationLevelling.Source=null; }
    }

    [Fact]
    public void PlacedPlatformUsesSavedRootRatherThanRecomputedGround()
    {
        var world=new Flat();
        LocationLevelling.PlacementHeightSource=_=>66f;
        try
        {
            Assert.Equal(64.8f,LocationLevelling.PlatformHeight(LocationLevelling.CentreHeight(new Vector2(),world),
                new[] {new LevelOp(2,0,-1.2f,8,false)}));
            LocationLevelling.PlacementHeightSource=_=>float.NaN;
            Assert.Equal(60f,LocationLevelling.CentreHeight(new Vector2(),world));
            LocationLevelling.PlacementHeightSource=_=>null;
            Assert.Equal(60f,LocationLevelling.CentreHeight(new Vector2(),world));
        }
        finally { LocationLevelling.PlacementHeightSource=null; }
    }

    [Fact]
    public void PlatformTargetDoesNotInventAnEmbankmentOrMergeDifferentStoreys()
    {
        Assert.Equal(58.8f, LocationLevelling.PlatformHeight(60f, new[] { new LevelOp(2,0,-1.2f,8,false) }));
        Assert.Null(LocationLevelling.PlatformHeight(60f,new[] { new LevelOp(0,0,0,8,false),new LevelOp(10,0,6,4,false) }));
        var world = new PlatformSlope(); WorldGenerator.instance=world;
        LocationLevelling.Source = _ => new[] { new LevelOp(0,0,8,4,false) };
        try
        {
            Assert.Null(RoadNetworkGenerator.ApproachGround(new Vector2(16,0),new Vector2(),16));
            Assert.Equal(60f,RoadNetworkGenerator.ApproachGround(new Vector2(0,-16),new Vector2(),16));
        }
        finally { LocationLevelling.Source=null; WorldGenerator.instance=null; }
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
    public void CannotImproveByCuttingThroughTheSite()
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
        Assert.Same(path, RoadSiteApproach.Improve(path, new Vector2(), 16, 4, new Flat(), false,
            _ => float.NaN, _ => float.NaN));
    }

    [Fact]
    public void ShortRoadAndUnknownFootprintStayAlone()
    {
        var world = new Flat(); var shortPath = new List<Vector2> { new(24, 0), new(16, 0) };
        Assert.Same(shortPath, Improve(shortPath, world));
        var path = Route();
        Assert.Same(path, RoadSiteApproach.Improve(path, new Vector2(), 0, 4, world, false,
            _ => 60f, _ => 60f));
    }
}
