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
