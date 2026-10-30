using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// How a chain of two staircases meets through a valley (one owner: one StairChain rule for approaches and road
/// staircases). A valley whose floor lies no deeper under the lower of its two ends than the
/// <see cref="StairKit.WalkwayCap"/> is crossed level, by a walkway on columns, not down stairs to its floor and up again:
/// an approach goes straight over it (RoadStairRuns.Turn), and two road staircases meeting in it are spanned by floors
/// (RoadStairRuns.SpanTheValley). Deeper, a valley is the meet it always was: down to a shared floor, or spanned where the
/// span turns least.
/// <para>A chain never doubles back alongside itself (<see cref="BackTracks"/>): a crest (both legs down from where the two
/// meet) or a pit (both up) whose two legs go off away from each other, over a ridge and on down its far side, is laid; one
/// whose second leg comes back beside the first, the flights passing a few metres apart at one height (a knoll at -6960,3370:
/// up to a pad at 40 and straight back down beside the way up), or that turns its road back however wide its loop
/// (<see cref="TurnsBack"/>), is laid as road across the hump where its ends are close and earthwork takes it
/// (RoadNetworkGenerator.PlanFromPath, before any stair is laid), else joined where they pass (RoadStairRuns.JoinLow and JoinHigh), else laid as one staircase over both stretches, else
/// refused. Nor is a switchback's pad laid with both legs flat.</para>
/// </summary>
public static class StairChain
{
    /// <summary>A crest or a valley at least this far over or under both ends is climbed and come down again (or the other
    /// way): two staircases that meet on top, or at the bottom.</summary>
    public const float CrestRise = 1f;

    /// <summary>How deep a valley under the lower of its two ends has to be before the chain goes down into it.</summary>
    public const float ValleyDepth = StairKit.WalkwayCap > CrestRise ? StairKit.WalkwayCap : CrestRise;

    /// <summary>Whether a valley whose floor is at <paramref name="bottom"/> between ends at <paramref name="endA"/> and
    /// <paramref name="endB"/> is crossed level.</summary>
    public static bool CrossesLevel(float bottom, float endA, float endB) => bottom > Mathf.Min(endA, endB) - ValleyDepth - 1e-3f;

    /// <summary>Furthest apart two stairs at one height on the two legs of a chain stand to be passing each other (the knoll's
    /// flights passed 3.7 m apart at 38.5 and 5.2 m at 37.5).</summary>
    public const float PassClose = 2f * StairLayout.PieceRun + 1.5f;

    /// <summary>Two such stairs facing less than this apart: the second leg comes back beside the first, the walk turning
    /// back by more than 135 degrees between them. Legs that turn 135 or less go off away from each other (over a sharp
    /// ridge, up one face on 135 and down the other on 0, 5.3 m apart across the crest).</summary>
    public const float BackTrackYaw = 45f;

