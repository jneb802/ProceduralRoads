using UnityEngine;

namespace ProceduralRoads.Tests;

/// <summary>Place test locations through the same zone-indexed collection the game reads, their ZoneLocations registered by
/// prefab as the game registers them.</summary>
internal static class TestLocations
{
    internal static void Add(ZoneSystem zones, ZoneSystem.LocationInstance location)
    {
        var zone = ZoneSystem.GetZone(location.m_position);
        if (zones.m_locationInstances.ContainsKey(zone))
            throw new System.InvalidOperationException($"Two test locations occupy zone {zone}.");
        zones.m_locationInstances.Add(zone, location);
        // The game registers every ZoneLocation by its prefab name's hash when it sets up its locations (ZoneSystem.SetupLocations),
        // the first of a name kept; a saved LocationInstance is resolved by it.
        int hash = location.m_location?.m_prefab.Name.GetStableHashCode() ?? 0;
        if (location.m_location != null && !zones.m_locationsByHash.ContainsKey(hash)) zones.m_locationsByHash.Add(hash, location.m_location);
    }
}
