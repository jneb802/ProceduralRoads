using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// A staircase's kit chooses its supports on the ground its layout was laid out on (RoadStairRuns.PlanningGround, one
/// owner), so the search and the first build choose the same: a road's staircase on the 1 m lattice, an approach on the
/// ground its location is expected to leave.
/// </summary>
public sealed class StairPlanningGroundTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    /// <summary>A slope rising west of x -1.2 to 50 m; east of it the ground at 20 m on every whole metre of x and
    /// 2 m lower half way between: the 1 m lattice a road's staircase is laid out on reads 20 m everywhere there.</summary>
    private sealed class RidgedWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy)
        {
            if (wx < -1.2f) return 50f;
            float frac = wx - Mathf.Floor(wx);
            return 20f - 2f * (1f - 2f * Mathf.Abs(frac - 0.5f));
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 78;
    }

    /// <summary>Level ground at 20 m.</summary>
    private sealed class LevelWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => 20f;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 79;
    }

    public StairPlanningGroundTests()
    {
        RoadNetworkGenerator.Reset();
        PlacedLocations.Reset();
    }

    public void Dispose()
    {
        LocationLevelling.Source = null;
        PlacedLocations.Reset();
        RoadLocationProtection.Reset();
        RoadNetworkGenerator.Reset();
        _scope.Dispose();
    }

    /// <summary>One stone stair climbing +z, its back edge's middle at (<paramref name="x"/>, 0), underside at
    /// <paramref name="underside"/>.</summary>
    private static StairRun OneStair(float x, float underside)
    {
        var run = new StairRun { From = new Vector2(x, 0f), To = new Vector2(x, 2f), Width = RoadStairRuns.Width, Kit = StairKit.Stone };
        run.Pieces.Add(new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = StairKit.Stone.Stair, Position = new Vector3(x, underside, 1f), YawDegrees = 180f });
        return run;
    }

    /// <summary>Ground that differs at every lattice point: x and z each weighted differently, and a wave.</summary>
    private sealed class WavyWorld : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => 30f + 0.37f * wx - 0.21f * wy + 3f * Mathf.Sin(wx * 0.7f) * Mathf.Cos(wy * 1.3f);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 80;
    }

    [Fact]
    public void TheLatticeIsTheGroundAtItsMetresJoinedAsTheGamesMeshJoinsThemAcrossTileEdges()
    {
        // The lattice a staircase is laid out on reads each cell's four corners from the ground as locations leave it and joins
        // them as the game's mesh does, whichever tiles they fall in: in the middle of a tile, along its last row or column,
        // across its corner, and below zero.
        var world = new WavyWorld();
        _scope.WithWorld(world);
        RoadStairRuns.ForgetGround();
        float Corner(int i, int k) => PlacedLocations.GroundAt(i, k, world);
        foreach (float x in new[] { 5.3f, 31.4f, 31.999f, 32f, 63.7f, -0.6f, -1f, -32.5f, -33.25f, 1000.75f })
            foreach (float z in new[] { 7.8f, 31.6f, -0.2f, -31.9f, -64.5f, 95.01f })
            {
                float expected = BuriedPieces.MeshHeight(x, z, Corner);
                Assert.Equal(expected, RoadStairRuns.GroundAt(x, z, world));
            }
    }

    [Fact]
    public void ARoadsStaircaseIsHeldUpOnTheLatticeItWasLaidOutOn()
    {
        // The stair's downhill (east) edge, at x 1.5, stands 3.5 m over the lattice there, a wall's height (the search
        // prices it as walled); over the continuous ground half way between whole metres it stands 5.5 m. Chosen on that,
        // the kit hung an arch and corbels off the slope where the search had priced a wall.
        var world = new RidgedWorld();
        _scope.WithWorld(world);
        StairRun run = OneStair(0.5f, 23.5f);
        StairSupport.Walk w = StairSupport.WalkOf(run.Pieces[0], StairKit.Stone)!.Value;
        Assert.Equal(StairSupport.Style.Wall, StairSupport.Choose(StairKit.Stone, w, (x, z) => RoadStairRuns.GroundAt(x, z, world)).Style);
        Assert.Equal(StairSupport.Style.Arch, StairSupport.Choose(StairKit.Stone, w, (x, z) => world.GetHeight(x, z)).Style);

        List<StructurePiece> dressed = StairStructures.Dresser(run, world);
        Assert.DoesNotContain(dressed, p => p.Prefab == "stone_arch");
        Assert.Contains(dressed, p => p.Kind == StructurePieceKind.StairSupport && p.Prefab.StartsWith("stone_wall_"));
    }

    [Fact]
    public void AnApproachIsHeldUpOnTheGroundItsLocationIsExpectedToLeave()
    {
        // A location at the origin, placed after its roads, levelling a flat 4 m over the level ground out to 10 m. An
        // approach's top stair at x 7 stands on that flat (its underside on it), 4 m over the natural ground: chosen on
        // the natural ground it was walled down through the fill; on the ground it was laid out on, it needs nothing. (The
        // finished ground here is the natural ground, the location not being one the writer knows: the kit, finding the stair
        // falling as chosen, holds it again on that ground, StairKit.Dress.)
        var world = new LevelWorld();
        _scope.WithWorld(world);
        var centre = new Vector2(0f, 0f);
        IReadOnlyList<LevelOp> ops = new[] { new LevelOp(0f, 0f, 4f, 10f, false, 14f, 3f) };
        LocationLevelling.Source = c => Vector2.Distance(c, centre) < 0.01f ? ops : null;
        StairRun run = OneStair(7f, 24f);
        run.KeepsLocationRocks = true;
        Assert.Contains(KitDressedOnNatural(run, world), p => p.Kind == StructurePieceKind.StairSupport);
        run.ExpectedLocation = (centre, 20f);
        StairSupport.Walk w = StairSupport.WalkOf(run.Pieces[0], StairKit.Stone)!.Value;
        Assert.Equal(StairSupport.Style.Ground, StairSupport.Choose(StairKit.Stone, w, RoadStairRuns.PlanningGround(run, world)).Style);
        Assert.Equal(24f, RoadStairRuns.PlanningGround(run, world)(7f, 1f), 3);
    }

    [Fact]
    public void AWayOntoAPlacedLocationsLandingIsHeldUpOnTheGroundItWasLaidOutOnPastTheFootprint()
    {
        // A location placed before its roads at (12, 0): its footprint 6 m round it, its grid out to x -10, its flat level with
        // the lattice. The stair of the road's staircase test stands between the footprint's edge and the grid's, as a way
        // onto the landing's flight up the slope just outside a Mountain tower's footprint does. It was searched on the
        // location's own grid, the lattice's metres there (a wall, 3.5 m over it at its downhill edge), and dressed on the
        // continuous ground past the footprint (5.5 m: an arch): what the search had priced, the kit did not lay.
        var world = new RidgedWorld();
        _scope.WithWorld(world);
        var centre = new Vector2(12f, 0f);
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(centre, 6f) });
        PlacedLocations.Remember(new PlacedLocationRecord
        {
            Prefab = "StoneTowerRuins05", Centre = centre, Root = 20f, Ops = new List<LevelOp> { new LevelOp(0f, 0f, 0f, 3f, false, 0f, 0f) },
            X0 = -10, Z0 = -10, W = 40, H = 21,
        });
        PlacedLocations.Prepare((x, z) => BiomeBlendedHeight.GetBlendedHeight(x, z, world));
        Assert.True(PlacedLocations.TryGetReady(centre, out PlacedLocation location));
        StairRun run = OneStair(0.5f, 23.5f);
        run.KeepsLocationRocks = true;
        Assert.Null(PlacedLocations.GroundAt(new Vector2(1.5f, 1f)));

        Func<float, float, float> planning = RoadStairRuns.PlanningGround(run, world);
        foreach (float x in new[] { 0.5f, 1f, 1.5f, 2f, 2.5f })
            Assert.Equal(location.Ground(x, 1f), planning(x, 1f), 3);
        StairSupport.Walk w = StairSupport.WalkOf(run.Pieces[0], StairKit.Stone)!.Value;
        Assert.Equal(StairSupport.Style.Wall, StairSupport.Choose(StairKit.Stone, w, location.Ground).Style);
        List<StructurePiece> dressed = StairStructures.Dresser(run, world);
        Assert.DoesNotContain(dressed, p => p.Prefab == "stone_arch");
        Assert.Contains(dressed, p => p.Kind == StructurePieceKind.StairSupport && p.Prefab.StartsWith("stone_wall_"));
    }

    [Fact]
    public void AnApproachOnALocationsGroundNotKnownIsNotLaidAndSaysSo()
    {
        // A location at the origin, its ground not published, cutting a flat 4 m into the level ground out to 10 m, its footprint
        // 12 m round it. An approach's stair at x 7 stands 3 m over that cut. The finished ground is not known inside the
        // footprint: a bug under the preload-list rule. The staircase lays and saves nothing, said as an error, never laid on
        // the ground the location was expected to leave in its stead; nothing waits for that ground.
        var world = new LevelWorld();
        _scope.WithWorld(world);
        var centre = new Vector2(0f, 0f);
        IReadOnlyList<LevelOp> ops = new[] { new LevelOp(0f, 0f, -4f, 10f, false, 14f, 3f) };
        LocationLevelling.Source = c => Vector2.Distance(c, centre) < 0.01f ? ops : null;
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(centre, 12f) });
        StairRun run = OneStair(7f, 19f);
        run.KeepsLocationRocks = true;
        run.ExpectedLocation = (centre, 20f);
        StairStructures.Reset();
        StairStructures.Add(run);
        try
        {
            Assert.True(float.IsNaN(StructureGround.Vertices(world)(7, 1)));
            var lines = new List<string>();
            BepInEx.Logging.ManualLogSource.Captured = lines;
            try { Assert.Empty(StairStructures.Build(run, world)); }
            finally { BepInEx.Logging.ManualLogSource.Captured = null; }
            Assert.Null(BuiltStairs.Of(run));
            Assert.True(lines.Exists(l => l.Contains("[STAIRS] staircase") && l.Contains("not laid") && l.Contains("not known")), string.Join(" | ", lines));
            // Its ends are not verified on ground not known, never read off the ground it was laid out on.
            Assert.Contains(StairEnds.Read(run, StairEnds.GroundOf(run, world)), e => e.Verdict == StairEnds.Verdict.NotVerified && e.Why.Contains("not known"));

        }
        finally
        {
            StairStructures.Reset();
        }
    }

    private static List<StructurePiece> KitDressedOnNatural(StairRun run, WorldGenerator world) =>
        StairKit.Stone.Dress(run, (x, z) => world.GetHeight(x, z), StructureGround.Vertices(world), false);

    [Fact]
    public void AnApproachsLocationIsSavedWithIt()
    {
        StairRun run = OneStair(7f, 26f);
        run.KeepsLocationRocks = true;
        run.ExpectedLocation = (new Vector2(3f, -4f), 21.5f);
        StairStructures.Reset();
        StairStructures.Add(run);
        StairRun read = StairStructures.Parse(StairStructures.Serialize()).Single();
        Assert.Equal(run.ExpectedLocation, read.ExpectedLocation);
        StairStructures.Reset();
        StairStructures.Add(OneStair(0f, 10f));
        Assert.Null(StairStructures.Parse(StairStructures.Serialize()).Single().ExpectedLocation);
        StairStructures.Reset();
    }
}
