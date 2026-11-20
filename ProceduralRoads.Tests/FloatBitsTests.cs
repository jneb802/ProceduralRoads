using Xunit;
namespace ProceduralRoads.Tests;

/// <summary>FloatBits.Abs gives System.Math.Abs's bits (what Mathf.Abs returns) for every kind of float, and FloatBits.ToInt
/// BitConverter's bits.</summary>
public class FloatBitsTests
{
    private static int B(float f) => System.BitConverter.ToInt32(System.BitConverter.GetBytes(f), 0);
    private static float F(int bits) => System.BitConverter.ToSingle(System.BitConverter.GetBytes(bits), 0);

    [Fact]
    public void AbsIsMathAbsToTheBit()
    {
        var random = new System.Random(9);
        var cases = new System.Collections.Generic.List<float>
        {
            0f, -0f, 1f, -1f, float.Epsilon, -float.Epsilon, float.MaxValue, float.MinValue, float.PositiveInfinity,
            float.NegativeInfinity, float.NaN, F(unchecked((int)0xFFC00001)), F(0x7F800001), F(unchecked((int)0x80000001)),
        };
        for (int i = 0; i < 100000; i++) cases.Add(F(random.Next() ^ (random.Next(2) << 31)));
        foreach (float f in cases) Assert.Equal(B(System.Math.Abs(f)), B(FloatBits.Abs(f)));
        Assert.Equal(System.Math.Abs(-7), FloatBits.Abs(-7));
        Assert.Throws<System.OverflowException>(() => FloatBits.Abs(int.MinValue));
    }

    [Fact]
    public void ToIntIsBitConvertersBits()
    {
        var random = new System.Random(10);
        var cases = new System.Collections.Generic.List<float>
        {
            0f, -0f, 1f, -1f, float.Epsilon, -float.Epsilon, float.MaxValue, float.MinValue, float.PositiveInfinity,
            float.NegativeInfinity, float.NaN, F(unchecked((int)0xFFC00001)), F(unchecked((int)0x80000001)),
        };
        for (int i = 0; i < 100000; i++) cases.Add(F(random.Next() ^ (random.Next(2) << 31)));
        foreach (float f in cases) Assert.Equal(B(f), FloatBits.ToInt(f));
    }
}
