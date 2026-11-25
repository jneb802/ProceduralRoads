using System;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// No zone is generated before a player comes near it. The game turns a location as its zone is placed
/// (ZoneSystem.PlaceLocations), at random (Random.Range(0, 16) steps of 22.5 degrees) or down its slope (ten
/// Random.insideUnitCircle ground samples, GetTerrainDelta), from the global, unseeded Random: the turn depends on when the zone
/// first loads. For the locations whose landing the roads settle before they are built (LocationsFirstLoad's listing), and only
/// those, the game's own turn code is run with Random seeded from the world seed, prefab and zone (<see cref="FixedLocationYaw.Seed"/>,
/// the one seed the test switch PROCEDURALROADS_FIXED_LOCATION_YAW uses too) and Random put back before SpawnLocation (which seeds
/// it again itself). Every other location is placed as the game places it. The roads predict the turn and the root the game
/// will give it (<see cref="Predict"/>), and from them, by the game's own draw replayed (LocationPrefabLevelling.PlacedOps), its
/// levelling and parts (PlacedLocationReader.Predict), and lay their roads onto that.
///
/// Which locations: those with a record in <see cref="PlacedLocations"/> whose LocationInstance is not placed yet. The records are
/// saved with the network (RoadNetworkPersistence, the metadata ZDO) and read back at world load, before any zone is placed, so the
/// set needs no record of its own. A world made before roads has none until its first generation: until then every location is
/// placed as the game places it. A location placed before the network (an explored zone) is read, never predicted.
///
/// The mismatch guard: as each such location is placed, its saved turn and root (its LocationProxy) are compared with its record
/// and any difference is logged as a warning (<see cref="Guard"/>).
/// </summary>
public static class SeededLocationTurn
{
    /// <summary>The ground the game's terrain raycast hits on a freshly built zone: the blended height at the whole-metre vertices
    /// (NaturalGround.ForLevelling, HeightmapBuilder's formula) joined by the collision mesh's two triangles a cell (BuriedPieces.MeshHeight).</summary>
    internal static float MeshGround(float x, float z, WorldGenerator world) =>
        BuriedPieces.MeshHeight(x, z, (i, k) => NaturalGround.ForLevelling(i, k, world));

    private static UnityEngine.Random.State s_saved;
    private static bool s_holding;

    /// <summary>The record of the location in <paramref name="zone"/> when it is one the roads chose and it is not placed yet.</summary>
    internal static bool Chosen(ZoneSystem zones, Vector2s zone, out ZoneSystem.LocationInstance instance, out PlacedLocationRecord record)
    {
        record = null!;
        if (!zones.m_locationInstances.TryGetValue(zone, out instance) || instance.m_placed || instance.m_location?.m_prefab == null) return false;
        return PlacedLocations.TryGet(LocationPrefabLevelling.Centre(instance), out record);
    }

    /// <summary>ZoneSystem.PlaceLocations prefix (the patch): the record of the location in <paramref name="zone"/> when it is a
    /// chosen one (<see cref="Chosen"/>), kept for the guard, and Random seeded for it (<see cref="Begin"/>).</summary>
    internal static PlacedLocationRecord? BeforePlacing(ZoneSystem zones, Vector2s zone)
    {
        PlacedLocationRecord? chosen = Chosen(zones, zone, out _, out PlacedLocationRecord record) ? record : null;
        Begin(zones, zone);
        return chosen;
    }

