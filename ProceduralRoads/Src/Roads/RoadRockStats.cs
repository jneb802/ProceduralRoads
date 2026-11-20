using System.Diagnostics;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Where the rock pass spends its time, summed over a minute and written as
/// one debug line: the automatic pass re-probes a loaded zone every half
/// second on every client with the mod, and a server probes each zone it
/// generates for a player, so both are per-frame costs worth seeing.
/// Timings are Stopwatch ticks; the counts say how much work they bought.
/// </summary>
internal static class RoadRockStats
{
    internal static long Passes, Skipped, NotReady, Points, Queries, Colliders, Judged, Carved, Removed;
    internal static long TotalTicks, MaxPassTicks, LookupTicks, ClearanceTicks, GroundTicks, QueryTicks, JudgeTicks, CarveTicks, SyncTicks;
    private static float s_nextLog = -1f;
    private const float Period = 60f;

    internal static long Now => Stopwatch.GetTimestamp();

    internal static void EndPass(long started)
    {
        long spent = Stopwatch.GetTimestamp() - started;
        Passes++;
        TotalTicks += spent;
        if (spent > MaxPassTicks) MaxPassTicks = spent;
    }

    private static string Ms(long ticks) => (ticks * 1000.0 / Stopwatch.Frequency).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Write the minute's line (when the pass ran at all) and start the next minute.</summary>
    internal static void MaybeLog()
    {
        float now = Time.unscaledTime;
        if (s_nextLog < 0f) { s_nextLog = now + Period; return; }
        if (now < s_nextLog) return;
        s_nextLog = now + Period;
        if (Passes > 0 || Skipped > 0)
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(
                $"Road rocks timing ({Period:F0} s): {Passes} pass(es) ({Skipped} zone(s) skipped as done, {NotReady} not ready yet), {Ms(TotalTicks)} ms (worst pass {Ms(MaxPassTicks)} ms); " +
                $"lookup {Ms(LookupTicks)} ms; ground {Ms(GroundTicks)} ms for {Points} road points; clearance boxes {Ms(ClearanceTicks)} ms; overlap {Ms(QueryTicks)} ms for {Queries} queries returning {Colliders} colliders; " +
                $"judging {Ms(JudgeTicks)} ms for {Judged} objects; carving {Ms(CarveTicks)} ms ({Carved} carved, {Removed} removed whole); " +
                $"transform sync {Ms(SyncTicks)} ms");
        Passes = Skipped = NotReady = Points = Queries = Colliders = Judged = Carved = Removed = 0;
        TotalTicks = MaxPassTicks = LookupTicks = ClearanceTicks = GroundTicks = QueryTicks = JudgeTicks = CarveTicks = SyncTicks = 0;
    }
}
