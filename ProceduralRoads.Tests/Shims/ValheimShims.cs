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

/// <summary>
/// Mirror of Valheim 1.0's global Vector2s, which replaced Vector2i as the
/// type of a zone id. The fields really are short in the game: a zone id is
/// small and the game packs a lot of them, so the mod must not assume it can
/// put an arbitrary int in one. The int constructor narrows exactly as the
/// game's does, which is why the mod's own grid coordinates stay Vector2i.
/// </summary>
public struct Vector2s
{
    public short x;
    public short y;

    public Vector2s(short x, short y)
    {
        this.x = x;
        this.y = y;
    }

    public Vector2s(int x, int y)
    {
        this.x = (short)x;
        this.y = (short)y;
    }

    public Vector2s(Vector2i v)
    {
        x = (short)v.x;
        y = (short)v.y;
    }

    public override bool Equals(object? other) =>
        other is Vector2s v && v.x == x && v.y == y;

    public override int GetHashCode() => x.GetHashCode() ^ (y.GetHashCode() << 16);

    public static Vector2s operator +(Vector2s a, Vector2s b) =>
        new Vector2s((short)(a.x + b.x), (short)(a.y + b.y));
    public static Vector2s operator -(Vector2s a, Vector2s b) =>
        new Vector2s((short)(a.x - b.x), (short)(a.y - b.y));
    public static bool operator ==(Vector2s a, Vector2s b) => a.x == b.x && a.y == b.y;
    public static bool operator !=(Vector2s a, Vector2s b) => !(a == b);

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

    // --- terrain-modifier surface (RoadTerrainModifier) ---
    // A zone heightmap: m_width vertices per side plus one, m_scale metres
    // per vertex, centred on transform.position. Tests build one per zone
    // with a TerrainComp whose arrays start zeroed, exactly like a fresh
    // _TerrainCompiler in the game.
    public static UnityEngine.Color m_paintMaskPaved = new(0f, 0f, 1f, 1f);
    public static Heightmap? Registered;

    public Transform transform = new();
    public float m_scale = 1f;
    public TerrainComp? m_terrainComp;
    public int PokeCount;

    public static Heightmap? FindHeightmap(UnityEngine.Vector3 point) => Registered;
    public static System.Collections.Generic.List<Heightmap> GetAllHeightmaps() =>
        Registered == null ? new() : new() { Registered };
    public TerrainComp? GetAndCreateTerrainCompiler() => m_terrainComp;
    public void Poke(bool delayed) => PokeCount++;

    public static Heightmap CreateForZone(Vector2i zoneID, int width = 64)
    {
        var hm = new Heightmap { m_scale = ZoneSystem.ZoneSize / width };
        hm.transform.position = ZoneSystem.GetZonePos(zoneID);
        hm.m_terrainComp = new TerrainComp(hm, width);
        return hm;
    }
}

/// <summary>Shim for UnityEngine.Transform: only the position is read.</summary>
public class Transform
{
    public UnityEngine.Vector3 position;
}

/// <summary>Shim for ZNetView: the terrain compiler is always ours here.</summary>
public class ZNetView
{
    public bool IsOwner() => true;
}

/// <summary>
/// Shim for Valheim's TerrainComp (_TerrainCompiler): the per-vertex arrays
/// the road code writes. (m_width + 1)^2 vertices, row-major, y outer.
/// </summary>
public class TerrainComp
{
    public int m_width;
    public Heightmap m_hmap;
    public ZNetView m_nview = new();
    public float[] m_levelDelta;
    public float[] m_smoothDelta;
    public bool[] m_modifiedHeight;
    public UnityEngine.Color[] m_paintMask;
    public bool[] m_modifiedPaint;
    public int SaveCount;

    public TerrainComp(Heightmap hmap, int width)
    {
        m_hmap = hmap;
        m_width = width;
        int n = (width + 1) * (width + 1);
        m_levelDelta = new float[n];
        m_smoothDelta = new float[n];
        m_modifiedHeight = new bool[n];
        m_paintMask = new UnityEngine.Color[n];
        m_modifiedPaint = new bool[n];
    }

    public void Save() => SaveCount++;
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

    /// <summary>
    /// Valheim's base height is a normalised value where water lies below 0.05
    /// (IslandDetector.WaterThreshold) and the terrain height is roughly
    /// 200 × base; map the shim's metres onto that scale so the island detector
    /// sees ocean where the synthetic world puts it (sea level 30 m → 0.05).
    /// </summary>
    public virtual float GetBaseHeight(float wx, float wy, bool menuTerrain) =>
        0.05f + (GetHeight(wx, wy) - ProceduralRoads.RoadConstants.SeaLevel) / 200f;

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

    // ---- locations-generated, as Valheim 1.0 actually behaves ----
    //
    // 1.0 kept the LocationsGenerated property and the GenerateLocationsCompleted
    // event, but changed who writes the backing field. The setter still raises the
    // event (once, then drops the handlers), and subscribing after the fact fires
    // immediately -- but ZoneSystem.Load now writes m_locationsGenerated DIRECTLY
    // from the save package, bypassing the setter. So a world read from disk never
    // raises the event, however early a handler subscribed. Before 1.0 the setter
    // was the only writer and every path raised it.
    //
    // The shim models all three doors so a test can tell them apart.

    private bool m_locationsGenerated;
    private System.Action? m_generateLocationsCompleted;

    public bool LocationsGenerated
    {
        get => m_locationsGenerated;
        set
        {
            m_locationsGenerated = value;
            if (!m_locationsGenerated) return;
            m_generateLocationsCompleted?.Invoke();
            m_generateLocationsCompleted = null;
        }
    }

    public event System.Action GenerateLocationsCompleted
    {
        add
        {
            if (m_locationsGenerated) { value?.Invoke(); return; }
            m_generateLocationsCompleted += value;
        }
        remove => m_generateLocationsCompleted -= value;
    }

    /// <summary>
    /// What ZoneSystem.Load does in 1.0: set the flag straight from the save and
    /// raise nothing. This is the door the mod used to be told about and is not.
    /// </summary>
    public void LoadLocationsGeneratedFromSave(bool generated) => m_locationsGenerated = generated;

    // Valheim 1.0 types a zone id as Vector2s, not Vector2i.
    public static Vector2s GetZone(UnityEngine.Vector3 point) =>
        new(UnityEngine.Mathf.FloorToInt((point.x + ZoneSize / 2f) / ZoneSize),
            UnityEngine.Mathf.FloorToInt((point.z + ZoneSize / 2f) / ZoneSize));

    public static UnityEngine.Vector3 GetZonePos(Vector2s id) =>
        new(id.x * ZoneSize, 0f, id.y * ZoneSize);
}
