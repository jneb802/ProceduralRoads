using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>The endpoint ramp: its curve, and its effect on road-point heights through AddRoadPath.</summary>
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
        // On a plane slope smoothing has nothing to remove, so every stored
        // height must be the natural height, ramp or no ramp. The one-sided
        // smoothing window at the ends used to lean the last twenty metres
        // toward the interior: a hump on the way down, a cut on the way up.
        var world = new PlaneSlope();
        WorldGenerator.instance = world;
        RoadSpatialGrid.Clear();
        try
        {
            var path = new System.Collections.Generic.List<Vector2>();
            for (float x = -200f; x <= 200f; x += 8f)
                path.Add(new Vector2(x, 0f));
            RoadSpatialGrid.AddRoadPath(path, 4f, world);

            foreach (float end in new[] { -200f, 200f })
            {
                var points = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(end, 0f, 0f), 30f);
                Assert.True(points.Count > 10, $"too few road points near {end}");
                foreach (var rp in points)
                {
                    float natural = world.GetHeight(rp.p.x, rp.p.y);
                    Assert.True(Mathf.Abs(rp.h - natural) < 0.02f,
                        $"road point at x={rp.p.x:F1} is {rp.h - natural:F3} m off the slope");
                }
            }
        }
        finally
        {
            RoadSpatialGrid.Clear();
            WorldGenerator.instance = null;
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
        WorldGenerator.instance = world;
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
            WorldGenerator.instance = null;
        }
    }
}
