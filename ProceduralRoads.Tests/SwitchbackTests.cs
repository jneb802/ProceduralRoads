using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;
namespace ProceduralRoads.Tests;

public class SwitchbackTests
{
    private sealed class Slope : WorldGenerator
    { public override float GetHeight(float x,float z)=>60+z*0.3f; }
    private static List<Vector2> WideU()=>new(){new(0,0),new(40,0),new(40,20),new(0,20)};
    [Fact] public void BroadUTurnHasSpaceAndGentleLanding()
    {
        RoadSpatialGrid.Clear();RoadSiteProtection.Reset();using var cap=GradeCap.At(0.35f);
        var path=WideU();
        Assert.True(RoadSwitchbacks.Shape(path,4,out var rounded,out var landing));
        Assert.NotNull(rounded);Assert.NotNull(landing);
        Assert.Equal(path[0],rounded![0]);Assert.Equal(path[3],rounded[rounded.Count-1]);
        var plan=RoadSpatialGrid.PlanRoadPath(path,4,new Slope());Assert.NotNull(plan);
        Assert.Equal(rounded.Count,plan!.Points.Count);
        for(int i=1;i<rounded.Count;i++)
        {
            float grade=Mathf.Abs(plan.Heights[i]-plan.Heights[i-1])/Vector2.Distance(rounded[i],rounded[i-1]);
            Assert.InRange(grade,0,(landing![i]||landing[i-1])?0.1002f:0.3502f);
        }
        Assert.True(RoadSwitchbacks.Separated(plan.Points,plan.Heights,4));
    }
    [Fact] public void BroadTurnRemainsGentleAfterTheTerrainHeightFit()
    {
        RoadSpatialGrid.Clear();RoadSiteProtection.Reset();
        using var cap=GradeCap.At(0.35f);
        var world=new Slope();var plan=RoadSpatialGrid.PlanRoadPath(WideU(),4,world)!;
        Assert.NotNull(plan);
        var points=new List<RoadSpatialGrid.RoadPoint>();
        for(int i=0;i<plan.Points.Count;i++) points.Add(new(plan.Points[i],4,plan.Heights[i]));
        var method=typeof(RoadTerrainModifier).GetMethod("CalculateBlendedHeight",
            System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
        var heights=new List<float>();
        foreach(var point in plan.Points)
        {
            float ground=world.GetHeight(point.x,point.y);
            var fit=method.Invoke(null,new object?[]{points,point,ground,null})!;
            float target=(float)fit.GetType().GetField("TargetHeight")!.GetValue(fit)!;
            float blend=(float)fit.GetType().GetField("MaxBlend")!.GetValue(fit)!;
            heights.Add(ground+Mathf.Clamp(Mathf.Lerp(ground,target,blend)-ground,-8,8));
        }
        Assert.InRange(RoadGrade.SteepestStep(plan.Points,heights),0,0.3502f);
    }
    [Fact] public void PinchedUIsRefusedInsteadOfBlendingItsLegs()
    {
        RoadSpatialGrid.Clear();RoadSiteProtection.Reset();
        var path=new List<Vector2>{new(0,0),new(40,0),new(40,8),new(0,8)};
        Assert.False(RoadSwitchbacks.Shape(path,4,out _,out _));
        Assert.Null(RoadSpatialGrid.PlanRoadPath(path,4,new Slope()));
    }
    [Fact] public void LongVCanRoundButShortVIsRefused()
    {
        Assert.True(RoadSwitchbacks.Shape(new(){new(0,0),new(80,0),new(0,40)},4,out var shaped,out _));
        Assert.NotNull(shaped);
        Assert.False(RoadSwitchbacks.Shape(new(){new(0,0),new(8,0),new(0,4)},4,out _,out _));
    }
    [Fact] public void OldRepeatedPinchedVFixtureIsNowRefused()
    {
        var path=new List<Vector2>();
        for(int i=0;i<=20;i++) path.Add(new(i*8,(i%2==0?1:-1)*16));
        Assert.False(RoadSwitchbacks.Shape(path,4,out _,out _));
    }
    [Fact] public void TinyBankSnappingSpurIsNotAClimbingSwitchback()
    {
        var path = new List<Vector2>{new(64,0),new(80,-8),new(88,0),new(82,0)};
        Assert.True(RoadSwitchbacks.Shape(path,4,out var shaped,out _));
        Assert.Null(shaped); // preserve ordinary bank smoothing, including road-sharing geometry
        path.Reverse();
        Assert.True(RoadSwitchbacks.Shape(path,4,out shaped,out _));
        Assert.Null(shaped);
    }
    [Fact] public void FlatPinchedBendKeepsItsExistingSharingGeometry()
    {
        var path=new List<Vector2>{new(0,0),new(40,0),new(40,8),new(0,8)};
        Assert.True(RoadSwitchbacks.Shape(path,4,out var shape,out _,_=>33f));
        Assert.Null(shape);
    }
    [Fact] public void OrdinaryBendKeepsExistingSmoothing()
    { Assert.True(RoadSwitchbacks.Shape(new(){new(0,0),new(24,0),new(48,24)},4,out var shape,out _));Assert.Null(shape); }
    [Fact] public void DistantInPathNearbyInSpaceMustNotBlendDifferentElevations()
    {
        var points=new List<Vector2>{new(0,0),new(30,0),new(30,6),new(0,6)};
        Assert.False(RoadSwitchbacks.Separated(points,new(){60,65,67,69},4));
        Assert.True(RoadSwitchbacks.Separated(points,new(){60,60,60,60},4));
    }
    [Fact] public void RoundingMustNotCutThroughAProtectedSite()
    {
        RoadSpatialGrid.Clear();
        RoadSiteProtection.Set(new[]{new RoadSiteProtection.Footprint(new(38,2),2)});
        try { Assert.Null(RoadSpatialGrid.PlanRoadPath(WideU(),4,new Slope())); }
        finally {RoadSiteProtection.Reset();}
    }
}
