using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
using Xunit.Abstractions;

namespace ProceduralRoads.Tests;

public sealed class ConnectedLandingStairTests : IDisposable
{
    private readonly ValheimWorldScope _scope=new();
    private readonly ITestOutputHelper _output;
    public ConnectedLandingStairTests(ITestOutputHelper output) { _output = output; RoadNetworkGenerator.Reset(); RoadLocationProtection.Reset(); }
    public void Dispose() { RoadNetworkGenerator.Reset(); RoadLocationProtection.Reset(); _scope.Dispose(); }
    private static PlacedLocationShape Shape(CapturedLandingFile f) => PlacedLocationShape.Of(f.Record.Centre,f.Record.Yaw,RoadStairRuns.FlatOf(f.Record.Ops)!.Value,
        f.Record.RockRadius,f.Record.Rocks,f.Ground,f.Record.KeepSmallRocks,f.Record.Root,f.Record.Prefab);

    /// <summary>A floor top at a raised landing whose ground rises past its far edge: no finish is tried on it (it is the floor
    /// the rule lays), and it arrives where its own far corners stand on the landing within a step (StairEnds.Lands, the one
    /// reader): the ground rising past them is the next floor's, read by none.</summary>
    [Theory]
    [InlineData(0f, true)]
    [InlineData(1f, true)]
    public void ExistingFloorMustNotAcceptHigherGroundAfterFinishFallthrough(float rise, bool expectedArrival)
    {
        float Ground(float x, float z) => 40f + rise * Mathf.Clamp((z - 1f) * 2f, 0f, 1f);
        var shape = PlacedLocationShape.Of(new Vector2(0,-6), 0, new LevelOp(0,0,0,4,false), 0, null, Ground, root:40);
        var region = shape.ConnectedLanding(Ground,1f);
        Assert.True(region.Contains(new Vector2(0,1)), "Real connected region must reach the floor exit");
        var plan = new StairPlan { Kit="stone", Reached=true, EndHeading=0, End=new Vector3(0,40,1),
            LastRowKind=StairPieceKind.Landing, LastRowStart=new Vector2(0,-1), LastRowHeading=0, LastRowHeight=40,
            LastRowPieces=0, LastRowPieceCount=1, LastRowClearances=0, LastRowClearanceCount=0, Landings=1 };
        plan.Pieces.Add(new StairPiece { Kind=StairPieceKind.Landing, Prefab="stone_floor_2x2", Position=new Vector3(0,39.5f,0) });
        Assert.All(PlacedLandingStairTests.Walk(plan), s=>Assert.True(s.height>=Ground(s.at.x,s.at.y)));
        var before=plan.Copy(deepPieces:true);
        int calls=0;
        var finish=RoadStairRuns.FinishOverLedge(plan,Ground,2,p=> {
            calls++;
            var validity=RoadStairRuns.RaisedLandingWalk(p,region);
            _output.WriteLine($"trial {calls}: clear={validity.clear}, arrival={validity.arrival}");
            return validity.clear && validity.arrival;
        });
        var final=RoadStairRuns.RaisedLandingWalk(plan,region);
        _output.WriteLine($"rise={rise}, finish={finish}, calls={calls}, final={final}, forward-ground={Ground(0,1.5f)}");
        Assert.Equal(StairLayout.TopFinish.None,finish);
        Assert.Equal(0,calls);
        Assert.Equal(before.Pieces.Select(p=>(p.Prefab,p.Position,p.YawDegrees)),plan.Pieces.Select(p=>(p.Prefab,p.Position,p.YawDegrees)));
        Assert.Equal(before.End,plan.End);
        Assert.Equal(before.Clearances,plan.Clearances);Assert.Equal(before.Cuts,plan.Cuts);
        Assert.Equal((before.LastRowKind,before.LastRowStart,before.LastRowHeading,before.LastRowHeight,before.Landings),
            (plan.LastRowKind,plan.LastRowStart,plan.LastRowHeading,plan.LastRowHeight,plan.Landings));
        Assert.True(final.clear);
        if (rise>0) Assert.True(Ground(0,1.5f)-40f>StairLayout.TopFloorBand);
        Assert.Equal(expectedArrival,final.arrival);
    }

