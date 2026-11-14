using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// docs/OWNERS.md is the one list of decisions and their owners. Every <c>Type.Member</c> in its tables must exist in the code,
/// so a rename or a delete of an owner fails the gate until the list is updated. The mod's sources are compiled into this
/// assembly, so a member is looked up by reflection; the few mod files the tests do not compile (they need the game) are
/// checked in the source text instead (only for a type the tests do not compile: a compiled type must have the member for real).
/// </summary>
public class OwnersDocTests
{
    private const BindingFlags Everything = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
        | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    // A table cell token that is exactly `Type.Member`.
    private static readonly Regex Token = new(@"`([A-Z]\w*)\.(\w+)`", RegexOptions.Compiled);
    private static readonly Regex NotCode = new(@"/\*.*?\*/|//[^\n]*|@""(?:[^""]|"""")*""|""(?:[^""\\\n]|\\.)*""|'(?:[^'\\\n]|\\.)*'",
        RegexOptions.Compiled | RegexOptions.Singleline);

    internal static string RepoRoot([CallerFilePath] string here = "")
    {
        foreach (string start in new[] { Path.GetDirectoryName(here) ?? "", AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "docs", "OWNERS.md")) && Directory.Exists(Path.Combine(dir.FullName, "ProceduralRoads", "Src")))
                    return dir.FullName;
        throw new DirectoryNotFoundException("docs/OWNERS.md not found from the test's source path or output folder");
    }

    /// <summary>Every <c>Type.Member</c> token in a table row of docs/OWNERS.md, with the line it is on.</summary>
    internal static List<(string type, string member, int line)> OwnerTokens()
    {
        string[] lines = File.ReadAllLines(Path.Combine(RepoRoot(), "docs", "OWNERS.md"));
        var found = new List<(string, string, int)>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("|")) continue;
            foreach (Match m in Token.Matches(lines[i])) found.Add((m.Groups[1].Value, m.Groups[2].Value, i + 1));
        }
        return found;
    }

    private static readonly Type[] AllTypes = LoadTypes();

    private static Type[] LoadTypes()
    {
        var types = new List<Type>();
        foreach (Type t in typeof(OwnersDocTests).Assembly.GetTypes()) types.Add(t);
        return types.ToArray();
    }

    private static bool InCompiledTypes(string type, string member) =>
        AllTypes.Where(t => t.Name == type).Any(t => t.GetMember(member, Everything).Length > 0
            || t.GetNestedTypes(Everything).Any(n => n.Name == member));

    /// <summary>For a type in a mod file the tests do not compile: the file declaring it must declare the member.</summary>
    private static bool InSource(string type, string member)
    {
        string src = Path.Combine(RepoRoot(), "ProceduralRoads", "Src");
        var declares = new Regex(@"\b(?:class|struct|enum|interface|record)\s+" + Regex.Escape(type) + @"\b");
        var memberDecl = new Regex(@"\b" + Regex.Escape(member) + @"\s*(?:\(|=[^=>]|;|\{|=>|<)");
        foreach (string file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            string code = NotCode.Replace(File.ReadAllText(file), " ");
            if (declares.IsMatch(code) && memberDecl.IsMatch(code)) return true;
        }
        return false;
    }

    [Fact]
    public void EveryOwnerInTheOwnersDocExists()
    {
        var tokens = OwnerTokens();
        Assert.True(tokens.Count > 100, $"only {tokens.Count} owners parsed from docs/OWNERS.md");
        // A partial class the tests compile only part of (LocationPrefabLevelling) declares the rest in its other files.
        var missing = tokens.Where(t => AllTypes.Any(x => x.Name == t.type) ? !InCompiledTypes(t.type, t.member) && !InSource(t.type, t.member) : !InSource(t.type, t.member))
            .Select(t => $"docs/OWNERS.md:{t.line}: {t.type}.{t.member}").Distinct().ToList();
        Assert.True(missing.Count == 0,
            "These owners in docs/OWNERS.md do not exist in the code (renamed or deleted? update the list):\n" + string.Join("\n", missing));
    }

    [Fact]
    public void EveryDecisionRowNamesAnOwner()
    {
        string[] lines = File.ReadAllLines(Path.Combine(RepoRoot(), "docs", "OWNERS.md"));
        var bare = new List<string>();
        bool inDecisions = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("## ")) inDecisions = lines[i].StartsWith("## Decisions");
            if (!inDecisions || !lines[i].StartsWith("| ") || lines[i].StartsWith("| Decision") || lines[i].StartsWith("|---")) continue;
            string[] cells = lines[i].Split('|');
            if (cells.Length < 5 || !Token.IsMatch(cells[2])) bare.Add($"docs/OWNERS.md:{i + 1}: {lines[i].Substring(0, Math.Min(70, lines[i].Length))}");
        }
        Assert.True(bare.Count == 0, "A decision row needs an owner written Type.Member in the second column:\n" + string.Join("\n", bare));
    }
}
