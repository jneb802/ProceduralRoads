using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// One owner of "this route rides an existing road" (a second road laid 1.2 m beside the
/// first under a ford's footbridge is one road, and bridges/fords/stairs
/// along the way must not trip it up).
///
/// The network's line here is what a road stored (its road points, a wade's painted points included, a staircase's cuts
/// not) and what a structure carries in a road's place: a crossing's line from bank to bank (a bridge's or a spanned
/// ford's deck, a wade, a raise) and a staircase's flight: a structure's gap in a road is still road.
///
/// A route rides the network where its centreline lies within a road's width of the network's line, heading the same
/// way (<see cref="Angle"/>) and at its height (<see cref="Height"/>; along a structure's line at any height: a road under
/// a deck or on a flight is in its place), over <see cref="MinRun"/> or more: there the two are one road, the one laid
/// first. A riding route stores nothing there (<see cref="Own"/>): not its points, not its crossings
/// (<see cref="RidesCrossing"/>), not its staircases; the commit path refuses to store a ridden stretch
/// (RoadSpatialGrid.CommitRoad). Asked by the snap onto the network, the meeting refusals (RoadBeside, CrossesNetwork),
/// and the commit.
/// </summary>
public static class RoadRide
{
    /// <summary>Centrelines within this of each other are one road: the road's width (two roads nearer than it overlap).</summary>
    public const float Reach = RoadConstants.DefaultRoadWidth;
    /// <summary>Headings within this many degrees (either way along) are the same corridor.</summary>
    public const float Angle = 25f;
    /// <summary>Within this of the other road's surface the two are at one height (RoadBeside's height).</summary>
    public const float Height = RoadBeside.Height;
    /// <summary>Along a structure, within this of the road it carries at its ends (a footbridge's deck stands about a metre
    /// over its banks' road, a ford's water under them).</summary>
    public const float StructureHeight = 3f;
    /// <summary>A ride this long or longer is the other road's; shorter, the route only touches it (a junction's mouth).</summary>
    public const float MinRun = 4f;

    /// <summary>The network's line nearest a point: where, its surface height there, its heading (unit; zero where it cannot
    /// be told), and whether a structure carries it there.</summary>
    public readonly struct Hit
    {
        public readonly Vector2 At;
        public readonly float Height;
        public readonly Vector2 Dir;
        public readonly bool Structure;
        public readonly float Distance;
        public Hit(Vector2 at, float height, Vector2 dir, bool structure, float distance)
        { At = at; Height = height; Dir = dir; Structure = structure; Distance = distance; }
        public bool HasHeading => Dir.sqrMagnitude > 0.5f;
    }

    /// <summary>A structure's line in a road's place, from end to end, at the road's height at each end (a deck from bank to
    /// bank, a staircase's step to the next).</summary>
    public readonly struct Segment
    {
        public readonly Vector2 A, B;
        public readonly float Ha, Hb;
        public Segment(Vector2 a, Vector2 b, float ha, float hb) { A = a; B = b; Ha = ha; Hb = hb; }
    }

    private const float Cell = 4f;
    /// <summary>Road abreast of a point lies within this along its heading: a stored road's points are about a metre apart.</summary>
    private const float Abreast = 1f;

    /// <summary>The network's lines near a set of points, read once (the road points of the 64 m grid cells they reach, the
    /// crossings and staircases near them), then asked point by point without a lock.</summary>
    public sealed class Lines
    {
        private readonly List<RoadSpatialGrid.RoadPoint> m_points = new();
        private readonly Dictionary<(int, int), List<int>> m_cells = new();
        private readonly List<Segment> m_segments = new();
        private Vector2[]? m_dirs;

