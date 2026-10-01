using System;
using System.Collections.Generic;

namespace ProceduralRoads;

/// <summary>
/// The one place that decides when the bake queue looks at a zone again, and
/// how many more times it is worth trying.
///
/// It began as a ledger of zones whose road terrain failed to go in while the
/// game generated them for a remote peer. That is still the case it exists
/// for -- the ghost write is the only chance the queue gives a zone it left to
/// generation, so a failure there used to mean no road until a restart -- but
/// it now owns EVERY deferral, because two owners is a bug:
///
///   A failed live write used to spend an attempt here (making the zone ready
///   for the next one-second poll) AND enter a separate ten-second wait list.
///   The zone was scheduled twice. Worse, when the third failure gave up and
///   dropped the entry, the wait entry from the second failure survived; when
///   it came due it queued the zone again with no entry behind it, and a zone
///   with no entry was treated as having unlimited retries. Work continued
///   after "given up", forever.
///
/// So: one entry per zone, holding both when it may be looked at again and how
/// many writes have actually failed for it. Waiting costs nothing -- for an
/// owner to leave, for terrain to be built, for a player to walk away -- and
/// only <see cref="FailedWrite"/> spends the budget. A zone handed to the
/// queue is marked as being in it and is not handed over again until the queue
/// answers, so the queue never holds two copies of it.
///
/// The clock is passed in rather than read, so the whole schedule can be run
/// against a fake one.
/// </summary>
public sealed class GhostRepairLedger
{
    /// <summary>How many writes may actually fail before the zone is called a failure.</summary>
    public const int MaxFailedWrites = 3;

    private struct Entry
    {
        /// <summary>Writes that were tried for this zone and failed.</summary>
        public int FailedWrites;
        /// <summary>The queue holds it and owes an answer; it is not scheduled.</summary>
        public bool InQueue;
        /// <summary>Not before this time, once it is out of the queue.</summary>
        public float DueAt;
        /// <summary>It is here because a ghost write failed, not merely deferred.</summary>
        public bool Repair;
    }

    private readonly Dictionary<Vector2s, Entry> m_zones = new();

    /// <summary>
    /// Zones whose TERRAIN has spent its attempts, kept apart from the entries
    /// on purpose.
    ///
    /// Giving up used to mean only "remove the entry", and an entry is also
    /// what every other kind of deferral works through. So when terrain gave
    /// up on a zone that still had vegetation to clear with a player standing
    /// near it, the vegetation deferral recreated the entry a moment later --
    /// with the count back at zero -- and terrain started again with a fresh
    /// three. Unbounded, through a door terrain never looked at.
    ///
    /// Remembering it here instead means deferring a zone for other work
    /// cannot revive terrain's budget. It is forgotten by <see cref="Clear"/>
    /// (a new network) or by an explicit success.
    /// </summary>
    private readonly HashSet<Vector2s> m_gaveUp = new();

    /// <summary>Whether this zone's terrain has spent its attempts for this network.</summary>
    public bool HasGivenUp(Vector2s zone) => m_gaveUp.Contains(zone);

    /// <summary>Zones with something still owed to them: queued, waiting, or ready.</summary>
    public int Count => m_zones.Count;

    public bool Holds(Vector2s zone) => m_zones.ContainsKey(zone);

    /// <summary>Zones held because a write failed, rather than merely waiting for a prerequisite.</summary>
    public int RepairCount
    {
        get
        {
            int n = 0;
            foreach (KeyValuePair<Vector2s, Entry> kv in m_zones)
                if (kv.Value.Repair)
                    n++;
            return n;
        }
    }

    /// <summary>Whether the queue currently holds this zone (so it must not be scheduled).</summary>
    public bool IsQueued(Vector2s zone) => m_zones.TryGetValue(zone, out Entry entry) && entry.InQueue;

    /// <summary>How many writes have actually failed for this zone.</summary>
    public int FailedWrites(Vector2s zone) => m_zones.TryGetValue(zone, out Entry entry) ? entry.FailedWrites : 0;

    /// <summary>When it may next be handed over (only meaningful while it is not in the queue).</summary>
    public float DueAt(Vector2s zone) => m_zones.TryGetValue(zone, out Entry entry) ? entry.DueAt : 0f;

    /// <summary>
    /// A ghost write failed here: watch this zone and hand it to the queue as
    /// soon as the game has finished generating it. A zone already held keeps
    /// the attempts it has spent and whatever is scheduled for it.
    /// </summary>
    public void Record(Vector2s zone)
    {
        if (m_zones.TryGetValue(zone, out Entry entry))
        {
            entry.Repair = true;
            m_zones[zone] = entry;
            return;
        }
        m_zones[zone] = new Entry { FailedWrites = 0, InQueue = false, DueAt = 0f, Repair = true };
    }

