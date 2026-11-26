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
/// Another owner hears of each real change (<see cref="NetworkChanged"/>) and may add a record of its own to the same send
/// (<see cref="AddRecord"/>): it rides the package above, at the same times, with no RPC of its own.
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

    /// <summary>
    /// Raised once per real change of the network this peer holds or reads, with its version (0: none). On the server when
    /// <see cref="Publish"/> or <see cref="Gone"/> finds a version other than the one last raised (a generation's end,
    /// road_regen_island, a manual road, a loaded network; "gone" as a generation starts), before any peer is sent it, so a
    /// record's writer (<see cref="AddRecord"/>) reads the new network. On a client once a package was read
    /// (<see cref="Receive"/>: taken, gone or refused). A peer joining changes nothing and raises nothing: it is sent the
    /// current network, every record included. Never on a timer, a poll or an Update loop.
    /// </summary>
    public static event Action<int>? NetworkChanged;

    /// <summary>The version <see cref="NetworkChanged"/> was last raised for on this peer (0: none yet, or none).</summary>
    private static int s_raised;

    /// <summary>The records other owners send with the network (<see cref="AddRecord"/>), after
    /// RoadNetworkPersistence.SentRecords, in the order added.</summary>
    private static readonly List<(string name, Func<byte[]?> write, Action<byte[]?> read)> s_records = new();

    /// <summary>
    /// Another owner's record, sent with the road network in the same package, to the same peers, at the same times (a join,
    /// once per change): no second RPC, and the road points are not sent twice. <paramref name="write"/> runs on the server as
    /// a package is made, after <see cref="NetworkChanged"/> (null leaves the record out of it); <paramref name="read"/> on a
    /// client once the network it came with was taken, with the record's bytes, or null where the package had none. A client
    /// whose network goes is told by <see cref="NetworkChanged"/> (0). Added once, before the game starts, on server and client
    /// alike (one build: a record a client's build does not send refuses the package, as any other). With none added the
    /// package is byte for byte what it was without this.
    /// </summary>
    public static void AddRecord(string name, Func<byte[]?> write, Action<byte[]?> read)
    {
        if (write == null) throw new ArgumentNullException(nameof(write));
        if (read == null) throw new ArgumentNullException(nameof(read));
        if (string.IsNullOrEmpty(name) || Array.Exists(RoadNetworkPersistence.SentRecords, r => r.name == name) || s_records.Exists(r => r.name == name))
            throw new ArgumentException($"a road network record named \"{name}\" is sent already", nameof(name));
        s_records.Add((name, write, read));
    }

    /// <summary>Tests: the records other owners added, dropped.</summary>
    internal static void ForgetRecords() => s_records.Clear();

    /// <summary>Register the RPC, once, before the game starts (Jotunn registers its RPCs at Game.Start), with the check of
    /// which peers the version handshake validated.</summary>
    public static void Register(Func<ZNetPeer, bool> isValidated)
    {
        s_isValidated = isValidated;
        s_rpc = NetworkManager.Instance.AddRPC(RpcName, OnServerReceive, OnClientReceive);
    }

    /// <summary>The world unloaded, or a peer left: what it was sent is forgotten.</summary>
    internal static void Reset() { s_sent.Clear(); s_raised = 0; }

    internal static void PeerLeft(long uid) => s_sent.Remove(uid);

    /// <summary>The server, once anything may have changed what its peers should hold (a peer joined, a generation ended, a
    /// manual road was added, a network was loaded): every validated peer not already at the current network is sent it
    /// whole, or "gone" when there is none. A peer at the current version is sent nothing.</summary>
    internal static void Publish() => SendTo(RoadLifecycleManager.HasNetwork ? RoadSpatialGrid.RoadNetworkVersion : 0);

    /// <summary>The server: a generation started; every peer sent a network is told it is gone.</summary>
    internal static void Gone() => SendTo(0);

    private static void SendTo(int version)
    {
        if (!RoadLifecycleManager.HoldsTheNetwork) return;
        Changed(version);
        if (s_rpc == null || ZNet.instance == null) return;
        byte[]? full = null;
        foreach (ZNetPeer peer in ZNet.instance.GetPeers())
        {
            if (peer == null || !peer.IsReady() || !s_isValidated(peer)) continue;
            bool known = s_sent.TryGetValue(peer.m_uid, out int at);
            if (known ? at == version : version == 0) { s_sent[peer.m_uid] = version; continue; }
            byte[] bytes = version == 0 ? Package(0, null) : full ??= Package(version, Records());
            s_rpc.SendPackage(peer.m_uid, new ZPackage(bytes));
            s_sent[peer.m_uid] = version;
            Log.LogInfo(version == 0
                ? $"[SYNC] told peer {peer.m_uid} the road network is gone"
                : $"[SYNC] sent peer {peer.m_uid} the road network {version} ({bytes.Length} bytes before compression)");
        }
    }

    /// <summary>The server: the records sent with the network, the road network's own (RoadNetworkGenerator.SentRecords), then
    /// each added one its writer gave bytes for (<see cref="AddRecord"/>). A writer that throws leaves its record out.</summary>
    private static List<(string name, byte[] bytes)> Records()
    {
        List<(string name, byte[] bytes)> records = RoadNetworkGenerator.SentRecords();
        foreach (var (name, write, _) in s_records)
        {
            byte[]? bytes = null;
            try { bytes = write(); }
            catch (Exception e) when (!(e is OutOfMemoryException)) { Log.LogError($"[SYNC] the {name} record could not be written, sent without it: {e}"); }
            if (bytes != null) records.Add((name, bytes));
        }
        return records;
    }

    /// <summary><see cref="NetworkChanged"/>, raised where <paramref name="version"/> is not the one last raised. A listener that
    /// throws is logged and keeps no other from hearing it.</summary>
    private static void Changed(int version)
    {
        if (version == s_raised) return;
        s_raised = version;
        if (NetworkChanged == null) return;
        foreach (Delegate listener in NetworkChanged.GetInvocationList())
        {
            try { ((Action<int>)listener)(version); }
            catch (Exception e) when (!(e is OutOfMemoryException)) { Log.LogError($"[SYNC] a road network change listener failed: {e}"); }
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
    /// or a read that fails leaves this client no network. Never generates one. True when a network was taken. The records
    /// other owners added (<see cref="AddRecord"/>) go to their readers once the network was taken; then
    /// <see cref="NetworkChanged"/>.
    /// </summary>
    internal static bool Receive(ZPackage package)
    {
        if (RoadLifecycleManager.HoldsTheNetwork) return false;
        string? refused = null;
        int version = 0;
        var records = new Dictionary<int, byte[]>();
        var added = new Dictionary<string, byte[]>();
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
                    Changed(0);
                    return false;
                }
                if (kind != Full || version == 0) refused = $"package kind {kind} for version {version}";
                else
                {
                    int count = package.ReadInt();
                    if (count < 0 || count > RoadNetworkPersistence.SentRecords.Length + s_records.Count) refused = $"{count} records";
                    for (int i = 0; refused == null && i < count; i++)
                    {
                        string name = package.ReadString();
                        byte[] bytes = package.ReadByteArray();
                        int key = Array.FindIndex(RoadNetworkPersistence.SentRecords, r => r.name == name);
                        if (key >= 0 && !records.ContainsKey(RoadNetworkPersistence.SentRecords[key].key)) records[RoadNetworkPersistence.SentRecords[key].key] = bytes;
                        else if (key < 0 && s_records.Exists(r => r.name == name) && !added.ContainsKey(name)) added[name] = bytes;
                        else refused = $"record {name}";
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
            Changed(0);
            return false;
        }
        Log.LogInfo($"[SYNC] received the road network {version}: {RoadSpatialGrid.StoredPointRecords} stored records, " +
                    $"{RoadSpatialGrid.GridCellsWithRoads} cells, {RoadNetworkGenerator.GetRoadStartPoints().Count} start points");
        foreach (var (name, _, read) in s_records)
        {
            try { read(added.TryGetValue(name, out byte[]? bytes) ? bytes : null); }
            catch (Exception e) when (!(e is OutOfMemoryException)) { Log.LogError($"[SYNC] the {name} record could not be read: {e}"); }
        }
        Changed(version);
        return true;
    }
}
