using System.Collections.Generic;
using System;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>The cell hash behind the meander field and a ford's style: the one owner's outputs, pinned.</summary>
public class CellHashTests
{
    private static readonly MethodInfo SiteHash = typeof(RoadCrossingDetector).GetMethod("SiteHash", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo Lattice = typeof(RoadPathfinder).GetMethod("Lattice", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static float L(int x, int y) => (float)Lattice.Invoke(null, new object[] { x, y })!;
    private static int S(int x, int y) => (int)SiteHash.Invoke(null, new object[] { new Vector2(x, y) })!;

    [Fact]
    public void LatticeAndSiteHashKeepTheirOutputsAtAFewCells()
    {
        Assert.Equal(0f, L(0, 0));
        Assert.Equal(0.06101835f, L(1, -1));
        Assert.Equal(0.5049684f, L(-300, 7));
        Assert.Equal(0.097429045f, L(123456, -98765));
        Assert.Equal(0, S(0, 0));
        Assert.Equal(1276092134, S(1, -1));
        Assert.Equal(729892251, S(-300, 7));
        Assert.Equal(1142485276, S(123456, -98765));
    }

    /// <summary>Recorded from the two hashes as they were written before they shared RoadPathfinder.HashCell: a hash of every
    /// output over zero, +-1, +-7, thousands, the int extremes and 40 seeded random coordinates, squared.</summary>
    [Fact]
    public void LatticeAndSiteHashAreBitForBitAsRecordedOverAGrid()
    {
        var xs = new List<int>();
        foreach (int v in new[] { 0, 1, -1, 2, -2, 7, -7, 1000, -1000, 9999, -10000, 123456789, -123456789, int.MaxValue, int.MinValue, int.MaxValue - 1, int.MinValue + 1 })
            xs.Add(v);
        var rng = new System.Random(20261106);
        for (int i = 0; i < 40; i++) xs.Add(rng.Next(int.MinValue, int.MaxValue));
        uint lattice = 2166136261u, site = 2166136261u;
        foreach (int x in xs)
            foreach (int y in xs)
            {
                lattice = (lattice ^ (uint)BitConverter.ToInt32(BitConverter.GetBytes(L(x, y)), 0)) * 16777619u;
                // SiteHash rounds a float centre; every float below 2^24 is exact.
                if (Math.Abs((long)x) < (1 << 24) && Math.Abs((long)y) < (1 << 24)) site = (site ^ (uint)S(x, y)) * 16777619u;
            }
        Assert.Equal(1730020358u, lattice);
        Assert.Equal(287448893u, site);
    }
}
