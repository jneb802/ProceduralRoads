using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A network this build generates and saves is read back by this same build. Generated whole, on a world with
/// a wading ford, a bridge and a ridge climbed by two staircases, saved through the metadata ZDO and loaded
/// through the same door a world load uses.
///
/// Every world generated with a staircase was refused at its next load as "a saved road
/// network this build cannot read", and so had no roads. A staircase's cuts make the network encoding 4,
/// which carries the append block whether or not anything was appended; the reader still demanded at least one
/// append there, as it may for encoding 3. Two such worlds (5479632 and 5579723 bytes) both failed on exactly that check, and a modded client refused the
/// server's snapshot of such a network on the same check.
/// </summary>
public sealed class NetworkRoundTripTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        RoadNetworkPersistence.Reset();
        _scope.Dispose();
    }

    /// <summary>Meadows at 33 m inside 400 m, sea beyond. A knee-deep gully at x = -150 (a ford), a river
    /// 11 m deep and 40 m wide at x = -280 (a bridge), and a ridge along z at x = +120: faces of 1.2 rising
    /// 24 m to a 32 m top, too steep for road, so it is climbed on one staircase and descended on another.</summary>
    private sealed class MixedWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 400f || Mathf.Abs(wy) > 400f) return 0f;
            float h = 33f;
            float gully = Mathf.Abs(wx + 150f);
            if (gully < 22f) h = Mathf.Lerp(29.5f, 33f, Mathf.Clamp01((gully - 10f) / 12f));
            if (Mathf.Abs(wx + 280f) < 20f) h = 22f;
            return h + Mathf.Clamp(24f - (Mathf.Abs(wx - 120f) - 16f) * 1.2f, 0f, 24f);
        }

        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            GetHeight(wx, wy) < RoadConstants.SeaLevel - 2f ? Heightmap.Biome.Ocean : Heightmap.Biome.Meadows;

        public override void GetRiverWeight(float wx, float wy, out float weight, out float width)
        {
            float gully = Mathf.Abs(wx + 150f), river = Mathf.Abs(wx + 280f);
            if (gully < 24f) { weight = Mathf.Clamp01(1f - gully / 24f); width = 48f; return; }
            if (river < 40f) { weight = Mathf.Clamp01(1f - river / 40f); width = 80f; return; }
            weight = 0f; width = 0f;
        }

        public override int GetSeed() => 77;
    }

    [Fact]
    public void AGeneratedNetworkWithFordsBridgesAndStaircasesLoadsBackAsItWasSaved()
    {
        var world = new MixedWorld();
        _scope.WithWorld(world).WithZoneSystem();
        foreach ((string name, float x, float z, float radius) in new[]
                 {
                     ("StartTemple", -60f, 0f, 25f), ("Eikthyrnir", 260f, 30f, 10f),
                     ("Crypt4", -360f, 60f, 18f), ("Crypt4", -220f, -200f, 18f),
                 })
            TestLocations.Add(ZoneSystem.instance!, new ZoneSystem.LocationInstance
            {
                m_location = new ZoneSystem.ZoneLocation { m_prefab = new SoftReferenceableAssets.SoftReference<GameObject>(name), m_exteriorRadius = radius },
                m_position = new Vector3(x, world.GetHeight(x, z), z),
            });
        _scope.WithZdos().WithNetwork(server: true);
        RoadNetworkGenerator.Reset();

        RoadNetworkGenerator.GenerateRoads(force: true);

        // What makes this network realistic, so the test proves something: every kind of point and structure.
        List<RoadSpatialGrid.RoadPoint> points = RoadSpatialGrid.SnapshotAllRoadPoints();
        Assert.Contains(points, p => p.IsRoad);
        Assert.Contains(points, p => p.cutOnly);           // a staircase's earthwork
        Assert.Contains(points, p => p.paintOnly);         // a waded ford
        Assert.DoesNotContain(points, p => p.addition > 0); // generated, nothing appended: the case that broke
        Assert.True(StairStructures.Runs.Count >= 2, $"{StairStructures.Runs.Count} staircase(s); the ridge should take two");
        List<RoadCrossing> crossings = RoadNetworkGenerator.GetRoadCrossings().ToList();
        Assert.Contains(crossings, c => c.Kind == CrossingKind.Ford);
        Assert.Contains(crossings, c => c.Kind == CrossingKind.Bridge);

        byte[] grid = RoadSpatialGrid.SerializeAllRoadPoints()!;
        int version = RoadSpatialGrid.RoadNetworkVersion;
        int stairs = StairStructures.Runs.Count;
        int stairPieces = StairStructures.Runs.Sum(r => r.Pieces.Count);
        Assert.Equal(4, BitConverter.ToInt32(grid, 0));
        Assert.True(BitConverter.ToInt32(grid, 4) >= 4, $"encoding {BitConverter.ToInt32(grid, 4)}: the cuts are not in the saved network");

        RoadNetworkGenerator.SaveGlobalRoadData();
        RoadNetworkGenerator.Reset();
        Assert.Equal(0, RoadSpatialGrid.StoredPointRecords);
        Assert.Empty(StairStructures.Runs);

        Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData(),
            $"the network this build saved did not load back: {RoadSpatialGrid.LastReadRefusal}");
        Assert.False(RoadNetworkPersistence.LastLoadUnreadable);
        Assert.Null(RoadSpatialGrid.LastReadRefusal);
        Assert.Equal(grid, RoadSpatialGrid.SerializeAllRoadPoints());
        Assert.Equal(version, RoadSpatialGrid.RoadNetworkVersion);
        Assert.Equal(crossings.Count, RoadNetworkGenerator.GetRoadCrossings().Count);
        Assert.Equal(stairs, StairStructures.Runs.Count);
        Assert.Equal(stairPieces, StairStructures.Runs.Sum(r => r.Pieces.Count));
    }
}
