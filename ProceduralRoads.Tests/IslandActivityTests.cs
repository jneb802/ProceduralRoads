using System;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Valheim.Testing.Doubles;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public sealed class IslandActivityTests
{
    private sealed class SearchWorld : WorldGenerator
    {
        internal Func<long>? Steps;
        internal readonly ManualResetEventSlim First=new(false), Second=new(false), ContinueFirst=new(false), ContinueSecond=new(false);
        private int reads, stage;
        public override float GetHeight(float x,float z)
        {
            if((++reads & 127)==0 && Steps!=null)
            {
                long steps=Steps();
                if(stage==0 && steps>=256) { stage=1; First.Set(); if(!ContinueFirst.Wait(5000)) throw new TimeoutException("first search gate"); }
                else if(stage==1 && steps>=512) { stage=2; Second.Set(); if(!ContinueSecond.Wait(5000)) throw new TimeoutException("second search gate"); }
            }
            return 45;
        }
        public override Heightmap.Biome GetBiome(float x,float z,float oceanLevel=.02f,bool waterAlwaysOcean=false)=>Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x,float z,out float weight,out float width) { weight=width=0; }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealPathfinderPublishesBatchesWhileDestinationCountsStayUnchanged(bool toNetwork)
    {
        var world=new SearchWorld(); using var scope=new ValheimWorldScope(); scope.WithWorld(world);
        RoadSpatialGrid.Clear(); int limit=RoadPathfinder.MaxIterations; RoadPathfinder.MaxIterations=1024;
        var owner=new RoadIslandActivity(1,1); var token=owner.Claim(4,3);
        world.Steps=()=>owner.Snapshot()[0].SearchSteps;
        var finder=new RoadPathfinder(world) { Activity=token };
        var search=Task.Run(()=>toNetwork ? finder.FindPathToNetwork(new Vector2(0,0),8) : finder.FindPath(new Vector2(0,0),new Vector2(20000,20000),1024));
        try
        {
            Assert.True(world.First.Wait(3000)); var first=owner.Snapshot()[0];
            Assert.False(search.IsCompleted); Assert.True(first.SearchSteps>=256); Assert.Equal(1,first.ActiveSearches);
            world.ContinueFirst.Set(); Assert.True(world.Second.Wait(3000)); var second=owner.Snapshot()[0];
            Assert.False(search.IsCompleted); Assert.True(second.SearchSteps>first.SearchSteps);
            Assert.Equal(0,second.Processed); Assert.Equal(0,second.Reached);
        }
        finally
        {
            world.ContinueFirst.Set(); world.ContinueSecond.Set();
            try { await search; } finally { RoadPathfinder.MaxIterations=limit; }
            world.First.Dispose(); world.Second.Dispose(); world.ContinueFirst.Dispose(); world.ContinueSecond.Dispose();
            RoadSpatialGrid.Clear();
        }
        Assert.Equal(0,owner.Snapshot()[0].ActiveSearches);
        Assert.Equal(1024,owner.Snapshot()[0].SearchSteps);
    }
    [Fact]
    public void RealCommitAccountingMatchesSharedPathsAndExcludesLevelledPieces()
    {
        using var scope=new ValheimWorldScope(); var world=new SyntheticWorld { HasRiver=false, HasMountain=false }; scope.WithWorld(world);
        RoadSpatialGrid.Clear(); var owner=new RoadIslandActivity(1,1); var token=owner.Claim(1,2);
        var previous=RoadNetworkGenerator.IslandActivity; RoadNetworkGenerator.IslandActivity=token;
        try
        {
            var plan=RoadSpatialGrid.PlanRoadPath(new System.Collections.Generic.List<Vector2> { new(-100,-100),new(100,-100) },4,world);
            Assert.NotNull(plan); RoadSpatialGrid.Commit(plan!); RoadSpatialGrid.Commit(plan!);
            float length=RoadSpatialGrid.TotalRoadLength;
            Assert.Null(RoadSpatialGrid.PlanRoadPath(new System.Collections.Generic.List<Vector2> { new Vector2(0,0) },4,world));
            Assert.Equal(length,RoadSpatialGrid.TotalRoadLength);
            RoadSpatialGrid.CommitLevelled(new[]{new Vector2(0,0),new Vector2(8,0)},new[]{45f,45f},4);
            Assert.Equal(length,RoadSpatialGrid.TotalRoadLength);
            Assert.Equal(length/1000d,owner.Snapshot()[0].Kilometres,5);
            Assert.Equal(plan!.TotalLength*2,length,3);
        }
        finally { RoadNetworkGenerator.IslandActivity=previous; RoadSpatialGrid.Clear(); }
    }
    [Fact]
    public void ActiveProjectionHidesWaitingAndFinishedAndOrdersLogicalSlots()
    {
        var owner=new RoadIslandActivity(3,4);
        var first=owner.Claim(9,2); var second=owner.Claim(2,1); first.Finish();
        var progress=new RoadGenerationProgress(1,"Generating",1,4,2,islands:owner.Snapshot());
        Assert.Equal(new[]{2},progress.ActiveIslands.Select(row=>row.Island));
        var reused=owner.Claim(7,1);
        var next=new RoadGenerationProgress(1,"Generating",1,4,3,islands:owner.Snapshot());
        Assert.Equal(new[]{7,2},next.ActiveIslands.Select(row=>row.Island));
        Assert.Equal(new[]{2},progress.ActiveIslands.Select(row=>row.Island));
        reused.Finish(); second.Finish();
        Assert.Empty(new RoadGenerationProgress(1,"Generating",4,4,4,islands:owner.Snapshot()).ActiveIslands);
    }
    [Fact]
    public void RunTotalUsesCommittedAuthorityAcrossFinishedAndReusedSlots()
    {
        using var scope=new ValheimWorldScope(); var world=new SyntheticWorld { HasRiver=false, HasMountain=false }; scope.WithWorld(world);
        RoadSpatialGrid.Clear(); var owner=new RoadIslandActivity(1,2); var first=owner.Claim(1,2);
        var previous=RoadNetworkGenerator.IslandActivity;
        try
        {
            var plan=RoadSpatialGrid.PlanRoadPath(new System.Collections.Generic.List<Vector2> { new(-100,-100),new(100,-100) },4,world)!;
            RoadNetworkGenerator.IslandActivity=first; RoadSpatialGrid.Commit(plan);
            first.Finish(); first.Finish();
            var finished=new RoadGenerationProgress(1,"Generating",1,2,1,islands:owner.Snapshot(),totalKilometres:RoadSpatialGrid.SnapshotTotalKilometres());
            Assert.Empty(finished.ActiveIslands); Assert.Equal(plan.TotalLength/1000d,finished.TotalKilometres!.Value,6);
            var second=owner.Claim(2,1); first.Committed(999999); first.Finish();
            RoadNetworkGenerator.IslandActivity=second; RoadSpatialGrid.Commit(plan);
            var reused=new RoadGenerationProgress(1,"Generating",1,2,2,islands:owner.Snapshot(),totalKilometres:RoadSpatialGrid.SnapshotTotalKilometres());
            Assert.Equal(plan.TotalLength/1000d,reused.ActiveIslands.Single().Kilometres,6);
            Assert.Equal(plan.TotalLength*2/1000d,reused.TotalKilometres!.Value,6);
            Assert.Equal(plan.TotalLength/1000d,finished.TotalKilometres!.Value,6);
            second.Finish(); Assert.Equal(reused.TotalKilometres,RoadSpatialGrid.SnapshotTotalKilometres());
            RoadSpatialGrid.Clear(); Assert.Equal(0,RoadSpatialGrid.SnapshotTotalKilometres());
        }
        finally { RoadNetworkGenerator.IslandActivity=previous; RoadSpatialGrid.Clear(); }
    }
    [Theory]
    [InlineData(8,0,0)]
    [InlineData(8,2,2)]
    [InlineData(2,8,2)]
    [InlineData(12,20,12)]
    public void SlotsAreBoundedByWorkersAndIslands(int workers,int islands,int expected)
    {
        Assert.Equal(expected,new RoadIslandActivity(workers,islands).Snapshot().Count);
    }
    [Fact]
    public void ReuseRejectsOldAssignmentAndClosedRunCannotPublish()
    {
        var owner=new RoadIslandActivity(1,3);
        var first=owner.Claim(1,2);
        first.Settled(0); first.Settled(0); first.Committed(120);
        var before=owner.Snapshot();
        first.Finish();
        var second=owner.Claim(3,1);
        first.Settled(1); first.Committed(999); first.Finish(); first.Search(256,1);
        second.Settled(0); second.Reached(1); second.Committed(80);
        var after=owner.Snapshot().Single();
        Assert.True(after.Active); Assert.Equal(3,after.Island); Assert.Equal(2,after.Assignment);
        Assert.Equal(1,after.Processed); Assert.Equal(.08,after.Kilometres,6); Assert.Equal(0,after.SearchSteps);
        Assert.Equal(1,before[0].Processed); Assert.Equal(.12,before[0].Kilometres,6);
        owner.Close(); second.Search(256,1); second.Reached(99);
        Assert.Equal(1,owner.Snapshot()[0].Reached);
        var next=new RoadIslandActivity(1,1); next.Claim(1,1);
        second.Committed(999); Assert.Equal(0,next.Snapshot()[0].Kilometres);
    }
    [Fact]
    public void NestedPricingAggregatesBatchesAndFlushesRemainder()
    {
        var owner=new RoadIslandActivity(1,1); var token=owner.Claim(1,3);
        token.Pricing(4);
        Parallel.For(0,4,i => { using(var search=new RoadSearchActivity(token)) for(int n=0;n<300;n++) search.Step(); token.Priced(); });
        var row=owner.Snapshot().Single();
        Assert.Equal(1200,row.SearchSteps); Assert.Equal(0,row.ActiveSearches);
        Assert.Equal(4,row.PricesCompleted); Assert.Equal(4,row.PricesTotal); Assert.Equal(0,row.Processed);
    }
    [Fact]
    public void SearchPublishesNonterminalActivityWithoutSettlingDestinations()
    {
        var owner=new RoadIslandActivity(1,1); var token=owner.Claim(2,4);
        using(var search=new RoadSearchActivity(token))
        {
            for(int n=0;n<256;n++) search.Step();
            var first=owner.Snapshot()[0];
            for(int n=0;n<257;n++) search.Step();
            var second=owner.Snapshot()[0];
            Assert.Equal(256,first.SearchSteps); Assert.Equal(512,second.SearchSteps);
            Assert.Equal(1,second.ActiveSearches); Assert.Equal(0,second.Processed); Assert.Equal(0,second.Reached);
        }
        Assert.Equal(513,owner.Snapshot()[0].SearchSteps); Assert.Equal(0,owner.Snapshot()[0].ActiveSearches);
    }
    [Fact]
    public void PlannerRetriesDoNotSettleUntilDestinationIsExhausted()
    {
        var places=new[] { new RoadConnectionPlanner.Place("Start",Vector3.zero,0),
            new RoadConnectionPlanner.Place("Same",new Vector3(100,0,0),10),
            new RoadConnectionPlanner.Place("Same",new Vector3(200,0,0),10) };
        var owner=new RoadIslandActivity(1,1); var token=owner.Claim(1,2);
        int retries=0,settles=0;
        RoadConnectionPlanner.Connect(places,(_,__)=>1,(_,__)=>false,_=>false,_=>1,
            (_,text)=> { if(text.Contains("trying")) { retries++; Assert.True(settles<2); } },
            index=> { token.Settled(index-1); settles++; });
        Assert.True(retries>1); Assert.Equal(2,settles); Assert.Equal(2,owner.Snapshot()[0].Processed);
        Assert.Equal(0,owner.Snapshot()[0].Reached);
    }
    [Fact]
    public void UnpricedPartnerSettlesEachIndexOnce()
    {
        var places=new[] { new RoadConnectionPlanner.Place("Start",Vector3.zero,0),
            new RoadConnectionPlanner.Place("Same",new Vector3(100,0,0),10),
            new RoadConnectionPlanner.Place("Same",new Vector3(200,0,0),10) };
        var settled=new int[3];
        RoadConnectionPlanner.Connect(places,(_,__)=>null,(a,b)=>a.Name!="Start",_=>false,_=>1,(_,__)=>{},i=>settled[i]++);
        Assert.Equal(new[]{0,1,1},settled);
    }

    [Fact]
    public void ClaimThroughSettleReachCommitAndBridgeCountsReachTheSnapshot()
    {
        var owner=new RoadIslandActivity(1,1); var token=owner.Claim(5,4);
        token.Connecting("Crypt4",340); token.Committed(1500); token.Committed(500); token.BuiltStaircases(3); token.BuiltStaircases(0); token.BuiltBridges(2); token.BuiltBridges(0);
        token.Settled(0); token.Settled(1,false); token.Settled(1,false); token.Settled(2,true); token.Reached(2);
        var row=owner.Snapshot()[0];
        Assert.Equal(3,row.Processed); Assert.Equal(1,row.Unreached); Assert.Equal(2,row.Reached);
        Assert.Equal(2.0,row.Kilometres,6); Assert.Equal(3,row.Staircases); Assert.Equal(2,row.Bridges);
        Assert.Equal("Crypt4",row.Destination); Assert.Equal(340,row.DestinationMetres);
        Assert.True(row.IslandSeconds>=0);
    }

    [Fact]
    public void IslandSecondsSpanStageChangesAndStopAtFinish()
    {
        var owner=new RoadIslandActivity(1,1); var token=owner.Claim(1,2);
        Thread.Sleep(30); token.Stage("Connecting");
        double atStage=owner.Snapshot()[0].IslandSeconds;
        Assert.True(atStage>=.02,"the clock must not restart at a stage change");
        token.Finish();
        double done=owner.Snapshot()[0].IslandSeconds; Thread.Sleep(30);
        Assert.Equal(done,owner.Snapshot()[0].IslandSeconds);
    }

    [Fact]
    public void ANewClaimStartsWithoutTheOldIslandsCounts()
    {
        var owner=new RoadIslandActivity(1,2); var first=owner.Claim(1,2);
        first.BuiltBridges(3); first.Committed(10); first.BuiltStaircases(4); first.Settled(0,false); first.Connecting("A",1); first.Finish();
        owner.Claim(2,1); var row=owner.Snapshot()[0];
        Assert.Equal(0,row.Bridges); Assert.Equal(0,row.Staircases); Assert.Equal(0,row.Unreached); Assert.Null(row.Destination);
        first.BuiltBridges(9); Assert.Equal(0,owner.Snapshot()[0].Bridges);
    }

    [Fact]
    public void APlaceALaterRoadServesLeavesUnreached()
    {
        var owner=new RoadIslandActivity(1,1); var token=owner.Claim(3,1);
        token.Settled(0,false); token.Settled(1,true);
        Assert.Equal(1,owner.Snapshot()[0].Unreached);
        token.Measured(new[]{true,true,false});
        var row=owner.Snapshot()[0];
        Assert.Equal(0,row.Unreached); Assert.Equal(2,row.Reached);
        token.Measured(new[]{false,true,true});
        row=owner.Snapshot()[0];
        Assert.Equal(1,row.Unreached); Assert.Equal(2,row.Reached);
    }

    [Fact]
    public void AStageOtherThanConnectingForgetsThePlace()
    {
        var owner=new RoadIslandActivity(1,1); var token=owner.Claim(1,1);
        token.Stage("Connecting"); token.Connecting("Crypt4",340);
        Assert.Equal("Crypt4",owner.Snapshot()[0].Destination);
        token.Stage("Fitting");
        Assert.Null(owner.Snapshot()[0].Destination);
    }

    [Fact]
    public void SettlingSecondsStartWhenConnectingStartsNotAtTheClaim()
    {
        var owner=new RoadIslandActivity(1,1); var token=owner.Claim(1,2);
        token.Pricing(4); Thread.Sleep(40);
        Assert.Equal(0,owner.Snapshot()[0].SettlingSeconds);
        token.Connecting("A",10);
        var row=owner.Snapshot()[0];
        Assert.True(row.SettlingSeconds<row.IslandSeconds-.03,"pricing time must not count toward the estimate");
    }
}
