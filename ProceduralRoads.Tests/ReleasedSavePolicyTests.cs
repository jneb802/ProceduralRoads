using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A network a released build saved, opened by this one, is saved through every entry point the same way
/// (<see cref="RoadNetworkPersistence.AllowsNetworkChanges"/>): its network, start points and crossings stay as that build wrote
/// them, no record of this build's built structures is written and no road is added, while the zones given their bridge
/// pieces are still saved. The same input in this build's own format is a control: there the crossings are written.
/// The network is the records of a world a 1.4.3 build saved, its network record cut down to its first cells as that build
/// wrote them (Fixtures/released-network/README.md).
/// </summary>
public sealed class ReleasedSavePolicyTests : IDisposable
{
    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0; width = 0; }
    }

    private const string Network = "ProceduralRoads_GlobalData";
    private const string Starts = "ProceduralRoads_StartPoints";
    private const string Crossings = "ProceduralRoads_Crossings";
    private static readonly string[] NewerRecords =
        { "ProceduralRoads_CoastLandings", "ProceduralRoads_BuiltBridges", "ProceduralRoads_BuiltStairs" };
    private static readonly Vector2s Built = new(2, -3);
    /// <summary>The cut-down network record's grid cells and stored points (Fixtures/released-network/README.md).</summary>
    private const int ReleasedCells = 4, ReleasedPoints = 232;

    private readonly ValheimWorldScope _world = new ValheimWorldScope().WithWorld(new FlatWorld()).WithZdos().WithZoneSystem().WithScene().AsServer();
    private readonly byte[] _network = Fixture("global-data.bin.gz");
    private readonly byte[] _starts = Fixture("start-points.bin");

    public ReleasedSavePolicyTests()
    {
        LegacyWorldTests.SetWorldDataLoaded(false);
        RoadNetworkPersistence.Reset(); RoadSpatialGrid.Clear(); RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld();
        StructurePlans.Reset(); NetworkCutoff.Forget();
    }

    public void Dispose()
    {
        LegacyWorldTests.SetWorldDataLoaded(false);
        NetworkCutoff.Forget(); StructurePlans.Reset();
        RoadNetworkPersistence.Reset(); RoadSpatialGrid.Clear(); RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld();
        _world.Dispose();
    }

    private static byte[] Fixture(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "released-network", name);
        if (!name.EndsWith(".gz", StringComparison.Ordinal)) return File.ReadAllBytes(path);
        using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }

    private static void SetGenerator(string field, bool value) =>
        typeof(RoadNetworkGenerator).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, value);

    /// <summary>The world opened with the released records on its metadata object; <paramref name="released"/> false puts
    /// the same network there in this build's own format instead.</summary>
    private byte[] Open(bool released)
    {
        byte[] network = _network;
        if (!released)
        {
            Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(_network));
            network = RoadSpatialGrid.SerializeAllRoadPoints()!;
            RoadSpatialGrid.Clear();
        }
        RoadNetworkPersistence.EnsureMetadataInstance();
        ZDO metadata = LegacyWorldTests.Metadata();
        metadata.Set(Network.GetStableHashCode(), network);
        metadata.Set(Starts.GetStableHashCode(), _starts);
        RoadNetworkPersistence.Reset();
        LegacyWorldTests.OpenWorld();
        Assert.True(RoadNetworkGenerator.RoadsLoadedFromZDO, "the saved network must load");
        Assert.False(RoadNetworkGenerator.RoadsGenerated, "a network was generated over the saved one");
        Assert.Equal(released, RoadSpatialGrid.LoadedFromReleasedFormat);
        return network;
    }

    /// <summary>The released records as they were saved, the network still read by 1.4.4 point for point, and no record
    /// a released build never wrote.</summary>
    private void AssertReleasedRecordsKept()
    {
        Assert.Equal(_network, RoadNetworkPersistence.SavedRecord(Network));
        Assert.Equal(_starts, RoadNetworkPersistence.SavedRecord(Starts));
        Assert.Null(RoadNetworkPersistence.SavedRecord(Crossings));
        foreach (string key in NewerRecords) Assert.Null(RoadNetworkPersistence.SavedRecord(key));
        Assert.True(Released144.Reads(RoadNetworkPersistence.SavedRecord(Network)!, out int read), "1.4.4 must still read the network");
        Assert.Equal(ReleasedPoints, read);
    }

    [Fact]
    public void TheReleasedRecordsAreTheirBuildsOwnBytes()
    {
        // The fixture is the save's own bytes, the network record cut down to its first cells with only its cell count
        // rewritten (Fixtures/released-network/released-network.sha256), format 1.
        static string Sha(byte[] bytes) { using var sha = SHA256.Create(); return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2"))); }
        Assert.Equal("c7c1449f89032ab81f4049ff4266bafd66e75384ed9279f0c758c19a3a23bab2", Sha(_network));
        Assert.Equal("297ae76bdb0b85c589aedbe3c5c8aef9f29f6e2c02cc6e0f9cdd33f3aa95cfed", Sha(_starts));
        Open(released: true);
        Assert.Equal(ReleasedPoints, RoadSpatialGrid.StoredPointRecords);
        Assert.Equal(ReleasedCells, RoadSpatialGrid.GridCellsWithRoads);
    }

    /// <summary>Every full save over the released network leaves it as saved, an addition refused first; in this build's
    /// format the same save writes the crossings.</summary>
    [Theory]
    [InlineData("lifecycle", true)]
    [InlineData("generator", true)]
    [InlineData("persistence", true)]
    [InlineData("lifecycle", false)]
    [InlineData("generator", false)]
    [InlineData("persistence", false)]
    public void EveryFullSaveKeepsAReleasedNetwork(string entry, bool released)
    {
        Open(released);
        if (released)
            Assert.Throws<InvalidOperationException>(() =>
                ManualRoads.Prepare(new[] { new Vector2(-20f, 100f), new Vector2(60f, 100f) }, false));

        SetGenerator("m_networkDirty", true); // as an append marks it
        switch (entry)
        {
            case "lifecycle": RoadLifecycleManager.OnPrepareSave(); break;
            case "generator": RoadNetworkGenerator.SaveGlobalRoadData(); break;
            default: RoadNetworkPersistence.SaveGlobalRoadData(new List<(Vector2, string)>(), new List<RoadCrossing>(), new HashSet<Vector2s>()); break;
        }

        if (!released)
        {
            Assert.NotNull(RoadNetworkPersistence.SavedRecord(Crossings));
            return;
        }
        AssertReleasedRecordsKept();
        // And the next session reads it as released again.
        RoadLifecycleManager.OnZoneSystemDestroy(ZoneSystem.instance);
        RoadNetworkPersistence.Reset(); RoadSpatialGrid.Clear();
        LegacyWorldTests.OpenWorld();
        Assert.True(RoadSpatialGrid.LoadedFromReleasedFormat);
        Assert.Equal(ReleasedPoints, RoadSpatialGrid.StoredPointRecords);
    }

    /// <summary>A bridge save over the released network keeps its crossings as saved and still saves the zones built;
    /// in this build's format the same save writes the crossings too.</summary>
    [Theory]
    [InlineData("generator", true)]
    [InlineData("persistence", true)]
    [InlineData("generator", false)]
    [InlineData("persistence", false)]
    public void EveryBridgeSaveKeepsAReleasedNetworksCrossingsAndSavesTheZonesBuilt(string entry, bool released)
    {
        Open(released);
        StructurePlans.MarkSpawned(Built);
        if (entry == "generator")
        {
            SetGenerator("m_kitsChosen", true); // a crossing's kit chosen as its bridge was built
            RoadNetworkGenerator.SaveBridgeZones();
        }
        else
            RoadNetworkPersistence.SaveBridgeZones(StructurePlans.SpawnedZones, new List<RoadCrossing>());

        if (released) AssertReleasedRecordsKept();
        else Assert.NotNull(RoadNetworkPersistence.SavedRecord(Crossings));

        // The zones built are the network's bookkeeping, saved either way.
        RoadSpatialGrid.Clear();
        var zones = new HashSet<Vector2s>();
        Assert.True(RoadNetworkPersistence.TryLoadGlobalRoadData(new List<(Vector2, string)>(), new List<RoadCrossing>(), zones));
        Assert.Equal(new[] { Built }, zones.ToArray());
    }
}
