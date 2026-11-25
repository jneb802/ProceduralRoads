using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

public class PreparationParityTests
{
    private sealed class Landscape : WorldGenerator
    {
        internal int Kind;
        /// <summary>The reads each thread made, counted from every thread: the terrain is mapped on the island workers.</summary>
        internal readonly System.Collections.Concurrent.ConcurrentDictionary<int,int> ReadThreads=new();
        /// <summary>While set, a read east of x = 0 off <see cref="Preparer"/> waits until it is cleared (or 10 s pass).</summary>
        internal readonly ManualResetEventSlim Open=new(true);
        internal int Preparer=-1;
        /// <summary>Reads that waited on <see cref="Open"/>.</summary>
        internal int Held;
        internal int Total => ReadThreads.Values.Sum();
        public override float GetHeight(float x,float z)
        {
            int me=Thread.CurrentThread.ManagedThreadId;
            ReadThreads.AddOrUpdate(me,1,(_,n)=>n+1);
            if(x>0 && me!=Preparer && !Open.IsSet) { Interlocked.Increment(ref Held); Open.Wait(10000); }
            return Kind==2 ? x<0?25:33 : Math.Abs(x)<24 ? Kind==1?29:20 : 40;
        }
        public override Heightmap.Biome GetBiome(float x,float z,float oceanLevel=.02f,bool waterAlwaysOcean=false) => Kind==1?Heightmap.Biome.Swamp:Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x,float z,out float weight,out float width) { weight=Kind==3 && Math.Abs(x)<24?1:0; width=48; }
    }
    // Captured from the original00ea detector BEFORE the incremental refactor. Includes exact float bits and ordered cells.
    [Theory]
    [InlineData(0,"a891f6beacb7bc662b7617a0c8bcaf48702cefce1c69b6fa500d4a7e38f66065")]
    [InlineData(1,"9ed25d2d1428b138885183e18029cd4967f2a043d32eba6b09588afd7b4debbf")]
    [InlineData(2,"305c349274c4b8441ac690f12f8e5b9efe141a7d40293540eb723a5d40843c20")]
    [InlineData(3,"6b0ac593dd35da49f0f820ccaf3f12e1f191db861aa4eba49e924f3a93221d52")]
    public void OriginalDetectorOrderedGeometryIsPreserved(int kind,string expected)
    {
        var world=new Landscape {Kind=kind}; using var scope=new ValheimWorldScope(); scope.WithWorld(world);
        // The pinned hashes include the shore landings, which are off as shipped: the search is on for this pin.
        var given=RoadCoastalLandings.GivenIn; RoadCoastalLandings.GivenIn=_=>true;
        List<Island> islands; try { islands=RoadIslandDetector.Detect(world,worldRadius:256,minArea:1,options:new()); } finally { RoadCoastalLandings.GivenIn=given; }
        Assert.Equal(expected,Fingerprint(islands));
    }
    /// <summary>
    /// The terrain is mapped on the island workers while the preparation waits a frame at a time: with the workers held
    /// in the world's eastern half, the preparation keeps ending its slices in "Mapping island terrain", its progress the
    /// western columns done and never past its total, with no island published and no terrain read on its own thread. Let
    /// go, it ends with the islands one drained detection finds (OriginalDetectorOrderedGeometryIsPreserved pins those).
    /// </summary>
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void TinySlicesRetainOriginalOrderedOutputAndDoNotPublishPartialIslands(int kind)
    {
        var world=new Landscape {Kind=kind}; using var scope=new ValheimWorldScope(); scope.WithWorld(world);
        var expected=RoadIslandDetector.Detect(world,worldRadius:256,minArea:1,options:new());
        int me=Thread.CurrentThread.ManagedThreadId;
        world.Preparer=me;
        List<Island>? result=null;
        using var preparation=new RoadPreparation(p=>RoadIslandDetector.DetectSteps(world,p,value=>result=value,minArea:1,worldRadius:256,options:new()));
        int oldLimit=RoadPreparation.CheckpointsPerTickForTests;
        RoadPreparation.CheckpointsPerTickForTests=1;
        var slices=new Dictionary<string,int>();
        long mapped=0;
        try
        {
            while(preparation.Stage!="Mapping island terrain") Assert.False(preparation.Advance());
            world.Open.Reset(); world.ReadThreads.Clear();
            var held=System.Diagnostics.Stopwatch.StartNew();
            while(held.ElapsedMilliseconds<200 || mapped==0)
            {
                Assert.True(held.ElapsedMilliseconds<5000,"the western columns were never counted");
                Assert.False(preparation.Advance());
                Assert.Null(result);
                Assert.Equal("Mapping island terrain",preparation.Stage);
                Assert.InRange(preparation.Completed,mapped,preparation.Total-1);
                mapped=preparation.Completed;
                slices.TryGetValue(preparation.Stage,out int count); slices[preparation.Stage]=count+1;
            }
            Assert.False(world.ReadThreads.ContainsKey(me),"the terrain was read on the preparation's thread");
            Assert.NotEmpty(world.ReadThreads);
            world.Open.Set();
            while(!preparation.Advance())
            {
                Assert.Null(result);
                if(preparation.Total>0) Assert.InRange(preparation.Completed,0,preparation.Total);
            }
        }
        finally { world.Open.Set(); RoadPreparation.CheckpointsPerTickForTests=oldLimit; }
        Assert.True(slices["Mapping island terrain"]>8,
            "The preparation must wait for the mapping a frame at a time, not block until it is done.");
        Assert.Equal(Fingerprint(expected),Fingerprint(result!));
    }
    /// <summary>
    /// A preparation disposed in the middle of the mapping (a generation cancelled, its world unloaded) waits for the runs it
    /// started: the dispose returns only once the held runs are let go and have stopped, no thread reads the world after it,
    /// and the river window the mapping opened is closed again.
    /// </summary>
    [Fact]
    public void DisposingInTheMiddleOfTheMappingWaitsForItsRunsAndClosesTheRiverWindow()
    {
        var world=new Landscape {Kind=3}; using var scope=new ValheimWorldScope(); scope.WithWorld(world);
        world.Preparer=Thread.CurrentThread.ManagedThreadId;
        var preparation=new RoadPreparation(p=>RoadIslandDetector.DetectSteps(world,p,_=>Assert.Fail("published"),minArea:1,worldRadius:256,options:new()));
        int oldLimit=RoadPreparation.CheckpointsPerTickForTests;
        RoadPreparation.CheckpointsPerTickForTests=1;
        try
        {
            while(preparation.Stage!="Mapping island terrain") Assert.False(preparation.Advance());
            world.Open.Reset();
            Assert.False(preparation.Advance());
            Assert.True(SpinWait.SpinUntil(()=>world.Held>0,5000),"no run reached the held half");
            Assert.True(SpinWait.SpinUntil(()=>RiverWeightReads.Indexed(world),5000),"the river window was not opened");
            var disposing=new Thread(preparation.Dispose) {IsBackground=true}; disposing.Start();
            Assert.False(disposing.Join(200),"the dispose did not wait for the held runs");
            world.Open.Set();
            Assert.True(disposing.Join(5000),"the dispose did not end once the runs were let go");
            int reads=world.Total;
            Thread.Sleep(100);
            Assert.Equal(reads,world.Total);
            Assert.False(RiverWeightReads.Indexed(world));
        }
        finally { world.Open.Set(); preparation.Dispose(); RoadPreparation.CheckpointsPerTickForTests=oldLimit; }
    }
    [Fact]
    public void ActiveTimingExcludesTimeBetweenFramesAndDisposalClosesNestedWork()
    {
        double clock=0; int disposed=0,visited=0;
        IEnumerable<bool> Work(RoadPreparation preparation)
        {
            try
            {
                preparation.BeginStage("Mapping test terrain",3,"cells"); yield return true;
                for(int i=0;i<3;i++) { clock+=2; visited++; preparation.Checkpoint(); yield return true; }
            }
            finally { disposed++; }
        }
        int oldLimit=RoadPreparation.CheckpointsPerTickForTests;
        RoadPreparation.CheckpointsPerTickForTests=1; RoadPreparation.ClockForTests=()=>clock;
        try
        {
            using var preparation=new RoadPreparation(Work);
            while(!preparation.Advance()) clock+=100;
            Assert.Equal(3,visited); Assert.Equal(6,preparation.ActiveMilliseconds);
            Assert.Equal(2,preparation.MaxSliceMilliseconds); Assert.True(preparation.WallMilliseconds>=400);
            preparation.Dispose(); preparation.Dispose(); Assert.Equal(1,disposed);
        }
        finally { RoadPreparation.CheckpointsPerTickForTests=oldLimit; RoadPreparation.ClockForTests=null; }
    }
    [Fact]
    public void CancelledIndexBuildNeverPublishesItsPartialDictionary()
    {
        var oldSource=RoadLocationProtection.Source;
        RoadLocationProtection.Source=null; RoadLocationProtection.Reset(); LocationPlacementHeights.Clear();
        var footprints=Enumerable.Range(0,1024).Select(i=>new RoadLocationProtection.Footprint(new Vector2(i*128,0),8)).ToArray();
        using var cancellation=new CancellationTokenSource();
        using var preparation=new RoadPreparation(p=>RoadLocationProtection.SetSteps(footprints,p));
        preparation.Cancellation=cancellation.Token;
        int oldLimit=RoadPreparation.CheckpointsPerTickForTests; RoadPreparation.CheckpointsPerTickForTests=1;
        try
        {
            Assert.False(preparation.Advance());
            cancellation.Cancel(); Assert.Throws<OperationCanceledException>(()=>preparation.Advance());
            preparation.Dispose();
            RoadLocationProtection.Source=null;
            Assert.False(RoadLocationProtection.Contains(new Vector2(0,0)));
        }
        finally { RoadPreparation.CheckpointsPerTickForTests=oldLimit; RoadLocationProtection.Reset(); RoadLocationProtection.Source=oldSource; }
    }
    internal static string Fingerprint(IReadOnlyList<Island> islands)
    {
        using var stream=new MemoryStream(); using(var writer=new BinaryWriter(stream,System.Text.Encoding.UTF8,true))
        {
            writer.Write(islands.Count);
            foreach(var i in islands)
            {
                writer.Write(i.Id);writer.Write(i.Center.x);writer.Write(i.Center.y);writer.Write(i.Min.x);writer.Write(i.Min.y);
                writer.Write(i.Max.x);writer.Write(i.Max.y);writer.Write(i.CellCount);writer.Write(i.ApproxArea);writer.Write(i.CellSize);writer.Write(i.WorldOffset);
                writer.Write(i.Cells.Count);foreach(var c in i.Cells){writer.Write(c.x);writer.Write(c.y);}
                writer.Write(i.CoastalLandings.Count);foreach(var c in i.CoastalLandings){writer.Write(c.x);writer.Write(c.y);writer.Write(c.z);}
            }
        }
        using var sha=SHA256.Create();return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-","").ToLowerInvariant();
    }
}
