using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A world whose saved road network this build cannot read takes no manual road: the operator's command is refused
/// before it changes anything, and the next save leaves the saved network byte for byte as it was. A world with no
/// network at all takes one, and so does a world whose unread network road_generate has replaced.
/// </summary>
public sealed class ManualRoadUnreadNetworkTests : IDisposable
{
    private sealed class SquareIsland : WorldGenerator
    {
        /// <summary>Every height read throws: a generation that fails part way.</summary>
        public bool Failing;
        public override float GetHeight(float x, float z) =>
            Failing ? throw new InvalidOperationException("the world failed") : Mathf.Abs(x) < 400f && Mathf.Abs(z) < 400f ? 40f : -50f;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0; width = 0; }
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = .02f, bool waterAlwaysOcean = false) =>
            GetHeight(x, z) > 0 ? Heightmap.Biome.Meadows : Heightmap.Biome.Ocean;
    }

    private const string GlobalKey = "ProceduralRoads_GlobalData";
    private readonly SquareIsland _island = new();
    private readonly ValheimWorldScope _world;

    public ManualRoadUnreadNetworkTests()
    {
        _world = new ValheimWorldScope().WithWorld(_island)
            .WithZdos().WithZoneSystem().WithScene().WithNetwork(server: true).WithCommands().WithCheats();
        RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld(); NetworkCutoff.Forget();
        RoadNetworkGenerator.RoadWidth = 4; RoadGrade.Configured = .35f;
        ManualRoadCommands.Register();
    }

    public void Dispose()
    {
        RoadLifecycleManager.OnZoneSystemDestroy(ZoneSystem.instance);
        RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld(); NetworkCutoff.Forget();
        _world.Dispose();
    }

    /// <summary>A network this build cannot read: a format it does not know, or a readable one cut short.</summary>
    private static byte[] Unreadable(bool unknownFormat)
    {
        if (unknownFormat) return new byte[] { 99, 0, 0, 0, 0, 0, 0, 0 };
        RoadSpatialGrid.CommitLevelled(new List<Vector2> { new(0, 0), new(8, 0), new(16, 0) }, new List<float> { 40, 40, 40 }, 4f);
        RoadSpatialGrid.FinalizeRoadNetwork();
        byte[] whole = RoadSpatialGrid.SerializeAllRoadPoints()!;
        RoadSpatialGrid.Clear();
        return whole.Take(whole.Length / 2).ToArray();
    }

    private static string RoadPath()
    {
        var console = new Terminal();
        console.TryRunCommand("road_path 0,60 120,60");
        return string.Join(" | ", console.Output);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AManualRoadIsRefusedAndTheUnreadNetworkSurvivesTheSave(bool unknownFormat)
    {
        RoadNetworkPersistence.EnsureMetadataInstance();
        byte[] unread = Unreadable(unknownFormat);
        RoadNetworkPersistence.SetSavedRecord(GlobalKey, unread);
        LegacyWorldTests.OpenWorld();
        Assert.True(RoadNetworkPersistence.LastLoadUnreadable);
        Assert.False(RoadNetworkGenerator.RoadsAvailable);

        string output = RoadPath();
        Assert.Contains("could not be read", output);
        Assert.Equal(0, RoadSpatialGrid.AppendCount);
        Assert.Equal(0, RoadSpatialGrid.StoredPointRecords);
        Assert.Equal(0, StructureAppendQueue.Count);
        Assert.False(RoadNetworkGenerator.NetworkDirty);

        RoadLifecycleManager.OnPrepareSave();
        Assert.Equal(unread, RoadNetworkPersistence.SavedRecord(GlobalKey));
    }

    [Fact]
    public void AWorldWithNoNetworkTakesAManualRoad()
    {
        // The load finds no saved network (the world-load decision would then generate one; the operator's road comes
        // first here).
        Assert.False(RoadNetworkGenerator.TryLoadGlobalRoadData());
        Assert.False(RoadNetworkPersistence.LastLoadUnreadable);
        RoadNetworkGenerator.MarkLocationsReady();

        string output = RoadPath();
        Assert.Equal(1, RoadSpatialGrid.AppendCount);
        Assert.Contains("Road added", output);
    }

    [Fact]
    public void ANetworkRoadGenerateReplacedTakesAManualRoad()
    {
        RoadNetworkPersistence.EnsureMetadataInstance();
        byte[] unread = Unreadable(unknownFormat: true);
        RoadNetworkPersistence.SetSavedRecord(GlobalKey, unread);
        LegacyWorldTests.OpenWorld();
        Assert.True(RoadNetworkPersistence.LastLoadUnreadable);

        RoadNetworkGenerator.GenerateRoads(force: true);
        Assert.True(RoadNetworkGenerator.RoadsGenerated);
        Assert.False(RoadNetworkPersistence.LastLoadUnreadable);

        string output = RoadPath();
        Assert.Equal(1, RoadSpatialGrid.AppendCount);
        Assert.Contains("Road added", output);
        RoadLifecycleManager.OnPrepareSave();
        Assert.NotEqual(unread, RoadNetworkPersistence.SavedRecord(GlobalKey));
    }

    [Fact]
    public void AFailedRoadGenerateKeepsARecordThatCouldNotBeRead()
    {
        // A readable network with a record this build cannot read. road_generate is asked to replace it and fails:
        // nothing replaced it, so the record is still the world's, still refuses a road added to it, and is saved as it was.
        RoadNetworkPersistence.EnsureMetadataInstance();
        RoadSpatialGrid.CommitLevelled(new List<Vector2> { new(0, 0), new(8, 0), new(16, 0) }, new List<float> { 40, 40, 40 }, 4f);
        RoadSpatialGrid.FinalizeRoadNetwork();
        RoadNetworkPersistence.SaveGlobalRoadData(new List<(Vector2, string)>(), new List<RoadCrossing>(), new HashSet<Vector2s>());
        RoadSpatialGrid.Clear();
        const string StairsKey = "ProceduralRoads_StairRuns";
        byte[] unread = { 99, 1, 2, 3, 4, 5, 6 };
        RoadNetworkPersistence.SetSavedRecord(StairsKey, unread);
        LegacyWorldTests.OpenWorld();
        Assert.True(RoadNetworkGenerator.RoadsAvailable);
        Assert.Single(RoadNetworkPersistence.KeptRecords);

        _island.Failing = true;
        Assert.ThrowsAny<Exception>(() => RoadNetworkGenerator.GenerateRoads(force: true));   // as road_generate runs it
        _island.Failing = false;
        Assert.False(RoadNetworkGenerator.RoadsGenerated);
        Assert.Single(RoadNetworkPersistence.KeptRecords);

        string output = RoadPath();
        Assert.Contains("could not be read", output);
        Assert.Equal(0, RoadSpatialGrid.AppendCount);
        RoadLifecycleManager.OnPrepareSave();
        Assert.Equal(unread, RoadNetworkPersistence.SavedRecord(StairsKey));

        // One that succeeds does replace it.
        RoadNetworkGenerator.GenerateRoads(force: true);
        Assert.True(RoadNetworkGenerator.RoadsGenerated);
        Assert.Empty(RoadNetworkPersistence.KeptRecords);
        RoadLifecycleManager.OnPrepareSave();
        Assert.NotEqual(unread, RoadNetworkPersistence.SavedRecord(StairsKey));
    }

    [Fact]
    public void AFailedRoadGenerateLeavesTheSavedNetworkAsItWas()
    {
        // A readable network; road_generate is asked to replace it and fails. The world's roads are still the saved ones:
        // no road is added over them, and the save leaves every one of them. A generation that succeeds replaces them.
        RoadNetworkPersistence.EnsureMetadataInstance();
        RoadSpatialGrid.CommitLevelled(new List<Vector2> { new(0, 0), new(8, 0), new(16, 0) }, new List<float> { 40, 40, 40 }, 4f);
        RoadSpatialGrid.FinalizeRoadNetwork();
        RoadNetworkPersistence.SaveGlobalRoadData(new List<(Vector2, string)>(), new List<RoadCrossing>(), new HashSet<Vector2s>());
        RoadSpatialGrid.Clear();
        byte[] saved = RoadNetworkPersistence.SavedRecord(GlobalKey)!;
        LegacyWorldTests.OpenWorld();
        Assert.True(RoadNetworkGenerator.RoadsAvailable);

        _island.Failing = true;
        Assert.ThrowsAny<Exception>(() => RoadNetworkGenerator.GenerateRoads(force: true));   // as road_generate runs it
        _island.Failing = false;
        Assert.False(RoadNetworkGenerator.RoadsAvailable);

        string output = RoadPath();
        Assert.Contains("loads again on the next spawn", output);
        Assert.Equal(0, RoadSpatialGrid.AppendCount);
        Assert.False(RoadNetworkGenerator.NetworkDirty);
        RoadLifecycleManager.OnPrepareSave();
        Assert.Equal(saved, RoadNetworkPersistence.SavedRecord(GlobalKey));

        // As the refusal says: the host's next spawn loads the saved network again.
        RoadLifecycleManager.OnPlayerSpawn(Vector3.zero);
        Assert.True(RoadNetworkGenerator.RoadsAvailable);
        Assert.Contains("Road added", RoadPath());

        RoadNetworkGenerator.GenerateRoads(force: true);
        Assert.True(RoadNetworkGenerator.RoadsGenerated);
        Assert.Contains("Road added", RoadPath());
        RoadLifecycleManager.OnPrepareSave();
        Assert.NotEqual(saved, RoadNetworkPersistence.SavedRecord(GlobalKey));
    }
}
