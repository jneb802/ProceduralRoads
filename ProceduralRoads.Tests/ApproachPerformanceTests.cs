using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public class ApproachPerformanceTests
{
    private sealed class Ground : WorldGenerator
    {
        public int Calls;
        public override float GetHeight(float x, float z) { Calls++; return 52f + Mathf.Max(0,-z)*0.5f; }
    }
    private static List<Vector2> Route()
    {
        var path = new List<Vector2>();
        for (int x=200; x>=16; x-=8) path.Add(new Vector2(x,0));
        return path;
    }

    [Fact]
    public void BadArrivalHeightIsRejectedBeforeProfiling()
    {
        RoadSpatialGrid.Clear(); RoadSiteProtection.Reset();
        using var grade=GradeCap.At(0.35f);
        var world=new Ground(); var path=Route();
        int unusableTargets=0;
        float GroundAt(Vector2 p) => world.GetHeight(p.x,p.y);
        float? Target(Vector2 p)
        {
            // The original profile must still be scored, even when its end is bad.
            if (!p.Equals(path[path.Count-1]) && Mathf.Abs(GroundAt(p)-60f)>1.5f) unusableTargets++;
            return Mathf.Abs(GroundAt(p)-60f)<=1.5f ? 60f : null;
        }
        var better=RoadSiteApproach.Improve(path,new Vector2(),16,4,world,false,Target,GroundAt,60f);
        Assert.NotSame(path,better);
        Assert.InRange(GroundAt(better[better.Count-1]),58.5f,61.5f);
        Assert.Equal(0,unusableTargets);
    }

    [Fact]
    public void SharedSamplesKeepExactProfilesAndAvoidRepeatedTerrainQueries()
    {
        RoadSpatialGrid.Clear(); RoadSiteProtection.Reset();
        using var grade=GradeCap.At(0.35f);
        var world=new Ground(); var path=Route();
        var expected=RoadSpatialGrid.PlanRoadPath(path,4,world);
        Assert.NotNull(expected);
        var samples=new ApproachTerrainSamples(p=>BiomeBlendedHeight.GetBlendedHeight(p.x,p.y,world));
        var first=RoadSpatialGrid.PlanRoadPath(path,4,world,terrainHeight:samples.Get);
        int calls=world.Calls;
        var second=RoadSpatialGrid.PlanRoadPath(path,4,world,terrainHeight:samples.Get);
        Assert.Equal(calls,world.Calls);
        Assert.Equal(expected!.Points,first!.Points);
        Assert.Equal(expected.Heights,first.Heights);
        Assert.Equal(first.Points,second!.Points);
        Assert.Equal(first.Heights,second.Heights);
        Assert.Equal(expected.TotalLength,second.TotalLength);
    }

    private sealed class FlatCountingWorld : WorldGenerator
    {
        public readonly Dictionary<Vector2,int> Reads = new();
        public override float GetHeight(float x,float z)
        {
            var point=new Vector2(x,z);
            Reads.TryGetValue(point,out int count); Reads[point]=count+1;
            return 60f;
        }
    }

    [Fact]
    public void ApproachTrialsReuseProceduralSamplesButKeepAuthoredGroundSeparate()
    {
        RoadSpatialGrid.Clear(); RoadSiteProtection.Reset();
        using var grade=GradeCap.At(0.35f);
        var world=new FlatCountingWorld(); var path=Route();
        // Different authored ground forces trial profiles. Using that ground
        // for raw heights would never read the world; bypassing the shared
        // sampler would read the same procedural point for multiple trials.
        RoadSiteApproach.Improve(path,new Vector2(),16,4,world,false,_=>70f,_=>70f,70f);
        Assert.NotEmpty(world.Reads);
        Assert.All(world.Reads.Values,count=>Assert.Equal(1,count));
    }

    [Fact]
    public void CapacityLimitsRetentionWithoutChangingUncachedValues()
    {
        int calls=0;
        var samples=new ApproachTerrainSamples(p=>{calls++;return p.x+2*p.y;},2);
        Assert.Equal(1f,samples.Get(new Vector2(1,0)));
        Assert.Equal(2f,samples.Get(new Vector2(2,0)));
        Assert.Equal(3f,samples.Get(new Vector2(3,0)));
        Assert.Equal(3f,samples.Get(new Vector2(3,0)));
        Assert.Equal(1f,samples.Get(new Vector2(1,0)));
        Assert.Equal(2,samples.Count);
        Assert.Equal(4,calls);
        // A new decision cannot reuse the previous world's results.
        var next=new ApproachTerrainSamples(_=>80f);
        Assert.Equal(80f,next.Get(new Vector2(1,0)));
        Assert.Equal(1f,samples.Get(new Vector2(1,0)));
    }
}
