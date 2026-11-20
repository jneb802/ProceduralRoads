using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

public class SearchBudgetTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EverySearchKeepsALevelJunctionFoundOnItsLastIteration(int entry)
    {
        using var budget = StaticOverride.Keep(() => RoadPathfinder.MaxIterations);
        using var junction = StaticOverride.Keep(() => RoadPathfinder.JunctionStep);
        RoadPathfinder.MaxIterations = 1;
        RoadPathfinder.JunctionStep = RoadPathfinder.DefaultJunctionStep;
        var world = new TerrainWorld(new PlaneTerrain(60f));
        using var scope = new ValheimWorldScope().WithWorld(world);
        Vector2 start = new(0f, 0f), end = new(RoadPathfinder.CellSize, 0f);
        RoadSpatialGrid.Clear();
        try
        {
            Assert.True(RoadSpatialGrid.AddRoadPath(new() { end, end + new Vector2(0f, 32f) }, 4f, world));
            var joining = new RoadPathfinder(world);
            var joined = joining.FindPathToNetwork(start, 0f, served: () => false);
            Assert.False(joining.AlreadyOnNetwork);
            Assert.NotNull(joined);
            Assert.Equal(end, joined![joined.Count - 1]);

            var pair = new RoadPathfinder(world);
            var path = entry == 0 ? pair.FindPath(start, end, joinedThrough: end)
                : entry == 1 ? pair.FindPath(start, end, iterationLimit: 1, joinedThrough: end)
                : pair.FindPathBetween(new[] { (start, 0f) }, new[] { (end, 0f) }, start, end, joinedThrough: end);
            Assert.NotNull(path);
            Assert.Equal(joined, path);
            Assert.Equal(joining.LastPathCost, pair.LastPathCost);
            Assert.Equal(0, pair.LastStartIndex);
            Assert.Equal(0, pair.LastEndIndex);
            Assert.Equal(joining.LastStairSteps, pair.LastStairSteps);
            Assert.Equal(joining.LastViaductJumps, pair.LastViaductJumps);
        }
        finally { RoadSpatialGrid.Clear(); }
    }
}
