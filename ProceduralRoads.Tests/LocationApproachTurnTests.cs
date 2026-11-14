using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Xunit.Abstractions;
namespace ProceduralRoads.Tests;
public class LocationApproachTurnTests
{
    private readonly ITestOutputHelper _out;
    public LocationApproachTurnTests(ITestOutputHelper output) => _out = output;

    private static readonly IReadOnlyList<LevelOp> CaveFloor = new[] { new LevelOp(-0.06f, -4.75f, -4.09f, 4f, false, 7f, 3f) };

    /// <summary>Level ground at 50 before a frost cave facing +x (yaw 90), with its floor levelled as the
    /// game leaves it, and a band across the way in at x 10-26, flat for 4 m in the middle with sides at
    /// 1:2: a ridge (+) or a valley (-) of <paramref name="bump"/> m.</summary>
    private static System.Func<float, float, float> Ground(float bump) => (x, z) =>
    {
        float h = 50f + bump * Mathf.Clamp01(1f - (Mathf.Abs(x - 18f) - 2f) / 6f);
        LevelOp op = CaveFloor[0];
        Vector2 floor = LocationLevelling.OffsetAt(op, 90f);
        float d = Vector2.Distance(new Vector2(x, z), floor);
        float level = 50f + op.Height;
        if (d <= op.Radius) h = level;
        if (d <= op.SmoothRadius) h = Mathf.Lerp(h, level, 1f - Mathf.Pow(d / op.SmoothRadius, op.SmoothPower));
        return h;
    };

    private static RoadStairSpan? Plan(System.Func<float, float, float> ground, out string why)
    {
        var end = new Vector2(30f, 0f);
        Vector2 before = new Vector2(60f, 0f);
        var request = new LocationApproachRequest
        {
            RoadEnd = end, RoadHeight = ground(end.x, end.y), Arriving = StairLayout.Bearing(before, end),
            Centre = new Vector2(0f, 0f), RoadTail = new List<Vector2> { before, end },
        };
        return RoadStairRuns.PlanPlacedLocationApproach(request, 90f, CaveFloor, TestLattice.Of(ground), (x, z) => Heightmap.Biome.Mountain, 77, out why);
    }

    [Fact]
    public void TheHighestOrLowestPointBetweenIsFoundAwayFromTheEnds()
    {
        var ridge = RoadStairRuns.Turn(new Vector2(30f, 0f), new Vector2(6f, 0f), Ground(3f));
        Assert.True(ridge.HasValue && ridge.Value.crest);
        Assert.InRange(ridge!.Value.at.x, 17f, 19f);
        var valley = RoadStairRuns.Turn(new Vector2(30f, 0f), new Vector2(6f, 0f), Ground(-10f));
        Assert.True(valley.HasValue && !valley.Value.crest);
        Assert.InRange(valley!.Value.at.x, 17f, 19f);
        // Under a rise's worth either way: one staircase, or none needed.
        Assert.Null(RoadStairRuns.Turn(new Vector2(30f, 0f), new Vector2(6f, 0f), Ground(0.5f)));
    }

    [Theory]
    [InlineData(3f)]
    [InlineData(-10f)]
    public void ARoadRefusedOverARidgeOrValleyGetsStairsUpAndDown(float bump)
    {
        RoadStairSpan? span = Plan(Ground(bump), out string why);
        _out.WriteLine(why);
        Assert.NotNull(span);
        var walked = span!.Plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair).ToList();
        Assert.NotEmpty(walked);
        float top = walked.Max(p => p.Position.y), bottom = walked.Min(p => p.Position.y);
        if (bump > 0f) Assert.True(top >= 50f + bump - 1.5f, $"top {top}");
        else Assert.True(bottom <= 50f + bump + 0.5f, $"bottom {bottom}");
    }
}

public class StairDescentTests
{
    private static StairPlan TopAt(float height) => new StairPlan
    {
        Kit = StairKit.Stone.Name, End = new Vector3(0f, height, 0f), EndHeading = 90f,
    };

