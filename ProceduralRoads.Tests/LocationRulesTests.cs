using System.Linq;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The rule values a Location changes (<see cref="LocationRules"/>): one global per rule, the exceptions beside it keyed by
/// prefab, read through <see cref="LocationRules.For"/> alone.
/// </summary>
public class LocationRulesTests
{
    /// <summary>A Location not listed, or not known, takes the globals: a staircase may serve it.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("Crypt2")]
    [InlineData("StoneTower1")]
    [InlineData("trollcave02")]
    public void ALocationNotListedTakesTheGlobals(string? prefab)
    {
        Assert.Same(LocationRules.Global, LocationRules.For(prefab));
        Assert.True(LocationRules.For(prefab).Staircase);
        Assert.Null(LocationRules.NoStaircase(prefab));
    }

    /// <summary>The troll cave and the bear cave (the same cave's prefab, the same small landing before its mouth): no staircase,
    /// and the refusal names the rule.</summary>
    [Theory]
    [InlineData("TrollCave02")]
    [InlineData("BearCave")]
    public void NoStaircaseServesATrollCaveOrABearCave(string prefab)
    {
        Assert.False(LocationRules.For(prefab).Staircase);
        Assert.Equal($"no staircase to {prefab} (rule)", LocationRules.NoStaircase(prefab));
    }

    /// <summary>The helper itself, on the road's end Locations: a staircase the road lays with an end within the reach of a troll cave or
    /// a bear cave the road ends at is no staircase to it; one beside a tower, past the reach, or one an earlier road laid, is not; a
    /// road that does not end at the cave (no end Locations) is not judged. The wiring in the planner is pinned by the tests in
    /// LocationsFirstTests (EntranceStairs) through GenerateRoad.</summary>
    [Theory]
    [InlineData("TrollCave02", 30f, true)]
    [InlineData("BearCave", 30f, true)]
    [InlineData("TrollCave02", 90f, false)]
    [InlineData("StoneTower1", 30f, false)]
    public void AStaircaseEndingAtTheCaveARoadEndsAtIsNoStaircaseToIt(string prefab, float away, bool refused)
    {
        var record = new PlacedLocationRecord { Prefab = prefab, Centre = new UnityEngine.Vector2(100f, 100f) };
        var stair = new RoadStairSpan { FromPoint = new UnityEngine.Vector2(100f + away, 100f), ToPoint = new UnityEngine.Vector2(100f + away + 50f, 100f) };
        string? why = LocationRules.NoStaircaseEndingAt(new[] { stair }, new[] { record }, out _);
        Assert.Equal(refused ? $"no staircase to {prefab} (rule)" : null, why);
        Assert.Null(LocationRules.NoStaircaseEndingAt(new[] { stair }, System.Array.Empty<PlacedLocationRecord>(), out _));
        stair.Existing = true;
        Assert.Null(LocationRules.NoStaircaseEndingAt(new[] { stair }, new[] { record }, out _));
    }

    private static string Row(LocationRules.Rules r) =>
        $"staircase={r.Staircase} frontOnly={r.FrontOnly} cutIntoRock={r.CutIntoRock} ownLandingOnly={r.OwnLandingOnly}";

    /// <summary>The approved exceptions, all of them: a new one is a line here and in <see cref="LocationRules.Exceptions"/>.</summary>
    [Fact]
    public void TheExceptionsAreTheApprovedOnes()
    {
        Assert.Equal(new[]
            {
                "BearCave: staircase=False frontOnly=False cutIntoRock=False ownLandingOnly=False",
                "Mistlands_DvergrTownEntrance1: staircase=True frontOnly=True cutIntoRock=False ownLandingOnly=False",
                "Mistlands_DvergrTownEntrance2: staircase=True frontOnly=True cutIntoRock=False ownLandingOnly=False",
                "MountainCave02: staircase=True frontOnly=False cutIntoRock=True ownLandingOnly=False",
                "TrollCave02: staircase=False frontOnly=False cutIntoRock=True ownLandingOnly=False",
            },
            LocationRules.Exceptions.OrderBy(e => e.Key, System.StringComparer.Ordinal).Select(e => $"{e.Key}: {Row(e.Value)}"));
        Assert.True(LocationRules.Staircase);
        Assert.False(LocationRules.FrontOnly);
        Assert.False(LocationRules.CutIntoRock);
        Assert.False(LocationRules.OwnLandingOnly);
    }

    /// <summary>A cave cut into rock (the old s_cutIntoRock): the troll cave and the frost cave, no other.</summary>
    [Theory]
    [InlineData("TrollCave02", true)]
    [InlineData("MountainCave02", true)]
    [InlineData("BearCave", false)]
    [InlineData("Crypt2", false)]
    [InlineData("Mistlands_DvergrTownEntrance1", false)]
    [InlineData(null, false)]
    public void ACaveCutIntoRockIsTheTrollCaveAndTheFrostCaveOnly(string? prefab, bool expected) =>
        Assert.Equal(expected, LocationRules.For(prefab).CutIntoRock);

