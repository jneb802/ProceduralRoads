using HarmonyLib;
using UnityEngine;

namespace ProceduralRoads;

public class WorldGenerator_Patch
{
    [HarmonyPatch(typeof(WorldGenerator), "Pregenerate")]
    public class WorldGeneratorPatch
    {
        [HarmonyPostfix]
        public static void Postfix(WorldGenerator __instance)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug("Calling Pregenerate");
            RoadGenerator.PlaceRoads(__instance);
        }
    }

    [HarmonyPrefix]
    public static bool Prefix(Heightmap __instance, Vector3 worldPos, ref int x, ref int y)
    {
        if (__instance.transform == null)
        {
            Debug.LogError("WorldToVertex: Transform is null for heightmap at position " + worldPos);
            x = 0;
            y = 0;
            return false;
        }
        
        if (__instance.m_width == 0 || __instance.m_scale == 0)
        {
            Debug.LogError("WorldToVertex: Invalid m_width (" + __instance.m_width + ") or m_scale (" + __instance.m_scale + ") for heightmap at position " + worldPos);
            x = 0;
            y = 0;
            return false;
        }
        
        Debug.Log("WorldToVertex: Proceeding with calculation for world position " + worldPos);
        
        return true;
    }
}