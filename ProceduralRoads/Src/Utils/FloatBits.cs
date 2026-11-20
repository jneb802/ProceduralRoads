using System.Runtime.InteropServices;

namespace ProceduralRoads;

/// <summary>
/// Exact float helpers that the game's runtime runs as native calls. <see cref="Abs(float)"/> is UnityEngine.Mathf.Abs to the
/// bit: Mathf.Abs calls System.Math.Abs, which on the game's Mono is a call out to native code (fabsf) that clears the sign
/// bit; here the sign bit is cleared in place, the same bits for every float (NaNs, both zeros, infinities, subnormals).
/// <see cref="ToInt(float)"/> is the one reader of a float's bits (BitConverter's, on every runtime the mod runs on, with no
/// allocation and no native call).
/// </summary>
internal static class FloatBits
{
    [StructLayout(LayoutKind.Explicit)]
    private struct Bits
    {
        [FieldOffset(0)] public float Float;
        [FieldOffset(0)] public int Int;
    }

    /// <summary>The bits of <paramref name="value"/> as an int: BitConverter.ToInt32(BitConverter.GetBytes(value), 0) on a
    /// little-endian machine, read in place.</summary>
    internal static int ToInt(float value) => new Bits { Float = value }.Int;

    /// <summary><c>Mathf.Abs(value)</c>, the same bits: the sign bit cleared.</summary>
    internal static float Abs(float value) => new Bits { Int = new Bits { Float = value }.Int & 0x7FFFFFFF }.Float;

    /// <summary><c>Mathf.Abs(value)</c> of an int (System.Math.Abs, which throws for int.MinValue as Mathf.Abs does).</summary>
    internal static int Abs(int value) => System.Math.Abs(value);
}
