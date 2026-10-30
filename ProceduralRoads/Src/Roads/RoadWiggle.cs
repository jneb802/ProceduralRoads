using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// A gentle side-to-side sway put into a road's straights after the search,
/// before its bends are shaped. The meander bends the route, but the search
/// moves on an 8 m grid in a few fixed headings, so a small cost ripple never
/// bends a line inside one heading; the sway has to be laid on the road itself.
///
/// The offset across the road is layered value noise along the road's length
/// (97, 41 and 17 m waves, weighted 0.5/0.33/0.17), phased by where the road
/// starts, so no two roads sway alike and nothing repeats: a repeating
/// period is noticed. It is at most <see cref="Amplitude"/> metres,
/// scaled by the biome (the meander's table: none in mountains or Mistlands),
/// eased on climbs (full up to 0.12 grade, then down to <see cref="SlopeFloor"/>
/// of it at the road's grade cap, never to nothing), faded in over the first
/// and last 24 m, and wherever the swayed point would be wet or inside a site. On a sidehill
/// it follows the hillside's contour instead of crossing it (<see cref="FollowContour"/>). The shaped road
/// then passes every check any road does; if it is refused or moves more earth, the road is
/// planned again with the sway held round the stretch at fault (<see cref="HeldAt"/>), and
/// without the sway where that fails too. Settable for tests; 0 is no sway.
/// </summary>
public static class RoadWiggle
{
    internal static float Amplitude = 10f;
    internal static bool Enabled => Amplitude > 0f;

    /// <summary>Spacing of the swayed waypoints, metres.</summary>
    public const float Spacing = 12f;
    /// <summary>Distance from each end over which the sway fades in, metres.</summary>
    public const float EndFade = 24f;
    /// <summary>Distance over which the sway fades back in after a stretch shared with another road, metres.</summary>
    public const float ShareFade = 16f;
    /// <summary>Where the road being planned keeps its plain line, on this thread: within <see cref="HoldRadius"/>
    /// of each point the sway is held, faded back in over <see cref="ShareFade"/> beyond it. Set by the planner
    /// round the stretches where a swayed plan moved more earth or was refused (RoadSpatialGrid.PlanRoadPath).</summary>
    [ThreadStatic] internal static List<Vector2>? HeldAt;
    /// <summary>Radius round a held point within which the sway is held, metres.</summary>
    public const float HoldRadius = 24f;
    /// <summary>Times a refused or earth-moving swayed plan is planned again with more of its sway held.</summary>
    public const int HoldRounds = 3;
    /// <summary>Ground grade along the road up to which the sway is at full strength; above it
    /// the sway eases down to <see cref="SlopeFloor"/> at the grade cap. Validation switch
    /// WIGGLE_FLAT.</summary>
    public static readonly float Flat = DebugSwitches.Number("WIGGLE_FLAT", 0.12f, 0.02f, 1f);

    /// <summary>The share of the sway kept at the road's grade cap, and beyond it: a reduced
    /// sway on slopes instead of switching it off.
    /// The sway used to fade out completely by <see cref="Flat"/>, which left roads dead
    /// straight on every steep slope (ground falling 56.0, 52.6, 50.5, 47.9 every 10 m, about
    /// 0.27 grade).</summary>
    public const float SlopeFloor = 0.4f;

    /// <summary>
    /// The share of the sway kept on ground of this grade: 1 up to <see cref="Flat"/>, then
    /// falling in a straight line to <see cref="SlopeFloor"/> at <paramref name="cap"/>, and
    /// <see cref="SlopeFloor"/> beyond it. Never zero. A cap of 0 means no cap and takes the
    /// default one.
    /// </summary>
    public static float GradeFactor(float grade, float cap)
    {
        if (cap <= 0f) cap = RoadConstants.DefaultMaxRoadGrade;
        grade = Mathf.Abs(grade);
        if (grade <= Flat) return 1f;
        if (cap <= Flat || grade >= cap) return SlopeFloor;
        return Mathf.Lerp(1f, SlopeFloor, (grade - Flat) / (cap - Flat));
    }

    private static readonly (float wave, float weight)[] Octaves = { (97f, 0.5f), (41f, 0.33f), (17f, 0.17f) };

    /// <summary>The sway in [-1,1] at a distance along a road with this phase.</summary>
    internal static float Sway(float along, float phase)
    {
        float sum = 0f;
        for (int i = 0; i < Octaves.Length; i++)
            sum += Octaves[i].weight * RoadPathfinder.ValueNoise(along / Octaves[i].wave + phase * (i + 1), 17.3f * i + phase);
        // An average of octaves sits near the middle (measured: about +/-0.2
        // typical), so stretch it to use the range; the ends are clamped.
        return Mathf.Clamp((2f * sum - 1f) * Stretch, -1f, 1f);
    }

    /// <summary>How much the averaged octaves are stretched so a typical sway
    /// reaches about half the amplitude.</summary>
    public const float Stretch = 2.5f;

