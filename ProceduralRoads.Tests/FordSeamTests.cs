using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// One verdict on a ford's end seam (RoadNetworkGenerator.FordSeamMeets over PiecesMeet): the seams one plan makes (PlanPieces) and
/// the seam where a road kept to its last structure meets the stretch planned again from it (PlanFromItsLastStructure), and the
/// joint a road and its way in share (WalkStep). Offline, the way in's re-plan from a road's last structure stored a ford's end a
/// step no one walks up from the road kept before it: 0.75 m at a raised ford's end (2384, -1896), the road at 30.50 and the ford
/// at 31.25, and 0.80 m at a ford's end (-4521.5, 1454.5), 30.50 and 29.7; and, the road's end on its wade carried on at dry land's floor to a Location's edge (RoadNetworkGenerator.CarryOnToEdges), 0.61 m at (-4536.0, 1460.5), 30.50 and 29.89; and a 6.06 m step at a joint (5541.5, 3069.0), 31.33
/// and 37.39, that no grade judge read: each piece's own judge saw only its own heights there.
/// </summary>
public class FordSeamTests
{
    private static RoadSpatialGrid.PlannedPath Piece(Vector2 from, Vector2 to, float fromHeight, float toHeight, RoadCrossing? ford = null)
    {
        int n = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(from, to)));
        var points = Enumerable.Range(0, n + 1).Select(k => Vector2.Lerp(from, to, (float)k / n)).ToList();
        var heights = Enumerable.Range(0, n + 1).Select(k => Mathf.Lerp(fromHeight, toHeight, (float)k / n)).ToList();
        return new RoadSpatialGrid.PlannedPath(points, heights, points.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, n, false)
        {
            CrossingAtStart = ford, CrossingAtEnd = ford,
        };
    }

    [Theory]
    [InlineData(2384f, -1896f, 30.50f, 31.25f, FordStyle.Raise)]
    [InlineData(-4521.5f, 1454.5f, 30.50f, 29.70f, FordStyle.Wade)]
    [InlineData(-4536.0f, 1460.5f, 30.50f, 29.89f, FordStyle.Wade)]
    public void AFordsEndMoreThanAStepFromTheRoadKeptBeforeItDoesNotMeetIt(float x, float z, float road, float ford, FordStyle style)
    {
        var end = new Vector2(x, z);
        var crossing = new RoadCrossing { Kind = CrossingKind.Ford, Style = style, Shallow = true };
        RoadSpatialGrid.PlannedPath kept = Piece(end - new Vector2(12f, 0f), end, road, road);
        RoadSpatialGrid.PlannedPath again = Piece(end, end + new Vector2(10f, 0f), ford, ford, crossing);

        Assert.False(RoadNetworkGenerator.FordSeamMeets(kept, again, out float rise));
        Assert.Equal(Mathf.Abs(ford - road), rise, 3);
        // Either way round, as at a road's start.
        Assert.False(RoadNetworkGenerator.FordSeamMeets(Piece(end + new Vector2(10f, 0f), end, ford, ford, crossing), Piece(end, end - new Vector2(12f, 0f), road, road), out _));
        // Within a step, it meets; past a span the structure meets the road.
        Assert.True(RoadNetworkGenerator.FordSeamMeets(kept, Piece(end, end + new Vector2(10f, 0f), road + 0.15f, road + 0.15f, crossing), out _));
        var span = new RoadCrossing { Kind = CrossingKind.Ford, Style = FordStyle.Span };
        Assert.True(RoadNetworkGenerator.FordSeamMeets(kept, Piece(end, end + new Vector2(10f, 0f), ford, ford, span), out _));
    }

    /// <summary>Two pieces sharing their joint (a road and its way in planned as one, split there) whose heights there are 6.06 m
    /// apart: the walk step refuses it at the joint. Before, the joint's second point was dropped as the same piece's and no judge
    /// read the two heights together. Within a step, the joint is walked.</summary>
    [Fact]
    public void AJointTwoPiecesShareMoreThanAStepApartIsAStepNoOneWalksUp()
    {
        var joint = new Vector2(5541.5f, 3069.0f);
        RoadSpatialGrid.PlannedPath a = Piece(joint - new Vector2(0f, 10f), joint, 31.33f, 31.33f);
        RoadSpatialGrid.PlannedPath b = Piece(joint, joint + new Vector2(0f, 10f), 37.39f, 37.39f);

        string? step = RoadNetworkGenerator.WalkStep(new List<RoadSpatialGrid.PlannedPath> { a, b }, out Vector2 at);

        Assert.NotNull(step);
        Assert.Equal(joint, at);
        Assert.Null(RoadNetworkGenerator.WalkStep(new List<RoadSpatialGrid.PlannedPath> { a, Piece(joint, joint + new Vector2(0f, 10f), 31.43f, 31.43f) }, out _));
    }
}
