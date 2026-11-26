using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public partial class LocationsFirstTests
{
    /// <summary>A cave's entrance is reached by network level with its landing on its entrance side (measured from the landing), anywhere
    /// there, not on its door's lane only: a road ending beside the mouth but in front of it, within the landing's reach, is at the
    /// entrance; one beside it and behind the landing is not (it would be walked in under the mouth's overhang). A non-cave entrance is
    /// reached from anywhere within the landing's reach as before.</summary>
    [Theory]
    [InlineData("TrollCave02", false)]
    [InlineData("Crypt4", true)]
    public void ACavesEntranceIsReachedByNetworkOnItsEntranceSide(string prefab, bool reachedFromBehind)
    {
        PlacedLocation cave = Cave();
        cave.Record.Prefab = prefab;
        cave.Entrances = new[] { PlacedLocationShape.Of(cave.Centre, cave.Record.Yaw, CaveFloor[0], 0f, null, cave.Ground, false, cave.Record.Root, prefab, 0, bodyKnown: true) };
        PlacedLocations.Ready(cave);
        PlacedLocationShape entrance = cave.Entrances[0];
        Vector2 landing = entrance.Landing, across = new(entrance.Front.y, -entrance.Front.x);
        float ground = cave.Ground(landing.x, landing.y);
        void Road(Vector2 at)
        {
            var points = new List<Vector2> { at - entrance.Front, at };
            RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(_ => ground), points.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 6f, false));
        }
        // Beside the mouth, in front: 5 m across, 2 m out.
        Road(landing + across * 5f + entrance.Front * 2f);
        Assert.Same(entrance, RoadNetworkGenerator.ReachedTarget(cave));
        RoadSpatialGrid.Clear();
        // Beside the mouth, behind the landing: 5 m across, 2 m back.
        Road(landing + across * 5f - entrance.Front * 2f);
        Assert.Equal(reachedFromBehind, RoadNetworkGenerator.ReachedTarget(cave) == entrance);
        RoadSpatialGrid.Clear();
        // On the lane, 5 m out in front.
        Road(landing + entrance.Front * 5f);
        Assert.Same(entrance, RoadNetworkGenerator.ReachedTarget(cave));
        RoadSpatialGrid.Clear();
    }
}
