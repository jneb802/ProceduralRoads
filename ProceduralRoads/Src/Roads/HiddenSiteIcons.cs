using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Map icons of places placed before their roads that the game shows once placed (m_iconPlaced: Hildir's
/// quest sites, with AttemptRoadsToHildirQuestSites on). The game lists every such place once it is placed
/// (ZoneSystem.GetLocationIcons) and the server sends the list to every player; placed under the loading
/// screen, all of them would be on every player's map from the start. Vanilla places one only when a player
/// comes near enough for its zone to be generated (ZoneSystem.CreateGhostZones), so the server leaves each
/// out of the list (<see cref="Filter"/>) until a player has come that near (<see cref="Reveal"/>): the
/// players see the map as they would have. Saved with the network. Pure; the game's hooks are in
/// ZoneSystem_Patch.
/// </summary>
public static class HiddenSiteIcons
{
    private static readonly object s_gate = new();
    private static readonly Dictionary<(int, int), Vector3> s_hidden = new();

    private static (int, int) Key(Vector3 p) => (Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.z));

    public static int Count { get { lock (s_gate) return s_hidden.Count; } }

    /// <summary>Leave the place at <paramref name="position"/> (its location's position) off the map until a player comes near.</summary>
    public static void Hide(Vector3 position) { lock (s_gate) s_hidden[Key(position)] = position; }

    public static bool IsHidden(Vector3 position) { lock (s_gate) return s_hidden.ContainsKey(Key(position)); }

    public static void Reset() { lock (s_gate) s_hidden.Clear(); }

    /// <summary>The game's icon list (server side) without the hidden places.</summary>
    public static void Filter(Dictionary<Vector3, string> icons)
    {
        lock (s_gate)
        {
            if (s_hidden.Count == 0) return;
            var drop = new List<Vector3>();
            foreach (Vector3 p in icons.Keys)
                if (s_hidden.ContainsKey(Key(p))) drop.Add(p);
            foreach (Vector3 p in drop) icons.Remove(p);
        }
    }

    /// <summary>The game's zone of a point (ZoneSystem.GetZone, 64 m zones).</summary>
    public static Vector2s ZoneOf(Vector3 p) =>
        new(Mathf.FloorToInt((float)((p.x + 32.0) / 64.0)), Mathf.FloorToInt((float)((p.z + 32.0) / 64.0)));

    /// <summary>
    /// Whether the game would generate <paramref name="site"/>'s zone, and so place what stands in it, for a
    /// player whose reference position is in <paramref name="player"/>: ZoneSystem.CreateGhostZones generates
    /// the zones within <paramref name="total"/> (SimulationDistance.TotalSimulationDistance) of it, a square
    /// in the classic setting, otherwise those whose middles are less than total + 0.8 zones apart
    /// (ZonesWithinRadius, ghostZone). The server's own reference position counts as a player's.
    /// </summary>
    public static bool WithinPlacingReach(Vector2s player, Vector2s site, int total, bool classic, float zoneSize = 64f)
    {
        int dx = site.x - player.x, dz = site.y - player.y;
        if (Mathf.Abs(dx) > total || Mathf.Abs(dz) > total) return false;
        if (classic) return true;
        float reach = total * zoneSize + zoneSize * 0.8f;
        return (dx * dx + dz * dz) * zoneSize * zoneSize < reach * reach;
    }

    /// <summary>Show again every hidden place a reference position has come within placing reach of; the
    /// places shown (the caller sends the game's icon list again where any are).</summary>
    public static List<Vector3> Reveal(IEnumerable<Vector3> references, int total, bool classic)
    {
        var shown = new List<Vector3>();
        lock (s_gate)
        {
            if (s_hidden.Count == 0) return shown;
            var zones = new List<Vector2s>();
            foreach (Vector3 r in references) zones.Add(ZoneOf(r));
            foreach (var kv in s_hidden)
            {
                Vector2s site = ZoneOf(kv.Value);
                if (zones.Exists(z => WithinPlacingReach(z, site, total, classic))) shown.Add(kv.Value);
            }
            foreach (Vector3 p in shown) s_hidden.Remove(Key(p));
        }
        return shown;
    }

    public static byte[] Serialize()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        lock (s_gate)
        {
            writer.Write(1);
            writer.Write(s_hidden.Count);
            foreach (Vector3 p in s_hidden.Values) { writer.Write(p.x); writer.Write(p.y); writer.Write(p.z); }
        }
        return stream.ToArray();
    }

    /// <summary>Replace the hidden places with saved ones; none saved leaves none hidden. A damaged record
    /// throws <see cref="InvalidDataException"/> or <see cref="IOException"/> and changes nothing.</summary>
    public static void Load(byte[]? data)
    {
        var loaded = new List<Vector3>();
        if (data != null && data.Length > 0)
        {
            using var reader = new BinaryReader(new MemoryStream(data));
            int format = reader.ReadInt32();
            if (format != 1) throw new InvalidDataException($"hidden site icons format {format}");
            int count = reader.ReadInt32();
            if (count < 0 || count > 100000) throw new InvalidDataException($"hidden site icons count {count}");
            for (int i = 0; i < count; i++) loaded.Add(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
        }
        lock (s_gate)
        {
            s_hidden.Clear();
            foreach (Vector3 p in loaded) s_hidden[Key(p)] = p;
        }
    }
}
