using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A road or staircase ends on a placed Location's landing region within its lip, level with it (PlacedLocationShape.Ends, the one
/// owner of where a road ends at a Location, the rule a staircase's top is held to, PlacedLocationShape.OnItsLip): nearest its
/// entrance's measured landing first, else anywhere on that ground nearest the road (its fallback, PlacedLocationShape.LevelledArea);
/// the entrance a price, not a wall (PlacedLocation.EntranceArc).
/// </summary>
public partial class LocationsFirstTests
{
    /// <summary>A road's clearance onto a landing (RoadLocationEntry.Plan's).</summary>
    private static float RoadClearance => RoadLocationEntry.InsideReach(RoadNetworkGenerator.RoadWidth) + RoadLocationEntry.EndSlack;

    /// <summary>Whether <paramref name="p"/> is on <paramref name="shape"/>'s lip for a road (its region for a road's clearance, within a
    /// metre: a road's own points are not its lattice).</summary>
    private static bool OnTheLip(PlacedLocationShape shape, Vector2 p)
    {
        PlacedLocationShape.LandingRegion region = shape.RegionFor(shape.Ground!, RoadClearance);
        if (PlacedLocationShape.OnItsLip(region, p)) return true;
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
                if (PlacedLocationShape.OnItsLip(region, new Vector2(Mathf.Round(p.x) + dx, Mathf.Round(p.y) + dz))) return true;
        return false;
    }

    /// <summary>A SunkenCrypt4 as read in game at (1412.1, 2895.7): its entrance turned toward (1406.7, 2890.4), where a player walked
    /// onto its landing from the road at (1432.3, 2882.5), 0.8 m below over 25 m; it levels its root + 0.5 m 9 m round a point 1.61 m
    /// behind its centre (the door's landing, 8.5 m out, on the lip past it); its two ground torches up on the porch masonry 1.45 m over
    /// that ground, 3.94 m out on the door side, 2.5 m off its axis. <paramref name="blocked"/>: a built wall across its whole front.</summary>
    private static readonly Vector2 CryptAt = new(1412.1f, 2895.7f), WalkedFrom = new(1432.3f, 2882.5f), WalkedTo = new(1406.7f, 2890.4f);
    private const float CryptRoot = 31.06f, CryptYaw = 45.5f;
    private static readonly LevelOp CryptFlat = new(0f, 1.61f, 0.5f, 9f, false, 12f, 3f);

    private static float CryptNatural(float x, float z) => CryptRoot - 0.03f * Mathf.Max(0f, Vector2.Distance(new Vector2(x, z), CryptAt) - 6f);

