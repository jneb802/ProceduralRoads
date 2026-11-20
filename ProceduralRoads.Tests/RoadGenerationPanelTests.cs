using System.Linq;
using Xunit;

namespace ProceduralRoads.Tests;

public sealed class RoadGenerationPanelTests
{
    [Fact]
    public void IslandsAlternateBetweenTheTwoColumns()
    {
        var cells = Enumerable.Range(0, 7).Select(RoadGenerationPanel.Cell).ToArray();
        Assert.Equal(new[] { (0, 0), (1, 0), (0, 1), (1, 1), (0, 2), (1, 2), (0, 3) }, cells);
    }

    [Fact]
    public void OneIslandIsTheLeftColumnOfTheSameTwoColumnLayout()
    {
        Assert.Equal(2, RoadGenerationPanel.Columns);
        Assert.Equal((0, 0), RoadGenerationPanel.Cell(0));
    }

    [Theory]
    [InlineData(300f)]
    [InlineData(546f)]
    [InlineData(900f)]
    [InlineData(5000f)]
    public void FewIslandsKeepTheNaturalSizeThatDependsOnlyOnTheViewport(float height)
    {
        var (font, row, shown, hidden) = RoadGenerationPanel.Metrics(height, 2);
        Assert.InRange(font, 12f, 20f);
        Assert.True(row > font * 3f);
        Assert.Equal((font, row, 2, 0), RoadGenerationPanel.Metrics(height, 2));
        Assert.Equal(0, hidden);
        Assert.Equal(2, shown);
    }

    [Fact]
    public void ALargerViewportNeverShrinksTheText()
    {
        for (int islands = 1; islands <= 40; islands++)
            Assert.True(RoadGenerationPanel.Metrics(900f, islands).FontSize >= RoadGenerationPanel.Metrics(400f, islands).FontSize);
    }

    [Fact]
    public void FifteenIslandsOnAFullHdPanelFitByShrinkingTheRows()
    {
        // In game: viewport ~546 px high, eight rows of 79 px did not fit and the last one ran under the Cancel button.
        var (font, row, shown, hidden) = RoadGenerationPanel.Metrics(546f, 15);
        Assert.Equal(15, shown);
        Assert.Equal(0, hidden);
        Assert.True(8 * row <= 546f + .01f);
        Assert.True(font < RoadGenerationPanel.Metrics(546f, 2).FontSize && font >= RoadGenerationPanel.MinFontSize);
    }

    [Theory]
    [InlineData(200f)]
    [InlineData(300f)]
    [InlineData(546f)]
    [InlineData(900f)]
    [InlineData(1400f)]
    public void EveryIslandCountFitsTheViewportOrIsSummarisedInTheLastCell(float height)
    {
        for (int islands = 1; islands <= 64; islands++)
        {
            var (font, row, shown, hidden) = RoadGenerationPanel.Metrics(height, islands);
            int lines = (shown + RoadGenerationPanel.Columns - 1) / RoadGenerationPanel.Columns;
            Assert.True(lines * row <= height + .01f, $"{islands} islands at {height}: {lines} lines of {row}");
            Assert.True(font >= RoadGenerationPanel.MinFontSize);
            // Nothing is lost: every island is a cell, or counted by the summary cell.
            Assert.Equal(islands, hidden == 0 ? shown : shown - 1 + hidden);
            if (hidden > 0) Assert.True(hidden >= 2, "a summary cell must replace at least two islands");
        }
    }

    [Fact]
    public void ARoomyViewportShowsEveryIslandWithNoSummary()
    {
        Assert.Equal(0, RoadGenerationPanel.Metrics(1400f, 30).Hidden);
        Assert.True(RoadGenerationPanel.Metrics(200f, 30).Hidden > 0);
    }

    private static RoadIslandProgress Row(string stage = "Connecting", int processed = 18, int destinations = 40, int reached = 15,
        double metres = 12400, int pricesCompleted = 0, int pricesTotal = 0, double seconds = 252, int unreached = 3,
        int bridges = 6, int staircases = 9, string? destination = "Crypt4", double destinationMetres = 340) =>
        new(0, 3, 1, true, stage, processed, destinations, reached, metres, pricesCompleted, pricesTotal, 0, 0, seconds,
            unreached, bridges, staircases, destination, destinationMetres);

    [Fact]
    public void RowTextSaysWhatTheIslandIsDoingAndWhatItHasDoneAndHowLongItHasRun()
    {
        Assert.Equal("Island 3 · Building roads to Crypt4 (340 m)\n<size=85%>18 of 40 locations · 15 reached · 3 unreached\n12.4 km · 6 bridges · 9 staircases · 4m 12s</size>",
            RoadGenerationPanel.RowText(Row()));
    }

