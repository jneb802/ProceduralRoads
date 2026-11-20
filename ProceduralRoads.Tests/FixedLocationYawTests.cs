using System;
using System.Linq;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// PROCEDURALROADS_FIXED_LOCATION_YAW (<see cref="FixedLocationYaw"/>): a location placed twice in one zone of one world is
/// turned the same way; zones and locations are turned over every step of the circle with no pattern between neighbours;
/// the game's draw is still made, and without a fixed turn it is what the location takes.
/// </summary>
public class FixedLocationYawTests
{
    /// <summary>Any fixed seed. The even-share band below is about three standard deviations wide, so about one seed in
    /// thirty falls outside it by chance; this one is inside every band.</summary>
    private const int WorldSeed = 42;

    [Fact]
    public void ALocationPlacedTwiceInOneZoneIsTurnedTheSameWay()
    {
        Assert.Equal(FixedLocationYaw.Degrees(WorldSeed, "Crypt2", 3, -7), FixedLocationYaw.Degrees(WorldSeed, "Crypt2", 3, -7));
        Assert.Equal(FixedLocationYaw.Seed(WorldSeed, "Crypt2", 3, -7), FixedLocationYaw.Seed(WorldSeed, "Crypt2", 3, -7));
        float yaw = FixedLocationYaw.Degrees(WorldSeed, "Crypt2", 3, -7);
        Assert.InRange(yaw, 0f, 337.5f);
        Assert.Equal(0f, yaw % FixedLocationYaw.StepDegrees);
    }

    [Fact]
    public void ZonesAndLocationsAreTurnedOverTheWholeCircle()
    {
        var zones = (from x in Enumerable.Range(-20, 40) from y in Enumerable.Range(-20, 40) select (x, y)).ToList();
        int[] count = new int[FixedLocationYaw.Steps];
        foreach (var (x, y) in zones) count[FixedLocationYaw.Step(WorldSeed, "Crypt2", x, y)]++;
        double even = zones.Count / (double)FixedLocationYaw.Steps;
        // Every step, each within 30 % of an even share (100 of 1600).
        foreach (int c in count) Assert.InRange(c, (int)(even * 0.7), (int)(even * 1.3));

        // No pattern between neighbours, nor between two locations in one zone: as often alike as chance (1 in 16).
        int besideAlike = zones.Count(z => FixedLocationYaw.Step(WorldSeed, "Crypt2", z.x, z.y) == FixedLocationYaw.Step(WorldSeed, "Crypt2", z.x + 1, z.y));
        int aboveAlike = zones.Count(z => FixedLocationYaw.Step(WorldSeed, "Crypt2", z.x, z.y) == FixedLocationYaw.Step(WorldSeed, "Crypt2", z.x, z.y + 1));
        int otherAlike = zones.Count(z => FixedLocationYaw.Step(WorldSeed, "Crypt2", z.x, z.y) == FixedLocationYaw.Step(WorldSeed, "TrollCave02", z.x, z.y));
        int worldAlike = zones.Count(z => FixedLocationYaw.Step(WorldSeed, "Crypt2", z.x, z.y) == FixedLocationYaw.Step(WorldSeed + 1, "Crypt2", z.x, z.y));
        foreach (int alike in new[] { besideAlike, aboveAlike, otherAlike, worldAlike }) Assert.InRange(alike, (int)(even * 0.6), (int)(even * 1.4));
    }

    [Fact]
    public void TheGamesDrawIsAlwaysMadeAndTakenWithoutAFixedTurn()
    {
        int calls = 0;
        int Random(int min, int max) { calls++; Assert.Equal((0, 16), (min, max)); return 11; }

        Assert.Equal(11, FixedLocationYaw.Draw(0, 16, null, Random));
        Assert.Equal(1, calls);
        Assert.Equal(5, FixedLocationYaw.Draw(0, 16, 5, Random));
        Assert.Equal(2, calls);

        // Any other draw is the game's, fixed turn or not.
        Assert.Equal(42, FixedLocationYaw.Draw(0, 99999, 5, (_, _) => 42));
    }

    [Fact]
    public void TheSwitchIsOffUnlessSet()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PROCEDURALROADS_" + FixedLocationYaw.Switch))) return;
        Assert.False(FixedLocationYaw.On);
    }
}
