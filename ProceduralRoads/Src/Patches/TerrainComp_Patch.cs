using System.Collections.Generic;
using HarmonyLib;

namespace ProceduralRoads;

/// <summary>
/// The game keeps one terrain compiler per zone and brings the saved one to
/// life from its ZDO only after the zone is loaded. Roads are written into a
/// compiler when it comes alive, so a zone loaded from the save gets them
/// through its own compiler instead of a second one.
/// </summary>
public static class TerrainComp_Patch
{
    [HarmonyPatch(typeof(TerrainComp), nameof(TerrainComp.Awake))]
    public static class TerrainComp_Awake_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(TerrainComp __instance)
        {
            if (!RoadNetworkGenerator.RoadsAvailable)
                return;
            RoadTerrainModifier.OnTerrainCompilerReady(__instance);
        }
    }

    [HarmonyPatch(typeof(TerrainComp), nameof(TerrainComp.ApplyToHeightmap))]
    public static class TerrainComp_ApplyToHeightmap_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(TerrainComp __instance, List<float> heights, Heightmap hm)
        {
            RoadTerrainModifier.ApplyPendingTerrain(__instance, hm, heights);
        }
    }

    [HarmonyPatch(typeof(TerrainComp), nameof(TerrainComp.OnDestroy))]
    public static class TerrainComp_OnDestroy_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(TerrainComp __instance)
        {
            RoadTerrainModifier.OnTerrainCompilerDestroyed(__instance);
        }
    }
}
