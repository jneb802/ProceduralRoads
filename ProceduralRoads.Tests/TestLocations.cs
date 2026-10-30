using UnityEngine;

namespace ProceduralRoads.Tests;

/// <summary>Place test locations through the same zone-indexed collection the game reads.</summary>
internal static class TestLocations
{
    internal static void Add(ZoneSystem zones, ZoneSystem.LocationInstance location)
    {
        var zone = ZoneSystem.GetZone(location.m_position);
        if (zones.m_locationInstances.ContainsKey(zone))
            throw new System.InvalidOperationException($"Two test locations occupy zone {zone}.");
        zones.m_locationInstances.Add(zone, location);
    }
}
