using HarmonyLib;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Harmony patches for Game to handle deferred road loading.
/// </summary>
public static class Game_Patch
{
    [HarmonyPatch(typeof(Game), nameof(Game.SpawnPlayer))]
    public static class Game_SpawnPlayer_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Vector3 spawnPoint)
        {
            RoadLifecycleManager.OnPlayerSpawn(spawnPoint);
        }
    }
}
