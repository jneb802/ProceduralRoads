using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public class NetworkEndpointTests
{
    private sealed class Shore : WorldGenerator
    {
        public bool AllWater;
        public override float GetHeight(float x, float z) => !AllWater && x >= 8 ? 35 : 20;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        { weight=0; width=0; }
    }

    [Fact]
    public void LeavesDryCentreWhereAuthored()
    {
        var center = new Vector2(40, 20);
        Assert.Equal(center, RoadEndpoint.FindGround(new Shore(), center, 15));
    }

    [Fact]
    public void WetCentreMovesOntoNearbyGround()
    {
        var world = new Shore();
        var point = RoadEndpoint.FindGround(world, new Vector2(0, 0), 15);
        Assert.True(world.GetHeight(point.x, point.y) >= RoadPathfinder.LandingFloor);
        Assert.InRange(Vector2.Distance(point, new Vector2(0, 0)), 0.01f, 31f);
        Assert.Equal(point, RoadEndpoint.FindGround(world, new Vector2(0, 0), 15));
    }

    /// <summary>Ground only in a small patch off the +x axis, at a ring a large
    /// exterior radius reaches. Sampling a fixed twelve points per ring walks
    /// past it; one sample per path cell of arc finds it.</summary>
    private sealed class Spit : WorldGenerator
    {
        private const float Fifteen = 0.26179939f; // 15 degrees, off the sampled axis
        public static readonly Vector2 Patch =
            new(64 * Mathf.Cos(Fifteen), 64 * Mathf.Sin(Fifteen));
        public override float GetHeight(float x, float z) =>
            Vector2.Distance(new Vector2(x, z), Patch) <= 6 ? 35 : 20;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        { weight=0; width=0; }
    }

    [Fact]
    public void NarrowGroundOffTheAxisIsFoundAtALargeRadius()
    {
        var world = new Spit();
        var found = RoadEndpoint.FindGround(world, new Vector2(0, 0), 64);
        Assert.True(world.GetHeight(found.x, found.y) >= RoadPathfinder.LandingFloor,
            $"ring search returned ({found.x:F1},{found.y:F1}), which is not ground");
        Assert.InRange(Vector2.Distance(found, Spit.Patch), 0f, 6f);
    }

    [Fact]
    public void NoGroundDoesNotInventAnUnrelatedDestination() =>
        Assert.Equal(new Vector2(0, 0), RoadEndpoint.FindGround(new Shore { AllWater=true }, new Vector2(0, 0), 15));
}
