using System.Collections.Generic;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;
namespace ProceduralRoads.Tests;

/// <summary>The join search judges each arrival at a network cell, not the cell.</summary>
[Collection("RoadStatics")]
public class JoinArrivalTests : System.IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    public JoinArrivalTests() => RoadSpatialGrid.Clear();
    public void Dispose() { RoadSpatialGrid.Clear(); _scope.Dispose(); }

    /// <summary>A plateau at 43.5 m round a short road at 40 m, sunk in its own cut, with one way in at the road's
    /// height: a corridor at 40 m from the east. The only network cell at grade, (8, 0), is reached cheapest over the
    /// plateau, arriving 3.5 m off the road's height; level only along the corridor. Water past 100 m.</summary>
    private sealed class CutWithOneWayIn : WorldGenerator
    {
        public bool Corridor = true;
        public override float GetHeight(float x, float z) =>
            Mathf.Abs(x) > 100f || Mathf.Abs(z) > 100f ? 0f : Corridor && Mathf.Abs(z) <= 3f && x >= 5f && x <= 70f ? 40f : 43.5f;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
        public override int GetSeed() => 7;
    }

    /// <summary>A join search refuses an arrival direction that is off grade, not the cell it arrives at: another
    /// direction that arrives level is still tried. Judged once per cell, the off-grade arrival closed the only junction and the search found none.</summary>
    [Fact]
    public void AJoinSearchTriesALevelArrivalAfterAnOffGradeOneAtTheSameCell()
    {
        float step = RoadPathfinder.JunctionStep;
        RoadPathfinder.JunctionStep = RoadPathfinder.DefaultJunctionStep;
        try
        {
            List<Vector2>? Search(bool corridor)
            {
                var world = new CutWithOneWayIn { Corridor = corridor };
                _scope.WithWorld(world);
                RoadSpatialGrid.Clear();
                var road = new List<Vector2> { new(-2f, 0f), new(0f, 0f), new(2f, 0f) };
                RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(road, new List<float> { 40f, 40f, 40f },
                    new List<RoadPointDebugInfo> { default, default, default }, 4f, 4f, false));
                RoadSpatialGrid.FinalizeRoadNetwork();
                var finder = new RoadPathfinder(world) { Stairs = false, TerrainVarianceThreshold = 1000f, Meander = 0f };
                var path = finder.FindPathToNetwork(new Vector2(0f, 48f), 8f);
                Assert.False(finder.AlreadyOnNetwork);
                return path;
            }
            List<Vector2>? path = Search(corridor: true);
            Assert.NotNull(path);
            // It ends at the junction from the corridor's cell, at the road's height.
            Assert.True(Vector2.Distance(path![path.Count - 2], new Vector2(8f, 0f)) < 0.5f, $"the last cell is {path[path.Count - 2]}");
            Assert.True(Vector2.Distance(path[path.Count - 3], new Vector2(16f, 0f)) < 0.5f, $"it arrives from {path[path.Count - 3]}, not along the corridor");
            // With no level way in there is no junction: the off-grade arrivals are refused, not priced.
            Assert.Null(Search(corridor: false));
        }
        finally { RoadPathfinder.JunctionStep = step; }
    }

    /// <summary>A join search whose budget runs out after it has found a level junction returns that junction, not
    /// nothing: the goal waits for cheaper cells to be expanded first, and the budget could end in between.</summary>
    [Fact]
    public void AJoinSearchOutOfBudgetReturnsTheJunctionItFound()
    {
        float step = RoadPathfinder.JunctionStep;
        int budget = RoadPathfinder.MaxIterations;
        RoadPathfinder.JunctionStep = RoadPathfinder.DefaultJunctionStep;
        try
        {
            var world = new CutWithOneWayIn();
            _scope.WithWorld(world);
            RoadSpatialGrid.Clear();
            var road = new List<Vector2> { new(-2f, 0f), new(0f, 0f), new(2f, 0f) };
            RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(road, new List<float> { 40f, 40f, 40f },
                new List<RoadPointDebugInfo> { default, default, default }, 4f, 4f, false));
            RoadSpatialGrid.FinalizeRoadNetwork();
            var used = typeof(RoadPathfinder).GetField("m_lastIterations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            List<Vector2>? Search(int iterations, out int spent)
            {
                RoadPathfinder.MaxIterations = iterations;
                var finder = new RoadPathfinder(world) { Stairs = false, TerrainVarianceThreshold = 1000f, Meander = 0f };
                var path = finder.FindPathToNetwork(new Vector2(0f, 48f), 8f);
                spent = (int)used.GetValue(finder)!;
                return path;
            }
            Assert.NotNull(Search(budget, out int full));
            int first = 1;
            while (first < full && Search(first, out _) == null) first++;
            // Found with a budget smaller than the search that ran until the goal was the cheapest thing left.
            Assert.True(first < full, $"a path only with the full {full} iterations");
            List<Vector2> early = Search(first, out _)!;
            Assert.True(Vector2.Distance(early[early.Count - 2], new Vector2(8f, 0f)) < 0.5f);
        }
        finally { RoadPathfinder.JunctionStep = step; RoadPathfinder.MaxIterations = budget; }
    }
}
