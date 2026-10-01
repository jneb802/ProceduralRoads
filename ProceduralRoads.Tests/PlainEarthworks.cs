using ProceduralRoads;
using Valheim.Testing;

namespace ProceduralRoads.Tests;

/// <summary>
/// Turns the earthwork noise and the fill spread off for a test that pins
/// the cross-section's own geometry (a fixed blend margin), until disposed,
/// which puts back the values they had. The noise and spread have their own tests.
/// </summary>
internal static class PlainEarthworks
{
    internal static StaticOverride Apply() =>
        StaticOverride.Set(() => RoadEarthworkNoise.Amplitude, 0f).And(() => RoadEarthworkNoise.FillSpread, 0f);
}
