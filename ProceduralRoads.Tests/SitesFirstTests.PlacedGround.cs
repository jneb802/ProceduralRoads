using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public partial class SitesFirstTests
{
    /// <summary>One reader of the ground a placed place leaves (PlacedSites.GroundAt): the terrain writer reads a place
    /// placed first from its preload, as the finished-ground predictor does, and rebuilds from the saved placement (its
    /// hook) only a place with no preload. Before, the writer rebuilt every place itself.</summary>
    [Fact]
    public void TheWriterReadsAPlacedPlacesGroundFromItsPreloadAsThePredictorDoes()
    {
        Func<Vector2, float?>? hook = RoadTerrainModifier.PlacedGround;
        try
        {
            RoadTerrainModifier.PlacedGround = _ => 999f;
            var inside = new Vector2(3f, 2f);
            // Not placed first: no preload, the hook's rebuild.
            Assert.Null(PlacedSites.GroundAt(inside));
            Assert.Equal(999f, RoadTerrainModifier.PlacedBaseAt(inside));
            PlacedSite cave = Cave();
            PlacedSites.Ready(cave);
            float preload = cave.Ground(inside.x, inside.y);
            Assert.Equal(preload, PlacedSites.GroundAt(inside));
            Assert.Equal(preload, RoadTerrainModifier.PlacedBaseAt(inside));
            Assert.Equal(preload, RoadTerrainModifier.FinishedGround((x, z) => _world.GetHeight(x, z))(3, 2));
            // Outside every footprint there is no place's ground.
            Assert.Null(PlacedSites.GroundAt(new Vector2(500f, 500f)));
        }
        finally { RoadTerrainModifier.PlacedGround = hook; }
    }

    /// <summary>A place at the origin, placed first, that levels its landing 6 m over the level ground: a flat 6 m round a
    /// point 5 m off its centre (at x -5, turned to face +x), smoothed out to 10 m, as a Mountain tower's corner levellings
    /// raise its landing over the slope.</summary>
    private PlacedSite RaisedLanding()
    {
        var flat = new LevelOp(0f, -5f, 0f, 6f, false, 10f, 3f);
        var record = new PlacedSiteRecord
        {
            Prefab = "StoneTowerRuins05", Centre = new Vector2(0f, 0f), Yaw = 90f, Root = 56f, Ops = new List<LevelOp> { flat },
            X0 = -60, Z0 = -60, W = 121, H = 121,
        };
        Func<float, float, float> ground = PlacedSiteGround.Sampler(record.Ground((x, z) => _world.GetHeight(x, z)), record.X0, record.Z0, record.W, record.H);
        return new PlacedSite { Record = record, Ground = ground, Shape = PlacedSiteShape.Of(record.Centre, record.Yaw, flat, 0f, null, ground, false, record.Root) };
    }

    /// <summary>The ground a road's staircases are laid out on (RoadStairRuns.GroundAt) is the ground as places will leave it,
    /// its one owner: on a placed place's raised landing, the landing, not the natural ground under it, from the moment the
    /// place is made ready, whatever the thread read before.</summary>
    [Fact]
    public void AStaircaseIsLaidOutOnThePlacedPlacesLevelledGround()
    {
        var landing = new Vector2(-5f, 0f);
        var smoothed = new Vector2(-13f, 1f);
        // Read before the place is ready: the natural ground.
        Assert.Equal(50f, RoadStairRuns.GroundAt(landing.x, landing.y, _world), 3);
        PlacedSite site = RaisedLanding();
        PlacedSites.Ready(site);
        Assert.Equal(56f, site.Ground(landing.x, landing.y), 3);
        Assert.InRange(site.Ground(smoothed.x, smoothed.y), 50.5f, 55.5f);
        foreach (Vector2 p in new[] { landing, smoothed })
        {
            Assert.Equal(site.Ground(p.x, p.y), PlacedSites.GroundAt(p.x, p.y, _world), 3);
            Assert.Equal(site.Ground(p.x, p.y), RoadStairRuns.GroundAt(p.x, p.y, _world), 3);
        }
        // Outside every footprint, the natural ground.
        Assert.Equal(_world.GetHeight(500f, 500f), RoadStairRuns.GroundAt(500f, 500f, _world), 3);
    }

    /// <summary>A staircase from a road on the level ground 30 m out up onto the place's raised landing
    /// (<see cref="RaisedLanding"/>), 3 m inside its edge: laid out on the stair ground (RoadStairRuns.GroundAt), as a road's
    /// staircase is, or <paramref name="onItsPreload"/> on the place's own ground, as the way onto its landing is.</summary>
    private StairRun OntoTheRaisedLanding(bool onItsPreload = false)
    {
        PlacedSite site = RaisedLanding();
        PlacedSites.Ready(site);
        Vector2 from = new(-30f, 0f), to = new(-8f, 0f);
        Func<float, float, float> ground = onItsPreload ? site.Ground : (x, z) => RoadStairRuns.GroundAt(x, z, _world);
        StairPlan plan = StairLayout.Build(from, to, ground, RoadStairRuns.Width, StairKit.Stone, maxBuried: StairCover.ApproachSearch);
        Assert.True(plan.Reached, plan.Reason);
        return StairRun.FromPlan(from, to, RoadStairRuns.Width, plan, StairKit.Stone);
    }

    private static string Said(IEnumerable<StructurePiece> pieces) => string.Join(" ", pieces.Select(p =>
        FormattableString.Invariant($"{p.Prefab}({p.Position.x:F1},{p.Position.y:F1},{p.Position.z:F1})")));

    /// <summary>A road's staircase up to a placed place's raised landing climbs onto it, and no walked piece of it stands
    /// under the ground the place leaves (beyond an approach's cover). On the natural ground it stayed at the road's level,
    /// 6 m down inside the landing.</summary>
    [Fact]
    public void AStaircaseOntoAPlacedPlacesRaisedLandingIsNotBuriedInIt()
    {
        List<StructurePiece> walked = OntoTheRaisedLanding().Pieces.FindAll(p => p.Kind != StructurePieceKind.StairSupport);
        Assert.Equal(56f, walked.Max(RoadStairRuns.Surface), 0);
        var buried = walked.FindAll(p =>
            (PlacedSites.GroundAt(new Vector2(p.Position.x, p.Position.z)) ?? 50f) - RoadStairRuns.Surface(p) > StairCover.ApproachRefusal);
        Assert.True(buried.Count == 0, "under the landing: " + Said(buried));
    }

    /// <summary>A staircase's kit chooses its supports on the ground as places will leave it (StairStructures.Dresser): the
    /// top of a staircase onto a placed place's raised landing is not held up as if over the natural ground under it, an
    /// arch or a cornice hung down into the landing (its walls stand down to the finished ground, the landing, either way).
    /// On the natural ground an arch stood 1.4 m into it under the last stair.</summary>
    [Fact]
    public void AStaircaseOntoAPlacedPlacesRaisedLandingIsNotHeldUpThroughIt()
    {
        List<StructurePiece> dressed = StairStructures.Dresser(OntoTheRaisedLanding(onItsPreload: true), _world);
        var through = dressed.FindAll(p => (p.Prefab == "stone_arch" || p.Prefab == "blackmarble_out_1")
            && p.Position.y < (PlacedSites.GroundAt(new Vector2(p.Position.x, p.Position.z)) ?? 50f) - 0.5f);
        Assert.True(through.Count == 0, "through the landing: " + Said(through));
    }

    /// <summary>A staircase onto a placed place's raised landing dresses the same whether its first zone is built in the
    /// session that generated the network or after the world is saved and loaded again: the places placed before their roads
    /// are saved with the network and made ready again as it loads. Before, a load forgot them: the staircase's supports
    /// were chosen on the natural ground under the landing, and the finished ground in the place's footprint was unknown.</summary>
    [Fact]
    public void AStaircaseOntoAPlacedPlaceDressesTheSameAfterAReload()
    {
        _scope.WithZdos();
        RoadNetworkPersistence.EnsureMetadataInstance();
        RoadSpatialGrid.CommitLevelled(new List<Vector2> { new(100f, 100f), new(108f, 100f), new(116f, 100f) }, new List<float> { 50f, 50f, 50f }, 4f);
        RoadSpatialGrid.FinalizeRoadNetwork();
        // As a generation makes it ready (RoadNetworkGenerator's preparation), from the record its placing remembered.
        PlacedSites.Remember(RaisedLanding().Record);
        PlacedSites.Prepare((x, z) => BiomeBlendedHeight.GetBlendedHeight(x, z, _world));
        Assert.True(PlacedSites.TryGetReady(new Vector2(0f, 0f), out PlacedSite site));
        Vector2 from = new(-30f, 0f), to = new(-8f, 0f);
        StairPlan plan = StairLayout.Build(from, to, site.Ground, RoadStairRuns.Width, StairKit.Stone, maxBuried: StairCover.ApproachSearch);
        Assert.True(plan.Reached, plan.Reason);
        StairRun run = StairRun.FromPlan(from, to, RoadStairRuns.Width, plan, StairKit.Stone);
        StairStructures.Add(run);
        StairStructures.Settle(run, StructureGround.Vertices(_world));
        string sameSession = Said(StairStructures.Dresser(run, _world));

        RoadNetworkPersistence.SaveGlobalRoadData(new List<(Vector2, string)>(), new List<RoadCrossing>(), new HashSet<Vector2s>());
        // A world unload (RoadLifecycleManager.OnZoneSystemDestroy), then a load: the places' footprints come again from
        // the world's locations, the network from its save.
        RoadNetworkGenerator.Reset();
        PlacedSites.Reset();
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(new Vector2(0f, 0f), Footprint) });
        Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData(), $"the network did not load back: {RoadSpatialGrid.LastReadRefusal}");

        Assert.Equal(sameSession, Said(StairStructures.Dresser(Assert.Single(StairStructures.Runs), _world)));
    }

    /// <summary>A saved network that fails to read part way is not a network: what was read is forgotten, the places placed
    /// before their roads and their hidden icons with the rest. Kept, the places stayed ready for roads that were not there and
    /// their icons stayed off the map.</summary>
    [Fact]
    public void ANetworkThatCannotBeReadForgetsThePlacesItHadRead()
    {
        _scope.WithZdos();
        RoadNetworkPersistence.EnsureMetadataInstance();
        RoadSpatialGrid.CommitLevelled(new List<Vector2> { new(100f, 100f), new(108f, 100f), new(116f, 100f) }, new List<float> { 50f, 50f, 50f }, 4f);
        RoadSpatialGrid.FinalizeRoadNetwork();
        PlacedSites.Remember(RaisedLanding().Record);
        HiddenSiteIcons.Hide(new Vector3(0f, 56f, 0f));
        RoadNetworkPersistence.SaveGlobalRoadData(new List<(Vector2, string)>(), new List<RoadCrossing>(), new HashSet<Vector2s>());
        RoadNetworkGenerator.Reset();
        PlacedSites.Reset();
        HiddenSiteIcons.Reset();
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(new Vector2(0f, 0f), Footprint) });
        try
        {
            // The load's report of what it read throws, after the places and icons were read.
            BepInEx.Logging.ManualLogSource.ThrowOnNextInfo = true;
            Assert.False(RoadNetworkGenerator.TryLoadGlobalRoadData());
        }
        finally { BepInEx.Logging.ManualLogSource.ThrowOnNextInfo = false; }
        bool hidden = HiddenSiteIcons.IsHidden(new Vector3(0f, 56f, 0f));
        HiddenSiteIcons.Reset();
        Assert.True(RoadNetworkPersistence.LastLoadUnreadable);
        Assert.Equal(0, PlacedSites.Count);
        Assert.Equal(0, PlacedSites.ReadyCount);
        Assert.False(hidden);
    }
}
