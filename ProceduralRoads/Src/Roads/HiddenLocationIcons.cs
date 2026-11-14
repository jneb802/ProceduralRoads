using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Map icons of locations the mod has the game place early (before their roads, under the loading screen) that the
/// game shows once placed (m_iconPlaced): whatever the game places while the mod has it place a zone
/// (<see cref="PlacingEarly"/>, hooked at ZoneSystem.PlaceLocations), not only Hildir's quest locations (with
/// AttemptRoadsToHildirQuestLocations on) but any other such location the zone holds. One shown always (m_iconAlways)
/// is left as the game shows it, and the game's own placing for a player is untouched. The game lists every such location once it is placed
/// (ZoneSystem.GetLocationIcons) and the server sends the list to every player; placed under the loading
/// screen, all of them would be on every player's map from the start. Vanilla locations one only when a player
/// comes near enough for its zone to be generated (ZoneSystem.CreateGhostZones), so the server leaves each
/// out of the list (<see cref="Filter"/>) until a player has come that near (<see cref="Reveal"/>): the
/// players see the map as they would have. Saved with the network. Pure; the game's hooks are in
/// ZoneSystem_Patch.
/// </summary>
public static class HiddenLocationIcons
{
    private static readonly object s_gate = new();
    private static readonly Dictionary<(int, int), Vector3> s_hidden = new();

    private static (int, int) Key(Vector3 p) => (Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.z));

    public static int Count { get { lock (s_gate) return s_hidden.Count; } }

    /// <summary>Leave the location at <paramref name="position"/> (its location's position) off the map until a player comes near. The one owner of hidden location pins (docs/OWNERS.md).</summary>
    public static void Hide(Vector3 position) { lock (s_gate) s_hidden[Key(position)] = position; }

    public static bool IsHidden(Vector3 position) { lock (s_gate) return s_hidden.ContainsKey(Key(position)); }

    public static void Reset() { lock (s_gate) { s_hidden.Clear(); s_placingEarly = 0; } }

    private static int s_placingEarly;

    /// <summary>Whether the mod is having the game place a zone now (<see cref="PlacingEarly"/>).</summary>
    public static bool IsPlacingEarly { get { lock (s_gate) return s_placingEarly > 0; } }

    /// <summary>While disposed of later, whatever the game places is placed early, for the mod (LocationsFirstLoad's
    /// ZoneSystem.SpawnZone): its icon is held back (<see cref="BeforePlacing"/>).</summary>
    public static System.IDisposable PlacingEarly()
    {
        lock (s_gate) s_placingEarly++;
        return new EarlyPlacing();
    }

    private sealed class EarlyPlacing : System.IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            lock (s_gate) if (s_placingEarly > 0) s_placingEarly--;
        }
    }

    /// <summary>As the game is about to place a zone's location (ZoneSystem.PlaceLocations, which sends the icons at
    /// once): one placed early (<see cref="PlacingEarly"/>), not placed yet, shown once placed and not always is hidden
    /// first. Whether it was hidden here (<see cref="AfterPlacing"/> takes it back if the game did not place it).</summary>
    public static bool BeforePlacing(Vector3 position, bool iconPlaced, bool iconAlways, bool placed)
    {
        if (placed || !iconPlaced || iconAlways || !IsPlacingEarly) return false;
        Hide(position);
        return true;
    }

    /// <summary>After the game's placing: a location hidden for it that the game did not place after all is not hidden
    /// (its icon shows only once placed anyway, and then by the game's own placing for a player).</summary>
    public static void AfterPlacing(Vector3 position, bool hiddenBefore, bool placed)
    {
        if (hiddenBefore && !placed) lock (s_gate) s_hidden.Remove(Key(position));
    }

    /// <summary>The game's icon list (server side) without the hidden locations.</summary>
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
    /// Whether the game would generate <paramref name="location"/>'s zone, and so place what stands in it, for a
    /// player whose reference position is in <paramref name="player"/>: ZoneSystem.CreateGhostZones generates
    /// the zones within <paramref name="total"/> (SimulationDistance.TotalSimulationDistance) of it, a square
    /// in the classic setting, otherwise those whose middles are less than total + 0.8 zones apart
    /// (ZonesWithinRadius, ghostZone). The server's own reference position counts as a player's.
    /// </summary>
    public static bool WithinPlacingReach(Vector2s player, Vector2s location, int total, bool classic, float zoneSize = 64f)
    {
        int dx = location.x - player.x, dz = location.y - player.y;
        if (Mathf.Abs(dx) > total || Mathf.Abs(dz) > total) return false;
        if (classic) return true;
        float reach = total * zoneSize + zoneSize * 0.8f;
        return (dx * dx + dz * dz) * zoneSize * zoneSize < reach * reach;
    }

    /// <summary>Show again every hidden location a reference position has come within placing reach of; the
    /// locations shown (the caller sends the game's icon list again where any are).</summary>
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
                Vector2s location = ZoneOf(kv.Value);
                if (zones.Exists(z => WithinPlacingReach(z, location, total, classic))) shown.Add(kv.Value);
            }
            foreach (Vector3 p in shown) s_hidden.Remove(Key(p));
        }
        return shown;
    }

    /// <summary>The saved record's format.</summary>
    private const int Format = 1;

    public static byte[] Serialize()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        lock (s_gate)
        {
            writer.Write(Format);
            writer.Write(s_hidden.Count);
            foreach (Vector3 p in s_hidden.Values) { writer.Write(p.x); writer.Write(p.y); writer.Write(p.z); }
        }
        return stream.ToArray();
    }

    /// <summary>Replace the hidden locations with saved ones; none saved leaves none hidden. A damaged record
    /// throws <see cref="InvalidDataException"/> or <see cref="IOException"/> and changes nothing.</summary>
    public static void Load(byte[]? data)
    {
        var loaded = new List<Vector3>();
        if (data != null && data.Length > 0)
        {
            using var reader = new BinaryReader(new MemoryStream(data));
            int format = reader.ReadInt32();
            if (format != Format) throw new InvalidDataException($"hidden location icons format {format}");
            int count = reader.ReadInt32();
            if (count < 0 || count > 100000) throw new InvalidDataException($"hidden location icons count {count}");
            for (int i = 0; i < count; i++) loaded.Add(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
        }
        lock (s_gate)
        {
            s_hidden.Clear();
            foreach (Vector3 p in loaded) s_hidden[Key(p)] = p;
        }
    }
}
