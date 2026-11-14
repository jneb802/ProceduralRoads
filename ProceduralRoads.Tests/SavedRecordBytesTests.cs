using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>The bytes the zone-list records save, which worlds saved by released builds fix: little-endian 32-bit
/// ints, a zone as two of them. And that every save claims the metadata object it writes to.</summary>
public sealed class SavedRecordBytesTests : IDisposable
{
    private readonly ValheimWorldScope _world = new ValheimWorldScope();

    public SavedRecordBytesTests()
    {
        _world.WithWorld(new SyntheticWorld { HasRiver = false, HasMountain = false })
            .WithZdos().WithZoneSystem().WithNetwork(server: true);
        RoadNetworkGenerator.Reset();
        RoadNetworkPersistence.Reset();
        RoadNetworkPersistence.EnsureMetadataInstance();
    }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        RoadNetworkPersistence.Reset();
        _world.Dispose();
    }

    private static byte[] Ints(params int[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();

    private static readonly Vector2s[] Zones = { new(1, -2), new(-300, 4) };

    [Fact]
    public void TheBridgeZonesRecordIsVersionLayoutCountThenAZonePerPairOfInts()
    {
        RoadNetworkPersistence.SaveBridgeZones(Zones);
        Assert.Equal(Ints(2, BridgeLayout.LayoutVersion, 2, 1, -2, -300, 4), RoadNetworkPersistence.SavedRecord("ProceduralRoads_BridgeZones"));
    }

    [Fact]
    public void TheClearedZonesRecordIsFormatVersionCountZones()
    {
        RoadNetworkPersistence.SaveClearedZones(7, Zones);
        Assert.Equal(Ints(2, 7, 2, 1, -2, -300, 4, 0), RoadNetworkPersistence.SavedRecord("ProceduralRoads_ClearedZones"));
    }

    [Fact]
    public void ZonesOwedOnlyAppendsAreListedWithTheirThroughVersion()
    {
        RoadNetworkPersistence.SaveClearedZones(7, Zones, new Dictionary<Vector2s, int> { [new Vector2s(5, 6)] = 3 });
        Assert.Equal(Ints(2, 7, 2, 1, -2, -300, 4, 1, 5, 6, 3), RoadNetworkPersistence.SavedRecord("ProceduralRoads_ClearedZones"));
    }

    [Fact]
    public void EverySaveClaimsTheMetadataObjectItWritesTo()
    {
        var zdo = ZDOMan.instance!.m_objectsByID.Values.Single(z => z.GetPosition().x > 40000f);
        foreach (Action save in new Action[]
                 {
                     () => RoadNetworkPersistence.SaveBridgeZones(Zones),
                     () => RoadNetworkPersistence.SaveClearedZones(7, Zones),
                     () => RoadNetworkPersistence.SaveZonesWithNetwork(new byte[] { 1 }),
                     () => RoadNetworkPersistence.SaveFirstLoadSkipped(),
                     () => RoadNetworkPersistence.SaveGlobalRoadData(new List<(Vector2, string)>(), new List<RoadCrossing>(), Zones),
                 })
        {
            zdo.SetOwner(99);
            Assert.False(zdo.IsOwner());
            save();
            Assert.True(zdo.IsOwner());
        }
    }
}
