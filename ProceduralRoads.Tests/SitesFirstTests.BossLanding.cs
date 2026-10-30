using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

public partial class SitesFirstTests
{
    /// <summary>A placed boss entrance is counted reached as the planner joins it: only once a road or staircase was carried
    /// onto its own landing, not by road passing beside the landing. The count said reached where the planner
    /// went on building a road to it.</summary>
    [Fact]
    public void APlacedBossEntranceIsReachedOnlyOnceARoadIsCarriedOntoItsLanding()
    {
        PlacedSite cave = Cave();
        cave.Record.Prefab = "Mistlands_DvergrBossEntrance1";
        PlacedSites.Ready(cave);
        Vector2 landing = cave.Shape.Landing;
        float ground = cave.Ground(landing.x, landing.y);
        var points = new List<Vector2> { landing + new Vector2(-3f, 4f), landing + new Vector2(0f, 4f), landing + new Vector2(3f, 4f) };
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(_ => ground), points.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 6f, false));

        Assert.True(RoadNetworkGenerator.LandingReached(cave));
        Assert.False(RoadNetworkGenerator.Reached(Vector3.zero, 10f));
        Assert.False(RoadNetworkGenerator.PlaceServed(cave.Record.Prefab, Vector3.zero, 10f));
        PlacedSites.MarkLanded(cave.Centre);
        Assert.True(RoadNetworkGenerator.Reached(Vector3.zero, 10f));
        Assert.True(RoadNetworkGenerator.PlaceServed(cave.Record.Prefab, Vector3.zero, 10f));
        RoadSpatialGrid.Clear();
    }

    /// <summary>A boss entrance that was not placed first is reached the same way by the count and the planner: never by a
    /// road passing within reach of it. The count said reached where the planner said not served.</summary>
    [Fact]
    public void ABossEntranceNotPlacedFirstIsNotReachedByARoadPassingBy()
    {
        var points = new List<Vector2> { new(-20f, 6f), new(0f, 6f), new(20f, 6f) };
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(_ => 40f), points.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 40f, false));
        const string boss = "Mistlands_DvergrBossEntrance1";
        Assert.True(RoadNetworkGenerator.Reached(Vector3.zero, 10f), "an ordinary place there is reached");
        Assert.False(RoadNetworkGenerator.Reached(Vector3.zero, 10f, boss));
        Assert.False(RoadNetworkGenerator.PlaceServed(boss, Vector3.zero, 10f));
        // Nor is its road's own stub dropped where it merges into that road: the stub drop asks the same rule.
        _scope.WithZoneSystem();
        TestLocations.Add(ZoneSystem.instance!, new ZoneSystem.LocationInstance
        {
            m_location = new ZoneSystem.ZoneLocation { m_prefab = new SoftReferenceableAssets.SoftReference<GameObject>(boss), m_exteriorRadius = 10f },
            m_position = new Vector3(0f, 40f, 0f),
        });
        Assert.False(RoadNetworkGenerator.EndReached(new Vector2(0f, 0f), 10f));
        RoadSpatialGrid.Clear();
    }
}
