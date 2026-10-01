using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public class LocationTerrainCompositionTests : IDisposable
{
    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 60f;
    }

    private const int Centre = 32 * 65 + 32;
    private static readonly Vector2s Zone = new(0, 0);
    private readonly Heightmap hm;
    private readonly TerrainComp tc;

    public LocationTerrainCompositionTests()
    {
        RoadTerrainModifier.ResetDebugCounters();
        RoadSpatialGrid.Clear();
        WorldGenerator.instance = new FlatWorld();
        ZDOMan.instance = new ZDOMan();
        hm = Heightmap.CreateForZone(Zone, 64);
        Heightmap.Registered = hm;
        tc = hm.m_terrainComp!;
    }

    private static List<RoadSpatialGrid.RoadPoint> Road(float height)
    {
        var points = new List<RoadSpatialGrid.RoadPoint>();
        for (int x = -8; x <= 0; x++)
            points.Add(new RoadSpatialGrid.RoadPoint(new Vector2(x, 0), 4f, height));
        return points;
    }

    private void Apply(float target) => RoadTerrainModifier.ApplyRoadTerrainModsWithContext(Zone, Road(target), hm, tc);

    [Theory]
    [InlineData(60f)]
    [InlineData(65f)]
    [InlineData(55f)]
    public void RoadMeetsRaisedOrLoweredLocationInsteadOfAddingItsHeightTwice(float platform)
    {
        hm.AuthoredHeight = (_, _) => platform;
        Apply(platform);
        Assert.True(tc.m_modifiedHeight[Centre]);
        Assert.Equal(0f, tc.m_levelDelta[Centre], 4);
        Assert.Equal(platform, hm.LastRenderedHeights![Centre], 4);
    }

    [Fact]
    public void VergeBlendsFromTheLocationSurfaceRatherThanOriginalGround()
    {
        hm.AuthoredHeight = (_, _) => 65f;
        Apply(65f);
        for (int z = -4; z <= 4; z++)
        {
            int index = (z + 32) * 65 + 32;
            Assert.Equal(65f, hm.LastRenderedHeights![index], 4);
            Assert.Equal(0f, tc.m_levelDelta[index], 4);
        }
    }

    [Fact]
    public void ReapplicationUsesFreshAuthoredGroundNotPreviouslyRenderedHeight()
    {
        hm.AuthoredHeight = (_, _) => 64f;
        Apply(65f);
        Assert.Equal(1f, tc.m_levelDelta[Centre], 4);
        // Even a prior compiler value that saturates the game's clamp must
        // not be subtracted from the rendered height to guess the baseline.
        tc.m_levelDelta[Centre] = 8f;
        tc.m_smoothDelta[Centre] = 1f;
        hm.RebuildTerrain();
        Assert.Equal(72f, hm.LastRenderedHeights![Centre], 4);
        Apply(65f);
        Assert.Equal(1f, tc.m_levelDelta[Centre], 4);
        Assert.Equal(65f, hm.LastRenderedHeights![Centre], 4);
        Assert.Equal(0f, tc.m_smoothDelta[Centre]);
    }

    [Fact]
    public void HeightmapLocalHeightsAreConvertedToWorldHeight()
    {
        hm.transform.position = new Vector3(0, 20, 0);
        hm.AuthoredHeight = (_, _) => 64f;
        Apply(65f);
        Assert.Equal(1f, tc.m_levelDelta[Centre], 4);
        Assert.Equal(65f, hm.LastRenderedHeights![Centre] + hm.transform.position.y, 4);
    }

    [Fact]
    public void RequestsWaitForLocationPlacementAndCoalesceWithoutARebuildLoop()
    {
        hm.AutoRebuild = false;
        Apply(65f);
        Apply(65f);
        Assert.Equal(0, tc.SaveCount);
        Assert.Equal(1, RoadTerrainModifier.PendingWriteCount);
        // Location gets placed after TerrainComp.Awake requested the write.
        hm.AuthoredHeight = (_, _) => 65f;
        int pokes = hm.PokeCount;
        hm.RebuildTerrain();
        Assert.Equal(1, tc.SaveCount);
        Assert.Equal(0, RoadTerrainModifier.PendingWriteCount);
        Assert.Equal(pokes, hm.PokeCount);
        Assert.Equal(65f, hm.LastRenderedHeights![Centre], 4);
        hm.RebuildTerrain();
        Assert.Equal(1, tc.SaveCount);
    }

    [Fact]
    public void RebuildWithoutARequestKeepsPlayerChanges()
    {
        hm.AuthoredHeight = (_, _) => 65f;
        Apply(65f);
        tc.m_levelDelta[Centre] = 2f;
        tc.m_smoothDelta[Centre] = 0.5f;
        hm.RebuildTerrain();
        Assert.Equal(67.5f, hm.LastRenderedHeights![Centre], 4);
        Assert.Equal(1, tc.SaveCount);
    }

    [Fact]
    public void OwnershipIsCheckedAgainAtTheDelayedWrite()
    {
        hm.AutoRebuild = false;
        Apply(65f);
        tc.m_nview.GetZDO().SetOwner(2);
        hm.RebuildTerrain();
        Assert.Equal(0, tc.SaveCount);
        Assert.False(tc.m_modifiedHeight[Centre]);
        Assert.Equal(0, RoadTerrainModifier.PendingWriteCount);
    }

    [Fact]
    public void AChangedNetworkCancelsAnOldQueuedWrite()
    {
        hm.AutoRebuild = false;
        Apply(65f);
        RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(-20, 0), new(20, 0) }, 4f, WorldGenerator.instance);
        RoadSpatialGrid.FinalizeRoadNetwork();
        Assert.NotEqual(0, RoadSpatialGrid.RoadNetworkVersion);
        hm.RebuildTerrain();
        Assert.Equal(0, tc.SaveCount);
        Assert.Equal(0, RoadTerrainModifier.PendingWriteCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompilerDestructionAndWorldResetReleasePendingReferences(bool worldReset)
    {
        hm.AutoRebuild = false;
        Apply(65f);
        if (worldReset) RoadTerrainModifier.ResetDebugCounters();
        else RoadTerrainModifier.OnTerrainCompilerDestroyed(tc);
        Assert.Equal(0, RoadTerrainModifier.PendingWriteCount);
        hm.RebuildTerrain();
        Assert.Equal(0, tc.SaveCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidBaselineCannotWriteOrStamp(bool nonFinite)
    {
        hm.AutoRebuild = false;
        Apply(65f);
        var heights = new List<float>();
        int count = nonFinite ? 65 * 65 : 10;
        for (int i = 0; i < count; i++) heights.Add(65f);
        if (nonFinite) heights[count - 1] = float.NaN;
        RoadTerrainModifier.ApplyPendingTerrain(tc, hm, heights);
        Assert.Equal(0, tc.SaveCount);
        Assert.False(tc.m_modifiedHeight[Centre]);
    }

    [Fact]
    public void BakeCompletesQueuedWriteBeforeTemporaryTerrainIsReleased()
    {
        hm.AutoRebuild = false;
        Apply(65f);
        Assert.Equal(0, tc.SaveCount);
        hm.AuthoredHeight = (_, _) => 64f;
        RoadTerrainModifier.CompletePendingTerrain(tc, hm);
        Assert.Equal(1, tc.SaveCount);
        Assert.Equal(RoadTerrainModifier.WriteOutcome.Written, RoadTerrainModifier.LastWriteOutcome);
        Assert.Equal(0, RoadTerrainModifier.PendingWriteCount);
        Assert.Equal(1f, tc.m_levelDelta[Centre], 4);
        Assert.Equal(65f, hm.LastRenderedHeights![Centre], 4);
        RoadTerrainModifier.CompletePendingTerrain(tc, hm);
        Assert.Equal(1, tc.SaveCount);
    }

    [Fact]
    public void BakeCompletionCannotUseAnotherHeightmap()
    {
        hm.AutoRebuild = false;
        Apply(65f);
        RoadTerrainModifier.CompletePendingTerrain(tc, Heightmap.CreateForZone(new Vector2s(1, 0)));
        Assert.Equal(0, tc.SaveCount);
        Assert.Equal(1, RoadTerrainModifier.PendingWriteCount);
    }

    [Fact]
    public void BakeCompletionStillChecksOwnershipAtWriteTime()
    {
        hm.AutoRebuild = false;
        Apply(65f);
        tc.m_nview.GetZDO().SetOwner(2);
        RoadTerrainModifier.CompletePendingTerrain(tc, hm);
        Assert.Equal(0, tc.SaveCount);
        Assert.Equal(0, RoadTerrainModifier.PendingWriteCount);
    }

    public void Dispose()
    {
        RoadTerrainModifier.ResetDebugCounters();
        RoadSpatialGrid.Clear();
        Heightmap.Registered = null;
        WorldGenerator.instance = null;
        ZDOMan.instance = null;
    }
}
