using System;
using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A manual road (road_mark, road_path, road_connect) on a network this build generated is built in full, also
/// through zones explored before the network: those zones become the network's, every writer may write them, the
/// record keeps them across sessions, and the operator is told which ones. Zones off the road stay as they are. (A
/// network a released build saved takes no manual road at all: LegacyWorldTests.AppendOnA144NetworkLeavesItsBlobReadableBy144.)
/// </summary>
public sealed class ManualRoadCutoffTests : IDisposable
{
    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0; width = 0; }
    }

    /// <summary>The manual road runs east along z = 0 from x = -16 to x = 80, through this zone (x 32 to 96).</summary>
    private static readonly Vector2s Explored = new(1, 0);
    /// <summary>Explored too, far from any road.</summary>
    private static readonly Vector2s FarAway = new(6, 6);

    private readonly ValheimWorldScope _scope = new ValheimWorldScope();

    public ManualRoadCutoffTests()
    {
        RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld(); NetworkCutoff.Forget();
        _scope.WithWorld(new FlatWorld()).WithZdos().WithZoneSystem(); RoadNetworkGenerator.MarkLocationsReady();
        RoadGrade.Configured = .35f;
        RoadNetworkGenerator.RoadWidth = 4;
        RoadSiteProtection.Set(Array.Empty<RoadSiteProtection.Footprint>());
    }

    public void Dispose()
    {
        RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld(); NetworkCutoff.Forget();
        _scope.Dispose();
    }

    private static Island Island()
    {
        var i = new Island { Id = 1, Min = new Vector2(-1024, -1024), Max = new Vector2(1024, 1024), CellSize = 8, WorldOffset = 0 };
        for (int x = -128; x <= 128; x++) for (int z = -128; z <= 128; z++) i.Cells.Add(new Vector2Int(x, z));
        return i;
    }

    /// <summary>A network this build generated on an explored world: a road along z = -200, far from the manual one.</summary>
    private static void GeneratedNetworkOnAnExploredWorld()
    {
        RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(-40f, -200f), new(40f, -200f) }, 4f, WorldGenerator.instance!);
        RoadSpatialGrid.FinalizeRoadNetwork();
        RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        ZoneSystem.instance!.m_generatedZones.Add(Explored);
        ZoneSystem.instance.m_generatedZones.Add(FarAway);
        NetworkCutoff.SnapshotWorld(new[] { Explored, FarAway });
        NetworkCutoff.UseNewNetwork();
    }

    [Fact]
    public void AManualRoadThroughAnExploredZoneBuildsThereAndSaysSo()
    {
        GeneratedNetworkOnAnExploredWorld();
        Heightmap hm = _scope.RegisterHeightmap(Explored);
        TerrainComp tc = hm.m_terrainComp!;
        Assert.True(NetworkCutoff.Excludes(Explored));

        List<string> log = _scope.CaptureLog();
        var plan = ManualRoads.Prepare(new[] { new Vector2(-16f, 0f), new Vector2(80f, 0f) }, false, new[] { Island() });
        ManualRoads.Commit(plan);

        // The zone is the network's now, for every writer, and the operator is told so by name.
        Assert.False(NetworkCutoff.Excludes(Explored));
        Assert.True(NetworkCutoff.Excludes(FarAway), "a zone off the road stays as it is");
        Assert.Contains(Explored, plan.ExploredZonesWritten);
        Assert.DoesNotContain(FarAway, plan.ExploredZonesWritten);
        string warning = ManualRoads.ExploredZonesWarning(plan.ExploredZonesWritten);
        Assert.Contains("(1, 0)", warning);
        Assert.Contains(log, line => line.Contains(warning));

        // Its terrain is written, and the seam fade no longer treats it as an edge.
        RoadTerrainModifier.ApplyAdditionsToLoadedZones();
        Assert.True(tc.SaveCount > 0, "the explored zone on the manual road must get the road");
        Assert.True(RoadTerrainModifier.CarriesCurrentRoads(tc));
        Assert.Equal(1f, NetworkCutoff.SeamWeight(new Vector2(30f, 0f)));

        // The next session keeps it the network's.
        int version = RoadSpatialGrid.RoadNetworkVersion;
        byte[] record = NetworkCutoff.Record(version, ZoneSystem.instance!.m_generatedZones)!;
        NetworkCutoff.Forget();
        NetworkCutoff.SnapshotWorld(new[] { Explored, FarAway });
        NetworkCutoff.UseLoadedNetwork(version, record);
        Assert.False(NetworkCutoff.Excludes(Explored));
        Assert.True(NetworkCutoff.Excludes(FarAway));
    }

    [Fact]
    public void WithoutAManualRoadTheExploredZoneIsLeftAlone()
    {
        GeneratedNetworkOnAnExploredWorld();
        // The same road committed straight into the grid, as no command does: nothing admits the zone.
        var path = RoadSpatialGrid.PlanRoadPath(new List<Vector2> { new(-16f, 0f), new(80f, 0f) }, 4f, WorldGenerator.instance!)!;
        RoadSpatialGrid.CommitAppend(new[] { path }, RoadSpatialGrid.RoadNetworkVersion);
        Heightmap hm = _scope.RegisterHeightmap(Explored);
        RoadTerrainModifier.ApplyAdditionsToLoadedZones();
        Assert.True(NetworkCutoff.Excludes(Explored));
        Assert.Equal(0, hm.m_terrainComp!.SaveCount);
    }
}
