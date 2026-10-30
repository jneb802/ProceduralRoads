using System;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Fixed-size terrain memo, held for the life of one pathfinder rather than
/// one search. Collisions replace a sample, never approximate it.
///
/// Only immutable world-generator facts belong here. Road sharing, crossing
/// decisions and configurable move costs do NOT: they change as roads are
/// committed, and a memo that outlives a search must not hold anything that
/// can go out of date within one.
///
/// The facts it does hold are true for as long as the generator it was built
/// for is the generator being asked, and a pathfinder is built at the start of
/// a generation and discarded at the end of it.
/// </summary>
internal sealed class RoadTerrainSamples
{
    // 1.75 MiB of value data per pathfinder, independent of explored area;
    // chosen by measurement.
    internal const int Capacity = 65536;
    private struct Sample
    {
        public Vector2i Position;
        public byte Flags;
        public float Height, River, Variance;
        public Heightmap.Biome Biome;
    }
    private readonly Sample[] samples = new Sample[Capacity];
    private readonly WorldGenerator world;
    public RoadTerrainSamples(WorldGenerator world) { this.world = world; }
    // How the memo is actually behaving, which a wall-clock second cannot say.
    // A memo belongs to one pathfinder and a pathfinder to one thread, so
    // these are plain fields: one add, on a line the caller already owns, and
    // no interlocking in the hot path. Hits and misses are counted per FACT
    // asked for, not per slot touched; a replacement is an occupied slot
    // evicted by a different position, which is what says the memo is too
    // small for the way it is being asked.
    internal long Hits, Misses, Replacements, TerrainCalls;
    /// <summary>Stopwatch ticks spent inside the world generator for this memo: the
    /// share of a search that is the game's terrain (and its river cache's lock),
    /// which in game cannot be told from the search's own work any other way.
    /// Counted only with PROCEDURALROADS_TIMINGS (<see cref="SearchStats.Enabled"/>).</summary>
    internal long TerrainTicks;
    private static long Now => SearchStats.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

    // Whole-generation totals. A memo is folded in when its owner is done,
    // so a pathfinder discarded mid-generation does not take its numbers with
    // it. Interlocked because several island workers fold at once.
    internal static long TotalHits, TotalMisses, TotalReplacements, TotalTerrainCalls, TotalTerrainTicks;

    internal static void ResetTotals()
    {
        SharedTerrain.Clear();
        System.Threading.Interlocked.Exchange(ref TotalHits, 0);
        System.Threading.Interlocked.Exchange(ref TotalMisses, 0);
        System.Threading.Interlocked.Exchange(ref TotalReplacements, 0);
        System.Threading.Interlocked.Exchange(ref TotalTerrainCalls, 0);
        System.Threading.Interlocked.Exchange(ref TotalTerrainTicks, 0);
    }

    internal void FoldIntoTotals()
    {
        System.Threading.Interlocked.Add(ref TotalHits, Hits);
        System.Threading.Interlocked.Add(ref TotalMisses, Misses);
        System.Threading.Interlocked.Add(ref TotalReplacements, Replacements);
        System.Threading.Interlocked.Add(ref TotalTerrainCalls, TerrainCalls);
        System.Threading.Interlocked.Add(ref TotalTerrainTicks, TerrainTicks);
        Hits = Misses = Replacements = TerrainCalls = TerrainTicks = 0;
    }

    private ref Sample At(Vector2i position)
    {
        int slot = unchecked((position.x * 73856093) ^ (position.y * 83492791)) & (Capacity - 1);
        ref Sample sample = ref samples[slot];
        if (sample.Position != position)
        {
            if (sample.Flags != 0) Replacements++;
            sample = default;
            sample.Position = position;
        }
        return ref sample;
    }
    private static Vector2 World(Vector2i p) => new(p.x * RoadPathfinder.CellSize, p.y * RoadPathfinder.CellSize);