        internal Lines(IReadOnlyList<Vector2> around, float reach)
        {
            if (!RoadSpatialGrid.IsInitialized || around.Count == 0) return;
            // Road points: every 64 m grid cell within reach (and the heading's 4 m) of a point.
            float margin = reach + 4f;
            var gridCells = new HashSet<Vector2i>();
            foreach (Vector2 p in around)
            {
                Vector2i lo = RoadSpatialGrid.GetRoadGrid(p.x - margin, p.y - margin), hi = RoadSpatialGrid.GetRoadGrid(p.x + margin, p.y + margin);
                for (int x = lo.x; x <= hi.x; x++)
                    for (int y = lo.y; y <= hi.y; y++)
                        gridCells.Add(new Vector2i(x, y));
            }
            // The cells' world box (a metre wider for rounding at a cell's border): a crossing or a staircase piece outside
            // it is in none of them, and is passed over without reading the cells.
            int loX = int.MaxValue, loY = int.MaxValue, hiX = int.MinValue, hiY = int.MinValue;
            foreach (Vector2i c in gridCells) { loX = Mathf.Min(loX, c.x); loY = Mathf.Min(loY, c.y); hiX = Mathf.Max(hiX, c.x); hiY = Mathf.Max(hiY, c.y); }
            float half = RoadSpatialGrid.GridSize / 2f + 1f;
            float minX = loX * RoadSpatialGrid.GridSize - half, maxX = hiX * RoadSpatialGrid.GridSize + half;
            float minY = loY * RoadSpatialGrid.GridSize - half, maxY = hiY * RoadSpatialGrid.GridSize + half;
            bool InBox(float x, float y) => x >= minX && x <= maxX && y >= minY && y <= maxY;
            foreach (Vector2i c in gridCells)
                if (RoadSpatialGrid.CellPoints(c) is RoadSpatialGrid.RoadPoint[] pts)
                    foreach (var q in pts)
                    {
                        if (q.cutOnly) continue;
                        var key = (Mathf.FloorToInt(q.p.x / Cell), Mathf.FloorToInt(q.p.y / Cell));
                        if (!m_cells.TryGetValue(key, out var list)) m_cells[key] = list = new List<int>();
                        list.Add(m_points.Count);
                        m_points.Add(q);
                    }
            // A crossing's line, bank to bank, near the points (a grid cell along it read above), at the height of the road
            // stored at its banks (the water's where none is).
            bool Near(RoadCrossing c)
            {
                if (Mathf.Max(c.FromBank.x, c.ToBank.x) < minX || Mathf.Min(c.FromBank.x, c.ToBank.x) > maxX
                    || Mathf.Max(c.FromBank.y, c.ToBank.y) < minY || Mathf.Min(c.FromBank.y, c.ToBank.y) > maxY) return false;
                float len = Vector2.Distance(c.FromBank, c.ToBank);
                int n = Mathf.Max(1, Mathf.CeilToInt(len / 8f));
                for (int k = 0; k <= n; k++)
                {
                    Vector2 q = Vector2.Lerp(c.FromBank, c.ToBank, (float)k / n);
                    if (gridCells.Contains(RoadSpatialGrid.GetRoadGrid(q.x, q.y))) return true;
                }
                return false;
            }
            foreach (RoadCrossing c in RoadNetworkGenerator.CrossingsSnapshot())
                if (Near(c))
                    m_segments.Add(new Segment(c.FromBank, c.ToBank, StoredHeight(c.FromBank, c.WaterLevel), StoredHeight(c.ToBank, c.WaterLevel)));
            // A staircase's flight: its walked pieces in order.
            // Only flights in the grid cells read above (no lock held per piece, no scan per point).
            foreach (StairRun run in StairStructuresRuns())
            {
                StructurePiece? last = null;
                foreach (StructurePiece p in run.Pieces)
                {
                    if (p.Kind != StructurePieceKind.StairStep && p.Kind != StructurePieceKind.StairFloor) continue;
                    if (last is StructurePiece l && (InBox(l.Position.x, l.Position.z) || InBox(p.Position.x, p.Position.z))
                        && (gridCells.Contains(RoadSpatialGrid.GetRoadGrid(l.Position.x, l.Position.z))
                            || gridCells.Contains(RoadSpatialGrid.GetRoadGrid(p.Position.x, p.Position.z))))
                        m_segments.Add(new Segment(new Vector2(l.Position.x, l.Position.z), new Vector2(p.Position.x, p.Position.z), RoadStairRuns.Surface(l), RoadStairRuns.Surface(p)));
                    last = p;
                }
            }
        }

