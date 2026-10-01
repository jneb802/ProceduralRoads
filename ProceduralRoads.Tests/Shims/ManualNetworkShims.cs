using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class HarmonyPatch : Attribute { public HarmonyPatch(Type type, string method) { } }
}
namespace UnityEngine { public static class Time { public static float realtimeSinceStartup; } }
public sealed class ZNetPeer { }
public sealed class ZNet
{
    public static ZNet instance = new();
    public bool Server;
    public readonly Dictionary<long, ZNetPeer> Peers = new();
    public bool IsServer() => Server;
    public ZNetPeer? GetPeer(long id) => Peers.TryGetValue(id, out var p) ? p : null;
    public void Start() { }
    public void Update() { }
}
public sealed class Player { public void OnSpawned() { } }
public sealed class ZRoutedRpc
{
    public static ZRoutedRpc instance = new();
    public long GetServerPeerID() => 42;
    // Retained to make the original direct-send defect fail this harness.
    public void InvokeRoutedRPC(long peer, string name, params object[] args)
    {
        foreach (var arg in args)
            if (arg is ZPackage p && p.Size() > 512 * 1024)
                throw new InvalidOperationException("Vanilla message exceeds Steam's limit");
    }
    public void Register(string name, Action<long> handler) { }
    public void Register<T>(string name, Action<long, T> handler) { }
}
public sealed class ZPackage
{
    private readonly MemoryStream stream = new();
    public void Write(int value) => new BinaryWriter(stream).Write(value);
    public void Write(byte[] value) { Write(value.Length); new BinaryWriter(stream).Write(value); }
    public int ReadInt() => new BinaryReader(stream).ReadInt32();
    public byte[] ReadByteArray() => new BinaryReader(stream).ReadBytes(ReadInt());
    public void SetPos(int pos) => stream.Position = pos;
    public int Size() => (int)stream.Length;
}
namespace Jotunn.Entities
{
    public sealed class CustomRPC
    {
        public readonly List<(long Peer, ZPackage Package)> Sent = new();
        public Jotunn.Managers.NetworkManager.CoroutineHandler Server = null!, Client = null!;
        public void Initiate() { }
        public void SendPackage(long peer, ZPackage package) => Sent.Add((peer, package));
    }
}
namespace Jotunn.Managers
{
    public sealed class NetworkManager
    {
        public delegate IEnumerator CoroutineHandler(long sender, ZPackage package);
        public static NetworkManager Instance = new();
        public Jotunn.Entities.CustomRPC Rpc = null!;
        public Jotunn.Entities.CustomRPC AddRPC(string name, CoroutineHandler server, CoroutineHandler client)
            => Rpc = new Jotunn.Entities.CustomRPC { Server = server, Client = client };
    }
}
