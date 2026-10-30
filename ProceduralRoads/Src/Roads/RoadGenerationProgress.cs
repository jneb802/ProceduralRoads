using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>An immutable observation of the one owned generation. Counts are completed islands, never started islands.</summary>
public sealed class RoadGenerationProgress
{
    public ReadOnlyCollection<RoadIslandProgress> Islands { get; }
    public ReadOnlyCollection<RoadIslandProgress> ActiveIslands { get; }
    public double? TotalKilometres { get; }
    public string PreparationStage { get; }
    public string PreparationUnit { get; }
    public long PreparationCompleted { get; }
    public long PreparationTotal { get; }
    public double PreparationActiveMilliseconds { get; }
    public double PreparationMaxSliceMilliseconds { get; }
    public long RunId { get; }
    public string Phase { get; }
    public int Completed { get; }
    public int Total { get; }
    public double ElapsedSeconds { get; }
    public string? Failure { get; }
    public bool Active => Phase == "Preparing" || Phase == "Generating" || Phase == "Finalizing" || Phase == "Stopping" || Phase == "ReturningToMenu" || Phase == "MenuFailed";
    internal RoadGenerationProgress(long runId, string phase, int completed, int total, double elapsedSeconds, string? failure = null, ReadOnlyCollection<RoadIslandProgress>? islands = null, double? totalKilometres = 0, RoadPreparation? preparation = null)
    {
        PreparationStage=phase=="Preparing" ? preparation?.Stage ?? "" : "";
        PreparationUnit=PreparationStage.Length>0 ? preparation!.Unit : "";
        PreparationCompleted=PreparationStage.Length>0 ? preparation!.Completed : 0;
        PreparationTotal=PreparationStage.Length>0 ? preparation!.Total : 0;
        PreparationActiveMilliseconds=preparation?.ActiveMilliseconds ?? 0;
        PreparationMaxSliceMilliseconds=preparation?.MaxSliceMilliseconds ?? 0;
        Islands=islands ?? Array.AsReadOnly(Array.Empty<RoadIslandProgress>());
        var active=new List<RoadIslandProgress>();
        foreach(var island in Islands) if(island.Active && island.Assignment>0) active.Add(island);
        active.Sort((left,right)=>left.Slot.CompareTo(right.Slot));
        ActiveIslands=active.AsReadOnly(); TotalKilometres=totalKilometres;
        RunId = runId; Phase = phase; Completed = completed; Total = total; ElapsedSeconds = elapsedSeconds; Failure = failure; }
    public string Text => Phase == "Stopping" ? "Stopping generation…" : Phase == "ReturningToMenu" ? "Returning to menu…" : Phase == "MenuFailed" ? "Generation stopped. Return to menu" : Phase == "Preparing" ? PreparationText : Phase == "Finalizing" ? "Finishing roads…" :
        Phase == "Failed" ? "Road generation failed (see the log)" : $"Generating roads: {Completed} of {Total} islands complete";
    public string PreparationText => PreparationStage.Length==0 ? "Preparing roads…" :
        PreparationTotal>0 ? $"{PreparationStage}: {PreparationCompleted:N0} / {PreparationTotal:N0} {PreparationUnit}" :
        PreparationCompleted>0 ? $"{PreparationStage}: {PreparationCompleted:N0} {PreparationUnit}" : $"{PreparationStage}…";

}

