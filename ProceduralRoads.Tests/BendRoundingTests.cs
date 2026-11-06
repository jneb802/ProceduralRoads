using System.Collections.Generic;
using UnityEngine;
using Xunit;
namespace ProceduralRoads.Tests;

/// <summary>Ordinary bends rounded with arcs , switchbacks kept tight.</summary>
public class BendRoundingTests
{
    // A gentle wander on flat ground: bends of 18-45 degrees, no switchback.
    private static List<Vector2> Wander() => new() { new(0, 0), new(16, 8), new(32, 8), new(48, 16), new(56, 32), new(72, 40) };
    private static float Flat(Vector2 v) => 40f;

    private static float SharpestCorner(List<Vector2> p)
    {
        float worst = 0f;
        for (int i = 1; i < p.Count - 1; i++)
        {
            if (Vector2.Distance(p[i - 1], p[i]) < 1e-3f || Vector2.Distance(p[i], p[i + 1]) < 1e-3f) continue;
            Vector2 a = (p[i] - p[i - 1]).normalized, b = (p[i + 1] - p[i]).normalized;
            worst = Mathf.Max(worst, (float)System.Math.Acos(Mathf.Clamp(a.x * b.x + a.y * b.y, -1f, 1f)) * 57.29578f);
        }
        return worst;
    }

    [Fact]
    public void WithoutBendRadiusAWanderIsLeftForTheSpline()
    {
        Assert.True(RoadSwitchbacks.Shape(Wander(), 4, out var shaped, out _, Flat, stair: false, bend: 0f));
        Assert.Null(shaped);
    }

    [Fact]
    public void WithBendRadiusEveryCornerIsSmallAndNoneIsALanding()
    {
        Assert.True(RoadSwitchbacks.Shape(Wander(), 4, out var shaped, out var landing, Flat, stair: false, bend: 24f));
        Assert.NotNull(shaped);
        // Dense points one metre apart along arcs of 5 m radius or more turn a
        // few degrees each, never the 18-45 of the raw corners.
        Assert.True(SharpestCorner(shaped!) < 12f, $"sharpest {SharpestCorner(shaped!):F1} deg");
        Assert.DoesNotContain(true, landing!);
        Assert.Equal(Wander()[0], shaped[0]);
        Assert.Equal(Wander()[Wander().Count - 1], shaped[shaped.Count - 1]);
        Assert.Contains(true, RoadSwitchbacks.LastCurve!);
    }

    [Fact]
    public void ARoundedBendNeverLeavesTheCornerByMoreThanItsLegsAllow()
    {
        Assert.True(RoadSwitchbacks.Shape(Wander(), 4, out var shaped, out _, Flat, stair: false, bend: 200f));
        // However large the radius asked for, each arc takes at most 45% of
        // the legs beside it, so the path stays near the waypoints.
        foreach (var pt in shaped!)
        {
            float nearest = float.MaxValue;
            var w = Wander();
            for (int i = 1; i < w.Count; i++)
            {
                Vector2 a = w[i - 1], b = w[i], d = b - a;
                float t = Mathf.Clamp01(((pt - a).x * d.x + (pt - a).y * d.y) / d.sqrMagnitude);
                nearest = Mathf.Min(nearest, Vector2.Distance(pt, a + d * t));
            }
            Assert.True(nearest < 4f, $"point {pt} is {nearest:F1} m off the waypoint path");
        }
    }
}
