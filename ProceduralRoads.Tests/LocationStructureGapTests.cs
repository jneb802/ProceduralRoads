using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// An approach onto a placed location keeps a clear gap (<see cref="PlacedLocationShape.StructureGap"/>) from the location's own
/// built pieces (<see cref="LocationRock.Built"/>), wherever they stand but on the way in to its own door
/// (<see cref="PlacedLocationShape.InDoorCorridor"/>), and is refused with a reason of its own where it comes too near. From a
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
        Assert.True(shape.InDoorCorridor(DoorLanding));
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
        Assert.True(shape.InDoorCorridor(DoorLanding + shape.Front * (RoadNetworkGenerator.LandingReach - 0.1f)));
        Assert.False(shape.InDoorCorridor(DoorLanding + shape.Front * (RoadNetworkGenerator.LandingReach + 0.1f)));
        // Never nearer the location than its landing: up beside its own stairs.
        Assert.False(shape.InDoorCorridor(DoorLanding - shape.Front * 0.5f));
        // A location without a door has none.
        PlacedLocationShape doorless = PlacedLocationShape.Of(Centre, Yaw, Flat, Exterior, Built(), (_, _) => Level, false, Level, "Mistlands_Excavation3");
        Assert.False(doorless.InDoorCorridor(doorless.Landing));
    }

    [Fact]
    public void AStairHuggingTheStairsOutsideTheCorridorIsRefusedForTheGap()
    {
        PlacedLocationShape shape = Shape(Built());
        // Beside the entrance's stairs, just behind the landing's line, 2.5 m out from the side stair's middle.
        Vector2 beside = At(-3.5f, 21f);
        Assert.False(shape.InDoorCorridor(beside));
        string why = Stair(shape, beside);
        Assert.StartsWith($"too near {Prefab}'s built pieces (", why);
        Assert.Contains("blackmarble_stair", why);
        Assert.StartsWith($"too near {Prefab}'s built pieces (", Road(shape, beside));
        // The gap's own edge: the walker's body and the gap kept off the nearest piece, no further.
        LocationRock side = shape.Body[2];
        float half = RoadStairRuns.Width * 0.5f + RoadStairRuns.FillerReach;
        Vector2 across = LocationRock.World(new Vector2(-1f, 0f), Yaw);
        Vector2 Off(float edge) { Vector2 p = side.At; while (side.Distance(p) < edge) p += across * 0.01f; return p; }
        Assert.Contains("built pieces", Stair(shape, Off(half + PlacedLocationShape.StructureGap - 0.1f)));
        Assert.DoesNotContain("built pieces", Stair(shape, Off(half + PlacedLocationShape.StructureGap + 0.1f)));
    }

    [Fact]
    public void ABoulderInThatPlaceKeepsOnlyItsOwnMargin()
    {
        // The same boxes as boulders round its mouth: kept off by the approach's margin alone, as before.
        PlacedLocationShape shape = Shape(Built(built: false));
        Assert.DoesNotContain("built pieces", Stair(shape, At(-3.5f, 21f)));
    }

    [Fact]
    public void APlacedSitesFileKeepsWhichPiecesAreBuilt()
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
