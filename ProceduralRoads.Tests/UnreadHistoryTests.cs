using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A world's history saved with its network (which zones were generated with it, which were cleared of vegetation)
/// that this build cannot read: the roads load, the record is unknown rather than empty, so the conservative answer
/// holds for the session, and every save writes the record back as it was.
/// </summary>
public sealed class UnreadHistoryTests : IDisposable
{
    private readonly ValheimWorldScope _world = new ValheimWorldScope();
    private static readonly Vector2s Zone = new(0, 0);
    private const string CutoffKey = "ProceduralRoads_ZonesWithNetwork";
    private const string ClearedKey = "ProceduralRoads_ClearedZones";

    public UnreadHistoryTests()
    {
        _world.WithWorld(new SyntheticWorld { HasRiver = false, HasMountain = false })
            .WithZdos().WithZoneSystem().WithNetwork(server: true);
        RoadNetworkGenerator.Reset();
        RoadNetworkPersistence.Reset();
        NetworkCutoff.Forget();
    }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        RoadNetworkPersistence.Reset();
        NetworkCutoff.Forget();
        _world.Dispose();
    }

    /// <summary>A saved network with one zone generated with it in place, as an earlier session left it.</summary>
    private static void SaveReadableWorld()
    {
        NetworkCutoff.SnapshotWorld(Array.Empty<Vector2s>());
        NetworkCutoff.BeginNewNetwork(); NetworkCutoff.UseNewNetwork();
        RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(-8, 0), new(8, 0) }, 4f, WorldGenerator.instance);
        RoadSpatialGrid.FinalizeRoadNetwork();
        RoadNetworkPersistence.EnsureMetadataInstance();
        RoadNetworkPersistence.SetSavedRecord("ProceduralRoads_GlobalData", RoadSpatialGrid.SerializeAllRoadPoints()!);
        ZoneSystem.instance!.Generated.Add(Zone);
        RoadNetworkPersistence.SaveZonesWithNetwork(NetworkCutoff.Record(RoadSpatialGrid.RoadNetworkVersion, new[] { Zone })!);
        RoadNetworkPersistence.SaveClearedZones(RoadSpatialGrid.RoadNetworkVersion, new[] { Zone });
    }

    /// <summary>The bake's own question for a zone (ServerTerrainBake.ProcessVegetation): is a clearing pass owed?</summary>
    private static bool ClearingOwed(Vector2s zone) =>
        ServerBakePlanner.VegetationOwed(true, VegetationClearing.IsCleared(zone, RoadSpatialGrid.RoadNetworkVersion), NetworkCutoff.Excludes(zone));

    /// <summary>A record of the history's own layout that this build cannot read: a format it does not know, or one in
    /// the record's current <paramref name="format"/> cut short (one zone promised, none there).</summary>
    internal static byte[] Unreadable(bool unknownFormat, int format)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(unknownFormat ? 99 : format);
        w.Write(RoadSpatialGrid.RoadNetworkVersion);
        w.Write(1);
        return ms.ToArray();
    }

    /// <summary>The world is opened again: the save's generated zones, then the saved network.</summary>
    private static void Reload()
    {
        RoadNetworkGenerator.Reset();
        NetworkCutoff.Forget();
        NetworkCutoff.SnapshotWorld(ZoneSystem.instance!.m_generatedZones);
        Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData(), "the roads did not load");
        RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnUnreadCutoffRecordKeepsItsZonesAloneAndSurvivesSaves(bool unknownFormat)
    {
        SaveReadableWorld();
        byte[] unread = Unreadable(unknownFormat, format: 1);
        RoadNetworkPersistence.SetSavedRecord(CutoffKey, unread);

        Reload();
        Assert.True(NetworkCutoff.Excludes(Zone), "an unread record must leave the generated zone alone");
        RoadLifecycleManager.OnPrepareSave();
        RoadLifecycleManager.OnPrepareSave();
        Assert.Equal(unread, RoadNetworkPersistence.SavedRecord(CutoffKey));

        Reload();
        Assert.True(NetworkCutoff.Excludes(Zone));
        RoadLifecycleManager.OnPrepareSave();
        Assert.Equal(unread, RoadNetworkPersistence.SavedRecord(CutoffKey));
    }

    [Fact]
    public void AReadableCutoffRecordAdmitsItsZonesAndIsSavedAgain()
    {
        SaveReadableWorld();
        byte[] readable = RoadNetworkPersistence.SavedRecord(CutoffKey)!;

        Reload();
        Assert.False(NetworkCutoff.Excludes(Zone));
        Assert.Empty(RoadNetworkPersistence.KeptRecords);
        RoadLifecycleManager.OnPrepareSave();
        Assert.Equal(readable, RoadNetworkPersistence.SavedRecord(CutoffKey));
    }

    [Fact]
    public void NoCutoffRecordIsNotAnUnreadOneAndASaveWritesOne()
    {
        SaveReadableWorld();
        RoadNetworkPersistence.SetSavedRecord(CutoffKey, Array.Empty<byte>());

        Reload();
        Assert.True(NetworkCutoff.Excludes(Zone));
        Assert.Empty(RoadNetworkPersistence.KeptRecords);
        RoadLifecycleManager.OnPrepareSave();
        Assert.True(NetworkCutoff.TryRead(RoadNetworkPersistence.SavedRecord(CutoffKey), out int version, out HashSet<Vector2s> zones));
        Assert.Equal(RoadSpatialGrid.RoadNetworkVersion, version);
        Assert.Empty(zones);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnUnreadClearingRecordClearsNoZoneAgainAndSurvivesSaves(bool unknownFormat)
    {
        SaveReadableWorld();
        byte[] unread = Unreadable(unknownFormat, format: 2);
        RoadNetworkPersistence.SetSavedRecord(ClearedKey, unread);

        Reload();
        Assert.False(NetworkCutoff.Excludes(Zone), "the zone was generated with the network");
        Assert.False(ClearingOwed(Zone), "whether the zone was cleared is unknown, and a player may have planted since");
        // A later zone cleared in the session is not saved over the record.
        VegetationClearing.MarkCleared(new Vector2s(1, 0), RoadSpatialGrid.RoadNetworkVersion);
        RoadLifecycleManager.OnPrepareSave();
        Assert.Equal(unread, RoadNetworkPersistence.SavedRecord(ClearedKey));

        Reload();
        Assert.False(ClearingOwed(Zone));
        RoadLifecycleManager.OnPrepareSave();
        Assert.Equal(unread, RoadNetworkPersistence.SavedRecord(ClearedKey));
    }

    /// <summary>The clearing record as only development builds wrote it (format 1: no owed block), whole and naming the
    /// zone: not read, so kept as saved and the history unknown, exactly as for any record this build cannot read.</summary>
    [Fact]
    public void ADevelopmentClearingRecordIsRefusedAndKept()
    {
        SaveReadableWorld();
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        { w.Write(1); w.Write(RoadSpatialGrid.RoadNetworkVersion); w.Write(1); w.Write((int)Zone.x); w.Write((int)Zone.y); }
        byte[] development = ms.ToArray();
        RoadNetworkPersistence.SetSavedRecord(ClearedKey, development);
        List<string> log = _world.CaptureLog();
        try { Reload(); }
        finally { BepInEx.Logging.ManualLogSource.Captured = null; }
        Assert.Contains("zones cleared of vegetation", RoadNetworkPersistence.KeptRecords);
        Assert.Contains(log, l => l.Contains("The saved zones cleared of vegetation could not be read") && l.Contains("unknown cleared zone data format 1"));
        Assert.False(ClearingOwed(Zone), "whether the zone was cleared is unknown");
        RoadLifecycleManager.OnPrepareSave();
        Assert.Equal(development, RoadNetworkPersistence.SavedRecord(ClearedKey));
    }

    /// <summary>The clearing record round-trips in its one format, with and without zones owed only appends.</summary>
    [Fact]
    public void TheClearingRecordRoundTripsInItsOneFormat()
    {
        SaveReadableWorld();
        byte[] plain = RoadNetworkPersistence.SavedRecord(ClearedKey)!;
        Assert.Equal(2, BitConverter.ToInt32(plain, 0));
        Assert.Equal(0, BitConverter.ToInt32(plain, plain.Length - 4));   // no zone owed only appends
        Reload();
        Assert.Empty(RoadNetworkPersistence.KeptRecords);
        Assert.False(ClearingOwed(Zone));
        RoadLifecycleManager.OnPrepareSave();
        Assert.Equal(plain, RoadNetworkPersistence.SavedRecord(ClearedKey));

        var owed = new Dictionary<Vector2s, int> { [new Vector2s(1, 0)] = 3 };
        RoadNetworkPersistence.SaveClearedZones(RoadSpatialGrid.RoadNetworkVersion, new[] { Zone }, owed);
        byte[] withOwed = RoadNetworkPersistence.SavedRecord(ClearedKey)!;
        Reload();
        Assert.Empty(RoadNetworkPersistence.KeptRecords);
        Assert.Equal(owed, VegetationClearing.ClearedThrough);
        RoadNetworkGenerator.SaveClearedZones();
        Assert.Equal(withOwed, RoadNetworkPersistence.SavedRecord(ClearedKey));
    }

    [Fact]
    public void AReadableClearingRecordClearsOnlyTheZonesItDoesNotHold()
    {
        SaveReadableWorld();
        var other = new Vector2s(1, 0);
        ZoneSystem.instance!.Generated.Add(other);
        RoadNetworkPersistence.SaveZonesWithNetwork(NetworkCutoff.Record(RoadSpatialGrid.RoadNetworkVersion, new[] { Zone, other })!);

        Reload();
        Assert.False(ClearingOwed(Zone));
        Assert.True(ClearingOwed(other), "a zone the readable record does not hold is owed its pass");
    }

    [Fact]
    public void NoClearingRecordIsNotAnUnreadOne()
    {
        SaveReadableWorld();
        RoadNetworkPersistence.SetSavedRecord(ClearedKey, Array.Empty<byte>());

        Reload();
        Assert.Empty(RoadNetworkPersistence.KeptRecords);
        Assert.True(ClearingOwed(Zone));
    }
}
