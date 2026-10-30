using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// A staircase from a road's end at a place up to the place's flat ground
/// (a frost cave's apron).
/// </summary>
public class SiteApproachStairTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    /// <summary>A place's flat apron at 52 m out to 12 m from the origin, a face of
    /// 1.2 below it down to 40 m at 22 m out, flat ground at 40 m beyond, sea past 200 m.</summary>
    private sealed class ApronWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            float r = new Vector2(wx, wy).magnitude;
            return 40f + Mathf.Clamp((22f - r) * 1.2f, 0f, 12f);
        }
        /// <summary>The Mountains, where a place gets an approach staircase (<see cref="RoadNetworkGenerator.ApproachStairsAt"/>).</summary>
        public Heightmap.Biome Biome = Heightmap.Biome.Mountain;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Biome;
        public override int GetSeed() => 77;
    }

    private const float LevelRadius = 12f;
    private const float Footprint = 20f;
    private static readonly IReadOnlyList<LevelOp> Apron = new[] { new LevelOp(0f, 0f, 0f, LevelRadius, false) };
    private readonly ApronWorld _world = new();

    public SiteApproachStairTests()
    {
        _scope.WithWorld(_world);
        RoadNetworkGenerator.Reset();
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(new Vector2(0f, 0f), Footprint) });
    }

    public void Dispose()
    {
        SetPathfinder(null);
        LocationLevelling.Source = null;
        RoadSiteProtection.Reset();
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, pathfinder);

    private RoadStairSpan? Lay(Vector2 end, IReadOnlyList<LevelOp>? ops, out string why)
    {
        var path = new List<Vector2> { end + new Vector2(-30f, 0f), end };
        return RoadStairRuns.LaySiteApproach(end, _world.GetHeight(end.x, end.y), StairLayout.Bearing(path[0], end),
            new Vector2(0f, 0f), 52f, ops, false, path, 1, new List<RoadStairSpan>(), _world, out why);
    }

    [Fact]
    public void ARoadEndBelowAPlacesApronGetsAStaircaseUpToIt()
    {
        RoadStairSpan? span = Lay(new Vector2(-24f, 0f), Apron, out string why);

        Assert.True(span != null, why);
        StairPlan plan = span!.Plan;
        Assert.False(plan.Reversed);
        Assert.Equal(new Vector2(-24f, 0f), span.FromPoint);
        // It ends on the apron, at the apron's height, just inside its edge.
        Assert.InRange(plan.End.y, 52f - StairLayout.ReachHeight, 52f + StairLayout.ReachHeight);
        float reached = new Vector2(plan.End.x, plan.End.z).magnitude;
        // Just inside its edge, or on it: arriving within reach of its aim a metre in, a top whose turn's filler
        // would have been buried (StairLayout.WedgeBuried) took another way and ended 5 cm past the edge.
        Assert.InRange(reached, LevelRadius - RoadStairRuns.ApronReach, LevelRadius + 0.1f);
        // Nothing walked far onto the apron.
        foreach (StairPiece piece in plan.Pieces.Where(p => p.Kind != StairPieceKind.Support))
            Assert.True(new Vector2(piece.Position.x, piece.Position.z).magnitude >= LevelRadius - RoadStairRuns.ApronReach,
                $"{piece.Kind} at {piece.Position}");
        Assert.True(plan.MaxCut - StairLayout.CutBelowSurface <= StairCover.ApproachRefusal, $"cut {plan.MaxCut:F2} m");
    }

    [Fact]
    public void ARoadEndAboveTheApronGetsAStaircaseDownToIt()
    {
        // The same place, sunk into the ground: the road arrives above it.
        var sunk = new[] { new LevelOp(0f, 0f, -12f, LevelRadius, false) };
        var world = new SunkWorld();
        _scope.WithWorld(world);
        var end = new Vector2(-24f, 0f);
        var path = new List<Vector2> { end + new Vector2(-30f, 0f), end };
        RoadStairSpan? span = RoadStairRuns.LaySiteApproach(end, world.GetHeight(end.x, end.y), StairLayout.Bearing(path[0], end),
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

    /// <summary>52 m flat, falling at 1.2 inside 22 m to a pit floor of 40 m; the place's centre stands at 52.</summary>
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

    [Fact]
    public void ItMayCrossItsOwnPlacesFootprintButNoOther()
    {
        // Another place's footprint over the face the staircase must climb.
        RoadSiteProtection.Set(new[]
        {
            new RoadSiteProtection.Footprint(new Vector2(0f, 0f), Footprint),
            new RoadSiteProtection.Footprint(new Vector2(-17f, 0f), 4f),
            new RoadSiteProtection.Footprint(new Vector2(-17f, 6f), 4f),
            new RoadSiteProtection.Footprint(new Vector2(-17f, -6f), 4f),
            new RoadSiteProtection.Footprint(new Vector2(-12f, 12f), 5f),
            new RoadSiteProtection.Footprint(new Vector2(-12f, -12f), 5f),
        });
        Assert.Null(Lay(new Vector2(-24f, 0f), Apron, out string why));
        Assert.StartsWith("another place at (", why);
    }

    [Fact]
    public void PlacedGroundIsLevelledInsideAndBlendedOutToTheSmoothingRadius()
    {
        var ops = new[] { new LevelOp(0f, 0f, 2f, 10f, false, smoothRadius: 20f, smoothPower: 3f) };
        Assert.Equal(52f, LocationLevelling.PlacedGroundAt(new Vector2(5f, 0f), new Vector2(0f, 0f), 50f, ops, 30f));
        // Halfway out the ground has moved 1 - 0.5^3 of the way to the level.
        Assert.Equal(Mathf.Lerp(30f, 52f, 0.875f), LocationLevelling.PlacedGroundAt(new Vector2(0f, 10f), new Vector2(0f, 0f), 50f,
            new[] { new LevelOp(0f, 0f, 2f, 5f, false, 20f, 3f) }, 30f), 3);
        Assert.Equal(30f, LocationLevelling.PlacedGroundAt(new Vector2(25f, 0f), new Vector2(0f, 0f), 50f, ops, 30f));
        // An operation off the centre is left out: where it lies depends on the rotation.
        Assert.Equal(30f, LocationLevelling.PlacedGroundAt(new Vector2(5f, 0f), new Vector2(0f, 0f), 50f, new[] { new LevelOp(4f, 0f, 2f, 10f, false, 20f, 3f) }, 30f));
    }

    [Fact]
    public void AGeneratedRoadToThePlaceClimbsTheLastStretchOnStairs()
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
        var request = new SiteApproachRequest
        {
            RoadEnd = end, RoadHeight = CaveGround(end.x, end.y), Arriving = StairLayout.Bearing(before, end),
            Centre = new Vector2(0f, 0f), RoadTail = new List<Vector2> { before, end },
        };
        return RoadStairRuns.PlanPlacedSiteApproach(request, 90f, CaveFloor, CaveGround, (x, z) => Heightmap.Biome.Mountain, 77, out why, rock);
    }

    [Fact]
    public void AnOffCentreFloorWaitsForThePlaceToBePlaced()
    {
        var world = new HillsideWorld();
        _scope.WithWorld(world);
        var end = new Vector2(24f, 0f);
        var path = new List<Vector2> { new Vector2(54f, 0f), end };
        Assert.Null(RoadStairRuns.LaySiteApproach(end, world.GetHeight(end.x, end.y), 270f, new Vector2(0f, 0f), 50f, CaveFloor, true,
            path, 1, new List<RoadStairSpan>(), world, out string why));
        Assert.Equal(RoadStairRuns.NotPlacedBeforeItsRoad, why);
        Assert.Null(RoadStairRuns.LaySiteApproach(end, world.GetHeight(end.x, end.y), 270f, new Vector2(0f, 0f), 50f, CaveFloor, false,
            path, 1, new List<RoadStairSpan>(), world, out why));
        Assert.Equal("flat off its centre, turned at random", why);
    }

    [Fact]
    public void AStaircaseOntoAPlaceWithADoorLandingEndsAtTheDoor()
    {
        const string prefab = "Mistlands_DvergrBossEntrance1";
        Vector2 end = new Vector2(40f, 0f), before = end + end.normalized * 30f;
        var request = new SiteApproachRequest
        {
            RoadEnd = end, RoadHeight = CaveGround(end.x, end.y), Arriving = StairLayout.Bearing(before, end),
            Centre = new Vector2(0f, 0f), RoadTail = new List<Vector2> { before, end },
        };
        PlacedSiteShape shape = PlacedSiteShape.Of(request.Centre, 90f, CaveFloor[0], 0f, null, CaveGround, prefab: prefab);
        RoadStairSpan? span = RoadStairRuns.PlanPlacedSiteApproach(request, 90f, CaveFloor, CaveGround, (x, z) => Heightmap.Biome.Mountain, 77,
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
        Assert.True(plan.MaxCut - StairLayout.CutBelowSurface <= StairCover.ApproachRefusal, $"cut {plan.MaxCut:F2} m");
        // Never down in the entrance: no piece within the floor's smoothing.
        foreach (StairPiece piece in plan.Pieces.Where(p => p.Kind != StairPieceKind.Support))
            Assert.True(Vector2.Distance(new Vector2(piece.Position.x, piece.Position.z), floor) >= 7f, $"{piece.Kind} at {piece.Position} in the entrance");
        // It ends facing into the cave (towards -x), within the alignment the layout allows.
        float off = Mathf.Abs(((plan.EndHeading - 270f) % 360f + 540f) % 360f - 180f);
        Assert.True(off <= StairLayout.AlignSteps * StairLayout.HeadingStep + 0.01f, $"ends facing {plan.EndHeading}");
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
        // It comes to the cave's front: its place end is in front of the floor.
        Vector2 placeEnd = plan.Reversed ? new Vector2(span.FromPoint.x, span.FromPoint.y) : new Vector2(plan.End.x, plan.End.z);
        Vector2 caveEnd = plan.Reversed ? new Vector2(span.ToPoint.x, span.ToPoint.y) : placeEnd;
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
        // 100 m east of this class's place at the origin, clear of its footprint.
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

        Assert.Equal("on another staircase", RoadStairRuns.SiteConflict(on, centre, road, road.Count - 1, Slope, _world, new[] { laid }));
        Assert.Equal("", RoadStairRuns.SiteConflict(on, centre, road, road.Count - 1, Slope, _world, new[] { laid }, meet: roadEnd));
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
        var request = new SiteApproachRequest
        {
            RoadEnd = end, RoadHeight = height, Arriving = StairLayout.Bearing(before, end),
            Centre = new Vector2(0f, 0f), RoadTail = new List<Vector2> { before, end },
        };
        // The road's staircase climbed west (toward the cave) to its top row's far edge at the road's end.
        var row = new StairContinuation(end, 270f, height, StairPieceKind.Stair);

        RoadStairSpan? span = RoadStairRuns.PlanPlacedSiteApproach(request, 90f, CaveFloor, CaveGround, (x, z) => Heightmap.Biome.Mountain, 77,
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
        var request = new SiteApproachRequest
        {
            RoadEnd = end, RoadHeight = height, Arriving = StairLayout.Bearing(before, end),
            Centre = new Vector2(0f, 0f), RoadTail = new List<Vector2> { before, end },
        };
        // The road's staircase climbed west, toward the cave, to its top row at the road's end.
        var row = new StairContinuation(end, 270f, height, StairPieceKind.Stair);

        RoadStairSpan? span = RoadStairRuns.PlanPlacedSiteApproach(request, 90f, CaveFloor, Ground, (x, z) => Heightmap.Biome.Mountain, 77,
            out string why, onStairs: row);

        Assert.True(span != null, why);
        foreach (StairPiece piece in span!.Plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair))
            Assert.True(piece.Position.y >= height - 0.01f, $"{piece.Kind} at {piece.Position} under the row at {height:F2}");
    }

    /// <summary>A cave not placed in time gets no approach (nothing is laid later; a cave left for a player to find is never
    /// a destination at all, <see cref="PlacedSites.PlayerDecides"/>): its road's end at the ring, 12 m under its mouth, is
    /// not level with it, so the road is refused and the cave left unreached (a road always ends
    /// level, even at the cost of a POI). Before, the ring end was kept and counted the cave reached.</summary>
    [Fact]
    public void ACaveNotPlacedInTimeIsNotReachedFromItsRing()
    {
        LocationLevelling.Source = c => c.magnitude < 0.5f ? CaveFloor : null;
        LocationLevelling.FacesDownhillSource = c => c.magnitude < 0.5f;
        try
        {
            RoadNetworkGenerator.Reset();
            RoadSpatialGrid.Clear();
            PlacedSites.Reset();
            RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(new Vector2(0f, 0f), Footprint) });
            SetPathfinder(new RoadPathfinder(_world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });

            Assert.False(RoadNetworkGenerator.GenerateRoad(new Vector2(-120f, 0f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the cave"));
            Assert.Equal(RoadNetworkGenerator.NotLevelRefusal, RoadSpatialGrid.LastRefusal);
            Assert.Empty(StairStructures.Runs);
            Assert.False(RoadNetworkGenerator.Reached(new Vector3(0f, 0f, 0f), 10f));
        }
        finally { LocationLevelling.FacesDownhillSource = null; PlacedSites.Reset(); }
    }

    /// <summary>A place a staircase reaches is reached, with no road near it: the network counts its
    /// staircases (MountainCave02 -2560,1536: its staircase's top 38.8 m out, the nearest road
    /// 52 m, counted unreached).</summary>
    [Fact]
    public void APlaceAStaircaseReachesIsServedByTheNetwork()
    {
        float Slope(float x, float z) => 40f + 0.5f * (x + 60f);
        StairPlan plan = StairLayout.Build(new Vector2(-60f, 0f), new Vector2(-30f, 0f), Slope);
        Assert.True(plan.Reached, plan.Reason);
        Assert.False(RoadNetworkGenerator.Reached(new Vector3(0f, 0f, 0f), 10f));

        StairStructures.Add(StairRun.FromPlan(new Vector2(-60f, 0f), new Vector2(-30f, 0f), 2f, plan));

        Assert.True(RoadNetworkGenerator.Reached(new Vector3(0f, 0f, 0f), 10f));
    }

    /// <summary>A road's end not level with its place (<see cref="RoadNetworkGenerator.ArrivalLevel"/>), over or under it,
    /// does not reach the place unless an approach staircase was laid from it onto the place (a road does not end
    /// below or above these places). Before, only an end more than 20 m under was refused: a 100 m staircase ended
    /// 45 m under Hildir's cave.</summary>
    [Fact]
    public void ARoadEndNotLevelWithItsPlaceDoesNotReachItWithoutAnApproach()
    {
        LocationLevelling.Source = c => c.magnitude < 0.5f ? Apron : null;
        var endNotLevel = typeof(RoadNetworkGenerator).GetMethod("EndNotLevel", BindingFlags.NonPublic | BindingFlags.Static)!;
        var path = new List<Vector2>();
        for (int x = -120; x <= -24; x += 8) path.Add(new Vector2(x, 0f));
        RoadSpatialGrid.PlannedPath plan = RoadSpatialGrid.PlanRoadPath(path, 4f, _world)!;
        Assert.NotNull(plan);
        string? Why(List<RoadStairSpan> stairs, List<RoadStairSpan>? approaches = null) => (string?)endNotLevel.Invoke(null, new object[] {
            path, new List<RoadSpatialGrid.PlannedPath> { plan }, stairs, approaches ?? new List<RoadStairSpan>(), path[0], 0f, new Vector2(0f, 0f), 10f, false, true });
        RoadStairSpan EndsOn(float height) => new() { FromIndex = path.Count - 3, ToIndex = path.Count - 1, FromPoint = path[path.Count - 3],
            FromHeight = 5f, ToPoint = path[path.Count - 1], ToHeight = height };
        // The platform is at 52 m; the road ends on the ground at 40 m, 12 m under it (20 m was allowed before).
        Assert.Contains("ends 12.0 m under its place", Why(new List<RoadStairSpan>()));
        // An approach staircase laid from that end onto the place: reached by it.
        Assert.Null(Why(new List<RoadStairSpan>(), new List<RoadStairSpan> { new() { FromIndex = path.Count - 1, ToIndex = path.Count - 1 } }));
        // The road ends on a staircase's top: level with the platform, a step over it, 45 m under it.
        Assert.Null(Why(new List<RoadStairSpan> { EndsOn(52f + RoadNetworkGenerator.ArrivalLevel) }));
        Assert.Contains("ends 1.0 m over its place", Why(new List<RoadStairSpan> { EndsOn(53f) }));
        Assert.Contains("ends 45.0 m under its place", Why(new List<RoadStairSpan> { EndsOn(7f) }));
        // Outside the Mountains an end off level is kept as it is.
        _world.Biome = Heightmap.Biome.Swamp;
        Assert.Null(Why(new List<RoadStairSpan>()));
        Assert.Null(Why(new List<RoadStairSpan> { EndsOn(7f) }));
    }

    /// <summary>A road whose end stands under its place and from which no approach can be laid is refused, and the place
    /// left unreached: there is no last-resort road for it any more (it was built ending under its place, and the place
    /// counted reached by it).</summary>
    [Fact]
    public void ARoadEndingUnderItsPlaceWithNoApproachIsRefused()
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

    /// <summary>Outside the Mountains (<see cref="RoadNetworkGenerator.ApproachStairsAt"/>) a road that comes close to its place is
    /// enough: one ending well under its platform, with no approach staircase, is kept, not refused, and the place is reached
    /// by it.</summary>
    [Fact]
    public void ARoadEndingUnderItsPlaceOutsideTheMountainsIsKeptWithNoApproach()
    {
        _world.Biome = Heightmap.Biome.Meadows;
        IReadOnlyList<LevelOp> raised = new[] { new LevelOp(0f, 0f, 15f, LevelRadius, false) };
        LocationLevelling.Source = c => c.magnitude < 0.5f ? raised : null;
        RoadNetworkGenerator.Reset();
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(new Vector2(0f, 0f), Footprint) });
        SetPathfinder(new RoadPathfinder(_world) { Stairs = false, TerrainVarianceThreshold = 1000f, Meander = 0f });

        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-120f, 0f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the apron"),
            "last refusal: " + (RoadSpatialGrid.LastRefusal ?? "none"));

        Assert.Empty(StairStructures.Runs);
        Assert.True(RoadNetworkGenerator.Reached(new Vector3(0f, 0f, 0f), 10f));
    }

    /// <summary>An approach staircase is laid onto a place in the Mountains only: the same road to the same apron climbs its
    /// last stretch on stairs there, and ends at the foot of the face elsewhere, reaching the place.</summary>
    [Fact]
    public void AnApproachStaircaseIsLaidOnlyAtAPlaceInTheMountains()
    {
        LocationLevelling.Source = c => c.magnitude < 0.5f ? Apron : null;
        float? Top(Heightmap.Biome biome)
        {
            _world.Biome = biome;
            RoadNetworkGenerator.Reset();
            RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(new Vector2(0f, 0f), Footprint) });
            SetPathfinder(new RoadPathfinder(_world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-120f, 0f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the apron"),
                $"{biome}: last refusal: " + (RoadSpatialGrid.LastRefusal ?? "none"));
            Assert.True(RoadNetworkGenerator.Reached(new Vector3(0f, 0f, 0f), 10f), $"{biome}: not reached");
            return StairStructures.Runs.Count == 0 ? null
                : StairStructures.Runs.SelectMany(r => r.Pieces).Where(p => p.Kind != StructurePieceKind.StairSupport).Max(p => p.Position.y);
        }

        Assert.InRange(Top(Heightmap.Biome.Mountain) ?? 0f, 51f, 52.5f);
        Assert.Null(Top(Heightmap.Biome.Meadows));
    }

    /// <summary>Only network level with a place (<see cref="RoadNetworkGenerator.ArrivalLevel"/>) within its reach serves it:
    /// a road 25 m under its platform does not (it did with no height guard), nor one a metre under or 8 m over it; one level
    /// with it does.</summary>
    [Fact]
    public void OnlyNetworkLevelWithAPlaceServesIt()
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
        RoadSpatialGrid.Commit(Road(4f, 51f));
        Assert.False(RoadNetworkGenerator.Reached(new Vector3(0f, 0f, 0f), 10f));
        RoadSpatialGrid.Commit(Road(-4f, 60f));
        Assert.False(RoadNetworkGenerator.Reached(new Vector3(0f, 0f, 0f), 10f));
        RoadSpatialGrid.Commit(Road(6f, 52f + 0.4f));
        Assert.True(RoadNetworkGenerator.Reached(new Vector3(0f, 0f, 0f), 10f));
    }
    /// <summary>The stub drop case: a road 25 m under a place's platform, within reach of it, does not
    /// reach it, so a new road to the place keeps its own stub where it merges with that low road (the one reached
    /// test, <see cref="RoadNetworkGenerator.Reached"/>). Before, the stub drop had its own test with no height guard:
    /// it cut the stub, the place was left with no access and then counted unreached.</summary>
    [Fact]
    public void ARoadUnderAPlaceDoesNotDropTheStubOfTheRoadToIt()
    {
        LocationLevelling.Source = c => c.magnitude < 0.5f ? Apron : null;
        RoadSpatialGrid.PlannedPath low = RoadSpatialGrid.PlanRoadPath(new List<Vector2> { new(-40f, 0f), new(-26f, 0f) }, 4f, _world)!;
        for (int i = 0; i < low.Heights.Count; i++) low.Heights[i] = 27f;
        RoadSpatialGrid.Commit(low);
        // A road to the place along z = 0, over the low road 7 m before its end at the place's edge.
        var path = new List<Vector2> { new(-120f, 0f), new(-90f, 0f), new(-60f, 0f), new(-33f, 0f), new(-20f, 0f) };
        float snap = RoadNetworkGenerator.RoadSnap;
        try
        {
            RoadNetworkGenerator.RoadSnap = 6f;
            Assert.False(RoadNetworkGenerator.EndReached(new Vector2(0f, 0f), 10f));
            Assert.Equal(path.Count, RoadNetworkGenerator.DropStubs(path, true, RoadNetworkGenerator.EndReached(new Vector2(0f, 0f), 10f))!.Count);
            // Control: a road level with the place within its reach reaches it, and the stub is dropped.
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
}
