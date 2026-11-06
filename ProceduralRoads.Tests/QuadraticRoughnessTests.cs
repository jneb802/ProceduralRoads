using System;
using Xunit;
namespace ProceduralRoads.Tests;

/// <summary>The roughness fit with its constant elimination worked out once per ring size gives, to the bit, what solving
/// the whole normal system per sample set gives (the solve as it was, kept here as the reference).</summary>
public class QuadraticRoughnessTests
{
    private static float Solved(float centre, float[] ring)
    {
        int n = ring.Length + 1;
        var rows = new double[n][];
        var h = new double[n];
        rows[0] = new double[] { 1, 0, 0, 0, 0, 0 }; h[0] = centre;
        for (int i = 0; i < ring.Length; i++)
        {
            double angle = i * Math.PI * 2.0 / ring.Length;
            double x = Math.Cos(angle), y = Math.Sin(angle);
            rows[i + 1] = new double[] { 1, x, y, x * x, x * y, y * y };
            h[i + 1] = ring[i];
        }
        var m = new double[6, 7];
        for (int r = 0; r < n; r++)
            for (int i = 0; i < 6; i++)
            {
                for (int j = 0; j < 6; j++) m[i, j] += rows[r][i] * rows[r][j];
                m[i, 6] += rows[r][i] * h[r];
            }
        for (int col = 0; col < 6; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < 6; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col])) pivot = r;
            if (Math.Abs(m[pivot, col]) < 1e-9) continue;
            for (int j = 0; j < 7; j++) { double t = m[col, j]; m[col, j] = m[pivot, j]; m[pivot, j] = t; }
            for (int r = 0; r < 6; r++)
            {
                if (r == col) continue;
                double f = m[r, col] / m[col, col];
                for (int j = col; j < 7; j++) m[r, j] -= f * m[col, j];
            }
        }
        var c = new double[6];
        for (int i = 0; i < 6; i++) c[i] = Math.Abs(m[i, i]) < 1e-9 ? 0 : m[i, 6] / m[i, i];
        double lo = double.MaxValue, hi = double.MinValue;
        for (int r = 0; r < n; r++)
        {
            double fit = 0; for (int i = 0; i < 6; i++) fit += c[i] * rows[r][i];
            double res = h[r] - fit;
            lo = Math.Min(lo, res); hi = Math.Max(hi, res);
        }
        return (float)(hi - lo);
    }

    private static int Bits(float f) => BitConverter.ToInt32(BitConverter.GetBytes(f), 0);

    [Theory]
    [InlineData(8)]
    [InlineData(6)]
    [InlineData(12)]
    public void TheFitGivesTheSameBitsAsTheWholeSolve(int ringLength)
    {
        var random = new Random(ringLength);
        for (int k = 0; k < 20000; k++)
        {
            float centre = (float)(random.NextDouble() * 400 - 50);
            var ring = new float[ringLength];
            // Smooth, stepped and wild rings, and some flat ones (residuals of exactly zero).
            for (int i = 0; i < ringLength; i++)
                ring[i] = k % 7 == 0 ? centre : centre + (float)((random.NextDouble() - 0.5) * (k % 3 == 0 ? 0.5 : k % 3 == 1 ? 20 : 200));
            Assert.Equal(Bits(Solved(centre, ring)),
                Bits(RoadTerrainSamples.QuadraticRoughness(centre, ring, 16f)));
        }
        // Back and forth between ring sizes: the cached fit follows the size asked for.
        Assert.Equal(Solved(1f, new float[] { 2, 3, 4, 5, 6, 7, 8, 9 }), RoadTerrainSamples.QuadraticRoughness(1f, new float[] { 2, 3, 4, 5, 6, 7, 8, 9 }, 16f));
    }
}
