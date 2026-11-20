using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The last resort's one pitch (RoadPitches.AsLastResort): a climb too steep for its rests within the grade cap is refused,
/// and only when the road is planned once more as the last resort may that climb take one pitch, at most PitchLength metres
/// at up to PitchGrade between two of its rests, every rest kept; a world stores pitches within its budget and spaced out
/// (RoadPitches.PitchRefusal, Record). Outside the last resort nothing changes: no edge past the cap.
/// </summary>
[Collection("RoadStatics")]
public class LastResortPitchTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    private sealed class Ground : WorldGenerator
    {
        public Func<float, float, float> Height = (_, _) => 40f;
        public override float GetHeight(float wx, float wy) => Height(wx, wy);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
        public override int GetSeed() => 91;
    }

    private readonly Ground _world = new();

    public LastResortPitchTests()
    {
        _scope.WithWorld(_world);
        RoadNetworkGenerator.Reset();
        RoadPitches.ResetCounters();
    }

    public void Dispose()
    {
        RoadPitches.ResetCounters();
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    private static (List<Vector2> pts, List<float> h) Climb(float length, float grade, float step = 1f)
    {
        var p = new List<Vector2>(); var h = new List<float>();
        for (float x = 0; x <= length + 1e-3f; x += step) { p.Add(new Vector2(x, 0)); h.Add(40f + grade * x); }
        return (p, h);
    }

    private static List<(int from, int to)> Runs(float[] grades, Func<float, bool> on)
    {
        var runs = new List<(int, int)>();
        for (int i = 1; i < grades.Length; i++)
        {
            if (!on(grades[i])) continue;
            int j = i; while (j + 1 < grades.Length && on(grades[j + 1])) j++;
            runs.Add((i, j)); i = j;
        }
        return runs;
    }

    /// <summary>A 60 m climb at 30 % has too little room for its rests at the cap (the rests at 9 % leave the edges between
    /// them short of its 18 m): refused. As the last resort it keeps every rest and takes one pitch, between two rests, at
    /// most PitchLength long at PitchGrade; built so, every other edge is at the cap at most and the pitch at 50 % at most.</summary>
    [Fact]
    public void AClimbTooSteepForItsRestsTakesOnePitchBetweenTwoRestsOnlyAsTheLastResort()
    {
        var (p, h) = Climb(60f, 0.30f);
        var steep = new List<Vector2>();
        var normal = RoadPitches.PlacePitches(p, h, 0.35f, null, tooSteep: steep);
        Assert.NotEmpty(steep);
        Assert.All(normal!, g => Assert.True(g <= 0.35f + 1e-6f));
        int restsNormal = Runs(normal!, g => g <= RoadPitches.EaseGrade + 1e-4f).Count;

        steep.Clear();
        var pitched = RoadPitches.AsLastResort("a test", () => RoadPitches.PlacePitches(p, h, 0.35f, null, tooSteep: steep));
        Assert.Empty(steep);
        var rests = Runs(pitched!, g => g <= RoadPitches.EaseGrade + 1e-4f);
        Assert.Equal(restsNormal, rests.Count);
        var pitches = Runs(pitched!, g => g > 0.35f + 1e-4f);
        Assert.Single(pitches);
        var (from, to) = pitches[0];
        Assert.True(p[to].x - p[from - 1].x <= RoadPitches.PitchLength + 1e-3f, $"a pitch of {p[to].x - p[from - 1].x} m");
        for (int i = from; i <= to; i++) Assert.Equal(RoadPitches.PitchGrade, pitched![i], 4);
        Assert.Contains(rests, r => r.to < from);
        Assert.Contains(rests, r => r.from > to);

        Assert.Equal(RoadPitches.PitchGrade, RoadPitches.LimitCap(0.35f, pitched), 4);
        float end = h[h.Count - 1];
        Assert.True(RoadGrade.Limit(p, h, RoadPitches.LimitCap(0.35f, pitched), pitched), "the limiter could not build it with its pitch");
        Assert.Equal(end, h[h.Count - 1], 3);
        for (int i = 1; i < p.Count; i++)
        {
            float g = (h[i] - h[i - 1]) / Vector2.Distance(p[i - 1], p[i]);
            Assert.True(g <= (i >= from && i <= to ? RoadPitches.PitchGrade : 0.35f) + 1e-3f, $"step {i} at {g:P0}");
        }
    }

    /// <summary>A climb whose height one pitch cannot make (200 m at 32 %) stays too steep for its rests, last resort or not;
    /// a schedule with no pitch is limited and judged at the cap (LimitCap).</summary>
    [Fact]
    public void OnePitchIsAllTheLastResortGives()
    {
        var (p, h) = Climb(200f, 0.32f);
        var steep = new List<Vector2>();
        var grades = RoadPitches.AsLastResort("a test", () => RoadPitches.PlacePitches(p, h, 0.35f, null, tooSteep: steep));
        Assert.NotEmpty(steep);
        Assert.Equal(0.35f, RoadPitches.LimitCap(0.35f, grades), 6);
        Assert.Equal(0.35f, RoadPitches.LimitCap(0.35f, null), 6);
    }

    /// <summary>Through the generation planner (RoadNetworkGenerator.PlanRoadPathWithCrossings) at the mod's own settings:
    /// a road up a 60 m climb at 32 % is refused for its rests; planned as the last resort it is built with one pitch
    /// (RoadPitches.PitchesOf) of at most PitchLength at PitchGrade at most, and every other stored step within the cap.</summary>
    [Fact]
    public void ThePlannerBuildsAClimbTooSteepForItsRestsOnlyAsTheLastResortWithOnePitch()
    {
        _world.Height = (x, _) => 30f + 0.32f * Mathf.Clamp(x, 0f, 60f);
        var path = new List<Vector2>();
        for (float x = -30f; x <= 90f + 1e-3f; x += 2f) path.Add(new Vector2(x, 0f));
        List<RoadSpatialGrid.PlannedPath>? normal = null, last = null;
        string? normalWhy = null;
        PlainRoads.WithModDefaults(() =>
        {
            normal = RoadNetworkGenerator.PlanRoadPathWithCrossings(path, new List<RoadCrossing>(), 4f, null, null, new List<RoadStairSpan>());
            normalWhy = RoadSpatialGrid.LastRefusal;
            last = RoadPitches.AsLastResort("a test", () =>
                RoadNetworkGenerator.PlanRoadPathWithCrossings(path, new List<RoadCrossing>(), 4f, null, null, new List<RoadStairSpan>()));
        });
        Assert.Null(normal);
        Assert.Equal(RoadSpatialGrid.TooSteepForRestsRefusal, normalWhy);
        Assert.True(last != null, RoadSpatialGrid.LastRefusal);
        var pitches = RoadPitches.PitchesOf(last!, RoadGrade.Configured);
        Assert.Single(pitches);
        Assert.True(pitches[0].Length <= RoadPitches.PitchLength + 1e-3f, $"a pitch of {pitches[0].Length} m");
        Assert.True(pitches[0].Steepest <= RoadPitches.PitchGrade + 1e-3f, $"a pitch at {pitches[0].Steepest:P0}");
        foreach (var plan in last!)
            for (int i = 1; i < plan.Points.Count; i++)
            {
                float run = Vector2.Distance(plan.Points[i - 1], plan.Points[i]);
                if (run < 1e-3f || plan.Grades![i] > RoadGrade.Configured + 1e-4f) continue;
                Assert.True(Mathf.Abs(plan.Heights[i] - plan.Heights[i - 1]) / run <= RoadGrade.Configured + 2e-3f, $"step {i} off the pitch past the cap");
            }
    }

    private static RoadSpatialGrid.PlannedPath Pitched(float x0)
    {
        // A 10 m stretch with a 4 m pitch at 45 % in its middle, the rest level.
        var points = new List<Vector2>(); var heights = new List<float>(); var grades = new List<float>();
        float y = 10f;
        for (int i = 0; i <= 10; i++)
        {
            points.Add(new Vector2(x0 + i, 0f));
            bool pitch = i >= 4 && i <= 7;
            if (i > 0 && pitch) y += 0.45f;
            heights.Add(y);
            grades.Add(pitch ? RoadPitches.PitchGrade : 0.35f);
        }
        return new RoadSpatialGrid.PlannedPath(points, heights, new List<RoadPointDebugInfo>(), 4f, 10f, false) { Grades = grades };
    }

    /// <summary>A pitch is measured where its schedule allowed it past the cap and the road is stored past it (4 m here); a
    /// stored road is refused for a pitch nearer than PitchSpacing to one already stored, or past its island's share of the
    /// world's budget; a road with no pitch is never refused for one.</summary>
    [Fact]
    public void PitchesAreBudgetedAndSpacedAsTheyAreStored() => CrossIslandReads.On(1, () =>
    {
        RoadPitches.BeginIsland(1, 10f);
        var first = new List<RoadSpatialGrid.PlannedPath> { Pitched(0f) };
        var found = RoadPitches.PitchesOf(first, 0.35f);
        Assert.Single(found);
        Assert.Equal(4f, found[0].Length, 3);
        Assert.Null(RoadPitches.PitchRefusal(first, 0.35f, out _));
        Assert.Single(RoadPitches.Record(first, 0.35f));

        Assert.Equal(RoadPitches.PitchSpacingRefusal, RoadPitches.PitchRefusal(new List<RoadSpatialGrid.PlannedPath> { Pitched(150f) }, 0.35f, out _));
        Assert.Null(RoadPitches.PitchRefusal(new List<RoadSpatialGrid.PlannedPath> { Pitched(300f) }, 0.35f, out _));
        // Two of them on one road, 30 m apart: too near each other.
        Assert.Equal(RoadPitches.PitchSpacingRefusal,
            RoadPitches.PitchRefusal(new List<RoadSpatialGrid.PlannedPath> { Pitched(600f), Pitched(630f) }, 0.35f, out _));
        Assert.Single(RoadPitches.Record(new List<RoadSpatialGrid.PlannedPath> { Pitched(300f) }, 0.35f));
        // 8 m stored of the island's 10 m: a third pitch of 4 m is past the budget, however far off.
        Assert.Equal(RoadPitches.PitchBudgetRefusal, RoadPitches.PitchRefusal(new List<RoadSpatialGrid.PlannedPath> { Pitched(900f) }, 0.35f, out _));

        var plain = Pitched(1200f);
        var level = new RoadSpatialGrid.PlannedPath(plain.Points, plain.Heights, new List<RoadPointDebugInfo>(), 4f, 10f, false)
        { Grades = plain.Grades!.Select(_ => 0.35f).ToList() };
        Assert.Empty(RoadPitches.PitchesOf(new[] { level }, 0.35f));
        Assert.Null(RoadPitches.PitchRefusal(new[] { level }, 0.35f, out _));
        Assert.Equal((2, 8f), (RoadPitches.Stored().count, RoadPitches.Stored().metres));
        return true;
    });

    /// <summary>Off an island worker (a manual road, a test) a road is judged against every pitch the world stores, any island's,
    /// and the world's whole budget, never a fresh ledger: an island's pitch keeps a pitch 150 m off it out, and with the
    /// budget at what is stored, one far off is past it.</summary>
    [Fact]
    public void OffAnIslandWorkerTheWholeWorldsPitchesAndBudgetApply()
    {
        RoadPitches.BeginIsland(1, RoadPitches.PitchBudget);
        Assert.Single(CrossIslandReads.On(1, () => RoadPitches.Record(new List<RoadSpatialGrid.PlannedPath> { Pitched(0f) }, 0.35f)));
        Assert.Equal(0, CrossIslandReads.Me);
        Assert.Equal(RoadPitches.PitchSpacingRefusal, RoadPitches.PitchRefusal(new List<RoadSpatialGrid.PlannedPath> { Pitched(150f) }, 0.35f, out _));
        Assert.Null(RoadPitches.PitchRefusal(new List<RoadSpatialGrid.PlannedPath> { Pitched(300f) }, 0.35f, out _));
        float budget = RoadPitches.PitchBudget;
        try
        {
            RoadPitches.PitchBudget = 6f;
            Assert.Equal(RoadPitches.PitchBudgetRefusal, RoadPitches.PitchRefusal(new List<RoadSpatialGrid.PlannedPath> { Pitched(900f) }, 0.35f, out _));
        }
        finally { RoadPitches.PitchBudget = budget; }
        // Another island does not see island 1's pitch.
        RoadPitches.BeginIsland(2, 10f);
        Assert.Null(CrossIslandReads.On(2, () => RoadPitches.PitchRefusal(new List<RoadSpatialGrid.PlannedPath> { Pitched(150f) }, 0.35f, out _)));
    }

    /// <summary>The ledger as a save keeps it (RoadPitches.Serialize) is read back whole (Load), and no record is no pitch.</summary>
    [Fact]
    public void TheLedgerIsReadBackAsSaved()
    {
        RoadPitches.Record(new List<RoadSpatialGrid.PlannedPath> { Pitched(0f), Pitched(400f) }, 0.35f);
        var before = RoadPitches.Stored();
        byte[] saved = RoadPitches.Serialize();
        RoadPitches.ResetCounters();
        RoadPitches.Load(saved);
        Assert.Equal(before, RoadPitches.Stored());
        Assert.Equal(RoadPitches.PitchSpacingRefusal, RoadPitches.PitchRefusal(new List<RoadSpatialGrid.PlannedPath> { Pitched(550f) }, 0.35f, out _));
        RoadPitches.Load(null);
        Assert.Equal(0, RoadPitches.Stored().count);
    }
}
