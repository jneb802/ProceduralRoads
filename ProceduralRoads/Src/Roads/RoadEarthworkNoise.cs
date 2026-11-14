using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Make a road's earthworks read like Valheim's own ground. Levelled side
/// slopes come out at one grade with sharp lines, and generic value noise on
/// the 1 m vertex grid makes spikes and teeth, with steep faces rendering as
/// rock against smooth ground.
///
/// So this mirrors the terrain's own detail. Every biome height in vanilla's
/// WorldGenerator ends with the same two Perlin octaves (heights are x200):
/// 0.1/m at 0.01 (a 10 m wave of ~2 m) and 0.4/m at 0.003 (a 2.5 m wave of
/// ~0.6 m); the Mistlands use 0.03 and 0.01 (~6 m and ~2 m). The levelling
/// replaces that detail on a side slope in proportion to how hard it pulls,
/// so the faces were smooth where the ground around them is not.
///
/// Three parts, all on the sides only (the road surface stays flat; routes
/// are untouched):
///   - the noise: that detail put back on fill and cut faces at vanilla's own
///     amount, zero on the deck and at the far edge, scaled down on small
///     earthworks and, octave by octave, on faces narrower than the octave's
///     wave, and kept between the natural ground and the road;
///   - the side slope's width wandering along the road, 1 +/- 0.3 of the
///     blend margin over about 32 m;
///   - on fill over FLAT ground only, the side slope widened by a metre per
///     metre of fill above 1 m (at most 4 m), so a causeway's faces are gentle
///     enough to stay grass or snow. Never on a sidehill: widening fill there
///     fans an apron down the slope.
/// World-position noise, so neighbouring zones agree. The fields are settable
/// for tests.
/// </summary>
internal static class RoadEarthworkNoise
{
    /// <summary>Face detail as a multiple of vanilla's.</summary>
    internal static float Amplitude = 1f;
    internal static float WidthVariation = 0.3f;
    /// <summary>Extra side-slope metres per metre of fill above 1 m, on flat ground.</summary>
    internal static float FillSpread = 1f;
    internal static float FillSpreadMax = 4f;
    /// <summary>Steepest natural ground (rise over run across 12 m) that still counts as flat for the fill spread.</summary>
    internal static float FlatGround = 0.15f;
    internal static bool Enabled => Amplitude > 0f;

    /// <summary>How much wider or narrower the side slope is at this road point.</summary>
    internal static float MarginFactor(Vector2 roadPoint) =>
        Enabled ? 1f + WidthVariation * (2f * Perlin01((roadPoint.x + 1013f) / 32f, (roadPoint.y - 2027f) / 32f) - 1f) : 1f;

    /// <summary>Extra side-slope width for one road point on fill over flat
    /// ground; <paramref name="ground"/> samples the natural ground.</summary>
    internal static float FillExtra(Vector2 p, float roadHeight, System.Func<float, float, float> ground)
    {
        if (FillSpread <= 0f) return 0f;
        float here = ground(p.x, p.y);
        float fill = roadHeight - here;
        if (fill <= 1f) return 0f;
        float sx = Mathf.Abs(ground(p.x + 6f, p.y) - ground(p.x - 6f, p.y)) / 12f;
        float sz = Mathf.Abs(ground(p.x, p.y + 6f) - ground(p.x, p.y - 6f)) / 12f;
        if (Mathf.Max(sx, sz) > FlatGround) return 0f;
        return Mathf.Min(FillSpreadMax, FillSpread * (fill - 1f));
    }

    /// <summary>The coarse octave's frequency, per metre (vanilla's 0.1: a 10 m wave).</summary>
    internal const float CoarseFrequency = 0.1f;
    /// <summary>The fine octave's frequency, per metre (vanilla's 0.4: a 2.5 m wave).</summary>
    internal const float FineFrequency = 0.4f;

    /// <summary>Vanilla's fine terrain detail at a point, in metres about zero, as <paramref name="biome"/> carries it
    /// (RoadRules.FaceDetailCoarse, RoadRules.FaceDetailFine).</summary>
    internal static float Detail(float x, float z, RoadRules.Rules biome) => Detail(x, z, biome, float.PositiveInfinity);

