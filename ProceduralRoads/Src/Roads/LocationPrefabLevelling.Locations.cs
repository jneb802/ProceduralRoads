using UnityEngine;

namespace ProceduralRoads;

public static partial class LocationPrefabLevelling
{
    /// <summary>
    /// The location standing within half a metre of <paramref name="centre"/> each way: the one resolver of a location from
    /// coordinates only (docs/OWNERS.md). Its levelling (<see cref="OpsAt"/>), its turn (<see cref="FacesDownhillAt"/>), its
    /// prefab (<see cref="PrefabAt"/>: the generator's reached test and staircase
    /// rule), the location a manual mark stands in (ManualRoads.LocationAt), the locations a generation gives no road
    /// (PlacedLocations.FindNotPlaced) and the locations it places first (LocationsFirstLoad) all ask it. A zone holds at most one
    /// location, so it is the one in the zone under that square's corners (one zone but where the square straddles a zone's
    /// edge). Two locations either side of a zone's edge that both stand within the square are ambiguous: neither is taken
    /// (false), never the first corner's, and <paramref name="two"/> says so (<see cref="TwoLocations"/>), for the callers that
    /// refuse the location to say why; it is null where no location stands there. A lookup in the zone table, never a walk of
    /// the whole list, which island workers ask of every road point in a location's reach (the reached test, through
    /// RoadNetworkGenerator.ArrivalHeight). Once resolved, a location is carried by its location's centre, as its footprint
    /// carries it (<see cref="RoadLocationProtection.Footprint.Is"/>), and found again by it exactly (<see cref="LocationOf"/>),
    /// not looked up again here.
    /// </summary>
    internal static bool LocationAt(Vector2 centre, out ZoneSystem.LocationInstance location, out string? two)
    {
        location = default;
        two = null;
        if (ZoneSystem.instance == null || ZoneSystem.instance.m_locationInstances == null) return false;
        bool found = false;
        Vector2s first = default, matched = default;
        for (int corner = 0; corner < 4; corner++)
        {
            Vector2s zone = ZoneSystem.GetZone(new Vector3(centre.x + ((corner & 1) == 0 ? -0.5f : 0.5f), 0f,
                centre.y + ((corner & 2) == 0 ? -0.5f : 0.5f)));
            if (corner > 0 && zone.Equals(first)) continue;
            if (corner == 0) first = zone;
            if (found && zone.Equals(matched)) continue;
            if (ZoneSystem.instance.m_locationInstances.TryGetValue(zone, out ZoneSystem.LocationInstance inst)
                && Mathf.Abs(inst.m_position.x - centre.x) <= 0.5f && Mathf.Abs(inst.m_position.z - centre.y) <= 0.5f)
            {
                if (found) { location = default; two = TwoLocations; return false; }
                location = inst;
                matched = zone;
                found = true;
            }
        }
        return found;
    }

    /// <summary>The ZoneLocation the game registers for <paramref name="prefab"/>: its table by the prefab name's stable hash
    /// (ZoneSystem.m_locationsByHash, which ZoneSystem.GetLocation(int) reads), the one a saved LocationInstance is resolved by
    /// when the world loads. Filled when the game sets up its locations, before any LocationInstance is listed, so a placed
    /// record's turn (m_slopeRotation, m_randomRotation) is known from its prefab alone (PlacedLocations.AimedOnly). False where no
    /// ZoneLocation is registered under that name.</summary>
    internal static bool ZoneLocationOf(string prefab, out ZoneSystem.ZoneLocation location)
    {
        location = null!;
        if (ZoneSystem.instance == null || ZoneSystem.instance.m_locationsByHash == null) return false;
        return ZoneSystem.instance.m_locationsByHash.TryGetValue(prefab.GetStableHashCode(), out location!) && location != null;
    }

    /// <summary>Why <see cref="LocationAt"/> resolves no location where two stand: what a refusal of it says.</summary>
    internal const string TwoLocations = "two locations stand within half a metre of its centre, either side of a zone's edge, and which one it is is not known";

    /// <summary>
    /// The location whose identity is <paramref name="identity"/> (<see cref="Centre"/>, compared exactly as
    /// <see cref="RoadLocationProtection.Footprint.Is"/> compares it), or false where no location has it: a location already
    /// resolved, found again by what it is carried by, never by a tolerance. The game files each location under its own
    /// position's zone (ZoneSystem.RegisterLocation) and keeps one a zone, so it is the one in the identity's zone, if its
    /// centre is the identity. Readers of a location they hold by its identity ask it: a saved road point's location bound to this
    /// world's (RoadSpatialGrid.RoadPoint.location; a location gone from the world is none), and a placed location's prefab, rock and
    /// rock rule (LocationsFirstLoad, PlacedLocationReader), and the parts its placing kept (<see cref="PlacedOps"/>).
    /// </summary>
    internal static bool LocationOf(Vector2 identity, out ZoneSystem.LocationInstance location)
    {
        location = default;
        if (ZoneSystem.instance == null || ZoneSystem.instance.m_locationInstances == null) return false;
        if (!ZoneSystem.instance.m_locationInstances.TryGetValue(ZoneSystem.GetZone(new Vector3(identity.x, 0f, identity.y)),
                out ZoneSystem.LocationInstance inst)
            || inst.m_position.x != identity.x || inst.m_position.z != identity.y) return false;
        location = inst;
        return true;
    }

    /// <summary>A location's identity as its footprint carries it (RoadLocationProtection.Footprint.Is): its position on the
    /// ground plane.</summary>
    internal static Vector2 Centre(ZoneSystem.LocationInstance location) => new Vector2(location.m_position.x, location.m_position.z);

    /// <summary>The prefab of the location <see cref="LocationAt"/> resolves at <paramref name="centre"/>, or null where it
    /// resolves none.</summary>
    internal static string? PrefabAt(Vector2 centre) =>
        LocationAt(centre, out ZoneSystem.LocationInstance location, out _) ? location.m_location.m_prefab.Name : null;
}
