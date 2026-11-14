using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Which generated road stored which points (debug only, with PROCEDURALROADS_ROADS_LOG): each piece a generation commits,
/// a road's own pieces, its pieces into a location, its ends carried into one, its junction fills and its start pads, and
/// each manual road's pieces, kept with the road's label in this process. Staircases are not listed: their pieces are
/// structures, not stored road points, and their cuts only lower the ground. Nothing is saved or sent: a network loaded
/// from a save has no record, a network generated here has one until the network is reset (RoadNetworkGenerator.ResetData:
/// a whole-world generation, road_regen_island, which replaces the whole network, a load). Read with the road_owner command
/// or <see cref="Near"/>.
/// </summary>
public static class RoadOwners
{
    /// <summary>Kept only with PROCEDURALROADS_ROADS_LOG on (<see cref="RoadNetworkGenerator.RoadsLog"/>), read once at
    /// startup. Settable only in the tests' build.</summary>
#if ROADS_TESTS
    internal static bool Enabled = RoadNetworkGenerator.RoadsLog;
#else
    internal static readonly bool Enabled = RoadNetworkGenerator.RoadsLog;
#endif

    private static readonly object s_gate = new();
    private static readonly List<(string road, string kind, Vector2[] points)> s_pieces = new();

    /// <summary>Notes that the road <paramref name="road"/> stored <paramref name="points"/> as a <paramref name="kind"/>.</summary>
    internal static void Record(string? road, string kind, IReadOnlyList<Vector2> points)
    {
        if (!Enabled || points.Count == 0) return;
        var copy = new Vector2[points.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = points[i];
        lock (s_gate) s_pieces.Add((road ?? "(unlabelled)", kind, copy));
    }

    /// <summary>Forgets every record, with the network.</summary>
    internal static void Clear() { lock (s_gate) s_pieces.Clear(); }

    /// <summary>One line per stored piece with points within <paramref name="radius"/> of (<paramref name="x"/>,
    /// <paramref name="z"/>), nearest first: its road, what it is, how many of its points are within reach, the nearest, and
    /// the piece's ends.</summary>
    public static List<string> Near(float x, float z, float radius)
    {
        var at = new Vector2(x, z);
        var found = new List<(float nearest, string line)>();
        lock (s_gate)
            foreach (var (road, kind, points) in s_pieces)
            {
                int within = 0;
                float nearest = float.PositiveInfinity;
                Vector2 nearestAt = default;
                foreach (Vector2 p in points)
                {
                    float d = Vector2.Distance(p, at);
                    if (d <= radius) within++;
                    if (d < nearest) { nearest = d; nearestAt = p; }
                }
                if (within == 0) continue;
                Vector2 a = points[0], b = points[points.Length - 1];
                found.Add((nearest, string.Format(CultureInfo.InvariantCulture,
                    "{0} ({1}): {2} of {3} point(s) within {4:F0} m, nearest {5:F1} m at ({6:F1}, {7:F1}); piece ({8:F1}, {9:F1}) -> ({10:F1}, {11:F1})",
                    road, kind, within, points.Length, radius, nearest, nearestAt.x, nearestAt.y, a.x, a.y, b.x, b.y)));
            }
        found.Sort((p, q) => p.nearest.CompareTo(q.nearest));
        return found.ConvertAll(f => f.line);
    }

    /// <summary>How many pieces are recorded.</summary>
    public static int Count { get { lock (s_gate) return s_pieces.Count; } }
}
