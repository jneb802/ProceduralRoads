using HarmonyLib;
using UnityEngine;

namespace ProceduralRoads;

public class WorldGenerator_Patch
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

    [HarmonyPatch(typeof(WorldGenerator), "GetBiomeHeight")]
    public static class AddRoadPatch
    {
        static void Postfix(ref float __result, float wx, float wy, ref Color mask, bool preGeneration)
        {
            __result = RoadGenerator.AddRoad(__result, wx, wy);
        }
    }

    [HarmonyPrefix]
    public static bool Prefix(Heightmap __instance, Vector3 worldPos, ref int x, ref int y)
    {
        // Check if the transform is null and log the error
        if (__instance.transform == null)
        {
            Debug.LogError("WorldToVertex: Transform is null for heightmap at position " + worldPos);
            x = 0;
            y = 0;
            return false; // Skip the original method
        }

        // Check if m_width or m_scale are invalid and log the error
        if (__instance.m_width == 0 || __instance.m_scale == 0)
        {
            Debug.LogError("WorldToVertex: Invalid m_width (" + __instance.m_width + ") or m_scale (" + __instance.m_scale + ") for heightmap at position " + worldPos);
            x = 0;
            y = 0;
            return false; // Skip the original method
        }

        // Log that the method is proceeding
        Debug.Log("WorldToVertex: Proceeding with calculation for world position " + worldPos);
        
        // Allow the original method to continue
        return true;
    }
}