    /// <summary>A Location entered only from its front (the old s_frontOnly): the two Dvergr town entrances, no other, not the
    /// boss's entrance.</summary>
    [Theory]
    [InlineData("Mistlands_DvergrTownEntrance1", true)]
    [InlineData("Mistlands_DvergrTownEntrance2", true)]
    [InlineData("Mistlands_DvergrBossEntrance1", false)]
    [InlineData("TrollCave02", false)]
    [InlineData(null, false)]
    public void ALocationEnteredFromItsFrontOnlyIsADvergrTownEntrance(string? prefab, bool expected) =>
        Assert.Equal(expected, LocationRules.For(prefab).FrontOnly);

    /// <summary>The boss's entrance match (the old name match): any prefab whose name holds BossEntrance, in any case, and no other; its
    /// other values are the globals.</summary>
    [Theory]
    [InlineData("Mistlands_DvergrBossEntrance1", true)]
    [InlineData("SomeBossEntrance", true)]
    [InlineData("custom_bossentrance_2", true)]
    [InlineData("Crypt4", false)]
    [InlineData("Mistlands_DvergrTownEntrance1", false)]
    [InlineData("Boss", false)]
    public void ABossEntranceIsAnyNameHoldingBossEntranceInAnyCase(string prefab, bool expected)
    {
        LocationRules.Rules rules = LocationRules.For(prefab);
        Assert.Equal(expected, rules.OwnLandingOnly);
        Assert.Equal(expected, RoadNetworkGenerator.OwnLandingOnly(prefab));
        Assert.True(rules.Staircase);
        Assert.False(rules.CutIntoRock);
    }

    /// <summary>The stake prefix a way clears (the old LocationRock.ClearedForAWay literal), read through LocationRules.</summary>
    [Theory]
    [InlineData("dvergrprops_wood_stakewall", true)]
    [InlineData("dvergrprops_wood_stake", true)]
    [InlineData("dvergrprops_wood_stakewall_short", true)]
    [InlineData("dvergrprops_wood_wall", false)]
    [InlineData("Dvergrprops_wood_stakewall", false)]
    [InlineData("a_dvergrprops_wood_stakewall", false)]
    [InlineData(null, false)]
    public void TheStakesAWayClearsAreTheDvergrStakePrefix(string? name, bool expected)
    {
        Assert.Equal(expected, LocationRules.ClearedForAWay(name));
        Assert.Equal(expected, LocationRock.ClearedForAWay(name));
        Assert.Equal("dvergrprops_wood_stake", LocationRules.ClearedPiecePrefix);
    }

    /// <summary>A prefab list or match that decides a per-Location rule lives in LocationRules and nowhere else (docs/OWNERS.md): outside
    /// RoadRules.cs no code names a prefab a rule is keyed by, nor matches a boss's entrance by its name, nor writes the stake prefix, nor
    /// declares a prefab-name set. The listed lines are other facts (a prefab's door geometry, its road priority), not rules.</summary>
    [Fact]
    public void NoPerPrefabListIsOutsideLocationRules()
    {
        var named = new System.Text.RegularExpressions.Regex(
            "\"[^\"]*(TrollCave02|MountainCave02|BearCave|Mistlands_DvergrTownEntrance|BossEntrance|dvergrprops_wood_stake)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var sets = new System.Text.RegularExpressions.Regex(@"static\s+readonly\s+(?:HashSet|IReadOnlySet)<string>");
        var allowed = new[]
        {
            ("Roads/PlacedLocationShape.cs", "new(new[] { new LocationEntrance("), // door geometry per prefab, not a rule
            ("Roads/RoadNetworkGenerator.cs", "BossPriority }"),                   // the road priorities per prefab
            ("Roads/RoadNetworkGenerator.cs", ", 80 },"),
            ("Roads/RoadNetworkGenerator.cs", ", 40 },"),
        };
        // Name sets that are not a Location's rule: registered and configured names, warned prefabs, the rock prefabs RoadRockPolicy owns.
        var allowedSets = new[] { "RegisteredLocationNames", "ConfiguredLocationNames", "s_warnedPrefabs", "NaturalBoulders", "MistlandsBoulders", "MistlandsCliffs", "s_reported" };
        string root = System.IO.Path.Combine(OwnersDocTests.RepoRoot(), "ProceduralRoads", "Src");
        var found = new System.Collections.Generic.List<string>();
        foreach (string file in System.IO.Directory.GetFiles(root, "*.cs", System.IO.SearchOption.AllDirectories))
        {
            string rel = file.Substring(root.Length).TrimStart('/', '\\').Replace('\\', '/');
            if (rel == "Roads/RoadRules.cs") continue;
            string[] lines = System.IO.File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int slashes = line.IndexOf("//", System.StringComparison.Ordinal);
                string code = slashes >= 0 && !line.Substring(0, slashes).Contains('"') ? line.Substring(0, slashes) : line;
                if (named.IsMatch(code) && !allowed.Any(a => a.Item1 == rel && code.Contains(a.Item2)))
                    found.Add($"{rel}:{i + 1}: {line.Trim()}");
                if (sets.IsMatch(code) && !allowedSets.Any(code.Contains))
                    found.Add($"{rel}:{i + 1}: {line.Trim()}");
            }
        }
        Assert.True(found.Count == 0,
            "A prefab a Location rule is keyed by, a boss's entrance name match, the stake prefix or a prefab-name set is written outside "
            + "LocationRules; a per-prefab rule is a line of LocationRules.Exceptions read through LocationRules.For (docs/OWNERS.md):\n"
            + string.Join("\n", found));
    }
}
