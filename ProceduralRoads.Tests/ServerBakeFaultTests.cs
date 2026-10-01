using System;
using Xunit;

namespace ProceduralRoads.Tests;

public class ServerBakeFaultTests
{
    [Fact]
    public void AThrowingZoneDoesNotStopTheNextZone()
    {
        var faults = new ServerBakeFaults();
        var repairs = new GhostRepairLedger();
        var bad = new Vector2s(1, 1);
        var healthy = new Vector2s(2, 1);
        repairs.Record(bad);
        repairs.TakeReady(_ => true, 0);
        Assert.False(faults.TryProcessZone<int>(bad, () => throw new InvalidOperationException("zone fault"),
            repairs, out _, out var error));
        Assert.NotNull(error);
        Assert.False(repairs.Holds(bad));
        Assert.False(faults.IsPaused);
        Assert.True(faults.TryProcessZone(healthy, () => 42, repairs, out var value, out error));
        Assert.Equal(42, value);
        Assert.Null(error);
        Assert.False(faults.IsQuarantined(healthy));
    }

    [Fact]
    public void RepeatedDeferralsCannotRestartAQuarantinedZone()
    {
        var faults = new ServerBakeFaults();
        var repairs = new GhostRepairLedger();
        var zone = new Vector2s(1, 1);
        int calls = 0;
        int Work() { calls++; throw new InvalidOperationException(); }
        for (int i = 0; i < 12; i++)
        {
            repairs.Defer(zone, i, 0);
            repairs.TakeReady(_ => true, i);
            Assert.False(faults.TryProcessZone(zone, Work, repairs, out _, out _));
            repairs.PendingWorkDone(zone);
            Assert.False(repairs.Holds(zone));
        }
        Assert.Equal(1, calls);
        Assert.Equal(1, faults.Count);
    }

    [Fact]
    public void AnExceptionAfterTerrainSuccessDoesNotSpendATerrainWriteAttempt()
    {
        var faults = new ServerBakeFaults();
        var repairs = new GhostRepairLedger();
        var zone = new Vector2s(1, 1);
        Assert.False(faults.TryProcessZone<int>(zone, () =>
        {
            repairs.Succeeded(zone);
            throw new InvalidOperationException("vegetation failed");
        }, repairs, out _, out _));
        Assert.Equal(0, repairs.FailedWrites(zone));
        Assert.False(repairs.HasGivenUp(zone));
        Assert.True(faults.IsQuarantined(zone));
    }

    [Fact]
    public void AHandledWriteFailureKeepsItsExistingRetry()
    {
        var faults = new ServerBakeFaults();
        var repairs = new GhostRepairLedger();
        var zone = new Vector2s(1, 1);
        Assert.True(faults.TryProcessZone(zone, () =>
        {
            repairs.FailedWrite(zone, 0, 10);
            repairs.PendingWorkDone(zone);
            return 0;
        }, repairs, out _, out _));
        Assert.True(repairs.Holds(zone));
        Assert.Equal(1, repairs.FailedWrites(zone));
        Assert.Equal(10, repairs.DueAt(zone));
        Assert.Equal(0, faults.Count);
    }

    [Fact]
    public void ClearingFaultsAllowsAnExplicitRetry()
    {
        var faults = new ServerBakeFaults();
        var repairs = new GhostRepairLedger();
        var zone = new Vector2s(1, 1);
        faults.TryProcessZone<int>(zone, () => throw new InvalidOperationException(), repairs, out _, out _);
        faults.Pause(new InvalidOperationException("queue fault"));
        faults.Clear();
        Assert.False(faults.IsPaused);
        Assert.Null(faults.PauseReason);
        Assert.Equal(0, faults.Count);
        Assert.True(faults.TryProcessZone(zone, () => 42, repairs, out _, out _));
    }

    [Fact]
    public void GlobalQueuePauseDoesNotQuarantineNewGhostZones()
    {
        var faults = new ServerBakeFaults();
        faults.Pause(new InvalidOperationException("queue fault"));
        Assert.True(faults.IsPaused);
        Assert.Equal("queue fault", faults.PauseReason);
        Assert.False(faults.IsQuarantined(new Vector2s(3, 2)));
    }

    [Fact]
    public void InactiveForeignOwnerWaitsOnlyForALiveCompiler()
    {
        var foreign = new[] { new ServerBakePlanner.Compiler(1, 7, false) };
        Assert.Equal(ServerBakePlanner.Action.WaitForOwner,
            ServerBakePlanner.Decide(2, true, true, foreign, 1));
        Assert.Equal(ServerBakePlanner.Action.WriteSavedCompiler,
            ServerBakePlanner.Decide(2, true, false, foreign, 1));
        Assert.Equal(ServerBakePlanner.Action.WriteLiveCompiler,
            ServerBakePlanner.Decide(2, true, true, new[] { new ServerBakePlanner.Compiler(1, 0, false) }, 1));
    }
}
