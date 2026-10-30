using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

public partial class SitesFirstTests
{
    /// <summary>The cave placed on a plateau 10 m over the level ground out to 12 m from its centre, its edge falling 1 in 1
    /// to the ground 22 m out: too steep for a road onto its landing, a staircase climbs it. <paramref name="prefab"/> in
    /// place of the cave's own.</summary>
    private PlacedSite OnAPlateau(string? prefab = null)
    {
        RoadNetworkGenerator.Reset();
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(new Vector2(0f, 0f), Footprint) });
        PlacedSites.Reset();
        PlacedSite cave = Cave();
        PlacedSiteRecord record = cave.Record;
        if (prefab != null) record.Prefab = prefab;
        Func<float, float, float> plateau = (x, z) => cave.Ground(x, z) + Mathf.Clamp(10f - (new Vector2(x, z).magnitude - 12f), 0f, 10f);
        var site = new PlacedSite { Record = record, Ground = plateau, Shape = PlacedSiteShape.Of(cave.Centre, 90f, CaveFloor[0], 0f, null, plateau, false, 60f) };
        PlacedSites.Ready(site);
        LocationLevelling.Source = c => c.magnitude < 0.5f ? CaveFloor : null;
        SetPathfinder(new RoadPathfinder(_world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        return site;
    }

    /// <summary>Outside the Mountains a road to a placed place is still carried on onto its landing where a road gets there,
    /// with no staircase anywhere (<see cref="RoadNetworkGenerator.ApproachStairsAt"/>).</summary>
    [Fact]
    public void APlacedPlaceOutsideTheMountainsStillGetsARoadOntoItsLanding()
    {
        _world.Biome = Heightmap.Biome.BlackForest;
        RoadNetworkGenerator.Reset();
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(new Vector2(0f, 0f), Footprint) });
        PlacedSites.Reset();
        PlacedSite cave = Cave();
        PlacedSites.Ready(cave);
        LocationLevelling.Source = c => c.magnitude < 0.5f ? CaveFloor : null;
        SetPathfinder(new RoadPathfinder(_world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });

        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, 120f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the cave"),
            "last refusal: " + (RoadSpatialGrid.LastRefusal ?? "none"));

        Assert.True(PlacedSites.Landed(cave.Centre));
        Assert.Empty(StairStructures.Runs);
        Assert.True(RoadNetworkGenerator.Reached(Vector3.zero, 10f));
    }

    /// <summary>A placed place whose landing only a staircase climbs to: in the Mountains the staircase is laid onto it; elsewhere
    /// none is, and the road is kept, ending at the place's ring, which reaches it. It was refused there before
    /// ("no way onto a placed place's landing").</summary>
    [Fact]
    public void APlacedPlaceOutsideTheMountainsGetsNoStaircaseOntoItsLandingAndKeepsItsRoad()
    {
        PlacedSite cave = OnAPlateau();
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, 120f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the cave"),
            "last refusal: " + (RoadSpatialGrid.LastRefusal ?? "none"));
        Assert.True(PlacedSites.Landed(cave.Centre));
        Assert.Single(StairStructures.Runs);

        _world.Biome = Heightmap.Biome.Plains;
        cave = OnAPlateau();
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, 120f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the cave"),
            "last refusal: " + (RoadSpatialGrid.LastRefusal ?? "none"));
        Assert.False(PlacedSites.Landed(cave.Centre));
        Assert.Empty(StairStructures.Runs);
        float nearest = RoadSpatialGrid.GetRoadPointsNearPosition(Vector3.zero, 60f).Where(p => p.IsRoad).Min(p => p.p.magnitude);
        Assert.True(nearest > 20f && nearest < 32f, $"the road comes {nearest:F1} m from the cave's centre");
        Assert.False(RoadNetworkGenerator.LandingReached(cave));
        Assert.True(RoadNetworkGenerator.Reached(Vector3.zero, 10f));
    }

    /// <summary>A boss's entrance keeps its staircase onto its landing outside the Mountains too
    /// (<see cref="RoadNetworkGenerator.ApproachStairsAt"/>): where no road gets onto its landing, a staircase is laid onto it
    /// and it is landed, reached on its own landing as before (<see cref="RoadNetworkGenerator.OwnLandingOnly"/>).</summary>
    [Fact]
    public void ABossEntranceOutsideTheMountainsWhoseLandingNoRoadGetsOntoGetsItsStaircase()
    {
        _world.Biome = Heightmap.Biome.Mistlands;
        PlacedSite boss = OnAPlateau("Mistlands_DvergrBossEntrance1");

        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, 120f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the entrance"),
            "last refusal: " + (RoadSpatialGrid.LastRefusal ?? "none"));

        Assert.Single(StairStructures.Runs);
        Assert.True(PlacedSites.Landed(boss.Centre));
        Assert.True(RoadNetworkGenerator.Reached(Vector3.zero, 10f));
    }
}
