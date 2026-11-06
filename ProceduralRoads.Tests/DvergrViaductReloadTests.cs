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

    /// <summary>A viaduct saved with the ends an earlier layout recorded (at grade, its deck stopping within a metre of each
    /// bank) is built to those ends by a layout that raises some viaducts a step or two and runs a river viaduct's deck on
    /// past its bay piers: its deck at their height and between their edges, which its end stairs come down from. Before,
    /// the deck stood raised and reached past them while its stairs came down from the recorded ends, at grade.</summary>
    [Fact]
    public void AViaductSavedWithAnEarlierLayoutsEndsIsBuiltToThem()
    {
        var world = new DvergrViaductTests.MistGorge { Line = 20f };
        _scope.WithWorld(world);
        float share = DvergrViaduct.RaisedShare;
        try
        {
            DvergrViaduct.RaisedShare = 1f;
            RoadCrossing c = DvergrViaductTests.Gorge();
            Assert.True(DvergrViaduct.Applies(c, world), DvergrViaduct.Refusal(c, world));
            // The earlier layout's deck: at grade, whole modules from the first pier to within a metre of each bank.
            float atGrade = DvergrViaduct.AtGrade(c, world), module = DvergrViaduct.Module;
            float anchor = DvergrViaduct.PierAlongs(c, world)[0];
            float first = anchor - module * Mathf.CeilToInt((anchor - 1f) / module - 1e-4f);
            float last = anchor + module * Mathf.CeilToInt((c.Width - 1f - anchor) / module - 1e-4f);
            Vector2 nearEdge = c.FromBank + c.Direction * (first - module * 0.5f), farEdge = c.FromBank + c.Direction * (last + module * 0.5f);
            // This layout, asked of the crossing with no ends recorded, raises its deck and runs it on past the far bay pier.
            Assert.True(DvergrViaduct.RaisedSteps(c, world) > 0, "the fixture's viaduct is not raised");
            Assert.True(c.Along(DvergrViaduct.EdgesOf(c, world).far) > last + module, "the fixture's deck does not reach past the far bank");
            float Ground(Vector2 p) => world.GetHeight(p.x, p.y);
            c.FromEnd = StructureEnds.Decide(nearEdge, c.Direction, atGrade, Ground);
            c.ToEnd = StructureEnds.Decide(farEdge, -c.Direction, atGrade, Ground);
            var loaded = new List<RoadCrossing>();
            Assert.True(RoadNetworkPersistence.DeserializeRoadCrossings(RoadNetworkPersistence.SerializeRoadCrossings(new[] { c }), loaded, world));
            RoadCrossing saved = Assert.Single(loaded);

            Assert.Equal(atGrade, DvergrViaduct.DeckHeight(saved, world), 3);
            Assert.Equal((nearEdge, farEdge), DvergrViaduct.EdgesOf(saved, world));
            List<StructurePiece> pieces = DvergrViaduct.SolveComplete(saved, world, world.GetSeed());
            Assert.All(pieces.FindAll(p => p.Prefab == DvergrViaduct.Floor), p => Assert.Equal(atGrade - DvergrViaduct.FloorDrop, p.Position.y, 3));
            // Its deck between the recorded edges, every piece of it; nothing stands past them, a pier no more than a floor.
            List<float> deck = pieces.FindAll(p => p.Kind == StructurePieceKind.ViaductDeck).ConvertAll(p => DvergrViaductTests.Along(saved, p));
            Assert.Equal(first, deck.Min(), 2);
            Assert.Equal(last, deck.Max(), 2);
            Assert.DoesNotContain(pieces, p => p.Kind == StructurePieceKind.ViaductPier
                && (DvergrViaductTests.Along(saved, p) < first - module || DvergrViaductTests.Along(saved, p) > last + module));
        }
        finally { DvergrViaduct.RaisedShare = share; }
    }
}
