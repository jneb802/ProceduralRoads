using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Single-island regeneration (road_regen_island) on the synthetic world: one island, a spawn temple and three road locations.
/// </summary>
public class IslandRegenerationTests : System.IDisposable
{
    private readonly ValheimWorldScope _world = new ValheimWorldScope();
    /// <summary>LocationsFirstLoad.PlacingStage (the mod's loading-panel label; LocationsFirstLoad itself is not compiled here).</summary>
    private const string PlacingStage = "Placing locations before their roads";

    private static readonly (string name, float x, float z, float radius)[] StandardLocations =
    {
        ("StartTemple", 0f, 0f, 25f),
        ("Eikthyrnir", 260f, 40f, 10f),
        ("WoodVillage1", -220f, 150f, 18f),
        ("WoodVillage1", 120f, -260f, 18f),
    };

    private SyntheticWorld SetUp() => SetUp(StandardLocations);

    private SyntheticWorld SetUp(params (string name, float x, float z, float radius)[] locations)
    {
        var world = new SyntheticWorld { HasRiver = false, HasMountain = false };
        _world.WithWorld(world);
        _world.WithZoneSystem();
        var zones = ZoneSystem.instance!;
        foreach (var (name, x, z, radius) in locations)
        {
            TestLocations.Add(zones, new ZoneSystem.LocationInstance
            {
                m_location = new ZoneSystem.ZoneLocation { m_prefab = new SoftReferenceableAssets.SoftReference<GameObject>(name), m_exteriorRadius = radius },
                m_position = new Vector3(x, world.GetHeight(x, z), z),
            });
        }

        // A host's world: only the server generates roads (RoadLifecycleManager.HoldsTheNetwork).
        _world.WithZdos().WithNetwork(server: true);
        RoadNetworkGenerator.Reset();
        return world;
    }

