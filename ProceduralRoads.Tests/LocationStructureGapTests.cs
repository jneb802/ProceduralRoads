using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// An approach onto a placed location keeps its own body off the location's built pieces (<see cref="LocationRock.Built"/>): what the
/// walker would hit, the piece's box against the walker's (LocationRock.InTheWay over the ground), no gap of its own past that; on the
/// way in to its own door (<see cref="PlacedLocationShape.InDoorCorridor"/>) the entrance's clearance. Refused with a reason of its own
/// where it comes too near. A Location's stakes are no obstacle (LocationRock.ClearedForAWay). From a
/// Mistlands Dvergr boss entrance placed at (-3328, 49.5, -5760) turned 225, as read in game: its door landing at
/// (-3345.0, -5774.0), at the foot of its own black marble stairs (pieces turned 195), whose approach was refused at a 3 m gap
/// with no room for the landing ("grade: no room for a staircase foot's level landing").
/// </summary>
public class LocationStructureGapTests
{
    private const string Prefab = "Mistlands_DvergrBossEntrance1";
    private static readonly Vector2 Centre = new(-3328f, -5760f);
    private const float Yaw = 225f, Level = 49.5f, Exterior = 32f, PieceYaw = 195f;
    /// <summary>Its main flat, as its prefab levels it (offset x, z, height, radius, smoothing radius and power).</summary>
    private static readonly LevelOp Flat = new(0f, 6.5f, 1f, 15f, false, 24f, 3f);
    private static readonly Vector2 DoorLanding = new(-3345.0f, -5774.0f);

    /// <summary>The entrance's own pieces nearest its door landing, where the game placed them (x, y, z), each a 2 m piece's box.</summary>
    private static readonly (string name, Vector3 at)[] Pieces =
    {
        ("blackmarble_stair", new Vector3(-3343.91f, 49.50f, -5774.50f)),
        ("blackmarble_2x2x2", new Vector3(-3343.91f, 48.50f, -5774.50f)),
        ("blackmarble_stair", new Vector3(-3342.50f, 49.50f, -5775.91f)),
        ("blackmarble_2x2x2", new Vector3(-3342.50f, 48.50f, -5775.91f)),
        ("blackmarble_stair", new Vector3(-3342.50f, 50.50f, -5773.08f)),
        ("blackmarble_stair", new Vector3(-3341.08f, 50.50f, -5774.50f)),
        ("blackmarble_2x2x2", new Vector3(-3344.06f, 52.50f, -5770.02f)),
        ("blackmarble_base_2", new Vector3(-3344.92f, 50.50f, -5770.52f)),
    };

    private static List<LocationRock> Built(bool built = true)
    {
        var rocks = new List<LocationRock>();
        foreach (var (name, at) in Pieces)
            rocks.Add(new LocationRock(new Vector2(at.x, at.z), 1f, 1f, PieceYaw, at.y - 1f, at.y + 1f, name, built: built));
        return rocks;
    }

    private static PlacedLocationShape Shape(List<LocationRock> rocks) =>
        PlacedLocationShape.Of(Centre, Yaw, Flat, Exterior, rocks, (_, _) => Level, false, Level, Prefab);

    /// <summary>A point in the location's own frame (x across, z out of its front), in the world.</summary>
    private static Vector2 At(float x, float z) => Centre + LocationRock.World(new Vector2(x, z), Yaw);

    /// <summary>What a staircase piece at <paramref name="p"/> runs into, as its approach's layouts are held (ApproachBounds.Kept,
    /// a filler's reach further off).</summary>
    private static string Stair(PlacedLocationShape shape, Vector2 p) =>
        shape.Refusal(p, RoadStairRuns.FillerReach, RoadStairRuns.Width * 0.5f + RoadStairRuns.FillerReach);

    /// <summary>What a road onto the landing at <paramref name="p"/> runs into, as RoadLocationEntry holds it.</summary>
    private static string Road(PlacedLocationShape shape, Vector2 p, float width = 4f) =>
        shape.Refusal(p, RoadLocationEntry.InsideReach(width), width * 0.5f + RoadLocationEntry.HugGap, width * 0.5f + PlacedLocationShape.BodyGap);

    [Fact]
    public void TheDoorLandingAndItsStairsAreAsReadInGame()
    {
        PlacedLocationShape shape = Shape(Built());
        Assert.InRange(Vector2.Distance(shape.Landing, DoorLanding), 0f, 0.1f);
        Assert.Equal(Pieces.Length, shape.Body.Count);
        // Its own bottom stair stands well within the gap of the landing: kept off there too, no staircase or road could land.
        Assert.InRange(shape.Body[0].Distance(DoorLanding), 0f, 0.5f);
    }

    [Fact]
    public void TheDoorLandingIsReachedThroughItsCorridor()
    {
        PlacedLocationShape shape = Shape(Built());
        Assert.True(shape.InDoorCorridor(shape.Door!.Value, 0.5f));
        Assert.Equal("", Stair(shape, DoorLanding));
        Assert.Equal("", Road(shape, DoorLanding));
        // The last stretch onto it from the entrance side, within the network's reach of the landing.
        Vector2 out4 = DoorLanding + shape.Front * 4f;
        Assert.Equal("", Stair(shape, out4));
        Assert.Equal("", Road(shape, out4));
        // A staircase's landing region (ApproachBounds) holds it.
        PlacedLocationShape.LandingRegion region = shape.ConnectedLanding((_, _) => Level, PlacedLocationShape.LevelGroundClearance);
        Assert.True(region.Contains(DoorLanding));
    }

