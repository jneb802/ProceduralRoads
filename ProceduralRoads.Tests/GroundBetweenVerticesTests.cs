using System;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The ground between four heightmap vertices has one owner (<see cref="BuriedPieces.BetweenVertices"/>): the game's two
/// triangles. The staircase planning lattice, the placed-location sampler and the kits' mesh height all read it, so at a
/// saddle cell's centre each gives the mean of the two corners on the diagonal, not of all four.
/// </summary>
[Collection("StairLayout")]
public class GroundBetweenVerticesTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public GroundBetweenVerticesTests() { RoadNetworkGenerator.Reset(); RoadSpatialGrid.Clear(); StairStructures.Reset(); }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        StairStructures.Reset();
        _scope.Dispose();
    }

    /// <summary>A saddle: the vertex (x, z) at 10 m where x + z is even and at 0 m where it is odd.</summary>
    private sealed class SaddleWorld : WorldGenerator
    {
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override float GetBiomeHeight(Heightmap.Biome biome, float wx, float wy, out Color mask, bool preGeneration = false, bool riverPreDN = true)
        {
            mask = default;
            return GetHeight(wx, wy);
        }
        public override float GetHeight(float wx, float wy) => (Mathf.RoundToInt(wx) + Mathf.RoundToInt(wy)) % 2 == 0 ? 10f : 0f;
        public override int GetSeed() => 78;
    }

    // Cell (100, 100): h00 = 10, h10 = 0, h01 = 0, h11 = 10. The four-corner mean is 5; the triangles' diagonal joins (x+1, z) and (x, z+1), both 0.
    [Fact]
    public void ThePlanningLatticeJoinsAnAlternatingCellOnTheDiagonal()
    {
        Assert.Equal(0f, RoadStairRuns.GroundAt(100.5f, 100.5f, new SaddleWorld()), 4);
        Assert.Equal(5f, RoadStairRuns.GroundAt(100.25f, 100.25f, new SaddleWorld()), 4);
    }

    [Fact]
    public void ThePlacedLocationSamplerJoinsAnAlternatingCellOnTheDiagonal()
    {
        var ground = PlacedLocationGround.Sampler(new[] { 10f, 0f, 0f, 10f }, 0, 0, 2, 2);
        Assert.Equal(0f, ground(0.5f, 0.5f), 4);
        Assert.Equal(0f, BuriedPieces.MeshHeight(0.5f, 0.5f, (i, k) => (i + k) % 2 == 0 ? 10f : 0f), 4);
    }
}
