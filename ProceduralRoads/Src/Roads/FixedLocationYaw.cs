using System;

namespace ProceduralRoads;

/// <summary>
/// A test-only switch, PROCEDURALROADS_FIXED_LOCATION_YAW (off by default; <see cref="DebugSwitches"/>): every location
/// is turned the same way each time its world is generated, so two builds run on one world fixture place every
/// location identically and differ only by their code.
///
/// The game turns a location as its zone is placed (ZoneSystem.PlaceLocations) from the global, unseeded
/// UnityEngine.Random: a randomly turned one by Random.Range(0, 16) steps of 22.5 degrees, one turned down its slope
/// by ten Random.insideUnitCircle ground samples (GetTerrainDelta). Positions and the generator's terrain are the same on
/// every run of a seed; these draws are not, so a landing, and the roads to it, moved between runs. With the switch on,
/// the zone's placing draws from <see cref="Seed"/> (the slope's samples) and a randomly turned location takes
/// <see cref="Step"/>; the game's location seed, handed to SpawnLocation right after, is untouched. The seeding is the seeded
/// turn's (SeededLocationTurn.Begin, the one owner of seeding a turn: Random saved, seeded and put back before SpawnLocation);
/// this switch only widens it to every location and fixes the random step. Locations placed before
/// their roads (LocationsFirstLoad) are placed by the same game method, so they agree; what is read back from a placing
/// (its LocationProxy's rotation) follows.
///
/// The one owner of the fixed turn: offline tools may ask <see cref="Degrees"/> for a randomly turned location's turn, and
/// <see cref="SlopeDegrees"/> for one turned down its slope, the same owner the seeded turn's prediction asks
/// (SeededLocationTurn.Predict), on the same ground (<see cref="GameGround"/>, SeededLocationTurn.MeshGround).
/// </summary>
public static class FixedLocationYaw
{
    /// <summary>The switch's name after PROCEDURALROADS_.</summary>
    public const string Switch = "FIXED_LOCATION_YAW";
    /// <summary>The game's turns for a randomly turned location: Random.Range(0, 16) steps of 22.5 degrees; a slope-turned one is
    /// rounded to the same step (ZoneSystem.PlaceLocations).</summary>
    public const int Steps = 16;
    public const float StepDegrees = 360f / Steps;

    private static bool? s_on;

    /// <summary>Whether the switch is on; read once.</summary>
    internal static bool On => s_on ??= DebugSwitches.Flag(Switch, false);

    /// <summary>
    /// The hash a location's turn is drawn from: the game's own location seed for its zone (WorldGenerator seed +
    /// zone.x * 4271 + zone.y * 9187, as PlaceLocations hands SpawnLocation) mixed with its prefab name's stable hash,
    /// through a 32-bit finaliser so neighbouring zones are not turned in a pattern.
    /// </summary>
    public static uint Hash(int worldSeed, string prefab, int zoneX, int zoneY)
    {
        uint h;
        unchecked
        {
            int locationSeed = LocationSeed(worldSeed, zoneX, zoneY);
            h = (uint)locationSeed ^ ((uint)prefab.GetStableHashCode() * 0x9E3779B1u);
            h ^= h >> 16; h *= 0x85EBCA6Bu;
            h ^= h >> 13; h *= 0xC2B2AE35u;
            h ^= h >> 16;
        }
        return h;
    }

    /// <summary>The game's location seed for a zone, as PlaceLocations hands SpawnLocation: WorldGenerator seed + zone.x * 4271 +
    /// zone.y * 9187. The one copy of the game's formula (the seeded turn's prediction draws with it, PlacedLocationReader.Predict).</summary>
    public static int LocationSeed(int worldSeed, int zoneX, int zoneY) => unchecked(worldSeed + zoneX * 4271 + zoneY * 9187);

    /// <summary>The seed a seeded Location's turn draws from (SeededLocationTurn.Begin: a chosen one, and every one with the switch
    /// on; the slope's ground samples).</summary>
    public static int Seed(int worldSeed, string prefab, int zoneX, int zoneY) => unchecked((int)Hash(worldSeed, prefab, zoneX, zoneY));