        /// <summary>The height of the road read nearest <paramref name="p"/> within 4 m, or <paramref name="otherwise"/>.</summary>
        internal float StoredHeight(Vector2 p, float otherwise)
        {
            float best = 16f, height = otherwise;
            int cx = Mathf.FloorToInt(p.x / Cell), cz = Mathf.FloorToInt(p.y / Cell);
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    if (m_cells.TryGetValue((cx + dx, cz + dz), out var list))
                        foreach (int i in list)
                        {
                            var q = m_points[i];
                            float d = (q.p - p).sqrMagnitude;
                            if (d < best && q.IsRoad) { best = d; height = q.h; }
                        }
            return height;
        }

        /// <summary>A road's own piece, kept: its later pieces ride it as they ride the network.</summary>
        internal void AddRoad(IReadOnlyList<Vector2> points, IReadOnlyList<float> heights)
        {
            for (int i = 0; i < points.Count; i++)
            {
                var q = new RoadSpatialGrid.RoadPoint(points[i], RoadConstants.DefaultRoadWidth, heights[i]);
                var key = (Mathf.FloorToInt(q.p.x / Cell), Mathf.FloorToInt(q.p.y / Cell));
                if (!m_cells.TryGetValue(key, out var list)) m_cells[key] = list = new List<int>();
                list.Add(m_points.Count);
                m_points.Add(q);
            }
            m_dirs = null;
        }

        /// <summary>A structure's line in a road's place (a deck from bank to bank).</summary>
        internal void AddSegment(Vector2 a, Vector2 b, float ha, float hb) => m_segments.Add(new Segment(a, b, ha, hb));
        internal void AddSegment(Segment s) => m_segments.Add(s);

        private static IReadOnlyList<StairRun> StairStructuresRuns() => StairStructures.Runs;

        /// <summary>The stored road's heading at its point <paramref name="i"/>: from its points 1.5-4 m away.</summary>
        private Vector2 DirAt(int i)
        {
            m_dirs ??= new Vector2[m_points.Count];
            if (m_dirs[i].sqrMagnitude > 0f) return m_dirs[i];
            Vector2 q = m_points[i].p, dir = new(0f, 0f);
            int cx = Mathf.FloorToInt(q.x / Cell), cz = Mathf.FloorToInt(q.y / Cell);
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    if (m_cells.TryGetValue((cx + dx, cz + dz), out var list))
                        foreach (int j in list)
                        {
                            float d = Vector2.Distance(m_points[j].p, q);
                            if (d < 1.5f || d > 4f) continue;
                            Vector2 v = m_points[j].p - q;
                            if (Vector2.Dot(v, dir) < 0f) v = -v;
                            dir += v;
                        }
            // Cached as found; a point with no neighbours keeps a tiny non-zero mark so it is not worked out again.
            m_dirs[i] = dir.sqrMagnitude < 1e-4f ? new Vector2(1e-4f, 0f) : dir.normalized;
            return m_dirs[i];
        }

