using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Valheim.Testing.Doubles;
using Xunit;

namespace ProceduralRoads.Tests;

/// <summary>
/// What each builder does when the game's support rule, read over the structure as it is built (SupportGraph.Settle through
/// StairSupport.Holding), breaks a piece it needs: it takes its remedies, and where none holds it, it refuses the structure,
/// laying nothing; never a piece built to fall and said in the log (docs/OWNERS.md, a structure's final support).
/// </summary>
public sealed class SupportResponseTests : IDisposable
{
    private readonly ValheimWorldScope _scope = new();

    // The fixtures here are kits' structure cases whose water only raises a deck (standing it just under the deck, over ground
    // above the sea) or lies under a valley deepened past the sea: none is water a ship is meant to sail, so no lane off a
    // river here (BridgeLayout.DeepWaterLanes; ShipGapTests has the lanes, river or not).
    public SupportResponseTests()
    {
        BridgeLayout.DeepWaterLanes = false;
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        PlacedLocations.Reset();
    }

    public void Dispose()
    {
        BridgeLayout.DeepWaterLanes = true;
        RoadLocationProtection.Reset();
        PlacedLocations.Reset();
        RoadNetworkGenerator.Reset();
        RoadSpatialGrid.Clear();
        _scope.Dispose();
    }

    /// <summary>Level ground at <see cref="Height"/>.</summary>
    private sealed class LevelWorld : WorldGenerator
    {
        public float Height = 10f;
        public override float GetHeight(float wx, float wy) => Height;
        public override Heightmap.Biome GetBiome(float wx, float wy, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Heightmap.Biome.Mountain;
        public override int GetSeed() => 81;
    }

    /// <summary>One stone stair climbing +z, its back edge's middle at the origin, its underside at <paramref name="underside"/>.</summary>
    private static StairRun OneStair(float underside)
    {
        var run = new StairRun { From = new Vector2(0f, 0f), To = new Vector2(0f, 2f), Width = RoadStairRuns.Width, Kit = StairKit.Stone };
        run.Pieces.Add(new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = StairKit.Stone.Stair, Position = new Vector3(0f, underside, 1f), YawDegrees = 180f });
        return run;
    }

    private static string Say(IEnumerable<ViaductSupport.Held> held) => string.Join(", ", held.OrderBy(h => h.Share).Take(4)
        .Select(h => FormattableString.Invariant($"{h.Piece.Prefab} at {h.Piece.Position} {h.Support:F0} ({h.Share:F2})")));

    /// <summary>
    /// A stair whose layout's ground had it lying on the slope (held by nothing), the finished ground 3 m under it (a road's cut
    /// the search did not see): held as its layout's ground chose, nothing holds it and the rule breaks it. The kit holds it
    /// again as chosen on the finished ground it stands on: walled down to it, and every piece stands.
    /// </summary>
    [Fact]
    public void AStairTheRuleBreaksAsChosenIsHeldAgainOnTheGroundItStandsOn()
    {
        StairRun run = OneStair(20f);
        Func<float, float, float> planning = (x, z) => 20f;
        Func<int, int, float> finished = (x, z) => 17f;
        StairSupport.Walk w = StairSupport.WalkOf(run.Pieces[0], StairKit.Stone)!.Value;
        Assert.Equal(StairSupport.Style.Ground, StairSupport.Choose(StairKit.Stone, w, planning).Style);

        var shortfall = new StairSupport.Shortfall();
        List<StructurePiece> laid = StairKit.Stone.Dress(run, planning, finished, false, shortfall);
        Assert.Contains(laid, p => p.Kind == StructurePieceKind.StairSupport && p.Prefab.StartsWith("stone_wall_"));
        List<ViaductSupport.Held> held = StairSupport.Holding(laid, finished);
        Assert.True(held.All(h => !h.Falls), Say(held));
        Assert.Empty(shortfall.Falling);
    }

    /// <summary>
    /// A stair 30 m over level ground with nothing within reach to hold it (an older layout's; the search never lays one): the
    /// rule breaks it however it is held, so the staircase is refused at its first build: nothing laid, nothing saved, and
    /// said where.
    /// </summary>
    [Fact]
    public void AStaircaseTheRuleBreaksHoweverHeldIsRefusedNotBuiltToFall()
    {
        var world = new LevelWorld { Height = 10f };
        _scope.WithWorld(world);
        StairRun run = OneStair(40f);
        Assert.Empty(StairStructures.Build(run, world));
        Assert.Null(BuiltStairs.Of(run));

        var shortfall = new StairSupport.Shortfall();
        Assert.Empty(StairKit.Stone.Dress(run, (x, z) => 10f, (x, z) => 10f, false, shortfall));
        Assert.Equal(new Vector3(0f, 40f, 1f), Assert.Single(shortfall.Falling));
    }

