using System.Reflection;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Which island network strategies this base of RoadNetworkGenerator offers.
/// The strategies are private, so the harness finds them by name: Chain and
/// MST on upstream master, GenerateReachableRoads once warp-71 (PR #16)
/// replaces them. Tests for a strategy the base does not have are reported
/// as skipped, never as passed, and <see cref="StrategySupportTests"/> fails
/// the suite if no known strategy is found at all.
/// </summary>
internal static class StrategySupport
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Static;

    public static readonly MethodInfo? ChainMethod =
        typeof(RoadNetworkGenerator).GetMethod("GenerateChainRoads", Private);

    public static readonly MethodInfo? MstMethod =
        typeof(RoadNetworkGenerator).GetMethod("GenerateMSTRoads", Private);

    public static readonly MethodInfo? ReachableMethod =
        typeof(RoadNetworkGenerator).GetMethod("GenerateReachableRoads", Private);

    public static bool LegacyAvailable => ChainMethod != null && MstMethod != null;
    public static bool ReachableAvailable => ReachableMethod != null;
}

/// <summary>A fact about the Chain/MST strategies; skipped on bases without them.</summary>
internal sealed class LegacyStrategyFactAttribute : FactAttribute
{
    public LegacyStrategyFactAttribute()
    {
        if (!StrategySupport.LegacyAvailable)
            Skip = "GenerateChainRoads/GenerateMSTRoads not on this base (replaced by warp-71)";
    }
}

/// <summary>A fact about GenerateReachableRoads; skipped on bases without it.</summary>
internal sealed class ReachableStrategyFactAttribute : FactAttribute
{
    public ReachableStrategyFactAttribute()
    {
        if (!StrategySupport.ReachableAvailable)
            Skip = "GenerateReachableRoads not on this base (arrives with warp-71)";
    }
}

public class StrategySupportTests
{
    [Fact]
    public void ANetworkStrategyTheHarnessKnowsExists()
    {
        // If every strategy test is skipped the suite would stay green while
        // covering nothing; a rename or removal of the strategy methods
        // must show up here.
        Assert.True(StrategySupport.LegacyAvailable || StrategySupport.ReachableAvailable,
            "RoadNetworkGenerator has neither GenerateChainRoads+GenerateMSTRoads nor GenerateReachableRoads; " +
            "update StrategySupport for the new strategy");
    }
}
