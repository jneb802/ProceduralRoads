using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A pool beside a placed Location's levelled landing (in game a SunkenCrypt4: its natural ground under the landing 28.58, in the
/// pool, its levelled ground there 30.25). The crossing detector reads the ground before roads, the one ground the road's planner
/// reads (RoadNetworkGenerator.GroundBeforeRoads): the pool ends where the Location's levelled ground comes out of the water, and
/// the landing is its bank.
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>The crypt of <see cref="FordCryptGround"/> with its levelled ground published, and its landing.</summary>
    private (PlacedLocation crypt, Vector2 landing) CryptInItsPool()
    {
        PlacedLocation crypt = SunkenCryptInASwamp(FordCryptGround);
        crypt.Ground = FordCryptLevelledGround;
        PlacedLocationShape door = PlacedLocationShape.Of(crypt.Centre, 0f, PlacedLocationShape.DoorFlat, 0f, null, crypt.Ground, false, crypt.Record.Root,
            "SunkenCrypt4", entrance: 0, bodyKnown: true);
        crypt.Shape = door;
        crypt.Entrances = new[] { door };
        PlacedLocations.Ready(crypt);
        Vector2 end = RoadLocationEntry.WayInEnd(door, new Vector2(0f, -40f), PlacedLocationShape.Walker.Road(RoadNetworkGenerator.RoadWidth));
        return (crypt, end);
    }

    /// <summary>The road's line from the dirt 40 m out in front of the crypt to its landing, waypoints a cell apart.</summary>
    private static List<Vector2> LineToTheLanding(Vector2 landing)
    {
        var path = new List<Vector2>();
        for (float z = -40f; z < landing.y - 1f; z += RoadPathfinder.CellSize) path.Add(new Vector2(0f, z));
        path.Add(landing);
        return path;
    }

    /// <summary>The pool in front of the crypt is measured from its far shore to where the crypt's levelled ground comes out of the
    /// water, short of the landing, never on past the landing under the crypt as on the natural ground; it is crossed, and the
    /// crossing ends on the landing's side at a bank, not at the road's own end in the water.</summary>
    [Fact]
    public void APoolBesideALocationsLevelledLandingStopsAtTheLandingWhichIsItsBank()
    {
        (PlacedLocation crypt, Vector2 landing) = CryptInItsPool();
        WorldGenerator world = RoadNetworkGenerator.GenerationWorld;
        Assert.True(RoadWater.Wet(FordCryptGround(landing.x, landing.y)), $"the landing's natural ground at {landing} is in the pool");
        Assert.False(RoadWater.Wet(crypt.Ground(landing.x, landing.y)), "the landing's levelled ground is out of the water");
        float landingR = Vector2.Distance(landing, crypt.Centre);

        List<RoadCrossing> found = RoadCrossingDetector.Detect(LineToTheLanding(landing), world, bridges: true);

        RoadCrossing pool = Assert.Single(found, c => c.Shallow);
        // The pool runs from its far shore (26.2 m out) in to where the levelled ground comes out of the water (11.5 m out): on the
        // natural ground it ran on past the landing (8.5 m out) under the crypt, 26 m.
        Assert.True(pool.WetLength <= 26.2f - landingR,
            FormattableString.Invariant($"the pool was measured {pool.WetLength:F1} m, past the landing {landingR:F1} m out: {pool.ShallowNote}"));
        Assert.False(pool.Invalid, pool.ShallowNote);
        float near = Mathf.Min(Vector2.Distance(pool.FromBank, crypt.Centre), Vector2.Distance(pool.ToBank, crypt.Centre));
        Assert.True(near >= landingR - 0.5f, FormattableString.Invariant($"the crossing ends {near:F1} m from the crypt, past its landing: {pool.ShallowNote}"));
        Assert.DoesNotContain("INVALID", pool.ShallowNote);
    }

    /// <summary>At a point on a placed Location's levelled ground the crossing detector (RoadCrossingDetector.Ground), the road's planner
    /// (RoadNetworkGenerator.GroundBeforeRoads, which the earthwork cap, the ridge judge and the way in read) and the search's ground
    /// (RoadTerrainSamples.Ground, through which the search's crossing step reads it) read the same ground, the Location's own
    /// levelled ground, not the natural ground under it.</summary>
    [Fact]
    public void TheCrossingDetectorAndThePlannerReadTheSameGroundAtALevelledPoint()
    {
        (PlacedLocation crypt, Vector2 landing) = CryptInItsPool();
        WorldGenerator world = RoadNetworkGenerator.GenerationWorld;
        var memo = new RoadTerrainSamples(world);
        Vector2i cell = new(0, -1);
        Vector2 centre = RoadPathfinder.CellCentre(cell);
        foreach (Vector2 p in new[] { landing, new Vector2(0f, -10f), new Vector2(0.37f, -11.2f), centre })
        {
            float planner = RoadNetworkGenerator.GroundBeforeRoads(p.x, p.y, world);
            Assert.Equal(planner, RoadCrossingDetector.Ground(p.x, p.y, world));
            Assert.Equal(crypt.Ground(p.x, p.y), planner, 3);
            Assert.NotEqual(FordCryptGround(p.x, p.y), planner, 2);
        }
        Assert.Equal(RoadNetworkGenerator.GroundBeforeRoads(centre.x, centre.y, world), memo.Ground(cell));
        Assert.Equal(RoadWater.Wet(RoadNetworkGenerator.GroundBeforeRoads(landing.x, landing.y, world)),
            RoadWater.Wet(RoadCrossingDetector.Ground(landing.x, landing.y, world)));
    }

    /// <summary>A Dvergr viaduct's layout (its ends, deck at grade, piers and end-step cuts) stands on the ground every structure's
    /// ends are decided on (StructureEnds, the wooden kit's BridgeLayout.Generator): the ground before roads, so on a placed
    /// Location's levelled ground it reads the Location's ground, not the natural ground under it.</summary>
    [Fact]
    public void AViaductReadsTheGroundBeforeRoadsAsTheWoodenKitDoes()
    {
        (PlacedLocation crypt, Vector2 landing) = CryptInItsPool();
        WorldGenerator world = RoadNetworkGenerator.GenerationWorld;
        Func<Vector2, float> wooden = BridgeLayout.Generator(world);
        foreach (Vector2 p in new[] { landing, new Vector2(0f, -10f), new Vector2(0.37f, -11.2f) })
        {
            float viaduct = DvergrViaduct.Ground(p, world);
            Assert.Equal(wooden(p), viaduct);
            Assert.Equal(crypt.Ground(p.x, p.y), viaduct, 3);
            Assert.NotEqual(FordCryptGround(p.x, p.y), viaduct, 2);
        }
    }
}
