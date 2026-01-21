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
        [HarmonyPostfix]
        public static void Postfix()
        {
            RoadLifecycleManager.OnPrepareSave();
        }
    }
}
