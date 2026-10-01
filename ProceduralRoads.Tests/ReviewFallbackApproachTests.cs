using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public class ReviewFallbackApproachTests
{
    private sealed class BentHighBanks : WorldGenerator
    {
        public override float GetHeight(float x, float z) =>
            Mathf.Abs(x) < 12f ? 26f : Mathf.Abs(z) >= 8f ? 40f : 33f;
        public override Heightmap.Biome GetBiome(float x, float z) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        { weight = Mathf.Abs(x) < 12f ? 1f : 0f; width = 24f; }
    }

    [Fact]
    public void ReturningToWaterEdgeMustRestoreTheApproachConsumedByOptionalTops()
    {
        var world = new BentHighBanks();
        WorldGenerator.instance = world;
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        try
        {
            // The accepted jump starts at index ONE, with land before it.
            // Optional tops consume that land, then the new heading fallback
            // returns the banks to the water without returning the indices.
            var path = new List<Vector2>
            {
                new(-16f, -8f), new(-16f, 0f), new(16f, 0f), new(16f, 8f)
            };
            var crossing = Assert.Single(RoadCrossingDetector.Detect(path, world, bridges: true, fords: false));
            Assert.True(BridgeLayout.HeadingIsPlaceable(BridgeLayout.YawDegrees(crossing.Direction)));
            Assert.InRange(crossing.FromBank.y, -0.01f, 0.01f);
            Assert.InRange(crossing.ToBank.y, -0.01f, 0.01f);

            // Cap held out: the fixture is a bank-top climb on purpose.
            using (GradeCap.Off())
                typeof(RoadNetworkGenerator).GetMethod("AddRoadPathWithCrossings",
                    BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
                    new object?[] { path, new List<RoadCrossing> { crossing }, 4f, null, null });

            var approach = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(-16f, 0f, -4f), 2f);
            Assert.True(approach.Count > 0,
                $"No painted near approach after water-edge fallback; crossing indices {crossing.FromIndex}..{crossing.ToIndex}, " +
                "accepted jump indices 1..2. Returning only bank coordinates left the rejected tops consuming the land segment.");
        }
        finally
        {
            RoadNetworkGenerator.Reset();
            RoadSpatialGrid.Clear();
            WorldGenerator.instance = new WorldGenerator();
        }
    }
}
