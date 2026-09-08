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
    private bool m_doLateUpdate;

    public static Heightmap? FindHeightmap(UnityEngine.Vector3 point) => Registered;
    public static System.Collections.Generic.List<Heightmap> GetAllHeightmaps() =>
        Registered == null ? new() : new() { Registered };
    /// <summary>Like the game: the zone's live compiler, or a new one (with a new ZDO) if it has none.</summary>
    public TerrainComp GetAndCreateTerrainCompiler() => m_terrainComp ??= new TerrainComp(this, 64);
    /// <summary>Like the game: delayed queues a rebuild for the late update, otherwise it runs now.</summary>
    public void Poke(bool delayed) { PokeCount++; if (delayed) m_doLateUpdate = true; }
    public bool HaveQueuedRebuild() => m_doLateUpdate;
    /// <summary>The game's late update: run the queued rebuild.</summary>
    public void Regenerate() => m_doLateUpdate = false;

    public static Heightmap CreateForZone(Vector2i zoneID, int width = 64, bool withCompiler = true)
    {
        var hm = new Heightmap { m_scale = ZoneSystem.ZoneSize / width };
        hm.transform.position = ZoneSystem.GetZonePos(zoneID);
        if (withCompiler)
            hm.m_terrainComp = new TerrainComp(hm, width);
        return hm;
    }
}

/// <summary>Shim for UnityEngine.Transform: only the position is read.</summary>
public class Transform
{
    public UnityEngine.Vector3 position;
}

/// <summary>Shim for ZNetView: one ZDO behind it, ours unless a test says otherwise.</summary>
public class ZNetView
{
    public ZDO Zdo;
    public ZNetView(ZDO zdo) { Zdo = zdo; }
    public bool IsValid() => Zdo != null;
    public bool IsOwner() => Zdo.IsOwner();
    public bool HasOwner() => Zdo.HasOwner();
    public void ClaimOwnership() { if (!IsOwner()) Zdo.SetOwner(ZDOMan.instance?.m_sessionID ?? 1); }
    public ZDO GetZDO() => Zdo;
}

/// <summary>Mirror of Valheim's ZDOID, as far as the road code prints it.</summary>
public struct ZDOID
{
    public long ID;
    public override string ToString() => ID.ToString();
}

/// <summary>
/// Shim for a Valheim ZDO: the typed key/value bag the road code stores its
/// network and per-zone markers in. Only the members the mod calls.
/// </summary>
public class ZDO
{
    private static long s_nextId = 1;

    public ZDOID m_uid = new() { ID = s_nextId++ };
    public bool Persistent;
    private int m_prefab;
    private long m_owner;
    private UnityEngine.Vector3 m_position;
    private readonly System.Collections.Generic.Dictionary<int, int> m_ints = new();
    private readonly System.Collections.Generic.Dictionary<int, byte[]> m_byteArrays = new();

    public ZDO(UnityEngine.Vector3 position, int prefab)
    {
        m_position = position;
        m_prefab = prefab;
    }

    public void SetPrefab(int prefab) => m_prefab = prefab;
    public int GetPrefab() => m_prefab;
    public void SetOwner(long owner) => m_owner = owner;
    public long GetOwner() => m_owner;
    public bool HasOwner() => m_owner != 0;
    /// <summary>Ours when it carries our session id; without a ZDOMan every ZDO counts as ours.</summary>
    public bool IsOwner() => ZDOMan.instance == null || m_owner == ZDOMan.instance.m_sessionID;
    public UnityEngine.Vector3 GetPosition() => m_position;
    public Vector2i GetSector() => ZoneSystem.GetZone(m_position);
    public void SetPosition(UnityEngine.Vector3 position) => m_position = position;

