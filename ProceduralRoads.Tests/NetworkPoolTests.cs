using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace ProceduralRoads.Tests;

public class NetworkPoolTests
{
    private static Dictionary<string, int> Priorities =>
        (Dictionary<string, int>)typeof(RoadNetworkGenerator).GetField("LocationPriorities", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    [Fact]
    public void QueenIsAnEntranceNotACreatureName()
    {
        var bosses = (HashSet<string>)typeof(RoadNetworkGenerator).GetField("BossLocationNames", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.Contains("Mistlands_DvergrBossEntrance1", bosses);
        Assert.DoesNotContain("SeekerQueen", bosses);
        Assert.Equal(100, Priorities["Mistlands_DvergrBossEntrance1"]);
        Assert.False(Priorities.ContainsKey("SeekerQueen"));
    }

    [Theory]
    [InlineData("Hildir_cave", 80)]
    [InlineData("Hildir_crypt", 80)]
    [InlineData("Hildir_plainsfortress", 80)]
    [InlineData("BearCave", 40)]
    [InlineData("GoblinCamp2", 60)]
    [InlineData("AbandonedLogCabin02", 30)]
    public void ExpandedPoolIncludesUsefulDestinations(string name, int priority) =>
        Assert.Equal(priority, Priorities[name]);

    [Theory]
    [InlineData("Vendor_BlackForest")]
    [InlineData("Hildir_camp")]
    [InlineData("BogWitch_Camp")]
    public void ReservedTraderCandidatesAreNotAutomaticallyAllConnected(string name) =>
        Assert.False(Priorities.ContainsKey(name));
}