    public float Height(Vector2i position)
    {
        ref Sample sample = ref At(position);
        if ((sample.Flags & 1) == 0)
        {
            Misses++;
            if (!SharedTerrain.TryHeight(world, position, out sample.Height))
            {
                TerrainCalls++;
                var p = World(position);
                long began = Now;
                sample.Height = world.GetHeight(p.x, p.y);
                TerrainTicks += Now - began;
                SharedTerrain.PutHeight(world, position, sample.Height);
            }
            sample.Flags |= 1;
        }
        else Hits++;
        return sample.Height;
    }
    public float River(Vector2i position)
    {
        ref Sample sample = ref At(position);
        if ((sample.Flags & 2) == 0)
        {
            Misses++;
            if (!SharedTerrain.TryRiver(world, position, out sample.River))
            {
                TerrainCalls++;
                var p = World(position);
                long began = Now;
                world.GetRiverWeight(p.x, p.y, out sample.River, out _);
                TerrainTicks += Now - began;
                SharedTerrain.PutRiver(world, position, sample.River);
            }
            sample.Flags |= 2;
        }
        else Hits++;
        return sample.River;
    }
    public Heightmap.Biome Biome(Vector2i position)
    {
        ref Sample sample = ref At(position);
        if ((sample.Flags & 4) == 0)
        {
            Misses++;
            if (!SharedTerrain.TryBiome(world, position, out sample.Biome))
            {
                TerrainCalls++;
                var p = World(position);
                long began = Now;
                sample.Biome = world.GetBiome(p.x, p.y);
                TerrainTicks += Now - began;
                SharedTerrain.PutBiome(world, position, sample.Biome);
            }
            sample.Flags |= 4;
        }
        else Hits++;
        return sample.Biome;
    }
    /// <summary>
    /// Measure roughness about the best QUADRATIC surface through the ring and
    /// the centre, so slope and curvature both read as smooth and only broken
    /// ground reads rough (a raw spread reads every smooth sidehill rough, a
    /// plane fit every trough). Off keeps the raw spread. Settable for tests.
    /// </summary>
    internal static bool DefaultQuadVariance = true;
    internal bool QuadVariance = DefaultQuadVariance;

    /// <summary>
    /// Spread of the residuals left by the least-squares fit of
    /// h = a + bx + cy + dx^2 + exy + fy^2 to the centre and the ring samples
    /// (nine points, six unknowns). A smooth trough, ridge, bowl or slope fits
    /// exactly; a step, spike or broken ground does not.
    /// </summary>
    internal static float QuadraticRoughness(float centre, float[] ring, float radius)
    {
        QuadraticFit fit = QuadraticFit.For(ring.Length);
        double[] h = t_heights ??= new double[16];
        if (h.Length < fit.Points) h = t_heights = new double[fit.Points];
        h[0] = centre;
        for (int i = 0; i < ring.Length; i++) h[i + 1] = ring[i];
        return fit.Roughness(h);
    }

    [ThreadStatic] private static double[]? t_heights;
    [ThreadStatic] private static double[]? t_rhs, t_coefficients;
    [ThreadStatic] private static float[]? t_ring;

    /// <summary>
    /// The least-squares fit of h = a + bx + cy + dx^2 + exy + fy^2 to the centre and a ring of n samples, solved by the
    /// normal equations A^T A c = A^T h and Gaussian elimination with partial pivoting. A^T A, and so every pivot and
    /// row factor, depends on the ring alone: they are worked out once per ring size, and each sample set replays the
    /// elimination on its right-hand side alone, operation for operation in the same order, so each residual comes out
    /// to the bit as when the whole system was solved per sample set (QuadraticRoughnessTests).
    /// </summary>
    private sealed class QuadraticFit
    {
        public readonly int Points;
        private readonly double[][] m_rows;
        private readonly double[] m_diagonal = new double[6];
        // Per eliminated column in order: the row swap (col, pivot), then the factor for every other row.
        private readonly int[] m_swapCol, m_swapPivot;
        private readonly double[][] m_factors;
        private static volatile QuadraticFit? s_last;

        public static QuadraticFit For(int ringLength)
        {
            QuadraticFit? fit = s_last;
            if (fit == null || fit.Points != ringLength + 1) s_last = fit = new QuadraticFit(ringLength);
            return fit;
        }

