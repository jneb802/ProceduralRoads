using System;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>One location from coordinates, one way (LocationPrefabLevelling.LocationAt): the location a manual mark stands in
/// (ManualRoads.LocationAt), the prefab the generator reads (LocationPrefabLevelling.PrefabAt) and the location preparation
/// levels (LocationAt itself) are the same location, or none on every path, at a nudged centre, across a zone's edge, and where
/// two locations stand either side of one.</summary>
[Collection("RoadStatics")]
public sealed class PoiIdentityTests : IDisposable
{
    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
    }

    private readonly ValheimWorldScope scope = new ValheimWorldScope().WithWorld(new FlatWorld()).WithZoneSystem();
    private readonly Func<System.Collections.Generic.IEnumerable<RoadLocationProtection.Footprint>?>? previousSource = RoadLocationProtection.Source;

    public PoiIdentityTests()
    {
        RoadLocationProtection.Source = null;
        PlacedLocations.Reset();
    }

    public void Dispose()
    {
        RoadLocationProtection.Reset();
        RoadLocationProtection.Source = previousSource;
        PlacedLocations.Reset();
        scope.Dispose();
    }

    private static void Place(string name, float x, float z) => TestLocations.Add(ZoneSystem.instance!, new ZoneSystem.LocationInstance
    {
        m_location = new ZoneSystem.ZoneLocation { m_prefab = new SoftReferenceableAssets.SoftReference<GameObject>(name), m_exteriorRadius = 10f },
        m_position = new Vector3(x, 40f, z),
    });

    private static string? Automatic(Vector2 at) => LocationPrefabLevelling.PrefabAt(at);

    private static string? Preparation(Vector2 at) =>
        LocationPrefabLevelling.LocationAt(at, out ZoneSystem.LocationInstance location, out _) ? location.m_location.m_prefab.Name : null;

    private static string? Manual(Vector2 at)
    {
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(at, 10f) });
        try { return ManualRoads.LocationAt(at)?.Name; }
        catch (ArgumentException) { return null; }
    }

    [Theory]
    [InlineData(100f, 100f, 0f, 0f, "Ruin")]
    [InlineData(100f, 100f, 0.4f, 0.4f, "Ruin")]
    [InlineData(100f, 100f, 0.75f, 0f, null)]
    [InlineData(96.2f, 100f, -0.3f, 0f, "Ruin")]
    public void ACoordinateNamesOneLocationOnEveryPath(float x, float z, float dx, float dz, string? expected)
    {
        Place("Ruin", x, z);
        var at = new Vector2(x + dx, z + dz);
        if (x == 96.2f) Assert.NotEqual(ZoneSystem.GetZone(new Vector3(x, 0f, z)), ZoneSystem.GetZone(new Vector3(at.x, 0f, at.y)));
        Assert.Equal(expected, Preparation(at));
        Assert.Equal(expected, Manual(at));
        Assert.Equal(expected, Automatic(at));
    }

    [Fact]
    public void TwoLocationsEitherSideOfAZoneEdgeAreNamedByNoPath()
    {
        Place("West", 95.9f, 100f);
        Place("East", 96.1f, 100f);
        var at = new Vector2(96f, 100f);
        Assert.Null(Preparation(at));
        Assert.Null(Manual(at));
        Assert.Null(Automatic(at));
    }

    /// <summary>
    /// Two locations the one resolver cannot tell apart (within half a metre of each other, either side of a zone's edge) are
    /// refused with that reason on every path that refuses a location (decision 6d): a manual mark in one is told two locations
    /// stand there, and generation gives the destination no road (PlacedLocations.FindNotPlaced) with the same reason, which
    /// road_connect and the routing log report (PlacedLocations.NotPlacedWhy). Before, the mark was told only that it stood in
    /// a protected location, and the destination was planned as a location that levels nothing.
    /// </summary>
    [Fact]
    public void TwoLocationsEitherSideOfAZoneEdgeAreRefusedWithTheReason()
    {
        Place("West", 95.9f, 100f);
        Place("East", 96.1f, 100f);
        Vector2 west = new(95.9f, 100f), east = new(96.1f, 100f);
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(west, 10f), new RoadLocationProtection.Footprint(east, 10f) });
        var manual = Assert.Throws<ArgumentException>(() => ManualRoads.LocationAt(new Vector2(95f, 104f)));
        Assert.Contains("two locations stand within half a metre", manual.Message);

        PlacedLocations.FindNotPlaced(new[] { west, east }, WorldGenerator.instance!);
        foreach (Vector2 destination in new[] { west, east })
        {
            Assert.True(PlacedLocations.NotPlaced(destination), $"the destination at {destination} was given a road");
            string? why = PlacedLocations.NotPlacedWhy(destination);
            Assert.NotNull(why);
            Assert.Contains(why!, manual.Message);
        }
    }

    /// <summary>
    /// Two locations 0.8 m apart either side of a zone's edge (each its own location to the resolver) keep their own placed-location
    /// data, carried by their identities exactly: each its own record and landing flag. Before, both rounded to one
    /// whole-metre key and shared them.
    /// </summary>
    [Fact]
    public void LocationsAMetreApartKeepTheirOwnPlacedData()
    {
        Place("West", 95.6f, 100f);
        Place("East", 96.4f, 100f);
        Vector2 west = new(95.6f, 100f), east = new(96.4f, 100f);
        Assert.Equal("West", Preparation(west));
        Assert.Equal("East", Preparation(east));
        PlacedLocations.Remember(new PlacedLocationRecord { Prefab = "West", Centre = west });
        PlacedLocations.Remember(new PlacedLocationRecord { Prefab = "East", Centre = east });
        Assert.True(PlacedLocations.TryGet(west, out PlacedLocationRecord w) && w.Prefab == "West", "the west location reads the east one's record");
        Assert.True(PlacedLocations.TryGet(east, out PlacedLocationRecord e) && e.Prefab == "East", "the east location reads the west one's record");
        Assert.Equal(2, PlacedLocations.Count);
        PlacedLocations.MarkLanded(west);
        Assert.True(PlacedLocations.Landed(west));
        Assert.False(PlacedLocations.Landed(east), "a road landed on the west location counted the east one landed");
    }
}
