using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>Exact-coordinate samples for one approach decision. No static state,
/// rounding or retained world references. At capacity, new points are evaluated
/// without retention; correctness never depends on fitting in the cache.</summary>
internal sealed class ApproachTerrainSamples
{
    internal const int DefaultCapacity = 16384;
    private readonly Func<Vector2, float> sample;
    private readonly int capacity;
    private readonly Dictionary<Vector2, float> values = new();
    internal int Count => values.Count;

    internal ApproachTerrainSamples(Func<Vector2, float> sample, int capacity = DefaultCapacity)
    {
        this.sample = sample;
        this.capacity = Math.Max(0, capacity);
    }

    internal float Get(Vector2 point)
    {
        if (values.TryGetValue(point, out float height)) return height;
        height = sample(point);
        if (values.Count < capacity) values.Add(point, height);
        return height;
    }
}
