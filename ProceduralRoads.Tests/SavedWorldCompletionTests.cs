using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ProceduralRoads;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>A current-format save reopens without rewriting completed terrain and finishes its half-built structures.
/// The fixture is made by this build in the doubles, not evidence of compatibility with an earlier development format.
/// Released-network compatibility is covered by ReleasedSavePolicyTests and its released fixture.</summary>
public sealed class SavedWorldCompletionTests : IDisposable
{
    private readonly ValheimWorldScope _world = new ValheimWorldScope().WithWorld(new BridgeTests.WideRiverWorld()).WithZdos().WithZoneSystem().WithScene().AsServer();

    public SavedWorldCompletionTests()
    {
        RoadNetworkPersistence.Reset(); RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld(); NetworkCutoff.Forget();
        RoadNetworkGenerator.MarkLocationsReady(); RoadLocationProtection.Set(Array.Empty<RoadLocationProtection.Footprint>());
        RoadGrade.Configured = RoadConstants.DefaultMaxRoadGrade; RoadNetworkGenerator.RoadWidth = RoadConstants.DefaultRoadWidth;
    }

    public void Dispose()
    {
        NetworkCutoff.Forget();
        RoadNetworkPersistence.Reset(); RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld();
        _world.Dispose();
    }

    /// <summary>What a save left in the world, and the pieces the saving build finished it with.</summary>
    private sealed class Fixture
    {
        public readonly List<Vector2s> Generated = new();
        public readonly List<string> Prefabs = new();
        public readonly List<SavedZdo> Zdos = new();
        /// <summary>Every marked piece once the saving build built every zone: prefab hash, position, rotation.</summary>
        public readonly List<(int prefab, Vector3 position, Vector3 euler)> Finished = new();
    }

    /// <summary>One saved object: its prefab, place and every value (the doubles' tables, by hash).</summary>
    private sealed class SavedZdo
    {
        public int Prefab;
        public Vector3 Position, Euler;
        public readonly Dictionary<string, IDictionary> Values = new();
    }

    private static readonly string[] Tables = { "m_ints", "m_floats", "m_longs", "m_strings", "m_byteArrays", "m_vec3s", "m_quats" };
    private static IDictionary Table(ZDO zdo, string name) =>
        (IDictionary)typeof(ZDO).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(zdo)!;
    private static List<DictionaryEntry> Entries(IDictionary values)
    {
        var entries = new List<DictionaryEntry>();
        IDictionaryEnumerator e = values.GetEnumerator();
        while (e.MoveNext()) entries.Add(e.Entry);
        return entries;
    }

    /// <summary>The object as a save keeps it: persistent ones only, without an owner (ZDO.RoundTripThroughSave).</summary>
    private static SavedZdo Capture(ZDO zdo)
    {
        var saved = new SavedZdo { Prefab = zdo.GetPrefab(), Position = zdo.GetPosition(), Euler = zdo.GetRotation().eulerAngles };
        foreach (string table in Tables)
        {
            var copy = new Dictionary<int, object>();
            foreach (DictionaryEntry e in Entries(Table(zdo, table))) copy[(int)e.Key] = e.Value is byte[] b ? b.Clone() : e.Value!;
            saved.Values[table] = copy;
        }
        return saved;
    }

    private static ZDO Restore(SavedZdo saved)
    {
        ZDO zdo = ZDOMan.instance!.CreateNewZDO(saved.Position, saved.Prefab);
        zdo.Persistent = true;
        zdo.SetOwner(0);
        zdo.SetRotation(Quaternion.Euler(saved.Euler));
        foreach (string table in Tables)
        {
            IDictionary into = Table(zdo, table);
            foreach (DictionaryEntry e in Entries(saved.Values[table])) into[e.Key] = e.Value;
        }
        return zdo;
    }

    private static bool Marked(ZDO zdo) => zdo.GetInt(StructurePlans.MarkerHash) == 1;
    private static List<(int, Vector3, Vector3)> Sorted(IEnumerable<(int, Vector3, Vector3)> pieces) => pieces
        .OrderBy(p => p.Item2.x).ThenBy(p => p.Item2.y).ThenBy(p => p.Item2.z).ThenBy(p => p.Item1).ThenBy(p => p.Item3.y).ToList();
    private static List<(int, Vector3, Vector3)> LivePieces() => Sorted(ZDOMan.instance!.Zdos
        .Where(z => !ZDOMan.instance.DestroyQueue.Contains(z) && Marked(z)).Select(z => (z.GetPrefab(), z.GetPosition(), z.GetRotation().eulerAngles)));

