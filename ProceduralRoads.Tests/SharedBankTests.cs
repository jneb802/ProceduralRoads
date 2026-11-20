using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
using Xunit.Abstractions;

namespace ProceduralRoads.Tests;

/// <summary>
/// Two crossings that share a bank (RoadCrossingDetector.JoinSharedBanks): one river split in two by a path point in the
/// water, each half a bridge, with no ground between for the first one's far end to meet. They become one crossing, bank to
/// bank, built by the same detector as a single jump; with ground between, or where the joined span is longer than a bridge
/// may span, they stay two as before.
/// </summary>
public class SharedBankTests
{
    private readonly ITestOutputHelper m_out;
    public SharedBankTests(ITestOutputHelper output) => m_out = output;

    /// <summary>A Meadows river across x, flat along y: ground 33 m, river bed 25 m over each channel
    /// (<c>from..to</c> pairs, metres along x), river water only over the channels.</summary>
    private sealed class Channels : WorldGenerator
    {
        private readonly float[] m_channels;
        public Channels(params float[] channels) => m_channels = channels;
        private bool InChannel(float x)
        {
            for (int i = 0; i + 1 < m_channels.Length; i += 2)
                if (x >= m_channels[i] && x <= m_channels[i + 1]) return true;
            return false;
        }
        public override float GetHeight(float wx, float wy) => Mathf.Abs(wy) > 200f ? 20f : InChannel(wx) ? 25f : 33f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = InChannel(wx) ? 1f : 0f;
            width = weight > 0f ? 60f : 0f;
        }
    }

    /// <summary>A road along x with a point at x = 0 in the middle of the river (as a road bent there leaves it): two jumps,
    /// each from dry ground to that point.</summary>
    private static List<Vector2> SplitAtTheMiddle(float half) => new()
    {
        new Vector2(-half - 40f, 0f), new Vector2(-half - 10f, 0f), new Vector2(0f, 0f), new Vector2(half + 10f, 0f), new Vector2(half + 40f, 0f),
    };

    private void Describe(List<RoadCrossing> crossings)
    {
        foreach (var c in crossings)
            m_out.WriteLine(System.FormattableString.Invariant($"{c.Kind}/{c.Style} {c.FromBank} -> {c.ToBank}, {c.Width:F1} m, [{c.FromIndex}..{c.ToIndex}], invalid {c.Invalid}"));
    }

    [Fact]
    public void TwoCrossingsSharingABankBecomeOne()
    {
        var world = new Channels(-30f, 30f);
        using (new ValheimWorldScope().WithWorld(world))
        {
            var path = SplitAtTheMiddle(30f);
            var crossings = RoadCrossingDetector.Detect(path, world, bridges: true, fords: true);
            Describe(crossings);
            var c = Assert.Single(crossings);
            Assert.Equal(CrossingKind.Bridge, c.Kind);
            Assert.False(c.Invalid);
            // Bank to bank: from the near shore to the far one, over the whole river.
            Assert.InRange(Mathf.Min(c.FromBank.x, c.ToBank.x), -32f, -28f);
            Assert.InRange(Mathf.Max(c.FromBank.x, c.ToBank.x), 28f, 32f);
            Assert.True(c.FromIndex < 2 && c.ToIndex > 2, "the joined crossing consumes the path point in the water");
        }
    }

    [Fact]
    public void TwoCrossingsWithGroundBetweenStayTwo()
    {
        // The same river with a 12 m island in its middle (more than a stub of road, StructureEnds.StubLength), the road's
        // point on it: each bridge's end meets the island.
        var world = new Channels(-30f, -6f, 6f, 30f);
        using (new ValheimWorldScope().WithWorld(world))
        {
            var crossings = RoadCrossingDetector.Detect(SplitAtTheMiddle(30f), world, bridges: true, fords: true);
            Describe(crossings);
            Assert.Equal(2, crossings.Count);
            Assert.All(crossings, c => Assert.Equal(CrossingKind.Bridge, c.Kind));
            Assert.True(Vector2.Distance(crossings[0].ToBank, crossings[1].FromBank) >= StructureEnds.StubLength);
        }
        // The same river without the island: one crossing.
        var whole = new Channels(-30f, 30f);
        using (new ValheimWorldScope().WithWorld(whole))
            Assert.Single(RoadCrossingDetector.Detect(SplitAtTheMiddle(30f), whole, bridges: true, fords: true));
    }

    [Fact]
    public void AJoinedSpanOverTheBridgeSpanIsRefusedAsBefore()
    {
        // Each half within the bridge span, the whole river past it: the two stay as they were, and their shared bank is
        // refused by the end owner as before (never one bridge longer than the search may cross).
        float half = 0.5f * RoadConstants.MaxBridgeSpan + 6f;
        var world = new Channels(-half, half);
        using (new ValheimWorldScope().WithWorld(world))
        {
            var path = SplitAtTheMiddle(half);
            var crossings = RoadCrossingDetector.Detect(path, world, bridges: true, fords: true);
            Describe(crossings);
            Assert.Equal(2, crossings.Count);
            Assert.True(Vector2.Distance(crossings[0].ToBank, crossings[1].FromBank) < StructureEnds.StubLength, "the two share a bank");
            Assert.All(crossings, c => Assert.True(c.Width <= RoadConstants.MaxBridgeSpan));
        }
        // A river just within the span, split the same way: one crossing.
        float within = 0.5f * RoadConstants.MaxBridgeSpan - 6f;
        var narrower = new Channels(-within, within);
        using (new ValheimWorldScope().WithWorld(narrower))
        {
            var c = Assert.Single(RoadCrossingDetector.Detect(SplitAtTheMiddle(within), narrower, bridges: true, fords: true));
            Assert.True(c.Width <= RoadConstants.MaxBridgeSpan);
        }
    }
}
