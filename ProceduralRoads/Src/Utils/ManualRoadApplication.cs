using System;
using HarmonyLib;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>Structure pieces appended to the network (a manual road's bridges and staircases) reach the zones the
/// server has loaded: retried every 2 s while any are owed, since a zone's owner may not be ready yet. An appended road's
/// terrain is the sweep's (RoadTerrainModifier.SweepUnstamped), as every road's is. Only the server writes; a client
/// holds no network.</summary>
public static class ManualRoadApplication
{
    private static float nextRetry;

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Update))]
    private static class RetryLocalApplication
    {
        private static void Postfix()
        {
            if (RoadLifecycleManager.IsGenerating || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (StructureAppendQueue.Count == 0 || Time.realtimeSinceStartup < nextRetry) return;
            nextRetry = Time.realtimeSinceStartup + 2f;
            try { StructurePlacement.ApplyPendingAppends(); }
            catch (Exception ex) { ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning($"Manual road application remains pending: {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Start))]
    private static class ResetOnStart
    {
        private static void Postfix() => nextRetry = 0;
    }
}