    [Fact]
    public void RowTextHasNoStepsSearchingOrStageSeconds()
    {
        string text = RoadGenerationPanel.RowText(Row());
        Assert.DoesNotContain("steps", text);
        Assert.DoesNotContain("searching", text);
        Assert.DoesNotContain("processed", text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(30)]
    public void NoTimeLeftIsShownAtAnyProgress(int processed)
    {
        string text = RoadGenerationPanel.RowText(Row(processed: processed, destinations: 40, seconds: 90));
        Assert.DoesNotContain("left", text);
        Assert.DoesNotContain("~", text);
        Assert.EndsWith("1m 30s</size>", text);
    }

    [Fact]
    public void StagesAreRenamedForTheDisplayOnly()
    {
        Assert.Contains("Planning routes 3/12", RoadGenerationPanel.RowText(Row("Pricing", pricesCompleted: 3, pricesTotal: 12)));
        Assert.DoesNotContain("Pricing", RoadGenerationPanel.RowText(Row("Pricing", pricesCompleted: 3, pricesTotal: 12)));
        Assert.DoesNotContain("Crypt4", RoadGenerationPanel.RowText(Row("Pricing", pricesCompleted: 3, pricesTotal: 12)));
        Assert.Contains("Building roads", RoadGenerationPanel.RowText(Row("Connecting")));
        Assert.Contains("Fitting terrain", RoadGenerationPanel.RowText(Row("Fitting")));
        Assert.DoesNotContain("Crypt4", RoadGenerationPanel.RowText(Row("Fitting")));
    }

    [Fact]
    public void BuiltCountsAreSingularOrOmittedAndNoDestinationIsNotShown()
    {
        string text = RoadGenerationPanel.RowText(Row(bridges: 1, staircases: 0, destination: null));
        Assert.Contains("12.4 km · 1 bridge · 4m 12s", text);
        Assert.DoesNotContain("staircase", text);
        Assert.StartsWith("Island 3 · Building roads\n", text);
    }

    [Theory]
    [InlineData("Crypt4", "Crypt4")]
    [InlineData("Mistlands_DvergrBossEntrance1", "Dvergr Boss Entrance1")]
    [InlineData("StoneTowerRuins03", "Stone Tower Ruins03")]
    [InlineData("Hildir_cave", "Hildir cave")]
    [InlineData("Mistlands_GuardTower3_ruined_new", "Guard Tower3 ruined new")]
    public void FriendlyNamesDropTheBiomePrefixAndSplitWords(string prefab, string expected) =>
        Assert.Equal(expected, RoadGenerationPanel.FriendlyName(prefab));

    [Fact]
    public void TheBarFractionIsSettledOverLocations()
    {
        Assert.Equal(.45f, RoadGenerationPanel.Fraction(Row()), 3);
        Assert.Equal(0f, RoadGenerationPanel.Fraction(Row(processed: 0, destinations: 0)));
    }

    [Fact]
    public void ReplacementRoundsLeaveTheTotalsAndTheBarConsistentAndNeverBackwards()
    {
        // Production path: the island's activity as RouteIsland drives it. Four chosen destinations settle (two end unreached),
        // then ReplaceUnreachedRounds appends picks to the same list and the planner measures after each one, as the rounds do.
        var owner = new RoadIslandActivity(1, 1);
        var token = owner.Claim(1, 4);
        var locations = Enumerable.Range(0, 4).Select(i => ($"L{i}", new UnityEngine.Vector3(100f * i, 0f, 0f), 20f)).ToList();
        var reachedNames = new System.Collections.Generic.HashSet<string> { "L0", "L1" };
        var seen = new System.Collections.Generic.List<RoadIslandProgress>();
        void Observe() => seen.Add(owner.Snapshot()[0]);
        void Measure() { token.Measured(locations.Select(d => reachedNames.Contains(d.Item1)).ToArray()); Observe(); }
        for (int i = 0; i < 4; i++) { token.Settled(i, reachedNames.Contains(locations[i].Item1)); Measure(); }
        int picked = 0;
        RoadNetworkGenerator.ReplaceUnreachedRounds(locations, d => reachedNames.Contains(d.name),
            (kept, tried, count) => Enumerable.Range(0, count).Select(_ => ($"R{picked++}", new UnityEngine.Vector3(5000f + 100f * picked, 0f, 0f), 20f)).ToList(),
            (picks, kept, number) =>
            {
                Measure();
                if (number == 1) reachedNames.Add(picks[0].name);   // one replacement reached, one not: a second round follows
                Measure();
            });
        Measure();
        Assert.True(locations.Count > 4, "the rounds must have added picks");
        Assert.All(seen, row => Assert.Equal(4, row.Destinations));
        Assert.All(seen, row => Assert.InRange(row.Reached + row.Unreached, 0, row.Destinations));
        Assert.All(seen, row => Assert.True(row.Processed <= row.Destinations));
        for (int i = 1; i < seen.Count; i++)
            Assert.True(RoadGenerationPanel.Fraction(seen[i]) >= RoadGenerationPanel.Fraction(seen[i - 1]), $"the bar went back at observation {i}");
        var last = seen[seen.Count - 1];
        Assert.Equal(1f, RoadGenerationPanel.Fraction(last));
        Assert.Contains("4 of 4 locations · 2 reached · 2 unreached", RoadGenerationPanel.RowText(last));
    }
}
