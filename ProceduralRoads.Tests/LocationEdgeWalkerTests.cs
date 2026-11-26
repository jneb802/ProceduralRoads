using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A Location's edge has one keeps-off test (<see cref="PlacedLocationShape.KeepsOff"/>) and one lead-round ring
/// (<see cref="PlacedLocationShape.LeadRoundRing"/>), each with the walker as its parameter (<see cref="PlacedLocationShape.Walker"/>):
/// the road onto a landing (RoadLocationEntry), the staircase to it (RoadStairRuns) and the network reaching it on its entrance side
/// (RoadNetworkGenerator.ReachedTarget) ask them, never their own copies of the widths.
/// </summary>
public class LocationEdgeWalkerTests
{
    private const float Width = 4f, RockRadius = 12f;
    private static readonly Vector2 Centre = new(0f, 0f);

    // A body boulder beside the mouth.
    private static readonly LocationRock Mouth = new(new Vector2(6f, 4f), 1.5f, 1.5f, 0f, 0f, 4f, "rock1_mountain");

    private static PlacedLocationShape Shape() =>
        PlacedLocationShape.Of(Centre, 0f, PlacedLocationShape.DoorFlat, RockRadius, new List<LocationRock> { Mouth },
            prefab: "TrollCave02", entrance: 0, bodyKnown: false);

    /// <summary>A road led round from behind and a staircase led round from behind circle the Location on the ring its owner
    /// gives each walker: the road's chords no nearer than its ring, the staircase's waypoints on its own.</summary>
    [Fact]
    public void ARoadAndAStaircaseFromBehindAreLedRoundOnTheRingItsOwnerGivesTheirWalker()
    {
        PlacedLocationShape shape = Shape();
        Vector2 behind = Centre - shape.Front * 40f;
        Assert.True(shape.Behind(behind, shape.Landing, PlacedLocationShape.Walker.Road(Width)));

        var road = PlacedLocationShape.Walker.Road(Width);
        List<Vector2> line = RoadLocationEntry.Route(behind, shape, shape.Landing, road);
        Assert.True(line.Count > 2, "the road is led round");
        float roadRing = shape.LeadRoundRing(road);
        for (int i = 1; i < line.Count - 1; i++) Assert.True(Vector2.Distance(line[i], Centre) >= roadRing - 1e-3f, $"waypoint {line[i]} inside {roadRing}");

        var stairs = PlacedLocationShape.Walker.Stairs(RoadStairRuns.FillerReach);
        float stairRing = shape.LeadRoundRing(stairs);
        List<Vector2> around = RoadStairRuns.Around(behind, Centre, shape.Front, stairRing);
        for (int i = 1; i < around.Count; i++) Assert.Equal(stairRing, Vector2.Distance(around[i], Centre), 3);

        Assert.Equal(shape.KeepOut + road.RingReach, roadRing, 4);
        Assert.Equal(shape.KeepOut + stairs.RingReach, stairRing, 4);
    }

    /// <summary>The road's way in, the staircase's layout bounds and the reached test's lane judge a point by the one keeps-off
    /// test with their walker: what the shape refuses for the walker is what each refuses.</summary>
    [Fact]
    public void TheRoadTheStairsAndTheLaneJudgeAPointByTheOneKeepsOffTestWithTheirWalker()
    {
        PlacedLocationShape shape = Shape();
        var bounds = new RoadStairRuns.ApproachBounds(shape, RoadStairRuns.LayoutGround.Own((x, z) => 0f));
        var road = PlacedLocationShape.Walker.Road(Width);
        int refusedByRoad = 0, refusedByStairs = 0;
        for (float x = -20f; x <= 20f; x += 0.5f)
            for (float z = -20f; z <= 20f; z += 0.5f)
            {
                var p = new Vector2(x, z);
                Assert.Equal(shape.KeepsOff(p, PlacedLocationShape.Walker.Stairs(RoadStairRuns.FillerReach)) == "", bounds.Allowed(p, RoadStairRuns.FillerReach));
                Assert.Equal(shape.KeepsOff(p, PlacedLocationShape.Walker.Stairs(0f)) == "", bounds.Allowed(p, 0f));
                Assert.Equal(shape.KeepsOff(p, road), shape.Refusal(p, road.Margin, road.RockMargin, road.BodyMargin));
                if (shape.KeepsOff(p, road) != "") refusedByRoad++;
                if (!bounds.Allowed(p, RoadStairRuns.FillerReach)) refusedByStairs++;
            }
        Assert.True(refusedByRoad > 0 && refusedByStairs > 0);
    }