    [Fact]
    public void TheCorridorIsTheLandingsReachOnTheEntranceSideOnly()
    {
        PlacedLocationShape shape = Shape(Built());
        Assert.True(shape.InDoorCorridor(shape.Door!.Value + shape.Front * (RoadNetworkGenerator.LandingReach - 0.1f), 0.5f));
        Assert.False(shape.InDoorCorridor(shape.Door!.Value + shape.Front * (RoadNetworkGenerator.LandingReach + 0.1f), 0.5f));
        // Never nearer the location than its landing: up beside its own stairs.
        Assert.False(shape.InDoorCorridor(shape.Door!.Value - shape.Front * 0.5f, 0.5f));
        // A location without a door has none.
        PlacedLocationShape doorless = PlacedLocationShape.Of(Centre, Yaw, Flat, Exterior, Built(), (_, _) => Level, false, Level, "Mistlands_Excavation3");
        Assert.False(doorless.InDoorCorridor(doorless.Landing));
    }

    [Fact]
    public void AStairHuggingTheStairsOutsideTheCorridorIsKeptOffByItsOwnBody()
    {
        PlacedLocationShape shape = Shape(Built());
        // Beside the entrance's stairs, just behind the landing's line, 2.5 m out from the side stair's middle.
        Vector2 beside = At(-3.5f, 21f);
        Assert.False(shape.InDoorCorridor(beside));
        string why = Stair(shape, beside);
        Assert.StartsWith($"too near {Prefab}'s built pieces (", why);
        Assert.Contains("blackmarble_stair", why);
        Assert.StartsWith($"too near {Prefab}'s built pieces (", Road(shape, beside));
        // The edge: the walker's own body (a staircase piece's half width and a filler's reach) kept off the nearest piece, no gap past it.
        LocationRock side = shape.Body[2];
        float half = RoadStairRuns.Width * 0.5f + RoadStairRuns.FillerReach;
        Vector2 across = LocationRock.World(new Vector2(-1f, 0f), Yaw);
        Vector2 Off(float edge) { Vector2 p = side.At; while (side.Distance(p) < edge) p += across * 0.01f; return p; }
        Assert.Contains("built pieces", Stair(shape, Off(half - 0.1f)));
        Assert.DoesNotContain("built pieces", Stair(shape, Off(half + 0.1f)));
        // A road's edge keeps its body gap (PlacedLocationShape.BodyGap) off it: its half width and that, no further.
        float road = 2f + PlacedLocationShape.BodyGap;
        Assert.Contains("built pieces", Road(shape, Off(road - 0.1f)));
        Assert.DoesNotContain("built pieces", Road(shape, Off(road + 0.1f)));
    }

    /// <summary>A Location's built pieces are kept off at any height: an end may stand next to a light post, never under a piece over
    /// its head. Its stakes (a Dvergr stake wall, LocationRock.ClearedForAWay) are no obstacle at any height: they are not in its body,
    /// and the rock pass removes those a road or staircase goes through (RoadRockHit.Judged).</summary>
    [Fact]
    public void ALocationsStakesAreNoObstacleAtAnyHeightItsOtherPiecesAre()
    {
        Vector2 at = At(-8f, 26f);
        LocationRock Over(string name, float low) => new(at, 1f, 1f, 0f, Level + low, Level + low + 1f, name, built: true);
        string Road(PlacedLocationShape shape) => shape.Refusal(at, RoadLocationEntry.InsideReach(4f), 2f + RoadLocationEntry.HugGap,
            2f + PlacedLocationShape.BodyGap);
        foreach (float low in new[] { 0f, 3f })
        {
            PlacedLocationShape stakes = Shape(new List<LocationRock> { Over("dvergrprops_wood_stakewall", low) });
            Assert.Empty(stakes.Body);
            Assert.DoesNotContain("built pieces", Road(stakes));
            Assert.DoesNotContain("built pieces", Stair(stakes, at));
        }
        Assert.Contains("built pieces", Road(Shape(new List<LocationRock> { Over("dverger_light_post", 3f) })));
        Assert.Contains("built pieces", Road(Shape(new List<LocationRock> { Over("dvergrprops_wood_wall", 0f) })));
    }

    [Fact]
    public void ABoulderInThatPlaceKeepsOnlyItsOwnMargin()
    {
        // The same boxes as boulders round its mouth: kept off by the approach's margin alone, as before.
        PlacedLocationShape shape = Shape(Built(built: false));
        Assert.DoesNotContain("built pieces", Stair(shape, At(-3.5f, 21f)));
    }

    [Fact]
    public void APlacedLocationsFileKeepsWhichPiecesAreBuilt()
    {
        List<LocationRock> rocks = Built();
        rocks.AddRange(Built(built: false));
        var record = new PlacedLocationRecord
        {
            Prefab = Prefab, Centre = Centre, Yaw = Yaw, Root = Level, RockRadius = Exterior,
            Ops = new List<LevelOp> { Flat }, Rocks = rocks,
            X0 = -3400, Z0 = -5830, W = 145, H = 145,
        };
        List<PlacedLocationRecord> read = PlacedLocations.Read(PlacedLocations.Write(new[] { record }));
        Assert.True(read[0].Rocks[0].Built);
        Assert.False(read[0].Rocks[Pieces.Length].Built);
    }
}
