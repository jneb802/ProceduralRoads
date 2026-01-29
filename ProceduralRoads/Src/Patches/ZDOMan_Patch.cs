using HarmonyLib;

namespace ProceduralRoads;

/// <summary>
/// Harmony patches for ZDOMan to handle road data persistence.
/// </summary>
public static class ZDOMan_Patch
{
    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.PrepareSave))]
    public static class ZDOMan_PrepareSave_Patch
    {
        [HarmonyPrefix]
        public static void Prefix()
        {
            // Must run BEFORE PrepareSave clones ZDO data, otherwise our changes won't be saved
            RoadLifecycleManager.OnPrepareSave();
        }
    }
}
