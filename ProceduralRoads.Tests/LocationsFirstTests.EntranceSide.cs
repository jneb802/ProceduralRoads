using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A Dvergr town entrance is entered only from its front, its back the blocked rock of the building
/// (<see cref="PlacedLocationShape.FrontOnly"/>): the network at its ring reaches it only on its entrance side
/// (<see cref="PlacedLocationShape.OnEntranceSide"/>). Mistlands_DvergrTownEntrance2 at (5760, 3264), turned to 157.5 and its
/// root at 42.5 as the offline generator placed it, faces south-south-east (its stairs' foot at about (5768.5, 3243.4)); its
/// road ended 41.7 m north-east of its centre at (5789.5, 51.5, 3293.5), 54 m from the stairs on its blocked back, 9 m over its
/// level, and on the fallback the location counted reached off level. In the game its road ended 47 m north at (5759.7, 56.4,
/// 3311.4), in a cave on its back.
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>Mistlands_DvergrTownEntrance2's levelling as its prefab has it: the flat at its stairs' foot (3 m round
    /// (0, 22.25)), the ground under the stairs, and the raised ground and smoothing round the building behind.</summary>
    private static readonly IReadOnlyList<LevelOp> TownEntrance2Ops = new[]
    {
        new LevelOp(14.32f, -15.16f, 0f, 0f, false, 15f, 2f), new LevelOp(3.09f, -22.01f, 0f, 0f, false, 15f, 2f),
        new LevelOp(-11.9f, -18.14f, 0f, 0f, false, 15f, 2f), new LevelOp(7.08f, -15.7f, 10f, 0f, false, 15f, 2f),
        new LevelOp(-8f, -14.19f, 10f, 0f, false, 15f, 2f), new LevelOp(0f, -8f, 8f, 0f, false, 8f, 3f),
        new LevelOp(14f, -7f, 5f, 0f, false, 15f, 2f), new LevelOp(-14f, -1f, 5f, 0f, false, 15f, 2f),
        new LevelOp(0f, 22.25f, 0f, 3f, false, 14f, 2f), new LevelOp(0f, 13f, 1f, 0f, false, 14f, 2f),
    };

    private const float TownEntranceYaw = 157.5f, TownEntranceExterior = 32f, TownEntranceTerrain = 37.226f;
    /// <summary>Its centre in the world, which the test moves to the origin.</summary>
    private static readonly Vector2 TownEntranceAt = new(5760f, 3264f);

    private PlacedLocation TownEntrance2()
    {
        var record = new PlacedLocationRecord
        {
            Prefab = "Mistlands_DvergrTownEntrance2", Centre = new Vector2(0f, 0f), Yaw = TownEntranceYaw, Root = 50f, RockRadius = TownEntranceExterior,
            Ops = new List<LevelOp>(TownEntrance2Ops), X0 = -60, Z0 = -60, W = 121, H = 121,
        };
        Func<float, float, float> ground = PlacedLocationGround.Sampler(record.Ground((x, z) => _world.GetHeight(x, z)), record.X0, record.Z0, record.W, record.H);
        LevelOp flat = RoadStairRuns.FlatOf(record.Ops)!.Value;
        return new PlacedLocation
        {
            Record = record, Ground = ground,
            Shape = PlacedLocationShape.Of(record.Centre, record.Yaw, flat, record.RockRadius, null, ground, false, record.Root, record.Prefab),
        };
    }

    /// <summary>The entrance placed on the level ground (Mistlands), with one straight road through <paramref name="through"/>
    /// (relative to the location's centre) at <paramref name="height"/>, across the line to the centre.</summary>
    private PlacedLocation TownEntranceWithRoad(Vector2 through, float height)
    {
        _world.Biome = Heightmap.Biome.Mistlands;
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        PlacedLocations.Reset();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), TownEntranceTerrain) });
        PlacedLocation location = TownEntrance2();
        PlacedLocations.Ready(location);
        Vector2 across = new Vector2(-through.y, through.x).normalized * 6f;
        var points = new List<Vector2> { through - across, through, through + across };
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(_ => height), points.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 12f, false));
        return location;
    }

    /// <summary>Its front is its stairs' side, as placed: the prefab's +z turned to 157.5, south-south-east, and its landing there.</summary>
    [Fact]
    public void ADvergrTownEntranceFacesOutOfItsStairs()
    {
        PlacedLocation location = TownEntrance2();
        Assert.True(location.Shape.EnteredFromFront);
        Assert.True(Vector2.Distance(location.Shape.Front, new Vector2(0.383f, -0.924f)) < 0.01f, $"front {location.Shape.Front}");
        // Its stairs' foot, (0, 22.25) in the prefab, at (5768.5, 3243.4) in the world.
        Vector2 foot = location.Shape.FlatCentre + TownEntranceAt;
        Assert.True(Vector2.Distance(foot, new Vector2(5768.5f, 3243.4f)) < 0.2f, $"stairs' foot {foot}");
        Assert.True(location.Shape.OnEntranceSide(location.Shape.Landing));
        Assert.False(location.Shape.OnEntranceSide(new Vector2(5789.5f, 3293.5f) - TownEntranceAt));
        Assert.False(location.Shape.OnEntranceSide(new Vector2(5759.7f, 3311.4f) - TownEntranceAt));
        // Other locations are entered from anywhere round their ring.
        Assert.True(Cave().Shape.OnEntranceSide(new Vector2(-30f, 0f)));
        Assert.False(PlacedLocationShape.FrontOnly("Mistlands_DvergrBossEntrance1"));
    }

    /// <summary>The road's end on its blocked back, 9 m over its level and 41.7 m from its centre (at its ring), does not
    /// reach it on the fallback: the location is left unserved, not counted reached off level. Before, it was.</summary>
    [Fact]
    public void ARoadOnADvergrTownEntrancesBackDoesNotReachItOffLevel()
    {
        PlacedLocation location = TownEntranceWithRoad(new Vector2(5789.5f, 3293.5f) - TownEntranceAt, 59f);
        try
        {
            Assert.False(RoadNetworkGenerator.Reached(Vector3.zero, TownEntranceExterior), "reached at height");
            Assert.False(RoadNetworkGenerator.ReachOffLevel("Mistlands_DvergrTownEntrance2", Vector3.zero, TownEntranceExterior, () => false));
            Assert.Equal(0, RoadNetworkGenerator.ReachedOffLevelCount);
            Assert.Equal(0, RoadNetworkGenerator.BehindRidgeCount);
        }
        finally { RoadSpatialGrid.Clear(); }
    }

    /// <summary>A Mistlands road at its ring on its stairs' side reaches it even nine metres above the landing.</summary>
    [Theory]
    [InlineData(52f, true)]
    [InlineData(59f, true)]
    public void ARoadAtADvergrTownEntrancesFrontKeepsTheMistlandsHeightExemption(float height, bool reached)
    {
        PlacedLocation probe = TownEntrance2();
        Vector2 front = probe.Shape.Front * 41.7f;
        PlacedLocation location = TownEntranceWithRoad(front, height);
        try
        {
            Assert.True(Vector2.Distance(front, location.Shape.Landing) > RoadNetworkGenerator.LandingReach, "the road is on the landing");
            Assert.Equal(reached, RoadNetworkGenerator.Reached(Vector3.zero, TownEntranceExterior));
            Assert.False(RoadNetworkGenerator.ReachOffLevel("Mistlands_DvergrTownEntrance2", Vector3.zero, TownEntranceExterior, () => false));
            Assert.Equal(reached, RoadNetworkGenerator.Reached(Vector3.zero, TownEntranceExterior));
            Assert.Equal(0, RoadNetworkGenerator.ReachedOffLevelCount);
        }
        finally { RoadSpatialGrid.Clear(); }
    }

    /// <summary>Level with it, a road on its back does not reach it either, and no road is searched to a door behind it.</summary>
    [Fact]
    public void NoRoadEndsOnADvergrTownEntrancesBack()
    {
        PlacedLocation location = TownEntranceWithRoad(new Vector2(5789.5f, 3293.5f) - TownEntranceAt, 50f);
        try
        {
            Assert.False(RoadNetworkGenerator.Reached(Vector3.zero, TownEntranceExterior));
            var doors = RoadNetworkGenerator.DoorsOf(new Vector2(0f, 0f), TownEntranceExterior, 4f);
            Assert.NotEmpty(doors);
            Assert.All(doors, d => Assert.True(location.Shape.InFront(d.At), $"a door behind it at {d.At}"));
        }
        finally { RoadSpatialGrid.Clear(); }
    }
}
