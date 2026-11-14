using System;
using System.Collections.Generic;
using System.Linq;
using ProceduralRoads;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

// Bridge append and respawn lifecycles through the real StructurePlacement and StructureAppendQueue on the multi-zone
// river crossing ManualBridgeTests uses. Only the scene is doubled (Valheim.Testing.Doubles): instantiating a
// networked prefab creates its ZDO, and ZDO destruction is queued until the test processes it, as in the game.
// Expectations are the queue's own debt taken before any placement, compared by piece key and multiplicity.
public sealed class BridgeLifecycleTests : IDisposable
{
    private static readonly int AppendKey = "ProceduralRoads_AppendPiece".GetStableHashCode();

    private readonly ValheimWorldScope _world = new ValheimWorldScope().WithWorld(new BridgeTests.WideRiverWorld()).WithZdos().WithZoneSystem().WithScene().AsServer();

    public BridgeLifecycleTests()
    {
        RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld();
        RoadNetworkGenerator.MarkLocationsReady(); RoadLocationProtection.Set(Array.Empty<RoadLocationProtection.Footprint>());
        RoadGrade.Configured = RoadConstants.DefaultMaxRoadGrade; RoadNetworkGenerator.RoadWidth = RoadConstants.DefaultRoadWidth;
        var island = new Island { Id = 1, CellSize = 8, WorldOffset = 0, Min = new Vector2(-216, -120), Max = new Vector2(216, 120) };
        for (int x = -27; x <= 27; x++) for (int z = -15; z <= 15; z++) island.Cells.Add(new Vector2Int(x, z));
        ManualRoads.Commit(ManualRoads.Prepare(new[] { new Vector2(-160, 0), new Vector2(160, 0) }, false, new[] { island }));
    }
    public void Dispose() { RoadNetworkGenerator.Reset(); _world.Dispose(); }

