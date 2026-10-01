using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// How steeply a road is allowed to climb, as rise over run.
///
/// Two places enforce it. The pathfinder refuses a step whose ground is
/// steeper than the cap, so the search has to traverse or fail instead of
/// paying a large but finite price for the direct climb. This class enforces
/// the other half: the height profile that is actually stored. Smoothing and
/// the endpoint ramp both move stored heights away from the ground the search
/// judged, so a route inside the cap can still be built outside it.
///
/// <see cref="Limit"/> replaces a profile with the closest profile whose every
/// step is inside the cap, holding both ends where they were. Ends are held
/// because they are the heights the road has to meet: a location's ground at
/// one end, a bridge bank or the natural terrain at the other. Holding them
/// makes one case impossible - two ends further apart in height than the cap
/// allows over the length between them - and that case is a road that cannot
/// be built at this grade, which <see cref="Limit"/> reports rather than
/// quietly delivering a ramp too steep to walk.
/// </summary>
public static class RoadGrade
{
    /// <summary>
    /// The cap in force, set from "Roads/MaxGrade". One value, read by both
    /// halves: the pathfinder takes it as its default and the spatial grid
    /// holds the stored profile to it. They were briefly two - the grid read
    /// the pathfinder's copy - which is a coupling with nothing to say for
    /// itself and two values to get out of step.
    /// </summary>
    public static float Configured = RoadConstants.DefaultMaxRoadGrade;

    /// <summary>
    /// The steepest step of any road planned since the last reset, as rise
    /// over run. This is the cap's own claim about the roads it let through,
    /// and it has to be recorded here: once points are in the spatial grid
    /// they are a set per cell with no order and no road they belong to, so
    /// the nearest neighbour of a point can be a different road crossing it
    /// rather than the next metre of its own.
    /// </summary>
    public static float SteepestPlanned;

    /// <summary>A cap at or below zero is no cap at all.</summary>
    public static bool Capped(float maxGrade) => maxGrade > 0f;

    /// <summary>
    /// The steepest step in a profile, as rise over run. Steps shorter than a
    /// millimetre are skipped: two coincident dense points would otherwise
    /// report an infinite grade over no distance.
    /// </summary>
    public static float SteepestStep(IReadOnlyList<Vector2> points, IReadOnlyList<float> heights)
    {
        float steepest = 0f;
        for (int i = 1; i < points.Count && i < heights.Count; i++)
        {
            float run = Vector2.Distance(points[i - 1], points[i]);
            if (run < 0.001f) continue;
            float grade = Mathf.Abs(heights[i] - heights[i - 1]) / run;
            if (grade > steepest) steepest = grade;
        }
        return steepest;
    }

    /// <summary>
    /// Rewrite <paramref name="heights"/> in place as the closest profile no
    /// step of which exceeds <paramref name="maxGrade"/>, with the first and
    /// last heights unchanged. Returns false and leaves the profile untouched
    /// when the two ends cannot be joined inside the cap over the length
    /// between them; the caller decides what an unbuildable road means.
    ///
    /// The result is the grade-limited profile nearest the input, then clamped
    /// between the two cones that rise from the held ends. Both are built from
    /// minima and maxima of cones and so are themselves inside the cap, and
    /// clamping one by the others keeps it there, so the three-way clamp is
    /// inside the cap too - and it equals the end heights at the ends, because
    /// there the two cones meet.
    /// </summary>
    public static bool Limit(IReadOnlyList<Vector2> points, IList<float> heights, float maxGrade, IReadOnlyList<float>? edgeGrades = null)
    {
        if (!Capped(maxGrade)) return true;
        int n = Mathf.Min(points.Count, heights.Count);
        // Two points are all ends and no middle: nothing to reshape, but they
        // can still be further apart than the cap allows, and that is a road
        // refused just the same.
        if (n < 2) return true;

        // Optional per-edge caps shorten the available climb through turn landings.
        // Arc length along the profile, so the cap is metres of rise per metre
        // of road travelled rather than per point.
        float[] s = new float[n];
        for (int i = 1; i < n; i++)
            s[i] = s[i - 1] + Vector2.Distance(points[i - 1], points[i]) *
                (edgeGrades == null ? 1f : Mathf.Min(maxGrade, Mathf.Max(0f, edgeGrades[i])) / maxGrade);
        float length = s[n - 1];
        if (length <= 0f) return true;

        float first = heights[0];
        float last = heights[n - 1];
        if (Mathf.Abs(last - first) > maxGrade * length + 0.001f)
            return false;

        // Greatest capped profile at or below the input, and least capped
        // profile at or above it: each is one forward and one backward pass of
        // a cone. Their midpoint is the capped profile nearest the input.
        float[] below = new float[n];
        float[] above = new float[n];
        for (int i = 0; i < n; i++) { below[i] = heights[i]; above[i] = heights[i]; }

        for (int i = 1; i < n; i++)
        {
            float reach = maxGrade * (s[i] - s[i - 1]);
            below[i] = Mathf.Min(below[i], below[i - 1] + reach);
            above[i] = Mathf.Max(above[i], above[i - 1] - reach);
        }
        for (int i = n - 2; i >= 0; i--)
        {
            float reach = maxGrade * (s[i + 1] - s[i]);
            below[i] = Mathf.Min(below[i], below[i + 1] + reach);
            above[i] = Mathf.Max(above[i], above[i + 1] - reach);
        }

        for (int i = 0; i < n; i++)
        {
            float nearest = (below[i] + above[i]) * 0.5f;
            float floor = Mathf.Max(first - maxGrade * s[i], last - maxGrade * (length - s[i]));
            float ceiling = Mathf.Min(first + maxGrade * s[i], last + maxGrade * (length - s[i]));
            heights[i] = Mathf.Min(Mathf.Max(nearest, floor), ceiling);
        }

        // The ends are held exactly: at i = 0 and i = n-1 the two cones meet,
        // so the clamp has no room to move them. Say so rather than trust it.
        heights[0] = first;
        heights[n - 1] = last;
        return true;
    }
}
