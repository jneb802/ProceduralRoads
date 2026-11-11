using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The supported road API for other mods (for example a travel service). It answers on the server and on every client
/// with this mod, from the same copy of the network's shape (<see cref="RoadTopologyService"/>). Bind by reflection and
/// check <see cref="ApiVersion"/>: a change to any signature here changes the version.
/// </summary>
public static class RoadTravelApi
{
    /// <summary>2: answers on clients from the server's copy; the copy is sent by this mod, not by its callers.</summary>
    public const int ApiVersion = 2;

    /// <summary>Whether a copy with road in it is in hand on this peer.</summary>
    public static bool Ready => RoadTopologyService.Ready;

    /// <summary>Changes whenever the copy changes. A route name from an earlier revision never connects.</summary>
    public static int Revision => RoadTopologyService.Revision;

    /// <summary>The road nearest <paramref name="position"/> within <paramref name="radius"/> metres across: its route,
    /// the point on its centreline across from the position, its heading there and its width.</summary>
    public static bool TryFindRoad(Vector3 position, float radius, out string routeId, out Vector3 point, out Vector3 direction, out float width)
    {
        RoadTopology? topology = RoadTopologyService.Current;
        if (topology != null) return topology.TryFindRoad(position, radius, out routeId, out point, out direction, out width);
        routeId = ""; point = direction = default; width = 0f;
        return false;
    }

    /// <summary>Where a road ends at the registered or configured location whose centre is <paramref name="center"/>:
    /// its route, the road's centre there, its heading and width. False where no road reaches it.</summary>
    public static bool TryGetLocationAccess(Vector3 center, out string routeId, out Vector3 point, out Vector3 direction, out float width)
    {
        RoadTopology? topology = RoadTopologyService.Current;
        if (topology != null) return topology.TryGetLocationAccess(center, out routeId, out point, out direction, out width);
        routeId = ""; point = direction = default; width = 0f;
        return false;
    }

    /// <summary>Whether two routes are on one connected network (bridges and staircases included).</summary>
    public static bool Connected(string firstRoute, string secondRoute) =>
        RoadTopologyService.Current?.Connected(firstRoute, secondRoute) ?? false;
}