        /// <summary>The nearest point of the network's line within <paramref name="reach"/> of <paramref name="p"/>; with a
        /// <paramref name="heading"/>, only line heading that way (<see cref="Angle"/>); with a <paramref name="height"/>,
        /// only road within <paramref name="heightTolerance"/> of it (a structure's line at any height).</summary>
        /// <param name="abreast">Only road abreast of the point along <paramref name="heading"/> (within a point's spacing
        /// along it): beside it, not ahead of it or behind it (a road carrying on from where another ends rides nothing).</param>
        public bool Nearest(Vector2 p, float reach, out Hit hit, Vector2? heading = null, float? height = null, float heightTolerance = Height,
            bool paintOnly = true, bool abreast = false, bool roads = true, bool structures = true)
        {
            hit = default;
            float best = reach * reach;
            bool found = false;
            float cosLimit = Mathf.Cos(Angle * Mathf.Deg2Rad);
            Vector2 head = heading is Vector2 hv && hv.sqrMagnitude > 1e-6f ? hv.normalized : new Vector2(0f, 0f);
            bool wantHeading = head.sqrMagnitude > 0.5f;
            int bestPoint = -1;
            int r = Mathf.CeilToInt(reach / Cell);
            int cx = Mathf.FloorToInt(p.x / Cell), cz = Mathf.FloorToInt(p.y / Cell);
            for (int dx = -r; dx <= r && roads; dx++)
                for (int dz = -r; dz <= r; dz++)
                    if (m_cells.TryGetValue((cx + dx, cz + dz), out var list))
                        foreach (int i in list)
                        {
                            var q = m_points[i];
                            if (!paintOnly && q.paintOnly) continue;
                            float d = (q.p - p).sqrMagnitude;
                            if (d > best) continue;
                            if (height is float h && Mathf.Abs(q.h - h) > heightTolerance) continue;
                            if (abreast && wantHeading && Mathf.Abs(Vector2.Dot(q.p - p, head)) > Abreast) continue;
                            Vector2 dir = new(0f, 0f);
                            if (wantHeading)
                            {
                                dir = DirAt(i);
                                if (dir.sqrMagnitude < 0.5f || Mathf.Abs(Vector2.Dot(dir, head)) < cosLimit) continue;
                            }
                            best = d; found = true; bestPoint = i;
                            hit = new Hit(q.p, q.h, dir, false, Mathf.Sqrt(d));
                        }
            foreach (Segment s in m_segments)
            {
                if (!structures) break;
                Vector2 ab = s.B - s.A;
                float len2 = ab.sqrMagnitude;
                if (len2 < 1e-6f) continue;
                // Over its gap only: past its ends the road it carries is stored as road.
                float t = Vector2.Dot(p - s.A, ab) / len2;
                if (t <= 0f || t >= 1f) continue;
                Vector2 at = s.A + ab * t;
                float d = (at - p).sqrMagnitude;
                if (d > best) continue;
                Vector2 dir = ab * (1f / Mathf.Sqrt(len2));
                if (wantHeading && (dir.sqrMagnitude < 0.5f || Mathf.Abs(Vector2.Dot(dir, head)) < cosLimit)) continue;
                if (height is float hs && Mathf.Abs(Mathf.Lerp(s.Ha, s.Hb, t) - hs) > heightTolerance) continue;
                best = d; found = true; bestPoint = -1;
                hit = new Hit(at, Mathf.Lerp(s.Ha, s.Hb, t), dir, true, Mathf.Sqrt(d));
            }
            // The road's heading where it was found, asked for or not (CrossesNetwork judges a crossing by it).
            if (bestPoint >= 0 && !hit.HasHeading)
            {
                Vector2 dir = DirAt(bestPoint);
                hit = new Hit(hit.At, hit.Height, dir.sqrMagnitude > 0.5f ? dir : new Vector2(0f, 0f), false, hit.Distance);
            }
            return found;
        }
    }

    /// <summary>The network's lines near <paramref name="points"/>, within <paramref name="reach"/> of any.</summary>
    public static Lines Around(IReadOnlyList<Vector2> points, float reach) => new(points, reach);

    /// <summary>A route's own heading at its point <paramref name="i"/>, from its points either side.</summary>
    private static Vector2 Heading(IReadOnlyList<Vector2> points, int i)
    {
        Vector2 d = points[Mathf.Min(i + 2, points.Count - 1)] - points[Mathf.Max(i - 2, 0)];
        return d.sqrMagnitude < 1e-6f ? new Vector2(0f, 0f) : d.normalized;
    }

