using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A road arriving level with a place that has no flat nor landing is carried on from its ring to the place's edge, where its
/// arrival height is measured (RoadNetworkGenerator.CarryOnToEdges), as a crypt's road is carried to its door. In game:
/// AbandonedLogCabin04 (-1867, 7101), its road at the right height but ending short of the cabin, at the ring its blend
/// keeps outside the ground the cabin shapes.
/// </summary>
public partial class SitesFirstTests
{
    /// <summary>A cabin's levelling: it only smooths round its centre out to 10 m, no flat.</summary>
    private static readonly IReadOnlyList<LevelOp> CabinSmooth = new[] { new LevelOp(0f, 0f, 0f, 0f, false, 10f, 3f) };
    /// <summary>A place with a flat 6 m round its centre.</summary>
    private static readonly IReadOnlyList<LevelOp> Flat = new[] { new LevelOp(0f, 0f, 0f, 6f, false, 10f, 3f) };

    /// <summary>The nearest road point to the place at the origin, after a road to it from 120 m north, on level ground in the
    /// Black Forest; the place's footprint 20 m round, the ground it shapes 10 m (where its own road stops, plus the road's
    /// clearance: 14 m).</summary>
    private RoadSpatialGrid.RoadPoint NearestAfterRoad(IReadOnlyList<LevelOp> ops)
    {
        _world.Biome = Heightmap.Biome.BlackForest;
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        PlacedSites.Reset();
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(new Vector2(0f, 0f), 20f, 10f) });
        LocationLevelling.Source = c => c.magnitude < 0.5f ? ops : null;
        SetPathfinder(new RoadPathfinder(_world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        Assert.Null(LocationLevelling.ApproachHeight(50f, CabinSmooth));
        Assert.NotNull(LocationLevelling.ApproachHeight(50f, Flat));

        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, 120f), 0f, new Vector2(0f, 0f), 10f, 4f, "to the cabin"),
            "last refusal: " + (RoadSpatialGrid.LastRefusal ?? "none"));
        Assert.True(RoadNetworkGenerator.Reached(Vector3.zero, 10f));
        return RoadSpatialGrid.GetRoadPointsNearPosition(Vector3.zero, 30f).Where(p => p.IsRoad).OrderBy(p => p.p.magnitude).First();
    }

    [Fact]
    public void ARoadToAPlaceWithNoFlatIsCarriedOnToItsEdge()
    {
        RoadSpatialGrid.RoadPoint nearest = NearestAfterRoad(CabinSmooth);
        Assert.InRange(nearest.p.magnitude, 9.5f, 10.5f);
        Assert.InRange(nearest.h, 50f - RoadNetworkGenerator.ReachRise, 50f + RoadNetworkGenerator.ReachRise);
        Assert.Equal(1, RoadNetworkGenerator.CarriedToEdgeCount);
    }

    /// <summary>A place with a flat keeps its road at its ring, as before.</summary>
    [Fact]
    public void ARoadToAPlaceWithAFlatKeepsItsEndAtTheRing()
    {
        RoadSpatialGrid.RoadPoint nearest = NearestAfterRoad(Flat);
        Assert.True(nearest.p.magnitude > 13f, $"the road comes {nearest.p.magnitude:F1} m from the centre");
        Assert.Equal(0, RoadNetworkGenerator.CarriedToEdgeCount);
    }
}
