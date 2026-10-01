using System;
using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// (d) A manual road appended through a loaded zone whose terrain compiler another peer owns: this server must leave
/// that zone exactly as it is, keep its new road points owed across a save and restart, and write them once the other
/// peer lets the compiler go, without touching anything outside the road's reach.
/// </summary>
public sealed class ForeignOwnerAppendTests : IDisposable
{
    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0; width = 0; }
    }
    private const long OtherPeer = 99;
    private readonly ValheimWorldScope _world = new ValheimWorldScope().WithWorld(new FlatWorld()).WithZdos().WithZoneSystem().WithScene().AsServer();
    private readonly StaticOverride _plain = PlainEarthworks.Apply().And(() => RoadTerrainModifier.BatterPerMetre, 0f);

    public ForeignOwnerAppendTests()
    {
        RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetDebugCounters();
        RoadNetworkGenerator.MarkLocationsReady(); RoadSiteProtection.Set(Array.Empty<RoadSiteProtection.Footprint>());
        RoadGrade.Configured = .35f; RoadNetworkGenerator.RoadWidth = 4;
    }
    public void Dispose() { RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetDebugCounters(); _plain.Dispose(); _world.Dispose(); }

    private static Island Group()
    {
        var island = new Island { Id = 1, CellSize = 8, WorldOffset = 0, Min = new Vector2(-512, -512), Max = new Vector2(512, 512) };
        for (int x = -64; x <= 64; x++) for (int z = -64; z <= 64; z++) island.Cells.Add(new Vector2Int(x, z));
        return island;
    }
    private static void Reload()
    {
        RoadLifecycleManager.OnPrepareSave(); RoadNetworkGenerator.Reset();
        Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData()); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
    }
    private static bool Owed(Vector2s zone, TerrainComp compiler) =>
        RoadSpatialGrid.PendingPoints(RoadSpatialGrid.GetRoadPointsInZone(zone), compiler.m_nview.GetZDO().GetInt(RoadTerrainModifier.AppliedVersionHash)).Count > 0;

    [Fact]
    public void AZoneAnotherPeerOwnsIsLeftAloneStaysOwedAcrossARestartAndIsWrittenOnceReleased()
    {
        // A base road elsewhere, so the append is an addition to a loaded network.
        ManualRoads.Commit(ManualRoads.Prepare(new[] { new Vector2(-200, -200), new Vector2(-120, -200) }, false, new[] { Group() }));
        var zone = new Vector2s(1, 0);
        var hm = _world.RegisterHeightmap(zone); var compiler = hm.m_terrainComp!;
        compiler.m_nview.GetZDO().SetOwner(OtherPeer);
        for (int i = 0; i < compiler.m_levelDelta.Length; i++) { compiler.m_levelDelta[i] = 0.3f; compiler.m_modifiedHeight[i] = true; }
        var before = TerrainSnapshot.Of(compiler);

        // The append runs east-west through zone (1,0), whose centre is x = 64.
        ManualRoads.Commit(ManualRoads.Prepare(new[] { new Vector2(20, 0), new Vector2(110, 0) }, false, new[] { Group() }));
        Assert.NotEmpty(RoadSpatialGrid.GetRoadPointsInZone(zone));
        RoadTerrainModifier.ApplyAdditionsToLoadedZones();
        RoadTerrainModifier.SweepUnstamped();
        TerrainAssert.Unchanged(before, compiler, "the other peer's zone");
        Assert.Equal(OtherPeer, compiler.m_nview.GetZDO().GetOwner());
        Assert.True(Owed(zone, compiler), "the zone's new road points must stay owed");

        Reload();
        Assert.True(Owed(zone, compiler), "a restart must not forget what the zone is owed");
        RoadTerrainModifier.ApplyAdditionsToLoadedZones();
        TerrainAssert.Unchanged(before, compiler, "the other peer's zone after the restart");

        compiler.m_nview.GetZDO().SetOwner(0); // The other peer left the area; the game releases the compiler.
        Assert.Equal(1, RoadTerrainModifier.SweepUnstamped());
        Assert.False(Owed(zone, compiler), "once released, the zone must carry the road");
        Assert.True(RoadTerrainModifier.CarriesCurrentRoads(compiler));
        float reach = RoadTerrainModifier.MaxInfluenceRadius(4f) + hm.m_scale;
        TerrainAssert.OnlyChangedWithin(before, compiler, (x, z) => Mathf.Abs(z) <= reach, "the released zone");
    }
}
