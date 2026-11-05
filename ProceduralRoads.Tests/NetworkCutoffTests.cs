using System;
using System.Collections.Generic;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Which zones automatic writers leave alone (NetworkCutoff): every zone generated before the network reached the
/// world, across sessions, downgrades and regenerations.
/// </summary>
public sealed class NetworkCutoffTests : IDisposable
{
    private readonly ValheimWorldScope _world = new ValheimWorldScope().WithZoneSystem();
    private static readonly Vector2s A = new(0, 0), B = new(1, 0), C = new(2, 0), D = new(3, 0);

    public NetworkCutoffTests() => NetworkCutoff.Forget();
    public void Dispose() { NetworkCutoff.Forget(); _world.Dispose(); }

    [Fact]
    public void ANewNetworkOnAnExploredWorldLeavesTheExploredZonesAlone()
    {
        NetworkCutoff.SnapshotWorld(new[] { A, B });
        NetworkCutoff.UseNewNetwork();
        Assert.True(NetworkCutoff.Excludes(A));
        Assert.True(NetworkCutoff.Excludes(B));
        Assert.False(NetworkCutoff.Excludes(C));
    }

    [Fact]
    public void AFreshWorldExcludesNothing()
    {
        NetworkCutoff.SnapshotWorld(Array.Empty<Vector2s>());
        NetworkCutoff.UseNewNetwork();
        Assert.Equal(0, NetworkCutoff.ExcludedCount);
    }

    [Fact]
    public void ASaveWithoutARecordExcludesEveryGeneratedZone()
    {
        NetworkCutoff.SnapshotWorld(new[] { A, B });
        NetworkCutoff.UseLoadedNetwork(42, null);
        Assert.True(NetworkCutoff.Excludes(A));
        Assert.True(NetworkCutoff.Excludes(B));
    }

    [Fact]
    public void ZonesGeneratedWithTheNetworkStayItsAndZonesADowngradeGeneratedDoNot()
    {
        // Session 1 (this build): A was explored before, C generated with the network.
        NetworkCutoff.SnapshotWorld(new[] { A });
        NetworkCutoff.UseLoadedNetwork(42, null);
        byte[] record = NetworkCutoff.Record(42, new[] { A, C })!;
        // Session 2 (1.4.4, downgraded): generates D without any road. Session 3 (this build again):
        NetworkCutoff.Forget();
        NetworkCutoff.SnapshotWorld(new[] { A, C, D });
        NetworkCutoff.UseLoadedNetwork(42, record);
        Assert.True(NetworkCutoff.Excludes(A));
        Assert.False(NetworkCutoff.Excludes(C));
        Assert.True(NetworkCutoff.Excludes(D), "a zone generated while the network was not in place stays excluded");
    }

    [Fact]
    public void ARecordForAnotherNetworkCountsForNothing()
    {
        NetworkCutoff.SnapshotWorld(new[] { A, C });
        NetworkCutoff.UseLoadedNetwork(42, null);
        byte[] record = NetworkCutoff.Record(41, new[] { C })!;
        NetworkCutoff.UseLoadedNetwork(42, record);
        Assert.True(NetworkCutoff.Excludes(C));
    }

    [Fact]
    public void AnotherWorldNeverInheritsTheCutoff()
    {
        NetworkCutoff.SnapshotWorld(new[] { A });
        NetworkCutoff.UseNewNetwork();
        using var other = new ValheimWorldScope().WithZoneSystem();
        Assert.False(NetworkCutoff.Excludes(A));
    }

    /// <summary>
    /// road_generate or road_regen_island in a running session: zones generated this session under the old network
    /// may hold a player's work too, so the new network leaves them alone as well. The first network of a fresh world
    /// still reaches the zones generated around spawn this session.
    /// </summary>
    [Fact]
    public void ANetworkReplacedInTheSessionLeavesThisSessionsZonesAlone()
    {
        NetworkCutoff.SnapshotWorld(new[] { A });
        ZoneSystem.instance!.Generated.Add(A);
        ZoneSystem.instance.Generated.Add(B);   // generated this session, before the first network
        NetworkCutoff.UseNewNetwork();
        Assert.False(NetworkCutoff.Excludes(B), "the first network reaches this session's zones");
        ZoneSystem.instance.Generated.Add(C);   // generated with the first network
        NetworkCutoff.UseNewNetwork();          // a command replaces it
        Assert.True(NetworkCutoff.Excludes(A));
        Assert.True(NetworkCutoff.Excludes(B));
        Assert.True(NetworkCutoff.Excludes(C));
        Assert.False(NetworkCutoff.Excludes(D));
    }

    [Fact]
    public void ALateTeardownOfAnotherWorldKeepsThisOnesCutoffAndNoRecordIsWrittenForAStrangeWorld()
    {
        NetworkCutoff.SnapshotWorld(new[] { A });
        NetworkCutoff.UseNewNetwork();
        NetworkCutoff.Forget(new object());
        Assert.True(NetworkCutoff.Excludes(A));
        using var other = new ValheimWorldScope().WithZoneSystem();
        Assert.Null(NetworkCutoff.Record(1, new[] { A, B }));
    }
}
