using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// One rule of whether a floor lands (the owner's decision of 12 Nov 2026; StairEnds.Exit, StairEnds.EdgeLands): a floor lands
/// where one of its free edges (straight on, its left or its right; never its back, the edge it comes off the stair from, nor
/// an edge joined to another walk) has BOTH corners within a step (StairLayout.StepUp) of the ground. That edge is its exit,
/// and the railing leaves it open (StairRailing). Every reader asks the one rule: the layout's (StairEnds.Lands, StairEnds.At,
/// StairLayout.AddTopFloor), the end reader's off a floor and off a bare stair (StairEnds.Read, RoadStairRuns.WayOffItsTop,
/// RoadStairRuns.NoWayOff, StairEnds.Unfinished) and the railing's open edge. Four stairs north from (0, 0) at 40 m, their top
/// at 44 m at z = 8, a floor's place past it from z = 8 to 10 and x = -1 to 1, the ground read at whole-metre vertices as the
/// game's mesh spreads it; level with the foot behind it, far below under the flight.
/// </summary>
[Collection("StairLayout")]
public sealed class FloorExitTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();
    public FloorExitTests() { RoadSpatialGrid.Clear(); StairStructures.Reset(); RoadNetworkGenerator.Reset(); }
    public void Dispose() { RoadSpatialGrid.Clear(); StairStructures.Reset(); RoadNetworkGenerator.Reset(); _scope.Dispose(); }

    /// <summary>The foot's height, over the water (a foot's landing stands above it, RoadWater.BuiltDry), and the top's.</summary>
    private const float Foot = 40f, Top = Foot + 4f;

    /// <summary>The ground round a floor's place: at its back corners (x = -1, 1 at z = 8), its far corners (at z = 10), its far
    /// edge's middle and on past it (x = 0, z >= 10), and the rest of its place (x = 0 at z = 8, 9; x = -1, 1 at z = 9 is the
    /// back corner's on that side); level with the foot behind the flight (z &lt;= 0), far below under it.</summary>
    private sealed record Place(string Name, float BackLeft, float BackRight, float FarLeft, float FarRight, float Middle, float Past, StairEnds.FloorEdge Exit)
    {
        public float Vertex(int x, int z)
        {
            if (z <= 0) return Foot;
            if (z < 8) return -50f;
            if (z >= 10 && x > -1 && x < 1) return Past;
            if (x <= -1) return z >= 10 ? FarLeft : BackLeft;
            if (x >= 1) return z >= 10 ? FarRight : BackRight;
            return Middle;
        }
        public float Ground(float x, float z) => BuriedPieces.MeshHeight(x, z, Vertex);
        public override string ToString() => Name;
    }

    private static readonly float Step = StairLayout.StepUp, Ledge = StairRailing.LedgeDrop;

    /// <summary>The floors read: the owner's one-sided top (in game at 5704.6,3590.9: level on its right, the ground falling
    /// away on its left and past its far edge), a floor level across its far edge with its sides over a dip, one within a
    /// step at its far corners' middle only (0.41 m under, 0.61 m over: a road top at -9424.6,-1413.0), and one within a step
    /// at one far corner only (0.16 m and 0.6-0.7 m round the rest: a cave's approach's top at 5046.8,-5892.6).</summary>
    private static readonly Place[] Places =
    {
        new("one-sided", Top - Ledge - 0.5f, Top - Step / 2f, Top - Ledge - 0.5f, Top - Step / 2f, Top - Step / 2f, Top - Ledge - 0.2f, StairEnds.FloorEdge.Right),
        new("level ahead", Top - 0.6f, Top - 0.6f, Top - Step / 2f, Top - Step / 2f, Top - Step / 2f, Top - Step / 2f, StairEnds.FloorEdge.Front),
        new("a step either way", Top, Top, Top + 0.41f, Top - 0.61f, Top, Top - 0.1f, StairEnds.FloorEdge.None),
        new("one corner", Top - 0.6f, Top - 0.6f, Top - 0.16f, Top - 0.7f, Top - 0.6f, Top - 0.6f, StairEnds.FloorEdge.None),
    };

    public static IEnumerable<object[]> Names() => Places.Select(p => new object[] { p.Name });

    /// <summary>Floors read by the railing alone: level within a step all round, so its far edge and both its sides land and its
    /// exit is the first of them (straight on).</summary>
    private static readonly Place[] RailingOnly =
    {
        new("level all round", Top - Step / 2f, Top - Step / 2f, Top - Step / 2f, Top - Step / 2f, Top - Step / 2f, Top - Step / 2f, StairEnds.FloorEdge.Front),
    };

    private static Place Named(string name) => Places.Concat(RailingOnly).Single(p => p.Name == name);

    private static StairPlan Flight(Place place) =>
        StairLayout.LayFlight(new Vector2(0f, 0f), Foot, new[] { 0f, 0f, 0f, 0f }, StairKit.Stone, place.Ground);

    /// <summary>The flight with a floor laid off its top by hand, as the finisher lays one (StairLayout.AddTopFloor's piece on
    /// level ground), whatever the ground there.</summary>
    private static StairPlan Floored(Place place)
    {
        float Level(float x, float z) => z <= 0f ? Foot : z < 8f ? -50f : Top;
        StairPlan level = StairLayout.LayFlight(new Vector2(0f, 0f), Foot, new[] { 0f, 0f, 0f, 0f }, StairKit.Stone, Level);
        Assert.True(StairLayout.AddTopFloor(level, Level, RoadStairRuns.Width).pieces > 0);
        StairPlan plan = Flight(place);
        plan.Pieces.Add(level.Pieces.Last(p => p.Kind == StairPieceKind.Landing));
        return plan;
    }

    private static readonly Vector2 Edge = new(0f, 8f), North = new(0f, 1f);

    /// <summary>The floor's edges by their corners: its right side (x = 1), its left (x = -1), straight on (z = 10).</summary>
    private static readonly (StairEnds.FloorEdge edge, Vector2 a, Vector2 b, Vector2 middle)[] Edges =
    {
        (StairEnds.FloorEdge.Right, new(1f, 8f), new(1f, 10f), new(1f, 9f)),
        (StairEnds.FloorEdge.Left, new(-1f, 8f), new(-1f, 10f), new(-1f, 9f)),
        (StairEnds.FloorEdge.Front, new(-1f, 10f), new(1f, 10f), new(0f, 10f)),
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void EveryReaderOfDoesTheFloorLandAsksTheOneRule(string name)
    {
        Place place = Named(name);
        bool lands = place.Exit != StairEnds.FloorEdge.None;
        var drops = StairEnds.FloorDrops(Edge, North, Top, RoadStairRuns.Width, place.Ground);
        Assert.Equal(place.Exit, StairEnds.Exit(drops));
        // Its exit the one edge of it with both corners within a step (none: it does not land).
        foreach (var (edge, a, b, _) in Edges)
            Assert.True((edge == place.Exit) == StairEnds.EdgeLands(Top - place.Ground(a.x, a.y), Top - place.Ground(b.x, b.y)), $"{name}: its {edge} edge");

        // The layout's readers: its test, the rule's case, the floor it lays.
        Assert.Equal(lands, StairEnds.Lands(Edge, North, Top, RoadStairRuns.Width, place.Ground));
        Assert.Equal(lands, StairEnds.At(Edge, North, Top, RoadStairRuns.Width, place.Ground) == StairEnds.Case.Floor);
        Assert.Equal(lands, StairLayout.AddTopFloor(Flight(place), place.Ground, RoadStairRuns.Width).pieces > 0);
        // Off the bare stair: the ground a floor off it would stand on.
        Assert.Equal(lands, StairEnds.StairStepsOff(Edge, North, Top, RoadStairRuns.Width, Step, place.Ground, out _));

        // The end reader's, of the floor laid: its way off, the audit's verdict and finish, the road's refusal.
        StairPlan plan = Floored(place);
        Assert.Equal(lands, RoadStairRuns.WayOffItsTop(plan, place.Ground));
        StairRun run = StairRun.FromPlan(new Vector2(0f, 0f), new Vector2(0f, 10f), RoadStairRuns.Width, plan, StairKit.Stone);
        StairEnds.End top = StairEnds.Read(run, place.Ground).Single(e => e.Top);
        Assert.True(lands == (top.Verdict == StairEnds.Verdict.Ok), $"{name}: {StairEnds.Line(top)}");
        Assert.True(!lands || StairEnds.Unfinished(top) == null, $"{name}: {StairEnds.Line(top)} UNFINISHED: {StairEnds.Unfinished(top)}");
        Assert.Equal(lands, RoadStairRuns.NoWayOff(new[] { run }, _ => place.Ground, out _) == null);
    }

    /// <summary>The railing of a floor the walk ends on (its end recorded at the last stair's top, so neither the road nor a
    /// place's landing takes the walk over from it): exactly its exit left open, every other free edge railed, the one exit
    /// StairEnds.Exit chooses where several edges land (level all round: straight on).</summary>
    [Theory]
    [InlineData("one-sided")]
    [InlineData("level ahead")]
    [InlineData("level all round")]
    public void TheRailingLeavesExactlyTheFloorsExitOpen(string name)
    {
        Place place = Named(name);
        StairPlan plan = Floored(place);
        var run = StairRun.FromPlan(new Vector2(0f, 0f), new Vector2(0f, 7f), RoadStairRuns.Width, plan, StairKit.Stone);
        run.KeepsLocationRocks = true;
        List<StructurePiece> rails = StairRailingTests.Railing(run, StairKit.Stone, place.Vertex, natural: place.Ground)
            .Where(p => p.Prefab is StairRailing.LevelRailPrefab or StairRailing.ShortRailPrefab).ToList();
        bool RailedAt(Vector2 at) => rails.Any(r => Vector2.Distance(new Vector2(r.Position.x, r.Position.z), at) < 0.05f);
        foreach (var (edge, _, _, middle) in Edges)
            Assert.True(RailedAt(middle) == (edge != place.Exit), $"{name}: its {edge} edge {(RailedAt(middle) ? "railed" : "open")}, its exit {place.Exit}");
    }

    /// <summary>The owner's one-sided top (in game at 5704.6,3590.9), as its approach finishes it: its floor laid, level with the
    /// top; its exit is its level right side, which the end reader steps off (its far edge and its left side over the drop).</summary>
    [Fact]
    public void TheOneSidedTopFloorsExitIsItsLevelSide()
    {
        Place place = Named("one-sided");
        StairPlan plan = Flight(place);
        Assert.Equal(StairLayout.TopFinish.TopFloor, RoadStairRuns.FinishOverLedge(plan, place.Ground, RoadStairRuns.Width, _ => true));
        StairPiece floor = Assert.Single(plan.Pieces, p => p.Kind == StairPieceKind.Landing);
        var (back, heading, across, _, height) = StairLayout.Footprint(floor);
        Assert.Equal(StairEnds.FloorEdge.Right, StairEnds.Exit(StairEnds.FloorDrops(back, StairLayout.Forward(heading), height, across, place.Ground)));
        StairEnds.End top = RoadStairRuns.TopEnd(plan, place.Ground)!;
        Assert.True(top.Verdict == StairEnds.Verdict.Ok && top.FloorsPast == 1 && top.StairsPast == 0, StairEnds.Line(top));
    }

    /// <summary>A floor level straight on, its sides over a dip more than a step under it: its exit is its far edge.</summary>
    [Fact]
    public void AFloorLevelStraightOnHasItsFarEdgeForItsExit()
    {
        Place place = Named("level ahead");
        StairPlan plan = Flight(place);
        Assert.Equal(StairLayout.TopFinish.TopFloor, RoadStairRuns.FinishOverLedge(plan, place.Ground, RoadStairRuns.Width, _ => true));
        StairPiece floor = Assert.Single(plan.Pieces, p => p.Kind == StairPieceKind.Landing);
        var (back, heading, across, _, height) = StairLayout.Footprint(floor);
        Assert.Equal(StairEnds.FloorEdge.Front, StairEnds.Exit(StairEnds.FloorDrops(back, StairLayout.Forward(heading), height, across, place.Ground)));
        Assert.True(RoadStairRuns.WayOffItsTop(plan, place.Ground));
    }

    /// <summary>The ground of a case as the game's world (its heights; Meadows), for production's own checks of what a top's
    /// finish adds (RoadStairRuns.TopFits: water, places, roads, other staircases).</summary>
    private sealed class World : WorldGenerator
    {
        private readonly Func<float, float, float> _ground;
        public World(Func<float, float, float> ground) { _ground = ground; }
        public override float GetHeight(float wx, float wy) => _ground(wx, wy);
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Meadows;
        public override int GetSeed() => 109;
    }

    /// <summary>Production's fit of a top's finish (RoadStairRuns.TopFits, as a road staircase's Lay hands it to its top's
    /// finish): what the finish adds past the flight's pieces, on a road climbing the flight and carrying on north past its top.</summary>
    private Func<StairPlan, bool> ProductionFits(StairPlan plan, Func<float, float, float> ground)
    {
        var world = new World(ground);
        _scope.WithWorld(world);
        int added = plan.Pieces.Count;
        var path = new List<Vector2> { new(0f, -8f), new(0f, 0f), new(0f, 8f), new(0f, 24f) };
        return p => RoadStairRuns.TopFits(p, added, path, 1, 2, world, new List<RoadStairSpan>(), null);
    }

    /// <summary>The owner's one-sided top through production's fit of what its finish adds (<see cref="ProductionFits"/>):
    /// the same floor, its exit its level right side.</summary>
    [Fact]
    public void TheOneSidedTopFloorsExitIsItsLevelSideThroughProductionsFits()
    {
        Place place = Named("one-sided");
        StairPlan plan = Flight(place);
        Assert.Equal(StairLayout.TopFinish.TopFloor, RoadStairRuns.FinishOverLedge(plan, place.Ground, RoadStairRuns.Width, ProductionFits(plan, place.Ground)));
        StairPiece floor = Assert.Single(plan.Pieces, p => p.Kind == StairPieceKind.Landing);
        var (back, heading, across, _, height) = StairLayout.Footprint(floor);
        Assert.Equal(StairEnds.FloorEdge.Right, StairEnds.Exit(StairEnds.FloorDrops(back, StairLayout.Forward(heading), height, across, place.Ground)));
        Assert.True(RoadStairRuns.WayOffItsTop(plan, place.Ground));

        // Production's fit does judge it: another road across the floor's place refuses the floor there.
        StairPlan crossed = Flight(place);
        Func<StairPlan, bool> fits = ProductionFits(crossed, place.Ground);
        var other = new List<Vector2>();
        for (float x = -30f; x <= 30.01f; x += 1f) other.Add(new Vector2(x, 9f));
        Assert.True(RoadSpatialGrid.AddRoadPath(other, 4f, new World(place.Ground)));
        RoadSpatialGrid.FinalizeRoadNetwork();
        Assert.NotEqual(StairLayout.TopFinish.TopFloor, RoadStairRuns.FinishOverLedge(crossed, place.Ground, RoadStairRuns.Width, fits));
    }

    /// <summary>The floor then a stair down, its stair its exit, through production's fit (<see cref="ProductionFits"/>): the
    /// top over ground within a step for its first metre and 0.6 m under it on from there (as StairLandingLipTests' falling
    /// landing, over the water here); and with a bank over the floor's back left corner, up to a step over its walk the floor
    /// then the stair down is laid, 0.3 m over it the floor is refused (RoadStairRuns.SurfaceUnderGroundAllowed) and, no other
    /// finish fitting, the bare top refused.</summary>
    [Theory]
    [InlineData(0f, StairLayout.TopFinish.FloorThenDown)]
    [InlineData(0.15f, StairLayout.TopFinish.FloorThenDown)]
    [InlineData(0.3f, StairLayout.TopFinish.None)]
    public void AFloorThenAStairDownThroughProductionsFits(float bank, StairLayout.TopFinish expected)
    {
        var falling = new Place("falling", Top - 0.75f * Step, Top - 0.75f * Step, Top - 3f * Step, Top - 3f * Step, Top - 0.75f * Step, Top - 3f * Step, StairEnds.FloorEdge.None);
        float Ground(float x, float z) => bank > 0f && x <= -0.95f && z >= 7.9f && z <= 8.1f ? Top + bank : falling.Ground(x, z);
        StairPlan plan = StairLayout.LayFlight(new Vector2(0f, 0f), Foot, new[] { 0f, 0f, 0f, 0f }, StairKit.Stone, Ground);
        int pieces = plan.Pieces.Count;
        var (finish, bare) = RoadStairRuns.FinishApproachTop(plan, Ground, ProductionFits(plan, Ground), refuseBare: true);
        Assert.Equal(expected, finish);
        if (expected == StairLayout.TopFinish.None) { Assert.True(bare); Assert.Equal(pieces, plan.Pieces.Count); return; }
        StairEnds.End top = RoadStairRuns.TopEnd(plan, Ground)!;
        Assert.True(top.Verdict == StairEnds.Verdict.Ok && top.FloorsPast == 1 && top.StairsPast == 1 && StairEnds.Unfinished(top) == null, StairEnds.Line(top));
    }

    /// <summary>Four stairs north from a foot at (0, 0) <paramref name="foot"/> high, a floor laid off their top on level ground
    /// there, and the ground at the foot from <paramref name="vertex"/> (z &lt;= 0; far below under the flight): the staircase
    /// as its road would store it, both ends read by the one end reader.</summary>
    private static StairRun FootCase(float foot, Func<int, int, float> vertex, out Func<float, float, float> ground)
    {
        float top = foot + 4f;
        float V(int x, int z) => z <= 0 ? vertex(x, z) : z < 8 ? -50f : top;
        Func<float, float, float> g = (x, z) => BuriedPieces.MeshHeight(x, z, V);
        StairPlan plan = StairLayout.LayFlight(new Vector2(0f, 0f), foot, new[] { 0f, 0f, 0f, 0f }, StairKit.Stone, g);
        Assert.True(StairLayout.AddTopFloor(plan, g, RoadStairRuns.Width).pieces > 0);
        ground = g;
        return StairRun.FromPlan(new Vector2(0f, 0f), new Vector2(0f, 10f), RoadStairRuns.Width, plan, StairKit.Stone);
    }

    /// <summary>A foot on a shoreline: a floor off its bare stair would land on its right side (x = -1 here, walking out south),
    /// level with the foot at 31 m, built dry (RoadWater.BuiltDry); its other far corner 2 m under, at 29 m, under the water
    /// line. Its exit is its right side, judged by that side's own ground: dry, a way off, its road kept (ConnectionRefusal).
    /// Judged by the far corner the front edge read worst (29 m), it was wet: a gap, its road refused. With the right side
    /// itself under the water line (30 m), it is wet: no way off.</summary>
    [Theory]
    [InlineData(31f, true)]
    [InlineData(30f, false)]
    public void AFootOnAShorelineIsJudgedDryByItsExitsOwnGround(float side, bool dry)
    {
        float foot = side;
        StairRun run = FootCase(foot, (x, z) => x >= 1 && z <= -2 ? 29f : side, out var ground);
        StairEnds.End end = StairEnds.Read(run, ground).Single(e => !e.Top);
        Assert.True((end.Verdict == StairEnds.Verdict.Ok) == dry, StairEnds.Line(end));
        Assert.True(StairEnds.Read(run, ground).Single(e => e.Top).Verdict == StairEnds.Verdict.Ok);
        string? refused = RoadStairRuns.ConnectionRefusal(new RoadStairSpan[0], new List<RoadSpatialGrid.PlannedPath>(), out _, new List<StairRun> { run }, _ => ground);
        Assert.Equal(dry ? null : RoadStairRuns.NoWayOffItsEnd, refused);
    }

    /// <summary>A foot with a way off it but not finished as its rule chose refuses its road as such (RoadStairRuns.FootUnfinished,
    /// the owner's of 12 Nov: refuse and reroute, not left to the audit), not as a gap: walking out south at 40 m, a floor off its
    /// bare stair lands on its right side with the ground up to a rise over the walk (the band at a stair's low edge: a way
    /// off), its far right corner 0.5 m over it and its far left 0.4 m under: no edge within a step, the rule's stair down
    /// (Reverse) and none laid. The control: level ground round the foot, the rule's floor, finished as its bare stair.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFootWithAWayOffButUnfinishedRefusesItsRoadAsUnfinished(bool level)
    {
        const float foot = 40f;
        StairRun run = FootCase(foot, (x, z) => level || z > -2 ? foot : x <= -1 ? foot + 0.5f : x >= 1 ? foot - 0.4f : foot, out var ground);
        StairEnds.End end = StairEnds.Read(run, ground).Single(e => !e.Top);
        Assert.True(end.Verdict == StairEnds.Verdict.Ok, StairEnds.Line(end));
        Assert.Equal(level ? StairEnds.Case.Floor : StairEnds.Case.Reverse, end.Rule);
        Assert.Equal(level, StairEnds.Unfinished(end) == null);
        string? refused = RoadStairRuns.ConnectionRefusal(new RoadStairSpan[0], new List<RoadSpatialGrid.PlannedPath>(), out _, new List<StairRun> { run }, _ => ground);
        Assert.Equal(level ? null : RoadStairRuns.FootUnfinished, refused);
    }

    /// <summary>The railing's exit stands dry (RoadWater.BuiltDry, as the end reader asks a foot's): a top floor at 30.6 m on a
    /// shore, the ground within a step of it all round, its far edge and left side 0.15 m under it (30.45 m, under the line a
    /// road or a staircase is built dry at), its right side 0.05 m under it (30.55 m, dry). Its exit is its right side, not
    /// straight on: open there, railed straight on and on its left.</summary>
    [Fact]
    public void TheRailingLeavesTheFloorsDryExitOpenOnAShore()
    {
        const float top = 30.6f;
        float V(int x, int z) => z <= 0 ? top - 4f : z < 8 ? -50f : x >= 1 ? top - 0.05f : top - 0.15f;
        float G(float x, float z) => BuriedPieces.MeshHeight(x, z, V);
        StairPlan plan = StairLayout.LayFlight(new Vector2(0f, 0f), top - 4f, new[] { 0f, 0f, 0f, 0f }, StairKit.Stone, G);
        Assert.True(StairLayout.AddTopFloor(plan, G, RoadStairRuns.Width).pieces > 0);
        var run = StairRun.FromPlan(new Vector2(0f, 0f), new Vector2(0f, 7f), RoadStairRuns.Width, plan, StairKit.Stone);
        run.KeepsLocationRocks = true;
        List<StructurePiece> rails = StairRailingTests.Railing(run, StairKit.Stone, V, natural: G)
            .Where(p => p.Prefab is StairRailing.LevelRailPrefab or StairRailing.ShortRailPrefab).ToList();
        bool RailedAt(Vector2 at) => rails.Any(r => Vector2.Distance(new Vector2(r.Position.x, r.Position.z), at) < 0.05f);
        foreach (var (edge, _, _, middle) in Edges)
            Assert.True(RailedAt(middle) == (edge != StairEnds.FloorEdge.Right), $"its {edge} edge {(RailedAt(middle) ? "railed" : "open")}");
    }
}