public static partial class RoadLifecycleManager
{
    private sealed class Generation
    {
        internal readonly WorldGenerator World;
        internal readonly ZoneSystem Zones;
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly Stopwatch Clock = Stopwatch.StartNew();
        internal RoadPreparation? Preparation;
        internal RoadNetworkGenerator.GenerationWork? Work;
        internal Task? Worker;
        internal bool ExitRequested, CleanupDone, MenuRequested, DrainFaultReported;
        internal double NextPublicationMs;
        internal double PreparationMs;
        internal double ComputeStartedMs;
        internal Generation(WorldGenerator world, ZoneSystem zones) { World = world; Zones = zones; }
    }
    private static Generation? m_generation;
    private static bool m_synchronous;
    private static bool m_initialAttempted;
    internal static void AllowInitialRetry() => m_initialAttempted = false;
#if ROADS_TESTS
    internal static Action? BeforeWorkerCompletionCheck;
    internal static Action? ReturnToMenuForTests;
    internal static Func<bool>? MenuTransitionBlockedForTests;
    internal static bool DisableActivityForTests;
#endif
    private static long m_nextRun;
    private static RoadGenerationProgress m_progress = new(0, "Idle", 0, 0, 0);
    public static RoadGenerationProgress Progress => Volatile.Read(ref m_progress);
    public static bool IsGenerating => m_generation != null || m_synchronous;
    public static bool InitialGenerationActive => m_generation != null;
    internal static bool WorkersOwnWorld => InitialGenerationActive && Progress.Phase != "Finalizing";
    internal static void ThrowIfCancellationRequested() => m_generation?.Cancellation.Token.ThrowIfCancellationRequested();
    private static void SetProgress(string phase, int completed, int total, string? failure = null)
    {
        // Commit holds grid then activity. Snapshot activity completely before reading the grid scalar.
        var islands=m_generation?.CleanupDone==true ? null : m_generation?.Work?.Activity?.Snapshot();
        double? kilometres=RoadSpatialGrid.SnapshotTotalKilometres();
        Volatile.Write(ref m_progress, new RoadGenerationProgress(m_nextRun, phase, completed, total,
            m_generation?.Clock.Elapsed.TotalSeconds ?? 0, failure, islands, kilometres, m_generation?.Preparation));
    }

    private static void BeginInitialGeneration()
    {
        if (IsGenerating || m_initialAttempted || !RoadNetworkGenerator.GenerateOnLoad || ZNet.instance == null || !ZNet.instance.IsServer()) return;
        m_initialAttempted = true;
        m_generation = new Generation(WorldGenerator.instance!, ZoneSystem.instance!);
        m_nextRun++;
        m_generation.Preparation=RoadNetworkGenerator.BeginPreparation(false, placeSitesFirst: true);
        m_generation.Preparation.Cancellation=m_generation.Cancellation.Token;
        SetProgress("Preparing", 0, 0);
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo("Preparing road network generation…");
    }

    /// <summary>Called each Unity frame, independently of the terrain sweep cadence.</summary>
    public static void Tick()
    {
        var run = m_generation;
        if (run == null) return;
        if (!ReferenceEquals(run.World, WorldGenerator.instance) || !ReferenceEquals(run.Zones, ZoneSystem.instance))
        { CancelAndDrain(); return; }
        if(run.ExitRequested) { TickCancellation(run); return; }
        try
        {
            if (run.Worker == null)
            {
                var preparation=run.Preparation!;
                string previousStage=Progress.PreparationStage;
                bool finished=preparation.Advance();
                if(!finished)
                {
                    if(previousStage!=preparation.Stage || run.Clock.Elapsed.TotalMilliseconds>=run.NextPublicationMs)
                    {
                        SetProgress("Preparing",0,0);
                        if(previousStage!=preparation.Stage)
                            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(Progress.PreparationText);
                        run.NextPublicationMs=run.Clock.Elapsed.TotalMilliseconds+250;
                    }
                    return;
                }
                preparation.LogTimings();
                run.PreparationMs=preparation.WallMilliseconds;
                run.Work=preparation.TakeResult();
                preparation.Dispose(); run.Preparation=null;
                if (run.Work == null) { End(run, "Complete"); return; }
                run.Work.Cancellation = run.Cancellation.Token;
                ReportPrepared(run.Work);
                // TaskScheduler.Default: workers never need a Unity continuation in order to terminate.
                run.ComputeStartedMs = run.Clock.Elapsed.TotalMilliseconds;
                run.Worker = Task.Factory.StartNew(run.Work.Compute, run.Cancellation.Token,
                    TaskCreationOptions.None, TaskScheduler.Default);
                return;
            }
            ReportFinished(run.Work!);
#if ROADS_TESTS
            BeforeWorkerCompletionCheck?.Invoke();
#endif
            if (!run.Worker.IsCompleted)
            {
                if(run.Clock.Elapsed.TotalMilliseconds >= run.NextPublicationMs)
                { SetProgress("Generating", Progress.Completed, Progress.Total); run.NextPublicationMs=run.Clock.Elapsed.TotalMilliseconds+250; }
                return;
            }
            run.Worker.GetAwaiter().GetResult();
            // Joining closes publication: the last island may have finished after the earlier drain.
            ReportFinished(run.Work!);
            double computeMs = run.Clock.Elapsed.TotalMilliseconds - run.ComputeStartedMs;
            var finalizeClock = Stopwatch.StartNew();
            SetProgress("Finalizing", Progress.Completed, Progress.Total);
            RoadNetworkGenerator.CompleteGeneration(run.Work!);
            RoadNetworkGenerator.ApplyGeneratedNetworkToLoadedZones();
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo($"Road generation phases: prepare={run.PreparationMs:F0}ms compute={computeMs:F0}ms finalize/apply={finalizeClock.Elapsed.TotalMilliseconds:F0}ms");
            End(run, "Complete");
        }
        catch (Exception error)
        {
            // A faulted Parallel loop has joined every island before its Task completes.
            run.Cancellation.Cancel();
            Drain(run);
            DisposePreparation(run);
            RoadNetworkGenerator.ReleaseGenerationResources();
            if (RoadNetworkGenerator.RoadsGenerated)
            {
                // Loaded-zone application can already have written terrain or spawned bridge ZDOs.
                // Keep its finalized network so those effects retain their identity and can be saved/reapplied.
                // RoadsGenerated is set before the last steps of finalizing (metadata, pending registrations), so
                // this does not claim which step failed.
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogError(
                    "Road network was completed and kept, but a later step failed " +
                    $"(the loaded zones may lack some or all of its terrain and bridges); network retained: {error}");
                End(run, "ApplicationFailed", error.GetBaseException().Message);
            }
            else
            {
                RoadNetworkGenerator.ResetData();
                ProceduralRoadsPlugin.ProceduralRoadsLogger.LogError($"Road generation failed; no network was published: {error}");
                End(run, "Failed", error.GetBaseException().Message);
            }
        }
    }

