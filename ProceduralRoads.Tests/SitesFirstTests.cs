using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// Places placed before their roads ("sites first"): the
/// load-time generation waits for them, and a road to one goes onto its real landing.
/// </summary>
public partial class SitesFirstTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    /// <summary>Level ground at 50 m out to 200 m, sea beyond.</summary>
    private sealed class LevelWorld : WorldGenerator
    {
        /// <summary>The Mountains, where a place gets a staircase onto its landing (<see cref="RoadNetworkGenerator.ApproachStairsAt"/>).</summary>
        public Heightmap.Biome Biome = Heightmap.Biome.Mountain;
        public override float GetHeight(float wx, float wy) => Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f ? 0f : 50f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Biome;
        public override int GetSeed() => 77;
    }

    private const float Footprint = 20f;
    /// <summary>MountainCave02's levelling: a floor 4 m across, 4.75 m behind the centre and 4.09 m down, smoothed out to 7 m.</summary>
    private static readonly IReadOnlyList<LevelOp> CaveFloor = new[] { new LevelOp(-0.06f, -4.75f, -4.09f, 4f, false, 7f, 3f) };
    private readonly LevelWorld _world = new();

    public SitesFirstTests()
    {
        _scope.WithWorld(_world);
        RoadNetworkGenerator.Reset();
        PlacedSites.Reset();
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(new Vector2(0f, 0f), Footprint) });
    }

    public void Dispose()
    {
        SetPathfinder(null);
        PlacedSites.Reset();
        LocationLevelling.Source = null;
        RoadSiteProtection.Reset();
        RoadNetworkGenerator.SitesFirst = null;
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, pathfinder);

    /// <summary>The level ground with a deep sea east of x 28 for 12 m either side of the cave's facing:
    /// its ring point on that side is dry, but just beyond it is water.</summary>
    private sealed class SeaOnTheLandingsSide : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f || (wx > 28f && Mathf.Abs(wy) < 12f) ? 0f : 50f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    /// <summary>A frost cave at the origin turned to face +x (yaw 90), as placed on the level ground.</summary>
    private PlacedSite Cave()
    {
        var record = new PlacedSiteRecord
        {
            Prefab = "MountainCave02", Centre = new Vector2(0f, 0f), Yaw = 90f, Root = 50f, RockRadius = 0f,
            Ops = new List<LevelOp>(CaveFloor), X0 = -60, Z0 = -60, W = 121, H = 121,
        };
        Func<float, float, float> ground = PlacedSiteGround.Sampler(record.Ground((x, z) => _world.GetHeight(x, z)), record.X0, record.Z0, record.W, record.H);
        return new PlacedSite { Record = record, Ground = ground, Shape = PlacedSiteShape.Of(record.Centre, record.Yaw, CaveFloor[0], 0f, null, ground, false, record.Root) };
    }

    /// <summary>A road ending 58 m from a placed place's landing is still carried on onto it (a ring point
    /// at the side of a Dvergr entrance is 41 m out, its landing 24 m in); an approach keeps its 40 m.</summary>
    [Fact]
    public void AWayOntoAPlacedPlacesLandingReachesFurtherThanAnApproach()
    {
        PlacedSite cave = Cave();
        Vector2 end = cave.Shape.Landing + new Vector2(0f, 58f);
        RoadSpatialGrid.PlannedPath? From(float reach, out string why)
        {
            var request = new SiteApproachRequest
            {
                RoadEnd = end, RoadHeight = 50f, Arriving = 180f, Centre = cave.Centre,
                RoadTail = new List<Vector2> { end + new Vector2(0f, 20f), end }, Reach = reach,
            };
            return RoadSiteEntry.Plan(request, cave.Shape, 4f, cave.Ground, _world, out why);
        }

        Assert.True(From(RoadNetworkGenerator.PlacedSiteReach, out string placed) != null, placed);
        Assert.True(From(RoadStairRuns.SiteReach, out string approach) == null && approach.Contains("too far"), approach);
    }

    /// <summary>A road to a placed place goes onto its landing as part of the road, and nothing is left to lay
    /// later (no 'give up at outskirts' approach that leaves the zone load to finish it off).</summary>
    [Fact]
    public void ARoadToAPlacedPlaceGoesOntoItsLanding()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        StairStructures.Reset();
        PlacedSites.Reset();
        PlacedSite cave = Cave();
        PlacedSites.Ready(cave);
        LocationLevelling.Source = c => c.magnitude < 0.5f ? CaveFloor : null;
        SetPathfinder(new RoadPathfinder(_world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });

        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, 120f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the cave"));

        var walked = RoadSpatialGrid.GetRoadPointsNearPosition(Vector3.zero, 60f).Where(p => p.IsRoad).Select(p => p.p)
            .Concat(StairStructures.Runs.SelectMany(r => r.Pieces).Select(p => new Vector2(p.Position.x, p.Position.z))).ToList();
        float toLanding = walked.Min(p => Vector2.Distance(p, cave.Shape.Landing));
        string outcomes = "last refusal: " + (RoadSpatialGrid.LastRefusal ?? "none");
        // A road keeps its own reach (half its width and its blend) off the entrance's pit, so it
        // stops a little short of the shape's nominal landing, but inside the place's footprint.
        Assert.True(toLanding <= 6f, $"nearest the landing {cave.Shape.Landing} is {toLanding:F1} m off; {outcomes}");
        Assert.Contains(walked, p => p.magnitude < Footprint);
        // Reached because it was carried on onto the landing, wherever on it that ended.
        Assert.True(PlacedSites.Landed(cave.Centre));
        Assert.True(RoadNetworkGenerator.Reached(Vector3.zero, 10f));
    }

    /// <summary>A regeneration forgets which places the old network landed on (its roads are gone), not the
    /// places placed: they are the world's.</summary>
    [Fact]
    public void ARegenerationForgetsWhichPlacesTheOldNetworkLandedOn()
    {
        PlacedSite cave = Cave();
        PlacedSites.Remember(cave.Record);
        PlacedSites.MarkLanded(cave.Centre);
        RoadNetworkGenerator.Reset();
        Assert.False(PlacedSites.Landed(cave.Centre));
        Assert.True(PlacedSites.TryGet(cave.Centre, out _));
    }

    /// <summary>A road carried on onto a level landing further out than the shape's own landing point reaches
    /// the place: the staircases onto a frost cave's ledge and a Dvergr entrance's landing (5440,4800) ended 9.3 m
    /// and 9.0 m from it and were counted unreached.</summary>
    [Fact]
    public void ARoadCarriedOntoALevelLandingFurtherOutReachesThePlace()
    {
        PlacedSite cave = Cave();
        Vector2 point = cave.Shape.Landing;
        // Uneven ground round the shape's landing point: the level landing is further out.
        Func<float, float, float> rippled = (x, z) =>
        {
            float h = cave.Ground(x, z);
            float d = Vector2.Distance(new Vector2(x, z), point);
            return d < 10f && d > 1.5f ? h + 0.6f * Mathf.Sin(x * 1.7f) * Mathf.Sin(z * 1.7f) : h;
        };
        var shape = PlacedSiteShape.Of(cave.Centre, 90f, CaveFloor[0], 0f, null, rippled, false, cave.Record.Root);
        PlacedSites.Ready(new PlacedSite { Record = cave.Record, Ground = rippled, Shape = shape });
        LocationLevelling.Source = c => c.magnitude < 0.5f ? CaveFloor : null;
        SetPathfinder(new RoadPathfinder(_world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });

        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, 120f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the cave"));

        var landings = shape.Landings(rippled, 4f);
        Vector2 nearest = RoadSpatialGrid.GetRoadPointsNearPosition(Vector3.zero, 60f).Where(p => p.IsRoad).OrderBy(p => Vector2.Distance(p.p, point)).First().p;
        string outcomes = "last refusal: " + (RoadSpatialGrid.LastRefusal ?? "none");
        Assert.True(Vector2.Distance(nearest, point) > RoadNetworkGenerator.LandingReach,
            $"the road came {Vector2.Distance(nearest, point):F1} m from {point}; landings {string.Join(" ", landings.Select(l => l.at))}; {outcomes}");
        Assert.True(RoadNetworkGenerator.Reached(Vector3.zero, 10f), outcomes);
    }

    /// <summary>Where nothing gets from the ring onto a placed place's landing, the road is refused, not left at
    /// the outskirts: the planner tries its other candidates.</summary>
    [Fact]
    public void ARoadThatCannotGetOntoAPlacedPlacesLandingIsRefused()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        StairStructures.Reset();
        PlacedSites.Reset();
        PlacedSite cave = Cave();
        // The placed ground a sheer wall 40 m high everywhere but the ring the road ends on.
        Func<float, float, float> wall = (x, z) => new Vector2(x, z).magnitude < 22f ? 90f : 50f;
        PlacedSites.Ready(new PlacedSite { Record = cave.Record, Ground = wall, Shape = PlacedSiteShape.Of(cave.Centre, 90f, CaveFloor[0], 0f, null, wall, false, 90f) });
        LocationLevelling.Source = c => c.magnitude < 0.5f ? CaveFloor : null;
        SetPathfinder(new RoadPathfinder(_world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });

        bool built = RoadNetworkGenerator.GenerateRoad(new Vector2(0f, 120f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the cave");

        Assert.False(built);
        Assert.Equal("no way onto a placed place's landing", RoadSpatialGrid.LastRefusal);
        Assert.Empty(RoadSpatialGrid.GetRoadPointsNearPosition(Vector3.zero, 60f));
    }

    /// <summary>Where the landing's side cannot be reached from beyond the ring, the road still comes in as
    /// near that side as it can, further round the ring, not wherever a search to the place arrives (three Dvergr
    /// entrances on one test world found no route to their landing's side, and one then came in on
    /// the far side, too far from its landing).</summary>
    [Fact]
    public void ARoadThatCannotReachAPlacedPlacesLandingSideComesInFurtherRoundTheRing()
    {
        var sea = new SeaOnTheLandingsSide();
        _scope.WithWorld(sea);
        PlacedSite cave = Cave();
        Func<float, float, float> ground = PlacedSiteGround.Sampler(cave.Record.Ground((x, z) => sea.GetHeight(x, z)), cave.Record.X0, cave.Record.Z0, cave.Record.W, cave.Record.H);
        PlacedSites.Ready(new PlacedSite { Record = cave.Record, Ground = ground, Shape = PlacedSiteShape.Of(cave.Centre, 90f, CaveFloor[0], 0f, null, ground, false, cave.Record.Root) });
        LocationLevelling.Source = c => c.magnitude < 0.5f ? CaveFloor : null;
        SetPathfinder(new RoadPathfinder(sea) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });

        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-20f, 120f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the cave"));

        Vector2 nearest = RoadSpatialGrid.GetRoadPointsNearPosition(Vector3.zero, 60f).Where(p => p.IsRoad && p.p.magnitude > Footprint)
            .OrderBy(p => p.p.magnitude).First().p;
        float offLanding = Mathf.Abs(Mathf.Atan2(nearest.y, nearest.x) * Mathf.Rad2Deg);
        string outcomes = "last refusal: " + (RoadSpatialGrid.LastRefusal ?? "none");
        Assert.True(offLanding <= 60f, $"the road's nearest point outside the cave is {nearest}, {offLanding:F0} deg round from its landing's side; {outcomes}");
    }

    /// <summary>A staircase onto a placed place's landing that could not be laid is not laid out again from
    /// the same start, and a place gets no more than EntryStairsPerPlace of them: the slowest islands took
    /// 570 and 537 s, most of it laying the same failing staircases for each road tried to a place.</summary>
    [Fact]
    public void AFailedStaircaseOntoAPlacedPlacesLandingIsNotLaidOutAgain()
    {
        int perPlace = RoadNetworkGenerator.EntryStairsPerPlace;
        try
        {
            PlacedSite cave = Cave();
            Func<float, float, float> wall = (x, z) => new Vector2(x, z).magnitude < 22f ? 90f : 50f;
            PlacedSites.Ready(new PlacedSite { Record = cave.Record, Ground = wall, Shape = PlacedSiteShape.Of(cave.Centre, 90f, CaveFloor[0], 0f, null, wall, false, 90f) });
            LocationLevelling.Source = c => c.magnitude < 0.5f ? CaveFloor : null;
            SetPathfinder(new RoadPathfinder(_world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
            RoadNetworkGenerator.EntryStairLayouts = 0;

            Assert.False(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, 120f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the cave"));
            int first = RoadNetworkGenerator.EntryStairLayouts;
            Assert.True(first > 0);
            Assert.False(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, 120f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the cave again"));
            Assert.Equal(first, RoadNetworkGenerator.EntryStairLayouts);

            RoadNetworkGenerator.EntryStairsPerPlace = 0;
            Assert.False(RoadNetworkGenerator.GenerateRoad(new Vector2(120f, 60f), 0f, new Vector2(0f, 0f), 10f, 4f, "from elsewhere"));
            Assert.Equal(first, RoadNetworkGenerator.EntryStairLayouts);
        }
        finally { RoadNetworkGenerator.EntryStairsPerPlace = perPlace; }
    }

    /// <summary>The level ground with the cave on an islet: deep sea from 30 to 80 m out all round.</summary>
    private sealed class Islet : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            float r = new Vector2(wx, wy).magnitude;
            return Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f || (r > 30f && r < 80f) ? 0f : 50f;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    /// <summary>A road from the north to a place facing east comes in on the east, its landing's side.</summary>
    [Fact]
    public void ARoadToAPlacedPlaceComesInOnItsLandingsSide()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        PlacedSites.Reset();
        PlacedSites.Ready(Cave());
        LocationLevelling.Source = c => c.magnitude < 0.5f ? CaveFloor : null;
        SetPathfinder(new RoadPathfinder(_world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });

        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, 120f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the cave"));

        Vector2 nearest = RoadSpatialGrid.GetRoadPointsNearPosition(Vector3.zero, 60f).Where(p => p.IsRoad)
            .OrderBy(p => p.p.magnitude).First().p;
        Assert.True(nearest.x > Mathf.Abs(nearest.y), $"the road's nearest point to the cave is {nearest}");
    }
}
