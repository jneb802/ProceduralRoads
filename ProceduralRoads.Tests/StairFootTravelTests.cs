using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The direction a staircase's foot faces comes from one owner for road staircases and approaches,
/// the road's travel measured 8 m back along it (<see cref="RoadStairRuns.TravelReach"/>).
/// </summary>
public sealed class StairFootTravelTests
{
    /// <summary>A road north up x = 0, turning east 12 m before its end at (12, 0).</summary>
    private static List<Vector2> Bend() => new() { new Vector2(0f, -40f), new Vector2(0f, 0f), new Vector2(12f, 0f) };

    [Fact]
    public void ARoadStaircaseAndAnApproachReadTheRoadsTravelEightMetresBack()
    {
        List<Vector2> path = Bend();
        // 8 m back is still on the road's last leg: due east. (16 m back reached round the bend: 71.6 degrees.)
        Assert.Equal(90f, RoadStairRuns.RoadTravel(path, 2, 1, into: true), 2);
        // An approach from the road's end reads it the same way, through the same owner.
        Assert.Equal(90f, RoadStairRuns.ArrivingTravel(path, path[2], atStart: false), 2);
        // And from the road's start: the travel out along it to that end, as the last segment gave before.
        var reversed = new List<Vector2>(path);
        reversed.Reverse();
        Assert.Equal(90f, RoadStairRuns.ArrivingTravel(reversed, reversed[0], atStart: true), 2);
        // Where an approach sets off from a staircase's end rather than the path's last point, the travel is to that end.
        Assert.Equal(90f, RoadStairRuns.ArrivingTravel(path, new Vector2(13f, 0f), atStart: false), 2);
    }
}