    // A Location placed first, its body read: a wall standing behind it, inside its exterior radius, its front +z.
    private const float TowerRadius = 24f;
    private static readonly LevelOp TowerFlat = new(0f, 0f, 0f, 6f, false);

    private static PlacedLocationShape Tower(bool bodyKnown) =>
        PlacedLocationShape.Of(Centre, 0f, TowerFlat, TowerRadius,
            new List<LocationRock> { new(new Vector2(0f, -20f), 6f, 0.25f, 0f, 0f, 4f, "stone_wall_4x2", built: true) },
            (_, _) => 0f, root: 0f, bodyKnown: bodyKnown);

    /// <summary>Where a placed-first Location's body is known, its old rock-radius ring only leads a way round: a way in from
    /// behind whose straight line its body refuses is led round on the ring, by the road and the staircase alike; one whose
    /// straight line keeps off the body comes straight in, inside the radius; no point is refused by the radius; and the body
    /// still keeps its gap. Where the body is not known, every way in from behind is led round as before.</summary>
    [Fact]
    public void APlacedFirstLocationWithAKnownBodyIsLedRoundOnTheRadiusRingButNeverRefusedByIt()
    {
        PlacedLocationShape shape = Tower(bodyKnown: true);
        var road = PlacedLocationShape.Walker.Road(Width);
        var stairs = PlacedLocationShape.Walker.Stairs(RoadStairRuns.FillerReach);
        Vector2 landing = shape.Landing;

        // Behind, the wall across its straight line in: led round on the ring, the road's chords and the staircase's waypoints.
        Vector2 walled = new(0f, -45f);
        Assert.False(shape.KeepsOffAllTheWay(walled, landing, road));
        Assert.True(shape.Behind(walled, landing, road));
        Assert.True(shape.Behind(walled, landing, stairs));
        List<Vector2> line = RoadLocationEntry.Route(walled, shape, landing, road);
        Assert.True(line.Count > 2, "the road is led round");
        float roadRing = shape.LeadRoundRing(road);
        for (int i = 1; i < line.Count - 1; i++) Assert.True(Vector2.Distance(line[i], Centre) >= roadRing - 1e-3f, $"waypoint {line[i]} inside {roadRing}");
        float stairRing = shape.LeadRoundRing(stairs);
        List<Vector2> around = RoadStairRuns.Around(walled, Centre, shape.Front, stairRing);
        Assert.True(around.Count > 1);
        for (int i = 1; i < around.Count; i++) Assert.Equal(stairRing, Vector2.Distance(around[i], Centre), 3);

        // Behind, its straight line clear of the wall: straight in, though it crosses the radius off the entrance side.
        Vector2 clear = new(-30f, -14f);
        Assert.False(shape.InFront(clear));
        Assert.True(shape.KeepsOffAllTheWay(clear, landing, road));
        Assert.False(shape.Behind(clear, landing, road));
        Assert.Equal(new List<Vector2> { clear, landing }, RoadLocationEntry.Route(clear, shape, landing, road));
        bool insideTheRadius = false;
        for (int k = 0; k <= 40; k++)
        {
            Vector2 p = Vector2.Lerp(clear, landing, k / 40f);
            insideTheRadius |= Vector2.Distance(p, Centre) < shape.KeepOut + road.Margin && !shape.InFront(p);
        }
        Assert.True(insideTheRadius, "the straight line crosses the radius off the entrance side");

        // No point is refused by the radius, for either walker; the wall keeps its gap.
        for (float x = -40f; x <= 40f; x += 1f)
            for (float z = -40f; z <= 40f; z += 1f)
            {
                var p = new Vector2(x, z);
                Assert.DoesNotContain("in the location's rock", shape.KeepsOff(p, road));
                Assert.DoesNotContain("in the location's rock", shape.KeepsOff(p, stairs));
            }
        Assert.Contains("built pieces", shape.KeepsOff(new Vector2(0f, -22f), road));
        Assert.Contains("built pieces", shape.KeepsOff(new Vector2(0f, -22f), stairs));
        Assert.Equal("", shape.KeepsOff(new Vector2(0f, -27f), road));

        // Not known, the radius still keeps the way off and every end behind is led round, as before.
        PlacedLocationShape unknown = Tower(bodyKnown: false);
        Assert.True(unknown.Behind(clear, landing, road));
        Assert.True(RoadLocationEntry.Route(clear, unknown, landing, road).Count > 2);
        Assert.StartsWith("in the location's rock", unknown.KeepsOff(new Vector2(-15f, -4f), road));
    }
    /// <summary>A way in to a Crypt3 from its ring at its side, where a way went straight in through its own stones and stair
    /// slabs to its landing: its prefab's static rocks (as LocationRock.OfPart places them) are its body, so the straight line in is
    /// refused for the road and the staircase alike, they agree on whether it is led round (Behind), and the road's line onto the
    /// landing (RoadLocationEntry.Route) never stands in one of them. From straight behind it, both are led round on their rings.
    /// Without the prefab's rocks the same end went straight in, through them.</summary>
    [Fact]
    public void AWayInFromBehindACryptNeverPassesThroughItsOwnRock()
    {
        List<LocationRock> stones = Crypt3Stones.Rocks();
        PlacedLocationShape shape = Crypt3Stones.Shape(stones);
        var road = PlacedLocationShape.Walker.Road(Width);
        var stairs = PlacedLocationShape.Walker.Stairs(RoadStairRuns.FillerReach);
        Vector2 landing = shape.Door!.Value;
        Vector2 end = Crypt3Stones.RingEnd;
        Assert.Equal(24, shape.Body.Count);
        // Its stair slab is walked onto at its door (the next test); every other stone is never under the road.
        string slab = Assert.Single(shape.DoorStairs).Name;
        List<LocationRock> others = stones.Where(r => r.Name != slab).ToList();

        Assert.False(shape.KeepsOffAllTheWay(end, landing, road));
        Assert.False(shape.KeepsOffAllTheWay(end, landing, stairs));
        Assert.Equal(shape.Behind(end, landing, road), shape.Behind(end, landing, stairs));
        List<Vector2> line = RoadLocationEntry.Route(end, shape, landing, road);
        Assert.True(line.Count > 2, "the road is led round");
        foreach (Vector2 p in Along(line))
            foreach (LocationRock rock in others)
                Assert.False(rock.InTheWay(p, Width * 0.5f, Crypt3Stones.Ground(p.x, p.y), out _), $"{p.x:F1}, {p.y:F1} is under its {rock.Name}");
        // Past the leg off its end (which ended inside the reach of its cap slab's box), every point keeps off the crypt.
        foreach (Vector2 p in Along(line.Skip(1).ToList())) Assert.Equal("", shape.KeepsOff(p, road));

        // Straight behind it, its body across the line in: led round, the road on its ring and the staircase on its own.
        Vector2 behind = Crypt3Stones.Centre - shape.Front * 25f;
        Assert.False(shape.InFront(behind));
        Assert.True(shape.Behind(behind, landing, road));
        Assert.True(shape.Behind(behind, landing, stairs));
        List<Vector2> round = RoadLocationEntry.Route(behind, shape, landing, road);
        float roadRing = shape.LeadRoundRing(road);
        for (int i = 1; i < round.Count - 1; i++) Assert.True(Vector2.Distance(round[i], Crypt3Stones.Centre) >= roadRing - 1e-3f);
        foreach (Vector2 p in Along(round))
            foreach (LocationRock rock in others)
                Assert.False(rock.InTheWay(p, Width * 0.5f, Crypt3Stones.Ground(p.x, p.y), out _), $"{p.x:F1}, {p.y:F1} is under its {rock.Name}");
        float stairRing = shape.LeadRoundRing(stairs);
        List<Vector2> around = RoadStairRuns.Around(behind, Crypt3Stones.Centre, shape.Front, stairRing);
        Assert.True(around.Count > 1);
        for (int i = 1; i < around.Count; i++) Assert.Equal(stairRing, Vector2.Distance(around[i], Crypt3Stones.Centre), 3);

        // Without them, the ring end went straight in through its stones.
        PlacedLocationShape bare = Crypt3Stones.Shape(new List<LocationRock>());
        Assert.Equal(new List<Vector2> { end, landing }, RoadLocationEntry.Route(end, bare, landing, road));
        Assert.Contains(Along(new List<Vector2> { end, landing }), p => others.Any(r => r.InTheWay(p, Width * 0.5f, Crypt3Stones.Ground(p.x, p.y), out _)));
    }

