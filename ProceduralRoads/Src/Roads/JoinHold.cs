namespace ProceduralRoads;

/// <summary>
/// Holds joining players off while the world is held for the road network (the server or the hosting player builds it, or the host is asked about an explored world). A zone a player loads
/// before the network exists gets its roads later from the server bake, which levels the road over whatever was built
/// there meanwhile; a refused player can join again once generation has finished. The refusal is vanilla's own, so a
/// player without the mod understands it. The hosting player is not a joining peer and is never held.
/// </summary>
internal static class JoinHold
{
    /// <summary>The status a held peer is refused with: the game's "server full", which a vanilla client shows as
    /// "Server full", the nearest of its messages to "come back later".</summary>
    internal const ZNet.ConnectionStatus Status = ZNet.ConnectionStatus.ErrorFull;

    /// <summary>Why joins are held, with how far generation is; null when nothing holds them. They are held while the world is
    /// (<see cref="RoadLifecycleManager.HoldsTheWorld"/>): a generation building or stopping, or the host being asked about an
    /// explored world's first network. A zone streamed meanwhile would have no roads. The one owner of joining a network (docs/OWNERS.md).</summary>
    internal static string? Reason
    {
        get
        {
            if (!RoadLifecycleManager.HoldsTheWorld) return null;
            if (!RoadLifecycleManager.IsGenerating) return "waiting for the host's answer about the explored world";
            var progress = RoadLifecycleManager.Progress;
            return $"{progress.Text}, {progress.ElapsedSeconds:F0} s in";
        }
    }

    /// <summary>On a server, from the joining peer's PeerInfo: refuses it, as the game refuses a full server, and logs
    /// why, while the world is held for the network. False when the peer may join.</summary>
    internal static bool Refuse(ZRpc rpc)
    {
        string? reason = Reason;
        if (reason == null) return false;
        rpc.Invoke("Error", (int)Status);
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(
            $"Peer ({rpc.GetSocket().GetHostName()}) held off while the world is held for the road network ({reason}); " +
            "it is told the server is full and can join once the hold ends");
        return true;
    }
}
