using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;
using Xunit.Abstractions;
using Valheim.Testing.Doubles;

namespace ProceduralRoads.Tests;

/// <summary>
/// Stairs on steep faces: flights held out from a face on cantilevers
/// (a hand-built reference: pillars clipped into the wall, arches out from them, the stair on the
/// arches, a 2x1 block beside it), priced by the face's grade; turns priced by how steep the
/// ground is where they are made (aim a turn at a tree well); cantilevered legs may pass over
/// lower ones with headroom; and the road search offers stairs over faces steeper than 1:1 where
/// the stairs can be held out. Each rule has its own test.
/// </summary>
public class CantileverTests
{
    private readonly ITestOutputHelper _out;
    public CantileverTests(ITestOutputHelper output) => _out = output;

    private static Vector3 V(float x, float y, float z) => new(x, y, z);

    private static void Near(Vector3 expected, Vector3 actual, string what)
    {
        Assert.True(Vector3.Distance(expected, actual) < 0.01f, $"{what}: expected {expected}, was {actual}");
    }

    /// <summary>The pieces of one stair held out from the face, at a hand-built reference stair (read from the snap
    /// points): stone_stair 58027 rising toward +x over x -2301.4..-2299.4,
    /// z 1553.5..1555.5, foot at 278.611; the face on its -z side. Its pillars, arches and block
    /// stand exactly where the cantilever puts them, and each arch's tall end (its local +x end:
    /// snaps top and bottom there, cli_piece_geometry) is against the pillars.</summary>
    [Fact]
    public void AStairIsHeldOutWhereTheHandBuiltOneIs()
    {
        var plan = new StairPlan();
        // heading 90 (+x); its right is -z, the face's side.
        StairLayout.AddCantilever(plan, StairKit.Stone.Cantilever!, new Vector2(-2301.396f, 1554.542f), 90f, 278.611f, side: 1, across: 2f);

        StairPiece[] pillars = plan.Pieces.Where(p => p.Prefab == "stone_pillar").ToArray();
        StairPiece[] arches = plan.Pieces.Where(p => p.Prefab == "stone_arch").ToArray();
        StairPiece[] blocks = plan.Pieces.Where(p => p.Prefab == "stone_wall_2x1").ToArray();
        Assert.Equal(2, pillars.Length);
        Assert.Equal(2, arches.Length);
        Assert.Single(blocks);
        Assert.All(plan.Pieces, p => Assert.True(p.Cantilevered && p.Kind == StairPieceKind.Support));
        // Pillars 57992/57996: x -2301.4..-2300.4 and -2300.4..-2299.4, y 276.6..278.6, z 1552.5..1553.5.
        Near(V(-2300.896f, 277.611f, 1553.042f), pillars[0].Position, "first pillar");
        Near(V(-2299.896f, 277.611f, 1553.042f), pillars[1].Position, "second pillar");
        // Arches 58015/58005: the same x, y 277.6..278.6, z 1553.5..1555.5.
        Near(V(-2300.896f, 278.111f, 1554.542f), arches[0].Position, "first arch");
        Near(V(-2299.896f, 278.111f, 1554.542f), arches[1].Position, "second arch");
        // Block 58021: x -2301.4..-2299.4, y 278.6..279.6, z 1552.5..1553.5, its 2 m (local x) along the flight.
        Near(V(-2300.396f, 279.111f, 1553.042f), blocks[0].Position, "block");
        Vector2 blockLength = StairLayout.Right(blocks[0].YawDegrees);
        Assert.True(Mathf.Abs(blockLength.x) > 0.99f, $"block's local x points {blockLength}, not along the flight");
        foreach (StairPiece arch in arches)
        {
            // Local x is Right(yaw); the tall end (+1) sits at z 1553.542, against the pillars.
            Vector2 tall = new Vector2(arch.Position.x, arch.Position.z) + StairLayout.Right(arch.YawDegrees) * StairKit.Stone.Cantilever!.ArchTallEnd;
            Assert.True(Mathf.Abs(tall.y - 1553.542f) < 0.01f, $"arch's tall end at z {tall.y}");
        }
    }

    /// <summary>Black marble's arch is tall at its local -x end (cli_piece_geometry): turned
    /// the other way round from stone's, its tall end still meets the pillars, and its pillars are
    /// two 1 m columns a metre.</summary>
    [Fact]
    public void BlackMarblesArchIsTurnedSoItsTallEndMeetsThePillars()
    {
        var plan = new StairPlan();
        CantileverKit ck = StairKit.BlackMarble.Cantilever!;
        StairLayout.AddCantilever(plan, ck, new Vector2(0f, 0f), 0f, 10f, side: -1, across: 2f);
        StairPiece[] arches = plan.Pieces.Where(p => p.Prefab == "blackmarble_arch").ToArray();
        Assert.Equal(2, arches.Length);
        Assert.Equal(4, plan.Pieces.Count(p => p.Prefab == "blackmarble_column_1"));
        foreach (StairPiece arch in arches)
        {
            // Heading 0 (+z), face on the left: -x. Pillars at x -1.5.
            Vector2 tall = new Vector2(arch.Position.x, arch.Position.z) + StairLayout.Right(arch.YawDegrees) * ck.ArchTallEnd;
            Assert.True(Mathf.Abs(tall.x + 1f) < 0.01f, $"arch's tall end at x {tall.x}");
            Assert.Equal(9f, arch.Position.y, 3); // 2 m tall, its top under the stair's foot
        }
        Assert.All(plan.Pieces.Where(p => p.Prefab == "blackmarble_column_1"), p => Assert.Equal(-1.5f, p.Position.x, 3));
    }

    /// <summary>
    /// A planar face of 6:1 across z (the reference face is about 6:1), rising along x at a flight's
    /// 1:2 so a flight along it keeps its place on the face, and a shelf at its top (x past 20)
    /// for the staircase to finish on. Without cantilevers every piece stood on a column down the
    /// face (up to 12 m of pillars); held out, no column hangs into the drop.
    /// </summary>
    private static float Face(float x, float z) => x < 20f ? 0.5f * x - 6f * z : Mathf.Max(4f, 10f - 6f * z);

    private StairPlan LayAlongTheFace() =>
        StairLayout.Build(new Vector2(0f, 2f), new Vector2(21f, 2f), Face, footHeight: -6f, arriving: 90f);

    [Fact]
    public void AFlightAlongASheerFaceIsHeldOutNotStoodOnColumns()
    {
        StairPlan plan = LayAlongTheFace();
        _out.WriteLine($"reached {plan.Reached} {plan.Reason}; stairs {plan.Stairs}, cantilevered {plan.Cantilevered}, steepest face {plan.SteepestFace:F2}; " +
                       $"columns {plan.Pieces.Count(p => p.Kind == StairPieceKind.Support && !p.Cantilevered)}");
        Assert.True(plan.Reached, plan.Reason);
        Assert.True(plan.Cantilevered >= 8, $"{plan.Cantilevered} rows held out");
        Assert.True(plan.SteepestFace >= 5f, $"steepest face {plan.SteepestFace}");
        Assert.Contains(plan.Pieces, p => p.Prefab == "stone_arch");
        // Nothing hangs into the drop: no column under any piece.
        Assert.DoesNotContain(plan.Pieces, p => p.Kind == StairPieceKind.Support && !p.Cantilevered);
    }

    /// <summary>Wood has no arch: its flights stand on poles as ever.</summary>
    [Fact]
    public void WoodHasNoCantilever()
    {
        Assert.Null(StairKit.Wood.Cantilever);
        StairPlan plan = StairLayout.Build(new Vector2(0f, 2f), new Vector2(21f, 2f), Face, kit: StairKit.Wood, footHeight: -6f, arriving: 90f);
        Assert.Equal(0, plan.Cantilevered);
    }

