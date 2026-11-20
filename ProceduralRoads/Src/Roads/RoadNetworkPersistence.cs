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
    /// Hash key for storing the river crossings (fords prototype) on the ZDO.
    /// </summary>
    private static readonly int RoadCrossingsHash = "ProceduralRoads_Crossings".GetStableHashCode();

    /// <summary>
    /// Hash key for the zones that have received their bridge pieces (bridges prototype).
    /// </summary>
    private static readonly int BridgeZonesHash = "ProceduralRoads_BridgeZones".GetStableHashCode();

    /// <summary>
    /// Hash key for the zones whose vegetation matches the network (see VegetationClearing).
    /// </summary>
    private static readonly int ClearedZonesHash = "ProceduralRoads_ClearedZones".GetStableHashCode();

    /// <summary>The network's staircases: their pieces and clearances (StairStructures).</summary>
    private static readonly int StairRunsHash = "ProceduralRoads_StairRuns".GetStableHashCode();

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

    /// <summary>Whether the saved staircases could not be read this session: none may be added (their record is kept as it was).</summary>
    internal static bool StairRecordKept => s_kept.ContainsKey(StairRunsHash);

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
    public static void SaveGlobalRoadData(
        IReadOnlyList<(Vector2 position, string label)> roadStartPoints,
        IReadOnlyList<RoadCrossing> roadCrossings,
        IReadOnlyCollection<Vector2s> bridgeZones)
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
        else if (RoadSpatialGrid.GridCellsWithRoads == 0)
        {
            // A world with no roads is a legitimate state - IslandRoadPercentage
            // is documented as disabling generation at zero - and it is saved on
            // the same schedule as any other, so warning here fired on every
            // autosave for the rest of the session.
            Log.LogDebug("[SAVE] No roads to save; the network is empty.");
        }
        else
        {
            Log.LogWarning("[SAVE] SerializeAllRoadPoints returned null or empty data!");
        }

        WriteRecord(metadataZdo, RoadStartPointsHash, () => SerializeRoadStartPoints(roadStartPoints));
        // Always written, so a network regenerated without fords does not
        // keep the crossings of an earlier one.
        WriteRecord(metadataZdo, RoadCrossingsHash, () => SerializeRoadCrossings(roadCrossings));
        WriteStructureRecords(metadataZdo, bridgeZones);
    }

    private static void WriteStructureRecords(ZDO metadataZdo, IReadOnlyCollection<Vector2s> bridgeZones)
    {
        WriteRecord(metadataZdo, BridgeZonesHash, () => SerializeBridgeZones(bridgeZones));
        WriteRecord(metadataZdo, StairRunsHash, StairStructures.Serialize);
    }

    /// <summary>
    /// Save only the zones that have their bridge pieces, on a network that
    /// was loaded rather than generated this session.
    /// </summary>
    public static void SaveBridgeZones(IReadOnlyCollection<Vector2s> bridgeZones)
    {
        ZDO? metadataZdo = GetMetadataZDO();
        if (metadataZdo == null)
        {
            Log.LogWarning("[SAVE] No metadata ZDO: bridge zones not saved");
            return;
        }
        if (!metadataZdo.IsOwner())
            metadataZdo.SetOwner(ZDOMan.instance.m_sessionID);
        WriteStructureRecords(metadataZdo, bridgeZones);
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
    public static bool TryLoadGlobalRoadData(
        List<(Vector2 position, string label)> roadStartPoints,
        List<RoadCrossing> roadCrossings,
        HashSet<Vector2s> bridgeZones)
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
                ReadOrKeep(metadataZdo, RoadCrossingsHash, "river crossings", b => ReadCrossings(b, roadCrossings), roadCrossings.Clear);
                ReadOrKeep(metadataZdo, BridgeZonesHash, "bridge zones", b => ReadBridgeZonesOrStartAfresh(b, bridgeZones), bridgeZones.Clear);
                ReadOrKeep(metadataZdo, StairRunsHash, "staircases", StairStructures.Load, StairStructures.Reset);
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
                roadCrossings.Clear();
                bridgeZones.Clear();
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

    /// <summary>The river crossings stored with the network; a save without the record leaves the list empty.</summary>
    private static void ReadCrossings(byte[]? data, List<RoadCrossing> roadCrossings)
    {
        roadCrossings.Clear();
        if (data == null || data.Length == 0) return;
        if (!DeserializeRoadCrossings(data, roadCrossings)) throw new InvalidDataException("unreadable river crossings");
    }

    /// <summary>
    /// Serialize river crossings to binary format.
    /// Format: [version=3][count] then per crossing [fromX][fromY][toX][toY][riverbed][fairwayX][fairwayY][fairwayWidth][kind][style][flags][kit+1, 0 none].
    /// flags: bit 0, a Mistlands viaduct the search bought over a dry valley (<see cref="RoadCrossing.ValleyJump"/>),
    /// not a river's bridge. Versions 1 (no flags) and 2 (no kit, decided again from the crossing) are read too. The rest
    /// of a crossing is derived from its banks on load.
    /// </summary>
    private static byte[] SerializeRoadCrossings(IReadOnlyList<RoadCrossing> roadCrossings)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write(CrossingsFormat);
        writer.Write(roadCrossings.Count);
        foreach (RoadCrossing crossing in roadCrossings)
        {
            writer.Write(crossing.FromBank.x);
            writer.Write(crossing.FromBank.y);
            writer.Write(crossing.ToBank.x);
            writer.Write(crossing.ToBank.y);
            writer.Write(crossing.RiverbedHeight);
            writer.Write(crossing.FairwayCenter.x);
            writer.Write(crossing.FairwayCenter.y);
            writer.Write(crossing.FairwayWidth);
            writer.Write((int)crossing.Kind);
            writer.Write((int)crossing.Style);
            writer.Write(crossing.ValleyJump ? 1 : 0);
            writer.Write(crossing.Kit is BridgeStructures.Kit kit ? (int)kit + 1 : 0);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// The spawned-zone record is a cache of which zones have their bridge pieces: one this build cannot read is started
    /// afresh and this session's zones are saved (keeping it unread would lose every session's record, and a bridge whose
    /// pieces players took would come back at each restart). Only a record a newer build wrote (a format past 2) is kept
    /// as it is (<see cref="ReadOrKeep"/>), for that build.
    /// </summary>
    private static void ReadBridgeZonesOrStartAfresh(byte[]? data, HashSet<Vector2s> bridgeZones)
    {
        if (data != null && data.Length >= 4 && BitConverter.ToInt32(data, 0) > 2)
            throw new InvalidDataException($"bridge zone data version {BitConverter.ToInt32(data, 0)} is a newer build's");
        try { ReadBridgeZones(data, bridgeZones); }
        catch (Exception e) when (!(e is OutOfMemoryException))
        {
            Log.LogWarning($"[LOAD] The saved bridge zones could not be read ({e.Message}); they are recorded afresh from this session");
            bridgeZones.Clear();
        }
    }

    /// <summary>Read the spawned-zone record. A record written by an older bridge layout keeps its zones marked
    /// spawned: a layout change reaches only zones that have no pieces yet, and a world's standing bridges are never
    /// rebuilt.</summary>
    private static void ReadBridgeZones(byte[]? data, HashSet<Vector2s> bridgeZones)
    {
        bridgeZones.Clear();
        if (data == null || data.Length == 0)
            return;
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);
        int version = reader.ReadInt32();
        int layout;
        if (version == 1)
        {
            // Written before the record carried a layout id: the narrow,
            // single-lane bridges. Every zone recorded here holds pieces of
            // a shape this build no longer makes.
            layout = 1;
        }
        else if (version == 2)
        {
            layout = reader.ReadInt32();
        }
        else
            throw new InvalidDataException($"unknown bridge zone data version {version}");
        int count = reader.ReadInt32();
        if (count < 0 || count > 1_000_000)
            throw new InvalidDataException($"invalid bridge zone count {count}");
        if (layout != BridgeLayout.LayoutVersion)
        {
            Log.LogInfo($"[BRIDGES] the saved bridges were laid out by layout {layout}; this build is layout {BridgeLayout.LayoutVersion}. " +
                        $"Their {count} zone(s) keep the pieces they have; only zones without pieces get the new layout.");
        }
        for (int i = 0; i < count; i++)
            bridgeZones.Add(new Vector2s((short)reader.ReadInt32(), (short)reader.ReadInt32()));
        Log.LogDebug($"Loaded {bridgeZones.Count} bridge zones from ZDO");
    }

    /// <summary>
    /// Format: [version=2][layout][count] then [x][y] per zone, each a 32-bit
    /// int. Version 1 had no layout id and is read as layout 1, the narrow
    /// bridges. Valheim 1.0 made a zone id a pair of shorts; the casts here
    /// keep the saved bytes as they were, so worlds written before 1.0 still
    /// read.
    /// </summary>
    private static byte[] SerializeBridgeZones(IReadOnlyCollection<Vector2s> zones)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(2);
        writer.Write(BridgeLayout.LayoutVersion);
        writer.Write(zones.Count);
        foreach (Vector2s zone in zones)
        {
            writer.Write((int)zone.x);
            writer.Write((int)zone.y);
        }
        return ms.ToArray();
    }

    /// <summary>The crossings record's format: 2 carries each crossing's flags (a valley viaduct's
    /// <see cref="RoadCrossing.ValleyJump"/>, which the load puts its keep-out back from). 1 is read too.</summary>
    internal const int CrossingsFormat = 3;

    private static bool DeserializeRoadCrossings(byte[] data, List<RoadCrossing> roadCrossings)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            int version = reader.ReadInt32();
            if (version < 1 || version > CrossingsFormat)
            {
                Log.LogWarning($"Unknown river crossing data version: {version}");
                return false;
            }

            int count = reader.ReadInt32();
            if (count < 0 || count > 10000)
            {
                Log.LogWarning($"Invalid river crossing count: {count}");
                return false;
            }

            roadCrossings.Clear();
            for (int i = 0; i < count; i++)
            {
                Vector2 fromBank = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                Vector2 toBank = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                float riverbed = reader.ReadSingle();
                Vector2 fairwayCenter = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                float fairwayWidth = reader.ReadSingle();
                CrossingKind kind = (CrossingKind)reader.ReadInt32();
                FordStyle style = (FordStyle)reader.ReadInt32();
                int flags = version >= 2 ? reader.ReadInt32() : 0;
                int kit = version >= 3 ? reader.ReadInt32() : 0;
                if (kit < 0 || kit > Enum.GetValues(typeof(BridgeStructures.Kit)).Length) { Log.LogWarning($"Invalid crossing kit: {kit}"); return false; }
                RoadCrossing crossing = RoadCrossing.Between(fromBank, toBank, riverbed, fairwayCenter, fairwayWidth, kind, style);
                crossing.ValleyJump = (flags & 1) != 0;
                if (kit > 0) crossing.Kit = (BridgeStructures.Kit)(kit - 1);
                roadCrossings.Add(crossing);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Failed to deserialize river crossings: {ex.Message}");
            return false;
        }
    }
}
