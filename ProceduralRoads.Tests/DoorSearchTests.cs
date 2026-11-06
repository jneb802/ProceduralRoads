using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The search to a place's doors (RoadPathfinder.FindPathBetween): several starts and ends, each priced, and the road found
/// ends at the cheapest end with its price counted, not the nearest.
/// </summary>
public class DoorSearchTests
{
    private sealed class Flat : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float w, out float width) { w = 0f; width = 0f; }
    }

    [Fact]
    public void TheSearchEndsAtTheCheapestDoorWithItsPriceCounted()
    {
        var world = new Flat();
        using var scope = new ValheimWorldScope().WithWorld(world);
        RoadNetworkGenerator.Reset();
        var finder = new RoadPathfinder(world) { Meander = 0f };
        var start = new[] { (new Vector2(0f, 0f), 0f) };
        Vector2 near = new Vector2(80f, 0f), far = new Vector2(160f, 0f);

        Assert.NotNull(finder.FindPathBetween(start, new[] { (near, 200f), (far, 0f) }, null, null));
        Assert.Equal(1, finder.LastEndIndex);
        var path = finder.FindPathBetween(start, new[] { (near, 0f), (far, 0f) }, null, null);
        Assert.NotNull(path);
        Assert.Equal(0, finder.LastEndIndex);
        Assert.Equal(near, path![path.Count - 1]);
    }

    [Fact]
    public void OneStartAndOneEndIsTheOrdinarySearch()
    {
        var world = new Flat();
        using var scope = new ValheimWorldScope().WithWorld(world);
        RoadNetworkGenerator.Reset();
        var finder = new RoadPathfinder(world) { Meander = 0f };
        Vector2 a = new Vector2(0f, 0f), b = new Vector2(120f, 40f);
        var plain = finder.FindPath(a, b);
        float cost = finder.LastPathCost;
        var between = finder.FindPathBetween(new[] { (a, 0f) }, new[] { (b, 0f) }, a, b);
        Assert.Equal(plain, between);
        Assert.Equal(cost, finder.LastPathCost);
    }

    [Fact]
    public void TheSwitchesDefaultAsTheyPlayAndAPointHasNoDoors()
    {
        Assert.Equal(2, RoadNetworkGenerator.DoorTries);
        Assert.Equal(3, RoadNetworkGenerator.DoorWeight);
        Assert.Empty(RoadNetworkGenerator.DoorsOf(new Vector2(0f, 0f), 0f, 4f));
    }
}
