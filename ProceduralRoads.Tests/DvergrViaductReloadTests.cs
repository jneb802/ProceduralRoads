using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A network saved with a valley viaduct gives the viaduct back on a reload: crossings are
/// restored, so <see cref="RoadCrossing.ValleyJump"/> is saved with them, and the reload plans the same pieces and
/// puts the viaduct's keep-out back. Once nothing came back at all: the reader refused every network with a
/// staircase's cuts, and ValleyJump was not saved either.
/// </summary>
public sealed class DvergrViaductReloadTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public DvergrViaductReloadTests()
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
    }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        RoadNetworkPersistence.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    /// <summary>One bridge in a record before format 5: version, its banks and bed, and (format 3 and on) the kit stored
    /// as its number and one; format 4 adds its ends, none here.</summary>
    private static byte[] OldRecord(int version, float bank, int kit)
    {
        using var ms = new System.IO.MemoryStream();
        using (var w = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(version); w.Write(1);
            foreach (float f in new[] { -31f, 0f, 31f, 0f, 26f, -6f, 0f, 20f }) w.Write(f);
            w.Write((int)CrossingKind.Bridge); w.Write((int)FordStyle.None);
            if (version >= 2) w.Write(0);
            if (version >= 3) w.Write(kit);
            if (version >= 4) for (int end = 0; end < 2; end++) for (int f = 0; f < 7; f++) w.Write(0);
        }
        return ms.ToArray();
    }

    /// <summary>A record of format 3 or 4 that stored the retired plain wooden kit still reads: the bridge's kit is chosen
    /// when it is first built, its other crossings kept.</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void ARecordWithTheRetiredWoodenKitReadsAsUndecided(int version)
    {
        var loaded = new List<RoadCrossing>();
        Assert.True(RoadNetworkPersistence.DeserializeRoadCrossings(OldRecord(version, 45f, RoadNetworkPersistence.RetiredWoodKit), loaded));
        Assert.Equal(BridgeStructures.Kit.Undecided, Assert.Single(loaded).Kit);
        // Format 5 never stored it: still refused there.
        byte[] five = RoadNetworkPersistence.SerializeRoadCrossings(loaded);
        BitConverter.GetBytes(RoadNetworkPersistence.RetiredWoodKit).CopyTo(five, 8 + 11 * 4);
        Assert.False(RoadNetworkPersistence.DeserializeRoadCrossings(five, new List<RoadCrossing>()));
    }

    /// <summary>A Mistlands bridge a record before format 5 laid as no viaduct, whose tallest pier stands between the
    /// former 20 m ceiling and today's, stays no viaduct: a record that stored its kind (formats 3 and 4, no kit) is read by
    /// what it stored, one that did not (formats 1 and 2) by the ceiling it was laid under.</summary>
    [Fact]
    public void AnOldRecordKeepsTheViaductsItLaid()
    {
        var world = new DvergrViaductTests.MistRiver();
        RoadCrossing probe = DvergrViaductTests.River();
        float? between = null, under = null;
        for (float bank = 30f; bank <= 70f && (between == null || under == null); bank += 0.5f)
        {
            world.Bank = bank;
            if (!DvergrViaduct.Applies(probe, world)) continue;
            if (DvergrViaduct.Applies(probe, world, DvergrViaduct.FormerPierCeiling)) under ??= bank;
            else between ??= bank;
        }
        Assert.True(between.HasValue && under.HasValue, "no river whose viaduct the ceiling's raise decides");
        world.Bank = between!.Value;
        foreach (int version in new[] { 1, 2, 3, 4 })
        {
            var loaded = new List<RoadCrossing>();
            Assert.True(RoadNetworkPersistence.DeserializeRoadCrossings(OldRecord(version, world.Bank, 0), loaded, world));
            Assert.False(BridgeStructures.IsViaduct(Assert.Single(loaded), world), $"format {version}");
            Assert.Equal(BridgeStructures.Kit.Undecided, loaded[0].Kit);
        }
        world.Bank = under!.Value;
        foreach (int version in new[] { 1, 2 })
        {
            var loaded = new List<RoadCrossing>();
            Assert.True(RoadNetworkPersistence.DeserializeRoadCrossings(OldRecord(version, world.Bank, 0), loaded, world));
            Assert.Equal(BridgeStructures.Kit.DvergrViaduct, Assert.Single(loaded).Kit);
        }
    }
}
