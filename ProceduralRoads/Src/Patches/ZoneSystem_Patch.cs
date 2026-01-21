using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Harmony patches for ZoneSystem and related classes to integrate road generation.
/// This file contains thin wrappers that delegate to dedicated modules.
/// </summary>
public static class ZoneSystem_Patch
{
    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.Start))]
    public static class ZoneSystem_Start_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ZoneSystem __instance)
        {
            RoadLifecycleManager.OnZoneSystemStart(__instance);
        }
    }

    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.PlaceVegetation))]
    public static class ZoneSystem_PlaceVegetation_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(Vector2i zoneID, List<ZoneSystem.ClearArea> clearAreas)
        {
            if (!RoadNetworkGenerator.RoadsGenerated)
                return;

            List<ZoneSystem.ClearArea> roadClearAreas = RoadClearAreaManager.GetOrCreateClearAreas(zoneID);
            clearAreas.AddRange(roadClearAreas);
        }
    }

    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.SpawnZone))]
    public static class ZoneSystem_SpawnZone_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ZoneSystem __instance, Vector2i zoneID, ZoneSystem.SpawnMode mode, ref bool __result)
        {
            if (mode == ZoneSystem.SpawnMode.Client)
                return;

            if (__result && RoadNetworkGenerator.RoadsGenerated)
            {
                List<RoadSpatialGrid.RoadPoint> roadPoints = RoadSpatialGrid.GetRoadPointsInZone(zoneID);
                if (roadPoints.Count > 0)
                    RoadTerrainModifier.ApplyRoadTerrainMods(zoneID, roadPoints);
            }
        }
    }

    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.OnDestroy))]
    public static class ZoneSystem_OnDestroy_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(ZoneSystem __instance)
        {
            RoadLifecycleManager.OnZoneSystemDestroy(__instance);
        }
    }

}
