using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A location's built pieces are always in its <see cref="PlacedLocationShape.Body"/>, whether or not a box covers the landing;
/// only a mouth boulder, whose box overstates it, is left out where it covers the landing.
/// </summary>
public class BuiltPieceBodyTests
{
    private static readonly Vector2 Centre = new(100f, 200f);
    private static readonly LevelOp Flat = new(0f, 0f, 0f, 8f, false);

    private static PlacedLocationShape Shape(LocationRock rock, float rockRadius = 0f) =>
        PlacedLocationShape.Of(Centre, 0f, Flat, rockRadius, new List<LocationRock> { rock }, (_, _) => 10f, root: 10f);

    [Fact]
    public void ABuiltPieceWhoseBoxCoversAFlatEdgeLandingStaysInTheBodyAndIsKeptOffByTheWalker()
    {
        Vector2 landing = PlacedLocationShape.Of(Centre, 0f, Flat, 0f, null).Landing;
        // A wall 12 m long, 0.5 m thick, 4 m tall across the landing.
        var wall = new LocationRock(landing, 6f, 0.25f, 0f, 10f, 14f, "stone_wall_4x2", built: true);
        PlacedLocationShape shape = Shape(wall);
        Assert.Equal(landing, shape.Landing);
        Assert.Equal(wall, Assert.Single(shape.Body));
        // A road along the wall keeps its gap from it, and clear of it is accepted.
        Assert.Contains("built pieces", shape.Refusal(landing + new Vector2(0f, 1f), 1f, 1f));
        // Kept off by the walker's own body (rockMargin 1 m here, no gap past it): 1.25 m off the wall is clear.
        Assert.DoesNotContain("built pieces", shape.Refusal(landing + new Vector2(3f, 1.5f), 1f, 1f));
        Assert.Equal("", shape.Refusal(landing + new Vector2(0f, 6f), 1f, 1f));
    }

    /// <summary>A Location's built pieces are kept off at any height: a road does not end on its floor (a Mistlands guard tower's
    /// landing on its own blackmarble floor) nor under its roof; it ends beside them, on its levelled ground
    /// (PlacedLocationShape.Ends).</summary>
    [Fact]
    public void ALandingOnAFloorOrUnderARoofIsKeptOff()
    {
        Vector2 landing = PlacedLocationShape.Of(Centre, 0f, Flat, 0f, null).Landing;
        // A floor whose top is the ground it stands on, and a roof 3 m over it, both across the landing.
        var floor = new LocationRock(landing, 4f, 4f, 0f, 9.5f, 10f, "wood_floor", built: true);
        var roof = new LocationRock(landing, 4f, 4f, 0f, 13f, 13.5f, "wood_roof", built: true);
        foreach (LocationRock piece in new[] { floor, roof })
        {
            PlacedLocationShape shape = Shape(piece);
            Assert.Equal(piece, Assert.Single(shape.Body));
            Assert.Contains("built pieces", shape.Refusal(landing, 1f, 1f));
            Assert.Equal("", shape.Refusal(landing + new Vector2(0f, 5.1f), 1f, 1f));
        }
    }

    [Fact]
    public void ABoulderCoveringTheLandingRoundTheMouthIsStillDropped()
    {
        Vector2 landing = PlacedLocationShape.Of(Centre, 0f, Flat, 0f, null).Landing;
        var boulder = new LocationRock(landing, 4f, 4f, 0f, 10f, 14f, "rock2_mountain");
        PlacedLocationShape shape = Shape(boulder, rockRadius: 12f);
        Assert.Empty(shape.Body);
    }
}
