using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The world's raw ground has one door (docs/OWNERS.md "Ground readers"). The game's raw height readers and the blend
/// (BiomeBlendedHeight.GetBlendedHeight) are banned at compile time outside NaturalGround.cs and the blend itself
/// (ProceduralRoads/BannedSymbols.txt, RS0030 an error in every build). This pins the rest against the one allowlist,
/// docs/OWNERS.md "Raw ground readers":
/// <list type="bullet">
/// <item>no file but the ones listed there with "ban off" switches the ban off;</item>
/// <item>each NaturalGround member is called only by the files listed there, as many times as listed, each with the reason it is
/// not a road decision. A pre-road judge reads RoadNetworkGenerator.GroundBeforeRoads, a water test RoadWater.Ground, a reader of
/// the built terrain the finished ground: a new caller is a new row there, with its reason, for review.</item>
/// <item>the water lines (RoadConstants.SeaLevel, DryRoadFloor, ShallowWaterHeight, DeepWaterHeight) are banned outside
/// RoadConstants and RoadWater, which switch the ban off only around the lines' declarations (docs/OWNERS.md "Is this ground water").</item>
/// </list>
/// </summary>
public class NaturalGroundTests
{
    private sealed record Row(string File, string Reader, string Calls, string Reason, int Line);

    /// <summary>The rows of docs/OWNERS.md "Raw ground readers": File | Reader | Calls | Reason.</summary>
    private static List<Row> Allowlist()
    {
        string[] lines = File.ReadAllLines(Path.Combine(OwnersDocTests.RepoRoot(), "docs", "OWNERS.md"));
        var rows = new List<Row>();
        bool inSection = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("## ", StringComparison.Ordinal)) inSection = lines[i] == "## Raw ground readers";
            if (!inSection || !lines[i].StartsWith("| ", StringComparison.Ordinal) || lines[i].StartsWith("| File ", StringComparison.Ordinal)) continue;
            string[] cells = lines[i].Split('|').Select(c => c.Trim()).ToArray();
            Assert.True(cells.Length == 6, $"docs/OWNERS.md:{i + 1}: a raw ground reader row is File | Reader | Calls | Reason");
            rows.Add(new Row(cells[1], cells[2].Trim('`'), cells[3], cells[4], i + 1));
        }
        Assert.True(rows.Count > 20, $"only {rows.Count} rows in docs/OWNERS.md \"Raw ground readers\"");
        return rows;
    }

    private const string BanOff = "ban off";

    /// <summary>The files that may switch RS0030 off (docs/OWNERS.md "Raw ground readers", Calls "ban off"): the ground's door
    /// (and its game-only part), the blend it calls, and the biome's owner.</summary>
    private static readonly string[] Unbanned = Allowlist().Where(r => r.Calls == BanOff).Select(r => r.File).ToArray();

    /// <summary>The water lines' home (RoadConstants) and owner (RoadWater): they switch RS0030 off only around the reads of the
    /// four water-line constants (docs/OWNERS.md "Is this ground water"), each a disable/restore pair of constant declarations.</summary>
    private static readonly string[] WaterLineHomes = { "Roads/RoadWater.cs", "Roads/RoadConstants.cs" };

    /// <summary>The water-line constants banned outside their homes (BannedSymbols.txt F: entries).</summary>
    private static readonly string[] WaterLines = { "SeaLevel", "DryRoadFloor", "ShallowWaterHeight", "DeepWaterHeight" };

    private sealed record Caller(string File, string Member, int Calls, string Reason);

    /// <summary>Every call of a NaturalGround member outside it (docs/OWNERS.md "Raw ground readers", a count in Calls).</summary>
    private static readonly Caller[] Callers = Allowlist().Where(r => r.Calls != BanOff).Select(r =>
    {
        Assert.True(r.Reader.StartsWith("NaturalGround.", StringComparison.Ordinal) && int.TryParse(r.Calls, out _),
            $"docs/OWNERS.md:{r.Line}: a raw ground caller names a NaturalGround member and a whole number of calls, or \"{BanOff}\"");
        return new Caller(r.File, r.Reader.Substring("NaturalGround.".Length), int.Parse(r.Calls), r.Reason);
    }).ToArray();

    private static readonly Regex Door = new(@"\bNaturalGround\.(\w+)", RegexOptions.Compiled);
    private static readonly Regex Unban = new(@"#pragma\s+warning\s+disable\b[^\n]*\bRS0030\b|SuppressMessage[^\n]*RS0030",RegexOptions.Compiled);

    private static string Src => Path.Combine(OwnersDocTests.RepoRoot(), "ProceduralRoads", "Src");

    private static IEnumerable<(string rel, string text)> Files()
    {
        var files = Directory.GetFiles(Src, "*.cs", SearchOption.AllDirectories);
        Assert.True(files.Length > 50, $"only {files.Length} source files under {Src}");
        foreach (string file in files.OrderBy(f => f, StringComparer.Ordinal))
            yield return (file.Substring(Src.Length).TrimStart('/', '\\').Replace('\\', '/'), File.ReadAllText(file));
    }

    [Fact]
    public void OnlyTheDoorTheBlendAndTheBiomeOwnerSwitchTheRawReadersBanOff()
    {
        var files = Files().ToList();
        var unbanned = files.Where(f => !Unbanned.Contains(f.rel) && !WaterLineHomes.Contains(f.rel) && Unban.IsMatch(f.text)).Select(f => f.rel).ToList();
        Assert.True(unbanned.Count == 0, "These files switch the raw-ground ban (RS0030) off; read the ground's owner instead, or add the read "
            + "to NaturalGround with its reason (docs/OWNERS.md \"Raw ground readers\"):\n" + string.Join("\n", unbanned));
        var idle = Unbanned.Where(u => !files.Any(f => f.rel == u && Unban.IsMatch(f.text))).ToList();
        Assert.True(idle.Count == 0, "Listed with \"ban off\" in docs/OWNERS.md \"Raw ground readers\", but the file does not switch RS0030 off:\n"
            + string.Join("\n", idle));
        string project = File.ReadAllText(Path.Combine(OwnersDocTests.RepoRoot(), "ProceduralRoads", "ProceduralRoads.csproj"));
        Assert.Contains("Microsoft.CodeAnalysis.BannedApiAnalyzers", project);
        Assert.Matches(@"<WarningsAsErrors>[^<]*RS0030", project);
        string banned = File.ReadAllText(Path.Combine(OwnersDocTests.RepoRoot(), "ProceduralRoads", "BannedSymbols.txt"));
        foreach (string reader in new[] { "BiomeBlendedHeight.GetBlendedHeight(", "WorldGenerator.GetHeight(", "WorldGenerator.GetBiomeHeight(",
                     "ZoneSystem.GetGroundHeight(", "Heightmap.GetHeight(" })
            Assert.Contains(banned.Split('\n'), l => l.StartsWith("M:", StringComparison.Ordinal) && l.Contains(reader));
    }

    [Fact]
    public void TheWaterLinesAreReadOnlyThroughRoadWater()
    {
        string[] banned = File.ReadAllText(Path.Combine(OwnersDocTests.RepoRoot(), "ProceduralRoads", "BannedSymbols.txt")).Split('\n');
        foreach (string line in WaterLines)
            Assert.Contains(banned, l => l.StartsWith($"F:ProceduralRoads.RoadConstants.{line};", StringComparison.Ordinal) && l.Contains("RoadWater."));
        string names = string.Join("|", WaterLines);
        var read = new Regex($@"\bRoadConstants\.(?:{names})\b");
        var region = new Regex(@"#pragma\s+warning\s+disable\s+RS0030\b(.*?)#pragma\s+warning\s+restore\s+RS0030\b", RegexOptions.Singleline);
        var declaration = new Regex($@"^(?:public\s+|internal\s+|private\s+)?const\s+float\s+\w+\s*=\s*(?:RoadConstants\.)?(?:{names})\s*;$");
        var wrong = new List<string>();
        foreach (var (rel, text) in Files())
        {
            string code = OwnerLiteralTests.Code(text);
            if (!WaterLineHomes.Contains(rel))
            {
                if (read.IsMatch(code)) wrong.Add($"{rel}: reads a water line from RoadConstants; ask RoadWater");
                continue;
            }
            string rest = region.Replace(code, m =>
            {
                foreach (string l in m.Groups[1].Value.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
                    if (!declaration.IsMatch(l)) wrong.Add($"{rel}: inside its RS0030 switch-off, not a water line's declaration: {l}");
                return "";
            });
            if (Unban.IsMatch(rest)) wrong.Add($"{rel}: switches RS0030 off outside a disable/restore pair");
            if (rel == "Roads/RoadWater.cs" && read.IsMatch(rest)) wrong.Add($"{rel}: reads a water line from RoadConstants outside its declarations");
        }
        Assert.True(wrong.Count == 0, "The water lines are read only through RoadWater (docs/OWNERS.md \"Is this ground water\"):\n"
            + string.Join("\n", wrong));
    }

    [Fact]
    public void EachNaturalGroundMemberIsCalledOnlyByItsListedReaders()
    {
        var found = new Dictionary<(string file, string member), int>();
        foreach (var (rel, text) in Files())
        {
            if (Unbanned.Contains(rel)) continue;
            foreach (Match m in Door.Matches(OwnerLiteralTests.Code(text)))
            {
                var key = (rel, m.Groups[1].Value);
                found[key] = found.TryGetValue(key, out int n) ? n + 1 : 1;
            }
        }
        var listed = Callers.ToDictionary(c => (c.File, c.Member), c => c.Calls);
        var wrong = found.Where(f => !listed.TryGetValue(f.Key, out int n) || n != f.Value)
            .Select(f => $"{f.Key.file}: NaturalGround.{f.Key.member} x{f.Value} (listed {(listed.TryGetValue(f.Key, out int n) ? n : 0)})")
            .Concat(listed.Where(l => !found.ContainsKey(l.Key)).Select(l => $"{l.Key.File}: NaturalGround.{l.Key.Member} listed x{l.Value}, called nowhere"))
            .ToList();
        Assert.True(wrong.Count == 0, "NaturalGround's callers differ from docs/OWNERS.md \"Raw ground readers\". A road decision reads the ground's owner "
            + "(RoadNetworkGenerator.GroundBeforeRoads, RoadWater.Ground, the finished ground), never the raw ground; a new raw reader that is "
            + "not a decision is listed with its reason:\n" + string.Join("\n", wrong));
        Assert.True(Callers.All(c => c.Reason.Length > 10), "Every caller needs its reason");
        Assert.Equal(Callers.Length, Callers.Select(c => (c.File, c.Member)).Distinct().Count());
    }
}
