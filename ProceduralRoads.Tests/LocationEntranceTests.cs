using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A location's entrances (<see cref="PlacedLocationShape.DoorLandings"/>), measured from the game's prefabs: each turns with its
/// location, faces out along its own facing and keeps its measured clearance off the location's built pieces on its way in; the
/// door landing a location had before is its fallback, shaped as before.
/// </summary>
public class LocationEntranceTests
{
    private static readonly Vector2 Centre = new(100f, 200f);

    [Fact]
    public void TheTableHoldsTheMeasuredEntrancesAndTheDoorLandingsBefore()
    {
        Assert.Equal(33, PlacedLocationShape.DoorLandings.Count);
        LocationEntrances boss = PlacedLocationShape.DoorLandings["Mistlands_DvergrBossEntrance1"];
        LocationEntrance stairsFoot = Assert.Single(boss.Entrances);
        Assert.Equal(new Vector2(0f, 24.5f), stairsFoot.At);
        Assert.Equal(0f, stairsFoot.Facing);
        Assert.Equal(2f, stairsFoot.Clearance);
        Assert.Equal(new Vector2(2.1f, 21.9f), boss.Fallback);
        Assert.Equal(new Vector2(0.5f, -8.5f), PlacedLocationShape.DoorLandings["Crypt2"].Fallback);
        Assert.Equal(1.5f, PlacedLocationShape.DoorLandings["Mistlands_GuardTower1_ruined_new"].Entrances[0].Clearance);
        // A location given its entrances by the table alone has no door landing of its own to fall back to.
        Assert.Null(PlacedLocationShape.DoorLandings["WoodHouse1"].Fallback);
        Assert.True(PlacedLocationShape.HasDoor("WoodHouse1"));
        Assert.False(PlacedLocationShape.ReachedOnlyAtADoor("WoodHouse1"));
        Assert.True(PlacedLocationShape.ReachedOnlyAtADoor("Crypt4"));
        // The entrances the walk could not reach on foot are left out: open until checked in game.
        foreach (string open in new[] { "SwampHut3", "SwampHut4", "SwampHut5", "PlaceofMystery3" })
            Assert.False(PlacedLocationShape.HasDoor(open));
    }

    [Fact]
    public void AnEntranceTurnsWithItsLocationAndFacesOutAlongItsFacing()
    {
        // Ruin2's door is on its prefab's +x side: (5.49, 1.71), facing 85.
        PlacedLocationShape shape = PlacedLocationShape.Of(Centre, 90f, PlacedLocationShape.DoorFlat, 0f, null, prefab: "Ruin2", entrance: 0);
        Assert.Equal(0, shape.Entrance);
        Assert.Equal(2f, shape.DoorClearance);
        Vector2 door = shape.Door!.Value;
        Assert.InRange(Vector2.Distance(door, Centre + new Vector2(1.71f, -5.49f)), 0f, 1e-3f);
        Assert.Equal(door, shape.Landing);
        Assert.InRange(Vector2.Distance(shape.Front, StairLayout.Forward(175f)), 0f, 1e-4f);
        Assert.Equal(PlacedLocationShape.EntranceLanding("Ruin2", 0, Centre, 90f), door);
    }

    [Fact]
    public void TheFallbackIsTheDoorLandingBeforeShapedAsBefore()
    {
        PlacedLocationShape fallback = PlacedLocationShape.Of(Centre, 0f, PlacedLocationShape.DoorFlat, 0f, null, prefab: "Crypt2");
        Assert.Equal(-1, fallback.Entrance);
        Assert.True(float.IsNaN(fallback.DoorClearance));
        Assert.Equal(Centre + new Vector2(0.5f, -8.5f), fallback.Door);
        // A crypt entered from its prefab's back faces out of it.
        Assert.InRange(Vector2.Distance(fallback.Front, -StairLayout.Forward(0f)), 0f, 1e-4f);
        PlacedLocationShape entrance = PlacedLocationShape.Of(Centre, 0f, PlacedLocationShape.DoorFlat, 0f, null, prefab: "Crypt2", entrance: 0);
        Assert.Equal(Centre + new Vector2(0.5f, -7.25f), entrance.Door);
        Assert.InRange(Vector2.Distance(entrance.Front, fallback.Front), 0f, 1e-4f);
        // A location with no door landing before has none as its fallback shape.
        Assert.Null(PlacedLocationShape.Of(Centre, 0f, PlacedLocationShape.DoorFlat, 0f, null, prefab: "WoodHouse1").Door);
    }

    [Fact]
    public void AnEntranceKeepsItsClearanceOffItsBuiltPiecesOnItsWayIn()
    {
        // A Dvergr tower's landing, 13.5 m out of its front with 1.5 m of open ground round it; a built piece 1.2 m to its side.
        Vector2 landing = Centre + new Vector2(-0.25f, 13.5f);
        var piece = new List<LocationRock> { new(landing + new Vector2(1.7f, 0f), 0.25f, 0.25f, 0f, 0f, 2f, "piece_dvergr_stair", built: true) };
        PlacedLocationShape entrance = PlacedLocationShape.Of(Centre, 0f, PlacedLocationShape.DoorFlat, 0f, piece,
            prefab: "Mistlands_GuardTower1_ruined_new", entrance: 0);
        Assert.Equal(landing, entrance.Landing);
        Assert.True(entrance.InDoorCorridor(landing));
        Assert.Contains("at its entrance", entrance.Refusal(landing, 1f, 1f));
        // 2 m further out the piece is 1.5 m off or more: clear.
        Assert.Equal("", entrance.Refusal(landing + new Vector2(-1f, 2f), 1f, 1f));
        // A door landing measured with no clearance keeps none on its way in, as before.
        Vector2 old = Centre + new Vector2(2.1f, 21.9f);
        var near = new List<LocationRock> { new(old + new Vector2(1.2f, 0f), 0.25f, 0.25f, 0f, 0f, 2f, "piece_dvergr_stair", built: true) };
        PlacedLocationShape before = PlacedLocationShape.Of(Centre, 0f, PlacedLocationShape.DoorFlat, 0f, near, prefab: "Mistlands_DvergrBossEntrance1");
        Assert.Equal("", before.Refusal(old, 1f, 1f));
    }

