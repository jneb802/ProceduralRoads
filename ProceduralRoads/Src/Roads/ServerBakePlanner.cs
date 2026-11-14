using System;
using System.Collections.Generic;
using UnityEngine;

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
    /// <summary>The ghost pass's probe boundary: a roadless zone is still probed when a nearby structure needs rock
    /// cleared. The callback runs the native rock pass once eligibility is known.</summary>
    internal static int ProbeGhostRocks(Vector3 centre, float radius, bool settle, Func<int> probe)
    {
        if (!settle && RoadSpatialGrid.GetRoadPointsNearPosition(centre, radius).Count == 0 &&
            !StructurePlans.AnyStructureNear(centre, radius)) return 0;
        return probe();
    }

    /// <summary>Every zone the network owes work: the zones its roads write terrain into (<paramref name="roadZones"/> of them),
    /// those its structures stand in, and those whose vegetation its roads or structures reach (RoadClearAreaManager.ZonesReached,
    /// StructurePlans.ZonesWithWalks): the bake's queue and road_bake vegetation alike.</summary>
    internal static HashSet<Vector2s> NetworkZones(out int roadZones)
    {
        HashSet<Vector2s> zones = RoadSpatialGrid.GetZonesWithRoadPoints();
        roadZones = zones.Count;
        foreach (Vector2s zone in StructurePlans.PlannedZones)
            zones.Add(zone);
        zones.UnionWith(RoadClearAreaManager.ZonesReached());
        zones.UnionWith(StructurePlans.ZonesWithWalks());
        return zones;
    }

    public enum Action
    {
        /// <summary>No road network: nothing to write.</summary>
        NoNetwork,
        /// <summary>The zone has not been generated. It is written while it
        /// is generated (the ghost-generation hook), not before.</summary>
        LeaveToGeneration,
        /// <summary>The zone was generated before this road network reached
        /// the world (NetworkCutoff): its players' terrain is left exactly as
        /// it is, whatever its compiler carries.</summary>
        GeneratedBeforeNetwork,
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
    /// What a write the bake asked of the terrain writer did (<paramref name="carries"/>: the compiler carries the current
    /// roads after it; <paramref name="outcome"/>: RoadTerrainModifier.LastWriteOutcome), for every compiler it writes
    /// through (a live one, a saved or new one on a temporary terrain, a ghost zone's): stamped with nothing to write (the
    /// writer stamps that too, RoadTerrainModifier.AppliedVersionHash), nothing to write; stamped, written; anything else, a
    /// failed write. A compiler with nothing to write is kept, stamped and empty, so no later load writes the zone again.
    /// Ground not known inside a location is no outcome of its own: the writer says it as an error and writes the rest
    /// (RoadTerrainModifier.SayUnknownGround); nothing waits for it.
    /// </summary>
    public static WriteReport ReportOf(bool carries, RoadTerrainModifier.WriteOutcome outcome) =>
        !carries ? WriteReport.Failed
        : outcome == RoadTerrainModifier.WriteOutcome.NothingToWrite ? WriteReport.NothingToWrite
        : WriteReport.Written;

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
                // NoNetwork, LeaveToGeneration, GeneratedBeforeNetwork, LeaveToLiveZone, AlreadyCurrent:
                // nothing for the repair queue to carry. The generation and
                // live-zone hooks own those, and a ghost failure there records
                // the zone again.
                return RepairOutcome.Resolved;
        }
    }

    /// <summary>
    /// Whether a zone still owes the vegetation pass anything: generated, not yet cleared for this network, and not a zone
    /// the network protects (<paramref name="excluded"/>: NetworkCutoff.Excludes). A protected zone keeps everything that
    /// grows or lies in it, a stump or log on an old road too: automatic work leaves it alone.
    /// </summary>
    public static bool VegetationOwed(bool generated, bool cleared, bool excluded) =>
        generated && !cleared && !excluded;

    /// <summary>Where a zone's clearing stands, each a distinct answer: stopped after a fault, protected (the network
    /// leaves it as it is), not generated (generation clears it), completed, owed now, or owed and waiting for players near
    /// it to leave.</summary>
    public enum Clearing { Stopped, Protected, NotGenerated, Completed, Owed, WaitingForPlayer }

    /// <summary>
    /// The bake's current assessment of a zone's clearing, read only: the server bake acts on it (ServerTerrainBake's
    /// vegetation pass) and road_bake zone and road_bake vegetation report it, so the report says what the pass would do
    /// now. <paramref name="work"/> (whether the owed clearing reaches anything: road areas, road points, saved rocks)
    /// and <paramref name="playerNear"/> are asked only of a zone that owes it (<see cref="VegetationOwed"/>), the
    /// player only where there is work. Not a promise: the pass assesses again when it runs. The one owner of the bake
    /// assessment (docs/OWNERS.md).
    /// </summary>
    public static Clearing AssessClearing(bool stopped, bool generated, bool cleared, bool excluded, Func<bool> work,
        Func<bool> playerNear)
    {
        if (stopped) return Clearing.Stopped;
        if (!VegetationOwed(generated, cleared, excluded))
            return excluded ? Clearing.Protected : !generated ? Clearing.NotGenerated : Clearing.Completed;
        return work() && playerNear() ? Clearing.WaitingForPlayer : Clearing.Owed;
    }

    /// <summary>
    /// Whether a zone owed its vegetation pass also has its saved rocks
    /// cleared: only where the server does not have the zone loaded (a loaded zone's rocks are live objects,
    /// which the half-second pass of RoadRockClearing clears).
    /// </summary>
    public static bool SavedRocksOwed(bool loadedHere) => !loadedHere;

    /// <summary>Which saved objects the rock pass brings alive: the clearable rocks and their fractured copies (a mined or carved rock), never a player's.</summary>
    public static bool BringsRockAlive(string prefab, bool playerCreated, IReadOnlyDictionary<string, List<string>> fracturedFrom) =>
        !playerCreated && RoadRockPolicy.IsClearableRock(prefab, fracturedFrom);

    /// <summary>What the server knows about one saved compiler ZDO.</summary>
    public readonly struct Compiler
    {
        /// <summary>The road network version stamped on it (0 = none).</summary>
        public readonly int AppliedVersion;
        /// <summary>The owning peer's session id (0 = nobody).</summary>
        public readonly long Owner;
        /// <summary>Whether that owner is connected and has the zone in its active area.</summary>
        public readonly bool OwnerActiveHere;

        /// <summary>It carries the current roads (<see cref="RoadTerrainModifier.CarriesCurrentRoads"/>): stamped
        /// with the network's version, or with an ancestor of it and no new road points in this zone.</summary>
        public readonly bool Current;

        public Compiler(int appliedVersion, long owner, bool ownerActiveHere, bool current = false)
        {
            AppliedVersion = appliedVersion;
            Owner = owner;
            OwnerActiveHere = ownerActiveHere;
            Current = current;
        }
    }

    public static Action Decide(int networkVersion, bool generated, bool generatedBeforeNetwork, bool loadedHere,
        IReadOnlyList<Compiler> compilers, long mySession)
    {
        if (networkVersion == 0)
            return Action.NoNetwork;
        if (!generated)
            return Action.LeaveToGeneration;
        // Before any compiler fact: an unstamped compiler here is the player's
        // terrain (or a released build's roads), never ours to rewrite.
        if (generatedBeforeNetwork)
            return Action.GeneratedBeforeNetwork;
        if (compilers.Count > 1)
            return Action.DuplicateCompilers;
        // A loaded zone with no compiler yet gets one from the zone-spawn hook.
        if (compilers.Count == 0)
            return loadedHere ? Action.LeaveToLiveZone : Action.CreateCompiler;

        Compiler compiler = compilers[0];
        if (compiler.Current)
            return Action.AlreadyCurrent;
        // A live compiler of another owner is never taken (its owner may be
        // editing it); a saved one is taken unless its owner is active here.
        // Neither wait spends a failed-write attempt; the zone stays pending.
        if (compiler.Owner != 0 && compiler.Owner != mySession && (loadedHere || compiler.OwnerActiveHere))
            return Action.WaitForOwner;
        // Loaded here with a stale compiler of our own: write through the live
        // one. Bringing the saved ZDO alive on a temporary terrain would put a
        // second compiler in a zone that already has one.
        return loadedHere ? Action.WriteLiveCompiler : Action.WriteSavedCompiler;
    }
}
