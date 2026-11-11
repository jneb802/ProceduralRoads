using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// A read-only copy of the road network's shape for other mods: which roads connect, the nearest road to a point, and
/// where a road ends at a registered or configured location. The server builds it from the finished network
/// (<see cref="RoadTopologyService"/>); clients with the mod receive the same bytes and never hold a network or write terrain.
/// Server and client answer every question from the same decoded copy, so they agree to the centimetre.
/// </summary>
/// <remarks>
/// Built from the stored road points, not from the plans: the saved network keeps no road identity (RoadSpatialGrid keeps
/// a set of points per cell). Points about a metre apart along a road (the planner's dense spacing) join when they stand
/// within a road's reach of each other (<see cref="RoadRide.Reach"/>) and at each other's height
/// (<see cref="RoadBeside.AtItsHeight"/>): a road ending on another joins it; roads never cross or run beside each other.
/// Bridges and staircases leave a gap in the points; their two ends are given as links.
/// </remarks>
public sealed class RoadTopology
{
    /// <summary>The byte format of <see cref="Serialize"/>. A reader refuses any other.</summary>
    public const int FormatVersion = 1;
    private const int Magic = 0x31545250; // "PRT1"
    /// <summary>A sane ceiling for a decoded count: a corrupt or hostile package is refused before it allocates.</summary>
    private const int MostItems = 50_000_000;
    private const float Centimetre = 0.01f;

    /// <summary>One road point given to <see cref="Build"/>: its centre, height and width, and whether a player walks it
    /// (<see cref="RoadSpatialGrid.RoadPoint.IsRoad"/>; a waded ford's paint-only point connects but is never offered).</summary>
    public readonly struct SourcePoint
    {
        public readonly Vector2 Position;
        public readonly float Height;
        public readonly float Width;
        public readonly bool Walked;
        public SourcePoint(Vector2 position, float height, float width, bool walked)
        { Position = position; Height = height; Width = width; Walked = walked; }
    }

    /// <summary>A location (a <see cref="ZoneSystem.LocationInstance"/>): its centre and how far from it a road still arrives
    /// at it. Every location joins the roads that arrive at it (a player walks through it from one to the other); one
    /// <see cref="Offered"/> also records its road end for other mods (<see cref="TryGetLocationAccess"/>).</summary>
    public readonly struct SourceLocation
    {
        public readonly Vector2 Centre;
        public readonly float Reach;
        public readonly bool Offered;
        public SourceLocation(Vector2 centre, float reach, bool offered = true) { Centre = centre; Reach = reach; Offered = offered; }
    }

    private readonly struct Point : IComparable<Point>
    {
        internal readonly int X, Z, H, W;
        internal readonly bool Walked;
        internal Point(int x, int z, int h, int w, bool walked) { X = x; Z = z; H = h; W = w; Walked = walked; }
        public int CompareTo(Point other)
        {
            int c = X.CompareTo(other.X); if (c != 0) return c;
            c = Z.CompareTo(other.Z); if (c != 0) return c;
            c = H.CompareTo(other.H); if (c != 0) return c;
            c = W.CompareTo(other.W); if (c != 0) return c;
            return Walked.CompareTo(other.Walked);
        }
        internal bool SameAs(Point other) => CompareTo(other) == 0;
        internal Vector2 Flat => new(X * Centimetre, Z * Centimetre);
        internal float Height => H * Centimetre;
        internal float Width => W * Centimetre;
    }

    private readonly struct Access
    {
        internal readonly int ZoneX, ZoneZ, Point;
        internal Access(int zoneX, int zoneZ, int point) { ZoneX = zoneX; ZoneZ = zoneZ; Point = point; }
    }

    private readonly Point[] m_points;
    private readonly int[] m_component;
    private readonly Access[] m_access;
    private readonly Dictionary<long, List<int>> m_cells = new();
    private readonly Dictionary<long, int> m_accessByZone = new();
    private readonly byte[] m_bytes;

