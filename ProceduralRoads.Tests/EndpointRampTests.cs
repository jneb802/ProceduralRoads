using Valheim.Testing;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>The endpoint ramp: its curve, the smoothing it blends toward, and its effect on road-point heights through AddRoadPath.</summary>
public class EndpointRampTests
{
    [Fact]
    public void RampRisesFromZeroToOne()
    {
        Assert.Equal(0f, RoadEndpointRamp.Blend(0f));
        Assert.Equal(1f, RoadEndpointRamp.Blend(RoadEndpointRamp.RampLength));
        Assert.Equal(1f, RoadEndpointRamp.Blend(RoadEndpointRamp.RampLength * 3f));

        float prev = -1f;
        for (float d = 0f; d <= RoadEndpointRamp.RampLength; d += 2f)
        {
            float blend = RoadEndpointRamp.Blend(d);
            Assert.True(blend >= prev, $"Ramp fell at {d:F0}m");
            prev = blend;
        }
    }

    [Fact]
    public void SmoothedHeightsCarryNoSlopeBiasNearTheEnds()
    {
        // On a plane slope smoothing has nothing to remove, so every smoothed
        // height must be the natural height. The one-sided smoothing window at
        // the ends used to lean the last twenty metres toward the interior: a
        // hump on the way down, a cut on the way up. The reviewer's
        // reproduction is a plane rising half a metre per metre, steeper than
        // any road the cap lets through, so it is run through the smoothing
        // itself (RoadSpatialGrid.SmoothHeights, the planner's own call) rather
        // than a whole road: the cap's own behaviour on such a slope is a
        // refusal, tested in RoadGradeRoadTests. Both windows the planner uses
        // (the long one, and the short one when the road follows the ground).
        var world = new PlaneSlope();
        using var follow = StaticOverride.Keep(() => RoadSpatialGrid.FollowGround);
        foreach (bool followGround in new[] { false, true })
        {
            RoadSpatialGrid.FollowGround = followGround;
            // A 4 m road's dense points from -200 to 200 at the planner's quarter-width spacing, evenly spaced to
            // the ends, on the natural ground. (The spline bunches its last few points, which a line fitted by
            // index reads a little off a plane; the ramp holds the very end to the ground there.)
            var heights = new System.Collections.Generic.List<float>();
            var xs = new System.Collections.Generic.List<float>();
            for (float x = -200f; x <= 200f; x += 1f) { xs.Add(x); heights.Add(world.GetHeight(x, 0f)); }
            var smoothed = RoadSpatialGrid.SmoothHeights(heights, RoadSpatialGrid.SmoothingWindow(4f), out _);
            Assert.Equal(heights.Count, smoothed.Count);

            foreach (float end in new[] { -200f, 200f })
            {
                int near = 0;
                for (int i = 0; i < xs.Count; i++)
                {
                    if (Mathf.Abs(xs[i] - end) > 30f) continue;
                    near++;
                    Assert.True(Mathf.Abs(smoothed[i] - heights[i]) < 0.02f,
                        $"window {RoadSpatialGrid.SmoothingWindow(4f)}: smoothed height at x={xs[i]:F1} is {smoothed[i] - heights[i]:F3} m off the slope");
                }
                Assert.True(near > 10, $"too few points near {end}");
            }
        }
    }

    private sealed class PlaneSlope : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => 60f + 0.5f * wx;
    }

    [Fact]
    public void RoadEndsMeetNaturalTerrainHeight()
    {
        // Integration: after AddRoadPath, the first road point carries the
        // natural terrain height (ramp blend 0) while smoothing still applies
        // mid-road, so a road meets its location without a ledge.
        var world = new SyntheticWorld { HasRiver = false, HasMountain = false };
        using var scope = new ValheimWorldScope().WithWorld(world);
        RoadSpatialGrid.Clear();
        try
        {
            var path = new System.Collections.Generic.List<Vector2>();
            for (float x = -200f; x <= 200f; x += 8f)
                path.Add(new Vector2(x, x * 0.4f)); // long enough to leave the ramps

            RoadSpatialGrid.AddRoadPath(path, 4f, world);

            Vector2 start = path[0];
            float rawStart = BiomeBlendedHeight.GetBlendedHeight(start.x, start.y, world);
            var startPoints = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(start.x, 0, start.y), 1.5f);
            Assert.True(startPoints.Count > 0, "No road point at path start");
            Assert.True(Mathf.Abs(startPoints[0].h - rawStart) < 0.05f,
                $"Start height {startPoints[0].h:F2} != natural {rawStart:F2}");
        }
        finally
        {
            RoadSpatialGrid.Clear();
        }
    }
}
