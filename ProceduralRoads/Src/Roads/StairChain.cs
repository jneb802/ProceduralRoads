using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// How a chain of two staircases meets through a valley (one owner: one StairChain rule; shallow dips
/// are crossed level for road staircases too). A valley no deeper than <see cref="ShallowDip"/> under the lower of its two
/// ends is crossed level, not down stairs to its floor and up again: an approach goes straight over it
/// (RoadStairRuns.Turn), and two road staircases meeting in it are spanned at the lower top by floors
/// (RoadStairRuns.SpanTheValley). Deeper, a valley is the meet it always was: down to a shared floor, or spanned where the
/// span turns least.
/// </summary>
public static class StairChain
{
    /// <summary>A crest or a valley at least this far over or under both ends is climbed and come down again (or the other
    /// way): two staircases that meet on top, or at the bottom.</summary>
    public const float CrestRise = 1f;

    /// <summary>A valley no deeper than this under the lower of its two ends is crossed level (at the Queen's
    /// approach: a 1.5 m dip laid as stairs down, a floor and stairs up; a straight run of floors over it is better).</summary>
    public const float ShallowDip = 2f;

    /// <summary>How deep a valley under the lower of its two ends has to be before the chain goes down into it.</summary>
    public static float ValleyDepth => Mathf.Max(CrestRise, ShallowDip);

    /// <summary>Whether a valley whose floor is at <paramref name="bottom"/> between ends at <paramref name="endA"/> and
    /// <paramref name="endB"/> is crossed level.</summary>
    public static bool CrossesLevel(float bottom, float endA, float endB) => bottom > Mathf.Min(endA, endB) - ValleyDepth - 1e-3f;
}
