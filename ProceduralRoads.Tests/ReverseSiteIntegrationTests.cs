using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public sealed class ReverseSiteIntegrationTests : IDisposable
{
    private sealed class Flat : WorldGenerator { public override float GetHeight(float x, float z) => 60f; }
    private readonly Flat world = new();
    private readonly WorldGenerator? previous = WorldGenerator.instance;

    public ReverseSiteIntegrationTests()
    {
        RoadSpatialGrid.Clear(); RoadSiteProtection.Source = null;
        RoadSiteProtection.Reset(); WorldGenerator.instance = world;
        Assert.True(RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(80, -40), new(80, 40) }, 4, world));
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(new Vector2(0, 0), 12) });
    }

    [Fact]
    public void ReverseSearchCanLeaveItsOwnProtectedDestination()
    {
        var path = new RoadPathfinder(world).FindPathToNetwork(new Vector2(0, 0), 8);
        Assert.NotNull(path);
        Assert.True(RoadSpatialGrid.TryGetRoadWithin(path![path.Count - 1], 0.01f, out _));
    }

    [Fact]
    public void ReverseSearchDoesNotKeepThePreviousDestinationExempt()
    {
        var finder = new RoadPathfinder(world);
        Assert.NotNull(finder.FindPath(new Vector2(-80, 40), new Vector2(0, 0)));
        var path = finder.FindPathToNetwork(new Vector2(-80, 0), 8);
        Assert.NotNull(path);
        for (int i = 1; i < path!.Count; i++)
        {
            Vector2 a = path[i - 1], d = path[i] - a;
            float t = d.sqrMagnitude == 0 ? 0 : Mathf.Clamp01(-(a.x * d.x + a.y * d.y) / d.sqrMagnitude);
            Assert.True((a + d * t).sqrMagnitude >= 16f * 16f - 0.01f,
                "Reverse search cut through the previous destination's footprint");
        }
    }

    public void Dispose()
    {
        RoadSiteProtection.Reset(); RoadSiteProtection.Source = null;
        RoadSpatialGrid.Clear(); WorldGenerator.instance = previous;
    }
}
