using System;
using System.Collections.Generic;
using System.Linq;
using ProceduralRoads;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

// Bridge append and respawn lifecycles through the real BridgePlacement and BridgeAppendQueue on the multi-zone
// river crossing ManualBridgeTests uses. Only the scene is doubled (Valheim.Testing.Doubles): instantiating a
// networked prefab creates its ZDO, and ZDO destruction is queued until the test processes it, as in the game.
// Expectations are the queue's own debt taken before any placement, compared by piece key and multiplicity.
public sealed class BridgeLifecycleTests : IDisposable
{
    private static readonly int AppendKey = "ProceduralRoads_AppendPiece".GetStableHashCode();

    private readonly ValheimWorldScope _world = new ValheimWorldScope().WithWorld(new BridgeTests.WideRiverWorld()).WithZdos().WithZoneSystem().WithScene().AsServer();

    public BridgeLifecycleTests()
    {
        RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetDebugCounters();
        RoadNetworkGenerator.MarkLocationsReady(); RoadSiteProtection.Set(Array.Empty<RoadSiteProtection.Footprint>());
        RoadGrade.Configured = .35f; RoadNetworkGenerator.RoadWidth = 4;
        var island = new Island { Id = 1, CellSize = 8, WorldOffset = 0, Min = new Vector2(-216, -120), Max = new Vector2(216, 120) };
        for (int x = -27; x <= 27; x++) for (int z = -15; z <= 15; z++) island.Cells.Add(new Vector2Int(x, z));
        ManualRoads.Commit(ManualRoads.Prepare(new[] { new Vector2(-160, 0), new Vector2(160, 0) }, false, new[] { island }));
    }
    public void Dispose() { RoadNetworkGenerator.Reset(); _world.Dispose(); }

