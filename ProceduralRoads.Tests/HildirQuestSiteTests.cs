using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Hildir's quest sites behind the player's switch (a binary flag in the config: if false they are excluded from the POI
/// list, if true they are like boss locations, always included and pre-loaded like other caves), and their map icons kept as vanilla shows them.
/// </summary>
public class HildirQuestSiteTests
{
    private static bool Call(string name, string location) => (bool)typeof(RoadNetworkGenerator)
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { location })!;

    private static T With<T>(bool on, System.Func<T> read)
    {
        RoadNetworkOptions before = RoadNetworkGenerator.NetworkOptions;
        try
        {
            RoadNetworkGenerator.NetworkOptions = new RoadNetworkOptions { HildirQuestSites = on };
            return read();
        }
        finally { RoadNetworkGenerator.NetworkOptions = before; }
    }

    /// <summary>Off, as shipped: no road goes to a quest site.</summary>
    [Theory]
    [InlineData("Hildir_cave")]
    [InlineData("Hildir_crypt")]
    [InlineData("Hildir_plainsfortress")]
    public void OffAsShippedNoRoadGoesToAQuestSite(string site)
    {
        Assert.False(RoadNetworkOptions.DefaultHildirQuestSites);
        Assert.False(new RoadNetworkOptions().HildirQuestSites);
        Assert.False(With(false, () => Call("IsRoadLocation", site)));
        Assert.False(With(false, () => Call("IsRequiredLocation", site)));
    }

    /// <summary>On (as in the tests): a quest site is always a destination, as a boss is.</summary>
    [Theory]
    [InlineData("Hildir_cave")]
    [InlineData("Hildir_crypt")]
    [InlineData("Hildir_plainsfortress")]
    public void OnAQuestSiteIsAlwaysADestination(string site)
    {
        Assert.True(With(true, () => Call("IsRoadLocation", site)));
        Assert.True(With(true, () => Call("IsRequiredLocation", site)));
    }

    [Fact]
    public void HildirsCampIsNotAQuestSite()
    {
        Assert.False(RoadNetworkGenerator.IsHildirQuestSite("Hildir_camp"));
        Assert.False(With(true, () => Call("IsRoadLocation", "Hildir_camp")));
        // Unique and on the map: still the player's to find.
        Assert.True(With(true, () => PlacedSites.PlayerDecides("Hildir_camp", true, true, false)));
    }

    [Fact]
    public void OnTheCaveIsPlacedFirstLikeTheOtherCaves()
    {
        // m_iconPlaced, as Hildir_cave is (road_sites_dump).
        Assert.True(With(false, () => PlacedSites.PlayerDecides("Hildir_cave", false, true, false)));
        Assert.False(With(true, () => PlacedSites.PlayerDecides("Hildir_cave", false, true, false)));
        Assert.True(With(true, () => PlacedSites.PlayerDecides("Vendor_BlackForest", true, false, false)));
    }

    /// <summary>The hard rule: a place the game leaves for a player to find, whatever makes it so (unique,
    /// icon once placed, icon always, a trader), is never a destination. The one exception is Hildir's quest sites, with the flag
    /// on, which are placed first like the other caves.</summary>
    [Theory]
    [InlineData("Vendor_BlackForest", true, false, false, false, true)]
    [InlineData("Vendor_BlackForest", true, false, false, true, true)]
    [InlineData("SomeShrine", false, true, false, false, true)]
    [InlineData("SomeShrine", false, false, true, false, true)]
    [InlineData("Hildir_cave", false, true, false, true, false)]
    [InlineData("Hildir_cave", false, true, false, false, true)]
    [InlineData("Crypt2", false, false, false, false, false)]
    public void APlaceTheGameLeavesForAPlayerIsNeverADestination(string prefab, bool unique, bool iconPlaced, bool iconAlways, bool hildirFlag, bool leftForAPlayer) =>
        Assert.Equal(leftForAPlayer, With(hildirFlag, () => PlacedSites.PlayerDecides(prefab, unique, iconPlaced, iconAlways)));

    [Fact]
    public void APlaceHiddenFromTheMapIsLeftOutOfTheIconsTheServerSends()
    {
        HiddenSiteIcons.Reset();
        try
        {
            var cave = new Vector3(-2304f, 0f, 1536f);
            HiddenSiteIcons.Hide(cave);
            var icons = new Dictionary<Vector3, string> { [new Vector3(-2304f, 296.3f, 1536f)] = "Hildir_cave", [new Vector3(1664f, 0f, 768f)] = "Hildir_crypt" };
            HiddenSiteIcons.Filter(icons);
            Assert.Equal(new[] { "Hildir_crypt" }, new List<string>(icons.Values));
        }
        finally { HiddenSiteIcons.Reset(); }
    }

    [Theory]
    // TotalSimulationDistance 3, as the game's default: zones whose middles are under 3.8 zones apart, or the square in classic.
    [InlineData(3, 0, false, true)]
    [InlineData(3, 2, false, true)]
    [InlineData(3, 3, false, false)]
    [InlineData(3, 3, true, true)]
    [InlineData(4, 0, true, false)]
    [InlineData(0, 0, false, true)]
    public void AHiddenPlaceGoesOnTheMapWhereTheGameWouldHavePlacedIt(int dx, int dz, bool classic, bool shown)
    {
        HiddenSiteIcons.Reset();
        try
        {
            var site = new Vector3(-2304f, 0f, 1536f); // zone (-36, 24)
            HiddenSiteIcons.Hide(site);
            Assert.Equal(new Vector2s(-36, 24), HiddenSiteIcons.ZoneOf(site));
            var player = new Vector3((-36 + dx) * 64f + 20f, 0f, (24 + dz) * 64f - 20f);
            List<Vector3> revealed = HiddenSiteIcons.Reveal(new[] { new Vector3(5000f, 0f, 5000f), player }, 3, classic);
            Assert.Equal(shown, revealed.Count == 1);
            Assert.Equal(!shown, HiddenSiteIcons.IsHidden(site));
        }
        finally { HiddenSiteIcons.Reset(); }
    }

    /// <summary>A regeneration keeps the places placed under the loading screen hidden: nothing places them
    /// again to hide them, and their icons would go on every player's map.</summary>
    [Fact]
    public void HiddenPlacesStayHiddenThroughARegeneration()
    {
        HiddenSiteIcons.Reset();
        try
        {
            HiddenSiteIcons.Hide(new Vector3(-2304f, 296.3f, 1536f));
            RoadNetworkGenerator.Reset();
            Assert.True(HiddenSiteIcons.IsHidden(new Vector3(-2304f, 0f, 1536f)));
        }
        finally { HiddenSiteIcons.Reset(); }
    }

    [Fact]
    public void HiddenPlacesAreSavedWithTheNetwork()
    {
        HiddenSiteIcons.Reset();
        try
        {
            HiddenSiteIcons.Hide(new Vector3(-2304f, 296.3f, 1536f));
            HiddenSiteIcons.Hide(new Vector3(-3520f, 120f, -4608f));
            byte[] saved = HiddenSiteIcons.Serialize();
            HiddenSiteIcons.Reset();
            HiddenSiteIcons.Load(saved);
            Assert.Equal(2, HiddenSiteIcons.Count);
            Assert.True(HiddenSiteIcons.IsHidden(new Vector3(-3520f, 0f, -4608f)));
            HiddenSiteIcons.Load(null);
            Assert.Equal(0, HiddenSiteIcons.Count);
            Assert.Throws<InvalidDataException>(() => HiddenSiteIcons.Load(new byte[] { 9, 0, 0, 0 }));
        }
        finally { HiddenSiteIcons.Reset(); }
    }
}