    /// <summary>
    /// Look at this zone again no sooner than <paramref name="delay"/> from
    /// now: a prerequisite is missing (an owner in the zone, terrain the game
    /// has not built, a player standing near it). This spends nothing.
    /// </summary>
    public void Defer(Vector2s zone, float now, float delay)
    {
        if (!m_zones.TryGetValue(zone, out Entry entry))
            entry = new Entry { FailedWrites = 0, Repair = false };
        entry.InQueue = false;
        entry.DueAt = now + delay;
        m_zones[zone] = entry;
    }

    /// <summary>A new network: everything is owed afresh, given-up marks included.</summary>
    public void Clear()
    {
        m_zones.Clear();
        m_gaveUp.Clear();
    }

    /// <summary>
    /// The zones to hand to the queue now: those out of the queue, due, and
    /// which the game has finished generating. Handing one over spends
    /// nothing; it only records that the queue owes an answer.
    /// </summary>
    public List<Vector2s> TakeReady(Func<Vector2s, bool> isGenerated, float now)
    {
        var ready = new List<Vector2s>();
        if (m_zones.Count == 0)
            return ready;

        var zones = new List<Vector2s>(m_zones.Keys);
        foreach (Vector2s zone in zones)
        {
            Entry entry = m_zones[zone];
            if (entry.InQueue || now < entry.DueAt || !isGenerated(zone))
                continue;
            entry.InQueue = true;
            m_zones[zone] = entry;
            ready.Add(zone);
        }
        return ready;
    }

    /// <summary>
    /// Its roads are in, it has nothing to write, or it is not this queue's
    /// work: forget it entirely, deadline included. Nothing scheduled earlier
    /// can bring it back.
    /// </summary>
    public void Succeeded(Vector2s zone)
    {
        m_zones.Remove(zone);
        m_gaveUp.Remove(zone);
    }

    /// <summary>
    /// The work this zone was still owed is finished, but terrain's give-up
    /// STANDS. Distinct from <see cref="Succeeded"/> on purpose: that one means
    /// "terrain is fine now" and lifts the mark, which would let the budget
    /// start over.
    ///
    /// Without this there was nowhere to put "vegetation is done, terrain is
    /// not coming back": terrain returned early without touching the ledger,
    /// vegetation cleared and returned without touching it either, and the
    /// entry vegetation's own Defer had created sat there marked as queued for
    /// the rest of the session -- so the bake never reported itself finished
    /// even though nothing was left to do.
    ///
    /// It retires only work that NOTHING IS STILL TRYING FOR. A zone with a
    /// spent attempt behind it has terrain's retry scheduled in this same
    /// entry, and removing it would cancel that retry: the zone would then be
    /// neither held, nor given up, nor counted, and the bake would report
    /// itself finished with the road unwritten. That is reachable -- terrain
    /// returns Done after a NONTERMINAL failed write (a live compiler that took
    /// nothing, terrain the game never built, an ordinary failed write), and
    /// ProcessZone runs the vegetation pass immediately after it, so this is
    /// called with terrain's fresh retry sitting in the entry. Which of two
    /// concerns happens to finish second must not decide whether a road gets
    /// written, so the refusal lives here rather than resting on the caller.
    /// </summary>
    public void PendingWorkDone(Vector2s zone)
    {
        // A spent attempt means terrain is coming back for this zone; only it
        // may retire that. Vegetation's own deferral never spends one, so an
        // entry with no failures is genuinely vegetation's to close.
        if (m_zones.TryGetValue(zone, out Entry entry) && entry.FailedWrites > 0)
            return;
        m_zones.Remove(zone);
    }

    /// <summary>
    /// Nothing here can finish it -- two saved compilers, say. Forget it, and
    /// let the caller say so. Same effect as success: no stale deadline is
    /// left behind to revive it.
    /// </summary>
    public void Terminal(Vector2s zone)
    {
        m_zones.Remove(zone);
        m_gaveUp.Remove(zone);
    }

    /// <summary>
    /// A write was attempted for this zone and failed. A zone that was not
    /// being watched starts being watched here, so a first failure from the
    /// ordinary queue gets the same budget as a repair instead of retrying
    /// without one. Spends an attempt and returns whether that was the last:
    /// the zone is then dropped and the caller reports a real failure.
    /// Otherwise it is scheduled once, <paramref name="delay"/> from now.
    /// </summary>
    public bool FailedWrite(Vector2s zone, float now, float delay)
    {
        // Given up stays given up. The caller should not be asking again --
        // ProcessTerrain refuses first -- but the invariant must not rest on
        // every caller remembering to check. A fresh budget has to be
        // impossible from here, not merely unlikely.
        if (m_gaveUp.Contains(zone))
            return true;
        if (!m_zones.TryGetValue(zone, out Entry entry))
            entry = new Entry { FailedWrites = 0, Repair = true };
        entry.FailedWrites++;
        if (entry.FailedWrites >= MaxFailedWrites)
        {
            // Remembered apart from the entry: another concern deferring this
            // zone must not hand terrain a fresh budget.
            m_gaveUp.Add(zone);
            m_zones.Remove(zone);
            return true;
        }
        entry.Repair = true;
        entry.InQueue = false;
        entry.DueAt = now + delay;
        m_zones[zone] = entry;
        return false;
    }
}
