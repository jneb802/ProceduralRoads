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
/// scaled by the biome (the meander's table: none in mountains or Mistlands)
/// and faded out on climbs (by 0.12 grade), in the first and last 24 m, and
/// wherever the swayed point would be wet or inside a site. The shaped road
/// then passes every check any road does; if it is refused, the road is
/// planned again without the sway. Settable for tests; 0 is no sway.
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
    /// <summary>Ground grade along the road at which the sway has faded out. Validation switch
    /// WIGGLE_FLAT.</summary>
    public static readonly float Flat = DebugSwitches.Number("WIGGLE_FLAT", 0.12f, 0.02f, 1f);

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
        // on it, not sway beside it, or corners are drawn twice.
        var shareFade = new float[even.Count];
        {
            int since = int.MaxValue / 2;
            for (int i = 0; i < even.Count; i++)
            {
                since = onRoad != null && onRoad(even[i]) ? 0 : since + 1;
                shareFade[i] = Mathf.Clamp01(since * Spacing / ShareFade);
            }
            since = int.MaxValue / 2;
            for (int i = even.Count - 1; i >= 0; i--)
            {
                since = onRoad != null && onRoad(even[i]) ? 0 : since + 1;
                shareFade[i] = Mathf.Min(shareFade[i], Mathf.Clamp01(since * Spacing / ShareFade));
            }
        }
        var result = new List<Vector2>(even.Count) { even[0] };
        for (int i = 1; i < even.Count - 1; i++)
        {
            Vector2 p = even[i];
            Vector2 tangent = (even[i + 1] - even[i - 1]).normalized;
            var across = new Vector2(-tangent.y, tangent.x);
            float along = i * Spacing;
            float fade = Mathf.Clamp01(Mathf.Min(along, total - along) / EndFade);
            float grade = Mathf.Abs(ground(even[i + 1]) - ground(even[i - 1])) / (2f * Spacing);
            fade *= Mathf.Clamp01(1f - grade / Flat);
            fade *= RoadPathfinder.MeanderBiome(biome(p));
            fade *= shareFade[i];
            Vector2 q = p + across * (Amplitude * fade * Sway(along, phase));
            if (fade > 0f && (wet(q) || RoadSiteProtection.BlocksSegment(p, q, width * 0.5f + 2f, null, null))) q = p;
            result.Add(q);
        }
        result.Add(even[even.Count - 1]);
        return result;
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
