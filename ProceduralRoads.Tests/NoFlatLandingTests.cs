using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;
using Valheim.Testing;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// A location with no flat (a cabin that only smooths round its centre) gets the level landing every location's road end gets
/// (RoadSpatialGrid.LocationEnds, from RoadNetworkGenerator.ApproachGround), where the road arrives at its ring, at its ground
/// there, and the same whether the location is the road's start or its end. In game: AbandonedLogCabin04 (-1867, 7101), placed
/// with no landing to go onto, its road climbing on into the cabin's edge without levelling out.
/// </summary>
[Collection("RoadStatics")]
public partial class NoFlatLandingTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    /// <summary>The suite's plain roads have no landing (PlainRoads): the mod's own landing, pitches and ground-following.</summary>
    private readonly IDisposable _landing = StaticOverride.Keep(() => RoadPitches.Enabled).AndKeep(() => RoadPitches.JunctionLanding)
        .AndKeep(() => RoadSpatialGrid.FollowGround);

    /// <summary>Ground rising north at <see cref="Slope"/> from 50 m at the origin, out to 200 m; sea beyond. Black Forest: no
    /// approach staircase onto a location.</summary>
    private sealed class SlopeWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f ? 0f : 50f + Slope * wy;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.BlackForest;
        public override int GetSeed() => 77;
    }

    private const float Slope = 0.15f;
    private const float Width = 4f;
    private const float Radius = 10f;
    private static readonly Vector2 Far = new(0f, 120f);
    private static readonly Vector2 Origin = new(0f, 0f);
    /// <summary>A cabin's levelling: it only smooths round its centre out to 10 m, no flat.</summary>
    private static readonly IReadOnlyList<LevelOp> CabinSmooth = new[] { new LevelOp(0f, 0f, 0f, 0f, false, 10f, 3f) };
    /// <summary>A location with a flat 6 m round its centre.</summary>
    private static readonly IReadOnlyList<LevelOp> Flat = new[] { new LevelOp(0f, 0f, 0f, 6f, false, 10f, 3f) };
    private readonly SlopeWorld _world = new();

    public NoFlatLandingTests()
    {
        _scope.WithWorld(_world);
        RoadPitches.Enabled = true;
        RoadPitches.JunctionLanding = 6f;
        RoadSpatialGrid.FollowGround = true;
    }

    public void Dispose()
    {
        SetPathfinder(null);
        PlacedLocations.Reset();
        LocationLevelling.Source = null;
        RoadLocationProtection.Reset();
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        _landing.Dispose();
        _scope.Dispose();
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, pathfinder);

    private void Setup(IReadOnlyList<LevelOp> ops)
    {
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        PlacedLocations.Reset();
        // Placed first with no landing to go onto (PlacedLocations.FlatlessPlaced): on the preload list, so its road may be
        // carried on into it (RoadLocationProtection.Enterable).
        PlacedLocations.Preload(new Vector2(0f, 0f));
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), 20f, Radius) });
        LocationLevelling.Source = c => c.magnitude < 0.5f ? ops : null;
        SetPathfinder(new RoadPathfinder(_world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
    }

    /// <summary>The road's own points near the location at the origin after a road between it and <see cref="Far"/>, the location
    /// the road's start or its end; with how many of the road's ends were stored on a location's level landing.</summary>
    private (List<RoadSpatialGrid.RoadPoint> near, int landings) Road(IReadOnlyList<LevelOp> ops, bool locationAtStart)
    {
        Setup(ops);
        bool built = locationAtStart
            ? RoadNetworkGenerator.GenerateRoad(Origin, Radius, Far, 0f, Width, "from the location")
            : RoadNetworkGenerator.GenerateRoad(Far, 0f, Origin, Radius, Width, "to the location");
        Assert.True(built, "last refusal: " + (RoadSpatialGrid.LastRefusal ?? "none"));
        var near = RoadSpatialGrid.GetRoadPointsNearPosition(Vector3.zero, 40f).Where(p => p.IsRoad).OrderBy(p => p.p.magnitude).ToList();
        return (near, RoadSpatialGrid.LocationLandingsStored);
    }

    /// <summary>The road's height at <paramref name="distance"/> from the location's centre, between its points either side.</summary>
    private static float HeightAt(List<RoadSpatialGrid.RoadPoint> near, float distance)
    {
        var inside = near.Where(p => p.p.magnitude <= distance).ToList();
        var outside = near.Where(p => p.p.magnitude >= distance).ToList();
        if (inside.Count == 0) return outside[0].h;
        if (outside.Count == 0) return inside[inside.Count - 1].h;
        RoadSpatialGrid.RoadPoint inner = inside[inside.Count - 1], outer = outside[0];
        float span = outer.p.magnitude - inner.p.magnitude;
        return span < 1e-3f ? inner.h : Mathf.Lerp(inner.h, outer.h, (distance - inner.p.magnitude) / span);
    }

    /// <summary>The distances out from the location's centre the landing is read at: from its edge (where it is arrived at,
    /// the footprint's arrival radius) to its ring and on along the road over the landing's length, a metre apart.</summary>
    private static IEnumerable<float> OverTheLanding()
    {
        float ring = RoadNetworkGenerator.KeepOutEdge(Origin, Radius, Width);
        for (float d = Radius; d <= ring + RoadPitches.JunctionLanding - 1f + 1e-3f; d += 1f) yield return d;
    }

    /// <summary>The road is level from the location's edge, at the location's ground there, out over its landing: within the
    /// landing's grade of that height all the way, on ground rising at <see cref="Slope"/>.</summary>
    private static void AssertLevelLanding(List<RoadSpatialGrid.RoadPoint> near, string which)
    {
        float edge = RoadNetworkGenerator.ArrivalHeight(Origin, Radius, new Vector2(0f, Radius))!.Value;
        Assert.InRange(near[0].p.magnitude, Radius - 0.5f, Radius + 0.5f);
        foreach (float d in OverTheLanding())
        {
            float h = HeightAt(near, d), allowed = RoadPitches.JunctionLandingGrade * (d - Radius) + 0.02f;
            Assert.True(Mathf.Abs(h - edge) <= allowed,
                $"the road's {which} at the location: {h:F2} m at {d:F1} m out, {h - edge:+0.00;-0.00} m off the location's ground at its edge ({edge:F2}); ground there {50f + Slope * d:F2}");
        }
    }

    /// <summary>The road's end at the location carried on to its edge (CarryOnToEdges), both stored on a level landing: the
    /// road's own end at the ring and the stretch on into the location.</summary>
    [Fact]
    public void ARoadEndingAtALocationWithNoFlatEndsOnALevelLanding()
    {
        var (near, landings) = Road(CabinSmooth, locationAtStart: false);
        Assert.Equal(1, RoadNetworkGenerator.CarriedToEdgeCount);
        Assert.Equal(2, landings);
        AssertLevelLanding(near, "end");
    }

    [Fact]
    public void ARoadStartingAtALocationWithNoFlatStartsOnTheSameLanding()
    {
        var (atEnd, endLandings) = Road(CabinSmooth, locationAtStart: false);
        var (atStart, startLandings) = Road(CabinSmooth, locationAtStart: true);
        Assert.Equal(1, RoadNetworkGenerator.CarriedToEdgeCount);
        Assert.Equal(endLandings, startLandings);
        AssertLevelLanding(atStart, "start");
        foreach (float d in OverTheLanding())
            Assert.True(Mathf.Abs(HeightAt(atStart, d) - HeightAt(atEnd, d)) <= 0.02f,
                $"at {d:F1} m out the road's start stands at {HeightAt(atStart, d):F2}, its end at {HeightAt(atEnd, d):F2}");
    }

    /// <summary>The landing is at the location's ground where the road arrives (its edge on the road's side, ArrivalHeight), the
    /// end held there as an end level with a location's flat is held (ApproachGround); not where the ground is further off it than
    /// the reached test allows (ReachRise).</summary>
    [Fact]
    public void ANoFlatLocationsEndIsHeldAtItsArrivalHeight()
    {
        Setup(CabinSmooth);
        float ring = RoadNetworkGenerator.KeepOutEdge(Origin, Radius, Width);
        Vector2 end = new(0f, ring);
        Assert.Null(LocationLevelling.ApproachHeight(50f, CabinSmooth));
        float arrival = RoadNetworkGenerator.ArrivalHeight(Origin, Radius, end)!.Value;
        float? held = RoadNetworkGenerator.ApproachGround(end, Origin, Radius);
        Assert.NotNull(held);
        Assert.Equal(arrival, held!.Value, 3);
        Vector2 off = new(0f, (arrival - 50f + RoadNetworkGenerator.ReachRise + 0.5f) / Slope);
        Assert.Null(RoadNetworkGenerator.ApproachGround(off, Origin, Radius));
    }

    /// <summary>A location with a flat is unchanged: its end held at its ground only within StairLayout.StairFitsFrom of its flat, as before.</summary>
    [Fact]
    public void ALocationWithAFlatIsUnchanged()
    {
        Setup(Flat);
        float? platform = LocationLevelling.ApproachHeight(50f, Flat);
        Assert.NotNull(platform);
        float ring = RoadNetworkGenerator.KeepOutEdge(Origin, Radius, Width);
        foreach (float y in new[] { ring, 2f / Slope, 0.4f / Slope })
        {
            Vector2 at = new(0f, y);
            float natural = _world.GetHeight(at.x, at.y);
            float? before = (at.magnitude <= Flat[0].Radius ? 50f + Flat[0].Height : (float?)null)
                ?? (Mathf.Abs(natural - platform!.Value) <= StairLayout.StairFitsFrom ? natural : null);
            Assert.Equal(before, RoadNetworkGenerator.ApproachGround(at, Origin, Radius));
        }
    }
}
