using System.Collections.Generic;

namespace ProceduralRoads;

/// <summary>
/// The game's terrain build data (HeightmapBuilder.HMBuildData) a zone's terrain write took, kept for that same zone's
/// saved-rock pass, which makes the zone's terrain a second time in the same call.
///
/// Heightmap.Generate takes a zone's build data off the builder's ready list (RequestTerrainSync removes what it
/// returns), so a second temporary terrain for the zone asked again and waited on the main thread behind every zone
/// still queued on the builder's one thread. The kept data is put back on the ready list just before the second
/// terrain is made, where Generate takes it the game's own way (its own match on centre, size and world generator).
/// Generate only reads the data (it copies the heights and mask out), so handing it back is safe.
///
/// Rules, so the data can only ever serve the zone it was built for, once:
///   - <see cref="Take"/> answers only for the zone it was kept for, and forgets it either way;
///   - <see cref="Drop"/> runs at the end of every zone, so data a zone never handed back goes with it;
///   - a hand-back goes to the newest end of the ready list (<see cref="HandBack{T}"/>), the end the builder evicts
///     last, and whatever is left unused is taken back off it (<see cref="Withdraw{T}"/>). The caller still asks the
///     game whether the zone's terrain is ready before making the terrain, so a hand-back the builder evicted after
///     all is a zone that comes back later; only an eviction between that answer and the terrain being made (the
///     builder's thread runs meanwhile) still costs a wait on the main thread.
/// Pure: ServerTerrainBake passes the builder's ready list, under the builder's lock.
/// </summary>
public sealed class ServerBakeKeptBuild<TData> where TData : class
{
    private bool m_has;
    private Vector2s m_zone;
    private TData? m_data;

    /// <summary>Keep the data a zone's terrain was just made from (nothing kept for null).</summary>
    public void Keep(Vector2s zone, TData? data)
    {
        if (data == null) { Drop(); return; }
        m_has = true;
        m_zone = zone;
        m_data = data;
    }

    /// <summary>Forget whatever is kept.</summary>
    public void Drop()
    {
        m_has = false;
        m_zone = default;
        m_data = null;
    }

    /// <summary>The kept data, for <paramref name="zone"/> only and only when <paramref name="enabled"/>; null
    /// otherwise. Forgets it either way: the data is handed back at most once.</summary>
    public TData? Take(Vector2s zone, bool enabled)
    {
        TData? data = m_has && enabled && m_zone.Equals(zone) ? m_data : null;
        Drop();
        return data;
    }

    /// <summary>Put data back on the builder's ready list, at the newest end (the builder evicts from the front), once.</summary>
    public static void HandBack<T>(List<T> ready, T data) where T : class
    {
        if (!ready.Contains(data)) ready.Add(data);
    }

    /// <summary>Take handed-back data off the ready list again if nothing used it; true when it was still there.</summary>
    public static bool Withdraw<T>(List<T> ready, T data) where T : class => ready.Remove(data);
}
