using HarmonyLib;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>Keep the local host on its existing loading screen while the owned initial network is built, or while it is
/// asked what an explored world's first network does.</summary>
[HarmonyPatch]
internal static class RoadGeneration_Patch
{
    [HarmonyPatch(typeof(Game), "UpdateRespawn")]
    [HarmonyPrefix]
    private static bool BeforeRespawn() => !RoadLifecycleManager.HoldsLoadingScreen;

    // Shutdown destroys ZNetScene before ZoneSystem.OnDestroy. Drain while the world's managed terrain and caches still exist.
    [HarmonyPatch(typeof(Game), "Shutdown")]
    [HarmonyPrefix]
    private static void BeforeShutdown() { RoadGenerationPanel.Clear(); ExistingWorldChoicePanel.Clear(); RoadLifecycleManager.CancelAndDrain(); }

    [HarmonyPatch(typeof(GameCamera), "UpdateMouseCapture")]
    [HarmonyPrefix]
    private static bool UpdateMouseCapture()
    {
        if(!RoadGenerationPanel.OwnsPointer && !ExistingWorldChoicePanel.OwnsPointer) return true;
        // Skip vanilla's capture branch, which otherwise suppresses mouse input for two frames.
        ZCursor.LockState=CursorLockMode.None;
        ZCursor.Show();
        return false;
    }

    [HarmonyPatch(typeof(Hud), "UpdateProgressIndicator")]
    [HarmonyPrefix]
    private static bool UpdateProgress(Hud __instance, ref bool ___m_progressIndicatorShown)
    {
        var progress = RoadLifecycleManager.Progress;
        if (RoadLifecycleManager.ChoicePending)
        {
            RoadGenerationPanel.Clear();
            ___m_progressIndicatorShown = true;
            ExistingWorldChoicePanel.Show(__instance);
            return false;
        }
        ExistingWorldChoicePanel.Clear();
        if (!RoadLifecycleManager.InitialGenerationActive) { RoadGenerationPanel.Clear(); return true; }
        var indicator = __instance.m_loadingIndicator;
        if (indicator == null) return true;
        // Vanilla owns this field too. Leaving it true lets its next ordinary update hide/restore the indicator on release.
        ___m_progressIndicatorShown = true;
        RoadGenerationPanel.Show(__instance, progress);
        indicator.SetShowProgress(true);
        indicator.SetProgress(progress.Total == 0 ? 0f : (float)progress.Completed / progress.Total);
        indicator.SetText(progress.Text, false);
        return false;
    }
}
