using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// The memory a generation keeps for speed (GenerationMemory, the Roads.LowMemoryGeneration setting) changes its time and
/// its memory, never its roads: a staircase laid with the pool, its search sets, the lattice kept and its layouts laid ahead
/// is the one laid with none of them, and a search piece taken back from the pool is a new one in every field.
/// </summary>
public class GenerationMemoryTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private readonly bool _low = GenerationMemory.Low;

    private sealed class SharedFloorValleyWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => CantileverTests.SharedFloorValleyGround(wx, wy);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 76;
    }

    /// <summary>The road through a valley (StairRowLayTests.ValleyRoad): down to the bottom at index 4, up to index 6.</summary>
    private static readonly List<Vector2> ValleyRoad = new()
    {
        new(4360f, 6448f), new(4368f, 6448f), new(4376f, 6448f), new(4386f, 6448f),
        new(4392f, 6440f),
        new(4400f, 6440f), new(4408f, 6448f), new(4416f, 6448f), new(4424f, 6448f),
    };

    public GenerationMemoryTests() => RoadNetworkGenerator.Reset();

    public void Dispose()
    {
        GenerationMemory.Low = _low;
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    private static List<RoadStairSpan>? LayValley(WorldGenerator world)
    {
        var steps = new List<(Vector2 from, Vector2 to)> { (ValleyRoad[3], ValleyRoad[4]), (ValleyRoad[4], ValleyRoad[5]), (ValleyRoad[5], ValleyRoad[6]) };
        RoadStairRuns.ResetCache();
        return RoadStairRuns.Lay(ValleyRoad, steps, new List<RoadCrossing>(), world);
    }

    private static Type NodeType => typeof(StairLayout).GetNestedType("Node", BindingFlags.NonPublic)!;
    private static int PooledHere =>
        (int)typeof(StairLayout).GetNestedType("NodePool", BindingFlags.NonPublic)!.GetProperty("Kept", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

    [Fact]
    public void AStaircaseKeepingLessIsTheOneKeepingAll()
    {
        var world = new SharedFloorValleyWorld();
        _scope.WithWorld(world);
        GenerationMemory.Low = false;
        List<RoadStairSpan>? first = LayValley(world);
        Assert.NotNull(first);
        Assert.True(PooledHere > 0, "the search took nothing from the pool");
        // Laid again, from the nodes the first layout gave back.
        List<RoadStairSpan>? reused = LayValley(world);
        GenerationMemory.Low = true;
        List<RoadStairSpan>? low = LayValley(world);
        Assert.Equal(0, PooledHere);
        var differences = StairRowLayTests.Differences(first, reused);
        Assert.True(differences.Count == 0, "from the pool: " + string.Join("; ", differences));
        differences = StairRowLayTests.Differences(first, low);
        Assert.True(differences.Count == 0, "keeping less: " + string.Join("; ", differences));
    }

    [Fact]
    public void ANodeTakenBackFromThePoolIsNewInEveryField()
    {
        object fresh = Activator.CreateInstance(NodeType, nonPublic: true)!;
        object used = Activator.CreateInstance(NodeType, nonPublic: true)!;
        FieldInfo[] fields = NodeType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.True(fields.Length > 10);
        foreach (FieldInfo f in fields)
        {
            Type t = f.FieldType;
            object value = t == NodeType ? Activator.CreateInstance(NodeType, nonPublic: true)!
                : t == typeof(bool) ? true
                : t == typeof(int) ? 7
                : t == typeof(long) ? 7L
                : t == typeof(float) ? 7.5f
                : t == typeof(Vector2) ? new Vector2(3f, 4f)
                : t.IsEnum ? Enum.GetValues(t).GetValue(Enum.GetValues(t).Length - 1)!
                : throw new InvalidOperationException($"a field of a type this test does not set: {f.Name} {t}");
            f.SetValue(used, value);
            Assert.NotEqual(f.GetValue(fresh), f.GetValue(used));
        }
        NodeType.GetMethod("Reset")!.Invoke(used, null);
        foreach (FieldInfo f in fields)
            Assert.True(Equals(f.GetValue(fresh), f.GetValue(used)), $"{f.Name} is not reset");
    }
}
