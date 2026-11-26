using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The order the ways onto a placed location's landing are tried in (RoadNetworkGenerator.EntryRoadsFirst, asked by PlanEntryTo):
/// onto a measured entrance every start's road before any start's staircase; onto any other landing each start in turn, its road then its staircase.
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>The ground, the pathfinder's and the placed location's alike: level at 50, rising <c>slope</c> in 1 from 16 m
    /// out of the centre toward it to <c>top</c> over it, flat on top inside 16 m.</summary>
    private sealed class MoundWorld : WorldGenerator
    {
        public Func<float, float, float> Ground = (_, _) => 50f;
        public Heightmap.Biome Biome = Heightmap.Biome.Mountain;
        public override float GetHeight(float x, float z) => Ground(x, z);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Biome;
        public override int GetSeed() => 77;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    /// <summary>A Dvergr guard tower (its entrance 13.5 m out of its front, facing out toward the road's start) on top of a
    /// mound, its ring (20 m) on the mound's side: from the road's end there, too steep for a road onto its landing and a
    /// staircase fits; from 12 or 24 m back along the road a road onto it fits. With <paramref name="withEntrance"/> its measured
    /// entrance and its fallback, else its one landing (a location with no entrances).</summary>
    private PlacedLocation TowerOnAMound(Heightmap.Biome biome, float slope, float top, bool withEntrance)
    {
        const string tower = "Mistlands_GuardTower1_ruined_new";
        var world = new MoundWorld { Biome = biome };
        world.Ground = (x, z) => 50f + Mathf.Clamp(top - slope * (new Vector2(x, z).magnitude - 16f), 0f, top);
        _scope.WithWorld(world);
        RoadNetworkGenerator.Reset();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), Footprint) });
        PlacedLocations.Reset();
        var flat = new List<LevelOp> { PlacedLocationShape.DoorFlat };
        var record = new PlacedLocationRecord
        {
            Prefab = tower, Centre = new Vector2(0f, 0f), Yaw = 0f, Root = 50f + top, RockRadius = 0f,
            Ops = flat, X0 = -60, Z0 = -60, W = 121, H = 121,
        };
        Func<float, float, float> ground = world.Ground;
        PlacedLocationShape fallback = PlacedLocationShape.Of(record.Centre, record.Yaw, PlacedLocationShape.DoorFlat, 0f, null, ground, false, record.Root, tower);
        PlacedLocationShape entrance = PlacedLocationShape.Of(record.Centre, record.Yaw, PlacedLocationShape.DoorFlat, 0f, null, ground, false, record.Root,
            tower, entrance: 0);
        var location = new PlacedLocation
        {
            Record = record, Ground = ground, Shape = withEntrance ? entrance : fallback, Fallback = withEntrance ? fallback : null,
            Entrances = withEntrance ? new[] { entrance } : Array.Empty<PlacedLocationShape>(),
        };
        PlacedLocations.Ready(location);
        LocationLevelling.Source = c => c.magnitude < 0.5f ? flat : null;
        SetPathfinder(new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        return location;
    }

    private void LandsByRoad(PlacedLocation tower)
    {
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, 120f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the tower"),
            "last refusal: " + (RoadSpatialGrid.LastRefusal ?? "none"));
        Assert.True(PlacedLocations.Landed(tower.Centre));
        Assert.Empty(StairStructures.Runs);
    }

    /// <summary>A tower on a low mound (2 m) with a measured entrance: the road onto the entrance's landing, planned as the road's own last
    /// stretch from its end or from 12 or 24 m back along it, is tried before any staircase, outside the Mountains and in them: it is
    /// laid, no staircase (each start in turn, its road then its staircase, a staircase at the road's end was laid).</summary>
    [Theory]
    [InlineData(Heightmap.Biome.Mistlands)]
    [InlineData(Heightmap.Biome.Mountain)]
    public void ARoadStartBeatsAStaircaseStartOntoAMeasuredEntrance(Heightmap.Biome biome)
    {
        PlacedLocation tower = TowerOnAMound(biome, 0.6f, 2f, withEntrance: true);
        LandsByRoad(tower);
        Assert.True(RoadNetworkGenerator.EntryRoadsFirst(tower.Entrances[0]));
    }

    /// <summary>A Mountains location with no entrances, the same mound shape (a staircase fits from the road's end, a road from
    /// further back): every target now takes the arrival ladder's order, every start's road before any start's staircase ("level,
    /// then stairs", RoadNetworkGenerator.EntryRoadsFirst), so the road from further back lands it and no staircase is laid.</summary>
    [Fact]
    public void AMountainsArrivalOntoALandingThatIsNoEntranceTriesEveryRoadFirst()
    {
        PlacedLocation tower = TowerOnAMound(Heightmap.Biome.Mountain, 1.0f, 8f, withEntrance: false);
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, 120f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the tower"),
            "last refusal: " + (RoadSpatialGrid.LastRefusal ?? "none"));
        Assert.True(PlacedLocations.Landed(tower.Centre));
        Assert.Empty(StairStructures.Runs);
        Assert.True(RoadNetworkGenerator.EntryRoadsFirst(tower.Shape));
    }
}
