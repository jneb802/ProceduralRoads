using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>All location footprints, including locations not selected for roads.
/// Shared by routing and the terrain writer; indexed so searches never scan
/// the world's full location list per move. No Unity objects are retained.</summary>
public static class RoadSiteProtection
{
    /// <summary>How far a road's centreline keeps from a protected site's edge: half its width and a margin.</summary>
    public static float Clearance(float width) => width * 0.5f + 2f;

    public readonly struct Footprint
    {
        public readonly Vector2 Centre;
        /// <summary>Everything the place's terrain modifiers touch, its paint included: other roads keep out of it.</summary>
        public readonly float Radius;
        /// <summary>Where the place's own road stops (<see cref="ArrivalRadiusAt"/>): the ground the place shapes
        /// (<see cref="LocationLevelling.ArrivalReach"/>), at most <see cref="Radius"/>; past it out to Radius the
        /// place only paints, and its own road is carried on over that.</summary>
        public readonly float Arrival;
        public Footprint(Vector2 centre, float radius) : this(centre, radius, radius) { }
        public Footprint(Vector2 centre, float radius, float arrival)
        {
            Centre = centre; Radius = radius;
            Arrival = float.IsNaN(arrival) ? radius : Mathf.Clamp(arrival, 0f, radius);
        }
    }
    public static Func<IEnumerable<Footprint>?>? Source;
    private const float Cell = 128f;
    private static Dictionary<Vector2i, List<Footprint>> cells = new(RoadGridComparer.Instance);
    private static volatile bool ready;
    // Ensure() builds `cells` on first use. Pricing now runs on several threads
    // and every move cost asks this, so first use can arrive on many threads at
    // once; without the gate they all rebuild the same dictionary together.
    private static readonly object gate = new object();

