using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// One treatment for a road's water (rule 13): a way onto a placed Location's landing treats its pool through generation's own loop
/// (RoadNetworkGenerator.EachTreatment over DetectCrossings and MeetEnds, as PlanCandidate's PlanTreatments), never a copy of its
/// own. Before, the way in detected, met and planned once: a pool whose first treatment could not be planned was refused instead of
/// treated as the next style, and the search's viaduct jumps were not found on it.
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>The ground of a crypt on dry dirt with one pool across its approach: the dirt out to 28 m, the swamp's shallows from 32 to
    /// 38 m, dry again from 42 m.</summary>
    private static float PoolBeforeTheCrypt(float x, float z)
    {
        float r = new Vector2(x, z).magnitude;
        return r < 35f ? Mathf.Lerp(CryptDirt, SwampShallows, Mathf.Clamp01((r - 28f) / 4f)) : Mathf.Lerp(SwampShallows, CryptDirt, Mathf.Clamp01((r - 38f) / 4f));
    }

    /// <summary>One pool, the same line across one pool of a swamp's shallows toward a crypt's landing: planned as a way in (PlanWayIn, its road the
    /// end alone) and as a road's candidate (PlanCandidate, the road's own treatment loop), the same crossings with the same
    /// treatments. The road stops outside the crypt's footprint, as a road does; its line is not shaped before its crossings here (the way in's line is its route, never swayed).</summary>
    [Fact]
    public void APoolOnAWayInAndOnARoadGetsTheSameTreatment()
    {
        PlacedLocation crypt = SunkenCryptInASwamp(PoolBeforeTheCrypt);
        var finder = (RoadPathfinder)typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Vector2 roadEnd = new(0f, -50f);
        PlacedLocationShape door = crypt.Entrances[0];
        var walker = PlacedLocationShape.Walker.Road(RoadNetworkGenerator.RoadWidth);
        // One line for both paths, ending 2 m in front of the landing (as the way in's end stood before it ended on the landing
        // itself): the parity asked is of the two planners given the same line. Ended on the landing, the way in finds the pool's
        // banks a metre wider and wades it where the road raises it (an open finding of the levelled-area experiment).
        Vector2 end = door.Landing + door.Front * door.DoorClearance;
        List<Vector2> line = RoadLocationEntry.Route(roadEnd, door, end, walker);
        float landing = crypt.Ground(end.x, end.y);
        var request = new LocationApproachRequest { RoadEnd = roadEnd, RoadHeight = CryptDirt, Centre = crypt.Centre, RoadTail = new List<Vector2> { roadEnd }, Reach = 64f };

        RoadSpatialGrid.PlannedPath? wayIn = RoadNetworkGenerator.PlanWayIn(RoadNetworkGenerator.WayInRoad.Alone(request), line, landing, crypt.Centre,
            RoadNetworkGenerator.RoadWidth, out string why, out RoadNetworkGenerator.WayInPlan? planned);
        Assert.True(wayIn != null && planned != null, why);

        var dense = new List<Vector2> { roadEnd };
        for (int k = 1; k < line.Count; k++)
        {
            int cells = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(line[k - 1], line[k]) / RoadPathfinder.CellSize));
            for (int c = 1; c <= cells; c++) dense.Add(Vector2.Lerp(line[k - 1], line[k], c / (float)cells));
        }
        // The road stops outside the crypt's footprint: only a way in enters it (RoadLocationProtection.Targets.Into).
        dense.RemoveAll(p => p.magnitude < Footprint + RoadNetworkGenerator.RoadWidth);
        bool was = RoadSpatialGrid.SuppressWiggle;
        RoadSpatialGrid.SuppressWiggle = true;
        RoadNetworkGenerator.RoadPlan asRoad;
        try
        {
            asRoad = RoadNetworkGenerator.PlanCandidate(finder, dense, new Vector2(0f, 0f), 0f, crypt.Centre, 0f, false, false, false, false, RoadNetworkGenerator.RoadWidth);
        }
        finally { RoadSpatialGrid.SuppressWiggle = was; }

        Assert.NotEmpty(planned!.Crossings);
        Assert.Equal(asRoad.Crossings.Count, planned.Crossings.Count);
        for (int i = 0; i < asRoad.Crossings.Count; i++)
        {
            RoadCrossing road = asRoad.Crossings[i], way = planned.Crossings[i];
            Assert.Equal(road.Kind, way.Kind);
            Assert.Equal(road.Style, way.Style);
            Assert.Equal(road.Shallow, way.Shallow);
            Assert.True(Vector2.Distance(road.FromBank, way.FromBank) < 0.01f && Vector2.Distance(road.ToBank, way.ToBank) < 0.01f,
                FormattableString.Invariant($"crossing {i}: the road's ({road.FromBank.x:F1}, {road.FromBank.y:F1}) -> ({road.ToBank.x:F1}, {road.ToBank.y:F1}), the way in's ({way.FromBank.x:F1}, {way.FromBank.y:F1}) -> ({way.ToBank.x:F1}, {way.ToBank.y:F1})"));
        }
    }

    /// <summary>The one treatment loop: a shallow ford whose treatment failed is refused at its site for its style and the attempt
    /// made again with it refused, up to CrossingRetries more times; an attempt that plans, or fails for no ford's treatment, ends it.</summary>
    [Fact]
    public void TheTreatmentLoopRefusesAFailedPoolsStyleAndTriesTheNext()
    {
        var site = new Vector2(3f, 4f);
        var asked = new List<List<(Vector2 site, FordStyle style)>>();
        FordStyle[] styles = { FordStyle.Raise, FordStyle.Span, FordStyle.Wade };
        RoadNetworkGenerator.EachTreatment((refused, failed) =>
        {
            asked.Add(new List<(Vector2, FordStyle)>(refused));
            if (asked.Count > styles.Length) return false;
            failed.Add(new RoadCrossing { Kind = CrossingKind.Ford, Style = styles[asked.Count - 1], Shallow = true, ShallowSite = site });
            return true;
        });
        Assert.Equal(RoadNetworkGenerator.CrossingRetries + 1, asked.Count);
        Assert.Empty(asked[0]);
        Assert.Equal(new[] { (site, FordStyle.Raise) }, asked[1]);
        Assert.Equal(new[] { (site, FordStyle.Raise), (site, FordStyle.Span) }, asked[2]);

        // Planned at once, or failed for no ford: one attempt.
        int once = 0;
        RoadNetworkGenerator.EachTreatment((_, _) => { once++; return false; });
        RoadNetworkGenerator.EachTreatment((_, failed) => { once++; failed.Add(new RoadCrossing { Kind = CrossingKind.Bridge }); return true; });
        Assert.Equal(2, once);
    }
}