    /// <summary>Which points of a route ride the network (<paramref name="lines"/>): within <paramref name="reach"/> of its
    /// line, heading its way, at its height (along a structure, at any height), in runs of <see cref="MinRun"/> or more.</summary>
    public static bool[] Rides(Lines lines, IReadOnlyList<Vector2> points, IReadOnlyList<float> heights, float reach = Reach)
    {
        var on = new bool[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            Vector2 head = Heading(points, i);
            if (head.sqrMagnitude < 0.5f) continue;
            // On road at its height, or on a structure's line about the height of the road it carries (a road under a
            // deck, through the water below a footbridge, or beside a flight); never one far under a viaduct.
            on[i] = lines.Nearest(points[i], reach, out _, head, heights[i], abreast: true)
                || lines.Nearest(points[i], reach, out _, head, heights[i], StructureHeight, abreast: true, roads: false);
        }
        // Runs shorter than MinRun only touch the network.
        int a = 0;
        while (a < on.Length)
        {
            if (!on[a]) { a++; continue; }
            int b = a;
            while (b + 1 < on.Length && on[b + 1]) b++;
            float run = 0f;
            for (int k = a; k < b; k++) run += Vector2.Distance(points[k], points[k + 1]);
            if (run < MinRun) for (int k = a; k <= b; k++) on[k] = false;
            a = b + 1;
        }
        return on;
    }

    /// <summary>The lines of a road's own <paramref name="decks"/>, bank to bank, at the height of its planned pieces at each
    /// bank (the water's where none comes near). Read once from the road as planned, before <see cref="Own"/> leaves anything
    /// out, so <see cref="Own"/> and the commit's guard (<see cref="Doubled"/>) judge against the same lines.</summary>
    public static List<Segment> DeckLines(IReadOnlyList<RoadSpatialGrid.PlannedPath> plans, IReadOnlyList<RoadCrossing> decks)
    {
        float At(Vector2 bank, float otherwise)
        {
            float best = 16f, h = otherwise;
            foreach (var plan in plans)
                for (int i = 0; i < plan.Points.Count; i++)
                {
                    float d = (plan.Points[i] - bank).sqrMagnitude;
                    if (d < best) { best = d; h = plan.Heights[i]; }
                }
            return h;
        }
        return decks.Select(c => new Segment(c.FromBank, c.ToBank, At(c.FromBank, c.WaterLevel), At(c.ToBank, c.WaterLevel))).ToList();
    }

    /// <summary>Of a road's planned pieces, what is its own: each piece with the stretches riding the network left out (the
    /// road ends on the other road where it comes onto it, and starts again where it leaves); pieces wholly ridden are gone.
    /// Ridden: the network as stored before this road, the road's own <paramref name="decks"/> (<see cref="DeckLines"/>: a
    /// structure replaces the road over its gap, no piece of the road runs on under it), and the road's earlier pieces (a
    /// piece laid again along one before it, a ford's over a neighbour's in a pool chain). Every piece it keeps passes
    /// <see cref="Doubled"/>: the two read a ride and a junction's mouth by one rule.</summary>
    public static List<RoadSpatialGrid.PlannedPath> Own(List<RoadSpatialGrid.PlannedPath> plans, out float riddenMetres,
        IReadOnlyList<Segment>? decks = null) => Ride(plans, decks, trim: true, out riddenMetres, out _);

    /// <summary>Where <paramref name="pieces"/> ride the network or themselves (as <see cref="Own"/> judges): the first ridden
    /// point inside a piece, or null. What the commit path refuses to store.</summary>
    public static Vector2? Doubled(List<RoadSpatialGrid.PlannedPath> pieces, IReadOnlyList<Segment>? decks = null)
    {
        Ride(pieces, decks, trim: false, out _, out Vector2? at);
        return at;
    }

    /// <summary>A ridden point inside a piece: one with a ridden point either side (a piece's own two ends meet the network
    /// at a junction's mouth, <see cref="Mouths"/>; only a ride inside it counts). -1 where there is none.</summary>
    private static int RiddenInside(bool[] on)
    {
        for (int i = 1; i + 1 < on.Length; i++)
            if (on[i] && on[i - 1] && on[i + 1]) return i;
        return -1;
    }

