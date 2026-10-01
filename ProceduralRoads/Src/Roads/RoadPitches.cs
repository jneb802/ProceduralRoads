using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Climbs as short steep pitches and short easy stretches, with variety,
/// instead of one steady grade at the cap the whole way. The shape follows
/// trail-building practice (a sustained grade, a short-pitch maximum, and how
/// often pitches may come); the numbers were chosen by pulling a cart up test
/// ramps: empty it barely slows at 50 %, loaded it falls to 1.9 m/s at 40 %
/// and 1.0 m/s at 50 %.
///
/// On each climb an ease of <see cref="EaseMin"/>..<see cref="EaseMax"/>
/// metres at <see cref="EaseGrade"/> comes every
/// <see cref="EaseEveryMin"/>..<see cref="EaseEveryMax"/> metres, lengths
/// varied by world-position noise; edges within <see cref="PitchLength"/> of
/// an ease may run up to <see cref="PitchGrade"/>, everything else keeps the
/// road's cap. The grade limiter then holds the eases and pushes their height
/// into the pitches beside them. The route search pays for climbing above the
/// sustained grade (<see cref="OverSustainedPrice"/>) rather than refusing it,
/// so gentle climbs are preferred and a clearly better steep line can still
/// win. A road that joins another ends on a level landing
/// (<see cref="JunctionLanding"/>). The fields are settable for tests.
/// </summary>
public static class RoadPitches
{
    /// <summary>Off, a climb is held to the road's cap alone. Settable for tests.</summary>
    internal static bool Enabled = true;
    internal static float PitchGrade = 0.5f;
    internal static float PitchLength = 8f;
    /// <summary>Climbing above this pays <see cref="PitchOverWeight"/> per metre per unit of grade over it.</summary>
    internal static float Sustained = 0.30f;
    internal static float EaseEveryMin = 12f;
    internal static float EaseEveryMax = 25f;
    internal static float EaseMin = 4f;
    internal static float EaseMax = 8f;
    internal static float EaseGrade = 0.09f;
    /// <summary>A run whose grade over <see cref="Window"/> metres stays above this is a climb.</summary>
    internal static float MinGrade = 0.12f;
    internal static float PitchOverWeight = 5f;
    internal const float Window = 20f;
    internal const float Slack = 0.8f;

    private static int s_climbsThatLostEases;
    /// <summary>Climbs that lost eases because even pitches the whole way between them could not
    /// make the height: the route search let through a climb too steep for rests. Should be 0.</summary>
    public static int ClimbsThatLostEases => s_climbsThatLostEases;
    public static void ResetCounters() => s_climbsThatLostEases = 0;

    /// <summary>The price of one search step over the sustained grade.</summary>
    public static float OverSustainedPrice(float slope, float distance) =>
        Enabled && PitchOverWeight > 0f && slope > Sustained ? PitchOverWeight * (slope - Sustained) * distance : 0f;

    /// <summary>
    /// The last metres of a road that joins another road are a level landing
    /// at the joined road's height, so the two meet level instead of one
    /// arriving tilted. The landing counts as an ease.
    /// </summary>
    internal static float JunctionLanding = 6f;
    public const float JunctionLandingGrade = 0.05f;

    /// <summary>Hold the edges within <paramref name="length"/> metres of a
    /// joined end to the landing grade; returns the (possibly new) array.</summary>
    public static float[] ApplyJunctionLanding(IReadOnlyList<Vector2> points, float[]? edgeGrades, float cap,
        bool atStart, bool atEnd, float length)
    {
        int n = points.Count;
        var result = edgeGrades ?? Filled(n, cap);
        var s = new float[n];
        for (int i = 1; i < n; i++) s[i] = s[i - 1] + Vector2.Distance(points[i - 1], points[i]);
        for (int i = 1; i < n; i++)
        {
            bool nearStart = atStart && s[i - 1] < length;
            bool nearEnd = atEnd && s[n - 1] - s[i] < length;
            if (nearStart || nearEnd) result[i] = Mathf.Min(result[i], JunctionLandingGrade);
        }
        return result;
    }

