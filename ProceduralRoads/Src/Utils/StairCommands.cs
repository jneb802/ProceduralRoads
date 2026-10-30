using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Console commands for looking at the network's staircases in a world: road_stairs_scan (highest and lowest ground
/// round a point, from the world generator) and road_stairs_list (the staircases, tallest first). Registered only with
/// PROCEDURALROADS_DEV_COMMANDS on (<see cref="ConsoleCommands.DevCommands"/>); off by default. Read-only.
/// </summary>
public static class StairCommands
{
    public static void Register()
    {
        new Terminal.ConsoleCommand("road_stairs_list",
            "The network's staircases, tallest first: road_stairs_list [count, default 20] [kit stone|wood|blackmarble]. Each row: its foot (lowest step) and top, its kit, how far it climbs, how many pieces it has and the share of its walked pieces in each biome.",
            List, isCheat: true);
        new Terminal.ConsoleCommand("road_stairs_scan",
            "Highest and lowest ground round a point, from the world generator (works where nothing is loaded): road_stairs_scan <x> <z> <radius> [step, default 16].",
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
                        all.Add((WorldGenerator.instance.GetHeight(x, z), x, z));
            all.Sort((a, b) => b.h.CompareTo(a.h));
            args.Context.AddString($"SCAN centre=({cx:F0},{cz:F0}) radius={radius:F0} step={step:F0} points={all.Count} (generator heights; no roads or edits)");
            for (int i = 0; i < Math.Min(5, all.Count); i++) args.Context.AddString($"  HIGH {all[i].h:F1} at ({all[i].x:F0},{all[i].z:F0})");
            for (int i = Math.Max(0, all.Count - 5); i < all.Count; i++) args.Context.AddString($"  LOW  {all[i].h:F1} at ({all[i].x:F0},{all[i].z:F0})");
        }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
        {
            args.Context.AddString($"ERROR: {e.Message}");
        }
    }
}
