using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public class ManualRoadTests : IDisposable
{
    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0; width = 0; }
    }
    private readonly WorldGenerator world = new FlatWorld();
    public ManualRoadTests()
    {
        RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetDebugCounters();
        WorldGenerator.instance = world; ZDOMan.instance = new ZDOMan();
        ZoneSystem.instance = new ZoneSystem(); RoadNetworkGenerator.MarkLocationsReady();
        Heightmap.Registered = null; RoadGrade.Configured = .35f;
        RoadNetworkGenerator.RoadWidth = 4;
        RoadSiteProtection.Set(Array.Empty<RoadSiteProtection.Footprint>());
    }
    public void Dispose()
    {
        RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetDebugCounters();
        WorldGenerator.instance = null; ZDOMan.instance = null; ZoneSystem.instance = null; Heightmap.Registered = null;
    }
    private static Island Group(int xmin = -64, int xmax = 64)
    {
        var i = new Island { Id = 1, CellSize = 8, WorldOffset = 0,
            Min = new Vector2(xmin * 8, -512), Max = new Vector2(xmax * 8, 512) };
        for (int x = xmin; x <= xmax; x++) for (int z = -64; z <= 64; z++) i.Cells.Add(new Vector2Int(x, z));
        return i;
    }
    private RoadSpatialGrid.PlannedPath Line(float z, float start = -24, float end = 24)
    {
        var plan = RoadSpatialGrid.PlanRoadPath(new List<Vector2> { new(start, z), new(end, z) }, 4, world);
        Assert.NotNull(plan); return plan!;
    }
    private int BaseRoad(float z = -16)
    {
        RoadSpatialGrid.Commit(Line(z)); RoadSpatialGrid.FinalizeRoadNetwork();
        RoadNetworkGenerator.MarkRoadsLoadedFromZDO(); return RoadSpatialGrid.RoadNetworkVersion;
    }
    private void Add(float z) => RoadSpatialGrid.CommitAppend(new[] { Line(z) }, RoadSpatialGrid.RoadNetworkVersion);

    [Fact] public void CoordinatesUseInvariantXZAndCanBeCopiedBack()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var d = new ManualRoadDraft(); d.Add(ManualRoadDraft.ParsePoint("-120.5", "450.25")); d.Add(new Vector2(0, -80));
            Assert.Equal("road_path -120.5,450.25 0,-80", d.Command);
            Assert.Equal(d.Points, ManualRoadDraft.ParsePath(d.Command.Split(' ').Skip(1)));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }
    [Theory]
    [InlineData("NaN", "0")][InlineData("Infinity", "2")][InlineData("10000", "0")][InlineData("2,3", "4")][InlineData("a", "b")]
    public void InvalidCoordinatesAreRefused(string x, string z) => Assert.Throws<ArgumentException>(() => ManualRoadDraft.ParsePoint(x,z));
    [Fact] public void DraftLimitsDuplicatesAndClearAreLocal()
    {
        var a = ManualRoads.Draft("a"); var b = ManualRoads.Draft("b");
        a.Add(new Vector2(10,20)); b.Add(new Vector2(50,60));
        Assert.Throws<ArgumentException>(() => a.Add(new Vector2(10.1f,20)));
        Assert.True(a.Undo()); Assert.False(a.Undo());
        for (int i=0;i<32;i++) a.Add(new Vector2(i*2,0));
        Assert.Throws<ArgumentException>(()=>a.Add(new Vector2(100,0)));
        a.Clear(); Assert.Single(b.Points);
        ManualRoads.Reset(); Assert.Empty(ManualRoads.Draft("b").Points);
    }
    [Theory]
    [InlineData("1,2")][InlineData("1,2 1,2")][InlineData("1,2,3 4,5")]
    public void BadPathsFailBeforePlanning(string path) => Assert.Throws<ArgumentException>(()=>ManualRoadDraft.ParsePath(path.Split(' ')));

    [Fact] public void AppendKeepsOldRecordsAndPersistsHistoryInTheSameBlob()
    {
        int original = BaseRoad(); var before = RoadSpatialGrid.GetRoadPointsInZone(new Vector2s(0,0)).Where(p=>p.addition==0).ToArray();
        Add(16); int appended = RoadSpatialGrid.RoadNetworkVersion;
        var bytes = RoadSpatialGrid.SerializeAllRoadPoints()!;
        Assert.Equal(3, BitConverter.ToInt32(bytes,0));
        RoadSpatialGrid.Clear(); Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(bytes));
        Assert.Equal(appended,RoadSpatialGrid.RoadNetworkVersion);
        var all = RoadSpatialGrid.GetRoadPointsInZone(new Vector2s(0,0));
        Assert.Equal(before,all.Where(p=>p.addition==0));
        Assert.All(RoadSpatialGrid.PendingPoints(all,original),p=>Assert.Equal(1,p.addition));
        Assert.NotEmpty(RoadSpatialGrid.PendingPoints(all,original));
        Assert.Empty(RoadSpatialGrid.PendingPoints(all,appended));
    }
    [Fact] public void TwoOfflineAppendsOnlyApplyWhatEachCompilerMissed()
    {
        int a=BaseRoad(); Add(0); int b=RoadSpatialGrid.RoadNetworkVersion; Add(16);
        var all=RoadSpatialGrid.GetRoadPointsInZone(new Vector2s(0,0));
        Assert.Equal(new[]{1,2},RoadSpatialGrid.PendingPoints(all,a).Select(p=>p.addition).Distinct().OrderBy(x=>x));
        Assert.All(RoadSpatialGrid.PendingPoints(all,b),p=>Assert.Equal(2,p.addition));
        Assert.Equal(all,RoadSpatialGrid.PendingPoints(all,0));
        Assert.Equal(all,RoadSpatialGrid.PendingPoints(all,123456));
    }
    [Fact] public void OrdinaryNetworksStillWriteTheExistingFormat()
    {
        BaseRoad(); var before=RoadSpatialGrid.SerializeAllRoadPoints()!;
        Assert.Equal(2,BitConverter.ToInt32(before,0));
        RoadSpatialGrid.Clear(); Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(before));
        Assert.Equal(before,RoadSpatialGrid.SerializeAllRoadPoints());
    }
    [Fact] public void TruncatedAppendHistoryCannotReplaceTheCurrentNetwork()
    {
        BaseRoad(); Add(16); var before=RoadSpatialGrid.SerializeAllRoadPoints()!;
        Assert.False(RoadSpatialGrid.DeserializeAllRoadPoints(before.Take(before.Length-1).ToArray()));
        Assert.Equal(before,RoadSpatialGrid.SerializeAllRoadPoints());
    }
    [Fact] public void UnchangedZoneIsNotRewrittenAfterAnAppendElsewhere()
    {
        BaseRoad(); var hm=Heightmap.CreateForZone(new Vector2s(0,0)); Heightmap.Registered=hm;
        RoadTerrainModifier.OnTerrainCompilerReady(hm.m_terrainComp!);
        int saves=hm.m_terrainComp!.SaveCount;
        Add(200); RoadTerrainModifier.OnTerrainCompilerReady(hm.m_terrainComp);
        Assert.Equal(saves,hm.m_terrainComp.SaveCount); Assert.True(RoadTerrainModifier.CarriesCurrentRoads(hm.m_terrainComp));
    }
    [Fact] public void SharedZoneKeepsPlayerEditsOnOldRoadAndOffTheAdditionAfterReload()
    {
        int original=BaseRoad(); var zone=new Vector2s(0,0);
        var hm=Heightmap.CreateForZone(zone); Heightmap.Registered=hm; var tc=hm.m_terrainComp!;
        RoadTerrainModifier.OnTerrainCompilerReady(tc);
        int oldRoad=16*65+32, untouched=32*65+32, newRoad=48*65+32;
        tc.m_levelDelta[oldRoad]=3.25f; tc.m_smoothDelta[oldRoad]=.75f;
        tc.m_levelDelta[untouched]=-2.5f; tc.m_smoothDelta[untouched]=.5f;
        tc.m_levelDelta[newRoad]=-3f;
        Add(16); var bytes=RoadSpatialGrid.SerializeAllRoadPoints()!;
        RoadSpatialGrid.Clear(); Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(bytes));
        RoadTerrainModifier.OnTerrainCompilerReady(tc);
        Assert.Equal(3.25f,tc.m_levelDelta[oldRoad]); Assert.Equal(.75f,tc.m_smoothDelta[oldRoad]);
        Assert.Equal(-2.5f,tc.m_levelDelta[untouched]); Assert.Equal(.5f,tc.m_smoothDelta[untouched]);
        Assert.Equal(0,tc.m_levelDelta[newRoad],3);
        int saves=tc.SaveCount; RoadTerrainModifier.OnTerrainCompilerReady(tc); Assert.Equal(saves,tc.SaveCount);
        Assert.True(RoadTerrainModifier.CarriesCurrentRoads(tc));
    }
    [Fact] public void BlendBoundaryIncludesTheNeighbouringZone()
    {
        int old=BaseRoad(); Add(32); // Zone edge, road influences both sides.
        Assert.NotEmpty(RoadSpatialGrid.PendingPoints(RoadSpatialGrid.GetRoadPointsInZone(new Vector2s(0,0)),old));
        Assert.NotEmpty(RoadSpatialGrid.PendingPoints(RoadSpatialGrid.GetRoadPointsInZone(new Vector2s(0,1)),old));
    }
    [Fact] public void StalePlanCannotPublishAndDraftSurvives()
    {
        BaseRoad(); var draft=ManualRoads.Draft("host");draft.Add(new Vector2(0,40));draft.Add(new Vector2(80,40));
        var plan=ManualRoads.Prepare(draft.Points,false,new[]{Group()}); Add(16);
        var before=RoadSpatialGrid.SerializeAllRoadPoints();
        Assert.Throws<InvalidOperationException>(()=>ManualRoads.Commit(plan));
        Assert.Equal(before,RoadSpatialGrid.SerializeAllRoadPoints()); Assert.Equal(2,draft.Points.Count); Assert.False(ManualRoads.Busy);
    }
    [Fact] public void WorldUnloadInvalidatesAPlanEvenIfTheNetworkHashIsTheSame()
    {
        BaseRoad(); var plan=ManualRoads.Prepare(new[]{new Vector2(0,40),new Vector2(80,40)},false,new[]{Group()});
        ManualRoads.Reset(); Assert.Throws<InvalidOperationException>(()=>ManualRoads.Commit(plan));
    }
    [Fact] public void OrderedWaypointsArePlannedAsOneContinuousProfile()
    {
        var points=new[]{new Vector2(-80,0),new Vector2(0,40),new Vector2(80,0)};
        var plan=ManualRoads.Prepare(points,false,new[]{Group()});
        Assert.Single(plan.Paths); Assert.False(RoadSpatialGrid.IsInitialized);
        foreach(var p in points) Assert.Contains(plan.Paths[0].Points,q=>Vector2.Distance(p,q)<8);
        Assert.True(RoadGrade.SteepestStep(plan.Paths[0].Points,plan.Paths[0].Heights)<=.351f);
        ManualRoads.Commit(plan);
        Assert.True(RoadNetworkGenerator.RoadsGenerated); Assert.Equal(1,RoadSpatialGrid.AppendCount);
        RoadNetworkGenerator.SaveGlobalRoadData(); var bytes=RoadSpatialGrid.SerializeAllRoadPoints();
        RoadNetworkGenerator.Reset(); Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData()); Assert.Equal(bytes,RoadSpatialGrid.SerializeAllRoadPoints());
    }
    [Fact] public void ProtectedWaypointIsRefusedWithoutExemptingItsOwnFootprint()
    {
        RoadSiteProtection.Set(new[]{new RoadSiteProtection.Footprint(new Vector2(0,0),20)});
        Assert.Throws<ArgumentException>(()=>ManualRoads.Prepare(new[]{new Vector2(0,0),new Vector2(80,0)},false,new[]{Group()}));
        Assert.False(RoadSpatialGrid.IsInitialized); Assert.False(ManualRoads.Busy);
    }
    [Fact] public void ConnectRequiresAnExistingNetworkOnTheSourceIsland()
    {
        BaseRoad();
        Assert.Throws<InvalidOperationException>(()=>ManualRoads.Prepare(new[]{new Vector2(160,0)},true,new[]{Group(16,32)}));
    }
    [Fact] public void ConnectionUsesTheSourceIslandEvenIfAnotherRoadIsCloser()
    {
        BaseRoad(); Add(80);
        Assert.True(RoadSpatialGrid.TryGetRoadWithin(new Vector2(0,60),100,out var near,p=>p.y<0));
        Assert.True(near.y<0);
        var finder=new RoadPathfinder(world);
        var path=finder.FindPathToNetwork(new Vector2(0,60),8,new[]{new Vector2(0,-16)},p=>p.y<0);
        Assert.NotNull(path); Assert.True(path![path.Count-1].y<0);
    }
    [Fact] public void AlreadyConnectedIsANoop()
    {
        BaseRoad(); var before=RoadSpatialGrid.SerializeAllRoadPoints();
        var plan=ManualRoads.Prepare(new[]{new Vector2(0,-16)},true,new[]{Group()});
        Assert.True(plan.AlreadyConnected); ManualRoads.Commit(plan); Assert.Equal(before,RoadSpatialGrid.SerializeAllRoadPoints());
    }
    [Fact] public void RealConnectPlansAndAppendsAJoin()
    {
        BaseRoad();var plan=ManualRoads.Prepare(new[]{new Vector2(80,80)},true,new[]{Group()});
        Assert.False(plan.AlreadyConnected);Assert.NotEmpty(plan.Paths);ManualRoads.Commit(plan);Assert.Equal(1,RoadSpatialGrid.AppendCount);
    }
    [Fact] public void AnEmittedPieceIsNotRecreatedIfItIsDestroyedBeforeTheBatchCompletes()
    {
        var zone = new Vector2s(0,0);
        var emitted = new BridgePiece { Prefab="wood_floor", Position=new Vector3(0,40,0) };
        var waiting = new BridgePiece { Prefab="wood_floor", Position=new Vector3(2,40,0) };
        BridgeAppendQueue.Enqueue(new Dictionary<Vector2s,List<BridgePiece>> { { zone,new(){emitted,waiting} } });
        BridgeAppendQueue.Acknowledge(zone,BridgeAppendQueue.Key(emitted));
        var saved=BridgeAppendQueue.Serialize(); BridgeAppendQueue.Reset(); BridgeAppendQueue.Load(saved);
        Assert.Single(BridgeAppendQueue.ForZone(zone)!);
        Assert.Equal(BridgeAppendQueue.Key(waiting),BridgeAppendQueue.Key(BridgeAppendQueue.ForZone(zone)![0]));
    }

    [Fact] public void PendingBridgeQueueSurvivesReloadAndDeduplicatesRepeatedEnqueue()
    {
        var zone=new Vector2s(0,0);var piece=new BridgePiece {Prefab="wood_floor",Position=new Vector3(0,40,0),RollDegrees=15,HealthFraction=.5f};
        var additions=new Dictionary<Vector2s,List<BridgePiece>>{{zone,new(){piece}}};
        BridgeAppendQueue.Enqueue(additions);BridgeAppendQueue.Enqueue(additions);Assert.Single(BridgeAppendQueue.ForZone(zone)!);
        var data=BridgeAppendQueue.Serialize();BridgeAppendQueue.Reset();BridgeAppendQueue.Load(data);
        Assert.Equal(BridgeAppendQueue.Key(piece),BridgeAppendQueue.Key(BridgeAppendQueue.ForZone(zone)![0]));
        Assert.Throws<EndOfStreamException>(()=>BridgeAppendQueue.Load(data.Take(data.Length-1).ToArray()));
        Assert.Equal(data,BridgeAppendQueue.Serialize());
        BridgeAppendQueue.Complete(zone);Assert.True(BridgePlans.IsSpawned(zone));Assert.Empty(BridgeAppendQueue.Zones);
    }
}

