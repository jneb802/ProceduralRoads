using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// What a road is paved with, from its distance to the world centre: dirt in
/// the meadows around spawn, stone further out, handed over in a 10 m band.
///
///   centre ── dirt ──────────────┤10 m├────────────── stone
///                        1000 m ± 100 m
///
/// The handover lies between two of the game's own biome rings
/// (WorldGenerator.GetBiome): past where Black Forest can first appear
/// (600 m + 100 m × WorldAngle) and well inside where swamps can (2000 m). It
/// wobbles with the same 20-lobed WorldAngle the game gives its rings. The
/// surface is a pure function of world position, evaluated per painted
/// texel, so overlapping road points, junctions and zone borders all agree
/// and nothing needs saving.
/// </summary>
public static class RoadSurface
{
    /// <summary>Distance from the world centre where roads turn from dirt to stone.</summary>
    public const float HandoverAt = 1000f;

    /// <summary>The handover ring wobbles by this much with WorldAngle, as the game's biome rings do.</summary>
    public const float HandoverWobble = 100f;

    /// <summary>How much road the handover takes, centred on the ring.</summary>
    public const float HandoverWidth = 10f;

    /// <summary>
    /// How far along the change from dirt to stone a point is: 0 = dirt,
    /// 1 = stone, smoothstep across the handover band.
    /// </summary>
    public static float StoneShare(float x, float z)
    {
        float handover = HandoverAt + HandoverWobble * WorldGenerator.WorldAngle(x, z);
        float distance = Mathf.Sqrt(x * x + z * z);
        return Mathf.SmoothStep(0f, 1f, (distance - handover) / HandoverWidth + 0.5f);
    }

    /// <summary>The paint mask a road is painted toward at world (x, z).</summary>
    public static Color MaskAt(float x, float z) => Mask(StoneShare(x, z));

    /// <summary>
    /// The paint mask for a stone share. The change runs from the game's dirt
    /// (1, 0, 0), through paving laid over dirt (1, 0, 1), to the game's
    /// paving (0, 0, 1), which is what the hoe paints and what stone roads
    /// were before. One channel is always full: a straight blend
    /// (1 - s, 0, s) is only half dirt and half paving mid-way, which shows
    /// the ground through the road and lets grass grow on it (the game clears
    /// grass only where a channel is above 0.5). Alpha, the vegetation mask,
    /// is left to the painter.
    /// </summary>
    public static Color Mask(float stone)
    {
        stone = Mathf.Clamp01(stone);
        return new Color(Mathf.Clamp01(2f - 2f * stone), 0f, Mathf.Clamp01(2f * stone), 1f);
    }
}
