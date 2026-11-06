using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// One StairChain rule, so a shallow dip is crossed level for road staircases as it is for
/// approaches.
/// </summary>
public sealed class StairChainTests
{
    private static StairPiece Stair(StairKit kit, float x, float y, float yaw) =>
        new() { Kind = StairPieceKind.Stair, Prefab = kit.Stair, Position = new Vector3(x, y, 0f), YawDegrees = yaw };

    /// <summary>Two road staircases of one road through a dip 2 m under both tops (34.5 to 32.5, 8 m across): the first
    /// down into it eastward, the second up out of it.</summary>
    private static (StairPlan below, StairPlan up) ShallowValley(StairKit kit)
    {
        var below = new StairPlan { Kit = kit.Name, Reached = true, Reversed = true };
        below.Pieces.AddRange(new[] { Stair(kit, -3f, 33.5f, 90f), Stair(kit, -1f, 32.5f, 90f) });
        var up = new StairPlan { Kit = kit.Name, Reached = true };
        up.Pieces.AddRange(new[] { Stair(kit, 1f, 32.5f, 270f), Stair(kit, 3f, 33.5f, 270f) });
        return (below, up);
    }

    private static float Dip(float x, float z) => 32.5f + 0.5f * Mathf.Min(Mathf.Abs(x), 4f);

    [Fact]
    public void AShallowDipBetweenTwoRoadStaircasesIsCrossedLevel()
    {
        StairKit kit = StairKit.BlackMarble;
        // The chain's one rule, for approaches and road staircases alike.
        Assert.True(StairChain.CrossesLevel(32.5f, 34.5f, 34.5f));
        Assert.False(StairChain.CrossesLevel(32.5f, 37.5f, 35.5f));
        Assert.Equal(RoadStairRuns.ShallowDip, StairChain.ShallowDip);

        var (below, up) = ShallowValley(kit);
        Assert.True(RoadStairRuns.SpanTheValley(below, up, kit, Dip, 32.5f, 2f, (_, _) => true, out Vector2 from, level: true));
        // No stair left down into the dip: floors at the tops' height from one top edge to the other.
        Assert.DoesNotContain(below.Pieces, p => p.Kind == StairPieceKind.Stair);
        Assert.DoesNotContain(up.Pieces, p => p.Kind == StairPieceKind.Stair);
        var floors = up.Pieces.Where(p => p.Kind == StairPieceKind.Landing).ToList();
        Assert.Equal(4, floors.Count);
        Assert.All(floors, f => Assert.Equal(34.5f, f.Position.y + kit.FloorTop, 2));
        Assert.Equal(-4f, from.x, 2);

        // Control: as a deep valley (the span turning least, then lowest) it stays a rise down, a floor and a rise up.
        var (b2, u2) = ShallowValley(kit);
        Assert.True(RoadStairRuns.SpanTheValley(b2, u2, kit, Dip, 32.5f, 2f, (_, _) => true, out _, level: false));
        Assert.Contains(b2.Pieces, p => p.Kind == StairPieceKind.Stair);
        Assert.Contains(u2.Pieces, p => p.Kind == StairPieceKind.Stair);
    }
}
