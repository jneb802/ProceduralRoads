using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads.Tests;

/// <summary>
/// A stand-in for the load's placing (LocationsFirstLoad, not compiled here) for tests whose generation goes to locations that
/// wait for their placing (PlacedLocations.NeedsPlacing: every Mountain destination, a crypt): each chosen destination that
/// does is remembered as the game would leave it, turned to 0, its root on the ground at its centre, its levelling the
/// world's for it (none known: none).
/// </summary>
internal static class TestPlacing
{
    internal static IEnumerable<bool> PlaceAll(RoadNetworkGenerator.GenerationWork work, RoadPreparation preparation)
    {
        var world = WorldGenerator.instance!;
        var zones = ZoneSystem.instance!;
        foreach (var (_, position, _) in work.AllSelected.ToList())
        {
            var centre = new Vector2(position.x, position.z);
            if (!zones.m_locationInstances.TryGetValue(ZoneSystem.GetZone(position), out ZoneSystem.LocationInstance instance)) continue;
            if (Vector2.Distance(new Vector2(instance.m_position.x, instance.m_position.z), centre) > 1f) continue;
            if (!PlacedLocations.NeedsPlacing(instance, world) || PlacedLocations.TryGet(centre, out _)) continue;
            PlacedLocations.Remember(new PlacedLocationRecord
            {
                Prefab = instance.m_location.m_prefab.Name ?? "", Centre = centre, Yaw = 0f, Root = world.GetHeight(centre.x, centre.y),
                RockRadius = instance.m_location.m_exteriorRadius,
                Ops = new List<LevelOp>(LocationLevelling.OpsAt(centre) ?? new List<LevelOp>()),
                X0 = (int)centre.x - 60, Z0 = (int)centre.y - 60, W = 121, H = 121,
            });
        }
        yield break;
    }
}
