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
    /// <summary>The game's placing of one zone's location (ZoneSystem.PlaceLocations as the icons patch wraps it): hidden
    /// first where placed early, then placed, then put back if the game did not place it.</summary>
    private static bool Place(Vector3 position, bool iconPlaced, bool iconAlways, bool placesIt = true)
    {
        bool hidden = HiddenSiteIcons.BeforePlacing(position, iconPlaced, iconAlways, placed: false);
        HiddenSiteIcons.AfterPlacing(position, hidden, placesIt);
        return hidden;
    }

    /// <summary>A zone the load places holds a place the game shows once placed that is not a quest site (Haldor, the
    /// forge): placed early, it stays off the map until a player comes as near as the game would have placed it from,
    /// then goes on it; saved with the network meanwhile.</summary>
    [Fact]
    public void AnyPlaceShownOncePlacedThatTheLoadPlacesStaysOffTheMapUntilAPlayerComesNear()
    {
        HiddenSiteIcons.Reset();
        try
        {
            var vendor = new Vector3(-1543.3f, 32f, -905.9f);
            var forge = new Vector3(-2168f, 120f, 6976.7f);
            using (HiddenSiteIcons.PlacingEarly())
            {
                Assert.True(Place(vendor, iconPlaced: true, iconAlways: false));
                Assert.True(Place(forge, iconPlaced: true, iconAlways: false));
            }
            Assert.False(HiddenSiteIcons.IsPlacingEarly);
            var icons = new Dictionary<Vector3, string> { [vendor] = "Vendor_BlackForest", [forge] = "AncientUpgradeStation", [new Vector3(-134.9f, 30f, 1.2f)] = "StartTemple" };
            HiddenSiteIcons.Filter(icons);
            Assert.Equal(new[] { "StartTemple" }, new List<string>(icons.Values));

            byte[] saved = HiddenSiteIcons.Serialize();
            HiddenSiteIcons.Reset();
            HiddenSiteIcons.Load(saved);
            Assert.True(HiddenSiteIcons.IsHidden(vendor));

            // A player at spawn is not near either; one beside Haldor reveals him alone.
            Assert.Empty(HiddenSiteIcons.Reveal(new[] { new Vector3(-134.9f, 30f, 1.2f) }, 3, false));
            List<Vector3> shown = HiddenSiteIcons.Reveal(new[] { vendor + new Vector3(60f, 0f, 0f) }, 3, false);
            Assert.Equal(vendor, Assert.Single(shown));
            Assert.False(HiddenSiteIcons.IsHidden(vendor));
            Assert.True(HiddenSiteIcons.IsHidden(forge));
        }
        finally { HiddenSiteIcons.Reset(); }
    }

    /// <summary>The game's own placing for a player (no early placing) shows the icon at once, as vanilla; one always
    /// shown (the start temple) is never hidden; one the game did not place after all is not left hidden.</summary>
    [Fact]
    public void TheGamesOwnPlacingAndAlwaysShownIconsAreLeftAsVanilla()
    {
        HiddenSiteIcons.Reset();
        try
        {
            var vendor = new Vector3(-1543.3f, 32f, -905.9f);
            Assert.False(Place(vendor, iconPlaced: true, iconAlways: false));
            Assert.False(HiddenSiteIcons.IsHidden(vendor));
            using (HiddenSiteIcons.PlacingEarly())
            {
                Assert.False(Place(new Vector3(-134.9f, 30f, 1.2f), iconPlaced: false, iconAlways: true));
                Assert.False(Place(new Vector3(-134.9f, 30f, 1.2f), iconPlaced: true, iconAlways: true));
                Assert.False(Place(new Vector3(300f, 30f, 300f), iconPlaced: false, iconAlways: false));
                Assert.False(HiddenSiteIcons.BeforePlacing(vendor, iconPlaced: true, iconAlways: false, placed: true));
                Assert.True(Place(vendor, iconPlaced: true, iconAlways: false, placesIt: false));
            }
            Assert.Equal(0, HiddenSiteIcons.Count);
        }
        finally { HiddenSiteIcons.Reset(); }
    }

    /// <summary>Hildir's quest sites, with roads to them on, are placed early as before and so hidden as before.</summary>
    [Fact]
    public void AQuestSitePlacedEarlyIsHiddenAsBefore()
    {
        HiddenSiteIcons.Reset();
        try
        {
            var cave = new Vector3(-2304f, 296.3f, 1536f);
            using (HiddenSiteIcons.PlacingEarly()) Assert.True(Place(cave, iconPlaced: true, iconAlways: false));
            Assert.True(HiddenSiteIcons.IsHidden(cave));
            Assert.Equal(cave, Assert.Single(HiddenSiteIcons.Reveal(new[] { cave }, 3, false)));
        }
        finally { HiddenSiteIcons.Reset(); }
    }
}
