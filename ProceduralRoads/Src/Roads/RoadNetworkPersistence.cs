using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Handles ZDO-based persistence for the road network.
/// Manages the metadata ZDO lifecycle and serialization of road start points.
/// </summary>
public static class RoadNetworkPersistence
{
    private static ManualLogSource Log => ProceduralRoadsPlugin.ProceduralRoadsLogger;

    /// <summary>
    /// Unique prefab name for our metadata ZDO. Must not conflict with any game prefabs.
    /// This is public so Plugin.cs can register the prefab with Jotunn.
    /// </summary>
    public const string MetadataPrefabName = "ProceduralRoads_Metadata";

    /// <summary>
    /// Prefab hash derived from the name.
    /// </summary>
    private static readonly int MetadataPrefabHash = MetadataPrefabName.GetStableHashCode();

    /// <summary>
    /// Hash key for storing road start points data on the ZDO.
    /// </summary>
    private static readonly int RoadStartPointsHash = "ProceduralRoads_StartPoints".GetStableHashCode();

    /// <summary>
    /// Hash key for the zones whose vegetation matches the network (see VegetationClearing).
    /// </summary>
    private static readonly int ClearedZonesHash = "ProceduralRoads_ClearedZones".GetStableHashCode();


    /// <summary>Hash key for storing global road network data on the ZDO.</summary>
    private static readonly int GlobalRoadDataHash = "ProceduralRoads_GlobalData".GetStableHashCode();

    /// <summary>
    /// Hash key for the zones generated with the saved network in place (see NetworkCutoff).
    /// </summary>
    private static readonly int ZonesWithNetworkHash = "ProceduralRoads_ZonesWithNetwork".GetStableHashCode();

    /// <summary>
    /// Whether the last TryLoadGlobalRoadData found a saved network it could
    /// not read: the metadata ZDO holds road data, but its version is unknown,
    /// it fails validation, or reading it threw. Such a world is never given a
    /// new network automatically (RoadLifecycleManager): only road_generate
    /// replaces it.
    /// </summary>
    public static bool LastLoadUnreadable { get; private set; }

    /// <summary>
    /// Cached reference to the metadata ZDO.
    /// </summary>
    private static ZDO? s_metadataZdo;

    /// <summary>
    /// Reset persistence state. Call on world unload.
    /// </summary>
    public static void Reset()
    {
        s_metadataZdo = null;
        s_kept.Clear();
    }

    /// <summary>Saved records this build could not read at load, by key: not used this session and written back unchanged.</summary>
    private static readonly Dictionary<int, (string what, byte[] bytes)> s_kept = new();


    /// <summary>What of the saved network could not be read this session (kept as saved); empty when everything was read.</summary>
    internal static IEnumerable<string> KeptRecords => System.Linq.Enumerable.Select(s_kept.Values, k => k.what);

    /// <summary>
    /// The one rule for a saved record this build cannot read: it costs the world neither its roads nor the record.
    /// Its part is empty this session (<paramref name="empty"/>), its bytes are written back as they were on every save,
    /// and nothing new is saved under its key.
    /// </summary>
    private static void ReadOrKeep(ZDO zdo, int key, string what, Action<byte[]?> read, Action empty)
    {
        byte[]? bytes = zdo.GetByteArray(key, null);
        try { read(bytes); }
        catch (Exception e) when (!(e is OutOfMemoryException))
        {
            Log.LogError($"[LOAD] The saved {what} could not be read; the roads load without them this session and the record is kept as it is: {e.Message}");
            empty();
            if (bytes != null) s_kept[key] = (what, bytes);
        }
    }

    /// <summary>Set a record, or write back the one kept unread (<see cref="ReadOrKeep"/>).</summary>
    private static void WriteRecord(ZDO zdo, int key, Func<byte[]> current) =>
        zdo.Set(key, s_kept.TryGetValue(key, out var kept) ? kept.bytes : current());

    /// <summary>The bytes saved under a key of the network's metadata (tests).</summary>
    internal static byte[]? SavedRecord(string key) => GetMetadataZDO()?.GetByteArray(key.GetStableHashCode(), null);

    /// <summary>Replaces the bytes saved under a key of the network's metadata (tests).</summary>
    internal static void SetSavedRecord(string key, byte[] bytes) => GetMetadataZDO()!.Set(key.GetStableHashCode(), bytes);

    /// <summary>
    /// Position far outside playable area to prevent ZNetScene from trying to instantiate.
    /// The world radius is ~10000, so 50000 is well beyond any active area.
    /// </summary>
    private static readonly Vector3 FarPosition = new Vector3(50000f, 0f, 50000f);

