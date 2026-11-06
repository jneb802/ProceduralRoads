using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// What a road staircase's top is carried on with past its top (RoadStairRuns.TopOntoSurface: stairs climbed on, then a
/// floor or a crest onto the surface) passes the same check as the staircase itself, or another finish is tried, or the top
/// stays as it is. Unchecked, up to three stairs and a crest past the top could land on
/// another road, another staircase, water or a place.
/// </summary>
public class StairTopFitsTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    /// <summary>Flats at 40 west of x = 0, a face of 1.2 to 64 at x = 20, then ground rising 0.3 east of it (the lip past
    /// a top laid to the face's edge stands over it). Sea past 200 m.</summary>
    private sealed class LipWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            return wx < 20f ? 40f + Mathf.Clamp(wx * 1.2f, 0f, 24f) : 64f + Mathf.Min(6f, (wx - 20f) * 0.3f);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    public StairTopFitsTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
    }

    public void Dispose()
    {
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    private static (List<Vector2> path, List<(Vector2 from, Vector2 to)> steps) Road(WorldGenerator world)
    {
        var path = new List<Vector2>();
        for (float x = -16f; x <= 48.01f; x += 8f) path.Add(new Vector2(x, 0f));
        var steps = new List<(Vector2 from, Vector2 to)>();
        for (int i = 1; i < path.Count; i++)
            if (Mathf.Abs(world.GetHeight(path[i].x, 0f) - world.GetHeight(path[i - 1].x, 0f)) / 8f > 0.5f) steps.Add((path[i - 1], path[i]));
        return (path, steps);
    }

    [Fact]
    public void WhatATopIsCarriedOnWithNeverLandsOnAnotherRoad()
    {
        var world = new LipWorld();
        _scope.WithWorld(world);
        var (path, steps) = Road(world);
        // Laid alone, the top is carried on past its path point.
        List<RoadStairSpan>? alone = RoadStairRuns.Lay(path, steps, new List<RoadCrossing>(), world);
        Assert.NotNull(alone);
        RoadStairSpan span = Assert.Single(alone!);
        float lastPathX = path[span.ToIndex].x;
        var past = span.Plan.Pieces.Where(p => p.Kind != StairPieceKind.Support && p.Position.x > lastPathX - 1f).ToList();
        Assert.True(past.Count > 0, $"no piece carried on past the top at x {lastPathX}: top {span.Plan.End}, road from {span.ToPoint}");
        // Another road across where those pieces stood, north to south, at the ground's height.
        float across = past.Max(p => p.Position.x);
        var other = new List<Vector2>();
        for (float z = -60f; z <= 60.01f; z += 1f) other.Add(new Vector2(across, z));
        Assert.True(RoadSpatialGrid.AddRoadPath(other, 4f, world));
        RoadSpatialGrid.FinalizeRoadNetwork();
        Assert.True(RoadSpatialGrid.TryGetRoadWithin(new Vector2(across, 0f), 1f, out _));
        RoadStairRuns.ResetCache();

        List<RoadStairSpan>? laid = RoadStairRuns.Lay(path, steps, new List<RoadCrossing>(), world);

        // Laid without the pieces past the top, or not laid: never a walked piece on the other road.
        if (laid != null)
            foreach (RoadStairSpan s in laid)
                Assert.DoesNotContain(s.Plan.Pieces, p => p.Kind != StairPieceKind.Support && Mathf.Abs(p.Position.x - across) < RoadStairRuns.OnRoad);
    }
}
