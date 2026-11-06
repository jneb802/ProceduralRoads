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

    /// <summary>Hash key for the shore landings and whether the network reaches them.</summary>
    private static readonly int CoastLandingsHash = "ProceduralRoads_CoastLandings".GetStableHashCode();

    /// <summary>
    /// Hash key for the zones that have received their bridge pieces (bridges prototype).
    /// </summary>
    private static readonly int BridgeZonesHash = "ProceduralRoads_BridgeZones".GetStableHashCode();

    /// <summary>
    /// Hash key for the zones whose vegetation matches the network (see VegetationClearing).
    /// </summary>
    private static readonly int ClearedZonesHash = "ProceduralRoads_ClearedZones".GetStableHashCode();

    /// <summary>Hash key for the bridge pieces owed by manual roads (StructureAppendQueue).</summary>
    private static readonly int AppendBridgesHash = "ProceduralRoads_AppendBridges".GetStableHashCode();

    /// <summary>Map icons of places placed before their roads, kept off the map until a player comes near (<see cref="HiddenSiteIcons"/>).</summary>
    private static readonly int HiddenSiteIconsHash = "ProceduralRoads_HiddenSiteIcons".GetStableHashCode();

    /// <summary>The places placed before their roads: their placings, which a load makes ready again (PlacedSites).</summary>
    private static readonly int PlacedSitesHash = "ProceduralRoads_PlacedSites".GetStableHashCode();

    /// <summary>The network's staircases: their layouts, clearances and ground (StairStructures).</summary>
    private static readonly int StairRunsHash = "ProceduralRoads_StairRuns".GetStableHashCode();

    /// <summary>The crossing sites whose first zone was built: their pieces and the ground they stand on (BuiltBridges).</summary>
    private static readonly int BuiltBridgesHash = "ProceduralRoads_BuiltBridges".GetStableHashCode();

    /// <summary>The staircases whose first zone was built: the pieces their kit laid and the ground they stand on (BuiltStairs).</summary>
    private static readonly int BuiltStairsHash = "ProceduralRoads_BuiltStairs".GetStableHashCode();

    /// <summary>Hash key for storing global road network data on the ZDO.</summary>
    private static readonly int GlobalRoadDataHash = "ProceduralRoads_GlobalData".GetStableHashCode();

    /// <summary>
    /// Hash key for the zones generated with the saved network in place (see NetworkCutoff).
    /// </summary>
    private static readonly int ZonesWithNetworkHash = "ProceduralRoads_ZonesWithNetwork".GetStableHashCode();

    /// <summary>Hash key for the answer of the world's first load, saved when it skipped the roads (ExistingWorldFirstLoad).</summary>
    private static readonly int FirstLoadHash = "ProceduralRoads_FirstLoad".GetStableHashCode();

    /// <summary>
    /// Whether the last TryLoadGlobalRoadData found a saved network it could
    /// not read: the metadata ZDO holds road data, but its version is unknown,
    /// it fails validation, or reading it threw. Such a world is never given a
    /// new network automatically (RoadLifecycleManager), nor a road added to it
    /// (ManualRoads): only road_generate replaces it.
    /// </summary>
    public static bool LastLoadUnreadable { get; private set; }

    /// <summary>A network generated this session is the world's from now on: a saved one that could not be read no
    /// longer stands in the way of roads added to it, and its records are saved afresh in place of the ones kept unread.</summary>
    public static void NetworkGenerated()
    {
        LastLoadUnreadable = false;
        s_kept.Clear();
    }

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
        LastLoadUnreadable = false;
        s_kept.Clear();
    }

    /// <summary>Saved records this build could not read at load, by key: not used this session and written back unchanged.
    /// Like <see cref="LastLoadUnreadable"/>, they are the saved world's until a network is generated in their place
    /// (<see cref="NetworkGenerated"/>) or the world unloads (<see cref="Reset"/>): a generation that fails replaces nothing.</summary>
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

    /// <summary>The metadata object a save writes to, claimed for this session so the change is persisted to the save file
    /// (null when there is none).</summary>
    private static ZDO? ClaimedMetadata()
    {
        ZDO? zdo = GetMetadataZDO();
        if (zdo != null && !zdo.IsOwner())
            zdo.SetOwner(ZDOMan.instance.m_sessionID);
        return zdo;
    }

    /// <summary>Set a record, or write back the one kept unread (<see cref="ReadOrKeep"/>).</summary>
    private static void WriteRecord(ZDO zdo, int key, Func<byte[]> current) =>
        zdo.Set(key, s_kept.TryGetValue(key, out var kept) ? kept.bytes : current());

    /// <summary>Whether the world holds a saved road network, whether or not this session has it in memory.</summary>
    internal static bool SavedNetworkExists => GetMetadataZDO()?.GetByteArray(GlobalRoadDataHash, null) is { Length: > 0 };

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
        IReadOnlyCollection<Vector2s> bridgeZones,
        IReadOnlyList<CoastalLandingPlace>? coastalLandings = null)
    {
        Log.LogDebug($"[SAVE] SaveGlobalRoadData called");

        ZDO? metadataZdo = ClaimedMetadata();

        if (metadataZdo == null)
        {
            Log.LogError("[SAVE] No metadata ZDO available! EnsureMetadataInstance should have been called after road generation.");
            return;
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
        if (coastalLandings != null)
            WriteRecord(metadataZdo, CoastLandingsHash, () => SerializeCoastalLandings(coastalLandings));
        WriteStructureRecords(metadataZdo, bridgeZones);
    }

    private static void WriteStructureRecords(ZDO metadataZdo, IReadOnlyCollection<Vector2s> bridgeZones)
    {
        WriteRecord(metadataZdo, BridgeZonesHash, () => SerializeBridgeZones(bridgeZones));
        WriteRecord(metadataZdo, AppendBridgesHash, StructureAppendQueue.Serialize);
        WriteRecord(metadataZdo, StairRunsHash, StairStructures.Serialize);
        if (StructureSites.Saving)
        {
            WriteRecord(metadataZdo, BuiltBridgesHash, BuiltBridges.Serialize);
            WriteRecord(metadataZdo, BuiltStairsHash, BuiltStairs.Serialize);
        }
        WriteRecord(metadataZdo, HiddenSiteIconsHash, HiddenSiteIcons.Serialize);
        WriteRecord(metadataZdo, PlacedSitesHash, PlacedSites.Serialize);
    }

    /// <summary>
    /// Save only the zones that have their bridge pieces, and the crossings when
    /// given, on a network that was loaded rather than generated this session.
    /// </summary>
    public static void SaveBridgeZones(IReadOnlyCollection<Vector2s> bridgeZones, IReadOnlyList<RoadCrossing>? roadCrossings = null)
    {
        ZDO? metadataZdo = ClaimedMetadata();
        if (metadataZdo == null)
        {
            Log.LogWarning("[SAVE] No metadata ZDO: bridge zones not saved");
            return;
        }
        // The crossings, when given: the kits chosen as their bridges were built (BridgeStructures.ChooseKit).
        if (roadCrossings != null)
            WriteRecord(metadataZdo, RoadCrossingsHash, () => SerializeRoadCrossings(roadCrossings));
        WriteStructureRecords(metadataZdo, bridgeZones);
    }

    /// <summary>
    /// Save the zones whose vegetation matches the network (VegetationClearing),
    /// with the network version they belong to, or write back the record kept unread.
    /// Format: [format=1][network version][count] then [x][y] per zone, 32-bit ints. Format 2, written only when some zone is
    /// owed just the roads appended since it was cleared (<see cref="VegetationClearing.ClearedThrough"/>), adds
    /// [count] then [x][y][appends cleared through] per such zone.
    /// </summary>
    public static void SaveClearedZones(int networkVersion, IReadOnlyCollection<Vector2s> zones,
        IReadOnlyDictionary<Vector2s, int>? through = null)
    {
        ZDO? metadataZdo = ClaimedMetadata();
        if (metadataZdo == null)
        {
            Log.LogWarning("[SAVE] No metadata ZDO: cleared zones not saved");
            return;
        }

        WriteRecord(metadataZdo, ClearedZonesHash, () =>
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            bool owed = through != null && through.Count > 0;
            writer.Write(owed ? ClearedZonesOwedFormat : ClearedZonesFormat);
            writer.Write(networkVersion);
            WriteZones(writer, zones);
            if (owed)
            {
                writer.Write(through!.Count);
                foreach (var zone in through)
                {
                    writer.Write((int)zone.Key.x);
                    writer.Write((int)zone.Key.y);
                    writer.Write(zone.Value);
                }
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
        if (format != ClearedZonesFormat && format != ClearedZonesOwedFormat) throw new InvalidDataException($"unknown cleared zone data format {format}");
        int version = reader.ReadInt32();
        List<Vector2s> zones = ReadZones(reader, "cleared zone");
        var through = new Dictionary<Vector2s, int>();
        if (format == ClearedZonesOwedFormat)
        {
            int owed = reader.ReadInt32();
            if (owed < 0 || owed > MaxSavedZones) throw new InvalidDataException($"invalid owed zone count {owed}");
            for (int i = 0; i < owed; i++)
                through[new Vector2s((short)reader.ReadInt32(), (short)reader.ReadInt32())] = reader.ReadInt32();
        }
        if (ms.Position != ms.Length) throw new InvalidDataException("cleared zone data runs past its zones");
        VegetationClearing.Load(version, zones, RoadSpatialGrid.RoadNetworkVersion, through);
    }

    /// <summary>
    /// Try to load the entire road network from persisted ZDO.
    /// </summary>
    /// <param name="roadStartPoints">List to populate with loaded start points</param>
    /// <returns>True if road data was found and loaded</returns>
    public static bool TryLoadGlobalRoadData(
        List<(Vector2 position, string label)> roadStartPoints,
        List<RoadCrossing> roadCrossings,
        HashSet<Vector2s> bridgeZones,
        List<CoastalLandingPlace>? coastalLandings = null)
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
                if (coastalLandings != null)
                    ReadOrKeep(metadataZdo, CoastLandingsHash, "shore landings", b => ReadCoastalLandings(b, coastalLandings), coastalLandings.Clear);
                ReadOrKeep(metadataZdo, BridgeZonesHash, "bridge zones", b => ReadBridgeZonesOrStartAfresh(b, bridgeZones), bridgeZones.Clear);
                ReadOrKeep(metadataZdo, AppendBridgesHash, "bridge pieces owed by manual roads", StructureAppendQueue.Load, StructureAppendQueue.Reset);
                // Unread, the places' icons show: the map as the game would have it once they are placed.
                ReadOrKeep(metadataZdo, HiddenSiteIconsHash, "hidden site icons", HiddenSiteIcons.Load, HiddenSiteIcons.Reset);
                // Unread, the places are not ready for the roads: as a network saved before they were.
                ReadOrKeep(metadataZdo, PlacedSitesHash, "places placed before their roads", PlacedSites.Restore, PlacedSites.Reset);
                var read = System.Diagnostics.Stopwatch.StartNew();
                ReadOrKeep(metadataZdo, StairRunsHash, "staircases", StairStructures.Load, StairStructures.Reset);
                ReadOrKeep(metadataZdo, BuiltBridgesHash, "built bridges", BuiltBridges.Load, BuiltBridges.Reset);
                // After the staircases, whose reset forgets them.
                ReadOrKeep(metadataZdo, BuiltStairsHash, "built staircases", BuiltStairs.Load, BuiltStairs.Reset);
                Log.LogInfo($"[LOAD] saved structures: {PlacedSites.Count} place(s) placed before their roads " +
                            $"({metadataZdo.GetByteArray(PlacedSitesHash, null)?.Length ?? 0} bytes), {StairStructures.Runs.Count} staircase(s) " +
                            $"({metadataZdo.GetByteArray(StairRunsHash, null)?.Length ?? 0} bytes), {BuiltBridges.Sites.Count} built bridge(s) " +
                            $"({metadataZdo.GetByteArray(BuiltBridgesHash, null)?.Length ?? 0} bytes), {BuiltStairs.Sites.Count} built staircase(s) " +
                            $"({metadataZdo.GetByteArray(BuiltStairsHash, null)?.Length ?? 0} bytes), read in {read.Elapsed.TotalMilliseconds:F0} ms");
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
                coastalLandings?.Clear();
                bridgeZones.Clear();
                StructureAppendQueue.Reset();
                HiddenSiteIcons.Reset();
                PlacedSites.Reset();
                StairStructures.Reset();
                BuiltBridges.Reset();
                BuiltStairs.Reset();
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
        ZDO? metadataZdo = ClaimedMetadata();
        if (metadataZdo == null)
        {
            Log.LogWarning("[SAVE] No metadata ZDO: zones generated with the network not saved");
            return;
        }
        WriteRecord(metadataZdo, ZonesWithNetworkHash, () => record);
    }

    /// <summary>Whether the world's first load skipped the roads (ExistingWorldFirstLoad).</summary>
    public static bool FirstLoadSkipped() => ExistingWorldFirstLoad.SaysSkipped(GetMetadataZDO()?.GetByteArray(FirstLoadHash, null));

    /// <summary>Remember that the world's first load skipped the roads, so no later load asks again or builds them.</summary>
    public static void SaveFirstLoadSkipped()
    {
        EnsureMetadataInstance();
        ZDO? metadataZdo = ClaimedMetadata();
        if (metadataZdo == null)
        {
            Log.LogWarning("[SAVE] No metadata ZDO: the skipped roads are not remembered, and the next load asks again");
            return;
        }
        metadataZdo.Set(FirstLoadHash, ExistingWorldFirstLoad.SkipRecord());
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
    internal static byte[] SerializeRoadStartPoints(IReadOnlyList<(Vector2 position, string label)> roadStartPoints)
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
    internal static bool DeserializeRoadStartPoints(byte[] data, List<(Vector2 position, string label)> roadStartPoints)
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
        if (!DeserializeRoadCrossings(data, roadCrossings, WorldGenerator.instance)) throw new InvalidDataException("unreadable river crossings");
    }

    /// <summary>
    /// Serialize river crossings to binary format.
    /// Format: [version=5][count] then per crossing [fromX][fromY][toX][toY][riverbed][fairwayX][fairwayY][fairwayWidth][kind][style][flags][kit+1, 0 none, -1 undecided]
    /// and each end, from then to (<see cref="RoadCrossing.FromEnd"/>, <see cref="RoadCrossing.ToEnd"/>): [is][edgeX][edgeY][upX][upY][height][steps],
    /// is 0 for none (the rest then 0), 1 a structure's end, 2 an end where the crossing is road.
    /// flags: bit 0, a Mistlands viaduct the search bought over a dry valley (<see cref="RoadCrossing.ValleyJump"/>),
    /// not a river's bridge. An undecided kit is a deck's whose kit is chosen when it is first built
    /// (<see cref="BridgeStructures.ChooseKit"/>). Versions 1 (no flags), 2 (no kit, chosen as an undecided one is),
    /// 3 (no ends, each then as the bank takes it; the ends it recorded in its flags are not read) and 4 (no undecided kit:
    /// every kit it stored is the crossing's, chosen) are read too. The rest of a crossing is derived
    /// from its banks on load.
    /// </summary>
    internal static byte[] SerializeRoadCrossings(IReadOnlyList<RoadCrossing> roadCrossings)
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
            writer.Write(crossing.Kit switch { null => 0, BridgeStructures.Kit.Undecided => UndecidedKit, BridgeStructures.Kit kit => (int)kit + 1 });
            foreach (StructureEnds.End? end in new[] { crossing.FromEnd, crossing.ToEnd })
            {
                StructureEnds.End e = end ?? default;
                writer.Write(end == null ? 0 : e.Road ? 2 : 1);
                writer.Write(e.Edge.x);
                writer.Write(e.Edge.y);
                writer.Write(e.Up.x);
                writer.Write(e.Up.y);
                writer.Write(e.Height);
                writer.Write(e.Steps);
            }
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
        if (data != null && data.Length >= 4 && BitConverter.ToInt32(data, 0) > BridgeZonesFormat)
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
        else if (version == BridgeZonesFormat)
        {
            layout = reader.ReadInt32();
        }
        else
            throw new InvalidDataException($"unknown bridge zone data version {version}");
        List<Vector2s> zones = ReadZones(reader, "bridge zone");
        if (layout != BridgeLayout.LayoutVersion)
        {
            Log.LogInfo($"[BRIDGES] the saved bridges were laid out by layout {layout}; this build is layout {BridgeLayout.LayoutVersion}. " +
                        $"Their {zones.Count} zone(s) keep the pieces they have; only zones without pieces get the new layout.");
        }
        bridgeZones.UnionWith(zones);
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
        writer.Write(BridgeZonesFormat);
        writer.Write(BridgeLayout.LayoutVersion);
        WriteZones(writer, zones);
        return ms.ToArray();
    }

    /// <summary>The bridge-zones record's format: 2 adds the layout id; 1 is read too.</summary>
    private const int BridgeZonesFormat = 2;

    /// <summary>The cleared-zones record's formats: 1, and 2 when some zone is owed only the roads appended since.</summary>
    private const int ClearedZonesFormat = 1, ClearedZonesOwedFormat = 2;

    /// <summary>The longest prefab name a saved piece may carry (a damaged length is refused).</summary>
    internal const int MaxSavedPrefabName = 100;

    /// <summary>The most zones any saved zone list may hold (a damaged count is refused, not allocated).</summary>
    internal const int MaxSavedZones = 1_000_000;

    /// <summary>A zone list: [count] then [x][y] per zone, each a 32-bit int (the casts keep the bytes as they were before
    /// Valheim 1.0 made a zone id a pair of shorts).</summary>
    private static void WriteZones(BinaryWriter writer, IReadOnlyCollection<Vector2s> zones)
    {
        writer.Write(zones.Count);
        foreach (Vector2s zone in zones)
        {
            writer.Write((int)zone.x);
            writer.Write((int)zone.y);
        }
    }

    /// <summary>A zone list as <see cref="WriteZones"/> writes it; a count past <see cref="MaxSavedZones"/> throws.</summary>
    private static List<Vector2s> ReadZones(BinaryReader reader, string what)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > MaxSavedZones) throw new InvalidDataException($"invalid {what} count {count}");
        var zones = new List<Vector2s>(count);
        for (int i = 0; i < count; i++)
            zones.Add(new Vector2s((short)reader.ReadInt32(), (short)reader.ReadInt32()));
        return zones;
    }

    /// <summary>The crossings record's format: 2 carries each crossing's flags (a valley viaduct's
    /// <see cref="RoadCrossing.ValleyJump"/>, which the load puts its keep-out back from), 3 its kit, 4 its ends, 5 a kit
    /// not chosen yet (<see cref="UndecidedKit"/>). 1 is read too.</summary>
    internal const int CrossingsFormat = 5;

    /// <summary>The crossings record's number for <see cref="BridgeStructures.Kit.Undecided"/> (format 5 on).</summary>
    internal const int UndecidedKit = -1;

    /// <summary>The crossings record's number for the retired plain wooden kit (formats 3 and 4); it names no kit since.</summary>
    internal const int RetiredWoodKit = 1;

    /// <summary>The longest crossing a network holds, bank to bank: a bridge's jump (MaxBridgeCrossingCells) with a
    /// swamp bridge's banks walked out over the shelf at both ends (SwampBridgeDryReach).</summary>
    internal const float MaxCrossingSpan = RoadConstants.MaxBridgeSpan + 2f * RoadConstants.SwampBridgeDryReach;

    /// <summary>
    /// Why a saved crossing is not one this build ever makes, or null when it is: every number finite, its points in
    /// the world, its span within <see cref="MaxCrossingSpan"/> (a valley viaduct's within DvergrViaduct's), a kind and
    /// style that exist and go together, and no flag but the valley's (a record before format 4 may carry its ends in
    /// the flags too, which are not read). Checked before anything is planned from it: a
    /// bridge's layout is sized by its span.
    /// </summary>
    private static string? CrossingFault(Vector2 fromBank, Vector2 toBank, float riverbed, Vector2 fairwayCenter,
        float fairwayWidth, CrossingKind kind, FordStyle style, int flags, int version)
    {
        static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
        static bool InWorld(Vector2 p) => Finite(p.x) && Finite(p.y) && p.magnitude <= RoadConstants.WorldEdge;
        if (!InWorld(fromBank) || !InWorld(toBank) || !InWorld(fairwayCenter)) return "a point outside the world";
        if (!Finite(riverbed) || !Finite(fairwayWidth)) return "a height or width that is not a number";
        float span = Vector2.Distance(fromBank, toBank);
        if (span <= 0f || span > MaxCrossingSpan) return $"a span of {span} m";
        if (fairwayWidth < 0f || fairwayWidth > MaxCrossingSpan) return $"a fairway of {fairwayWidth} m";
        if (!Enum.IsDefined(typeof(CrossingKind), kind) || !Enum.IsDefined(typeof(FordStyle), style)) return $"kind {(int)kind}, style {(int)style}";
        if (kind == CrossingKind.Bridge && style != FordStyle.None) return $"a bridge with ford style {style}";
        if ((flags & ~(version >= 4 ? 1 : 0x7FFF)) != 0) return $"flags {flags}";
        bool valley = (flags & 1) != 0;
        if (valley && (kind != CrossingKind.Bridge || span < DvergrViaduct.MinValleySpan - 1f || span > DvergrViaduct.MaxValleySpan + 1f))
            return $"a valley viaduct {kind} of {span} m";
        return null;
    }

    /// <summary>One end of a crossing as <see cref="SerializeRoadCrossings"/> wrote it, or null for none; a
    /// <paramref name="fault"/> where it is not one this build makes: every number finite, its way onto the crossing a unit
    /// direction, its edge within <see cref="EndReach"/> of the crossing's line between its banks, and no more stairs
    /// than a kit lays.</summary>
    private static StructureEnds.End? ReadEnd(BinaryReader reader, RoadCrossing crossing, out string? fault)
    {
        fault = null;
        int kind = reader.ReadInt32();
        Vector2 edge = new(reader.ReadSingle(), reader.ReadSingle()), up = new(reader.ReadSingle(), reader.ReadSingle());
        float height = reader.ReadSingle();
        int steps = reader.ReadInt32();
        if (kind == 0) return null;
        static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
        Vector2 line = crossing.ToBank - crossing.FromBank;
        float t = Mathf.Clamp01(Vector2.Dot(edge - crossing.FromBank, line) / Mathf.Max(line.sqrMagnitude, 1e-6f));
        if (kind > 2 || !Finite(edge.x) || !Finite(edge.y) || !Finite(up.x) || !Finite(up.y) || !Finite(height)
            || Mathf.Abs(up.magnitude - 1f) > 0.01f || steps < 0 || steps > BridgeLayout.MaxStairSteps
            || Vector2.Distance(edge, crossing.FromBank + line * t) > EndReach)
        { fault = FormattableString.Invariant($"an end {kind} at ({edge.x}, {edge.y}), {steps} steps"); return null; }
        return new StructureEnds.End(edge, up, height, steps, road: kind == 2);
    }

    /// <summary>How far a crossing's saved end's edge may stand off the line between its banks: a deck's ends stand on
    /// it, a viaduct's a module past a bank at most.</summary>
    private const float EndReach = 16f;

    /// <summary>The shore landings record's format.</summary>
    internal const int CoastLandingsFormat = 1;

    /// <summary>
    /// Serialize the shore landings.
    /// Format: [version=1][count] then per landing [x][y][z] floats, [island id] int, [flags] byte (bit 0 chosen, bit 1 reached).
    /// </summary>
    internal static byte[] SerializeCoastalLandings(IReadOnlyList<CoastalLandingPlace> landings)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(CoastLandingsFormat);
        writer.Write(landings.Count);
        foreach (CoastalLandingPlace l in landings)
        {
            writer.Write(l.Position.x);
            writer.Write(l.Position.y);
            writer.Write(l.Position.z);
            writer.Write(l.IslandId);
            writer.Write((byte)((l.Chosen ? 1 : 0) | (l.Reached ? 2 : 0)));
        }
        return ms.ToArray();
    }

    private static void ReadCoastalLandings(byte[]? data, List<CoastalLandingPlace> landings)
    {
        landings.Clear();
        if (data == null || data.Length == 0) return;
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);
        int version = reader.ReadInt32();
        if (version != CoastLandingsFormat) throw new InvalidDataException($"unknown shore landings version {version}");
        int count = reader.ReadInt32();
        if (count < 0 || count > 100000) throw new InvalidDataException($"invalid shore landing count {count}");
        var read = new List<CoastalLandingPlace>(count);
        for (int i = 0; i < count; i++)
        {
            var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            int island = reader.ReadInt32();
            byte flags = reader.ReadByte();
            if (float.IsNaN(position.x + position.y + position.z) || float.IsInfinity(position.x + position.y + position.z) || (flags & ~3) != 0)
                throw new InvalidDataException($"invalid shore landing at {position}");
            read.Add(new CoastalLandingPlace(position, island, (flags & 1) != 0, (flags & 2) != 0));
        }
        landings.AddRange(read);
    }

    /// <summary>
    /// The crossings of a record (<see cref="SerializeRoadCrossings"/>). A record before format 5 is read as it was laid: the
    /// retired plain wooden kit (stored 1, formats 3 and 4) names a deck whose kit is chosen when it is first built
    /// (<see cref="BridgeStructures.Kit.Undecided"/>), as does a deck it stored with no kit, which it laid as no viaduct; a
    /// deck of a record that stored no kind (formats 1 and 2) is a viaduct where it was under the ceiling it was laid under
    /// (<see cref="DvergrViaduct.FormerPierCeiling"/>), read on <paramref name="world"/>, else undecided; without a world
    /// its kind is left to be asked.
    /// </summary>
    internal static bool DeserializeRoadCrossings(byte[] data, List<RoadCrossing> roadCrossings, WorldGenerator? world = null)
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
                bool undecided = kit == UndecidedKit && version >= 5;
                if (version < 5 && kit == RetiredWoodKit) { kit = 0; undecided = true; }
                if (!undecided && (kit < 0 || (kit > 0 && !Enum.IsDefined(typeof(BridgeStructures.Kit), kit - 1))))
                { Log.LogWarning($"Invalid crossing kit: {kit}"); return false; }
                string? fault = CrossingFault(fromBank, toBank, riverbed, fairwayCenter, fairwayWidth, kind, style, flags, version);
                if (fault != null) { Log.LogWarning($"Invalid river crossing {i}: {fault}"); return false; }
                RoadCrossing crossing = RoadCrossing.Between(fromBank, toBank, riverbed, fairwayCenter, fairwayWidth, kind, style);
                crossing.ValleyJump = (flags & 1) != 0;
                if (undecided) crossing.Kit = BridgeStructures.Kit.Undecided;
                else if (kit > 0) crossing.Kit = (BridgeStructures.Kit)(kit - 1);
                if (version < 5 && crossing.Kit == null && BridgeStructures.HasDeck(crossing))
                {
                    if (version >= 3) crossing.Kit = BridgeStructures.Kit.Undecided;
                    else if (world != null)
                        crossing.Kit = DvergrViaduct.AppliedFormerly(crossing, world)
                            ? BridgeStructures.Kit.DvergrViaduct : BridgeStructures.Kit.Undecided;
                }
                if (version >= 4)
                {
                    StructureEnds.End? from = ReadEnd(reader, crossing, out string? endFault), to = endFault == null ? ReadEnd(reader, crossing, out endFault) : null;
                    if (endFault != null) { Log.LogWarning($"Invalid river crossing {i}: {endFault}"); return false; }
                    (crossing.FromEnd, crossing.ToEnd) = (from, to);
                }
                roadCrossings.Add(crossing);
            }
            if (ms.Position != ms.Length)
            {
                Log.LogWarning("River crossing data runs past its crossings");
                return false;
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
