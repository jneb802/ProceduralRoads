using System;

namespace ProceduralRoads;

/// <summary>
/// A faithful copy of UnityEngine.Random for code that cannot call Unity's (offline tools, worker threads): Xorshift128 seeded as
/// Random.InitState seeds it, and the draws the game's location placing makes from it (ZoneSystem.PlaceLocations and
/// GetTerrainDelta): <see cref="Value"/>, <see cref="Range(float, float)"/>, <see cref="Range(int, int)"/> and
/// <see cref="InsideUnitCircle"/>. A value type: each copy is its own stream, and a copy made from a seed draws exactly what
/// Unity's global Random draws after InitState with that seed. Pinned by UnityRandomTests (the draws) and the turns the game
/// gave a world's locations (FixedLocationYawTests, the offline placer's yaw check).
/// </summary>
public struct UnityRandom
{
    private uint m_x, m_y, m_z, m_w;

    /// <summary>Random.InitState(<paramref name="seed"/>): the seed and three words from it by 1812433253 * previous + 1.</summary>
    public UnityRandom(int seed)
    {
        unchecked
        {
            m_x = (uint)seed;
            m_y = m_x * 1812433253u + 1u;
            m_z = m_y * 1812433253u + 1u;
            m_w = m_z * 1812433253u + 1u;
        }
    }

    /// <summary>The next 32-bit word (Xorshift128, Marsaglia's 11, 19, 8).</summary>
    public uint NextUInt()
    {
        unchecked
        {
            uint t = m_x ^ (m_x << 11);
            m_x = m_y; m_y = m_z; m_z = m_w;
            m_w = m_w ^ (m_w >> 19) ^ t ^ (t >> 8);
            return m_w;
        }
    }

    /// <summary>Random.value: the low 23 bits over 2^23 - 1, so 0 and 1 both included.</summary>
    public float Value() => (NextUInt() & 0x7FFFFFu) * (1f / 8388607f);

    /// <summary>Random.Range(float, float): one <see cref="Value"/> t, mixed as t * min + (1 - t) * max (max at t = 0).</summary>
    public float Range(float min, float max)
    {
        float t = Value();
        return t * min + (1f - t) * max;
    }

    /// <summary>Random.Range(int, int), max excluded: min plus the next word modulo the span; min when they are equal.</summary>
    public int Range(int min, int max)
    {
        if (min == max) return min;
        if (min > max) { int s = min; min = max; max = s; }
        return unchecked((int)(min + NextUInt() % (uint)((long)max - min)));
    }

    /// <summary>Random.insideUnitCircle: an angle drawn by Range(0, 2 pi), then a radius sqrt(Range(0, 1)).</summary>
    public (float x, float y) InsideUnitCircle()
    {
        float angle = Range(0f, 2f * (float)Math.PI);
        float radius = (float)Math.Sqrt(Range(0f, 1f));
        return (radius * (float)Math.Cos(angle), radius * (float)Math.Sin(angle));
    }
}
