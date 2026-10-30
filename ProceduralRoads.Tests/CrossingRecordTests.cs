using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A saved crossings record that holds a crossing this build never makes (a number that is not one, a span no bridge
/// has, a kind or style that does not exist, bytes past its crossings) is not read: no crossing reaches planning, the
/// roads load, and the record is written back as it was. Only loads here; nothing is planned from a bad record.
/// </summary>
public sealed class CrossingRecordTests : IDisposable
{
    private readonly ValheimWorldScope _world = new ValheimWorldScope().WithZdos();
    private const string Key = "ProceduralRoads_Crossings";

    public CrossingRecordTests()
    {
        RoadNetworkPersistence.Reset(); RoadSpatialGrid.Clear(); StairStructures.Reset();
        RoadNetworkPersistence.EnsureMetadataInstance();
        RoadSpatialGrid.CommitLevelled(new List<Vector2> { new(0, 0), new(8, 0), new(16, 0) }, new List<float> { 40, 40, 40 }, 4f);
        RoadSpatialGrid.FinalizeRoadNetwork();
        Save(new List<RoadCrossing>());
    }

    public void Dispose()
    {
        RoadNetworkPersistence.Reset(); RoadSpatialGrid.Clear(); StairStructures.Reset();
        _world.Dispose();
    }

    private static void Save(List<RoadCrossing> crossings) =>
        RoadNetworkPersistence.SaveGlobalRoadData(new List<(Vector2, string)>(), crossings, new HashSet<Vector2s>());

    private static List<RoadCrossing> Load()
    {
        RoadSpatialGrid.Clear();
        var loaded = new List<RoadCrossing>();
        Assert.True(RoadNetworkPersistence.TryLoadGlobalRoadData(new List<(Vector2, string)>(), loaded, new HashSet<Vector2s>()), "the roads did not load");
        return loaded;
    }

    /// <summary>One crossing in the current format: a 20 m wooden bridge, or the given fault in it.</summary>
    private static byte[] Record(string fault)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(RoadNetworkPersistence.CrossingsFormat); w.Write(1);
        w.Write(fault == "NaN bank" ? float.NaN : 0f); w.Write(0f);
        w.Write(fault == "huge span" ? 1_000_000_000f : fault == "outside the world" ? 20_000f : fault == "long valley" ? 120f : 20f); w.Write(0f);
        w.Write(fault == "infinite bed" ? float.PositiveInfinity : 20f);
        w.Write(10f); w.Write(0f); w.Write(fault == "negative fairway" ? -1f : 0f);
        w.Write(fault == "unknown kind" ? 99 : fault == "ford" ? (int)CrossingKind.Ford : (int)CrossingKind.Bridge);
        w.Write(fault == "unknown style" ? 99 : fault == "bridge with a ford style" ? (int)FordStyle.Wade : fault == "ford" ? (int)FordStyle.Raise : (int)FordStyle.None);
        w.Write(fault == "unknown flag" ? 2 : fault == "long valley" ? 1 : 0);
        w.Write((int)BridgeStructures.Kit.WoodRail + 1);
        // Its ends: none, road ends at a ford's banks, or a structure's (the near landed on its fourth row, the far met level
        // a quarter turn off the line), or the given fault in the near one.
        void End(int kind, Vector2 edge, Vector2 up, float height, int steps)
        {
            w.Write(kind); w.Write(edge.x); w.Write(edge.y); w.Write(up.x); w.Write(up.y); w.Write(height); w.Write(steps);
        }
        if (fault == "ford") { End(2, new Vector2(0f, 0f), new Vector2(1f, 0f), 30f, 0); End(2, new Vector2(20f, 0f), new Vector2(-1f, 0f), 30f, 0); }
        else if (fault == "valid" || fault == "long valley") { End(0, default, default, 0f, 0); End(0, default, default, 0f, 0); }
        else
        {
            End(fault == "unknown end" ? 3 : 1, new Vector2(0f, fault == "end off the line" ? 40f : 0f), fault == "end's way not a direction" ? new Vector2(2f, 0f) : new Vector2(1f, 0f),
                fault == "end at no height" ? float.NaN : 34f, fault == "more stairs than a kit lays" ? BridgeLayout.MaxStairSteps + 1 : 4);
            End(1, new Vector2(20f, 0f), new Vector2(0f, 1f), 34f, 0);
        }
        if (fault == "trailing bytes") w.Write(0x12345678);
        return ms.ToArray();
    }

    [Theory]
    [InlineData("NaN bank")]
    [InlineData("huge span")]
    [InlineData("outside the world")]
    [InlineData("infinite bed")]
    [InlineData("negative fairway")]
    [InlineData("unknown kind")]
    [InlineData("unknown style")]
    [InlineData("bridge with a ford style")]
    [InlineData("unknown flag")]
    [InlineData("unknown end")]
    [InlineData("end off the line")]
    [InlineData("end's way not a direction")]
    [InlineData("end at no height")]
    [InlineData("more stairs than a kit lays")]
    [InlineData("long valley")]
    [InlineData("trailing bytes")]
    public void AMalformedCrossingIsNotPublishedAndItsRecordIsKept(string fault)
    {
        byte[] bytes = Record(fault);
        RoadNetworkPersistence.SetSavedRecord(Key, bytes);

        List<RoadCrossing> loaded = Load();
        Assert.Empty(loaded);
        Assert.Contains("river crossings", RoadNetworkPersistence.KeptRecords);
        Assert.True(RoadSpatialGrid.GridCellsWithRoads > 0, "the readable roads load without their crossings");
        Save(loaded);
        Assert.Equal(bytes, RoadNetworkPersistence.SavedRecord(Key));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("ford")]
    [InlineData("landed")]
    public void AWellFormedCrossingLoads(string fault)
    {
        RoadNetworkPersistence.SetSavedRecord(Key, Record(fault));

        RoadCrossing crossing = Load().Single();
        Assert.Empty(RoadNetworkPersistence.KeptRecords);
        Assert.Equal(20f, crossing.Width);
        Assert.Equal(fault == "valid" ? null : fault == "ford" ? 0 : 4, crossing.FromEnd?.Steps);
        Assert.Equal(fault == "ford", crossing.FromEnd?.Road ?? false);
    }

    [Fact]
    public void TheLongestCrossingsTheNetworkMakesLoad()
    {
        var swampBridge = RoadCrossing.Between(new Vector2(0, 0), new Vector2(RoadNetworkPersistence.MaxCrossingSpan, 0), 20f, new Vector2(100, 0), 8f, CrossingKind.Bridge);
        var valley = RoadCrossing.Between(new Vector2(0, 100), new Vector2(DvergrViaduct.MaxValleySpan, 100), 60f, new Vector2(48, 100), 0f, CrossingKind.Bridge);
        valley.ValleyJump = true;
        Save(new List<RoadCrossing> { swampBridge, valley });

        List<RoadCrossing> loaded = Load();
        Assert.Equal(2, loaded.Count);
        Assert.True(loaded[1].ValleyJump);
        Assert.Empty(RoadNetworkPersistence.KeptRecords);
    }
}
