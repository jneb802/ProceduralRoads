using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The ground the game renders has one owner: <see cref="BiomeBlendedHeight"/> for the natural ground, and
/// <see cref="PlacedLocations.GroundAt(float, float, WorldGenerator)"/> (through <see cref="RoadWater.Ground"/>) for the ground
/// as locations will leave it. WorldGenerator.GetHeight is the height of the biome at the point alone, which the game does not
/// render where a zone's heightmap corners are another biome: read in production it puts cliffs and water where the world
/// has neither. The world doubles cannot catch such a read (their GetBiomeHeight returns GetHeight, so blended and raw agree
/// on them), so this scans the mod's sources instead.
/// </summary>
public class OneGroundSourceTests
{
    /// <summary>The files allowed to read the raw point-biome height, and how many reads each has. BiomeBlendedHeight is the
    /// owner; the others are dev and debug commands that show the raw height beside the rendered one on purpose, each
    /// commented "Raw on purpose". A new raw read anywhere, these files included, fails until it is listed here.</summary>
    private static readonly Dictionary<string, (int getHeight, int getBiomeHeight)> Allowed = new()
    {
        ["Utils/BiomeBlendedHeight.cs"] = (1, 9),
        ["Utils/ConsoleCommands.cs"] = (6, 0),
        ["Debug/RoadPointDebugMarker.cs"] = (1, 0),
    };

    // A call or method group named GetHeight, except Heightmap.GetHeight (the live heightmap the game renders).
    private static readonly Regex RawHeight = new(@"(?<!\bHeightmap\s*\.\s*)\bGetHeight\b", RegexOptions.Compiled);
    private static readonly Regex BiomeHeight = new(@"\bGetBiomeHeight\b", RegexOptions.Compiled);
    // Block comments, line comments, then string and char literals (verbatim strings first).
    private static readonly Regex NotCode = new(@"/\*.*?\*/|//[^\n]*|@""(?:[^""]|"""")*""|""(?:[^""\\\n]|\\.)*""|'(?:[^'\\\n]|\\.)*'",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static string SourceRoot([CallerFilePath] string here = "")
    {
        foreach (string start in new[] { Path.GetDirectoryName(here) ?? "", AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                string src = Path.Combine(dir.FullName, "ProceduralRoads", "Src");
                if (Directory.Exists(src)) return src;
            }
        throw new DirectoryNotFoundException("ProceduralRoads/Src not found from the test's source path or output folder");
    }

    private static string Code(string text) => NotCode.Replace(text, m => new string('\n', m.Value.Count(c => c == '\n')));

    [Fact]
    public void OnlyTheOwnerAndTheRawHeightCommandsReadThePointBiomeHeight()
    {
        string root = SourceRoot();
        var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
        Assert.True(files.Length > 50, $"only {files.Length} source files under {root}");
        var found = new Dictionary<string, (int getHeight, int getBiomeHeight)>();
        var lines = new List<string>();
        foreach (string file in files)
        {
            string rel = file.Substring(root.Length).TrimStart('/', '\\').Replace('\\', '/');
            string code = Code(File.ReadAllText(file));
            int raw = RawHeight.Matches(code).Count, biome = BiomeHeight.Matches(code).Count;
            if (raw == 0 && biome == 0) continue;
            found[rel] = (raw, biome);
            string[] split = code.Split('\n');
            for (int i = 0; i < split.Length; i++)
                if (RawHeight.IsMatch(split[i]) || BiomeHeight.IsMatch(split[i]))
                    lines.Add($"{rel}:{i + 1}: {split[i].Trim()}");
        }
        var unexpected = found.Where(kv => !Allowed.TryGetValue(kv.Key, out var allowed) || allowed != kv.Value)
            .Select(kv => $"{kv.Key}: GetHeight {kv.Value.getHeight}, GetBiomeHeight {kv.Value.getBiomeHeight}"
                + (Allowed.TryGetValue(kv.Key, out var a) ? $" (allowed {a.getHeight}, {a.getBiomeHeight})" : " (not allowed)"))
            .ToList();
        var missing = Allowed.Keys.Where(k => !found.ContainsKey(k)).ToList();
        Assert.True(unexpected.Count == 0 && missing.Count == 0,
            "Read the ground through BiomeBlendedHeight or PlacedLocations.GroundAt, not WorldGenerator.GetHeight / GetBiomeHeight.\n"
            + string.Join("\n", unexpected.Concat(missing.Select(m => $"{m}: listed but has no raw reads (update the list)")))
            + "\nReads found:\n" + string.Join("\n", lines));
    }

    /// <summary>The scan sees what it is meant to: a call and a method group count, Heightmap.GetHeight, comments and
    /// strings do not.</summary>
    [Fact]
    public void TheScanCountsCodeNotCommentsOrStrings()
    {
        const string sample = "float a = wg.GetHeight(x, z); Func<float,float,float> f = wg.GetHeight;\n"
            + "Heightmap.GetHeight(p, out float h); // wg.GetHeight(x, z)\n"
            + "/* GetHeight( */ string s = \"GetHeight(\"; string v = @\"GetBiomeHeight\"\"\"; float b = wg.GetBiomeHeight(bi, x, z, out _);";
        string code = Code(sample);
        Assert.Equal(2, RawHeight.Matches(code).Count);
        Assert.Single(BiomeHeight.Matches(code));
    }
}
