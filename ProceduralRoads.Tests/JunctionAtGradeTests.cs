using System.Collections.Generic;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;
namespace ProceduralRoads.Tests;
[Collection("RoadStatics")]
public class JunctionAtGradeTests : System.IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    public JunctionAtGradeTests()
    {
        _scope.WithWorld(new SyntheticWorld { HasRiver = false, HasMountain = false });
        RoadSpatialGrid.Clear();
    }
    public void Dispose() { RoadSpatialGrid.Clear(); _scope.Dispose(); }

    [Fact]
    public void ARoadIsJoinedOnlyWhereItStandsAboutTheGroundsHeight()
    {
        var path = new List<Vector2>();
        for (int i = -6; i <= 6; i++) path.Add(new Vector2(i * 8f, 0f));
        RoadSpatialGrid.AddRoadPath(path, 4f, WorldGenerator.instance);
        RoadSpatialGrid.FinalizeRoadNetwork();
        var near = new Vector2(0f, 3f);
        Assert.True(RoadSpatialGrid.TryGetRoadHeightWithin(near, 6f, out float surface));
        // Arriving on ground at the road's height: a junction.
        Assert.True(RoadSpatialGrid.TryGetRoadAtGradeWithin(near, 6f, surface + 1f, 1.5f, out Vector2 at));
        Assert.True(Vector2.Distance(at, near) <= 6f);
        // Arriving 3 m above or below it (the road on a fill or in a cut beside here): none.
        Assert.False(RoadSpatialGrid.TryGetRoadAtGradeWithin(near, 6f, surface + 3f, 1.5f, out _));
        Assert.False(RoadSpatialGrid.TryGetRoadAtGradeWithin(near, 6f, surface - 3f, 1.5f, out _));
    }

    /// <summary>A Meadows world at 40 m with a Black Forest patch inside the heightmap of zone (0, 0) touching none of its
    /// corners: the game renders the patch at Meadows height, while the world generator reads the forest's 44 m there.</summary>
    private sealed class PatchWorld : WorldGenerator
    {
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            Mathf.Abs(wx) <= 4f && Mathf.Abs(wy) <= 4f ? Heightmap.Biome.BlackForest : Heightmap.Biome.Meadows;
        public override float GetBiomeHeight(Heightmap.Biome biome, float wx, float wy, out Color mask, bool preGeneration = false, bool riverPreDN = true)
        {
            mask = default;
            return Mathf.Abs(wx) > 100f || Mathf.Abs(wy) > 100f ? 0f : biome == Heightmap.Biome.BlackForest ? 44f : 40f;
        }
        public override float GetHeight(float wx, float wy) => GetBiomeHeight(GetBiome(wx, wy), wx, wy, out _);
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
        public override int GetSeed() => 7;
    }

    /// <summary>The join search judges a junction on the ground every junction rule reads, the ground the game renders
    /// (RoadSpatialGrid.JunctionGround), as the trunk branch does: on the world generator's raw height, a road at 40 m 6 m
    /// from a start the game renders at 40 m was not at grade (the raw height there is a forest patch's 44 m).</summary>
    [Fact]
    public void AJunctionIsJudgedOnTheGroundTheGameRenders()
    {
        float step = RoadPathfinder.JunctionStep;
        RoadPathfinder.JunctionStep = RoadPathfinder.DefaultJunctionStep;
        try
        {
            var world = new PatchWorld();
            _scope.WithWorld(world);
            RoadSpatialGrid.Clear();
            var road = new List<Vector2>();
            for (int x = -20; x <= 20; x += 2) road.Add(new Vector2(x, 6f));
            RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(road, road.ConvertAll(_ => 40f), road.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 40f, false));
            RoadSpatialGrid.FinalizeRoadNetwork();
            Assert.Equal(44f, world.GetHeight(0f, 0f));
            Assert.Equal(40f, RoadSpatialGrid.JunctionGround(new Vector2(0f, 0f), world), 3);
            var finder = new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f };
            finder.FindPathToNetwork(new Vector2(0f, 0f), 8f);
            Assert.True(finder.AlreadyOnNetwork, "the road 6 m off is at grade with the ground the game renders here");
        }
        finally { RoadPathfinder.JunctionStep = step; }
    }
}

[Collection("RoadStatics")]
public class TrunkBranchTests : System.IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private readonly float snap = RoadNetworkGenerator.RoadSnap;
    private readonly float step = RoadPathfinder.JunctionStep;
    public TrunkBranchTests()
    {
        RoadPathfinder.JunctionStep = RoadPathfinder.DefaultJunctionStep;
        _scope.WithWorld(new SyntheticWorld { HasRiver = false, HasMountain = false });
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.RoadSnap = 6f;
    }
    public void Dispose()
    {
        RoadNetworkGenerator.RoadSnap = snap;
        RoadPathfinder.JunctionStep = step;
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    [Fact]
    public void ARoadAlongsideTheOneServingItsPlaceBranchesOffItThere()
    {
        // The place's road runs east along z = 0; a second road leaves the same place 6 m beside it,
        // never touching it, for 60 m, then turns away north. It should share the first as a trunk
        // and set off from it where they part: its start moves onto the trunk at about x = 60.
        var trunk = new List<Vector2>();
        for (int i = 0; i <= 15; i++) trunk.Add(new Vector2(i * 8f, 0f));
        RoadSpatialGrid.AddRoadPath(trunk, 4f, WorldGenerator.instance);
        RoadSpatialGrid.FinalizeRoadNetwork();
        var path = new List<Vector2>();
        for (int i = 0; i <= 7; i++) path.Add(new Vector2(i * 8f, 6f));
        for (int i = 1; i <= 5; i++) path.Add(new Vector2(56f + i * 4f, 6f + i * 8f));
        List<Vector2> kept = RoadNetworkGenerator.DropStubs(path, startReached: true, endReached: false)!;
        Assert.True(Mathf.Abs(kept[0].y) < 2.5f, $"starts at {kept[0]}, not on the trunk");
        Assert.InRange(kept[0].x, 40f, 64f);
        Assert.Equal(path[path.Count - 1], kept[kept.Count - 1]);
        // A place no other road serves keeps its whole road.
        Assert.Equal(path.Count, RoadNetworkGenerator.DropStubs(path, startReached: false, endReached: false)!.Count);
    }
}
