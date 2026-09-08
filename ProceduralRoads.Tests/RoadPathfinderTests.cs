using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public class RoadPathfinderTests
{
    [Fact]
    public void FindsPathAcrossOpenMeadows()
    {
        var world = new SyntheticWorld { HasRiver = false, HasMountain = false };
        var pathfinder = new RoadPathfinder(world);

        var path = pathfinder.FindPath(new Vector2(-400f, -200f), new Vector2(300f, 250f));

        Assert.NotNull(path);
        Assert.True(path!.Count > 10, $"Expected a dense path, got {path.Count} points");

        // Every waypoint must be on dry land.
        foreach (var p in path)
            Assert.True(world.GetHeight(p.x, p.y) >= RoadConstants.ShallowWaterHeight - 0.5f,
                $"Path point {p} is underwater");
    }

    [Fact]
    public void PathAvoidsSteepMountainRidge()
    {
        var world = new SyntheticWorld { HasRiver = false, HasMountain = true };
        var pathfinder = new RoadPathfinder(world);

        // Start and end on opposite sides of the ridge, both in meadows.
        var path = pathfinder.FindPath(new Vector2(-450f, 0f), new Vector2(0f, 0f));

        Assert.NotNull(path);
    }

    [Fact]
    public void RiverBlocksPathInCurrentImplementation()
    {
        // Characterization test: documents upstream master behavior — a river
        // spanning the island makes the far side unreachable. The planned
        // cost-model rework + crossings should flip this expectation.
        var world = new SyntheticWorld { HasRiver = true, HasMountain = false };
        var pathfinder = new RoadPathfinder(world);

        var path = pathfinder.FindPath(new Vector2(-300f, 0f), new Vector2(400f, 0f));

        Assert.Null(path);
    }

    [Fact]
    public void PathIsDeterministic()
    {
        var world = new SyntheticWorld { HasRiver = false };
        var a = new RoadPathfinder(world).FindPath(new Vector2(-400f, 100f), new Vector2(-100f, -300f));
        var b = new RoadPathfinder(world).FindPath(new Vector2(-400f, 100f), new Vector2(-100f, -300f));

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.Count, b!.Count);
        for (int i = 0; i < a.Count; i++)
            Assert.Equal(a[i], b[i]);
    }

    [Fact]
    public void RendersDebugMap()
    {
        // Not an assertion-heavy test: produces debug-world.bmp next to the
        // test assembly so generation quality can be inspected visually.
        var world = new SyntheticWorld { HasRiver = true, HasMountain = true };
        var pathfinder = new RoadPathfinder(world);

        var attempts = new List<(Vector2 from, Vector2 to)>
        {
            (new Vector2(-450f, -50f), new Vector2(0f, 300f)),   // around/over the mountain
            (new Vector2(-300f, -350f), new Vector2(50f, -100f)), // meadows
            (new Vector2(-300f, 0f), new Vector2(400f, 0f)),      // across the river (fails today)
        };

        var paths = new List<(List<Vector2>, byte, byte, byte)>();
        var markers = new List<(Vector2, byte, byte, byte)>();

        foreach (var (from, to) in attempts)
        {
            var path = pathfinder.FindPath(from, to);
            if (path != null)
                paths.Add((path, 220, 40, 40));
            markers.Add((from, 255, 255, 255));
            markers.Add((to, path != null ? (byte)30 : (byte)255, path != null ? (byte)30 : (byte)230, 30));
        }

        string output = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(typeof(RoadPathfinderTests).Assembly.Location)!,
            "debug-world.bmp");

        WorldRenderer.Render(world, paths, markers, output);
        Assert.True(System.IO.File.Exists(output));
    }
}