    /// <summary>
    /// Ensure the metadata ZDO exists. Call this after road generation.
    /// Creates the ZDO directly - no GameObject needed during the session.
    /// </summary>
    public static void EnsureMetadataInstance()
    {
        if (s_metadataZdo != null)
        {
            Log.LogDebug("[META] Metadata ZDO already cached");
            return;
        }

        s_metadataZdo = FindMetadataZDO();
        if (s_metadataZdo != null)
        {
            // Migrate old ZDOs that were created at origin to far position
            MigrateZdoPosition(s_metadataZdo);
            Log.LogDebug($"[META] Found existing metadata ZDO: {s_metadataZdo.m_uid}");
            return;
        }

        if (ZDOMan.instance == null)
        {
            Log.LogError("[META] ZDOMan.instance is null!");
            return;
        }

        s_metadataZdo = ZDOMan.instance.CreateNewZDO(FarPosition, MetadataPrefabHash);
        s_metadataZdo.Persistent = true;
        s_metadataZdo.SetPrefab(MetadataPrefabHash);
        s_metadataZdo.SetOwner(ZDOMan.instance.m_sessionID);

        Log.LogDebug($"[META] Created metadata ZDO directly: m_uid={s_metadataZdo.m_uid}, prefab={s_metadataZdo.GetPrefab()}, owner={s_metadataZdo.GetOwner()}");
    }

    /// <summary>
    /// Save the entire road network to a dedicated ZDO for persistence across world reloads.
    /// </summary>
    public static void SaveGlobalRoadData(IReadOnlyList<(Vector2 position, string label)> roadStartPoints)
    {
        Log.LogDebug($"[SAVE] SaveGlobalRoadData called");

        ZDO? metadataZdo = GetMetadataZDO();

        if (metadataZdo == null)
        {
            Log.LogError("[SAVE] No metadata ZDO available! EnsureMetadataInstance should have been called after road generation.");
            return;
        }

        // Claim ownership so our changes are persisted to the save file
        if (!metadataZdo.IsOwner())
        {
            metadataZdo.SetOwner(ZDOMan.instance.m_sessionID);
            Log.LogDebug($"[SAVE] Claimed ownership of metadata ZDO");
        }

        Log.LogDebug($"[SAVE] Using metadata ZDO: m_uid={metadataZdo.m_uid}, prefab={metadataZdo.GetPrefab()}, owner={metadataZdo.GetOwner()}");

        byte[]? data = RoadSpatialGrid.SerializeAllRoadPoints();
        if (data != null && data.Length > 0)
        {
            metadataZdo.Set(GlobalRoadDataHash, data);
            Log.LogDebug($"[SAVE] Saved global road data: {data.Length} bytes, {RoadSpatialGrid.GridCellsWithRoads} cells, {RoadSpatialGrid.StoredPointRecords} stored records");
        }
        else
        {
            Log.LogWarning("[SAVE] SerializeAllRoadPoints returned null or empty data!");
        }

        WriteRecord(metadataZdo, RoadStartPointsHash, () => SerializeRoadStartPoints(roadStartPoints));
    }


