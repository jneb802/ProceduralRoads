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
public class SiteEntryRoadTests : IDisposable
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

    /// <summary>The steep hillside of <see cref="SiteApproachStairTests"/>: 0.8 near the cave.</summary>
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

    /// <summary>MountainCave02's floor, as in <see cref="SiteApproachStairTests"/>.</summary>
    private static readonly IReadOnlyList<LevelOp> CaveFloor = new[] { new LevelOp(-0.06f, -4.75f, -4.09f, 4f, false, 7f, 3f) };
    private const float Footprint = 20f, Width = 4f;

    public SiteEntryRoadTests()
    {
        RoadSiteProtection.Set(new[] { new RoadSiteProtection.Footprint(new Vector2(0f, 0f), Footprint) });
    }

    public void Dispose()
    {
        RoadSiteProtection.Reset();
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

    private static SiteApproachRequest Request(Vector2 end, Func<float, float, float> ground)
    {
        Vector2 before = end + end.normalized * 30f;
        return new SiteApproachRequest
        {
            RoadEnd = end, RoadHeight = ground(end.x, end.y), Arriving = StairLayout.Bearing(before, end),
            Centre = new Vector2(0f, 0f), RoadTail = new List<Vector2> { before, end },
        };
    }

    private static PlacedSiteShape Shape(float rock = 0f, IReadOnlyList<SiteRock>? rocks = null, Func<float, float, float>? ground = null) =>
        PlacedSiteShape.Of(new Vector2(0f, 0f), 90f, CaveFloor[0], rock, rocks, ground);

    [Fact]
    public void WhereTheGroundAllowsTheRoadItselfGoesOntoTheLanding()
    {
        var world = new GentleWorld();
        _scope.WithWorld(world);
        var ground = CaveGround(world);
        PlacedSiteShape shape = Shape();

        RoadSpatialGrid.PlannedPath? road = RoadSiteEntry.Plan(Request(new Vector2(26f, 0f), ground), shape, Width, ground, world, out string why);

        Assert.True(road != null, why);
        Vector2 last = road!.Points[road.Points.Count - 1];
        Assert.InRange(Vector2.Distance(last, shape.LandingFor(RoadSiteEntry.InsideReach(Width) + RoadSiteEntry.EndSlack)), 0f, 1f);
        Assert.InRange(road.Heights[road.Heights.Count - 1], ground(last.x, last.y) - 0.5f, ground(last.x, last.y) + 0.5f);
        // It reaches no nearer the entrance pit than its earthwork reaches inside a footprint.
        float reach = RoadSiteEntry.InsideReach(Width);
        Assert.All(road.Points, p => Assert.True(Vector2.Distance(p, shape.FlatCentre) >= shape.Pit + reach - 0.01f, $"{p} in the entrance"));
        Assert.True(road.Points.Any(p => p.magnitude < Footprint), "it never entered the footprint");
    }

    [Fact]
    public void WhereItIsTooSteepForRoadThereIsNoRoad()
    {
        var world = new SteepWorld();
        _scope.WithWorld(world);
        var ground = CaveGround(world);

        Assert.Null(RoadSiteEntry.Plan(Request(new Vector2(24f, 0f), ground), Shape(), Width, ground, world, out string why));
        Assert.StartsWith("road: ", why);
    }

    [Fact]
    public void TheRoadKeepsOffThePlacesOwnBoulders()
    {
        var world = new GentleWorld();
        _scope.WithWorld(world);
        var ground = CaveGround(world);
        // A boulder of the cave's beside the straight line in.
        var rocks = new[] { new SiteRock(new Vector2(14f, 2f), 2f) };

        Assert.Null(RoadSiteEntry.Plan(Request(new Vector2(26f, 0f), ground), Shape(0f, rocks), Width, ground, world, out string why));
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
        PlacedSiteShape shape = Shape(rock);
        // The road ends behind the cave (it faces +x), off to one side.
        SiteApproachRequest request = Request(new Vector2(-24f, 12f), ground);

        RoadSpatialGrid.PlannedPath? road = RoadSiteEntry.Plan(request, shape, Width, ground, world, out string why);

        Assert.True(road != null, why);
        float reach = RoadSiteEntry.InsideReach(Width);
        Assert.All(road!.Points, p => Assert.True(shape.InFront(p) || Vector2.Distance(p, shape.Centre) >= shape.KeepOut + reach - 0.01f,
            $"{p} in the cave's rock"));
        // Straight in, it would have cut through the hill behind the mouth.
        Vector2 target = shape.LandingFor(reach + RoadSiteEntry.EndSlack);
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
        PlacedSiteShape shape = Shape(0f, null, Ground);

        var landings = shape.Landings(Ground, 1f);

        Assert.NotEmpty(landings);
        Assert.InRange(landings[0].at.x, 6f, 12f);
        Assert.InRange(landings[0].at.y, 5f, 11f);
        Assert.Equal(47.3f, landings[0].height, 2);

        // The road comes up to the ledge's height, on the ledge.
        RoadSpatialGrid.PlannedPath? road = RoadSiteEntry.Plan(Request(new Vector2(24f, 8f), Ground), shape, Width, Ground, world, out string why);
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
        var rock = new SiteRock(new Vector2(0f, 0f), 10f, 2f, 90f, float.NegativeInfinity, float.PositiveInfinity);
        Assert.Equal(0f, rock.Distance(new Vector2(0f, -9f)), 3);
        Assert.Equal(0f, rock.Distance(new Vector2(1.5f, 9f)), 3);
        Assert.Equal(3f, rock.Distance(new Vector2(5f, 0f)), 3);
        Assert.Equal(1f, rock.Distance(new Vector2(0f, 11f)), 3);
        // Local and World undo each other, the way Unity turns.
        Vector2 w = SiteRock.World(new Vector2(1f, 0f), 90f);
        Assert.Equal(0f, w.x, 3);
        Assert.Equal(-1f, w.y, 3);
        Vector2 back = SiteRock.Local(w, 90f);
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
        PlacedSiteShape plain = Shape(rock);
        float reach = RoadSiteEntry.InsideReach(Width);
        Vector2 target = plain.LandingFor(reach + RoadSiteEntry.EndSlack);
        float g = ground(target.x, target.y);
        // A 2 m boulder beside the mouth, its near side 3 m off the road's end.
        Vector2 at = target + new Vector2(-2f, 4.5f);
        var top = new float[9];
        for (int i = 0; i < 9; i++) top[i] = g + 2f;
        var known = new SiteRock(at, 1f, 1f, 0f, g - 1f, g + 2f, "Rock_4", top, Mathf.RoundToInt(at.x) - 1, Mathf.RoundToInt(at.y) - 1, 3);
        var boxed = new SiteRock(at, 1f, 1f, 0f, g - 1f, g + 2f, "Rock_4");

        Assert.NotNull(RoadSiteEntry.Plan(Request(new Vector2(26f, 0f), ground), Shape(rock, new[] { known }, ground), Width, ground, world, out string why));
        Assert.Null(RoadSiteEntry.Plan(Request(new Vector2(26f, 0f), ground), Shape(rock, new[] { boxed }, ground), Width, ground, world, out why));
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
        var rock = new SiteRock(new Vector2(0f, 0f), 10f, 10f, 0f, 40f, 52f, "rock1_mountain", top, -10, -10, 21);

        // Inside its box, but where nothing of it stands: not in the way (its box said it was).
        Assert.False(rock.InTheWay(new Vector2(-5f, 0f), 2f, 50f, 0.5f, 3f, out _));
        Assert.True(new SiteRock(new Vector2(0f, 0f), 10f, 10f, 0f, 40f, 52f).InTheWay(new Vector2(-5f, 0f), 2f, 50f, 0.5f, 3f, out _));
        // Beside the part buried under the ground: not in the way either.
        Assert.False(rock.InTheWay(new Vector2(2f, 0f), 1.5f, 50f, 0.5f, 3f, out _));
        // Beside the part standing above the ground: in the way, and how near is said.
        Assert.True(rock.InTheWay(new Vector2(5.5f, 0f), 1.5f, 50f, 0.5f, 3f, out float d));
        Assert.InRange(d, 0f, 1.5f);
    }

    [Fact]
    public void TheCavesOwnRockBodyIsKeptOutByItsRingNotAsABoulder()
    {
        var world = new GentleWorld();
        _scope.WithWorld(world);
        var ground = CaveGround(world);
        const float rock = 7f;
        // The cave's body: a boulder at its centre, wider than the way to its mouth.
        var body = new SiteRock(new Vector2(0.9f, -0.2f), 15f, "rock2_mountain");
        PlacedSiteShape shape = Shape(rock, new[] { body });

        Assert.Empty(shape.Rocks);
        Assert.Empty(shape.Body);
        Assert.NotNull(RoadSiteEntry.Plan(Request(new Vector2(26f, 0f), ground), shape, Width, ground, world, out string why));
        Assert.True(why == "", why);
        // One scattered outside its exterior radius is still kept clear.
        Assert.Single(Shape(rock, new[] { body, new SiteRock(new Vector2(12f, 9f), 2f) }).Rocks);
    }

    [Fact]
    public void TheBouldersHuggingTheMouthAreNotTouchedEvenSmallOnes()
    {
        var world = new GentleWorld();
        _scope.WithWorld(world);
        var ground = CaveGround(world);
        const float rock = 7f;
        PlacedSiteShape plain = Shape(rock);
        float reach = RoadSiteEntry.InsideReach(Width);
        Vector2 target = plain.LandingFor(reach + RoadSiteEntry.EndSlack);
        // A small boulder beside the mouth, inside the exterior radius, at the walking level,
        // its edge 1.8 m off the road's end: a road there would dig to it.
        var flank = new SiteRock(target + new Vector2(-2f, 2.5f), 1f, 1f, 0f, ground(target.x, target.y) - 1f, ground(target.x, target.y) + 2f, "Rock_4");
        PlacedSiteShape shape = Shape(rock, new[] { flank }, ground);

        Assert.Single(shape.Body);
        Assert.Null(RoadSiteEntry.Plan(Request(new Vector2(26f, 0f), ground), shape, Width, ground, world, out string why));
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
        var small = new[] { new SiteRock(new Vector2(14f, 0.5f), 1f) };

        Assert.NotNull(RoadSiteEntry.Plan(Request(new Vector2(26f, 0f), ground), Shape(0f, small), Width, ground, world, out string why));
        Assert.Empty(Shape(0f, small).Rocks);
        // At a place that keeps every rock it is kept clear.
        PlacedSiteShape hildir = PlacedSiteShape.Of(new Vector2(0f, 0f), 90f, CaveFloor[0], 0f, small, ground, keepSmallRocks: true);
        Assert.Single(hildir.Rocks);
        Assert.Null(RoadSiteEntry.Plan(Request(new Vector2(26f, 0f), ground), hildir, Width, ground, world, out why));
        Assert.StartsWith("road: ", why);
        Assert.Contains("on its rock", why);
        Assert.True(SiteRock.KeepsEveryRock("Hildir_cave"));
        Assert.False(SiteRock.KeepsEveryRock("MountainCave02"));
    }

    [Fact]
    public void ALargeBoulderBelowTheWalkingLevelIsNotInTheWay()
    {
        var world = new GentleWorld();
        _scope.WithWorld(world);
        var ground = CaveGround(world);
        // A big boulder down the slope: seen from above it covers the way in, but its top is
        // 10 m under the ground there.
        var below = new[] { new SiteRock(new Vector2(14f, 0f), 6f, 6f, 0f, 20f, 37f, "rock2_mountain") };

        Assert.NotNull(RoadSiteEntry.Plan(Request(new Vector2(26f, 0f), ground), Shape(0f, below, ground), Width, ground, world, out string why));
        // Up at the walking level it is in the way.
        var level = new[] { new SiteRock(new Vector2(14f, 0f), 6f, 6f, 0f, 40f, 60f, "rock2_mountain") };
        Assert.Null(RoadSiteEntry.Plan(Request(new Vector2(26f, 0f), ground), Shape(0f, level, ground), Width, ground, world, out why));
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
        var request = new SiteApproachRequest
        {
            RoadEnd = end, RoadHeight = ground(end.x, end.y), Arriving = 270f, Centre = new Vector2(0f, 0f),
            RoadTail = new List<Vector2> { new(10f, -1f), new(40f, -1f), end },
        };

        Assert.Null(RoadSiteEntry.Plan(request, Shape(), Width, ground, world, out string why));
        Assert.StartsWith("road: ", why);
        Assert.Contains("back over its own road", why);
    }

    [Fact]
    public void AStaircaseKeepsItsPiecesOffThePlacesOwnBoulders()
    {
        var world = new SteepWorld();
        _scope.WithWorld(world);
        var ground = CaveGround(world);
        // A boulder of the cave's on the straight line from the road's end to the landing.
        var rock = new SiteRock(new Vector2(15f, 0.5f), 2f);
        SiteApproachRequest request = Request(new Vector2(24f, 0f), ground);

        RoadStairSpan? span = RoadStairRuns.PlanPlacedSiteApproach(request, 90f, CaveFloor, ground,
            (x, z) => Heightmap.Biome.Mountain, 77, out string why, rocks: new[] { rock });

        Assert.True(span != null, why);
        foreach (StairPiece piece in span!.Plan.Pieces.Where(p => p.Kind != StairPieceKind.Support))
            Assert.True(rock.Distance(new Vector2(piece.Position.x, piece.Position.z)) >= RoadStairRuns.Width * 0.5f - 0.01f,
                $"{piece.Kind} at {piece.Position} on the boulder");
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

        RoadSpatialGrid.PlannedPath? road = RoadSiteEntry.Plan(Request(new Vector2(24f, -8f), Ground), Shape(0f, null, Ground), Width, Ground, world, out string why);

        Assert.True(road != null, why);
        Assert.True(road!.Points[road.Points.Count - 1].y < 0f, $"ended at {road.Points[road.Points.Count - 1]}, across the mouth");
    }

    [Fact]
    public void AFlatLevelWithThePlaceIsItsLandingAndALowerSpotIsNot()
    {
        // A BearCave-like place: its flat is the ground in front of its mouth, level with its root
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
        PlacedSiteShape shape = PlacedSiteShape.Of(new Vector2(0f, 0f), 90f, flat, 0f, null, Ground, root: 50f);

        var landings = shape.Landings(Ground, 1f);

        Assert.Equal(0f, shape.Pit);
        Assert.NotEmpty(landings);
        Assert.All(landings, l => Assert.InRange(l.height, 50f - PlacedSiteShape.LandingRise, 50f + PlacedSiteShape.LandingRise));
        // Without the place's root nothing holds it to the entrance's level: the bench far below qualifies.
        PlacedSiteShape blind = PlacedSiteShape.Of(new Vector2(0f, 0f), 90f, flat, 0f, null, Ground);
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

        Assert.NotNull(RoadSiteEntry.Plan(Request(new Vector2(26f, 0f), ground), Shape(), Width, ground, world, out string why));
        Assert.Null(RoadSiteEntry.Plan(Request(new Vector2(26f, 0f), ground), Shape(), Width, ground, world, out why, Near));
        Assert.Contains("beside another road", why);
    }



    /// <summary>The staircase record is written as format 5, a number no earlier build wrote, with the layout that laid each
    /// run; records 1 (no kept-rocks flag), 2 (no kit, no cantilever flag), 3 (no ground) and 4 (no layout) still load.</summary>
    [Fact]
    public void StairRecordsOneToFourStillLoadAndFiveIsWritten()
    {
        StairStructures.Reset();
        StairStructures.Add(new StairRun { From = new Vector2(1f, 2f), To = new Vector2(3f, 4f), Width = 2f, KeepsSiteRocks = true });
        byte[] v5 = StairStructures.Serialize();
        StairStructures.Reset();
        Assert.Equal(5, BitConverter.ToInt32(v5, 0));
        Assert.Equal(StairLayout.LayoutVersion, Assert.Single(StairStructures.Parse(v5)).Layout);
        // Format 4: format 5 without the run's layout (after its kit, an empty string's one length byte).
        var v4 = new List<byte>(v5);
        v4.RemoveRange(4 + 4 + 8 + 8 + 4 + 1 + 1, 4);
        v4.RemoveRange(0, 4);
        v4.InsertRange(0, BitConverter.GetBytes(4));
        StairRun four = Assert.Single(StairStructures.Parse(v4.ToArray()));
        Assert.True(four.KeepsSiteRocks);
        Assert.Equal(0, four.Layout);
        // Format 3: format 4 without the run's ground (its count, none, at the record's end).
        var v3 = new List<byte>(v4);
        v3.RemoveRange(v3.Count - 4, 4);
        v3.RemoveRange(0, 4);
        v3.InsertRange(0, BitConverter.GetBytes(3));
        StairRun three = Assert.Single(StairStructures.Parse(v3.ToArray()));
        Assert.True(three.KeepsSiteRocks);
        Assert.Empty(three.Ground);
        // Format 2: format 3 without the run's kit (an empty string, one length byte after the flag; no pieces).
        var v2 = new List<byte>(v3);
        v2.RemoveAt(4 + 4 + 8 + 8 + 4 + 1);
        v2.RemoveRange(0, 4);
        v2.InsertRange(0, BitConverter.GetBytes(2));
        StairRun two = Assert.Single(StairStructures.Parse(v2.ToArray()));
        Assert.True(two.KeepsSiteRocks);
        Assert.Null(two.Kit);
        // Format 1: format 2 without the kept-rocks flag.
        var v1 = new List<byte>(v2);
        v1.RemoveAt(4 + 4 + 8 + 8 + 4);
        v1.RemoveRange(0, 4);
        v1.InsertRange(0, BitConverter.GetBytes(1));
        StairRun one = Assert.Single(StairStructures.Parse(v1.ToArray()));
        Assert.False(one.KeepsSiteRocks);
        Assert.Equal(new Vector2(3f, 4f), one.To);
        Assert.Equal(2f, one.Width);
    }
}
