using System;
using System.Collections.Generic;
using System.Linq;
using ProceduralRoads;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

// Late kit choice for staircases, through the real StructurePlacement on BridgeLifecycleTests' world: the network saves a
// staircase's layout; its kit's pieces are laid over it when its first zone is built and saved then (BuiltStairs), as a
// crossing site's are (BuiltBridges). Only the scene is doubled (Valheim.Testing.Doubles).
public sealed class StairSiteTests : IDisposable
{
    private readonly ValheimWorldScope _world = new ValheimWorldScope().WithWorld(new BridgeTests.WideRiverWorld()).WithZdos().WithZoneSystem().WithScene().AsServer();

    public StairSiteTests()
    {
        RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld();
        RoadNetworkGenerator.MarkLocationsReady(); RoadLocationProtection.Set(Array.Empty<RoadLocationProtection.Footprint>());
        RoadGrade.Configured = RoadConstants.DefaultMaxRoadGrade; RoadNetworkGenerator.RoadWidth = RoadConstants.DefaultRoadWidth;
        CommitFixtureRoad();
    }
    public void Dispose() { RoadNetworkGenerator.Reset(); _world.Dispose(); }

    /// <summary>BridgeLifecycleTests' road over the wide river, as a generated network has it: one bridge site over several
    /// zones, none of them built, nothing owed (the manual road's own append forgotten).</summary>
    internal static void CommitFixtureRoad()
    {
        var island = new Island { Id = 1, CellSize = 8, WorldOffset = 0, Min = new Vector2(-216, -120), Max = new Vector2(216, 120) };
        for (int x = -27; x <= 27; x++) for (int z = -15; z <= 15; z++) island.Cells.Add(new Vector2Int(x, z));
        ManualRoads.Commit(ManualRoads.Prepare(new[] { new Vector2(-160, 0), new Vector2(160, 0) }, false, new[] { island }));
        StructureAppendQueue.Reset();
        BuiltBridges.Reset();
    }

    /// <summary>
    /// A staircase of the network across two zones, north of the road at <paramref name="x"/> (z 10 to 50, over the zone
    /// edge at z 32), as generation adds one: its layout planned (on a slope of its own with a dip it crosses), added, and
    /// settled on the finished ground. The layout carries no supports: its kit lays what holds it up over the world's ground
    /// when its first zone is built (StairSupport).
    /// </summary>
    internal static StairRun AddStaircase(WorldGenerator world, float x)
    {
        Vector2 from = new(x, 10f), to = new(x, 50f);
        // Its slope climbs 4 m off the world's 32 m ground and stays within a wall's height over it, so its kit holds every
        // piece up by the game's support rule (StairKit.Dress refuses one it cannot).
        StairPlan plan = StairLayout.Build(from, to, (px, pz) => Mathf.Min(32f + 0.5f * (pz - 10f), 36f) - (pz > 22f && pz < 42f ? 3f : 0f), kit: StairKit.Stone);
        Assert.True(plan.Reached, plan.Reason);
        StairRun run = StairRun.FromPlan(from, to, RoadStairRuns.Width, plan, StairKit.Stone);
        StairStructures.Add(run);
        StairStructures.MarkStanding(run);
        StairStructures.Settle(run, StructureGround.Vertices(world));
        Assert.True(run.Pieces.Select(p => ZoneSystem.GetZone(p.Position)).Distinct().Count() >= 2, "the staircase must span more than one zone");
        Assert.DoesNotContain(run.Pieces, p => p.Kind == StructurePieceKind.StairSupport);
        Assert.Contains(StairStructures.Dresser(run, world), p => p.Kind == StructurePieceKind.StairSupport);
        return run;
    }

