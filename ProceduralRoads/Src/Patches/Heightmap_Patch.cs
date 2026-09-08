using System.Diagnostics;
using HarmonyLib;

namespace ProceduralRoads;

/// <summary>
/// A road write pokes its heightmap with delayed=true, which only queues the
/// rebuild; the game runs Regenerate in its late update. This times that
/// rebuild for the heightmaps we poked (terrain.rebuild in road_timings).
/// </summary>
public static class Heightmap_Patch
{
    [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.Regenerate))]
    public static class Heightmap_Regenerate_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(Heightmap __instance, out long __state)
        {
            __state = RoadTerrainModifier.TakePendingRebuild(__instance) ? Stopwatch.GetTimestamp() : 0;
        }

        [HarmonyPostfix]
        public static void Postfix(Heightmap __instance, long __state)
        {
            if (__state == 0)
                return;
            double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
            RoadTimings.Record("terrain.rebuild", ms, ZoneSystem.GetZone(__instance.transform.position).ToString());
        }
    }
}
