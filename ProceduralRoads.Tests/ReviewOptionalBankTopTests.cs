using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public class ReviewOptionalBankTopTests
{
    private sealed class BentHighBanks : WorldGenerator
    {
        public override float GetHeight(float x, float z) =>
            Mathf.Abs(x) < 12f ? 26f : Mathf.Abs(z) >= 8f ? 40f : 33f;
        public override Heightmap.Biome GetBiome(float x, float z) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        {
            weight = Mathf.Abs(x) < 12f ? 1f : 0f;
            width = 24f;
        }
    }

    [Fact]
    public void OptionalBankTopsMustNotDiscardAnAvailableRepairableWaterEdgeCrossing()
    {
        var world = new BentHighBanks();
        // Make the bounded control choose the crossing promptly; geometry
        // limits remain the production defaults.
        var edgePath = new RoadPathfinder(world)
            { Bridges = true, Fords = false, BridgeCostFixed = 0f, BridgeCostPerMeter = 0f }
            .FindPath(new Vector2(-16f, 0f), new Vector2(16f, 0f));
        Assert.NotNull(edgePath);
        var edge = Assert.Single(RoadCrossingDetector.Detect(edgePath!, world, bridges: true, fords: false));
        Assert.True(BridgeLayout.HeadingIsPlaceable(BridgeLayout.YawDegrees(edge.Direction)),
            "Control: the original water-edge crossing must be repairable.");

        // The crossing itself is the same horizontal jump. Grid-aligned
        // approaches bend toward equal-height bank tops within HighBankReach.
        var fullPath = new List<Vector2>
        {
            new(-16f, -16f), new(-16f, -8f), new(-16f, 0f),
            new(16f, 0f), new(16f, 8f), new(16f, 16f)
        };
        var actual = Assert.Single(RoadCrossingDetector.Detect(fullPath, world, bridges: true, fords: false));
        float heading = BridgeLayout.YawDegrees(actual.Direction);
        Assert.True(BridgeLayout.HeadingIsPlaceable(heading),
            $"Optional bank tops changed a repairable {BridgeLayout.YawDegrees(edge.Direction):F3}-degree crossing " +
            $"into {heading:F3} degrees, banks ({actual.FromBank.x:F3},{actual.FromBank.y:F3}) -> " +
            $"({actual.ToBank.x:F3},{actual.ToBank.y:F3}). The water-edge fallback exists and should be tried before retaining an unrepairable bridge.");
    }
}