    /// <summary>The cap to hand the grade limiter with these edge grades: the
    /// pitch grade when a pitch raised any edge above the road's cap.</summary>
    public static float LimitCap(float cap, float[]? edgeGrades)
    {
        if (edgeGrades == null) return cap;
        float top = cap;
        foreach (var g in edgeGrades) top = Mathf.Max(top, g);
        return top;
    }

    /// <summary>Per-edge grade limits with pitches and eases placed, or null
    /// when no climb takes an ease (the caller's limits stand).</summary>
    public static float[]? PlacePitches(IReadOnlyList<Vector2> points, IReadOnlyList<float> heights, float cap, float[]? edgeGrades,
        float? pitchGrade = null, float? pitchLength = null, float? everyMin = null, float? everyMax = null,
        float? easeMin = null, float? easeMax = null, float? easeGrade = null, float? minGrade = null,
        IReadOnlyList<float>? ground = null)
    {
        float pg = pitchGrade ?? PitchGrade, pl = pitchLength ?? PitchLength, eMin = everyMin ?? EaseEveryMin, eMax = everyMax ?? EaseEveryMax;
        float lMin = easeMin ?? EaseMin, lMax = easeMax ?? EaseMax, eg = easeGrade ?? EaseGrade, climbing = minGrade ?? MinGrade;
        int n = Mathf.Min(points.Count, heights.Count);
        if (n < 3 || cap <= 0f) return null;
        var s = new float[n];
        for (int i = 1; i < n; i++) s[i] = s[i - 1] + Vector2.Distance(points[i - 1], points[i]);
        var dir = ClimbDirections(s, heights, n, climbing);
        float[]? result = null;
        int a = 0;
        while (a < n)
        {
            if (dir[a] == 0) { a++; continue; }
            int b = a;
            while (b + 1 < n && dir[b + 1] == dir[a]) b++;
            float rise = Mathf.Abs(heights[b] - heights[a]);
            // A switchback landing (or any limit already at an ease's grade) IS
            // an ease. Collect them first; the next ease is spaced from their
            // end, and the edges beside them may pitch.
            var fixedEases = new List<(float from, float to)>();
            if (edgeGrades != null)
                for (int i = a + 1; i <= b; i++)
                {
                    if (edgeGrades[i] > eg + 0.02f) continue;
                    int j = i; while (j + 1 <= b && edgeGrades[j + 1] <= eg + 0.02f) j++;
                    fixedEases.Add((s[i - 1], s[j])); i = j;
                }
            // Lay eases along the climb, then drop the last until the climb can
            // still make its height: eases at their grade, edges beside them at
            // the pitch grade, the rest at the cap.
            var eases = new List<(float from, float to)>();
            float at = s[a];
            int nextFixed = 0;
            while (true)
            {
                while (nextFixed < fixedEases.Count && fixedEases[nextFixed].to <= at) nextFixed++;
                float n1 = RoadEarthworkNoise.Perlin01(points[FindIndex(s, at, a, b)].x * 0.037f + 11.1f, points[FindIndex(s, at, a, b)].y * 0.037f - 7.3f);
                float every = Mathf.Lerp(eMin, eMax, n1);
                float from = at + every;
                float n2 = RoadEarthworkNoise.Perlin01(points[FindIndex(s, from, a, b)].x * 0.051f - 3.7f, points[FindIndex(s, from, a, b)].y * 0.051f + 19.9f);
                float easeLen = Mathf.Lerp(lMin, lMax, n2);
                // With the ground given, put the ease where the natural ground
                // is flattest within the spacing window, so it sits on a bench
                // instead of being built out of the slope.
                if (ground != null)
                {
                    float best = float.MaxValue, bestFrom = from;
                    for (float c = at + eMin; c <= at + eMax && c + easeLen <= s[b]; c += 1f)
                    {
                        int i0 = FindIndex(s, c, a, b), i1 = FindIndex(s, c + easeLen, a, b);
                        if (i1 <= i0) continue;
                        float g = Mathf.Abs(ground[i1] - ground[i0]) / Mathf.Max(0.1f, s[i1] - s[i0]);
                        if (g < best - 1e-4f) { best = g; bestFrom = c; }
                    }
                    from = bestFrom;
                }
                float to = from + easeLen;
                // A landing comes before this ease would end: it is the ease.
                if (nextFixed < fixedEases.Count && fixedEases[nextFixed].from < to)
                {
                    at = Mathf.Max(at, fixedEases[nextFixed].to); nextFixed++;
                    continue;
                }
                if (to > s[b] - eMin * 0.5f) break;
                eases.Add((from, to));
                at = to;
            }
            List<(float from, float to)> All() { var l = new List<(float, float)>(fixedEases); l.AddRange(eases); return l; }
            float Room(float pitchLen)
            {
                float room = 0f; var all = All();
                for (int i = a + 1; i <= b; i++) room += (s[i] - s[i - 1]) * EdgeLimit(s[i - 1], s[i], all, cap, pg, pitchLen, eg);
                return room;
            }
            // Rests are never traded away for height; more steep sections are added instead. A
            // climb short of room first
            // gets LONGER pitches beside its eases, up to the whole gap between them; only a climb
            // that cannot make its height even then (and without the slack) loses eases, and that
            // is counted: the route search should not have produced it.
            float climbPl = pl;
            if (eases.Count > 0)
            {
                float longest = Mathf.Max(pl, eMax);
                while (rise > Room(climbPl) * Slack && climbPl < longest) climbPl = Mathf.Min(longest, climbPl + 1f);
                if (rise > Room(climbPl) * Slack && rise <= Room(climbPl)) { /* tight, but buildable: keep every ease */ }
                else
                {
                    int before = eases.Count;
                    while (eases.Count > 0 && rise > Room(climbPl) * Slack) eases.RemoveAt(eases.Count - 1);
                    if (eases.Count < before) System.Threading.Interlocked.Increment(ref s_climbsThatLostEases);
                }
            }
            if (eases.Count > 0 || fixedEases.Count > 0)
            {
                result ??= edgeGrades != null ? (float[])edgeGrades.Clone() : Filled(n, cap);
                var all = All();
                for (int i = a + 1; i <= b; i++)
                {
                    float lim = EdgeLimit(s[i - 1], s[i], all, cap, pg, climbPl, eg);
                    // A landing or other limit already below stands; a pitch may rise above the cap.
                    result[i] = lim < cap ? Mathf.Min(result[i], lim) : (result[i] >= cap - 1e-4f ? lim : result[i]);
                }
            }
            a = b + 1;
        }
        return result;
    }

