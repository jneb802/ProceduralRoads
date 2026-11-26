using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A viaduct a manual road builds keeps later roads clear as a generated one does (RoadNetworkGenerator.KeepClearOfViaducts),
/// in the session that built it and after a reload alike: the cells under it (DvergrViaduct.Standing), its ends and
/// clearance (DvergrViaduct.TooClose) and its deck (RoadNetworkGenerator.UnderALaidDeck, asked of a manual road through
/// RoadNetworkGenerator.BuiltRoadRefusal). A road up the valley floor to the middle of the deck, where its middle pier
/// stands, is refused; without the keep-out it was built into the pier.
/// </summary>
[Collection("RoadStatics")]
public sealed class ManualViaductKeepOutTests : IDisposable
{
    private readonly DvergrViaductTests.MistValley _world = new();
    private readonly ValheimWorldScope _scope;

    public ManualViaductKeepOutTests()
    {
        _scope = new ValheimWorldScope().WithWorld(_world).WithZdos().WithZoneSystem().WithScene().AsServer();
        RoadNetworkGenerator.Reset(); NetworkCutoff.Forget();
        RoadNetworkGenerator.MarkLocationsReady();
        RoadNetworkGenerator.RoadWidth = RoadConstants.DefaultRoadWidth; RoadGrade.Configured = RoadConstants.DefaultMaxRoadGrade;
        RoadLocationProtection.Set(Array.Empty<RoadLocationProtection.Footprint>());
        RoadNetworkGenerator.ConfigureFinders(f => { f.Stairs = true; f.Viaducts = true; f.TerrainVarianceThreshold = 1000f; f.Meander = 0f; });
    }

    public void Dispose()
    {
        RoadNetworkGenerator.ConfigureFinders(null); RoadNetworkGenerator.Reset(); NetworkCutoff.Forget(); _scope.Dispose();
    }

    private static Island Everywhere()
    {
        var island = new Island { Id = 1, CellSize = 8, WorldOffset = 0, Min = new Vector2(-280, -280), Max = new Vector2(280, 280) };
        for (int x = -35; x <= 35; x++) for (int z = -35; z <= 35; z++) island.Cells.Add(new Vector2Int(x, z));
        return island;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARoadUpTheValleyFloorStopsShortOfTheManualViaduct(bool reload)
    {
        var across = ManualRoads.Prepare(new[] { new Vector2(-100, 0), new Vector2(100, 0) }, false, new[] { Everywhere() });
        RoadCrossing viaduct = Assert.Single(across.NewCrossings, c => c.ValleyJump);
        ManualRoads.Commit(across);
        Assert.Contains(BridgeStructures.Solve(viaduct, _world, _world.GetSeed()),
            p => p.Kind == StructurePieceKind.ViaductPier && (new Vector2(p.Position.x, p.Position.z) - viaduct.Center).magnitude < 4f);
        if (reload)
        {
            RoadNetworkGenerator.SaveGlobalRoadData();
            RoadNetworkGenerator.Reset();
            Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData()); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
            RoadNetworkGenerator.MarkLocationsReady();
        }
        Vector2 middle = viaduct.Center;
        Assert.True(DvergrViaduct.Standing(Mathf.RoundToInt(middle.x / RoadPathfinder.CellSize), Mathf.RoundToInt(middle.y / RoadPathfinder.CellSize)));
        int appended = RoadSpatialGrid.AppendCount;

        var refused = Assert.Throws<InvalidOperationException>(() =>
            ManualRoads.Prepare(new[] { middle + new Vector2(0, -60), middle + new Vector2(0, -2) }, false, new[] { Everywhere() }));
        Assert.DoesNotContain("not loaded", refused.Message);
        Assert.Equal(appended, RoadSpatialGrid.AppendCount);
    }

    private RoadCrossing LayTheManualViaduct()
    {
        var across = ManualRoads.Prepare(new[] { new Vector2(-100, 0), new Vector2(100, 0) }, false, new[] { Everywhere() });
        RoadCrossing viaduct = Assert.Single(across.NewCrossings, c => c.ValleyJump);
        ManualRoads.Commit(across);
        return viaduct;
    }

