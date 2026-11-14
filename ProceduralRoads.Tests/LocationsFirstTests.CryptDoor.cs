using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A crypt's road ends in front of its door: the crypts are placed before their roads wherever they are (their door
/// turns with them) and reached only on their door landing (<see cref="PlacedLocationShape.DoorLandings"/>). On one test world, 0 of 49
/// reached crypts had their road at the door: every road stopped at the edge of everything the crypt paints or levels
/// (Crypt2 16 m, Crypt3 20 m, Crypt4 38 m from the door, median).
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>Crypt4's levelling as its prefab has it: its mound (3 m up 5 m round its centre, smoothed to 4 m out to 15 m,
    /// cleared out to 30 m) and the three sinks that bring the ground in front of its door back to its root.</summary>
    private static readonly IReadOnlyList<LevelOp> Crypt4Ops = new[]
    {
        new LevelOp(0f, 0f, 0f, 0f, false, 30f, 2f), new LevelOp(0f, 0f, 3f, 5f, false, 0f, 20f), new LevelOp(0f, 0f, 4f, 0f, false, 15f, 2f),
        new LevelOp(0f, 12f, 0f, 0f, false, 3.5f, 10f), new LevelOp(0f, 8.6f, 0f, 0f, false, 2.45f, 5.1f), new LevelOp(0f, 7.338f, 0f, 0f, false, 2f, 5.1f),
    };

    private PlacedLocation Crypt(string prefab, IReadOnlyList<LevelOp> ops, float yaw, float exterior)
    {
        var record = new PlacedLocationRecord
        {
            Prefab = prefab, Centre = new Vector2(0f, 0f), Yaw = yaw, Root = 50f, RockRadius = exterior,
            Ops = new List<LevelOp>(ops), X0 = -60, Z0 = -60, W = 121, H = 121,
        };
        Func<float, float, float> ground = PlacedLocationGround.Sampler(record.Ground((x, z) => _world.GetHeight(x, z)), record.X0, record.Z0, record.W, record.H);
        LevelOp flat = RoadStairRuns.FlatOf(ops) ?? PlacedLocationShape.DoorFlat;
        return new PlacedLocation { Record = record, Ground = ground, Shape = PlacedLocationShape.Of(record.Centre, yaw, flat, exterior, null, ground, false, record.Root, prefab) };
    }

    /// <summary>Each crypt's landing is in front of its door, on whichever side of its prefab the door is, and the location faces
    /// out of it: Crypt4 out of its front, Crypt3 at the top of its own stairs behind, Crypt2 out of its back. Before, a crypt's
    /// landing (had it been placed) was its flat's edge: Crypt3's on the far side from its stairs.</summary>
    [Fact]
    public void ACryptsLandingIsInFrontOfItsDoor()
    {
        // Turned to 90 degrees: its prefab's +z faces +x.
        PlacedLocation crypt4 = Crypt("Crypt4", Crypt4Ops, 90f, 18f);
        Assert.True(Vector2.Distance(crypt4.Shape.Landing, new Vector2(11.5f, 0f)) < 0.01f, $"Crypt4 landing {crypt4.Shape.Landing}");
        Assert.True(Vector2.Distance(crypt4.Shape.Front, new Vector2(1f, 0f)) < 0.01f);

        var crypt3Ops = new[] { new LevelOp(-2.07f, -8.4f, 0f, 4f, false, 6f, 3f), new LevelOp(0.01f, 5.51f, 0f, 5f, false, 7f, 3f), new LevelOp(0f, 3.53f, -3f, 3f, false, 5f, 3f) };
        PlacedLocation crypt3 = Crypt("Crypt3", crypt3Ops, 90f, 11f);
        Assert.True(Vector2.Distance(crypt3.Shape.Landing, new Vector2(-10.5f, 0f)) < 0.01f, $"Crypt3 landing {crypt3.Shape.Landing}");
        Assert.True(Vector2.Distance(crypt3.Shape.Front, new Vector2(-1f, 0f)) < 0.01f);

        // Crypt2 only smooths round its centre: no flat, its door all the same.
        PlacedLocation crypt2 = Crypt("Crypt2", new[] { new LevelOp(0.59f, 0.31f, 0f, 0f, false, 11.24f, 3f) }, 90f, 12f);
        Assert.True(Vector2.Distance(crypt2.Shape.Landing, new Vector2(-8.5f, -0.5f)) < 0.01f, $"Crypt2 landing {crypt2.Shape.Landing}");

        // Placed first wherever it is, turned at random.
        Assert.True(PlacedLocations.NeedsPlacing("Crypt3", false, true, Heightmap.Biome.Swamp, crypt3Ops));
        Assert.True(PlacedLocations.NeedsPlacing("Crypt2", false, true, Heightmap.Biome.BlackForest, null));
        Assert.True(PlacedLocations.NeedsPlacing("Hildir_crypt", false, true, Heightmap.Biome.BlackForest, Crypt4Ops));
    }

    /// <summary>A Crypt4 in the Black Forest placed before its road, its door facing +x, the road coming from behind it: the road goes
    /// round and onto the ground in front of its door, never over the crypt, and only that reaches it. On a test world a Crypt4's
    /// road stopped 38 m from its door, at the edge of the ground it clears.</summary>
    [Fact]
    public void ACryptsRoadEndsInFrontOfItsDoor()
    {
        _world.Biome = Heightmap.Biome.BlackForest;
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        StairStructures.Reset();
        PlacedLocations.Reset();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), 30f) });
        PlacedLocation crypt = Crypt("Crypt4", Crypt4Ops, 90f, 18f);
        PlacedLocations.Ready(crypt);
        LocationLevelling.Source = c => c.magnitude < 0.5f ? Crypt4Ops : null;
        SetPathfinder(new RoadPathfinder(_world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });

        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-120f, 10f), 0f, new Vector2(0f, 0f), 18f, 4f, "to the crypt"),
            "last refusal: " + (RoadSpatialGrid.LastRefusal ?? "none"));

        var road = RoadSpatialGrid.GetRoadPointsNearPosition(Vector3.zero, 60f).Where(p => p.IsRoad).ToList();
        float toLanding = road.Min(p => Vector2.Distance(p.p, crypt.Shape.Landing));
        Assert.True(toLanding < 1.5f, $"the road ends {toLanding:F1} m from the door's landing");
        // The door's face is 7.5 m out on +x; nothing of the road comes over the crypt behind its landing.
        Assert.DoesNotContain(road, p => p.p.magnitude < 11f);
        float landing = crypt.Ground(crypt.Shape.Landing.x, crypt.Shape.Landing.y);
        Assert.All(road.Where(p => Vector2.Distance(p.p, crypt.Shape.Landing) < 6f),
            p => Assert.InRange(p.h, landing - RoadNetworkGenerator.ReachRise, landing + RoadNetworkGenerator.ReachRise));
        Assert.Empty(StairStructures.Runs);
        Assert.True(PlacedLocations.Landed(crypt.Centre));
        Assert.True(RoadNetworkGenerator.Reached(Vector3.zero, 18f));
    }

    /// <summary>A road passing a placed crypt within reach of its ring, level with it, does not reach it: only one in front of
    /// its door does (it reached a crypt before, the road 38 m from the door).</summary>
    [Fact]
    public void ARoadPassingACryptDoesNotReachIt()
    {
        _world.Biome = Heightmap.Biome.BlackForest;
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), 30f) });
        PlacedLocation crypt = Crypt("Crypt4", Crypt4Ops, 90f, 18f);
        PlacedLocations.Ready(crypt);
        var points = new List<Vector2> { new(-40f, -34f), new(0f, -34f), new(40f, -34f) };
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(_ => 50f), points.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 80f, false));

        Assert.False(RoadNetworkGenerator.Reached(Vector3.zero, 18f));
        RoadSpatialGrid.Clear();
    }
}
