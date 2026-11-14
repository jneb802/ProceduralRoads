using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// What holds a staircase up (StairSupport, one owner), against hand-built 2x2 prototypes: a wall in
/// running bond under the downhill half up to 4.5 m, an arch off the slope above that, corbels out to about 6 m, refused
/// past it, and black marble's stepped cornices down to the ground. The search prices a piece by the same choice the kit
/// lays at first build.
/// </summary>
public class StairSupportTests
{
    private static readonly StairKit Stone = StairKit.Stone, Marble = StairKit.BlackMarble;

    /// <summary>A stair climbing +z (heading 0) from (0, 0) with its underside at <paramref name="base_"/>.</summary>
    private static StairSupport.Walk Stair(float base_, float z = 0f) => new(new Vector2(0f, z), 0f, 2f, 2f, base_, true, 0f);

    /// <summary>Ground rising to the west (-x, the left of a stair climbing +z): flat at <paramref name="low"/> east of
    /// <paramref name="wallAt"/>, a face rising <paramref name="grade"/> a metre westward from it.</summary>
    private static Func<float, float, float> Wall(float low, float wallAt, float grade) =>
        (x, z) => x > wallAt ? low : low + grade * (wallAt - x);

    /// <summary>Flat at 10 east of <paramref name="cliffAt"/>, a cliff to 100 west of it.</summary>
    private static Func<float, float, float> Cliff(float cliffAt) => (x, z) => x > cliffAt ? 10f : 100f;

    [Fact]
    public void APieceOnTheGroundNeedsNothing()
    {
        // Sunk to its top: nothing laid, nothing to price.
        StairSupport.Choice sunk = StairSupport.Choose(Stone, Stair(10f), (x, z) => 11f);
        Assert.Equal(StairSupport.Style.Ground, sunk.Style);
        Assert.Equal(0f, sunk.Price);
        // Its underside in the ground, its upper steps out of it: still nothing laid, but priced as every piece up to the wall
        // height is, by the mean air under its walking surface (as columns were): over its 9 samples only the 3 at 1.5 m
        // along stand out, by 0.25 m.
        StairSupport.Choice c = StairSupport.Choose(Stone, Stair(10f), (x, z) => 10.5f);
        Assert.Equal(StairSupport.Style.Ground, c.Style);
        Assert.Equal(StairSupport.WallPrice * 3f * 0.25f / 9f, c.Price, 4);
    }

    /// <summary>The search chooses for every piece it tries: one whose edges are both in the ground is priced by the air
    /// under its walking surface alone, without reading the slope out to the corbels' reach or the face's grade.</summary>
    [Fact]
    public void APieceWithBothEdgesInTheGroundReadsOnlyItsOwnGround()
    {
        int reads = 0;
        StairSupport.Choice c = StairSupport.Choose(Stone, Stair(10f), (x, z) => { reads++; return 10.5f; });
        Assert.Equal(StairSupport.Style.Ground, c.Style);
        Assert.Equal(StairSupport.WallPrice * 3f * 0.25f / 9f, c.Price, 4);
        // Its two edges and the three samples across its walking surface, at each of three rows.
        Assert.Equal(15, reads);
    }

    [Theory]
    [InlineData(4f, StairSupport.Style.Wall)]
    [InlineData(4.5f, StairSupport.Style.Wall)]
    [InlineData(5f, StairSupport.Style.Arch)]
    public void StoneIsWalledUpToFourAndAHalfMetresOverItsDownhillEdge(float height, StairSupport.Style style)
    {
        // The slope rises west: wall side left (-1), the downhill (east) edge `height` over the ground; the west edge is in
        // the slope, so an arch there is sunk.
        StairSupport.Choice c = StairSupport.Choose(Stone, Stair(10f), Wall(10f - height, 0f, 10f));
        Assert.Equal(style, c.Style);
        Assert.Equal(-1, c.Side);
        Assert.False(c.Refused);
    }

    [Fact]
    public void AStairTooHighForAWallIsHeldByAnArchOrCorbelsOutToSixMetresThenRefused()
    {
        // 20 m of air under the east edge; the cliff further and further west. The arch's tall end is 1 m out from the
        // stair's centre line, each corbel course's 2 m further: 3 and 5 m.
        StairSupport.Choice arch = StairSupport.Choose(Stone, Stair(30f), Cliff(-0.9f));
        Assert.Equal(StairSupport.Style.Arch, arch.Style);
        Assert.Equal(-1, arch.Side);
        Assert.Equal(1, arch.Courses);
        StairSupport.Choice corbel = StairSupport.Choose(Stone, Stair(30f), Cliff(-2.9f));
        Assert.Equal(2, corbel.Courses);
        Assert.True(corbel.Price > arch.Price);
        StairSupport.Choice far = StairSupport.Choose(Stone, Stair(30f), Cliff(-4.9f));
        Assert.Equal(3, far.Courses);
        Assert.False(far.Refused);
        StairSupport.Choice refused = StairSupport.Choose(Stone, Stair(30f), Cliff(-6.9f));
        Assert.True(refused.Refused);
    }

    [Fact]
    public void ABlockAndAnArchAreLaidUnderBothHalvesOfTheStairWithTheirTallEndsInTheSlope()
    {
        var stair = new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = Stone.Stair, Position = new Vector3(0f, 30f, 1f), YawDegrees = 180f };
        Func<float, float, float> ground = Cliff(-0.9f);
        List<StructurePiece> laid = StairSupport.Lay(Stone, new List<StructurePiece> { stair }, ground, (x, z) => ground(x, z));
        // The hand-built stone prototype: a 2x1 block 0.5 m under the stair and an arch under it; under both
        // halves (z 0..1 and 1..2 of a stair climbing +z from z 0), so the whole 2 x 2 stair is held. The stair's
        // underside is flat (its bottom snap points are at its foot's height at both ends), so both blocks meet it.
        var blocks = laid.Where(p => p.Prefab == "stone_wall_2x1").OrderBy(p => p.Position.z).ToList();
        var arches = laid.Where(p => p.Prefab == "stone_arch").OrderBy(p => p.Position.z).ToList();
        Assert.Equal(new[] { new Vector3(0f, 29.5f, 0.5f), new Vector3(0f, 29.5f, 1.5f) }, blocks.Select(p => p.Position));
        Assert.Equal(new[] { new Vector3(0f, 28.5f, 0.5f), new Vector3(0f, 28.5f, 1.5f) }, arches.Select(p => p.Position));
        Assert.Equal(4, laid.Count);
        // Their tall ends (local +x) toward the slope (-x).
        Assert.All(arches, arch => Assert.True(StairLayout.Right(arch.YawDegrees).x < -0.99f, $"arch yaw {arch.YawDegrees}"));
    }

    [Fact]
    public void AStairWhoseUpperHalfNothingWithinReachHoldsIsRefused()
    {
        // The slope a metre off beside the stair's lower half (z 0..1), past the corbels' reach beside its upper half: the
        // search prices both halves as the kit lays them, so it refuses the stair.
        Func<float, float, float> near = Cliff(-0.9f), far = Cliff(-6.9f);
        Func<float, float, float> ground = (x, z) => z < 1f ? near(x, z) : far(x, z);
        Assert.False(StairSupport.Choose(Stone, Stair(30f), near).Refused);
        Assert.True(StairSupport.Choose(Stone, Stair(30f), ground).Refused);
        // The upper half's own reach sets the courses: corbels out beside it, the arch alone beside the lower half.
        Func<float, float, float> corbel = Cliff(-2.9f);
        StairSupport.Choice c = StairSupport.Choose(Stone, Stair(30f), (x, z) => z < 1f ? near(x, z) : corbel(x, z));
        Assert.Equal(2, c.Courses);
    }

    /// <summary>Rolling ground with 15 m ledges standing sheer out of it.</summary>
    private static float Ledged(float x, float z)
    {
        static float N(float a, float b) => 0.5f + 0.25f * (Mathf.Sin(a * 1.3f + Mathf.Cos(b * 0.7f)) + Mathf.Sin(b * 1.1f + Mathf.Cos(a * 0.9f)));
        return 40f * N(x * 0.03f + 14.2f, z * 0.03f + 2f) + 25f * N(x * 0.11f + 2f, z * 0.09f) + (N(x * 0.2f + 2f, z * 0.2f) > 0.6f ? 15f : 0f);
    }

    [Fact]
    public void TheSearchLaysNoPieceTheKitCannotHoldUp()
    {
        // Every walked piece of the layout, a turn's wedge fillers and a switchback's floors among them, is one the kit holds
        // up (Lay never meets a refused choice): before, the search checked only its own candidate pieces, and over these
        // ledges laid three wedge fillers and a floor out over drops nothing within reach held.
        Func<float, float, float> ground = Ledged;
        StairPlan plan = StairLayout.Build(new Vector2(13f, 7f), new Vector2(43f, 32f), ground, 2f, Stone);
        Assert.True(plan.Reached, plan.Reason);
        StairRun run = StairRun.FromPlan(new Vector2(13f, 7f), new Vector2(43f, 32f), 2f, plan, Stone);
        var hanging = run.Pieces.Where(p => StairSupport.WalkOf(p, Stone) is StairSupport.Walk w && StairSupport.Choose(Stone, w, ground).Refused).ToList();
        Assert.True(hanging.Count == 0, "nothing holds " + string.Join(", ", hanging.Select(p => $"{p.Prefab} at {p.Position}")));
    }

    [Theory]
    [InlineData("stone")]
    [InlineData("blackmarble")]
    public void APieceNothingWithinReachHoldsIsLeftUnheldNotHungWithSupports(string kitName)
    {
        // The search never lays such a piece; an older build's saved layout may hold one. Nothing is laid under it: not an
        // arch's first course hanging in the air, nor black marble's every cornice course down to the support ceiling.
        StairKit kit = StairKit.Named(kitName)!;
        Func<float, float, float> ground = (x, z) => 10f;
        var stair = new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = kit.Stair, Position = new Vector3(0f, 40f, 1f), YawDegrees = 180f };
        Assert.True(StairSupport.Choose(kit, StairSupport.WalkOf(stair, kit)!.Value, ground).Refused);
        int before = StairSupport.Unheld;
        List<StructurePiece> laid = StairSupport.Lay(kit, new List<StructurePiece> { stair }, ground, (x, z) => 10f);
        Assert.Empty(laid);
        Assert.True(StairSupport.Unheld > before);
    }

    [Fact]
    public void TheSearchsChoiceOfWhatHoldsAPieceAllocatesNothing()
    {
        // The search chooses for every piece it tries and every filler of every turn (StairLayout.Route.WedgeHeld), on the
        // island workers: an enumerator or a closure a call was garbage on each. A stair 10 m
        // over the ground beside a cliff: stone arches it, black marble corbels it, each reading its rows.
        Func<float, float, float> cliff = Cliff(-1.2f);
        Assert.Equal(StairSupport.Style.Arch, StairSupport.Choose(Stone, Stair(20f), cliff).Style);
        Assert.Equal(StairSupport.Style.Cornice, StairSupport.Choose(Marble, Stair(20f), cliff).Style);
        // A turn's fillers are worked out once and handed back as they are, moved to each pivot by the reader.
        FillerPiece[] around = StairFiller.Around(0f, 2, 2f, false, Stone.Fillers);
        Assert.Same(around, StairFiller.Around(0f, 2, 2f, false, Stone.Fillers));
        Assert.Equal(StairFiller.Choose(new Vector2(3f, 4f), 0f, 2, 2f, false, Stone.Fillers), around.Select(f => StairFiller.At(f, new Vector2(3f, 4f))));
#if NET
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            StairSupport.Choose(Stone, Stair(20f), cliff);
            StairSupport.Choose(Marble, Stair(20f), cliff);
            StairSupport.Choose(Marble, new StairSupport.Walk(new Vector2(0f, 0f), 0f, 2f, 2f, 20f, false, Marble.FloorThickness), cliff);
            foreach (FillerPiece f in StairFiller.Around(0f, 2, 2f, false, Stone.Fillers)) StairFiller.At(f, new Vector2(i, 0f));
        }
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
#endif
    }

    /// <summary>A walk's surface is one formula (Walk.Surface by a kit, Walk.Walking by the piece's own thickness): a floor's
    /// stands a thickness over its underside, a stair's rises its metre over its run whatever the thickness, and a floor
    /// asked by another kit's thickness reads that kit's (a rail laid in a kit judges by it).</summary>
    [Fact]
    public void AWalksSurfaceIsOneFormulaByTheThicknessAsked()
    {
        var floor = new StairSupport.Walk(new Vector2(0f, 0f), 0f, 2f, 2f, 10f, false, Stone.FloorThickness);
        var stair = new StairSupport.Walk(new Vector2(0f, 0f), 0f, 2f, 2f, 10f, true, 0f);
        Assert.Equal(10f + Stone.FloorThickness, floor.Walking(1f));
        Assert.Equal(floor.Walking(1f), floor.Surface(1f, Stone));
        Assert.Equal(10f + Marble.FloorThickness, floor.Surface(1f, Marble));
        Assert.Equal(10.5f, stair.Walking(1f));
        Assert.Equal(stair.Walking(1f), stair.Surface(1f, Marble));
    }

    [Fact]
    public void AValleysSharedFloorIsLaidOnlyWhereSomethingHoldsItUp()
    {
        // The shared floor two staircases start from at a valley's bottom (RoadStairRuns): on the ground, laid; 30 m over a
        // drop with nothing within reach, not laid, as no finish lays a row the kit cannot hold up.
        Func<float, float, float> flat = (x, z) => 10f;
        var onGround = new StairPlan { Kit = Stone.Name };
        Assert.True(StairLayout.AddFloorRow(onGround, new Vector2(0f, 0f), 0f, 10.5f, flat, 2f));
        Assert.NotEmpty(onGround.Pieces);
        var overDrop = new StairPlan { Kit = Stone.Name };
        Assert.False(StairLayout.AddFloorRow(overDrop, new Vector2(0f, 0f), 0f, 40f, flat, 2f));
        Assert.Empty(overDrop.Pieces);
        Assert.Equal(0, overDrop.Landings);
    }

    [Fact]
    public void WhatTheKitCannotHoldUpIsSaidWhereItIsAndCountedUntilTheNextGeneration()
    {
        // A stair 30 m over the ground, nothing within reach: left unheld, said at the middle of its underside.
        var shortfall = new StairSupport.Shortfall();
        var floating = new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = Stone.Stair, Position = new Vector3(0f, 40f, 1f), YawDegrees = 180f };
        StairSupport.Lay(Stone, new List<StructurePiece> { floating }, (x, z) => 10f, (x, z) => 10f, shortfall);
        Vector3 unheld = Assert.Single(shortfall.Unheld);
        Assert.Equal(new Vector3(0f, 40f, 1f), unheld);
        // A stair 4 m over the ground beside a face, walled, over a cut 34 m deep: its wall stops at the most courses, said at
        // each short cell's lowest course.
        var walled = new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = Stone.Stair, Position = new Vector3(0f, 14f, 1f), YawDegrees = 180f };
        StairSupport.Lay(Stone, new List<StructurePiece> { walled }, (x, z) => x < -1.2f ? 50f : 10f, (x, z) => -20f, shortfall);
        Assert.NotEmpty(shortfall.ShortWalls);
        Assert.All(shortfall.ShortWalls, p => Assert.Equal(14f - StairSupport.MostCourses, p.y, 3));
        Assert.True(StairSupport.Unheld > 0 && StairSupport.ShortWalls > 0);
        // Counted for the generation, as the search's refusals are: a new one starts from none.
        RoadStairRuns.ResetCache();
        Assert.Equal(0, StairSupport.Unheld);
        Assert.Equal(0, StairSupport.ShortWalls);
    }

    private sealed class LevelGround : WorldGenerator
    {
        public override float GetHeight(float wx, float wy) => 10f;
    }

    [Fact]
    public void AStaircasesKitSaysOnceWhatItCouldNotHoldUp()
    {
        // As a staircase is dressed each time the plans are made (StairStructures.Dresser): said the first time, with the
        // piece's place, the kit and the staircase's ends.
        var world = new LevelGround();
        using var scope = new Valheim.Testing.Doubles.ValheimWorldScope();
        scope.WithWorld(world);
        var run = new StairRun { From = new Vector2(0f, -4f), To = new Vector2(0f, 2f), Width = 2f, Kit = Stone };
        run.Pieces.Add(new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = Stone.Stair, Position = new Vector3(0f, 40f, 1f), YawDegrees = 180f });
        var log = new List<string>();
        BepInEx.Logging.ManualLogSource.Captured = log;
        try
        {
            StairStructures.Dresser(run, world);
            StairStructures.Dresser(run, world);
        }
        finally { BepInEx.Logging.ManualLogSource.Captured = null; }
        string said = Assert.Single(log, w => w.StartsWith("[STAIRS] staircase"));
        Assert.Contains("stone (0.0, -4.0) -> (0.0, 2.0)", said);
        Assert.Contains("1 walking piece(s) with nothing within reach to hold them up, left unheld at (0.0, 40.0, 1.0)", said);
    }

    [Fact]
    public void AStraightRunIsWalledUnderItsDownhillHalfInRunningBond()
    {
        // Four stairs climbing +z, a metre a stair, over flat ground at 0 with a face rising west: walls 1-4 m tall.
        var stairs = new List<StructurePiece>();
        for (int i = 0; i < 4; i++)
            stairs.Add(new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = Stone.Stair, Position = new Vector3(0f, 1f + i, 1f + 2f * i), YawDegrees = 180f });
        Func<float, float, float> ground = (x, z) => x < -1.2f ? 50f : 0.3f;
        List<StructurePiece> laid = StairSupport.Lay(Stone, stairs, ground, (x, z) => ground(x, z));
        Assert.NotEmpty(laid);
        // All under the downhill (east) half: x 0..1, centred 0.5.
        Assert.All(laid, p => Assert.Equal(0.5f, p.Position.x, 3));
        Assert.All(laid, p => Assert.StartsWith("stone_wall_", p.Prefab));
        // Every metre along is walled from its stair's underside down into the ground, and no course is wholly under it.
        for (int cell = 0; cell < 8; cell++)
        {
            float top = 1f + cell / 2;
            var column = laid.Where(p => Covers(p, cell + 0.5f)).ToList();
            Assert.Equal(top - 0.5f, column.Max(p => p.Position.y), 3);
            Assert.True(column.Min(p => p.Position.y) - 0.5f <= 0.3f - StairSupport.Bury + 1e-3f, $"cell {cell} ends at {column.Min(p => p.Position.y) - 0.5f}");
            Assert.True(column.Min(p => p.Position.y) + 0.5f > 0.3f);
        }
        // Courses alternate where their stretchers' joints fall (running bond): no joint lines up with the one below.
        foreach (var course in laid.GroupBy(p => Mathf.RoundToInt(p.Position.y * 2f)))
        {
            var below = laid.Where(p => Mathf.RoundToInt(p.Position.y * 2f) == course.Key - 2).ToList();
            foreach (float joint in Joints(course))
                Assert.DoesNotContain(joint, Joints(below).Where(j => j > 0.01f && j < 7.99f));
        }
    }

    /// <summary>Four stairs climbing +z a metre a stair from z 0, their underside 1-4 m, at x -1..1.</summary>
    private static List<StructurePiece> FourStairs()
    {
        var stairs = new List<StructurePiece>();
        for (int i = 0; i < 4; i++)
            stairs.Add(new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = Stone.Stair, Position = new Vector3(0f, 1f + i, 1f + 2f * i), YawDegrees = 180f });
        return stairs;
    }

    [Fact]
    public void AWallIsLeftOutWhereTheFinishedGroundUnderItIsNotKnown()
    {
        // The finished ground is not known at the terrain's vertices along z 3 (inside a location's footprint whose levelling
        // is not known, as RoadTerrainModifier.FinishedGround answers there), so under z 2..4: those metres are left
        // unwalled, not walled 8 m down through whatever the location leaves; the rest walled as ever.
        Func<float, float, float> ground = (x, z) => x < -1.2f ? 50f : 0.3f;
        List<StructurePiece> laid = StairSupport.Lay(Stone, FourStairs(), ground, (x, z) => z == 3 ? float.NaN : ground(x, z));
        for (int cell = 0; cell < 8; cell++)
        {
            var column = laid.Where(p => Covers(p, cell + 0.5f)).ToList();
            if (cell is 2 or 3) Assert.Empty(column);
            else
            {
                Assert.NotEmpty(column);
                Assert.True(column.Min(p => p.Position.y) - 0.5f <= 0.3f - StairSupport.Bury + 1e-3f, $"cell {cell} ends at {column.Min(p => p.Position.y) - 0.5f}");
            }
        }
    }

    [Fact]
    public void AWallReachesTheFinishedGroundBelowARoadsCut()
    {
        // 3.7 m of air over the natural ground under the stair's downhill edge, walled; a road's cut beside it lowers the
        // finished ground 5 m (earthwork cuts up to 8 m): the wall stands down to that, 9 courses under the underside.
        Func<float, float, float> natural = (x, z) => x < -1.2f ? 50f : 10f - 3.7f;
        Func<float, float, float> finished = (x, z) => natural(x, z) - (x > -1.2f ? 5f : 0f);
        var stair = new List<StructurePiece> { new() { Kind = StructurePieceKind.StairStep, Prefab = Stone.Stair, Position = new Vector3(0f, 10f, 1f), YawDegrees = 180f } };
        Assert.Equal(StairSupport.Style.Wall, StairSupport.Choose(Stone, StairSupport.WalkOf(stair[0], Stone)!.Value, natural).Style);
        List<StructurePiece> laid = StairSupport.Lay(Stone, stair, natural, (x, z) => finished(x, z));
        for (int cell = 0; cell < 2; cell++)
        {
            var column = laid.Where(p => Covers(p, cell + 0.5f)).ToList();
            Assert.Equal(9.5f, column.Max(p => p.Position.y), 3);
            float bottom = column.Min(p => p.Position.y) - 0.5f;
            Assert.True(bottom <= 1.3f - StairSupport.Bury + 1e-3f, $"cell {cell} ends at {bottom}, the finished ground at 1.3");
            Assert.True(bottom + 1f > 1.3f, $"cell {cell} has a course wholly under the ground");
        }
    }

    /// <summary>
    /// The style a piece is held by is chosen on the ground its layout was searched on (the cliff 0.9 m off its edge: an
    /// arch), but what is laid reaches the finished ground: a road's cut takes the cliff 2 m back, and the arch is carried
    /// out on a corbel course to it, both halves, where on the planning ground it stopped with its tall end over the cut.
    /// </summary>
    [Fact]
    public void AnArchIsCarriedOutToTheFinishedGroundPastARoadsCut()
    {
        Func<float, float, float> natural = Cliff(-0.9f), finished = Cliff(-2.9f);
        var stair = new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = Stone.Stair, Position = new Vector3(0f, 30f, 1f), YawDegrees = 180f };
        Assert.Equal(1, StairSupport.Choose(Stone, StairSupport.WalkOf(stair, Stone)!.Value, natural).Courses);
        List<StructurePiece> laid = Stone.Dress(new StairRun { Pieces = { stair } }, natural, (x, z) => finished(x, z), false);
        var arches = laid.Where(p => p.Prefab == "stone_arch").ToList();
        Assert.Equal(4, arches.Count);
        Assert.Equal(2, arches.Count(p => Mathf.Abs(p.Position.y - 27.5f) < 1e-3f));
        foreach (ViaductSupport.Held h in StairSupport.Holding(laid, (x, z) => finished(x, z)))
            if (h.Piece.Kind == StructurePieceKind.StairSupport) Assert.True(h.Support >= h.Min, $"{h.Piece.Prefab} at {h.Piece.Position} falls");
    }

    private static bool Covers(StructurePiece p, float z)
    {
        float half = p.Prefab == "stone_wall_2x1" ? 1f : 0.5f;
        return z > p.Position.z - half && z < p.Position.z + half;
    }

    private static IEnumerable<float> Joints(IEnumerable<StructurePiece> course) =>
        course.Where(p => p.Prefab == "stone_wall_2x1").SelectMany(p => new[] { p.Position.z - 1f, p.Position.z + 1f })
            .Select(z => (float)Math.Round(z, 2));

    [Fact]
    public void BlackMarbleStepsItsCornicesIntoTheSlopeDownToTheGround()
    {
        // The slope rises west a metre a metre from x = -1; 6 m of air under the stair's east edge.
        Func<float, float, float> ground = Wall(4f, -1f, 1f);
        StairSupport.Choice c = StairSupport.Choose(Marble, Stair(10f), ground);
        Assert.Equal(StairSupport.Style.Cornice, c.Style);
        Assert.Equal(-1, c.Side);
        var stair = new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = Marble.Stair, Position = new Vector3(0f, 10f, 1f), YawDegrees = 180f };
        List<StructurePiece> laid = StairSupport.Lay(Marble, new List<StructurePiece> { stair }, ground, (x, z) => ground(x, z));
        // The hand-built black marble prototype: a 2x1x1 under the stair's lower half 0.5 m down, and cornice columns under both halves,
        // each course 2 m lower and 1 m further west.
        Assert.Contains(laid, p => p.Prefab == "blackmarble_2x1x1" && Vector3.Distance(p.Position, new Vector3(0f, 9.5f, 0.5f)) < 0.001f);
        var upper = laid.Where(p => p.Prefab == "blackmarble_out_1" && Math.Abs(p.Position.z - 1.5f) < 0.01f).OrderByDescending(p => p.Position.y).ToList();
        Assert.True(upper.Count >= 2);
        for (int i = 0; i < upper.Count; i++)
        {
            Assert.Equal(9f - 2f * i, upper[i].Position.y, 3);
            Assert.Equal(-i, upper[i].Position.x, 3);
        }
        // The lowest course's 1 m block (on its wall side) is in the ground.
        StructurePiece lowest = upper[upper.Count - 1];
        Assert.True(ground(lowest.Position.x - 0.5f, 1.5f) >= lowest.Position.y);
    }

    [Fact]
    public void TheSearchPricesAPieceByTheChoiceTheKitLays()
    {
        // Search and first build call the one Choose on the natural ground: a stair the search refused is never laid, and
        // one it priced as an arch is laid with arches.
        Func<float, float, float> ground = Cliff(-2.9f);
        StairSupport.Choice c = StairSupport.Choose(Stone, Stair(30f), ground);
        var stair = new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = Stone.Stair, Position = new Vector3(0f, 30f, 1f), YawDegrees = 180f };
        StairSupport.Walk w = StairSupport.WalkOf(stair, Stone)!.Value;
        Assert.Equal(c.Style, StairSupport.Choose(Stone, w, ground).Style);
        Assert.Equal(c.Price, StairSupport.Choose(Stone, w, ground).Price);
    }

    [Fact]
    public void OnARealFaceEveryPieceNotOnTheGroundIsHeldAndNoSupportIsWhollyBuried()
    {
        var (plan, ground) = StairLayoutTests.Face.Value;
        Assert.True(plan.Reached, plan.Reason);
        StairRun run = StairRun.FromPlan(new Vector2(-2061.41f, 1504.82f), new Vector2(-2125f, 1460f), 2f, plan, Stone);
        List<StructurePiece> dressed = Stone.Dress(run, ground, (x, z) => ground(x, z));
        List<StructurePiece> supports = dressed.Where(p => p.Kind == StructurePieceKind.StairSupport).ToList();
        Assert.NotEmpty(supports);
        Assert.DoesNotContain(supports, p => p.Prefab == "stone_pillar");
        foreach (StructurePiece p in supports)
            Assert.False(BuriedPieces.Buried(p.Prefab, p.Position, p.YawDegrees, (x, z) => ground(x, z)), $"{p.Prefab} at {p.Position} wholly under the ground");
        foreach (StructurePiece piece in dressed.Where(p => StairStructures.IsLayoutKind(p.Kind) && p.Kind != StructurePieceKind.StairSupport))
        {
            StairSupport.Walk w = StairSupport.WalkOf(piece, Stone)!.Value;
            StairSupport.Choice c = StairSupport.Choose(Stone, w, ground);
            Assert.False(c.Refused, $"{piece.Prefab} at {piece.Position}");
            if (c.Style == StairSupport.Style.Ground) continue;
            // A wall's courses stop short of the ground where its downhill half rests in the slope already.
            float under = new[] { 0.5f, 1.5f }.Min(a => { Vector2 p = w.At(a, -c.Side * 0.5f); return ground(p.x, p.y); });
            if (c.Style == StairSupport.Style.Wall && under >= w.Underside - StairSupport.Bury) continue;
            Vector2 centre = w.At(w.Depth / 2f, 0f);
            Assert.True(supports.Any(s => Vector2.Distance(new Vector2(s.Position.x, s.Position.z), centre) < 6.5f && s.Position.y < w.Underside),
                $"{piece.Prefab} at {piece.Position} ({c.Style}, side {c.Side}, {c.Courses} courses) has nothing under it");
        }
        foreach (StructurePiece arch in supports.Where(p => p.Prefab == "stone_arch"))
            Assert.True(supports.Any(b => b.Prefab == "stone_wall_2x1" && Vector3.Distance(b.Position, arch.Position + new Vector3(0f, 1f, 0f)) < 0.01f),
                $"arch at {arch.Position} yaw {arch.YawDegrees} has no block over it");
        StoneArchTurnTests.AssertNoSupportStandsInAWalkedPiece(Stone, dressed);
    }

}

