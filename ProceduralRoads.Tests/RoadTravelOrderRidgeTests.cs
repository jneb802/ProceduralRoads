using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The ridge and trench judge reads a road's pieces as one line in travel order (RoadNetworkGenerator.InTravelOrder, asked by the
/// one list, RoadNetworkGenerator.RoadRefusal, and by a way in as it is planned, RoadNetworkGenerator.PlanWayIn): a way in at the
/// road's start comes reversed ahead of its pieces. Offline, a road to a MountainCave02 was cut back into its own trench and its way
/// in run from there: the trench ran 8.7 m along the road and 5.5 m along the way in, read as two stretches (the way in after the
/// road, its first point at the road's start, so RoadRidges.Join started the line again), each under RoadRidges.MinLength, and it
/// was stored; read as the one line it is, 14.2 m.
/// </summary>
[Collection("RoadStatics")]
public sealed class RoadTravelOrderRidgeTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private const float Ground = 50f;

    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => Ground;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    public RoadTravelOrderRidgeTests()
    {
        _scope.WithWorld(new FlatWorld());
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        PlacedLocations.Reset();
    }

    public void Dispose()
    {
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.Reset();
        PlacedLocations.Reset();
        _scope.Dispose();
    }

    /// <summary>The live road's start (5351.3, -5921.4) and its way in's end (5372.0, -5897.0).</summary>
    private static readonly Vector2 Start = new(5351.3f, -5921.4f), WayInEnd = new(5372.0f, -5897.0f);

    /// <summary>A piece from <paramref name="from"/> along <paramref name="along"/> for <paramref name="length"/> m, a point a metre,
    /// <paramref name="depth"/> m under the ground for its first <paramref name="sunk"/> m, on the ground after.</summary>
    private static RoadSpatialGrid.PlannedPath Piece(Vector2 from, Vector2 along, float length, float sunk, float depth)
    {
        var points = new List<Vector2>();
        var heights = new List<float>();
        for (int k = 0; k <= Mathf.RoundToInt(length); k++)
        {
            points.Add(from + along * k);
            heights.Add(k <= sunk ? Ground - depth : Ground);
        }
        return new RoadSpatialGrid.PlannedPath(points, heights, points.ConvertAll(_ => default(RoadPointDebugInfo)), RoadNetworkGenerator.RoadWidth,
            points.Count - 1, false);
    }

    /// <summary>The road away from its start (8.7 m of it 4.26 m deep) and its way in from the same start (5.5 m of it 5.25 m deep), as
    /// the live numbers read.</summary>
    private static (List<RoadSpatialGrid.PlannedPath> road, RoadSpatialGrid.PlannedPath wayIn) TheRoadAndItsWayIn()
    {
        Vector2 toWayIn = (WayInEnd - Start).normalized;
        var road = new List<RoadSpatialGrid.PlannedPath> { Piece(Start, -toWayIn, 300f, 8.7f, 4.26f) };
        RoadSpatialGrid.PlannedPath wayIn = Piece(Start, toWayIn, Vector2.Distance(Start, WayInEnd), 5.5f, 5.25f);
        return (road, wayIn);
    }

    /// <summary>The road and a way in at its start, read in travel order: one trench, 14.2 m, refused "in a trench". Assembled as the
    /// one list read them before (the road's pieces, then its ways in), the trench was two short stretches and nothing was refused.</summary>
    [Fact]
    public void AWayInAtARoadsStartIsReadAheadOfItsPiecesAndItsTrenchIsRefused()
    {
        var (road, wayIn) = TheRoadAndItsWayIn();

        string? inOrder = RoadNetworkGenerator.AlongARidge(RoadNetworkGenerator.InTravelOrder(road, new[] { wayIn }), new List<RoadCrossing>(), out Vector2 at, out string reason);
        string? asBefore = RoadNetworkGenerator.AlongARidge(road.Concat(new[] { wayIn }).ToList(), new List<RoadCrossing>(), out _, out _);

        Assert.True(inOrder != null, "the trench across the road's start is not refused");
        Assert.Equal(RoadNetworkGenerator.TrenchRoadRefusal, reason);
        Assert.True(Vector2.Distance(at, Start) < 10f, FormattableString.Invariant($"refused at ({at.x:F1}, {at.y:F1})"));
        Assert.Null(asBefore);
    }

    /// <summary>One verdict for the stored line and the walked pieces (rule 13): the road and its way in stored as one line (the way in
    /// run toward the road, then the road) and walked as pieces in travel order give the same stretch.</summary>
    [Fact]
    public void TheStoredLineAndTheWalkedPiecesGiveTheSameRidgeVerdict()
    {
        var (road, wayIn) = TheRoadAndItsWayIn();
        RoadSpatialGrid.PlannedPath back = wayIn.Reversed();
        var points = new List<Vector2>(back.Points);
        var heights = new List<float>(back.Heights);
        points.AddRange(road[0].Points.Skip(1));
        heights.AddRange(road[0].Heights.Skip(1));
        var stored = new RoadSpatialGrid.PlannedPath(points, heights, points.ConvertAll(_ => default(RoadPointDebugInfo)), RoadNetworkGenerator.RoadWidth,
            points.Count - 1, false);

        RoadRidges.Stretch line = Assert.Single(RoadNetworkGenerator.Ridges(new List<RoadSpatialGrid.PlannedPath> { stored }, new List<RoadCrossing>()));
        RoadRidges.Stretch walked = Assert.Single(RoadNetworkGenerator.Ridges(RoadNetworkGenerator.InTravelOrder(road, new[] { wayIn }), new List<RoadCrossing>()));

        Assert.Equal(line.Kind, walked.Kind);
        Assert.Equal(line.Length, walked.Length, 3);
        Assert.Equal(line.Worst, walked.Worst, 3);
    }

    /// <summary>A way in at a road's start as PlanWayIn makes it: run from its landing to the joint, its last point the road's start.
    /// Read in travel order it is not turned, however short (a house's way in 1.5 m long, its landing as near the start as its joint,
    /// within RoadRidges.Link), so the line runs landing, joint, road, each piece meeting the one before at its end, never folded back
    /// at the joint. Before, its first point within RoadRidges.Link of the start turned it: joint, landing, then the road from the
    /// joint.</summary>
    [Theory]
    [InlineData(1.5f)]
    [InlineData(2.9f)]
    [InlineData(20f)]
    public void AWayInAtARoadsStartAsPlanWayInMakesItIsReadUnfolded(float length)
    {
        Vector2 toWayIn = (WayInEnd - Start).normalized;
        var road = new List<RoadSpatialGrid.PlannedPath> { Piece(Start, -toWayIn, 300f, 0f, 0f) };
        Vector2 landing = Start + toWayIn * length;
        int n = Mathf.Max(1, Mathf.CeilToInt(length));
        var points = Enumerable.Range(0, n + 1).Select(k => Vector2.Lerp(landing, Start, (float)k / n)).ToList();
        var wayIn = new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(_ => Ground), points.ConvertAll(_ => default(RoadPointDebugInfo)),
            RoadNetworkGenerator.RoadWidth, points.Count - 1, false);

        List<RoadSpatialGrid.PlannedPath> ordered = RoadNetworkGenerator.InTravelOrder(road, new[] { wayIn });

        Assert.Equal(2, ordered.Count);
        Assert.Same(wayIn, ordered[0]);
        for (int i = 1; i < ordered.Count; i++)
        {
            Vector2 was = ordered[i - 1].Points[ordered[i - 1].Points.Count - 1], next = ordered[i].Points[0];
            Assert.True(Vector2.Distance(was, next) < 1e-3f, FormattableString.Invariant($"piece {i} starts at ({next.x:F1}, {next.y:F1}), not where piece {i - 1} ends ({was.x:F1}, {was.y:F1})"));
        }
        // Stored joint first, it is turned to the same line.
        List<RoadSpatialGrid.PlannedPath> turned = RoadNetworkGenerator.InTravelOrder(road, new[] { wayIn.Reversed() });
        Assert.Equal(landing, turned[0].Points[0]);
        Assert.Equal(Start, turned[0].Points[turned[0].Points.Count - 1]);
    }

    /// <summary>The live trench with its way in as PlanWayIn makes it (landing to joint): the same trench, refused at the same place,
    /// as with the way in stored joint first.</summary>
    [Fact]
    public void AWayInAtARoadsStartGivesTheSameTrenchVerdictInEitherOrientation()
    {
        var (road, wayIn) = TheRoadAndItsWayIn();
        string? jointFirst = RoadNetworkGenerator.AlongARidge(RoadNetworkGenerator.InTravelOrder(road, new[] { wayIn }), new List<RoadCrossing>(), out Vector2 a, out _);
        string? landingFirst = RoadNetworkGenerator.AlongARidge(RoadNetworkGenerator.InTravelOrder(road, new[] { wayIn.Reversed() }), new List<RoadCrossing>(), out Vector2 b, out _);
        Assert.NotNull(jointFirst);
        Assert.Equal(jointFirst, landingFirst);
        Assert.True(Vector2.Distance(a, b) < 1e-3f);
    }

    /// <summary>One assembly of a road's own shape (rule 13): the final check (RoadRefusal, the way in among the road's ways onto
    /// landings) and a way in as it is planned (PlanWayIn, through ShapeRefusal) give the live trench the same refusal at the same
    /// place, with the same inputs (the way in among its extras, its crossings, read in travel order).</summary>
    [Fact]
    public void TheFinalCheckAndAWayInAskTheOneAssemblyOfARoadsShape()
    {
        var (road, wayIn) = TheRoadAndItsWayIn();
        var landingFirst = wayIn.Reversed();
        var planned = new RoadNetworkGenerator.PlannedRoad { Plans = road };
        planned.Entries.Add((null!, landingFirst, null, null));

        RoadNetworkGenerator.Refusal? final = RoadNetworkGenerator.RoadRefusal(planned, out _);
        RoadNetworkGenerator.Refusal? asked = RoadNetworkGenerator.ShapeRefusal(road, new[] { landingFirst }, new List<RoadCrossing>(), null,
            RoadNetworkGenerator.LinesAtEnds(road, new List<RoadCrossing>(), Array.Empty<RoadStairSpan>()));

        Assert.NotNull(asked);
        Assert.NotNull(final);
        Assert.Equal(RoadNetworkGenerator.TrenchRoadRefusal, asked!.Reason);
        Assert.Equal(asked.Reason, final!.Reason);
        Assert.Equal(asked.Said, final.Said);
        Assert.True(Vector2.Distance(asked.At, final.At) < 1e-3f);
    }

    /// <summary>A piece run the other way keeps each edge's limit on its own edge (PlannedPath.Grades: entry i the edge from point
    /// i - 1 to point i), not shifted one edge along, and its junctions' "start" and "end" swap with its ends.</summary>
    [Fact]
    public void APieceRunTheOtherWayKeepsEachEdgesLimitOnItsEdge()
    {
        RoadSpatialGrid.PlannedPath piece = Piece(Start, new Vector2(1f, 0f), 3f, 0f, 0f);
        piece.Grades = new List<float> { 0.9f, 0.1f, 0.2f, 0.3f };
        piece.Junctions = new List<RoadSpatialGrid.Junction> { new(Start, 1f, Start, 1f, "start") };

        RoadSpatialGrid.PlannedPath back = piece.Reversed();

        for (int i = 1; i < piece.Points.Count; i++)
        {
            // Original edge (i - 1, i) is the reversed edge (n - 1 - i, n - i), stored at n - i.
            int j = piece.Points.Count - i;
            Assert.Equal(piece.Points[i - 1], back.Points[j]);
            Assert.Equal(piece.Points[i], back.Points[j - 1]);
            Assert.Equal(piece.Grades[i], back.Grades![j]);
        }
        Assert.Equal(piece.Grades, back.Reversed().Grades);
        Assert.Equal("end", Assert.Single(back.Junctions!).Where);
    }
}
