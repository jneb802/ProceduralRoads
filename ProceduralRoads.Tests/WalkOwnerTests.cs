using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Every height a player walks between a road (or a shared landing) and a staircase, floor, structure end or walkable block
/// is judged by one owner, StairLayout.Walkable (docs/OWNERS.md), so a bug in it is fixed in one place. This scans the mod's
/// code (comments and strings removed, as OwnerLiteralTests does) for every reader of the walker's step, StairLayout.StepUp:
/// outside the owner each one must be listed in <see cref="Allowed"/> with the reason it is not a walk between two heights (a
/// one-sided band, a margin, a rock layer). A new inline step comparison fails here: call Walkable instead. The step's old
/// aliases (StructureEnds.StepTolerance, RoadEndRing.LevelWithRoad) must not come back.
/// </summary>
public class WalkOwnerTests
{
    private sealed record Entry(string File, string Line, string Reason);

    private static readonly Regex StepRead = new(@"\bStepUp\b", RegexOptions.Compiled);
    private static readonly Regex Aliases = new(@"\b(?:StepTolerance|LevelWithRoad|Reachable)\b", RegexOptions.Compiled);

    private static readonly Entry[] Allowed =
    {
        // ---- The owner ----
        new("Roads/StairLayout.cs", "public const float StepUp = 0.2f;", "the walker's step"),
        new("Roads/StairLayout.cs", "Walkable(float a, float b) => Mathf.Abs(a - b) <= StepUp", "the one walk test"),

        // ---- Not a walk between two heights ----
        new("Roads/RockClearances.cs", "highest + StairLayout.StepUp", "the rock layer over the treads a lump on them is in, not a walk"),
        new("Roads/RoadStairRuns.cs", "CrestStubJoinReach, StairLayout.StepUp, clip", "two staircases' tops joined top to top (no road): JoinTops' height tolerance, a whole number of rises apart within it (two calls)"),
        new("Roads/RoadStairRuns.PlacedLocations.cs", "ground - surface <= StairLayout.StepUp", "a walking surface up to a step under the ground, one-sided: the step up onto it (SurfaceUnderGroundAllowed: a raised landing's walk, an onward floor's corners)"),
        new("Roads/StairEnds.cs", "EdgeIn(a, b, StairLayout.StepUp)", "the one floor-lands rule's band over the walk: a step, the band's under side Walkable's (EdgeLands)"),
        new("Roads/StairEnds.cs", "Exit(d, StairLayout.StepUp, joined", "a floor's exit by the same band: a step over the walk (Exit)"),
        new("Roads/StairEnds.cs", "StepsOff(back, o, hs[1], length, StairLayout.StepUp", "a floor's edge read by the end reader in the same band: a step over the walk"),
        new("Roads/StairEnds.cs", "Mathf.Abs(d - StairLayout.StepUp), Mathf.Abs(d + over)", "the distance to the step-off band, for the corner furthest from it (StepsOff's line)"),
        new("Roads/StairEnds.cs", "Mathf.Abs(off + StairLayout.StepUp), Mathf.Abs(off - over)", "the distance to the same band, for the nearest miss"),
        new("Roads/StairEnds.cs", "StairLayout.PieceRise : StairLayout.StepUp", "the band's other side: a rise at a stair's low edge, else a step"),
        new("Roads/StairEnds.cs", "seq[0].Walk(seq[0].Depth) > g + StairLayout.StepUp", "a foot's stairs sunk in the ground: their high edge no more than a step over it, one-sided"),
        new("Roads/StairEnds.cs", "gl - seq[0].Walk(0f) <= StairLayout.StepUp", "a sunk stair's low edge more than a step under the ground, one-sided"),
        new("Roads/StairLayout.Feet.cs", "height + StepUp + RoadPitches.ClimbToFoot(d, cap)", "a bank at a foot: ground over it by more than a step and the road's climb, one-sided"),
        new("Roads/StairLayout.Tops.cs", "<= PieceRise + StepUp", "the top pre-gate's band (a rise and a step over the ground ahead), one-sided"),
        new("Roads/StairLayout.Tops.cs", "ground(p.x, p.y) < height - StepUp", "ground under a stair's low end no more than a step under it, one-sided"),
    };

    private static List<(string file, int line, string text)> Lines(Regex pattern)
    {
        string root = Path.Combine(OwnersDocTests.RepoRoot(), "ProceduralRoads", "Src");
        var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
        Assert.True(files.Length > 50, $"only {files.Length} source files under {root}");
        var hits = new List<(string, int, string)>();
        foreach (string file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            string rel = file.Substring(root.Length).TrimStart('/', '\\').Replace('\\', '/');
            string[] lines = OwnerLiteralTests.Code(File.ReadAllText(file)).Split('\n');
            for (int i = 0; i < lines.Length; i++)
                if (pattern.IsMatch(lines[i])) hits.Add((rel, i + 1, lines[i].Trim()));
        }
        return hits;
    }

    [Fact]
    public void EveryStepAPlayerWalksIsReadByTheOneOwner()
    {
        var unlisted = Lines(StepRead).Where(h => !Allowed.Any(a => a.File == h.file && h.text.Contains(a.Line)))
            .Select(h => $"{h.file}:{h.line}: {h.text}").ToList();
        Assert.True(unlisted.Count == 0,
            "StairLayout.StepUp is read outside its owner. Where a player walks between two heights, call StairLayout.Walkable. If this\n"
            + "is another fact (a one-sided band, a margin), add it to Allowed in WalkOwnerTests with the reason:\n" + string.Join("\n", unlisted));
    }

    [Fact]
    public void EveryAllowedStepReaderStillExists()
    {
        var hits = Lines(StepRead);
        var stale = Allowed.Where(a => !hits.Any(h => a.File == h.file && h.text.Contains(a.Line))).Select(a => $"{a.File}: {a.Line}").ToList();
        Assert.True(stale.Count == 0, "These WalkOwnerTests.Allowed entries match nothing now (fixed or moved: remove them):\n" + string.Join("\n", stale));
        Assert.True(Allowed.All(a => a.Reason.Length > 10), "Every Allowed entry needs its reason");
    }

    /// <summary>A stair is sunk in one place (StairLayout.SunkStair: through SinkStep for a staircase's own sunk step and the
    /// stair StairLayout.FootToRoad sinks where a foot stands over the road it meets, and for the stair StairLayout.ClipTops
    /// sinks into a top floor a clip stands over): no other code marks a piece sunk.</summary>
    [Fact]
    public void AStairIsSunkUnderAFootInOnePlace()
    {
        var sinks = Lines(new Regex(@"\.Sunk\s*=\s*true", RegexOptions.Compiled)).ToList();
        Assert.True(sinks.Count == 1 && sinks[0].file == "Roads/StairLayout.Feet.cs",
            "A piece is marked sunk outside StairLayout.SunkStair:\n" + string.Join("\n", sinks.Select(h => $"{h.file}:{h.line}: {h.text}")));
    }

    [Fact]
    public void TheStepHasOneName()
    {
        var aliases = Lines(Aliases).Select(h => $"{h.file}:{h.line}: {h.text}").ToList();
        Assert.True(aliases.Count == 0, "An alias of the walker's step is back; read StairLayout.Walkable:\n" + string.Join("\n", aliases));
    }
}