    // A world save and restart through the game's own hooks (BridgeLifecycleTests.Reload).
    private static void Reload()
    {
        RoadLifecycleManager.OnPrepareSave(); RoadNetworkGenerator.Reset();
        Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData()); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
    }

    private static StairRun TheRun(Vector2 from) => Assert.Single(StairStructures.Runs, r => r.From == from);

    /// <summary>The zones a staircase's planned pieces stand in, with their prefabs registered, in a fixed order.</summary>
    private static List<Vector2s> ZonesOf(StairRun run)
    {
        List<Vector2s> zones = StairStructures.PiecesOf(run, WorldGenerator.instance!).Select(p => ZoneSystem.GetZone(p.Position)).Distinct()
            .OrderBy(z => z.y).ThenBy(z => z.x).ToList();
        foreach (var piece in zones.SelectMany(z => StructurePlans.PlanFor(z)!))
            if (ZNetScene.instance!.GetPrefab(piece.Prefab) == null) ZNetScene.instance.AddPrefab(piece.Prefab);
        return zones;
    }

    private static List<string> Keys(IEnumerable<StructurePiece> pieces) => pieces.Select(StructureAppendQueue.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

    // Placed piece objects in these zones, by prefab and position.
    private static List<(int, Vector3)> LiveIn(ICollection<Vector2s> zones) => ZDOMan.instance!.Zdos
        .Where(z => !ZDOMan.instance.DestroyQueue.Contains(z) && z.GetInt(StructurePlans.MarkerHash) == 1 && zones.Contains(z.GetSector()))
        .Select(z => (z.GetPrefab(), z.GetPosition())).OrderBy(k => k.Item2.x).ThenBy(k => k.Item2.y).ThenBy(k => k.Item2.z).ThenBy(k => k.Item1).ToList();
    private static List<(int, Vector3)> Expected(IEnumerable<StructurePiece> pieces) => pieces
        .Select(p => (p.Prefab.GetStableHashCode(), p.Position)).OrderBy(k => k.Item2.x).ThenBy(k => k.Item2.y).ThenBy(k => k.Item2.z).ThenBy(k => k.Item1).ToList();

    /// <summary>
    /// The kit's pieces of a staircase are laid over its layout when its first zone is built, and saved, stamped with the
    /// laying, with the ground the layout was settled on and the ground under the kit's supports; with today's kits they are
    /// the layout's walking pieces and what holds them up (StairSupport). A later
    /// build whose kits lay otherwise finishes the half-built staircase with the pieces it was started with, and lays its
    /// own kit's pieces only on a staircase nobody has reached.
    /// </summary>
    [Fact] public void AHalfBuiltStaircaseFinishesAsItWasStartedUnderALaterKit()
    {
        WorldGenerator world = WorldGenerator.instance!;
        StairRun built = AddStaircase(world, -100f), unreached = AddStaircase(world, 170f);
        Reload();
        Assert.Empty(BuiltStairs.Sites);
        StairRun run = TheRun(built.From);
        // What this build's kit lays over the layout: its walking pieces, and its supports (StairSupport).
        List<StructurePiece> dressed = StairStructures.PiecesOf(run, world);
        List<string> layout = Keys(dressed);
        Assert.Equal(Keys(built.Pieces.Where(p => p.Kind != StructurePieceKind.StairSupport).ToList()),
            Keys(StairStructures.PiecesOf(run, world).Where(p => StairStructures.IsLayoutKind(p.Kind) && p.Kind != StructurePieceKind.StairSupport).ToList()));
        List<Vector2s> zones = ZonesOf(run);
        Assert.True(StructurePlacement.OnZoneSpawned(zones[0], ZoneSystem.SpawnMode.Full) > 0);
        BuiltStair site = Assert.Single(BuiltStairs.Sites);
        Assert.Equal(layout, Keys(site.Pieces));
        Assert.Equal(StairKit.DressingVersion, site.Layout);
        Assert.Same(StairKit.Stone, site.Kit);
        Assert.Equal(run.Ground, site.Ground.Take(run.Ground.Count));
        Reload();
        Assert.Equal(layout, Keys(Assert.Single(BuiltStairs.Sites).Pieces));

        // The later build: its kit's columns are another piece, and it hangs a light on every step.
        Func<StairRun, WorldGenerator, List<StructurePiece>> today = StairStructures.Dresser;
        try
        {
            StairStructures.Dresser = (r, w) =>
            {
                List<StructurePiece> pieces = today(r, w);
                foreach (StructurePiece p in pieces) if (p.Kind == StructurePieceKind.StairSupport) p.Prefab = "later_pillar";
                pieces.AddRange(pieces.Where(p => p.Kind == StructurePieceKind.StairStep).Select(p => new StructurePiece
                    { Kind = StructurePieceKind.Flair, Prefab = "later_light", Position = p.Position + new Vector3(0f, 2f, 0f) }).ToList());
                return pieces;
            };
            StructurePlans.InvalidatePlans();
            run = TheRun(built.From);
            Assert.Equal(layout, Keys(StairStructures.PiecesOf(run, world)));
            List<Vector2s> rest = ZonesOf(run).Skip(1).ToList();
            foreach (Vector2s zone in rest) StructurePlacement.OnZoneSpawned(zone, ZoneSystem.SpawnMode.Full);
            List<Vector2s> all = ZonesOf(run);
            Assert.Equal(Expected(dressed), LiveIn(all));

            // Nobody had reached the other: it takes the later kit, and is saved in it.
            StairRun other = TheRun(unreached.From);
            List<StructurePiece> later = StairStructures.PiecesOf(other, world);
            Assert.Contains(later, p => p.Prefab == "later_light");
            List<Vector2s> otherZones = ZonesOf(other);
            Assert.Empty(otherZones.Intersect(all));
            foreach (Vector2s zone in otherZones) StructurePlacement.OnZoneSpawned(zone, ZoneSystem.SpawnMode.Full);
            Assert.Equal(Keys(later), Keys(Assert.Single(BuiltStairs.Sites, s => s.From == other.From).Pieces));
            Assert.Equal(Expected(later), LiveIn(otherZones));
        }
        finally { StairStructures.Dresser = today; }
    }

    /// <summary>A staircase with pieces standing and no saved site (built before staircases were saved, or its record lost)
    /// is started: reaching another of its zones lays it zone by zone from its layout, as before, saving nothing.</summary>
    [Fact] public void AStaircaseWithZonesBuiltAndNoSavedSiteIsLaidZoneByZone()
    {
        StairRun run = AddStaircase(WorldGenerator.instance!, -100f);
        // Its layout as its kit lays it: the walking pieces and what holds them up.
        List<StructurePiece> dressed = StairStructures.Dresser(run, WorldGenerator.instance!);
        List<Vector2s> zones = ZonesOf(run);
        Assert.True(StructurePlacement.OnZoneSpawned(zones[0], ZoneSystem.SpawnMode.Full) > 0);
        BuiltStairs.Reset();
        StructurePlans.ForgetSpawned();
        foreach (Vector2s zone in zones.Skip(1)) Assert.True(StructurePlacement.OnZoneSpawned(zone, ZoneSystem.SpawnMode.Full) > 0);
        Assert.Empty(BuiltStairs.Sites);
        Assert.Equal(Expected(dressed), LiveIn(zones));
    }

    /// <summary>A full respawn (road_bridges) builds the staircases again from their layouts: the saved pieces go with the
    /// old pieces. A saved staircase survives a save, and its record reads back as it was written.</summary>
    [Fact] public void ASavedStaircaseSurvivesASaveAndAFullRespawnForgetsIt()
    {
        StairRun run = AddStaircase(WorldGenerator.instance!, -100f);
        List<StructurePiece> dressed = StairStructures.Dresser(run, WorldGenerator.instance!);
        List<Vector2s> zones = ZonesOf(run);
        Assert.True(StructurePlacement.OnZoneSpawned(zones[0], ZoneSystem.SpawnMode.Full) > 0);
        byte[] saved = BuiltStairs.Serialize();
        Reload();
        Assert.Equal(saved, BuiltStairs.Serialize());
        BuiltStair site = Assert.Single(BuiltStairs.Parse(saved));
        Assert.Equal(Keys(dressed), Keys(site.Pieces));
        StructurePlacement.ClearSpawnedPieces();
        Assert.Empty(BuiltStairs.Sites);
        Assert.Single(StairStructures.Runs);
    }
}
