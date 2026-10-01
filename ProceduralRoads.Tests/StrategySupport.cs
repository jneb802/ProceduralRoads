using System.Reflection;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Finds the private Chain and MST entry points exercised by the topology
/// tests. The guard below fails if those entry points disappear, so skipped
/// topology tests cannot silently leave the suite without strategy coverage.
/// </summary>
internal static class StrategySupport
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Static;

    public static readonly MethodInfo? ChainMethod =
        typeof(RoadNetworkGenerator).GetMethod("GenerateChainRoads", Private);

    public static readonly MethodInfo? MstMethod =
        typeof(RoadNetworkGenerator).GetMethod("GenerateMSTRoads", Private);

    public static bool LegacyAvailable => ChainMethod != null && MstMethod != null;
}

/// <summary>A fact about the Chain/MST strategies; skipped on bases without them.</summary>
internal sealed class LegacyStrategyFactAttribute : FactAttribute
{
    public LegacyStrategyFactAttribute()
    {
        if (!StrategySupport.LegacyAvailable)
            Skip = "GenerateChainRoads/GenerateMSTRoads not on this base";
    }
}

public class StrategySupportTests
{
    [Fact]
    public void ANetworkStrategyTheHarnessKnowsExists()
    {
        Assert.True(StrategySupport.LegacyAvailable,
            "RoadNetworkGenerator no longer has both GenerateChainRoads and GenerateMSTRoads; " +
            "update the topology tests for the new strategy");
    }
}
