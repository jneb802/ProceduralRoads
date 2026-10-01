using System;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>Only local host/server-console commands. No client mutation RPC.</summary>
public static class ManualRoadCommands
{
    public static void Register()
    {
        new Terminal.ConsoleCommand("road_connect", "Add a road to the existing network on this island: road_connect [x z | player <peer-id>]. Host only.", a => Execute(a, "connect"), isCheat: true);
        new Terminal.ConsoleCommand("road_path", "Add a terrain-aware road through X,Z pairs in order: road_path 120,-450 180,-470 240,-420. Host only.", a => Execute(a, "path"), isCheat: true);
        new Terminal.ConsoleCommand("road_mark", "Waypoint draft: add [x z | player <peer-id>], list, undo, build, clear. Clearing removes marks, never built roads. Host only.", a => Execute(a, "mark"), isCheat: true);
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

    private static void Execute(Terminal.ConsoleEventArgs args, string operation)
    {
        bool committed = false;
        try
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) throw new InvalidOperationException("Run this command on the host or dedicated-server console.");
            if (ZDOMan.instance == null || ZNetScene.instance == null) throw new InvalidOperationException("The world is not ready.");
            string actor = Player.m_localPlayer == null ? "console" : "host";
            var draft = ManualRoads.Draft(actor);
            string[] words = args.Args.Skip(1).ToArray();
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
                        plan = ManualRoads.Prepare(draft.Points, false); clearDraft = true; break;
                    default: throw new ArgumentException("Use road_mark add, list, undo, build or clear.");
                }
            }
            else plan = operation == "connect"
                ? ManualRoads.Prepare(new[] { Position(words) }, true)
                : ManualRoads.Prepare(ManualRoadDraft.ParsePath(words), false);
            if (plan.AlreadyConnected) { args.Context.AddString("Already connected to a road on this island. Nothing added."); return; }
            ManualRoads.Commit(plan); committed = true;
            RoadNetworkGenerator.SaveGlobalRoadData();
            if (clearDraft) { args.Context.AddString(draft.Command); draft.Clear(); }
            args.Context.AddString($"Road added: {plan.Length:F0} m. Existing roads retained. Terrain applies as its zones and owners become ready; normal world saves preserve this addition.");
            ManualRoadNetworkSync.Publish();
            RoadTerrainModifier.ApplyAdditionsToLoadedZones();
            BridgePlacement.ApplyPendingAppends();
            RoadNetworkGenerator.SaveGlobalRoadData();
            if (BridgeAppendQueue.Count > 0) args.Context.AddString($"Bridge additions pending in {BridgeAppendQueue.Count} zone(s).");
        }
        catch (Exception ex)
        {
            args.Context.AddString(committed ? $"Road was added; application needs attention: {ex.Message}. Do not repeat the build. Save/reload or revisit the affected zones to retry." : $"Road not added: {ex.Message}");
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogWarning($"Manual road: {ex}");
        }
    }
}
