using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Harmony patches for ZoneSystem and related classes to integrate road generation.
/// This file contains thin wrappers that delegate to dedicated modules.
/// </summary>
public static partial class ZoneSystem_Patch
{
    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.Start))]
    public static class ZoneSystem_Start_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ZoneSystem __instance)
        {
            RoadLifecycleManager.OnZoneSystemStart(__instance);
        }
    }

    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.PlaceVegetation))]
    public static class ZoneSystem_PlaceVegetation_Patch
    {
        /// <summary>Where the roads' clear areas start in the zone being generated, and the structures' after them (the game's
        /// own come first); none before the prefix, and none while no roads are available: every area is then the game's.</summary>
        private static int s_firstRoadArea = int.MaxValue, s_firstStructureArea = int.MaxValue;
        private static readonly List<StairSupport.Walk> NoWalks = new();
        /// <summary>The structures' walks the zone being generated judges its vegetation against.</summary>
        private static IReadOnlyList<StairSupport.Walk> s_walks = NoWalks;
        private static readonly List<VegetationClearing.RoadCentre> NoRoad = new();
        /// <summary>The road points the zone being generated tests its grown trees against.</summary>
        private static List<VegetationClearing.RoadCentre> s_road = NoRoad;
        /// <summary>The network version the zone being generated read its clear areas and road points at, when it has any
        /// (0: none): its clearing is recorded at that version once placement is done (<see cref="Postfix"/>).</summary>
        private static int s_clearedFor;

        [HarmonyPrefix]
        public static void Prefix(Vector2s zoneID, List<ZoneSystem.ClearArea> clearAreas)
        {
            s_firstRoadArea = int.MaxValue;
            s_firstStructureArea = int.MaxValue;
            s_walks = NoWalks;
            s_road = NoRoad;
            s_clearedFor = 0;
            s_owedToBake = false;
            if (!RoadLifecycleManager.WritesTheNetwork)
                return;
            // Recorded once the placement is done (Postfix), not before it has run; never where the transpiler did not put the
            // road's test in (the server bake clears such a zone, RoadClearAreaManager.ForGeneration).
            VegetationClearing.ZoneAreas areas = RoadClearAreaManager.ForGeneration(zoneID, clearAreas, s_judged, out s_road, out s_clearedFor);
            s_firstRoadArea = areas.FirstRoad;
            s_firstStructureArea = areas.FirstStructure;
            s_walks = areas.Walks;
            s_owedToBake = !s_judged && (areas.AnyRoadOrStructure || s_road.Count > 0);
        }

        /// <summary>Whether the transpiler put the road's test (<see cref="InsideClearArea"/>, VegetationClearing.Judge) into
        /// PlaceVegetation: without it a zone is generated with the game's own test over the clear areas alone, and its clearing
        /// is left to the server bake.</summary>
        private static bool s_judged;
        /// <summary>The zone being generated reaches the roads or structures but is generated without the road's test: handed
        /// to the server bake once placed (<see cref="Postfix"/>).</summary>
        private static bool s_owedToBake;

        /// <summary>
        /// PlaceVegetation tests each candidate against the clear areas with ZoneSystem.InsideClearArea, which cannot
        /// tell which vegetation entry asks. The call is replaced by <see cref="InsideClearArea"/>, handed the entry the
        /// loop is on (the closure's <c>veg</c>, loaded the way the method itself loads it) and the scale drawn for the
        /// candidate (the local Random.Range(m_scaleMin, m_scaleMax) is stored to; the entry's m_scaleMax if that is not
        /// found), so the road's clear areas keep trees and stones off the road but not the large rocks the rock
        /// clearing takes there, and a grown tree whose trunk reaches the road is not placed. If the method no longer has that
        /// shape it is left as it is: large rocks stay out of the road's clear areas as before, and grown trees are kept
        /// out of them alone.
        /// </summary>
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            MethodInfo inside = AccessTools.Method(typeof(ZoneSystem), "InsideClearArea");
            int call = code.FindIndex(c => c.Calls(inside));
            int veg = call < 0 ? -1 : code.FindLastIndex(call, c => c.opcode == OpCodes.Ldfld && c.operand is FieldInfo field &&
                field.FieldType == typeof(ZoneSystem.ZoneVegetation));
            if (veg < 1 || !code[veg - 1].IsLdloc())
            {
                s_judged = false;
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning(
                    "ZoneSystem_PlaceVegetation_Patch.Transpiler did not apply (PlaceVegetation has changed shape): generation keeps " +
                    "vegetation and large rocks out of the roads' and staircases' clear areas only, and clears nothing off a bridge's or " +
                    "staircase's walk, no grown tree whose trunk reaches a road and no log lying across one; every zone it generates is " +
                    "left to the server bake's clearing, which judges it by the road's rule");
                return code;
            }
            s_judged = true;
            FieldInfo scaleMax = AccessTools.Field(typeof(ZoneSystem.ZoneVegetation), nameof(ZoneSystem.ZoneVegetation.m_scaleMax));
            MethodInfo range = AccessTools.Method(typeof(UnityEngine.Random), nameof(UnityEngine.Random.Range), new[] { typeof(float), typeof(float) });
            int drawn = call < 0 ? -1 : code.FindLastIndex(call, c => c.LoadsField(scaleMax));
            CodeInstruction[] loadScale = drawn >= 0 && drawn + 2 < call && code[drawn + 1].Calls(range) && code[drawn + 2].IsStloc()
                ? new[] { LoadLocal(code[drawn + 2]) }
                : new[] { new CodeInstruction(code[veg - 1].opcode, code[veg - 1].operand), new CodeInstruction(OpCodes.Ldfld, code[veg].operand),
                    new CodeInstruction(OpCodes.Ldfld, scaleMax) };
            // The yaw drawn for the candidate (Random.Range(0, 360), as an int turned float), by address, so one lying across the
            // road is turned beside it (VegetationClearing.Lie); a dummy if that is not found, and nothing is turned.
            MethodInfo rangeInt = AccessTools.Method(typeof(UnityEngine.Random), nameof(UnityEngine.Random.Range), new[] { typeof(int), typeof(int) });
            int yawDraw = call < 0 ? -1 : code.FindLastIndex(call, c => c.Calls(rangeInt) );
            while (yawDraw > 0 && !(code[yawDraw - 1].opcode == OpCodes.Ldc_I4 && code[yawDraw - 1].operand is int max && max == 360))
                yawDraw = code.FindLastIndex(yawDraw - 1, c => c.Calls(rangeInt));
            CodeInstruction loadYaw = yawDraw > 0 && yawDraw + 2 < call && code[yawDraw + 1].opcode == OpCodes.Conv_R4 && code[yawDraw + 2].IsStloc()
                ? AddressOf(code[yawDraw + 2])
                : new CodeInstruction(OpCodes.Ldsflda, AccessTools.Field(typeof(ZoneSystem_PlaceVegetation_Patch), nameof(s_noYaw)));
            s_yawFound = loadYaw.opcode != OpCodes.Ldsflda;
            if (!s_yawFound)
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning("PlaceVegetation has changed shape: logs lying across a road are removed, never turned beside it");
            // The tilts drawn for it, about x then z (Random.Range(-m_randTilt, m_randTilt), each stored to a local), so armour
            // across the road can be turned so the road runs through it; NaN if they are not found, and it never is.
            FieldInfo randTilt = AccessTools.Field(typeof(ZoneSystem.ZoneVegetation), nameof(ZoneSystem.ZoneVegetation.m_randTilt));
            var tilts = new List<CodeInstruction>();
            int afterDraws = Mathf.Max(drawn, yawDraw);
            for (int k = afterDraws + 1; afterDraws >= 0 && k + 1 < call; k++)
                if (code[k].Calls(range) && code[k - 1].LoadsField(randTilt) && code[k + 1].IsStloc()) tilts.Add(LoadLocal(code[k + 1]));
            if (tilts.Count != 2)
            {
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning("PlaceVegetation has changed shape: ancient armour across a road is never turned so the road runs through it");
                tilts = new List<CodeInstruction> { new CodeInstruction(OpCodes.Ldc_R4, float.NaN), new CodeInstruction(OpCodes.Ldc_R4, float.NaN) };
            }
            code[call].opcode = OpCodes.Call;
            code[call].operand = AccessTools.Method(typeof(ZoneSystem_PlaceVegetation_Patch), nameof(InsideClearArea));
            var load = new List<CodeInstruction> { new CodeInstruction(code[veg - 1].opcode, code[veg - 1].operand), new CodeInstruction(OpCodes.Ldfld, code[veg].operand) };
            load.AddRange(loadScale);
            load.Add(loadYaw);
            load.AddRange(tilts);
            code.InsertRange(call, load);
            return code;
        }

        /// <summary>Where PlaceVegetation's yaw goes when it is not found: never read, and never written back.</summary>
        public static float s_noYaw;
        /// <summary>Whether the transpiler found PlaceVegetation's yaw: without it nothing can be turned, so a log lying
        /// across the road is removed (RoadClearAreaManager.OnRoad's turns).</summary>
        private static bool s_yawFound = true;

        /// <summary>The address of the local <paramref name="store"/> stores to.</summary>
        private static CodeInstruction AddressOf(CodeInstruction store)
        {
            if (store.opcode == OpCodes.Stloc_0) return new CodeInstruction(OpCodes.Ldloca_S, (byte)0);
            if (store.opcode == OpCodes.Stloc_1) return new CodeInstruction(OpCodes.Ldloca_S, (byte)1);
            if (store.opcode == OpCodes.Stloc_2) return new CodeInstruction(OpCodes.Ldloca_S, (byte)2);
            if (store.opcode == OpCodes.Stloc_3) return new CodeInstruction(OpCodes.Ldloca_S, (byte)3);
            return new CodeInstruction(store.opcode == OpCodes.Stloc_S ? OpCodes.Ldloca_S : OpCodes.Ldloca, store.operand);
        }

        /// <summary>The load of the local <paramref name="store"/> stores to.</summary>
        private static CodeInstruction LoadLocal(CodeInstruction store)
        {
            if (store.opcode == OpCodes.Stloc_0) return new CodeInstruction(OpCodes.Ldloc_0);
            if (store.opcode == OpCodes.Stloc_1) return new CodeInstruction(OpCodes.Ldloc_1);
            if (store.opcode == OpCodes.Stloc_2) return new CodeInstruction(OpCodes.Ldloc_2);
            if (store.opcode == OpCodes.Stloc_3) return new CodeInstruction(OpCodes.Ldloc_3);
            return new CodeInstruction(store.opcode == OpCodes.Stloc_S ? OpCodes.Ldloc_S : OpCodes.Ldloc, store.operand);
        }

        /// <summary>The replaced clear-area test. The point stands on the ground; the game raises the object's origin by its
        /// entry's m_groundOffset after the test. How high it will stand is its prefab's solid colliders' box
        /// (PlacedLocationReader.LocalBox) at its scale over that origin (VegetationClearing.Body.Of).</summary>
        public static bool InsideClearArea(ZoneSystem zones, List<ZoneSystem.ClearArea> areas, Vector3 point, ZoneSystem.ZoneVegetation veg, float scale,
            ref float yaw, float tiltX, float tiltZ)
        {
            float origin = point.y + veg.m_groundOffset;
            return RoadClearAreaManager.OnRoad(new VegetationClearing.ZoneAreas(areas, s_firstRoadArea, s_firstStructureArea, s_walks),
                s_road, point, veg.m_prefab.name, scale, BiomeAt, ref yaw, new VegetationClearing.Lean(tiltX, tiltZ, origin),
                turns: s_yawFound, body: VegetationClearing.Body.Of(PlacedLocationReader.LocalBox(veg.m_prefab), origin, scale),
                groundTilt: GroundTilted(veg));
        }

        /// <summary>Whether PlaceVegetation will lay this candidate on the ground's tilt (RoadClearAreaManager.OnRoad's
        /// groundTilt), asked only of one lying along the ground, whose heading it turns. The game draws Random.value against
        /// the entry's m_chanceToUseGroundTilt straight after this test, with no draw between; Random.value is in [0, 1], so a
        /// chance of 1 always takes it, and a lesser one is read off a copy of the generator's state, which is put back.</summary>
        private static bool GroundTilted(ZoneSystem.ZoneVegetation veg)
        {
            float chance = veg.m_chanceToUseGroundTilt;
            if (!(chance > 0f) || !VegetationClearing.IsLying(veg.m_prefab.name.GetStableHashCode())) return false;
            if (chance >= 1f) return true;
            UnityEngine.Random.State state = UnityEngine.Random.state;
            float drawn = UnityEngine.Random.value;
            UnityEngine.Random.state = state;
            return drawn <= chance;
        }

        private static Heightmap.Biome BiomeAt(Vector3 point) => RoadBiome.At(point);

        /// <summary>
        /// A zone generated for a remote peer (ghost mode) is torn down as
        /// soon as it has been populated and only its ZDOs remain, so its road
        /// terrain has to go into a ZDO now, while its heightmap still exists.
        /// SpawnZone's postfix is too late: the zone root is gone by then.
        /// The vegetation is placed (a placement that throws never gets here): the zone's clearing is recorded at the
        /// version its clear areas were read at (VegetationClearing.MarkCleared).
        /// </summary>
        [HarmonyPostfix]
        public static void Postfix(Vector2s zoneID, Heightmap hmap, ZoneSystem.SpawnMode mode)
        {
            if (s_clearedFor != 0) VegetationClearing.MarkCleared(zoneID, s_clearedFor);
            // Generated without the road's test: the bake judges its vegetation now, as a zone generated before its roads.
            if (s_owedToBake) VegetationClearing.Reached(new[] { zoneID });
            s_clearedFor = 0;
            s_owedToBake = false;
            s_firstRoadArea = int.MaxValue;
            s_firstStructureArea = int.MaxValue;
            s_walks = NoWalks;
            s_road = NoRoad;
            if (mode == ZoneSystem.SpawnMode.Ghost)
                ServerTerrainBake.OnGhostZoneGenerated(zoneID, hmap);
        }
    }

    [HarmonyPatch(typeof(ZoneSystem), "Update")]
    public static class ZoneSystem_Update_Patch
    {
        // Location/zone placement mutates world state that the sealed island workers read. HUD and CLI still tick.
        // Held too while the host is asked what an explored world's first network does: nothing new is explored first.
        // In the world the host's player is held where it stands meanwhile (RoadGeneration_Patch), so it never needs a
        // zone this holds back; an operator's generation starts only with no other player connected.
        [HarmonyPrefix]
        public static bool Prefix() => !RoadLifecycleManager.HoldsTheWorld;

        private static float s_nextReveal;

        [HarmonyPostfix]
        public static void Postfix(ZoneSystem __instance)
        {
            if (!RoadLifecycleManager.IsGenerating) ServerTerrainBake.Tick();
            RevealLocationIcons(__instance);
        }

        /// <summary>A location placed before its roads goes on the map once a player has come as near as the
        /// game would have placed it from (<see cref="HiddenLocationIcons"/>): checked twice a second, as the
        /// game's own zone creation for each player and the server's own position.</summary>
        private static void RevealLocationIcons(ZoneSystem zones)
        {
            if (HiddenLocationIcons.Count == 0 || !RoadLifecycleManager.HoldsTheNetwork || Time.time < s_nextReveal) return;
            s_nextReveal = Time.time + 0.5f;
            var references = new List<Vector3> { ZNet.instance.GetReferencePosition() };
            foreach (ZNetPeer peer in ZNet.instance.GetPeers()) references.Add(peer.GetRefPos());
            List<Vector3> shown = HiddenLocationIcons.Reveal(references, zones.m_simulationDistance.TotalSimulationDistance, zones.m_simulationDistance.IsClassic);
            if (shown.Count == 0) return;
            if (RoadNetworkGenerator.RoadsLog)
                foreach (Vector3 p in shown)
                    ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo($"[LOCATIONS] icon at {p} on the map: a player came near enough for the game to have placed it");
            zones.SendLocationIcons(0L);
        }
    }

    /// <summary>The server's map icons without the locations placed before their roads that no player has come
    /// near yet (<see cref="HiddenLocationIcons"/>); the list the server sends every player is built here too.</summary>
    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.GetLocationIcons))]
    public static class ZoneSystem_GetLocationIcons_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Dictionary<Vector3, string> icons)
        {
            if (RoadLifecycleManager.HoldsTheNetwork) HiddenLocationIcons.Filter(icons);
        }
    }

    /// <summary>A location the mod has the game place early (<see cref="HiddenLocationIcons.PlacingEarly"/>) that the game shows
    /// on the map once placed is hidden before PlaceLocations sends the icons (<see cref="HiddenLocationIcons.BeforePlacing"/>);
    /// the game's own placing for a player is left alone.</summary>
    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.PlaceLocations))]
    public static class ZoneSystem_PlaceLocations_Icons_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(ZoneSystem __instance, Vector2s zoneID, out bool __state)
        {
            __state = __instance.m_locationInstances.TryGetValue(zoneID, out ZoneSystem.LocationInstance instance) &&
                      HiddenLocationIcons.BeforePlacing(instance.m_position, instance.m_location.m_iconPlaced, instance.m_location.m_iconAlways, instance.m_placed);
        }

        [HarmonyPostfix]
        public static void Postfix(ZoneSystem __instance, Vector2s zoneID, bool __state)
        {
            if (!__state || !__instance.m_locationInstances.TryGetValue(zoneID, out ZoneSystem.LocationInstance instance)) return;
            HiddenLocationIcons.AfterPlacing(instance.m_position, __state, instance.m_placed);
            if (instance.m_placed && RoadNetworkGenerator.RoadsLog)
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo($"[LOCATIONS] {instance.m_location.m_prefab.Name} at {instance.m_position} placed before its roads: off the map until a player comes near");
        }
    }

    /// <summary>
    /// PROCEDURALROADS_FIXED_LOCATION_YAW (<see cref="FixedLocationYaw"/>): a zone's location is turned from its world seed,
    /// prefab and zone instead of the unseeded Random. Not patched at all with the switch off (Prepare). The prefix seeds
    /// Random for the zone's placing (a slope-turned location's ground samples); SpawnLocation seeds it again with the
    /// game's location seed straight after, so nothing drawn from there on changes. The transpiler takes the one
    /// Random.Range(int, int) in PlaceLocations, the random turn, through <see cref="FixedLocationYaw.Draw"/>.
    /// </summary>
    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.PlaceLocations))]
    public static class ZoneSystem_PlaceLocations_Patch
    {
        private static int? s_step;

        [HarmonyPrepare]
        public static bool Prepare() => FixedLocationYaw.On;

        [HarmonyPrefix]
        public static void Prefix(ZoneSystem __instance, Vector2s zoneID)
        {
            s_step = null;
            if (WorldGenerator.instance == null || !__instance.m_locationInstances.TryGetValue(zoneID, out ZoneSystem.LocationInstance instance) || instance.m_placed)
                return;
            int seed = WorldGenerator.instance.GetSeed();
            string prefab = instance.m_location.m_prefab.Name;
            UnityEngine.Random.InitState(FixedLocationYaw.Seed(seed, prefab, zoneID.x, zoneID.y));
            s_step = FixedLocationYaw.Step(seed, prefab, zoneID.x, zoneID.y);
        }

        [HarmonyPostfix]
        public static void Postfix() => s_step = null;

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            MethodInfo range = AccessTools.Method(typeof(UnityEngine.Random), nameof(UnityEngine.Random.Range), new[] { typeof(int), typeof(int) });
            var draws = new List<int>();
            for (int i = 0; i < code.Count; i++) if (code[i].Calls(range)) draws.Add(i);
            if (draws.Count != 1)
            {
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning(
                    $"PlaceLocations has changed shape ({draws.Count} Random.Range(int, int) draws): a randomly turned location keeps the game's random turn");
                return code;
            }
            code[draws[0]].operand = AccessTools.Method(typeof(ZoneSystem_PlaceLocations_Patch), nameof(RandomTurn));
            return code;
        }

        /// <summary>The random turn's draw, in steps of 22.5 degrees.</summary>
        public static int RandomTurn(int min, int max) => FixedLocationYaw.Draw(min, max, s_step, UnityEngine.Random.Range);
    }

    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.OnDestroy))]
    public static class ZoneSystem_OnDestroy_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(ZoneSystem __instance)
        {
            ServerTerrainBake.Reset();
            RoadLifecycleManager.OnZoneSystemDestroy(__instance);
        }
    }

}
