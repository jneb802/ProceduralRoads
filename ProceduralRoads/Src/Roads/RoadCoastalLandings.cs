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
    private const float Sea = RoadConstants.SeaLevel;
    private static readonly (int x, int z)[] Directions =
        { (-1,-1), (-1,0), (-1,1), (0,-1), (0,1), (1,-1), (1,0), (1,1) };

    public static List<Vector3> Find(Island island, float[] heights, int size,
        Func<float, float, Heightmap.Biome> biomeAt, RoadNetworkOptions options)
    {
        List<Vector3>? result=null;
        using var preparation=new RoadPreparation(p=>FindSteps(island,heights,size,biomeAt,options,p,value=>result=value));
        preparation.Drain(); return result!;
    }

    internal static IEnumerable<bool> FindSteps(Island island,float[] heights,int size,
        Func<float,float,Heightmap.Biome> biomeAt,RoadNetworkOptions options,RoadPreparation preparation,Action<List<Vector3>> complete)
    {
        var candidates = new List<(Vector3 point, float rise)>();
        if (heights.Length != size * size) throw new ArgumentException("Height raster dimensions differ");
        preparation.BeginStage($"Checking island {island.Id+1} coast",island.Cells.Count,"cells visited", "Checking coastal candidates"); yield return true;
        float spacing = island.CellSize;
        float H(int x, int z) => x >= 0 && x < size && z >= 0 && z < size
            ? heights[x * size + z] : float.MinValue;
        bool Known(float h) => h != float.MinValue && !float.IsNaN(h) && !float.IsInfinity(h);
        foreach (var cell in island.Cells)
        {
            if(preparation.Checkpoint(0)) yield return true;
            try
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
                    if(preparation.Checkpoint(0)) yield return true;
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
                {
                    rise = Math.Max(rise, H(cell.x+dx, cell.y+dz) - h);
                    if(preparation.Checkpoint(0)) yield return true;
                }
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
            finally { preparation.CompleteUnit(); }
        }

        // Thin first, using gentleness and coordinates for stable tie breaking.
        preparation.BeginStage($"Spacing island {island.Id+1} landings",candidates.Count,"candidates visited", "Spacing coastal landings"); yield return true;
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
            if (close) { if(preparation.Checkpoint()) yield return true; continue; }
            if (!buckets.TryGetValue((x,z), out var bucket)) buckets[(x,z)] = bucket = new List<Vector3>();
            bucket.Add(p); thinned.Add(p);
            if(preparation.Checkpoint()) yield return true;
        }

        int target = Math.Max(1, Mathf.CeilToInt(island.ApproxArea / AreaPerLanding));
        preparation.BeginStage($"Selecting island {island.Id+1} landings",Math.Min(target,thinned.Count),"landings", "Selecting coastal landings"); yield return true;
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
                if(preparation.Checkpoint(0)) yield return true;
            }
            selected.Add(thinned[best]); thinned.RemoveAt(best);
            if(preparation.Checkpoint()) yield return true;
        }
        complete(selected);
    }

    private static float DistanceSquared(Vector3 a, Vector3 b) =>
        (a.x-b.x)*(a.x-b.x) + (a.z-b.z)*(a.z-b.z);
}

/// <summary>A shore landing the network knew of: where it is, its island, whether the island's destinations took it
/// (<see cref="Chosen"/>) and whether the finished network reaches it (<see cref="Reached"/>, the one reached test, taken
/// when the island's roads were done).</summary>
public sealed class CoastalLandingPlace
{
    public Vector3 Position { get; }
    public int IslandId { get; }
    public bool Chosen { get; }
    public bool Reached { get; }
    public string Name => RoadCoastalLandings.Name;

    public CoastalLandingPlace(Vector3 position, int islandId, bool chosen, bool reached)
    { Position = position; IslandId = islandId; Chosen = chosen; Reached = reached; }
}
