using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;
using Xunit.Abstractions;
namespace ProceduralRoads.Tests;

/// <summary>The join search offers a junction only where the road has room for its junction landing before the stretch it
/// shares with the road it meets (RoadPathfinder.ArrivesLevel, RoadSpatialGrid.RoomToLandBefore), as the planner lays it
/// (RoadPitches.AcceptLanding). Before, the search offered shallow merges onto steep trunks that the planner then refused.</summary>
[Collection("RoadStatics")]
public class JoinLandingRoomTests : System.IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private readonly ITestOutputHelper _output;
    public JoinLandingRoomTests(ITestOutputHelper output) { _output = output; RoadSpatialGrid.Clear(); }
    public void Dispose() { RoadSpatialGrid.Clear(); _scope.Dispose(); }

    /// <summary>A plane climbing 20 % along z; water past 200 m.</summary>
    private sealed class Slope : WorldGenerator
    {
        public float BermTo = float.NegativeInfinity;
        public override float GetHeight(float x, float z) => Mathf.Abs(x) > 200f || Mathf.Abs(z) > 200f ? 0f
            : 60f + 0.2f * z + (x > 4f && x < 12f && z < BermTo ? 8f : 0f);
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
        public override int GetSeed() => 7;
    }

    /// <summary>A trunk up the 20 % plane along z = x * 0: its points 1 m apart at the ground's height.</summary>
    private static void Trunk()
    {
        var road = new List<Vector2>(); var heights = new List<float>();
        for (int z = -150; z <= 150; z++) { road.Add(new Vector2(0f, z)); heights.Add(60f + 0.2f * z); }
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(road, heights, road.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f, 300f, false));
        RoadSpatialGrid.FinalizeRoadNetwork();
    }

    /// <summary>On the 20 % trunk a way in converging at a shallow angle has its landing beside the trunk, held up its climb:
    /// no room. A square way in lands level on the plane's contour.</summary>
    [Fact]
    public void AShallowMergeOntoASteepTrunkHasNoRoomToLandAndASquareOneHas()
    {
        PlainRoads.WithModDefaults(() =>
        {
            _scope.WithWorld(new Slope());
            Trunk();
            // From the junction outward: along the trunk's side, 3 m off it, then away.
            Assert.False(RoadSpatialGrid.RoomToLandBefore(new List<Vector2> { new(0f, 0f), new(3f, -8f), new(6f, -16f), new(10f, -24f) }, 4f));
            Assert.True(RoadSpatialGrid.RoomToLandBefore(new List<Vector2> { new(0f, 0f), new(8f, 0f), new(16f, 0f), new(24f, 0f) }, 4f));
        });
    }

    /// <summary>The production join (RoadPathfinder.FindPathToNetwork, then the planner on the road it found): a road from
    /// behind an 8 m berm beside a 20 % trunk (x 4..12, up to z = -20). The cheapest junction, straight over the berm onto the
    /// trunk at a slant, has no room for its landing where it leaves the trunk and is not offered (the planner refused the
    /// road to it as held to the trunk over the grade cap); the search goes round the berm's end and arrives square, and the
    /// road planned on it is built.</summary>
    [Fact]
    public void AJoinOntoASteepTrunkIsOfferedOnlyWhereItCanLand()
    {
        float step = RoadPathfinder.JunctionStep;
        RoadPathfinder.JunctionStep = RoadPathfinder.DefaultJunctionStep;
        try
        {
            PlainRoads.WithModDefaults(() =>
            {
                RoadWiggle.Amplitude = 0f;
                var world = new Slope { BermTo = -20f };
                _scope.WithWorld(world);
                RoadSpatialGrid.Clear();
                Trunk();
                var from = new Vector2(16f, -56f);
                // The slanting way in over the berm, junction outward: no room.
                Assert.False(RoadSpatialGrid.RoomToLandBefore(new List<Vector2> { new(0f, -64f), new(8f, -60f), from }, 4f));
                var finder = new RoadPathfinder(world) { Stairs = false, TerrainVarianceThreshold = 1000f, Meander = 0f };
                var path = finder.FindPathToNetwork(from, RoadPathfinder.CellSize, () => RoadNetworkGenerator.Reached(from, null));
                Assert.NotNull(path);
                _output.WriteLine("path: " + string.Join(" ", path!.Select(p => $"({p.x:F1},{p.y:F1})")));
                var way = new List<Vector2>(path); way.Reverse();
                Assert.True(RoadSpatialGrid.RoomToLandBefore(way, 4f), "the junction offered has no room to land");
                Assert.True(path[path.Count - 1].y >= -20f, $"it joins at {path[path.Count - 1]}, beside the berm");
                var plan = RoadSpatialGrid.PlanRoadPath(path, 4f, world);
                Assert.True(plan != null, $"the road to it is refused: {RoadSpatialGrid.LastRefusal}");
            });
        }
        finally { RoadPathfinder.JunctionStep = step; }
    }
}
