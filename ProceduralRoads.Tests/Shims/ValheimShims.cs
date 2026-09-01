// Minimal stand-ins for the Valheim types the road logic uses.
// WorldGenerator is virtual here so tests can plug in synthetic worlds.

// ReSharper disable InconsistentNaming

/// <summary>Mirror of Valheim's string.GetStableHashCode extension (Utils).</summary>
public static class StringExtensionMethods
{
    public static int GetStableHashCode(this string str)
    {
        unchecked
        {
            int hash1 = 5381;
            int hash2 = hash1;
            for (int i = 0; i < str.Length && str[i] != '\0'; i += 2)
            {
                hash1 = ((hash1 << 5) + hash1) ^ str[i];
                if (i == str.Length - 1 || str[i + 1] == '\0')
                    break;
                hash2 = ((hash2 << 5) + hash2) ^ str[i + 1];
            }
            return hash1 + hash2 * 1566083941;
        }
    }
}

/// <summary>Mirror of Valheim's global Vector2i (integer grid coordinate).</summary>
public struct Vector2i
{
    public int x;
    public int y;

    public Vector2i(int x, int y)
    {
        this.x = x;
        this.y = y;
    }

    public override bool Equals(object? other) =>
        other is Vector2i v && v.x == x && v.y == y;

    public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() << 16);

    public static bool operator ==(Vector2i a, Vector2i b) => a.x == b.x && a.y == b.y;
    public static bool operator !=(Vector2i a, Vector2i b) => !(a == b);

    public override string ToString() => $"({x}, {y})";
}

public class Heightmap
{
    [System.Flags]
    public enum Biome
    {
        None = 0,
        Meadows = 1,
        Swamp = 2,
        Mountain = 4,
        BlackForest = 8,
        Plains = 16,
        AshLands = 32,
        DeepNorth = 64,
        Ocean = 256,
        Mistlands = 512,
    }
}

/// <summary>
/// Shim base for Valheim's WorldGenerator exposing only the members the road
/// code calls. Tests subclass this with synthetic terrain.
/// </summary>
public class WorldGenerator
{
    public static WorldGenerator? instance;

    public virtual float GetHeight(float wx, float wy) => 0f;

    public virtual Heightmap.Biome GetBiome(float wx, float wy) => Heightmap.Biome.Meadows;

    public virtual void GetRiverWeight(float wx, float wy, out float weight, out float width)
    {
        weight = 0f;
        width = 0f;
    }

    public virtual int GetSeed() => 0;

    public virtual float GetBaseHeight(float wx, float wy, bool menuTerrain) => GetHeight(wx, wy);

    public virtual float GetBiomeHeight(Heightmap.Biome biome, float wx, float wy, out UnityEngine.Color mask)
    {
        mask = default;
        return GetHeight(wx, wy);
    }
}

/// <summary>
/// Shim for Valheim's ZoneSystem exposing only the members the road code
/// references. GetLocationList returns an empty list unless a test fills it.
/// </summary>
public class ZoneSystem
{
    public const float ZoneSize = 64f;

    public static ZoneSystem? instance;

    public class ZoneLocation
    {
        public PrefabEntry m_prefab = new();
        public float m_exteriorRadius;

        public class PrefabEntry
        {
            public string Name = "";
        }
    }

    public struct LocationInstance
    {
        public ZoneLocation m_location;
        public UnityEngine.Vector3 m_position;
    }

    public System.Collections.Generic.List<LocationInstance> Locations = new();

    public System.Collections.Generic.List<LocationInstance> GetLocationList() => Locations;

    public static Vector2i GetZone(UnityEngine.Vector3 point) =>
        new(UnityEngine.Mathf.FloorToInt((point.x + ZoneSize / 2f) / ZoneSize),
            UnityEngine.Mathf.FloorToInt((point.z + ZoneSize / 2f) / ZoneSize));

    public static UnityEngine.Vector3 GetZonePos(Vector2i id) =>
        new(id.x * ZoneSize, 0f, id.y * ZoneSize);
}