    /// <summary>A UI request only. The Unity frame must remain free while cancellation drains.</summary>
    public static bool RequestCancelToMenu(long expectedRunId)
    {
        var run=m_generation;
        if(run==null || expectedRunId!=m_nextRun || !ReferenceEquals(run.World,WorldGenerator.instance)
            || !ReferenceEquals(run.Zones,ZoneSystem.instance) || RoadNetworkGenerator.RoadsGenerated
            || Progress.Phase=="Finalizing") return false;
        if(run.ExitRequested) return true;
        run.ExitRequested=true;
        run.Cancellation.Cancel();
        SetProgress("Stopping",Progress.Completed,Progress.Total);
        return true;
    }
    public static bool RetryReturnToMenu(long expectedRunId)
    {
        var run=m_generation;
        if(run==null || expectedRunId!=m_nextRun || !run.ExitRequested || !run.CleanupDone
            || !ReferenceEquals(run.World,WorldGenerator.instance) || !ReferenceEquals(run.Zones,ZoneSystem.instance) || MenuTransitionBlocked()) return false;
        run.MenuRequested=false;
        DispatchMenu(run);
        return true;
    }
    private static bool MenuTransitionBlocked()
    {
#if ROADS_TESTS
        return MenuTransitionBlockedForTests?.Invoke() ?? false;
#else
        return UnifiedPopup.IsVisible() || (Game.instance!=null && Game.instance.IsShuttingDown());
#endif
    }
    private static void TickCancellation(Generation run)
    {
        if(run.CleanupDone) return;
        if(run.Worker!=null && !run.Worker.IsCompleted)
        {
            if(run.Clock.Elapsed.TotalMilliseconds>=run.NextPublicationMs)
            { SetProgress("Stopping",Progress.Completed,Progress.Total); run.NextPublicationMs=run.Clock.Elapsed.TotalMilliseconds+250; }
            return;
        }
        Drain(run, true); // IsCompleted above proves this observes results without waiting on live workers.
        DisposePreparation(run);
        RoadNetworkGenerator.ReleaseGenerationResources();
        run.Work?.Activity?.Close();
        RoadNetworkGenerator.ResetData();
        run.CleanupDone=true;
        SetProgress("ReturningToMenu",Progress.Completed,Progress.Total);
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo("Road generation stopped; unpublished road data discarded. Returning to menu.");
        DispatchMenu(run);
    }
    private static void DispatchMenu(Generation run)
    {
        if(run.MenuRequested) return;
        if(MenuTransitionBlocked())
        {
            SetProgress("MenuFailed",Progress.Completed,Progress.Total,"A game dialog or shutdown already owns the menu transition.");
            return;
        }
        run.MenuRequested=true;
        SetProgress("ReturningToMenu",Progress.Completed,Progress.Total);
        try
        {
#if ROADS_TESTS
            ReturnToMenuForTests?.Invoke();
#else
            // Logout(false) is not a reliable no-save transition in the actual client.
            // Vanilla may save the world; the unpublished Roads network has already been discarded.
            Game.instance.Logout();
#endif
        }
        catch(Exception error)
        {
            SetProgress("MenuFailed",Progress.Completed,Progress.Total,error.GetBaseException().Message);
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogError($"Road generation stopped, but return to menu failed: {error}");
        }
    }

