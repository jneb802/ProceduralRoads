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
    private const string Prefab = BridgeLayout.StoneStairPrefab;

    public GeneratedSpawnRecoveryTests()
    {
        RoadNetworkGenerator.Reset();
        NetworkCutoff.Forget();
        NetworkCutoff.SnapshotWorld(Array.Empty<Vector2s>());
        NetworkCutoff.BeginNewNetwork(); NetworkCutoff.UseNewNetwork();
        var run = new StairRun { From = new Vector2(0, 0), To = new Vector2(2, 0), Width = 4f, Kit = StairKit.Stone };
        run.Pieces.Add(new StructurePiece { Prefab = Prefab, Kind = StructurePieceKind.StairStep, Position = new Vector3(0, 40, 0) });
        Assert.NotNull(BuiltStairs.Keep(run, StairKit.Stone, run.Pieces, world: null));
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
        else { NetworkCutoff.SnapshotWorld(new[] { Zone }); NetworkCutoff.BeginNewNetwork(); NetworkCutoff.UseNewNetwork(); }

        Assert.Equal(0, StructurePlacement.SpawnGhostInZone(Zone));
        Assert.Equal(0, StructurePlacement.OnZoneSpawned(Zone, ZoneSystem.SpawnMode.Full));
        Assert.False(StructurePlans.IsSpawned(Zone));
        Assert.Empty(Pieces());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APlanThatFailsPartWayOwesOnlyTheRestOfIt(bool ghost)
    {
        // The worn piece fails in a step of the mod's own after its object is out (ShowHealth): it stands, and is not
        // owed again with the rest of the plan.
        // Save these fixture pieces as built, so the retry exercises this plan without dressing extra supports.
        var run = new StairRun { From = new Vector2(8, 0), To = new Vector2(12, 0), Width = 4f, Kit = StairKit.Stone };
        run.Pieces.Add(new StructurePiece { Prefab = "Breaking", Kind = StructurePieceKind.StairStep, Position = new Vector3(8, 40, 0), HealthFraction = 0.5f });
        run.Pieces.Add(new StructurePiece { Prefab = Prefab, Kind = StructurePieceKind.StairStep, Position = new Vector3(12, 40, 0) });
        Assert.NotNull(BuiltStairs.Keep(run, StairKit.Stone, run.Pieces, world: null));
        StairStructures.Add(run);
        ZNetScene.instance!.AddPrefab(Prefab);
        ZNetScene.instance.AddPrefab("Breaking");
        Assert.Equal(3, StructurePlans.BuildablePlanFor(Zone)!.Count);

        WearNTear.FailVisual = true;
        try { Assert.ThrowsAny<Exception>(() => Spawn(ghost)); }
        finally { WearNTear.FailVisual = false; }
        Assert.False(StructurePlans.IsSpawned(Zone), "a zone part of whose plan is not out is not done");
        Assert.Contains(Live(), z => z.GetPrefab() == "Breaking".GetStableHashCode());

        Spawn(ghost);
        Assert.True(StructurePlans.IsSpawned(Zone));
        Assert.Null(StructureAppendQueue.ForZone(Zone));
        Assert.Single(Live(), z => z.GetPrefab() == "Breaking".GetStableHashCode());
        Assert.Equal(2, Live().Count(z => z.GetPrefab() == Prefab.GetStableHashCode()));
        Assert.Equal(3, Live().Count);
        Assert.Equal(0, Spawn(ghost));
        Assert.Equal(3, Live().Count);
    }

    /// <summary>
    /// A generated zone whose own plan (the staircase here) is still owed, and an append's piece (another staircase's) queued
    /// there: the append's piece goes out, and the zone's plan after it, each once. An append marks only its own work done: it
    /// is not the zone's plan, so it neither records the zone built nor stands for its plan (StructurePlans.ZoneHasLivePieces
    /// counts an append's pieces as the plan only when the plan is all of them). Whether the append's pieces go out from the
    /// zone's spawn or first from the queue's own retry (StructurePlacement.ApplyPendingAppends). Before, the completed
    /// append recorded the zone built and its own staircase was never laid.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnAppendInAZoneThatOwesItsPlanLeavesThePlanOwed(bool retriedFirst)
    {
        ZNetScene.instance!.AddPrefab(Prefab);
        ZNetScene.instance.AddPrefab("Appended");
        ZoneSystem.instance!.Generated.Add(Zone);
        var run = new StairRun { From = new Vector2(8, 0), To = new Vector2(12, 0), Width = 4f, Kit = StairKit.Stone };
        run.Pieces.Add(new StructurePiece { Prefab = "Appended", Kind = StructurePieceKind.StairStep, Position = new Vector3(12, 40, 0) });
        Assert.NotNull(BuiltStairs.Keep(run, StairKit.Stone, run.Pieces, world: null));
        StairStructures.Add(run);
        List<StructurePiece> appended = StructurePlans.BuildablePlanFor(Zone)!.FindAll(p => p.Prefab == "Appended");
        Assert.Single(appended);
        StructureAppendQueue.Enqueue(StructureAppendQueue.Prepare(appended));
        Assert.NotNull(StructureAppendQueue.ForZone(Zone));

        if (retriedFirst)
        {
            StructurePlacement.ApplyPendingAppends();
            Assert.Null(StructureAppendQueue.ForZone(Zone));
            Assert.False(StructurePlans.IsSpawned(Zone), "the zone's own staircase is still owed");
        }
        Spawn(ghost: true);
        Assert.True(StructurePlans.IsSpawned(Zone));
        Assert.Single(Live(), z => z.GetPrefab() == Prefab.GetStableHashCode());
        Assert.Single(Live(), z => z.GetPrefab() == "Appended".GetStableHashCode());
        Assert.Equal(0, Spawn(ghost: true));
        Assert.Equal(2, Live().Count);
    }

    /// <summary>Every live ZDO, marked as the mod's piece or not.</summary>
    private static List<ZDO> Live() => ZDOMan.instance!.Zdos.Where(z => !ZDOMan.instance.DestroyQueue.Contains(z)).ToList();
}
