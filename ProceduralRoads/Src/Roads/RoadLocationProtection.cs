using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>All location footprints, including locations not selected for roads.
/// Shared by routing and the terrain writer; indexed so searches never scan
/// the world's full location list per move. No Unity objects are retained.</summary>
public static class RoadLocationProtection
{
    /// <summary>How far a road's centreline keeps from a protected location's edge: half its width and the terrain blend margin
    /// (<see cref="RoadConstants.TerrainBlendMargin"/>): room for the road's whole terrain blend. The one owner of location protection (docs/OWNERS.md).</summary>
    public static float Clearance(float width) => width * 0.5f + RoadConstants.TerrainBlendMargin;

    public readonly struct Footprint
    {
        public readonly Vector2 Centre;
        /// <summary>Everything the location's terrain modifiers touch, its paint included: other roads keep out of it.</summary>
        public readonly float Radius;
        /// <summary>Where the location's own road stops (<see cref="ArrivalRadiusAt"/>): the ground the location shapes
        /// (<see cref="LocationLevelling.ArrivalReach"/>), at most <see cref="Radius"/>; past it out to Radius the
        /// location only paints, and its own road is carried on over that.</summary>
        public readonly float Arrival;
        /// <summary>Whether this is the footprint of the location at <paramref name="location"/>: a location is carried by its
        /// location's centre, as its footprint holds it (its identity, resolved once from coordinates by
        /// LocationPrefabLevelling.LocationAt), exactly. Never "a footprint holding that point": an overlapping location's
        /// footprint can hold another's centre. The one owner of a location's identity (docs/OWNERS.md).</summary>
        public bool Is(Vector2 location) => Centre.x == location.x && Centre.y == location.y;
        public Footprint(Vector2 centre, float radius) : this(centre, radius, radius) { }
        public Footprint(Vector2 centre, float radius, float arrival)
        {
            Centre = centre; Radius = radius;
            Arrival = float.IsNaN(arrival) ? radius : Mathf.Clamp(arrival, 0f, radius);
        }
    }
    /// <summary>
    /// The locations a road or staircase is planned between, by their identity (<see cref="Footprint.Is"/>): the selected locations
    /// it leaves (<see cref="Start"/>) and arrives at (<see cref="End"/>), null where an end is no location. The only footprints
    /// protection lets its ends into (<see cref="BlocksSegment(Vector2, Vector2, float, Targets)"/>,
    /// <see cref="BlocksRoute"/>) or lets its trimmed arrival graze (<see cref="ArrivalKeep"/>). Another location's footprint
    /// keeps its protection wherever it lies: over the target's centre, nested in it, or round its entrance. Default:
    /// neither end is a location, and no footprint is exempt.
    /// </summary>
    public readonly struct Targets
    {
        public readonly Vector2? Start, End;
        public Targets(Vector2? start, Vector2? end) { Start = start; End = end; }
        internal bool Departs(Footprint f) => Start is Vector2 s && f.Is(s);
        internal bool Arrives(Footprint f) => End is Vector2 e && f.Is(e);
        /// <summary>Whether <paramref name="f"/> is a target whose footprint may be entered (<see cref="Enterable"/>): a
        /// target not on the preload list is a location the road stops at the edge of.</summary>
        internal bool Enters(Footprint f) => (Departs(f) || Arrives(f)) && Enterable(f.Centre);
        /// <summary>The location a road carried on into a location (a way onto its landing, a carried edge) is planned into: its
        /// end, else its start, where that is a location's footprint (<see cref="Footprint.Is"/>) that may be entered
        /// (<see cref="Enterable"/>); null for none (an end that is no location's, a shore landing say, has no footprint to be let
        /// into; a location not on the preload list is not entered).</summary>
        internal Vector2? Location => End is Vector2 e && Enterable(e) && Holds(e, e) ? e : Start is Vector2 s && Enterable(s) && Holds(s, s) ? s : null;
        /// <summary>The target whose own footprint holds <paramref name="p"/> (rim included, as <see cref="Contains(Vector2)"/>)
        /// and may be entered (<see cref="Enterable"/>): the arrival's, else the departure's; null where neither's does.
        /// Another location's footprint holding it makes it no target's.</summary>
        internal Vector2? Holding(Vector2 p) =>
            End is Vector2 e && Enterable(e) && Holds(e, p) ? e : Start is Vector2 s && Enterable(s) && Holds(s, p) ? s : null;
    }

