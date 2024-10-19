using HarmonyLib;
using UnityEngine;

namespace ProceduralRoads;

public class GenerateWorld_Patch
{
    [HarmonyPatch(typeof(WorldGenerator), "Pregenerate")]
    public class WorldGeneratorPatch
    {
        // Patch the method to inject road generation after the world has been generated
        [HarmonyPostfix]
        public static void Postfix(WorldGenerator __instance)
        {
            // Call the road generation function after the world generation is complete
            RoadGenerator.PlaceRoads(__instance);
        }
    }
}