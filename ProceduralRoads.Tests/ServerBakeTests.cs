using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Road terrain written by the server for clients without the mod: which
/// zones, what to do with each, and the rule that a zone is stamped as
/// carrying its roads only once its compiler has saved them. The Unity side
/// (temporary terrain, ghost init) is ServerTerrainBake and is exercised in
/// game; everything it decides is decided here.
/// </summary>
public class ServerBakeTests
{
    private const int Version = 12345;
    private const long Me = 1;

    private static List<ServerBakePlanner.Compiler> Compilers(params ServerBakePlanner.Compiler[] compilers) =>
        new(compilers);

    private static ServerBakePlanner.Action Decide(bool generated, bool loadedHere,
        List<ServerBakePlanner.Compiler> compilers, int version = Version) =>
        ServerBakePlanner.Decide(version, generated, loadedHere, compilers, Me);

    [Fact]
    public void NoNetworkMeansNothingToWrite()
    {
        Assert.Equal(ServerBakePlanner.Action.NoNetwork, Decide(true, false, Compilers(), version: 0));
    }

    [Fact]
    public void AnUngeneratedZoneIsLeftToItsGeneration()
    {
        Assert.Equal(ServerBakePlanner.Action.LeaveToGeneration, Decide(false, false, Compilers()));
        Assert.Equal(ServerBakePlanner.Action.LeaveToGeneration,
            Decide(false, false, Compilers(new ServerBakePlanner.Compiler(0, 0, false))));
    }

    [Fact]
    public void AZoneLoadedHereIsLeftToTheLiveZoneHooks()
    {
        // Nothing saved yet: the zone-spawn hook makes its one compiler.
        Assert.Equal(ServerBakePlanner.Action.LeaveToLiveZone, Decide(true, true, Compilers()));
    }

    /// <summary>
    /// A loaded zone is not evidence its terrain was written. The live write is
    /// an Awake postfix that returns without writing when another peer owns the
    /// compiler, and no later ownership transfer makes Awake run again -- so a
    /// stale compiler in a loaded zone must stay pending, not be counted done.
    /// </summary>
    [Fact]
    public void AStaleCompilerInALoadedZoneIsNeverCountedAsDone()
    {
        // Owned by a peer standing there: wait for them, whether or not it is loaded here.
        Assert.Equal(ServerBakePlanner.Action.WaitForOwner,
            Decide(true, true, Compilers(new ServerBakePlanner.Compiler(0, 7, ownerActiveHere: true))));

        // Ours, or nobody's, and the zone is live: write through the live compiler
        // rather than raising a second one on a temporary terrain.
        Assert.Equal(ServerBakePlanner.Action.WriteLiveCompiler,
            Decide(true, true, Compilers(new ServerBakePlanner.Compiler(0, 0, false))));
        Assert.Equal(ServerBakePlanner.Action.WriteLiveCompiler,
            Decide(true, true, Compilers(new ServerBakePlanner.Compiler(Version - 1, Me, ownerActiveHere: true))));
        // A live compiler still owned by another peer waits for actual release.
        Assert.Equal(ServerBakePlanner.Action.WaitForOwner,
            Decide(true, true, Compilers(new ServerBakePlanner.Compiler(0, 7, ownerActiveHere: false))));

        // Already carrying this network: nothing to do, loaded or not.
        Assert.Equal(ServerBakePlanner.Action.AlreadyCurrent,
            Decide(true, true, Compilers(new ServerBakePlanner.Compiler(Version, 0, false))));
        // Two compilers are left alone in a loaded zone too.
        Assert.Equal(ServerBakePlanner.Action.DuplicateCompilers,
            Decide(true, true, Compilers(new ServerBakePlanner.Compiler(0, 0, false),
                new ServerBakePlanner.Compiler(0, 0, false))));
    }

    [Fact]
    public void AGeneratedZoneWithoutACompilerGetsItsOneCompiler()
    {
        Assert.Equal(ServerBakePlanner.Action.CreateCompiler, Decide(true, false, Compilers()));
    }

