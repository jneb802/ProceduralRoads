using System;
using System.Collections.Generic;
using UnityEngine;

namespace ProceduralRoads;

/// <summary>
/// The height each location was actually placed at, read once from the saved
/// world and then only read.
///
/// It lives apart from the prefab levelling that fills it because it is the
/// part with no Unity in it, and because it is shared mutable state that
/// island workers read: it needs to be reachable by a test, and every way of
/// emptying it needs to go through one guarded door. It used to have two --
/// a guarded Reset() and an unguarded lambda handed to LocationLevelling --
/// and guarding one of them protected nothing.
/// </summary>
public static class LocationPlacementHeights
{
    /// <summary>Every saved placement: where a location stands, and at what
    /// height. Supplied by whatever can read the world; null means the world
    /// cannot be read yet.</summary>
    public static Func<IEnumerable<(Vector2 centre, float height)>?>? Source;

    private static readonly Dictionary<Vector2, float> m_heights = new();
    private static volatile bool m_read;
    private static readonly object m_gate = new object();

    /// <summary>Fill the store, once. Several island workers used to arrive
    /// here together, fill the same dictionary, and read heights that were not
    /// in it yet; two approaches were refused that way.</summary>
    public static void Read()
    {
        if (m_read) return;
        if (LocationLevelling.RefuseAfterSealing("saved placement heights")) return;
        lock (m_gate)
        {
            if (m_read) return;
            var source = Source?.Invoke();
            if (source == null) return;
            foreach (var (centre, height) in source) m_heights[centre] = height;
            m_read = true;
        }
    }

    /// <summary>The one door out. Emptying this while island workers are
    /// reading it is the same corruption as filling it while they read.</summary>
    public static void Clear()
    {
        if (LocationLevelling.Sealed)
        {
            ProceduralRoadsPlugin.ProceduralRoadsLogger.LogError(
                "saved placement heights were asked to clear while roads were being generated; refused");
            return;
        }
        lock (m_gate) { m_heights.Clear(); m_read = false; }
    }

    public static bool HasBeenRead => m_read;

    /// <summary>The height saved for this centre, if there is one.</summary>
    public static float? At(Vector2 centre)
    {
        if (!m_read) Read();
        if (m_heights.TryGetValue(centre, out float height)) return height;
        // Stored and generated coordinates can differ below console precision.
        foreach (var entry in m_heights)
            if ((entry.Key - centre).sqrMagnitude < 0.25f) return entry.Value;
        return null;
    }
}
