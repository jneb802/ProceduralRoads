using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>The grade limiter: what it promises about the profile it returns.</summary>
public class RoadGradeTests
{
    private const float Cap = 0.2f;

    private static List<Vector2> Line(int count, float spacing = 1f)
    {
        var points = new List<Vector2>(count);
        for (int i = 0; i < count; i++) points.Add(new Vector2(i * spacing, 0f));
        return points;
    }

    [Fact]
    public void AProfileInsideTheCapIsLeftAlone()
    {
        var points = Line(50);
        var heights = new List<float>();
        for (int i = 0; i < 50; i++) heights.Add(40f + i * 0.1f); // 10%, inside a 20% cap
        var before = new List<float>(heights);

        Assert.True(RoadGrade.Limit(points, heights, Cap));
        for (int i = 0; i < heights.Count; i++)
            Assert.True(Mathf.Abs(heights[i] - before[i]) < 0.001f,
                $"point {i} moved {heights[i] - before[i]:F4} m for nothing");
    }

    [Fact]
    public void EveryStepIsInsideTheCapAfterwards()
    {
        // A cliff in the middle: 30 m of rise over one metre.
        var points = Line(201);
        var heights = new List<float>();
        for (int i = 0; i < 201; i++) heights.Add(i < 100 ? 40f : 70f);

        Assert.True(RoadGrade.Limit(points, heights, Cap));
        Assert.True(RoadGrade.SteepestStep(points, heights) <= Cap + 0.001f,
            $"steepest step {RoadGrade.SteepestStep(points, heights):F3} still over {Cap}");
    }

    [Fact]
    public void BothEndsAreHeldExactly()
    {
        var points = Line(201);
        var heights = new List<float>();
        for (int i = 0; i < 201; i++) heights.Add(i < 100 ? 40f : 70f);

        Assert.True(RoadGrade.Limit(points, heights, Cap));
        Assert.Equal(40f, heights[0]);
        Assert.Equal(70f, heights[200]);
    }

    [Fact]
    public void EndsFurtherApartThanTheCapAllowsAreRefused()
    {
        // 30 m of rise over 50 m of road needs 60%; the cap is 20%.
        var points = Line(51);
        var heights = new List<float>();
        for (int i = 0; i < 51; i++) heights.Add(i < 25 ? 40f : 70f);
        var before = new List<float>(heights);

        Assert.False(RoadGrade.Limit(points, heights, Cap));
        Assert.Equal(before, heights); // refused, and left untouched
    }

    [Fact]
    public void ExactlyReachableEndsAreAccepted()
    {
        // 10 m of rise over exactly 50 m at a 20% cap: the limit case is in.
        var points = Line(51);
        var heights = new List<float>();
        for (int i = 0; i < 51; i++) heights.Add(i < 25 ? 40f : 50f);

        Assert.True(RoadGrade.Limit(points, heights, Cap));
        Assert.True(RoadGrade.SteepestStep(points, heights) <= Cap + 0.001f);
        Assert.Equal(40f, heights[0]);
        Assert.Equal(50f, heights[50]);
    }

    [Fact]
    public void TheCapIsMetresPerMetreNotPerPoint()
    {
        // Same heights, points four times further apart: four times the run,
        // so a profile that is refused at one spacing is accepted at the other.
        var heightsA = new List<float>();
        var heightsB = new List<float>();
        for (int i = 0; i < 51; i++) { heightsA.Add(40f + i * 0.5f); heightsB.Add(40f + i * 0.5f); }

        Assert.False(RoadGrade.Limit(Line(51, 1f), heightsA, Cap));   // 0.5 m per 1 m = 50%
        Assert.True(RoadGrade.Limit(Line(51, 4f), heightsB, Cap));    // 0.5 m per 4 m = 12.5%
    }

    [Fact]
    public void TheResultIsTheNearestCappedProfileWeCanFind()
    {
        // Against a thousand random capped profiles through the same two ends,
        // none is closer to the input than what Limit returned. This is the
        // property that matters: the limiter is not free to redraw a road, only
        // to take the steepness out of it.
        var rng = new Random(20260918);
        var points = Line(120);
        var input = new List<float>();
        for (int i = 0; i < 120; i++)
            input.Add(50f + Mathf.Sin(i * 0.3f) * 6f + (float)rng.NextDouble() * 2f);
        // Ends within reach of each other so the profile is buildable.
        input[0] = 50f;
        input[119] = 56f;

        var limited = new List<float>(input);
        Assert.True(RoadGrade.Limit(points, limited, Cap));
        float ourError = MaxError(input, limited);

        for (int trial = 0; trial < 1000; trial++)
        {
            var candidate = new List<float>(input);
            for (int i = 0; i < candidate.Count; i++)
                candidate[i] += ((float)rng.NextDouble() - 0.5f) * 8f;
            candidate[0] = input[0];
            candidate[119] = input[119];
            if (!RoadGrade.Limit(points, candidate, Cap)) continue;
            Assert.True(MaxError(input, candidate) >= ourError - 0.001f,
                $"trial {trial} found a capped profile {MaxError(input, candidate):F3} m off, better than our {ourError:F3} m");
        }
    }

    [Fact]
    public void AZeroOrNegativeCapDoesNothing()
    {
        var points = Line(51);
        var heights = new List<float>();
        for (int i = 0; i < 51; i++) heights.Add(i < 25 ? 40f : 70f);
        var before = new List<float>(heights);

        Assert.True(RoadGrade.Limit(points, heights, 0f));
        Assert.Equal(before, heights);
        Assert.True(RoadGrade.Limit(points, heights, -1f));
        Assert.Equal(before, heights);
        Assert.False(RoadGrade.Capped(0f));
        Assert.True(RoadGrade.Capped(0.2f));
    }

    [Fact]
    public void TwoPointsAreCheckedToo()
    {
        // All ends and no middle. There is nothing to reshape, but the two can
        // still be further apart than the cap allows, and a road that steep is
        // refused whether or not it has a point in the middle to prove it.
        var points = new List<Vector2> { new(0f, 0f), new(10f, 0f) };
        var steep = new List<float> { 40f, 50f };   // 100%
        var gentle = new List<float> { 40f, 41f };  // 10%

        Assert.False(RoadGrade.Limit(points, steep, Cap));
        Assert.Equal(new List<float> { 40f, 50f }, steep);
        Assert.True(RoadGrade.Limit(points, gentle, Cap));
        Assert.Equal(new List<float> { 40f, 41f }, gentle);
    }

    [Fact]
    public void CoincidentPointsDoNotReportAnInfiniteGrade()
    {
        var points = new List<Vector2> { new(0f, 0f), new(0f, 0f), new(10f, 0f) };
        var heights = new List<float> { 40f, 45f, 46f };
        Assert.Equal(0.1f, RoadGrade.SteepestStep(points, heights), 3);
    }

    private static float MaxError(List<float> a, List<float> b)
    {
        float worst = 0f;
        for (int i = 0; i < a.Count; i++) worst = Mathf.Max(worst, Mathf.Abs(a[i] - b[i]));
        return worst;
    }
}