    private static List<RoadSpatialGrid.PlannedPath> Ride(List<RoadSpatialGrid.PlannedPath> plans, IReadOnlyList<Segment>? decks,
        bool trim, out float riddenMetres, out Vector2? doubledAt)
    {
        riddenMetres = 0f;
        doubledAt = null;
        if (!RoadSpatialGrid.IsInitialized || plans.Count == 0) return plans;
        var own = new List<RoadSpatialGrid.PlannedPath>(plans.Count);
        var all = new List<Vector2>();
        foreach (var plan in plans) all.AddRange(plan.Points);
        var lines = Around(all, Reach);
        if (decks != null)
            foreach (Segment deck in decks) lines.AddSegment(deck);
        // Pieces in order; a piece split by a ride is judged again slice by slice against what is kept before it (a slice
        // that runs back along an earlier one of its own piece is that one's), as the commit's guard judges them.
        var queue = new LinkedList<RoadSpatialGrid.PlannedPath>(plans);
        while (queue.Count > 0)
        {
            var plan = queue.First!.Value;
            queue.RemoveFirst();
            bool[] on = Rides(lines, plan.Points, plan.Heights, Mathf.Max(plan.Width, Reach));
            float Off(int i) => OffRoad(lines, plan.Points[i]);
            Mouths(on, Off);
            if (!trim)
            {
                int inside = RiddenInside(on);
                if (inside >= 0 && doubledAt == null) doubledAt = plan.Points[inside];
                lines.AddRoad(plan.Points, plan.Heights);
                continue;
            }
            if (System.Array.IndexOf(on, true) < 0) { own.Add(plan); lines.AddRoad(plan.Points, plan.Heights); continue; }
            for (int k = 1; k < on.Length; k++)
                if (on[k] && on[k - 1]) riddenMetres += Vector2.Distance(plan.Points[k - 1], plan.Points[k]);
            // Into the ride as far as it comes closer to the other road (Tail): the road meets the road it joins rather than
            // stopping at the edge of its reach. That tail is the junction's mouth, as the commit's guard reads it (Mouths).
            var slices = new List<RoadSpatialGrid.PlannedPath>();
            int a = 0;
            while (a < on.Length)
            {
                if (on[a]) { a++; continue; }
                int b = a;
                while (b + 1 < on.Length && !on[b + 1]) b++;
                // Onto the other road at each side: the first ridden point either side, where the two meet.
                int from = a > 0 ? a - 1 : a, to = b < on.Length - 1 ? b + 1 : b;
                // A short bit left between rides, or at a piece's end, that never leaves the network's reach is the other
                // road's too (the turn onto a crossing's bank): kept only where it goes somewhere of its own.
                if (!(to > from && (Leaves(lines, plan.Points, from, to) || Length(plan.Points, from, to) >= 2f * MinRun))) { a = b + 1; continue; }
                if (from < a) from = Tail(on, Off, from, -1);
                if (to > b) to = Tail(on, Off, to, +1);
                slices.Add(plan.Slice(from, to));
                a = b + 1;
            }
            // Judged once more only where it changed. A slice the same as its piece is kept as it is only where the commit's
            // guard keeps it too; else the piece keeps no point that rides (no tails), so every round leaves points out.
            if (slices.Count == 1 && slices[0].Points.Count == plan.Points.Count)
            {
                if (RiddenInside(on) < 0) { own.Add(plan); lines.AddRoad(plan.Points, plan.Heights); continue; }
                slices.Clear();
                for (int s = 0; s < on.Length; s++)
                {
                    if (on[s]) continue;
                    int e = s;
                    while (e + 1 < on.Length && !on[e + 1]) e++;
                    if (e > s) slices.Add(plan.Slice(s, e));
                    s = e;
                }
            }
            for (int k = slices.Count - 1; k >= 0; k--) queue.AddFirst(slices[k]);
        }
        return own;
    }