    /// <summary>The road network version (<see cref="RoadSpatialGrid.RoadNetworkVersion"/>) this was built from.</summary>
    public int NetworkVersion { get; }
    /// <summary>The join reach it was built with; also the neighbourhood a road's heading is read over.</summary>
    public float Reach { get; }
    /// <summary>A hash of its bytes: two copies with the same hash answer every question alike.</summary>
    public long Hash { get; }
    public int PointCount => m_points.Length;
    public int Components { get; }
    public int AccessCount => m_access.Length;
    /// <summary>Links (bridge and staircase ends) given to <see cref="Build"/> and how many found road at both ends. Not
    /// carried by <see cref="Serialize"/>: zero on a decoded copy.</summary>
    public int LinksGiven { get; private set; }
    public int LinksJoined { get; private set; }
    /// <summary>The links with an end that found no road in reach, and which ends (for the diagnostics). Empty on a decoded copy.</summary>
    public IReadOnlyList<(Vector2 a, Vector2 b, bool aFound, bool bFound)> UnjoinedLinks { get; private set; } =
        Array.Empty<(Vector2, Vector2, bool, bool)>();
    /// <summary>The compressed bytes clients receive; <see cref="Deserialize"/> of them gives an equal copy.</summary>
    public byte[] Bytes => m_bytes;

    private RoadTopology(int networkVersion, float reach, Point[] points, int[] component, Access[] access, byte[] bytes, long hash)
    {
        NetworkVersion = networkVersion; Reach = reach; m_points = points; m_component = component; m_access = access;
        m_bytes = bytes; Hash = hash;
        int components = 0;
        foreach (int c in component) components = Math.Max(components, c + 1);
        Components = components;
        for (int i = 0; i < points.Length; i++)
        {
            long key = CellKey(points[i].Flat);
            if (!m_cells.TryGetValue(key, out List<int> list)) m_cells[key] = list = new List<int>();
            list.Add(i);
        }
        for (int i = 0; i < access.Length; i++) m_accessByZone[ZoneKey(access[i].ZoneX, access[i].ZoneZ)] = i;
    }