public class ManualRoadFailureTests : IDisposable
{
    private sealed class Cliff : WorldGenerator
    {
        public override float GetHeight(float x,float z)=>x<64 ? 40 : 180;
        public override void GetRiverWeight(float x,float z,out float weight,out float width){weight=0;width=0;}
    }
    public ManualRoadFailureTests()
    {
        RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetDebugCounters();
        WorldGenerator.instance=new Cliff(); ZDOMan.instance=new ZDOMan();ZoneSystem.instance=new ZoneSystem();
        RoadNetworkGenerator.MarkLocationsReady();RoadGrade.Configured=.35f;RoadNetworkGenerator.RoadWidth=4;
        RoadSiteProtection.Set(Array.Empty<RoadSiteProtection.Footprint>());
    }
    public void Dispose()
    {
        RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetDebugCounters();WorldGenerator.instance=null;
        ZDOMan.instance=null;ZoneSystem.instance=null;Heightmap.Registered=null;
    }
    private static Island Island()
    {
        var i=new Island {Id=1,Min=new Vector2(-1024,-1024),Max=new Vector2(1024,1024),CellSize=8,WorldOffset=0};
        for(int x=-128;x<=128;x++)for(int z=-128;z<=128;z++)i.Cells.Add(new Vector2Int(x,z));return i;
    }
    [Fact] public void AFailingSecondLegPublishesNeitherTheFirstLegNorBridgeMetadata()
    {
        var d=ManualRoads.Draft("host");d.Add(new Vector2(-80,0));d.Add(new Vector2(-16,0));d.Add(new Vector2(160,0));
        var error=Assert.Throws<InvalidOperationException>(()=>ManualRoads.Prepare(d.Points,false,new[]{Island()}));
        Assert.Contains("Leg 2",error.Message);Assert.False(RoadSpatialGrid.IsInitialized);
        Assert.Empty(RoadNetworkGenerator.GetRoadCrossings());Assert.Empty(BridgeAppendQueue.Zones);Assert.Equal(3,d.Points.Count);Assert.False(ManualRoads.Busy);
    }
    [Fact] public void OwnershipDefersTheAdditionUntilTheCompilerCanBeWritten()
    {
        var world=WorldGenerator.instance!;
        RoadSpatialGrid.AddRoadPath(new List<Vector2>{new(-24,-16),new(24,-16)},4,world);RoadSpatialGrid.FinalizeRoadNetwork();
        var hm=Heightmap.CreateForZone(new Vector2s(0,0));Heightmap.Registered=hm;var tc=hm.m_terrainComp!;
        RoadTerrainModifier.OnTerrainCompilerReady(tc);int saves=tc.SaveCount;
        var plan=RoadSpatialGrid.PlanRoadPath(new List<Vector2>{new(-24,16),new(24,16)},4,world)!;
        RoadSpatialGrid.CommitAppend(new[]{plan},RoadSpatialGrid.RoadNetworkVersion);
        tc.m_nview.GetZDO().SetOwner(9999);RoadTerrainModifier.ApplyAdditionsToLoadedZones();Assert.Equal(saves,tc.SaveCount);
        tc.m_nview.ClaimOwnership();RoadTerrainModifier.ApplyAdditionsToLoadedZones();Assert.Equal(saves+1,tc.SaveCount);
    }
    [Fact] public void ADeferredRebuildCoalescesTwoAdditionsWithoutForgettingEither()
    {
        var world=WorldGenerator.instance!;var zone=new Vector2s(0,0);
        RoadSpatialGrid.AddRoadPath(new List<Vector2>{new(-24,-16),new(24,-16)},4,world);RoadSpatialGrid.FinalizeRoadNetwork();
        var hm=Heightmap.CreateForZone(zone);Heightmap.Registered=hm;var tc=hm.m_terrainComp!;
        RoadTerrainModifier.OnTerrainCompilerReady(tc);hm.AutoRebuild=false;
        foreach(float z in new[]{0f,16f})
        {
            var plan=RoadSpatialGrid.PlanRoadPath(new List<Vector2>{new(-24,z),new(24,z)},4,world)!;
            RoadSpatialGrid.CommitAppend(new[]{plan},RoadSpatialGrid.RoadNetworkVersion);
            RoadTerrainModifier.OnTerrainCompilerReady(tc);
        }
        hm.RebuildTerrain();Assert.True(tc.m_modifiedHeight[32*65+32]);Assert.True(tc.m_modifiedHeight[48*65+32]);Assert.True(RoadTerrainModifier.CarriesCurrentRoads(tc));
    }
    [Fact] public void SnapshotValidationDoesNotDestroyTheOldNetworkAndValidSnapshotKeepsHistory()
    {
        var plan=ManualRoads.Prepare(new[]{new Vector2(-80,0),new Vector2(-16,0)},false,new[]{Island()});ManualRoads.Commit(plan);
        byte[] grid=RoadSpatialGrid.SerializeAllRoadPoints()!;
        byte[] starts=RoadNetworkPersistence.SerializeRoadStartPoints(RoadNetworkGenerator.GetRoadStartPoints());
        byte[] crossings=RoadNetworkPersistence.SerializeRoadCrossings(RoadNetworkGenerator.GetRoadCrossings());
        Assert.Throws<InvalidOperationException>(()=>RoadNetworkGenerator.AcceptManualSnapshot(grid,starts.Take(1).ToArray(),crossings));
        Assert.Equal(grid,RoadSpatialGrid.SerializeAllRoadPoints());
        RoadNetworkGenerator.Reset();RoadNetworkGenerator.AcceptManualSnapshot(grid,starts,crossings);
        Assert.Equal(grid,RoadSpatialGrid.SerializeAllRoadPoints());Assert.True(RoadNetworkGenerator.RoadsAvailable);Assert.Single(RoadNetworkGenerator.GetRoadStartPoints());
    }
}

