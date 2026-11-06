using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>The zone index and the blended ground over zone borders and negative coordinates stay as they were.</summary>
public class ZoneMathTests
{
    private sealed class Mixed : WorldGenerator
    {
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            ((int)Mathf.Floor(wx / 37f) + 2 * (int)Mathf.Floor(wy / 29f)) % 3 == 0 ? Heightmap.Biome.Swamp
            : ((int)Mathf.Floor(wx / 37f) + (int)Mathf.Floor(wy / 29f)) % 2 == 0 ? Heightmap.Biome.Meadows : Heightmap.Biome.Plains;
        public override float GetBiomeHeight(Heightmap.Biome biome, float wx, float wy, out Color mask, bool preGeneration = false, bool riverPreDN = true)
        {
            mask = default;
            return (biome == Heightmap.Biome.Swamp ? 29f : biome == Heightmap.Biome.Meadows ? 36.3f : 41.7f) + 0.013f * wx - 0.007f * wy;
        }
        public override float GetHeight(float wx, float wy) => GetBiomeHeight(GetBiome(wx, wy), wx, wy, out _);
    }

    private static IEnumerable<float> Coordinates()
    {
        foreach (float b in new[] { -192f, -128f, -64f, 0f, 64f, 128f, 192f })
            foreach (float d in new[] { -32.001f, -32f, -31.999f, -0.5f, 0f, 0.25f, 31.999f, 32f, 32.001f })
                yield return b + d;
        for (float x = -150.3f; x < 150f; x += 7.77f) yield return x;
    }

    /// <summary>Recorded before the zone arithmetic was folded: a hash of every blended height's bits.</summary>
    [Fact]
    public void BlendedGroundIsBitForBitAsRecorded()
    {
        var world = new Mixed();
        uint hash = 2166136261u;
        foreach (float x in Coordinates())
            foreach (float z in Coordinates())
                hash = (hash ^ (uint)System.BitConverter.SingleToInt32Bits(BiomeBlendedHeight.GetBlendedHeight(x, z, world))) * 16777619u;
        Assert.Equal(2273996284u, hash);
    }

    [Fact]
    public void ZoneIndexIsTheGamesZone()
    {
        foreach (float x in Coordinates())
            Assert.Equal(ZoneSystem.GetZone(new Vector3(x, 0f, 0f)).x, RoadConstants.ZoneIndex(x));
    }

    [Fact]
    public void TheRoadGridIsZoneAligned() => Assert.Equal(RoadConstants.ZoneSize, RoadConstants.SpatialGridSize);
}

/// <summary>Grid index math shared by the search and its caches.</summary>
public class GridMathTests
{
    [Theory]
    [InlineData(32)]
    [InlineData(64)]
    public void FloorDivRoundsDown(int size)
    {
        for (int i = -300; i <= 300; i++)
            Assert.Equal((int)System.Math.Floor(i / (double)size), SharedTerrain.FloorDiv(i, size));
    }

    [Fact]
    public void CellCentreScalesByTheCellSize()
    {
        foreach (var c in new[] { new Vector2i(0, 0), new Vector2i(-3, 7), new Vector2i(125, -126) })
            Assert.Equal(new Vector2(c.x * RoadPathfinder.CellSize, c.y * RoadPathfinder.CellSize), RoadPathfinder.CellCentre(c));
    }
}

public class CrossingSpanTests
{
    /// <summary>Recorded before the spans were named: a ford jump is 48 m and a bridge jump 128 m.</summary>
    [Fact]
    public void SpansAreAsRecorded()
    {
        Assert.Equal(48f, RoadConstants.MaxFordSpan);
        Assert.Equal(128f, RoadConstants.MaxBridgeSpan);
        Assert.Equal(128f + 2f * RoadConstants.SwampBridgeDryReach, RoadNetworkPersistence.MaxCrossingSpan);
    }
}
