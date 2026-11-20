using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// Where a road or staircase ends at a placed Location's entrance (PlacedLocationShape.Ends, the one owner): aimed toward the entrance,
/// but ended as soon as it is on the levelled landing, on its lip, level with it, never run on up to the door; a staircase's top the
/// same as a road's end (PlacedLocationShape.Aims for an entrance is Ends); never under the Location's own rock (inside a cave, past its
/// mouth: PlacedLocationShape.UnderItsRock), refused where it can end only there; and a Location's stakes no obstacle, taken whole by
/// the rock pass where a way goes through them (LocationRock.ClearedForAWay, RoadRockHit.Judged, RoadRockPolicy.HowTaken).
/// From a Mistlands guard tower on a levelled pad: its staircase ran up to its door, stake spikes stood inside it.
/// </summary>
public class StairLipTests
{
    private const string Tower = "Mistlands_GuardTower1_new";
    private const float Level = 50f, Pad = 18f;
    private static readonly Vector2 Centre = new(0f, 0f);
    /// <summary>Its main flat as a Mistlands guard tower's prefab levels it: 7 m round, smoothed out to 20 m.</summary>
    private static readonly LevelOp Flat = new(0f, 0f, 0f, 7f, false, 20f, 3f);

    /// <summary>The tower's pad: level out to <see cref="Pad"/> round its centre, a metre a metre down outside it.</summary>
    private static readonly Func<float, float, float> Ground = (x, z) => Level - Mathf.Max(0f, new Vector2(x, z).magnitude - Pad);

    private static PlacedLocationShape Shape(List<LocationRock>? rocks = null) =>
        PlacedLocationShape.Of(Centre, 0f, Flat, 0f, rocks, Ground, false, Level, Tower, entrance: 0, bodyKnown: true);

    private static float RoadClearance => RoadLocationEntry.InsideReach(RoadNetworkGenerator.RoadWidth) + RoadLocationEntry.EndSlack;

    /// <summary>A road's end out in front of its door, 30 m off.</summary>
    private static Vector2 From(PlacedLocationShape shape) => shape.Door!.Value + shape.Front * 30f;

    /// <summary>Rule (a): aimed toward the entrance, a road ends as soon as it is on the landing: on its lip nearest the road, level
    /// with the landing within a step, not at the door the landing's level ground runs on to.</summary>
    [Fact]
    public void ARoadToAnEntranceEndsOnTheLandingsLipNotAtItsDoor()
    {
        PlacedLocationShape shape = Shape();
        Vector2 door = shape.Door!.Value, from = From(shape);
        PlacedLocationShape.LandingRegion region = shape.RegionFor(Ground, RoadClearance);
        Assert.True(region.Depth(door) > RoadStairRuns.LipReach, FormattableString.Invariant($"the door ({door.x:F1}, {door.y:F1}) is on the lip"));

        var ends = shape.Ends(Ground, from, RoadClearance, 4);

        Assert.NotEmpty(ends);
        Vector2 end = ends[0].at;
        Assert.True(PlacedLocationShape.OnItsLip(region, end), FormattableString.Invariant($"the end ({end.x:F1}, {end.y:F1}) is off the lip"));
        Assert.True(Vector2.Distance(end, from) < Vector2.Distance(door, from) - RoadStairRuns.LipReach, "the end runs on toward the door");
        Assert.True(Vector2.Distance(end, door) <= RoadNetworkGenerator.LandingReach);
        Assert.True(StairLayout.Walkable(ends[0].height, Ground(door.x, door.y)), "the end is not level with the landing");
    }

    /// <summary>Rule (b), the parity: a staircase's top onto the same entrance aims where a road's end stops (Aims is Ends), on the one
    /// lip, for the clearance asked; none aims at the door past the lip.</summary>
    [Fact]
    public void ARoadsEndAndAStaircasesTopOnOneEntranceStopAtTheSameLip()
    {
        PlacedLocationShape shape = Shape();
        Vector2 door = shape.Door!.Value;
        Vector2 across = new(shape.Front.y, -shape.Front.x);
        foreach (Vector2 from in new[] { From(shape), door + shape.Front * 20f + across * 15f })
            foreach (float clearance in new[] { PlacedLocationShape.LevelGroundClearance, RoadClearance })
            {
                PlacedLocationShape.LandingRegion region = shape.RegionFor(Ground, clearance);
                var ends = shape.Ends(Ground, from, clearance, RoadStairRuns.AimTries);
                var aims = shape.Aims(Ground, from, clearance, RoadStairRuns.AimTries, region);
                Assert.NotEmpty(aims);
                Assert.Equal(ends.Select(e => e.at), aims.Select(a => a.at));
                foreach (var (name, at) in aims)
                {
                    Assert.True(PlacedLocationShape.OnItsLip(region, at), FormattableString.Invariant($"a staircase's {name} ({at.x:F1}, {at.y:F1}) past the lip"));
                    Assert.True((at - door).sqrMagnitude > 1e-4f, "a staircase aimed at the door");
                }
            }
    }