    /// <summary>
    /// Where a chain doubles back alongside itself (the invariant every chain laid keeps; tests read it too). Across
    /// <paramref name="first"/> and <paramref name="second"/> (two staircases of a chain, as laid): two stairs at one height,
    /// facing less than <see cref="BackTrackYaw"/> apart, within <see cref="PassClose"/> of each other: the chain climbs
    /// over a hump and comes back down beside the way up (a crest), or down into a dip and back up beside it (a pit). A
    /// staircase only climbs, so a height comes once in each; over a ridge crossed on, the two stairs at a height face
    /// opposite ways. And in either, or both: a switchback's pad (two floors abreast) whose two legs, the pieces behind
    /// each floor, both go down from it, both up, or are both flat. One line per fault; none where the chain is a through
    /// climb or its legs go off away from each other.
    /// </summary>
    public static List<string> BackTracks(IReadOnlyList<StairPiece> first, IReadOnlyList<StairPiece>? second, StairKit? kit = null)
    {
        kit ??= StairKit.Stone;
        var faults = new List<string>();
        static Vector2 At(StairPiece p) => new(p.Position.x, p.Position.z);
        if (second != null)
            foreach (StairPiece a in first)
            {
                if (a.Kind != StairPieceKind.Stair) continue;
                foreach (StairPiece b in second)
                    if (b.Kind == StairPieceKind.Stair && Mathf.Abs(a.Position.y - b.Position.y) < 0.05f
                        && YawApart(a.YawDegrees, b.YawDegrees) < BackTrackYaw - 0.1f && Vector2.Distance(At(a), At(b)) <= PassClose)
                        faults.Add(FormattableString.Invariant(
                            $"stairs from {a.Position.y:F2} pass {Vector2.Distance(At(a), At(b)):F1} m apart: ({a.Position.x:F1}, {a.Position.z:F1}) yaw {a.YawDegrees} and ({b.Position.x:F1}, {b.Position.z:F1}) yaw {b.YawDegrees}"));
            }
        var all = new List<StairPiece>(first);
        if (second != null) all.AddRange(second);
        // A stair by its middle (its origin is its base), a floor by its walking surface.
        float Surface(StairPiece p) => p.Kind == StairPieceKind.Stair ? p.Position.y + StairLayout.PieceRise / 2f : p.Position.y + kit.FloorTop;
        var floors = all.FindAll(p => p.Kind == StairPieceKind.Landing);
        for (int i = 0; i < floors.Count; i++)
            for (int j = i + 1; j < floors.Count; j++)
            {
                StairPiece a = floors[i], b = floors[j];
                float h = Surface(a);
                if (Mathf.Abs(h - Surface(b)) > 0.05f || YawApart(a.YawDegrees, b.YawDegrees) > 0.1f) continue;
                Vector2 d = At(b) - At(a);
                if (Mathf.Abs(Vector2.Dot(d, StairLayout.Forward(a.YawDegrees))) > 0.1f
                    || Mathf.Abs(Mathf.Abs(Vector2.Dot(d, StairLayout.Right(a.YawDegrees))) - StairLayout.PieceRun) > 0.1f) continue;
                // A pad: the leg behind each floor (a flight leaving it may bend a step off straight back).
                int? Leg(StairPiece pad)
                {
                    Vector2 behind = At(pad) - StairLayout.Forward(pad.YawDegrees) * StairLayout.PieceRun;
                    foreach (StairPiece p in all)
                    {
                        if (p == a || p == b || (p.Kind != StairPieceKind.Stair && p.Kind != StairPieceKind.Landing)
                            || Vector2.Distance(At(p), behind) > 1f || Mathf.Abs(Surface(p) - h) > StairLayout.PieceRise) continue;
                        return p.Kind == StairPieceKind.Landing ? (Mathf.Abs(Surface(p) - h) < 0.05f ? 0 : (int?)null) : Surface(p) > h ? 1 : -1;
                    }
                    return null;
                }
                if (Leg(a) is int la && Leg(b) is int lb && la == lb)
                    faults.Add(FormattableString.Invariant(
                        $"switchback pad at ({(a.Position.x + b.Position.x) / 2f:F1}, {(a.Position.z + b.Position.z) / 2f:F1}) {h:F2}: both legs {(la > 0 ? "up" : la < 0 ? "down" : "flat")}"));
            }
        return faults;
    }

    /// <summary>
    /// Whether a road that comes to a crest or a pit's two staircases travelling <paramref name="into"/> and leaves them
    /// travelling <paramref name="outOf"/> turns back over it: by more than 180 less <see cref="BackTrackYaw"/> (135 degrees),
    /// however wide the loop its flights make. Over a ridge crossed on, or turned off it, it turns less (the knoll at
    /// -6955,3372: in on 243, up west to a landing at 40 and back down east 8.7 m from the way up, out on 26: 143 degrees).
    /// </summary>
    public static bool TurnsBack(float into, float outOf) => YawApart(into, outOf) > 180f - BackTrackYaw + 0.1f;

