using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace ProceduralRoads;

public static partial class RoadStairRuns
{
    /// <summary>The layouts <see cref="LayOnce"/> asks for after a stretch's first fails, in order, and lays ahead
    /// (<see cref="LayoutsAhead.Offer"/>): the one owner of their calls. Free of the road's headings in its cells, then free of
    /// its cells (still facing as the first, its allowance held); with no facing, the second is laid on the whole allowance.</summary>
    private static readonly (float? arriving, float? leaving, bool corridor, bool retried, bool faced)[] Ladder =
        { (null, null, true, true, true), (null, null, false, false, true) };
    /// <summary>As <see cref="Ladder"/>, where the first faces the road: the third is held, and a fourth, faced freely, follows.</summary>
    private static readonly (float? arriving, float? leaving, bool corridor, bool retried, bool faced)[] FacedLadder =
        { (null, null, true, true, true), (null, null, false, true, true), (null, null, false, false, false) };

    /// <summary>
    /// The last island's stretch's later layouts laid ahead on the workers the other islands, all finished, left
    /// (<see cref="RoadParallel.Spare"/>):
    /// where a stretch's layout fails, <see cref="LayOnce"/> lays the next of its ladder (free of the road's headings, then of
    /// its cells, then of its facing), each a pure layout of its call (<see cref="PlanKey"/>), so those are laid on spare
    /// workers while the first is laid, and <see cref="Plan"/> takes the one it asks for as if it had laid it itself, exactly:
    /// the same call, the same layout, stored as Plan stores its own (<see cref="PlanCache.Store"/>). A layout never asked for
    /// is dropped, so the layouts made this generation are those laid without it. Nothing is laid ahead where no worker is
    /// spare (while two or more islands are being built), on a thread no generation runs (<see cref="Start"/>), or for a layout
    /// traced.
    /// </summary>
    internal static class LayoutsAhead
    {
        private sealed class Job
        {
            public readonly PlanKey Key;
            public readonly Func<StairPlan> Make;
            public readonly int Island;
            public readonly bool Unpriced;
            /// <summary>0 waiting, 1 being laid, 2 laid, 3 dropped (or taken back by its owner to lay itself).</summary>
            public int State;
            public StairPlan? Plan;
            public bool Paid, Failed;
            /// <summary>The unheld pieces its search tallied, added once it is taken (StairLayout.Holding).</summary>
            public int Unheld;
            public Job(PlanKey key, Func<StairPlan> make, int island, bool unpriced) { Key = key; Make = make; Island = island; Unpriced = unpriced; }
        }

        private static readonly object s_gate = new();
        /// <summary>Layouts laid ahead and taken (tests).</summary>
        internal static int Taken;
        /// <summary>Layouts laid ahead (tests).</summary>
        internal static int Laid;
        private static readonly Queue<Job> s_queue = new();
        private static List<Thread>? s_threads;
        private static bool s_stopping;
        /// <summary>The layouts this thread's current stretch offered (<see cref="Offer"/>).</summary>
        private sealed class OfferedScratch : RoadNetworkGenerator.GenerationScratch
        {
            public List<Job>? Jobs;
            internal override void Clear()
            {
                Jobs = null;
            }
        }

        [ThreadStatic] private static OfferedScratch? t_offered;
        private static List<Job>? Mine
        {
            get => t_offered?.Jobs;
            set
            {
                if (t_offered == null || t_offered.Released) t_offered = new OfferedScratch();
                t_offered.Jobs = value;
            }
        }
        [ThreadStatic] private static Job? t_job;

        /// <summary>The spare workers for one generation's islands: <see cref="RoadParallel.IslandWorkers"/> threads, each laying a
        /// waiting layout only while a worker is spare. Stopped by <see cref="Stop"/>.</summary>
        internal static void Start()
        {
            lock (s_gate)
            {
                if (s_threads != null) return;
                s_stopping = false;
                s_threads = new List<Thread>();
                for (int i = 0; i < RoadParallel.IslandWorkers; i++)
                {
                    var t = new Thread(Work) { IsBackground = true, Name = "ProceduralRoads layouts ahead" };
                    s_threads.Add(t);
                    t.Start();
                }
            }
        }

        /// <summary>Abandons every unused layout and joins the spare workers at their next search stop.</summary>
        internal static void Stop()
        {
            List<Thread>? threads;
            lock (s_gate)
            {
                threads = s_threads;
                s_threads = null;
                Volatile.Write(ref s_stopping, true);
                foreach (Job job in s_queue) if (job.State == 0) job.State = 3;
                s_queue.Clear();
                Monitor.PulseAll(s_gate);
            }
            if (threads != null) foreach (Thread t in threads) t.Join();
        }

        /// <summary>A worker became spare (an island finished): the waiting layouts are looked at again.</summary>
        internal static void Wake()
        {
            lock (s_gate) Monitor.PulseAll(s_gate);
        }