    public void Set(int hash, int value) => m_ints[hash] = value;
    public int GetInt(int hash, int defaultValue = 0) => m_ints.TryGetValue(hash, out int v) ? v : defaultValue;
    public void Set(int hash, byte[] value) => m_byteArrays[hash] = value;
    public byte[]? GetByteArray(int hash, byte[]? defaultValue = null) =>
        m_byteArrays.TryGetValue(hash, out var v) ? v : defaultValue;
}

/// <summary>Shim for ZDOMan: the world's ZDOs as a list. Tests create one per world.</summary>
public class ZDOMan
{
    public static ZDOMan? instance;

    public long m_sessionID = 1;
    public readonly System.Collections.Generic.List<ZDO> Zdos = new();

    public ZDO CreateNewZDO(UnityEngine.Vector3 position, int prefabHash)
    {
        var zdo = new ZDO(position, prefabHash);
        Zdos.Add(zdo);
        return zdo;
    }

    /// <summary>Adds every ZDO of the prefab to the list; the real one pages, this one finishes in a single call.</summary>
    public bool GetAllZDOsWithPrefabIterative(string prefab, System.Collections.Generic.List<ZDO> zdos, ref int index)
    {
        int hash = prefab.GetStableHashCode();
        foreach (var zdo in Zdos)
            if (zdo.GetPrefab() == hash)
                zdos.Add(zdo);
        index = Zdos.Count;
        return true;
    }

    /// <summary>The ZDOs whose position lies in the sector (zone).</summary>
    public void FindObjects(Vector2i sector, System.Collections.Generic.List<ZDO> objects)
    {
        foreach (var zdo in Zdos)
            if (zdo.GetSector() == sector)
                objects.Add(zdo);
    }

    public int CountWithPrefab(string prefab)
    {
        var found = new System.Collections.Generic.List<ZDO>();
        int index = 0;
        GetAllZDOsWithPrefabIterative(prefab, found, ref index);
        return found.Count;
    }
}

/// <summary>
/// Shim for Valheim's TerrainComp (_TerrainCompiler): the per-vertex arrays
/// the road code writes. (m_width + 1)^2 vertices, row-major, y outer.
/// </summary>
public class TerrainComp
{
    public const string PrefabName = "_TerrainCompiler";

    public int m_width;
    public Heightmap m_hmap;
    public ZNetView m_nview;
    public float[] m_levelDelta;
    public float[] m_smoothDelta;
    public bool[] m_modifiedHeight;
    public UnityEngine.Color[] m_paintMask;
    public bool[] m_modifiedPaint;
    public int SaveCount;

    /// <summary>The zone's live compiler: the one on the registered heightmap, if that heightmap has one.</summary>
    public static TerrainComp? FindTerrainCompiler(UnityEngine.Vector3 pos)
    {
        var hm = Heightmap.Registered;
        if (hm?.m_terrainComp == null)
            return null;
        return UnityEngine.Mathf.Abs(hm.transform.position.x - pos.x) < 32f && UnityEngine.Mathf.Abs(hm.transform.position.z - pos.z) < 32f
            ? hm.m_terrainComp : null;
    }

    /// <summary>A new compiler for the heightmap's zone with its own ZDO, registered with the ZDOMan when there is one and owned by us.</summary>
    public TerrainComp(Heightmap hmap, int width)
    {
        m_hmap = hmap;
        m_width = width;
        int prefab = PrefabName.GetStableHashCode();
        ZDO zdo = ZDOMan.instance != null
            ? ZDOMan.instance.CreateNewZDO(hmap.transform.position, prefab)
            : new ZDO(hmap.transform.position, prefab);
        zdo.Persistent = true;
        zdo.SetOwner(ZDOMan.instance?.m_sessionID ?? 1);
        m_nview = new ZNetView(zdo);
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

    /// <summary>Zones the test declares loaded; every zone counts as loaded when null.</summary>
    public System.Collections.Generic.HashSet<Vector2i>? LoadedZones;
    public bool IsZoneLoaded(Vector2i zone) => LoadedZones == null || LoadedZones.Contains(zone);

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