    /// <summary>A Crypt3's stair slab (its prefab's Cube, on the Rock_4 mesh) stands 1.7 m by its box behind its measured landing,
    /// inside the entrance's clearance: it is the entrance's own stairs (DoorStairs), walked onto from the landing, so the landing
    /// stands for a road and a staircase. Every other stone keeps its clearance (the entrance's on the way in, the road's reach
    /// elsewhere), the door-side stones among them, and off the way in the slab keeps the road's reach too.</summary>
    [Fact]
    public void ACryptsDoorStairSlabIsWalkedOntoAtItsDoorWhileItsOtherRocksKeepTheirClearance()
    {
        PlacedLocationShape shape = Crypt3Stones.Shape(Crypt3Stones.Rocks());
        var road = PlacedLocationShape.Walker.Road(Width);
        var stairs = PlacedLocationShape.Walker.Stairs(RoadStairRuns.FillerReach);
        Vector2 landing = shape.Door!.Value;
        Assert.Equal(2f, shape.DoorClearance);
        LocationRock slab = Assert.Single(shape.DoorStairs);
        Assert.Equal("Cube", slab.Name);
        Assert.True(slab.InTheWay(landing, shape.DoorClearance, Crypt3Stones.Ground(landing.x, landing.y), out _), "the slab is inside the clearance");
        Assert.Equal("", shape.KeepsOff(landing, road));
        Assert.Equal("", shape.KeepsOff(landing, stairs));

        var kept = new HashSet<string>();
        int slabKeptOff = 0;
        for (float x = -12f; x <= 12f; x += 0.25f)
            for (float z = -12f; z <= 12f; z += 0.25f)
            {
                var p = landing + new Vector2(x, z);
                float g = Crypt3Stones.Ground(p.x, p.y);
                string refused = shape.KeepsOff(p, road);
                bool onTheWayIn = shape.InDoorCorridor(p, road.RockMargin);
                float keep = onTheWayIn ? shape.DoorClearance : road.Margin;
                foreach (LocationRock rock in shape.Body)
                {
                    if (rock.Name == "Cube" || !rock.InTheWay(p, keep, g, out _)) continue;
                    Assert.NotEqual("", refused);
                    kept.Add(rock.Name);
                }
                if (!onTheWayIn && slab.InTheWay(p, road.BodyMargin, g, out _))
                {
                    Assert.NotEqual("", refused);
                    if (refused.Contains("mouth's Cube at")) slabKeptOff++;
                }
            }
        Assert.Contains("Stone (19)", kept);
        Assert.Contains("Stone (22)", kept);
        Assert.True(slabKeptOff > 0, "off the way in the slab is kept off");

        // A rock whose box covers the way in but stands over the walker (a cliff over a mouth) is no stair: kept off as before.
        var cliff = new LocationRock(landing - shape.Front * 6f, 8f, 8f, 0f, Crypt3Stones.Root - 5f, Crypt3Stones.Root + 40f, "cliff_mistlands1");
        PlacedLocationShape under = Crypt3Stones.Shape(new List<LocationRock>(Crypt3Stones.Rocks()) { cliff });
        Assert.DoesNotContain(under.DoorStairs, r => r.Name == "cliff_mistlands1");
        Assert.Contains(under.DoorStairs, r => r.Name == "Cube");
    }

