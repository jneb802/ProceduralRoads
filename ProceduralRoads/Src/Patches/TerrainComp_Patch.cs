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
}
