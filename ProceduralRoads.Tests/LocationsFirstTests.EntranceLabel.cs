using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public partial class LocationsFirstTests
{
    /// <summary>The landing a way onto an entrance was planned onto rides on the road's entry (PlannedRoad.Entries' arrival), so
    /// the log's label reads one record: a way laid onto the tower's entrance 0 (a road since its way in stops its entrance's
    /// clearance short of the door, RoadLocationEntry.WayInEnd; a staircase before) is recorded as laid onto entrance 0, as the
    /// reached test's geometry (RoadNetworkGenerator.ReachedTarget) finds it.</summary>
    [Fact]
    public void AWayLaidOntoAnEntranceIsRecordedOnThatEntranceAsTheReachedTestFindsIt()
    {
        PlacedLocation tower = TowerOnAPad(PadSlope, PadHeight, PadBatter, null);
        Assert.True(ToTheTower(tower, out string state), state);
        Assert.True(PlacedLocations.Landed(tower.Centre), state);
        Assert.Equal(0, EntranceArrivals.LaidAt(tower.Centre));
        Assert.Equal(0, RoadNetworkGenerator.ReachedTarget(tower)?.Entrance);
    }

    /// <summary>A manual road's way onto an entrance carries its entry's record to where it is laid (ManualRoads: the append's
    /// Arrivals, read by EntranceArrivals.Laid as the location is marked landed), as a generated road's does: a manual way onto a
    /// landing marked landed with no record was labelled "network at its landing".</summary>
    [Fact]
    public void AManualWayOntoAnEntranceCarriesItsEntrysRecordToWhereItIsLaid()
    {
        PlacedLocation tower = TowerOnAPad(PadSlope, PadHeight, PadBatter, null);
        var arrival = new EntranceArrivals.Arrival(tower.Entrances[0], "");
        var road = new RoadNetworkGenerator.PlannedRoad();
        road.Entries.Add((tower, null, null, arrival));
        RoadNetworkGenerator.ManualAppend append = RoadNetworkGenerator.AppendOf(new List<RoadSpatialGrid.PlannedPath>(), new List<RoadCrossing>(),
            new List<RoadStairSpan>(), RoadNetworkGenerator.LocationEnds.Of(road));
        var (location, carried, _) = Assert.Single(append.Arrivals);
        Assert.Same(tower, location);
        Assert.Same(arrival, carried);
        Assert.Contains(tower.Centre, append.Landed);
    }
}
