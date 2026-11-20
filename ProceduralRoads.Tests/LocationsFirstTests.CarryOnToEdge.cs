using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A road arriving level with a location that has no flat nor landing is carried on from its ring to the location's edge, where its
/// arrival height is measured (RoadNetworkGenerator.CarryOnToEdges), as a crypt's road is carried to its door. In game:
/// AbandonedLogCabin04 (-1867, 7101), its road at the right height but ending short of the cabin, at the ring its blend
/// keeps outside the ground the cabin shapes.
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>A cabin's levelling: it only smooths round its centre out to 10 m, no flat.</summary>
    private static readonly IReadOnlyList<LevelOp> CabinSmooth = new[] { new LevelOp(0f, 0f, 0f, 0f, false, 10f, 3f) };
    /// <summary>A location with a flat 6 m round its centre.</summary>
    private static readonly IReadOnlyList<LevelOp> Flat = new[] { new LevelOp(0f, 0f, 0f, 6f, false, 10f, 3f) };

    /// <summary>The nearest road point to the location at the origin, after a road to it from 120 m north, on level ground in the
    /// Black Forest; the location's footprint 20 m round, the ground it shapes 10 m (where its own road stops, plus the road's
    /// clearance: 14 m).</summary>
    private RoadSpatialGrid.RoadPoint NearestAfterRoad(IReadOnlyList<LevelOp> ops, bool preloaded = true)
    {
        _world.Biome = Heightmap.Biome.BlackForest;
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        PlacedLocations.Reset();
        // Placed first with no landing to go onto (PlacedLocations.FlatlessPlaced): on the preload list
        // (RoadLocationProtection.Enterable), its ground known.
        if (preloaded) PlacedLocations.Preload(new Vector2(0f, 0f), (x, z) => _world.GetHeight(x, z));
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), 20f, 10f) });
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
    public void ARoadToALocationWithNoFlatIsCarriedOnToItsEdge()
    {
        RoadSpatialGrid.RoadPoint nearest = NearestAfterRoad(CabinSmooth);
        Assert.InRange(nearest.p.magnitude, 9.5f, 10.5f);
        Assert.InRange(nearest.h, 50f - RoadNetworkGenerator.ReachRise, 50f + RoadNetworkGenerator.ReachRise);
        Assert.Equal(1, RoadNetworkGenerator.CarriedToEdgeCount);
    }

    /// <summary>The same location not on the preload list (the game places it after its roads, its ground not known when they are
    /// planned): its road stops at its edge, its whole footprint and the road's clearance kept, nothing carried into it and
    /// no point stored into it (RoadLocationProtection.Enterable).</summary>
    [Fact]
    public void ARoadToALocationNotOnThePreloadListStopsAtItsEdge()
    {
        RoadSpatialGrid.RoadPoint nearest = NearestAfterRoad(CabinSmooth, preloaded: false);
        float edge = 20f + RoadLocationProtection.Clearance(4f);
        Assert.True(nearest.p.magnitude >= edge - 0.1f, $"the road comes {nearest.p.magnitude:F1} m from the centre, inside {edge:F1} m");
        Assert.Equal(0, RoadNetworkGenerator.CarriedToEdgeCount);
        Assert.DoesNotContain(RoadSpatialGrid.SnapshotAllRoadPoints(), p => p.intoLocation);
    }

    /// <summary>A shore landing (RoadCoastalLandings: no location, no footprint, nothing placed there) is no location the preload
    /// list keeps a road out of (RoadLocationProtection.Enterable): its road is carried on from its ring to its edge as before the
    /// list, its ground the natural ground. Two such were lost to the list on one world, their roads stopped short of them.</summary>
    [Fact]
    public void ARoadToAShoreLandingIsCarriedOnToItsEdge()
    {
        _world.Biome = Heightmap.Biome.BlackForest;
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        PlacedLocations.Reset();
        // A location elsewhere, so the footprint index is installed and the landing is in none of it.
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(600f, 600f), 20f, 10f) });
        LocationLevelling.Source = _ => null;
        SetPathfinder(new RoadPathfinder(_world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        var landing = new Vector2(0f, 0f);
        Assert.False(PlacedLocations.Preloaded(landing));

        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(0f, 120f), 0f, landing, RoadCoastalLandings.Radius, 4f, "to the landing"),
            "last refusal: " + (RoadSpatialGrid.LastRefusal ?? "none"));
        Assert.Equal(1, RoadNetworkGenerator.CarriedToEdgeCount);
        Assert.True(RoadNetworkGenerator.Reached(Vector3.zero, RoadCoastalLandings.Radius, RoadCoastalLandings.Name));
        RoadSpatialGrid.RoadPoint nearest = RoadSpatialGrid.GetRoadPointsNearPosition(Vector3.zero, 40f).Where(p => p.IsRoad).OrderBy(p => p.p.magnitude).First();
        Assert.InRange(nearest.p.magnitude, RoadCoastalLandings.Radius - 0.5f, RoadCoastalLandings.Radius + 0.5f);
        Assert.True(RoadLocationProtection.Enterable(landing));
        Assert.False(RoadLocationProtection.Enterable(new Vector2(600f, 600f)));
    }

    /// <summary>A location with a flat keeps its road at its ring, as before.</summary>
    [Fact]
    public void ARoadToALocationWithAFlatKeepsItsEndAtTheRing()
    {
        RoadSpatialGrid.RoadPoint nearest = NearestAfterRoad(Flat);
        Assert.True(nearest.p.magnitude > 13f, $"the road comes {nearest.p.magnitude:F1} m from the centre");
        Assert.Equal(0, RoadNetworkGenerator.CarriedToEdgeCount);
    }
}