    // What the queue owes, by piece key, before anything is placed.
    private static Dictionary<string, int> Owed() => StructureAppendQueue.Zones.SelectMany(z => StructureAppendQueue.ForZone(z)!)
        .GroupBy(StructureAppendQueue.Key).ToDictionary(g => g.Key, g => g.Count());
    private static List<ZDO> Live() => ZDOMan.instance!.Zdos.Where(z => !ZDOMan.instance.DestroyQueue.Contains(z)).ToList();
    private static Dictionary<string, int> Emitted() => Live().Select(z => z.GetString(AppendKey)).Where(k => k.Length > 0)
        .GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
    private static void RegisterPrefabs(string? except = null)
    {
        foreach (var piece in StructureAppendQueue.Zones.SelectMany(z => StructureAppendQueue.ForZone(z)!))
            if (piece.Prefab != except && ZNetScene.instance!.GetPrefab(piece.Prefab) == null) ZNetScene.instance.AddPrefab(piece.Prefab);
    }
    // One zone (at most 64 pieces) per call, as the game's retry does; bounded so a stall cannot hang the test.
    private static int Drain(int limit)
    {
        int calls = 0;
        for (; calls < limit && StructureAppendQueue.Count > 0; calls++) StructurePlacement.ApplyPendingAppends();
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

    /// <summary>
    /// Pieces (bridges, staircases) never go into a zone explored before the network (NetworkCutoff): neither the
    /// append queue nor a zone spawn puts them there, and the zone is not marked spawned, while every other zone of
    /// the same structure gets its pieces.
    /// </summary>
    [Fact] public void AZoneExploredBeforeTheNetworkGetsNoPieces()
    {
        var zones = StructureAppendQueue.Zones.ToList(); RegisterPrefabs();
        Assert.True(zones.Count >= 2, "the fixture must append in more than one zone");
        Vector2s kept = zones[0];
        NetworkCutoff.Forget();
        try
        {
            NetworkCutoff.SnapshotWorld(new[] { kept });
            NetworkCutoff.BeginNewNetwork(); NetworkCutoff.UseNewNetwork();
            Drain(10 * zones.Count);
            Assert.Equal(0, StructurePlacement.OnZoneSpawned(kept, ZoneSystem.SpawnMode.Full));
            Assert.Equal(0, StructurePlacement.SpawnGhostInZone(kept));
            Assert.DoesNotContain(Live(), z => z.GetString(AppendKey).Length > 0 && z.GetSector() == kept);
            Assert.False(StructurePlans.IsSpawned(kept));
            Assert.Contains(Live(), z => z.GetString(AppendKey).Length > 0 && z.GetSector() != kept);
        }
        finally { NetworkCutoff.Forget(); }
    }

    /// <summary>
    /// A generated bridge that would put any piece in a zone explored before the network is built nowhere: no zone of
    /// it gets a piece, live or ghost, and none is marked spawned, so no half bridge stands. On a world with nothing
    /// explored the same bridge gets every planned piece.
    /// </summary>
    [Fact] public void AGeneratedBridgeReachingAnExploredZoneIsBuiltNowhere()
    {
        List<Vector2s> zones = GeneratedPlanZones();
        foreach (Vector2s zone in zones) _world.RegisterHeightmap(zone);   // loaded too, for SpawnInLoadedZones
        NetworkCutoff.Forget();
        try
        {
            NetworkCutoff.SnapshotWorld(new[] { zones[zones.Count - 1] });
            NetworkCutoff.BeginNewNetwork(); NetworkCutoff.UseNewNetwork();
            foreach (Vector2s zone in zones)
            {
                Assert.Equal(0, StructurePlacement.OnZoneSpawned(zone, ZoneSystem.SpawnMode.Full));
                Assert.Equal(0, StructurePlacement.SpawnGhostInZone(zone));
                Assert.False(StructurePlans.IsSpawned(zone));
            }
            Assert.Equal(0, StructurePlacement.SpawnInLoadedZones());
            Assert.DoesNotContain(Live(), z => z.GetInt(StructurePlans.MarkerHash) == 1);

            NetworkCutoff.Forget();
            NetworkCutoff.SnapshotWorld(Array.Empty<Vector2s>());
            NetworkCutoff.BeginNewNetwork(); NetworkCutoff.UseNewNetwork();
            Assert.Equal(StructurePlans.TotalPlannedPieces, zones.Sum(z => StructurePlacement.OnZoneSpawned(z, ZoneSystem.SpawnMode.Full)));
        }
        finally { NetworkCutoff.Forget(); }
    }

    /// <summary>
    /// A generated bridge is saved undecided, and it gets its kit when the server builds its first zone: the kit its first
    /// build picks, put on every record of the site and saved with the zones (a loaded network saves no more than that).
    /// Its other zones are built in the same kit, and it keeps it over another save and load.
    /// </summary>
    [Fact] public void AGeneratedBridgeGetsItsKitWhenItsFirstZoneIsBuiltAndKeepsIt()
    {
        WorldGenerator world = WorldGenerator.instance!;
        foreach (RoadCrossing c in RoadNetworkGenerator.GetRoadCrossings())
            if (BridgeStructures.HasDeck(c) && !BridgeStructures.IsViaduct(c, world)) c.Kit = BridgeStructures.Kit.Undecided;
        Reload();
        RoadCrossing site = Assert.Single(BridgeLayout.DistinctSites(RoadNetworkGenerator.GetRoadCrossings()), BridgeStructures.HasDeck);
        Assert.Equal(BridgeStructures.Kit.Undecided, site.Kit);
        Assert.False(RoadNetworkGenerator.NetworkDirty);
        BridgeStructures.Kit? expected = BridgeStructures.KitFor(site, world);
        Assert.True(BridgeStructures.Chosen(expected));

        List<Vector2s> zones = GeneratedPlanZones();
        Assert.True(StructurePlacement.OnZoneSpawned(zones[0], ZoneSystem.SpawnMode.Full) > 0);
        Assert.Equal(expected, site.Kit);
        Assert.All(RoadNetworkGenerator.GetRoadCrossings().Where(c => RoadCrossing.SameBanks(c, site, BridgeLayout.SameSiteRadius)),
            c => Assert.Equal(expected, c.Kit));
        foreach (Vector2s zone in zones.Skip(1)) StructurePlacement.OnZoneSpawned(zone, ZoneSystem.SpawnMode.Full);
        Assert.Equal(expected, site.Kit);
        Assert.Equal(BridgeStructures.Solve(site, world, world.GetSeed()).Count, Live().Count(z => z.GetInt(StructurePlans.MarkerHash) == 1));

        Reload();
        RoadCrossing loaded = Assert.Single(BridgeLayout.DistinctSites(RoadNetworkGenerator.GetRoadCrossings()), BridgeStructures.HasDeck);
        Assert.Equal(expected, loaded.Kit);
        Assert.All(zones, zone => Assert.True(StructurePlans.IsSpawned(zone)));
    }

    /// <summary>
    /// A generated bridge's pieces are solved once, when its first zone is built, and saved with the ground they are laid
    /// on: a later build whose layout, kits and terrain writer differ finishes the half-built bridge with the pieces it was
    /// started with, on the same ground, though it would solve the site otherwise.
    /// </summary>
    [Fact] public void AHalfBuiltBridgeFinishesAsItWasStartedUnderALaterLayoutAndWriter()
    {
        WorldGenerator world = WorldGenerator.instance!;
        float batter = RoadTerrainModifier.BatterPerMetre, threshold = RoadTerrainModifier.BatterThreshold;
        try
        {
            // As generated: no site built yet, its kit undecided.
            BuiltBridges.Reset();
            foreach (RoadCrossing c in RoadNetworkGenerator.GetRoadCrossings())
                if (BridgeStructures.HasDeck(c) && !BridgeStructures.IsViaduct(c, world)) c.Kit = BridgeStructures.Kit.Undecided;
            Reload();
            Assert.Empty(BuiltBridges.Sites);
            List<Vector2s> zones = GeneratedPlanZones();
            var started = zones.ToDictionary(z => z, z => StructurePlans.PlanFor(z)!.Select(StructureAppendQueue.Key).ToList());
            Assert.True(StructurePlacement.OnZoneSpawned(zones[0], ZoneSystem.SpawnMode.Full) > 0);
            Reload();

            // The later build: the site in another kit, the terrain written by another batter.
            RoadCrossing site = Assert.Single(BridgeLayout.DistinctSites(RoadNetworkGenerator.GetRoadCrossings()), BridgeStructures.HasDeck);
            BuiltBridge built = Assert.IsType<BuiltBridge>(BuiltBridges.Of(site));
            Assert.Equal(site.Kit, built.Kit);
            BridgeStructures.Kit other = BridgeStructures.KitsThatFit(site, world).First(k => k != site.Kit);
            foreach (RoadCrossing c in RoadNetworkGenerator.GetRoadCrossings())
                if (RoadCrossing.SameBanks(c, site, BridgeLayout.SameSiteRadius)) c.Kit = other;
            RoadTerrainModifier.BatterThreshold = 0f;
            RoadTerrainModifier.BatterPerMetre = 4f;
            StructurePlans.InvalidatePlans();
            Assert.NotEqual(started.Values.SelectMany(k => k).OrderBy(k => k),
                BridgeStructures.Solve(site, world, world.GetSeed()).Select(StructureAppendQueue.Key).OrderBy(k => k));

            foreach (Vector2s zone in zones.Skip(1))
            {
                Assert.Equal(started[zone], StructurePlans.PlanFor(zone)!.Select(StructureAppendQueue.Key).ToList());
                StructurePlacement.OnZoneSpawned(zone, ZoneSystem.SpawnMode.Full);
            }
            Assert.Equal(started.Values.Sum(k => k.Count), Live().Count(z => z.GetInt(StructurePlans.MarkerHash) == 1));

            // Its ground: held under the later writer.
            Assert.NotEmpty(built.Ground);
            Func<int, int, float> finished = StructureGround.Vertices(world);
            Assert.All(built.Ground, pin => Assert.Equal(pin.Height, finished(pin.X, pin.Z)));
        }
        finally
        {
            RoadTerrainModifier.BatterPerMetre = batter; RoadTerrainModifier.BatterThreshold = threshold;
        }
    }

    /// <summary>An addition beside a bridge not built yet owes only its own pieces: in a generated zone the bridge is not
    /// the request's, so it is neither queued nor saved because of it; in a zone not generated yet the addition waits for
    /// generation, which lays the zone's plan whole with it (StructureAppendQueue.Prepare).</summary>
    [Fact] public void AnAdditionBesideAnUnbuiltBridgeOwesOnlyItsOwnPieces()
    {
        BuiltBridges.Reset();
        List<Vector2s> zones = GeneratedPlanZones();
        RoadCrossing site = Assert.Single(BridgeLayout.DistinctSites(RoadNetworkGenerator.GetRoadCrossings()), BridgeStructures.HasDeck);
        var added = new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = "stone_stair", Position = StructurePlans.PlanFor(zones[0])![0].Position };
        Assert.Empty(StructureAppendQueue.Prepare(new[] { added }));
        ZoneSystem.instance!.Generated.Add(zones[0]);
        var owed = Assert.Single(StructureAppendQueue.Prepare(new[] { added }));
        Assert.Equal(zones[0], owed.Key);
        Assert.Same(added, Assert.Single(owed.Value));
        Assert.Null(BuiltBridges.Of(site));
    }

