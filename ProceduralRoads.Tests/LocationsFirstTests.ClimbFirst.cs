using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// The climb rule onto a placed Location (RoadStairRuns.ClimbsUnderAPlayer) gives its verdict whether or not a staircase was laid
/// out: where the layout search lays none, the heights alone judge it, and a road's end the target says arrives on it
/// (PlacedLocationShape.Arrives, within ReachRise) ends the road there. Only where every way that arrives level was refused (every
/// target, start and rung of the arrival ladder) does the road end nearby up to a player's height off the landing
/// (PlacedLocationShape.ArrivesNearby, RoadRockPolicy.PlayerHeight): the ladder's last resort. Every other reached test keeps
/// RoadNetworkGenerator.ReachRise.
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>The crypt's mound level on top out to 12 m and falling a stair to level ground 14 m out: from the road's end 18 m out,
    /// a stair under the landing and within its reach, the staircase onto the landing walks level rows from the road's end to its
    /// stair. With a rest three rows long (RoadPitches.EaseMax 6 m, as StructureEndLastResortTests sets it), every layout of it
    /// walks past a rest (StairLayout.PastARest) and the search lays none ("not reached"; at the 8 m rest it is laid). Asked only
    /// of a laid staircase, the climb rule was never asked and the start was refused; asked of the heights where none is laid,
    /// the staircase climbs less than a player's height, and the road ends there, the crypt reached at its entrance.</summary>
    [Fact]
    public void AClimbUnderAPlayerWhoseLayoutWouldWalkPastARestStillEndsTheRoadNearby()
    {
        MoundWorld world = Mound(Heightmap.Biome.BlackForest, 0.5f, 3f);
        world.Ground = (x, z) => 50f + Mathf.Clamp(3f - 0.5f * (new Vector2(x, z).magnitude - 12f), 2f, 3f);
        PlacedLocation crypt = CryptOnAMound(world, 3f);
        float easeMax = RoadPitches.EaseMax;
        RoadPitches.EaseMax = 6f;
        try
        {
            Assert.Equal(3, StairLayout.RestFloors);
            var (entered, road, span, why, arrival, end, height) = StairsOntoTheCrypt(world, crypt);

            PlacedLocationShape entrance = crypt.Entrances[0];
            float climb = entrance.Ground!(entrance.Landing.x, entrance.Landing.y) - height;
            Assert.InRange(climb, StairLayout.StairFitsFrom, RoadNetworkGenerator.ReachRise);
            Assert.True(entered, why);
            Assert.Null(span);
            Assert.Null(road);
            Assert.True(arrival != null && arrival.Target.Entrance == 0, $"arrived at {(arrival == null ? "none" : EntranceArrivals.Name(arrival.Target))}");
            Assert.Equal(new Vector2(0f, CryptRoadEnd), end);
        }
        finally { RoadPitches.EaseMax = easeMax; }
    }

    /// <summary>The Dvergr tower on a mound falling 0.85: the road's end 18 m out, within its entrance's landing's reach, is 1.7 m
    /// under the landing, over ReachRise (1.5 m) and under a player's height. Its staircase rung the only one, and no staircase
    /// laid from any start (a rest two rows long, RoadPitches.EaseMax 4 m), every way onto the landing is refused: the way in
    /// alone does not take it (PlanEntry). Last, the ladder ends the road there, nearby up to a player's height
    /// (PlacedLocationShape.ArrivesNearby), where Arrives keeps ReachRise.</summary>
    [Fact]
    public void AClimbOfOnePointSevenMetresWithinTheLandingsReachEndsTheRoadNearby()
    {
        PlacedLocation tower = TowerOnAMound(0.85f, out MoundWorld world);
        float easeMax = RoadPitches.EaseMax;
        RoadNetworkGenerator.ArrivalRung[] order = RoadNetworkGenerator.ArrivalOrder;
        RoadPitches.EaseMax = 4f;
        RoadNetworkGenerator.ArrivalOrder = new[] { RoadNetworkGenerator.ArrivalRung.WayInStairs };
        try
        {
            var (path, plans) = DownTheMound(world, CryptRoadEnd);
            Vector2 end = path[path.Count - 1];
            float height = plans[0].Heights[plans[0].Heights.Count - 1];
            PlacedLocationShape entrance = tower.Entrances[0];
            float climb = entrance.Ground!(entrance.Landing.x, entrance.Landing.y) - height;
            Assert.InRange(climb, 1.69f, 1.71f);
            Assert.False(entrance.Arrives(end, height));
            Assert.True(entrance.ArrivesNearby(end, height));

            // Every way onto the landing, as a rung asks it: none.
            var (wayPath, wayPlans) = DownTheMound(world, CryptRoadEnd);
            Assert.False(RoadNetworkGenerator.PlanEntry(tower, false, wayPath, wayPlans, new List<RoadStairSpan>(), new List<RoadCrossing>(), RoadNetworkGenerator.RoadWidth,
                out _, out _, out _, out _, only: RoadNetworkGenerator.EntryWays.Stairs));

            var (entered, road, stairs, why, rung) = Arrive(tower, path, plans);
            Assert.True(entered, why);
            Assert.Null(road);
            Assert.Null(stairs);
            Assert.Equal("nearby", rung);
            Assert.Equal(new Vector2(0f, CryptRoadEnd), path[path.Count - 1]);
        }
        finally
        {
            RoadPitches.EaseMax = easeMax;
            RoadNetworkGenerator.ArrivalOrder = order;
        }
    }

    /// <summary>The crypt on the mound falling 0.85, the road's end 18 m out 1.7 m under its entrance's landing: the road's own way
    /// onto the landing brings it level, and it is built, never the road ended there 1.7 m off. Nearby up to a player's height is
    /// the arrival ladder's last resort.</summary>
    [Fact]
    public void ALevelRoadOntoTheLandingIsBuiltRatherThanEndingNearby()
    {
        MoundWorld world = Mound(Heightmap.Biome.BlackForest, 0.85f, 3f);
        PlacedLocation crypt = CryptOnAMound(world, 3f);
        var (path, plans) = DownTheMound(world, CryptRoadEnd);
        float height = plans[0].Heights[plans[0].Heights.Count - 1];
        PlacedLocationShape entrance = crypt.Entrances[0];
        Assert.True(entrance.ArrivesNearby(path[path.Count - 1], height));
        Assert.False(entrance.Arrives(path[path.Count - 1], height));

        var (entered, road, stairs, why, rung) = Arrive(crypt, path, plans);
        Assert.True(entered, why);
        Assert.NotNull(road);
        Assert.Null(stairs);
        Assert.Equal("level", rung);
    }

    /// <summary>The same 1.7 m where no staircase is refused: a road built down the mound to 18 m out, 1.7 m under the crypt's
    /// entrance's landing and within its reach, does not reach it. The reached test keeps ReachRise (Arrives, ReachedTarget,
    /// LandingReached); only the ladder's last resort is nearby up to a player's height.</summary>
    [Fact]
    public void AOnePointSevenMetreDifferenceWhereNoStaircaseIsRefusedIsNotReached()
    {
        MoundWorld world = Mound(Heightmap.Biome.BlackForest, 0.85f, 3f);
        PlacedLocation crypt = CryptOnAMound(world, 3f);
        var (_, plans) = DownTheMound(world, CryptRoadEnd);
        RoadSpatialGrid.PlannedPath built = plans[plans.Count - 1];
        RoadSpatialGrid.CommitRoad(new List<RoadSpatialGrid.PlannedPath> { built });
        RoadSpatialGrid.FinalizeRoadNetwork();
        Vector2 end = built.Points[built.Points.Count - 1];
        float height = built.Heights[built.Heights.Count - 1];

        PlacedLocationShape entrance = crypt.Entrances[0];
        float off = entrance.Ground!(entrance.Landing.x, entrance.Landing.y) - height;
        Assert.InRange(off, RoadNetworkGenerator.ReachRise, RoadRockPolicy.PlayerHeight);
        Assert.True(entrance.ArrivesNearby(end, height), "nearby on the ladder's last resort");
        Assert.False(entrance.Arrives(end, height));
        Assert.Null(RoadNetworkGenerator.ReachedTarget(crypt));
        Assert.False(RoadNetworkGenerator.LandingReached(crypt));
    }

    /// <summary>The arrival ladder (RoadNetworkGenerator.PlanArrival, by reflection) at <paramref name="location"/> from a road
    /// along <paramref name="path"/> ending there: entered, its way's road and staircase, why not, and its rung.</summary>
    private static (bool entered, RoadSpatialGrid.PlannedPath? road, RoadStairSpan? stairs, string why, string rung) Arrive(PlacedLocation location,
        List<Vector2> path, List<RoadSpatialGrid.PlannedPath> plans)
    {
        var road = new RoadNetworkGenerator.PlannedRoad
        {
            Path = path, Plans = plans, EndCenter = location.Centre,
            Ends = new RoadNetworkGenerator.LocationEndContext(0f, 10f, false, true, null, location, RoadNetworkGenerator.RoadWidth, 0f, 0f),
        };
        MethodInfo planArrival = typeof(RoadNetworkGenerator).GetMethod("PlanArrival", BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = new object?[] { road, location, false, null, null, null, null, null, null, null };
        bool entered = (bool)planArrival.Invoke(null, args)!;
        return (entered, (RoadSpatialGrid.PlannedPath?)args[4], (RoadStairSpan?)args[5], (string)args[6]!, (string)args[8]!);
    }
}
