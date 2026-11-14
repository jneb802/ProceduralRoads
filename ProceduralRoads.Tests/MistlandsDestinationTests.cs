using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>Mistlands destinations are placed first and aimed at their placed landing.</summary>
public class MistlandsDestinationTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private readonly bool _mistlandsDefault = PlacedLocations.PlaceMistlandsDestinations;

    private sealed class OneBiomeWorld : SyntheticWorld
    {
        public Heightmap.Biome Land = Heightmap.Biome.Meadows;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false)
        {
            Heightmap.Biome biome = base.GetBiome(x, z, oceanLevel, waterAlwaysOcean);
            return biome == Heightmap.Biome.Ocean ? biome : Land;
        }
    }

    /// <summary>A ruin turned at random: a floor raised 1.5 m round its centre and a patch raised 3 m 7 m off it, which turns with it, so
    /// two levels (no single platform) and ground off its centre only its placing settles. Outside the Mountains and the
    /// Mistlands, with no door and not turned to face downhill, it is not on the preload list.</summary>
    private static readonly Vector2 RuinAt = new(-220f, 150f);
    private const float RuinRadius = 10f;
    private static readonly IReadOnlyList<LevelOp> RuinOps = new[]
    {
        new LevelOp(0f, 0f, 1.5f, 4f, false, 4f, 3f),
        new LevelOp(0f, 7f, 3f, 3f, false, 5f, 3f),
    };

    private OneBiomeWorld SetUp(Heightmap.Biome biome)
    {
        var world = new OneBiomeWorld { HasRiver = false, HasMountain = false, Land = biome };
        _scope.WithWorld(world);
        _scope.WithZoneSystem();
        TestLocations.Add(ZoneSystem.instance!, new ZoneSystem.LocationInstance
        {
            m_location = new ZoneSystem.ZoneLocation
            {
                m_prefab = new SoftReferenceableAssets.SoftReference<GameObject>("Ruin2"), m_exteriorRadius = RuinRadius,
                m_slopeRotation = false, m_randomRotation = true,
            },
            m_position = new Vector3(RuinAt.x, world.GetHeight(RuinAt.x, RuinAt.y), RuinAt.y),
        });
        LocationLevelling.Source = c => Vector2.Distance(c, RuinAt) < 0.5f ? RuinOps : null;
        RoadNetworkGenerator.Reset();
        PlacedLocations.Reset();
        return world;
    }

    public void Dispose()
    {
        PlacedLocations.PlaceMistlandsDestinations = _mistlandsDefault;
        LocationLevelling.Source = null;
        PlacedLocations.Reset();
        RoadNetworkGenerator.Reset();
        RoadNetworkPersistence.Reset();
        _scope.Dispose();
    }

    private ZoneSystem.LocationInstance Ruin => ZoneSystem.instance!.m_locationInstances[ZoneSystem.GetZone(new Vector3(RuinAt.x, 0f, RuinAt.y))];

    /// <summary>The ruin as the game placed it, turned so its raised patch lies toward +z.</summary>
    private static PlacedLocationRecord PlacedRuin(WorldGenerator world) => new()
    {
        Prefab = "Ruin2", Centre = RuinAt, Yaw = 0f, Root = world.GetHeight(RuinAt.x, RuinAt.y), RockRadius = 0f,
        Ops = new List<LevelOp>(RuinOps), X0 = (int)RuinAt.x - 40, Z0 = (int)RuinAt.y - 40, W = 81, H = 81,
    };

    [Fact]
    public void E2IsRemovedAndOnlyMistlandsExtrasArePlacedFirst()
    {
        var world = SetUp(Heightmap.Biome.Meadows);
        Assert.False(PlacedLocations.NeedsPlacing(Ruin, world));
        Assert.False(PlacedLocations.PlacesFirst(Ruin, world));
        Assert.False(PlacedLocations.PlacesFirst("Ruin2", false, true, Heightmap.Biome.Meadows, RuinOps));
        Assert.True(PlacedLocations.PlacesFirst("Ruin2", false, true, Heightmap.Biome.Mistlands, null));
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        Assert.Null(typeof(PlacedLocations).GetField("PlaceEveryDestination", flags));
        Assert.Null(typeof(PlacedLocations).GetField("PlaceOnlyUnpredictable", flags));
    }

    /// <summary>The default placement and road arrival paths agree on the LocationInstance's placed landing.</summary>
    [Fact]
    public void E3IsOnByDefaultAndRoadsAimAtThePlacedMistlandsLanding()
    {
        var world = SetUp(Heightmap.Biome.Mistlands);
        // Levelling only round its centre: nothing of it turns with it, so in the Mistlands too it is not on the preload list.
        IReadOnlyList<LevelOp> round = new[] { new LevelOp(0f, 0f, 1.5f, 4f, false, 4f, 3f) };
        LocationLevelling.Source = c => Vector2.Distance(c, RuinAt) < 0.5f ? round : null;
        Assert.False(PlacedLocations.NeedsPlacing(Ruin, world));
        Assert.True(PlacedLocations.PlaceMistlandsDestinations);
        Assert.True(PlacedLocations.PlacesFirst(Ruin, world));
        Assert.True(PlacedLocations.PlacesFirst("Ruin2", false, true, Heightmap.Biome.Mistlands, round));
        PlacedLocationRecord record = PlacedRuin(world);
        record.Ops = new List<LevelOp>(round);
        PlacedLocations.Remember(record);
        PlacedLocations.PrepareSteps((x, z) => world.GetHeight(x, z), new RoadPreparation(_ => Enumerable.Empty<bool>()), world).ToList();
        Assert.True(PlacedLocations.TryGetAimed(RuinAt, out _));
        Assert.False(PlacedLocations.Preloaded(RuinAt));
        Assert.True(PlacedLocations.TryGetAimedLanding(RuinAt, out float landing));
        Assert.Equal(landing, RoadNetworkGenerator.ArrivalHeight(RuinAt, RuinRadius, RuinAt + new Vector2(0f, 30f))!.Value, 3);
    }

    /// <summary>Placed first for the switch, the LocationInstance is not on the preload list and has no landing to be carried onto (its road
    /// stops at its edge), but its road's arrival height is its landing as placed (else the ground its placing left at its edge):
    /// off the ground predicted at its edge from its levelling round its centre, which has no single level.</summary>
    [Fact]
    public void AMistlandsDestinationUsesItsPlacedGroundWhenItsLevelsTurn()
    {
        var world = SetUp(Heightmap.Biome.Mistlands);
        Ruin.m_location.m_randomRotation = false;
        Func<float, float, float> natural = (x, z) => world.GetHeight(x, z);
        Vector2 from = RuinAt + new Vector2(0f, 30f);
        float predicted = RoadNetworkGenerator.ArrivalHeight(RuinAt, RuinRadius, from)!.Value;

        PlacedLocationRecord record = PlacedRuin(world);
        PlacedLocations.Remember(record);
        PlacedLocations.PrepareSteps(natural, new RoadPreparation(_ => Enumerable.Empty<bool>()), world).ToList();

        Assert.True(PlacedLocations.TryGetAimed(RuinAt, out _));
        Assert.False(PlacedLocations.Preloaded(RuinAt));
        Assert.False(PlacedLocations.TryGetReady(RuinAt, out _));
        Vector2 edge = RuinAt + new Vector2(0f, RoadLocationProtection.ArrivalRadiusAt(RuinAt, RuinRadius));
        Func<float, float, float> placed = PlacedLocationGround.Sampler(record.Ground((x, z) => natural(x, z)), record.X0, record.Z0, record.W, record.H);
        float real = placed(edge.x, edge.y);
        float aimed = RoadNetworkGenerator.ArrivalHeight(RuinAt, RuinRadius, from)!.Value;
        // With a landing (here its flat round its centre) the road at its edge comes level with the landing as placed; with
        // none, with the ground its placing left at its edge.
        Assert.Equal(PlacedLocations.TryGetAimedLanding(RuinAt, out float landing) ? landing : real, aimed, 3);
        Assert.True(Mathf.Abs(aimed - predicted) > 0.25f, $"aimed {aimed:F2} m, predicted {predicted:F2} m: the placing changed nothing");
    }

    /// <summary>Switch off, the same record is on the preload list as today (every placed record is): nothing is aimed only.</summary>
    [Fact]
    public void SwitchOffAPlacedRecordIsPreloadedAsBefore()
    {
        var world = SetUp(Heightmap.Biome.Meadows);
        PlacedLocations.PlaceMistlandsDestinations = false;
        PlacedLocations.Remember(PlacedRuin(world));
        PlacedLocations.PrepareSteps((x, z) => world.GetHeight(x, z), new RoadPreparation(_ => Enumerable.Empty<bool>()), world).ToList();
        Assert.False(PlacedLocations.TryGetAimed(RuinAt, out _));
        Assert.True(PlacedLocations.Preloaded(RuinAt));
    }
    [Fact]
    public void E3KeepsAnAlreadyPlacedLocationOutsideMistlandsOnThePreloadList()
    {
        var world = SetUp(Heightmap.Biome.Meadows);
        Assert.True(PlacedLocations.PlaceMistlandsDestinations);
        PlacedLocations.Remember(PlacedRuin(world));
        PlacedLocations.PrepareSteps((x, z) => world.GetHeight(x, z), new RoadPreparation(_ => Enumerable.Empty<bool>()), world).ToList();
        Assert.False(PlacedLocations.TryGetAimed(RuinAt, out _));
        Assert.True(PlacedLocations.Preloaded(RuinAt));
    }

    [Theory]
    [InlineData(Heightmap.Biome.Mountain)]
    [InlineData(Heightmap.Biome.Mistlands)]
    public void ReplacementsSkipPreloadAndMistlandsLocationsEvenWhenAlreadyPlaced(Heightmap.Biome biome)
    {
        var world = SetUp(biome);
        PlacedLocations.PlaceMistlandsDestinations = true;
        LocationLevelling.Source = _ => null;
        var record = PlacedRuin(world);
        record.Ops.Clear();
        PlacedLocations.Remember(record);
        PlacedLocations.PrepareSteps((x, z) => world.GetHeight(x, z), new RoadPreparation(_ => Enumerable.Empty<bool>()), world).ToList();
        Assert.Equal(biome == Heightmap.Biome.Mountain, PlacedLocations.NeedsPlacing(Ruin, world));
        Assert.True(PlacedLocations.PlacesFirst(Ruin, world));
        PlacedLocations.FindNotPlaced(Array.Empty<Vector2>(), world, new[] { RuinAt });
        Assert.False(PlacedLocations.NotPlaced(RuinAt));
        Assert.True(PlacedLocations.NoRoadAsReplacement(RuinAt));

        var blocked = (name: "Ruin2", position: Ruin.m_position, radius: RuinRadius);
        var coast = new Vector3(120f, world.GetHeight(120f, 0f), 0f);
        var island = new Island { ApproxArea = 9 * Side * Side, CoastalLandings = new List<Vector3> { coast } };
        var select = typeof(RoadNetworkGenerator).GetMethod("SelectReplacements", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var picks = (List<(string name, Vector3 position, float radius)>)select.Invoke(null, new object[]
        {
            island, new List<(string, Vector3, float)> { blocked }, Array.Empty<(string, Vector3, float)>(), new HashSet<(float, float)>(), 2,
        })!;
        var replacement = Assert.Single(picks);
        Assert.Equal(RoadCoastalLandings.Name, replacement.name);
        Assert.Equal(coast, replacement.position);
    }

    private const float Side = 300f;

    [Theory]
    [InlineData(Heightmap.Biome.Meadows)]
    [InlineData(Heightmap.Biome.Mistlands)]
    public void GenerationAndSavedNetworkReloadKeepTheSamePreloadAdmissionAndLandingAim(Heightmap.Biome biome)
    {
        var world = SetUp(biome);
        PlacedLocations.PlaceMistlandsDestinations = true;
        Ruin.m_location.m_randomRotation = false;
        _scope.WithZdos().WithNetwork(server: true);
        PlacedLocations.Remember(PlacedRuin(world));
        PlacedLocations.PrepareSteps((x, z) => BiomeBlendedHeight.GetBlendedHeight(x, z, world),
            new RoadPreparation(_ => Enumerable.Empty<bool>()), world).ToList();
        var before = Admission();
        Assert.Equal(biome == Heightmap.Biome.Meadows, before.preloaded);
        Assert.Equal(biome == Heightmap.Biome.Mistlands, before.aimed);
        RoadNetworkPersistence.EnsureMetadataInstance();
        RoadSpatialGrid.CommitLevelled(new List<Vector2> { new(100f, 100f), new(108f, 100f), new(116f, 100f) },
            new List<float> { 50f, 50f, 50f }, 4f);
        RoadSpatialGrid.FinalizeRoadNetwork();
        RoadNetworkPersistence.SaveGlobalRoadData(new List<(Vector2, string)>(), new List<RoadCrossing>(), new HashSet<Vector2s>());

        RoadNetworkGenerator.Reset();
        PlacedLocations.Reset();
        Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData(), RoadSpatialGrid.LastReadRefusal);
        Assert.Equal(before, Admission());

        (bool preloaded, bool aimed, bool ready, float? arrival) Admission() =>
            (PlacedLocations.Preloaded(RuinAt), PlacedLocations.TryGetAimed(RuinAt, out _), PlacedLocations.TryGetReady(RuinAt, out _),
                RoadNetworkGenerator.ArrivalHeight(RuinAt, RuinRadius, RuinAt + new Vector2(0f, 30f)));
    }

    /// <summary>A saved network reloaded before the world's LocationInstances are indexed (none listed yet): each placed record
    /// keeps the admission its generation gave it, decided from the record (its prefab's registered ZoneLocation, its biome, its
    /// kept levelling), never put on the preload list because its LocationInstance could not be looked up.</summary>
    [Theory]
    [InlineData(Heightmap.Biome.Meadows)]
    [InlineData(Heightmap.Biome.Mistlands)]
    public void AReloadBeforeTheLocationInstancesAreIndexedKeepsTheGenerationsAdmission(Heightmap.Biome biome)
    {
        var world = SetUp(biome);
        Ruin.m_location.m_randomRotation = false;
        _scope.WithZdos().WithNetwork(server: true);
        PlacedLocations.Remember(PlacedRuin(world));
        PlacedLocations.PrepareSteps((x, z) => BiomeBlendedHeight.GetBlendedHeight(x, z, world),
            new RoadPreparation(_ => Enumerable.Empty<bool>()), world).ToList();
        var before = (PlacedLocations.Preloaded(RuinAt), PlacedLocations.TryGetAimed(RuinAt, out _), PlacedLocations.TryGetReady(RuinAt, out _));
        Assert.Equal((biome == Heightmap.Biome.Meadows, biome == Heightmap.Biome.Mistlands), (before.Item1, before.Item2));
        RoadNetworkPersistence.EnsureMetadataInstance();
        RoadSpatialGrid.CommitLevelled(new List<Vector2> { new(100f, 100f), new(108f, 100f), new(116f, 100f) },
            new List<float> { 50f, 50f, 50f }, 4f);
        RoadSpatialGrid.FinalizeRoadNetwork();
        RoadNetworkPersistence.SaveGlobalRoadData(new List<(Vector2, string)>(), new List<RoadCrossing>(), new HashSet<Vector2s>());

        RoadNetworkGenerator.Reset();
        PlacedLocations.Reset();
        ZoneSystem.instance!.m_locationInstances.Clear();
        Assert.False(LocationPrefabLevelling.LocationAt(RuinAt, out _, out _));
        Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData(), RoadSpatialGrid.LastReadRefusal);
        Assert.Equal(before, (PlacedLocations.Preloaded(RuinAt), PlacedLocations.TryGetAimed(RuinAt, out _), PlacedLocations.TryGetReady(RuinAt, out _)));
    }

}