    public static IEnumerable<object[]> FlatExitCases()
    {
        foreach(float onward in new[]{0f,90f,202.5f,337.5f})
            foreach(int lanes in new[]{1,2})
                foreach(float rise in new[]{0f,0.45f,1f})
                    yield return new object[]{onward,lanes,rise};
    }

    [Theory]
    [MemberData(nameof(FlatExitCases))]
    public void ExistingAndAddedFloorsAgreeAtTheSamePhysicalExit(float onward,int lanes,float rise)
    {
        Vector2 dir=StairLayout.Forward(onward),across=StairLayout.Right(onward);
        float Ground(float x,float z)=>40f+rise*Mathf.Clamp((x*dir.x+z*dir.y-1f)*2f,0f,1f);
        var shape=PlacedLocationShape.Of(-dir*6f,onward,new LevelOp(0,0,0,4,false),0,null,Ground,root:40);
        var region=shape.ConnectedLanding(Ground,1f);
        Assert.True(region.Contains(dir));
        var existing=new StairPlan { Kit="stone",Reached=true,EndHeading=onward,End=new Vector3(dir.x,40,dir.y) };
        for(int lane=0;lane<lanes;lane++)
        {
            Vector2 centre=across*((lane-(lanes-1)/2f)*2f);
            existing.Pieces.Add(new StairPiece { Kind=StairPieceKind.Landing,Prefab="stone_floor_2x2",
                Position=new Vector3(centre.x,39.5f,centre.y),YawDegrees=onward });
        }
        Assert.All(PlacedLandingStairTests.Walk(existing),s=>Assert.True(s.height>=Ground(s.at.x,s.at.y)));
        var prospective=new StairPlan { Kit="stone",Reached=true,EndHeading=onward,End=new Vector3(-dir.x,40,-dir.y),LastRowKind=StairPieceKind.Stair };
        // Both read at the same far corners (StairEnds.Corners, the one reader): the floor laid and the floor added there
        // stand on ground level with them, the rise past them read by neither.
        bool expected=true;
        if(rise==0.45f)Assert.InRange(rise,StairLayout.StepUp+0.01f,StairLayout.TopFloorBand-0.01f);
        var validity=RoadStairRuns.RaisedLandingWalk(existing,region);
        var added=StairLayout.AddTopFloor(prospective,Ground,lanes*2f);
        _output.WriteLine($"onward={onward}, lanes={lanes}, rise={rise}, existing={validity}, added={added.pieces}");
        Assert.Equal((true,expected),validity);
        Assert.Equal(expected?lanes:0,added.pieces);
        if(expected)
        {
            Assert.Equal((true,true),RoadStairRuns.RaisedLandingWalk(prospective,region));
            var exit=StairLayout.Terminal(prospective)!;
            Assert.InRange(Vector2.Distance(new Vector2(exit.Edge.x,exit.Edge.z),dir),0f,0.01f);
        }
    }

