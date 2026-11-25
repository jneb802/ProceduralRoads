using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A world's history saved with its network (which zones were generated with it) that this build cannot read: the
/// roads load, the record is unknown rather than empty, so the conservative answer holds for the session, and every
/// save writes the record back as it was.
/// </summary>
public sealed class UnreadHistoryTests : IDisposable
{
    private readonly ValheimWorldScope _world = new ValheimWorldScope();
    private static readonly Vector2s Zone = new(0, 0);
    private const string CutoffKey = "ProceduralRoads_ZonesWithNetwork";

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
        NetworkCutoff.UseNewNetwork();
        RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(-8, 0), new(8, 0) }, 4f, WorldGenerator.instance);
        RoadSpatialGrid.FinalizeRoadNetwork();
        RoadNetworkPersistence.EnsureMetadataInstance();
        RoadNetworkPersistence.SetSavedRecord("ProceduralRoads_GlobalData", RoadSpatialGrid.SerializeAllRoadPoints()!);
        ZoneSystem.instance!.Generated.Add(Zone);
        RoadNetworkPersistence.SaveZonesWithNetwork(NetworkCutoff.Record(RoadSpatialGrid.RoadNetworkVersion, new[] { Zone })!);
    }

    /// <summary>A record of the history's own layout that this build cannot read: a format it does not know, or one
    /// cut short (one zone promised, none there).</summary>
    internal static byte[] Unreadable(bool unknownFormat)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(unknownFormat ? 99 : 1);
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
        byte[] unread = Unreadable(unknownFormat);
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
}
