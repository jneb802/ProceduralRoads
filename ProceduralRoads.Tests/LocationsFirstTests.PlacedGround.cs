using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public partial class LocationsFirstTests
{
    /// <summary>
    /// One published ground for a location (PlacedLocations.GroundAt), read alike by the terrain writer and the finished-ground
    /// predictor on any thread: a location not on the preload list has none (not known to either, never the natural ground);
    /// on it, its preload's ground, known to a worker too.
    /// </summary>
    [Fact]
    public void ALocationsGroundIsKnownOnlyFromItsPreloadAndAloneForTheWriterAndThePredictor()
    {
        PlacedLocation cave = Cave();
        var inside = new Vector2(3f, 2f);
        Func<int, int, float> Predictor() => RoadTerrainModifier.FinishedGround((x, z) => _world.GetHeight(x, z));
        // Not on the preload list: not known, to the writer and the predictor alike.
        Assert.Null(PlacedLocations.GroundAt(inside));
        Assert.True(float.IsNaN(Predictor()(3, 2)));
        PlacedLocations.Preload(cave.Centre, cave.Ground);
        float placed = cave.Ground(inside.x, inside.y);
        Assert.Equal(placed, PlacedLocations.GroundAt(inside));
        Assert.Equal(placed, Predictor()(3, 2));
        // A worker reads what the main thread published.
        float onWorker = float.NaN;
        var worker = new System.Threading.Thread(() => onWorker = Predictor()(3, 2));
        worker.Start();
        worker.Join();
        Assert.Equal(placed, onWorker);
        // Outside every footprint there is no location's ground.
        Assert.Null(PlacedLocations.GroundAt(new Vector2(500f, 500f)));
    }

    /// <summary>A location placed first whose levelling leaves it no landing (no flat, no smoothing) still publishes the ground
    /// it leaves: knowing its ground and having a landing are two facts.</summary>
    [Fact]
    public void APlacedLocationWithNoLandingStillPublishesItsGround()
    {
        var record = new PlacedLocationRecord
        {
            Prefab = "MountainCave02", Centre = new Vector2(0f, 0f), Yaw = 90f, Root = 50f,
            Ops = new List<LevelOp> { new LevelOp(0f, 0f, 0f, 0f, false, 0f, 0f) }, X0 = -60, Z0 = -60, W = 121, H = 121,
        };
        PlacedLocations.Remember(record);
        PlacedLocations.Prepare((x, z) => _world.GetHeight(x, z));
        Assert.False(PlacedLocations.TryGetReady(record.Centre, out _));
        float expected = PlacedLocationGround.Sampler(record.Ground((x, z) => _world.GetHeight(x, z)), record.X0, record.Z0, record.W, record.H)(3f, 2f);
        Assert.Equal(expected, PlacedLocations.GroundAt(new Vector2(3f, 2f)));
    }

    /// <summary>A location at the origin, placed first, that levels its landing 6 m over the level ground: a flat 6 m round a
    /// point 5 m off its centre (at x -5, turned to face +x), smoothed out to 10 m, as a Mountain tower's corner levellings
    /// raise its landing over the slope.</summary>
    private PlacedLocation RaisedLanding()
    {
        var flat = new LevelOp(0f, -5f, 0f, 6f, false, 10f, 3f);
        var record = new PlacedLocationRecord
        {
            Prefab = "StoneTowerRuins05", Centre = new Vector2(0f, 0f), Yaw = 90f, Root = 56f, Ops = new List<LevelOp> { flat },
            X0 = -60, Z0 = -60, W = 121, H = 121,
        };
        Func<float, float, float> ground = PlacedLocationGround.Sampler(record.Ground((x, z) => _world.GetHeight(x, z)), record.X0, record.Z0, record.W, record.H);
        return new PlacedLocation { Record = record, Ground = ground, Shape = PlacedLocationShape.Of(record.Centre, record.Yaw, flat, 0f, null, ground, false, record.Root) };
    }

    /// <summary>The ground a road's staircases are laid out on (RoadStairRuns.GroundAt) is the ground as locations will leave it,
    /// its one owner: on a placed location's raised landing, the landing, not the natural ground under it, from the moment the
    /// location is made ready, whatever the thread read before.</summary>
    [Fact]
    public void AStaircaseIsLaidOutOnThePlacedLocationsLevelledGround()
    {
        var landing = new Vector2(-5f, 0f);
        var smoothed = new Vector2(-13f, 1f);
        // Read before the location is ready: the natural ground.
        Assert.Equal(50f, RoadStairRuns.GroundAt(landing.x, landing.y, _world), 3);
        PlacedLocation location = RaisedLanding();
        PlacedLocations.Ready(location);
        Assert.Equal(56f, location.Ground(landing.x, landing.y), 3);
        Assert.InRange(location.Ground(smoothed.x, smoothed.y), 50.5f, 55.5f);
        foreach (Vector2 p in new[] { landing, smoothed })
        {
            Assert.Equal(location.Ground(p.x, p.y), PlacedLocations.GroundAt(p.x, p.y, _world), 3);
            Assert.Equal(location.Ground(p.x, p.y), RoadStairRuns.GroundAt(p.x, p.y, _world), 3);
        }
        // Outside every footprint, the natural ground.
        Assert.Equal(_world.GetHeight(500f, 500f), RoadStairRuns.GroundAt(500f, 500f, _world), 3);
    }

    /// <summary>A staircase from a road on the level ground 30 m out up onto the location's raised landing
    /// (<see cref="RaisedLanding"/>), 3 m inside its edge: laid out on the stair ground (RoadStairRuns.GroundAt), as a road's
    /// staircase is, or <paramref name="onItsPreload"/> on the location's own ground, as the way onto its landing is.</summary>
    private StairRun OntoTheRaisedLanding(bool onItsPreload = false)
    {
        PlacedLocation location = RaisedLanding();
        PlacedLocations.Ready(location);
        Vector2 from = new(-30f, 0f), to = new(-8f, 0f);
        Func<float, float, float> ground = onItsPreload ? location.Ground : (x, z) => RoadStairRuns.GroundAt(x, z, _world);
        StairPlan plan = StairLayout.Build(from, to, ground, RoadStairRuns.Width, StairKit.Stone, maxBuried: StairCover.ApproachSearch);
        Assert.True(plan.Reached, plan.Reason);
        return StairRun.FromPlan(from, to, RoadStairRuns.Width, plan, StairKit.Stone);
    }

    private static string Said(IEnumerable<StructurePiece> pieces) => string.Join(" ", pieces.Select(p =>
        FormattableString.Invariant($"{p.Prefab}({p.Position.x:F1},{p.Position.y:F1},{p.Position.z:F1})")));

    /// <summary>A road's staircase up to a placed location's raised landing climbs onto it, and no walked piece of it stands
    /// under the ground the location leaves (beyond an approach's cover). On the natural ground it stayed at the road's level,
    /// 6 m down inside the landing.</summary>
    [Fact]
    public void AStaircaseOntoAPlacedLocationsRaisedLandingIsNotBuriedInIt()
    {
        List<StructurePiece> walked = OntoTheRaisedLanding().Pieces.FindAll(p => p.Kind != StructurePieceKind.StairSupport);
        Assert.Equal(56f, walked.Max(RoadStairRuns.Surface), 0);
        var buried = walked.FindAll(p =>
            (PlacedLocations.GroundAt(new Vector2(p.Position.x, p.Position.z)) ?? 50f) - RoadStairRuns.Surface(p) > StairCover.ApproachRefusal);
        Assert.True(buried.Count == 0, "under the landing: " + Said(buried));
    }

    /// <summary>A staircase's kit chooses its supports on the ground as locations will leave it (StairStructures.Dresser): the
    /// top of a staircase onto a placed location's raised landing is not held up as if over the natural ground under it, an
    /// arch or a cornice hung down into the landing (its walls stand down to the finished ground, the landing, either way).
    /// On the natural ground an arch stood 1.4 m into it under the last stair.</summary>
    [Fact]
    public void AStaircaseOntoAPlacedLocationsRaisedLandingIsNotHeldUpThroughIt()
    {
        List<StructurePiece> dressed = StairStructures.Dresser(OntoTheRaisedLanding(onItsPreload: true), _world);
        var through = dressed.FindAll(p => (p.Prefab == "stone_arch" || p.Prefab == "blackmarble_out_1")
            && p.Position.y < (PlacedLocations.GroundAt(new Vector2(p.Position.x, p.Position.z)) ?? 50f) - 0.5f);
        Assert.True(through.Count == 0, "through the landing: " + Said(through));
    }

    /// <summary>A staircase onto a placed location's raised landing dresses the same whether its first zone is built in the
    /// session that generated the network or after the world is saved and loaded again: the locations placed before their roads
    /// are saved with the network and made ready again as it loads. Before, a load forgot them: the staircase's supports
    /// were chosen on the natural ground under the landing, and the finished ground in the location's footprint was unknown.</summary>
    [Fact]
    public void AStaircaseOntoAPlacedLocationDressesTheSameAfterAReload()
    {
        _scope.WithZdos();
        RoadNetworkPersistence.EnsureMetadataInstance();
        RoadSpatialGrid.CommitLevelled(new List<Vector2> { new(100f, 100f), new(108f, 100f), new(116f, 100f) }, new List<float> { 50f, 50f, 50f }, 4f);
        RoadSpatialGrid.FinalizeRoadNetwork();
        // As a generation makes it ready (RoadNetworkGenerator's preparation), from the record its placing remembered.
        PlacedLocations.Remember(RaisedLanding().Record);
        PlacedLocations.Prepare((x, z) => BiomeBlendedHeight.GetBlendedHeight(x, z, _world));
        Assert.True(PlacedLocations.TryGetReady(new Vector2(0f, 0f), out PlacedLocation location));
        Vector2 from = new(-30f, 0f), to = new(-8f, 0f);
        StairPlan plan = StairLayout.Build(from, to, location.Ground, RoadStairRuns.Width, StairKit.Stone, maxBuried: StairCover.ApproachSearch);
        Assert.True(plan.Reached, plan.Reason);
        StairRun run = StairRun.FromPlan(from, to, RoadStairRuns.Width, plan, StairKit.Stone);
        StairStructures.Add(run);
        StairStructures.Settle(run, StructureGround.Vertices(_world));
        string sameSession = Said(StairStructures.Dresser(run, _world));

        RoadNetworkPersistence.SaveGlobalRoadData(new List<(Vector2, string)>(), new List<RoadCrossing>(), new HashSet<Vector2s>());
        // A world unload (RoadLifecycleManager.OnZoneSystemDestroy), then a load: the locations' footprints come again from
        // the world's locations, the network from its save.
        RoadNetworkGenerator.Reset();
        PlacedLocations.Reset();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), Footprint) });
        Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData(), $"the network did not load back: {RoadSpatialGrid.LastReadRefusal}");

        Assert.Equal(sameSession, Said(StairStructures.Dresser(Assert.Single(StairStructures.Runs), _world)));
    }

    /// <summary>A saved network that fails to read part way is not a network: what was read is forgotten, the locations placed
    /// before their roads and their hidden icons with the rest. Kept, the locations stayed ready for roads that were not there and
    /// their icons stayed off the map.</summary>
    [Fact]
    public void ANetworkThatCannotBeReadForgetsTheLocationsItHadRead()
    {
        _scope.WithZdos();
        RoadNetworkPersistence.EnsureMetadataInstance();
        RoadSpatialGrid.CommitLevelled(new List<Vector2> { new(100f, 100f), new(108f, 100f), new(116f, 100f) }, new List<float> { 50f, 50f, 50f }, 4f);
        RoadSpatialGrid.FinalizeRoadNetwork();
        PlacedLocations.Remember(RaisedLanding().Record);
        HiddenLocationIcons.Hide(new Vector3(0f, 56f, 0f));
        RoadNetworkPersistence.SaveGlobalRoadData(new List<(Vector2, string)>(), new List<RoadCrossing>(), new HashSet<Vector2s>());
        RoadNetworkGenerator.Reset();
        PlacedLocations.Reset();
        HiddenLocationIcons.Reset();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), Footprint) });
        try
        {
            // The load's report of what it read throws, after the locations and icons were read.
            BepInEx.Logging.ManualLogSource.ThrowOnNextInfo = true;
            Assert.False(RoadNetworkGenerator.TryLoadGlobalRoadData());
        }
        finally { BepInEx.Logging.ManualLogSource.ThrowOnNextInfo = false; }
        bool hidden = HiddenLocationIcons.IsHidden(new Vector3(0f, 56f, 0f));
        HiddenLocationIcons.Reset();
        Assert.True(RoadNetworkPersistence.LastLoadUnreadable);
        Assert.Equal(0, PlacedLocations.Count);
        Assert.Equal(0, PlacedLocations.ReadyCount);
        Assert.False(hidden);
    }
}
