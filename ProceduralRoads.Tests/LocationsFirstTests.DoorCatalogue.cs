using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The door catalogue built at preparation (RoadNetworkGenerator.CatalogueDoors) is a step per location, so the preparation's frame
/// budget slices it: in game, with the prefabs' own rocks read (1563 static rocks from 73 Locations), each placed Location's doors led
/// in on its way in against its body's rocks, and the catalogue in one step held the main thread 3.6 s ("Finishing road preparation",
/// 0.13 s before).
/// </summary>
public partial class LocationsFirstTests
{
    [Fact]
    public void TheDoorCatalogueIsAStepPerLocationSoThePreparationSlicesIt()
    {
        PlacedLocation cave = Cave();
        PlacedLocations.Ready(cave);
        var work = new RoadNetworkGenerator.GenerationWork(DateTime.Now, WorldGenerator.instance!);
        var destinations = new List<(string name, Vector3 position, float radius)>
        {
            ("MountainCave02", new Vector3(cave.Centre.x, 50f, cave.Centre.y), 10f), ("open", new Vector3(80f, 50f, 0f), 10f),
            ("open", new Vector3(-80f, 50f, 0f), 10f),
        };
        work.Islands.Add(new RoadNetworkGenerator.IslandWork(new Island { ApproxArea = 1e6f }, destinations, new Vector3(0f, 50f, 120f), 0f));
        var preparation = new RoadPreparation(_ => Enumerable.Empty<bool>());

        int steps = RoadNetworkGenerator.CatalogueDoors(work, preparation).Count();

        Assert.Equal(destinations.Count + 1, steps);
        Assert.Equal("Cataloguing doors", preparation.Stage);
        Assert.Equal(destinations.Count + 1, preparation.Completed);
    }
}
