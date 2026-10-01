using System;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Fixed-size, search-local terrain memo. Collisions replace a sample, never
/// approximate it. Only immutable world-generator facts belong here: road
/// sharing, crossing decisions and configurable move costs are not cached.
/// </summary>
internal sealed class RoadTerrainSamples
{
    internal const int Capacity = 16384; // 448 KiB of value data, independent of explored area.
    private struct Sample
    {
        public Vector2i Position;
        public byte Flags;
        public float Height, River, Variance;
        public Heightmap.Biome Biome;
    }
    private readonly Sample[] samples = new Sample[Capacity];
    private readonly WorldGenerator world;
    public RoadTerrainSamples(WorldGenerator world) { this.world = world; }
    public void Reset() => Array.Clear(samples, 0, samples.Length);

    private ref Sample At(Vector2i position)
    {
        int slot = unchecked((position.x * 73856093) ^ (position.y * 83492791)) & (Capacity - 1);
        ref Sample sample = ref samples[slot];
        if (sample.Position != position)
        {
            sample = default;
            sample.Position = position;
        }
        return ref sample;
    }
    private static Vector2 World(Vector2i p) => new(p.x * RoadPathfinder.CellSize, p.y * RoadPathfinder.CellSize);

    public float Height(Vector2i position)
    {
        ref Sample sample = ref At(position);
        if ((sample.Flags & 1) == 0)
        {
            var p = World(position);
            sample.Height = world.GetHeight(p.x, p.y);
            sample.Flags |= 1;
        }
        return sample.Height;
    }
    public float River(Vector2i position)
    {
        ref Sample sample = ref At(position);
        if ((sample.Flags & 2) == 0)
        {
            var p = World(position);
            world.GetRiverWeight(p.x, p.y, out sample.River, out _);
            sample.Flags |= 2;
        }
        return sample.River;
    }
    public Heightmap.Biome Biome(Vector2i position)
    {
        ref Sample sample = ref At(position);
        if ((sample.Flags & 4) == 0)
        {
            var p = World(position);
            sample.Biome = world.GetBiome(p.x, p.y);
            sample.Flags |= 4;
        }
        return sample.Biome;
    }
    public float Variance(Vector2i position)
    {
        float centerHeight = Height(position);
        ref Sample sample = ref At(position);
        if ((sample.Flags & 8) == 0)
        {
            var p = World(position);
            float min = centerHeight, max = centerHeight;
            for (int i = 0; i < RoadConstants.TerrainVarianceSampleCount; i++)
            {
                float angle = i * Mathf.PI * 2f / RoadConstants.TerrainVarianceSampleCount;
                float h = world.GetHeight(p.x + Mathf.Cos(angle) * RoadConstants.TerrainVarianceSampleRadius,
                    p.y + Mathf.Sin(angle) * RoadConstants.TerrainVarianceSampleRadius);
                min = Mathf.Min(min, h);
                max = Mathf.Max(max, h);
            }
            sample.Variance = max - min;
            sample.Flags |= 8;
        }
        return sample.Variance;
    }
}