    /// <summary>A door-stair rock is walked onto only where it is a step by the one walk rule (StairLayout.Walkable from the way in's
    /// level, the landing's ground): a 0.15 m lip is walked all along the corridor, a 1.5 m stone never is. Every other part of a
    /// door-stair rock is kept off by the walker's own reach (Walker.RockMargin), as a kept rock is, the road's and the staircase's
    /// alike: a lip at the door exempts neither the rock where it rises nor its tall cells beside the way (before, the headroom box
    /// counted a 2 m stone as a step, and beside a door stair only cells over head height were kept off). A rock whose top is not
    /// known is no door stair: its box keeps the entrance's clearance.</summary>
    [Fact]
    public void ADoorStairRockIsWalkedOntoOnlyWhereItIsAStep()
    {
        LocationRock box = Crypt3Stones.Boxes().Single(r => r.Name == "Cube");
        Vector2 landing = Crypt3Stones.Shape(Crypt3Stones.Rocks()).Door!.Value;
        float level = Crypt3Stones.Ground(landing.x, landing.y), stone = 1.5f;
        var road = PlacedLocationShape.Walker.Road(Width);
        var stairs = PlacedLocationShape.Walker.Stairs(RoadStairRuns.FillerReach);
        PlacedLocationShape Only(LocationRock rock) => Crypt3Stones.Shape(new List<LocationRock> { rock });
        IEnumerable<Vector2> Corridor(PlacedLocationShape shape, PlacedLocationShape.Walker walker)
        {
            for (float x = -12f; x <= 12f; x += 0.25f)
                for (float z = -12f; z <= 12f; z += 0.25f)
                {
                    var p = landing + new Vector2(x, z);
                    if (shape.InDoorCorridor(p, walker.RockMargin)) yield return p;
                }
        }

        // A lip a step over the landing: a door stair, walked onto wherever the way in crosses it.
        LocationRock lip = Crypt3Stones.WithTop(box, _ => level + Crypt3Stones.SlabLip);
        PlacedLocationShape lipped = Only(lip);
        Assert.Contains(lipped.DoorStairs, r => r.Name == "Cube");
        int nearTheLip = 0;
        foreach (var walker in new[] { road, stairs })
            foreach (Vector2 p in Corridor(lipped, walker))
            {
                Assert.Equal("", lipped.KeepsOff(p, walker));
                if (lip.InTheWay(p, walker.RockMargin, Crypt3Stones.Ground(p.x, p.y), out _)) nearTheLip++;
            }
        Assert.True(nearTheLip > 0, "the way in comes within its reach of the lip");

        // A 1.5 m stone is no step anywhere: no door stair, kept off by the entrance's clearance, never walked onto.
        LocationRock stoneRock = Crypt3Stones.WithTop(box, _ => level + stone);
        PlacedLocationShape stoned = Only(stoneRock);
        Assert.Empty(stoned.DoorStairs);
        Assert.Contains("mouth's Cube at", stoned.KeepsOff(landing, road));
        foreach (Vector2 p in Corridor(stoned, road))
            Assert.Equal(stoneRock.InTheWay(p, stoned.DoorClearance, Crypt3Stones.Ground(p.x, p.y), out _), stoned.KeepsOff(p, road).Contains("mouth's Cube at"));

        // A lip at the door that rises to a stone further in: a door stair whose stone is kept off by the walker's reach, at the
        // point and beside it, exactly as the stone alone would be (the lip cells left out), and whose lip is still walked onto.
        Func<Vector2, bool> tall = c => Vector2.Distance(c, landing) >= 2.6f;
        PlacedLocationShape rising = Only(Crypt3Stones.WithTop(box, c => tall(c) ? level + stone : level + Crypt3Stones.SlabLip));
        LocationRock tallOnly = Crypt3Stones.WithTop(box, c => tall(c) ? level + stone : float.NaN);
        LocationRock lipOnly = Crypt3Stones.WithTop(box, c => tall(c) ? float.NaN : level + Crypt3Stones.SlabLip);
        Assert.Contains(rising.DoorStairs, r => r.Name == "Cube");
        int keptOff = 0, walked = 0;
        foreach (var walker in new[] { road, stairs })
            foreach (Vector2 p in Corridor(rising, walker))
            {
                bool refused = rising.KeepsOff(p, walker).Contains("mouth's Cube at");
                Assert.Equal(tallOnly.InTheWay(p, walker.RockMargin, Crypt3Stones.Ground(p.x, p.y), out _), refused);
                if (refused) keptOff++;
                else if (lipOnly.InTheWay(p, walker.RockMargin, Crypt3Stones.Ground(p.x, p.y), out _)) walked++;
            }
        Assert.True(keptOff > 0, "where it rises past a step it is kept off");
        Assert.True(walked > 0, "within reach of its lip it is still walked onto");

        // Its top not known, its box is no step: no door stair, the landing inside its clearance refused.
        PlacedLocationShape boxed = Only(box);
        Assert.Empty(boxed.DoorStairs);
        Assert.Contains("mouth's Cube at", boxed.KeepsOff(landing, road));
    }

