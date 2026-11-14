using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Console commands for looking at the network's staircases in a world: road_stairs_scan (highest and lowest ground
/// round a point, the natural ground the game renders) and road_stairs_list (the staircases, tallest first). Registered only with
/// PROCEDURALROADS_DEV_COMMANDS on (<see cref="ConsoleCommands.DevCommands"/>); off by default. Read-only.
/// </summary>
public static class StairCommands
{
    public static void Register()
    {
        new Terminal.ConsoleCommand("road_stairs_list",
            "The network's staircases, tallest first: road_stairs_list [count, default 20] [kit stone|wood|blackmarble]. Each row: its foot (lowest step) and top, its kit, how far it climbs, how many pieces it has and the share of its walked pieces in each biome.",
            List, isCheat: true);
        new Terminal.ConsoleCommand("road_stairs_ends",
            "Every staircase end of the built network in one call (StairEnds): road_stairs_ends [all]. Each end not walkable (all: every end) as a line, then the counts. Ground: the terrain where its zone is loaded (live), else the finished ground the mod predicts (finished).",
            args => { foreach (string line in Ends(args.Length > 1 && args[1] == "all").Split('\n')) args.Context.AddString(line); }, isCheat: true);
        new Terminal.ConsoleCommand("road_stairs_scan",
            "Highest and lowest ground round a point, the natural ground the game renders (works where nothing is loaded): road_stairs_scan <x> <z> <radius> [step, default 16].",
            Scan, isCheat: true);
    }

    private static float Parse(string word) =>
        float.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
            ? v
            : throw new ArgumentException($"'{word}' is not a number.");

    private static void List(Terminal.ConsoleEventArgs args)
    {
        int count = args.Length > 1 && int.TryParse(args[1], out int n) ? n : 20;
        string? kit = args.Length > 2 ? args[2] : null;
        var rows = new List<(float rise, string line)>();
        foreach (StairRun run in StairStructures.Runs)
        {
            var steps = run.Pieces.FindAll(p => p.Kind == StructurePieceKind.StairStep);
            if (steps.Count == 0) continue;
            string runKit = run.KitName;
            if (kit != null && runKit != kit) continue;
            StructurePiece foot = steps[0], top = steps[0];
            foreach (StructurePiece p in steps)
            {
                if (p.Position.y < foot.Position.y) foot = p;
                if (p.Position.y > top.Position.y) top = p;
            }
            float rise = top.Position.y - foot.Position.y + StairLayout.PieceRise;
            // Which biomes its walked pieces stand in: why it got its kit.
            var biomes = new Dictionary<Heightmap.Biome, int>();
            int walked = 0;
            if (WorldGenerator.instance != null)
                foreach (StructurePiece p in run.Pieces)
                {
                    if (p.Kind == StructurePieceKind.StairSupport) continue;
                    Heightmap.Biome biome = WorldGenerator.instance.GetBiome(p.Position.x, p.Position.z);
                    biomes[biome] = biomes.TryGetValue(biome, out int seen) ? seen + 1 : 1;
                    walked++;
                }
            var shares = new List<string>();
            foreach (var kv in biomes) shares.Add($"{kv.Key} {100f * kv.Value / walked:F0}%");
            shares.Sort(StringComparer.Ordinal);
            rows.Add((rise, string.Format(CultureInfo.InvariantCulture,
                "STAIR foot {0:F1} {1:F1} {2:F1} top {3:F1} {4:F1} {5:F1} kit {6} rise {7:F0} pieces {8} biomes {9}",
                foot.Position.x, foot.Position.y, foot.Position.z, top.Position.x, top.Position.y + StairLayout.PieceRise, top.Position.z,
                runKit, rise, run.Pieces.Count, shares.Count == 0 ? "?" : string.Join(", ", shares))));
        }
        rows.Sort((a, b) => b.rise.CompareTo(a.rise));
        for (int i = 0; i < rows.Count && i < count; i++) args.Context.AddString(rows[i].line);
        args.Context.AddString($"OK: STAIRS_LIST {Math.Min(count, rows.Count)} of {rows.Count} staircase(s)");
    }

