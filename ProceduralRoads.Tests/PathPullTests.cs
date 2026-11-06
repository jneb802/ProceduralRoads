using System.Collections.Generic;
using UnityEngine;
using Xunit;
namespace ProceduralRoads.Tests;

public class PathPullTests
{
    private static bool Dry(Vector2 p) => false;

    // A staircase of cells: east, north, east, north... on the grid.
    private static List<Vector2> Staircase()
    {
        var p = new List<Vector2> { new(0, 0) };
        for (int i = 1; i <= 8; i++) p.Add(new Vector2(8f * ((i + 1) / 2), 8f * (i / 2)));
        return p;
    }

    [Fact]
    public void AStaircaseOnFlatGroundBecomesAStraightLine()
    {
        RoadSiteProtection.Reset();
        var pulled = RoadPathPull.Pull(Staircase(), 4, _ => 40f, Dry, 0.45f, maxPull: 256f);
        Assert.Equal(2, pulled.Count);
        Assert.Equal(new Vector2(0, 0), pulled[0]);
        Assert.Equal(new Vector2(32, 32), pulled[pulled.Count - 1]);
    }

    [Fact]
    public void AContouringZigzagIsKept()
    {
        RoadSiteProtection.Reset();
        // Ground rises 0.3 per metre to the north. The zigzag climbs gently
        // east-west; the straight line between its ends climbs the fall line.
        var zig = new List<Vector2> { new(0, 0), new(40, 8), new(0, 16), new(40, 24), new(0, 32) };
        var pulled = RoadPathPull.Pull(zig, 4, p => 40f + 0.3f * p.y, Dry, 0.45f, maxPull: 256f);
        Assert.Equal(zig.Count, pulled.Count);
    }

    [Fact]
    public void WaterStopsAStraightLine()
    {
        RoadSiteProtection.Reset();
        // A pond on the straight diagonal, 5.7 m from every staircase waypoint.
        bool Pond(Vector2 p) => Vector2.Distance(p, new Vector2(12, 12)) < 3f;
        var pulled = RoadPathPull.Pull(Staircase(), 4, _ => 40f, Pond, 0.45f, maxPull: 256f);
        Assert.True(pulled.Count > 2);
        foreach (var pt in pulled) Assert.False(Pond(pt));
    }

    [Fact]
    public void BothEndsAreHeld()
    {
        RoadSiteProtection.Reset();
        var path = Staircase();
        var pulled = RoadPathPull.Pull(path, 4, p => 40f + 0.02f * p.x, Dry, 0.45f);
        Assert.Equal(path[0], pulled[0]);
        Assert.Equal(path[path.Count - 1], pulled[pulled.Count - 1]);
    }
}
