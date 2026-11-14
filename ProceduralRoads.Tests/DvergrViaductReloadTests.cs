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
        PlacedLocations.Reset();
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
        // A Mistlands destination is placed before its roads (PlacedLocations.PlacesFirst), here as the load would have placed it
        // (it levels nothing): one not placed would be given no road.
        PlacedLocations.Reset();
        PlacedLocations.Remember(new PlacedLocationRecord
        {
            Prefab = "Eikthyrnir", Centre = new Vector2(100f, 0f), Root = world.GetHeight(100f, 0f), X0 = 40, Z0 = -60, W = 121, H = 121,
        });
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

    /// <summary>The crossings record reads only its current format: any other version number is refused.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    public void ACrossingsRecordOfAnotherVersionIsRefused(int version)
    {
        var c = RoadCrossing.Between(new Vector2(-24f, 0f), new Vector2(24f, 0f), 30f, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
        byte[] bytes = RoadNetworkPersistence.SerializeRoadCrossings(new[] { c });
        BitConverter.GetBytes(version).CopyTo(bytes, 0);
        Assert.False(RoadNetworkPersistence.DeserializeRoadCrossings(bytes, new List<RoadCrossing>()));
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

    private sealed class FlatWorldForKit : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => 40f;
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
            Assert.True(RoadNetworkPersistence.DeserializeRoadCrossings(RoadNetworkPersistence.SerializeRoadCrossings(new[] { c }), loaded));
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
