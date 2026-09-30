using System.Collections.Generic;

namespace ProceduralRoads.TestFixtures;

// A declared input, not a recording of the writer's output. Compiled by both
// test layers; it has no game or toolkit dependencies and never calls RoadProfile.
public static class FlatRoadFixture
{
    public const string Id = "flat-road-two-zones-v1";
    public const int ZoneX = 200, ZoneZ = 200;
    public const float Origin = 12800f, Platform = 64f, Width = 4f;
    public static readonly float[] Targets = { 65f, 65f, 80f, 48f };
    public static readonly string[] Stages = { "raise", "repeat", "upper-clamp", "lower-clamp" };

    public static IEnumerable<(int x, int z)> Samples(int zoneOffset)
    {
        foreach (int x in zoneOffset == 0 ? new[] { 24, 32 } : new[] { 32, 40 })
            foreach (int z in new[] { 0, 2, 3, 4, 6 }) yield return (x, z);
    }

    // Width 4 has a 2 m flat half-width and a 2 m smoothstep release.
    // z=3 is halfway through the release: smoothstep(1/2)=1/2.
    // At and beyond z=4, the road has no influence. The writer/game limit is
    // +/-8 m. These values are independent of the implementation under test.
    public static float Expected(string stage, int z)
    {
        if (stage == "baseline" || z >= 4) return 64f;
        if (stage == "raise" || stage == "repeat") return z == 3 ? 64.5f : 65f;
        if (stage == "upper-clamp") return 72f;
        if (stage == "lower-clamp") return 56f;
        throw new System.ArgumentException("Unknown fixture stage.");
    }
}