    /// <summary>
    /// The detail as a face <paramref name="faceWidth"/> metres wide carries it: each octave scaled by the face's width
    /// over the octave's own wavelength, at most 1. <see cref="FaceBump"/> lays a whole bump across the face, so an
    /// octave at full height on a face narrower than its wave is that much steeper than the ground's own detail: a 6 m
    /// Mistlands wave on a 2 m face stood as a wall. Scaled, each octave's wave is squeezed onto the face no more than the
    /// face is narrower than it, so the long wave that made the wall is mostly gone and the short one nearly whole.
    /// </summary>
    internal static float Detail(float x, float z, RoadRules.Rules biome, float faceWidth)
    {
        float coarse = biome.FaceDetailCoarse, fine = biome.FaceDetailFine;
        coarse *= OnFace(faceWidth, CoarseFrequency);
        fine *= OnFace(faceWidth, FineFrequency);
        return coarse * (Perlin01(x * CoarseFrequency, z * CoarseFrequency) - 0.5f)
             + fine * (Perlin01(x * FineFrequency + 71.3f, z * FineFrequency - 19.7f) - 0.5f);
    }

    /// <summary>How much of an octave of this frequency a face this wide carries: its width over the octave's wavelength,
    /// at most all of it.</summary>
    private static float OnFace(float faceWidth, float frequency) => Mathf.Clamp01(Mathf.Max(0f, faceWidth) * frequency);

    /// <summary>Height added to a vertex on a side slope: <paramref name="blend"/>
    /// is how strongly the road pulls it (1 on the deck, 0 at the far edge), <paramref name="earthwork"/> how far the
    /// road stands from the ground there, and <paramref name="faceWidth"/> how wide the side slope is
    /// (<see cref="RoadProfile.FaceWidth"/>). The writer then keeps the face between the natural ground and the road
    /// (RoadTerrainModifier.RoadTarget): a fill face is never dug below the ground, a cut face never raised above it.</summary>
    internal static float FaceBump(Vector2 vertex, float blend, float earthwork, float faceWidth)
    {
        if (!Enabled || blend <= 0f || blend >= 1f) return 0f;
        var wg = WorldGenerator.instance;
        RoadRules.Rules here = wg != null ? RoadRules.For(RoadBiome.At(wg, vertex)) : RoadRules.Global;
        float shape = 4f * blend * (1f - blend);
        float scale = Mathf.Min(1f, Mathf.Abs(earthwork) / 2f);
        return Amplitude * Detail(vertex.x, vertex.y, here, faceWidth) * shape * scale;
    }

    // Ken Perlin's improved noise in 2D, mapped to [0, 1] like Unity's
    // Mathf.PerlinNoise (vanilla's DUtils.PerlinNoise), with a fixed table so
    // it is the same on every machine and in the headless tests.
    private static readonly int[] P = BuildTable();
    private static int[] BuildTable()
    {
        var p = new int[512]; var t = new int[256];
        for (int i = 0; i < 256; i++) t[i] = i;
        uint s = 0x9E3779B9u;
        for (int i = 255; i > 0; i--) { s = s * 1664525u + 1013904223u; int j = (int)(s % (uint)(i + 1)); (t[i], t[j]) = (t[j], t[i]); }
        for (int i = 0; i < 512; i++) p[i] = t[i & 255];
        return p;
    }
    private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);
    private static float Grad(int h, float x, float y)
    {
        switch (h & 7)
        {
            case 0: return x + y; case 1: return -x + y; case 2: return x - y; case 3: return -x - y;
            case 4: return x; case 5: return -x; case 6: return y; default: return -y;
        }
    }
    internal static float Perlin01(float x, float y)
    {
        int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
        float xf = x - xi, yf = y - yi;
        int X = xi & 255, Y = yi & 255;
        float u = Fade(xf), v = Fade(yf);
        int aa = P[P[X] + Y], ab = P[P[X] + Y + 1], ba = P[P[X + 1] + Y], bb = P[P[X + 1] + Y + 1];
        float n = Mathf.Lerp(Mathf.Lerp(Grad(aa, xf, yf), Grad(ba, xf - 1f, yf), u),
                             Mathf.Lerp(Grad(ab, xf, yf - 1f), Grad(bb, xf - 1f, yf - 1f), u), v);
        return Mathf.Clamp01(0.5f + 0.5f * n);
    }
}
