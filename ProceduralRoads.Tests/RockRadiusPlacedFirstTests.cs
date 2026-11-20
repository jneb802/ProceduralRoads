using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The exterior radius stands in for the rock a cave is cut into. A Location placed first has its rocks and built pieces read
/// (<see cref="PlacedLocationShape.Body"/>): they decide, not the radius. A shape made without them keeps the radius.
/// </summary>
public class RockRadiusPlacedFirstTests
{
    private static readonly Vector2 Centre = new(100f, 200f);
    private static readonly LevelOp Flat = new(0f, 0f, 0f, 6f, false);
    private const float Exterior = 24f, Reach = 4f, Walker = 1f;

    private static PlacedLocationShape Tower(bool placedFirst)
    {
        // Behind the tower (its front is +z at yaw 0): a wall 3 m wide and a boulder, inside the radius.
        var rocks = new List<LocationRock>
        {
            new(Centre + new Vector2(0f, -22f), 1.5f, 0.25f, 0f, 0f, 4f, "stone_wall_2x1", built: true),
            new(Centre + new Vector2(10f, -18f), 1.5f, 1.5f, 0f, 0f, 3f, "rock2_mountain"),
        };
        return PlacedLocationShape.Of(Centre, 0f, Flat, Exterior, rocks, (_, _) => 0f, root: 0f, bodyKnown: placedFirst);
    }

    [Fact]
    public void APlacedFirstTowersWayInFromARingEndOffItsEntranceSideIsNotRefusedByTheRadiusButKeepsOffItsPieces()
    {
        PlacedLocationShape shape = Tower(placedFirst: true);
        Vector2 ringEnd = Centre + new Vector2(-7f, -27.6f); // 28.5 m out, inside the radius's 31 m, clear of every piece
        Assert.False(shape.InFront(ringEnd));
        Assert.Equal("", shape.Refusal(ringEnd, Reach, Walker));
        // Still the walker's own body off its wall (no gap past it), and the boulder's reach off it.
        Assert.Contains("built pieces", shape.Refusal(Centre + new Vector2(0f, -22.25f - Walker + 0.1f), Reach, Walker));
        Assert.DoesNotContain("built pieces", shape.Refusal(Centre + new Vector2(0f, -22.25f - Walker - 0.1f), Reach, Walker));
        Assert.Contains("rock2_mountain", shape.Refusal(Centre + new Vector2(10f, -21f), Reach, Walker));
    }

    [Fact]
    public void ALocationNotPlacedFirstKeepsTheRadius()
    {
        PlacedLocationShape shape = Tower(placedFirst: false);
        Vector2 ringEnd = Centre + new Vector2(-7f, -27.6f);
        string why = shape.Refusal(ringEnd, Reach, Walker);
        Assert.StartsWith("in the location's rock (", why);
        Assert.Contains("only the radius", why);
    }
}