    [Fact]
    public void AStampedCompilerIsLeftAloneAndAnOldStampIsRewritten()
    {
        Assert.Equal(ServerBakePlanner.Action.AlreadyCurrent,
            Decide(true, false, Compilers(new ServerBakePlanner.Compiler(Version, 0, false))));
        Assert.Equal(ServerBakePlanner.Action.WriteSavedCompiler,
            Decide(true, false, Compilers(new ServerBakePlanner.Compiler(Version - 1, 0, false))));
        Assert.Equal(ServerBakePlanner.Action.WriteSavedCompiler,
            Decide(true, false, Compilers(new ServerBakePlanner.Compiler(0, 0, false))));
    }

    [Fact]
    public void TwoSavedCompilersAreNeitherOfThemWritten()
    {
        Assert.Equal(ServerBakePlanner.Action.DuplicateCompilers,
            Decide(true, false, Compilers(new ServerBakePlanner.Compiler(0, 0, false),
                new ServerBakePlanner.Compiler(0, 0, false))));
    }

    [Fact]
    public void APlayerInTheZoneKeepsTheirCompilerUntilTheyLeave()
    {
        // Owned by a connected peer standing in the zone: wait.
        Assert.Equal(ServerBakePlanner.Action.WaitForOwner,
            Decide(true, false, Compilers(new ServerBakePlanner.Compiler(0, 7, ownerActiveHere: true))));
        // Their owner gone or elsewhere: write it.
        Assert.Equal(ServerBakePlanner.Action.WriteSavedCompiler,
            Decide(true, false, Compilers(new ServerBakePlanner.Compiler(0, 7, ownerActiveHere: false))));
        // Ours already: write it.
        Assert.Equal(ServerBakePlanner.Action.WriteSavedCompiler,
            Decide(true, false, Compilers(new ServerBakePlanner.Compiler(0, Me, ownerActiveHere: true))));
        // Already current is current whoever owns it.
        Assert.Equal(ServerBakePlanner.Action.AlreadyCurrent,
            Decide(true, false, Compilers(new ServerBakePlanner.Compiler(Version, 7, ownerActiveHere: true))));
    }

    [Fact]
    public void TheZonesWithRoadPointsAreExactlyTheZonesThatHaveThem()
    {
        var world = new SyntheticWorld { HasRiver = false, HasMountain = false };
        WorldGenerator.instance = world;
        try
        {
            RoadSpatialGrid.Clear();
            // A 4 m diagonal road, and a 2 m road running along the border
            // between zone rows 0 and 1 (z = 32), so points sit at every kind
            // of zone edge.
            var diagonal = new List<Vector2>();
            for (float t = -150f; t <= 150f; t += 10f)
                diagonal.Add(new Vector2(t, t * 0.7f + 5f));
            RoadSpatialGrid.AddRoadPath(diagonal, 4f, world);
            var border = new List<Vector2>();
            for (float x = -100f; x <= 100f; x += 10f)
                border.Add(new Vector2(x, 32f));
            RoadSpatialGrid.AddRoadPath(border, 2f, world);
            RoadSpatialGrid.FinalizeRoadNetwork();

            HashSet<Vector2s> zones = RoadSpatialGrid.GetZonesWithRoadPoints();
            Assert.NotEmpty(zones);
            Assert.All(zones, z =>
            {
                Assert.InRange(z.x, -6, 6);
                Assert.InRange(z.y, -6, 6);
            });
            for (int y = -6; y <= 6; y++)
            {
                for (int x = -6; x <= 6; x++)
                {
                    var zone = new Vector2s(x, y);
                    Assert.Equal(RoadSpatialGrid.GetRoadPointsInZone(zone).Count > 0, zones.Contains(zone));
                }
            }
            // The border road lies in both rows.
            Assert.Contains(new Vector2s(0, 0), zones);
            Assert.Contains(new Vector2s(0, 1), zones);
        }
        finally
        {
            RoadSpatialGrid.Clear();
            WorldGenerator.instance = null;
        }
    }