    /// <summary>
    /// Every end of the built network's staircases (StairStructures.Runs) read by the one end reader (StairEnds), as one
    /// string, for road_stairs_ends and a cli_call: a line per end not walkable (with <paramref name="all"/>, every end), then
    /// "OK: STAIR_ENDS n ok, m miss, k gap, u not verified, t twin(s) (live a, finished b)" (not verified: a foot onto a road
    /// whose slope into it is not known, as after a reload: StairEnds.OntoItsRoad), the twins the network's locations with two ways in
    /// arriving the same way (LocationWaysIn.Twins), each listed. Read on the terrain where the end's zone is loaded
    /// (ZoneSystem.GetGroundHeight, live), else on the finished ground its walls stand down to (StairEnds.GroundOf), and
    /// marked which. Read-only; no zone is loaded for it.
    /// </summary>
    public static string Ends(bool all = false)
    {
        WorldGenerator? world = WorldGenerator.instance;
        if (world == null) return "ERROR: The world is not ready.";
        var sb = new System.Text.StringBuilder();
        int ok = 0, miss = 0, gap = 0, unverified = 0, live = 0, finished = 0;
        foreach (StairRun run in StairStructures.Runs)
        {
            Func<float, float, float> predicted = StairEnds.GroundOf(run, world);
            bool anyLive = false, anyPredicted = false;
            float Terrain(float x, float z)
            {
                if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(new Vector3(x, 0f, z), out float h)) { anyLive = true; return h; }
                anyPredicted = true;
                return predicted(x, z);
            }
            // A road's surface where one is, as the finished ground reads it (StairEnds.WithRoads).
            Func<float, float, float> ground = StairEnds.WithRoads(Terrain);
            foreach (StairEnds.End end in StairEnds.Read(run, ground, StairStructures.MetAtEnds(run)))
            {
                if (end.Verdict == StairEnds.Verdict.Ok) ok++; else if (end.Verdict == StairEnds.Verdict.Miss) miss++;
                else if (end.Verdict == StairEnds.Verdict.Gap) gap++; else unverified++;
                if (all || end.Verdict != StairEnds.Verdict.Ok)
                    sb.Append(StairEnds.Line(end)).Append(anyPredicted ? (anyLive ? " [live+finished]" : " [finished]") : " [live]").Append('\n');
            }
            if (anyLive) live++; else finished++;
        }
        // Beside the ends, the network's twin ways into locations (LocationWaysIn.Twins): a line for each.
        var twins = LocationWaysIn.Twins();
        foreach (var twin in twins) sb.Append(twin.line).Append('\n');
        sb.Append(FormattableString.Invariant($"OK: STAIR_ENDS {ok} ok, {miss} miss, {gap} gap, {unverified} not verified, {twins.Count} twin(s) (staircases read live {live}, finished {finished})"));
        return sb.ToString();
    }

    private static void Scan(Terminal.ConsoleEventArgs args)
    {
        try
        {
            if (WorldGenerator.instance == null) throw new InvalidOperationException("The world is not ready.");
            if (args.Length < 4) throw new ArgumentException("Use road_stairs_scan <x> <z> <radius> [step].");
            float cx = Parse(args[1]), cz = Parse(args[2]), radius = Parse(args[3]);
            float step = args.Length > 4 ? Parse(args[4]) : 16f;
            if (step < 2f || radius / step > 200f) throw new ArgumentException("Keep the step at 2 m or more and under 200 steps across the radius.");
            var all = new List<(float h, float x, float z)>();
            for (float x = cx - radius; x <= cx + radius; x += step)
                for (float z = cz - radius; z <= cz + radius; z += step)
                    if ((x - cx) * (x - cx) + (z - cz) * (z - cz) <= radius * radius)
                        all.Add((BiomeBlendedHeight.GetBlendedHeight(x, z, WorldGenerator.instance), x, z));
            all.Sort((a, b) => b.h.CompareTo(a.h));
            args.Context.AddString($"SCAN centre=({cx:F0},{cz:F0}) radius={radius:F0} step={step:F0} points={all.Count} (rendered natural ground; no roads or edits)");
            for (int i = 0; i < Math.Min(5, all.Count); i++) args.Context.AddString($"  HIGH {all[i].h:F1} at ({all[i].x:F0},{all[i].z:F0})");
            for (int i = Math.Max(0, all.Count - 5); i < all.Count; i++) args.Context.AddString($"  LOW  {all[i].h:F1} at ({all[i].x:F0},{all[i].z:F0})");
        }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
        {
            args.Context.AddString($"ERROR: {e.Message}");
        }
    }
}
