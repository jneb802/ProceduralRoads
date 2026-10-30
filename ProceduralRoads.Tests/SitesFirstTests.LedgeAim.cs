using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public partial class SitesFirstTests
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

    private PlacedSite CaveOn(WorldGenerator world)
    {
        PlacedSite cave = Cave();
        Func<float, float, float> ground = PlacedSiteGround.Sampler(cave.Record.Ground((x, z) => world.GetHeight(x, z)), cave.Record.X0, cave.Record.Z0, cave.Record.W, cave.Record.H);
        return new PlacedSite { Record = cave.Record, Ground = ground, Shape = PlacedSiteShape.Of(cave.Centre, 90f, CaveFloor[0], 0f, null, ground, false, cave.Record.Root) };
    }

    /// <summary>A road to a cave on a ledge is aimed at the ring where its ground is at the landing's level, not at the bench
    /// under the landing's bearing: aimed there, a road went down a staircase to a bench 38 m under a frost cave's landing and
    /// the way onto the landing climbed straight back up, where the ground round the ring was at the landing's level.</summary>
    [Fact]
    public void ARoadToAPlacedPlaceOnALedgeIsAimedAtTheRingAtItsLandingsLevel()
    {
        var world = new LedgeOverABench();
        _scope.WithWorld(world);
        PlacedSite cave = CaveOn(world);
        Vector2 landing = cave.Shape.Landing;
        float level = cave.Ground(landing.x, landing.y);
        Vector2 onBearing = landing.normalized * (24f + PlacedSites.AimOut);
        Assert.True(cave.Ground(onBearing.x, onBearing.y) < LedgeOverABench.Bench + 1f, $"the landing's bearing meets the ring at {onBearing}, off the bench");

        Vector2 aim = PlacedSites.Aims(cave, 24f, _ => true)[0];

        Assert.True(Mathf.Abs(cave.Ground(aim.x, aim.y) - level) < 5f, $"aimed at {aim}, {cave.Ground(aim.x, aim.y)} m; the landing is {level} m");
        Assert.True(aim.x > 0f, $"aimed at {aim}, round from the landing's side");
    }

    /// <summary>Where the bench is the only road ground round the ring, the road is still aimed there.</summary>
    [Fact]
    public void ARoadToAPlacedPlaceOnALedgeIsAimedAtTheBenchWhereOnlyThatIsRoadGround()
    {
        var world = new LedgeOverABench();
        _scope.WithWorld(world);
        PlacedSite cave = CaveOn(world);

        Vector2 aim = PlacedSites.Aims(cave, 24f, p => world.GetHeight(p.x, p.y) == LedgeOverABench.Bench)[0];

        Assert.Equal(LedgeOverABench.Bench, world.GetHeight(aim.x, aim.y));
        Assert.True(aim.x > 20f, $"aimed at {aim}");
    }

    /// <summary>The ledge aim's points in the order tried: the ring at the landing's level first, the bench under the landing's
    /// bearing last, so a road aimed by level that is refused is still aimed where the bearing alone would aim it.</summary>
    [Fact]
    public void ARoadToAPlacedPlaceOnALedgeIsAimedAtTheBenchLast()
    {
        var world = new LedgeOverABench();
        _scope.WithWorld(world);
        PlacedSite cave = CaveOn(world);

        IReadOnlyList<Vector2> aims = PlacedSites.Aims(cave, 24f, _ => true);

        Assert.InRange(aims.Count, 2, PlacedSites.AimTries);
        Assert.Equal(LedgeOverABench.Bench, world.GetHeight(aims[aims.Count - 1].x, aims[aims.Count - 1].y));
        Assert.All(aims.Take(aims.Count - 1), a => Assert.Equal(50f, world.GetHeight(a.x, a.y)));
        // Turned round the ring, a road is aimed by bearing alone: priced by level, every turn came back to one point.
        Assert.Single(PlacedSites.Aims(cave, 24f, _ => true, 90f));
    }

    /// <summary>A cave on a mesa 16 m over the plain, with a cut 8 m under the landing on its landing's side that runs down to
    /// the plain: the ring at the landing's level is on the mesa, its cliffs a road without stairs cannot climb; the ring
    /// under the landing's bearing is on the cut.</summary>
    private sealed class MesaWithACut : WorldGenerator
    {
        public const float Plain = 34f;
        public const float Cut = 42f;
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f ? 0f
            : wx > 11f && Mathf.Abs(wy) < 8f ? Mathf.Lerp(Cut, Plain, Mathf.Clamp01((wx - 30f) / 60f))
            : Mathf.Abs(wx) <= 40f && Mathf.Abs(wy) <= 40f ? 50f : Plain;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    /// <summary>A road aimed at the ring at the landing's level that gets no road there is aimed again at the next ring point,
    /// the landing's bearing's last, and reaches the place: aimed by level alone, a Hildir frost cave the bearing reached was lost.</summary>
    [Fact]
    public void ARoadToAPlacedPlaceRefusedAtTheLandingsLevelIsAimedAgainAtTheBearing()
    {
        var world = new MesaWithACut();
        _scope.WithWorld(world);
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        StairStructures.Reset();
        PlacedSites.Reset();
        PlacedSite cave = CaveOn(world);
        PlacedSites.Ready(cave);
        LocationLevelling.Source = c => c.magnitude < 0.5f ? CaveFloor : null;
        SetPathfinder(new RoadPathfinder(world) { Stairs = false, TerrainVarianceThreshold = 1000f, Meander = 0f });
        float ring = RoadNetworkGenerator.KeepOutEdge(cave.Centre, 10f, 4f);
        bool Ground(Vector2 p) => RoadCrossingDetector.IsRoadGround(p, world);
        IReadOnlyList<Vector2> aims = PlacedSites.Aims(cave, ring, p => Ground(p) && Ground(p + (p - cave.Centre).normalized * RoadNetworkGenerator.LeadInLength));
        Assert.True(aims.Count >= 2 && world.GetHeight(aims[0].x, aims[0].y) == 50f && world.GetHeight(aims[aims.Count - 1].x, aims[aims.Count - 1].y) == MesaWithACut.Cut, $"aimed at {string.Join(", ", aims)} in turn: first on the mesa, then the cut");

        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(120f, 0f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the cave on the mesa"),
            "last refusal: " + (RoadSpatialGrid.LastRefusal ?? "none"));
        Assert.True(PlacedSites.Landed(cave.Centre));
    }
}
