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
///
/// Valheim 1.0 added a third case that looks like neither. Its chunked loader
/// (ZoneSystem.Load) writes the LocationsGenerated field directly instead of
/// through the property setter, so the event is not raised early -- it is not
/// raised at all. The other two doors survive on 1.0 and still raise it: a new
/// world's location coroutine, and ZoneSystem.LoadOld, which is what 1.0 runs
/// the first time a player opens a world saved before they updated. The last
/// two tests cover the silent door, and hold that it still does not excuse the
/// world-data gate.
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

    /// <summary>
    /// The ZDOs the world would have loaded, held back until the test says
    /// they have arrived. Removing the metadata ZDO from ZDOMan is exactly
    /// what "the world's data is not in memory yet" means to the real search,
    /// so these tests exercise FindMetadataZDO for real rather than a stub.
    /// </summary>
    private readonly List<ZDO> m_heldBack = new();

    /// <summary>A world as the game hands one over: the generator reset, the
    /// mod subscribed, nothing decided yet.</summary>
    private ZoneSystem SetUp(bool savedNetworkExists)
    {
        WorldGenerator.instance = new SyntheticWorld { HasRiver = false, HasMountain = false };
        ZDOMan.instance = new ZDOMan();
        SetWorldDataLoaded(false);
        RoadNetworkPersistence.Reset();
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.Reset();

        ZoneSystem zones = WorldWithPlaces();
        ZoneSystem.instance = zones;

        if (savedNetworkExists)
        {
            // A previous session's network, written through the real save path
            // so the bytes and the metadata ZDO are the ones the mod makes.
            List<Vector2> path = new();
            for (int i = -6; i <= 6; i++)
                path.Add(new Vector2(i * 8f, 0f));
            RoadSpatialGrid.AddRoadPath(path, 4f, WorldGenerator.instance);
            RoadSpatialGrid.FinalizeRoadNetwork();
            RoadNetworkPersistence.EnsureMetadataInstance();
            RoadNetworkPersistence.SaveGlobalRoadData(new List<(Vector2, string)>());

            // Now start the session over, with that world on "disk" but not
            // yet read: the metadata ZDO is held back until the test says the
            // world's data has arrived.
            m_heldBack.AddRange(ZDOMan.instance.Zdos);
            ZDOMan.instance.Zdos.Clear();
            RoadNetworkPersistence.Reset();
            RoadSpatialGrid.Clear();
            RoadNetworkGenerator.Reset();
        }

        RoadLifecycleManager.OnZoneSystemStart(zones);
        return zones;
    }

    /// <summary>
    /// The world's data finishing loading: its ZDOs enter ZDOMan, and then
    /// ZNet's world load returns. In that order, as in the game.
    /// </summary>
    private void WorldDataArrives()
    {
        ZDOMan.instance!.Zdos.AddRange(m_heldBack);
        m_heldBack.Clear();
        RoadLifecycleManager.OnWorldDataLoaded();
    }

    public void Dispose()
    {
        SetWorldDataLoaded(false);
        m_heldBack.Clear();
        RoadNetworkPersistence.Reset();
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.Reset();
        WorldGenerator.instance = null;
        ZoneSystem.instance = null;
        ZDOMan.instance = null;
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
    }

    [Fact]
    public void TheSavedNetworkIsLoadedOnceTheWorldsDataArrives()
    {
        ZoneSystem zones = SetUp(savedNetworkExists: true);

        zones.LocationsGenerated = true;
        WorldDataArrives();

        Assert.True(RoadNetworkGenerator.RoadsLoadedFromZDO, "the saved network was not loaded");
        Assert.False(RoadNetworkGenerator.RoadsGenerated, "a network was generated over the saved one");
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
    }

    [Fact]
    public void PlayerSpawnIsTheBackstopWhenTheWorldLoadHookDoesNot()
    {
        // Not every path goes through ZNet's server world load, so spawning is
        // the last chance to decide. This covers the host: the test hands the
        // world's ZDOs over before spawning, which is what a host has by then.
        // It says nothing about a client, where the road metadata may not have
        // replicated yet -- that case is not covered here or anywhere.
        ZoneSystem zones = SetUp(savedNetworkExists: true);

        zones.LocationsGenerated = true;
        Assert.False(RoadNetworkGenerator.RoadsAvailable);

        ZDOMan.instance!.Zdos.AddRange(m_heldBack);
        m_heldBack.Clear();
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
        ZDOMan.instance!.Zdos.AddRange(m_heldBack);
        m_heldBack.Clear();
        RoadLifecycleManager.OnPlayerSpawn(Vector3.zero);

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

    [Fact]
    public void TheChunkedLoaderSetsTheFlagWithoutAnEventAndTheSavedNetworkStillLoads()
    {
        // Valheim 1.0's chunked loader does not go through the property setter
        // that raises GenerateLocationsCompleted -- ZoneSystem.Load writes
        // m_locationsGenerated straight from the save. Subscribing early does
        // not help: the handler is already waiting and is never called. If the
        // decision waited on the event, an existing world would sit here for
        // ever and the player would find their roads gone.
        ZoneSystem zones = SetUp(savedNetworkExists: true);

        bool eventFired = false;
        zones.GenerateLocationsCompleted += () => eventFired = true;

        zones.LoadLocationsGeneratedFromSave(true);   // the 1.0 load path
        Assert.False(eventFired, "1.0 raises nothing when a world is read from disk");
        Assert.False(RoadNetworkGenerator.RoadsAvailable, "decided before the world's data was read");

        WorldDataArrives();

        Assert.True(RoadNetworkGenerator.RoadsLoadedFromZDO, "the saved network was not loaded");
        Assert.False(RoadNetworkGenerator.RoadsGenerated, "a network was generated over the saved one");
    }

    [Fact]
    public void TheFlagArrivingWithoutTheWorldsDataDecidesNothing()
    {
        // The gate that must survive the 1.0 fix. Locations being in place says
        // nothing about whether the world's ZDOs have been read, and the saved
        // network lives in a ZDO -- so deciding on the flag alone would look for
        // it before anything was in memory, find nothing, and build over it.
        // This is the same trap as TheLocationsEventAloneDecidesNothing, reached
        // through 1.0's door instead of the legacy one.
        ZoneSystem zones = SetUp(savedNetworkExists: true);

        zones.LoadLocationsGeneratedFromSave(true);

        Assert.False(RoadNetworkGenerator.RoadsGenerated,
            "a network was generated before the world's data had been read");
        Assert.False(RoadNetworkGenerator.RoadsAvailable);
    }
}
