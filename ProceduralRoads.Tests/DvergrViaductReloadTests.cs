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

    /// <summary>A world generated with a valley viaduct, saved and loaded the way a world load does: the crossing comes
    /// back as a viaduct (the same banks, still a valley jump), its pieces are planned again, the same ones, its keep-out
    /// stands again, and the record of the crossing is the same bytes.</summary>
    [Fact]
    public void ASavedAndReloadedNetworkStillYieldsItsViaducts()
    {
        var world = new DvergrViaductTests.MistValley();
        _scope.WithWorld(world).WithZoneSystem();
        foreach ((string name, float x) in new[] { ("StartTemple", -100f), ("Eikthyrnir", 100f) })
            TestLocations.Add(ZoneSystem.instance!, new ZoneSystem.LocationInstance
            {
                m_location = new ZoneSystem.ZoneLocation { m_prefab = new SoftReferenceableAssets.SoftReference<GameObject>(name), m_exteriorRadius = 10f },
                m_position = new Vector3(x, world.GetHeight(x, 0f), 0f),
            });
        _scope.WithZdos().WithNetwork(server: true);
        RoadNetworkGenerator.Reset();
        bool viaducts = RoadPathfinder.ConfiguredViaducts;
        RoadPathfinder.ConfiguredViaducts = true;
        try
        {
            RoadNetworkGenerator.GenerateRoads(force: true);
        }
        finally
        {
            RoadPathfinder.ConfiguredViaducts = viaducts;
        }

        RoadCrossing before = Assert.Single(RoadNetworkGenerator.GetRoadCrossings(), c => c.ValleyJump);
        Assert.True(DvergrViaduct.Applies(before, world));
        int seed = world.GetSeed();
        List<StructurePiece> planned = BridgeStructures.Solve(before, world, seed);
        Assert.NotEmpty(planned);
        List<Vector2s> zones = StructurePlans.PlannedZones;
        int piecesInZones = zones.Sum(z => StructurePlans.PlannedPieceCount(z));
        byte[] record = RoadNetworkPersistence.SerializeRoadCrossings(RoadNetworkGenerator.GetRoadCrossings());
        Vector2 under = before.Center;
        (int, int) Cell(Vector2 p) => (Mathf.RoundToInt(p.x / RoadPathfinder.CellSize), Mathf.RoundToInt(p.y / RoadPathfinder.CellSize));
        Assert.True(DvergrViaduct.Standing(Cell(under).Item1, Cell(under).Item2));

        RoadNetworkGenerator.SaveGlobalRoadData();
        RoadNetworkGenerator.Reset();
        Assert.Empty(RoadNetworkGenerator.GetRoadCrossings());
        Assert.False(DvergrViaduct.Standing(Cell(under).Item1, Cell(under).Item2));

        Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData(), $"the network did not load back: {RoadSpatialGrid.LastReadRefusal}");
        RoadCrossing after = Assert.Single(RoadNetworkGenerator.GetRoadCrossings(), c => c.ValleyJump);
        Assert.Equal(before.FromBank, after.FromBank);
        Assert.Equal(before.ToBank, after.ToBank);
        Assert.True(DvergrViaduct.Applies(after, world), DvergrViaduct.Refusal(after, world));
        List<StructurePiece> again = BridgeStructures.Solve(after, world, seed);
        Assert.Equal(planned.Select(p => (p.Prefab, p.Position, p.YawDegrees)), again.Select(p => (p.Prefab, p.Position, p.YawDegrees)));
        Assert.Equal(zones, StructurePlans.PlannedZones);
        Assert.Equal(piecesInZones, zones.Sum(z => StructurePlans.PlannedPieceCount(z)));
        Assert.True(DvergrViaduct.Standing(Cell(under).Item1, Cell(under).Item2), "the viaduct's keep-out did not come back");
        Assert.Equal(record, RoadNetworkPersistence.SerializeRoadCrossings(RoadNetworkGenerator.GetRoadCrossings()));
    }

    [Fact]
    public void AVersionOneCrossingsRecordStillReads()
    {
        // Written before the flags: [1][count] and ten fields a crossing.
        using var ms = new System.IO.MemoryStream();
        using (var w = new System.IO.BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(1); w.Write(1);
            foreach (float f in new[] { -24f, 0f, 24f, 0f, 46f, 0f, 0f, 0f }) w.Write(f);
            w.Write((int)CrossingKind.Bridge); w.Write((int)FordStyle.None);
        }
        var loaded = new List<RoadCrossing>();
        Assert.True(RoadNetworkPersistence.DeserializeRoadCrossings(ms.ToArray(), loaded));
        RoadCrossing c = Assert.Single(loaded);
        Assert.False(c.ValleyJump);
        Assert.Equal(new Vector2(24f, 0f), c.ToBank);
        Assert.Equal(RoadNetworkPersistence.CrossingsFormat, BitConverter.ToInt32(RoadNetworkPersistence.SerializeRoadCrossings(loaded), 0));
        Assert.Null(c.Kit);
    }

    /// <summary>A crossing keeps the kit it was stored with: saved with it, and read back, it is not decided again when the
    /// rules that chose it change (a saved bridge respawned in another kit).</summary>
    [Fact]
    public void ACrossingKeepsTheKitItWasStoredWith()
    {
        var c = RoadCrossing.Between(new Vector2(-24f, 0f), new Vector2(24f, 0f), 30f, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
        c.Kit = BridgeStructures.Kit.DvergrViaduct;
        var loaded = new List<RoadCrossing>();
        Assert.True(RoadNetworkPersistence.DeserializeRoadCrossings(RoadNetworkPersistence.SerializeRoadCrossings(new[] { c }), loaded));
        RoadCrossing back = Assert.Single(loaded);
        Assert.Equal(BridgeStructures.Kit.DvergrViaduct, back.Kit);
        float was = DvergrViaduct.MinViaductPier;
        try
        {
            DvergrViaduct.MinViaductPier = 1000f;   // no viaduct could be chosen now
            Assert.Equal(BridgeStructures.Kit.DvergrViaduct, BridgeStructures.KitFor(back, new FlatWorldForKit()));
        }
        finally { DvergrViaduct.MinViaductPier = was; }
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

    private sealed class FlatWorldForKit : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => 40f;
    }
}
