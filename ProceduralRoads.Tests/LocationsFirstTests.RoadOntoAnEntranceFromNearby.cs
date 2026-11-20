using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A placed Location's join tries a road onto an entrance from a road already built near it before a staircase
/// (RoadNetworkGenerator.JoinTo's first pass, RoadNetworkGenerator.NearAnEntrance), in the arrival ladder's order: a road onto an
/// entrance, a staircase onto it, then the fallback landing (RoadNetworkGenerator.ArrivalOrder). The Dvergr guard tower of
/// <see cref="TowerOnAPlateau"/>, a causeway at its entrance landing's height running out to the west.
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>Level ground at 50 m with a causeway 12 m over it and 12 m wide (z 7.5 to 19.5, its sides falling 2.4 in 1), running from
    /// 6 m west of the tower's entrance out to x -80: gentle ground at the entrance landing's height.</summary>
    private sealed class CausewayWorld : WorldGenerator
    {
        /// <summary>The causeway's and the tower's plateau's height over the level ground.</summary>
        public const float Rise = 12f;
        public static float Causeway(float x, float z)
        {
            float off = Mathf.Max(Mathf.Max(0f, Mathf.Max(7.5f - z, z - 19.5f)), Mathf.Max(0f, Mathf.Max(-80f - x, x + 6f)));
            return Mathf.Clamp(Rise - 2.4f * off, 0f, Rise);
        }
        public override float GetHeight(float x, float z) => Mathf.Abs(x) > 200f || Mathf.Abs(z) > 200f ? 0f : 50f + Causeway(x, z);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mistlands;
        public override int GetSeed() => 77;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    private static void CommitRoad(Vector2 a, Vector2 b, float height)
    {
        var points = new List<Vector2>();
        int n = Mathf.CeilToInt(Vector2.Distance(a, b) / 2f);
        for (int i = 0; i <= n; i++) points.Add(Vector2.Lerp(a, b, i / (float)n));
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(_ => height),
            points.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 4f, false));
    }

    /// <summary>The guard tower of <see cref="TowerOnAPlateau"/> (its entrance 13.5 m out of its front, north, on the Location's
    /// plateau, here 12 m over the ground out to 16 m and falling 2.4 in 1, its ring at the plateau's foot) on <see cref="CausewayWorld"/>, the
    /// causeway meeting the plateau's top west of the entrance, with the network's roads built: in the lowland 44 m north at 50 m
    /// (the junction the join's search finds first) and, with <paramref name="causewayRoad"/>, across the causeway 58 m west at
    /// its height.</summary>
    private PlacedLocation TowerByACauseway(CausewayWorld world, bool causewayRoad = true)
    {
        const string tower = "Mistlands_GuardTower1_ruined_new";
        _scope.WithWorld(world).WithZdos().WithZoneSystem().WithScene();
        RoadNetworkGenerator.Reset(); RoadTerrainModifier.ResetForWorld();
        RoadNetworkGenerator.MarkLocationsReady();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), Footprint) });
        PlacedLocations.Reset();
        RoadSpatialGrid.Clear();
        var flat = new List<LevelOp> { PlacedLocationShape.DoorFlat };
        var record = new PlacedLocationRecord
        {
            Prefab = tower, Centre = new Vector2(0f, 0f), Yaw = 0f, Root = 50f + CausewayWorld.Rise, RockRadius = 0f,
            Ops = flat, X0 = -60, Z0 = -60, W = 121, H = 121,
        };
        Func<float, float, float> ground = (x, z) =>
            world.GetHeight(x, z) + Mathf.Clamp(CausewayWorld.Rise - 2.4f * (new Vector2(x, z).magnitude - 16f), 0f, CausewayWorld.Rise - CausewayWorld.Causeway(x, z));
        PlacedLocationShape fallback = PlacedLocationShape.Of(record.Centre, record.Yaw, PlacedLocationShape.DoorFlat, 0f, null, ground, false, record.Root, tower);
        PlacedLocationShape entrance = PlacedLocationShape.Of(record.Centre, record.Yaw, PlacedLocationShape.DoorFlat, 0f, null, ground, false, record.Root,
            tower, entrance: 0);
        var location = new PlacedLocation
        {
            Record = record, Ground = ground, Shape = entrance, Fallback = fallback, Entrances = new[] { entrance },
        };
        PlacedLocations.Ready(location);
        LocationLevelling.Source = c => c.magnitude < 0.5f ? flat : null;
        CommitRoad(new Vector2(-6f, 44f), new Vector2(6f, 44f), 50f);
        if (causewayRoad) CommitRoad(new Vector2(-58f, 9.5f), new Vector2(-58f, 17.5f), 50f + CausewayWorld.Rise);
        RoadSpatialGrid.FinalizeRoadNetwork();
        return location;
    }

    private bool JoinTheTower(WorldGenerator world, PlacedLocation tower, Vector2? from = null)
    {
        var finder = RoadNetworkGenerator.NewFinder(world, RoadLocationProtection.Clearance(RoadNetworkGenerator.RoadWidth));
        finder.Stairs = true; finder.TerrainVarianceThreshold = 1000f; finder.Meander = 0f;
        SetPathfinder(finder);
        Vector3 position = new(tower.Centre.x, 0f, tower.Centre.y);
        return RoadNetworkGenerator.JoinTo(finder, from ?? tower.Centre, RoadNetworkGenerator.JoinTargets(_ => true), tower.Record.Prefab, position, 10f, null,
            () => RoadNetworkGenerator.Reached(tower.Centre, 10f, tower.Record.Prefab), RoadNetworkGenerator.JoinTargets(_ => true));
    }

    /// <summary>A road along the causeway's top, at its height, toward the entrance's landing.</summary>
    private static bool RoadAlongTheCauseway() =>
        RoadSpatialGrid.TryGetRoadHeightWithin(new Vector2(-30f, 13.5f), 6f, out float h) && Mathf.Abs(h - 50f - CausewayWorld.Rise) <= 1f;

    /// <summary>The guard tower's case (Mistlands, a staircase allowed onto its entrance): the road the join's search finds first,
    /// to the lowland road north, gets no road onto the entrance (12 m under it at the plateau's foot, past the grade cap); a
    /// staircase onto the entrance from there is the join's arrival without the causeway's road, and, before the first pass, with it
    /// too. The road already built 58 m west across the gentle causeway, at the landing's height, gives a road onto the entrance:
    /// the join's first pass takes it, and no staircase is laid.</summary>
    [Fact]
    public void ARoadFromANearbyRoadOntoTheEntranceIsTakenWhereTheFirstJunctionGivesNoRoadOntoIt()
    {
        var world = new CausewayWorld();
        PlacedLocation tower = TowerByACauseway(world, causewayRoad: false);
        Assert.True(JoinTheTower(world, tower), RoadSpatialGrid.LastRefusal ?? "refused");
        Assert.Single(StairStructures.Runs);
        Assert.Equal(0, RoadNetworkGenerator.ReachedTarget(tower)?.Entrance);

        tower = TowerByACauseway(world);
        Assert.True(JoinTheTower(world, tower), RoadSpatialGrid.LastRefusal ?? "refused");
        Assert.True(PlacedLocations.Landed(tower.Centre), "the landing is not reached by road");
        Assert.Empty(StairStructures.Runs);
        Assert.Equal(0, RoadNetworkGenerator.ReachedTarget(tower)?.Entrance);
        Assert.True(RoadAlongTheCauseway(), "no road along the causeway");
        Assert.False(RoadSpatialGrid.TryGetRoadHeightWithin(new Vector2(0f, 32f), 6f, out _), "a road toward the lowland road north");
    }

    /// <summary>One way in (the rule on a second way into a Location already reached, LocationWaysIn): once the tower is joined by
    /// the road from the causeway onto its entrance, a second join (another road's, from the lowland north-east) finds it served and
    /// builds nothing: its one way in is that road, no staircase beside it, and no twin ways.</summary>
    [Fact]
    public void ATowerJoinedByARoadFromANearbyRoadGetsNoSecondWayIn()
    {
        // The ways in are recorded with the mod's corridor (LocationWaysIn.On; the suite runs on plain roads).
        using var corridor = StaticOverride.Keep(() => RoadNetworkGenerator.RoadSnap).AndKeep(() => RoadNetworkGenerator.CorridorSnap);
        RoadNetworkGenerator.RoadSnap = (float)PlainRoads.ModDefaults["road snap"];
        RoadNetworkGenerator.CorridorSnap = (float)PlainRoads.ModDefaults["corridor snap"];
        LocationWaysIn.Reset();
        var world = new CausewayWorld();
        PlacedLocation tower = TowerByACauseway(world);
        Assert.True(JoinTheTower(world, tower), RoadSpatialGrid.LastRefusal ?? "refused");
        Assert.Empty(StairStructures.Runs);
        Assert.True(JoinTheTower(world, tower, from: new Vector2(16f, 36f)));
        Assert.Empty(StairStructures.Runs);
        LocationWaysIn.Way way = Assert.Single(LocationWaysIn.There(tower.Centre));
        Assert.Null(way.Approach);
        Assert.Empty(LocationWaysIn.Twins());
        Assert.True(RoadAlongTheCauseway(), "no road along the causeway");
    }

    /// <summary>The roads a nearby road may join the tower from are near an entrance (RoadNetworkGenerator.NearAnEntrance): within
    /// the reach a way onto a landing starts from (RoadNetworkGenerator.PlacedLocationReach) of the entrance's landing, and within the
    /// grade cap of it (RoadGrade.EaseLength): a lowland road 12 m under the landing 16.5 m off (73 %) is not near it; the causeway's
    /// road, at the landing's height 58 m off, is; a road past the reach is not; nor is a road within the reach of only the fallback
    /// landing (the tower's centre), never an entrance.</summary>
    [Fact]
    public void ANearbyRoadIsOneWithinReachOfAnEntranceThatCouldClimbToIt()
    {
        var world = new CausewayWorld();
        PlacedLocation tower = TowerByACauseway(world);
        CommitRoad(new Vector2(14f, 16f), new Vector2(14f, 28f), 50f);
        CommitRoad(new Vector2(-4f, -60f), new Vector2(4f, -60f), 50f + CausewayWorld.Rise);
        Assert.False(RoadNetworkGenerator.NearAnEntrance(tower, new Vector2(14f, 22f)));
        Assert.True(RoadNetworkGenerator.NearAnEntrance(tower, new Vector2(-58f, 13.5f)));
        Assert.False(RoadNetworkGenerator.NearAnEntrance(tower, new Vector2(0f, 13.5f + RoadNetworkGenerator.PlacedLocationReach + 2f)));
        Assert.True(Vector2.Distance(new Vector2(0f, -60f), tower.Fallback!.Landing) <= RoadNetworkGenerator.PlacedLocationReach);
        Assert.False(RoadNetworkGenerator.NearAnEntrance(tower, new Vector2(0f, -60f)));
    }
}
