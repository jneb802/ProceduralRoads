using HarmonyLib;
using UnityEngine;

namespace ProceduralRoads;

public class ZoneSystem_Patch
{
    [HarmonyPatch(typeof(ZoneSystem), "PlaceLocations")]
    public class ZoneSystem_PostFix
    {
        // Patch the method to inject road generation after the world has been generated
        [HarmonyPostfix]
        public static void Postfix(ZoneSystem __instance)
        {
            if (!RoadGenerator.roadsRendered)
            {
                RoadGenerator.RenderRoads(RoadGenerator.roads);
                RoadGenerator.roadsRendered = true;
            }
        }
    }
}