public class ManualBridgeTests : IDisposable
{
    public ManualBridgeTests()
    {
        RoadNetworkGenerator.Reset();RoadTerrainModifier.ResetDebugCounters();
        WorldGenerator.instance=new BridgeTests.WideRiverWorld();ZDOMan.instance=new ZDOMan();ZoneSystem.instance=new ZoneSystem();
        RoadNetworkGenerator.MarkLocationsReady();RoadSiteProtection.Set(Array.Empty<RoadSiteProtection.Footprint>());
        RoadGrade.Configured=.35f;RoadNetworkGenerator.RoadWidth=4;
    }
    public void Dispose()
    {
        RoadNetworkGenerator.Reset();WorldGenerator.instance=null;ZoneSystem.instance=null;ZDOMan.instance=null;Heightmap.Registered=null;
    }
    [Fact] public void FullRespawnDiscardsAppendProgressButKeepsTheCompletePlan()
    {
        var island=new Island {Id=1,CellSize=8,WorldOffset=0,Min=new Vector2(-216,-120),Max=new Vector2(216,120)};
        for(int x=-27;x<=27;x++)for(int z=-15;z<=15;z++)island.Cells.Add(new Vector2Int(x,z));
        var plan=ManualRoads.Prepare(new[]{new Vector2(-160,0),new Vector2(160,0)},false,new[]{island});
        ManualRoads.Commit(plan);
        var zone=BridgeAppendQueue.Zones.First(z=>BridgeAppendQueue.ForZone(z)!.Count>1);
        int fullCount=BridgePlans.PlannedPieceCount(zone);
        var first=BridgeAppendQueue.ForZone(zone)![0];
        BridgeAppendQueue.Acknowledge(zone,BridgeAppendQueue.Key(first));
        Assert.True(BridgeAppendQueue.ForZone(zone)!.Count<fullCount);
        // Ordinary retry/reload keeps acknowledgements: do not resurrect damage.
        var pending=BridgeAppendQueue.Serialize(); BridgeAppendQueue.Load(pending);
        Assert.DoesNotContain(BridgeAppendQueue.ForZone(zone)!,p=>BridgeAppendQueue.Key(p)==BridgeAppendQueue.Key(first));
        BridgePlans.MarkSpawned(zone);
        // The real clear/respawn path calls these two methods before spawning.
        BridgePlans.InvalidatePlans(); BridgePlans.ForgetSpawned();
        Assert.Null(BridgeAppendQueue.ForZone(zone));
        Assert.False(BridgePlans.IsSpawned(zone));
        Assert.Equal(fullCount,BridgePlans.PlannedPieceCount(zone));
        // Persisting and restoring the queue cannot reinstate the stale remainder.
        var after=BridgeAppendQueue.Serialize(); BridgeAppendQueue.Load(after);
        Assert.Empty(BridgeAppendQueue.Zones);
    }