    /// <summary>ZoneSystem.PlaceLocations postfix and finalizer (the patch): Random put back if it was not yet (<see cref="End"/>),
    /// and a chosen location the game has just placed checked against its record (<see cref="Guard"/>).</summary>
    internal static void AfterPlacing(ZoneSystem zones, Vector2s zone, PlacedLocationRecord? chosen)
    {
        End();
        if (chosen != null && zones.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance instance) && instance.m_placed)
            Guard(zone, instance, chosen);
    }

    /// <summary>Random seeded for the chosen location's turn, its state kept.</summary>
    internal static void Begin(ZoneSystem zones, Vector2s zone)
    {
        End();
        if (WorldGenerator.instance == null || !RoadLifecycleManager.HoldsTheNetwork || !Chosen(zones, zone, out ZoneSystem.LocationInstance instance, out _)) return;
        s_saved = UnityEngine.Random.state;
        s_holding = true;
        UnityEngine.Random.InitState(TurnSeed(WorldGenerator.instance.GetSeed(), instance.m_location.m_prefab.name, zone));
    }

    /// <summary>Random put back as it was before the turn: right before SpawnLocation (the transpiler), and in the postfix.</summary>
    public static void End()
    {
        if (!s_holding) return;
        UnityEngine.Random.state = s_saved;
        s_holding = false;
    }

    internal static int TurnSeed(int worldSeed, string prefab, Vector2s zone) => FixedLocationYaw.Seed(worldSeed, prefab, zone.x, zone.y);

    /// <summary>
    /// The turn and root the game will give the location in <paramref name="zone"/> with the turn seeded: ZoneSystem.PlaceLocations
    /// replayed on <paramref name="ground"/> (the ground the game's raycast hits on a freshly built zone: its 1 m mesh,
    /// BiomeBlendedHeight.Rendered), Random seeded as <see cref="Begin"/> seeds it and put back. The root is the ground at its
    /// position (GetGroundData), sea level for one that snaps to water (the game writes 30); a slope's ten samples (GetTerrainDelta) read the same ground, the
    /// highest and lowest kept (first of equals), and the heading from the highest to the lowest rounded to 22.5 degrees.
    /// </summary>
    internal static Quaternion Predict(ZoneSystem.LocationInstance instance, Vector2s zone, Func<float, float, float> ground, out float root)
    {
        ZoneSystem.ZoneLocation location = instance.m_location;
        Vector3 p = instance.m_position;
        p.y = ground(p.x, p.z);
        if (location.m_snapToWater) p.y = RoadWater.Surface;
        root = p.y;
        UnityEngine.Random.State state = UnityEngine.Random.state;
        UnityEngine.Random.InitState(TurnSeed(WorldGenerator.instance!.GetSeed(), location.m_prefab.name, zone));
        try
        {
            Quaternion rot = Quaternion.identity;
            if (location.m_slopeRotation)
            {
                float radius = location.m_exteriorRadius, high = -999999f, low = 999999f;
                Vector3 highest = p, lowest = p;
                for (int i = 0; i < 10; i++)
                {
                    Vector2 d = UnityEngine.Random.insideUnitCircle * radius;
                    Vector3 q = p + new Vector3(d.x, 0f, d.y);
                    float h = ground(q.x, q.z);
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
            else if (location.m_randomRotation)
            {
                int step = UnityEngine.Random.Range(0, 16);
                if (FixedLocationYaw.On) step = FixedLocationYaw.Step(WorldGenerator.instance.GetSeed(), location.m_prefab.name, zone.x, zone.y);
                rot = Quaternion.Euler(0f, step * 22.5f, 0f);
            }
            return rot;
        }
        finally { UnityEngine.Random.state = state; }
    }

    /// <summary>The mismatch guard: the chosen location just placed in <paramref name="zone"/> against its record (turn and root
    /// as its LocationProxy saved them). A difference is a warning; a match is logged with the roads log.</summary>
    internal static void Guard(Vector2s zone, ZoneSystem.LocationInstance instance, PlacedLocationRecord record)
    {
        Vector2 centre = LocationPrefabLevelling.Centre(instance);
        if (!PlacedLocationReader.Placed(zone, centre, out ZDO? proxy)) return;
        float yaw = proxy!.GetRotation().eulerAngles.y, root = proxy.GetPosition().y;
        float dYaw = Mathf.Abs(Mathf.DeltaAngle(yaw, record.Yaw)), dRoot = Mathf.Abs(root - record.Root);
        var log = ProceduralRoadsPlugin.ProceduralRoadsLogger;
        string line = FormattableString.Invariant(
            $"[SEEDEDTURN] {record.Prefab} at ({centre.x:F1}, {centre.y:F1}) placed: turn {yaw:F2} (predicted {record.Yaw:F2}), root {root:F3} (predicted {record.Root:F3})");
        if (dYaw > 0.01f || dRoot > 0.01f) log.LogWarning(line + " MISMATCH: its road was laid onto the predicted landing");
        else if (RoadNetworkGenerator.RoadsLog) log.LogInfo(line + " as predicted");
    }
}
