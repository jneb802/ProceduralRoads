using System;
using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

public sealed class ZoneSpawnTerrainTests : IDisposable
{
    private readonly ValheimWorldScope scope = new ValheimWorldScope()
        .WithWorld(new BridgeTests.WideRiverWorld()).WithZdos().WithZoneSystem().WithScene().AsServer();
    private static readonly Vector2s Zone = new(0, 0);

    public ZoneSpawnTerrainTests()
    {
        RoadNetworkGenerator.Reset();
        RoadTerrainModifier.ResetForWorld();
        NetworkCutoff.Forget();
        NetworkCutoff.SnapshotWorld(Array.Empty<Vector2s>());
        NetworkCutoff.BeginNewNetwork();
        NetworkCutoff.UseNewNetwork();
        RoadSpatialGrid.CommitLevelled(new List<Vector2> { new(-16f, 0f), new(0f, 0f), new(16f, 0f) },
            new List<float> { 40f, 40f, 40f }, RoadConstants.DefaultRoadWidth);
        RoadSpatialGrid.FinalizeRoadNetwork();
        RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        var run = new StairRun { From = new Vector2(8f, 0f), To = new Vector2(12f, 0f), Width = RoadConstants.DefaultRoadWidth, Kit = StairKit.Stone };
        run.Pieces.Add(new StructurePiece { Prefab = "Breaking", Kind = StructurePieceKind.StairStep,
            Position = new Vector3(8f, 40f, 0f), HealthFraction = 0.5f });
        StairStructures.Add(run);
        ZNetScene.instance!.AddPrefab("Breaking");
        // The staircase is laid as planned (no dressing), so its one breaking step reaches placement.
        dressing = StairStructures.Dressing;
        StairStructures.Dressing = (r, w, finished, shortfall, _) => new List<StructurePiece>(r.Pieces);
    }

    private readonly Func<StairRun, WorldGenerator, Func<int, int, float>, StairSupport.Shortfall, bool, List<StructurePiece>> dressing;

    public void Dispose()
    {
        WearNTear.FailVisual = false;
        StairStructures.Dressing = dressing;
        RoadTerrainModifier.ResetForWorld();
        RoadNetworkGenerator.Reset();
        NetworkCutoff.Forget();
        scope.Dispose();
    }

    [Theory]
    [InlineData(ZoneSystem.SpawnMode.Full)]
    [InlineData(ZoneSystem.SpawnMode.Client)]
    [InlineData(ZoneSystem.SpawnMode.Ghost)]
    public void APlacementExceptionStillWritesTerrainInEveryLiveSpawnMode(ZoneSystem.SpawnMode mode)
    {
        var compiler = scope.RegisterHeightmap(Zone).m_terrainComp!;
        Assert.NotEmpty(StructurePlans.BuildablePlanFor(Zone)!);
        WearNTear.FailVisual = true;
        bool spawned = true;
        Assert.ThrowsAny<Exception>(() => ZoneSystem_Patch.ZoneSystem_SpawnZone_Patch.Postfix(
            ZoneSystem.instance!, Zone, mode, ref spawned));
        bool ghost = mode == ZoneSystem.SpawnMode.Ghost;
        Assert.Equal(ghost ? 0 : RoadSpatialGrid.RoadNetworkVersion,
            compiler.m_nview.GetZDO().GetInt(RoadTerrainModifier.AppliedVersionHash));
        Assert.Equal(!ghost, Array.Exists(compiler.m_modifiedHeight, h => h));
    }
}
