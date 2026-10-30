using System;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A viaduct a manual road builds keeps later roads out from under its deck as a generated one does
/// (DvergrViaduct.Standing), in the session that built it and after a reload. A road up the valley floor to the middle of
/// the deck, where its middle pier stands, is refused; without the keep-out it was built into the pier.
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
        RoadNetworkGenerator.RoadWidth = 4f; RoadGrade.Configured = .35f;
        RoadSiteProtection.Set(Array.Empty<RoadSiteProtection.Footprint>());
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
            Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData());
            RoadNetworkGenerator.MarkLocationsReady();
        }
        Vector2 middle = viaduct.Center;
        Assert.True(DvergrViaduct.Standing(Mathf.RoundToInt(middle.x / RoadPathfinder.CellSize), Mathf.RoundToInt(middle.y / RoadPathfinder.CellSize)));
        int appended = RoadSpatialGrid.AppendCount;

        Assert.Throws<InvalidOperationException>(() =>
            ManualRoads.Prepare(new[] { middle + new Vector2(0, -60), middle + new Vector2(0, -2) }, false, new[] { Everywhere() }));
        Assert.Equal(appended, RoadSpatialGrid.AppendCount);
    }
}
