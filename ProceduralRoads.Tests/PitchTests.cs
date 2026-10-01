using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using Xunit;
namespace ProceduralRoads.Tests;

public class PitchTests
{
    private static (List<Vector2> pts, List<float> h) Climb(float length, float grade, float step = 1f)
    {
        var p = new List<Vector2>(); var h = new List<float>();
        for (float x = 0; x <= length + 1e-3f; x += step) { p.Add(new Vector2(x, 0)); h.Add(40f + grade * x); }
        return (p, h);
    }

    [Fact]
    public void PitchesMakeShortSteepBitsAndShortVariedEasesUnderTheSustainedGrade()
    {
        // A 200 m climb at 25%.
        var (p, h) = Climb(200f, 0.25f);
        var grades = RoadPitches.PlacePitches(p, h, 0.35f, null, pitchGrade: 0.5f, pitchLength: 8f,
            everyMin: 12f, everyMax: 25f, easeMin: 4f, easeMax: 8f, easeGrade: 0.08f, minGrade: 0.12f);
        Assert.NotNull(grades);
        float cap = RoadPitches.LimitCap(0.35f, grades);
        Assert.Equal(0.5f, cap, 3);
        Assert.True(RoadGrade.Limit(p, h, cap, grades));
        // Ease runs: several, each 4-8 m, at no more than 8%; not all the same length.
        var easeRuns = new List<float>(); float run = 0f; float steepest = 0f;
        for (int i = 1; i < p.Count; i++)
        {
            float g = (h[i] - h[i - 1]) / Vector2.Distance(p[i - 1], p[i]);
            steepest = Mathf.Max(steepest, g);
            bool ease = grades![i] <= 0.08f + 1e-4f;
            if (ease) { Assert.True(g <= 0.0801f, $"ease step {i} at {g:F3}"); run += 1f; }
            else if (run > 0f) { easeRuns.Add(run); run = 0f; }
        }
        Assert.True(easeRuns.Count >= 5, $"{easeRuns.Count} eases on 200 m");
        Assert.All(easeRuns, r => Assert.InRange(r, 3f, 9f));
        Assert.True(easeRuns.Max() - easeRuns.Min() >= 1f, "every ease the same length");
        // The height the eases give up goes into pitches steeper than the old cap, never past 50%.
        Assert.True(steepest > 0.35f, $"steepest {steepest:F2}: no pitch");
        Assert.True(steepest <= 0.5001f);
        Assert.Equal(40f, h[0]); Assert.Equal(40f + 0.25f * 200f, h[h.Count - 1], 3);
    }

    [Fact]
    public void ASwitchbackLandingCountsAsAnEase()
    {
        // A 200 m climb with a flat landing (as a switchback leaves) at 40-46 m.
        var (p, h) = Climb(200f, 0.25f);
        var landing = new float[p.Count];
        for (int i = 0; i < p.Count; i++) landing[i] = (p[i].x > 40f && p[i].x <= 46f) ? 0.05f : 0.35f;
        var grades = RoadPitches.PlacePitches(p, h, 0.35f, landing, pitchGrade: 0.5f, pitchLength: 8f,
            everyMin: 12f, everyMax: 25f, easeMin: 4f, easeMax: 8f, easeGrade: 0.09f, minGrade: 0.12f);
        Assert.NotNull(grades);
        // The landing stays at its own grade, the edges beside it may pitch,
        // and no generated ease starts within 12 m after it.
        Assert.All(Enumerable.Range(0, p.Count).Where(i => p[i].x > 40.5f && p[i].x <= 46f), i => Assert.Equal(0.05f, grades![i], 3));
        Assert.Equal(0.5f, grades![p.FindIndex(q => q.x > 48f)], 3);
        Assert.DoesNotContain(Enumerable.Range(0, p.Count).Where(i => p[i].x > 46.5f && p[i].x < 58f), i => grades![i] <= 0.0901f);
        Assert.True(RoadGrade.Limit(p, h, RoadPitches.LimitCap(0.35f, grades), grades));
    }

    [Fact]
    public void FollowingTheGroundPutsEasesOnNaturalBenches()
    {
        // A 120 m climb whose natural ground has a level bench at 40-47 m.
        var (p, h) = Climb(120f, 0.25f);
        var ground = new List<float>();
        float gh = 40f;
        for (int i = 0; i < p.Count; i++)
        {
            if (i > 0) gh += (p[i].x > 40f && p[i].x <= 47f ? 0.02f : 0.29f) * (p[i].x - p[i - 1].x);
            ground.Add(gh);
        }
        var grades = RoadPitches.PlacePitches(p, h, 0.35f, null, pitchGrade: 0.5f, pitchLength: 8f,
            everyMin: 12f, everyMax: 25f, easeMin: 4f, easeMax: 8f, easeGrade: 0.09f, minGrade: 0.12f, ground: ground);
        Assert.NotNull(grades);
        // Some ease lies on the bench.
        Assert.Contains(Enumerable.Range(0, p.Count), i => p[i].x > 40.5f && p[i].x <= 47f && grades![i] <= 0.0901f);
        var without = RoadPitches.PlacePitches(p, h, 0.35f, null, pitchGrade: 0.5f, pitchLength: 8f,
            everyMin: 12f, everyMax: 25f, easeMin: 4f, easeMax: 8f, easeGrade: 0.09f, minGrade: 0.12f);
        Assert.NotEqual(string.Join(",", grades!), string.Join(",", without!));
    }

