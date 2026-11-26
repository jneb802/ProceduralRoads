using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// A Location's own networked parts and the one matcher of an object to them (<see cref="IsPart"/>): the part of
/// LocationPrefabLevelling the tests compile. The parts are read where the prefab is replayed (LocationPrefabLevelling.PlacedOps,
/// <see cref="PartOf"/>); everything that asks whether an object is a Location's own asks here, the same way.
/// </summary>
public static partial class LocationPrefabLevelling
{
    /// <summary>Where each location prefab's networked objects stand, from its root, by prefab
    /// name: read as its parts are replayed (<see cref="PlacedOps"/>).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Part[]> s_parts = new();

    /// <summary>
    /// One networked part of a location prefab: where it stands from the root, its own prefab (the prefab name's stable hash, as
    /// its ZDO names it), and the height its SnapToGround puts it over the ground (null when it has none). The game drops a
    /// snapping part onto the ground once the location is spawned (ZoneSystem.SpawnLocation ends with SnapToGround.SnappAll: the
    /// terrain raycast under it plus m_offset, saved to its ZDO; which ground depends on the spawn mode,
    /// PlacedLocationReader.Snapped), so of a snapping part only x and z are where the prefab put it.
    /// </summary>
    public readonly struct Part
    {
        public readonly Vector3 Local;
        public readonly int Prefab;
        public readonly float? Snap;
        public Part(Vector3 local, int prefab, float? snap) { Local = local; Prefab = prefab; Snap = snap; }
    }

    /// <summary>The parts of the location prefab <paramref name="prefabName"/>, as its replay read them (<see cref="PlacedOps"/>).</summary>
    internal static void SetParts(string prefabName, Part[] parts) => s_parts[prefabName] = parts;

    /// <summary>Forget every prefab's parts (tests).</summary>
    internal static void ForgetParts() => s_parts.Clear();

    /// <summary>How far an object may stand from one of its location's part spots and be that part.</summary>
    public const float PartTolerance = 0.5f;

    /// <summary>
    /// Whether the object of prefab <paramref name="objectPrefab"/> (its prefab name's stable hash, as its ZDO names it) at
    /// <paramref name="at"/> is one of the networked parts of the location <paramref name="prefabName"/> placed at
    /// <paramref name="root"/> turned by <paramref name="rotation"/>: the game spawns each at root + rotation x its offset in the
    /// prefab (ZoneSystem.SpawnLocation). The one matcher: the reader (PlacedLocationReader.OwnRocks, every branch), the rock
    /// pass (RoadRockProbe.KeptOnApproach) and the bake's vegetation (ServerTerrainBake.PartOfLocation) ask it the same way.
    /// Null while that prefab's parts are unknown (not yet replayed this session). Only a part of the object's own prefab counts:
    /// a vegetation stone beside one of a crypt's parts is not the crypt's, and a rock2_mountain in a frost cave's footprint that
    /// stays put while the cave turns between runs is not the cave's. A part the game snaps to the ground (<see cref="Part.Snap"/>:
    /// a Dvergr stake wall) is matched on x and z only (<see cref="PartOffset"/>).
    /// </summary>
    public static bool? IsPart(string prefabName, Vector3 root, Quaternion rotation, Vector3 at, int objectPrefab)
    {
        if (!s_parts.TryGetValue(prefabName, out Part[]? parts)) return null;
        foreach (Part part in parts)
        {
            if (part.Prefab != objectPrefab) continue;
            Vector3 d = PartOffset(part, root, rotation, at);
            if (d.x * d.x + d.y * d.y + d.z * d.z <= PartTolerance * PartTolerance) return true;
        }
        return false;
    }

    /// <summary>How far the object at <paramref name="at"/> stands from <paramref name="part"/> of a location at
    /// <paramref name="root"/> turned by <paramref name="rotation"/>; level only for a part the game snaps to the ground.</summary>
    public static Vector3 PartOffset(Part part, Vector3 root, Quaternion rotation, Vector3 at)
    {
        Vector3 d = root + rotation * part.Local - at;
        if (part.Snap.HasValue) d.y = 0f;
        return d;
    }
}
