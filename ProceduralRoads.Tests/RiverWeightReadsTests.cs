using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The river weight read without the game's shared cache while a generation's workers run (RiverWeightReads): the game's
/// own weight and width, to the bit, at many points in any order and on many threads, across an opening that forgets every
/// thread's last cell; and nothing read while it is closed or for another world.
/// </summary>
public sealed class RiverWeightReadsTests : IDisposable
{
    /// <summary>A world with rivers, whose GetRiverWeight is the game's (WorldGenerator.GetRiverWeight in Valheim): its one
    /// shared last river grid cell behind a ReaderWriterLockSlim, the points read through it.</summary>
    private class GameRivers : WorldGenerator
    {
        private readonly ReaderWriterLockSlim m_riverCacheLock = new();
        private Vector2i m_cachedRiverGrid = new(-999999, -999999);
        private RiverPoint[]? m_cachedRiverPoints;

        public GameRivers(int seed) => Fill(seed);

        /// <summary>River points along a few wandering lines, from <paramref name="seed"/>, filed by river grid cell as the
        /// game files them (AddRiverPoint: every cell within the point's width and a half cell of it). The game's
        /// CleanCachedRiverData first.</summary>
        public void Fill(int seed)
        {
            m_riverPoints.Clear();
            m_cachedRiverPoints = null;
            var random = new System.Random(seed);
            var cells = new Dictionary<Vector2i, List<RiverPoint>>();
            for (int river = 0; river < 6; river++)
            {
                Vector2 p = new((float)(random.NextDouble() * 1200.0 - 600.0), (float)(random.NextDouble() * 1200.0 - 600.0));
                float heading = (float)(random.NextDouble() * Math.PI * 2.0);
                for (int k = 0; k < 120; k++)
                {
                    float r = (float)(random.NextDouble() * 40.0 + 20.0);
                    Vector2i grid = GetRiverGrid(p.x, p.y);
                    int reach = Mathf.CeilToInt((float)((double)r / 64.0));
                    for (int i = grid.y - reach; i <= grid.y + reach; i++)
                        for (int j = grid.x - reach; j <= grid.x + reach; j++)
                        {
                            Vector2 corner = new((float)((double)j * 64.0), (float)((double)i * 64.0));
                            Vector2 d = p - corner;
                            if (!(Math.Abs(d.x) < (float)((double)r + 32.0) && Math.Abs(d.y) < (float)((double)r + 32.0))) continue;
                            var at = new Vector2i(j, i);
                            if (!cells.TryGetValue(at, out var list)) cells[at] = list = new List<RiverPoint>();
                            list.Add(new RiverPoint(p, r));
                        }
                    heading += (float)(random.NextDouble() - 0.5) * 0.6f;
                    p += new Vector2(Mathf.Cos(heading), Mathf.Sin(heading)) * 9f;
                }
            }
            foreach (var cell in cells) m_riverPoints[cell.Key] = cell.Value.ToArray();
        }

        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            Vector2i riverGrid = GetRiverGrid(wx, wy);
            m_riverCacheLock.EnterReadLock();
            if (riverGrid == m_cachedRiverGrid)
            {
                if (m_cachedRiverPoints != null)
                {
                    GetWeight(m_cachedRiverPoints, wx, wy, out weight, out width);
                    m_riverCacheLock.ExitReadLock();
                }
                else
                {
                    weight = 0f;
                    width = 0f;
                    m_riverCacheLock.ExitReadLock();
                }
                return;
            }
            m_riverCacheLock.ExitReadLock();
            if (m_riverPoints.TryGetValue(riverGrid, out var value))
            {
                GetWeight(value, wx, wy, out weight, out width);
                m_riverCacheLock.EnterWriteLock();
                m_cachedRiverGrid = riverGrid;
                m_cachedRiverPoints = value;
                m_riverCacheLock.ExitWriteLock();
            }
            else
            {
                m_riverCacheLock.EnterWriteLock();
                m_cachedRiverGrid = riverGrid;
                m_cachedRiverPoints = null;
                m_riverCacheLock.ExitWriteLock();
                weight = 0f;
                width = 0f;
            }
        }
    }

    private static int Bits(float f) => BitConverter.ToInt32(BitConverter.GetBytes(f), 0);

    private readonly List<WorldGenerator> m_opened = new();

    private void Open(WorldGenerator world)
    {
        RiverWeightReads.Begin(world);
        m_opened.Add(world);
    }

    public void Dispose()
    {
        foreach (var world in m_opened) RiverWeightReads.End(world);
    }

    /// <summary>Points over the rivers' area: runs along a line (a search's scan, mostly within one cell), and jumps (a
    /// read from another worker's island).</summary>
    private static List<Vector2> Points(int seed, int count)
    {
        var random = new System.Random(seed);
        var points = new List<Vector2>();
        Vector2 p = new(0f, 0f);
        while (points.Count < count)
        {
            if (random.Next(8) == 0)
                p = new Vector2((float)(random.NextDouble() * 1400.0 - 700.0), (float)(random.NextDouble() * 1400.0 - 700.0));
            else
                p += new Vector2((float)(random.NextDouble() * 16.0 - 8.0), (float)(random.NextDouble() * 16.0 - 8.0));
            points.Add(p);
        }
        return points;
    }

    private static void AssertTheGames(GameRivers world, IEnumerable<Vector2> points, int leastOnARiver = 101)
    {
        int rivers = 0;
        foreach (Vector2 p in points)
        {
            world.GetRiverWeight(p.x, p.y, out float weight, out float width);
            Assert.True(RiverWeightReads.TryRead(world, p.x, p.y, out float readWeight, out float readWidth));
            Assert.Equal(Bits(weight), Bits(readWeight));
            Assert.Equal(Bits(width), Bits(readWidth));
            if (weight > 0f) rivers++;
        }
        Assert.True(rivers >= leastOnARiver, $"{rivers} points on a river: the points should cross the rivers often");
    }

    [Fact]
    public void WhileOpenTheWeightIsTheGamesToTheBit()
    {
        var world = new GameRivers(11);
        Open(world);
        AssertTheGames(world, Points(5, 20000));
    }

    /// <summary>The world's rivers made again (the game's CleanCachedRiverData, then new points) and the reads opened again:
    /// no thread's last cell carries the old points over.</summary>
    [Fact]
    public void AnOpeningForgetsEveryThreadsLastCell()
    {
        var world = new GameRivers(11);
        Open(world);
        var points = Points(6, 5000);
        AssertTheGames(world, points);
        // This thread's last cell holds a river of the old points; the first read after the opening is in that cell.
        Vector2 last = points.Find(p => { world.GetRiverWeight(p.x, p.y, out float w, out _); return w > 0f; });
        Assert.True(RiverWeightReads.TryRead(world, last.x, last.y, out _, out _));
        world.Fill(12);
        Open(world);
        AssertTheGames(world, new[] { last }, leastOnARiver: 0);
        AssertTheGames(world, points);
        // On another thread too, which read the old points before the opening: its last cell is forgotten as well.
        using var other = new Reader();
        other.Run(() => AssertTheGames(world, points));
        world.Fill(13);
        Open(world);
        other.Run(() => AssertTheGames(world, points));
    }

    /// <summary>One thread that runs what it is given, in turn, keeping its thread-statics between runs.</summary>
    private sealed class Reader : IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> m_work = new();
        private readonly Thread m_thread;
        public Reader()
        {
            m_thread = new Thread(() => { foreach (Action a in m_work.GetConsumingEnumerable()) a(); }) { IsBackground = true };
            m_thread.Start();
        }
        public void Run(Action read)
        {
            Exception? failed = null;
            using var done = new ManualResetEventSlim();
            m_work.Add(() => { try { read(); } catch (Exception e) { failed = e; } finally { done.Set(); } });
            done.Wait();
            if (failed != null) throw new Exception("on the other thread", failed);
        }
        public void Dispose()
        {
            m_work.CompleteAdding();
            m_thread.Join();
        }
    }

    [Fact]
    public void ManyThreadsReadTheGamesWeights()
    {
        var world = new GameRivers(21);
        var expected = new Dictionary<int, (int weight, int width)>();
        var points = Points(9, 20000);
        for (int i = 0; i < points.Count; i++)
        {
            world.GetRiverWeight(points[i].x, points[i].y, out float w, out float width);
            expected[i] = (Bits(w), Bits(width));
        }
        Open(world);
        Parallel.For(0, 8, worker =>
        {
            for (int k = 0; k < points.Count; k++)
            {
                int i = (k * 7919 + worker * 104729) % points.Count;
                Assert.True(RiverWeightReads.TryRead(world, points[i].x, points[i].y, out float w, out float width));
                Assert.Equal(expected[i], (Bits(w), Bits(width)));
            }
        });
    }

    /// <summary>A world whose river weight is not the game's formula over its river points (a game update, or another mod's
    /// change no patch list shows): half the game's weight.</summary>
    private sealed class ChangedRivers : GameRivers
    {
        public ChangedRivers(int seed) : base(seed) { }

        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            base.GetRiverWeight(wx, wy, out weight, out width);
            weight *= 0.5f;
        }
    }

    /// <summary>Where the game's own method gives this read's weights, the start compares the world's river points and
    /// opens.</summary>
    [Fact]
    public void TheStartComparesTheRiversAndOpens()
    {
        var world = new GameRivers(21);
        Assert.Null(RiverWeightReads.SelfCheck(world, out int compared));
        Assert.True(compared >= 32, $"{compared} river points compared");
        Assert.True(RiverWeightReads.Begin(world));
        m_opened.Add(world);
        Assert.True(RiverWeightReads.TryRead(world, 10f, 10f, out _, out _));
    }

    /// <summary>Where another mod patches GetRiverWeight, nothing is opened: the game's method, with that mod's patch, is
    /// read throughout.</summary>
    [Fact]
    public void WhereAnotherModPatchesTheMethodTheGamesMethodRuns()
    {
        var world = new GameRivers(22);
        System.Func<string[]> before = RiverWeightReads.PatchedByOthers;
        RiverWeightReads.PatchedByOthers = () => new[] { "another.rivers.mod" };
        try
        {
            Assert.False(RiverWeightReads.Begin(world));
            Assert.False(RiverWeightReads.TryRead(world, 10f, 10f, out _, out _));
        }
        finally { RiverWeightReads.PatchedByOthers = before; RiverWeightReads.End(world); }
    }

    /// <summary>Where any mod patches the game's GetWeight, the reads open but the index is not used: every point of a cell
    /// is read through the game's GetWeight, patch and all (the index sums the points itself and would pass it by).</summary>
    [Fact]
    public void WhereAModPatchesGetWeightEveryPointIsReadThroughIt()
    {
        var plain = new GameRivers(24);
        Open(plain);
        Assert.True(RiverWeightReads.Indexed(plain));
        var world = new GameRivers(25);
        System.Func<string[]> before = RiverWeightReads.WeightPatchedBy;
        RiverWeightReads.WeightPatchedBy = () => new[] { "another.weights.mod" };
        try
        {
            Assert.True(RiverWeightReads.Begin(world));
            m_opened.Add(world);
        }
        finally { RiverWeightReads.WeightPatchedBy = before; }
        Assert.False(RiverWeightReads.Indexed(world));
        AssertTheGames(world, Points(5, 5000));
    }

    /// <summary>The next float above and below <paramref name="f"/>.</summary>
    private static float Up(float f) => f == 0f ? float.Epsilon
        : BitConverter.ToSingle(BitConverter.GetBytes(Bits(f) + (f > 0f ? 1 : -1)), 0);
    private static float Down(float f) => -Up(-f);

    /// <summary>River points that try the index's filing (RiverWeightReads.CellIndex) at its edges, for the cell
    /// <paramref name="grid"/>: on the sub-cells' edges and corners, on the cell's rim, outside the cell (a point beyond the
    /// rim whose circle reaches in), small widths that just reach or just miss a sub-cell, and widths larger than the
    /// cell.</summary>
    private static WorldGenerator.RiverPoint[] EdgePoints(Vector2i grid, int seed)
    {
        float ox = grid.x * 64f - 32f, oy = grid.y * 64f - 32f;
        var random = new System.Random(seed);
        var list = new List<WorldGenerator.RiverPoint>();
        for (int i = 0; i <= 8; i++)
            for (int k = 0; k <= 8; k += 2)
            {
                // On a sub-cell's edge or corner, a hair either side of it, and on the rim (i or k 0 or 8).
                float x = ox + i * 8f, y = oy + k * 8f;
                list.Add(new WorldGenerator.RiverPoint(new Vector2(x, y), 0.5f + (float)random.NextDouble() * 3f));
                list.Add(new WorldGenerator.RiverPoint(new Vector2(Up(x), Down(y)), 1f));
                list.Add(new WorldGenerator.RiverPoint(new Vector2(x + 0.25f, y - 0.25f), 0.01f));
            }
        // Outside the cell, their circles reaching in by a little or a lot, or not at all.
        foreach (float w in new[] { 0.5f, 3f, 9f, 30f })
        {
            list.Add(new WorldGenerator.RiverPoint(new Vector2(ox - w * 0.5f, oy + 20f), w));
            list.Add(new WorldGenerator.RiverPoint(new Vector2(ox + 64f + w * 0.9f, oy + 41f), w));
            list.Add(new WorldGenerator.RiverPoint(new Vector2(ox + 13f, oy - w - 0.5f), w));
            list.Add(new WorldGenerator.RiverPoint(new Vector2(ox - w * 0.7f, oy + 64f + w * 0.7f), w));
        }
        // Widths past the cell: one circle holds the whole cell, one from outside it holds most of it.
        list.Add(new WorldGenerator.RiverPoint(new Vector2(ox + 30f, oy + 33f), 80f));
        list.Add(new WorldGenerator.RiverPoint(new Vector2(ox - 50f, oy - 10f), 120f));
        // Random points over and round the cell, small and large.
        for (int n = 0; n < 60; n++)
            list.Add(new WorldGenerator.RiverPoint(new Vector2(ox - 20f + (float)random.NextDouble() * 104f, oy - 20f + (float)random.NextDouble() * 104f),
                n % 7 == 0 ? 40f + (float)random.NextDouble() * 60f : 0.2f + (float)random.NextDouble() * 12f));
        return list.ToArray();
    }

    /// <summary>The index of one river cell's points (RiverWeightReads.CellIndex) gives the game's GetWeight over the same
    /// points, to the bit, at every point of the cell: a 0.25 m lattice over it, every sub-cell edge and the cell's rim, a
    /// float either side of each, and each river point's centre and rim. The points are filed on the sub-cells' edges, on
    /// the cell's rim, outside the cell and with widths larger than the cell, so a change to the filing (its metre of
    /// reach, its half-metre margin, its clamp) that drops a point is seen here.</summary>
    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(3, -2, 2)]
    [InlineData(-7, 5, 3)]
    [InlineData(-120, 131, 4)]
    public void TheCellIndexGivesTheGamesWeightAtEveryPointOfItsCell(int gx, int gy, int seed)
    {
        var world = new GameRivers(1);
        var grid = new Vector2i(gx, gy);
        var points = EdgePoints(grid, seed);
        var index = new RiverWeightReads.CellIndex(grid, points);
        float ox = gx * 64f - 32f, oy = gy * 64f - 32f;
        var xs = new List<float>();
        var ys = new List<float>();
        for (int i = 0; i <= 256; i++) { xs.Add(ox + i * 0.25f); ys.Add(oy + i * 0.25f); }
        for (int i = 0; i <= 8; i++)
            foreach (float d in new[] { 0f, -1f, 1f })
            {
                float x = ox + i * 8f, y = oy + i * 8f;
                xs.Add(d < 0 ? Down(x) : d > 0 ? Up(x) : x);
                ys.Add(d < 0 ? Down(y) : d > 0 ? Up(y) : y);
            }
        int compared = 0, onRiver = 0;
        void Compare(float x, float y)
        {
            if (world.GetRiverGrid(x, y) != grid) return;
            world.GetWeight(points, x, y, out float weight, out float width);
            index.Weight(x, y, out float read, out float readWidth);
            Assert.True(Bits(weight) == Bits(read) && Bits(width) == Bits(readWidth),
                $"at ({x:R}, {y:R}) the game gives weight {weight:R} width {width:R}, the index {read:R} {readWidth:R}");
            compared++;
            if (weight > 0f) onRiver++;
        }
        foreach (float x in xs)
            foreach (float y in ys)
                Compare(x, y);
        foreach (var rp in points)
            foreach (var (dx, dy) in new[] { (0f, 0f), (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f), (0.7071f, 0.7071f) })
            {
                float x = rp.p.x + dx * rp.w, y = rp.p.y + dy * rp.w;
                Compare(x, y);
                Compare(Up(x), Down(y));
                Compare(Down(x), Up(y));
            }
        Assert.True(compared > 65000, $"{compared} points compared");
        Assert.True(onRiver > compared / 2, $"{onRiver} of {compared} points on a river: the points should cover the cell");
    }

    /// <summary>With KeepsRiverIndex off (LowMemoryGeneration) the reads open unindexed and give the game's weight, to the
    /// bit: every point of a cell read through GetWeight.</summary>
    [Fact]
    public void WithoutTheRiverIndexTheReadsAreTheGamesToo()
    {
        var world = new GameRivers(26);
        bool keeps = GenerationMemory.KeepsRiverIndex;
        GenerationMemory.KeepsRiverIndex = false;
        try
        {
            Open(world);
            Assert.False(RiverWeightReads.Indexed(world));
        }
        finally { GenerationMemory.KeepsRiverIndex = keeps; }
        AssertTheGames(world, Points(7, 20000));
        // And with it on, on the same rivers: indexed, the same weights.
        var indexed = new GameRivers(26);
        Open(indexed);
        Assert.True(RiverWeightReads.Indexed(indexed));
        AssertTheGames(indexed, Points(7, 20000));
    }

    /// <summary>Where the game's method gives other weights than this read at the world's river points, nothing is opened.</summary>
    [Fact]
    public void WhereTheGameReadsOtherwiseTheGamesMethodRuns()
    {
        var world = new ChangedRivers(23);
        Assert.NotNull(RiverWeightReads.SelfCheck(world, out _));
        Assert.False(RiverWeightReads.Begin(world));
        Assert.False(RiverWeightReads.TryRead(world, 10f, 10f, out _, out _));
        RiverWeightReads.End(world);
    }

    /// <summary>Closed, or for a world the workers do not run on, nothing is read: the game's own method runs.</summary>
    [Fact]
    public void ClosedOrForAnotherWorldTheGamesOwnMethodRuns()
    {
        var world = new GameRivers(11);
        var other = new GameRivers(12);
        Assert.False(RiverWeightReads.TryRead(world, 10f, 10f, out _, out _));
        Open(world);
        Assert.False(RiverWeightReads.TryRead(other, 10f, 10f, out _, out _));
        Assert.True(RiverWeightReads.TryRead(world, 10f, 10f, out _, out _));
        RiverWeightReads.End(other);
        Assert.True(RiverWeightReads.TryRead(world, 10f, 10f, out _, out _));
        RiverWeightReads.End(world);
        Assert.False(RiverWeightReads.TryRead(world, 10f, 10f, out _, out _));
    }

    /// <summary>Two runs open the same world and the first ends: the world stays open and indexed for the second, and closes
    /// only when the second ends too (the generator's finally ends each run's own opening).</summary>
    [Fact]
    public void ARunThatEndsWhileAnotherIsOpenLeavesTheWorldIndexedUntilTheLastEnds()
    {
        var world = new GameRivers(31);
        Assert.True(RiverWeightReads.Begin(world));
        Assert.True(RiverWeightReads.Begin(world));
        try
        {
            Assert.True(RiverWeightReads.Indexed(world));
            RiverWeightReads.End(world);
            Assert.True(RiverWeightReads.Indexed(world));
            AssertTheGames(world, Points(9, 20000));
            RiverWeightReads.End(world);
            Assert.False(RiverWeightReads.Indexed(world));
            Assert.False(RiverWeightReads.TryRead(world, 10f, 10f, out _, out _));
        }
        finally
        {
            // A world already closed is a no-op.
            RiverWeightReads.End(world);
            RiverWeightReads.End(world);
        }
    }

    /// <summary>A run whose Begin returned false opened nothing, and the generator does not end it: another run's opening
    /// stays open and indexed.</summary>
    [Fact]
    public void ABeginThatReturnsFalseLeavesAnotherRunsOpeningOpen()
    {
        var first = new GameRivers(32);
        var second = new GameRivers(33);
        Assert.True(RiverWeightReads.Begin(first));
        try
        {
            System.Func<string[]> before = RiverWeightReads.PatchedByOthers;
            RiverWeightReads.PatchedByOthers = () => new[] { "another.rivers.mod" };
            try { Assert.False(RiverWeightReads.Begin(second)); }
            finally { RiverWeightReads.PatchedByOthers = before; }
            // The caller's rule (riversOpened): no End for a Begin that returned false.
            Assert.False(RiverWeightReads.TryRead(second, 10f, 10f, out _, out _));
            Assert.True(RiverWeightReads.Indexed(first));
            AssertTheGames(first, Points(10, 20000));
        }
        finally { RiverWeightReads.End(first); }
        Assert.False(RiverWeightReads.Indexed(first));
    }
}
