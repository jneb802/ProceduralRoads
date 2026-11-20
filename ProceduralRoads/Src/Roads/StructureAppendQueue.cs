using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>Durable outstanding bridge additions, separate from the older zone-level
/// spawned flag. Retrying an addition must not resurrect a player's old ruined bridge.
/// A generated plan whose placement failed part way owes the rest of its pieces here too.</summary>
public static class StructureAppendQueue
{
    private static readonly Dictionary<Vector2s, List<StructurePiece>> pending = new();
    public static IEnumerable<Vector2s> Zones => pending.Keys.ToArray();
    public static int Count => pending.Count;
    public static void Reset() => pending.Clear();
    public static void Forget(IEnumerable<Vector2s> zones) { foreach (Vector2s zone in zones) pending.Remove(zone); }
    public static List<StructurePiece>? ForZone(Vector2s zone) => pending.TryGetValue(zone, out var p) ? p : null;
    /// <summary>The zone's owed pieces are all out: it owes the queue nothing more. Its own plan is not the queue's to certify
    /// (StructurePlacement marks the zone spawned once its plan is all out, which an append's pieces may or may not be).
    /// The one owner of placing pieces into zones, and whether a zone's structure is started (docs/OWNERS.md).</summary>
    public static void Complete(Vector2s zone) => pending.Remove(zone);
    public static void Acknowledge(Vector2s zone, string key)
    {
        if (!pending.TryGetValue(zone, out var pieces)) return;
        int index = pieces.FindIndex(p => Key(p) == key);
        if (index >= 0) pieces.RemoveAt(index);
    }
    public static string Key(StructurePiece p) => p.Prefab + ":" + string.Join(":", new[] {
        p.Position.x, p.Position.y, p.Position.z, p.PitchDegrees, p.YawDegrees, p.RollDegrees }.Select(x => x.ToString("R", CultureInfo.InvariantCulture)));

    /// <summary>A manual road's bridges, built now through the queue: each site's pieces saved as its first zone is built
    /// (<see cref="BridgeStructures.Build"/>). The one owner of placing pieces into zones, and whether a zone's structure is started (docs/OWNERS.md).</summary>
    internal static Dictionary<Vector2s, List<StructurePiece>> Prepare(IEnumerable<RoadCrossing> crossings) =>
        Prepare(BridgeLayout.DistinctSites(crossings.ToList())
            .SelectMany(crossing => BridgeStructures.Build(crossing, WorldGenerator.instance, WorldGenerator.instance.GetSeed())));

    /// <summary>
    /// The pieces of the requested work, by zone, as the queue owes them: those pieces only. A zone's other structures are
    /// not the request's: one built stays as it stands (a piece a player destroyed since does not come back), and one
    /// withheld (StructurePlans.BuildablePlanFor) is not built because the request made one of its zones writable; only once
    /// the request makes the last of them writable is it owed, whole (StructurePlans.ReleasedBy). A zone not
    /// generated yet whose own plan is still to be laid gets none here: generation lays its plan whole as it spawns it
    /// (StructurePlacement.SpawnInZone), these pieces with it, since the plans read them from now on; queued, they would
    /// mark the zone built and its plan would never be laid. The one owner of placing pieces into zones, and whether a
    /// zone's structure is started (docs/OWNERS.md).
    /// </summary>
    internal static Dictionary<Vector2s, List<StructurePiece>> Prepare(IEnumerable<StructurePiece> newPieces)
    {
        var result = new Dictionary<Vector2s, List<StructurePiece>>();
        var leftToGeneration = new HashSet<Vector2s>();
        foreach (var piece in newPieces)
        {
            var zone = ZoneSystem.GetZone(piece.Position);
            if (leftToGeneration.Contains(zone)) continue;
            if (!result.TryGetValue(zone, out var pieces))
            {
                if (ZoneSystem.instance is ZoneSystem zones && !zones.IsZoneGenerated(zone) && !StructurePlans.Started(zone) &&
                    StructurePlans.BuildablePlanFor(zone) != null)
                {
                    leftToGeneration.Add(zone);
                    continue;
                }
                pieces = new List<StructurePiece>(); result.Add(zone, pieces);
            }
            pieces.Add(piece);
        }
        return result;
    }
    internal static void Enqueue(Dictionary<Vector2s, List<StructurePiece>> additions)
    {
        foreach (var entry in additions)
        {
            if (!pending.TryGetValue(entry.Key, out var pieces)) pending.Add(entry.Key, pieces = new List<StructurePiece>());
            var keys = new HashSet<string>(pieces.Select(Key));
            foreach (var piece in entry.Value) if (keys.Add(Key(piece))) pieces.Add(piece);
        }
    }
    /// <summary>The saved record's format.</summary>
    private const int Format = 1;

    public static byte[] Serialize()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(Format); writer.Write(pending.Count);
        foreach (var entry in pending)
        {
            writer.Write((int)entry.Key.x); writer.Write((int)entry.Key.y); writer.Write(entry.Value.Count);
            foreach (var p in entry.Value)
            {
                writer.Write(p.Prefab); writer.Write(p.Position.x); writer.Write(p.Position.y); writer.Write(p.Position.z);
                writer.Write(p.PitchDegrees); writer.Write(p.YawDegrees); writer.Write(p.RollDegrees); writer.Write(p.HealthFraction);
            }
        }
        return stream.ToArray();
    }
    public static void Load(byte[]? bytes)
    {
        var loaded = new Dictionary<Vector2s, List<StructurePiece>>();
        if (bytes != null && bytes.Length > 0)
        {
            using var stream = new MemoryStream(bytes); using var reader = new BinaryReader(stream);
            if (reader.ReadInt32() != Format) throw new InvalidDataException("Unknown bridge append data version.");
            int zones = reader.ReadInt32();
            if (zones < 0 || zones > 10000) throw new InvalidDataException("Invalid bridge append zone count.");
            int total = 0;
            for (int i = 0; i < zones; i++)
            {
                int x = reader.ReadInt32(), y = reader.ReadInt32(), count = reader.ReadInt32();
                if (Math.Abs((long)x) > 200 || Math.Abs((long)y) > 200 || count < 0 || count > 100000 || (total += count) > 100000)
                    throw new InvalidDataException("Invalid bridge append record.");
                var zone = new Vector2s((short)x, (short)y); var pieces = new List<StructurePiece>();
                for (int j = 0; j < count; j++)
                {
                    string prefab = reader.ReadString();
                    var pos = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    float pitch = reader.ReadSingle(), yaw = reader.ReadSingle(), roll = reader.ReadSingle(), health = reader.ReadSingle();
                    if (prefab.Length > RoadNetworkPersistence.MaxSavedPrefabName || !RoadConstants.InWorld(new Vector2(pos.x, pos.z)) ||
                        !ManualRoadDraft.IsFinite(pos.y) || !ManualRoadDraft.IsFinite(pitch) || !ManualRoadDraft.IsFinite(yaw) ||
                        !ManualRoadDraft.IsFinite(roll) || !ManualRoadDraft.IsFinite(health) || health < 0 || health > 1)
                        throw new InvalidDataException("Invalid pending bridge piece.");
                    pieces.Add(new StructurePiece { Prefab = prefab, Position = pos, PitchDegrees = pitch, YawDegrees = yaw, RollDegrees = roll, HealthFraction = health });
                }
                loaded.Add(zone, pieces);
            }
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing bridge append data.");
        }
        pending.Clear(); foreach (var entry in loaded) pending.Add(entry.Key, entry.Value);
    }
}
