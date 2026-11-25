using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The seeded turn (<see cref="SeededLocationTurn"/>), the approved behaviour pinned: a chosen location (one with a
/// record, not yet placed) is turned by the game's own turn code from Random seeded by world seed, prefab and zone, so its turn
/// is the same whatever Random held before; Random is put back exactly before SpawnLocation, with the test switch
/// PROCEDURALROADS_FIXED_LOCATION_YAW on too, and by the finalizer when PlaceLocations throws; a location that is not chosen keeps
/// the game's unseeded draw; the turn and root the roads predict are the ones the replay below gives it; the mismatch guard warns
/// when the placed turn differs from the record; and the Harmony wiring (the prefix last, the transpiler's one call right before
/// SpawnLocation, the finalizer).
///
/// What this proves, and what it does not. <see cref="PlaceAsTheGame"/> is a copy of ZoneSystem.PlaceLocations (decompiled from
/// the game, 1.0.16), with the patch's hooks where Harmony puts them: <see cref="SeededLocationTurn.BeforePlacing"/> (the prefix),
/// <see cref="SeededLocationTurn.End"/> right before SpawnLocation (the transpiler), SpawnLocation's own InitState(seed) and
/// LocationProxy, then <see cref="SeededLocationTurn.AfterPlacing"/> (the postfix). So the tests prove that the hooks and the
/// prediction agree with that copy, not with the game itself: the game's method cannot be loaded here (the suite compiles the
/// mod's sources against Valheim.Testing.Doubles, not assembly_valheim, and runs where no game is installed), so its IL is never
/// run or transpiled; the transpiler is run on an instruction list written to the method's shape. The proof against the game is
/// the in-game check (seededturn DESIGN.md: 196/196 and 199/199 placed as predicted, and the 9 chosen ones placed in play logged
/// as predicted). The game's terrain raycast on a fresh zone is its 1 m mesh (<see cref="SeededLocationTurn.MeshGround"/>), as that
/// check measured it.
/// </summary>
public class SeededLocationTurnTests : IDisposable
{
    private const int WorldSeed = 1234;
    private static readonly int ProxyHash = "LocationProxy".GetStableHashCode();

    /// <summary>A tilted, rippled meadow: every slope sample reads different ground.</summary>
    private sealed class TiltedWorld : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 60f + 0.35f * x - 0.2f * z + 3f * (float)Math.Sin(x / 9f) * (float)Math.Cos(z / 7f);
        public override int GetSeed() => WorldSeed;
    }

    private readonly ValheimWorldScope _scope = new();
    private readonly TiltedWorld _world = new();

    public SeededLocationTurnTests()
    {
        _scope.WithWorld(_world).WithZoneSystem().WithZdos().WithNetwork(server: true);
        PlacedLocations.Reset();
        SeededLocationTurn.End();
    }

    public void Dispose()
    {
        SeededLocationTurn.End();
        PlacedLocations.Reset();
        _scope.Dispose();
    }

    private Vector2s Add(string prefab, float x, float z, bool slope, bool chosen)
    {
        var instance = new ZoneSystem.LocationInstance
        {
            m_location = new ZoneSystem.ZoneLocation
            {
                m_prefab = new SoftReferenceableAssets.SoftReference<GameObject>(prefab), m_exteriorRadius = 12f,
                m_slopeRotation = slope, m_randomRotation = !slope,
            },
            m_position = new Vector3(x, 0f, z),
        };
        TestLocations.Add(ZoneSystem.instance!, instance);
        if (chosen) PlacedLocations.Remember(Predicted(ZoneSystem.GetZone(instance.m_position)));
        return ZoneSystem.GetZone(instance.m_position);
    }

    private float Ground(float x, float z) => SeededLocationTurn.MeshGround(x, z, _world);

    /// <summary>The roads' prediction for the location in <paramref name="zone"/>, as a record (yaw and root only).</summary>
    private PlacedLocationRecord Predicted(Vector2s zone)
    {
        ZoneSystem.LocationInstance instance = ZoneSystem.instance!.m_locationInstances[zone];
        Quaternion turn = SeededLocationTurn.Predict(instance, zone, Ground, out float root);
        return new PlacedLocationRecord
        {
            Prefab = instance.m_location.m_prefab.name ?? "", Centre = LocationPrefabLevelling.Centre(instance), Yaw = turn.eulerAngles.y, Root = root,
        };
    }

    /// <summary>ZoneSystem.PlaceLocations for <paramref name="zone"/> as the game runs it, the patch's hooks in place. Returns the
    /// turn and root the game gave the location (its LocationProxy).</summary>
    private (float yaw, float root) PlaceAsTheGame(Vector2s zone)
    {
        ZoneSystem zones = ZoneSystem.instance!;
        PlacedLocationRecord? chosen = SeededLocationTurn.BeforePlacing(zones, zone);
        ZoneSystem.LocationInstance value = zones.m_locationInstances[zone];
        Vector3 p = value.m_position;
        p.y = Ground(p.x, p.z);                                     // GetGroundData
        if (value.m_location.m_snapToWater) p.y = 30f;
        Quaternion rot = Quaternion.identity;
        if (value.m_location.m_slopeRotation)
        {
            // GetTerrainDelta
            float high = -999999f, low = 999999f;
            Vector3 highest = p, lowest = p;
            for (int i = 0; i < 10; i++)
            {
                Vector2 d = UnityEngine.Random.insideUnitCircle * value.m_location.m_exteriorRadius;
                Vector3 q = p + new Vector3(d.x, 0f, d.y);
                float h = Ground(q.x, q.z);
                if (h < low) { low = h; lowest = q; }
                if (h > high) { high = h; highest = q; }
            }
            Vector3 slope = Vector3.Normalize(lowest - highest);
            Vector3 forward = new Vector3(slope.x, 0f, slope.z);
            forward.Normalize();
            rot = Quaternion.LookRotation(forward);
            Vector3 euler = rot.eulerAngles;
            euler.y = Mathf.Round(euler.y / FixedLocationYaw.StepDegrees) * FixedLocationYaw.StepDegrees;
            rot.eulerAngles = euler;
        }
        else if (value.m_location.m_randomRotation)
            // The transpiler takes this one draw through SeededLocationTurn.RandomTurn with the test switch on, and leaves it with it off.
            rot = Quaternion.Euler(0f, (FixedLocationYaw.On ? SeededLocationTurn.RandomTurn(0, FixedLocationYaw.Steps) : UnityEngine.Random.Range(0, FixedLocationYaw.Steps)) * FixedLocationYaw.StepDegrees, 0f);
        int seed = FixedLocationYaw.LocationSeed(WorldGenerator.instance!.GetSeed(), zone.x, zone.y);
        SeededLocationTurn.End();                                   // the transpiler's call, right before SpawnLocation
        UnityEngine.Random.InitState(seed);                         // SpawnLocation
        ZDO proxy = ZDOMan.instance!.CreateNewZDO(p, ProxyHash);   // its LocationProxy
        proxy.SetRotation(rot);
        value.m_placed = true;
        zones.m_locationInstances[zone] = value;
        SeededLocationTurn.AfterPlacing(zones, zone, chosen);
        return (proxy.GetRotation().eulerAngles.y, proxy.GetPosition().y);
    }

    /// <summary>The location in <paramref name="zone"/> back to not placed, its proxy gone: placed again from scratch.</summary>
    private static void Unplace(Vector2s zone)
    {
        ZoneSystem zones = ZoneSystem.instance!;
        ZoneSystem.LocationInstance value = zones.m_locationInstances[zone];
        value.m_placed = false;
        zones.m_locationInstances[zone] = value;
        var objects = new List<ZDO>();
        ZDOMan.instance!.FindObjects(zone, objects, new HashSet<ZoneSystem.SectorIndex>());
        foreach (ZDO zdo in objects.Where(z => z.GetPrefab() == ProxyHash)) ZDOMan.instance.DestroyZDO(zdo);
        ZDOMan.instance.ProcessDestroyed();
    }

    private static float[] Draws(int n) => Enumerable.Range(0, n).Select(_ => UnityEngine.Random.value).ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AChosenLocationIsTurnedTheSameWhateverRandomHeldBefore(bool slope)
    {
        Vector2s zone = Add(slope ? "MountainCave02" : "Crypt4", 300f, -200f, slope, chosen: true);
        var turns = new List<float>();
        foreach (int before in new[] { 1, 99, 31337 })
        {
            UnityEngine.Random.InitState(before);
            Draws(before % 7);
            turns.Add(PlaceAsTheGame(zone).yaw);
            Unplace(zone);
        }
        Assert.All(turns, t => Assert.Equal(turns[0], t, 3));
    }

    [Fact]
    public void RandomIsPutBackExactlyBeforeSpawnLocation()
    {
        Add("Crypt4", 300f, -200f, slope: false, chosen: true);
        Vector2s zone = Add("MountainCave02", 400f, 100f, slope: true, chosen: true);
        UnityEngine.Random.InitState(5);
        Draws(3);
        float[] expected = Draws(8);

        UnityEngine.Random.InitState(5);
        Draws(3);
        Assert.NotNull(SeededLocationTurn.BeforePlacing(ZoneSystem.instance!, zone));
        Draws(40);                                                  // the turn's own draws, seeded
        SeededLocationTurn.End();
        Assert.Equal(expected, Draws(8));
        SeededLocationTurn.End();                                   // the postfix's End changes nothing more
        UnityEngine.Random.InitState(5);
        Draws(11);
        float[] after = Draws(4);
        UnityEngine.Random.InitState(5);
        Draws(11);
        SeededLocationTurn.AfterPlacing(ZoneSystem.instance!, zone, null);
        Assert.Equal(after, Draws(4));
    }

    [Fact]
    public void AfterPlacingRandomIsWhereTheGameLeavesIt()
    {
        Vector2s chosen = Add("Crypt4", 300f, -200f, slope: false, chosen: true);
        UnityEngine.Random.InitState(8);
        PlaceAsTheGame(chosen);
        float[] seeded = Draws(5);
        // SpawnLocation seeds Random itself: after it the game's Random is the placement seed's, patched or not.
        int seed = FixedLocationYaw.LocationSeed(WorldSeed, chosen.x, chosen.y);
        UnityEngine.Random.InitState(seed);
        Assert.Equal(Draws(5), seeded);
    }

    [Fact]
    public void OnlyAChosenLocationIsSeeded()
    {
        Vector2s chosen = Add("Crypt4", 300f, -200f, slope: false, chosen: true);
        Vector2s other = Add("Crypt4", -300f, 200f, slope: false, chosen: false);
        foreach (int before in new[] { 3, 17, 4242, 90001 })
        {
            // The one not chosen takes the game's own draw from Random as it stood.
            UnityEngine.Random.InitState(before);
            float vanilla = UnityEngine.Random.Range(0, FixedLocationYaw.Steps) * FixedLocationYaw.StepDegrees;
            UnityEngine.Random.InitState(before);
            Assert.Null(SeededLocationTurn.BeforePlacing(ZoneSystem.instance!, other));
            Assert.Equal(vanilla, UnityEngine.Random.Range(0, FixedLocationYaw.Steps) * FixedLocationYaw.StepDegrees);
            SeededLocationTurn.AfterPlacing(ZoneSystem.instance!, other, null);
            UnityEngine.Random.InitState(before);
            Assert.Equal(vanilla, PlaceAsTheGame(other).yaw, 3);
            Unplace(other);
        }
        // A chosen one already placed (an explored zone) is not chosen again.
        PlaceAsTheGame(chosen);
        UnityEngine.Random.InitState(3);
        float drawn = UnityEngine.Random.value;
        UnityEngine.Random.InitState(3);
        Assert.Null(SeededLocationTurn.BeforePlacing(ZoneSystem.instance!, chosen));
        Assert.Equal(drawn, UnityEngine.Random.value);
    }

    [Theory]
    [InlineData("Crypt4", false, 300f, -200f)]
    [InlineData("MountainCave02", true, 400f, 100f)]
    [InlineData("MountainCave02", true, -250f, 330f)]
    [InlineData("GoblinCamp2", false, -120f, -410f)]
    public void ThePredictedTurnAndRootAreTheOnesTheGameGives(string prefab, bool slope, float x, float z)
    {
        Vector2s zone = Add(prefab, x, z, slope, chosen: true);
        Assert.True(PlacedLocations.TryGet(new Vector2(x, z), out PlacedLocationRecord record));
        var lines = new List<string>();
        BepInEx.Logging.ManualLogSource.Captured = lines;
        try
        {
            UnityEngine.Random.InitState(x > 0 ? 77 : 78);
            var (yaw, root) = PlaceAsTheGame(zone);
            Assert.Equal(record.Yaw, yaw, 3);
            Assert.Equal(record.Root, root, 4);
            Assert.DoesNotContain(lines, l => l.Contains("MISMATCH"));
        }
        finally { BepInEx.Logging.ManualLogSource.Captured = null; }
    }

    [Fact]
    public void TheGuardWarnsWhenThePlacedTurnDiffersFromItsRecord()
    {
        Vector2s zone = Add("Crypt4", 300f, -200f, slope: false, chosen: true);
        Assert.True(PlacedLocations.TryGet(new Vector2(300f, -200f), out PlacedLocationRecord record));
        // A record that no longer says what the game will do (another mod turning locations, say).
        PlacedLocations.Remember(new PlacedLocationRecord { Prefab = record.Prefab, Centre = record.Centre, Yaw = (record.Yaw + 90f) % 360f, Root = record.Root });
        var lines = new List<string>();
        BepInEx.Logging.ManualLogSource.Captured = lines;
        try { PlaceAsTheGame(zone); }
        finally { BepInEx.Logging.ManualLogSource.Captured = null; }
        Assert.Contains(lines, l => l.Contains("[SEEDEDTURN] Crypt4") && l.Contains("MISMATCH"));
    }

    private static System.Reflection.MethodInfo PatchMethod(string name) =>
        typeof(ZoneSystem_Patch.ZoneSystem_PlaceLocations_Patch).GetMethod(name)!;

    /// <summary>The test switch PROCEDURALROADS_FIXED_LOCATION_YAW set for one test (it is read once from the environment).</summary>
    private static IDisposable FixedYaw(bool on)
    {
        var field = typeof(FixedLocationYaw).GetField("s_on", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        object? before = field.GetValue(null);
        field.SetValue(null, on);
        return new Restore(() => field.SetValue(null, before));
    }

    private sealed class Restore : IDisposable
    {
        private readonly Action _undo;
        public Restore(Action undo) => _undo = undo;
        public void Dispose() => _undo();
    }

    /// <summary>The Harmony wiring: one patch of PlaceLocations, its prefix last of every mod's (no prefix after it can draw from
    /// the seeded Random), with a transpiler, a postfix and a finalizer.</summary>
    [Fact]
    public void ThePlaceLocationsPatchRunsItsPrefixLastWithATranspilerPostfixAndFinalizer()
    {
        Type patch = typeof(ZoneSystem_Patch.ZoneSystem_PlaceLocations_Patch);
        var target = (HarmonyLib.HarmonyPatch)Attribute.GetCustomAttribute(patch, typeof(HarmonyLib.HarmonyPatch))!;
        Assert.Equal(typeof(ZoneSystem), target.info.declaringType);
        Assert.Equal(nameof(ZoneSystem.PlaceLocations), target.info.methodName);
        var priority = (HarmonyLib.HarmonyPriority)Attribute.GetCustomAttribute(PatchMethod("Prefix"), typeof(HarmonyLib.HarmonyPriority))!;
        Assert.Equal(HarmonyLib.Priority.Last, priority.info.priority);
        Assert.NotNull(Attribute.GetCustomAttribute(PatchMethod("Prefix"), typeof(HarmonyLib.HarmonyPrefix)));
        Assert.NotNull(Attribute.GetCustomAttribute(PatchMethod("Transpiler"), typeof(HarmonyLib.HarmonyTranspiler)));
        Assert.NotNull(Attribute.GetCustomAttribute(PatchMethod("Postfix"), typeof(HarmonyLib.HarmonyPostfix)));
        Assert.NotNull(Attribute.GetCustomAttribute(PatchMethod("Finalizer"), typeof(HarmonyLib.HarmonyFinalizer)));
        // The one owner of seeding a turn: the test switch has no PlaceLocations patch of its own.
        Assert.Single(typeof(ZoneSystem_Patch).GetNestedTypes(), t => Attribute.GetCustomAttribute(t, typeof(HarmonyLib.HarmonyPatch)) is HarmonyLib.HarmonyPatch p
            && p.info.methodName == nameof(ZoneSystem.PlaceLocations));
    }

    /// <summary>PlaceLocations' shape where the transpiler works on it: the random turn's draw, then SpawnLocation's arguments and
    /// the call. Written to the decompiled method's shape (the game's IL is not loaded here).</summary>
    private static List<HarmonyLib.CodeInstruction> PlaceLocationsShape(int spawnCalls = 1)
    {
        var range = typeof(UnityEngine.Random).GetMethod(nameof(UnityEngine.Random.Range), new[] { typeof(int), typeof(int) })!;
        var spawn = typeof(ZoneSystem).GetMethod(nameof(ZoneSystem.SpawnLocation))!;
        var code = new List<HarmonyLib.CodeInstruction>
        {
            new(OpCodes.Ldc_I4_0), new(OpCodes.Ldc_I4_S, (sbyte)16), new(OpCodes.Call, range), new(OpCodes.Conv_R4), new(OpCodes.Pop),
        };
        for (int i = 0; i < spawnCalls; i++)
        {
            code.Add(new(OpCodes.Ldarg_0));
            code.Add(new(OpCodes.Ldloc_0));
            code.Add(new(OpCodes.Call, spawn));
            code.Add(new(OpCodes.Pop));
        }
        code.Add(new(OpCodes.Ret));
        return code;
    }

    /// <summary>The transpiler adds one call, <see cref="SeededLocationTurn.End"/>, right before the one SpawnLocation call, after
    /// its arguments (a call with no arguments and no result), and nothing else; with the test switch on it also takes the one
    /// Random.Range(int, int), the random turn, through <see cref="SeededLocationTurn.RandomTurn"/>. A method of another shape is
    /// left as it is, with a warning.</summary>
    [Fact]
    public void TheTranspilerPutsEndRightBeforeTheOneSpawnLocationCall()
    {
        var end = typeof(SeededLocationTurn).GetMethod(nameof(SeededLocationTurn.End))!;
        var randomTurn = typeof(SeededLocationTurn).GetMethod(nameof(SeededLocationTurn.RandomTurn))!;
        var spawn = typeof(ZoneSystem).GetMethod(nameof(ZoneSystem.SpawnLocation))!;
        var range = typeof(UnityEngine.Random).GetMethod(nameof(UnityEngine.Random.Range), new[] { typeof(int), typeof(int) })!;
        Assert.Empty(end.GetParameters());
        Assert.Equal(typeof(void), end.ReturnType);
        foreach (bool fixedYaw in new[] { false, true })
        {
            List<HarmonyLib.CodeInstruction> before = PlaceLocationsShape();
            var operands = before.Select(c => c.operand).ToList();
            List<HarmonyLib.CodeInstruction> after = ZoneSystem_Patch.ZoneSystem_PlaceLocations_Patch.Transpile(before, fixedYaw);
            Assert.Equal(before.Count + 1, after.Count);
            int call = after.FindIndex(c => Equals(c.operand, spawn));
            Assert.Equal(OpCodes.Call, after[call - 1].opcode);
            Assert.Equal(end, after[call - 1].operand);
            Assert.Single(after, c => Equals(c.operand, end));
            // Everything else as it was, but the random turn's draw with the switch on.
            var rest = after.Where((_, i) => i != call - 1).Select(c => c.operand).ToList();
            Assert.Equal(operands.Select(o => fixedYaw && Equals(o, range) ? randomTurn : o), rest);
        }
        var lines = new List<string>();
        BepInEx.Logging.ManualLogSource.Captured = lines;
        try
        {
            List<HarmonyLib.CodeInstruction> two = PlaceLocationsShape(spawnCalls: 2);
            int count = two.Count;
            List<HarmonyLib.CodeInstruction> left = ZoneSystem_Patch.ZoneSystem_PlaceLocations_Patch.Transpile(two, fixedYaw: false);
            Assert.Equal(count, left.Count);
            Assert.DoesNotContain(left, c => Equals(c.operand, end));
        }
        finally { BepInEx.Logging.ManualLogSource.Captured = null; }
        Assert.Contains(lines, l => l.Contains("PlaceLocations has changed shape (2 SpawnLocation calls)"));
    }

    /// <summary>The finalizer: PlaceLocations throwing after the seeding and before the transpiler's End (in the turn, or a mod's
    /// patch of the turn code) leaves Random as it was before the placing, and hands the exception on unchanged.</summary>
    [Fact]
    public void TheFinalizerPutsRandomBackWhenPlaceLocationsThrows()
    {
        Vector2s zone = Add("Crypt4", 300f, -200f, slope: false, chosen: true);
        UnityEngine.Random.InitState(21);
        Draws(2);
        float[] expected = Draws(6);
        UnityEngine.Random.InitState(21);
        Draws(2);
        var state = new object?[] { ZoneSystem.instance!, zone, null };
        PatchMethod("Prefix").Invoke(null, state);
        Assert.NotNull(state[2]);
        Draws(9);                                                   // the seeded turn's draws, then the throw
        var thrown = new InvalidOperationException("a mod's turn code threw");
        Assert.Same(thrown, PatchMethod("Finalizer").Invoke(null, new object?[] { thrown }));
        Assert.Equal(expected, Draws(6));
        // No exception: the finalizer after the transpiler's End changes nothing.
        Assert.Null(PatchMethod("Finalizer").Invoke(null, new object?[] { null }));
    }

    /// <summary>With the test switch on, the seeded turn is the one owner: every location not placed yet is seeded (a chosen one and
    /// one not chosen alike), its random turn takes the switch's fixed step, the prediction agrees, and Random is put back exactly
    /// before SpawnLocation. The switch's own prefix used to seed Random without saving it, ahead of the seeded turn's.</summary>
    [Fact]
    public void WithTheFixedYawSwitchOnRandomIsPutBackExactlyAndTheTurnIsTheFixedStep()
    {
        using IDisposable on = FixedYaw(true);
        Vector2s chosen = Add("Crypt4", 300f, -200f, slope: false, chosen: true);
        Vector2s other = Add("GoblinCamp2", -120f, -410f, slope: false, chosen: false);
        foreach (Vector2s zone in new[] { chosen, other })
        {
            string prefab = ZoneSystem.instance!.m_locationInstances[zone].m_location.m_prefab.name!;
            UnityEngine.Random.InitState(13);
            Draws(4);
            float[] expected = Draws(5);
            UnityEngine.Random.InitState(13);
            Draws(4);
            SeededLocationTurn.BeforePlacing(ZoneSystem.instance!, zone);
            Assert.Equal(FixedLocationYaw.Step(WorldSeed, prefab, zone.x, zone.y), SeededLocationTurn.RandomTurn(0, FixedLocationYaw.Steps));
            Draws(7);
            SeededLocationTurn.End();
            Assert.Equal(expected, Draws(5));
            // After End no fixed step is left for a later draw.
            UnityEngine.Random.InitState(2);
            int vanilla = UnityEngine.Random.Range(0, FixedLocationYaw.Steps);
            UnityEngine.Random.InitState(2);
            Assert.Equal(vanilla, SeededLocationTurn.RandomTurn(0, FixedLocationYaw.Steps));
        }
        Assert.True(PlacedLocations.TryGet(new Vector2(300f, -200f), out PlacedLocationRecord record));
        UnityEngine.Random.InitState(5);
        var (yaw, _) = PlaceAsTheGame(chosen);
        Assert.Equal(FixedLocationYaw.Degrees(WorldSeed, "Crypt4", chosen.x, chosen.y), yaw, 3);
        Assert.Equal(record.Yaw, yaw, 3);
    }
}
