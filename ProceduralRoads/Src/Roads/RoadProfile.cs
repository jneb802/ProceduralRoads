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
    /// How strongly terrain is pulled to road height at a lateral distance
    /// from the centerline: 1 inside the flat core, smoothstep falloff to 0
    /// at halfWidth + TerrainBlendMargin.
    /// </summary>
    public static float LevelBlend(float distFromCenter, float roadWidth)
    {
        float halfWidth = roadWidth * 0.5f;
        float flatCore = halfWidth * RoadConstants.RoadFlatCoreRatio;
        float outerEdge = halfWidth + RoadConstants.TerrainBlendMargin;

        if (distFromCenter <= flatCore)
            return 1f;
        if (distFromCenter >= outerEdge)
            return 0f;

        return 1f - Smooth((distFromCenter - flatCore) / (outerEdge - flatCore));
    }

    /// <summary>
    /// Paint intensity at a lateral distance: solid inside the flat core,
    /// fading to zero at RoadPaintOuterRatio of the half-width — strictly
    /// inside the leveled footprint, leaving an unpainted leveled verge.
    /// </summary>
    public static float PaintStrength(float distFromCenter, float roadWidth)
    {
        float halfWidth = roadWidth * 0.5f;
        float solidEdge = halfWidth * RoadConstants.RoadFlatCoreRatio;
        float paintEdge = halfWidth * RoadConstants.RoadPaintOuterRatio;

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
