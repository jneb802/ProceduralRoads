using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Logging;
using Jotunn.Entities;
using Jotunn.Managers;

namespace ProceduralRoads;

/// <summary>
/// The road network's points, sent by the server to its validated same-version modded peers (the version handshake's
/// RpcHandlers.ValidatedPeers) so a client can read them: its road queries answer (RoadSpatialGrid.RoadHeightAt,
/// GetRoadPointsNearPosition, the on-road checks) and RoadLifecycleManager.HasNetwork is true; it writes nothing
/// (RoadLifecycleManager.WritesTheNetwork). Over one Jotunn CustomRPC, which compresses and slices a big package.
/// The one rule for what is sent and when (docs/OWNERS.md), event-driven, never on a timer, a poll or an Update loop:
/// <list type="bullet">
/// <item>the whole network once, when a peer joins (its PeerInfo handled: <see cref="Publish"/>);</item>
/// <item>after that only when the network changed (RoadSpatialGrid.RoadNetworkVersion: a generation ended, road_regen_island,
/// a manual road), once per change (<see cref="Publish"/> from the lifecycle and ManualRoads.Commit);</item>
/// <item>"gone" when a generation starts (<see cref="Gone"/>);</item>
/// <item>never again to a peer already sent the current version (<see cref="s_sent"/>).</item>
/// </list>
/// A vanilla peer, a peer with another version, a dedicated server alone and single player are sent nothing.
/// </summary>
public static class RoadNetworkSync
{
    private static ManualLogSource Log => ProceduralRoadsPlugin.ProceduralRoadsLogger;

    internal const string RpcName = "ProceduralRoads_RoadNetwork";

    /// <summary>The package's own layout: [format][mod version][network version][kind], then for <see cref="Full"/> [count]
    /// and per record [name][bytes]. A client refuses any other format or version.</summary>
    internal const int Format = 1;
    private const byte Full = 1, GoneKind = 2;

    private static CustomRPC? s_rpc;

    /// <summary>Whether a connected peer is one the version handshake validated (same version, modded): given once, with the
    /// RPC (<see cref="Register"/>; the plugin's is RpcHandlers.ValidatedPeers). Before that, no peer is.</summary>
    private static Func<ZNetPeer, bool> s_isValidated = _ => false;

    /// <summary>The network version each peer (by id) was last sent: 0 for "gone". A peer not here was sent nothing yet.</summary>
    private static readonly Dictionary<long, int> s_sent = new();

    /// <summary>Register the RPC, once, before the game starts (Jotunn registers its RPCs at Game.Start), with the check of
    /// which peers the version handshake validated.</summary>
    public static void Register(Func<ZNetPeer, bool> isValidated)
    {
        s_isValidated = isValidated;
        s_rpc = NetworkManager.Instance.AddRPC(RpcName, OnServerReceive, OnClientReceive);
    }

    /// <summary>The world unloaded, or a peer left: what it was sent is forgotten.</summary>
    internal static void Reset() => s_sent.Clear();

    internal static void PeerLeft(long uid) => s_sent.Remove(uid);

    /// <summary>The server, once anything may have changed what its peers should hold (a peer joined, a generation ended, a
    /// manual road was added, a network was loaded): every validated peer not already at the current network is sent it
    /// whole, or "gone" when there is none. A peer at the current version is sent nothing.</summary>
    internal static void Publish() => SendTo(RoadLifecycleManager.HasNetwork ? RoadSpatialGrid.RoadNetworkVersion : 0);

    /// <summary>The server: a generation started; every peer sent a network is told it is gone.</summary>
    internal static void Gone() => SendTo(0);

    private static void SendTo(int version)
    {
        if (s_rpc == null || !RoadLifecycleManager.HoldsTheNetwork || ZNet.instance == null) return;
        byte[]? full = null;
        foreach (ZNetPeer peer in ZNet.instance.GetPeers())
        {
            if (peer == null || !peer.IsReady() || !s_isValidated(peer)) continue;
            bool known = s_sent.TryGetValue(peer.m_uid, out int at);
            if (known ? at == version : version == 0) { s_sent[peer.m_uid] = version; continue; }
            byte[] bytes = version == 0 ? Package(0, null) : full ??= Package(version, RoadNetworkGenerator.SentRecords());
            s_rpc.SendPackage(peer.m_uid, new ZPackage(bytes));
            s_sent[peer.m_uid] = version;
            Log.LogInfo(version == 0
                ? $"[SYNC] told peer {peer.m_uid} the road network is gone"
                : $"[SYNC] sent peer {peer.m_uid} the road network {version} ({bytes.Length} bytes before compression)");
        }
    }