    private PlacedLocation LevelledCrypt(bool blocked)
    {
        const string crypt = "SunkenCrypt4";
        var world = new MoundWorld { Biome = Heightmap.Biome.Swamp, Ground = CryptNatural };
        _scope.WithWorld(world);
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        StairStructures.Reset();
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(CryptAt, Footprint) });
        PlacedLocations.Reset();
        var ops = new List<LevelOp> { CryptFlat };
        var record = new PlacedLocationRecord
        {
            Prefab = crypt, Centre = CryptAt, Yaw = CryptYaw, Root = CryptRoot, RockRadius = 0f, Ops = ops,
            X0 = Mathf.FloorToInt(CryptAt.x) - 60, Z0 = Mathf.FloorToInt(CryptAt.y) - 60, W = 121, H = 121,
        };
        Func<float, float, float> ground = PlacedLocationGround.Sampler(record.Ground((x, z) => CryptNatural(x, z)), record.X0, record.Z0, record.W, record.H);
        float porch = CryptRoot + CryptFlat.Height + 1.45f;
        var pieces = new List<LocationRock>
        {
            new(CryptAt + LocationRock.World(new Vector2(2.5f, -3.94f), CryptYaw), 0.2f, 0.2f, CryptYaw, porch, porch + 1f, "CastleKit_groundtorch_green", built: true),
            new(CryptAt + LocationRock.World(new Vector2(-2.5f, -3.94f), CryptYaw), 0.2f, 0.2f, CryptYaw, porch, porch + 1f, "CastleKit_groundtorch_green", built: true),
        };
        // Across its whole front, from its porch out past its landing: no end on its entrance side keeps off it.
        if (blocked) pieces.Add(new LocationRock(CryptAt + LocationRock.World(new Vector2(0f, -9f), CryptYaw), 9f, 4f, CryptYaw, CryptRoot - 1f, CryptRoot + 4f, "stone_wall_4x2", built: true));
        record.Rocks = pieces;
        PlacedLocationShape entrance = PlacedLocationShape.Of(CryptAt, CryptYaw, CryptFlat, 0f, pieces, ground, false, CryptRoot, crypt, entrance: 0, bodyKnown: true);
        PlacedLocationShape area = PlacedLocationShape.Of(CryptAt, CryptYaw, CryptFlat, 0f, pieces, ground, false, CryptRoot, crypt, bodyKnown: true, levelledArea: true);
        var location = new PlacedLocation { Record = record, Ground = ground, Shape = area, Entrances = new[] { entrance }, Fallback = area };
        PlacedLocations.Ready(location);
        LocationLevelling.Source = c => Vector2.Distance(c, CryptAt) < 0.5f ? ops : null;
        SetPathfinder(new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f });
        return location;
    }

    /// <summary>A road on the natural ground from 40 m further out to <paramref name="end"/>, its waypoints a cell apart.</summary>
    private static (List<Vector2> path, List<RoadSpatialGrid.PlannedPath> plans) RoadTo(Vector2 end, Vector2 from)
    {
        Vector2 dir = (end - from).normalized;
        int cells = Mathf.RoundToInt(Vector2.Distance(from, end) / RoadPathfinder.CellSize);
        var path = Enumerable.Range(0, cells + 1).Select(k => Vector2.Lerp(from, end, k / (float)cells)).ToList();
        int metres = Mathf.RoundToInt(Vector2.Distance(from, end));
        var points = Enumerable.Range(0, metres + 1).Select(k => Vector2.Lerp(from, end, k / (float)metres)).ToList();
        var piece = new RoadSpatialGrid.PlannedPath(points, points.ConvertAll(p => CryptNatural(p.x, p.y)), points.ConvertAll(_ => default(RoadPointDebugInfo)),
            RoadNetworkGenerator.RoadWidth, metres, false);
        return (path, new List<RoadSpatialGrid.PlannedPath> { piece });
    }

    /// <summary>The crypt's door side open, the road arriving along the line a player walked onto its landing: its way in is the
    /// road's own last stretch close to that line, ending on its landing region's lip (where the crypt's levelled ground meets the
    /// slope, as a staircase's top is held) within the network's reach of its entrance's landing, level with it; the torches up on the porch
    /// refuse nothing (their ground moves less than a step), and the crypt is reached at its entrance.</summary>
    [Fact]
    public void ACryptsRoadEndsOnItsLevelledGroundLevelWithItWhenItsDoorSideIsOpen()
    {
        PlacedLocation crypt = LevelledCrypt(blocked: false);
        PlacedLocationShape door = crypt.Entrances[0];
        var (path, plans) = RoadTo(WalkedFrom, WalkedFrom + (WalkedFrom - WalkedTo).normalized * 40f);

        bool entered = RoadNetworkGenerator.PlanEntry(crypt, false, path, plans, new List<RoadStairSpan>(), new List<RoadCrossing>(), RoadNetworkGenerator.RoadWidth,
            out RoadSpatialGrid.PlannedPath? road, out _, out string why, out EntranceArrivals.Arrival? arrival);

        Assert.True(entered && road != null, why);
        Assert.True(arrival != null && arrival.Target.Entrance == 0, $"arrived at {(arrival == null ? "none" : EntranceArrivals.Name(arrival.Target))}");
        Vector2 last = road!.Points[road.Points.Count - 1];
        float level = crypt.Ground(last.x, last.y);
        Assert.True(OnTheLip(door, last), FormattableString.Invariant($"the way in ends at ({last.x:F1}, {last.y:F1}), off its landing's lip"));
        Assert.True(StairLayout.Walkable(road.Heights[road.Heights.Count - 1], level),
            FormattableString.Invariant($"the way in ends at {road.Heights[road.Heights.Count - 1]:F2}, the ground there at {level:F2}"));
        Assert.True(Vector2.Distance(last, door.Landing) <= RoadNetworkGenerator.LandingReach);
        // Aimed toward the entrance, stopped as soon as it is on the landing: on the walker's side of it, short of the door.
        Assert.True(Vector2.Distance(last, WalkedFrom) < Vector2.Distance(door.Landing, WalkedFrom),
            FormattableString.Invariant($"the way in ends at ({last.x:F1}, {last.y:F1}), past the door from the walked way"));
        // Straight from where the walk set off to where it stops on the rim (no turn back, no zig-zag): every point of the way in
        // before its last LandingReach within a road's width of that line.
        foreach (Vector2 p in road.Points)
        {
            if (Vector2.Distance(p, last) <= RoadNetworkGenerator.LandingReach) continue;
            Vector2 ab = last - WalkedFrom;
            float t = Mathf.Clamp01(Vector2.Dot(p - WalkedFrom, ab) / ab.sqrMagnitude);
            float off = Vector2.Distance(p, WalkedFrom + ab * t);
            Assert.True(off <= RoadNetworkGenerator.RoadWidth, FormattableString.Invariant($"the way in at ({p.x:F1}, {p.y:F1}) is {off:F1} m off the straight way to its end"));
        }
        Assert.Null(RoadNetworkGenerator.UnderALocationsPieces(road, out _));
    }

    /// <summary>The same crypt with its door side blocked by a wall: no end on its entrance's side keeps off it, so the road ends on its
    /// levelled ground on another side, level with it (its fallback, the levelled ground as a whole): the entrance is a preference.</summary>
    [Fact]
    public void WithItsDoorSideBlockedACryptsRoadEndsLevelOnAnotherSide()
    {
        PlacedLocation crypt = LevelledCrypt(blocked: true);
        Vector2 side = CryptAt + LocationRock.World(new Vector2(30f, 3f), CryptYaw);
        var (path, plans) = RoadTo(side, side + LocationRock.World(new Vector2(40f, 0f), CryptYaw));

        bool entered = RoadNetworkGenerator.PlanEntry(crypt, false, path, plans, new List<RoadStairSpan>(), new List<RoadCrossing>(), RoadNetworkGenerator.RoadWidth,
            out RoadSpatialGrid.PlannedPath? road, out _, out string why, out EntranceArrivals.Arrival? arrival);

        Assert.True(entered && road != null, why);
        Assert.True(arrival != null && arrival.Target.LevelledArea, $"arrived at {(arrival == null ? "none" : EntranceArrivals.Name(arrival.Target))}");
        Vector2 last = road!.Points[road.Points.Count - 1];
        Assert.True(OnTheLip(arrival!.Target, last), FormattableString.Invariant($"the way in ends at ({last.x:F1}, {last.y:F1}), off its landing's lip"));
        Assert.True(StairLayout.Walkable(road.Heights[road.Heights.Count - 1], crypt.Ground(last.x, last.y)));
        Assert.True(Vector2.Distance(last, crypt.Entrances[0].Landing) > RoadNetworkGenerator.LandingReach,
            FormattableString.Invariant($"it ended by the blocked door at ({last.x:F1}, {last.y:F1})"));
        Assert.True(RoadNetworkGenerator.ReachedTarget(crypt) == null || RoadNetworkGenerator.ReachedTarget(crypt)!.LevelledArea);
    }

    /// <summary>A Mountain Location levelling a flat 8 m round its centre on a slope rising 1 in 3: the road comes up from below,
    /// and its end on its landing's lip is where that flat meets the slope on the road's side, level with the flat.</summary>
    [Fact]
    public void AMountainLocationOnASlopeIsEndedOnWhereItsFlatMeetsTheSlope()
    {
        var flat = new LevelOp(0f, 0f, 0f, 8f, false, 12f, 3f);
        var ops = new List<LevelOp> { flat };
        const float root = 100f;
        var record = new PlacedLocationRecord { Prefab = "StoneTowerRuins04", Centre = new Vector2(0f, 0f), Yaw = 0f, Root = root, Ops = ops, X0 = -40, Z0 = -40, W = 81, H = 81 };
        Func<float, float, float> ground = PlacedLocationGround.Sampler(record.Ground((x, z) => root + z / 3f), record.X0, record.Z0, record.W, record.H);
        PlacedLocationShape area = PlacedLocationShape.Of(record.Centre, 0f, flat, 0f, null, ground, false, root, record.Prefab, bodyKnown: true, levelledArea: true);
        var below = new Vector2(3f, -30f);

        var ends = area.Ends(null, below, RoadClearance, 1);

        Assert.NotEmpty(ends);
        Vector2 end = ends[0].at;
        Assert.True(OnTheLip(area, end));
        Assert.True(Mathf.Abs(ends[0].height - root) <= StairLayout.StairFitsFrom, FormattableString.Invariant($"the end at {ends[0].height:F2}, the flat at {root:F2}"));
        Assert.True(end.y < 0f && end.magnitude >= flat.Radius - 2f, FormattableString.Invariant($"the end ({end.x:F1}, {end.y:F1}) is not where the flat meets the slope below"));
    }

    /// <summary>A Mistlands guard tower whose own landing is on its own floor (Mistlands_GuardTower1_new at (4224.1, -6916.1): aimed at
    /// (4230.6, -6913.4) on its blackmarble_floor_large, refused "too near its built pieces", the road kept at its ring): its levelled
    /// ground as a whole is ended on at the point nearest the road that keeps off its pieces, beside the floor, never on it.</summary>
    [Fact]
    public void ATowerWhoseLandingIsOnItsFloorIsEndedOnItsLevelledGroundBesideIt()
    {
        var centre = new Vector2(4224.1f, -6916.1f);
        var flat = new LevelOp(0f, 0f, 0f, 12f, false, 16f, 3f);
        var ops = new List<LevelOp> { flat };
        const float root = 61.27f;
        var record = new PlacedLocationRecord { Prefab = "Mistlands_GuardTower1_new", Centre = centre, Yaw = 0f, Root = root, Ops = ops,
            X0 = Mathf.FloorToInt(centre.x) - 40, Z0 = Mathf.FloorToInt(centre.y) - 40, W = 81, H = 81 };
        Func<float, float, float> ground = PlacedLocationGround.Sampler(record.Ground((_, _) => root - 2f), record.X0, record.Z0, record.W, record.H);
        var floor = new LocationRock(new Vector2(4230.6f, -6913.4f), 4f, 4f, 0f, root, root + 0.5f, "blackmarble_floor_large", built: true);
        var pieces = new List<LocationRock> { floor };
        PlacedLocationShape area = PlacedLocationShape.Of(centre, 0f, flat, 0f, pieces, ground, false, root, record.Prefab, bodyKnown: true, levelledArea: true);
        var walker = PlacedLocationShape.Walker.Road(RoadNetworkGenerator.RoadWidth);
        var ringEnd = new Vector2(4234.6f, -6889.5f);

        var ends = area.Ends(null, ringEnd, walker.Margin + RoadLocationEntry.EndSlack, 1, walker.BodyMargin);

        Assert.NotEmpty(ends);
        Vector2 end = ends[0].at;
        Assert.Equal("", area.KeepsOff(end, walker));
        Assert.True(floor.Distance(end) >= walker.BodyMargin, FormattableString.Invariant($"the end ({end.x:F1}, {end.y:F1}) is on or by the floor"));
        Assert.True(OnTheLip(area, end));
        Assert.True(Vector2.Distance(end, ringEnd) < Vector2.Distance(centre, ringEnd), "the end is not on the road's side");
    }

    /// <summary>One lip for a road's end and a staircase's top on the same Location: the ends a road's way in is laid to
    /// (PlacedLocationShape.Ends) and the aims a staircase onto the same landing climbs to (PlacedLocationShape.Aims, AtLip) both
    /// stand on the one landing region's lip (PlacedLocationShape.OnItsLip), the region read once for the clearance asked.</summary>
    [Fact]
    public void ARoadsEndAndAStaircasesTopOnOneLocationAreHeldToTheOneLip()
    {
        var flat = new LevelOp(0f, 0f, 0f, 8f, false, 12f, 3f);
        var ops = new List<LevelOp> { flat };
        const float root = 100f;
        var record = new PlacedLocationRecord { Prefab = "StoneTowerRuins04", Centre = new Vector2(0f, 0f), Yaw = 0f, Root = root, Ops = ops, X0 = -40, Z0 = -40, W = 81, H = 81 };
        Func<float, float, float> ground = PlacedLocationGround.Sampler(record.Ground((x, z) => root + z / 3f), record.X0, record.Z0, record.W, record.H);
        PlacedLocationShape area = PlacedLocationShape.Of(record.Centre, 0f, flat, 0f, null, ground, false, root, record.Prefab, bodyKnown: true, levelledArea: true);
        PlacedLocationShape.LandingRegion region = area.RegionFor(ground, RoadClearance);
        Assert.NotEmpty(region.Lip);
        foreach (Vector2 from in new[] { new Vector2(3f, -30f), new Vector2(30f, 0f), new Vector2(-20f, 25f) })
        {
            foreach (var (at, _) in area.Ends(ground, from, RoadClearance, 4))
                Assert.True(PlacedLocationShape.OnItsLip(region, at), FormattableString.Invariant($"a road's end ({at.x:F1}, {at.y:F1}) off the lip"));
            foreach (var (name, at) in area.Aims(ground, from, RoadClearance, 4, region))
                Assert.True(PlacedLocationShape.OnItsLip(region, at) || !region.Contains(at), FormattableString.Invariant($"a staircase's {name} ({at.x:F1}, {at.y:F1}) past the lip"));
        }
    }

    /// <summary>An entrance whose landing is within a boulder's box (a Dvergr town entrance's landing 0.5 m off its cliff_mistlands1's
    /// box, Run61p): its landing region seeds nothing, so its way in ends at the nearest point on its entrance side clear of the
    /// boulder and level with the landing, within the network's reach of it; never on the landing the boulder refuses.</summary>
    [Fact]
    public void AnEntranceWhoseLandingABoulderCrowdsEndsOnItsEntranceSideClearOfIt()
    {
        const string tower = "Mistlands_GuardTower1_new";
        Func<float, float, float> ground = (_, _) => 50f;
        Vector2 landing = PlacedLocationShape.Of(new Vector2(0f, 0f), 0f, PlacedLocationShape.DoorFlat, 0f, null, ground, false, 50f, tower, entrance: 0).Landing;
        var cliff = new LocationRock(landing + new Vector2(3f, -2f), 2.5f, 3f, 0f, 49f, 60f, "cliff_mistlands1");
        PlacedLocationShape door = PlacedLocationShape.Of(new Vector2(0f, 0f), 0f, PlacedLocationShape.DoorFlat, 30f, new List<LocationRock> { cliff }, ground, false, 50f,
            tower, entrance: 0, bodyKnown: true);
        Assert.Single(door.Body);
        Assert.NotEqual("", door.Refusal(landing, RoadClearance, RoadClearance));

        var ends = door.Ends(null, landing + door.Front * 30f, RoadClearance, 4);

        Vector2 end = Assert.Single(ends).at;
        Assert.Equal("", door.Refusal(end, RoadClearance, RoadClearance));
        Assert.True(Vector2.Distance(end, landing) is > 0.5f and <= RoadNetworkGenerator.LandingReach, FormattableString.Invariant($"the end ({end.x:F1}, {end.y:F1})"));
        Assert.True(door.OutsideItsDoor(end), "not on the entrance side of its landing");
    }
}
