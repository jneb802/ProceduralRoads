using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
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
        NetworkCutoff.BeginNewNetwork(); NetworkCutoff.UseNewNetwork();
        Assert.True(NetworkCutoff.Excludes(A));
        Assert.True(NetworkCutoff.Excludes(B));
        Assert.False(NetworkCutoff.Excludes(C));
    }

    [Fact]
    public void AFreshWorldExcludesNothing()
    {
        NetworkCutoff.SnapshotWorld(Array.Empty<Vector2s>());
        NetworkCutoff.BeginNewNetwork(); NetworkCutoff.UseNewNetwork();
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
        NetworkCutoff.BeginNewNetwork(); NetworkCutoff.UseNewNetwork();
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
        NetworkCutoff.BeginNewNetwork(); NetworkCutoff.UseNewNetwork();
        Assert.False(NetworkCutoff.Excludes(B), "the first network reaches this session's zones");
        ZoneSystem.instance.Generated.Add(C);   // generated with the first network
        NetworkCutoff.BeginNewNetwork(); NetworkCutoff.UseNewNetwork();   // a command replaces it
        Assert.True(NetworkCutoff.Excludes(A));
        Assert.True(NetworkCutoff.Excludes(B));
        Assert.True(NetworkCutoff.Excludes(C));
        Assert.False(NetworkCutoff.Excludes(D));
    }

    /// <summary>A replacing network leaves alone the zones generated before its generation began, not the zones its own
    /// generation placed first (LocationsFirstLoad: the locations it will visit, placed before their roads while the world holds
    /// still): no player has been there, and they are where its roads go onto those locations' landings.</summary>
    [Fact]
    public void AReplacingNetworkWritesTheZonesItsOwnGenerationPlaced()
    {
        NetworkCutoff.SnapshotWorld(new[] { A });
        ZoneSystem.instance!.Generated.Add(A);
        NetworkCutoff.BeginNewNetwork(); NetworkCutoff.UseNewNetwork();
        ZoneSystem.instance.Generated.Add(B);   // generated with the first network, players about
        NetworkCutoff.BeginNewNetwork();        // a command replaces it
        ZoneSystem.instance.Generated.Add(C);   // placed first by that command's generation
        NetworkCutoff.UseNewNetwork();
        Assert.True(NetworkCutoff.Excludes(A));
        Assert.True(NetworkCutoff.Excludes(B));
        Assert.False(NetworkCutoff.Excludes(C), "a zone the generation placed itself was left as it is");
    }

    [Fact]
    public void ALateTeardownOfAnotherWorldKeepsThisOnesCutoffAndNoRecordIsWrittenForAStrangeWorld()
    {
        NetworkCutoff.SnapshotWorld(new[] { A });
        NetworkCutoff.BeginNewNetwork(); NetworkCutoff.UseNewNetwork();
        NetworkCutoff.Forget(new object());
        Assert.True(NetworkCutoff.Excludes(A));
        using var other = new ValheimWorldScope().WithZoneSystem();
        Assert.Null(NetworkCutoff.Record(1, new[] { A, B }));
    }

    /// <summary>
    /// One protected set for automatic work, captured as the network begins (BeginNewNetwork): in a fresh world none; in
    /// an explored world built only in new land, every zone explored before; over the explored land, the zones players
    /// built in. Planning goes round exactly what the writers leave alone, across a zone and into one alike (no zone a
    /// road ends in is exempt). A zone the network's own preparation generates, and one explored after it is published,
    /// stay the network's, through a save and a reload. Before, only the network over the explored land went round its
    /// set, and a road could still end in a protected zone.
    /// </summary>
    [Theory]
    [InlineData("fresh")]
    [InlineData("avoid explored")]
    [InlineData("avoid player modified")]
    public void AutomaticPlanningProtectsTheZonesWritersProtect(string mode)
    {
        var prior = new Vector2s(0, 0);
        var exploredUnmodified = new Vector2s(0, 2);
        var prepared = new Vector2s(2, 0);
        var exploredLater = new Vector2s(3, 0);
        bool fresh = mode == "fresh";
        bool modified = mode == "avoid player modified";
        byte[]? record;
        Vector2s[] generatedAtSave;
        const int version = 71;

        using (new ValheimWorldScope().WithZoneSystem())
        {
            var zones = ZoneSystem.instance!.m_generatedZones;
            if (!fresh)
            {
                zones.Add(prior);
                zones.Add(exploredUnmodified);
            }
            NetworkCutoff.SnapshotWorld(zones);
            if (modified) NetworkCutoff.IncludeExplored(new[] { prior });
            NetworkCutoff.BeginNewNetwork();
            var from = new Vector2(-48f, 0f);
            var to = new Vector2(48f, 0f);
            var protectedEnd = new Vector2(0f, 0f);
            bool excluded = NetworkCutoff.Excludes(prior);
            Assert.Equal(!fresh, excluded);
            Assert.Equal(excluded, NetworkCutoff.Avoids(prior));
            Assert.Equal(excluded, NetworkCutoff.AvoidsSegment(from, to, from, to));
            Assert.Equal(excluded, NetworkCutoff.AvoidsSegment(from, protectedEnd, from, protectedEnd));

            // A zone the network's preparation generates after the capture; a zone explored after publication.
            zones.Add(prepared);
            NetworkCutoff.UseNewNetwork();
            zones.Add(exploredLater);
            generatedAtSave = zones.ToArray();
            record = NetworkCutoff.Record(version, zones);
            Assert.NotNull(record);
            AssertProtected(prior, exploredUnmodified, prepared, exploredLater, fresh, modified);
        }

        NetworkCutoff.Forget();
        using var reload = new ValheimWorldScope().WithZoneSystem();
        ZoneSystem.instance!.m_generatedZones.UnionWith(generatedAtSave);
        NetworkCutoff.SnapshotWorld(ZoneSystem.instance.m_generatedZones);
        NetworkCutoff.UseLoadedNetwork(version, record);
        AssertProtected(prior, exploredUnmodified, prepared, exploredLater, fresh, modified);
    }

    private static void AssertProtected(Vector2s prior, Vector2s unmodified, Vector2s prepared, Vector2s later, bool fresh, bool modified)
    {
        Assert.Equal(!fresh, NetworkCutoff.Excludes(prior));
        Assert.Equal(!fresh && !modified, NetworkCutoff.Excludes(unmodified));
        Assert.False(NetworkCutoff.Excludes(prepared));
        Assert.False(NetworkCutoff.Excludes(later));
    }
}
