using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Keeps the current <see cref="RoadTopology"/>: on the server (or host) it is rebuilt whenever the network's version
/// changes (a generation, a load, a manual road, a clear). A client with the mod asks for it once, when it joins, and keeps
/// the copy it receives; the server sends it then (or as soon as it is built), and again only when an admin changes the
/// network. Nothing is sent on a timer. A client never holds a network or writes terrain: the copy is read-only shape and
/// connections. A client without the mod never asks and is unaffected.
/// </summary>
public static class RoadTopologyService
{
    private const string RequestRpc = "ProceduralRoads.Topology.Request1";
    private const string DataRpc = "ProceduralRoads.Topology.Data1";
    private const byte KindNone = 0, KindChunk = 1;
    /// <summary>The largest piece of a copy sent in one message, under the transport's reliable message size.</summary>
    private const int ChunkBytes = 256 * 1024;

    private static ZNet? s_session;
    private static ZRoutedRpc? s_rpc;
    private static Task<RoadTopology>? s_building;
    private static int s_buildingVersion, s_failedVersion;
    /// <summary>Client: the one request of this session has been sent.</summary>
    private static bool s_requested;
    /// <summary>Server: the peers that asked, and the hash of the copy each was last sent (0: none yet).</summary>
    private static readonly Dictionary<long, long> s_subscribers = new();
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
        s_subscribers.Clear(); s_received = null; s_receivingHash = 0;
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
            SendToSubscribers();
            return;
        }
        // While a generation holds the world the copy is left as it was: its old network stays until the new one is published.
        if (RoadLifecycleManager.HoldsTheWorld) return;
        if (!RoadNetworkGenerator.RoadsAvailable)
        {
            if (Current != null) { Publish(null); SendToSubscribers(); }
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
        List<RoadTopology.SourcePlace> places = new();
        if (ZoneSystem.instance != null && ZoneSystem.instance.GetLocationList() is { } locations)
            foreach (ZoneSystem.LocationInstance location in locations)
            {
                // Every place joins the roads arriving at it; only a registered or configured one is offered a way in.
                ZoneSystem.ZoneLocation? definition = location.m_location;
                string? name = definition?.m_prefab.Name;
                if (definition == null || name == null) continue;
                Vector2 centre = new(location.m_position.x, location.m_position.z);
                places.Add(new RoadTopology.SourcePlace(centre, RoadNetworkGenerator.ArrivalReach(definition.m_exteriorRadius),
                    RoadNetworkGenerator.IsCustomLocation(name)));
            }
        float reach = RoadRide.Reach;
        s_buildingVersion = version;
        s_building = Task.Run(() => RoadTopology.Build(version, reach, points, links, places));
    }

    private static void Log(RoadTopology t)
    {
        List<int> sizes = t.ComponentSizes();
        string largest = string.Join(", ", sizes.GetRange(0, Math.Min(5, sizes.Count)));
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(
            $"Road topology {t.Hash:x16} for network {t.NetworkVersion}: {t.PointCount} points, {t.Components} connected network(s) " +
            $"(largest {largest}), {t.LinksJoined}/{t.LinksGiven} bridge and staircase ends joined, {t.AccessCount} place(s) with a way in, " +
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

    /// <summary>Server: a joining client asks once. It is sent the current copy (if any) and every later copy.</summary>
    private static void OnRequest(long sender, ZPackage package)
    {
        if (!RoadLifecycleManager.HoldsTheNetwork || sender == ZNet.GetUID() || ZNet.instance.GetPeer(sender) == null) return;
        try
        {
            // A client of another format gets nothing: it keeps no copy and its road features stay unavailable.
            if (package.ReadInt() != RoadTopology.FormatVersion) return;
            if (!s_subscribers.ContainsKey(sender)) s_subscribers[sender] = 0L;
            SendTo(sender);
        }
        catch (Exception e) when (e is System.IO.EndOfStreamException || e is ArgumentException)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning("Ignored a malformed road topology request: " + e.Message);
        }
    }

    /// <summary>Server: send the current copy to every client that asked and is still connected (after the copy changed).</summary>
    private static void SendToSubscribers()
    {
        foreach (long peer in new List<long>(s_subscribers.Keys))
            if (ZNet.instance == null || ZNet.instance.GetPeer(peer) == null) s_subscribers.Remove(peer);
            else SendTo(peer);
    }

    /// <summary>Server: send one peer the current copy, at most once per copy; or tell it there is none, once, after it had
    /// one. Repeated requests send nothing more.</summary>
    private static void SendTo(long peer)
    {
        RoadTopology? current = Ready ? Current : null;
        long sent = s_subscribers[peer];
        if (current == null)
        {
            if (sent == 0L) return;
            s_subscribers[peer] = 0L;
            ZPackage none = new();
            none.Write(KindNone);
            ZRoutedRpc.instance.InvokeRoutedRPC(peer, DataRpc, none);
            return;
        }
        if (sent == current.Hash) return;
        s_subscribers[peer] = current.Hash;
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
            byte kind = package.ReadByte();
            if (kind == KindNone) { s_received = null; Publish(null); return; }
            if (kind != KindChunk) return;
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
                $"Received road topology {copy.Hash:x16}: {copy.PointCount} points, {copy.Components} connected network(s), {copy.AccessCount} place(s) with a way in.");
        }
        catch (Exception e) when (e is System.IO.EndOfStreamException || e is ArgumentException)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning("Ignored a malformed road topology message: " + e.Message);
        }
    }
}
