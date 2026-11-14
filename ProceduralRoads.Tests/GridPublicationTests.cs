using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

public sealed class GridPublicationTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 30f;
        public override int GetSeed() => 77;
    }

    public GridPublicationTests()
    {
        _scope.WithWorld(new FlatWorld());
        RoadNetworkGenerator.Reset(); RoadLocationProtection.Reset(); RoadSpatialGrid.Clear();
    }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset(); RoadLocationProtection.Reset(); _scope.Dispose();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void FirstPublishedPointsAreVisibleToSettlementBeforeAndAfterReload(bool cuts, bool ordinaryRoadFirst)
    {
        if (ordinaryRoadFirst)
            RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(300, 300), new(340, 300) }, 4, WorldGenerator.instance);
        if (cuts)
            RoadSpatialGrid.CommitCuts(new[] { new Vector3(0, 28, 0) }, StairLayout.CutRadius * 2f);
        else
            RoadSpatialGrid.CommitLevelled(new[] { new Vector2(0, 0) }, new[] { 28f }, 4);
        RoadSpatialGrid.FinalizeRoadNetwork();
        var zone = new Vector2s(0, 0);
        var before = RoadSpatialGrid.GetRoadPointsInZone(zone);
        float beforeGround = RoadTerrainModifier.FinishedGround((_, _) => 30)(0, 0);
        bool initialized = RoadSpatialGrid.IsInitialized;
        int records = RoadSpatialGrid.StoredPointRecords, version = RoadSpatialGrid.RoadNetworkVersion;
        float? length = RoadSpatialGrid.CommittedPathLengthMeters;
        byte[] saved = RoadSpatialGrid.SerializeAllRoadPoints()!;
        RoadSpatialGrid.Clear();
        Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(saved));
        var after = RoadSpatialGrid.GetRoadPointsInZone(zone);
        float afterGround = RoadTerrainModifier.FinishedGround((_, _) => 30)(0, 0);
        Assert.True(initialized, $"cuts={cuts}, roadFirst={ordinaryRoadFirst}: stored={records}, visible before/after={before.Count}/{after.Count}, finished ground={beforeGround:R}/{afterGround:R}");
        Assert.NotEmpty(before);
        Assert.All(before, p => Assert.Equal(cuts, p.cutOnly));
        Assert.Equal(before.Select(p => (p.p, p.w, p.h, p.paintOnly, p.cutOnly, p.unpainted, p.addition)),
            after.Select(p => (p.p, p.w, p.h, p.paintOnly, p.cutOnly, p.unpainted, p.addition)));
        Assert.True(afterGround < 30f);
        Assert.Equal(beforeGround, afterGround);
        if (cuts) Assert.Equal(28f, beforeGround);
        Assert.Equal(records, RoadSpatialGrid.StoredPointRecords);
        Assert.Equal(length, RoadSpatialGrid.CommittedPathLengthMeters);
        Assert.Equal(version, RoadSpatialGrid.RoadNetworkVersion);
        Assert.Equal(saved, RoadSpatialGrid.SerializeAllRoadPoints());
        Assert.Empty(RoadSpatialGrid.GetRoadPointsInZone(new Vector2s(20, 20)));
    }

    [Fact]
    public void EmptyAuxiliaryPublicationsDoNotInitializeOrChangeTheEmptyNetwork()
    {
        RoadSpatialGrid.FinalizeRoadNetwork();
        int version = RoadSpatialGrid.RoadNetworkVersion;
        byte[] saved = RoadSpatialGrid.SerializeAllRoadPoints()!;
        RoadSpatialGrid.CommitCuts(Array.Empty<Vector3>(), StairLayout.CutRadius * 2f);
        RoadSpatialGrid.CommitLevelled(Array.Empty<Vector2>(), Array.Empty<float>(), 4);
        RoadSpatialGrid.FinalizeRoadNetwork();
        Assert.False(RoadSpatialGrid.IsInitialized);
        Assert.Equal(0, RoadSpatialGrid.StoredPointRecords);
        Assert.Equal(0, RoadSpatialGrid.GridCellsWithRoads);
        Assert.Equal(version, RoadSpatialGrid.RoadNetworkVersion);
        Assert.Equal(saved, RoadSpatialGrid.SerializeAllRoadPoints());
    }

    [Fact]
    public void ExplicitRoadCommitRetainsItsEmptyInitializationContract()
    {
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(new List<Vector2>(), new List<float>(),
            new List<RoadPointDebugInfo>(), 4, 0, false));
        Assert.True(RoadSpatialGrid.IsInitialized);
        Assert.Equal(0, RoadSpatialGrid.StoredPointRecords);
    }
}
