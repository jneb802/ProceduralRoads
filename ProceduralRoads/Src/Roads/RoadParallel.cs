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
    /// the same roads as building them in turn.</summary>
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
}
