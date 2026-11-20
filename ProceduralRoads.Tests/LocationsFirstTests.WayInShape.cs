using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A way onto a placed Location's landing enters its footprint (RoadLocationProtection.Targets.Into), so the pull and the sway do not
/// keep it clear of that Location; its shape judges the road as shaped only after it is planned (RoadLocationEntry.PlanTo). A shaped road the shape refuses is
/// planned again on its plain line (unswayed) before the way in is refused. Before, a sway that moved a ring chord point inside the
/// Location's keep refused the whole way in, though the same line unswayed passed.
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>The shaped road through one of a crypt's stones, the plain one clear: the plain one is the way in. Refused unswayed
    /// too, the way in is refused for the shape.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AWayInWhoseShapedRoadTheLocationRefusesIsPlannedOnItsPlainLine(bool plainPasses)
    {
        PlacedLocationShape shape = Crypt3Stones.Shape(Crypt3Stones.Rocks());
        Vector2 landing = shape.Door!.Value, from = landing + shape.Front * 25f;
        LocationRock stone = shape.Body.First(r => r.Name == "Stone (19)");
        float level = Crypt3Stones.Ground(landing.x, landing.y);
        RoadSpatialGrid.PlannedPath Piece(params Vector2[] points)
        {
            var dense = new List<Vector2>();
            for (int k = 1; k < points.Length; k++)
            {
                int n = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(points[k - 1], points[k])));
                for (int c = k == 1 ? 0 : 1; c <= n; c++) dense.Add(Vector2.Lerp(points[k - 1], points[k], (float)c / n));
            }
            return new RoadSpatialGrid.PlannedPath(dense, dense.ConvertAll(_ => level), dense.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, dense.Count - 1, false);
        }
        var swayedWith = new List<bool>();
        RoadNetworkGenerator.WayInPlan? Planner(List<Vector2> line, float height, out string why)
        {
            why = "";
            bool plain = RoadSpatialGrid.SuppressWiggle;
            swayedWith.Add(!plain);
            Vector2 end = line[line.Count - 1];
            RoadSpatialGrid.PlannedPath entry = Piece(line.ToArray());
            // Swayed, the road before the way in swings through a stone inside the lead-round ring.
            RoadSpatialGrid.PlannedPath road = plain && plainPasses ? Piece(from + shape.Front * 30f, from) : Piece(from + shape.Front * 30f, stone.At, from);
            return new RoadNetworkGenerator.WayInPlan
            {
                Entry = entry, Plans = new List<RoadSpatialGrid.PlannedPath> { road }, Path = new List<Vector2> { from + shape.Front * 30f, from },
                Crossings = new List<RoadCrossing>(),
            };
        }
        var request = new LocationApproachRequest { RoadEnd = from, RoadHeight = level, Centre = Crypt3Stones.Centre, RoadTail = new List<Vector2> { from }, Reach = 64f };

        RoadSpatialGrid.PlannedPath? wayIn = RoadLocationEntry.Plan(request, shape, 4f, Crypt3Stones.Ground, out string refused, planner: Planner);

        Assert.Equal(new[] { true, false }, swayedWith);
        Assert.False(RoadSpatialGrid.SuppressWiggle);
        if (plainPasses) Assert.True(wayIn != null, refused);
        else
        {
            Assert.Null(wayIn);
            Assert.Contains("Stone (19)", refused);
        }
    }
}
