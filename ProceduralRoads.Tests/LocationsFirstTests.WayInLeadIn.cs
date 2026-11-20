using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A road to a placed Location's door comes in head on to its way onto the landing (RoadNetworkGenerator.LeadInOnTheWayIn), and a
/// Location whose own landing is behind its entrance has no fallback landing there (PlacedLocations.FallbackOf). Offline and in game
/// a road searched to a door off the landing's bearing came in radially, toward the centre, then turned onto its way in's lead round
/// the location and again onto the front: two tight turns back to back on open ground (a TrollCave02's road swung past its mouth,
/// doubled back, then ran down its lane). A SunkenCrypt4 that reached no entrance ended on its own landing out of its prefab's front,
/// against the crypt's back wall, the water between it and the door.
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>A troll cave at the origin facing +z (yaw 0), its mouth cut into rock (its way in along its door's lane only), on the
    /// level ground, placed and ready.</summary>
    private PlacedLocation TrollCaveReady()
    {
        const string cave = "TrollCave02";
        var flat = new List<LevelOp> { PlacedLocationShape.DoorFlat };
        var record = new PlacedLocationRecord
        {
            Prefab = cave, Centre = new Vector2(0f, 0f), Yaw = 0f, Root = 50f, RockRadius = 0f,
            Ops = flat, X0 = -60, Z0 = -60, W = 121, H = 121,
        };
        Func<float, float, float> ground = PlacedLocationGround.Sampler(record.Ground((x, z) => _world.GetHeight(x, z)), record.X0, record.Z0, record.W, record.H);
        PlacedLocationShape entrance = PlacedLocationShape.Of(record.Centre, record.Yaw, PlacedLocationShape.DoorFlat, 0f, null, ground, false, record.Root,
            cave, entrance: 0, bodyKnown: true);
        var location = new PlacedLocation { Record = record, Ground = ground, Shape = entrance, Entrances = new[] { entrance } };
        PlacedLocations.Ready(location);
        LocationLevelling.Source = c => c.magnitude < 0.5f ? flat : null;
        return location;
    }

    /// <summary>A placed Location with an entrance has doors all round (the entrance side priced cheaper, PlacedLocation.EntranceArc);
    /// from a door on its entrance side the way in runs straight toward the landing (at a cave cut into rock too: the entrance side,
    /// not a lane), and every door's lead-in is a search cell back along the line its way in leaves it on, so the road carries
    /// straight on into its way in, never toward the centre.</summary>
    [Fact]
    public void ARoadComesInToAPlacedLocationsDoorHeadOnToItsWayIn()
    {
        PlacedLocation cave = TrollCaveReady();
        PlacedLocationShape aim = cave.Aim;
        var walker = PlacedLocationShape.Walker.Road(RoadNetworkGenerator.RoadWidth);
        IReadOnlyList<RoadDoor> doors = RoadNetworkGenerator.DoorsOf(cave.Centre, Footprint, RoadNetworkGenerator.RoadWidth);
        Assert.NotEmpty(doors);
                foreach (RoadDoor d in doors)
        {
            List<Vector2> line = RoadLocationEntry.Route(d.At, aim, RoadLocationEntry.WayInEnd(aim, d.At, walker), walker);
            Vector2 leaves = (line[1] - d.At).normalized, inward = (d.At - d.LeadIn).normalized;
            Assert.True(Mathf.Abs(Vector2.Distance(d.LeadIn, d.At) - RoadNetworkGenerator.LeadInLength) < 0.01f, $"lead-in {d.LeadIn} for the door {d.At}");
            Assert.True(Vector2.Dot(inward, leaves) > 0.999f,
                FormattableString.Invariant($"the door at ({d.At.x:F1}, {d.At.y:F1}): the road comes in along ({inward.x:F2}, {inward.y:F2}), its way in leaves along ({leaves.x:F2}, {leaves.y:F2})"));
            // A door on the entrance side: its way in runs straight in from it, no lead round, no fold.
            if (aim.InFront(d.At) && aim.OnTheWayInAllTheWay(d.At, line[line.Count - 1]))
                Assert.True(line.Count == 2, $"the way in from {d.At} is led round ({line.Count} points)");
        }
    }

    /// <summary>The way onto a crypt's door landing ends on its landing region's lip within the network's reach of the landing (no
    /// setback, no fixed point: the rule a staircase's top is held to), level with the ground there, and the network counts it
    /// reached there.</summary>
    [Fact]
    public void AWayInEndsOnItsEntrancesLandingLipLevelWithIt()
    {
        PlacedLocation crypt = SunkenCryptInASwamp();
        var (path, plans) = RoadAcrossTheSwamp();
        PlacedLocationShape door = crypt.Entrances[0];

        bool entered = RoadNetworkGenerator.PlanEntry(crypt, false, path, plans, new List<RoadStairSpan>(), new List<RoadCrossing>(), RoadNetworkGenerator.RoadWidth,
            out RoadSpatialGrid.PlannedPath? road, out _, out string why);

        Assert.True(entered && road != null, why);
        Vector2 last = road!.Points[road.Points.Count - 1];
        Vector2 end = RoadLocationEntry.WayInEnd(door, path[path.Count - 1], PlacedLocationShape.Walker.Road(RoadNetworkGenerator.RoadWidth));
        Assert.True(OnTheLip(door, end) && Vector2.Distance(end, door.Landing) <= RoadNetworkGenerator.LandingReach, $"the end {end}, the door {door.Landing}");
        Assert.True(OnTheLip(door, last), $"the way in ends at {last}, off the lip (the first end from the ring: {end})");
        float landing = crypt.Ground(end.x, end.y);
        Assert.True(Mathf.Abs(road.Heights[road.Heights.Count - 1] - landing) <= StairLayout.StepUp,
            FormattableString.Invariant($"the way in ends at {road.Heights[road.Heights.Count - 1]:F2}, the landing at {landing:F2}"));
        Assert.True(Vector2.Distance(last, door.Landing) <= RoadNetworkGenerator.LandingReach && door.OnTheWayIn(last));
    }

    /// <summary>A Location's fallback is its levelled ground as a whole wherever it levels some (PlacedLocationShape.LevelledArea:
    /// ended on from any side, its body kept off). Where none is known: a SunkenCrypt4's own landing (its shape without its entrance,
    /// out of its prefab's front) is the crypt's back, one fixed point, so no fallback and a road that reaches no entrance stops at its
    /// ring; a crypt whose fallback is a door landing in front keeps it; a Location with no entrance has its own landing first and no
    /// other fallback. Every reader of the fallback reads the one field: the ladder's fallback rung, the targets the reached test
    /// walks.</summary>
    [Fact]
    public void ALocationsFallbackIsItsLevelledGroundElseNeverBehindItsEntrance()
    {
        // A levelled flat 6 m round its centre, as a crypt levels its mound.
        var flat = new LevelOp(0f, 0f, 0f, 6f, false, 10f, 2f);
        Func<float, float, float> ground = (_, _) => 31f;
        PlacedLocationShape Shape(string prefab, int entrance = -1) =>
            PlacedLocationShape.Of(new Vector2(0f, 0f), 0f, flat, 0f, null, ground, false, 31f, prefab, entrance: entrance, bodyKnown: true);

        PlacedLocationShape sunkBack = Shape("SunkenCrypt4"), sunkDoor = Shape("SunkenCrypt4", 0);
        Assert.True(Vector2.Dot(sunkBack.Landing, sunkDoor.Front) < 0f, $"its own landing {sunkBack.Landing} is behind its door {sunkDoor.Landing}");
        Assert.Null(PlacedLocations.FallbackOf(sunkBack, new[] { sunkDoor }));
        var crypt = new PlacedLocation { Shape = sunkBack, Entrances = new[] { sunkDoor }, Fallback = PlacedLocations.FallbackOf(sunkBack, new[] { sunkDoor }) };
        Assert.DoesNotContain(crypt.Targets, t => t.Entrance < 0);

        PlacedLocationShape crypt4Fallback = Shape("Crypt4"), crypt4Door = Shape("Crypt4", 0);
        Assert.Same(crypt4Fallback, PlacedLocations.FallbackOf(crypt4Fallback, new[] { crypt4Door }));

        PlacedLocationShape ruin = Shape("StoneTowerRuins07");
        Assert.Null(PlacedLocations.FallbackOf(ruin, Array.Empty<PlacedLocationShape>()));

        // With its levelling known: its levelled ground, behind its entrance or not.
        var ops = new List<LevelOp> { flat };
        PlacedLocationShape area = PlacedLocationShape.Of(new Vector2(0f, 0f), 0f, flat, 0f, null, ground, false, 31f, "SunkenCrypt4", bodyKnown: true, levelledArea: true);
        Assert.True(area.LevelledArea);
        Assert.Same(area, PlacedLocations.FallbackOf(sunkBack, new[] { sunkDoor }, area));
        Assert.Same(area, PlacedLocations.FallbackOf(ruin, Array.Empty<PlacedLocationShape>(), area));
        var levelledCrypt = new PlacedLocation { Shape = area, Entrances = new[] { sunkDoor }, Fallback = area };
        Assert.Contains(levelledCrypt.Targets, t => t.LevelledArea);
    }

    /// <summary>A Crypt3 in its prefab's frame (its door at the top of its stairs behind, facing -z; its flat far from it, round
    /// (0.01, 5.51)): a road end at a side door, on the flat's entrance side but beside the door's own line, (23.6, 1.34). Measured
    /// on the line from the road's end, its way in ended 0.86 m behind the door's line, off the entrance side measured from the door.
    /// Now it ends on its landing region's lip within the network's reach of the door's landing (PlacedLocationShape.Ends, the rule a
    /// staircase's top is held to), never measured from the road's end; no setback, no fixed point.</summary>
    [Fact]
    public void AWayInFromASideDoorEndsOnTheEntranceSideOfItsDoor()
    {
        var crypt3Ops = new[] { new LevelOp(-2.07f, -8.4f, 0f, 4f, false, 6f, 3f), new LevelOp(0.01f, 5.51f, 0f, 5f, false, 7f, 3f), new LevelOp(0f, 3.53f, -3f, 3f, false, 5f, 3f) };
        LevelOp flat = RoadStairRuns.FlatOf(crypt3Ops) ?? PlacedLocationShape.DoorFlat;
        Func<float, float, float> ground = (_, _) => 50f;
        PlacedLocationShape door = PlacedLocationShape.Of(new Vector2(0f, 0f), 0f, flat, 11f, null, ground, false, 50f, "Crypt3", entrance: 0, bodyKnown: true);
        var walker = PlacedLocationShape.Walker.Road(RoadNetworkGenerator.RoadWidth);
        var from = new Vector2(23.6f, 1.34f);
        Assert.True(door.Door.HasValue && door.InFront(from), $"the side door {from} is not on the flat's entrance side");
        // As it was: from the door toward the road's end.
        Vector2 straight = door.Door!.Value + (from - door.Door.Value).normalized * door.DoorClearance;
        Assert.False(door.OutsideItsDoor(straight), $"the straight line's end {straight} is outside the door");

        Vector2 end = RoadLocationEntry.WayInEnd(door, from, walker);

        Assert.True(OnTheLip(door, end), $"the way in ends at {end}, off its landing's lip");
        Assert.True(Vector2.Distance(end, door.Landing) <= RoadNetworkGenerator.LandingReach, $"the way in ends at {end}, {Vector2.Distance(end, door.Landing):F2} m from the landing");
    }

    /// <summary>A road ending behind a troll cave (its mouth cut into rock, facing +z): its way in is led round the cave's rock on its
    /// lead-round ring (RoadLocationEntry.Route) and planned with the road as its last stretch, so the road is pulled, swayed and
    /// splined through those corners. As shaped, every point of the road and its way in within the lead-round ring keeps off the
    /// cave's edge (PlacedLocationShape.KeepsOff): no shaping cuts a corner back toward the rock it was led round.</summary>
    [Fact]
    public void AWayInLedRoundFromBehindStillKeepsOffTheLocationOnceShaped()
    {
        PlacedLocation cave = TrollCaveReady();
        SetPathfinder(new RoadPathfinder(_world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        Vector2 At(float z) => new(0f, z);
        var path = Enumerable.Range(0, 9).Select(k => At(-22f - RoadPathfinder.CellSize * (8 - k))).ToList();
        var points = Enumerable.Range(0, Mathf.RoundToInt(RoadPathfinder.CellSize * 8) + 1).Select(k => At(path[0].y + k)).ToList();
        var plans = new List<RoadSpatialGrid.PlannedPath> { new(points, points.ConvertAll(p => cave.Ground(p.x, p.y)), points.ConvertAll(_ => default(RoadPointDebugInfo)),
            RoadNetworkGenerator.RoadWidth, points.Count - 1, false) };
        var walker = PlacedLocationShape.Walker.Road(RoadNetworkGenerator.RoadWidth);
        Assert.True(RoadLocationEntry.Route(path[path.Count - 1], cave.Shape, cave.Shape.Landing, walker).Count > 2, "the way in is not led round");

        bool entered = RoadNetworkGenerator.PlanEntry(cave, false, path, plans, new List<RoadStairSpan>(), new List<RoadCrossing>(), RoadNetworkGenerator.RoadWidth,
            out RoadSpatialGrid.PlannedPath? road, out _, out string why);

        Assert.True(entered && road != null, why);
        float ring = cave.Shape.LeadRoundRing(walker);
        foreach (Vector2 p in plans[plans.Count - 1].Points.Concat(road!.Points))
        {
            if (Vector2.Distance(p, cave.Centre) > ring) continue;
            string off = cave.Shape.KeepsOff(p, walker);
            Assert.True(off == "", FormattableString.Invariant($"at ({p.x:F1}, {p.y:F1}): {off}"));
        }
    }
}
