using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>Versioned road geometry and connectivity. Identities do not depend on generation order.</summary>
public static class RoadTravelApi
{
    public const int ApiVersion = 1;
    internal static long WorldId;
    public static bool Ready => RoadNetworkGenerator.RoadsAvailable && Routes.Count > 0;
    public static int Revision { get; private set; }
    private static readonly List<Route> Routes = new();
    private static readonly Dictionary<string, Route> ById = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> Components = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Route> AccessRoads = new(StringComparer.Ordinal);
    private static readonly Dictionary<Vector2i, List<Segment>> Cells = new();
    private const float CellSize = 32f;
    public static void SetWorld(long worldId)
    {
        if (WorldId == worldId) return;
        Clear(); WorldId = worldId;
    }

    public sealed class Route
    {
        public string Id { get; }
        public string StartNode { get; }
        public string EndNode { get; }
        public Vector3 StartCenter { get; }
        public Vector3 EndCenter { get; }
        public float Width { get; }
        public IReadOnlyList<Vector3> Points { get; }
        internal Route(string id, string start, string end, Vector3 startCenter, Vector3 endCenter, float width, List<Vector3> points)
        {
            Id = id; StartNode = start; EndNode = end; StartCenter = startCenter; EndCenter = endCenter;
            Width = width; Points = points.AsReadOnly();
        }
    }

    private readonly struct Segment
    {
        internal readonly Route Road;
        internal readonly Vector3 A, B;
        internal Segment(Route road, Vector3 a, Vector3 b) { Road = road; A = a; B = b; }
    }

    public static IReadOnlyList<Route> GetRoutes() => Routes.AsReadOnly();
    public static string NodeId(Vector3 center) =>
        WorldId.ToString(CultureInfo.InvariantCulture) + ":" + Math.Round(center.x * 10).ToString(CultureInfo.InvariantCulture) + ":" + Math.Round(center.z * 10).ToString(CultureInfo.InvariantCulture);