    /// <summary>A Crypt3 with its prefab's static rocks (its own stones make it entered from its entrance side only, FrontLaneOnly), a
    /// road end in front of it and one at a side door (on the flat's entrance side but beside the door's own line, as measured in
    /// its prefab's frame): each way in ends on the entrance side measured from the door (OutsideItsDoor), inside the door's corridor
    /// (where the door's stair slab is walked onto), level with the landing, and keeps off the crypt from its end past the slab to
    /// the landing. The entrance side and the corridor are two decisions: the end is on the first and lies inside the second.</summary>
    [Fact]
    public void ACryptsWayInFromItsFrontAndFromASideDoorEndsOnItsEntranceSideInsideItsCorridorLevelWithItsLanding()
    {
        PlacedLocationShape shape = Crypt3Stones.Shape(Crypt3Stones.Rocks());
        var road = PlacedLocationShape.Walker.Road(Width);
        Vector2 landing = shape.Door!.Value;
        float level = Crypt3Stones.Ground(landing.x, landing.y);
        Assert.True(shape.FrontLaneOnly, "its own stones make it entered from its entrance side only");
        Assert.Contains(shape.DoorStairs, r => r.Name == "Cube");

        // The side door in its prefab's frame (door behind, facing -z; the flat far from it), turned into the world.
        float r = Crypt3Stones.Yaw * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
        var side = new Vector2(23.6f, 1.34f);
        Vector2 sideDoor = Crypt3Stones.Centre + new Vector2(side.x * c + side.y * s, -side.x * s + side.y * c);
        Vector2 front = landing + shape.Front * 25f;
        Assert.True(shape.InFront(sideDoor), "the side door is on the flat's entrance side");
        Assert.False(shape.OutsideItsDoor(landing + (sideDoor - landing).normalized * shape.DoorClearance), "straight from the side door it ends beside the door");

        foreach (Vector2 from in new[] { front, sideDoor })
        {
            Vector2 end = RoadLocationEntry.WayInEnd(shape, from, road);
            string at = FormattableString.Invariant($"from ({from.x:F1}, {from.y:F1}) the way in ends at ({end.x:F1}, {end.y:F1})");
            Assert.True(shape.OutsideItsDoor(end), at + ", off its entrance side");
            Assert.True(shape.OnTheWayIn(end), at + ", off its way in");
            Assert.True(shape.InDoorCorridor(end, road.RockMargin), at + ", outside its door's corridor");
            Assert.True(Vector2.Distance(end, landing) <= shape.DoorClearance + 0.01f, at + ", past the entrance's clearance");
            Assert.Equal("", shape.KeepsOff(end, road));
            foreach (Vector2 p in Along(new List<Vector2> { end, landing })) Assert.Equal("", shape.KeepsOff(p, road));

            // The road's own way in (RoadLocationEntry.Plan) is planned along a line ending there, at the landing's height.
            List<Vector2>? line = null;
            float height = float.NaN;
            var request = new LocationApproachRequest { RoadEnd = from, RoadHeight = level, Centre = Crypt3Stones.Centre };
            RoadLocationEntry.Plan(request, shape, Width, Crypt3Stones.Ground, out _, planner: (List<Vector2> l, float h, out string why) =>
            {
                line ??= l; if (float.IsNaN(height)) height = h; why = "captured"; return null;
            });
            Assert.True(line != null, at + ": no way in planned");
            Assert.True(Vector2.Distance(line![line.Count - 1], end) < 0.01f, at + FormattableString.Invariant($", planned to ({line[line.Count - 1].x:F1}, {line[line.Count - 1].y:F1})"));
            Assert.True(Mathf.Abs(height - level) <= StairLayout.StepUp, at + FormattableString.Invariant($" at {height:F2}, the landing at {level:F2}"));
        }
    }

