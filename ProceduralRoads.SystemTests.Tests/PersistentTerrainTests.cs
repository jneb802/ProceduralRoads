using System.Text.Json;
using ProceduralRoads.SystemTests;
using Xunit;
public class PersistentTerrainTests
{
    [Theory]
    [InlineData(64,65,0,65)] [InlineData(64,65,2,65)] [InlineData(64,65,3,64.5f)] [InlineData(64,65,4,64)]
    [InlineData(64,90,0,72)] [InlineData(64,0,3,56)]
    public void IndependentProfile(float before,float target,int distance,float expected)=>Assert.Equal(expected,PersistentTerrainScenario.Expected(before,target,distance));
    private static JsonElement Data(Action<List<Dictionary<string,object>>>? edit=null,int width=4,string profile="paved")
    {
        var rows=new List<Dictionary<string,object>>();
        for(int zone=0;zone<2;zone++) foreach(int x in zone==0?new[]{24,32}:new[]{32,40}) foreach(int d in width==8?new[]{0,2,3,4,5,6}:new[]{0,2,3,4,6})
        {
            float expected=d<=width/2?65:d==width/2+1?64.5f:64;
            rows.Add(new(){["zoneOffset"]=zone,["x"]=x,["z"]=d,["distance"]=d,["before"]=64,["height"]=expected,["colliderHeight"]=expected});
        }
        edit?.Invoke(rows);
        return JsonSerializer.SerializeToElement(new{source="natural-heightmap-road-fixture",complete=true,x=32,z=0,target=65,width,margin=2,paintProfile=profile,samples=rows});
    }
    [Fact] public void BothBoundaryCopiesRequired()=>Assert.Equal(20,PersistentTerrainScenario.Compare(Data(),4).Count);
    [Fact] public void MissingBoundaryRejected()=>Assert.Throws<InvalidOperationException>(()=>PersistentTerrainScenario.Compare(Data(r=>r.RemoveAt(10)),4));
    [Fact] public void ShiftedRowRejected()=>Assert.Throws<InvalidOperationException>(()=>PersistentTerrainScenario.Compare(Data(r=>r[0]["x"]=25),4));
    [Fact] public void NoWriteIsDetected()=>Assert.Equal(12,PersistentTerrainScenario.Compare(Data(r=>r.ForEach(x=>{x["height"]=64;x["colliderHeight"]=64;})),4).Count(x=>!x.Passed));
    [Fact] public void WrongColliderIsDetected()=>Assert.Single(PersistentTerrainScenario.Compare(Data(r=>r[0]["colliderHeight"]=62),4).Where(x=>!x.Passed));
    [Theory] [InlineData(64,65,4,65)] [InlineData(64,65,5,64.5f)] [InlineData(64,65,6,64)] [InlineData(64,90,5,72)]
    public void IndependentWidthEightProfile(float before,float target,int distance,float expected)=>Assert.Equal(expected,PersistentTerrainScenario.Expected(before,target,distance,8));
    [Fact] public void WidthEightChecksHalfStrengthRelease()
    {
        Assert.Equal(24,PersistentTerrainScenario.Compare(Data(width:8,profile:"dirt-fade"),8).Count);
        // A core that runs on through the release fails exactly the four 5 m samples.
        Assert.Equal(4,PersistentTerrainScenario.Compare(Data(r=>r.Where(x=>(int)x["distance"]==5).ToList().ForEach(x=>{x["height"]=65f;x["colliderHeight"]=65f;}),8,"dirt-fade"),8).Count(x=>!x.Passed));
        Assert.Throws<InvalidOperationException>(()=>PersistentTerrainScenario.Compare(Data(r=>r.RemoveAll(x=>(int)x["distance"]==5),8,"dirt-fade"),8));
    }
    [Fact] public void ReportedWidthMustMatchThePlannedProfile()
    {
        Assert.Throws<InvalidOperationException>(()=>PersistentTerrainScenario.Compare(Data(),8));
        Assert.Throws<InvalidOperationException>(()=>PersistentTerrainScenario.Compare(Data(profile:"dirt-fade"),4));
    }
    [Fact] public void PaintProfileSelectsWidth()
    {
        var plan=PlanTests.Valid();
        Assert.Equal(4,PersistentTerrainScenario.WidthFor(plan));
        plan.Environment["ROADS_TEST_PAINT_PROFILE"]="dirt-fade"; Assert.Equal(8,PersistentTerrainScenario.WidthFor(plan));
        Assert.Equal("paved",PersistentTerrainScenario.ProfileName(4)); Assert.Equal("dirt-fade",PersistentTerrainScenario.ProfileName(8));
    }
    private static JsonElement PaintData(Action<List<Dictionary<string,object>>>? edit=null)
    {
        var rows=new List<Dictionary<string,object>>();
        for(int zone=0;zone<2;zone++) foreach(int x in zone==0?new[]{24,30}:new[]{34,40}) foreach(int d in new[]{0,1,2,4})
            rows.Add(new(){["zoneOffset"]=zone,["x"]=x,["z"]=1800+d,["distance"]=d,["before"]=new[]{.2f,.4f,.6f,.3f},["paint"]=d<=1?new[]{0f,0f,1f,.3f}:new[]{.2f,.4f,.6f,.3f}});
        edit?.Invoke(rows);
        return JsonSerializer.SerializeToElement(new{x=32,z=1800,paintInput="saved-explicit-rgba",paintSamples=rows});
    }
    [Fact] public void PaintUsesBothSidesOfSeam()=>Assert.Equal(16,PersistentTerrainScenario.ComparePaint(PaintData()).Count);
    [Fact] public void PaintNoWriteRejected()=>Assert.Throws<InvalidOperationException>(()=>PersistentTerrainScenario.ComparePaint(PaintData(r=>r.ForEach(x=>x["paint"]=x["before"]))));
    [Fact] public void PaintAlphaDamageRejected()=>Assert.Throws<InvalidOperationException>(()=>PersistentTerrainScenario.ComparePaint(PaintData(r=>r[0]["paint"]=new[]{0f,0f,1f,1f})));
    [Fact] public void PaintMissingSampleRejected()=>Assert.Throws<InvalidOperationException>(()=>PersistentTerrainScenario.ComparePaint(PaintData(r=>r.RemoveAt(0))));
    [Fact] public void WidthEightPaintChecksDirtFadeAndAlphaIndependently()
    {
        var rows=new List<Dictionary<string,object>>();
        for(int zone=0;zone<2;zone++) foreach(int x in zone==0?new[]{24,30}:new[]{34,40}) foreach(int distance in new[]{0,2,3,4})
            rows.Add(new(){["zoneOffset"]=zone,["x"]=x,["z"]=distance,["distance"]=distance,["before"]=new[]{.2f,.4f,.6f,.3f},
                ["paint"]=distance<=2?new[]{1f,0f,0f,.3f}:distance==3?new[]{.4816f,.2592f,.3888f,.3f}:new[]{.2f,.4f,.6f,.3f}});
        JsonElement Data()=>JsonSerializer.SerializeToElement(new{x=32,z=0,width=8,paintInput="saved-explicit-rgba",paintSamples=rows});
        Assert.Equal(16,PersistentTerrainScenario.ComparePaint(Data()).Count);
        rows[2]["paint"]=new[]{1f,0f,0f,.3f}; // A solid edge cannot masquerade as fading paint.
        Assert.Throws<InvalidOperationException>(()=>PersistentTerrainScenario.ComparePaint(Data()));
    }
}
