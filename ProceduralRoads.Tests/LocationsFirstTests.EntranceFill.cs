using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The ground under a placed Location's own pieces, which no road's write moves by more than a step either way
/// (RoadNetworkGenerator.UnderALocationsPieces, the final check's verdict, also asked by a way in as it is planned).
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>A Dvergr guard tower (its entrance 13.5 m out of its front, toward the road's start at (0, 120)) whose levelling
    /// raises a pad <paramref name="pad"/> over a hillside that rises <paramref name="slope"/> toward it: inside 16 m the pad, out
    /// to 16 + pad / <paramref name="batter"/> its batter down to the hillside. <paramref name="pieces"/>: its own built pieces.</summary>
    private PlacedLocation TowerOnAPad(float slope, float pad, float batter, IReadOnlyList<LocationRock>? pieces)
    {
        const string tower = "Mistlands_GuardTower1_ruined_new";
        var world = new MoundWorld { Biome = Heightmap.Biome.Mistlands };
        float Hill(float x, float z) => 50f + slope * (120f - Mathf.Max(16f, new Vector2(x, z).magnitude));
        world.Ground = (x, z) => Hill(x, z) + Mathf.Clamp(pad - batter * (new Vector2(x, z).magnitude - 16f), 0f, pad);
        _scope.WithWorld(world);
        RoadNetworkGenerator.Reset();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), Footprint) });
        PlacedLocations.Reset();
        var flat = new List<LevelOp> { PlacedLocationShape.DoorFlat };
        var record = new PlacedLocationRecord
        {
            Prefab = tower, Centre = new Vector2(0f, 0f), Yaw = 0f, Root = world.Ground(0f, 0f), RockRadius = 0f,
            Ops = flat, X0 = -60, Z0 = -60, W = 121, H = 121,
        };
        Func<float, float, float> ground = world.Ground;
        PlacedLocationShape fallback = PlacedLocationShape.Of(record.Centre, record.Yaw, PlacedLocationShape.DoorFlat, 0f, pieces, ground, false, record.Root, tower, bodyKnown: true);
        PlacedLocationShape entrance = PlacedLocationShape.Of(record.Centre, record.Yaw, PlacedLocationShape.DoorFlat, 0f, pieces, ground, false, record.Root,
            tower, entrance: 0, bodyKnown: true);
        var location = new PlacedLocation { Record = record, Ground = ground, Shape = entrance, Fallback = fallback, Entrances = new[] { entrance } };
        PlacedLocations.Ready(location);
        LocationLevelling.Source = c => c.magnitude < 0.5f ? flat : null;
        SetPathfinder(new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        return location;
    }

    private static string EntranceState()
    {
        var t = typeof(EntranceArrivals);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var laid = (System.Collections.IEnumerable)t.GetField("s_laid", flags)!.GetValue(null)!;
        var failed = (System.Collections.IEnumerable)t.GetField("s_failed", flags)!.GetValue(null)!;
        return "laid " + string.Join(",", laid.Cast<object>()) + " failed " + string.Join(",", failed.Cast<object>());
    }

    /// <summary>The hillside rising 0.2 toward the tower, its pad 5 m over it with a batter of 2 in 1: from the road's ring no road
    /// climbs onto the entrance's landing within the cap, from 12 or 24 m back none within the way in's fill limit, and a staircase
    /// fits.</summary>
    private const float PadSlope = 0.2f, PadHeight = 5f, PadBatter = 2f;

    private static float OnThePad(float x, float z) => 50f + PadSlope * (120f - Mathf.Max(16f, new Vector2(x, z).magnitude)) + PadHeight;

    private static LocationRock Piece(Vector2 at, float ground) =>
        new(at, 0.25f, 0.25f, 0f, ground, ground + 2f, "dvergrprops_wood_wall", built: true);

    private static bool ToTheTower(PlacedLocation tower, out string state)
    {
        EntranceArrivals.Reset();
        bool built = RoadNetworkGenerator.GenerateRoad(new Vector2(0f, 120f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the tower");
        state = FormattableString.Invariant(
            $"built {built}, landed {PlacedLocations.Landed(tower.Centre)}, staircases {StairStructures.Runs.Count}; last refusal {RoadSpatialGrid.LastRefusal}; {EntranceState()}");
        return built;
    }

    /// <summary>The ground under each of the tower's pieces as the roads leave it, against the ground the tower leaves there: moved a
    /// step (StairLayout.StepUp) at most either way.</summary>
    private static void PiecesGroundKept(PlacedLocation tower)
    {
        WorldGenerator world = WorldGenerator.instance!;
        Func<int, int, float> finished = RoadTerrainModifier.FinishedGround((x, z) => BiomeBlendedHeight.GetBlendedHeight(x, z, world));
        int read = 0;
        foreach (LocationRock piece in tower.Shape.Body)
            foreach (var (x, z) in RoadNetworkGenerator.PieceVertices(piece, tower.Ground))
            {
                float rise = finished(x, z) - tower.Ground(x, z);
                Assert.True(StairLayout.Walkable(rise, 0f),
                    FormattableString.Invariant($"the ground under its {piece.Name} at ({piece.At.x:F1}, {piece.At.y:F1}) moved {rise:F2} m at ({x}, {z})"));
                read++;
            }
        Assert.True(read > 0, "no vertex under its pieces read");
    }

    /// <summary>
    /// The same tower with a piece on the way in, 2.25 m off the road's line (clear of it by the entrance's clearance), on the pad's
    /// batter where a way on works the ground: whatever way the arrival takes (a staircase onto the entrance, else the fallback
    /// landing), the piece's ground is as the tower left it.
    /// </summary>
    [Fact]
    public void AnArrivalBesideAPieceOnTheWayInLeavesItsGroundAsTheLocationLeftIt()
    {
        PlacedLocation probe = TowerOnAPad(PadSlope, PadHeight, PadBatter, null);
        var at = new Vector2(2f, 13f);
        var pieces = new[] { Piece(at, probe.Ground(at.x, at.y)) };
        PlacedLocation tower = TowerOnAPad(PadSlope, PadHeight, PadBatter, pieces);
        ToTheTower(tower, out string state);
        Assert.True(PlacedLocations.Landed(tower.Centre), state);
        Assert.True(StairStructures.Runs.Count == 1 || state.Contains("(-1, "), state);
        PiecesGroundKept(tower);
    }

    /// <summary>The verdict's own rule, at a piece a way in passes beside on the tower's pad: one rule for every piece, standing in
    /// the dirt or up on masonry (a crypt's ground torch 1.45 m over its levelled ground): its ground moved a step
    /// (StairLayout.StepUp) or less either way does not move it, more than a step either way does.</summary>
    [Theory]
    [InlineData(-0.3f, 0f, true)]
    [InlineData(-0.05f, 0f, false)]
    [InlineData(0.15f, 0f, false)]
    [InlineData(0.3f, 0f, true)]
    [InlineData(-0.05f, 1.45f, false)]
    [InlineData(-0.3f, 1.45f, true)]
    public void AWayInMayMoveAPiecesGroundAStepEitherWayAndNoMore(float roadOver, float pieceOver, bool refused)
    {
        var at = new Vector2(1.5f, 10f);
        PlacedLocation tower = TowerOnAPad(PadSlope, PadHeight, PadBatter, new[] { Piece(at, OnThePad(at.x, at.y) + pieceOver) });
        // A way in level across the pad, roadOver over it, past the piece.
        var points = new List<Vector2> { new(-0.25f, 4f), new(-0.25f, 8f), new(-0.25f, 12f), new(-0.25f, 15f) };
        var entry = new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(p => OnThePad(p.x, p.y) + roadOver), points.ConvertAll(_ => default(RoadPointDebugInfo)),
            4f, 11f, false) { Locations = new RoadLocationProtection.Targets(null, tower.Centre) };
        string? why = RoadNetworkGenerator.UnderALocationsPieces(entry, out _);
        Assert.True((why != null) == refused, FormattableString.Invariant($"{roadOver:F2} over, the piece {pieceOver:F2} up: {why ?? "allowed"}"));
    }

    /// <summary>Only a Location's built pieces hold their ground: a large rock of its own (its box from its colliders' corners,
    /// which overstates a rough rock) at the same place on the pad, under a way in raised past a step over it, refuses nothing; the
    /// built piece there is refused.</summary>
    [Fact]
    public void OnlyALocationsBuiltPiecesHoldTheirGroundNotItsRocks()
    {
        var at = new Vector2(1.5f, 10f);
        var rock = new LocationRock(at, 3f, 3f, 0f, OnThePad(at.x, at.y), OnThePad(at.x, at.y) + 4f, "cliff_mistlands1");
        Assert.True(rock.Large);
        var points = new List<Vector2> { new(-0.25f, 4f), new(-0.25f, 8f), new(-0.25f, 12f), new(-0.25f, 15f) };
        RoadSpatialGrid.PlannedPath Entry(PlacedLocation tower) =>
            new(points, points.ConvertAll(p => OnThePad(p.x, p.y) + 0.3f), points.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 11f, false)
            { Locations = new RoadLocationProtection.Targets(null, tower.Centre) };
        PlacedLocation withRock = TowerOnAPad(PadSlope, PadHeight, PadBatter, new[] { rock });
        Assert.Null(RoadNetworkGenerator.UnderALocationsPieces(Entry(withRock), out _));
        PlacedLocation withPiece = TowerOnAPad(PadSlope, PadHeight, PadBatter, new[] { Piece(at, OnThePad(at.x, at.y)) });
        Assert.NotNull(RoadNetworkGenerator.UnderALocationsPieces(Entry(withPiece), out _));
    }
}