    /// <summary>What holds a cantilever is its pillars sunk into the face: read in game, stone
    /// arches 1.6 m clear of the face held at 720 off the pillars alone (minimum 100). So a piece whose
    /// arches stand clear of the face is held out as long as its pillars are sunk; one whose pillars
    /// would not reach the ground (stacked no deeper than CantileverKit.MaxDepth) is not.</summary>
    [Fact]
    public void ACantileverIsHeldByItsPillarsSunkIntoTheFace()
    {
        Vector2 start = new(0f, 2f), dir = StairLayout.Forward(90f), right = StairLayout.Right(90f);
        // On the 6:1 face of the test above, the stair's wall edge at the ground: held out.
        Assert.Equal(1, StairLayout.CantileverSide(start, dir, right, 1f, -6f, StairKit.Stone, Face, out float face));
        Assert.Equal(6f, face, 2);
        // Two and a half metres higher, the arches' tall ends 2 m over the face, the pillars still sunk: held out,
        // stone as black marble.
        Assert.Equal(1, StairLayout.CantileverSide(start, dir, right, 1f, -3.5f, StairKit.Stone, Face, out _));
        Assert.Equal(1, StairLayout.CantileverSide(start, dir, right, 1f, -3.5f, StairKit.BlackMarble, Face, out _));
        // Up to 8 m over the face its strip columns still reach down into it (four stone pillars); higher, not held out.
        Assert.Equal(1, StairLayout.CantileverSide(start, dir, right, 1f, 4.5f, StairKit.Stone, Face, out _));
        Assert.Equal(0, StairLayout.CantileverSide(start, dir, right, 1f, 6f, StairKit.Stone, Face, out _));
        // A gentle slope is not held out from at all.
        Assert.Equal(0, StairLayout.CantileverSide(start, dir, right, 1f, 1f, StairKit.Stone, (x, z) => -0.8f * z, out _));
    }

    /// <summary>The price of a cantilevered piece rises with the face's grade (cheaper at lesser grades) and stops rising at its maximum.</summary>
    [Fact]
    public void ACantileversPriceRisesWithTheFaceUpToAMaximum()
    {
        float last = 0f;
        for (float g = StairLayout.CantileverFromGrade; g <= StairLayout.CantileverFullGrade; g += 0.5f)
        {
            float price = StairLayout.CantileverPrice(g);
            Assert.True(price > last, $"price at {g} is {price}, not above {last}");
            last = price;
        }
        Assert.Equal(StairLayout.CantileverMaxPrice, StairLayout.CantileverPrice(StairLayout.CantileverFullGrade + 1f), 3);
        Assert.Equal(StairLayout.CantileverMaxPrice, StairLayout.CantileverPrice(20f), 3);
    }

    /// <summary>A cantilevered piece may pass over a lower one of its own staircase where its lowest
    /// part keeps headroom over that piece; a piece on columns never may, and with the overhang rule off
    /// neither may a cantilever.</summary>
    [Fact]
    public void ACantileveredLegMayPassOverALowerOneWithHeadroom()
    {
        // A lower flight's piece at (0, 0) heading +x, its top at 0; then a piece it carries on to.
        var laid = new List<(Vector2, float, float, int)> { (new Vector2(0f, 0f), 90f, 0f, 0), (new Vector2(20f, 0f), 90f, 1f, 0) };
        // Straight above it, held out from a face on its right (-z): arch bottom 1 under its underside.
        Assert.False(StairLayout.CrossesLaid(laid, new Vector2(0f, 0f), 90f, underside: 3.5f, cantilever: 1, StairKit.Stone));
        // Its arch would stand in the headroom: 2.6 - 1 is 1.6 over the lower top.
        Assert.True(StairLayout.CrossesLaid(laid, new Vector2(0f, 0f), 90f, underside: 2.6f, cantilever: 1, StairKit.Stone));
        // Its pillars (2 m deep, in the strip a metre and a half to its right) over the lower piece:
        // shifted so the strip lies over the lower piece, 3.5 leaves 1.5 of headroom under them.
        Assert.True(StairLayout.CrossesLaid(laid, new Vector2(0f, 1.5f), 90f, underside: 3.5f, cantilever: 1, StairKit.Stone));
        Assert.False(StairLayout.CrossesLaid(laid, new Vector2(0f, 1.5f), 90f, underside: 4.5f, cantilever: 1, StairKit.Stone));
        // On columns: never over another piece.
        Assert.True(StairLayout.CrossesLaid(laid, new Vector2(0f, 0f), 90f, underside: 10f, cantilever: 0, StairKit.Stone));
    }

    /// <summary>The tree-well rule's price: a switchback on a bench costs what it always did, on steep
    /// ground more, the more so the steeper, up to a cap.</summary>
    [Fact]
    public void SwitchbacksCostMoreOnSteepGround()
    {
        Assert.Equal(0f, StairLayout.SwitchbackSteepPrice(0.3f));
        Assert.Equal(0f, StairLayout.SwitchbackSteepPrice(StairLayout.BenchGrade));
        Assert.True(StairLayout.SwitchbackSteepPrice(2f) > StairLayout.SwitchbackSteepPrice(1.5f));
        Assert.Equal(StairLayout.SwitchbackSteepPrice(StairLayout.SteepCap), StairLayout.SwitchbackSteepPrice(10f), 3);
    }

    /// <summary>
    /// A slope of 0.6 straight up the climb, steeper than a flight, the staircase kept to a strip 9 m wide (as a road's
    /// corridor keeps it). Past the strip's east side, 1.1 m beyond it, the ground falls away sheer, under no piece. A switchback
    /// there is priced by the ground round it (<see cref="StairLayout.SwitchbackSteepPrice"/>: a switchback on a sheer cliff
    /// would be hard, so aim for less steep terrain for those).
    /// </summary>
    private static float SlopeWithADropEast(float x, float z) => 0.6f * z - (x > 5.6f ? 20f * (x - 5.6f) : 0f);

    /// <summary>A scarp of 2.5:1 rising 24 m across the whole map: no road climbs it, and no step of a
    /// staircase on columns (1:1 at most, knight's moves included). Held out on cantilevers, stone
    /// stairs climb it on the diagonal (1.8:1 between cells), priced up with the grade; wood's (Meadows) still cannot.</summary>
    private sealed class SheerScarp : WorldGenerator
    {
        public Heightmap.Biome Biome = Heightmap.Biome.Mountain;
        public override float GetHeight(float wx, float wy)
        {
            if (Mathf.Abs(wx) > 200f || Mathf.Abs(wy) > 200f) return 0f;
            return 40f + Mathf.Clamp(wx * 2.5f, 0f, 24f);
        }
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Biome;
        public override int GetSeed() => 77;
    }

