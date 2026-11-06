using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The table of how deep ground may stand over a staircase (<see cref="StairCover"/>): one owner, named cases,
/// each with its value and its reason; the order is what each case is for.
/// </summary>
public class StairCoverTests
{
    /// <summary>An approach searches shallower than it refuses (so a turn filler a little deeper than the search allowed is still
    /// laid), and a road's staircase, which may be dug out, is allowed deepest.</summary>
    [Fact]
    public void AnApproachSearchesShallowerThanItRefusesAndARoadIsAllowedDeepest()
    {
        Assert.True(StairCover.ApproachSearch < StairCover.ApproachRefusal);
        Assert.True(StairCover.ApproachRefusal < StairCover.RoadStaircase);
    }
}
