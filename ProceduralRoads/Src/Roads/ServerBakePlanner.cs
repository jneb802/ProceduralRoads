using System.Collections.Generic;

namespace ProceduralRoads;

/// <summary>
/// What the server does about one zone's road terrain, decided from facts
/// about the zone alone. Pure logic: ServerTerrainBake gathers the facts from
/// the running game and carries the decision out.
///
/// Road terrain reaches every client as vanilla data: the zone's terrain
/// compiler (one _TerrainCompiler ZDO per zone) carries the height and paint
/// changes, and an unmodded client applies them to the terrain it generates
/// itself. The server therefore has to write that compiler for every zone a
/// road touches, including zones it never loads itself, and it must never make
/// a second compiler for a zone: the game destroys "another terrain compiler in
/// this area" on sight.
/// </summary>
public static class ServerBakePlanner
{
    public enum Action
    {
        /// <summary>No road network: nothing to write.</summary>
        NoNetwork,
        /// <summary>The zone has not been generated. It is written while it
        /// is generated (the ghost-generation hook), not before.</summary>
        LeaveToGeneration,
        /// <summary>The zone is loaded on this peer and has no compiler yet;
        /// the zone-spawn hook makes its one compiler and writes it.</summary>
        LeaveToLiveZone,
        /// <summary>The zone is loaded on this peer and its compiler is alive
        /// and writable: write through that one. Never make a second compiler
        /// for a zone the game already has in hand.</summary>
        WriteLiveCompiler,
        /// <summary>Its compiler already carries the current network.</summary>
        AlreadyCurrent,
        /// <summary>More than one saved compiler: writing either would leave
        /// the other to fight it, so neither is touched.</summary>
        DuplicateCompilers,
        /// <summary>A foreign owner still prevents a write. For a live compiler
        /// wait for release; for a saved compiler wait while its owner is active
        /// in the zone. Taking a live compiler could race player edits.</summary>
        WaitForOwner,
        /// <summary>No compiler yet: create the zone's one compiler.</summary>
        CreateCompiler,
        /// <summary>Write the roads into the zone's saved compiler.</summary>
        WriteSavedCompiler,
    }

    /// <summary>What a write actually did, where one was attempted at all.</summary>
    public enum WriteReport
    {
        /// <summary>No write was tried: something it needed was missing.</summary>
        NotAttempted,
        Written,
        NothingToWrite,
        Failed,
    }

    /// <summary>
    /// What the queue owes a zone it is repairing, once it has looked at it.
    /// Every path out of processing lands on exactly one of these, so a zone
    /// handed over for repair always gets an answer and can never sit in the
    /// ledger marked as queued with nothing coming.
    /// </summary>
    public enum RepairOutcome
    {
        /// <summary>Written, nothing to write, or not this queue's work: stop watching it.</summary>
        Resolved,
        /// <summary>What it needs is not there yet; it stays queued and comes back.</summary>
        Waiting,
        /// <summary>A write was tried and failed: spend one of its attempts.</summary>
        FailedWrite,
        /// <summary>Nothing here can fix it; a person must. Stop watching, and say so.</summary>
        Terminal,
    }

    /// <summary>
    /// Map what the queue decided and what its write did onto the repair
    /// ledger's four states. Waiting for a prerequisite -- an owner to leave,
    /// the game to build terrain, a live compiler to appear -- is not a
    /// failure and costs a zone nothing; only an attempted write that failed
    /// does. Duplicate compilers are terminal: the queue refuses to touch
    /// either of them, so retrying would repeat that refusal forever.
    /// </summary>
    public static RepairOutcome ResolveRepair(Action action, WriteReport report)
    {
        switch (action)
        {
            case Action.DuplicateCompilers:
                return RepairOutcome.Terminal;
            case Action.WaitForOwner:
                return RepairOutcome.Waiting;
            case Action.CreateCompiler:
            case Action.WriteSavedCompiler:
            case Action.WriteLiveCompiler:
                switch (report)
                {
                    case WriteReport.Written:
                    case WriteReport.NothingToWrite:
                        return RepairOutcome.Resolved;
                    case WriteReport.Failed:
                        return RepairOutcome.FailedWrite;
                    default:
                        return RepairOutcome.Waiting;
                }
            default:
                // NoNetwork, LeaveToGeneration, LeaveToLiveZone, AlreadyCurrent:
                // nothing for the repair queue to carry. The generation and
                // live-zone hooks own those, and a ghost failure there records
                // the zone again.
                return RepairOutcome.Resolved;
        }
    }

    /// <summary>What the server knows about one saved compiler ZDO.</summary>
    public readonly struct Compiler
    {
        /// <summary>The road network version stamped on it (0 = none).</summary>
        public readonly int AppliedVersion;
        /// <summary>The owning peer's session id (0 = nobody).</summary>
        public readonly long Owner;
        /// <summary>Whether that owner is connected and has the zone in its active area.</summary>
        public readonly bool OwnerActiveHere;

        /// <summary>The saved stamp is an ancestor, with no new road points in this zone.</summary>
        public readonly bool UnchangedByAppends;

        public Compiler(int appliedVersion, long owner, bool ownerActiveHere, bool unchangedByAppends = false)
        {
            AppliedVersion = appliedVersion;
            Owner = owner;
            OwnerActiveHere = ownerActiveHere;
            UnchangedByAppends = unchangedByAppends;
        }
    }

    public static Action Decide(int networkVersion, bool generated, bool loadedHere,
        IReadOnlyList<Compiler> compilers, long mySession)
    {
        if (networkVersion == 0)
            return Action.NoNetwork;
        if (!generated)
            return Action.LeaveToGeneration;
        if (compilers.Count > 1)
            return Action.DuplicateCompilers;
        // A loaded zone with no compiler yet gets one from the zone-spawn hook.
        if (compilers.Count == 0)
            return loadedHere ? Action.LeaveToLiveZone : Action.CreateCompiler;

        Compiler compiler = compilers[0];
        if (compiler.AppliedVersion == networkVersion || (compiler.AppliedVersion != 0 && compiler.UnchangedByAppends))
            return Action.AlreadyCurrent;
        // Match the live writer's conservative rule: it never takes a live
        // compiler from another owner, even outside that owner's active area.
        // Saved compilers retain their existing inactive-owner takeover policy.
        // Neither wait spends a failed-write attempt; the zone stays pending.
        if (compiler.Owner != 0 && compiler.Owner != mySession && (loadedHere || compiler.OwnerActiveHere))
            return Action.WaitForOwner;
        // Loaded here with a stale compiler of our own: write through the live
        // one. Bringing the saved ZDO alive on a temporary terrain would put a
        // second compiler in a zone that already has one.
        return loadedHere ? Action.WriteLiveCompiler : Action.WriteSavedCompiler;
    }
}
