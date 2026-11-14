using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;
using Valheim.Testing;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// A staircase from a road's end at a location up to the location's flat ground
/// (a frost cave's apron).
/// </summary>
public class LocationApproachStairTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    /// <summary>A location's flat apron at 52 m out to 12 m from the origin, a face of
    /// 1.2 below it down to 40 m at 22 m out, flat ground at 40 m beyond, sea past 200 m.</summary>
    private sealed class ApronWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            float r = new Vector2(wx, wy).magnitude;
            return 40f + Mathf.Clamp((22f - r) * 1.2f, 0f, 12f);
        }
        /// <summary>The Mountains, where a location gets an approach staircase (<see cref="RoadNetworkGenerator.ApproachStairsAt"/>).</summary>
        public Heightmap.Biome Biome = Heightmap.Biome.Mountain;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Biome;
        public override int GetSeed() => 77;
    }

    private const float LevelRadius = 12f;
    private const float Footprint = 20f;
    private static readonly IReadOnlyList<LevelOp> Apron = new[] { new LevelOp(0f, 0f, 0f, LevelRadius, false) };
    private readonly ApronWorld _world = new();

    public LocationApproachStairTests()
    {
        _scope.WithWorld(_world);
        RoadNetworkGenerator.Reset();
        // A location another class left placed (PlacedLocations is static) would be read as this apron's (its landing height).
        PlacedLocations.Reset();
        // Every location in the Mountains is placed first (PlacedLocations.NeedsPlacing): on the preload list, its ground published
        // as the preload publishes it (the location's own levelling over the natural ground, PlacedLocationGround.Build), read
        // once a test has set the location's levelling.
        Func<float, float, float>? built = null;
        PlacedLocations.Preload(new Vector2(0f, 0f), (x, z) => (built ??= PreloadedGround())(x, z));
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), Footprint) });
    }

    /// <summary>The ground the preload would publish for the location at the origin: its levelling (LocationLevelling.OpsAt) over
    /// the natural ground, past its grid the natural ground.</summary>
    private static Func<float, float, float> PreloadedGround()
    {
        WorldGenerator world = WorldGenerator.instance!;
        IReadOnlyList<LevelOp>? ops = LocationLevelling.OpsAt(new Vector2(0f, 0f));
        if (ops == null) return (x, z) => world.GetHeight(x, z);
        const int n = 40;
        float root = LocationLevelling.CentreHeight(new Vector2(0f, 0f), world);
        float[] heights = PlacedLocationGround.Build(-n, -n, 2 * n + 1, 2 * n + 1, new Vector2(0f, 0f), root, 0f, ops, (x, z) => world.GetHeight(x, z));
        Func<float, float, float> grid = PlacedLocationGround.Sampler(heights, -n, -n, 2 * n + 1, 2 * n + 1);
        return (x, z) => grid(x, z) is float h && h < PlacedLocationGround.OffGrid ? h : world.GetHeight(x, z);
    }

    public void Dispose()
    {
        SetPathfinder(null);
        LocationLevelling.Source = null;
        PlacedLocations.Reset();
        RoadLocationProtection.Reset();
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, pathfinder);

    private RoadStairSpan? Lay(Vector2 end, IReadOnlyList<LevelOp>? ops, out string why)
    {
        var path = new List<Vector2> { end + new Vector2(-30f, 0f), end };
        return RoadStairRuns.LayLocationApproach(end, _world.GetHeight(end.x, end.y), StairLayout.Bearing(path[0], end),
            new Vector2(0f, 0f), 52f, ops, false, path, 1, new List<RoadStairSpan>(), _world, out why);
    }

    /// <summary>
    /// An approach trial counts nothing a road's approach counts (RoadStairRuns.LayLocationApproach with tally false, as
    /// RoadNetworkGenerator.TrialBuilds lays them): laid for a trial, the approach up to the apron leaves the road's counts
    /// (its layouts, aims buried, laid beside their aim, facing reruns, laid not facing in, location checks across its road) as
    /// they were, and is counted among the trials' layouts; laid for a road, it is counted among the road's.
    /// </summary>
    [Fact]
    public void AnApproachLaidForATrialCountsNothingOfARoads()
    {
        Vector2 end = new(-24f, 0f);
        var path = new List<Vector2> { end + new Vector2(-30f, 0f), end };
        RoadStairSpan? Laid(bool tally) => RoadStairRuns.LayLocationApproach(end, _world.GetHeight(end.x, end.y), StairLayout.Bearing(path[0], end),
            new Vector2(0f, 0f), 52f, Apron, false, path, 1, new List<RoadStairSpan>(), _world, out _, tally);
        (int, int, int, int, int, int) Roads() => (RoadStairRuns.ApproachLayouts, RoadStairRuns.AimsBuried, RoadStairRuns.LaidBesideTheirAim,
            RoadStairRuns.ApproachFacingReruns, RoadStairRuns.ApproachesNotFacingIn, RoadStairRuns.LocationChecksAcross);
        var before = Roads();
        int trials = RoadStairRuns.TrialApproachLayouts;
        Assert.NotNull(Laid(tally: false));
        Assert.Equal(before, Roads());
        Assert.True(RoadStairRuns.TrialApproachLayouts > trials, "no trial layout counted");
        Assert.NotNull(Laid(tally: true));
        Assert.True(RoadStairRuns.ApproachLayouts > before.Item1, "no road layout counted");
    }

    [Fact]
    public void ARoadEndBelowALocationsApronGetsAStaircaseUpToIt()
    {
        RoadStairSpan? span = Lay(new Vector2(-24f, 0f), Apron, out string why);

        Assert.True(span != null, why);
        StairPlan plan = span!.Plan;
        Assert.False(plan.Reversed);
        Assert.Equal(new Vector2(-24f, 0f), span.FromPoint);
        // It ends on the apron, at the apron's height, just inside its edge.
        Assert.InRange(plan.End.y, 52f - StairLayout.ReachHeight, 52f + StairLayout.ReachHeight);
        float reached = new Vector2(plan.End.x, plan.End.z).magnitude;
        // At its edge: no further onto the apron than its lip, and short of it by no more than the step off its top
        // (StairLayout.ProudReach): a walker stepping straight off its middle lands on the flat. It ends 0.25 m short of the
        // edge, where it was 5 cm onto the flat when a piece's burial was read at its inside only: that layout buried a turn's
        // filler 0.78 m and a stair 0.47 m at their edges (StairLayout.Cover).
        Assert.InRange(reached, LevelRadius - RoadStairRuns.LipReach, LevelRadius + StairLayout.ProudReach);
        Assert.True(StairLayout.Terminal(plan) is { } exit && plan.End.y - _world.GetHeight(exit.Edge.x + exit.Direction.x * StairLayout.ProudReach,
            exit.Edge.z + exit.Direction.y * StairLayout.ProudReach) <= StairLayout.StepUp, "its top stands over a drop");
        // Nothing walked far onto the apron.
        foreach (StairPiece piece in plan.Pieces.Where(p => p.Kind != StairPieceKind.Support))
            Assert.True(new Vector2(piece.Position.x, piece.Position.z).magnitude >= LevelRadius - RoadStairRuns.LipReach,
                $"{piece.Kind} at {piece.Position}");
        float buried = RoadStairRuns.Cover(plan, (x, z) => _world.GetHeight(x, z));
        Assert.True(buried <= StairCover.ApproachRefusal, $"buried {buried:F2} m");
    }

    [Fact]
    public void ARoadEndAboveTheApronGetsAStaircaseDownToIt()
    {
        // The same location, sunk into the ground: the road arrives above it, 25 m out (from 24 m the top arrived on the road's
        // end's ring only within the search's old band round the circle; an arrival is the start's precise target now,
        // RoadEndRing.Allows, decision 6j).
        var sunk = new[] { new LevelOp(0f, 0f, -12f, LevelRadius, false) };
        var world = new SunkWorld();
        _scope.WithWorld(world);
        var end = new Vector2(-25f, 0f);
        var path = new List<Vector2> { end + new Vector2(-30f, 0f), end };
        RoadStairSpan? span = RoadStairRuns.LayLocationApproach(end, world.GetHeight(end.x, end.y), StairLayout.Bearing(path[0], end),
            new Vector2(0f, 0f), 52f, sunk, false, path, 1, new List<RoadStairSpan>(), world, out string why);

        Assert.True(span != null, why);
        // Laid from the pit floor up: its top, with the landing the road meets, at the road's end.
        Assert.True(span!.Plan.Reversed);
        Assert.InRange(Vector2.Distance(span.FromPoint, end), 0f, StairLayout.ReachRadius + StairLayout.PieceRun);
        Assert.InRange(span.FromHeight, 52f - StairLayout.ReachHeight, 52f + StairLayout.ReachHeight);
        Assert.Equal(40f, span.ToHeight, 2);
        float bottom = span.Plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair).Min(p => p.Position.y);
        Assert.InRange(bottom, 40f - 2f * StairLayout.PieceRise, 40f);
    }

    /// <summary>52 m flat, falling at 1.2 inside 22 m to a pit floor of 40 m; the location's centre stands at 52.</summary>
    private sealed class SunkWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            float r = new Vector2(wx, wy).magnitude;
            return 52f - Mathf.Clamp((22f - r) * 1.2f, 0f, 12f);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    [Fact]
    public void NoStaircaseWithoutAKnownApronOrWhereTheRoadIsAlreadyLevelWithIt()
    {
        Assert.Null(Lay(new Vector2(-24f, 0f), null, out string why));
        Assert.Equal("no levelling known", why);
        // Two levels: no single apron to aim for.
        Assert.Null(Lay(new Vector2(-24f, 0f), new[] { Apron[0], new LevelOp(0f, 0f, 3f, 6f, false) }, out why));
        Assert.Equal("no single flat level", why);
        // An apron at the road's own height.
        Assert.Null(Lay(new Vector2(-24f, 0f), new[] { new LevelOp(0f, 0f, -12f, LevelRadius, false) }, out why));
        Assert.Equal("level enough", why);
        Assert.Null(Lay(new Vector2(-70f, 0f), Apron, out why));
        Assert.Equal("too far", why);
    }

    /// <summary>
    /// A staircase's location check reads its road's end at the height the road was planned to there, as its layout did: on the
    /// ground 1.5 m over a road cut down to its end, a flight climbing 60 degrees to the right off the road's middle read as a
    /// top, leaving the road 120 degrees to its left, and its first stair, on the right of the road's line, was across it.
    /// Changed: judged by the ring at the road's end (RoadEndRing) there is no top-or-foot guess: from the road's middle it is
    /// across its road at either height, from the ring on its own heading at neither.
    /// </summary>
    [Fact]
    public void ALocationCheckReadsTheRoadsEndAtItsPlannedHeight()
    {
        var path = new List<Vector2> { new(-54f, 0f), new(-24f, 0f) };
        var plan = new StairPlan { Reached = true, StartHeading = 150f };
        Vector2 end = path[1], up = StairLayout.Forward(150f);
        var ringed = new StairPlan { Reached = true, StartHeading = 150f };
        foreach (var (along, y) in new[] { (1f, 38.2f), (3f, 38.7f), (5f, 39.2f) })
        {
            plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Stair, Prefab = "stone_stair", Position = new Vector3((end + up * along).x, y, (end + up * along).y), YawDegrees = 330f });
            Vector2 on = end + up * (along + 1.5f);
            ringed.Pieces.Add(new StairPiece { Kind = StairPieceKind.Stair, Prefab = "stone_stair", Position = new Vector3(on.x, y, on.y), YawDegrees = 330f });
        }
        float Ground(float x, float z) => _world.GetHeight(x, z);
        var none = new List<RoadStairSpan>();
        Assert.Equal(RoadStairRuns.AcrossItsRoad, RoadStairRuns.LocationConflict(plan, new Vector2(0f, 0f), path, 1, Ground, _world, none, null, 90f, 38.5f));
        Assert.Equal(RoadStairRuns.AcrossItsRoad, RoadStairRuns.LocationConflict(plan, new Vector2(0f, 0f), path, 1, Ground, _world, none, null, 90f));
        Assert.Equal("", RoadStairRuns.LocationConflict(ringed, new Vector2(0f, 0f), path, 1, Ground, _world, none, null, 90f, 38.5f));
        Assert.Equal("", RoadStairRuns.LocationConflict(ringed, new Vector2(0f, 0f), path, 1, Ground, _world, none, null, 90f));
    }

    [Fact]
    public void ItMayCrossItsOwnLocationsFootprintButNoOther()
    {
        // Another location's footprint over the face the staircase must climb.
        RoadLocationProtection.Set(new[]
        {
            new RoadLocationProtection.Footprint(new Vector2(0f, 0f), Footprint),
            new RoadLocationProtection.Footprint(new Vector2(-17f, 0f), 4f),
            new RoadLocationProtection.Footprint(new Vector2(-17f, 6f), 4f),
            new RoadLocationProtection.Footprint(new Vector2(-17f, -6f), 4f),
            new RoadLocationProtection.Footprint(new Vector2(-12f, 12f), 5f),
            new RoadLocationProtection.Footprint(new Vector2(-12f, -12f), 5f),
        });
        Assert.Null(Lay(new Vector2(-24f, 0f), Apron, out string why));
        Assert.StartsWith("another location at (", why);
    }

    /// <summary>The ground a location the game places after its roads is expected to leave is the game's own build,
    /// vertex by vertex (<see cref="PlacedLocationGround.Build"/>): a square flat of radius 4.5 covers the whole 5 vertex box, a
    /// round one the vertices within its radius, the smoothing counted in whole vertices from the vertex nearest the centre.</summary>
    [Fact]
    public void ThePlacedGroundALocationIsExpectedToLeaveIsTheVertexExactBuild()
    {
        var centre = new Vector2(0.4f, 0f);
        var ops = new[] { new LevelOp(0f, 0f, 1f, 4.5f, true, 10f, 3f), new LevelOp(0f, 0f, 0f, 3f, false) };
        Func<float, float, float> expected = RoadStairRuns.ApproachGround(centre, 50f, ops, _world);
        float[] built = PlacedLocationGround.Build(-12, -12, 25, 25, centre, 50f, 0f, ops, (x, z) => _world.GetHeight(x, z));
        for (int z = -12; z <= 12; z++)
            for (int x = -12; x <= 12; x++)
                Assert.Equal(built[(z + 12) * 25 + x + 12], expected(x, z), 3);
        // The square reaches the fifth vertex out (4.5 m rounds up), and the vertex beyond it is only smoothed.
        Assert.Equal(51f, expected(5f, 5f), 3);
        Assert.NotEqual(51f, expected(6f, 0f), 3);
        // Between vertices, as the game's mesh joins them.
        Assert.Equal((built[12 * 25 + 12 + 7] + built[12 * 25 + 12 + 8]) / 2f, expected(7.5f, 0f), 3);
        // An operation off the centre is left out: where it lies depends on the rotation.
        Func<float, float, float> offCentre = RoadStairRuns.ApproachGround(centre, 50f, new[] { new LevelOp(4f, 0f, 2f, 10f, false, 20f, 3f) }, _world);
        Assert.Equal(PlacedLocations.GroundAt(3f, 0f, _world), offCentre(3f, 0f), 3);
    }

    /// <summary>Every reader of the ground such a location leaves reads that one build: a road's arrival height at its edge,
    /// the carry-on to it, the scoring of its approach and the end held to its flat.</summary>
    [Fact]
    public void EveryReaderOfALocationNotPlacedFirstReadsTheVertexExactBuild()
    {
        var centre = new Vector2(0f, 0f);
        var ops = new[] { new LevelOp(0f, 0f, 3f, 4.5f, true, 12f, 3f) };
        LocationLevelling.Source = c => c.magnitude < 0.5f ? ops : null;
        float root = LocationLevelling.CentreHeight(centre, _world);
        Func<float, float, float> expected = RoadStairRuns.ApproachGround(centre, root, ops, _world);
        // The end 4.9 m out on the axis: on the fifth vertex, which the square levels.
        Assert.Equal(expected(4.9f, 0f), RoadNetworkGenerator.LocationGround(new Vector2(4.9f, 0f), centre, 32f)!.Value, 3);
        Assert.Equal(root + 3f, RoadNetworkGenerator.LocationGround(new Vector2(4.9f, 0f), centre, 32f)!.Value, 3);
    }

    [Fact]
    public void AGeneratedRoadToTheLocationClimbsTheLastStretchOnStairs()
    {
        LocationLevelling.Source = c => c.magnitude < 0.5f ? Apron : null;
        SetPathfinder(new RoadPathfinder(_world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });

        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-120f, 0f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the apron"));

        StairRun run = Assert.Single(StairStructures.Runs);
        float top = run.Pieces.Where(p => p.Kind != StructurePieceKind.StairSupport).Max(p => p.Position.y);
        Assert.InRange(top, 51f, 52.5f);
        // It starts where the road stops: road points within a couple of metres of its foot, at its height.
        var foot = new Vector3(run.From.x, 0f, run.From.y);
        Assert.Contains(RoadSpatialGrid.GetRoadPointsNearPosition(foot, 2f), p => p.IsRoad && Mathf.Abs(p.h - 40f) < 0.6f);
    }

    /// <summary>Where the approach starts at the road's end, the road is levelled under its start (RoadEndRing.PadUnder, laid
    /// with the road by RoadStairRuns.StartPads): unpainted road points at its foot's height round the road's end and under
    /// its first stair.</summary>
    [Fact]
    public void AGeneratedRoadsApproachStartsOnALevelPad()
    {
        LocationLevelling.Source = c => c.magnitude < 0.5f ? Apron : null;
        SetPathfinder(new RoadPathfinder(_world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });

        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-120f, 0f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the apron"));

        StairRun run = Assert.Single(StairStructures.Runs);
        var foot = new Vector3(run.From.x, 0f, run.From.y);
        var pad = RoadSpatialGrid.GetRoadPointsNearPosition(foot, 4f).FindAll(p => p.IsRoad && p.unpainted);
        Assert.True(pad.Count > 20, $"{pad.Count} pad point(s) at the foot");
        Assert.All(pad, p => Assert.InRange(p.h, pad[0].h - 0.01f, pad[0].h + 0.01f));
        Assert.InRange(pad[0].h, 39.4f, 40.6f);
    }

    /// <summary>Ground falling at 0.8 towards +x from 12 m behind to 14 m in front, then at 0.3 (where a
    /// road can reach); level behind (a road can end there too); sea past 200 m.</summary>
    private sealed class HillsideWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f ? 0f : 50f - 0.8f * Mathf.Clamp(wx, -12f, 14f) - 0.3f * Mathf.Max(wx - 14f, 0f);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    /// <summary>MountainCave02's one levelling operation, as road_sites_dump read it off the prefab:
    /// a floor 4 m across, 4.75 m behind the centre and 4.09 m below it, smoothed out to 7 m.</summary>
    private static readonly IReadOnlyList<LevelOp> CaveFloor = new[] { new LevelOp(-0.06f, -4.75f, -4.09f, 4f, false, 7f, 3f) };

    /// <summary>Ground at a frost cave turned to face +x (yaw 90), as the game leaves it: the
    /// hillside, with the cave's floor levelled and smoothed 4.75 m behind the centre.</summary>
    private static float CaveGround(float x, float z)
    {
        float h = new HillsideWorld().GetHeight(x, z);
        LevelOp op = CaveFloor[0];
        Vector2 floor = LocationLevelling.OffsetAt(op, 90f);
        float d = Vector2.Distance(new Vector2(x, z), floor);
        float level = 50f + op.Height;
        if (d <= op.Radius) h = level;
        if (d <= op.SmoothRadius) h = Mathf.Lerp(h, level, 1f - Mathf.Pow(d / op.SmoothRadius, op.SmoothPower));
        return h;
    }

    private static RoadStairSpan? PlanAtCave(Vector2 end, out string why, float rock = 0f)
    {
        Vector2 before = end + end.normalized * 30f;
        var request = new LocationApproachRequest
        {
            RoadEnd = end, RoadHeight = CaveGround(end.x, end.y), Arriving = StairLayout.Bearing(before, end),
            Centre = new Vector2(0f, 0f), RoadTail = new List<Vector2> { before, end },
        };
        // Its own cave ground, not the lattice: in the footprint the lattice reads the location this class preloads at the origin
        // (the apron's), which is not this cave.
        return RoadStairRuns.PlanPlacedLocationApproach(request, 90f, CaveFloor, RoadStairRuns.LayoutGround.Own(CaveGround), (x, z) => Heightmap.Biome.Mountain, 77, out why, rock);
    }

    /// <summary>A way onto a landing whose legs ask what holds up their pieces of the answers its other legs gave on its ground
    /// (StairSupport.Answers, kept while the generation keeps memory for speed) is the way laid choosing every piece afresh,
    /// field for field; some of its legs' pieces were answered, not chosen again.</summary>
    [Fact]
    public void AnApproachsLegsLaidOnItsAnswersAreThoseChosenAfresh()
    {
        bool low = GenerationMemory.Low;
        try
        {
            GenerationMemory.Low = true;
            RoadStairRuns.ResetCache();
            long before = StairSupport.AnswersReused;
            RoadStairSpan? afresh = PlanAtCave(new Vector2(24f, 0f), out string why);
            Assert.True(afresh != null, why);
            Assert.Equal(before, StairSupport.AnswersReused);
            GenerationMemory.Low = false;
            RoadStairRuns.ResetCache();
            RoadStairSpan? answered = PlanAtCave(new Vector2(24f, 0f), out why);
            var differences = StairRowLayTests.Differences(afresh, answered);
            Assert.True(differences.Count == 0, string.Join("; ", differences));
            Assert.True(StairSupport.AnswersReused > before, "no leg of the approach was answered");
        }
        finally { GenerationMemory.Low = low; }
    }

    /// <summary>An approach laid out up the face another approach of the same location climbs (a way in it has, LocationWaysIn) is
    /// refused at each aim (RoadStairRuns.DuplicatesAWayIn, read in the layout's own refusal): the next aim or start is tried,
    /// so what is laid, if anything, arrives another way, rather than the same twin laid and refused after the layout from
    /// every start.</summary>
    [Fact]
    public void AnApproachUpTheFaceAnotherClimbsIsRefusedAtItsAim()
    {
        using var corridor = StaticOverride.Keep(() => RoadNetworkGenerator.RoadSnap).AndKeep(() => RoadNetworkGenerator.CorridorSnap);
        RoadNetworkGenerator.RoadSnap = 6f;
        RoadNetworkGenerator.CorridorSnap = 14f;
        StairStructures.Reset();
        LocationWaysIn.Reset();
        var end = new Vector2(24f, 0f);
        RoadStairSpan? first = PlanAtCave(end, out string why);
        Assert.True(first != null, why);
        // The same staircase 6 m to the side, laid before as the cave's approach.
        var beside = new StairPlan { Kit = first!.Plan.Kit, Reached = true };
        foreach (StairPiece p in first.Plan.Pieces)
            beside.Pieces.Add(new StairPiece { Kind = p.Kind, Prefab = p.Prefab, Position = p.Position + new Vector3(0f, 0f, 6f), YawDegrees = p.YawDegrees });
        beside.End = first.Plan.End + new Vector3(0f, 0f, 6f);
        StairRun twin = StairRun.FromPlan(first.FromPoint + new Vector2(0f, 6f), first.ToPoint + new Vector2(0f, 6f), RoadStairRuns.Width, beside);
        twin.KeepsLocationRocks = true;
        twin.ExpectedLocation = (new Vector2(0f, 0f), 50f);
        StairStructures.Add(twin);
        try
        {
            // The first layout is the twin's way in: the rule holds it.
            Assert.True(RoadStairRuns.DuplicatesAWayIn(first.Plan, new Vector2(0f, 0f)));

            RoadStairSpan? second = PlanAtCave(end, out why);

            if (second == null) Assert.Contains(RoadStairRuns.BesideAnApproach, why);
            else Assert.False(RoadStairRuns.DuplicatesAWayIn(second.Plan, new Vector2(0f, 0f)), "laid as the twin's way in");
        }
        finally { StairStructures.Reset(); }
    }

    [Fact]
    public void AnOffCentreFloorWaitsForTheLocationToBePlaced()
    {
        var world = new HillsideWorld();
        _scope.WithWorld(world);
        var end = new Vector2(24f, 0f);
        var path = new List<Vector2> { new Vector2(54f, 0f), end };
        Assert.Null(RoadStairRuns.LayLocationApproach(end, world.GetHeight(end.x, end.y), 270f, new Vector2(0f, 0f), 50f, CaveFloor, true,
            path, 1, new List<RoadStairSpan>(), world, out string why));
        Assert.Equal(RoadStairRuns.NotPlacedBeforeItsRoad, why);
        Assert.Null(RoadStairRuns.LayLocationApproach(end, world.GetHeight(end.x, end.y), 270f, new Vector2(0f, 0f), 50f, CaveFloor, false,
            path, 1, new List<RoadStairSpan>(), world, out why));
        Assert.Equal("flat off its centre, turned at random", why);
    }

    [Fact]
    public void AStaircaseOntoALocationWithADoorLandingEndsAtTheDoor()
    {
        const string prefab = "Mistlands_DvergrBossEntrance1";
        Vector2 end = new Vector2(40f, 0f), before = end + end.normalized * 30f;
        var request = new LocationApproachRequest
        {
            RoadEnd = end, RoadHeight = CaveGround(end.x, end.y), Arriving = StairLayout.Bearing(before, end),
            Centre = new Vector2(0f, 0f), RoadTail = new List<Vector2> { before, end },
        };
        PlacedLocationShape shape = PlacedLocationShape.Of(request.Centre, 90f, CaveFloor[0], 0f, null, CaveGround, prefab: prefab);
        RoadStairSpan? span = RoadStairRuns.PlanPlacedLocationApproach(request, 90f, CaveFloor, RoadStairRuns.LayoutGround.Own(CaveGround), (x, z) => Heightmap.Biome.Mountain, 77,
            out string why, placed: shape);

        Assert.True(span != null, why);
        Vector2 top = new Vector2(span!.Plan.End.x, span.Plan.End.z);
        Assert.InRange(Vector2.Distance(top, shape.Door!.Value), 0f, RoadStairRuns.LedgeReach + RoadStairRuns.FinishReach);
    }

    [Fact]
    public void ARoadBelowAPlacedFrostCaveClimbsToTheLandingInFrontOfItsMouth()
    {
        RoadStairSpan? span = PlanAtCave(new Vector2(24f, 0f), out string why);

        Assert.True(span != null, why);
        StairPlan plan = span!.Plan;
        Assert.False(plan.Reversed);
        // It lands on the ground in front of the entrance: the floor is 4.75 m behind the centre
        // (facing +x) and smoothed out to 7 m; the landing is aimed 3 m beyond that.
        Vector2 floor = new Vector2(-4.75f, 0.06f);
        Vector2 landing = floor + new Vector2(7f + RoadStairRuns.LandingOut, 0f);
        Vector2 top = new Vector2(plan.End.x, plan.End.z);
        Assert.InRange(Vector2.Distance(top, landing), 0f, RoadStairRuns.LedgeReach + RoadStairRuns.FinishReach);
        Assert.InRange(plan.End.y, CaveGround(top.x, top.y) - StairLayout.ReachHeight, CaveGround(top.x, top.y) + StairLayout.ReachHeight);
        float buried = RoadStairRuns.Cover(plan, CaveGround);
        Assert.True(buried <= StairCover.ApproachRefusal, $"buried {buried:F2} m");
        // Never down in the entrance: no piece within the floor's smoothing.
        foreach (StairPiece piece in plan.Pieces.Where(p => p.Kind != StairPieceKind.Support))
            Assert.True(Vector2.Distance(new Vector2(piece.Position.x, piece.Position.z), floor) >= 7f, $"{piece.Kind} at {piece.Position} in the entrance");
        // It ends facing into the cave (towards -x), within the hold on how it arrives (StairLayout.ArrivalSteps): never away.
        float off = Mathf.Abs(((plan.EndHeading - 270f) % 360f + 540f) % 360f - 180f);
        Assert.True(off <= StairLayout.ArrivalSteps * StairLayout.HeadingStep + 0.01f, $"ends facing {plan.EndHeading}");
    }

    [Fact]
    public void FromBehindACaveTheStaircaseGoesRoundItsRockToTheFront()
    {
        const float rock = 7f;
        // The road ends uphill of the cave, behind it and off to one side.
        RoadStairSpan? span = PlanAtCave(new Vector2(-20f, 14f), out string why, rock);

        Assert.True(span != null, why);
        StairPlan plan = span!.Plan;
        // No piece in the cave's rock but in the cone in front of its floor (apex at the floor, 4.75 m behind the centre, facing +x).
        Vector2 floor = new Vector2(-4.75f, 0.06f);
        float cos = Mathf.Cos(RoadStairRuns.FrontCone * Mathf.Deg2Rad);
        foreach (StairPiece piece in plan.Pieces.Where(p => p.Kind != StairPieceKind.Support))
        {
            var at = new Vector2(piece.Position.x, piece.Position.z);
            bool inFront = Vector2.Dot((at - floor).normalized, new Vector2(1f, 0f)) >= cos;
            Assert.True(at.magnitude >= rock + RoadStairRuns.RockMargin || inFront, $"{piece.Kind} at {piece.Position} in the cave's rock");
        }
        // It comes to the cave's front: its location end is in front of the floor.
        Vector2 locationEnd = plan.Reversed ? new Vector2(span.FromPoint.x, span.FromPoint.y) : new Vector2(plan.End.x, plan.End.z);
        Vector2 caveEnd = plan.Reversed ? new Vector2(span.ToPoint.x, span.ToPoint.y) : locationEnd;
        Assert.True(caveEnd.x > floor.x, $"ends at {caveEnd}, not in front of the floor");
    }

    [Fact]
    public void WithoutItsRockKnownARoadBehindTheCaveIsStillLaidStraight()
    {
        // The keep-out is what leads it round: with none, nothing stops a straight line.
        Assert.NotNull(PlanAtCave(new Vector2(24f, 0f), out string why));
    }

    /// <summary>An approach that carries on from the top of the staircase its road ends on
    /// touches that staircase there, as staircases meeting head-on may: given the meeting
    /// point it is not refused "on another staircase" (a frost cave 13 m
    /// above a 38 m staircase's top got none). Without one, it still is.</summary>
    [Fact]
    public void AnApproachCarriesOnFromTheTopOfTheStaircaseItsRoadEndsOn()
    {
        // 100 m east of this class's location at the origin, clear of its footprint.
        float Slope(float x, float z) => 40f + 0.5f * z;
        Vector2 foot = new(100f, 0f);
        StairPlan below = StairLayout.Build(foot, foot + new Vector2(0f, 20f), Slope);
        Assert.True(below.Reached, below.Reason);
        Vector2 top = new(below.End.x, below.End.z);
        var laid = new RoadStairSpan { FromIndex = 0, ToIndex = 1, FromPoint = foot, FromHeight = Slope(foot.x, foot.y), ToPoint = top, ToHeight = below.End.y, Plan = below, Kit = StairKit.Stone };
        // In game the road ended 1.7 m back over the top row (at 3698.8,3293.0): the approach sets off from there.
        Vector2 roadEnd = top - StairLayout.Forward(below.EndHeading) * 1.7f;
        StairPlan on = StairLayout.Build(roadEnd, roadEnd + new Vector2(0f, 16f), Slope, arriving: below.EndHeading);
        Assert.True(on.Reached, on.Reason);
        var road = new List<Vector2> { foot + new Vector2(0f, -40f), foot + new Vector2(0f, -20f), roadEnd };
        var centre = roadEnd + new Vector2(0f, 60f);

        Assert.Equal("on another staircase", RoadStairRuns.LocationConflict(on, centre, road, road.Count - 1, Slope, _world, new[] { laid }));
        Assert.Equal("", RoadStairRuns.LocationConflict(on, centre, road, road.Count - 1, Slope, _world, new[] { laid }, meet: roadEnd));
    }

    /// <summary>The top row of a laid staircase is read back off its pieces: its far edge, the
    /// heading it walks and its walking height, whether it ends on a stair or on a floor carried
    /// on from the top; a road end at its foot is no top.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ALaidStaircasesTopRowIsFoundAtItsEnd(int topLandings)
    {
        float Slope(float x, float z) => 40f + 0.5f * z;
        Vector2 foot = new(100f, 0f);
        StairPlan plan = StairLayout.Build(foot, foot + new Vector2(0f, 20f), Slope, topLandings: topLandings);
        Assert.True(plan.Reached, plan.Reason);
        StairRun run = StairRun.FromPlan(foot, new Vector2(plan.End.x, plan.End.z), RoadStairRuns.Width, plan);
        var end = new Vector2(plan.End.x, plan.End.z);

        StairContinuation? top = RoadStairRuns.TopAt(end, plan.End.y, run.Pieces);

        Assert.True(top.HasValue);
        Assert.True(Vector2.Distance(top!.Value.Edge, end) < 0.05f, $"edge {top.Value.Edge}, end {end}");
        Assert.Equal(plan.End.y, top.Value.Height, 3);
        Assert.Equal(plan.EndHeading, top.Value.Heading, 3);
        Assert.Equal(topLandings > 0 ? StairPieceKind.Landing : StairPieceKind.Stair, top.Value.Kind);
        Assert.Null(RoadStairRuns.TopAt(foot, Slope(foot.x, foot.y), run.Pieces));
    }

    /// <summary>A cave's approach from the top of the staircase its road ends on carries on from that
    /// staircase's top row, off its snap edge: its first piece's back edge on the row's far edge at its
    /// walking height (or pivoting off it with a filler where it turns), within a turn's reach of the
    /// row's heading, and no sunk foot dug in under the row (MountainCave02
    /// -2560,1536: the final staircase builds off the snap points of what is already there).</summary>
    [Fact]
    public void AnApproachFromAStaircasesTopCarriesOnFromItsTopRow()
    {
        var end = new Vector2(24f, 0f);
        float height = CaveGround(end.x, end.y);
        Vector2 before = end + end.normalized * 30f;
        var request = new LocationApproachRequest
        {
            RoadEnd = end, RoadHeight = height, Arriving = StairLayout.Bearing(before, end),
            Centre = new Vector2(0f, 0f), RoadTail = new List<Vector2> { before, end },
        };
        // The road's staircase climbed west (toward the cave) to its top row's far edge at the road's end.
        var row = new StairContinuation(end, 270f, height, StairPieceKind.Stair);

        RoadStairSpan? span = RoadStairRuns.PlanPlacedLocationApproach(request, 90f, CaveFloor, RoadStairRuns.LayoutGround.Own(CaveGround), (x, z) => Heightmap.Biome.Mountain, 77,
            out string why, onStairs: row);

        Assert.True(span != null, why);
        StairPlan plan = span!.Plan;
        Assert.False(plan.Reversed);
        // No sunk foot: nothing walked below the row it carries on from.
        foreach (StairPiece piece in plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair))
            Assert.True(piece.Position.y >= height - 0.01f, $"{piece.Kind} at {piece.Position} under the row at {height:F2}");
        float turned = Mathf.Abs(StairLayout.Wrap(plan.StartHeading - row.Heading + 180f) - 180f);
        Assert.True(turned <= StairLayout.MaxStairTurnSteps * StairLayout.HeadingStep + 0.01f, $"sets off at {plan.StartHeading}");
        StairPiece first = plan.Pieces.First(p => p.Kind == StairPieceKind.Stair || p.Kind == StairPieceKind.Landing);
        float heading = first.Kind == StairPieceKind.Stair ? StairLayout.Wrap(first.YawDegrees + 180f) : first.YawDegrees;
        Vector2 back = new Vector2(first.Position.x, first.Position.z) - StairLayout.Forward(heading) * (StairLayout.PieceRun / 2f);
        if (turned < 0.01f)
            Assert.True(Vector2.Distance(back, end) < 0.05f, $"first piece starts at {back}, the row's edge is {end}");
        else
        {
            // Pivoting on the row's inside corner: its back edge within a piece's width, the wedge filled.
            Assert.True(Vector2.Distance(back, end) <= RoadStairRuns.Width + 0.05f, $"first piece starts at {back}, the row's edge is {end}");
            Assert.Contains(plan.Pieces, p => p.Kind == StairPieceKind.Filler);
        }
    }

    /// <summary>Where nothing fits carried straight on from a staircase's top row, the approach sets off
    /// from the row's end any way, at its height (MountainCave02 -2560,1536: every heading is
    /// buried, crosses itself or turns too far off the row, and the staircase stopped under the cave).</summary>
    [Fact]
    public void AnApproachSetsOffFromAStaircasesTopAnyWayWhereNothingFitsStraightOn()
    {
        var end = new Vector2(24f, 0f);
        float height = CaveGround(end.x, end.y);
        // A block of rock-hard ground 6 m high straight on from the row and either side of it: every
        // piece carried on from the row, or turned off it, is buried in it.
        float Ground(float x, float z) => CaveGround(x, z) + (x > 17f && x < 23.5f && Mathf.Abs(z) < 7f ? 6f : 0f);
        Vector2 before = end + new Vector2(30f, 0f);
        var request = new LocationApproachRequest
        {
            RoadEnd = end, RoadHeight = height, Arriving = StairLayout.Bearing(before, end),
            Centre = new Vector2(0f, 0f), RoadTail = new List<Vector2> { before, end },
        };
        // The road's staircase climbed west, toward the cave, to its top row at the road's end.
        var row = new StairContinuation(end, 270f, height, StairPieceKind.Stair);

        RoadStairSpan? span = RoadStairRuns.PlanPlacedLocationApproach(request, 90f, CaveFloor, RoadStairRuns.LayoutGround.Own(Ground), (x, z) => Heightmap.Biome.Mountain, 77,
            out string why, onStairs: row);

        Assert.True(span != null, why);
        foreach (StairPiece piece in span!.Plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair))
            Assert.True(piece.Position.y >= height - 0.01f, $"{piece.Kind} at {piece.Position} under the row at {height:F2}");
    }

    /// <summary>A location a staircase reaches is reached, with no road near it: the network counts its
    /// staircases (MountainCave02 -2560,1536: its staircase's top 38.8 m out, the nearest road
    /// 52 m, counted unreached). Its top (42 m) is level with the location's ground at its edge on its side (42.4 m).</summary>
    [Fact]
    public void ALocationAStaircaseReachesIsServedByTheNetwork()
    {
        float Slope(float x, float z) => 30f + 0.4f * (x + 60f);
        StairPlan plan = StairLayout.Build(new Vector2(-60f, 0f), new Vector2(-30f, 0f), Slope);
        Assert.True(plan.Reached, plan.Reason);
        Assert.False(RoadNetworkGenerator.Reached(new Vector3(0f, 0f, 0f), 10f));

        StairStructures.Add(StairRun.FromPlan(new Vector2(-60f, 0f), new Vector2(-30f, 0f), 2f, plan));

        Assert.True(RoadNetworkGenerator.Reached(new Vector3(0f, 0f, 0f), 10f));
    }

    /// <summary>A road's end not level with its location (<see cref="RoadNetworkGenerator.ReachRise"/>), over or under it,
    /// does not reach the location unless an approach staircase was laid from it onto the location (a road does not end
    /// below or above these locations). Before, only an end more than 20 m under was refused: a 100 m staircase ended
    /// 45 m under Hildir's cave.</summary>
    [Fact]
    public void ARoadEndNotLevelWithItsLocationDoesNotReachItWithoutAnApproach()
    {
        LocationLevelling.Source = c => c.magnitude < 0.5f ? Apron : null;
        var endNotLevel = typeof(RoadNetworkGenerator).GetMethod("EndNotLevel", BindingFlags.NonPublic | BindingFlags.Static)!;
        var path = new List<Vector2>();
        for (int x = -120; x <= -24; x += 8) path.Add(new Vector2(x, 0f));
        RoadSpatialGrid.PlannedPath plan = RoadSpatialGrid.PlanRoadPath(path, 4f, _world, locations: new RoadLocationProtection.Targets(null, new Vector2(0f, 0f)))!;
        Assert.NotNull(plan);
        string? Why(List<RoadStairSpan> stairs, List<RoadStairSpan>? approaches = null) => ((string refusal, string why)?)endNotLevel.Invoke(null, new object[] {
            path, new List<RoadSpatialGrid.PlannedPath> { plan }, stairs, approaches ?? new List<RoadStairSpan>(), path[0], 0f, new Vector2(0f, 0f), 10f, false, true, true })
            is (_, string why) ? why : null;
        RoadStairSpan EndsOn(float height) => new() { FromIndex = path.Count - 3, ToIndex = path.Count - 1, FromPoint = path[path.Count - 3],
            FromHeight = 5f, ToPoint = path[path.Count - 1], ToHeight = height };
        // The platform is at 52 m; the road ends on the ground at 40 m, 12 m under it (20 m was allowed before).
        Assert.Contains("ends 12.0 m under its location", Why(new List<RoadStairSpan>()));
        // An approach staircase laid from that end onto the location: reached by it.
        Assert.Null(Why(new List<RoadStairSpan>(), new List<RoadStairSpan> { new() { FromIndex = path.Count - 1, ToIndex = path.Count - 1 } }));
        // The road ends on a staircase's top: within a step of the platform, two metres over it, 45 m under it.
        Assert.Null(Why(new List<RoadStairSpan> { EndsOn(52f + RoadNetworkGenerator.ReachRise) }));
        Assert.Contains("ends 2.0 m over its location", Why(new List<RoadStairSpan> { EndsOn(54f) }));
        Assert.Contains("ends 45.0 m under its location", Why(new List<RoadStairSpan> { EndsOn(7f) }));
        // Outside the Mountains the same (reached needs height everywhere); no staircase is laid there.
        _world.Biome = Heightmap.Biome.Swamp;
        Assert.Contains("ends 12.0 m under its location", Why(new List<RoadStairSpan>()));
        Assert.Contains("ends 45.0 m under its location", Why(new List<RoadStairSpan> { EndsOn(7f) }));
    }

    /// <summary>A road whose end stands under its location and from which no approach can be laid is refused, and the location
    /// left unreached: there is no last-resort road for it any more (it was built ending under its location, and the location
    /// counted reached by it).</summary>
    [Fact]
    public void ARoadEndingUnderItsLocationWithNoApproachIsRefused()
    {
        // A platform raised 15 m over the apron (67 m): a road from the flat ends well under it, 40 m and more out.
        IReadOnlyList<LevelOp> raised = new[] { new LevelOp(0f, 0f, 15f, LevelRadius, false) };
        LocationLevelling.Source = c => c.magnitude < 0.5f ? raised : null;
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        SetPathfinder(new RoadPathfinder(_world) { Stairs = false, TerrainVarianceThreshold = 1000f, Meander = 0f });

        Assert.False(RoadNetworkGenerator.GenerateRoad(new Vector2(-120f, 0f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the apron"));
        Assert.Equal(RoadNetworkGenerator.NotLevelRefusal, RoadSpatialGrid.LastRefusal);
        Assert.False(RoadNetworkGenerator.Reached(new Vector3(0f, 0f, 0f), 10f));
    }

    /// <summary>Outside the Mountains (<see cref="RoadNetworkGenerator.ApproachStairsAt"/>) no approach staircase is laid, and a
    /// road that only comes close is not enough: one ending well under its platform is refused, and the location is
    /// not reached. Before, it was kept and counted the location reached.</summary>
    [Fact]
    public void ARoadEndingUnderItsLocationOutsideTheMountainsIsRefusedToo()
    {
        _world.Biome = Heightmap.Biome.Meadows;
        IReadOnlyList<LevelOp> raised = new[] { new LevelOp(0f, 0f, 15f, LevelRadius, false) };
        LocationLevelling.Source = c => c.magnitude < 0.5f ? raised : null;
        RoadNetworkGenerator.Reset();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), Footprint) });
        SetPathfinder(new RoadPathfinder(_world) { Stairs = false, TerrainVarianceThreshold = 1000f, Meander = 0f });

        // Refused before any search: none of its doors on the ground round it is level with its platform, and nothing can
        // climb to it outside the Mountains (RoadNetworkGenerator.NoLevelDoor).
        Assert.False(RoadNetworkGenerator.GenerateRoad(new Vector2(-120f, 0f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the apron"));
        Assert.Equal(RoadNetworkGenerator.NoLevelDoorRefusal, RoadSpatialGrid.LastRefusal);

        Assert.Empty(StairStructures.Runs);
        Assert.False(RoadNetworkGenerator.Reached(new Vector3(0f, 0f, 0f), 10f));
    }

    /// <summary>A location on a platform raised 15 m over the apron, no staircase searched, so no road can arrive level with it,
    /// as an island's planner tries it: it stays unserved in every biome. Outside the Mountains the fallback
    /// (<see cref="RoadNetworkGenerator.ReachOffLevel"/>) refuses an end past two junction steps off its platform too.</summary>
    [Theory]
    [InlineData(Heightmap.Biome.Mountain, false)]
    [InlineData(Heightmap.Biome.Meadows, false)]
    public void ALocationWhoseRingEndIsTooFarOffLevelStaysUnreached(Heightmap.Biome biome, bool reached)
    {
        _world.Biome = biome;
        IReadOnlyList<LevelOp> raised = new[] { new LevelOp(0f, 0f, 15f, LevelRadius, false) };
        LocationLevelling.Source = c => c.magnitude < 0.5f ? raised : null;
        RoadNetworkGenerator.Reset();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), Footprint) });
        SetPathfinder(new RoadPathfinder(_world) { Stairs = false, TerrainVarianceThreshold = 1000f, Meander = 0f });

        RoadNetworkGenerator.GenerateRoutedRoads(new Vector3(-120f, 0f, 0f), 0f,
            new List<(string name, Vector3 position, float radius)> { ("TrollCave02", Vector3.zero, 10f) });

        Assert.Empty(StairStructures.Runs);
        Assert.Equal(reached, RoadNetworkGenerator.Reached(new Vector3(0f, 0f, 0f), 10f));
        Assert.Equal(reached ? 1 : 0, RoadNetworkGenerator.ReachedOffLevelCount);
        Assert.Equal(!reached, RoadNetworkGenerator.HeightNeeded(new Vector2(0f, 0f)));
        Assert.Equal(reached, RoadSpatialGrid.GetRoadPointsNearPosition(Vector3.zero, 40f).Any(p => p.IsRoad));
    }

    /// <summary>An approach staircase is laid onto a location in the Mountains only: the same road to the same apron climbs its
    /// last stretch on stairs there; elsewhere it would end at the foot of the face, 12 m under the location, and is refused
    /// (not reached).</summary>
    [Fact]
    public void AnApproachStaircaseIsLaidOnlyAtALocationInTheMountains()
    {
        LocationLevelling.Source = c => c.magnitude < 0.5f ? Apron : null;
        float? Top(Heightmap.Biome biome)
        {
            _world.Biome = biome;
            RoadNetworkGenerator.Reset();
            RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), Footprint) });
            SetPathfinder(new RoadPathfinder(_world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
            bool built = RoadNetworkGenerator.GenerateRoad(new Vector2(-120f, 0f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the apron");
            if (biome != Heightmap.Biome.Mountain)
            {
                Assert.False(built, $"{biome}: a road ending under the location was kept");
                Assert.False(RoadNetworkGenerator.Reached(new Vector3(0f, 0f, 0f), 10f), $"{biome}: reached");
                return null;
            }
            Assert.True(built, $"{biome}: last refusal: " + (RoadSpatialGrid.LastRefusal ?? "none"));
            Assert.True(RoadNetworkGenerator.Reached(new Vector3(0f, 0f, 0f), 10f), $"{biome}: not reached");
            return StairStructures.Runs.Count == 0 ? null
                : StairStructures.Runs.SelectMany(r => r.Pieces).Where(p => p.Kind != StructurePieceKind.StairSupport).Max(p => p.Position.y);
        }

        Assert.InRange(Top(Heightmap.Biome.Mountain) ?? 0f, 51f, 52.5f);
        Assert.Null(Top(Heightmap.Biome.Meadows));
    }

    /// <summary>Only network level with a location (<see cref="RoadNetworkGenerator.ReachRise"/>) within its reach serves it:
    /// a road 25 m under its platform does not (it did with no height guard), nor one 2 m under or 8 m over it; one within a
    /// step of it does.</summary>
    [Fact]
    public void OnlyNetworkLevelWithALocationServesIt()
    {
        LocationLevelling.Source = c => c.magnitude < 0.5f ? Apron : null;
        RoadSpatialGrid.PlannedPath Road(float z, float height)
        {
            RoadSpatialGrid.PlannedPath road = RoadSpatialGrid.PlanRoadPath(new List<Vector2> { new(-40f, z), new(-26f, z) }, 4f, _world)!;
            for (int i = 0; i < road.Heights.Count; i++) road.Heights[i] = height;
            return road;
        }
        RoadSpatialGrid.Commit(Road(0f, 27f));
        Assert.False(RoadNetworkGenerator.Reached(new Vector3(0f, 0f, 0f), 10f));
        RoadSpatialGrid.Commit(Road(4f, 50f));
        Assert.False(RoadNetworkGenerator.Reached(new Vector3(0f, 0f, 0f), 10f));
        RoadSpatialGrid.Commit(Road(-4f, 60f));
        Assert.False(RoadNetworkGenerator.Reached(new Vector3(0f, 0f, 0f), 10f));
        RoadSpatialGrid.Commit(Road(6f, 52f - 1.4f));
        Assert.True(RoadNetworkGenerator.Reached(new Vector3(0f, 0f, 0f), 10f));
    }
    /// <summary>A location with no levelled flat (a Mountain cabin only smooths the ground round it) is reached only by network
    /// level with its ground at its edge on the network's side (RoadNetworkGenerator.ArrivalHeight), placed first or not. Before,
    /// with no single flat its height was not known and any road within reach counted it reached: AbandonedLogCabin04's
    /// (-1867,7101) ended 14 m out and 10 m under it, "connected", its ground at its edge on that side 4.7 m over the
    /// road. Here the cabin's edge is its apron's (12 m), at 52 m; the road below the face, at 40 m.</summary>
    [Fact]
    public void ALocationWithNoFlatIsReachedOnlyLevelWithItsGroundAtItsEdge()
    {
        IReadOnlyList<LevelOp> cabin = new[] { new LevelOp(0f, 0f, 0f, 0f, false, 9f, 5f) };
        LocationLevelling.Source = c => c.magnitude < 0.5f ? cabin : null;
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), Footprint, LevelRadius) });
        RoadSpatialGrid.PlannedPath Road(float z, float height)
        {
            RoadSpatialGrid.PlannedPath road = RoadSpatialGrid.PlanRoadPath(new List<Vector2> { new(-40f, z), new(-17f, z) }, 4f, _world, locations: new RoadLocationProtection.Targets(null, new Vector2(0f, 0f)))!;
            for (int i = 0; i < road.Heights.Count; i++) road.Heights[i] = height;
            return road;
        }
        RoadSpatialGrid.Commit(Road(0f, 40f));
        Assert.Equal(52f, RoadNetworkGenerator.ArrivalHeight(new Vector2(0f, 0f), 10f, new Vector2(-30f, 0f))!.Value, 2);
        Assert.False(RoadNetworkGenerator.Reached(new Vector3(0f, 0f, 0f), 10f, "AbandonedLogCabin04"));
        // Placed before its roads with no flat: the same.
        PlacedLocations.Remember(new PlacedLocationRecord
        {
            Prefab = "AbandonedLogCabin04", Centre = new Vector2(0f, 0f), Yaw = 0f, Root = 52f, Ops = new List<LevelOp>(cabin),
            X0 = -60, Z0 = -60, W = 121, H = 121,
        });
        try
        {
            Assert.True(PlacedLocations.FlatlessPlaced(new Vector2(0f, 0f)));
            Assert.False(RoadNetworkGenerator.Reached(new Vector3(0f, 0f, 0f), 10f, "AbandonedLogCabin04"));
            RoadSpatialGrid.Commit(Road(6f, 52f - 1.2f));
            Assert.True(RoadNetworkGenerator.Reached(new Vector3(0f, 0f, 0f), 10f, "AbandonedLogCabin04"));
        }
        finally { PlacedLocations.Reset(); }
    }

    /// <summary>A location with no flat entered from its side on a hillside (a troll cave) is reached by a road level with its
    /// ground at its edge on the road's side, however far that is under or over its centre's: its root is not its level.</summary>
    [Fact]
    public void ALocationWithNoFlatOnASlopeIsReachedLevelWithItsEdgeNotItsCentre()
    {
        LocationLevelling.Source = c => c.magnitude < 0.5f ? Array.Empty<LevelOp>() : null;
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), Footprint, 16f) });
        // At 16 m out the face is at 40 + 6 x 1.2 = 47.2 m, the centre at 52 m.
        Assert.Equal(47.2f, RoadNetworkGenerator.ArrivalHeight(new Vector2(0f, 0f), 10f, new Vector2(-30f, 0f))!.Value, 1);
        RoadSpatialGrid.PlannedPath road = RoadSpatialGrid.PlanRoadPath(new List<Vector2> { new(-40f, 0f), new(-17f, 0f) }, 4f, _world, locations: new RoadLocationProtection.Targets(null, new Vector2(0f, 0f)))!;
        for (int i = 0; i < road.Heights.Count; i++) road.Heights[i] = 46.5f;
        RoadSpatialGrid.Commit(road);
        Assert.True(RoadNetworkGenerator.Reached(new Vector3(0f, 0f, 0f), 10f, "TrollCave02"));
    }

    /// <summary>The stub drop case: a road 25 m under a location's platform, within reach of it, does not
    /// reach it, so a new road to the location keeps its own stub where it merges with that low road (the one reached
    /// test, <see cref="RoadNetworkGenerator.Reached"/>). Before, the stub drop had its own test with no height guard:
    /// it cut the stub, the location was left with no access and then counted unreached.</summary>
    [Fact]
    public void ARoadUnderALocationDoesNotDropTheStubOfTheRoadToIt()
    {
        LocationLevelling.Source = c => c.magnitude < 0.5f ? Apron : null;
        RoadSpatialGrid.PlannedPath low = RoadSpatialGrid.PlanRoadPath(new List<Vector2> { new(-40f, 0f), new(-26f, 0f) }, 4f, _world)!;
        for (int i = 0; i < low.Heights.Count; i++) low.Heights[i] = 27f;
        RoadSpatialGrid.Commit(low);
        // A road to the location along z = 0, over the low road 7 m before its end at the location's edge.
        var path = new List<Vector2> { new(-120f, 0f), new(-90f, 0f), new(-60f, 0f), new(-33f, 0f), new(-20f, 0f) };
        float snap = RoadNetworkGenerator.RoadSnap;
        try
        {
            RoadNetworkGenerator.RoadSnap = 6f;
            Assert.False(RoadNetworkGenerator.EndReached(new Vector2(0f, 0f), 10f));
            Assert.Equal(path.Count, RoadNetworkGenerator.DropStubs(path, true, RoadNetworkGenerator.EndReached(new Vector2(0f, 0f), 10f))!.Count);
            // Control: a road level with the location within its reach reaches it, and the stub is dropped.
            RoadSpatialGrid.PlannedPath level = RoadSpatialGrid.PlanRoadPath(new List<Vector2> { new(-40f, 8f), new(-26f, 8f) }, 4f, _world)!;
            for (int i = 0; i < level.Heights.Count; i++) level.Heights[i] = 52f;
            RoadSpatialGrid.Commit(level);
            Assert.True(RoadNetworkGenerator.EndReached(new Vector2(0f, 0f), 10f));
            Assert.True(RoadNetworkGenerator.DropStubs(path, true, RoadNetworkGenerator.EndReached(new Vector2(0f, 0f), 10f))!.Count < path.Count);
        }
        finally
        {
            RoadNetworkGenerator.RoadSnap = snap;
        }
    }
    /// <summary>The earthwork re-route plans through the one pipeline (PlanFromPath), so it refuses what preparation
    /// missed as the road's own build does: before, its own copy built the re-route anyway.</summary>
    [Fact]
    public void AReRouteIsRefusedWherePreparationMissedAsTheRoadIs()
    {
        var finder = new RoadPathfinder(_world) { Stairs = false, TerrainVarianceThreshold = 1000f, Meander = 0f };
        SetPathfinder(finder);
        Vector2 from = new(-180f, -100f), to = new(-80f, -100f);
        try
        {
            LocationLevelling.PreparationMissedHere = false;
            Assert.NotNull(RoadNetworkGenerator.Replan(finder, from, to, from, 0f, to, 0f, 4f, null, null, true, true));
            LocationLevelling.PreparationMissedHere = true;
            Assert.Null(RoadNetworkGenerator.Replan(finder, from, to, from, 0f, to, 0f, 4f, null, null, true, true));
            var path = finder.FindPath(from, to)!;
            Assert.Equal(RoadNetworkGenerator.PlanStop.Preparation,
                RoadNetworkGenerator.PlanFromPath(finder, path, from, 0f, to, 0f, false, false, true, true, 4f).Stop);
        }
        finally { LocationLevelling.PreparationMissedHere = false; }
    }

    /// <summary>A location mod's location levelling one flat 12 m below the ground round it (as read in game: a Mountains altar's
    /// flat 8 m under its road's end, the staircase down onto it ending in a big rock the location keeps): no staircase is laid
    /// down onto it (<see cref="RoadNetworkGenerator.ApproachStairsAt"/>); its first attempt leaves it unserved, and its
    /// fallback refuses a ring end too far above its landing. A vanilla location in the same pit still
    /// gets its staircase down, reached at height. Its footprint 21 m: at 20 m the road's end the vanilla location's staircase
    /// is laid from met its ring only within the search's old band round the circle, which an arrival no longer has
    /// (decision 6j: the start's precise target, RoadEndRing.Allows).</summary>
    [Theory]
    [InlineData("MwlFixtureAltar", true)]
    [InlineData("MountainCave02", false)]
    public void ACustomLocationBelowItsRoadGetsNoStaircaseDownAndNoRoadFarAboveItsLanding(string name, bool custom)
    {
        var world = new SunkWorld();
        _scope.WithWorld(world);
        IReadOnlyList<LevelOp> flat = new[] { new LevelOp(0f, 0f, 0f, LevelRadius, false) };
        LocationLevelling.Source = c => c.magnitude < 0.5f ? flat : null;
        RoadNetworkGenerator.Reset();
        RoadNetworkGenerator.RegisterLocation("MwlFixtureAltar");
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), Footprint + 1f) });
        SetPathfinder(new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        try
        {
            RoadNetworkGenerator.GenerateRoutedRoads(new Vector3(-120f, 0f, 0f), 0f,
                new List<(string name, Vector3 position, float radius)> { (name, Vector3.zero, 10f) });

            Assert.Equal(!custom, RoadNetworkGenerator.Reached(new Vector2(0f, 0f), 10f, name));
            Assert.Equal(0, RoadNetworkGenerator.ReachedOffLevelCount);
            Assert.True(RoadNetworkGenerator.HeightNeeded(new Vector2(0f, 0f)));
            Assert.Equal(custom, StairStructures.Runs.Count == 0);
            if (!custom) return;
            Assert.Empty(RoadSpatialGrid.GetRoadPointsNearPosition(Vector3.zero, 60f).Where(p => p.IsRoad));
        }
        finally { RoadNetworkGenerator.UnregisterLocation("MwlFixtureAltar"); }
    }

    /// <summary>The one owner's choice from a road's end (<see cref="RoadNetworkGenerator.ApproachStairsAt"/>, in the
    /// Mountains, on the apron at 52 m): a custom location gets a staircase up from an end under its flat, none down from one
    /// over it (more than <see cref="StairLayout.StairFitsFrom"/>); a vanilla location both.</summary>
    [Fact]
    public void ApproachStairsGoDownOnlyToAVanillaLocation()
    {
        LocationLevelling.Source = c => c.magnitude < 0.5f ? Apron : null;
        RoadNetworkGenerator.RegisterLocation("MwlFixtureAltar");
        try
        {
            var centre = new Vector2(0f, 0f);
            Assert.True(RoadNetworkGenerator.ApproachStairsAt(centre, "MwlFixtureAltar", 44f));
            Assert.True(RoadNetworkGenerator.ApproachStairsAt(centre, "MwlFixtureAltar", 52f + StairLayout.StairFitsFrom));
            Assert.False(RoadNetworkGenerator.ApproachStairsAt(centre, "MwlFixtureAltar", 60f));
            Assert.True(RoadNetworkGenerator.ApproachStairsAt(centre, "MwlFixtureAltar"));
            Assert.True(RoadNetworkGenerator.ApproachStairsAt(centre, "MountainCave02", 60f));
        }
        finally { RoadNetworkGenerator.UnregisterLocation("MwlFixtureAltar"); }
    }

    /// <summary>On the fallback the same custom location on its apron (52 m) keeps its stand-off where it stands above the road's
    /// end: network 8 m under it just past its ring does not reach it, at its stand-off (8 m over the grade cap past the ring)
    /// it does; network 8 m over it just past its ring does not reach its landing level.</summary>
    [Theory]
    [InlineData(-8f, false, false)]
    [InlineData(-8f, true, true)]
    [InlineData(8f, false, false)]
    public void ACustomLocationAboveItsEndKeepsItsStandOffAndOneBelowItDoesNot(float over, bool atStandOff, bool reached)
    {
        LocationLevelling.Source = c => c.magnitude < 0.5f ? Apron : null;
        RoadNetworkGenerator.RegisterLocation("MwlFixtureAltar");
        float ring = RoadNetworkGenerator.KeepOutEdge(new Vector2(0f, 0f), 10f, RoadNetworkGenerator.RoadWidth);
        float z = atStandOff ? ring + Mathf.Max(0f, -over) / RoadGrade.Configured : ring + 2f;
        var points = new List<Vector2> { new(-20f, z), new(0f, z), new(20f, z) };
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(_ => 52f + over), points.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 40f, false));
        try
        {
            Assert.False(RoadNetworkGenerator.Reached(new Vector2(0f, 0f), 10f, "MwlFixtureAltar"));
            Assert.Equal(reached, RoadNetworkGenerator.ReachOffLevel("MwlFixtureAltar", Vector3.zero, 10f, () => false));
            Assert.Equal(reached, RoadNetworkGenerator.Reached(new Vector2(0f, 0f), 10f, "MwlFixtureAltar"));
            Assert.Equal(reached ? 1 : 0, RoadNetworkGenerator.ReachedOffLevelCount);
        }
        finally { RoadSpatialGrid.Clear(); RoadNetworkGenerator.UnregisterLocation("MwlFixtureAltar"); }
    }
}