    /// <summary>The package for <paramref name="version"/> (0: gone) and its records.</summary>
    internal static byte[] Package(int version, IReadOnlyList<(string name, byte[] bytes)>? records)
    {
        var pkg = new ZPackage();
        pkg.Write(Format);
        pkg.Write(ProceduralRoadsPlugin.ModVersion);
        pkg.Write(version);
        pkg.Write(version == 0 ? GoneKind : Full);
        if (version != 0)
        {
            pkg.Write(records!.Count);
            foreach (var (name, bytes) in records) { pkg.Write(name); pkg.Write(bytes); }
        }
        return pkg.GetArray();
    }

    /// <summary>Nothing is taken from a client: the server holds the network.</summary>
    private static IEnumerator OnServerReceive(long sender, ZPackage package) { yield break; }

    private static IEnumerator OnClientReceive(long sender, ZPackage package)
    {
        if (ZNet.instance == null || ZNet.instance.IsServer()) yield break;
        if (ZRoutedRpc.instance == null || sender != ZRoutedRpc.instance.GetServerPeerID())
        {
            Log.LogWarning($"[SYNC] a road network package from peer {sender}, not the server: ignored");
            yield break;
        }
        Receive(package);
    }

    /// <summary>
    /// A client: the server's package read. A full network is read through the one reader (RoadNetworkGenerator.LoadReceivedNetwork)
    /// and must come out at the server's version; "gone", another format or mod version, a record not sent by this build,
    /// or a read that fails leaves this client no network. Never generates one. True when a network was taken.
    /// </summary>
    internal static bool Receive(ZPackage package)
    {
        if (RoadLifecycleManager.HoldsTheNetwork) return false;
        string? refused = null;
        int version = 0;
        var records = new Dictionary<int, byte[]>();
        try
        {
            int format = package.ReadInt();
            string mod = format == Format ? package.ReadString() : "";
            if (format != Format) refused = $"package format {format} (this build reads {Format})";
            else if (mod != ProceduralRoadsPlugin.ModVersion) refused = $"sent by {ProceduralRoadsPlugin.ModName} {mod} (this client runs {ProceduralRoadsPlugin.ModVersion})";
            else
            {
                version = package.ReadInt();
                byte kind = package.ReadByte();
                if (kind == GoneKind && version == 0)
                {
                    RoadNetworkGenerator.ForgetReceivedNetwork();
                    Log.LogInfo("[SYNC] the server's road network is gone (a generation started there)");
                    return false;
                }
                if (kind != Full || version == 0) refused = $"package kind {kind} for version {version}";
                else
                {
                    int count = package.ReadInt();
                    if (count < 0 || count > RoadNetworkPersistence.SentRecords.Length) refused = $"{count} records";
                    for (int i = 0; refused == null && i < count; i++)
                    {
                        string name = package.ReadString();
                        byte[] bytes = package.ReadByteArray();
                        int key = Array.FindIndex(RoadNetworkPersistence.SentRecords, r => r.name == name);
                        if (key < 0 || records.ContainsKey(RoadNetworkPersistence.SentRecords[key].key)) refused = $"record {name}";
                        else records[RoadNetworkPersistence.SentRecords[key].key] = bytes;
                    }
                }
            }
        }
        catch (Exception e) when (!(e is OutOfMemoryException)) { refused = $"{e.GetType().Name}: {e.Message}"; }
        if (refused == null && !RoadNetworkGenerator.LoadReceivedNetwork(records)) refused = "its records could not be read (see above)";
        if (refused == null && RoadSpatialGrid.RoadNetworkVersion != version)
            refused = $"it read as network {RoadSpatialGrid.RoadNetworkVersion}, the server's is {version} (another world seed?)";
        if (refused != null)
        {
            RoadNetworkGenerator.ForgetReceivedNetwork();
            Log.LogError($"[SYNC] the server's road network was refused: {refused}; this client has no roads to read");
            return false;
        }
        Log.LogInfo($"[SYNC] received the road network {version}: {RoadSpatialGrid.StoredPointRecords} stored records, " +
                    $"{RoadSpatialGrid.GridCellsWithRoads} cells, {RoadNetworkGenerator.GetRoadStartPoints().Count} start points");
        return true;
    }
}