    [Fact]
    public void AZoneWhoseCompilerWillNotSaveIsNotStamped()
    {
        var world = new SyntheticWorld { HasRiver = false, HasMountain = false };
        WorldGenerator.instance = world;
        ZDOMan.instance = new ZDOMan();
        try
        {
            RoadSpatialGrid.Clear();
            var path = new List<Vector2>();
            for (float x = -40f; x <= 40f; x += 8f)
                path.Add(new Vector2(x, 0f));
            RoadSpatialGrid.AddRoadPath(path, 4f, world);
            RoadSpatialGrid.FinalizeRoadNetwork();
            var zone = new Vector2s(0, 0);
            List<RoadSpatialGrid.RoadPoint> points = RoadSpatialGrid.GetRoadPointsInZone(zone);
            Heightmap hm = Heightmap.CreateForZone(zone, 64);
            Heightmap.Registered = hm;
            TerrainComp tc = hm.m_terrainComp!;
            tc.m_nview.GetZDO().SetOwner(2); // another peer's

            // The explicit path writes the arrays whatever it is given; the
            // game refuses the save, and a stamp now would be a lie.
            RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, points, hm, tc);
            Assert.Equal(0, tc.SaveCount);
            Assert.Null(tc.m_nview.GetZDO().GetByteArray(ZDOVars.s_TCData));
            Assert.False(RoadTerrainModifier.CarriesCurrentRoads(tc));

            // Once it is ours the same write saves, and only then is stamped.
            tc.m_nview.GetZDO().SetOwner(ZDOMan.instance.m_sessionID);
            RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, points, hm, tc);
            Assert.Equal(1, tc.SaveCount);
            Assert.NotNull(tc.m_nview.GetZDO().GetByteArray(ZDOVars.s_TCData));
            Assert.True(RoadTerrainModifier.CarriesCurrentRoads(tc));
        }
        finally
        {
            Heightmap.Registered = null;
            RoadSpatialGrid.Clear();
            ZDOMan.instance = null;
            WorldGenerator.instance = null;
        }
    }

    /// <summary>
    /// A failed ghost write is the zone's only chance unless something keeps it:
    /// vanilla finishes generating it, so it never takes the new-zone path
    /// again, and the queue has already passed it by.
    /// </summary>
    // ---- the scheduler: when a zone comes round again, and how many tries ----
    //
    // These drive the real scheduler with a fake clock, because the bug they
    // guard against was an interaction rather than a rule: a failed write made
    // the ledger ready for its next poll AND entered a separate ten-second wait
    // list. When the budget ran out the ledger dropped the zone, the surviving
    // wait entry queued it again, and a zone with no entry was treated as
    // having unlimited retries. Work carried on after "given up", forever.

    private const float Delay = 10f;

    /// <summary>
    /// One look at a zone, mapped exactly as ServerTerrainBake.Resolve maps it:
    /// the real ResolveRepair, the real scheduler. Returns whether it is worth
    /// coming back.
    /// </summary>
    private static bool Apply(GhostRepairLedger ledger, Vector2s zone, ServerBakePlanner.Action action,
        ServerBakePlanner.WriteReport report, float now)
    {
        switch (ServerBakePlanner.ResolveRepair(action, report))
        {
            case ServerBakePlanner.RepairOutcome.Resolved:
                ledger.Succeeded(zone);
                return true;
            case ServerBakePlanner.RepairOutcome.FailedWrite:
                return !ledger.FailedWrite(zone, now, Delay);
            case ServerBakePlanner.RepairOutcome.Terminal:
                ledger.Terminal(zone);
                return false;
            default:
                ledger.Defer(zone, now, Delay);
                return true;
        }
    }

    [Fact]
    public void AZoneWhoseGhostWriteFailedIsWrittenFromTheQueueInstead()
    {
        var ledger = new GhostRepairLedger();
        var zone = new Vector2s(4, -9);
        ledger.Record(zone);
        Assert.True(ledger.Holds(zone));

        // Still generating: nothing to hand over yet, and it is not forgotten.
        Assert.Empty(ledger.TakeReady(_ => false, 0f));
        Assert.Equal(1, ledger.Count);

        // Generated: it goes to the queue, and stays watched until the write
        // actually lands -- being handed over is not the same as fixed.
        Assert.Equal(new[] { zone }, ledger.TakeReady(_ => true, 0f));
        Assert.True(ledger.IsQueued(zone));

        // The queue wrote it: nothing left to watch, at any later time.
        ledger.Succeeded(zone);
        Assert.False(ledger.Holds(zone));
        Assert.Empty(ledger.TakeReady(_ => true, 1000f));
    }

    /// <summary>
    /// The budget belongs to failed WRITES, not to the clock. A zone can sit in
    /// the queue through a backlog or a terrain build; polling it must cost
    /// nothing and must never hand over a second copy.
    /// </summary>
    [Fact]
    public void PollingSpendsNothingAndNeverQueuesTheZoneTwice()
    {
        var ledger = new GhostRepairLedger();
        var zone = new Vector2s(4, -9);
        ledger.Record(zone);

        int queued = 0;
        for (int poll = 0; poll < 12; poll++)
            queued += ledger.TakeReady(_ => true, poll).Count;

        Assert.Equal(1, queued);
        Assert.True(ledger.Holds(zone));
        Assert.Equal(0, ledger.FailedWrites(zone));
    }

    /// <summary>
    /// Three failed live writes, then the clock run past every deadline any of
    /// them scheduled. Nothing may come back.
    /// </summary>
    [Fact]
    public void ThreeFailedLiveWritesStopForGoodEvenAfterEveryOldDeadlinePasses()
    {
        var ledger = new GhostRepairLedger();
        var zone = new Vector2s(7, 7);
        ledger.Record(zone);

        int writes = 0;
        float now = 0f;
        bool worthRetrying = true;
        while (worthRetrying && writes < 10)
        {
            List<Vector2s> ready = ledger.TakeReady(_ => true, now);
            if (ready.Count == 0)
            {
                now += 1f;
                continue;
            }
            Assert.Equal(new[] { zone }, ready);
            writes++;
            worthRetrying = Apply(ledger, zone, ServerBakePlanner.Action.WriteLiveCompiler,
                ServerBakePlanner.WriteReport.Failed, now);
        }

        Assert.Equal(GhostRepairLedger.MaxFailedWrites, writes);
        Assert.False(worthRetrying);
        Assert.False(ledger.Holds(zone));

        // Long past every deadline the failures ever set: still nothing.
        for (float later = now; later <= now + 1000f; later += 10f)
            Assert.Empty(ledger.TakeReady(_ => true, later));
    }

    /// <summary>
    /// A live write failing in an ordinary bake -- no ghost failure before it --
    /// used to have no ledger entry, and absence from the ledger was treated as
    /// permission to retry for ever. The first failure starts the budget.
    /// </summary>
    [Fact]
    public void AnOrdinaryFailedWriteGetsABudgetFromItsFirstFailure()
    {
        var ledger = new GhostRepairLedger();
        var zone = new Vector2s(3, 3);
        Assert.False(ledger.Holds(zone));

        Assert.True(Apply(ledger, zone, ServerBakePlanner.Action.WriteLiveCompiler,
            ServerBakePlanner.WriteReport.Failed, 0f));
        Assert.True(ledger.Holds(zone));
        Assert.Equal(1, ledger.FailedWrites(zone));

        Assert.True(Apply(ledger, zone, ServerBakePlanner.Action.WriteLiveCompiler,
            ServerBakePlanner.WriteReport.Failed, Delay));
        Assert.False(Apply(ledger, zone, ServerBakePlanner.Action.WriteLiveCompiler,
            ServerBakePlanner.WriteReport.Failed, Delay * 2));
        Assert.False(ledger.Holds(zone));
        Assert.Empty(ledger.TakeReady(_ => true, 1000f));
    }

    [Fact]
    public void TwoFailuresThenSuccessIsNotRevivedByTheClock()
    {
        var ledger = new GhostRepairLedger();
        var zone = new Vector2s(5, 5);
        ledger.Record(zone);

        Assert.True(Apply(ledger, zone, ServerBakePlanner.Action.WriteLiveCompiler,
            ServerBakePlanner.WriteReport.Failed, 0f));
        Assert.True(Apply(ledger, zone, ServerBakePlanner.Action.WriteLiveCompiler,
            ServerBakePlanner.WriteReport.Failed, Delay));

        // It works on the third go. The deadlines the failures left behind must
        // not bring the finished job back.
        Assert.True(Apply(ledger, zone, ServerBakePlanner.Action.WriteLiveCompiler,
            ServerBakePlanner.WriteReport.Written, Delay * 2));
        Assert.False(ledger.Holds(zone));
        for (float later = 0f; later <= 1000f; later += 10f)
            Assert.Empty(ledger.TakeReady(_ => true, later));
    }

    /// <summary>
    /// The production sequence, not the ledger alone: ProcessZone runs vegetation
    /// whenever terrain returns Done -- and a TERMINAL terrain failure returns
    /// Done. So on a generated, uncleared zone with a player standing near it,
    /// vegetation deferred the zone immediately after terrain gave up, and the
    /// deferral recreated the entry the give-up had just removed, with the count
    /// back at zero. Terrain then started again with a fresh three, for ever.
    /// Terrain's terminal state has to survive other work deferring the zone.
    /// </summary>
    [Fact]
    public void VegetationDeferralCannotRestartAnExhaustedTerrainBudget()
    {
        var ledger = new GhostRepairLedger();
        var zone = new Vector2s(8, 8);
        int writes = 0;
        float now = 0f;

        for (int round = 1; round <= 7; round++)
        {
            // ProcessTerrain refuses once terrain has given up -- the zone is
            // still here, but not for terrain.
            if (!ledger.HasGivenUp(zone))
            {
                writes++;
                ledger.FailedWrite(zone, now, Delay);
            }
            // ProcessVegetation: generated, uncleared, a peer nearby.
            ledger.Defer(zone, now, Delay);
            now += Delay;
        }

        Assert.Equal(GhostRepairLedger.MaxFailedWrites, writes);
        Assert.True(ledger.HasGivenUp(zone));

        // The peer leaves: the zone is handed over once more, terrain still
        // refuses, and vegetation finally clears. Something has to retire the
        // entry vegetation's own Defer created, or the zone stays pending for
        // the rest of the session and the bake never reports itself finished --
        // while terrain's give-up must survive that retirement.
        Assert.Equal(new[] { zone }, ledger.TakeReady(_ => true, now));
        Assert.True(ledger.HasGivenUp(zone));
        ledger.PendingWorkDone(zone);

        Assert.Equal(0, ledger.Count);
        Assert.True(ledger.HasGivenUp(zone));
        for (float later = now; later <= now + 1000f; later += 50f)
            Assert.Empty(ledger.TakeReady(_ => true, later));
        Assert.Equal(GhostRepairLedger.MaxFailedWrites, writes);
    }

    /// <summary>
    /// The already-cleared exit matters as much as the clearing one: a zone
    /// whose vegetation was done long ago still arrives here, and it must not
    /// leave a pending entry behind either.
    /// </summary>
    [Fact]
    public void VegetationThatWasAlreadyDoneAlsoRetiresThePendingEntry()
    {
        var ledger = new GhostRepairLedger();
        var zone = new Vector2s(11, 11);
        for (int i = 0; i < GhostRepairLedger.MaxFailedWrites; i++)
            ledger.FailedWrite(zone, i * Delay, Delay);
        ledger.Defer(zone, 0f, Delay);
        Assert.Equal(1, ledger.Count);

        ledger.PendingWorkDone(zone);

        Assert.Equal(0, ledger.Count);
        // Retiring the work is not terrain getting well again.
        Assert.True(ledger.HasGivenUp(zone));
    }

    /// <summary>
    /// Vegetation finishing must not cancel a retry TERRAIN is still owed.
    ///
    /// This is the ordinary order, not a corner: ProcessTerrain returns Done
    /// after a NONTERMINAL failed write -- a live compiler that took nothing,
    /// terrain the game never built, an ordinary failed write -- and
    /// ProcessZone runs the vegetation pass immediately afterwards, on the very
    /// entry that now carries terrain's retry. Retiring it left the zone
    /// neither held, nor given up, nor counted, so the bake reported
    /// "0 deferred, 0 pending" with the road unwritten.
    /// </summary>
    [Fact]
    public void VegetationCompletionDoesNotCancelTheFirstTerrainRetry()
    {
        var ledger = new GhostRepairLedger();
        var zone = new Vector2s(3, 4);

        Assert.False(ledger.FailedWrite(zone, 0f, Delay));
        Assert.Equal(1, ledger.Count);

        // The vegetation pass finds this zone already cleared and retires it.
        ledger.PendingWorkDone(zone);

        Assert.Equal(1, ledger.Count);
        Assert.True(ledger.Holds(zone));
        Assert.False(ledger.HasGivenUp(zone));
        Assert.Equal(1, ledger.FailedWrites(zone));
        Assert.Single(ledger.TakeReady(_ => true, Delay * 10f));
    }

    /// <summary>
    /// The same on the SECOND failure, and through the other exit -- the one
    /// where vegetation actually cleared on this pass rather than finding the
    /// work already done. A zone one attempt from its bound is exactly the one
    /// that must not lose its last try.
    /// </summary>
    [Fact]
    public void VegetationCompletionDoesNotCancelTheSecondTerrainRetry()
    {
        var ledger = new GhostRepairLedger();
        var zone = new Vector2s(5, 6);

        Assert.False(ledger.FailedWrite(zone, 0f, Delay));
        Assert.Single(ledger.TakeReady(_ => true, Delay * 2f));
        Assert.False(ledger.FailedWrite(zone, Delay * 2f, Delay));
        Assert.Equal(2, ledger.FailedWrites(zone));

        ledger.PendingWorkDone(zone);

        Assert.Equal(1, ledger.Count);
        Assert.False(ledger.HasGivenUp(zone));
        Assert.Equal(2, ledger.FailedWrites(zone));
        Assert.Single(ledger.TakeReady(_ => true, Delay * 10f));
    }

    /// <summary>
    /// And the refusal must not over-reach: an entry with no spent attempt is
    /// vegetation's own deferral, which is precisely what PendingWorkDone
    /// exists to close. Without this the bake would never report itself
    /// finished, which is the bug the method was added for.
    /// </summary>
    [Fact]
    public void VegetationsOwnDeferralIsStillRetiredWhenTerrainOwesNothing()
    {
        var ledger = new GhostRepairLedger();
        var zone = new Vector2s(7, 8);

        ledger.Defer(zone, 0f, Delay);
        Assert.Equal(1, ledger.Count);

        ledger.PendingWorkDone(zone);

        Assert.Equal(0, ledger.Count);
        Assert.False(ledger.HasGivenUp(zone));
        Assert.Empty(ledger.TakeReady(_ => true, Delay * 10f));
    }

    [Fact]
    public void ANewNetworkOrAnOutrightResolutionLiftsTheGiveUp()
    {
        var ledger = new GhostRepairLedger();
        var zone = new Vector2s(9, 9);
        for (int i = 0; i < GhostRepairLedger.MaxFailedWrites; i++)
            ledger.FailedWrite(zone, 0f, Delay);
        Assert.True(ledger.HasGivenUp(zone));

        // road_bake again / a new network version.
        ledger.Clear();
        Assert.False(ledger.HasGivenUp(zone));

        // And an explicit resolution does the same for one zone.
        for (int i = 0; i < GhostRepairLedger.MaxFailedWrites; i++)
            ledger.FailedWrite(zone, 0f, Delay);
        Assert.True(ledger.HasGivenUp(zone));
        ledger.Succeeded(zone);
        Assert.False(ledger.HasGivenUp(zone));
    }

    [Fact]
    public void ADuplicateCompilerRepairIsEndedRatherThanLeftQueued()
    {
        var ledger = new GhostRepairLedger();
        var zone = new Vector2s(6, 6);
        ledger.Record(zone);
        ledger.TakeReady(_ => true, 0f);

        Assert.False(Apply(ledger, zone, ServerBakePlanner.Action.DuplicateCompilers,
            ServerBakePlanner.WriteReport.NotAttempted, 0f));
        Assert.False(ledger.Holds(zone));
        Assert.Empty(ledger.TakeReady(_ => true, 1000f));
    }

    // ---- what the queue owes a zone it is repairing ----
    //
    // The ledger alone was not enough: the queue has paths that end without
    // writing anything, and a repair that leaves one of them unanswered stays
    // marked as queued for ever -- never handed over again, never given up.

    private static ServerBakePlanner.RepairOutcome Outcome(ServerBakePlanner.Action action,
        ServerBakePlanner.WriteReport report = ServerBakePlanner.WriteReport.NotAttempted) =>
        ServerBakePlanner.ResolveRepair(action, report);

    [Fact]
    public void WaitingForAPrerequisiteCostsTheZoneNothing()
    {
        // An owner standing in the zone, a compiler not up yet, terrain the
        // game has not built: all of them mean "come back", not "that failed".
        Assert.Equal(ServerBakePlanner.RepairOutcome.Waiting, Outcome(ServerBakePlanner.Action.WaitForOwner));
        Assert.Equal(ServerBakePlanner.RepairOutcome.Waiting, Outcome(ServerBakePlanner.Action.WriteLiveCompiler));
        Assert.Equal(ServerBakePlanner.RepairOutcome.Waiting, Outcome(ServerBakePlanner.Action.WriteSavedCompiler));
    }

    [Fact]
    public void AFailedWriteIsAFailedWriteWhicheverCompilerItWas()
    {
        Assert.Equal(ServerBakePlanner.RepairOutcome.FailedWrite,
            Outcome(ServerBakePlanner.Action.WriteLiveCompiler, ServerBakePlanner.WriteReport.Failed));
        Assert.Equal(ServerBakePlanner.RepairOutcome.FailedWrite,
            Outcome(ServerBakePlanner.Action.WriteSavedCompiler, ServerBakePlanner.WriteReport.Failed));
        Assert.Equal(ServerBakePlanner.RepairOutcome.FailedWrite,
            Outcome(ServerBakePlanner.Action.CreateCompiler, ServerBakePlanner.WriteReport.Failed));
    }

    [Fact]
    public void AWriteOrANoOpEndsTheRepairAndSoDoesWorkThatIsNotOurs()
    {
        Assert.Equal(ServerBakePlanner.RepairOutcome.Resolved,
            Outcome(ServerBakePlanner.Action.WriteLiveCompiler, ServerBakePlanner.WriteReport.Written));
        Assert.Equal(ServerBakePlanner.RepairOutcome.Resolved,
            Outcome(ServerBakePlanner.Action.WriteLiveCompiler, ServerBakePlanner.WriteReport.NothingToWrite));
        // Handed to the generation or live-zone hooks, or already carrying the
        // roads: the repair queue has nothing left to do about it.
        Assert.Equal(ServerBakePlanner.RepairOutcome.Resolved, Outcome(ServerBakePlanner.Action.LeaveToGeneration));
        Assert.Equal(ServerBakePlanner.RepairOutcome.Resolved, Outcome(ServerBakePlanner.Action.LeaveToLiveZone));
        Assert.Equal(ServerBakePlanner.RepairOutcome.Resolved, Outcome(ServerBakePlanner.Action.AlreadyCurrent));
        Assert.Equal(ServerBakePlanner.RepairOutcome.Resolved, Outcome(ServerBakePlanner.Action.NoNetwork));
    }

    /// <summary>
    /// Two saved compilers are refused the same way every time, so a repair
    /// waiting on that zone would wait for ever. It is ended and reported.
    /// </summary>
    [Fact]
    public void DuplicateCompilersEndTheRepairInsteadOfStrandingIt()
    {
        Assert.Equal(ServerBakePlanner.RepairOutcome.Terminal, Outcome(ServerBakePlanner.Action.DuplicateCompilers));
    }

    /// <summary>
    /// An owner that holds the compiler far longer than the wait: every round
    /// defers the zone again, none of them spends a write attempt, and the
    /// queue is never handed a second copy. (The three-failure sequence has its
    /// own fake-clock regression above.)
    /// </summary>
    [Fact]
    public void AnOwnerHeldLongerThanTheWaitSpendsNothing()
    {
        var ledger = new GhostRepairLedger();
        var zone = new Vector2s(7, 7);
        ledger.Record(zone);

        // An owner keeps hold of the compiler for far longer than the wait.
        // Every round defers it again; none of them costs an attempt, and the
        // queue is never given a second copy of the zone.
        float now = 0f;
        int handedOver = 0;
        for (int round = 0; round < 5; round++)
        {
            handedOver += ledger.TakeReady(_ => true, now).Count;
            Assert.True(Apply(ledger, zone, ServerBakePlanner.Action.WaitForOwner,
                ServerBakePlanner.WriteReport.NotAttempted, now));
            Assert.Equal(0, ledger.FailedWrites(zone));
            now += Delay * 1.5f;
        }
        Assert.Equal(5, handedOver);
        Assert.True(ledger.Holds(zone));

        // The owner finally leaves and the write lands: nothing is left behind.
        ledger.TakeReady(_ => true, now);
        Assert.True(Apply(ledger, zone, ServerBakePlanner.Action.WriteLiveCompiler,
            ServerBakePlanner.WriteReport.Written, now));
        Assert.False(ledger.Holds(zone));
    }

    [Theory]
    [InlineData(false, false, PeerAdmission.Verdict.WithoutMod, true)]
    [InlineData(false, true, PeerAdmission.Verdict.WithoutMod, true)]
    [InlineData(true, true, PeerAdmission.Verdict.SameVersion, true)]
    [InlineData(true, false, PeerAdmission.Verdict.VersionMismatch, false)]
    public void APeerWithoutTheModIsAdmittedAndAMismatchedOneIsNot(
        bool answered, bool matched, PeerAdmission.Verdict expected, bool admitted)
    {
        PeerAdmission.Verdict verdict = PeerAdmission.Decide(answered, matched);
        Assert.Equal(expected, verdict);
        Assert.Equal(admitted, PeerAdmission.Admits(verdict));
    }

    /// <summary>
    /// What the server decides at PeerInfo from the two lists it keeps. A peer
    /// on neither list never answered: no mod, admitted. A peer on the refused
    /// list answered with another version and is refused HERE, not only when
    /// its answer arrived; before this, refusal depended on the client acting
    /// on the error it was sent, and a mismatched client that did not was let
    /// in as if it had no mod.
    /// </summary>
    [Theory]
    [InlineData(false, false, PeerAdmission.Verdict.WithoutMod, true)]
    [InlineData(true, false, PeerAdmission.Verdict.SameVersion, true)]
    [InlineData(false, true, PeerAdmission.Verdict.VersionMismatch, false)]
    public void ARecordedMismatchIsRefusedWhenItsPeerInfoArrives(
        bool validated, bool refused, PeerAdmission.Verdict expected, bool admitted)
    {
        PeerAdmission.Verdict verdict = PeerAdmission.DecideFor(validated, refused);
        Assert.Equal(expected, verdict);
        Assert.Equal(admitted, PeerAdmission.Admits(verdict));
    }
}
