using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>One actor's disposable waypoint list. Built roads live in world persistence.</summary>
public sealed class ManualRoadDraft
{
    public const int MaxPoints = 32;
    public const int MaxAppends = 4096;
    private readonly List<Vector2> points = new();
    public IReadOnlyList<Vector2> Points => points.AsReadOnly();
    public static bool IsFinite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
    public static bool ValidPoint(Vector2 p) => IsFinite(p.x) && IsFinite(p.y) && p.sqrMagnitude < 10000f * 10000f;

    public static Vector2 ParsePoint(string x, string z)
    {
        if (!float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out float px) ||
            !float.TryParse(z, NumberStyles.Float, CultureInfo.InvariantCulture, out float pz) ||
            !ValidPoint(new Vector2(px, pz)))
            throw new ArgumentException("Coordinates must be finite X,Z values inside the playable world.");
        return new Vector2(px, pz);
    }
    public static List<Vector2> ParsePath(IEnumerable<string> pairs)
    {
        var draft = new ManualRoadDraft();
        foreach (string pair in pairs)
        {
            string[] xy = pair.Split(',');
            if (xy.Length != 2) throw new ArgumentException("Use X,Z pairs, for example: road_path 120,-450 180,-470");
            draft.Add(ParsePoint(xy[0], xy[1]));
        }
        Validate(draft.points);
        return new List<Vector2>(draft.points);
    }
    public static void Validate(IReadOnlyList<Vector2> path)
    {
        if (path.Count < 2 || path.Count > MaxPoints)
            throw new ArgumentException($"A road needs 2–{MaxPoints} waypoints.");
        for (int i = 0; i < path.Count; i++)
        {
            if (!ValidPoint(path[i])) throw new ArgumentException($"Waypoint {i + 1} is outside the playable world or not finite.");
            if (i > 0 && Vector2.Distance(path[i - 1], path[i]) < 1f)
                throw new ArgumentException($"Waypoints {i} and {i + 1} are less than one metre apart.");
        }
    }
    public void Add(Vector2 point)
    {
        if (points.Count >= MaxPoints) throw new ArgumentException($"At most {MaxPoints} marks are supported.");
        if (!ValidPoint(point)) throw new ArgumentException("The mark must be inside the playable world.");
        if (points.Count > 0 && Vector2.Distance(points[points.Count - 1], point) < 1f)
            throw new ArgumentException("This mark is less than one metre from the previous mark.");
        points.Add(point);
    }
    public bool Undo()
    {
        if (points.Count == 0) return false;
        points.RemoveAt(points.Count - 1); return true;
    }
    public void Clear() => points.Clear();
    public string Command => "road_path " + string.Join(" ", points.Select(p =>
        p.x.ToString("R", CultureInfo.InvariantCulture) + "," + p.y.ToString("R", CultureInfo.InvariantCulture)));
}
