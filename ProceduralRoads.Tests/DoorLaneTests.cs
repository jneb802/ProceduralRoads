using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The way in to a location's own door is a lane along the entrance's facing, not the whole half-disc in front of the landing: beside
/// it, the location's built pieces are kept off by the walker's own body as everywhere else.
/// </summary>
public class DoorLaneTests
{
    private static readonly Vector2 Centre = new(100f, 200f);

    // A Dvergr tower's entrance: landing 13.5 m out of its front (clearance 1.5 m), a built piece 6 m beside the lane's middle line, 2 m out.
    private static PlacedLocationShape Tower(out Vector2 landing)
    {
        landing = Centre + new Vector2(-0.25f, 13.5f);
        var piece = new List<LocationRock> { new(landing + new Vector2(6f, 2f), 0.25f, 0.25f, 0f, 0f, 2f, "piece_dvergr_stair", built: true) };
        return PlacedLocationShape.Of(Centre, 0f, PlacedLocationShape.DoorFlat, 0f, piece, prefab: "Mistlands_GuardTower1_ruined_new", entrance: 0);
    }

    [Fact]
    public void TheCorridorIsALaneOfTheWalkersReachAndTheEntrancesClearanceEachSideOfItsFacing()
    {
        PlacedLocationShape shape = Tower(out Vector2 landing);
        Assert.Equal(1.5f, shape.DoorClearance);
        const float walker = 1f;
        Assert.True(shape.InDoorCorridor(landing + new Vector2(walker + 1.5f - 0.1f, 2f), walker));
        Assert.False(shape.InDoorCorridor(landing + new Vector2(walker + 1.5f + 0.1f, 2f), walker));
        Assert.False(shape.InDoorCorridor(landing + new Vector2(-(walker + 1.5f + 0.1f), 2f), walker));
        // The lane's length is still the landing's reach, on the entrance side.
        Assert.True(shape.InDoorCorridor(landing + new Vector2(0f, RoadNetworkGenerator.LandingReach - 0.1f), walker));
        Assert.False(shape.InDoorCorridor(landing + new Vector2(0f, RoadNetworkGenerator.LandingReach + 0.1f), walker));
    }

    [Fact]
    public void ABuiltPieceBesideTheLaneKeepsTheWalkersBodyOffAndOneInsideItKeepsTheClearance()
    {
        PlacedLocationShape shape = Tower(out Vector2 landing);
        // Inside the lane (2.0 m off its line): only the entrance's clearance from the piece (3.75 m off), as before.
        Assert.Equal("", shape.Refusal(landing + new Vector2(2f, 2f), 1f, 1f));
        // Outside it (2.6 m off its line, 3.15 m from the piece): only the walker's own body (rockMargin 1) is kept off the piece.
        Assert.Equal("", shape.Refusal(landing + new Vector2(2.6f, 2f), 1f, 1f));
        Assert.Contains("built pieces (", shape.Refusal(landing + new Vector2(4.85f, 2f), 1f, 1f));
        Assert.Equal("", shape.Refusal(landing + new Vector2(4.65f, 2f), 1f, 1f));
        // An arrival along the lane still lays: the landing and the way in to it are clear.
        Assert.Equal("", shape.Refusal(landing, 1f, 1f));
        Assert.Equal("", shape.Refusal(landing + new Vector2(0f, 4f), 1f, 1f));
    }
}
