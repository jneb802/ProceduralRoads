using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A way onto a placed Location's landing at a road's start (RoadNetworkGenerator.PlanWayIn): the joint is searched for past every
/// structure the road keeps, its staircases and its kept crossings read in the joined path's indices alike. With a kept crossing
/// read at its index on the road as it was (short of the way in's points ahead of it on the joined path), the joint's search stopped
/// among the way in's own points, and the road's path ran from the joint back toward the landing and on past the joint again.
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>A road starting at <paramref name="start"/> in front of a crypt (as a start cut back along the road leaves it,
    /// EntryCutBacks) and running away from it, over a lake whose near bank at <paramref name="nearBank"/> is 10 to 25 m past that start
    /// (bridged), its way onto the landing planned at its start, longer than the road is to the bridge: the road's path runs from the
    /// joint straight away from the crypt, never back toward it nor on the spot.</summary>
    [Theory]
    [InlineData(-60f, -46f)]
    [InlineData(-66f, -54f)]
    public void AWayInAtARoadsStartWithABridgeJustPastItLeavesNoFoldInItsPath(float nearBank, float start)
    {
        PlacedLocation crypt = SunkenCryptInASwamp(LakeAt(nearBank), Heightmap.Biome.Meadows);
        var finder = (RoadPathfinder)typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        float cell = RoadPathfinder.CellSize;
        int count = Mathf.RoundToInt(200f / cell) + 1;
        var path = Enumerable.Range(0, count).Select(k => new Vector2(0f, start - cell * k)).ToList();
        bool was = RoadSpatialGrid.SuppressWiggle;
        RoadSpatialGrid.SuppressWiggle = true;
        RoadNetworkGenerator.RoadPlan plan;
        try { plan = RoadNetworkGenerator.PlanCandidate(finder, path, crypt.Centre, 22f, path[count - 1], 0f, true, false, false, false, RoadNetworkGenerator.RoadWidth); }
        finally { RoadSpatialGrid.SuppressWiggle = was; }
        Assert.True(plan.Plans != null, "the road over the lake was not planned: " + RoadSpatialGrid.LastRefusal);
        RoadCrossing bridge = Assert.Single(plan.Crossings);
        Assert.Equal(CrossingKind.Bridge, bridge.Kind);
        float pastItsStart = start - bridge.FromBank.y;
        Assert.True(pastItsStart >= 10f && pastItsStart <= 25f, $"the bridge's near bank {bridge.FromBank} is {pastItsStart:F1} m past the road's start");
        PlacedLocationShape door = crypt.Entrances[0];
        var walker = PlacedLocationShape.Walker.Road(RoadNetworkGenerator.RoadWidth);
        Vector2 roadEnd = plan.Path[0];
        Vector2 end = RoadLocationEntry.WayInEnd(door, roadEnd, walker);
        List<Vector2> line = RoadLocationEntry.Route(roadEnd, door, end, walker);
        var road = RoadNetworkGenerator.WayInRoad.Of(true, plan.Path, plan.Plans!, plan.Stairs, plan.Crossings, 0, null);

        RoadSpatialGrid.PlannedPath? wayIn = RoadNetworkGenerator.PlanWayIn(road, line, crypt.Ground(end.x, end.y), crypt.Centre,
            RoadNetworkGenerator.RoadWidth, out string why, out RoadNetworkGenerator.WayInPlan? planned);

        Assert.True(wayIn != null && planned != null, why);
        List<Vector2> newPath = planned!.Path;
        for (int i = 1; i < newPath.Count; i++)
            Assert.True(newPath[i].y < newPath[i - 1].y,
                $"the road's path turns back toward the crypt at {i}: {newPath[i - 1]} then {newPath[i]} (joint {newPath[0]})");
    }
}