/// <summary>Stone held from the slope through a turn (the staircase below StoneTowerRuins05): every arch has its
/// block over it, the first stair past a turn's too, and both halves of every stair are held.</summary>
public class StoneArchTurnTests
{
    private static readonly StairKit Stone = StairKit.Stone;

    /// <summary>A sheer face <paramref name="off"/> metres out along <paramref name="toward"/> from <paramref name="origin"/>:
    /// <paramref name="low"/> in front of it, 100 m higher behind.</summary>
    private static Func<float, float, float> Face(Vector2 origin, Vector2 toward, float off, float low) =>
        (x, z) => Vector2.Dot(new Vector2(x, z) - origin, toward) > off ? low + 100f : low;

    /// <summary>Three stairs climbing south from (0, 0) at 30 m, then three turned <paramref name="turn"/>, a sheer face on the
    /// turn's inside (as at -4787.5,1727.7) 0.6 m off both flights' inner edges, 20 m and more over the ground in front.</summary>
    private static (StairRun run, List<StructurePiece> dressed, Func<float, float, float> ground) Flight(float turn, StairKit? kit = null)
    {
        kit ??= Stone;
        float t = StairLayout.Wrap(180f + turn), inside = Math.Sign(turn);
        Vector2 pivot = new Vector2(0f, -6f) + StairLayout.Right(180f) * inside;
        Func<float, float, float> before = Face(pivot, StairLayout.Right(180f) * inside, 0.6f, 10f), after = Face(pivot, StairLayout.Right(t) * inside, 0.6f, 10f);
        Func<float, float, float> ground = (x, z) => Mathf.Min(before(x, z), after(x, z));
        StairPlan plan = StairLayout.LayFlight(new Vector2(0f, 0f), 30f, new[] { 180f, 180f, 180f, t, t, t }, kit, ground);
        StairRun run = StairRun.FromPlan(new Vector2(0f, 0f), new Vector2(plan.End.x, plan.End.z), 2f, plan, kit);
        return (run, kit.Dress(run, ground, (x, z) => ground(x, z), false), ground);
    }

    /// <summary>Black marble through a turn (the staircase from 1768,6960 to 1712,6920): the 2x1x1 block under
    /// the first stair past the turn shares the turn's 2 x 1 filler's corner just as stone's does, and was left out by the
    /// same test (<see cref="StairSupport.Lay"/>); every stair has its block and the top course of both cornice columns.</summary>
    [Theory]
    [InlineData(22.5f)]
    [InlineData(45f)]
    [InlineData(-45f)]
    [InlineData(67.5f)]
    public void ThroughATurnEveryBlackMarbleStairHasItsBlockAndBothCornices(float turn)
    {
        StairKit kit = StairKit.BlackMarble;
        var (run, dressed, ground) = Flight(turn, kit);
        var supports = dressed.Where(p => p.Kind == StructurePieceKind.StairSupport).ToList();
        bool Has(string prefab, Vector2 at, float y) => supports.Any(b => b.Prefab == prefab && Vector3.Distance(b.Position, new Vector3(at.x, y, at.y)) < 0.01f);
        foreach (StructurePiece stair in run.Pieces.Where(p => p.Kind == StructurePieceKind.StairStep))
        {
            StairSupport.Walk w = StairSupport.WalkOf(stair, kit)!.Value;
            StairSupport.Choice c = StairSupport.Choose(kit, w, ground);
            Assert.Equal(StairSupport.Style.Cornice, c.Style);
            Assert.False(c.Refused, $"stair at {stair.Position}");
            Assert.True(Has("blackmarble_2x1x1", w.At(0.5f, 0f), w.Underside - 0.5f), $"stair at {stair.Position} yaw {stair.YawDegrees}: no block under its lower half");
            Assert.True(Has("blackmarble_out_1", w.At(1.5f, 0f), w.Underside - 1f), $"stair at {stair.Position} yaw {stair.YawDegrees}: no cornice under its upper half");
            Assert.True(Has("blackmarble_out_1", w.At(0.5f, 0f), w.Underside - 2f), $"stair at {stair.Position} yaw {stair.YawDegrees}: no cornice under its block");
        }
        AssertNoSupportStandsInAWalkedPiece(kit, dressed);
    }

    [Theory]
    [InlineData(22.5f)]
    [InlineData(45f)]
    [InlineData(-45f)]
    [InlineData(67.5f)]
    public void ThroughATurnEveryArchHasItsBlockOverItAndBothHalvesOfEveryStairAreHeld(float turn)
    {
        var (run, dressed, ground) = Flight(turn);
        var supports = dressed.Where(p => p.Kind == StructurePieceKind.StairSupport).ToList();
        foreach (StructurePiece arch in supports.Where(p => p.Prefab == "stone_arch"))
            Assert.True(supports.Any(b => b.Prefab == "stone_wall_2x1" && Vector3.Distance(b.Position, arch.Position + new Vector3(0f, 1f, 0f)) < 0.01f),
                $"arch at {arch.Position} yaw {arch.YawDegrees} has no block over it");
        foreach (StructurePiece stair in run.Pieces.Where(p => p.Kind == StructurePieceKind.StairStep))
        {
            StairSupport.Walk w = StairSupport.WalkOf(stair, Stone)!.Value;
            StairSupport.Choice c = StairSupport.Choose(Stone, w, ground);
            Assert.Equal(StairSupport.Style.Arch, c.Style);
            Assert.False(c.Refused, $"stair at {stair.Position}");
            foreach (float along in new[] { 0.5f, 1.5f })
            {
                Vector2 at = w.At(along, 0f);
                Assert.True(supports.Any(b => b.Prefab == "stone_wall_2x1" && Vector3.Distance(b.Position, new Vector3(at.x, w.Underside - 0.5f, at.y)) < 0.01f),
                    $"stair at {stair.Position} yaw {stair.YawDegrees}: no block under its metre at {along}");
            }
        }
        AssertNoSupportStandsInAWalkedPiece(Stone, dressed);
    }

    /// <summary>The staircase below StoneTowerRuins05, 2 Nov: a stair at -4786.9,1729.8 heading south, then a 45 degree
    /// turn to the south-west. The turn's 2 x 1 filler, level with the first stair's top, reaches under the turned
    /// stair's back corner; the block under that stair's lower half shares that corner of it, and was left out. It was laid
    /// by hand: stone_wall_2x1 at (-4787.521, 183.266, 1727.742).</summary>
    [Fact]
    public void TheBlockUnderTheFirstStairPastATurnIsLaidByHand()
    {
        Vector2 foot = new(-4786.875f, 1730.802f);
        // The slope rises to the west-north-west, on the turn's inside.
        Func<float, float, float> ground = Face(new Vector2(-4787f, 1728f), StairLayout.Right(202.5f), 1.6f, 100f);
        StairPlan plan = StairLayout.LayFlight(foot, 182.766f, new[] { 180f, 225f, 225f }, Stone, ground);
        StairRun run = StairRun.FromPlan(foot, new Vector2(plan.End.x, plan.End.z), 2f, plan, Stone);
        // The layout as read in game.
        Assert.Contains(run.Pieces, p => p.Prefab == "stone_stair" && Vector3.Distance(p.Position, new Vector3(-4787.875f, 183.766f, 1727.388f)) < 0.01f);
        Assert.Contains(run.Pieces, p => p.Prefab == "stone_wall_1x1" && Vector3.Distance(p.Position, new Vector3(-4786.461f, 183.266f, 1728.095f)) < 0.01f);
        List<StructurePiece> dressed = Stone.Dress(run, ground, (x, z) => ground(x, z), false);
        Assert.Contains(dressed, p => p.Kind == StructurePieceKind.StairSupport && p.Prefab == "stone_wall_2x1"
            && Vector3.Distance(p.Position, new Vector3(-4787.521f, 183.266f, 1727.742f)) < 0.01f);
        Assert.Contains(dressed, p => p.Kind == StructurePieceKind.StairSupport && p.Prefab == "stone_arch"
            && Vector3.Distance(p.Position, new Vector3(-4787.521f, 182.266f, 1727.742f)) < 0.01f);
        AssertNoSupportStandsInAWalkedPiece(Stone, dressed);
    }

    /// <summary>Stone walled through a turn (the second tower approach: a dark gap through the wall under a
    /// 22.5 degree turn at -1924.2,6698.5): the top course under the first stair past the turn shares the turn's 2 x 1
    /// filler's corner, as the arch's block does, and was left out by the same test. Every metre of every stair's downhill
    /// half is walled up to its underside. The ground: 1.5 m under each piece's middle, rising 2 m a metre toward the turn's
    /// inside (its wall side), 3.5 m under its downhill edge.</summary>
    [Theory]
    [InlineData(22.5f)]
    [InlineData(45f)]
    [InlineData(-45f)]
    [InlineData(67.5f)]
    public void ThroughATurnEveryMetreOfAWalledStairIsWalledUpToIt(float turn)
    {
        float t = StairLayout.Wrap(180f + turn), inside = Math.Sign(turn);
        var headings = new[] { 180f, 180f, 180f, t, t, t };
        var walks = StairLayout.LayFlight(new Vector2(0f, 0f), 30f, headings, Stone, (x, z) => -50f).Pieces
            .Select(p => StairSupport.WalkOf(new StructurePiece { Kind = p.Kind == StairPieceKind.Stair ? StructurePieceKind.StairStep : StructurePieceKind.StairFiller,
                Prefab = p.Prefab, Position = p.Position, YawDegrees = p.YawDegrees }, Stone)!.Value).ToList();
        Func<float, float, float> ground = (x, z) =>
        {
            var q = new Vector2(x, z);
            StairSupport.Walk w = walks.OrderBy(o => Vector2.Distance(q, o.At(o.Depth / 2f, 0f))).First();
            return w.Underside - 1.5f + 2f * inside * Vector2.Dot(q - w.At(w.Depth / 2f, 0f), w.Right);
        };
        StairPlan plan = StairLayout.LayFlight(new Vector2(0f, 0f), 30f, headings, Stone, ground);
        StairRun run = StairRun.FromPlan(new Vector2(0f, 0f), new Vector2(plan.End.x, plan.End.z), 2f, plan, Stone);
        List<StructurePiece> dressed = Stone.Dress(run, ground, (x, z) => ground(x, z), false);
        foreach (StructurePiece stair in run.Pieces.Where(p => p.Kind == StructurePieceKind.StairStep))
        {
            StairSupport.Walk w = StairSupport.WalkOf(stair, Stone)!.Value;
            StairSupport.Choice c = StairSupport.Choose(Stone, w, ground);
            Assert.Equal(StairSupport.Style.Wall, c.Style);
            AssertWalledUpTo(dressed, w, -c.Side, $"stair at {stair.Position} yaw {stair.YawDegrees}");
        }
        AssertNoSupportStandsInAWalkedPiece(Stone, dressed);
    }

    /// <summary>The staircase on the second tower approach, 2 Nov: two stairs heading east-south-east from -1928.8,6700.6,
    /// then a 22.5 degree turn east, walled under their south halves. The live pieces, and the hole seen in game: the top course's
    /// 1 x 1 under the first stair past the turn, at (-1924.220, 250.313, 6698.501).</summary>
    [Fact]
    public void TheWallUnderTheFirstStairPastATurnHasNoHole()
    {
        Vector2 foot = new(-1928.798f, 6700.608f);
        // Rising north (the wall side), falling south, climbing east with the flight.
        Func<float, float, float> ground = (x, z) => 247.8f + 0.5f * (x + 1928f) + 2f * (z - 6699.5f);
        StairPlan plan = StairLayout.LayFlight(foot, 248.813f, new[] { 112.5f, 112.5f, 90f, 90f }, Stone, ground);
        StairRun run = StairRun.FromPlan(foot, new Vector2(plan.End.x, plan.End.z), 2f, plan, Stone);
        List<StructurePiece> dressed = Stone.Dress(run, ground, (x, z) => ground(x, z), false);
        bool Has(string prefab, float x, float y, float z, float yaw) => dressed.Any(p => p.Prefab == prefab
            && Vector3.Distance(p.Position, new Vector3(x, y, z)) < 0.01f && Mathf.Abs(StairLayout.Wrap(p.YawDegrees - yaw + 180f) - 180f) < 0.01f);
        // As read in game.
        Assert.True(Has("stone_stair", -1926.026f, 249.813f, 6699.459f, 292.5f));
        Assert.True(Has("stone_stair", -1923.719f, 250.813f, 6699.001f, 270f));
        Assert.True(Has("stone_wall_2x1", -1924.640f, 250.313f, 6698.886f, 292.5f)); // the turn's filler
        Assert.True(Has("stone_wall_2x1", -1926.217f, 249.313f, 6698.998f, 22.5f));
        Assert.True(Has("stone_wall_1x1", -1924.831f, 249.313f, 6698.424f, 112.5f));
        Assert.True(Has("stone_wall_2x1", -1923.719f, 249.313f, 6698.501f, 0f));
        Assert.True(Has("stone_wall_2x1", -1922.719f, 250.313f, 6698.501f, 0f));
        // The hole.
        Assert.True(Has("stone_wall_1x1", -1924.219f, 250.313f, 6698.501f, 90f), "no wall under the first stair past the turn");
        foreach (StructurePiece stair in run.Pieces.Where(p => p.Kind == StructurePieceKind.StairStep))
        {
            StairSupport.Walk w = StairSupport.WalkOf(stair, Stone)!.Value;
            StairSupport.Choice c = StairSupport.Choose(Stone, w, ground);
            Assert.Equal(StairSupport.Style.Wall, c.Style);
            AssertWalledUpTo(dressed, w, -c.Side, $"stair at {stair.Position} yaw {stair.YawDegrees}");
        }
        AssertNoSupportStandsInAWalkedPiece(Stone, dressed);
    }

    /// <summary>The top of the same staircase, 2 Nov: a stair heading east, then a 45 degree turn onto the top floor, the
    /// turn's wedge filled by a 2 x 1 and a 1 x 1. The wall under the 1 x 1 stood a quarter metre off it, at
    /// -1924.641,6712.884 (the middle of the downhill half of a 2 m piece); it stands right under it, square to it, as the
    /// walls under the 2 m pieces stand flush with their downhill edges.</summary>
    [Fact]
    public void AWallUnderAOneByOneFillerStandsRightUnderIt()
    {
        Vector2 foot = new(-1927.524f, 6713.475f);
        // Rising north (the wall side), falling south.
        Func<float, float, float> ground = (x, z) => 273.6f + 2f * (z - 6713.5f);
        StairPlan plan = StairLayout.LayFlight(foot, 275.813f, new[] { 90f, 45f }, Stone, ground, new[] { StairPieceKind.Stair, StairPieceKind.Landing });
        StairRun run = StairRun.FromPlan(foot, new Vector2(plan.End.x, plan.End.z), 2f, plan, Stone);
        // As read in game.
        StructurePiece filler = Assert.Single(run.Pieces, p => p.Prefab == "stone_wall_1x1");
        Assert.True(Vector3.Distance(filler.Position, new Vector3(-1924.817f, 276.313f, 6713.061f)) < 0.01f, $"{filler.Position}");
        Assert.Contains(run.Pieces, p => p.Prefab == "stone_wall_2x1" && Vector3.Distance(p.Position, new Vector3(-1925.024f, 276.313f, 6713.475f)) < 0.01f);
        List<StructurePiece> dressed = Stone.Dress(run, ground, (x, z) => ground(x, z), false);
        StairSupport.Walk w = StairSupport.WalkOf(filler, Stone)!.Value;
        Assert.Equal(StairSupport.Style.Wall, StairSupport.Choose(Stone, w, ground).Style);
        // Its wall: square to it, right under it from its underside down; no course square to it stands a fraction off it.
        // (The turn's two fillers overlap, so the 2 x 1's own wall, square to that, overlaps this one as they do.)
        Vector2 centre = w.At(w.Depth / 2f, 0f);
        var square = dressed.Where(p => p.Kind == StructurePieceKind.StairSupport && p.Position.y < w.Underside
            && StairLayout.Wrap(p.YawDegrees - filler.YawDegrees + 0.01f) % 90f < 0.02f
            && Mathf.Abs(Vector2.Dot(new Vector2(p.Position.x, p.Position.z) - centre, w.Dir)) < 0.5f
            && Mathf.Abs(Vector2.Dot(new Vector2(p.Position.x, p.Position.z) - centre, w.Right)) < 0.5f).ToList();
        Assert.Contains(square, p => p.Prefab == "stone_wall_1x1" && Mathf.Abs(p.Position.y + 0.5f - w.Underside) < 0.01f);
        foreach (StructurePiece p in square)
            Assert.True(Vector2.Distance(new Vector2(p.Position.x, p.Position.z), centre) < 0.01f, $"wall at {p.Position} is off the filler at {filler.Position}");
        // The walls under the 2 m pieces stay flush with their downhill (south) edges: under the stair, centred 0.5 m in.
        Assert.Contains(dressed, p => p.Kind == StructurePieceKind.StairSupport && Mathf.Abs(p.Position.z - 6712.975f) < 0.01f
            && Mathf.Abs(p.Position.y + 0.5f - 275.813f) < 0.01f);
        AssertNoSupportStandsInAWalkedPiece(Stone, dressed);
    }

    /// <summary>Each metre of a walled stair's downhill half has a wall course whose top meets the stair's underside.</summary>
    private static void AssertWalledUpTo(List<StructurePiece> dressed, StairSupport.Walk w, int downhill, string what)
    {
        foreach (float along in new[] { 0.5f, 1.5f })
        {
            Vector2 cell = w.At(along, downhill * w.Across / 4f);
            Assert.True(dressed.Any(p => p.Kind == StructurePieceKind.StairSupport && p.Prefab.StartsWith("stone_wall_")
                    && Mathf.Abs(p.Position.y + 0.5f - w.Underside) < 0.01f && Covers(p, cell)),
                $"{what}: no wall course up to its underside at its metre {along}");
        }
    }

    private static bool Covers(StructurePiece p, Vector2 q)
    {
        BuriedPieces.BoundsOf(p.Prefab, out Vector3 min, out Vector3 max);
        Vector2 d = q - new Vector2(p.Position.x, p.Position.z);
        float x = Vector2.Dot(d, StairLayout.Right(p.YawDegrees)), z = Vector2.Dot(d, StairLayout.Forward(p.YawDegrees));
        return x > min.x && x < max.x && z > min.z && z < max.z;
    }

    /// <summary>
    /// No support stands in the room of a walked piece's own solid, sampled through the support's bounds: never in a stair's
    /// solid (its underside up to its walking surface) or a floor; in a turn's filler only where both lie under another
    /// walked piece, stone hidden in stone under it.
    /// </summary>
    internal static void AssertNoSupportStandsInAWalkedPiece(StairKit kit, IReadOnlyList<StructurePiece> dressed)
    {
        var walked = dressed.Where(p => StairSupport.WalkOf(p, kit) != null).Select(p => (p, w: StairSupport.WalkOf(p, kit)!.Value)).ToList();
        foreach (StructurePiece s in dressed.Where(p => p.Kind == StructurePieceKind.StairSupport))
        {
            Assert.True(BuriedPieces.BoundsOf(s.Prefab, out Vector3 min, out Vector3 max), s.Prefab);
            Vector2 at = new(s.Position.x, s.Position.z), right = StairLayout.Right(s.YawDegrees), forward = StairLayout.Forward(s.YawDegrees);
            for (int i = 0; i < 5; i++)
                for (int j = 0; j < 5; j++)
                    for (int k = 0; k < 5; k++)
                    {
                        Vector3 local = new(min.x + (max.x - min.x) * (0.1f + 0.2f * i), min.y + (max.y - min.y) * (0.1f + 0.2f * j), min.z + (max.z - min.z) * (0.1f + 0.2f * k));
                        Vector2 q = at + right * local.x + forward * local.z;
                        float y = s.Position.y + local.y;
                        foreach (var (p, w) in walked)
                        {
                            if (!InSolid(w, kit, q, y)) continue;
                            Assert.True(p.Kind == StructurePieceKind.StairFiller,
                                $"{s.Prefab} at {s.Position} yaw {s.YawDegrees} stands in {p.Prefab} at {p.Position} yaw {p.YawDegrees}");
                            Assert.True(walked.Any(o => !ReferenceEquals(o.p, p) && Under(o.w, q, y)),
                                $"{s.Prefab} at {s.Position} yaw {s.YawDegrees} stands in {p.Prefab} at {p.Position} where nothing is walked over them");
                        }
                    }
        }
    }

    private static bool InSolid(StairSupport.Walk w, StairKit kit, Vector2 q, float y)
    {
        const float tol = 0.02f;
        float along = Vector2.Dot(q - w.Back, w.Dir), across = Vector2.Dot(q - w.Back, w.Right);
        if (along <= tol || along >= w.Depth - tol || Mathf.Abs(across) >= w.Across / 2f - tol) return false;
        return y > w.Underside + tol && y < (w.Stair ? w.Surface(along, kit) : w.Underside + kit.FloorThickness) - tol;
    }

    private static bool Under(StairSupport.Walk w, Vector2 q, float y)
    {
        float along = Vector2.Dot(q - w.Back, w.Dir), across = Vector2.Dot(q - w.Back, w.Right);
        return along >= 0f && along <= w.Depth && Mathf.Abs(across) <= w.Across / 2f && y <= w.Underside + 0.02f;
    }
}

/// <summary>Rests on a staircase: a floor every 6 to 12 stairs, varied along the way.</summary>
public class StairRestTests
{
    /// <summary>Where the ground leaves room for a floor. On a slope at the stair's own grade (1:2) a floor buries itself or
    /// leaves the next leg off the ground, and the flight climbs on without one (a known limit: the earthwork to cut a rest
    /// into it is not priced).</summary>
    [Theory]
    [InlineData(0.4f)]
    [InlineData(0.3f)]
    public void ALongClimbRestsOnAFloorAtLeastEveryTwelveStairs(float grade)
    {
        StairPlan plan = StairLayout.Build(new Vector2(0f, 0f), new Vector2(0f, 60f / grade), (x, z) => grade * z);
        Assert.True(plan.Reached, plan.Reason);
        Assert.True(plan.Stairs >= 40, $"{plan.Stairs} stairs");
        Assert.InRange(plan.LongestFlight, 1, StairLayout.RestEveryMax);
    }

    [Fact]
    public void TheStairsDueBeforeARestVaryAlongTheWayWithinSixToTwelve()
    {
        var due = Enumerable.Range(0, 200).Select(i => StairLayout.RestDueAt(new Vector2(i * 7.3f, i * 3.1f))).ToList();
        Assert.All(due, d => Assert.InRange(d, StairLayout.RestEveryMin, StairLayout.RestEveryMax));
        Assert.True(due.Distinct().Count() >= 4, string.Join(" ", due.Distinct()));
        Assert.Equal(StairLayout.RestDueAt(new Vector2(12f, 34f)), StairLayout.RestDueAt(new Vector2(12f, 34f)));
    }

}

/// <summary>Black marble cornices under a turn's 1 x 1 filler: the cornice's overhanging metre under the block,
/// its top level with the block's bottom, not sticking out half a metre either side.</summary>
public class CorniceUnderFillerTests
{
    [Fact]
    public void ACorniceUnderAOneByOneFillerHasItsOverhangUnderTheBlock()
    {
        StairKit kit = StairKit.BlackMarble;
        // A 1 x 1 filler heading north at (0, 0), its top at 20 m; the ground rises east (the wall side) and falls west.
        var filler = new StructurePiece { Kind = StructurePieceKind.StairFiller, Prefab = "blackmarble_1x1",
            Position = new Vector3(0f, 20f - kit.FloorTop, 0f), YawDegrees = 180f };
        Func<float, float, float> ground = (x, z) => 16f + 2f * x;
        StairSupport.Walk w = StairSupport.WalkOf(filler, kit)!.Value;
        Assert.Equal(1f, w.Across, 3);
        List<StructurePiece> laid = StairSupport.Lay(kit, new List<StructurePiece> { filler }, ground, (x, z) => ground(x, z));
        StructurePiece top = laid.Where(p => p.Prefab == "blackmarble_out_1").OrderByDescending(p => p.Position.y).First();
        // Its top (origin + 1 m) meets the block's bottom.
        Assert.Equal(w.Underside, top.Position.y + 1f, 3);
        // Its middle half a metre toward the wall (east), so its 2 m top spans the block's metre and a metre into the wall side.
        float across = Vector2.Dot(new Vector2(top.Position.x, top.Position.z) - w.At(w.Depth / 2f, 0f), w.Right);
        Assert.Equal(0.5f * Math.Sign(Vector2.Dot(new Vector2(1f, 0f), w.Right)), across, 3);
    }

    /// <summary>Stone, held high: the block and arch under a turn's 1 x 1 filler stand half a metre toward the wall, under the
    /// filler and a metre into the wall side, not half a metre out past it (-4786.5,1728.1).</summary>
    [Fact]
    public void AnArchUnderAOneByOneFillerStandsUnderTheBlock()
    {
        StairKit kit = StairKit.Stone;
        var filler = new StructurePiece { Kind = StructurePieceKind.StairFiller, Prefab = "stone_wall_1x1",
            Position = new Vector3(0f, 20f - kit.FloorTop, 0f), YawDegrees = 180f };
        // The wall side rises east at 2:1, well over the wall height under the west edge.
        Func<float, float, float> ground = (x, z) => 15.2f + 2f * x;
        StairSupport.Walk w = StairSupport.WalkOf(filler, kit)!.Value;
        Assert.Equal(1f, w.Across, 3);
        Assert.Equal(StairSupport.Style.Arch, StairSupport.Choose(kit, w, ground).Style);
        List<StructurePiece> laid = StairSupport.Lay(kit, new List<StructurePiece> { filler }, ground, (x, z) => ground(x, z));
        float wallSide = 0.5f * Math.Sign(Vector2.Dot(new Vector2(1f, 0f), w.Right));
        foreach (string prefab in new[] { "stone_wall_2x1", "stone_arch" })
        {
            StructurePiece top = laid.Where(p => p.Prefab == prefab).OrderByDescending(p => p.Position.y).First();
            float across = Vector2.Dot(new Vector2(top.Position.x, top.Position.z) - w.At(w.Depth / 2f, 0f), w.Right);
            Assert.Equal(wallSide, across, 3);
        }
    }
}

/// <summary>Black marble's square corner: the 2 x 2 floor at a turn's outer corner held by blackmarble_outcorner, not by
/// straight cornices (in game: the black marble cornice corner used instead).</summary>
public class BlackMarbleCornerTests
{
    private static readonly StairKit Kit = StairKit.BlackMarble;

