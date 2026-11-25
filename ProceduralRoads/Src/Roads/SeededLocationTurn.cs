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
/// it again itself). With the test switch on, every location not placed yet is seeded the same way, its random turn taking the
/// switch's fixed step (<see cref="Begin"/>, <see cref="RandomTurn"/>): one owner of seeding a turn, so Random is put back exactly
/// either way. Every other location is placed as the game places it. The roads predict the turn and the root the game
/// will give it (<see cref="Predict"/>), and from them, by the game's own draw replayed (LocationPrefabLevelling.PlacedOps), its
/// levelling and parts (PlacedLocationReader.Predict), and lay their roads onto that.
///
/// Which locations: those with a record in <see cref="PlacedLocations"/> whose LocationInstance is not placed yet. The records are
/// saved with the network (RoadNetworkPersistence, the metadata ZDO) and read back with it at world load (the load decision), so the
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
    /// <summary>The random turn's step with the test switch on (FixedLocationYaw.Step), while the location being placed is seeded.</summary>
    private static int? s_fixedStep;

    /// <summary>The record of the location in <paramref name="zone"/> when it is one the roads chose and it is not placed yet.</summary>
    internal static bool Chosen(ZoneSystem zones, Vector2s zone, out ZoneSystem.LocationInstance instance, out PlacedLocationRecord record)
    {
        record = null!;
        if (!NotPlaced(zones, zone, out instance)) return false;
        return PlacedLocations.TryGet(LocationPrefabLevelling.Centre(instance), out record);
    }

    private static bool NotPlaced(ZoneSystem zones, Vector2s zone, out ZoneSystem.LocationInstance instance) =>
        zones.m_locationInstances.TryGetValue(zone, out instance) && !instance.m_placed && instance.m_location?.m_prefab != null;

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

    /// <summary>
    /// Random seeded for the turn of the location in <paramref name="zone"/>, its state kept to be put back (<see cref="End"/>):
    /// for a chosen one (<see cref="Chosen"/>, while the roads hold the network), and with the test switch
    /// PROCEDURALROADS_FIXED_LOCATION_YAW on (<see cref="FixedLocationYaw.On"/>) for every one not placed yet, whose random turn
    /// then takes <see cref="FixedLocationYaw.Step"/> (<see cref="RandomTurn"/>). The one owner of seeding a turn.
    /// </summary>
    internal static void Begin(ZoneSystem zones, Vector2s zone)
    {
        End();
        if (WorldGenerator.instance == null || !NotPlaced(zones, zone, out ZoneSystem.LocationInstance instance)) return;
        if (!FixedLocationYaw.On && !(RoadLifecycleManager.HoldsTheNetwork && Chosen(zones, zone, out _, out _))) return;
        int worldSeed = WorldGenerator.instance.GetSeed();
        string prefab = instance.m_location.m_prefab.name!;
        s_saved = UnityEngine.Random.state;
        s_holding = true;
        s_fixedStep = FixedStep(worldSeed, prefab, zone);
        UnityEngine.Random.InitState(TurnSeed(worldSeed, prefab, zone));
    }

    /// <summary>Random put back as it was before the turn: right before SpawnLocation (the transpiler), and in the postfix and the
    /// finalizer.</summary>
    public static void End()
    {
        s_fixedStep = null;
        if (!s_holding) return;
        UnityEngine.Random.state = s_saved;
        s_holding = false;
    }

    /// <summary>PlaceLocations' random turn's draw with the test switch on (the transpiler puts it in place of the one
    /// Random.Range(int, int)): <see cref="FixedLocationYaw.Draw"/> with the seeded location's fixed step.</summary>
    public static int RandomTurn(int min, int max) => FixedLocationYaw.Draw(min, max, s_fixedStep, UnityEngine.Random.Range);

    internal static int TurnSeed(int worldSeed, string prefab, Vector2s zone) => FixedLocationYaw.Seed(worldSeed, prefab, zone.x, zone.y);

    /// <summary>The random turn's fixed step with the test switch on (<see cref="FixedLocationYaw.Step"/>), none with it off.</summary>
    private static int? FixedStep(int worldSeed, string prefab, Vector2s zone) =>
        FixedLocationYaw.On ? FixedLocationYaw.Step(worldSeed, prefab, zone.x, zone.y) : null;

    /// <summary>
    /// The turn and root the game will give the location in <paramref name="zone"/> with the turn seeded: ZoneSystem.PlaceLocations
    /// replayed on <paramref name="ground"/> (the ground the game's raycast hits on a freshly built zone: its 1 m mesh,
    /// <see cref="MeshGround"/>), Random seeded as <see cref="Begin"/> seeds it and put back. The root is the ground at its
    /// position (GetGroundData), sea level for one that snaps to water (the game writes 30); a slope's ten samples (GetTerrainDelta) read the same ground, the
    /// highest and lowest kept (first of equals), and the heading from the highest to the lowest rounded to the game's step
    /// (<see cref="FixedLocationYaw.StepDegrees"/>); a random turn is the game's draw of a step (<see cref="FixedLocationYaw.Draw"/>,
    /// the test switch's fixed step in its place when it is on).
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
                euler.y = Mathf.Round(euler.y / FixedLocationYaw.StepDegrees) * FixedLocationYaw.StepDegrees;
                rot.eulerAngles = euler;
            }
            else if (location.m_randomRotation)
            {
                int step = FixedLocationYaw.Draw(0, FixedLocationYaw.Steps, FixedStep(WorldGenerator.instance.GetSeed(), location.m_prefab.name, zone), UnityEngine.Random.Range);
                rot = Quaternion.Euler(0f, step * FixedLocationYaw.StepDegrees, 0f);
            }
            return rot;
        }
        finally { UnityEngine.Random.state = state; }
    }

    /// <summary>How a generation settles a chosen destination's landing (LocationsFirstLoad, at world load, road_generate and
    /// road_regen_island alike).</summary>
    internal enum Settling
    {
        /// <summary>Already placed: read as the game left it, never predicted.</summary>
        Read,
        /// <summary>Not placed yet: predicted, and seeded when the game places it.</summary>
        Predicted,
        /// <summary>Placed, or its zone generated, but no placing of it found to read: no road (PlacedLocations.NotPlaced).</summary>
        Neither,
    }

    /// <summary>
    /// The one decision of how a chosen destination's landing is settled: one whose placing is found (its LocationProxy in its
    /// zone, PlacedLocationReader.Placed: <paramref name="placingFound"/>) is read, whatever else; one not placed whose zone is not
    /// generated (<paramref name="placedOrGenerated"/> false) is predicted; any other gets no road. So a regeneration reads the
    /// destinations the game has placed since the network was made and predicts only those still unplaced.
    /// </summary>
    internal static Settling Settle(bool placingFound, bool placedOrGenerated) =>
        placingFound ? Settling.Read : placedOrGenerated ? Settling.Neither : Settling.Predicted;

    /// <summary>
    /// The saved destinations the world no longer has where the network expects them: a record (PlacedLocations) with no
    /// LocationInstance of its prefab at its centre (<paramref name="prefabAt"/>: the prefab of the instance whose identity is that
    /// centre, LocationPrefabLevelling.LocationOf, or null). The game moves a LocationInstance it has not placed yet when it
    /// generates its locations again (ZoneSystem.ClearNonPlacedLocations: a location list or version change, genloc, Upgrade
    /// World), and a mod or a location-list change can remove one. Such a destination is only logged (<see cref="SayMissing"/>): its road stays as saved, and the game places the moved
    /// location with its own unseeded turn, since no record stands at its new centre.
    /// </summary>
    internal static System.Collections.Generic.List<PlacedLocationRecord> Missing(
        System.Collections.Generic.IEnumerable<PlacedLocationRecord> records, Func<Vector2, string?> prefabAt)
    {
        var moved = new System.Collections.Generic.List<PlacedLocationRecord>();
        foreach (PlacedLocationRecord record in records)
            // The record stores the prefab name as the save writes it, commas as underscores.
            if (prefabAt(record.Centre) is not string prefab || prefab.Replace(',', '_') != record.Prefab) moved.Add(record);
        moved.Sort((a, b) => a.Centre.x != b.Centre.x ? a.Centre.x.CompareTo(b.Centre.x) : a.Centre.y.CompareTo(b.Centre.y));
        return moved;
    }

    /// <summary>The prefab of the LocationInstance whose identity is <paramref name="centre"/> (LocationPrefabLevelling.LocationOf), or null.</summary>
    internal static string? InstancePrefabAt(Vector2 centre) =>
        LocationPrefabLevelling.LocationOf(centre, out ZoneSystem.LocationInstance instance) ? instance.m_location?.m_prefab.name : null;

    /// <summary>How many missing destinations the load's warning names before "+N more" (a log line, not a rule).</summary>
    internal const int SayMissingNamed = 10;

    /// <summary>The warning a load logs for the saved destinations the world no longer has where the network expects them
    /// (<see cref="Missing"/>): null when every one is there, else one line naming the first <see cref="SayMissingNamed"/>. Logged
    /// once each time a saved network is read
    /// (RoadNetworkGenerator.UseReadNetwork). The roads are left as saved.</summary>
    internal static string? SayMissing(System.Collections.Generic.IEnumerable<PlacedLocationRecord> records)
    {
        var missing = Missing(records, InstancePrefabAt);
        if (missing.Count == 0) return null;
        var names = new System.Text.StringBuilder();
        for (int i = 0; i < missing.Count && i < SayMissingNamed; i++)
            names.Append(i == 0 ? "" : ", ").Append(FormattableString.Invariant($"{missing[i].Prefab} ({missing[i].Centre.x:F1}, {missing[i].Centre.y:F1})"));
        if (missing.Count > SayMissingNamed) names.Append(FormattableString.Invariant($", +{missing.Count - SayMissingNamed} more"));
        return FormattableString.Invariant(
            $"[SEEDEDTURN] {missing.Count} saved destination(s) have no LocationInstance where the network expects them (moved when the game generated its locations again, or removed); their roads are left as saved: {names}");
    }

    /// <summary>The mismatch guard: the chosen location just placed in <paramref name="zone"/> against its record (turn and root
    /// as its LocationProxy saved them). A difference is a warning; a match is logged with the roads log.</summary>
    internal static void Guard(Vector2s zone, ZoneSystem.LocationInstance instance, PlacedLocationRecord record)
    {
        Vector2 centre = LocationPrefabLevelling.Centre(instance);
        var log = ProceduralRoadsPlugin.ProceduralRoadsLogger;
        if (!PlacedLocationReader.Placed(zone, centre, out ZDO? proxy))
        {
            log.LogWarning(FormattableString.Invariant(
                $"[SEEDEDTURN] {record.Prefab} at ({centre.x:F1}, {centre.y:F1}) placed, but no LocationProxy stands at its centre: its turn and root are not checked"));
            return;
        }
        float yaw = proxy!.GetRotation().eulerAngles.y, root = proxy.GetPosition().y;
        float dYaw = Mathf.Abs(Mathf.DeltaAngle(yaw, record.Yaw)), dRoot = Mathf.Abs(root - record.Root);
        string line = FormattableString.Invariant(
            $"[SEEDEDTURN] {record.Prefab} at ({centre.x:F1}, {centre.y:F1}) placed: turn {yaw:F2} (predicted {record.Yaw:F2}), root {root:F3} (predicted {record.Root:F3})");
        if (dYaw > 0.01f || dRoot > 0.01f) log.LogWarning(line + " MISMATCH: its road was laid onto the predicted landing");
        else if (RoadNetworkGenerator.RoadsLog) log.LogInfo(line + " as predicted");
    }
}
