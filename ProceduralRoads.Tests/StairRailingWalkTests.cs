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
        // A stone deck 1.2 m lower: its underside is 2.2 m under this floor, which the old test took as walked onto.
        Assert.Equal(2, RailsAlongTheSharedEdge(Floor(StairKit.Stone, 96f, Surface - 1.2f, EdgeZ - 1f), Floor(StairKit.Stone, 96f, Surface - 1.2f, EdgeZ + 1f)));
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

    [Fact]
    public void AnEdgeBesideAFlightRisingAlongItGetsItsOwnFlatPair()
    {
        // A stone flight running along the shared edge (its side on the edge), its top at this floor's walking surface: the walk
        // does not carry on across the edge, it climbs along it. The floor's own pair stands beside the flight's sloped rails.
        float yaw = new[] { 0f, 90f, 180f, 270f }.First(y => Vector2.Dot(StairLayout.Forward(StairLayout.Wrap(y + 180f)), new Vector2(0f, 1f)) > 0.99f);
        var stair = new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = "stone_stair", Position = new Vector3(96f, Surface - 1f, EdgeZ), YawDegrees = yaw };
        var walking = new List<StructurePiece>();
        for (int k = 0; k < 5; k++) walking.Add(Floor(StairKit.Stone, 94f, Surface, 205f + 2f * k));
        List<StructurePiece> laid = StairRailing.Lay(StairKit.Stone, walking, Array.Empty<StairEnd>(), (x, z) => Surface - 1f, null, new[] { stair });
        var pair = laid.Where(p => p.Prefab == StairRailing.LevelRailPrefab && Mathf.Abs(p.Position.x - EdgeX) < 0.01f && Mathf.Abs(p.Position.z - EdgeZ) < 0.01f).ToList();
        Assert.Equal(2, pair.Count);
        Assert.Contains(pair, p => Mathf.Abs(p.Position.y - Surface) < 0.01f);
        Assert.Contains(pair, p => Mathf.Abs(p.Position.y - (Surface + BridgeLayout.RailHeight)) < 0.01f);
        Assert.All(pair, p => Assert.Equal(pair[0].YawDegrees, p.YawDegrees));
        Assert.Contains(pair, p => Mathf.Abs(StairLayout.Wrap(p.YawDegrees - 270f)) < 0.01f || Mathf.Abs(StairLayout.Wrap(p.YawDegrees - 90f)) < 0.01f);
    }

    /// <summary>Two black marble staircases met end to end (in game at -6592.6,-3488.3): the upper one's first floor carries on
    /// the lower one's heading, the next turns 22.5 degrees, the two overlapping on the inside of the turn, and a second floor
    /// laid 0.1 m on from it. Their inner edges cross inside each other's walk: each is railed only to the crossing, a post there
    /// closing the inside, and no post or rail stands in another floor's walk.</summary>
    [Fact]
    public void TheInsideRailAtAShallowJogStopsAtTheOtherFloorsEdge()
    {
        StructurePiece F(float x, float y, float z, float yaw) =>
            new() { Kind = StructurePieceKind.StairFloor, Prefab = StairKit.BlackMarble.Floor, Position = new Vector3(x, y, z), YawDegrees = yaw };
        var walking = new List<StructurePiece>
        {
            F(-6593.817f, 61.935f, -3488.492f, 67.5f),   // carries on the lower staircase's top floor
            F(-6592.799f, 61.935f, -3488.402f, 90f),     // turned 22.5 degrees, over it on the inside
            F(-6592.696f, 61.935f, -3488.402f, 90f),
            F(-6590.695f, 62.004f, -3488.402f, 270f),
        };
        List<StructurePiece> laid = StairRailing.Lay(StairKit.BlackMarble, walking, Array.Empty<StairEnd>(), (x, z) => 0f);
        var walks = walking.Select(p => StairSupport.WalkOf(p, StairKit.BlackMarble)!.Value).ToList();
        bool InAWalk(Vector2 p) => walks.Exists(w => StairLayout.OnFootprint(w.Back, w.Dir, w.Right, w.Depth, w.Across, p, -0.1f));
        Vector2 Flat(Vector3 v) => new(v.x, v.z);

        Assert.DoesNotContain(laid, p => p.Prefab == BridgeLayout.RailPostPrefab && InAWalk(Flat(p.Position)));
        foreach (StructurePiece rail in laid.Where(p => p.Prefab is StairRailing.LevelRailPrefab or StairRailing.ShortRailPrefab))
        {
            float half = rail.Prefab == StairRailing.ShortRailPrefab ? 0.5f : 1f;
            Vector2 along = StairLayout.Right(rail.YawDegrees);
            Assert.False(InAWalk(Flat(rail.Position) + along * half) || InAWalk(Flat(rail.Position) - along * half),
                $"{rail.Prefab} at {rail.Position} yaw {rail.YawDegrees} stands in the walk");
        }
        // The inner edges cross at (-6593.40, -3489.40): a post closes the inside there.
        Assert.Contains(laid, p => p.Prefab == BridgeLayout.RailPostPrefab && Vector2.Distance(Flat(p.Position), new Vector2(-6593.40f, -3489.402f)) < 0.1f);
    }

    /// <summary>The black marble staircase's head of flight in game at 5265.3,3269.7 (pieces 18 to 23): a floor, a floor jogged
    /// right of it, then the next flight in line with the first floor. That floor's front corner and the stair's back corner are in
    /// line (no turn), so no wedge stands between them: no rail crosses the walk and none is laid twice, while the real wedge
    /// between the jogged floor and the stair stays closed.</summary>
    [Fact]
    public void AJogsInLineFloorTwoBackIsNotClosedAcrossTheWalk()
    {
        StructurePiece P(StructurePieceKind kind, string prefab, float x, float y, float z, float yaw) =>
            new() { Kind = kind, Prefab = prefab, Position = new Vector3(x, y, z), YawDegrees = yaw };
        StructurePiece Stair(float x, float y, float z, float yaw) => P(StructurePieceKind.StairStep, "blackmarble_stair", x, y, z, yaw);
        StructurePiece FloorAt(float x, float y, float z, float yaw) => P(StructurePieceKind.StairFloor, "blackmarble_floor", x, y, z, yaw);
        StructurePiece Filler(float x, float y, float z, float yaw) => P(StructurePieceKind.StairFiller, "blackmarble_2x1x1", x, y, z, yaw);
        var walking = new List<StructurePiece>
        {
            Stair(5261.81f, 37.39f, 3266.32f, 225f),
            FloorAt(5263.23f, 37.89f, 3267.73f, 45f),
            FloorAt(5264.23f, 37.89f, 3268.14f, 90f),
            Filler(5265.73f, 37.89f, 3268.14f, 270f),
            Filler(5265.58f, 37.89f, 3268.08f, 225f),
            Stair(5266.64f, 38.39f, 3269.14f, 225f),
        };
        List<StructurePiece> laid = StairRailing.Lay(StairKit.BlackMarble, walking, Array.Empty<StairEnd>(), (x, z) => 30f);
        var walks = walking.Where(p => p.Kind != StructurePieceKind.StairFiller).Select(p => StairSupport.WalkOf(p, StairKit.BlackMarble)!.Value).ToList();
        bool InAWalk(Vector2 p) => walks.Exists(w => StairLayout.OnFootprint(w.Back, w.Dir, w.Right, w.Depth, w.Across, p, -0.3f));
        Vector2 Flat(Vector3 v) => new(v.x, v.z);

        var level = laid.Where(p => p.Prefab is StairRailing.LevelRailPrefab or StairRailing.ShortRailPrefab).ToList();
        foreach (StructurePiece rail in level)
        {
            float half = rail.Prefab == StairRailing.ShortRailPrefab ? 0.5f : 1f;
            Vector2 along = StairLayout.Right(rail.YawDegrees), c = Flat(rail.Position);
            Assert.False(InAWalk(c) || InAWalk(c + along * half * 0.5f) || InAWalk(c - along * half * 0.5f),
                $"{rail.Prefab} at {rail.Position} yaw {rail.YawDegrees} lies across the walk");
        }
        var keys = laid.Select(p => (p.Prefab, (int)Mathf.Round(p.Position.x * 100f), (int)Mathf.Round(p.Position.y * 100f),
            (int)Mathf.Round(p.Position.z * 100f), (int)Mathf.Round(p.YawDegrees * 10f))).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        // The real wedge, floor 20's front corner to the stair's back corner, keeps its two 1 m runs.
        Assert.Contains(level, p => p.Prefab == StairRailing.ShortRailPrefab
            && Vector2.Distance(Flat(p.Position), new Vector2(5265.73f, 3267.14f)) < 0.05f && Mathf.Abs(StairLayout.Wrap(p.YawDegrees)) < 0.5f);
        Assert.Contains(level, p => p.Prefab == StairRailing.ShortRailPrefab
            && Vector2.Distance(Flat(p.Position), new Vector2(5266.29f, 3267.38f)) < 0.05f && Mathf.Abs(StairLayout.Wrap(p.YawDegrees - 135f)) < 0.5f);
    }
}
