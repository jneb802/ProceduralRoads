using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A road to a place is carried on over the ground the place only paints, up to the
/// ground it shapes, instead of stopping at the edge of everything its terrain modifiers touch.
/// </summary>
public class SiteDoorTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    /// <summary>Level meadow at 40 m.</summary>
    private sealed class Level : WorldGenerator
    {
        public override float GetHeight(float x, float z) => 40f;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
        public override int GetSeed() => 24;
    }

    /// <summary>Crypt4 as the game's prefab has it: levels a disc 5 m across 3 m up on its centre,
    /// exterior radius 18 m, and paints cleared ground out to 30 m.</summary>
    private static readonly IReadOnlyList<LevelOp> Crypt4 = new[] { new LevelOp(0f, 0f, 3f, 5f, false, 0f, 20f) };
    private const float Crypt4Exterior = 18f, Crypt4Paint = 30f;
    /// <summary>A cave's floor off its centre (MountainCave02): where it lies turns with the cave.</summary>
    private static readonly IReadOnlyList<LevelOp> CaveFloor = new[] { new LevelOp(-0.06f, -4.75f, -4.09f, 4f, false, 7f, 3f) };

    public SiteDoorTests()
    {
        _scope.WithWorld(new Level());
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        PlacedSites.Reset();
    }

    public void Dispose()
    {
        SetPathfinder(null);
        LocationLevelling.Source = null;
        RoadSiteProtection.Reset();
        RoadSpatialGrid.Clear();
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    private static void SetPathfinder(RoadPathfinder? pathfinder) =>
        typeof(RoadNetworkGenerator).GetField("m_pathfinder", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, pathfinder);

    private static void Place(Vector2 centre, IReadOnlyList<LevelOp> ops)
    {
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(centre, Crypt4Paint,
            LocationLevelling.ArrivalReach(ops, Crypt4Exterior, Crypt4Paint)) });
        LocationLevelling.Source = c => (c - centre).magnitude < 0.5f ? ops : null;
        SetPathfinder(new RoadPathfinder(WorldGenerator.instance) { Stairs = false, TerrainVarianceThreshold = 1000f, Meander = 0f });
    }

    private static float NearestRoad(Vector2 centre, out RoadSpatialGrid.RoadPoint nearest)
    {
        nearest = default;
        float best = float.PositiveInfinity;
        foreach (var rp in RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(centre.x, 0f, centre.y), 80f))
        {
            if (!rp.IsRoad) continue;
            float d = Vector2.Distance(rp.p, centre);
            if (d < best) { best = d; nearest = rp; }
        }
        return best;
    }

    [Fact]
    public void ACrypt4sOwnRoadStopsAtItsStructureNotAtItsPaint()
    {
        Assert.Equal(Crypt4Exterior, LocationLevelling.ArrivalReach(Crypt4, Crypt4Exterior, Crypt4Paint), 3);
        Assert.Equal(Crypt4Paint, LocationLevelling.ArrivalReach(CaveFloor, Crypt4Exterior, Crypt4Paint), 3);
        Assert.Equal(Crypt4Paint, LocationLevelling.ArrivalReach(null, Crypt4Exterior, Crypt4Paint), 3);
        // A smoothing reach past the exterior is shaped ground too.
        Assert.Equal(25f, LocationLevelling.ArrivalReach(new[] { new LevelOp(0f, 0f, 0f, 10f, false, 25f, 3f) }, Crypt4Exterior, Crypt4Paint), 3);

        var crypt = new Vector2(0f, 0f);
        Place(crypt, Crypt4);
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(200f, 0f), 0f, crypt, Crypt4Exterior, 4f, "to the crypt"));
        // Was 34 m out: the paint ring (30) + half the width + 2 m. Now the structure's edge (18) + 4.
        float end = NearestRoad(crypt, out _);
        Assert.InRange(end, 21f, 23f);
        // Its stretch over the crypt's paint is stored into the site: the terrain writer levels and paints it
        // there along itself only, and every other point inside a footprint is left alone.
        var inside = RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(0f, 0f, 0f), Crypt4Paint)
            .Where(rp => rp.IsRoad && Vector2.Distance(rp.p, crypt) < Crypt4Paint - 0.5f).ToList();
        Assert.NotEmpty(inside);
        Assert.All(inside, rp => Assert.True(rp.intoSite));
        Assert.All(RoadSpatialGrid.GetRoadPointsNearPosition(new Vector3(150f, 0f, 0f), 20f).Where(rp => rp.IsRoad), rp => Assert.False(rp.intoSite));
        Assert.True(RoadNetworkGenerator.Reached(new Vector3(0f, 40f, 0f), Crypt4Exterior));
    }

    [Fact]
    public void APlaceWhoseGroundTurnsWithItKeepsItsWholeFootprint()
    {
        var cave = new Vector2(0f, 0f);
        Place(cave, CaveFloor);
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(200f, 0f), 0f, cave, Crypt4Exterior, 4f, "to the cave"));
        Assert.True(NearestRoad(cave, out _) >= Crypt4Paint + 4f - 0.5f);
    }

    [Fact]
    public void AnotherRoadStillKeepsOutOfThePlacesPaint()
    {
        var crypt = new Vector2(0f, 0f);
        Place(crypt, Crypt4);
        Assert.True(RoadNetworkGenerator.GenerateRoad(new Vector2(-200f, 10f), 0f, new Vector2(200f, 10f), 0f, 4f, "past the crypt"));
        Assert.True(NearestRoad(crypt, out _) >= Crypt4Paint, "a road past the crypt ran over its paint");
        // And it does not count as reaching it: only a road to the crypt's own edge does.
        Assert.False(RoadNetworkGenerator.Reached(new Vector3(0f, 40f, 0f), Crypt4Exterior));
    }

    [Fact]
    public void AnArrivalCarriedInStillCannotCrossTheStructure()
    {
        var crypt = new Vector2(0f, 0f);
        Place(crypt, Crypt4);
        // Ends at the carried-in edge (18 + 4) on the far side, having crossed the structure: refused.
        var across = new List<Vector2> { new(60f, 0f), new(-22f, 0f) };
        Assert.Null(RoadSpatialGrid.PlanRoadPath(across, 4f, WorldGenerator.instance));
        // Straight in to that edge: built.
        var straight = new List<Vector2> { new(60f, 0f), new(22f, 0f) };
        Assert.NotNull(RoadSpatialGrid.PlanRoadPath(straight, 4f, WorldGenerator.instance));
    }
}
