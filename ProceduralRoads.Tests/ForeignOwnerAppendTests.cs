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
        RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld();
        RoadNetworkGenerator.MarkLocationsReady(); RoadSiteProtection.Set(Array.Empty<RoadSiteProtection.Footprint>());
        RoadGrade.Configured = .35f; RoadNetworkGenerator.RoadWidth = 4;
    }
    public void Dispose() { RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld(); _plain.Dispose(); _world.Dispose(); }

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

}
