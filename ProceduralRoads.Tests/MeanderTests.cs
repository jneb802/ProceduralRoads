using Xunit;
namespace ProceduralRoads.Tests;
public class MeanderTests
{
    [Fact]
    public void TheFieldIsSmoothBoundedAndRepeatable()
    {
        float lo = 1f, hi = 0f, jump = 0f;
        for (int i = 0; i < 2000; i++)
        {
            float x = i * 0.013f - 7f, y = i * 0.007f + 3f;
            float v = RoadPathfinder.MeanderField(x * 150f, y * 150f);
            Assert.InRange(v, 0f, 1f);
            Assert.Equal(v, RoadPathfinder.MeanderField(x * 150f, y * 150f));
            lo = System.Math.Min(lo, v); hi = System.Math.Max(hi, v);
            jump = System.Math.Max(jump, System.Math.Abs(v - RoadPathfinder.MeanderField((x + 0.013f) * 150f, (y + 0.007f) * 150f)));
        }
        // It varies (a flat field would bend nothing) and has no steps.
        Assert.True(hi - lo > 0.3f, $"range {lo}..{hi}");
        Assert.True(jump < 0.08f, $"largest change between neighbouring samples {jump}");
    }

    [Fact]
    public void ThePullToleranceKeepsBendsAndDropsTheStaircase()
    {
        var staircase = new System.Collections.Generic.List<UnityEngine.Vector2> { new(0, 0), new(8, 3), new(16, 0), new(24, 3), new(32, 0) };
        Assert.True(RoadPathPull.WithinTolerance(staircase, 0, 4, 4f));
        var bend = new System.Collections.Generic.List<UnityEngine.Vector2> { new(0, 0), new(8, 6), new(16, 9), new(24, 6), new(32, 0) };
        Assert.False(RoadPathPull.WithinTolerance(bend, 0, 4, 4f));
    }

    [Fact]
    public void TheSwayIsBoundedAndDoesNotRepeat()
    {
        float lo = 1f, hi = -1f;
        var a = new float[400];
        for (int i = 0; i < a.Length; i++) { a[i] = RoadWiggle.Sway(i * 4f, 11.7f); lo = System.Math.Min(lo, a[i]); hi = System.Math.Max(hi, a[i]); }
        Assert.InRange(lo, -1f, 1f); Assert.InRange(hi, -1f, 1f);
        Assert.True(hi - lo > 0.4f, $"range {lo}..{hi}");
        // No period: the sway 97 m (one long wave) on is not the sway here.
        float diff = 0f;
        for (int i = 0; i < 300; i++) diff += System.Math.Abs(a[i] - RoadWiggle.Sway(i * 4f + 97f, 11.7f));
        Assert.True(diff / 300f > 0.05f, $"mean difference one long wave apart {diff / 300f}");
    }

    [Fact]
    public void ResamplingKeepsBothEnds()
    {
        var path = new System.Collections.Generic.List<UnityEngine.Vector2> { new(0, 0), new(50, 0), new(50, 37) };
        var even = RoadWiggle.Resample(path, 12f);
        Assert.Equal(path[0], even[0]);
        Assert.Equal(path[2], even[even.Count - 1]);
        for (int i = 1; i < even.Count - 1; i++) Assert.InRange(UnityEngine.Vector2.Distance(even[i - 1], even[i]), 6f, 12.01f);
    }

    [Fact]
    public void ASwayThatMovesMoreEarthIsNotKept()
    {
        System.Func<UnityEngine.Vector2, float> ground = q => q.y;   // ground rises to the north
        var plain = new System.Collections.Generic.List<UnityEngine.Vector2> { new(0, 0), new(10, 0), new(20, 0) };
        var level = new System.Collections.Generic.List<float> { 0f, 0f, 0f };
        var north = new System.Collections.Generic.List<UnityEngine.Vector2> { new(0, 0), new(10, 3), new(20, 0) };
        // Swayed 3 m north onto ground 3 m higher: a 3 m cut the plain road does not have.
        Assert.False(RoadWiggle.NoMoreEarth(north, level, plain, level, ground));
        var slight = new System.Collections.Generic.List<UnityEngine.Vector2> { new(0, 0), new(10, 0.4f), new(20, 0) };
        Assert.True(RoadWiggle.NoMoreEarth(slight, level, plain, level, ground));
    }
}