    /// <summary>
    /// Build from the stored points, the links across structures, and the locations to find the road ends of. The same input in
    /// any order gives the same bytes: points are rounded to centimetres, sorted and de-duplicated (the grid stores a point
    /// in every cell its width reaches) before anything is joined.
    /// </summary>
    public static RoadTopology Build(int networkVersion, float reach, IEnumerable<SourcePoint> points,
        IEnumerable<(Vector2 a, Vector2 b)> links, IEnumerable<SourceLocation> locations)
    {
        if (!(reach > 0f)) throw new ArgumentOutOfRangeException(nameof(reach), "The join reach must be positive.");
        List<Point> sorted = new();
        foreach (SourcePoint p in points)
            sorted.Add(new Point(Cm(p.Position.x), Cm(p.Position.y), Cm(p.Height), Cm(p.Width), p.Walked));
        sorted.Sort();
        List<Point> unique = new(sorted.Count);
        foreach (Point p in sorted)
            if (unique.Count == 0 || !unique[unique.Count - 1].SameAs(p)) unique.Add(p);
        Point[] all = unique.ToArray();

        Dictionary<long, List<int>> cells = new();
        for (int i = 0; i < all.Length; i++)
        {
            long key = CellKey(all[i].Flat, reach);
            if (!cells.TryGetValue(key, out List<int> list)) cells[key] = list = new List<int>();
            list.Add(i);
        }

        int[] parent = new int[all.Length];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        float reachSquared = reach * reach;
        for (int i = 0; i < all.Length; i++)
        {
            Vector2 p = all[i].Flat;
            (int cx, int cz) = Cell(p, reach);
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!cells.TryGetValue(CellKey(cx + dx, cz + dz), out List<int> near)) continue;
                    foreach (int j in near)
                        if (j > i && (all[j].Flat - p).sqrMagnitude <= reachSquared && RoadBeside.AtItsHeight(all[j].Height, all[i].Height))
                            Union(parent, i, j);
                }
        }

        // A structure's two ends are one node each, past the points: an end joins the road within reach of it, its own other
        // end, and every other structure end within reach (a staircase that runs straight onto a bridge has no road between).
        List<(Vector2 a, Vector2 b)> linkList = new(links);
        int given = linkList.Count, joined = 0;
        int ends = linkList.Count * 2;
        Array.Resize(ref parent, all.Length + ends);
        for (int k = all.Length; k < parent.Length; k++) parent[k] = k;
        Vector2 End(int k) => k % 2 == 0 ? linkList[k / 2].a : linkList[k / 2].b;
        bool[] onRoad = new bool[ends];
        for (int k = 0; k < ends; k++)
        {
            Union(parent, all.Length + k, all.Length + (k ^ 1));
            int road = Nearest(all, cells, reach, End(k), reach, walkedOnly: false);
            if (road >= 0) { Union(parent, all.Length + k, road); onRoad[k] = true; }
        }
        Dictionary<long, List<int>> endCells = new();
        for (int k = 0; k < ends; k++)
        {
            long key = CellKey(End(k), reach);
            if (!endCells.TryGetValue(key, out List<int> list)) endCells[key] = list = new List<int>();
            list.Add(k);
        }
        bool[] touches = new bool[ends];
        for (int k = 0; k < ends; k++)
        {
            (int cx, int cz) = Cell(End(k), reach);
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!endCells.TryGetValue(CellKey(cx + dx, cz + dz), out List<int> near)) continue;
                    foreach (int other in near)
                        if (other / 2 != k / 2 && (End(other) - End(k)).sqrMagnitude <= reachSquared)
                        { Union(parent, all.Length + k, all.Length + other); touches[k] = true; }
                }
        }
        List<(Vector2, Vector2, bool, bool)> unjoined = new();
        for (int l = 0; l < given; l++)
        {
            bool a = onRoad[2 * l] || touches[2 * l], b = onRoad[2 * l + 1] || touches[2 * l + 1];
            if (a && b) joined++;
            else unjoined.Add((linkList[l].a, linkList[l].b, onRoad[2 * l], onRoad[2 * l + 1]));
        }

        List<SourceLocation> locationList = new(locations);
        foreach (SourceLocation location in locationList)
        {
            // The roads arriving at a location meet there: each walked point within its reach joins the location's first one.
            int first = -1;
            (int cx, int cz) = Cell(location.Centre, reach);
            int span = Mathf.CeilToInt(location.Reach / reach);
            float locationSquared = location.Reach * location.Reach;
            for (int dx = -span; dx <= span; dx++)
                for (int dz = -span; dz <= span; dz++)
                {
                    if (!cells.TryGetValue(CellKey(cx + dx, cz + dz), out List<int> near)) continue;
                    foreach (int i in near)
                    {
                        if (!all[i].Walked || (all[i].Flat - location.Centre).sqrMagnitude > locationSquared) continue;
                        if (first < 0) first = i; else Union(parent, first, i);
                    }
                }
        }

        // Component numbers in the canonical point order: the same network numbers its roads alike every time.
        int[] component = new int[all.Length];
        Dictionary<int, int> label = new();
        for (int i = 0; i < all.Length; i++)
        {
            int root = Find(parent, i);
            if (!label.TryGetValue(root, out int id)) label[root] = id = label.Count;
            component[i] = id;
        }

        List<(int zx, int zz, Vector2 centre, int point)> found = new();
        foreach (SourceLocation location in locationList)
        {
            if (!location.Offered) continue;
            int point = Nearest(all, cells, reach, location.Centre, location.Reach, walkedOnly: true);
            if (point < 0) continue;
            found.Add((RoadConstants.ZoneIndex(location.Centre.x), RoadConstants.ZoneIndex(location.Centre.y), location.Centre, point));
        }
        // One location per zone (the game's own rule); where two inputs share a zone, the one nearer its road keeps it.
        found.Sort((x, y) =>
        {
            int c = x.zx.CompareTo(y.zx); if (c != 0) return c;
            c = x.zz.CompareTo(y.zz); if (c != 0) return c;
            return (all[x.point].Flat - x.centre).sqrMagnitude.CompareTo((all[y.point].Flat - y.centre).sqrMagnitude);
        });
        List<Access> access = new();
        foreach ((int zx, int zz, Vector2 _, int point) in found)
            if (access.Count == 0 || access[access.Count - 1].ZoneX != zx || access[access.Count - 1].ZoneZ != zz)
                access.Add(new Access(zx, zz, point));

        byte[] raw = Encode(networkVersion, reach, all, component, access);
        RoadTopology built = new(networkVersion, reach, all, component, access.ToArray(), Compress(raw), HashOf(raw))
        { LinksGiven = given, LinksJoined = joined, UnjoinedLinks = unjoined };
        return built;
    }

    /// <summary>A copy from <see cref="Bytes"/>, or null where they are not a whole copy in this format.</summary>
    public static RoadTopology? Deserialize(byte[] compressed)
    {
        try
        {
            byte[] raw = Decompress(compressed);
            using MemoryStream stream = new(raw);
            using BinaryReader reader = new(stream);
            if (reader.ReadInt32() != Magic || reader.ReadInt32() != FormatVersion) return null;
            int networkVersion = reader.ReadInt32();
            float reach = reader.ReadSingle();
            if (!(reach > 0f)) return null;
            int count = Count(reader);
            Point[] points = new Point[count];
            int[] component = new int[count];
            int x = 0, z = 0, h = 0, w = 0, labels = 0;
            for (int i = 0; i < count; i++)
            {
                x += ReadSigned(reader); z += ReadSigned(reader); h += ReadSigned(reader); w += ReadSigned(reader);
                bool walked = reader.ReadByte() != 0;
                points[i] = new Point(x, z, h, w, walked);
                // Components are numbered in point order (Build): each is one already seen or the next new one.
                component[i] = Count(reader);
                if (component[i] > labels) return null;
                if (component[i] == labels) labels++;
            }
            for (int i = 1; i < count; i++) if (points[i].CompareTo(points[i - 1]) <= 0) return null;
            int accessCount = Count(reader);
            Access[] access = new Access[accessCount];
            for (int i = 0; i < accessCount; i++)
            {
                int zx = ReadSigned(reader), zz = ReadSigned(reader), point = Count(reader);
                if (point >= count || !points[point].Walked) return null;
                access[i] = new Access(zx, zz, point);
            }
            if (stream.Position != stream.Length) return null;
            return new RoadTopology(networkVersion, reach, points, component, access, compressed, HashOf(raw));
        }
        catch (Exception e) when (e is IOException || e is InvalidDataException || e is ArgumentException || e is OverflowException)
        {
            return null;
        }
    }

    /// <summary>The road nearest <paramref name="position"/> within <paramref name="radius"/> (across only; a waded ford is
    /// never offered): its route, the point on its centreline across from <paramref name="position"/>, its heading there,
    /// and its width.</summary>
    public bool TryFindRoad(Vector3 position, float radius, out string route, out Vector3 point, out Vector3 direction, out float width)
    {
        int nearest = Nearest(m_points, m_cells, Reach, new Vector2(position.x, position.z), radius, walkedOnly: true);
        return Describe(nearest, new Vector2(position.x, position.z), out route, out point, out direction, out width);
    }

    /// <summary>Where a road ends at the location whose centre is <paramref name="centre"/> (one location per zone): its
    /// route, the road's centre there, its heading and width. False where no road reached the location when built.</summary>
    public bool TryGetLocationAccess(Vector3 centre, out string route, out Vector3 point, out Vector3 direction, out float width)
    {
        int zx = RoadConstants.ZoneIndex(centre.x), zz = RoadConstants.ZoneIndex(centre.z);
        if (!m_accessByZone.TryGetValue(ZoneKey(zx, zz), out int index))
        {
            route = ""; point = direction = default; width = 0f;
            return false;
        }
        Point at = m_points[m_access[index].Point];
        return Describe(m_access[index].Point, at.Flat, out route, out point, out direction, out width);
    }

    /// <summary>Whether two routes from this copy are one connected road network. A route from another copy is not.</summary>
    public bool Connected(string first, string second) =>
        TryParseRoute(first, out int a) && TryParseRoute(second, out int b) && a == b;

    /// <summary>The route name of a component: the copy's hash and the component, so a name never matches across copies.</summary>
    public string RouteName(int component) => Hash.ToString("x16") + ":" + component;

    /// <summary>How many points each component holds, largest first (for the diagnostics).</summary>
    public List<int> ComponentSizes()
    {
        int[] sizes = new int[Components];
        foreach (int c in m_component) sizes[c]++;
        List<int> list = new(sizes);
        list.Sort((a, b) => b.CompareTo(a));
        return list;
    }

    /// <summary>Each location's road end: its zone, the road point and its component (for the diagnostics).</summary>
    public List<(int zoneX, int zoneZ, Vector3 road, int component)> LocationAccesses()
    {
        List<(int, int, Vector3, int)> list = new();
        foreach (Access a in m_access)
        {
            Point p = m_points[a.Point];
            list.Add((a.ZoneX, a.ZoneZ, new Vector3(p.Flat.x, p.Height, p.Flat.y), m_component[a.Point]));
        }
        return list;
    }

    /// <summary>Each component's point count and the box its points span, largest first (for the diagnostics).</summary>
    public List<(int component, int points, Vector2 min, Vector2 max)> ComponentExtents()
    {
        int[] counts = new int[Components];
        Vector2[] min = new Vector2[Components], max = new Vector2[Components];
        for (int i = 0; i < m_points.Length; i++)
        {
            int c = m_component[i];
            Vector2 p = m_points[i].Flat;
            if (counts[c]++ == 0) { min[c] = p; max[c] = p; continue; }
            min[c] = new Vector2(Mathf.Min(min[c].x, p.x), Mathf.Min(min[c].y, p.y));
            max[c] = new Vector2(Mathf.Max(max[c].x, p.x), Mathf.Max(max[c].y, p.y));
        }
        List<(int, int, Vector2, Vector2)> list = new();
        for (int c = 0; c < Components; c++) list.Add((c, counts[c], min[c], max[c]));
        list.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return list;
    }

    /// <summary>For each component, the nearest point of any other component within <paramref name="radius"/> across: how far,
    /// where, and the height between (for the diagnostics: a near miss between two networks shows a join this build missed).</summary>
    public List<(int component, int other, float distance, Vector2 at, float rise)> NearestOtherComponents(float radius)
    {
        Dictionary<int, (int other, float distance, Vector2 at, float rise)> best = new();
        int span = Mathf.CeilToInt(radius / Reach);
        for (int i = 0; i < m_points.Length; i++)
        {
            Vector2 p = m_points[i].Flat;
            (int cx, int cz) = Cell(p, Reach);
            for (int dx = -span; dx <= span; dx++)
                for (int dz = -span; dz <= span; dz++)
                {
                    if (!m_cells.TryGetValue(CellKey(cx + dx, cz + dz), out List<int> cell)) continue;
                    foreach (int j in cell)
                    {
                        if (m_component[j] == m_component[i]) continue;
                        float d = (m_points[j].Flat - p).magnitude;
                        if (d > radius || best.TryGetValue(m_component[i], out var b) && b.distance <= d) continue;
                        best[m_component[i]] = (m_component[j], d, p, m_points[j].Height - m_points[i].Height);
                    }
                }
        }
        List<(int, int, float, Vector2, float)> list = new();
        foreach (KeyValuePair<int, (int other, float distance, Vector2 at, float rise)> kv in best)
            list.Add((kv.Key, kv.Value.other, kv.Value.distance, kv.Value.at, kv.Value.rise));
        list.Sort((a, b) => a.Item3.CompareTo(b.Item3));
        return list;
    }

    private bool TryParseRoute(string route, out int component)
    {
        component = -1;
        string prefix = Hash.ToString("x16") + ":";
        return route != null && route.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(route.Substring(prefix.Length), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out component)
            && component >= 0 && component < Components;
    }

    /// <summary>A point's route, and the road's heading across its neighbourhood (the main axis of its own component's walked
    /// points within <see cref="Reach"/>), with <paramref name="from"/> projected onto that line.</summary>
    private bool Describe(int index, Vector2 from, out string route, out Vector3 point, out Vector3 direction, out float width)
    {
        route = ""; point = direction = default; width = 0f;
        if (index < 0) return false;
        Point at = m_points[index];
        Vector2 centre = at.Flat;
        int component = m_component[index];
        List<Vector2> near = new();
        (int cx, int cz) = Cell(centre, Reach);
        float reachSquared = Reach * Reach;
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (!m_cells.TryGetValue(CellKey(cx + dx, cz + dz), out List<int> cell)) continue;
                foreach (int j in cell)
                    if (m_points[j].Walked && m_component[j] == component && (m_points[j].Flat - centre).sqrMagnitude <= reachSquared)
                        near.Add(m_points[j].Flat);
            }
        if (near.Count < 2) return false;
        float sumX = 0f, sumZ = 0f;
        foreach (Vector2 p in near) { sumX += p.x; sumZ += p.y; }
        Vector2 mean = new(sumX / near.Count, sumZ / near.Count);
        float xx = 0f, xz = 0f, zz = 0f;
        foreach (Vector2 p in near)
        {
            Vector2 d = p - mean;
            xx += d.x * d.x; xz += d.x * d.y; zz += d.y * d.y;
        }
        float angle = 0.5f * Mathf.Atan2(2f * xz, xx - zz);
        Vector2 axis = new(Mathf.Cos(angle), Mathf.Sin(angle));
        // Along the road no further than its own points reach: at a road's end the line runs on past it (into a river).
        float least = float.PositiveInfinity, most = float.NegativeInfinity;
        foreach (Vector2 p in near)
        {
            float along = Vector2.Dot(p - mean, axis);
            least = Mathf.Min(least, along); most = Mathf.Max(most, along);
        }
        Vector2 on = mean + axis * Mathf.Clamp(Vector2.Dot(from - mean, axis), least, most);
        route = RouteName(component);
        point = new Vector3(on.x, at.Height, on.y);
        direction = new Vector3(axis.x, 0f, axis.y);
        width = at.Width;
        return true;
    }

    private static int Nearest(Point[] points, Dictionary<long, List<int>> cells, float cellSize, Vector2 at, float radius, bool walkedOnly)
    {
        if (!(radius > 0f)) return -1;
        (int cx, int cz) = Cell(at, cellSize);
        int span = Mathf.CeilToInt(radius / cellSize);
        float best = radius * radius;
        int found = -1;
        for (int dx = -span; dx <= span; dx++)
            for (int dz = -span; dz <= span; dz++)
            {
                if (!cells.TryGetValue(CellKey(cx + dx, cz + dz), out List<int> cell)) continue;
                foreach (int i in cell)
                {
                    if (walkedOnly && !points[i].Walked) continue;
                    float d = (points[i].Flat - at).sqrMagnitude;
                    // Ties go to the lower index: the canonical order, the same on every peer.
                    if (d < best || (d == best && (found < 0 || i < found))) { best = d; found = i; }
                }
            }
        return found;
    }

    private long CellKey(Vector2 p) => CellKey(p, Reach);
    private static long CellKey(Vector2 p, float size) { (int x, int z) = Cell(p, size); return CellKey(x, z); }
    private static (int, int) Cell(Vector2 p, float size) => (Mathf.FloorToInt(p.x / size), Mathf.FloorToInt(p.y / size));
    private static long CellKey(int x, int z) => ((long)x << 32) | (uint)z;
    private static long ZoneKey(int x, int z) => CellKey(x, z);
    private static int Cm(float metres) => Mathf.RoundToInt(metres / Centimetre);

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
        return i;
    }

    private static void Union(int[] parent, int a, int b)
    {
        int ra = Find(parent, a), rb = Find(parent, b);
        if (ra == rb) return;
        // The lower index is the root, so the labelling never depends on the order of the unions.
        if (ra < rb) parent[rb] = ra; else parent[ra] = rb;
    }

    private static byte[] Encode(int networkVersion, float reach, Point[] points, int[] component, List<Access> access)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(networkVersion);
            writer.Write(reach);
            WriteCount(writer, points.Length);
            int x = 0, z = 0, h = 0, w = 0;
            for (int i = 0; i < points.Length; i++)
            {
                Point p = points[i];
                WriteSigned(writer, p.X - x); WriteSigned(writer, p.Z - z); WriteSigned(writer, p.H - h); WriteSigned(writer, p.W - w);
                x = p.X; z = p.Z; h = p.H; w = p.W;
                writer.Write((byte)(p.Walked ? 1 : 0));
                WriteCount(writer, component[i]);
            }
            WriteCount(writer, access.Count);
            foreach (Access a in access)
            {
                WriteSigned(writer, a.ZoneX); WriteSigned(writer, a.ZoneZ); WriteCount(writer, a.Point);
            }
        }
        return stream.ToArray();
    }

    private static void WriteCount(BinaryWriter writer, int value)
    {
        uint v = (uint)value;
        while (v >= 0x80) { writer.Write((byte)(v | 0x80)); v >>= 7; }
        writer.Write((byte)v);
    }

    private static int Count(BinaryReader reader)
    {
        uint value = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            byte b = reader.ReadByte();
            value |= (uint)(b & 0x7f) << shift;
            if (b < 0x80)
            {
                if (value > MostItems) throw new InvalidDataException("A count is out of range.");
                return (int)value;
            }
        }
        throw new InvalidDataException("A count is too long.");
    }

    private static void WriteSigned(BinaryWriter writer, int value) => WriteUnsigned(writer, (uint)((value << 1) ^ (value >> 31)));

    private static void WriteUnsigned(BinaryWriter writer, uint v)
    {
        while (v >= 0x80) { writer.Write((byte)(v | 0x80)); v >>= 7; }
        writer.Write((byte)v);
    }

    private static int ReadSigned(BinaryReader reader)
    {
        uint value = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            byte b = reader.ReadByte();
            value |= (uint)(b & 0x7f) << shift;
            if (b < 0x80) return (int)(value >> 1) ^ -(int)(value & 1);
        }
        throw new InvalidDataException("A number is too long.");
    }

    private static byte[] Compress(byte[] raw)
    {
        using MemoryStream stream = new();
        using (GZipStream zip = new(stream, CompressionMode.Compress, true)) zip.Write(raw, 0, raw.Length);
        return stream.ToArray();
    }

    private static byte[] Decompress(byte[] compressed)
    {
        using GZipStream zip = new(new MemoryStream(compressed), CompressionMode.Decompress);
        using MemoryStream raw = new();
        zip.CopyTo(raw);
        return raw.ToArray();
    }

    /// <summary>FNV-1a over the uncompressed bytes: compression output may differ between runtimes, the content does not.</summary>
    private static long HashOf(byte[] raw)
    {
        unchecked
        {
            ulong hash = 14695981039346656037UL;
            foreach (byte b in raw) { hash ^= b; hash *= 1099511628211UL; }
            return (long)hash;
        }
    }
}