    [Fact]
    public void ATopOverTheGroundAheadGetsAStepDownToIt()
    {
        // The top's walking surface 0.8 m over the clearing: one stair, facing back, its low end at 4 m.
        StairPlan plan = TopAt(5f);
        var added = StairLayout.AddDescent(plan, (x, z) => 4.2f, 2f);
        Assert.True(added.pieces >= 1);
        StairPiece step = plan.Pieces.First(p => p.Kind == StairPieceKind.Stair);
        Assert.Equal(4f, step.Position.y, 3);
        Assert.True(Vector2.Distance(new Vector2(step.Position.x, step.Position.z), new Vector2(0f, 0f) + StairLayout.Forward(90f) * 1f) < 0.01f);
        Assert.Equal(1, plan.Stairs);
        Assert.Equal(added.pieces, plan.Pieces.Count);
    }

    [Fact]
    public void ATopAPlayerCanStepOntoIsLeftAlone()
    {
        StairPlan plan = TopAt(5f);
        Assert.Equal(0, StairLayout.AddDescent(plan, (x, z) => 5f - StairLayout.StepUp + 0.01f, 2f).pieces);
        // Over it by up to a ledge (StairRailing.LedgeDrop): one row, a top's single reversed stair; past a ledge none (the
        // end rule steps back: a stair down would not reach the ground).
        plan = TopAt(5f);
        StairLayout.AddDescent(plan, (x, z) => 5f - (StairLayout.StepUp + StairRailing.LedgeDrop) / 2f, 2f);
        Assert.Equal(1, plan.Stairs);
        plan = TopAt(5f);
        Assert.Equal(0, StairLayout.AddDescent(plan, (x, z) => 5f - StairRailing.LedgeDrop - StairLayout.PieceRise, 2f).pieces);
    }

    [Fact]
    public void NoStairIsAddedPastAnEndWhereNothingWithinReachWouldHoldItUp()
    {
        // 20 m over flat ground with no slope within the corbels' reach: the kit could not hold a stair there (StairSupport
        // refuses it, as the search refuses its own pieces), so none is added down from the top.
        StairPlan plan = TopAt(20f);
        Assert.Equal(0, StairLayout.AddDescent(plan, (x, z) => 0f, 2f).pieces);
        Assert.Empty(plan.Pieces);
        // Nor on down from a foot to a road below it.
        plan = new StairPlan { Kit = StairKit.Stone.Name, Reached = true, StartHeading = 0f, Foot = new Vector3(0f, 20f, 0f) };
        Assert.Null(StairLayout.FootToRoad(plan, (x, z) => 0f, 2f, 17f));
        Assert.Empty(plan.Pieces);
        Assert.Equal(new Vector3(0f, 20f, 0f), plan.Foot);
    }
}

