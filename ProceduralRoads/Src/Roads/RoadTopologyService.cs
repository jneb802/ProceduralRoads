using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Keeps the current <see cref="RoadTopology"/>: on the server (or host) it is rebuilt whenever the network's version
/// changes (a generation, a load, a clear). A client with the mod asks for it once, when it joins, and is sent it once: at
/// once, or as soon as the server has built it. Nothing else is ever sent; a client keeps its copy until it leaves. A client
/// never holds a network or writes terrain: the copy is read-only shape and connections. A client without the mod never
/// asks and is unaffected.
/// </summary>
public static class RoadTopologyService
{
    private const string RequestRpc = "ProceduralRoads.Topology.Request1";
    private const string DataRpc = "ProceduralRoads.Topology.Data1";
    private const byte KindChunk = 1;
    /// <summary>The largest piece of a copy sent in one message, under the transport's reliable message size.</summary>
    private const int ChunkBytes = 256 * 1024;

    private static ZNet? s_session;
    private static ZRoutedRpc? s_rpc;
    private static Task<RoadTopology>? s_building;
    private static int s_buildingVersion, s_failedVersion;
    /// <summary>Client: the one request of this session has been sent.</summary>
    private static bool s_requested;
    /// <summary>Server: the peers that asked before there was a copy to send; each is sent the first one built.</summary>
    private static readonly HashSet<long> s_waiting = new();
    private static long s_receivingHash;
    private static byte[][]? s_received;

    /// <summary>The copy this peer answers from, or null.</summary>
    public static RoadTopology? Current { get; private set; }
    /// <summary>Counts every change of <see cref="Current"/> in this session.</summary>
    public static int Revision { get; private set; }
    /// <summary>Whether there is a copy with road in it.</summary>
    public static bool Ready => Current != null && Current.PointCount > 0;

    /// <summary>Called once a frame (ProceduralRoadsPlugin.Update).</summary>
    internal static void Tick()
    {
        if (ZNet.instance == null)
        {
            if (s_session != null) Forget();
            return;
        }
        if (!ReferenceEquals(s_session, ZNet.instance)) { Forget(); s_session = ZNet.instance; }
        if (ZRoutedRpc.instance == null) return;
        if (!ReferenceEquals(s_rpc, ZRoutedRpc.instance))
        {
            s_rpc = ZRoutedRpc.instance;
            s_rpc.Register<ZPackage>(RequestRpc, OnRequest);
            s_rpc.Register<ZPackage>(DataRpc, OnData);
        }
        if (RoadLifecycleManager.HoldsTheNetwork) ServerTick();
        else ClientTick();
    }

    private static void Forget()
    {
        s_session = null; s_rpc = null; s_building = null; s_buildingVersion = s_failedVersion = 0; s_requested = false;
        s_waiting.Clear(); s_received = null; s_receivingHash = 0;
        Publish(null);
    }

    private static void Publish(RoadTopology? topology)
    {
        if (ReferenceEquals(Current, topology)) return;
        Current = topology;
        Revision++;
    }

    private static void ServerTick()
    {
        if (s_building != null)
        {
            if (!s_building.IsCompleted) return;
            Task<RoadTopology> done = s_building;
            s_building = null;
            if (done.IsFaulted || done.IsCanceled)
            {
                s_failedVersion = s_buildingVersion;
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogError("Road topology could not be built: " + done.Exception?.GetBaseException().Message);
                return;
            }
            // A network changed while it was built is built again on the next frame.
            Publish(done.Result);
            Log(done.Result);
            SendToWaiting();
            return;
        }
        // While a generation holds the world the copy is left as it was: its old network stays until the new one is published.
        if (RoadLifecycleManager.HoldsTheWorld) return;
        if (!RoadNetworkGenerator.RoadsAvailable)
        {
            if (Current != null) Publish(null);
            return;
        }
        int version = RoadSpatialGrid.RoadNetworkVersion;
        if (Current != null && Current.NetworkVersion == version) return;
        if (s_failedVersion == version) return;
        // Copied here, on the main thread: the grid, the records and the location list are the game's.
        List<RoadTopology.SourcePoint> points = new();
        foreach (RoadSpatialGrid.RoadPoint p in RoadSpatialGrid.SnapshotAllRoadPoints())
            if (!p.cutOnly) points.Add(new RoadTopology.SourcePoint(p.p, p.h, p.w, p.IsRoad));
        List<(Vector2, Vector2)> links = new();
        foreach (RoadCrossing c in RoadNetworkGenerator.CopyRoadCrossings())
            if (!c.Invalid) links.Add((c.FromBank, c.ToBank));
        foreach (StairRun run in StairStructures.Runs)
            links.Add((run.FromMeet ?? run.From, run.ToMeet ?? run.To));
        List<RoadTopology.SourceLocation> locations = new();
        if (ZoneSystem.instance != null && ZoneSystem.instance.GetLocationList() is { } instances)
            foreach (ZoneSystem.LocationInstance location in instances)
            {
                // Every location joins the roads arriving at it; only a registered or configured one has its road end recorded.
                ZoneSystem.ZoneLocation? definition = location.m_location;
                string? name = definition?.m_prefab.Name;
                if (definition == null || name == null) continue;
                Vector2 centre = new(location.m_position.x, location.m_position.z);
                locations.Add(new RoadTopology.SourceLocation(centre, RoadNetworkGenerator.ArrivalReach(centre, definition.m_exteriorRadius),
                    RoadNetworkGenerator.IsCustomLocation(name)));
            }
        float reach = RoadRide.Reach;
        s_buildingVersion = version;
        s_building = Task.Run(() => RoadTopology.Build(version, reach, points, links, locations));
    }

