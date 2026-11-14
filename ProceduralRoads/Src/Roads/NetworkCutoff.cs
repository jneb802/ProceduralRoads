using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The one answer to "may the mod write into this zone on its own?".
///
/// Zones players explored before this network reached them (every zone of a
/// 1.4.x world, zones generated without the mod, the explored part of a world
/// whose network is generated now) are left exactly as they are by every
/// automatic writer: no terrain, paint, clearing or bridge pieces.
///
/// The record is the set of zones generated with this network in place, saved
/// with its version. At load, a generated zone outside that record is excluded;
/// no record, or one for another network, excludes every generated zone.
///
/// A manual road (road_mark, road_path, road_connect) may write them; the
/// explored zones it writes become the network's (see Admit).
///
/// A new network may include the explored land instead
/// (ExistingWorldRoads.IncludeExplored, see IncludeExplored: the first load's
/// answer, or road_generate and road_regen_island told so): then only the
/// explored zones players built in or dug are left as they are.
///
/// The set is captured once, as a network's generation begins (BeginNewNetwork,
/// before it chooses its destinations and places any of them), and the network
/// is published with that very set (UseNewNetwork). An automatic network plans
/// round the same set its writers leave alone (Avoids): its destinations, its
/// search and every stage that moves its line after.
/// </summary>
public static class NetworkCutoff
{
    private const int RecordFormat = 1;

    /// <summary>
    /// The world (its ZoneSystem) everything below belongs to. A fact about
    /// one world is never applied to another, whatever order the game tears
    /// one down and brings the next up in.
    /// </summary>
    private static object? s_world;

    /// <summary>The zones the save said were generated when this world was loaded; null until known.</summary>
    private static HashSet<Vector2s>? s_generatedAtLoad;

    /// <summary>The zones automatic writers leave alone, for the network in memory.</summary>
    private static HashSet<Vector2s> s_excluded = new();

    /// <summary>Whether this world has had a network in place this session.</summary>
    private static bool s_hadNetwork;

    /// <summary>
    /// Set when the next new network is to include the explored land (ExistingWorldRoads.IncludeExplored): the explored
    /// zones players built in or dug, the only ones it leaves as they are. Null for every other network.
    /// </summary>
    private static HashSet<Vector2s>? s_includeExploredKeeping;

    /// <summary>Whether the network being generated took <see cref="s_includeExploredKeeping"/> (what it is told as it is published).</summary>
    private static bool s_overExplored;

    /// <summary>
    /// What the road being planned goes round (<see cref="Avoids"/>). Replaced whole, never changed in place: the
    /// search's workers read it while it is set.
    /// </summary>
    private sealed class Planning
    {
        internal static readonly Planning None = new(new HashSet<Vector2s>(), 0f, false);
        /// <summary>The zones it goes round.</summary>
        internal readonly HashSet<Vector2s> Zones;
        /// <summary>How far, in metres, a road's centreline keeps from them: the writer's reach (RoadSpatialGrid.ZoneReach), so
        /// its levelling and paint stay out too; 0 for nothing to go round.</summary>
        internal readonly float Margin;
        /// <summary>Whether the zones a road starts and ends in are exempt: a manual road's own marks
        /// (<see cref="AvoidWhilePlanning"/>). An automatic network's are not: it chooses no destination in them.</summary>
        internal readonly bool EndsExempt;
        internal Planning(HashSet<Vector2s> zones, float margin, bool endsExempt) { Zones = zones; Margin = margin; EndsExempt = endsExempt; }
    }

    private static Planning s_planning = Planning.None;

    /// <summary>
    /// How far, in metres, a road fades in from the edge of a zone left as it is. At that edge the
    /// road changes nothing; this far from it the road is laid in full.
    /// </summary>
    public const float SeamFadeDistance = 16f;

    private static bool Current => s_world != null && ReferenceEquals(s_world, ZoneSystem.instance);

    /// <summary>Whether automatic writers must leave this zone exactly as it is. The one owner of which zones the mod may write (docs/OWNERS.md).</summary>
    public static bool Excludes(Vector2s zone) => Current && s_excluded.Contains(zone);

    /// <summary>How many zones are excluded (logs and tests).</summary>
    public static int ExcludedCount => Current ? s_excluded.Count : 0;

