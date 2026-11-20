using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// Islands are separate networks: an island's worker builds as if no other island had been built. The one owner of that
/// rule. Every road point, ride line, staircase, crossing and viaduct an island worker commits carries its island's number
/// (<see cref="Tag"/>); every read of the network asks <see cref="Sees(int, int)"/> of each thing it finds, so a worker
/// sees its own island's work and untagged work (laid before the islands, loaded from a save, laid by a manual road) and
/// nothing of another island's, whichever island finished first. Off an island worker (the main thread, the bake, console
/// commands, a loaded network) everything is seen. A read that passed over another island's work is counted
/// (<see cref="Skipped"/>) for the generation summary.
/// </summary>
internal static class CrossIslandReads
{
    internal enum Kind { Points, Lines, Staircases, Crossings, Viaducts }
    private static readonly string[] Names = { "road points", "ride lines", "staircases", "crossings", "viaducts" };

    /// <summary>The island worker running this thread, 1-based; 0 on any other thread. Set for an island's whole run, and
    /// on the worker threads its pricing runs on (<see cref="On{T}"/>).</summary>
    [System.ThreadStatic] internal static int Current;
    private static readonly long[] s_counts = new long[5];
    private static readonly ConcurrentQueue<string> s_samples = new();

    /// <summary>This thread's island: its whole worker number, or 0 off an island worker.</summary>
    internal static int Me { get { int c = Current; return c > 0 ? c : 0; } }

    /// <summary>What a commit on this thread tags its work with: 0 for none.</summary>
    internal static int Tag => Me;

    /// <summary>Whether island worker <paramref name="me"/> (<see cref="Me"/>, read once per read) sees work tagged
    /// <paramref name="tag"/>: its own island's and untagged work; everything off an island worker.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static bool Sees(int me, int tag) => me == 0 || tag == 0 || tag == me;

    /// <summary><see cref="Sees(int, int)"/> for this thread.</summary>
    internal static bool Sees(int tag) => Sees(Me, tag);

    /// <summary><paramref name="work"/> run as island worker <paramref name="island"/> on this thread (an island's pricing
    /// on another worker thread), this thread's own island restored after.</summary>
    internal static T On<T>(int island, System.Func<T> work)
    {
        int was = Current;
        Current = island;
        try { return work(); }
        finally { Current = was; }
    }

    /// <summary>A read on this thread passed over work of island <paramref name="tag"/> (one count per read).</summary>
    internal static void Skipped(Kind kind, int tag, Vector2 at)
    {
        if (tag == 0) return;
        Interlocked.Increment(ref s_counts[(int)kind]);
        if (s_samples.Count < 6) s_samples.Enqueue(System.FormattableString.Invariant($"{Names[(int)kind]} of island {tag} passed over by island {Current} at ({at.x:F0}, {at.y:F0})"));
    }

    internal static void Reset()
    {
        for (int i = 0; i < s_counts.Length; i++) Interlocked.Exchange(ref s_counts[i], 0);
        while (s_samples.TryDequeue(out _)) { }
    }

    internal static long Total { get { long t = 0; for (int i = 0; i < s_counts.Length; i++) t += Interlocked.Read(ref s_counts[i]); return t; } }

    internal static string Describe()
    {
        var parts = new System.Collections.Generic.List<string>();
        for (int i = 0; i < s_counts.Length; i++) parts.Add($"{Names[i]} {Interlocked.Read(ref s_counts[i])}");
        string text = $"{Total} read(s) passed over another island's work (" + string.Join(", ", parts) + ")";
        return s_samples.IsEmpty ? text : text + "; first: " + string.Join("; ", s_samples);
    }
}
