using System;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Development switches come from the environment, not the config file, so an
/// unset environment must be ordinary play and a typo must not quietly change
/// how the mod behaves.
/// </summary>
public class DebugSwitchTests : IDisposable
{
    private const string Variable = "PROCEDURALROADS_A_TEST_SWITCH";

    private static void Set(string? value) => Environment.SetEnvironmentVariable(Variable, value);

    public void Dispose() => Set(null);

    [Fact]
    public void AnUnsetVariableLeavesTheDefaultAlone()
    {
        Set(null);
        Assert.True(DebugSwitches.Flag("A_TEST_SWITCH", true));
        Assert.False(DebugSwitches.Flag("A_TEST_SWITCH", false));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("  On  ")]
    public void TheOnSpellingsTurnItOn(string value)
    {
        Set(value);
        Assert.True(DebugSwitches.Flag("A_TEST_SWITCH", false));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("No")]
    [InlineData("off")]
    public void TheOffSpellingsTurnItOff(string value)
    {
        Set(value);
        Assert.False(DebugSwitches.Flag("A_TEST_SWITCH", true));
    }

    [Fact]
    public void SomethingThatIsNeitherKeepsTheDefault()
    {
        // A typo must not silently flip a switch: the mod says so and carries
        // on with the behaviour it would have had.
        Set("maybe");
        Assert.True(DebugSwitches.Flag("A_TEST_SWITCH", true));
        Assert.False(DebugSwitches.Flag("A_TEST_SWITCH", false));
    }

    [Fact]
    public void AnEmptyValueIsTreatedAsUnset()
    {
        Set("   ");
        Assert.True(DebugSwitches.Flag("A_TEST_SWITCH", true));
    }
}
