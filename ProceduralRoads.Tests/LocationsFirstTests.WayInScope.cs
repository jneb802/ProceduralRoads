using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A way onto a placed Location's landing is judged on its own stretch and joint (RoadNetworkGenerator.PlanWayIn), never vetoed by
/// a structure the road keeps further back. In game a crypt's road was refused "a bridge end the road does not meet" for its own
/// bridge 1 km back: the bridge's far end was met by earthwork over its water, so the road's waypoint at that end stood 0.85 m in
/// from the bank, over the water. The way in's stretch starts at that waypoint, and the detector measured the pool it stood in
/// whole, back across the bridge to its near bank (a wet end measured as at a through crossing): the road's own bridge found again
/// as a new one, its near end off the stretch with no road before it, so never met. A crossing found again that the road keeps
/// (the same site, RoadNetworkGenerator.SameSite) is that crossing, met as the road was planned.
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>Where the lake across the road lies (z), and how far in from its far bank the road's waypoint at the bridge's far end
    /// stands, over the water, as in game (0.85 m).</summary>
    private const float LakeFar = -240f, LakeLength = 60f, FootOverTheWater = 0.85f;

    /// <summary>The crypt's dirt, with a deep lake across the road from <paramref name="far"/> back <see cref="LakeLength"/> m, its
    /// banks falling 2 m to its bed under the water.</summary>
    private static Func<float, float, float> LakeAt(float far) => (x, z) =>
    {
        float near = far - LakeLength;
        if (z <= near || z >= far) return CryptDirt;
        return Mathf.Lerp(CryptDirt, 24f, Mathf.Clamp01(Mathf.Min(z - near, far - z) / 2f));
    };

    /// <summary>The road from <paramref name="length"/> m in front of the crypt to its ring end 22 m out, a cell a waypoint, planned
    /// as generation plans a candidate (PlanCandidate: its crossings found, their ends met, its pieces planned), over the lake with a
    /// bridge, and that bridge's far end as in game: its waypoint moved <see cref="FootOverTheWater"/> in from the far bank, over the
    /// water.</summary>
    private (PlacedLocation crypt, RoadNetworkGenerator.RoadPlan plan, RoadCrossing bridge) RoadOverALake(float length, float far)
    {
        PlacedLocation crypt = SunkenCryptInASwamp(LakeAt(far), Heightmap.Biome.Meadows);
        var finder = (RoadPathfinder)typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        float cell = RoadPathfinder.CellSize;
        int count = Mathf.RoundToInt(length / cell) + 1;
        var path = Enumerable.Range(0, count).Select(k => new Vector2(0f, -22f - cell * (count - 1 - k))).ToList();
        bool was = RoadSpatialGrid.SuppressWiggle;
        RoadSpatialGrid.SuppressWiggle = true;
        RoadNetworkGenerator.RoadPlan plan;
        try { plan = RoadNetworkGenerator.PlanCandidate(finder, path, path[0], 0f, crypt.Centre, 22f, false, true, false, false, RoadNetworkGenerator.RoadWidth); }
        finally { RoadSpatialGrid.SuppressWiggle = was; }
        Assert.True(plan.Plans != null, "the road over the lake was not planned: " + RoadSpatialGrid.LastRefusal);
        RoadCrossing bridge = Assert.Single(plan.Crossings);
        Assert.Equal(CrossingKind.Bridge, bridge.Kind);
        plan.Path[bridge.ToIndex] = bridge.ToBank - bridge.Direction * FootOverTheWater;
        Vector2 foot = plan.Path[bridge.ToIndex];
        Assert.True(RoadWater.Wet(crypt.Ground(foot.x, foot.y)), $"the road's waypoint at the bridge's far end {foot} is not over the water");
        return (crypt, plan, bridge);
    }

    /// <summary>The way in past the road's bridge (its last structure): the bridge found again from its far end's waypoint over the
    /// water is the road's own, kept as the road met it, not met again on the way in's stretch, where no road leads to its near end.
    /// The way in is planned; the road keeps the one bridge, its banks and ends as planned.</summary>
    [Fact]
    public void AWayInPastARoadsBridgeKeepsTheBridgeAsTheRoadMetIt()
    {
        var (crypt, plan, bridge) = RoadOverALake(320f, LakeFar);
        Vector2 fromBank = bridge.FromBank, toBank = bridge.ToBank;
        StructureEnds.End? fromEnd = bridge.FromEnd, toEnd = bridge.ToEnd;
        Assert.True(fromEnd != null && toEnd != null, "the road's bridge has its ends met");
        PlacedLocationShape door = crypt.Entrances[0];
        var walker = PlacedLocationShape.Walker.Road(RoadNetworkGenerator.RoadWidth);
        Vector2 roadEnd = plan.Path[plan.Path.Count - 1];
        Vector2 end = RoadLocationEntry.WayInEnd(door, roadEnd, walker);
        List<Vector2> line = RoadLocationEntry.Route(roadEnd, door, end, walker);
        var road = RoadNetworkGenerator.WayInRoad.Of(false, plan.Path, plan.Plans!, plan.Stairs, plan.Crossings, 0, null);

        RoadSpatialGrid.PlannedPath? wayIn = RoadNetworkGenerator.PlanWayIn(road, line, crypt.Ground(end.x, end.y), crypt.Centre,
            RoadNetworkGenerator.RoadWidth, out string why, out RoadNetworkGenerator.WayInPlan? planned);

        Assert.True(wayIn != null && planned != null, why);
        RoadCrossing kept = Assert.Single(planned!.Crossings);
        Assert.Same(bridge, kept);
        Assert.Equal(fromBank, kept.FromBank);
        Assert.Equal(toBank, kept.ToBank);
        Assert.Equal(fromEnd, kept.FromEnd);
        Assert.Equal(toEnd, kept.ToEnd);
    }

    /// <summary>A crypt's road with its bridge a kilometre back (in game 967 m): the way onto the landing is planned on its own
    /// stretch and joint, and the road is entered by a road onto the landing, not refused for that bridge.</summary>
    [Fact]
    public void AWayInIsNotVetoedByARoadsBridgeAKilometreBack()
    {
        var (crypt, plan, bridge) = RoadOverALake(1100f, -1000f);
        Assert.True(Vector2.Distance(bridge.ToBank, crypt.Centre) > 900f, $"the bridge's far bank {bridge.ToBank} is near the crypt");

        bool entered = RoadNetworkGenerator.PlanEntry(crypt, false, plan.Path, plan.Plans!, plan.Stairs, plan.Crossings, RoadNetworkGenerator.RoadWidth,
            out RoadSpatialGrid.PlannedPath? road, out RoadStairSpan? span, out string why);

        Assert.True(entered && road != null && span == null, $"entered {entered}, staircase {span != null}: {why}");
        Assert.Same(bridge, Assert.Single(plan.Crossings));
    }

    /// <summary>Reject, never protect: the same lake with no road kept over it, the way in starting from a road's end over the water at
    /// its far bank (the very waypoint of the case above). The pool is the way in's own, measured whole as before, and its near end is
    /// one no road meets: the way in is refused for it.</summary>
    [Fact]
    public void AWayInWhoseOwnStretchHasABridgeEndNoRoadMeetsIsStillRefused()
    {
        PlacedLocation crypt = SunkenCryptInASwamp(LakeAt(LakeFar), Heightmap.Biome.Meadows);
        Vector2 roadEnd = new(0f, LakeFar - FootOverTheWater);
        Assert.True(RoadWater.Wet(crypt.Ground(roadEnd.x, roadEnd.y)), $"the road's end {roadEnd} is not over the water");
        PlacedLocationShape door = crypt.Entrances[0];
        var walker = PlacedLocationShape.Walker.Road(RoadNetworkGenerator.RoadWidth);
        Vector2 end = RoadLocationEntry.WayInEnd(door, roadEnd, walker);
        List<Vector2> line = RoadLocationEntry.Route(roadEnd, door, end, walker);
        var request = new LocationApproachRequest { RoadEnd = roadEnd, RoadHeight = CryptDirt, Centre = crypt.Centre, RoadTail = new List<Vector2> { roadEnd }, Reach = 64f };

        RoadSpatialGrid.PlannedPath? wayIn = RoadNetworkGenerator.PlanWayIn(RoadNetworkGenerator.WayInRoad.Alone(request), line, crypt.Ground(end.x, end.y),
            crypt.Centre, RoadNetworkGenerator.RoadWidth, out string why, out _);

        Assert.Null(wayIn);
        Assert.StartsWith(RoadNetworkGenerator.EndNotMet, why);
    }
}
