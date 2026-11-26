using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

public sealed class RoadStatisticsTests : IDisposable
{
    private readonly ValheimWorldScope scope=new ValheimWorldScope().WithWorld(new SyntheticWorld());
    private readonly bool debug=RoadSpatialGrid.RecordPointDebugInfo;
    public RoadStatisticsTests() { scope.WithZdos().WithZoneSystem().WithNetwork(server:true); RoadNetworkPersistence.Reset(); RoadSpatialGrid.Clear(); RoadSpatialGrid.RecordPointDebugInfo=false; }
    public void Dispose() { RoadSpatialGrid.BeforeStatisticsPublication=null; RoadSpatialGrid.BeforeVersionCalculation=null; RoadSpatialGrid.BeforeAppendTransaction=null; RoadSpatialGrid.AfterAppendPublication=null; RoadNetworkGenerator.Reset(); RoadNetworkPersistence.Reset(); RoadSpatialGrid.RecordPointDebugInfo=debug; scope.Dispose(); }
    private static RoadSpatialGrid.PlannedPath Plan(float length=2)=>new(
        new List<Vector2> {new(31,0),new(33,0)},new List<float> {40,40},new List<RoadPointDebugInfo>(),4,length,false);
    private static void Commit()=>RoadSpatialGrid.Commit(Plan());
    private static byte[] Bytes()=>RoadSpatialGrid.SerializeAllRoadPoints()!;
    private static void AssertStatistics(int records,float? length)
    {
        var statistics=RoadSpatialGrid.Statistics;
        Assert.Equal(records,statistics.StoredPointRecords);
        Assert.Equal(records,RoadSpatialGrid.SnapshotAllRoadPoints().Count);
        Assert.Equal(length,statistics.CommittedPathLengthMeters);
        Assert.Equal(length.HasValue,statistics.LengthKnown);
    }
    [Fact]
    public void StoredRecordsIncludeCellCopiesRepeatedCommitsAndLevelledSupports()
    {
        Commit(); int once=RoadSpatialGrid.Statistics.StoredPointRecords;
        Assert.True(once>2,"Setup must produce copies across cell boundaries.");
        Commit(); AssertStatistics(once*2,4);
        RoadSpatialGrid.CommitLevelled(new[]{new Vector2(-1000,0),new Vector2(1000,0)},new[]{40f,40f},4);
        Assert.True(RoadSpatialGrid.StoredPointRecords>once*2);
        int records=RoadSpatialGrid.StoredPointRecords;
        RoadSpatialGrid.FinalizeRoadNetwork(); int version=RoadSpatialGrid.RoadNetworkVersion;
        byte[] saved=Bytes(); RoadSpatialGrid.Clear(); Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(saved));
        AssertStatistics(records,4); Assert.Equal(version,RoadSpatialGrid.RoadNetworkVersion); Assert.Equal(saved,Bytes());
    }
    [Fact]
    public void KnownLengthBitsAndAppendHistoryRoundTripWithoutInventingUniqueLength()
    {
        RoadSpatialGrid.Commit(Plan(123.4567f)); RoadSpatialGrid.FinalizeRoadNetwork();
        int parent=RoadSpatialGrid.RoadNetworkVersion;
        RoadSpatialGrid.CommitAppend(new[]{Plan(99.8765f)},parent);
        var before=RoadSpatialGrid.Statistics; byte[] saved=Bytes(); int version=RoadSpatialGrid.RoadNetworkVersion;
        RoadSpatialGrid.Clear(); Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(saved));
        AssertStatistics(before.StoredPointRecords,before.CommittedPathLengthMeters);
        Assert.Equal(BitConverter.GetBytes(before.CommittedPathLengthMeters!.Value),BitConverter.GetBytes(RoadSpatialGrid.CommittedPathLengthMeters!.Value));
        Assert.Equal(1,RoadSpatialGrid.AppendCount); Assert.Equal(version,RoadSpatialGrid.RoadNetworkVersion);
        Assert.Contains(RoadSpatialGrid.SnapshotAllRoadPoints(),p=>p.addition==1);
        Assert.Equal(saved,Bytes());
    }
    [Fact]
    public void LegacyNonemptyLoadReplacesPriorLengthWithUnknownAndAppendPreservesUnknown()
    {
        Commit(); Assert.True(RoadSpatialGrid.Statistics.LengthKnown);
        Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(Released()));
        AssertStatistics(1,null); Assert.True(float.IsNaN(RoadSpatialGrid.TotalRoadLength));
        Assert.Equal("unknown (legacy save)",RoadSpatialGrid.Statistics.LengthText());
        var before=Bytes(); Assert.Equal(4,BitConverter.ToInt32(before,0));
        Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(before)); AssertStatistics(1,null);
        RoadSpatialGrid.CommitAppend(new[]{Plan()},RoadSpatialGrid.RoadNetworkVersion);
        Assert.False(RoadSpatialGrid.Statistics.LengthKnown);
        before=Bytes(); RoadSpatialGrid.Clear(); Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(before));
        Assert.Null(RoadSpatialGrid.CommittedPathLengthMeters); Assert.Equal(before,Bytes());
    }
    [Fact]
    public void EmptyLegacyAndNewNetworksHaveKnownZeroLength()
    {
        AssertStatistics(0,0); Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(Released(true)));
        AssertStatistics(0,0); var saved=Bytes(); RoadSpatialGrid.Clear();
        Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(saved)); AssertStatistics(0,0);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)] [InlineData(12)]
    public void InvalidEnvelopeCannotReplaceGeometryStatisticsOrIdentity(int defect)
    {
        Commit(); RoadSpatialGrid.FinalizeRoadNetwork(); byte[] before=Bytes(); int version=RoadSpatialGrid.RoadNetworkVersion;
        byte[] bad=Envelope(defect);
        Assert.False(RoadSpatialGrid.DeserializeAllRoadPoints(bad));
        Assert.Equal(before,Bytes()); Assert.Equal(version,RoadSpatialGrid.RoadNetworkVersion);
    }
    [Fact]
    public async Task SnapshotAndSerializationWaitForCoherentCommitPublication()
    {
        using var entered=new ManualResetEventSlim(); using var release=new ManualResetEventSlim();
        using var reading=new ManualResetEventSlim(); using var serializing=new ManualResetEventSlim();
        RoadSpatialGrid.BeforeStatisticsPublication=()=> { entered.Set(); Assert.True(release.Wait(5000),"test did not release commit gate"); };
        Task writer=Task.Run(Commit);
        try
        {
            Assert.True(entered.Wait(3000));
            var snapshot=Task.Run(()=> { reading.Set(); return RoadSpatialGrid.Statistics; });
            var bytes=Task.Run(()=> { serializing.Set(); return Bytes(); });
            Assert.True(reading.Wait(3000)); Assert.True(serializing.Wait(3000));
            Assert.False(ReferenceEquals(snapshot,await Task.WhenAny(snapshot,Task.Delay(100))),"snapshot escaped the active geometry/statistics publication lock");
            Assert.False(ReferenceEquals(bytes,await Task.WhenAny(bytes,Task.Delay(100))),"serializer escaped the active geometry/statistics publication lock");
            release.Set(); await writer;
            var result=await snapshot; byte[] saved=await bytes;
            Assert.True(result.StoredPointRecords>2); Assert.Equal(2f,result.CommittedPathLengthMeters);
            RoadSpatialGrid.BeforeStatisticsPublication=null;
            RoadSpatialGrid.Clear(); Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(saved));
            AssertStatistics(result.StoredPointRecords,2);
        }
        finally { release.Set(); await writer; RoadSpatialGrid.BeforeStatisticsPublication=null; }
    }
    [Fact]
    public async Task AppendUsesTheBaseThatExistsWhenItsWriteTransactionBegins()
    {
        Commit(); RoadSpatialGrid.FinalizeRoadNetwork(); int version=RoadSpatialGrid.RoadNetworkVersion;
        int records=RoadSpatialGrid.StoredPointRecords;
        using var entered=new ManualResetEventSlim(); using var release=new ManualResetEventSlim();
        RoadSpatialGrid.BeforeAppendTransaction=()=> { entered.Set(); Assert.True(release.Wait(5000)); };
        var append=Task.Run(()=>RoadSpatialGrid.CommitAppend(new[]{Plan()},version));
        try
        {
            Assert.True(entered.Wait(3000));
            Commit(); // A real competing geometry/statistics transaction after append entered but before it acquired the lock.
            AssertStatistics(records*2,4);
            release.Set(); await append;
            AssertStatistics(records*3,6); Assert.Equal(1,RoadSpatialGrid.AppendCount);
            byte[] saved=Bytes(); RoadSpatialGrid.Clear(); Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(saved));
            AssertStatistics(records*3,6);
        }
        finally { release.Set(); await append; RoadSpatialGrid.BeforeAppendTransaction=null; }
    }
    [Fact]
    public async Task ASecondAppendCannotReuseTheParentOfAnAlreadyPublishedAppend()
    {
        Commit(); RoadSpatialGrid.FinalizeRoadNetwork(); int parent=RoadSpatialGrid.RoadNetworkVersion;
        int records=RoadSpatialGrid.StoredPointRecords;
        using var published=new ManualResetEventSlim(); using var release=new ManualResetEventSlim();
        int entries=0;
        RoadSpatialGrid.AfterAppendPublication=()=>
        {
            if(Interlocked.Increment(ref entries)==1) { published.Set(); Assert.True(release.Wait(5000)); }
        };
        var first=Task.Run(()=>RoadSpatialGrid.CommitAppend(new[]{Plan()},parent));
        try
        {
            Assert.True(published.Wait(3000));
            var stale=await Task.Run(()=>Record.Exception(()=>RoadSpatialGrid.CommitAppend(new[]{Plan()},parent)));
            Assert.IsType<InvalidOperationException>(stale);
            AssertStatistics(records*2,4); Assert.Equal(1,RoadSpatialGrid.AppendCount);
            release.Set(); await first;
            var saved=Bytes(); RoadSpatialGrid.Clear(); Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(saved));
            AssertStatistics(records*2,4); Assert.Equal(1,RoadSpatialGrid.AppendCount);
        }
        finally { release.Set(); await first; RoadSpatialGrid.AfterAppendPublication=null; }
    }
    [Fact]
    public void TheGlobalMetadataZdoCarriesTheStatisticsBlob()
    {
        Commit(); RoadSpatialGrid.FinalizeRoadNetwork(); var stats=RoadSpatialGrid.Statistics;
        var starts=new List<(Vector2,string)>(); var crossings=new List<RoadCrossing>(); var zones=new HashSet<Vector2s>();
        RoadNetworkPersistence.EnsureMetadataInstance(); RoadNetworkPersistence.SaveGlobalRoadData(starts,crossings,zones);
        var expected=Bytes(); int version=RoadSpatialGrid.RoadNetworkVersion;
        RoadSpatialGrid.Clear(); RoadNetworkPersistence.Reset();
        Assert.True(RoadNetworkPersistence.TryLoadGlobalRoadData(starts,crossings,zones));
        AssertStatistics(stats.StoredPointRecords,stats.CommittedPathLengthMeters);
        Assert.Equal(version,RoadSpatialGrid.RoadNetworkVersion); Assert.Equal(expected,Bytes());
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void HashFailureCannotPublishCandidateGeometryStatisticsOrHistory(bool append)
    {
        Commit(); RoadSpatialGrid.FinalizeRoadNetwork(); int version=RoadSpatialGrid.RoadNetworkVersion;
        var before=Bytes();
        RoadSpatialGrid.BeforeVersionCalculation=()=>throw new InvalidOperationException("controlled canonical hash failure");
        try
        {
            if(append) Assert.Throws<InvalidOperationException>(()=>RoadSpatialGrid.CommitAppend(new[]{Plan()},version));
            else Assert.False(RoadSpatialGrid.DeserializeAllRoadPoints(Released()));
            Assert.Equal(before,Bytes()); Assert.Equal(version,RoadSpatialGrid.RoadNetworkVersion);
            Assert.Equal(0,RoadSpatialGrid.AppendCount);
        }
        finally { RoadSpatialGrid.BeforeVersionCalculation=null; }
    }
    /// <summary>
    /// A world saved by 1.4.x (format 1) whose network holds a point that build never checked: the point is left out,
    /// the rest of the network loads, and the world is not handed to a fresh generation for one bad number. Any other
    /// format is this build's own and is still refused whole.
    /// </summary>
    [Theory]
    [InlineData("NaN height")] [InlineData("infinite x")] [InlineData("width 0")] [InlineData("negative width")]
    public void LegacyV1WithOneBadPointLoadsTheRest(string defect)
    {
        byte[] Blob(bool released)
        {
            using var stream=new MemoryStream(); using var w=new BinaryWriter(stream);
            Header(w,released); w.Write(2);
            w.Write(0); w.Write(0); w.Write(2);
            Point(w,1,2,4,40,released);
            Point(w,defect=="infinite x"?float.PositiveInfinity:3,4,defect=="width 0"?0:defect=="negative width"?-2:4,defect=="NaN height"?float.NaN:40,released);
            w.Write(5); w.Write(5); w.Write(1);
            Point(w,330,330,4,41,released);
            Tail(w,released);
            return stream.ToArray();
        }
        Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(Blob(true)));
        Assert.Equal(2,RoadSpatialGrid.StoredPointRecords);
        Assert.Equal(2,RoadSpatialGrid.GridCellsWithRoads);
        Assert.All(RoadSpatialGrid.SnapshotAllRoadPoints(),p=>Assert.True(ManualRoadDraft.IsFinite(p.h) && ManualRoadDraft.IsFinite(p.p.x) && p.w>0));
        Assert.NotEqual(0,RoadSpatialGrid.RoadNetworkVersion);
        RoadSpatialGrid.Clear();
        Assert.False(RoadSpatialGrid.DeserializeAllRoadPoints(Blob(false)),"this build's own format is refused whole");
    }
    /// <summary>
    /// A released build (format 1) read a cell listed twice as one cell, so its blob loads here with both lists. In this
    /// build's own formats a repeated cell is damage and the blob is refused whole.
    /// </summary>
    [Fact]
    public void ARepeatedCellLoadsInAReleasedBlobAndIsRefusedInOurOwn()
    {
        byte[] Blob(bool released)
        {
            using var stream=new MemoryStream(); using var w=new BinaryWriter(stream);
            Header(w,released); w.Write(2);
            for(int i=0;i<2;i++)
            {
                w.Write(0); w.Write(0); w.Write(1);
                Point(w,1f+i,2f,4f,40f,released);
            }
            Tail(w,released);
            return stream.ToArray();
        }
        Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(Blob(true)));
        Assert.Equal(2,RoadSpatialGrid.StoredPointRecords);
        Assert.Equal(1,RoadSpatialGrid.GridCellsWithRoads);
        RoadSpatialGrid.Clear();
        Assert.False(RoadSpatialGrid.DeserializeAllRoadPoints(Blob(false)));
        Assert.Contains("appears twice",RoadSpatialGrid.LastReadRefusal);
    }
    /// <summary>
    /// Every network, whatever it holds (road only, appended, cut, unpainted, a mound, into a location), is written in the one
    /// current format (4, encoding 8) and round-trips byte for byte with its flags and its identity. Any other format or encoding
    /// in the same place is refused (and the world then takes the unreadable path: nothing generated over it).
    /// </summary>
    [Fact]
    public void EveryNetworkWritesTheOneCurrentFormat()
    {
        void RoundTrip(Func<RoadSpatialGrid.RoadPoint,bool>? flag=null)
        {
            byte[] saved=Bytes(); int version=RoadSpatialGrid.RoadNetworkVersion;
            Assert.Equal(RoadSpatialGrid.SavedFormat,BitConverter.ToInt32(saved,0)); Assert.Equal(RoadSpatialGrid.SavedEncoding,BitConverter.ToInt32(saved,4));
            RoadSpatialGrid.Clear(); Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(saved),RoadSpatialGrid.LastReadRefusal);
            Assert.Equal(saved,Bytes()); Assert.Equal(version,RoadSpatialGrid.RoadNetworkVersion);
            if(flag!=null) Assert.Contains(RoadSpatialGrid.SnapshotAllRoadPoints(),p=>flag(p));
            foreach(int other in new[]{2,3,4,5,6,7,9})
            {
                byte[] unknown=(byte[])saved.Clone(); Array.Copy(BitConverter.GetBytes(other),0,unknown,4,4);
                Assert.False(RoadSpatialGrid.DeserializeAllRoadPoints(unknown),$"encoding {other}");
                Assert.Contains($"format 4, encoding {other}: a format this build does not know",RoadSpatialGrid.LastReadRefusal);
            }
            foreach(int other in new[]{2,3,5})
            {
                byte[] unknown=(byte[])saved.Clone(); Array.Copy(BitConverter.GetBytes(other),0,unknown,0,4);
                Assert.False(RoadSpatialGrid.DeserializeAllRoadPoints(unknown),$"format {other}");
            }
            Assert.Equal(saved,Bytes());
        }
        Commit(); RoadSpatialGrid.FinalizeRoadNetwork();
        RoundTrip(p=>!p.paintOnly&&!p.cutOnly&&!p.intoLocation&&!p.unpainted&&p.addition==0);
        RoadSpatialGrid.CommitAppend(new[]{Plan()},RoadSpatialGrid.RoadNetworkVersion);
        RoundTrip(p=>p.addition>0);
        RoadSpatialGrid.CommitAppendCuts(new[]{new Vector3(32,39,1)},2f,RoadSpatialGrid.RoadNetworkVersion);
        RoundTrip(p=>p.cutOnly);
        var under=Plan(); under.Unpainted=new List<bool>{false,true};
        RoadSpatialGrid.CommitAppend(new[]{under},RoadSpatialGrid.RoadNetworkVersion);
        RoundTrip(p=>p.unpainted&&p.IsRoad);
        RoadSpatialGrid.CommitAppend(new[]{new StructureGround.Mound(new List<Vector2>{new(40,4)},41f).Plan()},RoadSpatialGrid.RoadNetworkVersion);
        RoundTrip(p=>p.mound&&!p.IsRoad&&!p.unpainted);
        RoadSpatialGrid.CommitAppend(new[]{Plan()},RoadSpatialGrid.RoadNetworkVersion,intoLocation:true);
        RoundTrip(p=>p.intoLocation);
        Assert.Contains(RoadSpatialGrid.SnapshotAllRoadPoints(),p=>p.cutOnly);
        Assert.Contains(RoadSpatialGrid.SnapshotAllRoadPoints(),p=>p.intoLocation);
    }
    /// <summary>
    /// The road-point formats only development builds wrote (bare 2 and 3; format 4 with encodings 2, 3, 4, 6 and 7), each
    /// as those builds laid it out, are refused as unreadable with the reason; the released format 1 reads.
    /// </summary>
    [Theory]
    [InlineData(2,0)] [InlineData(3,0)] [InlineData(4,2)] [InlineData(4,3)] [InlineData(4,4)] [InlineData(4,6)] [InlineData(4,7)]
    public void ADevelopmentRoadFormatIsRefused(int format,int encoding)
    {
        int layout=format==4 ? encoding : format;
        using var stream=new MemoryStream(); using var w=new BinaryWriter(stream);
        w.Write(format); if(format==4) w.Write(encoding);
        w.Write(1); w.Write(0); w.Write(0); w.Write(1);
        w.Write(1f); w.Write(2f); w.Write(4f); w.Write(40f);
        w.Write(false);
        if(layout>=4) w.Write(false);
        if(layout>=6) { w.Write(false); w.Write(false); }
        if(layout>=3) w.Write(1);
        if(layout>=3) { w.Write(1); w.Write(77); }
        if(format==4) { w.Write((byte)0); w.Write(0f); }
        Assert.False(RoadSpatialGrid.DeserializeAllRoadPoints(stream.ToArray()));
        Assert.Equal(FormattableString.Invariant($"format {format}, encoding {(format==4?encoding:format)}: a format this build does not know (it reads the released format 1, and format 4 with encoding 8)"),RoadSpatialGrid.LastReadRefusal);
        Assert.False(RoadSpatialGrid.IsInitialized);
        Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(Released()));
    }
    /// <summary>
    /// A generated network with a staircase's cuts and nothing appended: an empty append block, the shape of every network
    /// generated with a staircase (a reader once demanded at least one append, and so refused its own saves).
    /// </summary>
    [Fact]
    public void ANetworkWithStaircaseCutsAndNoAppendsRoundTrips()
    {
        Commit();
        RoadSpatialGrid.CommitCuts(new[]{new Vector3(32,39,1)},2f);
        RoadSpatialGrid.FinalizeRoadNetwork();
        byte[] saved=Bytes(); int version=RoadSpatialGrid.RoadNetworkVersion;
        Assert.Equal(4,BitConverter.ToInt32(saved,0)); Assert.Equal(8,BitConverter.ToInt32(saved,4));
        Assert.DoesNotContain(RoadSpatialGrid.SnapshotAllRoadPoints(),p=>p.addition>0);
        Assert.Equal(0,BitConverter.ToInt32(saved,saved.Length-9));   // the append block's count, before the length record
        RoadSpatialGrid.Clear();
        Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(saved),RoadSpatialGrid.LastReadRefusal);
        Assert.Null(RoadSpatialGrid.LastReadRefusal);
        Assert.Equal(saved,Bytes()); Assert.Equal(version,RoadSpatialGrid.RoadNetworkVersion);
        Assert.Contains(RoadSpatialGrid.SnapshotAllRoadPoints(),p=>p.cutOnly);
        // Cut short, the refusal says where the read stopped.
        byte[] cut=saved.Take(saved.Length-3).ToArray();
        Assert.False(RoadSpatialGrid.DeserializeAllRoadPoints(cut));
        Assert.StartsWith("format 4, encoding 8: EndOfStreamException at byte ",RoadSpatialGrid.LastReadRefusal);
        Assert.Contains($" of {cut.Length}:",RoadSpatialGrid.LastReadRefusal);
        Assert.DoesNotContain("at byte 0 ",RoadSpatialGrid.LastReadRefusal);
    }
    /// <summary>A network as a released build (format 1) wrote it: one cell, one point.</summary>
    internal static byte[] Released(bool empty=false)
    {
        using var stream=new MemoryStream(); using var writer=new BinaryWriter(stream);
        Header(writer,true); Geometry(writer,true,empty);
        return stream.ToArray();
    }
    /// <summary>A record's leading numbers: the released format 1, or this build's format 4 with encoding 8.</summary>
    private static void Header(BinaryWriter w,bool released)
    { w.Write(released?1:RoadSpatialGrid.SavedFormat); if(!released) w.Write(RoadSpatialGrid.SavedEncoding); }
    /// <summary>A plain road point: the released layout, or this build's (every flag false, no addition).</summary>
    private static void Point(BinaryWriter w,float x,float z,float width,float h,bool released)
    {
        w.Write(x); w.Write(z); w.Write(width); w.Write(h);
        if(!released) { w.Write(false); w.Write(false); w.Write(false); w.Write(false); w.Write(false); w.Write(0); }
    }
    /// <summary>What follows the cells in this build's format: an empty append block and an unknown length.</summary>
    private static void Tail(BinaryWriter w,bool released)
    { if(!released) { w.Write(0); w.Write((byte)0); w.Write(0f); } }
    private static void Geometry(BinaryWriter writer,bool released,bool empty=false,bool duplicate=false,bool badAddition=false,bool duplicateParents=false)
    {
        writer.Write(empty?0:duplicate?2:1);
        if(!empty)
            for(int cell=0;cell<(duplicate?2:1);cell++)
            {
                writer.Write(0); writer.Write(0); writer.Write(1);
                writer.Write(1f); writer.Write(2f); writer.Write(4f); writer.Write(40f);
                if(!released) { writer.Write(true); writer.Write(false); writer.Write(false); writer.Write(false); writer.Write(false); writer.Write(badAddition?2:1); }
            }
        if(!released) { writer.Write(duplicateParents?2:1); writer.Write(77); if(duplicateParents) writer.Write(77); }
    }
    private static byte[] Envelope(int defect)
    {
        using var stream=new MemoryStream(); using var writer=new BinaryWriter(stream);
        writer.Write(RoadSpatialGrid.SavedFormat); writer.Write(defect==0?1:RoadSpatialGrid.SavedEncoding);
        Geometry(writer,false,empty:defect==10,duplicate:defect==7,badAddition:defect==11,duplicateParents:defect==12);
        writer.Write((byte)(defect==1?2:defect==2 || defect==9?0:1));
        writer.Write(defect==2?5f:defect==3?-1f:defect==4?float.NaN:defect==5?float.PositiveInfinity:defect==9?BitConverter.ToSingle(new byte[]{0,0,0,128},0):5f);
        if(defect==8) writer.Write((byte)0);
        var result=stream.ToArray(); return defect==6?result.Take(result.Length-1).ToArray():result;
    }
}
