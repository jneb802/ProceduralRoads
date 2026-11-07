using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A decision constant lives in its owner (docs/OWNERS.md). Code that needs the number reads the owner; it never writes the
/// number again, because a second copy drifts. This scans the mod's code (comments and string literals removed) for the numbers
/// of a short list of key decisions and fails on a bare use outside the owner.
/// Every match must be in <see cref="Allowed"/>: the owner's own declaration, a known unrelated number (with the reason it is a
/// different fact), or a known duplicate not yet folded onto its owner. An entry that matches nothing fails too, so the list
/// shrinks as duplicates are fixed. Not checked, because the bare number is too common to tell the decision from other uses:
/// the rise of 1 m beyond lines that name a rise, and the half-width of a road (RoadWidth / 2).
/// </summary>
public class OwnerLiteralTests
{
    private enum Why { Owner, Unrelated, Known }

    private sealed record Rule(string Name, string Owner, Regex Literal, Regex? Context, Regex? Skip = null);

    private sealed record Entry(string Rule, string File, string Line, Why Why, string Reason);

    private static Regex R(string pattern) => new(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static Regex Exact(string pattern) => new(pattern, RegexOptions.Compiled);

    // A float literal: the number with an f suffix, not part of a longer number or a name. Integers are other facts
    // (an enum value, a count, a font size).
    private static Regex Float(string number) => Exact(@"(?<![\w.])" + number + @"[fF](?![\w.])");

    // As Float, for a number that is a distance on its own (assigned, compared, added or passed), not half of something or a doubling.
    private static Regex Alone(string number) => Exact(@"(?<=[=<>+\-(,]\s*)(?<![\w.])" + number + @"[fF](?![\w.])(?!\s*\*)");

    private static readonly Rule[] Rules =
    {
        new("earthwork cap 8 m", "RoadConstants.TerrainDeltaMax", Float(@"8(?:\.0+)?"),
            R(@"earthwork|\bcap\b|terrainlimit|shaped|\bcut|\bfill|batter|terraindelta")),
        new("step 0.2 m", "StairLayout.StepUp", Float(@"0?\.20*"), R(@"step|rise|walk|stair|tolerance|\bup\b|ledge")),
        new("half a rise", "StairLayout.WholeStairsOver",
            Exact(@"\bPieceRise\s*(?:\*\s*0?\.5[fF]?|/\s*2(?:\.0+)?[fF]?)(?![\w.])|(?<![\w.])0?\.5[fF]?\s*\*\s*(?:\w+\.)*PieceRise\b"), null),
        new("rise 1 m", "StairLayout.PieceRise", Float(@"1(?:\.0+)?"), Exact(@"\w*Rise\b")),
        new("run 2 m", "StairLayout.PieceRun", Alone(@"2(?:\.0+)?"), R(@"\bstair|\bstep|\bpiece"), R(@"PieceRun|StairRun")),
        new("headroom 2 m", "RoadRockPolicy.ClearanceHeight", Alone(@"2(?:\.0+)?"), R(@"clearance|headroom|boxtop|\bbox\b|highest")),
        new("clearance lift 0.1 m", "RoadRockPolicy.ClearanceLift", Float(@"0?\.10*"), R(@"clearance|lift|bottom|rock")),
        new("sea level 30", "RoadConstants.SeaLevel", Float(@"30(?:\.0+)?"), null),
        new("dry road floor 30.5", "RoadConstants.DryRoadFloor", Float(@"30\.50*"), null),
        new("deep water 28", "RoadConstants.DeepWaterHeight", Float(@"28(?:\.0+)?"), null),
        new("paint edge ratio 0.85", "RoadConstants.RoadPaintOuterRatio", Float(@"0?\.850*"), null),
    };

    private static readonly Entry[] Allowed =
    {
        // ---- The owners' own declarations ----
        new("earthwork cap 8 m", "Roads/RoadConstants.cs", "TerrainDeltaMax = 8f", Why.Owner, "the earthwork cap's source: the level-delta clamp the game applies"),
        new("earthwork cap 8 m", "Roads/RoadConstants.cs", "TerrainDeltaMin = -8f", Why.Owner, "the negative end of the same clamp"),
        new("step 0.2 m", "Roads/StairLayout.cs", "StepUp = 0.2f", Why.Owner, "a walker's step"),
        new("rise 1 m", "Roads/StairLayout.cs", "PieceRise = 1f", Why.Owner, "the rise of one stair piece"),
        new("headroom 2 m", "Roads/RoadRockPolicy.cs", "ClearanceHeight = 2f", Why.Owner, "the headroom box"),
        new("clearance lift 0.1 m", "Roads/RoadRockPolicy.cs", "ClearanceLift = 0.1f", Why.Owner, "the headroom box's lift over the walk"),
        new("sea level 30", "Roads/RoadConstants.cs", "SeaLevel = 30f", Why.Owner, "the sea level"),
        new("dry road floor 30.5", "Roads/RoadConstants.cs", "ShallowWaterHeight = 30.5f", Why.Owner, "the shallow-water line, which is also the dry road floor"),
        new("deep water 28", "Roads/RoadConstants.cs", "DeepWaterHeight = 28f", Why.Owner, "the deep-water line"),
        new("paint edge ratio 0.85", "Roads/RoadConstants.cs", "RoadPaintOuterRatio = 0.85f", Why.Owner, "the painted share of the half width"),

        // ---- Another fact that shares the number ----
        new("paint edge ratio 0.85", "Patches/ExistingWorldChoicePanel.cs", "new Color(.85f", Why.Unrelated, "a grey in the question panel"),
        new("paint edge ratio 0.85", "Patches/RoadGenerationPanel.cs", "new Color(.85f", Why.Unrelated, "a colour of the progress bar"),
        new("paint edge ratio 0.85", "Roads/BridgeLayout.cs", "BankSurvival = 0.85f", Why.Unrelated, "a chance that a bank stub survives weathering"),
        new("paint edge ratio 0.85", "Roads/BridgeLayout.cs", "PierPersistence = 0.85f", Why.Unrelated, "a chance that a pier persists"),
        new("paint edge ratio 0.85", "Roads/BridgeLayout.cs", "supportShare: 0.85f", Why.Unrelated, "a share of the bridge's pieces, not a road's paint"),
        new("sea level 30", "Patches/RoadGenerationPanel.Layout.cs", "viewportHeight / 30f", Why.Unrelated, "a font size from the screen height"),
        new("sea level 30", "Roads/BridgeLayout.cs", "Ruin.RoleDebris, 5) * 30f", Why.Unrelated, "a roll in degrees"),
        new("sea level 30", "Roads/RoadNetworkGenerator.cs", "StandOffPastRing = 30f", Why.Unrelated, "a distance from a place's ring"),
        new("sea level 30", "Roads/RoadNetworkGenerator.cs", "TrunkNear = 30f", Why.Unrelated, "a distance from the trunk network"),
        new("sea level 30", "Roads/RoadRockClearing.cs", "RetryDelay, 30f) : RetryDelay", Why.Unrelated, "the longest wait, in seconds"),
        new("sea level 30", "Roads/RoadRockClearing.cs", "RetryDelay, 30f):F1}", Why.Unrelated, "the same wait in the log line"),
        new("sea level 30", "Roads/RoadRockProbe.cs", "radius + 30f", Why.Unrelated, "how far past a rock's radius the probe looks for what it drops onto"),
        new("sea level 30", "Roads/RoadStairRuns.PlacedSites.cs", "Mathf.Abs(sweep) / 30f", Why.Unrelated, "an angle step in degrees"),
        new("sea level 30", "Roads/ServerTerrainBake.cs", "GhostLogEverySeconds = 30f", Why.Unrelated, "a log interval in seconds"),
        new("rise 1 m", "Roads/DvergrViaduct.cs", "ArchRise = 1f", Why.Unrelated, "the viaduct arch's own rise, not a stair piece's"),
        new("rise 1 m", "Roads/DvergrViaduct.cs", "deck + ArchRise, ArchTurn(a), 1f", Why.Unrelated, "the last argument is a piece scale"),
        new("rise 1 m", "Roads/RoadConstants.cs", "FordSpanDeckRise = 1f", Why.Unrelated, "how far a ford's deck stands over the bed"),
        new("rise 1 m", "Roads/RoadSiteEntry.cs", "OtherRoadRise = 1f", Why.Unrelated, "how far another road may stand above or below an approach"),
        new("rise 1 m", "Roads/StairChain.cs", "CrestRise = 1f", Why.Unrelated, "how far a crest or valley must clear both ends to be climbed and come down again"),
        new("rise 1 m", "Roads/StairChain.cs", "> 1f ||", Why.Unrelated, "a distance in metres behind a piece; the rise on the same line reads PieceRise"),
        new("run 2 m", "Roads/BridgeStructures.cs", "piece.Position) < 2f", Why.Unrelated, "how close two bridge pieces are before one is dropped"),
        new("run 2 m", "Roads/RoadCrossing.cs", "const float step = 2f", Why.Unrelated, "the sampling step along a crossing"),
        new("run 2 m", "Roads/RoadPathfinder.cs", "StairHeldFromGrade = 2f", Why.Unrelated, "a grade (rise over run), not a length"),
        new("run 2 m", "Roads/RoadStairRuns.cs", "piece.Position.y) < 2f", Why.Unrelated, "vertical closeness of two pieces, in metres of height (two lines)"),
        new("run 2 m", "Utils/StairCommands.cs", "step < 2f", Why.Unrelated, "the console command's sampling step"),
        new("headroom 2 m", "Roads/PlacedSiteReader.cs", "box.MaxY + 2f", Why.Unrelated, "how far above a rock's box the downward probe starts"),
        new("clearance lift 0.1 m", "Roads/StairRailing.cs", "- bottom) < 0.1f", Why.Unrelated, "a tolerance between a walk's surface and a rail's bottom"),

        // ---- Known duplicates: the number is written again; fold each onto its owner and delete its entry ----
        new("half a rise", "Roads/StructureEnds.cs", "off <= StairLayout.PieceRise * 0.5f", Why.Known, "an end's stairs chosen to the nearest whole stair, its own copy of the rounding; should read StairLayout.WholeStairsOver"),
        new("half a rise", "Roads/RoadNetworkGenerator.cs", "ArrivalLevel = StairLayout.PieceRise / 2f", Why.Known, "a named half rise, a road's arrival band; should read the rounding owner (StairLayout.WholeStairsOver)"),
        new("half a rise", "Roads/StairLayout.Chains.cs", "ClipStep = PieceRise / 2f", Why.Known, "a named half rise, a top clip's band; should read the rounding owner (StairLayout.WholeStairsOver)"),
        new("half a rise", "Roads/RoadStairRuns.cs", "StairLayout.PieceRise / 2f;", Why.Known, "a stair step's walking surface (its middle) restated; StairLayout.CentreSurface is the owner"),
        new("half a rise", "Roads/StairChain.cs", "StairLayout.PieceRise / 2f : p.Position.y", Why.Known, "a stair piece's surface restated; StairLayout.CentreSurface is the owner"),
        new("half a rise", "Roads/StairLayout.Tops.cs", "PieceRise / 2f : StairKit.FloorTopOf", Why.Known, "StairLayout.CentreSurface's own body; the middle of a stair step is half a rise over its base"),
        new("half a rise", "Roads/StairRailing.cs", "float middle = w.Underside + StairLayout.PieceRise / 2f;", Why.Known, "the middle of a stair step, restated (twice in this file); StairLayout.CentreSurface is the owner"),
        new("half a rise", "Roads/StairRailing.cs", "w.Underside + StairLayout.PieceRise / 2f - 0.01f", Why.Known, "the middle of a stair step, restated; StairLayout.CentreSurface is the owner"),
        new("half a rise", "Roads/StairRailing.cs", "float middle = w.Underside + StairLayout.PieceRise / 2f, surface", Why.Known, "the middle of a stair step, restated; StairLayout.CentreSurface is the owner"),
        new("run 2 m", "Roads/StairLayout.cs", "float width = 2f", Why.Known, "a stair's default width is one piece (StairLayout.Build refuses less than PieceRun); should read PieceRun"),
        new("run 2 m", "Roads/StairLayout.Chains.cs", "float width = 2f", Why.Known, "the same default width, in BuildDown; should read PieceRun"),
        new("run 2 m", "Roads/RoadSwitchbacks.cs", "StairRadius = 2f", Why.Known, "a stair turn's radius is one piece; should read PieceRun"),
    };

    private static readonly Regex NotCode = new(@"/\*.*?\*/|//[^\n]*|@""(?:[^""]|"""")*""|""(?:[^""\\\n]|\\.)*""|'(?:[^'\\\n]|\\.)*'",
        RegexOptions.Compiled | RegexOptions.Singleline);

    internal static string Code(string text) => NotCode.Replace(text, m => new string('\n', m.Value.Count(c => c == '\n')));

    private static List<(Rule rule, string file, int line, string text)> Hits()
    {
        string root = Path.Combine(OwnersDocTests.RepoRoot(), "ProceduralRoads", "Src");
        var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
        Assert.True(files.Length > 50, $"only {files.Length} source files under {root}");
        var hits = new List<(Rule, string, int, string)>();
        foreach (string file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            string rel = file.Substring(root.Length).TrimStart('/', '\\').Replace('\\', '/');
            string[] lines = Code(File.ReadAllText(file)).Split('\n');
            for (int i = 0; i < lines.Length; i++)
                foreach (Rule rule in Rules)
                    if (rule.Literal.IsMatch(lines[i]) && (rule.Context == null || rule.Context.IsMatch(lines[i]))
                        && (rule.Skip == null || !rule.Skip.IsMatch(lines[i])))
                        hits.Add((rule, rel, i + 1, lines[i].Trim()));
        }
        return hits;
    }

    [Fact]
    public void AKeyDecisionsNumberIsNotWrittenOutsideItsOwner()
    {
        var hits = Hits();
        var unlisted = hits.Where(h => !Allowed.Any(a => a.Rule == h.rule.Name && a.File == h.file && h.text.Contains(a.Line)))
            .Select(h => $"[{h.rule.Name}] {h.file}:{h.line}: {h.text}   (read {h.rule.Owner})").ToList();
        Assert.True(unlisted.Count == 0,
            "A decision's number is written again outside its owner (docs/OWNERS.md). Read the owner. If this is a different fact that\n"
            + "shares the number, add it to Allowed in OwnerLiteralTests with the reason:\n" + string.Join("\n", unlisted));
    }

    [Fact]
    public void EveryAllowedLiteralStillExists()
    {
        var hits = Hits();
        var stale = Allowed.Where(a => !hits.Any(h => a.Rule == h.rule.Name && a.File == h.file && h.text.Contains(a.Line)))
            .Select(a => $"[{a.Rule}] {a.File}: {a.Line}").ToList();
        Assert.True(stale.Count == 0, "These OwnerLiteralTests.Allowed entries match nothing now (fixed or moved: remove them):\n" + string.Join("\n", stale));
        var unknown = Allowed.Where(a => Rules.All(r => r.Name != a.Rule)).Select(a => a.Rule).Distinct().ToList();
        Assert.True(unknown.Count == 0, "Allowed names a rule that does not exist: " + string.Join(", ", unknown));
        Assert.True(Allowed.All(a => a.Reason.Length > 10), "Every Allowed entry needs its reason");
    }

    [Fact]
    public void EveryRuleNamesAnOwnerThatExists()
    {
        string doc = File.ReadAllText(Path.Combine(OwnersDocTests.RepoRoot(), "docs", "OWNERS.md"));
        foreach (Rule rule in Rules)
            Assert.True(doc.Contains("`" + rule.Owner + "`"), $"{rule.Name}: its owner {rule.Owner} is not in docs/OWNERS.md");
    }
}
