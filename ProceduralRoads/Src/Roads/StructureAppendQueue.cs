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
    public static void Complete(Vector2s zone) { pending.Remove(zone); StructurePlans.MarkSpawned(zone); }
    public static void Acknowledge(Vector2s zone, string key)
    {
        if (!pending.TryGetValue(zone, out var pieces)) return;
        int index = pieces.FindIndex(p => Key(p) == key);
        if (index >= 0) pieces.RemoveAt(index);
    }
    public static string Key(StructurePiece p) => p.Prefab + ":" + string.Join(":", new[] {
        p.Position.x, p.Position.y, p.Position.z, p.PitchDegrees, p.YawDegrees, p.RollDegrees }.Select(x => x.ToString("R", CultureInfo.InvariantCulture)));

    /// <summary>A manual road's bridges, built now through the queue: each site's pieces saved as its first zone is built
    /// (<see cref="BridgeStructures.Build"/>).</summary>
    internal static Dictionary<Vector2s, List<StructurePiece>> Prepare(IEnumerable<RoadCrossing> crossings) =>
        Prepare(BridgeLayout.DistinctSites(crossings.ToList())
            .SelectMany(crossing => BridgeStructures.Build(crossing, WorldGenerator.instance, WorldGenerator.instance.GetSeed())));

    /// <summary>New structure pieces, by zone, as the queue owes them: a zone not
    /// yet built also gets its existing plan, since the zone is recorded as spawned
    /// once the addition completes.</summary>
    internal static Dictionary<Vector2s, List<StructurePiece>> Prepare(IEnumerable<StructurePiece> newPieces)
    {
        var result = new Dictionary<Vector2s, List<StructurePiece>>();
        foreach (var piece in newPieces)
        {
            var zone = ZoneSystem.GetZone(piece.Position);
            if (!result.TryGetValue(zone, out var pieces))
            {
                pieces = new List<StructurePiece>(); result.Add(zone, pieces);
                // An unspawned zone must receive its old plans too, since we
                // will mark the zone spawned when this operation completes.
                if (!StructurePlans.IsSpawned(zone) && !StructurePlans.ZoneHasLivePieces(zone))
                {
                    // Not the pieces of a generated bridge withheld for reaching an explored zone.
                    var old = StructurePlans.BuildablePlanFor(zone);
                    if (old != null)
                    {
                        // A bridge of the network whose first zone this is is saved as it goes out, as a zone spawn saves it.
                        if (WorldGenerator.instance is WorldGenerator world) StructureSites.KeepFirstBuilt(old, world);
                        pieces.AddRange(old);
                    }
                }
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
    public static byte[] Serialize()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(1); writer.Write(pending.Count);
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
            if (reader.ReadInt32() != 1) throw new InvalidDataException("Unknown bridge append data version.");
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
                    if (prefab.Length > 100 || !ManualRoadDraft.ValidPoint(new Vector2(pos.x, pos.z)) ||
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
