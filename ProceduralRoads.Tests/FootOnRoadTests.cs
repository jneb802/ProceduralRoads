using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// No staircase's foot sets off back along its road on the road (judged by its ring at the road's end, RoadEndRing) (the staircase onto Hildir_cave
/// -2304,1536 from MountainCave02 started in the middle of the road, heading backwards). The pieces are a recorded layout's (replayed to the millimetre on the location's ground, road_site_ground):
/// the road came in heading 180 to its end at -2304.35,1566.65, 255.77 m; the search set off back (StairLayout's
/// MostSetOffBack) with a floor on the road's end heading 337.5, turned on a filler floor 3 m back up the road and
/// climbed away at 247.5. Both floors lay within the joining step's 3.2 m, under the road's level, and as an approach's
/// road end the stretch tested as both its ends let anything within 6 m of it at the road's level stand there.
/// </summary>
[Collection("StairLayout")]
public class FootOnRoadTests : IDisposable
{
    public FootOnRoadTests() => RoadSpatialGrid.Clear();
    public void Dispose() => RoadSpatialGrid.Clear();

    private static readonly Vector2 End = new(-2304.35f, 1566.65f);

    /// <summary>The road's last metres in the recorded layout, to its end.</summary>
    private static List<Vector2> Road() => new()
    {
        new(-2307.37f, 1575.85f), new(-2306.11f, 1574.37f), new(-2305.17f, 1572.66f), new(-2304.59f, 1570.81f), new(-2304.37f, 1568.88f), End,
    };

    private static float Ground(float x, float z) => 255.77f;

    private static StairPiece Piece(StairPieceKind kind, string prefab, float x, float y, float z, float yaw) =>
        new() { Kind = kind, Prefab = prefab, Position = new Vector3(x, y, z), YawDegrees = yaw };

    /// <summary>The flight the recorded layout climbed away on, from its first stair.</summary>
    private static IEnumerable<StairPiece> Flight()
    {
        for (int i = 0; i < 5; i++)
            yield return Piece(StairPieceKind.Stair, "stone_stair", -2307.346f - 1.8476f * i, 255.767f + i, 1568.656f - 0.7653f * i, 67.5f);
    }

    /// <summary>The road arriving heading 180 to its end: its ring (RoadEndRing), the one owner of where a staircase meets its
    /// road's end.</summary>
    private static RoadEndRing Ring() => new(End, 180f, 255.77f);

    /// <summary>Changed owner: the end zone is the ring's (OverRoad leaves the joining step's reach of a road's end to it). A
    /// floor set off back on the road's end stands inside the ring and in the lane back along the road.</summary>
    [Fact]
    public void AFootSetOffBackOnTheRoadIsOnItsRoad()
    {
        var plan = new StairPlan { SetOffBack = 1, StartHeading = 337.5f };
        plan.Pieces.Add(Piece(StairPieceKind.Landing, "stone_floor_2x2", -2304.733f, 255.267f, 1567.574f, 337.5f));
        plan.Pieces.Add(Piece(StairPieceKind.Filler, "stone_floor_2x2", -2305.498f, 255.267f, 1569.422f, 337.5f));
        plan.Pieces.AddRange(Flight());
        var road = Road();
        // Its flight, past the joining step's reach, runs on along the road: on it.
        Assert.True(RoadStairRuns.OverRoad(plan, road, road.Count - 1, road.Count - 1, Ground));
        Assert.True(Ring().Intrudes(plan));
    }

    /// <summary>Even counted as no set-off (a free layout holds no facing, so counts none), a floor back on the road behind
    /// the end is inside its ring.</summary>
    [Fact]
    public void AFloorBehindTheEndIsOnTheRoadHoweverTheSearchCountedIt()
    {
        var plan = new StairPlan { SetOffBack = 0, StartHeading = 337.5f };
        plan.Pieces.Add(Piece(StairPieceKind.Landing, "stone_floor_2x2", -2304.733f, 255.267f, 1567.574f, 337.5f));
        plan.Pieces.AddRange(Flight());
        Assert.True(Ring().Intrudes(plan));
    }

    /// <summary>What the rules want stays allowed: a floor on the front of the ring straight on with the road, and a sunk
    /// stair under the road's level as its joining step. Changed: a floor whose middle is a metre past the end, in the road's
    /// middle, stands inside the ring.</summary>
    [Fact]
    public void AFootStraightOnFromTheEndOrASunkStepIsNot()
    {
        var road = Road();
        var middle = new StairPlan { StartHeading = 180f };
        middle.Pieces.Add(Piece(StairPieceKind.Landing, "stone_floor_2x2", End.x, 255.27f, End.y - 1f, 180f));
        Assert.True(Ring().Intrudes(middle));
        var ahead = new StairPlan { StartHeading = 180f };
        // A floor whose back edge is on the front of the ring, heading on with the road.
        ahead.Pieces.Add(Piece(StairPieceKind.Landing, "stone_floor_2x2", End.x, 255.27f, End.y - Ring().Radius - 1f, 180f));
        Assert.False(RoadStairRuns.OverRoad(ahead, road, road.Count - 1, road.Count - 1, Ground));
        Assert.False(Ring().Intrudes(ahead, out string piece), piece);

        var sunk = new StairPlan { SetOffBack = 1, StartHeading = 0f, SunkPieces = 1 };
        sunk.Pieces.Add(Piece(StairPieceKind.Stair, "stone_stair", End.x, 254.6f, End.y + 1f, 180f));
        Assert.False(RoadStairRuns.OverRoad(sunk, road, road.Count - 1, road.Count - 1, Ground));
        Assert.False(Ring().Intrudes(sunk, out piece), piece);
    }
}
