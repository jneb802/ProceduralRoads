using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// The road carried on onto a placed cave's landing where it can get there, stairs only
/// where it cannot, and both kept off the cave's own boulders.
/// </summary>
public class LocationEntryRoadTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    /// <summary>A gentle hillside falling 0.15 to +x, a cave at the origin facing down it.</summary>
    private sealed class GentleWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) > 300f || Mathf.Abs(wy) > 300f ? 0f : 50f - 0.15f * wx;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    /// <summary>The steep hillside of <see cref="LocationApproachStairTests"/>: 0.8 near the cave.</summary>
    private sealed class SteepWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) =>
            Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f ? 0f : 50f - 0.8f * Mathf.Clamp(wx, -12f, 14f) - 0.3f * Mathf.Max(wx - 14f, 0f);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    private sealed class FlatWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => 30f;
    }

    /// <summary>MountainCave02's floor, as in <see cref="LocationApproachStairTests"/>.</summary>
    private static readonly IReadOnlyList<LevelOp> CaveFloor = new[] { new LevelOp(-0.06f, -4.75f, -4.09f, 4f, false, 7f, 3f) };
    private const float Footprint = 20f, Width = 4f;

    public LocationEntryRoadTests()
    {
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), Footprint) });
        // A cave in the Mountains is placed first (PlacedLocations.NeedsPlacing): on the preload list, so its road may enter it.
        PlacedLocations.Preload(new Vector2(0f, 0f));
    }

    public void Dispose()
    {
        // The locations' grounds these tests publish (PlacedLocations.Preload) are not another class's.
        PlacedLocations.Reset();
        RoadTerrainModifier.ResetForWorld();
        RoadLocationProtection.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    /// <summary>The ground a cave turned to face +x leaves on <paramref name="world"/>.</summary>
    private static Func<float, float, float> CaveGround(WorldGenerator world) => (x, z) =>
    {
        float h = world.GetHeight(x, z);
        LevelOp op = CaveFloor[0];
        Vector2 floor = LocationLevelling.OffsetAt(op, 90f);
        float d = Vector2.Distance(new Vector2(x, z), floor);
        float level = world.GetHeight(0f, 0f) + op.Height;
        if (d <= op.Radius) h = level;
        if (d <= op.SmoothRadius) h = Mathf.Lerp(h, level, 1f - Mathf.Pow(d / op.SmoothRadius, op.SmoothPower));
        return h;
    };

    private static LocationApproachRequest Request(Vector2 end, Func<float, float, float> ground)
    {
        Vector2 before = end + end.normalized * 30f;
        return new LocationApproachRequest
        {
            RoadEnd = end, RoadHeight = ground(end.x, end.y), Arriving = StairLayout.Bearing(before, end),
            Centre = new Vector2(0f, 0f), RoadTail = new List<Vector2> { before, end },
        };
    }

    private static PlacedLocationShape Shape(float rock = 0f, IReadOnlyList<LocationRock>? rocks = null, Func<float, float, float>? ground = null) =>
        PlacedLocationShape.Of(new Vector2(0f, 0f), 90f, CaveFloor[0], rock, rocks, ground);

    [Fact]
    public void WhereTheGroundAllowsTheRoadItselfGoesOntoTheLanding()
    {
        var world = new GentleWorld();
        _scope.WithWorld(world);
        var ground = CaveGround(world);
        PlacedLocationShape shape = Shape();

        RoadSpatialGrid.PlannedPath? road = RoadLocationEntry.Plan(Request(new Vector2(26f, 0f), ground), shape, Width, ground, world, out string why);

        Assert.True(road != null, why);
        Vector2 last = road!.Points[road.Points.Count - 1];
        Assert.InRange(Vector2.Distance(last, shape.LandingFor(RoadLocationEntry.InsideReach(Width) + RoadLocationEntry.EndSlack)), 0f, 1f);
        Assert.InRange(road.Heights[road.Heights.Count - 1], ground(last.x, last.y) - 0.5f, ground(last.x, last.y) + 0.5f);
        // It reaches no nearer the entrance pit than its earthwork reaches inside a footprint.
        float reach = RoadLocationEntry.InsideReach(Width);
        Assert.All(road.Points, p => Assert.True(Vector2.Distance(p, shape.FlatCentre) >= shape.Pit + reach - 0.01f, $"{p} in the entrance"));
        Assert.True(road.Points.Any(p => p.magnitude < Footprint), "it never entered the footprint");
    }

    [Fact]
    public void WhereItIsTooSteepForRoadThereIsNoRoad()
    {
        var world = new SteepWorld();
        _scope.WithWorld(world);
        var ground = CaveGround(world);

        Assert.Null(RoadLocationEntry.Plan(Request(new Vector2(24f, 0f), ground), Shape(), Width, ground, world, out string why));
        Assert.StartsWith("road: ", why);
    }

    [Fact]
    public void TheRoadKeepsOffTheLocationsOwnBoulders()
    {
        var world = new GentleWorld();
        _scope.WithWorld(world);
        var ground = CaveGround(world);
        // A boulder of the cave's beside the straight line in.
        var rocks = new[] { new LocationRock(new Vector2(14f, 2f), 2f) };

        Assert.Null(RoadLocationEntry.Plan(Request(new Vector2(26f, 0f), ground), Shape(0f, rocks), Width, ground, world, out string why));
        Assert.StartsWith("road: ", why);
        Assert.Contains("on its rock at (14.0, 2.0)", why);
    }

    [Fact]
    public void FromBehindTheCaveTheRoadGoesRoundItsRockToTheFront()
    {
        var world = new GentleWorld();
        _scope.WithWorld(world);
        var ground = CaveGround(world);
        const float rock = 7f;
        PlacedLocationShape shape = Shape(rock);
        // The road ends behind the cave (it faces +x), off to one side.
        LocationApproachRequest request = Request(new Vector2(-24f, 12f), ground);

        RoadSpatialGrid.PlannedPath? road = RoadLocationEntry.Plan(request, shape, Width, ground, world, out string why);

        Assert.True(road != null, why);
        float reach = RoadLocationEntry.InsideReach(Width);
        Assert.All(road!.Points, p => Assert.True(shape.InFront(p) || Vector2.Distance(p, shape.Centre) >= shape.KeepOut + reach - 0.01f,
            $"{p} in the cave's rock"));
        // Straight in, it would have cut through the hill behind the mouth.
        Vector2 target = shape.LandingFor(reach + RoadLocationEntry.EndSlack);
        Assert.Contains(Enumerable.Range(0, 21), k => shape.Refusal(Vector2.Lerp(request.RoadEnd, target, k / 20f), reach, reach) != "");
    }

    [Fact]
    public void TheApproachAimsAtTheLevelLedgeBesideTheMouthAndArrivesLevelWithIt()
    {
        var world = new GentleWorld();
        _scope.WithWorld(world);
        // A level ledge beside the mouth, as at a frost cave: the
        // hillside falls 0.3 to +x (the slope in front of the mouth is not level), the cave's
        // floor dug in as MountainCave02's, and a 6 by 6 m ledge at 47.3 off to its side.
        LevelOp op = CaveFloor[0];
        Vector2 floor = LocationLevelling.OffsetAt(op, 90f);
        float Ground(float x, float z)
        {
            if (x >= 6f && x <= 12f && z >= 5f && z <= 11f) return 47.3f;
            float h = 50f - 0.3f * x, level = 50f + op.Height;
            float d = Vector2.Distance(new Vector2(x, z), floor);
            if (d <= op.Radius) return level;
            return d <= op.SmoothRadius ? Mathf.Lerp(h, level, 1f - Mathf.Pow(d / op.SmoothRadius, op.SmoothPower)) : h;
        }
        PlacedLocationShape shape = Shape(0f, null, Ground);

        var landings = shape.Landings(Ground, 1f);

        Assert.NotEmpty(landings);
        Assert.InRange(landings[0].at.x, 6f, 12f);
        Assert.InRange(landings[0].at.y, 5f, 11f);
        Assert.Equal(47.3f, landings[0].height, 2);

        // The road comes up to the ledge's height, on the ledge.
        RoadSpatialGrid.PlannedPath? road = RoadLocationEntry.Plan(Request(new Vector2(24f, 8f), Ground), shape, Width, Ground, world, out string why);
        Assert.True(road != null, why);
        Vector2 end = road!.Points[road.Points.Count - 1];
        Assert.InRange(end.x, 6f - 0.5f, 12f + 0.5f);
        Assert.InRange(end.y, 5f - 0.5f, 11f + 0.5f);
        Assert.Equal(47.3f, road.Heights[road.Heights.Count - 1], 1);
    }

    [Fact]
    public void ABoulderIsItsTurnedBoxNotACircleRoundIt()
    {
        // 20 m long and 4 m wide in its own frame, turned 90 degrees: its length, local x, lies along -z.
        var rock = new LocationRock(new Vector2(0f, 0f), 10f, 2f, 90f, float.NegativeInfinity, float.PositiveInfinity);
        Assert.Equal(0f, rock.Distance(new Vector2(0f, -9f)), 3);
        Assert.Equal(0f, rock.Distance(new Vector2(1.5f, 9f)), 3);
        Assert.Equal(3f, rock.Distance(new Vector2(5f, 0f)), 3);
        Assert.Equal(1f, rock.Distance(new Vector2(0f, 11f)), 3);
        // Local and World undo each other, the way Unity turns.
        Vector2 w = LocationRock.World(new Vector2(1f, 0f), 90f);
        Assert.Equal(0f, w.x, 3);
        Assert.Equal(-1f, w.y, 3);
        Vector2 back = LocationRock.Local(w, 90f);
        Assert.Equal(1f, back.x, 3);
        Assert.Equal(0f, back.y, 3);
    }

    [Fact]
    public void AMouthBoulderWithItsRealTopKnownKeepsTheRoadsSurfaceNotItsEarthworkOff()
    {
        var world = new GentleWorld();
        _scope.WithWorld(world);
        var ground = CaveGround(world);
        const float rock = 7f;
        PlacedLocationShape plain = Shape(rock);
        float reach = RoadLocationEntry.InsideReach(Width);
        Vector2 target = plain.LandingFor(reach + RoadLocationEntry.EndSlack);
        float g = ground(target.x, target.y);
        // A 2 m boulder beside the mouth, its near side 3 m off the road's end.
        Vector2 at = target + new Vector2(-2f, 4.5f);
        var top = new float[9];
        for (int i = 0; i < 9; i++) top[i] = g + 2f;
        var known = new LocationRock(at, 1f, 1f, 0f, g - 1f, g + 2f, "Rock_4", top, Mathf.RoundToInt(at.x) - 1, Mathf.RoundToInt(at.y) - 1, 3);
        var boxed = new LocationRock(at, 1f, 1f, 0f, g - 1f, g + 2f, "Rock_4");

        Assert.NotNull(RoadLocationEntry.Plan(Request(new Vector2(26f, 0f), ground), Shape(rock, new[] { known }, ground), Width, ground, world, out string why));
        Assert.Null(RoadLocationEntry.Plan(Request(new Vector2(26f, 0f), ground), Shape(rock, new[] { boxed }, ground), Width, ground, world, out why));
        Assert.Contains("at its mouth's Rock_4", why);
    }

    [Fact]
    public void ARocksRealTopDecidesWhereItIsInTheWay()
    {
        // A 20 by 20 m box from -10..10; the rock itself stands only in x 4..10, its top 52 m
        // at x >= 7 and 49 m (under the 50 m ground) at x 4..6.
        var top = new float[21 * 21];
        for (int k = 0; k <= 20; k++)
            for (int i = 0; i <= 20; i++)
            {
                int x = i - 10;
                top[k * 21 + i] = x >= 7 ? 52f : x >= 4 ? 49f : float.NaN;
            }
        var rock = new LocationRock(new Vector2(0f, 0f), 10f, 10f, 0f, 40f, 52f, "rock1_mountain", top, -10, -10, 21);

        // Inside its box, but where nothing of it stands: not in the way (its box said it was).
        Assert.False(rock.InTheWay(new Vector2(-5f, 0f), 2f, 50f, out _));
        Assert.True(new LocationRock(new Vector2(0f, 0f), 10f, 10f, 0f, 40f, 52f).InTheWay(new Vector2(-5f, 0f), 2f, 50f, out _));
        // Beside the part buried under the ground: not in the way either.
        Assert.False(rock.InTheWay(new Vector2(2f, 0f), 1.5f, 50f, out _));
        // Beside the part standing above the ground: in the way, and how near is said.
        Assert.True(rock.InTheWay(new Vector2(5.5f, 0f), 1.5f, 50f, out float d));
        Assert.InRange(d, 0f, 1.5f);

        // In the way only where it reaches into a walker's box there (RockClearances.Bottom to Top over the ground): a rock
        // wholly over the box, or wholly under the walk, is not.
        float ground = 50f, bottom = RockClearances.Bottom(ground), over = RockClearances.Top(ground);
        Assert.True(new LocationRock(new Vector2(0f, 0f), 2f, 2f, 0f, over - 0.05f, over + 5f).InTheWay(new Vector2(0f, 0f), 1f, ground, out _));
        Assert.False(new LocationRock(new Vector2(0f, 0f), 2f, 2f, 0f, over + 0.05f, over + 5f).InTheWay(new Vector2(0f, 0f), 1f, ground, out _));
        Assert.True(new LocationRock(new Vector2(0f, 0f), 2f, 2f, 0f, ground - 5f, bottom + 0.05f).InTheWay(new Vector2(0f, 0f), 1f, ground, out _));
        Assert.False(new LocationRock(new Vector2(0f, 0f), 2f, 2f, 0f, ground - 5f, bottom - 0.05f).InTheWay(new Vector2(0f, 0f), 1f, ground, out _));
    }

    [Fact]
    public void TheCavesOwnRockBodyIsKeptOutByItsRingNotAsABoulder()
    {
        var world = new GentleWorld();
        _scope.WithWorld(world);
        var ground = CaveGround(world);
        const float rock = 7f;
        // The cave's body: a boulder at its centre, wider than the way to its mouth.
        var body = new LocationRock(new Vector2(0.9f, -0.2f), 15f, "rock2_mountain");
        PlacedLocationShape shape = Shape(rock, new[] { body });

        Assert.Empty(shape.Rocks);
        Assert.Empty(shape.Body);
        Assert.NotNull(RoadLocationEntry.Plan(Request(new Vector2(26f, 0f), ground), shape, Width, ground, world, out string why));
        Assert.True(why == "", why);
        // One scattered outside its exterior radius is still kept clear.
        Assert.Single(Shape(rock, new[] { body, new LocationRock(new Vector2(12f, 9f), 2f) }).Rocks);
    }

    [Fact]
    public void TheBouldersHuggingTheMouthAreNotTouchedEvenSmallOnes()
    {
        var world = new GentleWorld();
        _scope.WithWorld(world);
        var ground = CaveGround(world);
        const float rock = 7f;
        PlacedLocationShape plain = Shape(rock);
        float reach = RoadLocationEntry.InsideReach(Width);
        Vector2 target = plain.LandingFor(reach + RoadLocationEntry.EndSlack);
        // A small boulder beside the mouth, inside the exterior radius, at the walking level,
        // its edge 1.8 m off the road's end: a road there would dig to it.
        var flank = new LocationRock(target + new Vector2(-2f, 2.5f), 1f, 1f, 0f, ground(target.x, target.y) - 1f, ground(target.x, target.y) + 2f, "Rock_4");
        PlacedLocationShape shape = Shape(rock, new[] { flank }, ground);

        Assert.Single(shape.Body);
        Assert.Null(RoadLocationEntry.Plan(Request(new Vector2(26f, 0f), ground), shape, Width, ground, world, out string why));
        Assert.StartsWith("road: ", why);
        Assert.Contains("at its mouth's Rock_4", why);
    }

    [Fact]
    public void ASmallBoulderIsClearedNotAvoidedButAtHildirsCave()
    {
        var world = new GentleWorld();
        _scope.WithWorld(world);
        var ground = CaveGround(world);
        // A small boulder of the cave's on the straight line in.
        var small = new[] { new LocationRock(new Vector2(14f, 0.5f), 1f) };

        Assert.NotNull(RoadLocationEntry.Plan(Request(new Vector2(26f, 0f), ground), Shape(0f, small), Width, ground, world, out string why));
        Assert.Empty(Shape(0f, small).Rocks);
        // At a location that keeps every rock it is kept clear.
        PlacedLocationShape hildir = PlacedLocationShape.Of(new Vector2(0f, 0f), 90f, CaveFloor[0], 0f, small, ground, keepSmallRocks: true);
        Assert.Single(hildir.Rocks);
        Assert.Null(RoadLocationEntry.Plan(Request(new Vector2(26f, 0f), ground), hildir, Width, ground, world, out why));
        Assert.StartsWith("road: ", why);
        Assert.Contains("on its rock", why);
        Assert.True(LocationRock.KeepsEveryRock("Hildir_cave"));
        Assert.False(LocationRock.KeepsEveryRock("MountainCave02"));
    }

    [Fact]
    public void ALargeBoulderBelowTheWalkingLevelIsNotInTheWay()
    {
        var world = new GentleWorld();
        _scope.WithWorld(world);
        var ground = CaveGround(world);
        // A big boulder down the slope: seen from above it covers the way in, but its top is
        // 10 m under the ground there.
        var below = new[] { new LocationRock(new Vector2(14f, 0f), 6f, 6f, 0f, 20f, 37f, "rock2_mountain") };

        Assert.NotNull(RoadLocationEntry.Plan(Request(new Vector2(26f, 0f), ground), Shape(0f, below, ground), Width, ground, world, out string why));
        // Up at the walking level it is in the way.
        var level = new[] { new LocationRock(new Vector2(14f, 0f), 6f, 6f, 0f, 40f, 60f, "rock2_mountain") };
        Assert.Null(RoadLocationEntry.Plan(Request(new Vector2(26f, 0f), ground), Shape(0f, level, ground), Width, ground, world, out why));
        Assert.StartsWith("road: ", why);
        Assert.Contains("on its rock2_mountain", why);
    }

    [Fact]
    public void ARoadThatWouldRunBackOverItsOwnRoadIsRefused()
    {
        var world = new GentleWorld();
        _scope.WithWorld(world);
        var ground = CaveGround(world);
        Vector2 end = new Vector2(26f, 0f);
        // Its road came in past the landing's line: from (26, -30) round to (40, 0), then back to (26, 0).
        var request = new LocationApproachRequest
        {
            RoadEnd = end, RoadHeight = ground(end.x, end.y), Arriving = 270f, Centre = new Vector2(0f, 0f),
            RoadTail = new List<Vector2> { new(10f, -1f), new(40f, -1f), end },
        };

        Assert.Null(RoadLocationEntry.Plan(request, Shape(), Width, ground, world, out string why));
        Assert.StartsWith("road: ", why);
        Assert.Contains("back over its own road", why);
    }

    [Fact]
    public void AStaircaseKeepsItsPiecesOffTheLocationsOwnBoulders()
    {
        var world = new SteepWorld();
        _scope.WithWorld(world);
        var ground = CaveGround(world);
        // A boulder of the cave's on the straight line from the road's end to the landing.
        var rock = new LocationRock(new Vector2(15f, 0.5f), 2f);
        LocationApproachRequest request = Request(new Vector2(24f, 0f), ground);

        RoadStairSpan? span = RoadStairRuns.PlanPlacedLocationApproach(request, 90f, CaveFloor, TestLattice.Of(ground),
            (x, z) => Heightmap.Biome.Mountain, 77, out string why, rocks: new[] { rock });

        Assert.True(span != null, why);
        foreach (StairPiece piece in span!.Plan.Pieces.Where(p => p.Kind != StairPieceKind.Support))
            Assert.True(rock.Distance(new Vector2(piece.Position.x, piece.Position.z)) >= RoadStairRuns.Width * 0.5f - 0.01f,
                $"{piece.Kind} at {piece.Position} on the boulder");
    }

    [Fact]
    public void InsideAFootprintOnlyARoadIntoTheLocationLevelsAndPaintsAndOnlyCloseToItself()
    {
        _scope.WithWorld(new FlatWorld());
        RoadSpatialGrid.Clear();
        // An ordinary road, 1 m above the flat ground, straight through the footprint.
        RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(-30f, -10f), new(30f, -10f) }, Width, WorldGenerator.instance);
        RoadSpatialGrid.FinalizeRoadNetwork();
        Vector2s zone = new(0, 0);
        Heightmap hm = Heightmap.CreateForZone(zone);
        TerrainComp tc = hm.m_terrainComp!;
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, RoadSpatialGrid.GetRoadPointsInZone(zone), hm, tc);
        Assert.DoesNotContain(tc.m_modifiedPaint, m => m);

        // A road into the location, 1 m up, along z = 5 from outside the footprint to near its middle.
        var points = new List<Vector2>();
        var heights = new List<float>();
        for (float x = 30f; x >= 0f; x -= 1f) { points.Add(new Vector2(x, 5f)); heights.Add(31f); }
        var plan = RoadSpatialGrid.PlanLocationEntry(points, Width, WorldGenerator.instance, 31f, 31f, p => 30f, new RoadLocationProtection.Targets(null, new Vector2(0f, 0f)));
        Assert.NotNull(plan);
        RoadSpatialGrid.CommitAppend(new[] { plan! }, RoadSpatialGrid.RoadNetworkVersion, intoLocation: true);
        Assert.Contains(RoadSpatialGrid.GetRoadPointsInZone(zone), p => p.intoLocation);
        // The location's ground, published by its preload.
        PlacedLocations.Preload(new Vector2(0f, 0f), (_, _) => 30f);
        hm = Heightmap.CreateForZone(zone);
        tc = hm.m_terrainComp!;
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, RoadSpatialGrid.GetRoadPointsInZone(zone), hm, tc);

        int grid = tc.m_width + 1;
        float spacing = RoadConstants.ZoneSize / tc.m_width;
        int Index(float x, float z) =>
            Mathf.RoundToInt(z / spacing + tc.m_width / 2f) * grid + Mathf.RoundToInt(x / spacing + tc.m_width / 2f);
        float reach = RoadLocationEntry.InsideReach(Width);
        // On it, inside the footprint: levelled and painted.
        Assert.True(tc.m_modifiedHeight[Index(4f, 5f)], "not levelled on the road inside the footprint");
        Assert.True(tc.m_modifiedPaint[Index(4f, 5f)], "not painted on the road inside the footprint");
        // Inside the footprint past its reach: untouched (the batter it has outside stops there).
        Assert.False(tc.m_modifiedHeight[Index(4f, 5f + reach + 1f)], "levelled past its reach inside the footprint");
        // The ordinary road still leaves the footprint alone.
        Assert.False(tc.m_modifiedHeight[Index(0f, -10f)], "an ordinary road levelled inside the footprint");
    }

    /// <summary>
    /// Ground not known inside a location has one meaning for the terrain writer and the finished-ground predictor, and under the
    /// preload-list rule it is a bug: a road into the cave (on the preload list, its ground not published: the state the rule
    /// rules out), 1 m over the flat ground, is written outside its footprint; inside it is not written, never levelled on the
    /// ground under the compiler in its stead, where the predictor reads ground not known (NaN). It is said as an error
    /// naming the zone, the location and the road point, and the zone is stamped: nothing waits. On the parent the zone was left
    /// unstamped, waiting, and written again once the ground was read.
    /// </summary>
    [Fact]
    public void AWayIntoALocationWhoseGroundIsNotKnownIsNotWrittenThereAndSaysSoAsAnError()
    {
        _scope.WithWorld(new FlatWorld());
        RoadSpatialGrid.Clear();
        RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(300f, 300f), new(340f, 300f) }, Width, WorldGenerator.instance);
        RoadSpatialGrid.FinalizeRoadNetwork();
        var points = new List<Vector2>();
        var heights = new List<float>();
        for (float x = 30f; x >= 0f; x -= 1f) { points.Add(new Vector2(x, 5f)); heights.Add(31f); }
        var plan = RoadSpatialGrid.PlanLocationEntry(points, Width, WorldGenerator.instance, 31f, 31f, p => 30f, new RoadLocationProtection.Targets(null, new Vector2(0f, 0f)));
        Assert.NotNull(plan);
        RoadSpatialGrid.CommitAppend(new[] { plan! }, RoadSpatialGrid.RoadNetworkVersion, intoLocation: true);
        Vector2s zone = new(0, 0);
        Heightmap hm = _scope.RegisterHeightmap(zone);
        TerrainComp tc = hm.m_terrainComp!;
        var lines = new List<string>();
        BepInEx.Logging.ManualLogSource.Captured = lines;
        try { RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, RoadSpatialGrid.GetRoadPointsInZone(zone), hm, tc); }
        finally { BepInEx.Logging.ManualLogSource.Captured = null; }
        int grid = tc.m_width + 1;
        float spacing = RoadConstants.ZoneSize / tc.m_width;
        int Index(float x, float z) =>
            Mathf.RoundToInt(z / spacing + tc.m_width / 2f) * grid + Mathf.RoundToInt(x / spacing + tc.m_width / 2f);
        Func<int, int, float> Predicted() => RoadTerrainModifier.FinishedGround((x, z) => WorldGenerator.instance.GetHeight(x, z));

        Assert.True(tc.m_modifiedHeight[Index(25f, 5f)], "not levelled outside the footprint");
        Assert.True(float.IsNaN(Predicted()(4, 5)));
        Assert.False(tc.m_modifiedHeight[Index(4f, 5f)], "levelled inside the cave on ground not known");
        Assert.True(RoadTerrainModifier.CarriesCurrentRoads(tc), "the zone was left unstamped, waiting");
        Assert.True(lines.Exists(l => l.Contains($"Zone {zone}") && l.Contains("whose ground is not known") && l.Contains("(0.0, 0.0)")),
            string.Join(" | ", lines));
    }

    /// <summary>The locations at <paramref name="centres"/> on the preload list, each one's ground the flat 30 m.</summary>
    private static void PreloadAt30(params Vector2[] centres)
    {
        foreach (Vector2 c in centres) PlacedLocations.Preload(c, (_, _) => 30f);
    }

    /// <summary>The fixture's cave as the world's location at the origin: the location a saved road point is bound to again.</summary>
    private static void ACaveAtTheOrigin() => TestLocations.Add(ZoneSystem.instance!, new ZoneSystem.LocationInstance
    {
        m_location = new ZoneSystem.ZoneLocation { m_prefab = new SoftReferenceableAssets.SoftReference<GameObject>("TestCave"), m_exteriorRadius = Footprint },
        m_position = new Vector3(0f, 30f, 0f),
    });

    /// <summary>The location a road is planned into (A, the fixture's cave at the origin) and another location (B) whose footprint
    /// overlaps A's over the road's last stretch: B's centre is in the zone north of A's, its footprint reaching down over
    /// the road into A at z = 10 for x below about 10.</summary>
    private static readonly Vector2 Other = new(0f, 34f);
    private const float OtherRadius = 26f;

    /// <summary>A way into A along z = 10 from outside every footprint, 1 m over the flat ground, stored as generation
    /// stores it (CommitRoad) or as a manual road's append does (CommitAppend), into A alone.</summary>
    private static void RoadIntoTheCaveBesideAnother(bool appended)
    {
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), Footprint), new RoadLocationProtection.Footprint(Other, OtherRadius) });
        RoadSpatialGrid.Clear();
        RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(300f, 300f), new(340f, 300f) }, Width, WorldGenerator.instance);
        RoadSpatialGrid.FinalizeRoadNetwork();
        var points = new List<Vector2>();
        for (float x = 30f; x >= 0f; x -= 1f) points.Add(new Vector2(x, 10f));
        var plan = RoadSpatialGrid.PlanLocationEntry(points, Width, WorldGenerator.instance, 31f, 31f, p => 30f, new RoadLocationProtection.Targets(null, new Vector2(0f, 0f)));
        Assert.NotNull(plan);
        if (appended) RoadSpatialGrid.CommitAppend(new[] { plan! }, RoadSpatialGrid.RoadNetworkVersion, intoLocation: true);
        else RoadSpatialGrid.CommitRoad(new List<RoadSpatialGrid.PlannedPath> { plan! }, intoLocation: true, ownChecked: true);
    }

    /// <summary>The ground the writer leaves and the predictor expects at (x, z) on the road into A: whether the writer
    /// levelled and painted it, and the two heights.</summary>
    private static (bool levelled, bool painted, float written, float predicted) WrittenAt(float x, float z)
    {
        Vector2s zone = new(0, 0);
        Heightmap hm = Heightmap.CreateForZone(zone);
        TerrainComp tc = hm.m_terrainComp!;
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, RoadSpatialGrid.GetRoadPointsInZone(zone), hm, tc);
        int grid = tc.m_width + 1;
        float spacing = RoadConstants.ZoneSize / tc.m_width;
        int at = Mathf.RoundToInt(z / spacing + tc.m_width / 2f) * grid + Mathf.RoundToInt(x / spacing + tc.m_width / 2f);
        float written = 30f + (tc.m_modifiedHeight[at] ? tc.m_levelDelta[at] + tc.m_smoothDelta[at] : 0f);
        float predicted = RoadTerrainModifier.FinishedGround((_, _) => 30f)(Mathf.RoundToInt(x), Mathf.RoundToInt(z));
        return (tc.m_modifiedHeight[at], tc.m_modifiedPaint[at], written, predicted);
    }

    /// <summary>
    /// A road planned into one location changes that location's ground alone (decision 6v): on its way into the cave its earthwork
    /// and paint reach a second location whose footprint overlaps the cave's, and they leave the second location's ground as it is,
    /// while the cave's own ground beside it is levelled and painted. The terrain writer and the finished-ground predictor
    /// agree at both, for a generated road and a manual road's append alike. Before, every point of a road into a location
    /// levelled and painted inside any footprint it reached.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARoadIntoOneLocationLeavesAnOverlappingLocationsGroundAlone(bool appended)
    {
        _scope.WithWorld(new FlatWorld());
        RoadIntoTheCaveBesideAnother(appended);
        PreloadAt30(new Vector2(0f, 0f), Other);
        var own = WrittenAt(14f, 10f);
        Assert.True(own.levelled && own.painted, "the cave's own ground on the road is not levelled and painted");
        Assert.True(own.written > 30.25f, $"the cave's own ground on the road at {own.written:F2}, not raised toward it");
        Assert.Equal(own.written, own.predicted, 3);
        var other = WrittenAt(4f, 10f);
        Assert.False(other.levelled, "the other location's ground was levelled by a road into the cave");
        Assert.False(other.painted, "the other location's ground was painted by a road into the cave");
        Assert.Equal(30f, other.predicted);
    }

    /// <summary>
    /// A saved road keeps the location it was planned into: read back, the cave's ground is still levelled under it and the
    /// overlapping location's still left alone, its saved location bound again to the world's cave by its identity
    /// (LocationPrefabLevelling.LocationOf). In a world where no location stands there it is let into no footprint.
    /// </summary>
    [Fact]
    public void ASavedRoadKeepsTheLocationItWasPlannedInto()
    {
        _scope.WithWorld(new FlatWorld()).WithZoneSystem();
        RoadIntoTheCaveBesideAnother(appended: false);
        ACaveAtTheOrigin();
        PreloadAt30(new Vector2(0f, 0f), Other);
        byte[] saved = RoadSpatialGrid.SerializeAllRoadPoints()!;
        RoadSpatialGrid.Clear();
        Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(saved), RoadSpatialGrid.LastReadRefusal);
        Assert.Equal(saved, RoadSpatialGrid.SerializeAllRoadPoints());
        var own = WrittenAt(14f, 10f);
        Assert.True(own.levelled && own.painted, "after a reload the cave's own ground is not levelled and painted");
        Assert.Equal(own.written, own.predicted, 3);
        var other = WrittenAt(4f, 10f);
        Assert.False(other.levelled || other.painted, "after a reload the other location's ground was changed");
        Assert.Equal(30f, other.predicted);

        ZoneSystem.instance!.m_locationInstances.Clear();
        RoadSpatialGrid.Clear();
        Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(saved), RoadSpatialGrid.LastReadRefusal);
        var none = WrittenAt(14f, 10f);
        Assert.False(none.levelled || none.painted, "a road whose saved location is not found changed a footprint's ground");
    }

    /// <summary>
    /// A saved road into a location keeps that location by its identity (decision 6d): the saved centre is the identity its plan
    /// carried, bound again exactly (LocationPrefabLevelling.LocationOf), not resolved again from coordinates. Here a small
    /// location stands 0.4 m from the cave's centre across a zone's edge (zones meet at x = 32), so the coordinate resolver
    /// finds two locations at the cave's centre; read back, the road still levels and paints the cave's own ground. With the
    /// cave gone from the world it is let into no footprint. Before, the cave was lost on reading and its ground written
    /// as any road's.
    /// </summary>
    [Fact]
    public void ASavedRoadIntoALocationBesideAnotherAcrossAZoneEdgeKeepsItsLocation()
    {
        _scope.WithWorld(new FlatWorld()).WithZoneSystem();
        Vector2 cave = new(31.8f, 0f), small = new(32.2f, 0f);
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(cave, Footprint), new RoadLocationProtection.Footprint(small, 0.3f) });
        PlacedLocations.Preload(cave);
        RoadSpatialGrid.Clear();
        RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(300f, 300f), new(340f, 300f) }, Width, WorldGenerator.instance);
        RoadSpatialGrid.FinalizeRoadNetwork();
        var points = new List<Vector2>();
        for (float x = cave.x - 30f; x <= cave.x; x += 1f) points.Add(new Vector2(x, 10f));
        var plan = RoadSpatialGrid.PlanLocationEntry(points, Width, WorldGenerator.instance, 31f, 31f, p => 30f, new RoadLocationProtection.Targets(null, cave));
        Assert.NotNull(plan);
        RoadSpatialGrid.CommitRoad(new List<RoadSpatialGrid.PlannedPath> { plan! }, intoLocation: true, ownChecked: true);
        foreach (var (name, at) in new[] { ("TestCave", cave), ("SmallLocation", small) })
            TestLocations.Add(ZoneSystem.instance!, new ZoneSystem.LocationInstance
            {
                m_location = new ZoneSystem.ZoneLocation { m_prefab = new SoftReferenceableAssets.SoftReference<GameObject>(name), m_exteriorRadius = 0.3f },
                m_position = new Vector3(at.x, 30f, at.y),
            });
        Assert.False(LocationPrefabLevelling.LocationAt(cave, out _, out _), "the fixture's cave centre is not ambiguous to the coordinate resolver");
        PreloadAt30(cave, small);
        byte[] saved = RoadSpatialGrid.SerializeAllRoadPoints()!;
        RoadSpatialGrid.Clear();
        Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(saved), RoadSpatialGrid.LastReadRefusal);
        Assert.Equal(saved, RoadSpatialGrid.SerializeAllRoadPoints());
        var own = WrittenAt(20f, 10f);
        Assert.True(own.levelled && own.painted, "after a reload the cave's own ground is not levelled and painted");
        Assert.Equal(own.written, own.predicted, 3);

        ZoneSystem.instance!.m_locationInstances.Remove(ZoneSystem.GetZone(new Vector3(cave.x, 0f, cave.y)));
        RoadSpatialGrid.Clear();
        Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(saved), RoadSpatialGrid.LastReadRefusal);
        var gone = WrittenAt(20f, 10f);
        Assert.False(gone.levelled || gone.painted, "a road whose saved location is gone from the world changed a footprint's ground");
    }

    [Fact]
    public void InsideALocationTheEarthworkIsMeasuredAgainstTheGroundTheLocationLeaves()
    {
        // The terrain written on is the bare 30 m; the cave levels its landing to 32 m, which the
        // game draws under the compiler's deltas in play. A road into the location at 32 m needs no
        // earthwork there: measured against the bare ground it added 2 m on top.
        _scope.WithWorld(new FlatWorld());
        RoadSpatialGrid.Clear();
        RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(300f, 300f), new(340f, 300f) }, Width, WorldGenerator.instance);
        RoadSpatialGrid.FinalizeRoadNetwork();
        var points = new List<Vector2>();
        for (float x = 30f; x >= 0f; x -= 1f) points.Add(new Vector2(x, 5f));
        var plan = RoadSpatialGrid.PlanLocationEntry(points, Width, WorldGenerator.instance, 32f, 32f, p => 32f, new RoadLocationProtection.Targets(null, new Vector2(0f, 0f)));
        RoadSpatialGrid.CommitAppend(new[] { plan! }, RoadSpatialGrid.RoadNetworkVersion, intoLocation: true);
        Vector2s zone = new(0, 0);
        // The cave's ground, published by its preload: 32 m.
        PlacedLocations.Preload(new Vector2(0f, 0f), (_, _) => 32f);
        Heightmap hm = Heightmap.CreateForZone(zone);
        TerrainComp tc = hm.m_terrainComp!;
        RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, RoadSpatialGrid.GetRoadPointsInZone(zone), hm, tc);
        int grid = tc.m_width + 1;
        float spacing = RoadConstants.ZoneSize / tc.m_width;
        int at = Mathf.RoundToInt(5f / spacing + tc.m_width / 2f) * grid + Mathf.RoundToInt(4f / spacing + tc.m_width / 2f);
        Assert.True(Mathf.Abs(tc.m_levelDelta[at]) < 0.2f, $"delta {tc.m_levelDelta[at]:F2} on the cave's own 32 m");
        // The finished-ground predictor reads the same location ground the writer wrote on: the one published by the preload.
        Assert.Equal(32f + tc.m_levelDelta[at], RoadTerrainModifier.FinishedGround((x, z) => WorldGenerator.instance.GetHeight(x, z))(4, 5), 3);
    }

    /// <summary><see cref="GentleWorld"/> with a wall 9 m high beside the way in, its middle 2 m off the line in from
    /// +x: a road along that line is at its ground at its centre and 9 m under it at one edge.</summary>
    private sealed class WalledWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) =>
            new GentleWorld().GetHeight(wx, wy) + (wx >= 15f && wx <= 22f && Mathf.Abs(wy - 2f) <= 0.6f ? 9f : 0f);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 77;
    }

    /// <summary>The earthwork cap is one measurement across the road's width, for a road onto a location's landing and an
    /// ordinary road alike: the same road past the cap at one edge, its centre within it, is refused by both. The ordinary
    /// road read its centre alone and was built with the wall cut 9 m deep at its edge.</summary>
    [Fact]
    public void AnEdgePastTheEarthworkCapRefusesARoadOntoALandingAndAnOrdinaryRoadAlike()
    {
        PlainRoads.WithModDefaults(() =>
        {
            RoadWiggle.Amplitude = 0f;
            var world = new WalledWorld();
            _scope.WithWorld(world);
            var ground = CaveGround(world);
            PlacedLocationShape shape = Shape(0f, null, ground);
            Vector2 landing = shape.LandingFor(RoadLocationEntry.InsideReach(Width) + RoadLocationEntry.EndSlack);

            Assert.Null(RoadLocationEntry.Plan(Request(new Vector2(26f, 0f), ground), shape, Width, ground, world, out string why));
            Assert.Contains("earthwork", why);

            // The same line over the same ground as an ordinary road: with no location there (its protection would keep an
            // ordinary road out), over the natural ground, which beside the wall is the ground the location leaves.
            RoadLocationProtection.Reset();
            var line = new List<Vector2> { new(30f, 0f), new(landing.x, 0f) };
            RoadSpatialGrid.PlannedPath? road = RoadSpatialGrid.PlanRoadPath(line, Width, world);
            Assert.True(road != null, RoadSpatialGrid.LastRefusal);
            Assert.All(road!.Points.Where(p => p.x >= 15f && p.x <= 22f), p => Assert.InRange(p.y, -0.5f, 0.5f));
            string? refused = RoadNetworkGenerator.BuiltRoadRefusal(new List<RoadSpatialGrid.PlannedPath> { road }, new List<RoadCrossing>(), out Vector2 at);
            Assert.True(refused == RoadNetworkGenerator.EarthworkRefusal, $"refused: {refused ?? "nothing"} at {at}");
            Assert.InRange(at.x, 15f, 22f);
        });
    }

    [Fact]
    public void ARoadTakesTheLedgeOnItsOwnSideOfTheMouth()
    {
        // Two level ledges, one each side of the mouth's axis; the road comes from the -z side.
        var world = new GentleWorld();
        _scope.WithWorld(world);
        LevelOp op = CaveFloor[0];
        Vector2 floor = LocationLevelling.OffsetAt(op, 90f);
        float Ground(float x, float z)
        {
            if (x >= 6f && x <= 12f && Mathf.Abs(z) >= 5f && Mathf.Abs(z) <= 11f) return 47.3f;
            float h = 50f - 0.3f * x, level = 50f + op.Height;
            float d = Vector2.Distance(new Vector2(x, z), floor);
            if (d <= op.Radius) return level;
            return d <= op.SmoothRadius ? Mathf.Lerp(h, level, 1f - Mathf.Pow(d / op.SmoothRadius, op.SmoothPower)) : h;
        }

        RoadSpatialGrid.PlannedPath? road = RoadLocationEntry.Plan(Request(new Vector2(24f, -8f), Ground), Shape(0f, null, Ground), Width, Ground, world, out string why);

        Assert.True(road != null, why);
        Assert.True(road!.Points[road.Points.Count - 1].y < 0f, $"ended at {road.Points[road.Points.Count - 1]}, across the mouth");
    }

    [Fact]
    public void AFlatLevelWithTheLocationIsItsLandingAndALowerSpotIsNot()
    {
        // A BearCave-like location: its flat is the ground in front of its mouth, level with its root
        // (50 m), 6 m across; the hillside falls 0.5 to +x below it (the road once ended 6 m
        // under the landing, on the nearest level ground below).
        var world = new GentleWorld();
        _scope.WithWorld(world);
        var flat = new LevelOp(0f, 3f, 0f, 6f, false, 8f, 3f);
        Vector2 flatAt = LocationLevelling.OffsetAt(flat, 90f);
        float Ground(float x, float z)
        {
            float d = Vector2.Distance(new Vector2(x, z), flatAt);
            if (d <= 6f) return 50f;
            float h = 50f - 0.5f * Mathf.Max(0f, x - flatAt.x - 6f);
            if (x > 20f && x < 30f) h = 42f; // a level bench far below
            return d <= 8f ? Mathf.Lerp(h, 50f, 1f - Mathf.Pow(d / 8f, 3f)) : h;
        }
        PlacedLocationShape shape = PlacedLocationShape.Of(new Vector2(0f, 0f), 90f, flat, 0f, null, Ground, root: 50f);

        var landings = shape.Landings(Ground, 1f);

        Assert.Equal(0f, shape.Pit);
        Assert.NotEmpty(landings);
        Assert.All(landings, l => Assert.InRange(l.height, 50f - PlacedLocationShape.LandingRise, 50f + PlacedLocationShape.LandingRise));
        // Without the location's root nothing holds it to the entrance's level: the bench far below qualifies.
        PlacedLocationShape blind = PlacedLocationShape.Of(new Vector2(0f, 0f), 90f, flat, 0f, null, Ground);
        Assert.Contains(blind.Landings(Ground, 1f), l => l.height < 45f || l.height > 49f);
    }

    [Fact]
    public void AnApproachDoesNotRunBesideAnotherRoadAtAnotherHeight()
    {
        var world = new GentleWorld();
        _scope.WithWorld(world);
        var ground = CaveGround(world);
        // Another road 3 m up, alongside the straight line in, 2 m to its side.
        IEnumerable<(Vector2 at, float height)> Near(Vector2 p, float within)
        {
            var other = new Vector2(p.x, 2f);
            if (p.x > 10f && p.x < 20f && Vector2.Distance(p, other) <= within) yield return (other, ground(p.x, p.y) + 3f);
        }

        Assert.NotNull(RoadLocationEntry.Plan(Request(new Vector2(26f, 0f), ground), Shape(), Width, ground, world, out string why));
        Assert.Null(RoadLocationEntry.Plan(Request(new Vector2(26f, 0f), ground), Shape(), Width, ground, world, out why, Near));
        Assert.Contains("beside another road", why);
    }

    [Fact]
    public void TheFlagsSurviveASave()
    {
        _scope.WithWorld(new FlatWorld()).WithZoneSystem();
        ACaveAtTheOrigin();
        RoadSpatialGrid.Clear();
        RoadSpatialGrid.AddRoadPath(new List<Vector2> { new(300f, 300f), new(340f, 300f) }, Width, WorldGenerator.instance);
        RoadSpatialGrid.FinalizeRoadNetwork();
        var plan = RoadSpatialGrid.PlanLocationEntry(new List<Vector2> { new(30f, 0f), new(10f, 0f) }, Width, WorldGenerator.instance, 30f, 30f, p => 30f, new RoadLocationProtection.Targets(null, new Vector2(0f, 0f)));
        RoadSpatialGrid.CommitAppend(new[] { plan! }, RoadSpatialGrid.RoadNetworkVersion, intoLocation: true);
        byte[] saved = RoadSpatialGrid.SerializeAllRoadPoints()!;
        RoadSpatialGrid.Clear();
        Assert.True(RoadSpatialGrid.DeserializeAllRoadPoints(saved));
        Assert.Contains(RoadSpatialGrid.GetRoadPointsInZone(new Vector2s(0, 0)), p => p.intoLocation);
        Assert.Equal(saved, RoadSpatialGrid.SerializeAllRoadPoints());

        StairStructures.Reset();
        StairStructures.Add(new StairRun { From = new Vector2(1f, 2f), To = new Vector2(3f, 4f), Width = 2f, KeepsLocationRocks = true });
        byte[] stairs = StairStructures.Serialize();
        Assert.True(Assert.Single(StairStructures.Parse(stairs)).KeepsLocationRocks);
        StairStructures.Reset();
    }


    /// <summary>The staircase record is written as format 7, a number no earlier build wrote, with the layout that laid each
    /// run, where the road meets each end and an approach's location; it is the only version read.</summary>
    [Fact]
    public void StairRecordSevenIsWrittenAndTheOnlyOneRead()
    {
        StairStructures.Reset();
        StairStructures.Add(new StairRun { From = new Vector2(1f, 2f), To = new Vector2(3f, 4f), Width = 2f, KeepsLocationRocks = true, ToMeet = new Vector2(5f, 6f) });
        byte[] v7 = StairStructures.Serialize();
        StairStructures.Reset();
        Assert.Equal(7, BitConverter.ToInt32(v7, 0));
        StairRun seven = Assert.Single(StairStructures.Parse(v7));
        Assert.Null(seven.ExpectedLocation);
        Assert.Equal(v7, Reserialized(seven));
        // No other version is read.
        foreach (int version in new[] { 0, 1, 2, 3, 4, 5, 6, 8 })
        {
            byte[] other = (byte[])v7.Clone();
            BitConverter.GetBytes(version).CopyTo(other, 0);
            Assert.Throws<System.IO.InvalidDataException>(() => StairStructures.Parse(other));
        }
    }

    private static byte[] Reserialized(StairRun run)
    {
        StairStructures.Reset();
        StairStructures.Add(run);
        byte[] bytes = StairStructures.Serialize();
        StairStructures.Reset();
        return bytes;
    }

    /// <summary>Approaching one location exempts only that location: another footprint offset over the target's own centre, or one
    /// nested in it, keeps its protection, on the search, a staircase, the final road's arrival and the road onto the landing.</summary>
    [Fact]
    public void ApproachingOneLocationNeverEntersAnotherThatOverlapsIt()
    {
        var world = new GentleWorld();
        _scope.WithWorld(world);
        var ground = CaveGround(world);
        var target = new Vector2(0f, 0f);
        float clearance = RoadLocationProtection.Clearance(Width);
        var own = new RoadLocationProtection.Footprint(target, Footprint);
        var toTarget = new RoadLocationProtection.Targets(null, target);
        // Another location whose footprint holds the target's centre.
        RoadLocationProtection.Set(new[] { own, new RoadLocationProtection.Footprint(new Vector2(-10f, 0f), 14f) });
        // The search: arriving into the target is let through, arriving through the other is not.
        Assert.False(RoadLocationProtection.BlocksRoute(new Vector2(5f, 40f), new Vector2(5f, 23f), clearance, toTarget));
        Assert.True(RoadLocationProtection.BlocksRoute(new Vector2(-40f, 0f), new Vector2(-23f, 0f), clearance, toTarget));
        // A staircase to the target: a landing in it stands, one in the other is refused.
        StairPlan Landing(float x, float z)
        {
            var plan = new StairPlan { Kit = "stone", Reached = true };
            plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Landing, Prefab = "stone_floor_2x2", Position = new Vector3(x, ground(x, z), z) });
            return plan;
        }
        Assert.Equal("", RoadStairRuns.Blocked(Landing(5f, 10f), world, target, ground));
        Assert.StartsWith("another location", RoadStairRuns.Blocked(Landing(-23f, 0f), world, target, ground));

        // The final road: its trimmed arrival at the target's clearance edge grazes only the target's margin, never another's.
        var end = new Vector2(24f, 0f);
        RoadLocationProtection.Set(new[] { own });
        Assert.False(RoadLocationProtection.BlocksFinalSegment(new Vector2(24f, 30f), end, clearance, new Vector2(24f, 30f), end, toTarget, out _));
        RoadLocationProtection.Set(new[] { own, new RoadLocationProtection.Footprint(new Vector2(34f, 0f), 6f) });
        Assert.True(RoadLocationProtection.BlocksFinalSegment(new Vector2(24f, 30f), end, clearance, new Vector2(24f, 30f), end, toTarget, out _));

        // The road onto the landing: a location nested in the target's footprint, on the straight line in, is kept out of.
        var nested = new Vector2(14f, 0f);
        RoadLocationProtection.Set(new[] { own, new RoadLocationProtection.Footprint(nested, 2f) });
        RoadSpatialGrid.PlannedPath? road = RoadLocationEntry.Plan(Request(new Vector2(26f, 0f), ground), Shape(), Width, ground, world, out string why);
        if (road == null) Assert.Contains("into another location", why);
        else Assert.DoesNotContain(road.Points, p => Vector2.Distance(p, nested) <= 2f);
    }

    /// <summary>Roads and staircases take one entrance side for the cave whose floor is 4.75 m behind its centre: a road's end
    /// between the floor's line and the centre's is led round the rock by both or by neither.</summary>
    [Theory]
    [InlineData(-2f, 15f)]
    [InlineData(-2f, -15f)]
    [InlineData(10f, 5f)]
    [InlineData(-20f, 5f)]
    public void RoadAndStairsTakeTheSameEntranceSide(float x, float z)
    {
        PlacedLocationShape shape = Shape(7f);
        float reach = RoadLocationEntry.InsideReach(Width);
        var end = new Vector2(x, z);
        bool roadGoesRound = RoadLocationEntry.Route(end, shape, shape.LandingFor(reach + RoadLocationEntry.EndSlack), reach).Count > 2;
        Assert.Equal(shape.Behind(end), roadGoesRound);
    }
}
