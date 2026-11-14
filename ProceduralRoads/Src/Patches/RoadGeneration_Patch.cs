using HarmonyLib;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// While a road generation runs (the load's, the spawn fallback's, road_generate's, road_regen_island's), or the host is
/// asked about an explored world's first network: hold respawn (and, ZoneSystem_Patch, the server's zone streaming), show
/// the generation panel, free the pointer for it, and drain the generation before the world shuts down. In the world the
/// host's player is held where it stands (no input, no autorun) and is not hurt, so it never walks into land the held
/// server has not made; an operator's generation starts, and goes on, only while the world can be held so
/// (<see cref="WhyTheWorldCannotHold"/>, RoadLifecycleManager.WorldCannotHold).
/// </summary>
[HarmonyPatch]
internal static class RoadGeneration_Patch
{
    /// <summary>Hands <see cref="WhyTheWorldCannotHold"/> to the generation's owner, which asks it as an operator's run starts
    /// and every frame it runs.</summary>
    internal static void Install() => RoadLifecycleManager.WorldCannotHold = WhyTheWorldCannotHold;

    /// <summary>
    /// Why an operator's generation cannot start, or go on, now, or null. While it runs the server makes no new land (its workers read
    /// the world's locations), so: no other player is connected (their new land would stay bare, and a vanilla client cannot be
    /// held), and the host stands on loaded ground: not on a ship (it sails on into unmade water) and not in a teleport (it
    /// lands in unmade land), and not riding (a mount walks on as it was told).
    /// </summary>
    internal static string? WhyTheWorldCannotHold()
    {
        int others = ZNet.instance != null ? ZNet.instance.GetPeers().Count : 0;
        if (others > 0)
            return $"{others} other player(s) are connected. While roads are built the server makes no new land, so theirs would stay bare: build roads with no one else connected.";
        Player? player = Player.m_localPlayer;
        if (player == null) return null;
        if (player.IsDead()) return "Respawn first: roads are built with you standing in the world.";
        if (player.IsTeleporting()) return "Wait for the teleport to end: roads are built with you standing in the world.";
        if (player.GetStandingOnShip() != null || player.IsAttachedToShip())
            return "Go ashore first: a ship sails on into land the world does not make while roads are built.";
        if (player.IsRiding()) return "Dismount first: a mount walks on into land the world does not make while roads are built.";
        return null;
    }

    /// <summary>The local player while a generation holds the world: takes no movement or action input.</summary>
    private static bool Held(Character? character) =>
        RoadLifecycleManager.IsGenerating && character != null && ReferenceEquals(character, Player.m_localPlayer);

    [HarmonyPatch(typeof(PlayerController), "TakeInput")]
    [HarmonyPostfix]
    private static void HoldMovement(PlayerController __instance, ref bool __result)
    {
        Player? player = Player.m_localPlayer;
        if (!Held(player) || !ReferenceEquals(__instance.GetComponent<Player>(), player)) return;
        __result = false;
        // With no input autorun runs on along its last heading (SetControls clears it only with input); held, it stops.
        player!.m_autoRun = false;
    }

    [HarmonyPatch(typeof(Player), nameof(Player.TakeInput))]
    [HarmonyPostfix]
    private static void HoldActions(Player __instance, ref bool __result)
    {
        if (__result && Held(__instance)) __result = false;
    }

    /// <summary>Held, the player is not hurt either: it cannot move away from what attacks it, and respawn is held. Hits come
    /// through RPC_Damage (their effects and stagger skipped too); smoke, burning and poison apply damage directly.</summary>
    [HarmonyPatch(typeof(Character), "RPC_Damage")]
    [HarmonyPrefix]
    private static bool HoldDamage(Character __instance) => !Held(__instance);

    [HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
    [HarmonyPrefix]
    private static bool HoldAppliedDamage(Character __instance) => !Held(__instance);

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
        if (RoadLifecycleManager.HoldsTheWorld) return false;
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
        if (!RoadLifecycleManager.IsGenerating)
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
        indicator.SetProgress(ServerBakeLoadScreen.Fraction(progress.Completed, progress.Total));
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
