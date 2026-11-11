using HarmonyLib;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>Administrators rebuild the server network; clients never change the authoritative graph.</summary>
internal static class RoadGenerationAuthority
{
    private const string Generate = "ProceduralRoads_GenerateNetworkV1";
    private static float nextGenerate;

    internal static bool RequestServer()
    {
        if (ZNet.instance == null || ZNet.instance.IsServer()) return false;
        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), Generate);
        return true;
    }
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Awake))]
    private static class Register
    {
        private static void Postfix()
        {
            nextGenerate = 0;
            ZRoutedRpc.instance.Register(Generate, OnGenerate);
        }
    }
    private static void OnGenerate(long sender)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer() || Time.unscaledTime < nextGenerate) return;
        ZNetPeer peer = ZNet.instance.GetPeer(sender);
        if (peer == null || !peer.IsReady() || !ZNet.instance.IsAdmin(peer.m_socket.GetHostName())) return;
        nextGenerate = Time.unscaledTime + 60;
        RoadNetworkGenerator.GenerateRoads(true);
        RoadClearAreaManager.ClearCache();
        foreach (Heightmap heightmap in Heightmap.GetAllHeightmaps())
        {
            if (heightmap == null) continue;
            Vector2s zone = ZoneSystem.GetZone(heightmap.transform.position);
            System.Collections.Generic.List<RoadSpatialGrid.RoadPoint> points = RoadSpatialGrid.GetRoadPointsInZone(zone);
            TerrainComp terrain = heightmap.GetAndCreateTerrainCompiler();
            if (points.Count > 0 && terrain != null && terrain.m_nview.IsOwner())
                RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, points, heightmap, terrain);
        }
        RoadNetworkGenerator.SaveGlobalRoadData();
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo("Administrator rebuilt and saved the authoritative road network.");
    }
}
