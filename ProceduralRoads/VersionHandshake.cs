using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;

namespace ProceduralRoads
{
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.OnNewConnection))]
    public static class RegisterAndCheckVersion
    {
        private static void Prefix(ZNetPeer peer, ref ZNet __instance)
        {
            // Register version check call
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Registering version RPC handler");
            peer.m_rpc.Register($"{ProceduralRoadsPlugin.ModName}_VersionCheck",
                new Action<ZRpc, ZPackage>(RpcHandlers.RPC_ProceduralRoads_Version));

            // Make calls to check versions
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Invoking version check");
            ZPackage zpackage = new();
            zpackage.Write(ProceduralRoadsPlugin.ModVersion);
            peer.m_rpc.Invoke($"{ProceduralRoadsPlugin.ModName}_VersionCheck", zpackage);
        }
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.RPC_PeerInfo))]
    public static class VerifyClient
    {
        private static bool Prefix(ZRpc rpc, ZPackage pkg, ref ZNet __instance)
        {
            if (!__instance.IsServer() || RpcHandlers.ValidatedPeers.Contains(rpc)) return true;
            // A modded client's version answer is queued on its socket before
            // its PeerInfo, so by now the server has recorded a match, a
            // mismatch, or nothing. Nothing means no ProceduralRoads on the
            // client: it is let in, the roads reach it as vanilla terrain and
            // vanilla pieces. A recorded mismatch is turned away here as well
            // as when its answer arrived, so admission never depends on the
            // client acting on the error it was sent.
            PeerAdmission.Verdict verdict = PeerAdmission.DecideFor(
                validated: false, refused: RpcHandlers.RefusedPeers.Contains(rpc));
            if (!PeerAdmission.Admits(verdict))
            {
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning(
                    $"Peer ({rpc.m_socket.GetHostName()}) with another {ProceduralRoadsPlugin.ModName} version sent PeerInfo; refused");
                rpc.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorVersion);
                return false;
            }
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(
                $"A peer without {ProceduralRoadsPlugin.ModName} joined: it receives the roads as vanilla terrain");
            return true;
        }

        private static void Postfix(ZNet __instance)
        {
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(),
                $"{ProceduralRoadsPlugin.ModName}RequestAdminSync",
                new ZPackage());
        }
    }

    [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.ShowConnectError))]
    public class ShowConnectionError
    {
        private static void Postfix(FejdStartup __instance)
        {
            if (__instance.m_connectionFailedPanel.activeSelf)
            {
                __instance.m_connectionFailedError.fontSizeMax = 25;
                __instance.m_connectionFailedError.fontSizeMin = 15;
                __instance.m_connectionFailedError.text += "\n" + ProceduralRoadsPlugin.ConnectionError;
            }
        }
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
    public static class RemoveDisconnectedPeerFromVerified
    {
        private static void Prefix(ZNetPeer peer, ref ZNet __instance)
        {
            if (!__instance.IsServer()) return;
            // Remove peer from validated list
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(
                $"Peer ({peer.m_rpc.m_socket.GetHostName()}) disconnected, removing from validated list");
            _ = RpcHandlers.ValidatedPeers.Remove(peer.m_rpc);
            _ = RpcHandlers.RefusedPeers.Remove(peer.m_rpc);
        }
    }

    public static class RpcHandlers
    {
        public static readonly List<ZRpc> ValidatedPeers = new();
        /// <summary>Peers that answered the version check with another version.</summary>
        public static readonly List<ZRpc> RefusedPeers = new();

        public static void RPC_ProceduralRoads_Version(ZRpc rpc, ZPackage pkg)
        {
            string? version = pkg.ReadString();

            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo("Version check, local: " +
                                                                ProceduralRoadsPlugin.ModVersion +
                                                                ",  remote: " + version);
            if (version != ProceduralRoadsPlugin.ModVersion)
            {
                ProceduralRoadsPlugin.ConnectionError =
                    $"{ProceduralRoadsPlugin.ModName} Installed: {ProceduralRoadsPlugin.ModVersion}\n Needed: {version}";
                if (!ZNet.instance.IsServer()) return;
                // Different versions - force disconnect client from server
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning(
                    $"Peer ({rpc.m_socket.GetHostName()}) has incompatible version, disconnecting...");
                if (!RefusedPeers.Contains(rpc)) RefusedPeers.Add(rpc);
                rpc.Invoke("Error", (int)ZNet.ConnectionStatus.ErrorVersion);
            }
            else
            {
                if (!ZNet.instance.IsServer())
                {
                    // Enable mod on client if versions match
                    ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(
                        "Received same version from server!");
                }
                else
                {
                    // Add client to validated list
                    ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(
                        $"Adding peer ({rpc.m_socket.GetHostName()}) to validated list");
                    ValidatedPeers.Add(rpc);
                }
            }
        }
    }
}