    /// <summary>
    /// Whether the footprint of the location at <paramref name="location"/> (its identity, <see cref="Footprint.Is"/>) may be entered
    /// by the network at all, as a target: only a location on the preload list (<see cref="PlacedLocations.Preloaded"/>), whose
    /// ground is known before its roads are planned. A location not on it may still be chosen, and its road stops at its edge:
    /// its trimmed end and every shaped stretch keep out of its whole footprint (<see cref="ArrivalKeep"/>, the ring
    /// RoadNetworkGenerator.KeepOutEdge), no point is stored into it (<see cref="Targets.Holding"/>, <see cref="Targets.Location"/>),
    /// nothing is carried on into it (RoadNetworkGenerator.CarryOnToEdges) and no structure stands in it
    /// (<see cref="StructureEnters"/>). The search may still run through it centre to centre (its line is trimmed at the
    /// ring). A target that is no location's (no location's footprint is it, <see cref="Footprint.Is"/>: a shore landing,
    /// RoadCoastalLandings) may be entered as before: nothing is placed there, so its ground is the natural ground, known
    /// when its road is planned. The one owner of the preload-list rule (docs/OWNERS.md, location protection).
    /// </summary>
    internal static bool Enterable(Vector2 location) => PlacedLocations.Preloaded(location) || !IsLocation(location);

    /// <summary>Whether a location's footprint is the location at <paramref name="location"/> (<see cref="Footprint.Is"/>). With the
    /// index installed but not built, every target is taken for a location (kept out of, as protection answers then).</summary>
    private static bool IsLocation(Vector2 location)
    {
        Ensure();
        if (InstalledButUnprepared) return true;
        if (cells.TryGetValue(new Vector2i(Bin(location.x), Bin(location.y)), out var list))
            foreach (var f in list)
                if (f.Is(location)) return true;
        return false;
    }

    /// <summary>Whether the footprint of the location <paramref name="location"/> (<see cref="Footprint.Is"/>) holds
    /// <paramref name="p"/>, rim included.</summary>
    private static bool Holds(Vector2 location, Vector2 p)
    {
        Ensure();
        if (InstalledButUnprepared) return false;
        if (cells.TryGetValue(new Vector2i(Bin(p.x), Bin(p.y)), out var list))
            foreach (var f in list)
                if (f.Is(location) && Contains(p - f.Centre, f.Radius)) return true;
        return false;
    }