    private static void Reload()
    {
        RoadNetworkGenerator.SaveGlobalRoadData();
        RoadNetworkGenerator.Reset();
        Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData()); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        RoadNetworkGenerator.MarkLocationsReady();
    }

    /// <summary>A road along the valley 1 m inside the viaduct's west deck end, under the deck (as the generation's own
    /// under-deck test lays it).</summary>
    private RoadSpatialGrid.PlannedPath UnderTheWestEnd(RoadCrossing viaduct)
    {
        var (near, _) = DvergrViaduct.Ends(viaduct, _world);
        Vector2 under = near.Edge + near.Up * 1f;
        float h = _world.Top - 3f;
        return new RoadSpatialGrid.PlannedPath(new List<Vector2> { under + new Vector2(0f, -20f), under, under + new Vector2(0f, 20f) },
            new List<float> { h, h, h }, new List<RoadPointDebugInfo>(), 4f, 40f, false);
    }

    /// <summary>
    /// What is refused near a saved viaduct is refused alike before and after a reload, by every path that asks: a second
    /// viaduct back to back with it (DvergrViaduct.TooClose, which the search's valley jump asks), and a road under its
    /// deck, as generation asks it (UnderALaidDeck) and as a manual road does (BuiltRoadRefusal). After a reload only the
    /// cells under it came back; its ends and its deck were forgotten, so a manual road could be planned there.
    /// </summary>
    [Fact]
    public void ARouteNearASavedViaductIsRefusedAlikeBeforeAndAfterAReload()
    {
        RoadCrossing viaduct = LayTheManualViaduct();
        Vector2 aside = new(0f, 8f);
        var under = new List<RoadSpatialGrid.PlannedPath> { UnderTheWestEnd(viaduct) };
        (string? tooClose, string? generation, string? manual) Verdicts() => (
            DvergrViaduct.TooClose(viaduct.FromBank + aside, viaduct.ToBank + aside),
            RoadNetworkGenerator.UnderALaidDeck(under, out _),
            RoadNetworkGenerator.BuiltRoadRefusal(under, new List<RoadCrossing>(), out _));

        var before = Verdicts();
        Assert.Contains("back to back", before.tooClose ?? "none");
        Assert.Equal("passes under a viaduct's deck", before.generation);
        Assert.Equal(before.generation, before.manual);

        Reload();
        var after = Verdicts();
        Assert.Equal(before, after);
    }

    /// <summary>The control: a manual road up to the viaduct's west bank, joining the road there outside the deck, is never
    /// refused for the deck's keep-clear, before and after a reload alike. Its end's levelling there changes the ground the
    /// viaduct's west end stands on, so it is refused for that instead (RoadNetworkGenerator.UnderAPlannedStructure: no later
    /// road changes the ground under another road's structure); once it was built.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AManualRoadToTheViaductsBankIsRefusedForTheGroundUnderItsEndNotItsDeck(bool reload)
    {
        RoadCrossing viaduct = LayTheManualViaduct();
        if (reload) Reload();
        var (near, _) = DvergrViaduct.Ends(viaduct, _world);
        // On the road 2 m short of the deck's west end: within the deck's keep-clear reach.
        Vector2 bank = near.Meet - near.Up * 2f;
        var refused = Assert.Throws<InvalidOperationException>(() =>
            ManualRoads.Prepare(new[] { bank + new Vector2(0f, -60f), bank }, false, new[] { Everywhere() }));
        Assert.Contains(RoadNetworkGenerator.GroundUnderAStructure + ": the viaduct", refused.Message);
        Assert.DoesNotContain("passes under a viaduct's deck", refused.Message);
    }

    /// <summary>A manual road is refused under a laid deck by the refusals every manual road is judged by
    /// (BuiltRoadRefusal), as a generated one is in its own (GenerateRoadCore asks UnderALaidDeck): once only generation
    /// asked it.</summary>
    [Fact]
    public void AManualRoadUnderALaidDeckIsRefused()
    {
        RoadCrossing viaduct = LayTheManualViaduct();
        var under = new List<RoadSpatialGrid.PlannedPath> { UnderTheWestEnd(viaduct) };
        Assert.Equal("passes under a viaduct's deck", RoadNetworkGenerator.BuiltRoadRefusal(under, new List<RoadCrossing>(), out Vector2 at));
        Assert.True(Vector2.Distance(at, UnderTheWestEnd(viaduct).Points[1]) <= 20.5f, $"refused at {at}");
    }
}
