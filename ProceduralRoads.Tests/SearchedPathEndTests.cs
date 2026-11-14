using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A searched path ends at its goal without running past it and folding back (RoadPathfinder.ReconstructPath, the one owner
/// of a searched path's points). The goal's cell is arrived at the exact goal: its centre is dropped where the goal lies back
/// toward the point before it, kept where the goal lies beyond it. Live case: a marked road's search ended at its goal cell's
/// centre (16, 0) on the 8 m grid, 2 m past its mark at (18, 0), and came back to the mark, the road folding onto itself.
/// Only a marked road's search stops so (RoadPathfinder.StopsAtItsGoal, set by ManualRoads); a generated road's keeps its
/// goal cell's centre, the last move it priced, its end carried on past the goal.
/// </summary>
public sealed class SearchedPathEndTests
{
    private sealed class Flat : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    private static List<Vector2> Searched(Vector2 from, Vector2 to, bool marked = true)
    {
        List<Vector2>? path = new RoadPathfinder(new Flat()) { StopsAtItsGoal = marked }.FindPath(from, to);
        Assert.NotNull(path);
        Assert.Equal(from, path![0]);
        Assert.Equal(to, path[path.Count - 1]);
        return path;
    }

    /// <summary>No point of the path doubles back: each edge runs on from the one before it, never back against it.</summary>
    private static void NeverFoldsBack(List<Vector2> path)
    {
        for (int i = 2; i < path.Count; i++)
            Assert.True(Vector2.Dot(path[i] - path[i - 1], path[i - 1] - path[i - 2]) > 0f,
                $"folds back at {path[i - 1]}: {string.Join(" ", path)}");
    }

    [Fact]
    public void ASearchedPathEndsAtItsMarkWithoutFoldingBack()
    {
        // From the east, the goal (18, 0) lies short of its cell's centre (16, 0).
        List<Vector2> path = Searched(new Vector2(60f, 0f), new Vector2(18f, 0f));
        NeverFoldsBack(path);
        Assert.All(path, p => Assert.True(p.x >= 18f - 1e-3f, $"past the goal at {p}: {string.Join(" ", path)}"));
        Assert.DoesNotContain(new Vector2(16f, 0f), path);
    }

    [Fact]
    public void AGoalBeyondItsCellsCentreKeepsTheCentre()
    {
        // From the west, the goal (18, 0) lies beyond its cell's centre: the path comes through the centre on to the goal.
        List<Vector2> path = Searched(new Vector2(-40f, 0f), new Vector2(18f, 0f));
        NeverFoldsBack(path);
        Assert.Contains(new Vector2(16f, 0f), path);
    }

    [Fact]
    public void AGeneratedRoadsSearchKeepsItsGoalCellsCentre()
    {
        // The same search as a marked road's, from the east to (18, 0), off a generated road's finder: it ends through its
        // goal cell's centre (16, 0), the last move it priced, and on to the goal.
        Assert.False(new RoadPathfinder(new Flat()).StopsAtItsGoal);
        List<Vector2> path = Searched(new Vector2(60f, 0f), new Vector2(18f, 0f), marked: false);
        Assert.Equal(new Vector2(16f, 0f), path[path.Count - 2]);
    }
}