    /// <summary>The turn, in steps of <see cref="StepDegrees"/>, a randomly turned location takes with the switch on.</summary>
    public static int Step(int worldSeed, string prefab, int zoneX, int zoneY) => (int)(Hash(worldSeed, prefab, zoneX, zoneY) >> 28);

    /// <summary>The turn, in degrees (0 to 337.5), a randomly turned location takes with the switch on.</summary>
    public static float Degrees(int worldSeed, string prefab, int zoneX, int zoneY) => Step(worldSeed, prefab, zoneX, zoneY) * StepDegrees;

    /// <summary>
    /// The turn, in degrees (0 to 337.5), a location turned down its slope (ZoneLocation.m_slopeRotation) takes with the switch
    /// on: ZoneSystem.PlaceLocations' slope turn replayed from <see cref="Seed"/> on a copy of Unity's Random
    /// (<see cref="UnityRandom"/>), so offline tools and worker threads predict it without Unity. GetTerrainDelta's ten
    /// Random.insideUnitCircle samples within <paramref name="radius"/> (the location's exterior radius) of the centre read
    /// <paramref name="ground"/> (the game's raycast onto the freshly built zone; <see cref="GameGround"/>), the highest and the
    /// lowest kept (the first of equals); the heading from the highest to the lowest (Quaternion.LookRotation's yaw), rounded to
    /// 22.5 degrees. A flat disc (no heading) is not turned. The one owner of the seeded slope turn: the game's prediction of a
    /// chosen Location (SeededLocationTurn.Predict) and the offline tools ask it alike.
    /// </summary>
    public static float SlopeDegrees(int worldSeed, string prefab, int zoneX, int zoneY, float x, float z, float radius, Func<float, float, float> ground)
    {
        var random = new UnityRandom(Seed(worldSeed, prefab, zoneX, zoneY));
        float high = -999999f, low = 999999f, highX = x, highZ = z, lowX = x, lowZ = z, highY = 0f, lowY = 0f;
        for (int i = 0; i < 10; i++)
        {
            (float dx, float dz) = random.InsideUnitCircle();
            float qx = x + dx * radius, qz = z + dz * radius, h = ground(qx, qz);
            if (h < low) { low = h; lowX = qx; lowZ = qz; lowY = h; }
            if (h > high) { high = h; highX = qx; highZ = qz; highY = h; }
        }
        // Vector3.Normalize(lowest - highest), then its level part normalised (Unity: no direction under 1e-5).
        float sx = lowX - highX, sy = lowY - highY, sz = lowZ - highZ;
        float length = (float)Math.Sqrt(sx * sx + sy * sy + sz * sz);
        if (length <= 1e-5f) return 0f;
        sx /= length; sz /= length;
        if ((float)Math.Sqrt(sx * sx + sz * sz) <= 1e-5f) return 0f;
        float yaw = (float)(Math.Atan2(sx, sz) * (180.0 / Math.PI));
        if (yaw < 0f) yaw += 360f;
        float turned = (float)Math.Round(yaw / StepDegrees) * StepDegrees;
        return turned >= 360f ? turned - 360f : turned;
    }

    /// <summary>The ground the game's slope samples read: its raycast onto a freshly built zone's 1 m mesh, the seeded turn's own
    /// (SeededLocationTurn.MeshGround), for offline tools that ask <see cref="SlopeDegrees"/>.</summary>
    public static Func<float, float, float> GameGround(WorldGenerator world) => (x, z) => SeededLocationTurn.MeshGround(x, z, world);

    /// <summary>
    /// The random turn's draw: the game's draw is always made (the stream advances as it does without the switch), and
    /// <paramref name="fixedStep"/> is taken in its place when there is one and the draw is the turn's (0 to
    /// <see cref="Steps"/>).
    /// </summary>
    internal static int Draw(int min, int max, int? fixedStep, Func<int, int, int> random)
    {
        int drawn = random(min, max);
        return fixedStep is int step && min == 0 && max == Steps ? step : drawn;
    }
}
