using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// How a road meets its ends. Height smoothing lifts or cuts a road relative
/// to the natural terrain; at the very end that shows as a ledge or a hump
/// against the location the road arrives at. Over the last RampLength metres
/// the final height blends from the natural terrain (at the end) to the
/// smoothed road height, so the road meets the ground it ends on.
/// </summary>
public static class RoadEndpointRamp
{
    /// <summary>Metres from each end over which the smoothed height fades in.</summary>
    public const float RampLength = 40f;

    /// <summary>
    /// Weight of the smoothed height at a distance from the nearer end: 0 at
    /// the end, rising with a smoothstep to 1 at RampLength and beyond.
    /// </summary>
    public static float Blend(float distanceFromNearestEnd)
    {
        float t = Mathf.Clamp01(distanceFromNearestEnd / RampLength);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// What the ramp blends away FROM. Without a target that is the natural
    /// terrain under the point, which is what a road meeting open ground or a
    /// river bank should arrive at. With one - the ground a location stands
    /// on - the ramp leaves the terrain as well, so the road arrives at the
    /// height of the place it is going to rather than at whatever the last
    /// metre of hillside happens to be. On a cliff-mouth cave those are not
    /// the same number, and the road was following the hillside.
    ///
    /// The target holds only at the end itself and fades out over the same
    /// RampLength as the smoothing, so nothing inland of the ramp moves.
    /// </summary>
    public static float BaseHeight(float naturalHere, float? endTarget, float distanceFromNearestEnd)
    {
        if (!endTarget.HasValue) return naturalHere;
        return Mathf.Lerp(endTarget.Value, naturalHere, Blend(distanceFromNearestEnd));
    }
}
