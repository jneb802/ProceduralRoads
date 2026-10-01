using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The search's grade margin: the ground a route may cross is the cap plus
/// RoadConstants.SearchGradeMargin, while the road that gets built is still
/// held to the cap itself.
///
/// The cases that discriminate are the two edges of the margin and the two
/// ends of it: a slope inside the margin must be crossed, one beyond it must
/// not, a cap switched off must stay off, and a route the search admits must
/// still be refused when its finished profile cannot be built.
/// </summary>
public class SearchGradeMarginTests
{
    /// <summary>A plane rising east, above water everywhere these tests look.</summary>
    private sealed class Ramp : WorldGenerator
    {
        public float Grade = 0.35f;
        public override float GetHeight(float wx, float wy) => 60f + Grade * wx;
        public override Heightmap.Biome GetBiome(float wx, float wy) => Heightmap.Biome.Meadows;
    }

    /// <summary>
    /// A one-cell-wide causeway rising east, with open water either side.
    ///
    /// On an open plane the search does not have to take the fall line: with
    /// sixteen headings it can angle across it, and a step at 0.50 grade taken
    /// diagonally is only 0.447, which the margin admits. That is correct
    /// behaviour and it makes a plane useless for testing a refusal. Here the
    /// only step that goes anywhere is due east, so the step's grade IS the
    /// causeway's and the ceiling has something to refuse.
    /// </summary>
    private sealed class Causeway : WorldGenerator
    {
        public float Grade = 0.35f;
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wy) <= 4f ? 60f + Grade * wx : 0f;
        public override Heightmap.Biome GetBiome(float wx, float wy) => Heightmap.Biome.Meadows;
    }

    // The variance term prices a plane alike everywhere and only slows the
    // search; these tests are about the grade.
    private static RoadPathfinder Finder(WorldGenerator world, float cap) =>
        new RoadPathfinder(world) { MaxGrade = cap, TerrainVarianceThreshold = 1000f };

    private static float SteepestAlong(IReadOnlyList<Vector2> path, WorldGenerator world)
    {
        float steepest = 0f;
        for (int i = 1; i < path.Count; i++)
        {
            float run = Vector2.Distance(path[i - 1], path[i]);
            if (run < 0.001f) continue;
            float rise = Mathf.Abs(world.GetHeight(path[i].x, path[i].y) - world.GetHeight(path[i - 1].x, path[i - 1].y));
            steepest = Mathf.Max(steepest, rise / run);
        }
        return steepest;
    }

    [Fact]
    public void GroundInsideTheMarginIsCrossedStraightUp()
    {
        // 0.40 is over the 0.35 cap and inside the 0.45 ceiling.
        var world = new Ramp { Grade = 0.40f };
        var path = Finder(world, 0.35f).FindPath(new Vector2(0f, 0f), new Vector2(240f, 0f));

        Assert.NotNull(path);
        // Straight up the fall line: the margin admits it at the ordinary price,
        // so there is nothing to detour around.
        Assert.InRange(SteepestAlong(path!, world), 0.39f, 0.41f);
    }

    [Fact]
    public void GroundBeyondTheMarginIsStillRefused()
    {
        // 0.50 is past cap + margin, and on a one-cell causeway there is no
        // shallower heading to take instead.
        var world = new Causeway { Grade = 0.50f };
        var path = Finder(world, 0.35f).FindPath(new Vector2(0f, 0f), new Vector2(240f, 0f));

        Assert.Null(path);
    }

    [Fact]
    public void TheSameCausewayInsideTheMarginIsCrossed()
    {
        // The control for the refusal above: same corridor, 0.40 instead of
        // 0.50, so the only difference is which side of the ceiling it sits on.
        var world = new Causeway { Grade = 0.40f };
        var path = Finder(world, 0.35f).FindPath(new Vector2(0f, 0f), new Vector2(240f, 0f));

        Assert.NotNull(path);
    }

    [Fact]
    public void TheMarginIsNotAppliedWhenTheCapIsOff()
    {
        // A cap of zero is "no cap". It must not become a 0.10 cap.
        var world = new Ramp { Grade = 1.20f };
        var path = Finder(world, 0f).FindPath(new Vector2(0f, 0f), new Vector2(240f, 0f));

        Assert.NotNull(path);
    }

    [Fact]
    public void TheReverseSearchUsesTheSameMargin()
    {
        // reach is the radius that already counts as joined, NOT how far the
        // search may travel: pass the 240 m separation here and the start is
        // "already on network" before a step is taken.
        const float JoinRadius = 16f;

        var inside = new Causeway { Grade = 0.40f };
        var beyond = new Causeway { Grade = 0.50f };

        // Across the causeway, not along it: at a constant x the ground is
        // level, so the seed road is storable under the finished cap. Along it
        // the seed would itself be 0.40 ground and the cap would refuse it,
        // leaving no network for the reverse search to find.
        RoadSpatialGrid.Clear();
        Assert.True(RoadSpatialGrid.AddRoadPath(
            new List<Vector2> { new Vector2(240f, -4f), new Vector2(240f, 4f) },
            8f, inside), "seed road was not stored, so this test would prove nothing");

        var insideFinder = Finder(inside, 0.35f);
        var reached = insideFinder.FindPathToNetwork(new Vector2(0f, 0f), JoinRadius, null);
        bool reachedWasAlreadyThere = insideFinder.AlreadyOnNetwork;

        var beyondFinder = Finder(beyond, 0.35f);
        var refused = beyondFinder.FindPathToNetwork(new Vector2(0f, 0f), JoinRadius, null);
        bool refusedWasAlreadyThere = beyondFinder.AlreadyOnNetwork;

        RoadSpatialGrid.Clear();

        Assert.NotNull(reached);
        Assert.False(reachedWasAlreadyThere);
        // Null for the right reason: refused on grade, not counted as joined.
        Assert.Null(refused);
        Assert.False(refusedWasAlreadyThere);
    }

    [Fact]
    public void ARouteTheSearchAdmitsCanStillBeRefusedWhenItIsBuilt()
    {
        // The whole point of a margin rather than a raised cap: the search may
        // cross 0.40 ground, and the finished profile is still held to 0.35.
        // Ends 96 m apart in height over 240 m of run is 0.40, which no profile
        // inside the cap can meet, so the road is refused after the path is found.
        var world = new Ramp { Grade = 0.40f };
        var path = Finder(world, 0.35f).FindPath(new Vector2(0f, 0f), new Vector2(240f, 0f));
        Assert.NotNull(path);

        using (GradeCap.At(0.35f))
        {
            RoadSpatialGrid.Clear();
            bool stored = RoadSpatialGrid.AddRoadPath(path!, 8f, world);
            RoadSpatialGrid.Clear();

            Assert.False(stored);
        }
    }
}
