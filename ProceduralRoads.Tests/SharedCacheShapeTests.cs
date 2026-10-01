using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The SHAPE of a shared lookup cache, modelled so its behaviour can be
/// reproduced on demand rather than waited for.
///
/// Two shapes are compared. "One entry" is what RoadSpatialGrid.GetRoadWeight
/// used to be, and what Valheim's own WorldGenerator.GetRiverWeight still is:
/// a dictionary of published arrays, plus a single remembered (key, array)
/// pair guarded by a ReaderWriterLockSlim, installed under an EXCLUSIVE lock
/// on every miss. "Fetch then compute" is what GetRoadWeight is now: one short
/// read lock around the dictionary, and the arithmetic outside it.
///
/// These are doubles, so their timings are not predictions of anything. What
/// they can settle is what each shape ALLOWS: which interleavings produce a
/// wrong answer, and how each behaves when readers are spread over the world
/// instead of walking it in order. Deliberately no sleeps: a sleep hands the
/// CPU away and would make the parallel numbers flattering and meaningless.
/// Interleavings are driven by events, so they happen every run.
/// </summary>
public class SharedCacheShapeTests
{
    /// <summary>A cell's published contents. Replaced wholesale on commit,
    /// never edited in place -- the invariant the real grid also holds.</summary>
    private sealed class Cell { public readonly int Version; public Cell(int v) { Version = v; } }

    private abstract class Grid
    {
        protected readonly Dictionary<int, Cell> published = new();
        protected readonly ReaderWriterLockSlim gate = new(LockRecursionPolicy.NoRecursion);
        public long Hits, Misses, Replacements;

        /// <summary>Deterministic arithmetic standing in for the work a real
        /// caller does around a lookup. NOT a sleep: a sleep hands the core
        /// away and would make every parallel number here flattering. The
        /// amount is what decides whether one worker's reads run back to back
        /// or another worker gets in between, so it is the knob this file
        /// exists to turn.</summary>
        public int WorkPerRead;
        protected double sink;
        protected void Work()
        {
            double acc = 0;
            for (int i = 1; i <= WorkPerRead; i++) acc += 1.0 / i;
            sink += acc;
        }

        public void Commit(int key, int version)
        {
            gate.EnterWriteLock();
            try { published[key] = new Cell(version); }
            finally { gate.ExitWriteLock(); }
            AfterCommit();
        }
        protected virtual void AfterCommit() { }
        public abstract int Read(int key);
    }

    /// <summary>The old shape, with a hook between fetching a cell and
    /// installing it as the shared entry -- the window the race lives in.</summary>
    private sealed class OneEntryGrid : Grid
    {
        private int cachedKey = int.MinValue;
        private Cell? cachedCell;
        public ManualResetEventSlim? PauseBeforeInstall;

        protected override void AfterCommit()
        {
            gate.EnterWriteLock();
            try { cachedKey = int.MinValue; cachedCell = null; }
            finally { gate.ExitWriteLock(); }
        }

        public override int Read(int key)
        {
            Work();
            gate.EnterReadLock();
            try
            {
                if (key == cachedKey) { Hits++; return cachedCell?.Version ?? -1; }
            }
            finally { gate.ExitReadLock(); }

            Misses++;
            Cell? cell;
            gate.EnterReadLock();
            try { published.TryGetValue(key, out cell); }
            finally { gate.ExitReadLock(); }

            PauseBeforeInstall?.Wait();

            gate.EnterWriteLock();
            try
            {
                if (cachedKey != int.MinValue) Replacements++;
                cachedKey = key; cachedCell = cell;
            }
            finally { gate.ExitWriteLock(); }
            return cell?.Version ?? -1;
        }
    }

    /// <summary>The new shape. Nothing is remembered, so there is nothing to
    /// install, nothing to invalidate and nothing to install stale.</summary>
    private sealed class FetchThenComputeGrid : Grid
    {
        public override int Read(int key)
        {
            Work();
            Cell? cell;
            gate.EnterReadLock();
            try { published.TryGetValue(key, out cell); }
            finally { gate.ExitReadLock(); }
            Misses++;
            return cell?.Version ?? -1;
        }
    }

