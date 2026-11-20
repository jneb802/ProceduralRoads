using System;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>Ground rising into a flight (a hump): the staircase turns and follows the hump's contour where it can,
/// and where it does not, levels the ground under the flight (the search's burial price and cuts, StairLayout.Cuts).</summary>
public class StairHumpTests
{
    /// <summary>A slope at the stairs' own grade (1:2) climbing north from (0, 0), a round hump on the straight line at z 20.</summary>
    private static StairPlan Climb(float height, float radius) => StairLayout.Build(new Vector2(0f, 0f), new Vector2(0f, 40f),
        (x, z) => 10f + 0.5f * z + height * (float)Math.Exp(-((x * x) + (z - 20f) * (z - 20f)) / (radius * radius)));

    [Theory]
    [InlineData(2f, 3f)]
    [InlineData(4f, 4f)]
    [InlineData(2f, 12f)]
    public void AHumpInTheWayIsGoneRound(float height, float radius)
    {
        StairPlan plan = Climb(height, radius);
        Assert.True(plan.Reached, plan.Reason);
        var walked = plan.Pieces.Where(p => p.Kind is StairPieceKind.Stair or StairPieceKind.Landing).ToList();
        Assert.True(walked.Max(p => Mathf.Abs(p.Position.x)) > 3f, "it climbed straight over the hump");
        Assert.True(plan.MaxCut <= StairCover.RoadStaircase + StairLayout.CutBelowSurface + 0.05f, $"cuts {plan.MaxCut:F2} m deep");
    }

    [Fact]
    public void ALowHumpIsLevelledUnderTheFlight()
    {
        StairPlan plan = Climb(0.8f, 3f);
        Assert.True(plan.Reached, plan.Reason);
        Assert.All(plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair), p => Assert.Equal(0f, p.Position.x, 2));
        Assert.InRange(plan.MaxCut, 0.5f, StairCover.RoadStaircase + StairLayout.CutBelowSurface + 0.05f);
    }
}