    private static IEnumerable<Vector2s> ZonesOf(IEnumerable<StructurePiece> pieces) => pieces.Select(p => ZoneSystem.GetZone(p.Position)).Distinct();

    /// <summary>One zone of a bridge and a staircase built and saved, plus the pieces from finishing that save.</summary>
    private Fixture SaveHalfBuiltWorld()
    {
        WorldGenerator world = WorldGenerator.instance!;
        NetworkCutoff.SnapshotWorld(Array.Empty<Vector2s>());
        NetworkCutoff.BeginNewNetwork(); NetworkCutoff.UseNewNetwork();
        StairSiteTests.CommitFixtureRoad();
        StairRun run = StairSiteTests.AddStaircase(world, -100f);

        List<Vector2s> planned = StructurePlans.PlannedZones;
        var fixture = new Fixture();
        fixture.Prefabs.AddRange(planned.SelectMany(z => StructurePlans.PlanFor(z)!).Select(p => p.Prefab).Distinct().OrderBy(p => p, StringComparer.Ordinal));
        foreach (string prefab in fixture.Prefabs) ZNetScene.instance!.AddPrefab(prefab);
        RoadCrossing site = Assert.Single(BridgeLayout.DistinctSites(RoadNetworkGenerator.GetRoadCrossings()), BridgeStructures.HasDeck);
        List<Vector2s> bridgeZones = ZonesOf(BridgeStructures.PiecesOf(site, world, world.GetSeed())).OrderBy(z => z.x).ThenBy(z => z.y).ToList();
        List<Vector2s> stairZones = ZonesOf(StairStructures.PiecesOf(run, world)).OrderBy(z => z.y).ThenBy(z => z.x).ToList();
        Assert.True(bridgeZones.Count >= 2 && stairZones.Count >= 2);
        Assert.Empty(bridgeZones.Intersect(stairZones));

        // The first session: one zone of each structure generated (terrain and pieces), and a road zone east of the river.
        var written = new List<Vector2s> { bridgeZones[0], stairZones[0], new Vector2s(2, 0) };
        foreach (Vector2s zone in written)
        {
            ZoneSystem.instance!.Generated.Add(zone);
            Heightmap hm = _world.RegisterHeightmap(zone);
            RoadTerrainModifier.OnZoneSpawned(zone, RoadSpatialGrid.GetRoadPointsInZone(zone));
            Assert.NotNull(hm.m_terrainComp!.m_nview.GetZDO().GetByteArray(ZDOVars.s_TCData));
            StructurePlacement.OnZoneSpawned(zone, ZoneSystem.SpawnMode.Full);
        }
        Assert.Single(BuiltBridges.Sites);
        Assert.Single(BuiltStairs.Sites);
        Assert.Equal(0, StructureAppendQueue.Count);
        Assert.All(bridgeZones.Skip(1).Concat(stairZones.Skip(1)), zone => Assert.False(StructurePlans.IsSpawned(zone)));
        RoadLifecycleManager.OnPrepareSave();
        fixture.Generated.AddRange(ZoneSystem.instance!.Generated.OrderBy(z => z.x).ThenBy(z => z.y));
        fixture.Zdos.AddRange(ZDOMan.instance!.Zdos.Where(z => z.Persistent && !ZDOMan.instance.DestroyQueue.Contains(z)).Select(Capture));

        // The saving build finishing it: every planned zone built.
        foreach (Vector2s zone in planned) StructurePlacement.OnZoneSpawned(zone, ZoneSystem.SpawnMode.Full);
        Assert.All(planned, zone => Assert.True(StructurePlans.IsSpawned(zone)));
        fixture.Finished.AddRange(LivePieces());
        return fixture;
    }

