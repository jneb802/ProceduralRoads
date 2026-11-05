using HarmonyLib;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>Keep the local host on its existing loading screen while the owned initial network is built, or while it is
/// asked what an explored world's first network does.</summary>
[HarmonyPatch]
internal static class RoadGeneration_Patch
{
    /// <summary>The host's first spawn point, found by the game while the server terrain bake held the spawn.</summary>
    private static (Game game, Vector3 point, bool usedLogoutPoint)? s_heldSpawn;

    /// <summary>
    /// Respawn waits while the world is held, and the host's first spawn also while the server terrain bake drains behind
    /// the loading screen (ServerTerrainBake.HoldsFirstSpawn). Then zone streaming goes on, and so does the game's own
    /// search for the spawn point: it streams the land in around the point and counts its load wait down meanwhile, so
    /// neither comes after the drain. A point it finds is kept, and the game spawns there once the hold lets go
    /// (<see cref="SpawnPointFoundWhileHeld"/>).
    /// </summary>
    [HarmonyPatch(typeof(Game), "UpdateRespawn")]
    [HarmonyPrefix]
    private static bool BeforeRespawn(Game __instance, float dt)
    {
        if (RoadLifecycleManager.HoldsLoadingScreen) return false;
        if (!ServerTerrainBake.HoldsFirstSpawn) return true;
        if (__instance.m_requestRespawn && !ReferenceEquals(s_heldSpawn?.game, __instance) &&
            __instance.FindSpawnPoint(out Vector3 point, out bool usedLogoutPoint, dt))
            s_heldSpawn = (__instance, point, usedLogoutPoint);
        return false;
    }

    /// <summary>The point the game found while the bake held the spawn is the one it spawns at: finding it again would
    /// not do (the game forgets a logout point once it has found it).</summary>
    [HarmonyPatch(typeof(Game), "FindSpawnPoint")]
    [HarmonyPrefix]
    private static bool SpawnPointFoundWhileHeld(Game __instance, ref Vector3 point, ref bool usedLogoutPoint, ref bool __result)
    {
        if (s_heldSpawn is not { } held) return true;
        s_heldSpawn = null;
        if (!ReferenceEquals(held.game, __instance)) return true;
        point = held.point;
        usedLogoutPoint = held.usedLogoutPoint;
        __result = true;
        return false;
    }

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
        if (!RoadLifecycleManager.InitialGenerationActive)
        {
            RoadGenerationPanel.Clear();
            return !ServerTerrainBake.HoldsFirstSpawn || !ShowBake(__instance, ref ___m_progressIndicatorShown);
        }
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

    /// <summary>While the server terrain bake holds the host's first spawn: what it is doing, on the game's own loading
    /// indicator (it has no islands and no cancel, so not the generation panel). Returns whether it was shown.</summary>
    private static bool ShowBake(Hud hud, ref bool progressIndicatorShown)
    {
        var indicator = hud.m_loadingIndicator;
        if (indicator == null) return false;
        var (written, total) = ServerTerrainBake.LoadScreenProgress();
        progressIndicatorShown = true;
        indicator.SetShowProgress(true);
        indicator.SetProgress(ServerBakeLoadScreen.Fraction(written, total));
        indicator.SetText(ServerBakeLoadScreen.Text(written, total), false);
        return true;
    }
}
