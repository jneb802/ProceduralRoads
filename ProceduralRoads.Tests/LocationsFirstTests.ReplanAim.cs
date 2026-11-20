using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

public partial class LocationsFirstTests
{
    /// <summary>An earthwork re-route of a road to a placed location comes in head on to the door the road's own search ended
    /// at. Here that door is round the ring from the landing's side (the landing's side is dry at the ring but the sea is
    /// just beyond it); a re-route that lost its door stopped at the lead-in, 8 m out.</summary>
    [Fact]
    public void AnEarthworkReRouteComesInToTheRingPointTheRoadWasAimedAt()
    {
        var sea = new SeaOnTheLandingsSide();
        _scope.WithWorld(sea);
        RoadNetworkGenerator.Reset();
        PlacedLocation cave = Cave();
        Func<float, float, float> ground = PlacedLocationGround.Sampler(cave.Record.Ground((x, z) => sea.GetHeight(x, z)), cave.Record.X0, cave.Record.Z0, cave.Record.W, cave.Record.H);
        var location = new PlacedLocation { Record = cave.Record, Ground = ground, Shape = PlacedLocationShape.Of(cave.Centre, 90f, CaveFloor[0], 0f, null, ground, false, cave.Record.Root) };
        PlacedLocations.Ready(location);
        LocationLevelling.Source = c => c.magnitude < 0.5f ? CaveFloor : null;
        var finder = new RoadPathfinder(sea) { Stairs = false, TerrainVarianceThreshold = 1000f, Meander = 0f };
        SetPathfinder(finder);
        Vector2 LeadIn(Vector2 a) => a + (a - location.Centre).normalized * RoadNetworkGenerator.LeadInLength;
        Vector2 aim = RoadNetworkGenerator.DoorsOf(location.Centre, 10f, 4f).OrderBy(d => d.Price).First().At;

        var retry = RoadNetworkGenerator.Replan(finder, new Vector2(-20f, 120f), LeadIn(aim), new Vector2(-20f, 120f), 0f, location.Centre, 10f, 4f, null, aim,
            startReached: true, endReached: RoadNetworkGenerator.EndReached(location.Centre, 10f));

        Assert.NotNull(retry);
        Vector2 end = retry!.Value.path[retry.Value.path.Count - 1];
        Assert.True(Vector2.Distance(end, aim) < 2f, $"the re-route ends at {end}, its ring point is {aim}");
    }
}
