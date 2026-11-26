using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A staircase layout says whether it was not reached for its search budget (StairPlan.OutOfBudget: its leg's steps, the
/// whole layout's, or the stall's), which a merge's free layout reads (RoadStairRuns.Merge: not laid once both held layouts
/// before it ran out), or because no piece fits.
/// </summary>
public sealed class StairOutOfBudgetTests
{
    /// <summary>A steady slope, 8 m up over 24 m.</summary>
    private static float Slope(float x, float z) => z / 3f;

    private static readonly Vector2 Foot = new(0f, 0f), Top = new(0f, 24f);

    [Fact]
    public void AReachedLayoutIsNotOutOfBudget()
    {
        StairPlan plan = StairLayout.Build(Foot, Top, Slope);
        Assert.True(plan.Reached, plan.Reason);
        Assert.False(plan.OutOfBudget);
    }

    [Fact]
    public void ALegsBudgetSpentIsOutOfBudget()
    {
        StairPlan plan = StairLayout.Build(Foot, Top, Slope, maxExpansions: 3);
        Assert.False(plan.Reached);
        Assert.True(plan.OutOfBudget, plan.Reason);
    }

    [Fact]
    public void TheLayoutsWholeBudgetSpentIsOutOfBudget()
    {
        StairPlan plan = StairLayout.Build(Foot, Top, Slope, totalExpansions: 0);
        Assert.False(plan.Reached);
        Assert.True(plan.OutOfBudget, plan.Reason);
    }

    [Fact]
    public void AStallIsOutOfBudget()
    {
        StairPlan plan = StairLayout.Build(Foot, Top, Slope, stallExpansions: 0);
        Assert.False(plan.Reached);
        Assert.True(plan.OutOfBudget, plan.Reason);
    }

    [Fact]
    public void WhereNoPieceFitsItIsNotOutOfBudget()
    {
        StairPlan plan = StairLayout.Build(Foot, Top, Slope, allowed: _ => false);
        Assert.False(plan.Reached);
        Assert.False(plan.OutOfBudget, plan.Reason);
    }
}
