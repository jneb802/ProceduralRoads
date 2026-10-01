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
        public static void Prefix(Vector2s zoneID, List<ZoneSystem.ClearArea> clearAreas)
        {
            if (!RoadNetworkGenerator.RoadsAvailable)
                return;

            List<ZoneSystem.ClearArea> roadClearAreas = RoadClearAreaManager.GetOrCreateClearAreas(zoneID);
            clearAreas.AddRange(roadClearAreas);
            List<ZoneSystem.ClearArea> bridgeClearAreas = BridgePlacement.GetClearAreas(zoneID);
            clearAreas.AddRange(bridgeClearAreas);

            // Generated with the road's clear areas: its vegetation matches the
            // network from the start, and the server never clears it later.
            if (roadClearAreas.Count > 0 || bridgeClearAreas.Count > 0)
                VegetationClearing.MarkCleared(zoneID, RoadSpatialGrid.RoadNetworkVersion);
        }

        /// <summary>
        /// A zone generated for a remote peer (ghost mode) is torn down as
        /// soon as it has been populated and only its ZDOs remain, so its road
        /// terrain has to go into a ZDO now, while its heightmap still exists.
        /// SpawnZone's postfix is too late: the zone root is gone by then.
        /// </summary>
        [HarmonyPostfix]
        public static void Postfix(Vector2s zoneID, Heightmap hmap, ZoneSystem.SpawnMode mode)
        {
            if (mode == ZoneSystem.SpawnMode.Ghost)
                ServerTerrainBake.OnGhostZoneGenerated(zoneID, hmap);
        }
    }

    [HarmonyPatch(typeof(ZoneSystem), "Update")]
    public static class ZoneSystem_Update_Patch
    {
        [HarmonyPostfix]
        public static void Postfix() => ServerTerrainBake.Tick();
    }

    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.SpawnZone))]
    public static class ZoneSystem_SpawnZone_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ZoneSystem __instance, Vector2s zoneID, ZoneSystem.SpawnMode mode, ref bool __result)
        {
            // Every zone that comes alive gets its road terrain, whether the
            // network was generated this session or loaded from the save, and
            // whether the zone spawns Full (first time) or Client (generated
            // earlier, as a ghost zone or in another session). Ghost spawns
            // are skipped: their zone root is destroyed on the spot, and a
            // terrain compiler made for it would only duplicate the one the
            // real spawn makes. A zone already stamped with the current network
            // version is left alone, so a reload does not overwrite the
            // player's terrain edits in the road. See RoadTerrainModifier.
            if (!__result || !RoadNetworkGenerator.RoadsAvailable)
                return;

            // Bridge pieces (prototype) go in under every mode: as ZDOs only
            // under ghost generation, the way the game places its own
            // objects, and a zone that already has them is left alone.
            BridgePlacement.OnZoneSpawned(zoneID, mode);

            if (mode == ZoneSystem.SpawnMode.Ghost)
                return;

            List<RoadSpatialGrid.RoadPoint> roadPoints = RoadSpatialGrid.GetRoadPointsInZone(zoneID);
            if (roadPoints.Count > 0)
                RoadTerrainModifier.OnZoneSpawned(zoneID, roadPoints);
        }
    }

    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.OnDestroy))]
    public static class ZoneSystem_OnDestroy_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(ZoneSystem __instance)
        {
            ServerTerrainBake.Reset();
            RoadLifecycleManager.OnZoneSystemDestroy(__instance);
        }
    }

}
