using System.Collections.Generic;
using System.Linq;
using System;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>The cell hash behind the meander field and a ford's style: the one owner gives what each of its two
/// callers computed before they shared it.</summary>
public class CellHashTests
{
    private static IEnumerable<int> Coordinates()
    {
        foreach (int v in new[] { 0, 1, -1, 2, -2, 7, -7, 1000, -1000, 9999, -10000, 123456789, -123456789, int.MaxValue, int.MinValue, int.MaxValue - 1, int.MinValue + 1 })
            yield return v;
        var rng = new System.Random(20261106);
        for (int i = 0; i < 40; i++) yield return rng.Next(int.MinValue, int.MaxValue);
    }

    [Fact]
    public void SiteHashAndLatticeKeepTheirOutputs()
    {
        var siteHash = typeof(RoadCrossingDetector).GetMethod("SiteHash", BindingFlags.NonPublic | BindingFlags.Static)!;
        var lattice = typeof(RoadPathfinder).GetMethod("Lattice", BindingFlags.NonPublic | BindingFlags.Static)!;
        var xs = Coordinates().ToArray();
        foreach (int x in xs)
            foreach (int y in xs)
            {
                // The two hashes as they were written before they shared RoadPathfinder.HashCell.
                int oldSite;
                float oldLattice;
                unchecked
                {
                    uint h = (uint)(x * 374761393 + y * 668265263);
                    h = (h ^ (h >> 13)) * 1274126177u;
                    oldSite = (int)((h ^ (h >> 16)) & 0x7FFFFFFF);
                    uint l = (uint)x * 374761393u + (uint)y * 668265263u;
                    l = (l ^ (l >> 13)) * 1274126177u;
                    l ^= l >> 16;
                    oldLattice = (l & 0xFFFFFF) / (float)0xFFFFFF;
                }
                Assert.Equal(oldLattice, (float)lattice.Invoke(null, new object[] { x, y })!);
                // SiteHash rounds a float centre; every float below 2^24 is exact.
                if (Math.Abs((long)x) < (1 << 24) && Math.Abs((long)y) < (1 << 24))
                    Assert.Equal(oldSite, (int)siteHash.Invoke(null, new object[] { new Vector2(x, y) })!);
            }
    }
}
