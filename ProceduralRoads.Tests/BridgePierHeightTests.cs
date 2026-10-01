using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Pier height - how far the structure stands above the channel bed - is the
/// quantity that decides whether vanilla structural support can reach the
/// deck. The crossing diagnostic used to report only water depth, which is a
/// different number and can be far smaller: a bridge over a shallow channel
/// between high banks is a tall structure. These tests pin the difference so
/// nobody surveys bridge height off the wrong field again.
/// </summary>
public class BridgePierHeightTests
{
    private static (RoadCrossing crossing, WorldGenerator world) Crossing(float eastRise) =>
        BridgeTests.WideCrossingForPierTests(eastRise);

    private static float BedDepth(RoadCrossing c) => c.WaterLevel - c.RiverbedHeight;

    [Fact]
    public void PierHeightIsDeckAboveBed()
    {
        var (crossing, world) = Crossing(0f);
        float expected = BridgeLayout.DeckHeight(crossing, world) - crossing.RiverbedHeight;
        Assert.Equal(expected, BridgeLayout.PierHeight(crossing, world), 3);
    }

    /// <summary>The whole point: the two numbers are not the same, and water
    /// depth is the smaller one. Banks stand above the water, so a deck sprung
    /// from them always clears the bed by more than the water is deep.</summary>
    [Fact]
    public void PierHeightExceedsWaterDepth()
    {
        var (crossing, world) = Crossing(0f);
        float pier = BridgeLayout.PierHeight(crossing, world);
        float depth = BedDepth(crossing);
        Assert.True(pier > depth,
            $"pier {pier:F2} m should stand taller than the water is deep ({depth:F2} m)");
    }

    /// <summary>Raising a bank raises the deck and therefore the piers, while
    /// the channel is untouched and its depth does not move. A survey taken
    /// from bed depth would see no change at all.</summary>
    [Fact]
    public void RaisingABankRaisesThePiersButNotTheWaterDepth()
    {
        var (flat, flatWorld) = Crossing(0f);
        var (raised, raisedWorld) = Crossing(2f);

        Assert.Equal(BedDepth(flat), BedDepth(raised), 3);

        float flatPier = BridgeLayout.PierHeight(flat, flatWorld);
        float raisedPier = BridgeLayout.PierHeight(raised, raisedWorld);
        Assert.True(raisedPier > flatPier + 1.5f,
            $"raising the bank by 2 m should raise the piers; {flatPier:F2} -> {raisedPier:F2}");
    }
}
