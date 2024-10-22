using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ProceduralRoads;

public class ZoneSystem_Patch
{
    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.SpawnZone))]
    public static class ZoneSystem_SpawnZone_Patch
    {
        private static void Postfix(ZoneSystem __instance, ref Vector2i zoneID)
        {
            List<RoadGenerator.RoadPoint> zoneRoadPoints = RoadGenerator.GetRoadPointsInZone(zoneID);
            foreach (RoadGenerator.RoadPoint roadPoint in zoneRoadPoints)
            {
                RoadGenerator.UpdateRoadPointHeight(roadPoint,__instance);
            }

            foreach (RoadGenerator.RoadPoint roadPoint in zoneRoadPoints)
            {
                RoadGenerator.RenderRoadPoint(roadPoint);
            }

            // foreach (RoadGenerator.RoadPoint roadPoint in zoneRoadPoints)
            // {
            //     RoadGenerator.LevelAreaNearRoadPoint(roadPoint,__instance);
            // }
        }
    }
}