using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Concurrent readers of the road grid while islands commit into it.
///
/// These are about the SHARED grid, not about pathfinding: what a reader is
/// allowed to see while another thread is publishing, and what it must never
/// see. They run the real RoadSpatialGrid, because the thing under test is its
/// locking, and a double of it would only restate the assumption being checked.
/// </summary>
public class RoadWeightConcurrencyTests : System.IDisposable
{
    // The grid is static and the suite runs sequentially (TestAssemblyInfo),
    // so each test clears it on the way in and this clears it on the way out
    // rather than leaving a world of roads behind for whatever runs next.
    public void Dispose() => RoadSpatialGrid.Clear();

    private static List<Vector2> Line(float x0, float x1, float z)
    {
        var path = new List<Vector2>();
        for (float x = x0; x <= x1; x += 4f) path.Add(new Vector2(x, z));
        return path;
    }

    private sealed class Flat : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f;
        public override Heightmap.Biome GetBiome(float x, float z) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float w, out float width) { w = 0f; width = 0f; }
    }

    [Fact]
    public void ReadersOnDifferentCellsDoNotBlockEachOtherIntoWrongAnswers()
    {
        // The removed one-entry cache was shared, so two readers on different
        // cells turned each other out of it and then took an exclusive lock to
        // put their own back. Whatever the cost of that, the answers had to
        // stay right; this pins the answers with many readers spread out.
        RoadSpatialGrid.Clear();
        var world = new Flat();
        RoadSpatialGrid.AddRoadPath(Line(-2000f, 2000f, 0f), 4f, world, null, null);

        var onRoad = new Vector2(0f, 0f);
        var offRoad = new Vector2(0f, 400f);
        RoadSpatialGrid.GetRoadWeight(onRoad.x, onRoad.y, out float expectOn, out float expectOnWidth);
        Assert.True(expectOn > 0f, "the fixture road does not cover the point it is supposed to");

        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
        {
            for (int n = 0; n < 200; n++)
            {
                // Alternating far-apart cells: the access pattern that made a
                // single shared cache useless.
                RoadSpatialGrid.GetRoadWeight(onRoad.x + i * 64f, onRoad.y, out float w, out _);
                Assert.True(w >= 0f && w <= 1f);
                RoadSpatialGrid.GetRoadWeight(onRoad.x, onRoad.y, out float on, out float onWidth);
                Assert.Equal(expectOn, on);
                Assert.Equal(expectOnWidth, onWidth);
                RoadSpatialGrid.GetRoadWeight(offRoad.x, offRoad.y, out float off, out _);
                Assert.Equal(0f, off);
            }
        });
    }

    [Fact]
    public void AReaderSeesEitherTheRoadBeforeACommitOrAfterItButNeverAStaleAnswerLater()
    {
        // The race the shared cache allowed: a reader fetches a cell's array,
        // is overtaken by a commit that replaces it, and then publishes the
        // array it fetched as everyone's cached answer -- so the new road
        // stayed invisible after it had been committed. Without the cache a
        // reader may see the old array while a commit is in flight, but once
        // the commit is done every later read sees it.
        RoadSpatialGrid.Clear();
        var world = new Flat();
        RoadSpatialGrid.AddRoadPath(Line(-400f, 400f, 0f), 4f, world, null, null);

        var probe = new Vector2(0f, 64f);
        RoadSpatialGrid.GetRoadWeight(probe.x, probe.y, out float before, out _);
        Assert.Equal(0f, before);

        using var readersStop = new CancellationTokenSource();
        var readers = new List<Task>();
        for (int i = 0; i < 8; i++)
            readers.Add(Task.Run(() =>
            {
                while (!readersStop.IsCancellationRequested)
                    RoadSpatialGrid.GetRoadWeight(probe.x, probe.y, out _, out _);
            }));

        // Commit a second road through the probe while those readers hammer it.
        RoadSpatialGrid.AddRoadPath(Line(-400f, 400f, 64f), 4f, world, null, null);

        RoadSpatialGrid.GetRoadWeight(probe.x, probe.y, out float after, out _);
        readersStop.Cancel();
        Task.WaitAll(readers.ToArray());

        Assert.True(after > 0f,
            "a committed road was still invisible to a reader afterwards: a stale answer was published");
        // And it stays visible: no cache entry survives to un-publish it.
        for (int n = 0; n < 1000; n++)
        {
            RoadSpatialGrid.GetRoadWeight(probe.x, probe.y, out float w, out _);
            Assert.Equal(after, w);
        }
    }

    [Fact]
    public void CommittingFromSeveralIslandsAtOnceLosesNoPoints()
    {
        // Every island commits into the one grid. The totals are a
        // read-modify-write and the cell arrays are replaced wholesale, so
        // this is the check that concurrent commits add up.
        RoadSpatialGrid.Clear();
        var world = new Flat();
        const int roads = 24;
        Parallel.For(0, roads, new ParallelOptions { MaxDegreeOfParallelism = 12 },
            i => RoadSpatialGrid.AddRoadPath(Line(-200f, 200f, i * 128f), 4f, world, null, null));

        int parallelPoints = RoadSpatialGrid.TotalRoadPoints;

        RoadSpatialGrid.Clear();
        for (int i = 0; i < roads; i++)
            RoadSpatialGrid.AddRoadPath(Line(-200f, 200f, i * 128f), 4f, world, null, null);

        Assert.Equal(RoadSpatialGrid.TotalRoadPoints, parallelPoints);
    }
}
