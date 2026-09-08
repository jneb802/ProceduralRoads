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
            if (!RoadNetworkGenerator.RoadsAvailable)
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
            // Every zone that finishes spawning gets its road terrain, in any spawn
            // mode and whether the network was generated this session or loaded
            // from the save. Zones first created as ghost zones come back later in
            // Client mode; returning early there left them with the road in the data
            // and nothing on the ground. A zone whose terrain compiler is already
            // stamped with the current network version is skipped, so a reload
            // does not overwrite the player's terrain edits in the road (see
            // RoadTerrainModifier.CarriesCurrentRoads). On a real client of a
            // dedicated server the TerrainComp is not owned locally and the
            // modifier skips the zone.
            if (__result && RoadNetworkGenerator.RoadsAvailable)
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
