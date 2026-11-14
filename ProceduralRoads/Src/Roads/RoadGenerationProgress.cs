using System;
using System.Diagnostics;
using System.Linq;
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
        internal readonly GenerationRequest Request;
        /// <summary>An operator's run wrote the network it replaces into the world first (<see cref="BeginForOperator"/>).</summary>
        internal bool KeptOld;
        /// <summary>The zones that network left alone as the run began, put back with it (<see cref="BringBackOldNetwork"/>).</summary>
        internal NetworkCutoff.State? KeptCutoff;
        /// <summary>Why an operator's run was stopped other than by its Cancel (<see cref="WorldCannotHold"/>).</summary>
        internal string? StoppedBecause;
        /// <summary>Ended as the world goes away (CancelAndDrain): nothing is read back; the world's save holds the old network.</summary>
        internal bool WorldGoing;
        internal Generation(WorldGenerator world, ZoneSystem zones, GenerationRequest request) { World = world; Zones = zones; Request = request; }
    }

    /// <summary>What a generation was asked for: the load's (none of these), or an operator's (road_generate, road_regen_island).</summary>
    internal sealed class GenerationRequest
    {
        /// <summary>An operator's (road_generate, road_regen_island), in the world: the network it replaces stays unless it
        /// publishes one, and Cancel stops it without leaving the world.</summary>
        internal bool Operator;
        /// <summary>Replace a network already there.</summary>
        internal bool Force;
        /// <summary>Only the island at this point (road_regen_island), replacing the whole network.</summary>
        internal Vector3? Island;
        /// <summary>How the published network goes into the world, instead of the load's (zones loaded before it).</summary>
        internal Action<RoadNetworkGenerator.GenerationWork>? Apply;
        /// <summary>Told as the generation ends: its phase (Complete, Failed, ApplicationFailed, Cancelled), why when it did
        /// not complete or published nothing, and for an operator's run that published nothing whether the network it was to
        /// replace was read back (null: there was none, or the world is going).</summary>
        internal Action<string, string?, bool?>? Ended;
    }
    private static Generation? m_generation;
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
    /// <summary>A generation is running (the load's, the spawn fallback's, or an operator's): the world is held for it.</summary>
    /// <summary>How often the panel's progress is republished while the run goes on.</summary>
    private const double PublishEveryMs = 250;
    public static bool IsGenerating => m_generation != null;
    /// <summary>The running generation is an operator's (road_generate, road_regen_island): Cancel keeps the old roads.</summary>
    public static bool OperatorRunActive => m_generation?.Request.Operator == true;
    internal static bool WorkersOwnWorld => IsGenerating && Progress.Phase != "Finalizing";
    /// <summary>Wake a worker waiting for generation work when the same generation's stop is requested.</summary>
    internal static CancellationTokenRegistration WakeOnCancellation(Action wake) =>
        m_generation?.Cancellation.Token.Register(wake) ?? default;
    internal static void ThrowIfCancellationRequested() => m_generation?.Cancellation.Token.ThrowIfCancellationRequested();
    private static void SetProgress(string phase, int completed, int total, string? failure = null)
    {
        // Commit holds grid then activity. Snapshot activity completely before reading the grid scalar.
        var islands=m_generation?.CleanupDone==true ? null : m_generation?.Work?.Activity?.Snapshot();
        double? kilometres=RoadSpatialGrid.SnapshotTotalKilometres();
        Volatile.Write(ref m_progress, new RoadGenerationProgress(m_nextRun, phase, completed, total,
            m_generation?.Clock.Elapsed.TotalSeconds ?? 0, failure, islands, kilometres, m_generation?.Preparation));
    }

    /// <summary>The world's first network, once (a failed attempt is not retried): at load under the loading screen, or for
    /// a host whose load went past it, when its player spawns.</summary>
    private static void BeginInitialGeneration()
    {
        if (IsGenerating || m_initialAttempted || !RoadLifecycleManager.HoldsTheNetwork) return;
        if (!RoadNetworkGenerator.GenerateOnLoad)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo("PROCEDURALROADS_GENERATE_ROADS_ON_LOAD is off: " +
                "no roads until road_generate or road_regen_island");
            return;
        }
        m_initialAttempted = true;
        Begin(new GenerationRequest());
    }

    /// <summary>
    /// Start a generation: the one way a network is generated in the game (the load; road_generate and road_regen_island,
    /// <see cref="BeginForOperator"/>). Its preparation runs on the main thread over frames, the locations it will visit placed
    /// first (RoadNetworkGenerator.LocationsFirst: placing waits for the game's frames), then workers build the roads and
    /// <see cref="Tick"/> finishes it. Meanwhile the world holds still (<see cref="HoldsTheWorld"/>: no zone is
    /// created under the workers) and the generation panel shows how it goes. False when a generation or a manual road is
    /// already under way.
    /// </summary>
    internal static bool Begin(GenerationRequest request) => Start(request) != null;

    /// <summary>What an operator is told when a generation or a manual road is already under way.</summary>
    internal const string Busy = "Road generation is busy.";

    private static Generation? Start(GenerationRequest request)
    {
        if (IsGenerating || ManualRoads.Busy || WorldGenerator.instance == null || ZoneSystem.instance == null) return null;
        m_generation = new Generation(WorldGenerator.instance, ZoneSystem.instance, request);
        m_nextRun++;
        m_generation.Preparation=RoadNetworkGenerator.BeginPreparation(request.Force, request.Island);
        m_generation.Preparation.Cancellation=m_generation.Cancellation.Token;
        SetProgress("Preparing", 0, 0);
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo("Preparing road network generation…");
        // The peers holding the network are told it is gone; they get the new one (or the old one back) as the run ends.
        RoadNetworkSync.Gone();
        return m_generation;
    }

    /// <summary>
    /// road_generate (the whole world) and road_regen_island (<paramref name="island"/>): the operator's generation, the
    /// load's own (<see cref="Begin"/>), so its locations are placed before their roads as at load. The explored land is
    /// treated as <paramref name="answer"/> says, the loading screen's answers carried out by the same owners
    /// (ExistingWorldFirstLoad, <see cref="IncludeExploredLand"/>, NetworkCutoff): none given, new land only over a road
    /// network and the first load's own answer on a world with none (ExistingWorldFirstLoad.CommandDefault). The network it
    /// replaces is written into the world first, as a save writes it, and comes back from there if this one publishes nothing
    /// (it fails, finds nothing to build, or is cancelled). Once published, the network goes into the loaded zones it may
    /// write, and the old network's bridge pieces there are replaced. <paramref name="tell"/> hears how it goes, each line
    /// logged too (a dedicated server's operator reads the log). Returns why it did not start, or null.
    /// </summary>
    internal static string? BeginForOperator(Vector3? island, Action<string> tell, Action<RoadNetworkGenerator.GenerationWork>? published = null,
        string? command = null, bool force = false, DateTime? now = null, ExistingWorldRoads? answer = null)
    {
        if (!HoldsTheNetwork) return NotTheServer;
        if (ChoicePending) return "The host is still asked what this world's first road network does";
        if (IsGenerating || ManualRoads.Busy) return Busy;
        if (WorldCannotHold?.Invoke() is string held) return held;
        // A saved network this build could not read counts as a network: the run would replace it, unread, for good.
        bool unread = !RoadNetworkGenerator.NetworkInMemory && RoadNetworkPersistence.LastLoadUnreadable;
        bool overNetwork = RoadNetworkGenerator.NetworkInMemory || unread;
        ExistingWorldRoads chosen = answer ?? ExistingWorldFirstLoad.CommandDefault(overNetwork);
        if (chosen == ExistingWorldRoads.Ask) chosen = ExistingWorldFirstLoad.CommandDefault(overNetwork);
        if (chosen == ExistingWorldRoads.Skip) return SkipText;
        // A command (road_generate, road_regen_island) over a network asks first, as a manual road asks about zones players
        // changed (ManualRoads.ConfirmAgain): the same command with the same answer again within the window, or force.
        // road_regen_island is the same command again on the same island, wherever on it the operator stands
        // (ManualRoads.IslandAt); off any island, in the same zone.
        if (command != null && !force && overNetwork
            && !ManualRoads.ConfirmAgain($"operator|{command}|{chosen}|{(island is Vector3 at ? "island " + (ManualRoads.IslandAt(at)?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none " + ZoneSystem.GetZone(at).x.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + ZoneSystem.GetZone(at).y.ToString(System.Globalization.CultureInfo.InvariantCulture)) : "world")}", now ?? DateTime.UtcNow))
            return unread ? UnreadWarning(command, chosen) : ReplaceWarning(command, chosen, island);
        void Tell(string line) { ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(line); tell(line); }
        // What a save would write, written now: the network this replaces comes back from it if none is published.
        bool keep = RoadNetworkGenerator.NetworkInMemory;
        if (keep) OnPrepareSave();
        NetworkCutoff.State cutoff = NetworkCutoff.Keep();
        int explored = NetworkCutoff.Explored.Count, kept = 0;
        if (chosen == ExistingWorldRoads.IncludeExplored)
        {
            // Saved, then backed up, before anything is written; the zones players changed kept, and for one island the
            // land off it.
            Island? only = island is Vector3 on ? ManualRoads.IslandAt(on) : null;
            if (!IncludeExploredLand(only, saveFirst: true, out string? backup, out kept)) return NotBackedUp(command ?? "road_generate");
            if (backup != null) Tell($"The world is backed up as {backup} (Manage saves restores it).");
        }
        else NetworkCutoff.IncludeExplored(null);
        var run = Start(new GenerationRequest
        {
            Operator = true,
            Force = true,
            Island = island,
            Apply = work =>
            {
                if (work.Summary != null) tell(work.Summary);
                else
                {
                    Tell("Road generation complete!");
                    Tell($"  Stored road-point records: {RoadSpatialGrid.StoredPointRecords}");
                    Tell($"  Committed planned-path length: {RoadSpatialGrid.Statistics.LengthText()}");
                    Tell($"  Grid cells with roads: {RoadSpatialGrid.GridCellsWithRoads}");
                }
                int zones = RoadTerrainModifier.ApplyToLoadedZones();
                Tell($"Queued road terrain for {zones} loaded zone(s).");
                var (destroyed, bridgeZones) = StructurePlacement.RespawnFromPlans();
                if (destroyed > 0) Tell($"Removed {destroyed} bridge pieces of the previous network.");
                if (bridgeZones > 0) Tell($"Spawned bridges into {bridgeZones} zone(s).");
                if (explored > 0)
                    Tell(chosen == ExistingWorldRoads.IncludeExplored ? ExistingWorldFirstLoad.KeptNote(kept) : NewLandOnlyNote);
                published?.Invoke(work);
            },
            Ended = (phase, why, keptOld) =>
            {
                string roads = keptOld == true ? " The roads are as they were." : keptOld == false
                    ? " The roads it was to replace could not be read back; the world's last save still holds them (see the log)." : "";
                if (phase == "Cancelled") Tell("Road generation cancelled" + (why != null ? $": {why}." : ".") + roads);
                else if (why != null) Tell((phase == "Complete" ? $"Nothing was generated: {why}." : $"Road generation {phase}: {why}.") + roads);
                else if (phase != "Complete") Tell($"Road generation {phase}. Check the log for details." + roads);
            },
        });
        if (run == null) { NetworkCutoff.PutBack(cutoff); return Busy; }
        run.KeptOld = keep;
        run.KeptCutoff = cutoff;
        Tell(StartText(chosen, explored, kept));
        return null;
    }

    /// <summary>What an operator is told as road_generate or road_regen_island starts: the world holds still, and for how long.</summary>
    internal const string HeldWhileBuilding =
        "Locations first, then the roads; this can take many minutes (road_progress). The world holds still meanwhile: you stay where you stand.";

    /// <summary>The line an operator's run starts with: how it treats the explored land, and how long it may take.</summary>
    internal static string StartText(ExistingWorldRoads chosen, int explored, int kept) =>
        explored == 0 ? "Generating roads. " + HeldWhileBuilding
        : chosen == ExistingWorldRoads.IncludeExplored
            ? $"Generating roads through the explored land too, round the {kept} zone(s) with your buildings or terrain edits. " + HeldWhileBuilding
            : $"Generating roads in new land only: the {explored} explored zone(s) stay as they are. " + HeldWhileBuilding;

    /// <summary>What a new-land-only network over explored land is told once it is published.</summary>
    internal const string NewLandOnlyNote =
        "The explored land is left as it is, the old roads in it too; the new roads are laid as new land is explored.";

    /// <summary>What Skip is told: nothing is run.</summary>
    internal const string SkipText = "Skip road building: nothing was run, and the roads are as they were.";

    /// <summary>What a command naming IncludeExplored is told when the world could not be saved and backed up.</summary>
    internal static string NotBackedUp(string command) =>
        "Not run: the world could not be saved and backed up, so no road is built through the explored land. " +
        $"{command} NewZonesOnly builds roads in new land only, without a backup.";

    /// <summary>What road_generate's and road_regen_island's help say of their answers and the confirm (<see cref="BeginForOperator"/>),
    /// for the window it keeps (ManualRoads.RepeatHint).</summary>
    internal static string AsksFirstHelp =>
        " The land explored so far is treated as you choose: NewZonesOnly builds only in land explored from now on (the default "
        + "over a road network); IncludeExplored saves and backs up the world, then builds through the explored land too, round "
        + "the zones with your buildings or terrain edits (the default on a world with no network); Skip runs nothing. It can "
        + "take many minutes. Over a generated network it asks first:" + ManualRoads.RepeatHint("run it");

    /// <summary>The three answers as a command lists them, the one it runs marked, each with its own words and what it does
    /// over the network it would replace.</summary>
    private static string AnswerList(string command, ExistingWorldRoads chosen, int explored) =>
        $" {explored} zone(s) of this world are explored. Choose how the new network treats them:"
        + string.Concat(new[] { ExistingWorldRoads.NewZonesOnly, ExistingWorldRoads.IncludeExplored, ExistingWorldRoads.Skip }.Select(a =>
            $"\n- {ExistingWorldFirstLoad.Label(a)} ({command} {a}{(a == chosen ? ", run by this command" : "")}): "
            + (a == ExistingWorldRoads.Skip ? "" : ExistingWorldFirstLoad.Detail(a) + " ") + ExistingWorldFirstLoad.OverNetworkNote(a)))
        + $"\nIt can take many minutes; the world holds still meanwhile."
        + "\n" + ManualRoads.RepeatHint(Lower(ExistingWorldFirstLoad.Label(chosen))).TrimStart();

    private static string Lower(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text.Substring(1);

    /// <summary>What a command is told when it would replace the network that stands: roads, length, staircases, bridges, then
    /// the answers.</summary>
    internal static string ReplaceWarning(string command, ExistingWorldRoads chosen, Vector3? island = null) =>
        $"Not run yet: {command} would replace the generated road network"
        + (island is Vector3 at ? FormattableString.Invariant($" with the roads of the island at ({at.x:F0}, {at.z:F0}) only") : "")
        + $": {RoadNetworkGenerator.GetRoadStartPoints().Count} road(s), "
        + $"{RoadSpatialGrid.Statistics.LengthText("F1", 1000f, " km")}, {StairStructures.Runs.Count} staircase(s), "
        + $"{RoadNetworkGenerator.GetRoadCrossings().Count(c => c.Kind == CrossingKind.Bridge)} bridge(s), and the structures laid with it."
        + (island != null ? " Land off this island is left as it is whatever you choose." : "")
        + AnswerList(command, chosen, NetworkCutoff.Explored.Count);

    /// <summary>What a command is told when it would replace a saved network this build could not read.</summary>
    internal static string UnreadWarning(string command, ExistingWorldRoads chosen) =>
        $"Not run yet: {command} would replace this world's saved road network, which this build could not read (it is "
        + "left as it is until then; a build that reads it keeps it)." + AnswerList(command, chosen, NetworkCutoff.Explored.Count);

    /// <summary>An operator's run that published nothing: the network it was to replace back as it was written into the
    /// world at its start (<see cref="BeginForOperator"/>), as a load reads it, and the zones it left alone as they were
    /// then (a load would count only the zones explored before this session). After the run has ended (a load is refused
    /// while one runs). Null where there is nothing to read back, else whether it was read back.</summary>
    private static bool? BringBackOldNetwork(Generation run)
    {
        if (!run.Request.Operator || run.WorldGoing || run.Work?.Published == true) return null;
        if (!ReferenceEquals(run.World, WorldGenerator.instance) || !ReferenceEquals(run.Zones, ZoneSystem.instance)) return null;
        // Refused before it cleared anything (an ocean point): the network is still in memory, its cutoff as it was.
        if (RoadNetworkGenerator.NetworkInMemory) { if (run.KeptCutoff != null) NetworkCutoff.PutBack(run.KeptCutoff); return run.KeptOld ? true : null; }
        if (!run.KeptOld) { if (run.KeptCutoff != null) NetworkCutoff.PutBack(run.KeptCutoff); return null; }
        RoadNetworkGenerator.ResetData();
        bool read = RoadNetworkGenerator.TryLoadGlobalRoadData();
        if (read) RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        if (run.KeptCutoff != null) NetworkCutoff.PutBack(run.KeptCutoff);
        if (read) ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo("Road generation published nothing: the network it was to replace is kept.");
        else ProceduralRoadsPlugin.ProceduralRoadsLogger.LogError("Road generation published nothing, and the network it was to replace could not be read back.");
        return read;
    }

    /// <summary>
    /// Why the world cannot be held for an operator's generation now, or null (RoadGeneration_Patch: another player
    /// connected, the host on a ship, riding, dead or in a teleport). Asked as the run would start, and every frame it runs:
    /// a player who connects meanwhile stops it, its old roads kept.
    /// </summary>
    internal static Func<string?>? WorldCannotHold;

    /// <summary>Called each Unity frame, independently of the terrain sweep cadence.</summary>
    public static void Tick()
    {
        var run = m_generation;
        if (run == null) return;
        if (!ReferenceEquals(run.World, WorldGenerator.instance) || !ReferenceEquals(run.Zones, ZoneSystem.instance))
        { CancelAndDrain(); return; }
        // An operator's run stops when the world can no longer be held for it (a player connected): its old roads are kept.
        if(!run.ExitRequested && run.Request.Operator && run.Work?.Published != true && Progress.Phase!="Finalizing"
            && WorldCannotHold?.Invoke() is string stop)
        {
            run.StoppedBecause=stop;
            run.ExitRequested=true;
            run.Cancellation.Cancel();
            SetProgress("Stopping",Progress.Completed,Progress.Total);
        }
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
                        run.NextPublicationMs=run.Clock.Elapsed.TotalMilliseconds+PublishEveryMs;
                    }
                    return;
                }
                preparation.LogTimings();
                run.PreparationMs=preparation.WallMilliseconds;
                run.Work=preparation.TakeResult();
                string? refusal=preparation.Refusal;
                preparation.Dispose(); run.Preparation=null;
                if (run.Work == null) { End(run, "Complete", told: refusal); return; }
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
                { SetProgress("Generating", Progress.Completed, Progress.Total); run.NextPublicationMs=run.Clock.Elapsed.TotalMilliseconds+PublishEveryMs; }
                return;
            }
            run.Worker.GetAwaiter().GetResult();
            // Joining closes publication: the last island may have finished after the earlier drain.
            ReportFinished(run.Work!);
            double computeMs = run.Clock.Elapsed.TotalMilliseconds - run.ComputeStartedMs;
            var finalizeClock = Stopwatch.StartNew();
            SetProgress("Finalizing", Progress.Completed, Progress.Total);
            RoadNetworkGenerator.CompleteGeneration(run.Work!);
            if (run.Request.Apply != null) run.Request.Apply(run.Work!);
            else RoadNetworkGenerator.ApplyGeneratedNetworkToLoadedZones();
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
            if (run.Work?.Published == true)
            {
                // Loaded-zone application can already have written terrain or spawned bridge ZDOs.
                // Keep its finalized network so those effects retain their identity and can be saved/reapplied.
                // Published is set before the last steps of finalizing (metadata, pending registrations), so
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

    /// <summary>A UI request only (the panel's Cancel). The Unity frame must remain free while cancellation drains. The load's
    /// generation then returns to the menu; an operator's stops, its old network kept, and the world goes on.</summary>
    public static bool RequestCancel(long expectedRunId)
    {
        var run=m_generation;
        if(run==null || expectedRunId!=m_nextRun || !ReferenceEquals(run.World,WorldGenerator.instance)
            || !ReferenceEquals(run.Zones,ZoneSystem.instance) || run.Work?.Published == true
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
            { SetProgress("Stopping",Progress.Completed,Progress.Total); run.NextPublicationMs=run.Clock.Elapsed.TotalMilliseconds+PublishEveryMs; }
            return;
        }
        Drain(run, true); // IsCompleted above proves this observes results without waiting on live workers.
        DisposePreparation(run);
        RoadNetworkGenerator.ReleaseGenerationResources();
        run.Work?.Activity?.Close();
        RoadNetworkGenerator.ResetData();
        run.CleanupDone=true;
        if(run.Request.Operator)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo("Road generation stopped; unpublished road data discarded.");
            End(run, "Cancelled", told: run.StoppedBecause);
            return;
        }
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
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo($"Generating roads on {work.Islands.Count} islands ({work.Workers} thread(s)). This can take many minutes.");
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
    /// <param name="told">Why a generation that completed published nothing (a refused preparation), for whoever asked.</param>
    private static void End(Generation run, string phase, string? failure = null, string? told = null)
    {
        DisposePreparation(run);
        if(!run.CleanupDone) { RoadNetworkGenerator.ReleaseGenerationResources(); run.Work?.Activity?.Close(); }
        SetProgress(phase, Progress.Completed, Progress.Total, failure);
        m_generation = null;
        run.Cancellation.Dispose();
        RoadNetworkGenerator.ApplyPendingRegistrations();
        if (phase == "Complete") TellIncluded();
        bool? keptOld = null;
        try { keptOld = BringBackOldNetwork(run); }
        catch (Exception error) { keptOld = false; ProceduralRoadsPlugin.ProceduralRoadsLogger.LogError($"Reading the old road network back failed: {error}"); }
        // Whatever network the run leaves (its own, the old one read back, or none), once, to the peers not holding it.
        if (!run.WorldGoing) RoadNetworkSync.Publish();
        try { run.Request.Ended?.Invoke(phase, told ?? failure, keptOld); }
        catch (Exception error) { ProceduralRoadsPlugin.ProceduralRoadsLogger.LogError($"Telling how road generation ended failed: {error}"); }
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
        run.WorldGoing=true;
        End(run, "Cancelled");
    }
#if ROADS_TESTS
    /// <summary>Tests: the frames the game would run, until the generation has ended.</summary>
    internal static void RunToEndForTests()
    {
        var clock=Stopwatch.StartNew();
        while(m_generation!=null)
        {
            if(clock.Elapsed.TotalSeconds>300) throw new TimeoutException("the road generation did not end in 300 s");
            Tick();
            Thread.Yield();
        }
        if(Progress.Phase=="Failed") throw new InvalidOperationException(Progress.Failure);
    }
#endif
}