    private static StructurePiece Walked(StructurePieceKind kind, string prefab, float x, float y, float z, float yaw) =>
        new() { Kind = kind, Prefab = prefab, Position = new Vector3(x, y, z), YawDegrees = yaw };

    private static bool Has(IEnumerable<StructurePiece> laid, string prefab, Vector3 at, float yaw) =>
        laid.Any(p => p.Prefab == prefab && Vector3.Distance(p.Position, at) < 0.05f
            && Mathf.Abs(StairLayout.Wrap(p.YawDegrees - yaw + 180f) - 180f) < 1f);

    /// <summary>The straight cornices whose middles stand under a walked piece's footprint.</summary>
    private static List<StructurePiece> StraightUnder(IEnumerable<StructurePiece> laid, StairSupport.Walk w) =>
        laid.Where(p => p.Prefab == "blackmarble_out_1" && p.Position.y < w.Underside).Where(p =>
        {
            Vector2 d = new Vector2(p.Position.x, p.Position.z) - w.Back;
            float along = Vector2.Dot(d, w.Dir), across = Vector2.Dot(d, w.Right);
            return along > 0.01f && along < w.Depth - 0.01f && Mathf.Abs(across) < w.Across / 2f - 0.01f;
        }).ToList();

    /// <summary>
    /// A staircase at -9323,-790, as laid in game: a stair west, one turned to 292.5, the switchback's landing two floors
    /// across (the outer corner floor where the stair arrives, the other on its wall side), and the flight back down
    /// south-east. The slope rises to the south-south-west, the floors' left (wall) side. The kit held the corner floor on
    /// two straight cornices a course, two courses down; by hand the top course was replaced with one blackmarble_outcorner,
    /// centred under the floor, its top at the floor's underside, turned 292.5 so its overhangs face the floor's open
    /// downhill side and its open far end. The floor beside it keeps its straight cornices as the kit laid them.
    /// </summary>
    [Fact]
    public void ASquareTurnsCornerFloorIsHeldByAnOutcornerAsLaidByHand()
    {
        var walking = new List<StructurePiece>
        {
            Walked(StructurePieceKind.StairStep, "blackmarble_stair", -9318.830f, 104.275f, -791.112f, 90f),
            Walked(StructurePieceKind.StairStep, "blackmarble_stair", -9321.137f, 105.275f, -790.653f, 112.5f),
            Walked(StructurePieceKind.StairFloor, "blackmarble_floor", -9322.984f, 105.775f, -789.888f, 292.5f),
            Walked(StructurePieceKind.StairFloor, "blackmarble_floor", -9323.750f, 105.775f, -791.735f, 292.5f),
            Walked(StructurePieceKind.StairStep, "blackmarble_stair", -9321.795f, 106.275f, -793.042f, 315f),
        };
        // A 2:1 face rising to the floors' left, 101 m under the corner floor's middle: two cornice courses under it, one
        // under the floor beside it, as in game.
        Vector2 corner = new(-9322.984f, -789.888f), uphill = -StairLayout.Right(292.5f);
        Func<float, float, float> ground = (x, z) => 101f + 2f * Vector2.Dot(new Vector2(x, z) - corner, uphill);
        List<StructurePiece> laid = StairSupport.Lay(Kit, walking, ground, (x, z) => ground(x, z));
        StairSupport.Walk floor = StairSupport.WalkOf(walking[2], Kit)!.Value;
        StairSupport.Choice c = StairSupport.Choose(Kit, floor, ground);
        Assert.Equal(StairSupport.Style.Cornice, c.Style);
        Assert.Equal(2, c.Courses);
        // The hand-laid outcorner.
        Assert.True(Has(laid, "blackmarble_outcorner", new Vector3(-9322.984f, 104.275f, -789.888f), 292.5f),
            "no blackmarble_outcorner at (-9322.984, 104.275, -789.888) yaw 292.5");
        // No straight cornice under the corner floor.
        Assert.Empty(StraightUnder(laid, floor));
        // The floor beside it keeps the kit's straight cornices.
        Assert.True(Has(laid, "blackmarble_out_1", new Vector3(-9323.288f, 104.275f, -791.927f), 292.5f));
        Assert.True(Has(laid, "blackmarble_out_1", new Vector3(-9324.212f, 104.275f, -791.544f), 292.5f));
        StoneArchTurnTests.AssertNoSupportStandsInAWalkedPiece(Kit, walking.Concat(laid).ToList());
    }

    /// <summary>
    /// The layout's own 90 degree turn (at -9253,-804, laid as in game): a stair north-west onto a floor, the
    /// turn's wedge a 2 x 2 floor past it, the flight off south-west from its left side. The slope rising to the south-west
    /// (the flight climbs into it), the wedge floor is the outer corner: held by an outcorner turned as the floors, the
    /// landing between the stair and it by straight cornices.
    /// </summary>
    [Fact]
    public void TheWedgeFloorOfALayoutsSquareTurnIsHeldByAnOutcorner()
    {
        var walking = new List<StructurePiece>
        {
            Walked(StructurePieceKind.StairStep, "blackmarble_stair", -9250.240f, 77.275f, -806.567f, 135f),
            Walked(StructurePieceKind.StairFloor, "blackmarble_floor", -9251.654f, 77.775f, -805.152f, 315f),
            Walked(StructurePieceKind.StairFiller, "blackmarble_floor", -9253.068f, 77.775f, -803.738f, 315f),
            Walked(StructurePieceKind.StairStep, "blackmarble_stair", -9254.482f, 78.275f, -805.152f, 45f),
            Walked(StructurePieceKind.StairStep, "blackmarble_stair", -9255.896f, 79.275f, -806.566f, 45f),
        };
        Vector2 corner = new(-9253.068f, -803.738f), uphill = -StairLayout.Right(315f);
        Func<float, float, float> ground = (x, z) => 73f + 2f * Vector2.Dot(new Vector2(x, z) - corner, uphill);
        List<StructurePiece> laid = StairSupport.Lay(Kit, walking, ground, (x, z) => ground(x, z));
        StairSupport.Walk wedge = StairSupport.WalkOf(walking[2], Kit)!.Value, landing = StairSupport.WalkOf(walking[1], Kit)!.Value;
        Assert.Equal(2, StairSupport.Choose(Kit, wedge, ground).Courses);
        Assert.True(Has(laid, "blackmarble_outcorner", new Vector3(-9253.068f, 76.275f, -803.738f), 315f),
            "no blackmarble_outcorner under the wedge floor");
        Assert.Empty(StraightUnder(laid, wedge));
        Assert.DoesNotContain(laid, p => p.Prefab == "blackmarble_outcorner" && Vector2.Distance(new Vector2(p.Position.x, p.Position.z), landing.At(1f, 0f)) < 0.01f);
        Assert.Equal(2, StraightUnder(laid, landing).Count(p => Mathf.Abs(p.Position.y + 1f - landing.Underside) < 0.01f));
        StoneArchTurnTests.AssertNoSupportStandsInAWalkedPiece(Kit, walking.Concat(laid).ToList());
    }

    /// <summary>
    /// Held high, the corner's column goes down course by course: each outcorner 2 m lower and a metre in on both its faces,
    /// into the slope and back from the open end, its top's outer edges flush under the block of the one above,
    /// down to its own block in the ground. A floor turned off north from a stair, the next floor on its west (wall) side,
    /// the slope rising west a metre a metre: four courses, stepping south-west.
    /// </summary>
    [Fact]
    public void ACornerHeldHighIsAColumnOfOutcornersSteppingIntoTheSlope()
    {
        var walking = new List<StructurePiece>
        {
            Walked(StructurePieceKind.StairStep, "blackmarble_stair", 0f, 19.5f, -2f, 180f),
            Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 0f, 20f, 0f, 0f),
            Walked(StructurePieceKind.StairFloor, "blackmarble_floor", -2f, 20f, 0f, 0f),
        };
        Func<float, float, float> ground = (x, z) => 11f - x;
        List<StructurePiece> laid = StairSupport.Lay(Kit, walking, ground, (x, z) => ground(x, z));
        StairSupport.Walk floor = StairSupport.WalkOf(walking[1], Kit)!.Value;
        StairSupport.Choice c = StairSupport.Choose(Kit, floor, ground);
        Assert.Equal(-1, c.Side);
        Assert.Equal(4, c.Courses);
        // Its overhangs face east (the open downhill side) and north (the open far end): turned 0.
        for (int course = 0; course < c.Courses; course++)
            Assert.True(Has(laid, "blackmarble_outcorner", new Vector3(-course, floor.Underside - 1f - 2f * course, -course), 0f),
                $"no outcorner in course {course}");
        Assert.Equal(c.Courses, laid.Count(p => p.Prefab == "blackmarble_outcorner"));
        Assert.Empty(StraightUnder(laid, floor));
        AssertEachOutcornerStandsFlushUnderTheBlockAbove(laid, 1);
        // The floor beside it, whose downhill side the corner floor closes, and the stair keep straight cornices.
        StairSupport.Walk beside = StairSupport.WalkOf(walking[2], Kit)!.Value;
        Assert.Equal(2, StraightUnder(laid, beside).Count(p => Mathf.Abs(p.Position.y + 1f - beside.Underside) < 0.01f));
        Assert.Contains(laid, p => p.Prefab == "blackmarble_out_1" && Vector3.Distance(p.Position, new Vector3(0f, 18.5f, -1.5f)) < 0.01f);
        StoneArchTurnTests.AssertNoSupportStandsInAWalkedPiece(Kit, walking.Concat(laid).ToList());
    }

    /// <summary>
    /// Each outcorner but the top one of its column stands a metre in on both its faces from the one above (its local -x
    /// and -z), turned the same, 2 m lower: its 2 x 2 top's outer edges flush under the block (the -x, -z quarter) of the
    /// one above, as a straight column's top is under its block on its one face. <paramref name="columns"/> columns.
    /// </summary>
    internal static void AssertEachOutcornerStandsFlushUnderTheBlockAbove(IReadOnlyList<StructurePiece> laid, int columns)
    {
        var corners = laid.Where(p => p.Prefab == "blackmarble_outcorner").ToList();
        int tops = 0;
        foreach (StructurePiece lower in corners)
        {
            StructurePiece? upper = corners.FirstOrDefault(u => Mathf.Abs(u.Position.y - 2f - lower.Position.y) < 0.01f
                && Vector2.Distance(new Vector2(u.Position.x, u.Position.z), new Vector2(lower.Position.x, lower.Position.z)) < 2f);
            if (upper == null) { tops++; continue; }
            Assert.True(Mathf.Abs(StairLayout.Wrap(lower.YawDegrees - upper.YawDegrees + 180f) - 180f) < 0.1f,
                $"outcorner at {lower.Position} turned {lower.YawDegrees}, the one above {upper.YawDegrees}");
            Vector2 flush = new Vector2(upper.Position.x, upper.Position.z) - StairLayout.Right(upper.YawDegrees) - StairLayout.Forward(upper.YawDegrees);
            Assert.True(Vector2.Distance(flush, new Vector2(lower.Position.x, lower.Position.z)) < 0.01f,
                $"outcorner at {lower.Position}: {Vector2.Distance(flush, new Vector2(lower.Position.x, lower.Position.z)):F3} m off flush under the block of the one at {upper.Position} (expected {flush})");
        }
        Assert.Equal(columns, tops);
    }

    /// <summary>No straight cornice lies wholly in an outcorner of its own course, laid twice with its faces flickering.</summary>
    internal static void AssertNoCorniceInAnOutcorner(IReadOnlyList<StructurePiece> laid)
    {
        foreach (StructurePiece o in laid.Where(p => p.Prefab == "blackmarble_outcorner"))
        {
            Vector2 oat = new(o.Position.x, o.Position.z), ox = StairLayout.Right(o.YawDegrees), oz = StairLayout.Forward(o.YawDegrees);
            foreach (StructurePiece k in laid.Where(p => p.Prefab == "blackmarble_out_1" && Mathf.Abs(p.Position.y - o.Position.y) < 0.01f))
            {
                Vector2 d = new Vector2(k.Position.x, k.Position.z) - oat;
                // Its block (its local -x half) the outcorner's, its middle within the outcorner's top.
                Assert.False(Mathf.Abs(Vector2.Dot(d, ox)) < 1f && Mathf.Abs(Vector2.Dot(d, oz)) < 1f
                    && Vector2.Distance(new Vector2(k.Position.x, k.Position.z) - 0.5f * StairLayout.Right(k.YawDegrees), oat - 0.5f * ox - 0.5f * oz) < 0.01f,
                    $"blackmarble_out_1 at {k.Position} yaw {k.YawDegrees} lies in the outcorner at {o.Position} yaw {o.YawDegrees}");
            }
        }
    }

    /// <summary>
    /// In game, a black marble staircase from 1576,-6160 to 1600,-6184: the lower outcorner half a block
    /// or so too far out, not lined up with the bottom of the one above it. Its two switchbacks as laid in
    /// game, the corner floor held on two courses: the lower outcorner stood a metre into the slope from the upper only,
    /// its top a metre out past the upper's block on the open end's face (1585.376,45.861,-6167.365 under
    /// 1584.452,47.861,-6167.748; 1581.845,38.861,-6184.135 under 1580.921,40.861,-6183.752). It steps back from the open
    /// end too, its top flush under the upper's block on both faces, and the arriving stair's cornice that course, wholly
    /// in it, is not laid twice. A 2:1 face rising to the wall side (where the switchback's other floor is), two courses as
    /// in game.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ASwitchbacksLowerOutcornerStandsFlushUnderTheBlockAbove(int turn)
    {
        List<StructurePiece> walking = turn == 0
            ? new List<StructurePiece>
            {
                Walked(StructurePieceKind.StairStep, "blackmarble_stair", 1585.676f, 47.861f, -6171.902f, 180f),
                Walked(StructurePieceKind.StairStep, "blackmarble_stair", 1585.217f, 48.861f, -6169.595f, 157.5f),
                Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 1584.452f, 49.361f, -6167.748f, 337.5f),
                Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 1586.300f, 49.361f, -6166.982f, 337.5f),
                Walked(StructurePieceKind.StairStep, "blackmarble_stair", 1587.065f, 49.861f, -6168.830f, 337.5f),
            }
            : new List<StructurePiece>
            {
                Walked(StructurePieceKind.StairStep, "blackmarble_stair", 1581.686f, 40.861f, -6179.291f, 337.5f),
                Walked(StructurePieceKind.StairStep, "blackmarble_stair", 1581.686f, 41.861f, -6181.904f, 22.5f),
                Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 1580.921f, 42.361f, -6183.752f, 202.5f),
                Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 1582.769f, 42.361f, -6184.517f, 202.5f),
                Walked(StructurePieceKind.StairStep, "blackmarble_stair", 1583.534f, 42.861f, -6182.669f, 202.5f),
            };
        StructurePiece cornerFloor = walking[2], other = walking[3];
        Vector2 corner = new(cornerFloor.Position.x, cornerFloor.Position.z);
        Vector2 uphill = (new Vector2(other.Position.x, other.Position.z) - corner).normalized;
        StairSupport.Walk floor = StairSupport.WalkOf(cornerFloor, Kit)!.Value;
        Func<float, float, float> ground = (x, z) => floor.Underside - 4.275f + 2f * Vector2.Dot(new Vector2(x, z) - corner, uphill);
        List<StructurePiece> laid = StairSupport.Lay(Kit, walking, ground, (x, z) => ground(x, z));
        Assert.Equal(2, StairSupport.Choose(Kit, floor, ground).Courses);
        var (upperAt, lowerAt, yaw) = turn == 0
            ? (new Vector3(1584.452f, 47.861f, -6167.748f), new Vector3(1585.759f, 45.861f, -6168.289f), 247.5f)
            : (new Vector3(1580.921f, 40.861f, -6183.752f), new Vector3(1582.228f, 38.861f, -6183.211f), 202.5f);
        Assert.True(Has(laid, "blackmarble_outcorner", upperAt, yaw), $"no outcorner at {upperAt} yaw {yaw}");
        Assert.True(Has(laid, "blackmarble_outcorner", lowerAt, yaw), $"no outcorner at {lowerAt} yaw {yaw}: " +
            string.Join(", ", laid.Where(p => p.Prefab == "blackmarble_outcorner").Select(p => $"{p.Position} {p.YawDegrees}")));
        Assert.Equal(2, laid.Count(p => p.Prefab == "blackmarble_outcorner"));
        AssertEachOutcornerStandsFlushUnderTheBlockAbove(laid, 1);
        AssertNoCorniceInAnOutcorner(laid);
        // The arriving stair keeps its top course of cornices and its block.
        StairSupport.Walk arriving = StairSupport.WalkOf(walking[1], Kit)!.Value;
        Assert.Contains(laid, p => p.Prefab == "blackmarble_out_1" && Vector2.Distance(new Vector2(p.Position.x, p.Position.z), arriving.At(1.5f, 0f)) < 0.01f
            && Mathf.Abs(p.Position.y - (arriving.Underside - 1f)) < 0.01f);
        StoneArchTurnTests.AssertNoSupportStandsInAWalkedPiece(Kit, walking.Concat(laid).ToList());
    }

    /// <summary>
    /// By the game's support rule (<see cref="SupportGraph"/>, black marble losing 1/8 a metre of centre to centre), a
    /// corner's column, stepping in on both faces, holds as many courses hung from the ground as a straight one
    /// (<see cref="StairSupport.MostHungCourses"/>, StairCorniceDepthTests), though each course keeps only 0.68 of the one
    /// under it (centres 2.45 m apart), a straight column 0.71 (2.24 m). A sheer face 6.5 m into the wall side, nothing
    /// under the corner short of it: eight courses, the lowest two touching the face, six hung over them. The column alone,
    /// nothing beside it lending support, and the corner floor on it hold at <see cref="DvergrViaduct.HoldMargin"/> times
    /// marble's least (the floor at 1.20, the top outcorner at 1500 x 0.68^6, about 150); with the staircase round it, more.
    /// A face 7.5 m in (seven hung) the search refuses, and no column is laid.
    /// </summary>
    [Fact]
    public void ACornersColumnHungFromTheGroundHoldsByTheGamesSupportRule()
    {
        var walking = new List<StructurePiece>
        {
            Walked(StructurePieceKind.StairStep, "blackmarble_stair", 0f, 19.5f, -2f, 180f),
            Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 0f, 20f, 0f, 0f),
            Walked(StructurePieceKind.StairFloor, "blackmarble_floor", -2f, 20f, 0f, 0f),
        };
        Func<float, float, float> ground = (x, z) => x < -6.5f ? 100f : -50f - x;
        List<StructurePiece> laid = StairSupport.Lay(Kit, walking, ground, (x, z) => ground(x, z));
        var corners = laid.Where(p => p.Prefab == "blackmarble_outcorner").OrderByDescending(p => p.Position.y).ToList();
        Assert.Equal(8, corners.Count);
        AssertEachOutcornerStandsFlushUnderTheBlockAbove(laid, 1);
        List<ViaductSupport.Held> alone = StairSupport.Holding(corners.Prepend(walking[1]).ToList(), (x, z) => ground(x, z));
        float[] support = corners.Select(c => alone.Single(h => h.Piece == c).Support).ToArray();
        Assert.Equal(new[] { false, false, false, false, false, false, true, true },
            support.Select(s => s >= SupportGraph.Marble.Max - 0.01f).ToArray());
        foreach (ViaductSupport.Held h in alone)
            Assert.True(h.Share >= DvergrViaduct.HoldMargin, $"{h.Piece.Prefab} at {h.Piece.Position}: support {h.Support:F1} under the margin");
        // Six hung courses keep 1500 x 0.68^6 at the top.
        Assert.InRange(support[0], 145f, 155f);
        foreach (ViaductSupport.Held h in StairSupport.Holding(walking.Concat(laid).ToList(), (x, z) => ground(x, z)))
            Assert.True(h.Share >= DvergrViaduct.HoldMargin, $"{h.Piece.Prefab} at {h.Piece.Position}: support {h.Support:F1} under the margin");

        Func<float, float, float> deeper = (x, z) => x < -7.5f ? 100f : -50f - x;
        Assert.True(StairSupport.Choose(Kit, StairSupport.WalkOf(walking[1], Kit)!.Value, deeper).Refused);
        Assert.Empty(StairSupport.Lay(Kit, walking, deeper, (x, z) => deeper(x, z)).Where(p => p.Prefab == "blackmarble_outcorner"));
    }

    /// <summary>
    /// The corner's column steps in on both faces whichever way the corner is turned (every 22.5 degrees), whichever side
    /// the wall is and whichever end of the floor the way comes on at: the high corner of
    /// <see cref="ACornerHeldHighIsAColumnOfOutcornersSteppingIntoTheSlope"/> turned, mirrored across its flight and along it.
    /// Its overhangs face away from the wall side's floor and from the stair.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ACornersColumnStepsInOnBothFacesHoweverItIsTurned(bool wallEast, bool stairAtFarEnd)
    {
        float sx = wallEast ? -1f : 1f, sz = stairAtFarEnd ? -1f : 1f;
        for (int k = 0; k < 16; k++)
        {
            float r = 22.5f * k;
            Vector2 Turn(float x, float z) => StairLayout.Right(r) * x + StairLayout.Forward(r) * z;
            Vector2 Back(float x, float z) => new(Vector2.Dot(new Vector2(x, z), StairLayout.Right(r)), Vector2.Dot(new Vector2(x, z), StairLayout.Forward(r)));
            StructurePiece At(StructurePieceKind kind, string prefab, float x, float y, float z, float yaw)
            {
                Vector2 p = Turn(x, z);
                return Walked(kind, prefab, p.x, y, p.y, StairLayout.Wrap(yaw + r));
            }
            // The base: a stair from the south onto a floor at the origin heading north, the next floor west; mirrored, the
            // floors keep their heading (a 2 x 2 floor turned 180 is the same floor).
            var walking = new List<StructurePiece>
            {
                At(StructurePieceKind.StairStep, "blackmarble_stair", 0f, 19.5f, -2f * sz, sz > 0 ? 180f : 0f),
                At(StructurePieceKind.StairFloor, "blackmarble_floor", 0f, 20f, 0f, 0f),
                At(StructurePieceKind.StairFloor, "blackmarble_floor", -2f * sx, 20f, 0f, 0f),
            };
            Func<float, float, float> ground = (x, z) => 11f - sx * Back(x, z).x;
            List<StructurePiece> laid = StairSupport.Lay(Kit, walking, ground, (x, z) => ground(x, z));
            var corners = laid.Where(p => p.Prefab == "blackmarble_outcorner").ToList();
            Assert.True(corners.Count == 4, $"turned {r}, wall east {wallEast}, stair at the far end {stairAtFarEnd}: {corners.Count} outcorners");
            StructurePiece top = corners.OrderByDescending(p => p.Position.y).First();
            Assert.True(Vector2.Distance(new Vector2(top.Position.x, top.Position.z), Turn(0f, 0f)) < 0.01f, $"turned {r}: the top outcorner is off the floor's middle");
            Vector2 overhang = StairLayout.Right(top.YawDegrees) + StairLayout.Forward(top.YawDegrees);
            Assert.True(Vector2.Dot(overhang, Turn(sx, 0f)) > 0.5f && Vector2.Dot(overhang, Turn(0f, sz)) > 0.5f,
                $"turned {r}, wall east {wallEast}, stair at the far end {stairAtFarEnd}: the outcorner turned {top.YawDegrees} faces its overhangs in");
            AssertEachOutcornerStandsFlushUnderTheBlockAbove(laid, 1);
            AssertNoCorniceInAnOutcorner(laid);
            StoneArchTurnTests.AssertNoSupportStandsInAWalkedPiece(Kit, walking.Concat(laid).ToList());
        }
    }
}

/// <summary>Stone walls at a corner (in game: the 90 degree wall corner had missing blocks):
/// a landing's open end is walled across, and where that wall meets the wall along the flight the courses interlock, a
/// course ending in a 2x1 turned along the other wall rather than a 1x1, which wall runs through alternating course by
/// course.</summary>
public class StoneWallCornerTests
{
    private static readonly StairKit Stone = StairKit.Stone;

    private static StructurePiece Walked(StructurePieceKind kind, string prefab, float x, float y, float z, float yaw) =>
        new() { Kind = kind, Prefab = prefab, Position = new Vector3(x, y, z), YawDegrees = yaw };

    private static bool Has(IEnumerable<StructurePiece> laid, string prefab, float x, float y, float z, float yaw) =>
        laid.Any(p => p.Prefab == prefab && Vector3.Distance(p.Position, new Vector3(x, y, z)) < 0.05f && SameYaw(p, yaw));

    /// <summary>A wall block's yaw, either way round (each is the same turned half round).</summary>
    private static bool SameYaw(StructurePiece p, float yaw) => Mathf.Abs(StairLayout.Wrap(p.YawDegrees - yaw + 90f) % 180f - 90f) < 1f;

    /// <summary>The wall blocks over a point at a course (centred <paramref name="y"/>).</summary>
    private static List<StructurePiece> Over(IEnumerable<StructurePiece> laid, Vector2 q, float y) => laid.Where(p =>
    {
        if (!p.Prefab.StartsWith("stone_wall_") || Mathf.Abs(p.Position.y - y) > 0.01f) return false;
        BuriedPieces.BoundsOf(p.Prefab, out Vector3 min, out Vector3 max);
        Vector2 d = q - new Vector2(p.Position.x, p.Position.z);
        float x = Vector2.Dot(d, StairLayout.Right(p.YawDegrees)), z = Vector2.Dot(d, StairLayout.Forward(p.YawDegrees));
        return x > min.x && x < max.x && z > min.z && z < max.z;
    }).ToList();

