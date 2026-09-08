using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ProceduralRoads;

/// <summary>
/// Stage timings and work counters for one validation run, so a slow in-game
/// cycle can say where its time went instead of being guessed at.
///
/// Everything is aggregated: a stage keeps its call count, total and longest
/// call (with the label of that call), a counter keeps a sum, a wait keeps a
/// count per reason, a mark keeps the moment an event happened. Nothing here
/// logs per point; the summary is a few lines and the JSON a few kilobytes
/// however large the world. Pure .NET (Stopwatch only), so the harness
/// compiles it and the tests exercise it; the plugin feeds it frame times
/// and the road_timings console command prints or writes it.
/// </summary>
public static class RoadTimings
{
    public struct StageStat
    {
        public int Count;
        public double TotalMs;
        public double MaxMs;
        public string? MaxLabel;
    }

    public struct MarkStat
    {
        public string Name;
        public DateTime Utc;
        public double MsSinceStart;
    }

    /// <summary>A frame longer than this counts as a stall.</summary>
    public const double StallFrameMs = 100.0;

    private static readonly object s_lock = new object();
    private static readonly Dictionary<string, StageStat> s_stages = new Dictionary<string, StageStat>();
    private static readonly Dictionary<string, long> s_counters = new Dictionary<string, long>();
    private static readonly Dictionary<string, int> s_waits = new Dictionary<string, int>();
    private static readonly List<MarkStat> s_marks = new List<MarkStat>();
    private static readonly Stopwatch s_clock = Stopwatch.StartNew();
    private static DateTime s_startedUtc = DateTime.UtcNow;
    private static long s_frames;
    private static int s_stallFrames;
    private static double s_maxFrameMs;
    private static double s_maxFrameAtMs;

    /// <summary>
    /// Off by default: a player's game records nothing (one branch per probe).
    /// [Debug] Timings = true in the config, or road_timings reset / run,
    /// switches recording on for the session.
    /// </summary>
    public static bool Enabled { get; set; }

    /// <summary>Identifier the station gave this run; empty until road_timings reset &lt;id&gt;.</summary>
    public static string RunId { get; private set; } = "";

    public static DateTime StartedUtc => s_startedUtc;
    public static double ElapsedMs => s_clock.Elapsed.TotalMilliseconds;

    /// <summary>Forget everything and start a new run (optionally named); recording on.</summary>
    public static void Reset(string? runId = null)
    {
        lock (s_lock)
        {
            Enabled = true;
            s_stages.Clear();
            s_counters.Clear();
            s_waits.Clear();
            s_marks.Clear();
            s_frames = 0;
            s_stallFrames = 0;
            s_maxFrameMs = 0;
            s_maxFrameAtMs = 0;
            s_clock.Restart();
            s_startedUtc = DateTime.UtcNow;
            RunId = runId ?? "";
        }
    }

    /// <summary>Name the run in progress without clearing it (a cold start keeps its load timings).</summary>
    public static void SetRunId(string runId)
    {
        lock (s_lock)
        {
            RunId = runId ?? "";
            Enabled = true;
        }
    }

    /// <summary>Time a block: <c>using (RoadTimings.Stage("gen.pathfind", label)) { ... }</c>.</summary>
    public static Scope Stage(string name, string? label = null) => Enabled ? new Scope(name, label) : default;

    /// <summary>Record one call of a stage that took <paramref name="ms"/>.</summary>
    public static void Record(string name, double ms, string? label = null)
    {
        if (!Enabled) return;
        lock (s_lock)
        {
            s_stages.TryGetValue(name, out StageStat stat);
            stat.Count++;
            stat.TotalMs += ms;
            if (ms > stat.MaxMs || stat.Count == 1)
            {
                stat.MaxMs = ms;
                stat.MaxLabel = label;
            }
            s_stages[name] = stat;
        }
    }

    /// <summary>Add to a work counter (vertices modified, iterations, zones written...).</summary>
    public static void Count(string name, long by = 1)
    {
        if (!Enabled) return;
        lock (s_lock)
        {
            s_counters.TryGetValue(name, out long value);
            s_counters[name] = value + by;
        }
    }

