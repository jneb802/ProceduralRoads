using System;
using System.Collections.Generic;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// <see cref="UnityRandom"/>, the copy of UnityEngine.Random, and the seeded slope turn built on it
/// (<see cref="FixedLocationYaw.SlopeDegrees"/>). Where the vectors come from: InitState, value and the two Range draws are the
/// station's game-world copy's (the gw overlay's UnityStandIns), which rebuilt the game's own world generator bit for bit on
/// 48.8M dump points; insideUnitCircle (an angle by Range(0, 2 pi), then a radius sqrt(Range(0, 1))) is the one formula of eight
/// tried that turned every slope-turned location of a tour's saved world as the game did (92 of 92, with the 104 turned at random:
/// 196 of 196 on the offline placer's yaw check, 25 Nov 2026); the next best turned 53. The numbers below are that copy's, pinned.
/// </summary>
public class UnityRandomTests
{
    [Fact]
    public void InitStateSeedsTheFourWordsAndXorshiftDrawsFromThem()
    {
        var random = new UnityRandom(0);
        uint[] words = { random.NextUInt(), random.NextUInt(), random.NextUInt(), random.NextUInt() };
        // By hand from InitState(0): x 0, y 1, z 1812433254, w 1812433253 * 1812433254 + 1 (mod 2^32), then t = x ^ (x << 11),
        // w' = w ^ (w >> 19) ^ t ^ (t >> 8).
        uint x = 0u, y = 1u, z = unchecked(1812433253u * y + 1u), w = unchecked(1812433253u * z + 1u);
        var expected = new List<uint>();
        for (int i = 0; i < 4; i++)
        {
            uint t = x ^ (x << 11);
            x = y; y = z; z = w;
            w = w ^ (w >> 19) ^ t ^ (t >> 8);
            expected.Add(w);
        }
        Assert.Equal(expected, words);
        Assert.Equal(Pinned.FirstWords, new[] { new UnityRandom(12345).NextUInt(), Second(12345) });
    }

    [Fact]
    public void ValueRangeAndInsideUnitCircleDrawAsUnitysDo()
    {
        var a = new UnityRandom(Pinned.Seed);
        var b = a;
        // value: the low 23 bits over 2^23 - 1; Range(float): t * min + (1 - t) * max, so Range(0, 1) is 1 - value.
        uint word = b.NextUInt();
        float value = a.Value();
        Assert.Equal((word & 0x7FFFFFu) * (1f / 8388607f), value);
        var c = new UnityRandom(Pinned.Seed);
        Assert.Equal(1f - value, c.Range(0f, 1f));
        // Range(int): min plus the word modulo the span (the random turn's Range(0, 16)).
        var d = new UnityRandom(Pinned.Seed);
        Assert.Equal((int)(word % 16u), d.Range(0, 16));

        var circle = new UnityRandom(Pinned.Seed);
        for (int i = 0; i < Pinned.Circle.Length; i++)
        {
            (float x, float y) = circle.InsideUnitCircle();
            Assert.Equal(Pinned.Circle[i], (x, y));
            Assert.True(x * x + y * y <= 1.000001f);
        }
    }

    [Fact]
    public void TheSlopeTurnSamplesWhereTheGameSampledAndHeadsFromTheHighestToTheLowest()
    {
        // A slope-turned location of the tour world (MountainCave02, exterior radius 15, zone (-146, 29)): the ten points the game's
        // GetTerrainDelta read there, seeded by FixedLocationYaw.Seed, as this copy draws them.
        var points = new List<(float x, float z)>();
        float Flat(float x, float z) { points.Add((x, z)); return 0f; }
        Assert.Equal(0f, FixedLocationYaw.SlopeDegrees(Pinned.WorldSeed, "MountainCave02", -146, 29, -9344f, 1856f, 15f, Flat));
        Assert.Equal(10, points.Count);
        Assert.Equal(Pinned.CavePoints, points.ToArray());
        foreach (var (x, z) in points) Assert.True(Math.Sqrt((x + 9344.0) * (x + 9344.0) + (z - 1856.0) * (z - 1856.0)) <= 15.001);

        // The first point the highest, the sixth the lowest, the rest between: the heading from the first to the sixth, in 22.5s.
        int read = 0;
        float Ground(float x, float z) { int i = read++; return i == 0 ? 10f : i == 5 ? -10f : 0f; }
        float yaw = FixedLocationYaw.SlopeDegrees(Pinned.WorldSeed, "MountainCave02", -146, 29, -9344f, 1856f, 15f, Ground);
        double heading = Math.Atan2(points[5].x - points[0].x, points[5].z - points[0].z) * 180.0 / Math.PI;
        Assert.Equal((float)((Math.Round((heading + 360.0) % 360.0 / 22.5) * 22.5) % 360.0), yaw);

        // Equal heights: the first of them is kept, so a second, equally high point turns nothing.
        read = 0;
        float Twice(float x, float z) { int i = read++; return i == 0 || i == 3 ? 10f : i == 5 ? -10f : 0f; }
        Assert.Equal(yaw, FixedLocationYaw.SlopeDegrees(Pinned.WorldSeed, "MountainCave02", -146, 29, -9344f, 1856f, 15f, Twice));
    }

    [Fact]
    public void AHeadingThatRoundsToAFullTurnIsNoTurn()
    {
        // A slope falling due north (+z) everywhere: every heading is near 0 or 360 and comes out as 0, never 360.
        for (int zone = 0; zone < 50; zone++)
        {
            float yaw = FixedLocationYaw.SlopeDegrees(Pinned.WorldSeed, "MountainCave02", zone, 0, 0f, 0f, 15f, (x, z) => -z * 100f + x);
            Assert.InRange(yaw, 0f, 337.5f);
        }
    }

    private static uint Second(int seed) { var r = new UnityRandom(seed); r.NextUInt(); return r.NextUInt(); }

    /// <summary>The copy's own draws, pinned (see the class summary for why they are Unity's).</summary>
    private static class Pinned
    {
        public const int WorldSeed = 92285445;
        public const int Seed = 12345;
        public static readonly uint[] FirstWords = { 692788716u, 3673367756u };
        public static readonly (float, float)[] Circle = { (-0.27089357f, 0.1644874f), (-0.28466707f, 0.058982298f), (-0.036831293f, -0.75735015f) };
        public static readonly (float, float)[] CavePoints =
        {
            (-9347.52f, 1849.7306f), (-9352.658f, 1861.2454f), (-9341.148f, 1850.6322f), (-9340.571f, 1851.6704f), (-9348.498f, 1852.2147f),
            (-9332.856f, 1858.5332f), (-9356.927f, 1854.9524f), (-9338.715f, 1865.5704f), (-9336.273f, 1850.5062f), (-9335.212f, 1854.1268f),
        };
    }
}
