using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// A placed Location's record (<see cref="PlacedLocationRecord"/>) built once, the same way for a placing read back
/// (LocationsFirstLoad.Read) and for one predicted (<see cref="Predict"/>), and which of its saved or drawn objects are its own:
/// the part of the reader the tests compile. Whatever reads the game's prefabs (the draw, the rocks' boxes) is in
/// PlacedLocationReader.cs.
/// </summary>
public static partial class PlacedLocationReader
{
    /// <summary>Whether the location whose identity is <paramref name="centre"/> (LocationPrefabLevelling.LocationOf) keeps every
    /// rock (<see cref="LocationRock.KeepsEveryRock"/>).</summary>
    internal static bool KeepsEveryRock(Vector2 centre) =>
        LocationPrefabLevelling.LocationOf(centre, out ZoneSystem.LocationInstance instance) && LocationRock.KeepsEveryRock(instance.m_location.m_prefab.name);

    /// <summary>The location's exterior radius (the location whose identity is <paramref name="centre"/>,
    /// LocationPrefabLevelling.LocationOf; 0 for none): its rock, for leading a staircase round it. The one owner of which rocks a location keeps (docs/OWNERS.md).</summary>
    internal static float RockRadius(Vector2 centre) =>
        LocationPrefabLevelling.LocationOf(centre, out ZoneSystem.LocationInstance instance) ? instance.m_location.m_exteriorRadius : 0f;

    /// <summary>The Location's own rocks and pieces in the read square from (<paramref name="x0"/>, <paramref name="z0"/>),
    /// <paramref name="w"/> metres a side.</summary>
    internal delegate List<LocationRock> RocksIn(int x0, int z0, int w);

    /// <summary>
    /// The one builder of a placed Location's record, read back or predicted: its prefab, turn and root, the terrain modifiers
    /// its placing kept (<paramref name="ops"/>), its rock radius and whether it keeps every rock, and the square its ground and
    /// rocks are read over (<see cref="ReadHalf"/> round its centre, a vertex past it each way), with its own rocks there
    /// (<paramref name="rocks"/>).
    /// </summary>
    internal static PlacedLocationRecord Record(Vector2 centre, string prefab, float yaw, float root, IReadOnlyList<LevelOp> ops, RocksIn rocks)
    {
        float rock = RockRadius(centre);
        // The ground the road onto its landing may be laid over (ReadHalf).
        float half = ReadHalf(centre, rock);
        int x0 = Mathf.FloorToInt(centre.x - half), z0 = Mathf.FloorToInt(centre.y - half), w = Mathf.CeilToInt(2f * half) + 1;
        return new PlacedLocationRecord
        {
            Prefab = prefab,
            Centre = centre, Yaw = yaw, Root = root, RockRadius = rock, KeepSmallRocks = KeepsEveryRock(centre),
            Ops = new List<LevelOp>(ops), Rocks = rocks(x0, z0, w), X0 = x0, Z0 = z0, W = w + 1, H = w + 1,
        };
    }

    /// <summary>A saved object as the rocks read it: its prefab, position, turn, its saved vector scale (a building piece's box)
    /// and its saved scale either way (<see cref="SavedScale"/>).</summary>
    internal readonly struct SavedObject
    {
        public readonly GameObject Prefab;
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly Vector3? PieceScale, Scale;
        public SavedObject(GameObject prefab, Vector3 position, Quaternion rotation, Vector3? pieceScale, Vector3? scale)
        { Prefab = prefab; Position = position; Rotation = rotation; PieceScale = pieceScale; Scale = scale; }
    }

    /// <summary>What a saved or drawn object in a Location's footprint is to it (<see cref="Classify"/>).</summary>
    internal enum Own
    {
        /// <summary>Not a rock and not one of its pieces: not read.</summary>
        None,
        /// <summary>One of its building pieces (a part with a WearNTear): its body.</summary>
        Piece,
        /// <summary>One of its parts of a kind no road clears (its ice, a rock-named part): kept.</summary>
        OwnRock,
        /// <summary>A clearable boulder that is one of its parts, or one where its parts are not known: its rock.</summary>
        Rock,
        /// <summary>A clearable boulder that is not one of its parts: the world's, any road's to carve.</summary>
        World,
    }

    /// <summary>
    /// What the object of prefab <paramref name="name"/> is to the Location: a clearable rock (<paramref name="clearable"/>) or a
    /// rock-named part (LocationRock.OwnRockName), a building piece (<paramref name="wearNTear"/>), and whether it is one of the
    /// Location's parts (<paramref name="isPart"/>: LocationPrefabLevelling.IsPart asked with the object's own prefab, null for no
    /// Location there). The one owner for the objects a placing saved and those a prediction draws (<see cref="OwnRocks"/>).
    /// </summary>
    internal static Own Classify(string name, bool clearable, bool wearNTear, Func<bool?>? isPart)
    {
        bool? part = null;
        bool Part() => (part ??= isPart?.Invoke()) == true;
        // The location's own rocks of kinds no road clears, its ice above all (Hildir's cave: ice_rock1, rock3_ice, caverock_ice_*):
        // kept and kept clear of, as its large rocks are. Left out, a staircase ran up beside and behind its great ice block.
        if (!clearable && isPart != null && LocationRock.OwnRockName(name) && Part()) return Own.OwnRock;
        if (!clearable) return isPart != null && wearNTear && Part() ? Own.Piece : Own.None;
        // A boulder of the world's standing in its footprint is any road's to carve (a rock2_mountain that stayed put while the
        // frost cave beside it turned, in game). Where the prefab's parts are not known, every boulder there, as before.
        if (isPart != null && (part ??= isPart()) == false) return Own.World;
        return Own.Rock;
    }

