using Valheim.Testing;
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

public class EarthworkLimitReadingTests
{
    /// <summary>At the default cap and with none, shaping is limited to the game's level-delta clamp: what the sway and the
    /// stair apron used to write as a literal 8.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(8f)]
    [InlineData(20f)]
    public void ShapedLimitIsTheGamesClamp(float cap)
    {
        using var keep = StaticOverride.Keep(() => RoadNetworkGenerator.EarthworkCap);
        RoadNetworkGenerator.EarthworkCap = cap;
        Assert.Equal(8f, RoadNetworkGenerator.ShapedLimit);
        Assert.Equal(RoadConstants.TerrainDeltaMax, RoadNetworkGenerator.ShapedLimit);
    }
}

public class PastTerrainLimitTests
{
    [Theory]
    [InlineData(38f, 30f, 8f)]
    [InlineData(38.0001f, 30f, 8f)]
    [InlineData(22f, 30f, 8f)]
    [InlineData(21.999f, 30f, 8f)]
    [InlineData(30f, 30f, 0f)]
    [InlineData(500f, 30f, float.PositiveInfinity)]
    public void TheHeightAgainstTheGroundIsOneComparison(float height, float ground, float cap)
    {
        Assert.Equal(Mathf.Abs(height - ground) > cap, RoadNetworkGenerator.PastTerrainLimit(height, ground, cap));
    }
}

public class NamedDefaultsTests
{
    /// <summary>Recorded before the defaults were named: a game runs with a 4 m road, an 8 m earthwork cap and 1.5 m stair cuts.</summary>
    [Fact]
    public void DefaultsAreAsRecorded()
    {
        Assert.Equal(4f, RoadConstants.DefaultRoadWidth);
        Assert.Equal(8f, RoadNetworkGenerator.DefaultEarthworkCap);
        Assert.Equal(1.5f, StairLayout.CutWidth);
    }
}

public class ModelledHeightRecordedTests
{
    /// <summary>Recorded before ModelledHeight read the writer's own delta: a hash of every height's bits.</summary>
    [Fact]
    public void ModelledHeightIsBitForBitAsRecorded()
    {
        var random = new System.Random(4711);
        float Next(float lo, float hi) => lo + (float)random.NextDouble() * (hi - lo);
        uint hash = 2166136261u;
        for (int round = 0; round < 6; round++)
        {
            var points = new List<RoadSpatialGrid.RoadPoint>();
            var at = new Vector2(Next(-500f, 500f), Next(-500f, 500f));
            for (int i = 0; i < 60; i++)
            {
                points.Add(new RoadSpatialGrid.RoadPoint(at, Next(2f, 10f), Next(10f, 70f), paintOnly: i % 13 == 0));
                at += new Vector2(Next(-1.5f, 1.5f), Next(-1.5f, 1.5f));
            }
            float[]? fill = round % 2 == 0 ? null : new float[points.Count];
            if (fill != null) for (int i = 0; i < fill.Length; i++) fill[i] = i % 3 == 0 ? Next(0f, 4f) : 0f;
            for (int v = 0; v < 400; v++)
            {
                var vertex = points[random.Next(points.Count)].p + new Vector2(Next(-14f, 14f), Next(-14f, 14f));
                float ground = Next(0f, 80f);
                hash = (hash ^ (uint)System.BitConverter.SingleToInt32Bits(RoadTerrainModifier.ModelledHeight(points, vertex, ground, fill))) * 16777619u;
            }
        }
        Assert.Equal(4142811402u, hash);
    }
}

public class RoadProfileExtentsTests
{
    [Theory]
    [InlineData(2f)]
    [InlineData(3.3f)]
    [InlineData(4f)]
    [InlineData(7.77f)]
    [InlineData(10f)]
    public void PaintEdgeAndFootprintAreAsTheyWere(float w)
    {
        Assert.Equal(w * 0.5f * 0.85f, RoadProfile.PaintEdge(w));
        Assert.Equal(w * 0.5f * RoadConstants.RoadPaintOuterRatio, RoadProfile.PaintEdge(w));
        Assert.Equal(w + 2f * 2f, RoadProfile.Footprint(w));
    }
}

public class EaseLengthTests
{
    [Theory]
    [InlineData(8f, 0.35f)]
    [InlineData(7.3f, 0.35f)]
    [InlineData(8f, 0f)]
    [InlineData(3.1f, 0.123f)]
    public void TwiceTheEaseIsTheEaseOfTwice(float rise, float grade)
    {
        using var keep = StaticOverride.Keep(() => RoadGrade.Configured);
        RoadGrade.Configured = grade;
        Assert.Equal(2f * rise / System.Math.Max(grade, 0.01f), 2f * RoadGrade.EaseLength(rise));
    }
}
