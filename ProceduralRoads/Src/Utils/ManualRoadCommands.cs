using System;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>Only local host/server-console commands. No client mutation RPC.</summary>
public static class ManualRoadCommands
{
    private static BepInEx.Logging.ManualLogSource Log => ProceduralRoadsPlugin.ProceduralRoadsLogger;

    public static void Register()
    {
        new Terminal.ConsoleCommand("road_connect", "Add a road to the existing network on this island: road_connect [x z | player <peer-id>] [force]. Goes round zones players changed unless forced. Host only.", a => Execute(a, "connect"), isCheat: true);
        new Terminal.ConsoleCommand("road_path", "Add a terrain-aware road through X,Z pairs in order: road_path 120,-450 180,-470 240,-420 [force]. Goes round zones players changed unless forced. Host only.", a => Execute(a, "path"), isCheat: true);
        new Terminal.ConsoleCommand("road_mark", "Waypoint draft: add [x z | player <peer-id>], list, undo, build [force], clear. Clearing removes marks, never built roads. Host only.", a => Execute(a, "mark"), isCheat: true);
    }

    private static Vector2 Position(string[] words)
    {
        if (words.Length == 0)
        {
            if (Player.m_localPlayer == null) throw new ArgumentException("No local player. Supply X Z, or player <peer-id> from the server console.");
            var p = Player.m_localPlayer.transform.position; return ManualRoadDraft.ParsePoint(p.x.ToString("R", CultureInfo.InvariantCulture), p.z.ToString("R", CultureInfo.InvariantCulture));
        }
        if (words.Length == 2 && words[0] == "player")
        {
            if (!long.TryParse(words[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
                throw new ArgumentException("Use the connected player's numeric peer ID.");
            var peer = ZNet.instance.GetPeers().FirstOrDefault(p => p.m_uid == id);
            var character = peer == null || peer.m_characterID.IsNone() ? null : ZDOMan.instance.GetZDO(peer.m_characterID);
            if (character == null) throw new ArgumentException("That peer has no connected character. Nothing marked or built.");
            var p = character.GetPosition(); return ManualRoadDraft.ParsePoint(p.x.ToString("R", CultureInfo.InvariantCulture), p.z.ToString("R", CultureInfo.InvariantCulture));
        }
        if (words.Length != 2) throw new ArgumentException("Use X Z or player <peer-id>.");
        return ManualRoadDraft.ParsePoint(words[0], words[1]);
    }

    /// <summary>The point a command is given as its first two words, "&lt;x&gt; &lt;z&gt;", read as every typed coordinate is
    /// (<see cref="ManualRoadDraft.ParsePoint"/>: invariant culture, finite, inside the world), or null where it is given
    /// fewer words (the command then acts where the player stands). False, with the reason printed after
    /// <paramref name="usage"/>, where the two words are no such point.</summary>
    internal static bool TryTypedPoint(Terminal.ConsoleEventArgs args, string usage, out Vector2? point)
    {
        point = null;
        if (args.Length < 3) return true;
        try { point = ManualRoadDraft.ParsePoint(args[1], args[2]); return true; }
        catch (ArgumentException e) { args.Context.AddString($"{usage}: {e.Message}"); return false; }
    }

    /// <summary>
    /// Runs an action a frame or two later, so the console has drawn what was printed before a long search freezes the
    /// game. Set by the plugin (a BepInEx coroutine); unset, the action runs at once.
    /// </summary>
    internal static Action<Action>? NextFrame;
    /// <summary>Frames the console gets to redraw between the start line and the search.</summary>
    internal const int FramesBeforeSearch = 2;
    private static bool s_connectPending;
    internal static bool ConnectPending => s_connectPending;
    internal const string ConnectBusy = "A road_connect search is already running. Wait for its result line.";

    /// <summary>A new world forgets a search that was waiting for its frame.</summary>
    internal static void ResetPending() => s_connectPending = false;

    private static void Defer(Action action)
    {
        if (NextFrame == null) action(); else NextFrame(action);
    }

    /// <summary>Whether the command's own work changed the road network, whatever threw after.</summary>
    private sealed class Outcome { public bool Committed; }

    private static void Execute(Terminal.ConsoleEventArgs args, string operation)
    {
        var outcome = new Outcome();
        // A road is in the network once the grid's version moves, whatever throws after that.
        int versionBefore = RoadSpatialGrid.RoadNetworkVersion;
        try
        {
            if (!RoadLifecycleManager.HoldsTheNetwork) throw new InvalidOperationException("Run this command on the host or dedicated-server console.");
            if (ZDOMan.instance == null || ZNetScene.instance == null) throw new InvalidOperationException("The world is not ready.");
            string actor = Player.m_localPlayer == null ? "console" : "host";
            var draft = ManualRoads.Draft(actor);
            string[] words = args.Args.Skip(1).ToArray();
            // force: through zones players changed, as asked, with no second run (ManualRoads.Prepare).
            bool force = ManualRoads.EndsInForce(words);
            if (force) words = words.Take(words.Length - 1).ToArray();
            if (force && operation == "mark" && (words.Length != 1 || words[0] != "build"))
                throw new ArgumentException("force goes only with road_mark build.");
            ManualRoads.Plan plan;
            bool clearDraft = false;
            if (operation == "mark")
            {
                if (words.Length == 0) throw new ArgumentException("Use road_mark add, list, undo, build or clear.");
                switch (words[0])
                {
                    case "add":
                        draft.Add(Position(words.Skip(1).ToArray()));
                        args.Context.AddString($"Mark {draft.Points.Count}: {draft.Command}"); return;
                    case "list":
                        if (words.Length != 1) throw new ArgumentException("Use road_mark list.");
                        for (int i = 0; i < draft.Points.Count; i++) args.Context.AddString($"{i + 1}: X={draft.Points[i].x:F2} Z={draft.Points[i].y:F2}");
                        args.Context.AddString(draft.Points.Count == 0 ? "No marks." : draft.Command); return;
                    case "undo":
                        if (words.Length != 1) throw new ArgumentException("Use road_mark undo.");
                        args.Context.AddString(draft.Undo() ? "Last mark removed." : "No marks."); return;
                    case "clear":
                        if (words.Length != 1) throw new ArgumentException("Use road_mark clear.");
                        draft.Clear(); args.Context.AddString("Marks cleared. Built roads are unchanged."); return;
                    case "build":
                        if (words.Length != 1) throw new ArgumentException("Use road_mark build.");
                        plan = ManualRoads.Prepare(draft.Points, false, force: force); clearDraft = true; break;
                    default: throw new ArgumentException("Use road_mark add, list, undo, build or clear.");
                }
            }
            else if (operation == "connect")
            {
                Vector2 at = Position(words);
                // Inside a location: the network is joined from the location, as the generator joins it, once asked twice.
                ManualRoads.Location? location = ManualRoads.LocationAt(at);
                if (location != null && !ManualRoads.ConfirmLocation(actor, location, DateTime.UtcNow))
                {
                    string warning = ManualRoads.InsideLocationWarning(at, location);
                    args.Context.AddString(warning);
                    Log.LogInfo($"Manual road: {warning}");
                    return;
                }
                if (s_connectPending) { args.Context.AddString(ConnectBusy); return; }
                // The start line is printed now; the search (main thread) starts once the console has drawn it.
                args.Context.AddString(ManualRoads.ConnectStarting(at));
                s_connectPending = true;
                void Search()
                {
                    var searched = new Outcome();
                    int before = RoadSpatialGrid.RoadNetworkVersion;
                    try
                    {
                        Complete(args, operation, ManualRoads.Prepare(new[] { at }, true, null, force, location), draft, false, actor, searched);
                    }
                    catch (Exception ex) { Fail(args, ex, searched, before); }
                    finally { s_connectPending = false; }
                }
                Defer(Search);
                return;
            }
            else plan = ManualRoads.Prepare(ManualRoadDraft.ParsePath(words), false, force: force);
            Complete(args, operation, plan, draft, clearDraft, actor, outcome);
        }
        catch (Exception ex) { Fail(args, ex, outcome, versionBefore); }
    }

    /// <summary>Commits a planned road and says the result; the summary is the console's last line.</summary>
    private static void Complete(Terminal.ConsoleEventArgs args, string operation, ManualRoads.Plan plan, ManualRoadDraft draft, bool clearDraft, string actor, Outcome outcome)
    {
        if (plan.AlreadyConnected)
        {
            args.Context.AddString(plan.From != null ? $"{plan.From} is already served by a road. Nothing added." : "Already connected to a road on this island. Nothing added.");
            return;
        }
        // Into zones players changed only on purpose: the same command a second time, or force.
        if (!ManualRoads.ConfirmPlayerZones(actor, operation, plan, DateTime.UtcNow))
        { args.Context.AddString(ManualRoads.PlayerZonesWarning(plan.PlayerZones, built: false)); return; }
        // Not saved here: the commit marks the network changed, and the next world save writes it whole
        // (RoadLifecycleManager.OnPrepareSave).
        ManualRoads.Commit(plan); outcome.Committed = true;
        if (clearDraft) { args.Context.AddString(draft.Command); draft.Clear(); }
        string built = operation == "connect"
            ? FormattableString.Invariant($"Road added: {plan.Length:F0} m from {(plan.From != null ? plan.From.ToString() : $"{plan.Start.x:F0}, {plan.Start.y:F0}")} to the network at ({plan.End.x:F0}, {plan.End.y:F0}){(plan.Refused.Count > 0 ? $", after {plan.Refused.Count} road(s) refused" : "")}.")
            : $"Road added: {plan.Length:F0} m.";
        Log.LogInfo($"Manual road: {built} Existing roads retained. Terrain applies as its zones and owners become ready; normal world saves preserve this addition.");
        // Warnings first, the summary last.
        if (plan.ExploredZonesWritten.Count > 0) args.Context.AddString(ManualRoads.ExploredZonesWarning(plan.ExploredZonesWritten));
        if (plan.PlayerZones.Count > 0) args.Context.AddString(ManualRoads.PlayerZonesWarning(plan.PlayerZones, built: true));
        RoadTerrainModifier.ApplyAdditionsToLoadedZones();
        StructurePlacement.ApplyPendingAppends();
        if (StructureAppendQueue.Count > 0) args.Context.AddString($"Bridge additions pending in {StructureAppendQueue.Count} zone(s).");
        args.Context.AddString(built);
    }

    /// <summary>One line to the console; only a defect carries its stack, and only in the log.</summary>
    private static void Fail(Terminal.ConsoleEventArgs args, Exception ex, Outcome outcome, int versionBefore)
    {
        outcome.Committed |= RoadSpatialGrid.RoadNetworkVersion != versionBefore;
        bool committed = outcome.Committed;
        string said = committed ? $"Road was added; application needs attention: {ex.Message}. Do not repeat the build. Save/reload or revisit the affected zones to retry." : $"Road not added: {ex.Message}";
        // A refusal is said in one line; only a defect carries its stack.
        if (!committed && ManualRoads.IsRefusal(ex)) Log.LogInfo($"Manual road: {said}");
        else Log.LogWarning($"Manual road: {ex}");
        args.Context.AddString(said);
    }
}
