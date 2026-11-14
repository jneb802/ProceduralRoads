using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

public sealed class SavedLocationIdentityTests : IDisposable
{
    private readonly ValheimWorldScope scope = new ValheimWorldScope().WithWorld(new FlatWorld()).WithZdos().WithZoneSystem().AsServer();
    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f;
    }

    public SavedLocationIdentityTests()
    {
        RoadNetworkGenerator.Reset();
        RoadNetworkPersistence.Reset();
        NetworkCutoff.Forget();
    }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        RoadNetworkPersistence.Reset();
        NetworkCutoff.Forget();
        scope.Dispose();
    }

    [Fact]
    public void APointWithNoLocationRoundTripsWithoutABindingWarning()
    {
        var plan = new RoadSpatialGrid.PlannedPath(new List<Vector2> { new(-8f, 0f), new(0f, 0f), new(8f, 0f) },
            new List<float> { 40f, 40f, 40f }, new List<RoadPointDebugInfo>(), RoadConstants.DefaultRoadWidth, 16f, false);
        RoadSpatialGrid.CommitRoad(new List<RoadSpatialGrid.PlannedPath> { plan }, intoLocation: true, ownChecked: true);
        RoadSpatialGrid.FinalizeRoadNetwork();
        Assert.All(RoadSpatialGrid.SnapshotAllRoadPoints(), p =>
        {
            Assert.True(p.intoLocation);
            Assert.True(float.IsNaN(p.location.x) && float.IsNaN(p.location.y));
        });
        byte[] saved = RoadSpatialGrid.SerializeAllRoadPoints()!;
        int version = RoadSpatialGrid.RoadNetworkVersion;
        var log = scope.CaptureLog();
        for (int load = 0; load < 2; load++)
        {
            RoadSpatialGrid.Clear();
            Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(saved), RoadSpatialGrid.LastReadRefusal);
            Assert.Equal(saved, RoadSpatialGrid.SerializeAllRoadPoints());
            Assert.Equal(version, RoadSpatialGrid.RoadNetworkVersion);
            Assert.All(RoadSpatialGrid.SnapshotAllRoadPoints(), p =>
            {
                Assert.True(float.IsNaN(p.location.x) && float.IsNaN(p.location.y));
                Assert.True(float.IsNaN(p.savedLocation.x) && float.IsNaN(p.savedLocation.y));
            });
        }
        Assert.DoesNotContain(log, l => l.Contains("saved road location"));
    }

    [Fact]
    public void AMissingSavedLocationKeepsTheNetworkAndItsBookkeepingAcrossRepeatedLoads()
    {
        var location = new Vector2(0f, 0f);
        var protectedZone = new Vector2s(3, 0);
        var roadZone = new Vector2s(0, 0);
        TestLocations.Add(ZoneSystem.instance!, new ZoneSystem.LocationInstance
        {
            m_position = new Vector3(location.x, 40f, location.y),
            m_location = new ZoneSystem.ZoneLocation
            { m_prefab = new SoftReferenceableAssets.SoftReference<GameObject>("TestLocation"), m_exteriorRadius = 12f },
        });
        NetworkCutoff.SnapshotWorld(new[] { protectedZone });
        NetworkCutoff.BeginNewNetwork();
        NetworkCutoff.UseNewNetwork();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(location, 12f) });
        PlacedLocations.Preload(location);
        var plan = new RoadSpatialGrid.PlannedPath(new List<Vector2> { new(-8f, 0f), new(0f, 0f), new(8f, 0f) },
            new List<float> { 40f, 40f, 40f }, new List<RoadPointDebugInfo>(), RoadConstants.DefaultRoadWidth, 16f, false)
            { Locations = new RoadLocationProtection.Targets(null, location) };
        RoadSpatialGrid.CommitRoad(new List<RoadSpatialGrid.PlannedPath> { plan }, intoLocation: true, ownChecked: true);
        RoadSpatialGrid.FinalizeRoadNetwork();
        RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        Assert.All(RoadSpatialGrid.SnapshotAllRoadPoints(), p => Assert.Equal(location, p.location));
        int version = RoadSpatialGrid.RoadNetworkVersion;
        byte[] saved = RoadSpatialGrid.SerializeAllRoadPoints()!;
        ZoneSystem.instance!.m_generatedZones.UnionWith(new[] { protectedZone, roadZone });
        RoadNetworkPersistence.EnsureMetadataInstance();
        Save();
        RoadNetworkGenerator.SaveNetworkCutoff();
        VegetationClearing.MarkCleared(roadZone, version);
        RoadNetworkGenerator.SaveClearedZones();
        ZoneSystem.instance.m_locationInstances.Clear();

        for (int load = 0; load < 2; load++)
        {
            RoadNetworkGenerator.Reset();
            NetworkCutoff.Forget();
            NetworkCutoff.SnapshotWorld(ZoneSystem.instance.m_generatedZones);
            var log = scope.CaptureLog();
            Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData(), RoadSpatialGrid.LastReadRefusal);
            RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
            Assert.Equal(version, RoadSpatialGrid.RoadNetworkVersion);
            Assert.True(NetworkCutoff.Excludes(protectedZone));
            Assert.False(NetworkCutoff.Excludes(roadZone));
            Assert.True(VegetationClearing.IsCleared(roadZone, version));
            Assert.Equal(saved, RoadSpatialGrid.SerializeAllRoadPoints());
            Assert.All(RoadSpatialGrid.SnapshotAllRoadPoints(), p => Assert.True(float.IsNaN(p.location.x)));
            Assert.Single(log.Where(l => l.Contains("saved road location") && l.Contains("not found")));
            Save();
        }

        // All hash callers read the same saved identities, including an append after a binding miss.
        RoadSpatialGrid.CommitAppendCuts(new[] { new Vector3(80f, 40f, 0f) }, StairLayout.CutWidth, version);
        int appendedVersion = RoadSpatialGrid.RoadNetworkVersion;
        Assert.NotEqual(version, appendedVersion);
        byte[] appended = RoadSpatialGrid.SerializeAllRoadPoints()!;
        RoadSpatialGrid.FinalizeRoadNetwork();
        Assert.Equal(appendedVersion, RoadSpatialGrid.RoadNetworkVersion);
        RoadSpatialGrid.Clear();
        Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(appended), RoadSpatialGrid.LastReadRefusal);
        Assert.Equal(appendedVersion, RoadSpatialGrid.RoadNetworkVersion);
        Assert.Equal(appended, RoadSpatialGrid.SerializeAllRoadPoints());
    }

    private static void Save() => RoadNetworkPersistence.SaveGlobalRoadData(
        Array.Empty<(Vector2, string)>(), Array.Empty<RoadCrossing>(), Array.Empty<Vector2s>());
}
