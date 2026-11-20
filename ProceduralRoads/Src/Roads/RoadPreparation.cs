using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace ProceduralRoads;

/// <summary>Resumable main-thread work owned by one road generation, also drained by synchronous callers.</summary>
internal sealed class RoadPreparation : IDisposable
{
    private IEnumerator<bool>? steps;
    private readonly Func<double> clock;
    private readonly double started;
    private double segmentStart, stageStarted;
    private bool advancing, disposed;
    private int batch;
    private StageMeasure? measure;
    private readonly List<StageMeasure> measures = new();
    internal RoadNetworkGenerator.GenerationWork? Result;
    internal CancellationToken Cancellation;
    internal string Stage { get; private set; } = "";
    internal string Unit { get; private set; } = "";
    internal long Completed { get; private set; }
    internal long Total { get; private set; }
    internal double ActiveMilliseconds { get; private set; }
    internal double MaxSliceMilliseconds { get; private set; }
    internal double WallMilliseconds => clock()-started;
    internal bool Finished { get; private set; }
#if ROADS_TESTS
    internal static int CheckpointsPerTickForTests=int.MaxValue;
    internal static Func<double>? ClockForTests;
#endif
    private sealed class StageMeasure
    {
        internal string Name="";
        internal double Active, Wall;
        internal long Units;
    }
    internal RoadPreparation(Func<RoadPreparation,IEnumerable<bool>> work)
    {
#if ROADS_TESTS
        clock=ClockForTests ?? Milliseconds;
#else
        clock=Milliseconds;
#endif
        started=stageStarted=clock();
        steps=work(this).GetEnumerator();
    }
    private static double Milliseconds()=>Stopwatch.GetTimestamp()*1000d/Stopwatch.Frequency;
    internal void BeginStage(string name,long total=0,string unit="items",string? timingGroup=null)
    {
        double now=clock();
        Accrue(now);
        if(measure!=null) { measure.Wall=now-stageStarted; measure.Units=Completed; }
        Stage=name; Unit=unit; Total=total; Completed=0; batch=0; stageStarted=now;
        measure=new StageMeasure {Name=timingGroup ?? name}; measures.Add(measure);
    }
    // Called for every operation, but a clock is read only at a bounded iterator checkpoint.
    internal void CompleteUnit() => Completed++;
    internal bool Checkpoint(int count=1)
    {
        Cancellation.ThrowIfCancellationRequested();
        Completed+=count;
        if(++batch<128) return false;
        batch=0; return true;
    }
    internal bool Advance(bool drain=false)
    {
        if(disposed) throw new ObjectDisposedException(nameof(RoadPreparation));
        if(Finished) return true;
        double begin=clock(); segmentStart=begin; advancing=true;
        int checkpoints=0;
        try
        {
            do
            {
                Cancellation.ThrowIfCancellationRequested();
                string prior=Stage;
                if(!steps!.MoveNext()) { Finished=true; break; }
                checkpoints++;
                // A phase boundary also gives its text a chance to render before the phase does work.
                if(!drain && prior!=Stage) break;
#if ROADS_TESTS
                if(!drain && checkpoints>=CheckpointsPerTickForTests) break;
#endif
            } while(drain || clock()-begin<8);
        }
        finally
        {
            double now=clock(); Accrue(now); advancing=false;
            MaxSliceMilliseconds=Math.Max(MaxSliceMilliseconds,now-begin);
            if(Finished && measure!=null) { measure.Wall=now-stageStarted; measure.Units=Completed; }
        }
        return Finished;
    }
    private void Accrue(double now)
    {
        if(!advancing) return;
        double active=now-segmentStart;
        ActiveMilliseconds+=active;
        if(measure!=null) measure.Active+=active;
        segmentStart=now;
    }
    internal void Drain() { while(!Advance(true)) { } }
    internal RoadNetworkGenerator.GenerationWork? TakeResult() { var result=Result; Result=null; return result; }
    internal void LogTimings()
    {
        var log=ProceduralRoadsPlugin.ProceduralRoadsLogger;
        // Coasts report per-island text, but retain one bounded timing record per kind of work.
        var grouped=new Dictionary<string,StageMeasure>();
        foreach(var stage in measures)
        {
            if(!grouped.TryGetValue(stage.Name,out var total)) grouped[stage.Name]=total=new StageMeasure {Name=stage.Name};
            total.Active+=stage.Active; total.Wall+=stage.Wall; total.Units+=stage.Units;
        }
        foreach(var stage in grouped.Values)
            log.LogInfo($"Road preparation stage: {stage.Name} active={stage.Active:F1}ms wall={stage.Wall:F1}ms units={stage.Units}");
        log.LogInfo($"Road preparation: active={ActiveMilliseconds:F1}ms wall={WallMilliseconds:F1}ms max-slice={MaxSliceMilliseconds:F1}ms");
    }
    // A custom priming iterator can fail both in MoveNext and Dispose. Keep the primary failure.
    internal static IEnumerable<bool> Own(IEnumerable<bool> work)
    {
        var enumerator=work.GetEnumerator();
        try { while(enumerator.MoveNext()) yield return enumerator.Current; }
        finally { DisposeSteps(enumerator); }
    }
    private static void DisposeSteps(IDisposable? owned)
    {
        try { owned?.Dispose(); }
        catch(Exception error)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogError($"Road preparation cleanup failed; continuing owned reset: {error}");
        }
    }
    public void Dispose()
    {
        if(disposed) return;
        disposed=true;
        try { DisposeSteps(steps); }
        finally { steps=null; Result=null; }
    }
}
