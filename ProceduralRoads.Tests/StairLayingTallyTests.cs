using Xunit;
namespace ProceduralRoads.Tests;
public class StairLayingTallyTests
{
    [Theory]
    [InlineData("no nearer the goal in 2000 steps of search (waypoint 7 of 7)", "no nearer the goal in # steps of search, last leg")]
    [InlineData("no nearer the goal in 2000 steps of search (waypoint 3 of 7)", "no nearer the goal in # steps of search, on the way")]
    [InlineData("no piece fits: every heading is buried, crosses itself or turns too far", "no piece fits: every heading is buried, crosses itself or turns too far, one leg")]
    [InlineData("", "no reason")]
    public void ALayoutsReasonIsTalliedWithoutItsNumbers(string reason, string kind) =>
        Assert.Equal(kind, RoadStairRuns.ReasonKind(reason));
}
