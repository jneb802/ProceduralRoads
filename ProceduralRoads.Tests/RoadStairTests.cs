using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;
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
        public float Rise = 24f;
        public Heightmap.Biome Biome = Heightmap.Biome.Mountain;

        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            return 40f + Mathf.Clamp(wx * Grade, 0f, Rise);
        }

        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Biome;
        public override int GetSeed() => 77;
    }

    /// <summary>A ridge (or a gully) along z: a 32 m flat top (or floor), faces
    /// of 1.2 rising 24 m either side, flats beyond, sea past 200 m. A crest
    /// narrower than a pathfinding cell would be stepped over as level ground.</summary>
    private sealed class RidgeWorld : WorldGenerator
    {
        public bool Gully;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            float bump = Mathf.Clamp(24f - (Mathf.Abs(wx) - 16f) * 1.2f, 0f, 24f);
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

    /// <summary>A staircase that leaves its road square to it sets off from the road's edge,
    /// not its middle: no step stands on the road's width (a bottom step on the road would cover half the road, so start
    /// closer to the road edge).</summary>
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
        // The first walked piece is the sunk foot, under the road on purpose.
        var walked = span.Plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair || p.Kind == StairPieceKind.Landing).Skip(1).ToList();
        Assert.NotEmpty(walked);
        foreach (StairPiece p in walked)
        {
            // Distance from the arriving road's centreline (x = 0, z <= 0), less the piece's own half width.
            float d = p.Position.z > 0f ? new Vector2(p.Position.x, p.Position.z).magnitude : Mathf.Abs(p.Position.x);
            Assert.True(d - 1f >= RoadNetworkGenerator.RoadWidth / 2f - 0.05f, $"{p.Kind} at {p.Position} stands on the road ({d:F2} m from its centreline)");
        }
        Assert.Equal(1, RoadStairRuns.FeetBeside);
    }

    /// <summary>A staircase that carries on roughly the way its road goes (here 27 degrees off it)
    /// sets off from the middle of the road, not beside it (a stair going the same direction as the road stays
    /// centred).</summary>
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
        Assert.Equal(0, RoadStairRuns.FeetBeside);
        Assert.Equal(0, RoadStairRuns.FeetBesideRefused);
        // Its foot on the road's end, not a road's width to one side of it.
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
        Assert.True(RoadStairRuns.FeetBeside + RoadStairRuns.FeetBesideRefused == 0,
            $"set beside: start {only.Plan.StartHeading}, settled {RoadStairRuns.SettledHeading(only.Plan, new Vector2(0f, 0f))}, travel {StairLayout.Bearing(RoadStairRuns.Back(path, 6, -1, RoadStairRuns.TravelReach), new Vector2(0f, 0f))}, pieces "
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
    /// and joined into one the road was refused.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARidgeOrAGullyIsTwoStaircases(bool gully)
    {
        var world = new RidgeWorld { Gully = gully };
        _scope.WithWorld(world);
        SetPathfinder(new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });

        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0f), 0f, new Vector2(80f, 0f), 0f, 4f, gully ? "gully" : "ridge"));

        Assert.Equal(2, StairStructures.Runs.Count);
        // One climbs the west face, one the east; close over the top, the second is
        // laid to meet the first, with floors between.
        Assert.Contains(StairStructures.Runs, r => r.Pieces.Any(p => p.Position.x < -20f) && r.Pieces.All(p => p.Position.x < 1f));
        Assert.Contains(StairStructures.Runs, r => r.Pieces.Any(p => p.Position.x > 20f));
        // Their tops meet, not one inside the other.
        var walked0 = StairStructures.Runs[0].Pieces.Where(p => p.Kind != StructurePieceKind.StairSupport).ToList();
        var walked1 = StairStructures.Runs[1].Pieces.Where(p => p.Kind != StructurePieceKind.StairSupport).ToList();
        foreach (var p in walked0)
            Assert.DoesNotContain(walked1, q => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), new Vector2(q.Position.x, q.Position.z)) < RoadStairRuns.StairOverlap
                && Mathf.Abs(p.Position.y - q.Position.y) < 2f);
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
    /// the places' edges); a step shortened that way still reads as stairs.</summary>
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
    /// the staircase still sets off within 90 degrees of the road's travel and turns on the stairs. Free, or relaid beside
    /// the road on its own heading, it set off 157.5 degrees off it, back alongside the road (the foot at
    /// Hildir_cave 1664,768; offline its staircase set off 158 degrees off its road).</summary>
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
        string Laid(out float off)
        {
            RoadNetworkGenerator.Reset();
            List<RoadStairSpan>? spans = RoadStairRuns.Lay(path, steps, new List<RoadCrossing>(), world);
            Assert.NotNull(spans);
            StairPlan plan = Assert.Single(spans!).Plan;
            Assert.True(plan.Reached, plan.Reason);
            off = Math.Abs(StairLayout.Wrap(plan.StartHeading - 90f + 180f) - 180f);
            return $"set off on {plan.StartHeading}: " + string.Join(" ", plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair).Take(4)
                .Select(p => $"({p.Position.x:F1},{p.Position.z:F1},{p.Position.y:F1})"));
        }
        string laid = Laid(out float within);
        Assert.True(within <= 90.01f, laid);
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
        Assert.Contains(first.Pieces, p => p.Kind == StructurePieceKind.StairFloor && Mathf.Abs(p.Position.y + StairKit.Stone.FloorTop - crest) < 0.05f);
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
        Assert.True(!builtAfter || RoadStairRuns.CrestStubs == 0 || StairStructures.Runs.Count == 1, "a stub kept: " + after);
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

    /// <summary>A chain over ridge, valley and ridge: each meet snapped, no road between the staircases (a ridge transitions to a valley and maybe back again).</summary>
    [Fact]
    public void ARidgeValleyRidgeChainMeetsAtEachTurn()
    {
        bool Lay(out string laid)
        {
            RoadNetworkGenerator.Reset();
            var world = new RidgeValleyRidgeWorld();
            _scope.WithWorld(world);
            SetPathfinder(new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
            bool built = RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0f), 0f, new Vector2(80f, 0f), 0f, 4f, "chain");
            var road = new List<Vector2>();
            for (float x = -36f; x <= 36f; x += 1f) for (float z = -60f; z <= 60f; z += 1f)
                if (RoadSpatialGrid.TryGetRoadWithin(new Vector2(x, z), 0.5f, out Vector2 at)) road.Add(at);
            int joined = RoadStairRuns.RidgesSnapped + RoadStairRuns.RidgesBridged + RoadStairRuns.CrestStubsJoined;
            laid = $"built {built}, {StairStructures.Runs.Count} staircase(s) {string.Join(" | ", StairStructures.Runs.Select(r => r.From + "->" + r.To))}, ridges snapped {RoadStairRuns.RidgesSnapped}, bridged {RoadStairRuns.RidgesBridged}, stubs joined {RoadStairRuns.CrestStubsJoined}, refused {RoadStairRuns.CrestStubs}, valleys {RoadStairRuns.ValleysMet}, road off the flats at {string.Join(" ", road.Distinct().Take(8))}";
            // Four staircases, up, down, up, down, both crests joined, and no road anywhere between the outer feet (x = -40 and
            // 40): the two in the valley meet on a shared floor.
            return built && StairStructures.Runs.Count == 4 && joined >= 2 && RoadStairRuns.ValleysMet >= 1 && road.Count == 0;
        }
        Assert.True(Lay(out string chain), chain);
    }

    /// <summary>A flat world: a road built north-south, then one east-west across it. The second would cross the first at
    /// grade and is not built (roads that cross at grade are disallowed even if that rejects a POI); one that
    /// ends on the first (a T) is.</summary>
    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f ? 0f : 40f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override int GetSeed() => 77;
    }

    /// <summary>A foot straight on with its road stays in the road's middle only where the road ends there; where another road
    /// goes on past it (the ridge at -6961,3369, the foot at -6951.1,3360.4 on a junction, road all round it:
    /// the stairs would get in the way of the straight-through road versus branching off to the side), it is a spur, set beside.</summary>
    [Fact]
    public void AFootOnARoadGoingOnPastItIsASpur()
    {
        bool GoesOn(bool trunk)
        {
            RoadNetworkGenerator.Reset();
            var world = new FlatWorld();
            _scope.WithWorld(world);
            SetPathfinder(new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f });
            if (trunk) Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, -80f), 0f, new Vector2(0f, 80f), 0f, 4f, "trunk"));
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0.5f), 0f, new Vector2(-1f, 0.5f), 0f, 4f, "arriving"));
            var path = new List<Vector2>();
            for (float x = 0f; x >= -60f; x -= 4f) path.Add(new Vector2(x, 0.5f));
            path.Reverse();
            int foot = path.Count - 1;
            return RoadStairRuns.RoadGoesOnPast(path[foot], path, foot, -1, 90f, out _);
        }
        Assert.True(GoesOn(trunk: true), "the trunk going on past the foot was not seen");
        Assert.False(GoesOn(trunk: false), "the arriving road's own end was taken for another road");
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
    /// second set off from the first's foot point.</summary>
    [Fact]
    public void TwoStaircasesThroughASharpValleyMeetOnASharedFloor()
    {
        string Lay(out int shared)
        {
            RoadNetworkGenerator.Reset();
            var world = new SharpValleyWorld();
            _scope.WithWorld(world);
            SetPathfinder(new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
            bool built = RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 0f), 0f, new Vector2(80f, 0f), 0f, 4f, "valley");
            shared = RoadStairRuns.ValleysMet;
            var low = new List<Vector2>();
            for (float x = -12f; x <= 12f; x += 1f) for (float z = -30f; z <= 30f; z += 1f)
                if (world.GetHeight(x, z) < 50f && RoadSpatialGrid.TryGetRoadWithin(new Vector2(x, z), 0.5f, out Vector2 at)) low.Add(at);
            Assert.True(built, "not built");
            return $"{StairStructures.Runs.Count} staircase(s), road in the valley at {string.Join(" ", low.Distinct().Take(6))}";
        }
        string laid = Lay(out int met);
        Assert.True(met >= 1, "not met on a shared floor: " + laid);
        // Snapped: the shared floor straight on from the first's foot, its heading the first flight's turned round (the two runs
        // must line up where they connect in the valley).
        StairRun downRun = StairStructures.Runs[0], upRun = StairStructures.Runs[1];
        StructurePiece footStair = downRun.Pieces.Where(p => p.Kind == StructurePieceKind.StairStep).OrderBy(p => p.Position.y).First();
        var footAt = new Vector2(footStair.Position.x, footStair.Position.z);
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
    /// aimed at. Another place has no door landing.</summary>
    [Fact]
    public void ABossEntrancesLandingIsAtTheFootOfItsOwnStairs()
    {
        var flat = new LevelOp(0f, 6.5f, 1f, 15f, false, 24f, 3f);
        var centre = new Vector2(7552f, 3392f);
        PlacedSiteShape queen = PlacedSiteShape.Of(centre, 315f, flat, 32f, null, (x, z) => 46f, prefab: "Mistlands_DvergrBossEntrance1");
        Assert.True(Vector2.Distance(queen.Landing, new Vector2(7538f, 3409f)) < 1f, $"landing at {queen.Landing}");
        Assert.Single(queen.Aims((x, z) => 46f, new Vector2(7525f, 3415f), 1f, 4));
        PlacedSiteShape other = PlacedSiteShape.Of(centre, 315f, flat, 32f, null, (x, z) => 46f, prefab: "Crypt4");
        Assert.Null(other.Door);
    }

    /// <summary>A Queen approach: from the road's end at 41.5 over a dip to 39.5 and up to the entrance at 46,
    /// laid as stairs down, a floor and stairs up; it should be a straight run of floors over the dip. A valley no
    /// deeper than RoadStairRuns.ShallowDip under both ends is no turn: the approach crosses it level.</summary>
    [Fact]
    public void AnApproachCrossesAShallowDipLevel()
    {
        float Ground(float x, float z) => x < 4f ? 41.5f - x * 0.4f : x < 8f ? 39.9f : Mathf.Min(46f, 39.9f + (x - 8f) * 0.8f);
        Assert.Null(RoadStairRuns.Turn(new Vector2(0f, 0f), new Vector2(20f, 0f), Ground));
        float Deep(float x, float z) => x < 4f ? 41.5f - x * 1.2f : x < 8f ? 36.5f : Mathf.Min(46f, 36.5f + (x - 8f) * 1.2f);
        Assert.NotNull(RoadStairRuns.Turn(new Vector2(0f, 0f), new Vector2(20f, 0f), Deep));
    }
}
