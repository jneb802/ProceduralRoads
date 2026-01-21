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
    /// Hash key for storing global road network data on the ZDO.
    /// </summary>
    private static readonly int GlobalRoadDataHash = "ProceduralRoads_GlobalData".GetStableHashCode();

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
    }

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
            Log.LogDebug($"[META] Found existing metadata ZDO: {s_metadataZdo.m_uid}");
            return;
        }

        if (ZDOMan.instance == null)
        {
            Log.LogError("[META] ZDOMan.instance is null!");
            return;
        }

        s_metadataZdo = ZDOMan.instance.CreateNewZDO(Vector3.zero, MetadataPrefabHash);
        s_metadataZdo.Persistent = true;
        s_metadataZdo.SetPrefab(MetadataPrefabHash);

        Log.LogDebug($"[META] Created metadata ZDO directly: m_uid={s_metadataZdo.m_uid}, prefab={s_metadataZdo.GetPrefab()}");
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

        Log.LogDebug($"[SAVE] Using metadata ZDO: m_uid={metadataZdo.m_uid}, prefab={metadataZdo.GetPrefab()}");

        byte[]? data = RoadSpatialGrid.SerializeAllRoadPoints();
        if (data != null && data.Length > 0)
        {
            metadataZdo.Set(GlobalRoadDataHash, data);
            Log.LogDebug($"[SAVE] Saved global road data: {data.Length} bytes, {RoadSpatialGrid.GridCellsWithRoads} cells, {RoadSpatialGrid.TotalRoadPoints} points");
        }
        else
        {
            Log.LogWarning("[SAVE] SerializeAllRoadPoints returned null or empty data!");
        }

        byte[] startPointsData = SerializeRoadStartPoints(roadStartPoints);
        if (startPointsData != null && startPointsData.Length > 0)
        {
            metadataZdo.Set(RoadStartPointsHash, startPointsData);
            Log.LogDebug($"[SAVE] Saved {roadStartPoints.Count} road start points ({startPointsData.Length} bytes)");
        }
    }

    /// <summary>
    /// Try to load the entire road network from persisted ZDO.
    /// </summary>
    /// <param name="roadStartPoints">List to populate with loaded start points</param>
    /// <returns>True if road data was found and loaded</returns>
    public static bool TryLoadGlobalRoadData(List<(Vector2 position, string label)> roadStartPoints)
    {
        Log.LogDebug("[LOAD] TryLoadGlobalRoadData called");

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
            Log.LogDebug($"[LOAD] Successfully loaded: {RoadSpatialGrid.GridCellsWithRoads} cells, {RoadSpatialGrid.TotalRoadPoints} points");

            TryLoadRoadMetadata(roadStartPoints);

            return true;
        }

        Log.LogWarning("[LOAD] DeserializeAllRoadPoints returned false!");
        return false;
    }

    /// <summary>
    /// Try to load road metadata from persisted ZDO.
    /// </summary>
    /// <param name="roadStartPoints">List to populate with loaded start points</param>
    /// <returns>True if metadata was found and loaded</returns>
    public static bool TryLoadRoadMetadata(List<(Vector2 position, string label)> roadStartPoints)
    {
        if (ZDOMan.instance == null)
            return false;

        ZDO? metadataZdo = FindMetadataZDO();
        if (metadataZdo == null)
        {
            Log.LogDebug("No road metadata ZDO found");
            return false;
        }

        byte[]? data = metadataZdo.GetByteArray(RoadStartPointsHash, null);
        if (data == null || data.Length == 0)
        {
            Log.LogDebug("Metadata ZDO found but no start points data");
            return false;
        }

        if (DeserializeRoadStartPoints(data, roadStartPoints))
        {
            Log.LogDebug($"Loaded {roadStartPoints.Count} road start points from ZDO");
            return true;
        }

        return false;
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
