using System.Collections.Generic;
#if !ROADS_TESTS
using HarmonyLib;
#endif

namespace ProceduralRoads;

public static partial class ZoneSystem_Patch
{
#if !ROADS_TESTS
    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.SpawnZone))]
#endif
    public static class ZoneSystem_SpawnZone_Patch
    {
#if !ROADS_TESTS
        [HarmonyPostfix]
#endif
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
            if (!__result || !RoadLifecycleManager.WritesTheNetwork)
                return;

            // Bridge pieces (prototype) go in under every mode: as ZDOs only
            // under ghost generation, the way the game places its own
            // objects, and a zone that already has them is left alone.
            try
            {
                StructurePlacement.OnZoneSpawned(zoneID, mode);
            }
            finally
            {
                // Placement can resume structures after acquiring location ground. Keep that order, and write the
                // live zone's road terrain even if placement throws. The placement failure still reaches the caller.
                if (mode != ZoneSystem.SpawnMode.Ghost)
                {
                    List<RoadSpatialGrid.RoadPoint> roadPoints = RoadSpatialGrid.GetRoadPointsInZone(zoneID);
                    if (roadPoints.Count > 0)
                        RoadTerrainModifier.OnZoneSpawned(zoneID, roadPoints);
                }
            }
        }
    }

}