public class LedgeFinishTests
{
    /// <summary>A staircase topped by a floor at 10 m facing +x, its front edge at x = 2.</summary>
    private static StairPlan TopFloor()
    {
        var plan = new StairPlan { Kit = StairKit.Stone.Name, LastRowPieces = 0, LastRowClearances = 0, LastRowPieceCount = 1, LastRowClearanceCount = 0,
            LastRowKind = StairPieceKind.Landing, LastRowStart = new Vector2(0f, 0f), LastRowHeading = 90f, LastRowHeight = 10f, Landings = 1,
            End = new Vector3(2f, 10f, 0f), EndHeading = 90f };
        plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Landing, Prefab = StairKit.Stone.Floor, Position = new Vector3(1f, 9.5f, 0f) });
        return plan;
    }

    /// <summary>Over a lip a ledge under its top floor (StairRailing.LedgeDrop), read at the floor's own far corners (the
    /// end rule: up to a ledge, a stair down): with room past it, the floor is kept and the stair down laid off it (landing
    /// first: "it looks better with a 2x2 between them"); with none, the floor itself becomes the step down, back to back,
    /// within its own 2 m; within a step, it stays.</summary>
    [Fact]
    public void OverALedgeTheTopFloorBecomesTheStepDown()
    {
        StairPlan room = TopFloor();
        Assert.Equal(StairLayout.TopFinish.FloorThenDown, RoadStairRuns.FinishOverLedge(room, (x, z) => 10f - (StairLayout.StepUp + StairRailing.LedgeDrop) / 2f, 2f, _ => true));
        Assert.Single(room.Pieces, p => p.Kind == StairPieceKind.Landing);
        Assert.Single(room.Pieces, p => p.Kind == StairPieceKind.Stair && p.Position.x > 2f);

        // Nothing may stand past the landing's front edge: back to back, within the landing's own 2 m.
        StairPlan tight = TopFloor();
        Assert.Equal(StairLayout.TopFinish.BackToBack,
            RoadStairRuns.FinishOverLedge(tight, (x, z) => 10f - (StairLayout.StepUp + StairRailing.LedgeDrop) / 2f, 2f, p => p.Pieces.All(pc => pc.Position.x <= 2.01f)));
        Assert.DoesNotContain(tight.Pieces, p => p.Kind == StairPieceKind.Landing);
        Assert.Single(tight.Pieces, p => p.Kind == StairPieceKind.Stair);

        // Level with the lip, within a step: left as it is.
        StairPlan level = TopFloor();
        Assert.Equal(StairLayout.TopFinish.None, RoadStairRuns.FinishOverLedge(level, (x, z) => 10f - StairLayout.StepUp / 2f, 2f, _ => true));
        Assert.Single(level.Pieces);
    }
}

