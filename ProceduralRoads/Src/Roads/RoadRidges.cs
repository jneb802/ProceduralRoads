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
    /// <summary>How much higher (a trench) or lower (a ridge) the ground must stand on both sides, metres: a player's height
    /// (<see cref="RoadRockPolicy.PlayerHeight"/>), past which a road reads as a causeway a player cannot step off, or a cut
    /// he cannot see out of. A 4 m depth let a road ride ground 2.6 to 3.2 m under it on both sides for more than 30 m.</summary>
    public const float Depth = RoadRockPolicy.PlayerHeight;
    /// <summary>The shortest stretch that is a trench or a ridge, metres.</summary>
    public const float MinLength = 10f;
    /// <summary>A trench (the ground more than <see cref="Depth"/> over the road on both sides for <see cref="MinLength"/> or
    /// more) is one only where it is this long or longer, metres, or <see cref="TrenchMinDepth"/> deep or deeper: a shorter,
    /// shallower cut is a notch, and is built. A ridge is held to <see cref="MinLength"/> alone.</summary>
    public const float TrenchMinLength = 20f;
    /// <summary>A trench at least this deep (its <see cref="Stretch.Worst"/>), metres, is one at any length from
    /// <see cref="MinLength"/> (<see cref="TrenchMinLength"/>).</summary>
    public const float TrenchMinDepth = 4f;

    /// <summary>A biome's own ridge and trench values: each biome's is defined with its other overrides beside the globals,
    /// RoadRules (its Ridge, read through RoadRules.For; the owner's rule of 13 Nov 2026).</summary>
    public sealed class Override
    {
        /// <summary>The depth a stretch shorter than <see cref="Long"/> is judged at, metres (instead of <see cref="Depth"/>).</summary>
        public readonly float Depth;
        /// <summary>How long a stretch must be to be judged at the global <see cref="RoadRidges.Depth"/>, metres, a ridge as a
        /// trench.</summary>
        public readonly float Long;
        /// <summary>How far along the road from a viaduct's end a stretch is judged by this override, metres
        /// (<see cref="Judged"/>); null: every stretch; infinity: every stretch of a road with a viaduct of its own. A stretch
        /// further from every one is judged by <see cref="Counts(Stretch)"/>.</summary>
        public readonly float? ViaductReach;
        public Override(float depth, float longLength, float? viaductReach = null) { Depth = depth; Long = longLength; ViaductReach = viaductReach; }
    }

    /// <summary>The viaduct ends a road's stretches are measured from (<see cref="Override.ViaductReach"/>): the bank ends of
    /// the road's own viaducts, its crossings (one it rides among them), and those of the viaducts already laid in the network.</summary>
    public sealed class Viaducts
    {
        public readonly IReadOnlyList<Vector2> Own, Laid;
        public Viaducts(IReadOnlyList<Vector2> own, IReadOnlyList<Vector2> laid) { Own = own; Laid = laid; }
    }

    /// <summary>Whether a stretch in a biome with an override is judged by it (<see cref="Override.ViaductReach"/>): with no
    /// reach, or no viaducts read (<paramref name="viaducts"/> null, the offline tools'), always; with an infinite one, where
    /// the road has a viaduct of its own; else where a viaduct's end lies within the reach of it, travelled along the road
    /// (<paramref name="along"/>, the road's points at their distance along it, as Find places its stretches): from the
    /// road's point nearest the end, plus that point's distance to the end.</summary>
    public static bool Judged(Stretch stretch, Override biome, Viaducts? viaducts, IReadOnlyList<(Vector2 at, float along)> along)
    {
        if (biome.ViaductReach is not float reach || viaducts == null) return true;
        if (float.IsPositiveInfinity(reach)) return viaducts.Own.Count > 0;
        bool Near(IReadOnlyList<Vector2> ends)
        {
            foreach (Vector2 end in ends)
            {
                float gap = float.PositiveInfinity, at = 0f;
                foreach (var (p, a) in along)
                {
                    float d = Vector2.Distance(p, end);
                    if (d < gap) { gap = d; at = a; }
                }
                float travelled = gap + (at < stretch.From ? stretch.From - at : at > stretch.To ? at - stretch.To : 0f);
                if (travelled <= reach) return true;
            }
            return false;
        }
        return Near(viaducts.Own) || Near(viaducts.Laid);
    }

    /// <summary>Whether a stretch of <see cref="MinLength"/> or more is a trench or ridge: every ridge, and a trench
    /// <see cref="TrenchMinLength"/> long or <see cref="TrenchMinDepth"/> deep. Where no biome overrides it; a stretch in a
    /// biome that does is judged by <see cref="Counts(Stretch, Override, IReadOnlyList{Stretch})"/>.</summary>
    public static bool Counts(Stretch stretch) =>
        stretch.Kind == Kind.Ridge || stretch.Length >= TrenchMinLength || stretch.Worst >= TrenchMinDepth;

    /// <summary>Whether a stretch of <see cref="MinLength"/> or more in a biome with an override is a trench or ridge: one
    /// <see cref="Override.Long"/> long, or one that holds a stretch of its kind deeper than <see cref="Override.Depth"/> for
    /// <see cref="MinLength"/> or more (<paramref name="deep"/>, the road's stretches judged at that depth). A shorter,
    /// shallower one is built.</summary>
    public static bool Counts(Stretch stretch, Override biome, IReadOnlyList<Stretch> deep)
    {
        if (stretch.Length >= biome.Long) return true;
        foreach (Stretch d in deep)
            if (d.Kind == stretch.Kind && d.From < stretch.To && d.To > stretch.From) return true;
        return false;
    }

    /// <summary>A stretch carries on over a gap of the road shorter than this, metres.</summary>
    public const float Link = 3f;
    /// <summary>Spacing of the points read along a road, metres: none, every planned point (about a metre apart) is read,
    /// so a stretch is measured to the metre the road stands on it (read every 2 m, its ends were placed up to a metre off
    /// either way, and a crest ridden for 11 m read as under 10).</summary>
    public const float Spacing = 0f;

    public enum Kind { Trench, Ridge }

    /// <summary>One trench or ridge: its kind, its length along the road, the most the ground stands over (a trench) or
    /// under (a ridge) the road on its lower (higher) side, and the points read on it.</summary>
    public sealed class Stretch
    {
        public Kind Kind;
        public float Length, Worst;
        public readonly List<Vector2> Points = new();
        /// <summary>Where along its road it runs, metres from the road's first point: from half way back to the reading
        /// before its first to half way on to the reading after its last.</summary>
        public float From, To;
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
        // Each reading stands for the road half way to the readings beside it: a stretch runs from half way back to the
        // reading before its first to half way on to the reading after its last (the road's own ends where none), so a
        // ridge is measured at its length, not short by up to a reading's spacing at each end.
        float openFrom = 0f, openTo = 0f, along = 0f, lastRead = float.NegativeInfinity, previous = float.NaN, afterLast = float.NaN;
        void Close()
        {
            float end = float.IsNaN(afterLast) ? openTo : (openTo + afterLast) / 2f;
            if (open != null && end - openFrom >= minLength) { open.Length = end - openFrom; open.From = openFrom; open.To = end; found.Add(open); }
            open = null;
        }
        for (int i = 0; i < n; i++)
        {
            if (i > 0) along += Vector2.Distance(points[i - 1], points[i]);
            if (i > 0 && i < n - 1 && along - lastRead < Spacing) continue;
            lastRead = along;
            Vector2 heading = points[Mathf.Min(n - 1, i + 1)] - points[Mathf.Max(0, i - 1)];
            if (heading.sqrMagnitude < 1e-6f) continue;
            float before = previous;
            previous = along;
            var (trench, ridge) = built ? Built(points[i], heights[i], heading, ground) : Across(points[i], heights[i], heading, ground);
            Kind? kind = trench > depth ? Kind.Trench : ridge > depth ? Kind.Ridge : null;
            if (open != null)
            {
                bool gap = along - openTo > Link;
                if (float.IsNaN(afterLast) && (kind == null || kind != open.Kind)) afterLast = along;
                if (kind == null ? gap : kind != open.Kind || gap) Close();
            }
            if (kind == null) continue;
            if (open == null) { open = new Stretch { Kind = kind.Value }; openFrom = float.IsNaN(before) ? along : (before + along) / 2f; }
            open.Points.Add(points[i]);
            open.Worst = Mathf.Max(open.Worst, kind == Kind.Trench ? trench : ridge);
            openTo = along;
            afterLast = float.NaN;
        }
        Close();
        return found;
    }

    /// <summary>The trenches and ridges of a road's planned pieces (a ford painted on its bed is ground, not judged), read
    /// along the road as one: the pieces in their order, each joined to the one before where it starts within
    /// <see cref="Link"/> of where that one ends, their stretches placed along the joined road and one carried on into the
    /// next where no more than <see cref="Link"/> of road lies between them. A stretch is measured over the road it covers,
    /// the road between two it joins counted once; a piece that does not start where the one before ends (a carried end,
    /// another leg) starts the road again, placed further along than <see cref="Link"/> so no stretch of one run overlaps one of
    /// another. A stretch with a point in a biome that overrides the rule (<paramref name="biomeAt"/>,
    /// RoadRules.For(...).Ridge of its first such point; null: no biome read) is judged by its override
    /// (<see cref="Counts(Stretch, Override, IReadOnlyList{Stretch})"/>), every other by <see cref="Counts(Stretch)"/>.</summary>
    public static List<Stretch> Find(IEnumerable<RoadSpatialGrid.PlannedPath> plans, Func<Vector2, float> ground, Func<Vector2, Heightmap.Biome>? biomeAt = null) =>
        Find(plans, ground, biomeAt, null);

    /// <summary>As <see cref="Find(IEnumerable{RoadSpatialGrid.PlannedPath}, Func{Vector2, float}, Func{Vector2, Heightmap.Biome}?)"/>,
    /// a stretch in a biome with an override judged by it only where <see cref="Judged"/> says so, from the road's
    /// <paramref name="viaducts"/> (generation's, RoadNetworkGenerator.Ridges).</summary>
    public static List<Stretch> Find(IEnumerable<RoadSpatialGrid.PlannedPath> plans, Func<Vector2, float> ground, Func<Vector2, Heightmap.Biome>? biomeAt,
        Viaducts? viaducts)
    {
        var pieces = plans as IReadOnlyList<RoadSpatialGrid.PlannedPath> ?? new List<RoadSpatialGrid.PlannedPath>(plans);
        var found = new List<Stretch>();
        Dictionary<float, List<Stretch>>? deep = null;
        var along = new List<(Vector2, float)>();
        foreach (Stretch stretch in Join(pieces, ground, Depth, along))
        {
            Override? biome = null;
            if (biomeAt != null)
                foreach (Vector2 p in stretch.Points)
                    if ((biome = RoadRules.For(biomeAt(p)).Ridge) != null) break;
            if (biome != null && !Judged(stretch, biome, viaducts, along)) biome = null;
            if (biome == null) { if (Counts(stretch)) found.Add(stretch); continue; }
            List<Stretch>? deeper = null;
            if (stretch.Length < biome.Long && !(deep ??= new()).TryGetValue(biome.Depth, out deeper))
                deep[biome.Depth] = deeper = Join(pieces, ground, biome.Depth);
            if (Counts(stretch, biome, deeper ?? new List<Stretch>())) found.Add(stretch);
        }
        return found;
    }

    /// <summary>The stretches of <see cref="MinLength"/> or more deeper than <paramref name="depth"/> along a road's planned
    /// pieces read as one (<see cref="Find(IEnumerable{RoadSpatialGrid.PlannedPath}, Func{Vector2, float}, Func{Vector2, Heightmap.Biome}?)"/>),
    /// before they are judged.</summary>
    private static List<Stretch> Join(IReadOnlyList<RoadSpatialGrid.PlannedPath> plans, Func<Vector2, float> ground, float depth,
        List<(Vector2 at, float along)>? along = null)
    {
        var found = new List<Stretch>();
        Stretch? open = null;
        float offset = 0f;
        Vector2? lastEnd = null;
        void Close()
        {
            if (open != null && open.To - open.From >= MinLength) { open.Length = open.To - open.From; found.Add(open); }
            open = null;
        }
        foreach (var plan in plans)
        {
            int n = Mathf.Min(plan.Points.Count, plan.Heights.Count);
            if (plan.FollowTerrain || n == 0) { Close(); lastEnd = null; continue; }
            if (lastEnd is Vector2 end && Vector2.Distance(end, plan.Points[0]) <= Link) offset += Vector2.Distance(end, plan.Points[0]);
            // A road started again (a carried end, another leg, past a deck or a ford) goes on further along than Link, so its
            // stretches never share positions with the run before it: a stretch is judged against its own run (Counts).
            else { Close(); offset += 2f * Link; }
            foreach (Stretch run in Find(plan.Points, plan.Heights, ground, minLength: 0f, depth: depth))
            {
                float from = offset + run.From, to = offset + run.To;
                if (open != null && (run.Kind != open.Kind || from - open.To > Link)) Close();
                if (open == null) { open = new Stretch { Kind = run.Kind, From = from, To = to }; }
                open.Points.AddRange(run.Points);
                open.Worst = Mathf.Max(open.Worst, run.Worst);
                open.To = Mathf.Max(open.To, to);
            }
            for (int i = 0; i < n; i++)
            {
                if (i > 0) offset += Vector2.Distance(plan.Points[i - 1], plan.Points[i]);
                along?.Add((plan.Points[i], offset));
            }
            lastEnd = plan.Points[n - 1];
        }
        Close();
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