        private QuadraticFit(int ringLength)
        {
            Points = ringLength + 1;
            m_rows = new double[Points][];
            m_rows[0] = new double[] { 1, 0, 0, 0, 0, 0 };
            for (int i = 0; i < ringLength; i++)
            {
                double angle = i * Math.PI * 2.0 / ringLength;
                double x = Math.Cos(angle), y = Math.Sin(angle);   // in units of the radius
                m_rows[i + 1] = new double[] { 1, x, y, x * x, x * y, y * y };
            }
            var m = new double[6, 6];
            for (int r = 0; r < Points; r++)
                for (int i = 0; i < 6; i++)
                    for (int j = 0; j < 6; j++) m[i, j] += m_rows[r][i] * m_rows[r][j];
            var swapCol = new System.Collections.Generic.List<int>();
            var swapPivot = new System.Collections.Generic.List<int>();
            var factors = new System.Collections.Generic.List<double[]>();
            for (int col = 0; col < 6; col++)
            {
                int pivot = col;
                for (int r = col + 1; r < 6; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col])) pivot = r;
                if (Math.Abs(m[pivot, col]) < 1e-9) continue;   // x^2 and y^2 are dependent with the offset on a ring alone; the centre breaks that
                for (int j = 0; j < 6; j++) { double t = m[col, j]; m[col, j] = m[pivot, j]; m[pivot, j] = t; }
                var f = new double[6];
                for (int r = 0; r < 6; r++)
                {
                    if (r == col) continue;
                    f[r] = m[r, col] / m[col, col];
                    for (int j = col; j < 6; j++) m[r, j] -= f[r] * m[col, j];
                }
                swapCol.Add(col); swapPivot.Add(pivot); factors.Add(f);
            }
            m_swapCol = swapCol.ToArray(); m_swapPivot = swapPivot.ToArray(); m_factors = factors.ToArray();
            for (int i = 0; i < 6; i++) m_diagonal[i] = m[i, i];
        }

        /// <summary>The spread of the residuals for heights <paramref name="h"/> (centre first, then the ring).</summary>
        public float Roughness(double[] h)
        {
            double[] rhs = t_rhs ??= new double[6];
            double[] c = t_coefficients ??= new double[6];
            for (int i = 0; i < 6; i++) rhs[i] = 0;
            for (int r = 0; r < Points; r++)
                for (int i = 0; i < 6; i++) rhs[i] += m_rows[r][i] * h[r];
            for (int k = 0; k < m_swapCol.Length; k++)
            {
                int col = m_swapCol[k], pivot = m_swapPivot[k];
                double t = rhs[col]; rhs[col] = rhs[pivot]; rhs[pivot] = t;
                double[] f = m_factors[k];
                for (int r = 0; r < 6; r++)
                {
                    if (r == col) continue;
                    rhs[r] -= f[r] * rhs[col];
                }
            }
            for (int i = 0; i < 6; i++) c[i] = Math.Abs(m_diagonal[i]) < 1e-9 ? 0 : rhs[i] / m_diagonal[i];
            double lo = double.MaxValue, hi = double.MinValue;
            for (int r = 0; r < Points; r++)
            {
                double[] row = m_rows[r];
                double fit = 0; for (int i = 0; i < 6; i++) fit += c[i] * row[i];
                double res = h[r] - fit;
                lo = Math.Min(lo, res); hi = Math.Max(hi, res);
            }
            return (float)(hi - lo);
        }
    }

    /// <summary>The ring's offsets per sample, for one sample count: the same floats the loop worked out per sample.</summary>
    private static volatile float[][]? s_ringTrig;
    private static float[][] RingTrig(int n)
    {
        float[][]? trig = s_ringTrig;
        if (trig != null && trig[0].Length == n) return trig;
        var cos = new float[n]; var sin = new float[n];
        for (int i = 0; i < n; i++)
        {
            float angle = i * Mathf.PI * 2f / n;
            cos[i] = Mathf.Cos(angle); sin[i] = Mathf.Sin(angle);
        }
        s_ringTrig = trig = new[] { cos, sin };
        return trig;
    }

    public float Variance(Vector2i position)
    {
        float centerHeight = Height(position);
        ref Sample sample = ref At(position);
        // Shared only as the default roughness measure computes it.
        bool share = QuadVariance == DefaultQuadVariance;
        if ((sample.Flags & 8) == 0 && share && SharedTerrain.TryVariance(world, position, out float shared))
        {
            Misses++;
            sample.Variance = shared;
            sample.Flags |= 8;
        }
        else if ((sample.Flags & 8) == 0)
        {
            Misses++; TerrainCalls += RoadConstants.TerrainVarianceSampleCount;
            var p = World(position);
            int n = RoadConstants.TerrainVarianceSampleCount;
            float radius = RoadConstants.TerrainVarianceSampleRadius;
            float min = centerHeight, max = centerHeight;
            float[] ring = t_ring is { } spare && spare.Length == n ? spare : t_ring = new float[n];
            float[][] trig = RingTrig(n);
            float[] cos = trig[0], sin = trig[1];
            long began = Now;
            for (int i = 0; i < n; i++)
            {
                float h = world.GetHeight(p.x + cos[i] * radius, p.y + sin[i] * radius);
                ring[i] = h;
                min = Mathf.Min(min, h);
                max = Mathf.Max(max, h);
            }
            TerrainTicks += Now - began;
            if (QuadVariance)
                sample.Variance = QuadraticRoughness(centerHeight, ring, radius);
            else
                sample.Variance = max - min;
            sample.Flags |= 8;
            if (share) SharedTerrain.PutVariance(world, position, sample.Variance);
        }
        else Hits++;
        return sample.Variance;
    }
}

