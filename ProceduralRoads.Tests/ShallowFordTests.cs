using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
namespace ProceduralRoads.Tests;

/// <summary>
/// Shallow water the road walks (swamp pools) is a ford in the river fords' style mix. Before, a
/// generated world had 0 swamp fords of any style: the search wades swamp shallows as ordinary steps and
/// the river detector only sees rivers, so every pool was waded, however long or deep.
/// </summary>
[Collection("RoadStatics")]
public class ShallowFordTests
{
    /// <summary>A swamp at 31 m with a pool of the given depth and length centred on (cx, 0), no river.</summary>
    private sealed class SwampPool : WorldGenerator
    {
        private readonly float m_cx, m_half, m_bed;
        public SwampPool(float cx, float length, float depth) { m_cx = cx; m_half = length * 0.5f; m_bed = RoadConstants.SeaLevel - depth; }
        public override float GetHeight(float x, float z) => Mathf.Abs(x - m_cx) < m_half ? m_bed : 31f;
        public override Heightmap.Biome GetBiome(float x, float z) => Heightmap.Biome.Swamp;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    private static List<Vector2> Line(float cx) =>
        Enumerable.Range(-6, 13).Select(i => new Vector2(cx + i * 8f, 0f)).ToList();

    [Fact]
    public void ASwampPoolTheRoadWalksIsAFordWithDryBanks()
    {
        bool was = RoadCrossingDetector.Shallows;
        try
        {
            RoadCrossingDetector.Shallows = true;
            var world = new SwampPool(0f, 16f, 0.5f);
            var c = Assert.Single(RoadCrossingDetector.Detect(Line(0f), world, bridges: true, fords: true));
            Assert.Equal(CrossingKind.Ford, c.Kind);
            Assert.True(c.Shallow);
            Assert.Contains(c.Style, new[] { FordStyle.Raise, FordStyle.Wade, FordStyle.Span });
            Assert.InRange(c.Width, 15f, 19f);
            Assert.True(world.GetHeight(c.FromBank.x, c.FromBank.y) >= RoadConstants.SeaLevel, "from bank in the water");
            Assert.True(world.GetHeight(c.ToBank.x, c.ToBank.y) >= RoadConstants.SeaLevel, "to bank in the water");
            Assert.True(c.FromIndex < c.ToIndex);
        }
        finally { RoadCrossingDetector.Shallows = was; }
    }

    [Fact]
    public void PoolsComeInAMixOfRaisedWadedAndSpanned()
    {
        // A third raised, a third waded, a third on a short span. Short, shallow pools
        // allow all three; the site hash picks.
        var counts = new Dictionary<FordStyle, int>();
        for (int k = 0; k < 90; k++)
        {
            float cx = k * 157f;
            var c = Assert.Single(RoadCrossingDetector.Detect(Line(cx), new SwampPool(cx, 12f, 0.4f), bridges: true, fords: true));
            counts[c.Style] = counts.TryGetValue(c.Style, out int n) ? n + 1 : 1;
        }
        foreach (var s in new[] { FordStyle.Raise, FordStyle.Wade, FordStyle.Span })
            Assert.True(counts.TryGetValue(s, out int n) && n >= 18, $"{s}: {(counts.TryGetValue(s, out int m) ? m : 0)} of 90");
    }