    /// <summary>
    /// A switchback as laid in game (the corner at -1902,6630): a stair turned 292.5 onto the landing's first
    /// floor, the second floor beside it on its right (uphill) side, and the flight back off it. Walls stand under the
    /// first floor's and flight's left (downhill) half and the second floor's left half, all running 202.5; under the
    /// first floor's right half at the landing's open end there was nothing. By hand the corner's 1x1 closure was taken out,
    /// stone_wall_1x1 (-1902.590, 209.397, 6629.921), and the 2x1 under the second floor's left half,
    /// stone_wall_2x1 (-1901.363, 210.397, 6631.577) turned 202.5, and laid two 2x1s turned 292.5 across the landing's
    /// end instead: (-1902.399, 209.397, 6630.383) and (-1902.016, 210.397, 6631.307), "replaced the 1x1 with a 1x2 going
    /// the perpendicular direction". The ground: 3.5 m under the landing's downhill edge at its end, rising 1.5 m a metre
    /// to the uphill side and a metre a metre back along the flight (as the in-game courses show: four under the corner, two
    /// under the first floor's uphill half, one under the second floor's wall, none under its uphill half).
    /// </summary>
    [Fact]
    public void ALandingsEndIsWalledAcrossAndBondedAtItsCornerAsLaidByHand()
    {
        var walking = new List<StructurePiece>
        {
            Walked(StructurePieceKind.StairStep, "stone_stair", -1900.089f, 210.897f, 6629.426f, 112.5f),
            Walked(StructurePieceKind.StairFloor, "stone_floor_2x2", -1901.937f, 211.397f, 6630.191f, 292.5f),
            Walked(StructurePieceKind.StairFloor, "stone_floor_2x2", -1901.172f, 211.397f, 6632.039f, 292.5f),
            Walked(StructurePieceKind.StairStep, "stone_stair", -1899.324f, 211.897f, 6631.273f, 292.5f),
            Walked(StructurePieceKind.StairStep, "stone_stair", -1897.476f, 212.897f, 6630.508f, 292.5f),
        };
        Vector2 landing = new(-1901.937f, 6630.191f), along = StairLayout.Forward(292.5f), across = StairLayout.Right(292.5f);
        Func<float, float, float> ground = (x, z) =>
        {
            Vector2 d = new Vector2(x, z) - landing;
            float a = Vector2.Dot(d, along), c = Vector2.Dot(d, across);
            return 208.9f + 1.5f * (c - 0.05f) + Mathf.Max(0f, -0.05f - a);
        };
        List<StructurePiece> laid = StairSupport.Lay(Stone, walking, ground, (x, z) => ground(x, z));
        // The walls along the flight as in game.
        Assert.True(Has(laid, "stone_wall_2x1", -1902.128f, 210.397f, 6629.729f, 202.5f));
        Assert.True(Has(laid, "stone_wall_2x1", -1901.204f, 209.397f, 6629.347f, 202.5f));
        Assert.True(Has(laid, "stone_wall_2x1", -1902.128f, 208.397f, 6629.729f, 202.5f));
        Assert.True(Has(laid, "stone_wall_1x1", -1902.590f, 207.397f, 6629.921f, 292.5f));
        Assert.True(Has(laid, "stone_wall_1x1", -1901.666f, 207.397f, 6629.538f, 292.5f));
        Assert.True(Has(laid, "stone_wall_2x1", -1900.281f, 210.397f, 6628.964f, 202.5f));
        // The two 2x1s laid by hand across the landing's end, and what was taken out.
        Assert.True(Has(laid, "stone_wall_2x1", -1902.399f, 209.397f, 6630.383f, 292.5f), "no 2x1 at (-1902.399, 209.397, 6630.383) turned 292.5");
        Assert.True(Has(laid, "stone_wall_2x1", -1902.016f, 210.397f, 6631.307f, 292.5f), "no 2x1 at (-1902.016, 210.397, 6631.307) turned 292.5");
        Assert.False(Has(laid, "stone_wall_1x1", -1902.590f, 209.397f, 6629.921f, 292.5f), "the corner's 1x1 closure is still laid");
        Assert.False(Has(laid, "stone_wall_2x1", -1901.363f, 210.397f, 6631.577f, 202.5f), "the second floor's 2x1 is still laid");
        // The landing's end: each metre across it, every course down to the ground under it, has one block over it.
        foreach (var (c, courses) in new[] { (-0.5f, 4), (0.5f, 2), (1.5f, 1) })
        {
            Vector2 q = landing + along * 0.5f + across * c;
            for (int k = 0; k < courses; k++)
                Assert.True(Over(laid, q, 210.397f - k).Count == 1, $"{Over(laid, q, 210.397f - k).Count} blocks at {c} across, course {k}");
            Assert.True(Over(laid, q, 210.397f - courses).Count == 0, $"a block under the {courses} courses at {c} across: {string.Join(", ", Over(laid, q, 210.397f - courses).Select(p => $"{p.Prefab} {p.Position} {p.YawDegrees}"))}");
        }
        StoneArchTurnTests.AssertNoSupportStandsInAWalkedPiece(Stone, walking.Concat(laid).ToList());
    }

    /// <summary>
    /// A wall's corner over several courses: three stairs climbing north onto a floor whose far end is open, the ground
    /// 1.2 m under all of it, falling a little to the east (the downhill side): the wall under the east half and the wall
    /// across the floor's far end meet at its north-east corner, five courses deep. Each course has one block over the
    /// corner, never a 1x1: the wall along the flight runs through it on one course, the wall across the end, turned, the
    /// next, and so on down.
    /// </summary>
    [Fact]
    public void AWallsCornerIsBondedCourseByCourseWithNoGapAndNoOneByOne()
    {
        var walking = new List<StructurePiece>();
        for (int i = 0; i < 3; i++)
            walking.Add(Walked(StructurePieceKind.StairStep, "stone_stair", 0f, 1f + i, 1f + 2f * i, 180f));
        walking.Add(Walked(StructurePieceKind.StairFloor, "stone_floor_2x2", 0f, 3.5f, 7f, 0f));
        Func<float, float, float> ground = (x, z) => -1.2f - 0.01f * x;
        List<StructurePiece> laid = StairSupport.Lay(Stone, walking, ground, (x, z) => ground(x, z));
        Vector2 corner = new(0.5f, 7.5f), mouth = new(-0.5f, 7.5f);
        var through = new List<bool>();
        for (int k = 0; k < 5; k++)
        {
            float y = 2.5f - k;
            StructurePiece block = Assert.Single(Over(laid, corner, y));
            Assert.Equal("stone_wall_2x1", block.Prefab);
            Assert.Single(Over(laid, mouth, y));
            // Along the flight (turned 270) or across the end (turned 0).
            Assert.True(SameYaw(block, 270f) || SameYaw(block, 0f), $"block at {block.Position} turned {block.YawDegrees}");
            through.Add(SameYaw(block, 270f));
        }
        Assert.Empty(Over(laid, corner, -2.5f));
        for (int k = 1; k < through.Count; k++)
            Assert.True(through[k] != through[k - 1], $"courses {k - 1} and {k} both {(through[k] ? "along the flight" : "across the end")}");
        // Every metre along the flight's wall, every course, has one block over it.
        for (int cell = 0; cell < 8; cell++)
        {
            float top = Mathf.Min(3f, 1f + cell / 2);
            for (float y = top - 0.5f; y > -2f; y -= 1f)
                Assert.True(Over(laid, new Vector2(0.5f, cell + 0.5f), y).Count == 1, $"cell {cell}, course at {y}");
        }
        StoneArchTurnTests.AssertNoSupportStandsInAWalkedPiece(Stone, walking.Concat(laid).ToList());
    }
}

/// <summary>A staircase laid weathered (StairWear, the Structures.Weathered setting): worn as a location's, some rails gone,
/// nothing anything rests on missing.</summary>
public class StairWearTests
{
    private static List<StructurePiece> Dressed(bool weathered)
    {
        // On a slope a metre under the flight as it climbs, so its kit holds every piece up (StairKit.Dress).
        Func<float, float, float> slope = (x, z) => 0.5f * z - 1f;
        StairPlan plan = StairLayout.LayFlight(new Vector2(0f, 0f), 0f, new[] { 0f, 0f, 0f, 0f, 22.5f, 22.5f, 22.5f, 22.5f, 22.5f, 22.5f },
            StairKit.Stone, slope);
        StairRun run = StairRun.FromPlan(new Vector2(0f, 0f), new Vector2(plan.End.x, plan.End.z), 2f, plan, StairKit.Stone);
        return StairKit.Stone.Dress(run, slope, (x, z) => slope(x, z), weathered);
    }

    [Fact]
    public void WeatheredSomeRailsAreGoneAndEveryPieceIsWornButNothingAnythingRestsOn()
    {
        List<StructurePiece> whole = Dressed(false), worn = Dressed(true);
        Assert.All(whole, p => Assert.Equal(1f, p.HealthFraction));
        Assert.All(worn, p => Assert.InRange(p.HealthFraction, BridgeLayout.LocationDamageMin, BridgeLayout.LocationDamageMax));
        // Every stair, floor, filler, support and post is still there; only rails went.
        foreach (StructurePieceKind kind in whole.Select(p => p.Kind).Distinct().Where(k => k != StructurePieceKind.Beam))
            Assert.Equal(whole.Count(p => p.Kind == kind), worn.Count(p => p.Kind == kind));
        int rails = whole.Count(p => p.Kind == StructurePieceKind.Beam), left = worn.Count(p => p.Kind == StructurePieceKind.Beam);
        Assert.InRange(left, 1, rails - 1);
        Assert.All(worn.Where(p => p.Kind == StructurePieceKind.Beam), r => Assert.Contains(whole, w => Vector3.Distance(w.Position, r.Position) < 0.001f && w.Prefab == r.Prefab));
    }

    [Fact]
    public void AStaircaseIsWornTheSameWayEveryTime()
    {
        List<StructurePiece> a = Dressed(true), b = Dressed(true);
        Assert.Equal(a.Select(p => (p.Prefab, p.Position, p.HealthFraction)), b.Select(p => (p.Prefab, p.Position, p.HealthFraction)));
    }
}

/// <summary>A staircase's railing (StairRailing, a hand-built railing prototype): both sides, from one stair in from the road,
/// never across the way on.</summary>
public class StairRailingTests
{
    private static List<StructurePiece> Rails(StairPlan plan, out StairRun run, Vector2? to = null)
    {
        run = StairRun.FromPlan(new Vector2(0f, 0f), to ?? new Vector2(plan.End.x, plan.End.z), 2f, plan, StairKit.Stone);
        return StairRailing.Lay(StairKit.Stone, run.Pieces, run.Ends, (x, z) => -50f);
    }

