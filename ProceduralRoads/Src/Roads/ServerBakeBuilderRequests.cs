using System;
using System.Collections.Generic;

namespace ProceduralRoads;

/// <summary>
/// The terrain requests the server bake has waiting in the game's heightmap builder at once, capped.
///
/// The builder (HeightmapBuilder) has one thread working a FIFO list of requests and keeps at most 16 finished builds
/// on its ready list, dropping the oldest past that. Asking whether a zone's terrain is ready (IsTerrainReady) queues
/// its build when it is not there, so a bake asking for more zones than the ready list holds had its own finished
/// builds evicted before it came back for them, asked again, and kept the queue long for the game's own zone
/// streaming. The cap is half the ready list (<see cref="HalfTheReadyList"/>): finished builds are not evicted before
/// the bake takes them, and the game's own streaming keeps the other half.
///
/// A zone counts from the first time the bake asks for its terrain until the zone is finished with it: it took its
/// data, gave up, or left the queue for any other reason (<see cref="Release"/>; every pass through a zone that does
/// not end waiting on its terrain releases it). A zone asked for already is never refused: it holds its location.
///
/// The order the bake's queue is worked in is decided here too (<see cref="RunSlice"/>): the zones holding a request
/// (at most <see cref="Max"/>) are tried first each slice, outside the queue; the queue is worked from its front only
/// while a request is free, and a zone the cap refuses stays where it is and ends the slice. Sending waiting and
/// refused zones to the back of the queue instead let a full pass over N zones write only the few the cap had asked
/// for, so N zones took about N squared over Max frames.
/// Pure: ServerTerrainBake asks the game.
/// </summary>
public sealed class ServerBakeBuilderRequests
{
    public enum Answer
    {
        /// <summary>The terrain is built: the zone may make its terrain now.</summary>
        Ready,
        /// <summary>Asked (the build is queued or running): come back to the zone later.</summary>
        Building,
        /// <summary>Not asked: as many requests as the cap allows are waiting. Come back later; no try is spent.</summary>
        Capped,
    }

    /// <summary>The zones asked for and not yet released, in the order they were asked (never more than <see cref="Max"/>).</summary>
    private readonly List<Vector2s> m_waiting = new();

    public ServerBakeBuilderRequests(int max) => Max = max;

    /// <summary>How many requests may wait in the builder at once.</summary>
    public int Max { get; }

    /// <summary>Zones asked for and not yet released.</summary>
    public int Count => m_waiting.Count;

    /// <summary>Every request is taken: a zone not asked for already would be refused.</summary>
    public bool IsFull => m_waiting.Count >= Max;

    /// <summary>Whether the zone holds a request (asked for and not yet released).</summary>
    public bool Holds(Vector2s zone) => m_waiting.Contains(zone);

    /// <summary>Half the builder's ready list: the bake's share of it.</summary>
    public static int HalfTheReadyList(int readySlots) => readySlots / 2;

    /// <summary>Ask for a zone's terrain through <paramref name="isReady"/> (which queues the build when it is not
    /// ready), unless the cap is full and the zone was not asked for already.</summary>
    public Answer Ask(Vector2s zone, Func<bool> isReady)
    {
        if (!m_waiting.Contains(zone))
        {
            if (m_waiting.Count >= Max) return Answer.Capped;
            m_waiting.Add(zone);
        }
        return isReady() ? Answer.Ready : Answer.Building;
    }

    /// <summary>The zone is finished with the builder for now: its slot is free.</summary>
    public void Release(Vector2s zone) => m_waiting.Remove(zone);

    /// <summary>The queue went away (a new network, the world going away).</summary>
    public void Clear() => m_waiting.Clear();

    /// <summary>
    /// One slice of the bake's queue. First every zone holding a request (waiting on its terrain) is tried once, in the
    /// order it was asked; then the queue from <paramref name="next"/>, one zone at a time, while
    /// <paramref name="inBudget"/> allows and a request is free. <paramref name="process"/> works one zone and says
    /// whether it comes back (it waits on its terrain, or the cap refused it): a zone that comes back holding its
    /// request leaves the queue for the waiting zones; one that comes back without one (refused) stays at
    /// <paramref name="next"/> and ends the slice. The check that a request is free comes before the zone is worked at
    /// all, so a full cap costs no zone's planning. A waiting zone that comes back without its request (nothing does
    /// today) goes to the back of the queue rather than being lost.
    /// </summary>
    public void RunSlice(List<Vector2s> queue, ref int next, Func<Vector2s, bool> process, Func<bool> inBudget)
    {
        if (m_waiting.Count > 0)
        {
            Vector2s[] waiting = m_waiting.ToArray();
            foreach (Vector2s zone in waiting)
            {
                if (!inBudget()) return;
                if (process(zone) && !Holds(zone)) queue.Add(zone);
            }
        }
        while (next < queue.Count && !IsFull && inBudget())
        {
            Vector2s zone = queue[next];
            if (process(zone) && !Holds(zone)) return;
            next++;
        }
    }

    /// <summary>The zones holding a request, in the order they were asked.</summary>
    public IReadOnlyList<Vector2s> Waiting => m_waiting;

    /// <summary>
    /// Zones owed new work (an append's, RoadSpatialGrid.ZonesChangedSince) join the queue at its back, each once: not one
    /// still in the queue's unworked part (from <paramref name="next"/>), holding a request (it is worked again before
    /// the queue), or held for later elsewhere (<paramref name="held"/>: deferred or due a repair, it comes back on its own).
    /// Each reads what it owes when it is worked, so one already waiting takes the new work with it. A zone the queue has
    /// worked already is owed again: a zone completed earlier still owes a new append. Returns how many joined. The one
    /// owner of the bake queue's admission (docs/OWNERS.md).
    /// </summary>
    public int Admit(List<Vector2s> queue, int next, IEnumerable<Vector2s> owed, Func<Vector2s, bool>? held = null)
    {
        var pending = new HashSet<Vector2s>(m_waiting);
        for (int i = Math.Max(0, next); i < queue.Count; i++) pending.Add(queue[i]);
        int added = 0;
        foreach (Vector2s zone in owed)
        {
            if (!pending.Add(zone) || held != null && held(zone)) continue;
            queue.Add(zone);
            added++;
        }
        return added;
    }
}
