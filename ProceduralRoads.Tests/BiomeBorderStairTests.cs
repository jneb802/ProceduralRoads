using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;
using Xunit.Abstractions;

namespace ProceduralRoads.Tests;

/// <summary>
/// The road search prices its grades on the ground the game renders, not on WorldGenerator.GetHeight. GetHeight is the
/// height of the biome at the point; the game renders a zone from the biomes at its heightmap's corners
/// (<see cref="BiomeBlendedHeight"/>). Where the point biome changes inside a zone whose corners are two biomes, GetHeight
/// jumps from one biome's height to the other's: a cliff that is not in the world.
/// The world here is the zone (4288, -3520) on a road from a coastal landing to the Bonemass altar, rebuilt from numbers
/// read in game (<see cref="CarryWadeTests.DumpedZone"/>): heightmap corners Black Forest to the south, Swamp to the north;
/// point biome Black Forest at the search's cells on z = -3528 and -3536 and Swamp on z = -3520 and -3512 (GetHeight 45.6
/// and 28.7 at x = 4296, 8 m apart). The rendered ground between them rises about a metre. A stone staircase of six
/// stairs was laid there for that metre, on gentle ground far inside the earthwork cap.
/// </summary>
public class BiomeBorderStairTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private readonly ITestOutputHelper _out;

    public BiomeBorderStairTests(ITestOutputHelper output)
    {
        _out = output;
        RoadNetworkGenerator.Reset();
    }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    // River weights read in game at the search's cell centres (GetRiverWeight); every other cell read 0.
    private static readonly Dictionary<(int x, int z), float> Rivers = new()
    {
        [(4280, -3536)] = 0.43f, [(4288, -3536)] = 0.10f,
        [(4280, -3528)] = 0.59f, [(4288, -3528)] = 0.19f,
        [(4280, -3520)] = 0.42f, [(4288, -3520)] = 0.10f,
        [(4280, -3512)] = 0.19f,
    };

    private CarryWadeTests.DumpedZone World()
    {
        var world = new CarryWadeTests.DumpedZone { ForestSouthOf = -3524f, Rivers = Rivers };
        _scope.WithWorld(world);
        return world;
    }

    /// <summary>The cliff GetHeight reads between two cells 8 m apart across the point-biome border, and the metre or so the
    /// game renders there.</summary>
    [Fact]
    public void GetHeightReadsACliffTheGameDoesNotRender()
    {
        var world = World();
        Vector2 swamp = new(4296f, -3520f), forest = new(4296f, -3528f);
        float rawRise = world.GetHeight(forest.x, forest.y) - world.GetHeight(swamp.x, swamp.y);
        float renderedRise = BiomeBlendedHeight.GetBlendedHeight(forest.x, forest.y, world) - BiomeBlendedHeight.GetBlendedHeight(swamp.x, swamp.y, world);
        _out.WriteLine($"GetHeight {world.GetHeight(swamp.x, swamp.y):F2} -> {world.GetHeight(forest.x, forest.y):F2} (rise {rawRise:F2} m, {rawRise / 8f:P0}); "
            + $"rendered {BiomeBlendedHeight.GetBlendedHeight(swamp.x, swamp.y, world):F2} -> {BiomeBlendedHeight.GetBlendedHeight(forest.x, forest.y, world):F2} (rise {renderedRise:F2} m, {renderedRise / 8f:P0})");
        Assert.InRange(rawRise, 16f, 18f);
        Assert.InRange(renderedRise, 0f, 2.5f);
        Assert.True(renderedRise / 8f < RoadGrade.Configured, "the rendered rise is within the road's grade cap");
    }

    /// <summary>From the swamp side of the border south into the Black Forest, as the road ran, the search lays a road: no
    /// stair step is bought for the rendered rise. Priced on GetHeight it bought one from (4304, -3520) to (4296, -3536):
    /// 15.5 m over 17.9 m (87 %), the road refused there above its grade cap, where the game renders 2.9 m (16 %).</summary>
    [Fact]
    public void TheSearchClimbsTheBorderAsRoad()
    {
        var world = World();
        var finder = new RoadPathfinder(world);
        List<Vector2>? path = finder.FindPath(new Vector2(4296f, -3512f), new Vector2(4296f, -3536f));
        Assert.NotNull(path);
        foreach (var (from, to) in finder.LastStairSteps)
        {
            float raw = world.GetHeight(to.x, to.y) - world.GetHeight(from.x, from.y);
            float rendered = BiomeBlendedHeight.GetBlendedHeight(to.x, to.y, world) - BiomeBlendedHeight.GetBlendedHeight(from.x, from.y, world);
            float dist = Vector2.Distance(from, to);
            _out.WriteLine($"stair step ({from.x:F0},{from.y:F0}) {world.GetBiome(from.x, from.y)} -> ({to.x:F0},{to.y:F0}) {world.GetBiome(to.x, to.y)}: "
                + $"GetHeight rise {raw:F2} m ({Mathf.Abs(raw) / dist:P0}), rendered rise {rendered:F2} m ({Mathf.Abs(rendered) / dist:P0}) over {dist:F1} m");
        }
        _out.WriteLine($"path: {string.Join(" ", path!.Select(p => $"({p.x:F0},{p.y:F0})"))}");
        Assert.Empty(finder.LastStairSteps);
    }
}
