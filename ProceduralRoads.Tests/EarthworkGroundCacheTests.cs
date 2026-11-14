using System;
using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The earthwork cap reads the ground before roads at every heightmap vertex a road's surface covers
/// (RoadNetworkGenerator.EarthworkAcross), for every road it measures and measures again: at whole metres it is read through
/// the 1 m planning lattice (RoadStairRuns.GroundAtVertex), each vertex once a thread until the ground changes. The lattice is
/// exact (the same ground to the bit, so the same verdicts) and is forgotten wherever the ground before roads can change: the
/// locations' ground (PlacedLocations) and their footprints (RoadLocationProtection), which say whose ground a point is in.
/// </summary>
public sealed class EarthworkGroundCacheTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public EarthworkGroundCacheTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
    }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    /// <summary>Rolling Meadows ground, no two vertices alike.</summary>
    private sealed class Rolling : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 60f + 0.4f * x + 3f * Mathf.Sin(x * 0.37f) + 2f * Mathf.Cos(z * 0.23f);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override int GetSeed() => 30;
    }

    /// <summary>A road winding over <see cref="Rolling"/>: a point every metre, at a height off the ground by
    /// <paramref name="lift"/> and a wave, so some points are past the cap and some are not.</summary>
    private static RoadSpatialGrid.PlannedPath Winding(WorldGenerator world, float lift, float phase)
    {
        var points = new List<Vector2>();
        var heights = new List<float>();
        for (int k = 0; k <= 60; k++)
        {
            float t = k * 0.1f + phase;
            var p = new Vector2(-30f + k + 0.37f, 8f * Mathf.Sin(t) + 0.21f);
            points.Add(p);
            heights.Add(world.GetHeight(p.x, p.y) + lift + 4f * Mathf.Sin(t * 2.3f));
        }
        return new RoadSpatialGrid.PlannedPath(points, heights, new List<RoadPointDebugInfo>(), RoadConstants.DefaultRoadWidth, 60f, false);
    }

    /// <summary>The same measurement with the ground read straight from its owner every time
    /// (PlacedLocations.GroundAt, uncached) and through the lattice: the same deepest cut or fill, the same most fill and the same
    /// points past the cap, to the bit; measured again, every vertex comes from the lattice.</summary>
    [Fact]
    public void TheLatticeGivesTheSameVerdictsAsTheGroundItself()
    {
        var world = new Rolling();
        _scope.WithWorld(world);
        Func<Vector2, float> direct = p => PlacedLocations.GroundAt(p.x, p.y, world);
        Func<Vector2, float> cached = RoadNetworkGenerator.GroundBeforeRoads(world);
        const float cap = 8f;
        int pastSeen = 0, withinSeen = 0;
        foreach (float lift in new[] { -6f, -2f, 0f, 3f, 6f })
            foreach (float phase in new[] { 0f, 1.3f })
            {
                RoadSpatialGrid.PlannedPath plan = Winding(world, lift, phase);
                var pastDirect = new List<Vector2>();
                var pastCached = new List<Vector2>();
                RoadNetworkGenerator.Earthwork a = RoadNetworkGenerator.EarthworkAcross(plan, direct, cap, pastDirect.Add);
                RoadNetworkGenerator.Earthwork b = RoadNetworkGenerator.EarthworkAcross(plan, cached, cap, pastCached.Add);
                Assert.Equal(a.Worst, b.Worst);
                Assert.Equal(a.Fill, b.Fill);
                Assert.Equal(a.Past, b.Past);
                Assert.Equal(pastDirect, pastCached);
                if (a.Past > 0) pastSeen++; else withinSeen++;
            }
        Assert.True(pastSeen > 0 && withinSeen > 0, $"{pastSeen} past, {withinSeen} within: the roads do not set both cases");

        // Every vertex, to the bit.
        for (int x = -32; x <= 32; x++)
            for (int z = -12; z <= 12; z++)
                Assert.Equal(direct(new Vector2(x, z)), cached(new Vector2(x, z)));

        // Measured again, no vertex is read from the ground.
        RoadSpatialGrid.PlannedPath again = Winding(world, 0f, 0f);
        var (_, readsBefore, knownBefore) = RoadNetworkGenerator.EarthworkReads;
        RoadNetworkGenerator.EarthworkAcross(again, cached, cap);
        var (_, readsAfter, knownAfter) = RoadNetworkGenerator.EarthworkReads;
        Assert.True(readsAfter > readsBefore);
        Assert.Equal(readsAfter - readsBefore, knownAfter - knownBefore);
    }

    /// <summary>The lattice is keyed by the ground's generation (RoadStairRuns.GroundGeneration), which changes wherever the
    /// ground before roads can: a location's ground published or the locations made ready (PlacedLocations), and the footprints set or
    /// cleared (RoadLocationProtection.Set, Reset), which say whose ground a point is in.</summary>
    [Fact]
    public void TheLatticeIsForgottenWhenTheFootprintsChange()
    {
        int before = RoadStairRuns.GroundGeneration;
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(5f, 5f), 10f) });
        int set = RoadStairRuns.GroundGeneration;
        Assert.NotEqual(before, set);
        RoadLocationProtection.Reset();
        Assert.NotEqual(set, RoadStairRuns.GroundGeneration);
    }
}