    internal static void Add(Vector2 start, Vector2 end, float width, List<Vector2> points, List<float> heights)
    {
        List<Vector3> line = new(points.Count);
        for (int i = 0; i < points.Count; i++) line.Add(new Vector3(points[i].x, heights[i], points[i].y));
        Vector3 a = new(start.x, 0, start.y), b = new(end.x, 0, end.y);
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, true))
        {
            bool reverse = string.CompareOrdinal(NodeId(a), NodeId(b)) > 0;
            writer.Write(NodeId(reverse ? b : a)); writer.Write(NodeId(reverse ? a : b)); writer.Write(width);
            for (int i = 0; i < line.Count; i++)
            {
                Vector3 point = line[reverse ? line.Count - 1 - i : i];
                writer.Write(point.x); writer.Write(point.y); writer.Write(point.z);
            }
        }
        using SHA256 sha = SHA256.Create();
        string id = BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "");
        Routes.Add(new Route(id, NodeId(a), NodeId(b), a, b, width, line));
    }

    internal static void Clear()
    {
        Routes.Clear(); ById.Clear(); Components.Clear(); AccessRoads.Clear(); Cells.Clear(); Revision++;
    }

    internal static void Rebuild()
    {
        ById.Clear(); Components.Clear(); AccessRoads.Clear(); Cells.Clear();
        Dictionary<string, string> nodes = new(StringComparer.Ordinal);
        foreach (Route road in Routes)
        {
            ById[road.Id] = road; Components[road.Id] = road.Id;
            foreach (string node in new[] { road.StartNode, road.EndNode })
            {
                if (!AccessRoads.TryGetValue(node, out Route previous) || string.CompareOrdinal(road.Id, previous.Id) < 0) AccessRoads[node] = road;
                if (nodes.TryGetValue(node, out string prior)) Join(road.Id, prior);
                else nodes[node] = road.Id;
            }
            for (int i = 1; i < road.Points.Count; i++)
            {
                Segment segment = new(road, road.Points[i - 1], road.Points[i]);
                int minX = Mathf.FloorToInt(Mathf.Min(segment.A.x, segment.B.x) / CellSize);
                int maxX = Mathf.FloorToInt(Mathf.Max(segment.A.x, segment.B.x) / CellSize);
                int minZ = Mathf.FloorToInt(Mathf.Min(segment.A.z, segment.B.z) / CellSize);
                int maxZ = Mathf.FloorToInt(Mathf.Max(segment.A.z, segment.B.z) / CellSize);
                for (int x = minX; x <= maxX; x++) for (int z = minZ; z <= maxZ; z++)
                {
                    Vector2i key = new(x, z);
                    if (!Cells.TryGetValue(key, out List<Segment> cell)) Cells[key] = cell = new();
                    foreach (Segment other in cell)
                        if (other.Road != road && Crosses(segment, other)) Join(road.Id, other.Road.Id);
                    cell.Add(segment);
                }
            }
        }
        foreach (string id in ById.Keys) Components[id] = Root(id);
        Revision++;
    }

    private static string Root(string id)
    {
        string root = id;
        while (Components[root] != root) root = Components[root];
        while (Components[id] != id) { string next = Components[id]; Components[id] = root; id = next; }
        return root;
    }
    private static void Join(string a, string b)
    {
        a = Root(a); b = Root(b);
        if (a != b) Components[string.CompareOrdinal(a, b) < 0 ? b : a] = string.CompareOrdinal(a, b) < 0 ? a : b;
    }
    private static float Cross(Vector3 a, Vector3 b) => a.x * b.z - a.z * b.x;
    private static bool Crosses(Segment a, Segment b)
    {
        Vector3 r = a.B - a.A, s = b.B - b.A;
        float d = Cross(r, s);
        if (Mathf.Abs(d) < 0.0001f)
        {
            float length = r.x * r.x + r.z * r.z;
            if (length < 0.0001f || Mathf.Abs(Cross(b.A - a.A, r)) > 0.001f) return false;
            float t0 = ((b.A.x - a.A.x) * r.x + (b.A.z - a.A.z) * r.z) / length;
            float t1 = ((b.B.x - a.A.x) * r.x + (b.B.z - a.A.z) * r.z) / length;
            float lo = Mathf.Max(0, Mathf.Min(t0, t1)), hi = Mathf.Min(1, Mathf.Max(t0, t1));
            if (lo > hi) return false;
            Vector3 p = a.A + r * ((lo + hi) * 0.5f);
            float otherLength = s.x * s.x + s.z * s.z;
            float fraction = otherLength > 0 ? Mathf.Clamp01(((p.x - b.A.x) * s.x + (p.z - b.A.z) * s.z) / otherLength) : 0;
            return Mathf.Abs(p.y - (b.A + s * fraction).y) <= 2;
        }
        float t = Cross(b.A - a.A, s) / d, u = Cross(b.A - a.A, r) / d;
        return t >= 0 && t <= 1 && u >= 0 && u <= 1 && Mathf.Abs((a.A + r * t).y - (b.A + s * u).y) <= 2f;
    }

    public static bool Connected(string firstRoute, string secondRoute) => Ready &&
        Components.TryGetValue(firstRoute, out string a) && Components.TryGetValue(secondRoute, out string b) && a == b;

    /// <summary>Projects onto centerline segments in nearby cells. Equal distances use the route identity.</summary>
    public static bool TryFindRoad(Vector3 position, float radius, out string routeId, out Vector3 point, out Vector3 direction, out float width)
    {
        routeId = ""; point = default; direction = default; width = 0;
        if (!Ready || radius < 0 || radius > 32f) return false;
        float best = radius * radius;
        for (int x = Mathf.FloorToInt((position.x - radius) / CellSize); x <= Mathf.FloorToInt((position.x + radius) / CellSize); x++)
        for (int z = Mathf.FloorToInt((position.z - radius) / CellSize); z <= Mathf.FloorToInt((position.z + radius) / CellSize); z++)
        {
            if (!Cells.TryGetValue(new Vector2i(x, z), out List<Segment> cell)) continue;
            foreach (Segment segment in cell)
            {
                Vector3 delta = segment.B - segment.A;
                float length = delta.x * delta.x + delta.z * delta.z;
                if (length < 0.0001f) continue;
                Vector3 relative = position - segment.A;
                float t = Mathf.Clamp01((relative.x * delta.x + relative.z * delta.z) / length);
                Vector3 candidate = segment.A + delta * t;
                float dx = position.x - candidate.x, dz = position.z - candidate.z;
                float distance = dx * dx + dz * dz;
                if (Mathf.Abs(position.y - candidate.y) > 6f || distance > best ||
                    (distance == best && routeId.Length > 0 && string.CompareOrdinal(segment.Road.Id, routeId) >= 0)) continue;
                best = distance; point = candidate; direction = new Vector3(delta.x, 0, delta.z).normalized;
                width = segment.Road.Width; routeId = segment.Road.Id;
            }
        }
        return routeId.Length > 0;
    }

    /// <summary>Returns only a road generated to this location node, never a nearby road.</summary>
    public static bool TryGetLocationAccess(Vector3 center, out string routeId, out Vector3 point, out Vector3 direction, out float width)
    {
        string node = NodeId(center);
        if (AccessRoads.TryGetValue(node, out Route road))
        {
            if (road.Points.Count < 2) { routeId = ""; point = direction = default; width = 0; return false; }
            bool start = road.StartNode == node;
            int step = start ? 1 : -1;
            int i = start ? 0 : road.Points.Count - 1;
            float length = 0;
            for (int k = 1; k < road.Points.Count; k++)
            {
                Vector3 delta = road.Points[k] - road.Points[k - 1];
                length += Mathf.Sqrt(delta.x * delta.x + delta.z * delta.z);
            }
            float remaining = Mathf.Min(40f, length * 0.35f);
            point = road.Points[i]; direction = default;
            while (i + step >= 0 && i + step < road.Points.Count)
            {
                Vector3 next = road.Points[i + step]; Vector3 delta = next - point;
                float distance = Mathf.Sqrt(delta.x * delta.x + delta.z * delta.z);
                direction = new Vector3(delta.x, 0, delta.z).normalized;
                if (distance >= remaining && distance > 0.0001f) { point = Vector3.Lerp(point, next, remaining / distance); break; }
                remaining -= distance; point = next; i += step;
            }
            routeId = road.Id; width = road.Width; return Ready;
        }
        routeId = ""; point = direction = default; width = 0; return false;
    }

    public static byte[] Export()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write(ApiVersion); writer.Write(WorldId); writer.Write(Routes.Count);
        foreach (Route road in Routes)
        {
            writer.Write(road.Id); writer.Write(road.StartNode); writer.Write(road.EndNode);
            writer.Write(road.StartCenter.x); writer.Write(road.StartCenter.z); writer.Write(road.EndCenter.x); writer.Write(road.EndCenter.z);
            writer.Write(road.Width); writer.Write(road.Points.Count);
            foreach (Vector3 p in road.Points) { writer.Write(p.x); writer.Write(p.y); writer.Write(p.z); }
        }
        return stream.ToArray();
    }
    public static bool Import(byte[] data)
    {
        try
        {
            using BinaryReader reader = new(new MemoryStream(data));
            if (reader.ReadInt32() != ApiVersion || reader.ReadInt64() != WorldId) return false;
            int count = reader.ReadInt32();
            if (count < 0 || count > 10000) return false;
            List<Route> loaded = new(count);
            for (int i = 0; i < count; i++)
            {
                string id = reader.ReadString(), start = reader.ReadString(), end = reader.ReadString();
                Vector3 a = new(reader.ReadSingle(), 0, reader.ReadSingle()), b = new(reader.ReadSingle(), 0, reader.ReadSingle());
                float width = reader.ReadSingle(); int n = reader.ReadInt32();
                if (n < 2 || n > 1000000 || width < 1 || width > 32) return false;
                List<Vector3> points = new(n);
                for (int j = 0; j < n; j++) points.Add(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
                loaded.Add(new Route(id, start, end, a, b, width, points));
            }
            Clear(); Routes.AddRange(loaded); Rebuild(); RoadNetworkGenerator.MarkRoadsLoadedFromZDO(); return true;
        }
        catch (Exception e) when (e is IOException || e is ArgumentException) { return false; }
    }
}
