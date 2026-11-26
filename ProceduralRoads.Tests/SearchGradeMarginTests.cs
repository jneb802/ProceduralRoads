using Valheim.Testing;
using Valheim.Testing.Doubles;
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
    private sealed class Ramp : TerrainWorld
    {
        public readonly float Grade;
        public Ramp(float grade = .35f) : base(new PlaneTerrain(60, grade)) { Grade = grade; }
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
    private sealed class Causeway : TerrainWorld
    {
        public Causeway(float grade = .35f) : base(new CompositeTerrain(new PlaneTerrain(0),
            new TerrainRegion((x,z) => System.Math.Abs(z) <= 4, new PlaneTerrain(60, grade)))) { }
    }

    // The variance term prices a plane alike everywhere and only slows the
    // search; these tests are about the grade.
    private static RoadPathfinder Finder(WorldGenerator world, float cap) =>
        new RoadPathfinder(world) { MaxGrade = cap, TerrainVarianceThreshold = 1000f, Stairs = false }; // road's margin, no staircases

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
        var world = new Ramp(0.40f);
        var path = Finder(world, 0.35f).FindPath(new Vector2(0f, 0f), new Vector2(240f, 0f));

        Assert.NotNull(path);
        // Straight up the fall line: the margin admits it at the ordinary price,
        // so there is nothing to detour around.
        Assert.InRange(SteepestAlong(path!, world), 0.39f, 0.41f);
    }

    [Theory]
    [InlineData(0.40f, true)]   // over the 0.35 cap, inside the 0.45 ceiling
    [InlineData(0.50f, false)]  // past cap + margin, and on a one-cell causeway there is no shallower heading
    public void EverySearchJudgesTheCausewayAlike(float grade, bool crossed)
    {
        // The same causeway, the same cap, through each way into the search: point to point (FindPath), between end
        // sets (FindPathBetween) and back to a road already laid (FindPathToNetwork). They share one step price
        // (RoadPathfinder.RoadMoveCost), so they must agree on which side of the ceiling a slope sits; the two grades
        // differ only in that.
        var world = new Causeway(grade);
        Vector2 from = new(0f, 0f), to = new(240f, 0f);
        Assert.Equal(crossed, Finder(world, 0.35f).FindPath(from, to) != null);
        Assert.Equal(crossed, Finder(world, 0.35f).FindPathBetween(new[] { (from, 0f) }, new[] { (to, 0f) }, null, null) != null);

        // reach is the radius that already counts as joined, NOT how far the search may travel: pass the 240 m
        // separation here and the start is "already on network" before a step is taken.
        const float JoinRadius = 16f;
        // Across the causeway, not along it: at a constant x the ground is level, so the seed road is storable under
        // the finished cap. Along it the seed would itself be steep ground and the cap would refuse it, leaving no
        // network for the search to find.
        RoadSpatialGrid.Clear();
        try
        {
            Assert.True(RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(240f, -4f), new(240f, 4f) }, 8f, world),
                "seed road was not stored, so this test would prove nothing");
            var finder = Finder(world, 0.35f);
            var joined = finder.FindPathToNetwork(from, JoinRadius, served: () => false); // no road within reach of the start
            // Null for the right reason: refused on grade, not counted as joined.
            Assert.False(finder.AlreadyOnNetwork);
            Assert.Equal(crossed, joined != null);
        }
        finally { RoadSpatialGrid.Clear(); }
    }

    [Fact]
    public void TheMarginIsNotAppliedWhenTheCapIsOff()
    {
        // A cap of zero is "no cap". It must not become a 0.10 cap.
        var world = new Ramp(1.20f);
        var path = Finder(world, 0f).FindPath(new Vector2(0f, 0f), new Vector2(240f, 0f));

        Assert.NotNull(path);
    }

    [Fact]
    public void ARouteTheSearchAdmitsCanStillBeRefusedWhenItIsBuilt()
    {
        // The whole point of a margin rather than a raised cap: the search may
        // cross 0.40 ground, and the finished profile is still held to 0.35.
        // Ends 96 m apart in height over 240 m of run is 0.40, which no profile
        // inside the cap can meet, so the road is refused after the path is found.
        var world = new Ramp(0.40f);
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