    // What the queue owes, by piece key, before anything is placed.
    private static Dictionary<string, int> Owed() => BridgeAppendQueue.Zones.SelectMany(z => BridgeAppendQueue.ForZone(z)!)
        .GroupBy(BridgeAppendQueue.Key).ToDictionary(g => g.Key, g => g.Count());
    private static List<ZDO> Live() => ZDOMan.instance!.Zdos.Where(z => !ZDOMan.instance.DestroyQueue.Contains(z)).ToList();
    private static Dictionary<string, int> Emitted() => Live().Select(z => z.GetString(AppendKey)).Where(k => k.Length > 0)
        .GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
    private static void RegisterPrefabs(string? except = null)
    {
        foreach (var piece in BridgeAppendQueue.Zones.SelectMany(z => BridgeAppendQueue.ForZone(z)!))
            if (piece.Prefab != except && ZNetScene.instance!.GetPrefab(piece.Prefab) == null) ZNetScene.instance.AddPrefab(piece.Prefab);
    }
    // One zone (at most 64 pieces) per call, as the game's retry does; bounded so a stall cannot hang the test.
    private static int Drain(int limit)
    {
        int calls = 0;
        for (; calls < limit && BridgeAppendQueue.Count > 0; calls++) BridgePlacement.ApplyPendingAppends();
        return calls;
    }
    // A world save and restart through the game's own hooks: OnPrepareSave saves in full after an edit, and only the
    // bridge zones and append queue for a loaded network; the load marks the network loaded, as the lifecycle's
    // decision does (without the mark, later saves of the queue are skipped).
    private static void Reload()
    {
        RoadLifecycleManager.OnPrepareSave(); RoadNetworkGenerator.Reset();
        Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData()); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
    }

    [Fact] public void AMultiZoneAppendResumesAfterAReloadAndEmitsEachOwedPieceOnce()
    {
        var owed = Owed(); var zones = BridgeAppendQueue.Zones.ToList();
        Assert.True(zones.Count >= 2, "the fixture must append in more than one zone");
        RegisterPrefabs();
        BridgePlacement.ApplyPendingAppends();
        int first = Emitted().Values.Sum();
        Assert.InRange(first, 1, 64); Assert.True(BridgeAppendQueue.Count > 0, "a reload must interrupt unfinished work");
        Reload();
        Assert.True(Drain(10 * owed.Count) < 10 * owed.Count, "the queue must drain");
        Assert.Equal(owed.OrderBy(k => k.Key), Emitted().OrderBy(k => k.Key));
        foreach (var zone in zones) Assert.True(BridgePlans.IsSpawned(zone));
        Reload(); Assert.Equal(0, BridgeAppendQueue.Count);
    }
    [Fact] public void ALostQueueSaveDoesNotDuplicatePiecesAlreadyEmitted()
    {
        var owed = Owed(); RegisterPrefabs();
        var lastSaved = BridgeAppendQueue.Serialize();
        BridgePlacement.ApplyPendingAppends();
        Assert.NotEmpty(Emitted());
        // The acknowledgements never reached a save: the queue comes back owing pieces that already stand.
        BridgeAppendQueue.Load(lastSaved);
        Drain(10 * owed.Count);
        Assert.Equal(0, BridgeAppendQueue.Count);
        Assert.Equal(owed.OrderBy(k => k.Key), Emitted().OrderBy(k => k.Key));
    }
    [Fact] public void AFullRespawnMidAppendLeavesEveryPlannedPieceOnceAndStrandsNothing()
    {
        var zones = BridgeAppendQueue.Zones.ToList(); RegisterPrefabs();
        foreach (var zone in zones) ZoneSystem.instance!.Generated.Add(zone);
        var planned = zones.ToDictionary(z => z, BridgePlans.PlannedPieceCount);
        BridgePlacement.ApplyPendingAppends();
        int partial = Emitted().Values.Sum(); Assert.True(partial > 0);
        var (destroyed, respawnedZones) = BridgePlacement.RespawnFromPlans();
        Assert.Equal(partial, destroyed); Assert.Equal(zones.Count, respawnedZones);
        Assert.Equal(partial, ZDOMan.instance!.ProcessDestroyed());
        Assert.Equal(0, BridgeAppendQueue.Count); Assert.Empty(Emitted());
        foreach (var zone in zones)
            Assert.Equal(planned[zone], Live().Count(z => z.GetInt(BridgePlans.MarkerHash) == 1 && z.GetSector() == zone));
        // A second respawn replaces, never adds.
        BridgePlacement.RespawnFromPlans(); ZDOMan.instance.ProcessDestroyed();
        Assert.Equal(planned.Values.Sum(), Live().Count(z => z.GetInt(BridgePlans.MarkerHash) == 1));
    }
    [Fact] public void AMissingPrefabStallsOnlyTheZonesThatNeedItAndResumesWhenItAppears()
    {
        var owed = Owed();
        string missing = BridgeAppendQueue.ForZone(BridgeAppendQueue.Zones.First())![0].Prefab;
        var needing = BridgeAppendQueue.Zones.Where(z => BridgeAppendQueue.ForZone(z)!.Any(p => p.Prefab == missing)).ToHashSet();
        RegisterPrefabs(except: missing);
        Drain(10 * owed.Count);
        Assert.Equal(needing.OrderBy(z => z.x).ThenBy(z => z.y), BridgeAppendQueue.Zones.OrderBy(z => z.x).ThenBy(z => z.y));
        ZNetScene.instance!.AddPrefab(missing);
        Drain(10 * owed.Count);
        Assert.Equal(0, BridgeAppendQueue.Count);
        Assert.Equal(owed.OrderBy(k => k.Key), Emitted().OrderBy(k => k.Key));
    }
    [Fact] public void RepeatedOrUnknownAcknowledgementsChangeNothing()
    {
        var zone = BridgeAppendQueue.Zones.First(z => BridgeAppendQueue.ForZone(z)!.Count > 1);
        string key = BridgeAppendQueue.Key(BridgeAppendQueue.ForZone(zone)![0]);
        BridgeAppendQueue.Acknowledge(zone, key); int left = BridgeAppendQueue.ForZone(zone)!.Count;
        BridgeAppendQueue.Acknowledge(zone, key); BridgeAppendQueue.Acknowledge(zone, "not-a-piece");
        BridgeAppendQueue.Acknowledge(new Vector2s(999, 999), key);
        Assert.Equal(left, BridgeAppendQueue.ForZone(zone)!.Count);
        BridgeAppendQueue.Complete(zone); BridgeAppendQueue.Acknowledge(zone, key);
        Assert.Null(BridgeAppendQueue.ForZone(zone)); Assert.True(BridgePlans.IsSpawned(zone));
    }
    // (d) An append into zones neither generated nor loaded: the server places the pieces as ZDOs, the restart keeps
    // what was done, and the zones' own generation later neither repeats nor loses a piece.
    [Fact] public void AppendsIntoUngeneratedZonesSurviveARestartAndAreNotRepeatedWhenTheZonesGenerate()
    {
        var owed = Owed(); var zones = BridgeAppendQueue.Zones.ToList(); RegisterPrefabs();
        Assert.All(zones, z => Assert.False(ZoneSystem.instance!.IsZoneGenerated(z)));
        Assert.Empty(Heightmap.GetAllHeightmaps());
        Assert.True(Drain(10 * owed.Count) < 10 * owed.Count, "the queue must drain");
        Assert.Equal(owed.OrderBy(k => k.Key), Emitted().OrderBy(k => k.Key));
        Reload();
        foreach (var zone in zones)
        {
            ZoneSystem.instance!.Generated.Add(zone);
            Assert.Equal(0, BridgePlacement.OnZoneSpawned(zone, ZoneSystem.SpawnMode.Full));
        }
        Assert.Equal(owed.OrderBy(k => k.Key), Emitted().OrderBy(k => k.Key));
        Assert.Equal(owed.Values.Sum(), Live().Count(z => z.GetInt(BridgePlans.MarkerHash) == 1));
    }
    [Fact] public void PlacementDoesNothingOffTheServer()
    {
        var owed = Owed(); RegisterPrefabs(); ZNet.instance.Server = false;
        Drain(10);
        Assert.Empty(Emitted()); Assert.Equal(owed.Count, BridgeAppendQueue.Zones.Sum(z => BridgeAppendQueue.ForZone(z)!.Count));
    }
}
