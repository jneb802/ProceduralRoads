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
        internal readonly HashSet<int> ReadThreads=new();
        internal int Reads;
        public override float GetHeight(float x,float z)
        {
            ReadThreads.Add(Thread.CurrentThread.ManagedThreadId); Reads++;
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
        var islands=RoadIslandDetector.Detect(world,worldRadius:256,minArea:1,options:new());
        Assert.Equal(expected,Fingerprint(islands));
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void TinySlicesRetainOriginalOrderedOutputAndDoNotPublishPartialIslands(int kind)
    {
        var world=new Landscape {Kind=kind}; using var scope=new ValheimWorldScope(); scope.WithWorld(world);
        var expected=RoadIslandDetector.Detect(world,worldRadius:256,minArea:1,options:new());
        List<Island>? result=null;
        using var preparation=new RoadPreparation(p=>RoadIslandDetector.DetectSteps(world,p,value=>result=value,minArea:1,worldRadius:256,options:new()));
        int oldLimit=RoadPreparation.CheckpointsPerTickForTests;
        RoadPreparation.CheckpointsPerTickForTests=1;
        var slices=new Dictionary<string,int>();
        try
        {
            while(!preparation.Advance())
            {
                Assert.Null(result);
                slices.TryGetValue(preparation.Stage,out int count); slices[preparation.Stage]=count+1;
                if(preparation.Total>0) Assert.InRange(preparation.Completed,0,preparation.Total);
            }
        }
        finally { RoadPreparation.CheckpointsPerTickForTests=oldLimit; }
        Assert.True(slices["Mapping island terrain"]>8,
            "The real raster must yield inside its loop, not only at phase boundaries.");
        Assert.Equal(Fingerprint(expected),Fingerprint(result!));
        Assert.Equal(new[] {Thread.CurrentThread.ManagedThreadId},world.ReadThreads);
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
        var oldSource=RoadSiteProtection.Source;
        RoadSiteProtection.Source=null; RoadSiteProtection.Reset(); LocationPlacementHeights.Clear();
        var footprints=Enumerable.Range(0,1024).Select(i=>new RoadSiteProtection.Footprint(new Vector2(i*128,0),8)).ToArray();
        using var cancellation=new CancellationTokenSource();
        using var preparation=new RoadPreparation(p=>RoadSiteProtection.SetSteps(footprints,p));
        preparation.Cancellation=cancellation.Token;
        int oldLimit=RoadPreparation.CheckpointsPerTickForTests; RoadPreparation.CheckpointsPerTickForTests=1;
        try
        {
            Assert.False(preparation.Advance());
            cancellation.Cancel(); Assert.Throws<OperationCanceledException>(()=>preparation.Advance());
            preparation.Dispose();
            RoadSiteProtection.Source=null;
            Assert.False(RoadSiteProtection.Contains(new Vector2(0,0)));
        }
        finally { RoadPreparation.CheckpointsPerTickForTests=oldLimit; RoadSiteProtection.Reset(); RoadSiteProtection.Source=oldSource; }
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