    /// <summary>
    /// The path, resampled every <see cref="Spacing"/> metres and swayed
    /// sideways; the ends stay where they are.
    /// </summary>
    public static List<Vector2> Apply(List<Vector2> path, Func<Vector2, float> ground, Func<Vector2, bool> wet,
        Func<Vector2, Heightmap.Biome> biome, float width, Func<Vector2, bool>? onRoad = null)
    {
        if (!Enabled || path.Count < 2) return path;
        var even = Resample(path, Spacing);
        if (even.Count < 4) return path;
        float total = (even.Count - 1) * Spacing;
        if (total < 2f * EndFade + Spacing) return path;
        // Phase from where the road starts: deterministic, and different per road.
        float seed = path[0].x * 0.0137f + path[0].y * 0.0291f;
        float phase = seed - 97f * Mathf.Floor(seed / 97f);
        // No sway where the road runs on or beside a road already built, faded
        // back in over ShareFade: a road that shares another's line must stay
        // on it, not sway beside it, or corners are drawn twice. Nor round a
        // spot the sway is held at (HeldAt).
        List<Vector2>? held = HeldAt;
        bool Still(Vector2 p) => (onRoad != null && onRoad(p)) || (held != null && held.Exists(h => (h - p).sqrMagnitude <= HoldRadius * HoldRadius));
        var shareFade = new float[even.Count];
        {
            int since = int.MaxValue / 2;
            for (int i = 0; i < even.Count; i++)
            {
                since = Still(even[i]) ? 0 : since + 1;
                shareFade[i] = Mathf.Clamp01(since * Spacing / ShareFade);
            }
            since = int.MaxValue / 2;
            for (int i = even.Count - 1; i >= 0; i--)
            {
                since = Still(even[i]) ? 0 : since + 1;
                shareFade[i] = Mathf.Min(shareFade[i], Mathf.Clamp01(since * Spacing / ShareFade));
            }
        }
        int n = even.Count;
        var across = new Vector2[n];
        var offset = new float[n];
        var hold = new float[n];
        var cross = new float[n];
        for (int i = 1; i < n - 1; i++)
        {
            Vector2 p = even[i];
            Vector2 tangent = (even[i + 1] - even[i - 1]).normalized;
            across[i] = new Vector2(-tangent.y, tangent.x);
            float along = i * Spacing;
            hold[i] = Mathf.Clamp01(Mathf.Min(along, total - along) / EndFade)
                * RoadPathfinder.MeanderBiome(biome(p)) * shareFade[i];
            float grade = Mathf.Abs(ground(even[i + 1]) - ground(even[i - 1])) / (2f * Spacing);
            offset[i] = Amplitude * hold[i] * GradeFactor(grade, RoadGrade.Configured) * Sway(along, phase);
            cross[i] = (ground(p + across[i] * CrossProbe) - ground(p - across[i] * CrossProbe)) / (2f * CrossProbe);
        }
        FollowContour(even, across, offset, hold, cross, ground);
        var result = new List<Vector2>(n) { even[0] };
        for (int i = 1; i < n - 1; i++)
        {
            Vector2 p = even[i], q = p + across[i] * offset[i];
            if (hold[i] > 0f && (wet(q) || RoadSiteProtection.BlocksSegment(p, q, RoadSiteProtection.Clearance(width), null, null))) q = p;
            result.Add(q);
        }
        result.Add(even[n - 1]);
        return result;
    }

    /// <summary>Half the width over which the ground's fall across the road is read, metres.</summary>
    public const float CrossProbe = 4f;
    /// <summary>Fall across the road below which the sway is left as it is.</summary>
    public const float SidehillFrom = 0.05f;
    /// <summary>Fall across the road from which the sway follows the contour fully.</summary>
    public const float SidehillFull = 0.15f;
    /// <summary>Half-width of the window, in swayed points, over which the ground along the
    /// swayed line is smoothed to find its contour (3 points, 36 m each side).</summary>
    public const int ContourWindow = 3;