    /// <summary>
    /// A staircase not stored yet is read by its kit's support response before its road is (StairStructures.Refusal, decision
    /// 6p): a stair 30 m over level ground that nothing holds would be refused, and is said so with where; a stair lying on
    /// the ground is laid. The ground it reads is the finished ground as its road would be stored
    /// (RoadTerrainModifier.AsStored, as RoadNetworkGenerator.StairsRefusal reads it): the same stair with its road's
    /// staircase cuts taking the ground 30 m down under it (cuts the road has not stored yet) would be refused too.
    /// </summary>
    [Fact]
    public void AStaircaseIsReadBeforeItsRoadIsStoredOnTheGroundItsCutsWillLeave()
    {
        var world = new LevelWorld { Height = 10f };
        _scope.WithWorld(world);
        Assert.Equal(StairStructures.RefusedBySupport, StairStructures.Refusal(OneStair(40f), world, out Vector2 at));
        Assert.Equal(new Vector2(0f, 1f), at);

        StairRun lying = OneStair(10f);
        Assert.Null(StairStructures.Refusal(lying, world, out _));
        var cuts = new List<RoadSpatialGrid.RoadPoint>();
        for (float z = -2f; z <= 4f + 1e-3f; z += 1f)
            for (float x = -3f; x <= 3f + 1e-3f; x += 1f) cuts.Add(RoadSpatialGrid.CutPoint(new Vector3(x, -20f, z), StairLayout.CutWidth));
        using (RoadTerrainModifier.AsStored(cuts, new List<(RoadSpatialGrid.RoadPoint, float)>()))
            Assert.Equal(StairStructures.RefusedBySupport, StairStructures.Refusal(lying, world, out _));
    }

