using System;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The console layer over manual roads: road_mark, road_path and road_connect as a host or server console runs them,
/// through the real ManualRoadCommands and the toolkit's console doubles. The planning and commit underneath have their
/// own tests (ManualRoadTests); these pin what the commands print, what they refuse, and that a refusal changes nothing.
/// </summary>
public sealed class ManualRoadCommandsTests : IDisposable
{
    /// <summary>An 800 m square of flat land in the sea: small enough for the command's own island detection.</summary>
    private sealed class SquareIsland : WorldGenerator
    {
        public override float GetHeight(float x, float z) => Mathf.Abs(x) < 400f && Mathf.Abs(z) < 400f ? 40f : -50f;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0; width = 0; }
        public override Heightmap.Biome GetBiome(float x, float z) => GetHeight(x, z) > 0f ? Heightmap.Biome.Meadows : Heightmap.Biome.Ocean;
    }

    private readonly ValheimWorldScope _world = new ValheimWorldScope()
        .WithWorld(new SquareIsland()).WithZdos().WithZoneSystem().WithScene().WithNetwork(server: true).WithCommands();
    private readonly Terminal _console = new();

    public ManualRoadCommandsTests()
    {
        RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetDebugCounters();
        RoadNetworkGenerator.MarkLocationsReady(); RoadNetworkGenerator.RoadWidth = 4;
        RoadSiteProtection.Set(Array.Empty<RoadSiteProtection.Footprint>());
        ManualRoadNetworkSync.Register();
        ManualRoadCommands.Register();
    }
    public void Dispose() { RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetDebugCounters(); _world.Dispose(); }

    private string[] Run(string line) { int from = _console.Output.Count; _console.TryRunCommand(line); return _console.Output.Skip(from).ToArray(); }

    private void BaseRoad()
    {
        var plan = RoadSpatialGrid.PlanRoadPath(new[] { new Vector2(-40, -16), new Vector2(40, -16) }.ToList(), 4, WorldGenerator.instance!);
        RoadSpatialGrid.Commit(plan!); RoadSpatialGrid.FinalizeRoadNetwork(); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
    }

    [Fact] public void TheThreeCommandsAreRegisteredAsCheats()
    {
        foreach (var name in new[] { "road_mark", "road_path", "road_connect" })
            Assert.True(Terminal.commands[name].IsCheat, name);
    }

    [Fact] public void MarksAreAddedListedUndoneAndClearedWithoutTouchingTheNetwork()
    {
        int version = RoadSpatialGrid.RoadNetworkVersion;
        Assert.Equal(new[] { "Mark 1: road_path 10,20" }, Run("road_mark add 10 20"));
        Assert.Equal(new[] { "Mark 2: road_path 10,20 -30.5,40" }, Run("road_mark add -30.5 40"));
        Assert.Equal(new[] { "1: X=10.00 Z=20.00", "2: X=-30.50 Z=40.00", "road_path 10,20 -30.5,40" }, Run("road_mark list"));
        Assert.Equal(new[] { "Last mark removed." }, Run("road_mark undo"));
        Assert.Equal(new[] { "Marks cleared. Built roads are unchanged." }, Run("road_mark clear"));
        Assert.Equal(new[] { "No marks.", "No marks." }, Run("road_mark list").Concat(Run("road_mark undo")));
        Assert.Equal(version, RoadSpatialGrid.RoadNetworkVersion);
    }

    [Theory]
    [InlineData("road_mark", "Road not added: Use road_mark add, list, undo, build or clear.")]
    [InlineData("road_mark list extra", "Road not added: Use road_mark list.")]
    [InlineData("road_mark add 10", "Road not added: Use X Z or player <peer-id>.")]
    [InlineData("road_mark add player seven", "Road not added: Use the connected player's numeric peer ID.")]
    [InlineData("road_mark add player 99", "Road not added: That peer has no connected character. Nothing marked or built.")]
    [InlineData("road_mark add", "Road not added: No local player. Supply X Z, or player <peer-id> from the server console.")]
    public void UsageMistakesArePrintedAndMarkNothing(string line, string expected)
    {
        Assert.Equal(new[] { expected }, Run(line));
        Assert.Equal(new[] { "No marks." }, Run("road_mark list"));
    }

    [Fact] public void AHostMarksWhereItStandsAndTheConsoleMarksWhereAPeersCharacterIs()
    {
        _world.WithLocalPlayer(new Vector3(12.5f, 40f, -7f));
        Assert.Equal(new[] { "Mark 1: road_path 12.5,-7" }, Run("road_mark add"));
        Player.m_localPlayer = null; // The server console marks the peer's position.
        var character = ZDOMan.instance!.CreateNewZDO(new Vector3(-3f, 40f, 44f), 1);
        ZNet.instance.Peers.Add(21, new ZNetPeer { m_characterID = character.m_uid });
        Assert.Equal(new[] { "Mark 1: road_path -3,44" }, Run("road_mark add player 21"));
    }

    [Fact] public void OffTheServerNothingRunsAndNothingChanges()
    {
        ZNet.instance.Server = false;
        int version = RoadSpatialGrid.RoadNetworkVersion;
        foreach (var line in new[] { "road_mark add 1 2", "road_path 0,40 80,40", "road_connect 80 80" })
            Assert.Equal(new[] { "Road not added: Run this command on the host or dedicated-server console." }, Run(line));
        ZNet.instance.Server = true;
        Assert.Equal(new[] { "No marks." }, Run("road_mark list"));
        Assert.Equal(version, RoadSpatialGrid.RoadNetworkVersion);
    }

    [Fact] public void BeforeTheWorldIsReadyNothingRuns()
    {
        ZDOMan.instance = null;
        Assert.Equal(new[] { "Road not added: The world is not ready." }, Run("road_mark add 1 2"));
    }

    [Fact] public void BuildingTheMarksAddsARoadPrintsItsCommandAndClearsTheDraft()
    {
        BaseRoad(); int appends = RoadSpatialGrid.AppendCount;
        Run("road_mark add 0 60"); Run("road_mark add 120 60");
        var output = Run("road_mark build");
        Assert.Equal("road_path 0,60 120,60", output[0]);
        Assert.StartsWith("Road added: ", output[1]);
        Assert.Equal(appends + 1, RoadSpatialGrid.AppendCount);
        Assert.Equal(new[] { "No marks." }, Run("road_mark list"));
    }

    [Fact] public void ARefusedPathAddsNothingAndSaysWhy()
    {
        BaseRoad(); int version = RoadSpatialGrid.RoadNetworkVersion;
        var output = Run("road_path 0,60 1000,60"); // Ends in the sea.
        Assert.StartsWith("Road not added: ", Assert.Single(output));
        Assert.Equal(version, RoadSpatialGrid.RoadNetworkVersion);
    }

    [Fact] public void ConnectJoinsOnceAndThenSaysItIsAlreadyConnected()
    {
        BaseRoad(); int appends = RoadSpatialGrid.AppendCount;
        Assert.StartsWith("Road added: ", Assert.Single(Run("road_connect 80 80")));
        Assert.Equal(appends + 1, RoadSpatialGrid.AppendCount);
        Assert.Equal(new[] { "Already connected to a road on this island. Nothing added." }, Run("road_connect 0 -16"));
        Assert.Equal(appends + 1, RoadSpatialGrid.AppendCount);
    }

    [Fact] public void ConnectWithoutANetworkIsRefused()
    {
        Assert.Equal(new[] { "Road not added: No road network exists on this island." }, Run("road_connect 80 80"));
    }
}