    /// <summary>How far the sway follows the contour on ground falling this much across the road:
    /// 0 up to <see cref="SidehillFrom"/>, 1 from <see cref="SidehillFull"/>.</summary>
    public static float SidehillShare(float crossFall)
    {
        float t = Mathf.Clamp01((Mathf.Abs(crossFall) - SidehillFrom) / (SidehillFull - SidehillFrom));
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// On a sidehill a sideways step is a step up or down the hill, so a sway laid across the
    /// slope gives the swayed line's ground a bump each bend, which the grade limiter cuts or
    /// fills. There each swayed point is moved along the fall line onto the height of the
    /// swayed line's ground smoothed over <see cref="ContourWindow"/> points: the long waves
    /// stay (the smoothed line rises and falls with them), the short ones turn into following
    /// the hillside, out round each spur and in along each gully. Within the amplitude; nothing
    /// moves where the sway is held (the ends, shared road, biomes without sway); on ground
    /// falling less than <see cref="SidehillFrom"/> across the road the sway is unchanged.
    /// </summary>
    internal static void FollowContour(List<Vector2> even, Vector2[] across, float[] offset, float[] hold, float[] cross,
        Func<Vector2, float> ground)
    {
        int n = even.Count;
        var swayed = new float[n];
        var smooth = new float[n];
        for (int pass = 0; pass < 3; pass++)
        {
            for (int i = 0; i < n; i++) swayed[i] = ground(even[i] + across[i] * offset[i]);
            for (int i = 0; i < n; i++)
            {
                float sum = 0f, weights = 0f;
                for (int k = -ContourWindow; k <= ContourWindow; k++)
                {
                    int j = i + k;
                    if (j < 0 || j >= n) continue;
                    float w = ContourWindow + 1 - Mathf.Abs(k);
                    sum += w * swayed[j]; weights += w;
                }
                smooth[i] = sum / weights;
            }
            for (int i = 1; i < n - 1; i++)
            {
                float share = SidehillShare(cross[i]) * hold[i];
                if (share <= 0f) continue;
                float step = (smooth[i] - swayed[i]) / cross[i];
                offset[i] = Mathf.Clamp(offset[i] + share * step, -Amplitude * hold[i], Amplitude * hold[i]);
            }
        }
    }

    internal static List<Vector2> Resample(List<Vector2> path, float spacing)
    {
        var result = new List<Vector2> { path[0] };
        float carry = 0f;
        for (int i = 1; i < path.Count; i++)
        {
            Vector2 a = path[i - 1], b = path[i];
            float len = Vector2.Distance(a, b), t = spacing - carry;
            while (t <= len) { result.Add(a + (b - a) * (t / len)); t += spacing; }
            carry = len - (t - spacing);
        }
        if (Vector2.Distance(result[result.Count - 1], path[path.Count - 1]) > spacing * 0.25f) result.Add(path[path.Count - 1]);
        else result[result.Count - 1] = path[path.Count - 1];
        return result;
    }

    /// <summary>Earthwork a swayed plan may add to its deepest cut or fill, metres.</summary>
    public const float EarthSlack = 0.5f;
    /// <summary>The game's terrain edit limit: a level delta is clamped to +/-8 m.</summary>
    public const float TerrainLimit = 8f;

    /// <summary>
    /// The points of a swayed plan that move more earth than the plain plan allows (past its deepest cut or fill
    /// by more than <see cref="EarthSlack"/>, or past the terrain limit where the plain one is not), at least
    /// <see cref="HoldRadius"/> apart: where its sway is to be held.
    /// </summary>
    public static List<Vector2> MoreEarthAt(IList<Vector2> swayed, IList<float> swayedHeights,
        IList<Vector2> plain, IList<float> plainHeights, Func<Vector2, float> ground)
    {
        float deepest = 0f;
        for (int i = 0; i < plain.Count; i++) deepest = Mathf.Max(deepest, Mathf.Abs(plainHeights[i] - ground(plain[i])));
        float allowed = Mathf.Min(deepest + EarthSlack, Mathf.Max(deepest, TerrainLimit));
        var spots = new List<Vector2>();
        for (int i = 0; i < swayed.Count; i++)
        {
            if (Mathf.Abs(swayedHeights[i] - ground(swayed[i])) <= allowed) continue;
            Vector2 p = swayed[i];
            if (spots.Count == 0 || (spots[spots.Count - 1] - p).sqrMagnitude >= HoldRadius * HoldRadius) spots.Add(p);
        }
        return spots;
    }

    /// <summary>
    /// Points every <see cref="HoldRadius"/> along one half of a path (the first, or the second), which hold the
    /// sway over that half: for a swayed plan refused where nothing says where.
    /// </summary>
    public static List<Vector2> HalfOf(List<Vector2> path, bool second)
    {
        var even = Resample(path, HoldRadius);
        int mid = even.Count / 2;
        return second ? even.GetRange(mid, even.Count - mid) : even.GetRange(0, mid + 1);
    }

    /// <summary>
    /// The swayed road moves no more earth than the plain one: its deepest cut
    /// or fill is within <see cref="EarthSlack"/> of the plain one's, and it
    /// has no more points past the terrain limit.
    /// </summary>
    public static bool NoMoreEarth(IList<Vector2> swayed, IList<float> swayedHeights,
        IList<Vector2> plain, IList<float> plainHeights, Func<Vector2, float> ground)
    {
        (float deepest, int past) Measure(IList<Vector2> pts, IList<float> hs)
        {
            float deepest = 0f; int past = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                float d = Mathf.Abs(hs[i] - ground(pts[i]));
                deepest = Mathf.Max(deepest, d);
                if (d > TerrainLimit) past++;
            }
            return (deepest, past);
        }
        var s = Measure(swayed, swayedHeights);
        var p = Measure(plain, plainHeights);
        return s.deepest <= p.deepest + EarthSlack && s.past <= p.past;
    }
}
