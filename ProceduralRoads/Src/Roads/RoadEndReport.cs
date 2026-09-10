using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Where roads end relative to the ground: for every placed location that has a
/// road point within its exterior radius (plus a margin), the nearest road
/// point's height against the natural terrain at that point and against the
/// mean natural height on a ring around it. A large difference is a road that
/// arrives at its location on a ledge or a hump. Pure data; the road_ends
/// console command formats it and writes the CSV.
/// </summary>
public static class RoadEndReport
{
    public const float SearchMargin = 8f;
    public const int RingSamples = 12;

    public struct Entry
    {
        public string Name;
        public Vector2 Point;
        public float RoadHeight;
        public float TerrainAtEnd;
        public float RingMean;
        public float RingMin;
        public float RingMax;
        public float DeltaEnd => RoadHeight - TerrainAtEnd;
        public float DeltaRing => RoadHeight - RingMean;
    }

    /// <summary>Entries sorted by |road height - ring mean|, largest first.</summary>
    public static List<Entry> Compute(
        IEnumerable<(string name, Vector3 position, float radius)> locations,
        float ring, WorldGenerator world)
    {
        var rows = new List<Entry>();
        foreach (var loc in locations)
        {
            var near = RoadSpatialGrid.GetRoadPointsNearPosition(loc.position, loc.radius + SearchMargin);
            if (near.Count == 0)
                continue;

            Vector2 centre = new Vector2(loc.position.x, loc.position.z);
            RoadSpatialGrid.RoadPoint best = near[0];
            float bestDistance = float.MaxValue;
            foreach (var rp in near)
            {
                float d = Vector2.Distance(rp.p, centre);
                if (d < bestDistance) { bestDistance = d; best = rp; }
            }

            float terrain = BiomeBlendedHeight.GetBlendedHeight(best.p.x, best.p.y, world);
            float sum = 0f, min = float.MaxValue, max = float.MinValue;
            for (int i = 0; i < RingSamples; i++)
            {
                float a = i * Mathf.PI * 2f / RingSamples;
                float h = BiomeBlendedHeight.GetBlendedHeight(best.p.x + Mathf.Cos(a) * ring, best.p.y + Mathf.Sin(a) * ring, world);
                sum += h;
                if (h < min) min = h;
                if (h > max) max = h;
            }

            rows.Add(new Entry
            {
                Name = loc.name, Point = best.p, RoadHeight = best.h, TerrainAtEnd = terrain,
                RingMean = sum / RingSamples, RingMin = min, RingMax = max
            });
        }

        rows.Sort((a, b) => Mathf.Abs(b.DeltaRing).CompareTo(Mathf.Abs(a.DeltaRing)));
        return rows;
    }
}
