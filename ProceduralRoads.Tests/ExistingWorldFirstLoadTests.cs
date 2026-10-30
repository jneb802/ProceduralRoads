using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The first load of a world players explored before it had roads (Roads.ExistingWorldFirstLoadRoads, or the choice on
/// the loading screen). A fresh world and a world with its roads never ask. An explored one asks the host, or takes
/// the setting: skip the roads (remembered), build them only in new land (what the mod always did), build them through
/// the explored land too but round every zone players built in or dug, or go back to the menu having decided nothing.
/// </summary>
public sealed class ExistingWorldFirstLoadTests : IDisposable
{
    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0; width = 0; }
    }

    /// <summary>Explored, nobody built there.</summary>
    private static readonly Vector2s Wild = new(3, 0);
    /// <summary>Explored, a player's piece stands there.</summary>
    private static readonly Vector2s Built = new(4, 0);
    /// <summary>Explored, a player dug there (a terrain compiler and nothing else).</summary>
    private static readonly Vector2s Dug = new(5, 0);

    private readonly ValheimWorldScope _world = new();
    private readonly ExistingWorldRoads _setting = ExistingWorldFirstLoad.Setting;
    private readonly Func<bool> _canAsk = ExistingWorldFirstLoad.CanAsk;
    private readonly Func<string?>? _backup = ExistingWorldFirstLoad.Backup;
    private readonly Action<string>? _tell = ExistingWorldFirstLoad.Tell;
    private readonly List<string> _told = new();
    private int _menus;

    public ExistingWorldFirstLoadTests()
    {
        ExistingWorldFirstLoad.Backup = () => "World_backup_20261030-120000";
        ExistingWorldFirstLoad.Tell = _told.Add;
        RoadLifecycleManager.ReturnToMenuForTests = () => _menus++;
    }

    public void Dispose()
    {
        SetWorldDataLoaded(false);
        ExistingWorldFirstLoad.Setting = _setting;
        ExistingWorldFirstLoad.CanAsk = _canAsk;
        ExistingWorldFirstLoad.Backup = _backup;
        ExistingWorldFirstLoad.Tell = _tell;
        RoadLifecycleManager.ReturnToMenuForTests = null;
        if (ZoneSystem.instance != null) RoadLifecycleManager.OnZoneSystemDestroy(ZoneSystem.instance);
        NetworkCutoff.Forget();
        RoadNetworkPersistence.Reset(); RoadSpatialGrid.Clear(); RoadNetworkGenerator.Reset();
        _world.Dispose();
    }

    private static void SetWorldDataLoaded(bool value) =>
        typeof(RoadLifecycleManager).GetField("m_worldDataLoaded", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, value);

    private static void ResetState()
    {
        SetWorldDataLoaded(false);
        NetworkCutoff.Forget();
        RoadNetworkPersistence.Reset(); RoadSpatialGrid.Clear(); RoadNetworkGenerator.Reset();
    }

    /// <summary>
    /// A world with places, and the zones its save says were generated: <paramref name="explored"/> true gives it the
    /// three explored zones (one with a player's piece, one dug), false is a fresh world.
    /// </summary>
    private ZoneSystem World(bool explored)
    {
        _world.WithWorld(new SyntheticWorld { HasRiver = false, HasMountain = false }).WithZdos().WithZoneSystem().WithScene().WithNetwork(server: true);
        ResetState();
        ZoneSystem zones = ZoneSystem.instance!;
        foreach ((string name, float x) in new[] { ("StartTemple", 0f), ("Eikthyrnir", 420f) })
            TestLocations.Add(zones, new ZoneSystem.LocationInstance
            {
                m_position = new Vector3(x, 0f, 0f),
                m_location = new ZoneSystem.ZoneLocation { m_prefab = new SoftReferenceableAssets.SoftReference<GameObject>(name), m_exteriorRadius = 16f },
            });
        if (explored)
        {
            zones.Generated.Add(Wild);
            zones.Generated.Add(Built);
            zones.Generated.Add(Dug);
            ZDO piece = ZDOMan.instance!.CreateNewZDO(ZoneSystem.GetZonePos(Built) + new Vector3(5f, 0f, 3f), "piece_workbench".GetStableHashCode());
            piece.Set(ZDOVars.s_creator, 1234L);
            ZDOMan.instance.CreateNewZDO(ZoneSystem.GetZonePos(Dug), "_TerrainCompiler".GetStableHashCode());
            // Not a player's: a tree in the wild zone has no creator.
            ZDOMan.instance.CreateNewZDO(ZoneSystem.GetZonePos(Wild), "Beech1".GetStableHashCode());
        }
        return zones;
    }

    /// <summary>The server opens the world: locations in place, then ZNet's world load returns.</summary>
    private static void Open(ZoneSystem zones)
    {
        zones.LoadLocationsGeneratedFromSave(true);
        RoadLifecycleManager.OnZoneSystemStart(zones);
        RoadLifecycleManager.OnWorldDataLoaded();
    }

    private static void FinishGeneration() =>
        Assert.True(System.Threading.SpinWait.SpinUntil(() => { RoadLifecycleManager.Tick(); return !RoadLifecycleManager.IsGenerating; }, 20000));

    /// <summary>The world unloads and the next session opens it from what was saved.</summary>
    private static void Reopen(ZoneSystem zones)
    {
        RoadLifecycleManager.OnPrepareSave();
        RoadLifecycleManager.OnZoneSystemDestroy(zones);
        ResetState();
        Open(zones);
    }

    [Theory]
    [InlineData(0, false, ExistingWorldRoads.Skip, true, false, ExistingWorldFirstLoad.Decision.AsAlways)]
    [InlineData(5, true, ExistingWorldRoads.IncludeExplored, true, false, ExistingWorldFirstLoad.Decision.Skip)]
    [InlineData(5, false, ExistingWorldRoads.Ask, true, false, ExistingWorldFirstLoad.Decision.Ask)]
    [InlineData(5, false, ExistingWorldRoads.Ask, false, false, ExistingWorldFirstLoad.Decision.IncludeExplored)]
    [InlineData(5, false, ExistingWorldRoads.Ask, true, true, ExistingWorldFirstLoad.Decision.IncludeExplored)]
    [InlineData(5, false, ExistingWorldRoads.Skip, false, false, ExistingWorldFirstLoad.Decision.Skip)]
    [InlineData(5, false, ExistingWorldRoads.NewZonesOnly, true, false, ExistingWorldFirstLoad.Decision.NewZonesOnly)]
    [InlineData(5, false, ExistingWorldRoads.IncludeExplored, false, false, ExistingWorldFirstLoad.Decision.IncludeExplored)]
    public void TheDecision(int explored, bool skippedBefore, ExistingWorldRoads setting, bool canAsk, bool playerInWorld,
        ExistingWorldFirstLoad.Decision expected)
    {
        // A fresh world generates as always; a skip is remembered; Ask without a screen (a dedicated server) or past
        // the loading screen takes the recommended answer, the explored land included.
        Assert.Equal(expected, ExistingWorldFirstLoad.Decide(explored, skippedBefore, setting, canAsk, playerInWorld));
    }

    [Fact]
    public void AFreshWorldNeverAsks()
    {
        ExistingWorldFirstLoad.Setting = ExistingWorldRoads.Ask;
        ExistingWorldFirstLoad.CanAsk = () => true;
        Open(World(explored: false));
        Assert.False(RoadLifecycleManager.ChoicePending);
        Assert.True(RoadLifecycleManager.InitialGenerationActive);
        FinishGeneration();
        Assert.True(RoadNetworkGenerator.RoadsGenerated);
    }

    [Fact]
    public void AnExploredWorldAsksAndHoldsTheLoadingScreenUntilAnswered()
    {
        ExistingWorldFirstLoad.Setting = ExistingWorldRoads.Ask;
        ExistingWorldFirstLoad.CanAsk = () => true;
        ZoneSystem zones = World(explored: true);
        Open(zones);

        Assert.True(RoadLifecycleManager.ChoicePending);
        Assert.True(RoadLifecycleManager.HoldsLoadingScreen, "the host would spawn before answering");
        Assert.False(RoadLifecycleManager.IsGenerating);
        // Another signal while asking decides nothing.
        RoadLifecycleManager.OnPlayerSpawn(Vector3.zero);
        Assert.False(RoadLifecycleManager.IsGenerating);

        Assert.True(RoadLifecycleManager.Answer(ExistingWorldRoads.NewZonesOnly));
        Assert.False(RoadLifecycleManager.ChoicePending);
        Assert.False(RoadLifecycleManager.Answer(ExistingWorldRoads.Skip), "a second answer was taken");
        FinishGeneration();
        Assert.True(RoadNetworkGenerator.RoadsGenerated);
        foreach (Vector2s zone in new[] { Wild, Built, Dug })
            Assert.True(NetworkCutoff.Excludes(zone), $"explored zone {zone.x},{zone.y} would be written");
    }

    [Fact]
    public void ADedicatedServerWithAskIncludesTheExploredLand()
    {
        ExistingWorldFirstLoad.Setting = ExistingWorldRoads.Ask;
        ExistingWorldFirstLoad.CanAsk = () => false;
        Open(World(explored: true));
        Assert.False(RoadLifecycleManager.ChoicePending);
        FinishGeneration();
        Assert.True(RoadNetworkGenerator.RoadsGenerated);
        Assert.False(NetworkCutoff.Excludes(Wild));
        Assert.True(NetworkCutoff.Excludes(Built));
        Assert.True(NetworkCutoff.Excludes(Dug));
    }

    [Fact]
    public void ASetToNewLandOnlyLeavesEveryExploredZone()
    {
        ExistingWorldFirstLoad.Setting = ExistingWorldRoads.NewZonesOnly;
        Open(World(explored: true));
        FinishGeneration();
        Assert.True(RoadNetworkGenerator.RoadsGenerated);
        Assert.Equal(3, NetworkCutoff.ExcludedCount);
    }

    [Fact]
    public void SkippingBuildsNothingAndIsRememberedOnTheNextLoad()
    {
        ExistingWorldFirstLoad.Setting = ExistingWorldRoads.Ask;
        ExistingWorldFirstLoad.CanAsk = () => true;
        ZoneSystem zones = World(explored: true);
        Open(zones);
        Assert.True(RoadLifecycleManager.Answer(ExistingWorldRoads.Skip));
        Assert.False(RoadLifecycleManager.IsGenerating);
        Assert.False(RoadNetworkGenerator.RoadsAvailable);
        Assert.True(RoadNetworkPersistence.FirstLoadSkipped());

        // The next session, even set to build: the first load decided, and it skipped.
        ExistingWorldFirstLoad.Setting = ExistingWorldRoads.IncludeExplored;
        Reopen(zones);
        Assert.False(RoadLifecycleManager.ChoicePending);
        Assert.False(RoadLifecycleManager.IsGenerating);
        RoadLifecycleManager.OnPlayerSpawn(Vector3.zero);
        Assert.False(RoadLifecycleManager.IsGenerating);
        Assert.False(RoadNetworkGenerator.RoadsAvailable);
    }

    [Fact]
    public void BackToTheMenuDecidesAndSavesNothing()
    {
        ExistingWorldFirstLoad.Setting = ExistingWorldRoads.Ask;
        ExistingWorldFirstLoad.CanAsk = () => true;
        ZoneSystem zones = World(explored: true);
        Open(zones);
        Assert.True(RoadLifecycleManager.CancelToMenu());
        Assert.Equal(1, _menus);
        // Still held until the game has left the world: the player never spawns into it.
        Assert.True(RoadLifecycleManager.HoldsLoadingScreen);
        Assert.False(RoadLifecycleManager.IsGenerating);
        Assert.False(RoadNetworkPersistence.FirstLoadSkipped());

        Reopen(zones);
        Assert.True(RoadLifecycleManager.ChoicePending, "the next load did not ask again");
    }

    [Fact]
    public void IncludingTheExploredLandLeavesOnlyTheZonesPlayersBuiltInOrDug()
    {
        ExistingWorldFirstLoad.Setting = ExistingWorldRoads.IncludeExplored;
        ZoneSystem zones = World(explored: true);
        Assert.Equal(new HashSet<Vector2s> { Built, Dug },
            ExistingWorldFirstLoad.PlayerMarkedZones(new HashSet<Vector2s> { Wild, Built, Dug }));

        Open(zones);
        // Set when the network is planned (the first preparation step), held until it is published.
        Assert.True(System.Threading.SpinWait.SpinUntil(() => { RoadLifecycleManager.Tick(); return NetworkCutoff.AvoidsAny || !RoadLifecycleManager.IsGenerating; }, 20000));
        Assert.True(NetworkCutoff.Avoids(Built), "the search would go through a player's base");
        Assert.True(NetworkCutoff.Avoids(Dug));
        Assert.False(NetworkCutoff.Avoids(Wild));
        FinishGeneration();
        Assert.True(RoadNetworkGenerator.RoadsGenerated);
        Assert.False(NetworkCutoff.Excludes(Wild), "explored land nobody built in was left out");
        Assert.True(NetworkCutoff.Excludes(Built));
        Assert.True(NetworkCutoff.Excludes(Dug));
        Assert.False(NetworkCutoff.AvoidsAny, "a later search would still go round them");
        Assert.Contains(_told, t => t.Contains("2 explored zone(s)") && t.Contains("road_connect"));

        // Saved: the wild zone is the network's from now on, the two others stay left alone.
        Reopen(zones);
        Assert.True(RoadNetworkGenerator.RoadsLoadedFromZDO);
        Assert.False(NetworkCutoff.Excludes(Wild));
        Assert.True(NetworkCutoff.Excludes(Built));
        Assert.True(NetworkCutoff.Excludes(Dug));
    }

    [Fact]
    public void WithoutABackupNoExploredZoneIsWritten()
    {
        ExistingWorldFirstLoad.Setting = ExistingWorldRoads.IncludeExplored;
        ExistingWorldFirstLoad.Backup = () => null;
        Open(World(explored: true));
        FinishGeneration();
        Assert.True(RoadNetworkGenerator.RoadsGenerated);
        foreach (Vector2s zone in new[] { Wild, Built, Dug })
            Assert.True(NetworkCutoff.Excludes(zone), $"explored zone {zone.x},{zone.y} would be written without a backup");
        Assert.Contains(_told, t => t.Contains("could not be backed up"));
    }

    [Fact]
    public void TheSearchGoesRoundAnAvoidedZone()
    {
        _world.WithWorld(new FlatWorld()).WithZdos().WithZoneSystem();
        ResetState();
        // Zone (2,0) spans x 96..160, z -32..32: right across the straight line from (0,0) to (256,0).
        var avoided = new Vector2s(2, 0);
        NetworkCutoff.SnapshotWorld(new[] { avoided });
        NetworkCutoff.IncludeExplored(new[] { avoided });
        NetworkCutoff.BeginNewNetwork();
        Assert.True(NetworkCutoff.AvoidsAny);

        List<Vector2>? path = new RoadPathfinder(WorldGenerator.instance!).FindPath(new Vector2(0f, 0f), new Vector2(256f, 0f));
        Assert.NotNull(path);
        Assert.DoesNotContain(path!, p => ZoneSystem.GetZone(new Vector3(p.x, 0f, p.y)) == avoided);

        // A place inside the zone is still reached, up to it; and a jump cannot fly over one.
        Assert.False(NetworkCutoff.AvoidsSegment(new Vector2(90f, 0f), new Vector2(120f, 0f), new Vector2(0f, 0f), new Vector2(120f, 0f)));
        Assert.True(NetworkCutoff.AvoidsSegment(new Vector2(80f, 0f), new Vector2(176f, 0f), null, null));
    }
}