    private static IEnumerable<Vector2> Along(List<Vector2> line)
    {
        for (int i = 1; i < line.Count; i++)
        {
            int n = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(line[i - 1], line[i]) * 4f));
            for (int k = 0; k <= n; k++) yield return Vector2.Lerp(line[i - 1], line[i], (float)k / n);
        }
    }
}


/// <summary>A Crypt3 as a test world placed it, with its prefab's own static rocks as measured from the game's prefab.</summary>
internal static class Crypt3Stones
{
    public static readonly Vector2 Centre = new(-2248.16138f, 1151.31116f);
    public const float Yaw = 292.5f, Root = 42.0193367f, RockRadius = 11f;
    /// <summary>Where a way in ended at the crypt's ring, behind it, before going straight in to its landing.</summary>
    public static readonly Vector2 RingEnd = new(-2249.234f, 1163.0133f);

    public static readonly List<LevelOp> Ops = new()
    {
        new(0f, 0f, 0f, 0f, false, 10f, 5f, 0), new(-2.07f, -8.4f, 0f, 4f, false, 6f, 3f, 1), new(2.14f, -8.399994f, 0f, 4f, false, 6f, 3f, 1),
        new(0f, 3.53f, -3f, 3f, false, 5f, 3f, 2), new(0.01f, 5.51f, 0f, 5f, false, 7f, 3f, 1), new(0f, -1.847992f, -3f, 3f, false, 5f, 3f, 3),
    };

