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
/// An explicit operator command (road_generate, road_regen_island) may write
/// them; the explored zones it writes become the network's (see Admit).
///
/// A world's first network may include the explored land instead
/// (ExistingWorldRoads.IncludeExplored, see IncludeExplored): then only the
/// explored zones players built in or dug are left as they are, and its roads
/// go round them.
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

    /// <summary>
    /// The zones the network being planned goes round (<see cref="Avoids"/>). Replaced whole, never changed in place:
    /// the search's workers read it while it is set.
    /// </summary>
    private static HashSet<Vector2s> s_avoided = new();

    /// <summary>
    /// How far, in metres, a road fades in from the edge of a zone left as it is. At that edge the
    /// road changes nothing; this far from it the road is laid in full.
    /// </summary>
    public const float SeamFadeDistance = 16f;

    private static bool Current => s_world != null && ReferenceEquals(s_world, ZoneSystem.instance);

    /// <summary>Whether automatic writers must leave this zone exactly as it is.</summary>
    public static bool Excludes(Vector2s zone) => Current && s_excluded.Contains(zone);

    /// <summary>How many zones are excluded (logs and tests).</summary>
    public static int ExcludedCount => Current ? s_excluded.Count : 0;

    /// <summary>
    /// Whether the network being planned goes round this zone rather than through it: an explored zone players built
    /// in or dug, when the network includes the explored land. Safe from the search's workers.
    /// </summary>
    public static bool Avoids(Vector2s zone)
    {
        HashSet<Vector2s> avoided = s_avoided;
        return avoided.Count > 0 && avoided.Contains(zone);
    }

    /// <summary>Whether the network being planned goes round any zone (<see cref="Avoids"/>).</summary>
    public static bool AvoidsAny => s_avoided.Count > 0;

    /// <summary>
    /// Whether a stretch of road from <paramref name="a"/> to <paramref name="b"/> enters a zone the network goes round
    /// (<see cref="Avoids"/>), sampled every half zone-cell along it so a long jump cannot fly over one. The zones the
    /// road starts and ends in are exempt: a place in one is still reached, up to that zone's border, where the road
    /// fades out (<see cref="SeamWeight(Vector2)"/>) and the zone is left as it is. The one rule the search, its jumps
    /// and the straightening after it share.
    /// </summary>
    public static bool AvoidsSegment(Vector2 a, Vector2 b, Vector2? start, Vector2? end)
    {
        HashSet<Vector2s> avoided = s_avoided;
        if (avoided.Count == 0) return false;
        Vector2s? startZone = start.HasValue ? ZoneOf(start.Value) : null;
        Vector2s? endZone = end.HasValue ? ZoneOf(end.Value) : null;
        int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / 4f));
        for (int i = 0; i <= steps; i++)
        {
            Vector2s zone = ZoneOf(Vector2.Lerp(a, b, i / (float)steps));
            if (avoided.Contains(zone) && zone != startZone && zone != endZone) return true;
        }
        return false;
    }

    private static Vector2s ZoneOf(Vector2 p) => ZoneSystem.GetZone(new Vector3(p.x, 0f, p.y));

    /// <summary>How many zones players had explored when this world was loaded (0 for a fresh world).</summary>
    public static int ExploredAtLoadCount => Current && s_generatedAtLoad != null ? s_generatedAtLoad.Count : 0;

    /// <summary>The zones players had explored when this world was loaded.</summary>
    public static IReadOnlyCollection<Vector2s> ExploredAtLoad => GeneratedAtLoad;

    /// <summary>
    /// The next new network includes the explored land: every zone explored before it is written like a new one, except
    /// <paramref name="keep"/> (the zones players built in or dug), which are left exactly as they are and which its
    /// roads go round. Only for the network this session generates first.
    /// </summary>
    public static void IncludeExplored(IEnumerable<Vector2s> keep)
    {
        Adopt();
        s_includeExploredKeeping = new HashSet<Vector2s>(keep);
        s_includeExploredKeeping.IntersectWith(GeneratedAtLoad);
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
        s_hadNetwork = false;
        s_includeExploredKeeping = null;
        s_avoided = new HashSet<Vector2s>();
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
        s_hadNetwork = true;
        Log($"loaded network {networkVersion}: {s_excluded.Count} zone(s) generated before it are left as they are");
    }

    /// <summary>
    /// A network was generated in this session (at load, or by a command).
    /// Every zone an earlier session generated is excluded: the explored part
    /// of the world keeps what its players made of it. A network that
    /// replaces another in the running session (road_generate,
    /// road_regen_island) also excludes the zones generated so far this
    /// session, where players may have built under the old one; the command
    /// writes only the zones loaded around the operator.
    /// </summary>
    public static void UseNewNetwork()
    {
        bool included = s_includeExploredKeeping != null && !s_hadNetwork;
        s_excluded = ExcludedForNewNetwork();
        s_hadNetwork = true;
        // Planned: what the search went round is now simply what the writers leave alone.
        s_includeExploredKeeping = null;
        s_avoided = new HashSet<Vector2s>();
        Log(included
            ? $"new network over the explored land: {s_excluded.Count} explored zone(s) with player buildings or terrain edits are left as they are"
            : $"new network: {s_excluded.Count} zone(s) generated before it are left as they are");
    }

    /// <summary>
    /// A network is about to be planned (main thread, before any worker starts): the zones it must leave alone are
    /// known now, so planning (a staircase's pieces and cuts) avoids them as the writers will.
    /// <see cref="UseNewNetwork"/> settles the set again when the network is published.
    /// </summary>
    public static void BeginNewNetwork()
    {
        s_excluded = ExcludedForNewNetwork();
        s_avoided = s_includeExploredKeeping != null && !s_hadNetwork ? new HashSet<Vector2s>(s_excluded) : new HashSet<Vector2s>();
    }

    private static HashSet<Vector2s> ExcludedForNewNetwork()
    {
        // The first network over the explored land leaves only the zones players built in or dug.
        if (Current && !s_hadNetwork && s_includeExploredKeeping != null)
            return new HashSet<Vector2s>(s_includeExploredKeeping);
        var excluded = new HashSet<Vector2s>(GeneratedAtLoad);
        if (Current && s_hadNetwork && ZoneSystem.instance != null)
            excluded.UnionWith(ZoneSystem.instance.m_generatedZones);
        Adopt();
        return excluded;
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
        admitted.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
        return admitted;
    }

    /// <summary>What the operator is told when a command builds roads in zones explored before the network.</summary>
    public static string AdmissionWarning(string what, IReadOnlyList<Vector2s> zones) =>
        $"Warning: {what} {zones.Count} zone(s) explored before the road network. They are now treated " +
        "like land explored after it: every road there is built in full, over whatever stands on its line. Zones: " +
        string.Join(", ", System.Linq.Enumerable.Select(zones, z => $"({z.x}, {z.y})")) + ".";

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
        s_includeExploredKeeping = null;
        s_avoided = new HashSet<Vector2s>();
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
    public static float SeamWeight(Vector2 point) =>
        Current && s_excluded.Count > 0 ? SeamWeight(point, s_excluded) : 1f;

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
    internal static float SeamWeight(Vector2 point, ICollection<Vector2s> excluded)
    {
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
                if (distance < nearest) nearest = distance;
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
            if (count < 0 || count > 1_000_000) return false;
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