    [Fact] public void AManualRiverCrossingQueuesNewPiecesInAnAlreadySpawnedZoneAndKeepsOldObjects()
    {
        var island=new Island {Id=1,CellSize=8,WorldOffset=0,Min=new Vector2(-216,-120),Max=new Vector2(216,120)};
        for(int x=-27;x<=27;x++)for(int z=-15;z<=15;z++)island.Cells.Add(new Vector2Int(x,z));
        var plan=ManualRoads.Prepare(new[]{new Vector2(-160,0),new Vector2(160,0)},false,new[]{island});
        Assert.NotEmpty(plan.NewCrossings);
        var planned=BridgeAppendQueue.Prepare(plan.NewCrossings);Assert.NotEmpty(planned);
        foreach(var zone in planned.Keys)BridgePlans.MarkSpawned(zone);
        var existing=ZDOMan.instance!.CreateNewZDO(new Vector3(0,40,0),"wood_floor".GetStableHashCode());
        existing.Set(BridgePlans.MarkerHash,1);
        int oldCount=ZDOMan.instance.CountWithPrefab("wood_floor");
        ManualRoads.Commit(plan);RoadNetworkGenerator.SaveGlobalRoadData();
        Assert.Equal(oldCount,ZDOMan.instance.CountWithPrefab("wood_floor"));
        Assert.NotEmpty(BridgeAppendQueue.Zones);
        var queue=BridgeAppendQueue.Serialize();
        RoadNetworkGenerator.Reset();Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData());
        Assert.Equal(queue,BridgeAppendQueue.Serialize());Assert.Equal(oldCount,ZDOMan.instance.CountWithPrefab("wood_floor"));
        foreach(var zone in BridgeAppendQueue.Zones)Assert.True(BridgePlans.IsSpawned(zone));
    }
}
