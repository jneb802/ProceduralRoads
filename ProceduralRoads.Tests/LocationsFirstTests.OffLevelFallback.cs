using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// in the Mountains a location is reached only at height (a road's end within <see cref="RoadNetworkGenerator.ReachRise"/>
/// of its level, or its landing or approach staircase), or it is unserved. Everywhere else a location is first tried at height,
/// and only where no road can arrive level with it, falls back to a road ending at its ring off its level, the location reached
/// (<see cref="RoadNetworkGenerator.ReachOffLevel"/>, "reached off level"). Crypts are still reached only at their doors and a
/// boss's entrance only on its own landing. Replaces "the Mistlands need no height" and "only the Mountains need it"
/// (a try).
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>The island's planner, as a generation runs it: a start 120 m north and the one location at the origin.</summary>
    private static void Routed(string name, float radius) =>
        RoadNetworkGenerator.GenerateRoutedRoads(new Vector3(0f, 0f, 120f), 0f,
            new List<(string name, Vector3 position, float radius)> { (name, Vector3.zero, radius) });

    /// <summary>Height is needed at every location (<see cref="RoadNetworkGenerator.HeightNeeded"/>), the Mistlands too, until a
    /// location's second attempt runs without it.</summary>
    [Theory]
    [InlineData(Heightmap.Biome.Mountain)]
    [InlineData(Heightmap.Biome.BlackForest)]
    [InlineData(Heightmap.Biome.Mistlands)]
    public void HeightIsNeededEverywhereAtFirst(Heightmap.Biome biome)
    {
        _world.Biome = biome;
        Assert.True(RoadNetworkGenerator.HeightNeeded(new Vector2(0f, 0f)));
    }

    /// <summary>A prepared ring arrival eight metres under its landing is exempt in Mistlands. Elsewhere it remains
    /// unreached even on the fallback; a vanilla Mountains location never falls back.</summary>
    [Theory]
    [InlineData(Heightmap.Biome.Mistlands, true)]
    [InlineData(Heightmap.Biome.BlackForest, false)]
    [InlineData(Heightmap.Biome.Mountain, false)]
    public void APlacedRingArrivalRetainsItsHeightLimitOutsideMistlands(Heightmap.Biome biome, bool reached)
    {
        _world.Biome = biome;
        PlacedLocation cave = OnAPlateau();
        Vector2 landing = cave.Shape.Landing;
        float under = cave.Ground(landing.x, landing.y) - 8f;
        var points = new List<Vector2> { new(-20f, 26f), new(0f, 26f), new(20f, 26f) };
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(_ => under), points.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 40f, false));
        try
        {
            Assert.True(Vector2.Distance(landing, new Vector2(0f, 26f)) > RoadNetworkGenerator.LandingReach, "the road is on the landing");
            Assert.Equal(reached, RoadNetworkGenerator.Reached(Vector3.zero, 10f));
            bool attempted = false;
            Assert.False(RoadNetworkGenerator.ReachOffLevel("MountainCave02", Vector3.zero, 10f, () => attempted = true));
            Assert.Equal(biome == Heightmap.Biome.BlackForest, attempted);
            Assert.Equal(reached, RoadNetworkGenerator.Reached(Vector3.zero, 10f));
            Assert.True(RoadNetworkGenerator.HeightNeeded(new Vector2(0f, 0f)));
            Assert.Equal(0, RoadNetworkGenerator.ReachedOffLevelCount);
        }
        finally { RoadSpatialGrid.Clear(); }
    }

    /// <summary>The same Mountains location with its network under its landing: a location mod's (registered by name) is reached off
    /// level by the fallback only by network standing out at least the height difference over the grade cap past its ring (11.5 m
    /// under: about 33 m), a vanilla one (<c>MountainCave02</c>) is not reached off level at all.</summary>
    [Theory]
    [InlineData("MwlFixtureCave", 8f, true, true)]
    [InlineData("MwlFixtureCave", 11.5f, true, true)]
    [InlineData("MwlFixtureCave", 11.5f, false, false)]
    [InlineData("MwlFixtureCave", -7.5f, false, false)]
    [InlineData("MountainCave02", 8f, true, false)]
    public void ACustomMountainsLocationFallsBackToOffLevelAtItsStandOffAndAVanillaOneDoesNot(string name, float below, bool atStandOff, bool reached)
    {
        _world.Biome = Heightmap.Biome.Mountain;
        PlacedLocation cave = OnAPlateau();
        RoadNetworkGenerator.RegisterLocation("MwlFixtureCave");
        Vector2 landing = cave.Shape.Landing;
        float under = cave.Ground(landing.x, landing.y) - below;
        float ring = RoadNetworkGenerator.KeepOutEdge(new Vector2(0f, 0f), 10f, RoadNetworkGenerator.RoadWidth);
        float z = atStandOff ? ring + Mathf.Max(0f, below) / RoadGrade.Configured : ring + 2f;
        var points = new List<Vector2> { new(-20f, z), new(0f, z), new(20f, z) };
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(_ => under), points.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 40f, false));
        try
        {
            Assert.False(RoadNetworkGenerator.Reached(Vector3.zero, 10f));
            Assert.Equal(reached, RoadNetworkGenerator.ReachOffLevel(name, Vector3.zero, 10f, () => false));
            Assert.Equal(reached, RoadNetworkGenerator.Reached(Vector3.zero, 10f));
            Assert.Equal(reached ? 1 : 0, RoadNetworkGenerator.ReachedOffLevelCount);
            if (reached) Assert.True(z >= ring + Mathf.Max(0f, below) / RoadGrade.Configured);
        }
        finally { RoadSpatialGrid.Clear(); RoadNetworkGenerator.UnregisterLocation("MwlFixtureCave"); }
    }

    /// <summary>A Black Forest location a road arrives level with (onto its landing) is reached at height, by its first attempt:
    /// never on the fallback.</summary>
    [Fact]
    public void ABlackForestLocationARoadArrivesLevelWithIsReachedLevel()
    {
        _world.Biome = Heightmap.Biome.BlackForest;
        RoadNetworkGenerator.Reset();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), Footprint) });
        PlacedLocations.Reset();
        PlacedLocation cave = Cave();
        PlacedLocations.Ready(cave);
        LocationLevelling.Source = c => c.magnitude < 0.5f ? CaveFloor : null;
        SetPathfinder(new RoadPathfinder(_world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });

        Routed("MountainCave02", 10f);

        Assert.True(PlacedLocations.Landed(cave.Centre));
        Assert.True(RoadNetworkGenerator.Reached(Vector3.zero, 10f));
        Assert.Equal(0, RoadNetworkGenerator.ReachedOffLevelCount);
        Assert.True(RoadNetworkGenerator.HeightNeeded(new Vector2(0f, 0f)));
    }

    /// <summary>A plateau's ring ten metres under its landing is refused in the Black Forest on both attempts.
    /// Mistlands keeps the road at its ring without a landing entry.</summary>
    [Theory]
    [InlineData(Heightmap.Biome.BlackForest, false)]
    [InlineData(Heightmap.Biome.Mistlands, true)]
    public void APlateausRingRoadIsKeptOnlyWithTheMistlandsExemption(Heightmap.Biome biome, bool reached)
    {
        _world.Biome = biome;
        PlacedLocation cave = OnAPlateau();

        Routed("MountainCave02", 10f);

        Assert.False(PlacedLocations.Landed(cave.Centre));
        Assert.Empty(StairStructures.Runs);
        var road = RoadSpatialGrid.GetRoadPointsNearPosition(Vector3.zero, 40f).Where(p => p.IsRoad).ToList();
        Assert.Equal(reached, road.Count > 0);
        Assert.Equal(reached, RoadNetworkGenerator.Reached(Vector3.zero, 10f));
        Assert.Equal(0, RoadNetworkGenerator.ReachedOffLevelCount);
        Assert.True(RoadNetworkGenerator.HeightNeeded(new Vector2(0f, 0f)));
    }

    /// <summary>A crypt is reached at its door, at its first attempt, as before; and network under its door's landing does
    /// not reach it on a fallback: a crypt never falls back.</summary>
    [Fact]
    public void ACryptIsStillReachedOnlyAtItsDoor()
    {
        _world.Biome = Heightmap.Biome.BlackForest;
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        StairStructures.Reset();
        PlacedLocations.Reset();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), 30f) });
        PlacedLocation crypt = Crypt("Crypt4", Crypt4Ops, 90f, 18f);
        PlacedLocations.Ready(crypt);
        LocationLevelling.Source = c => c.magnitude < 0.5f ? Crypt4Ops : null;
        SetPathfinder(new RoadPathfinder(_world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });

        Routed("Crypt4", 18f);

        Assert.True(PlacedLocations.Landed(crypt.Centre));
        Assert.True(RoadNetworkGenerator.Reached(Vector3.zero, 18f));
        Assert.Equal(0, RoadNetworkGenerator.ReachedOffLevelCount);

        // The same crypt with only a road 8 m under its door's landing, 4 m from it: not reached, and no fallback.
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        PlacedLocations.Reset();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), 30f) });
        crypt = Crypt("Crypt4", Crypt4Ops, 90f, 18f);
        PlacedLocations.Ready(crypt);
        Vector2 door = crypt.Shape.Landing;
        float under = crypt.Ground(door.x, door.y) - 8f;
        var points = new List<Vector2> { door + new Vector2(4f, -20f), door + new Vector2(4f, 0f), door + new Vector2(4f, 20f) };
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(_ => under), points.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 40f, false));
        try
        {
            Assert.False(RoadNetworkGenerator.Reached(Vector3.zero, 18f));
            bool attempted = false;
            Assert.False(RoadNetworkGenerator.ReachOffLevel("Crypt4", Vector3.zero, 18f, () => attempted = true));
            Assert.False(attempted);
            Assert.False(RoadNetworkGenerator.Reached(Vector3.zero, 18f));
            Assert.Equal(0, RoadNetworkGenerator.ReachedOffLevelCount);
        }
        finally { RoadSpatialGrid.Clear(); }
    }

    /// <summary>A Mistlands location that levels nothing, its root 13 m above the ground round it (a fort on its rock), and the
    /// network level with that ground 12.5 m from its centre: a location mod's is not reached there, only at its stand-off (the
    /// 13 m over the grade cap past its ring); a location mod's 7 m under the network, and a vanilla one 13 m above it, are
    /// reached there as before. No fallback is needed for any of them.</summary>
    [Theory]
    [InlineData("MwlFixtureFort", 13f, false, false)]
    [InlineData("MwlFixtureFort", 13f, true, true)]
    [InlineData("MwlFixtureFort", -7f, false, true)]
    [InlineData("AbandonedLogCabin04", 13f, false, true)]
    public void ACustomLocationThatLevelsNothingStandsItsRoadOffOnlyWhereItStandsAboveIt(string name, float above, bool atStandOff, bool reached)
    {
        _world.Biome = Heightmap.Biome.Mistlands;
        RoadNetworkGenerator.RegisterLocation("MwlFixtureFort");
        LocationLevelling.Source = null;
        const float ground = 50f;
        LocationLevelling.PlacementHeightSource = c => c.magnitude < 0.5f ? ground + above : null;
        float ring = RoadNetworkGenerator.KeepOutEdge(new Vector2(0f, 0f), 10f, RoadNetworkGenerator.RoadWidth);
        float z = atStandOff ? ring + Mathf.Max(0f, above) / RoadGrade.Configured : 12.5f;
        var points = new List<Vector2> { new(-20f, z), new(0f, z), new(20f, z) };
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(_ => ground), points.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 40f, false));
        try
        {
            Assert.True(12.5f <= ring + RoadNetworkGenerator.ReachPastEdge, "12.5 m out is past the ring's reach");
            Assert.True(RoadNetworkGenerator.HeightNeeded(new Vector2(0f, 0f)));
            Assert.Equal(reached, RoadNetworkGenerator.Reached(Vector3.zero, 10f, name));
            Assert.Equal(0, RoadNetworkGenerator.ReachedOffLevelCount);
        }
        finally
        {
            RoadSpatialGrid.Clear();
            LocationLevelling.PlacementHeightSource = null;
            RoadNetworkGenerator.UnregisterLocation("MwlFixtureFort");
        }
    }

    /// <summary>Level at 80 m north of the origin; south, falling at 0.6 (steeper than the grade cap) to 30 m.</summary>
    private sealed class FallingSouth : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => wy < 0f ? Mathf.Max(30f, 80f + 0.6f * wy) : 80f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override int GetSeed() => 77;
    }

    /// <summary>A road searched from a custom location's centre south down ground steeper than the grade cap is trimmed on the
    /// bearing it crosses the ring on (south), not on the search's own first point (the centre, no bearing): there it stands
    /// off to the first ground low enough for the cap (about 143 m past the ring, onto the 30 m floor), where a fixed point
    /// on that slope ran away; north, on its own level, it stands at the ring.</summary>
    [Fact]
    public void ACustomLocationsStandOffIsReadWhereItsRoadCrossesTheRingAndOnSteepGroundStillLandsWhereTheCapAllows()
    {
        using var scope = new Valheim.Testing.Doubles.ValheimWorldScope().WithWorld(new FallingSouth());
        RoadNetworkGenerator.RegisterLocation("MwlFixtureFort");
        LocationLevelling.Source = null;
        LocationLevelling.PlacementHeightSource = c => c.magnitude < 0.5f ? 80f : null;
        try
        {
            RoadNetworkGenerator.NoteCustomLocation("MwlFixtureFort", Vector3.zero);
            var centre = new Vector2(0f, 0f);
            float ring = RoadNetworkGenerator.KeepOutEdge(centre, 10f, RoadNetworkGenerator.RoadWidth);
            var path = new List<Vector2>();
            for (int i = 0; i <= 60; i++) path.Add(new Vector2(0f, -4f * i));
            Vector2 crossing = RoadNetworkGenerator.RingCrossing(path, centre, ring, true);
            Assert.True(crossing.y < -ring + 0.01f && crossing.y > -ring - 4.01f, $"crossing {crossing}");
            float south = RoadNetworkGenerator.OffLevelStandOff(centre, 10f, crossing);
            float want = (80f - 30f) / RoadGrade.Configured;
            Assert.InRange(south, want - 0.01f, want + RoadNetworkGenerator.ReachPastEdge);
            Assert.Equal(0f, RoadNetworkGenerator.OffLevelStandOff(centre, 10f, new Vector2(0f, 40f)));
            // The search's own first point, the centre, gives no bearing: the stand-off read there is the north's, 0.
            Assert.Equal(0f, RoadNetworkGenerator.OffLevelStandOff(centre, 10f, path[0]));
        }
        finally
        {
            LocationLevelling.PlacementHeightSource = null;
            RoadNetworkGenerator.UnregisterLocation("MwlFixtureFort");
        }
    }

    /// <summary>Level Mistlands ground at 50 m; where <paramref name="wetFrom"/> is below <paramref name="wetTo"/>, water (20 m)
    /// from that far to that far from the origin.</summary>
    private sealed class StandOffGround : WorldGenerator
    {
        private readonly float wetFrom, wetTo;
        public StandOffGround(float wetFrom = 0f, float wetTo = 0f) { this.wetFrom = wetFrom; this.wetTo = wetTo; }
        public override float GetHeight(float wx, float wy)
        {
            float d = Mathf.Sqrt(wx * wx + wy * wy);
            return wetFrom < wetTo && d >= wetFrom && d <= wetTo ? 20f : 50f;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override int GetSeed() => 78;
    }

    /// <summary>A custom location that levels nothing, its root <paramref name="above"/> over the ground (a fort on its rock), on
    /// ground <paramref name="world"/>; runs <paramref name="check"/> with its ring.</summary>
    private static void OnAFort(WorldGenerator world, float above, System.Action<Vector2, float> check)
    {
        using var scope = new Valheim.Testing.Doubles.ValheimWorldScope().WithWorld(world);
        RoadNetworkGenerator.RegisterLocation("MwlFixtureFort");
        LocationLevelling.Source = null;
        LocationLevelling.PlacementHeightSource = c => c.magnitude < 0.5f ? 50f + above : null;
        try
        {
            RoadNetworkGenerator.NoteCustomLocation("MwlFixtureFort", Vector3.zero);
            var centre = new Vector2(0f, 0f);
            check(centre, RoadNetworkGenerator.KeepOutEdge(centre, 8f, RoadNetworkGenerator.RoadWidth));
        }
        finally
        {
            RoadSpatialGrid.Clear();
            LocationLevelling.PlacementHeightSource = null;
            RoadNetworkGenerator.UnregisterLocation("MwlFixtureFort");
        }
    }

    /// <summary>A custom location standing 21 m over its road's end (ring 8): the grade's stand-off is 60 m past its ring, but the
    /// road ends 30 m past it, and network there reaches it; network at the grade's stand-off (a road searched again without
    /// the MWL stand-off) still does, and network short of it does not.</summary>
    [Fact]
    public void ACustomLocationStandingWellAboveItsRoadsEndIsStoodOff30mPastTheRingNotAtTheGrade()
    {
        OnAFort(new StandOffGround(), 21f, (centre, ring) =>
        {
            Assert.True(21f / RoadGrade.Configured > RoadNetworkGenerator.StandOffPastRing, "the grade's stand-off is past 30 m");
            float out1 = RoadNetworkGenerator.OffLevelStandOff(centre, 8f, new Vector2(0f, -ring - 2f));
            Assert.Equal(RoadNetworkGenerator.StandOffPastRing, out1, 2);
            foreach (var (z, reached) in new[] { (ring + out1, true), (ring + 21f / RoadGrade.Configured, true), (ring + 10f, false) })
            {
                // A short stretch across the bearing, so none of it strays past 30 m.
                var points = new List<Vector2> { new(-1f, -z), new(0f, -z), new(1f, -z) };
                RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(_ => 50f), points.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 40f, false));
                Assert.True(reached == RoadNetworkGenerator.Reached(Vector3.zero, 8f, "MwlFixtureFort"), $"network {z - ring:F1} m past the ring");
                RoadSpatialGrid.Clear();
            }
        });
    }

    /// <summary>The same location with water across 30 m past the ring and the step short of it: no end there is dry, and the road stands off at the grade
    /// (60 m past its ring), as before the MWL stand-off.</summary>
    [Fact]
    public void ACustomLocationWhoseStandOffIsUnderWaterStandsOffAtTheGrade()
    {
        float edge = RoadNetworkGenerator.KeepOutEdge(new Vector2(0f, 0f), 8f, RoadNetworkGenerator.RoadWidth);
        var world = new StandOffGround(edge + RoadNetworkGenerator.StandOffPastRing - 2f * RoadNetworkGenerator.ReachPastEdge, edge + RoadNetworkGenerator.StandOffPastRing + 6f);
        OnAFort(world, 21f, (centre, ring) =>
        {
            float out1 = RoadNetworkGenerator.OffLevelStandOff(centre, 8f, new Vector2(0f, -ring - 2f));
            float want = 21f / RoadGrade.Configured;
            Assert.InRange(out1, want - 0.01f, want + RoadNetworkGenerator.ReachPastEdge);
        });
    }

    /// <summary>The reached test asks the trim's own owner of the MWL stand-off's ends (StandOffInBand): with water across
    /// both band cells on the bearing, no road ends there, so network there 30 m past the ring does not reach the location (it
    /// did when the reached test kept a band window of its own); network at the grade's stand-off still does.</summary>
    [Fact]
    public void NetworkAtTheMwlStandOffReachesOnlyWhereTheTrimWouldEndARoad()
    {
        float edge = RoadNetworkGenerator.KeepOutEdge(new Vector2(0f, 0f), 8f, RoadNetworkGenerator.RoadWidth);
        var world = new StandOffGround(edge + RoadNetworkGenerator.StandOffPastRing - 2f * RoadNetworkGenerator.ReachPastEdge, edge + RoadNetworkGenerator.StandOffPastRing + 6f);
        OnAFort(world, 21f, (centre, ring) =>
        {
            foreach (var (z, reached) in new[] { (ring + RoadNetworkGenerator.StandOffPastRing, false), (ring + 21f / RoadGrade.Configured, true) })
            {
                var points = new List<Vector2> { new(-1f, -z), new(0f, -z), new(1f, -z) };
                RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(_ => 50f), points.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 40f, false));
                Assert.True(reached == RoadNetworkGenerator.Reached(Vector3.zero, 8f, "MwlFixtureFort"), $"network {z - ring:F1} m past the ring");
                RoadSpatialGrid.Clear();
            }
        });
    }

    /// <summary>A road ending at the MWL stand-off and refused is searched again at the grade's stand-off only where that can
    /// change it: refused where its refusal does not say, or on the stretch the grade's stand-off gives up (inside the grade's
    /// stand-off of the location, and a path cell); refused far off along the road, it is not searched again.</summary>
    [Fact]
    public void ARoadRefusedAwayFromItsMwlStandOffEndIsNotSearchedAgain()
    {
        var centre = new Vector2(100f, 100f);
        var banded = new List<(Vector2, float)> { (centre, 60f) };
        Assert.True(RoadNetworkGenerator.RefusedAtABandedEnd(null, banded));
        Assert.True(RoadNetworkGenerator.RefusedAtABandedEnd(new List<Vector2>(), banded));
        Assert.True(RoadNetworkGenerator.RefusedAtABandedEnd(new List<Vector2> { centre + new Vector2(0f, -45f) }, banded));
        Assert.True(RoadNetworkGenerator.RefusedAtABandedEnd(new List<Vector2> { centre + new Vector2(0f, -60f - RoadNetworkGenerator.ReachPastEdge + 0.5f) }, banded));
        Assert.False(RoadNetworkGenerator.RefusedAtABandedEnd(new List<Vector2> { centre + new Vector2(0f, -300f) }, banded));
        // Any refused point on the stretch will do.
        Assert.True(RoadNetworkGenerator.RefusedAtABandedEnd(new List<Vector2> { centre + new Vector2(0f, -300f), centre + new Vector2(20f, -30f) }, banded));
    }

    /// <summary>A small rise (5 m, the grade's stand-off about 14 m past the ring, short of 30 m) keeps the grade's,
    /// nearer than 30 m; a location 7 m under its road's end (a hut below the road) and a vanilla location stand nothing off.</summary>
    [Theory]
    [InlineData(5f, true)]
    [InlineData(-7f, true)]
    [InlineData(21f, false)]
    public void ASmallRiseKeepsTheGradesStandOffAndALocationBelowOrVanillaNone(float above, bool custom)
    {
        OnAFort(new StandOffGround(), above, (centre, ring) =>
        {
            // A vanilla location: one not noted as a custom location.
            Vector2 at = custom ? centre : new Vector2(300f, 0f);
            float out1 = RoadNetworkGenerator.OffLevelStandOff(at, 8f, at + new Vector2(0f, -ring - 2f));
            float want = custom ? Mathf.Max(0f, above) / RoadGrade.Configured : 0f;
            Assert.True(want < RoadNetworkGenerator.StandOffPastRing);
            Assert.InRange(out1, want - 0.01f, want + RoadNetworkGenerator.ReachPastEdge);
        });
    }

    /// <summary>A custom Mountains location with network eight metres under its landing has no landing entry.
    /// Only a built entry reaches it without the ring's stand-off.</summary>
    [Fact]
    public void ACustomPlacedLocationNeedsABuiltLandingEntryToBypassItsStandOff()
    {
        _world.Biome = Heightmap.Biome.Mountain;
        PlacedLocation cave = OnAPlateau();
        RoadNetworkGenerator.RegisterLocation("MwlFixtureCave");
        Vector2 landing = cave.Shape.Landing;
        float under = cave.Ground(landing.x, landing.y) - 8f;
        var points = new List<Vector2> { landing + new Vector2(-2f, 0f), landing, landing + new Vector2(2f, 0f) };
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(_ => under), points.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 40f, false));
        try
        {
            Assert.False(RoadNetworkGenerator.Reached(Vector3.zero, 10f));
            Assert.False(RoadNetworkGenerator.ReachOffLevel("MwlFixtureCave", Vector3.zero, 10f, () => false));
            Assert.False(RoadNetworkGenerator.Reached(Vector3.zero, 10f));
            PlacedLocations.MarkLanded(cave.Centre);
            Assert.True(RoadNetworkGenerator.Reached(Vector3.zero, 10f));
        }
        finally { RoadSpatialGrid.Clear(); RoadNetworkGenerator.UnregisterLocation("MwlFixtureCave"); }
    }

    /// <summary>A location mod's fort that levels nothing, its root 13 m over the flat ground round it, on the fallback: a road
    /// trimmed to its stand-off on the ground (about 37 m past its ring, the trim's next path cell) is kept by the refusal of its
    /// end (<see cref="RoadNetworkGenerator.EndNotLevel"/>) only where the reached test then reaches the location, by the one ring
    /// test both ask. Ending on the ground it reaches it; held 3 m over the ground (the grade limiter), its own stand-off is about
    /// 9 m nearer and the end is not at it: refused before, not committed and the location left unserved with a road to nowhere.</summary>
    [Theory]
    [InlineData(0f, true)]
    [InlineData(3f, false)]
    public void OnTheFallbackARoadsEndIsKeptOnlyWhereTheReachedTestThenReachesItsLocation(float held, bool served)
    {
        _world.Biome = Heightmap.Biome.Mistlands;
        RoadNetworkGenerator.RegisterLocation("MwlFixtureFort");
        LocationLevelling.Source = null;
        const float ground = 50f;
        LocationLevelling.PlacementHeightSource = c => c.magnitude < 0.5f ? ground + 8f : null;
        var centre = new Vector2(0f, 0f);
        var start = new Vector2(0f, 120f);
        float ring = RoadNetworkGenerator.KeepOutEdge(centre, 10f, RoadNetworkGenerator.RoadWidth);
        try
        {
            (string refusal, string why)? refused = null;
            bool committed = false;
            bool reached = RoadNetworkGenerator.ReachOffLevel("MwlFixtureFort", Vector3.zero, 10f, () =>
            {
                Assert.False(RoadNetworkGenerator.HeightNeeded(centre));
                float z = ring + RoadNetworkGenerator.OffLevelStandOff(centre, 10f, start);
                Assert.InRange(z, ring + 8f / RoadGrade.Configured, ring + 8f / RoadGrade.Configured + RoadNetworkGenerator.ReachPastEdge);
                var points = new List<Vector2> { start, new(0f, (start.y + z) / 2f), new(0f, z) };
                var plan = new RoadSpatialGrid.PlannedPath(points, new List<float> { ground, ground, ground + held },
                    points.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 40f, false);
                refused = RoadNetworkGenerator.EndNotLevel(points, new List<RoadSpatialGrid.PlannedPath> { plan }, new List<RoadStairSpan>(),
                    new List<RoadStairSpan>(), start, 0f, centre, 10f, false, true);
                if (refused != null) return false;
                RoadSpatialGrid.Commit(plan);
                committed = true;
                return true;
            });
            Assert.Equal(served, refused == null);
            if (!served) Assert.Equal(RoadNetworkGenerator.NotLevelRefusal, refused!.Value.refusal);
            Assert.Equal(served, reached);
            Assert.Equal(committed, reached);
        }
        finally
        {
            RoadSpatialGrid.Clear();
            LocationLevelling.PlacementHeightSource = null;
            RoadNetworkGenerator.UnregisterLocation("MwlFixtureFort");
        }
    }
}