    /// <summary>
    /// Whether the road being planned goes round this zone rather than through it. An automatic network's: every zone
    /// its writers leave alone (<see cref="Excludes"/>, captured at <see cref="BeginNewNetwork"/>). A manual road's: the
    /// zones players changed that it was not asked into (<see cref="AvoidWhilePlanning"/>). Safe from the search's workers.
    /// The one owner of which zones the mod may write (docs/OWNERS.md).
    /// </summary>
    public static bool Avoids(Vector2s zone)
    {
        HashSet<Vector2s> avoided = s_planning.Zones;
        return avoided.Count > 0 && avoided.Contains(zone);
    }

    /// <summary><see cref="Avoids(Vector2s)"/> for the zone a point is in: a destination there is not chosen.</summary>
    public static bool Avoids(Vector3 point) => Avoids(ZoneSystem.GetZone(point));

    /// <summary>Whether the network being planned goes round any zone (<see cref="Avoids"/>).</summary>
    public static bool AvoidsAny => s_planning.Zones.Count > 0;

    /// <summary>
    /// The zones of <paramref name="written"/> (every zone a road's whole footprint writes, ManualRoads.ZonesReached) that the
    /// road being planned goes round (<see cref="Avoids"/>), in a stable order: none, or the road is not stored. The one final
    /// check of a road's footprint against protection, before anything of it is stored, for a generated road
    /// and a manual one, a road_connect's and an approach trial alike (RoadNetworkGenerator.WritesProtected, in the one list of
    /// refusals RoadNetworkGenerator.RoadRefusal), with no exemption: a manual
    /// road's own marks' zones are not among those it goes round. The one owner of which zones the mod may write (docs/OWNERS.md).
    /// </summary>
    public static List<Vector2s> Protected(IEnumerable<Vector2s> written)
    {
        HashSet<Vector2s> avoided = s_planning.Zones;
        var protectedZones = new List<Vector2s>();
        if (avoided.Count == 0) return protectedZones;
        foreach (Vector2s zone in written)
            if (avoided.Contains(zone)) protectedZones.Add(zone);
        protectedZones.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
        return protectedZones;
    }

    /// <summary>Every zone a box from <paramref name="min"/> to <paramref name="max"/> (x, z) touches, its edges included, into
    /// <paramref name="into"/>: the zones a point's reach, a piece's body or a rock's bounds are in.</summary>
    internal static void ZonesOver(Vector2 min, Vector2 max, ISet<Vector2s> into)
    {
        Vector2s low = ZoneSystem.GetZone(new Vector3(min.x, 0f, min.y));
        Vector2s high = ZoneSystem.GetZone(new Vector3(max.x, 0f, max.y));
        for (int x = low.x; x <= high.x; x++)
            for (int y = low.y; y <= high.y; y++)
                into.Add(new Vector2s((short)x, (short)y));
    }

    /// <summary>Whether automatic writers leave any zone the box from <paramref name="min"/> to <paramref name="max"/> (x, z)
    /// touches as it is (<see cref="Excludes"/>): an object whose body reaches such a zone is left whole.</summary>
    public static bool ExcludesAny(Vector2 min, Vector2 max)
    {
        if (!Current || s_excluded.Count == 0) return false;
        var zones = new HashSet<Vector2s>();
        ZonesOver(min, max, zones);
        foreach (Vector2s zone in zones)
            if (s_excluded.Contains(zone)) return true;
        return false;
    }

