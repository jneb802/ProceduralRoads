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
                hash = (hash ^ (uint)System.BitConverter.ToInt32(System.BitConverter.GetBytes(BiomeBlendedHeight.GetBlendedHeight(x, z, world)), 0)) * 16777619u;
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
    [InlineData(0, 32, 0)]
    [InlineData(31, 32, 0)]
    [InlineData(32, 32, 1)]
    [InlineData(-1, 32, -1)]
    [InlineData(-32, 32, -1)]
    [InlineData(-33, 32, -2)]
    [InlineData(-300, 64, -5)]
    [InlineData(300, 64, 4)]
    public void FloorDivRoundsDown(int i, int size, int expected) => Assert.Equal(expected, SharedTerrain.FloorDiv(i, size));

    [Fact]
    public void CellCentreScalesByTheCellSize()
    {
        Assert.Equal(new Vector2(0f, 0f), RoadPathfinder.CellCentre(new Vector2i(0, 0)));
        Assert.Equal(new Vector2(-24f, 56f), RoadPathfinder.CellCentre(new Vector2i(-3, 7)));
        Assert.Equal(new Vector2(1000f, -1008f), RoadPathfinder.CellCentre(new Vector2i(125, -126)));
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
        Assert.Equal(368f, RoadNetworkPersistence.MaxCrossingSpan);
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
    }
}

public class PastTerrainLimitTests
{
    [Theory]
    [InlineData(38f, 30f, 8f, false)]
    [InlineData(38.0001f, 30f, 8f, true)]
    [InlineData(22f, 30f, 8f, false)]
    [InlineData(21.999f, 30f, 8f, true)]
    [InlineData(30f, 30f, 0f, false)]
    [InlineData(500f, 30f, float.PositiveInfinity, false)]
    public void TheHeightAgainstTheGroundIsOneComparison(float height, float ground, float cap, bool past)
    {
        Assert.Equal(past, RoadNetworkGenerator.PastTerrainLimit(height, ground, cap));
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
    /// <summary>Recorded before ModelledHeight read the writer's own delta: a hash of every height's bits. Recorded again when
    /// the fit past a road's end took the road's own last stretch (RoadTerrainModifier.FitHeightAtVertex): the vertices with
    /// every reaching point on one side of them changed, no other.</summary>
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
                hash = (hash ^ (uint)System.BitConverter.ToInt32(System.BitConverter.GetBytes(RoadTerrainModifier.ModelledHeight(points, vertex, ground, fill)), 0)) * 16777619u;
            }
        }
        Assert.Equal(2890224176u, hash);
    }
}

public class RoadProfileExtentsTests
{
    [Theory]
    [InlineData(2f, 0.85f, 6f)]
    [InlineData(4f, 1.7f, 8f)]
    [InlineData(8f, 3.4f, 12f)]
    public void PaintEdgeAndFootprintAreAsTheyWere(float w, float paintEdge, float footprint)
    {
        Assert.Equal(paintEdge, RoadProfile.PaintEdge(w));
        Assert.Equal(footprint, RoadProfile.Footprint(w));
    }
}

public class EaseLengthTests
{
    [Theory]
    [InlineData(8f, 0.35f, 22.857f)]
    [InlineData(7.3f, 0.35f, 20.857f)]
    [InlineData(8f, 0f, 800f)]
    [InlineData(3.1f, 0.123f, 25.203f)]
    public void TheEaseIsTheRiseOverTheGradeFlooredAtOneHundredth(float rise, float grade, float ease)
    {
        // The zero row is the floor of RoadGrade.Floored's divisor, the one zero cap the suite sets: only EaseLength is
        // called under it, and the configured cap is put back at once (no road is planned with it).
        using var keep = StaticOverride.Keep(() => RoadGrade.Configured);
        RoadGrade.Configured = grade;
        Assert.Equal(ease, RoadGrade.EaseLength(rise), 3);
    }
}
