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
    private readonly Func<bool>? _saveNow = ExistingWorldFirstLoad.SaveNow;
    private readonly Action<string>? _tell = ExistingWorldFirstLoad.Tell;
    private readonly List<string> _told = new();
    private int _menus;

    public ExistingWorldFirstLoadTests()
    {
        ExistingWorldFirstLoad.Backup = () => "World_backup_20261030-120000";
        ExistingWorldFirstLoad.SaveNow = () => true;
        ExistingWorldFirstLoad.Tell = _told.Add;
        RoadLifecycleManager.ReturnToMenuForTests = () => _menus++;
    }

    public void Dispose()
    {
        SetWorldDataLoaded(false);
        ExistingWorldFirstLoad.Setting = _setting;
        ExistingWorldFirstLoad.CanAsk = _canAsk;
        ExistingWorldFirstLoad.Backup = _backup;
        ExistingWorldFirstLoad.SaveNow = _saveNow;
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
    /// A world with locations, and the zones its save says were generated: <paramref name="explored"/> true gives it the
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
        Assert.True(RoadLifecycleManager.IsGenerating);
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
        Assert.True(RoadLifecycleManager.HoldsTheWorld, "the host would spawn before answering");
        Assert.NotNull(JoinHold.Reason);
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
        Assert.True(RoadLifecycleManager.HoldsTheWorld);
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

        // An automatic network has no exemption for the zone a road ends in: a location inside the zone is not one of its
        // destinations, and no road is laid up to it (before, it was reached up to the zone). A jump cannot fly over one.
        Assert.True(NetworkCutoff.AvoidsSegment(new Vector2(90f, 0f), new Vector2(120f, 0f), new Vector2(0f, 0f), new Vector2(120f, 0f)));
        Assert.True(NetworkCutoff.AvoidsSegment(new Vector2(80f, 0f), new Vector2(176f, 0f), null, null));
        // A manual road keeps its own: the zones its marks are in.
        using (NetworkCutoff.AvoidWhilePlanning(new HashSet<Vector2s> { avoided }, 0f))
            Assert.False(NetworkCutoff.AvoidsSegment(new Vector2(90f, 0f), new Vector2(120f, 0f), new Vector2(0f, 0f), new Vector2(120f, 0f)));
    }

    [Fact]
    public void AFailedEarthworkRetryClearsTheFinderBeforeItsNextRoad()
    {
        _world.WithWorld(new FlatWorld()).WithZdos().WithZoneSystem();
        ResetState();
        NetworkCutoff.SnapshotWorld(new[] { new Vector2s(1, 1), new Vector2s(2, 1), new Vector2s(3, 1) });
        NetworkCutoff.BeginNewNetwork();
        var finder = new RoadPathfinder(WorldGenerator.instance!);
        SetPathfinder(finder);
        bool injected = false;
        RoadNetworkGenerator.AfterEarthworkPenalty = active =>
        {
            Assert.Same(finder, active);
            Assert.True(active.EarthworkPenaltyCells > 0);
            injected = true;
            throw new InvalidOperationException("controlled earthwork retry failure");
        };
        try
        {
            Assert.False(RoadNetworkGenerator.GenerateRoad(new Vector3(0f, 0f, 24f), 0f,
                new Vector3(256f, 0f, 24f), 0f, 8f, "wide"));
            Assert.True(injected);
            Assert.Equal(RoadNetworkGenerator.PlanningExceptionRefusal, RoadSpatialGrid.LastRefusal);
            Assert.Equal(0, finder.EarthworkPenaltyCells);
            RoadNetworkGenerator.AfterEarthworkPenalty = null;
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(0f, 0f, -64f), 0f,
                new Vector3(256f, 0f, -64f), 0f, 4f, "next"), RoadSpatialGrid.LastRefusal);
            Assert.Equal(0, finder.EarthworkPenaltyCells);
        }
        finally
        {
            RoadNetworkGenerator.AfterEarthworkPenalty = null;
            SetPathfinder(null);
        }
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, pathfinder);

    /// <summary>
    /// The same protected strip across a road's straight line, in both modes for an explored world: the search, and the
    /// generator's whole road (straightened, swayed and snapped after it), go only through zones the writers may write
    /// (NetworkCutoff.Excludes, captured as the network begins). Built only in new land, every explored zone of the strip
    /// is protected; over the explored land only the two players built in, so the road may take the wild one. Before, a
    /// network built only in new land planned straight through the explored zones its writers then left as they were.
    /// </summary>
    [Theory]
    [InlineData(ExistingWorldRoads.NewZonesOnly)]
    [InlineData(ExistingWorldRoads.IncludeExplored)]
    public void ARoadIsPlannedOnlyThroughZonesItsWritersMayWrite(ExistingWorldRoads mode)
    {
        _world.WithWorld(new FlatWorld()).WithZdos().WithZoneSystem();
        ResetState();
        // Column x 96..160 across the line from (0,0) to (256,0): players built in (2,0) and (2,1); (2,-1) is wild.
        var built = new[] { new Vector2s(2, 0), new Vector2s(2, 1) };
        var wild = new Vector2s(2, -1);
        NetworkCutoff.SnapshotWorld(built.Append(wild));
        if (mode == ExistingWorldRoads.IncludeExplored) NetworkCutoff.IncludeExplored(built);
        NetworkCutoff.BeginNewNetwork();
        Assert.Equal(mode == ExistingWorldRoads.NewZonesOnly, NetworkCutoff.Excludes(wild));
        static Vector2s Zone(Vector2 p) => ZoneSystem.GetZone(new Vector3(p.x, 0f, p.y));
        var from = new Vector2(0f, 0f);
        var to = new Vector2(256f, 0f);

        List<Vector2>? path = new RoadPathfinder(WorldGenerator.instance!).FindPath(from, to);
        Assert.NotNull(path);
        Assert.DoesNotContain(path!, p => NetworkCutoff.Excludes(Zone(p)));

        SetPathfinder(new RoadPathfinder(WorldGenerator.instance!));
        try
        {
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector3(from.x, 0f, from.y), 0f, new Vector3(to.x, 0f, to.y), 0f, 4f, "across"), RoadSpatialGrid.LastRefusal);
            List<RoadSpatialGrid.RoadPoint> laid = RoadSpatialGrid.SnapshotAllRoadPoints();
            Assert.NotEmpty(laid);
            Assert.DoesNotContain(laid, p => NetworkCutoff.Excludes(Zone(p.p)));
        }
        finally { SetPathfinder(null); }
    }

    /// <summary>
    /// A generated road whose whole footprint, as it would be stored, reaches a zone the network goes round is searched again
    /// round it, and where that does not take it out is refused whole by the one final check (NetworkCutoff.Protected over
    /// ManualRoads.ZonesReached), whatever stage put it there. Here a road 8 m wide past the protected zones above it: its
    /// levelling reaches further than the search keeps a road of the 4 m setting (the writer's reach at the setting's width).
    /// With the searches again spent (EarthworkRetries 0) it is refused and nothing of it is stored; with them it is laid round,
    /// out of the zones' reach. Before, it was stored either way, and the writer gathered its points for the protected zones.
    /// </summary>
    [Fact]
    public void AGeneratedRoadWhoseFootprintReachesAProtectedZoneIsSearchedRoundElseRefusedWhole()
    {
        var kept = new[] { new Vector2s(1, 1), new Vector2s(2, 1), new Vector2s(3, 1) };
        int retries = RoadNetworkGenerator.EarthworkRetries;
        foreach (int spent in new[] { 0, retries })
        {
            _world.WithWorld(new FlatWorld()).WithZdos().WithZoneSystem();
            ResetState();
            NetworkCutoff.SnapshotWorld(kept);
            NetworkCutoff.BeginNewNetwork();
            SetPathfinder(new RoadPathfinder(WorldGenerator.instance!));
            RoadNetworkGenerator.EarthworkRetries = spent;
            try
            {
                bool built = RoadNetworkGenerator.GenerateRoad(new Vector3(0f, 0f, 24f), 0f, new Vector3(256f, 0f, 24f), 0f, 8f, "wide");
                if (spent == 0)
                {
                    Assert.False(built, "a road writing into a protected zone was stored");
                    Assert.Equal(RoadNetworkGenerator.ProtectedZoneRefusal, RoadSpatialGrid.LastRefusal);
                    Assert.Empty(RoadSpatialGrid.SnapshotAllRoadPoints());
                }
                else
                {
                    Assert.True(built, RoadSpatialGrid.LastRefusal);
                    Assert.NotEmpty(RoadSpatialGrid.SnapshotAllRoadPoints());
                    foreach (Vector2s zone in kept) Assert.True(RoadSpatialGrid.GetRoadPointsInZone(zone).Count == 0, $"the writer gathers road points for {zone}");
                }
            }
            finally { SetPathfinder(null); RoadNetworkGenerator.EarthworkRetries = retries; }
        }
    }

    /// <summary>
    /// A location in a zone the network leaves alone is not chosen at all, in both modes for an explored world: it neither
    /// ranks its island nor takes a location in its quota, and no road is aimed at it; the spawn there does not start the
    /// island's roads. A location in land the network may write is chosen. Before, it was chosen and connected up to the
    /// protected zone's border.
    /// </summary>
    [Theory]
    [InlineData(ExistingWorldRoads.NewZonesOnly)]
    [InlineData(ExistingWorldRoads.IncludeExplored)]
    public void ALocationInAProtectedZoneIsNotChosen(ExistingWorldRoads mode)
    {
        ExistingWorldFirstLoad.Setting = mode;
        ZoneSystem zones = World(explored: true);
        var spawnZone = new Vector2s(0, 0);
        zones.Generated.Add(spawnZone);
        ZDO bench = ZDOMan.instance!.CreateNewZDO(new Vector3(6f, 0f, 9f), "piece_workbench".GetStableHashCode());
        bench.Set(ZDOVars.s_creator, 1234L);
        Vector3 inBuilt = ZoneSystem.GetZonePos(Built), open = new(0f, 0f, -200f);
        foreach ((string name, Vector3 at) in new[] { ("Crypt4", inBuilt), ("Crypt3", open) })
            TestLocations.Add(zones, new ZoneSystem.LocationInstance
            {
                m_position = at,
                m_location = new ZoneSystem.ZoneLocation { m_prefab = new SoftReferenceableAssets.SoftReference<GameObject>(name), m_exteriorRadius = 16f },
            });
        List<string>? chosen = null;
        bool startsAtSpawn = true;
        IEnumerable<bool> Look(RoadNetworkGenerator.GenerationWork work, RoadPreparation preparation)
        {
            chosen = work.AllSelected.Select(d => d.name).ToList();
            startsAtSpawn = work.Islands.Any(i => i.Spawn != null);
            yield break;
        }
        RoadNetworkGenerator.LocationsFirst = Look;
        try
        {
            Open(zones);
            FinishGeneration();
        }
        finally { RoadNetworkGenerator.LocationsFirst = null; }
        Assert.True(NetworkCutoff.Excludes(Built));
        Assert.True(NetworkCutoff.Excludes(spawnZone));
        Assert.NotNull(chosen);
        Assert.DoesNotContain("Crypt4", chosen!);
        Assert.Contains("Crypt3", chosen!);
        Assert.Contains("Eikthyrnir", chosen!);
        Assert.False(startsAtSpawn, "the roads start at a spawn the network leaves alone");
    }

    /// <summary>What a network over the explored world leaves as it is, the zones it goes round while planned, and its roads.</summary>
    private sealed record Outcome(string Excluded, string Avoided, string Points);

    private static string ZonesOf(Func<Vector2s, bool> holds) =>
        string.Join(" ", new[] { Wild, Built, Dug }.Where(holds).Select(z => $"{z.x},{z.y}"));

    private static Outcome Run(Func<bool> begin)
    {
        Assert.True(begin());
        Assert.True(System.Threading.SpinWait.SpinUntil(() => { RoadLifecycleManager.Tick(); return NetworkCutoff.AvoidsAny || !RoadLifecycleManager.IsGenerating; }, 20000));
        string avoided = ZonesOf(NetworkCutoff.Avoids);
        FinishGeneration();
        Assert.True(RoadNetworkGenerator.RoadsGenerated);
        return new Outcome(ZonesOf(NetworkCutoff.Excludes), avoided,
            string.Join(";", RoadSpatialGrid.SnapshotAllRoadPoints().Select(p => FormattableString.Invariant($"{p.p.x:F2},{p.p.y:F2}"))));
    }

    /// <summary>
    /// One answer, one outcome, whichever path carries it out (rule 13): the first load given the answer, road_generate
    /// given it on the same world once its first load skipped the roads, and road_regen_island at the island. The same zones
    /// left as they are and gone round, and the same roads (road_regen_island: the zones). Before, the commands took no
    /// answer: they planned through every explored zone and left out all three.
    /// </summary>
    [Theory]
    [InlineData(ExistingWorldRoads.NewZonesOnly)]
    [InlineData(ExistingWorldRoads.IncludeExplored)]
    public void TheLoadAndTheCommandsTakeAnAnswerAlike(ExistingWorldRoads answer)
    {
        ExistingWorldFirstLoad.Setting = answer;
        ZoneSystem zones = World(explored: true);
        Outcome load = Run(() => { Open(zones); return true; });

        foreach (Vector3? island in new Vector3?[] { null, Vector3.zero })
        {
            RoadLifecycleManager.OnZoneSystemDestroy(zones);
            ExistingWorldFirstLoad.Setting = ExistingWorldRoads.Skip;
            zones = World(explored: true);
            Open(zones);
            Assert.False(RoadNetworkGenerator.NetworkInMemory);
            Outcome command = Run(() => RoadLifecycleManager.BeginForOperator(island, _ => { },
                command: island == null ? "road_generate" : "road_regen_island", force: true, answer: answer) == null);
            Assert.Equal(load.Excluded, command.Excluded);
            Assert.Equal(load.Avoided, command.Avoided);
            if (island == null) Assert.Equal(load.Points, command.Points);
        }
    }
}