    /// <summary>Something was deferred or skipped; the reason is the key, the count says how often.</summary>
    public static void Wait(string reason)
    {
        if (!Enabled) return;
        lock (s_lock)
        {
            s_waits.TryGetValue(reason, out int value);
            s_waits[reason] = value + 1;
        }
    }

    /// <summary>An event happened now (world load started, locations ready, player spawned...).</summary>
    public static void Mark(string name)
    {
        if (!Enabled) return;
        lock (s_lock)
        {
            s_marks.Add(new MarkStat { Name = name, Utc = DateTime.UtcNow, MsSinceStart = s_clock.Elapsed.TotalMilliseconds });
        }
    }

    /// <summary>One rendered frame took <paramref name="deltaMs"/>; the plugin calls this every Update.</summary>
    public static void Frame(double deltaMs)
    {
        if (!Enabled) return;
        lock (s_lock)
        {
            s_frames++;
            if (deltaMs > StallFrameMs)
                s_stallFrames++;
            if (deltaMs > s_maxFrameMs)
            {
                s_maxFrameMs = deltaMs;
                s_maxFrameAtMs = s_clock.Elapsed.TotalMilliseconds;
            }
        }
    }

    public static bool TryGetStage(string name, out StageStat stat)
    {
        lock (s_lock) return s_stages.TryGetValue(name, out stat);
    }

    public static long GetCount(string name)
    {
        lock (s_lock) return s_counters.TryGetValue(name, out long value) ? value : 0;
    }

    public static int GetWaits(string reason)
    {
        lock (s_lock) return s_waits.TryGetValue(reason, out int value) ? value : 0;
    }

    public static IReadOnlyList<MarkStat> Marks
    {
        get { lock (s_lock) return s_marks.ToArray(); }
    }

    public static long Frames { get { lock (s_lock) return s_frames; } }
    public static int StallFrames { get { lock (s_lock) return s_stallFrames; } }
    public static double MaxFrameMs { get { lock (s_lock) return s_maxFrameMs; } }

