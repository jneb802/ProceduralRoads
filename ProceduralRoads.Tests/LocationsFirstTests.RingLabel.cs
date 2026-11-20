using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The label of an arrival kept at a Location's ring (RoadNetworkGenerator.PlanArrival's Ring): "nearby" is the ladder's last
/// resort alone (Way); a ring end more than ReachRise off its arrival height, which the Mistlands' any-height ring keeps, is
/// "ring-offlevel", and the [WAYIN] line says how far off it is.
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>A Mistlands tower whose road ends at its ring 12 m under its entrance's landing, the ring rung the only one: the
    /// Mistlands' any-height ring keeps the end (RingLevel), and its rung is "ring-offlevel", never "nearby"; the [WAYIN] line
    /// prints the 12 m (RingOffText).</summary>
    [Fact]
    public void AMistlandsRingEndTwelveMetresOffItsLandingIsLabelledRingOfflevel()
    {
        PlacedLocation tower = TowerOnAMound(0.5f, out MoundWorld world);
        var (path, plans) = DownTheMound(world, CryptRoadEnd);
        // The road's ground 12 m under the tower's landing (its own ground is kept), the end well off any junction step.
        world.Ground = (x, z) => new Vector2(x, z).magnitude > 16f ? 41f : 53f;
        (path, plans) = DownTheMound(world, CryptRoadEnd);
        var road = new RoadNetworkGenerator.PlannedRoad
        {
            Path = path, Plans = plans, EndCenter = tower.Centre,
            Ends = new RoadNetworkGenerator.LocationEndContext(0f, 10f, false, true, null, tower, RoadNetworkGenerator.RoadWidth, 0f, 0f),
        };
        MethodInfo planArrival = typeof(RoadNetworkGenerator).GetMethod("PlanArrival", BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = new object?[] { road, tower, false, null, null, null, null, null, null, null };
        RoadNetworkGenerator.ArrivalRung[] order = RoadNetworkGenerator.ArrivalOrder;
        RoadNetworkGenerator.ArrivalOrder = new[] { RoadNetworkGenerator.ArrivalRung.RingNearby };
        try
        {
            Assert.False((bool)planArrival.Invoke(null, args)!);
            Assert.Equal(RoadNetworkGenerator.OffLevelRing, (string)args[8]!);
            Assert.Equal("ring-offlevel", (string)args[8]!);
            MethodInfo off = typeof(RoadNetworkGenerator).GetMethod("RingOffText", BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.Equal(" (-12.0 m)", (string)off.Invoke(null, new object?[] { road, tower, false, "ring-offlevel" })!);
        }
        finally { RoadNetworkGenerator.ArrivalOrder = order; }
    }
}
