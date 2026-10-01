using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>Possible boat approaches, using the detector's existing height raster.
/// No prefab, location instance, dock, or guarantee of boat/road access is created.</summary>
public static class RoadCoastalLandings
{
    public const string Name = "CoastLanding";
    public const float AreaPerLanding = 5_000_000;
    public const float Separation = 400;
    public const float Radius = 16;
    private const float Sea = 30;
    private static readonly (int x, int z)[] Directions =
        { (-1,-1), (-1,0), (-1,1), (0,-1), (0,1), (1,-1), (1,0), (1,1) };

    public static List<Vector3> Find(Island island, float[] heights, int size,
        Func<float, float, Heightmap.Biome> biomeAt, RoadNetworkOptions options)
    {
        var candidates = new List<(Vector3 point, float rise)>();
        if (!options.CoastalLandings) return new List<Vector3>();
        if (heights.Length != size * size) throw new ArgumentException("Height raster dimensions differ");
        float spacing = island.CellSize;
        float H(int x, int z) => x >= 0 && x < size && z >= 0 && z < size
            ? heights[x * size + z] : float.MinValue;
        bool Known(float h) => h != float.MinValue && !float.IsNaN(h) && !float.IsInfinity(h);
        foreach (var cell in island.Cells)
        {
            float h = H(cell.x, cell.y);
            if (!Known(h) || h < RoadPathfinder.LandingFloor || h > Sea + 6) continue;
            // First screen is entirely in memory; query the biome only at a low shore.
            int shoreReach = Mathf.CeilToInt(32 / spacing);
            if (!Directions.Any(d => Known(H(cell.x+d.x*shoreReach, cell.y+d.z*shoreReach))
                && H(cell.x+d.x*shoreReach, cell.y+d.z*shoreReach) < Sea)) continue;
            var p = island.CellToWorld(cell);

            // A continuous sampled water approach, not a deep point behind another bank.
            // 64 m of water rejects narrow rivers/puddles; depth must reach 2 m by 150 m.
            bool approach = false;
            foreach (var d in Directions)
            {
                bool deep = false, wet = false;
                float waterLength = 0;
                float step = spacing * (d.x != 0 && d.z != 0 ? 1.41421356f : 1);
                for (int i = 1; i * step <= 150; i++)
                {
                    float water = H(cell.x + d.x*i, cell.y + d.z*i);
                    if (!Known(water)) break;
                    if (water >= Sea)
                    {
                        if (wet || i * step > 32 || water > h + 1) break;
                        continue;
                    }
                    wet = true;
                    waterLength += step;
                    deep |= water <= Sea - 2;
                    if (deep && waterLength >= 64) { approach = true; break; }
                }
                if (approach) break;
            }
            if (!approach) continue;

            // Reject steep surroundings. This bounded window reads cached heights,
            // not thousands of additional WorldGenerator queries per candidate.
            float rise = 0;
            int reach = Mathf.CeilToInt(100 / spacing);
            for (int dx = -reach; dx <= reach && rise <= 8; dx++)
                for (int dz = -reach; dz <= reach; dz++)
                    rise = Math.Max(rise, H(cell.x+dx, cell.y+dz) - h);
            if (rise > 8) continue;

            // The biome query is the only thing here that asks the world
            // generator; everything above reads the raster the detector already
            // built. It runs last so it is paid once per surviving candidate
            // rather than once per low shore cell on every island.
            var biome = biomeAt(p.x, p.y);
            if (!options.Allows(biome) || (biome != Heightmap.Biome.Meadows
                && biome != Heightmap.Biome.BlackForest && biome != Heightmap.Biome.Plains
                && biome != Heightmap.Biome.Mistlands)) continue;

            candidates.Add((new Vector3(p.x, h, p.y), rise));
        }

        // Thin first, using gentleness and coordinates for stable tie breaking.
        var buckets = new Dictionary<(int,int), List<Vector3>>();
        var thinned = new List<Vector3>();
        foreach (var candidate in candidates.OrderBy(c => c.rise).ThenBy(c => c.point.y)
                     .ThenBy(c => c.point.x).ThenBy(c => c.point.z))
        {
            var p = candidate.point;
            int x = Mathf.FloorToInt(p.x / Separation), z = Mathf.FloorToInt(p.z / Separation);
            bool close = false;
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    if (buckets.TryGetValue((x+dx,z+dz), out var nearby)
                        && nearby.Any(q => DistanceSquared(p,q) < Separation*Separation)) close = true;
            if (close) continue;
            if (!buckets.TryGetValue((x,z), out var bucket)) buckets[(x,z)] = bucket = new List<Vector3>();
            bucket.Add(p); thinned.Add(p);
        }

        int target = Math.Max(1, Mathf.CeilToInt(island.ApproxArea / AreaPerLanding));
        var selected = new List<Vector3>();
        while (selected.Count < target && thinned.Count > 0)
        {
            // After the gentlest first site, spread along the island's coast.
            int best = 0;
            float distance = -1;
            for (int i = 0; i < thinned.Count && selected.Count > 0; i++)
            {
                float nearest = selected.Min(p => DistanceSquared(p, thinned[i]));
                if (nearest > distance) { distance = nearest; best = i; }
            }
            selected.Add(thinned[best]); thinned.RemoveAt(best);
        }
        return selected;
    }

    private static float DistanceSquared(Vector3 a, Vector3 b) =>
        (a.x-b.x)*(a.x-b.x) + (a.z-b.z)*(a.z-b.z);
}