    [Fact]
    public void FollowingTheGroundSmoothsOverAShortWindow()
    {
        bool saved = RoadSpatialGrid.FollowGround;
        try
        {
            RoadSpatialGrid.FollowGround = false;
            Assert.Equal(RoadConstants.HeightSmoothingWindow, RoadSpatialGrid.SmoothingWindow(4f));
            RoadSpatialGrid.FollowGround = true;
            Assert.Equal(9, RoadSpatialGrid.SmoothingWindow(4f));
            Assert.True(RoadSpatialGrid.SmoothingWindow(4f) % 2 == 1);
        }
        finally { RoadSpatialGrid.FollowGround = saved; }
    }

    [Fact]
    public void TheSustainedGradeIsAPriceNotAWall()
    {
        float savedW = RoadPitches.PitchOverWeight, savedS = RoadPitches.Sustained;
        bool savedE = RoadPitches.Enabled;
        try
        {
            RoadPitches.Enabled = true;
            RoadPitches.Sustained = 0.30f;
            RoadPitches.PitchOverWeight = 0f;
            Assert.Equal(0f, RoadPitches.OverSustainedPrice(0.40f, 8f));
            RoadPitches.PitchOverWeight = 50f;
            Assert.Equal(0f, RoadPitches.OverSustainedPrice(0.25f, 8f));       // under sustained: free
            Assert.Equal(50f * 0.10f * 8f, RoadPitches.OverSustainedPrice(0.40f, 8f), 3);
        }
        finally { RoadPitches.PitchOverWeight = savedW; RoadPitches.Sustained = savedS; RoadPitches.Enabled = savedE; }
    }

    [Fact]
    public void AJoinGetsALevelLandingThatPitchesTreatsAsAnEase()
    {
        var (p, h) = Climb(120f, 0.25f);
        var g = RoadPitches.ApplyJunctionLanding(p, null, 0.35f, atStart: false, atEnd: true, length: 6f);
        Assert.All(Enumerable.Range(0, p.Count).Where(i => p[i].x > 114.5f), i => Assert.Equal(0.05f, g[i], 3));
        Assert.All(Enumerable.Range(1, p.Count - 1).Where(i => p[i].x < 110f), i => Assert.Equal(0.35f, g[i], 3));
        var pitched = RoadPitches.PlacePitches(p, h, 0.35f, g, pitchGrade: 0.5f, pitchLength: 8f,
            everyMin: 12f, everyMax: 25f, easeMin: 4f, easeMax: 8f, easeGrade: 0.09f, minGrade: 0.12f);
        Assert.NotNull(pitched);
        Assert.True(RoadGrade.Limit(p, h, RoadPitches.LimitCap(0.35f, pitched), pitched));
        for (int i = 1; i < p.Count; i++)
            if (p[i].x > 114.5f) Assert.True((h[i] - h[i - 1]) / (p[i].x - p[i - 1].x) <= 0.0501f, $"landing step at {p[i].x} is {(h[i] - h[i - 1]):F3}");
    }
    [Theory]
    [InlineData(0.28f)]
    [InlineData(0.30f)]
    [InlineData(0.33f)]
    [InlineData(0.35f)]
    public void ASteepClimbKeepsItsEasesAndTakesTheHeightInLongerPitches(float grade)
    {
        // A 216 m climb held at the 35% cap lost every ease (the room test
        // dropped them one by one); from 30% up no climb kept a rest. Rests are never dropped:
        // the pitches beside them get longer instead.
        var (p, h) = Climb(216f, grade);
        RoadPitches.ResetCounters();
        var grades = RoadPitches.PlacePitches(p, h, 0.35f, null);
        Assert.NotNull(grades);
        int eases = 0; bool inEase = false;
        for (int i = 1; i < grades!.Length; i++) { bool e = grades[i] <= RoadPitches.EaseGrade + 1e-4f; if (e && !inEase) eases++; inEase = e; }
        Assert.True(eases >= 7, $"{eases} eases on a 216 m climb at {grade:P0}");
        Assert.Equal(0, RoadPitches.ClimbsThatLostEases);
        float end = h[h.Count - 1];
        Assert.True(RoadGrade.Limit(p, h, RoadPitches.LimitCap(0.35f, grades), grades), "the limiter could not build it");
        Assert.Equal(end, h[h.Count - 1], 3);
        for (int i = 1; i < p.Count; i++)
        {
            float g = (h[i] - h[i - 1]) / Vector2.Distance(p[i - 1], p[i]);
            Assert.True(g <= RoadPitches.PitchGrade + 1e-3f, $"step {i} at {g:P0}");
            if (grades[i] <= RoadPitches.EaseGrade + 1e-4f) Assert.True(g <= RoadPitches.EaseGrade + 1e-3f, $"ease step {i} at {g:P0}");
        }
    }
}
