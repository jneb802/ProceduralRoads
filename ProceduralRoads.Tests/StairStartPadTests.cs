using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A small level pad where a staircase starts at its road's end (RoadEndRing.PadUnder, the ring's: the road
/// mostly level where the stairs start, as a junction is levelled but much smaller, just the bit under the stairs).
/// The staircase to a stone tower in game set off from its road's end turned 85 degrees off a road climbing to it: the road
/// ended level, but the ground under the first stair, off the road's end, followed the slope. Read on the ground the terrain
/// writer leaves (RoadTerrainModifier.FinishedGround), a road's end, a staircase's first pieces and the pad as stored.
/// </summary>
[Collection("StairLayout")]
public class StairStartPadTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    public StairStartPadTests() => RoadSpatialGrid.Clear();
    public void Dispose()
    {
        RoadSpatialGrid.Clear();
        StairStructures.Reset();
        _scope.Dispose();
    }

    private sealed class World : WorldGenerator
    {
        public Func<float, float, float> Height = (x, z) => 100f;
        public override float GetHeight(float x, float z) => Height(x, z);
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    private const float H = 100f;
    private static readonly Vector2 End = new(0f, 0f);

    /// <summary>Ground climbing north at 0.3 (the road's way to its end at the origin, at 100 m) and east at
    /// <paramref name="east"/> past a metre out.</summary>
    private World Slope(float east = 0.2f)
    {
        var world = new World { Height = (x, z) => H + 0.3f * z + east * Mathf.Max(0f, x - 1f) };
        _scope.WithWorld(world);
        return world;
    }

    private static float Ground(World world, float x, float z) => BiomeBlendedHeight.GetBlendedHeight(x, z, world);

    /// <summary>The road climbing north to its end at the origin, planned to meet it at <see cref="H"/>, stored.</summary>
    private static RoadSpatialGrid.PlannedPath Road(World world)
    {
        var path = Enumerable.Range(0, 41).Select(k => new Vector2(0f, -40f + k)).ToList();
        RoadSpatialGrid.PlannedPath road = RoadSpatialGrid.PlanRoadPath(path, RoadNetworkGenerator.RoadWidth, world, null, H)!;
        Assert.NotNull(road);
        RoadSpatialGrid.Commit(road);
        return road;
    }

    private static StairPiece Stair(Vector2 at, float y, float climbing) =>
        new() { Kind = StairPieceKind.Stair, Prefab = "stone_stair", Position = new Vector3(at.x, y, at.y), YawDegrees = StairLayout.Wrap(climbing + 180f) };

    private static StairPiece Floor(Vector2 at, float top, float heading) =>
        new() { Kind = StairPieceKind.Landing, Prefab = "stone_floor_2x2", Position = new Vector3(at.x, top - StairKit.Stone.FloorTop, at.y), YawDegrees = heading };

    /// <summary>A flight set off from the ring round the road's end (travel north) at <paramref name="arc"/> degrees round,
    /// climbing <paramref name="heading"/>: its sunk stair under the road's level, then five stairs from its foot.</summary>
    private static StairPlan FootFlight(float arc, float heading)
    {
        var ring = new RoadEndRing(End, 0f, H);
        Vector2 start = ring.ArcPoint(arc);
        var plan = new StairPlan { StartHeading = heading, Reached = true, SunkPieces = 1, Ring = ring, RingStart = start, Foot = new Vector3(start.x, H, start.y) };
        Vector2 step = StairLayout.Forward(heading) * StairLayout.PieceRun;
        for (int i = -1; i < 5; i++) plan.Pieces.Add(Stair(start + step * (i + 0.5f), H + i, heading));
        return plan;
    }

    /// <summary>The finished ground less the pad's height at every whole-metre vertex under the first walked piece and
    /// between it and the road's end (its back edge's half metre in from the circle), worst first.</summary>
    private static float WorstOffLevel(Func<int, int, float> finished, StairPlan plan, float level)
    {
        StairPiece first = plan.Pieces[plan.SunkPieces];
        var (back, heading, across, depth, _) = StairLayout.Footprint(first);
        Vector2 dir = StairLayout.Forward(heading), right = StairLayout.Right(heading);
        float worst = 0f;
        for (int x = -6; x <= 6; x++)
            for (int z = -6; z <= 6; z++)
            {
                var v = new Vector2(x, z);
                float along = Vector2.Dot(v - back, dir), side = Vector2.Dot(v - back, right);
                bool under = along >= 0f && along <= depth && Mathf.Abs(side) <= across / 2f;
                bool between = along >= -1.5f && along < 0f && Mathf.Abs(side) <= across / 2f;
                if (!under && !between) continue;
                float off = finished(x, z) - level;
                if (Mathf.Abs(off) > Mathf.Abs(worst)) worst = off;
            }
        return worst;
    }

    private static Func<int, int, float> Finished(World world)
    {
        RoadSpatialGrid.FinalizeRoadNetwork();
        return RoadTerrainModifier.FinishedGround((x, z) => BiomeBlendedHeight.GetBlendedHeight(x, z, world));
    }

    /// <summary>Tower 2's start: a flight turned a quarter off a road climbing to its end. Without a pad the ground under its
    /// first stair follows the slope (more than 0.15 m off its foot); with it, within 0.15 m of level all over.</summary>
    [Fact]
    public void ATurnedFootOnASlopingRoadEndStandsOnALevelPad()
    {
        World world = Slope();
        Road(world);
        StairPlan plan = FootFlight(90f, 90f);
        float before = WorstOffLevel(Finished(world), plan, H);
        Assert.True(Mathf.Abs(before) > 0.15f, $"the ground under the first stair was already level: {before:F2} m");

        RoadEndRing.Pad? pad = RoadEndRing.PadUnder(plan, (x, z) => Ground(world, x, z), 8f, out string why);
        Assert.True(pad != null, why);
        Assert.False(pad!.Top);
        Assert.Equal(End, pad.End);
        Assert.Equal(H, pad.Height);
        Assert.Equal(1, pad.Pieces);
        // The road's end (half its width round it) and the first stair, nothing of the flight above it.
        Assert.Contains(pad.Points, p => Vector2.Distance(p, End) < 0.1f);
        Assert.Contains(pad.Points, p => p.x > 3f);
        Assert.DoesNotContain(pad.Points, p => p.x > 3.6f);
        RoadSpatialGrid.CommitLevelled(pad.Points, pad.Heights, RoadEndRing.PadPointWidth, unpainted: true);
        float after = WorstOffLevel(Finished(world), plan, H);
        Assert.True(Mathf.Abs(after) <= 0.15f, $"{after:F2} m off level under the first stair");
    }

    /// <summary>A flight setting off straight on from the front of the ring: the pad runs on under its first stair.</summary>
    [Fact]
    public void AStraightOnStartStandsOnALevelPad()
    {
        World world = Slope(east: 0.5f);
        Road(world);
        StairPlan plan = FootFlight(0f, 0f);
        RoadEndRing.Pad? pad = RoadEndRing.PadUnder(plan, (x, z) => Ground(world, x, z), 8f, out string why);
        Assert.True(pad != null, why);
        Assert.Contains(pad!.Points, p => p.y > 3f);
        Assert.DoesNotContain(pad.Points, p => p.y > 3.6f);
        RoadSpatialGrid.CommitLevelled(pad.Points, pad.Heights, RoadEndRing.PadPointWidth, unpainted: true);
        float after = WorstOffLevel(Finished(world), plan, H);
        Assert.True(Mathf.Abs(after) <= 0.15f, $"{after:F2} m off level under the first stair");
    }

    /// <summary>Floors at the road's level before the first stair (a pad of floors at the foot): the pad lies under them, a
    /// little under their top (a floor with the ground at its top all over is not placed, BuriedPieces), and under the stair
    /// after them.</summary>
    [Fact]
    public void FloorsAtTheFootAndTheStairAfterThemArePadded()
    {
        World world = Slope();
        var ring = new RoadEndRing(End, 0f, H);
        Vector2 start = ring.ArcPoint(0f);
        var plan = new StairPlan { StartHeading = 0f, Reached = true, Ring = ring, RingStart = start, Foot = new Vector3(start.x, H, start.y), StartsOnFloor = true };
        plan.Pieces.Add(Floor(start + new Vector2(0f, 1f), H, 0f));
        plan.Pieces.Add(Floor(start + new Vector2(0f, 3f), H, 0f));
        for (int i = 0; i < 4; i++) plan.Pieces.Add(Stair(start + new Vector2(1f + 2f * i, 5f), H + i, 90f));
        RoadEndRing.Pad? pad = RoadEndRing.PadUnder(plan, (x, z) => Ground(world, x, z), 8f, out string why);
        Assert.True(pad != null, why);
        Assert.Equal(3, pad!.Pieces);
        Assert.Contains(pad.Heights, h => Mathf.Abs(h - (H - RoadEndRing.PadUnderFloor)) < 1e-3f);
        // Under the floors a little under their top, under the stair at its foot.
        for (int i = 0; i < pad.Points.Count; i++)
            if (pad.Points[i].y > 2.2f && pad.Points[i].y < 5f && pad.Points[i].x < 0.8f) Assert.Equal(H - RoadEndRing.PadUnderFloor, pad.Heights[i], 3);
        Assert.Contains(pad.Points, p => p.x > 1.5f && p.y > 7f);
        Assert.DoesNotContain(pad.Points, p => p.x > 2.1f || p.y > 7.6f);
    }

    /// <summary>A flight going down from the road's end (an approach to a location below): its top's floor, its exit edge on the
    /// ring, is padded with the road's end; the flight going down from it is not (no fill up to it).</summary>
    [Fact]
    public void ATopGoingDownFromTheRoadsEndStandsOnALevelPad()
    {
        // The road climbs north to its end; past it the ground falls away north at 0.3.
        var world = new World { Height = (x, z) => H + (z < 0f ? 0.3f * z : -0.3f * z) };
        _scope.WithWorld(world);
        Road(world);
        var ring = new RoadEndRing(End, 0f, H);
        // Laid from the bottom up, heading south toward the road: four stairs, then the top floor whose exit edge is on the ring.
        var plan = new StairPlan { Reversed = true, Reached = true, Ring = ring, StartHeading = 180f, EndHeading = 180f, End = new Vector3(0f, H, ring.Radius) };
        for (int i = 0; i < 4; i++) plan.Pieces.Add(Stair(new Vector2(0f, ring.Radius + 2f + 2f * (4 - i) - 1f), H - 4f + i, 180f));
        plan.Pieces.Add(Floor(new Vector2(0f, ring.Radius + 1f), H, 180f));
        Assert.True(RoadEndRing.MetAtTop(plan));
        RoadEndRing.Pad? pad = RoadEndRing.PadUnder(plan, (x, z) => Ground(world, x, z), 8f, out string why);
        Assert.True(pad != null, why);
        Assert.True(pad!.Top);
        Assert.Equal(1, pad.Pieces);
        Assert.Contains(pad.Points, p => p.y > ring.Radius + 1.8f);
        Assert.DoesNotContain(pad.Points, p => p.y > ring.Radius + 2.1f);
        RoadSpatialGrid.CommitLevelled(pad.Points, pad.Heights, RoadEndRing.PadPointWidth, unpainted: true);
        Func<int, int, float> finished = Finished(world);
        // The road's end and under the floor (its centre half a metre each way): level with the floor's pad.
        foreach (var (x, z) in new[] { (0, 0), (0, 1), (-1, 1), (1, 1), (0, 2), (0, 3), (-1, 3), (1, 3) })
            Assert.True(Mathf.Abs(finished(x, z) - H) <= RoadEndRing.PadUnderFloor + 0.15f, $"({x}, {z}) at {finished(x, z):F2}");
    }

    /// <summary>No pad where it would take earthwork past the road's cap (8 m): the road is left as it would be without one.
    /// Nor is a ridge filled up to the stair: off the road's end, ground more than a metre under the pad is left as it is.</summary>
    [Fact]
    public void NoPadPastTheEarthworkCapAndNoRidgeFilledUpToTheStair()
    {
        StairPlan plan = FootFlight(90f, 90f);
        // A wall 9 m over the road's end under the first stair.
        Assert.Null(RoadEndRing.PadUnder(plan, (x, z) => x > 2.5f ? H + 9f : H, 8f, out string why));
        Assert.StartsWith(RoadEndRing.PastTheCap, why);
        // Still none with ground left out elsewhere under it: the cap is the whole pad's.
        Assert.Null(RoadEndRing.PadUnder(plan, (x, z) => x > 2.5f ? (z > 0f ? H + 9f : H - 1.5f) : H, 8f, out why));
        Assert.StartsWith(RoadEndRing.PastTheCap, why);
        // The ground falling 1.5 m away under its far half: a fill over a metre off the road's end, left as it is there, and
        // a lattice step short of it (a pad point's flat core would level that too); the road's end and the stair's near
        // edge padded.
        RoadEndRing.Pad? partial = RoadEndRing.PadUnder(plan, (x, z) => x > 2.5f ? H - 1.5f : H, 8f, out why);
        Assert.True(partial != null, why);
        Assert.True(partial!.LeftOut > 0);
        Assert.DoesNotContain(partial.Points, p => p.x > 2.5f - RoadEndRing.PadSpacing + 1e-3f);
        Assert.Contains(partial.Points, p => p.x > 2f - 1e-3f && p.sqrMagnitude > 2f * 2f);
        Assert.True(partial.MostFill <= RoadEndRing.PadMostFill);
        // Within both, it is laid.
        Assert.NotNull(RoadEndRing.PadUnder(plan, (x, z) => x > 2.5f ? H + 7f : H, 8f, out why));
        Assert.NotNull(RoadEndRing.PadUnder(plan, (x, z) => x > 2.5f ? H - 0.9f : H, 8f, out why));
        // No ring, no pad.
        plan.Ring = null;
        Assert.Null(RoadEndRing.PadUnder(plan, (x, z) => H, 8f, out _));
    }

    /// <summary>The network's side (RoadStairRuns.StartPads): a pad only where one of the road's own pieces ends at its road's
    /// end at its height, stored unpainted; none on another road at another height.</summary>
    /// <summary>The slope of <see cref="Slope"/> rendered over a Swamp patch round the road's end that the generator reads
    /// at 29 m (its heightmap's corners are all Mountain, so the game renders the slope there): GetHeight is the point's
    /// biome's formula, as the game's is.</summary>
    private sealed class SwampPatch : WorldGenerator
    {
        private static bool InPatch(float x, float z) => Mathf.Abs(x) < 20f && Mathf.Abs(z) < 20f;
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) =>
            InPatch(x, z) ? Heightmap.Biome.Swamp : Heightmap.Biome.Mountain;
        public override float GetBiomeHeight(Heightmap.Biome biome, float x, float z, out Color mask, bool preGeneration = false, bool riverPreDN = true)
        {
            mask = default;
            return biome == Heightmap.Biome.Swamp ? 29f : H + 0.3f * z + 0.2f * Mathf.Max(0f, x - 1f);
        }
        public override float GetHeight(float x, float z) => GetBiomeHeight(GetBiome(x, z), x, z, out _);
        public override void GetRiverWeight(float x, float z, out float weight, out float width) { weight = 0f; width = 0f; }
    }

    /// <summary>The pad judges water as every road water test does (RoadWater.BuiltDry on the ground as it will be): on
    /// ground the generator reads at 29 m that the game renders dry it is laid. In game, both start pads of the
    /// staircase (4288,-3528) -> (4296,-3512) were refused "in water" on dry land the generator read as Swamp under the
    /// shallow line.</summary>
    [Fact]
    public void APadOnGroundTheGeneratorReadsAsWaterButIsRenderedDryIsLaid()
    {
        var world = new SwampPatch();
        _scope.WithWorld(world);
        Assert.True(world.GetHeight(End.x, End.y) < RoadConstants.ShallowWaterHeight);
        Assert.True(RoadWater.BuiltDry(world, End));
        StairPlan plan = FootFlight(90f, 90f);
        var span = new RoadStairSpan { FromPoint = End, FromHeight = H, ToPoint = new Vector2(11.5f, 0f), ToHeight = H + 5f, Kit = StairKit.Stone, Plan = plan };
        var path = Enumerable.Range(0, 41).Select(k => new Vector2(0f, -40f + k)).ToList();
        RoadSpatialGrid.PlannedPath road = RoadSpatialGrid.PlanRoadPath(path, RoadNetworkGenerator.RoadWidth, world, null, H)!;
        Assert.NotNull(road);
        RoadStairRuns.PadsRefused.Clear();
        RoadSpatialGrid.PlannedPath laid = Assert.Single(RoadStairRuns.StartPads(new[] { road }, new[] { span }, world));
        Assert.All(laid.Heights, h => Assert.Equal(H, h));
        Assert.False(RoadStairRuns.PadsRefused.ContainsKey("in water"));
    }

    [Fact]
    public void APadIsLaidOnlyWhereItsRoadEndsThere()
    {
        World world = Slope();
        StairPlan plan = FootFlight(90f, 90f);
        var span = new RoadStairSpan { FromPoint = End, FromHeight = H, ToPoint = new Vector2(11.5f, 0f), ToHeight = H + 5f, Kit = StairKit.Stone, Plan = plan };
        var path = Enumerable.Range(0, 41).Select(k => new Vector2(0f, -40f + k)).ToList();
        RoadSpatialGrid.PlannedPath road = RoadSpatialGrid.PlanRoadPath(path, RoadNetworkGenerator.RoadWidth, world, null, H)!;
        var elsewhere = RoadSpatialGrid.PlanRoadPath(path.ConvertAll(p => p + new Vector2(30f, 0f)), RoadNetworkGenerator.RoadWidth, world, null, H)!;

        Assert.Empty(RoadStairRuns.StartPads(new[] { elsewhere }, new[] { span }, world));

        RoadSpatialGrid.PlannedPath laid = Assert.Single(RoadStairRuns.StartPads(new[] { road }, new[] { span }, world));
        Assert.NotNull(laid.Unpainted);
        Assert.All(laid.Unpainted!, u => Assert.True(u));
        Assert.All(laid.Heights, h => Assert.Equal(H, h));

        // Another road across its end 2 m over it: none.
        RoadSpatialGrid.CommitLevelled(new[] { new Vector2(-6f, 0.5f), new Vector2(-3f, 0.5f), new Vector2(0f, 0.5f), new Vector2(3f, 0.5f) },
            new[] { H + 2f, H + 2f, H + 2f, H + 2f }, RoadNetworkGenerator.RoadWidth);
        Assert.Empty(RoadStairRuns.StartPads(new[] { road }, new[] { span }, world));
    }

    /// <summary>
    /// A road staircase's top, where its road starts again a ring's radius out from its exit (RoadEndPast), is padded too:
    /// it is laid against the ring at its foot only, so its top had none. The top 2x2 of a small staircase down to the
    /// south-west, its surface at 42.78, stood 0.37 m over the road at (-4564.2, 42.41, 2167.6), too high to step onto: the
    /// road's grade (0.33 up to the north-east) carried on past its end at (-4563.5, 2166.8) over the 1.5 m to the floor.
    /// The road points and pieces as laid there.
    /// </summary>
    [Fact]
    public void ARoadStaircasesTopIsPaddedWhereItsRoadStartsAgain()
    {
        var points = new List<Vector2>
        {
            new(-4563.5f, 2166.8f), new(-4562.8f, 2166.2f), new(-4562.2f, 2165.7f), new(-4561.5f, 2165.1f), new(-4560.9f, 2164.5f),
            new(-4560.3f, 2163.9f), new(-4559.6f, 2163.3f), new(-4559.0f, 2162.7f), new(-4558.2f, 2162.1f), new(-4557.4f, 2161.5f),
            new(-4556.6f, 2160.9f), new(-4555.7f, 2160.4f),
        };
        var heights = new List<float> { 42.8f, 43.1f, 43.4f, 43.7f, 44.0f, 44.3f, 44.6f, 44.9f, 45.3f, 45.6f, 46.0f, 46.5f };
        Vector2 end = points[0], floorAt = new(-4565.232f, 2168.601f), stood = new(-4564.2f, 2167.6f);
        Vector2 up = StairLayout.Forward(135f);
        // The ground falling south-west, the way the staircase goes down.
        var world = new World { Height = (x, z) => 42.8f + 0.4f * Vector2.Dot(new Vector2(x, z) - end, up) };
        _scope.WithWorld(world);
        float top = 42.277f + StairKit.Stone.FloorTop;
        Vector2 exit = floorAt + up * (StairLayout.PieceRun / 2f);
        Assert.Equal(new RoadEndRing(end, 315f, 42.8f).Radius, Vector2.Distance(exit, end), 1);
        // Laid up from its foot's ring to the south-west: its first stair there, then (as laid) the last flight's stair and the
        // top floor.
        Vector2 foot = new(-4572.9f, 2180.3f);
        var plan = new StairPlan
        {
            Reached = true, StartHeading = 135f, EndHeading = 135f, End = new Vector3(exit.x, top, exit.y),
            Ring = new RoadEndRing(foot - up * 1.5f, 135f, 36.8f), RingStart = foot,
        };
        plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Stair, Prefab = "stone_stair", Position = new Vector3(-4566.646f, 41.777f, 2170.016f), YawDegrees = 315f });
        plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Landing, Prefab = "stone_floor_2x2", Position = new Vector3(floorAt.x, 42.277f, floorAt.y), YawDegrees = 135f });
        Assert.False(RoadEndRing.MetAtTop(plan));
        var span = new RoadStairSpan { FromPoint = foot - up * 1.5f, FromHeight = 36.8f, ToPoint = end, ToHeight = 42.8f, Kit = StairKit.Stone, Plan = plan };
        var road = new RoadSpatialGrid.PlannedPath(points, heights, points.ConvertAll(_ => default(RoadPointDebugInfo)), RoadNetworkGenerator.RoadWidth, 11f, false);
        List<RoadSpatialGrid.PlannedPath> pads = RoadStairRuns.StartPads(new[] { road }, new[] { span }, world);

        // The road alone: its grade carried on past its end, the floor's edge over a step-up above it where the walker comes off the road.
        RoadSpatialGrid.CommitLevelled(points, heights, RoadNetworkGenerator.RoadWidth);
        float Under(Func<int, int, float> finished, Vector2 p)
        {
            int x0 = Mathf.FloorToInt(p.x), z0 = Mathf.FloorToInt(p.y);
            float fx = p.x - x0, fz = p.y - z0;
            return Mathf.Lerp(Mathf.Lerp(finished(x0, z0), finished(x0 + 1, z0), fx), Mathf.Lerp(finished(x0, z0 + 1), finished(x0 + 1, z0 + 1), fx), fz);
        }
        float bare = top - Under(Finished(world), stood);
        Assert.True(bare > StairLayout.StepUp, $"the floor already within a step-up of the road: {bare:F2} m");

        // Padded at its top: from the road's end to the floor's edge the ground stands within a step-up under its surface.
        RoadSpatialGrid.PlannedPath pad = Assert.Single(pads);
        Assert.All(pad.Heights, h => Assert.True(h <= top + 1e-3f));
        RoadSpatialGrid.CommitLevelled(pad.Points, pad.Heights, pad.Width, unpainted: true);
        Func<int, int, float> padded = Finished(world);
        float step = top - Under(padded, stood);
        Assert.True(Mathf.Abs(step) <= StairLayout.StepUp, $"{step:F2} m from the road onto the floor where the walker comes off the road");
        for (float along = 0f; along <= 1.5f + 1e-3f; along += 0.25f)
        {
            float there = top - Under(padded, end - up * along);
            Assert.True(Mathf.Abs(there) <= StairLayout.StepUp, $"{there:F2} m from the road onto the floor {along:F2} m past the road's end");
        }
    }

    /// <summary>
    /// A pad is judged as every road's earthwork is (RoadNetworkGenerator.EarthworkAcross, its points as stored, each
    /// RoadEndRing.PadPointWidth wide): the writer levels the vertex half a metre past its outermost lattice points to the
    /// pad's height too, so a wall standing there, 9 m over the pad, is cut past the cap. Its lattice points alone all stand
    /// on level ground, and the lattice check laid it.
    /// </summary>
    [Fact]
    public void APadWhoseFlatCoreReachesAWallPastItsLatticeIsNotLaid()
    {
        StairPlan plan = FootFlight(90f, 90f);
        RoadEndRing.Pad? level = RoadEndRing.PadUnder(plan, (x, z) => H, 8f, out string why);
        Assert.True(level != null, why);
        float edge = level!.Points.Max(p => p.x);
        Assert.Equal(0.5f, edge - Mathf.Floor(edge));
        // The wall begins a quarter metre past the pad's last lattice column: no lattice point stands on it, the vertex half
        // a metre out (within a pad point's flat core) does.
        float Wall(float x, float z) => x > edge + 0.25f ? H + 9f : H;
        Assert.All(level.Points, p => Assert.Equal(H, Wall(p.x, p.y)));

        Assert.Null(RoadEndRing.PadUnder(plan, Wall, 8f, out why));
        Assert.StartsWith(RoadEndRing.PastTheCap, why);
    }

    /// <summary>A start pad is decided by the one owner for a generated road (RoadStairRuns.StartPads, as
    /// RoadNetworkGenerator.GenerateRoadCore lays it) and a manual road's append (RoadNetworkGenerator.AppendOf) alike, with
    /// the mod's cap: none where the writer would cut it past the cap, the same pad where it would not.</summary>
    [Fact]
    public void APadIsLaidOrRefusedAlikeForAGeneratedRoadAndAManualRoad()
    {
        StairPlan plan = FootFlight(90f, 90f);
        float edge = RoadEndRing.PadUnder(plan, (x, z) => H, 8f, out _)!.Points.Max(p => p.x);
        float cap = RoadNetworkGenerator.EarthworkCap;
        RoadNetworkGenerator.EarthworkCap = RoadNetworkGenerator.DefaultEarthworkCap;
        try
        {
            foreach (float wall in new[] { 0f, 9f })
            {
                var world = new World { Height = (x, z) => x > edge + 0.25f ? H + wall : H };
                _scope.WithWorld(world);
                RoadSpatialGrid.Clear();
                var span = new RoadStairSpan { FromPoint = End, FromHeight = H, ToPoint = new Vector2(11.5f, 0f), ToHeight = H + 5f, Kit = StairKit.Stone, Plan = plan };
                var path = Enumerable.Range(0, 41).Select(k => new Vector2(0f, -40f + k)).ToList();
                RoadSpatialGrid.PlannedPath road = RoadSpatialGrid.PlanRoadPath(path, RoadNetworkGenerator.RoadWidth, world, null, H)!;
                Assert.NotNull(road);
                var own = new List<RoadSpatialGrid.PlannedPath> { road };
                List<RoadSpatialGrid.PlannedPath> generated = RoadStairRuns.StartPads(own, new[] { span }, world);
                var appended = RoadNetworkGenerator.AppendOf(own, new List<RoadCrossing>(), new List<RoadStairSpan> { span }).WithFills
                    .Where(p => p.Unpainted != null && p.Unpainted.All(u => u)).ToList();
                Assert.Equal(wall > 8f ? 0 : 1, generated.Count);
                Assert.Equal(generated.Count, appended.Count);
                for (int i = 0; i < generated.Count; i++)
                {
                    Assert.Equal(generated[i].Points, appended[i].Points);
                    Assert.Equal(generated[i].Heights, appended[i].Heights);
                }
            }
        }
        finally { RoadNetworkGenerator.EarthworkCap = cap; }
    }
}
