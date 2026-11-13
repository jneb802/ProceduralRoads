using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The road cross-section, defined once and used by both terrain leveling
/// and paint so they can never drift apart:
///
///   centerline ──┬── flat core ──┬── shoulder ──┬── blend margin ──┤
///   level blend:  1.0             smoothstep→     →0 at halfW+margin
///   paint:        solid           fades→0 at 85% of halfW (grass verge)
///
/// The leveled footprint is deliberately WIDER than the painted one: the
/// road reads as a dirt/stone strip with smoothed, grassy verges instead of
/// a hard-edged carpet, and terrain eases back to natural over the margin.
/// </summary>
public static class RoadProfile
{
    /// <summary>
    /// How much of the half-width is levelled fully flat: all of it. At the
    /// paint's 60 % a 4 m road had a 2.4 m flat top and its paint (to 85 %)
    /// ran onto the shoulder, which on a raised road is the side of the bank.
    /// Levelling only: paint keeps its own bands. Settable for tests.
    /// </summary>
    internal static float FlatCoreRatio = 1f;

    /// <summary>
    /// How strongly terrain is pulled to road height at a lateral distance
    /// from the centerline: 1 inside the flat core, smoothstep falloff to 0
    /// at halfWidth + TerrainBlendMargin.
    /// </summary>
    public static float LevelBlend(float distFromCenter, float roadWidth) =>
        LevelBlend(distFromCenter, roadWidth, RoadConstants.TerrainBlendMargin);

    /// <summary>
    /// The same profile with the blend margin given explicitly, so a deep cut
    /// can be released over more ground than a shallow one. At the fixed 2 m
    /// margin a seven-metre cut is an eight-metre slot with near-vertical
    /// walls: the depth is legal, the SHAPE is what reads as a trench.
    /// </summary>
    public static float LevelBlend(float distFromCenter, float roadWidth, float blendMargin)
    {
        float halfWidth = roadWidth * 0.5f;
        float flatCore = halfWidth * FlatCoreRatio;
        float outerEdge = halfWidth + Mathf.Max(0f, blendMargin);

        if (distFromCenter <= flatCore)
            return 1f;
        if (distFromCenter >= outerEdge)
            return 0f;

        return 1f - Smooth((distFromCenter - flatCore) / (outerEdge - flatCore));
    }

    /// <summary>How wide the side slope <see cref="LevelBlend"/> makes for this road width and blend margin: from the
    /// flat core's edge to the blend's outer edge. The face the earthwork noise is laid across
    /// (<see cref="RoadEarthworkNoise.FaceBump"/>).</summary>
    public static float FaceWidth(float roadWidth, float blendMargin) =>
        roadWidth * 0.5f * (1f - FlatCoreRatio) + Mathf.Max(0f, blendMargin);

    /// <summary>The width two roads' levelled ground meets at: a road's width and the blend past each edge. The one owner of the painted edge and footprint of a road (docs/OWNERS.md).</summary>
    public static float Footprint(float width) => width + 2f * RoadConstants.TerrainBlendMargin;

    /// <summary>How far from the centre line a road of <paramref name="roadWidth"/> is painted: <see cref="RoadConstants.RoadPaintOuterRatio"/>
    /// of its half width. The one owner of the painted edge and footprint of a road (docs/OWNERS.md).</summary>
    public static float PaintEdge(float roadWidth) => roadWidth * 0.5f * RoadConstants.RoadPaintOuterRatio;

    /// <summary>
    /// Paint intensity at a lateral distance: solid inside the flat core,
    /// fading to zero at RoadPaintOuterRatio of the half-width — strictly
    /// inside the leveled footprint, leaving an unpainted leveled verge.
    /// </summary>
    public static float PaintStrength(float distFromCenter, float roadWidth)
    {
        float halfWidth = roadWidth * 0.5f;
        float solidEdge = halfWidth * RoadConstants.RoadFlatCoreRatio;
        float paintEdge = PaintEdge(roadWidth);

        if (distFromCenter <= solidEdge)
            return 1f;
        if (distFromCenter >= paintEdge)
            return 0f;

        return 1f - Smooth((distFromCenter - solidEdge) / (paintEdge - solidEdge));
    }


    private static float Smooth(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }
}