    /// <summary>
    /// Whether a road point planned into the location <paramref name="location"/> (its target, RoadSpatialGrid.RoadPoint.location) may
    /// change the ground at <paramref name="p"/>, a vertex or paint texel inside a footprint: <paramref name="p"/> lies in that
    /// location's own footprint (rim included, as <see cref="Contains(Vector2)"/>) and in no other location's. Where another
    /// location's footprint holds it too (over the target, nested in it, round its entrance) no road changes it: each location
    /// keeps its ground from every road but its own. The terrain writer's levelling and paint and the finished-ground
    /// predictor ask this alone (RoadTerrainModifier.EnteringAt). An unknown location (NaN) is no location's. The one owner of
    /// location protection (docs/OWNERS.md).
    /// </summary>
    internal static bool Admits(Vector2 p, Vector2 location)
    {
        Ensure();
        if (InstalledButUnprepared) return false;
        if (!cells.TryGetValue(new Vector2i(Bin(p.x), Bin(p.y)), out var list)) return false;
        bool own = false;
        foreach (var f in list)
        {
            if (!Contains(p - f.Centre, f.Radius)) continue;
            if (!f.Is(location)) return false;
            own = true;
        }
        return own;
    }
    /// <summary>
    /// The first of a structure's <paramref name="pieces"/> that stands in a location it does not go to, with that location's
    /// footprint (<paramref name="hit"/>); null where none does. A piece stands in a footprint (rim included, as
    /// <see cref="Contains(Vector2)"/>) by its body (StructurePlans.BodyBox, its measured box turned as laid; its origin
    /// where not measured) or by a vertex the ground it stands on is read at (StructureGround.VerticesUnder): it would be
    /// laid on, or hold, that location's ground. The locations it goes to are its road's targets (<paramref name="own"/>, any
    /// one's start or end, <see cref="Footprint.Is"/>); every other location keeps its whole footprint, as from a road's line. A
    /// bridge's, a ford span's, a viaduct's and a staircase's pieces all ask this (the one owner of location protection,
    /// docs/OWNERS.md); no clearance is added to the footprint.
    /// </summary>
    internal static StructurePiece? StructureEnters(IEnumerable<StructurePiece> pieces, IEnumerable<Targets> own, out Footprint hit)
    {
        hit = default;
        Ensure();
        if (InstalledButUnprepared || cells.Count == 0) return null;
        var mine = own as IReadOnlyCollection<Targets> ?? new List<Targets>(own);
        bool Other(Footprint f)
        {
            foreach (Targets t in mine) if (t.Enters(f)) return false;
            return true;
        }
        foreach (StructurePiece piece in pieces)
        {
            StructurePlans.BodyBox(piece, out Vector2 low, out Vector2 high);
            for (int z = Bin(low.y); z <= Bin(high.y); z++)
                for (int x = Bin(low.x); x <= Bin(high.x); x++)
                {
                    if (!cells.TryGetValue(new Vector2i(x, z), out var list)) continue;
                    foreach (var f in list)
                    {
                        // The point of the body's box nearest the centre: inside the footprint, the body reaches it.
                        var nearest = new Vector2(Mathf.Clamp(f.Centre.x, low.x, high.x), Mathf.Clamp(f.Centre.y, low.y, high.y));
                        if (Contains(nearest - f.Centre, f.Radius) && Other(f)) { hit = f; return piece; }
                    }
                }
            foreach ((int vx, int vz) in StructureGround.VerticesUnder(piece.Position))
            {
                var v = new Vector2(vx, vz);
                if (!cells.TryGetValue(new Vector2i(Bin(v.x), Bin(v.y)), out var list)) continue;
                foreach (var f in list)
                    if (Contains(v - f.Centre, f.Radius) && Other(f)) { hit = f; return piece; }
            }
        }
        return null;
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
                "the location index was asked to clear while roads were being generated; refused");
            return;
        }
        cells.Clear(); ready = false;
        RoadStairRuns.ForgetGround();
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
        RoadStairRuns.ForgetGround();
    }
    internal static IEnumerable<bool> PrimeSteps(RoadPreparation preparation)
    {
        if(ready || Source==null) yield break;
        if(LocationLevelling.RefuseAfterSealing("the location footprint index")) yield break;
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
        if (LocationLevelling.RefuseAfterSealing("the location footprint index")) return;
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

    /// <summary>Whether <paramref name="point"/> is in a location's footprint. The footprint is closed: its exact rim is
    /// inside. The one owner of footprint membership (docs/OWNERS.md): protection's collision check and the zero-reach
    /// lookup (<see cref="NearestFootprint"/>) both ask the private overload.</summary>
    public static bool Contains(Vector2 point) => BlocksSegment(point, point, 0f, default);

    /// <summary>Whether <paramref name="offset"/> from a footprint's centre is within <paramref name="radius"/>, rim included.</summary>
    private static bool Contains(Vector2 offset, float radius) => offset.sqrMagnitude <= radius * radius;

    /// <summary>Whether <paramref name="a"/> to <paramref name="b"/> comes within <paramref name="clearance"/> of a location's
    /// footprint, but the <paramref name="targets"/>' own (a staircase into its location, a road onto its landing, a mark's
    /// location) where it may be entered (<see cref="Enterable"/>). The one owner of location protection (docs/OWNERS.md).</summary>
    public static bool BlocksSegment(Vector2 a, Vector2 b, float clearance, Targets targets) =>
        BlocksSegment(a, b, clearance, null, null, targets, Mode.Exempt, out _);

    /// <summary>The location clearance of a road's searched line, a step <paramref name="a"/> to <paramref name="b"/> on its
    /// way between <paramref name="targets"/>: as <see cref="BlocksSegment(Vector2, Vector2, float, Targets)"/>,
    /// but a target's own footprint is crossed only as the road's departure or arrival: once the line is inside the
    /// end's footprint and clearance it stays there, and once out of the start's it never comes back. The road is trimmed
    /// where it last leaves the end's ring (TrimPathToRadii), so a searched line that came into a location's clearance and left
    /// it again kept that stretch, and the final road (<see cref="BlocksRoad"/>) was refused for it. The one owner of location protection (docs/OWNERS.md).</summary>
    internal static bool BlocksRoute(Vector2 a, Vector2 b, float clearance, Targets targets) =>
        BlocksSegment(a, b, clearance, null, null, targets, Mode.Route, out _);

    /// <summary>The location clearance of a road's line once trimmed: the final check (RoadSpatialGrid.PlanRoadPath) and every
    /// step that shapes the line before it (the pull, the sway, the network snap, the bends' fillets and the spline) ask
    /// this, with the road's width and the ends it was planned between, so a shaped line the final check would refuse is
    /// never made. <see cref="Clearance"/> of <paramref name="width"/>; a trimmed arrival may graze its own location's
    /// clearance, its own location's alone (<paramref name="targets"/>, <see cref="BlocksFinalSegment"/>). The one owner of location protection (docs/OWNERS.md).</summary>
    internal static bool BlocksRoad(Vector2 a, Vector2 b, float width, Vector2 start, Vector2 end, Targets targets) =>
        BlocksSegment(a, b, Clearance(width), start, end, targets, Mode.Final, out _);

    /// <summary><see cref="BlocksRoad"/> over every leg of <paramref name="line"/>, each leg at the wider of its two
    /// points' <paramref name="widths"/> where given; the first leg that crosses, or -1.</summary>
    internal static int FirstBlockedLeg(IReadOnlyList<Vector2> line, float width, IReadOnlyList<float>? widths, Vector2 start, Vector2 end,
        Targets targets)
    {
        for (int i = 1; i < line.Count; i++)
        {
            float w = widths != null && widths.Count == line.Count ? Mathf.Max(widths[i - 1], widths[i]) : width;
            if (BlocksRoad(line[i - 1], line[i], w, start, end, targets)) return i;
        }
        return -1;
    }

    private enum Mode { Exempt, Route, Final }

    // A trimmed arrival is outside the location, at the edge of its extra road
    // clearance. It may graze that margin while turning towards the location.
    // Unlike the centre-to-centre search exemption, it never permits crossing
    // the authored footprint itself. Use the input ends, before reshaping,
    // and only the locations they were planned to (targets).
    /// <summary>The one owner of location protection (docs/OWNERS.md).</summary>
    internal static bool BlocksFinalSegment(Vector2 a, Vector2 b, float clearance,
        Vector2 start, Vector2 end, Targets targets, out Footprint? hit) =>
        BlocksSegment(a, b, clearance, start, end, targets, Mode.Final, out hit);

    /// <summary>How far in a trimmed arrival at <paramref name="location"/> keeps its road (its segments may
    /// not come nearer the centre), or NaN where <paramref name="endpoint"/> is not one: an end between
    /// the location's arrival edge (<see cref="Footprint.Arrival"/>) and its whole footprint's clearance edge. Asked only of
    /// the location the end was planned to (<see cref="Targets"/>): another footprint near that end keeps its whole clearance.
    /// An end on the clearance edge keeps the whole footprint, as before; an end carried in over the ground
    /// the location only paints keeps only the ground it shapes.</summary>
    private static float ArrivalKeep(Vector2? endpoint, Footprint location, float clearance)
    {
        if (!endpoint.HasValue) return float.NaN;
        float distance = (endpoint.Value - location.Centre).magnitude;
        // A location not on the preload list keeps its whole footprint: its road stops at its edge (Enterable).
        float inner = Enterable(location.Centre) ? location.Arrival : location.Radius;
        // Five centimetres accommodates float rounding of the radial trim
        // at world coordinates; it does not enlarge the allowed footprint.
        if (distance < inner || distance > location.Radius + clearance + 0.05f) return float.NaN;
        return Mathf.Clamp(distance - clearance - 0.05f, inner, location.Radius);
    }

    private static bool BlocksSegment(Vector2 a, Vector2 b, float clearance,
        Vector2? start, Vector2? end, Targets targets, Mode mode, out Footprint? hit)
    {
        bool final = mode == Mode.Final;
        hit = null;
        Ensure();
        // Footprints exist and cannot be consulted. Answering "nothing is in
        // the way" would run roads through every POI on the world, so this
        // answers that everything is, which refuses the routes concerned. An
        // unreached destination is recoverable; a road through the middle of
        // a location is not.
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
                        // Only a target's own footprint (Footprint.Is), never another holding its centre or entrance.
                        bool departs = targets.Departs(f), arrives = targets.Arrives(f);
                        // Exempt (a structure's line into its location): only a location that may be entered (Enterable); one
                        // not on the preload list is kept out of as any other location. The search (Route) runs through
                        // either, centre to centre: its line is trimmed at the ring and judged in the final mode.
                        if ((departs || arrives) && (mode == Mode.Route || Enterable(f.Centre)))
                        {
                            if (mode == Mode.Exempt || (departs && arrives)) continue;
                            // A step that ends inside the end's ring (or starts inside the start's) is the arrival (the
                            // departure), its edge included as the collision below includes it; one that ends outside
                            // it is held to the whole clearance as any other.
                            Vector2 inside = arrives ? b : a;
                            if (Contains(inside - f.Centre, r)) continue;
                        }
                    }
                    Vector2 d = b - a, to = f.Centre - a;
                    float t = d.sqrMagnitude < 0.0001f ? 0f : Mathf.Clamp01((to.x * d.x + to.y * d.y) / d.sqrMagnitude);
                    if (final)
                    {
                        float keep = targets.Departs(f) ? ArrivalKeep(start, f, clearance) : float.NaN;
                        if (float.IsNaN(keep) && targets.Arrives(f)) keep = ArrivalKeep(end, f, clearance);
                        if (!float.IsNaN(keep)) r = keep;
                    }
                    if (Contains(a + d * t - f.Centre, r))
                    { hit = f; return true; }
                }
            }
        return false;
    }

    /// <summary>Whether no footprint's edge comes within <paramref name="reach"/> of <paramref name="p"/>: then no segment from
    /// <paramref name="p"/> shorter than <paramref name="reach"/> less a clearance comes within that clearance of one, and
    /// <see cref="BlocksRoute"/> refuses none (a search's steps from a cell: RoadPathfinder.RoadMoveCost). False where the
    /// index is installed but not built (<see cref="InstalledButUnprepared"/>), where <see cref="BlocksRoute"/> refuses
    /// everything.</summary>
    internal static bool NoneWithin(Vector2 p, float reach)
    {
        Ensure();
        if (InstalledButUnprepared) return false;
        for (int z = Bin(p.y - reach); z <= Bin(p.y + reach); z++)
            for (int x = Bin(p.x - reach); x <= Bin(p.x + reach); x++)
            {
                if (!cells.TryGetValue(new Vector2i(x, z), out var list)) continue;
                foreach (var f in list)
                {
                    float r = f.Radius + reach;
                    if ((p - f.Centre).sqrMagnitude <= r * r) return false;
                }
            }
        return true;
    }

    /// <summary>How far <paramref name="p"/> is from the edge of the nearest
    /// footprint within <paramref name="reach"/> of it: negative inside one,
    /// infinity when none is that close.</summary>
    public static float EdgeDistance(Vector2 p, float reach) =>
        NearestFootprint(p, reach, out Footprint nearest) ? (p - nearest.Centre).magnitude - nearest.Radius : float.PositiveInfinity;

    /// <summary>The footprint whose edge is nearest <paramref name="p"/>
    /// within <paramref name="reach"/>; false when none is that close. At zero reach this is membership, asked of
    /// <see cref="Contains(Vector2)"/>'s rule: the deepest footprint holding <paramref name="p"/>, rim included.</summary>
    public static bool NearestFootprint(Vector2 p, float reach, out Footprint nearest)
    {
        nearest = default;
        Ensure();
        if (InstalledButUnprepared) return false;
        bool member = reach == 0f, found = false;
        float best = float.PositiveInfinity;
        for (int z = Bin(p.y - reach); z <= Bin(p.y + reach); z++)
            for (int x = Bin(p.x - reach); x <= Bin(p.x + reach); x++)
            {
                if (!cells.TryGetValue(new Vector2i(x, z), out var list)) continue;
                foreach (var f in list)
                {
                    if (member && !Contains(p - f.Centre, f.Radius)) continue;
                    float d = (p - f.Centre).magnitude - f.Radius;
                    if (d < best) { best = d; nearest = f; found = true; }
                }
            }
        return member ? found : best <= reach;
    }

    /// <summary>Every footprint whose edge lies within <paramref name="reach"/> of <paramref name="p"/> (those holding it
    /// included), each once.</summary>
    internal static List<Footprint> FootprintsNear(Vector2 p, float reach)
    {
        var near = new List<Footprint>();
        Ensure();
        if (InstalledButUnprepared) return near;
        for (int z = Bin(p.y - reach); z <= Bin(p.y + reach); z++)
            for (int x = Bin(p.x - reach); x <= Bin(p.x + reach); x++)
            {
                if (!cells.TryGetValue(new Vector2i(x, z), out var list)) continue;
                foreach (var f in list)
                    if ((p - f.Centre).magnitude - f.Radius <= reach && !near.Exists(n => n.Is(f.Centre))) near.Add(f);
            }
        return near;
    }

    /// <summary>Where a road to the location at <paramref name="centre"/> (its identity, <see cref="Footprint.Is"/>) stops: the ground the location shapes
    /// (<see cref="Footprint.Arrival"/>), or <paramref name="fallback"/> (its exterior radius) where larger.
    /// <see cref="RadiusAt"/> is the whole footprint, paint included.</summary>
    public static float ArrivalRadiusAt(Vector2 centre, float fallback)
    {
        Ensure();
        if (InstalledButUnprepared) return fallback;
        if (cells.TryGetValue(new Vector2i(Bin(centre.x), Bin(centre.y)), out var list))
            foreach (var f in list)
                if (f.Is(centre)) fallback = Mathf.Max(fallback, f.Arrival);
        return fallback;
    }

    /// <summary>The whole footprint's radius of the location at <paramref name="centre"/> (<see cref="Footprint.Is"/>), paint included, or
    /// <paramref name="fallback"/> where larger.</summary>
    public static float RadiusAt(Vector2 centre, float fallback)
    {
        Ensure();
        if (InstalledButUnprepared) return fallback;
        if (cells.TryGetValue(new Vector2i(Bin(centre.x), Bin(centre.y)), out var list))
            foreach (var f in list)
                if (f.Is(centre)) fallback = Mathf.Max(fallback, f.Radius);
        return fallback;
    }
}
