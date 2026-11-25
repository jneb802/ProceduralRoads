using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// What a generated road's staircase layout is allowed to spend (the slowest island took 301 s in
/// game, 191 s of it laying staircases, most of that on layouts that failed): no layout over a stretch
/// longer than any staircase laid, and a held layout tried first on a share of the allowance.
/// </summary>
public class StairAllowanceTests : IDisposable
{
    private readonly float _span = RoadStairRuns.MaxStairSpan;
    private readonly int _share = RoadStairRuns.HeldShare;

    public StairAllowanceTests() => RoadStairRuns.ResetCache();

    public void Dispose()
    {
        RoadStairRuns.MaxStairSpan = _span;
        RoadStairRuns.HeldShare = _share;
        RoadStairRuns.ResetCache();
    }

    private static StairPlan Plan(Vector2 a, Vector2 b, Func<float, float, float> ground, float? arriving, bool retried = false) =>
        (StairPlan)typeof(RoadStairRuns).GetMethod("Plan", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object?[]
        {
            a, b, StairKit.Stone, ground, arriving, null, null, StairCover.RoadStaircase, RoadStairRuns.TopLandings, null, null, retried, true, null, null,
        })!;

    /// <summary>A stretch longer than any staircase laid is refused at once, not searched (the 19 tried over
    /// 150 m offline all failed, to 878 m, spending 50 s).</summary>
    [Fact]
    public void AStretchLongerThanAnyStaircaseIsNotLaidOut()
    {
        Func<float, float, float> ramp = (x, _) => x * 0.6f;

        StairPlan plan = Plan(new Vector2(0f, 0f), new Vector2(300f, 0f), ramp, null);

        Assert.False(plan.Reached);
        Assert.Contains("longer than a staircase is laid", plan.Reason);
    }

    /// <summary>A layout another follows if it fails gets half the allowance; the last, all of it.</summary>
    [Fact]
    public void AStaircaseLayoutWithAnotherToFollowIsTriedOnAShareOfTheAllowance()
    {
        Func<float, float, float> ramp = (x, _) => x * 0.6f;

        Plan(new Vector2(0f, 0f), new Vector2(20f, 0f), ramp, 90f, retried: true);
        Assert.True(RoadStairRuns.HeldShare > 1);
        Assert.Equal(RoadStairRuns.GenerationTotalExpansions / RoadStairRuns.HeldShare, RoadStairRuns.LastAllowance);
        Plan(new Vector2(0f, 0f), new Vector2(20f, 1f), ramp, null);
        Assert.Equal(RoadStairRuns.GenerationTotalExpansions, RoadStairRuns.LastAllowance);
    }
}
