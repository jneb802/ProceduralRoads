using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Ordered centerline for one generated road. This is separate from the terrain
/// spatial grid so actor routes can follow a road from start to end.
/// </summary>
public sealed class RoadRoute
{
    public int Index { get; }
    public string Label { get; }
    public float Width { get; }
    public List<Vector3> Points { get; }
    public float Length { get; }

    public RoadRoute(int index, string label, float width, List<Vector3> points)
    {
        Index = index;
        Label = label;
        Width = width;
        Points = points;
        Length = CalculateLength(points);
    }

    public static RoadRoute FromWaypoints(int index, string label, float width, List<Vector2> waypoints, WorldGenerator worldGen)
    {
        float spacing = Mathf.Max(2f, width);
        List<Vector2> centerline = SplinePath(waypoints, spacing);
        List<Vector3> points = new List<Vector3>(centerline.Count);

        for (int i = 0; i < centerline.Count; i++)
        {
            Vector2 point = centerline[i];
            float height = BiomeBlendedHeight.GetBlendedHeight(point.x, point.y, worldGen);
            points.Add(new Vector3(point.x, height, point.y));
        }

        return new RoadRoute(index, label, width, points);
    }

    public List<Vector3> Resample(float spacing, bool reverse)
    {
        float clampedSpacing = Mathf.Max(1f, spacing);
        List<Vector3> source = reverse ? ReversedPoints() : Points;
        List<Vector3> result = new List<Vector3>();

        if (source.Count == 0)
        {
            return result;
        }

        result.Add(source[0]);

        float distanceSinceLast = 0f;
        for (int i = 1; i < source.Count; i++)
        {
            Vector3 segmentStart = source[i - 1];
            Vector3 segmentEnd = source[i];
            float segmentLength = HorizontalDistance(segmentStart, segmentEnd);

            if (segmentLength <= 0.001f)
            {
                continue;
            }

            float consumed = 0f;
            while (distanceSinceLast + (segmentLength - consumed) >= clampedSpacing)
            {
                float needed = clampedSpacing - distanceSinceLast;
                consumed += needed;
                float t = Mathf.Clamp01(consumed / segmentLength);
                Vector3 point = Vector3.Lerp(segmentStart, segmentEnd, t);
                result.Add(point);
                distanceSinceLast = 0f;
            }

            distanceSinceLast += segmentLength - consumed;
        }

        Vector3 finalPoint = source[source.Count - 1];
        if (HorizontalDistance(result[result.Count - 1], finalPoint) > 0.1f)
        {
            result.Add(finalPoint);
        }

        return result;
    }

    private List<Vector3> ReversedPoints()
    {
        List<Vector3> reversed = new List<Vector3>(Points);
        reversed.Reverse();
        return reversed;
    }

    private static float CalculateLength(List<Vector3> points)
    {
        float length = 0f;
        for (int i = 0; i < points.Count - 1; i++)
        {
            length += HorizontalDistance(points[i], points[i + 1]);
        }

        return length;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private static List<Vector2> SplinePath(List<Vector2> waypoints, float segmentLength)
    {
        if (waypoints.Count < 2)
        {
            return new List<Vector2>(waypoints);
        }

        List<Vector2> result = new List<Vector2>();

        for (int i = 0; i < waypoints.Count - 1; i++)
        {
            Vector2 p0 = waypoints[Mathf.Max(0, i - 1)];
            Vector2 p1 = waypoints[i];
            Vector2 p2 = waypoints[i + 1];
            Vector2 p3 = waypoints[Mathf.Min(waypoints.Count - 1, i + 2)];

            float segmentDistance = Vector2.Distance(p1, p2);
            int steps = Mathf.Max(1, Mathf.CeilToInt(segmentDistance / segmentLength));

            for (int step = 0; step < steps; step++)
            {
                float t = step / (float)steps;
                result.Add(CatmullRom(p0, p1, p2, p3, t));
            }
        }

        result.Add(waypoints[waypoints.Count - 1]);
        return result;
    }

    private static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        return 0.5f * (
            (2f * p1) +
            (-p0 + p2) * t +
            (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
            (-p0 + 3f * p1 - 3f * p2 + p3) * t3
        );
    }
}
