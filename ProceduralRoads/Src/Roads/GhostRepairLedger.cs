using System;
using System.Collections.Generic;

namespace ProceduralRoads;

/// <summary>
/// The one place that decides when the bake queue looks at a zone again, and
/// how many more times it is worth trying.
///
/// One entry per zone, holding both when it may be looked at again and how
/// many writes have failed for it. Waiting costs nothing (for an owner to
/// leave, for terrain to be built, for a player to walk away); only
/// <see cref="FailedWrite"/> spends the budget. A zone handed to the queue is
/// not handed over again until the queue answers.
///
/// The clock is passed in, so the schedule can be run against a fake one.
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
    /// Zones whose terrain has spent its attempts, kept apart from the entries
    /// so that deferring a zone for other work cannot revive terrain's budget.
    /// Forgotten by <see cref="Clear"/> (a new network) or an explicit success.
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
    /// stands (unlike <see cref="Succeeded"/>). Never retires a zone with a
    /// spent attempt: that entry holds terrain's scheduled retry.
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
