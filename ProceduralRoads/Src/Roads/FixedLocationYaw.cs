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
/// <see cref="Step"/>; the game's location seed, handed to SpawnLocation right after, is untouched. Locations placed before
/// their roads (LocationsFirstLoad) are placed by the same game method, so they agree; what is read back from a placing
/// (its LocationProxy's rotation) follows.
///
/// The one owner of the fixed turn: offline tools may ask <see cref="Degrees"/> for a randomly turned location's turn.
/// </summary>
public static class FixedLocationYaw
{
    /// <summary>The switch's name after PROCEDURALROADS_.</summary>
    public const string Switch = "FIXED_LOCATION_YAW";
    /// <summary>The game's turns for a randomly turned location: Random.Range(0, 16) steps of 22.5 degrees.</summary>
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
            int locationSeed = worldSeed + zoneX * 4271 + zoneY * 9187;
            h = (uint)locationSeed ^ ((uint)prefab.GetStableHashCode() * 0x9E3779B1u);
            h ^= h >> 16; h *= 0x85EBCA6Bu;
            h ^= h >> 13; h *= 0xC2B2AE35u;
            h ^= h >> 16;
        }
        return h;
    }

    /// <summary>The seed the zone's placing draws from with the switch on (the slope's ground samples).</summary>
    public static int Seed(int worldSeed, string prefab, int zoneX, int zoneY) => unchecked((int)Hash(worldSeed, prefab, zoneX, zoneY));

    /// <summary>The turn, in steps of <see cref="StepDegrees"/>, a randomly turned location takes with the switch on.</summary>
    public static int Step(int worldSeed, string prefab, int zoneX, int zoneY) => (int)(Hash(worldSeed, prefab, zoneX, zoneY) >> 28);

    /// <summary>The turn, in degrees (0 to 337.5), a randomly turned location takes with the switch on.</summary>
    public static float Degrees(int worldSeed, string prefab, int zoneX, int zoneY) => Step(worldSeed, prefab, zoneX, zoneY) * StepDegrees;

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
