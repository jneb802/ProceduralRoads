using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;
namespace ProceduralRoads.Tests;

/// <summary>No ordinary bend is rounded tighter than the minimum radius: a bend between short legs takes more of a
/// gentler neighbour's leg, and short legs round a corner become one arc.</summary>
public class CornerFilletTests
{
    private const float Width = 4f;
    private static float Flat(Vector2 v) => 40f;

    /// <summary>Waypoints from a start, leg lengths and the turn (degrees, left positive) at each inner waypoint.</summary>
    private static List<Vector2> Walk(float[] legs, float[] turns)
    {
        var p = new List<Vector2> { new(0f, 0f) };
        float heading = 0f;
        for (int i = 0; i < legs.Length; i++)
        {
            if (i > 0) heading += turns[i - 1] * Mathf.Deg2Rad;
            p.Add(p[p.Count - 1] + new Vector2(Mathf.Cos(heading), Mathf.Sin(heading)) * legs[i]);
        }
        return p;
    }

    // A 90 degree bend between 7 m legs and a 135 degree bend between 16 m legs, gentle bends either side of each.
    private static List<Vector2> TwoCorners() => Walk(new[] { 16f, 7f, 7f, 16f, 16f, 16f }, new[] { 20f, 90f, -20f, -135f, 20f });

    /// <summary>The tightest radius the dense line turns on: over every stretch of 2 m or more between segment
    /// middles, its length over its heading change.</summary>
    internal static float TightestRadius(IReadOnlyList<Vector2> p)
    {
        var heading = new List<Vector2>(); var length = new List<float>();
        for (int i = 1; i < p.Count; i++)
        {
            Vector2 d = p[i] - p[i - 1];
            if (d.magnitude < 1e-4f) continue;
            heading.Add(d.normalized); length.Add(d.magnitude);
        }
        float tightest = float.PositiveInfinity;
        for (int k = 0; k < heading.Count; k++)
        {
            float run = length[k] * 0.5f, turn = 0f;
            for (int j = k + 1; j < heading.Count; j++)
            {
                turn += Turn(heading[j - 1], heading[j]);
                run += (length[j - 1] + length[j]) * 0.5f - (j - 1 == k ? length[k] * 0.5f : 0f);
                if (run < 2f) continue;
                if (Mathf.Abs(turn) > 1e-4f) tightest = Mathf.Min(tightest, run / Mathf.Abs(turn));
                break;
            }
        }
        return tightest;
    }

    private static float Turn(Vector2 a, Vector2 b) => Mathf.Atan2(a.x * b.y - a.y * b.x, a.x * b.x + a.y * b.y);

    /// <summary>The sharpest heading change at any one dense point, in degrees.</summary>
    internal static float SharpestVertex(IReadOnlyList<Vector2> p)
    {
        float worst = 0f;
        for (int i = 1; i < p.Count - 1; i++)
        {
            Vector2 a = p[i] - p[i - 1], b = p[i + 1] - p[i];
            if (a.magnitude < 1e-3f || b.magnitude < 1e-3f) continue;
            worst = Mathf.Max(worst, Mathf.Abs(Turn(a.normalized, b.normalized)) * Mathf.Rad2Deg);
        }
        return worst;
    }

    [Fact]
    public void Bends90And135BetweenShortLegsAreFilletedAtTheMinimumRadius()
    {
        // Without the minimum, the 45% share of each leg rounds both corners on a radius of 3 m or less.
        Assert.True(RoadSwitchbacks.Shape(TwoCorners(), Width, out var plain, out _, Flat, stair: false, bend: 24f, minRadius: 0f));
        Assert.True(TightestRadius(plain!) < 3.5f, $"plain tightest {TightestRadius(plain!):F2} m");

        Assert.True(RoadSwitchbacks.Shape(TwoCorners(), Width, out var shaped, out var landing, Flat, stair: false, bend: 24f, minRadius: 4f));
        Assert.Equal(2, RoadSwitchbacks.LastFilleted);
        Assert.True(TightestRadius(shaped!) >= 4f * 0.97f, $"tightest {TightestRadius(shaped!):F2} m");
        // On the 1 m dense step a 4 m arc turns a quarter radian a point.
        Assert.True(SharpestVertex(shaped!) <= 15f, $"sharpest {SharpestVertex(shaped!):F1} deg");
        Assert.DoesNotContain(true, landing!);
        Assert.Equal(TwoCorners()[0], shaped![0]);
        Assert.Equal(TwoCorners()[TwoCorners().Count - 1], shaped[shaped.Count - 1]);
    }

    [Fact]
    public void AHookOfShortLegsBecomesOneTurn()
    {
        // North 20 m, a 4 m hook west, then away east-north-east: the road ran to a point and left the other way.
        var hook = new List<Vector2> { new(0f, -20f), new(0f, 0f), new(-4f, 0.5f), new(16f, 9.5f) };
        Assert.True(RoadSwitchbacks.Shape(hook, Width, out var plain, out _, Flat, stair: false, bend: 24f, minRadius: 0f));
        Assert.True(SharpestVertex(plain!) > 45f, $"plain sharpest {SharpestVertex(plain!):F1} deg");

        Assert.True(RoadSwitchbacks.Shape(hook, Width, out var shaped, out _, Flat, stair: false, bend: 24f, minRadius: 4f));
        Assert.True(TightestRadius(shaped!) >= 4f * 0.97f, $"tightest {TightestRadius(shaped!):F2} m");
        Assert.True(SharpestVertex(shaped!) <= 15f, $"sharpest {SharpestVertex(shaped!):F1} deg");
        // The hook is gone: the road turns from north to east-north-east once, never out west.
        foreach (Vector2 q in shaped!) Assert.True(q.x > -0.5f, $"point {q} still runs out on the hook");
        // Its new legs, and the arc, are checked for water and sites.
        Assert.Contains(true, RoadSwitchbacks.LastCurve!);
    }

    [Fact]
    public void AHeldWaypointIsNeverFilleted()
    {
        var corners = TwoCorners();
        Vector2 kept = corners[4];
        Assert.True(RoadSwitchbacks.Shape(corners, Width, out var shaped, out _, Flat, stair: false, bend: 24f, minRadius: 4f,
            held: q => (q - kept).sqrMagnitude < 0.25f));
        Assert.Equal(1, RoadSwitchbacks.LastFilleted);
        Assert.True(TightestRadius(shaped!) < 3.5f, "the held 135 degree bend keeps its own arc");
    }

    [Fact]
    public void AWaypointOnAnotherRoadIsRoundedButNeverDropped()
    {
        var hook = new List<Vector2> { new(0f, -20f), new(0f, 0f), new(-4f, 0.5f), new(16f, 9.5f) };
        Vector2 junction = hook[2];
        Assert.True(RoadSwitchbacks.Shape(hook, Width, out var shaped, out _, Flat, stair: false, bend: 24f, minRadius: 4f,
            onRoad: q => (q - junction).sqrMagnitude < 0.25f));
        Assert.Contains(shaped!, q => q.x < -2f);
    }

    [Fact]
    public void WithoutBendRoundingNothingIsFilleted()
    {
        Assert.True(RoadSwitchbacks.Shape(TwoCorners(), Width, out var shaped, out _, Flat, stair: false, bend: 0f, minRadius: 4f));
        Assert.Null(shaped);
        Assert.Equal(0, RoadSwitchbacks.LastFilleted);
    }
}