    /// <summary>
    /// A staircase read before its road is stored reads the ground its own walks leave once it is (StairStructures.CutFloor,
    /// through RoadTerrainModifier.AsStored): a cut under a lower stair sunk 2 m into the ground, whose batter reaches under the higher stair beside
    /// it is floored under that higher walk as once the staircase is stored, so the ground and the verdict its kit reads
    /// before (RoadNetworkGenerator.StairsRefusal) are the ground and verdict it reads after it is stored as generation stores
    /// it (JoinNetwork: its cuts, then StairStructures.Add). Before, the read before storing saw only the staircases stored
    /// already, and dug the batter out under the higher stair.
    /// </summary>
    [Fact]
    public void AStaircaseReadBeforeItsRoadIsStoredReadsItsOwnWalksOverItsCuts()
    {
        var world = new LevelWorld { Height = 10f };
        _scope.WithWorld(world);
        var run = new StairRun { From = new Vector2(0f, 0f), To = new Vector2(0f, 4f), Width = RoadStairRuns.Width, Kit = StairKit.Stone };
        run.Pieces.Add(new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = StairKit.Stone.Stair, Position = new Vector3(0f, 8f, 1f), YawDegrees = 180f });
        run.Pieces.Add(new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = StairKit.Stone.Stair, Position = new Vector3(0f, 10.5f, 3f), YawDegrees = 180f });
        var cuts = new List<Vector3> { new(0f, 7f, 1f) };
        var written = new RoadNetworkGenerator.ManualAppend();
        written.Runs.Add((run, new StairPlan(), StairKit.Stone));
        written.StairCuts.AddRange(cuts);

        var read = new List<(Dictionary<(int, int), float> ground, string? verdict)>();
        var dressing = StairStructures.Dressing;
        StairStructures.Dressing = (r, w, finished, shortfall, shared) =>
        {
            var ground = new Dictionary<(int, int), float>();
            for (int x = -3; x <= 3; x++) for (int z = -3; z <= 7; z++) ground[(x, z)] = finished(x, z);
            List<StructurePiece> laid = dressing(r, w, finished, shortfall, shared);
            read.Add((ground, shortfall.Falling.Count > 0 ? StairStructures.RefusedBySupport : null));
            return laid;
        };
        try
        {
            string? before = RoadNetworkGenerator.StairsRefusal(written, out _);
            RoadSpatialGrid.CommitCuts(cuts, StairLayout.CutWidth);
            StairStructures.Add(run);
            string? after = StairStructures.Refusal(run, world, out _);
            Assert.Equal(2, read.Count);
            Assert.Equal(after, before);
            Assert.Equal(read[1].verdict, read[0].verdict);
            var differ = read[1].ground.Where(kv => Mathf.Abs(kv.Value - read[0].ground[kv.Key]) > 1e-4f)
                .Select(kv => FormattableString.Invariant($"({kv.Key.Item1}, {kv.Key.Item2}) {read[0].ground[kv.Key]:F2} before, {kv.Value:F2} stored")).ToList();
            Assert.True(differ.Count == 0, string.Join("; ", differ));
            // The cut reaches under the higher stair: floored there, not dug to its batter.
            Assert.True(read[1].ground[(0, 3)] > 9.9f, $"ground under the higher stair {read[1].ground[(0, 3)]:F2}");
            Assert.True(read[1].ground[(0, 1)] < 9.9f, $"ground under the lower stair {read[1].ground[(0, 1)]:F2}");
        }
        finally
        {
            StairStructures.Dressing = dressing;
            StairStructures.Reset();
        }
    }

    /// <summary>
    /// A marked road's staircase is settled and built on the ground its check read (RoadNetworkGenerator.AppendManualRoad,
    /// StairStructures.Settle and Build, inside RoadTerrainModifier.AsStored with its walks): the overlapping-cut staircase of
    /// <see cref="AStaircaseReadBeforeItsRoadIsStoredReadsItsOwnWalksOverItsCuts"/>, appended with a road, reads at its build
    /// the ground its check read before the road was stored (RoadNetworkGenerator.StairsRefusal), its batter floored under
    /// the higher stair, and the ground it is saved on is that ground. Before, the append settled and built it before its
    /// walks were the network's, on the batter dug out under the higher stair.
    /// </summary>
    [Fact]
    public void AMarkedRoadsStaircaseIsBuiltOnTheGroundItsCheckRead()
    {
        var world = new LevelWorld { Height = 10f };
        _scope.WithWorld(world).WithZdos().WithZoneSystem().WithScene().AsServer();
        RoadNetworkGenerator.MarkLocationsReady();
        var far = new List<Vector2> { new(100f, 100f), new(140f, 100f) };
        RoadSpatialGrid.Commit(new RoadSpatialGrid.PlannedPath(far, far.ConvertAll(_ => 10f), far.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 40f, false));
        RoadSpatialGrid.FinalizeRoadNetwork(); RoadNetworkGenerator.MarkRoadsLoadedFromZDO();
        int version = RoadSpatialGrid.RoadNetworkVersion;
        var run = new StairRun { From = new Vector2(0f, 0f), To = new Vector2(0f, 4f), Width = RoadStairRuns.Width, Kit = StairKit.Stone };
        run.Pieces.Add(new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = StairKit.Stone.Stair, Position = new Vector3(0f, 8f, 1f), YawDegrees = 180f });
        run.Pieces.Add(new StructurePiece { Kind = StructurePieceKind.StairStep, Prefab = StairKit.Stone.Stair, Position = new Vector3(0f, 10.5f, 3f), YawDegrees = 180f });
        var road = new List<Vector2> { new(0f, -12f), new(0f, -8f), new(0f, -4f) };
        var piece = new RoadSpatialGrid.PlannedPath(road, road.ConvertAll(_ => 10f), road.ConvertAll(_ => default(RoadPointDebugInfo)), 4f, 8f, false);
        var append = new RoadNetworkGenerator.ManualAppend();
        append.WithFills.Add(piece);
        append.Runs.Add((run, new StairPlan(), StairKit.Stone));
        append.StairCuts.Add(new Vector3(0f, 7f, 1f));

        var read = new List<Dictionary<(int, int), float>>();
        var dressing = StairStructures.Dressing;
        StairStructures.Dressing = (r, w, finished, shortfall, shared) =>
        {
            var ground = new Dictionary<(int, int), float>();
            for (int x = -3; x <= 3; x++) for (int z = -3; z <= 7; z++) ground[(x, z)] = finished(x, z);
            read.Add(ground);
            return dressing(r, w, finished, shortfall, shared);
        };
        try
        {
            RoadNetworkGenerator.StairsRefusal(append, out _);
            RoadNetworkGenerator.AppendManualRoad(new[] { piece }, new List<RoadCrossing>(), Array.Empty<RoadStairSpan>(), road[0], version, append);
            Assert.Equal(2, read.Count);
            var differ = read[1].Where(kv => Mathf.Abs(kv.Value - read[0][kv.Key]) > 1e-4f)
                .Select(kv => FormattableString.Invariant($"({kv.Key.Item1}, {kv.Key.Item2}) {read[0][kv.Key]:F2} checked, {kv.Value:F2} built")).ToList();
            Assert.True(differ.Count == 0, string.Join("; ", differ));
            Assert.True(read[1][(0, 3)] > 9.9f, $"ground under the higher stair at its build {read[1][(0, 3)]:F2}");
            // Saved on that ground.
            Assert.All(run.Ground.Where(pin => read[0].ContainsKey((pin.X, pin.Z))),
                pin => Assert.True(Mathf.Abs(pin.Height - read[0][(pin.X, pin.Z)]) < 1e-3f, FormattableString.Invariant($"saved ({pin.X}, {pin.Z}) {pin.Height:F2}")));
        }
        finally
        {
            StairStructures.Dressing = dressing;
            StairStructures.Reset();
        }
    }

    /// <summary>
    /// Ground not known under a staircase read before its road is stored is never a verdict (StructureGround.FirstOnUnknownGround)
    /// and a bug under the preload-list rule: its build would lay nothing (StairStructures.Dresser, StructureGround.NotLaid), so
    /// the read before its road is stored refuses the road for it (StairStructures.UnknownGroundRefusal, at the piece on it),
    /// and the build lays nothing, said as an error. A stair 30 m over level ground that the rule would refuse, with a location
    /// whose ground is not published under its step. On the parent the read refused nothing and the staircase waited.
    /// </summary>
    [Fact]
    public void AStaircaseOnGroundNotKnownRefusesItsRoadAndLaysNothing()
    {
        var world = new LevelWorld { Height = 10f };
        _scope.WithWorld(world);
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 1f), 0.4f) });
        StairRun run = OneStair(40f);
        string? refused = StairStructures.Refusal(run, world, out Vector2 at);
        Assert.True(refused != null && refused.StartsWith(StairStructures.UnknownGroundRefusal), refused ?? "not refused");
        Assert.Contains("is not known", refused);
        Assert.Equal(new Vector2(0f, 1f), at);

        var lines = new List<string>();
        BepInEx.Logging.ManualLogSource.Captured = lines;
        try { Assert.Empty(StairStructures.Build(run, world)); }
        finally { BepInEx.Logging.ManualLogSource.Captured = null; }
        Assert.Single(lines, l => l.Contains("not laid") && l.Contains("is not known"));
    }

    /// <summary>
    /// A Dvergr viaduct laid over a valley 14 m deep, then the valley floor 50 m under its deck (a stored viaduct on ground
    /// deeper than the pier ceiling it was admitted under): its piers are carried down to the floor, and the rule breaks the
    /// deck and pier tops with iron walked and every ornament left out. The viaduct is refused, whole and ruined: no piece
    /// laid, and what falls said.
    /// </summary>
    [Fact]
    public void AViaductWhoseDeckTheRuleBreaksWithEveryRemedyTakenIsRefused()
    {
        var world = new DvergrViaductTests.MistValley();
        _scope.WithWorld(world);
        RoadCrossing c = DvergrViaduct.ValleyCrossing(new Vector2(-24f, 0f), new Vector2(24f, 0f), world, out string why)!;
        Assert.True(c != null, why);
        Assert.NotEmpty(DvergrViaduct.Solve(c!, world, 1, false, out DvergrViaduct.HoldReport known));
        Assert.Empty(known.Falling);

        world.Depth = 50f;
        world.Face = 4f;
        foreach (bool ruin in new[] { false, true })
        {
            List<StructurePiece> laid = DvergrViaduct.Solve(c!, world, 1, ruin, out DvergrViaduct.HoldReport report);
            Assert.Empty(laid);
            Assert.NotEmpty(report.Falling);
            Assert.All(report.Falling, p => Assert.False(DvergrViaduct.IsOrnament(p)));
        }
    }

    /// <summary>
    /// Ground not known under a viaduct has one reader (docs/OWNERS.md, ground readers): the viaduct's own solve reads the
    /// finished ground's vertices exactly (StructureGround.Vertices) and asks StructureGround.FirstOnUnknownGround of them,
    /// as BridgeStructures.Solve does. The viaduct the rule refuses (its valley 50 m deep under the deck), with a location's
    /// ground not read yet one vertex past the vertices its pieces stand on: every vertex its pieces stand on is known, so
    /// it is read, refused, and nothing is laid, nor does it wait. On the parent the viaduct read its vertices through the
    /// mesh between them, a known vertex beside the unknown one read as not known, and returned its pieces unread; the
    /// bridge's solve, reading the vertices exactly, found them all known and laid the refused viaduct.
    /// </summary>
    [Fact]
    public void AViaductTheRuleRefusesBesideGroundNotKnownIsRefusedNotLaid()
    {
        var world = new DvergrViaductTests.MistValley();
        _scope.WithWorld(world);
        RoadCrossing c = DvergrViaduct.ValleyCrossing(new Vector2(-24f, 0f), new Vector2(24f, 0f), world, out string why)!;
        Assert.True(c != null, why);
        // The last row of vertices along +z that its pieces stand on (its deck's edge, as wide however deep the valley):
        // the vertex one further, over the middle of that row, is in a location whose ground is not read yet.
        List<StructurePiece> laid = DvergrViaduct.SolveComplete(c!, world, 1);
        int z = laid.Max(p => Mathf.CeilToInt(p.Position.z + StructureGround.HeldReach)) + 1;
        int x = Mathf.RoundToInt(laid.Where(p => Mathf.CeilToInt(p.Position.z + StructureGround.HeldReach) + 1 == z)
            .OrderBy(p => Mathf.Abs(p.Position.x)).First().Position.x);
        // Stored as a viaduct (its kind saved with the network), then the valley under it 50 m deep.
        c!.Kit = BridgeStructures.Kit.DvergrViaduct;
        world.Depth = 50f;
        world.Face = 4f;
        Assert.Equal(BridgeStructures.Kit.DvergrViaduct, BridgeStructures.KitFor(c, world));
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(x, z), 0.4f) });
        Func<int, int, float> vertices = StructureGround.Vertices(world);
        Assert.True(float.IsNaN(vertices(x, z)));
        Assert.False(float.IsNaN(vertices(x, z - 1)));

        Assert.Empty(BridgeStructures.Solve(c, world, 1));
        foreach (bool ruin in new[] { false, true })
        {
            Assert.Empty(DvergrViaduct.Solve(c, world, 1, ruin, out DvergrViaduct.HoldReport report));
            Assert.NotEmpty(report.Falling);
        }
    }

    /// <summary>
    /// A viaduct the support rule refuses is a deck built in no kit (BridgeStructures.BuiltInNoKit asks the viaduct's own
    /// verdict, DvergrViaduct.Refuses), so no road is stored up to it: the one list of a road's refusals
    /// (RoadNetworkGenerator.BuiltRoadRefusal, through WithNoKit) refuses a road over it for it, as for a bridge no kit
    /// stands in. The same viaduct over its 14 m valley stands and refuses nothing. On the parent the viaduct kit was its
    /// kit unconditionally (BridgeStructures.KitAmong), so the road was stored to the rim with nothing laid.
    /// </summary>
    [Fact]
    public void AViaductTheRuleRefusesRefusesItsRoadAsADeckWithNoKit()
    {
        var world = new DvergrViaductTests.MistValley();
        _scope.WithWorld(world);
        RoadCrossing c = DvergrViaduct.ValleyCrossing(new Vector2(-24f, 0f), new Vector2(24f, 0f), world, out string why)!;
        Assert.True(c != null, why);
        c!.Kit = BridgeStructures.Kit.DvergrViaduct;
        var plans = new List<RoadSpatialGrid.PlannedPath>();
        Assert.False(BridgeStructures.BuiltInNoKit(c, world));
        Assert.Null(RoadNetworkGenerator.BuiltRoadRefusal(plans, new List<RoadCrossing> { c }, out _));

        world.Depth = 50f;
        world.Face = 4f;
        Assert.Empty(BridgeStructures.Solve(c, world, world.GetSeed()));
        string? refused = RoadNetworkGenerator.BuiltRoadRefusal(plans, new List<RoadCrossing> { c }, out Vector2 at);
        Assert.True(refused != null && refused.StartsWith(RoadNetworkGenerator.DeckWithNoKit), refused ?? "not refused");
        Assert.Equal(c.Center, at);
        Assert.True(BridgeStructures.BuiltInNoKit(c, world));
    }

    /// <summary>The stone kit's reference crossing (StoneBridgeTests): 20 m along +x over a bed 28 m down at its middle, the
    /// walking surface at 34.83 m.</summary>
    private (RoadCrossing c, StoneBridgeTests.ReferenceWorld w) StoneReference()
    {
        var w = new StoneBridgeTests.ReferenceWorld();
        _scope.WithWorld(w);
        var c = RoadCrossing.Between(new Vector2(-10f, 0f), new Vector2(10f, 0f), 28f, new Vector2(0f, 0f), 0f, CrossingKind.Bridge);
        c.WaterLevel = 34.83f - BridgeLayout.DeckFreeboard;
        return (c, w);
    }

    /// <summary>The seeds, of the first <paramref name="seeds"/>, whose draw among the kits that fit is the stone kit
    /// (BridgeLayout.PickKit, the one draw).</summary>
    private static List<int> StoneDraws(RoadCrossing c, StoneBridgeTests.ReferenceWorld w, int seeds)
    {
        var drawn = new List<int>();
        for (int seed = 1; seed <= seeds; seed++)
        {
            w.Seed = seed;
            if (BridgeLayout.PickKit(c, seed, BridgeStructures.KitsThatFit(c, w)) == BridgeStructures.Kit.Stone) drawn.Add(seed);
        }
        return drawn;
    }

    /// <summary>
    /// Ground not known is never a verdict (docs/OWNERS.md, ground readers): a stone bridge whose inner piers stand on a location's
    /// ground not read yet (a location placed after its roads under the middle of the crossing) is not read by the support rule,
    /// so the stone kit's estimate stands and the seeded draw keeps it, on every path that asks the kit (the kit, the solve's
    /// pieces, the kit saved at the first build), and its record says why it was not read. Once that ground is read, the
    /// same crossing is read by the rule and its record said again.
    /// </summary>
    [Fact]
    public void AStoneBridgeOnGroundNotKnownKeepsItsDrawnKitAndSaysItWasNotRead()
    {
        var (c, w) = StoneReference();
        List<int> drawn = StoneDraws(c, w, 30);
        Assert.NotEmpty(drawn);
        RoadLocationProtection.Set(new[] { new RoadLocationProtection.Footprint(new Vector2(0f, 0f), 6f) });
        Assert.True(float.IsNaN(StructureGround.At(w)(new Vector2(3f, 0f))));
        Assert.True(BridgeLayout.StoneFits(c, w));
        Assert.Equal(drawn, StoneDraws(c, w, 30));
        var lines = new List<string>();
        BepInEx.Logging.ManualLogSource.Captured = lines;
        try
        {
            w.Seed = drawn[0];
            BridgeStructures.Kit? kit = BridgeStructures.KitFor(c, w);
            Assert.True(kit == BridgeStructures.Kit.Stone, $"{kit}: {string.Join(" | ", lines)}");
            Assert.All(BridgeStructures.Solve(c, w, w.Seed), p => Assert.Equal(BridgeStructures.Kit.Stone, p.Kit));
            string record = Assert.Single(lines, l => l.Contains("[SUPPORT] Stone"));
            Assert.Contains("estimate fits", record);
            Assert.Contains("full not read (ground not known under its piece at", record);
            Assert.Contains("response built", record);

            RoadLocationProtection.Reset();
            Assert.False(float.IsNaN(StructureGround.At(w)(new Vector2(3f, 0f))));
            Assert.Equal(BridgeStructures.Kit.Stone, BridgeStructures.KitFor(c, w));
            Assert.Contains(lines, l => l.Contains("[SUPPORT] Stone") && l.Contains("full holds 0 of"));

            // The kit is saved once the bridge is built on ground known: the drawn stone kit.
            Assert.NotEmpty(BridgeStructures.Solve(c, w, w.Seed));
            Assert.True(BridgeStructures.ChooseKit(c, w, new[] { c }));
            Assert.Equal(BridgeStructures.Kit.Stone, c.Kit);
        }
        finally { BepInEx.Logging.ManualLogSource.Captured = null; }
    }

    /// <summary>The reference crossing's bed under its deck cut down to <paramref name="floor"/> by a road along it (a road cut
    /// under its piers, laid after the bridge's kit was saved): road points levelled at that height down the crossing's middle,
    /// which the terrain writer and every finished-ground reader take the ground to.</summary>
    private static void CutUnderThePiers(float floor)
    {
        var points = new List<Vector2>();
        var heights = new List<float>();
        for (float x = -6f; x <= 6f + 1e-3f; x += 1f) { points.Add(new Vector2(x, 0f)); heights.Add(floor); }
        RoadSpatialGrid.CommitLevelled(points, heights, 8f);
    }

    /// <summary>
    /// A saved stone bridge whose ground is lowered after its kit was saved (a road cut under its piers) and that no record
    /// holds (none saved yet, or forgotten for a respawn) is solved again on the ground as it stands: its piers' courses
    /// stop at the kit's limit short of the cut, and the support rule reads it as at kit choice. The rule breaks it, so it is
    /// built in another kit that fits, read the same way, on every path that asks the kit (the kit, the solve's pieces, the
    /// record saved at its first build), and its record says it was refused in stone. Before, the saved kit was laid as it
    /// was, read by nothing: the stone bridge with its piers stopped short.
    /// </summary>
    [Fact]
    public void ASavedStoneBridgeOnGroundLoweredSinceIsReadAgainAndBuiltInAnotherKit()
    {
        var (c, w) = StoneReference();
        w.Seed = StoneDraws(c, w, 30)[0];
        Assert.True(BridgeStructures.ChooseKit(c, w, new[] { c }));
        Assert.Equal(BridgeStructures.Kit.Stone, c.Kit);

        CutUnderThePiers(24f);
        Assert.InRange(StructureGround.At(w)(new Vector2(0f, 0f)), 23.9f, 24.1f);
        Assert.False(BridgeLayout.StoneFits(c, w));
        BridgeStructures.SupportCheck stone = BridgeStructures.CheckSupport(c, w, BridgeStructures.Kit.Stone);
        Assert.True(stone.Breaks, BridgeStructures.SupportLine(c, stone, "read"));
        var lines = new List<string>();
        BepInEx.Logging.ManualLogSource.Captured = lines;
        try
        {
            BridgeStructures.Kit? kit = BridgeStructures.KitFor(c, w);
            Assert.True(kit is BridgeStructures.Kit k && k != BridgeStructures.Kit.Stone, $"{kit}: {string.Join(" | ", lines)}");
            Assert.False(BridgeStructures.CheckSupport(c, w, kit!.Value).Breaks);
            string record = Assert.Single(lines, l => l.Contains("[SUPPORT] Stone"));
            Assert.Contains("estimate refuses", record);
            Assert.Contains("full breaks", record);
            Assert.Contains($"response refused, built in {kit}{BridgeStructures.SavedReadAgain}", record);
            List<StructurePiece> pieces = BridgeStructures.Build(c, w, w.Seed);
            Assert.NotEmpty(pieces);
            Assert.All(pieces, p => Assert.Equal(kit, p.Kit));
            // The kit it was built in saved as its pieces go out (BridgeStructures.ChooseKits), and its record laid as saved
            // from now on, read again by nothing.
            Assert.Equal(kit, BuiltBridges.Of(c)!.Kit);
            Assert.True(BridgeStructures.ChooseKits(pieces, w, new[] { c }));
            Assert.Equal(kit, c.Kit);
            Assert.Equal(kit, BridgeStructures.KitFor(c, w));
        }
        finally { BepInEx.Logging.ManualLogSource.Captured = null; BuiltBridges.Reset(); }
    }

    /// <summary>
    /// The known-ground break (the switch-kit remedy driven by a real break, not a fixture on ground not known): the stone
    /// kit's reference crossing, its stone kit saved on the ground it was chosen on (where the rule holds it), then its bed
    /// cut down under its piers by a road (CutUnderThePiers), every vertex under its pieces known. The rule breaks the stone
    /// bridge its saved kit lays (piers stopped at the kit's limit, short of the cut) on every floor, and the builder's one
    /// response (BridgeStructures.Respond) takes its remedies in turn: at 24 m the braced kit drawn next holds, and the
    /// bridge is built in it; at 22 m the braced kit breaks too and the railed one holds; at 20 m every kit that fits breaks
    /// and none is built: no kit, nothing laid, its record says so.
    /// </summary>
    [Theory]
    [InlineData(24f, BridgeStructures.Kit.WoodBraced)]
    [InlineData(22f, BridgeStructures.Kit.WoodRail)]
    [InlineData(20f, null)]
    public void AStoneBridgeBrokenOnKnownGroundTakesTheRemediesInTurn(float floor, BridgeStructures.Kit? expected)
    {
        var (c, w) = StoneReference();
        w.Seed = StoneDraws(c, w, 30)[0];
        Assert.True(BridgeStructures.ChooseKit(c, w, new[] { c }));
        CutUnderThePiers(floor);
        BridgeStructures.SupportCheck stone = BridgeStructures.CheckSupport(c, w, BridgeStructures.Kit.Stone);
        Assert.True(stone.FullRead && !stone.GroundNotKnown && stone.Breaks, BridgeStructures.SupportLine(c, stone, "read"));
        foreach (BridgeStructures.Kit wooden in new[] { BridgeStructures.Kit.WoodBraced, BridgeStructures.Kit.WoodRail })
        {
            BridgeStructures.SupportCheck read = BridgeStructures.CheckSupport(c, w, wooden);
            Assert.True(read.FullRead, BridgeStructures.SupportLine(c, read, "read"));
            // Each kit the response passes over breaks; the one it builds in holds.
            if (wooden == expected) Assert.True(read.Holds, BridgeStructures.SupportLine(c, read, "read"));
            else if (expected == null || wooden == BridgeStructures.Kit.WoodBraced) Assert.True(read.Breaks, BridgeStructures.SupportLine(c, read, "read"));
        }
        var lines = new List<string>();
        BepInEx.Logging.ManualLogSource.Captured = lines;
        try
        {
            Assert.Equal(expected, BridgeStructures.KitFor(c, w));
            string record = Assert.Single(lines, l => l.Contains("[SUPPORT] Stone"));
            Assert.Contains(expected is BridgeStructures.Kit k ? $"response refused, built in {k}" : "response refused, no kit", record);
            List<StructurePiece> pieces = BridgeStructures.Solve(c, w, w.Seed);
            if (expected == null) Assert.Empty(pieces);
            else Assert.All(pieces, p => Assert.Equal(expected, p.Kit));
        }
        finally { BepInEx.Logging.ManualLogSource.Captured = null; }
    }

    /// <summary>
    /// A saved bridge laid as saved (decision 6ah): where its record holds the ground it was solved on, its saved kit is the
    /// kit it is laid in and its pieces are the saved ones, whatever a road cut since would make of the ground; where no
    /// record holds it and the ground is the one its kit was chosen on, the rule reads it again and holds it, so it keeps
    /// its kit.
    /// </summary>
    [Fact]
    public void ASavedBridgeOnTheGroundItWasSolvedOnKeepsItsKit()
    {
        var (c, w) = StoneReference();
        w.Seed = StoneDraws(c, w, 30)[0];
        Assert.True(BridgeStructures.ChooseKit(c, w, new[] { c }));
        Assert.Equal(BridgeStructures.Kit.Stone, BridgeStructures.KitFor(c, w));
        try
        {
            List<StructurePiece> pieces = BridgeStructures.Build(c, w, w.Seed);
            Assert.All(pieces, p => Assert.Equal(BridgeStructures.Kit.Stone, p.Kit));
            Assert.Equal(BridgeStructures.Kit.Stone, BuiltBridges.Of(c)!.Kit);
            // A road cut under it since: the ground its record holds is the ground it was solved on, so it is laid as saved.
            CutUnderThePiers(24f);
            Assert.Equal(BridgeStructures.Kit.Stone, BridgeStructures.KitFor(c, w));
            Assert.Equal(pieces.ConvertAll(p => p.Position), BridgeStructures.PiecesOf(c, w, w.Seed).ConvertAll(p => p.Position));
        }
        finally { BuiltBridges.Reset(); }
    }

    /// <summary>
    /// The calibration record (decision 6p): a selected candidate's cheap estimate and the support rule's reading of the same
    /// pieces, said once to the log for the offline maps tool. A stone draw on the reference crossing: its estimate fits with
    /// none to spare in its tallest footing or pier, the rule holds every piece; a wooden draw: its measured ceiling's margin
    /// beside the rule's reading of its pieces.
    /// </summary>
    [Fact]
    public void ASelectedCandidatesEstimateAndFullReadingAreRecorded()
    {
        var (c, w) = StoneReference();
        int stone = StoneDraws(c, w, 30)[0];
        var lines = new List<string>();
        BepInEx.Logging.ManualLogSource.Captured = lines;
        try
        {
            w.Seed = stone;
            Assert.Equal(BridgeStructures.Kit.Stone, BridgeStructures.KitFor(c, w));
            string record = Assert.Single(lines, l => l.Contains("[SUPPORT] Stone"));
            Assert.Contains("estimate fits", record);
            Assert.Contains("full holds 0 of", record);
            Assert.Contains("response built", record);

            var (wooden, ww) = StoneReference();
            for (ww.Seed = 1; BridgeLayout.PickKit(wooden, ww.Seed, BridgeStructures.KitsThatFit(wooden, ww)) == BridgeStructures.Kit.Stone; ww.Seed++)
                Assert.True(ww.Seed < 200, "no seed in 200 draws a wooden kit");
            BridgeStructures.Kit? kit = BridgeStructures.KitFor(wooden, ww);
            Assert.Contains(lines, l => l.Contains($"[SUPPORT] {kit}") && l.Contains("m under the measured ceiling") && l.Contains("full holds 0 of"));
        }
        finally { BepInEx.Logging.ManualLogSource.Captured = null; }
    }

    /// <summary>
    /// A bounded replay of candidates the stone kit's estimate refuses (decision 6p's pessimistic errors): the reference
    /// crossing over a bed 3, 4 and 6 m lower (ten, eleven and thirteen pier courses, past the estimate's nine), each laid with
    /// no course stopped at the estimate (BridgeLayout.SolveStoneCandidate) and read by the support rule. Recorded as found:
    /// the rule holds every piece of all three (the weakest the railing's top course over an arch, then an arch, at 1.02 of
    /// its least at 6 m), so the estimate is pessimistic here. Evidence for calibrating it, not a change to it.
    /// </summary>
    [Fact]
    public void RefusedStoneCandidatesReplayedThroughTheRuleAreRecordedAsTheyRead()
    {
        foreach (float drop in new[] { 3f, 4f, 6f })
        {
            var (c, w) = StoneReference();
            w.Drop = drop;
            Assert.False(BridgeLayout.StoneFits(c, w));
            int margin = BridgeLayout.StoneFitMargin(c, w)!.Value;
            List<StructurePiece> candidate = BridgeLayout.SolveStoneCandidate(c, w, 1, ruin: false);
            BridgeStructures.SupportCheck read = BridgeStructures.Read(BridgeStructures.Kit.Stone, false, margin, "course(s) or pillar(s) to spare",
                ViaductSupport.Of(c, w, candidate));
            Assert.True(read.Holds, $"drop {drop}: {BridgeStructures.SupportLine(c, read, "replayed")}");
            Assert.Contains("estimate pessimistic", BridgeStructures.SupportLine(c, read, "replayed"));
        }
    }
}
