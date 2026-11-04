using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// The other road's line a crossing is judged against is fitted from its road points only, never from a staircase's
/// cut-only (guard) points. Within about 8 m of a staircase's end its guard cuts can
/// outnumber the road points round a touch and turn the fitted line: a real crossing then passed.
/// </summary>
public class CrossingLineTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f ? 0f : 40f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override int GetSeed() => 77;
    }

    public CrossingLineTests() => RoadNetworkGenerator.Reset();

    public void Dispose()
    {
        SetPathfinder(null);
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, pathfinder);

    [Fact]
    public void GuardCutsBesideARoadDoNotTurnTheLineACrossingIsJudgedBy()
    {
        var world = new FlatWorld();
        _scope.WithWorld(world);
        SetPathfinder(new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f });
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, -80f), 0f, new Vector2(0f, 80f), 0f, 4f, "trunk"));
        // A staircase's guard cuts across the trunk at z = -30, east-west: more of them round the touch than road points.
        var cuts = new List<Vector3>();
        for (float x = -3.5f; x <= 3.51f; x += 0.5f)
            foreach (float dz in new[] { -0.6f, 0f, 0.6f })
                cuts.Add(new Vector3(x, 39.85f, -30f + dz));
        RoadSpatialGrid.CommitCuts(cuts, 2f);
        // A road straight across the trunk there, east-west.
        var points = new List<Vector2>();
        var heights = new List<float>();
        for (float x = -60f; x <= 60f; x += 1f) { points.Add(new Vector2(x, -30f)); heights.Add(40f); }
        var across = new List<RoadSpatialGrid.PlannedPath>
        {
            new(points, heights, points.Select(_ => default(RoadPointDebugInfo)).ToList(), 4f, 120f, false),
        };

        Assert.True(RoadNetworkGenerator.CrossesNetwork(across, out Vector2 at), "the road across the trunk was not seen to cross it");
        Assert.True(Vector2.Distance(at, new Vector2(0f, -30f)) < 3f, $"crossed at {at}");
    }
}