    /// <summary>
    /// The saved world opens in a fresh session: completed terrain is left as saved by every automatic writer, existing
    /// pieces remain, and the unfinished zones finish the bridge and staircase exactly as the original session did.
    /// </summary>
    [Fact]
    public void AHalfBuiltWorldReopensAndFinishesWithoutRewritingItsTerrain()
    {
        Fixture fixture = SaveHalfBuiltWorld();
        RoadNetworkPersistence.Reset(); RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld(); NetworkCutoff.Forget();
        using var reopened = new ValheimWorldScope().WithWorld(new BridgeTests.WideRiverWorld()).WithZdos().WithZoneSystem().WithScene().AsServer();
        RoadNetworkGenerator.MarkLocationsReady(); RoadLocationProtection.Set(Array.Empty<RoadLocationProtection.Footprint>());
        foreach (Vector2s zone in fixture.Generated) ZoneSystem.instance!.Generated.Add(zone);
        foreach (string prefab in fixture.Prefabs) ZNetScene.instance!.AddPrefab(prefab);
        var restored = fixture.Zdos.Select(Restore).ToList();
        int compilerHash = TerrainComp.PrefabName.GetStableHashCode();
        List<ZDO> compilers = restored.Where(z => z.GetPrefab() == compilerHash).ToList();
        List<ZDO> pieces = restored.Where(Marked).ToList();
        Assert.NotEmpty(compilers);
        Assert.NotEmpty(pieces);
        Assert.True(fixture.Finished.Count > pieces.Count, "the fixture's structures must be half built");

        // The world opens: the zones it generated are known before the network loads, as the game's load knows them.
        NetworkCutoff.SnapshotWorld(fixture.Generated);
        Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData(), "the saved network must load");
        RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        Assert.Empty(RoadNetworkPersistence.KeptRecords);
        Assert.Single(BuiltBridges.Sites);
        Assert.Single(BuiltStairs.Sites);
        Assert.Single(StairStructures.Runs);
        Assert.All(fixture.Generated, zone => Assert.False(NetworkCutoff.Excludes(zone), $"zone {zone} was generated with the network"));

        // Its written zones loaded, each compiler on its saved object, then every automatic writer as the game calls them.
        var loaded = new List<(Heightmap hm, ZDO zdo, byte[] terrain, List<(int, int)> stamps)>();
        foreach (ZDO zdo in compilers)
        {
            Vector2s zone = zdo.GetSector();
            Heightmap hm = reopened.RegisterHeightmap(zone);
            TerrainComp compiler = hm.m_terrainComp!;
            ZDOMan.instance!.Zdos.Remove(compiler.m_nview.GetZDO());
            compiler.m_nview = new ZNetView(zdo);
            Assert.True(compiler.Load());
            var stamps = Entries(Table(zdo, "m_ints")).Select(e => ((int)e.Key, (int)e.Value!)).OrderBy(e => e.Item1).ToList();
            loaded.Add((hm, zdo, (byte[])zdo.GetByteArray(ZDOVars.s_TCData)!.Clone(), stamps));
        }
        foreach (var (hm, _, _, _) in loaded)
        {
            // ZoneSystem.SpawnZone postfix (a zone generated in an earlier session), then TerrainComp.Awake postfix.
            Vector2s zone = ZoneSystem.GetZone(hm.transform.position);
            RoadTerrainModifier.OnZoneSpawned(zone, RoadSpatialGrid.GetRoadPointsInZone(zone));
            RoadTerrainModifier.OnTerrainCompilerReady(hm.m_terrainComp!);
        }
        // Plugin.Update, every 3 s; a manual-road snapshot's application. (ApplyToLoadedZones writes loaded zones again on
        // purpose, only after a network is generated or at an operator's command: never on a network loaded from the save.)
        for (int i = 0; i < 10; i++) RoadTerrainModifier.SweepUnstamped();
        RoadTerrainModifier.ApplyAdditionsToLoadedZones();
        foreach (var (hm, zdo, terrain, stamps) in loaded)
        {
            Assert.Equal(terrain, zdo.GetByteArray(ZDOVars.s_TCData));
            Assert.Equal(0, hm.m_terrainComp!.SaveCount);
            Assert.Equal(stamps, Entries(Table(zdo, "m_ints")).Select(e => ((int)e.Key, (int)e.Value!)).OrderBy(e => e.Item1).ToList());
        }

        // Every zone built: the half-built structures finish as the saving build would have finished them.
        foreach (Vector2s zone in StructurePlans.PlannedZones) StructurePlacement.OnZoneSpawned(zone, ZoneSystem.SpawnMode.Full);
        Assert.Equal(0, StructureAppendQueue.Count);
        Assert.All(StructurePlans.PlannedZones, zone => Assert.True(StructurePlans.IsSpawned(zone)));
        Assert.Empty(ZDOMan.instance!.DestroyQueue);
        Assert.All(pieces, piece => Assert.Contains(piece, ZDOMan.instance.Zdos));
        Assert.Equal(Sorted(fixture.Finished), LivePieces());
    }
}
