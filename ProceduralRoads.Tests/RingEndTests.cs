using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

[Collection("RoadStatics")]
public sealed class RingEndTests : IDisposable
{
    private sealed class LevelWorld : WorldGenerator
    {
        public override float GetHeight(float x, float z) => Ground(x, z);
        public Heightmap.Biome Biome = Heightmap.Biome.BlackForest;
        public Func<float, float, float> Ground = (_, _) => 50f;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Biome;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    private readonly LevelWorld _world = new();
    private readonly ValheimWorldScope _scope = new();
    private readonly IDisposable _source = StaticOverride.Keep(() => LocationLevelling.Source);
    private readonly IDisposable _root = StaticOverride.Keep(() => LocationLevelling.PlacementHeightSource);
    private readonly IDisposable _grade = GradeCap.At(0.35f);
    private static readonly Vector2 Centre = new(0f, 0f);
    private const float Radius = 20f;
    private const string Refusal = "ring end off its landing level";

    public RingEndTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        PlacedLocations.Reset();
        _scope.WithWorld(_world);
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(Centre, Radius) });
    }

    public void Dispose()
    {
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.Reset();
        PlacedLocations.Reset();
        RoadLocationProtection.Reset();
        _grade.Dispose();
        _root.Dispose();
        _source.Dispose();
        _scope.Dispose();
    }

    private PlacedLocation? Place(float offset, bool preloaded, bool fallback = true, float landingOffset = 0f)
    {
        float landing = 50f - offset;
        var flat = new LevelOp(0f, landingOffset, 0f, 12f, false);
        LocationLevelling.Source = c => c == Centre ? new[] { flat } : null;
        LocationLevelling.PlacementHeightSource = c => c == Centre ? landing : null;
        // Exercise the fallback that formerly counted a ring end at any height.
        if (fallback) RoadNetworkGenerator.RestoreReachedLocations(new[] { (Centre, true, false) });
        if (!preloaded) return null;
        Func<float, float, float> ground = (_, _) => landing;
        var location = new PlacedLocation
        {
            Record = new PlacedLocationRecord { Centre = Centre, Prefab = "MountainCave02", Root = landing, Ops = new List<LevelOp> { flat } },
            Ground = ground,
            Shape = PlacedLocationShape.Of(Centre, 0f, flat, 0f, null, ground, root: landing),
        };
        PlacedLocations.Ready(location);
        return location;
    }

    private RoadNetworkGenerator.PlannedRoad RingRoad(PlacedLocation? location)
    {
        float ring = RoadNetworkGenerator.KeepOutEdge(Centre, Radius, 4f);
        var path = new List<Vector2> { new(0f, 120f), new(0f, ring) };
        var plan = new RoadSpatialGrid.PlannedPath(path, new List<float> { 50f, 50f },
            new List<RoadPointDebugInfo> { default, default }, 4f, 120f - ring, false);
        var road = new RoadNetworkGenerator.PlannedRoad
        {
            Path = path, Plans = new List<RoadSpatialGrid.PlannedPath> { plan }, EndCenter = Centre,
            Ends = new RoadNetworkGenerator.LocationEndContext(0f, Radius, false, true, null, location, 4f, 0f, ring),
        };
        road.Ends.NoEntry[1] = "water";
        return road;
    }

    // Each caller is a separate case, so a refusal failure cannot hide a reached or trial failure.
    [Theory]
    [InlineData("refusal", true, 16f, false)]
    [InlineData("reached", true, 16f, false)]
    [InlineData("endReached", true, 16f, false)]
    [InlineData("refusal", true, -16f, false)]
    [InlineData("reached", true, -16f, false)]
    [InlineData("endReached", true, -16f, false)]
    [InlineData("refusal", true, 3f, true)]
    [InlineData("reached", true, 3f, true)]
    [InlineData("endReached", true, 3f, true)]
    [InlineData("refusal", true, -3f, true)]
    [InlineData("reached", true, -3f, true)]
    [InlineData("endReached", true, -3f, true)]
    [InlineData("refusal", true, 3.01f, false)]
    [InlineData("reached", true, 3.01f, false)]
    [InlineData("endReached", true, 3.01f, false)]
    [InlineData("refusal", false, 16f, false)]
    [InlineData("reached", false, 16f, false)]
    [InlineData("endReached", false, 16f, false)]
    [InlineData("refusal", false, 3f, true)]
    [InlineData("reached", false, 3f, true)]
    [InlineData("endReached", false, 3f, true)]
    public void EachRingCallerUsesTheLandingHeight(string caller, bool preloaded, float offset, bool accepted)
    {
        var road = RingRoad(Place(offset, preloaded));
        AssertCaller(caller, road, accepted);
    }

    private static void AssertCaller(string caller, RoadNetworkGenerator.PlannedRoad road, bool accepted)
    {
        switch (caller)
        {
            case "refusal":
                Assert.Equal(accepted ? null : Refusal, RoadNetworkGenerator.RoadRefusal(road, out _)?.Reason);
                break;
            case "reached":
                RoadSpatialGrid.Commit(road.Plans[0]);
                Assert.Equal(accepted, RoadNetworkGenerator.Reached(Centre, Radius));
                break;
            case "endReached":
                RoadSpatialGrid.Commit(road.Plans[0]);
                Assert.Equal(accepted, RoadNetworkGenerator.EndReached(Centre, Radius));
                break;
            case "endRefusal":
                Assert.Equal(accepted ? null : Refusal, RoadNetworkGenerator.EndNotLevel(road.Path, road.Plans,
                    road.Stairs, road.Approaches, road.StartCenter, 0f, Centre, Radius, false, true)?.refusal);
                break;
            default: throw new ArgumentException(caller);
        }
    }

    private RoadPathfinder Finder()
    {
        var finder = new RoadPathfinder(_world) { Stairs = true, Meander = 0f, TerrainVarianceThreshold = 1000f };
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, finder);
        return finder;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GenerationRefusesARingEndSixteenMetresAboveItsLanding(bool preloaded)
    {
        Place(16f, preloaded);
        Finder();
        Assert.False(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, 120f), 0f, Centre, Radius, 4f, "ring arrival"));
        Assert.Equal(Refusal, RoadSpatialGrid.LastRefusal);
        var reasons = (IDictionary<string, int>)typeof(RoadNetworkGenerator)
            .GetField("m_refusalReasons", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Assert.True(reasons.TryGetValue(Refusal, out int count) && count > 0, "the ring refusal has no counter");
        Assert.Empty(RoadSpatialGrid.GetRoadPointsNearPosition(Vector3.zero, 160f));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnApproachTrialRefusesARingEndSixteenMetresAboveItsLanding(bool preloaded)
    {
        Place(16f, preloaded);
        Assert.False(Trial(Finder(), preloaded));
    }

    private static bool Trial(RoadPathfinder finder, bool preloaded)
    {
        Vector2 start = new(0f, 120f);
        float ring = RoadNetworkGenerator.KeepOutEdge(Centre, Radius, 4f);
        return RoadNetworkGenerator.TrialBuilds(finder, new List<Vector2> { start, new(0f, ring) },
            start, 0f, Centre, ring, false, preloaded, false, false, 4f, 0f, Radius);
    }

    [Theory]
    [InlineData("refusal", Heightmap.Biome.Mistlands, true)]
    [InlineData("reached", Heightmap.Biome.Mistlands, true)]
    [InlineData("endReached", Heightmap.Biome.Mistlands, true)]
    [InlineData("endRefusal", Heightmap.Biome.Mistlands, true)]
    [InlineData("generation", Heightmap.Biome.Mistlands, true)]
    [InlineData("trial", Heightmap.Biome.Mistlands, true)]
    [InlineData("refusal", Heightmap.Biome.BlackForest, false)]
    [InlineData("reached", Heightmap.Biome.BlackForest, false)]
    [InlineData("endReached", Heightmap.Biome.BlackForest, false)]
    [InlineData("endRefusal", Heightmap.Biome.BlackForest, false)]
    [InlineData("generation", Heightmap.Biome.BlackForest, false)]
    [InlineData("trial", Heightmap.Biome.BlackForest, false)]
    public void MistlandsExemptsTheSameSixteenMetreRingArrivalOnEveryCaller(string caller, Heightmap.Biome biome, bool accepted)
    {
        _world.Biome = biome;
        var road = RingRoad(Place(16f, true, fallback: false));
        if (caller == "generation")
            {
            Finder();
            Assert.Equal(accepted, RoadNetworkGenerator.GenerateRoad(road.Path[0], 0f, Centre, Radius, 4f, "ring arrival"));
        }
        else if (caller == "trial") Assert.Equal(accepted, Trial(Finder(), true));
        else AssertCaller(caller, road, accepted);
    }

    [Theory]
    [InlineData("refusal")]
    [InlineData("reached")]
    [InlineData("endReached")]
    [InlineData("endRefusal")]
    public void AnUnloadedLocationUsesArrivalSideEdgeGround(string caller)
    {
        Place(16f, preloaded: false);
        LocationLevelling.Source = _ => null;
        LocationLevelling.PlacementHeightSource = _ => null;
        _world.Ground = (_, z) => z > 0f && z <= Radius ? 34f : 50f;
        var road = RingRoad(null);
        Assert.False(PlacedLocations.TryGetReady(Centre, out _));
        Assert.Null(LocationLevelling.ApproachHeight(50f, LocationLevelling.OpsAt(Centre)));
        Assert.Equal(50f, _world.GetHeight(0f, 0f));
        Assert.Equal(34f, RoadNetworkGenerator.ArrivalHeight(Centre, Radius, road.Path[1]));
        Assert.Equal(50f, RoadNetworkGenerator.ArrivalHeight(Centre, Radius, new Vector2(0f, -120f)));
        AssertCaller(caller, road, false);
    }

    [Theory]
    [InlineData("refusal")]
    [InlineData("reached")]
    [InlineData("endReached")]
    [InlineData("endRefusal")]
    public void NetworkNearTheLandingHasNotEnteredIt(string caller)
    {
        PlacedLocation location = Place(16f, true, landingOffset: 11f)!;
        var road = RingRoad(location);
        Assert.InRange(Vector2.Distance(location.Shape.Landing, road.Path[1]), 0f, RoadNetworkGenerator.LandingReach);
        Assert.False(PlacedLocations.Landed(Centre));
        Assert.False(RoadNetworkGenerator.BehindRidge(Centre, Radius, road.Path[1], 50f));
        AssertCaller(caller, road, false);
    }

    [Fact]
    public void LandingProximityAndABuiltEntryHaveDifferentReachedVerdicts()
    {
        PlacedLocation location = Place(16f, true, landingOffset: 11f)!;
        var road = RingRoad(location);
        Assert.Equal(Refusal, RoadNetworkGenerator.RoadRefusal(road, out _)?.Reason);
        RoadSpatialGrid.Commit(road.Plans[0]);
        Assert.False(RoadNetworkGenerator.Reached(Centre, Radius));
        PlacedLocations.MarkLanded(Centre);
        Assert.True(RoadNetworkGenerator.Reached(Centre, Radius));
    }

    [Theory]
    [InlineData("refusal")]
    [InlineData("reached")]
    [InlineData("endReached")]
    [InlineData("endRefusal")]
    [InlineData("generation")]
    [InlineData("trial")]
    public void AnUnloadedMistlandsFallbackKeepsTheSameHeightExemption(string caller)
    {
        _world.Biome = Heightmap.Biome.Mistlands;
        var road = RingRoad(Place(16f, preloaded: false));
        if (caller == "generation")
        {
            Finder();
            Assert.True(RoadNetworkGenerator.GenerateRoad(road.Path[0], 0f, Centre, Radius, 4f, "ring arrival"));
        }
        else if (caller == "trial") Assert.True(Trial(Finder(), false));
        else AssertCaller(caller, road, true);
    }

}
