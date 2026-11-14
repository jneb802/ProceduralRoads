using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The rule values a biome overrides, with their globals, live in one place (RoadRules), and which biome a point, a cell, a
/// location, a line or a structure is in is asked of one owner (RoadBiome) (docs/OWNERS.md; the owner's rule of 13 Nov 2026).
/// This scans the mod's code (comments and strings removed, as OwnerLiteralTests does) for a biome named
/// (<c>Heightmap.Biome.Mistlands</c> and the rest; <c>None</c> is not a biome) and for the world asked for one
/// (<c>GetBiome(</c>, <c>FindBiome(</c>). Outside the two owners each must be in <see cref="AllowedFiles"/> with the reason it is
/// not a rule, or in <see cref="Known"/>: a rule not yet moved. Known only shrinks: an entry that matches nothing fails, and
/// the list may not grow past <see cref="KnownCeiling"/>.
/// </summary>
public class BiomeOwnerTests
{
    private enum Kind { Biome, Ask }

    private sealed record Entry(Kind Kind, string File, string Line);

    private static readonly Regex BiomeNamed = new(@"\bHeightmap\.Biome\.(?!None\b)[A-Z]\w*", RegexOptions.Compiled);
    private static readonly Regex WorldAsked = new(@"\b(?:GetBiome|FindBiome)\s*\(", RegexOptions.Compiled);

    /// <summary>The two owners.</summary>
    private static readonly string[] Owners = { "Roads/RoadRules.cs", "Roads/RoadBiome.cs" };

    /// <summary>Files that name a biome or ask for one for something that is not a rule.</summary>
    private static readonly Dictionary<string, string> AllowedFiles = new()
    {
        ["Utils/BiomeBlendedHeight.cs"] = "the corner biomes the blended height reads its heights by (the game's own blend), not a rule",
        ["Utils/ConsoleCommands.cs"] = "a console command printing the biome, not a rule",
        ["Utils/StairCommands.cs"] = "a console command counting a staircase's biomes, not a rule",
        ["Utils/RailingCatalogueCommands.cs"] = "a console command laying the railing catalogue, not a rule",
        ["Roads/RoadNetworkOptions.cs"] = "the user setting ExcludedRoadBiomes' default: a setting, not a rule value",
    };

    /// <summary>The most entries <see cref="Known"/> may have: lower it as entries go, never raise it.</summary>
    private const int KnownCeiling = 0;

    /// <summary>Biome rules not yet moved to RoadRules and RoadBiome. Each goes as its rule moves.</summary>
    private static readonly Entry[] Known =
    {
    };

    private static List<(Kind kind, string file, int line, string text)> Hits()
    {
        string root = Path.Combine(OwnersDocTests.RepoRoot(), "ProceduralRoads", "Src");
        var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
        Assert.True(files.Length > 50, $"only {files.Length} source files under {root}");
        var hits = new List<(Kind, string, int, string)>();
        foreach (string file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            string rel = file.Substring(root.Length).TrimStart('/', '\\').Replace('\\', '/');
            if (Owners.Contains(rel) || AllowedFiles.ContainsKey(rel)) continue;
            string[] lines = OwnerLiteralTests.Code(File.ReadAllText(file)).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (BiomeNamed.IsMatch(lines[i])) hits.Add((Kind.Biome, rel, i + 1, lines[i].Trim()));
                if (WorldAsked.IsMatch(lines[i])) hits.Add((Kind.Ask, rel, i + 1, lines[i].Trim()));
            }
        }
        return hits;
    }

    [Fact]
    public void NoBiomeIsNamedOrAskedForOutsideItsOwners()
    {
        var unlisted = Hits().Where(h => !Known.Any(k => k.Kind == h.kind && k.File == h.file && h.text.Contains(k.Line)))
            .Select(h => $"[{h.kind}] {h.file}:{h.line}: {h.text}").ToList();
        Assert.True(unlisted.Count == 0,
            "A biome is named or asked for outside RoadRules and RoadBiome (docs/OWNERS.md). A biome's value is a line of RoadRules'\n"
            + "overrides, read through RoadRules.For; which biome a place is in is asked of RoadBiome:\n" + string.Join("\n", unlisted));
    }

    [Fact]
    public void TheKnownListOnlyShrinks()
    {
        var hits = Hits();
        var stale = Known.Where(k => !hits.Any(h => k.Kind == h.kind && k.File == h.file && h.text.Contains(k.Line)))
            .Select(k => $"[{k.Kind}] {k.File}: {k.Line}").ToList();
        Assert.True(stale.Count == 0, "These BiomeOwnerTests.Known entries match nothing now (moved: remove them, and lower KnownCeiling):\n" + string.Join("\n", stale));
        Assert.True(Known.Length <= KnownCeiling, $"BiomeOwnerTests.Known has {Known.Length} entries, more than its ceiling {KnownCeiling}: it only shrinks");
        foreach (string owner in Owners.Where(o => File.Exists(Path.Combine(OwnersDocTests.RepoRoot(), "ProceduralRoads", "Src", o))))
            Assert.DoesNotContain(owner, AllowedFiles.Keys);
        Assert.True(AllowedFiles.Values.All(r => r.Length > 10), "Every allowed file needs its reason");
    }
}
