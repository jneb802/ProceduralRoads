using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace ProceduralRoads;

/// <summary>
/// The game's river weight, read without its shared cache while a generation's workers run (RiverWeightReads, the one
/// owner): for any other world, and whenever no generation's workers run, the game's own GetRiverWeight runs unchanged.
/// Installed once with the mod's other patches. Not opened where another mod patches the method (RiverWeightReads.Begin).
/// </summary>
[HarmonyPatch(typeof(WorldGenerator), "GetRiverWeight")]
internal static class WorldGenerator_GetRiverWeight_Patch
{
    /// <summary>Installed: from now on each generation's start asks which other mods patch the method
    /// (RiverWeightReads.PatchedByOthers), so where one does its rivers are read, not the game's.</summary>
    private static void Prepare(MethodBase original)
    {
        if (original == null) return;
        const string mine = ProceduralRoadsPlugin.ModGUID;
        RiverWeightReads.PatchedByOthers = () =>
        {
            Patches? info = Harmony.GetPatchInfo(original);
            return info == null ? Array.Empty<string>() : info.Owners.Where(owner => owner != mine).Distinct().ToArray();
        };
    }

    private static bool Prefix(WorldGenerator __instance, float wx, float wy, ref float weight, ref float width) =>
        !RiverWeightReads.TryRead(__instance, wx, wy, out weight, out width);
}