    // Each collider: its object, its pose in the prefab's root frame (rows: the images of its x, y and z axes, then its position),
    // its mesh's box (centre, extents). The two meshes: the stones' Rock_3 and the slabs' Rock_4.
    private static readonly (Vector3 c, Vector3 e) Rock3 = (new(-0.0425f, 0.3313f, 0f), new(0.5242f, 0.5211f, 0.4718f));
    private static readonly (Vector3 c, Vector3 e) Rock4 = (new(0.0617f, 0.1865f, 0.0323f), new(0.5725f, 0.3442f, 0.4512f));
    private static readonly (string name, bool slab, float[] m)[] Measured =
    {
        ("Stone (19)", false, new[] { 0.4936f, 0.6022f, 4.8960f, -0.4943f, 4.2963f, -0.4786f, -4.0344f, -0.4132f, 0.4575f, -3.980f, -3.329f, -3.228f }),
        ("Stone (37)", false, new[] { 0.4523f, -0.2967f, -1.3212f, 0.0000f, 4.0782f, -0.9158f, 2.4864f, 0.1820f, 0.8103f, -0.687f, -3.949f, 10.370f }),
        ("Stone (39)", false, new[] { 1.4252f, 0.0000f, 0.0833f, 0.0000f, 4.1797f, 0.0000f, -0.2130f, 0.0000f, 3.6457f, 1.566f, -3.970f, 6.580f }),
        ("Cube (2)", true, new[] { 10.4449f, -0.3267f, 0.0374f, 0.0600f, 1.9570f, 0.3474f, -0.0448f, -0.8705f, 4.9109f, -0.480f, -1.334f, -5.856f }),
        ("Cube (4)", true, new[] { -8.8396f, -0.2463f, -0.2306f, -0.0659f, 1.9831f, 0.4085f, 0.0865f, 0.8795f, -4.2553f, 0.668f, -3.192f, -0.308f }),
        ("Cube (1)", true, new[] { -11.7432f, 0.0000f, -2.0293f, 0.0000f, 2.7784f, 0.0000f, 1.2915f, 0.0000f, -7.4735f, 0.366f, -0.130f, 6.413f }),
        ("Stone (35)", false, new[] { 1.4276f, 0.0000f, 0.0000f, 0.0000f, 4.1797f, 0.0000f, 0f, 0.0000f, 4.0215f, -2.034f, -4.099f, 5.834f }),
        ("Stone (15)", false, new[] { 0.0241f, -0.3084f, 4.9479f, 0.0674f, 4.3421f, 0.2703f, -4.0185f, 0.0609f, 0.0234f, 4.340f, -3.449f, 3.202f }),
        ("Stone (20)", false, new[] { -0.0056f, 0.5476f, -4.9272f, -0.5052f, 4.2951f, 0.4779f, 4.1479f, 0.4825f, 0.0489f, -4.560f, -3.405f, -0.488f }),
        ("Stone (22)", false, new[] { -1.3980f, 0.3953f, -4.7399f, -0.3689f, 4.3100f, 0.4682f, 3.2856f, 0.3831f, -0.9372f, -4.257f, -2.945f, -4.998f }),
        ("Stone (16)", false, new[] { -0.0821f, 0.5587f, 4.9252f, 0.5603f, 4.2883f, -0.4771f, -4.2886f, 0.5455f, -0.1334f, 4.560f, -3.446f, 0.652f }),
        ("Stone (14)", false, new[] { 0.0137f, 0.4265f, -1.9484f, -2.4164f, 3.5381f, 0.7576f, 4.4406f, 2.8906f, 0.6640f, 4.280f, -3.142f, 4.866f }),
        ("Stone (41)", false, new[] { 0.9018f, 0.0000f, -1.1067f, 0.0000f, 4.1797f, 0.0000f, 2.0322f, 0.0000f, 1.6560f, -1.490f, -3.970f, 9.272f }),
        ("Cube", true, new[] { 6.8584f, 0.0000f, 0.0000f, 0.0000f, 1.4716f, 0.3316f, 0.0000f, -0.7318f, 3.2476f, 0.000f, -0.549f, -7.358f }),
        ("Stone (9)", false, new[] { -1.7071f, 0.3501f, -0.9702f, 0.8740f, 4.2623f, 0.0000f, 0.9661f, -0.1981f, -1.7713f, -2.840f, -3.420f, 5.152f }),
        ("Cube (3)", true, new[] { 5.8934f, 0.0000f, 0.0000f, 0.0000f, 2.7877f, 0.0000f, 0f, 0.0000f, 6.5430f, -0.344f, -1.069f, 7.830f }),
        ("Stone (23)", false, new[] { -0.0079f, 0.7738f, -6.9633f, -0.5052f, 4.2951f, 0.4779f, 4.3005f, 0.5002f, 0.0507f, -4.250f, -3.339f, 2.822f }),
        ("Stone (40)", false, new[] { 1.4276f, 0.0000f, 0.0000f, 0.0000f, 4.1797f, 0.0000f, 0f, 0.0000f, 4.1954f, 1.306f, -3.970f, 8.550f }),
        ("Stone (17)", false, new[] { 0.0228f, -0.7119f, -6.2657f, 0.4782f, 4.2972f, -0.4865f, 3.9273f, -0.4299f, 0.0631f, 4.350f, -3.476f, -4.568f }),
        ("Stone (36)", false, new[] { 1.6413f, 0.0000f, 0.0000f, 0.0000f, 4.1797f, 0.0000f, 0f, 0.0000f, 3.9014f, -1.894f, -3.970f, 7.721f }),
        ("Stone (21)", false, new[] { 3.5544f, -0.7370f, 3.3764f, 0.4981f, 4.3024f, 0.4147f, -2.5910f, 0.0363f, 2.7355f, -3.920f, -3.444f, 5.306f }),
        ("Stone (38)", false, new[] { 0.0349f, -0.4795f, -1.3442f, 0f, 3.9368f, -1.4042f, 2.6206f, 0.0216f, 0.0604f, 0.335f, -3.910f, 10.475f }),
        ("Cube (3)", true, new[] { -9.1668f, -0.2921f, -0.1926f, -0.0659f, 2.0236f, 0.0677f, 0.0865f, 0.1482f, -4.3427f, 0.143f, -2.156f, -2.465f }),
        ("Stone (18)", false, new[] { 2.1211f, 0.2938f, 4.4712f, 0.3437f, 4.3143f, -0.4466f, -3.0956f, 0.3959f, 1.4425f, 4.490f, -3.404f, -2.558f }),
    };