public class LocationApproachToppingFloorTests
{
    [Fact]
    public void AToppingFloorOverTheGroundBecomesAStairDownInItsOwnPlace()
    {
        // A floor at 10 m facing +x (heading 90), the ground past it at 9.2: a stair, base 9 m,
        // its high end at the floor's back edge, nothing laid beyond the floor's own 2 m.
        var plan = new StairPlan { Kit = StairKit.Stone.Name, LastRowPieces = 0, LastRowClearances = 0, LastRowPieceCount = 1, LastRowClearanceCount = 0,
            LastRowKind = StairPieceKind.Landing, LastRowStart = new Vector2(0f, 0f), LastRowHeading = 90f, LastRowHeight = 10f, Landings = 1 };
        plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Landing, Prefab = StairKit.Stone.Floor, Position = new Vector3(1f, 9.5f, 0f) });
        var column = new StairPiece { Kind = StairPieceKind.Support, Prefab = "stone_pillar", Position = new Vector3(-5f, 3f, 0f) };
        plan.Pieces.Add(column);   // sunk later for an earlier piece: stays
        Assert.True(StairLayout.StepDownTop(plan, (x, z) => 9.2f, 2f));
        Assert.Contains(column, plan.Pieces);
        Assert.DoesNotContain(plan.Pieces, p => p.Kind == StairPieceKind.Landing);
        StairPiece step = plan.Pieces.Single(p => p.Kind == StairPieceKind.Stair);
        Assert.Equal(9f, step.Position.y, 3);
        Assert.InRange(step.Position.x, 0.9f, 1.1f);
        Assert.True(Vector3.Distance(new Vector3(2f, 9f, 0f), plan.End) < 0.001f, $"end {plan.End}");
        // A floor the player steps onto (the ground within a step, StairLayout.StepUp, under it) is left alone, and so is a
        // staircase ending on a stair.
        plan = new StairPlan { Kit = StairKit.Stone.Name, LastRowPieces = 0, LastRowPieceCount = 0, LastRowClearances = 0,
            LastRowKind = StairPieceKind.Landing, LastRowHeading = 90f, LastRowHeight = 10f };
        Assert.False(StairLayout.StepDownTop(plan, (x, z) => 10f - StairLayout.StepUp / 2f, 2f));
        plan.LastRowKind = StairPieceKind.Stair;
        Assert.False(StairLayout.StepDownTop(plan, (x, z) => 5f, 2f));
    }

    [Fact]
    public void ApproachStairsStopWhereTheGroundToTheLandingIsWalkable()
    {
        // Road end at x 30, 10 m higher; a steep bank from x 30 to 20, then gentle ground (10%) to the landing at x 0.
        float Ground(float x, float z) => x >= 20f ? 50f + (x - 20f) : 50f + 0.1f * (x - 20f);
        Vector2 flats = RoadStairRuns.FlatsFrom(new Vector2(30f, 0f), new Vector2(0f, 0f), Ground);
        Assert.InRange(flats.x, 19f, 21f);
        // Steep right up to the landing: the landing stays the goal.
        Assert.Equal(new Vector2(0f, 0f), RoadStairRuns.FlatsFrom(new Vector2(30f, 0f), new Vector2(0f, 0f), (x, z) => 50f + 0.6f * x));
        // Gentle all the way: the walk ends at the road (nothing to lay).
        Assert.True(Vector2.Distance(RoadStairRuns.FlatsFrom(new Vector2(30f, 0f), new Vector2(0f, 0f), (x, z) => 50f + 0.2f * x), new Vector2(30f, 0f)) < 1.01f);
    }

    /// <summary>A crest (the cantilevered staircase by the mountain cave at 1920,704): its top edge at
    /// 170.08 facing 315, the plateau 0.8 m higher just past it. One more step up and the same step turned round,
    /// back to back, down onto the plateau: hand-laid stone_stairs 54255 (1931.346, 170.077, 702.033) yaw 135 and 54264
    /// (1929.932, 170.077, 703.447) yaw 315. Without the rule, the top stays 0.8 m under the lip. By the end rule (5 Nov) the
    /// top is short there and climbs the hand-laid up step; the plateau a floor's size on from it within a step of it takes a
    /// floor, else the hand-laid down step (the crest).</summary>
    [Fact]
    public void UnderALipByLessThanARiseTheTopClimbsTheHandLaidStep()
    {
        Vector2 edge = new(1932.053f, 701.326f), dir = StairLayout.Forward(315f);
        float Ground(float x, float z)
        {
            float s = Vector2.Dot(new Vector2(x, z) - edge, dir);
            return s >= 1f ? 170.9f : 169.8f + 0.55f * (s + 1f);
        }
        StairPlan Top() => new() { Kit = StairKit.Stone.Name, Reached = true, End = new Vector3(edge.x, 170.077f, edge.y), EndHeading = 315f };

        StairPlan plan = Top();
        bool floor = Mathf.Abs(170.077f + StairLayout.PieceRise - 170.9f) <= StairLayout.StepUp;
        Assert.Equal(floor ? StairLayout.TopFinish.TopFloor : StairLayout.TopFinish.Crest, RoadStairRuns.FinishOverLedge(plan, Ground, 2f, _ => true));
        var stairs = plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair).ToList();
        Assert.Equal(floor ? 1 : 2, stairs.Count);
        Assert.True(Vector3.Distance(new Vector3(1931.346f, 170.077f, 702.033f), stairs[0].Position) < 0.01f, $"up step at {stairs[0].Position}");
        Assert.Equal(135f, stairs[0].YawDegrees, 2);
        if (!floor)
        {
            Assert.True(Vector3.Distance(new Vector3(1929.932f, 170.077f, 703.447f), stairs[1].Position) < 0.01f, $"down step at {stairs[1].Position}");
            Assert.Equal(315f, stairs[1].YawDegrees, 2);
        }

        // Over a lip more than a rise and a step-up high (and no climbing on), or on level ground, no crest.
        Assert.NotEqual(StairLayout.TopFinish.Crest, RoadStairRuns.FinishOverLedge(Top(), (x, z) => 171.5f, 2f, _ => true));
        Assert.NotEqual(StairLayout.TopFinish.Crest, RoadStairRuns.FinishOverLedge(Top(), (x, z) => 170.2f, 2f, _ => true));
    }

    /// <summary>A top (the mountain cave entry): its top edge at 172.7 facing +x, the ground past
    /// it rising to the shoulder, 1.1 m over the top 3-4 m on, which the top does not fully make. A crest's ridge, a
    /// rise over the top, meets it (a lip of a rise and a step-up); before, a lip over a rise was left. By the end rule
    /// (5 Nov) the top is short, climbs one stair, and the shoulder a floor's size on from it is within a step of it: a
    /// floor (0.11 m under it).</summary>
    [Fact]
    public void ACrestMeetsAShoulderARiseAndAStepUpOverTheTop()
    {
        float[] xs = { -9292.7f, -9292f, -9291f, -9290f, -9289f, -9287f }, hs = { 172.16f, 172.78f, 173.30f, 173.63f, 173.80f, 173.86f };
        float Ground(float x, float z)
        {
            if (x <= xs[0]) return hs[0] - (xs[0] - x) * 0.5f;
            for (int i = 1; i < xs.Length; i++) if (x <= xs[i]) return Mathf.Lerp(hs[i - 1], hs[i], (x - xs[i - 1]) / (xs[i] - xs[i - 1]));
            return hs[hs.Length - 1];
        }
        StairPlan Top() => new() { Kit = StairKit.Stone.Name, Reached = true, End = new Vector3(-9292.7f, 172.7f, 706.8f), EndHeading = 90f };
        Assert.Equal(StairEnds.Case.Short, StairLayout.TopCase(Top(), Ground, 2f));
        StairPlan plan = Top();
        Assert.Equal(StairLayout.TopFinish.TopFloor, RoadStairRuns.FinishOverLedge(plan, Ground, 2f, _ => true));
        Assert.Single(plan.Pieces, p => p.Kind == StairPieceKind.Stair);
    }

    /// <summary>A climb to a shelf whose ground goes on rising past the aim (a knoll, 1 m a metre for 4 m, then
    /// flat): finishing anywhere within reach of the aim, the staircase stopped under the knoll, the ground 2.5 m on
    /// more than a rise and a step-up over its top (seen at 3522,-5773); held
    /// to finish where the ground ahead is within that, it climbs on up.</summary>
    private static float Knoll(float x, float z) => x < 20f ? 0.5f * x : x < 24f ? 10f + (x - 20f) : 14f;

    [Fact]
    public void ATopFinishesWhereTheGroundAheadIsWithinReach()
    {
        StairPlan Lay() => StairLayout.Build(new Vector2(0f, 0f), new Vector2(20f, 0f), Knoll, arriveWithin: RoadStairRuns.LedgeReach,
            maxBuried: StairCover.ApproachSearch);
        float Lip(StairPlan p)
        {
            // The ground a floor out from the top stands over it, read at the two far corners of that floor directly (the gate's
            // own reader is not asked): the gentler of the two.
            Vector2 far = new Vector2(p.End.x, p.End.z) + StairLayout.Forward(p.EndHeading) * StairLayout.PieceRun;
            Vector2 side = StairLayout.Right(p.EndHeading) * (StairLayout.PieceRun / 2f);
            float a = Knoll(far.x - side.x, far.y - side.y) - p.End.y, b = Knoll(far.x + side.x, far.y + side.y) - p.End.y;
            return Mathf.Abs(a) <= Mathf.Abs(b) ? a : b;
        }
        StairPlan plan = Lay();
        Assert.True(plan.Reached, plan.Reason);
        Assert.True(Lip(plan) <= StairLayout.PieceRise + StairLayout.StepUp, $"top at {plan.End}, the ground {Lip(plan):F2} m over it ahead");
    }
}
