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

    // ---- counting switches ----
    //
    // Proving a failure path in a running game needs a way to cause exactly N
    // failures. The same rules apply: unset is ordinary behaviour, and a value
    // that is not a count is reported rather than quietly becoming one.

    [Fact]
    public void AnUnsetCountLeavesTheDefaultAlone()
    {
        Set(null);
        Assert.Equal(0, DebugSwitches.Count("A_TEST_SWITCH", 0));
        Assert.Equal(7, DebugSwitches.Count("A_TEST_SWITCH", 7));
    }

    [Theory]
    [InlineData("3", 3)]
    [InlineData("  2  ", 2)]
    [InlineData("10", 10)]
    public void ACountIsTakenAsWritten(string value, int expected)
    {
        Set(value);
        Assert.Equal(expected, DebugSwitches.Count("A_TEST_SWITCH", 99));
    }

    [Fact]
    public void ZeroIsACountAndNotAFallback()
    {
        // Explicitly asking for none must not be mistaken for "unset", or a
        // switch could never be turned off again once the default moved.
        Set("0");
        Assert.Equal(0, DebugSwitches.Count("A_TEST_SWITCH", 5));
    }

    [Theory]
    [InlineData("lots")]
    [InlineData("-1")]
    [InlineData("2.5")]
    [InlineData("   ")]
    public void SomethingThatIsNotACountKeepsTheDefault(string value)
    {
        Set(value);
        Assert.Equal(4, DebugSwitches.Count("A_TEST_SWITCH", 4));
    }

    [Theory]
    [InlineData("5,-90", true, 5, -90)]
    [InlineData(" 12 , 3 ", true, 12, 3)]
    [InlineData("", false, 0, 0)]
    [InlineData("5", false, 0, 0)]
    [InlineData("a,b", false, 0, 0)]
    public void AZoneSwitchReadsExactlyOnePairOfIndices(string value, bool set, int x, int z)
    {
        Environment.SetEnvironmentVariable("PROCEDURALROADS_ZONE_TEST", value);
        try
        {
            bool got = DebugSwitches.Zone("ZONE_TEST", out int gx, out int gz);
            Assert.Equal(set, got);
            Assert.Equal(x, gx);
            Assert.Equal(z, gz);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PROCEDURALROADS_ZONE_TEST", null);
        }
    }
}
