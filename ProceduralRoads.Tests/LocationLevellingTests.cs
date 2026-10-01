using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// What ground a location leaves behind, as the road generator predicts it.
/// </summary>
public class LocationLevellingTests
{
    private static readonly Vector2 Centre = new(1280f, 4224f);
    private const float CentreGround = 137f;

    [Fact]
    public void InsideTheFootprintTheGroundIsTheOperationsOwnHeight()
    {
        // The game levels covered vertices to the modifier's world height
        // outright: no clamp, because that only applies to player building.
        // So a location can leave ground metres from the generator's height.
        var ops = new List<LevelOp> { new(0f, 0f, -9f, 20f, square: false) };
        Assert.Equal(128f, LocationLevelling.GroundAt(new Vector2(1290f, 4224f), Centre, CentreGround, ops)!.Value, 3);
    }

    [Fact]
    public void OutsideTheFootprintItDeclinesToAnswer()
    {
        var ops = new List<LevelOp> { new(0f, 0f, -9f, 20f, square: false) };
        Assert.Null(LocationLevelling.GroundAt(new Vector2(1280f + 21f, 4224f), Centre, CentreGround, ops));
    }

    [Fact]
    public void ASquareFootprintReachesItsCorners()
    {
        var round = new List<LevelOp> { new(0f, 0f, 2f, 10f, square: false) };
        var square = new List<LevelOp> { new(0f, 0f, 2f, 10f, square: true) };
        var corner = new Vector2(Centre.x + 9f, Centre.y + 9f); // outside r=10, inside the square

        Assert.Null(LocationLevelling.GroundAt(corner, Centre, CentreGround, round));
        Assert.Equal(139f, LocationLevelling.GroundAt(corner, Centre, CentreGround, square)!.Value, 3);
    }

    [Fact]
    public void TheLastOperationCoveringAPointWins()
    {
        // Each level sets the height outright, so where footprints overlap the
        // later operation is the one the ground ends up at.
        var ops = new List<LevelOp>
        {
            new(0f, 0f, 5f, 30f, square: false),
            new(0f, 0f, -2f, 10f, square: false),
        };
        Assert.Equal(135f, LocationLevelling.GroundAt(new Vector2(1285f, 4224f), Centre, CentreGround, ops)!.Value, 3);
        Assert.Equal(142f, LocationLevelling.GroundAt(new Vector2(1305f, 4224f), Centre, CentreGround, ops)!.Value, 3);
    }

    [Fact]
    public void AnOffCentreOperationIsDeclinedRatherThanGuessedAt()
    {
        // A location's rotation is drawn from the global random stream when it
        // is placed and cannot be reproduced from world data, so an operation
        // offset from the centre could be anywhere on a circle. Answering
        // would be inventing a footprint.
        var ops = new List<LevelOp> { new(8f, 0f, -9f, 20f, square: false) };
        Assert.Null(LocationLevelling.GroundAt(new Vector2(1288f, 4224f), Centre, CentreGround, ops));
    }

    [Fact]
    public void NoOperationsMeansNoAnswer()
    {
        Assert.Null(LocationLevelling.GroundAt(new Vector2(1285f, 4224f), Centre, CentreGround, new List<LevelOp>()));
        Assert.Null(LocationLevelling.GroundAt(new Vector2(1285f, 4224f), Centre, CentreGround, null));
    }
}