    [Fact]
    public void AnEntranceInsideItsSunkYardKeepsThePitOffOnlyOnItsOwnSideOfTheLanding()
    {
        // A Dvergr mine shaft (Mistlands_DvergrTownEntrance1, yaw 315): its yard sunk 2 m over 20 m, its entrance landing
        // (-0.3, 10.24) facing 6 inside it. In game every road onto that landing was refused 23.6 m from the floor, 24 kept.
        var yard = new LevelOp(0f, 0f, -2f, 16f, false, 20f);
        PlacedLocationShape entrance = PlacedLocationShape.Of(Centre, 315f, yard, 0f, null, prefab: "Mistlands_DvergrTownEntrance1", entrance: 0);
        Vector2 landing = entrance.Landing, out1 = entrance.Front;
        Assert.InRange(Vector2.Distance(landing, PlacedLocationShape.EntranceLanding("Mistlands_DvergrTownEntrance1", 0, Centre, 315f)!.Value), 0f, 1e-3f);
        // The landing and the way in front of it, out to 23.6 m from the floor, are outside its pit.
        Assert.Equal("", entrance.Refusal(landing, 4f, 1f));
        foreach (float d in new[] { 4f, 8f, 13.2f })
            Assert.Equal("", entrance.Refusal(landing + out1 * d, 4f, 1f));
        // Behind the landing, toward the shaft, the pit is kept as before.
        Assert.Contains("in the entrance", entrance.Refusal(landing - out1 * 4f, 4f, 1f));
        // The location's fallback (no door landing) keeps its whole pit, measured from the floor.
        PlacedLocationShape fallback = PlacedLocationShape.Of(Centre, 315f, yard, 0f, null, prefab: "Mistlands_DvergrTownEntrance1");
        Assert.Contains("in the entrance", fallback.Refusal(landing + out1 * 13.2f, 4f, 1f));
    }

    [Fact]
    public void AnEntranceKeepsItsClearanceOffItsMouthsBouldersOnItsWayIn()
    {
        // The Queen's building (Mistlands_DvergrTownEntrance2): its own cliff_mistlands1, by its box, 3.8 m off its measured
        // landing's way in, where the road's 4 m reach was kept (3840,6336 in game). The entrance was measured 2 m clear over it.
        // Turned 311 so it faces out along +z (facing 49), its box seen square on.
        PlacedLocationShape probe = PlacedLocationShape.Of(Centre, 311f, PlacedLocationShape.DoorFlat, 0f, null, prefab: "Mistlands_DvergrTownEntrance2", entrance: 0);
        Vector2 landing = probe.Landing, front = probe.Front;
        Vector2 side = new(front.y, -front.x);
        // A large boulder of its body (inside its exterior radius, 30 here), its box's edge 3 m to the landing's side.
        var cliff = new List<LocationRock> { new(landing + side * 13f, 10f, 10f, 0f, 0f, 30f, "cliff_mistlands1") };
        PlacedLocationShape entrance = PlacedLocationShape.Of(Centre, 311f, PlacedLocationShape.DoorFlat, 30f, cliff,
            prefab: "Mistlands_DvergrTownEntrance2", entrance: 0);
        Assert.Single(entrance.Body);
        Assert.True(entrance.InDoorCorridor(landing));
        // On its way in: its measured clearance (2 m) is kept, not the road's 4 m reach.
        Assert.Equal("", entrance.Refusal(landing, 4f, 1f));
        Assert.Contains("at its mouth's cliff_mistlands1", entrance.Refusal(landing + side * 1.5f + front * 0.5f, 4f, 1f));
        // Off its way in (behind the landing), the reach is kept as before.
        Assert.Contains("at its mouth's cliff_mistlands1", entrance.Refusal(landing - front * 1f, 4f, 1f));
    }

    [Fact]
    public void ALocationWithEntrancesArrivesAtItsFirstEntrancesLevel()
    {
        // A Dvergr mine shaft on a slope: its entrance landing in its yard 16.6 m over the ground its fallback landing (its yard's
        // edge) stands on, as at 5632,-3648. Its arrival height, the level its road comes in at, is its entrance's.
        var yard = new LevelOp(0f, 0f, -2f, 16f, false, 20f);
        PlacedLocationShape fallback = PlacedLocationShape.Of(Centre, 0f, yard, 0f, null, prefab: "Mistlands_DvergrTownEntrance1");
        PlacedLocationShape entrance = PlacedLocationShape.Of(Centre, 0f, yard, 0f, null, prefab: "Mistlands_DvergrTownEntrance1", entrance: 0);
        float Ground(float x, float z) => Vector2.Distance(new Vector2(x, z), Centre) <= 12f ? 81.1f : 64.5f;
        var placed = new PlacedLocation { Ground = Ground, Shape = fallback, Entrances = new[] { entrance }, Fallback = fallback };
        Assert.Same(entrance, placed.Aim);
        Assert.Equal(81.1f, placed.LandingHeight);
        // Without entrances, its one shape.
        var plain = new PlacedLocation { Ground = Ground, Shape = fallback };
        Assert.Same(fallback, plain.Aim);
        Assert.Equal(64.5f, plain.LandingHeight);
    }
}
