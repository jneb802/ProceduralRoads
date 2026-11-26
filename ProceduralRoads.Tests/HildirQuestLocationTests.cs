using System.Reflection;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Hildir's quest locations behind the player's switch (a binary flag in the config: if false they are excluded from the POI
/// list, if true they are like boss locations, always included, and their landing settled before their roads like other caves).
/// </summary>
public class HildirQuestLocationTests
{
    private static bool Call(string name, string location) => (bool)typeof(RoadNetworkGenerator)
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { location })!;

    private static T With<T>(bool on, System.Func<T> read)
    {
        RoadNetworkOptions before = RoadNetworkGenerator.NetworkOptions;
        try
        {
            RoadNetworkGenerator.NetworkOptions = new RoadNetworkOptions { HildirQuestLocations = on };
            return read();
        }
        finally { RoadNetworkGenerator.NetworkOptions = before; }
    }

    /// <summary>Off, as shipped: no road goes to a quest location.</summary>
    [Theory]
    [InlineData("Hildir_cave")]
    [InlineData("Hildir_crypt")]
    [InlineData("Hildir_plainsfortress")]
    public void OffAsShippedNoRoadGoesToAQuestLocation(string location)
    {
        Assert.False(RoadNetworkOptions.DefaultHildirQuestLocations);
        Assert.False(new RoadNetworkOptions().HildirQuestLocations);
        Assert.False(With(false, () => Call("IsRoadLocation", location)));
        Assert.False(With(false, () => Call("IsRequiredLocation", location)));
    }

    /// <summary>On (as in the tests): a quest location is always a destination, as a boss is.</summary>
    [Theory]
    [InlineData("Hildir_cave")]
    [InlineData("Hildir_crypt")]
    [InlineData("Hildir_plainsfortress")]
    public void OnAQuestLocationIsAlwaysADestination(string location)
    {
        Assert.True(With(true, () => Call("IsRoadLocation", location)));
        Assert.True(With(true, () => Call("IsRequiredLocation", location)));
    }

    [Fact]
    public void HildirsCampIsNotAQuestLocation()
    {
        Assert.False(RoadNetworkGenerator.IsHildirQuestLocation("Hildir_camp"));
        Assert.False(With(true, () => Call("IsRoadLocation", "Hildir_camp")));
        // Unique and on the map: still the player's to find.
        Assert.True(With(true, () => PlacedLocations.PlayerDecides("Hildir_camp", true, true, false)));
    }

    [Fact]
    public void OnTheCaveIsPlacedFirstLikeTheOtherCaves()
    {
        // m_iconPlaced, as Hildir_cave is (road_locations_dump).
        Assert.True(With(false, () => PlacedLocations.PlayerDecides("Hildir_cave", false, true, false)));
        Assert.False(With(true, () => PlacedLocations.PlayerDecides("Hildir_cave", false, true, false)));
        Assert.True(With(true, () => PlacedLocations.PlayerDecides("Vendor_BlackForest", true, false, false)));
    }

    /// <summary>The hard rule: a location the game leaves for a player to find, whatever makes it so (unique,
    /// icon once placed, icon always, a trader), is never a destination. The one exception is Hildir's quest locations, with the flag
    /// on, which are placed first like the other caves.</summary>
    [Theory]
    [InlineData("Vendor_BlackForest", true, false, false, false, true)]
    [InlineData("Vendor_BlackForest", true, false, false, true, true)]
    [InlineData("SomeShrine", false, true, false, false, true)]
    [InlineData("SomeShrine", false, false, true, false, true)]
    [InlineData("Hildir_cave", false, true, false, true, false)]
    [InlineData("Hildir_cave", false, true, false, false, true)]
    [InlineData("Crypt2", false, false, false, false, false)]
    public void ALocationTheGameLeavesForAPlayerIsNeverADestination(string prefab, bool unique, bool iconPlaced, bool iconAlways, bool hildirFlag, bool leftForAPlayer) =>
        Assert.Equal(leftForAPlayer, With(hildirFlag, () => PlacedLocations.PlayerDecides(prefab, unique, iconPlaced, iconAlways)));
}
