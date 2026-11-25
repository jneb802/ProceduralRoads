using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public partial class LocationsFirstTests
{
    /// <summary>The level ground at 50 m with a bench 38 m lower east of x 20 for 12 m either side of the cave's facing:
    /// the cave's landing is on a ledge, its ring point at the landing's bearing on the bench under it.</summary>
    private sealed class LedgeOverABench : WorldGenerator
    {
        public const float Bench = 12f;
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f ? 0f : wx > 20f && Mathf.Abs(wy) < 12f ? Bench : 50f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    private PlacedLocation CaveOn(WorldGenerator world)
    {
        PlacedLocation cave = Cave();
        Func<float, float, float> ground = PlacedLocationGround.Sampler(cave.Record.Ground((x, z) => world.GetHeight(x, z)), cave.Record.X0, cave.Record.Z0, cave.Record.W, cave.Record.H);
        return new PlacedLocation { Record = cave.Record, Ground = ground, Shape = PlacedLocationShape.Of(cave.Centre, 90f, CaveFloor[0], 0f, null, ground, false, cave.Record.Root) };
    }

    /// <summary>A road to a cave on a ledge is aimed at the ring where its ground is at the landing's level, not at the bench
    /// under the landing's bearing: aimed there, a road went down a staircase to a bench 38 m under a frost cave's landing and
    /// the way onto the landing climbed straight back up, where the ground round the ring was at the landing's level.</summary>
    [Fact]
    public void ARoadToAPlacedLocationOnALedgeIsAimedAtTheRingAtItsLandingsLevel()
    {
        var world = new LedgeOverABench();
        _scope.WithWorld(world);
        PlacedLocation cave = CaveOn(world);
        Vector2 landing = cave.Shape.Landing;
        float level = cave.Ground(landing.x, landing.y);
        Vector2 onBearing = landing.normalized * (24f + PlacedLocations.AimOut);
        Assert.True(cave.Ground(onBearing.x, onBearing.y) < LedgeOverABench.Bench + 1f, $"the landing's bearing meets the ring at {onBearing}, off the bench");
        RoadNetworkGenerator.Reset();
        PlacedLocations.Ready(cave);

        Vector2 aim = RoadNetworkGenerator.DoorsOf(cave.Centre, 10f, 4f).OrderBy(d => d.Price).First().At;

        Assert.True(Mathf.Abs(cave.Ground(aim.x, aim.y) - level) < 5f, $"aimed at {aim}, {cave.Ground(aim.x, aim.y)} m; the landing is {level} m");
        Assert.True(aim.x > 0f, $"aimed at {aim}, round from the landing's side");
    }

    /// <summary>A plain at 34 m with a flat top at 50 m out to 26 m from the cave, its flanks falling at 70 % to the plain: the
    /// ground a location placed on a mesa's flat top has, its doors on the ring all level with its landing.</summary>
    private sealed class FlatTopWithFlanks : WorldGenerator
    {
        public const float Plain = 34f;
        public const float Top = 50f;
        public const float FlatRadius = 26f;
        public const float Flank = 0.7f;
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) > 400f || Mathf.Abs(wy) > 400f ? 0f
            : Mathf.Max(Plain, Top - Mathf.Max(0f, Mathf.Sqrt(wx * wx + wy * wy) - FlatRadius) * Flank);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    /// <summary>A road's first try, at the cheapest door level with a placed Location's landing, that is refused is searched again
    /// to the doors left (RoadDoors.GenerateRoadByDoors: not the doors round the ring within AimStep of the one taken), so the road
    /// goes on to another door or bearing: a Hildir frost cave the bearing reached was lost when it was not. This pins the re-aim
    /// only. The Location is built as the game's is, as a record remembered and prepared (PlacedLocations.Remember, Prepare): its
    /// footprint set after the resets, its ground grid 40 m round it with the natural ground past it, on a flat top whose flanks
    /// fall at 70 % to the plain. On such ground a first try is refused at the level door (its switchback turns do not fit)
    /// and searched again; no real-Location ground produced the corner this test once pinned, a staircase cut and laid, then
    /// refused only as it is stored (EntryStairsEndsRefused), with nothing else onto the landing: if that ever matters it needs
    /// its own test. Here the road is refused at its second door as well, for the same flank, and not built: the outcome is pinned
    /// as it is, so a change that reaches the landing from here shows as a change to this test.</summary>
    [Fact]
    public void ARoadToAPlacedLocationRefusedAtTheLandingsLevelIsAimedAgainAtTheBearing()
    {
        var world = new FlatTopWithFlanks();
        _scope.WithWorld(world);
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        StairStructures.Reset();
        PlacedLocations.Reset();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), Footprint) });
        var record = new PlacedLocationRecord
        {
            Prefab = "MountainCave02", Centre = new Vector2(0f, 0f), Yaw = 90f, Root = 50f, RockRadius = 0f,
            Ops = new List<LevelOp>(CaveFloor), X0 = -40, Z0 = -40, W = 81, H = 81,
        };
        PlacedLocations.Remember(record);
        PlacedLocations.Prepare((x, z) => world.GetHeight(x, z));
        Assert.True(PlacedLocations.TryGetReady(record.Centre, out PlacedLocation cave));
        LocationLevelling.Source = c => c.magnitude < 0.5f ? CaveFloor : null;
        SetPathfinder(new RoadPathfinder(world) { Stairs = false, TerrainVarianceThreshold = 1000f, Meander = 0f });
        var doors = RoadNetworkGenerator.DoorsOf(cave.Centre, 10f, 4f).OrderBy(d => d.Price).ToList();
        Assert.True(doors.Count >= 2 && doors.All(d => d.Level), $"doors {string.Join(", ", doors.Select(d => d.At))}: all level with the landing");

        var capture = new RoadNetworkGenerator.RoadCapture();
        bool built;
        using (RoadNetworkGenerator.PlanningWith(RoadNetworkGenerator.NewFinder(world, RoadLocationProtection.Clearance(RoadNetworkGenerator.RoadWidth)), capture))
            built = RoadNetworkGenerator.GenerateRoad(new Vector2(120f, 0f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the cave on the flat top");

        string seen = string.Join(" | ", capture.Refusals);
        // The first try, to the cheapest door level with the landing, is refused with its reason, at that door.
        Assert.True(capture.Refusals.Count >= 1, "no refusal: the first try was not refused");
        (string why, Vector2? at) first = capture.Refusals[0];
        Assert.Contains("switchback", first.why);
        Assert.True(first.at is Vector2 a && Vector2.Distance(a, doors[0].LeadIn) < 6f, $"the first try refused at {first.at}, not at the cheapest door {doors[0].LeadIn}");
        // The road is then searched again: a second leg, refused at a door beyond AimStep round the ring from the first.
        Assert.True(capture.Refusals.Count >= 2, $"the road was not searched again; refusals: {seen}");
        Vector2 firstOut = first.at!.Value - cave.Centre, secondOut = capture.Refusals[1].at!.Value - cave.Centre;
        Assert.True((float)Math.Acos(Mathf.Clamp(Vector2.Dot(firstOut, secondOut) / (firstOut.magnitude * secondOut.magnitude), -1f, 1f)) * Mathf.Rad2Deg > PlacedLocations.AimStep, $"the second try refused at {capture.Refusals[1].at}, at the first's bearing {first.at}");
        // The outcome: not reached; its last refusal is the switchback's, and the Location is not landed on.
        Assert.False(built, $"built; refusals: {seen}");
        Assert.Contains("switchback", RoadSpatialGrid.LastRefusal);
        Assert.False(PlacedLocations.Landed(cave.Centre));
    }
}