    /// <summary>One networked part a prediction's draw makes (<see cref="Predict"/>): its prefab, its place and turn in the prefab
    /// standing at the origin unturned, its scale saved when it syncs it, and its SnapToGround's offset (null for none).</summary>
    internal readonly struct DrawnPart
    {
        public readonly GameObject Prefab;
        public readonly Vector3 Local;
        public readonly Quaternion Rotation;
        public readonly Vector3? PieceScale, Scale;
        public readonly float? Snap;
        public DrawnPart(GameObject prefab, Vector3 local, Quaternion rotation, Vector3? pieceScale, Vector3? scale, float? snap)
        { Prefab = prefab; Local = local; Rotation = rotation; PieceScale = pieceScale; Scale = scale; Snap = snap; }
    }

    /// <summary>The Location's own rocks among <paramref name="objects"/> in the read square (<see cref="OwnRocks"/>).</summary>
    internal delegate List<LocationRock> OwnRocksOf(IEnumerable<SavedObject> objects, int x0, int z0, int w, int h, Vector2 centre, float root, float yaw);

    /// <summary>
    /// The record a predicted placing is read as, from what its draw gave: the predicted <paramref name="root"/> and
    /// <paramref name="turn"/>, the terrain modifiers kept (<paramref name="ops"/>), the networked parts SpawnLocation would make
    /// (<paramref name="parts"/>, each at root + turn x its place, turned by turn x its own turn; one the game snaps to the ground
    /// stood where the game stands it, <see cref="Snapped"/>), read by the one owner of a Location's own rocks
    /// (<paramref name="ownRocks"/>), and its prefab's own static rocks (<paramref name="prefabRocks"/>), built by the one record
    /// builder (<see cref="Record"/>).
    /// </summary>
    internal static PlacedLocationRecord Predicted(Vector2 centre, string prefab, Vector3 at, Quaternion turn, IReadOnlyList<LevelOp> ops,
        IReadOnlyList<DrawnPart> parts, IReadOnlyList<LocationRock> prefabRocks, Func<float, float, float> ground, OwnRocksOf ownRocks)
    {
        float yaw = turn.eulerAngles.y, root = at.y;
        var saved = new List<SavedObject>(parts.Count);
        foreach (DrawnPart part in parts)
        {
            var o = new SavedObject(part.Prefab, at + turn * part.Local, turn * part.Rotation, part.PieceScale, part.Scale);
            saved.Add(part.Snap is float offset ? Snapped(o, ground, offset) : o);
        }
        return Record(centre, prefab, yaw, root, ops, (x0, z0, w) =>
        {
            List<LocationRock> rocks = ownRocks(saved, x0, z0, w, w, centre, root, yaw);
            rocks.AddRange(prefabRocks);
            return rocks;
        });
    }

    /// <summary>
    /// A part the game snaps to the ground where the game leaves it. SpawnLocation ends with SnapToGround.SnappAll, which stands each
    /// on the terrain raycast under it plus its m_offset and saves that to its ZDO. A zone a server generates for its peers, or a
    /// dedicated server for anyone (SpawnMode.Ghost), has no location levelling yet at that moment: the location's own levelling
    /// comes with its static part, which its LocationProxy spawns only once a player loads it. So the raycast hits the fresh zone's
    /// mesh (<paramref name="ground"/>, the ground the turn is predicted on), as the check reads it back. A zone a hosting player generates for themself
    /// (SpawnMode.Full) spawns that static part at once and snaps onto the levelled ground: the same x and z, another height.
    /// </summary>
    private static SavedObject Snapped(SavedObject part, Func<float, float, float> ground, float offset)
    {
        Vector3 p = part.Position;
        p.y = ground(p.x, p.z) + offset;
        return new SavedObject(part.Prefab, p, part.Rotation, part.PieceScale, part.Scale);
    }

    /// <summary>Whether the object of prefab <paramref name="objectPrefab"/> at <paramref name="at"/> is one of the networked parts of
    /// the location <paramref name="prefab"/> at <paramref name="centre"/> in <paramref name="home"/> (LocationPrefabLevelling.IsPart,
    /// at its saved placement: its LocationProxy's root and turn), for the bake's vegetation (ServerTerrainBake). A location not
    /// placed yet has none in the world. Its parts are read from its prefab once a session (<see cref="KeptOps"/>, as a location
    /// placed before its roads is read); null where they cannot be.</summary>
    internal static bool? PartOfPlaced(Vector2s home, Vector2 centre, string prefab, bool placed, Vector3 at, int objectPrefab)
    {
        if (!placed) return false;
        if (!Placed(home, centre, out ZDO? proxy)) return null;
        Vector3 root = proxy!.GetPosition();
        Quaternion turn = proxy.GetRotation();
        bool? part = LocationPrefabLevelling.IsPart(prefab, root, turn, at, objectPrefab);
        if (part == null && KeptOps(centre, proxy) != null)
            part = LocationPrefabLevelling.IsPart(prefab, root, turn, at, objectPrefab);
        return part;
    }
}
