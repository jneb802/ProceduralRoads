using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A bridge layout change (BridgeLayout.LayoutVersion) on a world that already has bridges. The hard rule for old
/// worlds: what is built stays built. Zones whose pieces stand keep them; only zones without pieces get the new layout.
/// </summary>
public sealed class BridgeLayoutTests
{
    [Fact]
    public void AStaleLayoutLeavesSpawnedZonesAlone()
    {
        var world = new SyntheticWorld { HasRiver = false, HasMountain = false };
        using var scope = new ValheimWorldScope().WithWorld(world).WithZdos().WithZoneSystem().WithScene().AsServer();
        RoadSpatialGrid.Clear(); RoadNetworkPersistence.Reset(); RoadNetworkGenerator.Reset();
        try
        {
            RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(-40f, 0f), new(40f, 0f) }, 4f, world);
            RoadSpatialGrid.FinalizeRoadNetwork();
            RoadNetworkPersistence.EnsureMetadataInstance();
            RoadNetworkPersistence.SaveGlobalRoadData(new List<(Vector2 position, string label)>(), new List<RoadCrossing>(),
                new HashSet<Vector2s>());

            // The record an older build wrote: format 1, the narrow single-lane layout, one zone with its pieces.
            var zone = new Vector2s(3, 4);
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(1); w.Write(1); w.Write((int)zone.x); w.Write((int)zone.y);
                LegacyWorldTests.Metadata().Set("ProceduralRoads_BridgeZones".GetStableHashCode(), ms.ToArray());
            }
            ZDO piece = ZDOMan.instance.CreateNewZDO(ZoneSystem.GetZonePos(zone), "wood_beam".GetStableHashCode());
            piece.Persistent = true;
            piece.Set(StructurePlans.MarkerHash, 1);

            RoadSpatialGrid.Clear(); RoadNetworkPersistence.Reset();
            Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData());

            Assert.True(StructurePlans.IsSpawned(zone), "the zone's old pieces must stay its pieces");
            Assert.Contains(piece, ZDOMan.instance.Zdos);
            Assert.DoesNotContain(piece, ZDOMan.instance.DestroyQueue);   // destruction is queued in the doubles
            Assert.Equal(1, piece.GetInt(StructurePlans.MarkerHash));
        }
        finally
        {
            RoadNetworkPersistence.Reset(); RoadSpatialGrid.Clear(); RoadNetworkGenerator.Reset();
        }
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, pathfinder);

    /// <summary>
    /// A generated bridge that would put any piece in a zone explored before the network is not built at all, in any
    /// zone: no half bridge. A bridge wholly in zones the network writes is planned exactly as before.
    /// </summary>
    [Fact]
    public void ABridgeReachingAnExcludedZoneIsNotBuiltAnywhere()
    {
        var world = new SyntheticWorld { HasRiver = true, HasMountain = false };
        using var scope = new ValheimWorldScope().WithWorld(world).WithZoneSystem();
        NetworkCutoff.Forget();
        RoadNetworkGenerator.Reset();
        SetPathfinder(new RoadPathfinder(world) { Fords = true, Bridges = true, MaxGrade = 0f });
        try
        {
            Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-300f, 0f), 0f, new Vector2(400f, 0f), 0f, 4f, "Cross river"));
            var crossing = Assert.Single(RoadNetworkGenerator.GetRoadCrossings());
            List<Vector2s> zones = BridgeStructures.Solve(crossing, world, world.GetSeed())
                .Select(p => ZoneSystem.GetZone(p.Position)).Distinct().ToList();
            Assert.True(zones.Count >= 2, $"the bridge spans {zones.Count} zone(s); the test needs one that crosses a zone line");

            // A fresh world: every zone's plan, untouched.
            NetworkCutoff.SnapshotWorld(new Vector2s[0]);
            NetworkCutoff.UseNewNetwork();
            foreach (Vector2s zone in zones)
                Assert.Same(StructurePlans.PlanFor(zone), StructurePlans.BuildablePlanFor(zone));

            // Explored land elsewhere: still untouched.
            NetworkCutoff.SnapshotWorld(new[] { new Vector2s(40, 40) });
            NetworkCutoff.UseNewNetwork();
            foreach (Vector2s zone in zones)
                Assert.Same(StructurePlans.PlanFor(zone), StructurePlans.BuildablePlanFor(zone));

            // One of its zones explored before the network: no zone of this bridge gets a piece.
            foreach (Vector2s explored in zones)
            {
                NetworkCutoff.SnapshotWorld(new[] { explored });
                NetworkCutoff.UseNewNetwork();
                foreach (Vector2s zone in zones)
                {
                    Assert.NotNull(StructurePlans.PlanFor(zone));
                    Assert.Null(StructurePlans.BuildablePlanFor(zone));
                }
            }
        }
        finally
        {
            SetPathfinder(null);
            NetworkCutoff.Forget();
            RoadNetworkGenerator.Reset();
            RoadCrossingDetector.SetFordStyleWeights(1f, 1f, 1f);
        }
    }
}
