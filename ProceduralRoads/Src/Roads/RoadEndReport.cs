using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Procedural-terrain context near a location. The nearest stored road point
/// is not necessarily a path endpoint. These samples exclude location shaping,
/// compiler deltas and player edits, so they do not measure a visible rim.
/// </summary>
public static class RoadEndReport
{
    public const float SearchMargin = 8f;
    public const int RingSamples = 12;

    public struct Entry
    {
        public string Name;
        public Vector2 Point;
        public Vector2 LocationCentre;
        public float RingRadius;
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
            // Approaches can finish outside the footprint plus road width,
            // smoothing clearance and the local arrival band (up to 22 m).
            var near = RoadSpatialGrid.GetRoadPointsNearPosition(loc.position, RoadSiteProtection.RadiusAt(new Vector2(loc.position.x, loc.position.z), loc.radius) + Mathf.Max(SearchMargin, 24f));
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
                Name = loc.name, Point = best.p, LocationCentre = centre, RingRadius = ring,
                RoadHeight = best.h, TerrainAtEnd = terrain,
                RingMean = sum / RingSamples, RingMin = min, RingMax = max
            });
        }

        rows.Sort((a, b) => Mathf.Abs(b.DeltaRing).CompareTo(Mathf.Abs(a.DeltaRing)));
        return rows;
    }
}
