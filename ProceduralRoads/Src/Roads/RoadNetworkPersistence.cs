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

    /// <summary>The network's off-level arrivals and custom-location identities.</summary>
    private static readonly int ReachedLocationsHash = "ProceduralRoads_ReachedPlaces".GetStableHashCode();

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

    /// <summary>Map icons of locations placed before their roads, kept off the map until a player comes near (<see cref="HiddenLocationIcons"/>).</summary>
    private static readonly int HiddenLocationIconsHash = "ProceduralRoads_HiddenSiteIcons".GetStableHashCode();

    /// <summary>The locations placed before their roads: their placings, which a load makes ready again (PlacedLocations).</summary>
    private static readonly int PlacedLocationsHash = "ProceduralRoads_PlacedSites".GetStableHashCode();

    /// <summary>The network's staircases: their layouts, clearances and ground (StairStructures).</summary>
    private static readonly int StairRunsHash = "ProceduralRoads_StairRuns".GetStableHashCode();

    /// <summary>The crossing sites whose first zone was built: their pieces and the ground they stand on (BuiltBridges).</summary>
    private static readonly int BuiltBridgesHash = "ProceduralRoads_BuiltBridges".GetStableHashCode();

    /// <summary>The staircases whose first zone was built: the pieces their kit laid and the ground they stand on (BuiltStairs).</summary>
    private static readonly int BuiltStairsHash = "ProceduralRoads_BuiltStairs".GetStableHashCode();

    /// <summary>The world's last-resort pitches, for the budget and spacing of the roads added to it (RoadPitches.Serialize).</summary>
    private static readonly int PitchesHash = "ProceduralRoads_Pitches".GetStableHashCode();

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
    /// (<see cref="NetworkGenerated"/>) or the world unloads (<see cref="Reset"/>): a generation that fails replaces nothing.
    /// Arrival identities alone may be replaced once their tracked state is rewritten.</summary>
    private static readonly Dictionary<int, (string what, byte[] bytes, Func<long>? revision, long? readRevision)> s_kept = new();

    /// <summary>Whether the saved staircases could not be read this session: none may be added (their record is kept as it was).</summary>
    internal static bool StairRecordKept => s_kept.ContainsKey(StairRunsHash);

    /// <summary>What of the saved network could not be read this session (kept as saved); empty when everything was read.</summary>
    internal static IEnumerable<string> KeptRecords => System.Linq.Enumerable.Select(s_kept.Values, k => k.what);

    /// <summary>
    /// The one rule for a saved record this build cannot read: it costs the world neither its roads nor the record.
    /// Its part is empty this session (<paramref name="empty"/>), its bytes are written back as they were on every save,
    /// and nothing new is saved under its key until an explicitly tracked state is rewritten (<paramref name="revision"/>).
    /// </summary>
    private static void ReadOrKeep(RecordSource source, int key, string what, Action<byte[]?> read, Action empty, Func<long>? revision = null)
    {
        byte[]? bytes = source(key);
        try { read(bytes); }
        catch (Exception e) when (!(e is OutOfMemoryException))
        {
            Log.LogError($"[LOAD] The saved {what} could not be read; the roads load without them this session and the record is kept as it is: {e.Message}");
            empty();
            if (bytes != null) s_kept[key] = (what, bytes, revision, revision?.Invoke());
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

    /// <summary>
    /// The one rule for changing a network a released build saved (1.4.x, format 1; read without conversion,
    /// <see cref="RoadSpatialGrid.LoadedFromReleasedFormat"/>). That build reads only format 1 and throws away a network it
    /// cannot read, generating another over the world. A released network therefore gets no full save, crossing rewrite,
    /// added road, built bridge or staircase piece record, or terrain writer stamp.
    /// Bookkeeping is still saved: <see cref="WriteStructureRecords"/> writes bridge zones, pending appends, staircase
    /// plans, hidden icons and placed locations; the clearing, generated-zone and applied-stamp records have their own save
    /// paths. Unread records are written back as they were (<see cref="WriteRecord"/>).
    /// </summary>
    internal static bool AllowsNetworkChanges => !RoadSpatialGrid.LoadedFromReleasedFormat;

    /// <summary>Set a record, or write back the one kept unread until its tracked state is rewritten (<see cref="ReadOrKeep"/>).</summary>
    private static void WriteRecord(ZDO zdo, int key, Func<byte[]> current)
    {
        if (s_kept.TryGetValue(key, out var kept) && (kept.revision == null || kept.revision() == kept.readRevision))
        {
            zdo.Set(key, kept.bytes);
            return;
        }
        zdo.Set(key, current());
        s_kept.Remove(key);
    }

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
    /// The one owner of the saved network (docs/OWNERS.md).
    /// </summary>
    public static void SaveGlobalRoadData(
        IReadOnlyList<(Vector2 position, string label)> roadStartPoints,
        IReadOnlyList<RoadCrossing> roadCrossings,
        IReadOnlyCollection<Vector2s> bridgeZones,
        IReadOnlyList<CoastalLandingPlace>? coastalLandings = null)
    {
        Log.LogDebug($"[SAVE] SaveGlobalRoadData called");
        if (!AllowsNetworkChanges)
        {
            Log.LogWarning("[SAVE] The road network was saved by a released build (format 1); it is not rewritten");
            return;
        }

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
        WriteRecord(metadataZdo, ReachedLocationsHash, SerializeReachedLocations);
        // Always written, so a network regenerated without fords does not
        // keep the crossings of an earlier one.
        WriteRecord(metadataZdo, RoadCrossingsHash, () => SerializeRoadCrossings(roadCrossings));
        WriteRecord(metadataZdo, PitchesHash, RoadPitches.Serialize);
        if (coastalLandings != null)
            WriteRecord(metadataZdo, CoastLandingsHash, () => SerializeCoastalLandings(coastalLandings));
        WriteStructureRecords(metadataZdo, bridgeZones);
    }

    private static void WriteStructureRecords(ZDO metadataZdo, IReadOnlyCollection<Vector2s> bridgeZones)
    {
        WriteRecord(metadataZdo, BridgeZonesHash, () => SerializeBridgeZones(bridgeZones));
        WriteRecord(metadataZdo, AppendBridgesHash, StructureAppendQueue.Serialize);
        WriteRecord(metadataZdo, StairRunsHash, StairStructures.Serialize);
        if (AllowsNetworkChanges)
        {
            WriteRecord(metadataZdo, BuiltBridgesHash, BuiltBridges.Serialize);
            WriteRecord(metadataZdo, BuiltStairsHash, BuiltStairs.Serialize);
        }
        WriteRecord(metadataZdo, HiddenLocationIconsHash, HiddenLocationIcons.Serialize);
        WriteRecord(metadataZdo, PlacedLocationsHash, PlacedLocations.Serialize);
    }

    /// <summary>
    /// Save only the zones that have their bridge pieces, and the crossings when
    /// given, on a network that was loaded rather than generated this session.
    /// A released build's network keeps its crossings as saved (<see cref="AllowsNetworkChanges"/>).
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
        if (roadCrossings != null && AllowsNetworkChanges)
            WriteRecord(metadataZdo, RoadCrossingsHash, () => SerializeRoadCrossings(roadCrossings));
        WriteStructureRecords(metadataZdo, bridgeZones);
    }

    /// <summary>
    /// Save the zones whose vegetation matches the network (VegetationClearing),
    /// with the network version they belong to, or write back the record kept unread.
    /// Format: [format=2][network version][count] then [x][y] per zone, 32-bit ints, then [count] and [x][y][appends cleared
    /// through] per zone owed just the roads appended since it was cleared (<see cref="VegetationClearing.ClearedThrough"/>;
    /// count 0 when none is).
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
            writer.Write(ClearedZonesFormat);
            writer.Write(networkVersion);
            WriteZones(writer, zones);
            writer.Write(through?.Count ?? 0);
            if (through != null)
                foreach (var zone in through)
                {
                    writer.Write((int)zone.Key.x);
                    writer.Write((int)zone.Key.y);
                    writer.Write(zone.Value);
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
        if (format != ClearedZonesFormat) throw new InvalidDataException($"unknown cleared zone data format {format}");
        int version = reader.ReadInt32();
        List<Vector2s> zones = ReadZones(reader, "cleared zone");
        var through = new Dictionary<Vector2s, int>();
        int owed = reader.ReadInt32();
        if (owed < 0 || owed > MaxSavedZones) throw new InvalidDataException($"invalid owed zone count {owed}");
        for (int i = 0; i < owed; i++)
            through[new Vector2s((short)reader.ReadInt32(), (short)reader.ReadInt32())] = reader.ReadInt32();
        if (ms.Position != ms.Length) throw new InvalidDataException("cleared zone data runs past its zones");
        VegetationClearing.Load(version, zones, RoadSpatialGrid.RoadNetworkVersion, through);
    }

    /// <summary>
    /// Try to load the entire road network from persisted ZDO.
    /// The one owner of the saved network (docs/OWNERS.md).
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
        return ReadNetwork(key => metadataZdo.GetByteArray(key, null), received: false,
            roadStartPoints, roadCrossings, bridgeZones, coastalLandings);
    }

    /// <summary>Where the network's records are read from, by key: the metadata ZDO on the server, the package the server
    /// sent on a client (<see cref="TryLoadReceived"/>). Null for a record the source does not hold.</summary>
    internal delegate byte[]? RecordSource(int key);

    /// <summary>The records a client is sent (RoadNetworkSync): the road points and their start points (labels), by key.</summary>
    internal static readonly (string name, int key)[] SentRecords =
    {
        ("ProceduralRoads_GlobalData", GlobalRoadDataHash),
        ("ProceduralRoads_StartPoints", RoadStartPointsHash),
    };

    /// <summary>A record of the saved network as the server would send it (RoadNetworkSync), read from where this peer holds
    /// it: the in-memory network serialized as a save writes it, never the possibly older ZDO.</summary>
    internal static byte[]? CurrentRecord(int key, IReadOnlyList<(Vector2 position, string label)> roadStartPoints) =>
        key == GlobalRoadDataHash ? RoadSpatialGrid.SerializeAllRoadPoints()
        : key == RoadStartPointsHash ? SerializeRoadStartPoints(roadStartPoints)
        : null;

    /// <summary>
    /// A client: the network the server sent (<paramref name="records"/>, by key), read by the same reader and the same
    /// checks as a saved one (<see cref="ReadNetwork"/>): a record this build refuses leaves no network. Nothing read here
    /// is ever written back: a client saves nothing (RoadLifecycleManager.OnPrepareSave).
    /// </summary>
    internal static bool TryLoadReceived(IReadOnlyDictionary<int, byte[]> records,
        List<(Vector2 position, string label)> roadStartPoints,
        List<RoadCrossing> roadCrossings,
        HashSet<Vector2s> bridgeZones,
        List<CoastalLandingPlace>? coastalLandings = null)
    {
        LastLoadUnreadable = false;
        s_kept.Clear();
        return ReadNetwork(key => records.TryGetValue(key, out byte[]? bytes) ? bytes : null, received: true,
            roadStartPoints, roadCrossings, bridgeZones, coastalLandings);
    }

    /// <summary>The one reader of a network's records, saved (the metadata ZDO) or received (a client's package): the road
    /// points first (format refusal: <see cref="RoadSpatialGrid.DeserializeAllRoadPoints"/>), then every other record
    /// through <see cref="ReadOrKeep"/>, the crossings by <see cref="CrossingRecordFault"/>. Half a network is no network.</summary>
    private static bool ReadNetwork(RecordSource source, bool received,
        List<(Vector2 position, string label)> roadStartPoints,
        List<RoadCrossing> roadCrossings,
        HashSet<Vector2s> bridgeZones,
        List<CoastalLandingPlace>? coastalLandings)
    {
        byte[]? data = source(GlobalRoadDataHash);
        if (data == null || data.Length == 0)
        {
            Log.LogDebug(received ? "[LOAD] The received network holds no road data" : "[LOAD] Metadata ZDO found but no global road data");
            if (received) LastLoadUnreadable = true;
            return false;
        }
        if (RoadSpatialGrid.DeserializeAllRoadPoints(data, received))
        {
            Log.LogDebug($"[LOAD] Successfully loaded: {RoadSpatialGrid.GridCellsWithRoads} cells, {RoadSpatialGrid.StoredPointRecords} stored records");

            try
            {
                ReadOrKeep(source, RoadStartPointsHash, "road start points", b => ReadStartPoints(b, roadStartPoints), roadStartPoints.Clear);
                ReadOrKeep(source, ReachedLocationsHash, "location arrivals", ReadReachedLocations, () => RoadNetworkGenerator.RestoreReachedLocations(),
                    () => RoadNetworkGenerator.ReachedLocationsRevision);
                ReadOrKeep(source, RoadCrossingsHash, "river crossings", b => ReadCrossings(b, roadCrossings), roadCrossings.Clear);
                // Unread, the roads added to it are judged against no pitch of its: as a network with none.
                ReadOrKeep(source, PitchesHash, "last-resort pitches", RoadPitches.Load, RoadPitches.ForgetStored);
                if (coastalLandings != null)
                    ReadOrKeep(source, CoastLandingsHash, "shore landings", b => ReadCoastalLandings(b, coastalLandings), coastalLandings.Clear);
                ReadOrKeep(source, BridgeZonesHash, "bridge zones", b => ReadBridgeZonesOrStartAfresh(b, bridgeZones), bridgeZones.Clear);
                ReadOrKeep(source, AppendBridgesHash, "bridge pieces owed by manual roads", StructureAppendQueue.Load, StructureAppendQueue.Reset);
                // Unread, the locations' icons show: the map as the game would have it once they are placed.
                ReadOrKeep(source, HiddenLocationIconsHash, "hidden location icons", HiddenLocationIcons.Load, HiddenLocationIcons.Reset);
                // Unread, the locations are not ready for the roads: as a network saved before they were.
                ReadOrKeep(source, PlacedLocationsHash, "locations placed before their roads", PlacedLocations.Restore, PlacedLocations.Reset);
                var read = System.Diagnostics.Stopwatch.StartNew();
                ReadOrKeep(source, StairRunsHash, "staircases", StairStructures.Load, StairStructures.Reset);
                ReadOrKeep(source, BuiltBridgesHash, "built bridges", BuiltBridges.Load, BuiltBridges.Reset);
                // After the staircases, whose reset forgets them.
                ReadOrKeep(source, BuiltStairsHash, "built staircases", BuiltStairs.Load, BuiltStairs.Reset);
                Log.LogInfo($"[LOAD] saved structures: {PlacedLocations.Count} location(s) placed before their roads " +
                            $"({source(PlacedLocationsHash)?.Length ?? 0} bytes), {StairStructures.Runs.Count} staircase(s) " +
                            $"({source(StairRunsHash)?.Length ?? 0} bytes), {BuiltBridges.Sites.Count} built bridge(s) " +
                            $"({source(BuiltBridgesHash)?.Length ?? 0} bytes), {BuiltStairs.Sites.Count} built staircase(s) " +
                            $"({source(BuiltStairsHash)?.Length ?? 0} bytes), read in {read.Elapsed.TotalMilliseconds:F0} ms");
                // NetworkCutoff reads it when the network is used, and unread excludes every generated zone; kept, a
                // save cannot put that fallback over the zones the record says are the network's.
                ReadOrKeep(source, ZonesWithNetworkHash, "zones generated with the network", CheckZonesWithNetwork, () => { });
                // Unread, which zones were cleared is not known, not none: none is cleared again on a guess.
                ReadOrKeep(source, ClearedZonesHash, "zones cleared of vegetation", ReadClearedZones, VegetationClearing.HistoryUnread);
            }
            catch (Exception ex)
            {
                // Half a network is not a network: forget what was read, and
                // say the save could not be read rather than that it is empty.
                Log.LogError($"[LOAD] The {(received ? "received" : "saved")} road network could not be read: {ex}");
                RoadSpatialGrid.Clear();
                roadStartPoints.Clear();
                roadCrossings.Clear();
                RoadPitches.ForgetStored();
                coastalLandings?.Clear();
                bridgeZones.Clear();
                StructureAppendQueue.Reset();
                HiddenLocationIcons.Reset();
                PlacedLocations.Reset();
                StairStructures.Reset();
                BuiltBridges.Reset();
                BuiltStairs.Reset();
                RoadNetworkGenerator.RestoreReachedLocations();
                LastLoadUnreadable = true;
                return false;
            }
            return true;
        }

        LastLoadUnreadable = true;
        if (received)
        {
            Log.LogError($"[LOAD] The server sent a road network ({data.Length} bytes) this build cannot read " +
                         $"({RoadSpatialGrid.LastReadRefusal}); this client has no roads to read until the server sends one it can.");
            return false;
        }
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
        if (!DeserializeRoadCrossings(data, roadCrossings)) throw new InvalidDataException("unreadable river crossings");
    }

    /// <summary>
    /// Serialize river crossings to binary format.
    /// Format (<see cref="EncodeRoadCrossings"/>): [version=6][count] then per crossing [fromX][fromY][toX][toY][riverbed][fairwayX][fairwayY][fairwayWidth][kind][style][flags][kit+1, 0 none, -1 undecided]
    /// and each end, from then to (<see cref="RoadCrossing.FromEnd"/>, <see cref="RoadCrossing.ToEnd"/>): [is][edgeX][edgeY][upX][upY][height][steps],
    /// is 0 for none (the rest then 0), 1 a structure's end, 2 an end where the crossing is road.
    /// flags: bit 0, a Mistlands viaduct the search bought over a dry valley (<see cref="RoadCrossing.ValleyJump"/>),
    /// not a river's bridge; bit 1, standing water (RoadCrossing.Shallow). An undecided kit is a deck's whose kit is chosen when it is first built
    /// (<see cref="BridgeStructures.ChooseKit"/>). Any other version is refused. The rest of a crossing is derived
    /// from its banks on load.
    /// </summary>
    internal static byte[] SerializeRoadCrossings(IReadOnlyList<RoadCrossing> roadCrossings)
    {
        // The save writes only crossings the load reads back, by the one rule both ask (CrossingRecordFault): a crossing
        // this build would refuse on reading is a generation fault, named here, never a record that unreads the rest.
        var kept = new List<RoadCrossing>(roadCrossings.Count);
        for (int i = 0; i < roadCrossings.Count; i++)
        {
            string? fault = CrossingRecordFault(roadCrossings[i]);
            if (fault == null) kept.Add(roadCrossings[i]);
            else Log.LogError(FormattableString.Invariant(
                $"[SAVE] River crossing {i} ({roadCrossings[i].FromBank.x:F2}, {roadCrossings[i].FromBank.y:F2}) -> ({roadCrossings[i].ToBank.x:F2}, {roadCrossings[i].ToBank.y:F2}) is not saved: {fault}"));
        }
        return EncodeRoadCrossings(kept);
    }

    /// <summary>The crossings record exactly as given (<see cref="SerializeRoadCrossings"/> without its check): the
    /// encoding alone, which tests use to write a record the reader must refuse.</summary>
    internal static byte[] EncodeRoadCrossings(IReadOnlyList<RoadCrossing> roadCrossings)
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
            writer.Write((crossing.ValleyJump ? 1 : 0) | (crossing.Shallow ? 2 : 0));
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

    /// <summary>Read the spawned-zone record. Only the current format is read. A record written by an older bridge layout keeps its zones marked
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
        if (version != BridgeZonesFormat)
            throw new InvalidDataException($"unknown bridge zone data version {version}");
        int layout = reader.ReadInt32();
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
    /// int. Valheim 1.0 made a zone id a pair of shorts; the casts here
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

    /// <summary>The bridge-zones record's format (the layout id leads the zones); no other is read.</summary>
    private const int BridgeZonesFormat = 2;

    /// <summary>The cleared-zones record's one format (its owed block always written); no other is read.</summary>
    private const int ClearedZonesFormat = 2;

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

    /// <summary>The crossings record's format: each crossing carries its flags (a valley viaduct's
    /// <see cref="RoadCrossing.ValleyJump"/>, which the load puts its keep-out back from), its kit (one not chosen yet is
    /// <see cref="UndecidedKit"/>) and its ends. No other format is read.</summary>
    internal const int CrossingsFormat = 6;

    /// <summary>The crossings record's number for <see cref="BridgeStructures.Kit.Undecided"/>.</summary>
    internal const int UndecidedKit = -1;

    /// <summary>The longest crossing a network holds, bank to bank: a bridge's jump (MaxBridgeCrossingCells) with a
    /// swamp bridge's banks walked out over the shelf at both ends (RoadRules.SwampShelfReach).</summary>
    internal const float MaxCrossingSpan = RoadConstants.MaxBridgeSpan + 2f * RoadRules.SwampShelfReach;

    /// <summary>
    /// Why a crossing is not one the record holds, or null when it is: the one rule for a valid crossing record, asked by
    /// the save (<see cref="SerializeRoadCrossings"/> writes no crossing it refuses) and by the load
    /// (<see cref="DeserializeRoadCrossings"/> refuses a record holding one), so the two never disagree. Every number
    /// finite, its points in the world (<see cref="RoadConstants.InWorld"/>, the rule its built bridge is read and built
    /// by), banks a crossing apart (<see cref="RoadCrossing.HasSpan"/>) and within <see cref="MaxCrossingSpan"/> (a valley
    /// viaduct's within DvergrViaduct's), a kind, style and kit that exist and go together, and each end
    /// (<see cref="EndFault"/>). Checked before anything is planned from it: a bridge's layout is sized by its span.
    /// </summary>
    internal static string? CrossingRecordFault(RoadCrossing crossing)
    {
        static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
        Vector2 fromBank = crossing.FromBank, toBank = crossing.ToBank;
        CrossingKind kind = crossing.Kind; FordStyle style = crossing.Style;
        if (!RoadConstants.InWorld(fromBank) || !RoadConstants.InWorld(toBank) || !RoadConstants.InWorld(crossing.FairwayCenter)) return "a point outside the world";
        if (!Finite(crossing.RiverbedHeight) || !Finite(crossing.FairwayWidth)) return "a height or width that is not a number";
        float span = Vector2.Distance(fromBank, toBank);
        if (!RoadCrossing.HasSpan(span) || span > MaxCrossingSpan) return FormattableString.Invariant($"a span of {span} m");
        if (crossing.FairwayWidth < 0f || crossing.FairwayWidth > MaxCrossingSpan) return FormattableString.Invariant($"a fairway of {crossing.FairwayWidth} m");
        if (!Enum.IsDefined(typeof(CrossingKind), kind) || !Enum.IsDefined(typeof(FordStyle), style)) return $"kind {(int)kind}, style {(int)style}";
        if (kind == CrossingKind.Bridge && style != FordStyle.None) return $"a bridge with ford style {style}";
        if (crossing.Kit is BridgeStructures.Kit kit && !Enum.IsDefined(typeof(BridgeStructures.Kit), kit)) return $"kit {(int)kit}";
        if (crossing.ValleyJump && (kind != CrossingKind.Bridge || span < DvergrViaduct.MinValleySpan - 1f || span > DvergrViaduct.MaxValleySpan + 1f))
            return FormattableString.Invariant($"a valley viaduct {kind} of {span} m");
        return EndFault(crossing.FromEnd, crossing) ?? EndFault(crossing.ToEnd, crossing);
    }

    /// <summary>Why one end of a crossing is not one this build makes, or null (also for none): every number finite, its
    /// edge in the world (<see cref="RoadConstants.InWorld"/>), its way onto the crossing a unit direction, its edge within
    /// <see cref="EndReach"/> of the crossing's line between its banks, and no more stairs than a kit lays.</summary>
    private static string? EndFault(StructureEnds.End? end, RoadCrossing crossing)
    {
        if (end is not StructureEnds.End e) return null;
        static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
        Vector2 line = crossing.ToBank - crossing.FromBank;
        float t = Mathf.Clamp01(Vector2.Dot(e.Edge - crossing.FromBank, line) / Mathf.Max(line.sqrMagnitude, 1e-6f));
        if (!RoadConstants.InWorld(e.Edge) || !Finite(e.Up.x) || !Finite(e.Up.y) || !Finite(e.Height)
            || Mathf.Abs(e.Up.magnitude - 1f) > 0.01f || e.Steps < 0 || e.Steps > BridgeLayout.MaxStairSteps
            || Vector2.Distance(e.Edge, crossing.FromBank + line * t) > EndReach)
            return FormattableString.Invariant($"an end {(e.Road ? 2 : 1)} at ({e.Edge.x}, {e.Edge.y}), {e.Steps} steps");
        return null;
    }

    /// <summary>One end of a crossing as <see cref="EncodeRoadCrossings"/> wrote it, or null for none; a
    /// <paramref name="fault"/> where its tag is not one the record writes (0 none, 1 a structure's end, 2 road). Whether
    /// the end is one this build makes is <see cref="CrossingRecordFault"/>'s.</summary>
    private static StructureEnds.End? ReadEnd(BinaryReader reader, out string? fault)
    {
        fault = null;
        int kind = reader.ReadInt32();
        Vector2 edge = new(reader.ReadSingle(), reader.ReadSingle()), up = new(reader.ReadSingle(), reader.ReadSingle());
        float height = reader.ReadSingle();
        int steps = reader.ReadInt32();
        if (kind == 0) return null;
        if (kind < 0 || kind > 2) { fault = $"an end tagged {kind}"; return null; }
        return new StructureEnds.End(edge, up, height, steps, road: kind == 2);
    }

    /// <summary>How far a crossing's saved end's edge may stand off the line between its banks: a deck's ends stand on
    /// it, a viaduct's a module past a bank at most.</summary>
    private const float EndReach = 16f;

    /// <summary>Arrival metadata: [count] then [centre x][centre z] floats and [off level][custom] bools. Master wrote no
    /// such record; only this layout is read. Road geometry and its format are unchanged.</summary>
    private static byte[] SerializeReachedLocations()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        var locations = RoadNetworkGenerator.ReachedLocations();
        writer.Write(locations.Length);
        foreach (var location in locations)
        {
            writer.Write(location.centre.x); writer.Write(location.centre.y);
            writer.Write(location.offLevel); writer.Write(location.custom);
        }
        return ms.ToArray();
    }

    private static void ReadReachedLocations(byte[]? data)
    {
        if (data == null) { RoadNetworkGenerator.RestoreReachedLocations(); return; }
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);
        int count = reader.ReadInt32();
        if (count < 0 || (long)count * (sizeof(float) * 2 + sizeof(bool) * 2) != ms.Length - ms.Position)
            throw new InvalidDataException("invalid location arrival count");
        var locations = new List<(Vector2 centre, bool offLevel, bool custom)>();
        var seen = new HashSet<(float, float)>();
        for (int i = 0; i < count; i++)
        {
            var centre = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            bool offLevel = reader.ReadBoolean(), custom = reader.ReadBoolean();
            if (!RoadConstants.InWorld(centre) || !seen.Add(RoadNetworkGenerator.LocationKey(centre)) || !(offLevel || custom))
                throw new InvalidDataException("invalid location arrival identity");
            locations.Add((centre, offLevel, custom));
        }
        RoadNetworkGenerator.RestoreReachedLocations(locations);
    }

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
        int dropped = 0;
        for (int i = 0; i < count; i++)
        {
            var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            int island = reader.ReadInt32();
            byte flags = reader.ReadByte();
            if ((flags & ~3) != 0)
                throw new InvalidDataException($"invalid shore landing at {position}");
            if (!RoadConstants.InWorld(new Vector2(position.x, position.z)) || !ManualRoadDraft.IsFinite(position.y))
            {
                dropped++;
                continue;
            }
            read.Add(new CoastalLandingPlace(position, island, (flags & 1) != 0, (flags & 2) != 0));
        }
        if (dropped > 0) Log.LogWarning($"[LOAD] Dropped {dropped} saved shore landings outside the world");
        landings.AddRange(read);
    }

    /// <summary>
    /// The crossings of a record (<see cref="SerializeRoadCrossings"/>); a record of any other format is refused.
    /// </summary>
    internal static bool DeserializeRoadCrossings(byte[] data, List<RoadCrossing> roadCrossings)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            int version = reader.ReadInt32();
            if (version != CrossingsFormat)
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
                int flags = reader.ReadInt32();
                int kit = reader.ReadInt32();
                bool undecided = kit == UndecidedKit;
                if (!undecided && kit < 0) { Log.LogWarning($"Invalid crossing kit: {kit}"); return false; }
                if ((flags & ~3) != 0) { Log.LogWarning($"Invalid river crossing {i}: flags {flags}"); return false; }
                RoadCrossing crossing = RoadCrossing.Between(fromBank, toBank, riverbed, fairwayCenter, fairwayWidth, kind, style);
                crossing.ValleyJump = (flags & 1) != 0;
                crossing.Shallow = (flags & 2) != 0;
                if (undecided) crossing.Kit = BridgeStructures.Kit.Undecided;
                else if (kit > 0) crossing.Kit = (BridgeStructures.Kit)(kit - 1);
                StructureEnds.End? from = ReadEnd(reader, out string? endFault), to = endFault == null ? ReadEnd(reader, out endFault) : null;
                if (endFault != null) { Log.LogWarning($"Invalid river crossing {i}: {endFault}"); return false; }
                (crossing.FromEnd, crossing.ToEnd) = (from, to);
                // The save's own rule (CrossingRecordFault): a record holding a crossing it would not write is not one it wrote.
                string? fault = CrossingRecordFault(crossing);
                if (fault != null) { Log.LogWarning($"Invalid river crossing {i}: {fault}"); return false; }
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