    /// <summary>A floor's exit read at its far corners (StairEnds.Corners): the ground rising at its sides past them read by
    /// none, the ground falling at its sides under them, more than a step, read as a stair down's (the rule's reverse), not a
    /// floor's. The falls are in steps (StairLayout.StepUp), so the rule holds whatever StepUp is.</summary>
    [Theory]
    [InlineData(0f,1,4.5f,true)]
    [InlineData(90f,2,4.5f,true)]
    [InlineData(202.5f,2,4.5f,true)]
    [InlineData(337.5f,1,4.5f,true)]
    [InlineData(0f,1,-2f,false)]
    [InlineData(90f,2,-2f,false)]
    [InlineData(202.5f,2,-2f,false)]
    [InlineData(337.5f,1,-2f,false)]
    public void FlatExitsAreReadAtTheFloorsFarCorners(float onward,int lanes,float sideSteps,bool expected)
    {
        Vector2 dir=StairLayout.Forward(onward),across=StairLayout.Right(onward);
        float sideRise=sideSteps*StairLayout.StepUp,start=sideRise<0f?0.5f:1f;
        float Ground(float x,float z)=>40f+sideRise*Mathf.Clamp((x*dir.x+z*dir.y-start)*2f,0f,1f)*Mathf.Clamp(Mathf.Abs(x*across.x+z*across.y),0f,1f);
        var shape=PlacedLocationShape.Of(-dir*6f,onward,new LevelOp(0,0,0,4,false),0,null,Ground,root:40);
        var region=shape.ConnectedLanding(Ground,1f);
        var plan=new StairPlan { Kit="stone",Reached=true,EndHeading=onward,End=new Vector3(-dir.x,40,-dir.y),LastRowKind=StairPieceKind.Stair };
        var existing=plan.Copy();
        for(int lane=0;lane<lanes;lane++)
        {
            Vector2 centre=across*((lane-(lanes-1)/2f)*2f);
            existing.Pieces.Add(new StairPiece { Kind=StairPieceKind.Landing,Prefab="stone_floor_2x2",Position=new Vector3(centre.x,39.5f,centre.y),YawDegrees=onward });
        }
        Assert.All(PlacedLandingStairTests.Walk(existing),s=>Assert.True(s.height>=Ground(s.at.x,s.at.y)));
        Assert.Equal((true,expected),RoadStairRuns.RaisedLandingWalk(existing,region));
        // A top floor is laid where the rule reads a floor at its far corners: the same corners.
        Assert.Equal(expected?lanes:0,StairLayout.AddTopFloor(plan,Ground,lanes*2f).pieces);
    }

    [Fact]
    public void ExistingFloorSamplesItsOwnFarHalfRatherThanAnotherFloorAhead()
    {
        float Ground(float x,float z)=>40f+Mathf.Clamp((z-1.75f)*2f,0f,1f);
        var shape=PlacedLocationShape.Of(new Vector2(0,-6),0,new LevelOp(0,0,0,4,false),0,null,Ground,root:40);
        var region=shape.ConnectedLanding(Ground,1f);
        var plan=new StairPlan { Kit="stone",Reached=true,EndHeading=0,End=new Vector3(0,40,-1),LastRowKind=StairPieceKind.Stair };
        Assert.Equal(1,StairLayout.AddTopFloor(plan,Ground,2).pieces);
        plan.End=new Vector3(0,40,1); // Search metadata points to this emitted floor's far edge.
        Assert.Equal((true,true),RoadStairRuns.RaisedLandingWalk(plan,region));
    }