    /// <summary>A generation run to its end as the game's frames run it (<see cref="RoadLifecycleManager.Tick"/>),
    /// each frame timed: the frames it took, and the longest. One already ended takes none.</summary>
    private static (int frames, double longestMs) FinishLoad()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int frames = 0;
        double longest = 0;
        while (RoadLifecycleManager.IsGenerating && clock.Elapsed.TotalSeconds < 120)
        {
            var frame = System.Diagnostics.Stopwatch.StartNew();
            RoadLifecycleManager.Tick();
            longest = Math.Max(longest, frame.Elapsed.TotalMilliseconds);
            frames++;
            System.Threading.Thread.Sleep(1);
        }
        Assert.False(RoadLifecycleManager.IsGenerating, "the load-time generation did not finish in 120 s");
        return (frames, longest);
    }

    /// <summary>The world loads as the game loads it, a host's: the load-time generation starts
    /// (<see cref="RoadLifecycleManager"/>, under the loading screen) and the frames run it.</summary>
    private void StartLoad()
    {
        _world.WithNetwork(server: true);
        RoadLifecycleManager.OnZoneSystemStart(ZoneSystem.instance!);
        ZoneSystem.instance!.LocationsGenerated = true;
        RoadLifecycleManager.OnWorldDataLoaded();
    }

    private static void TearDown()
    {
        if (ZoneSystem.instance != null) RoadLifecycleManager.OnZoneSystemDestroy(ZoneSystem.instance);
        RoadNetworkGenerator.Reset();
        RoadNetworkGenerator.GenerateOnLoad = true;
        ProceduralRoadsPlugin.ConfigLocationNames = new();
    }

    public void Dispose() => _world.Dispose();

    [Fact]
    public void IslandRegenerationMatchesGlobalOnAOneIslandWorld()
    {
        SetUp();
        try
        {
            RoadNetworkGenerator.GenerateRoads(force: true);
            int globalPoints = RoadSpatialGrid.TotalRoadPoints;
            Assert.True(globalPoints > 0, "global generation produced no road points");

            Assert.True(RoadNetworkGenerator.RegenerateIslandAt(new Vector3(0f, 0f, 0f), out string summary), summary);
            Assert.True(RoadNetworkGenerator.RoadsAvailable);
            Assert.Equal(globalPoints, RoadSpatialGrid.TotalRoadPoints);
            Assert.Contains("roads", summary);
        }
        finally { TearDown(); }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void OperatorGenerationKeepsCapturedOptionsWhenLiveOptionsChange(bool oneIsland, bool capturedQuestLocations)
    {
        SetUp(("StartTemple", 0f, 0f, 25f), ("Eikthyrnir", 260f, 40f, 10f), ("Hildir_cave", -220f, 150f, 18f));
        RoadNetworkOptions previous = RoadNetworkGenerator.NetworkOptions;
        try
        {
            RoadNetworkGenerator.NetworkOptions = new RoadNetworkOptions { HildirQuestLocations = capturedQuestLocations };
            List<string>? chosen = null;
            bool? duringPlacing = null;
            IEnumerable<bool> Record(RoadNetworkGenerator.GenerationWork work)
            {
                chosen = work.AllSelected.Select(d => d.name).ToList();
                duringPlacing = RoadNetworkGenerator.HildirRoads("Hildir_cave");
                yield break;
            }
            RoadNetworkGenerator.LocationsFirst = (work, _) => Record(work);
            Assert.Null(RoadLifecycleManager.BeginForOperator(oneIsland ? Vector3.zero : null, _ => { },
                answer: ExistingWorldRoads.NewZonesOnly));
            RoadNetworkGenerator.NetworkOptions.HildirQuestLocations = !capturedQuestLocations;
            FinishLoad();

            Assert.True(RoadNetworkGenerator.RoadsAvailable);
            Assert.NotNull(chosen);
            Assert.Equal(capturedQuestLocations, chosen!.Contains("Hildir_cave"));
            Assert.Equal(capturedQuestLocations, duringPlacing);
        }
        finally
        {
            RoadNetworkGenerator.LocationsFirst = null;
            RoadNetworkGenerator.NetworkOptions = previous;
            TearDown();
        }
    }

    [Fact]
    public void LoadTimeGenerationCanBeSwitchedOffAndIslandRegenerationStillWorks()
    {
        SetUp();
        using var overrides = StaticOverride.Keep(() => RoadNetworkGenerator.GenerateOnLoad);
        try
        {
            RoadNetworkGenerator.GenerateOnLoad = false;
            StartLoad();
            Assert.False(RoadLifecycleManager.IsGenerating);
            Assert.False(RoadNetworkGenerator.RoadsAvailable);
            Assert.Equal(0, RoadSpatialGrid.TotalRoadPoints);

            Assert.True(RoadNetworkGenerator.RegenerateIslandAt(Vector3.zero, out string summary), summary);
            Assert.True(RoadSpatialGrid.TotalRoadPoints > 0);

            RoadNetworkGenerator.GenerateOnLoad = true;
            RoadNetworkGenerator.Reset();
            StartLoad();
            Assert.True(RoadLifecycleManager.IsGenerating);
            FinishLoad();
            Assert.True(RoadNetworkGenerator.RoadsAvailable);
        }
        finally { TearDown(); }
    }

    /// <summary>At world load the main thread is never held for the choosing or the building: the call that
    /// starts it returns before any road is built, and the frames go on, each short, while workers build the
    /// network (the choosing once held the loading screen's main thread, so the harness's CLI answered nothing
    /// for 30 s). With the load-time lifecycle the choosing is sliced on the main thread and the
    /// building runs on workers; the locations are placed (LocationsFirst) from a frame, on the main thread.</summary>
    [Fact]
    public void LoadTimeGenerationKeepsTheFramesGoingWhileWorkersBuildTheRoads()
    {
        SetUp();
        try
        {
            int main = Environment.CurrentManagedThreadId;
            var chosenOn = new List<int>();
            IEnumerable<bool> Record() { chosenOn.Add(Environment.CurrentManagedThreadId); yield break; }
            RoadNetworkGenerator.LocationsFirst = (_, _) => Record();
            var started = System.Diagnostics.Stopwatch.StartNew();

            StartLoad();

            double startMs = started.Elapsed.TotalMilliseconds;
            Assert.True(RoadLifecycleManager.IsGenerating);
            Assert.False(RoadNetworkGenerator.RoadsAvailable);
            var (frames, longest) = FinishLoad();
            double total = started.Elapsed.TotalMilliseconds;
            Assert.True(RoadNetworkGenerator.RoadsAvailable);
            Assert.True(RoadSpatialGrid.TotalRoadPoints > 0);
            // Locations are placed (LocationsFirst) on the main thread, from a frame.
            Assert.Equal(new[] { main }, chosenOn);
            Assert.True(frames > 2, $"{frames} frame(s) over {total:F0} ms");
            Assert.True(startMs < total / 2 && longest < total / 2,
                $"the start held the main thread {startMs:F0} ms and the longest frame {longest:F0} ms of {total:F0} ms");
        }
        finally { RoadNetworkGenerator.LocationsFirst = null; TearDown(); }
    }

    /// <summary>At world load the roads wait for the locations that are placed first ("locations first"):
    /// the destinations are chosen, the placing runs, and only then are the roads built. Built at once
    /// when nothing waits. The placing is a preparation stage of its own on the loading panel.</summary>
    [Fact]
    public void LoadTimeGenerationWaitsForItsLocationsToBePlacedBeforeBuildingRoads()
    {
        SetUp();
        try
        {
            bool release = false;
            List<string>? chosen = null;
            IEnumerable<bool> Hold(RoadNetworkGenerator.GenerationWork work, RoadPreparation preparation)
            {
                chosen = work.AllSelected.Select(d => d.name).ToList();
                preparation.BeginStage(PlacingStage, 1, "locations"); yield return true;
                while (!release) { preparation.EndSlice(); yield return true; }
                preparation.CompleteUnit();
            }
            RoadNetworkGenerator.LocationsFirst = Hold;

            StartLoad();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (RoadLifecycleManager.Progress.PreparationStage != PlacingStage && RoadLifecycleManager.IsGenerating && clock.Elapsed.TotalSeconds < 30)
                RoadLifecycleManager.Tick();
            for (int frame = 0; frame < 10; frame++) RoadLifecycleManager.Tick();

            Assert.True(RoadLifecycleManager.IsGenerating);
            Assert.Equal("Preparing", RoadLifecycleManager.Progress.Phase);
            Assert.Equal(PlacingStage, RoadLifecycleManager.Progress.PreparationStage);
            Assert.False(RoadNetworkGenerator.RoadsAvailable);
            Assert.Equal(0, RoadSpatialGrid.TotalRoadPoints);
            Assert.Contains("Eikthyrnir", chosen!);
            release = true;
            FinishLoad();
            Assert.True(RoadNetworkGenerator.RoadsAvailable);
            Assert.True(RoadSpatialGrid.TotalRoadPoints > 0);

            // Nothing to place: built at once.
            RoadNetworkGenerator.Reset();
            RoadNetworkGenerator.LocationsFirst = (_, _) => Enumerable.Empty<bool>();
            RoadLifecycleManager.OnWorldDataLoaded();
            Assert.True(RoadLifecycleManager.IsGenerating);
            FinishLoad();
            Assert.True(RoadNetworkGenerator.RoadsAvailable);
        }
        finally { RoadNetworkGenerator.LocationsFirst = null; TearDown(); }
    }

    [Fact]
    public void LoadTimeGenerationAppliesTerrainToZonesLoadedBeforeIt()
    {
        SetUp();
        try
        {
            // Zones that exist before generation, a column across the road from the
            // start temple towards Eikthyrnir (which door it arrives at decides which
            // of them it crosses): the one it crosses must carry road deltas and paint
            // right after the load-time generation, without any zone spawn, and none
            // is written twice.
            var zones = new[] { new Vector2s(2, -1), new Vector2s(2, 0), new Vector2s(2, 1) };
            var compilers = zones.Select(z => _world.RegisterHeightmap(z, 64).m_terrainComp!).ToList();

            StartLoad();
            FinishLoad();
            Assert.Contains(compilers, tc => tc.SaveCount == 1 && tc.m_modifiedHeight.Any(m => m) && tc.m_modifiedPaint.Any(m => m));
            Assert.All(compilers, tc => Assert.True(tc.SaveCount <= 1));
        }
        finally
        {
            TearDown();
        }
    }

    [Fact]
    public void IslandRegenerationOnARoadFreeWorldSavesAndReloads()
    {
        // The validation loop: load-time generation off, one island regenerated,
        // the world saved, the next session loads the roads back. Island
        // regeneration must create the metadata object the save path writes to,
        // exactly as global generation does.
        SetUp();
        try
        {
            RoadNetworkGenerator.GenerateOnLoad = false;
            StartLoad();
            Assert.False(RoadLifecycleManager.IsGenerating);
            Assert.True(RoadNetworkGenerator.RegenerateIslandAt(Vector3.zero, out string summary), summary);
            Assert.True(RoadSpatialGrid.TotalRoadPoints > 0);
            byte[] network = RoadSpatialGrid.SerializeAllRoadPoints()!;
            int version = RoadSpatialGrid.RoadNetworkVersion;
            Assert.NotEqual(0, version);

            var log = _world.CaptureLog();
            RoadNetworkGenerator.SaveGlobalRoadData();
            BepInEx.Logging.ManualLogSource.Captured = null;
            Assert.DoesNotContain(log, l => l.Contains("No metadata ZDO"));
            Assert.Equal(1, ZDOMan.instance!.CountWithPrefab(RoadNetworkPersistence.MetadataPrefabName));

            RoadNetworkGenerator.Reset();
            Assert.Equal(0, RoadSpatialGrid.TotalRoadPoints);
            Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData(), "saved network did not load back");
            Assert.Equal(network, RoadSpatialGrid.SerializeAllRoadPoints());
            Assert.Equal(version, RoadSpatialGrid.RoadNetworkVersion);
        }
        finally { TearDown(); }
    }

    [Fact]
    public void IslandRegenerationAfterGlobalGenerationReusesTheMetadataObject()
    {
        SetUp();
        try
        {
            RoadNetworkGenerator.GenerateRoads(force: true);
            Assert.True(RoadNetworkGenerator.RegenerateIslandAt(Vector3.zero, out string summary), summary);
            RoadNetworkGenerator.SaveGlobalRoadData();
            Assert.Equal(1, ZDOMan.instance!.CountWithPrefab(RoadNetworkPersistence.MetadataPrefabName));

            byte[] network = RoadSpatialGrid.SerializeAllRoadPoints()!;
            RoadNetworkGenerator.Reset();
            Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData());
            Assert.Equal(network, RoadSpatialGrid.SerializeAllRoadPoints());
        }
        finally { TearDown(); }
    }

    [Fact]
    public void FreshSessionIslandRegenerationSeesConfiguredCustomLocations()
    {
        // Only the start temple and a location the config names: without the
        // config registration the island has no road-eligible location at all.
        SetUp(("StartTemple", 0f, 0f, 25f), ("ModCamp", 240f, -60f, 12f));
        try
        {
            RoadNetworkGenerator.GenerateOnLoad = false;
            Assert.False(RoadNetworkGenerator.RegenerateIslandAt(Vector3.zero, out string without));
            Assert.Contains("no road-eligible locations", without);

            ProceduralRoadsPlugin.ConfigLocationNames = new() { "ModCamp" };
            Assert.True(RoadNetworkGenerator.RegenerateIslandAt(Vector3.zero, out string summary), summary);
            Assert.Contains("ModCamp", RoadNetworkGenerator.GetRegisteredLocations());
            var nearCamp = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(240f, 0f, -60f), 12f + 8f);
            Assert.True(nearCamp.Count > 0, "no road reaches the configured location");
        }
        finally
        {
            RoadNetworkGenerator.UnregisterLocation("ModCamp");
            TearDown();
        }
    }

    [Fact]
    public void RegenerationRefusesAPointInTheOcean()
    {
        SetUp();
        try
        {
            Assert.False(RoadNetworkGenerator.RegenerateIslandAt(new Vector3(3000f, 0f, 3000f), out string summary));
            Assert.Contains("No island", summary);
            Assert.Equal(0, RoadSpatialGrid.TotalRoadPoints);
        }
        finally { TearDown(); }
    }

}
