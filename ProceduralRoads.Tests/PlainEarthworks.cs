using ProceduralRoads;

namespace ProceduralRoads.Tests;

/// <summary>
/// Turns the earthwork noise and the fill spread off for a test that pins
/// the cross-section's own geometry (a fixed blend margin), and puts the
/// defaults back afterwards. The noise and spread have their own tests.
/// </summary>
internal static class PlainEarthworks
{
    private const float DefaultAmplitude = 1f, DefaultFillSpread = 1f;

    internal static void Begin()
    {
        RoadEarthworkNoise.Amplitude = 0f;
        RoadEarthworkNoise.FillSpread = 0f;
    }

    internal static void End()
    {
        RoadEarthworkNoise.Amplitude = DefaultAmplitude;
        RoadEarthworkNoise.FillSpread = DefaultFillSpread;
    }
}