    /// <summary>How far a point lies from the network's road (structures' gaps not: a mouth never runs into one); infinite
    /// beyond a road's width.</summary>
    private static float OffRoad(Lines lines, Vector2 p) =>
        lines.Nearest(p, Reach, out Hit h, structures: false) ? h.Distance : float.PositiveInfinity;

    /// <summary>A junction's mouth: from the ridden point <paramref name="start"/> on into the ride (<paramref name="step"/>
    /// -1 or +1) as long as it still comes closer to the other road (5 cm or more a point) and is not yet on its surface
    /// (RoadBeside.OnRoad); never a stretch beside it, which stays the other road's. The last point of the mouth. The one
    /// rule both <see cref="Own"/> (the tail it keeps into a ride) and <see cref="Mouths"/> (the tail the guard lets be)
    /// read.</summary>
    private static int Tail(bool[] on, System.Func<int, float> off, int start, int step)
    {
        int k = start;
        while (k + step >= 0 && k + step < on.Length && on[k + step])
        {
            float here = off(k), next = off(k + step);
            if (float.IsPositiveInfinity(next) || here <= RoadBeside.OnRoad || next >= here - 0.05f) break;
            k += step;
        }
        return k;
    }

    /// <summary>Clears in <paramref name="on"/> the junctions' mouths a kept piece ends with: a ride touching one end of it
    /// (not both: that is a piece riding the whole way) that is all mouth (<see cref="Tail"/> from its inner end reaches the
    /// piece's end: it came in to the other road the whole way; a piece lying along the other road is no mouth).</summary>
    private static void Mouths(bool[] on, System.Func<int, float> off)
    {
        int a = 0;
        while (a < on.Length)
        {
            if (!on[a]) { a++; continue; }
            int b = a;
            while (b + 1 < on.Length && on[b + 1]) b++;
            bool atStart = a == 0, atEnd = b == on.Length - 1;
            if (atStart != atEnd && (atStart ? Tail(on, off, b, -1) == 0 : Tail(on, off, a, +1) == on.Length - 1))
                for (int k = a; k <= b; k++) on[k] = false;
            a = b + 1;
        }
    }

    private static bool Leaves(Lines lines, List<Vector2> points, int from, int to)
    {
        for (int k = from; k <= to; k++)
            if (!lines.Nearest(points[k], Reach, out _)) return true;
        return false;
    }

    private static float Length(List<Vector2> points, int from, int to)
    {
        float length = 0f;
        for (int k = from; k < to; k++) length += Vector2.Distance(points[k], points[k + 1]);
        return length;
    }

    /// <summary>Whether a crossing lies on the network's line (its banks and middle ride it): the other road's crossing, or
    /// the other road through the same water. A road riding there builds none of its own.</summary>
    public static bool RidesCrossing(Lines lines, RoadCrossing c)
    {
        if (c.Shared) return true;
        Vector2 dir = c.ToBank - c.FromBank;
        if (dir.sqrMagnitude < 1e-4f) return false;
        foreach (Vector2 p in new[] { c.FromBank, c.Center, c.ToBank })
            if (!lines.Nearest(p, Reach, out _, dir)) return false;
        return true;
    }

    /// <summary>Ridden stretches the commit path caught and left out (none, unless a route reached it without
    /// <see cref="Own"/>); counted per generation.</summary>
    public static int CaughtAtCommit => m_caught;
    internal static int m_caught;
    /// <summary>Metres of planned road left out as riding the network, and crossings left to the road that built them.</summary>
    public static float RiddenMetres => m_riddenCentimetres / 100f;
    internal static long m_riddenCentimetres;
    public static int CrossingsRidden => m_crossingsRidden;
    internal static int m_crossingsRidden;
    internal static void Reset() { m_caught = 0; m_riddenCentimetres = 0; m_crossingsRidden = 0; }
#if ROADS_TESTS
    /// <summary>The unit suite throws where the commit path catches a ridden stretch, so a route that slips past
    /// <see cref="Own"/> fails its test. Settable for a test of the release behaviour.</summary>
    internal static bool ThrowWhenCaught = true;
#endif
}