    [Fact]
    public void TheOneEntryShapePublishesAnAnswerThatWasAlreadyOutOfDate()
    {
        var grid = new OneEntryGrid();
        grid.Commit(7, version: 1);

        // A reader fetches version 1 and is held just before it installs it.
        using var held = new ManualResetEventSlim(false);
        grid.PauseBeforeInstall = held;
        var slowReader = Task.Run(() => grid.Read(7));
        SpinWait.SpinUntil(() => held.IsSet == false && grid.Misses > 0);

        // A commit lands and correctly blanks the shared entry.
        grid.Commit(7, version: 2);

        // The held reader now installs the version it fetched before that.
        held.Set();
        Assert.Equal(1, slowReader.Result);

        // Everyone after it is served version 1, which no longer exists.
        grid.PauseBeforeInstall = null;
        Assert.Equal(1, grid.Read(7));
        Assert.True(grid.Hits > 0, "the stale answer was not served from the shared entry");
    }

    [Fact]
    public void TheFetchThenComputeShapeCannotPublishAnythingAtAll()
    {
        var grid = new FetchThenComputeGrid();
        grid.Commit(7, version: 1);
        Assert.Equal(1, grid.Read(7));
        grid.Commit(7, version: 2);
        // The same sequence that stranded version 1 above: every later read
        // sees the commit, because no reader can leave anything behind.
        for (int i = 0; i < 100; i++) Assert.Equal(2, grid.Read(7));
        Assert.Equal(0, grid.Hits);
        Assert.Equal(0, grid.Replacements);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(200)]
    [InlineData(2000)]
    public void WhatCompetingWorkersDoToASharedEntryDependsOnTheWorkBetweenReads(int workPerRead)
    {
        // A search stays inside a cell for several samples before it moves on
        // -- the neighbours of a point share its cell -- so a visit is several
        // reads of one key. That run of repeats is the whole value of a
        // one-entry cache.
        //
        // Whether competing workers destroy it is NOT self-evident, and this
        // is the measurement rather than the assumption. With no work between
        // reads a worker re-takes the lock it just released and mostly keeps
        // its own entry. The more work sits between two reads, the more room
        // another worker has to put its own key in and the closer the shared
        // entry comes to being useless.
        const int RepeatsPerVisit = 8;
        void Visit(Grid g, int key) { for (int i = 0; i < RepeatsPerVisit; i++) g.Read(key); }

        var walking = new OneEntryGrid { WorkPerRead = workPerRead };
        for (int key = 0; key < 64; key++) walking.Commit(key, 1);
        for (int pass = 0; pass < 20; pass++)
            for (int key = 0; key < 64; key++) Visit(walking, key);

        var spread = new OneEntryGrid { WorkPerRead = workPerRead };
        for (int key = 0; key < 64; key++) spread.Commit(key, 1);
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, worker =>
        {
            for (int pass = 0; pass < 20; pass++)
                for (int step = 0; step < 8; step++) Visit(spread, worker * 8 + step);
        });

        double walkingHitRate = (double)walking.Hits / (walking.Hits + walking.Misses);
        double spreadHitRate = (double)spread.Hits / (spread.Hits + spread.Misses);

        // One thread on its own always gets the run of repeats it paid for:
        // seven hits per eight reads, whatever the work between them.
        Assert.True(walkingHitRate > 0.8,
            $"a single walker kept only {walkingHitRate:P1}; the fixture is wrong, not the cache");
        // Competing workers never do BETTER than one walking alone. That is
        // the part that holds at every work level, and it is the claim this
        // file is willing to make about the real thing.
        Assert.True(spreadHitRate <= walkingHitRate + 0.001,
            $"spread readers kept {spreadHitRate:P1} against {walkingHitRate:P1} walking");
    }

    [Fact]
    public void TheSharedEntryCostsAnExclusiveLockThatTheOtherShapeNeverTakes()
    {
        // Independent of hit rate: every miss on the one-entry shape takes a
        // WRITE lock, which excludes every other reader for its duration. The
        // fetch-then-compute shape takes none at all, ever. That is the cost
        // that does not depend on how the interleaving happens to fall.
        var oneEntry = new OneEntryGrid();
        var fetchThenCompute = new FetchThenComputeGrid();
        for (int key = 0; key < 64; key++) { oneEntry.Commit(key, 1); fetchThenCompute.Commit(key, 1); }

        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, worker =>
        {
            for (int pass = 0; pass < 50; pass++)
                for (int step = 0; step < 8; step++)
                {
                    oneEntry.Read(worker * 8 + step);
                    fetchThenCompute.Read(worker * 8 + step);
                }
        });

        // Every miss installed an entry under an exclusive lock.
        Assert.True(oneEntry.Misses > 0);
        Assert.True(oneEntry.Replacements > 0);
        Assert.Equal(0, fetchThenCompute.Replacements);
    }
}
