using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
using Xunit.Abstractions;

namespace ProceduralRoads.Tests;

/// <summary>
/// No deck through the ground between a bridge's banks (RoadCrossingDetector.ClearOfGround; a
/// 103 m bridge ran through a ridge 12 m over its deck). The deck line is sampled every 1 m; where it meets ground over
/// the deck, the bridge ends at the first dry ground before the water, or, where the ground is in the water itself, the
/// crossing is refused (Invalid) and the generator goes round it.
/// </summary>
public class BridgeDeckLineTests
{
    private readonly ITestOutputHelper m_out;
    public BridgeDeckLineTests(ITestOutputHelper output) => m_out = output;

    /// <summary>A river across x, flat along y, its ground a piecewise-linear profile along x (metres, height pairs);
    /// river core over <c>riverFrom..riverTo</c>. In the Mistlands, as the bridge that ran through the ridge was.</summary>
    private sealed class ProfileWorld : WorldGenerator
    {
        private readonly float[] m_x, m_h;
        private readonly float m_riverFrom, m_riverTo;
        public ProfileWorld(float riverFrom, float riverTo, params float[] xh)
        {
            m_riverFrom = riverFrom; m_riverTo = riverTo;
            m_x = new float[xh.Length / 2]; m_h = new float[xh.Length / 2];
            for (int i = 0; i < m_x.Length; i++) { m_x[i] = xh[2 * i]; m_h[i] = xh[2 * i + 1]; }
        }
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wy) > 150f) return 20f;
            if (wx <= m_x[0]) return m_h[0];
            for (int i = 1; i < m_x.Length; i++)
                if (wx <= m_x[i]) return Mathf.Lerp(m_h[i - 1], m_h[i], (wx - m_x[i - 1]) / (m_x[i] - m_x[i - 1]));
            return m_h[m_h.Length - 1];
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            Heightmap.Biome.Mistlands;
        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            weight = wx >= m_riverFrom && wx <= m_riverTo ? 1f : 0f;
            width = weight > 0f ? m_riverTo - m_riverFrom : 0f;
        }
    }

    /// <summary>
    /// The case of a bridge deck line measured on RoadSeedA at (8045, 1485): a bank
    /// at 31.3 m, a shelf just above the water line, a land spur peaking 33.7 m (2.3 m over the 31.4 m deck) 20-36 m out,
    /// then the river proper to the far bank 88 m out. A ridge 12 m over the deck is the same shape taller.
    /// </summary>
    private static ProfileWorld RidgeBetweenBankAndWater(float ridge) => new(-4f, 92f,
        -40f, 31.6f, 0f, 31.3f, 2f, 30.1f, 8f, 30.5f, 13f, 29.7f, 16f, 31.0f, 22f, 32.5f, 25f, 32.0f,
        30f, 31.4f + ridge, 35f, 31.9f, 37f, 31.0f, 42f, 30.5f, 46f, 29.2f, 50f, 27.6f, 59f, 25.3f, 70f, 26.6f, 80f, 29.1f,
        86f, 30.7f, 88f, 31.4f, 92f, 31.6f, 140f, 31.6f);

    private static List<Vector2> Jump(float from, float to) => new() { new Vector2(from - 30f, 0f), new Vector2(from, 0f), new Vector2(to, 0f), new Vector2(to + 30f, 0f) };

    private void Describe(RoadCrossing c, WorldGenerator world) =>
        m_out.WriteLine(FormattableString($"{c.Kind}/{c.Style} {c.FromBank} -> {c.ToBank}, {c.Width:F1} m, deck {BridgeStructures.DeckHeight(c, world):F2}, invalid {c.Invalid}, fromLand {c.FromLand}, toLand {c.ToLand}"));

    private static string FormattableString(System.FormattableString s) => System.FormattableString.Invariant(s);

    [Theory]
    [InlineData(2.3f)]    // the bridge as measured
    [InlineData(12f)]     // a 12 m ridge
    public void ARidgeBetweenABankAndTheWaterEndsTheBridgeAtTheFirstDryGround(float ridge)
    {
        var world = RidgeBetweenBankAndWater(ridge);
        using (new ValheimWorldScope().WithWorld(world))
        {
            var c = Assert.Single(RoadCrossingDetector.Detect(Jump(-12f, 104f), world, bridges: true, fords: false));
            Describe(c, world);
            Assert.Equal(CrossingKind.Bridge, c.Kind);
            Assert.False(c.Invalid);
            // Clear of ground at every 1 m sample.
            Assert.Null(RoadCrossingDetector.GroundThroughDeck(c.FromBank, c.ToBank, BridgeStructures.DeckHeight(c, world), world));
            // The near end is past the ridge, on the last road ground before the river proper; the road takes the land.
            float near = Mathf.Min(c.FromBank.x, c.ToBank.x);
            Assert.InRange(near, 33f, 37f);
            Assert.True(RoadCrossingDetector.IsRoadGround(new Vector2(near, 0f), world));
            Assert.NotNull(c.FromBank.x < c.ToBank.x ? c.FromLand : c.ToLand);
        }
    }

    /// <summary>A bridge whose near end is walked in off land is still met by the road at its
    /// stairs' foot, on their line: the one owner of a structure's end (StructureEnds) brings the road there, not to the
    /// deck's end under the stairs.</summary>
    [Fact]
    public void ABridgeEndWalkedInOffLandIsMetAtItsStairsFoot()
    {
        var world = RidgeBetweenBankAndWater(2.3f);
        using (new ValheimWorldScope().WithWorld(world))
        {
            RoadSiteProtection.Set(System.Array.Empty<RoadSiteProtection.Footprint>());
            var c = Assert.Single(RoadCrossingDetector.Detect(Jump(-12f, 104f), world, bridges: true, fords: false));
            Assert.NotNull(c.FromBank.x < c.ToBank.x ? c.FromLand : c.ToLand);
            Assert.True(RoadNetworkGenerator.MeetsTheRoad(c));
        }
    }

    [Fact]
    public void GroundInTheWaterOverTheDeckRefusesTheCrossing()
    {
        // Rising banks both sides (so the deck springs from the tops at ~34.3 m) and a rock bar mid-river standing to
        // 37 m, under the 8 m of land that would split the crossing in two.
        var world = new ProfileWorld(-4f, 64f,
            -40f, 34.3f, -9f, 34.3f, 0f, 31.3f, 3f, 26f, 27f, 26f, 29f, 37f, 33f, 37f, 35f, 26f, 57f, 26f, 60f, 31.3f, 69f, 34.3f, 140f, 34.3f);
        using (new ValheimWorldScope().WithWorld(world))
        {
            var c = Assert.Single(RoadCrossingDetector.Detect(Jump(-12f, 72f), world, bridges: true, fords: false));
            Describe(c, world);
            Assert.Equal(CrossingKind.Bridge, c.Kind);
            Assert.True(c.Invalid);
            Assert.Equal(1, RoadNetworkGenerator.InvalidCrossings(new List<RoadCrossing> { c }, null));
        }
    }

    [Fact]
    public void APlainRiverIsUnchanged()
    {
        // The same banks with nothing between them but the river.
        var world = new ProfileWorld(-4f, 92f, -40f, 31.6f, 0f, 31.3f, 4f, 27f, 84f, 27f, 88f, 31.4f, 140f, 31.6f);
        using (new ValheimWorldScope().WithWorld(world))
        {
            var c = Assert.Single(RoadCrossingDetector.Detect(Jump(-12f, 104f), world, bridges: true, fords: false));
            Describe(c, world);
            Assert.False(c.Invalid);
            Assert.Null(c.FromLand);
            Assert.Null(c.ToLand);
            Assert.Null(RoadCrossingDetector.GroundThroughDeck(c.FromBank, c.ToBank, BridgeStructures.DeckHeight(c, world), world));
        }
    }
}
