using System.Collections.Generic;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The terrain write's build data kept for the same zone's saved-rock pass (ServerBakeKeptBuild): it serves that
/// zone only, once, and the builder's ready list (a FIFO the game caps at 16, evicting the oldest) gets it back at the
/// end it evicts last. The game side (HeightmapBuilder's lock, Heightmap.Generate taking it) is ServerTerrainBake's.
/// </summary>
public class ServerBakeKeptBuildTests
{
    private sealed class Build { }

    /// <summary>HeightmapBuilder.BuildThread's own rule after each build: add it, then drop the oldest past 16.</summary>
    private static void BuilderFinishes(List<Build> ready, Build built)
    {
        ready.Add(built);
        while (ready.Count > 16) ready.RemoveAt(0);
    }

    [Fact]
    public void TheKeptDataServesOnlyTheZoneItWasKeptFor()
    {
        var kept = new ServerBakeKeptBuild<Build>();
        var data = new Build();
        kept.Keep(new Vector2s(3, -2), data);
        Assert.Null(kept.Take(new Vector2s(3, -1), enabled: true));
        // Asked for another zone, it is gone: the zone it was kept for does not get it later either.
        Assert.Null(kept.Take(new Vector2s(3, -2), enabled: true));
    }

    [Fact]
    public void TheKeptDataIsHandedBackOnce()
    {
        var kept = new ServerBakeKeptBuild<Build>();
        var data = new Build();
        kept.Keep(new Vector2s(1, 1), data);
        Assert.Same(data, kept.Take(new Vector2s(1, 1), enabled: true));
        Assert.Null(kept.Take(new Vector2s(1, 1), enabled: true));
    }

    [Fact]
    public void SwitchedOffNothingIsHandedBack()
    {
        var kept = new ServerBakeKeptBuild<Build>();
        kept.Keep(new Vector2s(1, 1), new Build());
        Assert.Null(kept.Take(new Vector2s(1, 1), enabled: false));
    }

    [Fact]
    public void DataNoRockPassUsedIsDroppedWithItsZone()
    {
        var kept = new ServerBakeKeptBuild<Build>();
        kept.Keep(new Vector2s(1, 1), new Build());
        kept.Drop();
        Assert.Null(kept.Take(new Vector2s(1, 1), enabled: true));
    }

    [Fact]
    public void KeepingNothingForgetsWhatWasKept()
    {
        var kept = new ServerBakeKeptBuild<Build>();
        kept.Keep(new Vector2s(1, 1), new Build());
        kept.Keep(new Vector2s(1, 1), null);
        Assert.Null(kept.Take(new Vector2s(1, 1), enabled: true));
    }

    [Fact]
    public void AHandBackGoesToTheEndTheBuilderEvictsLast()
    {
        var ready = new List<Build>();
        for (int i = 0; i < 16; i++) ready.Add(new Build());
        var data = new Build();
        ServerBakeKeptBuild<Build>.HandBack(ready, data);
        // The builder finishing another zone evicts the oldest (two: the hand-back made it 17), not the hand-back.
        BuilderFinishes(ready, new Build());
        Assert.Contains(data, ready);
        // Fourteen more and it is the oldest: still there; the next evicts it, and the caller's readiness check
        // (asked again right before the terrain is made) sends the zone round the queue rather than waiting.
        for (int i = 0; i < 14; i++) BuilderFinishes(ready, new Build());
        Assert.Contains(data, ready);
        BuilderFinishes(ready, new Build());
        Assert.DoesNotContain(data, ready);
    }

    [Fact]
    public void AHandBackIsOnTheListOnceAndComesOffIfUnused()
    {
        var ready = new List<Build>();
        var data = new Build();
        ServerBakeKeptBuild<Build>.HandBack(ready, data);
        ServerBakeKeptBuild<Build>.HandBack(ready, data);
        Assert.Single(ready);
        Assert.True(ServerBakeKeptBuild<Build>.Withdraw(ready, data));
        Assert.Empty(ready);
        // Taken by Heightmap.Generate already: nothing to take back.
        Assert.False(ServerBakeKeptBuild<Build>.Withdraw(ready, data));
    }
}