    private static float EdgeLimit(float s0, float s1, List<(float from, float to)> eases, float cap, float pitch, float pitchLength, float ease)
    {
        // A long pitch beside an earlier rest can overlap the next rest.
        // Check every rest before assigning any pitch, both when measuring
        // available climb and when writing the per-edge limits.
        foreach (var (from, to) in eases)
            if (s1 > from && s0 < to) return ease;
        foreach (var (from, to) in eases)
            if (s1 > from - pitchLength && s0 < to + pitchLength) return pitch;
        return cap;
    }

    private static int FindIndex(float[] s, float at, int a, int b)
    {
        int i = a;
        while (i < b && s[i] < at) i++;
        return i;
    }

    private static int[] ClimbDirections(float[] s, IReadOnlyList<float> heights, int n, float climbing)
    {
        var dir = new int[n];
        int lo = 0, hi = 0;
        for (int i = 0; i < n; i++)
        {
            while (lo < i && s[i] - s[lo] > Window * 0.5f) lo++;
            while (hi < n - 1 && s[hi + 1] - s[i] <= Window * 0.5f) hi++;
            float run = s[hi] - s[lo];
            if (run < Window * 0.5f) continue;
            float g = (heights[hi] - heights[lo]) / run;
            dir[i] = g > climbing ? 1 : g < -climbing ? -1 : 0;
        }
        return dir;
    }

    private static float[] Filled(int n, float value)
    {
        var f = new float[n];
        for (int i = 0; i < n; i++) f[i] = value;
        return f;
    }
}
