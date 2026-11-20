namespace ProceduralRoads;

/// <summary>
/// Holds joining players off while the server (or the hosting player) builds its road network. A zone a player loads
/// before the network exists gets its roads later from the server bake, which levels the road over whatever was built
/// there meanwhile; a refused player can join again once generation has finished. The refusal is vanilla's own, so a
/// player without the mod understands it. The hosting player is not a joining peer and is never held.
/// </summary>
internal static class JoinHold
{
    /// <summary>The status a held peer is refused with: the game's "server full", which a vanilla client shows as
    /// "Server full", the nearest of its messages to "come back later".</summary>
    internal const ZNet.ConnectionStatus Status = ZNet.ConnectionStatus.ErrorFull;

    /// <summary>How far generation is, while joins are held; null when nothing holds them. Only a generation that is
    /// still building holds them: one stopping, failed, cancelled or finished lets players in.</summary>
    internal static string? Reason
    {
        get
        {
            if (!RoadLifecycleManager.IsGenerating) return null;
            var progress = RoadLifecycleManager.Progress;
            return progress.Phase is "Preparing" or "Generating" or "Finalizing"
                ? $"{progress.Text}, {progress.ElapsedSeconds:F0} s in"
                : null;
        }
    }

    /// <summary>On a server, from the joining peer's PeerInfo: refuses it, as the game refuses a full server, and logs
    /// why, while the network is being generated. False when the peer may join.</summary>
    internal static bool Refuse(ZRpc rpc)
    {
        string? reason = Reason;
        if (reason == null) return false;
        rpc.Invoke("Error", (int)Status);
        ProceduralRoadsPlugin.ProceduralRoadsLogger.LogInfo(
            $"Peer ({rpc.GetSocket().GetHostName()}) held off while the road network is generated ({reason}); " +
            "it is told the server is full and can join once generation finishes");
        return true;
    }
}
