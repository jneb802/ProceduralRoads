using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

public partial class SitesFirstTests
{
    /// <summary>The level ground falling away east of the cave's landing, 1 in 5 from x 12 to x 72, level again beyond.</summary>
    private sealed class SlopeBelowTheLanding : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f ? 0f : wx <= 12f ? 50f : wx <= 72f ? 50f - (wx - 12f) * 0.2f : 38f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    /// <summary>The road from the east to the cave on the slope, generated afresh: whether built, and its arrival (its points
    /// within the reach of a way onto a placed landing, and its walking surface there), nearest the place last.</summary>
    private (bool built, List<Vector3> arrival) RoadFromTheEast(SlopeBelowTheLanding slope, StairRun? approach, List<Vector2>? approachRoad, bool fromThePlace)
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        StairStructures.Reset();
        PlacedSites.Reset();
        // The cave's footprint, which the reset cleared: an approach's place is the footprint its top stands in (PlaceOf).
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(new Vector2(0f, 0f), Footprint) });
        PlacedSite cave = Cave();
        Func<float, float, float> ground = PlacedSiteGround.Sampler(cave.Record.Ground((x, z) => slope.GetHeight(x, z)), cave.Record.X0, cave.Record.Z0, cave.Record.W, cave.Record.H);
        PlacedSites.Ready(new PlacedSite { Record = cave.Record, Ground = ground, Shape = PlacedSiteShape.Of(cave.Centre, 90f, CaveFloor[0], 0f, null, ground, false, cave.Record.Root) });
        LocationLevelling.Source = c => c.magnitude < 0.5f ? CaveFloor : null;
        SetPathfinder(new RoadPathfinder(slope) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        if (approachRoad != null)
            RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(approachRoad, approachRoad.ConvertAll(p => slope.GetHeight(p.x, p.y)),
                approachRoad.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, Vector2.Distance(approachRoad[0], approachRoad[approachRoad.Count - 1]), true));
        // Laid as an earlier road laid it (the network may keep a trimmed copy of it: told by its pieces below).
        if (approach != null) StairStructures.Add(approach);
        bool built = fromThePlace
            ? RoadNetworkGenerator.GenerateRoad(new Vector2(0f, 0f), 10f, new Vector2(150f, 0f), 0f, 4f, "from the cave")
            : RoadNetworkGenerator.GenerateRoad(new Vector2(150f, 0f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the cave");
        var arrival = RoadSpatialGrid.GetRoadPointsNearPosition(Vector3.zero, RoadNetworkGenerator.PlacedSiteReach)
            .Where(q => q.IsRoad && q.p.magnitude <= RoadNetworkGenerator.PlacedSiteReach && (approachRoad == null || !approachRoad.Exists(a => Vector2.Distance(a, q.p) < 1.5f)))
            .Select(q => new Vector3(q.p.x, q.h, q.p.y))
            .Concat(StairStructures.Runs.Where(r => approach == null || !r.Pieces.Exists(p => approach.Pieces.Exists(q => Vector3.Distance(q.Position, p.Position) < 0.1f))).SelectMany(r => r.Pieces)
                .Where(p => p.Kind is StructurePieceKind.StairStep or StructurePieceKind.StairFloor)
                .Select(p => new Vector3(p.Position.x, RoadStairRuns.Surface(p), p.Position.z)))
            .OrderByDescending(p => new Vector2(p.x, p.z).magnitude).ToList();
        return (built, arrival);
    }

    /// <summary>A place has one way in up a face: a road whose way onto a placed place's landing would climb beside the approach
    /// another road laid up that face (the corridor rule, RoadStairRuns.ArrivalBesideAnApproach) is ended on that approach's
    /// road instead, at a junction where the approach's road meets it (RoadNetworkGenerator.ReEndOnAWayIn), and lays no way
    /// in of its own. The approach here is laid 8 m beside the way the road comes in on its own, at its heights, up the
    /// same slope, its own road leaving its foot to the south; or 15 m to its side, past the corridor's reach (a layout
    /// refused in the corridor laid just outside it is beside the first all the same). Two ways in going opposite ways are
    /// allowed: the same approach on the far side of the place, climbing to it the other way, leaves the road its own way in.</summary>
    /// <remarks>Either way along: the road set off from the place (the planner builds a place's road from a place already
    /// reached) or arriving at it.</remarks>
    [Theory]
    [InlineData(false, 8f, false)]
    [InlineData(true, 8f, false)]
    [InlineData(true, 15f, false)]
    [InlineData(true, 8f, true)]
    [InlineData(false, 8f, true)]
    public void ARoadUpTheFaceAnApproachClimbsEndsOnThatApproachsRoad(bool fromThePlace, float across, bool farSide)
    {
        var slope = new SlopeBelowTheLanding();
        _scope.WithWorld(slope);
        using var corridor = StaticOverride.Keep(() => RoadNetworkGenerator.RoadSnap).AndKeep(() => RoadNetworkGenerator.CorridorSnap);
        RoadNetworkGenerator.RoadSnap = 6f;
        RoadNetworkGenerator.CorridorSnap = 14f;

        var (alone, own) = RoadFromTheEast(slope, null, null, fromThePlace);
        Assert.True(alone, "on its own: " + RoadSpatialGrid.LastRefusal);
        Assert.True(own.Count >= 8, $"on its own it comes in on {own.Count} points");
        float rise = own.Max(p => p.y) - own.Min(p => p.y);
        Assert.True(rise >= 2f, $"on its own its arrival climbs {rise:F1} m");

        // The approach: a stair every 2 m along the road's own arrival, 8 m to its side, at its heights.
        var plan = new StairPlan { Kit = "stone", Reached = true };
        Vector2 last = new Vector2(float.MaxValue, 0f);
        foreach (Vector3 p in own)
        {
            var at = farSide ? new Vector2(-p.x, -p.z - across) : new Vector2(p.x, p.z + across); // the far side: through the place, 180 degrees round (a mirror leaves it 87 degrees off the road's diagonal arrival, under PlaceWaysIn.SameSideAngle)
            if (Vector2.Distance(at, last) < StairLayout.PieceRun) continue;
            last = at;
            plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Stair, Prefab = "stone_stair", Position = new Vector3(at.x, p.y - StairLayout.PieceRise / 2f, at.y) });
        }
        StairPiece footPiece = plan.Pieces.OrderBy(p => p.Position.y).First(), topPiece = plan.Pieces.OrderBy(p => p.Position.y).Last();
        Vector2 foot = new(footPiece.Position.x, footPiece.Position.z), top = new(topPiece.Position.x, topPiece.Position.z);
        plan.End = topPiece.Position;
        var approach = StairRun.FromPlan(foot, top, RoadStairRuns.Width, plan, StairKit.Stone);
        approach.KeepsSiteRocks = true;
        var itsRoad = new List<Vector2>();
        for (float d = 0f; d <= 40f; d += 1f) itsRoad.Add(foot + new Vector2(0f, d));

        var (built, arrival) = RoadFromTheEast(slope, approach, itsRoad, fromThePlace);

        Assert.True(built, "beside the approach: " + RoadSpatialGrid.LastRefusal);
        if (farSide)
        {
            // Its own way in, as on its own: up the slope onto the landing, nowhere near the other approach's foot.
            Assert.Contains(arrival, p => p.x > 0f && p.x < 20f);
            Assert.DoesNotContain(RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(foot.x, 0f, foot.y), 6f), q => q.IsRoad && Vector2.Distance(q.p, foot) <= 3f
                && !itsRoad.Exists(a => Vector2.Distance(a, q.p) < 1.5f));
            return;
        }
        // One way in up a face: none of the road's own ways into the cave (by staircase or as road) arrives the way the
        // approach does.
        PlaceWaysIn.Way first = PlaceWaysIn.Of(approach, new Vector2(0f, 0f))!;
        foreach (PlaceWaysIn.Way w in PlaceWaysIn.There(new Vector2(0f, 0f)))
            if (w.Approach == null || !w.Approach.Pieces.Exists(p => approach.Pieces.Exists(q => Vector3.Distance(q.Position, p.Position) < 0.1f)))
                Assert.False(PlaceWaysIn.SameWay(w, first), $"arrives as the approach does: {w.Outer}->{w.Inner}");
        // Set off from the place the road joins the approach's road at its foot (its door there is the one the approach
        // climbs to); arriving, it may come in at a door up another face instead (a level way in from the north east here).
        if (fromThePlace)
            Assert.Contains(RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(foot.x, 0f, foot.y), 6f), q => q.IsRoad && Vector2.Distance(q.p, foot) <= 3f);
    }

    /// <summary>A road arriving at a place as road is a way into it too (PlaceWaysIn.Record): a second road from the same side,
    /// up the same slope, does not build a second way in beside it; it ends on the first's road (ReEndOnAWayIn) or rides it.
    /// One from the far side keeps its own way in.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASecondRoadUpTheSameFaceJoinsTheFirstRoad(bool farSide)
    {
        var slope = new SlopeBelowTheLanding();
        _scope.WithWorld(slope);
        using var corridor = StaticOverride.Keep(() => RoadNetworkGenerator.RoadSnap).AndKeep(() => RoadNetworkGenerator.CorridorSnap);
        RoadNetworkGenerator.RoadSnap = 6f;
        RoadNetworkGenerator.CorridorSnap = 14f;
        RoadFromTheEast(slope, null, null, fromThePlace: true);
        var first = PlaceWaysIn.There(new Vector2(0f, 0f));
        Assert.True(first.Count == 1, $"{first.Count} ways in after the first road");

        Vector2 from = farSide ? new Vector2(-150f, 30f) : new Vector2(150f, -40f);
        bool built = RoadNetworkGenerator.GenerateRoad(new Vector2(0f, 0f), 10f, from, 0f, 4f, "from the cave again");

        Assert.True(built, "the second road: " + RoadSpatialGrid.LastRefusal);
        var ways = PlaceWaysIn.There(new Vector2(0f, 0f));
        foreach (PlaceWaysIn.Way w in ways)
            if (w != first[0]) Assert.False(PlaceWaysIn.SameWay(w, first[0]), $"a second way in as the first arrives: {w.Outer}->{w.Inner}");
        if (farSide) Assert.True(ways.Count == 2, $"{ways.Count} ways in: the far side's is its own");
    }
}