    /// <summary>How far over its landing's ground the door's stair slab (its Cube) stands where the game reads its real top: a lip,
    /// within a step (a crypt rooted at 56.56 read its slab's top at 56.25 to 56.72).</summary>
    public const float SlabLip = 0.15f;

    /// <summary>Its stones and slabs as the reader places them (LocationRock.OfPart): the root plus the turn times each pose; the door's
    /// stair slab with its real top as the reader reads it (<see cref="WithTop"/>), <see cref="SlabLip"/> over the ground throughout.</summary>
    public static List<LocationRock> Rocks() =>
        Boxes().Select(r => r.Name == "Cube" ? WithTop(r, _ => Root + SlabLip) : r).ToList();

    /// <summary><paramref name="rock"/> with a real top (LocationRock.Top, a metre grid) of <paramref name="top"/> over each metre
    /// its box covers.</summary>
    public static LocationRock WithTop(LocationRock rock, Func<Vector2, float> top)
    {
        int x0 = Mathf.FloorToInt(rock.At.x) - 8, z0 = Mathf.FloorToInt(rock.At.y) - 8, w = 17;
        var grid = new float[w * w];
        float highest = float.NegativeInfinity;
        for (int k = 0; k < w; k++)
            for (int i = 0; i < w; i++)
            {
                var cell = new Vector2(x0 + i, z0 + k);
                grid[k * w + i] = rock.Distance(cell) > 0.5f ? float.NaN : top(cell);
                if (!float.IsNaN(grid[k * w + i])) highest = Mathf.Max(highest, grid[k * w + i]);
            }
        return new LocationRock(rock.At, rock.HalfX, rock.HalfZ, rock.Yaw, rock.MinY, highest, rock.Name, grid, x0, z0, w);
    }

    /// <summary>Its stones and slabs by their boxes alone, no real top known.</summary>
    public static List<LocationRock> Boxes()
    {
        var rocks = new List<LocationRock>();
        foreach (var (name, slab, m) in Measured)
        {
            var (c, e) = slab ? Rock4 : Rock3;
            var local = new Bounds(c, e * 2f);
            var corners = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                Vector3 k = LocationRock.Corner(local, i);
                corners[i] = new Vector3(k.x * m[0] + k.y * m[3] + k.z * m[6] + m[9], k.x * m[1] + k.y * m[4] + k.z * m[7] + m[10], k.x * m[2] + k.y * m[5] + k.z * m[8] + m[11]);
            }
            rocks.Add(LocationRock.OfPart(new Vector3(Centre.x, Root, Centre.y), Yaw, new Vector3(m[6], m[7], m[8]), corners, name));
        }
        return rocks;
    }

    /// <summary>The ground the crypt levels in front of its door: its root's height.</summary>
    public static float Ground(float x, float z) => Root;

    /// <summary>Its entrance's shape, as a placed-first Location's (PlacedLocations.EntranceShapes).</summary>
    public static PlacedLocationShape Shape(IReadOnlyList<LocationRock> rocks) =>
        PlacedLocationShape.Of(Centre, Yaw, PlacedLocations.FlatFor("Crypt3", Ops)!.Value, RockRadius, rocks, Ground, false, Root, "Crypt3",
            entrance: 0, bodyKnown: true);
}
