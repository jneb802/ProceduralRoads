using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public class NetworkRoutingTests
{
    private static RoadConnectionPlanner.Place P(string name,float x) => new(name,new Vector3(x,0,0),0);

    [Fact]
    public void FailedProbeStillAttemptsAndReportsTheOrphan()
    {
        var attempted = new List<string>(); var reports = new List<string>();
        RoadConnectionPlanner.Connect(new[] { P("start",0),P("boss",100) },
            (_,__)=>null,(_,__)=>false,
            p=>{attempted.Add(p.Name);return false;},_=>100,(p,r)=>reports.Add(p.Name+":"+r));
        Assert.Equal(new[] { "boss" },attempted);
        Assert.Contains(reports,r=>r.Contains("boss:new component"));
    }

    [Fact]
    public void UnpricedCandidateEdgeIsStillBuiltAtTheBuildBudget()
    {
        // A pricing probe runs at a reduced budget and the open-sea test is a
        // straight line. A place whose only candidate edge lost its price that
        // way must still be offered to the builder, which searches in full.
        var built = new List<string>(); var reports = new List<string>();
        RoadConnectionPlanner.Connect(new[] { P("start",0),P("boss",100) },
            (_,__)=>null,
            (a,b)=>{built.Add(a.Name+"-"+b.Name);return true;},
            _=>false,_=>100,(p,r)=>reports.Add(p.Name+":"+r));
        Assert.Equal(new[] { "start-boss" },built);
        Assert.Contains(reports,r=>r.Contains("boss:joined"));
        Assert.DoesNotContain(reports,r=>r.Contains("new component"));
    }

    [Fact]
    public void UnpricedRetryPrefersTheNearestTreePlaceAndStopsWhenOneSucceeds()
    {
        var built = new List<string>();
        // "near" prices normally and joins the tree; "boss" has no price at all,
        // so its retry must try the nearer tree member first and then stop,
        // never falling back to the further "start".
        RoadConnectionPlanner.Connect(new[] { P("start",0),P("near",100),P("boss",120) },
            (a,b)=> a.Name=="start" && b.Name=="near" ? 1 : (float?)null,
            (a,b)=>{built.Add(a.Name+"-"+b.Name);return true;},
            _=>false,_=>1,(_,__)=>{});
        Assert.Equal(new[] { "start-near","near-boss" },built);
    }

    [Fact]
    public void UnpricedRetryDoesNotRunWhenTheReverseSearchAlreadyJoined()
    {
        int builds=0;
        RoadConnectionPlanner.Connect(new[] { P("start",0),P("boss",100) },
            (_,__)=>null,(_,__)=>{builds++;return true;},_=>true,_=>1,(_,__)=>{});
        Assert.Equal(0,builds);
    }

    [Fact]
    public void FailedBuildCanUseAnotherPricedEdge()
    {
        var attempts = new List<string>();
        RoadConnectionPlanner.Connect(new[] { P("start",0),P("a",100),P("b",200) },
            (a,b)=> a.Name=="start" && b.Name=="a" ? 1 : a.Name=="start" ? 2 : 3,
            (a,b)=>{ attempts.Add(a.Name+"-"+b.Name);return !(a.Name=="start" && b.Name=="b"); },
            _=>false,_=>1,(_,__)=>{});
        Assert.Equal(new[] { "start-a","start-b","a-b" },attempts);
    }

    [Fact]
    public void FailedPricedBuildIsNotReclassifiedAsAnUntriedProbe()
    {
        int builds=0;
        RoadConnectionPlanner.Connect(new[] { P("start",0),P("boss",100) },
            (_,__)=>1,(_,__)=>{builds++;return false;},_=>false,_=>1,(_,__)=>{});
        Assert.Equal(1,builds);
    }

    [Fact]
    public void FailedUnpricedPairIsNotRetriedFromItsOtherEndpoint()
    {
        int builds=0;
        // Start is beyond the candidate radius. Boss and neighbour have only
        // each other to try, and priority visits the boss first.
        RoadConnectionPlanner.Connect(new[] { P("start",4000),P("boss",0),P("neighbour",100) },
            (_,__)=>null,(_,__)=>{builds++;return false;},_=>false,
            name=>name=="boss" ? 100 : 1,(_,__)=>{});
        Assert.Equal(1,builds);
    }

    [Fact]
    public void BuildingToAPendingNeighbourHandlesBothEndpointsOnce()
    {
        var builds=new List<string>(); var reports=new List<string>();
        RoadConnectionPlanner.Connect(new[] { P("start",4000),P("boss",0),P("neighbour",100) },
            (_,__)=>null,(a,b)=>{builds.Add(a.Name+"-"+b.Name);return true;},_=>false,
            name=>name=="boss" ? 100 : 1,(p,r)=>reports.Add(p.Name+":"+r));
        Assert.Equal(new[] { "neighbour-boss" },builds);
        Assert.Equal(2,reports.Count);
        Assert.Single(reports.Where(r=>r.StartsWith("boss:")));
        Assert.Single(reports.Where(r=>r.StartsWith("neighbour:")));
        Assert.DoesNotContain(reports,r=>r.Contains("new component"));
    }

    private sealed class FlatGround : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40;
        public override Heightmap.Biome GetBiome(float x, float z) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        { weight = 0; width = 0; }
    }

    /// <summary>FindPathToNetwork returns null both when it fails and when the
    /// start was already within reach. The caller checks the exact point but
    /// the search checks the cell centre, up to 5.66 m away, so a destination
    /// the network already passes could be read as unreachable and given a
    /// road of its own.</summary>
    [Fact]
    public void AlreadyOnTheNetworkIsNotReportedAsAFailedSearch()
    {
        RoadSpatialGrid.Clear();
        try
        {
            var world = new FlatGround();
            var finder = new RoadPathfinder(world);
            RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(0, 0), new(8, 0), new(16, 0) }, 4, world);
            RoadSpatialGrid.FinalizeRoadNetwork();

            var path = finder.FindPathToNetwork(new Vector2(0, 0), 8);
            Assert.Null(path);
            Assert.True(finder.AlreadyOnNetwork, "a start already within reach is not a failure");

            var away = finder.FindPathToNetwork(new Vector2(4000, 4000), 8);
            Assert.False(finder.AlreadyOnNetwork, "a real search must not claim it was already connected");
            Assert.Null(away);
        }
        finally { RoadSpatialGrid.Clear(); }
    }

    [Fact]
    public void SuccessfulBranchDoesNotAlsoBuildThePricedPair()
    {
        int pairs=0;
        RoadConnectionPlanner.Connect(new[] { P("start",0),P("a",100) },
            (_,__)=>1,(_,__)=>{pairs++;return true;},_=>true,_=>1,(_,__)=>{});
        Assert.Equal(0,pairs);
    }

    [Fact]
    public void LongUnpricedPairIsStillReported()
    {
        int probes=0; int reports=0;
        RoadConnectionPlanner.Connect(new[] { P("start",0),P("far",5000) },
            (_,__)=>{probes++;return 1;},(_,__)=>false,_=>false,_=>1,(_,__)=>reports++);
        Assert.Equal(0,probes);
        Assert.Equal(1,reports);
    }

    private sealed class Flat : WorldGenerator
    {
        public override float GetHeight(float x,float z)=>40;
        public override void GetRiverWeight(float x,float z,out float weight,out float width)
        {weight=0;width=0;}
    }

    [Fact]
    public void ReverseSearchEndsOnAnActualRoadPoint()
    {
        RoadSpatialGrid.Clear();
        try
        {
            var world=new Flat();
            RoadSpatialGrid.AddRoadPath(new() {new Vector2(-80,0),new Vector2(80,0)},4,world);
            var path=new RoadPathfinder(world).FindPathToNetwork(new Vector2(0,100),8,
                new[] {new Vector2(-80,0),new Vector2(80,0)});
            Assert.NotNull(path);
            var end=path![path.Count-1];
            Assert.True(RoadSpatialGrid.TryGetRoadWithin(end,0.01f,out _));
            Assert.InRange(Math.Abs(end.y),0,0.01f);
        }
        finally {RoadSpatialGrid.Clear();}
    }

    [Fact]
    public void Vector3BuilderProvidesSamplesToTheNextReverseBranch()
    {
        var world=WorldGenerator.instance;
        RoadNetworkGenerator.Reset();
        try
        {
            WorldGenerator.instance=new Flat();
            typeof(RoadNetworkGenerator).GetField("m_pathfinder",
                System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!
                .SetValue(null,new RoadPathfinder(WorldGenerator.instance));
            var hints=new List<Vector2>();
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(0,40,0),0,
                new Vector3(80,40,0),0,4,"fixture",hints));
            Assert.NotEmpty(hints);
            Assert.All(hints,p=>Assert.InRange(System.Math.Abs(p.y),0,0.01f));
        }
        finally { RoadNetworkGenerator.Reset(); WorldGenerator.instance=world; }
    }

    [Fact]
    public void ProbeBudgetDoesNotMutateBuildBudget()
    {
        int original=RoadPathfinder.MaxIterations;
        var finder=new RoadPathfinder(new Flat());
        Assert.Null(finder.FindPath(new Vector2(0,0),new Vector2(800,0),iterationLimit:1));
        Assert.Equal(original,RoadPathfinder.MaxIterations);
        Assert.NotNull(finder.FindPath(new Vector2(0,0),new Vector2(80,0)));
        Assert.True(finder.LastPathCost>0);
    }

}