    private static void Log(RoadTopology t)
    {
        List<int> sizes = t.ComponentSizes();
        string largest = string.Join(", ", sizes.GetRange(0, Math.Min(5, sizes.Count)));
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(
            $"Road topology {t.Hash:x16} for network {t.NetworkVersion}: {t.PointCount} points, {t.Components} connected network(s) " +
            $"(largest {largest}), {t.LinksJoined}/{t.LinksGiven} bridge and staircase ends joined, {t.AccessCount} location(s) with a road end, " +
            $"{t.Bytes.Length} bytes to send.");
    }

    /// <summary>Client: ask the server once this session, as soon as it is known. Its answer comes when it has a copy.</summary>
    private static void ClientTick()
    {
        if (s_requested) return;
        long server = ZRoutedRpc.instance!.GetServerPeerID();
        if (server == 0) return;
        s_requested = true;
        ZPackage request = new();
        request.Write(RoadTopology.FormatVersion);
        ZRoutedRpc.instance.InvokeRoutedRPC(server, RequestRpc, request);
    }

    /// <summary>Server: a joining client asks once, and is sent the copy once: now, or as soon as it is built.</summary>
    private static void OnRequest(long sender, ZPackage package)
    {
        if (!RoadLifecycleManager.HoldsTheNetwork || sender == ZNet.GetUID() || ZNet.instance.GetPeer(sender) == null) return;
        try
        {
            // A client of another format gets nothing: it keeps no copy and its road features stay unavailable.
            if (package.ReadInt() != RoadTopology.FormatVersion) return;
            if (Ready) SendTo(sender, Current!);
            else s_waiting.Add(sender);
        }
        catch (Exception e) when (e is System.IO.EndOfStreamException || e is ArgumentException)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning("Ignored a malformed road topology request: " + e.Message);
        }
    }

    /// <summary>Server: send the first copy built to the clients that asked before it existed and are still connected.</summary>
    private static void SendToWaiting()
    {
        if (!Ready) return;
        foreach (long peer in s_waiting)
            if (ZNet.instance != null && ZNet.instance.GetPeer(peer) != null) SendTo(peer, Current!);
        s_waiting.Clear();
    }

    private static void SendTo(long peer, RoadTopology current)
    {
        byte[] bytes = current.Bytes;
        int chunks = Math.Max(1, (bytes.Length + ChunkBytes - 1) / ChunkBytes);
        for (int i = 0; i < chunks; i++)
        {
            int offset = i * ChunkBytes, length = Math.Min(ChunkBytes, bytes.Length - offset);
            byte[] part = new byte[length];
            Buffer.BlockCopy(bytes, offset, part, 0, length);
            ZPackage chunk = new();
            chunk.Write(KindChunk);
            chunk.Write(current.Hash);
            chunk.Write(i);
            chunk.Write(chunks);
            chunk.Write(part);
            ZRoutedRpc.instance.InvokeRoutedRPC(peer, DataRpc, chunk);
        }
    }

    private static void OnData(long sender, ZPackage package)
    {
        if (RoadLifecycleManager.HoldsTheNetwork || ZRoutedRpc.instance == null || sender != ZRoutedRpc.instance.GetServerPeerID()) return;
        try
        {
            if (package.ReadByte() != KindChunk) return;
            long hash = package.ReadLong();
            int index = package.ReadInt(), count = package.ReadInt();
            byte[] part = package.ReadByteArray();
            if (count <= 0 || count > 4096 || index < 0 || index >= count) return;
            if (s_received == null || s_receivingHash != hash || s_received.Length != count)
            {
                s_received = new byte[count][];
                s_receivingHash = hash;
            }
            s_received[index] = part;
            foreach (byte[]? piece in s_received) if (piece == null) return;
            int total = 0;
            foreach (byte[] piece in s_received) total += piece.Length;
            byte[] whole = new byte[total];
            int at = 0;
            foreach (byte[] piece in s_received) { Buffer.BlockCopy(piece, 0, whole, at, piece.Length); at += piece.Length; }
            s_received = null;
            RoadTopology? copy = RoadTopology.Deserialize(whole);
            if (copy == null || copy.Hash != hash)
            {
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning("The server's road topology could not be read; road features that need it stay unavailable.");
                return;
            }
            Publish(copy);
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(
                $"Received road topology {copy.Hash:x16}: {copy.PointCount} points, {copy.Components} connected network(s), {copy.AccessCount} location(s) with a road end.");
        }
        catch (Exception e) when (e is System.IO.EndOfStreamException || e is ArgumentException)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning("Ignored a malformed road topology message: " + e.Message);
        }
    }
}
