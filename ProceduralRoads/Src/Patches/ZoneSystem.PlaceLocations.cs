using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ProceduralRoads;

public static partial class ZoneSystem_Patch
{
    /// <summary>
    /// The seeded turn (<see cref="SeededLocationTurn"/>), the one owner of seeding a Location's turn: a location the roads chose
    /// and predicted, and with the test switch PROCEDURALROADS_FIXED_LOCATION_YAW on (<see cref="FixedLocationYaw"/>) every location
    /// not placed yet, is turned by the game's own turn code with Random seeded (the prefix, <see cref="SeededLocationTurn.BeforePlacing"/>),
    /// Random put back exactly as it was right before SpawnLocation (the transpiler puts <see cref="SeededLocationTurn.End"/> before
    /// the one SpawnLocation call; the finalizer puts it back when PlaceLocations throws before that), and a chosen one's turn and
    /// root checked against the prediction (the postfix). With the test switch on, the one Random.Range(int, int) in PlaceLocations,
    /// the random turn, is also taken through <see cref="SeededLocationTurn.RandomTurn"/> (FixedLocationYaw.Draw). Every other
    /// location: nothing. The prefix runs last, so another mod's prefix cannot draw from Random after the seeding.
    /// </summary>
    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.PlaceLocations))]
    public static class ZoneSystem_PlaceLocations_Patch
    {
        [HarmonyPrefix, HarmonyPriority(Priority.Last)]
        public static void Prefix(ZoneSystem __instance, Vector2s zoneID, out PlacedLocationRecord? __state) =>
            __state = SeededLocationTurn.BeforePlacing(__instance, zoneID);

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpile(instructions, FixedLocationYaw.On);

        /// <summary>The transpiler's work, the test switch given (<paramref name="fixedYaw"/>).</summary>
        internal static List<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, bool fixedYaw)
        {
            var code = new List<CodeInstruction>(instructions);
            var log = ProceduralRoadsPlugin.ProceduralRoadsLogger;
            if (fixedYaw)
            {
                MethodInfo range = AccessTools.Method(typeof(UnityEngine.Random), nameof(UnityEngine.Random.Range), new[] { typeof(int), typeof(int) })!;
                var draws = new List<int>();
                for (int i = 0; i < code.Count; i++) if (code[i].Calls(range)) draws.Add(i);
                if (draws.Count == 1)
                    code[draws[0]].operand = AccessTools.Method(typeof(SeededLocationTurn), nameof(SeededLocationTurn.RandomTurn));
                else
                    log.LogWarning($"PlaceLocations has changed shape ({draws.Count} Random.Range(int, int) draws): a randomly turned location keeps the game's random turn");
            }
            var spawns = new List<int>();
            for (int i = 0; i < code.Count; i++)
                if (code[i].operand is MethodInfo m && m.DeclaringType == typeof(ZoneSystem) && m.Name == "SpawnLocation") spawns.Add(i);
            if (spawns.Count != 1)
            {
                log.LogWarning($"PlaceLocations has changed shape ({spawns.Count} SpawnLocation calls): Random is put back only after the location is placed");
                return code;
            }
            // A call with no arguments and no result leaves SpawnLocation's arguments on the stack as they were.
            code.Insert(spawns[0], new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SeededLocationTurn), nameof(SeededLocationTurn.End))));
            return code;
        }

        [HarmonyPostfix]
        public static void Postfix(ZoneSystem __instance, Vector2s zoneID, PlacedLocationRecord? __state) =>
            SeededLocationTurn.AfterPlacing(__instance, zoneID, __state);

        [HarmonyFinalizer]
        public static System.Exception? Finalizer(System.Exception? __exception)
        {
            SeededLocationTurn.End();
            return __exception;
        }
    }
}
