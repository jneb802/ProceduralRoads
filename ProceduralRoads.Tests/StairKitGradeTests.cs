using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// A staircase's grade limit follows its kit, not each step's biome: the search may buy a step up to 2:1 where the kit holds
/// its flights out from the face (StairSupport). A mostly-Meadows climb is stone now, as every new staircase outside the
/// Mistlands is, so a Mountain band's steeper step on it is within its kit's grade.
/// </summary>
public class StairKitGradeTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    /// <summary>Meadows, a 1:1 slope from x = 0 to 24, with a Mountain band from 8 to 12 where it steepens to 1.5:1.</summary>
    private sealed class ClippedFaceWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            float x = Mathf.Clamp(wx, 0f, 24f);
            return 40f + x + 0.5f * Mathf.Clamp(x - 8f, 0f, 4f) * 2f;
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            wx >= 8f && wx < 12f ? Heightmap.Biome.Mountain : Heightmap.Biome.Meadows;
        public override int GetSeed() => 77;
    }

    public StairKitGradeTests() => RoadNetworkGenerator.Reset();

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    [Fact]
    public void AMeadowsClimbIsStoneAndItsSteeperStepIsWithinItsGrade()
    {
        var world = new ClippedFaceWorld();
        _scope.WithWorld(world);
        var path = new List<Vector2>();
        for (float x = -8f; x <= 32.01f; x += 4f) path.Add(new Vector2(x, 0f));
        var steps = new List<(Vector2 from, Vector2 to)>();
        for (int i = 1; i < path.Count; i++)
            if (Mathf.Abs(world.GetHeight(path[i].x, 0f) - world.GetHeight(path[i - 1].x, 0f)) / 4f > RoadPathfinder.StairMinGrade) steps.Add((path[i - 1], path[i]));
        Func<float, float, float> ground = (x, z) => world.GetHeight(x, z);

        // The kit RoadStairRuns.Lay builds the stretch of: stone, though most of it is Meadows.
        Assert.Same(StairKit.Stone, StairKit.ForLine(path, (x, z) => world.GetBiome(x, z)));
        Assert.All(steps, step => Assert.False(RoadPathfinder.StairStepTooSteep(
            Mathf.Abs(ground(step.to.x, step.to.y) - ground(step.from.x, step.from.y)) / Vector2.Distance(step.from, step.to))));
    }
}
