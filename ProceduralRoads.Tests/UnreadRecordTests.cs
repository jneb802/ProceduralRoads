using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProceduralRoads;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

/// <summary>
/// A saved record of the network this build cannot read is logged as an error, kept as saved and written back
/// unchanged on every save, with nothing new under its key; the roads load without it.
/// </summary>
public class UnreadRecordTests : System.IDisposable
{
    private readonly ValheimWorldScope _world = new ValheimWorldScope();

    public void Dispose()
    {
        RoadNetworkPersistence.Reset(); RoadSpatialGrid.Clear(); StructureAppendQueue.Reset(); StairStructures.Reset(); BuiltBridges.Reset(); BuiltStairs.Reset();
        RoadNetworkGenerator.RestoreReachedLocations();
        _world.Dispose();
    }

    private static void Save() => RoadNetworkPersistence.SaveGlobalRoadData(new List<(Vector2, string)>(), new List<RoadCrossing>(), new HashSet<Vector2s>());
    private static bool Load() => RoadNetworkPersistence.TryLoadGlobalRoadData(new List<(Vector2, string)>(), new List<RoadCrossing>(), new HashSet<Vector2s>());

    [Theory]
    [InlineData("ProceduralRoads_StairRuns")]
    [InlineData("ProceduralRoads_BuiltStairs")]
    public void ADevelopmentWoodStairRecordIsKeptUnread(string key)
    {
        _world.WithZdos();
        RoadNetworkPersistence.Reset(); RoadSpatialGrid.Clear(); StairStructures.Reset(); BuiltStairs.Reset();
        RoadNetworkPersistence.EnsureMetadataInstance();
        RoadSpatialGrid.CommitLevelled(new[] { new Vector2(0, 0), new Vector2(8, 0) }, new[] { 40f, 40f }, 4f);
        RoadSpatialGrid.FinalizeRoadNetwork();
        var run = new StairRun { From = new Vector2(0, 0), To = new Vector2(0, 2), Width = 2f, Kit = StairKit.Stone };
        run.Pieces.Add(new StructurePiece { Kind = StructurePieceKind.StairFloor, Prefab = StairKit.Stone.Floor, Position = new Vector3(0, 40, 1) });
        StairStructures.Add(run);
        Assert.NotNull(BuiltStairs.Keep(run, StairKit.Stone, run.Pieces, world: null));
        Save();
        byte[] saved = RoadNetworkPersistence.SavedRecord(key)!;
        if (key == "ProceduralRoads_StairRuns")
        {
            using var input = new MemoryStream(saved);
            using var reader = new BinaryReader(input);
            reader.ReadInt32(); reader.ReadInt32(); // format, run count
            for (int i = 0; i < 5; i++) reader.ReadSingle(); // ends, width
            reader.ReadBoolean();
            int kitAt = (int)input.Position;
            Assert.Equal("stone", reader.ReadString());
            int rest = (int)input.Position;
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output);
            writer.Write(saved, 0, kitAt); writer.Write("wood"); writer.Write(saved, rest, saved.Length - rest);
            saved = output.ToArray();
        }
        else
        {
            // Format and count, four endpoint floats, then the retired development kit's record number.
            const int kitAt = 6 * sizeof(int);
            Assert.Equal(StairKit.Stone.Number, System.BitConverter.ToInt32(saved, kitAt));
            System.BitConverter.GetBytes(3).CopyTo(saved, kitAt);
        }
        RoadNetworkPersistence.SetSavedRecord(key, saved);
        StairStructures.Reset(); BuiltStairs.Reset(); RoadSpatialGrid.Clear();
        Assert.True(Load());
        if (key == "ProceduralRoads_StairRuns") Assert.Empty(StairStructures.Runs);
        else Assert.Empty(BuiltStairs.Sites);
        Assert.Single(RoadNetworkPersistence.KeptRecords);
        Save(); Save();
        Assert.Equal(saved, RoadNetworkPersistence.SavedRecord(key));
    }

    [Theory]
    [InlineData("ProceduralRoads_StairRuns")]
    [InlineData("ProceduralRoads_BuiltBridges")]
    [InlineData("ProceduralRoads_BuiltStairs")]
    [InlineData("ProceduralRoads_HiddenSiteIcons")]
    [InlineData("ProceduralRoads_AppendBridges")]
    [InlineData("ProceduralRoads_Crossings")]
    [InlineData("ProceduralRoads_BridgeZones")]
    [InlineData("ProceduralRoads_StartPoints")]
    [InlineData("ProceduralRoads_ReachedPlaces")]
    public void ARecordThatCannotBeReadIsWrittenBackAsItWas(string key)
    {
        _world.WithZdos();
        RoadNetworkPersistence.Reset(); RoadSpatialGrid.Clear(); StructureAppendQueue.Reset();
        RoadNetworkPersistence.EnsureMetadataInstance();
        RoadSpatialGrid.CommitLevelled(new List<Vector2> { new(0, 0), new(8, 0), new(16, 0) }, new List<float> { 40, 40, 40 }, 4f);
        RoadSpatialGrid.FinalizeRoadNetwork();
        Save();
        byte[] damaged = { 99, 1, 2, 3, 4, 5, 6 };
        RoadNetworkPersistence.SetSavedRecord(key, damaged);

        RoadSpatialGrid.Clear();
        Assert.True(Load(), "the roads did not load");
        if (key == "ProceduralRoads_StairRuns") Assert.True(RoadNetworkPersistence.StairRecordKept);
        Assert.Single(RoadNetworkPersistence.KeptRecords);
        Save();
        Save();

        Assert.Equal(damaged, RoadNetworkPersistence.SavedRecord(key));
    }

    [Fact]
    public void AnUnreadArrivalRecordIsReplacedAfterTheArrivalStateIsRewritten()
    {
        _world.WithZdos();
        RoadNetworkGenerator.Reset(); RoadNetworkPersistence.Reset();
        RoadNetworkPersistence.EnsureMetadataInstance();
        RoadSpatialGrid.CommitLevelled(new[] { new Vector2(0, 0), new Vector2(8, 0) }, new[] { 40f, 40f }, RoadConstants.DefaultRoadWidth);
        RoadSpatialGrid.FinalizeRoadNetwork();
        Save();
        const string key = "ProceduralRoads_ReachedPlaces";
        byte[] damaged = { 99, 1, 2, 3, 4, 5, 6 };
        RoadNetworkPersistence.SetSavedRecord(key, damaged);
        RoadSpatialGrid.Clear();
        Assert.True(Load());
        Save();
        Assert.Equal(damaged, RoadNetworkPersistence.SavedRecord(key));
        var arrivals = new[] { (new Vector2(16f, 0f), true, true) };
        RoadNetworkGenerator.RestoreReachedLocations(arrivals);
        Save();
        Assert.NotEqual(damaged, RoadNetworkPersistence.SavedRecord(key));
        RoadSpatialGrid.Clear();
        Assert.True(Load());
        Assert.Empty(RoadNetworkPersistence.KeptRecords);
        Assert.Equal(arrivals, RoadNetworkGenerator.ReachedLocations());
    }

    [Fact]
    public void ABridgeZoneRecordThatCannotBeReadIsRecordedAfreshFromThisSession()
    {
        // The spawned-zone record is a cache: kept unread, every later session's zones would be lost with it.
        _world.WithZdos();
        RoadNetworkPersistence.Reset(); RoadSpatialGrid.Clear(); StructureAppendQueue.Reset();
        RoadNetworkPersistence.EnsureMetadataInstance();
        RoadSpatialGrid.CommitLevelled(new List<Vector2> { new(0, 0), new(8, 0), new(16, 0) }, new List<float> { 40, 40, 40 }, 4f);
        RoadSpatialGrid.FinalizeRoadNetwork();
        Save();
        byte[] damaged = { 2, 0, 0, 0, 1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF };   // format 2, layout 1, a negative count
        RoadNetworkPersistence.SetSavedRecord("ProceduralRoads_BridgeZones", damaged);

        RoadSpatialGrid.Clear();
        var zones = new HashSet<Vector2s>();
        Assert.True(RoadNetworkPersistence.TryLoadGlobalRoadData(new List<(Vector2, string)>(), new List<RoadCrossing>(), zones));
        Assert.Empty(RoadNetworkPersistence.KeptRecords);
        Assert.Empty(zones);
        zones.Add(new Vector2s(3, 4));
        RoadNetworkPersistence.SaveBridgeZones(zones);
        var again = new HashSet<Vector2s>();
        RoadSpatialGrid.Clear();
        Assert.True(RoadNetworkPersistence.TryLoadGlobalRoadData(new List<(Vector2, string)>(), new List<RoadCrossing>(), again));
        Assert.Equal(zones, again);
    }

    [Fact]
    public void ARecordWhoseTextCannotBeReadIsKeptLikeAnyOther()
    {
        // A malformed string length (BinaryReader.ReadString throws FormatException) is one more way a record cannot be
        // read: the record is kept, the roads load.
        _world.WithZdos();
        RoadNetworkPersistence.Reset(); RoadSpatialGrid.Clear(); StructureAppendQueue.Reset();
        RoadNetworkPersistence.EnsureMetadataInstance();
        RoadSpatialGrid.CommitLevelled(new List<Vector2> { new(0, 0), new(8, 0), new(16, 0) }, new List<float> { 40, 40, 40 }, 4f);
        RoadSpatialGrid.FinalizeRoadNetwork();
        Save();
        var damaged = new List<byte>();
        damaged.AddRange(System.BitConverter.GetBytes(3)); damaged.AddRange(System.BitConverter.GetBytes(1));
        damaged.AddRange(new byte[16]); damaged.AddRange(System.BitConverter.GetBytes(2f)); damaged.Add(0);
        damaged.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });   // the kit's name: no 7-bit length ends
        RoadNetworkPersistence.SetSavedRecord("ProceduralRoads_StairRuns", damaged.ToArray());

        RoadSpatialGrid.Clear();
        Assert.True(Load(), "the roads did not load");
        Assert.Single(RoadNetworkPersistence.KeptRecords);
    }
}
