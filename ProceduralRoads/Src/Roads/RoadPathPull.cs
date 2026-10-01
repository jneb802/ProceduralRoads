using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Straighten the search's grid path before it is shaped. The search moves in
/// 8-18 m cell steps, so a gentle curve arrives as a staircase of tiny turns,
/// and switchback shaping has to fit and check every one of them: measured,
/// "turns too close to fit their curves" was the largest reason a found road
/// was refused, and once roads could contour it doubled (291 to 588). Galin et al. 2010 reach the same place from the other
/// side: an 8-direction grid path goes straight up a slope, and only more
/// directions let it follow the contour.
///
/// From each waypoint this keeps the furthest later waypoint a straight line
/// can reach, within <see cref="MaxPull"/> metres, when that line is:
///  - dry, off every protected site, and within the search's own grade limit
///    between 2 m samples, and
///  - NO STEEPER than the path it replaces: its total rise and fall per metre
///    is at most <see cref="Steepen"/> times the replaced path's, or under
///    <see cref="Floor"/>. A contouring zigzag on a slope is kept; a staircase
///    of cells across gentle ground is straightened.
/// Both ends are held.
/// </summary>
public static class RoadPathPull
{
    /// <summary>Settable for tests.</summary>
    internal static bool Enabled = true;
    internal static float MaxPull = 48f;
    /// <summary>
    /// A straight line may only replace waypoints that all lie within this
    /// many metres of it. The pull exists to remove the search's 8 m
    /// staircase, a few metres off any line; with no tolerance it also
    /// flattened every bend on gentle ground, so the meander's bends never
    /// reached the road. 0 is no tolerance. Settable for tests.
    /// </summary>
    internal static float Tolerance = 5f;
    internal const float Steepen = 1.15f;
    internal const float Floor = 0.08f;
    private const float Sample = 2f;

    public static List<Vector2> Pull(List<Vector2> path, float width, Func<Vector2, float> ground,
        Func<Vector2, bool> wet, float gradeLimit, float? maxPull = null, Func<Vector2, bool>? anchor = null)
    {
        if (path.Count < 3) return path;
        float reach = maxPull ?? MaxPull;
        var result = new List<Vector2> { path[0] };
        int i = 0;
        while (i < path.Count - 1)
        {
            int best = i + 1;
            for (int j = i + 2; j < path.Count; j++)
            {
                if (Vector2.Distance(path[i], path[j]) > reach) break;
                // An anchor (a waypoint snapped onto a road already built) is
                // never skipped: pulling past it would leave the road beside
                // the one it was put on.
                if (anchor != null && anchor(path[j - 1])) break;
                if (Admissible(path, i, j, width, ground, wet, gradeLimit)) best = j;
            }
            result.Add(path[best]);
            i = best;
        }
        return result;
    }

    /// <summary>Total rise and fall per metre along a polyline, sampled every 2 m,
    /// with the steepest single sampled step. Null when a sample is wet.</summary>
    private static (float perMetre, float steepest)? Profile(IReadOnlyList<Vector2> line, Func<Vector2, float> ground, Func<Vector2, bool> wet)
    {
        float travelled = 0f, moved = 0f, steepest = 0f;
        Vector2 prev = line[0]; float prevH = ground(prev);
        for (int s = 1; s < line.Count; s++)
        {
            Vector2 a = line[s - 1], b = line[s];
            float len = Vector2.Distance(a, b);
            int n = Mathf.Max(1, Mathf.CeilToInt(len / Sample));
            for (int k = 1; k <= n; k++)
            {
                Vector2 p = a + (b - a) * (k / (float)n);
                if (wet(p)) return null;
                float h = ground(p), d = Vector2.Distance(prev, p);
                if (d > 1e-4f) steepest = Mathf.Max(steepest, Mathf.Abs(h - prevH) / d);
                moved += Mathf.Abs(h - prevH); travelled += d;
                prev = p; prevH = h;
            }
        }
        return (travelled > 0f ? moved / travelled : 0f, steepest);
    }

    /// <summary>Every waypoint between i and j lies within <paramref name="tolerance"/> of the line i-j.</summary>
    internal static bool WithinTolerance(List<Vector2> path, int i, int j, float tolerance)
    {
        Vector2 a = path[i], d = path[j] - a;
        float len = d.magnitude;
        if (len < 1e-4f) return true;
        for (int k = i + 1; k < j; k++)
        {
            Vector2 v = path[k] - a;
            if (Mathf.Abs(v.x * d.y - v.y * d.x) / len > tolerance) return false;
        }
        return true;
    }

    private static bool Admissible(List<Vector2> path, int i, int j, float width,
        Func<Vector2, float> ground, Func<Vector2, bool> wet, float gradeLimit)
    {
        if (RoadSiteProtection.BlocksSegment(path[i], path[j], width * 0.5f + 2f, null, null)) return false;
        if (Tolerance > 0f && !WithinTolerance(path, i, j, Tolerance)) return false;
        var line = Profile(new[] { path[i], path[j] }, ground, wet);
        if (line == null || line.Value.steepest > gradeLimit) return false;
        var replaced = Profile(path.GetRange(i, j - i + 1), ground, wet);
        float limit = Mathf.Max(Floor, (replaced?.perMetre ?? 0f) * Steepen);
        return line.Value.perMetre <= limit;
    }
}
