using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// One owner for the side a placed Location may be come to from (<see cref="PlacedLocation.OnEntranceSide"/>: all round but for a
/// blocked back) and one for the entrance preference (<see cref="PlacedLocation.EntranceArc"/>, a price): its door catalogue
/// (RoadNetworkGenerator.DoorsOf) and "reached" at its ring (RoadNetworkGenerator.Reached, as the arrival ladder's ring rungs and
/// the ring's own refusal ask) give the same verdict for the same point. A SunkenCrypt4 is offered doors on every side, the ones
/// nearer its entrance cheaper; a Dvergr town entrance, its back rock, only in front.
/// </summary>
public partial class LocationsFirstTests
{
    [Fact]
    public void ACryptsDoorCatalogueAndItsRingAgreeOnTheSideItIsComeToFromAndPriceTheEntranceSideCheaper()
    {
        const string crypt = "SunkenCrypt4";
        const float radius = 12f, level = 50f;
        var world = new MoundWorld { Biome = Heightmap.Biome.Swamp, Ground = (_, _) => level };
        _scope.WithWorld(world);
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        PlacedLocations.Reset();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), radius) });
        var flat = new List<LevelOp> { PlacedLocationShape.DoorFlat };
        var record = new PlacedLocationRecord
        {
            Prefab = crypt, Centre = new Vector2(0f, 0f), Yaw = 0f, Root = level, RockRadius = radius,
            Ops = flat, X0 = -60, Z0 = -60, W = 121, H = 121,
        };
        Func<float, float, float> ground = world.Ground;
        PlacedLocationShape fallback = PlacedLocationShape.Of(record.Centre, 0f, PlacedLocationShape.DoorFlat, radius, null, ground, false, level, crypt);
        PlacedLocationShape entrance = PlacedLocationShape.Of(record.Centre, 0f, PlacedLocationShape.DoorFlat, radius, null, ground, false, level, crypt, entrance: 0);
        var entrances = new[] { entrance };
        Assert.Null(PlacedLocations.FallbackOf(fallback, entrances));
        var location = new PlacedLocation { Record = record, Ground = ground, Shape = entrance, Entrances = entrances };
        PlacedLocations.Ready(location);
        LocationLevelling.Source = c => c.magnitude < 0.5f ? flat : null;
        try
        {
            IReadOnlyList<RoadDoor> doors = RoadNetworkGenerator.DoorsOf(record.Centre, radius, 4f);
            Assert.NotEmpty(doors);
            Assert.All(doors, d => Assert.True(location.OnEntranceSide(d.At), $"a door off its entrance side at {d.At}"));
            // Doors on both sides of it, the nearest its entrance the cheapest, each priced by its arc from the entrance's bearing.
            Assert.Contains(doors, d => entrance.InFront(d.At));
            Assert.Contains(doors, d => !entrance.InFront(d.At));
            RoadDoor cheapest = doors.OrderBy(d => d.Price).First();
            Assert.True(Vector2.Distance(cheapest.At, entrance.Landing) == doors.Min(d => Vector2.Distance(d.At, entrance.Landing)),
                $"the cheapest door {cheapest.At} is not the nearest the entrance");

            // A level road across the ring at each bearing: reached exactly where the catalogue's owner says it is entered from.
            int front = 0, back = 0;
            for (int b = 0; b < 360; b += 45)
            {
                RoadSpatialGrid.Clear();
                RoadNetworkGenerator.Reset();
                var at = new Vector2(Mathf.Sin(b * Mathf.Deg2Rad), Mathf.Cos(b * Mathf.Deg2Rad)) * (radius + 6f);
                Assert.True(Vector2.Distance(at, entrance.Landing) > RoadNetworkGenerator.LandingReach, $"bearing {b} is on the landing");
                Vector2 across = new Vector2(-at.y, at.x).normalized * 3f;
                var points = new List<Vector2> { at - across, at, at + across };
                RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(_ => level), points.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 12f, false));
                bool side = location.OnEntranceSide(at);
                Assert.Equal(side, RoadNetworkGenerator.Reached(Vector3.zero, radius));
                if (side) front++; else back++;
            }
            Assert.True(front == 8 && back == 0, $"{front} bearings it may be come to from, {back} not");
        }
        finally { RoadSpatialGrid.Clear(); }
    }

    /// <summary>A Dvergr town entrance's back is rock (LocationRules.FrontOnly): its doors are offered in front only, and network at
    /// its ring reaches it there only: the soft entrance preference never brings a road round to its back.</summary>
    [Fact]
    public void ADvergrTownEntrancesDoorsAndRingAreInFrontOnly()
    {
        const string town = "Mistlands_DvergrTownEntrance1";
        const float radius = 12f, level = 50f;
        var world = new MoundWorld { Biome = Heightmap.Biome.Mistlands, Ground = (_, _) => level };
        _scope.WithWorld(world);
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        PlacedLocations.Reset();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), radius) });
        var flatOp = new LevelOp(0f, 0f, 0f, 8f, false, 12f, 3f);
        var ops = new List<LevelOp> { flatOp };
        var record = new PlacedLocationRecord { Prefab = town, Centre = new Vector2(0f, 0f), Yaw = 0f, Root = level, RockRadius = radius, Ops = ops, X0 = -60, Z0 = -60, W = 121, H = 121 };
        Func<float, float, float> ground = world.Ground;
        PlacedLocationShape own = PlacedLocationShape.Of(record.Centre, 0f, flatOp, radius, null, ground, false, level, town, bodyKnown: true);
        PlacedLocationShape area = PlacedLocationShape.Of(record.Centre, 0f, flatOp, radius, null, ground, false, level, town, bodyKnown: true, levelledArea: true);
        Assert.True(area.LevelledArea && own.EnteredFromFront);
        var location = new PlacedLocation { Record = record, Ground = ground, Shape = own, Fallback = area };
        PlacedLocations.Ready(location);
        LocationLevelling.Source = c => c.magnitude < 0.5f ? ops : null;
        IReadOnlyList<RoadDoor> doors = RoadNetworkGenerator.DoorsOf(record.Centre, radius, 4f);
        Assert.NotEmpty(doors);
        Assert.All(doors, d => Assert.True(own.InFront(d.At), $"a door at its back at {d.At}"));
        // Its levelled ground as a whole is ended on in front only, whichever side the road comes from.
        Assert.All(area.Ends(ground, new Vector2(0f, -30f), 2.5f, 8), e => Assert.True(own.InFront(e.at), $"an end at its back at {e.at}"));
    }
}
