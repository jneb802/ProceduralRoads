using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

public partial class SitesFirstTests
{
    /// <summary>Level ground with two islets, deep sea from 30 to 60 m out round (-120, -120) and round (120, -120).</summary>
    private sealed class TwoIslets : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            float a = new Vector2(wx + 120f, wy + 120f).magnitude, b = new Vector2(wx - 120f, wy + 120f).magnitude;
            return Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f || (a > 30f && a < 60f) || (b > 30f && b < 60f) ? 0f : 50f;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    /// <summary>A frost cave at <paramref name="centre"/> facing +x, on <paramref name="world"/>'s ground.</summary>
    private static PlacedSite CaveAt(WorldGenerator world, Vector2 centre)
    {
        var record = new PlacedSiteRecord
        {
            Prefab = "MountainCave02", Centre = centre, Yaw = 90f, Root = 50f, RockRadius = 0f,
            Ops = new List<LevelOp>(CaveFloor), X0 = Mathf.RoundToInt(centre.x) - 60, Z0 = Mathf.RoundToInt(centre.y) - 60, W = 121, H = 121,
        };
        Func<float, float, float> ground = PlacedSiteGround.Sampler(record.Ground((x, z) => world.GetHeight(x, z)), record.X0, record.Z0, record.W, record.H);
        return new PlacedSite { Record = record, Ground = ground, Shape = PlacedSiteShape.Of(record.Centre, record.Yaw, CaveFloor[0], 0f, null, ground, false, record.Root) };
    }

    /// <summary>A failed ring search is kept against the places it belongs to: a road between two placed places, one of them
    /// on an islet, that found no route round either ring marks the pair, not the start alone. Marked on its start, the
    /// mainland cave lost its ring search for every later road to it.</summary>
    [Fact]
    public void AFailedRingSearchBetweenTwoPlacedPlacesIsKeptAgainstThePair()
    {
        var world = new TwoIslets();
        _scope.WithWorld(world);
        Vector2 mainland = new Vector2(0f, 100f), islet = new Vector2(-120f, -120f);
        PlacedSites.Ready(CaveAt(world, mainland));
        PlacedSites.Ready(CaveAt(world, islet));
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(mainland, Footprint), new RoadSiteProtection.Footprint(islet, Footprint) });
        LocationLevelling.Source = c => (c - mainland).magnitude < 0.5f || (c - islet).magnitude < 0.5f ? CaveFloor : null;
        SetPathfinder(new RoadPathfinder(world) { Stairs = false, TerrainVarianceThreshold = 1000f, Meander = 0f });
        RoadNetworkGenerator.RingTurnSearches = 0;

        Assert.False(RoadNetworkGenerator.GenerateRoad(mainland, 10f, islet, 10f, 4f, "mainland to islet"));
        int first = RoadNetworkGenerator.RingTurnSearches;
        Assert.True(first > 0);
        // The same pair again: not searched round again.
        Assert.False(RoadNetworkGenerator.GenerateRoad(mainland, 10f, islet, 10f, 4f, "again"));
        Assert.Equal(first, RoadNetworkGenerator.RingTurnSearches);
        // From the other islet to the mainland cave: its ring is still searched round.
        Assert.False(RoadNetworkGenerator.GenerateRoad(new Vector2(120f, -120f), 0f, mainland, 10f, 4f, "from the other islet"));
        Assert.True(RoadNetworkGenerator.RingTurnSearches > first, "the mainland cave lost its ring search to the islet's failure");
    }
}
