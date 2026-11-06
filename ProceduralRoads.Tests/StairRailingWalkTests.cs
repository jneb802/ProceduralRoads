using System;
using System.Collections.Generic;
using System.Linq;
using ProceduralRoads;
using UnityEngine;
using Xunit;

/// <summary>A floor edge is left open only where the walk carries on onto the neighbour by the stair-end rule
/// (<see cref="StairEnds.Walks"/>: its walking surface within <see cref="StairLayout.TopFloorBand"/>). Beside a higher piece
/// the rail continues against its face; over a lower one it guards the drop.</summary>
public class StairRailingWalkTests
{
    private const float Surface = 36.5f, EdgeX = 95f, EdgeZ = 209f;

    private static StructurePiece Floor(StairKit kit, float x, float surface, float z) =>
        new() { Kind = StructurePieceKind.StairFloor, Prefab = kit.Floor, Position = new Vector3(x, surface - kit.FloorTop, z), YawDegrees = 0f };

    /// <summary>The rails along the middle floor's inner edge of a walkway of five stone floors, with <paramref name="neighbour"/>
    /// beside it, over a ground a metre under the walkway.</summary>
    private static int RailsAlongTheSharedEdge(params StructurePiece[] neighbour)
    {
        var walking = new List<StructurePiece>();
        for (int k = 0; k < 5; k++) walking.Add(Floor(StairKit.Stone, 94f, Surface, 205f + 2f * k));
        List<StructurePiece> laid = StairRailing.Lay(StairKit.Stone, walking, Array.Empty<StairEnd>(), (x, z) => Surface - 1f, null, neighbour);
        return laid.Count(p => p.Prefab == StairRailing.LevelRailPrefab
            && Mathf.Abs(p.Position.x - EdgeX) < 0.5f && Mathf.Abs(p.Position.z - EdgeZ) < 1.2f);
    }

    [Fact]
    public void AnEdgeBesideAHigherFloorIsRailedAgainstItsFace()
    {
        // A stone floor 2.0 m higher: its underside is 1.0 m over this one, which the old underside test took as walked onto.
        Assert.Equal(2, RailsAlongTheSharedEdge(Floor(StairKit.Stone, 96f, Surface + 2f, EdgeZ)));
    }

    [Fact]
    public void AnEdgeOverALowerDeckIsRailedOverTheDrop()
    {
        // A wood deck 1.2 m lower: its underside is 1.33 m under this floor, which the old test took as walked onto.
        Assert.Equal(2, RailsAlongTheSharedEdge(Floor(StairKit.Wood, 96f, Surface - 1.2f, EdgeZ - 1f), Floor(StairKit.Wood, 96f, Surface - 1.2f, EdgeZ + 1f)));
    }

    [Fact]
    public void AnEdgeBesideALevelFloorStaysOpen()
    {
        Assert.Equal(0, RailsAlongTheSharedEdge(Floor(StairKit.Stone, 96f, Surface, EdgeZ)));
    }

    [Fact]
    public void AnEdgeBesideAStairAtItsOwnHeightStaysOpen()
    {
        // A stone stair climbing away from the edge, its foot at this floor's walking surface.
        float yaw = new[] { 0f, 90f, 180f, 270f }.First(y => Vector2.Dot(StairLayout.Forward(StairLayout.Wrap(y + 180f)), new Vector2(1f, 0f)) > 0.99f);
        var stair = new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = "stone_stair", Position = new Vector3(96f, Surface, EdgeZ), YawDegrees = yaw };
        Assert.Equal(0, RailsAlongTheSharedEdge(stair));
    }
}
