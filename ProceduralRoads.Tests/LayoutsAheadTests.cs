using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

public sealed class LayoutsAheadTests : IDisposable
{
    private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Type Ahead = typeof(RoadStairRuns.LayoutsAhead);
    private static object Gate => Ahead.GetField("s_gate", StaticPrivate)!.GetValue(null)!;
    private static object Queue => Ahead.GetField("s_queue", StaticPrivate)!.GetValue(null)!;
    private static int State(object job) => (int)job.GetType().GetField("State")!.GetValue(job)!;
    private readonly ValheimWorldScope scope = new();
    private readonly int workers = RoadParallel.Configured;

    private sealed class Slope : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f + 0.5f * Mathf.Clamp(z, 0f, 24f);
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
    }

    public LayoutsAheadTests()
    {
        scope.WithWorld(new Slope()).WithZoneSystem();
        RoadNetworkGenerator.Reset();
        RoadParallel.Configured = 2;
        RoadParallel.Islands(1);
        RoadStairRuns.LayoutsAhead.Start();
    }

    public void Dispose()
    {
        StairLayout.BeforeSearchStopForTests = null;
        RoadStairRuns.LayoutsAhead.Drop();
        RoadStairRuns.LayoutsAhead.Stop();
        RoadParallel.Islands(0);
        RoadParallel.Configured = workers;
        RoadNetworkGenerator.Reset();
        scope.Dispose();
    }

    [Fact]
    public void AFirstLayoutThatSucceedsDropsItsQueuedAlternatives()
    {
        var path = new List<Vector2> { new(0f, -8f), new(0f, 0f), new(0f, 8f), new(0f, 16f), new(0f, 24f), new(0f, 32f) };
        var steps = new List<(Vector2, Vector2)> { (path[1], path[2]), (path[2], path[3]), (path[3], path[4]) };
        int laid = RoadStairRuns.LayoutsAhead.Laid;
        // Keep the real spare workers outside the queue until the first layout has decided its stretch.
        lock (Gate)
        {
            Assert.NotNull(RoadStairRuns.Lay(path, steps, new List<RoadCrossing>(), WorldGenerator.instance!));
            var jobs = new List<object>();
            foreach (object job in (IEnumerable)Queue) jobs.Add(job);
            Assert.NotEmpty(jobs);
            Assert.All(jobs, job => Assert.Equal(3, State(job)));
        }
        RoadStairRuns.LayoutsAhead.Stop();
        Assert.Equal(laid, RoadStairRuns.LayoutsAhead.Laid);
    }

    private static object Enqueue(Func<StairPlan> make)
    {
        Type type = Ahead.GetNestedType("Job", BindingFlags.NonPublic)!;
        object job = Activator.CreateInstance(type, new object[] { new RoadStairRuns.PlanKey(new Vector2(0f, 0f), new Vector2(0f, 24f), StairKit.Stone,
            RoadStairRuns.LayoutGround.Lattice(WorldGenerator.instance!), null, null, null, StairCover.RoadStaircase, 0,
            null, null, StairLayout.MaxExpansions, int.MaxValue, int.MaxValue, true, null, null, null, null, null,
            0f, false, RoadStairRuns.GroundGeneration, false, StairLayout.EndGuardReach), make, 0, false })!;
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!;
        list.Add(job);
        Ahead.GetProperty("Mine", StaticPrivate)!.SetValue(null, list);
        lock (Gate)
        {
            Queue.GetType().GetMethod("Enqueue")!.Invoke(Queue, new[] { job });
            Monitor.PulseAll(Gate);
        }
        return job;
    }

    [Fact]
    public async Task StopAbandonsARunningLayoutAtItsNextSearchStop()
    {
        using var entered = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        using var escape = new ManualResetEventSlim();
        int checks = 0;
        StairLayout.BeforeSearchStopForTests = () =>
        {
            if (Interlocked.Increment(ref checks) == 1)
            {
                entered.Set();
                if (!proceed.Wait(5000)) throw new TimeoutException("search was not released");
            }
            else
            {
                escape.Wait(5000);
                throw new InvalidOperationException("search continued after stop");
            }
        };
        Enqueue(() => StairLayout.Build(new Vector2(0f, 0f), new Vector2(0f, 20000f), (x, z) => z * 0.5f,
            maxExpansions: int.MaxValue));
        Assert.True(entered.Wait(5000), "layout did not start searching");
        Task stopped = Task.Run(RoadStairRuns.LayoutsAhead.Stop);
        bool prompt;
        try
        {
            Assert.True(SpinWait.SpinUntil(() => (bool)Ahead.GetField("s_stopping", StaticPrivate)!.GetValue(null)!, 5000));
            proceed.Set();
            prompt = await Task.WhenAny(stopped, Task.Delay(1000)) == stopped;
        }
        finally { proceed.Set(); escape.Set(); await stopped; }
        Assert.True(prompt, "Stop waited for an abandoned layout past its next search stop");
        Assert.Equal(1, checks);
    }

    [Fact]
    public async Task CancellationWakesTakeAndReturnsItsWaitingWorker()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        StairLayout.BeforeSearchStopForTests = () =>
        {
            entered.Set();
            if (!release.Wait(5000)) throw new TimeoutException("search was not released");
        };
        object job = Enqueue(() => StairLayout.Build(new Vector2(0f, 0f), new Vector2(0f, 24f), (x, z) => z * 0.5f));
        Assert.True(entered.Wait(5000));
        Type generationType = typeof(RoadLifecycleManager).GetNestedType("Generation", BindingFlags.NonPublic)!;
        object generation = Activator.CreateInstance(generationType, BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { WorldGenerator.instance!, ZoneSystem.instance!, new RoadLifecycleManager.GenerationRequest() }, null)!;
        FieldInfo current = typeof(RoadLifecycleManager).GetField("m_generation", StaticPrivate)!;
        current.SetValue(null, generation);
        var cancellation = (CancellationTokenSource)generationType.GetField("Cancellation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(generation)!;
        Task<Exception?> taking = Task.Run(() =>
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(job.GetType()))!;
            list.Add(job);
            Ahead.GetProperty("Mine", StaticPrivate)!.SetValue(null, list);
            try { RoadStairRuns.LayoutsAhead.Take((RoadStairRuns.PlanKey)job.GetType().GetField("Key")!.GetValue(job)!, out _, out _); return null; }
            catch (Exception error) { return error; }
            finally { RoadStairRuns.LayoutsAhead.Drop(); }
        });
        bool prompt;
        try
        {
            FieldInfo waiting = typeof(RoadParallel).GetField("s_waiting", StaticPrivate)!;
            Assert.True(SpinWait.SpinUntil(() => (int)waiting.GetValue(null)! == 1, 5000));
            cancellation.Cancel();
            prompt = await Task.WhenAny(taking, Task.Delay(1000)) == taking;
            if (prompt) Assert.Equal(0, (int)waiting.GetValue(null)!);
        }
        finally
        {
            release.Set();
            await taking;
            RoadStairRuns.LayoutsAhead.Stop();
            current.SetValue(null, null);
            cancellation.Dispose();
        }
        Assert.True(prompt, "Take stayed asleep after generation cancellation");
        Assert.IsAssignableFrom<OperationCanceledException>(await taking);
    }
}
