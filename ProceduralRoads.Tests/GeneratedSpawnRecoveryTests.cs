using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A generated structure plan (a staircase here) whose prefab the scene does not have yet when its zone first spawns:
/// the zone is not recorded as having its pieces, and gets them, once, when the prefab is there. Through both the Full
/// and the Ghost entry points. A zone with nothing owed (a client, a zone explored before the network) is not recorded
/// either, and a zone whose pieces a player took does not get them back.
/// </summary>
public sealed class GeneratedSpawnRecoveryTests : IDisposable
{
    private readonly ValheimWorldScope _world = new ValheimWorldScope()
        .WithWorld(new BridgeTests.WideRiverWorld()).WithZdos().WithZoneSystem().WithScene().AsServer();
    private static readonly Vector2s Zone = new(0, 0);
    private const string Prefab = "wood_stair";

    public GeneratedSpawnRecoveryTests()
    {
        RoadNetworkGenerator.Reset();
        NetworkCutoff.Forget();
        NetworkCutoff.SnapshotWorld(Array.Empty<Vector2s>());
        NetworkCutoff.UseNewNetwork();
        var run = new StairRun { From = new Vector2(0, 0), To = new Vector2(2, 0), Width = 4f };
        run.Pieces.Add(new StructurePiece { Prefab = Prefab, Kind = StructurePieceKind.StairStep, Position = new Vector3(0, 40, 0) });
        StairStructures.Add(run);
    }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        NetworkCutoff.Forget();
        _world.Dispose();
    }

    private static int Spawn(bool ghost) => ghost
        ? StructurePlacement.SpawnGhostInZone(Zone)
        : StructurePlacement.OnZoneSpawned(Zone, ZoneSystem.SpawnMode.Full);

    private static List<ZDO> Pieces() => ZDOMan.instance!.Zdos
        .Where(z => z.GetInt(StructurePlans.MarkerHash) == 1 && !ZDOMan.instance.DestroyQueue.Contains(z)).ToList();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APlanWaitingOnAMissingPrefabIsBuiltOnceItIsThere(bool ghost)
    {
        Assert.Single(StructurePlans.BuildablePlanFor(Zone)!);
        Assert.Null(ZNetScene.instance!.GetPrefab(Prefab));

        Assert.Equal(0, Spawn(ghost));
        Assert.Empty(Pieces());
        Assert.False(StructurePlans.IsSpawned(Zone), "a zone that got none of its pieces is not done");

        ZNetScene.instance.AddPrefab(Prefab);
        Assert.Equal(1, Spawn(ghost));
        Assert.True(StructurePlans.IsSpawned(Zone));
        Assert.Equal(0, Spawn(ghost));
        Assert.Single(Pieces());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PiecesAPlayerTookDoNotComeBack(bool ghost)
    {
        ZNetScene.instance!.AddPrefab(Prefab);
        Assert.Equal(1, Spawn(ghost));
        ZDOMan.instance!.DestroyZDO(Pieces().Single());

        Assert.Equal(0, Spawn(ghost));
        Assert.Empty(Pieces());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AZoneWithNothingOwedIsNotRecorded(bool client)
    {
        ZNetScene.instance!.AddPrefab(Prefab);
        if (client) ZNet.instance!.Server = false;
        else { NetworkCutoff.SnapshotWorld(new[] { Zone }); NetworkCutoff.UseNewNetwork(); }

        Assert.Equal(0, StructurePlacement.SpawnGhostInZone(Zone));
        Assert.Equal(0, StructurePlacement.OnZoneSpawned(Zone, ZoneSystem.SpawnMode.Full));
        Assert.False(StructurePlans.IsSpawned(Zone));
        Assert.Empty(Pieces());
    }

    /// <summary>Every live ZDO, marked as the mod's piece or not.</summary>
    private static List<ZDO> Live() => ZDOMan.instance!.Zdos.Where(z => !ZDOMan.instance.DestroyQueue.Contains(z)).ToList();
}