    [Fact]
    public void ActualTowerRequestReachesItsConnectedLandingWithoutTheBuriedFloor()
    {
        var f=CapturedLandingFile.Tower();var r=f.Record;var shape=Shape(f);
        Assert.Equal("2c4cd8bbdf0c9e03c0de8ecffeaf7d8b9d85305a40db3e07c67c512f8547e77d",f.InputHash);
        // The native capture omitted the global footprint index: the tower's footprint as TowerPlacedFirstTests sets it.
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(r.Centre, 30f) });
        // A staircase from the captured road's end, set off from its ring (RoadEndRing), or where none is laid there, from where
        // the road is cut back for it (RoadNetworkGenerator.EntryCutBacks), over the captured ground carried on past its edge,
        // where a staircase from there may search. Before the ring every one from the road's end crossed it.
        float Carried(float x,float z)=>f.Ground(Mathf.Clamp(x,r.X0,r.X0+r.W-1.001f),Mathf.Clamp(z,r.Z0,r.Z0+r.H-1.001f));
        RoadStairSpan? Lay(LocationApproachRequest q,Func<float,float,float> ground,out string refused)=>RoadStairRuns.PlanPlacedLocationApproach(q,r.Yaw,r.Ops,TestLattice.Of(ground),(_,_)=>Heightmap.Biome.Mountain,f.Seed,out refused,
            r.RockRadius,inFootprint:RoadLocationProtection.Contains,rocks:r.Rocks,keepSmallRocks:r.KeepSmallRocks,root:r.Root);
        var span=Lay(f.Request,f.Ground,out string why);
        var from=f.Request;
        if(span==null){from=f.CutBack(RoadNetworkGenerator.EntryCutBacks[1]);span=Lay(from,Carried,out why);}
        Assert.True(span!=null,why);var plan=span!.Plan;
        Assert.True(plan.Reached);Assert.NotEmpty(plan.Pieces);
        var ring=new RoadEndRing(from.RoadEnd,from.Arriving,from.RoadHeight);
        Assert.False(ring.Intrudes(plan,out string inside),inside);
        // Its top: from the road's end, a stair laid back down onto the landing; from the cut, it climbs onto it (its last stair
        // climbing on the plan's end heading), meeting it as RaisedLandingWalk asks either way.
        var terminal=plan.Pieces.Last(p=>p.Kind!=StairPieceKind.Support);
        Assert.Equal(StairPieceKind.Stair,terminal.Kind);
        var region=shape.ConnectedLanding(f.Ground,1f);
        Assert.Equal((true,true),RoadStairRuns.RaisedLandingWalk(plan,region));
        var aims=shape.Aims(f.Ground,f.Request.RoadEnd,1f,RoadStairRuns.AimTries,region);
        Assert.Equal(new Vector2(-4797,1739),aims[0].at);
        Assert.True(Vector2.Distance(aims[0].at,shape.FlatCentre)>shape.Edge);
        Assert.True(region.Contains(aims[0].at));
        // Reuse the existing independent closed prefab-surface oracle, not production WalkingSurface/Cuts.
        var walked=PlacedLandingStairTests.Walk(plan).Where(s=>plan.Pieces.IndexOf(s.piece)>=plan.SunkPieces).ToArray();
        foreach(var s in walked.Where(s=>s.piece!=terminal && Carried(s.at.x,s.at.y)>=r.Root-0.01f && Carried(s.at.x,s.at.y)>f.Natural(Mathf.Clamp(s.at.x,r.X0,r.X0+r.W-1.001f),Mathf.Clamp(s.at.y,r.Z0,r.Z0+r.H-1.001f))+0.01f))
            Assert.True(s.height>=Carried(s.at.x,s.at.y)-0.01f,$"buried actual raised surface at {s.at}: {s.height}");
        var ramp=walked.Where(s=>s.piece==terminal).ToArray();
        float high=ramp.Max(s=>s.height);
        Assert.All(ramp.Where(s=>s.height>=high-0.01f),s=>Assert.True(s.height>=Carried(s.at.x,s.at.y)-0.01f));
    }

    /// <summary>The tower's staircase (StoneTowerRuins05 at -4801,1727): its landing is level out to about
    /// 10 m from the centre along the staircase's line, but the staircase climbed on over it and ended 5.5 m from the
    /// centre, its top stair between the tower's own walls. It ends at the lip, where the level ground meets the slope: its
    /// top's exit edge and every walked piece within a piece of it, but its finish within its own depth more, and nothing
    /// walked in the tower.</summary>
    [Fact]
    public void ActualTowerStaircaseEndsAtTheLandingsLip()
    {
        var f=CapturedLandingFile.Tower();var shape=Shape(f);
        // From where the game's staircase set off, its road arriving from the south-east: laid before, it climbed on over
        // the landing to 8.0 m from the centre, its top 5.1 m inside the lip.
        var plan=TowerApproach(f,shape,315f,out string why);
        Assert.True(plan!=null,why);
        var region=shape.ConnectedLanding(f.Ground,1f);
        var exit=StairLayout.Terminal(plan!)!;
        Vector2 edge=new Vector2(exit.Edge.x,exit.Edge.z);
        _output.WriteLine(FormattableString.Invariant($"exit edge ({edge.x:F2}, {edge.y:F2}): {region.Depth(edge):F2} m in, {Vector2.Distance(edge,f.Record.Centre):F2} from the centre"));
        Assert.True(region.Near(edge,StairLayout.SurfaceSpacing),"the top does not reach the landing");
        // The finish laid off its top (a floor, or the rule's stair down, which stands where that floor would) may stand on
        // past the lip by its own depth (RoadStairRuns.PastTheLip): laid so, the crest's stair down ends 3.1 m in.
        var finishing=exit.Pieces[0].Kind==StairPieceKind.Landing||exit.Descending?exit.Pieces:new List<StairPiece>();
        float Reach(bool last)=>StairLayout.PieceRun+(last&&finishing.Count>0?StairLayout.PieceRun:0f);
        Assert.InRange(region.Depth(edge),0f,Reach(true));
        foreach(var p in plan!.Pieces.Where(p=>p.Kind!=StairPieceKind.Support))
            Assert.True(region.Depth(new Vector2(p.Position.x,p.Position.z))<=Reach(finishing.Contains(p)),$"{p.Kind} at {p.Position} stands on the landing past its lip");
        AssertInTheTower(plan!,false);
    }

    /// <summary>Nor in the tower's own pieces, which the location's shape does not know (it keeps out of a location's pit, the
    /// rock round it but for its whole front side, and its boulders): the game's staircase's top, read in game, stood
    /// between the tower's walls, 5.2 to 6.0 m from its centre, and nothing refused it. Past the lip, it is.</summary>
    [Fact]
    public void TheGamesStaircaseTopInTheTowerIsPastTheLandingsLip()
    {
        var f=CapturedLandingFile.Tower();var region=Shape(f).ConnectedLanding(f.Ground,1f);
        var plan=new StairPlan { Kit="stone",Reached=true,EndHeading=135f,End=new Vector3(-4796.93f,186.77f,1729.96f) };
        plan.Pieces.Add(new StairPiece { Kind=StairPieceKind.Stair,Prefab="stone_stair",Position=new Vector3(-4799.21f,185.77f,1732.68f),YawDegrees=315f });
        plan.Pieces.Add(new StairPiece { Kind=StairPieceKind.Landing,Prefab="stone_floor_2x2",Position=new Vector3(-4797.80f,186.27f,1731.26f),YawDegrees=315f });
        plan.Pieces.Add(new StairPiece { Kind=StairPieceKind.Landing,Prefab="stone_floor_2x2",Position=new Vector3(-4796.93f,186.27f,1729.96f),YawDegrees=337.5f });
        foreach(var p in plan.Pieces)
            Assert.True(region.Depth(new Vector2(p.Position.x,p.Position.z))>StairLayout.PieceRun,$"{p.Kind} at {p.Position}: {region.Depth(new Vector2(p.Position.x,p.Position.z)):F2} m in");
        Assert.True(RoadStairRuns.PastTheLip(plan,region.Depth));
        AssertInTheTower(plan,true);
    }

    /// <summary>The tower's staircase from where the game's set off (-4790.98,1751.43), its road arriving on <paramref name="arriving"/>.</summary>
    private static StairPlan? TowerApproach(CapturedLandingFile f,PlacedLocationShape shape,float arriving,out string why)
    {
        var r=f.Record;Vector2 end=new Vector2(-4790.98f,1751.43f);
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(r.Centre, 30f) });
        var request=new LocationApproachRequest { Centre=r.Centre,RoadEnd=end,RoadHeight=f.Ground(end.x,end.y),Arriving=arriving,Reach=f.Request.Reach,
            TotalExpansions=f.Request.TotalExpansions,Stall=f.Request.Stall,Leg=f.Request.Leg,RoadTail=new List<Vector2>{end-StairLayout.Forward(arriving)*30f,end} };
        return RoadStairRuns.PlanPlacedLocationApproach(request,r.Yaw,r.Ops,TestLattice.Of(f.Ground),(_,_)=>Heightmap.Biome.Mountain,f.Seed,out why,
            r.RockRadius,inFootprint:RoadLocationProtection.Contains,rocks:r.Rocks,keepSmallRocks:r.KeepSmallRocks,root:r.Root,placed:shape)?.Plan;
    }

    /// <summary>The tower's own pieces as the game placed them, read beside its staircase (stone_wall_2x1 courses along
    /// z 1733.95 and 1729.95 from x -4801.6 to -4798.6, a wall along x -4798.12, stone_floor_2x2 inside to z 1727.95), and
    /// half a metre round them: whether any walked surface is there.</summary>
    private static void AssertInTheTower(StairPlan plan,bool expected)
    {
        var inside=PlacedLandingStairTests.Walk(plan).Where(s=>s.at.x>=-4802.6f-0.5f && s.at.x<=-4797.6f+0.5f && s.at.y>=1727.95f-0.5f && s.at.y<=1733.95f+0.5f).ToList();
        Assert.True(expected==(inside.Count>0),inside.Count==0?"nothing walked in the tower":$"{inside[0].piece.Kind} walked at {inside[0].at} in the tower");
    }

    [Fact]
    public void RejectedNativeFloorHasClearanceButNoForwardArrival()
    {
        var f=CapturedLandingFile.Tower();var region=Shape(f).ConnectedLanding(f.Ground,1f);
        var plan=new StairPlan { Kit="stone",Reached=true,EndHeading=202.5f,End=new Vector3(-4803.04248f,186.810776f,1737.78943f) };
        plan.Pieces.Add(new StairPiece { Kind=StairPieceKind.Landing,Prefab="stone_floor_2x2",Position=new Vector3(-4802.66f,186.310776f,1738.71338f),YawDegrees=202.5f });
        var walked=PlacedLandingStairTests.Walk(plan).ToArray();
        // Its walked surface clear of the ground, nearest 0.24 m over it (0.30 m when the captured ground was joined bilinearly
        // between its vertices; now it is joined as the game's mesh joins them, two triangles a cell).
        Assert.InRange(walked.Min(s=>s.height-f.Ground(s.at.x,s.at.y)),0.23f,0.25f);
        // Independent observed forward edge and just beyond it are more than a step below the floor.
        Assert.InRange(186.810776f-f.Ground(-4803.04248f,1737.78943f),0.45f,0.47f);
        Assert.InRange(186.810776f-f.Ground(-4803.13815f,1737.55846f),0.44f,0.46f);
        Assert.Equal((true,false),RoadStairRuns.RaisedLandingWalk(plan,region));
    }

    [Fact]
    public void RetainedNativeBaselineFloorIsBuriedAtItsIndependentInteriorVertex()
    {
        var f=CapturedLandingFile.Tower();var region=Shape(f).ConnectedLanding(f.Ground,1f);
        var floor=new StairPiece { Kind=StairPieceKind.Landing,Prefab="stone_floor_2x2",Position=new Vector3(-4800.35547f,185.310776f,1739.276f),YawDegrees=202.5f };
        // An independent geometric check verified (-4800,1739) is inside this fixed rotated 2x2, not an edge/tolerance case.
        float top=floor.Position.y+0.5f;
        Assert.InRange(f.Ground(-4800,1739)-top,0.49f,0.50f);
        Assert.False(RoadStairRuns.RaisedLandingSurfaceAllowed(new Vector3(-4800,top,1739),region));
        var plan=new StairPlan { Kit="stone",Reached=true };plan.Pieces.Add(floor);
        Assert.False(RoadStairRuns.RaisedLandingWalk(plan,region).clear);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(90f)]
    [InlineData(202.5f)]
    [InlineData(337.5f)]
    public void ArrivalUsesTheForwardExitRatherThanASideOrBackContact(float onward)
    {
        // In steps (StairLayout.StepUp; the numbers at 0.2): the landing rising behind the floor to three quarters of a step
        // (0.15 m), the floor first a step and a half over the landing ahead (0.3 m), then level with its rise (0.15 m).
        float step=StairLayout.StepUp,rise=0.75f*step,high=40f+1.5f*step,level=40f+rise;
        Vector2 dir=StairLayout.Forward(onward);
        float Terrain(float x,float z)=>40f+Mathf.Clamp(-(x*dir.x+z*dir.y),0f,rise);
        var shape=PlacedLocationShape.Of(new Vector2(),0,new LevelOp(0,0,0,4,false),0,null,Terrain,root:40);
        var region=shape.ConnectedLanding(Terrain,1f);
        var plan=new StairPlan { Kit="stone",Reached=true,EndHeading=onward };
        // A square floor's prefab can face back while the walker continues onward.
        var floor=new StairPiece { Kind=StairPieceKind.Landing,Prefab="stone_floor_2x2",Position=new Vector3(0,high-StairKit.Stone.FloorTop,0),YawDegrees=StairLayout.Wrap(onward+180f) };
        plan.Pieces.Add(floor);
        var samples=PlacedLandingStairTests.Walk(plan).ToArray();
        Assert.Contains(samples,s=>s.height-Terrain(s.at.x,s.at.y)<StairLayout.StepUp);
        Assert.Equal(1.5f*step,high-Terrain(dir.x*1.5f,dir.y*1.5f),3);
        Assert.Equal((true,false),RoadStairRuns.RaisedLandingWalk(plan,region));
        floor.Position=new Vector3(0,level-StairKit.Stone.FloorTop,0);
        Assert.Equal((true,true),RoadStairRuns.RaisedLandingWalk(plan,region));
    }

    [Fact]
    public void AReverseMayOverlapTheLandingOnlyWithItsHighConnectionClear()
    {
        var region=Extension().ConnectedLanding(Extended,1f);
        var plan=new StairPlan { Kit="stone",Reached=true,EndHeading=0 };
        var stair=new StairPiece { Kind=StairPieceKind.Stair,Prefab="stone_stair",Position=new Vector3(0,39.5f,12),YawDegrees=0 };
        plan.Pieces.Add(stair);
        Assert.All(PlacedLandingStairTests.Walk(plan).Where(s=>s.height>=40.49f),s=>Assert.True(region.Contains(s.at)));
        Assert.Equal((true,true),RoadStairRuns.RaisedLandingWalk(plan,region));
        stair.Position=new Vector3(0,38.5f,12);
        Assert.False(RoadStairRuns.RaisedLandingWalk(plan,region).clear);
        stair.Position=new Vector3(0,39.5f,12);stair.YawDegrees=180;
        Assert.False(RoadStairRuns.RaisedLandingWalk(plan,region).clear);
    }

    [Theory]
    [InlineData(0f,1)]
    [InlineData(90f,2)]
    [InlineData(202.5f,2)]
    public void TheTerminalFrameIncludesEveryLaneAndIgnoresSupportTails(float onward,int lanes)
    {
        Vector2 dir=StairLayout.Forward(onward),across=StairLayout.Right(onward);
        var plan=new StairPlan { Kit="stone",EndHeading=onward,End=new Vector3(99,99,99) };
        for(int n=0;n<lanes;n++)
        {
            var centre=across*((n-(lanes-1)/2f)*2f);
            plan.Pieces.Add(new StairPiece { Kind=StairPieceKind.Landing,Prefab="stone_floor_2x2",Position=new Vector3(centre.x,39.5f,centre.y),YawDegrees=onward });
            plan.Pieces.Add(new StairPiece { Kind=StairPieceKind.Support,Position=new Vector3(999,999,999) });
        }
        var exit=StairLayout.Terminal(plan)!;
        Assert.Equal(lanes,exit.Pieces.Count);Assert.InRange(exit.Width,lanes*2f-0.01f,lanes*2f+0.01f);
        Assert.InRange(exit.Depth,1.99f,2.01f);
        Assert.InRange(Vector2.Distance(new Vector2(exit.Edge.x,exit.Edge.z),dir),0f,0.01f);Assert.Equal(40f,exit.Edge.y,3);
        // A genuinely terminal filler is walked geometry too, and must not be silently skipped.
        plan.Pieces.Add(new StairPiece { Kind=StairPieceKind.Filler,Prefab="stone_wall_1x1",Position=new Vector3(0,39.5f,0),YawDegrees=onward });
        exit=StairLayout.Terminal(plan)!;
        Assert.Equal(StairPieceKind.Filler,Assert.Single(exit.Pieces).Kind);
        Assert.InRange(exit.Depth,0.99f,1.01f);
        Assert.InRange(Vector2.Distance(new Vector2(exit.Edge.x,exit.Edge.z),dir*0.5f),0f,0.01f);
    }

    // Independent long flat with an offset extension; a separate same-height island is not connected.
    private static float Extended(float x,float z)
    {
        if((new Vector2(x-14f,z)).magnitude<=2f)return 40f;
        float outside=Mathf.Max(Mathf.Abs(x)-4f,Mathf.Max(-4f-z,z-14f));
        return 40f-Mathf.Clamp(outside,0f,10f);
    }
    private static readonly LevelOp[] Ops={new(0,0,0,4,false),new(0,8,0,6,false)};
    private static PlacedLocationShape Extension()=>PlacedLocationShape.Of(new Vector2(),0,Ops[0],0,null,Extended,root:40);

    [Fact]
    public void TheOffsetExtensionIsConnectedButTheSeparateIslandIsNot()
    {
        var region=Extension().ConnectedLanding(Extended,1f);
        Assert.True(region.Contains(new Vector2(0,12)));Assert.False(region.Contains(new Vector2(14,0)));
        var detached=new StairPlan { Kit="stone",Reached=true };
        detached.Pieces.Add(new StairPiece { Kind=StairPieceKind.Landing,Prefab="stone_floor_2x2",Position=new Vector3(14,39.5f,0) });
        Assert.False(RoadStairRuns.RaisedLandingWalk(detached,region).arrival);
    }

    [Fact]
    public void ContinuousMembershipAndContactUseTheSameClosedCells()
    {
        var region=Extension().ConnectedLanding(Extended,1f);
        Assert.True(region.Contains(new Vector2(4.5f,12f)));
        Assert.True(region.Contains(new Vector2(4.5f,14.5f)));
        Assert.False(region.Contains(new Vector2(4.51f,12f)));
        Assert.True(region.Near(new Vector2(4.75f,12f),0.25f));
        Assert.False(region.Near(new Vector2(4.76f,12f),0.25f));
    }

    [Fact]
    public void AnIndependentOffsetLandingHasAReachableOutsideApproach()
    {
        var end=new Vector2(0,26);
        var request=new LocationApproachRequest { Centre=new Vector2(),RoadEnd=end,RoadHeight=Extended(end.x,end.y),Arriving=180,
            RoadTail=new List<Vector2> { new Vector2(0,56),end } };
        var span=RoadStairRuns.PlanPlacedLocationApproach(request,0,Ops,TestLattice.Of(Extended),(_,_)=>Heightmap.Biome.Mountain,77,out string why,root:40);
        Assert.True(span!=null,why);
        Assert.NotEmpty(span!.Plan.Pieces);
        Assert.Equal((true,true),RoadStairRuns.RaisedLandingWalk(span.Plan,Extension().ConnectedLanding(Extended,1f)));
        Assert.Contains(PlacedLandingStairTests.Walk(span.Plan),s=>s.at.y>8f && s.height>=40f && s.height-Extended(s.at.x,s.at.y)<=StairLayout.StepUp);
    }

    [Theory]
    [InlineData(StairPieceKind.Stair,"stone_stair")]
    [InlineData(StairPieceKind.Filler,"stone_wall_1x1")]
    [InlineData(StairPieceKind.Landing,"stone_floor_2x2")]
    public void BuriedWalkingPiecesOutsideTheAnchorDiscAreRejectedWithoutCuts(StairPieceKind kind,string prefab)
    {
        var plan=new StairPlan { Kit="stone",Reached=true };
        plan.Pieces.Add(new StairPiece { Kind=kind,Prefab=prefab,Position=new Vector3(0,38.5f,12) });
        Assert.Empty(plan.Cuts);Assert.False(RoadStairRuns.RaisedLandingWalk(plan,Extension().ConnectedLanding(Extended,1f)).clear);
    }

    [Fact]
    public void EarlierContactCannotExcuseATerminalPieceShortOfTheLanding()
    {
        var plan=new StairPlan { Kit="stone",Reached=true };
        plan.Pieces.Add(new StairPiece { Kind=StairPieceKind.Landing,Prefab="stone_floor_2x2",Position=new Vector3(0,39.5f,12) });
        plan.Pieces.Add(new StairPiece { Kind=StairPieceKind.Landing,Prefab="stone_floor_2x2",Position=new Vector3(0,39.5f,18) });
        Assert.False(RoadStairRuns.RaisedLandingWalk(plan,Extension().ConnectedLanding(Extended,1f)).arrival);
    }

    [Fact]
    public void AReversedTerminalBuriedHighEdgeCannotEmbedItsLowEnd()
    {
        var plan=new StairPlan { Kit="stone",Reached=true,EndHeading=0 };
        plan.Pieces.Add(new StairPiece { Kind=StairPieceKind.Stair,Prefab="stone_stair",Position=new Vector3(0,38.5f,12),YawDegrees=0 });
        Assert.False(RoadStairRuns.RaisedLandingWalk(plan,Extension().ConnectedLanding(Extended,1f)).clear);
    }
}
