using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public sealed class GenerationScratchTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private sealed class Flat : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f;

        /// <summary>The game's river weight over its river points, so the fast read opens on it (RiverWeightReads.SelfCheck).</summary>
        public override void GetRiverWeight(float x, float z, out float weight, out float width)
        {
            if (m_riverPoints.TryGetValue(GetRiverGrid(x, z), out RiverPoint[]? points) && points != null) GetWeight(points, x, z, out weight, out width);
            else { weight = 0f; width = 0f; }
        }
    }

    private static object? ScratchValue(Type owner, string before, string holder, string field)
    {
        FieldInfo? old = owner.GetField(before, PrivateStatic);
        if (old != null) return old.GetValue(null);
        object? scratch = owner.GetField(holder, PrivateStatic)!.GetValue(null);
        return scratch?.GetType().GetField(field)!.GetValue(scratch);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Dictionary<string, WeakReference> Warm()
    {
        var world = new Flat();
        var river = new[] { new WorldGenerator.RiverPoint(new Vector2(0f, 0f), 8f) };
        world.m_riverPoints[world.GetRiverGrid(0f, 0f)] = river;
        RiverWeightReads.Begin(world);
        try { Assert.True(RiverWeightReads.TryRead(world, 0f, 0f, out _, out _)); }
        finally { RiverWeightReads.End(world); }
        Assert.Equal(40f, RoadStairRuns.GroundAtVertex(0, 0, world));
        RoadSpatialGrid.PlannedPath? road = RoadSpatialGrid.PlanRoadPath(new List<Vector2>
            { new(0f, 0f), new(8f, 0f) }, RoadConstants.DefaultRoadWidth, world);
        Assert.NotNull(road);
        RoadNetworkGenerator.EarthworkAcross(road!, _ => 40f, RoadNetworkGenerator.EarthworkLimit);
        StairPlan plan = StairLayout.Build(new Vector2(0f, 0f), new Vector2(0f, 20f), (x, z) => z * 0.5f);
        Assert.True(plan.Reached, plan.Reason);
        if (GenerationMemory.LaysAhead)
        {
            RoadStairRuns.LayoutsAhead.Start();
            RoadStairRuns.LayoutsAhead.Offer(new Vector2(0f, 0f), new Vector2(0f, 20f), StairKit.Stone,
                RoadStairRuns.LayoutGround.Lattice(world), null, 0, true, null, null, null,
                new[] { ((float?)null, (float?)null, false, false, false) });
        }
        RoadStairRuns.LayoutsAhead.Stop();
        var references = new Dictionary<string, WeakReference>
        {
            ["world"] = new WeakReference(world),
            ["river points"] = new WeakReference(river),
            ["earthwork vertices"] = new WeakReference(ScratchValue(typeof(RoadNetworkGenerator), "t_earthVertices", "t_earthwork", "Vertices")!),
            ["ground tiles"] = new WeakReference(ScratchValue(typeof(RoadStairRuns), "t_tiles", "t_ground", "Tiles")!),
        };
        if (GenerationMemory.LaysAhead)
            references.Add("offered layouts", new WeakReference(ScratchValue(typeof(RoadStairRuns.LayoutsAhead), "t_mine", "t_offered", "Jobs")!));
        if (GenerationMemory.KeepsNodes)
        {
            Type poolType = typeof(StairLayout).GetNestedType("NodePool", BindingFlags.NonPublic)!;
            object pool = poolType.GetField("t_pool", PrivateStatic)!.GetValue(null)!;
            object nodes = poolType.GetField("m_nodes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pool)!;
            references.Add("pooled nodes", new WeakReference(nodes));
            references.Add("open search", new WeakReference(ScratchValue(typeof(StairLayout), "t_open", "t_search", "Open")!));
            references.Add("seen states", new WeakReference(ScratchValue(typeof(StairLayout), "t_seen", "t_search", "Seen")!));
        }
        return references;
    }

    /// <summary>The release under test, in a frame of its own: what it read while clearing goes with that frame.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Release() => RoadNetworkGenerator.ReleaseGenerationResources();

    /// <summary>
    /// Mono, the game's runtime, reads every thread's stack conservatively: a word a call that has returned left in a slot
    /// no frame reads any more counts as a reference, so an object nothing holds can read as rooted on a thread that is
    /// waiting (under Mono this test failed that way, a different set of objects in each case, while .NET 10 passed it).
    /// Before the worker idles, and before the collection, the thread overwrites the stack its calls used with zeros: only
    /// what a field, a static or a live frame still holds keeps an object alive, as on a runtime that reads the stack exactly.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ScrubStack(int depth = 256)
    {
        var pad = new StackPad();
        Keep(ref pad);
        if (depth > 0) ScrubStack(depth - 1);
    }

    /// <summary>512 bytes of zeros on the stack (locals are zeroed at entry): never written, only laid.</summary>
#pragma warning disable CS0649
    private struct StackPad
    {
        public Pad16 A, B, C, D;
    }

    private struct Pad16
    {
        public long A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P;
    }
#pragma warning restore CS0649

    private static long s_padRead;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Keep(ref StackPad pad) => s_padRead += pad.D.P;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleaseDropsScratchAndTheOldWorldFromAnIdleWorker(bool lowMemory)
    {
        bool low = GenerationMemory.Low;
        int workers = RoadParallel.Configured;
        using var warmed = new ManualResetEventSlim();
        using var releaseWorker = new ManualResetEventSlim();
        using var nextRun = new AutoResetEvent(false);
        Dictionary<string, WeakReference>? references = null;
        Exception? failure = null;
        RoadNetworkGenerator.Reset();
        GenerationMemory.Low = lowMemory;
        RoadParallel.Configured = 2;
        RoadParallel.Islands(1);
        var worker = new Thread(() =>
        {
            for (int run = 0; run < 2 && !releaseWorker.IsSet; run++)
            {
                try { references = Warm(); }
                catch (Exception error) { failure = error; }
                finally { ScrubStack(); warmed.Set(); }
                if (!nextRun.WaitOne(10000)) break;
            }
        }) { IsBackground = true };
        worker.Start();
        try
        {
            for (int run = 0; run < 2; run++)
            {
                Assert.True(warmed.Wait(5000), "worker did not warm its scratch");
                Assert.Null(failure);
                Assert.NotNull(references);
                Assert.All(references!, pair => Assert.True(pair.Value.IsAlive, pair.Key + " was not retained before release"));
                Release();
                ScrubStack();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Assert.All(references!, pair => Assert.False(pair.Value.IsAlive, pair.Key + " remains rooted on the idle worker"));
                warmed.Reset();
                if (run == 0) nextRun.Set();
            }
        }
        finally
        {
            releaseWorker.Set();
            nextRun.Set();
            Assert.True(worker.Join(5000));
            RoadStairRuns.LayoutsAhead.Stop();
            RoadParallel.Islands(0);
            RoadParallel.Configured = workers;
            GenerationMemory.Low = low;
            RoadNetworkGenerator.ReleaseGenerationResources();
        }
    }
}
