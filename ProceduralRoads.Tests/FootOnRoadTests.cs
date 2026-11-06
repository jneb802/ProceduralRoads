using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// No staircase's foot sets off back along its road on the road (the staircase onto Hildir_cave
/// -2304,1536 from MountainCave02 started in the middle of the road, heading backwards). The pieces are a recorded layout's (replayed to the millimetre on the place's ground, road_site_ground):
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

    [Fact]
    public void AFootSetOffBackOnTheRoadIsOnItsRoad()
    {
        var plan = new StairPlan { SetOffBack = 1, StartHeading = 337.5f };
        plan.Pieces.Add(Piece(StairPieceKind.Landing, "stone_floor_2x2", -2304.733f, 255.267f, 1567.574f, 337.5f));
        plan.Pieces.Add(Piece(StairPieceKind.Filler, "stone_floor_2x2", -2305.498f, 255.267f, 1569.422f, 337.5f));
        plan.Pieces.AddRange(Flight());
        var road = Road();
        Assert.True(RoadStairRuns.OverRoad(plan, road, road.Count - 1, road.Count - 1, Ground));
    }

    /// <summary>Even counted as no set-off (a free layout holds no facing, so counts none), a floor back on the road behind
    /// the end is on it.</summary>
    [Fact]
    public void AFloorBehindTheEndIsOnTheRoadHoweverTheSearchCountedIt()
    {
        var plan = new StairPlan { SetOffBack = 0, StartHeading = 337.5f };
        plan.Pieces.Add(Piece(StairPieceKind.Landing, "stone_floor_2x2", -2304.733f, 255.267f, 1567.574f, 337.5f));
        plan.Pieces.AddRange(Flight());
        var road = Road();
        Assert.True(RoadStairRuns.OverRoad(plan, road, road.Count - 1, road.Count - 1, Ground));
    }

    /// <summary>What the rules want stays allowed: a floor on the end straight on with the road, the flight turned off it, and
    /// a sunk stair under the road's level as its joining step.</summary>
    [Fact]
    public void AFootStraightOnFromTheEndOrASunkStepIsNot()
    {
        var road = Road();
        var ahead = new StairPlan { StartHeading = 180f };
        // A floor whose middle is a metre on past the end, heading on with the road.
        ahead.Pieces.Add(Piece(StairPieceKind.Landing, "stone_floor_2x2", End.x, 255.27f, End.y - 1f, 180f));
        Assert.False(RoadStairRuns.OverRoad(ahead, road, road.Count - 1, road.Count - 1, Ground));

        var sunk = new StairPlan { SetOffBack = 1, StartHeading = 0f };
        sunk.Pieces.Add(Piece(StairPieceKind.Stair, "stone_stair", End.x, 254.6f, End.y + 1f, 180f));
        Assert.False(RoadStairRuns.OverRoad(sunk, road, road.Count - 1, road.Count - 1, Ground));
    }
}
