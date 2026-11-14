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
/// Staircases on generated roads: the pathfinder buys a step too steep for road as a stair
/// step, and the generator lays the staircase and cuts the road at it.
/// </summary>
public class RoadStairTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    /// <summary>Flat ground at 40 m west of x = 0 and at 40 + Rise east of
    /// x = Rise / Grade, joined by a face of <see cref="Grade"/>: too steep
    /// for road even across it (a knight's move along it climbs Grade / 2.24).
    /// Sea beyond |x|, |y| = 200.</summary>
    private sealed class ScarpWorld : WorldGenerator
    {
        public float Grade = 1.2f;
        /// <summary>28.8 m: the face ends at x = 24, on a search cell line, so the flight's last step reaches the rim. At 24 m
        /// it ends mid-cell (x = 20) and the climb's last step crosses the rim (<see cref="AFlightUnderASharpRimIsLaidToTheRimAndItsRoadLeavesAtTheGround"/>).</summary>
        public float Rise = 28.8f;
        public Heightmap.Biome Biome = Heightmap.Biome.Mountain;

        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            return 40f + Mathf.Clamp(wx * Grade, 0f, Rise);
        }

        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Biome;
        public override int GetSeed() => 77;
    }

    /// <summary>A ridge (or a gully) along z: a flat top (or floor) 2 * Flat wide (32 m), faces
    /// of 1.2 rising 24 m either side, flats beyond, sea past 200 m. A crest
    /// narrower than a pathfinding cell would be stepped over as level ground.</summary>
    private sealed class RidgeWorld : WorldGenerator
    {
        public bool Gully;
        /// <summary>Half the flat top's (or floor's) width, metres.</summary>
        public float Flat = 16f;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            float bump = Mathf.Clamp(24f - (Mathf.Abs(wx) - Flat) * 1.2f, 0f, 24f);
            return Gully ? 64f - bump : 40f + bump;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    private readonly ScarpWorld _world = new();

    public RoadStairTests()
    {
        _scope.WithWorld(_world);
        RoadNetworkGenerator.Reset();
    }

    public void Dispose()
    {
        SetPathfinder(null);
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, pathfinder);

    private RoadPathfinder Finder(bool stairs) =>
        new(_world) { Stairs = stairs, TerrainVarianceThreshold = 1000f, Meander = 0f };

    [Fact]
    public void AFaceTooSteepForRoadIsClimbedOnStairSteps()
    {
        Assert.Null(Finder(stairs: false).FindPath(new Vector2(-80f, 0f), new Vector2(100f, 0f)));

        RoadPathfinder finder = Finder(stairs: true);
        List<Vector2>? path = finder.FindPath(new Vector2(-80f, 0f), new Vector2(100f, 0f));
        Assert.NotNull(path);
        Assert.NotEmpty(finder.LastStairSteps);
        foreach (var (from, to) in finder.LastStairSteps)
        {
            float slope = Mathf.Abs(_world.GetHeight(to.x, to.y) - _world.GetHeight(from.x, from.y)) / Vector2.Distance(from, to);
            Assert.InRange(slope, RoadPathfinder.StairMinGrade, RoadPathfinder.StairMaxGrade);
        }
        // Every stair step is a step of the path.
        foreach (var (from, to) in finder.LastStairSteps)
            Assert.Contains(Enumerable.Range(1, path!.Count - 1), i => path[i - 1] == from && path[i] == to);
    }

    /// <summary>A face of 0.6 from 40 m to 64 m: too steep for road straight up, so a road
    /// switches back across it; steep enough for stairs.</summary>
    private sealed class SwitchbackFaceWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            return 40f + Mathf.Clamp(wx * 0.6f, 0f, 24f);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    /// <summary>Cells opened to stairs (the generator opens them round switchback turns too close
    /// to fit) are climbed on stairs where road would switch back; elsewhere the road keeps its
    /// switchbacks (swap to stairs for tight switchbacks, then back to roads).</summary>
    [Fact]
    public void CellsOpenedToStairsAreClimbedOnStairsWhereRoadWouldSwitchBack()
    {
        var world = new SwitchbackFaceWorld();
        _scope.WithWorld(world);
        var finder = new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f };
        Assert.NotNull(finder.FindPath(new Vector2(-60f, 0f), new Vector2(100f, 0f)));
        Assert.Empty(finder.LastStairSteps);

        finder.OpenToStairs(new[] { new Vector2(20f, 0f) }, 3);
        Assert.NotNull(finder.FindPath(new Vector2(-60f, 0f), new Vector2(100f, 0f)));

        Assert.NotEmpty(finder.LastStairSteps);
        foreach (var (from, to) in finder.LastStairSteps)
            Assert.True(Mathf.Abs(from.x - 20f) <= 4 * RoadPathfinder.CellSize && Mathf.Abs(to.x - 20f) <= 4 * RoadPathfinder.CellSize, $"stair step {from}->{to} outside the opened cells");
        finder.ClearNoStairs();
        Assert.Equal(0, finder.OpenStairCells);
    }

    /// <summary>A staircase whose climb leaves its road square to it sets off from the ring at the road's end (RoadEndRing),
    /// not its middle: no step stands inside the ring or in the lane back along the road (a bottom step on the road would
    /// cover half the road). The face rises east, the road arrives heading north. Before the ring it was set beside the road,
    /// its edge on the road's edge; now its foot clips the road, on the circle.</summary>
    [Fact]
    public void AStaircaseLeavingItsRoadSquareStartsAtTheRoadsEdge()
    {
        // North up x = 0 to the foot of the face, then east up it.
        var path = new List<Vector2>();
        for (int z = -40; z <= 0; z += 8) path.Add(new Vector2(0f, z));
        for (int x = 8; x <= 32; x += 8) path.Add(new Vector2(x, 0f));
        var steps = new List<(Vector2, Vector2)> { (new Vector2(0f, 0f), new Vector2(8f, 0f)), (new Vector2(8f, 0f), new Vector2(16f, 0f)), (new Vector2(16f, 0f), new Vector2(24f, 0f)) };

        List<RoadStairSpan>? spans = RoadStairRuns.Lay(path, steps, new List<RoadCrossing>(), _world);

        Assert.NotNull(spans);
        RoadStairSpan span = Assert.Single(spans!);
        // The road ends at the ring's centre, the path's point; the flight sets off from the circle (straight on first where
        // that fits, turning on its stairs: the ring prices the start by its turn off the road).
        var ring = new RoadEndRing(new Vector2(0f, 0f), 0f, span.FromHeight);
        Assert.Equal(new Vector2(0f, 0f), span.FromPoint);
        Assert.True(span.Plan.RingStart is Vector2 start && Mathf.Abs(start.magnitude - ring.Radius) < 0.01f, $"set off from {span.Plan.RingStart}");
        Assert.False(ring.Intrudes(span.Plan, out string piece), piece);
    }

    /// <summary>A staircase that carries on roughly the way its road goes (here 27 degrees off it)
    /// sets off from the front of the ring at the road's end, not beside the road (a stair going the same direction as the
    /// road stays centred).</summary>
    [Fact]
    public void AStaircaseCarryingOnRoughlyTheWayItsRoadGoesStartsFromTheMiddle()
    {
        // North-east by knight's moves to the foot of the face, then east up it.
        var path = new List<Vector2>();
        for (int i = -3; i <= 0; i++) path.Add(new Vector2(16f * i, 8f * i));
        for (int x = 8; x <= 32; x += 8) path.Add(new Vector2(x, 0f));
        var steps = new List<(Vector2, Vector2)> { (new Vector2(0f, 0f), new Vector2(8f, 0f)), (new Vector2(8f, 0f), new Vector2(16f, 0f)), (new Vector2(16f, 0f), new Vector2(24f, 0f)) };

        List<RoadStairSpan>? spans = RoadStairRuns.Lay(path, steps, new List<RoadCrossing>(), _world);

        Assert.NotNull(spans);
        RoadStairSpan span = Assert.Single(spans!);
        // Its foot on the ring at the road's end, within the road's width, not a road's width to one side of it.
        float travel = StairLayout.Bearing(path[2], path[3]);
        var ring = new RoadEndRing(new Vector2(0f, 0f), travel, span.FromHeight);
        Assert.True(span.Plan.RingStart is Vector2 start && Mathf.Abs(start.magnitude - ring.Radius) < 0.01f
            && Mathf.Abs(Vector2.Dot(start, StairLayout.Right(travel))) <= ring.Radius + 0.01f, $"set off from {span.Plan.RingStart}, the road arriving on {travel}");
        Assert.False(ring.Intrudes(span.Plan, out string piece), piece);
        StairPiece foot = span.Plan.Pieces.First(p => p.Kind == StairPieceKind.Stair || p.Kind == StairPieceKind.Landing);
        Assert.True(Mathf.Abs(foot.Position.z) < RoadNetworkGenerator.RoadWidth / 2f, $"foot at {foot.Position} is beside the road");
    }

    /// <summary>East along z = 0, a last search step in from the north-west, then east up a face of 0.45: the
    /// road as a player sees it runs the staircase's way, though its last 8 m step is 45 degrees off it.</summary>
    private (List<Vector2> path, List<(Vector2, Vector2)> steps) EastWithAJogIn()
    {
        // A face a staircase climbs straight up: steeper than road, gentler than a flight (0.5).
        _world.Grade = 0.45f;
        var path = new List<Vector2>();
        for (int x = -48; x <= -16; x += 8) path.Add(new Vector2(x, 0f));
        path.Add(new Vector2(-8f, 8f));
        for (int x = 0; x <= 32; x += 8) path.Add(new Vector2(x, 0f));
        var steps = new List<(Vector2, Vector2)> { (new Vector2(0f, 0f), new Vector2(8f, 0f)), (new Vector2(8f, 0f), new Vector2(16f, 0f)), (new Vector2(16f, 0f), new Vector2(24f, 0f)) };
        return (path, steps);
    }

    /// <summary>The road's direction at a staircase's foot is read over its last stretch, and the
    /// staircase's from its settled flight: one carrying on the road's way starts from its middle though
    /// the last search step is 45 degrees off (MountainCave02 -2560,1536: the bottom of the
    /// stairs was not aligned).</summary>
    [Fact]
    public void AStaircaseCarryingOnTheRoadsWayPastAJogInItsLastStepStartsFromTheMiddle()
    {
        var (path, steps) = EastWithAJogIn();

        List<RoadStairSpan>? spans = RoadStairRuns.Lay(path, steps, new List<RoadCrossing>(), _world);

        Assert.NotNull(spans);
        RoadStairSpan only = Assert.Single(spans!);
        float arc = only.Plan.RingStart is Vector2 start ? RoadEndRing.Turn(StairLayout.Bearing(new Vector2(0f, 0f), start), 90f) : 999f;
        Assert.True(Mathf.Abs(arc) <= 45.01f,
            $"set off from the ring {arc:F1} round: start {only.Plan.StartHeading}, settled {RoadStairRuns.SettledHeading(only.Plan, new Vector2(0f, 0f))}, travel {StairLayout.Bearing(RoadStairRuns.Back(path, 6, -1, RoadStairRuns.TravelReach), new Vector2(0f, 0f))}, pieces "
            + string.Join(" ", only.Plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair).Take(6).Select(p => $"({p.Position.x:F1},{p.Position.z:F1})h{StairLayout.Wrap(p.YawDegrees + 180f)}")));
    }

    /// <summary>A flight that swings round on its bottom steps is told from one that turns later: the
    /// stairs at the foot of Ruin1 -> Hildir_cave faced 247.5, 270, then 315 up the flight
    /// (a weird jog; start aligned with the road).</summary>
    [Fact]
    public void ASwingOnTheBottomStepsIsToldFromALaterTurn()
    {
        StairPlan Flight(params float[] headings)
        {
            var plan = new StairPlan();
            for (int i = 0; i < headings.Length; i++)
                plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Stair, Position = new Vector3(2f * i + 1f, i, 0f), YawDegrees = StairLayout.Wrap(headings[i] + 180f) });
            return plan;
        }

        Assert.True(RoadStairRuns.SwingsAtItsFoot(Flight(247.5f, 270f, 315f, 315f, 315f), new Vector2(0f, 0f)));
        Assert.False(RoadStairRuns.SwingsAtItsFoot(Flight(315f, 315f, 315f, 247.5f, 247.5f), new Vector2(0f, 0f)));
        Assert.False(RoadStairRuns.SwingsAtItsFoot(Flight(292.5f, 315f, 315f, 315f), new Vector2(0f, 0f)));
    }

    /// <summary>A stretch whose staircase could not be laid is closed to stairs and the road
    /// searched again: the staircase moves off it, stairs still open elsewhere. Closing the whole
    /// face leaves what a search without stairs finds (none here). Cleared, the first route is back.</summary>
    [Fact]
    public void A_stretch_closed_to_stairs_moves_the_staircase_off_it()
    {
        RoadPathfinder finder = Finder(stairs: true);
        Vector2 from = new(-80f, 0f), to = new(100f, 0f);
        List<Vector2> first = finder.FindPath(from, to)!;
        var firstSteps = finder.LastStairSteps.ToList();
        Assert.NotEmpty(firstSteps);

        finder.CloseToStairs(firstSteps.SelectMany(s => new[] { s.from, s.to }), RoadNetworkGenerator.StairStretchReachCells);
        Assert.NotNull(finder.FindPath(from, to));
        Assert.NotEmpty(finder.LastStairSteps);
        float Off(Vector2 p) => firstSteps.Min(s => Mathf.Min(Vector2.Distance(p, s.from), Vector2.Distance(p, s.to)));
        foreach (var (a, b) in finder.LastStairSteps)
            Assert.True(Off(a) > RoadPathfinder.CellSize && Off(b) > RoadPathfinder.CellSize,
                $"stair step {a}->{b} is in a closed cell");

        var face = new List<Vector2>();
        for (float y = -200f; y <= 200f; y += RoadPathfinder.CellSize)
            for (float x = -8f; x <= 32f; x += RoadPathfinder.CellSize) face.Add(new Vector2(x, y));
        finder.CloseToStairs(face, 0);
        Assert.Null(finder.FindPath(from, to));

        finder.ClearNoStairs();
        Assert.Equal(first, finder.FindPath(from, to));
    }

    /// <summary>A face too steep for road, then 40 m of slope a road can climb:
    /// the staircase takes the face and the road the slope. Offered wherever it
    /// was cheaper, the staircase carried on up the slope too, and roads lost
    /// their traverses and switchbacks.</summary>
    private sealed class FaceThenSlopeWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            return 40f + Mathf.Clamp(wx * 1.2f, 0f, 24f) + Mathf.Clamp((wx - 20f) * 0.44f, 0f, 17.6f);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override int GetSeed() => 77;
    }

    [Fact]
    public void StairsOnlyWhereTheRoadCannotClimb()
    {
        var world = new FaceThenSlopeWorld();
        _scope.WithWorld(world);
        var finder = new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f };

        Assert.NotNull(finder.FindPath(new Vector2(-80f, 0f), new Vector2(120f, 0f)));

        Assert.NotEmpty(finder.LastStairSteps);
        // A staircase starts only where road cannot climb, and carries on only over
        // ground steeper than StairMinGrade: never onto the gentle ground beyond.
        float roadLimit = RoadGrade.Configured + RoadConstants.SearchGradeMargin;
        float Slope((Vector2 from, Vector2 to) s) => Mathf.Abs(world.GetHeight(s.to.x, s.to.y) - world.GetHeight(s.from.x, s.from.y)) / Vector2.Distance(s.from, s.to);
        Assert.True(Slope(finder.LastStairSteps[0]) > roadLimit, $"the staircase starts on {Slope(finder.LastStairSteps[0]):F2}");
        foreach (var step in finder.LastStairSteps)
            Assert.True(Slope(step) >= RoadPathfinder.StairMinGrade, $"a stair step of {Slope(step):F2} at {step.from} -> {step.to}");
    }

    /// <summary>A face whose rim lies between search cells (x = 20): the climb's last step, from the flight's top cell over
    /// the rim, is 27% end to end, so the search takes it as road. The flight is ended higher (RoadStairRuns.EndHigher: its
    /// road could not climb on from the top on its ground) and laid to the rim, in either kit: its top at the plateau's
    /// height, and the road on from it at the ground, with no cut (it stopped 4.4 m under the rim, and its road cut the rest
    /// into the top, a trench 25 m long).</summary>
    [Theory]
    [InlineData(Heightmap.Biome.Mountain)]
    [InlineData(Heightmap.Biome.Mistlands)]
    public void AFlightUnderASharpRimIsLaidToTheRimAndItsRoadLeavesAtTheGround(Heightmap.Biome biome)
    {
        _world.Rise = 24f;
        _world.Biome = biome;
        SetPathfinder(Finder(stairs: true));
        var pieces = new List<RoadSpatialGrid.PlannedPath>();
        RoadNetworkGenerator.PieceCommitted = (plan, _) => pieces.Add(plan);
        try
        {
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0f), 0f, new Vector2(100f, 0f), 0f, 4f, "scarp"), RoadSpatialGrid.LastRefusal);
        }
        finally { RoadNetworkGenerator.PieceCommitted = null; }
        StairRun run = Assert.Single(StairStructures.Runs);
        float plateau = 40f + _world.Rise, rim = _world.Rise / _world.Grade;
        float top = run.Pieces.Where(p => p.Kind == StructurePieceKind.StairFloor || p.Kind == StructurePieceKind.StairStep).Max(p => p.Position.y);
        Assert.True(top + (run.Kit ?? StairKit.Stone).FloorTop >= plateau - StairLayout.StepUp, $"the flight's top at {top:F2}, the plateau at {plateau:F2}");
        // The road past the rim stands at the ground: nowhere a cut deeper than a step.
        foreach (var plan in pieces)
            for (int i = 0; i < plan.Points.Count; i++)
                if (plan.Points[i].x > rim && plan.Points[i].x < rim + 30f)
                    Assert.True(plan.Heights[i] >= plateau - StairLayout.StepUp, $"the road at {plan.Points[i]} stands {plan.Heights[i]:F2}, the plateau {plateau:F2}");
    }

    [Fact]
    public void TheRoadStopsAtTheStaircaseAndCarriesOnFromItsTop()
    {
        SetPathfinder(Finder(stairs: true));

        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0f), 0f, new Vector2(100f, 0f), 0f, 4f, "scarp"));

        StairRun run = Assert.Single(StairStructures.Runs);
        Assert.All(run.Pieces, p => Assert.StartsWith("stone_", p.Prefab)); // a Mountain staircase
        Assert.Contains(run.Pieces, p => p.Kind == StructurePieceKind.StairStep);
        // Road on the flats both sides, none levelled on the face (cuts are not road).
        Assert.NotEmpty(RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(-40f, 0f, 0f), 12f).Where(p => p.IsRoad));
        Assert.NotEmpty(RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(60f, 0f, 0f), 12f).Where(p => p.IsRoad));
        float faceTop = _world.Rise / _world.Grade;
        foreach (StructurePiece step in run.Pieces.Where(p => p.Kind == StructurePieceKind.StairStep))
            Assert.DoesNotContain(RoadSpatialGrid.GetRoadPointsNearPosition(step.Position, 0.5f), p => p.IsRoad && p.p.x > 2f && p.p.x < faceTop - 2f);
        // No walked piece lies on the road away from the two junctions (the stairs must not go over the road they start from).
        foreach (StructurePiece piece in run.Pieces.Where(p => p.Kind != StructurePieceKind.StairSupport))
        {
            var at = new Vector2(piece.Position.x, piece.Position.z);
            if (Vector2.Distance(at, run.From) <= RoadStairRuns.JunctionReach + 2f || Vector2.Distance(at, run.To) <= RoadStairRuns.JunctionReach + 2f) continue;
            Assert.DoesNotContain(RoadSpatialGrid.GetRoadPointsNearPosition(piece.Position, 1.5f), p => p.IsRoad);
        }
        // The road meets the staircase level at both ends. The foot is the first step's base: the
        // lowest stair is sunk a rise below it, or, for a staircase set beside its road, stands on
        // the road's height itself (BesideItsRoad lays it from the road's height).
        float lowest = run.Pieces.Where(p => p.Kind == StructurePieceKind.StairStep).Min(p => p.Position.y);
        Assert.Contains(RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(run.From.x, 0f, run.From.y), 3f),
            p => p.IsRoad && (Mathf.Abs(p.h - (lowest + StairLayout.PieceRise)) < 0.6f || Mathf.Abs(p.h - lowest) < 0.6f));
    }

    /// <summary>A road's own staircase is recorded at the path points it climbs, and with them where the road meets each end:
    /// the road's own last point before it and first after it, on the far edge of its top landing, which its railing leaves
    /// bare (staircase -1880,6632 in game: the road came onto its top landing 3.4 m on from the recorded end, and the landing
    /// was railed across it).</summary>
    [Fact]
    public void ARoadsStaircaseRecordsWhereTheRoadMeetsEachEnd()
    {
        SetPathfinder(Finder(stairs: true));
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0f), 0f, new Vector2(100f, 0f), 0f, 4f, "scarp"));
        StairRun run = Assert.Single(StairStructures.Runs);
        Assert.NotNull(run.FromMeet);
        Assert.NotNull(run.ToMeet);
        foreach (StairEnd end in run.Ends)
        {
            Assert.False(end.AtLocation);
            Assert.Contains(RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(end.Meet.x, 0f, end.Meet.y), 0.25f), p => p.IsRoad);
        }
        // The top, its landing laid on past the recorded end: no rail where the road comes onto it.
        StairEnd top = run.Ends.OrderByDescending(e => _world.GetHeight(e.Meet.x, e.Meet.y)).First();
        Assert.True(Vector2.Distance(top.At, top.Meet) > 1f, $"top recorded at {top.At}, met at {top.Meet}");
        List<StructurePiece> dressed = StairKit.Stone.Dress(run, (x, z) => _world.GetHeight(x, z), (x, z) => _world.GetHeight(x, z), false);
        Assert.DoesNotContain(dressed, p => p.Kind == StructurePieceKind.Beam && Vector2.Distance(new Vector2(p.Position.x, p.Position.z), top.Meet) < 1.5f);
    }

    [Fact]
    public void WithoutStairsTheRoadIsNotBuiltAndNothingIsLaid()
    {
        SetPathfinder(Finder(stairs: false));

        Assert.False(RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0f), 0f, new Vector2(100f, 0f), 0f, 4f, "scarp"));
        Assert.Empty(StairStructures.Runs);
    }

    [Fact]
    public void AStaircaseIsBuiltOfItsBiomesPieces()
    {
        _world.Biome = Heightmap.Biome.Mistlands;
        SetPathfinder(Finder(stairs: true));

        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0f), 0f, new Vector2(100f, 0f), 0f, 4f, "scarp"));

        Assert.All(Assert.Single(StairStructures.Runs).Pieces, p => Assert.StartsWith("blackmarble_", p.Prefab));
    }

    /// <summary>Over a ridge or through a gully the road climbs one staircase and
    /// descends another (stairs can go downhill too): a
    /// layout climbs from its lower end, so one staircase cannot cross a crest,
    /// and joined into one the road was refused. Where they meet, edge to edge: in the gully the shared floor was laid on
    /// along the stair above the first's foot floor, 22.5 degrees off that floor, sharing no corner with it.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARidgeOrAGullyIsTwoStaircases(bool gully)
    {
        // The gully's faces end on search cell lines (a floor 24 m wide): from a 32 m floor the climb's last step crossed
        // the rim mid-step, its flight stopped under it, and its road cut a trench into the top (refused).
        var world = new RidgeWorld { Gully = gully, Flat = gully ? 12f : 16f };
        _scope.WithWorld(world);
        SetPathfinder(new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });

        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0f), 0f, new Vector2(80f, 0f), 0f, 4f, gully ? "gully" : "ridge"), RoadSpatialGrid.LastRefusal);

        Assert.Equal(2, StairStructures.Runs.Count);
        // One climbs the west face, one the east; close over the top, the second is
        // laid to meet the first, with floors between.
        Assert.Contains(StairStructures.Runs, r => r.Pieces.Any(p => p.Position.x < -20f) && r.Pieces.All(p => p.Position.x < 1f));
        Assert.Contains(StairStructures.Runs, r => r.Pieces.Any(p => p.Position.x > 20f));
        // Their tops meet, not one inside the other (save floors level with each other, which overlap at a turn).
        var walked0 = StairStructures.Runs[0].Pieces.Where(p => p.Kind != StructurePieceKind.StairSupport).ToList();
        var walked1 = StairStructures.Runs[1].Pieces.Where(p => p.Kind != StructurePieceKind.StairSupport).ToList();
        foreach (var p in walked0)
            Assert.DoesNotContain(walked1, q => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), new Vector2(q.Position.x, q.Position.z)) < RoadStairRuns.StairOverlap
                && Mathf.Abs(p.Position.y - q.Position.y) < 2f
                && !(p.Kind == StructurePieceKind.StairFloor && q.Kind == StructurePieceKind.StairFloor && Mathf.Abs(p.Position.y - q.Position.y) < 0.05f));
        // And every join between them, and within each, edge to edge (FlatRunJoinTests' rule).
        var walked = StairStructures.Runs.SelectMany(r => r.Pieces).Where(p => p.Kind == StructurePieceKind.StairStep || p.Kind == StructurePieceKind.StairFloor)
            .Select(p => new StairPiece { Kind = p.Kind == StructurePieceKind.StairStep ? StairPieceKind.Stair : StairPieceKind.Landing, Prefab = p.Prefab,
                Position = p.Position, YawDegrees = p.YawDegrees }).ToList();
        Assert.Empty(FlatRunJoinTests.JoinFaults(walked, StairStructures.Runs[0].Kit ?? StairKit.Stone));
    }

    /// <summary>Over a ridge, the staircase down from the crest is carried on to the top row of the
    /// one that climbed to it: the two meet edge to edge, with no road laid between them (at 7842,1803 the two stairs
    /// connect via their snap points without a short road section). Measured before: 1.2 m apart, a road piece over both tops.</summary>
    [Fact]
    public void TwoStaircasesOverARidgeMeetEdgeToEdgeWithNoRoadBetween()
    {
        RidgeMeeting();
    }

    private void RidgeMeeting()
    {
        var world = new RidgeWorld();
        _scope.WithWorld(world);
        SetPathfinder(new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });

        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0f), 0f, new Vector2(80f, 0f), 0f, 4f, "ridge"));

        Assert.Equal(2, StairStructures.Runs.Count);
        StairRun up = StairStructures.Runs[0], down = StairStructures.Runs[1];
        // The first one's top row: its highest floor nearest the crest (x = 0), and that floor's edge
        // toward the crest. The second has a row whose far edge is that same edge.
        float top = up.Pieces.Where(p => p.Kind != StructurePieceKind.StairSupport).Max(p => p.Position.y);
        StructurePiece last = up.Pieces.Where(p => p.Kind == StructurePieceKind.StairFloor && p.Position.y > top - 0.1f)
            .OrderBy(p => Mathf.Abs(p.Position.x)).First();
        float height = last.Position.y + StairKit.Stone.FloorTop;
        Vector2 middle = new Vector2(last.Position.x, last.Position.z);
        Vector2 edge = new[] { middle + StairLayout.Forward(last.YawDegrees), middle - StairLayout.Forward(last.YawDegrees) }
            .OrderBy(e => Mathf.Abs(e.x)).First();
        Assert.True(RoadStairRuns.TopAt(edge, height, up.Pieces).HasValue, $"no top row at {edge}");
        Assert.True(RoadStairRuns.TopAt(edge, height, down.Pieces).HasValue,
            $"the second's top does not reach {edge}: its floors at {string.Join(" ", down.Pieces.Where(p => p.Kind == StructurePieceKind.StairFloor && p.Position.y > top - 0.1f).Select(p => p.Position.x.ToString("F1")))}");
        // No road over the crest between the two.
        Vector2 dir = StairLayout.Forward(last.YawDegrees);
        for (float d = -4f; d <= 4f; d += 1f)
            Assert.False(RoadSpatialGrid.TryGetRoadWithin(edge + dir * d, 1f, out Vector2 at), $"road at {at}, {d} m from the meeting edge");
    }

    /// <summary>A second road over the same face walks the staircase the first
    /// one laid: two laid side by side spawned every piece twice.</summary>
    [Fact]
    public void ASecondRoadOverTheSameFaceWalksTheFirstOnesStaircase()
    {
        SetPathfinder(Finder(stairs: true));

        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0f), 0f, new Vector2(100f, 0f), 0f, 4f, "first"));
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 6f), 0f, new Vector2(100f, 6f), 0f, 4f, "second"));

        Assert.Single(StairStructures.Runs);
    }

    /// <summary>A road built after a staircase goes round it, never under or through
    /// it.</summary>
    [Fact]
    public void ALaterRoadGoesRoundAStaircase()
    {
        var flat = new ScarpWorld { Grade = 0f, Biome = Heightmap.Biome.Meadows };
        _scope.WithWorld(flat);
        // A staircase standing across the straight line, north to south at x = 0.
        var run = new StairRun { From = new Vector2(0f, -40f), To = new Vector2(0f, 40f), Width = 2f };
        for (float z = -40f; z <= 40f; z += 2f)
            run.Pieces.Add(new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = "stone_stair", Position = new Vector3(0f, 40f, z) });
        StairStructures.Add(run);
        StairStructures.MarkStanding(run);

        var finder = new RoadPathfinder(flat) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f };
        List<Vector2>? path = finder.FindPath(new Vector2(-60f, 0f), new Vector2(60f, 0f));

        Assert.NotNull(path);
        Assert.DoesNotContain(path!, p => Mathf.Abs(p.x) < 4f && Mathf.Abs(p.y) < 36f);
    }

    /// <summary>Two staircases with less than 20 m of road between them are one;
    /// further apart they stay two, with road between.</summary>
    [Theory]
    [InlineData(16f, 1)]
    [InlineData(24f, 2)]
    public void StaircasesCloseTogetherAreOne(float between, int runs)
    {
        var path = new List<Vector2> { new(0, 0), new(8, 0), new(16, 0), new(16 + between, 0), new(24 + between, 0), new(40 + between, 0) };
        var steps = new List<(Vector2, Vector2)> { (path[1], path[2]), (path[3], path[4]) };

        var found = RoadStairRuns.Find(path, steps);

        Assert.Equal(runs, found.Count);
        Assert.Equal(1, found[0].from);
        Assert.Equal(4, found[found.Count - 1].to);
    }

    /// <summary>Two staircases further apart than the stone gap stay two, unless
    /// the ground between them is too steep for the road to be built there:
    /// then they are one (217 roads on one world were refused between two).</summary>
    [Theory]
    [InlineData(0.1f, 2)]
    [InlineData(0.4f, 1)]
    public void StaircasesWithRoadTooSteepBetweenThemAreOne(float gapGrade, int runs)
    {
        // Steps at 0.6 either side of a 30 m stretch climbing at gapGrade.
        var path = new List<Vector2> { new(0, 0), new(8, 0), new(16, 0), new(46, 0), new(54, 0), new(70, 0) };
        float H(Vector2 p) => p.x <= 8f ? 0f
            : p.x <= 16f ? (p.x - 8f) * 0.6f
            : p.x <= 46f ? 4.8f + (p.x - 16f) * gapGrade
            : p.x <= 54f ? 4.8f + 30f * gapGrade + (p.x - 46f) * 0.6f
            : 4.8f + 30f * gapGrade + 4.8f;
        var steps = new List<(Vector2, Vector2)> { (path[1], path[2]), (path[3], path[4]) };

        Assert.Equal(runs, RoadStairRuns.Find(path, steps, H).Count);
    }

    /// <summary>The path's ends are moved a little after the search (trimmed to
    /// the locations' edges); a step shortened that way still reads as stairs.</summary>
    [Fact]
    public void AStepTrimmedShortIsStillAStairStep()
    {
        var path = new List<Vector2> { new(0, 0), new(8, 0), new(13, 0.4f) };
        var found = RoadStairRuns.Find(path, new List<(Vector2, Vector2)> { (new Vector2(8, 0), new Vector2(16, 0)) });

        Assert.Equal((1, 2), Assert.Single(found));
    }

    /// <summary>Flat at 40 m north of z = 0, rising south at <see cref="Grade"/> to 40 + <see cref="Rise"/>; sea past 200 m.</summary>
    private sealed class BehindWorld : WorldGenerator
    {
        public float Grade = 1.6f, Rise = 12f;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            return 40f + Mathf.Clamp(-wy * Grade, 0f, Rise);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    /// <summary>A road arriving heading east along the foot of a face, its climb lying back the way it came (south-west):
    /// the staircase sets off within 90 degrees of the road's travel and turns on the stairs. Free, or relaid beside
    /// the road on its own heading, it set off 157.5 degrees off it, back alongside the road (the foot at
    /// Hildir_cave 1664,768; offline its staircase set off 158 degrees off its road). Held to 90, it sets off square across
    /// the road's end from the road's middle, its first stair a metre past the road's line, and no layout from beside the
    /// road fits: on the road as planned to it, it crossed its road. Set off from the ring at the road's end
    /// (RoadEndRing), it does not (RoadStairRuns.SpanAcrossItsRoad).</summary>
    [Fact]
    public void ARoadStaircaseSetsOffWithin90DegreesOfItsRoadsTravel()
    {
        var world = new BehindWorld();
        _scope.WithWorld(world);
        var path = new List<Vector2>();
        for (int x = -40; x <= 0; x += 8) path.Add(new Vector2(x, 4f));
        var stair = new List<Vector2> { new(-4f, -1f), new(-8f, -6f), new(-12f, -11f) };
        path.AddRange(stair);
        path.Add(stair[2] + new Vector2(-8f, 0f));
        path.Add(stair[2] + new Vector2(-16f, 0f));
        var steps = new List<(Vector2, Vector2)> { (new Vector2(0f, 4f), stair[0]), (stair[0], stair[1]), (stair[1], stair[2]) };
        RoadNetworkGenerator.Reset();
        List<RoadStairSpan>? spans = RoadStairRuns.Lay(path, steps, new List<RoadCrossing>(), world);
        Assert.NotNull(spans);
        RoadStairSpan span = Assert.Single(spans!);
        StairPlan plan = span.Plan;
        Assert.True(plan.Reached, plan.Reason);
        float off = Math.Abs(StairLayout.Wrap(plan.StartHeading - 90f + 180f) - 180f);
        Assert.True(off <= 90.01f, $"set off on {plan.StartHeading}");
        // The road as planned to its foot, arriving heading east: across it there.
        Vector2 foot = plan.Reversed ? span.ToPoint : span.FromPoint;
        var points = Enumerable.Range(0, 13).Select(k => foot - new Vector2(12 - k, 0f)).ToList();
        var road = new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(q => world.GetHeight(q.x, q.y)),
            points.ConvertAll(_ => default(RoadPointDebugInfo)), RoadNetworkGenerator.RoadWidth, 12f, true);
        Assert.Null(RoadStairRuns.SpanAcrossItsRoad(span, new[] { road }, out string piece));
        Assert.True(piece == "", piece);
    }

    /// <summary>A sharp ridge along z: faces of 1.2 up to a crest line at x = 0, 24 m over the flats either side.</summary>
    private sealed class SharpRidgeWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            return 40f + Mathf.Clamp(24f - Mathf.Abs(wx) * 1.2f, 0f, 24f);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    /// <summary>Over a sharp ridge the two staircases meet top to top: the first's top stair, a floor across, the second's
    /// top stair, with no road on the crest (at 3377,6425 the two should snap together, with no road spur at
    /// the top). Laid as before (each top on its own way, then floors only where the two lined up) the road was not built.</summary>
    [Fact]
    public void TwoStaircasesOverASharpRidgeMeetTopToTopWithNoRoadOnTheCrest()
    {
        bool Lay(out string what)
        {
            RoadNetworkGenerator.Reset();
            var world = new SharpRidgeWorld();
            _scope.WithWorld(world);
            SetPathfinder(new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
            bool built = RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0f), 0f, new Vector2(80f, 0f), 0f, 4f, "ridge");
            var crestRoad = new List<Vector2>();
            for (float x = -12f; x <= 12f; x += 1f) for (float z = -30f; z <= 30f; z += 1f)
                if (world.GetHeight(x, z) > 50f && RoadSpatialGrid.TryGetRoadWithin(new Vector2(x, z), 0.5f, out Vector2 at)) crestRoad.Add(at);
            what = $"built {built}, {StairStructures.Runs.Count} staircase(s), road on the crest at {string.Join(" ", crestRoad.Distinct().Take(6))}";
            if (!built || StairStructures.Runs.Count != 2 || crestRoad.Count > 0) return false;
            // The two tops at one height, joined: the second's highest floors reach the first's top stair's edge.
            StairRun up = StairStructures.Runs[0], down = StairStructures.Runs[1];
            float top = up.Pieces.Where(p => p.Kind == StructurePieceKind.StairStep).Max(p => p.Position.y);
            return down.Pieces.Exists(p => p.Kind == StructurePieceKind.StairStep && Mathf.Abs(p.Position.y - top) < 0.05f);
        }
        Assert.True(Lay(out string laid), laid);
        // The second searched down from the far edge of a 2x2 on from the first's top stair (a ridge has at least one 2x2 between the up and down stairs).
        Assert.True(RoadStairRuns.RidgesSnapped >= 1, $"not snapped ({RoadStairRuns.RidgesBridged} bridged): {laid}");
        StairRun first = StairStructures.Runs[0];
        float crest = first.Pieces.Where(p => p.Kind == StructurePieceKind.StairStep).Max(p => p.Position.y) + StairLayout.PieceRise;
        // A 2x2 at the crest, level with the first's top stair: carried on from the first (CarryTopTo), or snapped off as the
        // second's (SnapOffCrest) where the first's top set off from its road's ring leaves no room to carry it.
        Assert.Contains(StairStructures.Runs.SelectMany(r => r.Pieces), p => p.Kind == StructurePieceKind.StairFloor && Mathf.Abs(p.Position.y + StairKit.Stone.FloorTop - crest) < 0.05f);
        // Still no road on the crest (at 6678,2440 a 2x2 at the ridge top and stairs snapping the whole way up and down are preferred), which Lay checked.
    }

    /// <summary>The sharp ridge with its east flats half a metre higher: two staircases laid each up from its own foot end
    /// half a metre apart in height at the crest.</summary>
    private sealed class OffsetRidgeWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            return wx < 0f ? 40f + Mathf.Clamp(24f + wx * 1.2f, 0f, 24f) : 40.5f + Mathf.Clamp(23.5f - wx * 1.2f, 0f, 23.5f);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    /// <summary>Over a ridge, two tops that could not be joined (the second laid from its own foot, their heights apart)
    /// leave no stub of road between them on the crest (at 6678,2440, about 2-3 m; short sections of road between two stairs
    /// are not allowed): one staircase over both, or none.</summary>
    [Fact]
    public void TwoTopsOverARidgeLeaveNoStubOfRoadBetweenThem()
    {
        string Lay(out bool built)
        {
            RoadNetworkGenerator.Reset();
            var world = new OffsetRidgeWorld();
            _scope.WithWorld(world);
            SetPathfinder(new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
            built = RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0f), 0f, new Vector2(80f, 0f), 0f, 4f, "ridge");
            var crestRoad = new List<Vector2>();
            for (float x = -12f; x <= 12f; x += 1f) for (float z = -30f; z <= 30f; z += 1f)
                if (world.GetHeight(x, z) > 58f && RoadSpatialGrid.TryGetRoadWithin(new Vector2(x, z), 0.5f, out Vector2 at)) crestRoad.Add(at);
            return $"built {built}, {StairStructures.Runs.Count} staircase(s), {RoadStairRuns.CrestStubs} stub(s), road on the crest at {string.Join(" ", crestRoad.Distinct().Take(6))}";
        }
        string after = Lay(out bool builtAfter);
        // What is built keeps no road on the crest: a stub met on an attempt is refused (CrestStubs counts the attempts).
        Assert.True(!builtAfter || after.EndsWith("road on the crest at ") || StairStructures.Runs.Count == 1, "a stub kept: " + after);
    }

    /// <summary>A sharp ridge along z: faces of 0.9 from flats at 40 up to a crest line at x = 0, 24.3 m over them: a staircase
    /// laid up from a foot at 40 ends a whole number of rises up, 0.3 m off the ground at the crest.</summary>
    private sealed class OffRiseCrestWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            return 40f + Mathf.Clamp(24.3f - Mathf.Abs(wx) * 0.9f, 0f, 24.3f);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    /// <summary>Over a crest whose ground stands off the rise from the first staircase's top, the two still meet there: the
    /// third of a rise between that top and the ground at the crest is no steep road between two staircases going the same
    /// way (merged into one, which cannot cross a crest, crest and pit pairs were lost at 26 places over six worlds: a 0.3-0.5 m
    /// step over the 0.3-1.7 m from the first's end).</summary>
    [Fact]
    public void TwoStaircasesMeetOverACrestOffTheRiseFromTheFirstsTop()
    {
        var world = new OffRiseCrestWorld();
        _scope.WithWorld(world);
        var path = new List<Vector2>();
        for (float x = -48f; x <= 48.01f; x += 8f) path.Add(new Vector2(x, 0f));
        var steps = new List<(Vector2 from, Vector2 to)>();
        for (int i = 1; i < path.Count; i++)
            if (Mathf.Abs(world.GetHeight(path[i].x, 0f) - world.GetHeight(path[i - 1].x, 0f)) / 8f > 0.5f) steps.Add((path[i - 1], path[i]));
        List<RoadStairSpan>? spans = RoadStairRuns.Lay(path, steps, new List<RoadCrossing>(), world);
        string laid = spans == null ? "not laid" : string.Join(" | ", spans.Select(s => FormattableString.Invariant(
            $"{s.FromIndex}..{s.ToIndex} {s.FromHeight:F2}->{s.ToHeight:F2}")));
        Assert.True(spans != null && spans.Count == 2, laid);
        // Up one side and down the other, met with no road between.
        Assert.True(Vector2.Distance(spans![0].ToPoint, spans[1].FromPoint) < 0.05f, laid);
    }

    /// <summary>Two sharp ridges (crests at x = -24 and 24, on the road's 8 m path points, 21.5 m over the flats, faces of 1)
    /// with a narrow valley between them (its bottom 5 m wide at x = 0): up, over, down, up, over and down again.</summary>
    private sealed class RidgeValleyRidgeWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            float x = Mathf.Abs(wx);
            return x <= 24f ? 40f + Mathf.Max(0f, x - 2.5f) : 40f + Mathf.Max(0f, 21.5f - (x - 24f));
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    /// <summary>A chain over ridge, valley and ridge (the valley 21.5 m deep, its sides 1:1): the road is built, across the valley
    /// whichever way costs less. Either staircases meeting at each turn with no road between them (a ridge transitions to a valley
    /// and maybe back again), or a bridge from rim to rim with a staircase up each outer face.</summary>
    [Fact(Skip = "Waits on a dry-valley bridge outside the Mistlands: the 2x2 kit's staircases down into this valley lie on the road's own way across it, and only the Mistlands bridges a dry valley")]
    public void ARidgeValleyRidgeChainIsCrossedByStaircasesOrABridge()
    {
        RoadNetworkGenerator.Reset();
        var world = new RidgeValleyRidgeWorld();
        _scope.WithWorld(world);
        SetPathfinder(new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        bool built = RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0f), 0f, new Vector2(80f, 0f), 0f, 4f, "chain");
        var road = new List<Vector2>();
        for (float x = -36f; x <= 36f; x += 1f) for (float z = -60f; z <= 60f; z += 1f)
            if (RoadSpatialGrid.TryGetRoadWithin(new Vector2(x, z), 0.5f, out Vector2 at)) road.Add(at);
        int joined = RoadStairRuns.RidgesSnapped + RoadStairRuns.RidgesBridged;
        var valleyBridges = RoadNetworkGenerator.GetRoadCrossings()
            .Where(c => c.ValleyJump && Mathf.Min(c.FromBank.x, c.ToBank.x) < -20f && Mathf.Max(c.FromBank.x, c.ToBank.x) > 20f).ToList();
        string laid = $"built {built}, {StairStructures.Runs.Count} staircase(s) {string.Join(" | ", StairStructures.Runs.Select(r => r.From + "->" + r.To))}, ridges snapped {RoadStairRuns.RidgesSnapped}, bridged {RoadStairRuns.RidgesBridged}, refused {RoadStairRuns.CrestStubs}, valleys {RoadStairRuns.ValleysMet}, valley bridges {valleyBridges.Count}, road off the flats at {string.Join(" ", road.Distinct().Take(8))}";
        Assert.True(built, laid);
        // Staircases: four, up, down, up, down, both crests joined, and no road anywhere between the outer feet (x = -40 and
        // 40): the two in the valley meet on a shared floor.
        bool stairs = StairStructures.Runs.Count == 4 && joined >= 2 && RoadStairRuns.ValleysMet >= 1 && road.Count == 0;
        // A bridge: rim to rim over the valley, and a staircase up each outer face to it.
        bool bridge = valleyBridges.Count == 1 && StairStructures.Runs.Count == 2;
        Assert.True(stairs || bridge, laid);
    }

    /// <summary>A flat world: a road built north-south, then one east-west across it. The second would cross the first at
    /// grade and is not built (roads that cross at grade are disallowed even if that rejects a POI); one that
    /// ends on the first (a T) is.</summary>
    private sealed class FlatWorld : WorldGenerator
    {
        public float Height = 40f;
        public override float GetHeight(float wx, float wy) => Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f ? 0f : Height;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override int GetSeed() => 77;
    }

    /// <summary>A foot straight on with its road stays in the road's middle only where the road ends there; where another road
    /// goes on past it (the ridge at -6961,3369, the foot at -6951.1,3360.4 on a junction, road all round it:
    /// the stairs would get in the way of the straight-through road versus branching off to the side), it is a spur: its ring
    /// keeps the lane clear both ways (RoadEndRing.Through).</summary>
    [Fact]
    public void AFootOnARoadGoingOnPastItIsASpur()
    {
        bool GoesOn(bool trunk, bool pad = false)
        {
            RoadNetworkGenerator.Reset();
            var world = new FlatWorld();
            _scope.WithWorld(world);
            SetPathfinder(new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f });
            if (trunk) Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, -80f), 0f, new Vector2(0f, 80f), 0f, 4f, "trunk"));
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0.5f), 0f, new Vector2(-1f, 0.5f), 0f, 4f, "arriving"));
            if (pad)
            {
                // A start pad straight on past the end, out to 5 m (StartPads, stored unpainted), as one is laid under a
                // staircase's first pieces: its own, or one an earlier road laid there.
                var points = new List<Vector2>();
                for (float x = 0f; x <= 5f; x += RoadEndRing.PadSpacing)
                    for (float z = -1f; z <= 2f; z += RoadEndRing.PadSpacing) points.Add(new Vector2(x, z));
                RoadSpatialGrid.CommitLevelled(points, points.ConvertAll(_ => 40f), RoadEndRing.PadPointWidth, unpainted: true);
            }
            var path = new List<Vector2>();
            for (float x = 0f; x >= -60f; x -= 4f) path.Add(new Vector2(x, 0.5f));
            path.Reverse();
            int foot = path.Count - 1;
            return RoadStairRuns.RoadGoesOnPast(path[foot], path, foot, -1);
        }
        Assert.True(GoesOn(trunk: true), "the trunk going on past the foot was not seen");
        Assert.False(GoesOn(trunk: false), "the arriving road's own end was taken for another road");
        Assert.False(GoesOn(trunk: false, pad: true), "a start pad past the road's end was taken for a road going on past it");
        Assert.True(GoesOn(trunk: true, pad: true), "the trunk going on past the foot was not seen beside a start pad");
    }

    /// <summary>A road's plain end is not through for its own staircase's start pad: read once the road is stored (as the
    /// ring is tallied, after the road's start pads are stored), the black marble foot at (1808,-6616) was through for its
    /// pad's corner (1809,-6611.5), 4.6 m out toward its stairs and over 3 m off the road arriving from the south-south-east.
    /// The road's points and the pad's 58 as stored live.</summary>
    [Fact]
    public void AFootsOwnStartPadIsNoRoadGoingOnPastIt()
    {
        RoadSpatialGrid.Clear();
        var end = new Vector2(1808f, -6616f);
        Vector2[] back =
        {
            new(0.00f, 0.00f), new(0.41f, -0.81f), new(0.81f, -1.63f), new(1.22f, -2.44f), new(1.63f, -3.25f), new(2.03f, -4.06f),
            new(2.44f, -4.88f), new(2.85f, -5.69f), new(3.25f, -6.50f), new(3.66f, -7.32f), new(4.10f, -8.10f), new(4.62f, -8.84f),
            new(5.21f, -9.52f), new(5.87f, -10.14f), new(6.59f, -10.69f), new(7.35f, -11.16f), new(8.16f, -11.56f), new(9.01f, -11.87f),
            new(9.82f, -12.13f),
        };
        var road = back.Select(o => end + o).ToList();
        RoadSpatialGrid.CommitLevelled(road, road.ConvertAll(_ => 92.7f), RoadNetworkGenerator.RoadWidth);
        Assert.False(RoadStairRuns.RoadGoesOnPast(end, road), "the road's own end alone was through");

        // Its start pad: 14 points at 92.587 toward its stairs, 44 at 92.687 round the end.
        Vector2[] low =
        {
            new(-1f, 1.5f), new(-1f, 2f), new(-0.5f, 1.5f), new(-0.5f, 2f), new(0f, 1.5f), new(0f, 2f), new(0f, 2.5f), new(0.5f, 1.5f),
            new(0.5f, 2f), new(0.5f, 2.5f), new(0.5f, 3f), new(1f, 3.5f), new(1f, 4f), new(1f, 4.5f),
        };
        var pad = low.Select(o => end + o).ToList();
        var heights = pad.ConvertAll(_ => 92.587f);
        Vector2[] high =
        {
            new(-2f, 0f), new(-1.5f, -1f), new(-1.5f, -0.5f), new(-1.5f, 0f), new(-1.5f, 0.5f), new(-1.5f, 1f), new(-1f, -1.5f),
            new(-1f, -1f), new(-1f, -0.5f), new(-1f, 0f), new(-1f, 0.5f), new(-1f, 1f), new(-0.5f, -1.5f), new(-0.5f, -1f),
            new(-0.5f, -0.5f), new(-0.5f, 0f), new(-0.5f, 0.5f), new(-0.5f, 1f), new(0f, -2f), new(0f, -1.5f), new(0f, -1f),
            new(0f, -0.5f), new(0f, 0f), new(0f, 0.5f), new(0f, 1f), new(0.5f, -1.5f), new(0.5f, -1f), new(0.5f, -0.5f),
            new(0.5f, 0f), new(0.5f, 0.5f), new(0.5f, 1f), new(1f, -1.5f), new(1f, -1f), new(1f, -0.5f), new(1f, 0f),
            new(1f, 0.5f), new(1f, 1f), new(1f, 1.5f), new(1.5f, -1f), new(1.5f, -0.5f), new(1.5f, 0f), new(1.5f, 0.5f),
            new(1.5f, 1f), new(2f, 0f),
        };
        foreach (Vector2 o in high) { pad.Add(end + o); heights.Add(92.687f); }
        Assert.Equal(58, pad.Count);
        RoadSpatialGrid.CommitLevelled(pad, heights, RoadEndRing.PadPointWidth, unpainted: true);
        Assert.False(RoadStairRuns.RoadGoesOnPast(end, road), "its own start pad was taken for a road going on past the foot");
        Assert.False(RoadStairRuns.RoadGoesOnPast(end, road, foot: 0, step: 1), "its own start pad was taken for a road going on past the foot");

        // The same corner painted, a road: through (the read still sees road going on there).
        RoadSpatialGrid.CommitLevelled(new List<Vector2> { end + new Vector2(1f, 4.5f) }, new List<float> { 92.587f }, RoadEndRing.PadPointWidth);
        Assert.True(RoadStairRuns.RoadGoesOnPast(end, road), "a painted road 4.6 m past the foot was not seen");
    }

    /// <summary>The walked pieces of the staircase at the foot -6951.1,3360.4 as laid (6952 m west and 3360 m north of where
    /// they stood, raised <paramref name="up"/>): up from the road's middle on 247.5, round to the north and onto the floors at
    /// 40 at its top. Its road came to the foot straight on from the north-east; its columns are left out.</summary>
    private static StairRun JunctionFootRun(float up)
    {
        var run = new StairRun { From = new Vector2(0f, 0f), To = new Vector2(-8f, 8f), Width = RoadStairRuns.Width, Kit = StairKit.BlackMarble };
        void Add(StructurePieceKind kind, string prefab, float x, float y, float z, float yaw) =>
            run.Pieces.Add(new StructurePiece { Kind = kind, Prefab = prefab, Position = new Vector3(x, y + up, z), YawDegrees = yaw });
        void S(float x, float y, float z, float yaw) => Add(StructurePieceKind.StairStep, StairKit.BlackMarble.Stair, x, y, z, yaw);
        void B(float x, float y, float z, float yaw) => Add(StructurePieceKind.StairFiller, "blackmarble_2x1x1", x, y, z, yaw);
        void C(float x, float y, float z, float yaw) => Add(StructurePieceKind.StairFiller, "blackmarble_1x1", x, y, z, yaw);
        void F(float x, float y, float z, float yaw) => Add(StructurePieceKind.StairFloor, StairKit.BlackMarble.Floor, x, y, z, yaw);
        S(0.924f, 31.477f, 0.383f, 67.5f); S(-0.924f, 32.477f, -0.383f, 67.5f); B(-2.310f, 32.977f, -0.957f, 67.5f);
        S(-3.230f, 33.477f, -0.842f, 90f); B(-4.730f, 33.977f, -0.842f, 90f); S(-5.537f, 34.477f, -0.383f, 112.5f);
        S(-7.385f, 35.477f, 0.383f, 112.5f); B(-8.771f, 35.977f, 0.957f, 112.5f); S(-9.340f, 36.477f, 1.689f, 135f);
        S(-10.754f, 37.477f, 3.104f, 135f); B(-11.814f, 37.977f, 4.164f, 135f); C(-12.254f, 37.977f, 4.018f, 180f);
        S(-11.754f, 38.477f, 5.518f, 180f); B(-11.754f, 38.977f, 7.018f, 180f); S(-11.295f, 39.477f, 7.824f, 202.5f);
        F(-8.683f, 39.977f, 8.906f, 292.5f); F(-10.530f, 39.977f, 9.672f, 22.5f);
        return run;
    }

    /// <summary>A road meets a staircase laid before it only at its foot or top, straight on, never on or under its flight: a
    /// road laid later joined the network at the foot at -6951.1,3360.4 from the south-west, levelled to the foot's height,
    /// and ran on under the first stairs, which stood in its middle (the spur rule, <see cref="AFootOnARoadGoingOnPastItIsASpur"/>,
    /// sees only the roads laid before the staircase). That road, as stored there, is refused; the staircase's own road, straight
    /// on from the north-east, is not. Generated, a road to the foot from the south-west stores nothing on the flight.</summary>
    [Fact]
    public void ARoadLaidLaterNeverRunsUnderAStaircasesFlight()
    {
        RoadSpatialGrid.PlannedPath Road(params (float x, float z, float h)[] points) => new(points.Select(p => new Vector2(p.x, p.z)).ToList(),
            points.Select(p => p.h).ToList(), points.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f, 40f, false);
        RoadNetworkGenerator.Reset();
        _scope.WithWorld(new FlatWorld());
        StairStructures.Add(JunctionFootRun(0f));
        // As stored on the integration build: down the slope to the foot's height 7 m short of it, level from there.
        var underIt = Road((-15.09f, -7.54f, 36.02f), (-13.15f, -6.57f, 35.12f), (-11.11f, -5.55f, 34.14f), (-9.04f, -4.52f, 33.34f),
            (-7.00f, -3.50f, 32.48f), (-6.01f, -3.01f, 32.48f), (-5.06f, -2.53f, 32.48f), (-4.15f, -2.07f, 32.48f), (-3.28f, -1.64f, 32.48f),
            (-2.48f, -1.24f, 32.48f), (-1.74f, -0.87f, 32.48f), (-1.08f, -0.54f, 32.48f), (-0.49f, -0.25f, 32.48f), (0f, 0f, 32.48f));
        Assert.Equal(RoadNetworkGenerator.OnAFlightRefusal, RoadNetworkGenerator.MeetingRefusal(new() { underIt }, out Vector2 at));
        Assert.InRange(Vector2.Distance(at, new Vector2(-5f, -2.5f)), 0f, 4f);
        var straightOn = Road((0f, 0f, 32.48f), (0.85f, 0.42f, 32.48f), (1.70f, 0.85f, 32.48f), (2.54f, 1.27f, 32.51f),
            (3.39f, 1.70f, 32.57f), (4.24f, 2.12f, 32.66f), (5.09f, 2.54f, 32.78f), (5.94f, 2.97f, 32.94f), (6.78f, 3.39f, 33.13f));
        Assert.Null(RoadNetworkGenerator.MeetingRefusal(new() { straightOn }, out _));

        // Generated on flat ground at 40 (the staircase raised to stand on it): the road from the south-west to the foot.
        RoadNetworkGenerator.Reset();
        var world = new FlatWorld();
        _scope.WithWorld(world);
        SetPathfinder(new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f });
        StairRun laid = JunctionFootRun(40f - 32.48f);
        StairStructures.Add(laid);
        StairStructures.MarkStanding(laid);
        RoadNetworkGenerator.GenerateRoad(new Vector2(-60f, -30f), 0f, new Vector2(0f, 0f), 0f, 4f, "to the foot");
        var stored = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(-6f, 0f, 2f), 20f).Where(rp => rp.IsRoad).ToList();
        Assert.False(RoadStairRuns.OnALaidFlight(stored.Select(rp => rp.p).ToList(), stored.Select(rp => rp.h).ToList(), out Vector2 onIt),
            FormattableString.Invariant($"road stored on the flight at ({onIt.x:F1}, {onIt.y:F1})"));
    }

    /// <summary>The road from the south-west onto the lower stairs of the staircase at the foot -6951.1,3360.4 (as stored on
    /// the integration build, <see cref="ARoadLaidLaterNeverRunsUnderAStaircasesFlight"/>), the staircase laid
    /// <paramref name="up"/> higher than it stood.</summary>
    private static RoadSpatialGrid.PlannedPath OntoTheLowerStairs(float up = 0f)
    {
        var points = new (float x, float z, float h)[] { (-15.09f, -7.54f, 36.02f), (-13.15f, -6.57f, 35.12f), (-11.11f, -5.55f, 34.14f),
            (-9.04f, -4.52f, 33.34f), (-7.00f, -3.50f, 32.48f), (-6.01f, -3.01f, 32.48f), (-5.06f, -2.53f, 32.48f), (-4.15f, -2.07f, 32.48f),
            (-3.28f, -1.64f, 32.48f), (-2.48f, -1.24f, 32.48f), (-1.74f, -0.87f, 32.48f), (-1.08f, -0.54f, 32.48f), (-0.49f, -0.25f, 32.48f), (0f, 0f, 32.48f) };
        return new(points.Select(p => new Vector2(p.x, p.z)).ToList(), points.Select(p => p.h + up).ToList(),
            points.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f, 40f, false);
    }

    /// <summary>A road laid later onto a staircase's lower stairs, not its foot, is not refused where a landing fits: the
    /// flight is cut back from below to the row the road reaches and the road's ground there filled level with that row, the
    /// road meeting its foot. The south-west road at the foot -6951.1,3360.4 lands on the row whose base stands at 34.48 (two
    /// metres over the foot), its fill within the cap; the three stairs under it, their two wedges and their cuts go, the staircase's
    /// record keeps its foot where the road meets it, and the road runs on or under no flight. Generated on flat ground, a
    /// road from the south-west to the foot lands on the same row: nothing stored on the flight, the road level with the
    /// row at its new foot.</summary>
    [Fact]
    public void ARoadOntoAStaircasesLowerStairsLandsOnTheRowItReaches()
    {
        RoadNetworkGenerator.Reset();
        _scope.WithWorld(new FlatWorld());
        StairRun standing = JunctionFootRun(0f);
        StairStructures.Add(standing);
        StairStructures.MarkStanding(standing);
        var road = OntoTheLowerStairs();
        var plans = new List<RoadSpatialGrid.PlannedPath> { road };
        Assert.Null(RoadNetworkGenerator.MeetingRefusal(plans, out _, out var landing, landOnFlight: true));
        Assert.NotNull(landing);
        Assert.Equal(34.477f, landing!.Height, 3);
        Assert.Equal(5, landing.Removed.Count);
        foreach (var (p, h) in road.Points.Zip(landing.Heights, (p, h) => (p, h)))
            Assert.InRange(Mathf.Abs(h - 40f), 0f, RoadConstants.TerrainDeltaMax);
        // Level with the row where it meets the row's foot.
        var atFoot = Enumerable.Range(0, road.Points.Count).Where(i => Vector2.Distance(road.Points[i], landing.Foot) <= RoadStairRuns.JoiningStep).ToList();
        Assert.NotEmpty(atFoot);
        Assert.All(atFoot, i => Assert.Equal(landing.Height, landing.Heights[i], 3));

        // Its cuts as laid, one under the second stair, one under the sixth (a road far off: the network stands).
        var far = new List<Vector2> { new(300f, 300f), new(340f, 300f) };
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(far, far.Select(_ => 40f).ToList(), far.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f, 40f, false));
        RoadSpatialGrid.CommitCuts(new[] { new Vector3(-0.924f, 32.327f, -0.383f), new Vector3(-9.34f, 36.327f, 1.689f) }, StairLayout.CutRadius * 2f);
        Assert.Null(RoadNetworkGenerator.MeetingRefusal(plans, out _, out landing, landOnFlight: true));

        RoadNetworkGenerator.LandOnTheFlight(plans, landing!);
        Assert.True(RoadStairRuns.LayLanding(landing!));
        // The cut under the stair taken away would dig through the landing's fill: it goes; the one up the flight stays.
        List<float> Cuts(float x, float z) => RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(x, 0f, z), 0.1f).Where(rp => rp.cutOnly).Select(rp => rp.h).ToList();
        Assert.Empty(Cuts(-0.924f, -0.383f));
        Assert.NotEmpty(Cuts(-9.34f, 1.689f));
        StairRun trimmed = Assert.Single(StairStructures.Runs);
        Assert.Same(landing.Trimmed, trimmed);
        Assert.Equal(landing.Foot, trimmed.From);
        Assert.Equal(standing.To, trimmed.To);
        Assert.Equal(standing.Pieces.Count - 5, trimmed.Pieces.Count);
        Assert.DoesNotContain(trimmed.Pieces, p => p.Kind == StructurePieceKind.StairStep && p.Position.y < landing.Height - 0.01f);
        Assert.False(RoadStairRuns.OnALaidFlight(road.Points, road.Heights, out Vector2 onIt),
            FormattableString.Invariant($"landed road on the flight at ({onIt.x:F1}, {onIt.y:F1})"));
        // Saved and read back as cut back.
        StairRun read = Assert.Single(StairStructures.Parse(StairStructures.Serialize()));
        Assert.Equal(trimmed.From, read.From);
        Assert.Equal(trimmed.Pieces.Select(p => p.Position), read.Pieces.Select(p => p.Position));
        // The cells under the stairs taken away are open to road again.
        foreach (Vector2 r in landing.Removed.Where(r => Vector2.Distance(r, trimmed.From) > RoadPathfinder.CellSize))
            Assert.False(StairStructures.Standing(Mathf.RoundToInt(r.x / RoadPathfinder.CellSize), Mathf.RoundToInt(r.y / RoadPathfinder.CellSize)));

        // Generated on flat ground at 40 (the staircase raised to stand on it): the road from the south-west to the foot.
        RoadNetworkGenerator.Reset();
        var world = new FlatWorld();
        _scope.WithWorld(world);
        SetPathfinder(new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f });
        StairRun laid = JunctionFootRun(40f - 32.48f);
        StairStructures.Add(laid);
        StairStructures.MarkStanding(laid);
        RoadNetworkGenerator.GenerateRoad(new Vector2(-60f, -30f), 0f, new Vector2(0f, 0f), 0f, 4f, "to the foot");
        var stored = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(-6f, 0f, 2f), 20f).Where(rp => rp.IsRoad).ToList();
        Assert.False(RoadStairRuns.OnALaidFlight(stored.Select(rp => rp.p).ToList(), stored.Select(rp => rp.h).ToList(), out onIt),
            FormattableString.Invariant($"road stored on the flight at ({onIt.x:F1}, {onIt.y:F1})"));
        // It landed on the same row, 34.48 + 7.52: the staircase cut back to it and the road stored level with it at its foot.
        Assert.Equal(1, RoadStairRuns.FlightLandings);
        StairRun cutBack = Assert.Single(StairStructures.Runs);
        Assert.Equal(laid.Pieces.Count - 5, cutBack.Pieces.Count);
        var footRoad = stored.Where(rp => Vector2.Distance(rp.p, cutBack.From) <= RoadStairRuns.JoiningStep).ToList();
        Assert.NotEmpty(footRoad);
        Assert.All(footRoad, rp => Assert.Equal(42f, rp.h, 2));
        // The road that meets its new foot is the landing's road, recorded on the moved end (RoadStairRuns.MeetLanding), and
        // the foot reads onto it; before, the moved end's road was dropped and the foot read NOT VERIFIED.
        Assert.NotNull(cutBack.FromRoad);
        Assert.True(Vector2.Distance(cutBack.FromRoad!.Points[0], cutBack.From) <= RoadStairRuns.JoiningStep);
        Assert.NotNull(cutBack.FromRoad.Stored());
        StairEnds.End newFoot = StairEnds.Read(cutBack, StairEnds.WithRoads((x, z) => 40f), StairStructures.MetAtEnds(cutBack)).Single(e => !e.Top);
        Assert.True(newFoot.Verdict == StairEnds.Verdict.Ok, StairEnds.Line(newFoot));
    }

    /// <summary>
    /// Generation stores the road its final check read (RoadNetworkGenerator.WrittenRoad), a landing's edits made first in
    /// what was checked: the south-west road to the foot lands on the row two metres up (RoadStairRuns.LayLanding), raising
    /// the staircase's own road from the north-east there. What is stored is exactly the network before it with the raises the check read
    /// (RoadSpatialGrid.Raises, the commit's own rule) and the cuts the landing takes back gone, plus the points the check
    /// read (RoadNetworkGenerator.WrittenPoints: its road, junction fills, start pads, ways into its locations and cuts), and
    /// its staircases are the runs the check read. Before, the landing's raises were not among those checked, the fills and
    /// pads were read again after the check, and the staircases were made again.
    /// </summary>
    [Fact]
    public void GenerationStoresTheRoadItsFinalCheckRead()
    {
        RoadNetworkGenerator.Reset();
        var world = new FlatWorld();
        _scope.WithWorld(world);
        SetPathfinder(new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f });
        StairRun laid = JunctionFootRun(40f - 32.48f);
        StairStructures.Add(laid);
        StairStructures.MarkStanding(laid);
        // The staircase's own road from the north-east, at the old foot's height, which the landing raises with it.
        RoadSpatialGrid.PlannedPath own = StaircasesOwnRoad();
        own = new RoadSpatialGrid.PlannedPath(own.Points, own.Heights.Select(h => h + 40f - 32.48f).ToList(), own.DebugInfos, own.Width, own.TotalLength, false);
        RoadSpatialGrid.Commit(own);
        laid.FromRoad = StairRoad.Of(own, atStart: false);
        var centre = new Vector3(-30f, 0f, -15f);
        RoadNetworkGenerator.WrittenRoad? read = null;
        List<RoadSpatialGrid.RoadPoint>? before = null;
        RoadNetworkGenerator.RoadChecked = w => { read = w; before = RoadSpatialGrid.GetRoadPointsNearPosition(centre, 150f); };
        try { Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-60f, -30f), 0f, new Vector2(0f, 0f), 0f, 4f, "to the foot")); }
        finally { RoadNetworkGenerator.RoadChecked = null; }
        Assert.Equal(1, RoadStairRuns.FlightLandings);
        Assert.NotNull(read);
        Assert.NotEmpty(read!.Ground.Raised);

        var after = RoadSpatialGrid.GetRoadPointsNearPosition(centre, 150f);
        static string Key(RoadSpatialGrid.RoadPoint rp) => FormattableString.Invariant(
            $"{rp.p.x:R},{rp.p.y:R} h{rp.h:R} w{rp.w:R} {(rp.cutOnly ? "c" : "")}{(rp.intoLocation ? "i" : "")}{(rp.unpainted ? "u" : "")}{(rp.mound ? "m" : "")}{(rp.paintOnly ? "p" : "")}");
        var raises = new RoadSpatialGrid.Raises(read.Ground.Raised);
        var afterKeys = after.Select(Key).ToList();
        var expected = new List<string>();
        foreach (RoadSpatialGrid.RoadPoint rp in before!)
        {
            RoadSpatialGrid.RoadPoint now = rp;
            if (raises.To(rp, CrossIslandReads.Me, out float to)) now.h = to;
            // The cuts under the stairs the landing takes away go (RoadSpatialGrid.TakeBackCuts).
            if (now.cutOnly && !afterKeys.Contains(Key(now))) continue;
            expected.Add(Key(now));
        }
        expected.AddRange(RoadNetworkGenerator.WrittenPoints(read.Footprint).Select(Key));
        expected.Sort(StringComparer.Ordinal);
        afterKeys.Sort(StringComparer.Ordinal);
        var missing = expected.Except(afterKeys).ToList();
        var extra = afterKeys.Except(expected).ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0 && expected.Count == afterKeys.Count,
            $"{expected.Count} checked, {afterKeys.Count} stored; checked not stored: {string.Join("; ", missing.Take(6))}; stored not checked: {string.Join("; ", extra.Take(6))}");
        // Its staircases are the runs its check read; the staircase it landed on stands cut back.
        foreach (var (run, _, _) in read.Footprint.Runs) Assert.Contains(StairStructures.Runs, r => ReferenceEquals(r, run));
        Assert.DoesNotContain(StairStructures.Runs, r => ReferenceEquals(r, laid));
    }

    /// <summary>
    /// A road's checks read the ground its landing leaves (RoadNetworkGenerator.AsWritten, RoadTerrainModifier.AsStored with
    /// its landing): the south-west road to the foot lands on the row two metres up over the staircase's lower stairs, whose
    /// cuts as laid dug under them. The landing takes back the cut under the stairs it takes away (RoadStairRuns.CutsTakenBack)
    /// and cuts the staircase back (its walk floor with it), and the ground its final check read round the landing is the
    /// ground the network leaves once it is stored. Before, the checks read the old cuts and the old staircase's floor, and
    /// were made on ground the commit then changed.
    /// </summary>
    [Fact]
    public void ALandingsChecksReadTheGroundItsCommitLeaves()
    {
        RoadNetworkGenerator.Reset();
        var world = new FlatWorld();
        _scope.WithWorld(world);
        SetPathfinder(new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f });
        float up = 40f - 32.48f;
        StairRun laid = JunctionFootRun(up);
        StairStructures.Add(laid);
        StairStructures.MarkStanding(laid);
        // Its cuts as laid, one under the second stair (taken back with it), one under the sixth.
        RoadSpatialGrid.CommitCuts(new[] { new Vector3(-0.924f, 32.327f + up, -0.383f), new Vector3(-9.34f, 36.327f + up, 1.689f) }, StairLayout.CutWidth);
        var far = new List<Vector2> { new(300f, 300f), new(340f, 300f) };
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(far, far.Select(_ => 40f).ToList(), far.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f, 40f, false));
        var vertices = new List<(int x, int z)>();
        for (int x = -14; x <= 4; x++) for (int z = -8; z <= 12; z++) vertices.Add((x, z));
        Dictionary<(int, int), float>? read = null;
        RoadNetworkGenerator.RoadChecked = w =>
        {
            using (RoadNetworkGenerator.AsWritten(w.Footprint))
            {
                Func<int, int, float> finished = StructureGround.Vertices(world);
                read = vertices.ToDictionary(v => v, v => finished(v.x, v.z));
            }
        };
        try { Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-60f, -30f), 0f, new Vector2(0f, 0f), 0f, 4f, "to the foot")); }
        finally { RoadNetworkGenerator.RoadChecked = null; }
        Assert.Equal(1, RoadStairRuns.FlightLandings);
        Assert.Empty(RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(-0.924f, 0f, -0.383f), 0.1f).Where(rp => rp.cutOnly));
        Func<int, int, float> stored = StructureGround.Vertices(world);
        var differ = vertices.Where(v => Mathf.Abs(stored(v.x, v.z) - read![v]) > 1e-4f)
            .Select(v => FormattableString.Invariant($"({v.x}, {v.z}) {read![v]:F2} checked, {stored(v.x, v.z):F2} stored")).ToList();
        Assert.True(differ.Count == 0, string.Join("; ", differ.Take(8)));
    }

    /// <summary>
    /// The keep-clear judges the ground a road's landing changes (RoadNetworkGenerator.UnderAPlannedStructure over its landing's
    /// edits): a landing onto the staircase at the foot -6951.1,3360.4 takes back the cut under the second stair, whose batter
    /// reaches under another staircase planned 2.5 m south of it. Nothing else of the road reaches that staircase, but taking
    /// the cut back changes the ground under it, so the road is refused for it. Before, the scan was bounded by the road's own
    /// points and raises alone, and never asked of that ground.
    /// </summary>
    [Fact]
    public void ALandingsTakenBackCutUnderAnotherStructureIsJudged()
    {
        RoadNetworkGenerator.Reset();
        _scope.WithWorld(new FlatWorld());
        StairRun standing = JunctionFootRun(0f);
        StairStructures.Add(standing);
        StairStructures.MarkStanding(standing);
        var road = OntoTheLowerStairs();
        var plans = new List<RoadSpatialGrid.PlannedPath> { road };
        var far = new List<Vector2> { new(300f, 300f), new(340f, 300f) };
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(far, far.Select(_ => 40f).ToList(), far.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f, 40f, false));
        RoadSpatialGrid.CommitCuts(new[] { new Vector3(-0.924f, 32.327f, -0.383f) }, StairLayout.CutWidth);
        Assert.Null(RoadNetworkGenerator.MeetingRefusal(plans, out _, out var landing, landOnFlight: true));
        Assert.NotNull(landing);
        // Another staircase, 2.5 m south of the cut, under its batter.
        var other = new StairRun { From = new Vector2(-0.924f, -3.4f), To = new Vector2(-0.924f, -2.4f), Width = RoadStairRuns.Width, Kit = StairKit.Stone };
        other.Pieces.Add(new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = StairKit.Stone.Stair, Position = new Vector3(-0.924f, 38f, -2.9f), YawDegrees = 180f });
        StairStructures.Add(other);
        // The road's landing alone, as its checks read it: no points of its own, no other raise.
        var written = new RoadNetworkGenerator.ManualAppend { Landing = landing };
        string? refused = RoadNetworkGenerator.UnderAPlannedStructure(written, new List<RoadCrossing>(), new List<RoadStairSpan>(), out Vector2 at, landing!.Run);
        Assert.True(refused != null && refused.Contains("staircase (-0.9, -3.4)"), refused ?? "not refused");
        Assert.True(Vector2.Distance(at, new Vector2(-0.924f, -2.9f)) <= 2f, $"at {at}");
    }

    /// <summary>
    /// A road's meeting with the network is judged as generation judges it, through the one planner of its location ends
    /// (RoadNetworkGenerator.PlanLocationEnds) and the one list (RoadRefusal). The south-west road onto the staircase's lower
    /// stairs meets the network as planned (a landing fits) and is laid with its landing: its plans changed since that meeting
    /// (LocationEndContext.ChangedSinceMeeting), so the list meets it again as finished. A road that nothing changed after its
    /// meeting is not met again. And with that landed arrival a way into the location it ends at already (LocationWaysIn), the
    /// re-end is judged before the finished meeting: it is searched again to that way's road (Refusal.ReEnd), as generation did.
    /// </summary>
    [Fact]
    public void ARoadChangedByItsLandingIsMetAgainAfterItsReEnd()
    {
        RoadNetworkGenerator.Reset();
        _scope.WithWorld(new FlatWorld { Height = 33f });
        StairRun standing = JunctionFootRun(0f);
        StairStructures.Add(standing);
        StairStructures.MarkStanding(standing);
        var far = new List<Vector2> { new(300f, 300f), new(340f, 300f) };
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(far, far.Select(_ => 40f).ToList(), far.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f, 40f, false));
        Vector2 location = new(4f, 2f);
        RoadNetworkGenerator.PlannedRoad Planned(RoadSpatialGrid.PlannedPath road, float radius)
        {
            var planned = new RoadNetworkGenerator.PlannedRoad
            {
                StartCenter = road.Points[0], EndCenter = location, Path = new List<Vector2>(road.Points), Plans = new List<RoadSpatialGrid.PlannedPath> { road },
                Ends = new RoadNetworkGenerator.LocationEndContext(0f, radius, false, radius > 0f, null, null, 4f, 0f, radius),
            };
            RoadNetworkGenerator.PlanLocationEnds(planned, null, tally: false, landOnFlight: true);
            return planned;
        }
        int MeetingsIn(RoadNetworkGenerator.PlannedRoad road)
        {
            int before = RoadNetworkGenerator.t_meetingsAsked;
            RoadNetworkGenerator.RoadRefusal(road, out _);
            return RoadNetworkGenerator.t_meetingsAsked - before;
        }

        // Landed on the flight: met as planned, changed by its landing, met again as finished.
        RoadNetworkGenerator.PlannedRoad landed = Planned(OntoTheLowerStairs(), 0f);
        Assert.True(landed.Ends.MeetingAsked && landed.Ends.MeetingRefused == null, landed.Ends.MeetingRefused);
        Assert.NotNull(landed.Landing);
        Assert.True(landed.Ends.ChangedSinceMeeting);
        Assert.Equal(1, MeetingsIn(landed));

        // A road off the flight that nothing changes after its meeting: not met again.
        var off = Enumerable.Range(0, 12).Select(k => new Vector2(100f + 2f * k, 100f)).ToList();
        RoadNetworkGenerator.PlannedRoad plain = Planned(new RoadSpatialGrid.PlannedPath(off, off.Select(_ => 40f).ToList(),
            off.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f, 22f, false), 0f);
        Assert.True(plain.Ends.MeetingAsked && plain.Landing == null && !plain.Ends.ChangedSinceMeeting);
        Assert.Equal(0, MeetingsIn(plain));

        // Its landed arrival a way into the location at its end already: re-ended, before the finished meeting.
        using var snap = StaticOverride.Keep(() => RoadNetworkGenerator.CorridorSnap);
        RoadNetworkGenerator.CorridorSnap = (float)PlainRoads.ModDefaults["corridor snap"];
        RoadNetworkGenerator.PlannedRoad reEnded = Planned(OntoTheLowerStairs(), 6f);
        Assert.NotNull(reEnded.Landing);
        RoadSpatialGrid.PlannedPath laid = reEnded.Plans[0];
        LocationWaysIn.Record(location, LocationWaysIn.Of(laid.Points, laid.Heights, location)!);
        try
        {
            RoadNetworkGenerator.Refusal? refused = RoadNetworkGenerator.RoadRefusal(reEnded, out _);
            Assert.Equal(RoadNetworkGenerator.BesideItsApproachRefusal, refused?.Reason);
            Assert.NotNull(refused!.ReEnd);
        }
        finally { LocationWaysIn.Reset(); }
    }

    /// <summary>
    /// Generation writes a landing's raises once, with the road's other raises in their order (CrossingGround.Raises,
    /// RaiseOthers), and the landing does not raise them again (RoadStairRuns.LayLanding with its own road raised already):
    /// the south-west road landing on the staircase whose own road from the north-east it raises, generated, writes one set
    /// of raises naming that road's points. Before, the landing raised them a second time after the road's raises.
    /// </summary>
    [Fact]
    public void GenerationWritesALandingsRaisesOnce()
    {
        RoadNetworkGenerator.Reset();
        var world = new FlatWorld();
        _scope.WithWorld(world);
        SetPathfinder(new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f });
        StairRun laid = JunctionFootRun(40f - 32.48f);
        StairStructures.Add(laid);
        StairStructures.MarkStanding(laid);
        RoadSpatialGrid.PlannedPath own = StaircasesOwnRoad();
        own = new RoadSpatialGrid.PlannedPath(own.Points, own.Heights.Select(h => h + 40f - 32.48f).ToList(), own.DebugInfos, own.Width, own.TotalLength, false);
        RoadSpatialGrid.Commit(own);
        laid.FromRoad = StairRoad.Of(own, atStart: false);
        RoadSpatialGrid.t_raisesWritten = new List<RoadSpatialGrid.Raises>();
        try { Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-60f, -30f), 0f, new Vector2(0f, 0f), 0f, 4f, "to the foot")); }
        finally { }
        List<RoadSpatialGrid.Raises> written = RoadSpatialGrid.t_raisesWritten!;
        RoadSpatialGrid.t_raisesWritten = null;
        Assert.Equal(1, RoadStairRuns.FlightLandings);
        var ownPoints = new HashSet<Vector2>(own.Points);
        int naming = written.Count(r => r.Sources.Any(rp => ownPoints.Contains(rp.p)));
        Assert.True(naming == 1, $"{naming} sets of raises written naming the staircase's own road");
    }

    /// <summary><see cref="FlatWorld"/> with a ditch 7 m deep beside the south-west road's last metres to the foot at the
    /// origin, 1.4 to 3 m off its line on the side away from the flight: under the road's edge, not its centre.</summary>
    private sealed class DitchWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            float along = (2f * wx + wy) / Mathf.Sqrt(5f), off = (2f * wy - wx) / Mathf.Sqrt(5f);
            return along > -14f && along < 2f && off < -1.4f && off > -3f ? 33f : 40f;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override int GetSeed() => 77;
    }

    /// <summary>A landing onto a staircase's lower stairs changes the road after its earthwork was first judged: the south-west
    /// road to the foot at the origin, level at 40 over a ditch 7 m under its edge (within the 8 m cap), lands on the row two
    /// metres up, and its edge then stands 9 m over the ditch. Its fill at its centre is within the cap, as the landing judges
    /// it; as finished, across its width, it is not: the road is refused, nothing of it stored and the staircase not cut back.</summary>
    [Fact]
    public void ARoadPastTheEarthworkCapOnceLandedOnAFlightIsRefused()
    {
        RoadNetworkGenerator.Reset();
        var world = new DitchWorld();
        _scope.WithWorld(world);
        SetPathfinder(new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f });
        StairRun laid = JunctionFootRun(40f - 32.48f);
        StairStructures.Add(laid);
        StairStructures.MarkStanding(laid);
        float cap = RoadNetworkGenerator.EarthworkCap;
        bool built;
        try
        {
            RoadNetworkGenerator.EarthworkCap = RoadNetworkGenerator.DefaultEarthworkCap;
            built = RoadNetworkGenerator.GenerateRoad(new Vector2(-60f, -30f), 0f, new Vector2(0f, 0f), 0f, 4f, "to the foot");
        }
        finally { RoadNetworkGenerator.EarthworkCap = cap; }
        var stored = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(-6f, 0f, -3f), 20f).Where(rp => rp.IsRoad).ToList();
        Assert.False(built, "built: " + string.Join(" ", stored.Select(rp => FormattableString.Invariant($"({rp.p.x:F1},{rp.p.y:F1},{rp.h:F2})"))));
        Assert.Empty(stored);
        Assert.Equal(0, RoadStairRuns.FlightLandings);
        Assert.Equal(laid.Pieces.Count, Assert.Single(StairStructures.Runs).Pieces.Count);
    }

    /// <summary>The staircase's own road at the foot -6951.1,3360.4, laid before the road from the south-west: from the
    /// east to the foot, at the foot's height.</summary>
    private static RoadSpatialGrid.PlannedPath StaircasesOwnRoad()
    {
        var own = new List<Vector2> { new(10.17f, 5.09f), new(6.78f, 3.39f), new(3.39f, 1.70f), new(0f, 0f) };
        return new RoadSpatialGrid.PlannedPath(own, own.Select(_ => 32.48f).ToList(), own.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f, 11.4f, false);
    }

    /// <summary>At -6952,3360 the staircase's own road meets its old foot about two metres under the row the south-west road
    /// lands on. That road is raised to the landing as well, its end level with the row on the landing's ground and eased
    /// down to where it was at the road grade, within the earthwork cap: both roads end level on the landing, and the
    /// network and the staircase save and read back so.</summary>
    [Fact]
    public void TheRoadAtTheOldFootIsRaisedToTheLandingToo()
    {
        RoadNetworkGenerator.Reset();
        _scope.WithWorld(new FlatWorld());
        StairRun standing = JunctionFootRun(0f);
        StairStructures.Add(standing);
        StairStructures.MarkStanding(standing);
        RoadSpatialGrid.Commit(StaircasesOwnRoad());
        standing.FromRoad = StairRoad.Of(StaircasesOwnRoad(), atStart: false);
        var road = OntoTheLowerStairs();
        var plans = new List<RoadSpatialGrid.PlannedPath> { road };
        Assert.Null(RoadNetworkGenerator.MeetingRefusal(plans, out _, out var landing, landOnFlight: true));
        Assert.NotNull(landing);
        Assert.Equal(34.477f, landing!.Height, 3);
        // The old road's end at the old foot goes up the two metres; on along it, eased at the road grade.
        Assert.Contains(landing.Others, o => o.at.p == default(Vector2) && Mathf.Abs(o.to - landing.Height) < 1e-3f);
        foreach (var (at, to) in landing.Others)
        {
            float off = landing.Removed.Min(r => Vector2.Distance(r, at.p));
            Assert.True(to >= at.h && to <= landing.Height + 1e-3f, FormattableString.Invariant($"the old road at {at.p} goes from {at.h:F2} to {to:F2}"));
            Assert.True(Mathf.Abs(to - 40f) <= RoadConstants.TerrainDeltaMax);
            if (off >= RoadStairRuns.OnRoad) Assert.True(landing.Height - to <= RoadGrade.Configured * Vector2.Distance(at.p, default) + 1e-3f, FormattableString.Invariant($"the old road at {at.p} stands {landing.Height - to:F2} under the landing"));
        }

        RoadNetworkGenerator.LandOnTheFlight(plans, landing);
        Assert.True(RoadStairRuns.LayLanding(landing));
        RoadSpatialGrid.Commit(road);
        // Both roads end level on the landing: every stored road point on its ground stands at the row's height.
        void AssertLevel(string when)
        {
            var onLanding = landing.Removed.SelectMany(r => RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(r.x, 0f, r.y), 0.5f))
                .Concat(RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(0f, 0f, 0f), 0.1f)).Where(rp => rp.IsRoad).ToList();
            Assert.NotEmpty(onLanding);
            Assert.All(onLanding, rp => Assert.True(Mathf.Abs(rp.h - landing.Height) < 0.01f,
                FormattableString.Invariant($"{when}: road at {rp.p} stands {rp.h:F2}, the landing {landing.Height:F2}")));
            var old = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(3.39f, 0f, 1.70f), 0.1f).Where(rp => rp.IsRoad).ToList();
            Assert.NotEmpty(old);
            Assert.All(old, rp => Assert.True(rp.h > 32.48f + 0.1f, FormattableString.Invariant($"{when}: the old road at {rp.p} still at {rp.h:F2}")));
        }
        AssertLevel("laid");
        // Saved and read back.
        byte[]? roads = RoadSpatialGrid.SerializeAllRoadPoints();
        byte[] stairs = StairStructures.Serialize();
        RoadSpatialGrid.Clear();
        Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(roads!));
        AssertLevel("read back");
        StairRun read = Assert.Single(StairStructures.Parse(stairs));
        Assert.Equal(landing.Foot, read.From);
        Assert.Equal(landing.Trimmed.Pieces.Select(p => p.Position), read.Pieces.Select(p => p.Position));
    }

    /// <summary>The road's width at each of its points, as a landing measures its earthwork (RoadStairRuns.LandingOnto).</summary>
    private static List<float> Widths(RoadSpatialGrid.PlannedPath road) => Enumerable.Range(0, road.Points.Count).Select(road.WidthAt).ToList();

    /// <summary>
    /// A landing raises only the staircase's own road (decision: the landing fill raises its own road, nothing else). At the
    /// foot -6951.1,3360.4 the staircase's own road from the east is raised to the landing as before; another road laid
    /// beside it, 3 m south of it and off the landing's ground, is left exactly as it stood (before, the raise walked from
    /// point to point within 4 m whatever road each was, and eased that road up too). And where the other road runs onto the
    /// landing's ground itself, at the foot's old height, the landing is refused rather than raise it.
    /// </summary>
    [Fact]
    public void ALandingRaisesOnlyTheStaircasesOwnRoad()
    {
        RoadNetworkGenerator.Reset();
        _scope.WithWorld(new FlatWorld());
        StairRun standing = JunctionFootRun(0f);
        StairStructures.Add(standing);
        StairStructures.MarkStanding(standing);
        RoadSpatialGrid.Commit(StaircasesOwnRoad());
        standing.FromRoad = StairRoad.Of(StaircasesOwnRoad(), atStart: false);
        // Beside the own road, 3 m south of it, from 4 m to 12 m east of the foot: within 4 m of the own road's points.
        var beside = new List<Vector2> { new(12f, 3.0f), new(9f, 1.5f), new(6f, 0f), new(4f, -1.0f) };
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(beside, beside.Select(_ => 32.48f).ToList(), beside.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f, 9f, false));
        var road = OntoTheLowerStairs();
        RoadStairRuns.FlightLanding? landing = RoadStairRuns.LandingOnto(road.Points, road.Heights, Widths(road), (_, _) => 40f, _ => 40f, RoadConstants.TerrainDeltaMax);
        Assert.NotNull(landing);
        Assert.Contains(landing!.Others, o => o.at.p == default(Vector2) && Mathf.Abs(o.to - landing.Height) < 1e-3f);
        var besideAt = new HashSet<Vector2>(beside);
        Assert.DoesNotContain(landing.Others, o => besideAt.Contains(o.at.p));
        Assert.All(landing.Others, o => Assert.Contains(o.at.p, StaircasesOwnRoad().Points));

        // Another road onto the landing's ground at the old foot's height: refused, nothing raised.
        RoadSpatialGrid.Clear();
        RoadSpatialGrid.Commit(StaircasesOwnRoad());
        var onto = new List<Vector2> { new(-2f, 6f), new(-1f, 3f), new(-0.5f, 1.5f) };
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(onto, onto.Select(_ => 32.48f).ToList(), onto.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f, 5f, false));
        Assert.Null(RoadStairRuns.LandingOnto(road.Points, road.Heights, Widths(road), (_, _) => 40f, _ => 40f, RoadConstants.TerrainDeltaMax));
    }

    /// <summary>A landing whose fill would pass the earthwork cap, or that would fill into water or a location, is not laid: the
    /// road is refused as before. The south-west road onto the lower stairs at -6951.1,3360.4: on ground at 50 every row's
    /// landing is past the 8 m cap (with no cap, the row at 34.48 is taken); on ground at 30, under the shallows, and on
    /// ground at 40 with a location over the stairs, or with the staircase's own road at its foot under the landing's ground
    /// and a location over that road where it would be raised, none is laid; with nothing in the way, one is.</summary>
    [Fact]
    public void ALandingPastTheCapOrIntoWaterOrALocationIsRefused()
    {
        RoadNetworkGenerator.Reset();
        _scope.WithWorld(new FlatWorld());
        StairRun standing = JunctionFootRun(0f);
        StairStructures.Add(standing);
        StairStructures.MarkStanding(standing);
        var road = OntoTheLowerStairs();
        RoadStairRuns.FlightLanding? Landing(float ground, float cap) => RoadStairRuns.LandingOnto(road.Points, road.Heights, Widths(road), (_, _) => ground, _ => ground, cap);
        Assert.NotNull(Landing(40f, RoadConstants.TerrainDeltaMax));
        Assert.Null(Landing(50f, RoadConstants.TerrainDeltaMax));
        Assert.Equal(34.477f, Landing(50f, float.PositiveInfinity)!.Height, 3);
        Assert.Null(Landing(30f, float.PositiveInfinity));
        try
        {
            RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(-3f, -1.5f), 3f) });
            Assert.Null(Landing(40f, float.PositiveInfinity));
            Assert.Equal(RoadNetworkGenerator.OnAFlightRefusal, RoadNetworkGenerator.MeetingRefusal(new() { road }, out _, out var none, landOnFlight: true));
            Assert.Null(none);
        }
        finally { RoadLocationProtection.Set(Array.Empty<RoadLocationProtection.Footprint>()); }
        // A road on the landing's ground under it that is not the staircase's own (none is recorded at its foot) is never
        // raised: the landing is refused. The staircase's own road there (StairRun.FromRoad) is.
        RoadSpatialGrid.Commit(StaircasesOwnRoad());
        Assert.Null(Landing(40f, RoadConstants.TerrainDeltaMax));
        standing.FromRoad = StairRoad.Of(StaircasesOwnRoad(), atStart: false);
        Assert.NotNull(Landing(40f, RoadConstants.TerrainDeltaMax));
        // Nor where the staircase's own road comes to its foot at the foot's height and cannot be raised to the landing: a
        // location stands over it where it would be.
        try
        {
            RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(4f, 2f), 1.5f) });
            Assert.Null(Landing(40f, RoadConstants.TerrainDeltaMax));
        }
        finally { RoadLocationProtection.Set(Array.Empty<RoadLocationProtection.Footprint>()); }
        // Nothing laid: the staircase stands as it was.
        Assert.Same(standing, Assert.Single(StairStructures.Runs));
    }

    /// <summary>A boss's entrance is reached only by a road onto its own landing (at the Queen's entrance
    /// 7552,3392: a passing road counted as reaching it); any other location still by a road passing within reach.</summary>
    [Fact]
    public void ABossEntranceIsReachedOnlyAtItsOwnLanding()
    {
        Assert.True(RoadNetworkGenerator.OwnLandingOnly("Mistlands_DvergrBossEntrance1"));
        Assert.False(RoadNetworkGenerator.OwnLandingOnly("Crypt4"));
    }

    /// <summary>A narrow valley along z: faces of 1.2 down to a bottom 5 m wide at x = 0, 24 m under the flats either side.</summary>
    private sealed class SharpValleyWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            return 64f - Mathf.Clamp(24f - Mathf.Max(0f, Mathf.Abs(wx) - 2.5f) * 1.2f, 0f, 24f);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    /// <summary>Through a narrow valley the two staircases meet at their feet on a shared floor at the bottom, with no road
    /// between them (the same rule applies to valleys); laid as before, the
    /// second set off from the first's foot point. Where the second, kept within the walkway cap, comes back close enough
    /// higher up, the valley is spanned there instead, the second setting off from the first's cut-back foot.</summary>
    [Fact]
    public void TwoStaircasesThroughASharpValleyMeetOnASharedFloor()
    {
        string Lay(out int shared, out int spanned)
        {
            RoadNetworkGenerator.Reset();
            var world = new SharpValleyWorld();
            _scope.WithWorld(world);
            SetPathfinder(new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
            bool built = RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0f), 0f, new Vector2(80f, 0f), 0f, 4f, "valley");
            shared = RoadStairRuns.ValleysMet;
            spanned = RoadStairRuns.ValleysSpanned;
            var low = new List<Vector2>();
            for (float x = -12f; x <= 12f; x += 1f) for (float z = -30f; z <= 30f; z += 1f)
                if (world.GetHeight(x, z) < 50f && RoadSpatialGrid.TryGetRoadWithin(new Vector2(x, z), 0.5f, out Vector2 at)) low.Add(at);
            Assert.True(built, "not built");
            return $"{StairStructures.Runs.Count} staircase(s), road in the valley at {string.Join(" ", low.Distinct().Take(6))}";
        }
        string laid = Lay(out int met, out int spans);
        Assert.True(met >= 1, "not met on a shared floor: " + laid);
        // Snapped: the shared floor straight on from the first's foot, its heading the first flight's turned round (the two runs
        // must line up where they connect in the valley).
        StairRun downRun = StairStructures.Runs[0], upRun = StairStructures.Runs[1];
        StructurePiece footStair = downRun.Pieces.Where(p => p.Kind == StructurePieceKind.StairStep).OrderBy(p => p.Position.y).First();
        var footAt = new Vector2(footStair.Position.x, footStair.Position.z);
        if (spans >= 1)
        {
            // Spanned: the second's first piece on the first's foot, edge to edge (the span's own joins: ValleySpanRoadTests).
            Assert.InRange(upRun.Pieces.Where(p => p.Kind != StructurePieceKind.StairSupport)
                .Min(p => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), footAt)), 0f, StairLayout.PieceRun + 0.3f);
            Assert.EndsWith("at ", laid);
            return;
        }
        StructurePiece floor = upRun.Pieces.Where(p => p.Kind == StructurePieceKind.StairFloor)
            .OrderBy(p => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), footAt)).First();
        var at = new Vector2(floor.Position.x, floor.Position.z);
        // The floor one piece on from the foot stair (edge to edge), and nothing of
        // the first standing in it (the step sunk under its foot taken away).
        Assert.InRange(Vector2.Distance(at, footAt), StairLayout.PieceRun - 0.3f, StairLayout.PieceRun + 0.3f);
        Assert.DoesNotContain(downRun.Pieces, p => p.Kind != StructurePieceKind.StairSupport && Vector2.Distance(new Vector2(p.Position.x, p.Position.z), at) < 1f);
        float turned = Mathf.Abs(StairLayout.Wrap(floor.YawDegrees - footStair.YawDegrees + 180f) - 180f) % 180f;
        Assert.True(turned < 0.5f, $"the shared floor turned {turned} off the foot's line (foot yaw {footStair.YawDegrees} at {footStair.Position}, floor yaw {floor.YawDegrees} at {floor.Position}); runs {string.Join(" | ", StairStructures.Runs.Select(r => r.From + "->" + r.To + ": " + string.Join(" ", r.Pieces.Where(p => p.Kind != StructurePieceKind.StairSupport && p.Position.y < 48.5f).Select(p => $"{p.Kind.ToString().Substring(5, 2)}({p.Position.x:F1},{p.Position.y:F1},{p.Position.z:F1})y{p.YawDegrees}"))))}");
        Assert.Contains("road in the valley at ", laid);
        Assert.EndsWith("at ", laid);
    }

    /// <summary>The Queen's entrance (Mistlands_DvergrBossEntrance1 at 7552,3392 turned 315): its landing is
    /// where its own stairs meet the ground in front of its door, about 7538,3409, not its levelled flat's edge against the
    /// entrance building's west wall, where the road ended (the aims along the flat's joined edges); it is the only landing
    /// aimed at. Another location has no door landing.</summary>
    [Fact]
    public void ABossEntrancesLandingIsAtTheFootOfItsOwnStairs()
    {
        var flat = new LevelOp(0f, 6.5f, 1f, 15f, false, 24f, 3f, 1);
        var centre = new Vector2(7552f, 3392f);
        PlacedLocationShape queen = PlacedLocationShape.Of(centre, 315f, flat, 32f, null, (x, z) => 46f, prefab: "Mistlands_DvergrBossEntrance1");
        Assert.True(Vector2.Distance(queen.Landing, new Vector2(7538f, 3409f)) < 1f, $"landing at {queen.Landing}");
        Assert.Single(queen.Aims((x, z) => 46f, new Vector2(7525f, 3415f), 1f, 4));
        PlacedLocationShape other = PlacedLocationShape.Of(centre, 315f, flat, 32f, null, (x, z) => 46f, prefab: "MountainCave02");
        Assert.Null(other.Door);
    }

    /// <summary>A Queen approach: from the road's end at 41.5 over a dip to 39.5 and up to the entrance at 46,
    /// laid as stairs down, a floor and stairs up; it should be a straight run of floors over the dip. A valley no
    /// deeper than the walkway cap under both ends is no turn: the approach crosses it level, on columns.</summary>
    [Fact]
    public void AnApproachCrossesADipWithinTheWalkwayCapLevel()
    {
        float Ground(float x, float z) => x < 4f ? 41.5f - x * 0.4f : x < 8f ? 39.9f : Mathf.Min(46f, 39.9f + (x - 8f) * 0.8f);
        Assert.Null(RoadStairRuns.Turn(new Vector2(0f, 0f), new Vector2(20f, 0f), Ground));
        float Within(float x, float z) => x < 4f ? 41.5f - x * 1.2f : x < 8f ? 36.5f : Mathf.Min(46f, 36.5f + (x - 8f) * 1.2f);
        Assert.Null(RoadStairRuns.Turn(new Vector2(0f, 0f), new Vector2(20f, 0f), Within));
        float Deep(float x, float z) => x < 4f ? 41.5f - x * 1.75f : x < 8f ? 34.5f : Mathf.Min(46f, 34.5f + (x - 8f) * 1.2f);
        Assert.NotNull(RoadStairRuns.Turn(new Vector2(0f, 0f), new Vector2(20f, 0f), Deep));
    }

    /// <summary>A sharp ridge (x = 0, 24 m over the flats, faces of 1) whose west side is rock (the stone kit) and whose east side
    /// is the Mistlands (black marble), the east face coming down in two steps with a shallow, long run between them.</summary>
    private sealed class MixedKitRidgeWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            if (wx <= 0f) return 40f + Mathf.Clamp(24f + wx, 0f, 24f);
            if (wx <= 8f) return 64f - wx;
            if (wx <= 16f) return 56f - 0.35f * (wx - 8f);
            return 53.2f - Mathf.Min(wx - 16f, 21.2f);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            wx < 0f ? Heightmap.Biome.Mountain : Heightmap.Biome.Mistlands;
        public override int GetSeed() => 77;
    }

    /// <summary>Two stretches that merge over a ridge: the first staircase (stone) is edited with its own kit. The east face's two
    /// stretches are laid as one in the kit their line guesses (black marble), and the crest's 2x2 carried on from the first's
    /// top stays stone, the first's own, not the merged kit's (RoadStairRuns.DownFromCrest).</summary>
    [Fact]
    public void ACrestFloorOverAMergedRidgeTakesTheFirstStaircasesOwnKit()
    {
        var world = new MixedKitRidgeWorld();
        _scope.WithWorld(world);
        var path = new List<Vector2>();
        for (float x = -48f; x <= 48.01f; x += 8f) path.Add(new Vector2(x, 0f));
        var steps = new List<(Vector2 from, Vector2 to)>();
        for (int i = 1; i < path.Count; i++)
            if (Mathf.Abs(world.GetHeight(path[i].x, 0f) - world.GetHeight(path[i - 1].x, 0f)) / 8f > 0.5f) steps.Add((path[i - 1], path[i]));
        List<RoadStairSpan>? spans = RoadStairRuns.Lay(path, steps, new List<RoadCrossing>(), world);
        string laid = spans == null ? "not laid" : string.Join(" | ", spans.Select(s => FormattableString.Invariant(
            $"{s.FromIndex}..{s.ToIndex} {s.Kit.Name} {s.FromHeight:F2}->{s.ToHeight:F2}")));
        Assert.True(spans != null && spans.Count == 2, laid);
        // The east face's two stretches merged into one staircase, in the kit their line guesses; the first stays stone.
        Assert.Equal(StairKit.Stone, spans![0].Kit);
        Assert.Equal(StairKit.BlackMarble, spans[1].Kit);
        Assert.True(spans[1].ToIndex - spans[1].FromIndex > 3, laid);
        var floors = spans[0].Plan.Pieces.Where(p => p.Kind == StairPieceKind.Landing).ToList();
        Assert.Contains(floors, p => Mathf.Abs(p.Position.y + StairKit.Stone.FloorTop - 64f) < 0.05f);
        Assert.All(floors, p => Assert.Equal(StairKit.Stone.Floor, p.Prefab));
    }
}
