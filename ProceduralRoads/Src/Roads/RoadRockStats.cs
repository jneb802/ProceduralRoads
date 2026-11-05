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
    /// <summary>Frames that applied carves a pass left for later (RoadRockClearing), and the longest of them.</summary>
    internal static long CarveFrames, MaxCarveFrameTicks;
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

    internal static void EndCarveFrame(long started)
    {
        long spent = Stopwatch.GetTimestamp() - started;
        CarveFrames++;
        if (spent > MaxCarveFrameTicks) MaxCarveFrameTicks = spent;
    }

    /// <summary>Stopwatch ticks as milliseconds.</summary>
    internal static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static string MsText(long ticks) => Ms(ticks).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Write the minute's line (when the pass ran at all) and start the next minute.</summary>
    internal static void MaybeLog()
    {
        float now = Time.unscaledTime;
        if (s_nextLog < 0f) { s_nextLog = now + Period; return; }
        if (now < s_nextLog) return;
        s_nextLog = now + Period;
        if (Passes > 0 || Skipped > 0 || CarveFrames > 0)
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogDebug(
                $"Road rocks timing ({Period:F0} s): {Passes} pass(es) ({Skipped} zone(s) skipped as done, {NotReady} not ready yet), {MsText(TotalTicks)} ms (worst pass {MsText(MaxPassTicks)} ms); " +
                $"lookup {MsText(LookupTicks)} ms; ground {MsText(GroundTicks)} ms for {Points} road points; clearance boxes {MsText(ClearanceTicks)} ms; overlap {MsText(QueryTicks)} ms for {Queries} queries returning {Colliders} colliders; " +
                $"judging {MsText(JudgeTicks)} ms for {Judged} objects; carving {MsText(CarveTicks)} ms ({Carved} carved, {Removed} removed whole; {CarveFrames} frame(s) carving after their pass, the longest {MsText(MaxCarveFrameTicks)} ms); " +
                $"transform sync {MsText(SyncTicks)} ms");
        Passes = Skipped = NotReady = Points = Queries = Colliders = Judged = Carved = Removed = CarveFrames = MaxCarveFrameTicks = 0;
        TotalTicks = MaxPassTicks = LookupTicks = ClearanceTicks = GroundTicks = QueryTicks = JudgeTicks = CarveTicks = SyncTicks = 0;
    }
}
