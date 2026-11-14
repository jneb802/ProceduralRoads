using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Xunit.Abstractions;
using Valheim.Testing;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// Stairs on steep faces: turns priced by how steep the ground is where they are made (aim a turn at a tree well); feet,
/// tops and crests on faces; and the road search offers stairs over faces steeper than 1:1, as the kits hold flights out
/// from them (StairSupport, its own tests). Each rule has its own test.
/// </summary>
public class CantileverTests
{
    private readonly ITestOutputHelper _out;
    public CantileverTests(ITestOutputHelper output) => _out = output;

    private static Vector3 V(float x, float y, float z) => new(x, y, z);

    private static void Near(Vector3 expected, Vector3 actual, string what)
    {
        Assert.True(Vector3.Distance(expected, actual) < 0.01f, $"{what}: expected {expected}, was {actual}");
    }

    /// <summary>
    /// A planar face of 6:1 across z (the reference face is about 6:1), rising along x at a flight's
    /// 1:2 so a flight along it keeps its location on the face, and a shelf at its top (x past 20)
    /// for the staircase to finish on. Without cantilevers every piece stood on a column down the
    /// face (up to 12 m of pillars); held out, no column hangs into the drop.
    /// </summary>
    private static float Face(float x, float z) => x < 20f ? 0.5f * x - 6f * z : Mathf.Max(4f, 10f - 6f * z);

    private StairPlan LayAlongTheFace() =>
        StairLayout.Build(new Vector2(0f, 2f), new Vector2(21f, 2f), Face, footHeight: -6f, arriving: 90f);

    /// <summary>The tree-well rule's price: a switchback on a bench costs what it always did, on steep
    /// ground more, the more so the steeper, up to a cap.</summary>
    [Fact]
    public void SwitchbacksCostMoreOnSteepGround()
    {
        Assert.Equal(0f, StairLayout.SwitchbackSteepPrice(0.3f));
        Assert.Equal(0f, StairLayout.SwitchbackSteepPrice(StairLayout.BenchGrade));
        Assert.True(StairLayout.SwitchbackSteepPrice(2f) > StairLayout.SwitchbackSteepPrice(1.5f));
        Assert.Equal(StairLayout.SwitchbackSteepPrice(StairLayout.SteepCap), StairLayout.SwitchbackSteepPrice(10f), 3);
    }

    /// <summary>
    /// A slope of 0.6 straight up the climb, steeper than a flight, the staircase kept to a strip 9 m wide (as a road's
    /// corridor keeps it). Past the strip's east side, 1.1 m beyond it, the ground falls away sheer, under no piece. A switchback
    /// there is priced by the ground round it (<see cref="StairLayout.SwitchbackSteepPrice"/>: a switchback on a sheer cliff
    /// would be hard, so aim for less steep terrain for those).
    /// </summary>
    private static float SlopeWithADropEast(float x, float z) => 0.6f * z - (x > 5.6f ? 20f * (x - 5.6f) : 0f);

    /// <summary>A scarp of 2.5:1 rising 24 m across the whole map: no road climbs it, and no step of a
    /// staircase on columns (1:1 at most, knight's moves included). Held out from the face (StairSupport), stone
    /// stairs climb it on the diagonal (1.8:1 between cells), priced up with the grade, in every biome: the Meadows build
    /// stone now too.</summary>
    private sealed class SheerScarp : WorldGenerator
    {
        public Heightmap.Biome Biome = Heightmap.Biome.Mountain;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            return 40f + Mathf.Clamp(wx * 2.5f, 0f, 24f);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Biome;
        public override int GetSeed() => 77;
    }