    /// <summary>Rule (d): the Location's own rock over the landing (a cave's arch over its mouth, over a walker's head, so no rock in its
    /// way) keeps every end and staircase top out from under it; where the whole landing is under it, nothing ends there at all and the
    /// way in is refused, never aimed at the door.</summary>
    [Fact]
    public void NoEndOrStaircaseTopStandsUnderTheLocationsRock()
    {
        PlacedLocationShape open = Shape();
        float clearance = PlacedLocationShape.LevelGroundClearance;
        Vector2 from = From(open), first = open.Ends(Ground, from, clearance, 1)[0].at;
        float over = RockClearances.Top(Level) + 1f;
        // An arch over the lip the road comes to first, a metre over the walker's head.
        var arch = new LocationRock(first, 2f, 2f, 0f, over, over + 4f, "rock_arch");
        PlacedLocationShape mouth = Shape(new List<LocationRock> { arch });
        Assert.Equal("", mouth.Refusal(first, RoadClearance, RoadClearance));
        Assert.True(mouth.UnderItsRock(first, RoadStairRuns.Width * 0.5f));

        var ends = mouth.Ends(Ground, from, clearance, 4);
        Assert.NotEmpty(ends);
        foreach (var (at, _) in ends)
            Assert.False(mouth.UnderItsRock(at, clearance), FormattableString.Invariant($"an end ({at.x:F1}, {at.y:F1}) under the arch"));
        foreach (var (_, at) in mouth.Aims(Ground, from, PlacedLocationShape.LevelGroundClearance, RoadStairRuns.AimTries))
            Assert.False(mouth.UnderItsRock(at, PlacedLocationShape.LevelGroundClearance));

        // The whole landing under it: no end, no aim; refused rather than carried on onto it.
        var roof = new LocationRock(Centre, 30f, 30f, 0f, over, over + 4f, "rock_roof");
        PlacedLocationShape cave = Shape(new List<LocationRock> { roof });
        Assert.Empty(cave.Ends(Ground, from, RoadClearance, 4));
        Assert.Empty(cave.Aims(Ground, from, PlacedLocationShape.LevelGroundClearance, RoadStairRuns.AimTries));
    }

    /// <summary>Rule (c): a Location's stakes in a road's or staircase's clearance are taken whole by the rock pass (bake and client
    /// alike run it), a Location's part or not, on its approach or not, whatever their size; never where a player placed them or
    /// another peer owns them. Any other Location piece is kept.</summary>
    [Fact]
    public void TheRockPassTakesALocationsStakesInAWayWhole()
    {
        const string stakes = "dvergrprops_wood_stakewall";
        var natural = new HashSet<string>();
        RoadRockHit Judge(string name, bool piece = false, bool owned = true) => RoadRockHit.Judged(name, 20f, new[] { name }, natural,
            mist: true, cliffs: false, locationChild: true, inLocationArea: true, keptOnApproach: "one of its parts", piece: piece,
            hasView: true, validView: true, owned: owned);

        RoadRockHit hit = Judge(stakes);
        Assert.True(hit.Clears);
        Assert.True(hit.LocationStakes);
        Assert.Equal(RoadRockPolicy.Taking.RemoveWhole, RoadRockPolicy.HowTaken(hit.Clears, hit.DeckTakes, false, hit.Size, true, hit.LocationStakes));
        Assert.True(hit.Revisited(true, 0, _ => "one of its parts").Clears, "an approach kept the stakes as the Location's own");

        Assert.False(Judge(stakes, piece: true).Clears);
        Assert.Equal(RoadRockHitKind.KeepNotOwner, Judge(stakes, owned: false).Kind);
        Assert.False(Judge("dvergrprops_wood_wall").Clears);
        Assert.False(Judge("dverger_light_post").Clears);
    }
}
