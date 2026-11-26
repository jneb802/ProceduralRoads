// Roads' part of the Mathf double (Valheim.Testing.Doubles): the angle conversions the staircase and
// placed-location code use, as Unity defines them.
namespace UnityEngine;

public partial struct Mathf
{
    public const float Deg2Rad = PI / 180f;
    public const float Rad2Deg = 180f / PI;

    /// <summary>Unity's Repeat: <paramref name="t"/> wrapped into 0 to <paramref name="length"/>.</summary>
    public static float Repeat(float t, float length) => Clamp(t - (float)System.Math.Floor(t / length) * length, 0f, length);

    /// <summary>Unity's DeltaAngle: the shortest turn from <paramref name="current"/> to <paramref name="target"/>, in -180 to 180 degrees.</summary>
    public static float DeltaAngle(float current, float target)
    {
        float delta = Repeat(target - current, 360f);
        if (delta > 180f) delta -= 360f;
        return delta;
    }
}