    [Theory]
    [InlineData(22.5f, 1)]
    [InlineData(45f, 2)]
    [InlineData(67.5f, 3)]
    [InlineData(-45f, 2)]
    public void ATurnsWedgeIsClosedByTheLowerFlightsOuterRailRunningOnAMetre(float turn, int perTier)
    {
        // Three stairs north from (0, 0) at 10 m, then three turned; the turn's joint is 13 m up, the outer front corner of the
        // third stair at (-1, 6) for a right turn (the railing prototype's 22.5 degree turns).
        float t = StairLayout.Wrap(turn), outer = turn > 0f ? -1f : 1f;
        StairPlan plan = StairLayout.LayFlight(new Vector2(0f, 0f), 10f, new[] { 0f, 0f, 0f, t, t, t }, StairKit.Stone, (x, z) => -50f);
        List<StructurePiece> rails = Rails(plan, out _);
        var wedge = rails.Where(p => p.Prefab is StairRailing.ShortRailPrefab or StairRailing.LevelRailPrefab).ToList();
        Assert.All(wedge, p => Assert.Equal(StairRailing.ShortRailPrefab, p.Prefab));
        Assert.Equal(perTier, wedge.Count(p => Mathf.Abs(p.Position.y - 13f) < 0.01f));
        Assert.Equal(perTier, wedge.Count(p => Mathf.Abs(p.Position.y - 13f - BridgeLayout.RailHeight) < 0.01f));
        // The lower flight's runs straight on: a metre on from its corner, to a post square to it.
        Assert.Contains(wedge, p => Mathf.Abs(p.Position.x - outer) < 0.01f && Mathf.Abs(p.Position.z - 6.5f) < 0.01f);
        var posts = rails.Where(p => p.Prefab == BridgeLayout.RailPostPrefab && Mathf.Abs(p.Position.y - BridgeLayout.RailHeight / 2f - 13f) < 0.01f).ToList();
        StructurePiece end = Assert.Single(posts, p => Mathf.Abs(p.Position.x - outer) < 0.01f && Mathf.Abs(p.Position.z - 7f) < 0.01f);
        Assert.Equal(0f, end.YawDegrees, 3);
        // At 22.5 degrees it ends by the upper flight's outer back corner, whose own post it stands in for.
        // The turn pivots on the inner corner (-outer, 6); the upper flight's outer back corner is 2 m across from it.
        Vector2 q = new Vector2(-outer, 6f) + StairLayout.Right(t) * (2f * outer);
        bool kept = posts.Any(p => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), q) < 0.05f);
        Assert.Equal(Mathf.Abs(turn) > 30f, kept);
    }

    [Fact]
    public void EachPostIsSquareToItsPieceAndToTheUpperWhereTwoShareIt()
    {
        // Three stairs north from (0, 0) at 10 m, then three turned 45 degrees; the turn's joint posts are shared by the
        // third stair (below) and the fourth (above, its underside 13 m).
        StairPlan plan = StairLayout.LayFlight(new Vector2(0f, 0f), 10f, new[] { 0f, 0f, 0f, 45f, 45f, 45f }, StairKit.Stone, (x, z) => -50f);
        var posts = Rails(plan, out _).Where(p => p.Prefab == BridgeLayout.RailPostPrefab).ToList();
        Assert.NotEmpty(posts);
        float Bottom(StructurePiece p) => p.Position.y - BridgeLayout.RailHeight / 2f;
        Assert.All(posts.Where(p => Bottom(p) < 12.99f), p => Assert.Equal(0f, p.YawDegrees, 3));
        Assert.All(posts.Where(p => Bottom(p) > 13.01f), p => Assert.Equal(45f, p.YawDegrees, 3));
        // The turn's inner corner (right of the third stair's top, (1, 6)) is shared: square to the fourth stair, above.
        StructurePiece inner = Assert.Single(posts, p => Mathf.Abs(Bottom(p) - 13f) < 0.01f && Mathf.Abs(p.Position.x - 1f) < 0.05f && Mathf.Abs(p.Position.z - 6f) < 0.05f);
        Assert.Equal(45f, inner.YawDegrees, 3);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(90f)]
    public void ALandingLaidOnPastTheRoadsEndHasNoRailTheRoadWouldMeet(float turn)
    {
        // Four stairs north from (0, 0), the road's end at the top of the fourth (0, 8), and a landing of two floors on past it,
        // the second straight on or turned (in game: a top landing railed on its far side, across the road coming onto it).
        var s = StairPieceKind.Stair; var f = StairPieceKind.Landing;
        StairPlan plan = StairLayout.LayFlight(new Vector2(0f, 0f), 10f, new[] { 0f, 0f, 0f, 0f, 0f, turn }, StairKit.Stone, (x, z) => -50f,
            new[] { s, s, s, s, f, f });
        // The road comes on at the landing's level: the ground past the top stair stands there.
        var run = StairRun.FromPlan(new Vector2(0f, 0f), new Vector2(0f, 8f), 2f, plan, StairKit.Stone);
        List<StructurePiece> rails = StairRailing.Lay(StairKit.Stone, run.Pieces, run.Ends, (x, z) => z >= 8 ? 14f : -50f);
        Assert.DoesNotContain(rails, p => p.Prefab == StairRailing.LevelRailPrefab);
        // Every stair railed both sides, down to the road and up to the landing: a handrail and a lower rail each.
        Assert.Equal(16, rails.Count(p => p.Prefab == BridgeLayout.StairRailPrefab));
    }

    /// <summary>
    /// A railing post is left out as standing wholly in the staircase's supports only where its measured box (wood_pole's,
    /// BuriedPieces.DataOf: 0.4 m square) is, corner by corner: a wall course whose face runs 0.15 m inside a post's middle
    /// leaves half of it clear, and the post is laid. On the parent a copy of its size (0.2 m square) put its corners inside
    /// the course and left it out.
    /// </summary>
    [Fact]
    public void APostHalfInAWallIsLaid()
    {
        StairPlan plan = StairLayout.LayFlight(new Vector2(0f, 0f), 10f, Enumerable.Repeat(0f, 6).ToList(), StairKit.Stone, (x, z) => -50f);
        var run = StairRun.FromPlan(new Vector2(0f, 0f), new Vector2(plan.End.x, plan.End.z), 2f, plan, StairKit.Stone);
        List<StructurePiece> open = StairRailing.Lay(StairKit.Stone, run.Pieces, run.Ends, (x, z) => -50f);
        StructurePiece post = open.Where(p => p.Prefab == BridgeLayout.RailPostPrefab).OrderBy(p => p.Position.y).ElementAt(4);
        Assert.Equal(0f, post.YawDegrees, 3);
        float bottom = post.Position.y - BridgeLayout.RailHeight * 0.5f;
        // A 2 x 1 x 2 course over the post's whole height, its face 0.15 m inside the post's middle along x.
        var course = new StructurePiece { Kind = StructurePieceKind.StairSupport, Prefab = "stone_floor_2x2",
            Position = new Vector3(post.Position.x + 1f - 0.15f, bottom + 0.5f, post.Position.z) };
        List<StructurePiece> walled = StairRailing.Lay(StairKit.Stone, run.Pieces, run.Ends, (x, z) => -50f, new[] { course });
        Assert.Contains(walled, p => p.Prefab == BridgeLayout.RailPostPrefab && Vector3.Distance(p.Position, post.Position) < 0.01f);
        // Wholly inside it (its face 0.25 m past the post's middle): left out.
        course.Position = new Vector3(post.Position.x + 1f - 0.25f, bottom + 0.5f, post.Position.z);
        List<StructurePiece> inside = StairRailing.Lay(StairKit.Stone, run.Pieces, run.Ends, (x, z) => -50f, new[] { course });
        Assert.DoesNotContain(inside, p => p.Prefab == BridgeLayout.RailPostPrefab && Vector3.Distance(p.Position, post.Position) < 0.01f);
    }

    [Fact]
    public void AStraightFlightIsRailedOnBothSidesRightDownToTheRoad()
    {
        StairPlan plan = StairLayout.LayFlight(new Vector2(0f, 0f), 10f, Enumerable.Repeat(0f, 6).ToList(), StairKit.Stone, (x, z) => -50f);
        List<StructurePiece> rails = Rails(plan, out _);
        var sloped = rails.Where(p => p.Prefab == BridgeLayout.StairRailPrefab).ToList();
        // Six stairs, every one railed (the end ones too, standing clear of the ground): two rails each side, the
        // handrail a metre over the walking line and the lower rail a metre under it.
        Assert.Equal(24, sloped.Count);
        Assert.Equal(12, sloped.Count(p => p.Position.x > 0.9f));
        Assert.Equal(12, sloped.Count(p => p.Position.x < -0.9f));
        Assert.All(rails.Where(p => p.Prefab == BridgeLayout.RailPostPrefab), p => Assert.Equal(1f, Mathf.Abs(p.Position.x), 3));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(22.5f)]
    [InlineData(45f)]
    [InlineData(-90f)]
    public void NoRailCrossesTheWayFromOnePieceToTheNext(float turn)
    {
        var s = StairPieceKind.Stair; var f = StairPieceKind.Landing;
        float t = StairLayout.Wrap(turn);
        StairPlan plan = StairLayout.LayFlight(new Vector2(0f, 0f), 10f, new[] { 0f, 0f, 0f, 0f, t, t, t }, StairKit.Stone, (x, z) => -50f,
            new[] { s, s, s, f, s, s, s });
        List<StructurePiece> rails = Rails(plan, out StairRun run);
        // The way on: each walked piece's middle to the next's, through a turn's wedge filler as laid.
        var walked = run.Pieces.Where(p => p.Kind is StructurePieceKind.StairStep or StructurePieceKind.StairFloor or StructurePieceKind.StairFiller)
            .Select(p => StairSupport.WalkOf(p, StairKit.Stone)!.Value).ToList();
        for (int i = 1; i < walked.Count; i++)
        {
            Vector2 a = walked[i - 1].At(walked[i - 1].Depth / 2f, 0f), b = walked[i].At(walked[i].Depth / 2f, 0f);
            foreach (StructurePiece rail in rails.Where(p => p.Prefab is StairRailing.LevelRailPrefab or StairRailing.ShortRailPrefab))
            {
                Vector2 c = new(rail.Position.x, rail.Position.z), d = StairLayout.Right(rail.YawDegrees);
                Assert.False(Crosses(a, b, c - d, c + d), $"rail at {rail.Position} yaw {rail.YawDegrees} crosses the way {a} -> {b}");
            }
        }
    }

    [Fact]
    public void AWalkwayJoggedFortyFiveDegreesIsRailedOnTheInsideOfEachJog()
    {
        // A cave's approach at 1578,6933: three stairs up east-south-east onto a walkway of floors, jogged south-south-east
        // and back, each jog's floors pivoting on their shared outer corner and overlapping on the inside; then a stair up
        // south onto a top floor and a stair down off it, as laid in game.
        var run = new StairRun { From = new Vector2(1578.08f, 6933.43f), To = new Vector2(1598.44f, 6918.11f), Width = 2f };
        StructurePiece Floor(float x, float z, float yaw) => Walked(StructurePieceKind.StairFloor, "stone_floor_2x2", x, 155.726f, z, yaw);
        run.Pieces.AddRange(new[]
        {
            Step(1577.996f, 153.226f, 6934.286f, 247.5f), Step(1579.844f, 154.226f, 6935.051f, 247.5f),
            Walked(StructurePieceKind.StairFiller, "stone_wall_2x1", 1581.230f, 154.726f, 6935.625f, 247.5f),
            Walked(StructurePieceKind.StairFiller, "stone_wall_1x1", 1581.263f, 154.726f, 6936.087f, 292.5f),
            Step(1582.457f, 155.226f, 6935.051f, 292.5f),
            Floor(1584.305f, 6934.286f, 112.5f), Floor(1586.153f, 6933.521f, 112.5f), Floor(1588.001f, 6932.755f, 112.5f),
            Floor(1589.542f, 6932.449f, 90f), Floor(1591.083f, 6932.143f, 112.5f), Floor(1592.931f, 6931.377f, 112.5f),
            Floor(1593.696f, 6930.612f, 157.5f), Floor(1594.462f, 6928.765f, 157.5f), Floor(1595.227f, 6926.917f, 157.5f),
            Floor(1595.992f, 6925.069f, 157.5f), Floor(1596.758f, 6924.304f, 112.5f), Floor(1598.606f, 6923.539f, 112.5f),
            Walked(StructurePieceKind.StairFiller, "stone_wall_2x1", 1599.991f, 155.726f, 6922.965f, 292.5f),
            Walked(StructurePieceKind.StairFiller, "stone_wall_2x1", 1600.147f, 155.726f, 6922.732f, 0f),
            Step(1600.147f, 156.226f, 6921.232f, 0f),
            Walked(StructurePieceKind.StairFiller, "stone_wall_2x1", 1600.147f, 156.726f, 6919.732f, 0f),
            Walked(StructurePieceKind.StairFiller, "stone_wall_1x1", 1600.561f, 156.726f, 6919.525f, 45f),
            Walked(StructurePieceKind.StairFloor, "stone_floor_2x2", 1599.147f, 156.726f, 6918.818f, 225f),
            Step(1597.733f, 156.226f, 6917.404f, 225f),
        });
        List<StructurePiece> rails = Railing(run);
        // Laid by hand on the inside of each jog: from each floor's clear corner a metre on along its inner edge, a lower rail
        // and a handrail to a post, the two posts a third of a metre apart where the edges run on into each other.
        foreach (float y in new[] { 156.226f, 157.226f })
        {
            AssertHas(rails, StairRailing.ShortRailPrefab, 1592.964f, y, 6929.768f, 67.5f, 180f);
            AssertHas(rails, StairRailing.ShortRailPrefab, 1592.086f, y, 6930.645f, 22.5f, 180f);
            AssertHas(rails, StairRailing.ShortRailPrefab, 1596.725f, y, 6925.914f, 67.5f, 180f);
            AssertHas(rails, StairRailing.ShortRailPrefab, 1597.603f, y, 6925.037f, 22.5f, 180f);
        }
        foreach (var (x, z) in new[] { (1592.548f, 6930.453f), (1592.773f, 6930.229f), (1596.916f, 6925.452f), (1597.141f, 6925.228f) })
            Assert.Single(rails, p => p.Prefab == BridgeLayout.RailPostPrefab && Vector3.Distance(p.Position, new Vector3(x, 156.726f, z)) < 0.05f);
        // Nothing else on the walkway changes: no rail over a floor's walk, only along its edges and those two runs; the 22.5
        // degree kinks either side of the turned floor, which overlap less, keep their railing as laid.
        foreach (Vector2 jog in new[] { new Vector2(1592.7f, 6930.4f), new Vector2(1597.0f, 6925.3f) })
            Assert.Equal(4, rails.Count(p => p.Prefab == StairRailing.ShortRailPrefab && Vector2.Distance(new Vector2(p.Position.x, p.Position.z), jog) < 3f));
    }

    [Fact]
    public void ABlackMarbleTurnFloorJoggedFortyFiveDegreesIsRailedOnTheInsideOfTheJog()
    {
        // A black marble staircase's foot at 8872,-88: two stairs down south onto a floor, a second floor jogged 45 degrees
        // south-west overlapping it, its turn's filler floor, and the next flight turned square up north-west, as laid in game.
        var run = new StairRun { From = new Vector2(8872f, -88f), To = new Vector2(8856f, -112f), Width = 2f };
        run.Pieces.AddRange(new[]
        {
            Step(8872.574f, 35.073f, -88.386f, 0f, "blackmarble_stair"), Step(8872.574f, 36.073f, -90.386f, 0f, "blackmarble_stair"),
            Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 8872.574f, 36.573f, -92.386f, 180f),
            Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 8872.160f, 36.573f, -93.386f, 225f),
            Walked(StructurePieceKind.StairFiller, "blackmarble_floor", 8870.746f, 36.573f, -94.800f, 225f),
            Step(8869.332f, 37.073f, -93.386f, 135f, "blackmarble_stair"),
            Walked(StructurePieceKind.StairFiller, "blackmarble_2x1x1", 8868.271f, 37.573f, -92.325f, 135f),
            Step(8868.025f, 38.073f, -91.430f, 157.5f, "blackmarble_stair"), Step(8867.260f, 39.073f, -89.583f, 157.5f, "blackmarble_stair"),
        });
        List<StructurePiece> rails = Railing(run, StairKit.BlackMarble);
        // As laid by hand in game: from each floor's clear corner on the inside of the jog a metre on, a lower rail and a
        // handrail to a post.
        foreach (float y in new[] { 37.073f, 38.073f })
        {
            AssertHas(rails, StairRailing.ShortRailPrefab, 8871.574f, y, -91.886f, 90f, 180f);
            AssertHas(rails, StairRailing.ShortRailPrefab, 8871.100f, y, -93.033f, 135f, 180f);
        }
        foreach (var (x, z) in new[] { (8871.453f, -92.679f), (8871.574f, -92.386f) })
            Assert.Single(rails, p => p.Prefab == BridgeLayout.RailPostPrefab && Vector3.Distance(p.Position, new Vector3(x, 37.573f, z)) < 0.05f);
    }

    [Fact]
    public void AnEndFloorOnItsLandingIsRailedAsARoadsEndFloorOnlyOverADrop()
    {
        // In game, one rule at both kinds of end: a black marble staircase's top floor at -5381.3,5362.0 where the road carries
        // on, bare; and a cave's approach's last floor at 5046.8,-5892.6 on the cave's landing, the ground round it 0.4-0.7 m
        // under it, railed on both sides though it stands on the flat landing. The pieces as laid; the ground beside each end
        // floor 0.76 m under it, far below elsewhere.
        var road = new StairRun { From = new Vector2(-5392f, 5352f), To = new Vector2(-5380.9f, 5364.4f), Width = 2f, Kit = StairKit.BlackMarble };
        road.Pieces.Add(Step(-5382.020f, 62.596f, 5360.114f, 202.5f, "blackmarble_stair"));
        road.Pieces.Add(Walked(StructurePieceKind.StairFloor, "blackmarble_floor", -5381.254f, 63.096f, 5361.962f, 22.5f));
        var cave = new StairRun { From = new Vector2(5056.35f, -5918.65f), To = new Vector2(5047.49f, -5893.35f), Width = 2f, KeepsLocationRocks = true };
        cave.Pieces.Add(Step(5042.954f, 253.759f, -5890.229f, 270f));
        cave.Pieces.Add(Walked(StructurePieceKind.StairFiller, "stone_wall_2x1", 5044.454f, 254.259f, -5890.229f, 270f));
        cave.Pieces.Add(Walked(StructurePieceKind.StairFiller, "stone_wall_1x1", 5044.661f, 254.259f, -5889.815f, 315f));
        cave.Pieces.Add(Step(5045.368f, 254.759f, -5891.229f, 315f));
        cave.Pieces.Add(Walked(StructurePieceKind.StairFloor, "stone_floor_2x2", 5046.782f, 255.259f, -5892.643f, 135f));
        foreach (var (run, kit) in new[] { (road, StairKit.BlackMarble), (cave, StairKit.Stone) })
        {
            StructurePiece top = run.Pieces[run.Pieces.Count - 1];
            Vector2 at = new(top.Position.x, top.Position.z);
            float level = top.Position.y + 0.5f - 0.76f;
            List<StructurePiece> rails = Railing(run, kit, (x, z) => Vector2.Distance(new Vector2(x, z), at) <= 4f ? level : level - 20f);
            Assert.DoesNotContain(rails, r => r.Prefab is StairRailing.LevelRailPrefab or StairRailing.ShortRailPrefab
                && Vector2.Distance(new Vector2(r.Position.x, r.Position.z), at) < 1.2f);
            // Over a drop of more than a metre beside it, the same floor is railed there.
            List<StructurePiece> over = Railing(run, kit, (x, z) => Vector2.Distance(new Vector2(x, z), at) <= 4f ? level - 0.5f : level - 20f);
            Assert.Contains(over, r => r.Prefab == StairRailing.LevelRailPrefab && Vector2.Distance(new Vector2(r.Position.x, r.Position.z), at) < 1.2f);
        }
    }

    // Staircases laid as in game, piece by piece, and the rails placed by hand to show
    // what was missing, each found in what the kit lays (StairKit.Dress, so the railing is judged as it is built).

    private static StructurePiece Walked(StructurePieceKind kind, string prefab, float x, float y, float z, float yaw) =>
        new() { Kind = kind, Prefab = prefab, Position = new Vector3(x, y, z), YawDegrees = yaw };

    private static StructurePiece Step(float x, float y, float z, float yaw, string prefab = "stone_stair") =>
        Walked(StructurePieceKind.StairStep, prefab, x, y, z, yaw);

    /// <summary>A flight of <paramref name="count"/> stairs as the layout lays them: the first at (x, y, z), each next a run on
    /// up its heading and a rise higher.</summary>
    private static IEnumerable<StructurePiece> Flight(float x, float y, float z, float yaw, int count, string prefab = "stone_stair")
    {
        Vector2 on = StairLayout.Forward(StairLayout.Wrap(yaw + 180f)) * StairLayout.PieceRun;
        for (int i = 0; i < count; i++) yield return Step(x + on.x * i, y + i * StairLayout.PieceRise, z + on.y * i, yaw, prefab);
    }

    /// <summary>The railing <paramref name="kit"/> lays over <paramref name="run"/>, on finished ground
    /// <paramref name="ground"/> (far below by default), round the supports it lays (StairKit.Dress's railing, read whether
    /// or not the support rule would let the kit build the staircase: these layouts are the railing's, hung in the air).</summary>
    internal static List<StructurePiece> Railing(StairRun run, StairKit? kit = null, Func<int, int, float>? ground = null,
        IReadOnlyList<StructurePiece>? beside = null, Func<float, float, float>? natural = null)
    {
        Func<int, int, float> finished = ground ?? ((x, z) => -50f);
        StairKit k = kit ?? StairKit.Stone;
        List<StructurePiece> walking = run.Pieces.FindAll(p => p.Kind != StructurePieceKind.StairSupport);
        List<StructurePiece> supports = StairSupport.Lay(k, walking, natural ?? ((x, z) => finished(Mathf.FloorToInt(x), Mathf.FloorToInt(z))), finished);
        return StairRailing.Lay(k, walking, run.Ends, finished, supports, beside)
            .Where(p => p.Kind is StructurePieceKind.Beam or StructurePieceKind.Post).ToList();
    }

    /// <summary>Whether <paramref name="rails"/> hold <paramref name="prefab"/> at (x, y, z) within 5 cm, turned
    /// <paramref name="yaw"/> within a degree, a piece looking the same every <paramref name="symmetry"/> degrees.</summary>
    private static bool Has(List<StructurePiece> rails, string prefab, float x, float y, float z, float yaw, float symmetry = 360f) =>
        rails.Any(p => p.Prefab == prefab && Vector3.Distance(p.Position, new Vector3(x, y, z)) < 0.05f
            && Mathf.Abs(((p.YawDegrees - yaw + symmetry / 2f) % symmetry + symmetry) % symmetry - symmetry / 2f) < 1f);

    private static void AssertHas(List<StructurePiece> rails, string prefab, float x, float y, float z, float yaw, float symmetry = 360f) =>
        Assert.True(Has(rails, prefab, x, y, z, yaw, symmetry), FormattableString.Invariant($"no {prefab} at ({x}, {y}, {z}) yaw {yaw}"));

    private static void AssertHasNone(List<StructurePiece> rails, float x, float y, float z) =>
        Assert.DoesNotContain(rails, p => Vector3.Distance(p.Position, new Vector3(x, y, z)) < 0.05f);

    /// <summary>A staircase as the game saved it and its ground (Fixtures/stair-ends-*.txt: run, meets, pieces, ground rows).</summary>
    private static (StairRun run, Func<int, int, float> ground) SavedStaircase(string name)
    {
        var run = new StairRun();
        var vertices = new Dictionary<(int, int), float>();
        static float F(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        foreach (string line in System.IO.File.ReadAllLines(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name)))
        {
            string[] f = line.Split(',');
            switch (f[0])
            {
                case "run": run.From = new Vector2(F(f[1]), F(f[2])); run.To = new Vector2(F(f[3]), F(f[4])); run.Width = F(f[5]); run.KeepsLocationRocks = f[6] == "True"; break;
                case "meets": run.FromMeet = new Vector2(F(f[1]), F(f[2])); run.ToMeet = new Vector2(F(f[3]), F(f[4])); break;
                case "piece": run.Pieces.Add(Walked((StructurePieceKind)Enum.Parse(typeof(StructurePieceKind), f[1]), f[2], F(f[3]), F(f[4]), F(f[5]), F(f[6]))); break;
                case "ground": vertices[((int)F(f[1]), (int)F(f[2]))] = F(f[3]); break;
            }
        }
        return (run, (x, z) => vertices.TryGetValue((x, z), out float h) ? h : float.NaN);
    }

    [Fact]
    public void AFlightIsRailedRightDownToItsFootFloorAndUpToItsTopFloor()
    {
        // A staircase down onto a stone tower's landing (StoneTowerRuins05, -1916.8,6724.3), as the game saved it, on its
        // finished ground (stair-ends-l.txt), without the second top floor no longer laid (one floor onto its road, in game).
        // Its lowest stair (yaw 270) steps down through a turn's 2 x 2 filler onto its foot floor on the landing: its north
        // side over ground 0.3 m under its walking line, its south side over a drop of 2-3 m. Its top stair (yaw 247.5) steps
        // up through a turn's fillers onto its top floor: one side over ground 0.45 m under it, the other over a drop of 3.7 m.
        // Left open on their sides over the ground near walking height, both were railed again by hand ("the bottom step ...
        // probably doesn't need the one set of railings that is mostly level" was a mistake): a flight is railed right down to
        // and up to its end floors.
        var (run, ground) = SavedStaircase("stair-ends-l.txt");
        run.Pieces.RemoveAt(run.Pieces.Count - 1);
        List<StructurePiece> rails = Railing(run, ground: ground);
        foreach (float y in new[] { 276.940f, 277.940f })
        {
            // The lowest stair, railed on both sides as built by hand: two sloped rails on its north side as on its south...
            AssertHas(rails, BridgeLayout.StairRailPrefab, -1901.090f, y, 6716.517f, 0f, 180f);
            AssertHas(rails, BridgeLayout.StairRailPrefab, -1901.090f, y, 6714.517f, 0f, 180f);
            // ...and the end floor itself open on its side over the tower's level ground, as it was.
            AssertHasNone(rails, -1904.090f, y, 6717.517f);
        }
        // The post at the stair's foot on that side, where the floor's rail meets it (added by hand at -1902.09,6716.52).
        Assert.Contains(rails, p => p.Prefab == BridgeLayout.RailPostPrefab && Vector3.Distance(p.Position, new Vector3(-1902.090f, 277.440f, 6716.517f)) < 0.05f);
        // The top stair, railed on both its sides.
        Vector2 top = new(-1872.3174f, 6729.7515f), across = StairLayout.Right(67.5f);
        foreach (float side in new[] { 1f, -1f })
        {
            Vector2 at = top + across * side;
            foreach (float y in new[] { 290.940f, 291.940f })
                AssertHas(rails, BridgeLayout.StairRailPrefab, at.x, y, at.y, 337.5f, 180f);
        }
    }

    [Fact]
    public void ATopStairUnderTheLipIsRailedBesideTheBank()
    {
        // A mountain cave's approach (MountainCave02, foot -799.1,7489.1, top -781.6,7486.0), its top as laid in game (from
        // the snap points of its pieces): a flight climbing south-east, a turn's 2 x 1 filler, one stair east-south-east and the
        // top 2 x 2 floor. The bank rises beside the stairs' north-east side under the lip, the ground falls away on the other.
        // The top stair stood open on its bank side, the flight's rail stopping a stair short of the top floor: "the railing on
        // the stairs near the top could use 1 more leg ... it isn't level, we have a 2x2 block landing at the end".
        var run = new StairRun { From = new Vector2(-799.1f, 7489.1f), To = new Vector2(-781.6f, 7486.0f), Width = 2f, KeepsLocationRocks = true };
        run.Pieces.Add(Step(-785.8725f, 162.769f, 7489.0675f, 315f));
        run.Pieces.Add(Step(-784.4575f, 163.769f, 7487.6525f, 315f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFiller, "stone_wall_2x1", -783.3975f, 164.269f, 7486.5925f, 315f));
        run.Pieces.Add(Step(-782.5025f, 164.769f, 7486.3475f, 292.5f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFloor, "stone_floor_2x2", -780.6550f, 165.269f, 7485.5825f, 112.5f));
        Vector2 stair = new(-782.5025f, 7486.3475f), bank = -StairLayout.Right(112.5f);
        // The bank: level with the top stair's middle a metre out on its north-east side, rising on past it; far under elsewhere.
        float Ground(int x, int z)
        {
            float off = Vector2.Dot(new Vector2(x, z) - stair, bank);
            return off > 1f ? 165.2f + (off - 1f) * 0.8f : off < -1f ? 150f : 164.8f;
        }
        List<StructurePiece> rails = Railing(run, ground: Ground);
        Vector2 side = stair + bank;
        foreach (float y in new[] { 164.769f, 165.769f })
            AssertHas(rails, BridgeLayout.StairRailPrefab, side.x, y, side.y, 22.5f, 180f);
        // Its post at the top floor's corner.
        Vector2 corner = stair + bank + StairLayout.Forward(112.5f);
        Assert.Contains(rails, p => p.Prefab == BridgeLayout.RailPostPrefab
            && Vector3.Distance(p.Position, new Vector3(corner.x, 166.269f, corner.y)) < 0.05f);
    }

    [Fact]
    public void ARestBetweenTwoFlightsIsRailedOnLevelSidesAsTheFlightsAre()
    {
        // A black marble road staircase (1520,-6072 -> 1536,-6072) as laid in game: the flight climbing east-south-east onto a
        // floor at its recorded end, then on past it, a stair and its top floor (the end read in game at 1539.0,41.00,-6073.2).
        // The floor is a rest between two flights, not an end: in game it stood open along both its sides with the flights
        // either side railed. Railed as the flights are, whatever the drop: here the ground level with it on both sides.
        var run = new StairRun { From = new Vector2(1520f, -6072f), To = new Vector2(1536f, -6072f), Width = 2f,
            FromMeet = new Vector2(1520f, -6072f), ToMeet = new Vector2(1538.5251f, -6072.971f) };
        run.Pieces.Add(Step(1532.5198f, 38f, -6070.4844f, 292.5f, "blackmarble_stair"));
        run.Pieces.Add(Step(1534.3676f, 39f, -6071.2495f, 292.5f, "blackmarble_stair"));
        run.Pieces.Add(Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 1536.2153f, 39.5f, -6072.0146f, 112.5f));
        run.Pieces.Add(Step(1538.0630f, 40f, -6072.7800f, 292.5f, "blackmarble_stair"));
        run.Pieces.Add(Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 1539.9108f, 40.5f, -6073.5454f, 112.5f));
        Vector2 rest = new(1536.2153f, -6072.0146f), across = StairLayout.Right(112.5f);
        // Level with the rest (its top 40.0) beside both its sides, else far under.
        float Ground(int x, int z) => Vector2.Distance(new Vector2(x, z), rest + across * 1.5f) < 1.6f
            || Vector2.Distance(new Vector2(x, z), rest - across * 1.5f) < 1.6f ? 39.9f : 20f;
        List<StructurePiece> rails = Railing(run, StairKit.BlackMarble, Ground);
        foreach (float side in new[] { 1f, -1f })
        {
            Vector2 middle = rest + across * side;
            foreach (float y in new[] { 40f, 41f })
                AssertHas(rails, StairRailing.LevelRailPrefab, middle.x, y, middle.y, 22.5f, 180f);
        }
    }

    [Fact]
    public void EveryFlightRailHasALowerRailAMetreUnderIt()
    {
        // The tower 2 approach (StoneTowerRuins05_leet, -1917,6724): stairs 40007 and 40008 at yaw 112.5, one more either side.
        var run = new StairRun { From = new Vector2(-1925f, 6707f), To = new Vector2(-1938f, 6713f), Width = 2f };
        run.Pieces.AddRange(Flight(-1928.983f, 260.813f, 6709.169f, 112.5f, 4));
        List<StructurePiece> rails = Railing(run);
        // The kit's handrails, as in game, and the lower rails laid by hand a metre under each, both sides (ids 168477..168489).
        foreach (var (x, y, z) in new[] { (-1931.214f, 262.813f, 6709.010f), (-1933.061f, 263.813f, 6709.775f),
                     (-1930.448f, 262.813f, 6710.858f), (-1932.296f, 263.813f, 6711.623f) })
        {
            AssertHas(rails, BridgeLayout.StairRailPrefab, x, y, z, 202.5f);
            AssertHas(rails, BridgeLayout.StairRailPrefab, x, y - 1f, z, 202.5f);
        }
        // Every sloped rail has its twin, a metre under or over it.
        var sloped = rails.Where(p => p.Prefab == BridgeLayout.StairRailPrefab).ToList();
        Assert.Equal(16, sloped.Count);
        Assert.All(sloped, r => Assert.Contains(sloped, t => Vector2.Distance(new Vector2(t.Position.x, t.Position.z), new Vector2(r.Position.x, r.Position.z)) < 0.01f
            && Mathf.Abs(Mathf.Abs(t.Position.y - r.Position.y) - BridgeLayout.RailHeight) < 0.01f));
    }

    [Fact]
    public void AnEndStepInTheGroundHasNoRailingWhichStartsAtTheFirstStepStandingClear()
    {
        // The foot of the MountainCave02 approach (-3008,-1792; staircase -3006.11,-1748.94 -> -3010.32,-1782.8): its first
        // stair's underside at 110.468, the finished ground 111.44 over it; the ground falls away under the next.
        var run = new StairRun { From = new Vector2(-3006.11f, -1748.94f), To = new Vector2(-3020f, -1755f), Width = 2f };
        run.Pieces.AddRange(Flight(-3005.183f, 110.468f, -1748.560f, 67.5f, 4));
        var first = new Vector2(-3005.183f, -1748.560f);
        List<StructurePiece> rails = Railing(run, ground: (x, z) => Vector2.Distance(new Vector2(x, z), first) < 1.5f ? 111.44f : 100f);
        // Its rails lay in the snow, at the ground (111.468 and their posts); none now, nor any under them.
        AssertHasNone(rails, -3004.801f, 111.468f, -1749.484f);
        AssertHasNone(rails, -3005.566f, 111.468f, -1747.636f);
        AssertHasNone(rails, -3004.801f, 110.468f, -1749.484f);
        AssertHasNone(rails, -3005.566f, 110.468f, -1747.636f);
        Assert.DoesNotContain(rails, p => p.Position.y < 111.468f);
        // The second stair is railed both sides from its foot, where the first stair's top was, posts and all.
        AssertHas(rails, BridgeLayout.StairRailPrefab, -3006.649f, 112.468f, -1750.249f, 157.5f);
        AssertHas(rails, BridgeLayout.StairRailPrefab, -3007.414f, 112.468f, -1748.401f, 157.5f);
        AssertHas(rails, BridgeLayout.StairRailPrefab, -3006.649f, 111.468f, -1750.249f, 157.5f);
        AssertHas(rails, BridgeLayout.RailPostPrefab, -3005.725f, 111.968f, -1749.866f, 67.5f, 90f);
        AssertHas(rails, BridgeLayout.RailPostPrefab, -3006.490f, 111.968f, -1748.019f, 67.5f, 90f);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(3f)]
    public void AStaircaseEndingOnALocationsLandingIsRailedButOnTheSideFacingTheLocation(float pastTheEdge)
    {
        // The top of the tower 2 approach: the last stair heading east, a 2x1 and a 1x1 in the turn's wedge, and the top floor
        // turned 45 degrees toward the location, its far edge's middle at -1923.403,6715.182 (the location's ground there, or on past
        // the landing's lip).
        Vector2 toLocation = StairLayout.Forward(45f);
        Vector2 meet = new Vector2(-1923.403f, 6715.182f) + toLocation * pastTheEdge;
        var run = new StairRun { From = new Vector2(-1934f, 6713f), To = meet, Width = 2f, KeepsLocationRocks = true };
        run.Pieces.AddRange(Flight(-1928.524f, 274.813f, 6713.475f, 270f, 2));
        run.Pieces.Add(Walked(StructurePieceKind.StairFiller, "stone_wall_2x1", -1925.024f, 276.313f, 6713.475f, 270f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFiller, "stone_wall_1x1", -1924.817f, 276.313f, 6713.061f, 225f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFloor, "stone_floor_2x2", -1924.110f, 276.313f, 6714.475f, 45f));
        List<StructurePiece> rails = Railing(run, ground: LandingAhead(run.Pieces[run.Pieces.Count - 1]));
        // Its two sides railed at both heights, posts at its far corners; its far edge, the way onto the location, open.
        foreach (float y in new[] { 276.813f, 277.813f })
        {
            AssertHas(rails, StairRailing.LevelRailPrefab, -1924.817f, y, 6715.182f, 135f, 180f);
            AssertHas(rails, StairRailing.LevelRailPrefab, -1923.403f, y, 6713.768f, 135f, 180f);
        }
        AssertHas(rails, BridgeLayout.RailPostPrefab, -1924.110f, 277.313f, 6715.889f, 45f, 90f);
        AssertHas(rails, BridgeLayout.RailPostPrefab, -1922.696f, 277.313f, 6714.475f, 45f, 90f);
        Assert.DoesNotContain(rails, p => p.Prefab is StairRailing.LevelRailPrefab or StairRailing.ShortRailPrefab
            && Vector2.Distance(new Vector2(p.Position.x, p.Position.z), new Vector2(-1923.403f, 6715.182f)) < 0.6f);
        // Nothing across the way from the last stair onto the floor and on to the location.
        Vector2 floor = new(-1924.110f, 6714.475f);
        foreach (StructurePiece rail in rails.Where(p => p.Prefab is StairRailing.LevelRailPrefab or StairRailing.ShortRailPrefab))
        {
            Vector2 c = new(rail.Position.x, rail.Position.z), d = StairLayout.Right(rail.YawDegrees) * (rail.Prefab == StairRailing.ShortRailPrefab ? 0.5f : 1f);
            Assert.False(Crosses(new Vector2(-1926.524f, 6713.475f), floor, c - d, c + d), $"rail at {rail.Position} crosses the way onto the floor");
            Assert.False(Crosses(floor, meet + toLocation, c - d, c + d), $"rail at {rail.Position} crosses the way onto the location");
        }
    }

    /// <summary>Whether a level rail of <paramref name="rails"/> crosses the walked line <paramref name="a"/> to <paramref name="b"/>.</summary>
    private static StructurePiece? RailAcross(List<StructurePiece> rails, Vector2 a, Vector2 b)
    {
        foreach (StructurePiece rail in rails.Where(p => p.Prefab is StairRailing.LevelRailPrefab or StairRailing.ShortRailPrefab))
        {
            Vector2 c = new(rail.Position.x, rail.Position.z), d = StairLayout.Right(rail.YawDegrees) * (rail.Prefab == StairRailing.ShortRailPrefab ? 0.5f : 1f);
            if (Crosses(a, b, c - d, c + d)) return rail;
        }
        return null;
    }

    /// <summary>The ground of a location's landing level with the top of <paramref name="floor"/> (a tenth under it) on from its
    /// far edge, out to 2.45 m round a point 1.5 m past it, the vertices a step off it all that edge's samples read; far below
    /// elsewhere. A location's end floor is open where the landing stands at walking height past it, as a road's (StairRailing's
    /// Ways.Road); over a drop it is railed.</summary>
    private static Func<int, int, float> LandingAhead(StructurePiece floor, StairKit? kit = null)
    {
        StairSupport.Walk w = StairSupport.WalkOf(floor, kit ?? StairKit.Stone)!.Value;
        Vector2 ahead = w.At(w.Depth + 1.5f, 0f);
        float level = w.Surface(0f, kit ?? StairKit.Stone) - 0.1f;
        return (x, z) => Vector2.Distance(new Vector2(x, z), ahead) <= 2.45f ? level : -50f;
    }

    [Fact]
    public void AnApproachsTopFloorLaidOnPastItsRecordedEndIsOpenWhereTheWalkLeavesIt()
    {
        // In game: the top of an approach staircase onto a stone tower (StoneTowerRuins05_leet,
        // -1916.81,6724.33), recorded to the last stair's top (-1930.73,6719.25), the top floor laid on past it turned 45
        // degrees; railed on three sides, across the way off it onto the landing too (a railing across the stairs, not
        // just on the side, which should never happen). The pieces as laid (ids 58508..58512).
        var run = new StairRun { From = new Vector2(-1926.95f, 6699.84f), To = new Vector2(-1930.73f, 6719.25f), Width = 2f, KeepsLocationRocks = true };
        run.Pieces.AddRange(Flight(-1932.441f, 273.845f, 6714.124f, 180f, 2));
        run.Pieces.Add(Walked(StructurePieceKind.StairFiller, "stone_wall_2x1", -1932.441f, 275.345f, 6717.624f, 180f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFiller, "stone_wall_1x1", -1932.855f, 275.345f, 6717.831f, 225f));
        run.Pieces.Add(Step(-1931.441f, 275.845f, 6718.538f, 225f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFloor, "stone_floor_2x2", -1930.026f, 276.345f, 6719.952f, 45f));
        List<StructurePiece> rails = Railing(run, ground: LandingAhead(run.Pieces[run.Pieces.Count - 1]));
        // Its two sides railed at both heights, as in game.
        foreach (float y in new[] { 276.845f, 277.845f })
        {
            AssertHas(rails, StairRailing.LevelRailPrefab, -1930.734f, y, 6720.659f, 135f, 180f);
            AssertHas(rails, StairRailing.LevelRailPrefab, -1929.319f, y, 6719.245f, 135f, 180f);
        }
        // Its far edge, the way off it onto the landing, open (in game rails at -1929.319,276.845/277.845,6720.659 yaw 225).
        AssertHasNone(rails, -1929.319f, 276.845f, 6720.659f);
        AssertHasNone(rails, -1929.319f, 277.845f, 6720.659f);
        Vector2 stair = new(-1931.441f, 6718.538f), floor = new(-1930.026f, 6719.952f), on = StairLayout.Forward(45f);
        Assert.Null(RailAcross(rails, stair, floor));
        Assert.Null(RailAcross(rails, floor, floor + on * 2f));
    }

    /// <summary>The same top floor, its end recorded back on the last stair (so the floor is the walk's end, StairRailing's
    /// WalkOut, not the end's), where the finished ground past it is not known (NaN: a location placed after the staircase, a
    /// run with no expected location): the way off it is left open, as at a road's end (StairRailing's TakenOver, one rule), and
    /// its sides railed.</summary>
    [Fact]
    public void AnApproachsTopFloorIsOpenWhereTheWalkLeavesItOntoGroundNotKnown()
    {
        var run = new StairRun { From = new Vector2(-1926.95f, 6699.84f), To = new Vector2(-1932.441f, 6716.124f), Width = 2f, KeepsLocationRocks = true };
        run.Pieces.AddRange(Flight(-1932.441f, 273.845f, 6714.124f, 180f, 2));
        run.Pieces.Add(Walked(StructurePieceKind.StairFiller, "stone_wall_2x1", -1932.441f, 275.345f, 6717.624f, 180f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFiller, "stone_wall_1x1", -1932.855f, 275.345f, 6717.831f, 225f));
        run.Pieces.Add(Step(-1931.441f, 275.845f, 6718.538f, 225f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFloor, "stone_floor_2x2", -1930.026f, 276.345f, 6719.952f, 45f));
        Func<int, int, float> landing = LandingAhead(run.Pieces[run.Pieces.Count - 1]);
        Func<int, int, float> unknown = (x, z) => landing(x, z) > -50f ? float.NaN : -50f;
        List<StructurePiece> rails = Railing(run, ground: unknown);
        foreach (float y in new[] { 276.845f, 277.845f })
        {
            AssertHas(rails, StairRailing.LevelRailPrefab, -1930.734f, y, 6720.659f, 135f, 180f);
            AssertHas(rails, StairRailing.LevelRailPrefab, -1929.319f, y, 6719.245f, 135f, 180f);
        }
        AssertHasNone(rails, -1929.319f, 276.845f, 6720.659f);
        AssertHasNone(rails, -1929.319f, 277.845f, 6720.659f);
        Vector2 floor = new(-1930.026f, 6719.952f);
        Assert.Null(RailAcross(rails, floor, floor + StairLayout.Forward(45f) * 2f));
    }

    [Fact]
    public void AConnectorFloorBetweenTwoStaircasesMeetingEndToEndIsRailedOnBothSides()
    {
        // In game: a black marble staircase (-6880,-3672 -> -6920,-3656) climbs onto a 2x2 floor at
        // -6920.993,-3653.766 (yaw 292.5), and the next (-6920,-3656 -> -6928,-3640) goes on down from its far edge (the 2x2
        // connector with the stairs on each side looked right); its railing was missing on one side: railed on its downhill side, its
        // uphill side bare, the road's end the two share (-6920,-3656) judged to come onto it there, over ground a step or so
        // under it. The pieces as laid; the ground assumed: 1.25 m under the floor on its uphill side, 3.5 m on the other.
        var six = new StairRun { From = new Vector2(-6880f, -3672f), To = new Vector2(-6920f, -3656f), Width = 2f, Kit = StairKit.BlackMarble };
        six.Pieces.Add(Step(-6916.838f, 55.547f, -3654.990f, 90f, "blackmarble_stair"));
        six.Pieces.Add(Walked(StructurePieceKind.StairFiller, "blackmarble_2x1x1", -6918.338f, 56.047f, -3654.990f, 90f));
        six.Pieces.Add(Step(-6919.145f, 56.547f, -3654.531f, 112.5f, "blackmarble_stair"));
        six.Pieces.Add(Walked(StructurePieceKind.StairFloor, "blackmarble_floor", -6920.993f, 57.047f, -3653.766f, 292.5f));
        var five = new StairRun { From = new Vector2(-6920f, -3656f), To = new Vector2(-6928f, -3640f), Width = 2f, Kit = StairKit.BlackMarble };
        five.Pieces.Add(Step(-6922.840f, 56.547f, -3653.000f, 292.5f, "blackmarble_stair"));
        five.Pieces.Add(Step(-6924.688f, 55.547f, -3651.153f, 337.5f, "blackmarble_stair"));
        Vector2 centre = new(-6920.993f, -3653.766f), right = StairLayout.Right(292.5f);
        Func<int, int, float> ground = (x, z) => Vector2.Dot(new Vector2(x, z) - centre, right) > 0f ? 54.047f : 56.297f;
        List<StructurePiece> beside;
        StairStructures.Reset();
        try
        {
            StairStructures.Add(six);
            StairStructures.Add(five);
            beside = StairStructures.MetAtEnds(six);
        }
        finally { StairStructures.Reset(); }
        Assert.Contains(beside, p => Vector3.Distance(p.Position, new Vector3(-6922.840f, 56.547f, -3653.000f)) < 0.01f);
        List<StructurePiece> rails = Railing(six, StairKit.BlackMarble, ground, beside);
        // Both sides railed at both heights: the downhill side as in game, the uphill side too.
        foreach (float y in new[] { 57.547f, 58.547f })
        {
            AssertHas(rails, StairRailing.LevelRailPrefab, -6920.610f, y, -3652.842f, 202.5f, 180f);
            AssertHas(rails, StairRailing.LevelRailPrefab, -6921.376f, y, -3654.690f, 202.5f, 180f);
        }
        // Nothing across the walk: up the upper staircase's top stair onto the floor and on down the next one's.
        Vector2 up = new(-6919.145f, -3654.531f), down = new(-6922.840f, -3653.000f);
        Assert.Null(RailAcross(rails, up, centre));
        Assert.Null(RailAcross(rails, centre, down));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASquareTurnOntoTheFloorTheRoadComesOntoIsRailedRoundItsOuterCornerAndOpenAlongTheWalk(bool level)
    {
        // In game: a black marble staircase's foot (5784,5632 -> 5768,5656), its last stair (yaw 292.5) turning square onto
        // the floor the road comes onto (yaw 202.5) across a 2 x 2 filler; the road's last point at 5769.646,5655.557 off the
        // floor's far edge. The floor stood on ground at walking height, bare as the road's end leaves it; the filler stood
        // bare round its outer corner over a 6 m drop. Railed by hand: a post at the corner (5767.766,5650.859) and one at
        // the floor's corner (5768.532,5652.707), rails from the stair's corner post round to it. The pieces as laid; the
        // ground: the road's beside the floor, 6 m under the walk elsewhere; with level, the road's all round.
        var run = new StairRun { From = new Vector2(5784f, 5632f), To = new Vector2(5768f, 5656f), ToMeet = new Vector2(5769.646f, 5655.557f),
            Width = 2f, Kit = StairKit.BlackMarble };
        run.Pieces.Add(Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 5769.838f, 54.028f, 5653.248f, 202.5f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFiller, "blackmarble_floor", 5769.073f, 54.028f, 5651.400f, 202.5f));
        run.Pieces.Add(Step(5770.920f, 54.528f, 5650.634f, 292.5f, "blackmarble_stair"));
        run.Pieces.Add(Walked(StructurePieceKind.StairFiller, "blackmarble_2x1x1", 5772.306f, 55.028f, 5650.061f, 292.5f));
        run.Pieces.Add(Step(5772.875f, 55.528f, 5649.328f, 315f, "blackmarble_stair"));
        Vector2 filler = new(5769.073f, 5651.400f), floor = new(5769.838f, 5653.248f), stair = new(5770.920f, 5650.634f);
        Func<int, int, float> ground = (x, z) => level || Vector2.Dot(new Vector2(x, z) - filler, StairLayout.Forward(22.5f)) > 0.5f ? 54.4f : 48.4f;
        List<StructurePiece> rails = Railing(run, StairKit.BlackMarble, ground);
        // Round the outer corner at both heights, as the floor's edges are railed, from the stair's corner post to the
        // corner's and on to the floor's.
        foreach (float y in new[] { 54.528f, 55.528f })
        {
            AssertHas(rails, StairRailing.LevelRailPrefab, 5768.690f, y, 5650.476f, 22.5f, 180f);
            AssertHas(rails, StairRailing.LevelRailPrefab, 5768.149f, y, 5651.783f, 112.5f, 180f);
        }
        foreach (var (x, z) in new[] { (5769.614f, 5650.093f), (5767.766f, 5650.859f), (5768.532f, 5652.707f) })
            Assert.Single(rails, r => r.Prefab == BridgeLayout.RailPostPrefab && Vector3.Distance(r.Position, new Vector3(x, 55.028f, z)) < 0.05f);
        // Open along the walk: off the stair onto the filler, on to the floor and off it to the road; the floor's sides bare.
        Assert.Null(RailAcross(rails, stair, filler));
        Assert.Null(RailAcross(rails, filler, floor));
        Assert.Null(RailAcross(rails, floor, new Vector2(5769.646f, 5655.557f)));
        foreach (float y in new[] { 54.528f, 55.528f })
        {
            AssertHasNone(rails, 5768.914f, y, 5653.630f);
            AssertHasNone(rails, 5770.762f, y, 5652.864f);
        }
    }

    // In game: a stone staircase (-1869.46,6734.31 -> -1903,6718) down from its road onto a stone tower's landing
    // (StoneTowerRuins05_leet, -1916.81,276.85,6724.33). Its last stair (yaw 292.5) faces the tower, turns square on a 2 x 2
    // filler onto its foot floor (yaw 202.5), walked on north-north-east to the end recorded on its far edge (-1903,6718),
    // where a bank rises over it; the tower's level ground lies west-north-west, past a dip off the floor's side. Laid: that
    // side railed, the bank side open. Fixed by hand: the side rails taken away, a stone_floor_2x2 laid on that side level
    // with the floor, joining it to the tower's ground, a rail across the bank side. The pieces as laid, in the layout's
    // order from its foot; the ground the saved staircase holds, the tower's level past it.
    private static readonly Vector2 Tower = new(-1916.81f, 6724.33f);
    private static readonly Vector2 LandingEnd = new(-1903f, 6718f);

    private static (StairPieceKind kind, string prefab, Vector3 at, float yaw)[] LandingPieces() => new[]
    {
        (StairPieceKind.Landing, "stone_floor_2x2", new Vector3(-1903.383f, 276.457f, 6717.076f), 202.5f),
        (StairPieceKind.Filler, "stone_floor_2x2", new Vector3(-1904.148f, 276.457f, 6715.229f), 202.5f),
        (StairPieceKind.Stair, "stone_stair", new Vector3(-1902.300f, 276.957f, 6714.463f), 292.5f),
        (StairPieceKind.Filler, "stone_wall_2x1", new Vector3(-1900.915f, 277.457f, 6713.889f), 292.5f),
        (StairPieceKind.Filler, "stone_wall_1x1", new Vector3(-1900.882f, 277.457f, 6713.427f), 247.5f),
        (StairPieceKind.Stair, "stone_stair", new Vector3(-1899.687f, 277.957f, 6714.463f), 247.5f),
        (StairPieceKind.Filler, "stone_wall_2x1", new Vector3(-1898.301f, 278.457f, 6715.037f), 247.5f),
        (StairPieceKind.Stair, "stone_stair", new Vector3(-1897.381f, 278.957f, 6714.922f), 270f),
    };

    /// <summary>The ground the saved staircase holds round the landing; the tower's level ground (276.85) on west of it, a
    /// slope falling away east. <paramref name="sunk"/> lowers the tower's side of the landing (west of -1903.5 m).</summary>
    private static Func<int, int, float> LandingGround(float sunk = 0f)
    {
        var held = new Dictionary<(int, int), float>();
        foreach (var (x, z, h) in new[]
        {
            (-1907, 6713, 276.26f), (-1906, 6713, 275.70f), (-1905, 6713, 274.69f), (-1904, 6713, 273.54f),
            (-1903, 6713, 272.56f), (-1902, 6713, 271.84f), (-1900, 6713, 272.29f), (-1899, 6713, 272.80f),
            (-1907, 6714, 276.53f), (-1906, 6714, 276.10f), (-1905, 6714, 275.41f), (-1904, 6714, 274.36f),
            (-1903, 6714, 273.63f), (-1902, 6714, 273.19f), (-1901, 6714, 273.11f), (-1900, 6714, 273.50f),
            (-1899, 6714, 274.15f), (-1907, 6715, 276.65f), (-1906, 6715, 276.36f), (-1905, 6715, 275.74f),
            (-1904, 6715, 274.83f), (-1903, 6715, 274.34f), (-1902, 6715, 274.06f), (-1901, 6715, 274.01f),
            (-1900, 6715, 274.61f), (-1899, 6715, 275.45f), (-1907, 6716, 276.83f), (-1906, 6716, 276.50f),
            (-1905, 6716, 276.02f), (-1904, 6716, 275.24f), (-1903, 6716, 275.00f), (-1902, 6716, 274.89f),
            (-1901, 6716, 275.01f), (-1900, 6716, 275.72f), (-1899, 6716, 276.61f), (-1907, 6717, 276.77f),
            (-1906, 6717, 276.64f), (-1905, 6717, 276.36f), (-1904, 6717, 275.90f), (-1903, 6717, 275.84f),
            (-1902, 6717, 276.01f), (-1901, 6717, 276.39f), (-1900, 6717, 277.10f), (-1899, 6717, 277.96f),
            (-1906, 6718, 276.75f), (-1905, 6718, 276.69f), (-1904, 6718, 276.69f), (-1903, 6718, 276.96f),
            (-1902, 6718, 277.44f), (-1901, 6718, 278.04f), (-1900, 6718, 278.43f), (-1899, 6718, 279.16f),
            (-1906, 6719, 276.83f), (-1905, 6719, 276.91f), (-1904, 6719, 277.17f), (-1903, 6719, 277.69f),
            (-1902, 6719, 278.43f), (-1901, 6719, 279.22f), (-1905, 6720, 277.03f), (-1904, 6720, 277.40f),
            (-1903, 6720, 278.03f), (-1902, 6720, 278.97f)
        })
            held[(x, z)] = h;
        return (x, z) => (held.TryGetValue((x, z), out float h) ? h : x <= -1905 ? 276.85f : 270f) - (x <= -1904 ? sunk : 0f);
    }

    /// <summary>The landing's layout as its search laid it (its foot first), on <paramref name="ground"/>, then finished at the
    /// location (StairLayout.AddLocationFloor, as an approach's foot on a location's landing is): the floor laid out, if any.</summary>
    private static (StairPlan plan, Vector2? meet) LandingPlan(Func<int, int, float> ground)
    {
        var plan = new StairPlan { Kit = StairKit.Stone.Name, Reached = true, Reversed = true, StartsOnFloor = true };
        foreach (var (kind, prefab, at, yaw) in LandingPieces())
            plan.Pieces.Add(new StairPiece { Kind = kind, Prefab = prefab, Position = at, YawDegrees = yaw });
        Vector2? meet = StairLayout.AddLocationFloor(plan, (x, z) => BuriedPieces.MeshHeight(x, z, ground), LandingEnd, Tower, _ => true);
        plan.LocationMeet = meet;
        return (plan, meet);
    }

    /// <summary>The staircase run the layout's plan makes at a location (RoadNetworkGenerator.JoinNetwork): its end at the location
    /// met where the location's ground meets a floor laid out onto it.</summary>
    private static StairRun LandingRun(StairPlan plan)
    {
        StairRun run = StairRun.FromPlan(new Vector2(-1869.46f, 6734.31f), LandingEnd, 2f, plan, StairKit.Stone);
        run.KeepsLocationRocks = true;
        run.ToMeet = plan.LocationMeet;
        return run;
    }

    [Fact]
    public void ALocationsLandingIsOpenOnTheSideFacingTheLocationAndJoinedToItsGroundThere()
    {
        Func<int, int, float> ground = LandingGround();
        var (plan, meet) = LandingPlan(ground);
        StairRun run = LandingRun(plan);
        List<StructurePiece> laid = StairKit.Stone.Dress(run, (x, z) => ground(Mathf.FloorToInt(x), Mathf.FloorToInt(z)), ground, false);
        List<StructurePiece> rails = laid.Where(p => p.Kind is StructurePieceKind.Beam or StructurePieceKind.Post).ToList();
        bool At(StructurePiece p, float x, float y, float z) => Vector3.Distance(p.Position, new Vector3(x, y, z)) < 0.05f;
        // The floor laid by hand, by the layout: on the foot floor's side facing the tower, level with it (in game, laid
        // again: its corners -1903.9,6718.4 -1904.7,6716.5 -1905.8,6719.1 -1906.5,6717.3, its top 276.96).
        Assert.Single(run.Pieces, p => p.Kind == StructurePieceKind.StairFloor && p.Prefab == "stone_floor_2x2" && At(p, -1905.230f, 276.457f, 6717.842f));
        Assert.Single(laid, p => p.Kind == StructurePieceKind.StairFloor && At(p, -1905.230f, 276.457f, 6717.842f));
        Assert.NotNull(meet);
        // The rails across that side gone (in game at -1904.307,6717.459, 276.957 and 277.957). The bank side, railed by hand
        // in game (its handrail at -1903.000,277.957,6718.000), is open now: the landing's floor at a location's end is railed
        // as a road's end floor, only over a drop (StairRailing.LedgeDrop), and the bank stands level with it.
        AssertHasNone(rails, -1904.307f, 276.957f, 6717.459f);
        AssertHasNone(rails, -1904.307f, 277.957f, 6717.459f);
        AssertHasNone(rails, -1903.000f, 277.957f, 6718.000f);
        // As laid and kept by hand: the floor's other side and the filler's outer corner railed at both heights, the posts
        // at the corners the rails meet.
        foreach (var (x, z) in new[] { (-1902.461f, 6716.693f), (-1905.071f, 6715.612f), (-1904.531f, 6714.306f) })
            foreach (float y in new[] { 276.957f, 277.957f })
                Assert.Contains(rails, p => p.Prefab == StairRailing.LevelRailPrefab && At(p, x, y, z));
        foreach (var (x, z) in new[] { (-1904.689f, 6716.535f), (-1902.076f, 6717.617f) })
            Assert.Single(rails, p => p.Prefab == BridgeLayout.RailPostPrefab && At(p, x, 277.457f, z));
        // The laid floor open all round, the tower's ground meeting it: no rail or post on it.
        Vector2 added = new(-1905.230f, 6717.842f);
        Assert.DoesNotContain(rails, p => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), added) < 1.2f);
        // The walk open from the last stair round the turn, onto the floor, the laid floor and on to the tower.
        Vector2 stair = new(-1902.300f, 6714.463f), filler = new(-1904.148f, 6715.229f), floor = new(-1903.383f, 6717.076f);
        Assert.Null(RailAcross(rails, stair, filler));
        Assert.Null(RailAcross(rails, filler, floor));
        Assert.Null(RailAcross(rails, floor, added));
        Assert.Null(RailAcross(rails, added, added + (Tower - added).normalized * 2f));
    }

    [Fact]
    public void AFloorLaidOutOntoALocationsGroundIsTheLayoutsOwnEndFloor()
    {
        // The floor laid out onto the tower's ground is the layout's: in its pieces first in the walk (the top's last row
        // stays last), with a clearance over it as any floor has, a landing more, and its kit holds it up as any end floor.
        Func<int, int, float> ground = LandingGround();
        var (plan, _) = LandingPlan(ground);
        Assert.Equal(LandingPieces().Length + 1, plan.Pieces.Count);
        StairPiece first = plan.Pieces[0];
        Assert.Equal(StairPieceKind.Landing, first.Kind);
        Assert.True(Vector3.Distance(first.Position, new Vector3(-1905.230f, 276.457f, 6717.842f)) < 0.05f);
        Assert.Equal(1, plan.Landings);
        StairClearance box = Assert.Single(plan.Clearances);
        Assert.True(Vector2.Distance(new Vector2(box.Centre.x, box.Centre.z), new Vector2(first.Position.x, first.Position.z)) < 0.05f);
        Assert.Equal(276.957f, box.Surface, 2);
        StairRun run = LandingRun(plan);
        Assert.Single(run.Clearances);
        // Held up by its kit as the foot floor beside it is: the same choice its supports are laid by, from the ground.
        Func<float, float, float> natural = (x, z) => BuriedPieces.MeshHeight(x, z, ground);
        StairSupport.Walk floor = StairSupport.WalkOf(run.Pieces[0], StairKit.Stone)!.Value;
        Assert.False(StairSupport.Choose(StairKit.Stone, floor, natural).Refused);
        var shortfall = new StairSupport.Shortfall();
        List<StructurePiece> laid = StairKit.Stone.Dress(run, natural, ground, false, shortfall);
        // (The run here stops at its third stair, over the default ground: that stair alone is left unheld.)
        Assert.DoesNotContain(shortfall.Unheld, u => Vector2.Distance(new Vector2(u.x, u.z), new Vector2(first.Position.x, first.Position.z)) < 1.5f);
        Assert.Contains(laid, p => p.Kind == StructurePieceKind.StairFloor && Vector3.Distance(p.Position, first.Position) < 0.05f);
    }

    [Fact]
    public void ALocationsLandingOverADeeperDipGetsNoFloorAndIsRailedAsBefore()
    {
        // The same landing with the tower's side of it a metre and a half lower, the dip and the level ground past it both
        // more than a floor's band (StairLayout.TopFloorBand) under the floor: no floor lands level there, so none is laid,
        // and the side facing the tower is over the ledge: railed there, as a road's end floor over a drop, and the walk out
        // straight on across the end recorded (the bank side) open.
        Func<int, int, float> ground = LandingGround(sunk: 1.5f);
        var (plan, meet) = LandingPlan(ground);
        Assert.Null(meet);
        Assert.Equal(LandingPieces().Length, plan.Pieces.Count);
        Assert.Empty(plan.Clearances);
        StairRun run = LandingRun(plan);
        Func<float, float, float> natural = (x, z) => ground(Mathf.FloorToInt(x), Mathf.FloorToInt(z));
        List<StructurePiece> rails = StairKit.Stone.Dress(run, natural, ground, false).Where(p => p.Kind == StructurePieceKind.Beam).ToList();
        Assert.Contains(rails, p => p.Prefab == StairRailing.LevelRailPrefab && Vector3.Distance(p.Position, new Vector3(-1904.307f, 277.957f, 6717.459f)) < 0.05f);
        Assert.Null(RailAcross(rails, new Vector2(-1903.383f, 6717.076f), LandingEnd + (LandingEnd - new Vector2(-1903.383f, 6717.076f)).normalized));
    }

    /// <summary>Ground level with the landing's foot floor all round (276.85, a tenth under its walking surface), but rising by
    /// <paramref name="rise"/> from the floor's side facing the tower on (a mound or a cave's mouth there).</summary>
    private static Func<int, int, float> GroundRisingTowardTheTower(float rise)
    {
        Vector2 floor = new(-1903.383f, 6717.076f), toward = (new Vector2(-1904.307f, 6717.459f) - floor).normalized;
        return (x, z) => 276.85f + (Vector2.Dot(new Vector2(x, z) - floor, toward) >= 1f ? rise : 0f);
    }

    private static List<StructurePiece> LandingRails(Func<int, int, float> ground)
    {
        var (plan, _) = LandingPlan(ground);
        StairRun run = LandingRun(plan);
        Func<float, float, float> natural = (x, z) => ground(Mathf.FloorToInt(x), Mathf.FloorToInt(z));
        return StairKit.Stone.Dress(run, natural, ground, false)
            .Where(p => p.Kind is StructurePieceKind.Beam or StructurePieceKind.Post).ToList();
    }

    [Fact]
    public void ALocationsLandingWhoseSideFacingTheLocationAbutsARiseIsOpenThereAndStraightOn()
    {
        // The landing on level ground but for a rise 0.8 m over its floor on from its side facing the tower (a mound or a
        // cave's mouth). No floor is laid there (the step off it is no drop). Railed as a road's end floor, only over a drop
        // (StairRailing.LedgeDrop), the side abutting the rise is open (railed there before), as is the walk straight on
        // across the end recorded (the bank side).
        Func<int, int, float> ground = GroundRisingTowardTheTower(0.8f);
        var (plan, meet) = LandingPlan(ground);
        Assert.Null(meet);
        Assert.Equal(LandingPieces().Length, plan.Pieces.Count);
        List<StructurePiece> rails = LandingRails(ground);
        AssertHasNone(rails, -1904.307f, 276.957f, 6717.459f);
        AssertHasNone(rails, -1904.307f, 277.957f, 6717.459f);
        Vector2 floor = new(-1903.383f, 6717.076f);
        Assert.Null(RailAcross(rails.Where(p => p.Kind == StructurePieceKind.Beam).ToList(), floor, LandingEnd + (LandingEnd - floor).normalized));
    }

    [Fact]
    public void ALocationsLandingOnLevelGroundIsOpenIntoTheLocationAndStraightOn()
    {
        // The landing on level ground all round: open on its side facing the tower, straight on off the walk across the end
        // recorded (the bank side), and on its other side too (railed before): a location's end floor is railed as a road's,
        // only over a drop (StairRailing.LedgeDrop). A floor off its side facing the tower lands (the end rule's 2x2 at the
        // lower landing, StairEnds.Lands), flush with the tower's ground.
        Func<int, int, float> ground = GroundRisingTowardTheTower(0f);
        var (_, meet) = LandingPlan(ground);
        Assert.NotNull(meet);
        List<StructurePiece> rails = LandingRails(ground).Where(p => p.Kind == StructurePieceKind.Beam).ToList();
        Vector2 floor = new(-1903.383f, 6717.076f), facing = new(-1904.307f, 6717.459f);
        AssertHasNone(rails, facing.x, 276.957f, facing.y);
        AssertHasNone(rails, facing.x, 277.957f, facing.y);
        Assert.Null(RailAcross(rails, floor, LandingEnd + (LandingEnd - floor).normalized));
        AssertHasNone(rails, -1902.461f, 277.957f, 6716.693f);
    }

    [Fact]
    public void TwoStaircasesMeetingEndToEndInAStraightLineAreRailedOnBothSidesOfTheirJoint()
    {
        // In game: a black marble staircase (5808,5648 -> 5784,5632) climbs onto its top floor at 5784.552,5635.472
        // (yaw 292.5) and the next (5784,5632 -> 5768,5656) goes on down straight from its far edge, its top stair at
        // 5782.705,5636.237; the floor railed on both sides, over a drop, nothing across the walk. The pieces as laid.
        var up = new StairRun { From = new Vector2(5808f, 5648f), To = new Vector2(5784f, 5632f), Width = 2f, Kit = StairKit.BlackMarble };
        up.Pieces.Add(Step(5788.707f, 60.528f, 5634.248f, 90f, "blackmarble_stair"));
        up.Pieces.Add(Walked(StructurePieceKind.StairFiller, "blackmarble_2x1x1", 5787.207f, 61.028f, 5634.248f, 90f));
        up.Pieces.Add(Step(5786.400f, 61.528f, 5634.707f, 112.5f, "blackmarble_stair"));
        up.Pieces.Add(Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 5784.552f, 62.028f, 5635.472f, 292.5f));
        var down = new StairRun { From = new Vector2(5784f, 5632f), To = new Vector2(5768f, 5656f), Width = 2f, Kit = StairKit.BlackMarble };
        down.Pieces.Add(Step(5782.705f, 61.528f, 5636.237f, 292.5f, "blackmarble_stair"));
        down.Pieces.Add(Walked(StructurePieceKind.StairFiller, "blackmarble_1x1", 5781.127f, 61.028f, 5636.349f, 292.5f));
        down.Pieces.Add(Walked(StructurePieceKind.StairFiller, "blackmarble_2x1x1", 5781.431f, 61.028f, 5636.699f, 337.5f));
        down.Pieces.Add(Step(5780.857f, 60.528f, 5638.084f, 337.5f, "blackmarble_stair"));
        List<StructurePiece> beside;
        StairStructures.Reset();
        try
        {
            StairStructures.Add(up);
            StairStructures.Add(down);
            beside = StairStructures.MetAtEnds(up);
        }
        finally { StairStructures.Reset(); }
        Func<int, int, float> ground = (x, z) => 35f;
        List<StructurePiece> rails = Railing(up, StairKit.BlackMarble, ground, beside);
        foreach (float y in new[] { 62.528f, 63.528f })
        {
            AssertHas(rails, StairRailing.LevelRailPrefab, 5784.935f, y, 5636.396f, 202.5f, 180f);
            AssertHas(rails, StairRailing.LevelRailPrefab, 5784.169f, y, 5634.548f, 202.5f, 180f);
        }
        Vector2 centre = new(5784.552f, 5635.472f);
        Assert.Null(RailAcross(rails, new Vector2(5786.400f, 5634.707f), centre));
        Assert.Null(RailAcross(rails, centre, new Vector2(5782.705f, 5636.237f)));
    }

    [Fact]
    public void TwoStaircasesMeetingAtTheTopAreRailedRoundTheirLandingButNotAcrossTheWalk()
    {
        // In game: a black marble staircase (3944,-6144 -> 3912,-6144) climbs from the
        // south-east onto two floors at 3913.549,-6146.429 and 3913.549,-6144.429 (yaw 0); the next (3912,-6144 ->
        // 3880,-6160) leaves from the north floor's west edge, its top stair at 3911.549,-6144.429. The first one's railing
        // railed that edge, across the walk onto the next, and left the landing's north and east edges bare over ground a
        // step or so under them; by hand the one was removed and the others added. The pieces as laid; the
        // ground assumed: 1 m under the landing to its north and east, 8 m under it to its west.
        var top = new StairRun { From = new Vector2(3944f, -6144f), To = new Vector2(3912f, -6144f), Width = 2f, Kit = StairKit.BlackMarble };
        top.Pieces.Add(Step(3916.938f, 66.5f, -6150.583f, 157.5f, "blackmarble_stair"));
        top.Pieces.Add(Walked(StructurePieceKind.StairFiller, "blackmarble_2x1x1", 3915.552f, 67.0f, -6148.926f, 292.5f));
        top.Pieces.Add(Walked(StructurePieceKind.StairFiller, "blackmarble_1x1", 3916.667f, 67.0f, -6148.847f, 112.5f));
        top.Pieces.Add(Step(3915.090f, 67.5f, -6148.735f, 112.5f, "blackmarble_stair"));
        top.Pieces.Add(Walked(StructurePieceKind.StairFiller, "blackmarble_2x1x1", 3913.705f, 68.0f, -6148.161f, 112.5f));
        top.Pieces.Add(Walked(StructurePieceKind.StairFiller, "blackmarble_1x1", 3913.049f, 68.0f, -6147.929f, 180f));
        top.Pieces.Add(Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 3913.549f, 68.0f, -6146.429f, 0f));
        top.Pieces.Add(Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 3913.549f, 68.0f, -6144.429f, 0f));
        var down = new StairRun { From = new Vector2(3912f, -6144f), To = new Vector2(3880f, -6160f), Width = 2f, Kit = StairKit.BlackMarble };
        down.Pieces.Add(Step(3911.549f, 67.5f, -6144.429f, 270f, "blackmarble_stair"));
        down.Pieces.Add(Step(3909.135f, 66.5f, -6143.429f, 315f, "blackmarble_stair"));
        Func<int, int, float> ground = (x, z) => x <= 3912 ? 60.5f : 67.5f;
        List<StructurePiece> beside;
        StairStructures.Reset();
        try
        {
            StairStructures.Add(top);
            StairStructures.Add(down);
            beside = StairStructures.MetAtEnds(top);
        }
        finally { StairStructures.Reset(); }
        List<StructurePiece> rails = StairKit.BlackMarble.Dress(top, (x, z) => ground(Mathf.FloorToInt(x), Mathf.FloorToInt(z)), ground, false, beside: beside)
            .Where(p => p.Kind is StructurePieceKind.Beam or StructurePieceKind.Post).ToList();
        foreach (float y in new[] { 68.5f, 69.5f })
        {
            // What was removed by hand: across the walk off the north floor onto the next staircase's top stair.
            AssertHasNone(rails, 3912.549f, y, -6144.429f);
            // What was added by hand: the north floor's north and east edges, the south floor's east edge; its west edge as in game.
            AssertHas(rails, StairRailing.LevelRailPrefab, 3913.549f, y, -6143.429f, 0f, 180f);
            AssertHas(rails, StairRailing.LevelRailPrefab, 3914.549f, y, -6144.429f, 90f, 180f);
            AssertHas(rails, StairRailing.LevelRailPrefab, 3914.549f, y, -6146.429f, 90f, 180f);
            AssertHas(rails, StairRailing.LevelRailPrefab, 3912.549f, y, -6146.429f, 90f, 180f);
        }
        AssertHas(rails, BridgeLayout.RailPostPrefab, 3914.549f, 69.0f, -6145.429f, 0f, 90f);
        AssertHas(rails, BridgeLayout.RailPostPrefab, 3914.549f, 69.0f, -6143.429f, 0f, 90f);
        // Nothing across the walk: up the first staircase's top flight onto the landing, across it and down the next.
        Vector2 north = new(3913.549f, -6144.429f), south = new(3913.549f, -6146.429f);
        Assert.Null(RailAcross(rails, south, north));
        Assert.Null(RailAcross(rails, north, new Vector2(3911.549f, -6144.429f)));
    }

    [Fact]
    public void TwoStaircasesMeetingAtTheTopAreRailedRoundTheirLandingWhereABankStandsOverIt()
    {
        // In game: the same top as the test before, the staircases (3944,-6144 -> 3912,-6144) and
        // (3912,-6144 -> 3880,-6160) on a later build. The landing's north floor stood bare on its north and east edges, the south
        // floor's east edge railed; both edges' rails and the corner post were added by hand. The finished ground
        // there stands over the landing, not under it: 68.914 at 3913.5,-6142.4, 68.926 at 3915.6,-6144.4 and 69.538 at
        // 3915.6,-6142.4 (cli_ground_height), the landing's walking surface at 68.5. The pieces as laid; the ground: the plane
        // through those three north of the south floor, as the test before (1 m under the landing) south of it, 8 m under it
        // to its west.
        var top = new StairRun { From = new Vector2(3944f, -6144f), To = new Vector2(3912f, -6144f), Width = 2f, Kit = StairKit.BlackMarble };
        top.Pieces.Add(Step(3916.938f, 66.5f, -6150.583f, 157.5f, "blackmarble_stair"));
        top.Pieces.Add(Walked(StructurePieceKind.StairFiller, "blackmarble_2x1x1", 3915.552f, 67.0f, -6148.926f, 292.5f));
        top.Pieces.Add(Walked(StructurePieceKind.StairFiller, "blackmarble_1x1", 3916.667f, 67.0f, -6148.847f, 112.5f));
        top.Pieces.Add(Step(3915.090f, 67.5f, -6148.735f, 112.5f, "blackmarble_stair"));
        top.Pieces.Add(Walked(StructurePieceKind.StairFiller, "blackmarble_2x1x1", 3913.705f, 68.0f, -6148.161f, 112.5f));
        top.Pieces.Add(Walked(StructurePieceKind.StairFiller, "blackmarble_1x1", 3913.049f, 68.0f, -6147.929f, 180f));
        top.Pieces.Add(Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 3913.549f, 68.0f, -6146.429f, 0f));
        top.Pieces.Add(Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 3913.549f, 68.0f, -6144.429f, 0f));
        var down = new StairRun { From = new Vector2(3912f, -6144f), To = new Vector2(3880f, -6160f), Width = 2f, Kit = StairKit.BlackMarble };
        down.Pieces.Add(Step(3911.549f, 67.5f, -6144.429f, 270f, "blackmarble_stair"));
        down.Pieces.Add(Step(3909.135f, 66.5f, -6143.429f, 315f, "blackmarble_stair"));
        // The plane through the three heights: 0.297 m a metre east, 0.306 m a metre north.
        Func<int, int, float> ground = (x, z) => x <= 3912 ? 60.5f : z < -6145 ? 67.5f
            : 68.914f + 0.297f * (x - 3913.5f) + 0.306f * (z + 6142.4f);
        List<StructurePiece> beside;
        StairStructures.Reset();
        try
        {
            StairStructures.Add(top);
            StairStructures.Add(down);
            beside = StairStructures.MetAtEnds(top);
        }
        finally { StairStructures.Reset(); }
        List<StructurePiece> rails = StairKit.BlackMarble.Dress(top, (x, z) => ground(Mathf.FloorToInt(x), Mathf.FloorToInt(z)), ground, false, beside: beside)
            .Where(p => p.Kind is StructurePieceKind.Beam or StructurePieceKind.Post).ToList();
        foreach (float y in new[] { 68.5f, 69.5f })
        {
            // What was added by hand: the north floor's north and east edges.
            AssertHas(rails, StairRailing.LevelRailPrefab, 3913.549f, y, -6143.429f, 0f, 180f);
            AssertHas(rails, StairRailing.LevelRailPrefab, 3914.549f, y, -6144.429f, 90f, 180f);
            // As in game: the south floor's east and west edges.
            AssertHas(rails, StairRailing.LevelRailPrefab, 3914.549f, y, -6146.429f, 90f, 180f);
            AssertHas(rails, StairRailing.LevelRailPrefab, 3912.549f, y, -6146.429f, 90f, 180f);
            // Never across the walk off the north floor onto the lower staircase's top stair.
            AssertHasNone(rails, 3912.549f, y, -6144.429f);
        }
        AssertHas(rails, BridgeLayout.RailPostPrefab, 3914.549f, 69.0f, -6143.429f, 0f, 90f);
        AssertHas(rails, BridgeLayout.RailPostPrefab, 3914.549f, 69.0f, -6145.429f, 0f, 90f);
        // Nothing across the walk: up the upper staircase's top flight onto the landing, across it and down the lower.
        Vector2 north = new(3913.549f, -6144.429f), south = new(3913.549f, -6146.429f);
        Assert.Null(RailAcross(rails, south, north));
        Assert.Null(RailAcross(rails, north, new Vector2(3911.549f, -6144.429f)));
    }

    [Fact]
    public void TwoStaircasesMeetingOnARidgeAreRailedAlongItWhereTheGroundStandsLevelBesideIt()
    {
        // In game: black marble staircases (1576,-6160 -> 1600,-6184) and (1600,-6184 ->
        // 1624,-6200) meet on a ridge, three floors in a row turned 45 degrees at 59.361 (walking surface 59.861): the first's top
        // stair onto the first floor's one side, the second's off the third floor's other side. No floor edge railed; standing beside
        // the first floor at 1601.0,59.8,-6187.5 (0.1 m over the ground), the ridge
        // joining two staircases wants railings. The pieces as laid; the ground: 59.7 along the ridge, 0.16 m under the floors, falling 2 m
        // a metre past 1.6 m off its line to either side (a step out from the floors level with them).
        var up = new StairRun { From = new Vector2(1576f, -6160f), To = new Vector2(1600f, -6184f), Width = 2f, Kit = StairKit.BlackMarble };
        up.Pieces.Add(Step(1597.625f, 57.861f, -6183.500f, 315f, "blackmarble_stair"));
        up.Pieces.Add(Step(1599.039f, 58.861f, -6184.914f, 315f, "blackmarble_stair"));
        up.Pieces.Add(Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 1600.453f, 59.361f, -6186.328f, 135f));
        up.Pieces.Add(Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 1601.867f, 59.361f, -6184.914f, 225f));
        up.Pieces.Add(Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 1603.282f, 59.361f, -6183.500f, 315f));
        var down = new StairRun { From = new Vector2(1600f, -6184f), To = new Vector2(1624f, -6200f), Width = 2f, Kit = StairKit.BlackMarble };
        down.Pieces.Add(Step(1604.696f, 58.861f, -6184.914f, 135f, "blackmarble_stair"));
        down.Pieces.Add(Step(1605.696f, 57.861f, -6187.328f, 180f, "blackmarble_stair"));
        // The ridge's frame: u along the row of floors (north-east), v across it (south-east), from the middle floor.
        Vector2 centre = new(1601.867f, -6184.914f), u = new Vector2(1f, 1f).normalized, v = new Vector2(1f, -1f).normalized;
        Vector2 At(float a, float b) => centre + u * a + v * b;
        Func<int, int, float> ground = (x, z) =>
        {
            float off = Mathf.Abs(Vector2.Dot(new Vector2(x, z) - centre, v));
            return off <= 1.6f ? 59.7f : 59.7f - 2f * (off - 1.6f);
        };
        List<StructurePiece> beside;
        StairStructures.Reset();
        try
        {
            StairStructures.Add(up);
            StairStructures.Add(down);
            beside = StairStructures.MetAtEnds(up);
        }
        finally { StairStructures.Reset(); }
        List<StructurePiece> rails = StairKit.BlackMarble.Dress(up, (x, z) => ground(Mathf.FloorToInt(x), Mathf.FloorToInt(z)), ground, false, beside: beside)
            .Where(p => p.Kind is StructurePieceKind.Beam or StructurePieceKind.Post).ToList();
        foreach (float y in new[] { 59.861f, 60.861f })
        {
            // Every edge not walked across: both long sides of the middle floor, the first floor's other side and far end, the
            // third floor's other side and far end. Along the row a rail turns 135 (or 315), across it 45 (or 225).
            foreach (Vector2 p in new[] { At(-2f, 1f), At(0f, 1f), At(0f, -1f), At(2f, -1f) })
                AssertHas(rails, StairRailing.LevelRailPrefab, p.x, y, p.y, 135f, 180f);
            foreach (Vector2 p in new[] { At(-3f, 0f), At(3f, 0f) })
                AssertHas(rails, StairRailing.LevelRailPrefab, p.x, y, p.y, 45f, 180f);
            // Never where the walk comes on from the first staircase or goes off onto the second.
            foreach (Vector2 p in new[] { At(-2f, -1f), At(2f, 1f) })
                AssertHasNone(rails, p.x, y, p.y);
        }
        foreach (Vector2 p in new[] { At(-3f, 1f), At(3f, -1f) })
            AssertHas(rails, BridgeLayout.RailPostPrefab, p.x, 60.361f, p.y, 45f, 90f);
        // Nothing across the walk: up 34's top stair, along the three floors and down 35's.
        Assert.Null(RailAcross(rails, At(-2f, -2f), At(-2f, 0f)));
        Assert.Null(RailAcross(rails, At(-2f, 0f), At(2f, 0f)));
        Assert.Null(RailAcross(rails, At(2f, 0f), At(2f, 2f)));
    }

    [Theory]
    [InlineData(0.8f)]
    [InlineData(0.25f)]
    public void TheFlatFloorsFromARoadsEndToTheFirstStairAreRailedOnBothSides(float eastUnder)
    {
        // In game: a black marble staircase's foot, the road's end at 1808,-6616 (92.6), six flat floors laid
        // on north from it to the first stair, at 92.187 (walking surface 92.687). Railed on their west side only, over a drop
        // of about 3 m (the ground at 89.8 to 89.9 a few metres west); the east side, the hillside, bare. The road's end meets
        // only the first floor (within its ring's reach); the five on past it were taken as the road's too, railed only over
        // a 1.5 m ledge. The pieces as laid; the ground: the road's, 92.6, round its end; 89.8 west of the floors' line; east of
        // it eastUnder under their walking surface (its height there not read in game; railed either way).
        var run = new StairRun { From = new Vector2(1808f, -6616f), To = new Vector2(1776f, -6552f), Width = 2f, Kit = StairKit.BlackMarble };
        run.Pieces.Add(Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 1807.894f, 92.187f, -6613.504f, 0f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 1808.200f, 92.187f, -6611.963f, 22.5f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 1808.966f, 92.187f, -6610.115f, 22.5f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 1809.272f, 92.187f, -6608.574f, 0f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 1809.272f, 92.187f, -6606.574f, 0f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 1809.272f, 92.187f, -6604.574f, 0f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFiller, "blackmarble_2x1x1", 1809.272f, 92.187f, -6603.074f, 180f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFiller, "blackmarble_2x1x1", 1809.005f, 92.187f, -6602.729f, 337.5f));
        run.Pieces.Add(Step(1808.813f, 92.687f, -6602.268f, 157.5f, "blackmarble_stair"));
        run.Pieces.Add(Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 1808.048f, 93.187f, -6600.420f, 337.5f));
        float east = 92.687f - eastUnder;
        List<StructurePiece> rails = Railing(run, StairKit.BlackMarble, (x, z) => z <= -6613 ? 92.6f : x <= 1809 ? 89.8f : east);
        foreach (float y in new[] { 92.687f, 93.687f })
        {
            // West, as in game: the second to sixth floors' edges; the third's and fourth's, on the inside of the jog back
            // between them, each railed to where it meets the other's edge.
            AssertHas(rails, StairRailing.LevelRailPrefab, 1807.276f, y, -6611.580f, 112.5f, 180f);
            AssertHas(rails, StairRailing.LevelRailPrefab, 1807.966f, y, -6609.917f, 112.5f, 180f);
            foreach (float z in new[] { -6608.376f, -6606.574f, -6604.574f })
                AssertHas(rails, StairRailing.LevelRailPrefab, 1808.272f, y, z, 90f, 180f);
            // East, the side that stood bare: the same five, however level the ground beside them; the second's, on the
            // inside of its jog off the first, from where it meets the first's edge.
            AssertHas(rails, StairRailing.LevelRailPrefab, 1809.200f, y, -6612.161f, 112.5f, 180f);
            AssertHas(rails, StairRailing.LevelRailPrefab, 1809.890f, y, -6610.498f, 112.5f, 180f);
            foreach (float z in new[] { -6608.574f, -6606.574f, -6604.574f })
                AssertHas(rails, StairRailing.LevelRailPrefab, 1810.272f, y, z, 90f, 180f);
            // The floor the road's end meets stays open to it: across its south edge and on its east.
            AssertHasNone(rails, 1807.894f, y, -6614.504f);
            AssertHasNone(rails, 1808.894f, y, -6613.504f);
        }
        // Nothing across the walk up the floors to the first stair.
        Assert.Null(RailAcross(rails, new Vector2(1807.894f, -6615f), new Vector2(1809.272f, -6603.574f)));
    }

    [Fact]
    public void TheFlatFloorsFromTheFirstStairToARoadsEndAtTheTopAreRailedOnBothSides()
    {
        // In game: the same staircase's top, the road's end at 1776,-6552 (107.7), four flat floors at 107.187
        // (walking surface 107.687) from the last stair to it, three turned 315, the fourth 292.5. No floor edge railed; the
        // ground stood a little under the floors to their south-west, under their edge to the north-east. The road's end meets
        // only the top floor. The pieces as laid; the ground: the road's round its end, 107.5 south-west of the floors' line,
        // 106.687 north-east of it.
        var run = new StairRun { From = new Vector2(1808f, -6616f), To = new Vector2(1776f, -6552f), Width = 2f, Kit = StairKit.BlackMarble };
        run.Pieces.Add(Step(1782.417f, 106.687f, -6558.874f, 112.5f, "blackmarble_stair"));
        run.Pieces.Add(Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 1780.569f, 107.187f, -6558.109f, 292.5f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 1779.262f, 107.187f, -6557.236f, 315f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 1777.848f, 107.187f, -6555.822f, 315f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFloor, "blackmarble_floor", 1776.434f, 107.187f, -6554.408f, 315f));
        Vector2 top = new(1776.434f, -6554.408f), northEast = new Vector2(1f, 1f).normalized;
        List<StructurePiece> rails = Railing(run, StairKit.BlackMarble, (x, z) =>
        {
            var p = new Vector2(x, z);
            if (Vector2.Distance(p, new Vector2(1775f, -6552.6f)) <= 2f) return 107.7f;
            return Vector2.Dot(p - top, northEast) > 0.5f ? 106.687f : 107.5f;
        });
        foreach (float y in new[] { 107.687f, 108.687f })
        {
            // The three floors on past the one the road meets: railed on both sides, over the drop and beside the level ground;
            // on the inside of the jog between the last stair's floor and the next, each to where it meets the other's edge.
            AssertHas(rails, StairRailing.LevelRailPrefab, 1778.555f, y, -6555.115f, 45f, 180f);
            AssertHas(rails, StairRailing.LevelRailPrefab, 1779.829f, y, -6556.389f, 45f, 180f);
            AssertHas(rails, StairRailing.LevelRailPrefab, 1781.135f, y, -6557.261f, 22.5f, 180f);
            AssertHas(rails, StairRailing.LevelRailPrefab, 1777.141f, y, -6556.529f, 45f, 180f);
            AssertHas(rails, StairRailing.LevelRailPrefab, 1778.555f, y, -6557.943f, 45f, 180f);
            AssertHas(rails, StairRailing.LevelRailPrefab, 1780.186f, y, -6559.033f, 22.5f, 180f);
            // The top floor, the road's, open to it: on its north-east (under 1.5 m to the ground) and across its exit.
            AssertHasNone(rails, 1777.141f, y, -6553.701f);
            AssertHasNone(rails, 1775.727f, y, -6553.701f);
        }
        Assert.Null(RailAcross(rails, new Vector2(1781.5f, -6558.5f), new Vector2(1775.0f, -6552.6f)));
    }

    /// <summary>
    /// The one rule at a staircase's two ends: no rail ever crosses the walk where it leaves the staircase, at its foot onto
    /// the road and at its top onto a location's landing, whichever way its top is turned and wherever its end was recorded
    /// (the top floor's near edge, where a finished top leaves it, or its far edge); the top floor railed on its two sides.
    /// </summary>
    [Theory]
    [InlineData(0f, false, false)]
    [InlineData(22.5f, false, false)]
    [InlineData(45f, false, false)]
    [InlineData(67.5f, false, false)]
    [InlineData(-45f, false, false)]
    [InlineData(22.5f, true, false)]
    [InlineData(45f, true, false)]
    [InlineData(67.5f, true, false)]
    [InlineData(-67.5f, true, false)]
    [InlineData(0f, false, true)]
    [InlineData(45f, false, true)]
    [InlineData(22.5f, true, true)]
    [InlineData(67.5f, true, true)]
    public void NoRailCrossesTheWalkAtEitherEndOfAStaircaseWhateverWayItsTopIsTurned(float turn, bool floorTurned, bool recordedAtFarEdge)
    {
        // Four stairs north from (0, 0) at 10 m, then (the last flight turned, or the top floor alone) two more and the top floor.
        var s = StairPieceKind.Stair; var f = StairPieceKind.Landing;
        float t = StairLayout.Wrap(turn);
        float[] headings = floorTurned ? new[] { 0f, 0f, 0f, 0f, 0f, 0f, t } : new[] { 0f, 0f, 0f, 0f, t, t, t };
        StairPlan plan = StairLayout.LayFlight(new Vector2(0f, 0f), 10f, headings, StairKit.Stone, (x, z) => -50f, new[] { s, s, s, s, s, s, f });
        var run = StairRun.FromPlan(new Vector2(0f, 0f), new Vector2(0f, 0f), 2f, plan, StairKit.Stone);
        StructurePiece top = run.Pieces.Last(p => p.Kind == StructurePieceKind.StairFloor);
        StairSupport.Walk floor = StairSupport.WalkOf(top, StairKit.Stone)!.Value;
        run.To = floor.At(recordedAtFarEdge ? floor.Depth : 0f, 0f);
        run.KeepsLocationRocks = true;
        // The location's landing level with the top floor past it, as an approach's top meets it.
        List<StructurePiece> rails = Railing(run, ground: LandingAhead(top));
        StairSupport.Walk first = StairSupport.WalkOf(run.Pieces.First(p => p.Kind == StructurePieceKind.StairStep), StairKit.Stone)!.Value;
        // At the foot: from the road onto the first stair.
        Vector2 middle = first.At(first.Depth / 2f, 0f);
        Assert.Null(RailAcross(rails, middle - first.Dir * 2f, middle));
        // At the top: from the last walked piece under it onto the floor, and across it on onto the landing.
        Vector2 centre = floor.At(floor.Depth / 2f, 0f);
        StructurePiece? across = RailAcross(rails, centre, centre + floor.Dir * 2.5f);
        Assert.True(across == null, FormattableString.Invariant($"rail at {across?.Position} yaw {across?.YawDegrees} across the way off the top floor"));
        StairSupport.Walk last = StairSupport.WalkOf(run.Pieces.Last(p => p.Kind == StructurePieceKind.StairStep), StairKit.Stone)!.Value;
        Assert.Null(RailAcross(rails, last.At(last.Depth / 2f, 0f), centre));
        // The top floor's two sides railed, at both heights.
        float surface = floor.Surface(0f, StairKit.Stone);
        foreach (int side in new[] { -1, 1 })
        {
            Vector2 edge = floor.At(floor.Depth / 2f, side * floor.Across / 2f);
            foreach (float y in new[] { surface, surface + BridgeLayout.RailHeight })
                Assert.True(Has(rails, StairRailing.LevelRailPrefab, edge.x, y, edge.y, floor.Heading + 90f, 180f),
                    FormattableString.Invariant($"no rail on the top floor's side at ({edge.x:F2}, {y:F2}, {edge.y:F2})"));
        }
    }

    [Fact]
    public void ARoadsStaircaseLeavesItsTopLandingBareWhereTheRoadComesOntoItPastItsRecordedEnd()
    {
        // Staircase -1880,6632 -> -1944,6608, its top as laid: stairs climbing north-west to a floor laid on past the recorded
        // end (the path point, on the top stair); the road comes onto the floor's far edge, its last point -1882.819,6633.985.
        var s = StairPieceKind.Stair; var f = StairPieceKind.Landing;
        StairPlan plan = StairLayout.LayFlight(new Vector2(-1877.162f, 6628.328f), 220.897f, new[] { 315f, 315f, 315f, 315f }, StairKit.Stone,
            (x, z) => -50f, new[] { s, s, s, f });
        var run = StairRun.FromPlan(new Vector2(-1880f, 6632f), new Vector2(-1877.162f, 6628.328f), 2f, plan, StairKit.Stone);
        run.FromMeet = new Vector2(plan.End.x, plan.End.z);
        Assert.True(Vector2.Distance(run.FromMeet.Value, new Vector2(-1882.819f, 6633.985f)) < 0.01f);
        // The road comes on at the floor's level: the ground past the top stair stands there.
        Vector2 top = new(-1881.405f, 6632.571f), up = StairLayout.Forward(315f);
        List<StructurePiece> rails = Railing(run, ground: (x, z) => Vector2.Dot(new Vector2(x, z) - top, up) >= 0f ? 223.897f : -50f);
        // None of the floor's rails, across the road and either side of it (in game at 223.897 and 224.897).
        foreach (var (x, z) in new[] { (-1882.819f, 6633.985f), (-1881.405f, 6633.985f), (-1882.819f, 6632.571f) })
            Assert.DoesNotContain(rails, p => Vector2.Distance(new Vector2(p.Position.x, p.Position.z), new Vector2(x, z)) < 0.05f);
        Assert.DoesNotContain(rails, p => p.Prefab == StairRailing.LevelRailPrefab);
        // The top stair is railed both sides up to the floor.
        AssertHas(rails, BridgeLayout.StairRailPrefab, -1881.405f, 223.897f, 6631.156f, 225f);
        AssertHas(rails, BridgeLayout.StairRailPrefab, -1879.990f, 223.897f, 6632.570f, 225f);
    }

    [Fact]
    public void ASwitchbacksLegsAreRailedAlongTheLineTheyShareTheirCrossedRailsOneRoofTop()
    {
        // A switchback at -1904,6608: the lower leg climbing east-south-east onto two floors, the upper leg back
        // west-north-west beside it.
        var run = new StairRun { From = new Vector2(-1912f, 6611f), To = new Vector2(-1912f, 6613f), Width = 2f };
        run.Pieces.AddRange(Flight(-1907.776f, 184.897f, 6608.940f, 292.5f, 2));
        run.Pieces.Add(Walked(StructurePieceKind.StairFloor, "stone_floor_2x2", -1903.315f, 186.397f, 6609.258f, 112.5f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFloor, "stone_floor_2x2", -1904.080f, 186.397f, 6607.410f, 112.5f));
        run.Pieces.AddRange(Flight(-1905.162f, 186.897f, 6610.023f, 112.5f, 2));
        List<StructurePiece> rails = Railing(run);
        // What was added by hand (ids 1724xx-1725xx): the lower leg's rails on the shared line and its lower rails ...
        AssertHas(rails, BridgeLayout.StairRailPrefab, -1905.545f, 185.897f, 6609.099f, 22.5f);
        AssertHas(rails, BridgeLayout.StairRailPrefab, -1906.311f, 185.897f, 6607.252f, 22.5f);
        AssertHas(rails, BridgeLayout.StairRailPrefab, -1907.393f, 184.897f, 6609.864f, 22.5f);
        AssertHas(rails, BridgeLayout.StairRailPrefab, -1908.158f, 184.897f, 6608.018f, 22.5f);
        // ... the upper leg's ...
        AssertHas(rails, BridgeLayout.StairRailPrefab, -1905.545f, 187.897f, 6609.099f, 202.5f);
        AssertHas(rails, BridgeLayout.StairRailPrefab, -1907.393f, 187.897f, 6609.864f, 202.5f);
        AssertHas(rails, BridgeLayout.StairRailPrefab, -1906.628f, 187.897f, 6611.712f, 202.5f);
        AssertHas(rails, BridgeLayout.StairRailPrefab, -1904.780f, 186.897f, 6610.947f, 202.5f);
        // ... a post where the shared line meets the landing, and one roof top where the lower leg's handrail and the upper
        // leg's lower rail cross, in place of the two.
        AssertHas(rails, BridgeLayout.RailPostPrefab, -1904.621f, 187.397f, 6608.717f, 22.5f, 90f);
        AssertHas(rails, StairRailing.CrossedRailPrefab, -1905.545f, 186.897f, 6609.099f, 202.5f);
        Assert.Single(rails, p => Vector3.Distance(p.Position, new Vector3(-1905.545f, 186.897f, 6609.099f)) < 0.05f);
        // The landing's two floors are walked across: no rail between them, nor across either leg's way onto them.
        Assert.DoesNotContain(rails, p => p.Prefab == StairRailing.LevelRailPrefab
            && Vector2.Distance(new Vector2(p.Position.x, p.Position.z), new Vector2(-1903.698f, 6608.334f)) < 0.05f);
        Assert.DoesNotContain(rails, p => p.Prefab == StairRailing.LevelRailPrefab
            && (Vector2.Distance(new Vector2(p.Position.x, p.Position.z), new Vector2(-1905.004f, 6607.792f)) < 0.05f
                || Vector2.Distance(new Vector2(p.Position.x, p.Position.z), new Vector2(-1904.239f, 6609.641f)) < 0.05f));
    }

    [Fact]
    public void ASwitchbacksLowerLegIsRailedAllAlongTheUpperLegBesideIt()
    {
        // The MountainCave02 approach's switchback: the lower leg turns west along the line z = -1770.816 it shares with the
        // upper leg coming back east, where neither had a rail beside -3012.752.
        var run = new StairRun { From = new Vector2(-3006f, -1767f), To = new Vector2(-3004f, -1772f), Width = 2f };
        run.Pieces.Add(Step(-3010.338f, 119.468f, -1768.816f, 45f));
        run.Pieces.Add(Step(-3012.752f, 120.468f, -1769.816f, 90f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFloor, "stone_floor_2x2", -3014.752f, 120.968f, -1769.816f, 270f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFloor, "stone_floor_2x2", -3014.752f, 120.968f, -1771.816f, 270f));
        run.Pieces.AddRange(Flight(-3012.752f, 121.468f, -1771.816f, 270f, 3));
        List<StructurePiece> rails = Railing(run);
        // Beside -3012.752 on the shared line: the lower leg's lower rail, the crossed pair as one roof top, the upper leg's
        // handrail; a post where the line meets the landing.
        AssertHas(rails, BridgeLayout.StairRailPrefab, -3012.752f, 120.468f, -1770.816f, 180f);
        AssertHas(rails, StairRailing.CrossedRailPrefab, -3012.752f, 121.468f, -1770.816f, 0f);
        AssertHas(rails, BridgeLayout.StairRailPrefab, -3012.752f, 122.468f, -1770.816f, 0f);
        AssertHas(rails, BridgeLayout.RailPostPrefab, -3013.752f, 121.968f, -1770.816f, 0f, 90f);
        // And on up the upper leg, as before.
        AssertHas(rails, BridgeLayout.StairRailPrefab, -3010.752f, 123.468f, -1770.816f, 0f);
    }

    [Fact]
    public void ASquareTurnsCornerFloorIsRailedOnItsOutsideToo()
    {
        // A black marble staircase at -9253,-804: a stair north-west onto a floor, the 90 degree turn's wedge a 2 x 2 floor
        // filled on past it, and the flight off to the south-west from the floor's left edge.
        var run = new StairRun { From = new Vector2(-9240f, -815f), To = new Vector2(-9265f, -800f), Width = 2f };
        run.Pieces.Add(Step(-9250.240f, 77.275f, -806.567f, 135f, "blackmarble_stair"));
        run.Pieces.Add(Walked(StructurePieceKind.StairFloor, "blackmarble_floor", -9251.654f, 77.775f, -805.152f, 315f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFiller, "blackmarble_floor", -9253.068f, 77.775f, -803.738f, 315f));
        run.Pieces.AddRange(Flight(-9254.482f, 78.275f, -805.152f, 45f, 2, "blackmarble_stair"));
        List<StructurePiece> rails = Railing(run, StairKit.BlackMarble);
        // The kit's floor rails on the floor's two sides, as in game ...
        foreach (float y in new[] { 78.275f, 79.275f })
        {
            AssertHas(rails, StairRailing.LevelRailPrefab, -9250.947f, y, -804.445f, 45f, 180f);
            AssertHas(rails, StairRailing.LevelRailPrefab, -9252.361f, y, -805.859f, 45f, 180f);
            // ... and those laid by hand (ids 1950xx-1951xx) round the corner floor's outside, with its corner post.
            AssertHas(rails, StairRailing.LevelRailPrefab, -9252.361f, y, -803.031f, 225f, 180f);
            AssertHas(rails, StairRailing.LevelRailPrefab, -9253.775f, y, -803.031f, 135f, 180f);
        }
        AssertHas(rails, BridgeLayout.RailPostPrefab, -9253.068f, 78.775f, -802.324f, 315f, 90f);
        // The next stair's lower rails.
        AssertHas(rails, BridgeLayout.StairRailPrefab, -9253.775f, 78.275f, -805.859f, 135f);
        AssertHas(rails, BridgeLayout.StairRailPrefab, -9255.189f, 78.275f, -804.445f, 135f);
    }

    private static StructurePiece Support(string prefab, float x, float y, float z, float yaw) =>
        new() { Kind = StructurePieceKind.StairSupport, Prefab = prefab, Position = new Vector3(x, y, z), YawDegrees = yaw };

    [Fact]
    public void AFortyFiveDegreeTurnUnderASwitchbacksUpperLegIsRailedRoundItsOuterCorner()
    {
        // A staircase at -1858,6890: two stairs climbing south-west, a 45 degree turn west (its wedge a 2x1
        // and a 1x1), a stair west onto the switchback's landing, and the upper leg back east over the turn's outer corner,
        // walled under its outer edge as in game.
        var walking = new List<StructurePiece>();
        walking.AddRange(Flight(-1856.775f, 259.664f, 6893.065f, 45f, 2));
        walking.Add(Walked(StructurePieceKind.StairFiller, "stone_wall_2x1", -1859.250f, 261.164f, 6890.590f, 45f));
        walking.Add(Walked(StructurePieceKind.StairFiller, "stone_wall_1x1", -1859.104f, 261.164f, 6890.151f, 90f));
        walking.Add(Step(-1860.604f, 261.664f, 6890.651f, 90f));
        walking.Add(Walked(StructurePieceKind.StairFloor, "stone_floor_2x2", -1862.604f, 262.164f, 6888.651f, 270f));
        walking.Add(Walked(StructurePieceKind.StairFloor, "stone_floor_2x2", -1862.604f, 262.164f, 6890.651f, 270f));
        walking.AddRange(Flight(-1860.604f, 262.664f, 6888.651f, 270f, 2));
        var walls = new List<StructurePiece>
        {
            Support("stone_wall_2x1", -1858.604f, 261.164f, 6889.151f, 0f), Support("stone_wall_2x1", -1860.604f, 261.164f, 6889.151f, 0f),
            Support("stone_wall_2x1", -1857.604f, 262.164f, 6889.151f, 0f), Support("stone_wall_2x1", -1859.604f, 262.164f, 6889.151f, 0f),
            Support("stone_wall_2x1", -1858.604f, 263.164f, 6889.151f, 0f),
        };
        var ends = new List<StairEnd> { new(new Vector2(-1848f, 6898f), new Vector2(-1848f, 6898f), false), new(new Vector2(-1850f, 6886f), new Vector2(-1850f, 6886f), false) };
        List<StructurePiece> rails = StairRailing.Lay(StairKit.Stone, walking, ends, (x, z) => -50f, walls);
        // As in game: the lower flight's outer top corner post, the upper flight's outer rail run back a metre from its
        // corner to a post, and that run's two rails.
        AssertHas(rails, BridgeLayout.RailPostPrefab, -1858.189f, 262.164f, 6890.237f, 225f, 90f);
        AssertHas(rails, BridgeLayout.RailPostPrefab, -1858.604f, 262.164f, 6889.651f, 270f, 90f);
        AssertHas(rails, StairRailing.ShortRailPrefab, -1859.104f, 261.664f, 6889.651f, 0f, 180f);
        AssertHas(rails, StairRailing.ShortRailPrefab, -1859.104f, 262.664f, 6889.651f, 0f, 180f);
        // What was added by hand: the lower flight's outer rail run on a metre under the upper leg, a lower rail and a
        // handrail (ids 103200, 103205) ...
        AssertHas(rails, StairRailing.ShortRailPrefab, -1858.543f, 261.664f, 6889.883f, 315f, 180f);
        AssertHas(rails, StairRailing.ShortRailPrefab, -1858.543f, 262.664f, 6889.883f, 315f, 180f);
        // ... ending in the upper leg's wall. The post at its end stands mostly in the wall, but its measured box (0.4 m
        // square, turned with the rail, BuriedPieces.DataOf) reaches 0.16 m out of the wall's face, so it is not wholly in a
        // support and stands (laid by hand without it; on the parent a copied 0.2 m square post, unturned, was wholly in it).
        Assert.Contains(rails, r => r.Prefab == BridgeLayout.RailPostPrefab
            && Vector2.Distance(new Vector2(r.Position.x, r.Position.z), new Vector2(-1858.897f, 6889.530f)) < 0.1f);
        // The inner corner keeps its one post, shared by the two flights.
        AssertHas(rails, BridgeLayout.RailPostPrefab, -1859.604f, 262.164f, 6891.651f, 270f, 90f);
    }

    [Fact]
    public void ASwitchbacksJoinFillerLeavesNoStairSideBareAndIsClosedByAHandrail()
    {
        // A staircase at -1868.3,6899.0: a flight climbing west-south-west onto a turned landing, the upper
        // leg back east from it beside a 2x1 join filler, which stands beside the flight's top stair's left side.
        var run = new StairRun { From = new Vector2(-1860f, 6906f), To = new Vector2(-1860f, 6894f), Width = 2f };
        run.Pieces.AddRange(Flight(-1866.835f, 246.664f, 6900.693f, 67.5f, 2));
        run.Pieces.Add(Walked(StructurePieceKind.StairFloor, "stone_floor_2x2", -1870.530f, 248.164f, 6899.163f, 247.5f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFloor, "stone_floor_2x2", -1869.765f, 248.164f, 6897.315f, 247.5f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFiller, "stone_wall_2x1", -1868.379f, 248.164f, 6897.890f, 247.5f));
        run.Pieces.Add(Step(-1867.458f, 248.664f, 6897.774f, 270f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFiller, "stone_wall_2x1", -1865.958f, 249.164f, 6897.774f, 270f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFiller, "stone_wall_2x1", -1864.960f, 249.164f, 6897.777f, 22.5f));
        run.Pieces.Add(Step(-1865.152f, 249.664f, 6897.315f, 292.5f));
        List<StructurePiece> rails = Railing(run);
        // What was added by hand (ids 111516..111859): the top stair's left side railed, a lower rail and a handrail, its corner post
        // clear of the wall ...
        AssertHas(rails, BridgeLayout.StairRailPrefab, -1868.300f, 247.664f, 6899.004f, 157.5f);
        AssertHas(rails, BridgeLayout.StairRailPrefab, -1868.300f, 248.664f, 6899.004f, 157.5f);
        AssertHas(rails, BridgeLayout.RailPostPrefab, -1869.224f, 249.164f, 6898.622f, 337.5f, 90f);
        // ... and a level handrail on to the upper leg's railing, with no lower rail, which would lie on the filler: here
        // from the corner post to the upper leg's post (the hand-laid one ran along the stair's edge, 0.15 m by that post).
        Vector2 corner = new(-1869.224f, 6898.622f), leg = new(-1868.458f, 6898.774f);
        Assert.Single(rails, r => Mathf.Abs(r.Position.y - 249.664f) < 0.01f && Spans(r, corner) && Spans(r, leg));
        Assert.DoesNotContain(rails, r => Mathf.Abs(r.Position.y - 248.664f) < 0.01f && Spans(r, corner) && Spans(r, leg));
    }

    [Fact]
    public void ASwitchbacksLegsAreRailedRoundTheirTurnsBesideAndOverEachOther()
    {
        // A staircase at -1858,6911: a stair east, a 22.5 degree turn, two stairs east-north-east, the
        // switchback's join (a 1x1 and a 2x1) and the upper leg back west-south-west beside them, then a 45 degree turn
        // south-south-west over the lower leg.
        var run = new StairRun { From = new Vector2(-1866f, 6912f), To = new Vector2(-1860f, 6904f), Width = 2f };
        run.Pieces.Add(Step(-1860.770f, 236.664f, 6912.085f, 270f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFiller, "stone_wall_2x1", -1859.270f, 237.164f, 6912.085f, 270f));
        run.Pieces.AddRange(Flight(-1858.463f, 237.664f, 6912.544f, 247.5f, 2));
        run.Pieces.Add(Walked(StructurePieceKind.StairFiller, "stone_wall_1x1", -1857.427f, 239.164f, 6911.350f, 247.5f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFiller, "stone_wall_2x1", -1856.041f, 239.164f, 6911.923f, 157.5f));
        run.Pieces.Add(Step(-1855.850f, 239.664f, 6911.461f, 67.5f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFiller, "stone_wall_2x1", -1857.236f, 240.164f, 6910.888f, 67.5f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFiller, "stone_wall_1x1", -1857.585f, 240.164f, 6911.191f, 22.5f));
        run.Pieces.AddRange(Flight(-1857.698f, 240.664f, 6909.614f, 22.5f, 2));
        List<StructurePiece> rails = Railing(run);
        // The lower leg's first stair past the 22.5 degree turn railed on its right, beside the join (hand-laid 111977, 111980),
        // and its turn closed round the outside: from the stair east's corner on to that stair's corner.
        AssertHas(rails, BridgeLayout.StairRailPrefab, -1858.080f, 237.664f, 6911.621f, 337.5f);
        AssertHas(rails, BridgeLayout.StairRailPrefab, -1858.080f, 238.664f, 6911.621f, 337.5f);
        foreach (float y in new[] { 237.664f, 238.664f })
            Assert.Contains(rails, r => Mathf.Abs(r.Position.y - y) < 0.01f && Spans(r, new Vector2(-1859.770f, 6911.085f))
                && RailEnds(r).Any(e => Vector2.Distance(e, new Vector2(-1859.004f, 6911.238f)) < 0.45f));
        // The upper leg's 45 degree turn: its rail run back a metre over the lower leg to a post (hand-laid 112024, 112027,
        // 112052), beside the lower flight's run on.
        AssertHas(rails, StairRailing.ShortRailPrefab, -1858.047f, 240.664f, 6911.383f, 112.5f, 180f);
        AssertHas(rails, StairRailing.ShortRailPrefab, -1858.047f, 241.664f, 6911.383f, 112.5f, 180f);
        AssertHas(rails, BridgeLayout.RailPostPrefab, -1857.856f, 241.164f, 6911.845f, 202.5f, 90f);
        AssertHas(rails, StairRailing.ShortRailPrefab, -1857.618f, 240.664f, 6911.812f, 157.5f, 180f);
    }

    [Fact]
    public void AFloorAtTheRoadsEndOverTheLedgeIsRailedOnItsSidesOverIt()
    {
        // A staircase's top: a stair west-north-west, a 45 degree turn onto two floors laid on west-south-west
        // to the road's end; the first floor out over a drop, the second on the ground the road comes on at.
        var run = new StairRun { From = new Vector2(-9344f, -792f), To = new Vector2(-9300f, -792f), Width = 2f };
        run.Pieces.Add(Step(-9336.493f, 117.275f, -793.258f, 112.5f));
        run.Pieces.Add(Step(-9338.941f, 118.275f, -792.244f, 112.5f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFiller, "stone_wall_2x1", -9340.327f, 118.775f, -791.670f, 112.5f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFiller, "stone_wall_1x1", -9340.360f, 118.775f, -791.209f, 67.5f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFloor, "stone_floor_2x2", -9341.555f, 118.775f, -792.244f, 247.5f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFloor, "stone_floor_2x2", -9343.402f, 118.775f, -793.010f, 247.5f));
        List<StructurePiece> rails = Railing(run, ground: (x, z) => x <= -9344 || (x <= -9342 && z <= -793) ? 118.5f : 100f);
        // What was added by hand (ids 118087..118165): both sides of the first floor ...
        foreach (float y in new[] { 119.275f, 120.275f })
        {
            AssertHas(rails, StairRailing.LevelRailPrefab, -9341.938f, y, -791.321f, 337.5f, 180f);
            AssertHas(rails, StairRailing.LevelRailPrefab, -9341.172f, y, -793.168f, 337.5f, 180f);
            // ... its rail run back over the turn, and the stair's run on to it.
            AssertHas(rails, StairRailing.ShortRailPrefab, -9340.552f, y, -790.747f, 337.5f, 180f);
            AssertHas(rails, StairRailing.ShortRailPrefab, -9339.944f, y, -790.747f, 22.5f, 180f);
        }
        foreach (var (x, z) in new[] { (-9342.861f, -791.703f), (-9342.096f, -793.551f), (-9341.014f, -790.938f), (-9340.406f, -790.555f), (-9340.090f, -790.555f) })
            Assert.Contains(rails, r => r.Prefab == BridgeLayout.RailPostPrefab && Vector3.Distance(r.Position, new Vector3(x, 119.775f, z)) < 0.05f);
        // The second floor, on the ground, stays open to the road on its far side and its south side. Its north side, where
        // the ground half a metre out reads 1.14 m under it (the drop beginning there), is over the ledge since that is a
        // metre (StairRailing.LedgeDrop; 1.5 m before, open).
        Assert.DoesNotContain(rails, r => r.Prefab is StairRailing.LevelRailPrefab or StairRailing.ShortRailPrefab
            && Vector2.Distance(new Vector2(r.Position.x, r.Position.z), new Vector2(-9343.402f, -793.010f)) < 1.6f
            && Vector2.Distance(new Vector2(r.Position.x, r.Position.z), new Vector2(-9343.785f, -792.086f)) > 0.05f);
        foreach (float y in new[] { 119.275f, 120.275f })
            AssertHas(rails, StairRailing.LevelRailPrefab, -9343.785f, y, -792.086f, 337.5f, 180f);
    }

    [Theory]
    [InlineData(22.5f, false, false)]
    [InlineData(45f, false, false)]
    [InlineData(67.5f, false, false)]
    [InlineData(-22.5f, false, false)]
    [InlineData(-45f, false, false)]
    [InlineData(-67.5f, false, false)]
    [InlineData(22.5f, true, false)]
    [InlineData(45f, true, false)]
    [InlineData(-22.5f, true, false)]
    [InlineData(-45f, true, false)]
    [InlineData(45f, false, true)]
    [InlineData(-67.5f, false, true)]
    [InlineData(22.5f, true, true)]
    [InlineData(45f, true, true)]
    [InlineData(-45f, true, true)]
    public void EveryTurnsOuterCornerIsRailedPostToPostAndItsInnerCornerHasItsPost(float turn, bool legOverhead, bool marble)
    {
        // Three stairs north from (0, 0) at 10 m, then three turned; with legOverhead, a switchback's upper leg coming back
        // beside the turned flight on its outer side, 2 m over it, over where the lower flight's outer rail would run on
        // (at -1858,6890).
        StairKit kit = marble ? StairKit.BlackMarble : StairKit.Stone;
        string stair = marble ? "blackmarble_stair" : "stone_stair";
        float t = StairLayout.Wrap(turn), outer = turn > 0f ? -1f : 1f;
        StairPlan plan = StairLayout.LayFlight(new Vector2(0f, 0f), 10f, new[] { 0f, 0f, 0f, t, t, t }, kit, (x, z) => -50f);
        var run = StairRun.FromPlan(new Vector2(0f, 0f), new Vector2(plan.End.x, plan.End.z), 2f, plan, kit);
        StructurePiece upper = run.Pieces.First(s => s.Kind == StructurePieceKind.StairStep && Mathf.Abs(s.Position.y - 13f) < 0.01f);
        StairSupport.Walk u = StairSupport.WalkOf(upper, kit)!.Value;
        Vector2 p = new(outer, 6f), q = new Vector2(-outer, 6f) + StairLayout.Right(t) * (2f * outer);
        if (legOverhead)
        {
            Vector2 c = u.At(u.Depth / 2f - 1f, outer * 2f);
            run.Pieces.Add(Step(c.x, 15f, c.y, StairLayout.Wrap(upper.YawDegrees + 180f), stair));
        }
        List<StructurePiece> rails = Railing(run, kit);
        var level = rails.Where(r => r.Prefab is StairRailing.ShortRailPrefab or StairRailing.LevelRailPrefab).ToList();
        var posts = rails.Where(r => r.Prefab == BridgeLayout.RailPostPrefab && Mathf.Abs(r.Position.y - BridgeLayout.RailHeight / 2f - 13f) < 0.01f)
            .Select(r => new Vector2(r.Position.x, r.Position.z)).ToList();
        foreach (float y in new[] { 13f, 13f + BridgeLayout.RailHeight })
        {
            // From the lower flight's outer top corner, rail to rail, each ending on a post (or by one, within a joint), to
            // the upper flight's outer back corner (or a post standing in for it).
            var at = level.Where(r => Mathf.Abs(r.Position.y - y) < 0.01f).ToList();
            var reached = new List<Vector2> { p };
            for (int grew = 1; grew > 0;)
            {
                grew = 0;
                foreach (StructurePiece r in at.ToList())
                {
                    if (!reached.Any(x => Spans(r, x) || RailEnds(r).Any(e => Vector2.Distance(e, x) < 0.45f))) continue;
                    at.Remove(r);
                    grew++;
                    reached.AddRange(RailEnds(r));
                    reached.AddRange(posts.Where(x => Spans(r, x)));
                }
            }
            Assert.True(reached.Any(x => Vector2.Distance(x, q) < 0.45f),
                FormattableString.Invariant($"turn {turn}: no railing at {y} from {p} round to {q}; reached {string.Join(" ", reached)}"));
        }
        Assert.Contains(posts, x => Vector2.Distance(x, new Vector2(-outer, 6f)) < 0.05f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OfTwoFloorsAtTheRoadsEndOnlyTheOneOverTheLedgeIsRailedAndNotWhereTheRoadComesOn(bool marble)
    {
        // Four stairs north from (0, 0) at 10 m, two floors on past them to the road's end at (0, 12); the ground at the
        // floors' level from z = 11 on (the far floor on it, the road coming on over its far edge), 64 m under them nearer
        // (a black marble top at -6934.9,-3706.2).
        StairKit kit = marble ? StairKit.BlackMarble : StairKit.Stone;
        var s = StairPieceKind.Stair; var f = StairPieceKind.Landing;
        StairPlan plan = StairLayout.LayFlight(new Vector2(0f, 0f), 10f, new[] { 0f, 0f, 0f, 0f, 0f, 0f }, kit, (x, z) => -50f,
            new[] { s, s, s, s, f, f });
        var run = StairRun.FromPlan(new Vector2(0f, 0f), new Vector2(0f, 12f), 2f, plan, kit);
        List<StructurePiece> rails = Railing(run, kit, (x, z) => z >= 11 ? 14f : -50f);
        var level = rails.Where(r => r.Prefab == StairRailing.LevelRailPrefab).ToList();
        // The near floor's two sides, at both heights; nothing on the far floor, nor across the way.
        foreach (float y in new[] { 14f, 15f })
        {
            Assert.Single(level, r => Mathf.Abs(r.Position.y - y) < 0.01f && Vector2.Distance(new Vector2(r.Position.x, r.Position.z), new Vector2(-1f, 9f)) < 0.01f);
            Assert.Single(level, r => Mathf.Abs(r.Position.y - y) < 0.01f && Vector2.Distance(new Vector2(r.Position.x, r.Position.z), new Vector2(1f, 9f)) < 0.01f);
        }
        Assert.Equal(4, level.Count);
        Assert.Equal(4, rails.Count(r => r.Prefab == BridgeLayout.RailPostPrefab && Mathf.Abs(r.Position.y - 14.5f) < 0.01f && r.Position.z > 7.9f));
    }

    [Fact]
    public void AFlightRestingOnTheSlopeDownToTheRoadIsRailedRightDown()
    {
        // A staircase's foot: three stairs climbing south-south-west from the road, each resting on the
        // slope (no supports under them), then a 22.5 degree turn west-south-west. The flight stood bare both sides.
        var run = new StairRun { From = new Vector2(-9220f, -789f), To = new Vector2(-9240f, -800f), Width = 2f };
        run.Pieces.AddRange(Flight(-9223.617f, 61.275f, -791.076f, 22.5f, 3));
        run.Pieces.Add(Walked(StructurePieceKind.StairFiller, "stone_wall_2x1", -9225.723f, 63.775f, -796.158f, 22.5f));
        run.Pieces.AddRange(Flight(-9226.996f, 64.275f, -796.619f, 67.5f, 2));
        // The ground rising 1 m every 2 m up the flight, at each step's underside under its middle, the road at its foot.
        Vector2 foot = new(-9223.234f, -790.152f), up = StairLayout.Forward(202.5f);
        float Ground(int x, int z) => 61.275f + Mathf.Clamp(Vector2.Dot(new Vector2(x, z) - foot, up), 0f, 6f) / 2f - 0.5f;
        List<StructurePiece> rails = Railing(run, ground: Ground);
        // What was added by hand: both sides' handrails and lower rails down all three, and their posts.
        foreach (var (x, y, z) in new[] { (-9225.307f, 62.275f, -792.541f), (-9224.541f, 61.275f, -790.693f), (-9224.225f, 63.275f, -795.154f),
                     (-9223.459f, 62.275f, -793.307f), (-9222.693f, 61.275f, -791.459f) })
        {
            AssertHas(rails, BridgeLayout.StairRailPrefab, x, y, z, 112.5f);
            AssertHas(rails, BridgeLayout.StairRailPrefab, x, y + 1f, z, 112.5f);
        }
        foreach (var (x, y, z) in new[] { (-9225.689f, 63.775f, -793.465f), (-9224.924f, 62.775f, -791.617f), (-9224.158f, 61.775f, -789.770f),
                     (-9223.842f, 63.775f, -794.231f), (-9223.076f, 62.775f, -792.383f), (-9222.311f, 61.775f, -790.535f) })
            AssertHas(rails, BridgeLayout.RailPostPrefab, x, y, z, 22.5f, 90f);
    }

    [Fact]
    public void ABlackMarbleTurnOverASwitchbacksLowerLegIsClosedRoundItsOuterCorner()
    {
        // A staircase at -6895,-3680 (Mistlands): the upper leg turning 45 degrees south-south-east, its wedge
        // a 2x1x1 and a 1x1, over the lower leg's turn 5 m under it. The wedge stood open between its two corner posts.
        var run = new StairRun { From = new Vector2(-6880f, -3672f), To = new Vector2(-6890f, -3690f), Width = 2f };
        run.Pieces.Add(Step(-6894.004f, 37.547f, -3679.285f, 135f, "blackmarble_stair"));
        run.Pieces.Add(Step(-6895.959f, 38.547f, -3677.979f, 112.5f, "blackmarble_stair"));
        run.Pieces.Add(Step(-6896.393f, 42.547f, -3680.158f, 292.5f, "blackmarble_stair"));
        run.Pieces.Add(Walked(StructurePieceKind.StairFiller, "blackmarble_2x1x1", -6895.007f, 43.047f, -3680.732f, 292.5f));
        run.Pieces.Add(Walked(StructurePieceKind.StairFiller, "blackmarble_1x1", -6894.658f, 43.047f, -3680.429f, 337.5f));
        run.Pieces.AddRange(Flight(-6894.545f, 43.547f, -3682.006f, 337.5f, 2, "blackmarble_stair"));
        List<StructurePiece> rails = Railing(run, StairKit.BlackMarble);
        // The lower flight's rail run on a metre and the upper's run back, over the lower leg, both heights, to their posts.
        foreach (float y in new[] { 43.547f, 44.547f })
        {
            AssertHas(rails, StairRailing.ShortRailPrefab, -6894.625f, y, -3679.808f, 22.5f, 180f);
            AssertHas(rails, StairRailing.ShortRailPrefab, -6894.195f, y, -3680.238f, 247.5f, 180f);
        }
        AssertHas(rails, BridgeLayout.RailPostPrefab, -6894.163f, 44.047f, -3680.000f, 112.5f, 90f);
        AssertHas(rails, BridgeLayout.RailPostPrefab, -6894.387f, 44.047f, -3679.776f, 157.5f, 90f);
    }

    [Theory]
    [InlineData(45f)]
    [InlineData(-22.5f)]
    [InlineData(67.5f)]
    [InlineData(90f)]
    public void AStaircaseTurnedOffTheAxesIsRailedAsOnThem(float turn)
    {
        // This staircase's pieces all sit on a grid turned 22.5 degrees; the railing does not depend on that: the same flight,
        // turned and with a landing, laid heading north and heading 22.5 degrees east of it, railed alike piece for piece.
        var s = StairPieceKind.Stair; var f = StairPieceKind.Landing;
        List<StructurePiece> Laid(float h)
        {
            float t = StairLayout.Wrap(h + turn);
            StairPlan plan = StairLayout.LayFlight(new Vector2(0f, 0f), 10f, new[] { h, h, h, t, t, t, t, t }, StairKit.Stone, (x, z) => -50f,
                new[] { s, s, s, s, s, f, s, s });
            return Rails(plan, out _);
        }
        List<StructurePiece> north = Laid(0f), turned = Laid(22.5f);
        Assert.Equal(north.Count, turned.Count);
        foreach (StructurePiece r in north)
        {
            Vector2 at = StairLayout.Forward(22.5f) * r.Position.z + StairLayout.Right(22.5f) * r.Position.x;
            Assert.True(turned.Any(o => o.Prefab == r.Prefab && Vector3.Distance(o.Position, new Vector3(at.x, r.Position.y, at.y)) < 0.02f),
                FormattableString.Invariant($"no {r.Prefab} turned from {r.Position}"));
        }
    }

    private static Vector2[] RailEnds(StructurePiece r)
    {
        Vector2 c = new(r.Position.x, r.Position.z), d = StairLayout.Right(r.YawDegrees) * (r.Prefab == StairRailing.ShortRailPrefab ? 0.5f : 1f);
        return new[] { c - d, c + d };
    }

    /// <summary>Whether level rail <paramref name="r"/> reaches <paramref name="p"/>: its line within 0.1 m of it, between its ends.</summary>
    private static bool Spans(StructurePiece r, Vector2 p)
    {
        if (r.Prefab is not (StairRailing.ShortRailPrefab or StairRailing.LevelRailPrefab)) return false;
        float half = r.Prefab == StairRailing.ShortRailPrefab ? 0.5f : 1f;
        Vector2 c = new(r.Position.x, r.Position.z), d = StairLayout.Right(r.YawDegrees);
        float along = Vector2.Dot(p - c, d);
        return Mathf.Abs(along) <= half + 0.02f && (p - c - d * along).magnitude < 0.1f;
    }

    private static bool Crosses(Vector2 p1, Vector2 p2, Vector2 q1, Vector2 q2)
    {
        static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
        float d1 = Cross(q2 - q1, p1 - q1), d2 = Cross(q2 - q1, p2 - q1), d3 = Cross(p2 - p1, q1 - p1), d4 = Cross(p2 - p1, q2 - p1);
        return d1 * d2 < 0f && d3 * d4 < 0f;
    }
}
