using System;
using UnityEngine;
using Xunit;
namespace ProceduralRoads.Tests;

/// <summary>Roughness about a quadratic surface: curvature is smooth, breaks are rough.</summary>
public class QuadVarianceTests
{
    private sealed class Ground : WorldGenerator
    {
        private readonly Func<float, float, float> m_h;
        public Ground(Func<float, float, float> h) { m_h = h; }
        public override float GetHeight(float x, float z) => m_h(x, z);
        public override Heightmap.Biome GetBiome(float x, float z) => Heightmap.Biome.Mountain;
    }

    private static float VarianceAtOrigin(Func<float, float, float> h, bool quad) =>
        new RoadTerrainSamples(new Ground(h)) { QuadVariance = quad }.Variance(new Vector2i(0, 0));

    [Fact]
    public void AValleyFloorIsRoughToTheRawSpreadButSmoothToTheQuadratic()
    {
        // A U-shaped valley running north, walls rising 0.02 x^2, floor climbing 0.15.
        Func<float, float, float> valley = (x, z) => 100f + 0.02f * x * x + 0.15f * z;
        Assert.True(VarianceAtOrigin(valley, quad: false) > 5f);
        Assert.True(VarianceAtOrigin(valley, quad: true) < 0.01f);
    }

    [Fact]
    public void ABowlAndASlopeAreSmooth()
    {
        Assert.True(VarianceAtOrigin((x, z) => 50f + 0.01f * (x * x + z * z), quad: true) < 0.01f);
        Assert.True(VarianceAtOrigin((x, z) => 50f + 0.4f * x - 0.2f * z, quad: true) < 0.01f);
    }

    [Fact]
    public void ASpikeUnderOneSampleIsStillRough()
    {
        // An 8 m spike under the east ring sample only.
        Func<float, float, float> spike = (x, z) => (x > 12f && x < 20f && Mathf.Abs(z) < 4f) ? 48f : 40f;
        Assert.True(VarianceAtOrigin(spike, quad: true) > 5f, $"read {VarianceAtOrigin(spike, quad: true):F2}");
    }
}
