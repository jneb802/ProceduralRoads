using System.Collections.Generic;
using UnityEngine;
using Xunit;
namespace ProceduralRoads.Tests;
public class ReviewerHeadingTests
{
    private class SmallBanks : WorldGenerator
    {
        bool Land(float x,float z) => Mathf.Abs(z)<0.6f && (Mathf.Abs(x)<0.6f || Mathf.Abs(x-32f)<0.6f);
        public override float GetHeight(float x,float z) => Land(x,z) ? 33f : 26f;
        public override Heightmap.Biome GetBiome(float x,float z) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x,float z,out float weight,out float width) {weight=Land(x,z)?0f:1f;width=32f;}
    }
    [Fact]
    public void AcceptedRiverJumpMustNotLoseItsBridgeInPostprocessing()
    {
        var world=new SmallBanks();
        var router=new RoadPathfinder(world){Bridges=true,Fords=false};
        var path=router.FindPath(new Vector2(0,0),new Vector2(32,0));
        Assert.NotNull(path);
        Assert.True(path!.Count>=2);
        var crossings=RoadCrossingDetector.Detect(path,world,bridges:true,fords:false);
        Assert.True(crossings.Count>0,$"router accepted {path.Count} waypoints spanning 32 m of river, but detector emitted {crossings.Count} crossings");
    }
}