    /// <summary>
    /// The compact human summary: one line per stage (count, total, longest
    /// and what it was), then counters, waits, marks and the frame stalls.
    /// </summary>
    public static string Summary()
    {
        var sb = new StringBuilder();
        lock (s_lock)
        {
            sb.Append("run=").Append(RunId.Length == 0 ? "-" : RunId)
              .Append(" started=").Append(s_startedUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
              .Append("Z elapsed=").Append(Fmt(s_clock.Elapsed.TotalMilliseconds)).Append('\n');

            var stageNames = new List<string>(s_stages.Keys);
            stageNames.Sort(StringComparer.Ordinal);
            foreach (string name in stageNames)
            {
                StageStat stat = s_stages[name];
                sb.Append("  ").Append(name.PadRight(26))
                  .Append(stat.Count.ToString(CultureInfo.InvariantCulture).PadLeft(6)).Append("x ")
                  .Append(Fmt(stat.TotalMs).PadLeft(9))
                  .Append(" max ").Append(Fmt(stat.MaxMs));
                if (!string.IsNullOrEmpty(stat.MaxLabel))
                    sb.Append(" (").Append(stat.MaxLabel).Append(')');
                sb.Append('\n');
            }

            var counterNames = new List<string>(s_counters.Keys);
            counterNames.Sort(StringComparer.Ordinal);
            foreach (string name in counterNames)
                sb.Append("  ").Append(name.PadRight(26)).Append(s_counters[name].ToString(CultureInfo.InvariantCulture).PadLeft(10)).Append('\n');

            var waitReasons = new List<string>(s_waits.Keys);
            waitReasons.Sort(StringComparer.Ordinal);
            foreach (string reason in waitReasons)
                sb.Append("  wait ").Append(s_waits[reason].ToString(CultureInfo.InvariantCulture).PadLeft(5)).Append("x ").Append(reason).Append('\n');

            foreach (MarkStat mark in s_marks)
                sb.Append("  mark ").Append(mark.Name.PadRight(26)).Append(Fmt(mark.MsSinceStart).PadLeft(9))
                  .Append("  ").Append(mark.Utc.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append("Z\n");

            sb.Append("  frames=").Append(s_frames)
              .Append(" stalls>").Append(StallFrameMs.ToString("F0", CultureInfo.InvariantCulture)).Append("ms=").Append(s_stallFrames)
              .Append(" longest=").Append(Fmt(s_maxFrameMs)).Append(" at ").Append(Fmt(s_maxFrameAtMs)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>The same data as JSON (hand-written: no JSON library in the harness or the mod).</summary>
    public static string ToJson()
    {
        var sb = new StringBuilder();
        lock (s_lock)
        {
            sb.Append('{');
            sb.Append("\"run_id\":").Append(Quote(RunId));
            sb.Append(",\"started_utc\":").Append(Quote(s_startedUtc.ToString("o", CultureInfo.InvariantCulture)));
            sb.Append(",\"elapsed_ms\":").Append(Num(s_clock.Elapsed.TotalMilliseconds));

            sb.Append(",\"stages\":{");
            bool first = true;
            var stageNames = new List<string>(s_stages.Keys);
            stageNames.Sort(StringComparer.Ordinal);
            foreach (string name in stageNames)
            {
                StageStat stat = s_stages[name];
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Quote(name)).Append(":{\"count\":").Append(stat.Count)
                  .Append(",\"total_ms\":").Append(Num(stat.TotalMs))
                  .Append(",\"max_ms\":").Append(Num(stat.MaxMs))
                  .Append(",\"max_label\":").Append(Quote(stat.MaxLabel ?? "")).Append('}');
            }
            sb.Append('}');

            sb.Append(",\"counters\":{");
            first = true;
            var counterNames = new List<string>(s_counters.Keys);
            counterNames.Sort(StringComparer.Ordinal);
            foreach (string name in counterNames)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Quote(name)).Append(':').Append(s_counters[name]);
            }
            sb.Append('}');

            sb.Append(",\"waits\":{");
            first = true;
            var waitReasons = new List<string>(s_waits.Keys);
            waitReasons.Sort(StringComparer.Ordinal);
            foreach (string reason in waitReasons)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Quote(reason)).Append(':').Append(s_waits[reason]);
            }
            sb.Append('}');

            sb.Append(",\"marks\":[");
            first = true;
            foreach (MarkStat mark in s_marks)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"name\":").Append(Quote(mark.Name))
                  .Append(",\"utc\":").Append(Quote(mark.Utc.ToString("o", CultureInfo.InvariantCulture)))
                  .Append(",\"ms\":").Append(Num(mark.MsSinceStart)).Append('}');
            }
            sb.Append(']');

            sb.Append(",\"frames\":{\"count\":").Append(s_frames)
              .Append(",\"stalls\":").Append(s_stallFrames)
              .Append(",\"stall_threshold_ms\":").Append(Num(StallFrameMs))
              .Append(",\"longest_ms\":").Append(Num(s_maxFrameMs))
              .Append(",\"longest_at_ms\":").Append(Num(s_maxFrameAtMs)).Append('}');
            sb.Append('}');
        }
        return sb.ToString();
    }

    private static string Fmt(double ms)
    {
        if (ms >= 10000) return (ms / 1000).ToString("F1", CultureInfo.InvariantCulture) + "s";
        if (ms >= 100) return ms.ToString("F0", CultureInfo.InvariantCulture) + "ms";
        return ms.ToString("F1", CultureInfo.InvariantCulture) + "ms";
    }

    private static string Num(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

    private static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>Disposable timer handed out by <see cref="Stage"/>.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly string? m_name;
        private readonly string? m_label;
        private readonly long m_start;

        public Scope(string name, string? label)
        {
            m_name = name;
            m_label = label;
            m_start = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (m_name == null) return; // the empty scope handed out while recording is off
            double ms = (Stopwatch.GetTimestamp() - m_start) * 1000.0 / Stopwatch.Frequency;
            Record(m_name, ms, m_label);
        }
    }
}
