using System;
using System.Collections.Generic;

namespace ProceduralRoads;

/// <summary>
/// Unexpected exceptions are not ordinary failed terrain writes: they may
/// occur in bridge or vegetation work after terrain has already succeeded.
/// Stop only the affected zone until an explicit requeue or a new network.
/// Keep a global queue fault separate from the operator's bake setting so
/// it cannot disable future ghost generation or prevent manual recovery.
/// </summary>
public sealed class ServerBakeFaults
{
    private readonly HashSet<Vector2s> m_quarantined = new();
    public int Count => m_quarantined.Count;
    public string? PauseReason { get; private set; }
    public bool IsPaused => PauseReason != null;
    public bool IsQuarantined(Vector2s zone) => m_quarantined.Contains(zone);

    public void Pause(Exception error) => PauseReason = error.Message;

    public void Clear()
    {
        m_quarantined.Clear();
        PauseReason = null;
    }

    /// <summary>
    /// Retire all retries for a quarantined zone, including any later deferral
    /// from another phase. The quarantine outlives the repair-ledger entry.
    /// On success the work itself owns its normal retry/deferral outcome.
    /// </summary>
    public bool TryProcessZone<T>(Vector2s zone, Func<T> work,
        GhostRepairLedger repairs, out T result, out Exception? error)
    {
        result = default!;
        error = null;
        if (IsQuarantined(zone))
        {
            repairs.Terminal(zone);
            return false;
        }
        try
        {
            result = work();
            return true;
        }
        catch (Exception ex)
        {
            m_quarantined.Add(zone);
            repairs.Terminal(zone);
            error = ex;
            return false;
        }
    }
}
