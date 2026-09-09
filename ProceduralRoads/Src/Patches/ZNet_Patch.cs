using HarmonyLib;

namespace ProceduralRoads;

/// <summary>
/// Harmony patches for ZNet: when the world's own data has finished loading.
/// </summary>
public static class ZNet_Patch
{
    /// <summary>
    /// ServerLoadWorld reads the world and its ZDOs, then asks ZoneSystem to
    /// generate locations if it must. Only after it returns is a saved road
    /// network in memory to be found: the locations event alone fires too
    /// early on a world loaded from a save, because ZoneSystem.Load raises it
    /// before ZDOMan.LoadChunks has run.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), "ServerLoadWorld")]
    public static class ZNet_ServerLoadWorld_Patch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            RoadLifecycleManager.OnWorldDataLoaded();
        }
    }
}
