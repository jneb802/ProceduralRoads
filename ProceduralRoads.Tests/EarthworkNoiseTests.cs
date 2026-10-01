using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>The earthwork noise (vanilla's own detail on the faces), its width
/// wander, and the fill spread; the deck stays flat.</summary>
public class EarthworkNoiseTests
{
    private const int Width = 64;
    private static int Index(int x, int z) => (z + Width / 2) * (Width + 1) + (x + Width / 2);

    private static TerrainComp Causeway(float amplitude, float fillSpread = 0f)
    {
        float saved = RoadEarthworkNoise.Amplitude, savedSpread = RoadEarthworkNoise.FillSpread;
        try
        {
            RoadEarthworkNoise.Amplitude = amplitude; RoadEarthworkNoise.FillSpread = fillSpread;
            WorldGenerator.instance = new SyntheticWorld { HasRiver = false, HasMountain = false };
            var zone = new Vector2s(0, 0);
            Heightmap hm = Heightmap.CreateForZone(zone, Width);
            Heightmap.Registered = hm;
            TerrainComp tc = hm.m_terrainComp!;
            float natural = BiomeBlendedHeight.GetBlendedHeight(0f, 0f, WorldGenerator.instance);
            var points = new List<RoadSpatialGrid.RoadPoint>();
            for (float x = -40f; x <= 40f; x += 1f)
                points.Add(new RoadSpatialGrid.RoadPoint(new Vector2(x, 0f), 4f, natural + 4f));
            RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, points, hm, tc);
            return tc;
        }
        finally
        {
            RoadEarthworkNoise.Amplitude = saved; RoadEarthworkNoise.FillSpread = savedSpread;
            Heightmap.Registered = null; WorldGenerator.instance = null;
        }
    }

    [Fact]
    public void TheDeckStaysFlat()
    {
        // Within a centimetre: the wandering margin reweights the points
        // along the road, not the height they share.
        var off = Causeway(0f); var on = Causeway(1f);
        for (int x = -20; x <= 20; x++)
            foreach (int z in new[] { -1, 0, 1 })
                Assert.Equal(off.m_levelDelta[Index(x, z)], on.m_levelDelta[Index(x, z)], 2);
    }

    [Fact]
    public void TheSideSlopeVariesAlongTheRoad()
    {
        static float Spread(TerrainComp tc, int z)
        {
            var d = Enumerable.Range(-20, 41).Select(x => tc.m_levelDelta[Index(x, z)]).ToList();
            return d.Max() - d.Min();
        }
        var off = Causeway(0f); var on = Causeway(1f);
        Assert.True(Spread(off, 3) < 0.3f, $"off: {Spread(off, 3):F3}");
        Assert.True(Spread(on, 3) > Spread(off, 3) + 0.3f, $"on: {Spread(on, 3):F3} off: {Spread(off, 3):F3}");
    }

    [Fact]
    public void TheDetailIsVanillasScaleAndSmoothOnTheVertexGrid()
    {
        float lo = float.MaxValue, hi = float.MinValue, jump = 0f;
        for (int i = 0; i < 4000; i++)
        {
            float x = i * 3.71f - 7000f, z = i * -1.93f + 1200f;
            float p = RoadEarthworkNoise.Perlin01(x * 0.1f, z * 0.1f);
            Assert.InRange(p, 0f, 1f);
            float d = RoadEarthworkNoise.Detail(x, z, false);
            lo = Mathf.Min(lo, d); hi = Mathf.Max(hi, d);
            // One vertex (1 m) apart never differs by more than the fine octave
            // can make it: no spikes on the grid.
            jump = Mathf.Max(jump, Mathf.Abs(d - RoadEarthworkNoise.Detail(x + 1f, z, false)));
        }
        Assert.True(hi - lo <= 2.6f + 1e-3f, $"range {hi - lo:F2}");
        Assert.True(hi - lo > 1f, $"range {hi - lo:F2}: too flat to be vanilla's detail");
        Assert.True(jump < 1.0f, $"1 m jump {jump:F2}");
        Assert.True(RoadEarthworkNoise.Detail(5f, 5f, true) != RoadEarthworkNoise.Detail(5f, 5f, false));
    }

    [Fact]
    public void FillSpreadsOnlyOverFlatGround()
    {
        float saved = RoadEarthworkNoise.FillSpread, savedAmplitude = RoadEarthworkNoise.Amplitude;
        try
        {
            RoadEarthworkNoise.FillSpread = 1f;
            // 4 m of fill on flat ground: 3 m of extra reach.
            Assert.Equal(3f, RoadEarthworkNoise.FillExtra(new Vector2(0, 0), 14f, (x, z) => 10f), 3);
            // The same fill across a 30% sidehill: nothing, no apron.
            Assert.Equal(0f, RoadEarthworkNoise.FillExtra(new Vector2(0, 0), 14f, (x, z) => 10f + 0.3f * z));
            // Cut, or fill under 1 m: nothing.
            Assert.Equal(0f, RoadEarthworkNoise.FillExtra(new Vector2(0, 0), 6f, (x, z) => 10f));
            Assert.Equal(0f, RoadEarthworkNoise.FillExtra(new Vector2(0, 0), 10.8f, (x, z) => 10f));
            Assert.True(RoadTerrainModifier.GatherRadius(4f) >= RoadTerrainModifier.MaxInfluenceRadius(4f) + RoadEarthworkNoise.FillSpreadMax - 1e-4f);
            // With neither the spread nor the noise the gather pads by the influence radius alone.
            RoadEarthworkNoise.FillSpread = 0f; RoadEarthworkNoise.Amplitude = 0f;
            Assert.Equal(RoadTerrainModifier.MaxInfluenceRadius(4f), RoadTerrainModifier.GatherRadius(4f));
        }
        finally { RoadEarthworkNoise.FillSpread = saved; RoadEarthworkNoise.Amplitude = savedAmplitude; }
    }

    [Fact]
    public void AFlatCausewayGetsGentlerSides()
    {
        var off = Causeway(0f); var spread = Causeway(0f, fillSpread: 1f);
        // 5 m out is past the plain reach (2 m half-width + 2 m margin) but
        // inside the spread one, so it is raised only with the fill spread.
        Assert.Equal(0f, off.m_levelDelta[Index(0, 5)]);
        Assert.True(spread.m_levelDelta[Index(0, 5)] > 0.2f, $"{spread.m_levelDelta[Index(0, 5)]:F2}");
    }
}
