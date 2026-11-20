using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit.Abstractions;
namespace ProceduralRoads.Tests;

/// <summary>The join search judges each arrival at a network cell, not the cell.</summary>
[Collection("RoadStatics")]
public class JoinArrivalTests : System.IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private readonly ITestOutputHelper _output;
    public JoinArrivalTests(ITestOutputHelper output) { _output = output; RoadSpatialGrid.Clear(); }
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
                var path = finder.FindPathToNetwork(new Vector2(0f, 48f), RoadPathfinder.CellSize, () => RoadNetworkGenerator.Reached(new Vector2(0f, 48f), null));
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
                var path = finder.FindPathToNetwork(new Vector2(0f, 48f), RoadPathfinder.CellSize, () => RoadNetworkGenerator.Reached(new Vector2(0f, 48f), null));
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

    /// <summary>The join's level arrival, through construction: the road the shared join (RoadNetworkGenerator.JoinTo)
    /// builds to the junction its search found arrives as that search judged the arrival, along the corridor, level with the
    /// road, and not down the cut's side over the plateau. The road is searched again from its start to the junction
    /// (RoadNetworkGenerator.GenerateRoad) through the network cell the join search arrived by, judged as that search judged
    /// it (RoadPathfinder.ArrivesLevel); with no arrival test it came straight down from the plateau. The corridor's walls, 3.5 m
    /// high, make a cut too short and shallow to refuse (RoadRidges.Counts).</summary>
    [Fact]
    public void AJoinedRoadArrivesLevelAsItsSearchJudgedTheArrival()
    {
        float step = RoadPathfinder.JunctionStep;
        RoadPathfinder.JunctionStep = RoadPathfinder.DefaultJunctionStep;
        try
        {
            var world = new CutWithOneWayIn();
            _scope.WithWorld(world).WithZdos().WithZoneSystem().WithScene();
            RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld(); PlacedLocations.Reset();
            RoadNetworkGenerator.MarkLocationsReady();
            RoadLocationProtection.Set(System.Array.Empty<RoadLocationProtection.Footprint>());
            var road = new List<Vector2> { new(-2f, 0f), new(0f, 0f), new(2f, 0f) };
            RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(road, new List<float> { 40f, 40f, 40f },
                new List<RoadPointDebugInfo> { default, default, default }, 4f, 4f, false));
            RoadSpatialGrid.FinalizeRoadNetwork(); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
            var from = new Vector2(0f, 48f);
            var finder = RoadNetworkGenerator.NewFinder(world, RoadLocationProtection.Clearance(RoadNetworkGenerator.RoadWidth));
            finder.Stairs = false; finder.TerrainVarianceThreshold = 1000f; finder.Meander = 0f;
            var capture = new RoadNetworkGenerator.RoadCapture();
            bool joined;
            using (RoadNetworkGenerator.PlanningWith(finder, capture))
                joined = RoadNetworkGenerator.JoinTo(finder, from, RoadNetworkGenerator.JoinTargets(_ => true), "road_connect",
                    new Vector3(from.x, 0f, from.y), 0f, null, null, null);
            // The corridor is 6 m wide between walls 3.5 m high, a short cut (RoadRidges.Counts: under TrenchMinLength and
            // TrenchMinDepth), so the road the join builds along it is built, arriving along the corridor level with the road.
            Assert.True(joined, RoadSpatialGrid.LastRefusal);
            var built = Assert.IsType<RoadNetworkGenerator.PlannedRoad>(capture.Road);
            // The last of its points before the network cell round the junction: on the corridor, level with the road.
            Vector2 junction = built.Path[built.Path.Count - 1];
            Vector2 before = built.Path.Last(p => Vector2.Distance(p, junction) > RoadPathfinder.CellSize);
            Assert.True(Mathf.Abs(world.GetHeight(before.x, before.y) - 40f) <= 2f * RoadPathfinder.JunctionStep,
                $"it arrives from {before}, over the plateau");
        }
        finally { RoadPathfinder.JunctionStep = step; RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld(); PlacedLocations.Reset(); }
    }

    /// <summary>A road with a 50% pitch between two landings, 40 m to the west and 43 m to the east, on ground that follows it
    /// (T3, tour 10 Nov: a branch met the road at (-2496.5, 6899.1) in the middle of its pitch, the road falling 5 m in 11 m
    /// beside the junction). Water past 100 m.</summary>
    private sealed class PitchedRoadGround : WorldGenerator
    {
        public static float Profile(float x) => x <= -3f ? 40f : x >= 3f ? 43f : 40f + (x + 3f) * 0.5f;
        public override float GetHeight(float x, float z) => Mathf.Abs(x) > 100f || Mathf.Abs(z) > 100f ? 0f : Profile(x);
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
        public override int GetSeed() => 7;
    }

    /// <summary>A join arrives on the road it meets where that road is within the grade cap across the junction, never on
    /// its pitch: the arrival straight down onto the pitch is at grade with the ground, but the road met there falls away
    /// past the cap, so the search ends on a landing either side. Judged by the one arrival owner (ArrivesLevel), so the
    /// join search and the road built to its junction both keep it.</summary>
    [Fact]
    public void AJoinDoesNotArriveOnTheMetRoadsPitch()
    {
        float step = RoadPathfinder.JunctionStep;
        RoadPathfinder.JunctionStep = RoadPathfinder.DefaultJunctionStep;
        using var landing = StaticOverride.Keep(() => RoadPitches.JunctionLanding);
        RoadPitches.JunctionLanding = (float)PlainRoads.ModDefaults["junction landing"];
        try
        {
            var world = new PitchedRoadGround();
            _scope.WithWorld(world);
            RoadSpatialGrid.Clear();
            var road = new List<Vector2>();
            var heights = new List<float>();
            for (int x = -40; x <= 40; x++) { road.Add(new Vector2(x, 0f)); heights.Add(PitchedRoadGround.Profile(x)); }
            RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(road, heights, road.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f, 80f, false));
            RoadSpatialGrid.FinalizeRoadNetwork();
            var finder = new RoadPathfinder(world) { Stairs = false, TerrainVarianceThreshold = 1000f, Meander = 0f };
            var path = finder.FindPathToNetwork(new Vector2(0f, 48f), RoadPathfinder.CellSize, () => RoadNetworkGenerator.Reached(new Vector2(0f, 48f), null));
            Assert.NotNull(path);
            Vector2 junction = path![path.Count - 1];
            _output.WriteLine("path: " + string.Join(" ", path.Select(p => $"({p.x:F1},{p.y:F1})")));
            Assert.True(Mathf.Abs(junction.x) >= 6f, $"it meets the road at {junction}, on its pitch (x -3..3)");
            Assert.True(RoadPathfinder.MeetsWithinCap(junction));
        }
        finally { RoadPathfinder.JunctionStep = step; }
    }

    /// <summary>Every road's meeting with the network asks the junction's grade, not only the join search: a planned road
    /// whose end meets the pitched road mid-pitch is refused by the one meeting test (RoadNetworkGenerator.MeetingRefusal,
    /// asked by the refusal list on every path: generation, approach trials, marked roads, carried edges), and one meeting it
    /// on its landing is not.</summary>
    [Fact]
    public void AnyRoadMeetingTheNetworkOnAPitchIsRefused()
    {
        using var landing = StaticOverride.Keep(() => RoadPitches.JunctionLanding);
        RoadPitches.JunctionLanding = (float)PlainRoads.ModDefaults["junction landing"];
        var world = new PitchedRoadGround();
        _scope.WithWorld(world);
        RoadSpatialGrid.Clear();
        var road = new List<Vector2>();
        var heights = new List<float>();
        for (int x = -40; x <= 40; x++) { road.Add(new Vector2(x, 0f)); heights.Add(PitchedRoadGround.Profile(x)); }
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(road, heights, road.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f, 80f, false));
        RoadSpatialGrid.FinalizeRoadNetwork();
        RoadSpatialGrid.PlannedPath Branch(float x)
        {
            var pts = new List<Vector2>();
            for (int z = 30; z >= 0; z--) pts.Add(new Vector2(x, z));
            float h = PitchedRoadGround.Profile(x);
            return new RoadSpatialGrid.PlannedPath(pts, pts.Select(_ => h).ToList(), pts.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f, 30f, false);
        }
        Assert.Equal(RoadNetworkGenerator.JunctionOverCapRefusal, RoadNetworkGenerator.MeetingRefusal(new() { Branch(0f) }, out Vector2 at));
        Assert.True(Mathf.Abs(at.x) < 3f, $"refused at {at}");
        Assert.Null(RoadNetworkGenerator.MeetingRefusal(new() { Branch(12f) }, out _));
        // A road that rides the pitched road from its landing and leaves it mid-pitch: its plan's ends are on the landing and
        // off the network, but what it stores of its own (RoadRide.Own) starts where it leaves, on the pitch.
        var riding = new List<Vector2>();
        for (int x = -12; x <= 0; x++) riding.Add(new Vector2(x, 0f));
        for (int z = 1; z <= 30; z++) riding.Add(new Vector2(0f, z));
        var ridingHeights = riding.Select(p => p.y == 0f ? PitchedRoadGround.Profile(p.x) : PitchedRoadGround.Profile(0f)).ToList();
        var rides = new RoadSpatialGrid.PlannedPath(riding, ridingHeights, riding.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f, 42f, false);
        Assert.Equal(RoadNetworkGenerator.JunctionOverCapRefusal, RoadNetworkGenerator.MeetingRefusal(new() { rides }, out Vector2 left));
        Assert.True(Mathf.Abs(left.x) < 3f, $"refused at {left}");
    }
}
