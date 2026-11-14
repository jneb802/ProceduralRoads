using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A road whose stretches stand side by side a step apart that no one walks up is refused (RoadNetworkGenerator.WalkStep, in
/// the one list). Live numbers: a road's end on a Mistlands hillside and its way onto a guard tower's landing left the same
/// point 73 degrees apart, one down and one up at the grade cap, and stood 0.77 m apart in height 1.3 m apart across the
/// corner. Two points meet as two pieces of road meet (RoadNetworkGenerator.PiecesMeet: within the grade cap over the
/// distance or within a step, never the two summed); two points of one piece within the reach along it are steps of that
/// piece, its grade judge's on the schedule it was limited on (a last-resort pitch's edges at their own grade).
/// </summary>
[Collection("RoadStatics")]
public sealed class WalkStepRefusalTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private readonly IDisposable _grade = GradeCap.At(0.35f);

    public WalkStepRefusalTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
    }

    public void Dispose()
    {
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.Reset();
        _grade.Dispose();
        _scope.Dispose();
    }

    private static RoadSpatialGrid.PlannedPath Piece(params (float x, float z, float y)[] at) => new(
        at.Select(p => new Vector2(p.x, p.z)).ToList(), at.Select(p => p.y).ToList(), new List<RoadPointDebugInfo>(), 4f, 0f, false);

    private static string? Step(params RoadSpatialGrid.PlannedPath[] plans) => RoadNetworkGenerator.WalkStep(plans.ToList(), out _);

    // The road's end, from down the hill up to the point it ends at.
    private static readonly RoadSpatialGrid.PlannedPath End = Piece(
        (7431.83f, 4207.94f, 85.25f), (7430.95f, 4207.50f, 85.59f), (7430.60f, 4207.33f, 85.73f), (7429.77f, 4206.94f, 86.05f),
        (7428.94f, 4206.54f, 86.37f), (7428.12f, 4206.15f, 86.69f), (7427.29f, 4205.75f, 87.01f), (7426.47f, 4205.36f, 87.33f),
        (7425.64f, 4204.96f, 87.65f), (7424.82f, 4204.57f, 87.97f), (7423.96f, 4204.10f, 88.31f));

    // Its way onto the landing, from beside that end up to the location.
    private static readonly RoadSpatialGrid.PlannedPath WayOnto = Piece(
        (7424.36f, 4203.73f, 88.50f), (7424.86f, 4203.27f, 88.74f), (7425.43f, 4202.73f, 88.91f), (7426.07f, 4202.13f, 89.08f),
        (7426.78f, 4201.47f, 89.27f), (7427.54f, 4200.76f, 89.52f), (7428.34f, 4200.02f, 89.91f), (7429.18f, 4199.23f, 90.31f));

    [Fact]
    public void TwoStretchesAreReadAPlannedPointsSpacingAndAHalfApart() => Assert.Equal(1.5f, RoadNetworkGenerator.WalkStepReach);

    [Fact]
    public void AnEndAndItsWayOntoALandingAStepApartAcrossTheirCornerAreRefused()
    {
        string? step = RoadNetworkGenerator.WalkStep(new List<RoadSpatialGrid.PlannedPath> { End, WayOnto }, out Vector2 at);
        Assert.NotNull(step);
        Assert.StartsWith(RoadNetworkGenerator.WalkStepRefusal, step);
        Assert.True(Vector2.Distance(at, new Vector2(7424.8f, 4204.0f)) < 1.5f, $"at {at}");
    }

    [Fact]
    public void EachStretchAloneIsWalked()
    {
        Assert.Null(Step(End));
        Assert.Null(Step(WayOnto));
    }

    /// <summary>Two stretches side by side 0.5 m apart in height a metre apart: neither within the cap's climb (0.35 m) nor
    /// within a step (0.2 m). Before, the two allowances were summed (0.35 + 0.2) and it passed. A step apart, they meet.</summary>
    [Fact]
    public void TwoStretchesHalfAMetreApartAMetreApartAreRefusedAndAStepApartMeet()
    {
        var level = Piece((0f, 0f, 40f), (1f, 0f, 40f), (2f, 0f, 40f));
        Assert.StartsWith(RoadNetworkGenerator.WalkStepRefusal, Step(level, Piece((0f, 1f, 40.5f), (1f, 1f, 40.5f), (2f, 1f, 40.5f))));
        Assert.Null(Step(level, Piece((0f, 1f, 40.2f), (1f, 1f, 40.2f), (2f, 1f, 40.2f))));
    }

    /// <summary>A 50% last-resort pitch on its schedule, points <paramref name="spacing"/> apart, is walked at any spacing: two
    /// points of it within the reach along it are its steps, its grade judge's. Before, each pair was allowed the cap's climb
    /// and a step: at 0.7 to 0.75 m a point (two points 1.4 to 1.5 m apart) and at 1.4 m it was refused.</summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(0.75f)]
    [InlineData(0.7f)]
    [InlineData(1.4f)]
    public void APitchOnItsScheduleIsWalkedAtAnySpacing(float spacing)
    {
        Assert.Null(Step(Pitch(spacing, 0f, scheduled: true)));
    }

    /// <summary>A piece folding back on itself, its two legs side by side a metre apart and half a metre apart in height: a
    /// fold is two stretches, refused however its grade climbs along it.</summary>
    [Fact]
    public void AFoldOfOnePieceIsTwoStretches()
    {
        var fold = new List<(float, float, float)>();
        for (int i = 0; i <= 5; i++) fold.Add((i, 0f, 40f + 0.1f * i));
        fold.Add((5.5f, 0.5f, 40.6f));
        for (int i = 5; i >= 0; i--) fold.Add((i, 1f, 40.7f + 0.1f * (5 - i)));
        Assert.StartsWith(RoadNetworkGenerator.WalkStepRefusal, Step(Piece(fold.ToArray())));
    }

    // --- Through the one list (RoadNetworkGenerator.RoadRefusal), as generation and a marked road ask it. ---

    /// <summary>Level at 40 m; north of z = 50 the ground follows the pitch (<see cref="PitchHeight"/>).</summary>
    private sealed class Ground : WorldGenerator
    {
        public override float GetHeight(float x, float z) => Mathf.Abs(x) > 100f || Mathf.Abs(z) > 100f ? 0f : z > 50f ? PitchHeight(x) : 40f;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
        public override int GetSeed() => 7;
    }

    /// <summary>Level at 40 m west of x = 0, a 50% pitch to 44 m at x = 8, level east of it.</summary>
    private static float PitchHeight(float x) => x <= 0f ? 40f : x >= 8f ? 44f : 40f + 0.5f * x;

    /// <summary>A road at z = <paramref name="z"/> over the pitch, points <paramref name="spacing"/> apart; scheduled: its
    /// edges on the pitch a last-resort pitch's (RoadSpatialGrid.PlannedPath.Grades at 50%), the cap elsewhere.</summary>
    private static RoadSpatialGrid.PlannedPath Pitch(float spacing, float z, bool scheduled)
    {
        var points = new List<Vector2>();
        for (float x = -6f; x <= 14f + 1e-3f; x += spacing) points.Add(new Vector2(x, z));
        var heights = points.Select(p => PitchHeight(p.x)).ToList();
        var path = new RoadSpatialGrid.PlannedPath(points, heights, points.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f,
            points.Zip(points.Skip(1), Vector2.Distance).Sum(), false);
        if (scheduled)
            path.Grades = points.Select((p, i) => i > 0 && points[i - 1].x >= -1e-3f && p.x <= 8f + 1e-3f ? 0.5f : 0.35f).ToList();
        return path;
    }

    private void Begin()
    {
        _scope.WithWorld(new Ground()).WithZdos().WithZoneSystem().WithScene().AsServer();
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.MarkLocationsReady();
        RoadLocationProtection.Set(Array.Empty<RoadLocationProtection.Footprint>());
    }

    private static RoadSpatialGrid.PlannedPath Line(Vector2 from, Vector2 to, float height)
    {
        int n = Mathf.CeilToInt(Vector2.Distance(from, to));
        var points = Enumerable.Range(0, n + 1).Select(k => Vector2.Lerp(from, to, k / (float)n)).ToList();
        return new RoadSpatialGrid.PlannedPath(points, points.Select(_ => height).ToList(), points.Select(_ => default(RoadPointDebugInfo)).ToList(),
            4f, Vector2.Distance(from, to), false);
    }

    /// <summary>The road as each path hands it to the one list: generation's, its end with its way onto a landing laid from
    /// beside it (PlannedRoad.Entries) and location ends with no location; a marked road's (ManualRoads.PrepareCore), its
    /// pieces and path only.</summary>
    private static RoadNetworkGenerator.Refusal? Refused(bool generated, RoadSpatialGrid.PlannedPath end, RoadSpatialGrid.PlannedPath? way)
    {
        var road = new RoadNetworkGenerator.PlannedRoad { Path = new List<Vector2>(end.Points) };
        road.Plans.Add(end);
        if (generated)
        {
            road.Ends = new RoadNetworkGenerator.LocationEndContext(0f, 0f, false, false, null, null, end.Width, 0f, 0f);
            if (way != null)
                road.Entries.Add((new PlacedLocation { Record = new PlacedLocationRecord { Prefab = "Test", Centre = way.Points[way.Points.Count - 1] } }, way, null));
        }
        else if (way != null) road.Plans.Add(way);
        return RoadNetworkGenerator.RoadRefusal(road, out _);
    }

    /// <summary>A road's end level at 40 m and a stretch from beside it level at 40.5 m, a metre across the corner: refused
    /// for the step on generation's path and a marked road's alike; at 40.2 m, a step, built on both.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheOneListRefusesAStepBetweenTwoStretchesOnEveryPath(bool generated)
    {
        Begin();
        RoadSpatialGrid.PlannedPath end = Line(new Vector2(-10f, 0f), new Vector2(0f, 0f), 40f);
        RoadNetworkGenerator.Refusal? refused = Refused(generated, end, Line(new Vector2(0f, 1f), new Vector2(0f, 10f), 40.5f));
        Assert.NotNull(refused);
        Assert.Equal(RoadNetworkGenerator.WalkStepRefusal, refused!.Reason);
        Assert.True(Vector2.Distance(refused.At, new Vector2(0f, 0.5f)) < 1.5f, $"at {refused.At}");

        Begin();
        RoadNetworkGenerator.Refusal? met = Refused(generated, end, Line(new Vector2(0f, 1f), new Vector2(0f, 10f), 40.2f));
        Assert.True(met == null, met?.Said);
    }

    /// <summary>A last-resort pitch on its schedule at 0.75 m a point (a 3 m road's spacing) passes the one list: two points
    /// 1.5 m along it climb 0.75 m, their schedule's climb.</summary>
    [Fact]
    public void TheOneListBuildsAPitchOnItsScheduleAtAnySpacing()
    {
        Begin();
        RoadNetworkGenerator.Refusal? refused = Refused(generated: true, Pitch(0.75f, 60f, scheduled: true), null);
        Assert.True(refused == null, refused?.Said);
    }
}
