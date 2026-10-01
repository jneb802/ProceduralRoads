using UnityEngine;
using Xunit;
namespace ProceduralRoads.Tests;
public class ReviewerFinalCrossingTests
{
    private sealed class ShoreThenCliff : WorldGenerator
    {
        public override float GetHeight(float x,float z) => Mathf.Abs(z)>4 ? 26 : x >= -20 && x <= -12 ? 33 : Mathf.Abs(x-16)<0.7f ? 33 : x >= 40 && x <=60 ? 50 : 26;
        public override Heightmap.Biome GetBiome(float x,float z) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x,float z,out float weight,out float width) {weight=GetHeight(x,z)<30?1:0;width=52;}
    }
    [Fact]
    public void FinalCrossingMustRespectTheBankDeltaTheRouterAccepted()
    {
        var w=new ShoreThenCliff();
        var path=new RoadPathfinder(w){Bridges=true,Fords=false}.FindPath(new Vector2(-16,0),new Vector2(16,0));
        Assert.NotNull(path);
        var c=Assert.Single(RoadCrossingDetector.Detect(path!,w,bridges:true,fords:false));
        float delta=Mathf.Abs(w.GetHeight(c.ToBank.x,c.ToBank.y)-w.GetHeight(c.FromBank.x,c.FromBank.y));
        Assert.True(delta<=RoadConstants.MaxBridgeBankDelta,
            $"priced jump: (-16,0)->(16,0), heights 33/33; built banks ({c.FromBank.x},{c.FromBank.y})->({c.ToBank.x},{c.ToBank.y}), delta {delta}, cap {RoadConstants.MaxBridgeBankDelta}");
    }
}
