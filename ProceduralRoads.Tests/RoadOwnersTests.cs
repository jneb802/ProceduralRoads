using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The debug record of which generated road stored which points (RoadOwners, read by road_owner): a road generation lays
/// is found by its label near its points, nothing is found away from them, and the record goes with the network.
/// </summary>
[Collection("RoadStatics")]
public class RoadOwnersTests
{
    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f ? 0f : 40f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
        public override int GetSeed() => 77;
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, pathfinder);

    [Fact]
    public void AGeneratedRoadIsFoundByItsLabelNearItsPoints()
    {
        bool enabled = RoadOwners.Enabled;
        using var scope = new ValheimWorldScope();
        try
        {
            RoadOwners.Enabled = true;
            RoadNetworkGenerator.Reset();
            var world = new FlatWorld();
            scope.WithWorld(world);
            SetPathfinder(new RoadPathfinder(world) { TerrainVarianceThreshold = 1000f, Meander = 0f });
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, -80f), 0f, new Vector2(0f, 80f), 0f, 4f, "trunk"));
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-80f, 10f), 0f, new Vector2(0f, 10f), 0f, 4f, "tee"));
            var onTrunk = RoadOwners.Near(0f, -40f, 3f);
            Assert.Contains(onTrunk, line => line.StartsWith("trunk (road)"));
            Assert.DoesNotContain(onTrunk, line => line.StartsWith("tee"));
            Assert.Contains(RoadOwners.Near(-40f, 10f, 3f), line => line.StartsWith("tee (road)"));
            Assert.Empty(RoadOwners.Near(-60f, -60f, 5f));
            RoadNetworkGenerator.Reset();
            Assert.Equal(0, RoadOwners.Count);
        }
        finally { SetPathfinder(null); RoadOwners.Enabled = enabled; RoadNetworkGenerator.Reset(); }
    }

    /// <summary>A manual road is recorded under its label, as a generated one is (ManualRoads' append).</summary>
    [Fact]
    public void AManualRoadIsFoundByItsLabelNearItsPoints()
    {
        bool enabled = RoadOwners.Enabled;
        using var scope = new ValheimWorldScope().WithNetwork(server: true);
        try
        {
            RoadOwners.Enabled = true;
            RoadNetworkGenerator.Reset();
            scope.WithWorld(new FlatWorld()).WithZdos().WithZoneSystem().WithScene().AsServer();
            RoadNetworkGenerator.MarkLocationsReady();
            var older = new List<Vector2>();
            for (float z = -60f; z <= -20f + 0.01f; z += 2f) older.Add(new Vector2(-24f, z));
            RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(older, older.ConvertAll(_ => 40f), older.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 40f, false));
            RoadSpatialGrid.FinalizeRoadNetwork(); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
            int before = RoadSpatialGrid.RoadNetworkVersion;
            var road = new List<Vector2>();
            for (float x = -60f; x <= 0f + 0.01f; x += 1f) road.Add(new Vector2(x, 0f));
            var piece = new RoadSpatialGrid.PlannedPath(road, road.ConvertAll(_ => 40f), road.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 60f, false);
            RoadNetworkGenerator.AppendManualRoad(new[] { piece }, new List<RoadCrossing>(), Array.Empty<RoadStairSpan>(), road[0], before);
            Assert.Contains(RoadOwners.Near(-30f, 0f, 3f), line => line.StartsWith("Manual road") && line.Contains("(manual road)"));
        }
        finally { RoadOwners.Enabled = enabled; RoadNetworkGenerator.Reset(); }
    }

    /// <summary>road_regen_island replaces the whole network (RoadNetworkGenerator.ResetData before its island's roads): no
    /// record of a road from the network it replaced is left for road_owner to name, and the island's new roads are listed.</summary>
    [Fact]
    public void ARegeneratedIslandLeavesNoRecordOfTheRoadsItReplaced()
    {
        bool enabled = RoadOwners.Enabled;
        using var scope = new ValheimWorldScope();
        try
        {
            RoadOwners.Enabled = true;
            var world = new SyntheticWorld { HasRiver = false, HasMountain = false };
            scope.WithWorld(world).WithZoneSystem();
            foreach (var (name, x, z, radius) in new[] { ("StartTemple", 0f, 0f, 25f), ("Eikthyrnir", 260f, 40f, 10f), ("WoodVillage1", -220f, 150f, 18f) })
                TestLocations.Add(ZoneSystem.instance!, new ZoneSystem.LocationInstance
                {
                    m_location = new ZoneSystem.ZoneLocation { m_prefab = new SoftReferenceableAssets.SoftReference<GameObject>(name), m_exteriorRadius = radius },
                    m_position = new Vector3(x, world.GetHeight(x, z), z),
                });
            scope.WithZdos().WithNetwork(server: true);
            RoadNetworkGenerator.Reset();
            RoadNetworkGenerator.GenerateRoads(force: true);
            Assert.True(RoadOwners.Count > 0);
            // A road of the network about to be replaced, far from any of the island's roads.
            RoadOwners.Record("replaced road", "road", new[] { new Vector2(-900f, -900f), new Vector2(-890f, -900f) });
            Assert.NotEmpty(RoadOwners.Near(-895f, -900f, 5f));
            Assert.True(RoadNetworkGenerator.RegenerateIslandAt(Vector3.zero, out string summary), summary);
            Assert.Empty(RoadOwners.Near(-895f, -900f, 5f));
            Assert.True(RoadOwners.Count > 0, "the island's new roads are listed");
        }
        finally
        {
            RoadOwners.Enabled = enabled;
            if (ZoneSystem.instance != null) RoadLifecycleManager.OnZoneSystemDestroy(ZoneSystem.instance);
            RoadNetworkGenerator.Reset();
        }
    }
}