    [Fact]
    public void TheRoadSearchOffersStairsUpFacesSteeperThan1To1WhereTheyCanBeHeldOut()
    {
        var world = new SheerScarp();
        using var worldScope = new ValheimWorldScope().WithWorld(world);
        RoadNetworkGenerator.Reset();
        try
        {
            var finder = new RoadPathfinder(world) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f };
            Assert.NotNull(finder.FindPath(new Vector2(-80f, 0f), new Vector2(100f, 0f)));
            Assert.Contains(finder.LastStairSteps, s =>
                Mathf.Abs(world.GetHeight(s.to.x, s.to.y) - world.GetHeight(s.from.x, s.from.y)) / Vector2.Distance(s.from, s.to) > RoadPathfinder.StairMaxGrade);
            // In the Meadows (wood, on columns) no step over 1:1 is offered: the scarp cannot be climbed.
            var wood = new SheerScarp { Biome = Heightmap.Biome.Meadows };
            worldScope.WithWorld(wood);
            RoadNetworkGenerator.Reset();
            var meadows = new RoadPathfinder(wood) { Stairs = true, TerrainVarianceThreshold = 1000f, Meander = 0f };
            Assert.Null(meadows.FindPath(new Vector2(-80f, 0f), new Vector2(100f, 0f)));
        }
        finally
        {
            RoadNetworkGenerator.Reset();
        }
    }

    /// <summary>The price per metre climbed rises from 1:1 to its maximum where flights are held out from the face, and
    /// stays there up to the steepest step offered: a sheer wall costs what a cantilever face does.</summary>
    [Fact]
    public void AStairStepsPriceRisesWithItsGrade()
    {
        Assert.Equal(1f, RoadPathfinder.SteepStairFactor(0.5f));
        Assert.Equal(1f, RoadPathfinder.SteepStairFactor(RoadPathfinder.StairMaxGrade));
        Assert.True(RoadPathfinder.SteepStairFactor(2f) > RoadPathfinder.SteepStairFactor(1.5f));
        Assert.Equal(RoadPathfinder.SteepStairPriceMax, RoadPathfinder.SteepStairFactor(StairLayout.CantileverFromGrade), 3);
        Assert.Equal(RoadPathfinder.SteepStairPriceMax, RoadPathfinder.SteepStairFactor(RoadPathfinder.StairCantileverMaxGrade), 3);
        Assert.True(RoadPathfinder.StairCantileverMaxGrade >= StairLayout.CantileverFromGrade,
            "the search would refuse faces the layout holds flights out from");
    }

    /// <summary>The wedge a turn opens is read where its filler lies: a turn left off a flight climbing +z on
    /// ground rising to the west (left) at 2:1 has its wedge on the outside (east, downhill), clear; turned
    /// right, its wedge lies uphill (west) and the ground covers it.</summary>
    [Fact]
    public void ATurnsWedgeIsReadWhereItsFillerLies()
    {
        float West(float x, float z) => -2f * x;
        // The piece before ends at (0, 2), its top at 0.5 (the ground at its middle line is 0).
        Vector2 edge = new(0f, 2f);
        // Turned right 45 degrees: pivot on the right (east) corner, the wedge on the west.
        Vector2 pivotRight = edge + StairLayout.Right(0f) * 1f, startRight = pivotRight - StairLayout.Right(45f) * 1f;
        // Turned left 45: pivot on the west corner, the wedge on the east.
        Vector2 pivotLeft = edge - StairLayout.Right(0f) * 1f, startLeft = pivotLeft + StairLayout.Right(-45f) * 1f;
        float right = StairLayout.WedgeBuried(edge, 0.5f, pivotRight, startRight, West);
        float left = StairLayout.WedgeBuried(edge, 0.5f, pivotLeft, startLeft, West);
        Assert.True(right > 0.3f, $"wedge uphill buried {right:F2}");
        Assert.True(left < 0f, $"wedge downhill buried {left:F2}");
    }

    /// <summary>A hand-built set of cantilevered turns (at 1934,164,705): five stairs climbing on
    /// 202.5, 157.5, 135, 157.5 and 202.5 (turns of -45, -22.5, +22.5 and +45, the face on the right), each held
    /// out, every turn's wedge on an arch and a strip column. Its pieces where they follow the one pattern
    /// (prefab, x, y, z, yaw); where they do not (CantileverTurnDifferences), the pattern is followed.</summary>
    private static readonly (string prefab, float x, float y, float z, float yaw)[] HandBuiltTurns =
    {
        // the five stairs
        ("stone_stair", 1933.632f, 161.496f, 711.875f, 22.5f), ("stone_stair", 1933.632f, 162.496f, 709.262f, 337.5f),
        ("stone_stair", 1934.938f, 163.496f, 707.306f, 315f), ("stone_stair", 1936.245f, 164.496f, 705.351f, 337.5f),
        ("stone_stair", 1936.245f, 165.496f, 702.738f, 22.5f),
        // each stair's two arches
        ("stone_arch", 1933.823f, 160.996f, 712.337f, 202.5f), ("stone_arch", 1933.440f, 160.996f, 711.413f, 202.5f),
        ("stone_arch", 1933.440f, 161.996f, 709.723f, 157.5f), ("stone_arch", 1933.823f, 161.996f, 708.800f, 157.5f),
        ("stone_arch", 1934.585f, 162.996f, 707.660f, 135f), ("stone_arch", 1935.292f, 162.996f, 706.953f, 135f),
        ("stone_arch", 1936.053f, 163.996f, 705.813f, 157.5f), ("stone_arch", 1936.436f, 163.996f, 704.889f, 157.5f),
        ("stone_arch", 1936.436f, 164.996f, 703.200f, 202.5f), ("stone_arch", 1936.053f, 164.996f, 702.276f, 202.5f),
        // each turn: the arch under its wedge, the 2x1 across the old edge, a 1x1 behind the new edge at 45
        ("stone_arch", 1933.058f, 160.996f, 710.489f, 202.5f), ("stone_wall_2x1", 1933.058f, 161.996f, 710.489f, 22.5f), ("stone_wall_1x1", 1932.596f, 161.996f, 710.456f, 67.5f),
        ("stone_arch", 1934.206f, 161.996f, 707.876f, 157.5f), ("stone_wall_2x1", 1934.206f, 162.996f, 707.876f, 337.5f),
        ("stone_arch", 1935.999f, 162.996f, 706.246f, 135f), ("stone_wall_2x1", 1935.999f, 163.996f, 706.246f, 135f),
        ("stone_arch", 1936.819f, 163.996f, 703.965f, 157.5f), ("stone_wall_2x1", 1936.819f, 164.996f, 703.965f, 157.5f), ("stone_wall_1x1", 1937.281f, 164.996f, 703.932f, 112.5f),
        // strip columns' top pillars and blocks beside, where they are laid (stairs 1, 4, 5), the turn columns (turns 1, 3),
        // and turn 1's block carried on past the first stair's top, turned away from the face
        ("stone_pillar", 1932.437f, 160.496f, 712.911f, 202.5f), ("stone_pillar", 1932.054f, 160.496f, 711.987f, 202.5f), ("stone_wall_2x1", 1932.246f, 161.996f, 712.449f, 292.5f),
        ("stone_pillar", 1934.667f, 163.496f, 705.239f, 337.5f), ("stone_pillar", 1935.050f, 163.496f, 704.315f, 337.5f), ("stone_wall_2x1", 1934.859f, 164.996f, 704.777f, 67.5f),
        ("stone_pillar", 1935.050f, 164.496f, 703.774f, 202.5f), ("stone_pillar", 1934.667f, 164.496f, 702.850f, 202.5f), ("stone_wall_2x1", 1934.859f, 165.996f, 703.312f, 292.5f),
        ("stone_pillar", 1931.672f, 160.496f, 711.063f, 202.5f), ("stone_pillar", 1934.938f, 162.496f, 705.185f, 135f),
        ("stone_wall_2x1", 1931.480f, 161.996f, 710.601f, 292.5f),
    };

    /// <summary>A piece's yaw as the game sees it: a pillar or 1x1 looks the same every quarter turn, a 2x1 every half.</summary>
    private static float Symmetric(string prefab, float yaw) =>
        StairLayout.Wrap(yaw) % (prefab == "stone_pillar" || prefab == "stone_wall_1x1" ? 90f : prefab == "stone_wall_2x1" ? 180f : 360f);

    /// <summary>Laid against ground far over it, the face holding every block beside (the hug rule leaves out blocks standing out
    /// of the face): the flight stands against the face.</summary>
    private static StairPlan LayHandBuiltTurns()
    {
        return StairLayout.LayFlight(new Vector2(1934.015f, 712.799f), 161.496f,
            new[] { 202.5f, 157.5f, 135f, 157.5f, 202.5f }, StairKit.Stone, (x, z) => 200f, side: 1);
    }

    [Fact]
    public void CantileveredTurnsAreBuiltAsTheHandBuiltOnes()
    {
        StairPlan plan = LayHandBuiltTurns();
        foreach (var (prefab, x, y, z, yaw) in HandBuiltTurns)
        {
            var at = new Vector3(x, y, z);
            Assert.True(plan.Pieces.Exists(p => p.Prefab == prefab && Vector3.Distance(p.Position, at) < 0.02f
                && Math.Abs(Symmetric(prefab, p.YawDegrees) - Symmetric(prefab, yaw)) < 0.1f),
                $"no {prefab} at {at} yaw {yaw}; nearest: " + string.Join(" ", plan.Pieces.Where(p => p.Prefab == prefab)
                    .OrderBy(p => Vector3.Distance(p.Position, at)).Take(2).Select(p => $"{p.Position} {p.YawDegrees}")));
        }
        // Nothing hangs into the drop: every support is the cantilever's.
        Assert.DoesNotContain(plan.Pieces, p => p.Kind == StairPieceKind.Support && !p.Cantilevered);
        Assert.Equal(4, plan.Turns);
    }

    /// <summary>Strip columns go on down until sunk into the face or the ground (a hand-built stack: where the
    /// lowest stair's top pillars hung 3-4 m over the ground, they were carried on down to it).</summary>
    [Fact]
    public void AStripColumnGoesDownUntilItIsSunk()
    {
        StairPlan high = StairLayout.LayFlight(new Vector2(0f, 0f), 10f, new[] { 0f }, StairKit.Stone, (x, z) => x > 1f ? 9.5f : 0f, side: 1);
        StairPlan low = StairLayout.LayFlight(new Vector2(0f, 0f), 10f, new[] { 0f }, StairKit.Stone, (x, z) => x > 1f ? 5.5f : 0f, side: 1);
        // Heading 0, face on the right (+x): the strip at x = 1.5. A face at 9.5 takes the one 2 m pillar (8..10);
        // at 5.5 the column goes on to 4..6, sunk 1.5 m.
        Assert.Equal(2, high.Pieces.Count(p => p.Prefab == "stone_pillar"));
        Assert.Equal(6, low.Pieces.Count(p => p.Prefab == "stone_pillar"));
        Assert.Equal(5f, low.Pieces.Where(p => p.Prefab == "stone_pillar").Min(p => p.Position.y), 3);
    }

    /// <summary>A flight set off a metre further from the 6:1 face than its first one: without the hug rule it
    /// keeps its line and its strip stands out of the face; priced by the strip that shows, it steps in to the face
    /// (the stairs hug the wall closer, so less of the side block shows through). Every tread stays clear.</summary>
    [Fact]
    public void ACantileveredFlightHugsTheFace()
    {
        float Shown(StairPlan plan) => plan.Pieces.Where(p => p.Cantilevered && p.Prefab == "stone_wall_2x1" && p.Kind == StairPieceKind.Support)
            .Select(p => Math.Max(0f, p.Position.y + 0.5f - Face(p.Position.x, p.Position.z))).DefaultIfEmpty(0f).Average();
        StairPlan Lay() => StairLayout.Build(new Vector2(0f, 2.5f), new Vector2(21f, 2.5f), Face, footHeight: -6f, arriving: 90f);
        StairPlan hug = Lay();
        Assert.True(hug.Reached, hug.Reason);
        Assert.True(hug.Cantilevered >= 6, $"{hug.Cantilevered} rows held out");
        _out.WriteLine($"strip shown: {Shown(hug):F2} m");
        Assert.True(Shown(hug) < 0.3f, $"the strip shows {Shown(hug):F2} m of side block");
        Assert.True(hug.MaxBuried <= StairCover.RoadStaircase);
    }

    /// <summary>A road arriving heading north (+z) at the foot of a slope that climbs east (a foot at Hildir's cave's
    /// approach met the road 55-90 degrees off its heading): held to set off within 45 degrees of the road, the
    /// layout laid a pad of floors and turned off it (stairs should join like this more often than two starter blocks).
    /// Laid again starting on a stair, it comes up out of the road and turns on the stairs.</summary>
    private static float EastSlope(float x, float z) => x < 3f ? 0f : 0.5f * (x - 3f);

    [Fact]
    public void AStaircaseComesUpOutOfTheRoadOnAStairNotAPad()
    {
        StairPlan Lay() => StairLayout.Build(new Vector2(0f, 0f), new Vector2(24f, 2f), EastSlope, arriving: 0f, sunkFoot: true);
        StairPlan plan = Lay();
        _out.WriteLine($"stair first: starts on floor {plan.StartsOnFloor}, start heading {plan.StartHeading}");
        Assert.True(plan.Reached, plan.Reason);
        Assert.False(plan.StartsOnFloor);
        Assert.InRange(Math.Abs(StairLayout.Wrap(plan.StartHeading + 180f) - 180f), 0f, 3f * StairLayout.HeadingStep + 0.01f);
    }

    /// <summary>The block beside a held-out stair is laid where the face holds most of it, and left out where it would
    /// stand clear of the rock between the stair and the face (the hug applies to the stairs, not the side wall).</summary>
    [Fact]
    public void ABlockBesideIsLaidOnlyWhereTheFaceHoldsIt()
    {
        // Heading 0, face on the right (+x): the strip's middle at x = 1.5. Base 10: the block spans 10..11.
        StairPlan sunk = StairLayout.LayFlight(new Vector2(0f, 0f), 10f, new[] { 0f }, StairKit.Stone, (x, z) => x > 1f ? 10.8f : 0f, side: 1);
        StairPlan clear = StairLayout.LayFlight(new Vector2(0f, 0f), 10f, new[] { 0f }, StairKit.Stone, (x, z) => x > 1f ? 9f : 0f, side: 1);
        Assert.Single(sunk.Pieces, p => p.Prefab == "stone_wall_2x1");
        Assert.DoesNotContain(clear.Pieces, p => p.Prefab == "stone_wall_2x1");
    }

    /// <summary>A road staircase top at 6468.3,-1522.5: its top edge at 162.3, climbing toward 202.5, the
    /// surface past it rising 1.1-1.5 m over the next 5 m, the road carrying on that way. The top goes a bit out
    /// and does the double stair ridge to land on the surface: one more stair out, then (a floor where the
    /// surface is about level, the ridge only higher) a floor onto the surface, and the road starts past it. Without the
    /// rule the road starts at the top, under the lip.</summary>
    [Fact]
    public void ARoadStaircaseTopGoesOutAndCrestsOntoTheSurface()
    {
        Vector2 edge = new(6467.936f, -1523.402f), dir = StairLayout.Forward(202.5f);
        float[] ds = { 0f, 1f, 2f, 3f, 5f, 8f }, hs = { 162.2f, 162.6f, 163.2f, 163.4f, 163.8f, 163.9f };
        float Ground(float x, float z)
        {
            float d = Vector2.Dot(new Vector2(x, z) - edge, dir);
            if (d <= 0f) return hs[0] + 0.5f * d;
            for (int i = 1; i < ds.Length; i++) if (d <= ds[i]) return Mathf.Lerp(hs[i - 1], hs[i], (d - ds[i - 1]) / (ds[i] - ds[i - 1]));
            return hs[hs.Length - 1];
        }
        StairPlan Top() => new() { Kit = StairKit.Stone.Name, Reached = true, End = new Vector3(edge.x, 162.326f, edge.y), EndHeading = 202.5f };
        var path = new List<Vector2> { edge - dir * 10f, edge, edge + dir * 12f };
        StairPlan plan = Top();
        Assert.True(RoadStairRuns.TopOntoSurface(plan, path, 2, Ground, out Vector2 past, out float pastHeight));
        // One stair out, then (the surface there within the floor's band of the new top) a floor onto it.
        Assert.Equal(1, plan.Pieces.Count(p => p.Kind == StairPieceKind.Stair));
        Assert.Single(plan.Pieces, p => p.Prefab == "stone_floor_2x2");
        Assert.True(Math.Abs(pastHeight - Ground(past.x, past.y)) <= StairLayout.TopFloorBand && pastHeight > 163f, $"road starts at {pastHeight:F2}");
        int pieces = plan.Pieces.Count;
        // Called again for the next road that walks it: the same, nothing added.
        Assert.True(RoadStairRuns.TopOntoSurface(plan, path, 2, Ground, out _, out _));
        Assert.Equal(pieces, plan.Pieces.Count);
    }

    /// <summary>A top at the mountain cave entry: top stair (1932.190, 169.451, 697.097) yaw 157.5, its top edge
    /// at 170.45, the plateau past it 0-0.5 m over it, a gap at the top of the stair run. The hand-built fix, one
    /// stone_floor_2x2 at (1931.424, 169.951, 698.945) yaw 247.5, level with the top and out onto the snow, is laid; without
    /// the rule the top is left as it was.</summary>
    [Fact]
    public void ATopNearlyLevelWithTheGroundGetsAFloorOutOntoIt()
    {
        Vector2 edge = new(1931.807f, 698.021f), dir = StairLayout.Forward(337.5f);
        float[] ds = { 0f, 1f, 2f, 3f, 4f, 6f }, hs = { 169.5f, 169.9f, 170.35f, 170.5f, 170.45f, 169.8f };
        float Ground(float x, float z)
        {
            float d = Vector2.Dot(new Vector2(x, z) - edge, dir);
            if (d <= 0f) return hs[0] + d;
            for (int i = 1; i < ds.Length; i++) if (d <= ds[i]) return Mathf.Lerp(hs[i - 1], hs[i], (d - ds[i - 1]) / (ds[i] - ds[i - 1]));
            return hs[hs.Length - 1];
        }
        StairPlan Top() => new() { Kit = StairKit.Stone.Name, Reached = true, End = new Vector3(edge.x, 170.451f, edge.y), EndHeading = 337.5f,
            LastRowKind = StairPieceKind.Stair, LastRowPieces = 0 };
        StairPlan plan = Top();
        Assert.Equal(RoadStairRuns.LedgeFinish.TopFloor, RoadStairRuns.FinishOverLedge(plan, Ground, 2f, _ => true));
        StairPiece floor = Assert.Single(plan.Pieces, p => p.Prefab == "stone_floor_2x2");
        Assert.True(Vector3.Distance(floor.Position, new Vector3(1931.424f, 169.951f, 698.945f)) < 0.02f, $"floor at {floor.Position}");
        Assert.Equal(247.5f % 90f, floor.YawDegrees % 90f, 1);
    }

    /// <summary>A road arriving heading north at the foot of a slope that climbs south, behind it: set off freely, the
    /// staircase started straight back across the road and on up the slope (at 1948.1,691.7, 180 degrees opposite the
    /// direction of road travel and blocking the actual road to the stair start). Held to the road's
    /// arrival it sets off back at most StairLayout.MostSetOffBack pieces, then faces within 90 degrees of it and turns on
    /// the stairs.</summary>
    [Fact]
    public void AStaircaseFromARoadsEndSetsOffFacingTheRoadsArrival()
    {
        float South(float x, float z) => z < 0f ? -0.5f * z : 0f;
        StairPlan Lay() => StairLayout.Build(new Vector2(0f, 0f), new Vector2(0f, -20f), South, sunkFoot: true, facing: 0f);
        StairPlan plan = Lay();
        Assert.True(plan.Reached, plan.Reason);
        Assert.InRange(plan.SetOffBack, 0, StairLayout.MostSetOffBack);
        Assert.True(BackRun(plan, 0f) <= StairLayout.MostSetOffBack, $"ran back {BackRun(plan, 0f)} pieces");
    }

    /// <summary>Walked pieces from the foot heading more than 90 degrees off <paramref name="facing"/> before the first that
    /// does not.</summary>
    private static int BackRun(StairPlan plan, float facing)
    {
        int run = 0;
        foreach (StairPiece p in plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair || p.Kind == StairPieceKind.Landing).Skip(1))
        {
            float heading = StairLayout.Wrap(p.YawDegrees + 180f);
            if (Math.Abs(StairLayout.Wrap(heading - facing + 180f) - 180f) <= 90.01f) break;
            run++;
        }
        return run;
    }

    /// <summary>A Hildir_cave foot (1664,768): the road arrives heading north into a face, every heading
    /// within 90 degrees of it buried at the foot, the way up a slope behind and to the east. Held strictly to the road's
    /// heading, no piece fits; set off back beside the road for up to three pieces (staircases back along the road at most
    /// about three stair lengths), it turns to face the road's way and climbs.</summary>
    [Fact]
    public void AFootInFrontOfAFaceSetsOffBackAtMostThreePiecesThenTurns()
    {
        float Ground(float x, float z) => z > 0.5f ? 6f + z : Mathf.Max(0f, 0.5f * x);
        StairPlan Lay() => StairLayout.Build(new Vector2(0f, 0f), new Vector2(16f, -6f), Ground, sunkFoot: true, facing: 0f);
        StairPlan plan = Lay();
        Assert.True(plan.Reached, plan.Reason);
        Assert.InRange(plan.SetOffBack, 1, StairLayout.MostSetOffBack);
    }

    /// <summary>At MountainCave02 1920,704: the foot set off 45 degrees off its road (it should line up better with the
    /// road, straight on, moved a bit to the left). Where straight on
    /// fits as well, the first piece sets off along the road's travel.</summary>
    [Fact]
    public void AFootSetsOffAlongItsRoadWhereThatFits()
    {
        // A slope rising 22.5 degrees off the road's travel (north): a flight up it fits straight on as well, a little off
        // the fall line.
        Vector2 up = StairLayout.Forward(22.5f);
        float Ground(float x, float z) => Mathf.Max(0f, 0.5f * Vector2.Dot(new Vector2(x, z), up));
        StairPlan Lay() => StairLayout.Build(new Vector2(0f, 0f), up * 24f, Ground, sunkFoot: true, facing: 0f);
        StairPlan plan = Lay();
        Assert.True(plan.Reached, plan.Reason);
        Assert.True(plan.StartHeading == 0f, $"set off on {plan.StartHeading}");
    }

    /// <summary>A Hildir_cave foot (-2304,1536): the road climbs to its end at (-2305.1, 1573.9) heading south
    /// along x = -2306 ((-2306.3, 1580.5) to (-2305.0, 1576.4)); the flight set off at 45 degrees, back up beside the road,
    /// from the road's end, a floor turned on it. A hand-built first stair at (-2303.734, 1575.360) sits beside the road
    /// (where it starts at a sharper angle it should align more to the side of the road). The approach is laid again from
    /// beside the road's end, on the side it turns to, at the road's edge; carrying on the road's way it stays where it is.</summary>
    [Fact]
    public void AFootSettingOffSharplyStartsBesideTheRoadsEnd()
    {
        Vector2 end = new(-2305.1f, 1573.9f);
        float travel = StairLayout.Bearing(new Vector2(-2306.3f, 1580.5f), new Vector2(-2305.0f, 1576.4f));
        List<Vector2> beside = RoadStairRuns.BesideTheRoadsEnd(end, travel, 45f);
        Assert.Single(beside);
        Vector2 first = beside[0];
        Assert.True(first.x > end.x, $"beside the road at {first}, not on the side the flight turns to");
        Vector2 across = StairLayout.Right(travel);
        Assert.True(Mathf.Abs(Vector2.Dot(first - end, across)) >= RoadNetworkGenerator.RoadWidth / 2f - 0.01f, $"{first} on the road");
        Assert.True(Vector2.Distance(first, new Vector2(-2303.734f, 1575.360f)) < 1.5f, $"{first} far from the hand-built first stair");
        Assert.Empty(RoadStairRuns.BesideTheRoadsEnd(end, travel, travel + 22.5f));
    }

    /// <summary>At the top of the 41 m staircase at 5669.6,4239.7: the flight came in facing away from the way
    /// the road leaves its top (it should face the other direction). Held to the
    /// road's departure, the top arrives within 90 degrees of it; free, it came straight up the slope, 135 degrees off.</summary>
    [Fact]
    public void AStaircaseTopArrivesFacingTheWayItsRoadLeaves()
    {
        float North(float x, float z) => Mathf.Max(0f, 0.5f * z);
        StairPlan Lay() => StairLayout.Build(new Vector2(0f, 0f), new Vector2(0f, 24f), North, sunkFoot: true, topFacing: 135f);
        float Off(StairPlan p) => Math.Abs(StairLayout.Wrap(p.EndHeading - 135f + 180f) - 180f);
        StairPlan plan = Lay();
        Assert.True(plan.Reached, plan.Reason);
        Assert.True(Off(plan) <= 90.01f, $"arrived on {plan.EndHeading}");
    }

    /// <summary>A foot at the 41 m staircase (5629.9,4266.0): two stairs set off along the road (yaw 247.5,
    /// climbing 67.5, the road arriving on about 53), then a bend to 112.5; set beside the road by its settled flight, it
    /// started off to the side. Straight on at its first stairs, it stays in the road's middle; turned at both, beside.</summary>
    [Fact]
    public void AFootStraightOnWithItsRoadStaysCentredThoughItBendsLater()
    {
        var plan = new StairPlan();
        void Stair(float x, float z, float yaw) => plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Stair, Position = new Vector3(x, 0f, z), YawDegrees = yaw });
        Stair(5629.889f, 4265.993f, 247.5f);
        Stair(5631.736f, 4266.758f, 247.5f);
        Stair(5634.350f, 4266.757f, 292.5f);
        var foot = new Vector2(5629.0f, 4265.6f);
        Assert.False(RoadStairRuns.SetsOffTurned(plan, foot, 53f));
        Assert.True(RoadStairRuns.SetsOffTurned(plan, foot, 160f), "turned at both, it is set beside the road");
        // Its first stairs back along the road (a foot at -3187.2,-27.5), though it settles facing it: beside.
        Assert.True(RoadStairRuns.SetsOffTurned(plan, foot, 225f), "set off back, it stayed in the road's middle");
    }

    /// <summary>A black marble top (463.6,6503.4): its 2x2 stood 0.2-0.7 m over the road past it, high enough that a
    /// player can get stuck on it briefly. A top floor is laid only where the ground across its whole far
    /// edge is within a step of it; a road falling away from the top meets the top itself.</summary>
    [Fact]
    public void ATopFloorIsNotLaidProudOfTheGroundPastIt()
    {
        StairPlan Top() => new() { Kit = StairKit.BlackMarble.Name, Reached = true, End = new Vector3(0f, 10f, 0f), EndHeading = 0f,
            LastRowKind = StairPieceKind.Stair, LastRowPieces = 0 };
        // Past the top the ground falls 0.25 m per metre, and across it 0.3 m per metre to the left: 0.6-1 m under the floor's far corners.
        float Falling(float x, float z) => 10f - Mathf.Max(0f, z) * 0.25f + Mathf.Min(0f, x) * 0.3f;
        Assert.Equal(0, StairLayout.AddTopFloor(Top(), Falling, 2f).pieces);
        // Level with it across: laid.
        float Level(float x, float z) => 10f - Mathf.Max(0f, z) * 0.05f;
        Assert.True(StairLayout.AddTopFloor(Top(), Level, 2f).pieces > 0);
    }

    /// <summary>A foot at -3187.2,-27.5: the road arriving heading north, the flight set off at 225 from the
    /// road's middle, a last resort (as a last resort the base is off the centre of the road).
    /// Relaid beside the road, a last resort is held to no facing (held to the road's, it could not be relaid and stayed in
    /// the middle); a foot that faced the road, or ran back facing it, still is.</summary>
    [Fact]
    public void ALastResortFootIsRelaidBesideItsRoadAsItIs()
    {
        Assert.Null(RoadStairRuns.BesideHold(new StairPlan { StartHeading = 225f }, 0f));
        Assert.Equal(0f, RoadStairRuns.BesideHold(new StairPlan { StartHeading = 67.5f }, 0f));
        Assert.Equal(0f, RoadStairRuns.BesideHold(new StairPlan { StartHeading = 180f, SetOffBack = 2 }, 0f));
    }

    /// <summary>A foot at 8972.6,3335.8: the road arriving on 161, the flight turning left to 112.5, its foot set
    /// on the road's right and the flight crossing back over the road's line. A turned foot is set only on the side it turns
    /// to; turned right back along the road, either side.</summary>
    [Fact]
    public void ATurnedFootIsSetOnlyOnTheSideItTurnsTo()
    {
        Assert.Equal(new[] { -1 }, RoadStairRuns.BesideSides(-48.5f, -1));
        Assert.Equal(new[] { 1, -1 }, RoadStairRuns.BesideSides(170f, 1));
    }

    /// <summary>Searched down from the top edge of a staircase over a ridge (-8030,-660: the top of one set of
    /// stairs was too high to walk over, fixed by snapping to the other set: just one stair up and another down),
    /// the second staircase's top stair stands back to back with the first's: its high edge on that edge, at that height,
    /// heading back, and its foot down by the road below.</summary>
    [Fact]
    public void AStaircaseSearchedDownFromAnotherTopStandsBackToBackWithIt()
    {
        float Ridge(float x, float z) => 40f + Mathf.Clamp(24f - Mathf.Abs(x) * 1.2f, 0f, 24f);
        var edge = new Vector2(-0.5f, 0f);
        var top = new StairContinuation(edge, 90f, 63.6f, StairPieceKind.Stair);
        StairPlan plan = StairLayout.BuildDown(top, new Vector2(24f, 0f), Ridge, 2f, StairKit.Stone);
        Assert.True(plan.Reached, plan.Reason);
        Assert.True(Vector3.Distance(plan.End, new Vector3(edge.x, 63.6f, edge.y)) < 0.01f, $"top at {plan.End}");
        Assert.Equal(270f, plan.EndHeading, 1);
        // Its top stair: a rise under the top, its back on the edge, climbing back west.
        StairPiece last = plan.Pieces.Last(p => p.Kind == StairPieceKind.Stair);
        Assert.Equal(63.6f - StairLayout.PieceRise, last.Position.y, 2);
        Assert.True(plan.Foot.HasValue && Mathf.Abs(plan.Foot.Value.y - Ridge(plan.Foot.Value.x, plan.Foot.Value.z)) < 1.5f, $"foot at {plan.Foot}");
        Assert.True(plan.Rise > 20f, $"rose {plan.Rise}");
    }

    /// <summary>A top at 6495.5,-1464.9: the last flight curled round over the road it hands to and lay on its
    /// surface for several pieces, taking over the road itself. At a top, as at a foot, only the joining piece
    /// (within JoiningStep) may stand on the road: a stair 4 m on along the road at its level lies on it.</summary>
    [Fact]
    public void AStaircasesTopTakesNoMoreOfItsRoadThanTheJoiningPiece()
    {
        float Ground(float x, float z) => 0.5f * Mathf.Max(0f, x);
        var path = new List<Vector2> { new(-16f, 0f), new(-8f, 0f), new(0f, 0f), new(8f, 0f), new(16f, 0f), new(24f, 0f), new(32f, 0f) };
        StairPlan Top(float x) => new() { Reached = true, Pieces = { new StairPiece { Kind = StairPieceKind.Stair, Position = new Vector3(x, 7f, 0.5f), YawDegrees = 270f } } };
        Assert.True(RoadStairRuns.OverRoad(Top(20f), path, 2, 4, Ground), "4 m on along the road, at its level");
        Assert.False(RoadStairRuns.OverRoad(Top(17.5f), path, 2, 4, Ground), "the joining piece");
    }

    /// <summary>At -5853,5918: a flight running beside its road a few metres off, both climbing, mostly floors
    /// (better to avoid running parallel, even if it costs POIs). Three walked pieces
    /// in a row within 5 m of a road and 1.5 m of its height, away from the staircase's own ends, refuse the layout.</summary>
    [Fact]
    public void AStaircaseRunningBesideARoadAtItsHeightIsRefused()
    {
        float Ground(float x, float z) => 40f + 0.1f * x;
        var path = new List<Vector2>();
        for (int x = -40; x <= 40; x += 8) path.Add(new Vector2(x, 0f));
        path.Add(new Vector2(40f, 16f));
        StairPlan Beside(float lift)
        {
            var plan = new StairPlan { Reached = true };
            foreach (float x in new[] { -24f, -22f, -20f, -18f })
                plan.Pieces.Add(new StairPiece { Kind = StairPieceKind.Landing, Position = new Vector3(x, Ground(x, 0f) - 0.5f + lift, 3f), YawDegrees = 90f });
            return plan;
        }
        // Its stretch is the road's last leg, (40,0) to (40,16): the run at x -24..-18 lies beside the road before it.
        Assert.True(RoadStairRuns.BesideARoad(Beside(0f), path, 10, 11, Ground));
        Assert.False(RoadStairRuns.BesideARoad(Beside(3f), path, 10, 11, Ground), "3 m over the road it is not beside it");
    }

    /// <summary>A foot at -8018,-660 stood against a bank (minor earthworks give it space): a level apron is cut behind it
    /// at its height, the bank 1:1 there; a foot that opens onto
    /// flat ground gets none.</summary>
    [Fact]
    public void AFootAgainstABankGetsAnApronCutBehindIt()
    {
        // The climb rises west; north of z = 0 a 1:1 bank.
        float Corner(float x, float z) => 40f + Mathf.Max(0f, -x) * 0.5f + Mathf.Max(0f, z);
        float Flat(float x, float z) => 40f + Mathf.Max(0f, -x) * 0.5f;
        StairPlan banked = StairLayout.Build(new Vector2(0f, 0f), new Vector2(-16f, 0f), Corner, sunkFoot: true);
        Assert.True(banked.Reached, banked.Reason);
        int before = banked.Cuts.Count;
        Assert.True(StairLayout.CutFootApron(banked, Corner, 2f) > 0, "no apron against the bank");
        Vector3 foot = banked.Foot!.Value;
        Assert.All(banked.Cuts.Skip(before), c => Assert.Equal(foot.y - StairLayout.CutBelowSurface, c.y, 3));
        Assert.All(banked.Cuts.Skip(before), c => Assert.True(Vector2.Distance(new Vector2(c.x, c.z), new Vector2(foot.x, foot.z)) <= StairLayout.ApronDepth + 2.5f));
        StairPlan flat = StairLayout.Build(new Vector2(0f, 0f), new Vector2(-16f, 0f), Flat, sunkFoot: true);
        Assert.Equal(0, StairLayout.CutFootApron(flat, Flat, 2f));
    }

    /// <summary>No switchback in a staircase's first five stairs from its foot (Hildir_cave 1664,768): up a narrow gully, the first
    /// switchback comes after five stairs; before, three.</summary>
    [Fact]
    public void TheFirstSwitchbackComesFiveStairsUpFromTheFoot()
    {
        int StairsBeforeSwitchback(StairPlan plan)
        {
            int stairs = 0;
            StairPiece? previous = null;
            foreach (StairPiece p in plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair || p.Kind == StairPieceKind.Landing))
            {
                if (previous != null && previous.Kind == StairPieceKind.Landing && p.Kind == StairPieceKind.Landing && previous.YawDegrees == p.YawDegrees
                    && Vector2.Distance(new Vector2(previous.Position.x, previous.Position.z), new Vector2(p.Position.x, p.Position.z)) > 1.5f) return stairs;
                if (p.Kind == StairPieceKind.Stair) stairs++;
                previous = p;
            }
            return int.MaxValue;
        }
        StairPlan Lay() => StairLayout.Build(new Vector2(0f, 0f), new Vector2(0f, 20f), SlopeWithADropEast, allowed: p => Mathf.Abs(p.x) <= 4.5f);
        StairPlan plan = Lay();
        Assert.True(plan.Reached, plan.Reason);
        Assert.True(StairsBeforeSwitchback(plan) >= StairLayout.FirstFlightStairs, $"switched back after {StairsBeforeSwitchback(plan)} stairs");
    }

    /// <summary>A second floor (6465.2,-1522.4): an end cap ridge done with a block. The hand-built
    /// top stair (6465.922, 162.355, -1520.561) yaw 22.5, top edge 163.36, the snow where a walker steps off up to about
    /// half a metre over it: a stone_floor_2x2 at (6465.156, 162.855, -1522.409), level with the top, not the crest.</summary>
    [Fact]
    public void ATopHalfAMetreUnderTheSnowGetsAFloorNotACrest()
    {
        Vector2 edge = new(6465.539f, -1521.485f), dir = StairLayout.Forward(202.5f);
        float[] ds = { 0f, 1f, 2f, 3f, 6f }, hs = { 162.9f, 163.4f, 163.8f, 164.1f, 164.1f };
        float Ground(float x, float z)
        {
            float d = Vector2.Dot(new Vector2(x, z) - edge, dir);
            if (d <= 0f) return hs[0] + 0.5f * d;
            for (int i = 1; i < ds.Length; i++) if (d <= ds[i]) return Mathf.Lerp(hs[i - 1], hs[i], (d - ds[i - 1]) / (ds[i] - ds[i - 1]));
            return hs[hs.Length - 1];
        }
        var plan = new StairPlan { Kit = StairKit.Stone.Name, Reached = true, End = new Vector3(edge.x, 163.355f, edge.y), EndHeading = 202.5f,
            LastRowKind = StairPieceKind.Stair, LastRowPieces = 0 };
        Assert.Equal(RoadStairRuns.LedgeFinish.TopFloor, RoadStairRuns.FinishOverLedge(plan, Ground, 2f, _ => true));
        StairPiece floor = Assert.Single(plan.Pieces, p => p.Prefab == "stone_floor_2x2");
        Assert.True(Vector3.Distance(floor.Position, new Vector3(6465.156f, 162.855f, -1522.409f)) < 0.02f, $"floor at {floor.Position}");
        Assert.DoesNotContain(plan.Pieces, p => p.Kind == StairPieceKind.Stair);
    }

    /// <summary>A top under a lip close enough for a crest by the ground read straight ahead, but with the ground under
    /// the down stair's low end falling away on one side: no crest out over the drop (the upper staircase overshot, at the mountain cave
    /// at -2560.6,1549.9).</summary>
    [Fact]
    public void ACrestIsNotLaidOutPastALipOverADrop()
    {
        static float Ground(float x, float z) => z > 3.2f && x > 0.3f ? 0f : 9.9f + 0.3f * Mathf.Max(z, -2f);
        LedgeFinishAndPieces Finish()
        {
            var plan = new StairPlan { Kit = StairKit.Stone.Name, Reached = true, End = new Vector3(0f, 10f, 0f), EndHeading = 0f,
                LastRowKind = StairPieceKind.Stair, LastRowPieces = 0 };
            return new(RoadStairRuns.FinishOverLedge(plan, Ground, 2f, _ => true), plan.Pieces);
        }
        LedgeFinishAndPieces with = Finish();
        Assert.NotEqual(RoadStairRuns.LedgeFinish.Crest, with.Finish);
        Assert.DoesNotContain(with.Pieces, p => p.Kind == StairPieceKind.Stair && p.Position.z > 2.5f);
    }

    /// <summary>An overshoot (the mountain cave at -2560.6,1549.9): the flight up 202.5, its last stair's top
    /// edge at 157.26, the ground probed along it (from the stair at -2559.0,1553.6): 157.76 a metre on, 158.22 at a crest's
    /// ridge, 158.4-158.55 past it. The crest's ridge stood 0.04 m over the ground and its down stair was buried whole:
    /// The upper staircase overshot. One more stair up and a floor on the ground's level instead.</summary>
    [Fact]
    public void ATopWhoseCrestWouldBeBuriedClimbsOneAndFloors()
    {
        Vector2 a = new(-2559.0f, 1553.6f), dir = StairLayout.Forward(202.5f), edge = a + dir * 3f;
        float[] hs = { 154.06f, 154.20f, 154.30f, 154.80f, 155.37f, 156.22f, 156.90f, 157.31f, 157.76f, 157.98f, 158.22f, 158.31f,
            158.42f, 158.47f, 158.52f, 158.55f, 158.56f };
        float Ground(float x, float z)
        {
            float along = Mathf.Clamp(Vector2.Dot(new Vector2(x, z) - a, dir) / 0.5f, 0f, hs.Length - 1.001f);
            int i = (int)along;
            return Mathf.Lerp(hs[i], hs[i + 1], along - i);
        }
        LedgeFinishAndPieces Finish()
        {
            var plan = new StairPlan { Kit = StairKit.Stone.Name, Reached = true, End = new Vector3(edge.x, 157.262f, edge.y), EndHeading = 202.5f,
                LastRowKind = StairPieceKind.Stair, LastRowPieces = 0 };
            return new(RoadStairRuns.FinishOverLedge(plan, Ground, 2f, _ => true), plan.Pieces);
        }
        LedgeFinishAndPieces with = Finish();
        Assert.Equal(RoadStairRuns.LedgeFinish.TopFloor, with.Finish);
        StairPiece up = Assert.Single(with.Pieces, p => p.Kind == StairPieceKind.Stair);
        Assert.Equal(22.5f, up.YawDegrees, 2);
        StairPiece floor = Assert.Single(with.Pieces, p => p.Kind == StairPieceKind.Landing);
        Assert.Equal(158.262f, floor.Position.y + StairKit.Stone.FloorTop, 2);
    }

    /// <summary>The two flights of a valley (4392-4398,6438-6444) as they were laid down to a shared floor at
    /// 32.5: the first (reversed) down into it from the west, the second up out of it to the east.</summary>
    internal static (StairPlan below, StairPlan up) SharedFloorValley()
    {
        StairKit kit = StairKit.BlackMarble;
        static StairPiece Piece(StairPieceKind kind, string prefab, float x, float y, float z, float yaw) =>
            new() { Kind = kind, Prefab = prefab, Position = new Vector3(x, y, z), YawDegrees = yaw };
        StairPiece Stair(float x, float y, float z, float yaw) => Piece(StairPieceKind.Stair, kit.Stair, x, y, z, yaw);
        StairPiece Filler(float x, float y, float z, float yaw) => Piece(StairPieceKind.Filler, "blackmarble_2x1x1", x, y, z, yaw);
        var below = new StairPlan { Kit = kit.Name, Reached = true, Reversed = true };
        below.Pieces.AddRange(new[] {
            Stair(4392.601f, 32.5f, 6440.604f, 180f), Filler(4392.601f, 33f, 6442.104f, 180f), Stair(4392.142f, 33.5f, 6442.911f, 157.5f),
            Filler(4391.568f, 34f, 6444.297f, 157.5f), Stair(4390.295f, 34.5f, 6444.759f, 112.5f), Stair(4387.682f, 35.5f, 6444.758f, 67.5f),
            Stair(4385.727f, 36.5f, 6443.452f, 45f), Stair(4383.313f, 37.5f, 6442.452f, 90f) });
        var up = new StairPlan { Kit = kit.Name, Reached = true };
        up.Pieces.AddRange(new[] {
            Filler(4394.101f, 32f, 6438.604f, 270f), Filler(4393.955f, 32f, 6438.544f, 225f), Stair(4395.015f, 32.5f, 6439.604f, 225f),
            Filler(4396.076f, 33f, 6440.665f, 225f), Stair(4396.970f, 33.5f, 6440.911f, 247.5f), Stair(4398.818f, 34.5f, 6441.676f, 247.5f),
            Stair(4400.666f, 35.5f, 6442.441f, 247.5f), Piece(StairPieceKind.Landing, kit.Floor, 4392.601f, 32f, 6438.604f, 180f) });
        return (below, up);
    }

    /// <summary>A V across the valley on its line: 31 at its bottom, rising 1.1 a metre to 35.1 where the span meets the
    /// first flight's stair at 34.5 (the floors' ends in the slope; read flat, no span was found here).</summary>
    internal static float SharedFloorValleyGround(float x, float z)
    {
        Vector2 l1 = new Vector2(4391.22f, 6444.38f), along = StairLayout.Forward(112.5f);
        return 31f + 1.1f * Mathf.Abs(Vector2.Dot(new Vector2(x, z) - l1, along) - 3.7f);
    }

    /// <summary>The second flight's top edge in <see cref="SharedFloorValley"/>: where it is laid again up to.</summary>
    internal static readonly Vector2 SharedFloorValleyTop = new(4401.59f, 6442.82f);

    /// <summary>A valley (4392-4398,6438-6444): both flights went down to a shared floor at 32.5 and doubled
    /// back. Taking off the bottom two stairs of each, the floor and its fillers, and laying floors at 34.5
    /// on from the first's stair at 34.5 toward the second's is smoother, with no 180 degree turn.</summary>
    [Fact]
    public void AValleyIsSpannedInLineAboveItsSwitchback()
    {
        StairKit kit = StairKit.BlackMarble;
        var (first, second) = SharedFloorValley();
        Assert.True(RoadStairRuns.SpanTheValley(first, second, kit, SharedFloorValleyGround, 32.5f, 2f,
            FlatRunJoinTests.Relay(SharedFloorValleyGround, SharedFloorValleyTop, kit), (_, _) => true, out StairPlan spanned, out Vector2 from));
        Assert.DoesNotContain(first.Pieces, p => p.Kind != StairPieceKind.Support && p.Position.y < 34.4f);
        Assert.DoesNotContain(spanned.Pieces, p => p.Kind != StairPieceKind.Support && p.Position.y + (p.Kind == StairPieceKind.Stair ? 0f : kit.FloorTop) < 34.4f);
        var floors = spanned.Pieces.Where(p => p.Kind == StairPieceKind.Landing).ToList();
        Assert.NotEmpty(floors);
        Assert.All(floors, f => Assert.True(f.Position.y + kit.FloorTop > 34.49f, $"floor at {f.Position}"));
        Assert.True(Vector2.Distance(from, new Vector2(4391.22f, 6444.376f)) < 0.1f, $"span from {from}");
        // Its first stair kept in the first, the 2x1 wedge of the cut turn gone; the second's pieces snapped to it.
        Assert.Contains(first.Pieces, p => p.Kind == StairPieceKind.Stair && Mathf.Abs(p.Position.y - 34.5f) < 0.01f);
        Assert.DoesNotContain(first.Pieces, p => p.Kind == StairPieceKind.Filler);
        Assert.Empty(FlatRunJoinTests.JoinFaults(first.Pieces.Concat(spanned.Pieces).ToList(), kit));
    }

    /// <summary>At the mountain cave (-2556.7,1554.2): the stairs must not stick out past the edge of the cliff, only the bottom step. A foot on a ledge
    /// with a sharp drop beside it sets off along the ledge instead; a steady slope crossed (the flight there, 0.525 a metre
    /// across, probed) is no edge.</summary>
    [Fact]
    public void AFootDoesNotStandOutPastALedgesEdgeButMayCrossASlope()
    {
        // The fourth stair (-2551.953, 159.960, 1550.053) yaw 292.5 on a plane through the probe: a slope, not an edge.
        var centre = new Vector2(-2551.953f, 1550.053f);
        Vector2 dir = StairLayout.Forward(112.5f), x = new Vector2(Mathf.Cos(292.5f * Mathf.Deg2Rad), -Mathf.Sin(292.5f * Mathf.Deg2Rad));
        float Probe(float px, float pz)
        {
            var d = new Vector2(px, pz) - centre;
            return 159.46f + 0.525f * Vector2.Dot(d, x) + 0.08f * Vector2.Dot(d, dir);
        }
        Assert.Equal(0f, StairLayout.HangsOverEdge(centre - dir * (StairLayout.PieceRun / 2f), dir, StairLayout.Right(112.5f), 1f, 159.96f, Probe));

        // A ledge rising north with a metre's drop east of x = 0.6: set off north from (0, 0), the first stairs' right side
        // stood out past the edge; with the rule the flight sets off along the ledge.
        static float Ledge(float px, float pz) => 0.5f * Mathf.Max(0f, pz) - (px > 0.6f ? 1f : 0f);
        bool Hangs(StairPlan plan) => plan.Pieces.Where(p => p.Kind == StairPieceKind.Stair).Take(StairLayout.EdgeStairs).Any(p =>
        {
            float climb = StairLayout.Wrap(p.YawDegrees + 180f);
            Vector2 d = StairLayout.Forward(climb);
            return StairLayout.HangsOverEdge(new Vector2(p.Position.x, p.Position.z) - d * (StairLayout.PieceRun / 2f), d, StairLayout.Right(climb), 1f, p.Position.y, Ledge) > 0f;
        });
        StairPlan with = StairLayout.Build(new Vector2(0f, 0f), new Vector2(-2f, 16f), Ledge, arriving: 0f);
        Assert.True(with.Reached, with.Reason);
        Assert.False(Hangs(with), "a foot stair stands out past the ledge's edge");
        // A stretch laid again without the price (RoadStairRuns.Lay, where the price closed the only way) may stand out past it.
        StairLayout.UnpricedLedge = true;
        try
        {
            StairPlan unpriced = StairLayout.Build(new Vector2(0f, 0f), new Vector2(-2f, 16f), Ledge, arriving: 0f);
            StairLayout.UnpricedLedge = false;
            Assert.True(unpriced.Reached, unpriced.Reason);
            Assert.True(Hangs(unpriced), "unpriced, no foot stair stood out anyway");
        }
        finally { StairLayout.UnpricedLedge = false; }
    }

    private sealed record LedgeFinishAndPieces(RoadStairRuns.LedgeFinish Finish, List<StairPiece> Pieces);

    /// <summary>A third floor (-9286.1,691.6): the surface past the top 0.1-0.6 m under it, so the top is improved with a 2x2.
    /// The hand-built last stair (-9286.095, 173.029, 691.647) yaw 247.5, top edge 174.03; its
    /// stone_floor_2x2 at (-9284.247, 173.529, 692.413), level with the top, slightly proud of the snow.</summary>
    [Fact]
    public void ATopAlittleOverTheSnowGetsAFloorToo()
    {
        Vector2 edge = new(-9285.171f, 692.030f), dir = StairLayout.Forward(67.5f);
        float[] ds = { 0f, 1f, 2f, 3f, 4f }, hs = { 173.4f, 173.6f, 173.8f, 173.9f, 173.9f };
        float Ground(float x, float z)
        {
            float d = Vector2.Dot(new Vector2(x, z) - edge, dir);
            if (d <= 0f) return hs[0] + 0.5f * d;
            for (int i = 1; i < ds.Length; i++) if (d <= ds[i]) return Mathf.Lerp(hs[i - 1], hs[i], (d - ds[i - 1]) / (ds[i] - ds[i - 1]));
            return hs[hs.Length - 1];
        }
        var plan = new StairPlan { Kit = StairKit.Stone.Name, Reached = true, End = new Vector3(edge.x, 174.029f, edge.y), EndHeading = 67.5f,
            LastRowKind = StairPieceKind.Stair, LastRowPieces = 0 };
        Assert.Equal(RoadStairRuns.LedgeFinish.TopFloor, RoadStairRuns.FinishOverLedge(plan, Ground, 2f, _ => true));
        StairPiece floor = Assert.Single(plan.Pieces, p => p.Prefab == "stone_floor_2x2");
        Assert.True(Vector3.Distance(floor.Position, new Vector3(-9284.247f, 173.529f, 692.413f)) < 0.02f, $"floor at {floor.Position}");
    }

    /// <summary>A staircase whose foot stands on a mound 2.3 m over its road 5 m back (-9308.1,690.3: the bottom of the stairs
    /// was not walkable, the earthworks too steep): one more stair down from the
    /// foot toward the road, cut into the mound, the sunk step under the foot becoming it; the road meets the new foot.</summary>
    private static float Mound(float x, float z) => z < -5f ? 0f : z < 0f ? 0.46f * (z + 5f) : 2.3f + 0.5f * z;

    [Fact]
    public void AFootOnAMoundIsLoweredToItsRoad()
    {
        StairPlan Lay() => StairLayout.Build(new Vector2(0f, 0f), new Vector2(0f, 20f), Mound, arriving: 0f, sunkFoot: true);
        StairPlan plan = Lay();
        Assert.True(plan.Reached, plan.Reason);
        Assert.NotNull(plan.Foot);
        int stairs = plan.Pieces.Count(p => p.Kind == StairPieceKind.Stair);
        Vector3? foot = StairLayout.LowerFoot(plan, Mound, 2f, 0f, 5f, 0.35f);
        Assert.NotNull(foot);
        Assert.Equal(1, plan.FootLowered);
        Assert.Equal(1.3f, foot!.Value.y, 2);
        Assert.Equal(-2f, foot.Value.z, 2);
        // The sunk step went; one stair down took its place, uncovered: the same number of stair pieces, one more walked.
        Assert.Equal(stairs, plan.Pieces.Count(p => p.Kind == StairPieceKind.Stair));
        Assert.Contains(plan.Pieces, p => p.Kind == StairPieceKind.Stair && Math.Abs(p.Position.y - 1.3f) < 0.01f && Math.Abs(p.Position.z + 1f) < 0.01f);
    }
}