        /// <summary>
        /// Offers the layouts this thread's stretch may ask for next, each by <see cref="Plan"/>'s call (its arguments as Plan
        /// takes them), in the order it would ask; replaces what the thread offered before (abandoned where unused). Only where
        /// a worker is spare now, and only layouts Plan would lay (not cached, not refused unlaid).
        /// </summary>
        internal static void Offer(Vector2 a, Vector2 b, StairKit kit, LayoutGround ground, List<Vector2>? corridor, int topLandings, bool relayPads,
            float? facing, float? topFacing, RoadEndRing? ring, IReadOnlyList<(float? arriving, float? leaving, bool corridor, bool retried, bool faced)> ladder)
        {
            Drop();
            if (s_threads == null || StairLayout.Trace != null || RoadParallel.Spare <= 0) return;
            List<Job>? mine = null;
            foreach (var (arriving, leaving, inCorridor, retried, faced) in ladder)
            {
                List<Vector2>? cells = inCorridor ? corridor : null;
                float? f = faced ? facing : null, tf = faced ? topFacing : null;
                PlanKey key = KeyOf(a, b, kit, ground, arriving, leaving, cells, StairCover.RoadStaircase, topLandings, null, null, ShareOf(retried),
                    relayPads, f, tf, null, ring, null);
                if (s_plans.Has(key) || Unlaid(a, b, cells, ground) != null) continue;
                List<Vector2>? own = cells == null ? null : new List<Vector2>(cells);
                (mine ??= new List<Job>()).Add(new Job(key,
                    () => LayOut(key, arriving, leaving, own, StairCover.RoadStaircase, topLandings, null, null, relayPads, f, tf, null, ring, null),
                    CrossIslandReads.Current, StairLayout.UnpricedLedge));
            }
            if (mine == null) return;
            Mine = mine;
            lock (s_gate)
            {
                foreach (Job job in mine) s_queue.Enqueue(job);
                Monitor.PulseAll(s_gate);
            }
        }

        /// <summary>Drops this stretch's unused layouts, including searches already running.</summary>
        internal static void Drop()
        {
            List<Job>? mine = Mine;
            if (mine == null) return;
            Mine = null;
            lock (s_gate)
            {
                foreach (Job job in mine)
                {
                    Volatile.Write(ref job.State, 3);
                    job.Plan = null;
                }
                mine.Clear();
                Monitor.PulseAll(s_gate);
            }
        }

        /// <summary>
        /// The layout this thread offered for <paramref name="key"/>, laid ahead (waiting for it while a worker lays it), and
        /// whether its search paid the foot's ledge price; false where it offered none, no worker began it (it is taken back
        /// and the caller lays it), or its laying failed or missed preparation (the caller lays it, as it would have).
        /// </summary>
        internal static bool Take(PlanKey key, out StairPlan? plan, out bool paid)
        {
            plan = null; paid = false;
            List<Job>? mine = Mine;
            if (mine == null) return false;
            Job? job = null;
            foreach (Job j in mine) if (j.Key.Equals(key)) { job = j; break; }
            if (job == null) return false;
            using var cancellation = RoadLifecycleManager.WakeOnCancellation(Wake);
            lock (s_gate)
            {
                RoadLifecycleManager.ThrowIfCancellationRequested();
                if (job.State == 0) { job.State = 3; return false; }
                if (job.State == 1)
                {
                    // Waiting, this island's worker is spare.
                    RoadParallel.Waiting(true);
                    Monitor.PulseAll(s_gate);
                    try
                    {
                        while (job.State == 1)
                        {
                            RoadLifecycleManager.ThrowIfCancellationRequested();
                            Monitor.Wait(s_gate);
                        }
                        RoadLifecycleManager.ThrowIfCancellationRequested();
                    }
                    finally { RoadParallel.Waiting(false); }
                }
                if (job.State != 2 || job.Failed) return false;
                job.State = 3;
            }
            plan = job.Plan;
            paid = job.Paid;
            StairLayout.AddUnheld(job.Unheld);
            Interlocked.Increment(ref Taken);
            return true;
        }

        private static void Work()
        {
            while (true)
            {
                Job? job = null;
                lock (s_gate)
                {
                    while (true)
                    {
                        if (s_stopping) return;
                        while (s_queue.Count > 0 && s_queue.Peek().State != 0) s_queue.Dequeue();
                        if (s_queue.Count > 0 && RoadParallel.TakeSpare())
                        {
                            job = s_queue.Dequeue();
                            job.State = 1;
                            break;
                        }
                        Monitor.Wait(s_gate);
                    }
                }
                try { Run(job); }
                finally
                {
                    RoadParallel.ReturnSpare();
                    lock (s_gate)
                    {
                        if (job.State == 1) job.State = s_stopping ? 3 : 2;
                        if (job.State == 3) job.Plan = null;
                        Monitor.PulseAll(s_gate);
                    }
                }
            }
        }

        /// <summary>At the layout search's existing stop check, leave work its stretch no longer needs.</summary>
        internal static void ThrowIfAbandoned()
        {
            Job? job = t_job;
            if (job != null && (Volatile.Read(ref s_stopping) || Volatile.Read(ref job.State) == 3))
                throw new OperationCanceledException();
        }

        /// <summary>Lays <paramref name="job"/> as its owner would: on its island (CrossIslandReads), with its ledge setting, and
        /// noting whether its search paid the ledge price and whether it missed preparation (then its owner lays it itself, so
        /// the miss marks its road).</summary>
        private static void Run(Job job)
        {
            bool unpriced = StairLayout.UnpricedLedge;
            t_job = job;
            try
            {
                ThrowIfAbandoned();
                StairLayout.UnpricedLedge = job.Unpriced;
                LocationLevelling.PreparationMissedHere = false;
                int before = StairLayout.LedgePrices;
                StairPlan plan = StairLayout.Holding(() => CrossIslandReads.On(job.Island, job.Make), out job.Unheld);
                job.Paid = StairLayout.LedgePrices != before;
                job.Failed = LocationLevelling.PreparationMissedHere;
                job.Plan = plan;
                Interlocked.Increment(ref Laid);
            }
            catch (Exception)
            {
                // Its owner lays it and meets the same fault itself.
                job.Failed = true;
            }
            finally
            {
                t_job = null;
                StairLayout.UnpricedLedge = unpriced;
                LocationLevelling.PreparationMissedHere = false;
            }
        }
    }
}
