using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public partial class LocationsFirstTests
{
    /// <summary>A cave on a knoll 28 m across at 80, the plain at 34 round it, and a spur running east from under the knoll's
    /// edge: its crest falls <see cref="Steep"/> from 72 at x 11 for 25 m, then 0.3 to the plain; its sides fall
    /// <see cref="Flank"/> from 2 m off the crest. The road up the spur ends on its crest, under the cave's landing.</summary>
    private sealed class SpurToAKnoll : WorldGenerator
    {
        public const float Plain = 34f, Knoll = 80f, KnollRadius = 14f;
        public float Steep = 0.5f, Flank = 0.5f, SteepLength = 25f;
        public float Crest(float x) => x <= 11f + SteepLength ? 72f - Steep * (x - 11f) : Mathf.Max(Plain, 72f - Steep * SteepLength - 0.3f * (x - 11f - SteepLength));
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            if (new Vector2(wx, wy).magnitude < KnollRadius) return Knoll;
            if (wx <= 0f) return Plain;
            return Mathf.Max(Plain, Crest(wx) - Flank * Mathf.Max(0f, Mathf.Abs(wy) - 2f));
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    /// <summary>The frost cave at the origin on the knoll, facing the spur.</summary>
    private static PlacedLocation CaveOnTheKnoll(SpurToAKnoll world)
    {
        var record = new PlacedLocationRecord
        {
            Prefab = "MountainCave02", Centre = new Vector2(0f, 0f), Yaw = 90f, Root = SpurToAKnoll.Knoll, RockRadius = 0f,
            Ops = new List<LevelOp>(CaveFloor), X0 = -60, Z0 = -60, W = 121, H = 121,
        };
        Func<float, float, float> grid = PlacedLocationGround.Sampler(record.Ground((x, z) => world.GetHeight(x, z)), record.X0, record.Z0, record.W, record.H);
        Func<float, float, float> ground = (x, z) => grid(x, z) is float h && h < PlacedLocationGround.OffGrid ? h : world.GetHeight(x, z);
        return new PlacedLocation { Record = record, Ground = ground, Shape = PlacedLocationShape.Of(record.Centre, 90f, CaveFloor[0], 0f, null, ground, false, record.Root) };
    }

    /// <summary>Where the road up the spur ends, on its crest 6 m out from the knoll.</summary>
    private const float RoadEnd = 20f;

    /// <summary>The road west along the spur's crest from x 140 to its end at <see cref="RoadEnd"/> under the landing, planned
    /// through the one pipeline (PlanFromPath) to the placed cave, then carried on onto its landing (PlanEntry).</summary>
    private (bool entered, RoadNetworkGenerator.RoadPlan planned, RoadStairSpan? span, string why) UpTheSpur(SpurToAKnoll world, PlacedLocation cave)
    {
        _scope.WithWorld(world);
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        StairStructures.Reset();
        PlacedLocations.Reset();
        PlacedLocations.Ready(cave);
        LocationLevelling.Source = c => c.magnitude < 0.5f ? CaveFloor : null;
        var finder = new RoadPathfinder(world) { Stairs = false, TerrainVarianceThreshold = 1000f, Meander = 0f };
        SetPathfinder(finder);
        var path = new List<Vector2>();
        for (float x = 140f; x >= RoadEnd; x -= RoadPathfinder.CellSize) path.Add(new Vector2(x, 0f));
        var planned = RoadNetworkGenerator.PlanFromPath(finder, path, path[0], 0f, cave.Centre, 10f, false, true, false, false, 4f);
        Assert.True(planned.Plans != null, "the road up the spur is not planned: " + RoadSpatialGrid.LastRefusal);
        bool entered = RoadNetworkGenerator.PlanEntry(cave, false, planned.Path, planned.Plans!, planned.Stairs, planned.Crossings, 4f,
            out _, out RoadStairSpan? span, out string why);
        return (entered, planned, span, why);
    }

    /// <summary>The longest stretch, metres, of the road's last 40 m (its end ramp to the staircase's foot) standing more than a
    /// metre over its own ground and the ground a metre out from both its edges, and its worst point.</summary>
    private static (float length, Vector2 at, float over) RidgeToTheFoot(List<RoadSpatialGrid.PlannedPath> plans, Func<float, float, float> ground)
    {
        RoadSpatialGrid.PlannedPath last = plans[plans.Count - 1];
        float side = RoadNetworkGenerator.RoadWidth / 2f + 1f;
        (float length, Vector2 at, float over) longest = (0f, default, 0f);
        float run = 0f, worst = 0f, back = 0f;
        Vector2 worstAt = default;
        for (int i = last.Points.Count - 1; i >= 0 && back <= 40f; i--)
        {
            Vector2 p = last.Points[i];
            if (i < last.Points.Count - 1) back += Vector2.Distance(last.Points[i + 1], p);
            Vector2 along = (last.Points[Mathf.Min(i + 1, last.Points.Count - 1)] - last.Points[Mathf.Max(0, i - 1)]).normalized;
            Vector2 across = new(-along.y, along.x);
            Vector2 l = p + across * side, r = p - across * side;
            float h = last.Heights[i];
            float over = Mathf.Min(h - ground(p.x, p.y), Mathf.Min(h - ground(l.x, l.y), h - ground(r.x, r.y)));
            if (over <= 1f) { run = 0f; worst = 0f; continue; }
            if (i < last.Points.Count - 1) run += Vector2.Distance(last.Points[i + 1], p);
            if (over > worst) (worst, worstAt) = (over, p);
            if (run > longest.length) longest = (run, worstAt, worst);
        }
        return longest;
    }

    /// <summary>A road up a spur whose crest climbs 0.5 for 25 m to under a cave's landing, over the road's cap: held to its end
    /// on the crest, the road stood on fill on the line climbing to it at the cap, 2.4 m over its ground and both sides for
    /// 35 m, and the staircase onto the landing set off from the top of that ridge (StoneTowerRuins05 at -4801,1727: the road a
    /// ridge below the stairs). The staircase starts lower instead, where the road climbs to it on the spur's crest, its foot on
    /// its ground.</summary>
    [Fact]
    public void AStaircaseOntoAPlacedLandingStartsBelowARidgeItsRoadWouldStandOn()
    {
        var world = new SpurToAKnoll { Flank = 0.2f };
        PlacedLocation cave = CaveOnTheKnoll(world);
        var (entered, planned, span, why) = UpTheSpur(world, cave);

        Assert.True(entered, why);
        Assert.NotNull(span);
        var ridge = RidgeToTheFoot(planned.Plans!, cave.Ground);
        Assert.True(ridge.length < RoadStairRuns.FootRidgeLength, FormattableString.Invariant(
            $"the road to the staircase's foot at {span!.FromPoint} stands over its ground and both sides for {ridge.length:F1} m, {ridge.over:F1} m at ({ridge.at.x:F1}, {ridge.at.y:F1})"));
        // Refused from the road's end and from each cut back along it, the ridge under all three.
        Assert.Equal(RoadNetworkGenerator.EntryCutBacks.Length, RoadStairRuns.RidgesToFeet);
        Assert.Equal(1, RoadStairRuns.FeetStartedLower);
        // Its foot lower down the spur, where the road ends now, on the ground the road arrives on.
        Vector2 foot = span!.FromPoint;
        Assert.Equal(planned.Path[planned.Path.Count - 1], foot);
        Assert.True(foot.x > RoadEnd + RoadPathfinder.CellSize, $"the staircase starts at {foot}");
        Assert.True(Mathf.Abs(span.FromHeight - cave.Ground(foot.x, foot.y)) < RoadStairRuns.LocationFootTolerance, FormattableString.Invariant(
            $"the staircase's foot at {span.FromHeight:F1}, its ground {cave.Ground(foot.x, foot.y):F1}"));
    }

    /// <summary>The spur's sides falling 0.5 and its crest climbing 0.5 for 40 m: no staircase from lower down its narrow crest
    /// reaches the landing, and none is laid from the top of the ridge either. The road gets no way onto the landing; the
    /// planner tries its other candidates. (Steep for 25 m, a staircase from lower down climbs onto the knoll's lip.)</summary>
    [Fact]
    public void NoStaircaseOntoAPlacedLandingIsLaidFromARidgeItsRoadWouldStandOn()
    {
        var world = new SpurToAKnoll { SteepLength = 40f };
        PlacedLocation cave = CaveOnTheKnoll(world);
        var (entered, _, span, why) = UpTheSpur(world, cave);

        Assert.False(entered, $"laid from {span?.FromPoint}");
        Assert.Null(span);
        Assert.Contains(RoadStairRuns.RidgeToFootRefusal + " on a ridge", why);
        Assert.Equal(0, RoadStairRuns.FeetStartedLower);
    }

    /// <summary>The cave's level landings in front of its mouth stand 9 m inside the knoll's lip, deeper on the landing than an
    /// approach may come (RoadStairRuns.PastTheLip): each is aimed at the lip cell nearest it instead, and they stay apart
    /// round the lip.</summary>
    [Fact]
    public void ALandingDeeperThanItsLipIsAimedAtTheLipNearestIt()
    {
        var world = new SpurToAKnoll { Steep = 0.3f };
        PlacedLocation cave = CaveOnTheKnoll(world);
        var landing = cave.Shape.ConnectedLanding(cave.Ground, 1f);
        Assert.All(landing.Landings, l => Assert.False(RoadStairRuns.WithinLip(landing.Depth(l.at)), $"the landing at {l.at} is within the lip"));
        var aims = cave.Shape.Aims(cave.Ground, new Vector2(RoadEnd, 0f), 1f, RoadStairRuns.AimTries, landing);
        Assert.Equal(landing.Landings.Count, aims.Count);
        foreach (var (at, _) in landing.Landings)
        {
            var nearest = landing.Lip.OrderBy(l => Vector2.Distance(l.at, at)).First().at;
            Assert.Contains(aims, a => a.at == nearest);
        }
        Assert.All(aims, a => Assert.Equal(0f, landing.Depth(a.at)));
    }

    /// <summary>The crest climbing 0.3, within the road's cap: the road climbs to its end on the crest, no ridge to the foot. From
    /// the road's end the staircase onto the landing turns square off it and its first flight crosses the road's end, with no
    /// room on the ring at the road's end on the narrow crest (RoadEndRing): the road may be cut back along the crest and the
    /// staircase sets off from its new end, not lower.</summary>
    [Fact]
    public void AStaircaseOntoAPlacedLandingItsRoadClimbsToStartsAtTheRoadsEnd()
    {
        var world = new SpurToAKnoll { Steep = 0.3f };
        PlacedLocation cave = CaveOnTheKnoll(world);
        var (entered, planned, span, why) = UpTheSpur(world, cave);

        Assert.True(entered, why);
        Assert.NotNull(span);
        Assert.Equal(0, RoadStairRuns.FeetStartedLower);
        Assert.Equal(0, RoadStairRuns.RidgesToFeet);
        Vector2 end = planned.Path[planned.Path.Count - 1];
        Assert.Equal(end, span!.FromPoint);
        Assert.Equal(0f, end.y);
        Assert.InRange(end.x, RoadEnd - 0.5f, RoadEnd + RoadNetworkGenerator.EntryCutBacks[RoadNetworkGenerator.EntryCutBacks.Length - 1] + 0.5f);
        Assert.True(TowerPlacedFirstTests.AcrossItsRoad(span.Plan, end, 270f, span.FromHeight) == "", TowerPlacedFirstTests.AcrossItsRoad(span.Plan, end, 270f, span.FromHeight));
        Assert.False(new RoadEndRing(end, 270f, span.FromHeight).Intrudes(span.Plan, out string inside), inside);
        Assert.True(RidgeToTheFoot(planned.Plans!, cave.Ground).length < RoadStairRuns.FootRidgeLength);
    }
}
