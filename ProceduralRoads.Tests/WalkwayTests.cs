using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A staircase crosses a valley at the lower level by a walkway on columns while the walkway stands within the
/// cap over the ground (StairKit.WalkwayCap); deeper, it goes down and up again, or round.
/// </summary>
public sealed class WalkwayTests
{
    /// <summary>Level ground at 0 with a trench <paramref name="depth"/> deep from x 4 to 10.</summary>
    private static System.Func<float, float, float> Trench(float depth) => (x, z) => x > 4f && x < 10f ? -depth : 0f;

    [Fact]
    public void AFlatRunCrossesATrenchWithinTheCapOnly()
    {
        StairKit kit = StairKit.BlackMarble;
        StairPlan within = StairLayout.Build(new Vector2(0f, 0f), new Vector2(14f, 0f), Trench(StairKit.WalkwayCap - 1f), kit: kit, arriving: 90f, leaving: 90f);
        Assert.True(within.Reached, within.Reason);
        Assert.Contains(within.Pieces, p => p.Kind == StairPieceKind.Landing && p.Position.x > 4f && p.Position.x < 10f);
        Assert.DoesNotContain(within.Pieces, p => p.Kind == StairPieceKind.Stair);
        Assert.Contains(within.Pieces, p => p.Kind == StairPieceKind.Support);

        // Over the cap: no floor stands over the trench.
        StairPlan over = StairLayout.Build(new Vector2(0f, 0f), new Vector2(14f, 0f), Trench(StairKit.WalkwayCap + 1f), kit: kit, arriving: 90f, leaving: 90f,
            maxExpansions: 4000);
        Assert.DoesNotContain(over.Pieces, p => p.Kind == StairPieceKind.Landing
            && p.Position.y + kit.FloorTop - Trench(StairKit.WalkwayCap + 1f)(p.Position.x, p.Position.z) > StairKit.WalkwayCap + 0.01f);
    }

    private static StairPiece Stair(StairKit kit, float x, float y, float yaw) =>
        new() { Kind = StairPieceKind.Stair, Prefab = kit.Stair, Position = new Vector3(x, y, 0f), YawDegrees = yaw };

    /// <summary>Two road staircases of one road meeting in a valley <paramref name="depth"/> under their tops at 34.5,
    /// 28 m from top to top: the first down into it eastward, the second up out of it.</summary>
    private static (StairPlan below, StairPlan up) WideValley(StairKit kit)
    {
        var below = new StairPlan { Kit = kit.Name, Reached = true, Reversed = true };
        below.Pieces.AddRange(new[] { Stair(kit, -13f, 33.5f, 90f), Stair(kit, -11f, 32.5f, 90f) });
        var up = new StairPlan { Kit = kit.Name, Reached = true };
        up.Pieces.AddRange(new[] { Stair(kit, 11f, 32.5f, 270f), Stair(kit, 13f, 33.5f, 270f) });
        return (below, up);
    }

    private static System.Func<float, float, float> WideValleyGround(float depth) =>
        (x, z) => 34.5f - depth + depth * Mathf.Clamp01((Mathf.Abs(x) - 9f) / 5f);

    /// <summary>The valley the knoll at -6984,3312 came down into: a flight off the knoll and one up out of a gully some
    /// 5 m deep and over 20 m across went down to its floor and up again, where a level walkway across it was wanted.</summary>
    [Fact]
    public void AValleyWithinTheCapIsCrossedByAWalkwayHoweverWide()
    {
        StairKit kit = StairKit.BlackMarble;
        var ground = WideValleyGround(StairKit.WalkwayCap - 1f);
        Assert.True(StairChain.CrossesLevel(34.5f - (StairKit.WalkwayCap - 1f), 34.5f, 34.5f));
        var (below, up) = WideValley(kit);
        Assert.True(RoadStairRuns.SpanTheValley(below, up, kit, ground, 32.5f, 2f, FlatRunJoinTests.Relay(ground, new Vector2(16f, 0f), kit),
            (_, _) => true, out StairPlan spanned, out _, level: true));
        Assert.DoesNotContain(spanned.Pieces, p => p.Kind == StairPieceKind.Stair);
        var floors = spanned.Pieces.Where(p => p.Kind == StairPieceKind.Landing).ToList();
        Assert.True(floors.Count >= 12, $"{floors.Count} floors");
        Assert.All(floors, f => Assert.Equal(34.5f, f.Position.y + kit.FloorTop, 2));
        var (most, length) = StairChain.Walkway(spanned, 34.5f, kit, ground);
        Assert.InRange(most, 0f, StairKit.WalkwayCap);
        Assert.True(length >= 24f, $"{length} m");
        Assert.Empty(FlatRunJoinTests.JoinFaults(below.Pieces.Concat(spanned.Pieces).ToList(), kit));
    }

    [Fact]
    public void AValleyDeeperThanTheCapIsNotCrossedLevel()
    {
        StairKit kit = StairKit.BlackMarble;
        float depth = StairKit.WalkwayCap + 2f;
        Assert.False(StairChain.CrossesLevel(34.5f - depth, 34.5f, 34.5f));
        var ground = WideValleyGround(depth);
        var (below, up) = WideValley(kit);
        Assert.False(RoadStairRuns.SpanTheValley(below, up, kit, ground, 32.5f, 2f, FlatRunJoinTests.Relay(ground, new Vector2(16f, 0f), kit),
            (_, _) => true, out _, out _, level: true));
        Assert.Contains(below.Pieces, p => p.Kind == StairPieceKind.Stair && p.Position.y < 33f);
    }
}
