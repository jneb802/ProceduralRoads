using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The height the mod plans on must be the height the game renders. Inside a
/// heightmap whose four corners share a biome, HeightmapBuilder builds every
/// vertex from THAT biome's formula - not from the biome at the vertex. A
/// patch of another biome smaller than a zone (a Black Forest tongue inside
/// Meadows) is therefore rendered at Meadows height, and a road planned on
/// WorldGenerator.GetHeight there was levelled to the forest formula instead:
/// a raised hump over ground that did not rise.
/// </summary>
public class BiomeBlendedHeightTests
{
    private const float MeadowsGround = 40f;
    private const float ForestGround = 46f;

    /// <summary>Two flat biome formulas, and a Black Forest tongue that lies
    /// inside the heightmap of zone (512,-128) without touching any of its
    /// corners (480..544 by -160..-96). GetHeight behaves as the game's does:
    /// the formula of the biome at the point.</summary>
    private sealed class TongueWorld : WorldGenerator
    {
        public bool Tongue = true;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            Tongue && wx >= 500f && wx <= 530f && wy >= -150f && wy <= -110f
                ? Heightmap.Biome.BlackForest : Heightmap.Biome.Meadows;
        public override float GetBiomeHeight(Heightmap.Biome biome, float wx, float wy, out Color mask, bool preGeneration = false, bool riverPreDN = true)
        {
            mask = default;
            return biome == Heightmap.Biome.BlackForest ? ForestGround : MeadowsGround;
        }
        public override float GetHeight(float wx, float wy) => GetBiomeHeight(GetBiome(wx, wy), wx, wy, out _);
    }

    [Fact]
    public void APatchBetweenFourLikeCornersIsRenderedAtTheCornersHeight()
    {
        var world = new TongueWorld();
        // The world generator reads the patch's own formula ...
        Assert.Equal(ForestGround, world.GetHeight(515f, -130f));
        // ... the rendered heightmap, and so the road, does not.
        Assert.Equal(MeadowsGround, BiomeBlendedHeight.GetBlendedHeight(515f, -130f, world), 3);
        var info = BiomeBlendedHeight.GetBlendDebugInfo(515f, -130f, world);
        Assert.False(info.IsBiomeBoundary);
        Assert.Equal(Heightmap.Biome.BlackForest, info.PointBiome);
        Assert.Equal(-(ForestGround - MeadowsGround), info.HeightDifference, 3);
    }

