using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// A placed location's saved placement and the terrain modifiers it kept (<see cref="Placed"/>, <see cref="KeptOps"/>): the part
/// of the reader the tests compile, whatever reads the game's prefabs (<see cref="Replay"/>) apart.
/// </summary>
public static partial class PlacedLocationReader
{
    /// <summary>How far past a location's ring (or its rock), and round a road's end, its ground is read
    /// or rebuilt: a road or staircase onto its landing cannot be laid beyond it.</summary>
    public const float Margin = 16f;

    /// <summary>
    /// Half the side of the square a placed Location's ground and rocks are read over (LocationsFirstLoad, and the offline maps'
    /// placed records): out to the road's door ring (RoadNetworkGenerator.DoorRing) and round its rock on a staircase's lead-round
    /// ring (PlacedLocationShape.LeadRoundRing: a staircase from behind is led round it), and <see cref="Margin"/> beyond. The one
    /// owner of the read area.
    /// </summary>
    public static float ReadHalf(Vector2 centre, float rockRadius) =>
        Mathf.Max(RoadNetworkGenerator.DoorRing(centre, rockRadius, RoadNetworkGenerator.RoadWidth, placed: true),
            PlacedLocationShape.LeadRoundRing(rockRadius, PlacedLocationShape.Walker.Stairs(RoadStairRuns.FillerReach))) + Margin;

    private static readonly int ProxyHash = "LocationProxy".GetStableHashCode();

    /// <summary>The terrain modifiers the placement at <paramref name="proxy"/> kept, the game's draw replayed: in
    /// PlacedLocationReader.cs, and a stand-in in the tests, which have no prefab to replay.</summary>
    private static partial IReadOnlyList<LevelOp>? Replay(Vector2 centre, ZDO proxy);

    /// <summary>The location's saved placement, its LocationProxy: root, turn and seed. The proxy standing at the location's
    /// identity <paramref name="centre"/> exactly: the game stands it at its location's centre (ZoneSystem.PlaceLocations
    /// moves only the height, the proxy is made there and its ZDO saves its position whole or losslessly), so no other
    /// proxy near it is taken for it.</summary>
    internal static bool Placed(Vector2s zone, Vector2 centre, out ZDO? proxy)
    {
        proxy = null;
        if (ZDOMan.instance == null) return false;
        var objects = new List<ZDO>();
        ZDOMan.instance.FindObjects(zone, objects, new HashSet<ZoneSystem.SectorIndex>());
        foreach (ZDO zdo in objects)
        {
            if (zdo.GetPrefab() != ProxyHash) continue;
            Vector3 p = zdo.GetPosition();
            if (p.x != centre.x || p.z != centre.y) continue;
            proxy = zdo;
            return true;
        }
        return false;
    }

    /// <summary>The parts each placed location kept, by its centre: replayed once a session.</summary>
    private static readonly Dictionary<Vector2, IReadOnlyList<LevelOp>> s_opsByLocation = new();

    /// <summary>Each placed location's prefab's own static rocks, by its centre: read in the same replay as its parts
    /// (<see cref="KeptOps"/>), for its rocks (PlacedLocationReader.LocationRocks).</summary>
    private static readonly Dictionary<Vector2, List<LocationRock>> s_prefabRocks = new();

    /// <summary>
    /// The terrain modifiers the placement at <paramref name="proxy"/> kept: the game's draw replayed
    /// (LocationPrefabLevelling.PlacedOps), once per location a session. Replaying loads the
    /// prefab, up to 387 ms on the main thread; it restores the prefab exactly, so it agrees with
    /// the placement (21 of 21). Null when the prefab cannot be read.
    /// </summary>
    internal static IReadOnlyList<LevelOp>? KeptOps(Vector2 centre, ZDO proxy)
    {
        if (s_opsByLocation.TryGetValue(centre, out IReadOnlyList<LevelOp>? known)) return known;
        IReadOnlyList<LevelOp>? ops = Replay(centre, proxy);
        if (ops != null) s_opsByLocation[centre] = ops;
        return ops;
    }

    public static void Reset()
    {
        s_opsByLocation.Clear();
        s_prefabRocks.Clear();
    }
}
