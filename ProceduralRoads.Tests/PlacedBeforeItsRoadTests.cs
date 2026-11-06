using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A place whose landing turns with it (<see cref="PlacedSites.NeedsPlacing(string, bool, bool, Heightmap.Biome, IReadOnlyList{LevelOp}?)"/>)
/// gets a road only where it was placed before its roads (<see cref="PlacedSites.NotPlaced"/>): one that missed its
/// placing gets none, in any biome, and is counted a destination not reached. On the synthetic one-island world, a frost cave
/// turned to face downhill among the standard places.
/// </summary>
public class PlacedBeforeItsRoadTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    /// <summary>The synthetic island with every piece of land in one biome.</summary>
    private sealed class OneBiomeWorld : SyntheticWorld
    {
        public Heightmap.Biome Land = Heightmap.Biome.Meadows;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false)
        {
            Heightmap.Biome biome = base.GetBiome(x, z, oceanLevel, waterAlwaysOcean);
            return biome == Heightmap.Biome.Ocean ? biome : Land;
        }
    }

    private static readonly Vector2 CaveAt = new(-220f, 150f);
    private const float CaveRadius = 10f;
    /// <summary>MountainCave02's levelling: a floor 4 m across, 4.75 m behind the centre and 4.09 m down, smoothed out to 7 m.</summary>
    private static readonly IReadOnlyList<LevelOp> CaveFloor = new[] { new LevelOp(-0.06f, -4.75f, -4.09f, 4f, false, 7f, 3f) };

    private OneBiomeWorld SetUp(Heightmap.Biome biome)
    {
        var world = new OneBiomeWorld { HasRiver = false, HasMountain = false, Land = biome };
        _scope.WithWorld(world);
        _scope.WithZoneSystem();
        var zones = ZoneSystem.instance!;
        foreach (var (name, x, z, radius, slope) in new[]
        {
            ("StartTemple", 0f, 0f, 25f, false),
            ("Eikthyrnir", 260f, 40f, 10f, false),
            ("MountainCave02", CaveAt.x, CaveAt.y, CaveRadius, true),
            ("Crypt4", 120f, -260f, 18f, false),
        })
        {
            TestLocations.Add(zones, new ZoneSystem.LocationInstance
            {
                m_location = new ZoneSystem.ZoneLocation
                {
                    m_prefab = new SoftReferenceableAssets.SoftReference<GameObject>(name), m_exteriorRadius = radius,
                    m_slopeRotation = slope, m_randomRotation = !slope,
                },
                m_position = new Vector3(x, world.GetHeight(x, z), z),
            });
        }
        bool AtCave(Vector2 c) => Vector2.Distance(c, CaveAt) < 0.5f;
        LocationLevelling.Source = c => AtCave(c) ? CaveFloor : null;
        LocationLevelling.FacesDownhillSource = AtCave;
        _scope.WithZdos().WithNetwork(server: true);
        RoadNetworkGenerator.Reset();
        PlacedSites.Reset();
        return world;
    }

    public void Dispose()
    {
        LocationLevelling.Source = null;
        LocationLevelling.FacesDownhillSource = null;
        PlacedSites.Reset();
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    /// <summary>The cave as the game placed it before its roads: turned to face +x, on the ground at its centre.</summary>
    private static PlacedSiteRecord PlacedCave(WorldGenerator world) => new()
    {
        Prefab = "MountainCave02", Centre = CaveAt, Yaw = 90f, Root = world.GetHeight(CaveAt.x, CaveAt.y), RockRadius = 0f,
        Ops = new List<LevelOp>(CaveFloor), X0 = (int)CaveAt.x - 60, Z0 = (int)CaveAt.y - 60, W = 121, H = 121,
    };

    /// <summary>The other two destinations as the game placed them before their roads: neither levels anything (the
    /// test's prefabs carry no levelling), so each is placed with no flat and reached at its ring (PlacedSites.FlatlessPlaced).
    /// In the Mountains every destination waits for its placing (PlacedSites.NeedsPlacing).</summary>
    private static void PlaceTheOthers(WorldGenerator world)
    {
        foreach (var (name, x, z) in new[] { ("Eikthyrnir", 260f, 40f), ("Crypt4", 120f, -260f) })
            PlacedSites.Remember(new PlacedSiteRecord
            {
                Prefab = name, Centre = new Vector2(x, z), Yaw = 0f, Root = world.GetHeight(x, z), RockRadius = 0f,
                X0 = (int)x - 60, Z0 = (int)z - 60, W = 121, H = 121,
            });
    }

    private static int Counter(string name) =>
        (int)typeof(RoadNetworkGenerator).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    /// <summary>Road or a staircase's piece within <paramref name="reach"/> of the cave's centre.</summary>
    private static bool NetworkNearCave(float reach) =>
        RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(CaveAt.x, 0f, CaveAt.y), reach).Any(p => p.IsRoad && Vector2.Distance(p.p, CaveAt) <= reach)
        || StairStructures.PiecesNear(new Vector3(CaveAt.x, 0f, CaveAt.y), reach).Count > 0;

    /// <summary>A place whose landing turns with it and that was not placed before its roads (here: a generation that places
    /// nothing first) gets no road at all, in the Meadows as in the Mountains: no road ends at its ring and no staircase is
    /// predicted onto it, and it is a destination not reached. Before, outside the Mountains its road ended at its ring and
    /// counted it reached ("keeps the outskirts end"); in the Mountains such an end was refused only where it was not level
    /// with the place.</summary>
    [Theory]
    [InlineData(Heightmap.Biome.Meadows)]
    [InlineData(Heightmap.Biome.Mountain)]
    public void APlaceWithALandingNotPlacedBeforeItsRoadGetsNoRoad(Heightmap.Biome biome)
    {
        var world = SetUp(biome);
        PlaceTheOthers(world);

        RoadNetworkGenerator.GenerateRoads(force: true);

        Assert.True(RoadNetworkGenerator.RoadsAvailable);
        Assert.False(RoadNetworkGenerator.Reached(new Vector3(CaveAt.x, 0f, CaveAt.y), CaveRadius, "MountainCave02"));
        Assert.False(NetworkNearCave(40f), "a road or staircase was laid to the cave");
        // Still one of the destinations, counted not reached; the others are reached.
        int selected = Counter("m_destinationsSelected"), connected = Counter("m_destinationsConnected");
        Assert.Equal(3, selected);
        Assert.Equal(2, connected);
    }

    /// <summary>The same cave placed before its roads keeps its road, onto its landing: reached, with every destination.</summary>
    [Theory]
    [InlineData(Heightmap.Biome.Meadows)]
    [InlineData(Heightmap.Biome.Mountain)]
    public void APlaceWithALandingPlacedBeforeItsRoadKeepsItsRoad(Heightmap.Biome biome)
    {
        var world = SetUp(biome);
        PlacedSites.Remember(PlacedCave(world));
        PlaceTheOthers(world);

        RoadNetworkGenerator.GenerateRoads(force: true);

        Assert.True(PlacedSites.TryGetReady(CaveAt, out _), "the placed cave was not made ready for its roads");
        Assert.True(RoadNetworkGenerator.Reached(new Vector3(CaveAt.x, 0f, CaveAt.y), CaveRadius, "MountainCave02"),
            "last refusal: " + (RoadSpatialGrid.LastRefusal ?? "none"));
        Assert.Equal(3, Counter("m_destinationsSelected"));
        Assert.Equal(3, Counter("m_destinationsConnected"));
    }

    /// <summary>The places the load places first and the places a road may go to with a landing are the same list: a place
    /// turned to face downhill with its floor off its centre waits for its placing, and so does a crypt turned at random in
    /// the Meadows (its door turns with it); a boss altar with no levelling off its centre does not.</summary>
    [Fact]
    public void ThePlacesPlacedFirstAreThePlacesThatWaitForIt()
    {
        var world = SetUp(Heightmap.Biome.Meadows);
        var zones = ZoneSystem.instance!;
        ZoneSystem.LocationInstance At(float x, float z) => zones.m_locationInstances[ZoneSystem.GetZone(new Vector3(x, 0f, z))];

        var crypt = new Vector2(120f, -260f);
        var eikthyr = new Vector2(260f, 40f);
        Assert.True(PlacedSites.NeedsPlacing(At(CaveAt.x, CaveAt.y), world));
        Assert.True(PlacedSites.NeedsPlacing(At(crypt.x, crypt.y), world));
        PlacedSites.FindNotPlaced(new[] { CaveAt, crypt, eikthyr }, world);
        Assert.True(PlacedSites.NotPlaced(CaveAt));
        Assert.True(PlacedSites.NotPlaced(crypt));
        Assert.False(PlacedSites.NotPlaced(eikthyr));

        PlacedSites.Remember(PlacedCave(world));
        PlacedSites.Prepare((x, z) => world.GetHeight(x, z));
        PlacedSites.FindNotPlaced(new[] { CaveAt, crypt }, world);
        Assert.False(PlacedSites.NotPlaced(CaveAt));
    }

    /// <summary>The places a generation leaves without a road are found once, on the main thread, before its island workers
    /// start: a worker only looks one up, reading neither a place's levelling (a search of the world's location
    /// list) nor the location list the main thread writes, however often it asks. The set is the generation's: over once
    /// the generation is, so a later road (a manual one) is not judged by it.</summary>
    [Fact]
    public void ThePlacesLeftWithoutARoadAreFoundOnceAndOnlyLookedUpAfter()
    {
        var world = SetUp(Heightmap.Biome.Mountain);

        PlaceTheOthers(world);
        RoadNetworkGenerator.GenerateRoads(force: true);
        Assert.Equal(1, Counter("m_destinationsNotPlaced"));
        Assert.False(PlacedSites.NotPlaced(CaveAt), "the set outlived its generation");

        PlacedSites.FindNotPlaced(new[] { CaveAt }, world);
        LocationLevelling.Source = _ => throw new InvalidOperationException("a place's levelling was read after the set was found");
        ZoneSystem.instance!.m_locationInstances.Clear();
        for (int i = 0; i < 100; i++) Assert.True(PlacedSites.NotPlaced(CaveAt));
    }

    /// <summary>A place given no road says why, as the placing knew it (placed but not readable, its
    /// zone left as it is); with no word from the placing it says only that it was not placed before its roads.</summary>
    [Fact]
    public void APlaceGivenNoRoadSaysWhyAsThePlacingKnewIt()
    {
        var world = SetUp(Heightmap.Biome.Mountain);
        PlacedSites.FindNotPlaced(new[] { CaveAt }, world);
        Assert.Equal("not placed before its roads", PlacedSites.NotPlacedWhy(CaveAt));

        PlacedSites.NoteWhyNotPlaced(CaveAt, "its zone is generated, but no placing of it was found there to read");
        PlacedSites.FindNotPlaced(new[] { CaveAt }, world);
        Assert.Equal("its zone is generated, but no placing of it was found there to read", PlacedSites.NotPlacedWhy(CaveAt));
        // A note is the generation's: the next one finds its own.
        PlacedSites.FindNotPlaced(new[] { CaveAt }, world);
        Assert.Equal("not placed before its roads", PlacedSites.NotPlacedWhy(CaveAt));
    }

    /// <summary>Drives the owned generation as the game's frames do, to its end.</summary>
    private static void Finish()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (RoadLifecycleManager.IsGenerating && clock.Elapsed.TotalSeconds < 120)
        {
            RoadLifecycleManager.Tick();
            System.Threading.Thread.Sleep(1);
        }
        Assert.False(RoadLifecycleManager.IsGenerating, "the generation did not finish in 120 s");
    }

    /// <summary>Every destination in the Mountains waits for its placing (the Mountains are too varied to
    /// guess): a cabin that only smooths round its centre and is turned at random (AbandonedLogCabin04: never placed before,
    /// its road ended 14 m out and 10 m under it), and a place that turns nowhere and levels nothing. Elsewhere such places
    /// do not; the rule for a turned landing stands.</summary>
    [Fact]
    public void EveryMountainDestinationWaitsForItsPlacing()
    {
        var cabin = new[] { new LevelOp(0f, 0f, 0f, 0f, false, 9f, 5f) };
        Assert.True(PlacedSites.NeedsPlacing("AbandonedLogCabin04", false, true, Heightmap.Biome.Mountain, cabin));
        Assert.True(PlacedSites.NeedsPlacing("SomePlace", false, false, Heightmap.Biome.Mountain, null));
        Assert.False(PlacedSites.NeedsPlacing("AbandonedLogCabin04", false, true, Heightmap.Biome.Meadows, cabin));
        Assert.False(PlacedSites.NeedsPlacing("SomePlace", false, false, Heightmap.Biome.Mistlands, null));
        Assert.True(PlacedSites.NeedsPlacing("MountainCave02", true, false, Heightmap.Biome.Meadows, CaveFloor));
    }

    /// <summary>A Mountain place placed before its roads with no flat to land on (a cabin, or one that levels nothing) is not
    /// left without a road: it is reached at its ring, as a place not placed first. Not placed, it waits for its placing and
    /// gets none.</summary>
    [Fact]
    public void AMountainPlacePlacedWithNoFlatKeepsItsRoad()
    {
        var world = SetUp(Heightmap.Biome.Mountain);
        var eikthyr = new Vector2(260f, 40f);
        PlacedSites.FindNotPlaced(new[] { eikthyr }, world);
        Assert.True(PlacedSites.NotPlaced(eikthyr), "a Mountain place not placed was given a road");

        PlacedSites.Remember(PlacedCave(world));
        PlaceTheOthers(world);
        PlacedSites.Prepare((x, z) => world.GetHeight(x, z));
        Assert.False(PlacedSites.TryGetReady(eikthyr, out _), "a place with no flat has no landing to be made ready for");
        Assert.True(PlacedSites.FlatlessPlaced(eikthyr));
        PlacedSites.FindNotPlaced(new[] { eikthyr }, world);
        Assert.False(PlacedSites.NotPlaced(eikthyr));

        RoadNetworkGenerator.GenerateRoads(force: true);
        Assert.Equal(0, Counter("m_destinationsNotPlaced"));
        Assert.True(RoadNetworkGenerator.Reached(new Vector3(eikthyr.x, 0f, eikthyr.y), 10f, "Eikthyrnir"),
            "last refusal: " + (RoadSpatialGrid.LastRefusal ?? "none"));
    }

    /// <summary>road_generate and road_regen_island preload the places they will visit, as the load does: an
    /// operator's generation on a world whose cave was never placed places it first, over frames on the main thread (here
    /// the placing leaves its record as the game's would), and its road goes onto the cave's landing. Before, road_generate
    /// placed nothing first, so under the strict rule every such place got no road.</summary>
    [Theory]
    [InlineData(Heightmap.Biome.Meadows, false)]
    [InlineData(Heightmap.Biome.Mountain, false)]
    [InlineData(Heightmap.Biome.Mountain, true)]
    public void AnOperatorsGenerationPlacesItsPlacesFirstAndReachesThem(Heightmap.Biome biome, bool oneIsland)
    {
        var world = SetUp(biome);
        int main = Environment.CurrentManagedThreadId;
        var placedOn = new List<int>();
        bool chosen = false;
        IEnumerable<bool> Place(RoadNetworkGenerator.GenerationWork work, RoadPreparation preparation)
        {
            chosen = work.AllSelected.Any(d => Vector2.Distance(new Vector2(d.position.x, d.position.z), CaveAt) < 0.5f);
            // A zone is placed a frame later, as the game's prefabs and terrain load between frames.
            preparation.EndSlice(); yield return true;
            placedOn.Add(Environment.CurrentManagedThreadId);
            PlacedSites.Remember(PlacedCave(world));
            PlaceTheOthers(world);
        }
        RoadNetworkGenerator.SitesFirst = Place;
        try
        {
            var told = new List<string>();
            Assert.Null(RoadLifecycleManager.BeginForOperator(oneIsland ? Vector3.zero : null, told.Add));
            Assert.True(RoadLifecycleManager.IsGenerating);
            Finish();

            Assert.True(chosen, "the cave was not among the destinations");
            Assert.Equal(new[] { main }, placedOn);
            Assert.True(RoadNetworkGenerator.RoadsAvailable);
            Assert.True(PlacedSites.TryGetReady(CaveAt, out _), "the placed cave was not made ready for its roads");
            Assert.True(RoadNetworkGenerator.Reached(new Vector3(CaveAt.x, 0f, CaveAt.y), CaveRadius, "MountainCave02"),
                "last refusal: " + (RoadSpatialGrid.LastRefusal ?? "none"));
            Assert.Equal(0, Counter("m_destinationsNotPlaced"));
            Assert.Equal(3, Counter("m_destinationsConnected"));
            Assert.Contains(told, l => oneIsland ? l.StartsWith("Island ") : l == "Road generation complete!");
        }
        finally { RoadNetworkGenerator.SitesFirst = null; }
    }
}
