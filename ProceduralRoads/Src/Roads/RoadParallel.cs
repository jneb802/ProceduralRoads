namespace ProceduralRoads;

/// <summary>
/// Worker limits for island generation and candidate pricing. Several islands
/// use one worker each; a single island can instead price candidates in parallel.
/// Building and joining stay ordered within each island because they read the
/// growing road network.
///
/// 0 means "decide from the machine". 1 disables it and restores exactly the
/// serial behaviour, which is what the determinism tests run against.
/// </summary>
public static class RoadParallel
{
    public static int Configured = 0;

    /// <summary>How many islands may be built at once. Islands are isolated
    /// networks -- no road on one can join a road on another, and the network
    /// hash is computed in canonical order -- so building them together gives
    /// the same roads as building them in turn. The one owner of server work (docs/OWNERS.md).</summary>
    public static int IslandWorkers =>
        Configured > 0 ? Configured : System.Math.Max(1, System.Environment.ProcessorCount - 1);

    /// <summary>How many islands are being built right now. Set before the
    /// island loop so pricing knows whether there is anything spare.</summary>
    public static int IslandsRunning = 1;

    /// <summary>Pricing threads WITHIN one island. Islands in parallel already
    /// use the machine, and nesting the two only oversubscribes it -- but a
    /// world with a single island has nothing to spread, so there the threads
    /// go back to pricing.</summary>
    public static int PricingWorkers => IslandsRunning > 1 ? 1 : IslandWorkers;

    /// <summary>Islands of this generation not finished yet (<see cref="Islands"/>), island workers waiting on work laid ahead
    /// for them, and the work laid ahead now (RoadStairRuns.LayoutsAhead).</summary>
    private static int s_left, s_waiting, s_ahead;

    /// <summary>A generation's islands are about to be built (<paramref name="count"/> of them).</summary>
    internal static void Islands(int count) => System.Threading.Volatile.Write(ref s_left, count);

    /// <summary>An island worker finished its island.</summary>
    internal static void IslandDone() => System.Threading.Interlocked.Decrement(ref s_left);

    /// <summary>An island worker waits (true) for work laid ahead for it, or goes on (false).</summary>
    internal static void Waiting(bool waits) => System.Threading.Interlocked.Add(ref s_waiting, waits ? 1 : -1);

    /// <summary>Workers spare now for the last island's work laid ahead: the last island left of a generation gets the workers
    /// the others left (<see cref="IslandWorkers"/>, less itself unless it waits, less those laying ahead), as a world of one
    /// island gets every worker for its pricing (<see cref="PricingWorkers"/>); none while two or more islands are being built.</summary>
    internal static int Spare => SpareNow(System.Threading.Volatile.Read(ref s_ahead));

    /// <summary>The current spare-worker count for queries and reservations.</summary>
    private static int SpareNow(int ahead)
    {
        int left = System.Threading.Volatile.Read(ref s_left);
        if (left != 1) return 0;
        return IslandWorkers - (1 - System.Threading.Volatile.Read(ref s_waiting)) - ahead;
    }

    /// <summary>Takes a spare worker to lay ahead on, if there is one.</summary>
    internal static bool TakeSpare()
    {
        while (true)
        {
            int ahead = System.Threading.Volatile.Read(ref s_ahead);
            if (SpareNow(ahead) <= 0) return false;
            if (System.Threading.Interlocked.CompareExchange(ref s_ahead, ahead + 1, ahead) == ahead) return true;
        }
    }

    /// <summary>Gives back a worker <see cref="TakeSpare"/> took.</summary>
    internal static void ReturnSpare() => System.Threading.Interlocked.Decrement(ref s_ahead);
}