    [Fact]
    public void ALongOrDeepPoolIsInvalidInEveryStyle()
    {
        // One rule for every style: as far as a river ford reaches (48 m), no deeper than 3 m of
        // fill. Past either the crossing is INVALID and the road must go round.
        for (int k = 0; k < 30; k++)
        {
            float cx = k * 211f;
            var longPool = Assert.Single(RoadCrossingDetector.Detect(Line(cx), new SwampPool(cx, 60f, 0.4f), bridges: true, fords: true));
            Assert.True(longPool.Invalid, "a 60 m pool");
            var deep = Assert.Single(RoadCrossingDetector.Detect(Line(cx), new SwampPool(cx, 12f, 3.5f), bridges: true, fords: true));
            Assert.True(deep.Invalid, "a 3.5 m deep pool");
            var metre = Assert.Single(RoadCrossingDetector.Detect(Line(cx), new SwampPool(cx, 12f, 1.5f), bridges: true, fords: true));
            Assert.False(metre.Invalid, "a 1.5 m deep pool: a wade is built up to wading depth");
            var fine = Assert.Single(RoadCrossingDetector.Detect(Line(cx), new SwampPool(cx, 30f, 0.6f), bridges: true, fords: true));
            Assert.False(fine.Invalid, "a 30 m pool 0.6 m deep");
        }
    }

    [Fact]
    public void AValidPoolMayBeWadedWhateverItsLengthUnderTheRule()
    {
        int waded = 0;
        for (int k = 0; k < 60; k++)
        {
            float cx = k * 173f;
            var c = Assert.Single(RoadCrossingDetector.Detect(Line(cx), new SwampPool(cx, 40f, 0.6f), bridges: true, fords: true));
            if (c.Style == FordStyle.Wade) waded++;
        }
        Assert.True(waded >= 10, $"{waded} of 60 forty-metre pools waded");
    }

    [Fact]
    public void APuddleIsNotACrossing()
    {
        Assert.Empty(RoadCrossingDetector.Detect(Line(0f), new SwampPool(0f, 3f, 0.3f), bridges: true, fords: true));
    }

    [Fact]
    public void WithShallowsOffAPoolIsWadedAsBefore()
    {
        bool was = RoadCrossingDetector.Shallows;
        try
        {
            RoadCrossingDetector.Shallows = false;
            Assert.Empty(RoadCrossingDetector.Detect(Line(0f), new SwampPool(0f, 16f, 0.5f), bridges: true, fords: true));
        }
        finally { RoadCrossingDetector.Shallows = was; }
    }
    /// <summary>A round pool of the given radius and depth at (cx, cz) in a swamp at 31 m.</summary>
    private sealed class RoundPool : WorldGenerator
    {
        private readonly Vector2 m_c; private readonly float m_r, m_bed;
        public RoundPool(Vector2 c, float r, float depth) { m_c = c; m_r = r; m_bed = RoadConstants.SeaLevel - depth; }
        public override float GetHeight(float x, float z) => (new Vector2(x, z) - m_c).magnitude < m_r ? m_bed : 31f;
        public override Heightmap.Biome GetBiome(float x, float z) => Heightmap.Biome.Swamp;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    [Fact]
    public void APoolOffTheHammersHeadingsStillGetsSpansTurnedOntoOne()
    {
        // A road at 30 degrees (between two of the hammer's 22.5 degree headings): a straight deck
        // could never be repaired, so spans were almost never eligible (measured: 4 of 270).
        var dir = new Vector2(Mathf.Sin(30f * Mathf.PI / 180f), Mathf.Cos(30f * Mathf.PI / 180f));
        int spans = 0;
        for (int k = 0; k < 90; k++)
        {
            var c0 = new Vector2(k * 211f, k * 97f);
            var path = Enumerable.Range(-6, 13).Select(i => c0 + dir * (i * 8f)).ToList();
            var world = new RoundPool(c0, 8f, 0.6f);
            var c = Assert.Single(RoadCrossingDetector.Detect(path, world, bridges: true, fords: true));
            if (c.Style != FordStyle.Span) continue;
            spans++;
            Assert.True(BridgeLayout.HeadingIsPlaceable(BridgeLayout.YawDegrees(c.Direction)), $"span heading {BridgeLayout.YawDegrees(c.Direction):F1}");
            Assert.True(world.GetHeight(c.ToBank.x, c.ToBank.y) >= RoadConstants.SeaLevel, "far bank in the water");
        }
        Assert.True(spans >= 18, $"{spans} of 90 spanned");
    }
}
