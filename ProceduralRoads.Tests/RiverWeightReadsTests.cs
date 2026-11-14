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
}