    public static void Reset()
    {
        // Clearing the index while island workers are reading it is the same
        // corruption as filling it while they read, so it waits for them.
        if (LocationLevelling.Sealed)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogError(
                "the site index was asked to clear while roads were being generated; refused");
            return;
        }
        cells.Clear(); ready = false;
    }
    public static void Set(IEnumerable<Footprint> footprints)
    {
        using var preparation=new RoadPreparation(p=>SetSteps(footprints,p));
        preparation.Drain();
    }
    internal static IEnumerable<bool> SetSteps(IEnumerable<Footprint> footprints,RoadPreparation preparation)
    {
        var built=new Dictionary<Vector2i,List<Footprint>>(RoadGridComparer.Instance);
        foreach(var f in footprints)
        {
            if(f.Radius>0f && !float.IsInfinity(f.Radius) &&
                !float.IsNaN(f.Centre.x) && !float.IsNaN(f.Centre.y) &&
                !float.IsInfinity(f.Centre.x) && !float.IsInfinity(f.Centre.y))
                for(int z=Bin(f.Centre.y-f.Radius);z<=Bin(f.Centre.y+f.Radius);z++)
                    for(int x=Bin(f.Centre.x-f.Radius);x<=Bin(f.Centre.x+f.Radius);x++)
                    {
                        var key=new Vector2i(x,z);
                        if(!built.TryGetValue(key,out var list)) built[key]=list=new List<Footprint>();
                        list.Add(f);
                        if(preparation.Checkpoint(0)) yield return true;
                    }
            if(preparation.Checkpoint()) yield return true;
        }
        preparation.Cancellation.ThrowIfCancellationRequested();
        cells=built;
        ready=true;
    }
    internal static IEnumerable<bool> PrimeSteps(RoadPreparation preparation)
    {
        if(ready || Source==null) yield break;
        if(LocationLevelling.RefuseAfterSealing("the site footprint index")) yield break;
        var source=Source.Invoke();
        if(source!=null)
            foreach(var step in SetSteps(source,preparation)) yield return step;
    }
    private static int Bin(float n) => Mathf.FloorToInt(n / Cell);
    private static void Ensure()
    {
        if (ready) return;
        // No source at all is an ordinary state, not a gap in preparation:
        // there is nothing to build, nothing to corrupt and nothing to report.
        // It has always meant "no footprints", and it still does.
        if (Source == null) return;
        // Source reads the world's location list and its prefabs. Prime()
        // calls this on the main thread before any island starts; arriving
        // here afterwards means preparation missed something, and building it
        // now would mean several workers writing one dictionary at once.
        if (LocationLevelling.RefuseAfterSealing("the site footprint index")) return;
        lock (gate)
        {
            if (ready) return;
            var source = Source?.Invoke();
            if (source != null) Set(source);
        }
    }

    /// <summary>Build the index now, on this thread, so that a parallel pass
    /// finds it ready and never contends for the gate. Safe to call when there
    /// is no source: it simply does nothing.</summary>
    public static void Prime() => Ensure();
    /// <summary>A source is installed but its index was never built. Having
    /// no source at all is an ordinary "there are no footprints"; this is a
    /// gap in preparation, and the answers it would give are wrong.</summary>
    public static bool InstalledButUnprepared => Source != null && !ready;

    public static bool Contains(Vector2 point) => BlocksSegment(point, point, 0f, null, null);

    // An endpoint's own footprint is exempt during the centre-to-centre
    // search. Those sections are clipped away before storing the road.
    public static bool BlocksSegment(Vector2 a, Vector2 b, float clearance,
        Vector2? start, Vector2? end) =>
        BlocksSegment(a, b, clearance, start, end, Mode.Exempt, out _);

    /// <summary>The site clearance of a road's searched line, a step <paramref name="a"/> to <paramref name="b"/> on its
    /// way from <paramref name="start"/> to <paramref name="end"/> (place centres): as <see cref="BlocksSegment(Vector2, Vector2, float, Vector2?, Vector2?)"/>,
    /// but an endpoint's own footprint is crossed only as the road's departure or arrival: once the line is inside the
    /// end's footprint and clearance it stays there, and once out of the start's it never comes back. The road is trimmed
    /// where it last leaves the end's ring (TrimPathToRadii), so a searched line that came into a place's clearance and left
    /// it again kept that stretch, and the final road (<see cref="BlocksRoad"/>) was refused for it.</summary>
    internal static bool BlocksRoute(Vector2 a, Vector2 b, float clearance, Vector2? start, Vector2? end) =>
        BlocksSegment(a, b, clearance, start, end, Mode.Route, out _);

    /// <summary>The site clearance of a road's line once trimmed: the final check (RoadSpatialGrid.PlanRoadPath) and every
    /// step that shapes the line before it (the pull, the sway, the network snap, the bends' fillets and the spline) ask
    /// this, with the road's width and the ends it was planned between, so a shaped line the final check would refuse is
    /// never made. <see cref="Clearance"/> of <paramref name="width"/>; a trimmed arrival may graze its own place's
    /// clearance (<see cref="BlocksFinalSegment"/>).</summary>
    internal static bool BlocksRoad(Vector2 a, Vector2 b, float width, Vector2 start, Vector2 end) =>
        BlocksSegment(a, b, Clearance(width), start, end, Mode.Final, out _);

    /// <summary><see cref="BlocksRoad"/> over every leg of <paramref name="line"/>, each leg at the wider of its two
    /// points' <paramref name="widths"/> where given; the first leg that crosses, or -1.</summary>
    internal static int FirstBlockedLeg(IReadOnlyList<Vector2> line, float width, IReadOnlyList<float>? widths, Vector2 start, Vector2 end)
    {
        for (int i = 1; i < line.Count; i++)
        {
            float w = widths != null && widths.Count == line.Count ? Mathf.Max(widths[i - 1], widths[i]) : width;
            if (BlocksRoad(line[i - 1], line[i], w, start, end)) return i;
        }
        return -1;
    }

    private enum Mode { Exempt, Route, Final }

    // A trimmed arrival is outside the site, at the edge of its extra road
    // clearance. It may graze that margin while turning towards the site.
    // Unlike the centre-to-centre search exemption, it never permits crossing
    // the authored footprint itself. Use the input ends, before reshaping.
    internal static bool BlocksFinalSegment(Vector2 a, Vector2 b, float clearance,
        Vector2 start, Vector2 end, out Footprint? hit) =>
        BlocksSegment(a, b, clearance, start, end, Mode.Final, out hit);

    /// <summary>How far in a trimmed arrival at <paramref name="site"/> keeps its road (its segments may
    /// not come nearer the centre), or NaN where <paramref name="endpoint"/> is not one: an end between
    /// the place's arrival edge (<see cref="Footprint.Arrival"/>) and its whole footprint's clearance edge.
    /// An end on the clearance edge keeps the whole footprint, as before; an end carried in over the ground
    /// the place only paints keeps only the ground it shapes.</summary>
    private static float ArrivalKeep(Vector2? endpoint, Footprint site, float clearance)
    {
        if (!endpoint.HasValue) return float.NaN;
        float distance = (endpoint.Value - site.Centre).magnitude;
        // Five centimetres accommodates float rounding of the radial trim
        // at world coordinates; it does not enlarge the allowed footprint.
        if (distance < site.Arrival || distance > site.Radius + clearance + 0.05f) return float.NaN;
        return Mathf.Clamp(distance - clearance - 0.05f, site.Arrival, site.Radius);
    }

    private static bool BlocksSegment(Vector2 a, Vector2 b, float clearance,
        Vector2? start, Vector2? end, Mode mode, out Footprint? hit)
    {
        bool final = mode == Mode.Final;
        hit = null;
        Ensure();
        // Footprints exist and cannot be consulted. Answering "nothing is in
        // the way" would run roads through every POI on the world, so this
        // answers that everything is, which refuses the routes concerned. An
        // unreached destination is recoverable; a road through the middle of
        // a place is not.
        if (InstalledButUnprepared) return true;
        for (int z = Bin(Mathf.Min(a.y, b.y) - clearance); z <= Bin(Mathf.Max(a.y, b.y) + clearance); z++)
            for (int x = Bin(Mathf.Min(a.x, b.x) - clearance); x <= Bin(Mathf.Max(a.x, b.x) + clearance); x++)
            {
                if (!cells.TryGetValue(new Vector2i(x, z), out var list)) continue;
                foreach (var f in list)
                {
                    float r = f.Radius + clearance;
                    if (!final)
                    {
                        bool departs = start.HasValue && (start.Value - f.Centre).sqrMagnitude < f.Radius * f.Radius;
                        bool arrives = end.HasValue && (end.Value - f.Centre).sqrMagnitude < f.Radius * f.Radius;
                        if (departs || arrives)
                        {
                            if (mode == Mode.Exempt || (departs && arrives)) continue;
                            // A step that ends inside the end's ring (or starts inside the start's) is the arrival (the
                            // departure); one that ends outside it is held to the whole clearance as any other.
                            Vector2 inside = arrives ? b : a;
                            if ((inside - f.Centre).sqrMagnitude < r * r) continue;
                        }
                    }
                    Vector2 d = b - a, to = f.Centre - a;
                    float t = d.sqrMagnitude < 0.0001f ? 0f : Mathf.Clamp01((to.x * d.x + to.y * d.y) / d.sqrMagnitude);
                    if (final)
                    {
                        float keep = ArrivalKeep(start, f, clearance);
                        if (float.IsNaN(keep)) keep = ArrivalKeep(end, f, clearance);
                        if (!float.IsNaN(keep)) r = keep;
                    }
                    if ((a + d * t - f.Centre).sqrMagnitude < r * r)
                    { hit = f; return true; }
                }
            }
        return false;
    }

    /// <summary>How far <paramref name="p"/> is from the edge of the nearest
    /// footprint within <paramref name="reach"/> of it: negative inside one,
    /// infinity when none is that close.</summary>
    public static float EdgeDistance(Vector2 p, float reach) =>
        NearestFootprint(p, reach, out Footprint nearest) ? (p - nearest.Centre).magnitude - nearest.Radius : float.PositiveInfinity;

    /// <summary>The footprint whose edge is nearest <paramref name="p"/>
    /// within <paramref name="reach"/>; false when none is that close.</summary>
    public static bool NearestFootprint(Vector2 p, float reach, out Footprint nearest)
    {
        nearest = default;
        Ensure();
        if (InstalledButUnprepared) return false;
        float best = float.PositiveInfinity;
        for (int z = Bin(p.y - reach); z <= Bin(p.y + reach); z++)
            for (int x = Bin(p.x - reach); x <= Bin(p.x + reach); x++)
            {
                if (!cells.TryGetValue(new Vector2i(x, z), out var list)) continue;
                foreach (var f in list)
                {
                    float d = (p - f.Centre).magnitude - f.Radius;
                    if (d < best) { best = d; nearest = f; }
                }
            }
        return best <= reach;
    }

    /// <summary>Where a road to the place at <paramref name="centre"/> stops: the ground the place shapes
    /// (<see cref="Footprint.Arrival"/>), or <paramref name="fallback"/> (its exterior radius) where larger.
    /// <see cref="RadiusAt"/> is the whole footprint, paint included.</summary>
    public static float ArrivalRadiusAt(Vector2 centre, float fallback)
    {
        Ensure();
        if (InstalledButUnprepared) return fallback;
        if (cells.TryGetValue(new Vector2i(Bin(centre.x), Bin(centre.y)), out var list))
            foreach (var f in list)
                if ((f.Centre - centre).sqrMagnitude < 0.25f) fallback = Mathf.Max(fallback, f.Arrival);
        return fallback;
    }

    /// <summary>The whole footprint's radius of the place at <paramref name="centre"/>, paint included, or
    /// <paramref name="fallback"/> where larger.</summary>
    public static float RadiusAt(Vector2 centre, float fallback)
    {
        Ensure();
        if (InstalledButUnprepared) return fallback;
        if (cells.TryGetValue(new Vector2i(Bin(centre.x), Bin(centre.y)), out var list))
            foreach (var f in list)
                if ((f.Centre - centre).sqrMagnitude < 0.25f) fallback = Mathf.Max(fallback, f.Radius);
        return fallback;
    }
}