    /// <summary>How far apart two yaws are, 0 to 180 degrees.</summary>
    public static float YawApart(float a, float b) => Mathf.Abs(StairLayout.Wrap(a - b + 180f) - 180f);

    /// <summary>One line per chain that doubled back alongside itself (<see cref="BackTracks"/>), once per place and outcome:
    /// a crest or a pit, and whether it was joined where its flights pass, laid as one staircase, or refused.</summary>
    public static void LogBackTrack(Vector2 at, StairKit kit, bool pit, string outcome)
    {
        if (!RoadNetworkGenerator.RoadsLog || !FirstAt("backtrack " + outcome, at, true)) return;
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(FormattableString.Invariant(
            $"[STAIRS] {(pit ? "pit" : "crest")} doubling back at ({at.x:F1}, {at.y:F1}) {kit.Name}: {outcome}"));
    }

    /// <summary>A plan's flat run at <paramref name="height"/>: how high over the ground it stands at most, and how long it is.</summary>
    public static (float most, float length) Walkway(StairPlan plan, float height, StairKit kit, Func<float, float, float> ground)
    {
        float most = 0f;
        int floors = 0;
        foreach (StairPiece p in plan.Pieces)
        {
            if (p.Kind != StairPieceKind.Landing || Mathf.Abs(p.Position.y + kit.FloorTop - height) > 0.05f) continue;
            floors++;
            most = Mathf.Max(most, height - ground(p.Position.x, p.Position.z));
        }
        return (most, floors * StairLayout.PieceRun);
    }

    /// <summary>One line per walkway a chain lays across a valley, or that the cap refused: where, the kit, how high
    /// over the ground it stands at most, how long it is. Once per place and outcome, not on every retry of the road (a
    /// place's road is laid again and again); none for a walkway with no floors.</summary>
    public static void LogWalkway(Vector2 at, StairKit kit, float most, float length, bool laid)
    {
        if (!RoadNetworkGenerator.RoadsLog || (laid && length < StairLayout.PieceRun - 0.01f) || !FirstAt("walkway", at, laid)) return;
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(FormattableString.Invariant(
            $"[STAIRS] walkway at ({at.x:F1}, {at.y:F1}) {kit.Name}: {(laid ? "laid" : "refused")}, {most:F2} m over the ground at most (cap {StairKit.WalkwayCap:F0}), {length:F1} m long"));
    }

    /// <summary>One line per top-to-top join (StairLayout.JoinTops) at <paramref name="at"/>, once per place and outcome:
    /// laid or refused and why, how far it carries floors, and how high over the ground they stand at most.</summary>
    public static void LogTopJoin(Vector2 at, StairKit kit, TopJoin join)
    {
        if (!RoadNetworkGenerator.RoadsLog || !FirstAt("tops", at, join.Laid)) return;
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(FormattableString.Invariant(
            $"[STAIRS] top-to-top at ({at.x:F1}, {at.y:F1}) {kit.Name}: {(join.Laid ? "laid" : "refused, " + join.Why)}, {join.Length:F1} m carried, {(float.IsNaN(join.Highest) ? "-" : join.Highest.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))} m over the ground at most (cap {StairKit.WalkwayCap:F0})"));
    }

    /// <summary>Places logged within a cell this wide count as one.</summary>
    private const float LogCell = 4f;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int, int, bool), byte> s_logged = new();

    /// <summary>Whether this is the first line of its kind and outcome logged at this place this generation.</summary>
    private static bool FirstAt(string what, Vector2 at, bool laid) =>
        s_logged.TryAdd((what, Mathf.RoundToInt(at.x / LogCell), Mathf.RoundToInt(at.y / LogCell), laid), 0);

    /// <summary>A new generation: every place may log again.</summary>
    public static void ResetLog() => s_logged.Clear();
}
