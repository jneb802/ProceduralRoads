using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// When the mod decides whether to load a saved road network or build a new
/// one.
///
/// Getting this wrong is expensive and silent: a world that already had roads
/// generates a second network over the top of the one it saved, and the player
/// never learns that the first is gone. It happened, and it happened because
/// the decision was taken too early -- loading a world sets ZoneSystem's
/// LocationsGenerated from the save, and that setter raises
/// GenerateLocationsCompleted while ZDOMan.LoadChunks, the next line of
/// ZNet.LoadWorld, has not run. Nothing was in memory to find.
///
/// So the decision waits for both halves -- locations ready, and the world's
/// data read -- and is taken by whichever arrives last. These tests hold that
/// in both arrival orders, which is not academic: a saved world raises the
/// locations event during its own load, a fresh one long afterwards.
/// </summary>
public class LoadDecisionTests : System.IDisposable
{
    private static void SetWorldDataLoaded(bool value) =>
        typeof(RoadLifecycleManager)
            .GetField("m_worldDataLoaded", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, value);

    private static ZoneSystem WorldWithPlaces()
    {
        ZoneSystem zones = new();
        (string name, float x, float z)[] places =
        {
            ("StartTemple", 0f, 0f),
            ("Eikthyrnir", -220f, -120f),
            ("GDKing", 260f, 140f),
            ("Crypt4", -120f, 240f),
        };
        foreach ((string name, float x, float z) place in places)
        {
            zones.Locations.Add(new ZoneSystem.LocationInstance
            {
                m_position = new Vector3(place.x, 0f, place.z),
                m_location = new ZoneSystem.ZoneLocation
                {
                    m_prefab = new ZoneSystem.ZoneLocation.PrefabEntry { Name = place.name },
                    m_exteriorRadius = 16f,
                },
            });
        }
        return zones;
    }

    /// <summary>A world as the game hands one over: the generator reset, the
    /// mod subscribed, nothing decided yet.</summary>
    private static ZoneSystem SetUp(bool savedNetworkExists)
    {
        WorldGenerator.instance = new SyntheticWorld { HasRiver = false, HasMountain = false };
        RoadNetworkPersistence.ResetForTest();
        RoadNetworkPersistence.SavedNetworkExists = savedNetworkExists;
        SetWorldDataLoaded(false);

        ZoneSystem zones = WorldWithPlaces();
        ZoneSystem.instance = zones;
        RoadLifecycleManager.OnZoneSystemStart(zones);
        return zones;
    }

    /// <summary>
    /// The world's data finishing loading: its ZDOs become searchable, and
    /// then ZNet's world load returns. In that order, as in the game.
    /// </summary>
    private static void WorldDataArrives()
    {
        RoadNetworkPersistence.SavedNetworkVisible = true;
        RoadLifecycleManager.OnWorldDataLoaded();
    }

    public void Dispose()
    {
        SetWorldDataLoaded(false);
        RoadNetworkPersistence.ResetForTest();
        RoadNetworkGenerator.Reset();
        WorldGenerator.instance = null;
        ZoneSystem.instance = null;
    }

    [Fact]
    public void TheLocationsEventAloneDecidesNothing()
    {
        // The moment the bug happened in: the event fires during the world's
        // own load, before its data is in memory. Deciding here is what buried
        // the saved network.
        ZoneSystem zones = SetUp(savedNetworkExists: true);

        zones.LocationsGenerated = true;

        Assert.False(RoadNetworkGenerator.RoadsGenerated,
            "a network was generated before the world's data had been read");
        Assert.False(RoadNetworkGenerator.RoadsAvailable);
        Assert.Equal(0, RoadNetworkPersistence.LoadAttempts);
    }

    [Fact]
    public void TheSavedNetworkIsLoadedOnceTheWorldsDataArrives()
    {
        ZoneSystem zones = SetUp(savedNetworkExists: true);

        zones.LocationsGenerated = true;
        WorldDataArrives();

        Assert.True(RoadNetworkGenerator.RoadsLoadedFromZDO, "the saved network was not loaded");
        Assert.False(RoadNetworkGenerator.RoadsGenerated, "a network was generated over the saved one");
        Assert.Equal(1, RoadNetworkPersistence.LoadAttempts);
    }

    [Fact]
    public void TheWorldsDataArrivingFirstAlsoWorks()
    {
        // A freshly created world generates its locations long after the world
        // data is read, so the signals arrive the other way round.
        ZoneSystem zones = SetUp(savedNetworkExists: true);

        WorldDataArrives();
        Assert.False(RoadNetworkGenerator.RoadsAvailable, "decided before the locations were ready");

        zones.LocationsGenerated = true;

        Assert.True(RoadNetworkGenerator.RoadsLoadedFromZDO);
        Assert.Equal(1, RoadNetworkPersistence.LoadAttempts);
    }

    [Fact]
    public void AWorldWithNoSavedNetworkGeneratesOne()
    {
        // The other half: waiting must not mean never building.
        ZoneSystem zones = SetUp(savedNetworkExists: false);

        zones.LocationsGenerated = true;
        WorldDataArrives();

        Assert.True(RoadNetworkGenerator.RoadsGenerated, "a world with no saved roads did not get any");
        Assert.False(RoadNetworkGenerator.RoadsLoadedFromZDO);
        Assert.Equal(1, RoadNetworkPersistence.LoadAttempts);
    }

    [Fact]
    public void PlayerSpawnIsTheBackstopWhenTheWorldLoadHookDoesNot()
    {
        // Not every path goes through ZNet's server world load. A player
        // cannot spawn into a world whose data has not been read, so spawning
        // is the last honest moment to decide.
        ZoneSystem zones = SetUp(savedNetworkExists: true);

        zones.LocationsGenerated = true;
        Assert.False(RoadNetworkGenerator.RoadsAvailable);

        RoadNetworkPersistence.SavedNetworkVisible = true;
        RoadLifecycleManager.OnPlayerSpawn(Vector3.zero);

        Assert.True(RoadNetworkGenerator.RoadsLoadedFromZDO);
    }

    [Fact]
    public void TheDecisionIsTakenOnlyOnce()
    {
        ZoneSystem zones = SetUp(savedNetworkExists: true);

        zones.LocationsGenerated = true;
        WorldDataArrives();
        WorldDataArrives();
        RoadNetworkPersistence.SavedNetworkVisible = true;
        RoadLifecycleManager.OnPlayerSpawn(Vector3.zero);

        Assert.Equal(1, RoadNetworkPersistence.LoadAttempts);
        Assert.True(RoadNetworkGenerator.RoadsLoadedFromZDO);
        Assert.False(RoadNetworkGenerator.RoadsGenerated);
    }

    [Fact]
    public void UnloadingTheWorldForgetsThatItsDataWasRead()
    {
        // Otherwise the next world inherits the flag and decides on the first
        // signal it gets, which is the bug again by another route.
        ZoneSystem zones = SetUp(savedNetworkExists: true);
        zones.LocationsGenerated = true;
        WorldDataArrives();
        Assert.True(RoadNetworkGenerator.RoadsAvailable);

        RoadLifecycleManager.OnZoneSystemDestroy(zones);

        bool stillLoaded = (bool)typeof(RoadLifecycleManager)
            .GetField("m_worldDataLoaded", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;
        Assert.False(stillLoaded, "the next world would decide before its own data was read");
    }
}
