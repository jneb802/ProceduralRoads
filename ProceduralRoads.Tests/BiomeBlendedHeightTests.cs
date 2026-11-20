using System.Collections.Generic;
using System.Linq;
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

    /// <summary>While a generation's workers run (BiomeBlendedHeight.CornerGrid open for the world), a reader without a corner
    /// memo gets the same heights to the bit as reading every corner from the world, each corner read from the world once;
    /// a corner past the grid's reach, and any other world, are read from the world each time; once the workers end, the
    /// corners are read from the world again (a world changed since is read as it is now).</summary>
    [Fact]
    public void TheWorkersCornerGridGivesTheSameHeightsToTheBitReadingEachCornerOnce()
    {
        var world = new PatchWorld();
        var random = new System.Random(11);
        var points = new List<Vector2>();
        for (int i = 0; i < 20000; i++) points.Add(new Vector2((float)(random.NextDouble() * 2000 - 1000), (float)(random.NextDouble() * 2000 - 1000)));
        foreach (float x in new[] { -32f, 32f, 31.999f, 32.001f, -1056f, 992f })
            foreach (float z in new[] { -32f, 96f, 95.999f, -96.001f })
                points.Add(new Vector2(x, z));
        var plain = points.Select(p => BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world)).ToList();
        long plainReads = world.BiomeReads;
        Assert.Equal(4L * points.Count, plainReads);
        var other = new PatchWorld();
        var grid = BiomeBlendedHeight.CornerGrid.Begin(world);
        try
        {
            world.BiomeReads = 0;
            for (int i = 0; i < points.Count; i++)
                Assert.Equal(plain[i], BiomeBlendedHeight.GetBlendedHeight(points[i].x, points[i].y, world));
            int Heightmap(float w) => Mathf.FloorToInt((w + RoadConstants.HalfZoneSize) / RoadConstants.ZoneSize);
            long corners = points.Select(p => (Heightmap(p.x), Heightmap(p.y)))
                .SelectMany(h => new[] { h, (h.Item1 + 1, h.Item2), (h.Item1, h.Item2 + 1), (h.Item1 + 1, h.Item2 + 1) }).Distinct().Count();
            Assert.Equal(corners, world.BiomeReads);
            // Asked again: nothing read.
            foreach (var p in points.Take(100)) BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world);
            Assert.Equal(corners, world.BiomeReads);
            // Past the reach: read from the world, the same height.
            float far = (BiomeBlendedHeight.CornerGrid.Reach + 5) * RoadConstants.ZoneSize;
            long before = world.BiomeReads;
            float farHeight = BiomeBlendedHeight.GetBlendedHeight(far, 10f, world);
            Assert.Equal(before + 4, world.BiomeReads);
            Assert.Equal(farHeight, BiomeBlendedHeight.GetBlendedHeight(far, 10f, world, new BiomeBlendedHeight.Corners()));
            // Another world: its own corners, each time.
            BiomeBlendedHeight.GetBlendedHeight(100f, 100f, other);
            Assert.Equal(4L, other.BiomeReads);
        }
        finally { BiomeBlendedHeight.CornerGrid.End(grid); }
        world.BiomeReads = 0;
        BiomeBlendedHeight.GetBlendedHeight(points[0].x, points[0].y, world);
        Assert.Equal(4L, world.BiomeReads);
    }

    /// <summary>With LowMemoryGeneration on the workers hold no corner grid: every corner is read from the world.</summary>
    [Fact]
    public void LowMemoryGenerationHoldsNoCornerGrid()
    {
        var world = new PatchWorld();
        bool low = GenerationMemory.Low;
        GenerationMemory.Low = true;
        BiomeBlendedHeight.CornerGrid? grid = null;
        try
        {
            grid = BiomeBlendedHeight.CornerGrid.Begin(world);
            Assert.Null(grid);
            BiomeBlendedHeight.GetBlendedHeight(100f, 100f, world);
            BiomeBlendedHeight.GetBlendedHeight(100f, 100f, world);
            Assert.Equal(8L, world.BiomeReads);
        }
        finally { BiomeBlendedHeight.CornerGrid.End(grid); GenerationMemory.Low = low; }
    }

    /// <summary>While a generation's workers run (BiomeBlendedHeight.BeginHeights), a point read again is answered with the
    /// height read the first time, to the bit, without asking the world; points in the same slot replace each other; before
    /// the opening, after the closing, for another world and with LowMemoryGeneration on, the world is asked each time.</summary>
    [Fact]
    public void TheWorkersHeightMemoGivesTheSameHeightsToTheBitAskingTheWorldOnce()
    {
        var world = new PatchWorld();
        var random = new System.Random(5);
        var points = new List<Vector2>();
        for (int i = 0; i < 100000; i++) points.Add(new Vector2((float)(random.NextDouble() * 4000 - 2000), (float)(random.NextDouble() * 4000 - 2000)));
        points.Add(new Vector2(0f, 0f)); points.Add(new Vector2(-0f, 0f)); points.Add(new Vector2(32f, -32f));
        var plain = new List<float>();
        foreach (var p in points) plain.Add(BiomeBlendedHeight.GetBlendedHeight(p.x, p.y, world));
        var window = BiomeBlendedHeight.BeginHeights(world);
        try
        {
            for (int round = 0; round < 2; round++)
                for (int i = 0; i < points.Count; i++)
                    Assert.Equal(Bits(plain[i]), Bits(BiomeBlendedHeight.GetBlendedHeight(points[i].x, points[i].y, world)));
            world.BiomeReads = 0;
            BiomeBlendedHeight.GetBlendedHeight(points[points.Count - 1].x, points[points.Count - 1].y, world);
            Assert.Equal(0L, world.BiomeReads);
            var other = new PatchWorld();
            BiomeBlendedHeight.GetBlendedHeight(points[points.Count - 1].x, points[points.Count - 1].y, other);
            BiomeBlendedHeight.GetBlendedHeight(points[points.Count - 1].x, points[points.Count - 1].y, other);
            Assert.Equal(8L, other.BiomeReads);
        }
        finally { BiomeBlendedHeight.EndHeights(window); }
        world.BiomeReads = 0;
        BiomeBlendedHeight.GetBlendedHeight(points[points.Count - 1].x, points[points.Count - 1].y, world);
        Assert.Equal(4L, world.BiomeReads);
        bool low = GenerationMemory.Low;
        GenerationMemory.Low = true;
        BiomeBlendedHeight.HeightsWindow? lowWindow = null;
        try
        {
            lowWindow = BiomeBlendedHeight.BeginHeights(world);
            world.BiomeReads = 0;
            BiomeBlendedHeight.GetBlendedHeight(5f, 5f, world);
            BiomeBlendedHeight.GetBlendedHeight(5f, 5f, world);
            Assert.Equal(8L, world.BiomeReads);
        }
        finally { BiomeBlendedHeight.EndHeights(lowWindow); GenerationMemory.Low = low; }
    }

    private static int Bits(float f) => System.BitConverter.ToInt32(System.BitConverter.GetBytes(f), 0);

    /// <summary>A world double changed between two generations' workers is read as it is now: the memo is forgotten at each
    /// opening.</summary>
    [Fact]
    public void TheHeightMemoForgetsAWorldChangedBetweenOpenings()
    {
        var world = new ShiftWorld();
        var first = BiomeBlendedHeight.BeginHeights(world);
        try { Assert.Equal(1f, BiomeBlendedHeight.GetBlendedHeight(10f, 10f, world)); }
        finally { BiomeBlendedHeight.EndHeights(first); }
        world.Ground = 2f;
        var second = BiomeBlendedHeight.BeginHeights(world);
        try { Assert.Equal(2f, BiomeBlendedHeight.GetBlendedHeight(10f, 10f, world)); }
        finally { BiomeBlendedHeight.EndHeights(second); }
    }

    /// <summary>A cancelled run that ends after the next run opened its windows closes only its own: the next run keeps its
    /// corner grid and its height memo until it ends itself.</summary>
    [Fact]
    public void ARunThatEndsAfterTheNextBeginsClosesOnlyItsOwnWindows()
    {
        var world = new PatchWorld();
        // The height memo alone (no corner grid open, so each read the world answers asks four corner biomes).
        var heightsA = BiomeBlendedHeight.BeginHeights(world);
        var heightsB = BiomeBlendedHeight.BeginHeights(world);
        try
        {
            BiomeBlendedHeight.EndHeights(heightsA);
            world.BiomeReads = 0;
            BiomeBlendedHeight.GetBlendedHeight(123.5f, -77.25f, world);
            BiomeBlendedHeight.GetBlendedHeight(123.5f, -77.25f, world);
            Assert.Equal(4L, world.BiomeReads);
        }
        finally { BiomeBlendedHeight.EndHeights(heightsB); }
        world.BiomeReads = 0;
        BiomeBlendedHeight.GetBlendedHeight(123.5f, -77.25f, world);
        Assert.Equal(4L, world.BiomeReads);
        // The corner grid alone.
        var gridA = BiomeBlendedHeight.CornerGrid.Begin(world);
        var gridB = BiomeBlendedHeight.CornerGrid.Begin(world);
        try
        {
            Assert.NotNull(gridB);
            BiomeBlendedHeight.CornerGrid.End(gridA);
            Assert.Same(gridB, BiomeBlendedHeight.CornerGrid.For(world));
        }
        finally { BiomeBlendedHeight.CornerGrid.End(gridB); }
        Assert.Null(BiomeBlendedHeight.CornerGrid.For(world));
    }

    /// <summary>A world whose four heightmap corners round (0, 0) are the biomes it is given, and which counts its biome
    /// height asks.</summary>
    private sealed class CornerWorld : WorldGenerator
    {
        public Heightmap.Biome[] Corners = new Heightmap.Biome[4];
        public long HeightAsks, BiomeReads;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false)
        {
            BiomeReads++;
            return Corners[(wx > 0f ? 1 : 0) + (wy > 0f ? 2 : 0)];
        }
        public override float GetBiomeHeight(Heightmap.Biome biome, float wx, float wy, out Color mask, bool preGeneration = false, bool riverPreDN = true)
        {
            HeightAsks++;
            mask = default;
            return (int)biome % 1000 * 1.3f + wx * 0.11f - wy * 0.05f;
        }
        public override float GetHeight(float wx, float wy) => 0f;
    }

    /// <summary>The blend asks the world for each distinct corner biome's height once, and gives the bilinear blend of
    /// all four corners' heights, to the bit, for every way the four corners can share biomes.</summary>
    [Fact]
    public void ABorderPointAsksEachDistinctCornerBiomeOnce()
    {
        var biomes = new[] { Heightmap.Biome.Meadows, Heightmap.Biome.Swamp, Heightmap.Biome.Mountain, Heightmap.Biome.BlackForest };
        var world = new CornerWorld();
        int patterns = 0;
        for (int a = 0; a < 4; a++)
            for (int b = 0; b < 4; b++)
                for (int c = 0; c < 4; c++)
                    for (int d = 0; d < 4; d++)
                    {
                        world.Corners = new[] { biomes[a], biomes[b], biomes[c], biomes[d] };
                        int distinct = world.Corners.Distinct().Count();
                        foreach (var (x, z) in new[] { (3.5f, -7.25f), (-20f, 20f), (0.5f, 0.5f), (-31f, 31.5f) })
                        {
                            world.HeightAsks = 0;
                            float height = BiomeBlendedHeight.GetBlendedHeight(x, z, world);
                            Assert.Equal(distinct, world.HeightAsks);
                            // Every corner's height asked, blended as the game blends them.
                            float H(int i) => world.GetBiomeHeight(world.Corners[i], x, z, out _);
                            float S(float t) { t = Mathf.Clamp01(t); return t * t * (3f - 2f * t); }
                            float tx = S((x + 32f) / 2f / 32f), tz = S((z + 32f) / 2f / 32f);
                            float expected = distinct == 1 ? H(0) : Mathf.Lerp(Mathf.Lerp(H(0), H(1), tx), Mathf.Lerp(H(2), H(3), tx), tz);
                            Assert.Equal(Bits(expected), Bits(height));
                        }
                        patterns++;
                    }
        Assert.Equal(256, patterns);
    }

    /// <summary>A biome value with its top bit set (a mod's, past what the game declares) is not kept in the workers' corner
    /// grid, whose slots hold a biome shifted left by one: it is read from the world each time and comes back whole.</summary>
    [Fact]
    public void TheCornerGridDoesNotKeepATopBitBiome()
    {
        var top = (Heightmap.Biome)unchecked((int)0x80000000);
        var world = new CornerWorld { Corners = new[] { top, top, Heightmap.Biome.Meadows, Heightmap.Biome.Meadows } };
        float plain = BiomeBlendedHeight.GetBlendedHeight(5f, 5f, world);
        var grid = BiomeBlendedHeight.CornerGrid.Begin(world);
        try
        {
            Assert.NotNull(grid);
            world.BiomeReads = 0;
            Assert.Equal(Bits(plain), Bits(BiomeBlendedHeight.GetBlendedHeight(5f, 5f, world)));
            Assert.Equal(4L, world.BiomeReads);
            // Asked again: the two Meadows corners come from the grid, the two top-bit ones from the world.
            Assert.Equal(Bits(plain), Bits(BiomeBlendedHeight.GetBlendedHeight(5f, 5f, world)));
            Assert.Equal(6L, world.BiomeReads);
        }
        finally { BiomeBlendedHeight.CornerGrid.End(grid); }
    }

    private sealed class ShiftWorld : WorldGenerator
    {
        public float Ground = 1f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override float GetBiomeHeight(Heightmap.Biome biome, float wx, float wy, out Color mask, bool preGeneration = false, bool riverPreDN = true)
        { mask = default; return Ground; }
        public override float GetHeight(float wx, float wy) => Ground;
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