    /// <summary>
    /// Whether a stretch of road from <paramref name="a"/> to <paramref name="b"/> enters a zone the road being planned
    /// goes round (<see cref="Avoids"/>), sampled every half zone-cell along it so a long jump cannot fly over one. For a
    /// manual road the zones it starts and ends in are exempt (<see cref="AvoidWhilePlanning"/>); an automatic network
    /// has no exemption. The one rule the search, its jumps (bridges, fords, viaducts) and every stage that moves the
    /// line after it (straightening, sway, corridor snap) share.
    /// </summary>
    public static bool AvoidsSegment(Vector2 a, Vector2 b, Vector2? start, Vector2? end)
    {
        Planning planning = s_planning;
        HashSet<Vector2s> avoided = planning.Zones;
        if (avoided.Count == 0) return false;
        float margin = planning.Margin;
        Vector2s? startZone = planning.EndsExempt && start.HasValue ? ZoneOf(start.Value) : null;
        Vector2s? endZone = planning.EndsExempt && end.HasValue ? ZoneOf(end.Value) : null;
        bool Kept(Vector2 p)
        {
            Vector2s zone = ZoneOf(p);
            return avoided.Contains(zone) && zone != startZone && zone != endZone;
        }
        int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / 4f));
        for (int i = 0; i <= steps; i++)
        {
            Vector2 p = Vector2.Lerp(a, b, i / (float)steps);
            if (Kept(p)) return true;
            // The margin is shorter than a zone: the corners of the square round the point find every zone within it.
            if (margin > 0f && (Kept(p + new Vector2(margin, margin)) || Kept(p + new Vector2(-margin, margin)) ||
                                Kept(p + new Vector2(margin, -margin)) || Kept(p + new Vector2(-margin, -margin))))
                return true;
        }
        return false;
    }

    /// <summary>
    /// For one manual road's planning (main thread, no generation running): its search and straightening go round
    /// <paramref name="zones"/>, keeping <paramref name="margin"/> metres from them so the road's levelling stays out
    /// too, except in the zones its own ends are in. Disposing restores what was there.
    /// </summary>
    public static IDisposable AvoidWhilePlanning(HashSet<Vector2s> zones, float margin)
    {
        Planning restore = s_planning;
        s_planning = new Planning(new HashSet<Vector2s>(zones), margin, endsExempt: true);
        return new Restore(() => s_planning = restore);
    }

    private sealed class Restore : IDisposable
    {
        private Action? m_undo;
        internal Restore(Action undo) => m_undo = undo;
        public void Dispose() { m_undo?.Invoke(); m_undo = null; }
    }

    private static Vector2s ZoneOf(Vector2 p) => ZoneSystem.GetZone(new Vector3(p.x, 0f, p.y));

    /// <summary>How many zones players had explored when this world was loaded (0 for a fresh world).</summary>
    public static int ExploredAtLoadCount => Current && s_generatedAtLoad != null ? s_generatedAtLoad.Count : 0;

    /// <summary>
    /// The next new network includes the explored land: every zone explored before it (<see cref="Explored"/>) is written
    /// like a new one, except <paramref name="keep"/> (the zones players built in or dug), which are left exactly as they
    /// are and which its roads go round. For the first load's answer and for road_generate and road_regen_island alike;
    /// taken by the next <see cref="BeginNewNetwork"/>, and put back with <see cref="PutBack"/>. Null: the next network
    /// does not (an earlier answer that started no network is not carried to another).
    /// </summary>
    public static void IncludeExplored(IEnumerable<Vector2s>? keep)
    {
        Adopt();
        if (keep == null) { s_includeExploredKeeping = null; return; }
        var kept = new HashSet<Vector2s>(keep);
        kept.IntersectWith(Explored);
        s_includeExploredKeeping = kept;
    }

    /// <summary>
    /// The explored land: the zones a new network leaves as they are unless it includes the explored land. Every zone an
    /// earlier session generated; once this world has had a network, every zone generated so far this session too, where
    /// players may have built under it. A world's first network takes the zones generated this session before it
    /// (round the login point on a fresh world) as its own.
    /// </summary>
    internal static HashSet<Vector2s> Explored
    {
        get
        {
            var explored = new HashSet<Vector2s>(GeneratedAtLoad);
            if (Current && s_hadNetwork && ZoneSystem.instance != null) explored.UnionWith(ZoneSystem.instance.m_generatedZones);
            return explored;
        }
    }

    /// <summary>Whether the world's generated zones have been read for this world.</summary>
    internal static bool HasSnapshot => Current && s_generatedAtLoad != null;

    private static HashSet<Vector2s> GeneratedAtLoad =>
        Current && s_generatedAtLoad != null ? s_generatedAtLoad : new HashSet<Vector2s>();

    private static void Adopt()
    {
        if (Current) return;
        s_world = ZoneSystem.instance;
        s_generatedAtLoad = null;
        s_excluded = new HashSet<Vector2s>();
        StructurePlans.ForgetBuildableStaircases();
        s_hadNetwork = false;
        s_includeExploredKeeping = null;
        s_overExplored = false;
        s_planning = Planning.None;
    }

    /// <summary>
    /// The world's own data has been read (ZNet.ServerLoadWorld has returned):
    /// remember which zones it says were already generated. Nothing of this
    /// session has generated a zone yet, so this is exactly what earlier
    /// sessions left.
    /// </summary>
    public static void SnapshotWorld(IEnumerable<Vector2s>? generated)
    {
        Adopt();
        s_generatedAtLoad = generated == null ? new HashSet<Vector2s>() : new HashSet<Vector2s>(generated);
    }

    /// <summary>A last resort for a host path that skipped the world load: the zones generated by now.</summary>
    internal static void SnapshotIfMissing(IEnumerable<Vector2s>? generated)
    {
        if (!HasSnapshot) SnapshotWorld(generated);
    }

    /// <summary>
    /// A network was loaded from the save. Zones generated before this load
    /// are excluded unless the record says they were generated with this
    /// very network in place.
    /// </summary>
    public static void UseLoadedNetwork(int networkVersion, byte[]? record)
    {
        var excluded = new HashSet<Vector2s>(GeneratedAtLoad);
        if (TryRead(record, out int recordVersion, out HashSet<Vector2s> withNetwork) &&
            recordVersion == networkVersion && networkVersion != 0)
            excluded.ExceptWith(withNetwork);
        Adopt();
        s_excluded = excluded;
        StructurePlans.ForgetBuildableStaircases();
        s_hadNetwork = true;
        Log($"loaded network {networkVersion}: {s_excluded.Count} zone(s) generated before it are left as they are");
    }

    /// <summary>What the cutoff is for the network in memory (<see cref="Keep"/>), to put back with it (<see cref="PutBack"/>).</summary>
    internal sealed class State
    {
        internal object? World;
        internal HashSet<Vector2s> Excluded = new();
        internal bool HadNetwork;
        internal HashSet<Vector2s>? IncludeExploredKeeping;
    }

    /// <summary>The cutoff as it stands: an operator's generation keeps it, to put back with the network it was to replace
    /// if it publishes none. Read back from the world, that network would leave alone only the zones explored before this
    /// session; it left alone those explored this session before it too.</summary>
    internal static State Keep() => new()
    {
        World = Current ? s_world : null,
        Excluded = Current ? new HashSet<Vector2s>(s_excluded) : new HashSet<Vector2s>(),
        HadNetwork = Current && s_hadNetwork,
        IncludeExploredKeeping = Current && s_includeExploredKeeping != null ? new HashSet<Vector2s>(s_includeExploredKeeping) : null,
    };

    /// <summary>Put back the cutoff <see cref="Keep"/> took, for the same world; nothing planned goes round any zone after.</summary>
    internal static void PutBack(State state)
    {
        if (state.World == null || !ReferenceEquals(state.World, s_world) || !Current) return;
        s_excluded = new HashSet<Vector2s>(state.Excluded);
        StructurePlans.ForgetBuildableStaircases();
        s_hadNetwork = state.HadNetwork;
        s_includeExploredKeeping = state.IncludeExploredKeeping == null ? null : new HashSet<Vector2s>(state.IncludeExploredKeeping);
        s_overExplored = false;
        s_planning = Planning.None;
    }

    /// <summary>
    /// The network <see cref="BeginNewNetwork"/> began was published (at load, or by a command): from now on the writers
    /// leave alone the zones captured then, and nothing planned goes round any zone.
    /// </summary>
    public static void UseNewNetwork()
    {
        bool included = s_overExplored;
        s_hadNetwork = true;
        s_includeExploredKeeping = null;
        s_overExplored = false;
        s_planning = Planning.None;
        Log(included
            ? $"new network over the explored land: {s_excluded.Count} explored zone(s) with player buildings or terrain edits are left as they are"
            : $"new network: {s_excluded.Count} zone(s) generated before it are left as they are");
    }

    /// <summary>
    /// A network's generation begins (main thread, before it chooses a destination, places one or starts a worker):
    /// the zones it leaves alone are captured now, once, and the network is published with them
    /// (<see cref="UseNewNetwork"/>): the explored land (<see cref="Explored"/>), or, for a network that includes it
    /// (<see cref="IncludeExplored"/>), only the explored zones kept from it. Not the zones its own generation places first
    /// (LocationsFirstLoad), where no player has been, which are where its roads go onto those locations' landings. While it runs
    /// the world holds still, so only that placing generates zones.
    ///
    /// Every network plans round exactly that set (<see cref="Avoids"/>), whoever starts it (the load, road_generate,
    /// road_regen_island): it chooses no destination in it, and its roads go round it, with no exemption for the zones
    /// they end in.
    /// </summary>
    public static void BeginNewNetwork()
    {
        bool overExplored = Current && s_includeExploredKeeping != null;
        HashSet<Vector2s> excluded = overExplored ? new HashSet<Vector2s>(s_includeExploredKeeping!) : Explored;
        Adopt();
        s_excluded = excluded;
        StructurePlans.ForgetBuildableStaircases();
        s_includeExploredKeeping = null;
        s_overExplored = overExplored;
        // Its roads keep the writer's reach from them (RoadSpatialGrid.ZoneReach: how far a road point changes the ground, the
        // zone gather's own reach), as a manual road's do (AvoidWhilePlanning): not only the centreline, the footprint goes round.
        s_planning = new Planning(new HashSet<Vector2s>(excluded), RoadSpatialGrid.ZoneReach(RoadNetworkGenerator.RoadWidth), endsExempt: false);
    }

    /// <summary>
    /// An operator's manual road crosses these zones: it is built in full there, so every writer (terrain, rocks,
    /// vegetation, bridge pieces) may write them from now on, and the record saves them as the network's. Returns the
    /// zones that were excluded until now, in a stable order, so the operator can be told which explored zones the
    /// road reshapes.
    /// </summary>
    public static List<Vector2s> Admit(IEnumerable<Vector2s> zones)
    {
        var admitted = new List<Vector2s>();
        if (!Current) return admitted;
        foreach (Vector2s zone in zones)
            if (s_excluded.Remove(zone)) admitted.Add(zone);
        StructurePlans.ForgetBuildableStaircases();
        admitted.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
        return admitted;
    }

    /// <summary>What the operator is told when a manual road builds in zones explored before the network.</summary>
    public static string AdmissionWarning(string what, IReadOnlyList<Vector2s> zones) =>
        $"Warning: {what} {zones.Count} zone(s) explored before the road network. They are now treated " +
        "like land explored after it: every road there is built in full, over whatever stands on its line. Zones: " +
        string.Join(", ", System.Linq.Enumerable.Select(zones, z => $"({z.x}, {z.y})")) + ".";

    /// <summary>Undo <see cref="Admit"/> when the road it was for was not added after all.</summary>
    internal static void Readmit(IEnumerable<Vector2s> zones)
    {
        if (!Current) return;
        foreach (Vector2s zone in zones) s_excluded.Add(zone);
        StructurePlans.ForgetBuildableStaircases();
    }

    /// <summary>
    /// The world is going away. With the world named, only its own state is
    /// dropped: a late teardown of an earlier world leaves the current one's.
    /// </summary>
    public static void Forget(object? world = null)
    {
        if (world != null && !ReferenceEquals(world, s_world)) return;
        s_world = null;
        s_hadNetwork = false;
        s_generatedAtLoad = null;
        s_excluded = new HashSet<Vector2s>();
        StructurePlans.ForgetBuildableStaircases();
        s_includeExploredKeeping = null;
        s_overExplored = false;
        s_planning = Planning.None;
    }

    /// <summary>
    /// How much of the road a terrain writer lays at this point, from 0 to 1: its change to the
    /// ground (level and smooth) and its paint. A zone left as it is keeps its own ground, so a road
    /// that leaves it for a zone the network writes would start with a step (up to the earthwork
    /// cap) and a hard paint line on the zone border. Instead the road fades in: 0 on the border of
    /// an excluded zone, rising evenly to 1 at <see cref="SeamFadeDistance"/> from it. Distance is
    /// measured to the excluded zone's square, so a border the road crosses on a slant, or a zone
    /// that only touches at a corner, fades by how close the ground really is. With nothing
    /// excluded (a fresh world) this is exactly 1 everywhere.
    /// </summary>
    public static float SeamWeight(Vector2 point) => SeamWeight(point, out _);

    /// <summary><see cref="SeamWeight(Vector2)"/>, and the excluded zone it is measured to (the nearest), whose ground
    /// the fade starts from (RoadTerrainModifier); meaningless where the weight is 1.</summary>
    public static float SeamWeight(Vector2 point, out Vector2s kept)
    {
        kept = default;
        return Current && s_excluded.Count > 0 ? SeamWeight(point, s_excluded, out kept) : 1f;
    }

    /// <summary>
    /// Whether any point of this zone can be faded: an excluded zone is among its eight
    /// neighbours or is the zone itself. The fade is shorter than a zone, so nothing further away counts. Writers ask
    /// once per zone and skip <see cref="SeamWeight(Vector2)"/> where this is false.
    /// </summary>
    public static bool SeamNear(Vector2s zone)
    {
        if (!Current || s_excluded.Count == 0) return false;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                var near = new Vector2s((short)(zone.x + dx), (short)(zone.y + dy));
                if (s_excluded.Contains(near)) return true;
            }
        return false;
    }

    /// <summary><see cref="SeamWeight(Vector2)"/> against a given excluded set (tests).</summary>
    internal static float SeamWeight(Vector2 point, ICollection<Vector2s> excluded) => SeamWeight(point, excluded, out _);

    private static float SeamWeight(Vector2 point, ICollection<Vector2s> excluded, out Vector2s kept)
    {
        kept = default;
        if (excluded.Count == 0) return 1f;
        // The fade is shorter than a zone, so only the point's own zone and its eight neighbours can be near.
        Vector2s home = ZoneSystem.GetZone(new Vector3(point.x, 0f, point.y));
        float nearest = SeamFadeDistance;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                var zone = new Vector2s((short)(home.x + dx), (short)(home.y + dy));
                if (!excluded.Contains(zone)) continue;
                Vector3 centre = ZoneSystem.GetZonePos(zone);
                float outX = Mathf.Max(0f, Mathf.Abs(point.x - centre.x) - RoadConstants.HalfZoneSize);
                float outZ = Mathf.Max(0f, Mathf.Abs(point.y - centre.z) - RoadConstants.HalfZoneSize);
                float distance = Mathf.Sqrt(outX * outX + outZ * outZ);
                if (distance < nearest) { nearest = distance; kept = zone; }
            }
        return nearest >= SeamFadeDistance ? 1f : nearest / SeamFadeDistance;
    }

    /// <summary>
    /// The record to save with the network: every zone generated by now that
    /// is not excluded, i.e. generated with this network in place.
    /// Format: [format=1][network version][count] then [x][y] per zone as 16-bit ints.
    /// </summary>
    public static byte[]? Record(int networkVersion, IEnumerable<Vector2s> generatedNow)
    {
        // Not this world's state: saying nothing beats saying every zone is the network's.
        if (!Current) return null;
        var zones = new List<Vector2s>();
        foreach (Vector2s zone in generatedNow)
            if (!Excludes(zone)) zones.Add(zone);
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(RecordFormat);
        writer.Write(networkVersion);
        writer.Write(zones.Count);
        foreach (Vector2s zone in zones)
        {
            writer.Write(zone.x);
            writer.Write(zone.y);
        }
        return ms.ToArray();
    }

    internal static bool TryRead(byte[]? record, out int networkVersion, out HashSet<Vector2s> zones)
    {
        networkVersion = 0;
        zones = new HashSet<Vector2s>();
        if (record == null || record.Length == 0) return false;
        try
        {
            using var ms = new MemoryStream(record);
            using var reader = new BinaryReader(ms);
            if (reader.ReadInt32() != RecordFormat) return false;
            int version = reader.ReadInt32();
            int count = reader.ReadInt32();
            if (count < 0 || count > RoadNetworkPersistence.MaxSavedZones) return false;
            for (int i = 0; i < count; i++)
                zones.Add(new Vector2s(reader.ReadInt16(), reader.ReadInt16()));
            if (ms.Position != ms.Length) { zones.Clear(); return false; }
            networkVersion = version;
            return true;
        }
        catch (Exception)
        {
            zones.Clear();
            return false;
        }
    }

    private static void Log(string line) =>
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo($"[CUTOFF] {line}");
}