    internal static void ReportPrepared(RoadNetworkGenerator.GenerationWork work)
    {
#if ROADS_TESTS
        work.Activity = DisableActivityForTests ? null : new RoadIslandActivity(work.Workers, work.Islands.Count);
#else
        work.Activity = new RoadIslandActivity(work.Workers, work.Islands.Count);
#endif
        SetProgress("Generating", 0, work.Islands.Count);
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo($"Generating roads on {work.Islands.Count} islands ({work.Workers} thread(s)). This takes a few minutes.");
    }
    internal static void ReportFinished(RoadNetworkGenerator.GenerationWork work)
    {
        int completed = Progress.Completed;
        while (work.Finished.TryDequeue(out string? detail))
        {
            completed++;
            SetProgress("Generating", completed, work.Islands.Count);
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo($"  {completed} of {work.Islands.Count} done - {detail}");
        }
    }
    /// <summary>The synchronous generation already reported "Complete" when a step after it failed: say so.</summary>
    internal static void ReportApplicationFailed(Exception error) =>
        SetProgress("ApplicationFailed", Progress.Completed, Progress.Total, error.GetBaseException().Message);
    private static void End(Generation run, string phase, string? failure = null)
    {
        DisposePreparation(run);
        if(!run.CleanupDone) { RoadNetworkGenerator.ReleaseGenerationResources(); run.Work?.Activity?.Close(); }
        SetProgress(phase, Progress.Completed, Progress.Total, failure);
        m_generation = null;
        run.Cancellation.Dispose();
        RoadNetworkGenerator.ApplyPendingRegistrations();
        if (phase == "Complete") TellIncluded();
    }
    private static void DisposePreparation(Generation run)
    {
        var preparation=run.Preparation; run.Preparation=null;
        preparation?.Dispose();
    }
    private static void Drain(Generation run, bool reportFault = false)
    {
        if (run.Worker == null) return;
        try { run.Worker.GetAwaiter().GetResult(); }
        catch (Exception error)
        {
            if(reportFault && !run.DrainFaultReported)
            {
                IEnumerable<Exception> faults=error is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : new[] { error };
                foreach(var fault in faults)
                    if(fault is not OperationCanceledException)
                        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogError($"Road worker failed while stopping generation: {fault}");
                run.DrainFaultReported=true;
            }
        }
    }
    /// <summary>Drain before destroying the world, unsealing its caches, or resetting any shared network state.</summary>
    public static void CancelAndDrain()
    {
        var run = m_generation;
        if (run == null) return;
        run.Cancellation.Cancel();
        Drain(run, true);
        DisposePreparation(run);
        if(!run.CleanupDone)
        {
            RoadNetworkGenerator.ReleaseGenerationResources();
            run.Work?.Activity?.Close();
            RoadNetworkGenerator.ResetData();
            run.CleanupDone=true;
        }
        End(run, "Cancelled");
    }
    internal static void RunSynchronous(Action action)
    {
        if (IsGenerating) throw new InvalidOperationException("Road generation is busy.");
        m_synchronous = true;
        m_nextRun++;
        SetProgress("Preparing", 0, 0);
        try { action(); SetProgress("Complete", Progress.Completed, Progress.Total); }
        catch { RoadNetworkGenerator.ReleaseGenerationResources(); if (!RoadNetworkGenerator.RoadsGenerated) RoadNetworkGenerator.ResetData(); SetProgress("Failed", Progress.Completed, Progress.Total); throw; }
        finally { m_synchronous = false; RoadNetworkGenerator.ApplyPendingRegistrations(); }
    }
}
