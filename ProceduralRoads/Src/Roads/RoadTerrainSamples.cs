using System;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Fixed-size terrain memo, held for the life of one pathfinder rather than
/// one search. Collisions replace a sample, never approximate it.
///
/// Only immutable world-generator facts belong here. Road sharing, crossing
/// decisions and configurable move costs do NOT: they change as roads are
/// committed, and a memo that outlives a search must not hold anything that
/// can go out of date within one.
///
/// The facts it does hold are true for as long as the generator it was built
/// for is the generator being asked, and a pathfinder is built at the start of
/// a generation and discarded at the end of it. Reset() remains the explicit
/// invalidation for a caller that wants to point one at a different world.
/// </summary>
internal sealed class RoadTerrainSamples
{
    // 1.75 MiB of value data per pathfinder, independent of explored area.
    // Chosen on the station, not from the shape of the cache: at 16,384 a
    // whole-world generation evicted 23.0 M slots and made 204.6 M generator
    // calls; here it evicts 8.3 M and makes 79.7 M, and the run falls from
    // 261-271 s to 227 s for 20 MB more peak working set across 15 workers.
    // 262,144 was measured too and is deliberately not taken: 214 s, only
    // 5.7% better and close to the 10 s spread between two runs of the same
    // build, for another 102 MB.
    internal const int Capacity = 65536;
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

    // How the memo is actually behaving, which a wall-clock second cannot say.
    // A memo belongs to one pathfinder and a pathfinder to one thread, so
    // these are plain fields: one add, on a line the caller already owns, and
    // no interlocking in the hot path. Hits and misses are counted per FACT
    // asked for, not per slot touched; a replacement is an occupied slot
    // evicted by a different position, which is what says the memo is too
    // small for the way it is being asked.
    internal long Hits, Misses, Replacements, TerrainCalls;

    // Whole-generation totals. A memo is folded in when its owner is done,
    // so a pathfinder discarded mid-generation does not take its numbers with
    // it. Interlocked because several island workers fold at once.
    internal static long TotalHits, TotalMisses, TotalReplacements, TotalTerrainCalls;

    internal static void ResetTotals()
    {
        System.Threading.Interlocked.Exchange(ref TotalHits, 0);
        System.Threading.Interlocked.Exchange(ref TotalMisses, 0);
        System.Threading.Interlocked.Exchange(ref TotalReplacements, 0);
        System.Threading.Interlocked.Exchange(ref TotalTerrainCalls, 0);
    }

    internal void FoldIntoTotals()
    {
        System.Threading.Interlocked.Add(ref TotalHits, Hits);
        System.Threading.Interlocked.Add(ref TotalMisses, Misses);
        System.Threading.Interlocked.Add(ref TotalReplacements, Replacements);
        System.Threading.Interlocked.Add(ref TotalTerrainCalls, TerrainCalls);
        Hits = Misses = Replacements = TerrainCalls = 0;
    }

    private ref Sample At(Vector2i position)
    {
        int slot = unchecked((position.x * 73856093) ^ (position.y * 83492791)) & (Capacity - 1);
        ref Sample sample = ref samples[slot];
        if (sample.Position != position)
        {
            if (sample.Flags != 0) Replacements++;
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
            Misses++; TerrainCalls++;
            var p = World(position);
            sample.Height = world.GetHeight(p.x, p.y);
            sample.Flags |= 1;
        }
        else Hits++;
        return sample.Height;
    }
    public float River(Vector2i position)
    {
        ref Sample sample = ref At(position);
        if ((sample.Flags & 2) == 0)
        {
            Misses++; TerrainCalls++;
            var p = World(position);
            world.GetRiverWeight(p.x, p.y, out sample.River, out _);
            sample.Flags |= 2;
        }
        else Hits++;
        return sample.River;
    }
    public Heightmap.Biome Biome(Vector2i position)
    {
        ref Sample sample = ref At(position);
        if ((sample.Flags & 4) == 0)
        {
            Misses++; TerrainCalls++;
            var p = World(position);
            sample.Biome = world.GetBiome(p.x, p.y);
            sample.Flags |= 4;
        }
        else Hits++;
        return sample.Biome;
    }
    public float Variance(Vector2i position)
    {
        float centerHeight = Height(position);
        ref Sample sample = ref At(position);
        if ((sample.Flags & 8) == 0)
        {
            Misses++; TerrainCalls += RoadConstants.TerrainVarianceSampleCount;
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
        else Hits++;
        return sample.Variance;
    }
}
