using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>Destination and island selection. Contains no game lifecycle or retained caches.</summary>
public static class RoadNetworkSelection
{
    public static List<Island> Islands(IReadOnlyList<Island> islands,
        Func<Island, int> contentCount,
        int percentage, RoadNetworkOptions options)
    {
        // Zero is a real opt-out, including required destinations.
        if (percentage <= 0) return new List<Island>();
        int count = Math.Max(1, Mathf.RoundToInt(islands.Count * Math.Min(100, percentage) / 100f));
        var ranked = options.ContentFirst
            ? islands.OrderByDescending(contentCount).ThenByDescending(i => i.ApproxArea).ThenBy(i => i.Id)
            : islands.OrderByDescending(i => i.ApproxArea).ThenBy(i => i.Id);
        // Bosses and quests are protected inside selected islands, not a reason
        // to add more islands. Some boss islands deliberately remain roadless.
        return ranked.Take(count).ToList();
    }

    /// <summary>Destinations an island earns: eight, plus four for every two
    /// square kilometres, capped by MaxLocationsPerIsland. One formula and one
    /// cap - the density multiplier this replaced was a third knob that could
    /// only be set to a value the other two already expressed.</summary>
    public static int Quota(float area, int cap)
    {
        double requested = (2 + Math.Floor(Math.Max(0, area) / 2000000d)) * 4;
        return (int)Math.Min(Math.Max(2, cap), Math.Max(2, requested));
    }

    public static List<(string name, Vector3 position, float radius)> Destinations(
        List<(string name, Vector3 position, float radius)> candidates, int quota, float area,
        Func<string, int> priority, Func<string, bool> required, int seed, RoadNetworkOptions options)
    {
        // Stable input makes the seeded draw independent of location enumeration order.
        var ordered = candidates.OrderBy(c => c.position.x).ThenBy(c => c.position.z)
            .ThenBy(c => c.name, StringComparer.Ordinal).ToList();
        var selected = ordered.Where(c => required(c.name)).ToList();
        var optional = ordered.Where(c => !required(c.name)).ToList();
        int room = Math.Max(0, quota - selected.Count);
        if (room >= optional.Count) { selected.AddRange(optional); return selected; }
        if (room == 0) return selected;
        if (!options.WeightedDestinations)
        {
            selected.AddRange(optional.OrderByDescending(c => priority(c.name)).Take(room));
            return selected;
        }

        float side = options.TargetSubAreas > 0 && area > 0
            ? Mathf.Sqrt(area / options.TargetSubAreas) : 0;
        if (side <= 0)
        {
            selected.AddRange(Draw(optional, room, priority, seed, options));
            return selected;
        }
        (int, int) Cell(Vector3 p) => (Mathf.FloorToInt(p.x / side), Mathf.FloorToInt(p.z / side));
        var buckets = optional.GroupBy(c => Cell(c.position)).ToDictionary(g => g.Key, g => g.ToList());
        // Start in the richest sub-area, then visit the farthest from those already visited.
        var pool = buckets.Keys.OrderByDescending(k => buckets[k].Count)
            .ThenBy(k => k.Item1).ThenBy(k => k.Item2).ToList();
        var keys = new List<(int, int)>();
        while (pool.Count > 0)
        {
            int best = 0;
            if (keys.Count > 0)
            {
                double bestDistance = -1;
                for (int i = 0; i < pool.Count; i++)
                {
                    double nearest = keys.Min(k => SquaredDistance(k, pool[i]));
                    if (nearest > bestDistance) { bestDistance = nearest; best = i; }
                }
            }
            keys.Add(pool[best]); pool.RemoveAt(best);
        }
        var turns = keys.Select(k => selected.Count(c => Cell(c.position) == k)).ToArray();
        var grants = new int[keys.Count];
        while (room > 0)
        {
            int best = -1;
            for (int i = 0; i < keys.Count; i++)
                if (grants[i] < buckets[keys[i]].Count && (best < 0 || turns[i] < turns[best])) best = i;
            if (best < 0) break;
            turns[best]++; grants[best]++; room--;
        }
        for (int i = 0; i < keys.Count; i++)
            selected.AddRange(Draw(buckets[keys[i]], grants[i], priority, seed, options));
        return selected;
    }

    private static double SquaredDistance((int, int) a, (int, int) b)
    {
        double x = (double)a.Item1 - b.Item1, z = (double)a.Item2 - b.Item2;
        return x * x + z * z;
    }

    private static List<(string name, Vector3 position, float radius)> Draw(
        List<(string name, Vector3 position, float radius)> candidates, int count,
        Func<string, int> priority, int seed, RoadNetworkOptions options)
    {
        var result = new List<(string name, Vector3 position, float radius)>();
        var pool = new List<(string name, Vector3 position, float radius)>(candidates);
        while (result.Count < count && pool.Count > 0)
        {
            int best = 0;
            double bestScore = double.NegativeInfinity;
            for (int i = 0; i < pool.Count; i++)
            {
                double nearest = result.Count == 0 ? 0 : result.Min(p => Vector3.Distance(p.position, pool[i].position));
                double logWeight = options.WeightExponent * Math.Log(Math.Max(1, priority(pool[i].name)))
                    - nearest / Math.Max(1, options.DistanceScale);
                double u = DrawValue(unchecked(seed + result.Count * 7919), pool[i].position, pool[i].name);
                // Equivalent weighted-key ranking in log space, avoiding underflow at long distances.
                double score = logWeight - Math.Log(-Math.Log(u));
                if (score > bestScore) { bestScore = score; best = i; }
            }
            result.Add(pool[best]); pool.RemoveAt(best);
        }
        return result;
    }

    private static double DrawValue(int seed, Vector3 position, string name)
    {
        unchecked
        {
            uint hash = 2166136261u;
            foreach (char c in name) hash = (hash ^ c) * 16777619u;
            hash = (hash ^ (uint)Mathf.RoundToInt(position.x)) * 16777619u;
            hash = (hash ^ (uint)Mathf.RoundToInt(position.z)) * 16777619u;
            hash = (hash ^ (uint)seed) * 16777619u;
            hash ^= hash >> 15;
            return Math.Max(1e-6, Math.Min(1 - 1e-6, (hash % 1000003u) / 1000003d));
        }
    }
}
