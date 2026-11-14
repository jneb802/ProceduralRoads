using System.Reflection;
using Valheim.Testing.Doubles;
using Xunit;
namespace ProceduralRoads.Tests;
public class StairLayingTallyTests
{
    /// <summary>The pieces the search left for want of support are counted per generation, as every other staircase
    /// count: a new network starts them at none. Before, they ran on from every generation of the session.</summary>
    [Fact]
    public void TheSupportRefusalsStartAtNoneWithEachNetwork()
    {
        typeof(StairLayout).GetField("s_supportRefusals", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, 3);
        RoadNetworkGenerator.Reset();
        Assert.Equal(0, StairLayout.SupportRefusals);
    }

    /// <summary>The staircase summary says how many staircases were started further back down their road, laid again in
    /// their own kit, and refused onto a landing over a ridge to their foot. They were counted and never said.</summary>
    [Fact]
    public void TheStaircaseSummarySaysEveryCount()
    {
        using var scope = new ValheimWorldScope();
        RoadNetworkGenerator.Reset();
        (RoadStairRuns.FeetStartedLower, RoadStairRuns.KitsRelaid, RoadStairRuns.RidgesToFeet) = (7, 5, 3);
        var log = scope.CaptureLog();
        try
        {
            RoadNetworkGenerator.LogStairs();
            string line = Assert.Single(log, l => l.Contains("Staircases:"));
            Assert.Contains("7 started further back down their road", line);
            Assert.Contains("5 stretch(es) laid again in the kit their staircase stood in", line);
            Assert.Contains("3 start(s) onto a location's landing refused over a ridge or trench to the foot", line);
        }
        finally { BepInEx.Logging.ManualLogSource.Captured = null; RoadNetworkGenerator.Reset(); }
    }

    [Theory]
    [InlineData("no nearer the goal in 2000 steps of search (waypoint 7 of 7)", "no nearer the goal in # steps of search, last leg")]
    [InlineData("no nearer the goal in 2000 steps of search (waypoint 3 of 7)", "no nearer the goal in # steps of search, on the way")]
    [InlineData("no piece fits: every heading is buried, crosses itself or turns too far", "no piece fits: every heading is buried, crosses itself or turns too far, one leg")]
    [InlineData("", "no reason")]
    public void ALayoutsReasonIsTalliedWithoutItsNumbers(string reason, string kind) =>
        Assert.Equal(kind, RoadStairRuns.ReasonKind(reason));
}