/// <summary>
/// The terrain facts every pathfinder of a generation shares: one lazily filled
/// grid of the 8 m search cells, in 64-cell tiles, behind each thread's own
/// memo, so workers over the same mountains read the world generator once.
/// A value is written before the bit that says it is
/// there, so a reader sees a whole value or none and reads the world itself;
/// two threads that do so write the same value.
/// </summary>
internal static class SharedTerrain
{
    private const int TileCells = 64;
    private sealed class Tile
    {
        public readonly float[] Height = new float[TileCells * TileCells];
        public readonly float[] River = new float[TileCells * TileCells];
        public readonly float[] Variance = new float[TileCells * TileCells];
        public readonly int[] Biome = new int[TileCells * TileCells];
        public readonly int[] Flags = new int[TileCells * TileCells];
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, Tile> s_tiles = new();
    private static WorldGenerator? s_world;
    private static readonly object s_gate = new();

    /// <summary>Forget everything: a new generation, or another world.</summary>
    internal static void Clear()
    {
        lock (s_gate)
        {
            s_tiles.Clear();
            s_world = null;
        }
    }

    private static Tile? Find(WorldGenerator world, Vector2i cell, bool create, out int index)
    {
        int tx = cell.x >= 0 ? cell.x / TileCells : (cell.x + 1) / TileCells - 1;
        int tz = cell.y >= 0 ? cell.y / TileCells : (cell.y + 1) / TileCells - 1;
        index = (cell.y - tz * TileCells) * TileCells + (cell.x - tx * TileCells);
        if (!ReferenceEquals(s_world, world))
        {
            lock (s_gate)
            {
                if (!ReferenceEquals(s_world, world)) { s_tiles.Clear(); s_world = world; }
            }
        }
        long key = ((long)tx << 32) ^ (uint)tz;
        if (s_tiles.TryGetValue(key, out Tile? tile)) return tile;
        return create ? s_tiles.GetOrAdd(key, _ => new Tile()) : null;
    }

    private static bool Has(Tile? tile, int index, int bit) =>
        tile != null && (System.Threading.Volatile.Read(ref tile.Flags[index]) & bit) != 0;

    private static void Mark(Tile tile, int index, int bit)
    {
        int seen = System.Threading.Volatile.Read(ref tile.Flags[index]);
        while ((seen & bit) == 0)
        {
            int was = System.Threading.Interlocked.CompareExchange(ref tile.Flags[index], seen | bit, seen);
            if (was == seen) return;
            seen = was;
        }
    }

    // Each Try reads the fact's bit ONCE. Read twice (once for the value, once for the answer),
    // another worker's Put between the two made it answer true with the empty value: a height
    // of 0 (deep sea), no river or the default biome, kept by that thread's memo for the rest of
    // the generation.
    internal static bool TryHeight(WorldGenerator world, Vector2i cell, out float value)
    {
        Tile? tile = Find(world, cell, false, out int i);
        bool has = Has(tile, i, 1);
        value = has ? tile!.Height[i] : 0f;
        return has;
    }
    internal static void PutHeight(WorldGenerator world, Vector2i cell, float value)
    {
        Tile tile = Find(world, cell, true, out int i)!;
        System.Threading.Volatile.Write(ref tile.Height[i], value);
        Mark(tile, i, 1);
    }
    internal static bool TryRiver(WorldGenerator world, Vector2i cell, out float value)
    {
        Tile? tile = Find(world, cell, false, out int i);
        bool has = Has(tile, i, 2);
        value = has ? tile!.River[i] : 0f;
        return has;
    }
    internal static void PutRiver(WorldGenerator world, Vector2i cell, float value)
    {
        Tile tile = Find(world, cell, true, out int i)!;
        System.Threading.Volatile.Write(ref tile.River[i], value);
        Mark(tile, i, 2);
    }
    internal static bool TryBiome(WorldGenerator world, Vector2i cell, out Heightmap.Biome value)
    {
        Tile? tile = Find(world, cell, false, out int i);
        bool has = Has(tile, i, 4);
        value = has ? (Heightmap.Biome)tile!.Biome[i] : default;
        return has;
    }
    internal static void PutBiome(WorldGenerator world, Vector2i cell, Heightmap.Biome value)
    {
        Tile tile = Find(world, cell, true, out int i)!;
        System.Threading.Volatile.Write(ref tile.Biome[i], (int)value);
        Mark(tile, i, 4);
    }
    internal static bool TryVariance(WorldGenerator world, Vector2i cell, out float value)
    {
        Tile? tile = Find(world, cell, false, out int i);
        bool has = Has(tile, i, 8);
        value = has ? tile!.Variance[i] : 0f;
        return has;
    }
    internal static void PutVariance(WorldGenerator world, Vector2i cell, float value)
    {
        Tile tile = Find(world, cell, true, out int i)!;
        System.Threading.Volatile.Write(ref tile.Variance[i], value);
        Mark(tile, i, 8);
    }
}
