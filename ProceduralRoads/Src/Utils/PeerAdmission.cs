namespace ProceduralRoads;

/// <summary>
/// Which peers the server lets in. Pure logic: VersionHandshake does the
/// networking.
///
/// The roads are vanilla data by the time a client sees them -- terrain
/// compiler height and paint, and bridges built from the game's own pieces --
/// so a client without the mod plays on a server with it and walks the same
/// roads. Such a client never answers the version check; that is not a reason
/// to turn it away. A client that does answer, with a different version,
/// still is: two builds of the mod would disagree about the roads they write.
/// </summary>
public static class PeerAdmission
{
    public enum Verdict
    {
        /// <summary>The same build of the mod: admitted.</summary>
        SameVersion,
        /// <summary>No mod on the client: admitted, it receives the roads as vanilla data.</summary>
        WithoutMod,
        /// <summary>A different build of the mod: turned away.</summary>
        VersionMismatch,
    }

    /// <param name="answeredVersionCheck">Whether the peer sent a version at all.</param>
    /// <param name="versionMatched">Whether the version it sent matched ours.</param>
    public static Verdict Decide(bool answeredVersionCheck, bool versionMatched)
    {
        if (!answeredVersionCheck)
            return Verdict.WithoutMod;
        return versionMatched ? Verdict.SameVersion : Verdict.VersionMismatch;
    }

    public static bool Admits(Verdict verdict) => verdict != Verdict.VersionMismatch;

    /// <summary>
    /// The verdict for a peer whose PeerInfo has arrived, from what the server
    /// recorded when (and if) the peer answered the version check: it is on
    /// the validated list, on the refused list, or on neither.
    /// </summary>
    public static Verdict DecideFor(bool validated, bool refused) =>
        Decide(answeredVersionCheck: validated || refused, versionMatched: validated);
}
