using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

public partial class SitesFirstTests
{
    /// <summary>Cutting a road back for its way onto a placed place's landing moves everything indexed along it together:
    /// its crossings as its staircases (they still name the same waypoints), so no network hint lands on a bridge or ford
    /// waypoint. A crossing in the part cut away refuses the cut, as a staircase does.</summary>
    [Fact]
    public void CuttingARoadBackMovesItsCrossingsWithItsStaircases()
    {
        (List<Vector2> path, RoadSpatialGrid.PlannedPath piece, List<RoadStairSpan> stairs, List<RoadCrossing> crossings) Road(int crossingFrom)
        {
            var path = new List<Vector2>();
            for (int i = 0; i < 20; i++) path.Add(new Vector2(i * 8f, 0f));
            var dense = new List<Vector2>();
            for (float x = 0f; x <= 96.01f; x += 1f) dense.Add(new Vector2(x, 0f));
            var piece = new RoadSpatialGrid.PlannedPath(dense, dense.ConvertAll(_ => 50f), dense.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, dense.Count - 1, false);
            var stairs = new List<RoadStairSpan> { new RoadStairSpan { FromIndex = 16, ToIndex = 17 } };
            var crossings = new List<RoadCrossing> { new RoadCrossing { FromIndex = crossingFrom, ToIndex = crossingFrom + 2 } };
            return (path, piece, stairs, crossings);
        }
        var (path, piece, stairs, crossings) = Road(12);
        var plans = new List<RoadSpatialGrid.PlannedPath> { piece };
        Vector2 crossingStart = path[crossings[0].FromIndex], crossingEnd = path[crossings[0].ToIndex], stairStart = path[stairs[0].FromIndex];
        Assert.True(RoadNetworkGenerator.CutBack(true, 12f, path, piece, stairs, crossings, out var cutPath, out var cutPiece, out int shift));
        Assert.True(shift > 0);
        RoadNetworkGenerator.ApplyCutBack(true, path, cutPath, plans, cutPiece, stairs, crossings, shift);
        Assert.Equal(crossingStart, path[crossings[0].FromIndex]);
        Assert.Equal(crossingEnd, path[crossings[0].ToIndex]);
        Assert.Equal(stairStart, path[stairs[0].FromIndex]);
        // A crossing at the road's start, in the part a cut would take away: no cut.
        var (path2, piece2, stairs2, crossings2) = Road(1);
        Assert.False(RoadNetworkGenerator.CutBack(true, 12f, path2, piece2, stairs2, crossings2, out _, out _, out _));
    }
}