    [Fact]
    public void TheRoadSearchOffersStairsUpFacesSteeperThan1To1WhereTheyCanBeHeldOut()
    {
        var world = new SheerScarp();
        using var worldScope = new ValheimWorldScope().WithWorld(world);
        RoadNetworkGenerator.Reset();
        try
        {
            var finder = new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f };
            Assert.NotNull(finder.FindPath(new Vector2(-80f, 0f), new Vector2(100f, 0f)));
            Assert.Contains(finder.LastStairSteps, s =>
                Mathf.Abs(world.GetHeight(s.to.x, s.to.y) - world.GetHeight(s.from.x, s.from.y)) / Vector2.Distance(s.from, s.to) > RoadPathfinder.StairMaxGrade);
            // In the Meadows, stone too (no new staircase is wood): the scarp is climbed there as well.
            var stone = new SheerScarp { Biome = Heightmap.Biome.Meadows };
            worldScope.WithWorld(stone);
            RoadNetworkGenerator.Reset();
            var meadows = new RoadPathfinder(stone) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f };
            Assert.NotNull(meadows.FindPath(new Vector2(-80f, 0f), new Vector2(100f, 0f)));
            Assert.Contains(meadows.LastStairSteps, s =>
                Mathf.Abs(stone.GetHeight(s.to.x, s.to.y) - stone.GetHeight(s.from.x, s.from.y)) / Vector2.Distance(s.from, s.to) > RoadPathfinder.StairMaxGrade);
            Assert.False(RoadPathfinder.StairStepTooSteep(1.8f));
        }
        finally
        {
            RoadNetworkGenerator.Reset();
        }
    }

    /// <summary>The price per metre climbed rises from 1:1 to its maximum where flights are held out from the face, and
    /// stays there up to the steepest step offered: a sheer wall costs what a cantilever face does.</summary>
    [Fact]
    public void AStairStepsPriceRisesWithItsGrade()
    {
        Assert.Equal(1f, RoadPathfinder.SteepStairFactor(0.5f));
        Assert.Equal(1f, RoadPathfinder.SteepStairFactor(RoadPathfinder.StairMaxGrade));
        Assert.True(RoadPathfinder.SteepStairFactor(2f) > RoadPathfinder.SteepStairFactor(1.5f));
        Assert.Equal(RoadPathfinder.SteepStairPriceMax, RoadPathfinder.SteepStairFactor(RoadPathfinder.StairHeldFromGrade), 3);
        Assert.Equal(RoadPathfinder.SteepStairPriceMax, RoadPathfinder.SteepStairFactor(RoadPathfinder.StairCantileverMaxGrade), 3);
        Assert.True(RoadPathfinder.StairCantileverMaxGrade >= RoadPathfinder.StairHeldFromGrade,
            "the search would refuse faces the layout holds flights out from");
    }

    /// <summary>The wedge a turn opens is read where its filler lies: a turn left off a flight climbing +z on
    /// ground rising to the west (left) at 1:1 has its wedge on the outside (east, downhill), clear; turned
    /// right, its wedge lies uphill (west) and the ground covers it. Each filler is read over its whole surface, edges
    /// included (StairLayout.Cover), so the ground is as steep as leaves the piece before clear at its corners (at 2:1,
    /// as before, its west corner, the left turn's pivot and its wedge's apex, stood 1.5 m under).</summary>
    [Fact]
    public void ATurnsWedgeIsReadWhereItsFillerLies()
    {
        float West(float x, float z) => -x;
        // The piece before ends at (0, 2), its top at 1 (the ground at its middle line is 0, at its west corner 1).
        Vector2 edge = new(0f, 2f);
        // Turned right 45 degrees: pivot on the right (east) corner, the wedge on the west.
        Vector2 pivotRight = edge + StairLayout.Right(0f) * 1f;
        // Turned left 45: pivot on the west corner, the wedge on the east.
        Vector2 pivotLeft = edge - StairLayout.Right(0f) * 1f;
        // Its fillers as the kit lays them, each read over its whole surface (StairLayout.Cover).
        float right = StairLayout.WedgeBuried(pivotRight, 0f, 1f, 2, 2f, false, StairKit.Stone.Fillers, West);
        float left = StairLayout.WedgeBuried(pivotLeft, 0f, 1f, -2, 2f, false, StairKit.Stone.Fillers, West);
        // Its fillers reach about 0.1 m further west than the piece before's flush corner, 0.12 m into the slope.
        Assert.True(right > 0.1f, $"wedge uphill buried {right:F2}");
        Assert.True(left < 0.01f, $"wedge downhill buried {left:F2}");
    }

    /// <summary>A road arriving heading north (+z) at the foot of a slope that climbs east (a foot at Hildir's cave's
    /// approach met the road 55-90 degrees off its heading): held to set off within 45 degrees of the road, the
    /// layout laid a pad of floors and turned off it (stairs should join like this more often than two starter blocks).
    /// Laid again starting on a stair, it comes up out of the road and turns on the stairs.</summary>
    private static float EastSlope(float x, float z) => x < 3f ? 0f : 0.5f * (x - 3f);

    [Fact]
    public void AStaircaseComesUpOutOfTheRoadOnAStairNotAPad()
    {
        StairPlan Lay() => StairLayout.Build(new Vector2(0f, 0f), new Vector2(24f, 2f), EastSlope, arriving: 0f, sunkFoot: true);
        StairPlan plan = Lay();
        _out.WriteLine($"stair first: starts on floor {plan.StartsOnFloor}, start heading {plan.StartHeading}");
        Assert.True(plan.Reached, plan.Reason);
        Assert.False(plan.StartsOnFloor);
        Assert.InRange(Math.Abs(StairLayout.Wrap(plan.StartHeading + 180f) - 180f), 0f, 3f * StairLayout.HeadingStep + 0.01f);
    }

    /// <summary>A road staircase top at 6468.3,-1522.5: its top edge at 162.3, climbing toward 202.5, the
    /// surface past it rising 1.1-1.5 m over the next 5 m, the road carrying on that way. The top goes a bit out
    /// and does the double stair ridge to land on the surface: stairs out while the ground a floor off the top would stand
    /// on stands more than a step over it (the end rule's short top), then the rule there, a floor or the crest's stair down
    /// onto the surface, and the road starts past it, where a walker steps off it (StairEnds). Without the rule the road
    /// starts at the top, under the lip.</summary>
    [Fact]
    public void ARoadStaircaseTopGoesOutAndCrestsOntoTheSurface()
    {
        Vector2 edge = new(6467.936f, -1523.402f), dir = StairLayout.Forward(202.5f);
        float[] ds = { 0f, 1f, 2f, 3f, 5f, 8f }, hs = { 162.2f, 162.6f, 163.2f, 163.4f, 163.8f, 163.9f };
        float Ground(float x, float z)
        {
            float d = Vector2.Dot(new Vector2(x, z) - edge, dir);
            if (d <= 0f) return hs[0] + 0.5f * d;
            for (int i = 1; i < ds.Length; i++) if (d <= ds[i]) return Mathf.Lerp(hs[i - 1], hs[i], (d - ds[i - 1]) / (ds[i] - ds[i - 1]));
            return hs[hs.Length - 1];
        }
        StairPlan Top() => new() { Kit = StairKit.Stone.Name, Reached = true, End = new Vector3(edge.x, 162.326f, edge.y), EndHeading = 202.5f };
        var path = new List<Vector2> { edge - dir * 10f, edge, edge + dir * 12f };
        StairPlan plan = Top();
        Assert.Equal(StairEnds.Case.Short, StairLayout.TopCase(Top(), Ground, RoadStairRuns.Width));
        Assert.True(RoadStairRuns.TopOntoSurface(plan, path, 2, Ground, out Vector2 past, out float pastHeight));
        Assert.True(plan.Pieces.Count(p => p.Kind == StairPieceKind.Stair) >= 1);
        StairEnds.End end = RoadStairRuns.TopEnd(plan, Ground)!;
        Assert.True(end.Verdict == StairEnds.Verdict.Ok, StairEnds.Line(end));
        Assert.True(Math.Abs(pastHeight - Ground(past.x, past.y)) <= StairLayout.TopFloorBand && pastHeight > 163f, $"road starts at {pastHeight:F2}");
        // Off the crest's stair down the road starts level with where the walker steps off, its low edge (not the ground
        // there, up to a rise over it, StairEnds.MeetsLowEdge).
        StairLayout.TerminalExit exit = StairLayout.Terminal(plan)!;
        if (exit.Descending) Assert.Equal(exit.Edge.y, pastHeight, 3);
        int pieces = plan.Pieces.Count;
        // Called again for the next road that walks it: the same, nothing added.
        Assert.True(RoadStairRuns.TopOntoSurface(plan, path, 2, Ground, out _, out _));
        Assert.Equal(pieces, plan.Pieces.Count);
    }

    /// <summary>A top at the mountain cave entry: top stair (1932.190, 169.451, 697.097) yaw 157.5, its top edge
    /// at 170.45, the plateau past it 0-0.5 m over it, a gap at the top of the stair run. The hand-built fix, one
    /// stone_floor_2x2 at (1931.424, 169.951, 698.945) yaw 247.5, level with the top and out onto the snow, is laid; without
    /// the rule the top is left as it was.</summary>
    [Fact]
    public void ATopNearlyLevelWithTheGroundGetsAFloorOutOntoIt()
    {
        Vector2 edge = new(1931.807f, 698.021f), dir = StairLayout.Forward(337.5f);
        float[] ds = { 0f, 1f, 2f, 3f, 4f, 6f }, hs = { 169.5f, 169.9f, 170.35f, 170.5f, 170.45f, 169.8f };
        float Ground(float x, float z)
        {
            float d = Vector2.Dot(new Vector2(x, z) - edge, dir);
            if (d <= 0f) return hs[0] + d;
            for (int i = 1; i < ds.Length; i++) if (d <= ds[i]) return Mathf.Lerp(hs[i - 1], hs[i], (d - ds[i - 1]) / (ds[i] - ds[i - 1]));
            return hs[hs.Length - 1];
        }
        StairPlan Top() => new() { Kit = StairKit.Stone.Name, Reached = true, End = new Vector3(edge.x, 170.451f, edge.y), EndHeading = 337.5f,
            LastRowKind = StairPieceKind.Stair, LastRowPieces = 0 };
        StairPlan plan = Top();
        Assert.Equal(StairLayout.TopFinish.TopFloor, RoadStairRuns.FinishOverLedge(plan, Ground, 2f, _ => true));
        StairPiece floor = Assert.Single(plan.Pieces, p => p.Prefab == "stone_floor_2x2");
        Assert.True(Vector3.Distance(floor.Position, new Vector3(1931.424f, 169.951f, 698.945f)) < 0.02f, $"floor at {floor.Position}");
        Assert.Equal(247.5f % 90f, floor.YawDegrees % 90f, 1);
    }

    /// <summary>A road arriving heading north at the foot of a slope that climbs south, behind it: set off freely, the
    /// staircase started straight back across the road and on up the slope (at 1948.1,691.7, 180 degrees opposite the
    /// direction of road travel and blocking the actual road to the stair start). Held to the road's
    /// arrival it sets off back at most StairLayout.MostSetOffBack pieces, then faces within 90 degrees of it and turns on
    /// the stairs.</summary>
    [Fact]
    public void AStaircaseFromARoadsEndSetsOffFacingTheRoadsArrival()
    {
        float South(float x, float z) => z < 0f ? -0.5f * z : 0f;
        StairPlan Lay() => StairLayout.Build(new Vector2(0f, 0f), new Vector2(0f, -20f), South, sunkFoot: true, facing: 0f);
        StairPlan plan = Lay();
        Assert.True(plan.Reached, plan.Reason);
        Assert.InRange(plan.SetOffBack, 0, StairLayout.MostSetOffBack);
        Assert.True(BackRun(plan, 0f) <= StairLayout.MostSetOffBack, $"ran back {BackRun(plan, 0f)} pieces");
    }

    /// <summary>Walked pieces from the foot heading more than 90 degrees off <paramref name="facing"/> before the first that
    /// does not.</summary>
    private static int BackRun(StairPlan plan, float facing)
    {
        int run = 0;
        foreach (StairPiece p in plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair || p.Kind == StairPieceKind.Landing).Skip(1))
        {
            float heading = StairLayout.Wrap(p.YawDegrees + 180f);
            if (Math.Abs(StairLayout.Wrap(heading - facing + 180f) - 180f) <= 90.01f) break;
            run++;
        }
        return run;
    }

    /// <summary>A Hildir_cave foot (1664,768): the road arrives heading north into a face, every heading
    /// within 90 degrees of it buried at the foot, the way up a slope behind and to the east. Held strictly to the road's
    /// heading, no piece fits; set off back beside the road for up to three pieces (staircases back along the road at most
    /// about three stair lengths), it turns to face the road's way and climbs.</summary>
    [Fact]
    public void AFootInFrontOfAFaceSetsOffBackAtMostThreePiecesThenTurns()
    {
        float Ground(float x, float z) => z > 0.5f ? 6f + z : Mathf.Max(0f, 0.5f * x);
        StairPlan Lay() => StairLayout.Build(new Vector2(0f, 0f), new Vector2(16f, -6f), Ground, sunkFoot: true, facing: 0f);
        StairPlan plan = Lay();
        Assert.True(plan.Reached, plan.Reason);
        Assert.InRange(plan.SetOffBack, 1, StairLayout.MostSetOffBack);
    }

    /// <summary>At MountainCave02 1920,704: the foot set off 45 degrees off its road (it should line up better with the
    /// road, straight on, moved a bit to the left). Where straight on
    /// fits as well, the first piece sets off along the road's travel.</summary>
    [Fact]
    public void AFootSetsOffAlongItsRoadWhereThatFits()
    {
        // A slope rising 22.5 degrees off the road's travel (north): a flight up it fits straight on as well, a little off
        // the fall line.
        Vector2 up = StairLayout.Forward(22.5f);
        float Ground(float x, float z) => Mathf.Max(0f, 0.5f * Vector2.Dot(new Vector2(x, z), up));
        StairPlan Lay() => StairLayout.Build(new Vector2(0f, 0f), up * 24f, Ground, sunkFoot: true, facing: 0f);
        StairPlan plan = Lay();
        Assert.True(plan.Reached, plan.Reason);
        Assert.True(plan.StartHeading == 0f, $"set off on {plan.StartHeading}");
    }

    /// <summary>A Hildir_cave foot (-2304,1536): the road climbs to its end at (-2305.1, 1573.9) heading south
    /// along x = -2306 ((-2306.3, 1580.5) to (-2305.0, 1576.4)); the flight set off at 45 degrees, back up beside the road,
    /// from the road's end, a floor turned on it. A hand-built first stair at (-2303.734, 1575.360) sits beside the road
    /// (where it starts at a sharper angle it should align more to the side of the road). From the ring at the road's end
    /// (RoadEndRing), the cheapest start on 45 degrees is on the side it turns to, its first stair by the hand-built one; a
    /// start straight on is cheaper still, and from the ring's front.</summary>
    [Fact]
    public void AFootSettingOffSharplyStartsBesideTheRoadsEnd()
    {
        Vector2 end = new(-2305.1f, 1573.9f);
        float travel = StairLayout.Bearing(new Vector2(-2306.3f, 1580.5f), new Vector2(-2305.0f, 1576.4f));
        var ring = new RoadEndRing(end, travel, 255f);
        List<RoadEndRing.Start> starts = ring.Starts(RoadStairRuns.Width);
        RoadEndRing.Start first = starts.First(s => Mathf.Abs(s.Heading - 45f) < 0.01f);
        Assert.True(first.At.x > end.x, $"set off from {first.At}, not on the side the flight turns to");
        Vector2 stair = first.At + StairLayout.Forward(45f) * (StairLayout.PieceRun / 2f);
        Assert.True(Vector2.Distance(stair, new Vector2(-2303.734f, 1575.360f)) < 1.5f, $"first stair at {stair}, far from the hand-built one");
        RoadEndRing.Start best = starts[0];
        Assert.True(Mathf.Abs(RoadEndRing.Turn(best.Heading, travel)) <= StairLayout.HeadingStep / 2f + 0.01f && Mathf.Abs(best.Arc) <= StairLayout.HeadingStep / 2f + 0.01f,
            $"cheapest start {best.Heading} at {best.Arc} round");
    }

    /// <summary>At the top of the 41 m staircase at 5669.6,4239.7: the flight came in facing away from the way
    /// the road leaves its top (it should face the other direction). Held to the
    /// road's departure, the top arrives within 90 degrees of it; free, it came straight up the slope, 135 degrees off.</summary>
    [Fact]
    public void AStaircaseTopArrivesFacingTheWayItsRoadLeaves()
    {
        float North(float x, float z) => Mathf.Max(0f, 0.5f * z);
        StairPlan Lay() => StairLayout.Build(new Vector2(0f, 0f), new Vector2(0f, 24f), North, sunkFoot: true, topFacing: 135f);
        float Off(StairPlan p) => Math.Abs(StairLayout.Wrap(p.EndHeading - 135f + 180f) - 180f);
        StairPlan plan = Lay();
        Assert.True(plan.Reached, plan.Reason);
        Assert.True(Off(plan) <= 90.01f, $"arrived on {plan.EndHeading}");
    }

    /// <summary>A black marble top (463.6,6503.4): its 2x2 stood 0.2-0.7 m over the road past it, high enough that a
    /// player can get stuck on it briefly. A top floor is laid only where the ground at one of its far corners at least is
    /// within a step of it (the end rule, StairEnds.Lands); a road falling away from the top meets the top itself. The falls
    /// are in steps and ledges (StairLayout.StepUp, StairRailing.LedgeDrop), so the rule holds whatever they are.</summary>
    [Fact]
    public void ATopFloorIsNotLaidProudOfTheGroundPastIt()
    {
        StairPlan Top() => new() { Kit = StairKit.BlackMarble.Name, Reached = true, End = new Vector3(0f, 10f, 0f), EndHeading = 0f,
            LastRowKind = StairPieceKind.Stair, LastRowPieces = 0 };
        float Past(float z) => Mathf.Clamp01(z / StairLayout.PieceRun);
        // Past the top the ground falls on the left more than a ledge under the floor's far corner, on its right half a step:
        // the sides disagree and one is within a step: laid, stepped off on that side.
        float Falling(float x, float z) => 10f - Past(z) * (x < 0f ? StairRailing.LedgeDrop + StairLayout.StepUp : StairLayout.StepUp / 2f);
        Assert.True(StairLayout.AddTopFloor(Top(), Falling, 2f).pieces > 0);
        // Falling away at both far corners, two steps under them: none (the rule's stair down).
        float AllRound(float x, float z) => 10f - Past(z) * 2f * StairLayout.StepUp;
        Assert.Equal(0, StairLayout.AddTopFloor(Top(), AllRound, 2f).pieces);
        // Level with it across, within a step: laid.
        float Level(float x, float z) => 10f - Past(z) * StairLayout.StepUp / 2f;
        Assert.True(StairLayout.AddTopFloor(Top(), Level, 2f).pieces > 0);
    }

    /// <summary>A foot at 8972.6,3335.8: the road arriving on 161, the flight turning left to 112.5, its foot set
    /// on the road's right and the flight crossing back over the road's line. From the ring at the road's end (RoadEndRing)
    /// the cheapest start on 112.5 is on the side it turns to, and a flight set off from the other side across the ring
    /// crosses the road.</summary>
    [Fact]
    public void ATurnedFootIsSetOnlyOnTheSideItTurnsTo()
    {
        var end = new Vector2(8972.6f, 3335.8f);
        var ring = new RoadEndRing(end, 161f, 40f);
        RoadEndRing.Start first = ring.Starts(RoadStairRuns.Width).First(s => Mathf.Abs(s.Heading - 112.5f) < 0.01f);
        Assert.True(first.Arc < 0f, $"set off {first.Arc} round the ring");
        Vector2 right = end + StairLayout.Forward(161f + 90f) * ring.Radius;
        var across = new StairPlan { Reached = true };
        for (int i = 0; i < 3; i++)
        {
            Vector2 at = right + StairLayout.Forward(112.5f) * (StairLayout.PieceRun * (i + 0.5f));
            across.Pieces.Add(new StairPiece { Kind = StairPieceKind.Stair, Prefab = "stone_stair", Position = new Vector3(at.x, 40f + i, at.y), YawDegrees = StairLayout.Wrap(112.5f + 180f) });
        }
        Assert.True(ring.Intrudes(across));
    }

    /// <summary>Searched down from the top edge of a staircase over a ridge (-8030,-660: the top of one set of
    /// stairs was too high to walk over, fixed by snapping to the other set: just one stair up and another down),
    /// the second staircase's top stair stands back to back with the first's: its high edge on that edge, at that height,
    /// heading back, and its foot down by the road below.</summary>
    [Fact]
    public void AStaircaseSearchedDownFromAnotherTopStandsBackToBackWithIt()
    {
        float Ridge(float x, float z) => 40f + Mathf.Clamp(24f - Mathf.Abs(x) * 1.2f, 0f, 24f);
        var edge = new Vector2(-0.5f, 0f);
        var top = new StairContinuation(edge, 90f, 63.6f, StairPieceKind.Stair);
        StairPlan plan = StairLayout.BuildDown(top, new Vector2(24f, 0f), Ridge, 2f, StairKit.Stone);
        Assert.True(plan.Reached, plan.Reason);
        Assert.True(Vector3.Distance(plan.End, new Vector3(edge.x, 63.6f, edge.y)) < 0.01f, $"top at {plan.End}");
        Assert.Equal(270f, plan.EndHeading, 1);
        // Its top stair: a rise under the top, its back on the edge, climbing back west.
        StairPiece last = plan.Pieces.Last(p => p.Kind == StairPieceKind.Stair);
        Assert.Equal(63.6f - StairLayout.PieceRise, last.Position.y, 2);
        Assert.True(plan.Foot.HasValue && Mathf.Abs(plan.Foot.Value.y - Ridge(plan.Foot.Value.x, plan.Foot.Value.z)) < 1.5f, $"foot at {plan.Foot}");
        Assert.True(plan.Rise > 20f, $"rose {plan.Rise}");
    }

    /// <summary>A top at 6495.5,-1464.9: the last flight curled round over the road it hands to and lay on its
    /// surface for several pieces, taking over the road itself. At a top, as at a foot, only the joining piece
    /// (within JoiningStep) may stand on the road: a stair 4 m on along the road at its level lies on it.</summary>
    [Fact]
    public void AStaircasesTopTakesNoMoreOfItsRoadThanTheJoiningPiece()
    {
        float Ground(float x, float z) => 0.5f * Mathf.Max(0f, x);
        var path = new List<Vector2> { new(-16f, 0f), new(-8f, 0f), new(0f, 0f), new(8f, 0f), new(16f, 0f), new(24f, 0f), new(32f, 0f) };
        StairPlan Top(float x) => new() { Reached = true, Pieces = { new StairPiece { Kind = StairPieceKind.Stair, Position = new Vector3(x, 7f, 0.5f), YawDegrees = 270f } } };
        Assert.True(RoadStairRuns.OverRoad(Top(20f), path, 2, 4, Ground), "4 m on along the road, at its level");
        Assert.False(RoadStairRuns.OverRoad(Top(17.5f), path, 2, 4, Ground), "the joining piece");
    }

    /// <summary>At -5853,5918: a flight running beside its road a few metres off, both climbing, mostly floors
    /// (better to avoid running parallel, even if it costs POIs). Three walked pieces
    /// in a row within 5 m of a road and 1.5 m of its height, away from the staircase's own ends, refuse the layout.</summary>
    [Fact]
    public void AStaircaseRunningBesideARoadAtItsHeightIsRefused()
    {
        float Ground(float x, float z) => 40f + 0.1f * x;
        var path = new List<Vector2>();
        for (int x = -40; x <= 40; x += 8) path.Add(new Vector2(x, 0f));
        path.Add(new Vector2(40f, 16f));
        StairPlan Beside(float lift)
        {
            var plan = new StairPlan { Reached = true };
            foreach (float x in new[] { -24f, -22f, -20f, -18f })
                plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Landing, Position = new Vector3(x, Ground(x, 0f) - 0.5f + lift, 3f), YawDegrees = 90f });
            return plan;
        }
        // Its stretch is the road's last leg, (40,0) to (40,16): the run at x -24..-18 lies beside the road before it.
        Assert.True(RoadStairRuns.BesideARoad(Beside(0f), path, 10, 11, Ground));
        Assert.False(RoadStairRuns.BesideARoad(Beside(3f), path, 10, 11, Ground), "3 m over the road it is not beside it");
    }

    /// <summary>A foot at -8018,-660 stood against a bank (minor earthworks give it space): a level apron is cut behind it
    /// at its height, the bank 1:1 there; a foot that opens onto
    /// flat ground gets none.</summary>
    [Fact]
    public void AFootAgainstABankGetsAnApronCutBehindIt()
    {
        // The climb rises west; north of z = 0 a 1:1 bank.
        float Corner(float x, float z) => 40f + Mathf.Max(0f, -x) * 0.5f + Mathf.Max(0f, z);
        float Flat(float x, float z) => 40f + Mathf.Max(0f, -x) * 0.5f;
        StairPlan banked = StairLayout.Build(new Vector2(0f, 0f), new Vector2(-16f, 0f), Corner, sunkFoot: true);
        Assert.True(banked.Reached, banked.Reason);
        int before = banked.Cuts.Count;
        Assert.True(StairLayout.CutFootApron(banked, Corner, 2f, RoadGrade.Configured) > 0, "no apron against the bank");
        Vector3 foot = banked.Foot!.Value;
        Assert.All(banked.Cuts.Skip(before), c => Assert.Equal(foot.y - StairLayout.CutBelowSurface, c.y, 3));
        Assert.All(banked.Cuts.Skip(before), c => Assert.True(Vector2.Distance(new Vector2(c.x, c.z), new Vector2(foot.x, foot.z)) <= StairLayout.ApronDepth + 2.5f));
        StairPlan flat = StairLayout.Build(new Vector2(0f, 0f), new Vector2(-16f, 0f), Flat, sunkFoot: true);
        Assert.Equal(0, StairLayout.CutFootApron(flat, Flat, 2f, RoadGrade.Configured));
    }

    /// <summary>No switchback in a staircase's first five stairs from its foot (Hildir_cave 1664,768): up a narrow gully, the first
    /// switchback comes after five stairs; before, three.</summary>
    [Fact]
    public void TheFirstSwitchbackComesFiveStairsUpFromTheFoot()
    {
        int StairsBeforeSwitchback(StairPlan plan)
        {
            int stairs = 0;
            StairPiece? previous = null;
            foreach (StairPiece p in plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair || p.Kind == StairPieceKind.Landing))
            {
                if (previous != null && previous.Kind == StairPieceKind.Landing && p.Kind == StairPieceKind.Landing && previous.YawDegrees == p.YawDegrees
                    && Vector2.Distance(new Vector2(previous.Position.x, previous.Position.z), new Vector2(p.Position.x, p.Position.z)) > 1.5f) return stairs;
                if (p.Kind == StairPieceKind.Stair) stairs++;
                previous = p;
            }
            return int.MaxValue;
        }
        StairPlan Lay() => StairLayout.Build(new Vector2(0f, 0f), new Vector2(0f, 20f), SlopeWithADropEast, allowed: p => Mathf.Abs(p.x) <= 4.5f);
        StairPlan plan = Lay();
        Assert.True(plan.Reached, plan.Reason);
        Assert.True(StairsBeforeSwitchback(plan) >= StairLayout.FirstFlightStairs, $"switched back after {StairsBeforeSwitchback(plan)} stairs");
    }

    /// <summary>A second floor (6465.2,-1522.4): an end cap ridge done with a block. The hand-built
    /// top stair (6465.922, 162.355, -1520.561) yaw 22.5, top edge 163.36, the snow where a walker steps off up to about
    /// half a metre over it: a stone_floor_2x2 at (6465.156, 162.855, -1522.409), level with the top. By the end rule (5 Nov:
    /// a floor only within a step) the top is short there: it climbs on and finishes by the rule there, walked off.</summary>
    [Fact]
    public void ATopHalfAMetreUnderTheSnowClimbsOnByTheRule()
    {
        Vector2 edge = new(6465.539f, -1521.485f), dir = StairLayout.Forward(202.5f);
        float[] ds = { 0f, 1f, 2f, 3f, 6f }, hs = { 162.9f, 163.4f, 163.8f, 164.1f, 164.1f };
        float Ground(float x, float z)
        {
            float d = Vector2.Dot(new Vector2(x, z) - edge, dir);
            if (d <= 0f) return hs[0] + 0.5f * d;
            for (int i = 1; i < ds.Length; i++) if (d <= ds[i]) return Mathf.Lerp(hs[i - 1], hs[i], (d - ds[i - 1]) / (ds[i] - ds[i - 1]));
            return hs[hs.Length - 1];
        }
        var plan = WithTopStair(new StairPlan { Kit = StairKit.Stone.Name, Reached = true, End = new Vector3(edge.x, 163.355f, edge.y), EndHeading = 202.5f,
            LastRowKind = StairPieceKind.Stair, LastRowPieces = 0 });
        Assert.Equal(StairEnds.Case.Short, StairLayout.TopCase(plan, Ground, 2f));
        Assert.NotEqual(StairLayout.TopFinish.None, RoadStairRuns.FinishOverLedge(plan, Ground, 2f, _ => true));
        Assert.Contains(plan.Pieces, p => p.Kind == StairPieceKind.Stair);
        StairEnds.End end = RoadStairRuns.TopEnd(plan, Ground)!;
        Assert.True(end.Verdict == StairEnds.Verdict.Ok, StairEnds.Line(end));
    }

    /// <summary>A top under a lip close enough for a crest by the ground read straight ahead, but with the ground under
    /// the down stair's low end falling away on one side: no crest out over the drop (the upper staircase overshot, at the mountain cave
    /// at -2560.6,1549.9).</summary>
    [Fact]
    public void ACrestIsNotLaidOutPastALipOverADrop()
    {
        static float Ground(float x, float z) => z > 3.2f && x > 0.3f ? 0f : 9.9f + 0.3f * Mathf.Max(z, -2f);
        LedgeFinishAndPieces Finish()
        {
            var plan = new StairPlan { Kit = StairKit.Stone.Name, Reached = true, End = new Vector3(0f, 10f, 0f), EndHeading = 0f,
                LastRowKind = StairPieceKind.Stair, LastRowPieces = 0 };
            return new(RoadStairRuns.FinishOverLedge(plan, Ground, 2f, _ => true), plan.Pieces);
        }
        LedgeFinishAndPieces with = Finish();
        Assert.NotEqual(StairLayout.TopFinish.Crest, with.Finish);
        Assert.DoesNotContain(with.Pieces, p => p.Kind == StairPieceKind.Stair && p.Position.z > 2.5f);
    }

    /// <summary>An overshoot (the mountain cave at -2560.6,1549.9): the flight up 202.5, its last stair's top
    /// edge at 157.26, the ground probed along it (from the stair at -2559.0,1553.6): 157.76 a metre on, 158.22 at a crest's
    /// ridge, 158.4-158.55 past it. The crest's ridge stood 0.04 m over the ground and its down stair was buried whole:
    /// The upper staircase overshot. Stairs on up while the ground a floor off the top would stand on stands more than a
    /// step over it, then the rule there: never a crest buried whole, and walked off (StairEnds).</summary>
    [Fact]
    public void ATopWhoseCrestWouldBeBuriedClimbsOnUntilTheRuleLands()
    {
        Vector2 a = new(-2559.0f, 1553.6f), dir = StairLayout.Forward(202.5f), edge = a + dir * 3f;
        float[] hs = { 154.06f, 154.20f, 154.30f, 154.80f, 155.37f, 156.22f, 156.90f, 157.31f, 157.76f, 157.98f, 158.22f, 158.31f,
            158.42f, 158.47f, 158.52f, 158.55f, 158.56f };
        float Ground(float x, float z)
        {
            float along = Mathf.Clamp(Vector2.Dot(new Vector2(x, z) - a, dir) / 0.5f, 0f, hs.Length - 1.001f);
            int i = (int)along;
            return Mathf.Lerp(hs[i], hs[i + 1], along - i);
        }
        var plan = WithTopStair(new StairPlan { Kit = StairKit.Stone.Name, Reached = true, End = new Vector3(edge.x, 157.262f, edge.y), EndHeading = 202.5f,
            LastRowKind = StairPieceKind.Stair, LastRowPieces = 0 });
        Assert.Equal(StairEnds.Case.Short, StairLayout.TopCase(plan, Ground, 2f));
        Assert.NotEqual(StairLayout.TopFinish.None, RoadStairRuns.FinishOverLedge(plan, Ground, 2f, _ => true));
        Assert.All(plan.Pieces.Skip(2).Where(p => p.Kind == StairPieceKind.Stair && p.YawDegrees == 22.5f), p => Assert.True(p.Position.y >= 157.262f - 0.01f));
        StairEnds.End end = RoadStairRuns.TopEnd(plan, Ground)!;
        Assert.True(end.Verdict == StairEnds.Verdict.Ok, StairEnds.Line(end));
        // No stair laid whole under the ground (the crest's down stair buried whole, as there).
        foreach (StairPiece p in plan.Pieces.Skip(2).Where(p => p.Kind == StairPieceKind.Stair))
            Assert.True(StairLayout.WalkingSurface(p).Any(q => q.y > Ground(q.x, q.z)), $"stair at {p.Position} buried whole");
    }

    /// <summary>The two flights of a valley (4392-4398,6438-6444) as they were laid down to a shared floor at
    /// 32.5: the first (reversed) down into it from the west, the second up out of it to the east.</summary>
    internal static (StairPlan below, StairPlan up) SharedFloorValley()
    {
        StairKit kit = StairKit.BlackMarble;
        static StairPiece Piece(StairPieceKind kind, string prefab, float x, float y, float z, float yaw) =>
            new() { Kind = kind, Prefab = prefab, Position = new Vector3(x, y, z), YawDegrees = yaw };
        StairPiece Stair(float x, float y, float z, float yaw) => Piece(StairPieceKind.Stair, kit.Stair, x, y, z, yaw);
        StairPiece Filler(float x, float y, float z, float yaw) => Piece(StairPieceKind.Filler, "blackmarble_2x1x1", x, y, z, yaw);
        var below = new StairPlan { Kit = kit.Name, Reached = true, Reversed = true };
        below.Pieces.AddRange(new[] {
            Stair(4392.601f, 32.5f, 6440.604f, 180f), Filler(4392.601f, 33f, 6442.104f, 180f), Stair(4392.142f, 33.5f, 6442.911f, 157.5f),
            Filler(4391.568f, 34f, 6444.297f, 157.5f), Stair(4390.295f, 34.5f, 6444.759f, 112.5f), Stair(4387.682f, 35.5f, 6444.758f, 67.5f),
            Stair(4385.727f, 36.5f, 6443.452f, 45f), Stair(4383.313f, 37.5f, 6442.452f, 90f) });
        var up = new StairPlan { Kit = kit.Name, Reached = true };
        up.Pieces.AddRange(new[] {
            Filler(4394.101f, 32f, 6438.604f, 270f), Filler(4393.955f, 32f, 6438.544f, 225f), Stair(4395.015f, 32.5f, 6439.604f, 225f),
            Filler(4396.076f, 33f, 6440.665f, 225f), Stair(4396.970f, 33.5f, 6440.911f, 247.5f), Stair(4398.818f, 34.5f, 6441.676f, 247.5f),
            Stair(4400.666f, 35.5f, 6442.441f, 247.5f), Piece(StairPieceKind.Landing, kit.Floor, 4392.601f, 32f, 6438.604f, 180f) });
        return (below, up);
    }

    /// <summary>A V across the valley on its line: 31 at its bottom, rising 1.1 a metre to 35.1 where the span meets the
    /// first flight's stair at 34.5 (the floors' ends in the slope; read flat, no span was found here).</summary>
    internal static float SharedFloorValleyGround(float x, float z)
    {
        Vector2 l1 = new Vector2(4391.22f, 6444.38f), along = StairLayout.Forward(112.5f);
        return 31f + 1.1f * Mathf.Abs(Vector2.Dot(new Vector2(x, z) - l1, along) - 3.7f);
    }

    /// <summary>The second flight's top edge in <see cref="SharedFloorValley"/>: where it is laid again up to.</summary>
    internal static readonly Vector2 SharedFloorValleyTop = new(4401.59f, 6442.82f);

    /// <summary>A valley (4392-4398,6438-6444): both flights went down to a shared floor at 32.5 and doubled
    /// back. Taking off the bottom two stairs of each, the floor and its fillers, and laying floors at 34.5
    /// on from the first's stair at 34.5 toward the second's is smoother, with no 180 degree turn.</summary>
    [Fact]
    public void AValleyIsSpannedInLineAboveItsSwitchback()
    {
        StairKit kit = StairKit.BlackMarble;
        var (first, second) = SharedFloorValley();
        Assert.True(RoadStairRuns.SpanTheValley(first, second, kit, SharedFloorValleyGround, 32.5f, 2f,
            FlatRunJoinTests.Relay(SharedFloorValleyGround, SharedFloorValleyTop, kit), (_, _) => true, out StairPlan spanned, out Vector2 from));
        Assert.DoesNotContain(first.Pieces, p => p.Kind != StairPieceKind.Support && p.Position.y < 34.4f);
        Assert.DoesNotContain(spanned.Pieces, p => p.Kind != StairPieceKind.Support && p.Position.y + (p.Kind == StairPieceKind.Stair ? 0f : kit.FloorTop) < 34.4f);
        var floors = spanned.Pieces.Where(p => p.Kind == StairPieceKind.Landing).ToList();
        Assert.NotEmpty(floors);
        Assert.All(floors, f => Assert.True(f.Position.y + kit.FloorTop > 34.49f, $"floor at {f.Position}"));
        Assert.True(Vector2.Distance(from, new Vector2(4391.22f, 6444.376f)) < 0.1f, $"span from {from}");
        // Its first stair kept in the first, the 2x1 wedge of the cut turn gone; the second's pieces snapped to it.
        Assert.Contains(first.Pieces, p => p.Kind == StairPieceKind.Stair && Mathf.Abs(p.Position.y - 34.5f) < 0.01f);
        Assert.DoesNotContain(first.Pieces, p => p.Kind == StairPieceKind.Filler);
        Assert.Empty(FlatRunJoinTests.JoinFaults(first.Pieces.Concat(spanned.Pieces).ToList(), kit));
    }

    /// <summary>At the mountain cave (-2556.7,1554.2): the stairs must not stick out past the edge of the cliff, only the bottom step. A foot on a ledge
    /// with a sharp drop beside it sets off along the ledge instead; a steady slope crossed (the flight there, 0.525 a metre
    /// across, probed) is no edge.</summary>
    [Fact]
    public void AFootDoesNotStandOutPastALedgesEdgeButMayCrossASlope()
    {
        // The fourth stair (-2551.953, 159.960, 1550.053) yaw 292.5 on a plane through the probe: a slope, not an edge.
        var centre = new Vector2(-2551.953f, 1550.053f);
        Vector2 dir = StairLayout.Forward(112.5f), x = new Vector2(Mathf.Cos(292.5f * Mathf.Deg2Rad), -Mathf.Sin(292.5f * Mathf.Deg2Rad));
        float Probe(float px, float pz)
        {
            var d = new Vector2(px, pz) - centre;
            return 159.46f + 0.525f * Vector2.Dot(d, x) + 0.08f * Vector2.Dot(d, dir);
        }
        Assert.Equal(0f, StairLayout.HangsOverEdge(centre - dir * (StairLayout.PieceRun / 2f), dir, StairLayout.Right(112.5f), 1f, 159.96f, Probe));

        // A ledge rising north with a metre's drop east of x = 0.6: set off north from (0, 0), the first stairs' right side
        // stood out past the edge; with the rule the flight sets off along the ledge.
        static float Ledge(float px, float pz) => 0.5f * Mathf.Max(0f, pz) - (px > 0.6f ? 1f : 0f);
        bool Hangs(StairPlan plan) => plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair).Take(StairLayout.EdgeStairs).Any(p =>
        {
            float climb = StairLayout.Wrap(p.YawDegrees + 180f);
            Vector2 d = StairLayout.Forward(climb);
            return StairLayout.HangsOverEdge(new Vector2(p.Position.x, p.Position.z) - d * (StairLayout.PieceRun / 2f), d, StairLayout.Right(climb), 1f, p.Position.y, Ledge) > 0f;
        });
        StairPlan with = StairLayout.Build(new Vector2(0f, 0f), new Vector2(-2f, 16f), Ledge, arriving: 0f);
        Assert.True(with.Reached, with.Reason);
        Assert.False(Hangs(with), "a foot stair stands out past the ledge's edge");
        // A stretch laid again without the price (RoadStairRuns.Lay, where the price closed the only way) may stand out past it.
        StairLayout.UnpricedLedge = true;
        try
        {
            StairPlan unpriced = StairLayout.Build(new Vector2(0f, 0f), new Vector2(-2f, 16f), Ledge, arriving: 0f);
            StairLayout.UnpricedLedge = false;
            Assert.True(unpriced.Reached, unpriced.Reason);
            Assert.True(Hangs(unpriced), "unpriced, no foot stair stood out anyway");
        }
        finally { StairLayout.UnpricedLedge = false; }
    }

    /// <summary><paramref name="plan"/> with its last two stairs laid, ending at its End on its EndHeading (the end reader
    /// reads a top off its last stair).</summary>
    private static StairPlan WithTopStair(StairPlan plan)
    {
        Vector2 dir = StairLayout.Forward(plan.EndHeading);
        for (int k = 1; k <= 2; k++)
            plan.Pieces.Insert(0, new StairPiece { Kind = StairPieceKind.Stair, Prefab = StairKit.Named(plan.Kit)!.Stair,
                Position = new Vector3(plan.End.x - dir.x * (k - 0.5f) * StairLayout.PieceRun, plan.End.y - k * StairLayout.PieceRise,
                    plan.End.z - dir.y * (k - 0.5f) * StairLayout.PieceRun),
                YawDegrees = StairLayout.Wrap(plan.EndHeading + 180f) });
        return plan;
    }

    private sealed record LedgeFinishAndPieces(StairLayout.TopFinish Finish, List<StairPiece> Pieces);

    /// <summary>A third floor (-9286.1,691.6): the surface past the top 0.1-0.6 m under it, so the top is improved with a 2x2.
    /// The hand-built last stair (-9286.095, 173.029, 691.647) yaw 247.5, top edge 174.03; its
    /// stone_floor_2x2 at (-9284.247, 173.529, 692.413), level with the top, slightly proud of the snow. The snow's drops under
    /// the top are written in steps (StairLayout.StepUp; the heights read in game at 0.2), so the rule holds whatever StepUp is.
    /// By the end rule (5 Nov) the snow at the floor's far corners, more than a step under it, takes the rule's stair down,
    /// walked off onto the snow.</summary>
    [Fact]
    public void ATopAlittleOverTheSnowTakesTheRulesStairDown()
    {
        Vector2 edge = new(-9285.171f, 692.030f), dir = StairLayout.Forward(67.5f);
        const float top = 174.029f;
        float[] ds = { 0f, 1f, 2f, 3f, 4f }, steps = { 3.145f, 2.145f, 1.145f, 0.645f, 0.645f };
        float[] hs = steps.Select(s => top - StairLayout.StepUp * s).ToArray();
        float Ground(float x, float z)
        {
            float d = Vector2.Dot(new Vector2(x, z) - edge, dir);
            if (d <= 0f) return hs[0] + 0.5f * d;
            for (int i = 1; i < ds.Length; i++) if (d <= ds[i]) return Mathf.Lerp(hs[i - 1], hs[i], (d - ds[i - 1]) / (ds[i] - ds[i - 1]));
            return hs[hs.Length - 1];
        }
        var plan = WithTopStair(new StairPlan { Kit = StairKit.Stone.Name, Reached = true, End = new Vector3(edge.x, top, edge.y), EndHeading = 67.5f,
            LastRowKind = StairPieceKind.Stair, LastRowPieces = 0 });
        Assert.Equal(StairEnds.Case.Reverse, StairLayout.TopCase(plan, Ground, 2f));
        Assert.Equal(StairLayout.TopFinish.LandingThenDown, RoadStairRuns.FinishOverLedge(plan, Ground, 2f, _ => true));
        StairEnds.End end = RoadStairRuns.TopEnd(plan, Ground)!;
        Assert.True(end.Verdict == StairEnds.Verdict.Ok, StairEnds.Line(end));
    }

    /// <summary>A staircase whose foot stands on a mound 2.3 m over its road 5 m back (-9308.1,690.3: the bottom of the stairs
    /// was not walkable, the earthworks too steep): one more stair down from the
    /// foot toward the road, cut into the mound, the sunk step under the foot becoming it; the road meets the new foot.</summary>
    private static float Mound(float x, float z) => z < -5f ? 0f : z < 0f ? 0.46f * (z + 5f) : 2.3f + 0.5f * z;

    [Fact]
    public void AFootOnAMoundIsLoweredToItsRoad()
    {
        StairPlan Lay() => StairLayout.Build(new Vector2(0f, 0f), new Vector2(0f, 20f), Mound, arriving: 0f, sunkFoot: true);
        StairPlan plan = Lay();
        Assert.True(plan.Reached, plan.Reason);
        Assert.NotNull(plan.Foot);
        int stairs = plan.Pieces.Count(p => p.Kind == StairPieceKind.Stair);
        Vector3? foot = StairLayout.LowerFoot(plan, Mound, 2f, 0f, 5f, 0.35f);
        Assert.NotNull(foot);
        Assert.Equal(1, plan.FootLowered);
        Assert.Equal(1.3f, foot!.Value.y, 2);
        Assert.Equal(-2f, foot.Value.z, 2);
        // The sunk step went; one stair down took its location, uncovered: the same number of stair pieces, one more walked.
        Assert.Equal(stairs, plan.Pieces.Count(p => p.Kind == StairPieceKind.Stair));
        Assert.Contains(plan.Pieces, p => p.Kind == StairPieceKind.Stair && Math.Abs(p.Position.y - 1.3f) < 0.01f && Math.Abs(p.Position.z + 1f) < 0.01f);
        // First in the lists, in walking order from the foot, as the sunk step was: the first walked piece is the new foot's
        // stair (read so by the start pad, RoadEndRing.PadUnder; appended last, the pad found the stair a rise over the road).
        StairPiece first = plan.Pieces.First(p => p.Kind != StairPieceKind.Support);
        Assert.Equal(StairPieceKind.Stair, first.Kind);
        Assert.Equal(foot.Value.y, StairLayout.Footprint(first).height, 2);
        // Lowered by two: both new stairs first, the lowest first.
        StairPlan twice = Lay();
        Vector3? low = StairLayout.LowerFoot(twice, Mound, 2f, 0f, 5f, 0.1f);
        Assert.NotNull(low);
        Assert.Equal(2, twice.FootLowered);
        var walked = twice.Pieces.Where(p => p.Kind == StairPieceKind.Stair).Take(2).Select(p => StairLayout.Footprint(p).height).ToList();
        Assert.Equal(2, walked.Count);
        Assert.Equal(low!.Value.y, walked[0], 2);
        Assert.Equal(low.Value.y + StairLayout.PieceRise, walked[1], 2);
    }
    /// <summary>A foot over its road's ground lowers until it reaches it, rounded up where nothing raises its road (StairLayout.StairsDownTo: "a stair a
    /// bit sunk is fine"): more than a step over, a stair drops, and stairs on until it is within a step or under. The mound's
    /// foot stands 2.3 m up, the plain road 5 m back climbing at the cap. Rounded to the nearest whole stair, a foot 1.3 m over
    /// took one stair and was left 0.3 m over its road, and one 0.3 m over took none.</summary>
    [Theory]
    [InlineData(0.40f, 1)]   // over 0.3: one stair, more than a step
    [InlineData(0.35f, 1)]   // over 0.55: one stair
    [InlineData(0.20f, 2)]   // over 1.3: two, the second a bit sunk (to the nearest, one left it 0.3 m over)
    [InlineData(0.10f, 2)]   // over 1.8: two
    public void AFootLowersUntilItReachesTheGround(float cap, int stairs)
    {
        StairPlan plan = StairLayout.Build(new Vector2(0f, 0f), new Vector2(0f, 20f), Mound, arriving: 0f, sunkFoot: true);
        Assert.True(plan.Reached, plan.Reason);
        Vector3? foot = StairLayout.LowerFoot(plan, Mound, 2f, 0f, 5f, cap);
        Assert.Equal(stairs, plan.FootLowered);
        Assert.Equal(stairs > 0, foot.HasValue);
    }

    /// <summary>A foot carried to a road held at a height (a crossing's end, StairLayout.FootToRoad) by whole stairs: left
    /// within a walker's step it meets the road there (1.15 m over: one stair, 0.15 over). Left 0.45 m over it (a step a player
    /// can't walk up), one stair more is sunk into the road under the foot, uncut, its top at the foot, so the walk meets the
    /// road on its slope; left 0.3 m under it (1.7 m over: two stairs would bury the foot), one stair is carried, a stair sunk under it
    /// (the one the foot was laid with goes with the stairs added), the foot 0.7 m over the road. Never further over than under a rise.</summary>
    [Theory]
    [InlineData(1.15f, 1, false, false)]
    [InlineData(1.45f, 1, true, true)]
    [InlineData(1.7f, 1, true, true)]
    public void AFootOverARoadHeldAtAHeightStandsOnAStairSunkIntoIt(float over, int lowered, bool sunk, bool laid)
    {
        StairPlan plan = StairLayout.Build(new Vector2(0f, 0f), new Vector2(0f, 20f), Mound, arriving: 0f, sunkFoot: true);
        Assert.True(plan.Reached && plan.SunkPieces > 0 && plan.Foot.HasValue, plan.Reason);
        float road = plan.Foot!.Value.y - over;
        var met = StairLayout.FootToRoad(plan, Mound, 2f, road);
        Assert.True(met.HasValue, "the foot does not meet the road");
        var (foot, meets, onSunk, laidHere) = met!.Value;
        Assert.Equal(lowered, plan.FootLowered);
        Assert.Equal((sunk, laid), (onSunk, laidHere));
        Assert.True(StairLayout.FootMeets(plan, foot.y, road));
        Assert.True(foot.y - road < StairLayout.PieceRise);
        if (!sunk)
        {
            Assert.True(StairLayout.Walkable(foot.y, road));
            Assert.Equal(foot.y, meets, 3);
            return;
        }
        Assert.Equal(road, meets, 3);
        Assert.False(StairLayout.Walkable(foot.y, road));
        // The sunk stair: first in the plan's pieces, marked sunk, its base a rise under the foot, a run out from it.
        Assert.True(plan.SunkPieces > 0);
        for (int i = 0; i < plan.SunkPieces; i++)
        {
            Assert.True(plan.Pieces[i].Sunk && plan.Pieces[i].Kind == StairPieceKind.Stair);
            Assert.Equal(foot.y - StairLayout.PieceRise, plan.Pieces[i].Position.y, 3);
        }
        Assert.DoesNotContain(plan.Pieces.Skip(plan.SunkPieces), p => p.Sunk);
    }

    /// <summary>A staircase's own sunk step (Emit's, laid along its first heading whichever way its road comes) is not a
    /// meeting with a road 0.5 m under its foot: nothing checked it lies over that road. Only the stair FootToRoad sank for
    /// that road is (a foot turned off its road would meet it side on, over a lip a player can't walk up).</summary>
    [Fact]
    public void AStaircasesOwnSunkStepAloneDoesNotMeetARoadUnderItsFoot()
    {
        StairPlan plan = StairLayout.Build(new Vector2(0f, 0f), new Vector2(0f, 20f), Mound, arriving: 0f, sunkFoot: true);
        Assert.True(plan.Reached && plan.SunkPieces > 0 && plan.Foot.HasValue, plan.Reason);
        float foot = plan.Foot!.Value.y;
        Assert.Null(plan.SunkStairMeets);
        Assert.False(StairLayout.FootMeets(plan, foot, foot - 0.5f));
        Assert.True(StairLayout.FootMeets(plan, foot, foot - 0.15f));
    }

    /// <summary>A foot 1.12 m over its road's ground 5 m back (-5353.5,2896.1). The road ends there on its level landing
    /// (6 m at 5 %, RoadPitches.ClimbToFoot), climbing 0.25 m over those 5 m, not 1.75 m at the cap: one stair more down
    /// from the foot toward the road. Read climbing at the cap, the foot was left as it was and its road's landing stood on
    /// 1.2 m of fill. On plain roads (no landing) the cap line still says the road climbs to it.</summary>
    private static float Shoulder(float x, float z) => z < -5f ? 0f : z < 0f ? 0.224f * (z + 5f) : 1.12f + 0.5f * z;

    [Fact]
    public void AFootOverItsRoadsLandingIsLowered()
    {
        StairPlan Lay() => StairLayout.Build(new Vector2(0f, 0f), new Vector2(0f, 20f), Shoulder, arriving: 0f, sunkFoot: true);
        StairPlan plain = Lay();
        Assert.True(plain.Reached, plain.Reason);
        Assert.Equal(1.12f, plain.Foot!.Value.y, 2);
        Assert.Null(StairLayout.LowerFoot(plain, Shoulder, 2f, 0f, 5f, 0.35f));
        using var landing = StaticOverride.Keep(() => RoadPitches.JunctionLanding);
        RoadPitches.JunctionLanding = (float)PlainRoads.ModDefaults["junction landing"];
        StairPlan plan = Lay();
        Vector3? foot = StairLayout.LowerFoot(plan, Shoulder, 2f, 0f, 5f, 0.35f);
        Assert.NotNull(foot);
        Assert.Equal(1, plan.FootLowered);
        Assert.Equal(0.12f, foot!.Value.y, 2);
        Assert.Equal(-2f, foot.Value.z, 2);
    }

    /// <summary>A foot whose road's ground rises 0.15 m per metre behind it: within what a road climbs at the cap, but its
    /// road ends there on its level landing (RoadPitches.ClimbToFoot), so the ground 4 m back stands 0.4 m over the landing
    /// and more than a step over the foot: a bank, and an apron is cut behind the foot. On plain roads (no landing) the cap
    /// line climbs over it and none is cut.</summary>
    [Fact]
    public void AFootWhoseRoadLandsBelowItsGroundGetsAnApron()
    {
        float Rising(float x, float z) => 40f + Mathf.Max(0f, -x) * 0.5f + Mathf.Max(0f, x) * 0.15f;
        StairPlan Lay() => StairLayout.Build(new Vector2(0f, 0f), new Vector2(-16f, 0f), Rising, sunkFoot: true);
        StairPlan plain = Lay();
        Assert.True(plain.Reached, plain.Reason);
        Assert.Equal(0, StairLayout.CutFootApron(plain, Rising, 2f, RoadGrade.Configured));
        using var landing = StaticOverride.Keep(() => RoadPitches.JunctionLanding);
        RoadPitches.JunctionLanding = (float)PlainRoads.ModDefaults["junction landing"];
        StairPlan banked = Lay();
        Assert.True(StairLayout.CutFootApron(banked, Rising, 2f, RoadGrade.Configured) > 0, "no apron where the road's landing meets rising ground");
    }

    /// <summary>A foot's apron reads the cap its caller gives it, as LowerFoot does (no fallback of its own), and a road with no
    /// cap (0) is built to climb nothing to a foot (RoadPitches.ClimbToFoot): the bank over it is judged against that.</summary>
    [Fact]
    public void AnApronReadsTheCapItsCallerGives()
    {
        float Rising(float x, float z) => 40f + Mathf.Max(0f, -x) * 0.5f + Mathf.Max(0f, x) * 0.15f;
        StairPlan Lay() => StairLayout.Build(new Vector2(0f, 0f), new Vector2(-16f, 0f), Rising, sunkFoot: true);
        using var landing = StaticOverride.Keep(() => RoadPitches.JunctionLanding);
        RoadPitches.JunctionLanding = (float)PlainRoads.ModDefaults["junction landing"];
        Assert.Equal(0f, RoadPitches.ClimbToFoot(10f, 0f));
        RoadPitches.JunctionLanding = 0f;
        Assert.Equal(0, StairLayout.CutFootApron(Lay(), Rising, 2f, RoadConstants.DefaultMaxRoadGrade));
        Assert.True(StairLayout.CutFootApron(Lay(), Rising, 2f, 0f) > 0, "a road with no cap climbs nothing to the foot: the bank stands");
    }
}
