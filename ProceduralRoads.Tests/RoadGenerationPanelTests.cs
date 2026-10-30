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
    public void FontAndRowHeightDependOnlyOnTheViewport(float height)
    {
        var (font, row) = RoadGenerationPanel.Metrics(height);
        Assert.InRange(font, 12f, 20f);
        Assert.True(row > font * 3f);
        Assert.Equal((font, row), RoadGenerationPanel.Metrics(height));
    }

    [Fact]
    public void ALargerViewportNeverShrinksTheText()
    {
        Assert.True(RoadGenerationPanel.Metrics(900f).FontSize >= RoadGenerationPanel.Metrics(400f).FontSize);
    }

    private static RoadIslandProgress Row(string stage = "Connecting", int processed = 18, int destinations = 40, int reached = 15,
        double metres = 12400, int pricesCompleted = 0, int pricesTotal = 0, double seconds = 252, int unreached = 3,
        int bridges = 6, int staircases = 9, string? destination = "Crypt4", double destinationMetres = 340, double? settling = null) =>
        new(0, 3, 1, true, stage, processed, destinations, reached, metres, pricesCompleted, pricesTotal, 0, 0, seconds,
            unreached, bridges, staircases, destination, destinationMetres, settling ?? seconds);

    [Fact]
    public void RowTextSaysWhatTheIslandIsDoingWhatItHasDoneAndHowLong()
    {
        // 252 s over 18 places, 22 left: 308 s, about 5 minutes.
        Assert.Equal("Island 3 · Building roads to Crypt4 (340 m)\n<size=85%>18 of 40 places · 15 reached · 3 unreached\n12.4 km · 6 bridges · 9 staircases · 4m 12s · ~5m left</size>",
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
    public void NoEstimateUntilEnoughPlacesAreSettled(int processed)
    {
        var row = Row(processed: processed);
        Assert.Null(RoadGenerationPanel.RemainingSeconds(row));
        Assert.Contains("— left", RoadGenerationPanel.RowText(row));
    }

    [Fact]
    public void EstimateAppearsAtThreeSettledPlaces()
    {
        var left = RoadGenerationPanel.RemainingSeconds(Row(processed: 3, destinations: 13, seconds: 30));
        Assert.Equal(100, left!.Value, 6);
        Assert.Contains("~2m left", RoadGenerationPanel.RowText(Row(processed: 3, destinations: 13, seconds: 30)));
        Assert.Contains("under 1m left", RoadGenerationPanel.RowText(Row(processed: 30, destinations: 40, seconds: 60)));
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
    public void TheEstimateIgnoresTimeSpentPlanningRoutes()
    {
        // 60 s on the island, 9 s of it connecting 3 places: 3 s a place, 10 left.
        Assert.Equal(30, RoadGenerationPanel.RemainingSeconds(Row(processed: 3, destinations: 13, seconds: 69, settling: 9))!.Value, 6);
    }

    [Fact]
    public void TheBarFractionIsSettledOverPlaces()
    {
        Assert.Equal(.45f, RoadGenerationPanel.Fraction(Row()), 3);
        Assert.Equal(0f, RoadGenerationPanel.Fraction(Row(processed: 0, destinations: 0)));
    }
}