    /// <summary>A second addition to a zone still owed pieces by the first brings no old plan again: the first queued it, and a
    /// piece a player destroyed since must not come back.</summary>
    [Fact] public void ASecondAppendToAZoneStillOwedDoesNotAddItsOldPlanAgain()
    {
        List<Vector2s> zones = GeneratedPlanZones();
        Vector3 at = StructurePlans.PlanFor(zones[0])![0].Position;
        var first = new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = "stone_stair", Position = at };
        var second = new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = "stone_stair", Position = new Vector3(at.x, at.y + 1f, at.z) };
        ZoneSystem.instance!.Generated.Add(ZoneSystem.GetZone(at));
        StructureAppendQueue.Enqueue(StructureAppendQueue.Prepare(new[] { first }));
        Assert.NotNull(StructureAppendQueue.ForZone(zones[0]));
        List<StructurePiece> again = StructureAppendQueue.Prepare(new[] { second })[ZoneSystem.GetZone(at)];
        Assert.Same(second, Assert.Single(again));
    }

    /// <summary>A join aims at the network as generation's hints do: never at a point on a crossing, its banks included (a join aimed at a bridge's
    /// waypoint in mid-river built a second bridge out to it).</summary>
    [Fact] public void AJoinNeverAimsAtAPointOnACrossing()
    {
        RoadCrossing bridge = Assert.Single(RoadNetworkGenerator.GetRoadCrossings(), c => c.Kind == CrossingKind.Bridge);
        Vector2 bank = bridge.ToBank;
        float reach = RoadNetworkGenerator.RoadWidth * 0.5f;
        Assert.Contains(RoadSpatialGrid.RoadTargets(_ => true), p => Vector2.Distance(p, bank) <= reach);
        List<Vector2> targets = RoadNetworkGenerator.JoinTargets(_ => true);
        Assert.NotEmpty(targets);
        Assert.DoesNotContain(targets, p => Vector2.Distance(p, bank) <= reach);
        Assert.Contains(targets, p => p.x < bridge.FromBank.x - 8f || p.x > bridge.ToBank.x + 8f || p.y < Mathf.Min(bridge.FromBank.y, bridge.ToBank.y) - 8f);
    }

    /// <summary>A site with pieces standing in a zone whose record was lost is started: reaching another of its zones lays it
    /// zone by zone as before, saving no fresh solve that could disagree with what stands.</summary>
    [Fact] public void ASiteWithPiecesStandingAndNoRecordIsNotSavedAfresh()
    {
        BuiltBridges.Reset();
        List<Vector2s> zones = GeneratedPlanZones();
        Assert.True(StructurePlacement.OnZoneSpawned(zones[0], ZoneSystem.SpawnMode.Full) > 0);
        BuiltBridges.Reset();
        StructurePlans.ForgetSpawned();
        Assert.True(StructurePlacement.OnZoneSpawned(zones[1], ZoneSystem.SpawnMode.Full) > 0);
        Assert.Empty(BuiltBridges.Sites);
    }

    /// <summary>A full respawn (road_bridges) builds the bridges again from the plans: the saved pieces and their held
    /// ground go with the old pieces.</summary>
    [Fact] public void AFullRespawnForgetsTheSavedBridgesAndTheirGround()
    {
        BuiltBridges.Reset();
        List<Vector2s> zones = GeneratedPlanZones();
        Assert.True(StructurePlacement.OnZoneSpawned(zones[0], ZoneSystem.SpawnMode.Full) > 0);
        GroundPin pin = Assert.Single(BuiltBridges.Sites).Ground[0];
        Assert.NotNull(StructureGround.Held(new Vector2(pin.X, pin.Z)));
        StructurePlacement.ClearSpawnedPieces();
        Assert.Empty(BuiltBridges.Sites);
        Assert.Null(StructureGround.Held(new Vector2(pin.X, pin.Z)));
    }

    /// <summary>A site's first build saves the pieces the plan solved, laid in that zone: the site is solved once.</summary>
    [Fact] public void TheFirstBuildOfASiteSolvesItOnce()
    {
        BuiltBridges.Reset();
        List<Vector2s> zones = GeneratedPlanZones();
        RoadCrossing site = Assert.Single(BridgeLayout.DistinctSites(RoadNetworkGenerator.GetRoadCrossings()), BridgeStructures.HasDeck);
        List<string> planned = zones.SelectMany(z => StructurePlans.PlanFor(z)!).Select(StructureAppendQueue.Key).OrderBy(k => k).ToList();
        int solves = BridgeStructures.Solves;
        Assert.True(StructurePlacement.OnZoneSpawned(zones[0], ZoneSystem.SpawnMode.Full) > 0);
        Assert.Equal(solves, BridgeStructures.Solves);
        Assert.Equal(planned, Assert.IsType<BuiltBridge>(BuiltBridges.Of(site)).Pieces.Select(StructureAppendQueue.Key).OrderBy(k => k));
    }

    /// <summary>A site is saved with the layout that solved it, which a save and reload keep. A record of another
    /// version is refused, for bridges and for staircases.</summary>
    [Fact] public void ASavedSiteKeepsItsLayoutAndAnotherVersionIsRefused()
    {
        BuiltBridges.Reset();
        List<Vector2s> zones = GeneratedPlanZones();
        Assert.True(StructurePlacement.OnZoneSpawned(zones[0], ZoneSystem.SpawnMode.Full) > 0);
        Assert.Equal(BridgeLayout.LayoutVersion, Assert.Single(BuiltBridges.Sites).Layout);
        Reload();
        BuiltBridge saved = Assert.Single(BuiltBridges.Sites);
        Assert.Equal(BridgeLayout.LayoutVersion, saved.Layout);

        byte[] v2 = BuiltBridges.Serialize();
        Assert.Equal(2, BitConverter.ToInt32(v2, 0));
        foreach (int other in new[] { 0, 1, 3 })
        {
            byte[] changed = (byte[])v2.Clone();
            BitConverter.GetBytes(other).CopyTo(changed, 0);
            Assert.Throws<System.IO.InvalidDataException>(() => BuiltBridges.Parse(changed));
            Assert.Throws<System.IO.InvalidDataException>(() => BuiltStairs.Parse(changed));
        }
    }

    /// <summary>road_bridges respawn older solves again only the sites an older layout built, replacing their saved pieces
    /// in this build's layout; with none older it touches nothing, and a structure's pieces in another zone stay.</summary>
    [Fact] public void ARespawnOfOlderLayoutSitesTouchesOnlyThose()
    {
        BuiltBridges.Reset();
        List<Vector2s> zones = GeneratedPlanZones();
        foreach (Vector2s zone in zones) ZoneSystem.instance!.Generated.Add(zone);
        foreach (Vector2s zone in zones) StructurePlacement.OnZoneSpawned(zone, ZoneSystem.SpawnMode.Full);
        int pieces = Live().Count(z => z.GetInt(StructurePlans.MarkerHash) == 1);
        Assert.True(pieces > 0);
        var away = new Vector2s(20, 20);
        Assert.DoesNotContain(away, zones);
        ZDO other = ZDOMan.instance!.CreateNewZDO(ZoneSystem.GetZonePos(away), "stone_stair".GetStableHashCode());
        other.Set(StructurePlans.MarkerHash, 1);
        StructurePlans.MarkSpawned(away);

        Assert.Equal((0, 0), StructurePlacement.RespawnFromPlans(olderLayoutOnly: true));
        Assert.Empty(ZDOMan.instance.DestroyQueue);

        BuiltBridge older = Assert.Single(BuiltBridges.Sites);
        older.Layout = BridgeLayout.LayoutVersion - 1;
        var (destroyed, respawned) = StructurePlacement.RespawnFromPlans(olderLayoutOnly: true);
        Assert.Equal(pieces, destroyed);
        Assert.Equal(zones.Count, respawned);
        Assert.Equal(pieces, ZDOMan.instance.ProcessDestroyed());
        BuiltBridge again = Assert.Single(BuiltBridges.Sites);
        Assert.NotSame(older, again);
        Assert.Equal(BridgeLayout.LayoutVersion, again.Layout);
        Assert.Equal(pieces + 1, Live().Count(z => z.GetInt(StructurePlans.MarkerHash) == 1));
        Assert.Contains(other, Live());
        Assert.True(StructurePlans.IsSpawned(away));
    }

    // The fixture's crossing as a generated plan, not an append, with its prefabs registered.
    private static List<Vector2s> GeneratedPlanZones()
    {
        StructureAppendQueue.Reset();
        List<Vector2s> zones = StructurePlans.PlannedZones;
        Assert.True(zones.Count >= 2, "the fixture's bridge must span more than one zone");
        foreach (var piece in zones.SelectMany(z => StructurePlans.PlanFor(z)!))
            if (ZNetScene.instance!.GetPrefab(piece.Prefab) == null) ZNetScene.instance.AddPrefab(piece.Prefab);
        return zones;
    }

    /// <summary>
    /// A bridge held back by a zone the network leaves as it is is built only whole, once a manual road opens the last such
    /// zone of it (NetworkCutoff.Admit): a zone of it built meanwhile (recorded spawned by another structure's plan) is owed
    /// its part through the queue, and the opened zone gets its own; never half of it. While any of its zones is left as it
    /// is, nothing of it is owed. Before, the zone built meanwhile never got its part: half a bridge after the admit.
    /// </summary>
    [Fact] public void AWithheldBridgeIsBuiltWholeOnceAManualRoadOpensItsLastProtectedZone()
    {
        List<Vector2s> zones = GeneratedPlanZones();
        // The zones of the two banks: one built meanwhile, the east bank's protected.
        Vector2s built = zones.OrderBy(z => z.x).First(), kept = zones.OrderBy(z => z.x).Last();
        var planned = zones.ToDictionary(z => z, z => StructurePlans.PlanFor(z)!.Select(StructureAppendQueue.Key).OrderBy(k => k).ToList());
        NetworkCutoff.Forget();
        try
        {
            NetworkCutoff.SnapshotWorld(new[] { kept });
            NetworkCutoff.BeginNewNetwork(); NetworkCutoff.UseNewNetwork();
            Assert.All(zones, z => Assert.Null(StructurePlans.BuildablePlanFor(z)));
            // Built meanwhile, by another structure's plan there.
            ZoneSystem.instance!.Generated.Add(built);
            StructurePlans.MarkSpawned(built);

            // A manual road in the protected zone (the east bank, beside the road onto the bridge): it opens that zone.
            var island = new Island { Id = 1, CellSize = 8, WorldOffset = 0, Min = new Vector2(-216, -120), Max = new Vector2(216, 120) };
            for (int x = -27; x <= 27; x++) for (int z = -15; z <= 15; z++) island.Cells.Add(new Vector2Int(x, z));
            ManualRoads.Plan plan = ManualRoads.Prepare(new[] { new Vector2(52f, -100f), new Vector2(84f, -100f) }, false, new[] { island });
            ManualRoads.Commit(plan);
            Assert.Contains(kept, plan.ExploredZonesWritten);
            Assert.False(NetworkCutoff.Excludes(kept));
            Assert.NotNull(StructureAppendQueue.ForZone(built));
            Assert.Equal(planned[built], StructureAppendQueue.ForZone(built)!.Select(StructureAppendQueue.Key).OrderBy(k => k).ToList());
        }
        finally { NetworkCutoff.Forget(); }
    }

    /// <summary>
    /// road_generate or road_regen_island in a running session: the new network leaves every zone generated so far
    /// as it is, so the respawn that follows destroys none of the bridge pieces standing there and builds no second
    /// copy over them, though the zones are loaded.
    /// </summary>
    [Fact] public void ARespawnKeepsThePiecesInExploredZones()
    {
        List<Vector2s> zones = GeneratedPlanZones();
        foreach (Vector2s zone in zones) _world.RegisterHeightmap(zone);   // loaded, so the respawn can rebuild
        NetworkCutoff.Forget();
        try
        {
            NetworkCutoff.SnapshotWorld(Array.Empty<Vector2s>());
            NetworkCutoff.BeginNewNetwork(); NetworkCutoff.UseNewNetwork();
            foreach (Vector2s zone in zones) StructurePlacement.OnZoneSpawned(zone, ZoneSystem.SpawnMode.Full);
            var standing = Live().Where(z => z.GetInt(StructurePlans.MarkerHash) == 1).ToList();
            Assert.Equal(StructurePlans.TotalPlannedPieces, standing.Count);

            NetworkCutoff.Forget();
            NetworkCutoff.SnapshotWorld(zones);   // every zone of the bridge explored before the next network
            NetworkCutoff.BeginNewNetwork(); NetworkCutoff.UseNewNetwork();
            StructurePlacement.RespawnFromPlans();

            Assert.All(standing, z => Assert.DoesNotContain(z, ZDOMan.instance!.DestroyQueue));
            Assert.Equal(standing.Count, Live().Count(z => z.GetInt(StructurePlans.MarkerHash) == 1));

            // Control: with nothing explored the same respawn destroys the pieces and builds the plan again.
            NetworkCutoff.Forget();
            NetworkCutoff.SnapshotWorld(Array.Empty<Vector2s>());
            NetworkCutoff.BeginNewNetwork(); NetworkCutoff.UseNewNetwork();
            StructurePlacement.RespawnFromPlans();
            Assert.All(standing, z => Assert.Contains(z, ZDOMan.instance!.DestroyQueue));
            Assert.Equal(StructurePlans.TotalPlannedPieces, Live().Count(z => z.GetInt(StructurePlans.MarkerHash) == 1));
        }
        finally { NetworkCutoff.Forget(); }
    }

    [Fact] public void AMultiZoneAppendResumesAfterAReloadAndEmitsEachOwedPieceOnce()
    {
        var owed = Owed(); var zones = StructureAppendQueue.Zones.ToList();
        Assert.True(zones.Count >= 2, "the fixture must append in more than one zone");
        RegisterPrefabs();
        StructurePlacement.ApplyPendingAppends();
        int first = Emitted().Values.Sum();
        Assert.InRange(first, 1, 64); Assert.True(StructureAppendQueue.Count > 0, "a reload must interrupt unfinished work");
        Reload();
        Assert.True(Drain(10 * owed.Count) < 10 * owed.Count, "the queue must drain");
        Assert.Equal(owed.OrderBy(k => k.Key), Emitted().OrderBy(k => k.Key));
        foreach (var zone in zones) Assert.True(StructurePlans.IsSpawned(zone));
        Reload(); Assert.Equal(0, StructureAppendQueue.Count);
    }
    [Fact] public void ALostQueueSaveDoesNotDuplicatePiecesAlreadyEmitted()
    {
        var owed = Owed(); RegisterPrefabs();
        var lastSaved = StructureAppendQueue.Serialize();
        StructurePlacement.ApplyPendingAppends();
        Assert.NotEmpty(Emitted());
        // The acknowledgements never reached a save: the queue comes back owing pieces that already stand.
        StructureAppendQueue.Load(lastSaved);
        Drain(10 * owed.Count);
        Assert.Equal(0, StructureAppendQueue.Count);
        Assert.Equal(owed.OrderBy(k => k.Key), Emitted().OrderBy(k => k.Key));
    }
    [Fact] public void AFullRespawnMidAppendLeavesEveryPlannedPieceOnceAndStrandsNothing()
    {
        var zones = StructureAppendQueue.Zones.ToList(); RegisterPrefabs();
        foreach (var zone in zones) ZoneSystem.instance!.Generated.Add(zone);
        var planned = zones.ToDictionary(z => z, StructurePlans.PlannedPieceCount);
        StructurePlacement.ApplyPendingAppends();
        int partial = Emitted().Values.Sum(); Assert.True(partial > 0);
        var (destroyed, respawnedZones) = StructurePlacement.RespawnFromPlans();
        Assert.Equal(partial, destroyed); Assert.Equal(zones.Count, respawnedZones);
        Assert.Equal(partial, ZDOMan.instance!.ProcessDestroyed());
        Assert.Equal(0, StructureAppendQueue.Count); Assert.Empty(Emitted());
        foreach (var zone in zones)
            Assert.Equal(planned[zone], Live().Count(z => z.GetInt(StructurePlans.MarkerHash) == 1 && z.GetSector() == zone));
        // A second respawn replaces, never adds.
        StructurePlacement.RespawnFromPlans(); ZDOMan.instance.ProcessDestroyed();
        Assert.Equal(planned.Values.Sum(), Live().Count(z => z.GetInt(StructurePlans.MarkerHash) == 1));
    }
    [Fact] public void AMissingPrefabStallsOnlyTheZonesThatNeedItAndResumesWhenItAppears()
    {
        var owed = Owed();
        string missing = StructureAppendQueue.ForZone(StructureAppendQueue.Zones.First())![0].Prefab;
        var needing = StructureAppendQueue.Zones.Where(z => StructureAppendQueue.ForZone(z)!.Any(p => p.Prefab == missing)).ToHashSet();
        RegisterPrefabs(except: missing);
        Drain(10 * owed.Count);
        Assert.Equal(needing.OrderBy(z => z.x).ThenBy(z => z.y), StructureAppendQueue.Zones.OrderBy(z => z.x).ThenBy(z => z.y));
        ZNetScene.instance!.AddPrefab(missing);
        Drain(10 * owed.Count);
        Assert.Equal(0, StructureAppendQueue.Count);
        Assert.Equal(owed.OrderBy(k => k.Key), Emitted().OrderBy(k => k.Key));
    }
    [Fact] public void RepeatedOrUnknownAcknowledgementsChangeNothing()
    {
        var zone = StructureAppendQueue.Zones.First(z => StructureAppendQueue.ForZone(z)!.Count > 1);
        string key = StructureAppendQueue.Key(StructureAppendQueue.ForZone(zone)![0]);
        StructureAppendQueue.Acknowledge(zone, key); int left = StructureAppendQueue.ForZone(zone)!.Count;
        StructureAppendQueue.Acknowledge(zone, key); StructureAppendQueue.Acknowledge(zone, "not-a-piece");
        StructureAppendQueue.Acknowledge(new Vector2s(999, 999), key);
        Assert.Equal(left, StructureAppendQueue.ForZone(zone)!.Count);
        // Completing the append marks only its own work done: the zone's plan, none of it out, is still owed.
        StructureAppendQueue.Complete(zone); StructureAppendQueue.Acknowledge(zone, key);
        Assert.Null(StructureAppendQueue.ForZone(zone)); Assert.False(StructurePlans.IsSpawned(zone));
    }
    // (d) An append into zones neither generated nor loaded: the server places the pieces as ZDOs, the restart keeps
    // what was done, and the zones' own generation later neither repeats nor loses a piece.
    [Fact] public void AppendsIntoUngeneratedZonesSurviveARestartAndAreNotRepeatedWhenTheZonesGenerate()
    {
        var owed = Owed(); var zones = StructureAppendQueue.Zones.ToList(); RegisterPrefabs();
        Assert.All(zones, z => Assert.False(ZoneSystem.instance!.IsZoneGenerated(z)));
        Assert.Empty(Heightmap.GetAllHeightmaps());
        Assert.True(Drain(10 * owed.Count) < 10 * owed.Count, "the queue must drain");
        Assert.Equal(owed.OrderBy(k => k.Key), Emitted().OrderBy(k => k.Key));
        Reload();
        foreach (var zone in zones)
        {
            ZoneSystem.instance!.Generated.Add(zone);
            Assert.Equal(0, StructurePlacement.OnZoneSpawned(zone, ZoneSystem.SpawnMode.Full));
        }
        Assert.Equal(owed.OrderBy(k => k.Key), Emitted().OrderBy(k => k.Key));
        Assert.Equal(owed.Values.Sum(), Live().Count(z => z.GetInt(StructurePlans.MarkerHash) == 1));
    }
    [Fact] public void PlacementDoesNothingOffTheServer()
    {
        var owed = Owed(); RegisterPrefabs(); ZNet.instance.Server = false;
        Drain(10);
        Assert.Empty(Emitted()); Assert.Equal(owed.Count, StructureAppendQueue.Zones.Sum(z => StructureAppendQueue.ForZone(z)!.Count));
    }
}
