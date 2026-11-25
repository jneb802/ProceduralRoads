using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// A road laid in a trench or along a ridge: over at least <see cref="MinLength"/> metres of it, the natural ground
/// <see cref="Side"/> metres out to each side is higher on both sides by more than <see cref="Depth"/> (a trench) or lower
/// on both sides by more than it (a ridge). Its sides are read straight across the road and on the two diagonals across
/// it, the worst of the three: a fill climbing a slope stands over the ground on both its diagonals while straight across
/// it the slope may still rise to one side. Never along the road, where any climb is higher ahead and lower behind. A shelf on a hillside, the ground up on one side and down on the other, is
/// neither, however deep its cut or fill; a stretch shorter than <see cref="MinLength"/> is a blip, not a defect. The one
/// test of it: the generator's re-route and staircase fallback, and what they keep, ask it.
/// </summary>
public static class RoadRidges
{
    /// <summary>How far out to each side of the road the natural ground is read, metres.</summary>
    public const float Side = 7f;
    /// <summary>How much higher (a trench) or lower (a ridge) the ground must stand on both sides, metres.</summary>
    public const float Depth = 4f;
    /// <summary>The shortest stretch that is a trench or a ridge, metres.</summary>
    public const float MinLength = 10f;
    /// <summary>A stretch carries on over a gap of the road shorter than this, metres.</summary>
    public const float Link = 3f;
    /// <summary>Spacing of the points read along a road, metres.</summary>
    public const float Spacing = 2f;

    public enum Kind { Trench, Ridge }

    /// <summary>One trench or ridge: its kind, its length along the road, the most the ground stands over (a trench) or
    /// under (a ridge) the road on its lower (higher) side, and the points read on it.</summary>
    public sealed class Stretch
    {
        public Kind Kind;
        public float Length, Worst;
        public readonly List<Vector2> Points = new();
    }

    /// <summary>The ground over the road on both sides (positive: a trench that deep) and under it on both sides (positive: a
    /// ridge that high) at <paramref name="at"/>, the road <paramref name="height"/> high and heading along
    /// <paramref name="heading"/>: the worst of straight across and the two diagonals.</summary>
    public static (float trench, float ridge) Across(Vector2 at, float height, Vector2 heading, Func<Vector2, float> ground)
    {
        Vector2 along = heading.normalized, across = new Vector2(-along.y, along.x);
        float trench = float.NegativeInfinity, ridge = float.NegativeInfinity;
        void Read(Vector2 side)
        {
            float left = ground(at + side * Side), right = ground(at - side * Side);
            trench = Mathf.Max(trench, Mathf.Min(left, right) - height);
            ridge = Mathf.Max(ridge, height - Mathf.Max(left, right));
        }
        Read(across);
        Read((across + along).normalized);
        Read((across - along).normalized);
        return (trench, ridge);
    }

    /// <summary>The trench or ridge the road builds at <paramref name="at"/>: as <see cref="Across"/>, but no deeper than its
    /// own cut or fill over the ground under it. A road at its ground along a crest or down a gully builds neither; one on fill
    /// along a crest, over its ground and both sides, builds a ridge.</summary>
    public static (float trench, float ridge) Built(Vector2 at, float height, Vector2 heading, Func<Vector2, float> ground)
    {
        var (trench, ridge) = Across(at, height, heading, ground);
        float under = ground(at);
        return (Mathf.Min(trench, under - height), Mathf.Min(ridge, height - under));
    }

    /// <summary>The trenches and ridges of at least <paramref name="minLength"/> metres along one road's points and
    /// heights, deeper than <paramref name="depth"/>; <paramref name="built"/>, only what the road builds (<see cref="Built"/>).</summary>
    public static List<Stretch> Find(IReadOnlyList<Vector2> points, IReadOnlyList<float> heights, Func<Vector2, float> ground,
        float minLength = MinLength, float depth = Depth, bool built = false)
    {
        var found = new List<Stretch>();
        int n = Mathf.Min(points.Count, heights.Count);
        if (n < 2) return found;
        Stretch? open = null;
        float openFrom = 0f, openTo = 0f, along = 0f, lastRead = float.NegativeInfinity;
        void Close()
        {
            if (open != null && openTo - openFrom >= minLength) { open.Length = openTo - openFrom; found.Add(open); }
            open = null;
        }
        for (int i = 0; i < n; i++)
        {
            if (i > 0) along += Vector2.Distance(points[i - 1], points[i]);
            if (i > 0 && i < n - 1 && along - lastRead < Spacing) continue;
            lastRead = along;
            Vector2 heading = points[Mathf.Min(n - 1, i + 1)] - points[Mathf.Max(0, i - 1)];
            if (heading.sqrMagnitude < 1e-6f) continue;
            var (trench, ridge) = built ? Built(points[i], heights[i], heading, ground) : Across(points[i], heights[i], heading, ground);
            Kind? kind = trench > depth ? Kind.Trench : ridge > depth ? Kind.Ridge : null;
            if (open != null)
            {
                bool gap = along - openTo > Link;
                if (kind == null ? gap : kind != open.Kind || gap) Close();
            }
            if (kind == null) continue;
            if (open == null) { open = new Stretch { Kind = kind.Value }; openFrom = along; }
            open.Points.Add(points[i]);
            open.Worst = Mathf.Max(open.Worst, kind == Kind.Trench ? trench : ridge);
            openTo = along;
        }
        Close();
        return found;
    }

    /// <summary>The trenches and ridges of a road's planned pieces (a ford painted on its bed is ground, not judged).</summary>
    public static List<Stretch> Find(IEnumerable<RoadSpatialGrid.PlannedPath> plans, Func<Vector2, float> ground)
    {
        var found = new List<Stretch>();
        foreach (var plan in plans)
            if (!plan.FollowTerrain) found.AddRange(Find(plan.Points, plan.Heights, ground));
        return found;
    }

    /// <summary>Metres of road in these stretches.</summary>
    public static float Metres(List<Stretch> stretches)
    {
        float metres = 0f;
        foreach (var s in stretches) metres += s.Length;
        return metres;
    }
}
