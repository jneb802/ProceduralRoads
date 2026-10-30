using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Threading;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

public sealed class GenerationProgressTests : IDisposable
{
    // The real generator sees two disjoint islands. Only worker terrain reads are gated; preparation is real and completes.
    private sealed class Islands : WorldGenerator
    {
        internal readonly ManualResetEventSlim Entered = new(false), Release = new(false), RightRelease = new(false), PricingEntered=new(false), PricingRelease=new(false);
        internal Func<bool>? PricingGate;
        internal readonly int MainThread = Thread.CurrentThread.ManagedThreadId;
        internal bool Gate, Fail, Reverse, HoldCancellation;
        internal int ActiveReads, MainReads;
        public override float GetHeight(float x, float z)
        {
            if(Thread.CurrentThread.ManagedThreadId==MainThread) MainReads++;
            if(Thread.CurrentThread.ManagedThreadId!=MainThread && PricingGate?.Invoke()==true)
            {
                PricingEntered.Set();
                while(!PricingRelease.Wait(10)) RoadLifecycleManager.ThrowIfCancellationRequested();
            }
            if (Gate && Thread.CurrentThread.ManagedThreadId != MainThread)
            {
                Interlocked.Increment(ref ActiveReads);
                try
                {
                    Entered.Set();
                    var gate = Reverse && x > 0 ? RightRelease : Release;
                    while (!gate.Wait(10)) if(!HoldCancellation) RoadLifecycleManager.ThrowIfCancellationRequested();
                    if (Fail) throw new InvalidOperationException("controlled terrain failure");
                }
                finally { Interlocked.Decrement(ref ActiveReads); }
            }
            return Math.Abs(z) < 400 && (Math.Abs(x + 1800) < 400 || Math.Abs(x - 1800) < 400) ? 45f : -50f;
        }
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            Math.Abs(z) < 400 && (Math.Abs(x + 1800) < 400 || Math.Abs(x - 1800) < 400) ? Heightmap.Biome.Meadows : Heightmap.Biome.Ocean;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = width = 0; }
    }
    private sealed class FaultingPrime : IEnumerable<bool>, IEnumerator<bool>
    {
        internal int Disposals;
        public bool Current=>true;
        object System.Collections.IEnumerator.Current=>Current;
        public IEnumerator<bool> GetEnumerator()=>this;
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();
        public bool MoveNext()=>throw new InvalidOperationException("primary preparation failure");
        public void Reset()=>throw new NotSupportedException();
        public void Dispose() { Disposals++; throw new InvalidOperationException("secondary cleanup failure"); }
    }
    private readonly ValheimWorldScope scope = new();
    private readonly Islands world = new();
    private readonly RoadNetworkOptions oldOptions = RoadNetworkGenerator.NetworkOptions;
    private readonly int oldPercentage = RoadNetworkGenerator.IslandRoadPercentage, oldWorkers = RoadParallel.Configured;
    public GenerationProgressTests()
    {
        scope.WithWorld(world).WithZdos().WithZoneSystem().WithNetwork(server: true);
        RoadNetworkGenerator.Reset();
        RoadNetworkGenerator.GenerateOnLoad = true;
        RoadNetworkGenerator.NetworkOptions = new RoadNetworkOptions();
        RoadNetworkGenerator.IslandRoadPercentage = 100;
        RoadParallel.Configured = 2;
        foreach (var place in new[] { ("StartTemple", -1800f, 0f), ("Crypt4", -1600f, 150f), ("Eikthyrnir", 1750f, 0f), ("Crypt4", 1950f, 150f) })
            TestLocations.Add(ZoneSystem.instance!, new ZoneSystem.LocationInstance
            { m_position = new Vector3(place.Item2, 45, place.Item3), m_location = new ZoneSystem.ZoneLocation
                { m_prefab = new SoftReferenceableAssets.SoftReference<GameObject>(place.Item1), m_exteriorRadius = 12 } });
        RoadLifecycleManager.OnZoneSystemStart(ZoneSystem.instance!);
    }
    private static void Begin()
    {
        ZoneSystem.instance!.LocationsGenerated = true;
        RoadLifecycleManager.OnWorldDataLoaded();
    }
    private static void AdvanceToCompute()
    {
        DrainPreparation();
        Assert.Equal("Generating",RoadLifecycleManager.Progress.Phase);
    }
    private static void DrainPreparation()
    {
        var timeout=Stopwatch.StartNew();
        while(RoadLifecycleManager.Progress.Phase=="Preparing" && timeout.ElapsedMilliseconds<10000)
            RoadLifecycleManager.Tick();
        Assert.NotEqual("Preparing",RoadLifecycleManager.Progress.Phase);
    }
    private static void Finish()
    {
        DrainPreparation();
        Assert.True(SpinWait.SpinUntil(() => { RoadLifecycleManager.Tick(); return !RoadLifecycleManager.IsGenerating; }, 10000), "generation did not settle");
    }
    public void Dispose()
    {
        LocationLevelling.PrimeIncremental=null;
        LocationLevelling.CanPrimeIncrementally=null;
        RoadPreparation.CheckpointsPerTickForTests=int.MaxValue;
        RoadPreparation.ClockForTests=null;
        RoadLifecycleManager.BeforeWorkerCompletionCheck = null;
        RoadLifecycleManager.ReturnToMenuForTests = null;
        RoadLifecycleManager.MenuTransitionBlockedForTests = null;
        RoadLifecycleManager.DisableActivityForTests=false;
        world.Release.Set(); world.RightRelease.Set(); world.PricingRelease.Set();
        RoadLifecycleManager.OnZoneSystemDestroy(ZoneSystem.instance!);
        RoadNetworkGenerator.NetworkOptions = oldOptions;
        RoadNetworkGenerator.IslandRoadPercentage = oldPercentage;
        RoadParallel.Configured = oldWorkers;
        RoadNetworkGenerator.GenerateOnLoad = true;
        scope.Dispose();
        world.Release.Dispose(); world.RightRelease.Dispose(); world.Entered.Dispose(); world.PricingEntered.Dispose(); world.PricingRelease.Dispose();
    }
    [Fact]
    public void RealPreparationSpansFramesWithoutStartingWorkersOrPublishingNetworkAndCancelsSafely()
    {
        RoadPreparation.CheckpointsPerTickForTests=1;
        Begin();
        Assert.True(SpinWait.SpinUntil(()=> { RoadLifecycleManager.Tick(); return RoadLifecycleManager.Progress.PreparationStage=="Mapping island terrain"; },1000));
        var before=RoadLifecycleManager.Progress;
        for(int frame=0;frame<10;frame++) RoadLifecycleManager.Tick();
        var run=typeof(RoadLifecycleManager).GetField("m_generation",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
        Assert.Null(run.GetType().GetField("Worker",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(run));
        Assert.False(LocationLevelling.Sealed); Assert.False(RoadNetworkGenerator.RoadsAvailable);
        Assert.Equal(0,RoadSpatialGrid.TotalRoadPoints); Assert.Empty(RoadLifecycleManager.Progress.ActiveIslands);
        RoadLifecycleManager.OnPrepareSave(); Assert.Empty(ZDOMan.instance!.Zdos);
        Assert.Throws<InvalidOperationException>(()=>RoadNetworkGenerator.GenerateRoads());
        int menus=0; RoadLifecycleManager.ReturnToMenuForTests=()=>menus++;
        Assert.True(RoadLifecycleManager.RequestCancelToMenu(before.RunId));
        Assert.Equal("Stopping generation…",RoadLifecycleManager.Progress.Text);
        Assert.Empty(RoadLifecycleManager.Progress.PreparationStage);
        int reads=world.MainReads;
        RoadLifecycleManager.Tick(); Assert.Equal(1,menus);
        for(int frame=0;frame<4;frame++) RoadLifecycleManager.Tick();
        Assert.Equal(reads,world.MainReads); Assert.Equal(0,world.ActiveReads);
        Assert.False(LocationLevelling.Sealed); Assert.False(RoadNetworkGenerator.RoadsAvailable);
        Assert.Empty(RoadLifecycleManager.Progress.PreparationStage);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PrimingCancellationOrWorldReplacementDisposesBeforeResetWithoutResuming(bool replaceWorld)
    {
        int entered=0,disposed=0;
        IEnumerable<bool> Prime(RoadPreparation preparation)
        {
            try
            {
                preparation.BeginStage("Controlled location footprints",1000,"prefabs"); yield return true;
                for(int i=0;i<1000;i++) { entered++; preparation.Checkpoint(); yield return true; }
            }
            finally { Assert.False(LocationLevelling.Sealed); disposed++; }
        }
        LocationLevelling.PrimeIncremental=Prime;
        Begin();
        Assert.True(SpinWait.SpinUntil(()=> { RoadLifecycleManager.Tick(); return RoadLifecycleManager.Progress.PreparationStage=="Controlled location footprints"; },10000));
        RoadPreparation.CheckpointsPerTickForTests=1;
        RoadLifecycleManager.Tick(); Assert.Equal(1,entered);
        if(replaceWorld) scope.WithWorld(new Islands());
        else Assert.True(RoadLifecycleManager.RequestCancelToMenu(RoadLifecycleManager.Progress.RunId));
        RoadLifecycleManager.Tick(); Assert.Equal(1,disposed);
        Assert.False(RoadNetworkGenerator.RoadsAvailable); Assert.Equal(0,RoadSpatialGrid.TotalRoadPoints);
        for(int i=0;i<3;i++) RoadLifecycleManager.Tick(); Assert.Equal(1,entered); Assert.Equal(1,disposed);
    }
    [Fact]
    public void PrimingFaultDisposesAndNeverStartsOrPublishesWorkerData()
    {
        int disposed=0;
        IEnumerable<bool> Prime(RoadPreparation preparation)
        {
            try { preparation.BeginStage("Faulting footprints"); yield return true; throw new InvalidOperationException("controlled preparation fault"); }
            finally { disposed++; }
        }
        LocationLevelling.PrimeIncremental=Prime;
        Begin(); Finish();
        Assert.Equal("Failed",RoadLifecycleManager.Progress.Phase); Assert.Equal(1,disposed);
        Assert.False(LocationLevelling.Sealed); Assert.False(RoadNetworkGenerator.RoadsAvailable);
        Assert.Equal(0,RoadSpatialGrid.TotalRoadPoints); Assert.Empty(RoadLifecycleManager.Progress.PreparationStage);
    }
    [Fact]
    public void SynchronousPrimingFaultUsesSameCoreAndCleansUpBeforeRethrowing()
    {
        int disposed=0;
        IEnumerable<bool> Prime(RoadPreparation preparation)
        {
            try { preparation.BeginStage("Synchronous fault"); yield return true; throw new InvalidOperationException("controlled synchronous preparation fault"); }
            finally { disposed++; }
        }
        LocationLevelling.PrimeIncremental=Prime;
        Assert.Throws<InvalidOperationException>(()=>RoadNetworkGenerator.GenerateRoads());
        Assert.Equal(1,disposed); Assert.False(RoadLifecycleManager.IsGenerating);
        Assert.False(LocationLevelling.Sealed); Assert.False(RoadNetworkGenerator.RoadsAvailable);
        Assert.Equal(0,RoadSpatialGrid.TotalRoadPoints);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ThrowingPreparationDisposalCannotStrandCancelOrReplacementCleanup(bool replaceWorld)
    {
        int disposed=0;
        IEnumerable<bool> Prime(RoadPreparation preparation)
        {
            try { preparation.BeginStage("Throwing cleanup"); yield return true; }
            finally { disposed++; throw new InvalidOperationException("controlled cleanup fault"); }
        }
        LocationLevelling.PrimeIncremental=Prime;
        Begin();
        Assert.True(SpinWait.SpinUntil(()=> { RoadLifecycleManager.Tick(); return RoadLifecycleManager.Progress.PreparationStage=="Throwing cleanup"; },10000));
        if(replaceWorld) scope.WithWorld(new Islands());
        else Assert.True(RoadLifecycleManager.RequestCancelToMenu(RoadLifecycleManager.Progress.RunId));
        RoadLifecycleManager.Tick();
        Assert.Equal(1,disposed); Assert.False(LocationLevelling.Sealed);
        Assert.False(RoadNetworkGenerator.RoadsAvailable); Assert.Equal(0,RoadSpatialGrid.TotalRoadPoints);
        Assert.Equal(replaceWorld?"Cancelled":"ReturningToMenu",RoadLifecycleManager.Progress.Phase);
        RoadLifecycleManager.CancelAndDrain(); Assert.False(RoadLifecycleManager.IsGenerating);
        Assert.Equal(1,disposed);
    }
    [Fact]
    public void ReplacedPrimeAndSourcesUseCompatibilityPathExactlyOnce()
    {
        int footprints=0,placements=0,prime=0;
        var oldFootprints=RoadSiteProtection.Source;
        var oldPlacements=LocationPlacementHeights.Source;
        var oldPrime=LocationLevelling.Prime;
        LocationLevelling.CanPrimeIncrementally=()=>false;
        LocationLevelling.PrimeIncremental=_=>throw new InvalidOperationException("installed incremental callback must not bypass replacements");
        RoadSiteProtection.Source=()=> { footprints++; return Array.Empty<RoadSiteProtection.Footprint>(); };
        LocationPlacementHeights.Source=()=> { placements++; return Array.Empty<(Vector2,float)>(); };
        LocationPlacementHeights.Clear();
        LocationLevelling.Prime=()=> { prime++; LocationPlacementHeights.Read(); };
        try
        {
            Begin(); Finish();
            Assert.Equal("Complete",RoadLifecycleManager.Progress.Phase);
            RoadSiteProtection.Prime(); RoadSiteProtection.Prime();
            LocationPlacementHeights.Read(); LocationPlacementHeights.Read();
            Assert.Equal(1,footprints); Assert.Equal(1,placements); Assert.Equal(1,prime);
        }
        finally
        {
            RoadSiteProtection.Source=oldFootprints; LocationPlacementHeights.Source=oldPlacements;
            LocationLevelling.Prime=oldPrime; LocationPlacementHeights.Clear();
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PrimaryPreparationFaultIsPreservedWhenCustomIteratorDisposalAlsoThrows(bool synchronous)
    {
        var prime=new FaultingPrime();
        LocationLevelling.PrimeIncremental=_=>prime;
        if(synchronous)
        {
            var error=Assert.Throws<InvalidOperationException>(()=>RoadNetworkGenerator.GenerateRoads());
            Assert.Equal("primary preparation failure",error.Message);
        }
        else
        {
            Begin(); Finish(); Assert.Equal("Failed",RoadLifecycleManager.Progress.Phase);
            Assert.Equal("primary preparation failure",RoadLifecycleManager.Progress.Failure);
        }
        Assert.Equal(1,prime.Disposals); Assert.False(RoadLifecycleManager.IsGenerating);
        Assert.False(LocationLevelling.Sealed); Assert.False(RoadNetworkGenerator.RoadsAvailable);
        Assert.Equal(0,RoadSpatialGrid.TotalRoadPoints);
    }
    [Fact]
    public void DestinationPreparationCountsSelectedIslandsWithoutCandidates()
    {
        foreach(var key in ZoneSystem.instance!.m_locationInstances.Where(p=>p.Value.m_position.x>0).Select(p=>p.Key).ToArray())
            ZoneSystem.instance.m_locationInstances.Remove(key);
        RoadPreparation.CheckpointsPerTickForTests=1;
        using var preparation=RoadNetworkGenerator.BeginPreparation(false);
        bool observedAll=false;
        while(!preparation.Advance())
        {
            if(preparation.Stage!="Selecting road destinations") continue;
            Assert.Equal(2,preparation.Total);
            if(preparation.Completed==2) observedAll=true;
        }
        Assert.True(observedAll,"An empty selected island must count as examined before leaving the stage.");
        Assert.Single(preparation.TakeResult()!.Islands);
        RoadNetworkGenerator.ReleaseGenerationResources();
    }
    [Fact]
    public async Task CancelRequestTicksWithoutBlockingWhileWorkerDrainsThenDispatchesMenuOnce()
    {
        world.Gate=world.HoldCancellation=true;
        Begin(); AdvanceToCompute(); Assert.True(world.Entered.Wait(3000));
        var partial=RoadSpatialGrid.PlanRoadPath(new List<Vector2> { new(-1800,0),new(-1750,0) },4,world)!;
        RoadSpatialGrid.Commit(partial);
        Assert.True(RoadSpatialGrid.SnapshotTotalKilometres()>0);
        int menu=0; RoadLifecycleManager.ReturnToMenuForTests=()=>menu++;
        long run=RoadLifecycleManager.Progress.RunId;
        Assert.False(RoadLifecycleManager.RequestCancelToMenu(run-1));
        Assert.True(RoadLifecycleManager.RequestCancelToMenu(run));
        Assert.True(RoadLifecycleManager.RequestCancelToMenu(run));
        Assert.Equal("Stopping",RoadLifecycleManager.Progress.Phase);
        Assert.True(RoadLifecycleManager.Progress.TotalKilometres>0);
        try
        {
            var tick=Task.Run(RoadLifecycleManager.Tick);
            Assert.True(await Task.WhenAny(tick,Task.Delay(1000))==tick,"cancellation Tick blocked on live work");
            await tick;
            Assert.True(RoadLifecycleManager.InitialGenerationActive);
            Assert.Equal(0,menu); Assert.True(world.ActiveReads>0); Assert.True(LocationLevelling.Sealed);
            RoadLifecycleManager.OnPrepareSave(); Assert.Empty(ZDOMan.instance!.Zdos);
        }
        finally { world.Release.Set(); }
        Assert.True(SpinWait.SpinUntil(()=> { RoadLifecycleManager.Tick(); return menu==1; },5000));
        Assert.Equal(0,world.ActiveReads); Assert.False(LocationLevelling.Sealed);
        Assert.True(RoadLifecycleManager.IsGenerating); Assert.True(RoadLifecycleManager.WorkersOwnWorld);
        Assert.Equal("ReturningToMenu",RoadLifecycleManager.Progress.Phase);
        Assert.Empty(RoadLifecycleManager.Progress.Islands); Assert.Equal(0,RoadSpatialGrid.TotalRoadPoints);
        Assert.Equal(0,RoadLifecycleManager.Progress.TotalKilometres);
        for(int i=0;i<5;i++) RoadLifecycleManager.Tick(); Assert.Equal(1,menu);
        RoadLifecycleManager.OnPrepareSave(); Assert.Empty(ZDOMan.instance!.Zdos);
        RoadLifecycleManager.CancelAndDrain(); // Game.Shutdown prefix precedes vanilla's final save.
        RoadLifecycleManager.OnPrepareSave();
        Assert.False(RoadLifecycleManager.IsGenerating); Assert.False(RoadNetworkGenerator.RoadsAvailable);
        Assert.Empty(ZDOMan.instance!.Zdos);
        world.Gate=false; RoadLifecycleManager.OnZoneSystemDestroy(ZoneSystem.instance!);
        RoadLifecycleManager.OnZoneSystemStart(ZoneSystem.instance!); Begin(); Finish();
        Assert.Equal("Complete",RoadLifecycleManager.Progress.Phase);
    }
    [Fact]
    public void EmergencyDrainClearsCommittedTotalBeforeTerminalAndNextRun()
    {
        world.Gate=true; Begin(); AdvanceToCompute(); Assert.True(world.Entered.Wait(3000));
        var partial=RoadSpatialGrid.PlanRoadPath(new List<Vector2> { new(-1800,0),new(-1750,0) },4,world)!;
        RoadSpatialGrid.Commit(partial); Assert.True(RoadSpatialGrid.SnapshotTotalKilometres()>0);
        RoadLifecycleManager.CancelAndDrain();
        Assert.Equal("Cancelled",RoadLifecycleManager.Progress.Phase);
        Assert.Equal(0,RoadLifecycleManager.Progress.TotalKilometres);
        Assert.Empty(RoadLifecycleManager.Progress.ActiveIslands);
        Assert.False(RoadLifecycleManager.IsGenerating); Assert.False(LocationLevelling.Sealed);
        RoadLifecycleManager.OnZoneSystemDestroy(ZoneSystem.instance!);
        RoadLifecycleManager.OnZoneSystemStart(ZoneSystem.instance!); Begin();
        Assert.Equal("Preparing",RoadLifecycleManager.Progress.Phase);
        Assert.Equal(0,RoadLifecycleManager.Progress.TotalKilometres);
        world.Gate=false; world.Release.Set(); Finish();
    }
    [Fact]
    public void AcceptedCancelWinsOverAlreadyCompletedUnpublishedWorker()
    {
        Begin(); AdvanceToCompute();
        var run=typeof(RoadLifecycleManager).GetField("m_generation",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
        var worker=(Task)run.GetType().GetField("Worker",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(run)!;
        Assert.True(SpinWait.SpinUntil(()=>worker.IsCompleted,5000));
        Assert.False(RoadNetworkGenerator.RoadsAvailable);
        int menus=0; RoadLifecycleManager.ReturnToMenuForTests=()=>menus++;
        Assert.True(RoadLifecycleManager.RequestCancelToMenu(RoadLifecycleManager.Progress.RunId));
        RoadLifecycleManager.Tick();
        Assert.Equal(1,menus); Assert.False(RoadNetworkGenerator.RoadsAvailable); Assert.Empty(ZDOMan.instance!.Zdos);
        Assert.Equal(0,RoadSpatialGrid.TotalRoadPoints);
    }
    [Fact]
    public void CancelBeforePreparationAndMenuFailureKeepGateUntilExplicitRetry()
    {
        Begin(); long run=RoadLifecycleManager.Progress.RunId;
        int menus=0; RoadLifecycleManager.ReturnToMenuForTests=()=> { menus++; throw new InvalidOperationException("controlled menu failure"); };
        Assert.True(RoadLifecycleManager.RequestCancelToMenu(run)); RoadLifecycleManager.Tick();
        Assert.Equal("MenuFailed",RoadLifecycleManager.Progress.Phase);
        Assert.Equal(1,menus); Assert.True(RoadLifecycleManager.InitialGenerationActive);
        for(int i=0;i<5;i++) RoadLifecycleManager.Tick(); Assert.Equal(1,menus);
        RoadLifecycleManager.ReturnToMenuForTests=()=>menus++;
        Assert.True(RoadLifecycleManager.RequestCancelToMenu(run)); Assert.Equal(1,menus);
        RoadLifecycleManager.MenuTransitionBlockedForTests=()=>true;
        Assert.False(RoadLifecycleManager.RetryReturnToMenu(run)); Assert.Equal(1,menus);
        RoadLifecycleManager.MenuTransitionBlockedForTests=()=>false;
        Assert.True(RoadLifecycleManager.RetryReturnToMenu(run)); Assert.Equal(2,menus);
        Assert.Equal("ReturningToMenu",RoadLifecycleManager.Progress.Phase);
    }
    [Fact]
    public void DialogAppearingDuringDrainDefersInitialMenuDispatchUntilExplicitRetry()
    {
        Begin(); int menus=0;
        RoadLifecycleManager.ReturnToMenuForTests=()=>menus++;
        RoadLifecycleManager.MenuTransitionBlockedForTests=()=>true;
        long run=RoadLifecycleManager.Progress.RunId;
        Assert.True(RoadLifecycleManager.RequestCancelToMenu(run)); RoadLifecycleManager.Tick();
        Assert.Equal(0,menus); Assert.True(RoadLifecycleManager.InitialGenerationActive);
        Assert.Equal("MenuFailed",RoadLifecycleManager.Progress.Phase);
        Assert.False(RoadLifecycleManager.RetryReturnToMenu(run));
        RoadLifecycleManager.MenuTransitionBlockedForTests=()=>false;
        RoadLifecycleManager.Tick(); Assert.Equal(0,menus);
        Assert.True(RoadLifecycleManager.RetryReturnToMenu(run)); Assert.Equal(1,menus);
    }
    [Fact]
    public void CompletedNetworkCannotBeCancelledToMenu()
    {
        Begin(); Finish();
        Assert.False(RoadLifecycleManager.RequestCancelToMenu(RoadLifecycleManager.Progress.RunId));
        Assert.True(RoadNetworkGenerator.RoadsAvailable);
    }
    [Fact]
    public void BeginClaimsOnceAndFramesContinueWhileWorkersAreGated()
    {
        world.Gate = true;
        Begin();
        Assert.Equal("Preparing", RoadLifecycleManager.Progress.Phase);
        long run = RoadLifecycleManager.Progress.RunId;
        RoadLifecycleManager.OnWorldDataLoaded();
        RoadLifecycleManager.OnZoneSystemStart(ZoneSystem.instance!);
        Assert.Equal(run, RoadLifecycleManager.Progress.RunId);
        AdvanceToCompute();
        Assert.True(world.Entered.Wait(3000));
        for (int frame = 0; frame < 20; frame++) RoadLifecycleManager.Tick();
        Assert.Equal("Generating", RoadLifecycleManager.Progress.Phase);
        Assert.Equal(0, RoadLifecycleManager.Progress.Completed);
        Assert.Equal(2, RoadLifecycleManager.Progress.Total);
        Assert.False(RoadNetworkGenerator.RoadsAvailable);
        RoadLifecycleManager.OnPrepareSave();
        Assert.Empty(ZDOMan.instance!.Zdos);
        Assert.Throws<InvalidOperationException>(() => RoadNetworkGenerator.GenerateRoads());
        Assert.Throws<InvalidOperationException>(() => ManualRoads.Prepare(new[] { new Vector2(0, 0), new Vector2(8, 0) }, false));
        world.Release.Set(); Finish();
        Assert.Equal("Complete", RoadLifecycleManager.Progress.Phase);
        Assert.Equal(2, RoadLifecycleManager.Progress.Completed);
        Assert.True(RoadNetworkGenerator.RoadsAvailable);
        int version = RoadSpatialGrid.RoadNetworkVersion;
        RoadLifecycleManager.Tick();
        Assert.Equal(version, RoadSpatialGrid.RoadNetworkVersion);
    }
    [Fact]
    public void CancellationDrainsBeforeResetAndCannotPublishIntoTheNextRun()
    {
        world.Gate = true;
        Begin(); AdvanceToCompute(); Assert.True(world.Entered.Wait(3000));
        long run = RoadLifecycleManager.Progress.RunId;
        RoadNetworkGenerator.Reset();
        Assert.Equal(0, world.ActiveReads);
        Assert.Equal("Cancelled", RoadLifecycleManager.Progress.Phase);
        Assert.False(RoadNetworkGenerator.RoadsAvailable);
        Assert.Equal(0, RoadSpatialGrid.TotalRoadPoints);
        Assert.False(LocationLevelling.Sealed);
        world.Gate = false;
        Begin(); Finish();
        Assert.True(RoadLifecycleManager.Progress.RunId > run);
        Assert.Equal("Complete", RoadLifecycleManager.Progress.Phase);
        Assert.Equal(2, RoadLifecycleManager.Progress.Completed);
    }
    [Fact]
    public void LocationRegistrationDuringGenerationIsQueuedNotRefused()
    {
        // Another mod registering from a world-load hook now overlaps the asynchronous initial
        // generation; its island workers read the registered names, so the change waits for it.
        world.Gate=true; Begin(); AdvanceToCompute();
        RoadNetworkGenerator.RegisterLocation("QueuedPlace");
        Assert.DoesNotContain("QueuedPlace", RoadNetworkGenerator.GetRegisteredLocations());
        world.Release.Set(); Finish();
        Assert.Contains("QueuedPlace", RoadNetworkGenerator.GetRegisteredLocations());
        RoadNetworkGenerator.UnregisterLocation("QueuedPlace");
        Assert.DoesNotContain("QueuedPlace", RoadNetworkGenerator.GetRegisteredLocations());
    }
    [Theory]
    [InlineData("failure")]
    [InlineData("cancel")]
    [InlineData("unload")]
    public void IntegratedQueuedRegistrationAppliesAfterEachTerminalPath(string terminal)
    {
        string name="QueuedIntegration"+terminal;
        world.Gate=true; Begin(); AdvanceToCompute(); Assert.True(world.Entered.Wait(3000));
        try
        {
            RoadNetworkGenerator.RegisterLocation(name);
            Assert.DoesNotContain(name,RoadNetworkGenerator.GetRegisteredLocations());
            if(terminal=="failure") { world.Fail=true; world.Release.Set(); Finish(); Assert.Equal("Failed",RoadLifecycleManager.Progress.Phase); }
            else if(terminal=="cancel") RoadLifecycleManager.CancelAndDrain();
            else RoadLifecycleManager.OnZoneSystemDestroy(ZoneSystem.instance!);
            Assert.False(RoadLifecycleManager.IsGenerating); Assert.Equal(0,world.ActiveReads);
            Assert.Contains(name,RoadNetworkGenerator.GetRegisteredLocations());
        }
        finally { RoadNetworkGenerator.UnregisterLocation(name); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IntegratedSynchronousGenerationFlushesRegistrationsEvenWhenPreparationFails(bool fail)
    {
        const string name="QueuedSynchronousIntegration";
        var oldPrime=LocationLevelling.Prime;
        LocationLevelling.Prime=()=>
        {
            RoadNetworkGenerator.RegisterLocation(name);
            Assert.DoesNotContain(name,RoadNetworkGenerator.GetRegisteredLocations());
            if(fail) throw new InvalidOperationException("controlled synchronous preparation failure");
        };
        try
        {
            if(fail) Assert.Throws<InvalidOperationException>(()=>RoadNetworkGenerator.GenerateRoads());
            else RoadNetworkGenerator.GenerateRoads();
            Assert.False(RoadLifecycleManager.IsGenerating);
            Assert.Contains(name,RoadNetworkGenerator.GetRegisteredLocations());
        }
        finally { LocationLevelling.Prime=oldPrime; RoadNetworkGenerator.UnregisterLocation(name); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IntegratedSpawnFallbackIsSynchronousOnceAndNeverThrows(bool fail)
    {
        int primes=0; var oldPrime=LocationLevelling.Prime;
        LocationLevelling.Prime=()=> { primes++; if(fail) throw new InvalidOperationException("controlled spawn preparation failure"); };
        try
        {
            ZoneSystem.instance!.LocationsGenerated=true;
            RoadLifecycleManager.OnPlayerSpawn(Vector3.zero);
            Assert.Equal(1,primes); Assert.False(RoadLifecycleManager.IsGenerating);
            Assert.Equal(!fail,RoadNetworkGenerator.RoadsAvailable);
            int reads=world.MainReads;
            RoadLifecycleManager.OnPlayerSpawn(Vector3.zero); RoadLifecycleManager.OnWorldDataLoaded();
            Assert.Equal(1,primes); Assert.Equal(reads,world.MainReads);
        }
        finally { LocationLevelling.Prime=oldPrime; }
    }
    [Fact]
    public void ZonesExploredBeforeTheNetworkAreLeftOutBeforeAnyRoadIsPlanned()
    {
        var explored=new Vector2s(3,3);
        NetworkCutoff.SnapshotWorld(new[]{explored});
        bool leftOut=false; var oldPrime=LocationLevelling.Prime;
        LocationLevelling.Prime=()=>leftOut=NetworkCutoff.Excludes(explored);
        try { RoadNetworkGenerator.GenerateRoads(); }
        finally { LocationLevelling.Prime=oldPrime; NetworkCutoff.Forget(); }
        Assert.True(leftOut);
    }
    [Fact]
    public void IntegratedSpawnFallbackDoesNotGenerateForJoinedClient()
    {
        scope.WithNetwork(server:false); ZoneSystem.instance!.LocationsGenerated=true;
        int reads=world.MainReads;
        RoadLifecycleManager.OnPlayerSpawn(Vector3.zero);
        Assert.Equal(reads,world.MainReads); Assert.False(RoadLifecycleManager.IsGenerating);
        Assert.False(RoadNetworkGenerator.RoadsAvailable);
    }
    [Fact]
    public void IntegratedSpawnFallbackRetainsPublishedNetworkAfterZoneApplicationFails()
    {
        RoadNetworkGenerator.GenerateRoads();
        var zones=RoadSpatialGrid.GetZonesWithRoadPoints().Take(2).ToArray();
        Assert.Equal(2,zones.Length); RoadNetworkGenerator.Reset();
        scope.RegisterHeightmap(zones[0],64,withCompiler:false);
        var broken=scope.RegisterHeightmap(zones[1],64);
        broken.m_terrainComp!.m_nview=null!;
        ZoneSystem.instance!.LocationsGenerated=true;
        RoadLifecycleManager.OnPlayerSpawn(Vector3.zero);
        Assert.False(RoadLifecycleManager.IsGenerating);
        Assert.True(RoadNetworkGenerator.RoadsAvailable);
        Assert.True(RoadSpatialGrid.Statistics.StoredPointRecords>0);
        Assert.True(ZDOMan.instance!.CountWithPrefab(TerrainComp.PrefabName)>=2);
        int version=RoadSpatialGrid.RoadNetworkVersion;
        RoadLifecycleManager.OnPrepareSave();
        Assert.Equal(version,RoadSpatialGrid.RoadNetworkVersion);
    }
    [Fact]
    public void WorkerFailureDiscardsPartialStateAndPublishesNoMetadata()
    {
        world.Gate = world.Fail = true;
        world.Release.Set();
        Begin(); Finish();
        Assert.Equal("Failed", RoadLifecycleManager.Progress.Phase);
        Assert.Contains("controlled terrain failure", RoadLifecycleManager.Progress.Failure!);
        Assert.False(RoadNetworkGenerator.RoadsAvailable);
        Assert.Equal(0, RoadSpatialGrid.TotalRoadPoints);
        Assert.Empty(ZDOMan.instance!.Zdos);
        Assert.Equal(0, world.ActiveReads);
        long failedRun = RoadLifecycleManager.Progress.RunId;
        RoadLifecycleManager.OnPlayerSpawn(Vector3.zero);
        RoadLifecycleManager.OnWorldDataLoaded();
        Assert.Equal(failedRun, RoadLifecycleManager.Progress.RunId);
        Assert.False(RoadLifecycleManager.IsGenerating);
    }
    [Fact]
    public void HeldWorkersReuseSnapshotBetweenPublicationDeadlinesButCompletionPublishesImmediately()
    {
        world.Gate=true; Begin(); AdvanceToCompute(); Assert.True(world.Entered.Wait(3000));
        var run=typeof(RoadLifecycleManager).GetField("m_generation",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
        var clock=(Stopwatch)run.GetType().GetField("Clock",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(run)!;
        clock.Stop(); // Hold the real owner's clock between deadlines without adding a runtime scheduler abstraction.
        RoadLifecycleManager.Tick(); var held=RoadLifecycleManager.Progress;
        for(int frame=0;frame<1000;frame++) RoadLifecycleManager.Tick();
        Assert.Same(held,RoadLifecycleManager.Progress);
        world.Release.Set(); Finish();
        Assert.NotSame(held,RoadLifecycleManager.Progress);
        Assert.Equal("Complete",RoadLifecycleManager.Progress.Phase); Assert.Equal(2,RoadLifecycleManager.Progress.Completed);
    }
    [Fact]
    public void SingleIslandNestedPricingReportsToItsOneLogicalSlot()
    {
        RoadNetworkGenerator.IslandRoadPercentage=50;
        world.Gate=true;
        Begin(); AdvanceToCompute(); Assert.True(world.Entered.Wait(3000));
        var run=typeof(RoadLifecycleManager).GetField("m_generation",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
        var work=(RoadNetworkGenerator.GenerationWork)run.GetType().GetField("Work",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(run)!;
        world.PricingGate=()=> {
            var activity=work.Activity!.Snapshot()[0];
            return activity.Stage=="Pricing" && activity.SearchSteps>0 && activity.ActiveSearches>0;
        };
        world.Gate=false; world.Release.Set();
        try
        {
            Assert.True(world.PricingEntered.Wait(3000));
            var pricing=Assert.Single(work.Activity!.Snapshot());
            Assert.Equal("Pricing",pricing.Stage); Assert.True(pricing.SearchSteps>0); Assert.True(pricing.ActiveSearches>0);
            Assert.Equal(0,pricing.Processed); Assert.Equal(0,pricing.Reached);
        }
        finally { world.PricingRelease.Set(); }
        Finish();
        var row=Assert.Single(RoadLifecycleManager.Progress.Islands);
        Assert.Equal(1,RoadLifecycleManager.Progress.Total);
        Assert.Equal(2,RoadParallel.Configured);
        Assert.True(row.PricesTotal>=3); Assert.Equal(row.PricesTotal,row.PricesCompleted);
        Assert.True(row.SearchSteps>0); Assert.Equal(0,row.ActiveSearches);
        Assert.Equal(row.Destinations,row.Processed);
        Assert.Equal(RoadSpatialGrid.TotalRoadLength/1000d,row.Kilometres,5);
    }
    [Fact]
    public void RealIslandRowsMatchFinalReachAndCommittedLengthAuthorities()
    {
        world.Gate=true; Begin(); AdvanceToCompute();
        Assert.True(SpinWait.SpinUntil(()=>world.ActiveReads==2,5000));
        var run=typeof(RoadLifecycleManager).GetField("m_generation",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
        var work=(RoadNetworkGenerator.GenerationWork)run.GetType().GetField("Work",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(run)!;
        world.Release.Set(); Finish();
        var rows=RoadLifecycleManager.Progress.Islands;
        Assert.Equal(2,rows.Count);
        foreach(var row in rows)
        {
            var selected=work.Islands[row.Island-1].Locations;
            Assert.Equal(selected.Count,row.Processed); Assert.Equal(selected.Count,row.Destinations);
            Assert.Equal(selected.Count(d=>RoadNetworkGenerator.Reached(d.position,d.radius)),row.Reached);
            Assert.Equal("Finished",row.Stage); Assert.False(row.Active); Assert.Equal(0,row.ActiveSearches);
            Assert.Equal(row.PricesTotal,row.PricesCompleted); Assert.True(row.SearchSteps>0);
        }
        Assert.Equal(RoadSpatialGrid.TotalRoadLength/1000d,rows.Sum(r=>r.Kilometres),5);
        Assert.Equal(RoadSpatialGrid.TotalRoadLength/1000d,RoadLifecycleManager.Progress.TotalKilometres!.Value,5);
        Assert.Empty(RoadLifecycleManager.Progress.ActiveIslands);
    }
    [Fact]
    public void AsyncTwoIslandNetworkMatchesSerialBytes()
    {
        RoadParallel.Configured = 1;
        RoadLifecycleManager.DisableActivityForTests=true;
        RoadNetworkGenerator.GenerateRoads();
        byte[] serial = RoadSpatialGrid.SerializeAllRoadPoints()!;
        int version = RoadSpatialGrid.RoadNetworkVersion;
        var starts = RoadNetworkGenerator.GetRoadStartPoints().Select(p => p.ToString()).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var crossings = RoadNetworkPersistence.SerializeRoadCrossings(RoadNetworkGenerator.GetRoadCrossings());
        Assert.True(RoadSpatialGrid.TotalRoadPoints > 0);
        RoadNetworkGenerator.Reset();
        RoadParallel.Configured = 2;
        RoadLifecycleManager.DisableActivityForTests=false;
        RoadPreparation.CheckpointsPerTickForTests=1;
        Begin(); Finish();
        Assert.Equal(CanonicalRecords(serial), CanonicalRecords(RoadSpatialGrid.SerializeAllRoadPoints()!));
        Assert.Equal(2, RoadLifecycleManager.Progress.Total);
        Assert.Equal(version, RoadSpatialGrid.RoadNetworkVersion);
        Assert.Equal(starts, RoadNetworkGenerator.GetRoadStartPoints().Select(p => p.ToString()).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        Assert.Equal(crossings, RoadNetworkPersistence.SerializeRoadCrossings(RoadNetworkGenerator.GetRoadCrossings()));
    }
    private static string[] CanonicalRecords(byte[] bytes)
    {
        using var input = new BinaryReader(new MemoryStream(bytes));
        int envelope=input.ReadInt32();
        Assert.Equal(4,envelope); Assert.Equal(2,input.ReadInt32());
        int cells = input.ReadInt32();
        var records = new List<string>();
        for (int c = 0; c < cells; c++)
        {
            int x = input.ReadInt32(), z = input.ReadInt32(), points = input.ReadInt32();
            for (int p = 0; p < points; p++)
                records.Add(x + ":" + z + ":" + Convert.ToBase64String(input.ReadBytes(17)));
        }
        Assert.Equal(1,input.ReadByte()); input.ReadSingle(); // Separate statistics tests assert exact length persistence.
        Assert.Equal(input.BaseStream.Length, input.BaseStream.Position);
        return records.OrderBy(r => r, StringComparer.Ordinal).ToArray();
    }
    [Fact]
    public void LastCompletionPublishedBetweenObservationAndTaskCheckIsConsumed()
    {
        world.Gate = true;
        Begin(); AdvanceToCompute(); Assert.True(world.Entered.Wait(3000));
        var run = typeof(RoadLifecycleManager).GetField("m_generation", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var worker = (Task)run.GetType().GetField("Worker", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(run)!;
        RoadLifecycleManager.BeforeWorkerCompletionCheck = () =>
        {
            // Exactly the former race: the first drain has observed no completions, then the workers publish and exit.
            world.Release.Set();
            Assert.True(SpinWait.SpinUntil(() => worker.IsCompleted, 5000));
        };
        RoadLifecycleManager.Tick();
        RoadLifecycleManager.BeforeWorkerCompletionCheck = null;
        Assert.Equal("Complete", RoadLifecycleManager.Progress.Phase);
        Assert.Equal(2, RoadLifecycleManager.Progress.Completed);
    }
    [Fact]
    public void ApplicationFailureRetainsTheFinalNetworkAndPriorWorldSideEffects()
    {
        RoadNetworkGenerator.GenerateRoads();
        var zones = RoadSpatialGrid.GetZonesWithRoadPoints().Take(2).ToArray();
        Assert.Equal(2, zones.Length);
        RoadNetworkGenerator.Reset();
        scope.RegisterHeightmap(zones[0], 64, withCompiler: false);
        var broken = scope.RegisterHeightmap(zones[1], 64);
        broken.m_terrainComp!.m_nview = null!; // fails after the first loaded zone has created its compiler
        Begin(); Finish();
        Assert.Equal("ApplicationFailed", RoadLifecycleManager.Progress.Phase);
        Assert.True(RoadNetworkGenerator.RoadsAvailable);
        Assert.True(RoadSpatialGrid.TotalRoadPoints > 0);
        Assert.True(ZDOMan.instance!.CountWithPrefab(TerrainComp.PrefabName) >= 2);
        int version = RoadSpatialGrid.RoadNetworkVersion;
        RoadLifecycleManager.OnPrepareSave();
        Assert.Equal(version, RoadSpatialGrid.RoadNetworkVersion);
    }
    /// <summary>The spawn fallback (no world-load hook): synchronous generation when the host's player spawns.</summary>
    private static void SpawnWithoutWorldLoad()
    {
        ZoneSystem.instance!.LocationsGenerated = true;
        RoadLifecycleManager.OnPlayerSpawn(Vector3.zero);
    }
    [Fact]
    public void SpawnFallbackFailingBeforePublicationSaysNoNetworkWasPublished()
    {
        int attempts = 0;
        LocationLevelling.PrimeIncremental=_=>{ attempts++; return new FaultingPrime(); };
        var log = scope.CaptureLog();
        SpawnWithoutWorldLoad();
        Assert.Contains(log, l => l.Contains("Road generation after spawn failed; no network was published"));
        Assert.Equal("Failed", RoadLifecycleManager.Progress.Phase);
        Assert.DoesNotContain(log, l => l.Contains("network retained"));
        Assert.False(RoadNetworkGenerator.RoadsAvailable);
        Assert.Equal(0, RoadSpatialGrid.TotalRoadPoints);
        Assert.Equal(1, attempts);
        // Once per world: a failed attempt is not retried.
        SpawnWithoutWorldLoad();
        Assert.Equal(1, attempts);
        Assert.False(RoadNetworkGenerator.RoadsAvailable);
    }
    [Fact]
    public void SpawnFallbackFailingWhileApplyingKeepsThePublishedNetworkAndSaysSo()
    {
        RoadNetworkGenerator.GenerateRoads();
        var zones = RoadSpatialGrid.GetZonesWithRoadPoints().Take(2).ToArray();
        Assert.Equal(2, zones.Length);
        RoadNetworkGenerator.Reset();
        scope.RegisterHeightmap(zones[0], 64, withCompiler: false);
        var broken = scope.RegisterHeightmap(zones[1], 64);
        broken.m_terrainComp!.m_nview = null!; // fails after the first loaded zone has created its compiler
        var log = scope.CaptureLog();
        SpawnWithoutWorldLoad();
        Assert.Contains(log, l => l.Contains("was completed and kept, but a later step failed") && l.Contains("network retained"));
        Assert.DoesNotContain(log, l => l.Contains("no network was published"));
        Assert.Equal("ApplicationFailed", RoadLifecycleManager.Progress.Phase);   // not "Complete"
        Assert.True(RoadNetworkGenerator.RoadsAvailable);
        Assert.True(RoadSpatialGrid.TotalRoadPoints > 0);
        Assert.True(ZDOMan.instance!.CountWithPrefab(TerrainComp.PrefabName) >= 2);
        int version = RoadSpatialGrid.RoadNetworkVersion;
        RoadLifecycleManager.OnPrepareSave();
        Assert.Equal(version, RoadSpatialGrid.RoadNetworkVersion);
    }
    [Fact]
    public void ReverseIslandCompletionCountsFinishedWorkAndSnapshotsStayImmutable()
    {
        world.Gate = world.Reverse = true;
        Begin(); AdvanceToCompute();
        Assert.True(world.Entered.Wait(3000));
        var initial = RoadLifecycleManager.Progress;
        world.RightRelease.Set();
        Assert.True(SpinWait.SpinUntil(() => { RoadLifecycleManager.Tick(); return RoadLifecycleManager.Progress.Completed == 1; }, 5000));
        var middle = RoadLifecycleManager.Progress;
        Assert.Equal(0, initial.Completed);
        Assert.Equal(2, middle.Total);
        Assert.True(RoadLifecycleManager.InitialGenerationActive);
        for (int frame = 0; frame < 10; frame++) RoadLifecycleManager.Tick();
        Assert.Equal(1, RoadLifecycleManager.Progress.Completed);
        world.Release.Set(); Finish();
        Assert.Equal(1, middle.Completed);
        Assert.Equal(2, RoadLifecycleManager.Progress.Completed);
    }
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void DisabledAndNonHostLoadsNeverClaimARun(bool enabled, bool server)
    {
        RoadNetworkGenerator.GenerateOnLoad = enabled;
        ZNet.instance!.Server = server;
        Begin();
        Assert.False(RoadLifecycleManager.IsGenerating);
        Assert.False(RoadNetworkGenerator.RoadsAvailable);
    }
    [Fact]
    public void ZeroSelectedIslandsCompletesAsAnEmptyNetwork()
    {
        RoadNetworkGenerator.IslandRoadPercentage = 0;
        Begin(); Finish();
        Assert.Equal("Complete", RoadLifecycleManager.Progress.Phase);
        Assert.Equal(0, RoadLifecycleManager.Progress.Total);
        Assert.True(RoadNetworkGenerator.RoadsAvailable);
        Assert.Equal(0, RoadSpatialGrid.TotalRoadPoints);
    }

    // A vanilla client joining this server through the game's handshake; the server's PeerInfo goes through JoinHold
    // first, as the mod's prefix on ZNet.RPC_PeerInfo does. Returns the status the client ends with.
    private static ZNet.ConnectionStatus Join(long uid)
    {
        var server=ZNet.instance; var client=new ZNet { Uid=uid };
        var serverEnd=new ZSteamSocket(76561190000000000+(ulong)uid); var clientEnd=new ZSteamSocket(76561190000000001);
        SocketDouble.Link(serverEnd,clientEnd);
        var atServer=new ZNetPeer(serverEnd,server:false); var atClient=new ZNetPeer(clientEnd,server:true);
        server.OnNewConnection(atServer);
        atServer.m_rpc.Register<ZPackage>("PeerInfo",(rpc,pkg)=> { if(!JoinHold.Refuse(rpc)) server.RPC_PeerInfo(rpc,pkg); });
        client.OnNewConnection(atClient);
        for(int i=0;i<4;i++) { server.UpdatePeers(0); client.UpdatePeers(0); }
        return client.Status;
    }
    [Fact]
    public void JoinsAreHeldWhileTheNetworkIsBuiltAndAdmittedOnceItIsDone()
    {
        Assert.Null(JoinHold.Reason);
        Assert.Equal(ZNet.ConnectionStatus.Connected,Join(101));
        world.Gate=true; Begin();
        Assert.Equal("Preparing",RoadLifecycleManager.Progress.Phase);
        Assert.Contains("s in",JoinHold.Reason);
        Assert.Equal(ZNet.ConnectionStatus.ErrorFull,Join(102));
        AdvanceToCompute(); Assert.True(world.Entered.Wait(3000));
        Assert.StartsWith("Generating roads: 0 of 2 islands complete",JoinHold.Reason);
        Assert.Equal(ZNet.ConnectionStatus.ErrorFull,Join(103));
        world.Release.Set(); Finish();
        Assert.Equal("Complete",RoadLifecycleManager.Progress.Phase);
        Assert.Null(JoinHold.Reason);
        Assert.Equal(ZNet.ConnectionStatus.Connected,Join(104));
    }
    [Theory]
    [InlineData("failure")]
    [InlineData("stop")]
    [InlineData("cancel")]
    public void AGenerationThatEndsWithoutANetworkLeavesNoJoinHeld(string terminal)
    {
        world.Gate=true; Begin(); AdvanceToCompute(); Assert.True(world.Entered.Wait(3000));
        Assert.NotNull(JoinHold.Reason);
        if(terminal=="failure") { world.Fail=true; world.Release.Set(); Finish(); Assert.Equal("Failed",RoadLifecycleManager.Progress.Phase); }
        else if(terminal=="stop")
        {
            // Cancel and return to menu: workers still stopping, the run not over, yet nothing will be built.
            Assert.True(RoadLifecycleManager.RequestCancelToMenu(RoadLifecycleManager.Progress.RunId));
            Assert.True(RoadLifecycleManager.IsGenerating); Assert.Equal("Stopping",RoadLifecycleManager.Progress.Phase);
        }
        else { RoadLifecycleManager.CancelAndDrain(); Assert.Equal("Cancelled",RoadLifecycleManager.Progress.Phase); }
        Assert.Null(JoinHold.Reason);
        Assert.Equal(ZNet.ConnectionStatus.Connected,Join(201));
        world.Release.Set();
    }
}
