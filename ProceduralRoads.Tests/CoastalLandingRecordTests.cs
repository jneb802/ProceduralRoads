using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>The shore landings of a generated network are recorded with the destinations: each with its island, whether the
/// island took it and whether the network reaches it, saved with the network and read back as saved.</summary>
public sealed class CoastalLandingRecordTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    public void Dispose()
    {
        RoadNetworkGenerator.Reset();
        RoadNetworkPersistence.Reset();
        _scope.Dispose();
    }

    /// <summary>Meadows at 33 m inside 600 m, a gentle beach falling to deep water beyond.</summary>
    private sealed class Island600 : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            float r = Mathf.Max(Mathf.Abs(wx), Mathf.Abs(wy));
            return r <= 600f ? 33f : Mathf.Max(5f, 33f - (r - 600f) * 0.25f);
        }

        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            GetHeight(wx, wy) < RoadConstants.SeaLevel - 2f ? Heightmap.Biome.Ocean : Heightmap.Biome.Meadows;

        public override int GetSeed() => 91;
    }

    private void Generate()
    {
        var world = new Island600();
        _scope.WithWorld(world).WithZoneSystem().WithZdos().WithNetwork(server: true);
        foreach ((string name, float x, float z, float radius) in new[]
                 { ("Crypt4", -200f, 100f, 18f), ("Crypt4", 150f, -100f, 18f), ("Eikthyrnir", 100f, 200f, 10f) })
            TestLocations.Add(ZoneSystem.instance!, new ZoneSystem.LocationInstance
            {
                m_location = new ZoneSystem.ZoneLocation { m_prefab = new SoftReferenceableAssets.SoftReference<GameObject>(name), m_exteriorRadius = radius },
                m_position = new Vector3(x, world.GetHeight(x, z), z),
            });
        RoadNetworkGenerator.Reset();
        RoadNetworkGenerator.GenerateRoads(force: true);
    }

    [Fact]
    public void AGeneratedNetworkRecordsItsLandingsWithTheirIslandChoiceAndReach()
    {
        Generate();
        IReadOnlyList<CoastalLandingPlace> landings = RoadNetworkGenerator.GetCoastalLandings();
        Assert.NotEmpty(landings);
        Assert.All(landings, l => Assert.Equal(RoadCoastalLandings.Name, l.Name));
        Assert.Contains(landings, l => l.Chosen);
        Assert.Contains(landings, l => l.Reached);
        // The generator's own tally and the record agree on the chosen ones that the network reaches.
        Assert.All(landings.Where(l => l.Chosen), l =>
            Assert.Equal(RoadNetworkGenerator.Reached(l.Position, RoadCoastalLandings.Radius, RoadCoastalLandings.Name), l.Reached));
        Assert.Equal(landings.OrderBy(l => l.IslandId).ThenBy(l => l.Position.x).ThenBy(l => l.Position.z).Select(l => l.Position),
            landings.Select(l => l.Position));
    }

    [Fact]
    public void LandingsSaveWithTheNetworkAndLoadBackAsSaved()
    {
        Generate();
        List<CoastalLandingPlace> before = RoadNetworkGenerator.GetCoastalLandings().ToList();
        Assert.NotEmpty(before);

        RoadNetworkGenerator.SaveGlobalRoadData();
        RoadNetworkGenerator.Reset();
        Assert.Empty(RoadNetworkGenerator.GetCoastalLandings());
        Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData(), $"{RoadSpatialGrid.LastReadRefusal}");

        List<CoastalLandingPlace> after = RoadNetworkGenerator.GetCoastalLandings().ToList();
        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Position, after[i].Position);
            Assert.Equal(before[i].IslandId, after[i].IslandId);
            Assert.Equal(before[i].Chosen, after[i].Chosen);
            Assert.Equal(before[i].Reached, after[i].Reached);
        }
    }

    [Fact]
    public void ARecordThatCannotBeReadIsKeptAndTheRoadsStillLoad()
    {
        Generate();
        RoadNetworkGenerator.SaveGlobalRoadData();
        RoadNetworkGenerator.Reset();
        byte[] saved = RoadNetworkPersistence.SavedRecord("ProceduralRoads_CoastLandings")!;
        byte[] newer = (byte[])saved.Clone();
        BitConverter.GetBytes(RoadNetworkPersistence.CoastLandingsFormat + 1).CopyTo(newer, 0);
        RoadNetworkPersistence.SetSavedRecord("ProceduralRoads_CoastLandings", newer);

        Assert.True(RoadNetworkGenerator.TryLoadGlobalRoadData());
        Assert.Empty(RoadNetworkGenerator.GetCoastalLandings());
        RoadNetworkGenerator.SaveGlobalRoadData();
        Assert.Equal(newer, RoadNetworkPersistence.SavedRecord("ProceduralRoads_CoastLandings"));
    }
}
