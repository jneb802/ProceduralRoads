using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The seeded turn (<see cref="SeededLocationTurn"/>), the approved behaviour pinned: a chosen location (one with a
/// record, not yet placed) is turned by the game's own turn code from Random seeded by world seed, prefab and zone, so its turn
/// is the same whatever Random held before; Random is put back exactly before SpawnLocation; a location that is not chosen keeps
/// the game's unseeded draw; the turn and root the roads predict are the ones the game then gives it; and the mismatch guard
/// warns when the placed turn differs from the record.
///
/// The game's side is ZoneSystem.PlaceLocations as the game runs it (decompiled), with the patch's hooks where Harmony puts
/// them: <see cref="SeededLocationTurn.BeforePlacing"/> (the prefix), <see cref="SeededLocationTurn.End"/> right before
/// SpawnLocation (the transpiler), SpawnLocation's own InitState(seed) and LocationProxy, then
/// <see cref="SeededLocationTurn.AfterPlacing"/> (the postfix). The game's terrain raycast on a fresh zone is its 1 m mesh
/// (<see cref="SeededLocationTurn.MeshGround"/>), as the in-game check measured it.
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
            euler.y = Mathf.Round(euler.y / 22.5f) * 22.5f;
            rot.eulerAngles = euler;
        }
        else if (value.m_location.m_randomRotation)
            rot = Quaternion.Euler(0f, UnityEngine.Random.Range(0, 16) * 22.5f, 0f);
        int seed = WorldGenerator.instance!.GetSeed() + zone.x * 4271 + zone.y * 9187;
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
        int seed = WorldSeed + chosen.x * 4271 + chosen.y * 9187;
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
            float vanilla = UnityEngine.Random.Range(0, 16) * 22.5f;
            UnityEngine.Random.InitState(before);
            Assert.Null(SeededLocationTurn.BeforePlacing(ZoneSystem.instance!, other));
            Assert.Equal(vanilla, UnityEngine.Random.Range(0, 16) * 22.5f);
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
}