    [Fact]
    public void AHeightmapThatIsOneBiomeThroughoutIsUnchanged()
    {
        var world = new TongueWorld();
        foreach (var p in new[] { new Vector2(0f, 0f), new Vector2(470f, -130f), new Vector2(600f, -130f) })
            Assert.Equal(world.GetHeight(p.x, p.y), BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world), 3);
    }

    [Fact]
    public void CornersThatDisagreeStillBlendByPositionNotByThePointsBiome()
    {
        // A boundary through the corners: Black Forest from x = 544 east. The
        // heightmap of zone (512,-128) has Black Forest on its east corners and
        // blends across, whatever the biome at the point itself.
        var world = new EastForest();
        float wx = 520f, wy = -128f;
        float t = (wx - 480f) / 64f;
        float tx = t * t * (3f - 2f * t);
        float expected = Mathf.Lerp(MeadowsGround, ForestGround, tx);
        Assert.Equal(expected, BiomeBlendedHeight.GetBlendedHeight(wx, wy, world), 3);
    }

    private sealed class EastForest : WorldGenerator
    {
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            wx >= 544f ? Heightmap.Biome.BlackForest : Heightmap.Biome.Meadows;
        public override float GetBiomeHeight(Heightmap.Biome biome, float wx, float wy, out Color mask, bool preGeneration = false, bool riverPreDN = true)
        {
            mask = default;
            return biome == Heightmap.Biome.BlackForest ? ForestGround : MeadowsGround;
        }
        public override float GetHeight(float wx, float wy) => GetBiomeHeight(GetBiome(wx, wy), wx, wy, out _);
    }

    [Fact]
    public void ARoadAcrossAPatchStaysOnTheRenderedGround()
    {
        // End to end: the road crosses the tongue on ground the game renders
        // flat at 40 m. It must not be lifted onto the forest formula's 46 m.
        var world = new TongueWorld();
        using var scope = new ValheimWorldScope().WithWorld(world);
        RoadSpatialGrid.Clear();
        try
        {
            var path = new List<Vector2>();
            for (float x = 300f; x <= 740f; x += 8f)
                path.Add(new Vector2(x, -130f));
            RoadSpatialGrid.AddRoadPath(path, 4f, world);

            var points = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(515f, 0f, -130f), 30f);
            Assert.True(points.Count > 10, "too few road points on the tongue");
            foreach (var rp in points)
                Assert.True(Mathf.Abs(rp.h - MeadowsGround) < 0.05f,
                    $"road point at x={rp.p.x:F1} is {rp.h - MeadowsGround:F2} m off the rendered ground");
        }
        finally
        {
            RoadSpatialGrid.Clear();
        }
    }

    /// <summary>A world of biome patches smaller and larger than a zone, each biome its own sloping formula, counting
    /// its biome reads.</summary>
    private sealed class PatchWorld : WorldGenerator
    {
        public long BiomeReads;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false)
        {
            BiomeReads++;
            float v = Mathf.Sin(wx * 0.013f) + Mathf.Cos(wy * 0.021f) + Mathf.Sin((wx + wy) * 0.005f);
            return v < -0.8f ? Heightmap.Biome.Swamp : v < 0.4f ? Heightmap.Biome.Meadows : v < 1.4f ? Heightmap.Biome.BlackForest : Heightmap.Biome.Mountain;
        }
        public override float GetBiomeHeight(Heightmap.Biome biome, float wx, float wy, out Color mask, bool preGeneration = false, bool riverPreDN = true)
        {
            mask = default;
            return (int)biome * 3.7f + wx * 0.011f - wy * 0.007f;
        }
        public override float GetHeight(float wx, float wy) => GetBiomeHeight(GetBiome(wx, wy), wx, wy, out _);
    }

    /// <summary>The corner memo changes no height, to the bit: the last heightmap's corners, a column scan's, and a
    /// memo asked in a random order (every corner replaced over and over).</summary>
    [Fact]
    public void TheCornerMemoGivesTheSameHeightsToTheBit()
    {
        var world = new PatchWorld();
        var last = new BiomeBlendedHeight.Corners();
        var scan = BiomeBlendedHeight.Corners.ForColumnScan(1000f);
        var random = new System.Random(7);
        for (int i = 0; i < 20000; i++)
        {
            float x = (float)(random.NextDouble() * 2000 - 1000), z = (float)(random.NextDouble() * 2000 - 1000);
            float plain = BiomeBlendedHeight.GetBlendedHeight(x, z, world);
            Assert.Equal(plain, BiomeBlendedHeight.GetBlendedHeight(x, z, world, last));
            Assert.Equal(plain, BiomeBlendedHeight.GetBlendedHeight(x, z, world, scan));
        }
        // On the lattice lines themselves (a corner's own point, and either side of a heightmap's edge).
        foreach (float x in new[] { -32f, 32f, 31.999f, 32.001f, -1056f, 992f })
            foreach (float z in new[] { -32f, 96f, 95.999f, -96.001f })
                Assert.Equal(BiomeBlendedHeight.GetBlendedHeight(x, z, world), BiomeBlendedHeight.GetBlendedHeight(x, z, world, last));
    }

    /// <summary>
    /// The island scan's walk (down whole columns of 8 m cells) reads each heightmap corner's biome once with the column
    /// memo, against four reads per cell without it; the one-heightmap memo, a search's, saves most of them too. A
    /// focused measure of the island detector's terrain pass (its biome reads are what the blended ground added to it).
    /// </summary>
    [Fact]
    public void AColumnScanReadsEachCornerBiomeOnce()
    {
        const float radius = 512f, cell = RoadIslandDetector.SampleSpacing;
        int grid = Mathf.CeilToInt(radius * 2f / cell);
        long Scan(BiomeBlendedHeight.Corners? corners)
        {
            var world = new PatchWorld();
            for (int x = 0; x < grid; x++)
                for (int y = 0; y < grid; y++)
                    BiomeBlendedHeight.GetBlendedHeight(x * cell - radius, y * cell - radius, world, corners);
            return world.BiomeReads;
        }
        long plain = Scan(null), column = Scan(BiomeBlendedHeight.Corners.ForColumnScan(radius)), last = Scan(new BiomeBlendedHeight.Corners());
        Assert.Equal(4L * grid * grid, plain);
        // The heightmaps the cells fall in (each centred on a zone, 32 m either side), and one corner more than them a side.
        int Heightmap(float w) => Mathf.FloorToInt((w + RoadConstants.HalfZoneSize) / RoadConstants.ZoneSize);
        int corners = Heightmap((grid - 1) * cell - radius) - Heightmap(-radius) + 2;
        Assert.Equal((long)corners * corners, column);
        // Eight cells a heightmap down a column: its corners read once per column of cells, not once per cell.
        Assert.True(last <= plain / 8 + 4L * grid, $"{last} reads with the one-heightmap memo against {plain}");
    }

    /// <summary>A memo asked about another world forgets the first's corners.</summary>
    [Fact]
    public void TheCornerMemoForgetsAnotherWorldsCorners()
    {
        var corners = new BiomeBlendedHeight.Corners();
        var meadows = new TongueWorld { Tongue = false };
        var forest = new EastForest();
        Assert.Equal(MeadowsGround, BiomeBlendedHeight.GetBlendedHeight(600f, -128f, meadows, corners), 3);
        Assert.Equal(ForestGround, BiomeBlendedHeight.GetBlendedHeight(600f, -128f, forest, corners), 3);
    }
}
