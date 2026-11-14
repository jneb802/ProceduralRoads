using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The road network a modded client receives (RoadNetworkSync): the server sends it to its validated same-version peers once
/// when they join and once per network change, never on a timer and never again to a peer already at the current version; the
/// client reads it through the one reader (RoadNetworkPersistence.ReadNetwork) and its road queries answer; a client holding a
/// received network writes nothing (RoadLifecycleManager.WritesTheNetwork) on every write path.
/// </summary>
public sealed class ClientNetworkSyncTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new ValheimWorldScope()
        .WithWorld(new BridgeTests.WideRiverWorld()).WithZdos().WithZoneSystem().WithScene().WithNetwork(server: true);
    private readonly HashSet<long> _modded = new();
    private static readonly Vector2s Zone = new(0, 0);

    public ClientNetworkSyncTests()
    {
        RoadNetworkGenerator.Reset();
        RoadTerrainModifier.ResetForWorld();
        RoadNetworkSync.Reset();
        NetworkCutoff.Forget();
        NetworkCutoff.SnapshotWorld(Array.Empty<Vector2s>());
        NetworkCutoff.BeginNewNetwork();
        NetworkCutoff.UseNewNetwork();
        RoadNetworkSync.Register(peer => _modded.Contains(peer.m_uid));
    }

    public void Dispose()
    {
        RoadNetworkSync.Register(_ => false);
        RoadNetworkSync.Reset();
        RoadTerrainModifier.ResetForWorld();
        RoadNetworkGenerator.Reset();
        NetworkCutoff.Forget();
        _scope.Dispose();
    }

    private static Jotunn.Entities.CustomRPC Rpc => Jotunn.Managers.NetworkManager.Instance.Rpcs[RoadNetworkSync.RpcName];
    private static int Sent => Rpc.Sent.Count;

    /// <summary>The server's network: a road across the zone, saved with a labelled start point and loaded as a server loads it.</summary>
    private static void ServerNetwork(float height = 40f)
    {
        RoadSpatialGrid.CommitLevelled(new List<Vector2> { new(-16f, 0f), new(0f, 0f), new(16f, 0f) },
            new List<float> { height, height, height }, RoadConstants.DefaultRoadWidth);
        RoadSpatialGrid.FinalizeRoadNetwork();
        RoadNetworkPersistence.EnsureMetadataInstance();
        RoadNetworkPersistence.SaveGlobalRoadData(new List<(Vector2, string)> { (new Vector2(-16f, 0f), "Road to the test location") },
            new List<RoadCrossing>(), new HashSet<Vector2s>());
        RoadSpatialGrid.Clear();
        Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData());
        RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        Assert.True(RoadLifecycleManager.WritesTheNetwork);
    }

    private void Join(long uid, bool modded = true)
    {
        ZNet.instance.Peers[uid] = new ZNetPeer();
        if (modded) _modded.Add(uid);
    }

    /// <summary>This process as the client: the server's package received after the client's own state was dropped.</summary>
    private static bool BecomeClientAndReceive(byte[] package)
    {
        ZNet.instance.Server = false;
        RoadNetworkGenerator.ResetData();
        RoadNetworkPersistence.Reset();
        Assert.False(RoadLifecycleManager.HasNetwork);
        return RoadNetworkSync.Receive(new ZPackage(package));
    }

    /// <summary>A client holding the server's network, received through the RPC as a real package.</summary>
    private void ClientWithReceivedNetwork()
    {
        ServerNetwork();
        Join(2);
        RoadNetworkSync.Publish();
        Assert.Equal(1, Sent);
        Assert.True(BecomeClientAndReceive(Rpc.Sent[0].Package.GetArray()));
        Assert.True(RoadLifecycleManager.HasNetwork);
        Assert.False(RoadLifecycleManager.WritesTheNetwork);
    }

    // ---- The bandwidth rule ----

    [Fact]
    public void TheNetworkIsSentOncePerPeerAndNeverAgainWithoutAChange()
    {
        ServerNetwork();
        Join(2); Join(3); Join(4, modded: false);
        RoadNetworkSync.Publish();
        Assert.Equal(new long[] { 2, 3 }, Rpc.Sent.Select(s => s.Peer).OrderBy(p => p));
        // Ticks and repeated publications with no change: nothing more is sent.
        for (int i = 0; i < 100; i++)
        {
            RoadLifecycleManager.Tick();
            RoadNetworkSync.Publish();
        }
        Assert.Equal(2, Sent);
    }

    [Fact]
    public void OneChangeSendsExactlyOncePerValidatedPeer()
    {
        ServerNetwork();
        Join(2); Join(3); Join(4, modded: false);
        RoadNetworkSync.Publish();
        int before = RoadSpatialGrid.RoadNetworkVersion;
        // A change of the network (a regeneration publishes a new version).
        RoadSpatialGrid.CommitLevelled(new List<Vector2> { new(0f, -16f), new(0f, 16f) }, new List<float> { 41f, 41f }, RoadConstants.DefaultRoadWidth);
        RoadSpatialGrid.FinalizeRoadNetwork();
        Assert.NotEqual(before, RoadSpatialGrid.RoadNetworkVersion);
        RoadNetworkSync.Publish(); RoadNetworkSync.Publish(); RoadNetworkSync.Publish();
        Assert.Equal(4, Sent);
        Assert.Equal(new long[] { 2, 3 }, Rpc.Sent.Skip(2).Select(s => s.Peer).OrderBy(p => p));
    }

    [Fact]
    public void APeerThatJoinsLateIsSentTheNetworkOnce()
    {
        ServerNetwork();
        Join(2);
        RoadNetworkSync.Publish();
        Join(3);
        RoadNetworkSync.Publish();
        RoadNetworkSync.Publish();
        Assert.Equal(new long[] { 2, 3 }, Rpc.Sent.Select(s => s.Peer));
        // One who leaves and comes back is sent it again, once.
        RoadNetworkSync.PeerLeft(3);
        RoadNetworkSync.Publish(); RoadNetworkSync.Publish();
        Assert.Equal(new long[] { 2, 3, 3 }, Rpc.Sent.Select(s => s.Peer));
    }

    [Fact]
    public void AGenerationStartTellsEachHolderOnceThatTheNetworkIsGone()
    {
        ServerNetwork();
        Join(2); Join(3);
        RoadNetworkSync.Gone();
        Assert.Equal(0, Sent);                 // never sent a network: nothing to take back
        RoadNetworkSync.Publish();
        RoadNetworkSync.Gone(); RoadNetworkSync.Gone();
        Assert.Equal(4, Sent);
        // The client told: no network to read.
        Assert.False(BecomeClientAndReceive(Rpc.Sent[3].Package.GetArray()));
        Assert.False(RoadLifecycleManager.HasNetwork);
    }

    [Fact]
    public void AClientAndAServerAloneSendNothing()
    {
        ServerNetwork();
        RoadNetworkSync.Publish();
        Assert.Equal(0, Sent);                 // a dedicated server or a host with no modded peer
        Join(2);
        ZNet.instance.Server = false;          // a client never sends
        RoadNetworkSync.Publish();
        Assert.Equal(0, Sent);
    }

    // ---- The client's read ----

    [Fact]
    public void AClientReadsTheServersNetworkAndItsQueriesAnswer()
    {
        ServerNetwork();
        int version = RoadSpatialGrid.RoadNetworkVersion;
        int records = RoadSpatialGrid.StoredPointRecords;
        float height = RoadSpatialGrid.RoadHeightAt(0f, 0.5f, 3f);
        int near = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(0f, 0f, 0f), 6f).Count;
        Assert.False(float.IsNaN(height));
        Join(2);
        RoadNetworkSync.Publish();
        Assert.True(BecomeClientAndReceive(Rpc.Sent[0].Package.GetArray()));
        Assert.True(RoadNetworkGenerator.NetworkReceived);
        Assert.Equal(version, RoadSpatialGrid.RoadNetworkVersion);
        Assert.Equal(records, RoadSpatialGrid.StoredPointRecords);
        Assert.Equal(height, RoadSpatialGrid.RoadHeightAt(0f, 0.5f, 3f));
        Assert.Equal(near, RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(0f, 0f, 0f), 6f).Count);
        Assert.True(RoadSpatialGrid.AnyRoadAt(0f, 0f));
        Assert.True(RoadSpatialGrid.TryGetRoadWithin(new Vector2(0f, 1f), 3f, out _));
        Assert.Equal("Road to the test location", Assert.Single(RoadNetworkGenerator.GetRoadStartPoints()).label);
        Assert.False(RoadLifecycleManager.IsGenerating);
    }

    /// <summary>A client reads the network from the package alone: the metadata ZDO it happens to see (here the server's own,
    /// with its record of the zones generated with the network) is not read for the cutoff, which only a writer needs.</summary>
    [Fact]
    public void AClientReadsNoCutoffFromTheMetadataItSees()
    {
        ServerNetwork();
        RoadNetworkPersistence.SaveZonesWithNetwork(NetworkCutoff.Record(RoadSpatialGrid.RoadNetworkVersion, new HashSet<Vector2s> { Zone })!);
        Assert.NotNull(RoadNetworkPersistence.LoadZonesWithNetwork());
        Join(2);
        RoadNetworkSync.Publish();
        byte[] package = Rpc.Sent[0].Package.GetArray();
        // The client's own world: one zone generated before it joined. Read with the metadata's record, that zone was left alone.
        NetworkCutoff.Forget();
        NetworkCutoff.SnapshotWorld(new[] { new Vector2s(5, 5) });
        Assert.True(BecomeClientAndReceive(package));
        Assert.True(RoadLifecycleManager.HasNetwork);
        Assert.Equal(0, NetworkCutoff.ExcludedCount);
        Assert.False(NetworkCutoff.Keep().HadNetwork);
    }

    [Theory]
    [InlineData("format")]
    [InlineData("version")]
    [InlineData("record")]
    [InlineData("points")]
    [InlineData("identity")]
    public void ARefusedPackageLeavesNoNetworkAndGeneratesNone(string fault)
    {
        ServerNetwork();
        int version = RoadSpatialGrid.RoadNetworkVersion;
        var records = RoadNetworkGenerator.SentRecords();
        byte[] package = fault switch
        {
            "format" => Rewrite(p => { p.Write(RoadNetworkSync.Format + 1); p.Write(ProceduralRoadsPlugin.ModVersion); }),
            "version" => Rewrite(p => { p.Write(RoadNetworkSync.Format); p.Write("another"); p.Write(version); p.Write((byte)1); p.Write(0); }),
            "record" => RoadNetworkSync.Package(version, new List<(string, byte[])> { ("ProceduralRoads_StairRuns", new byte[] { 1 }) }),
            // A road-point record of a format this build does not read: the saved network's own refusal.
            "points" => RoadNetworkSync.Package(version, new List<(string, byte[])> { ("ProceduralRoads_GlobalData", BitConverter.GetBytes(99)) }),
            _ => RoadNetworkSync.Package(version + 1, records),
        };
        Assert.False(BecomeClientAndReceive(package));
        Assert.False(RoadLifecycleManager.HasNetwork);
        Assert.Equal(0, RoadSpatialGrid.StoredPointRecords);
        Assert.False(RoadLifecycleManager.IsGenerating);
        // Nor is anything generated or loaded at the spawn or the load decision: the client holds no network.
        RoadLifecycleManager.OnPlayerSpawn(Vector3.zero);
        Assert.False(RoadLifecycleManager.IsGenerating);
        Assert.False(RoadLifecycleManager.HasNetwork);
    }

    private static byte[] Rewrite(Action<ZPackage> write)
    {
        var p = new ZPackage();
        write(p);
        return p.GetArray();
    }

    // ---- A client holding a received network writes nothing ----

    [Theory]
    [InlineData(ZoneSystem.SpawnMode.Full)]
    [InlineData(ZoneSystem.SpawnMode.Client)]
    public void AClientWritesNoTerrainWhenAZoneSpawns(ZoneSystem.SpawnMode mode)
    {
        ClientWithReceivedNetwork();
        var compiler = _scope.RegisterHeightmap(Zone).m_terrainComp!;
        bool spawned = true;
        ZoneSystem_Patch.ZoneSystem_SpawnZone_Patch.Postfix(ZoneSystem.instance!, Zone, mode, ref spawned);
        Assert.Equal(0, compiler.m_nview.GetZDO().GetInt(RoadTerrainModifier.AppliedVersionHash));
        Assert.DoesNotContain(true, compiler.m_modifiedHeight);
        Assert.Equal(0, RoadTerrainModifier.PendingWriteCount);
    }

    [Fact]
    public void AClientSavesNothing()
    {
        ServerNetwork();
        Join(2);
        RoadNetworkSync.Publish();
        byte[] package = Rpc.Sent[0].Package.GetArray();
        byte[] saved = RoadNetworkPersistence.SavedRecord("ProceduralRoads_GlobalData")!;
        // The client's world: the server's metadata is not in it (it sits far outside every player's area).
        _scope.WithZdos();
        Assert.True(BecomeClientAndReceive(package));
        RoadLifecycleManager.OnPrepareSave();
        RoadNetworkGenerator.SaveGlobalRoadData();
        RoadNetworkGenerator.SaveBridgeZones();
        RoadNetworkGenerator.SaveClearedZones();
        RoadNetworkGenerator.SaveNetworkCutoff();
        Assert.Null(RoadNetworkPersistence.SavedRecord("ProceduralRoads_GlobalData"));
        Assert.NotEmpty(saved);
    }

    [Fact]
    public void AClientAddsNoManualRoad()
    {
        ClientWithReceivedNetwork();
        var refused = Assert.Throws<InvalidOperationException>(() => ManualRoads.RefuseOnReleasedNetwork());
        Assert.Equal(RoadLifecycleManager.NotTheServer, refused.Message);
        Assert.Equal(RoadLifecycleManager.NotTheServer, RoadLifecycleManager.BeginForOperator(null, _ => { }));
    }

    /// <summary>
    /// Every write path in the files the tests cannot compile (Harmony patches and game-loop code) asks the write owner:
    /// vegetation placing, terrain compilers coming alive, the terrain sweep, the rock pass and the server bake. And the
    /// network's availability is read nowhere but through the two owners, so a new path cannot pass a client's network as
    /// one it may write.
    /// </summary>
    [Fact]
    public void EveryWritePathAsksTheWriteOwner()
    {
        string src = Path.Combine(OwnersDocTests.RepoRoot(), "ProceduralRoads", "Src");
        string Code(string file) => OwnerLiteralTests.Code(File.ReadAllText(Path.Combine(src, file)));
        (string file, string line)[] writes =
        {
            ("Patches/ZoneSystem.SpawnZone.cs", "if (!__result || !RoadLifecycleManager.WritesTheNetwork)"),
            ("Patches/ZoneSystem_Patch.cs", "if (!RoadLifecycleManager.WritesTheNetwork)\n                return;\n"),
            ("Patches/TerrainComp_Patch.cs", "if (!RoadLifecycleManager.WritesTheNetwork)\n                return;\n            RoadTerrainModifier.OnTerrainCompilerReady"),
            ("Plugin.cs", "if (RoadLifecycleManager.WritesTheNetwork)\n                RoadTerrainModifier.SweepUnstamped();"),
            ("Roads/RoadRockClearing.cs", "if (!RoadLifecycleManager.WritesTheNetwork || ZoneSystem.instance == null || ZNetScene.instance == null) return;"),
            ("Roads/ServerTerrainBake.cs", "if (s_faults.IsPaused || !RoadLifecycleManager.WritesTheNetwork) return false;"),
            ("Roads/ServerTerrainBake.cs", "if (!RoadLifecycleManager.WritesTheNetwork)\n                return;\n            int version"),
            ("Roads/ServerTerrainBake.cs", "if (!RoadLifecycleManager.WritesTheNetwork || hmap == null"),
        };
        var missing = writes.Where(w => !Regex.Replace(Code(w.file), @"\r", "").Contains(w.line)).Select(w => $"{w.file}: {w.line}").ToList();
        Assert.True(missing.Count == 0, "These write paths no longer ask RoadLifecycleManager.WritesTheNetwork:\n" + string.Join("\n", missing));

        var readers = new List<string>();
        foreach (string file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            string[] lines = OwnerLiteralTests.Code(File.ReadAllText(file)).Split('\n');
            for (int i = 0; i < lines.Length; i++)
                if (Regex.IsMatch(lines[i], @"\bRoadsAvailable\b") && !lines[i].Contains("public static bool RoadsAvailable =>")
                    && !lines[i].Contains("internal static bool HasNetwork =>") && !lines[i].Contains("internal static bool WritesTheNetwork =>"))
                    readers.Add($"{file.Substring(src.Length)}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(readers.Count == 0, "RoadsAvailable is read outside its two owners (read: RoadLifecycleManager.HasNetwork, write: " +
            "RoadLifecycleManager.WritesTheNetwork):\n" + string.Join("\n", readers));
    }
}
