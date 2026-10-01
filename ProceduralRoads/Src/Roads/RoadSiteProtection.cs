using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>All location footprints, including locations not selected for roads.
/// Shared by routing and the terrain writer; indexed so searches never scan
/// the world's full location list per move. No Unity objects are retained.</summary>
public static class RoadSiteProtection
{
    public readonly struct Footprint
    {
        public readonly Vector2 Centre;
        public readonly float Radius;
        public Footprint(Vector2 centre, float radius) { Centre = centre; Radius = radius; }
    }
    public static Func<IEnumerable<Footprint>?>? Source;
    private const float Cell = 128f;
    private static readonly Dictionary<Vector2i, List<Footprint>> cells = new(RoadGridComparer.Instance);
    private static bool ready;

    public static void Reset() { cells.Clear(); ready = false; }
    public static void Set(IEnumerable<Footprint> footprints)
    {
        cells.Clear();
        foreach (var f in footprints)
        {
            if (!(f.Radius > 0f) || float.IsInfinity(f.Radius) ||
                float.IsNaN(f.Centre.x) || float.IsNaN(f.Centre.y) ||
                float.IsInfinity(f.Centre.x) || float.IsInfinity(f.Centre.y)) continue;
            for (int z = Bin(f.Centre.y - f.Radius); z <= Bin(f.Centre.y + f.Radius); z++)
                for (int x = Bin(f.Centre.x - f.Radius); x <= Bin(f.Centre.x + f.Radius); x++)
                {
                    var key = new Vector2i(x, z);
                    if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<Footprint>();
                    list.Add(f);
                }
        }
        ready = true;
    }
    private static int Bin(float n) => Mathf.FloorToInt(n / Cell);
    private static void Ensure()
    {
        if (ready) return;
        var source = Source?.Invoke();
        if (source != null) Set(source);
    }
    public static bool Contains(Vector2 point) => BlocksSegment(point, point, 0f, null, null);

    // An endpoint's own footprint is exempt during the centre-to-centre
    // search. Those sections are clipped away before storing the road.
    public static bool BlocksSegment(Vector2 a, Vector2 b, float clearance,
        Vector2? start, Vector2? end)
    {
        Ensure();
        for (int z = Bin(Mathf.Min(a.y, b.y) - clearance); z <= Bin(Mathf.Max(a.y, b.y) + clearance); z++)
            for (int x = Bin(Mathf.Min(a.x, b.x) - clearance); x <= Bin(Mathf.Max(a.x, b.x) + clearance); x++)
            {
                if (!cells.TryGetValue(new Vector2i(x, z), out var list)) continue;
                foreach (var f in list)
                {
                    if ((start.HasValue && (start.Value - f.Centre).sqrMagnitude < f.Radius * f.Radius) ||
                        (end.HasValue && (end.Value - f.Centre).sqrMagnitude < f.Radius * f.Radius)) continue;
                    Vector2 d = b - a, to = f.Centre - a;
                    float t = d.sqrMagnitude < 0.0001f ? 0f : Mathf.Clamp01((to.x * d.x + to.y * d.y) / d.sqrMagnitude);
                    float r = f.Radius + clearance;
                    if ((a + d * t - f.Centre).sqrMagnitude < r * r) return true;
                }
            }
        return false;
    }

    public static float RadiusAt(Vector2 centre, float fallback)
    {
        Ensure();
        if (cells.TryGetValue(new Vector2i(Bin(centre.x), Bin(centre.y)), out var list))
            foreach (var f in list)
                if ((f.Centre - centre).sqrMagnitude < 0.25f) fallback = Mathf.Max(fallback, f.Radius);
        return fallback;
    }
}
