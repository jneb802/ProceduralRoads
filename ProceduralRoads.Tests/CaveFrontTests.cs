using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The way in to a cave's measured entrance comes from in front of its mouth: within the network's reach of the landing it keeps
/// to the door's lane (<see cref="PlacedLocationShape.InDoorCorridor"/>), so a road from the side is led round to the front
/// instead of running in under the mouth's overhang, which stands over a walker's head and so is no boulder in its way.
/// </summary>
public class CaveFrontTests
{
    private const float Width = 4f, RockRadius = 12f;
    private static readonly Vector2 Centre = new(0f, 0f);

    // The cave's mouth rock: a large boulder round its mouth, beside the landing, overhanging it from 3 m up.
    private static readonly LocationRock Overhang = new(new Vector2(6f, 2f), 3f, 3f, 0f, 3f, 10f, "rock4_forest");

    private static PlacedLocationShape Cave() =>
        PlacedLocationShape.Of(Centre, 0f, PlacedLocationShape.DoorFlat, RockRadius, new List<LocationRock> { Overhang },
            prefab: "TrollCave02", entrance: 0, bodyKnown: true);

    private static readonly PlacedLocationShape.Walker Road = PlacedLocationShape.Walker.Road(Width);

    private static string Judge(PlacedLocationShape shape, Vector2 p) =>
        shape.KeepsOff(p, Road);

    /// <summary>Every metre of <paramref name="line"/>, judged as a road's way in is.</summary>
    private static void AllAllowed(PlacedLocationShape shape, List<Vector2> line)
    {
        for (int i = 1; i < line.Count; i++)
        {
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(line[i - 1], line[i])));
            for (int k = 0; k <= steps; k++)
            {
                Vector2 p = Vector2.Lerp(line[i - 1], line[i], (float)k / steps);
                Assert.Equal("", Judge(shape, p));
            }
        }
    }

    /// <summary>A cave cut into rock is come to from anywhere on its entrance side, measured from its landing (not along its door's
    /// lane only: held to the lane, a road from the side was led round to the lane and turned in, folding back on itself before the
    /// mouth), and never from beside or behind its landing, where it would run in under the mouth's overhang.</summary>
    [Fact]
    public void ACaveEntranceIsComeToFromAnywhereOnItsEntranceSideNeverFromBehindItsLanding()
    {
        PlacedLocationShape cave = Cave();
        Assert.True(cave.FrontLaneOnly);
        // As placed in game: the overhang is the prefab's own rock, no boulder of its body (only its treasure chest stands there).
        var chest = new List<LocationRock> { new(new Vector2(11f, 7f), 0.5f, 0.5f, 0f, 0f, 1f, "TreasureChest_trollcave", built: true) };
        PlacedLocationShape asPlaced = PlacedLocationShape.Of(Centre, 0f, PlacedLocationShape.DoorFlat, RockRadius, chest,
            prefab: "TrollCave02", entrance: 0, bodyKnown: true);
        Assert.True(asPlaced.FrontLaneOnly);
        Vector2 door = cave.Door!.Value;
        // Beside and behind the landing: refused.
        Assert.StartsWith("beside TrollCave02's entrance", Judge(asPlaced, door + new Vector2(-6f, -2f)));
        Assert.StartsWith("beside TrollCave02's entrance, off its way in", Judge(cave, door + new Vector2(-6f, -2f)));
        // Beside it but in front: allowed.
        Assert.Equal("", Judge(cave, door + new Vector2(-6f, 2f)));
        // From the side, on the front half of the cave: straight in, no lead round.
        var end = new Vector2(-25f, 8f);
        Assert.False(cave.Behind(end, door, Road));
        Assert.True(cave.OnTheWayInAllTheWay(end, door));
        List<Vector2> line = RoadLocationEntry.Route(end, cave, door, Road);
        Assert.Equal(2, line.Count);
        AllAllowed(cave, line);
    }

    [Fact]
    public void ACaveEntranceApproachedFromItsFrontIsAllowedStraightIn()
    {
        PlacedLocationShape cave = Cave();
        Vector2 door = cave.Door!.Value;
        var end = door + cave.Front * 25f + new Vector2(1f, 0f);
        List<Vector2> line = RoadLocationEntry.Route(end, cave, door, Road);
        Assert.Equal(2, line.Count);
        AllAllowed(cave, line);
        // The lane's whole length, the landing included.
        Assert.Equal("", Judge(cave, door));
        Assert.Equal("", Judge(cave, door + cave.Front * (RoadNetworkGenerator.LandingReach - 0.5f)));
    }

    [Fact]
    public void ANonCaveEntranceIsComeToFromTheSideAsBefore()
    {
        // A crypt's entrance, its own built piece standing well clear: no boulder round its mouth.
        var piece = new List<LocationRock> { new(new Vector2(0f, -6f), 1f, 1f, 0f, 0f, 3f, "stone_wall", built: true) };
        PlacedLocationShape crypt = PlacedLocationShape.Of(Centre, 0f, PlacedLocationShape.DoorFlat, RockRadius, piece,
            prefab: "Crypt4", entrance: 0, bodyKnown: true);
        Assert.False(crypt.FrontLaneOnly);
        Vector2 door = crypt.Door!.Value;
        var end = new Vector2(-25f, 15f);
        Assert.Equal("", Judge(crypt, door + new Vector2(-6f, 2f)));
        Assert.True(crypt.OnTheWayInAllTheWay(end, door));
        List<Vector2> line = RoadLocationEntry.Route(end, crypt, door, Road);
        Assert.Equal(2, line.Count);
        AllAllowed(crypt, line);
    }
}