    /// <summary>
    /// Save the zones whose vegetation matches the network (VegetationClearing),
    /// with the network version they belong to, or write back the record kept unread.
    /// Format: [format=1][network version][count] then [x][y] per zone, 32-bit ints.
    /// </summary>
    public static void SaveClearedZones(int networkVersion, IReadOnlyCollection<Vector2s> zones)
    {
        ZDO? metadataZdo = GetMetadataZDO();
        if (metadataZdo == null)
        {
            Log.LogWarning("[SAVE] No metadata ZDO: cleared zones not saved");
            return;
        }
        if (!metadataZdo.IsOwner())
            metadataZdo.SetOwner(ZDOMan.instance.m_sessionID);

        WriteRecord(metadataZdo, ClearedZonesHash, () =>
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(1);
            writer.Write(networkVersion);
            writer.Write(zones.Count);
            foreach (Vector2s zone in zones)
            {
                writer.Write((int)zone.x);
                writer.Write((int)zone.y);
            }
            return ms.ToArray();
        });
        Log.LogDebug($"[SAVE] Saved {zones.Count} zones cleared of vegetation for network {networkVersion}");
    }

    /// <summary>The saved cleared-zone record into <see cref="VegetationClearing"/>; a save without one has cleared nothing.</summary>
    private static void ReadClearedZones(byte[]? data)
    {
        VegetationClearing.Reset();
        if (data == null || data.Length == 0) return;
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);
        int format = reader.ReadInt32();
        if (format != 1) throw new InvalidDataException($"unknown cleared zone data format {format}");
        int version = reader.ReadInt32();
        int count = reader.ReadInt32();
        if (count < 0 || count > 1_000_000) throw new InvalidDataException($"invalid cleared zone count {count}");
        var zones = new List<Vector2s>(count);
        for (int i = 0; i < count; i++)
            zones.Add(new Vector2s((short)reader.ReadInt32(), (short)reader.ReadInt32()));
        if (ms.Position != ms.Length) throw new InvalidDataException("cleared zone data runs past its zones");
        VegetationClearing.Load(version, zones, RoadSpatialGrid.RoadNetworkVersion);
    }

    /// <summary>
    /// Try to load the entire road network from persisted ZDO.
    /// </summary>
    /// <param name="roadStartPoints">List to populate with loaded start points</param>
    /// <returns>True if road data was found and loaded</returns>
    public static bool TryLoadGlobalRoadData(List<(Vector2 position, string label)> roadStartPoints)
    {
        Log.LogDebug("[LOAD] TryLoadGlobalRoadData called");
        LastLoadUnreadable = false;
        s_kept.Clear();

        if (ZDOMan.instance == null)
        {
            Log.LogDebug("[LOAD] ZDOMan.instance is null!");
            return false;
        }

        ZDO? metadataZdo = FindMetadataZDO();
        if (metadataZdo == null)
        {
            Log.LogDebug("[LOAD] No road metadata ZDO found");
            return false;
        }

        Log.LogDebug($"[LOAD] Found metadata ZDO: m_uid={metadataZdo.m_uid}, prefab={metadataZdo.GetPrefab()}");

        byte[]? data = metadataZdo.GetByteArray(GlobalRoadDataHash, null);
        if (data == null || data.Length == 0)
        {
            Log.LogDebug("[LOAD] Metadata ZDO found but no global road data");
            return false;
        }

        Log.LogDebug($"[LOAD] Got {data.Length} bytes of road data, deserializing...");

        if (RoadSpatialGrid.DeserializeAllRoadPoints(data))
        {
            Log.LogDebug($"[LOAD] Successfully loaded: {RoadSpatialGrid.GridCellsWithRoads} cells, {RoadSpatialGrid.StoredPointRecords} stored records");

            try
            {
                ReadOrKeep(metadataZdo, RoadStartPointsHash, "road start points", b => ReadStartPoints(b, roadStartPoints), roadStartPoints.Clear);
                // NetworkCutoff reads it when the network is used, and unread excludes every generated zone; kept, a
                // save cannot put that fallback over the zones the record says are the network's.
                ReadOrKeep(metadataZdo, ZonesWithNetworkHash, "zones generated with the network", CheckZonesWithNetwork, () => { });
                // Unread, which zones were cleared is not known, not none: none is cleared again on a guess.
                ReadOrKeep(metadataZdo, ClearedZonesHash, "zones cleared of vegetation", ReadClearedZones, VegetationClearing.HistoryUnread);
            }
            catch (Exception ex)
            {
                // Half a network is not a network: forget what was read, and
                // say the save could not be read rather than that it is empty.
                Log.LogError($"[LOAD] The saved road network could not be read: {ex}");
                RoadSpatialGrid.Clear();
                roadStartPoints.Clear();
                LastLoadUnreadable = true;
                return false;
            }
            return true;
        }

        LastLoadUnreadable = true;
        Log.LogError($"[LOAD] This world holds a saved road network ({data.Length} bytes) that this build cannot read " +
                     $"({RoadSpatialGrid.LastReadRefusal}). " +
                     "It is left as it is and no new network is generated over it; the world has no roads this session. " +
                     "Use a build that reads it, or road_generate to replace it on purpose.");
        return false;
    }

    /// <summary>The saved record of zones generated with the network in place (NetworkCutoff), if any.</summary>
    public static byte[]? LoadZonesWithNetwork() => GetMetadataZDO()?.GetByteArray(ZonesWithNetworkHash, null);

    /// <summary>Save the record of zones generated with the network in place (NetworkCutoff), or write back the one kept unread.</summary>
    public static void SaveZonesWithNetwork(byte[] record)
    {
        ZDO? metadataZdo = GetMetadataZDO();
        if (metadataZdo == null)
        {
            Log.LogWarning("[SAVE] No metadata ZDO: zones generated with the network not saved");
            return;
        }
        if (!metadataZdo.IsOwner())
            metadataZdo.SetOwner(ZDOMan.instance.m_sessionID);
        WriteRecord(metadataZdo, ZonesWithNetworkHash, () => record);
    }

    /// <summary>A saved record of zones generated with the network is one NetworkCutoff can read; none at all is not unread.</summary>
    private static void CheckZonesWithNetwork(byte[]? data)
    {
        if (data != null && data.Length > 0 && !NetworkCutoff.TryRead(data, out _, out _))
            throw new InvalidDataException("unreadable record of zones generated with the network");
    }

    private static void ReadStartPoints(byte[]? data, List<(Vector2 position, string label)> roadStartPoints)
    {
        roadStartPoints.Clear();
        if (data == null || data.Length == 0) return;
        if (!DeserializeRoadStartPoints(data, roadStartPoints)) throw new InvalidDataException("unreadable road start points");
    }

    /// <summary>
    /// Migrate ZDOs that were created at origin to far position.
    /// This prevents ZNetScene from trying to instantiate them when players are near spawn.
    /// </summary>
    private static void MigrateZdoPosition(ZDO zdo)
    {
        Vector3 currentPos = zdo.GetPosition();
        
        // Check if ZDO is within playable area (roughly within 15000 units of origin)
        if (currentPos.x < 15000f && currentPos.x > -15000f &&
            currentPos.z < 15000f && currentPos.z > -15000f)
        {
            Log.LogDebug($"[META] Migrating metadata ZDO from {currentPos} to {FarPosition}");
            zdo.SetPosition(FarPosition);
        }
    }

    /// <summary>
    /// Get the metadata ZDO, either from cache or by searching.
    /// </summary>
    private static ZDO? GetMetadataZDO()
    {
        if (s_metadataZdo != null)
            return s_metadataZdo;

        s_metadataZdo = FindMetadataZDO();
        return s_metadataZdo;
    }

    /// <summary>
    /// Find the metadata ZDO by iterating through all ZDOs with our prefab hash.
    /// </summary>
    private static ZDO? FindMetadataZDO()
    {
        if (ZDOMan.instance == null)
        {
            Log.LogDebug("[FIND] ZDOMan.instance is null");
            return null;
        }

        var zdos = new List<ZDO>();
        int index = 0;

        while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(MetadataPrefabName, zdos, ref index))
        {
        }

        Log.LogDebug($"[FIND] Search complete: found {zdos.Count} ZDOs matching '{MetadataPrefabName}'");

        if (zdos.Count > 0)
        {
            var zdo = zdos[0];
            Log.LogDebug($"[FIND] Found ZDO: m_uid={zdo.m_uid}, prefab={zdo.GetPrefab()}, persistent={zdo.Persistent}");
            return zdo;
        }

        Log.LogDebug("[FIND] No matching ZDO found");
        return null;
    }

    /// <summary>
    /// Serialize road start points to binary format.
    /// Format: [count][x1][y1][labelLen1][labelBytes1]...
    /// </summary>
    private static byte[] SerializeRoadStartPoints(IReadOnlyList<(Vector2 position, string label)> roadStartPoints)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(roadStartPoints.Count);

        foreach (var (position, label) in roadStartPoints)
        {
            writer.Write(position.x);
            writer.Write(position.y);

            byte[] labelBytes = Encoding.UTF8.GetBytes(label ?? "");
            writer.Write(labelBytes.Length);
            writer.Write(labelBytes);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Deserialize road start points from binary format.
    /// </summary>
    private static bool DeserializeRoadStartPoints(byte[] data, List<(Vector2 position, string label)> roadStartPoints)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            int count = reader.ReadInt32();
            if (count < 0 || count > 10000)
            {
                Log.LogWarning($"Invalid road start points count: {count}");
                return false;
            }

            roadStartPoints.Clear();

            for (int i = 0; i < count; i++)
            {
                float x = reader.ReadSingle();
                float y = reader.ReadSingle();
                int labelLen = reader.ReadInt32();

                if (labelLen < 0 || labelLen > 1000)
                {
                    Log.LogWarning($"Invalid label length: {labelLen}");
                    return false;
                }

                byte[] labelBytes = reader.ReadBytes(labelLen);
                string label = Encoding.UTF8.GetString(labelBytes);

                roadStartPoints.Add((new Vector2(x, y), label));
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Failed to deserialize road start points: {ex.Message}");
            return false;
        }
    }






}
