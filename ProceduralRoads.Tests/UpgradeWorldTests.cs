using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ProceduralRoads;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A world saved by an earlier build, opened by this one (the guard <see cref="LegacyWorldTests"/> is for 1.4.4, kept for
/// every build from the release on). The fixture holds what a save left in the world: the network's records on the
/// metadata object, the zones generated, the terrain compilers of the zones written (their terrain and stamps), and the
/// pieces of a bridge site and of a staircase that each have one zone built and the rest not. This build must load it,
/// rewrite no zone already written, and finish the half-built bridge and staircase exactly as the saving build would have.
/// <para>The fixture now is a stand-in, written by this test class from the build that added it
/// (<see cref="WriteFixture"/>), since no release exists yet. At release, replace it with the release's own: with the
/// release build, write it again (ROADS_WRITE_UPGRADE_FIXTURE=&lt;path to Fixtures/upgrade-world.bin&gt; dotnet test -f
/// net10.0 --filter UpgradeWorldTests.WriteFixture), or capture a real world: open a disposable world with the release,
/// build one zone of a bridge and of a staircase, save it (valheim-test save-world), and convert its metadata object,
/// terrain compilers and marked pieces into this file's ZDO section, the finished pieces from finishing a copy of the same
/// world with the release. Never regenerate it with a later build: it is the release's save, fixed.</para>
/// </summary>
public sealed class UpgradeWorldTests : IDisposable
{
    private const string FixtureName = "upgrade-world.bin";
    private const string Magic = "ProceduralRoads upgrade world";
    private const int FixtureFormat = 1;

    private readonly ValheimWorldScope _world = new ValheimWorldScope().WithWorld(new BridgeTests.WideRiverWorld()).WithZdos().WithZoneSystem().WithScene().AsServer();

    public UpgradeWorldTests()
    {
        RoadNetworkPersistence.Reset(); RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld(); NetworkCutoff.Forget();
        RoadNetworkGenerator.MarkLocationsReady(); RoadSiteProtection.Set(Array.Empty<RoadSiteProtection.Footprint>());
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
        public string Build = "";
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

    private static void Write(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
    private static Vector3 ReadVector(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    private static byte[] Serialize(Fixture f)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(Magic); w.Write(FixtureFormat); w.Write(f.Build);
        w.Write(f.Generated.Count); foreach (Vector2s z in f.Generated) { w.Write(z.x); w.Write(z.y); }
        w.Write(f.Prefabs.Count); foreach (string p in f.Prefabs) w.Write(p);
        w.Write(f.Zdos.Count);
        foreach (SavedZdo z in f.Zdos)
        {
            w.Write(z.Prefab); Write(w, z.Position); Write(w, z.Euler);
            foreach (string table in Tables)
            {
                IDictionary values = z.Values[table];
                w.Write(values.Count);
                foreach (DictionaryEntry e in Entries(values).OrderBy(e => (int)e.Key))
                {
                    w.Write((int)e.Key);
                    switch (e.Value)
                    {
                        case int i: w.Write(i); break;
                        case float x: w.Write(x); break;
                        case long l: w.Write(l); break;
                        case string s: w.Write(s); break;
                        case byte[] b: w.Write(b.Length); w.Write(b); break;
                        case Vector3 v: Write(w, v); break;
                        case Quaternion q: Write(w, q.eulerAngles); break;
                        default: throw new InvalidDataException($"No fixture form for {e.Value?.GetType()}.");
                    }
                }
            }
        }
        w.Write(f.Finished.Count);
        foreach (var (prefab, position, euler) in f.Finished) { w.Write(prefab); Write(w, position); Write(w, euler); }
        return stream.ToArray();
    }

    private static Fixture Parse(byte[] bytes)
    {
        using var r = new BinaryReader(new MemoryStream(bytes));
        Assert.Equal(Magic, r.ReadString());
        Assert.Equal(FixtureFormat, r.ReadInt32());
        var f = new Fixture { Build = r.ReadString() };
        for (int n = r.ReadInt32(), i = 0; i < n; i++) f.Generated.Add(new Vector2s(r.ReadInt16(), r.ReadInt16()));
        for (int n = r.ReadInt32(), i = 0; i < n; i++) f.Prefabs.Add(r.ReadString());
        for (int n = r.ReadInt32(), i = 0; i < n; i++)
        {
            var z = new SavedZdo { Prefab = r.ReadInt32(), Position = ReadVector(r), Euler = ReadVector(r) };
            foreach (string table in Tables)
            {
                var values = new Dictionary<int, object>();
                for (int m = r.ReadInt32(), k = 0; k < m; k++)
                {
                    int key = r.ReadInt32();
                    values[key] = table switch
                    {
                        "m_ints" => r.ReadInt32(),
                        "m_floats" => r.ReadSingle(),
                        "m_longs" => r.ReadInt64(),
                        "m_strings" => r.ReadString(),
                        "m_byteArrays" => r.ReadBytes(r.ReadInt32()),
                        "m_vec3s" => ReadVector(r),
                        _ => Quaternion.Euler(ReadVector(r)),
                    };
                }
                z.Values[table] = values;
            }
            f.Zdos.Add(z);
        }
        for (int n = r.ReadInt32(), i = 0; i < n; i++) f.Finished.Add((r.ReadInt32(), ReadVector(r), ReadVector(r)));
        Assert.Equal(r.BaseStream.Length, r.BaseStream.Position);
        return f;
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

    /// <summary>
    /// The stand-in fixture, written by this build when ROADS_WRITE_UPGRADE_FIXTURE names the file (the test does nothing
    /// otherwise): BridgeLifecycleTests' road over the wide river, one staircase of the network beside it, one zone of each
    /// built (its terrain written and its pieces out) and a road zone written, the world saved; then every zone built.
    /// </summary>
    [Fact]
    public void WriteFixture()
    {
        string? path = Environment.GetEnvironmentVariable("ROADS_WRITE_UPGRADE_FIXTURE");
        if (string.IsNullOrEmpty(path)) return;
        WorldGenerator world = WorldGenerator.instance!;
        NetworkCutoff.SnapshotWorld(Array.Empty<Vector2s>());
        NetworkCutoff.UseNewNetwork();
        StairSiteTests.CommitFixtureRoad();
        StairRun run = StairSiteTests.AddStaircase(world, -100f);

        List<Vector2s> planned = StructurePlans.PlannedZones;
        var fixture = new Fixture { Build = "stand-in: the build that added the upgrade test (no release yet)" };
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
        File.WriteAllBytes(path, Serialize(fixture));
    }

    /// <summary>
    /// The saved world opens with this build: its network loads; the zones it wrote are left exactly as they are by every
    /// automatic writer; the zones built keep their pieces; and building every other zone finishes the half-built bridge
    /// and staircase with the pieces the saving build would have laid, nothing more and nothing else.
    /// </summary>
    [Fact]
    public void AWorldSavedByAnEarlierBuildOpensAndFinishesAsStarted()
    {
        Fixture fixture = Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", FixtureName)));
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
            Heightmap hm = _world.RegisterHeightmap(zone);
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
